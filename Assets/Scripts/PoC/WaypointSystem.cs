using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// WAYPOINT SYSTEM — advances shift-queued destinations. When a unit has queued
// Waypoints and no active MoveTarget (the previous leg arrived or it was idle),
// pop the front into a SOFT slotless move: BehaviorSystem's direct-drive tier
// takes it straight there, and survival/engagement tiers can still interrupt on
// an attack-move leg. Queued chains are per-unit (no formation slots); a fresh
// unqueued order clears the buffer (CommandSystem).
//
// Arrival is detected HERE: slotless soft moves have no FormationSystem arrival
// handling, so this system clears the leg within ArriveRadius and pops the next.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(CommandApplySystem))]
[UpdateBefore(typeof(BehaviorSystem))]
public partial struct WaypointSystem : ISystem
{
    private const float ArriveRadius = 1.5f;

    public void OnUpdate(ref SystemState state)
    {
        bool hasReg = SystemAPI.TryGetSingleton<StableIdRegistry>(out var regS);
        var reg = hasReg ? regS.Map : default;
        bool hasGrid = SystemAPI.TryGetSingleton<ObstacleField>(out var obsF);
        var cellType = hasGrid ? obsF.CellType : default;
        var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                           .CreateCommandBuffer(state.WorldUnmanaged);

        foreach (var (wps, move, xf, entity) in
                 SystemAPI.Query<DynamicBuffer<Waypoint>, RefRW<MoveTarget>, RefRO<LocalTransform>>()
                          .WithNone<Dead>().WithEntityAccess())
        {
            ref var mv = ref move.ValueRW;
            float2 pos = new float2(xf.ValueRO.Position.x, xf.ValueRO.Position.z);

            // Complete the current slotless leg on arrival (only legs this system
            // issued: FormationId 0 + the buffer present).
            if (mv.HasTarget && mv.FormationId == 0 &&
                math.distance(pos, mv.Value) <= ArriveRadius)
                mv.HasTarget = false;

            // A BUILD leg holds until the site completes: while the assigned
            // blueprint/scaffold is still alive, the builder is BUSY — don't pop.
            // When it finishes (or dies), release the task and advance the chain.
            if (SystemAPI.HasComponent<BuildTask>(entity))
            {
                int ts = SystemAPI.GetComponent<BuildTask>(entity).TargetStableId;
                Entity se = Entity.Null;
                bool siteActive = hasReg && reg.TryGetValue(ts, out se) &&
                                  (SystemAPI.HasComponent<BlueprintTag>(se) || SystemAPI.HasComponent<Construction>(se));
                if (siteActive)
                {
                    if (!TryFootprint(se, out float2 c2, out float2 half2))
                    {
                        // def unavailable: fall back to on-station hold
                        if (mv.HasTarget && mv.FormationId == 0 &&
                            math.distance(pos, mv.Value) <= ArriveRadius * 2f)
                            mv.HasTarget = false;
                        continue;
                    }

                    // A blueprint is NON-SOLID: its center is standable ground, so
                    // builders could walk INSIDE the footprint — and their own
                    // bodies then blocked the scaffold conversion forever.
                    // Evacuate to the nearest OUTSIDE standable point.
                    if (SystemAPI.HasComponent<BlueprintTag>(se) &&
                        math.all(math.abs(pos - c2) < half2))
                    {
                        mv.Value = NavGrid.SnapStandableOutside(cellType, PerimeterPoint(pos, c2, half2, 0.5f), c2, half2, 12);
                        mv.HasTarget = true; mv.AttackMove = true; mv.FormationId = 0;
                        continue;
                    }

                    // CLOSED LOOP against the site: a tasked builder outside its
                    // buildRange is RE-VECTORED to the perimeter — evacuation
                    // overshoot, the blueprint->scaffold conversion, or a combat
                    // shove all end with the builder walking back to work. Within
                    // range it stands and works.
                    float range = math.max(0.5f, SystemAPI.HasComponent<BuildPower>(entity)
                                                 ? SystemAPI.GetComponent<BuildPower>(entity).Range : 2.5f);
                    float edge = CombatMath.DistanceToFootprint(pos, c2, half2);
                    if (edge > range)
                    {
                        mv.Value = NavGrid.SnapStandableOutside(cellType, PerimeterPoint(pos, c2, half2, 0.5f), c2, half2, 12);
                        mv.HasTarget = true; mv.AttackMove = true; mv.FormationId = 0;
                    }
                    else mv.HasTarget = false;   // on station: stop pushing, work
                    continue;
                }
                mv.HasTarget = false;                     // site done -> free to advance
                ecb.RemoveComponent<BuildTask>(entity);   // next tick pops the next leg
                continue;
            }

            if (wps.Length == 0 || mv.HasTarget) continue;

            var wp = wps[0];
            wps.RemoveAt(0);
            mv.Value = wp.Pos;
            mv.HasTarget = true;
            mv.AttackMove = true;                          // soft: direct-drive + combat tiers can interrupt
            mv.FormationId = 0;                            // slotless — individual travel

            if (wp.Kind == 1)                              // BUILD leg: take the assignment
            {
                ecb.AddComponent(entity, new BuildTask { TargetStableId = wp.TargetStableId });
                // Aim at the footprint PERIMETER on the builder's side, never the
                // center — standing inside a non-solid plan deadlocks conversion.
                // SNAPPED to standable ground: with ADJACENT buildings the nearest
                // boundary point of blueprint #2 can sit INSIDE the just-built
                // solid #1, and the raw point had the builder ramming that wall.
                if (hasReg && reg.TryGetValue(wp.TargetStableId, out Entity bpe) &&
                    TryFootprint(bpe, out float2 bc, out float2 bh))
                    mv.Value = NavGrid.SnapStandableOutside(cellType, PerimeterPoint(pos, bc, bh, 0.9f), bc, bh, 12);
            }
        }
    }

    // The site's world footprint (center + half extents) from its definition.
    private static bool TryFootprint(Entity site, out float2 center, out float2 half)
    {
        center = default; half = default;
        if (UnitFactory.Instance == null) return false;
        var em = Unity.Entities.World.DefaultGameObjectInjectionWorld.EntityManager;
        if (!em.HasComponent<LocalTransform>(site) || !em.HasComponent<UnitDefId>(site)) return false;
        var def = UnitFactory.Instance.Roster.GetDefinition(em.GetComponentData<UnitDefId>(site).Value) as BuildingDefinition;
        if (def == null) return false;
        var p = em.GetComponentData<LocalTransform>(site).Position;
        center = new float2(p.x, p.z);
        half = new float2(math.max(1, def.footprintX), math.max(1, def.footprintZ)) * (NavGrid.CellSize * 0.5f);
        return true;
    }

    // Closest point ON the footprint boundary (expanded by margin) from `from` —
    // a point inside is pushed out along the shortest axis.
    private static float2 PerimeterPoint(float2 from, float2 center, float2 half, float margin)
    {
        float2 h = half + margin;
        float2 d = from - center;
        if (math.all(math.abs(d) < h))   // inside: exit via the nearest face
        {
            float2 exit = h - math.abs(d);
            if (exit.x <= exit.y) d.x = (d.x >= 0 ? h.x : -h.x);
            else d.y = (d.y >= 0 ? h.y : -h.y);
            return center + d;
        }
        return center + math.clamp(d, -h, h);   // outside: clamp onto the boundary
    }
}
