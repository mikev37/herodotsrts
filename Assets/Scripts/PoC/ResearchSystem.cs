using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

// ===========================================================================
// RESEARCH — a single-slot, pay-as-you-build process on a building (same funding
// loop as Construction/Production). On completion it:
//   1. records the tech on the player's bank entity (ResearchedTech),
//   2. auto-upgrades every existing unit of FromDefId the player owns by arming a
//      FREE MorphState -> ToDefId (the tech was already paid),
//   3. (future production is substituted in ProductionSystem, which reads the record).
// Managed (roster + structural morph adds), so SystemBase.
// ===========================================================================
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class ResearchSystem : SystemBase
{
    protected override void OnUpdate()
    {
        var factory = UnitFactory.Instance;
        if (factory == null || !factory.Ready) return;
        var roster = factory.Roster;
        var em = EntityManager;

        bool hasBanks = SystemAPI.HasSingleton<PlayerBankRegistry>();
        var banks = hasBanks ? SystemAPI.GetSingleton<PlayerBankRegistry>().Map : default;

        var done = new NativeList<Entity>(4, Allocator.Temp);

        // advance pass (non-structural)
        foreach (var (rtRef, e) in SystemAPI.Query<RefRW<ResearchTask>>().WithEntityAccess())
        {
            var rt = rtRef.ValueRO;
            if (em.HasBuffer<BankDeposit>(e))
            {
                var dep = em.GetBuffer<BankDeposit>(e);
                for (int i = 0; i < dep.Length; i++) rt.Paid += dep[i].Amount;
                dep.Clear();
            }
            float paidFrac = 1f;
            if (rt.Cost.Any)
                for (int r = 0; r < ResourceAmount.Count; r++)
                    if (rt.Cost[r] > 0) paidFrac = math.min(paidFrac, (float)rt.Paid[r] / rt.Cost[r]);
            rt.Progress = math.min(math.min(rt.Progress + 1f, rt.BuildTime * paidFrac), rt.BuildTime);

            if (rt.Cost.Any && rt.Progress < rt.BuildTime && hasBanks &&
                em.HasComponent<Player>(e) && banks.TryGetValue(em.GetComponentData<Player>(e).Value, out var bank) &&
                em.HasBuffer<BankRequest>(bank))
            {
                float frac = rt.BuildTime > 0f ? math.min(1f, (rt.Progress + 1f) / rt.BuildTime) : 1f;
                var req = new ResourceAmount {
                    Gold = (int)math.round(rt.Cost.Gold * frac), Wood = (int)math.round(rt.Cost.Wood * frac),
                    Food = (int)math.round(rt.Cost.Food * frac) };
                var deficit = ResourceAmount.Max0(req - rt.Paid);
                if (deficit.Any)
                    em.GetBuffer<BankRequest>(bank).Add(new BankRequest {
                        Amount = deficit, RequesterStableId = em.GetComponentData<StableId>(e).Value,
                        Class = (byte)SpendClass.ProducerHigh, CastTick = 0 });
            }
            rtRef.ValueRW = rt;
            if (rt.Progress >= rt.BuildTime) done.Add(e);
        }

        // completion pass (structural)
        foreach (var e in done)
        {
            var rt = em.GetComponentData<ResearchTask>(e);
            int player = em.HasComponent<Player>(e) ? em.GetComponentData<Player>(e).Value : 0;

            if (rt.FromDefId >= 0 && rt.ToDefId >= 0)
            {
                // record for future production substitution (on the player's bank entity)
                if (hasBanks && banks.TryGetValue(player, out var bankE) && em.HasBuffer<ResearchedTech>(bankE))
                    em.GetBuffer<ResearchedTech>(bankE).Add(new ResearchedTech { FromDefId = rt.FromDefId, ToDefId = rt.ToDefId });

                // auto-upgrade existing units: grab all of the player's FromDefId, arm a free morph
                var targets = new NativeList<Entity>(64, Allocator.Temp);
                EconomyQuery.GatherByPlayerType(em, player, rt.FromDefId, targets);
                var toDef = roster.GetDefinition(rt.ToDefId);
                bool toBuilding = toDef is BuildingDefinition;
                for (int i = 0; i < targets.Length; i++)
                    if (!em.HasComponent<MorphState>(targets[i]))
                        em.AddComponentData(targets[i], new MorphState {
                            TargetDefId = rt.ToDefId, ToBuilding = (byte)(toBuilding ? 1 : 0),
                            Progress = 0f, BuildTime = math.max(1, rt.MorphTicks), Cost = default, Paid = default });
                targets.Dispose();
            }
            em.RemoveComponent<ResearchTask>(e);
        }
        done.Dispose();
    }
}
