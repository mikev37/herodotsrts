using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

// Multi-type depot -> player bank, via the bank job (no main-thread write): for
// each non-empty type, append a typed request from the depot's OWN bank paid to
// the player bank's StableId. Runs after ResourceBankSystem so the depot already
// reflects this tick's harvester deposits.
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ResourceBankSystem))]
public partial struct IntakeSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerBankRegistry>();
        state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                           .CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter();
        new IntakeJob
        {
            Banks = SystemAPI.GetSingleton<PlayerBankRegistry>().Map,
            SidLk = SystemAPI.GetComponentLookup<StableId>(true),
            Ecb = ecb,
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithAll(typeof(IntakeTag))]
    [WithNone(typeof(Dead))]
    private partial struct IntakeJob : IJobEntity
    {
        [ReadOnly] public NativeParallelHashMap<int, Entity> Banks;
        [ReadOnly] public ComponentLookup<StableId> SidLk;
        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute([ChunkIndexInQuery] int sortKey, Entity self, in Player player, in ResourceBank depot)
        {
            if (!Banks.TryGetValue(player.Value, out Entity bank) || !SidLk.HasComponent(bank)) return;
            if (depot.Amounts.Any)
                Ecb.AppendToBuffer(sortKey, self, new BankRequest
                { Amount = depot.Amounts, RequesterStableId = SidLk[bank].Value, Class = (byte)SpendClass.Transfer, CastTick = 0 });
        }
    }
}
