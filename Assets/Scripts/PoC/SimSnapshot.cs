using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// SimSnapshot — Phase 4. Serializes the COMPLETE simulation state to a byte
// blob and rebuilds a world from one. This single mechanism is game start,
// late baseline join, save/load, and desync recovery: in network mode a sim
// world only ever comes into existence by restoring a snapshot.
//
// WHY EVERY PEER (HOST INCLUDED) RESTORES, NEVER JUST THE DESYNCED ONE:
// the sim is iteration-order-sensitive (see the Selected comment in
// Components.cs — chunk layout perturbs float summation order in neighbor
// loops). Peers stay in sync today because they perform identical structural
// operations in identical tick order, growing identical chunk layouts. A
// restore rebuilds entities in canonical order (units sorted by StableId,
// projectiles/fields in blob order), which produces a DIFFERENT layout than
// any organically-grown world — so the only consistent option is that every
// peer, host included, rebuilds from the same blob. After that, all layouts
// are identical again and stay identical.
//
// WHAT IS SERIALIZED: every component that is genuine sim state — including
// values that look derived (live Speed/Attack stats) because redundancy is
// bytes, not bugs. Entity references (AttackOrder.Target, AbilityField
// .AnchorEntity) travel as StableIds and are fixed up in a second pass.
//
// WHAT IS DELIBERATELY NOT SERIALIZED (derived, rebuilt next tick from the
// restored entities): SpatialHash, Perception, UnitInfo/FriendlyUnit buffers,
// CombatTarget (BehaviorSystem reselects from fresh perception — identical on
// every peer because every peer restores identically), the obstacle grid
// (ObstacleGridSystem re-rasterizes every tick), and all flow-field caches
// (pure functions of passability + goals). The pending SimCommand buffer is
// empty between ticks in network mode (turns are injected and consumed within
// the same tick), so it is cleared rather than serialized.
//
// Records use INetworkSerializeByMemcpy + FastBufferWriter — the same memcpy
// contract SimCommand already relies on — so there is no hand-written
// field-by-field serializer to drift when a component gains a field.
// (Consequence: blobs are NOT portable across builds whose struct layouts
// differ; the Version header guards save files.)
// ===========================================================================
public static class SimSnapshot
{
    private const uint Magic   = 0x48535031;   // "HSP1"
    private const int  Version = 1;

    public static string DefaultSavePath
        => Path.Combine(Application.persistentDataPath, "savegame.snap");

    // --- wire records --------------------------------------------------------

    private struct UnitRecord : INetworkSerializeByMemcpy
    {
        public int  StableId, Team, DefId;
        public byte IsDead;

        public LocalTransform        Xf;
        public UnitTuning            Tuning;
        public Attack                Attack;
        public Defense               Defense;
        public Speed                 Speed;
        public UnitRadius            Radius;
        public Mass                  Mass;
        public Velocity              Vel;
        public KnockbackVelocity     Knockback;   // persistent: accumulated on contact, decayed by SteeringSystem across ticks
        public NavContext            Nav;
        public GroundSpeedMultiplier Ground;
        public MoveTarget            Move;
        public AttackOrder           AO;          // raw Entity inside is meaningless on the wire...
        public int                   AOTargetSid; // ...this is the real reference (-1 = none)
        public DesiredDestination    Desired;
        public Health                Hp;
        public Mana                  Mana;
        public DeathTimer            Death;
        public Ranged                Ranged;
        public UnitAnim              Anim;
        public CombatStatus          Combat;
        public BaseStats             Base;
        public PendingCast           Pending;
        public AbilitySlots          Slots;
        public AbilityCooldowns      Cds;

        public int ModCount;                      // ActiveModifier entries follow this record
    }

    private struct ModRecord : INetworkSerializeByMemcpy { public ActiveModifier M; }
    private struct FieldModRecord : INetworkSerializeByMemcpy { public FieldModifier M; }

    private struct ProjectileRecord : INetworkSerializeByMemcpy
    {
        public LocalTransform Xf;
        public Projectile     P;
        public int            ViewId;
    }

    private struct FieldRecord : INetworkSerializeByMemcpy
    {
        public AbilityField F;          // AnchorEntity inside is meaningless on the wire...
        public int          AnchorSid;  // ...this is the real reference (-1 = none)
        public int          ModCount;   // FieldModifier entries follow this record
    }

    // --- capture --------------------------------------------------------------

