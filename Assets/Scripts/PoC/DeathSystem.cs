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

        // CENTRAL death detection: any unit at <= 0 HP is marked Dead, no matter
        // what reduced its health. Contact and projectile damage also mark Dead at
        // their own sites (harmless duplicate; ECB AddComponent is idempotent),
        // but ability/modifier damage — and any future damage source — only kills
        // through this loop. Without it, modifier-killed units played the Die
        // clip (the view reads health) while every [WithNone(Dead)] sim system
        // kept marching their corpses around at negative HP.
        foreach (var (health, entity) in
                 SystemAPI.Query<RefRO<Health>>().WithAll<UnitTag>().WithNone<Dead>().WithEntityAccess())
        {
            if (health.ValueRO.Current <= 0f)
                ecb.AddComponent<Dead>(entity);
        }

        foreach (var (timer, entity) in
                 SystemAPI.Query<RefRW<DeathTimer>>().WithAll<Dead>().WithEntityAccess())
        {
            timer.ValueRW.Seconds -= dt;
            if (timer.ValueRO.Seconds <= 0f)
                ecb.DestroyEntity(entity);
        }
    }
}
