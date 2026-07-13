using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// PROJECTILE SIM — two passes:
//
//   1. MoveAndHashJob (this system, before InformationGatherSystem):
//      Move each projectile along its arc, build the ProjectileHash singleton
//      so InformationGatherSystem can fill each unit's IncomingProjectile buffer.
//
//   2. Cleanup pass (after ContactCombatSystem):
//      Destroy any projectile marked Stale (hit by a unit receiver-side in
//      ContactCombatSystem) or whose Life has expired.
//
// Hit detection has moved to ContactCombatSystem (receiver-side, parallel),
// matching the melee/contact pattern. Cross-entity Health writes are gone.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SpatialHashSystem))]
[UpdateBefore(typeof(InformationGatherSystem))]
public partial struct ProjectileSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        var e = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(e, new ProjectileHash
        {
            Map = default,
            CellSize = 12f,   // global: match SpatialHash.CellSize
        });
        state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        float dt = SystemAPI.Time.DeltaTime;

        var projQuery = SystemAPI.QueryBuilder()
            .WithAll<ProjectileTag, LocalTransform, Projectile>().Build();
        int count = math.max(projQuery.CalculateEntityCount(), 1);

        var hashRef = SystemAPI.GetSingletonRW<ProjectileHash>();
        // Rewindable allocator: auto-freed each frame, no manual Dispose.
        hashRef.ValueRW.Map = new NativeParallelMultiHashMap<int, IncomingProjectile>(
            count, state.WorldUpdateAllocator);

        // Single-threaded for the same determinism reason as SpatialHashSystem:
        // insertion order into the hash must be stable across runs.
        state.Dependency = new MoveAndHashJob
        {
            Dt       = dt,
            Map      = hashRef.ValueRW.Map,
            CellSize = hashRef.ValueRO.CellSize,
        }.Schedule(state.Dependency);
    }

    [BurstCompile]
    private partial struct MoveAndHashJob : IJobEntity
    {
        public float Dt;
        public float CellSize;
        public NativeParallelMultiHashMap<int, IncomingProjectile> Map;

        private void Execute(Entity entity, ref LocalTransform xform, ref Projectile proj)
        {
            if (proj.Stale) return;

            proj.Life -= Dt;
            if (proj.Life <= 0f) { proj.Stale = true; return; }

            float2 step = proj.Velocity * Dt;
            float3 np   = xform.Position + new float3(step.x, 0f, step.y);

            float total = math.max(proj.TotalLife, 1e-4f);
            float u     = math.saturate(1f - proj.Life / total);
            np.y = math.lerp(proj.StartY, proj.EndY, u)
                 + 4f * proj.Rise * u * (1f - u);

            xform.Position = np;
            xform.Rotation = quaternion.LookRotationSafe(
                new float3(proj.Velocity.x, 0f, proj.Velocity.y), math.up());

            // Only hash projectiles that are low enough to be hittable this frame.
            if (np.y > proj.EndY + proj.CollisionHeight) return;

            float2 pos = new float2(np.x, np.z);
            int key = ((int)math.floor(pos.x / CellSize) * 73856093)
                    ^ ((int)math.floor(pos.y / CellSize) * 19349663);

            Map.Add(key, new IncomingProjectile
            {
                Entity    = entity,
                Position  = pos,
                Velocity  = proj.Velocity,
                Direction = math.normalizesafe(proj.Velocity, new float2(0f, 1f)),
                Damage    = proj.Damage,
                HitRadius = proj.HitRadius,
                Player    = proj.Player,
            });
        }
    }
}

// ===========================================================================
// Destroys projectiles marked Stale (hit) or expired. Runs after
// ContactCombatSystem so all Stale flags from this frame are set.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ContactCombatSystem))]
public partial struct ProjectileCleanupSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = SystemAPI
            .GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
            .CreateCommandBuffer(state.WorldUnmanaged)
            .AsParallelWriter();

        state.Dependency = new CleanupJob { Ecb = ecb }.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    private partial struct CleanupJob : IJobEntity
    {
        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute([ChunkIndexInQuery] int sortKey, Entity entity, in Projectile proj)
        {
            if (proj.Stale)
                Ecb.DestroyEntity(sortKey, entity);
        }
    }
}
