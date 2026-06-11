using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// UNIT MANAGER — the single runtime owner of units. Two jobs, one registry:
//
//   BACKING  : on Start, creates entities directly from the roster's
//              UnitDefinitions (no sim prefab, no baking, no SubScene). Every
//              value comes from the definition; behaviors are packed into a
//              BehaviorFlags bitmask; the entity stores its definition's index
//              as UnitDefId.
//
//   VISUALS  : each LateUpdate, slaves a pooled viewPrefab to each entity,
//              looking the prefab up via UnitDefId -> roster[id].viewPrefab, and
//              tints the team color onto the prefab's TeamColorTarget slots.
//
// The roster list IS the registry: index = UnitDefId.
// ===========================================================================
public class UnitManager : MonoBehaviour
{
    [Serializable]
    public class SpawnEntry
    {
        public UnitDefinition definition;
        public int countPerTeam = 100;
    }

    [Header("Roster (index = definition ID)")]
    public List<SpawnEntry> teamone = new();
    public List<SpawnEntry> teamtwo = new();

    public static UnitManager Instance { get; private set; }

    [Header("Field")]
    public int teamCount = 2;
    public float fieldSize = 80f;

    [Header("Team / commander colors (index = team)")]
    public Color[] teamColors =
    {
        new Color(0.30f, 0.55f, 1.00f),   // team 0
        new Color(1.00f, 0.40f, 0.30f),   // team 1
    };

    [Header("Debug (runtime, read-only)")]
    public bool worldReady;
    public int spawnedCount;
    public int trackedEntities, activeViews, pooledViews;

    private EntityManager _em;
    private EntityQuery _viewQuery;
    private EntityArchetype _archetype;

    // view pooling, keyed by definition ID
    private readonly Dictionary<Entity, UnitView> _views = new();
    private readonly Dictionary<int, Stack<UnitView>> _pool = new();
    private readonly Dictionary<UnitView, int> _typeOf = new();
    private readonly List<Entity> _toRemove = new();

    private List<List<SpawnEntry>> roster = new();

    private void Start()
    {
        Instance = this;
        roster.Add(teamone);
        roster.Add(teamtwo);
        var world = World.DefaultGameObjectInjectionWorld;
        worldReady = world != null && world.IsCreated;
        if (!worldReady) { Debug.LogWarning("[UnitManager] No ECS world found."); return; }
        _em = world.EntityManager;

        _viewQuery = _em.CreateEntityQuery(
            ComponentType.ReadOnly<UnitTag>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<UnitAnim>(),
            ComponentType.ReadOnly<Health>(),
            ComponentType.ReadOnly<UnitDefId>(),
            ComponentType.ReadOnly<Team>());

        SpawnAll();
    }

