using Unity.Burst;
using Unity.Entities;

// ---------------------------------------------------------------------------
// Dead units linger long enough for the view to play the Die clip, then the
// entity is destroyed. The view manager notices the entity is gone and recycles
// its GameObject. DeathTimer was seeded from the unit's deathAnimSeconds.
// ---------------------------------------------------------------------------
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct DeathSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = SystemAPI
            .GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
            .CreateCommandBuffer(state.WorldUnmanaged);

        float dt = SystemAPI.Time.DeltaTime;

        foreach (var (timer, entity) in
                 SystemAPI.Query<RefRW<DeathTimer>>().WithAll<Dead>().WithEntityAccess())
        {
            timer.ValueRW.Seconds -= dt;
            if (timer.ValueRO.Seconds <= 0f)
                ecb.DestroyEntity(entity);
        }
    }
}
