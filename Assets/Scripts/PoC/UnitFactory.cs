using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// UNIT FACTORY — the SIM-creation half of the old UnitManager, decoupled from
// all view/pooling. Owns the archetype, the deterministic StableId counter, and
// the per-definition entity composition (including the economy roles). Reads the
// RosterDefinition asset for definitions and ids.
//
// `Create` replaces `UnitManager.SpawnUnit`. It is called from MapBootstrap,
// CommandApplySystem (blueprint/ability spawns), ProductionSystem, and
// SimSnapshot.Restore — all on the main thread at deterministic points.
// ===========================================================================
public class UnitFactory : MonoBehaviour
{
    public static UnitFactory Instance { get; private set; }

    // The roster is a single project asset, auto-resolved (RosterDefinition.Get)
    // — not a hand-wired field, so it can't be mis-assigned or left null in a
    // fresh scene. There is exactly one roster; wiring it per-scene was a foot-gun.
    private RosterDefinition roster;
    public RosterDefinition Roster => roster;

    [Header("Starting bank (per player)")]
    public int startingGold = 500, startingWood = 500, startingFood = 500;
    public int playerCount = 2;

    public bool Ready { get; private set; }

    // ---- map-placement coordination (folded in from the old coordinator) ----
    // MapBootstrap components register here (static, so registration survives
    // whatever order Awake/Start run in). After the factory is Ready it spawns
    // them all in ONE deterministic pass, so StableIds match on every peer.
    // PlacementsDone is the single gate LockstepNet checks before capturing the
    // starting snapshot.
    private static readonly List<MapBootstrap> _bootstraps = new();
    public bool PlacementsDone { get; private set; }

    public static void RegisterBootstrap(MapBootstrap b)
    {
        if (!_bootstraps.Contains(b)) _bootstraps.Add(b);
        // Late registration (runtime-instantiated after the initial pass): spawn
        // immediately so it isn't silently dropped. Outside the deterministic
        // startup set — route runtime map spawns through a command if they must
        // be lockstep-safe.
        var f = Instance;
        if (f != null && f.Ready && f.PlacementsDone && !IsNetworkClient() &&
            f.roster != null && b != null && b.definition != null)
        {
            Debug.LogWarning($"[UnitFactory] '{b.name}' registered after the initial spawn pass — " +
                             "spawning immediately (not part of the deterministic startup set).", b);
            b.Spawn(f.roster);
        }
    }

    public static void UnregisterBootstrap(MapBootstrap b) => _bootstraps.Remove(b);

    private EntityManager _em;
    private EntityArchetype _archetype;
    private int _nextStableId;
    private EntityQuery _terrainQuery;

    // Snapshot access (unchanged contract from UnitManager).
    public int NextStableId { get => _nextStableId; set => _nextStableId = value; }

    private void Awake() { Instance = this; }

