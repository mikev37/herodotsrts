using Unity.Burst;
using Unity.Entities;

// Non-death removal. A unit with Despawn plays its success/vanish anim (set by
// whoever added Despawn), then is destroyed when the timer elapses. Deliberately
// SEPARATE from DeathSystem so death can later grow corpses/loot/drops without
// haulers (or other vanishers) inheriting that behavior.
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct DespawnSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
        => state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                           .CreateCommandBuffer(state.WorldUnmanaged);
        float dt = SystemAPI.Time.DeltaTime;
        foreach (var (d, e) in SystemAPI.Query<RefRW<Despawn>>().WithEntityAccess())
        {
            d.ValueRW.Seconds -= dt;
            if (d.ValueRO.Seconds <= 0f) ecb.DestroyEntity(e);
        }
    }
}
