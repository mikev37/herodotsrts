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
        new HaulJob
        {
            Dt = SystemAPI.Time.DeltaTime,
            Registry = SystemAPI.GetSingleton<StableIdRegistry>().Map,
            XformLk = SystemAPI.GetComponentLookup<LocalTransform>(true),
            Ecb = ecb,
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct HaulJob : IJobEntity
    {
        public float Dt;
        [ReadOnly] public NativeParallelHashMap<int, Entity> Registry;
        [ReadOnly] public ComponentLookup<LocalTransform> XformLk;
        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute([ChunkIndexInQuery] int sortKey, Entity self, in StableId sid,
                             in LocalTransform xf, ref HaulTask task, ref MoveTarget move, in ResourceBank cargo)
        {
            float2 pos = new float2(xf.Position.x, xf.Position.z);
            switch (task.Phase)
            {
                case HaulPhase.ToSource:
                    if (!Resolve(task.SourceStableId, out _, out float2 sp)) { Vanish(sortKey, self); break; }   // colony gone
                    SetMove(ref move, sp);
                    if (math.distance(pos, sp) <= ArriveDist) { task.Phase = HaulPhase.Loading; task.Timer = LoadTime; }
                    break;

                case HaulPhase.Loading:
                    task.Timer -= Dt;
                    if (Registry.TryGetValue(task.SourceStableId, out Entity colony))
                    {
                        var room = new ResourceAmount();
                        for (int t = 0; t < 3; t++) { int r = cargo.Capacity[t] - cargo.Amounts[t]; if (r > 0) room[t] = r; }
                        if (room.Any) Ecb.AppendToBuffer(sortKey, colony, new BankRequest
                            { Amount = room, RequesterStableId = sid.Value, Class = (byte)SpendClass.Transfer, CastTick = 0 });
                    }
                    if (task.Timer <= 0f) task.Phase = HaulPhase.ToSink;
                    break;

                case HaulPhase.ToSink:
                    if (!Resolve(task.SinkStableId, out _, out float2 kp)) { Vanish(sortKey, self); break; }      // capital gone
                    SetMove(ref move, kp);
                    if (math.distance(pos, kp) <= ArriveDist) { task.Phase = HaulPhase.Unloading; task.Timer = UnloadTime; }
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
            }
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
        private static void SetMove(ref MoveTarget m, float2 to) { m.Value = to; m.HasTarget = true; m.AttackMove = false; }
    }
}