    // Serializes the full sim state. Call from the main thread between ticks
    // (any MonoBehaviour Update / message handler qualifies — EntityManager
    // access completes in-flight jobs).
    public static byte[] Capture(World world)
    {
        var em = world.EntityManager;

        uint tick = 0;
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<SimClock>()))
            if (q.HasSingleton<SimClock>()) tick = q.GetSingleton<SimClock>().Tick;

        int fieldNext = 1;
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<FieldIdSeq>()))
            if (q.HasSingleton<FieldIdSeq>()) fieldNext = q.GetSingleton<FieldIdSeq>().Next;

        int nextStableId = UnitManager.Instance != null ? UnitManager.Instance.NextStableId : 0;

        // Team resources.
        var resources = new List<int3>();
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<ResourcePoolTag>()))
            if (q.HasSingleton<ResourcePoolTag>())
            {
                var buf = em.GetBuffer<TeamResources>(q.GetSingletonEntity());
                for (int i = 0; i < buf.Length; i++) resources.Add(buf[i].Amounts);
            }

        // Units, sorted by StableId — the canonical rebuild order.
        var units = new List<(int sid, Entity e)>();
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<UnitTag>(), ComponentType.ReadOnly<StableId>()))
        {
            var ents = q.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
                units.Add((em.GetComponentData<StableId>(ents[i]).Value, ents[i]));
            ents.Dispose();
        }
        units.Sort((a, b) => a.sid.CompareTo(b.sid));

        // Projectiles: no stable identity needed — the blob's order IS the
        // canonical order, because every peer (host included) rebuilds from
        // the same blob.
        Entity[] projectiles;
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<ProjectileTag>(),
                                            ComponentType.ReadOnly<Projectile>(),
                                            ComponentType.ReadOnly<LocalTransform>(),
                                            ComponentType.ReadOnly<ProjectileView>()))
        {
            var ents = q.ToEntityArray(Allocator.Temp);
            projectiles = ents.ToArray();
            ents.Dispose();
        }

        // Ability fields, sorted by FieldId (deterministic ids from FieldIdSeq).
        var fields = new List<(int fid, Entity e)>();
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<AbilityField>()))
        {
            var ents = q.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
                fields.Add((em.GetComponentData<AbilityField>(ents[i]).FieldId, ents[i]));
            ents.Dispose();
        }
        fields.Sort((a, b) => a.fid.CompareTo(b.fid));

        using var w = new FastBufferWriter(256 * 1024, Allocator.Persistent, 64 * 1024 * 1024);

        w.WriteValueSafe(Magic);
        w.WriteValueSafe(Version);
        w.WriteValueSafe(tick);
        w.WriteValueSafe(nextStableId);
        w.WriteValueSafe(fieldNext);
        w.WriteValueSafe(ComputeStateHash(em));     // self-verification target for every restore
        w.WriteValueSafe(resources.Count);
        w.WriteValueSafe(units.Count);
        w.WriteValueSafe(projectiles.Length);
        w.WriteValueSafe(fields.Count);

        foreach (var r in resources)
        {
            w.WriteValueSafe(r.x); w.WriteValueSafe(r.y); w.WriteValueSafe(r.z);
        }

        foreach (var (sid, e) in units)
        {
            // AttackOrder: translate the Entity target to a StableId; a target
            // that no longer resolves drops the order (the SAME verdict lands
            // on every peer because everyone restores from this one capture).
            var ao = em.GetComponentData<AttackOrder>(e);
            int aoSid = -1;
            if (ao.Has && ao.Target != Entity.Null && em.Exists(ao.Target) && em.HasComponent<StableId>(ao.Target))
                aoSid = em.GetComponentData<StableId>(ao.Target).Value;
            if (aoSid < 0) { ao.Has = false; ao.Target = Entity.Null; }

            var rec = new UnitRecord
            {
                StableId = sid,
                Team     = em.GetComponentData<Team>(e).Value,
                DefId    = em.GetComponentData<UnitDefId>(e).Value,
                IsDead   = em.HasComponent<Dead>(e) ? (byte)1 : (byte)0,

                Xf       = em.GetComponentData<LocalTransform>(e),
                Tuning   = em.GetComponentData<UnitTuning>(e),
                Attack   = em.GetComponentData<Attack>(e),
                Defense  = em.GetComponentData<Defense>(e),
                Speed    = em.GetComponentData<Speed>(e),
                Radius   = em.GetComponentData<UnitRadius>(e),
                Mass     = em.GetComponentData<Mass>(e),
                Vel      = em.GetComponentData<Velocity>(e),
                Knockback = em.GetComponentData<KnockbackVelocity>(e),
                Nav      = em.GetComponentData<NavContext>(e),
                Ground   = em.GetComponentData<GroundSpeedMultiplier>(e),
                Move     = em.GetComponentData<MoveTarget>(e),
                AO       = ao,
                AOTargetSid = aoSid,
                Desired  = em.GetComponentData<DesiredDestination>(e),
                Hp       = em.GetComponentData<Health>(e),
                Mana     = em.GetComponentData<Mana>(e),
                Death    = em.GetComponentData<DeathTimer>(e),
                Ranged   = em.GetComponentData<Ranged>(e),
                Anim     = em.GetComponentData<UnitAnim>(e),
                Combat   = em.GetComponentData<CombatStatus>(e),
                Base     = em.GetComponentData<BaseStats>(e),
                Pending  = em.GetComponentData<PendingCast>(e),
                Slots    = em.GetComponentData<AbilitySlots>(e),
                Cds      = em.GetComponentData<AbilityCooldowns>(e),
            };

            var mods = em.GetBuffer<ActiveModifier>(e);
            rec.ModCount = mods.Length;
            w.WriteValueSafe(rec);
            for (int i = 0; i < mods.Length; i++) w.WriteValueSafe(new ModRecord { M = mods[i] });
        }

        foreach (var e in projectiles)
        {
            w.WriteValueSafe(new ProjectileRecord
            {
                Xf     = em.GetComponentData<LocalTransform>(e),
                P      = em.GetComponentData<Projectile>(e),
                ViewId = em.GetComponentData<ProjectileView>(e).Id,
            });
        }

        foreach (var (fid, e) in fields)
        {
            var f = em.GetComponentData<AbilityField>(e);
            int anchorSid = -1;
            if (f.AnchorEntity != Entity.Null && em.Exists(f.AnchorEntity) && em.HasComponent<StableId>(f.AnchorEntity))
                anchorSid = em.GetComponentData<StableId>(f.AnchorEntity).Value;

            var fmods = em.GetBuffer<FieldModifier>(e);
            w.WriteValueSafe(new FieldRecord { F = f, AnchorSid = anchorSid, ModCount = fmods.Length });
            for (int i = 0; i < fmods.Length; i++)
                w.WriteValueSafe(new FieldModRecord { M = fmods[i] });
        }

        return w.ToArray();
    }

    // --- restore ----------------------------------------------------------------

    // Tears down all sim entities and rebuilds them from the blob, in canonical
    // order, then verifies the rebuilt state hashes to the captured hash.
    // Returns false only on a structural failure (bad blob, roster mismatch);
    // a hash mismatch is logged as an error but still reported through `hash`
    // so the host-side ack comparison surfaces it centrally.
    public static bool Restore(World world, byte[] data, out uint tick, out uint hash)
    {
        tick = 0; hash = 0;
        var em = world.EntityManager;
        var um = UnitManager.Instance;
        if (um == null) { Debug.LogError("[Snapshot] restore failed: no UnitManager in the scene."); return false; }
        if (data == null || data.Length < 40) { Debug.LogError("[Snapshot] restore failed: empty/short blob."); return false; }

        using var r = new FastBufferReader(data, Allocator.Temp);

        r.ReadValueSafe(out uint magic);
        r.ReadValueSafe(out int version);
        if (magic != Magic || version != Version)
        {
            Debug.LogError($"[Snapshot] restore failed: bad header (magic {magic:X8}, version {version}).");
            return false;
        }
        r.ReadValueSafe(out tick);
        r.ReadValueSafe(out int nextStableId);
        r.ReadValueSafe(out int fieldNext);
        r.ReadValueSafe(out uint srcHash);
        r.ReadValueSafe(out int resCount);
        r.ReadValueSafe(out int unitCount);
        r.ReadValueSafe(out int projCount);
        r.ReadValueSafe(out int fieldCount);

        var resources = new List<int3>(resCount);
        for (int i = 0; i < resCount; i++)
        {
            r.ReadValueSafe(out int x); r.ReadValueSafe(out int y); r.ReadValueSafe(out int z);
            resources.Add(new int3(x, y, z));
        }

        // Local selection is the one piece of LOCAL state worth carrying across
        // the rebuild (it never affects the sim or the hash — Selected is an
        // enableable bit read only by the local commander).
        var selectedSids = new List<int>();
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<Selected>(), ComponentType.ReadOnly<StableId>()))
        {
            var sel = q.ToComponentDataArray<StableId>(Allocator.Temp);
            for (int i = 0; i < sel.Length; i++) selectedSids.Add(sel[i].Value);
            sel.Dispose();
        }

        // --- teardown: every sim-owned entity goes -------------------------------
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<UnitTag>()))        em.DestroyEntity(q);
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<ProjectileTag>()))  em.DestroyEntity(q);
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<AbilityField>()))   em.DestroyEntity(q);

        // --- singletons -----------------------------------------------------------
        using (var q = em.CreateEntityQuery(typeof(SimClock)))
            if (q.HasSingleton<SimClock>())
                em.SetComponentData(q.GetSingletonEntity(), new SimClock { Tick = tick });
        SimClockSystem.LastCompletedTick = tick;

        using (var q = em.CreateEntityQuery(typeof(CommandQueueTag)))
            if (q.HasSingleton<CommandQueueTag>())
            {
                var qe = q.GetSingletonEntity();
                em.GetBuffer<SimCommand>(qe).Clear();        // empty between ticks in network mode anyway
                em.GetBuffer<AbilityCastEvent>(qe).Clear();  // stale view events
                em.SetComponentData(qe, new FieldIdSeq { Next = fieldNext });
            }

        using (var q = em.CreateEntityQuery(typeof(ResourcePoolTag)))
            if (q.HasSingleton<ResourcePoolTag>())
            {
                var buf = em.GetBuffer<TeamResources>(q.GetSingletonEntity());
                buf.Clear();
                for (int i = 0; i < resources.Count; i++)
                    buf.Add(new TeamResources { Amounts = resources[i] });
            }

        // --- units, in blob order (= sorted by StableId at capture) ---------------
        // SpawnUnit gives the canonical creation sequence (same archetype, same
        // conditional adds, idempotent ability registration); the record then
        // overwrites every component with the captured bit patterns.
        var sidToEntity = new Dictionary<int, Entity>(unitCount);
        var aoFixups    = new List<(Entity e, AttackOrder ao, int sid)>(unitCount);

        for (int u = 0; u < unitCount; u++)
        {
            r.ReadValueSafe(out UnitRecord rec);

            var def = um.GetDefinition(rec.Team, rec.DefId);
            if (def == null)
            {
                Debug.LogError($"[Snapshot] restore failed: no definition for team {rec.Team} defId {rec.DefId} " +
                               "— rosters differ between this peer and the snapshot's author.");
                return false;
            }

            var e = um.SpawnUnit(def, rec.DefId, rec.Team, rec.Xf.Position);
            if (rec.IsDead != 0) em.AddComponent<Dead>(e);   // structural — do before component writes

            em.SetComponentData(e, new StableId { Value = rec.StableId });
            em.SetComponentData(e, rec.Xf);
            em.SetComponentData(e, rec.Tuning);
            em.SetComponentData(e, rec.Attack);
            em.SetComponentData(e, rec.Defense);
            em.SetComponentData(e, rec.Speed);
            em.SetComponentData(e, rec.Radius);
            em.SetComponentData(e, rec.Mass);
            em.SetComponentData(e, rec.Vel);
            em.SetComponentData(e, rec.Knockback);
            em.SetComponentData(e, rec.Nav);
            em.SetComponentData(e, rec.Ground);
            em.SetComponentData(e, rec.Move);
            em.SetComponentData(e, rec.Desired);
            em.SetComponentData(e, rec.Hp);
            em.SetComponentData(e, rec.Mana);
            em.SetComponentData(e, rec.Death);
            em.SetComponentData(e, rec.Ranged);
            em.SetComponentData(e, rec.Anim);
            em.SetComponentData(e, rec.Combat);
            em.SetComponentData(e, rec.Base);
            em.SetComponentData(e, rec.Pending);
            em.SetComponentData(e, rec.Slots);
            em.SetComponentData(e, rec.Cds);
            // CombatTarget / Perception / UnitInfo / FriendlyUnit stay at their
            // archetype defaults — derived per tick, rebuilt before any consumer.

            var mods = em.GetBuffer<ActiveModifier>(e);
            for (int i = 0; i < rec.ModCount; i++)
            {
                r.ReadValueSafe(out ModRecord mr);
                mods.Add(mr.M);
            }

            sidToEntity[rec.StableId] = e;
            aoFixups.Add((e, rec.AO, rec.AOTargetSid));
        }

        // Pass 2: entity-reference fixups, now that every StableId resolves.
        foreach (var (e, aoIn, sid) in aoFixups)
        {
            var ao = aoIn;
            if (sid >= 0 && sidToEntity.TryGetValue(sid, out var target)) ao.Target = target;
            else { ao.Target = Entity.Null; ao.Has = false; }
            em.SetComponentData(e, ao);
        }

        // --- projectiles ------------------------------------------------------------
        var projArch = em.CreateArchetype(typeof(LocalTransform), typeof(Projectile),
                                          typeof(ProjectileTag), typeof(ProjectileView));
        for (int i = 0; i < projCount; i++)
        {
            r.ReadValueSafe(out ProjectileRecord pr);
            var pe = em.CreateEntity(projArch);
            em.SetComponentData(pe, pr.Xf);
            em.SetComponentData(pe, pr.P);
            em.SetComponentData(pe, new ProjectileView { Id = pr.ViewId });
        }

        // --- ability fields -----------------------------------------------------------
        for (int i = 0; i < fieldCount; i++)
        {
            r.ReadValueSafe(out FieldRecord fr);
            var f = fr.F;
            f.AnchorEntity = (fr.AnchorSid >= 0 && sidToEntity.TryGetValue(fr.AnchorSid, out var anchor))
                ? anchor : Entity.Null;

            var fe = em.CreateEntity();
            em.AddComponentData(fe, f);
            var fmods = em.AddBuffer<FieldModifier>(fe);
            for (int m = 0; m < fr.ModCount; m++)
            {
                r.ReadValueSafe(out FieldModRecord fmr);
                fmods.Add(fmr.M);
            }
        }

        um.NextStableId = nextStableId;

        // Re-apply local selection (best effort — selected units may be gone).
        foreach (var sid in selectedSids)
            if (sidToEntity.TryGetValue(sid, out var e))
                em.SetComponentEnabled<Selected>(e, true);

        // Pre-restore checksums describe a dead timeline.
        ChecksumHistory.Clear();

        // Self-verification: the rebuilt state must hash to exactly what was
        // captured. A mismatch here is a serialization bug (a missed component,
        // a formula drift), never a network problem.
        hash = ComputeStateHash(em);
        if (hash != srcHash)
            Debug.LogError($"[Snapshot] restored state hash {hash:X8} != captured hash {srcHash:X8} " +
                           "— the snapshot serializer is missing state. This must be root-caused.");

        Debug.Log($"[Snapshot] restored tick {tick}: {unitCount} units, {projCount} projectiles, " +
                  $"{fieldCount} fields (hash {hash:X8}).");
        return true;
    }

    // --- shared helpers -------------------------------------------------------------

    // Managed twin of SimChecksumSystem — same query, same per-unit formula
    // (LockstepHash.Unit), same commutative sum. Used to verify a restore and
    // to stamp the capture header.
    public static uint ComputeStateHash(EntityManager em)
    {
        using var q = em.CreateEntityQuery(
            ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<Health>(),
            ComponentType.ReadOnly<Velocity>(), ComponentType.ReadOnly<Team>(),
            ComponentType.ReadOnly<StableId>(), ComponentType.ReadOnly<NavContext>());

        var xf = q.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var hp = q.ToComponentDataArray<Health>(Allocator.Temp);
        var v  = q.ToComponentDataArray<Velocity>(Allocator.Temp);
        var t  = q.ToComponentDataArray<Team>(Allocator.Temp);
        var s  = q.ToComponentDataArray<StableId>(Allocator.Temp);
        var n  = q.ToComponentDataArray<NavContext>(Allocator.Temp);

        uint sum = 0;
        for (int i = 0; i < xf.Length; i++)
            sum += LockstepHash.Unit(xf[i].Position, hp[i].Current, v[i].Value,
                                     t[i].Value, s[i].Value, n[i].Value);

        xf.Dispose(); hp.Dispose(); v.Dispose(); t.Dispose(); s.Dispose(); n.Dispose();
        return sum;
    }

    public static void SaveToFile(World world)
    {
        var data = Capture(world);
        File.WriteAllBytes(DefaultSavePath, data);
        Debug.Log($"[Snapshot] saved {data.Length} bytes to {DefaultSavePath}");
    }

    public static byte[] LoadFile()
        => File.Exists(DefaultSavePath) ? File.ReadAllBytes(DefaultSavePath) : null;
}
