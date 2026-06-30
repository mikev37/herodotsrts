using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// FORMATION — living maintainer; rebuilds every tick so attrition, new orders,
// and orientation changes are all handled automatically.
//
// SLOT ASSIGNMENT (per tick, per group):
//   1.  Depth-sort all living members by dot(pos-anchor, effFwd) DESCENDING
//       so frontmost units come first.
//   2.  Slice the sorted list into row buckets using BuildRowWidths (which
//       knows the per-shape row widths: Grid = uniform cols, Wedge = 1,2,3,…).
//   3.  Within each bucket, lateral-sort by dot(pos-anchor, right) ASCENDING
//       (most-negative = leftmost = col 0).  StableId is the final tiebreak.
//   4.  slot index  =  sum of widths of prior rows  +  rank within the row.
//
//   This matches what FormationGeometry.Offset decodes, so BehaviorSystem's
//   world point is always the correct one for this unit's row and column.
//
// ADVANCE GATE: the shared anchor steps toward the destination at the SLOWEST
// member's speed, but only while every non-charger unit is within its
//   tolerance = pitch × (StragglerBase + Looseness × StragglerScale)
// of its ideal slot.  A charger (Aggression > ChargerAgg) is exempt.
// At looseness 1 the tolerance is so large the gate is always open — a loose
// smattering never stalls its own advance.
//
// DETERMINISM: all sorts have a StableId integer tiebreak; no float is
// compared for equality to drive a branch.  The only float sorts are: depth
// and lateral — both are total orders (any two different positions produce
// different floats in practice; StableId resolves the degenerate tie).
// The entire pass is single-threaded (uses EntityManager, not Burst-safe).
// MoveTarget.{Anchor,Forward} are the only persisted fields; they ride the
// existing memcpy in the snapshot.  FormationSlot is rebuilt here and NOT
// serialised.
// ===========================================================================
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(InformationGatherSystem))]
[UpdateAfter(typeof(StatResolveSystem))]
[UpdateAfter(typeof(FlowFieldSystem))]
[UpdateBefore(typeof(BehaviorSystem))]
public partial struct FormationSystem : ISystem
{
    private const float StragglerBase      = 0.6f;
    private const float StragglerScale     = 30f;
    private const float ChargerAgg         = 1.0f;
    private const float MinPursueAgg       = 0.2f;
    private const float TerrainLoosen      = 8f;
    // Anchor is considered arrived once it is within this distance of effDest.
    // Freezes the forward direction so the near-zero anchor→dest vector can't
    // thrash the formation orientation on the final tick.
    private const float AnchorArriveRadius = 1.0f;
    private const int   AnchorLosRange     = 30;   // slightly wider than unit LoS (20)

    private EntityQuery                    _members;
    // One ghost entity per live FormationId: just LocalTransform + DesiredDestination.
    // FormationSystem updates it each tick so FlowFieldSystem builds a real BFS field
    // for the order destination (not a nearby slot position). Cleaned up on dissolve.
    private NativeHashMap<int, Entity>     _anchorEntities;

