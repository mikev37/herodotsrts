using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

// ===========================================================================
// CONSTRUCTION — receiver-side, Burst-parallel, PAY-AS-YOU-BUILD.
//
// Progress is bounded by the fraction actually PAID: paidFrac = min over
// resources of Paid/Cost. Each tick the site advances by min(builderPower,
// paidFrac-allowed, remaining), then requests ONLY the deficit needed to fund the
// NEXT power-step of progress — as one grouped request the bank grants
// proportionally. So a site that can't get gold simply stops advancing (and stops
// pulling food); it never banks food it can't yet turn into progress. Paid is the
// exact refund on cancel.
//
// Building-side is mandatory (a site never appears in a mobile unit's ContactList);
// the site also stamps BuildSignal on its contributing builders for the Build anim.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(InformationGatherSystem))]   // reads the site's ContactList (must be fresh)
public partial struct ConstructionSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerBankRegistry>();
        state.RequireForUpdate<SimClock>();
        state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                           .CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter();
        new BuildJob
        {
            Tick = SystemAPI.GetSingleton<SimClock>().Tick,
            Banks = SystemAPI.GetSingleton<PlayerBankRegistry>().Map,
            PowerLk = SystemAPI.GetComponentLookup<BuildPower>(true),
            DefIdLk = SystemAPI.GetComponentLookup<UnitDefId>(true),
            PrioLk = SystemAPI.GetComponentLookup<SpendPriority>(true),
            SignalLk = SystemAPI.GetComponentLookup<BuildSignal>(false),
            Ecb = ecb,
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct BuildJob : IJobEntity
    {
        public uint Tick;
        [ReadOnly] public NativeParallelHashMap<int, Entity> Banks;
        [ReadOnly] public ComponentLookup<BuildPower> PowerLk;
        [ReadOnly] public ComponentLookup<UnitDefId> DefIdLk;
        [ReadOnly] public ComponentLookup<SpendPriority> PrioLk;
        [NativeDisableParallelForRestriction] public ComponentLookup<BuildSignal> SignalLk;
        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute([ChunkIndexInQuery] int sortKey, Entity self, in StableId selfSid, in Player player,
                             in DynamicBuffer<UnitInfo> contacts, ref Construction site, ref Health hp,
                             ref DynamicBuffer<BankDeposit> deposits)
        {
            for (int i = 0; i < deposits.Length; i++) site.Paid += deposits[i].Amount;   // consume == receive
            deposits.Clear();

            // sacrifice gate: wait for the required unit to reach the site, consume it, THEN build
            if (site.SacrificeDefId >= 0)
            {
                for (int i = 0; i < contacts.Length; i++)
                {
                    var c = contacts[i];
                    if (c.Player == player.Value && DefIdLk.HasComponent(c.Entity) && DefIdLk[c.Entity].Value == site.SacrificeDefId)
                    {
                        Ecb.DestroyEntity(sortKey, c.Entity);   // the worker is consumed
                        site.SacrificeDefId = -1;               // gate opens; SelfPower (set at placement) now drives it
                        break;
                    }
                }
                if (site.SacrificeDefId >= 0) return;           // still waiting -> no progress this tick
            }

            float power = site.SelfPower;   // Protoss-style auto-build; 0 for worker-built
            for (int i = 0; i < contacts.Length; i++)
            {
                var c = contacts[i];
                if (c.Player == player.Value && PowerLk.HasComponent(c.Entity))
                {
                    power += PowerLk[c.Entity].Value;
                    if (SignalLk.HasComponent(c.Entity)) SignalLk[c.Entity] = new BuildSignal { LastTick = Tick };
                }
            }

            // progress is capped by what's been paid
            float paidFrac = 1f;
            for (int r = 0; r < ResourceAmount.Count; r++)
                if (site.Cost[r] > 0) paidFrac = math.min(paidFrac, (float)site.Paid[r] / site.Cost[r]);

            float allowedByPay = site.BuildTime * paidFrac;
            float gain = math.min(power, allowedByPay - site.Progress);
            gain = math.clamp(gain, 0f, site.BuildTime - site.Progress);
            if (gain > 0f) { site.Progress += gain; hp.Current = math.min(hp.Max, hp.Current + gain * site.HealthPerProgress); }

            // request the deficit to fund the NEXT power-step (one grouped, proportional ask)
            if (power > 0f && site.Progress < site.BuildTime && Banks.TryGetValue(player.Value, out Entity bank))
            {
                float nextProg = math.min(site.BuildTime, site.Progress + power);
                float frac = site.BuildTime > 0f ? nextProg / site.BuildTime : 1f;
                var required = new ResourceAmount
                {
                    Gold = (int)math.round(site.Cost.Gold * frac),
                    Wood = (int)math.round(site.Cost.Wood * frac),
                    Food = (int)math.round(site.Cost.Food * frac),
                };
                var deficit = ResourceAmount.Max0(required - site.Paid);
                if (deficit.Any)
                {
                    byte high = PrioLk.HasComponent(self) ? PrioLk[self].High : (byte)0;
                    Ecb.AppendToBuffer(sortKey, bank, new BankRequest
                    {
                        Amount = deficit, RequesterStableId = selfSid.Value,
                        Class = (byte)(high != 0 ? SpendClass.ConstructionHigh : SpendClass.ConstructionLow),
                        CastTick = 0,
                    });
                }
            }

            if (site.Progress >= site.BuildTime) Ecb.RemoveComponent<Construction>(sortKey, self);
        }
    }
}
