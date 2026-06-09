using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// BUILDING MANAGER — place/remove obstacles during play. Each building is a
// visual GameObject paired with a lightweight Obstacle ENTITY (what
// ObstacleGridSystem rasterizes). Adding/removing re-blocks the grid, bumps the
// obstacle version, and the flow field recomputes — units route around the new
// layout. Doodads are the same thing placed at edit/bootstrap time.
//
// Controls: B = place under cursor, N = remove nearest.
// Building entities are cleaned up in OnDestroy so they don't outlive the scene.
// ===========================================================================
public class BuildingManager : MonoBehaviour
{
    [Header("Config")]
    [SerializeField] private GameObject buildingPrefab;   // any visual; falls back to a cube
    [Tooltip("Obstacle radius in world units; rasterized into the nav grid.")]
    [SerializeField] private float radius = 2.5f;

    [Header("Debug (runtime, read-only)")]
    public int buildingCount;
    public bool worldReady;

    private EntityManager _em;
    private readonly List<(GameObject go, Entity e)> _buildings = new();

    private void Start()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        worldReady = world != null && world.IsCreated;
        if (worldReady) _em = world.EntityManager;
    }

    private void Update()
    {
        if (!worldReady || _em.World == null || !_em.World.IsCreated) return;
        if (Input.GetKeyDown(KeyCode.B) && GroundPoint(out var p)) Place(p);
        if (Input.GetKeyDown(KeyCode.N) && GroundPoint(out var q)) RemoveNearest(q);
        buildingCount = _buildings.Count;
    }

    private void Place(float2 p)
    {
        var pos = new Vector3(p.x, 0f, p.y);
        var go = buildingPrefab != null
            ? Instantiate(buildingPrefab, pos, Quaternion.identity)
            : GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.position = pos + Vector3.up * 1f;
        go.transform.localScale = new Vector3(radius * 2f, 2f, radius * 2f);

        var e = _em.CreateEntity();
        _em.AddComponentData(e, LocalTransform.FromPosition(pos));
        _em.AddComponentData(e, new Obstacle { Radius = radius });
        _buildings.Add((go, e));
    }

    private void RemoveNearest(float2 p)
    {
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < _buildings.Count; i++)
        {
            if (_buildings[i].go == null) continue;
            var gp = _buildings[i].go.transform.position;
            float d = math.distancesq(p, new float2(gp.x, gp.z));
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return;

        var b = _buildings[best];
        if (b.go != null) Destroy(b.go);
        if (_em.Exists(b.e)) _em.DestroyEntity(b.e);
        _buildings.RemoveAt(best);
    }

    private bool GroundPoint(out float2 p)
    {
        p = default;
        var cam = Camera.main; if (cam == null) return false;
        var ray = cam.ScreenPointToRay(Input.mousePosition);
        if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float enter)) return false;
        var hit = ray.GetPoint(enter);
        p = new float2(hit.x, hit.z);
        return true;
    }

    private void OnDestroy()
    {
        if (!worldReady || _em.World == null || !_em.World.IsCreated) return;
        foreach (var b in _buildings)
            if (_em.Exists(b.e)) _em.DestroyEntity(b.e);
        _buildings.Clear();
    }
}
