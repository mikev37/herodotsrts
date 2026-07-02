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
        var e = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(e, new SpatialHash
        {
            Map = default,
            CellSize = 12f,
        });
        state.RequireForUpdate<UnitTag>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var query = SystemAPI.QueryBuilder().WithAll<UnitTag, Player>().Build();
        int count = query.CalculateEntityCount();

        var hashRef = SystemAPI.GetSingletonRW<SpatialHash>();
        float cellSize = hashRef.ValueRO.CellSize;

        var map = new NativeParallelMultiHashMap<int, UnitInfo>(
            math.max(count, 1), state.WorldUpdateAllocator);

        var fill = new FillHashJob
        {
            CellSize       = cellSize,
            Writer         = map.AsParallelWriter(),
            BuildingLk     = SystemAPI.GetComponentLookup<BuildingTag>(true),
            NonCombatantLk = SystemAPI.GetComponentLookup<NonCombatant>(true),
        };
        // DETERMINISM: Schedule (single-thread), NOT ScheduleParallel.
        // With a parallel fill, bucket insertion order depends on OS thread
        // scheduling — different every run. Five systems iterate GetValuesForKey
        // in that order (separation sums are non-associative; targeting and
        // contact-combat break ties by it), so parallel fill = lockstep / replay
        // divergence. Single-threaded, insertion order = query chunk order =
        // deterministic given identical archetype history.
        state.Dependency = fill.Schedule(state.Dependency);

        hashRef.ValueRW.Map = map;
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct FillHashJob : IJobEntity
    {
        public float CellSize;
        public NativeParallelMultiHashMap<int, UnitInfo>.ParallelWriter Writer;
        [ReadOnly] public ComponentLookup<BuildingTag>   BuildingLk;
        [ReadOnly] public ComponentLookup<NonCombatant>  NonCombatantLk;

        private void Execute(Entity entity, in LocalTransform xform, in Player player,
                             in Velocity velocity, in Mass mass, in Health health,
                             in Attack attack, in StableId stableId,
                             in UnitRadius radius, in GroundSpeedMultiplier slope,
                             in CombatStatus status, in CombatTarget target,
                             in Defense defense, in UnitDefId defId)
        {
            float2 position = new float2(xform.Position.x, xform.Position.z);
            float3 forward3 = math.forward(xform.Rotation);
            float2 facing   = math.normalizesafe(new float2(forward3.x, forward3.z), new float2(0f, 1f));

            Writer.Add(SpatialHashSystem.Hash(position, CellSize), new UnitInfo
            {
                Entity          = entity,
                StableId        = stableId.Value,
                DefId           = defId.Value,
                Player          = player.Value,
                Position        = position,
                Height          = slope.Height,
                Velocity        = velocity.Value,
                Facing          = facing,
                Radius          = radius.Value,
                Mass            = mass.Value,
                Health          = health.Current,
                Damage          = attack.Damage,
                Armor           = defense.Armor,
                Shield          = defense.Shield,
                IsAttacking     = status.IsAttacking,
                AttackTarget    = target.Has ? target.Info.Entity : Entity.Null,
                StrikeDamage    = attack.Pulse,
                AttackRange     = attack.Range,
                StrikeArcDot    = attack.ArcDot,
                Cleave          = attack.Cleave,
                IsBuilding      = BuildingLk.HasComponent(entity),
                IsNonCombatant  = NonCombatantLk.HasComponent(entity),
            });
        }
    }
}
