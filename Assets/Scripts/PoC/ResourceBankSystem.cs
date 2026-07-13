using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

// ===========================================================================
// RESOURCE BANK SYSTEM — the ONLY writer of any bank Amounts. Per bank: fold the
// grouped deposits, then serve grouped requests in priority order, granting each
// the largest PROPORTIONAL fraction of itself the bank can still afford.
//
//   order:   (Class, CastTick, RequesterStableId)
//   grant:   frac = min over resources of (available / requested); give frac*request
//
// Proportional granting is what keeps a build's resources in step: a request for
// 50 gold + 100 food against a bank with only 25 gold is granted at 50% -> 25
// gold + 50 food, never 0 gold + 100 food. Deposits are summed (order-free).
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(HarvestSystem))]
[UpdateBefore(typeof(ProductionSystem))]
public partial struct ResourceBankSystem : ISystem
{
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
        new BankJob { Registry = SystemAPI.GetSingleton<StableIdRegistry>().Map, Ecb = ecb }.ScheduleParallel();
    }

    [BurstCompile]
    private partial struct BankJob : IJobEntity
    {
        [ReadOnly] public NativeParallelHashMap<int, Entity> Registry;
        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute([ChunkIndexInQuery] int sortKey, ref ResourceBank bank,
                             ref DynamicBuffer<BankDeposit> deposits, ref DynamicBuffer<BankRequest> requests)
        {
            // income
            for (int i = deposits.Length - 1; i >= 0; i--)
            {
                // Producer/Construction installments belong to ProductionSystem /
                // ConstructionSystem — leave them in the mailbox. Consuming them
                // here (a depot+producer castle) stole production's money into
                // stores, and the intake looped it back to the player bank forever.
                byte p = deposits[i].Purpose;
                if (p == (byte)SpendClass.ProducerHigh || p == (byte)SpendClass.ProducerLow ||
                    p == (byte)SpendClass.ConstructionHigh || p == (byte)SpendClass.ConstructionLow) continue;

                bank.Amounts += deposits[i].Amount;
                for (int r = 0; r < ResourceAmount.Count; r++)
                    if (bank.Capacity[r] > 0 && bank.Amounts[r] > bank.Capacity[r]) bank.Amounts[r] = bank.Capacity[r];
                deposits.RemoveAt(i);
            }

            int n = requests.Length;
            if (n == 0) return;

            var sorted = new NativeArray<BankRequest>(n, Allocator.Temp);
            for (int i = 0; i < n; i++) sorted[i] = requests[i];
            for (int i = 1; i < n; i++)   // insertion sort by (Class, CastTick, StableId)
            {
                var k = sorted[i]; int j = i - 1;
                while (j >= 0 && Greater(sorted[j], k)) { sorted[j + 1] = sorted[j]; j--; }
                sorted[j + 1] = k;
            }
            requests.Clear();

            for (int i = 0; i < n; i++)
            {
                var req = sorted[i];
                if (bank.Paused != 0) continue;
                ResourceAmount.AffordableFraction(bank.Amounts, req.Amount, out int num, out int den);
                var give = req.Amount.Scaled(num, den);
                if (!give.Any) continue;
                if (!Registry.TryGetValue(req.RequesterStableId, out Entity to)) continue;   // recipient gone -> drop
                bank.Amounts -= give;
                Ecb.AppendToBuffer(sortKey, to, new BankDeposit { Amount = give, Purpose = req.Class });
            }
            sorted.Dispose();
        }

        // a comes AFTER b?  (Class, then CastTick, then StableId)
        private static bool Greater(in BankRequest a, in BankRequest b)
        {
            if (a.Class != b.Class) return a.Class > b.Class;
            if (a.CastTick != b.CastTick) return a.CastTick > b.CastTick;
            return a.RequesterStableId > b.RequesterStableId;
        }
    }
}
