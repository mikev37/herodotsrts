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
[UpdateBefore(typeof(BehaviorSystem))]
public partial struct FormationSystem : ISystem
{
    private const float StragglerBase  = 0.6f;
    private const float StragglerScale = 30f;
    private const float ChargerAgg     = 1.0f;
    private const float MinPursueAgg   = 0.2f;

    private EntityQuery _members;

    public void OnCreate(ref SystemState state)
    {
        _members = SystemAPI.QueryBuilder()
            .WithAll<FormationMember, MoveTarget, FormationSlot, LocalTransform>()
            .WithAll<StableId, Speed, UnitTuning, Perception, AttackOrder>()
            .WithNone<Dead, Immobile>()
            .Build();
        state.RequireForUpdate(_members);
    }

    public void OnUpdate(ref SystemState state)
    {
        var em    = state.EntityManager;
        float dt  = SystemAPI.Time.DeltaTime;
        int n     = _members.CalculateEntityCount();
        if (n == 0) return;

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

        // Group indices — each FormationId forms a contiguous run after sort.
        var grouped = new NativeList<int>(n, Allocator.Temp);
        for (int i = 0; i < n; i++)
            if (moves[i].FormationId != 0) grouped.Add(i);
        if (grouped.Length == 0) { grouped.Dispose(); return; }
        grouped.Sort(new ByFormation { Moves = moves });

        int total = grouped.Length, s = 0;
        while (s < total)
        {
            int fid = moves[grouped[s]].FormationId;
            int e   = s + 1;
            while (e < total && moves[grouped[e]].FormationId == fid) e++;
            BuildGroup(em, dt, s, e, grouped, ents, moves, design, tunes, percs, aos, xforms, sids, speeds);
            s = e;
        }
        grouped.Dispose();
    }

    // -----------------------------------------------------------------------

    private void BuildGroup(
        EntityManager em, float dt, int s, int e,
        NativeList<int>          grouped,
        NativeArray<Entity>      ents,
        NativeArray<MoveTarget>  moves,
        NativeArray<FormationMember> design,
        NativeArray<UnitTuning>  tunes,
        NativeArray<Perception>  percs,
        NativeArray<AttackOrder> aos,
        NativeArray<LocalTransform> xforms,
        NativeArray<StableId>    sids,
        NativeArray<Speed>       speeds)
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
        for (int k = s; k < e; k++)
        {
            int i = grouped[k];
            if (percs[i].HasEnemies) { enemySum += percs[i].EnemyCenter; enemyCt++; }
            slowest   = math.min(slowest, speeds[i].Value);
            minPursue = math.min(minPursue, tunes[i].PursueDistance);
            minAgg    = math.min(minAgg, design[i].Aggression);
        }
        bool   threat      = enemyCt > 0;
        float2 enemyCenter = threat ? enemySum / enemyCt : anchor;
        float  distEnemy   = math.distance(anchor, enemyCenter);

        // ---- effective destination + forward ---------------------------
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

        float2 effDest, effFwd;
        if (lead.HasTarget)
        {
            effDest = baseDest;
            effFwd  = (lead.AttackMove && threat)
                ? math.normalizesafe(enemyCenter - anchor, lead.Forward)
                : math.normalizesafe(effDest     - anchor, lead.Forward);
        }
        else if (movingTarget)
        {
            effDest = baseDest;
            effFwd  = math.normalizesafe(effDest - anchor, lead.Forward);
        }
        else if (threat)
        {
            effFwd = math.normalizesafe(enemyCenter - anchor, lead.Forward);
            float pursue = minPursue * math.clamp(minAgg, MinPursueAgg, 2f);
            effDest = distEnemy <= pursue ? enemyCenter : anchor;
        }
        else { effFwd = lead.Forward; effDest = anchor; }

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

        // ---- advance gate: stall until non-charger stragglers catch up ---
        bool blocked = false;
        for (int slot = 0; slot < count; slot++)
        {
            int i = finalOrder[slot];
            if (design[i].Aggression > ChargerAgg) continue;  // chargers don't stall the advance
            float2 p     = new float2(xforms[i].Position.x, xforms[i].Position.z);
            float2 slotW = anchor
                         + FormationGeometry.Offset(shape, slot, count, cols, effFwd, right, pitch)
                         + FormationGeometry.Scatter(sids[i].Value, design[i].Looseness, pitch);
            float tol = pitch * (StragglerBase + design[i].Looseness * StragglerScale);
            if (math.distance(p, slotW) > tol) { blocked = true; break; }
        }

        float  remain    = math.distance(anchor, effDest);
        float2 newAnchor = anchor;
        if (!blocked && remain > 1e-3f)
            newAnchor = anchor + math.normalizesafe(effDest - anchor, effFwd)
                               * math.min(slowest * dt, remain);

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
            mv.Anchor  = newAnchor;
            mv.Forward = effFwd;
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
}
