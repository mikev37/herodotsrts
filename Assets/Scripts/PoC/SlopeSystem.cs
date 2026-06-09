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

        var job = new SlopeJob { Field = field, SlopeStrength = 1.5f };
        state.Dependency = job.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct SlopeJob : IJobEntity
    {
        [ReadOnly] public TerrainHeightField Field;
        public float SlopeStrength;

        private void Execute(in LocalTransform xform, in DesiredDestination dest,
                             ref GroundSpeedMultiplier mul)
        {
            if (!dest.Has) { mul.Value = 1f; return; }

            float2 pos = new float2(xform.Position.x, xform.Position.z);
            float2 heading = math.normalizesafe(dest.Value - pos);

            float2 grad = Gradient(pos);          // points uphill
            float slopeAlong = math.dot(grad, heading);   // >0 climbing, <0 descending

            // Climbing slows you, descending speeds you up. Clamp to sane range.
            mul.Value = math.clamp(1f - SlopeStrength * slopeAlong, 0.4f, 1.8f);
            mul.Height = SampleHeight(xform.Position.xz);
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
