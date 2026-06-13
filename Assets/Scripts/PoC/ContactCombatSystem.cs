using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ---------------------------------------------------------------------------
// CONTACT COMBAT — resolves physical contact and melee strikes, receiver-side
// (each unit only writes its OWN components -> parallel and Burst-safe).
//
// It iterates the unit's ContactList — the SAME per-tick neighbor snapshot
// Steering uses for separation (filled once by InformationGatherSystem) — so
// physics and combat can never disagree about who is touching whom.
//
//   * BODY contact (radii-based, any range weapon irrelevant): continuous
//     ramming damage from mass * closing speed, plus knockback. (Previously
//     contact range scaled with WEAPON reach, so archers "rammed" from 20m.)
//   * MELEE strikes: an attacker's published state decides who gets hit — the
//     strike lands on the unit the attacker DECLARED as its target
//     (UnitInfo.AttackTarget), or, for cleave attackers, on everyone inside
//     the strike arc. Either way a body standing between attacker and victim
//     blocks the hit (long weapons hit the first unit in line).
//   * PROJECTILE hits: iterates the unit's IncomingProjectile buffer (filled
//     by InformationGatherSystem from the ProjectileHash). Receiver-side, same
//     pattern as melee. Marks the projectile Stale; ProjectileCleanupSystem
//     destroys it after this system runs. Two threads may race to set Stale=true
//     on the same projectile — both write the same value, always safe.
//
//   impact    = enemyMass * closingSpeed
//   damage    = ImpactScale * impact * dt  (+ mitigated strike/projectile damage)
//   knockback = away from the rammer, scaled by impact / ownMass
//
// Downhill units close faster -> hit harder. Emergent, not special-cased.
// ---------------------------------------------------------------------------
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SteeringSystem))]
public partial struct ContactCombatSystem : ISystem
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
            .CreateCommandBuffer(state.WorldUnmanaged)
            .AsParallelWriter();

        new CombatJob
        {
            Dt = SystemAPI.Time.DeltaTime,
            ImpactScale = 6f,        // global: ramming-impact damage multiplier
            KnockbackScale = 0.4f,   // global: knockback strength
            BodyContactScale = 1.2f, // global: bodies "touch" within (rA + rB) * this
            ImmobileLk = SystemAPI.GetComponentLookup<Immobile>(true),
            ProjLookup = SystemAPI.GetComponentLookup<Projectile>(false),
            Ecb = ecb,
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct CombatJob : IJobEntity
    {
        public float Dt, ImpactScale, KnockbackScale, BodyContactScale;
        [ReadOnly] public ComponentLookup<Immobile> ImmobileLk;

        // Write Stale on hit projectiles. Race-safe: two threads writing true
        // simultaneously on the same projectile always produce true.
        [NativeDisableParallelForRestriction]
        public ComponentLookup<Projectile> ProjLookup;

        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute(
            [ChunkIndexInQuery] int sortKey,
            Entity self,
            ref LocalTransform xform,
            ref Health health,
            ref CombatStatus status,
            ref UnitAnim anim,
            in Velocity velocity,
            in UnitRadius radius,
            in Mass mass,
            in Defense defense,
            in Team team,
            DynamicBuffer<UnitInfo> contacts,
            DynamicBuffer<IncomingProjectile> incomingProjectiles)
        {
            float2 position = new float2(xform.Position.x, xform.Position.z);
            float3 forward3 = math.forward(xform.Rotation);
            float2 myFacing = math.normalizesafe(new float2(forward3.x, forward3.z), new float2(0f, 1f));
            float incoming = 0f;
            float2 knockback = float2.zero;
            bool inContact = false;

            for (int i = 0; i < contacts.Length; i++)
            {
                UnitInfo neighbor = contacts[i];
                if (neighbor.Team == team.Value) continue;

                float2 fromNeighbor = position - neighbor.Position;   // attacker -> me
                float distance = math.length(fromNeighbor);
                if (distance < 1e-4f) { fromNeighbor = new float2(0.01f, 0f); distance = 0.01f; }
                float2 normal = fromNeighbor / distance;

                // --- BODY contact: ramming damage + knockback (radii-based) ---
                float bodyRange = (radius.Value + neighbor.Radius) * BodyContactScale;
                if (distance <= bodyRange)
                {
                    inContact = true;
                    float closing = math.max(0f, math.dot(neighbor.Velocity - velocity.Value, -normal));
                    float impact = neighbor.Mass * closing;
                    incoming += ImpactScale * impact * Dt;
                    knockback += normal * (impact * KnockbackScale / mass.Value);
                }

                // --- MELEE strike: lands by the attacker's DECLARED state ---
                // Single-target: I am the unit the attacker committed to. Cleave:
                // anyone inside the attacker's strike arc. Both require being
                // within weapon reach and an unblocked line (first body in the
                // way eats a long weapon's hit instead of everyone behind it).
                if (neighbor.StrikeDamage > 0f &&
                    distance <= neighbor.AttackRange + bodyRange)
                {
                    bool targeted = neighbor.AttackTarget == self;
                    bool cleaved = neighbor.Cleave &&
                                   math.dot(neighbor.Facing, normal) >= neighbor.StrikeArcDot;
                    if ((targeted || cleaved) &&
                        !StrikeBlocked(position, neighbor, self, radius.Value, contacts))
                    {
                        incoming += CombatMath.Mitigate(neighbor.StrikeDamage, myFacing, -normal,
                                                        defense.Armor, defense.Shield);
                    }
                }
            }

            // --- PROJECTILE hits (receiver-side) ---
            for (int i = 0; i < incomingProjectiles.Length; i++)
            {
                IncomingProjectile proj = incomingProjectiles[i];
                if (!ProjLookup.HasComponent(proj.Entity)) continue;

                var projComp = ProjLookup[proj.Entity];
                if (projComp.Stale) continue;

                projComp.Stale = true;
                ProjLookup[proj.Entity] = projComp;

                incoming += CombatMath.Mitigate(proj.Damage, myFacing, -proj.Direction,
                                                defense.Armor, defense.Shield);
            }

            status.InContactWithEnemy = inContact;

            // Apply knockback directly to position (steering already ran).
            // Immobile entities (buildings) take the damage but never move.
            if (!ImmobileLk.HasComponent(self))
                xform.Position += new float3(knockback.x, 0f, knockback.y) * Dt;

            if (incoming > 0f)
            {
                health.Current -= incoming;
                if (health.Current <= 0f)
                {
                    anim.State = AnimState.Die;     // view plays the death clip
                    Ecb.AddComponent<Dead>(sortKey, self);
                }
            }
        }

        // True if some other body stands between the attacker and me. Scans my
        // own ContactList (blockers near the victim are the ones on the line);
        // cheap because it only runs on the rare ticks a strike actually pulses.
        private static bool StrikeBlocked(float2 me, in UnitInfo attacker, Entity self,
                                          float halfWidth, in DynamicBuffer<UnitInfo> contacts)
        {
            float2 line = me - attacker.Position;
            float lineLengthSq = math.lengthsq(line);
            if (lineLengthSq < 1e-4f) return false;
            float halfWidthSq = halfWidth * halfWidth;

            for (int i = 0; i < contacts.Length; i++)
            {
                UnitInfo body = contacts[i];
                if (body.Entity == self || body.Entity == attacker.Entity) continue;
                if (body.Team == attacker.Team) continue;
                float2 toBody = body.Position - attacker.Position;
                float t = math.dot(toBody, line) / lineLengthSq;     // position along attacker->me
                if (t <= 0.1f || t >= 0.9f) continue;                 // must be BETWEEN, not at an endpoint
                float2 lateral = toBody - line * t;                   // offset from the strike line
                if (math.lengthsq(lateral) < halfWidthSq) return true;
            }
            return false;
        }
    }
}
