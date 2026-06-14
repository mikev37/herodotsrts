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

            // Height and slope both come from SampleHeight/Gradient, which now
            // return the SURFACE (terrain or wall Roof/Transition) at the source —
            // walls behave exactly like terrain. Sampled ALWAYS (steering snaps y
            // every tick; knockback moves idle units too).
            mul.Height = SampleHeight(pos);

            if (!dest.Has) { mul.Value = 1f; return; }

            float2 heading = math.normalizesafe(dest.Value - pos);

            float2 grad = Gradient(pos);          // points uphill
            float slopeAlong = math.dot(grad, heading);   // >0 climbing, <0 descending

            // Climbing slows you, descending speeds you up. Clamp to sane range.
            mul.Value = math.clamp(1f - SlopeStrength * slopeAlong, 0.4f, 1.8f);
        }

        // Surface height at a world position — terrain, OR a wall's Roof/Transition
        // NavHeight where one is stamped. This is THE height source: mul.Height
        // (the unit's Y) and Gradient (the slope) both come from here, so walls
        // behave exactly like terrain — no separate sampling anywhere else.
        //
        // Nav-height blends bilinearly like terrain, but a corner is excluded from
        // the blend if it's across a sheer ground<->roof boundary from the base
        // cell (a Roof beside a Ground with no Transition). Without that, the
        // blend would invent a slope down a vertical wall face. Cells on the same
        // surface (or bridged by Transition) blend normally, giving the smooth
        // ramp; the sheer face stays a clean step.
        private float SampleHeight(float2 worldPos)
        {
            float terrainH = SampleTerrain(worldPos);
            if (!HasNav) return terrainH;

            int2 baseCell = NavGrid.Cell(worldPos);
            if (!NavGrid.InBounds(baseCell.x, baseCell.y)) return terrainH;
            byte baseType = CellType[NavGrid.Index(baseCell.x, baseCell.y)];

            // On a wall surface (Roof/Transition): height is the nav surface,
            // bilinear over the nav grid.
            if (baseType == NavCell.Roof || baseType == NavCell.Transition)
                return SampleNav(worldPos, baseType, terrainH);

            if (baseType == NavCell.Ground && AdjacentTransition(baseCell))
                return SampleNav(worldPos, NavCell.Transition, terrainH);

            return terrainH;
        }

        // True if a cardinal neighbour of `cell` is a Transition (ramp foot).
        private bool AdjacentTransition(int2 cell)
        {
            return IsTransition(cell.x + 1, cell.y) || IsTransition(cell.x - 1, cell.y)
                || IsTransition(cell.x, cell.y + 1) || IsTransition(cell.x, cell.y - 1);
        }

        private bool IsTransition(int x, int y)
            => NavGrid.InBounds(x, y) && CellType[NavGrid.Index(x, y)] == NavCell.Transition;

        // Terrain-only bilinear (unchanged from before walls existed).
        private float SampleTerrain(float2 worldPos)
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

        // Nav-surface bilinear over the four nav cells around worldPos. A corner
        // that's across a sheer ground<->roof boundary from the unit's cell is
        // excluded (clamped to terrain), so the blend follows the ramp but never
        // invents a slope down a vertical face.
        private float SampleNav(float2 worldPos, byte baseType, float terrainH)
        {
            float2 g = (worldPos - NavGrid.Origin) / NavGrid.CellSize - 0.5f;   // nav cell-center space
            int x0 = (int)math.floor(g.x), y0 = (int)math.floor(g.y);
            float fx = math.saturate(g.x - x0), fy = math.saturate(g.y - y0);
            float h00 = NavCorner(x0,     y0,     baseType, terrainH);
            float h10 = NavCorner(x0 + 1, y0,     baseType, terrainH);
            float h01 = NavCorner(x0,     y0 + 1, baseType, terrainH);
            float h11 = NavCorner(x0 + 1, y0 + 1, baseType, terrainH);
            return math.lerp(math.lerp(h00, h10, fx), math.lerp(h01, h11, fx), fy);
        }

        private float NavCorner(int x, int y, byte baseType, float terrainH)
        {
            if (!NavGrid.InBounds(x, y)) return terrainH;
            byte t = CellType[NavGrid.Index(x, y)];
            if (t != NavCell.Roof && t != NavCell.Transition) return terrainH;   // ground corner
            if (!NavCell.Connected(baseType, t)) return terrainH;                // across a sheer face
            return NavHeight[NavGrid.Index(x, y)];
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
