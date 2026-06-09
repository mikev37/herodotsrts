using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// TARGETING — every unit chooses an enemy each frame from the spatial hash.
// Score = distance + weight * health, so units drift toward the nearest AND
// weakest reachable enemy. Computed receiver-side (writes only own component),
// so it parallelizes cleanly. Feeds both melee advance and ranged fire.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SpatialHashSystem))]
public partial struct TargetingSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<SpatialHash>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var hash = SystemAPI.GetSingleton<SpatialHash>();
        if (!hash.Map.IsCreated) return;

        new TargetJob
        {
            Map = hash.Map,
            CellSize = hash.CellSize,
            SearchCells = 4,           // how many hash cells out to look
            HealthWeight = 0.05f,
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct TargetJob : IJobEntity
    {
        [ReadOnly] public NativeParallelMultiHashMap<int, NeighborData> Map;
        public float CellSize, HealthWeight;
        public int SearchCells;

        private void Execute(in LocalTransform xform, in Team team, ref CombatTarget target)
        {
            float2 pos = new float2(xform.Position.x, xform.Position.z);
            int cx = (int)math.floor(pos.x / CellSize);
            int cy = (int)math.floor(pos.y / CellSize);

            float bestScore = float.MaxValue;
            target.Has = false;

            for (int oy = -SearchCells; oy <= SearchCells; oy++)
            for (int ox = -SearchCells; ox <= SearchCells; ox++)
            {
                int key = ((cx + ox) * 73856093) ^ ((cy + oy) * 19349663);
                if (!Map.TryGetFirstValue(key, out var n, out var it)) continue;
                do
                {
                    if (n.Team == team.Value) continue;
                    float dist = math.distance(pos, n.Position);
                    float score = dist + HealthWeight * n.Health;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        target.Value = n.Entity;
                        target.Position = n.Position;
                        target.Has = true;
                    }
                }
                while (Map.TryGetNextValue(out n, ref it));
            }
        }
    }
}
