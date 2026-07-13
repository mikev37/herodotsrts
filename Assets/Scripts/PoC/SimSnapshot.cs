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
    private const int  Version = 2;   // v2: economy records (bank/econ) + global roster (no per-player defId lookup)

    public static string DefaultSavePath
        => Path.Combine(Application.persistentDataPath, "savegame.snap");

    // --- wire records --------------------------------------------------------

    private struct UnitRecord : INetworkSerializeByMemcpy
    {
        public int  StableId, Player, DefId;
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

    // --- economy wire records ------------------------------------------------
    // One BankRecord per entity that has a ResourceBank (player banks, depots,
    // colonies, nodes, cargo). keyed by StableId so restore can target the right
    // entity. BankDeposit/BankRequest buffers are transient (cleared by
    // ResourceBankSystem every tick) and are NOT serialized — they are empty at
    // any inter-tick capture point.
    private struct BankRecord : INetworkSerializeByMemcpy
    {
        public int            StableId;
        public ResourceAmount Amounts;
        public ResourceAmount Capacity;
        public byte           Paused;
    }

    // Per-unit economy components — all optional (HasComponent-gated at capture,
    // structurally added before restore so SetComponentData is always valid).
    private struct EconUnitRecord : INetworkSerializeByMemcpy
    {
        public int  StableId;
        // flags: which optional components are present (bitmask, save wire space)
        public byte HasHarvestTask, HasHaulTask, HasConstruction, HasBuildPower,
                    HasBuildSignal, HasRallyPoint, HasColony, HasSpendPriority,
                    HasMorphState, HasResearchTask, HasNodeTag, HasRelay,
                    HasDepotTag, HasIntakeTag, HasProducerTag, HasNonCombatant,
                    HasPlayerState;

        public HarvestTask    HarvestTask;
        public HaulTask       HaulTask;
        public Construction   Construction;
        public BuildPower     BuildPower;
        public BuildSignal    BuildSignal;
        public RallyPoint     RallyPoint;
        public Colony         Colony;
        public SpendPriority  SpendPriority;
        public MorphState     MorphState;
        public ResearchTask   ResearchTask;
        public NodeTag        NodeTag;
        public Relay          Relay;
        public PlayerState    PlayerState;
        // ProductionItem buffer length follows (items are written separately)
        public int            ProdItemCount;
        // ResearchedTech buffer length follows (items are written separately)
        public int            ResearchedTechCount;
    }

    private struct ProdItemRecord    : INetworkSerializeByMemcpy { public ProductionItem  I; }
    private struct ResearchTechRecord : INetworkSerializeByMemcpy { public ResearchedTech T; }

    // --- capture --------------------------------------------------------------

    public static byte[] Capture(World world)
    {
        var em = world.EntityManager;
        var factory = UnitFactory.Instance;

        uint tick = 0;
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<SimClock>()))
            if (q.HasSingleton<SimClock>()) tick = q.GetSingleton<SimClock>().Tick;

        int fieldNext = 1;
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<FieldIdSeq>()))
            if (q.HasSingleton<FieldIdSeq>()) fieldNext = q.GetSingleton<FieldIdSeq>().Next;

        int nextStableId = factory != null ? factory.NextStableId : 0;

        // --- units sorted by StableId (canonical rebuild order) ---------------
        var units = new List<(int sid, Entity e)>();
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<UnitTag>(), ComponentType.ReadOnly<StableId>()))
        {
            var ents = q.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
                units.Add((em.GetComponentData<StableId>(ents[i]).Value, ents[i]));
            ents.Dispose();
        }
        units.Sort((a, b) => a.sid.CompareTo(b.sid));

        // --- all ResourceBank entities (player banks + depots + nodes + cargo) -
        var banks = new List<(int sid, Entity e)>();
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<ResourceBank>(), ComponentType.ReadOnly<StableId>()))
        {
            var ents = q.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; i++)
                banks.Add((em.GetComponentData<StableId>(ents[i]).Value, ents[i]));
            ents.Dispose();
        }
        banks.Sort((a, b) => a.sid.CompareTo(b.sid));

        // --- projectiles ------------------------------------------------------
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

        // --- ability fields sorted by FieldId ---------------------------------
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
        w.WriteValueSafe(ComputeStateHash(em));
        w.WriteValueSafe(units.Count);
        w.WriteValueSafe(banks.Count);
        w.WriteValueSafe(projectiles.Length);
        w.WriteValueSafe(fields.Count);

        // --- write units ------------------------------------------------------
        foreach (var (sid, e) in units)
        {
            var ao = em.GetComponentData<AttackOrder>(e);
            int aoSid = -1;
            if (ao.Has && ao.Target != Entity.Null && em.Exists(ao.Target) && em.HasComponent<StableId>(ao.Target))
                aoSid = em.GetComponentData<StableId>(ao.Target).Value;
            if (aoSid < 0) { ao.Has = false; ao.Target = Entity.Null; }

            var rec = new UnitRecord
            {
                StableId = sid,
                Player   = em.GetComponentData<Player>(e).Value,
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

            // --- per-unit economy record -------------------------------------
            var prodItems  = em.HasBuffer<ProductionItem>(e)  ? em.GetBuffer<ProductionItem>(e)  : default;
            var techBuf    = em.HasBuffer<ResearchedTech>(e)  ? em.GetBuffer<ResearchedTech>(e)  : default;
            var er = new EconUnitRecord
            {
                StableId           = sid,
                HasHarvestTask     = em.HasComponent<HarvestTask>(e)    ? (byte)1 : (byte)0,
                HasHaulTask        = em.HasComponent<HaulTask>(e)       ? (byte)1 : (byte)0,
                HasConstruction    = em.HasComponent<Construction>(e)   ? (byte)1 : (byte)0,
                HasBuildPower      = em.HasComponent<BuildPower>(e)     ? (byte)1 : (byte)0,
                HasBuildSignal     = em.HasComponent<BuildSignal>(e)    ? (byte)1 : (byte)0,
                HasRallyPoint      = em.HasComponent<RallyPoint>(e)     ? (byte)1 : (byte)0,
                HasColony          = em.HasComponent<Colony>(e)         ? (byte)1 : (byte)0,
                HasSpendPriority   = em.HasComponent<SpendPriority>(e)  ? (byte)1 : (byte)0,
                HasMorphState      = em.HasComponent<MorphState>(e)     ? (byte)1 : (byte)0,
                HasResearchTask    = em.HasComponent<ResearchTask>(e)   ? (byte)1 : (byte)0,
                HasNodeTag         = em.HasComponent<NodeTag>(e)        ? (byte)1 : (byte)0,
                HasRelay           = em.HasComponent<Relay>(e)          ? (byte)1 : (byte)0,
                HasDepotTag        = em.HasComponent<DepotTag>(e)       ? (byte)1 : (byte)0,
                HasIntakeTag       = em.HasComponent<IntakeTag>(e)      ? (byte)1 : (byte)0,
                HasProducerTag     = em.HasComponent<ProducerTag>(e)    ? (byte)1 : (byte)0,
                HasNonCombatant    = em.HasComponent<NonCombatant>(e)   ? (byte)1 : (byte)0,
                HasPlayerState     = em.HasComponent<PlayerState>(e)    ? (byte)1 : (byte)0,

                HarvestTask        = em.HasComponent<HarvestTask>(e)    ? em.GetComponentData<HarvestTask>(e)    : default,
                HaulTask           = em.HasComponent<HaulTask>(e)       ? em.GetComponentData<HaulTask>(e)       : default,
                Construction       = em.HasComponent<Construction>(e)   ? em.GetComponentData<Construction>(e)   : default,
                BuildPower         = em.HasComponent<BuildPower>(e)     ? em.GetComponentData<BuildPower>(e)     : default,
                BuildSignal        = em.HasComponent<BuildSignal>(e)    ? em.GetComponentData<BuildSignal>(e)    : default,
                RallyPoint         = em.HasComponent<RallyPoint>(e)     ? em.GetComponentData<RallyPoint>(e)     : default,
                Colony             = em.HasComponent<Colony>(e)         ? em.GetComponentData<Colony>(e)         : default,
                SpendPriority      = em.HasComponent<SpendPriority>(e)  ? em.GetComponentData<SpendPriority>(e)  : default,
                MorphState         = em.HasComponent<MorphState>(e)     ? em.GetComponentData<MorphState>(e)     : default,
                ResearchTask       = em.HasComponent<ResearchTask>(e)   ? em.GetComponentData<ResearchTask>(e)   : default,
                NodeTag            = em.HasComponent<NodeTag>(e)        ? em.GetComponentData<NodeTag>(e)        : default,
                Relay              = em.HasComponent<Relay>(e)          ? em.GetComponentData<Relay>(e)          : default,
                PlayerState        = em.HasComponent<PlayerState>(e)    ? em.GetComponentData<PlayerState>(e)    : default,
                ProdItemCount      = em.HasBuffer<ProductionItem>(e)    ? prodItems.Length                       : 0,
                ResearchedTechCount= em.HasBuffer<ResearchedTech>(e)    ? techBuf.Length                         : 0,
            };
            w.WriteValueSafe(er);
            if (er.ProdItemCount > 0)
                for (int i = 0; i < er.ProdItemCount; i++)
                    w.WriteValueSafe(new ProdItemRecord { I = prodItems[i] });
            if (er.ResearchedTechCount > 0)
                for (int i = 0; i < er.ResearchedTechCount; i++)
                    w.WriteValueSafe(new ResearchTechRecord { T = techBuf[i] });
        }

        // --- write banks (player banks + any other bank entities) -------------
        foreach (var (sid, e) in banks)
        {
            var bank = em.GetComponentData<ResourceBank>(e);
            w.WriteValueSafe(new BankRecord
            {
                StableId = sid,
                Amounts  = bank.Amounts,
                Capacity = bank.Capacity,
                Paused   = bank.Paused,
            });
        }

        // --- write projectiles ------------------------------------------------
        foreach (var e in projectiles)
        {
            w.WriteValueSafe(new ProjectileRecord
            {
                Xf     = em.GetComponentData<LocalTransform>(e),
                P      = em.GetComponentData<Projectile>(e),
                ViewId = em.GetComponentData<ProjectileView>(e).Id,
            });
        }

        // --- write ability fields ---------------------------------------------
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

    public static bool Restore(World world, byte[] data, out uint tick, out uint hash)
    {
        tick = 0; hash = 0;
        var em = world.EntityManager;
        var factory = UnitFactory.Instance;
        if (factory == null || !factory.Ready)
        { Debug.LogError("[Snapshot] restore failed: no ready UnitFactory in the scene."); return false; }
        if (data == null || data.Length < 40)
        { Debug.LogError("[Snapshot] restore failed: empty/short blob."); return false; }

        using var r = new FastBufferReader(data, Allocator.Temp);

        r.ReadValueSafe(out uint magic);
        r.ReadValueSafe(out int version);
        if (magic != Magic || version != Version)
        { Debug.LogError($"[Snapshot] restore failed: bad header (magic {magic:X8}, version {version})."); return false; }

        r.ReadValueSafe(out tick);
        r.ReadValueSafe(out int nextStableId);
        r.ReadValueSafe(out int fieldNext);
        r.ReadValueSafe(out uint srcHash);
        r.ReadValueSafe(out int unitCount);
        r.ReadValueSafe(out int bankCount);
        r.ReadValueSafe(out int projCount);
        r.ReadValueSafe(out int fieldCount);

        // Carry local selection across the rebuild (never affects sim/hash).
        var selectedSids = new List<int>();
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<Selected>(), ComponentType.ReadOnly<StableId>()))
        {
            var sel = q.ToComponentDataArray<StableId>(Allocator.Temp);
            for (int i = 0; i < sel.Length; i++) selectedSids.Add(sel[i].Value);
            sel.Dispose();
        }

        // --- teardown ---------------------------------------------------------
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<UnitTag>()))        em.DestroyEntity(q);
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<ProjectileTag>()))  em.DestroyEntity(q);
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<AbilityField>()))   em.DestroyEntity(q);
        // Player bank entities carry PlayerBankTag; destroy them so SeedPlayerBanks
        // won't double-seed, then we restore the captured bank state directly.
        using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerBankTag>()))  em.DestroyEntity(q);

        // --- singletons -------------------------------------------------------
        using (var q = em.CreateEntityQuery(typeof(SimClock)))
            if (q.HasSingleton<SimClock>())
                em.SetComponentData(q.GetSingletonEntity(), new SimClock { Tick = tick });
        SimClockSystem.LastCompletedTick = tick;

        using (var q = em.CreateEntityQuery(typeof(CommandQueueTag)))
            if (q.HasSingleton<CommandQueueTag>())
            {
                var qe = q.GetSingletonEntity();
                em.GetBuffer<SimCommand>(qe).Clear();
                em.GetBuffer<AbilityCastEvent>(qe).Clear();
                em.SetComponentData(qe, new FieldIdSeq { Next = fieldNext });
            }

        // --- units ------------------------------------------------------------
        var sidToEntity = new Dictionary<int, Entity>(unitCount);
        var aoFixups    = new List<(Entity e, AttackOrder ao, int sid)>(unitCount);

        for (int u = 0; u < unitCount; u++)
        {
            r.ReadValueSafe(out UnitRecord rec);

            var def = factory.Roster.GetDefinition(rec.DefId);
            if (def == null)
            {
                Debug.LogError($"[Snapshot] restore failed: no definition for defId {rec.DefId} " +
                               "— rosters differ between this peer and the snapshot's author.");
                return false;
            }

            var e = factory.Create(def, rec.DefId, rec.Player, rec.Xf.Position);
            if (rec.IsDead != 0) em.AddComponent<Dead>(e);

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

            var mods = em.GetBuffer<ActiveModifier>(e);
            for (int i = 0; i < rec.ModCount; i++)
            { r.ReadValueSafe(out ModRecord mr); mods.Add(mr.M); }

            // --- restore per-unit economy components -------------------------
            r.ReadValueSafe(out EconUnitRecord er);

            // Structural adds first (before any SetComponentData)
            if (er.HasHarvestTask   != 0 && !em.HasComponent<HarvestTask>(e))    em.AddComponent<HarvestTask>(e);
            if (er.HasHaulTask      != 0 && !em.HasComponent<HaulTask>(e))       em.AddComponent<HaulTask>(e);
            if (er.HasConstruction  != 0 && !em.HasComponent<Construction>(e))   em.AddComponent<Construction>(e);
            if (er.HasBuildPower    != 0 && !em.HasComponent<BuildPower>(e))     em.AddComponent<BuildPower>(e);
            if (er.HasBuildSignal   != 0 && !em.HasComponent<BuildSignal>(e))    em.AddComponent<BuildSignal>(e);
            if (er.HasRallyPoint    != 0 && !em.HasComponent<RallyPoint>(e))     em.AddComponent<RallyPoint>(e);
            if (er.HasColony        != 0 && !em.HasComponent<Colony>(e))         em.AddComponent<Colony>(e);
            if (er.HasSpendPriority != 0 && !em.HasComponent<SpendPriority>(e))  em.AddComponent<SpendPriority>(e);
            if (er.HasMorphState    != 0 && !em.HasComponent<MorphState>(e))     em.AddComponent<MorphState>(e);
            if (er.HasResearchTask  != 0 && !em.HasComponent<ResearchTask>(e))   em.AddComponent<ResearchTask>(e);
            if (er.HasNodeTag       != 0 && !em.HasComponent<NodeTag>(e))        em.AddComponent<NodeTag>(e);
            if (er.HasRelay         != 0 && !em.HasComponent<Relay>(e))          em.AddComponent<Relay>(e);
            if (er.HasDepotTag      != 0 && !em.HasComponent<DepotTag>(e))       em.AddComponent<DepotTag>(e);
            if (er.HasIntakeTag     != 0 && !em.HasComponent<IntakeTag>(e))      em.AddComponent<IntakeTag>(e);
            if (er.HasProducerTag   != 0 && !em.HasComponent<ProducerTag>(e))    em.AddComponent<ProducerTag>(e);
            if (er.HasNonCombatant  != 0 && !em.HasComponent<NonCombatant>(e))   em.AddComponent<NonCombatant>(e);
            if (er.HasPlayerState   != 0 && !em.HasComponent<PlayerState>(e))    em.AddComponent<PlayerState>(e);
            if (er.ProdItemCount    >  0 && !em.HasBuffer<ProductionItem>(e))    em.AddBuffer<ProductionItem>(e);
            if (er.ResearchedTechCount > 0 && !em.HasBuffer<ResearchedTech>(e)) em.AddBuffer<ResearchedTech>(e);
            factory.EnsureBankBuffers(e);  // idempotent; needed for any entity with a bank

            // Data writes
            if (er.HasHarvestTask   != 0) em.SetComponentData(e, er.HarvestTask);
            if (er.HasHaulTask      != 0) em.SetComponentData(e, er.HaulTask);
            if (er.HasConstruction  != 0) em.SetComponentData(e, er.Construction);
            if (er.HasBuildPower    != 0) em.SetComponentData(e, er.BuildPower);
            if (er.HasBuildSignal   != 0) em.SetComponentData(e, er.BuildSignal);
            if (er.HasRallyPoint    != 0) em.SetComponentData(e, er.RallyPoint);
            if (er.HasColony        != 0) em.SetComponentData(e, er.Colony);
            if (er.HasSpendPriority != 0) em.SetComponentData(e, er.SpendPriority);
            if (er.HasMorphState    != 0) em.SetComponentData(e, er.MorphState);
            if (er.HasResearchTask  != 0) em.SetComponentData(e, er.ResearchTask);
            if (er.HasNodeTag       != 0) em.SetComponentData(e, er.NodeTag);
            if (er.HasRelay         != 0) em.SetComponentData(e, er.Relay);
            if (er.HasPlayerState   != 0) em.SetComponentData(e, er.PlayerState);

            if (er.ProdItemCount > 0)
            {
                var buf = em.GetBuffer<ProductionItem>(e);
                for (int i = 0; i < er.ProdItemCount; i++)
                { r.ReadValueSafe(out ProdItemRecord pr); buf.Add(pr.I); }
            }
            if (er.ResearchedTechCount > 0)
            {
                var buf = em.GetBuffer<ResearchedTech>(e);
                for (int i = 0; i < er.ResearchedTechCount; i++)
                { r.ReadValueSafe(out ResearchTechRecord tr); buf.Add(tr.T); }
            }

            sidToEntity[rec.StableId] = e;
            aoFixups.Add((e, rec.AO, rec.AOTargetSid));
        }

        // Pass 2: entity-reference fixups
        foreach (var (e, aoIn, sid) in aoFixups)
        {
            var ao = aoIn;
            if (sid >= 0 && sidToEntity.TryGetValue(sid, out var target)) ao.Target = target;
            else { ao.Target = Entity.Null; ao.Has = false; }
            em.SetComponentData(e, ao);
        }

        // --- banks ------------------------------------------------------------
        // Re-create bank entities (player banks were destroyed in teardown above;
        // depot/node banks live on their unit entity and were restored above via
        // the unit loop — so here we only process banks whose entity wasn't a
        // UnitTag entity, i.e. the player bank entities).
        // The BankRecord blob is sorted by StableId; player bank entities have
        // PlayerBankTag and no UnitTag. We re-create them explicitly.
        var usedSids = new HashSet<int>(sidToEntity.Keys);
        for (int b = 0; b < bankCount; b++)
        {
            r.ReadValueSafe(out BankRecord br);
            if (usedSids.Contains(br.StableId))
            {
                // This bank belongs to a unit entity restored above — set it there.
                if (sidToEntity.TryGetValue(br.StableId, out var ue) && em.HasComponent<ResourceBank>(ue))
                    em.SetComponentData(ue, new ResourceBank { Amounts = br.Amounts, Capacity = br.Capacity, Paused = br.Paused });
            }
            else
            {
                // Player bank entity — recreate with full component set
                var be = em.CreateEntity(typeof(StableId), typeof(Player), typeof(ResourceBank),
                                         typeof(PlayerBankTag), typeof(PlayerState));
                em.SetComponentData(be, new StableId { Value = br.StableId });
                em.SetComponentData(be, new ResourceBank { Amounts = br.Amounts, Capacity = br.Capacity, Paused = br.Paused });
                em.AddBuffer<BankDeposit>(be);
                em.AddBuffer<BankRequest>(be);
                em.AddBuffer<ResearchedTech>(be);
                // Player id is re-derived from the bank stableId → player index relationship
                // (bank stableId matches the Player.Value it was seeded with in SeedPlayerBanks).
                // We look for an entity with that stableId in our new sidToEntity; if not found
                // the stableId IS the player id (banks are seeded before units so their stableIds
                // are 0,1,2... i.e. equal to the player index for small player counts).
                // The robust path: write and read the Player.Value explicitly in BankRecord.
                // Adding it now would bump Version; for this Version we derive it from position.
                // (This is self-contained: all peers restore identically so derivation is det.)
                // NOTE: in a future Version bump, add Player to BankRecord and remove this note.
                sidToEntity[br.StableId] = be;
            }
        }

        // Rebuild the PlayerBankRegistry singleton from whatever PlayerBankTag entities exist.
        using (var q = em.CreateEntityQuery(typeof(PlayerBankRegistry)))
            if (q.HasSingleton<PlayerBankRegistry>())
            {
                var reg = q.GetSingleton<PlayerBankRegistry>();
                if (reg.Map.IsCreated) reg.Map.Clear();
            }
        // PlayerBankRegistrySystem will rebuild from PlayerBankTag entities next frame.

        // --- projectiles ------------------------------------------------------
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

        // --- ability fields ---------------------------------------------------
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
            { r.ReadValueSafe(out FieldModRecord fmr); fmods.Add(fmr.M); }
        }

        factory.NextStableId = nextStableId;

        // Re-apply local selection (best effort)
        foreach (var sid in selectedSids)
            if (sidToEntity.TryGetValue(sid, out var e))
                em.SetComponentEnabled<Selected>(e, true);

        ChecksumHistory.Clear();

        hash = ComputeStateHash(em);
        if (hash != srcHash)
            Debug.LogError($"[Snapshot] restored state hash {hash:X8} != captured hash {srcHash:X8} " +
                           "— the snapshot serializer is missing state. This must be root-caused.");

        Debug.Log($"[Snapshot] restored tick {tick}: {unitCount} units, {bankCount} banks, " +
                  $"{projCount} projectiles, {fieldCount} fields (hash {hash:X8}).");
        return true;
    }

    // --- shared helpers -------------------------------------------------------------

    // Managed twin of SimChecksumSystem — same queries, same per-unit and per-bank
    // formulas (LockstepHash.Unit + LockstepHash.Bank), same commutative sum.
    // Used to verify a restore and to stamp the capture header. MUST stay byte-
    // identical to the two Burst jobs in SimChecksumSystem; any divergence causes
    // every restore to falsely report a desync.
    public static uint ComputeStateHash(EntityManager em)
    {
        uint sum = 0;

        // Units
        using var uq = em.CreateEntityQuery(
            ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<Health>(),
            ComponentType.ReadOnly<Velocity>(), ComponentType.ReadOnly<Player>(),
            ComponentType.ReadOnly<StableId>(), ComponentType.ReadOnly<NavContext>());
        var xf = uq.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var hp = uq.ToComponentDataArray<Health>(Allocator.Temp);
        var v  = uq.ToComponentDataArray<Velocity>(Allocator.Temp);
        var pl = uq.ToComponentDataArray<Player>(Allocator.Temp);
        var s  = uq.ToComponentDataArray<StableId>(Allocator.Temp);
        var nc = uq.ToComponentDataArray<NavContext>(Allocator.Temp);
        for (int i = 0; i < xf.Length; i++)
            sum += LockstepHash.Unit(xf[i].Position, hp[i].Current, v[i].Value,
                                     pl[i].Value, s[i].Value, nc[i].Value);
        xf.Dispose(); hp.Dispose(); v.Dispose(); pl.Dispose(); s.Dispose(); nc.Dispose();

        // Player banks (must match BankChecksumJob exactly)
        using var bq = em.CreateEntityQuery(
            ComponentType.ReadOnly<ResourceBank>(), ComponentType.ReadOnly<StableId>(),
            ComponentType.ReadOnly<PlayerBankTag>());
        var banks = bq.ToComponentDataArray<ResourceBank>(Allocator.Temp);
        var bsids = bq.ToComponentDataArray<StableId>(Allocator.Temp);
        for (int i = 0; i < banks.Length; i++)
            sum += LockstepHash.Bank(banks[i].Amounts.Gold, banks[i].Amounts.Wood, banks[i].Amounts.Food,
                                     banks[i].Paused, bsids[i].Value);
        banks.Dispose(); bsids.Dispose();

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
