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
//   5. Facing: behavior DECIDES facing (dest.HasFace); steering only executes
//      it at the turn rate via a smoothed vel.faceDir. When holding (dest.Has
//      and dest.HasFace are both false), no turn is applied — the unit settles.
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
            PathMap          = lookup.Map,
            CoarseCost       = nf.CoarseCost,
            BlockOf          = nf.BlockOf,
            FineDir          = nf.FineDir,
            Passable         = obstacles.Passable,
            CellComp         = obstacles.CellComp,
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct SteerJob : IJobEntity
    {
        public float Dt, ObstacleStrength, ArriveRadius;
        [ReadOnly] public NativeParallelHashMap<int, int> PathMap;
        [ReadOnly] public NativeArray<int> CoarseCost;
        [ReadOnly] public NativeArray<int> BlockOf;
        [ReadOnly] public NativeArray<float2> FineDir;
        [ReadOnly] public NativeArray<byte> Passable;
        [ReadOnly] public NativeArray<byte> CellComp;

        private void Execute(
            ref LocalTransform xform,
            ref Velocity vel,
            in Speed speed,
            in UnitRadius radius,
            in GroundSpeedMultiplier slope,
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
            // Iterates the gather system's contact buffer — the same snapshot
            // ContactCombat resolves impacts from, so physics and combat agree.
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

            // --- 3. Obstacle response: composite normal + smooth slide -------
            // Every blocked neighbor cell contributes an away-vector weighted by
            // proximity. The weighted SUM is a surface normal: two stacked
            // vertical blocked cells beside the unit cancel vertically and
            // reinforce horizontally -> one clean sideways normal instead of two
            // point-bounces. Diagonals fall out of the same sum.
            float2 normalSum = float2.zero;
            float penetration = 0f;   // 0 = clear, 1 = pressed against a cell center
            float falloff = radius.Value + NavGrid.CellSize;   // response ramps in within this of a blocked center
            int2 cell = NavGrid.Cell(pos);
            for (int ox = -1; ox <= 1; ox++)
            for (int oy = -1; oy <= 1; oy++)
            {
                int nx = cell.x + ox, ny = cell.y + oy;
                if (!NavGrid.InBounds(nx, ny)) continue;
                if (Passable[NavGrid.Index(nx, ny)] != 0) continue;
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
                float2 normal = math.normalizesafe(normalSum);
                // SLIDE: cancel only the into-wall component of motion, scaled
                // by penetration. Tangential motion is untouched, so units slide
                // along edges instead of bouncing off every cell corner. At full
                // penetration the into-component is fully canceled — a wall
                // still stops a unit dead head-on.
                float into = math.dot(desired, normal);
                if (into < 0f) desired -= normal * (into * penetration);
                // PUSH: smooth repulsion out of the surface. Quadratic ramp:
                // gentle at the rim, firm at contact.
                desired += normal * (penetration * penetration * ObstacleStrength);
            }

            // --- 4. Integrate ------------------------------------------------
            // vel.Value is back-calculated from the actual step so that
            // ContactCombat's closing-speed math reflects what really moved.
            // desiredValue is then bled back to match actual speed in the
            // locomotion direction — blocked units decelerate, clear units
            // re-accelerate from wherever they actually are.
            vel.Value = desired;
            float2 step = desired * Dt;
            float2 desiredDir = math.normalizesafe(vel.desiredValue);
            float currentLen = math.min(math.length(vel.desiredValue), locomotion); // clamp to current top speed (handles cresting hills)
            float projectedLen = math.max(0f, math.dot(vel.Value, desiredDir));
            vel.desiredValue = desiredDir * math.min(currentLen, projectedLen);     // only bleed down, never boost
            xform.Position = new float3(xform.Position.x + step.x, slope.Height, xform.Position.z + step.y);

            // --- 5. Facing ---------------------------------------------------
            // Behavior decides facing (dest.HasFace); steering only executes it.
            // Priority: explicit face from behavior > movement heading.
            // When holding (neither dest.Has nor dest.HasFace), skip the turn
            // entirely — the unit has settled and shouldn't keep rotating.
            // The fallback uses vel.desiredValue (the smooth locomotion vector),
            // not desired (which includes noisy separation/repulsion forces).
            if ((!dest.Has && !dest.HasFace) || math.lengthsq(vel.desiredValue) < .1)
                return;   // holding — no turn

            float2 faceDir;
            if (dest.HasFace)
                faceDir = dest.Face;
            else
                faceDir = math.normalizesafe(vel.desiredValue);   // movement heading, noise-free

            if (math.lengthsq(faceDir) < 1e-4f)
                return;

            vel.faceDir = math.normalizesafe(math.lerp(vel.faceDir, faceDir, tuning.TurnSpeed * Dt));
            quaternion want = quaternion.LookRotationSafe(
                new float3(vel.faceDir.x, 0f, vel.faceDir.y), math.up());
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