    // -----------------------------------------------------------------------
    // BACKING
    // -----------------------------------------------------------------------
    private void SpawnAll()
    {
        var common = new ComponentType[]
        {
            typeof(LocalTransform), typeof(UnitTag), typeof(Team), typeof(UnitDefId),
            typeof(BehaviorFlags), typeof(BehaviorOverride), typeof(UnitTuning), typeof(Attack),
            typeof(Defense), typeof(Speed), typeof(Selected),
            typeof(UnitRadius), typeof(Mass), typeof(Velocity), typeof(GroundSpeedMultiplier),
            typeof(MoveTarget), typeof(AttackOrder), typeof(CombatTarget), typeof(DesiredDestination),
            typeof(Health), typeof(DeathTimer), typeof(Ranged), typeof(UnitAnim), typeof(CombatStatus),
            typeof(BaseStats), typeof(ActiveModifier), typeof(StableId),
            typeof(Perception), typeof(UnitInfo), typeof(FriendlyUnit),   // perception + contact/friendly lists
        };
        var archetype = _em.CreateArchetype(common);

        // Terrain height for spawn placement (baked by TerrainFieldBootstrap; if
        // it hasn't run yet, units spawn at 0 and Steering snaps them next tick).
        var terrainQuery = _em.CreateEntityQuery(ComponentType.ReadOnly<TerrainHeightField>());
        bool hasTerrain = false;
        TerrainHeightField terrainField = default;
        if (terrainQuery.HasSingleton<TerrainHeightField>())
        {
            terrainField = terrainQuery.GetSingleton<TerrainHeightField>();
            hasTerrain = terrainField.IsValid;
        }
        _archetype = archetype;

        float spacing = 2;
        for (int team = 0; team < teamCount; team++) {
            float teamSign = team == 0 ? -1f : 1f;
            float zFront = teamSign * (fieldSize * 0.25f);
            int sofarranks = 0;
            for (int id = 0; id < roster[team].Count; id++) {
                int ranks = roster[team].Count/50+1;
                float xCursor = -fieldSize * 0.4f;
                var def = roster[team][id].definition;
                if (def == null) {
                    Debug.LogWarning($"[UnitManager] Roster entry {id} has no definition; skipped.");
                    continue;
                }
                if (def.viewPrefab == null)
                    Debug.LogWarning($"[UnitManager] '{def.displayName}' has no viewPrefab; units invisible.");
                if (def.isRanged && def.projectile == null)
                    Debug.LogWarning($"[UnitManager] '{def.displayName}' is ranged but has no projectile.");

                int count = roster[team][id].countPerTeam;
                int cols = (count + ranks - 1) / ranks;
                for (int i = 0; i < count; i++) {
                    int col = i / ranks;
                    int rank = i % ranks;
                    float x = xCursor + col * spacing;
                    float z = zFront + teamSign * (sofarranks + rank) * spacing;
                    float y = hasTerrain ? NavTerrain.SampleHeight(terrainField, new float2(x, z)) : 0f;
                    SpawnUnit(def, id, team, new float3(x, y, z));
                }
                sofarranks += ranks + 1;
            }
        }
    }

    private int _nextStableId;

    // Creates one fully-configured unit entity. Heroes are just units that also
    // get a HeroTag + HeroAura — no special movement/combat path.
    public Entity SpawnUnit(UnitDefinition def, int defId, int team, float3 pos)
    {
        var e = _em.CreateEntity(_archetype);
        _em.SetComponentData(e, new StableId { Value = _nextStableId++ });
        _em.SetComponentEnabled<Selected>(e, false);
        _em.SetComponentData(e, LocalTransform.FromPosition(pos));
        _em.SetComponentData(e, new Team { Value = team });
        _em.SetComponentData(e, new UnitDefId { Value = defId });
        _em.SetComponentData(e, new BehaviorFlags { Value = PackFlags(def) });
        _em.SetComponentData(e, new UnitTuning
        {
            TurnSpeed          = def.turnSpeed,
            SeparationStrength = def.separationStrength,
            MeleeRange         = def.meleeRange,
            CombatSpacing      = def.combatSpacing,
            IdleSpacing        = def.idleSpacing,
            AttackNearbyRange  = def.attackNearbyRange,
            AvoidMeleeRange    = def.avoidMeleeRange,
            RetreatHealthPct   = def.retreatHealthFraction,
            PursueDistance     = def.pursueDistance,
        });
        _em.SetComponentData(e, BuildAttack(def));
        _em.SetComponentData(e, new Defense { Armor = def.armor, Shield = def.shield });
        _em.SetComponentData(e, new Speed { Value = def.speed });
        _em.SetComponentData(e, new UnitRadius { Value = def.radius });
        _em.SetComponentData(e, new Mass { Value = def.mass });
        _em.SetComponentData(e, new GroundSpeedMultiplier { Value = 1f });
        _em.SetComponentData(e, new Health { Current = def.maxHealth, Max = def.maxHealth });
        _em.SetComponentData(e, new DeathTimer { Seconds = def.deathAnimSeconds });
        _em.SetComponentData(e, new Ranged { IsRanged = def.isRanged });
        _em.SetComponentData(e, new UnitAnim { State = AnimState.Idle });
        _em.SetComponentData(e, new BaseStats
        {
            Speed        = def.speed,
            TurnSpeed    = def.turnSpeed,
            MeleeRange   = def.meleeRange,
            AttackDamage = def.attackDamage,
            Armor        = def.armor,
            Shield       = def.shield,
        });
        // BehaviorOverride / MoveTarget / AttackOrder / CombatTarget /
        // DesiredDestination default to zero (Has=false) — fine.

        if (def.isHero)
            _em.AddComponent<HeroTag>(e);

        var slots = new AbilitySlots { Ids = new int4(-1, -1, -1, -1) };
        if (def.abilities != null && AbilityManager.Instance != null)
            for (int s = 0; s < 4 && s < def.abilities.Length; s++)
                slots.Ids[s] = AbilityManager.Instance.Register(def.abilities[s]);
        _em.AddComponentData(e, slots);
        _em.AddComponentData(e, new AbilityCooldowns { ReadyTick = uint4.zero });

        spawnedCount++;
        return e;
    }

