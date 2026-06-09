using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// PROJECTILE SIM — move along an arc, expire, and on contact with an enemy unit
// apply mitigated damage and despawn. Firing/cadence lives in AttackTimerSystem;
// this only flies the projectiles that already exist.
//
// Arc: horizontal position advances straight at Velocity; the vertical position
// follows y(u) = LaunchHeight*(1-u) + 4*Rise*u*(1-u), where u = 1 - Life/TotalLife
// goes 0->1 over the flight. So it launches at LaunchHeight, bulges up by ~Rise,
// and lands at 0 right as it reaches the aimed point.
//
// Collision only happens at/below CollisionHeight, so a high arc clears nearer
// units and connects as it comes down. Damage uses the same armor/shield/backstab
// mitigation as melee (CombatMath), with the projectile's travel direction as the
// incoming-hit direction.
//
// Runs on the main thread: applying damage is a cross-entity write (Health on
// another unit), same reason melee stays receiver-side. Fine at modest counts.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ContactCombatSystem))]
public partial struct ProjectileSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<SpatialHash>();
        state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var hash = SystemAPI.GetSingleton<SpatialHash>();
        if (!hash.Map.IsCreated) return;

        var ecb = SystemAPI
            .GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
            .CreateCommandBuffer(state.WorldUnmanaged);
        var healthLookup = SystemAPI.GetComponentLookup<Health>(false);
        var animLookup = SystemAPI.GetComponentLookup<UnitAnim>(false);
        var defenseLookup = SystemAPI.GetComponentLookup<Defense>(true);

        float dt = SystemAPI.Time.DeltaTime;
        float cell = hash.CellSize;

        foreach (var (xform, proj, entity) in
                 SystemAPI.Query<RefRW<LocalTransform>, RefRW<Projectile>>().WithEntityAccess())
        {
            proj.ValueRW.Life -= dt;
            if (proj.ValueRO.Life <= 0f) { ecb.DestroyEntity(entity); continue; }

            // Horizontal advance.
            float2 step = proj.ValueRO.Velocity * dt;
            float3 np = xform.ValueRO.Position + new float3(step.x, 0f, step.y);

            // Vertical arc.
            float total = math.max(proj.ValueRO.TotalLife, 1e-4f);
            float u = math.saturate(1f - proj.ValueRO.Life / total);
            np.y = proj.ValueRO.LaunchHeight * (1f - u) + 4f * proj.ValueRO.Rise * u * (1f - u);

            xform.ValueRW.Position = np;
            xform.ValueRW.Rotation = quaternion.LookRotationSafe(
                new float3(proj.ValueRO.Velocity.x, 0f, proj.ValueRO.Velocity.y), math.up());

            // Only collide once the shot is low enough (lets high arcs clear units).
            if (np.y > proj.ValueRO.CollisionHeight) continue;

            float2 pos = new float2(np.x, np.z);
            float2 dir = math.normalizesafe(proj.ValueRO.Velocity, new float2(0f, 1f));
            int cx = (int)math.floor(pos.x / cell);
            int cy = (int)math.floor(pos.y / cell);
            bool consumed = false;

            for (int oy = -1; oy <= 1 && !consumed; oy++)
            for (int ox = -1; ox <= 1 && !consumed; ox++)
            {
                int key = ((cx + ox) * 73856093) ^ ((cy + oy) * 19349663);
                if (!hash.Map.TryGetFirstValue(key, out var n, out var it)) continue;
                do
                {
                    if (n.Team == proj.ValueRO.Team) continue;
                    if (math.distance(pos, n.Position) > proj.ValueRO.HitRadius) continue;
                    if (!healthLookup.HasComponent(n.Entity)) continue;

                    // Mitigate using the victim's facing vs. where the shot came from
                    // (toThreat = -travel direction). n.Forward is the victim's facing.
                    float armor = 0f, shield = 0f;
                    if (defenseLookup.HasComponent(n.Entity))
                    {
                        var d = defenseLookup[n.Entity];
                        armor = d.Armor; shield = d.Shield;
                    }
                    float dealt = CombatMath.Mitigate(proj.ValueRO.Damage, n.Forward, -dir, armor, shield);

                    var hp = healthLookup[n.Entity];
                    hp.Current -= dealt;
                    healthLookup[n.Entity] = hp;
                    if (hp.Current <= 0f && animLookup.HasComponent(n.Entity))
                    {
                        var a = animLookup[n.Entity]; a.State = AnimState.Die;
                        animLookup[n.Entity] = a;
                        ecb.AddComponent<Dead>(n.Entity);
                    }
                    ecb.DestroyEntity(entity);
                    consumed = true;
                    break;
                }
                while (hash.Map.TryGetNextValue(out n, ref it));
            }
        }
    }
}
