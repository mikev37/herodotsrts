using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// DEBUG OVERLAY — your eyes into the blind-coded sim. Drop on one GameObject.
//
//   * On-screen HUD (Game view) with all the live counts from SimDebug.
//   * Scene-view gizmos: flow-field directions, blocked obstacle cells, each
//     unit's team/facing, lines to its target and its desired destination,
//     and selection highlight.
//
// Everything is toggleable in the inspector, and the live stats are mirrored
// into read-only inspector fields so you can watch them update during play.
//
// IMPORTANT: data is snapshotted in LateUpdate (after the ECS frame) into plain
// managed lists; OnDrawGizmos only reads those copies, so it never touches a
// NativeArray that a job might still hold.
// ===========================================================================
public class DebugOverlay : MonoBehaviour
{
    [Header("HUD")]
    public bool showHud = true;

    [Header("Gizmo toggles (Scene view)")]
    public bool showFlowField = true;
    public bool showFineField = false;   // per-cell subgrid headings (only built corridor tiles)
    public bool showBlockedCells = true;
    public bool showUnitFacing = true;
    public bool showTargetLines = false;
    public bool showDestinationLines = false;
    public bool showSelection = true;

    [Header("Gizmo tuning")]
    [Tooltip("Draw every Nth flow-field cell (higher = sparser/faster).")]
    public int flowFieldStride = 3;
    [Tooltip("Draw every Nth fine subgrid cell when showFineField is on.")]
    public int fineFieldStride = 4;
    [Tooltip("Cap on units drawn per gizmo layer (perf).")]
    public int maxGizmoUnits = 400;
    [Tooltip("Y height to lift gizmos off the ground plane.")]
    public float gizmoY = 0.2f;

    [Header("Live stats (read-only)")]
    public float fps;
    public int unitsTeam0, unitsTeam1, aliveTotal, deadTotal, projectiles;
    public int wallFormers, tuckers, kiters, advancers;
    public int overridden, firing, inContact, selected;
    public int obstacleVersion, blockedCells;
    public bool flowValid;
    public int flowFieldCount, flowBlocks;
    public Vector2Int flowGoalCell;
    public bool worldReady;

    // --- snapshots for gizmos ---
    private struct UnitGiz
    {
        public Vector3 Pos, Forward, TargetPos, DestPos;
        public int Team; public bool HasTarget, HasDest, Selected;
    }
    private readonly List<(Vector3 pos, Vector3 dir)> _flowArrows = new();
    private readonly List<(Vector3 pos, Vector3 dir)> _fineArrows = new();
    private readonly List<Vector3> _blocked = new();
    private readonly List<UnitGiz> _units = new();

    private EntityManager _em;
    private EntityQuery _debugQuery, _unitQuery, _flowQuery, _obstacleQuery;
    private bool _ready;
    private float _fpsSmooth;

