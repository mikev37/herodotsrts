using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// BEHAVIOR — positional decision layer.
//
// Everything Behavior produces is a DESIRED POSITION, never a velocity or a
// carrot direction. The steering system owns "where we actually end up" and all
// collision avoidance; Behavior only answers "where do I want to be this tick".
//
//   slotWorld = slot.Anchor  +  Offset(shape, slot.Index, …)  +  Scatter(looseness)
//
//     slot        FormationSlot, written FRESH every tick by FormationSystem:
//                 the shared anchor + this unit's index among the LIVING members
//                 + the group size. Attrition is handled there — survivors
//                 re-pack, so the slot a unit reads already accounts for the dead.
//     Offset      the unit's place in the order's fixed frame (Forward/Cols/Shape
//                 all stored on the order), a pure function of index + count.
//     Scatter     a stable per-unit offset seeded by StableId, scaled by the
//                 unit's Looseness — loose units smatter around the ideal slot.
//
// We drive to that exact point (Act) and let steering arrive/decelerate — no
// Lookahead carrot, so no overshoot. Only the directionless flee/kite uses a raw
// direction. Idle does NOT seek the group center: it holds the slot (the
// formation simply stopped advancing) or holds position, so the order is never
// erased and units don't clump.
//
// PRIORITY LADDER (first match wins; see Execute):
//   1 BLOCKED     enemy in reach -> attack it (clears the way; beats orders)
//   2 HARD MOVE   advance in formation to a point; ignores enemies & survival
//   3 HARD ATTACK advance in formation onto an ordered target
//   4 SURVIVAL    retreat / kite — individual, drops formation
//   5 ENGAGE      advance on enemy & take position by ENEMY direction (individual)
//   6 ATTACK-MOVE soft move: advance in formation, but 1/4/5 fire en route
//   7 IDLE        hold the slot (or position); yield out of purposeful movers' lane
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(InformationGatherSystem))]
[UpdateAfter(typeof(StatResolveSystem))]
[UpdateAfter(typeof(FlowFieldSystem))]
public partial struct BehaviorSystem : ISystem
{
    public void OnCreate(ref SystemState state) => state.RequireForUpdate<ObstacleField>();

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var obstacles = SystemAPI.GetSingleton<ObstacleField>();

