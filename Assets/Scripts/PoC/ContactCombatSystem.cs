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

            // If I'm a building, my footprint is a rectangle — attackers reach my
            // EDGE, not my inscribed-circle radius. selfHalf lets the strike/contact
            // range below measure to the footprint so a unit at my long wall lands
            // its hit. Zero for mobile units (they stay circular).
            float2 selfHalf = ObstacleLk.HasComponent(self)
                ? (float2)ObstacleLk[self].Extents * (NavGrid.CellSize * 0.5f)
                : float2.zero;
            float selfEdgeInset = math.max(selfHalf.x, selfHalf.y);   // how far my edge extends past center (worst case)

            // A building is stationary infrastructure: no ramming, and no directional
            // defense. It has no meaningful facing (it doesn't rotate), so shield-arc
            // and backstab bonuses are nonsensical against it — it takes damage FLAT
            // (armor only). iAmBuilding gates both behaviors below.
            bool iAmBuilding = ImmobileLk.HasComponent(self);

            for (int i = 0; i < contacts.Length; i++)
            {
                UnitInfo neighbor = contacts[i];
                if (neighbor.Player == player.Value) continue;
                if (neighbor.IsNonCombatant) continue;   // nodes/neutral buildings can't be attacked

                float2 fromNeighbor = position - neighbor.Position;   // attacker -> me
                float distance = math.length(fromNeighbor);
                if (distance < 1e-4f) { fromNeighbor = new float2(0.01f, 0f); distance = 0.01f; }
                float2 normal = fromNeighbor / distance;

                // --- BODY contact: ramming damage + knockback (radii-based) ---
                // For a building victim, measure the attacker to my footprint EDGE
                // (a rectangle) instead of center + inscribed radius.
                float edgeDist = selfEdgeInset > 0f
                    ? CombatMath.DistanceToFootprint(neighbor.Position, position, selfHalf)
                    : distance;
                float bodyRange = (radius.Value + neighbor.Radius) * BodyContactScale;
                bool touching = edgeDist <= bodyRange;
                if (touching) inContact = true;

                // Ramming is a MOBILE-vs-MOBILE collision (mass × closing speed). A
                // building is stationary infrastructure — it neither rams nor gets
                // rammed. Without this a crowd of units milling next to a wall would
                // pour ram damage into it just by being nearby. Damage to a building
                // comes only from deliberate melee strikes and projectiles (below).
                if (touching && !iAmBuilding)
                {
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
                // If the ATTACKER is a building, its reach starts at its footprint
                // edge (HalfExtents), so a unit at a palisade's wall is in range.
                float attackerReach = neighbor.AttackRange + bodyRange
                    + math.max(neighbor.HalfExtents.x, neighbor.HalfExtents.y);
                if (neighbor.StrikeDamage > 0f &&
                    edgeDist <= attackerReach)
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

                // --- CONTACT DAMAGE from a spike/palisade building I'm touching ---
                // Receiver-side, exactly like the strike above: if the neighbor is a
                // building that deals contact damage, I (the touching unit) take it
                // per second while in contact — no order, no facing. This is the
                // palisade/spike bite. Buildings themselves never take contact/ram
                // damage (they aren't rammed; only real attacks hurt them), so this
                // only ever applies TO units, never to a building victim.
                if (touching && !iAmBuilding && neighbor.ContactDamage > 0f)
                {
                    incoming += CombatMath.MitigateFlat(neighbor.ContactDamage * Dt, defense.Armor);
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
