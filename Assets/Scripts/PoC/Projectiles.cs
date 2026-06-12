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
// interpolates from the SHOOTER's launch height to the TARGET's ground height,
// plus the bulge: y(u) = lerp(StartY, EndY, u) + 4*Rise*u*(1-u), u = 1-Life/TotalLife.
// So shots fired downhill descend onto the target and uphill shots climb to it.
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
            np.y = math.lerp(proj.ValueRO.StartY, proj.ValueRO.EndY, u)
                 + 4f * proj.ValueRO.Rise * u * (1f - u);

            xform.ValueRW.Position = np;
            xform.ValueRW.Rotation = quaternion.LookRotationSafe(
                new float3(proj.ValueRO.Velocity.x, 0f, proj.ValueRO.Velocity.y), math.up());

            // Only collide once the shot is low enough over the DESTINATION
            // terrain (lets high arcs clear nearer units on any slope).
            if (np.y > proj.ValueRO.EndY + proj.ValueRO.CollisionHeight) continue;

            float2 pos = new float2(np.x, np.z);
            float2 dir = math.normalizesafe(proj.ValueRO.Velocity, new float2(0f, 1f));
            int cx = (int)math.floor(pos.x / cell);
            int cy = (int)math.floor(pos.y / cell);
            bool consumed = false;

            for (int oy = -1; oy <= 1 && !consumed; oy++)
            for (int ox = -1; ox <= 1 && !consumed; ox++)
            {
                int key = ((cx + ox) * 73856093) ^ ((cy + oy) * 19349663);
                if (!hash.Map.TryGetFirstValue(key, out var victim, out var iterator)) continue;
                do
                {
                    if (victim.Team == proj.ValueRO.Team) continue;
                    // Buildings have extent: a shot connects at the footprint
                    // surface (inscribed radius), not only at the center.
                    float hitRange = proj.ValueRO.HitRadius + (victim.IsBuilding ? victim.Radius : 0f);
                    if (math.distance(pos, victim.Position) > hitRange) continue;
                    if (!healthLookup.HasComponent(victim.Entity)) continue;

                    // Mitigate using the victim's facing vs. where the shot came from
                    // (toThreat = -travel direction). victim.Facing is the victim's facing.
                    float armor = 0f, shield = 0f;
                    if (defenseLookup.HasComponent(victim.Entity))
                    {
                        var d = defenseLookup[victim.Entity];
                        armor = d.Armor; shield = d.Shield;
                    }
                    float dealt = CombatMath.Mitigate(proj.ValueRO.Damage, victim.Facing, -dir, armor, shield);

                    var hp = healthLookup[victim.Entity];
                    hp.Current -= dealt;
                    healthLookup[victim.Entity] = hp;
                    if (hp.Current <= 0f && animLookup.HasComponent(victim.Entity))
                    {
                        var a = animLookup[victim.Entity]; a.State = AnimState.Die;
                        animLookup[victim.Entity] = a;
                        ecb.AddComponent<Dead>(victim.Entity);
                    }
                    ecb.DestroyEntity(entity);
                    consumed = true;
                    break;
                }
                while (hash.Map.TryGetNextValue(out victim, ref iterator));
            }
        }
    }
}