    // View accessor for effect attachment (AbilityManager). Null while dead/unspawned.
    public GameObject GetView(Entity e)
        => _views.TryGetValue(e, out var v) && v != null ? v.gameObject : null;

    private static uint PackFlags(UnitDefinition d)
    {
        uint f = 0;
        if (d.attackNearby)       f |= (uint)BehaviorFlag.AttackNearby;
        if (d.flankTarget)        f |= (uint)BehaviorFlag.FlankTarget;
        if (d.bodyBlock)          f |= (uint)BehaviorFlag.BodyBlock;
        if (d.formWall)           f |= (uint)BehaviorFlag.FormWall;
        if (d.standBehindFriend)  f |= (uint)BehaviorFlag.StandBehindFriend;
        if (d.advanceOnEnemy)     f |= (uint)BehaviorFlag.AdvanceOnEnemy;
        if (d.advanceIndividual)  f |= (uint)BehaviorFlag.AdvanceIndividual;
        if (d.avoidMelee)         f |= (uint)BehaviorFlag.AvoidMelee;
        if (d.retreatLowHealth)   f |= (uint)BehaviorFlag.RetreatLowHealth;
        if (d.formWedge)          f |= (uint)BehaviorFlag.FormWedge;
        if (d.alignCardinal)      f |= (uint)BehaviorFlag.AlignCardinal;
        if (d.alignFacing)        f |= (uint)BehaviorFlag.AlignFacing;
        if (d.alignMovement)      f |= (uint)BehaviorFlag.AlignMovement;
        if (d.separate)           f |= (uint)BehaviorFlag.Separate;
        if (d.separateIdle)       f |= (uint)BehaviorFlag.SeparateIdle;
        if (d.spreadLateral)      f |= (uint)BehaviorFlag.SpreadLateral;
        return f;
    }

    private Color TeamColor(int team)
        => (teamColors != null && team >= 0 && team < teamColors.Length) ? teamColors[team] : Color.white;

    // Projectile view registry: referenced ProjectileDefinitions get a stable id
    // (their index) which projectiles carry so ProjectileViewManager can draw them.
    private readonly List<ProjectileDefinition> _projectileDefs = new();
    private readonly Dictionary<ProjectileDefinition, int> _projIndex = new();

    private int ResolveProjectileId(ProjectileDefinition pd)
    {
        if (pd == null) return -1;
        if (_projIndex.TryGetValue(pd, out var idx)) return idx;
        idx = _projectileDefs.Count;
        _projectileDefs.Add(pd);
        _projIndex[pd] = idx;
        return idx;
    }

