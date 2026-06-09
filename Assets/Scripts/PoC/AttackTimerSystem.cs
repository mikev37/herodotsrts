using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// ATTACK TIMER — one loop for melee AND ranged: countdown -> act -> cooldown.
// The only difference is the ACT:
//   * melee  -> set Pulse = Damage this frame (rides the hash; ContactCombat
//               applies it, gated by the strike arc + defender mitigation).
//   * ranged -> spawn an arcing Projectile aimed at the target.
//
// Runs BEFORE the spatial hash so a melee pulse set this frame is in NeighborData
// for the defender's contact loop the same frame.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(SpatialHashSystem))]
public partial struct AttackTimerSystem : ISystem
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
            .CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter();

        new AttackJob { Dt = SystemAPI.Time.DeltaTime, Ecb = ecb }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct AttackJob : IJobEntity
    {
        public float Dt;
        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute(
            [ChunkIndexInQuery] int sortKey,
            in LocalTransform xform,
            in Team team,
            in CombatTarget target,
            in Ranged ranged,
            ref Attack atk,
            ref CombatStatus status)
        {
            atk.Cooldown -= Dt;

            float2 pos = new float2(xform.Position.x, xform.Position.z);
            bool engaged = target.Has && math.distance(pos, target.Position) <= atk.Range;
            status.IsFiring = engaged && ranged.IsRanged;   // drives the ranged attack anim

            // Not striking this frame -> make sure no stale melee pulse lingers.
            if (!engaged || atk.Cooldown > 0f || atk.Interval <= 0f) { atk.Pulse = 0f; return; }

            atk.Cooldown = atk.Interval;    // reset (avoids float error building up vs +=)

            if (!ranged.IsRanged)
            {
                atk.Pulse = atk.Damage;     // MELEE: bash this frame
                return;
            }

            // RANGED: launch an arcing projectile toward the target's position now.
            atk.Pulse = 0f;
            if (atk.ProjSpeed <= 0f) return;

            float2 toTarget = target.Position - pos;
            float dist = math.length(toTarget);
            float2 dir = dist > 1e-4f ? toTarget / dist : new float2(0f, 1f);
            float life = dist / atk.ProjSpeed;   // land at the aimed point as the arc reaches 0

            var p = Ecb.CreateEntity(sortKey);
            Ecb.AddComponent(sortKey, p, LocalTransform.FromPosition(
                new float3(pos.x, atk.ProjLaunchHeight, pos.y)));
            Ecb.AddComponent(sortKey, p, new Projectile
            {
                Velocity = dir * atk.ProjSpeed,
                Damage = atk.Damage,
                Team = team.Value,
                Life = life,
                TotalLife = life,
                Rise = atk.ProjRise,
                LaunchHeight = atk.ProjLaunchHeight,
                HitRadius = atk.ProjHitRadius,
                CollisionHeight = atk.ProjCollisionHeight,
            });
            Ecb.AddComponent<ProjectileTag>(sortKey, p);
            Ecb.AddComponent(sortKey, p, new ProjectileView { Id = atk.ProjectileId });
        }
    }
}
