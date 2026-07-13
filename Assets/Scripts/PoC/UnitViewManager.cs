using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// UNIT VIEW MANAGER — the VISUALS half of the old UnitManager, decoupled from
// sim creation. Each LateUpdate it slaves a pooled viewPrefab to every unit
// entity (prefab resolved via UnitDefId -> roster.GetDefinition(id).viewPrefab),
// tints the owning player's color, and pushes Health / AnimState / ResourceBank /
// Construction into the prefab's view components. Never creates or mutates sim
// state.
// ===========================================================================
public class UnitViewManager : MonoBehaviour
{
    public static UnitViewManager Instance { get; private set; }

    // Auto-resolved single project asset (same one the factory uses) — not a
    // hand-wired field. Guarantees the view manager and factory never disagree
    // about the def-id space.
    private RosterDefinition roster;

    [Header("Player colors (index = player id)")]
    public Color[] playerColors =
    {
        new Color(0.30f, 0.55f, 1.00f),
        new Color(1.00f, 0.40f, 0.30f),
    };

    [Header("Debug (runtime, read-only)")]
    public bool worldReady;
    public int trackedEntities, activeViews, pooledViews;

    private EntityManager _em;
    private EntityQuery _viewQuery;
    private readonly Dictionary<Entity, UnitView> _views = new();
    private readonly Dictionary<long, Stack<UnitView>> _pool = new();
    private readonly Dictionary<UnitView, int>  _typeOf = new();   // defId, for morph detection
    private readonly Dictionary<UnitView, long> _poolKeyOf = new(); // composite pool key, for release
    private readonly Dictionary<Entity, GameObject> _rallyMarkers = new();   // view-only rally flags
    private static readonly List<Entity> _rallyDead = new();
    [Tooltip("Optional: material blueprints (plans) render with — assign a transparent material here. " +
             "Unset = a translucent tint is applied via property block (shader-dependent).")]
    public Material blueprintMaterial;

    private readonly HashSet<UnitView> _tinted = new();   // views currently plan-tinted
    private readonly Dictionary<Renderer, Material[]> _planOriginals = new();
    private static readonly int _colorId = Shader.PropertyToID("_Color");
    private static readonly int _baseColorId = Shader.PropertyToID("_BaseColor");

    private void ApplyPlanTint(UnitView view, bool plan)
    {
        var c = new Color(0.55f, 0.8f, 1f, 0.45f);
        foreach (var r in view.GetComponentsInChildren<Renderer>())
        {
            if (plan)
            {
                if (blueprintMaterial != null)
                {
                    if (!_planOriginals.ContainsKey(r))
                    {
                        _planOriginals[r] = r.sharedMaterials;
                        var mats = new Material[r.sharedMaterials.Length];
                        for (int m = 0; m < mats.Length; m++) mats[m] = blueprintMaterial;
                        r.sharedMaterials = mats;
                    }
                }
                else
                {
                    var mpb = new MaterialPropertyBlock();
                    r.GetPropertyBlock(mpb);
                    mpb.SetColor(_colorId, c); mpb.SetColor(_baseColorId, c);
                    r.SetPropertyBlock(mpb);
                }
            }
            else
            {
                if (_planOriginals.TryGetValue(r, out var orig)) { r.sharedMaterials = orig; _planOriginals.Remove(r); }
                r.SetPropertyBlock(null);   // restore original material state
            }
        }
        if (plan) _tinted.Add(view); else _tinted.Remove(view);
    }
    private readonly List<Entity> _toRemove = new();

    private void Awake() { Instance = this; }

    // Resolve the pooled view GameObject currently slaved to an entity, or null
    // if it has none yet (e.g. this frame's LateUpdate hasn't run). Used by
    // AbilityManager to parent attached-effect VFX onto the right unit view.
    public GameObject GetView(Entity e) => _views.TryGetValue(e, out var v) ? v.gameObject : null;

    private void Start()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        worldReady = world != null && world.IsCreated;
        if (!worldReady) { Debug.LogWarning("[UnitViewManager] No ECS world."); return; }
        _em = world.EntityManager;
        roster = RosterDefinition.Get();
        if (roster != null) roster.EnsureBuilt();

