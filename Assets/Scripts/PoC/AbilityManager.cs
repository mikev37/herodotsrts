using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// ===========================================================================
// ABILITY MANAGER — the AbilityDefinition counterpart of UnitFactory:
//   id -> ScriptableObject -> view prefabs.
//
// SIMULATION side: registers every AbilityDefinition (collected automatically
// from the roster's unit abilities, plus any listed extras) under a
// deterministic integer id, and bakes each into a blittable AbilitySpec +
// FieldModifier payload that CommandApplySystem reads when an Ability command
// fires. Registration order = roster order, so ids match on every client.
//
// VIEW side (LateUpdate, never touches sim state):
//   * Cast effects  — drains the AbilityCastEvent buffer and instantiates the
//     ability's castEffectPrefab at the cast center (timed self-destruct).
//   * Attached effects — for each unit with an ActiveModifier from an ability
//     that has an attachedEffectPrefab, keeps exactly one instance parented to
//     the unit's view while any such modifier remains; destroys it on release.
// ===========================================================================

// Blittable sim-side snapshot of an AbilityDefinition (what commit/fire need).
public struct AbilitySpec
{
    public ShapeType   Shape;
    public float       Radius, Width, Length;
    public AnchorType  Anchor;
    public ApplyMode   Mode;
    public AffectFilter Affects;
    public float       Lifetime;
    public uint        CooldownTicks;
    public uint        ChargeUpTicks;   // commit -> fire delay (0 = same tick)
    public float       CastRange;       // WorldPoint max cast distance (0 = unlimited)
    public float       ManaCost;
    public ResourceAmount Cost;         // commander resources (ResourceAmount; add Stone/etc. in one place)
    public byte        HasSpawn;        // definition has a spawnUnit (resolved managed at fire)
    public byte        AnchorToSpawn;   // banner/totem: bind the field to the spawned unit
}

public class AbilityManager : MonoBehaviour
{
    public static AbilityManager Instance { get; private set; }

    [Tooltip("Extra abilities to register beyond those referenced by the unit roster (optional).")]
    public List<AbilityDefinition> additionalAbilities = new();

    [Header("Debug (runtime, read-only)")]
    public int registeredAbilities;
    public int activeAttachedEffects;

    private readonly List<AbilityDefinition> _defs = new();
    private readonly Dictionary<AbilityDefinition, int> _idOf = new();
    private readonly List<AbilitySpec> _specs = new();
    private readonly List<FieldModifier[]> _mods = new();

    // (unit entity, ability id) -> live attached effect instance
    private readonly Dictionary<(Entity, int), GameObject> _attached = new();
    private readonly List<(Entity, int)> _toRemove = new();
    private readonly HashSet<(Entity, int)> _wanted = new();

    private EntityManager _em;
    private EntityQuery _eventQuery, _modifierQuery;
    private bool _ready;

    private void Awake() => Instance = this;

    private void OnDestroy() { if (Instance == this) Instance = null; }

