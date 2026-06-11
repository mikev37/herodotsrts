using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ---------------------------------------------------------------------------
// SPATIAL HASH — the heart of scaling. Every frame we bucket all units into a
// grid so any system can find nearby units in O(1) instead of scanning all n.
// This is what lets separation/targeting/kiting run on thousands of units.
//
// Runs first; steering depends on it.
// ---------------------------------------------------------------------------
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct SpatialHashSystem : ISystem
{
    public static int Hash(float2 p, float cellSize)
    {
        int x = (int)math.floor(p.x / cellSize);
        int y = (int)math.floor(p.y / cellSize);
        // Standard large-prime spatial hash.
        return (x * 73856093) ^ (y * 19349663);
    }

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        // Create the singleton that holds the map.
        var e = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(e, new SpatialHash
        {
            Map = default,
            CellSize = 12f,   // ~ a couple of unit-diameters; tune to your scale.
        });
        state.RequireForUpdate<UnitTag>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var query = SystemAPI.QueryBuilder().WithAll<UnitTag, Team>().Build();
        int count = query.CalculateEntityCount();   // upper bound (incl. dead) -> safe capacity

        var hashRef = SystemAPI.GetSingletonRW<SpatialHash>();
        float cellSize = hashRef.ValueRO.CellSize;

        // Rewindable allocator: auto-freed at end of frame, no manual Dispose.
        var map = new NativeParallelMultiHashMap<int, UnitInfo>(
            math.max(count, 1), state.WorldUpdateAllocator);

        var fill = new FillHashJob
        {
            CellSize = cellSize,
            Writer = map.AsParallelWriter(),
        };
        // DETERMINISM: Schedule (single-thread), NOT ScheduleParallel. With a
        // parallel fill, the order of values within each cell's bucket depends on
        // which worker thread inserted first — OS scheduling, different every run.
        // Five systems iterate GetValuesForKey in that order (Steering sums
        // separation floats — non-associative; Targeting breaks ties by it;
        // ContactCombat scans strikes/blocks in it), so parallel fill = lockstep
        // and replay divergence. Single-threaded, insertion order = query chunk
        // order, which is deterministic given identical archetype history.
        // Hundreds of units make this job trivial; if profiling ever demands a
        // parallel fill, consumers must sort each bucket (e.g. by StableId) first.
        state.Dependency = fill.Schedule(state.Dependency);

        hashRef.ValueRW.Map = map;
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct FillHashJob : IJobEntity
    {
        public float CellSize;
        public NativeParallelMultiHashMap<int, UnitInfo>.ParallelWriter Writer;

        private void Execute(Entity entity, in LocalTransform xform, in Team team,
                             in Velocity velocity, in Mass mass, in Health health,
                             in BehaviorFlags flags, in Attack attack, in StableId stableId,
                             in UnitRadius radius, in GroundSpeedMultiplier slope,
                             in CombatStatus status, in CombatTarget target,
                             in Defense defense, in UnitDefId defId)
        {
            float2 position = new float2(xform.Position.x, xform.Position.z);
            float3 forward3 = math.forward(xform.Rotation);
            float2 facing = math.normalizesafe(new float2(forward3.x, forward3.z), new float2(0f, 1f));
            Writer.Add(SpatialHashSystem.Hash(position, CellSize), new UnitInfo
            {
                Entity = entity,
                StableId = stableId.Value,
                DefId = defId.Value,
                Team = team.Value,
                Position = position,
                Height = slope.Height,
                Velocity = velocity.Value,
                Facing = facing,
                Radius = radius.Value,
                Mass = mass.Value,
                Health = health.Current,
                Damage = attack.Damage,
                Armor = defense.Armor,
                Shield = defense.Shield,
                Flags = flags.Value,
                IsAttacking = status.IsAttacking,
                AttackTarget = target.Has ? target.Info.Entity : Entity.Null,
                StrikeDamage = attack.Pulse,
                AttackRange = attack.Range,
                StrikeArcDot = attack.ArcDot,
                Cleave = attack.Cleave,
            });
        }    }
}
