using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// BEHAVIOR RESOLVER — a PRIORITIZED GUARDED CHAIN with fall-through.
//
// Each rung evaluates a condition and either ACTS (writes a DesiredDestination
// and returns) or DECLINES (falls through to the next rung). Code order = the
// priority order. Each rung is gated by the unit's EFFECTIVE behavior mask:
//
//     effective = (base BehaviorFlags | hero ForceOn) & ~hero ForceOff
//
// so abilities/auras turn rungs on and off at runtime. A rung that's disabled,
// or whose condition isn't met, simply passes control down — which is why a
// shield that has both flanks filled falls through from "line up" to "advance"
// and walks forward AS a wall.
//
// Steering consumes the single DesiredDestination; facing/melee/ranged are
// handled by their own systems (a unit that "holds" still auto-fights because
// ContactCombat/RangedAttack fire on proximity, and steering faces the target
// when stationary).
//
// To add a behavior: add a BehaviorFlag, add a rung here in priority order.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(TargetingSystem))]
[UpdateAfter(typeof(StatResolveSystem))]
[UpdateAfter(typeof(FlowFieldSystem))]
public partial struct BehaviorSystem : ISystem
{
    public void OnCreate(ref SystemState state) => state.RequireForUpdate<SpatialHash>();

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var hash = SystemAPI.GetSingleton<SpatialHash>();
        if (!hash.Map.IsCreated) return;
        var obs = SystemAPI.GetSingleton<ObstacleField>();
        bool hasTerrain = SystemAPI.TryGetSingleton<TerrainHeightField>(out var terrain) && terrain.IsValid;

