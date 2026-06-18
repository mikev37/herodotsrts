using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// Command pipeline — data + ECS side. (The issuing API lives in Commander.cs.)
//
//   PlayerCommander / AICommander / network  ->  Commander stream (static)
//        ->  CommandIngestSystem  ->  pending buffer  ->  CommandApplySystem
//
// A command carries an EXECUTION tick (issue tick + InputDelay) and the units it
// targets (by StableId). The same command, applied at the same tick on every
// client/replay, produces the same sim.
// ===========================================================================

public enum CommandKind : byte
{
    None = 0, Move = 1, AttackMove = 2, Stop = 3, AttackTarget = 4, Ability = 5,
    PlaceBuilding = 6,      // TargetPos = desired center (snapped at apply), TargetStableId = roster defId of the BuildingDefinition
    DemolishBuilding = 7,   // TargetStableId = StableId of an own-team building; flows through normal death
}

// One order. The struct is fully unmanaged/blittable (FixedList included), so it
// uses NGO's INetworkSerializeByMemcpy contract: a marker interface (no methods)
// that unlocks the public WriteValueSafe/ReadValueSafe ForStructs overloads —
// NGO memcpys the whole struct. No hand-written serializer to drift out of sync.
// (Wire cost: full 512-byte Units capacity is sent even for small selections —
// trivial at RTS command rates; optimize with manual packing only if it matters.)
public struct SimCommand : IBufferElementData, INetworkSerializeByMemcpy
{
    public uint                  Tick;            // execution tick
    public int                   PlayerId;
    public CommandKind           Kind;
    public float2                TargetPos;       // Move/AttackMove destination, or Ability cast point
    public int                   TargetStableId;  // AttackTarget victim
    public byte                  AbilitySlot;     // Ability: which slot (0..3); caster = Units[0]
    public FixedList512Bytes<int> Units;          // affected units (StableIds); up to ~125
}

// Marks the entity that owns the pending-command buffer (and the cast-event buffer).
public struct CommandQueueTag : IComponentData { }

// Per-unit roster of the LAST order this unit received: the StableIds of every
// unit that shared that order. Written by CommandApplySystem on each unit order,
// read by InformationGatherSystem to scope perception to the group — a unit only
// "sees" friendlies whose StableId is in this list. Persists until the unit's
// next order; empty => ungrouped (the gather falls back to proximity).
//
// PERSISTENT SIM STATE: serialize this in SimSnapshot like AttackOrder. It holds
// StableIds, not Entity refs, so it survives restore with NO fixup pass. Add the
// buffer to the unit archetype wherever FriendlyUnit is added, so the set of
// entities the gather runs on is unchanged. Capacity 0 keeps the (up to ~125-id)
// roster off the chunk, matching FriendlyUnit.
[InternalBufferCapacity(0)]
public struct GroupMember : IBufferElementData
{
    public int StableId;
}

// Deterministic sequence for AbilityField ids (replaces entity-index FieldIds,
// which aren't guaranteed identical across clients).
public struct FieldIdSeq : IComponentData { public int Next; }

// View-layer event: "ability X was cast at P this tick". Drained by
// AbilityManager to spawn cast VFX. Pure output — the sim never reads it.
public struct AbilityCastEvent : IBufferElementData
{
    public int    AbilityId;
    public float2 Pos;
}

// -------------------------------------------------------------------------
// Ingest: moves commands from the static Commander stream into the ECS pending
// buffer. Managed because it touches the stream. In Network mode, LockstepNet
// owns injection instead. Playback loads the whole recorded stream once (each
// command carries its own execution tick).
// -------------------------------------------------------------------------
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SimClockSystem))]
[UpdateBefore(typeof(CommandApplySystem))]
public partial class CommandIngestSystem : SystemBase
{
    private bool _playbackLoaded;
    private bool _warnedNetworkWithoutNet;

