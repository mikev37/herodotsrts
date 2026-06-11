using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// BEHAVIOR — hybrid decision layer. Three tiers:
//
//   TIER 1 — DIRECT ORDERS. A player order owns the objective, but maneuver
//     terms still shape HOW the unit travels (formations hold while marching).
//     Far / occluded orders route via the flow field untouched.
//
//   TIER 2 — SURVIVAL INSTINCT. Exclusive, strict priority, IGNORES the
//     maneuver stack — when these fire, nothing else has a vote:
//       RetreatLowHealth  -> flee the enemy center of mass
//       AvoidMelee        -> back off the closest enemy (kite)
//       AttackNearby      -> in weapon range: commit (melee presses, ranged
//                            plants). The CHASE toward a nearby enemy is not
//                            survival — it's navigation, handled in tier 3.
//
//   TIER 3 — MANEUVER. Every enabled behavior contributes a weighted vector
//     and they SUM (consensus stacking) — formations form while advancing
//     instead of competing for control:
//       * Navigation: pursue (AttackNearby beyond weapon range), advance on
//         target / enemy CoM, flank, body-block, stand-behind-friend.
//       * Formation (displacement-based consensus): each term is the summed
//         neighbor displacement error  Σ (x_j + d*_ij − x_i)  toward desired
//         offsets — cardinal lattice over ALL nearby friendlies, wall line,
//         wedge slot. Satisfied constraints fade smoothly (proportional cap),
//         so converged formations stop jittering.
//       * Vicsek alignment: movement consensus (move with the group). Facing
//         consensus lives in the facing channel below.
//       * Spacing: separation push; spacing tightens to CombatSpacing when
//         enemies are near, relaxes to IdleSpacing at rest.
//
//   FACING (independent channel, executed by steering): attacking -> face the
//   target; else AlignFacing -> Vicsek facing consensus (own + neighbors);
//   else the movement heading.
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
            ArriveRadius = 3f,        // global: within this of an order target, the order is released
            Lookahead = 4f,           // global: how far ahead the summed maneuver direction aims
            SlotSoftRadius = 2f,      // global: formation errors fade inside this (consensus settles, no jitter)
            HeightRangeBonus = 0.5f,  // global: extra ranged attack range per meter of height advantage
            FlankDistance = 2.5f,     // global: how far behind the target a flanker aims
            BodyBlockDistance = 2.5f, // global: how far in front of the enemy a blocker stands
            WallForwardOffset = 4f,   // global: the wall line sits this far toward the enemy from friendly CoM

            WeightOrder = 2.5f,       // global: order objective vs maneuver shaping while marching
            WeightPursue = 2.0f,      // global: chase toward an in-aggression enemy
            WeightAdvance = 1.0f,     // global: advance on target / enemy CoM
            WeightFlank = 1.5f,       // global: flank slot pull
            WeightBlock = 1.5f,       // global: body-block slot pull
            WeightBehind = 1.2f,      // global: stand-behind-friend slot pull
            WeightWall = 1.5f,        // global: wall-line consensus pull
            WeightWedge = 1.2f,       // global: wedge slot pull
            WeightCardinal = 1.0f,    // global: cardinal-lattice consensus pull
            WeightAlignMove = 0.8f,   // global: Vicsek movement consensus
            WeightSeparate = 1.0f,    // global: spacing push

            Passable = obstacles.Passable,
            LosRange = 10,            // global: max cells to test LOS; farther -> just use the field
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct BehaviorJob : IJobEntity
    {
        [ReadOnly] public NativeArray<byte> Passable;
        public float ArriveRadius, Lookahead, SlotSoftRadius, HeightRangeBonus;
        public float FlankDistance, BodyBlockDistance, WallForwardOffset;
        public float WeightOrder, WeightPursue, WeightAdvance, WeightFlank, WeightBlock,
                     WeightBehind, WeightWall, WeightWedge, WeightCardinal,
                     WeightAlignMove, WeightSeparate;
        public int LosRange;

        private void Execute(
            in LocalTransform xform,
            in BehaviorFlags baseFlags,
            in BehaviorOverride over,
            in Perception perception,
            in UnitTuning tuning,
            in Attack attack,
            in Ranged ranged,
            in Health health,
            in GroundSpeedMultiplier slope,
            in DynamicBuffer<FriendlyUnit> friendlies,
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

            // ---- target CHOICE (perception offers candidates; behavior decides) --
            target.Has = false;
            if (perception.HasClosestEnemy &&
                math.distance(position, perception.ClosestEnemy.Position) <= tuning.AttackNearbyRange)
            {
                target.Info = perception.ClosestEnemy;
                target.Has = true;
            }
            else if (perception.HasClosestEnemy)
            {
                target.Info = perception.ClosestEnemy;
                target.Has = true;
            }

            float targetDist = target.Has ? math.distance(position, target.Info.Position) : float.MaxValue;
            float2 enemyDir = target.Has
                ? math.normalizesafe(target.Info.Position - position, new float2(0f, 1f))
                : new float2(0f, 1f);

            float engageRange = attack.Range;
            if (ranged.IsRanged && target.Has)
                engageRange += HeightRangeBonus * math.max(0f, slope.Height - target.Info.Height);

            // Spacing tightens for battle, relaxes at rest.
            float spacing = perception.HasEnemies ? tuning.CombatSpacing : tuning.IdleSpacing;

            // =================== TIER 2 — SURVIVAL (exclusive) ===================
            // Checked before orders? No — orders are tier 1 and outrank survival
            // EXCEPT committed engagement, which orders release into naturally.
            // Survival here means: when it fires, the maneuver stack gets no vote.

            float healthFrac = health.Max > 0f ? health.Current / health.Max : 1f;

            // =================== TIER 1 — DIRECT ORDERS ==========================
            if (order.HasTarget)
            {
                bool engage = order.AttackMove && target.Has && Los(position, target.Info.Position);
                if (!engage)
                {
                    float orderDist = math.distance(position, order.Value);
                    if (orderDist > ArriveRadius)
                    {
                        if (!Los(position, order.Value))
                        {
                            // Far / occluded: pure flow routing; formations re-form on arrival.
                            Act(ref dest, position, order.Value);
                        }
                        else
                        {
                            // Visible objective: the order leads, maneuver shapes the march.
                            float2 objective = math.normalizesafe(order.Value - position) * WeightOrder;
                            float2 shaped = objective + Maneuver(E, position, enemyDir, spacing,
                                                                 in perception, in tuning, in friendlies,
                                                                 target.Has, target.Info, targetDist, engageRange,
                                                                 includeNavigation: false);
                            Act(ref dest, position, position + math.normalizesafe(shaped) * Lookahead);
                        }
                        ResolveFacing(E, ref dest, myFacing, enemyDir, in perception);
                        return;
                    }
                    order.HasTarget = false;   // arrived -> release
                }
            }

            if (attackOrder.Has)
            {
                if (target.Has && targetDist > engageRange)
                {
                    float2 objective = enemyDir * WeightOrder;
                    float2 shaped = objective + Maneuver(E, position, enemyDir, spacing,
                                                         in perception, in tuning, in friendlies,
                                                         target.Has, target.Info, targetDist, engageRange,
                                                         includeNavigation: false);
                    Act(ref dest, position, position + math.normalizesafe(shaped) * Lookahead);
                    ResolveFacing(E, ref dest, myFacing, enemyDir, in perception);
                    return;
                }
                attackOrder.Has = false;   // in range (or lost) -> release into survival
            }

            // =================== TIER 2 — SURVIVAL (exclusive) ===================
            if ((E & (uint)BehaviorFlag.RetreatLowHealth) != 0 && perception.HasEnemies &&
                healthFrac < tuning.RetreatHealthPct)
            {
                float2 away = math.normalizesafe(position - perception.EnemyCenter, -enemyDir);
                Act(ref dest, position, position + away * 8f);
                ResolveFacing(E, ref dest, myFacing, enemyDir, in perception);
                return;
            }

            if ((E & (uint)BehaviorFlag.AvoidMelee) != 0 && perception.HasClosestEnemy)
            {
                float closestDist = math.distance(position, perception.ClosestEnemy.Position);
                if (closestDist < tuning.AvoidMeleeRange)
                {
                    float2 away = math.normalizesafe(position - perception.ClosestEnemy.Position, -enemyDir);
                    Act(ref dest, position, position + away * (tuning.AvoidMeleeRange - closestDist));
                    ResolveFacing(E, ref dest, myFacing, enemyDir, in perception);
                    return;
                }
            }

            if ((E & (uint)BehaviorFlag.AttackNearby) != 0 && target.Has &&
                targetDist <= tuning.AttackNearbyRange && targetDist <= engageRange)
            {
                status.IsAttacking = true;
                Attack(ref dest, position, target.Info.Position, enemyDir, ranged.IsRanged);
                return;
            }

            // =================== TIER 3 — MANEUVER (consensus sum) ===============
            float2 summed = Maneuver(E, position, enemyDir, spacing,
                                     in perception, in tuning, in friendlies,
                                     target.Has, target.Info, targetDist, engageRange,
                                     includeNavigation: true);

            if (math.lengthsq(summed) > 0.01f)
                Act(ref dest, position, position + math.normalizesafe(summed) * Lookahead);
            else
                Hold(ref dest);

            ResolveFacing(E, ref dest, myFacing, enemyDir, in perception);
        }

        // The stacked maneuver vector. Every enabled term contributes; they SUM.
        // includeNavigation=false under a direct order (the order IS the
        // navigation; formations/spacing/alignment still shape the march).
        private float2 Maneuver(
            uint E, float2 position, float2 enemyDir, float spacing,
            in Perception perception, in UnitTuning tuning, in DynamicBuffer<FriendlyUnit> friendlies,
            bool hasTarget, in UnitInfo target, float targetDist, float engageRange,
            bool includeNavigation)
        {
            float2 sum = float2.zero;

            if (includeNavigation)
            {
                // Pursue: AttackNearby's chase phase (engagement itself is tier 2).
                if ((E & (uint)BehaviorFlag.AttackNearby) != 0 && hasTarget &&
                    targetDist <= tuning.AttackNearbyRange && targetDist > engageRange)
                    sum += enemyDir * WeightPursue;

                if ((E & (uint)BehaviorFlag.AdvanceIndividual) != 0 && hasTarget)
                    sum += enemyDir * WeightAdvance;

                if ((E & (uint)BehaviorFlag.AdvanceOnEnemy) != 0 && perception.HasEnemies)
                    sum += math.normalizesafe(perception.EnemyCenter - position) * WeightAdvance;

                if ((E & (uint)BehaviorFlag.FlankTarget) != 0 && hasTarget)
                    sum += SlotPull(position, target.Position - target.Facing * FlankDistance) * WeightFlank;

                if ((E & (uint)BehaviorFlag.BodyBlock) != 0 && hasTarget && perception.HasFriendlies)
                {
                    float2 toAllies = math.normalizesafe(perception.FriendlyCenter - target.Position, -enemyDir);
                    sum += SlotPull(position, target.Position + toAllies * BodyBlockDistance) * WeightBlock;
                }

                if ((E & (uint)BehaviorFlag.StandBehindFriend) != 0 &&
                    perception.HasClosestFriendly && perception.HasEnemies)
                {
                    float2 threatDir = math.normalizesafe(
                        perception.EnemyCenter - perception.ClosestFriendly.Position, enemyDir);
                    sum += SlotPull(position, perception.ClosestFriendly.Position - threatDir * spacing)
                           * WeightBehind;
                }
            }

            // ---- Formation: displacement-based consensus terms -------------------
            if ((E & (uint)BehaviorFlag.FormWall) != 0 &&
                perception.HasEnemies && perception.HasFriendlies)
            {
                float2 frontDir = math.normalizesafe(perception.EnemyCenter - perception.FriendlyCenter,
                                                     new float2(0f, 1f));
                float2 anchor = perception.FriendlyCenter + frontDir * WallForwardOffset;
                float2 lateral = new float2(-frontDir.y, frontDir.x);
                float along = math.dot(position - anchor, lateral);
                float slot = math.round(along / spacing) * spacing;
                sum += SlotPull(position, anchor + lateral * slot) * WeightWall;
            }

            if ((E & (uint)BehaviorFlag.FormWedge) != 0 && hasTarget &&
                TryClosestAhead(position, enemyDir, friendlies, out UnitInfo leader))
            {
                float2 lateral = new float2(-enemyDir.y, enemyDir.x);
                float side = math.dot(position - leader.Position, lateral) >= 0f ? 1f : -1f;
                float2 slot = leader.Position - enemyDir * spacing + lateral * (side * spacing);
                sum += SlotPull(position, slot) * WeightWedge;
            }

            // Cardinal lattice as TRUE consensus: sum the displacement error
            // toward the nearest 90-degree slot of EVERY nearby friendly, in each
            // friendly's facing frame:  Σ_j (x_j + d*_ij − x_i) / n.
            if ((E & (uint)BehaviorFlag.AlignCardinal) != 0 && friendlies.Length > 0)
            {
                float2 error = float2.zero; int counted = 0;
                for (int i = 0; i < friendlies.Length; i++)
                {
                    UnitInfo ally = friendlies[i].Info;
                    float2 f = math.normalizesafe(ally.Facing, new float2(0f, 1f));
                    float2 r = new float2(f.y, -f.x);
                    float2 best = default; float bestDist = float.MaxValue;
                    for (int k = 0; k < 4; k++)
                    {
                        float2 axis = k == 0 ? f : k == 1 ? r : k == 2 ? -f : -r;
                        float2 slot = ally.Position + axis * spacing;
                        float d = math.distancesq(position, slot);
                        if (d < bestDist) { bestDist = d; best = slot; }
                    }
                    error += best - position;
                    counted++;
                }
                if (counted > 0)
                    sum += Cap(error / counted) * WeightCardinal;
            }

            // ---- Vicsek movement consensus ---------------------------------------
            if ((E & (uint)BehaviorFlag.AlignMovement) != 0 &&
                math.lengthsq(perception.FriendlyAvgVelocity) > 0.05f)
                sum += math.normalizesafe(perception.FriendlyAvgVelocity) * WeightAlignMove;

            // ---- Spacing ----------------------------------------------------------
            bool separate =
                (E & (uint)BehaviorFlag.Separate) != 0 ||
                ((E & (uint)BehaviorFlag.SeparateIdle) != 0 && !perception.HasEnemies);
            if (separate)
            {
                float spreadRadius = spacing * 1.5f;   // crowding follows the active spacing
                float2 push = float2.zero;
                for (int i = 0; i < friendlies.Length; i++)
                {
                    UnitInfo ally = friendlies[i].Info;
                    float d = math.distance(position, ally.Position);
                    if (d > 0.01f && d < spreadRadius)
                        push += (position - ally.Position) / d * (1f - d / spreadRadius);
                }
                sum += Cap(push) * WeightSeparate;
            }

            return sum;
        }

        // Proportional slot pull: full strength when far, fading to zero as the
        // slot is reached (consensus terms settle instead of oscillating).
        private float2 SlotPull(float2 position, float2 slot)
        {
            float2 toSlot = slot - position;
            float dist = math.length(toSlot);
            if (dist < 1e-3f) return float2.zero;
            return (toSlot / dist) * math.min(1f, dist / SlotSoftRadius);
        }

        private static float2 Cap(float2 v)
        {
            float len = math.length(v);
            return len > 1f ? v / len : v;
        }

        // Facing: attacking -> the target; else AlignFacing -> Vicsek consensus
        // over my own facing plus neighbors'; else unset (steering uses heading).
// ResolveFacing: optionally overrides the movement-heading face that Act()
        // already set, when formation consensus wants a different facing.
        // Only touches dest.Face when AlignFacing is on — Act()'s heading-face
        // is the correct default and is deliberately left in place otherwise.
        private static void ResolveFacing(uint E, ref DesiredDestination dest,
                                          float2 myFacing, float2 enemyDir,
                                          in Perception perception)
        {
            /*
            if ((E & (uint)BehaviorFlag.AlignFacing) != 0 &&
                math.lengthsq(perception.FriendlyAvgFacing) > 1e-4f &&
                !dest.Has)   // don't override an explicit face (order target, enemy, etc.)
            {
                dest.Face = math.normalizesafe(myFacing + perception.FriendlyAvgFacing, myFacing);
                dest.HasFace = true;
            }*/
        }

        // The closest friendly AHEAD of me toward the enemy (wedge leader).
        private static bool TryClosestAhead(float2 position, float2 enemyDir,
                                            in DynamicBuffer<FriendlyUnit> friendlies, out UnitInfo leader)
        {
            leader = default;
            float bestDist = float.MaxValue; int bestId = int.MaxValue; bool found = false;
            for (int i = 0; i < friendlies.Length; i++)
            {
                UnitInfo ally = friendlies[i].Info;
                if (math.dot(ally.Position - position, enemyDir) <= 0f) continue;
                float d = math.distance(position, ally.Position);
                if (d < bestDist || (d == bestDist && ally.StableId < bestId))
                {
                    bestDist = d; bestId = ally.StableId; leader = ally; found = true;
                }
            }
            return found;
        }

        private bool Los(float2 from, float2 to) =>
            NavTerrain.LineOfSight(from, to, Passable, LosRange);

        // Act: move to value, face the direction of travel.
        // UseFlowField is derived from LoS — if the destination isn't directly
        // visible, route through the flow field. Never pass useFlow manually;
        // the answer is always "can I see it?".
        private void Act(ref DesiredDestination d, float2 position, float2 value)
        {
            d.Value = value; d.Has = true; d.UseFlowField = !Los(position, value);
        }

        // Hold: no movement; steering keeps current facing.
        private static void Hold(ref DesiredDestination d)
        {
            d.Has = false; d.UseFlowField = false;
            d.HasFace = false;
        }

        // Attack: the unit is committed to fighting its target.
        //   Melee:  press toward the target, routing through the flow field if
        //           the target is behind an obstacle. Facing = enemy direction.
        //   Ranged: plant in place and face the enemy.
        // Caller sets IsAttacking = true.
        private void Attack(ref DesiredDestination d, float2 position,
                            float2 targetPos, float2 enemyDir, bool isRanged)
        {
            if (isRanged)
            {
                d.Has = false; d.UseFlowField = false;
            }
            else
            {
                d.Value = targetPos; d.Has = true; d.UseFlowField = !Los(position, targetPos);
            }
            d.Face = enemyDir; d.HasFace = true;
        }
    }
}