        new BehaviorJob
        {
            Map = hash.Map,
            CellSize = hash.CellSize,
            FlankTolerance = 1f,     // global: how far off-line before we slide to fix it
            ArriveRadius = 3f,       // global: within this of an order target, the order is released
            Passable = obs.Passable,
            HasTerrain = hasTerrain,
            Terrain = terrain,
            LosRange = 10,             // global: max cells to test LOS; farther -> just use the field
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct BehaviorJob : IJobEntity
    {
        [ReadOnly] public NativeParallelMultiHashMap<int, NeighborData> Map;
        [ReadOnly] public NativeArray<byte> Passable;
        public float CellSize, FlankTolerance, ArriveRadius;
        public bool HasTerrain;
        [ReadOnly] public TerrainHeightField Terrain;
        public int LosRange;

        private void Execute(
            in LocalTransform xform,
            in Team team,
            in BehaviorFlags baseFlags,
            in BehaviorOverride over,
            in CombatTarget target,
            in UnitTuning tuning,
            in Attack atk,
            ref AttackOrder attack,
            ref MoveTarget order,
            ref DesiredDestination dest)
        {
            float2 pos = new float2(xform.Position.x, xform.Position.z);
            uint E = BehaviorOverride.Effective(baseFlags.Value, over);

            float wallSpacing = tuning.WallSpacing;
            float kiteRange = tuning.KiteRange;
            float spreadRadius = tuning.SpreadRadius;

            // ---- gather neighborhood facts in one hash sweep -------------
            float2 nearestEnemy = pos; float nearestEnemyDist = float.MaxValue;
            float2 nearestWall = pos; float nearestWallDist = 3;
            float2 spreadPush = float2.zero;   // dispersion from all nearby friendlies (idle)

            int cx = (int)math.floor(pos.x / CellSize);
            int cy = (int)math.floor(pos.y / CellSize);
            for (int oy = -3; oy <= 3; oy++)
            for (int ox = -3; ox <= 3; ox++)
            {
                int key = ((cx + ox) * 73856093) ^ ((cy + oy) * 19349663);
                if (!Map.TryGetFirstValue(key, out var n, out var it)) continue;
                do
                {
                    float d = math.distance(pos, n.Position);
                    if (n.Team != team.Value)
                    {
                        if (d < nearestEnemyDist) { nearestEnemyDist = d; nearestEnemy = n.Position; }
                    }
                    else  // friendly
                    {
                        if (d > 0.01f && d < spreadRadius)
                            spreadPush += (pos - n.Position) / d * (1f - d / spreadRadius);

                        if ((n.Flags & (uint)BehaviorFlag.FormShieldWall) != 0 && d > 0.01f)
                        {
                           float side = math.dot(pos - n.Position, xform.Right().xz);
                           if ((d < nearestWallDist && side > 0) || nearestWall.Equals(pos)) { nearestWallDist = d; nearestWall = n.Position; }
                        }
                    }
                }
                while (Map.TryGetNextValue(out n, ref it));
            }

            bool hasEnemy = nearestEnemyDist < float.MaxValue;
            float2 enemyDir = hasEnemy ? math.normalizesafe(nearestEnemy - pos) : new float2(0, 1);
            bool defensive = (E & (uint)BehaviorFlag.HoldWhenDefensive) != 0;

            // ================= THE GUARDED CHAIN =========================

            // Rung 1: explicit player move order. A plain move ignores enemies
            // and walks to the point; an ATTACK-move engages any enemy it can
            // actually SEE (falls through to the combat rungs, keeping the order
            // so it resumes once the enemy is gone). Flow field only when the
            // destination is NOT in line of sight; otherwise steer straight.
            if (order.HasTarget)
            {
                bool engage = order.AttackMove && hasEnemy && Los(pos, nearestEnemy);
                if (!engage)
                {
                    if (math.distance(pos, order.Value) > ArriveRadius)
                    {
                        Act(ref dest, order.Value, useFlow: !Los(pos, order.Value));
                        return;
                    }
                    order.HasTarget = false;   // arrived -> release, resume behavior below
                }
            }

            // Rung 2: explicit attack order — advance until we reach the target,
            // then LET GO so the normal combat rungs (hold & fight) take over.
            // Route via the field when the target is hidden behind a building/
            // cliff; walk straight at it once we can see it.
            if (attack.Has)
            {
                if (target.Has && math.distance(pos, target.Position) > atk.Range)
                {
                    Act(ref dest, target.Position, useFlow: !Los(pos, target.Position));
                    return;
                }
                attack.Has = false;        // reached or lost the target -> release
            }

            // Rung 3: KiteEnemies — if an enemy is inside our comfort range, back off.
            if ((E & (uint)BehaviorFlag.KiteEnemies) != 0 && hasEnemy)
            {
                if (nearestEnemyDist < kiteRange)
                {
                    Act(ref dest, pos - enemyDir * (kiteRange - nearestEnemyDist), useFlow: false);
                    return;
                }
            }

            // Rung 4: target within attack range -> plant and let combat fire.
            // Holding clears the seek, so the only thing that moves the unit now
            // is separation + knockback (other units' mass). Kiters never reach
            // here because the kite rung above outranks this. Uses atk.Range so a
            // ranged unit stands and shoots from range instead of walking in.
            if (target.Has && math.distance(pos, target.Position) <= atk.Range)
            {
                Hold(ref dest);   // steering keeps facing the target; ContactCombat/RangedAttack fire
                return;
            }

            // Rung 5: StayBehindWall — tuck in just behind the nearest friendly
            // wall-former, but only when there's an enemy (otherwise "behind" has
            // no meaning and the whole block drifts), and only if that wall is
            // actually IN FRONT of us (toward the enemy) so we don't recede
            // chasing peers that are beside or behind us.
            if ((E & (uint)BehaviorFlag.StayBehindWall) != 0 && hasEnemy &&
                nearestWallDist < 3 &&
                math.dot(nearestWall - pos, enemyDir) > 0f)
            {
                float2 behind = nearestWall - enemyDir * wallSpacing;
                if (math.distance(pos, behind) > FlankTolerance)
                {
                    Act(ref dest, behind, useFlow: false);
                    return;
                }
                // already tucked in -> fall through (so a covered spear can still advance)
            }

            // Rung 6: FormShieldWall — if a flank is open, slide to line up; else fall through.
            // Rung 6: FormShieldWall — line up SHOULDER-TO-SHOULDER beside the
            // nearest friendly wall-former, on whichever side I'm already on, at
            // the same forward distance (so the line stays perpendicular to the
            // enemy). Once I'm in that slot, fall through so the wall advances.
            // If I have no shield buddy yet, fall through to advance toward the
            // front — that's how scattered shields converge instead of freezing.
            if ((E & (uint)BehaviorFlag.FormShieldWall) != 0 && hasEnemy && math.dot(enemyDir,xform.Forward().xz) > .5f && !nearestWall.Equals(pos) &&
                nearestWallDist < 3)
            {
                float2 lateral = new float2(-enemyDir.y, enemyDir.x);
                float side = math.dot(pos - nearestWall, lateral);   // which side of the buddy I'm on
                float sign = side >= 0f ? 1f : -1f;
                float2 spot = nearestWall + lateral * (sign * wallSpacing);
                if (math.distance(pos, spot) > FlankTolerance)
                {
                    Act(ref dest, spot, useFlow: false);
                    return;
                }
                // already shoulder-to-shoulder -> fall through (advance/hold as a wall)
            }

            // Rung 7: AdvanceToTarget — march onto the best target (unless holding
            // defensively). Route via the field when the target is hidden behind
            // an obstacle (so we path AROUND it); steer straight once it's visible.
            if ((E & (uint)BehaviorFlag.AdvanceToTarget) != 0 && target.Has && !defensive)
            {
                Act(ref dest, target.Position, useFlow: !Los(pos, target.Position));
                return;
            }

            // Rung 8: IdleSpread — no enemy near: drift apart so the group spreads
            // out at rest, and naturally re-forms (via the rungs above) when an
            // enemy returns and hasEnemy flips true.
            if ((E & (uint)BehaviorFlag.IdleSpread) != 0 && !hasEnemy &&
                math.lengthsq(spreadPush) > 1e-3f)
            {
                Act(ref dest, pos + math.normalizesafe(spreadPush) * tuning.SpreadStrength, useFlow: false);
                return;
            }

            // Rung 9: nothing applied -> hold.
            Hold(ref dest);
        }

        private bool Los(float2 a, float2 b) =>
            NavTerrain.LineOfSight(a, b, Passable, LosRange);

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
