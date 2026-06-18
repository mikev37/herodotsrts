using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// FORMATION — the LIVING formation maintainer. Runs every tick (not once at
// order time), so a formation adapts to attrition: dead units leave the group,
// the survivors re-pack into slots, gaps close, no one holds a slot for a unit
// that died. CommandSystem only stamps the initial frame; everything dynamic
// lives here. BehaviorSystem just reads the slot this writes.
//
// Each tick, per FormationId group of living members:
//   1. Order members by a FIXED key (FrontPriority desc, then StableId) so the
//      slot ordering never flips — front-priority units fill front ranks, and
//      same-priority (same-type) units sit contiguously by intent.
//   2. slot index = position in that living, ordered list -> survivors compact.
//   3. Advance the shared ANCHOR from Origin toward the destination, but ONLY
//      while the worst straggler is within tolerance of its slot. A unit behind
//      its slot sprints to it at full speed; if anyone lags, the anchor pauses
//      so the formation can't outrun them. The slowest LIVING unit therefore
//      sets the pace with nobody measuring per-unit speed — it's emergent.
//   4. Write each member's slot (index/count/anchor) + the shared progress back.
//
// DETERMINISM: gather order is irrelevant (everything is sorted by a total key
// with a StableId tiebreak); the run walk and write-back are single-threaded.
// No floats are compared for equality to break ties. dt is the fixed sim step.
//
// State: only `Progress` (one float, in MoveTarget) is persistent — it rides the
// existing MoveTarget memcpy in the snapshot. The slot itself (FormationSlot) is
// rebuilt here every tick, so it is NOT serialized.
// ===========================================================================
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(StatResolveSystem))]
[UpdateBefore(typeof(BehaviorSystem))]
public partial struct FormationSystem : ISystem
{
    private EntityQuery _members;

    public void OnCreate(ref SystemState state)
    {
        _members = SystemAPI.QueryBuilder()
            .WithAll<FormationMember, MoveTarget, FormationSlot, LocalTransform, StableId, Speed>()
            .WithNone<Dead, Immobile>()
            .Build();
    }

    public void OnUpdate(ref SystemState state)
    {
        int n = _members.CalculateEntityCount();
        if (n == 0) return;

        var alloc  = state.WorldUpdateAllocator;
        var ents   = _members.ToEntityArray(alloc);
        var moves  = _members.ToComponentDataArray<MoveTarget>(alloc);
        var design = _members.ToComponentDataArray<FormationMember>(alloc);
        var xforms = _members.ToComponentDataArray<LocalTransform>(alloc);
        var sids   = _members.ToComponentDataArray<StableId>(alloc);
        var speeds = _members.ToComponentDataArray<Speed>(alloc);

        var keys = new NativeArray<Key>(n, Allocator.TempJob);
        var pos  = new NativeArray<float2>(n, Allocator.TempJob);   // indexed by SOURCE index i
        int live = 0;
        for (int i = 0; i < n; i++)
        {
            pos[i] = new float2(xforms[i].Position.x, xforms[i].Position.z);
            if (moves[i].FormationId == 0) continue;   // ungrouped: no slot, handled as idle in Behavior
            keys[live] = new Key
            {
                FormationId   = moves[i].FormationId,
                FrontPriority = design[i].FrontPriority,
                StableId      = sids[i].Value,
                Src           = i,
            };
            live++;
        }

        if (live == 0) { keys.Dispose(); pos.Dispose(); return; }
        var keysLive = keys.GetSubArray(0, live);
        keysLive.Sort();   // (FormationId, FrontPriority desc, StableId)

        new BuildJob
        {
            Keys            = keysLive,
            Pos             = pos,
            Moves           = moves,
            Speeds          = speeds,
            Ents            = ents,
            Slot            = SystemAPI.GetComponentLookup<FormationSlot>(false),
            Move            = SystemAPI.GetComponentLookup<MoveTarget>(false),
            Dt              = SystemAPI.Time.DeltaTime,
            StragglerFactor = 2.0f,   // global: anchor waits while any unit is > this × spacing off its slot
        }.Schedule(state.Dependency).Complete();

        keys.Dispose();
        pos.Dispose();
    }

