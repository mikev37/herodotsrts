using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// ABILITY FIRE — resolves PendingCasts whose FireTick has arrived. ONE fire
// path for everything: instant casts (ChargeUpTicks = 0, committed earlier
// this same tick by CommandApplySystem) and charged casts alike.
//
// At fire, from sim state AT THE FIRE TICK:
//   * geometry — Hero anchors use the caster's position/facing now; WorldPoint
//     uses the committed click point. LINE shapes always run FROM the caster
//     TOWARD the point (center = caster, dir = caster->point), so a line
//     ability is literally "between the caster and the world point".
//   * spawn — spawnUnit abilities spawn their unit/building at the point
//     (validated at commit; buildings re-snap via SpawnUnit).
//   * banner/totem — AnchorToSpawn binds the field to the spawned unit:
//     it follows it, dies with it, and kills it when the field expires
//     (handled in AbilityFieldSystem via BoundToSpawn).
//   * the AbilityCastEvent for VFX fires here (at the actual fire, not commit).
//
// A caster that died while charging never fires (its costs are lost — the
// charge was interrupted). Pure-spawn abilities (no modifiers) skip the field.
//
// NOT Burst-compiled: spawning is structural and reads the managed
// AbilityManager / UnitManager registries — a handful of casts per tick.
// Ordered after CommandApplySystem so 0-charge casts fire the tick they
// commit, and before BehaviorSystem like the old inline cast did.
// ===========================================================================
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(CommandApplySystem))]
[UpdateBefore(typeof(BehaviorSystem))]
public partial struct AbilityCastSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<SimClock>() || !SystemAPI.HasSingleton<CommandQueueTag>()) return;
        uint tick = SystemAPI.GetSingleton<SimClock>().Tick;
        var qe = SystemAPI.GetSingletonEntity<CommandQueueTag>();
        bool hasTerrain = SystemAPI.TryGetSingleton<TerrainHeightField>(out var terrain) && terrain.IsValid;
        var em = state.EntityManager;

        // Collect due casters first: firing is structural (field/unit spawns)
        // and would invalidate a live iterator. Chunk order is deterministic,
        // and fire order only matters for FieldIdSeq, which is itself state.
        var due = new NativeList<Entity>(4, Allocator.Temp);
        foreach (var (pending, entity) in SystemAPI.Query<RefRW<PendingCast>>().WithEntityAccess())
        {
            if (pending.ValueRO.Active == 0) continue;
            if (SystemAPI.HasComponent<Dead>(entity))           // died mid-charge -> cast is lost
            {
                pending.ValueRW.Active = 0;
                continue;
            }
            if (tick < pending.ValueRO.FireTick) continue;
            due.Add(entity);
        }

        for (int i = 0; i < due.Length; i++)
        {
            Entity caster = due[i];
            var pending = em.GetComponentData<PendingCast>(caster);
            pending.Active = 0;
            em.SetComponentData(caster, pending);
            Fire(em, qe, caster, pending.AbilityId, pending.TargetPos, hasTerrain, terrain);
        }
        due.Dispose();
    }

    private static void Fire(EntityManager em, Entity qe, Entity caster, int abilityId,
                             float2 targetPos, bool hasTerrain, in TerrainHeightField terrain)
    {
        var mgr = AbilityManager.Instance;
        if (mgr == null || !mgr.TryGetSpec(abilityId, out var spec)) return;
        var def = mgr.GetDefinition(abilityId);

        // Geometry from sim state at the FIRE tick.
        var xf = em.GetComponentData<LocalTransform>(caster);
        float2 casterPos = new float2(xf.Position.x, xf.Position.z);
        float3 fwd3 = math.forward(xf.Rotation);
        float2 casterFwd = math.normalizesafe(new float2(fwd3.x, fwd3.z), new float2(0f, 1f));

        float2 center, dir;
        if (spec.Anchor == AnchorType.Hero)
        {
            center = casterPos; dir = casterFwd;
        }
        else if (spec.Shape == ShapeType.Line)
        {
            // The line runs FROM the caster TOWARD the clicked point.
            center = casterPos;
            dir = math.normalizesafe(targetPos - casterPos, casterFwd);
        }
        else
        {
            center = targetPos;
            dir = math.normalizesafe(targetPos - casterPos, casterFwd);
        }

        int team = em.HasComponent<Team>(caster) ? em.GetComponentData<Team>(caster).Value : 0;

        // ---- optional spawn ----------------------------------------------------
        Entity spawned = Entity.Null;
        if (spec.HasSpawn != 0 && def != null && def.spawnUnit != null && UnitManager.Instance != null)
        {
            var um = UnitManager.Instance;
            int defId = um.GetDefId(team, def.spawnUnit);
            if (defId >= 0)
            {
                // Buildings snap + re-derive Y inside SpawnUnit; plain units
                // spawn at the terrain height under the point.
                float y = hasTerrain ? NavTerrain.SampleHeight(terrain, targetPos) : 0f;
                spawned = um.SpawnUnit(def.spawnUnit, defId, team, new float3(targetPos.x, y, targetPos.y));
            }
        }

        // ---- the field (skipped for pure-spawn abilities with no effects) -----
        var mods = mgr.GetModifiers(abilityId);
        if (mods.Length > 0)
        {
            bool bindToSpawn = spec.AnchorToSpawn != 0 && spawned != Entity.Null;
            if (bindToSpawn)
            {
                var sx = em.GetComponentData<LocalTransform>(spawned);
                center = new float2(sx.Position.x, sx.Position.z);   // snapped spawn position
            }

            var seq = em.GetComponentData<FieldIdSeq>(qe);
            int fieldId = seq.Next++;
            em.SetComponentData(qe, seq);

            var fe = em.CreateEntity();
            em.AddComponentData(fe, new AbilityField
            {
                FieldId = fieldId,
                AbilityId = abilityId,
                Team = team,
                Affects = spec.Affects,
                Shape = spec.Shape,
                Radius = spec.Radius,
                Width = spec.Width,
                Length = spec.Length,
                Center = center,
                Dir = dir,
                Anchor = spec.Anchor,
                AnchorEntity = bindToSpawn ? spawned
                             : spec.Anchor == AnchorType.Hero ? caster : Entity.Null,
                BoundToSpawn = (byte)(bindToSpawn ? 1 : 0),
                Mode = spec.Mode,
                Lifetime = spec.Lifetime,
                RefreshWindow = 0.2f,
            });
            var fmods = em.AddBuffer<FieldModifier>(fe);
            for (int i = 0; i < mods.Length; i++) fmods.Add(mods[i]);
        }

        // View event (the field entity may die the same tick for CastOnce).
        em.GetBuffer<AbilityCastEvent>(qe).Add(new AbilityCastEvent { AbilityId = abilityId, Pos = center });
    }
}