    public void OnCreate(ref SystemState state)
    {
        _members = SystemAPI.QueryBuilder()
            .WithAll<FormationMember, MoveTarget, FormationSlot, LocalTransform>()
            .WithAll<StableId, Speed, UnitTuning, Perception, AttackOrder, Velocity>()
            .WithNone<Dead, Immobile>()
            .Build();
        state.RequireForUpdate(_members);
        state.RequireForUpdate<NavFields>();
        state.RequireForUpdate<PathLookup>();
        _anchorEntities = new NativeHashMap<int, Entity>(64, Allocator.Persistent);
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_anchorEntities.IsCreated) _anchorEntities.Dispose();
    }

    public void OnUpdate(ref SystemState state)
    {

        var em    = state.EntityManager;
        int n     = _members.CalculateEntityCount();
        if (n == 0) return;


        var nf     = SystemAPI.GetSingleton<NavFields>();
        var lookup = SystemAPI.GetSingleton<PathLookup>();
        var obs    = SystemAPI.GetSingleton<ObstacleField>();
        var alloc  = state.WorldUpdateAllocator;
        var ents   = _members.ToEntityArray(alloc);
        var moves  = _members.ToComponentDataArray<MoveTarget>(alloc);
        var design = _members.ToComponentDataArray<FormationMember>(alloc);
        var tunes  = _members.ToComponentDataArray<UnitTuning>(alloc);
        var percs  = _members.ToComponentDataArray<Perception>(alloc);
        var aos    = _members.ToComponentDataArray<AttackOrder>(alloc);
        var xforms = _members.ToComponentDataArray<LocalTransform>(alloc);
        var sids   = _members.ToComponentDataArray<StableId>(alloc);
        var speeds = _members.ToComponentDataArray<Speed>(alloc);
        var vels   = _members.ToComponentDataArray<Velocity>(alloc);

        // Pre-pass: clear every slot before the group loop re-fills it.
        for (int i = 0; i < n; i++)
            em.SetComponentData(ents[i], new FormationSlot { Has = false });


        // Group indices — each FormationId forms a contiguous run after sort.
        var grouped = new NativeList<int>(n, Allocator.Temp);
        for (int i = 0; i < n; i++)
            if (moves[i].FormationId != 0) grouped.Add(i);
        if (grouped.Length == 0) { grouped.Dispose(); return; }
        grouped.Sort(new ByFormation { Moves = moves });

        var activeFids = new NativeHashSet<int>(32, Allocator.Temp);
        int total = grouped.Length, s = 0;
        while (s < total)
        {
            int fid = moves[grouped[s]].FormationId;
            int e   = s + 1;
            while (e < total && moves[grouped[e]].FormationId == fid) e++;
            activeFids.Add(fid);
            BuildGroup(em, s, e, grouped, ents, moves, design, tunes, percs, aos, xforms, sids, speeds, vels,
                       lookup.Map, nf.FineDir, nf.CoarseCost, nf.BlockOf, obs.CellComp, obs.CellType, obs.Clearance);
            s = e;
        }

        // Destroy ghost entities for formations that no longer exist.
        var staleKeys = _anchorEntities.GetKeyArray(Allocator.Temp);
        for (int k = 0; k < staleKeys.Length; k++)
            if (!activeFids.Contains(staleKeys[k]))
            {
                var ghost = _anchorEntities[staleKeys[k]];
                if (em.Exists(ghost)) em.DestroyEntity(ghost);
                _anchorEntities.Remove(staleKeys[k]);
            }
        staleKeys.Dispose();
        activeFids.Dispose();
        grouped.Dispose();
    }

    // -----------------------------------------------------------------------

    private void BuildGroup(
        EntityManager em, int s, int e,
        NativeList<int>          grouped,
        NativeArray<Entity>      ents,
        NativeArray<MoveTarget>  moves,
        NativeArray<FormationMember> design,
        NativeArray<UnitTuning>  tunes,
        NativeArray<Perception>  percs,
        NativeArray<AttackOrder> aos,
        NativeArray<LocalTransform> xforms,
        NativeArray<StableId>    sids,
        NativeArray<Speed>       speeds,
        NativeArray<Velocity>    vels,
        NativeParallelHashMap<int,int> pathMap,
        NativeArray<float2>      fineDir,
        NativeArray<int>         coarseCost,
        NativeArray<int>         blockOf,
        NativeArray<byte>        cellComp,
        NativeArray<byte>        cellType,
        NativeArray<float>       clearance)
    {
        int count = e - s;
        MoveTarget lead    = moves[grouped[s]];
        AttackOrder leadAo = aos[grouped[s]];
        float2 anchor      = lead.Anchor;

        // ---- group aggregates ------------------------------------------
        float2 enemySum  = float2.zero;
        int    enemyCt   = 0;
        float  slowest   = float.MaxValue;
        float  minPursue = float.MaxValue;
        float  minAgg    = float.MaxValue;
        float2 velSum    = float2.zero;   // avg velocity → stuckFraction for terrain gate
        for (int k = s; k < e; k++)
        {
            int i = grouped[k];
            if (percs[i].HasEnemies) { enemySum += percs[i].EnemyCenter; enemyCt++; }
            slowest   = math.min(slowest, speeds[i].Value);
            minPursue = math.min(minPursue, tunes[i].PursueDistance);
            minAgg    = math.min(minAgg, design[i].Aggression);
            velSum   += vels[i].Value;
        }
        float2 avgVel = velSum / count;
        bool   threat      = enemyCt > 0;
        float2 enemyCenter = threat ? enemySum / enemyCt : anchor;

        // ---- effective destination (moving target tracking) -------------
        Entity tgt = leadAo.Has ? leadAo.Target : Entity.Null;
        bool movingTarget = tgt != Entity.Null && em.Exists(tgt)
                         && em.HasComponent<LocalTransform>(tgt)
                         && !em.HasComponent<Dead>(tgt);
        float2 baseDest = lead.Value;
        if (movingTarget)
        {
            var tp = em.GetComponentData<LocalTransform>(tgt).Position;
            baseDest = new float2(tp.x, tp.z);
        }

        float2 effDest;
        if (lead.HasTarget || movingTarget)
            effDest = baseDest;
        else if (threat)
        {
            float pursue = minPursue * math.clamp(minAgg, MinPursueAgg, 2f);
            effDest = math.distance(anchor, enemyCenter) <= pursue ? enemyCenter : anchor;
        }
        else
            effDest = anchor;   // idle, no threat: hold position

        // Arrival: anchor is within one arrival radius of its goal.
        // Freeze the forward direction here so the near-zero anchor→dest
        // vector cannot thrash the formation orientation each tick.
        bool arrived = math.distance(anchor, effDest) < AnchorArriveRadius;

        // ---- effective forward ------------------------------------------
        float2 effFwd;
        if (arrived)
        {
            effFwd = lead.Forward;  // frozen — no normalizesafe from a ~0 vector
        }
        else if (lead.HasTarget)
        {
            effFwd = (lead.AttackMove && threat)
                ? math.normalizesafe(enemyCenter - anchor, lead.Forward)
                : math.normalizesafe(effDest     - anchor, lead.Forward);
        }
        else if (movingTarget)
            effFwd = math.normalizesafe(effDest - anchor, lead.Forward);
        else if (threat)
            effFwd = math.normalizesafe(enemyCenter - anchor, lead.Forward);
        else
            effFwd = lead.Forward;

        if (math.lengthsq(effFwd) < 1e-6f) effFwd = new float2(0f, 1f);
        // `right` is effFwd rotated 90° clockwise.
        // Col 0 = leftmost = most-negative right projection; sort ascending.
        float2 right = new float2(effFwd.y, -effFwd.x);

        int    cols  = math.max(1, lead.Cols);
        var    shape = (FormationShape)lead.Shape;

        // ---- per-tick spacing: tighten in combat, relax at rest ---------
        float pitch = 0.0001f;
        for (int k = s; k < e; k++)
        {
            int i = grouped[k];
            pitch = math.max(pitch, threat ? tunes[i].CombatSpacing : tunes[i].IdleSpacing);
        }

        // Formation's lateral width in cells: the corridor must fit the whole column.
        int formWidth = math.clamp(
            (int)math.ceil(cols * pitch / NavGrid.CellSize), 1, NavGrid.MaxWidth);

        // ================================================================
        //  TWO-PASS ROW BUCKET SORT
        //
        //  Pass 1 — depth sort (frontmost first): sort all members by
        //    dot(pos - anchor, effFwd) DESCENDING.  No equality branch
        //    needed; different positions always produce different floats.
        //    StableId is the final integer tiebreak for the degenerate case.
        //
        //  Pass 2 — per-row lateral sort: slice the depth-sorted list into
        //    row buckets using BuildRowWidths, then sort each bucket by
        //    dot(pos - anchor, right) ASCENDING (leftmost = most-negative
        //    right projection = col 0). Same StableId tiebreak.
        //
        //  Result: finalOrder[slotIndex] = src index.  slot index = the sum
        //  of all prior row widths + rank within this row, which is exactly
        //  what FormationGeometry.Offset decodes via its row-major formula.
        // ================================================================

        var rowWidths = new NativeList<int>(8, Allocator.Temp);
        FormationGeometry.BuildRowWidths(shape, count, cols, rowWidths);

        var depthKeys = new NativeArray<DepthKey>(count, Allocator.Temp);
        for (int k = s; k < e; k++)
        {
            int i = grouped[k];
            float2 rel    = new float2(xforms[i].Position.x, xforms[i].Position.z) - anchor;
            depthKeys[k - s] = new DepthKey
            {
                FrontPriority = design[i].FrontPriority,
                Depth         = math.dot(rel, effFwd),
                StableId      = sids[i].Value,
                Src           = i,
            };
        }
        depthKeys.Sort();   // descending depth, StableId tiebreak

        var finalOrder = new NativeArray<int>(count, Allocator.Temp);
        var rowBuf     = new NativeArray<LateralKey>(count, Allocator.Temp);
        int rowStart   = 0;
        for (int r = 0; r < rowWidths.Length; r++)
        {
            int rowW = rowWidths[r];
            for (int c = 0; c < rowW; c++)
            {
                int i = depthKeys[rowStart + c].Src;
                float2 rel = new float2(xforms[i].Position.x, xforms[i].Position.z) - anchor;
                rowBuf[c] = new LateralKey
                {
                    // Ascending on right projection = leftmost (most-negative) first = col 0.
                    Lateral  = math.dot(rel, right),
                    StableId = sids[i].Value,
                    Src      = i,
                };
            }
            rowBuf.GetSubArray(0, rowW).Sort();
            for (int c = 0; c < rowW; c++)
                finalOrder[rowStart + c] = rowBuf[c].Src;
            rowStart += rowW;
        }
        rowBuf.Dispose();
        depthKeys.Dispose();
        rowWidths.Dispose();

        // ================================================================
        // BUG #2 — TERRAIN-ADAPTIVE GATE
        //
        // stuckFraction = 0 → moving freely at speed.
        // stuckFraction = 1 → stationary (e.g. all units pressed against an
        //                      impassable face).
        // Threshold is 30% of max speed: normal deceleration on arrival does
        // not trigger this; only a genuine navigation stall does.
        //
        // terrainScale multiplies every unit's tolerance so that a formation
        // whose units are forced into single-file or an awkward shape can still
        // advance unit by unit — the slot positions just get a very wide
        // acceptance radius rather than deadlocking the whole column.
        // ================================================================
        float fwdSpeed     = math.max(0f, math.dot(avgVel, effFwd));
        float stuckFrac    = 1f - math.saturate(fwdSpeed / math.max(slowest * 0.3f, 0.001f));
        float terrainScale = 1f + stuckFrac * TerrainLoosen;

        bool blocked = false;
        for (int slot = 0; slot < count; slot++)
        {
            int i = finalOrder[slot];
            if (design[i].Aggression > ChargerAgg) continue;
            float2 p     = new float2(xforms[i].Position.x, xforms[i].Position.z);
            float2 slotW = anchor
                         + FormationGeometry.Offset(shape, slot, count, cols, effFwd, right, pitch)
                         + FormationGeometry.Scatter(sids[i].Value, design[i].Looseness, pitch);
            float tol = pitch * (StragglerBase + design[i].Looseness * StragglerScale) * terrainScale;
            if (math.distance(p, slotW) > tol) { blocked = true; break; }
        }

        // ================================================================
        // BUG #1 — REGISTER THE REAL DESTINATION WITH FlowFieldSystem AND
        // SAMPLE THE RESULTING FLOW FIELD
        //
        // FlowFieldSystem builds flow fields for every DesiredDestination
        // that has Has && UseFlowField. Those are always unit slot positions
        // (a few tiles ahead of the anchor) — never the order destination
        // (lead.Value). A BFS-based field for a slot one tile ahead gives
        // no useful obstacle-avoidance information for a mountain 30 tiles
        // away; using it would still result in straight-line movement.
        //
        // The fix: maintain one "ghost" entity per FormationId. It carries
        // only LocalTransform (at the anchor's world position, so the
        // fine-field corridor is built from here) and DesiredDestination
        // pointing at the REAL order destination. FlowFieldSystem picks it
        // up on the next tick and builds the correct global BFS field.
        // From tick 2 onward the anchor samples that field identically to
        // how a unit does. Tick 1 falls back to a direct line (acceptable;
        // no different from the current behaviour and over in one frame).
        //
        // Mirrors Act(): check LoS first; direct when clear, field when
        // occluded. The ghost registers UseFlowField = !LoS so the field is
        // only built when it is actually needed.
        // ================================================================
        bool anchorHasLos = !arrived &&
            NavTerrain.LineOfSight(anchor, effDest, cellType, AnchorLosRange, clearance, formWidth);

        // Create or reuse the ghost entity for this formation.
        if (!_anchorEntities.TryGetValue(lead.FormationId, out Entity ghostEnt) || !em.Exists(ghostEnt))
        {
            ghostEnt = em.CreateEntity(typeof(DesiredDestination), typeof(LocalTransform));
            _anchorEntities[lead.FormationId] = ghostEnt;
        }
        em.SetComponentData(ghostEnt, new DesiredDestination
        {
            Value        = effDest,
            Has          = !arrived,
            UseFlowField = !anchorHasLos && !arrived,
            HasFace      = false,
            PathWidth    = formWidth,   // route the corridor for the whole formation's width
        });
        em.SetComponentData(ghostEnt, LocalTransform.FromPosition(new float3(anchor.x, 0f, anchor.y)));

        float remain    = arrived ? 0f : math.distance(anchor, effDest);
        float2 newAnchor = anchor;
        if (!blocked && remain > 0f)
        {
            float2 anchorDir;
            if (anchorHasLos)
            {
                anchorDir = math.normalizesafe(effDest - anchor, effFwd);
            }
            else
            {
                // Sample the flow field built from LAST TICK's ghost registration.
                anchorDir = SampleFlowField(anchor, effDest, formWidth, pathMap, fineDir, coarseCost, blockOf, cellComp);
                // Tick 1 (ghost not yet registered) or open terrain (no field entry):
                if (math.lengthsq(anchorDir) < 1e-4f)
                    anchorDir = math.normalizesafe(effDest - anchor, effFwd);
            }
            newAnchor = anchor + anchorDir * math.min(slowest, remain);
        }

        // ---- write FormationSlot + live anchor/forward ------------------
        for (int slot = 0; slot < count; slot++)
        {
            int    i   = finalOrder[slot];
            Entity ent = ents[i];
            em.SetComponentData(ent, new FormationSlot
            {
                Has = true, Index = slot, Count = count,
                Anchor = newAnchor, Spacing = pitch,
            });
            MoveTarget mv = moves[i];
            mv.Anchor = newAnchor;
            mv.Forward = effFwd;
            if (arrived && !movingTarget) mv.HasTarget = false;  
            em.SetComponentData(ent, mv);
        }
        finalOrder.Dispose();
    }

    // ---- sort keys --------------------------------------------------------

    private struct ByFormation : IComparer<int>
    {
        [ReadOnly] public NativeArray<MoveTarget> Moves;
        public int Compare(int a, int b) => Moves[a].FormationId.CompareTo(Moves[b].FormationId);
    }

    // Primary: FrontPriority DESCENDING (higher value = front rank).
    // Secondary: Depth DESCENDING (frontmost physical position within same priority).
    // Tiebreak: StableId (determinism).
    private struct DepthKey : IComparable<DepthKey>
    {
        public int   FrontPriority;
        public float Depth;
        public int   StableId, Src;
        public int CompareTo(DepthKey o)
        {
            if (FrontPriority != o.FrontPriority) return FrontPriority > o.FrontPriority ? -1 : 1;
            if (Depth > o.Depth) return -1;
            if (Depth < o.Depth) return  1;
            return StableId.CompareTo(o.StableId);
        }
    }

    // ASCENDING lateral = leftmost (most-negative right projection) first = col 0.
    private struct LateralKey : IComparable<LateralKey>
    {
        public float Lateral;
        public int   StableId, Src;
        public int CompareTo(LateralKey o)
        {
            if (Lateral < o.Lateral) return -1;
            if (Lateral > o.Lateral) return  1;
            return StableId.CompareTo(o.StableId);
        }
    }

    // ---- flow field helpers -----------------------------------------------
    // Duplicated from SteerJob (which is private). The logic is identical to
    // the UseFlowField branch in SteeringSystem so the anchor navigates with
    // exactly the same pathfinding fidelity as a real unit.

    private static float2 SampleFlowField(
        float2 pos, float2 dest, int width,
        NativeParallelHashMap<int,int> pathMap,
        NativeArray<float2> fineDir,
        NativeArray<int>    coarseCost,
        NativeArray<int>    blockOf,
        NativeArray<byte>   cellComp)
    {
        int gi = NavGrid.Index(NavGrid.Cell(dest));
        if (!pathMap.TryGetValue(NavGrid.PathKey(gi, width), out int slot)) return float2.zero;
        int2 c = NavGrid.Cell(pos);
        if (!NavGrid.InBounds(c.x, c.y)) return float2.zero;
        int big   = NavGrid.BigIndex(NavGrid.BigOf(c));
        int block = blockOf[slot * NavGrid.BigCount + big];
        if (block >= 0)
            return fineDir[block * NavGrid.SubCells + NavGrid.SubIndex(c)];
        return CoarseDirForAnchor(coarseCost, cellComp, slot, c);
    }

    private static float2 CoarseDirForAnchor(
        NativeArray<int>  coarse, NativeArray<byte> cellComp, int slot, int2 cell)
    {
        int2 b     = NavGrid.BigOf(cell);
        int  baseC = slot * NavGrid.BigCount * NavGrid.MaxComp;
        byte comp  = cellComp[NavGrid.Index(cell)];
        int  cb    = comp != 255
            ? coarse[baseC + NavGrid.BigIndex(b) * NavGrid.MaxComp + comp]
            : MinCompAnchor(coarse, baseC, NavGrid.BigIndex(b));
        if (cb == int.MaxValue || cb == 0) return float2.zero;
        int best = cb; int2 nb = b;
        for (int oy = -1; oy <= 1; oy++)
        for (int ox = -1; ox <= 1; ox++)
        {
            if (ox == 0 && oy == 0) continue;
            int nx = b.x + ox, ny = b.y + oy;
            if (!NavGrid.BigInBounds(nx, ny)) continue;
            int c2 = MinCompAnchor(coarse, baseC, NavGrid.BigIndex(nx, ny));
            if (c2 < best) { best = c2; nb = new int2(nx, ny); }
        }
        if (math.all(nb == b)) return float2.zero;
        return math.normalizesafe(NavGrid.BigCenter(nb) - NavGrid.BigCenter(b));
    }

    private static int MinCompAnchor(NativeArray<int> coarse, int baseC, int bi)
    {
        int m = int.MaxValue;
        for (int c = 0; c < NavGrid.MaxComp; c++)
            m = math.min(m, coarse[baseC + bi * NavGrid.MaxComp + c]);
        return m;
    }
}
