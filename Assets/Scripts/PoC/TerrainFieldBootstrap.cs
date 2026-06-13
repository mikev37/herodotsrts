using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// ---------------------------------------------------------------------------
// Samples the active Unity Terrain into the ECS TerrainHeightField once, so the
// Burst SlopeSystem can read elevation without touching managed APIs.
//
// Put this on any GameObject in the (non-Sub) scene. If there's no terrain it
// just leaves the field invalid and the ground is treated as flat.
//
// NativeArray is allocated Persistent and disposed in OnDestroy.
// ---------------------------------------------------------------------------
public class TerrainFieldBootstrap : MonoBehaviour
{
    [SerializeField] private int resolution = 129;   // grid samples per side
    [Tooltip("Nav cells whose sampled terrain height is below this become impassable (water). Set well below the terrain min to disable.")]
    [SerializeField] private float waterLevel = -1000f;

    [Header("Debug (runtime, read-only)")]
    public bool terrainFound;
    public bool fieldValid;

    private NativeArray<float> _heights;
    private Entity _entity = Entity.Null;
    private EntityManager _em;

    private void Start()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) return;
        _em = world.EntityManager;

        var terrain = Terrain.activeTerrain;
        terrainFound = terrain != null;
        _entity = _em.CreateEntity();

        if (terrain == null)
        {
            _em.AddComponentData(_entity, new TerrainHeightField { IsValid = false });
            fieldValid = false;
            return;
        }

        var data = terrain.terrainData;
        Vector3 size = data.size;
        Vector3 origin = terrain.transform.position;
        float worldSize = Mathf.Max(size.x, size.z);

        _heights = new NativeArray<float>(resolution * resolution, Allocator.Persistent);
        for (int y = 0; y < resolution; y++)
        for (int x = 0; x < resolution; x++)
        {
            float u = x / (float)(resolution - 1);
            float v = y / (float)(resolution - 1);
            // GetInterpolatedHeight takes normalized coords; returns world height.
            _heights[y * resolution + x] = data.GetInterpolatedHeight(u, v);
        }

        _em.AddComponentData(_entity, new TerrainHeightField
        {
            Heights = _heights,
            Resolution = resolution,
            WorldSize = worldSize,
            Origin = new float2(origin.x, origin.z),
            WaterLevel = waterLevel,
            IsValid = true,
        });
        fieldValid = true;
    }

    private void OnDestroy()
    {
        // On play-mode exit the ECS world may already be gone; check it via the
        // managed world reference, never through _em (which would throw if its
        // data access is deallocated).
        var world = World.DefaultGameObjectInjectionWorld;
        if (world != null && world.IsCreated && _entity != Entity.Null &&
            world.EntityManager.Exists(_entity))
            world.EntityManager.DestroyEntity(_entity);
        if (_heights.IsCreated) _heights.Dispose();
    }
}
