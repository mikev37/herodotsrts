using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// BEHAVIOR — hybrid decision layer. Three tiers:
//
//   TIER 1 — DIRECT ORDERS. A player order owns the objective; cohesion terms
//     (formation, spread, alignment, spacing) still shape HOW the unit travels
//     so formations hold while marching. Only enemy-seeking navigation is
//     suppressed (attack-move re-enables engagement).
//
//   TIER 2 — INSTINCT. Exclusive, strict priority — when these fire, the unit
//     breaks cohesion and acts alone:
//       RetreatLowHealth  -> flee the enemy center of mass
//       AvoidMelee        -> back off the closest enemy (kite)
//       AttackNearby      -> in weapon range: commit (melee presses, ranged
//                            plants). In aggression range: break formation
//                            and chase.
//     Units committed to an attack are also EXCLUDED from other units'
//     formation math (slot candidates, wedge leaders) — an instinct-driven
//     unit neither holds formation nor anchors one.
//
//   TIER 3 — MANEUVER. Every enabled behavior contributes a weighted vector
//     and they SUM. If the resulting adjustment is minute (below
//     HoldThreshold), the unit HOLDS instead of micro-stepping — settled
//     formations stop creeping and stop turning.
//       * Navigation: pursue, advance on target / enemy CoM (gated by
//         PursueDistance), flank, body-block, stand-behind-friend.
//       * Cohesion (also active under tier-1 orders): wall LINE (perpendicular
//         depth constraint only — position along the wall is owned by spread/
//         separation), wedge slot, cardinal slot (pick best, seize when close,
//         ignore when far), lateral spread, Vicsek movement consensus, spacing.
//
//   FACING (executed by steering):
//     attacking         -> face the enemy (Attack() writes dest.Face)
//     moving at speed   -> face the movement direction
//     minute adjustment -> Hold; facing stays where it is
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
            ArriveRadius      = 2f,    // global: within this of an order target, the order is released
            Lookahead         = 4f,    // global: how far ahead the summed maneuver direction aims
            HoldThreshold     = 1.7f,  // global: summed maneuver below this magnitude -> Hold (no creep, no turn)
            SlotDeadZone      = 0.2f,  // global: inside this of a slot, the slot is HELD (zero pull)
            SlotCaptureRadius = 1f,    // global: inside this, full-strength pull (seize the slot)
            SlotMaxRange      = 5f,    // global: beyond this, the slot is ignored (not my slot)
            HeightRangeBonus  = 0.5f,  // global: extra ranged attack range per meter of height advantage
            FlankDistance     = 2.5f,  // global: how far behind the target a flanker aims
            BodyBlockDistance = 2.5f,  // global: how far in front of the enemy a blocker stands
            WallForwardOffset = 4f,    // global: the wall line sits this far toward the enemy from friendly CoM

            WeightOrder       = 10f,  // global: order objective vs maneuver shaping while marching
            WeightPursue      = 1.0f,  // global: chase toward an in-aggression enemy
            WeightAdvance     = 2.0f,  // global: advance on target / enemy CoM
            WeightFlank       = 1.5f,  // global: flank slot pull
            WeightBlock       = 1.5f,  // global: body-block slot pull
            WeightBehind      = 1.2f,  // global: stand-behind-friend slot pull
            WeightFrontline   = 1.2f,  // global: stand-frontline slot pull
            WeightWall        = 3.5f,  // global: wall-line depth constraint
            WeightWedge       = 1.2f,  // global: wedge slot pull
            WeightCardinal    = 1.0f,  // global: cardinal-lattice slot pull
            WeightAlignMove   = 0.8f,  // global: Vicsek movement consensus
            WeightSeparate    = 1.0f,  // global: spacing push
            WeightSpreadLat   = 1f,    // global: lateral spread perpendicular to the advance axis
            WeightCohesion    = 1.2f,  // global: pull toward the friendly center when too far
            WeightFollowMov   = 0.8f,  // global: align movement with friendlies that have a target

            Passable = obstacles.Passable,
            LosRange = 20,             // global: max cells to test LOS; farther -> just use the field
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead), typeof(Immobile))]   // Immobile (buildings): no decisions, no movement intent
    private partial struct BehaviorJob : IJobEntity
    {
        [ReadOnly] public NativeArray<byte> Passable;
        public float ArriveRadius, Lookahead, HoldThreshold;
        public float SlotDeadZone, SlotCaptureRadius, SlotMaxRange, HeightRangeBonus;
        public float FlankDistance, BodyBlockDistance, WallForwardOffset;
        public float WeightOrder, WeightPursue, WeightAdvance, WeightFlank, WeightBlock,
                     WeightBehind, WeightFrontline, WeightWall, WeightWedge, WeightCardinal,
                     WeightAlignMove, WeightSeparate, WeightSpreadLat, WeightCohesion, WeightFollowMov;
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

            // ---- target choice: closest known enemy -------------------------
            target.Has = false;
            if (perception.HasClosestEnemy)
            {
                target.Info = perception.ClosestEnemy;
                target.Has  = true;
            }

            float targetDist = target.Has
                ? math.distance(position, target.Info.Position)
                : float.MaxValue;
            // Buildings have extent: range checks run against the footprint
            // surface, or melee can never "reach" a large building's center.
            // (Restored — this adjustment was dropped in the merge.)
            if (target.Has && target.Info.IsBuilding)
                targetDist = math.max(0f, targetDist - target.Info.Radius);
            float2 enemyDir = target.Has
                ? math.normalizesafe(target.Info.Position - position, new float2(0f, 1f))
                : new float2(0f, 1f);

            float engageRange = tuning.AttackNearbyRange + attack.Range;
            if (ranged.IsRanged && target.Has)
                engageRange += HeightRangeBonus * math.max(0f, slope.Height - target.Info.Height);

            // Spacing tightens for battle, relaxes at rest.
            float spacing = perception.HasEnemies ? tuning.CombatSpacing : tuning.IdleSpacing;

            float healthFrac = health.Max > 0f ? health.Current / health.Max : 1f;

            // =================== TIER 1 — DIRECT ORDERS ==========================
            // Cohesion (formations, spread, alignment, spacing) stays active so
            // the group marches as one; only enemy-seeking navigation is
            // suppressed. Attack-move re-enables engagement en route.
            if (order.HasTarget)
            {

                float orderDist = math.distance(position, order.Value);
                if (orderDist > ArriveRadius)
                {
                    if (Los(position, order.Value))
                    {
                        // Goal directly visible: boid-shaped march toward a
                        // lookahead carrot — formation shaping stays active.
                        float2 objective = math.normalizesafe(order.Value - position) * WeightOrder;
                        float2 shaped = objective + Maneuver(E, position, enemyDir, spacing,
                                                                in perception, in tuning, in friendlies, myFacing,
                                                                target.Has, target.Info, targetDist, engageRange,
                                                                includeNavigation: false);
                        Act(ref dest, position, position + math.normalizesafe(shaped) * Lookahead);
                    }
                    else
                    {
                        // Goal occluded (or beyond LoS range): NAVIGATE. The
                        // destination must be the TRUE order point — Act derives
                        // UseFlowField from LoS, and steering only routes when
                        // the goal cell is stable. The lookahead carrot here
                        // disabled pathfinding entirely: a nearby carrot is
                        // almost always visible (UseFlowField = false), and when
                        // it wasn't, its goal cell moved every tick and thrashed
                        // the path slots.
                        Act(ref dest, position, order.Value);
                    }
                    bool engage = order.AttackMove && target.Has && Los(position, target.Info.Position);
                    if(!engage)
                        return;
                }
                else order.HasTarget = false;   // arrived -> release
                
            }

            if (attackOrder.Has)
            {
                if (target.Has && targetDist > engageRange)
                {
                    if (Los(position, target.Info.Position))
                    {
                        float2 objective = enemyDir * WeightOrder;
                        float2 shaped = objective + Maneuver(E, position, enemyDir, spacing,
                                                             in perception, in tuning, in friendlies, myFacing,
                                                             target.Has, target.Info, targetDist, engageRange,
                                                             includeNavigation: false);
                        Act(ref dest, position, position + math.normalizesafe(shaped) * Lookahead);
                    }
                    else
                    {
                        // Same navigation rule as ordered moves: occluded target
                        // -> route to its true position through the flow field.
                        Act(ref dest, position, target.Info.Position);
                    }
                    return;
                }
                attackOrder.Has = false;   // in range (or lost) -> release into instinct
            }

            // =================== TIER 2 — INSTINCT (exclusive) ===================
            if ((E & (uint)BehaviorFlag.RetreatLowHealth) != 0 && perception.HasEnemies &&
                healthFrac < tuning.RetreatHealthPct)
            {
                float2 away = math.normalizesafe(position - perception.EnemyCenter, -enemyDir);
                Act(ref dest, position, position + away * 8f);
                return;
            }

            if ((E & (uint)BehaviorFlag.AvoidMelee) != 0 && perception.HasClosestEnemy)
            {
                float closestDist = math.distance(position, perception.ClosestEnemy.Position);
                if (closestDist < tuning.AvoidMeleeRange)
                {
                    float2 away = math.normalizesafe(position - perception.ClosestEnemy.Position, -enemyDir);
                    Act(ref dest, position, position + away * (tuning.AvoidMeleeRange - closestDist));
                    return;
                }
            }

            if ((E & (uint)BehaviorFlag.AttackNearby) != 0 && target.Has &&
                targetDist <= engageRange)
            {
                // Engage: committed to the attack.
                status.IsAttacking = true;
                Attack(ref dest, position, target.Info.Position, enemyDir, ranged.IsRanged);
                return;
            }
            else if ((E & (uint)BehaviorFlag.AttackNearby) != 0 && target.Has &&
                     targetDist <= tuning.AttackNearbyRange)
            {
                // Chase: break formation and close on the enemy.
                Act(ref dest, position, target.Info.Position);
                return;
            }

            // =================== TIER 3 — MANEUVER (consensus sum) ===============
            float2 summed = Maneuver(E, position, enemyDir, spacing,
                                     in perception, in tuning, in friendlies, myFacing,
                                     target.Has, target.Info, targetDist, engageRange,
                                     includeNavigation: true);

            // Minute adjustment -> Hold. The unit neither creeps nor turns;
            // settled formations stay settled.
            if (math.lengthsq(summed) > HoldThreshold * HoldThreshold)
                Act(ref dest, position, position + math.normalizesafe(summed) * Lookahead);
            else
                Hold(ref dest);
        }

        // The stacked maneuver vector. Every enabled term contributes; they SUM.
        // includeNavigation=false under a direct order: the order IS the
        // navigation, so enemy-seeking terms are suppressed — cohesion terms
        // (wall, wedge, cardinal, spread, alignment, spacing) always apply.
        private float2 Maneuver(
            uint E, float2 position, float2 enemyDir, float spacing,
            in Perception perception, in UnitTuning tuning, in DynamicBuffer<FriendlyUnit> friendlies,
            float2 myFacing,
            bool hasTarget, in UnitInfo target, float targetDist, float engageRange,
            bool includeNavigation)
        {
            float2 sum = float2.zero;

            if (includeNavigation)
            {
                // Advance: march toward the target or enemy CoM, but only within
                // PursueDistance. Beyond that the unit holds and waits for the group.
                if ((E & (uint)BehaviorFlag.AdvanceIndividual) != 0 && hasTarget &&
                    targetDist <= tuning.PursueDistance)
                    sum += enemyDir * WeightAdvance;

                if ((E & (uint)BehaviorFlag.AdvanceOnEnemy) != 0 && perception.HasEnemies)
                {
                    float distToCenter = math.distance(position, perception.EnemyCenter);
                    if (distToCenter <= tuning.PursueDistance)
                        sum += math.normalizesafe(perception.EnemyCenter - position) * WeightAdvance;
                }

                if ((E & (uint)BehaviorFlag.FlankTarget) != 0 && hasTarget)
                    sum += SlotPull(position, target.Position - target.Facing * FlankDistance) * WeightFlank;

                if ((E & (uint)BehaviorFlag.BodyBlock) != 0 && hasTarget && perception.HasFriendlies)
                {
                    float2 toAllies = math.normalizesafe(perception.FriendlyCenter - target.Position, -enemyDir);
                    sum += SlotPull(position, target.Position + toAllies * BodyBlockDistance) * WeightBlock;
                }

                // StandBehindFriend: one slot directly behind each formation
                // ally (opposite the group forward direction). Same pipeline as
                // cardinal: merge overlaps, mark taken, pick best open slot.
                if ((E & (uint)BehaviorFlag.StandBehindFriend) != 0 && friendlies.Length > 0)
                    sum += FormationSlotPull(position, friendlies, GroupForward(enemyDir, in perception),
                                            spacing, offsetForward: -1f) * WeightBehind;

                // StandFrontline: one slot directly in front of each formation
                // ally (along the group forward direction). Rear-rank units fill
                // open front slots; front-rank units in an open slot hold it.
                if ((E & (uint)BehaviorFlag.StandFrontline) != 0 && friendlies.Length > 0)
                    sum += FormationSlotPull(position, friendlies, GroupForward(enemyDir, in perception),
                                            spacing, offsetForward: 1f) * WeightFrontline;
            }

            // ---- Cohesion: active under orders AND free maneuver -----------------

            // SpreadLateral: push perpendicular to the advance axis, away from
            // the friendly center projected laterally — widens the group into a
            // line. No depth component, so it composes cleanly with advance and
            // with the wall's depth constraint (each owns one axis).
            if ((E & (uint)BehaviorFlag.SpreadLateral) != 0 && perception.HasFriendlies && perception.HasEnemies) {
                float2 forward = enemyDir;
                float2 lateral = new float2(-forward.y, forward.x);

                float2 push = float2.zero;
                for (int i = 0; i < friendlies.Length; i++) {
                    float2 toAlly = position - friendlies[i].Info.Position;
                    float lateralDist = math.dot(toAlly, lateral);   // signed lateral separation
                    float absLat = math.abs(lateralDist);
                    if (absLat > 0.01f && absLat < spacing * 1.5f)
                        push += lateral * math.sign(lateralDist) * (1f - absLat / (spacing * 1.5f));
                }
                sum += Cap(push) * WeightSpreadLat;
            }

            // FormWall as a LINE constraint: pull only on the perpendicular
            // (depth) error toward the wall line. Position ALONG the line is
            // owned by SpreadLateral + Separate so they never fight.
            // The wall's orientation follows the group's shared direction
            // (movement consensus if moving, facing consensus if standing).
            if ((E & (uint)BehaviorFlag.FormWall) != 0 &&
                perception.HasEnemies && perception.HasFriendlies)
            {
                float2 frontDir = GroupForward(enemyDir, in perception);
                float2 anchor = perception.FriendlyCenter + frontDir * WallForwardOffset;
                float depth = math.dot(anchor - position, frontDir);   // signed distance off the line
                float mag = math.saturate((math.abs(depth) - SlotDeadZone)
                                          / (SlotCaptureRadius - SlotDeadZone));
                sum += frontDir * math.sign(depth) * mag * WeightWall;
            }

            if ((E & (uint)BehaviorFlag.FormWedge) != 0 &&
                TryClosestAhead(position, GroupForward(enemyDir, in perception), friendlies, out UnitInfo leader))
            {
                float2 fwd = GroupForward(enemyDir, in perception);
                float2 lateral = new float2(-fwd.y, fwd.x);
                float side = math.dot(position - leader.Position, lateral) >= 0f ? 1f : -1f;
                float2 slot = leader.Position - fwd * spacing + lateral * (side * spacing);
                sum += SlotPull(position, slot) * WeightWedge;
            }

            // Cardinal lattice: generate candidate slots around nearby
            // friendlies, merge overlaps, exclude taken ones, COMMIT to the
            // best. Units committed to an attack are skipped both as slot
            // sources and as slot takers — instinct units don't anchor
            // formations.
            if ((E & (uint)BehaviorFlag.AlignCardinal) != 0 && friendlies.Length > 0)
            {
                NativeList<SlotCandidate> candidates = new NativeList<SlotCandidate>(Allocator.Temp);

                // Lattice axes follow the group's shared direction (movement
                // consensus -> enemy direction -> group facing) — never an
                // individual unit's facing, and no arbitrary north default.
                float2 f = GroupForward(enemyDir, in perception);
                float2 r = new float2(f.y, -f.x);

                // --- 1. Generate candidate slots from formation-relevant allies ---
                for (int i = 0; i < friendlies.Length; i++)
                {
                    UnitInfo ally = friendlies[i].Info;
                    if (ally.IsAttacking) continue;   // instinct unit: not a formation anchor

                    for (int k = 0; k < 4; k++)
                    {
                        float2 axis = k == 0 ? f : k == 1 ? r : k == 2 ? -f : -r;
                        float2 slot = ally.Position + axis * spacing;

                        // --- 2. Merge overlapping slots ---
                        bool merged = false;
                        for (int c = 0; c < candidates.Length; c++)
                        {
                            if (math.distancesq(candidates[c].position, slot) < (spacing * spacing * 0.25f))
                            {
                                SlotCandidate existing = candidates[c];
                                existing.count += 1;
                                existing.score += 1f;   // consensus slots score higher
                                candidates[c] = existing;
                                merged = true;
                                break;
                            }
                        }

                        if (!merged)
                        {
                            candidates.Add(new SlotCandidate
                            {
                                position = slot,
                                score = 1f,
                                count = 1
                            });
                        }
                    }
                }

                // --- 3. Mark taken positions ---
                for (int c = 0; c < candidates.Length; c++)
                {
                    bool taken = false;
                    for (int i = 0; i < friendlies.Length; i++)
                    {
                        UnitInfo ally = friendlies[i].Info;
                        if (ally.IsAttacking) continue;   // an attacker doesn't occupy a slot
                        if (math.distancesq(ally.Position, candidates[c].position)
                            < (spacing * spacing * 0.25f))
                        {
                            taken = true;
                            break;
                        }
                    }
                    if (taken)
                    {
                        SlotCandidate sc = candidates[c];
                        sc.score = -1000f;   // invalidate
                        candidates[c] = sc;
                    }
                }

                // --- 4. Distance preference + pick best ---
                float bestScore = float.MinValue;
                float2 bestPos = position;
                for (int c = 0; c < candidates.Length; c++)
                {
                    SlotCandidate sc = candidates[c];
                    if (sc.score < 0f) continue;
                    float dist = math.distance(position, sc.position);
                    float finalScore = sc.score * 10f - dist;
                    if (finalScore > bestScore)
                    {
                        bestScore = finalScore;
                        bestPos = sc.position;
                    }
                }

                // Seize if close, fade if distant, ignore beyond range, hold in
                // the dead zone — all inside SlotPull.
                sum += SlotPull(position, bestPos) * WeightCardinal;

                candidates.Dispose();
            }

            // ---- Vicsek movement consensus ---------------------------------------
            if ((E & (uint)BehaviorFlag.AlignMovement) != 0 &&
                math.lengthsq(perception.FriendlyAvgVelocity) > 0.05f)
                sum += math.normalizesafe(perception.FriendlyAvgVelocity) * WeightAlignMove;

            // ---- Spacing ---------------------------------------------------------
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

            // ---- Group cohesion --------------------------------------------------
            // Pulls toward the friendly center when the unit has drifted too far.
            // Activates only beyond CohesionRadius; fades in smoothly so units
            // near the group feel nothing, stragglers get a firm pull back.
            if ((E & (uint)BehaviorFlag.GroupCohesion) != 0 && perception.HasFriendlies)
            {
                float2 toCenter = perception.FriendlyCenter - position;
                float dist = math.length(toCenter);
                if (dist > tuning.CohesionRadius)
                    sum += math.normalizesafe(toCenter)
                           * ((dist - tuning.CohesionRadius) / tuning.CohesionRadius)
                           * WeightCohesion;
            }

            // ---- Follow moving -----------------------------------------------
            // Align movement with nearby friendlies that have an active target
            // (either attacking or chasing). Idle friendlies are excluded so a
            // unit doesn't get anchored to a standing crowd when others are
            // already engaged and moving.
            if ((E & (uint)BehaviorFlag.FollowMoving) != 0 &&
                math.lengthsq(perception.FriendlyMovingAvgVelocity) > 0.05f)
                sum += math.normalizesafe(perception.FriendlyMovingAvgVelocity) * WeightFollowMov;

            return sum;
        }

        // Formation slot pipeline for behind/frontline behaviors.
        // Generates one slot per formation ally at (ally.Position + fwd * offsetForward * spacing),
        // merges overlaps, marks taken positions, and returns SlotPull toward
        // the best available slot. offsetForward = -1 for behind, +1 for frontline.
        private float2 FormationSlotPull(float2 position,
                                         in DynamicBuffer<FriendlyUnit> friendlies,
                                         float2 fwd, float spacing, float offsetForward)
        {
            NativeList<SlotCandidate> candidates = new NativeList<SlotCandidate>(Allocator.Temp);

            for (int i = 0; i < friendlies.Length; i++)
            {
                UnitInfo ally = friendlies[i].Info;
                if (ally.IsAttacking) continue;
                float2 slot = ally.Position + fwd * (offsetForward * spacing);

                bool merged = false;
                for (int c = 0; c < candidates.Length; c++)
                {
                    if (math.distancesq(candidates[c].position, slot) < spacing * spacing * 0.25f)
                    {
                        SlotCandidate e = candidates[c];
                        e.score += 1f; e.count += 1;
                        candidates[c] = e;
                        merged = true; break;
                    }
                }
                if (!merged)
                    candidates.Add(new SlotCandidate { position = slot, score = 1f, count = 1 });
            }

            for (int c = 0; c < candidates.Length; c++)
            {
                for (int i = 0; i < friendlies.Length; i++)
                {
                    UnitInfo ally = friendlies[i].Info;
                    if (ally.IsAttacking) continue;
                    if (math.distancesq(ally.Position, candidates[c].position) < spacing * spacing * 0.25f)
                    {
                        SlotCandidate sc = candidates[c]; sc.score = -1000f; candidates[c] = sc; break;
                    }
                }
            }

            float bestScore = float.MinValue;
            float2 bestPos = position;
            for (int c = 0; c < candidates.Length; c++)
            {
                SlotCandidate sc = candidates[c];
                if (sc.score < 0f) continue;
                float finalScore = sc.score * 10f - math.distance(position, sc.position);
                if (finalScore > bestScore) { bestScore = finalScore; bestPos = sc.position; }
            }

            candidates.Dispose();
            return SlotPull(position, bestPos);
        }

        // Shared group orientation: movement consensus when the group is moving,
        // falling back to the enemy direction (formations face the threat).
        // Using the group vector rather than individual vectors means the wall
        // and wedge orient consistently even as individual units turn.
        private float2 GroupForward(float2 enemyDir, in Perception perception)
        {
            if (math.lengthsq(perception.FriendlyAvgVelocity) > 0.1f)
                return math.normalizesafe(perception.FriendlyAvgVelocity);
            if (perception.HasEnemies)
                return enemyDir;
            return math.normalizesafe(perception.FriendlyAvgFacing, enemyDir);
        }

        // Slot pull with commitment semantics:
        //   dist <  SlotDeadZone      -> zero. The slot is HELD; with nothing
        //                                else pulling, the maneuver sum falls
        //                                under HoldThreshold and the unit Holds.
        //   dist <= SlotCaptureRadius -> full strength. Close to a good
        //                                position: SEIZE it.
        //   dist <  SlotMaxRange      -> fades with distance. Interested, not
        //                                committed.
        //   dist >= SlotMaxRange      -> zero. Not my slot.
        private float2 SlotPull(float2 position, float2 slot)
        {
            float2 toSlot = slot - position;
            float dist = math.length(toSlot);
            if (dist < SlotDeadZone) return float2.zero;
            float2 dir = toSlot / dist;
            if (dist <= SlotCaptureRadius) return dir;
            if (dist >= SlotMaxRange) return float2.zero;
            return dir * (1f - (dist - SlotCaptureRadius) / (SlotMaxRange - SlotCaptureRadius));
        }

        private static float2 Cap(float2 v)
        {
            float len = math.length(v);
            return len > 1f ? v / len : v;
        }

        // The closest formation-relevant friendly AHEAD of me toward the enemy
        // (wedge leader). Attack-committed units don't lead formations.
        private static bool TryClosestAhead(float2 position, float2 enemyDir,
                                            in DynamicBuffer<FriendlyUnit> friendlies, out UnitInfo leader)
        {
            leader = default;
            float bestDist = float.MaxValue; int bestId = int.MaxValue; bool found = false;
            for (int i = 0; i < friendlies.Length; i++)
            {
                UnitInfo ally = friendlies[i].Info;
                if (ally.IsAttacking) continue;
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

        // Act: move to value. Facing is NOT set here — steering faces the
        // movement direction on its own (see facing rules in the header).
        // UseFlowField is derived from LoS — if the destination isn't directly
        // visible, route through the flow field.
        private void Act(ref DesiredDestination d, float2 position, float2 value)
        {
            d.Value = value; d.Has = true; d.UseFlowField = !Los(position, value);
            d.HasFace = false;
        }

        // Hold: no movement, no facing change. Steering applies no turn at all.
        private static void Hold(ref DesiredDestination d)
        {
            d.Has = false; d.UseFlowField = false; d.HasFace = false;
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

struct SlotCandidate
{
    public float2 position;
    public float score;
    public int count;   // how many overlapping slot proposals merged into this
}
