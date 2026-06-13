using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ---------------------------------------------------------------------------
// TERRAIN + SLOPE. Question: "is uphill-slow / downhill-fast a separate
// behavior?" No. Behaviors decide INTENT (where to go). Slope modifies
// EXECUTION (how fast you actually get there). So it's a movement *modifier*,
// not a link in the decision chain. It writes GroundSpeedMultiplier, which the
// steering system multiplies into locomotion.
//
// You can't call Terrain.SampleHeight() from a Burst job, so a bootstrap
// MonoBehaviour bakes the terrain into this flat NativeArray once at startup;
// the job then samples it cheaply in parallel.
// ---------------------------------------------------------------------------
public struct TerrainHeightField : IComponentData
{
    public NativeArray<float> Heights;   // Resolution * Resolution, row-major
    public int Resolution;
    public float WorldSize;              // square side length, world units
    public float2 Origin;                // world XZ of the (min, min) corner
    public float WaterLevel;             // nav cells whose terrain is below this are Impassable (water)
    public bool IsValid;
}

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(BehaviorSystem))]
[UpdateBefore(typeof(SteeringSystem))]
public partial struct SlopeSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        // No terrain baked yet -> everyone stays at multiplier 1 (flat ground).
        if (!SystemAPI.TryGetSingleton<TerrainHeightField>(out var field) || !field.IsValid)
            return;

        // Surface override: roof/transition cells carry their own walk height, so
        // a unit on a wall-top gets the wall's Y (not the terrain under it). Keeps
        // slope.Height — read by combat, projectiles, the hash, the height gate —
        // consistent with where the unit actually stands.
        bool hasNav = SystemAPI.TryGetSingleton<ObstacleField>(out var obstacles);

        var job = new SlopeJob
        {
            Field = field,
            SlopeStrength = 1.5f,
            HasNav = hasNav,
            CellType = hasNav ? obstacles.CellType : default,
            NavHeight = hasNav ? obstacles.NavHeight : default,
        };
        state.Dependency = job.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    [WithNone(typeof(Dead), typeof(Immobile))]   // Immobile (buildings): Height is set once at spawn (footprint max)
    private partial struct SlopeJob : IJobEntity
    {
        [ReadOnly] public TerrainHeightField Field;
        public float SlopeStrength;
        public bool HasNav;
        [ReadOnly] public NativeArray<byte> CellType;
        [ReadOnly] public NativeArray<float> NavHeight;

        private void Execute(in LocalTransform xform, in DesiredDestination dest,
                             ref GroundSpeedMultiplier mul)
        {
            float2 pos = new float2(xform.Position.x, xform.Position.z);

            // Height tracks the surface the unit stands on. Roof/Transition cells
            // override terrain with their baked NavHeight; everything else is
            // terrain. Sampled ALWAYS (steering snaps y every tick, and external
            // forces like knockback move idle units too).
            float surfaceY = SampleHeight(pos);
            if (HasNav)
            {
                int2 c = NavGrid.Cell(pos);
                if (NavGrid.InBounds(c.x, c.y))
                {
                    byte t = CellType[NavGrid.Index(c.x, c.y)];
                    if (t == NavCell.Roof || t == NavCell.Transition)
                        surfaceY = NavHeight[NavGrid.Index(c.x, c.y)];
                }
            }
            mul.Height = surfaceY;

            if (!dest.Has) { mul.Value = 1f; return; }

            float2 heading = math.normalizesafe(dest.Value - pos);

            float2 grad = Gradient(pos);          // points uphill
            float slopeAlong = math.dot(grad, heading);   // >0 climbing, <0 descending

            // Climbing slows you, descending speeds you up. Clamp to sane range.
            mul.Value = math.clamp(1f - SlopeStrength * slopeAlong, 0.4f, 1.8f);
        }

        private float SampleHeight(float2 worldPos)
        {
            float spacing = Field.WorldSize / (Field.Resolution - 1);
            float2 local = (worldPos - Field.Origin) / spacing;
            int x = math.clamp((int)local.x, 0, Field.Resolution - 2);
            int y = math.clamp((int)local.y, 0, Field.Resolution - 2);
            float fx = math.saturate(local.x - x);
            float fy = math.saturate(local.y - y);

            float h00 = Field.Heights[y * Field.Resolution + x];
            float h10 = Field.Heights[y * Field.Resolution + x + 1];
            float h01 = Field.Heights[(y + 1) * Field.Resolution + x];
            float h11 = Field.Heights[(y + 1) * Field.Resolution + x + 1];
            return math.lerp(math.lerp(h00, h10, fx), math.lerp(h01, h11, fx), fy);
        }

        // Central-difference gradient (rise over run) in world units.
        private float2 Gradient(float2 pos)
        {
            float e = Field.WorldSize / (Field.Resolution - 1);
            float dx = (SampleHeight(pos + new float2(e, 0)) -
                        SampleHeight(pos - new float2(e, 0))) / (2f * e);
            float dy = (SampleHeight(pos + new float2(0, e)) -
                        SampleHeight(pos - new float2(0, e))) / (2f * e);
            return new float2(dx, dy);
        }
    }
}
