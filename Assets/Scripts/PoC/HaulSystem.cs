using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// HAUL SYSTEM — the ox-cart round trip. A hauler (HaulTask) loads its colony's
// holdings, delivers them to the pre-assigned capital, then DIES (Die anim via
// the normal death pipeline) on delivery. Source (colony) and Sink (capital) are
// set at spawn by ProductionSystem.
//
// No bank Amounts are written here: loading pulls from the colony (request paid
// to self); unloading PUSHES typed deposits straight to the capital (so the
// transfer doesn't depend on the dying hauler's own bank surviving), then adds
// Dead. Resource banks are per-entity and independent of the player bank — a
// colony bank, a hauler cargo bank, and the capital bank are all just entities.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(CommandApplySystem))]
[UpdateBefore(typeof(BehaviorSystem))]
public partial struct HaulSystem : ISystem
{
    private const float ArriveDist = 2.0f, LoadTime = 0.5f, UnloadTime = 0.3f, DeliverAnim = 0.75f;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<StableIdRegistry>();
        state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                           .CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter();

        // Capitals (intake depots) so a cart with a dead/unset sink can re-target
        // the nearest own capital instead of vanishing with a full hold.
        var capPlayer = new NativeList<int>(8, Allocator.TempJob);
        var capSid    = new NativeList<int>(8, Allocator.TempJob);
        var capPos    = new NativeList<float2>(8, Allocator.TempJob);
        foreach (var (pl, sidRef, xf) in
                 SystemAPI.Query<RefRO<Player>, RefRO<StableId>, RefRO<LocalTransform>>().WithAll<IntakeTag>())
        { capPlayer.Add(pl.ValueRO.Value); capSid.Add(sidRef.ValueRO.Value); capPos.Add(new float2(xf.ValueRO.Position.x, xf.ValueRO.Position.z)); }

        new HaulJob
        {
            Dt = SystemAPI.Time.DeltaTime,
            Registry = SystemAPI.GetSingleton<StableIdRegistry>().Map,
            XformLk = SystemAPI.GetComponentLookup<LocalTransform>(true),
            ObstacleLk = SystemAPI.GetComponentLookup<Obstacle>(true),
            BankLk = SystemAPI.GetComponentLookup<ResourceBank>(true),
            CapPlayer = capPlayer, CapSid = capSid, CapPos = capPos,
            Ecb = ecb,
        }.ScheduleParallel();

