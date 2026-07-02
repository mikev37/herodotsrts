using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// ATTACK CYCLE — runs the predictable charge-up -> fire -> cooldown loop, but
// ONLY while the unit is committed to attacking (CombatStatus.IsAttacking,
// decided by BehaviorSystem last tick) and has a target.
//
// Breaking off (moving, losing the target, being ordered away) resets the
// cycle to Ready, so every engagement starts from a known state: charge for
// ChargeUp seconds, fire, recover for Cooldown seconds, charge again. No unit
// ever arrives "somewhere in the middle of its attack cycle".
//
// Firing: melee sets Attack.Pulse for exactly one tick (the hash publishes it;
// ContactCombat lands it on the attacker's declared target). Ranged spawns a
// projectile that flies from the shooter's terrain height to the target's,
// with a damage bonus for shooting downhill (// global: below). Units cannot
// move and attack: behavior only sets IsAttacking while holding.
//
// Runs BEFORE SpatialHashSystem so this tick's Pulse is in this tick's hash.
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

        new AttackJob
        {
            Dt = SystemAPI.Time.DeltaTime,
            HeightDamageBonus = 0.05f,   // global: ranged damage bonus per meter of height advantage
            HeightBonusCap = 6f,         // global: height advantage stops counting beyond this (meters)
            Ecb = ecb,
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct AttackJob : IJobEntity
    {
        public float Dt, HeightDamageBonus, HeightBonusCap;
        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute(
            [ChunkIndexInQuery] int sortKey,
            in LocalTransform xform,
            in Player player,
            in Velocity vel,
            in CombatTarget target,
            in Ranged ranged,
            in GroundSpeedMultiplier slope,
            ref Attack attack,
            ref CombatStatus status)
        {
            attack.Pulse = 0f;

            // Not committed (moving, no target, ordered away) -> cycle resets.
            if (!status.IsAttacking || !target.Has)
            {
                attack.Phase = AttackPhase.Ready;
                attack.Timer = 0f;
                return;
            }
            float2 enemydir = target.Info.Position - xform.Position.xz;
            //moving or not facing the enemy = no attack
            if (math.length(vel.Value) > 1 || math.dot(xform.Forward().xz, enemydir) < .65f) return;

            switch (attack.Phase)
            {
                case AttackPhase.Ready:
                    attack.Phase = AttackPhase.Charging;
                    attack.Timer = attack.ChargeUp;
                    goto case AttackPhase.Charging;

                case AttackPhase.Charging:
                    attack.Timer -= Dt;
                    if (attack.Timer <= 0f)
                    {
                        Fire(sortKey, xform, player, target, ranged, slope, ref attack);
                        attack.Phase = AttackPhase.Cooldown;
                        attack.Timer += attack.Cooldown;   // += carries the sub-tick remainder
                    }
                    break;

                case AttackPhase.Cooldown:
                    attack.Timer -= Dt;
                    if (attack.Timer <= 0f)
                    {
                        attack.Phase = AttackPhase.Charging;
                        attack.Timer += attack.ChargeUp;
                    }
                    break;
            }
        }

        private void Fire(int sortKey, in LocalTransform xform, in Player player,
                          in CombatTarget target,
                          in Ranged ranged, in GroundSpeedMultiplier slope, ref Attack attack)
        {
            if (!ranged.IsRanged)
            {
                attack.Pulse = attack.Damage;   // MELEE: strike lands this tick (via ContactCombat)
                return;
            }

            // RANGED: launch an arcing projectile from our terrain height to the
            // target's, with a downhill damage bonus.
            if (attack.ProjSpeed <= 0f) return;

            float2 position = new float2(xform.Position.x, xform.Position.z);
            float2 toTarget = target.Info.Position - position;
            float distance = math.length(toTarget);
            float2 direction = distance > 1e-4f ? toTarget / distance : new float2(0f, 1f);
            float life = distance / attack.ProjSpeed;   // land at the aimed point as the arc completes

            float heightAdvantage = math.clamp(slope.Height - target.Info.Height,
                                               -HeightBonusCap, HeightBonusCap);
            float damage = attack.Damage * math.max(0.5f, 1f + HeightDamageBonus * heightAdvantage);

            float startY = slope.Height + attack.ProjLaunchHeight;
            float endY = target.Info.Height;

            var projectile = Ecb.CreateEntity(sortKey);
            Ecb.AddComponent(sortKey, projectile, LocalTransform.FromPosition(
                new float3(position.x, startY, position.y)));
            Ecb.AddComponent(sortKey, projectile, new Projectile
            {
                Velocity = direction * attack.ProjSpeed,
                Damage = damage,
                Player = player.Value,
                Life = life,
                TotalLife = life,
                Rise = attack.ProjRise,
                StartY = startY,
                EndY = endY,
                HitRadius = attack.ProjHitRadius,
                CollisionHeight = attack.ProjCollisionHeight,
            });
            Ecb.AddComponent<ProjectileTag>(sortKey, projectile);
            Ecb.AddComponent(sortKey, projectile, new ProjectileView { Id = attack.ProjectileId });
        }
    }
}
