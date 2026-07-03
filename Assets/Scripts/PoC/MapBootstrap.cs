using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

// ===========================================================================
// MAP BOOTSTRAP — one per placement. Drop this on an empty GameObject, move it
// where you want, and its transform.position is the spawn origin. Author the map
// by scattering these; each spawns one thing or a shaped group of things.
//
// SHAPES: a single point, a filled/outline grid, a line, or a circle/ring — with
// optional deterministic jitter for a natural scatter (a forest, a crowd). Jitter
// is seeded from the transform position, so it is IDENTICAL on every peer
// (UnityEngine.Random would desync lockstep — never used here).
//
// GIZMOS: each instance is drawn at the definition's real size (unit radius or
// building footprint), colored by owner, and building instances snap to the nav
// grid exactly as they will at spawn — so the Scene view preview matches runtime.
//
// After spawning (host / single-player), the GameObject optionally destroys
// itself to keep the scene clean.
//
// Registers with UnitFactory, which spawns all bootstraps in ONE deterministic
// pass (order, then name, then position) so StableIds match across peers.
// A networked CLIENT skips spawning (world arrives via snapshot).
// ===========================================================================
public class MapBootstrap : MonoBehaviour
{
    public enum Shape { Point, Grid, Line, Circle }

    [Header("What")]
    [Tooltip("Unit, building, resource node, or obstacle definition to spawn here.")]
    public UnitDefinition definition;

    [Tooltip("-1 = neutral (nodes, scenery, obstacles). A player id = an owned starting unit/building.")]
    public int ownerPlayer = -1;

    [Header("Shape")]
    public Shape shape = Shape.Point;

    [Tooltip("How many to spawn (Point ignores this and spawns 1).")]
    [Min(1)] public int count = 1;

    [Tooltip("Base spacing between instances (world units).")]
    public float spacing = 3f;

    [Tooltip("GRID: number of columns (rows derived from count). 0 = square-ish.")]
    [Min(0)] public int gridColumns = 0;

    [Tooltip("LINE: direction in degrees (0 = +X, 90 = +Z).")]
    public float lineAngle = 0f;

    [Tooltip("CIRCLE: radius (world units). Instances are spread around the ring.")]
    public float circleRadius = 8f;

    [Tooltip("CIRCLE: if true, only the ring; if false, fill the disc.")]
    public bool circleRingOnly = true;

    [Header("Naturalism")]
    [Tooltip("Max random offset per instance (world units), for a natural scatter. " +
             "Deterministic (seeded from position) — safe for lockstep.")]
    public float jitter = 0f;

    [Header("Ordering / cleanup")]
    [Tooltip("Deterministic spawn priority across ALL bootstraps (lower spawns first → lower StableIds). " +
             "Ties broken by name then position. Leave 0 unless something must exist before something else.")]
    public int order = 0;

    [Tooltip("Destroy this GameObject after it has spawned its instances (keeps the scene uncluttered). " +
             "Only happens on host / single-player; a client keeps it (harmless, does nothing).")]
    public bool destroyAfterSpawn = true;

    private void OnEnable()  => UnitFactory.RegisterBootstrap(this);
    private void OnDisable() => UnitFactory.UnregisterBootstrap(this);

    // Spawns this placement. Called only by UnitFactory, in deterministic order.
    // Returns the number of entities created.
    internal int Spawn(RosterDefinition roster)
    {
        if (definition == null) return 0;
        int id = roster.GetId(definition);
        if (id < 0)
        {
            Debug.LogError($"[MapBootstrap:{name}] '{definition.name}' is not in the Roster asset.", this);
            return 0;
        }

        int made = 0;
        var rng = MakeRng();
        foreach (var local in Offsets())
        {
            Vector3 p = transform.position + local + JitterOffset(ref rng);
            p = SnapForDefinition(p);
            UnitFactory.Instance.Create(definition, id, ownerPlayer, (float3)p);
            made++;
        }

        if (destroyAfterSpawn && made > 0) Destroy(gameObject);
        return made;
    }

    // ---- shape math (shared by Spawn and the gizmo, so preview == runtime) ----

    private IEnumerable<Vector3> Offsets()
    {
        int n = shape == Shape.Point ? 1 : Mathf.Max(1, count);
        switch (shape)
        {
            case Shape.Point:
                yield return Vector3.zero;
                break;

            case Shape.Grid:
            {
                int cols = gridColumns > 0 ? gridColumns : Mathf.CeilToInt(Mathf.Sqrt(n));
                float w = (cols - 1) * spacing;
                int rows = Mathf.CeilToInt(n / (float)cols);
                float h = (rows - 1) * spacing;
                for (int i = 0; i < n; i++)
                {
                    int cx = i % cols, cz = i / cols;
                    yield return new Vector3(cx * spacing - w * 0.5f, 0f, cz * spacing - h * 0.5f);
                }
                break;
            }

            case Shape.Line:
            {
                float rad = lineAngle * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
                float len = (n - 1) * spacing;
                for (int i = 0; i < n; i++)
                    yield return dir * (i * spacing - len * 0.5f);
                break;
            }

            case Shape.Circle:
            {
                if (circleRingOnly || n == 1)
                {
                    for (int i = 0; i < n; i++)
                    {
                        float a = (i / (float)n) * Mathf.PI * 2f;
                        yield return new Vector3(Mathf.Cos(a) * circleRadius, 0f, Mathf.Sin(a) * circleRadius);
                    }
                }
                else
                {
                    // Fill the disc with a sunflower (phyllotaxis) distribution — even, natural.
                    float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
                    for (int i = 0; i < n; i++)
                    {
                        float r = circleRadius * Mathf.Sqrt((i + 0.5f) / n);
                        float a = i * golden;
                        yield return new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    }
                }
                break;
            }
        }
    }

