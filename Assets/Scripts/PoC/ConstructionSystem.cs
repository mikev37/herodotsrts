using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

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
            Dt = SystemAPI.Time.DeltaTime,
            Banks = SystemAPI.GetSingleton<PlayerBankRegistry>().Map,
            PowerLk = SystemAPI.GetComponentLookup<BuildPower>(true),
            TaskLk = SystemAPI.GetComponentLookup<BuildTask>(true),
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
        public float Dt;   // fixed lockstep tick length
        [ReadOnly] public NativeParallelHashMap<int, Entity> Banks;
        [ReadOnly] public ComponentLookup<BuildPower> PowerLk;
        [ReadOnly] public ComponentLookup<BuildTask> TaskLk;
        [ReadOnly] public ComponentLookup<UnitDefId> DefIdLk;
        [ReadOnly] public ComponentLookup<SpendPriority> PrioLk;
        [NativeDisableParallelForRestriction] public ComponentLookup<BuildSignal> SignalLk;
        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute([ChunkIndexInQuery] int sortKey, Entity self, in StableId selfSid, in Player player,
                             in LocalTransform xf, in Obstacle obstacle,
                             in DynamicBuffer<UnitInfo> contacts, ref Construction site, ref Health hp,
                             ref DynamicBuffer<BankDeposit> deposits)
        {
            float2 myPos = new float2(xf.Position.x, xf.Position.z);
            float2 half  = (float2)obstacle.Extents * (NavGrid.CellSize * 0.5f);
            for (int i = deposits.Length - 1; i >= 0; i--)
            {
                byte pu = deposits[i].Purpose;
                if (pu != (byte)SpendClass.ConstructionHigh && pu != (byte)SpendClass.ConstructionLow) continue;
                site.Paid += deposits[i].Amount;                                   // consume == receive
                site.InFlight = ResourceAmount.Max0(site.InFlight - deposits[i].Amount);
                deposits.RemoveAt(i);
            }

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
                if (c.Player != player.Value || !PowerLk.HasComponent(c.Entity)) continue;
                // Only builders TASKED to this site, within THEIR build range of
                // the footprint edge — proximity alone never builds (a passer-by
                // click near the site must not contribute).
                if (!TaskLk.HasComponent(c.Entity) || TaskLk[c.Entity].TargetStableId != selfSid.Value) continue;
                var bp = PowerLk[c.Entity];
                if (CombatMath.DistanceToFootprint(c.Position, myPos, half) > math.max(0.5f, bp.Range)) continue;
                power += bp.Value;
                if (SignalLk.HasComponent(c.Entity)) SignalLk[c.Entity] = new BuildSignal { LastTick = Tick };
            }

            // progress is capped by what's been paid
            float paidFrac = 1f;
            for (int r = 0; r < ResourceAmount.Count; r++)
                if (site.Cost[r] > 0) paidFrac = math.min(paidFrac, (float)site.Paid[r] / site.Cost[r]);

            // Progress is SECONDS of work: buildPower contributes power × Dt per
            // tick. (Unscaled, power 10 added TEN "seconds" per tick — a 60s
            // build finished in 6 ticks with an instant-looking bank drain.)
            float allowedByPay = site.BuildTime * paidFrac;
            float gain = math.min(power * Dt, allowedByPay - site.Progress);
            gain = math.clamp(gain, 0f, site.BuildTime - site.Progress);
            if (gain > 0f) { site.Progress += gain; hp.Current = math.min(hp.Max, hp.Current + gain * site.HealthPerProgress); }

            // request the deficit to fund the NEXT power-step (one grouped, proportional ask)
            if (power > 0f && site.Progress < site.BuildTime && Banks.TryGetValue(player.Value, out Entity bank))
            {
                float nextProg = math.min(site.BuildTime, site.Progress + math.max(power * Dt, 1f));
                float frac = site.BuildTime > 0f ? nextProg / site.BuildTime : 1f;
                // CEIL, never round: round() truncated the first installment to
                // zero (the same deadlock production had) — nothing was ever
                // requested and the site never advanced past tick one.
                var required = new ResourceAmount
                {
                    Gold = math.min(site.Cost.Gold, (int)math.ceil(site.Cost.Gold * frac)),
                    Wood = math.min(site.Cost.Wood, (int)math.ceil(site.Cost.Wood * frac)),
                    Food = math.min(site.Cost.Food, (int)math.ceil(site.Cost.Food * frac)),
                };
                var deficit = ResourceAmount.Max0(required - site.Paid - site.InFlight);
                if (deficit.Any)
                {
                    byte high = PrioLk.HasComponent(self) ? PrioLk[self].High : (byte)0;
                    Ecb.AppendToBuffer(sortKey, bank, new BankRequest
                    {
                        Amount = deficit, RequesterStableId = selfSid.Value,
                        Class = (byte)(high != 0 ? SpendClass.ConstructionHigh : SpendClass.ConstructionLow),
                        CastTick = 0,
                    });
                    site.InFlight += deficit;   // don't re-bill while the payment travels
                    site.InFlightT = 6f;
                }
                // A short-funded bank pays less than asked; expire stale in-flight
                // so the shortfall is re-requested once funds exist.
                if (site.InFlight.Any && (site.InFlightT -= 1f) <= 0f) site.InFlight = default;
            }

            if (site.Progress >= site.BuildTime) Ecb.RemoveComponent<Construction>(sortKey, self);
        }
    }
}
