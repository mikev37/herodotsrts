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

    [Tooltip("The same RosterDefinition asset the UnitFactory uses.")]
    public RosterDefinition roster;

    [Header("Player colors (index = player id)")]
    public Color[] playerColors =
    {
        new Color(0.30f, 0.55f, 1.00f),
        new Color(1.00f, 0.40f, 0.30f),
    };

    [Tooltip("One-shot VFX spawned UNPARENTED at a morph swap, so it survives the prefab changeover " +
             "(an effect parented to either view would be pooled/destroyed with it). Should self-destruct.")]
    public GameObject morphEffectPrefab;

    [Header("Debug (runtime, read-only)")]
    public bool worldReady;
    public int trackedEntities, activeViews, pooledViews;

    private EntityManager _em;
    private EntityQuery _viewQuery;
    private readonly Dictionary<Entity, UnitView> _views = new();
    private readonly Dictionary<int, Stack<UnitView>> _pool = new();
    private readonly Dictionary<UnitView, int> _typeOf = new();
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
        if (roster != null) roster.EnsureBuilt();

        _viewQuery = _em.CreateEntityQuery(
            ComponentType.ReadOnly<UnitTag>(), ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<UnitAnim>(), ComponentType.ReadOnly<Health>(),
            ComponentType.ReadOnly<UnitDefId>(), ComponentType.ReadOnly<Player>());
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

        var alive = new HashSet<Entity>();
        for (int i = 0; i < entities.Length; i++)
        {
            var e = entities[i]; alive.Add(e);

            // swap the view if this entity MORPHED (UnitDefId changed under the same Entity)
            if (_views.TryGetValue(e, out var view) && _typeOf.TryGetValue(view, out var vdef) && vdef != ids[i].Value)
            {
                // detached one-shot VFX at the swap point — survives because it's parented to nothing
                if (morphEffectPrefab != null)
                    Destroy(Instantiate(morphEffectPrefab, xforms[i].Position, xforms[i].Rotation), 4f);
                Release(view); _views.Remove(e); view = null;
            }

            if (view == null && !_views.TryGetValue(e, out view))
            {
                view = Acquire(ids[i].Value);
                if (view == null) continue;
                view.SetPlayerColor(PlayerColor(players[i].Value));
                _views[e] = view;
            }
            var t = view.transform;
            t.position = xforms[i].Position;
            t.rotation = xforms[i].Rotation;
            view.Apply(anims[i].State);
            view.setHP(hps[i].Current);

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
        entities.Dispose(); xforms.Dispose(); anims.Dispose(); hps.Dispose(); ids.Dispose(); players.Dispose();
    }

    private UnitView Acquire(int defId)
    {
        var def = roster != null ? roster.GetDefinition(defId) : null;
        var prefab = def != null ? def.viewPrefab : null;
        if (prefab == null) return null;

        if (_pool.TryGetValue(defId, out var stack) && stack.Count > 0)
        {
            var reused = stack.Pop();
            reused.gameObject.SetActive(true);
            reused.Bind();
            return reused;
        }
        var go = Instantiate(prefab);
        var v = go.GetComponent<UnitView>() ?? go.AddComponent<UnitView>();
        v.Bind();
        go.name = $"UnitView_{def.displayName}";
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
