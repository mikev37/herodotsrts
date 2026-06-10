using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// BEHAVIOR — the decision layer. Consumes the gather system's Perception and
// CombatTarget (it does NOT scan the spatial hash; perception happens exactly
// once, in InformationGatherSystem) and decides, via the prioritized guarded
// chain, what the unit WANTS: a DesiredDestination for steering, and whether
// it is COMMITTED to attacking (CombatStatus.IsAttacking).
//
// IsAttacking is the contract with the rest of combat:
//   * AttackTimerSystem only runs the charge-up/cooldown cycle while it's set —
//     so a unit cannot move and attack: any rung that moves clears it, and only
//     the hold-and-fight rung sets it. (Bowmen no longer fire mid-march.)
//   * The hash publishes it (UnitInfo.IsAttacking) so ContactCombat resolves
//     strikes from the attacker's declared state instead of re-deriving it.
//
// Ranged units gain attack range with height advantage (// global: below).
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
            FlankTolerance = 1f,          // global: how far off-line before we slide to fix it
            ArriveRadius = 3f,            // global: within this of an order target, the order is released
            HeightRangeBonus = 0.5f,      // global: extra ranged attack range per meter of height advantage
            Passable = obstacles.Passable,
            LosRange = 10,                // global: max cells to test LOS; farther -> just use the field
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct BehaviorJob : IJobEntity
    {
        [ReadOnly] public NativeArray<byte> Passable;
        public float FlankTolerance, ArriveRadius, HeightRangeBonus;
        public int LosRange;

        private void Execute(
            in LocalTransform xform,
            in BehaviorFlags baseFlags,
            in BehaviorOverride over,
            in CombatTarget target,
            in Perception perception,
            in UnitTuning tuning,
            in Attack attack,
            in Ranged ranged,
            in GroundSpeedMultiplier slope,
            ref CombatStatus status,
            ref AttackOrder attackOrder,
            ref MoveTarget order,
            ref DesiredDestination dest)
        {
            float2 position = new float2(xform.Position.x, xform.Position.z);
            uint E = BehaviorOverride.Effective(baseFlags.Value, over);

            // Default stance: not committed to attacking. Exactly one rung below
            // (hold-and-fight) sets this; every movement rung leaves it false.
            status.IsAttacking = false;

            bool hasEnemy = perception.HasTarget != 0;
            float enemyDist = perception.TargetDist;
            float2 enemyDir = hasEnemy
                ? math.normalizesafe(target.Position - position, new float2(0, 1))
                : new float2(0, 1);
            bool defensive = (E & (uint)BehaviorFlag.HoldWhenDefensive) != 0;

            // Effective engage range: ranged units shoot farther downhill.
            float engageRange = attack.Range;
            if (ranged.IsRanged && hasEnemy)
                engageRange += HeightRangeBonus * math.max(0f, slope.Height - perception.TargetHeight);

            // ================= THE GUARDED CHAIN =========================

            // Rung 1: explicit player move order. A plain move ignores enemies
            // and walks to the point; an ATTACK-move engages any enemy it can
            // actually SEE (falls through to the combat rungs, keeping the order
            // so it resumes once the enemy is gone).
            if (order.HasTarget)
            {
                bool engage = order.AttackMove && hasEnemy && perception.TargetLos != 0;
                if (!engage)
                {
                    if (math.distance(position, order.Value) > ArriveRadius)
                    {
                        Act(ref dest, order.Value, useFlow: !Los(position, order.Value));
                        return;
                    }
                    order.HasTarget = false;   // arrived -> release, resume behavior below
                }
            }

            // Rung 2: explicit attack order — advance until we reach the target,
            // then LET GO so the normal combat rungs (hold & fight) take over.
            if (attackOrder.Has)
            {
                if (target.Has && enemyDist > engageRange)
                {
                    Act(ref dest, target.Position, useFlow: perception.TargetLos == 0);
                    return;
                }
                attackOrder.Has = false;   // reached or lost the target -> release
            }

            // Rung 3: KiteEnemies — if an enemy is inside our comfort range, back off.
            if ((E & (uint)BehaviorFlag.KiteEnemies) != 0 && hasEnemy &&
                enemyDist < tuning.KiteRange)
            {
                Act(ref dest, position - enemyDir * (tuning.KiteRange - enemyDist), useFlow: false);
                return;
            }

            // Rung 4: target within attack range -> plant, commit, and fight.
            // This is the ONLY place a unit becomes an attacker: holding clears
            // the seek (only separation + knockback move it now), and IsAttacking
            // starts the attack cycle (charge-up -> fire -> cooldown).
            if (target.Has && enemyDist <= engageRange)
            {
                if(ranged.IsRanged)
                    Hold(ref dest);
				else
                    Act(ref dest, target.Position, useFlow: perception.TargetLos == 0);
                status.IsAttacking = true;
                return;
            }

            // Rung 5: StayBehindWall — tuck in just behind the nearest friendly
            // wall-former, but only when there's an enemy, and only if that wall
            // is actually IN FRONT of us (toward the enemy).
            if ((E & (uint)BehaviorFlag.StayBehindWall) != 0 && hasEnemy &&
                perception.HasWallAlly != 0 && perception.WallAllyDist < 3f &&
                math.dot(perception.WallAllyPos - position, enemyDir) > 0f)
            {
                float2 behind = perception.WallAllyPos - enemyDir * tuning.WallSpacing;
                if (math.distance(position, behind) > FlankTolerance)
                {
                    Act(ref dest, behind, useFlow: false);
                    return;
                }
                // already tucked in -> fall through (so a covered spear can still advance)
            }

            // Rung 6: FormShieldWall — line up SHOULDER-TO-SHOULDER beside the
            // nearest friendly wall-former, on whichever side I'm already on, at
            // the same forward distance. Once in the slot, fall through so the
            // wall advances. No buddy yet -> fall through and converge forward.
            if ((E & (uint)BehaviorFlag.FormShieldWall) != 0 && hasEnemy &&
                math.dot(enemyDir, xform.Forward().xz) > 0.5f &&
                perception.HasWallAlly != 0 && perception.WallAllyDist < 3f)
            {
                float2 lateral = new float2(-enemyDir.y, enemyDir.x);
                float side = math.dot(position - perception.WallAllyPos, lateral);
                float sign = side >= 0f ? 1f : -1f;
                float2 spot = perception.WallAllyPos + lateral * (sign * tuning.WallSpacing);
                if (math.distance(position, spot) > FlankTolerance)
                {
                    Act(ref dest, spot, useFlow: false);
                    return;
                }
                // already shoulder-to-shoulder -> fall through (advance/hold as a wall)
            }

            // Rung 7: AdvanceToTarget — march onto the best target (unless
            // holding defensively). Route via the field when it's hidden.
            if ((E & (uint)BehaviorFlag.AdvanceToTarget) != 0 && target.Has && !defensive)
            {
                Act(ref dest, target.Position, useFlow: perception.TargetLos == 0);
                return;
            }

            // Rung 8: IdleSpread — no enemy near: drift apart so the group
            // spreads at rest and re-forms when an enemy returns.
            if ((E & (uint)BehaviorFlag.IdleSpread) != 0 && !hasEnemy &&
                math.lengthsq(perception.SpreadPush) > 1e-3f)
            {
                Act(ref dest, position + math.normalizesafe(perception.SpreadPush) * tuning.SpreadStrength,
                    useFlow: false);
                return;
            }

            // Rung 9: nothing applied -> hold (without committing to an attack).
            Hold(ref dest);
        }

        private bool Los(float2 from, float2 to) =>
            NavTerrain.LineOfSight(from, to, Passable, LosRange);

        private static void Act(ref DesiredDestination d, float2 value, bool useFlow)
        {
            d.Value = value; d.Has = true; d.UseFlowField = useFlow;
        }
        private static void Hold(ref DesiredDestination d)
        {
            d.Has = false; d.UseFlowField = false;
        }
    }
}