    protected override void OnUpdate()
    {
        if (Commander.Mode == Commander.LockstepMode.Network)
        {
            // In a live network session, LockstepNet drains the outbox and injects
            // combined turns itself. But if mode was left on Network with no
            // LockstepNet in the scene (e.g. after an MPPM test), the sim runs
            // freely while every command silently black-holes in the outbox.
            // Guard the trap: warn and fall through to normal ingestion.
            if (LockstepNet.Instance != null) return;
            if (!_warnedNetworkWithoutNet)
            {
                _warnedNetworkWithoutNet = true;
                Debug.LogWarning("[Lockstep] Commander mode is Network but no LockstepNet is in the scene — " +
                                 "ingesting commands locally so orders still work. Set mode = Live for " +
                                 "single-player, or add LockstepNet for a networked session.");
            }
        }
        if (!SystemAPI.HasSingleton<CommandQueueTag>()) return;

        var qe = SystemAPI.GetSingletonEntity<CommandQueueTag>();
        var buf = EntityManager.GetBuffer<SimCommand>(qe);

        if (Commander.Mode == Commander.LockstepMode.Playback)
        {
            if (!_playbackLoaded)
            {
                var rec = Commander.Recorded;
                for (int i = 0; i < rec.Count; i++) buf.Add(rec[i]);
                _playbackLoaded = true;
            }
        }
        else
        {
            while (Commander.Outbox.Count > 0) buf.Add(Commander.Outbox.Dequeue());
        }
    }
}

