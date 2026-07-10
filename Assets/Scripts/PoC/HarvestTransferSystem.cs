using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Entities;
using Unity.Transforms;

// ===========================================================================
// HARVEST TRANSFER = RECEIVER SIDE. The node/depot owns arrival, the resource
// move, and the harvester's phase flips — using its OWN ContactList (nearby
// non-building units), which is footprint/extent-tolerant, so a harvester at the
// edge of a large depot counts as "arrived" without ever reaching its center.
//
// Transfers still go through the one bank writer: a building APPENDS the request
// on the harvester's behalf (node -> appends to itself paid to the harvester;
// depot -> appends to the harvester's cargo paid to the depot), and the bank job
// performs it next tick. Only the building a harvester is TARGETING acts on it,
// so each harvester has a single cross-entity writer per tick (deterministic).
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(InformationGatherSystem))]
[UpdateBefore(typeof(ResourceBankSystem))]
public partial struct HarvestTransferSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
        => state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                           .CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter();
        var taskLk = SystemAPI.GetComponentLookup<HarvestTask>(false);
        var bankLk = SystemAPI.GetComponentLookup<ResourceBank>(true);
        var sidLk  = SystemAPI.GetComponentLookup<StableId>(true);

        new NodeJob  { TaskLk = taskLk, BankLk = bankLk, SidLk = sidLk, Ecb = ecb, Dt = SystemAPI.Time.DeltaTime }.ScheduleParallel();
        new DepotJob { TaskLk = taskLk, BankLk = bankLk, SidLk = sidLk, Ecb = ecb, Dt = SystemAPI.Time.DeltaTime }.ScheduleParallel();
    }

    // node grants to in-range harvesters that target it; flips them to ToDepot when full/depleted.
    [BurstCompile]
    [WithAll(typeof(NodeTag))]
    private partial struct NodeJob : IJobEntity
    {
        [NativeDisableParallelForRestriction] public ComponentLookup<HarvestTask> TaskLk;
        [ReadOnly] public ComponentLookup<ResourceBank> BankLk;
        [ReadOnly] public ComponentLookup<StableId> SidLk;
        public EntityCommandBuffer.ParallelWriter Ecb;
        public float Dt;   // fixed lockstep tick length (constant, deterministic)

        private void Execute([ChunkIndexInQuery] int sortKey, Entity self, in StableId nodeSid,
                             in NodeTag node, in ResourceBank bank, in DynamicBuffer<UnitInfo> contacts)
        {
            int avail = bank.Amounts[(int)node.Yield];
            for (int i = 0; i < contacts.Length; i++)
            {
                var h = contacts[i].Entity;
                if (!TaskLk.HasComponent(h) || !BankLk.HasComponent(h)) continue;
                var t = TaskLk[h];
                if (t.NodeStableId != nodeSid.Value) continue;            // not targeting me
                if (t.Phase != HarvestPhase.ToNode && t.Phase != HarvestPhase.Gathering) continue;

                // carrying a DIFFERENT type already? deliver it before taking a new one (single-type carts)
                var cargo = BankLk[h].Amounts;
                bool carryingOther = false;
                for (int r = 0; r < ResourceAmount.Count; r++) if (r != (int)node.Yield && cargo[r] > 0) carryingOther = true;
                if (carryingOther) { t.Phase = HarvestPhase.ToDepot; t.DepotStableId = -1; TaskLk[h] = t; continue; }

                t.Phase = HarvestPhase.Gathering; t.Carrying = node.Yield;     // arrived (in my contacts)
                int room = BankLk[h].Capacity[(int)node.Yield] - cargo[(int)node.Yield];

                if (room <= 0)                                                  // full -> deliver
                {
                    t.Phase = HarvestPhase.ToDepot; t.DepotStableId = -1; t.Accrued = 0f;
                }
                else if (avail <= 0)                                            // node drained
                {
                    // Carrying something: deliver it. Empty-handed: clear the node
                    // and stay in ToNode — HarvestSystem's Reacquire then targets
                    // the NEAREST node of the same type (or goes Idle if none),
                    // instead of hovering at the husk forever.
                    if (cargo[(int)node.Yield] > 0) { t.Phase = HarvestPhase.ToDepot; t.DepotStableId = -1; }
                    else { t.Phase = HarvestPhase.ToNode; t.NodeStableId = -1; }
                    t.Accrued = 0f;
                }
                else
                {
                    // Gather at Rate resources PER SECOND: accrue fractionally each
                    // tick, transfer whole units when the accumulator crosses 1.
                    // (The old code granted min(room,avail,Rate) EVERY TICK — at 30
                    // ticks/s a "rate 50" peasant drained a 50-wood tree instantly.)
                    t.Accrued += t.Rate * Dt;
                    int want = (int)t.Accrued;
                    if (want > 0)
                    {
                        int grant = math.min(math.min(room, avail), want);
                        t.Accrued -= grant;
                        var ask = new ResourceAmount(); ask[node.Yield] = grant;
                        Ecb.AppendToBuffer(sortKey, self, new BankRequest
                        { Amount = ask, RequesterStableId = SidLk[h].Value, Class = (byte)SpendClass.Transfer, CastTick = 0 });
                    }
                }
                TaskLk[h] = t;
            }
        }
    }

    // depot pulls cargo from in-range same-player harvesters that target it; flips them to ToNode when empty.
    [BurstCompile]
    [WithAll(typeof(DepotTag))]
    private partial struct DepotJob : IJobEntity
    {
        [NativeDisableParallelForRestriction] public ComponentLookup<HarvestTask> TaskLk;
        [ReadOnly] public ComponentLookup<ResourceBank> BankLk;
        [ReadOnly] public ComponentLookup<StableId> SidLk;
        public EntityCommandBuffer.ParallelWriter Ecb;
        public float Dt;

        private void Execute([ChunkIndexInQuery] int sortKey, Entity self, in StableId depotSid,
                             in Player player, in LocalTransform xf, in Obstacle obstacle,
                             in DynamicBuffer<UnitInfo> contacts)
        {
            float2 myPos = new float2(xf.Position.x, xf.Position.z);
            float2 half  = (float2)obstacle.Extents * (NavGrid.CellSize * 0.5f);

            for (int i = 0; i < contacts.Length; i++)
            {
                if (contacts[i].Player != player.Value) continue;          // own harvesters only
                var h = contacts[i].Entity;
                if (!TaskLk.HasComponent(h) || !BankLk.HasComponent(h)) continue;
                var t = TaskLk[h];
                if (t.DepotStableId != depotSid.Value) continue;
                if (t.Phase != HarvestPhase.ToDepot && t.Phase != HarvestPhase.Depositing) continue;

                // Deposit only AT the walls: the ContactList reaches farther (it
                // also serves perception), so gate on true footprint-edge distance.
                if (CombatMath.DistanceToFootprint(contacts[i].Position, myPos, half) > t.DropRange) continue;

                var cargo = BankLk[h].Amounts;
                if (!cargo.Any) { t.Phase = HarvestPhase.ToNode; t.Accrued = 0f; TaskLk[h] = t; continue; }

                // Unload takes TIME: DepositRate per second when authored,
                // otherwise symmetric with the gather rate.
                t.Phase = HarvestPhase.Depositing;
                t.Accrued += (t.DepositRate > 0f ? t.DepositRate : t.Rate) * Dt;
                int want = (int)t.Accrued;
                if (want > 0)
                {
                    // first non-empty cargo type this pass (single-type carts in
                    // practice; mixed cargo drains type by type)
                    int type = -1;
                    for (int r = 0; r < ResourceAmount.Count; r++) if (cargo[r] > 0) { type = r; break; }
                    int give = math.min(cargo[type], want);
                    t.Accrued -= give;
                    var pay = new ResourceAmount(); pay[(ResourceType)type] = give;
                    Ecb.AppendToBuffer(sortKey, h, new BankRequest
                    { Amount = pay, RequesterStableId = depotSid.Value, Class = (byte)SpendClass.Transfer, CastTick = 0 });
                }
                TaskLk[h] = t;
            }
        }
    }
}
