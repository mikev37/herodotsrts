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
//     slot        FormationSlot, written FRESH every tick by FormationSystem.
//                 Has = false if FormationSystem skipped this unit this tick
//                 (FormationId == 0, or broken out of formation). hasSlot is
//                 the correct gate for all formation-dependent tiers.
//     Offset      the unit's place in the order's fixed frame (Forward/Cols/Shape
//                 all stored on the order), a pure function of index + count.
//     Scatter     a stable per-unit offset seeded by StableId, scaled by the
//                 unit's Looseness — loose units smatter around the ideal slot.
//
// RANGES (all measured to the target; height adds reach for ranged):
//   closein = min(MeleeRange, 2)                    — point-blank / blocking check
//   engage  = MeleeRange + 1 + heightBonus          — normal attack reach
//   charge  = MeleeRange + AttackNearbyRange + hB   — close-to-engage band
//   patrol  = PursueDistance                        — hunt range from a held objective
//
// PRIORITY LADDER (first match wins). "Underway" = order.HasTarget (the
// formation is actively moving to an objective, not merely holding one).
// Tiers 1-3 and 9 additionally require hasSlot (unit is in a live formation).
//
//   1  BLOCKED        underway + enemy on the line to my lead point -> clear it
//   2  ENGAGE FRONT   underway + enemy in aggression cone ahead -> attack
//   3  HARD MOVE      underway, hard order -> advance in formation slot
//   4  HARD ATTACK    ordered target -> advance / charge onto it
//   5  SURVIVAL RETREAT  below RetreatHealthPct -> flee for RetreatTime seconds
//   6  SURVIVAL KITE  AvoidMeleeRange -> fire-then-retreat by aggression
//   7  ENGAGE         default attack (engage range); holding or attack-move
//   8  MANEUVER       close to engage range (charge x aggression)
//   9  SOFT MOVE      attack-move, no enemies -> advance in formation slot
//  10  PATROL         holding, enemies in pursue range -> hunt them
//  11  HOLD POSITION  nothing to do -> hold slot/pos, yield, separate from contacts
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
            HoldRadius       = 0.4f,   // within this of desired point -> Hold (no creep, no spin)
            HeightRangeBonus = 0.5f,   // extra ranged reach per metre of height advantage
            FleeDistance     = 8f,     // retreat carrot length (world units)
            WeightYield      = 1.5f,   // idle lane-clear nudge magnitude
            CellType         = obstacles.CellType,
            Clearance        = obstacles.Clearance,
            LosRange         = 20,     // max cells for LoS test; beyond this use the flow field
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead), typeof(Immobile))]
    private partial struct BehaviorJob : IJobEntity
    {
        [ReadOnly] public NativeArray<byte> CellType;
        [ReadOnly] public NativeArray<float> Clearance;
        public float HoldRadius;
        public float HeightRangeBonus, FleeDistance;
        public float WeightYield;
        public int   LosRange;

        private void Execute(
            in LocalTransform              xform,
            in StableId                    self,
            in Perception                  perception,
            in UnitTuning                  tuning,
            in Attack                      attack,
            in Ranged                      ranged,
            in Health                      health,
            in GroundSpeedMultiplier       slope,
            in DynamicBuffer<UnitInfo>     contacts,
            in FormationSlot               slot,
            in UnitRadius                  radius,
            ref FormationMember            member,
            ref CombatTarget               target,
            ref CombatStatus               status,
            ref AttackOrder                attackOrder,
            ref MoveTarget                 order,
            ref DesiredDestination         dest)
        {
            float2 position = new float2(xform.Position.x, xform.Position.z);
            float3 fwd3     = math.forward(xform.Rotation);
            float2 myFacing = math.normalizesafe(new float2(fwd3.x, fwd3.z), new float2(0f, 1f));

            // Own body width in cells; a formation move can stamp a larger value upstream.
            dest.PathWidth = math.max(1, (int)math.ceil(2f * radius.Value / NavGrid.CellSize));

            status.IsAttacking = false;

            // ---- target resolution -----------------------------------------
            target.Has = false;
            if (attackOrder.Has && TryPerceived(in perception, in contacts, attackOrder.Target, out UnitInfo ordered))
            { target.Info = ordered; target.Has = true; }
            if (!target.Has && perception.HasClosestEnemy)
            { target.Info = perception.ClosestEnemy; target.Has = true; }

            float targetDist = target.Has ? math.distance(position, target.Info.Position) : float.MaxValue;
            if (target.Has && target.Info.IsBuilding)
                // True distance to the rectangular footprint edge (not an inscribed
                // circle) — a melee unit engages a keep's long side correctly.
                targetDist = CombatMath.DistanceToFootprint(position, target.Info.Position, target.Info.HalfExtents);

            float2 enemyDir = target.Has
                ? math.normalizesafe(target.Info.Position - position, myFacing)
                : myFacing;

            // ---- ranges ----------------------------------------------------
            float heightBonus  = (ranged.IsRanged && target.Has)
                ? HeightRangeBonus * math.max(0f, slope.Height - target.Info.Height) : 0f;
            float closeinRange = math.min(attack.Range, 2f);
            float engageRange  = attack.Range + 1f + heightBonus;
            float chargeRange  = attack.Range + tuning.AttackNearbyRange + heightBonus;
            float patrolRange  = tuning.PursueDistance;

            // ---- spacing / slot --------------------------------------------
            bool  engaged = perception.HasEnemies || (target.Has && targetDist <= engageRange);
            float spread  = engaged ? tuning.CombatSpacing : tuning.IdleSpacing;
            float health_frac = health.Max > 0f ? health.Current / health.Max : 1f;

            bool   hasSlot   = slot.Has && order.FormationId > 0;
            float  pitch     = slot.Spacing > 0f ? slot.Spacing : spread;
            float2 slotWorld = order.Value;
            if (hasSlot)
            {
                float2 sfwd   = math.normalizesafe(order.Forward, myFacing);
                float2 sright = new float2(sfwd.y, -sfwd.x);
                slotWorld = slot.Anchor
                    + FormationGeometry.Offset((FormationShape)order.Shape, slot.Index, slot.Count,
                                               math.max(1, order.Cols), sfwd, sright, pitch)
                    + FormationGeometry.Scatter(self.Value, member.Looseness, pitch);
            }

            // ---- separation ------------------------------------------------
            float  sepRadius = member.Separation > 0f ? member.Separation : pitch * 0.5f;
            float2 sep       = Separation(position, in contacts, sepRadius);

            // ---- objective front direction (for tiers 1 & 2) ---------------
            float2 objectiveDir = math.normalizesafe(order.Value - position, order.Forward);

            // ================================================================
            // 1) BLOCKED — underway + in-formation + an enemy is on the direct
            //    line between me and my lead point, physically blocking the order.
            //    Clear it without leaving formation.
            // ================================================================
            if (order.HasTarget && hasSlot && target.Has)
            {
                float2 leadPt  = position + objectiveDir * math.max(closeinRange, 1f);
                float2 toEnemy = target.Info.Position - position;
                float  proj    = math.clamp(math.dot(toEnemy, objectiveDir), 0f,
                                            math.length(leadPt - position));
                float2 nearest = position + objectiveDir * proj;
                bool   blocking = math.distance(target.Info.Position, nearest) <= closeinRange
                               && targetDist <= engageRange;
                if (blocking)
                {
                    status.IsAttacking = true;
                    Attack(ref dest, position, target.Info.Position, enemyDir, ranged.IsRanged);
                    return;
                }
            }

            // ================================================================
            // 2) ENGAGE FRONT — underway + in-formation + enemy within
            //    (engageRange × aggression) and inside the looseness cone around
            //    the objective-front direction. Attack without leaving formation.
            //    looseness 0 = dead-ahead only; looseness 1 = full 360°.
            // ================================================================
            if (order.HasTarget && hasSlot && target.Has)
            {
                float frontRange = engageRange * math.max(0f, member.Aggression);
                if (targetDist <= frontRange)
                {
                    float coneCos  = math.cos(math.PI * math.saturate(member.Looseness));
                    float2 toTgt   = math.normalizesafe(target.Info.Position - position, objectiveDir);
                    if (math.dot(toTgt, objectiveDir) >= coneCos)
                    {
                        status.IsAttacking = true;
                        Attack(ref dest, position, target.Info.Position, enemyDir, ranged.IsRanged);
                        return;
                    }
                }
            }

            // ================================================================
            // 3) HARD MOVE ORDER — underway, in-formation, hard (non-attack-move)
            //    order. Advance in formation. Ends when FormationSystem clears
            //    HasTarget on arrival; then falls through to combat tiers.
            // ================================================================
            if (order.HasTarget && !order.AttackMove && hasSlot)
            {
                DriveOrHold(ref dest, position, slotWorld + sep);
                return;
            }

            // ================================================================
            // 4) HARD ATTACK ORDER — advance onto an ordered target.
            //    Uses charge range; in formation until close, then breaks off.
            // ================================================================
            if (attackOrder.Has && target.Has && target.Info.Entity == attackOrder.Target)
            {
                if (targetDist <= engageRange)
                {
                    status.IsAttacking = true;
                    Attack(ref dest, position, target.Info.Position, enemyDir, ranged.IsRanged);
                    return;
                }
                if (targetDist <= chargeRange)
                {
                    Act(ref dest, position, target.Info.Position);
                    BreakFormation(ref order, ref member);
                    return;
                }
                if (hasSlot)
                {
                    DriveOrHold(ref dest, position, slotWorld + sep);
                    return;
                }
            }

            // ================================================================
            // 4b) ATTACK ORDER, TARGET NOT YET PERCEIVED — advance to it.
            //     A target ordered from far away (typically a BUILDING, which is
            //     only resolvable once it enters the ContactList) isn't in range
            //     to perceive yet, so branch 4 above can't fire. Walk toward the
            //     stored ordered position until the target comes into perception,
            //     at which point 4 takes over and engages. Without this a unit
            //     ordered onto a distant structure would simply stand still.
            // ================================================================
            if (attackOrder.Has && !target.Has)
            {
                Act(ref dest, position, order.Value);   // order.Value = the ordered target's position (stamped at command time)
                BreakFormation(ref order, ref member);
                return;
            }
           
            // ================================================================
            // 5) SURVIVAL — RETREAT. Below RetreatHealthPct triggers a timed
            //    commitment of RetreatTime seconds using LockstepConfig.FixedDt,
            //    so health ping-pong at the threshold doesn't cancel the retreat
            //    and regeneration / healing doesn't immediately re-engage.
            //    ReEngageHealthPct guards re-entry once healed.
            // ================================================================
            if (status.RetreatSecondsLeft > 0f)
                status.RetreatSecondsLeft = math.max(0f,
                    status.RetreatSecondsLeft - LockstepConfig.FixedDt);

            if (status.RetreatSecondsLeft > 0f && perception.HasEnemies)
            {
                BreakFormation(ref order, ref member);
                float2 away = math.normalizesafe(position - perception.EnemyCenter, -enemyDir);
                Act(ref dest, position, position + away * FleeDistance);
                return;
            }

            if (tuning.RetreatHealthPct > 0f && perception.HasEnemies
                && health_frac < tuning.RetreatHealthPct
                && status.RetreatSecondsLeft <= 0f)
            {
                status.RetreatSecondsLeft = tuning.RetreatTime;
                BreakFormation(ref order, ref member);
                float2 away = math.normalizesafe(position - perception.EnemyCenter, -enemyDir);
                Act(ref dest, position, position + away * FleeDistance);
                return;
            }
            
           // ================================================================
           // 6) SURVIVAL — KITE. Units with AvoidMeleeRange alternate firing
           //    and retreating. Fires up to max(1, aggression) shots per step
           //    back; shot count persists on CombatStatus.
           // ================================================================
           if (tuning.AvoidMeleeRange > 0f && perception.HasClosestEnemy)
           {
               float closeDist    = math.distance(position, perception.ClosestEnemy.Position);
               if (closeDist < tuning.AvoidMeleeRange)
               {
                   int shotsPerRetreat = (int)math.max(1f, member.Aggression);
                   if (status.KiteShotCount < shotsPerRetreat
                       && target.Has && targetDist <= engageRange)
                   {
                       status.IsAttacking = true;
                       Attack(ref dest, position, target.Info.Position, enemyDir, ranged.IsRanged);
                       status.KiteShotCount++;
                       return;
                   }
                   BreakFormation(ref order, ref member);
                   float2 away = math.normalizesafe(
                       position - perception.ClosestEnemy.Position, -enemyDir);
                   Act(ref dest, position,
                       position + away * (tuning.AvoidMeleeRange - closeDist));
                   status.KiteShotCount = 0;
                   return;
               }
           }
           
            // ================================================================
            // 7) ENGAGE — default attack. Fires when holding or on attack-move
            //    and an enemy is within engage range. Breaks from formation.
            // ================================================================
            if (perception.HasEnemies
                && target.Has && targetDist <= engageRange)
            {
                status.IsAttacking = true;
                Attack(ref dest, position, target.Info.Position, enemyDir, ranged.IsRanged);
                BreakFormation(ref order, ref member);
                return;
            }

            // ================================================================
            // 8) MANEUVER — close the gap so tier 7 can fire. Gated by
            //    aggression so timid units hold and wait for the line to come
            //    to them; aggressive units advance to make contact.
            // ================================================================
            if (perception.HasEnemies && target.Has)
            {
                float reach = chargeRange * math.max(1f, member.Aggression);
                if (targetDist <= reach)
                {
                    Act(ref dest, position, target.Info.Position);
                    BreakFormation(ref order, ref member);
                    return;
                }
            }

            // ================================================================
            // 9) SOFT MOVE ORDER (attack-move) — in-formation, no enemy
            //    triggered above. Advance toward the objective in slot.
            // ================================================================
            if (order.HasTarget && order.AttackMove && hasSlot)
            {
                DriveOrHold(ref dest, position, slotWorld + sep);
                return;
            }

            // ================================================================
            // 10) PATROL — objective reached (no active underway order) but
            //     enemies are in pursue range. Hunt the closest; once inside
            //     charge range tiers 7/8 take over next tick.
            // ================================================================
            if (perception.HasEnemies && target.Has && targetDist <= patrolRange)
            {
                Act(ref dest, position, target.Info.Position);
                BreakFormation(ref order, ref member);
                return;
            }
            
            // ================================================================
            // 11) HOLD POSITION — nothing to fight, no active order. Rejoin
            //     formation if broken off and fight is over. Reset kite counter.
            //     Hold slot (or position), yield out of movers' lanes.
            // ================================================================
            if (member.ResumptionFormationId != 0 && !perception.HasEnemies)
            {
                order.FormationId            = member.ResumptionFormationId;
                member.ResumptionFormationId = 0;
            }
            status.KiteShotCount = 0;
            float2 idleDest = (hasSlot ? slotWorld : position) + sep;
            idleDest += YieldNudge(position, in perception, pitch);
            DriveOrHold(ref dest, position, idleDest);
            
        }

        // ---- helpers -------------------------------------------------------

        // Idle lane-clear: step perpendicular out of the moving consensus lane.
        private float2 YieldNudge(float2 position, in Perception perception, float spread)
        {
            if (math.lengthsq(perception.FriendlyMovingAvgVelocity) <= 0.05f) return float2.zero;
            float2 moveDir  = math.normalizesafe(perception.FriendlyMovingAvgVelocity);
            float2 lateral  = new float2(-moveDir.y, moveDir.x);
            float2 fromLane = position - (perception.HasFriendlies ? perception.FriendlyCenter : position);
            float  side     = math.dot(fromLane, lateral);
            float  along    = math.dot(fromLane, moveDir);
            float  laneHalf = spread * 1.5f;
            if (along <= -spread || math.abs(side) >= laneHalf) return float2.zero;
            float sign  = side >= 0f ? 1f : -1f;
            float clear = 1f - math.abs(side) / laneHalf;
            return lateral * (sign * clear * WeightYield);
        }

        // Break from formation: FormationSystem ignores this unit next tick.
        // ResumptionFormationId caches the id so tier 11 can rejoin later.
        // HasTarget is cleared so the unit falls through to combat tiers (7-10)
        // rather than being re-caught by the formation move tiers (3/9).
        private static void BreakFormation(ref MoveTarget order, ref FormationMember member)
        {
            if (order.FormationId == -1) return;
            member.ResumptionFormationId = order.FormationId;
            order.FormationId = -1;
            order.HasTarget   = false;
        }

        // Personal-space push from contacts within radius. Enemies reach tier 7/8
        // before tier 11 so in practice only friendly contacts push here, but the
        // filter is correctness-by-tier rather than by explicit player check.
        private static float2 Separation(float2 position,
                                         in DynamicBuffer<UnitInfo> contacts, float radius)
        {
            if (radius <= 0f) return float2.zero;
            float2 push = float2.zero;
            for (int i = 0; i < contacts.Length; i++)
            {
                float2 d    = position - contacts[i].Position;
                float  dist = math.length(d);
                if (dist > 0.01f && dist < radius) push += d / dist * (1f - dist / radius);
            }
            float len = math.length(push);
            if (len > 1f) push /= len;
            return push * radius;
        }

        private void DriveOrHold(ref DesiredDestination d, float2 position, float2 destPt)
        {
            if (math.distancesq(position, destPt) < HoldRadius * HoldRadius) Hold(ref d);
            else Act(ref d, position, destPt);
        }

        // Resolve an ordered target's live snapshot. Checks the three scored
        // perception slots first, then falls back to scanning the ContactList —
        // which is essential for BUILDINGS: InformationGatherSystem deliberately
        // never promotes a building into ClosestEnemy/MostDangerous/MostExposed
        // (instinct must never auto-pick a structure), so without the contacts
        // fallback an ordered attack on a building would find target.Has = false
        // and the unit would never engage it. Buildings ARE in contacts (added to
        // the buffer during the perception sweep), so this makes ordered attacks
        // on structures work for both melee and ranged units.
        private static bool TryPerceived(in Perception perception,
                                         in DynamicBuffer<UnitInfo> contacts,
                                         Entity wanted, out UnitInfo info)
        {
            if (perception.HasClosestEnemy   && perception.ClosestEnemy.Entity    == wanted) { info = perception.ClosestEnemy;       return true; }
            if (perception.HasMostDangerous  && perception.MostDangerousEnemy.Entity == wanted) { info = perception.MostDangerousEnemy; return true; }
            if (perception.HasMostExposed    && perception.MostExposedEnemy.Entity   == wanted) { info = perception.MostExposedEnemy;   return true; }
            for (int i = 0; i < contacts.Length; i++)
                if (contacts[i].Entity == wanted) { info = contacts[i]; return true; }
            info = default; return false;
        }

        private bool Los(float2 from, float2 to, int width) =>
            NavTerrain.LineOfSight(from, to, CellType, LosRange, Clearance, width);

        private void Act(ref DesiredDestination d, float2 position, float2 value)
        {
            d.Value = value; d.Has = true; d.UseFlowField = !Los(position, value, d.PathWidth);
            d.HasFace = false;
        }

        private static void Hold(ref DesiredDestination d)
        { d.Has = false; d.UseFlowField = false; d.HasFace = false; }

        private void Attack(ref DesiredDestination d, float2 position,
                            float2 targetPos, float2 enemyDir, bool isRanged)
        {
            if (isRanged) { d.Has = false; d.UseFlowField = false; }
            else { d.Value = targetPos; d.Has = true; d.UseFlowField = !Los(position, targetPos, d.PathWidth); }
            d.Face = enemyDir; d.HasFace = true;
        }
    }
}