    private void Start()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) { Debug.LogWarning("[UnitFactory] No ECS world."); return; }
        _em = world.EntityManager;
        roster = RosterDefinition.Get();
        if (roster == null) return;   // Get() already logged how to fix it
        roster.EnsureBuilt();          // also deterministically pre-registers the projectile-view id space
        _terrainQuery = _em.CreateEntityQuery(ComponentType.ReadOnly<TerrainHeightField>());

        // Archetype: every unit/building. (Was Team; now Player — single ownership axis.)
        var common = new ComponentType[]
        {
            typeof(LocalTransform), typeof(UnitTag), typeof(Player), typeof(UnitDefId),
            typeof(UnitTuning), typeof(Attack), typeof(Defense), typeof(Speed), typeof(Selected),
            typeof(KnockbackVelocity), typeof(UnitRadius), typeof(Mass), typeof(Velocity),
            typeof(GroundSpeedMultiplier), typeof(MoveTarget), typeof(AttackOrder), typeof(CombatTarget),
            typeof(DesiredDestination), typeof(Health), typeof(DeathTimer), typeof(Ranged), typeof(UnitAnim),
            typeof(CombatStatus), typeof(BaseStats), typeof(ActiveModifier), typeof(StableId),
            typeof(Mana), typeof(PendingCast), typeof(NavContext),
            typeof(FormationMember), typeof(FormationSlot),
            typeof(Perception), typeof(UnitInfo), typeof(IncomingProjectile),
        };
        _archetype = _em.CreateArchetype(common);

        PreRegisterAll();          // deterministic ability-id pre-registration (roster order, every peer)
        SeedPlayerBanks();         // one multi-type bank + PlayerState per player (replaces the old pool)

        Ready = true;

        SpawnAllPlacements();      // authored map content, in deterministic order
    }

    // Spawns every registered MapBootstrap in a deterministic global order
    // (order, then name, then position) — identical on every peer regardless of
    // scene-load or Awake/OnEnable ordering. A networked CLIENT skips this (its
    // world arrives via SimSnapshot.Restore).
    private void SpawnAllPlacements()
    {
        if (IsNetworkClient()) { PlacementsDone = true; return; }

        var ordered = _bootstraps
            .Where(b => b != null)
            .OrderBy(b => b.order)
            .ThenBy(b => b.name, System.StringComparer.Ordinal)
            .ThenBy(b => b.transform.position.x)
            .ThenBy(b => b.transform.position.z)
            .ToList();

        int total = 0, groups = 0;
        foreach (var b in ordered)
        {
            int made = b.Spawn(roster);
            if (made > 0) { total += made; groups++; }
        }

        PlacementsDone = true;
        Debug.Log($"[UnitFactory] spawned {total} entities from {groups} placements (deterministic order).");
    }

    internal static bool IsNetworkClient()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        return nm != null && nm.IsListening && nm.IsClient && !nm.IsServer;
    }

    // ---- entity creation (was SpawnUnit) -----------------------------------
    // Creates one fully-configured unit entity. Heroes are just units that also
    // get a HeroTag + HeroAura — no special movement/combat path. Buildings are
    // units too: same archetype and stat copy, plus BuildingTag (identity),
    // Immobile (behavior/steering skip it), an Obstacle footprint (rasterized
    // into the nav grid), a footprint-snapped position, and Y from the highest
    // footprint cell. Only ever called from MapBootstrap, CommandApplySystem (at
    // a command's execution tick), ProductionSystem, or SimSnapshot.Restore — all
    // deterministic points — so the structural adds are safe everywhere.
    public Entity Create(UnitDefinition def, int defId, int player, float3 pos)
    {
        var bdef = def as BuildingDefinition;
        int2 extents = default;
        if (bdef != null)
        {
            extents = new int2(math.max(1, bdef.footprintX), math.max(1, bdef.footprintZ));
            int2 min = BuildingFootprint.MinCell(new float2(pos.x, pos.z), extents);
            float2 snapped = BuildingFootprint.SnappedCenter(min, extents);
            pos = new float3(snapped.x, FootprintMaxHeight(min, extents, pos.y), snapped.y);
        }

        var e = _em.CreateEntity(_archetype);
        _em.SetComponentData(e, new StableId { Value = _nextStableId++ });   // deterministic identity
        _em.SetComponentEnabled<Selected>(e, false);                          // archetype components start enabled
        _em.SetComponentData(e, LocalTransform.FromPosition(pos));
        _em.SetComponentData(e, new Player { Value = player });
        _em.SetComponentData(e, new UnitDefId { Value = defId });
        _em.SetComponentData(e, new UnitTuning
        {
            TurnSpeed = def.turnSpeed,
            SeparationStrength = def.separationStrength,
            MeleeRange = def.meleeRange,
            CombatSpacing = def.combatSpacing,
            IdleSpacing = def.idleSpacing,
            AttackNearbyRange = def.attackNearbyRange,
            PursueDistance = def.pursueDistance,
            AvoidMeleeRange = def.avoidMeleeRange,
            RetreatHealthPct = def.retreatHealthFraction,
            CohesionRadius = def.cohesionRadius,
            ReEngageHealthPct = .75f,
            RetreatTime = 3,
            EyeOffset = def.eyeOffset
        });
        _em.SetComponentData(e, BuildAttack(def));
        // Non-combatant buildings: force the attack inert regardless of leftover
        // damage/range values, so canAttack is the single source of truth.
        if (bdef != null && !bdef.canAttack)
            _em.SetComponentData(e, new Attack { Phase = AttackPhase.Ready, ProjectileId = -1 });
        _em.SetComponentData(e, new Defense { Armor = def.armor, Shield = bdef != null ? 0f : def.shield });
        _em.SetComponentData(e, new Speed { Value = def.speed });
        // Buildings carry their footprint's INSCRIBED radius — consumers that
        // know about buildings (gather, behavior, projectiles) measure range to
        // the surface by subtracting it.
        _em.SetComponentData(e, new UnitRadius
        {
            Value = bdef != null
                ? math.min(extents.x, extents.y) * NavGrid.CellSize * 0.5f
                : def.radius
        });
        _em.SetComponentData(e, new Mass { Value = def.mass });
        // Height starts at the spawn height: steering snaps mobile units to
        // slope.Height every tick, so a zero here popped units to y=0 until the
        // slope system's first write. Buildings keep this value forever (they
        // skip both the slope and steering systems).
        _em.SetComponentData(e, new GroundSpeedMultiplier { Value = 1f, Height = pos.y });
        _em.SetComponentData(e, new NavContext { Value = NavCell.ContextGround });   // spawn on the ground
        _em.SetComponentData(e, new Health { Current = def.maxHealth, Max = def.maxHealth });
        _em.SetComponentData(e, new Mana { Current = def.maxMana, Max = def.maxMana, Regen = def.manaRegen });
        _em.SetComponentData(e, new DeathTimer { Seconds = def.deathAnimSeconds });
        _em.SetComponentData(e, new Ranged { IsRanged = def.isRanged });
        _em.SetComponentData(e, new UnitAnim { State = AnimState.Idle });
        _em.SetComponentData(e, new BaseStats
        {
            Speed = def.speed,
            TurnSpeed = def.turnSpeed,
            MeleeRange = def.meleeRange,
            AttackDamage = def.attackDamage,
            Armor = def.armor,
            Shield = def.shield,
        });
        // BehaviorOverride / MoveTarget / AttackOrder / CombatTarget /
        // DesiredDestination default to zero (Has=false) — fine.
        _em.SetComponentData(e, new FormationMember {
            FrontPriority = def.frontPriority,
            Looseness = def.looseness,
            Aggression = def.aggression,
            Separation = def.formationSpacing
        });
        if (def.isHero)
            _em.AddComponent<HeroTag>(e);   // abilities are cast at the hero via the ability system

        if (bdef != null)
        {
            _em.AddComponent<BuildingTag>(e);                          // identity (perception/targeting/info)
            _em.AddComponent<Immobile>(e);                             // movement gate (behavior/slope/steering/knockback skip)

            if (bdef is WallDefinition wdef)
            {
                // A wall stamps a walkable Roof top + Transition skirt instead of
                // a solid Impassable footprint. RoofHeight sits the configured
                // amount above the footprint's highest terrain cell.
                _em.AddComponentData(e, new Wall
                {
                    Extents = extents,
                    RoofHeight = pos.y + wdef.wallHeight,
                    RampCells = math.max(1, wdef.rampCells),
                    RampSide = (byte)wdef.rampSide,
                });
            }
            else
            {
                _em.AddComponentData(e, new Obstacle { Extents = extents, OccluderHeight = bdef.occluderHeight });  // nav-grid footprint + sight-block height
            }

            // Spikes / palisade: passive bite dealt to units that touch it. Only
            // added when authored (contactDamage > 0), so it costs nothing for the
            // vast majority of buildings.
            if (bdef.contactDamage > 0f)
                _em.AddComponentData(e, new ContactDamage { DamagePerSecond = bdef.contactDamage });
        }

        if (!def.receivesAbilities)
            _em.AddComponent<AbilityImmune>(e);                        // ability fields never stamp onto this entity

        // Ability slots: register each AbilityDefinition with the AbilityManager
        // (idempotent; roster order => deterministic ids) and store the ids.
        var slots = new AbilitySlots { Ids = new int4(-1, -1, -1, -1) };
        if (def.abilities != null && AbilityManager.Instance != null)
            for (int s = 0; s < 4 && s < def.abilities.Length; s++)
                slots.Ids[s] = AbilityManager.Instance.Register(def.abilities[s]);
        _em.AddComponentData(e, slots);
        _em.AddComponentData(e, new AbilityCooldowns { ReadyTick = uint4.zero });

        AddEconomyRoles(e, def, bdef, player);
        return e;
    }

    // ---- economy/role composition (new) ------------------------------------
    public void AddEconomyRoles(Entity e, UnitDefinition def, BuildingDefinition bdef, int player)
    {
        if (bdef != null)
        {
            if (bdef is ResourceNodeDefinition node)
            {
                var amt = new ResourceAmount(); var cap = new ResourceAmount();
                amt[node.resourceType] = node.amount; cap[node.resourceType] = node.amount;
                _em.AddComponentData(e, new NodeTag { Yield = node.resourceType, DespawnWhenEmpty = (byte)(node.despawnWhenDepleted ? 1 : 0), HuskLinger = node.huskLingerSeconds });
                if (!_em.HasComponent<ResourceBank>(e)) _em.AddComponentData(e, new ResourceBank { Amounts = amt, Capacity = cap });
                EnsureBankBuffers(e);
                _em.AddComponent<NonCombatant>(e);   // a node is harvested, never attacked
            }
            else if (bdef.isDepot || bdef.isColony)
            {
                _em.AddComponent<DepotTag>(e);
                if (!_em.HasComponent<ResourceBank>(e)) _em.AddComponentData(e, new ResourceBank { Amounts = default, Capacity = default });
                EnsureBankBuffers(e);
                if (bdef.isIntake) _em.AddComponent<IntakeTag>(e);
                if (bdef.isColony)
                {
                    int hid = bdef.haulerUnit != null ? roster.GetId(bdef.haulerUnit) : -1;
                    _em.AddComponentData(e, new Colony { HaulerDefId = hid, Threshold = bdef.haulThreshold, BuildTimer = 0f });
                }
            }
            if (bdef.isProducer)
            {
                _em.AddComponent<ProducerTag>(e);
                if (!_em.HasBuffer<ProductionItem>(e)) _em.AddBuffer<ProductionItem>(e);
                _em.AddComponentData(e, new RallyPoint { Has = 0 });
                EnsureBankBuffers(e);
            }
            if (bdef.isRelay) _em.AddComponentData(e, new Relay { Rate = bdef.relayRate, Range = bdef.relayRange });
        }
        if (def.buildPower > 0f)
        {
            _em.AddComponentData(e, new BuildPower { Value = def.buildPower });
            _em.AddComponentData(e, new BuildSignal { LastTick = 0 });
        }
        if (def.carryCapacity > 0)
        {
            var cap = new ResourceAmount { Gold = def.carryCapacity, Wood = def.carryCapacity, Food = def.carryCapacity };
            _em.AddComponentData(e, new HarvestTask { NodeStableId = -1, DepotStableId = -1, Phase = HarvestPhase.Idle, Rate = math.max(1, def.harvestRate) });
            if (!_em.HasComponent<ResourceBank>(e)) _em.AddComponentData(e, new ResourceBank { Amounts = default, Capacity = cap });
            EnsureBankBuffers(e);
        }
        if (def.isHauler)
        {
            var cap = new ResourceAmount { Gold = def.carryCapacity, Wood = def.carryCapacity, Food = def.carryCapacity };
            _em.AddComponentData(e, new HaulTask { SourceStableId = -1, SinkStableId = -1, Phase = HaulPhase.ToSource });
            if (!_em.HasComponent<ResourceBank>(e)) _em.AddComponentData(e, new ResourceBank { Amounts = default, Capacity = cap });
            EnsureBankBuffers(e);
        }
        if (def.abilities != null && def.abilities.Length > 0) EnsureBankBuffers(e);   // casters receive ability-cost grants
    }

    // Re-applies a form's full STAT block from its def (the same values SpawnUnit copies).
    // Used by Create (preserveVitals = false -> full HP/mana) and by morph/upgrade
    // (preserveVitals = true -> keep the current HP/mana FRACTION across the change).
    // Does NOT touch transform/velocity/roles — caller owns those.
    public void ApplyStats(Entity e, UnitDefinition def, int defId, bool preserveVitals)
    {
        var bdef = def as BuildingDefinition;
        int2 extents = bdef != null ? new int2(math.max(1, bdef.footprintX), math.max(1, bdef.footprintZ)) : default;

        _em.SetComponentData(e, new UnitDefId { Value = defId });
        _em.SetComponentData(e, new UnitTuning {
            TurnSpeed = def.turnSpeed, SeparationStrength = def.separationStrength, MeleeRange = def.meleeRange,
            CombatSpacing = def.combatSpacing, IdleSpacing = def.idleSpacing, AttackNearbyRange = def.attackNearbyRange,
            PursueDistance = def.pursueDistance, AvoidMeleeRange = def.avoidMeleeRange,
            RetreatHealthPct = def.retreatHealthFraction, CohesionRadius = def.cohesionRadius,
            ReEngageHealthPct = .75f, RetreatTime = 3, EyeOffset = def.eyeOffset });   // constants, matching SpawnUnit
        _em.SetComponentData(e, BuildAttack(def));
        if (bdef != null && !bdef.canAttack)
            _em.SetComponentData(e, new Attack { Phase = AttackPhase.Ready, ProjectileId = -1 });
        _em.SetComponentData(e, new Defense { Armor = def.armor, Shield = bdef != null ? 0f : def.shield });
        _em.SetComponentData(e, new Speed { Value = def.speed });
        _em.SetComponentData(e, new UnitRadius {
            Value = bdef != null ? math.min(extents.x, extents.y) * NavGrid.CellSize * 0.5f : def.radius });
        _em.SetComponentData(e, new Mass { Value = def.mass });
        _em.SetComponentData(e, new Ranged { IsRanged = def.isRanged });
        _em.SetComponentData(e, new DeathTimer { Seconds = def.deathAnimSeconds });
        _em.SetComponentData(e, new BaseStats {
            Speed = def.speed, TurnSpeed = def.turnSpeed, MeleeRange = def.meleeRange,
            AttackDamage = def.attackDamage, Armor = def.armor, Shield = def.shield });
        _em.SetComponentData(e, new FormationMember {
            FrontPriority = def.frontPriority, Looseness = def.looseness,
            Aggression = def.aggression, Separation = def.formationSpacing });

        float hpFrac = 1f, manaFrac = 1f;
        if (preserveVitals)
        {
            var h = _em.GetComponentData<Health>(e); hpFrac = h.Max > 0f ? h.Current / h.Max : 1f;
            var m = _em.GetComponentData<Mana>(e);   manaFrac = m.Max > 0f ? m.Current / m.Max : 1f;
        }
        _em.SetComponentData(e, new Health { Current = def.maxHealth * hpFrac, Max = def.maxHealth });
        _em.SetComponentData(e, new Mana { Current = def.maxMana * manaFrac, Max = def.maxMana, Regen = def.manaRegen });
    }

    // idempotent: safe to call from a fresh Create OR from MorphSystem re-applying roles
    public void EnsureBankBuffers(Entity e)
    {
        if (!_em.HasBuffer<BankDeposit>(e)) _em.AddBuffer<BankDeposit>(e);
        if (!_em.HasBuffer<BankRequest>(e)) _em.AddBuffer<BankRequest>(e);
    }

    private void SeedPlayerBanks()
    {
        for (int p = 0; p < playerCount; p++)
        {
            var e = _em.CreateEntity(typeof(StableId), typeof(Player), typeof(ResourceBank), typeof(PlayerBankTag), typeof(PlayerState));
            _em.SetComponentData(e, new StableId { Value = _nextStableId++ });
            _em.SetComponentData(e, new Player { Value = p });
            _em.SetComponentData(e, new ResourceBank { Amounts = new ResourceAmount { Gold = startingGold, Wood = startingWood, Food = startingFood }, Capacity = default });
            _em.SetComponentData(e, new PlayerState { HeroStableId = -1, Age = 0 });
            _em.AddBuffer<BankDeposit>(e); _em.AddBuffer<BankRequest>(e);
            _em.AddBuffer<ResearchedTech>(e);   // completed unit upgrades (Knight->Paladin)
        }
    }

    // ---- helpers (were private in UnitManager; bodies unchanged except for the
    //      projectile-id registry moving onto RosterDefinition) ----------------
    private Attack BuildAttack(UnitDefinition def)
    {
        var a = new Attack
        {
            ArcDot = Mathf.Cos(Mathf.Deg2Rad * def.meleeStrikeArc * 0.5f),
            Cleave = def.meleeCleave,
            Phase = AttackPhase.Ready,
            isRange = def.isRanged,
            Timer = 0f,
            Pulse = 0f,
            ProjectileId = -1,
        };
        if (def.isRanged)
        {
            a.Range = def.attackRange;
            a.ChargeUp = def.attackInterval;
            a.Cooldown = def.attackCooldown;
            a.Damage = def.attackDamage;
            var pd = def.projectile;
            if (pd != null)
            {
                a.ProjectileId = roster.ResolveProjectileId(pd);   // registry index, NOT unit def id
                a.ProjSpeed = pd.speed;
                a.ProjRise = pd.riseHeight;
                a.ProjLaunchHeight = pd.launchHeight;
                a.ProjHitRadius = pd.hitRadius;
                a.ProjCollisionHeight = pd.collisionHeight;
            }
        }
        else
        {
            a.Range = def.meleeRange;
            a.ChargeUp = def.attackInterval;
            a.Cooldown = def.attackCooldown;
            a.Damage = def.attackDamage;
        }
        return a;
    }

    private bool TryGetTerrain(out TerrainHeightField field)
    {
        // _terrainQuery is created in Start before any spawn path can run.
        field = default;
        if (!_terrainQuery.HasSingleton<TerrainHeightField>()) return false;
        field = _terrainQuery.GetSingleton<TerrainHeightField>();
        return field.IsValid;
    }

    private float FootprintMaxHeight(int2 minCell, int2 extents, float fallback)
    {
        if (!TryGetTerrain(out var terrain)) return fallback;
        float maxH = float.MinValue;
        for (int ly = 0; ly < extents.y; ly++)
        for (int lx = 0; lx < extents.x; lx++)
        {
            if (BuildingFootprint.CornerCut(lx, ly, extents)) continue;
            int x = minCell.x + lx, y = minCell.y + ly;
            if (!NavGrid.InBounds(x, y)) continue;
            maxH = math.max(maxH, NavTerrain.SampleHeight(terrain, NavGrid.CellCenter(x, y)));
        }
        return maxH > float.MinValue ? maxH : fallback;
    }

    // Deterministic ability-id pre-registration: walk the GLOBAL roster in index
    // order (same on every peer) so AbilityManager assigns identical ids before any
    // unit exists. Projectile-id pre-registration is already handled inside
    // roster.EnsureBuilt() (called just before this, in Start).
    private void PreRegisterAll()
    {
        for (int id = 0; id < roster.Count; id++)
        {
            var def = roster.GetDefinition(id);
            if (def == null || def.abilities == null || AbilityManager.Instance == null) continue;
            for (int s = 0; s < 4 && s < def.abilities.Length; s++)
                AbilityManager.Instance.Register(def.abilities[s]);
        }
    }
}
