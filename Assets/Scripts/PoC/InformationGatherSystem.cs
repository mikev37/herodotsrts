using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// INFORMATION GATHER — the single perception pass. Once per tick, each unit
// scans the spatial hash ONCE and records everything decision-making and
// combat need:
//
//   * CombatTarget  — the scored best enemy (distance + health weighting).
//     This absorbs the old TargetingSystem; there is now exactly ONE
//     definition of "my target" (Behavior previously ran its own pure-distance
//     scan and could disagree with Targeting about which enemy mattered).
//   * Perception    — target distance/height/LOS, nearest shield-wall ally,
//     idle-spread push.
//   * ContactList   — a DynamicBuffer<UnitInfo> of every unit (any team)
//     within contact range. Steering (separation) and ContactCombat (impacts,
//     strikes, blocking) iterate this SAME list, so they can never disagree
//     about who is touching whom. Data is from this tick's hash snapshot —
//     exactly as fresh as the old per-system scans, gathered once.
//
// Determinism: target ties break EXPLICITLY by lowest StableId instead of
// hash-bucket insertion order — robust and self-documenting.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SpatialHashSystem))]
public partial struct InformationGatherSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<SpatialHash>();
        state.RequireForUpdate<ObstacleField>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var hash = SystemAPI.GetSingleton<SpatialHash>();
        if (!hash.Map.IsCreated) return;
        var obstacles = SystemAPI.GetSingleton<ObstacleField>();

        new GatherJob
        {
            Map = hash.Map,
            CellSize = hash.CellSize,
            SearchCells = 4,            // global: how many hash cells out to look for targets
            HealthWeight = 0.05f,       // global: target scoring prefers weaker enemies
            ContactRadius = 6f,         // global: neighbors within this go into the ContactList
            WallSearchRadius = 3f,      // global: how near a shield-wall ally must be to matter
            LosRange = 10,              // global: max cells to test LOS; farther -> just use the field
            Passable = obstacles.Passable,
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct GatherJob : IJobEntity
    {
        [ReadOnly] public NativeParallelMultiHashMap<int, UnitInfo> Map;
        [ReadOnly] public NativeArray<byte> Passable;
        public float CellSize, HealthWeight, ContactRadius, WallSearchRadius;
        public int SearchCells, LosRange;

        private void Execute(
            Entity self,
            in LocalTransform xform,
            in Team team,
            in StableId stableId,
            in UnitTuning tuning,
            ref CombatTarget target,
            ref Perception perception,
            DynamicBuffer<UnitInfo> contacts)
        {
            float2 position = new float2(xform.Position.x, xform.Position.z);
            float2 right = xform.Right().xz;
            int cellX = (int)math.floor(position.x / CellSize);
            int cellY = (int)math.floor(position.y / CellSize);

            // --- target selection (the old TargetingSystem, with explicit ties) ---
            float bestScore = float.MaxValue;
            int bestStableId = int.MaxValue;
            target.Has = false;
            UnitInfo best = default;

            // --- wall ally / spread (the old BehaviorSystem side-scan) ---
            perception = default;
            perception.WallAllyDist = WallSearchRadius;
            float rightAllyDist = WallSearchRadius;
            float leftAllyDist = WallSearchRadius;
            float2 spreadPush = float2.zero;
            bool haveWall = false; float2 wallPos = default;

            contacts.Clear();

            for (int offsetY = -SearchCells; offsetY <= SearchCells; offsetY++)
            for (int offsetX = -SearchCells; offsetX <= SearchCells; offsetX++)
            {
                int key = ((cellX + offsetX) * 73856093) ^ ((cellY + offsetY) * 19349663);
                if (!Map.TryGetFirstValue(key, out UnitInfo neighbor, out var iterator)) continue;
                do
                {
                    if (neighbor.Entity == self) continue;
                    float distance = math.distance(position, neighbor.Position);

                    // Contact list: anyone (any team) physically near us. Only the
                    // inner ring can qualify, but testing by distance keeps it exact.
                    if (distance <= ContactRadius)
                        contacts.Add(neighbor);

                    if (neighbor.Team != team.Value)
                    {
                        float score = distance + HealthWeight * neighbor.Health;
                        // Deterministic tie-break: equal scores -> lower StableId wins.
                        if (score < bestScore ||
                            (score == bestScore && neighbor.StableId < bestStableId))
                        {
                            bestScore = score;
                            bestStableId = neighbor.StableId;
                            best = neighbor;
                            target.Has = true;
                        }
                    }
                    else
                    {
                        if (distance > 0.01f && distance < tuning.SpreadRadius)
                            spreadPush += (position - neighbor.Position) / distance
                                          * (1f - distance / tuning.SpreadRadius);

                        // Nearest shield-wall former: the first one seen seeds the
                        // slot unconditionally; after that, only a CLOSER one on my
                        // right side replaces it (verbatim port of the old rung scan).
                        if ((neighbor.Flags & (uint)BehaviorFlag.FormShieldWall) != 0 && distance > 0.01f)
                        {
                            float side = math.dot(position - neighbor.Position, right);
                            if ((distance < perception.WallAllyDist && side > 1) || !haveWall)
                            {
                                perception.WallAllyDist = distance;
                                wallPos = neighbor.Position;
                                haveWall = true;
                            }
                            if ((distance < perception.WallAllyDist && side > 1) || !haveWall) {
                                    perception.WallAllyDist = distance;
                                    wallPos = neighbor.Position;
                                    haveWall = true;
                                }
                            }
                    }
                }
                while (Map.TryGetNextValue(out neighbor, ref iterator));
            }

            if (target.Has)
            {
                target.Value = best.Entity;
                target.Position = best.Position;
                perception.HasTarget = 1;
                perception.TargetDist = math.distance(position, best.Position);
                perception.TargetHeight = best.Height;
                perception.TargetLos = (byte)(NavTerrain.LineOfSight(position, best.Position, Passable, LosRange) ? 1 : 0);
            }

            perception.HasWallAlly = (byte)(haveWall ? 1 : 0);
            perception.WallAllyPos = wallPos;
            perception.SpreadPush = spreadPush;
        }
    }
}
