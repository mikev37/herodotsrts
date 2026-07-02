using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Entities;

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

        new NodeJob  { TaskLk = taskLk, BankLk = bankLk, SidLk = sidLk, Ecb = ecb }.ScheduleParallel();
        new DepotJob { TaskLk = taskLk, BankLk = bankLk, SidLk = sidLk, Ecb = ecb }.ScheduleParallel();
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
                int rate = t.Rate > 0 ? t.Rate : int.MaxValue;                 // gather speed cap
                if (room > 0 && avail > 0)
                {
                    var ask = new ResourceAmount(); ask[node.Yield] = math.max(1, math.min(math.min(room, avail), rate));
                    Ecb.AppendToBuffer(sortKey, self, new BankRequest
                    { Amount = ask, RequesterStableId = SidLk[h].Value, Class = (byte)SpendClass.Transfer, CastTick = 0 });
                }
                else { t.Phase = HarvestPhase.ToDepot; t.DepotStableId = -1; }  // full or node empty -> deliver
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

        private void Execute([ChunkIndexInQuery] int sortKey, Entity self, in StableId depotSid,
                             in Player player, in DynamicBuffer<UnitInfo> contacts)
        {
            for (int i = 0; i < contacts.Length; i++)
            {
                if (contacts[i].Player != player.Value) continue;          // own harvesters only
                var h = contacts[i].Entity;
                if (!TaskLk.HasComponent(h) || !BankLk.HasComponent(h)) continue;
                var t = TaskLk[h];
                if (t.DepotStableId != depotSid.Value) continue;
                if (t.Phase != HarvestPhase.ToDepot && t.Phase != HarvestPhase.Depositing) continue;

                t.Phase = HarvestPhase.Depositing;                          // arrived
                var cargo = BankLk[h].Amounts;
                if (cargo.Any)
                    Ecb.AppendToBuffer(sortKey, h, new BankRequest
                    { Amount = cargo, RequesterStableId = depotSid.Value, Class = (byte)SpendClass.Transfer, CastTick = 0 });
                else t.Phase = HarvestPhase.ToNode;                          // emptied -> go again
                TaskLk[h] = t;
            }
        }
    }
}
