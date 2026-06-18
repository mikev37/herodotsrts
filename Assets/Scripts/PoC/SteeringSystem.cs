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
//   3. Obstacle response: composite surface normal from blocked cells, with a
//      smooth distance falloff. The into-wall velocity component is canceled
//      (slide along edges, hard stop head-on); a quadratic push keeps units
//      out of the surface.
//   4. Integrate: vel.desiredValue ramps toward the locomotion target via lerp
//      and is bled back down by how much the actual step was blocked. External
//      forces (separation, obstacle repulsion) are instantaneous — they affect
//      position but do not modify vel.desiredValue. vel.Value is back-calculated
//      from the step taken, so ContactCombat's closing-speed math stays correct.
//   5. Facing, three rules: attacking -> face the enemy (explicit dest.Face);
//      moving at speed -> face the movement direction; otherwise -> no turn,
//      facing holds where it is. One rate limiter (TurnSpeed rad/s).
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
            Dt               = SystemAPI.Time.DeltaTime,
            ObstacleStrength = 14f,   // global: repulsion from blocked cells
            ArriveRadius     = 0.4f,  // global: stop distance when seeking
            FaceMinSpeed     = 0.3f,  // global: below this locomotion speed, movement doesn't drive facing
            KnockbackDecay   = 5,
            PathMap          = lookup.Map,
            CoarseCost       = nf.CoarseCost,
            BlockOf          = nf.BlockOf,
            FineDir          = nf.FineDir,
            CellType         = obstacles.CellType,
            NavHeight        = obstacles.NavHeight,
            CellComp         = obstacles.CellComp,
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead), typeof(Immobile))]   // Immobile (buildings): position/rotation are spawn-fixed.
                                                 // Critically, a building sits ON its own blocked footprint
                                                 // cells — running this job would obstacle-push it out of
                                                 // its own footprint every tick. (Restored after merge.)
    private partial struct SteerJob : IJobEntity
    {
        public float Dt, ObstacleStrength, ArriveRadius, FaceMinSpeed, KnockbackDecay;
        [ReadOnly] public NativeParallelHashMap<int, int> PathMap;
        [ReadOnly] public NativeArray<int> CoarseCost;
        [ReadOnly] public NativeArray<int> BlockOf;
        [ReadOnly] public NativeArray<float2> FineDir;
        [ReadOnly] public NativeArray<byte> CellType;
        [ReadOnly] public NativeArray<float> NavHeight;
        [ReadOnly] public NativeArray<byte> CellComp;

        private void Execute(
            Entity self,
            ref LocalTransform xform,
            ref KnockbackVelocity kb,
            ref Velocity vel,
            ref NavContext navCtx,
            in Speed speed,
            in UnitRadius radius,
            in GroundSpeedMultiplier slope,
            in UnitTuning tuning,
            in DesiredDestination dest,
            DynamicBuffer<UnitInfo> contacts)
        {
            float2 pos = new float2(xform.Position.x, xform.Position.z);
            byte ctx = navCtx.Value;
            float locomotion = speed.Value * slope.Value;
            float2 desired = float2.zero;

            // --- 1. Seek destination (flow field for long commanded moves) ---
            if (dest.Has)
            {
                float2 dir = float2.zero;
                if (dest.UseFlowField)
                {
                    int gi = NavGrid.Index(NavGrid.Cell(dest.Value));
                    if (PathMap.TryGetValue(gi, out int slot))
                    {
                        int2 c = NavGrid.Cell(pos);
                        if (NavGrid.InBounds(c.x, c.y))
                        {
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
                vel.desiredValue = math.lerp(vel.desiredValue, dir * locomotion, Dt);
                desired += vel.desiredValue;
            }
            else
            {
                vel.desiredValue = math.lerp(vel.desiredValue, float2.zero, Dt);
                desired += vel.desiredValue;
            }

            // --- 2. Separation from units ------------------------------------

            float2 separation = float2.zero;
            for (int i = 0; i < contacts.Length; i++)
            {
                UnitInfo neighbor = contacts[i];

                if (neighbor.IsBuilding) continue;
                float2 away = pos - neighbor.Position;
                float dist = math.length(away);
                if (dist < 1e-4f) { away = new float2(0.01f, 0f); dist = 0.01f; }
                float minDist = radius.Value + neighbor.Radius;
                if (dist < minDist) separation += (away / dist) * (1f - dist / minDist);
            }
            desired += separation * tuning.SeparationStrength;

            // Capture pre-obstacle desired for the speed integrator below.
            float2 preObstacleDesired = desired;
  
            float2 normalSum = float2.zero;
            float penetration = 0f;   // 0 = clear, 1 = pressed against a cell center
            float falloff = radius.Value + NavGrid.CellSize;
            int2 cell = NavGrid.Cell(pos);

            for (int ox = -1; ox <= 1; ox++)
            for (int oy = -1; oy <= 1; oy++)
            {
                int nx = cell.x + ox, ny = cell.y + oy;
                if (!NavGrid.InBounds(nx, ny)) continue;
                byte nType = CellType[NavGrid.Index(nx, ny)];

                // A cell repels if THIS unit can't stand on it. Symmetric: a
                // ground unit is walled out of roof cells, a roof unit is fenced
                // off ground cells. Transitions are always standable so a ramp
                // never repels — the unit climbs/descends through it freely.
                bool blocks = !NavCell.CanStand(ctx, nType);
                if (!blocks) continue;
                float2 away = pos - NavGrid.CellCenter(nx, ny);
                float dist = math.length(away);
                if (dist < 1e-3f) continue;
                float w = math.saturate(1f - dist / falloff);
                if (w <= 0f) continue;
                normalSum += (away / dist) * w;
                penetration = math.max(penetration, w);
            }
            if (penetration > 0f)
            {
                float sumLen = math.length(normalSum);
                if (sumLen > 1e-4f)
                {
                    float2 normal = normalSum / sumLen;

                    // SLIDE: cancel the into-wall component of motion.
                    float into = math.dot(desired, normal);
                    if (into < 0f) desired -= normal * into;

                    // PUSH out of the surface, scaled by the RESULTANT normal
                    // magnitude. Opposing walls in a fit-through passage cancel
                    // (|normalSum| small near centre -> light nudge), while a
                    // single wall gives the full push so a unit walking into a
                    // building is stopped. This must NOT be stripped against the
                    // goal direction: the whole point is to oppose motion INTO a
                    // wall, which is by definition against the unit's desired dir.
                    desired += normal * (sumLen * sumLen * ObstacleStrength);
                }
            }

            //3b. Knockback
            desired += kb.Value;
            kb.Value = math.lerp(kb.Value, float2.zero, Dt * KnockbackDecay);


            // --- 4. Integrate ------------------------------------------------
          
            vel.Value = desired;
            float2 step = desired * Dt;
            // Speed integrator: bleed desiredValue down to what was achievable in
            // the forward direction, so obstacle clearance feels like smooth
            // acceleration not a speed snap. Use pre-obstacle desired (seek +
            // separation only) to measure blockage — the obstacle push and slide
            // are deflections, not forward losses, and shouldn't contaminate it.
            float2 desiredDir = math.normalizesafe(vel.desiredValue);
            float currentLen = math.min(math.length(vel.desiredValue), locomotion);
            float achievedForward = math.max(0f, math.dot(preObstacleDesired, desiredDir));
            vel.desiredValue = desiredDir * math.min(currentLen, achievedForward);
            // Surface + context, from the destination cell AFTER the step.
            float newX = xform.Position.x + step.x;
            float newZ = xform.Position.z + step.y;
            int2 destCell = NavGrid.Cell(new float2(newX, newZ));
            byte destType = NavGrid.InBounds(destCell.x, destCell.y)
                ? CellType[NavGrid.Index(destCell.x, destCell.y)] : NavCell.Ground;

            ctx = destType == NavCell.Impassable ? ctx : destType;   // context = the surface I'm on
            navCtx.Value = ctx;

            // Y comes from slope.Height — exactly as for terrain. SlopeSystem is

            xform.Position = new float3(newX, slope.Height, newZ);

            // --- 5. Facing ---------------------------------------------------
            // Three rules, in priority order:
            //   attacking       -> face the enemy (behavior wrote dest.Face)
            //   moving at speed -> face the movement direction (smooth
            //                      desiredValue, not the noisy force sum)
            //   otherwise       -> no turn at all; facing holds where it is
            float2 faceDir;
            if (dest.HasFace)
                faceDir = dest.Face;
            else if (dest.Has && math.lengthsq(vel.desiredValue) > FaceMinSpeed * FaceMinSpeed)
                faceDir = math.normalizesafe(vel.desiredValue);
            else
                return;   // minute adjustment / holding — facing stays put

            quaternion want = quaternion.LookRotationSafe(
                new float3(faceDir.x, 0f, faceDir.y), math.up());
            xform.Rotation = TurnToward(xform.Rotation, want, tuning.TurnSpeed * Dt);
        }

        // Heading toward the cheaper neighbor big tile, from the coarse field.
        private static float2 CoarseDir(NativeArray<int> coarse, NativeArray<byte> cellComp,
                                        int slot, int2 cell)
        {
            int2 b = NavGrid.BigOf(cell);
            int baseC = slot * NavGrid.BigCount * NavGrid.MaxComp;
            byte comp = cellComp[NavGrid.Index(cell)];
            int cb = comp != 255
                ? coarse[baseC + NavGrid.BigIndex(b) * NavGrid.MaxComp + comp]
                : MinComp(coarse, baseC, NavGrid.BigIndex(b));
            if (cb == int.MaxValue || cb == 0) return float2.zero;
            int best = cb; int2 nb = b;
            for (int oy = -1; oy <= 1; oy++)
            for (int ox = -1; ox <= 1; ox++)
            {
                if (ox == 0 && oy == 0) continue;
                int nx = b.x + ox, ny = b.y + oy;
                if (!NavGrid.BigInBounds(nx, ny)) continue;
                int c = MinComp(coarse, baseC, NavGrid.BigIndex(nx, ny));
                if (c < best) { best = c; nb = new int2(nx, ny); }
            }
            if (math.all(nb == b)) return float2.zero;
            return math.normalizesafe(NavGrid.BigCenter(nb) - NavGrid.BigCenter(b));
        }

        private static int MinComp(NativeArray<int> coarse, int baseC, int bi)
        {
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
