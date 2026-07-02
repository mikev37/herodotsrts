using Unity.Collections;
using Unity.Entities;

// ===========================================================================
// Read-only economy queries for the UI / AI COMMANDER side. Affordability is NOT
// enforced by the simulation — the sim processes whatever command arrives (a
// "soft" read: the value is observed, never locked; the bank job is the single
// synchronized authority that actually moves resources). These helpers let the
// commander grey out a button / skip an AI action when the player can't pay.
//
// They read the bank's last-settled Amounts (the bank job ran last tick), which
// is exactly the value every peer would see — so an AI using them stays in sync.
// ===========================================================================
public static class EconomyQuery
{
    public static bool TryGetBank(EntityManager em, int player, out ResourceAmount amounts)
    {
        amounts = default;
        using var q = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerBankTag>(),
                                           ComponentType.ReadOnly<Player>(), ComponentType.ReadOnly<ResourceBank>());
        var players = q.ToComponentDataArray<Player>(Allocator.Temp);
        var banks = q.ToComponentDataArray<ResourceBank>(Allocator.Temp);
        bool found = false;
        for (int i = 0; i < players.Length; i++)
            if (players[i].Value == player) { amounts = banks[i].Amounts; found = true; break; }
        players.Dispose(); banks.Dispose();
        return found;
    }

    // The button-enable test: does this player currently hold at least `cost`?
    public static bool CanAfford(EntityManager em, int player, ResourceAmount cost)
        => TryGetBank(em, player, out var have) && ResourceAmount.Covers(have, cost);

    // ----- building activity (one job at a time) -------------------------------
    public enum ActivityKind : byte { None, Construction, Production, Upgrade, Research }

    public struct ActivityInfo
    {
        public ActivityKind Kind;
        public float        Progress01;   // 0..1 for the progress bar
        public int          DisplayDefId; // resolve to displayName + icon (the thing being made/built/become); -1 = n/a
        public int          QueueCount;   // production queue length (0 otherwise)
    }

    // What the building is working on RIGHT NOW (for a progress bar + name + image).
    // Mutual exclusion guarantees at most one of construction/upgrade/research/production.
    public static ActivityInfo GetActivity(EntityManager em, Entity e)
    {
        var a = new ActivityInfo { Kind = ActivityKind.None, DisplayDefId = -1 };
        if (em.HasComponent<Construction>(e))
        {
            var c = em.GetComponentData<Construction>(e);
            a.Kind = ActivityKind.Construction; a.Progress01 = Frac(c.Progress, c.BuildTime);
            a.DisplayDefId = DefIdOf(em, e); return a;
        }
        if (em.HasComponent<MorphState>(e))
        {
            var m = em.GetComponentData<MorphState>(e);
            a.Kind = ActivityKind.Upgrade; a.Progress01 = Frac(m.Progress, m.BuildTime); a.DisplayDefId = m.TargetDefId; return a;
        }
        if (em.HasComponent<ResearchTask>(e))
        {
            var r = em.GetComponentData<ResearchTask>(e);
            a.Kind = ActivityKind.Research; a.Progress01 = Frac(r.Progress, r.BuildTime); a.DisplayDefId = r.ToDefId; return a;
        }
        if (em.HasBuffer<ProductionItem>(e))
        {
            var q = em.GetBuffer<ProductionItem>(e);
            if (q.Length > 0)
            { a.Kind = ActivityKind.Production; a.Progress01 = Frac(q[0].Progress, q[0].BuildTime); a.DisplayDefId = q[0].UnitDefId; a.QueueCount = q.Length; }
        }
        return a;
    }

    // The production queue, head-first, as def ids (UI resolves to name/icon).
    public static void GetQueue(EntityManager em, Entity e, Unity.Collections.NativeList<int> outDefIds)
    {
        if (!em.HasBuffer<ProductionItem>(e)) return;
        var q = em.GetBuffer<ProductionItem>(e);
        for (int i = 0; i < q.Length; i++) outDefIds.Add(q[i].UnitDefId);
    }

    // Mutual exclusion gate. Returns the blocking job (None = free to start).
    // queueingProduction = true ignores an existing production queue (you can stack
    // the queue, but not while constructing/upgrading/researching).
    public static ActivityKind BuildingBusy(EntityManager em, Entity e, bool queueingProduction)
    {
        if (em.HasComponent<Construction>(e)) return ActivityKind.Construction;
        if (em.HasComponent<MorphState>(e))   return ActivityKind.Upgrade;
        if (em.HasComponent<ResearchTask>(e)) return ActivityKind.Research;
        if (!queueingProduction && em.HasBuffer<ProductionItem>(e) && em.GetBuffer<ProductionItem>(e).Length > 0)
            return ActivityKind.Production;
        return ActivityKind.None;
    }

    private static float Frac(float p, float t) => t > 0f ? Unity.Mathematics.math.clamp(p / t, 0f, 1f) : 0f;
    private static int DefIdOf(EntityManager em, Entity e) => em.GetComponentData<UnitDefId>(e).Value;

    // Convenience for unit/building defs.
    public static ResourceAmount UnitProdCost(UnitDefinition d)
        => new ResourceAmount { Gold = d.prodCostGold, Wood = d.prodCostWood, Food = d.prodCostFood };
    public static ResourceAmount BuildingCost(BuildingDefinition d)
        => new ResourceAmount { Gold = d.costGold, Wood = d.costWood, Food = d.costFood };

    // Grab every entity of (player, defId). With radius > 0, only those within
    // `radius` of `center`. Deterministic (query chunk order). Used by tech
    // upgrades ("turn all my Knights into Paladins") and area effects; the same
    // shape works for selection-by-type or "upgrade everything in this region".
    public static void GatherByPlayerType(EntityManager em, int player, int defId,
                                          Unity.Collections.NativeList<Entity> outList,
                                          bool useArea = false, Unity.Mathematics.float2 center = default, float radius = 0f)
    {
        using var q = em.CreateEntityQuery(ComponentType.ReadOnly<UnitDefId>(),
                                           ComponentType.ReadOnly<Player>(),
                                           ComponentType.ReadOnly<Unity.Transforms.LocalTransform>());
        var ents = q.ToEntityArray(Allocator.Temp);
        var ids  = q.ToComponentDataArray<UnitDefId>(Allocator.Temp);
        var pls  = q.ToComponentDataArray<Player>(Allocator.Temp);
        var xfs  = q.ToComponentDataArray<Unity.Transforms.LocalTransform>(Allocator.Temp);
        float r2 = radius * radius;
        for (int i = 0; i < ents.Length; i++)
        {
            if (pls[i].Value != player || ids[i].Value != defId) continue;
            if (useArea)
            {
                var p = new Unity.Mathematics.float2(xfs[i].Position.x, xfs[i].Position.z);
                if (Unity.Mathematics.math.distancesq(p, center) > r2) continue;
            }
            outList.Add(ents[i]);
        }
        ents.Dispose(); ids.Dispose(); pls.Dispose(); xfs.Dispose();
    }
}