    private struct Key : IComparable<Key>
    {
        public int FormationId, FrontPriority, StableId, Src;
        public int CompareTo(Key o)
        {
            if (FormationId != o.FormationId) return FormationId < o.FormationId ? -1 : 1;
            if (FrontPriority != o.FrontPriority) return FrontPriority > o.FrontPriority ? -1 : 1; // higher = front
            return StableId.CompareTo(o.StableId);
        }
    }

    [BurstCompile]
    private struct BuildJob : IJob
    {
        [ReadOnly] public NativeArray<Key> Keys;          // sorted; groups are contiguous
        [ReadOnly] public NativeArray<float2> Pos;        // indexed by Key.Src (source index)
        [ReadOnly] public NativeArray<MoveTarget> Moves;  // indexed by Key.Src
        [ReadOnly] public NativeArray<Speed> Speeds;      // indexed by Key.Src
        [ReadOnly] public NativeArray<Entity> Ents;       // indexed by Key.Src

        [NativeDisableParallelForRestriction] public ComponentLookup<FormationSlot> Slot;
        [NativeDisableParallelForRestriction] public ComponentLookup<MoveTarget> Move;

        public float Dt, StragglerFactor;

        public void Execute()
        {
            int n = Keys.Length;
            int s = 0;
            while (s < n)
            {
                int fid = Keys[s].FormationId;
                int e = s + 1;
                while (e < n && Keys[e].FormationId == fid) e++;
                BuildGroup(s, e);
                s = e;
            }
        }

        // [s,e) are the sorted slot positions of one formation's living members.
        private void BuildGroup(int s, int e)
        {
            int count = e - s;

            // Shared order frame (identical across the group; read the leader's).
            MoveTarget lead = Moves[Keys[s].Src];
            float2 origin  = lead.Origin;
            float2 dest    = lead.Value;
            float2 fwd     = math.normalizesafe(lead.Forward, new float2(0f, 1f));
            float2 right   = new float2(fwd.y, -fwd.x);
            int    cols    = math.max(1, lead.Cols);
            FormationShape shape = (FormationShape)lead.Shape;
            float  spacing = lead.Spacing > 0f ? lead.Spacing : 1f;
            float  progress = lead.Progress;

            // Path length drives advancement. Move/AttackMove/AttackTarget all
            // set a distinct destination; Stop sets dest == origin, so its path
            // is zero and the anchor simply holds at the group center.
            float pathLen = math.distance(origin, dest);

            // Worst straggler at the CURRENT anchor, and the slowest member.
            float2 anchorNow = origin + fwd * math.clamp(progress, 0f, pathLen);
            float maxOff = 0f;
            float slowest = float.MaxValue;
            for (int k = s; k < e; k++)
            {
                int idx = k - s;
                float2 slotWorld = anchorNow + FormationGeometry.Offset(shape, idx, count, cols, fwd, right, spacing);
                maxOff = math.max(maxOff, math.distance(Pos[Keys[k].Src], slotWorld));
                slowest = math.min(slowest, Speeds[Keys[k].Src].Value);
            }

            // Advance only if the formation is tight; otherwise hold so we never
            // outrun a straggler. (Behind-slot units close the gap on their own.)
            bool tight = maxOff <= spacing * StragglerFactor;
            float newProgress = progress;
            if (tight && pathLen > 0f)
                newProgress = math.min(pathLen, progress + slowest * Dt);

            float2 anchorNew = origin + fwd * math.clamp(newProgress, 0f, pathLen);

            for (int k = s; k < e; k++)
            {
                int src = Keys[k].Src;
                Entity ent = Ents[src];

                Slot[ent] = new FormationSlot
                {
                    Has    = true,
                    Index  = k - s,
                    Count  = count,
                    Anchor = anchorNew,
                };

                if (newProgress != progress)
                {
                    MoveTarget mv = Moves[src];
                    mv.Progress = newProgress;
                    Move[ent] = mv;
                }
            }
        }
    }
}
