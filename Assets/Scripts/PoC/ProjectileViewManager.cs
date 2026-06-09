using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// PROJECTILE VIEW MANAGER — pools a visual per flying projectile. The prefab is
// PER-UNIT: each projectile carries the firing unit's UnitDefId, and we resolve
// the projectile registry on the UnitManager by id,
// pooling separately per definition. A fallback prefab covers definitions that
// don't set one.
// ===========================================================================
public class ProjectileViewManager : MonoBehaviour
{
    [Header("Config")]
    [Tooltip("The UnitManager holding the roster (used to resolve per-unit projectile prefabs).")]
    [SerializeField] private UnitManager unitManager;
    [Tooltip("Used when a projectile definition has no view prefab assigned.")]
    [SerializeField] private GameObject fallbackPrefab;

    [Header("Debug (runtime, read-only)")]
    public bool worldReady;
    public int activeProjectileViews;

    private EntityManager _em;
    private EntityQuery _query;
    private readonly Dictionary<Entity, Transform> _views = new();
    private readonly Dictionary<int, Stack<Transform>> _pool = new();
    private readonly Dictionary<Transform, int> _typeOf = new();
    private readonly List<Entity> _toRemove = new();

    private void Start()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        worldReady = world != null && world.IsCreated;
        if (!worldReady) { Debug.LogWarning("[ProjectileViewManager] No ECS world found."); return; }
        if (unitManager == null) Debug.LogWarning("[ProjectileViewManager] No UnitManager assigned.");
        _em = world.EntityManager;
        _query = _em.CreateEntityQuery(
            ComponentType.ReadOnly<ProjectileTag>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<ProjectileView>());
    }

    private void LateUpdate()
    {
        if (!worldReady || _em.World == null || !_em.World.IsCreated) return;

        var entities = _query.ToEntityArray(Allocator.Temp);
        var xforms = _query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var views = _query.ToComponentDataArray<ProjectileView>(Allocator.Temp);
        var alive = new HashSet<Entity>();

        for (int i = 0; i < entities.Length; i++)
        {
            var e = entities[i]; alive.Add(e);
            if (!_views.TryGetValue(e, out var t))
            {
                t = Acquire(views[i].Id);
                if (t == null) continue;
                _views[e] = t;
            }
            t.position = xforms[i].Position;
            t.rotation = xforms[i].Rotation;
        }

        _toRemove.Clear();
        foreach (var kv in _views) if (!alive.Contains(kv.Key)) _toRemove.Add(kv.Key);
        foreach (var e in _toRemove) { Release(_views[e]); _views.Remove(e); }

        activeProjectileViews = _views.Count;
        entities.Dispose(); xforms.Dispose(); views.Dispose();
    }

    private Transform Acquire(int id)
    {
        var prefab = unitManager != null ? unitManager.GetProjectileViewPrefab(id) : null;
        if (prefab == null) prefab = fallbackPrefab;
        if (prefab == null) return null;

        if (_pool.TryGetValue(id, out var stack) && stack.Count > 0)
        {
            var reused = stack.Pop();
            reused.gameObject.SetActive(true);
            return reused;
        }
        var t = Instantiate(prefab).transform;
        _typeOf[t] = id;
        return t;
    }

    private void Release(Transform t)
    {
        if (t == null) return;
        t.gameObject.SetActive(false);
        int defId = _typeOf.TryGetValue(t, out var id) ? id : 0;
        if (!_pool.TryGetValue(defId, out var stack)) _pool[defId] = stack = new Stack<Transform>();
        stack.Push(t);
    }
}