    // Deterministic RNG seeded from position (stable across peers, unlike UnityEngine.Random).
    private Unity.Mathematics.Random MakeRng()
    {
        var pos = transform.position;
        uint seed = math.hash(new int3(
            (int)math.round(pos.x * 100f),
            (int)math.round(pos.y * 100f),
            (int)math.round(pos.z * 100f)));
        return new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
    }

    private Vector3 JitterOffset(ref Unity.Mathematics.Random rng)
    {
        if (jitter <= 0f) return Vector3.zero;
        float2 j = rng.NextFloat2(new float2(-jitter, -jitter), new float2(jitter, jitter));
        return new Vector3(j.x, 0f, j.y);
    }

    // Buildings/nodes snap to the nav grid exactly as UnitFactory.Create will,
    // so the preview matches; units spawn at the raw point.
    private Vector3 SnapForDefinition(Vector3 p)
    {
        if (definition is BuildingDefinition b)
        {
            int2 ext = new int2(Mathf.Max(1, b.footprintX), Mathf.Max(1, b.footprintZ));
            int2 min = BuildingFootprint.MinCell(new float2(p.x, p.z), ext);
            float2 c = BuildingFootprint.SnappedCenter(min, ext);
            return new Vector3(c.x, p.y, c.y);
        }
        return p;
    }

    // ---- gizmo: real-size, owner-colored, grid-snapped for buildings ----

    private void OnDrawGizmos()
    {
        Color c = ownerPlayer < 0 ? new Color(0.6f, 0.6f, 0.6f, 0.9f)
                : ownerPlayer == 0 ? new Color(0.3f, 0.6f, 1f, 0.9f)
                                   : new Color(1f, 0.5f, 0.3f, 0.9f);
        Gizmos.color = c;

        var rng = MakeRng();
        foreach (var local in Offsets())
        {
            Vector3 p = transform.position + local + JitterOffset(ref rng);
            p = SnapForDefinition(p);
            DrawInstanceGizmo(p, c);
        }

#if UNITY_EDITOR
        if (definition != null)
            UnityEditor.Handles.Label(transform.position + Vector3.up * 2f, $"{definition.name}  x{(shape == Shape.Point ? 1 : count)}");
#endif
    }

    private void DrawInstanceGizmo(Vector3 p, Color c)
    {
        // Best preview: the definition's actual view prefab mesh, tinted by owner.
        if (definition != null && definition.viewPrefab != null && TryDrawPrefabMesh(p, c)) return;

        if (definition is BuildingDefinition b)
        {
            // Real footprint size, snapped — matches the Obstacle stamped at spawn.
            float sx = Mathf.Max(1, b.footprintX) * NavGrid.CellSize;
            float sz = Mathf.Max(1, b.footprintZ) * NavGrid.CellSize;
            Gizmos.DrawWireCube(p + Vector3.up * 0.5f, new Vector3(sx, 1f, sz));
            Gizmos.color = new Color(c.r, c.g, c.b, 0.15f);
            Gizmos.DrawCube(p + Vector3.up * 0.5f, new Vector3(sx, 1f, sz));
            Gizmos.color = c;
        }
        else if (definition != null)
        {
            // Unit: real radius.
            float r = Mathf.Max(0.1f, definition.radius);
            Gizmos.DrawWireSphere(p + Vector3.up * r, r);
        }
        else
        {
            Gizmos.DrawWireCube(p + Vector3.up * 0.5f, Vector3.one);
        }
    }

    // Draws every MeshFilter found on the view prefab at the placement point,
    // tinted with the owner color (semi-transparent), so the Scene preview looks
    // like the real unit/building. Editor-only. Returns false if no mesh found.
    private bool TryDrawPrefabMesh(Vector3 p, Color c)
    {
        var filters = definition.viewPrefab.GetComponentsInChildren<MeshFilter>();
        if (filters == null || filters.Length == 0) return false;

        var tint = new Color(c.r, c.g, c.b, 0.5f);
        Gizmos.color = tint;
        var prefabRoot = definition.viewPrefab.transform;
        foreach (var f in filters)
        {
            if (f == null || f.sharedMesh == null) continue;
            // Local transform of this mesh relative to the prefab root, re-based at p.
            var lp = prefabRoot.InverseTransformPoint(f.transform.position);
            var world = p + lp;
            Gizmos.DrawMesh(f.sharedMesh, world, f.transform.rotation, f.transform.lossyScale);
        }
        Gizmos.color = c;
        return true;
    }
}
