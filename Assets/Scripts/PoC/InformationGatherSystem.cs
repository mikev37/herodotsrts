using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// INFORMATION GATHER — the single perception pass. Once per tick, each unit
// scans the spatial hash ONCE and records the FACTS decision-making needs:
//
//   Perception
//     * Enemy / friendly centers of mass (outlier-trimmed; Clustered=false
//       flags a spread-out group whose CoM is a weak signal)
//     * Candidate enemies as full UnitInfo snapshots: closest, most dangerous
//       (would deal the most mitigated damage to ME), most exposed (I would
//       deal the most mitigated damage to THEM)
//     * Closest friendly + friendly facing/movement consensus
//   FriendlyUnit buffer — nearby friendlies (full snapshots) for formation
//     behaviors (wall/wedge/cardinal/align).
//   ContactList (UnitInfo buffer) — everyone physically near, shared by
//     Steering (separation) and ContactCombat (impacts/strikes/blocking).
//
// Perception supplies facts; BehaviorSystem makes decisions (it owns target
// CHOICE — CombatTarget is written there, not here). Ties break by lowest
// StableId, never by hash iteration order.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SpatialHashSystem))]
public partial struct InformationGatherSystem : ISystem {
    [BurstCompile]
    public void OnCreate(ref SystemState state) {
        state.RequireForUpdate<SpatialHash>();
        state.RequireForUpdate<ObstacleField>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state) {
        var hash = SystemAPI.GetSingleton<SpatialHash>();
        if (!hash.Map.IsCreated) return;

        new GatherJob {
            Map = hash.Map,
            CellSize = hash.CellSize,
            Passable = SystemAPI.GetSingleton<ObstacleField>().Passable,
            SearchCells = 4,            // global: how many hash cells out to perceive
            ContactRadius = 6f,         // global: neighbors within this go into the ContactList
            FriendlyRadius = 14f,       // global: friendlies within this go into the FriendlyUnit buffer
            OutlierFactor = 1.75f,      // global: CoM pass 2 drops units beyond mean dist * this
            ClusterRadius = 14f,        // global: trimmed mean spread above this -> "spread apart"
            LosRange = 10,              // global: max cells for LoS check
            NoLosMultiplier = 10f,       // global: effective distance penalty for enemies without LoS
            BuildingDistanceBias = 30f,  // global: buildings count this many times farther in closest-enemy choice (units are preferred)
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct GatherJob : IJobEntity {
        [ReadOnly] public NativeParallelMultiHashMap<int, UnitInfo> Map;
        [ReadOnly] public NativeArray<byte> Passable;
        public float CellSize, ContactRadius, FriendlyRadius, OutlierFactor, ClusterRadius;
        public float NoLosMultiplier, BuildingDistanceBias;
        public int SearchCells, LosRange;

        private void Execute(
            Entity self,
            in LocalTransform xform,
            in Team team,
            in UnitTuning meUnit,
            in Attack myAttack,
            in Defense myDefense,
            ref Perception perception,
            DynamicBuffer<UnitInfo> contacts,
            DynamicBuffer<FriendlyUnit> friendlies) {
            float2 position = new float2(xform.Position.x, xform.Position.z);
            float3 forward3 = math.forward(xform.Rotation);
            float2 myFacing = math.normalizesafe(new float2(forward3.x, forward3.z), new float2(0f, 1f));

            int cellX = (int)math.floor(position.x / CellSize);
            int cellY = (int)math.floor(position.y / CellSize);

            perception = default;
            contacts.Clear();
            friendlies.Clear();

            var enemies = new NativeList<UnitInfo>(32, Allocator.Temp);
            var allies = new NativeList<UnitInfo>(32, Allocator.Temp);

            // ---- one sweep: collect, fill buffers, score candidates ----------
            float closestEnemyDist = float.MaxValue;
            float closestFriendDist = float.MaxValue;
            float bestDanger = -1f, bestExposure = -1f;
            float dangerDist = float.MaxValue, exposureDist = float.MaxValue;
            int closestEnemyId = int.MaxValue, closestFriendId = int.MaxValue;
            int dangerId = int.MaxValue, exposureId = int.MaxValue;
            float2 avgFacing = float2.zero, avgVelocity = float2.zero;

            for (int offsetY = -SearchCells; offsetY <= SearchCells; offsetY++)
                for (int offsetX = -SearchCells; offsetX <= SearchCells; offsetX++) {
                    int key = ((cellX + offsetX) * 73856093) ^ ((cellY + offsetY) * 19349663);
                    if (!Map.TryGetFirstValue(key, out UnitInfo neighbor, out var iterator)) continue;
                    do {
                        if (neighbor.Entity == self) continue;
                        float distance = math.distance(position, neighbor.Position);

                        if (neighbor.IsBuilding)
                            distance = math.max(0f, distance - neighbor.Radius);
                        bool los = neighbor.IsBuilding ||
                                   NavTerrain.LineOfSight(position, neighbor.Position, Passable, LosRange);
                        float effectiveDist = los ? distance : distance + NoLosMultiplier;
                        if (effectiveDist <= ContactRadius)
                            contacts.Add(neighbor);

                        if (neighbor.Team != team.Value) {
                            if (effectiveDist > meUnit.PursueDistance && !myAttack.isRange)
                                continue;
                            enemies.Add(neighbor);
                            // Closest-enemy CHOICE prefers units: a building must
                            // be BuildingDistanceBias times closer to win.
                            float targetScore = neighbor.IsBuilding
                                ? effectiveDist * BuildingDistanceBias : effectiveDist;
                            if (Better(targetScore, neighbor.StableId, closestEnemyDist, closestEnemyId)) {
                                closestEnemyDist = targetScore; closestEnemyId = neighbor.StableId;
                                perception.ClosestEnemy = neighbor; perception.HasClosestEnemy = true;
                            }

                            float2 toThreat = math.normalizesafe(neighbor.Position - position, new float2(0f, 1f));
                            float danger = CombatMath.Mitigate(neighbor.Damage, myFacing, toThreat,
                                                               myDefense.Armor, myDefense.Shield);
                            if (danger > bestDanger ||
                                (danger == bestDanger && Better(effectiveDist, neighbor.StableId, dangerDist, dangerId))) {
                                bestDanger = danger; dangerDist = effectiveDist; dangerId = neighbor.StableId;
                                perception.MostDangerousEnemy = neighbor; perception.HasMostDangerous = true;
                            }

                            float2 theirToThreat = -toThreat;
                            float exposure = CombatMath.Mitigate(myAttack.Damage, neighbor.Facing, theirToThreat,
                                                                 neighbor.Armor, neighbor.Shield);
                            if (exposure > bestExposure ||
                                (exposure == bestExposure && Better(effectiveDist, neighbor.StableId, exposureDist, exposureId))) {
                                bestExposure = exposure; exposureDist = effectiveDist; exposureId = neighbor.StableId;
                                perception.MostExposedEnemy = neighbor; perception.HasMostExposed = true;
                            }
                        } else {
                            if (effectiveDist > FriendlyRadius)
                                continue;
                            allies.Add(neighbor);

                            if (distance <= FriendlyRadius) {
                                friendlies.Add(new FriendlyUnit { Info = neighbor });
                                avgFacing += neighbor.Facing;
                                avgVelocity += neighbor.Velocity;
                            }

                            if (Better(distance, neighbor.StableId, closestFriendDist, closestFriendId)) {
                                closestFriendDist = distance; closestFriendId = neighbor.StableId;
                                perception.ClosestFriendly = neighbor; perception.HasClosestFriendly = true;
                            }
                        }
                    }
                    while (Map.TryGetNextValue(out neighbor, ref iterator));
                }

            // ---- group structure: outlier-trimmed centers of mass -------------
            perception.HasEnemies = enemies.Length > 0;
            if (perception.HasEnemies)
                perception.EnemyCenter = TrimmedCenter(enemies, out perception.EnemiesClustered);

            perception.HasFriendlies = allies.Length > 0;
            if (perception.HasFriendlies)
                perception.FriendlyCenter = TrimmedCenter(allies, out perception.FriendliesClustered);

            perception.FriendlyAvgFacing = math.normalizesafe(avgFacing, float2.zero);
            perception.FriendlyAvgVelocity = friendlies.Length > 0
                ? avgVelocity / friendlies.Length : float2.zero;

            enemies.Dispose();
            allies.Dispose();
        }

        // Deterministic "closer wins, StableId breaks ties".
        private static bool Better(float dist, int id, float bestDist, int bestId)
            => dist < bestDist || (dist == bestDist && id < bestId);

        // Two-pass center of mass: mean, then re-mean excluding units farther than
        // OutlierFactor * meanDistance (stragglers don't drag the group's center).
        // clustered=false when even the trimmed group is spread beyond ClusterRadius.
        private float2 TrimmedCenter(in NativeList<UnitInfo> units, out bool clustered) {
            float2 mean = float2.zero;
            for (int i = 0; i < units.Length; i++) mean += units[i].Position;
            mean /= units.Length;

            float meanDist = 0f;
            for (int i = 0; i < units.Length; i++) meanDist += math.distance(units[i].Position, mean);
            meanDist /= units.Length;

            float cutoff = meanDist * OutlierFactor;
            float2 trimmed = float2.zero; int kept = 0; float keptSpread = 0f;
            for (int i = 0; i < units.Length; i++) {
                float d = math.distance(units[i].Position, mean);
                if (d > cutoff) continue;
                trimmed += units[i].Position; kept++; keptSpread += d;
            }
            if (kept == 0) { clustered = false; return mean; }

            trimmed /= kept;
            keptSpread /= kept;
            clustered = keptSpread <= ClusterRadius;
            return trimmed;
        }
    }
}