    private void Start()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) return;
        _em = world.EntityManager;
        _eventQuery = _em.CreateEntityQuery(ComponentType.ReadOnly<CommandQueueTag>());
        _modifierQuery = _em.CreateEntityQuery(
            ComponentType.ReadOnly<ActiveModifier>(),
            ComponentType.ReadOnly<UnitTag>());
        _ready = true;

        foreach (var ad in additionalAbilities) Register(ad);
    }

    // --- registry -------------------------------------------------------------

    // Idempotent. Called by UnitFactory at spawn for each roster ability, in
    // roster order — which makes ids deterministic across machines.
    public int Register(AbilityDefinition def)
    {
        if (def == null) return -1;
        if (_idOf.TryGetValue(def, out int id)) return id;

        id = _defs.Count;
        _idOf[def] = id;
        _defs.Add(def);
        _specs.Add(new AbilitySpec
        {
            Shape = def.shape,
            Radius = def.radius,
            Width = def.width,
            Length = def.length,
            Anchor = def.anchor,
            Mode = def.applyMode,
            Affects = def.affects,
            Lifetime = def.lifetime,
            CooldownTicks = (uint)math.max(1, (int)math.ceil(def.cooldown * LockstepConfig.TickRate)),
            ChargeUpTicks = (uint)math.max(0, (int)math.ceil(def.chargeUp * LockstepConfig.TickRate)),
            CastRange = math.max(0f, def.castRange),
            ManaCost = math.max(0f, def.manaCost),
            Cost = new ResourceAmount { Gold = math.max(0, def.costGold), Wood = math.max(0, def.costWood), Food = math.max(0, def.costFood) },
            HasSpawn = (byte)(def.spawnUnit != null ? 1 : 0),
            AnchorToSpawn = (byte)(def.anchorFieldToSpawn && def.spawnUnit != null &&
                                   def.applyMode == ApplyMode.PersistentArea ? 1 : 0),
        });

        // Bake the split lists into one FieldModifier payload: numeric rows
        // first, then flag rows. The order is deterministic (asset-defined), so
        // Slot identities match on every peer.
        var arr = new FieldModifier[def.numericModifiers.Count];
        int k = 0;
        for (int i = 0; i < def.numericModifiers.Count; i++)
        {
            var m = def.numericModifiers[i];
            arr[k++] = new FieldModifier
            {
                Target = (ModTarget)m.target,                 // NumericTarget mirrors ModTarget's numeric range
                Delta = m.delta,
                Mode = m.mode,
                Revert = (byte)(m.revert ? 1 : 0),
                BoolValue = 0,
                CapMode = m.capMode,
                CapRef = m.capRef,
                CapValue = m.capValue,
            };
        }
        _mods.Add(arr);
        registeredAbilities = _defs.Count;
        return id;
    }

    public bool TryGetSpec(int id, out AbilitySpec spec)
    {
        if (id >= 0 && id < _specs.Count) { spec = _specs[id]; return true; }
        spec = default; return false;
    }

    public FieldModifier[] GetModifiers(int id)
        => (id >= 0 && id < _mods.Count) ? _mods[id] : System.Array.Empty<FieldModifier>();

    public AbilityDefinition GetDefinition(int id)
        => (id >= 0 && id < _defs.Count) ? _defs[id] : null;

    // --- view effects ----------------------------------------------------------

    private void LateUpdate()
    {
        if (!_ready || _em.World == null || !_em.World.IsCreated) return;
        DrainCastEvents();
        SyncAttachedEffects();
    }

    private void DrainCastEvents()
    {
        if (_eventQuery.IsEmpty) return;
        var qe = _eventQuery.GetSingletonEntity();
        if (!_em.HasBuffer<AbilityCastEvent>(qe)) return;
        var events = _em.GetBuffer<AbilityCastEvent>(qe);
        for (int i = 0; i < events.Length; i++)
        {
            var def = GetDefinition(events[i].AbilityId);
            if (def != null && def.castEffectPrefab != null)
            {
                var go = Instantiate(def.castEffectPrefab,
                    new Vector3(events[i].Pos.x, 0f, events[i].Pos.y), Quaternion.identity);
                Destroy(go, math.max(0.1f, def.castEffectSeconds));
            }
        }
        events.Clear();
    }

    private void SyncAttachedEffects()
    {
        var vm = UnitViewManager.Instance;
        if (vm == null) return;

        // Which (unit, ability) pairs SHOULD have an attached effect right now.
        _wanted.Clear();
        var entities = _modifierQuery.ToEntityArray(Allocator.Temp);
        for (int i = 0; i < entities.Length; i++)
        {
            var mods = _em.GetBuffer<ActiveModifier>(entities[i]);
            for (int m = 0; m < mods.Length; m++)
            {
                int aid = mods[m].AbilityId;
                if (aid < 0) continue;
                var def = GetDefinition(aid);
                if (def != null && def.attachedEffectPrefab != null)
                    _wanted.Add((entities[i], aid));
            }
        }
        entities.Dispose();

        // Create missing.
        foreach (var key in _wanted)
        {
            if (_attached.ContainsKey(key)) continue;
            var view = vm.GetView(key.Item1);
            if (view == null) continue;
            var def = GetDefinition(key.Item2);
            var go = Instantiate(def.attachedEffectPrefab, view.transform);
            go.transform.localPosition = Vector3.zero;
            _attached[key] = go;
        }

        // Destroy released (modifier gone, unit died, or view recycled).
        _toRemove.Clear();
        foreach (var kv in _attached)
            if (!_wanted.Contains(kv.Key) || kv.Value == null) _toRemove.Add(kv.Key);
        foreach (var key in _toRemove)
        {
            if (_attached[key] != null) Destroy(_attached[key]);
            _attached.Remove(key);
        }

        activeAttachedEffects = _attached.Count;
    }
}