        _viewQuery = _em.CreateEntityQuery(
            ComponentType.ReadOnly<UnitTag>(), ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<UnitAnim>(), ComponentType.ReadOnly<Health>(),
            ComponentType.ReadOnly<UnitDefId>(), ComponentType.ReadOnly<Player>(),
            ComponentType.ReadOnly<StableId>());
    }

    private Color PlayerColor(int p) => (p >= 0 && p < playerColors.Length) ? playerColors[p] : Color.gray;

    private void LateUpdate()
    {
        if (!worldReady || _em.World == null || !_em.World.IsCreated) return;

        var entities = _viewQuery.ToEntityArray(Allocator.Temp);
        var xforms   = _viewQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var anims    = _viewQuery.ToComponentDataArray<UnitAnim>(Allocator.Temp);
        var hps      = _viewQuery.ToComponentDataArray<Health>(Allocator.Temp);
        var ids      = _viewQuery.ToComponentDataArray<UnitDefId>(Allocator.Temp);
        var players  = _viewQuery.ToComponentDataArray<Player>(Allocator.Temp);
        var sids     = _viewQuery.ToComponentDataArray<StableId>(Allocator.Temp);

        var alive = new HashSet<Entity>();
        for (int i = 0; i < entities.Length; i++)
        {
            var e = entities[i]; alive.Add(e);

            // swap the view if this entity MORPHED (UnitDefId changed under the same Entity)
            if (_views.TryGetValue(e, out var view) && _typeOf.TryGetValue(view, out var vdef) && vdef != ids[i].Value)
            {
                // Per-unit morph VFX: the effect belongs to the definition being
                // morphed INTO (a Castle's completion burst differs from a siege
                // tank deploying). Spawned unparented so it survives the view swap.
                var targetDef = roster != null ? roster.GetDefinition(ids[i].Value) : null;
                if (targetDef != null && targetDef.morphEffectPrefab != null)
                    Destroy(Instantiate(targetDef.morphEffectPrefab, xforms[i].Position, xforms[i].Rotation), 4f);
                Release(view); _views.Remove(e); view = null;
            }

            if (view == null && !_views.TryGetValue(e, out view))
            {
                view = Acquire(ids[i].Value, sids[i].Value);
                if (view == null) continue;
                view.SetPlayerColor(PlayerColor(players[i].Value), isNeutral: players[i].Value < 0);
                _views[e] = view;
            }
            var t = view.transform;
            t.position = xforms[i].Position;
            t.rotation = xforms[i].Rotation;
            view.Apply(anims[i].State);
            view.setHP(hps[i].Current);

            // Blueprints render as faded PLANS; the tint clears on conversion.
            bool isPlan = _em.HasComponent<BlueprintTag>(e);
            if (isPlan || _tinted.Contains(view)) ApplyPlanTint(view, isPlan);

            // economy view pushes (optional components on the prefab)
            var rv = view.GetComponent<ResourceView>();
            if (rv != null && _em.HasComponent<ResourceBank>(e))
            {
                var a = _em.GetComponentData<ResourceBank>(e).Amounts;
                rv.SetAmounts(a.Gold, a.Wood, a.Food);
            }
            var cv = view.GetComponent<ConstructionView>();
            if (cv != null)
            {
                if (_em.HasComponent<Construction>(e))
                {
                    var c = _em.GetComponentData<Construction>(e);
                    cv.SetProgress(c.BuildTime > 0f ? c.Progress / c.BuildTime : 1f);
                }
                else cv.SetProgress(1f);
            }

            // resource node depletion -> husk (fraction of the yielded slot remaining)
            var nv = view.GetComponent<NodeView>();
            if (nv != null && _em.HasComponent<NodeTag>(e) && _em.HasComponent<ResourceBank>(e))
            {
                var nt = _em.GetComponentData<NodeTag>(e);
                var b = _em.GetComponentData<ResourceBank>(e);
                int cap = b.Capacity[nt.Yield];
                nv.SetFill(cap > 0 ? (float)b.Amounts[nt.Yield] / cap : 0f);
            }
        }

        _toRemove.Clear();
        foreach (var kv in _views) if (!alive.Contains(kv.Key)) _toRemove.Add(kv.Key);
        foreach (var e in _toRemove) { Release(_views[e]); _views.Remove(e); }

        trackedEntities = entities.Length; activeViews = _views.Count;
        pooledViews = 0; foreach (var s in _pool.Values) pooledViews += s.Count;
        entities.Dispose(); xforms.Dispose(); anims.Dispose(); hps.Dispose(); ids.Dispose(); players.Dispose(); sids.Dispose();
        UpdateRallyMarkers();
    }

    // View-only rally flags: while a producer building is SELECTED and has a rally
    // point, show its def's rallyPrefab there. Markers vanish on deselect/clear.
    private void UpdateRallyMarkers()
    {
        var q = _em.CreateEntityQuery(
            ComponentType.ReadOnly<RallyPoint>(), ComponentType.ReadOnly<UnitDefId>(),
            ComponentType.ReadOnly<Selected>());
        var ents = q.ToEntityArray(Allocator.Temp);

        // mark-and-sweep: anything not refreshed this frame is removed below
        _rallyDead.Clear();
        foreach (var kv in _rallyMarkers) _rallyDead.Add(kv.Key);

        for (int i = 0; i < ents.Length; i++)
        {
            if (!_em.IsComponentEnabled<Selected>(ents[i])) continue;
            var rp = _em.GetComponentData<RallyPoint>(ents[i]);
            if (rp.Has == 0) continue;
            var def = roster != null ? roster.GetDefinition(_em.GetComponentData<UnitDefId>(ents[i]).Value) as BuildingDefinition : null;
            if (def == null || def.rallyPrefab == null) continue;

            if (!_rallyMarkers.TryGetValue(ents[i], out var go) || go == null)
                _rallyMarkers[ents[i]] = go = Instantiate(def.rallyPrefab);
            go.transform.position = new Vector3(rp.Value.x, go.transform.position.y, rp.Value.y);
            _rallyDead.Remove(ents[i]);
        }
        foreach (var e in _rallyDead)
        {
            if (_rallyMarkers.TryGetValue(e, out var go) && go != null) Destroy(go);
            _rallyMarkers.Remove(e);
        }
        ents.Dispose();
    }

    private UnitView Acquire(int defId, int stableId)
    {
        var def = roster != null ? roster.GetDefinition(defId) : null;
        if (def == null) return null;

        // Obstacles may present one of several meshes, chosen deterministically
        // from the entity's StableId (same entity → same mesh on every client).
        // variant indexes into the pool key so reused views never swap meshes.
        GameObject prefab = def.viewPrefab;
        int variant = 0;
        if (def is ObstacleDefinition obs)
        {
            prefab = obs.ResolveView(stableId);
            if (obs.viewPrefabVariants != null && obs.viewPrefabVariants.Length > 0)
                variant = (int)((uint)stableId % (uint)obs.viewPrefabVariants.Length);
        }
        if (prefab == null) return null;

        // Pool key: distinct meshes of the same def pool separately.
        long key = ((long)defId << 20) ^ (uint)variant;
        if (_pool.TryGetValue(key, out var stack) && stack.Count > 0)
        {
            var reused = stack.Pop();
            reused.gameObject.SetActive(true);
            reused.Bind();
            _typeOf[reused] = defId;
            _poolKeyOf[reused] = key;
            return reused;
        }
        var go = Instantiate(prefab);
        var v = go.GetComponent<UnitView>() ?? go.AddComponent<UnitView>();
        v.Bind();
        go.name = $"UnitView_{def.displayName}";
        _typeOf[v] = defId;
        _poolKeyOf[v] = key;
        return v;
    }

    private void Release(UnitView v)
    {
        if (v == null) return;
        v.gameObject.SetActive(false);
        long key = _poolKeyOf.TryGetValue(v, out var k) ? k : 0;
        if (!_pool.TryGetValue(key, out var stack)) _pool[key] = stack = new Stack<UnitView>();
        stack.Push(v);
    }
}
