using System;
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
//   IncomingProjectile buffer — enemy projectiles within HitRadius that are
//     low enough to collide this frame. ContactCombatSystem applies damage
//     receiver-side; behaviors can read this buffer to dodge slow shots.
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

        // ProjectileHash is created by ProjectileSystem.OnCreate; guard in case
        // it isn't up yet on the first frame.
        var projHash = SystemAPI.HasSingleton<ProjectileHash>()
            ? SystemAPI.GetSingleton<ProjectileHash>()
            : default;

        new GatherJob {
            Map = hash.Map,
            CellSize = hash.CellSize,
            ProjMap = projHash.Map,
            ProjCellSize = projHash.CellSize,
            CellType = SystemAPI.GetSingleton<ObstacleField>().CellType,
            SearchCells = 2,            // global: how many hash cells out to perceive
            ContactRadius = 6f,         // global: neighbors within this go into the ContactList
            FriendlyRadius = 8f,       // global: friendlies within this go into the FriendlyUnit buffer
            FriendlyCap = 16,           // global: max friendlies in the formation buffer (nearest kept, furthest dropped)
            OutlierFactor = 1.75f,      // global: CoM pass 2 drops units beyond mean dist * this
            ClusterRadius = 14f,        // global: trimmed mean spread above this -> "spread apart"
            LosRange = 10,              // global: max cells for LoS check
            NoLosMultiplier = 20f,       // global: effective distance penalty for enemies without LoS
            HeightGate = 2.5f,           // global: melee can't engage across a height delta larger than this (wall-tops)
        }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct GatherJob : IJobEntity {
        [ReadOnly] public NativeParallelMultiHashMap<int, UnitInfo> Map;
        [ReadOnly] public NativeParallelMultiHashMap<int, IncomingProjectile> ProjMap;
        [ReadOnly] public NativeArray<byte> CellType;
        public float CellSize, ContactRadius, FriendlyRadius, OutlierFactor, ClusterRadius;
        public float ProjCellSize;
        public float NoLosMultiplier, HeightGate;
        public int SearchCells, LosRange, FriendlyCap;

        private void Execute(
            Entity self,
            in LocalTransform xform,
            in Team team,
            in UnitTuning meUnit,
            in Attack myAttack,
            in Defense myDefense,
            in GroundSpeedMultiplier slope,
            ref Perception perception,
            DynamicBuffer<UnitInfo> contacts,
            DynamicBuffer<FriendlyUnit> friendlies,
            DynamicBuffer<IncomingProjectile> incomingProjectiles,
            [ReadOnly] DynamicBuffer<GroupMember> group) {
            float2 position = new float2(xform.Position.x, xform.Position.z);
            float myHeight = slope.Height;
            float3 forward3 = math.forward(xform.Rotation);
            float2 myFacing = math.normalizesafe(new float2(forward3.x, forward3.z), new float2(0f, 1f));

            int cellX = (int)math.floor(position.x / CellSize);
            int cellY = (int)math.floor(position.y / CellSize);

            perception = default;
            contacts.Clear();
            friendlies.Clear();
            incomingProjectiles.Clear();

            var enemies = new NativeList<UnitInfo>(32, Allocator.Temp);
            var allies = new NativeList<UnitInfo>(32, Allocator.Temp);
            // Same-group friendlies collected with distance, so the cap can keep
            // the nearest FriendlyCap and drop the furthest after the sweep.
            var friendlyCands = new NativeList<FriendlyCand>(32, Allocator.Temp);
            bool hasGroup = group.Length > 0;   // empty buffer -> ungrouped, proximity fallback

            // ---- one sweep: collect, fill buffers, score candidates ----------
            float closestEnemyDist = float.MaxValue;
            float closestFriendDist = float.MaxValue;
            float bestDanger = -1f, bestExposure = -1f;
            float dangerDist = float.MaxValue, exposureDist = float.MaxValue;
            int closestEnemyId = int.MaxValue, closestFriendId = int.MaxValue;
            int dangerId = int.MaxValue, exposureId = int.MaxValue;
            float2 avgFacing = float2.zero, avgVelocity = float2.zero, movingVelocity = float2.zero;
            int movingCount = 0;

            for (int offsetY = -SearchCells; offsetY <= SearchCells; offsetY++)
                for (int offsetX = -SearchCells; offsetX <= SearchCells; offsetX++) {
                    int key = ((cellX + offsetX) * 73856093) ^ ((cellY + offsetY) * 19349663);
                    if (!Map.TryGetFirstValue(key, out UnitInfo neighbor, out var iterator)) continue;
                    do {
                        if (neighbor.Entity == self) continue;
                        float distance = math.distance(position, neighbor.Position);

                        if (neighbor.IsBuilding)
                            distance = math.max(0f, distance - neighbor.Radius);

                        bool heightBlocked = !myAttack.isRange && !neighbor.IsBuilding &&
                                             math.abs(neighbor.Height - myHeight) > HeightGate;

                        bool los = neighbor.IsBuilding ||
                                   NavTerrain.LineOfSight(position, neighbor.Position, CellType, LosRange);
                        float effectiveDist = los ? distance : distance + NoLosMultiplier;
                        if (!neighbor.IsBuilding && !heightBlocked && effectiveDist <= ContactRadius)
                            contacts.Add(neighbor);

                        if (neighbor.Team != team.Value) {
                            if (effectiveDist > meUnit.PursueDistance && !myAttack.isRange)
                                continue;
                            if (heightBlocked)
                                continue;
                            enemies.Add(neighbor);
                            // Instinct never PICKS a building/wall as its target —
                            // units only attack structures on an explicit order
                            // (AttackTarget resolves directly, not via ClosestEnemy).
                            // Buildings still populate enemies/contacts so an
                            // ordered attack and threat awareness work.
                            if (!neighbor.IsBuilding &&
                                Better(effectiveDist, neighbor.StableId, closestEnemyDist, closestEnemyId)) {
                                closestEnemyDist = effectiveDist; closestEnemyId = neighbor.StableId;
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
                            // GROUP FILTER: a grouped unit perceives ONLY its own
                            // group's members; an ungrouped unit (empty buffer)
                            // falls back to proximity and sees everyone nearby.
                            if (hasGroup && !InGroup(group, neighbor.StableId))
                                continue;

                            allies.Add(neighbor);

                            // Formation neighborhood is MOBILE units only: a
                            // building must not anchor lattice/wedge/rank slots
                            // or drag the movement/facing consensus to zero.
                            // Collected with distance so the cap can drop the
                            // FURTHEST when the group exceeds FriendlyCap; the
                            // buffer + consensus are built from the kept set below.
                            if (distance <= FriendlyRadius && !neighbor.IsBuilding)
                                friendlyCands.Add(new FriendlyCand { Info = neighbor, Dist = distance });

                            if (Better(distance, neighbor.StableId, closestFriendDist, closestFriendId)) {
                                closestFriendDist = distance; closestFriendId = neighbor.StableId;
                                perception.ClosestFriendly = neighbor; perception.HasClosestFriendly = true;
                            }
                        }
                    }
                    while (Map.TryGetNextValue(out neighbor, ref iterator));
                }

            // ---- formation buffer: cap to the NEAREST FriendlyCap ------------
            // Over the cap, sort nearest-first (ties by StableId -> deterministic)
            // and keep only FriendlyCap. The facing/velocity/moving consensus is
            // built from the KEPT set so it matches the slots the buffer feeds.
            int keep = friendlyCands.Length;
            if (keep > FriendlyCap)
            {
                friendlyCands.Sort();
                keep = FriendlyCap;
            }
            for (int i = 0; i < keep; i++)
            {
                UnitInfo f = friendlyCands[i].Info;
                friendlies.Add(new FriendlyUnit { Info = f });
                avgFacing += f.Facing;
                avgVelocity += f.Velocity;
                if (f.IsAttacking || math.lengthsq(f.Velocity) > 0.1f)
                {
                    movingVelocity += f.Velocity;
                    movingCount++;
                }
            }
            friendlyCands.Dispose();

            // ---- incoming projectiles: 3x3 cell walk around my position -----
            if (ProjMap.IsCreated)
            {
                int projCellX = (int)math.floor(position.x / ProjCellSize);
                int projCellY = (int)math.floor(position.y / ProjCellSize);
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                    for (int offsetX = -1; offsetX <= 1; offsetX++) {
                        int key = ((projCellX + offsetX) * 73856093) ^ ((projCellY + offsetY) * 19349663);
                        if (!ProjMap.TryGetFirstValue(key, out IncomingProjectile proj, out var pit)) continue;
                        do {
                            if (proj.Team == team.Value) continue;
                            if (math.distance(position, proj.Position) > proj.HitRadius) continue;
                            incomingProjectiles.Add(proj);
                        }
                        while (ProjMap.TryGetNextValue(out proj, ref pit));
                    }
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
            perception.FriendlyMovingAvgVelocity = movingCount > 0
                ? movingVelocity / movingCount : float2.zero;

            enemies.Dispose();
            allies.Dispose();
        }

        // Deterministic "closer wins, StableId breaks ties".
        private static bool Better(float dist, int id, float bestDist, int bestId)
            => dist < bestDist || (dist == bestDist && id < bestId);

        // Group membership test for the perception filter. Linear — groups are
        // small (a selection); swap for a NativeHashSet if rosters get large.
        private static bool InGroup(in DynamicBuffer<GroupMember> group, int stableId)
        {
            for (int i = 0; i < group.Length; i++)
                if (group[i].StableId == stableId) return true;
            return false;
        }

        // A same-group friendly + its distance, so the formation cap can keep the
        // nearest and drop the furthest. Total order (distance, then StableId)
        // makes the sort identical on every peer.
        private struct FriendlyCand : IComparable<FriendlyCand>
        {
            public UnitInfo Info;
            public float Dist;
            public int CompareTo(FriendlyCand o)
            {
                if (Dist != o.Dist) return Dist < o.Dist ? -1 : 1;
                return Info.StableId.CompareTo(o.Info.StableId);
            }
        }

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