// -------------------------------------------------------------------------
// Apply: each tick, fire pending commands whose execution tick == now. Resolves
// StableIds via the registry and writes MoveTarget / AttackOrder; Ability
// commands spawn an AbilityField from the AbilityManager's baked specs, gated by
// the caster's tick-based cooldowns, and emit an AbilityCastEvent for VFX.
//
// NOT Burst-compiled: ability casts are structural changes and read the managed
// AbilityManager registry. This is a handful of commands per tick — determinism
// doesn't require Burst here (no float math beyond copying), and FloatMode only
// concerns Burst-compiled math anyway.
// -------------------------------------------------------------------------
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(StableIdRegistrySystem))]
[UpdateBefore(typeof(BehaviorSystem))]
public partial struct CommandApplySystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<CommandQueueTag>())
        {
            var e = state.EntityManager.CreateEntity(typeof(CommandQueueTag), typeof(FieldIdSeq));
            state.EntityManager.AddBuffer<SimCommand>(e);
            state.EntityManager.AddBuffer<AbilityCastEvent>(e);
            state.EntityManager.SetComponentData(e, new FieldIdSeq { Next = 1 });
        }
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<SimClock>() || !SystemAPI.HasSingleton<StableIdRegistry>()) return;

        uint tick = SystemAPI.GetSingleton<SimClock>().Tick;
        var map = SystemAPI.GetSingleton<StableIdRegistry>().Map;
        var qe  = SystemAPI.GetSingletonEntity<CommandQueueTag>();
        var em  = state.EntityManager;

        // Shared validation inputs, fetched once. The Passable array and the
        // resource ENTITY survive structural changes within the loop (the
        // resource BUFFER is re-fetched at use, since spawns invalidate it).
        bool hasGrid = SystemAPI.HasSingleton<ObstacleField>();
        NativeArray<byte> passable = hasGrid ? SystemAPI.GetSingleton<ObstacleField>().Passable : default;
        NativeArray<byte> cellType = hasGrid ? SystemAPI.GetSingleton<ObstacleField>().CellType : default;
        bool hasTerrain = SystemAPI.TryGetSingleton<TerrainHeightField>(out var terrain) && terrain.IsValid;
        Entity resourceEntity = SystemAPI.HasSingleton<ResourcePoolTag>()
            ? SystemAPI.GetSingletonEntity<ResourcePoolTag>() : Entity.Null;

        // 1) Pull this tick's commands out of the buffer (and drop expired ones)
        //    BEFORE applying anything: ability casts are structural changes that
        //    would invalidate the buffer mid-iteration.
        var due = new NativeList<SimCommand>(8, Allocator.Temp);
        {
            var buf = em.GetBuffer<SimCommand>(qe);
            for (int i = 0; i < buf.Length; i++)
                if (buf[i].Tick == tick) due.Add(buf[i]);
            for (int i = buf.Length - 1; i >= 0; i--)
                if (buf[i].Tick <= tick) buf.RemoveAt(i);
        }

        for (int ci = 0; ci < due.Length; ci++)
        {
            SimCommand c = due[ci];

            if (c.Kind == CommandKind.Ability)
            {
                CommitAbility(ref state, c, tick, map, hasGrid, passable, cellType, hasTerrain, terrain, resourceEntity);
                continue;
            }

            if (c.Kind == CommandKind.PlaceBuilding)
            {
                if (hasGrid)
                    ApplyPlaceBuilding(c, cellType, hasTerrain, terrain);
                continue;
            }

            if (c.Kind == CommandKind.DemolishBuilding)
            {
                // Own-team buildings only; "demolish" is just health -> 0 so the
                // normal death pipeline (anim, view recycle, obstacle unblock,
                // checksum) handles everything.
                if (map.TryGetValue(c.TargetStableId, out Entity b) &&
                    em.HasComponent<BuildingTag>(b) && em.HasComponent<Health>(b) &&
                    em.GetComponentData<Team>(b).Value == c.PlayerId)
                {
                    var hp = em.GetComponentData<Health>(b);
                    hp.Current = 0f;
                    em.SetComponentData(b, hp);
                }
                continue;
            }

            Entity atkTarget = Entity.Null;
            if (c.Kind == CommandKind.AttackTarget) map.TryGetValue(c.TargetStableId, out atkTarget);

            // ---- collect the formation members + the group frame --------------
            int cap = c.Units.Length;
            var ents = new NativeList<Entity>(cap, Allocator.Temp);
            float2 center = float2.zero, facingSum = float2.zero;
            for (int u = 0; u < c.Units.Length; u++)
            {
                if (!map.TryGetValue(c.Units[u], out Entity e)) continue;
                if (!em.HasComponent<MoveTarget>(e) || !em.HasComponent<AttackOrder>(e)) continue;
                if (em.HasComponent<Immobile>(e)) continue;              // buildings ignore movement orders
                if (!em.HasComponent<FormationMember>(e)) continue;      // only formation units take a slot
                var xf = em.GetComponentData<LocalTransform>(e);
                float3 f3 = math.forward(xf.Rotation);
                ents.Add(e);
                center += new float2(xf.Position.x, xf.Position.z);
                facingSum += new float2(f3.x, f3.z);
            }
            int n = ents.Length;
            if (n == 0) { ents.Dispose(); continue; }
            center /= n;

            // Unique id per ORDER, not per membership. Using a monotonic counter
            // means re-ordering a subset always produces a fresh id, so the units
            // left behind immediately stop being considered part of the new group.
            // 0 is the ungrouped sentinel (MoveTarget default); the counter starts
            // at 1 and only increments, so it never collides with 0.
            var seq = em.GetComponentData<FieldIdSeq>(qe);
            int formationId = seq.Next;
            seq.Next++;
            em.SetComponentData(qe, seq);

            // Initial forward + the live anchor's starting point (the group center).
            // FormationSystem advances/pivots both from here; CommandSystem never
            // touches them again.
            float2 avgFacing = math.normalizesafe(facingSum, new float2(0f, 1f));
            float2 aim = c.TargetPos;
            if (c.Kind == CommandKind.AttackTarget && atkTarget != Entity.Null)
            {
                var tx = em.GetComponentData<LocalTransform>(atkTarget).Position;
                aim = new float2(tx.x, tx.z);
            }
            float2 fwd = c.Kind == CommandKind.Stop ? avgFacing
                                                    : math.normalizesafe(aim - center, avgFacing);

            // Shape + width are ORDER properties. Width comes from the command
            // (right-click-drag length, set in Commander). 0 => auto-fit.
            // TODO(integration): set `width = c.FormationWidth;` once SimCommand
            // carries it (see FORMATION_INTEGRATION.md, imp2). Defaults keep Grid.
            FormationShape shape = FormationShape.Grid;
            int width = 0;
            int cols = width > 0 ? math.min(width, n) : FormationGeometry.Cols(shape, n);

            // ---- stamp the initial frame; FormationSystem owns it from here ----
            for (int a = 0; a < n; a++)
            {
                Entity e = ents[a];
                MoveTarget mv  = em.GetComponentData<MoveTarget>(e);
                AttackOrder ao = em.GetComponentData<AttackOrder>(e);
                switch (c.Kind)
                {
                    case CommandKind.Move:        mv.Value = c.TargetPos; mv.HasTarget = true;  mv.AttackMove = false; ao.Has = false; break;
                    case CommandKind.AttackMove:  mv.Value = c.TargetPos; mv.HasTarget = true;  mv.AttackMove = true;  ao.Has = false; break;
                    case CommandKind.Stop:        mv.Value = center;      mv.HasTarget = false; ao.Has = false; break;
                    case CommandKind.AttackTarget:mv.Value = aim;         mv.HasTarget = false; ao.Target = atkTarget; ao.Has = atkTarget != Entity.Null; break;
                }
                mv.Forward     = fwd;
                mv.Anchor      = center;     // live formation center; FormationSystem advances it
                mv.FormationId = formationId;
                mv.Cols        = cols;
                mv.Shape       = (byte)shape;
                em.SetComponentData(e, mv);
                em.SetComponentData(e, ao);
            }

            ents.Dispose();
        }

        due.Dispose();
    }

    // Spawns a building at the command's execution tick, with validation done
    // HERE — not at issue time — so every peer accepts or rejects identically
    // from identical sim state. Any client-side check (placement preview) is
    // advisory only. Reads ObstacleField.Passable as-is: depending on system
    // sort order it may be one tick stale, but it is the SAME staleness on
    // every peer, so the decision stays deterministic.
    //
    // Rules (per the footprint's non-corner cells): in grid bounds, currently
    // passable (covers obstacles AND slope-blocked cells), and terrain height
    // spread <= the definition's maxHeightDelta. Y = the highest sampled cell
    // height — the model's basement skirt covers the lower side.
    private void ApplyPlaceBuilding(in SimCommand c, NativeArray<byte> cellType,
                                    bool hasTerrain, in TerrainHeightField terrain)
    {
        var um = UnitManager.Instance;
        if (um == null) { Debug.LogWarning("[Building] placement dropped: no UnitManager in the scene."); return; }

        int team = c.PlayerId;
        var def = um.GetDefinition(team, c.TargetStableId) as BuildingDefinition;
        if (def == null)
        {
            Debug.LogWarning($"[Building] placement dropped: roster def {c.TargetStableId} (team {team}) is not a BuildingDefinition.");
            return;
        }

        int2 extents = new int2(math.max(1, def.footprintX), math.max(1, def.footprintZ));
        bool cutCorners = !(def is WallDefinition);   // walls are solid rectangles
        var verdict = BuildingFootprint.ValidatePlacement(c.TargetPos, extents, def.maxHeightDelta,
                                                          cellType, hasTerrain, terrain, cutCorners, out float3 spawnPos);
        if (verdict != PlacementVerdict.Ok)
        {
            // Identical verdict on every peer (validation is pure sim state),
            // so logging here is lockstep-safe. Loud on purpose: a silent
            // reject is indistinguishable from a dropped command.
            Debug.Log($"[Building] placement rejected at ({c.TargetPos.x:0.#},{c.TargetPos.y:0.#}): {verdict} " +
                      $"(footprint {extents.x}x{extents.y}, maxHeightDelta {def.maxHeightDelta}).");
            return;
        }

        um.SpawnUnit(def, c.TargetStableId, team, spawnPos);
    }

    // Spawns the AbilityField entity for an Ability command, exactly like the old
    // HeroController.TryCast — but on a deterministic tick, with tick-based
    // cooldowns, from the AbilityManager's baked spec.
    // COMMIT — the gate. All checks happen here, at the execution tick, from
    // sim state, so every peer reaches the same verdict; costs are consumed
    // only when EVERY check passes (an over-range or unaffordable cast fizzles
    // with nothing spent). On success the cooldown starts and a PendingCast is
    // armed; AbilityCastSystem fires it ChargeUpTicks later (same tick for 0).
    //
    // Checks, in order: caster alive & able, slot registered, not already
    // charging, off cooldown, mana, commander resources, cast range
    // (WorldPoint), and — for spawn abilities — that the spawn point is valid
    // (building footprint rule, or a passable cell for units) and the spawned
    // definition is in the team roster.
    private void CommitAbility(ref SystemState state, in SimCommand c, uint tick,
                               NativeParallelHashMap<int, Entity> map,
                               bool hasGrid, NativeArray<byte> passable, NativeArray<byte> cellType,
                               bool hasTerrain, in TerrainHeightField terrain,
                               Entity resourceEntity)
    {
        var em = state.EntityManager;
        var mgr = AbilityManager.Instance;
        if (mgr == null) { Debug.LogWarning("[Ability] cast dropped: no AbilityManager in the scene."); return; }
        if (c.Units.Length == 0) { Debug.LogWarning("[Ability] cast dropped: command carried no caster id."); return; }
        if (!map.TryGetValue(c.Units[0], out Entity caster)) return;        // caster died before execution — normal, silent
        if (!em.HasComponent<AbilitySlots>(caster) || !em.HasComponent<AbilityCooldowns>(caster) ||
            !em.HasComponent<PendingCast>(caster) || !em.HasComponent<Mana>(caster))
        { Debug.LogWarning($"[Ability] cast dropped: caster (StableId {c.Units[0]}) is missing ability components."); return; }

        int slot = c.AbilitySlot;
        if (slot < 0 || slot > 3) { Debug.LogWarning($"[Ability] cast dropped: bad slot {slot}."); return; }

        var slots = em.GetComponentData<AbilitySlots>(caster);
        int abilityId = slots.Ids[slot];
        if (abilityId < 0 || !mgr.TryGetSpec(abilityId, out var spec))
        { Debug.LogWarning($"[Ability] cast dropped: slot {slot} has no registered ability (id {abilityId})."); return; }

        var pending = em.GetComponentData<PendingCast>(caster);
        if (pending.Active != 0) return;                                    // already charging a cast — silent

        var cds = em.GetComponentData<AbilityCooldowns>(caster);
        if (cds.ReadyTick[slot] > tick) return;                             // still cooling down — normal, silent (HUD shows it)

        var mana = em.GetComponentData<Mana>(caster);
        if (mana.Current < spec.ManaCost) return;                           // can't afford — fizzle, nothing consumed

        int team = em.HasComponent<Team>(caster) ? em.GetComponentData<Team>(caster).Value : c.PlayerId;

        // Commander resources: check all three before consuming any.
        bool hasResources = resourceEntity != Entity.Null && em.HasBuffer<TeamResources>(resourceEntity);
        if (math.any(spec.Cost > 0))
        {
            if (!hasResources) return;                                      // no pool in the scene -> nothing to pay from
            var pool = em.GetBuffer<TeamResources>(resourceEntity);
            if (team < 0 || team >= pool.Length) return;
            if (math.any(pool[team].Amounts < spec.Cost)) return;           // can't afford — fizzle, nothing consumed
        }

        // Cast range: WorldPoint casts farther than CastRange from the caster fizzle.
        var xf = em.GetComponentData<LocalTransform>(caster);
        float2 casterPos = new float2(xf.Position.x, xf.Position.z);
        if (spec.Anchor == AnchorType.WorldPoint && spec.CastRange > 0f &&
            math.distance(casterPos, c.TargetPos) > spec.CastRange)
            return;

        // Spawn abilities: the spawn point must be valid NOW (a charge-up can
        // still be beaten to the spot — fire spawns unconditionally; that
        // window is the same accepted overlap as two same-tick placements).
        if (spec.HasSpawn != 0)
        {
            var um = UnitManager.Instance;
            var sdef = mgr.GetDefinition(abilityId) != null ? mgr.GetDefinition(abilityId).spawnUnit : null;
            if (um == null || sdef == null || um.GetDefId(team, sdef) < 0)
            {
                Debug.LogWarning($"[Ability] cast dropped: spawn def missing or not in team {team} roster.");
                return;
            }
            if (!hasGrid) return;
            if (sdef is BuildingDefinition bdef)
            {
                int2 extents = new int2(math.max(1, bdef.footprintX), math.max(1, bdef.footprintZ));
                bool cutCorners = !(bdef is WallDefinition);
                var verdict = BuildingFootprint.ValidatePlacement(c.TargetPos, extents, bdef.maxHeightDelta,
                                                                  cellType, hasTerrain, terrain, cutCorners, out _);
                if (verdict != PlacementVerdict.Ok)
                {
                    Debug.Log($"[Ability] spawn cast fizzled at ({c.TargetPos.x:0.#},{c.TargetPos.y:0.#}): {verdict}. Nothing consumed.");
                    return;
                }
            }
            else
            {
                int2 cell = NavGrid.Cell(c.TargetPos);
                if (!NavGrid.InBounds(cell.x, cell.y) || passable[NavGrid.Index(cell)] == 0)
                {
                    Debug.Log($"[Ability] spawn cast fizzled at ({c.TargetPos.x:0.#},{c.TargetPos.y:0.#}): cell blocked or off grid. Nothing consumed.");
                    return;
                }
            }
        }

        // ---- every gate passed: consume and arm -------------------------------
        mana.Current -= spec.ManaCost;
        em.SetComponentData(caster, mana);

        if (math.any(spec.Cost > 0))
        {
            var pool = em.GetBuffer<TeamResources>(resourceEntity);
            var tr = pool[team];
            tr.Amounts -= spec.Cost;
            pool[team] = tr;
        }

        cds.ReadyTick[slot] = tick + spec.ChargeUpTicks + spec.CooldownTicks;   // cooldown runs from the fire tick
        em.SetComponentData(caster, cds);

        em.SetComponentData(caster, new PendingCast
        {
            Active = 1,
            Slot = (byte)slot,
            AbilityId = abilityId,
            FireTick = tick + spec.ChargeUpTicks,
            TargetPos = c.TargetPos,
        });
    }
}