        state.Dependency = capPlayer.Dispose(state.Dependency);
        state.Dependency = capSid.Dispose(state.Dependency);
        state.Dependency = capPos.Dispose(state.Dependency);
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct HaulJob : IJobEntity
    {
        public float Dt;
        [ReadOnly] public NativeParallelHashMap<int, Entity> Registry;
        [ReadOnly] public ComponentLookup<LocalTransform> XformLk;
        [ReadOnly] public ComponentLookup<Obstacle> ObstacleLk;
        [ReadOnly] public ComponentLookup<ResourceBank> BankLk;
        [ReadOnly] public NativeList<int> CapPlayer;
        [ReadOnly] public NativeList<int> CapSid;
        [ReadOnly] public NativeList<float2> CapPos;
        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute([ChunkIndexInQuery] int sortKey, Entity self, in StableId sid, in Player player,
                             in LocalTransform xf, ref HaulTask task, ref MoveTarget move, in ResourceBank cargo)
        {
            float2 pos = new float2(xf.Position.x, xf.Position.z);
            switch (task.Phase)
            {
                case HaulPhase.ToSource:
                    if (!Resolve(task.SourceStableId, out Entity src, out float2 sp))
                    {
                        // Colony gone. Deliver whatever is already loaded; only an
                        // EMPTY orphaned cart vanishes.
                        if (cargo.Amounts.Any) { task.Phase = HaulPhase.ToSink; break; }
                        Vanish(sortKey, self); break;
                    }
                    SetMove(ref move, sp);
                    // Arrive at the building's footprint EDGE — its center is inside
                    // an impassable footprint, so a center-distance check could never
                    // pass and carts jammed forever "en route".
                    if (EdgeDist(pos, src, sp) <= ArriveDist) { task.Phase = HaulPhase.Loading; task.Timer = LoadTime; }
                    break;

                case HaulPhase.Loading:
                    task.Timer -= Dt;
                    if (Registry.TryGetValue(task.SourceStableId, out Entity colony) && BankLk.HasComponent(colony))
                    {
                        // Ask ONLY for what the colony actually holds, per type. The
                        // bank fulfils a request as a single proportional fraction
                        // across ALL types (worst ratio wins), so requesting 200 gold
                        // from a colony holding zero gold scaled the WHOLE transfer
                        // to zero — the cart left empty from a full colony.
                        var held = BankLk[colony].Amounts;
                        var room = new ResourceAmount();
                        for (int t = 0; t < ResourceAmount.Count; t++)
                        {
                            int r = math.min(cargo.Capacity[t] - cargo.Amounts[t], held[t]);
                            if (r > 0) room[t] = r;
                        }
                        if (room.Any) Ecb.AppendToBuffer(sortKey, colony, new BankRequest
                            { Amount = room, RequesterStableId = sid.Value, Class = (byte)SpendClass.Transfer, CastTick = 0 });
                    }
                    if (task.Timer <= 0f) task.Phase = HaulPhase.ToSink;
                    break;

                case HaulPhase.ToSink:
                    if (!Resolve(task.SinkStableId, out Entity cap, out float2 kp))
                    {
                        // Sink dead or never set (e.g. a player order re-routed the
                        // cart): re-target the NEAREST own capital so the cargo still
                        // arrives. Vanish only when the player has no capital at all.
                        task.SinkStableId = NearestCapital(player.Value, pos);
                        if (!Resolve(task.SinkStableId, out cap, out kp)) { Vanish(sortKey, self); break; }
                    }
                    SetMove(ref move, kp);
                    if (EdgeDist(pos, cap, kp) <= ArriveDist) { task.Phase = HaulPhase.Unloading; task.Timer = UnloadTime; }
                    break;

                case HaulPhase.Unloading:
                    task.Timer -= Dt;
                    if (task.Timer > 0f) break;
                    if (Registry.TryGetValue(task.SinkStableId, out Entity capital) && cargo.Amounts.Any)
                        Ecb.AppendToBuffer(sortKey, capital, new BankDeposit { Amount = cargo.Amounts });
                    task.Phase = HaulPhase.Done;
                    Vanish(sortKey, self);   // success anim + despawn (NOT death)
                    break;

                case HaulPhase.Done:
                    break;

                case HaulPhase.Manual:
                    // Player has taken the wheel (a direct move order). The cart
                    // keeps its cargo and route ids but drives nothing itself —
                    // right-clicking a depot (Deliver) or the sink-fallback puts it
                    // back on the job.
                    break;
            }
        }

        private int NearestCapital(int player, float2 from)
        {
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < CapSid.Length; i++)
            {
                if (CapPlayer[i] != player) continue;
                float d = math.distancesq(from, CapPos[i]);
                if (d < bestD || (d == bestD && CapSid[i] < best)) { bestD = d; best = CapSid[i]; }
            }
            return best;
        }

        // Distance from a point to the target building's footprint edge (falls back
        // to center distance if the target somehow has no footprint).
        private float EdgeDist(float2 pos, Entity target, float2 center)
        {
            if (ObstacleLk.HasComponent(target))
            {
                float2 half = (float2)ObstacleLk[target].Extents * (NavGrid.CellSize * 0.5f);
                return CombatMath.DistanceToFootprint(pos, center, half);
            }
            return math.distance(pos, center);
        }

        private bool Resolve(int s, out Entity e, out float2 p)
        {
            e = Entity.Null; p = default;
            if (s < 0 || !Registry.TryGetValue(s, out e) || !XformLk.HasComponent(e)) return false;
            var v = XformLk[e].Position; p = new float2(v.x, v.z); return true;
        }
        private void Vanish(int sortKey, Entity e)
        {
            Ecb.SetComponent(sortKey, e, new UnitAnim { State = AnimState.Deliver });   // success/vanish anim
            Ecb.AddComponent(sortKey, e, new Despawn { Seconds = DeliverAnim });          // NOT Dead
        }
        // SOFT slotless move: BehaviorSystem's direct-drive tier moves a lone
        // (FormationId 0) soft mover straight to the point. The old HARD move had
        // no drive tier without a formation slot — the cart never moved and sat in
        // the colony footprint until steering spat it out.
        private static void SetMove(ref MoveTarget m, float2 to) { m.Value = to; m.HasTarget = true; m.AttackMove = true; m.FormationId = 0; }
    }
}
