using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// A placed-but-unstarted building plan. NON-SOLID (no Obstacle) and untargetable
// (NonCombatant) — you cannot body-block with blueprints from across the map.
public struct BlueprintTag : IComponentData { }

// The builder's explicit assignment: which site (blueprint or scaffold) this
// worker is building. Peasants know WHICH building they build — contribution is
// task-gated, so clicking the tower funds the tower, not every site in reach.
public struct BuildTask : IComponentData { public int TargetStableId; }

// ===========================================================================
// BLUEPRINT -> SCAFFOLD conversion, exactly the design: "When a worker reaches
// a blueprint, they create a scaffold, which is a building under construction.
// At this point resources are removed from the bank." A blueprint becomes solid
// (Obstacle stamped), attackable (NonCombatant removed), and starts paying
// (Construction added at 1 HP) only when a builder TASKED to it stands within
// its build range of the footprint — and the footprint is clear of units.
// ===========================================================================
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(ConstructionSystem))]
public partial struct BlueprintSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var em = state.EntityManager;
        var factory = UnitFactory.Instance;
        if (factory == null) return;

        // builders with an assignment
        var bq = em.CreateEntityQuery(ComponentType.ReadOnly<BuildTask>(), ComponentType.ReadOnly<BuildPower>(),
                                      ComponentType.ReadOnly<LocalTransform>(), ComponentType.Exclude<Dead>());
        var bTasks = bq.ToComponentDataArray<BuildTask>(Allocator.Temp);
        var bPows  = bq.ToComponentDataArray<BuildPower>(Allocator.Temp);
        var bXfs   = bq.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        var pq = em.CreateEntityQuery(ComponentType.ReadOnly<BlueprintTag>(), ComponentType.ReadOnly<StableId>(),
                                      ComponentType.ReadOnly<UnitDefId>(), ComponentType.ReadOnly<LocalTransform>(),
                                      ComponentType.ReadOnly<Player>());
        var pEnts = pq.ToEntityArray(Allocator.Temp);
        var pSids = pq.ToComponentDataArray<StableId>(Allocator.Temp);
        var pDefs = pq.ToComponentDataArray<UnitDefId>(Allocator.Temp);
        var pXfs  = pq.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var pPlayers = pq.ToComponentDataArray<Player>(Allocator.Temp);

        for (int i = 0; i < pEnts.Length; i++)
        {
            var def = factory.Roster.GetDefinition(pDefs[i].Value) as BuildingDefinition;
            if (def == null) continue;
            int2 ext = new int2(math.max(1, def.footprintX), math.max(1, def.footprintZ));
            float2 half = (float2)ext * (NavGrid.CellSize * 0.5f);
            float2 pos = new float2(pXfs[i].Position.x, pXfs[i].Position.z);

            // a tasked builder within ITS build range of the footprint edge?
            bool arrived = false;
            for (int b = 0; b < bTasks.Length && !arrived; b++)
            {
                if (bTasks[b].TargetStableId != pSids[i].Value || bPows[b].Value <= 0f) continue;
                float2 wp = new float2(bXfs[b].Position.x, bXfs[b].Position.z);
                if (CombatMath.DistanceToFootprint(wp, pos, half) <= math.max(0.5f, bPows[b].Range)) arrived = true;
            }
            if (!arrived) continue;

            // MOVE ASIDE: idle friendly units loitering in the footprint get a
            // soft nudge to the nearest edge so the scaffold can form. Units with
            // active orders are respected (a deliberate stand blocks conversion —
            // the player's choice; an ENEMY standing here is valid denial).
            EvacuateIdleFriendlies(em, pPlayers[i].Value, pos, half);

            // footprint must be clear of mobile units (the builder stands outside it)
            if (FootprintBlocked(em, pos, half)) continue;

            // convert: solid, attackable, paying
            em.AddComponentData(pEnts[i], new Obstacle { Extents = ext });
            em.RemoveComponent<BlueprintTag>(pEnts[i]);
            if (em.HasComponent<NonCombatant>(pEnts[i])) em.RemoveComponent<NonCombatant>(pEnts[i]);
            em.AddComponentData(pEnts[i], new Construction
            {
                Progress = 0f,
                BuildTime = math.max(1f, def.buildTime),
                Cost = new ResourceAmount { Gold = def.costGold, Wood = def.costWood, Food = def.costFood },
                Paid = default,
                HealthPerProgress = def.maxHealth / math.max(1f, def.buildTime),
                SelfPower = def.selfBuildPower,
                SacrificeDefId = -1,
            });
            var hp = em.GetComponentData<Health>(pEnts[i]);
            hp.Current = 1f;
            em.SetComponentData(pEnts[i], hp);
        }

        bTasks.Dispose(); bPows.Dispose(); bXfs.Dispose();
        pEnts.Dispose(); pSids.Dispose(); pDefs.Dispose(); pXfs.Dispose(); pPlayers.Dispose();
    }

    // Idle friendly mobile units inside the footprint get a soft slotless move to
    // the nearest edge — "make way, we're building here". Ordered units are left
    // alone; enemies are left alone (blocking a site is legitimate denial).
    private static void EvacuateIdleFriendlies(EntityManager em, int owner, float2 center, float2 half)
    {
        // exits must land on WALKABLE ground: with adjacent buildings the nearest
        // edge can sit inside a solid neighbor
        var oq = em.CreateEntityQuery(ComponentType.ReadOnly<ObstacleField>());
        var cellType = oq.IsEmptyIgnoreFilter ? default : oq.GetSingleton<ObstacleField>().CellType;
        using var q = em.CreateEntityQuery(
            ComponentType.ReadOnly<UnitTag>(), ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<Player>(), ComponentType.ReadWrite<MoveTarget>(),
            ComponentType.Exclude<Immobile>(), ComponentType.Exclude<Dead>());
        var ents = q.ToEntityArray(Allocator.Temp);
        var xfs = q.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var pls = q.ToComponentDataArray<Player>(Allocator.Temp);
        for (int i = 0; i < ents.Length; i++)
        {
            if (pls[i].Value != owner) continue;
            float2 p = new float2(xfs[i].Position.x, xfs[i].Position.z);
            if (!math.all(math.abs(p - center) < half)) continue;   // not inside
            var mv = em.GetComponentData<MoveTarget>(ents[i]);
            if (mv.HasTarget) continue;                              // busy: respect the order
            // nearest edge, pushed out slightly past the wall
            float2 h = half + 0.9f;
            float2 d = p - center;
            float2 exit = h - math.abs(d);
            if (exit.x <= exit.y) d.x = (d.x >= 0 ? h.x : -h.x);
            else d.y = (d.y >= 0 ? h.y : -h.y);
            mv.Value = NavGrid.SnapStandableOutside(cellType, center + d, center, half, 12);
            mv.HasTarget = true; mv.AttackMove = true; mv.FormationId = 0;   // soft slotless nudge
            em.SetComponentData(ents[i], mv);
        }
        ents.Dispose(); xfs.Dispose(); pls.Dispose();
    }

    private static bool FootprintBlocked(EntityManager em, float2 center, float2 half)
    {
        using var q = em.CreateEntityQuery(
            ComponentType.ReadOnly<UnitTag>(), ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<UnitRadius>(), ComponentType.Exclude<Immobile>(), ComponentType.Exclude<Dead>());
        var xfs = q.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var rads = q.ToComponentDataArray<UnitRadius>(Allocator.Temp);
        bool blocked = false;
        for (int i = 0; i < xfs.Length && !blocked; i++)
        {
            float2 p = new float2(xfs[i].Position.x, xfs[i].Position.z);
            float2 d = math.abs(p - center) - half;
            if (math.length(math.max(d, 0f)) < rads[i].Value) blocked = true;
        }
        xfs.Dispose(); rads.Dispose();
        return blocked;
    }
}