    // Build the unified Attack from a definition (melee vs ranged share the timer).
    private Attack BuildAttack(UnitDefinition def)
    {
        var a = new Attack
        {
            ArcDot      = Mathf.Cos(Mathf.Deg2Rad * def.meleeStrikeArc * 0.5f),
            Cleave      = def.meleeCleave,
            Phase       = AttackPhase.Ready,
            isRange     = def.isRanged,
            Timer       = 0f,
            Pulse       = 0f,
            ProjectileId = -1,
        };
        if (def.isRanged)
        {
            a.Range    = def.attackRange;
            a.ChargeUp = def.attackInterval;
            a.Cooldown = def.attackCooldown;
            a.Damage   = def.attackDamage;
            var pd = def.projectile;
            if (pd != null)
            {
                a.ProjectileId       = ResolveProjectileId(pd);
                a.ProjSpeed          = pd.speed;
                a.ProjRise           = pd.riseHeight;
                a.ProjLaunchHeight   = pd.launchHeight;
                a.ProjHitRadius      = pd.hitRadius;
                a.ProjCollisionHeight = pd.collisionHeight;
            }
        }
        else
        {
            a.Range    = def.meleeRange;
            a.ChargeUp = def.attackInterval;
            a.Cooldown = def.attackCooldown;
            a.Damage   = def.attackDamage;
        }
        return a;
    }

    // Used by ProjectileViewManager to resolve a projectile's view prefab by id.
    public GameObject GetProjectileViewPrefab(int id)
        => (id >= 0 && id < _projectileDefs.Count) ? _projectileDefs[id].viewPrefab : null;

    // -----------------------------------------------------------------------
    // VISUALS
    // -----------------------------------------------------------------------
    private void LateUpdate()
    {
        if (!worldReady || _em.World == null || !_em.World.IsCreated) return;

        var entities = _viewQuery.ToEntityArray(Allocator.Temp);
        var xforms   = _viewQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var anims    = _viewQuery.ToComponentDataArray<UnitAnim>(Allocator.Temp);
        var hps      = _viewQuery.ToComponentDataArray<Health>(Allocator.Temp);
        var ids      = _viewQuery.ToComponentDataArray<UnitDefId>(Allocator.Temp);
        var teams    = _viewQuery.ToComponentDataArray<Team>(Allocator.Temp);

        var alive = new HashSet<Entity>();
        for (int i = 0; i < entities.Length; i++)
        {
            var e = entities[i];
            alive.Add(e);
            if (!_views.TryGetValue(e, out var view))
            {
                view = Acquire(teams[i].Value, ids[i].Value);
                if (view == null) continue;
                view.SetTeamColor(TeamColor(teams[i].Value));
                _views[e] = view;
            }
            var t = view.transform;
            t.position = xforms[i].Position;
            t.rotation = xforms[i].Rotation;
            view.Apply(anims[i].State);
            view.setHP(hps[i].Current);
        }

        _toRemove.Clear();
        foreach (var kv in _views) if (!alive.Contains(kv.Key)) _toRemove.Add(kv.Key);
        foreach (var e in _toRemove) { Release(_views[e]); _views.Remove(e); }

        trackedEntities = entities.Length;
        activeViews = _views.Count;
        pooledViews = 0; foreach (var s in _pool.Values) pooledViews += s.Count;

        entities.Dispose(); xforms.Dispose(); anims.Dispose(); ids.Dispose(); teams.Dispose();
    }

    private UnitView Acquire(int team, int defId)
    {
        if (defId < 0 || defId >= roster[team].Count) return null;
        var prefab = roster[team][defId].definition != null ? roster[team][defId].definition.viewPrefab : null;
        if (prefab == null) return null;

        if (_pool.TryGetValue(defId, out var stack) && stack.Count > 0)
        {
            var reused = stack.Pop();
            reused.gameObject.SetActive(true);
            reused.Bind();
            return reused;
        }

        var go = Instantiate(prefab);
        var v = go.GetComponent<UnitView>();
        if (v == null) v = go.AddComponent<UnitView>();
        v.Bind();
        go.name = $"UnitView_{roster[team][defId].definition.displayName}";
        _typeOf[v] = defId;
        return v;
    }

    private void Release(UnitView v)
    {
        if (v == null) return;
        v.gameObject.SetActive(false);
        int defId = _typeOf.TryGetValue(v, out var id) ? id : 0;
        if (!_pool.TryGetValue(defId, out var stack)) _pool[defId] = stack = new Stack<UnitView>();
        stack.Push(v);
    }
}