        new BehaviorJob
        {
            HoldRadius        = 0.4f,  // global: within this of the desired point -> Hold (no creep, no turn)

            PursueGate        = 1f,    // global: (× tuning.PursueDistance) advance only when this close to the enemy
            HeightRangeBonus  = 0.5f,  // global: extra ranged engage range per meter of height advantage
            FlankDistance     = 2.5f,  // global: how far behind a target a flanker stands
            BodyBlockDistance = 2.5f,  // global: how far in front of the enemy a blocker stands
            FleeDistance      = 8f,    // global: retreat carrot length

            WeightYield       = 1.5f,  // global: idle lane-clear nudge, in world units

            CellType = obstacles.CellType,
            LosRange = 20,             // global: max cells to test LOS; farther -> just use the field
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead), typeof(Immobile))]   // Immobile (buildings): no decisions, no movement intent
    private partial struct BehaviorJob : IJobEntity
    {
        [ReadOnly] public NativeArray<byte> CellType;
        public float HoldRadius;
        public float PursueGate, HeightRangeBonus, FlankDistance, BodyBlockDistance, FleeDistance;
        public float WeightYield;
        public int LosRange;


        private void Execute(
            in LocalTransform xform,
            in StableId self,                          // for the looseness scatter seed
            in BehaviorFlags baseFlags,
            in BehaviorOverride over,
            in Perception perception,
            in UnitTuning tuning,
            in Attack attack,
            in Ranged ranged,
            in Health health,
            in GroundSpeedMultiplier slope,
            in FormationSlot slot,                     // maintained each tick by FormationSystem
            in FormationMember member,                 // per-unit design: looseness / aggression
            ref CombatTarget target,
            ref CombatStatus status,
            ref AttackOrder attackOrder,
            ref MoveTarget order,
            ref DesiredDestination dest)
        {
            float2 position = new float2(xform.Position.x, xform.Position.z);
            float3 forward3 = math.forward(xform.Rotation);
            float2 myFacing = math.normalizesafe(new float2(forward3.x, forward3.z), new float2(0f, 1f));
            uint E = BehaviorOverride.Effective(baseFlags.Value, over);

            status.IsAttacking = false;

            // ---- target choice: prefer the ORDERED target, else closest -------
            // The ordered target wins when we can perceive it (matched by Entity
            // against the perception candidates); otherwise we fall back to the
            // closest known enemy so the unit still advances. (Pathing onto an
            // ordered target we CAN'T yet see needs a transform lookup — flagged.)
            target.Has = false;
            if (attackOrder.Has && TryPerceived(in perception, attackOrder.Target, out UnitInfo ordered))
            {
                target.Info = ordered; target.Has = true;
            }
            if (!target.Has && perception.HasClosestEnemy)
            {
                target.Info = perception.ClosestEnemy; target.Has = true;
            }

            float targetDist = target.Has ? math.distance(position, target.Info.Position) : float.MaxValue;
            if (target.Has && target.Info.IsBuilding)
                targetDist = math.max(0f, targetDist - target.Info.Radius);   // reach to the footprint, not the center
            float2 enemyDir = target.Has
                ? math.normalizesafe(target.Info.Position - position, myFacing)
                : myFacing;

            float engageRange = tuning.AttackNearbyRange + attack.Range;
            if (ranged.IsRanged && target.Has)
                engageRange += HeightRangeBonus * math.max(0f, slope.Height - target.Info.Height);

            // engageRange's ONLY job: tighten spacing for battle, relax at rest.
            bool engaged = perception.HasEnemies || (target.Has && targetDist <= engageRange);
            float spread = engaged ? tuning.CombatSpacing : tuning.IdleSpacing;

            float healthFrac = health.Max > 0f ? health.Current / health.Max : 1f;

            // ---- my formation slot (placed by FormationSystem this tick) ------
            // The slot world point = shared anchor + my offset in the order's
            // fixed frame + a stable looseness scatter. No per-tick direction
            // from FriendlyCenter, no rank contest — both are gone.
            bool hasSlot = slot.Has;
            float2 slotWorld = order.Value;
            if (hasSlot)
            {
                float2 sfwd = math.normalizesafe(order.Forward, myFacing);
                float2 sright = new float2(sfwd.y, -sfwd.x);
                float pitch = order.Spacing > 0f ? order.Spacing : spread;
                slotWorld = slot.Anchor
                          + FormationGeometry.Offset((FormationShape)order.Shape, slot.Index, slot.Count,
                                                     math.max(1, order.Cols), sfwd, sright, pitch)
                          + FormationGeometry.Scatter(self.Value, member.Looseness, pitch);
            }

            // ===================================================================
            // 1) BLOCKED — an enemy is in reach. Clear it before anything else;
            //    a melee in our face blocks the order too. (Obstacles are routed
            //    around automatically: Act flips to the flow field on lost LoS.)
            // ===================================================================
            if ((E & (uint)BehaviorFlag.AttackNearby) != 0 && target.Has && targetDist <= engageRange)
            {
                status.IsAttacking = true;
                Attack(ref dest, position, target.Info.Position, enemyDir, ranged.IsRanged);
                return;
            }

            // ===================================================================
            // 2) HARD MOVE ORDER — advance in formation. Ignores enemies and
            //    survival. The slot's anchor (FormationSystem) advances toward the
            //    point and stops there; the unit holds its slot when it arrives,
            //    so the order is NEVER erased and idle = "the formation stopped".
            // ===================================================================
            if (order.HasTarget && !order.AttackMove)
            {
                DriveOrHold(ref dest, position, slotWorld);
                return;
            }

            // ===================================================================
            // 3) HARD ATTACK ORDER — advance in formation onto the ordered target.
            // ===================================================================
            if (attackOrder.Has && target.Has && target.Info.Entity == attackOrder.Target)
            {
                if (targetDist <= engageRange)
                {
                    status.IsAttacking = true;
                    Attack(ref dest, position, target.Info.Position, enemyDir, ranged.IsRanged);
                    return;
                }
                DriveOrHold(ref dest, position, slotWorld);
                return;
            }

            // ===================================================================
            // 4) SURVIVAL — individual; drops formation and nudges entirely.
            // ===================================================================
            if ((E & (uint)BehaviorFlag.RetreatLowHealth) != 0 && perception.HasEnemies &&
                healthFrac < tuning.RetreatHealthPct)
            {
                float2 away = math.normalizesafe(position - perception.EnemyCenter, -enemyDir);
                Act(ref dest, position, position + away * FleeDistance);
                return;
            }

            if ((E & (uint)BehaviorFlag.AvoidMelee) != 0 && perception.HasClosestEnemy)
            {
                float closeDist = math.distance(position, perception.ClosestEnemy.Position);
                if (closeDist < tuning.AvoidMeleeRange)
                {
                    float2 away = math.normalizesafe(position - perception.ClosestEnemy.Position, -enemyDir);
                    Act(ref dest, position, position + away * (tuning.AvoidMeleeRange - closeDist));
                    return;
                }
            }

            // ===================================================================
            // 5) ENGAGEMENT MANEUVER — advance on the enemy and take position by
            //    ENEMY DIRECTION (not movement). Formation-shaped the whole way.
            //    The "goal" (where the formation sits) depends on the unit's role:
            //      BodyBlock -> between the enemy and our own center
            //      Flank     -> behind the target
            //      Advance   -> stand off the enemy center at a held distance
            // ===================================================================
            if (perception.HasEnemies)
            {
                float distToEnemy = math.distance(position, perception.EnemyCenter);
                bool advance =
                    ((E & (uint)BehaviorFlag.AdvanceOnEnemy) != 0 && distToEnemy <= tuning.PursueDistance * PursueGate) ||
                    ((E & (uint)BehaviorFlag.AdvanceIndividual) != 0 && target.Has && targetDist <= tuning.PursueDistance * PursueGate);

                bool wantsPosition = advance ||
                    (E & ((uint)BehaviorFlag.BodyBlock | (uint)BehaviorFlag.FlankTarget)) != 0;

                if (wantsPosition)
                {
                    // Frame faces the enemy mass; standoff keeps ranged at range.
                    float2 efwd = math.normalizesafe(perception.EnemyCenter - position, enemyDir);
                    float standoff = ranged.IsRanged ? math.max(1f, attack.Range * 0.8f) : 0f;

                    float2 goal;
                    if ((E & (uint)BehaviorFlag.BodyBlock) != 0 && target.Has && perception.HasFriendlies)
                    {
                        float2 toAllies = math.normalizesafe(perception.FriendlyCenter - target.Info.Position, -efwd);
                        goal = target.Info.Position + toAllies * BodyBlockDistance;
                    }
                    else if ((E & (uint)BehaviorFlag.FlankTarget) != 0 && target.Has)
                    {
                        goal = target.Info.Position - target.Info.Facing * FlankDistance;
                    }
                    else
                    {
                        goal = perception.EnemyCenter - efwd * standoff;
                    }

                    Act(ref dest, position, goal);   // individual; formation re-forms after combat
                    return;
                }
                // Enemies known but not close enough to commit: fall through to
                // IDLE — hold formation around the group, no blob creep forward.
            }

            // ===================================================================
            // 6) SOFT MOVE ORDER (attack-move) — advance in formation; engagement
            //    above already fired if anything was in reach.
            // ===================================================================
            if (order.HasTarget && order.AttackMove)
            {
                DriveOrHold(ref dest, position, slotWorld);
                return;
            }

            // ===================================================================
            // 7) IDLE — hold the formation slot if there is one (the formation
            //    simply stopped advancing), else hold position. Never seek the
            //    group center. Step out of a purposeful mover's lane.
            // ===================================================================
            float2 idleDest = hasSlot ? slotWorld : position;
            idleDest += YieldNudge(position, in perception, spread);
            DriveOrHold(ref dest, position, idleDest);
        }

        // Idle lane-clear: an idle unit steps perpendicular out of the moving
        // consensus's lane, toward the nearer side. Added in world units.
        private float2 YieldNudge(float2 position, in Perception perception, float spread)
        {
            if (math.lengthsq(perception.FriendlyMovingAvgVelocity) <= 0.05f) return float2.zero;
            float2 moveDir = math.normalizesafe(perception.FriendlyMovingAvgVelocity);
            float2 lateral = new float2(-moveDir.y, moveDir.x);
            float2 fromLane = position - (perception.HasFriendlies ? perception.FriendlyCenter : position);
            float side = math.dot(fromLane, lateral);
            float along = math.dot(fromLane, moveDir);
            float laneHalf = spread * 1.5f;
            if (along <= -spread || math.abs(side) >= laneHalf) return float2.zero;
            float sign = side >= 0f ? 1f : -1f;
            float clear = 1f - math.abs(side) / laneHalf;
            return lateral * (sign * clear * WeightYield);
        }
        // We know the exact desired point, so steer to it (steering arrives and
        // decelerates) — no carrot, no overshoot. At the point already? Hold, so
        // settled formations neither creep nor spin.
        private void DriveOrHold(ref DesiredDestination d, float2 position, float2 destPt)
        {
            if (math.distancesq(position, destPt) < HoldRadius * HoldRadius) Hold(ref d);
            else Act(ref d, position, destPt);
        }

        // Whether one of the perceived enemy candidates IS the ordered target
        // (matched by entity), so the ordered target can be preferred without a
        // component lookup. Misses only when the ordered target is out of
        // perception range (then we advance on the closest until it comes in).
        private static bool TryPerceived(in Perception perception, Entity wanted, out UnitInfo info)
        {
            if (perception.HasClosestEnemy && perception.ClosestEnemy.Entity == wanted) { info = perception.ClosestEnemy; return true; }
            if (perception.HasMostDangerous && perception.MostDangerousEnemy.Entity == wanted) { info = perception.MostDangerousEnemy; return true; }
            if (perception.HasMostExposed && perception.MostExposedEnemy.Entity == wanted) { info = perception.MostExposedEnemy; return true; }
            info = default;
            return false;
        }

        private bool Los(float2 from, float2 to) =>
            NavTerrain.LineOfSight(from, to, CellType, LosRange);

        // Act: desire a position. Facing is NOT set — steering faces the travel
        // direction itself. UseFlowField follows LoS: occluded goal -> route.
        private void Act(ref DesiredDestination d, float2 position, float2 value)
        {
            d.Value = value; d.Has = true; d.UseFlowField = !Los(position, value);
            d.HasFace = false;
        }

        // Hold: no movement, no facing change.
        private static void Hold(ref DesiredDestination d)
        {
            d.Has = false; d.UseFlowField = false; d.HasFace = false;
        }

        // Attack: committed to the target. Melee presses (routing if occluded),
        // ranged plants. Faces the enemy. Caller sets IsAttacking.
        private void Attack(ref DesiredDestination d, float2 position,
                            float2 targetPos, float2 enemyDir, bool isRanged)
        {
            if (isRanged) { d.Has = false; d.UseFlowField = false; }
            else { d.Value = targetPos; d.Has = true; d.UseFlowField = !Los(position, targetPos); }
            d.Face = enemyDir; d.HasFace = true;
        }
    }
}
