using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ---------------------------------------------------------------------------
// CONTACT COMBAT — replaces the rigidbody-collision math. There is no physics
// engine; we reconstruct "impact force" from data we already have.
//
// Each unit computes the damage it RECEIVES by looking at enemy neighbors that
// are touching it and moving INTO it. Computing on the receiver side means a
// unit only ever writes its OWN components -> the whole thing stays parallel
// and Burst-safe (no cross-entity writes).
//
//   impact      = enemyMass * closingSpeed     (closingSpeed = how fast the
//                 enemy is driving into us along the contact normal)
//   damage      = ContactDps * dt   (baseline melee) + ImpactScale * impact
//   knockback   = away from the attacker, scaled by impact / ownMass
//
// Because a downhill unit moves faster (GroundSpeedMultiplier > 1), its closing
// speed is higher, so it deals more impact damage automatically. That's your
// "downhill damage buff" — emergent, not a special case. Same mechanism gives a
// charging hero ability its punch: charge = high commanded velocity = big impact.
// ---------------------------------------------------------------------------
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SteeringSystem))]
public partial struct ContactCombatSystem : ISystem
{
    [BurstCompile]
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
            .CreateCommandBuffer(state.WorldUnmanaged)
            .AsParallelWriter();

        var job = new CombatJob
        {
            Dt = SystemAPI.Time.DeltaTime,
            Map = hash.Map,
            CellSize = hash.CellSize,
            ImpactScale = 6f,        // global: ramming-impact damage multiplier
            KnockbackScale = 0.4f,   // global: knockback strength
            Ecb = ecb,
        };
        state.Dependency = job.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct CombatJob : IJobEntity
    {
        public float Dt, CellSize, ImpactScale, KnockbackScale;
        [ReadOnly] public NativeParallelMultiHashMap<int, NeighborData> Map;
        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute(
            [ChunkIndexInQuery] int sortKey,
            Entity self,
            ref LocalTransform xform,
            ref Health health,
            ref CombatStatus status,
            ref UnitAnim anim,
            in Velocity vel,
            in UnitRadius radius,
            in Mass mass,
            in Defense defense,
            in Team team)
        {
            float2 pos = new float2(xform.Position.x, xform.Position.z);
            float3 fwd3 = math.forward(xform.Rotation);
            float2 myForward = math.normalizesafe(new float2(fwd3.x, fwd3.z), new float2(0f, 1f));
            float incoming = 0f;
            float2 knockback = float2.zero;
            bool inContact = false;

            int cx = (int)math.floor(pos.x / CellSize);
            int cy = (int)math.floor(pos.y / CellSize);

            for (int ox = -1; ox <= 1; ox++)
            for (int oy = -1; oy <= 1; oy++)
            {
                int key = ((cx + ox) * 73856093) ^ ((cy + oy) * 19349663);
                if (!Map.TryGetFirstValue(key, out var n, out var it)) continue;
                do
                {
                    if (n.Team == team.Value || n.Entity == self) continue;

                    float2 d = pos - n.Position;            // from attacker to me
                    float dist = math.length(d);
                    float contactRange = (radius.Value + n.MeleeRange) * 2.2f;
                    if (dist > contactRange || dist < 1e-4f) continue;

                    inContact = true;
                    float2 normal = d / dist;

                    // How fast the enemy is driving into us along the normal.
                    float closing = math.max(0f, math.dot(n.Velocity - vel.Value, -normal));

                    float impact = n.Mass * closing;
                    // Continuous velocity/impact damage (unchanged, no facing).
                    incoming += ImpactScale * impact * Dt;

                    // Discrete melee bash: lands only if I'm inside the attacker's
                    // strike arc (normal points from attacker to me) AND no other
                    // body stands between us — so a spear/long weapon hits the
                    // FIRST unit in line, not everyone behind it. Damage is
                    // mitigated by my armor/shield/backstab (toThreat = -normal).
                    if (n.StrikeDamage > 0f && math.dot(n.Forward, normal) >= n.StrikeArcDot &&
                        !StrikeBlocked(pos, n.Position, n, self, radius.Value, cx, cy))
                        incoming += CombatMath.Mitigate(n.StrikeDamage, myForward, -normal,
                                                        defense.Armor, defense.Shield);
                    knockback += normal * (impact * KnockbackScale / mass.Value);
                }
                while (Map.TryGetNextValue(out n, ref it));
            }

            status.InContactWithEnemy = inContact;

            // Apply knockback directly to position (steering already ran).
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

        // True if some other unit stands between the attacker and me — a body in
        // the path of the strike. Scans the same 3x3 neighborhood; cheap because
        // it only runs on the rare frames a neighbor actually has a strike pulse.
        private bool StrikeBlocked(float2 me, float2 attacker, NeighborData adata, Entity self,
                                   float halfWidth, int cx, int cy)
        {
            float2 ad = me - attacker;
            float adLen2 = math.lengthsq(ad);
            if (adLen2 < 1e-4f) return false;
            float hw2 = halfWidth * halfWidth;

            for (int ox = -1; ox <= 1; ox++)
            for (int oy = -1; oy <= 1; oy++)
            {
                int key = ((cx + ox) * 73856093) ^ ((cy + oy) * 19349663);
                if (!Map.TryGetFirstValue(key, out var b, out var it)) continue;
                do
                {
                    if (b.Entity == self || b.Entity == adata.Entity) continue;
                    if (b.Team == adata.Team) continue;
                    float2 ab = b.Position - adata.Position;
                    float t = math.dot(ab, ad) / adLen2;        // position along attacker->me (0..1)
                    if (t <= 0.1f || t >= 0.9f) continue;        // must be BETWEEN, not at an endpoint
                    float2 perp = ab - ad * t;                   // lateral distance from the strike line
                    if (math.lengthsq(perp) < hw2) return true;  // a body is in the way
                }
                while (Map.TryGetNextValue(out b, ref it));
            }
            return false;
        }
    }
}
