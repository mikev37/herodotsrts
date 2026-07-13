using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// HARVEST (harvester side) = MOVEMENT INTENT ONLY. It picks where the harvester
// wants to be this phase and writes a SOFT MoveTarget (AttackMove = true) so the
// destination flows through BehaviorSystem's ladder exactly like an attack-move
// order: survival/engagement (tiers 4/5) still fire, so a harvester under attack
// defends/flees and resumes afterward. It never checks arrival, never moves a
// bank, never flips its own phase — all of that is RECEIVER-SIDE in
// HarvestTransferSystem (the node/depot owns it, using its own ContactList, which
// is footprint/extent-tolerant). A player Move/Attack order clears HarvestTask
// (CommandApplySystem), so there's no MoveTarget contention.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(CommandApplySystem))]
[UpdateBefore(typeof(BehaviorSystem))]
public partial struct HarvestSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<StableIdRegistry>();
        state.RequireForUpdate<DepotRegistry>();
        state.RequireForUpdate<NodeRegistry>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new MoveJob
        {
            Registry = SystemAPI.GetSingleton<StableIdRegistry>().Map,
            Depots = SystemAPI.GetSingleton<DepotRegistry>().Map,
            Nodes = SystemAPI.GetSingleton<NodeRegistry>().Map,
            XformLk = SystemAPI.GetComponentLookup<LocalTransform>(true),
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct MoveJob : IJobEntity
    {
        [ReadOnly] public NativeParallelHashMap<int, Entity> Registry;
        [ReadOnly] public NativeParallelMultiHashMap<int, DepotInfo> Depots;
        [ReadOnly] public NativeParallelMultiHashMap<int, NodeInfo> Nodes;
        [ReadOnly] public ComponentLookup<LocalTransform> XformLk;

        private void Execute(in StableId self, in Player player, in LocalTransform xf,
                             ref HarvestTask task, ref MoveTarget move)
        {
            float2 pos = new float2(xf.Position.x, xf.Position.z);
            switch (task.Phase)
            {
                case HarvestPhase.Idle:
                    if (task.NodeStableId >= 0) task.Phase = HarvestPhase.ToNode;
                    return;

                case HarvestPhase.ToNode:
                case HarvestPhase.Gathering:                 // building flips us off Gathering; keep aiming at the node
                    if (!Resolve(task.NodeStableId, out float2 np))
                    {
                        if (!Reacquire(task.Carrying, pos, task.ReacquireRange, out int nid)) { task.Phase = HarvestPhase.Idle; return; }
                        task.NodeStableId = nid; Resolve(nid, out np);
                    }
                    SoftMove(ref move, np);
                    return;

                case HarvestPhase.ToDepot:
                case HarvestPhase.Depositing:                // building flips us off Depositing
                    if (task.DepotStableId < 0 || !Resolve(task.DepotStableId, out _))
                        task.DepotStableId = NearestDepot(player.Value, pos);
                    if (Resolve(task.DepotStableId, out float2 dp)) SoftMove(ref move, dp);
                    return;
            }
        }

        private bool Reacquire(ResourceType type, float2 from, float range, out int best)
        {
            best = -1; float bestD = float.MaxValue;
            float maxSq = range > 0f ? range * range : float.MaxValue;   // 0 = unlimited
            if (Nodes.TryGetFirstValue((int)type, out NodeInfo ni, out var it))
                do { float d = math.distancesq(from, ni.Pos);
                     if (d > maxSq) continue;
                     if (d < bestD || (d == bestD && (best < 0 || ni.StableId < best))) { bestD = d; best = ni.StableId; }
                } while (Nodes.TryGetNextValue(out ni, ref it));
            return best >= 0;
        }

        private int NearestDepot(int player, float2 from)
        {
            int best = -1; float bestD = float.MaxValue;
            if (Depots.TryGetFirstValue(player, out DepotInfo d, out var it))
                do { float dist = math.distancesq(from, d.Pos);
                     if (dist < bestD || (dist == bestD && (best < 0 || d.StableId < best))) { bestD = dist; best = d.StableId; }
                } while (Depots.TryGetNextValue(out d, ref it));
            return best;
        }

        private bool Resolve(int sid, out float2 p)
        {
            p = default;
            if (sid < 0 || !Registry.TryGetValue(sid, out Entity e) || !XformLk.HasComponent(e)) return false;
            var v = XformLk[e].Position; p = new float2(v.x, v.z); return true;
        }

        // SOFT move: BehaviorSystem treats AttackMove as the tier-6 soft move, so
        // survival/engagement can override and the unit still defends itself.
        private static void SoftMove(ref MoveTarget m, float2 to) { m.Value = to; m.HasTarget = true; m.AttackMove = true; }
    }
}
