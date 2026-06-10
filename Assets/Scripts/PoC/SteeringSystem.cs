using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// STEERING / LOCOMOTION — turns the resolver's DesiredDestination into actual
// movement, then integrates. Per unit:
//   1. Head toward the destination. For long commanded moves it samples the
//      FLOW FIELD (routes around buildings); for short local goals it goes
//      straight.
//   2. Separation from neighboring units (body-blocking, no physics).
//   3. Repulsion from blocked obstacle cells (keeps units out of buildings).
//   4. Integrate, apply slope, set facing (toward movement, or the target when
//      holding so shields/spears face the enemy line).
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SlopeSystem))]
public partial struct SteeringSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<UnitTag>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var nf = SystemAPI.GetSingleton<NavFields>();
        var lookup = SystemAPI.GetSingleton<PathLookup>();
        var obstacles = SystemAPI.GetSingleton<ObstacleField>();

        new SteerJob
        {
            Dt = SystemAPI.Time.DeltaTime,
            ObstacleStrength = 14f,   // global: repulsion from blocked cells
            ArriveRadius = 0.4f,      // global: stop distance when seeking
            FaceEnemyRange = 14f,     // global: face the target instead of heading within this range
            PathMap = lookup.Map,
            CoarseCost = nf.CoarseCost,
            BlockOf = nf.BlockOf,
            FineDir = nf.FineDir,
            Passable = obstacles.Passable,
            CellComp = obstacles.CellComp,
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct SteerJob : IJobEntity
    {
        public float Dt, ObstacleStrength, ArriveRadius, FaceEnemyRange;
        [ReadOnly] public NativeParallelHashMap<int, int> PathMap;
        [ReadOnly] public NativeArray<int> CoarseCost;
        [ReadOnly] public NativeArray<int> BlockOf;
        [ReadOnly] public NativeArray<float2> FineDir;
        [ReadOnly] public NativeArray<byte> Passable;
        [ReadOnly] public NativeArray<byte> CellComp;

        private void Execute(
            Entity self,
            ref LocalTransform xform,
            ref Velocity vel,
            in Speed speed,
            in UnitRadius radius,
            in GroundSpeedMultiplier slope,
            in CombatTarget target,
            in UnitTuning tuning,
            in DesiredDestination dest,
            DynamicBuffer<UnitInfo> contacts)
        {
            float2 pos = new float2(xform.Position.x, xform.Position.z);
            float locomotion = speed.Value * slope.Value;
            float2 desired = float2.zero;

            // --- 1. Seek destination (flow field for long commanded moves) ---
            if (dest.Has)
            {
                float2 dir = float2.zero;
                // Follow the tiled field for this unit's destination. O(1):
                // goal -> path slot, current big tile -> fine block. If the fine
                // field isn't built here yet, head along the coarse gradient
                // toward the cheaper neighbor big tile (keeps moving, no stall).
                if (dest.UseFlowField) {
                    int gi = NavGrid.Index(NavGrid.Cell(dest.Value));
                    if (PathMap.TryGetValue(gi, out int slot)) {
                        int2 c = NavGrid.Cell(pos);
                        if (NavGrid.InBounds(c.x, c.y)) {
                            int big = NavGrid.BigIndex(NavGrid.BigOf(c));
                            int block = BlockOf[slot * NavGrid.BigCount + big];
                            dir = block >= 0
                                ? FineDir[block * NavGrid.SubCells + NavGrid.SubIndex(c)]
                                : CoarseDir(CoarseCost, CellComp, slot, c);
                        }
                    }
                }
                if (math.lengthsq(dir) < 1e-4f)
                {
                    float2 to = dest.Value - pos;
                    if (math.length(to) > ArriveRadius) dir = math.normalizesafe(to);
                }
                desired += dir * locomotion;
            }

            // --- 2. Separation from units ------------------------------------
            // Iterates the gather system's ContactList — the SAME snapshot
            // ContactCombat resolves impacts from, so physics and combat agree.
            // Pushback range uses BOTH radii (big units demand more room).
            float2 separation = float2.zero;
            for (int i = 0; i < contacts.Length; i++)
            {
                UnitInfo neighbor = contacts[i];
                float2 away = pos - neighbor.Position;
                float dist = math.length(away);
                if (dist < 1e-4f) { away = new float2(0.01f, 0f); dist = 0.01f; }
                float minDist = radius.Value + neighbor.Radius;
                if (dist < minDist) separation += (away / dist) * (1f - dist / minDist);
            }
            desired += separation * tuning.SeparationStrength;

            // --- 3. Repulsion from blocked obstacle cells --------------------
            float2 obstaclePush = float2.zero;
            int2 cell = NavGrid.Cell(pos);
            for (int ox = -1; ox <= 1; ox++)
            for (int oy = -1; oy <= 1; oy++)
            {
                int nx = cell.x + ox, ny = cell.y + oy;
                if (!NavGrid.InBounds(nx, ny)) continue;
                if (Passable[NavGrid.Index(nx, ny)] != 0) continue;
                float2 away = pos - NavGrid.CellCenter(nx, ny);
                float dist = math.length(away);
                if (dist > 1e-3f) obstaclePush += away / (dist * dist);
            }
            desired += obstaclePush * ObstacleStrength;

            // --- 4. Integrate ------------------------------------------------
            vel.Value = desired;
            float2 step = desired * Dt;
            xform.Position = new float3(xform.Position.x + step.x,slope.Height, xform.Position.z + step.y);
            // --- 5. Facing ---------------------------------------------------
            // Face the THREAT if we have a target in range (so a shield sliding
            // sideways to line up still faces the enemy, not its shuffle dir);
            // otherwise face our heading. Turn at a capped rate so it doesn't
            // snap or jitter frame-to-frame.
            float2 faceDir = float2.zero;
            if (target.Has && math.distance(pos, target.Position) <= FaceEnemyRange)
                faceDir = target.Position - pos;
            else if (math.lengthsq(desired) > 0.05f)
                faceDir = desired;

            if (math.lengthsq(faceDir) > 1e-4f)
            {
                quaternion want = quaternion.LookRotationSafe(
                    new float3(faceDir.x, 0f, faceDir.y), math.up());
                xform.Rotation = TurnToward(xform.Rotation, want, tuning.TurnSpeed * Dt);
            }
        }

        // Heading toward the cheaper neighbor big tile, from the coarse field.
        private static float2 CoarseDir(NativeArray<int> coarse, NativeArray<byte> cellComp,
                                        int slot, int2 cell) {
            int2 b = NavGrid.BigOf(cell);
            int baseC = slot * NavGrid.BigCount * NavGrid.MaxComp;
            byte comp = cellComp[NavGrid.Index(cell)];
            int cb = comp != 255
                ? coarse[baseC + NavGrid.BigIndex(b) * NavGrid.MaxComp + comp]
                : MinComp(coarse, baseC, NavGrid.BigIndex(b));
            if (cb == int.MaxValue || cb == 0) return float2.zero;
            int best = cb; int2 nb = b;
            for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++) {
                    if (ox == 0 && oy == 0) continue;
                    int nx = b.x + ox, ny = b.y + oy;
                    if (!NavGrid.BigInBounds(nx, ny)) continue;
                    int c = MinComp(coarse, baseC, NavGrid.BigIndex(nx, ny));
                    if (c < best) { best = c; nb = new int2(nx, ny); }
                }
            if (math.all(nb == b)) return float2.zero;
            return math.normalizesafe(NavGrid.BigCenter(nb) - NavGrid.BigCenter(b));
        }

        private static int MinComp(NativeArray<int> coarse, int baseC, int bi) {
            int m = int.MaxValue;
            for (int c = 0; c < NavGrid.MaxComp; c++)
                m = math.min(m, coarse[baseC + bi * NavGrid.MaxComp + c]);
            return m;
        }

        // Rotate `from` toward `to` by at most maxRad radians (shortest path).
        private static quaternion TurnToward(quaternion from, quaternion to, float maxRad)
        {
            float d = math.clamp(math.abs(math.dot(from.value, to.value)), -1f, 1f);
            float ang = 2f * math.acos(d);
            if (ang < 1e-4f) return to;
            return math.slerp(from, to, math.min(1f, maxRad / ang));
        }
    }
}
