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
            ImpactScale = 20f,        // global: ramming-impact damage multiplier
            KnockbackScale = 0.4f,   // global: knockback strength
            BodyContactScale = 1.2f, // global: bodies "touch" within (rA + rB) * this
            ImmobileLk = SystemAPI.GetComponentLookup<Immobile>(true),
            ObstacleLk = SystemAPI.GetComponentLookup<Obstacle>(true),
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
        [ReadOnly] public ComponentLookup<Obstacle> ObstacleLk;

        // Write Stale on hit projectiles. Race-safe: two threads writing true
        // simultaneously on the same projectile always produce true.
        [NativeDisableParallelForRestriction]
        public ComponentLookup<Projectile> ProjLookup;

        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute(
            [ChunkIndexInQuery] int sortKey,
            Entity self,
            ref Health health,
            ref CombatStatus status,
            ref KnockbackVelocity kb,
            ref UnitAnim anim,
            in LocalTransform xform,
            in Velocity velocity,
            in UnitRadius radius,
            in Mass mass,
            in Defense defense,
            in Player player,
            DynamicBuffer<UnitInfo> contacts,
            DynamicBuffer<IncomingProjectile> incomingProjectiles)
        {
            float2 position = new float2(xform.Position.x, xform.Position.z);
            float3 forward3 = math.forward(xform.Rotation);
            float2 myFacing = math.normalizesafe(new float2(forward3.x, forward3.z), new float2(0f, 1f));
            float incoming = 0f;
            float2 knockback = float2.zero;
            bool inContact = false;

            // Am I a building? Decide first — it gates everything below. A building
            // has no facing (no shield-arc/backstab; it takes damage FLAT) and is
            // never rammed. selfHalf (my rectangular footprint) is only meaningful
            // for a building victim, so compute it only then.
            bool iAmBuilding = ImmobileLk.HasComponent(self);
            float2 selfHalf = float2.zero;
            if (iAmBuilding && ObstacleLk.HasComponent(self))
                selfHalf = (float2)ObstacleLk[self].Extents * (NavGrid.CellSize * 0.5f);

            for (int i = 0; i < contacts.Length; i++)
            {
                UnitInfo neighbor = contacts[i];
                if (neighbor.Player == player.Value) continue;
                if (neighbor.IsNonCombatant) continue;   // nodes / neutral obstacles: never a combat source

                float2 fromNeighbor = position - neighbor.Position;   // neighbor -> me
                float distance = math.length(fromNeighbor);
                if (distance < 1e-4f) { fromNeighbor = new float2(0.01f, 0f); distance = 0.01f; }
                float2 normal = fromNeighbor / distance;

                // If I'm a building, measure the neighbor to my footprint EDGE (a
                // rectangle), not my inscribed-circle radius, so a unit at my long
                // wall counts as touching. Mobile units stay circular (selfHalf 0).
                float edgeDist = iAmBuilding
                    ? CombatMath.DistanceToFootprint(neighbor.Position, position, selfHalf)
                    : distance;
                float bodyRange = (radius.Value + neighbor.Radius) * BodyContactScale;
                bool touching = edgeDist <= bodyRange;
                if (touching) inContact = true;

                // --- RAMMING: mobile-vs-mobile only (mass × closing speed) --------
                // Skipped when EITHER side is a building. A building never rams and
                // is never rammed: a unit walking up to a wall must not be flung
                // away, and a crowd milling by a wall must not chip it. Structures
                // are solid obstacles the nav grid routes around; steering/separation
                // keeps units off them. Damage to/from a building is ONLY the melee
                // strike and contact-damage paths below.
                if (touching && !iAmBuilding && !neighbor.IsBuilding)
                {
                    float closing = math.max(0f, math.dot(neighbor.Velocity - velocity.Value, -normal));
                    float impact = neighbor.Mass * closing;
                    incoming += ImpactScale * impact * Dt;
                    knockback += normal * (impact * KnockbackScale / mass.Value);
                }

                // --- CONTACT DAMAGE: a spiked/palisade body I'm touching ----------
                // Any touching enemy that carries ContactDamage deals it per second
                // while in contact — no order, no facing. A palisade's spikes are
                // just ContactDamage > 0; there's no separate "spike" concept. Applies
                // to a building neighbor (palisade) or a thorned unit alike; a building
                // victim still takes none itself (it isn't the one with the value here).
                if (touching && neighbor.ContactDamage > 0f)
                {
                    incoming += iAmBuilding
                        ? CombatMath.MitigateFlat(neighbor.ContactDamage * Dt, defense.Armor)
                        : CombatMath.Mitigate(neighbor.ContactDamage * Dt, myFacing, -normal,
                                              defense.Armor, defense.Shield);
                }

                // --- MELEE strike: lands by the attacker's DECLARED state ---------
                // Single-target: I'm the unit it committed to. Cleave: anyone in its
                // strike arc. Both need weapon reach and an unblocked line. If the
                // attacker is a building (palisade/tower), its reach starts at its
                // own footprint edge (HalfExtents).
                float attackerReach = neighbor.AttackRange + bodyRange
                    + math.max(neighbor.HalfExtents.x, neighbor.HalfExtents.y);
                if (neighbor.StrikeDamage > 0f && edgeDist <= attackerReach)
                {
                    bool targeted = neighbor.AttackTarget == self;
                    bool cleaved = neighbor.Cleave &&
                                   math.dot(neighbor.Facing, normal) >= neighbor.StrikeArcDot;
                    if ((targeted || cleaved) &&
                        !StrikeBlocked(position, neighbor, self, radius.Value, contacts))
                    {
                        incoming += iAmBuilding
                            ? CombatMath.MitigateFlat(neighbor.StrikeDamage, defense.Armor)
                            : CombatMath.Mitigate(neighbor.StrikeDamage, myFacing, -normal,
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

                incoming += iAmBuilding
                    ? CombatMath.MitigateFlat(proj.Damage, defense.Armor)
                    : CombatMath.Mitigate(proj.Damage, myFacing, -proj.Direction,
                                          defense.Armor, defense.Shield);
            }

            status.InContactWithEnemy = inContact;

            // Apply knockback directly to position (steering already ran).
            // Immobile entities (buildings) take the damage but never move.
            if (!ImmobileLk.HasComponent(self))
                kb.Value += knockback; //xform.Position += new float3(knockback.x, 0f, knockback.y) * Dt;

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
                if (body.Player == attacker.Player) continue;
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
