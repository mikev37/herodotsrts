using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

// ===========================================================================
// MORPH / UPGRADE — in-place form swap of the SAME entity (keeps StableId,
// selection, snapshot identity). ONE mechanism for both:
//   * FREE morph  (trebuchet siege, dino settle): MorphState.Cost = 0; the
//     transition just advances 1 build-tick/tick until BuildTime.
//   * PAID upgrade (Keep -> Castle, unit -> better unit): Cost/BuildTime come
//     from the target def; the transition advances pay-as-you-build, identical
//     to ConstructionSystem (progress capped by the fraction paid, deficit
//     requested each tick), then swaps.
//
// On completion the entity adopts the target form: stats re-copied from the
// target def (UnitFactory.ApplyStats, preserving HP/mana FRACTION), building/unit
// structural bits toggled, Obstacle stamped in place when planting (no snap /
// no placement check — unit radius is sized so footprints don't overlap), and
// economy roles cleared then reapplied for the new form. UnitInfo.IsBuilding and
// the nav grid update themselves (InfoGather derives IsBuilding from BuildingTag;
// ObstacleGridSystem re-rasterizes when the Obstacle set changes).
// ===========================================================================
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class MorphSystem : SystemBase
{
    protected override void OnUpdate()
    {
        var factory = UnitFactory.Instance;
        if (factory == null || !factory.Ready) return;
        var roster = factory.Roster;
        var em = EntityManager;

        bool hasBanks = SystemAPI.HasSingleton<PlayerBankRegistry>();
        var banks = hasBanks ? SystemAPI.GetSingleton<PlayerBankRegistry>().Map : default;

        var toSwap = new NativeList<Entity>(8, Allocator.Temp);

        // --- pay/advance pass (non-structural: SetComponentData + buffer appends) ---
        foreach (var (msRef, e) in SystemAPI.Query<RefRW<MorphState>>().WithEntityAccess())
        {
            var ms = msRef.ValueRO;

            if (em.HasBuffer<BankDeposit>(e))   // receive grants -> Paid
            {
                var dep = em.GetBuffer<BankDeposit>(e);
                for (int i = 0; i < dep.Length; i++) ms.Paid += dep[i].Amount;
                dep.Clear();
            }

            float paidFrac = 1f;
            if (ms.Cost.Gold > 0 || ms.Cost.Wood > 0 || ms.Cost.Food > 0)
                for (int r = 0; r < ResourceAmount.Count; r++)
                    if (ms.Cost[r] > 0) paidFrac = math.min(paidFrac, (float)ms.Paid[r] / ms.Cost[r]);

            float allowed = ms.BuildTime * paidFrac;
            ms.Progress = math.min(math.min(ms.Progress + 1f, allowed), ms.BuildTime);

            // request the deficit for the next step (paid upgrades only)
            if (ms.Cost.Any && ms.Progress < ms.BuildTime && hasBanks &&
                em.HasComponent<Player>(e) && banks.TryGetValue(em.GetComponentData<Player>(e).Value, out var bank) &&
                em.HasBuffer<BankRequest>(bank))
            {
                float frac = ms.BuildTime > 0f ? math.min(1f, (ms.Progress + 1f) / ms.BuildTime) : 1f;
                var required = new ResourceAmount {
                    Gold = (int)math.round(ms.Cost.Gold * frac),
                    Wood = (int)math.round(ms.Cost.Wood * frac),
                    Food = (int)math.round(ms.Cost.Food * frac) };
                var deficit = ResourceAmount.Max0(required - ms.Paid);
                if (deficit.Any)
                    em.GetBuffer<BankRequest>(bank).Add(new BankRequest {
                        Amount = deficit, RequesterStableId = em.GetComponentData<StableId>(e).Value,
                        Class = (byte)SpendClass.ConstructionHigh, CastTick = 0 });
            }

            msRef.ValueRW = ms;
            if (ms.Progress >= ms.BuildTime) toSwap.Add(e);
        }

        // --- swap pass (structural) ---
        foreach (var e in toSwap)
        {
            var ms = em.GetComponentData<MorphState>(e);
            var def = roster.GetDefinition(ms.TargetDefId);
            if (def == null) { em.RemoveComponent<MorphState>(e); continue; }
            int player = em.HasComponent<Player>(e) ? em.GetComponentData<Player>(e).Value : 0;

            // 1) full stat block of the new form (preserve HP/mana fraction); sets UnitDefId + UnitRadius
            factory.ApplyStats(e, def, ms.TargetDefId, preserveVitals: true);

            // 2) building/unit structural bits — plant in place (no snap, no validation)
            if (ms.ToBuilding != 0)
            {
                Add<BuildingTag>(em, e); Add<Immobile>(em, e);
                var b = def as BuildingDefinition;
                int2 ext = b != null ? new int2(math.max(1, b.footprintX), math.max(1, b.footprintZ)) : new int2(1, 1);
                if (em.HasComponent<Obstacle>(e)) em.SetComponentData(e, new Obstacle { Extents = ext, Radius = 0f });
                else em.AddComponentData(e, new Obstacle { Extents = ext, Radius = 0f });
            }
            else
            {
                Remove<BuildingTag>(em, e); Remove<Immobile>(em, e); Remove<Obstacle>(em, e);
                if (!em.HasComponent<MoveTarget>(e)) em.AddComponent<MoveTarget>(e);

                // The building's rally point becomes the new unit's first waypoint:
                // a barracks morphing into a trebuchet walks to where it was told
                // to send its recruits. Rally cleared — units don't rally.
                if (em.HasComponent<RallyPoint>(e))
                {
                    var rp = em.GetComponentData<RallyPoint>(e);
                    if (rp.Has != 0)
                    {
                        if (!em.HasBuffer<Waypoint>(e)) em.AddBuffer<Waypoint>(e);
                        em.GetBuffer<Waypoint>(e).Add(new Waypoint { Pos = rp.Value, AttackMove = 0 });
                        em.SetComponentData(e, new RallyPoint { Has = 0 });
                    }
                }
            }

            // 3) economy roles: clear, then reapply the new form's (idempotent buffers)
            ClearEconomyRoles(em, e);
            factory.AddEconomyRoles(e, def, def as BuildingDefinition, player);

            em.RemoveComponent<MorphState>(e);
        }
        toSwap.Dispose();
    }

    private static void ClearEconomyRoles(EntityManager em, Entity e)
    {
        Remove<DepotTag>(em, e); Remove<IntakeTag>(em, e); Remove<ProducerTag>(em, e);
        Remove<NodeTag>(em, e); Remove<Colony>(em, e); Remove<Relay>(em, e);
        Remove<HarvestTask>(em, e); Remove<HaulTask>(em, e); Remove<BuildPower>(em, e);
        // banks/buffers stay (AddEconomyRoles is idempotent) so a colony keeps its holdings across an uproot.
    }

    private static void Add<T>(EntityManager em, Entity e) where T : unmanaged, IComponentData
    { if (!em.HasComponent<T>(e)) em.AddComponent<T>(e); }
    private static void Remove<T>(EntityManager em, Entity e) where T : unmanaged, IComponentData
    { if (em.HasComponent<T>(e)) em.RemoveComponent<T>(e); }
}
