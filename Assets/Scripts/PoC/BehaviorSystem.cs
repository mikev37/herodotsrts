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
//   destination = anchor  +  slotOffset(rank,…)  +  Σ nudges
//
//     anchor      a point SHARED by the whole group — either the order/engage
//                 DESTINATION or the live FriendlyCenter (FrameAnchor switch).
//     slotOffset  this unit's formation slot, a pure function of its RANK in the
//                 shared group roster (GroupMember) and the group size. Rank is
//                 fixed for the life of an order, so the slot never flips and the
//                 grid is stable; the anchor moves smoothly so slots converge.
//     nudges      external desires (separation, idle yield) added DIRECTLY in
//                 world units — "move 3 toward open space", not normalize*weight.
//
// We then drive to that exact point (Act) and let steering arrive/decelerate —
// no Lookahead carrot, so no overshoot. Only the directionless flee/kite uses a
// raw direction.
//
// PRIORITY LADDER (first match wins; see Execute):
//   1 BLOCKED     enemy in reach -> attack it (clears the way; beats orders)
//   2 HARD MOVE   formation march to a point; ignores enemies & survival
//   3 HARD ATTACK formation march onto an ordered target
//   4 SURVIVAL    retreat / kite — individual, drops formation & nudges
//   5 ENGAGE      advance on enemy & take position by ENEMY direction
//   6 ATTACK-MOVE soft move: march to area, but 1/4/5 fire en route
//   7 IDLE        relaxed spacing; yield out of purposeful movers' lane
//
// FrameAnchor (global, configurable): 0 = anchor slots at the DESTINATION,
// 1 = anchor at the live FriendlyCenter. Both are stable because rank is stable;
// they differ in feel (lead-to-goal vs clump-and-merge). More knobs (wide/tall
// bias, formation looseness) layer onto SlotOffset later.
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
            FrameAnchor       = 0,     // global: 0 = anchor slots at DESTINATION, 1 = at FriendlyCenter
            ArriveRadius      = 2f,    // global: within this of an order point, the order releases
            HoldRadius        = 0.4f,  // global: within this of the desired point -> Hold (no creep, no turn)

            PursueGate        = 1f,    // global: (× tuning.PursueDistance) advance only when this close to the enemy
            HeightRangeBonus  = 0.5f,  // global: extra ranged engage range per meter of height advantage
            FlankDistance     = 2.5f,  // global: how far behind a target a flanker stands
            BodyBlockDistance = 2.5f,  // global: how far in front of the enemy a blocker stands
            FleeDistance      = 8f,    // global: retreat carrot length

            WeightSeparate    = 1.0f,  // global: separation nudge, in world units (direct, not normalized*lookahead)
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
        public byte FrameAnchor;
        public float ArriveRadius, HoldRadius;
        public float PursueGate, HeightRangeBonus, FlankDistance, BodyBlockDistance, FleeDistance;
        public float WeightSeparate, WeightYield;
        public int LosRange;

        private void Execute(
            in LocalTransform xform,
            in StableId self,                          // NEW: needed for this unit's formation rank
            in BehaviorFlags baseFlags,
            in BehaviorOverride over,
            in Perception perception,
            in UnitTuning tuning,
            in Attack attack,
            in Ranged ranged,
            in Health health,
            in GroundSpeedMultiplier slope,
            in DynamicBuffer<FriendlyUnit> friendlies,
            in DynamicBuffer<GroupMember> group,       // NEW: shared roster -> stable rank
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

            // ---- this unit's stable formation rank ----------------------------
            bool inFormation = TryRank(in group, self.Value, out int rank, out int count);

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
            // 2) HARD MOVE ORDER — formation march to a point. Ignores enemies
            //    and survival ("move there, no matter what"). Still fully shaped
            //    by the formation slot: it's "advance in formation", not a beeline.
            // ===================================================================
            if (order.HasTarget && !order.AttackMove)
            {
                if (math.distance(position, order.Value) <= ArriveRadius) { order.HasTarget = false; }
                else
                {
                    // Frame = the order's STORED forward (fixed at issue time in
                    // CommandSystem), not a per-tick center direction — so the
                    // grid never rotates as the group nears the point.
                    float2 ofwd = math.normalizesafe(order.Forward, myFacing);
                    DriveFormationFwd(ref dest, position, order.Value, order.Value, ofwd,
                                      in perception, in friendlies, E, spread, rank, count, inFormation);
                    return;
                }
            }

            // ===================================================================
            // 3) HARD ATTACK ORDER — formation march onto the ordered target.
            // ===================================================================
            if (attackOrder.Has && target.Has && target.Info.Entity == attackOrder.Target)
            {
                if (targetDist <= engageRange)
                {
                    status.IsAttacking = true;
                    Attack(ref dest, position, target.Info.Position, enemyDir, ranged.IsRanged);
                    return;
                }
                float standoff = ranged.IsRanged ? math.max(1f, attack.Range * 0.8f) : 0f;
                float2 goal = target.Info.Position - enemyDir * standoff;
                DriveFormation(ref dest, position, goal, target.Info.Position,
                               in perception, in friendlies, E, spread, rank, count, inFormation, myFacing);
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

                    DriveFormationFwd(ref dest, position, goal, perception.EnemyCenter, efwd,
                                      in perception, in friendlies, E, spread, rank, count, inFormation);
                    return;
                }
                // Enemies known but not close enough to commit: fall through to
                // IDLE — hold formation around the group, no blob creep forward.
            }

            // ===================================================================
            // 6) SOFT MOVE ORDER (attack-move) — march to the area; engagement
            //    above already fired if anything was in reach.
            // ===================================================================
            if (order.HasTarget && order.AttackMove)
            {
                if (math.distance(position, order.Value) <= ArriveRadius) { order.HasTarget = false; }
                else
                {
                    float2 ofwd = math.normalizesafe(order.Forward, myFacing);
                    DriveFormationFwd(ref dest, position, order.Value, order.Value, ofwd,
                                      in perception, in friendlies, E, spread, rank, count, inFormation);
                    return;
                }
            }

            // ===================================================================
            // 7) IDLE — relaxed formation around the group, and step out of the
            //    lane of friendlies moving with purpose (inverse body-block).
            // ===================================================================
            float2 idleFwd = GroupForward(myFacing, in perception);
            float2 idleRight = new float2(idleFwd.y, -idleFwd.x);
            float2 idleAnchor = position;

            float2 idleDest = idleAnchor;
            if (inFormation)
                idleDest += SlotOffset(rank, count, E, idleFwd, idleRight, spread);
            idleDest += Nudges(position, in perception, in friendlies, E, spread, yieldOk: true);

            DriveOrHold(ref dest, position, idleDest);
        }

        // ---- formation drive: anchor + slot(travel frame) + nudges ------------
        // frame forward = direction from the group toward the goal, so the grid
        // orients along travel. anchorTarget is what the slot is measured from
        // (destination or live center, per FrameAnchor).
        private void DriveFormation(ref DesiredDestination dest, float2 position,
                                    float2 goal, float2 anchorTarget,
                                    in Perception perception, in DynamicBuffer<FriendlyUnit> friendlies,
                                    uint E, float spread, int rank, int count, bool inFormation, float2 myFacing)
        {
            float2 from = perception.HasFriendlies ? perception.FriendlyCenter : position;
            float2 fwd = math.normalizesafe(goal - from, myFacing);
            DriveFormationFwd(ref dest, position, goal, anchorTarget, fwd,
                              in perception, in friendlies, E, spread, rank, count, inFormation);
        }

        // Same, with an explicit frame forward (engagement faces the enemy).
        private void DriveFormationFwd(ref DesiredDestination dest, float2 position,
                                       float2 goal, float2 anchorTarget, float2 fwd,
                                       in Perception perception, in DynamicBuffer<FriendlyUnit> friendlies,
                                       uint E, float spread, int rank, int count, bool inFormation)
        {
            float2 right = new float2(fwd.y, -fwd.x);
            float2 anchor = Anchor(anchorTarget, in perception);

            float2 destPt = anchor;
            if (inFormation)
                destPt += SlotOffset(rank, count, E, fwd, right, spread);
            destPt += Nudges(position, in perception, in friendlies, E, spread, yieldOk: false);

            DriveOrHold(ref dest, position, destPt);
        }

        // The slot's base point: the shared destination, or the live group center.
        private float2 Anchor(float2 destination, in Perception perception)
            => FrameAnchor == 0
                ? destination
                : (perception.HasFriendlies ? perception.FriendlyCenter : destination);

        // ---- RANK-BASED FORMATION SLOT ----------------------------------------
        // Geometry lives in FormationGeometry so CommandSystem (which assigns
        // units to slots by position at order time) and this placement use the
        // EXACT same offsets. `rank` is the slot this unit was assigned; the
        // shape (bounded grid / wedge) and width come from its flags.
        private float2 SlotOffset(int rank, int count, uint E, float2 fwd, float2 right, float spacing)
        {
            if (count <= 1 || !FormationGeometry.HasFormation(E)) return float2.zero;
            FormationShape shape = FormationGeometry.FromFlags(E);
            int cols = FormationGeometry.Cols(shape, count);
            return FormationGeometry.Offset(shape, rank, count, cols, fwd, right, spacing);
        }

        // External desires, added DIRECTLY in world units (no normalize×lookahead).
        // Separation keeps the slot from piling; idle yield clears purposeful
        // movers' lane. Steering still owns hard collision avoidance.
        private float2 Nudges(float2 position, in Perception perception,
                              in DynamicBuffer<FriendlyUnit> friendlies, uint E, float spread, bool yieldOk)
        {
            float2 n = float2.zero;

            bool separate = (E & (uint)BehaviorFlag.Separate) != 0 ||
                            ((E & (uint)BehaviorFlag.SeparateIdle) != 0 && !perception.HasEnemies);
            if (separate)
                n += Separation(position, in friendlies, spread * 1.5f) * WeightSeparate;

            // Idle units step perpendicular out of the moving consensus's lane.
            if (yieldOk && math.lengthsq(perception.FriendlyMovingAvgVelocity) > 0.05f)
            {
                float2 moveDir = math.normalizesafe(perception.FriendlyMovingAvgVelocity);
                float2 lateral = new float2(-moveDir.y, moveDir.x);
                float2 fromLane = position - (perception.HasFriendlies ? perception.FriendlyCenter : position);
                float side = math.dot(fromLane, lateral);
                float along = math.dot(fromLane, moveDir);
                float laneHalf = spread * 1.5f;
                if (along > -spread && math.abs(side) < laneHalf)
                {
                    float sign = side >= 0f ? 1f : -1f;
                    float clear = 1f - math.abs(side) / laneHalf;
                    n += lateral * sign * clear * WeightYield;
                }
            }
            return n;
        }

        private float2 Separation(float2 position, in DynamicBuffer<FriendlyUnit> friendlies, float radius)
        {
            float2 push = float2.zero;
            for (int i = 0; i < friendlies.Length; i++)
            {
                float2 d = position - friendlies[i].Info.Position;
                float dist = math.length(d);
                if (dist > 0.01f && dist < radius)
                    push += d / dist * (1f - dist / radius);
            }
            return Cap(push) * radius;   // up to one separation radius of clearance
        }

        // ---- drive / hold -----------------------------------------------------
        // We know the exact desired point, so steer to it (steering arrives and
        // decelerates) — no carrot, no overshoot. At the point already? Hold, so
        // settled formations neither creep nor spin.
        private void DriveOrHold(ref DesiredDestination d, float2 position, float2 destPt)
        {
            if (math.distancesq(position, destPt) < HoldRadius * HoldRadius) Hold(ref d);
            else Act(ref d, position, destPt);
        }

        // This unit's rank in the shared roster, and the roster size. Returns
        // false (no formation) when ungrouped or alone. Every unit in the group
        // holds the SAME roster, so all ranks agree -> one coherent formation.
        private static bool TryRank(in DynamicBuffer<GroupMember> group, int selfId, out int rank, out int count)
        {
            count = group.Length;
            for (int i = 0; i < count; i++)
                if (group[i].StableId == selfId) { rank = i; return count > 1; }
            rank = 0;
            return false;
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

        // Shared group orientation for the IDLE frame: movement consensus, else
        // facing consensus, else this unit's OWN facing — never arbitrary north.
        private float2 GroupForward(float2 myFacing, in Perception perception)
        {
            if (math.lengthsq(perception.FriendlyAvgVelocity) > 0.1f)
                return math.normalizesafe(perception.FriendlyAvgVelocity, myFacing);
            if (math.lengthsq(perception.FriendlyAvgFacing) > 0.01f)
                return math.normalizesafe(perception.FriendlyAvgFacing, myFacing);
            return myFacing;
        }

        private static float2 Cap(float2 v)
        {
            float len = math.length(v);
            return len > 1f ? v / len : v;
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