    private void Start()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        worldReady = world != null && world.IsCreated;
        if (!worldReady) return;
        _em = world.EntityManager;
        _debugQuery = _em.CreateEntityQuery(typeof(SimDebug));
        _flowQuery = _em.CreateEntityQuery(typeof(NavFields));
        _obstacleQuery = _em.CreateEntityQuery(typeof(ObstacleField));
        _unitQuery = _em.CreateEntityQuery(
            ComponentType.ReadOnly<UnitTag>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<Team>(),
            ComponentType.ReadOnly<CombatTarget>(),
            ComponentType.ReadOnly<DesiredDestination>());
        _ready = true;
    }

    private void LateUpdate()
    {
        _fpsSmooth = Mathf.Lerp(_fpsSmooth, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-5f), 0.1f);
        fps = _fpsSmooth;

        if (!_ready || _em.World == null || !_em.World.IsCreated) { worldReady = false; return; }
        worldReady = true;

        PullStats();
        SnapshotGizmos();
    }

    private void PullStats()
    {
        if (_debugQuery.IsEmptyIgnoreFilter) return;
        var d = _debugQuery.GetSingleton<SimDebug>();
        unitsTeam0 = d.UnitsTeam0; unitsTeam1 = d.UnitsTeam1;
        aliveTotal = d.AliveTotal; deadTotal = d.DeadTotal; projectiles = d.Projectiles;
        wallFormers = d.WallFormers; tuckers = d.Tuckers; kiters = d.Kiters; advancers = d.Advancers;
        overridden = d.Overridden; firing = d.Firing; inContact = d.InContact;
        selected = d.Selected; obstacleVersion = d.ObstacleVersion; blockedCells = d.BlockedCells;
        flowValid = d.FlowValid != 0; flowFieldCount = d.FlowGoalHas; flowBlocks = d.FlowBlocks;
        flowGoalCell = new Vector2Int(d.FlowGoalCell.x, d.FlowGoalCell.y);
    }

    private void SnapshotGizmos()
    {
        _flowArrows.Clear(); _fineArrows.Clear(); _blocked.Clear(); _units.Clear();

        // Flow field + blocked cells (read native arrays here, on the main thread,
        // after the sim frame; copy into managed lists for the gizmo pass).
        if ((showFlowField || showFineField || showBlockedCells) &&
            _flowQuery.TryGetSingleton<NavFields>(out var nf) &&
            _obstacleQuery.TryGetSingleton<ObstacleField>(out var obs))
        {
            // Blocked fine cells.
            if (showBlockedCells && obs.Passable.IsCreated)
            {
                int stride = Mathf.Max(1, flowFieldStride);
                for (int y = 0; y < NavGrid.Res; y += stride)
                for (int x = 0; x < NavGrid.Res; x += stride)
                    if (obs.Passable[NavGrid.Index(x, y)] == 0)
                    {
                        float2 c = NavGrid.CellCenter(x, y);
                        _blocked.Add(new Vector3(c.x, gizmoY, c.y));
                    }
            }

            // Coarse big-tile heading for the most-recently-used path (the fine
            // fields are pooled per corridor tile; the coarse field is the clean
            // whole-map thing to visualize).
            int mru = -1, mruTick = int.MinValue;
            for (int s = 0; s < NavGrid.MaxPaths; s++)
                if (nf.Slots[s].Valid != 0 && nf.Slots[s].UsedTick > mruTick)
                { mruTick = nf.Slots[s].UsedTick; mru = s; }

            if (showFlowField && mru >= 0 && nf.CoarseCost.IsCreated)
            {
                int baseC = mru * NavGrid.BigCount;
                for (int by = 0; by < NavGrid.BigTilesPerAxis; by++)
                for (int bx = 0; bx < NavGrid.BigTilesPerAxis; bx++)
                {
                    int cb = nf.CoarseCost[baseC + NavGrid.BigIndex(bx, by)];
                    if (cb == int.MaxValue) continue;
                    int best = cb; int2 nb = new int2(bx, by);
                    for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        int nx = bx + ox, ny = by + oy;
                        if (!NavGrid.BigInBounds(nx, ny)) continue;
                        int c = nf.CoarseCost[baseC + NavGrid.BigIndex(nx, ny)];
                        if (c < best) { best = c; nb = new int2(nx, ny); }
                    }
                    if (nb.x == bx && nb.y == by) continue;
                    float2 c0 = NavGrid.BigCenter(new int2(bx, by));
                    float2 dir = math.normalizesafe(NavGrid.BigCenter(nb) - c0);
                    _flowArrows.Add((new Vector3(c0.x, gizmoY, c0.y),
                                     new Vector3(dir.x, 0f, dir.y) * NavGrid.SubPerAxis * 0.5f));
                }
            }

            // Per-cell fine headings for the MRU slot's built corridor tiles only
            // (the rest aren't allocated). This is the actual data units steer from.
            if (showFineField && mru >= 0 &&
                nf.FineDir.IsCreated && nf.BlockOf.IsCreated && nf.FineBigVer.IsCreated)
            {
                int fstride = Mathf.Max(1, fineFieldStride);
                int sub = NavGrid.SubPerAxis;
                for (int b = 0; b < NavGrid.BigCount; b++)
                {
                    int block = nf.BlockOf[mru * NavGrid.BigCount + b];
                    if (block < 0) continue;
                    if (nf.FineBigVer[block] != obs.BigVersion[b]) continue;  // stale block
                    int bx = b % NavGrid.BigTilesPerAxis, by = b / NavGrid.BigTilesPerAxis;
                    int2 origin = new int2(bx, by) * sub;
                    int baseC = block * NavGrid.SubCells;
                    for (int ly = 0; ly < sub; ly += fstride)
                    for (int lx = 0; lx < sub; lx += fstride)
                    {
                        float2 d = nf.FineDir[baseC + ly * sub + lx];
                        if (math.lengthsq(d) < 1e-6f) continue;
                        float2 c = NavGrid.CellCenter(origin.x + lx, origin.y + ly);
                        _fineArrows.Add((new Vector3(c.x, gizmoY, c.y),
                                         new Vector3(d.x, 0f, d.y) * fstride * 0.8f));
                    }
                }
            }
        }

        // Units (capped + strided for perf).
        var entities = _unitQuery.ToEntityArray(Allocator.Temp);
        var xforms = _unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var teams = _unitQuery.ToComponentDataArray<Team>(Allocator.Temp);
        var targets = _unitQuery.ToComponentDataArray<CombatTarget>(Allocator.Temp);
        var dests = _unitQuery.ToComponentDataArray<DesiredDestination>(Allocator.Temp);

        int total = entities.Length;
        int step = Mathf.Max(1, total / Mathf.Max(1, maxGizmoUnits));
        for (int i = 0; i < total; i += step)
        {
            float3 p = xforms[i].Position;
            float3 fwd = math.mul(xforms[i].Rotation, new float3(0, 0, 1));
            _units.Add(new UnitGiz
            {
                Pos = new Vector3(p.x, gizmoY, p.z),
                Forward = new Vector3(fwd.x, 0f, fwd.z),
                Team = teams[i].Value,
                HasTarget = targets[i].Has,
                TargetPos = new Vector3(targets[i].Position.x, gizmoY, targets[i].Position.y),
                HasDest = dests[i].Has,
                DestPos = new Vector3(dests[i].Value.x, gizmoY, dests[i].Value.y),
                Selected = _em.HasComponent<Selected>(entities[i]) && _em.IsComponentEnabled<Selected>(entities[i]),
            });
        }

        entities.Dispose(); xforms.Dispose(); teams.Dispose(); targets.Dispose(); dests.Dispose();
    }

    private void OnGUI()
    {
        if (!showHud) return;
        const int w = 250, h = 250;
        GUI.Box(new Rect(Screen.width - w - 10, 10, w, h), "SIM DEBUG");
        var r = new Rect(Screen.width - w, 35, w - 15, 18);
        void Line(string s) { GUI.Label(r, s); r.y += 17; }

        if (!worldReady) { Line("NO ECS WORLD"); return; }
        Line($"FPS: {fps:0}");
        Line($"Units  T0:{unitsTeam0}  T1:{unitsTeam1}  alive:{aliveTotal}");
        Line($"Dead:{deadTotal}   Projectiles:{projectiles}");
        Line($"Flags  Wall:{wallFormers} Tuck:{tuckers} Kite:{kiters} Adv:{advancers}");
        Line($"Overridden by hero: {overridden}");
        Line($"Combat firing:{firing}  inContact:{inContact}");
        Line($"Selected:{selected}");
        Line($"Flow paths:{flowFieldCount}/{NavGrid.MaxPaths}  fine blocks:{flowBlocks}");
        Line($"Obstacles ver:{obstacleVersion}  blocked:{blockedCells}");
    }

    private void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;

        if (showFlowField)
        {
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.7f);
            foreach (var (pos, dir) in _flowArrows) Gizmos.DrawRay(pos, dir);
        }
        if (showFineField)
        {
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.8f);   // amber = per-cell subgrid heading
            foreach (var (pos, dir) in _fineArrows) Gizmos.DrawRay(pos, dir);
        }
        if (showBlockedCells)
        {
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.5f);
            foreach (var c in _blocked) Gizmos.DrawCube(c, new Vector3(0.8f, 0.4f, 0.8f) * flowFieldStride);
        }
        foreach (var u in _units)
        {
            if (showSelection && u.Selected)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(u.Pos, 0.6f);
            }
            if (showUnitFacing)
            {
                Gizmos.color = u.Team == 0 ? new Color(0.3f, 0.6f, 1f) : new Color(1f, 0.5f, 0.3f);
                Gizmos.DrawRay(u.Pos, u.Forward * 0.8f);
            }
            if (showTargetLines && u.HasTarget)
            {
                Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.4f);
                Gizmos.DrawLine(u.Pos, u.TargetPos);
            }
            if (showDestinationLines && u.HasDest)
            {
                Gizmos.color = new Color(1f, 1f, 0.3f, 0.4f);
                Gizmos.DrawLine(u.Pos, u.DestPos);
            }
        }
    }
}
