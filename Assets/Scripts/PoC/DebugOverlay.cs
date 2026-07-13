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
//     unit's player/facing, lines to its target and its desired destination,
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
    public int unitsPlayer0, unitsPlayer1, aliveTotal, deadTotal, projectiles;
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
        public int Player; public bool HasTarget, HasDest, Selected;
    }
    private readonly List<(Vector3 pos, Vector3 dir)> _flowArrows = new();
    private readonly List<(Vector3 pos, Vector3 dir)> _fineArrows = new();
    private readonly List<Vector3> _blocked = new();
    private readonly List<Vector3> _roof = new();
    private readonly List<Vector3> _ramp = new();
    private readonly List<UnitGiz> _units = new();

    // Live readout for the first selected unit (context / surface debug).
    private bool _selHas;
    private int _selCount;   // total selected units (detail readout shows the first)
    private byte _selCtx, _selCellType;
    private float _selY, _selNavH;
    private Vector2Int _selCell;
    // Selected-unit ECONOMY readout (harvest/haul phase, cargo, and any bank on it).
    private bool _selHasHarvest, _selHasHaul, _selHasBank;
    private string _selHarvestPhase, _selHaulPhase, _selHarvestType;
    private float _selHaulTimer;
    private int _selDeposits = -1, _selRequests = -1;   // bank buffer lengths (-1 = no buffer)
    private int _selProdQueue = -1;                      // ProductionItem count (-1 = not a producer)
    private string _selProdInfo;                          // head paid/cost/progress readout
    private string _selConstrInfo;                        // construction progress/paid readout
    private int _selCargoG, _selCargoW, _selCargoF;
    private int _selBankG, _selBankW, _selBankF;
    private int _selHarvestNode = -1;
    private bool _selHasTarget, _selTargetIsNode;
    private Vector3 _selTargetWorld, _selWorld;

    private EntityManager _em;
    private EntityQuery _debugQuery, _unitQuery, _flowQuery, _obstacleQuery, _requestQuery, _stableRegQuery;
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
        _stableRegQuery = _em.CreateEntityQuery(typeof(StableIdRegistry));
        _requestQuery = _em.CreateEntityQuery(typeof(SimDebugRequest));
        _unitQuery = _em.CreateEntityQuery(
            ComponentType.ReadOnly<UnitTag>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<Player>(),
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

        // Keep SimDebugSystem alive only while this overlay is enabled. The
        // system gates on SimDebugRequest, so without this it would never run.
        if (_requestQuery.IsEmptyIgnoreFilter)
            _em.CreateEntity(typeof(SimDebugRequest));

        PullStats();
        SnapshotGizmos();
    }

    // Disabling the overlay (or play-mode exit) removes the request so the
    // scan-heavy SimDebugSystem stops running.
    private void OnDisable()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        var q = em.CreateEntityQuery(typeof(SimDebugRequest));
        if (!q.IsEmptyIgnoreFilter) em.DestroyEntity(q);
    }

    private void PullStats()
    {
        if (_debugQuery.IsEmptyIgnoreFilter) return;
        var d = _debugQuery.GetSingleton<SimDebug>();
        unitsPlayer0 = d.UnitsPlayer0; unitsPlayer1 = d.UnitsPlayer1;
        aliveTotal = d.AliveTotal; deadTotal = d.DeadTotal; projectiles = d.Projectiles;
        wallFormers = d.WallFormers; tuckers = d.Tuckers; kiters = d.Kiters; advancers = d.Advancers;
        overridden = d.Overridden; firing = d.Firing; inContact = d.InContact;
        selected = d.Selected; obstacleVersion = d.ObstacleVersion; blockedCells = d.BlockedCells;
        flowValid = d.FlowValid != 0; flowFieldCount = d.FlowGoalHas; flowBlocks = d.FlowBlocks;
        flowGoalCell = new Vector2Int(d.FlowGoalCell.x, d.FlowGoalCell.y);
    }

    private void SnapshotGizmos()
    {
        _flowArrows.Clear(); _fineArrows.Clear(); _blocked.Clear(); _roof.Clear(); _ramp.Clear(); _units.Clear();

        // Flow field + blocked cells (read native arrays here, on the main thread,
        // after the sim frame; copy into managed lists for the gizmo pass).
        if ((showFlowField || showFineField || showBlockedCells) &&
            _flowQuery.TryGetSingleton<NavFields>(out var nf) &&
            _obstacleQuery.TryGetSingleton<ObstacleField>(out var obs))
        {
            // Blocked fine cells, plus walkable wall surfaces (Roof / Transition)
            // drawn at their NavHeight so you can SEE a wall and its ramps.
            if (showBlockedCells && obs.CellType.IsCreated)
            {
                int stride = Mathf.Max(1, flowFieldStride);
                bool hasNav = obs.NavHeight.IsCreated;
                for (int y = 0; y < NavGrid.Res; y += stride)
                for (int x = 0; x < NavGrid.Res; x += stride)
                {
                    byte t = obs.CellType[NavGrid.Index(x, y)];
                    if (t == NavCell.Ground) continue;
                    float2 c = NavGrid.CellCenter(x, y);
                    // Every non-ground marker sits at the terrain SURFACE (NavHeight)
                    // plus a small lift, so it draws on top of raised terrain rather
                    // than being buried under it. Impassable cells previously used a
                    // fixed low gizmoY and vanished under any elevated ground.
                    float surf = hasNav ? obs.NavHeight[NavGrid.Index(x, y)] : 0f;
                    float h = surf + gizmoY;
                    if (t == NavCell.Impassable) _blocked.Add(new Vector3(c.x, h, c.y));
                    else if (t == NavCell.Roof) _roof.Add(new Vector3(c.x, h, c.y));
                    else if (t == NavCell.Transition) _ramp.Add(new Vector3(c.x, h, c.y));
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
                for (int by = 0; by < NavGrid.BigTilesPerAxis; by++)
                for (int bx = 0; bx < NavGrid.BigTilesPerAxis; bx++)
                {
                    int cb = CoarseMin(nf.CoarseCost, mru, NavGrid.BigIndex(bx, by));
                    if (cb == int.MaxValue) continue;
                    int best = cb; int2 nb = new int2(bx, by);
                    for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        int nx = bx + ox, ny = by + oy;
                        if (!NavGrid.BigInBounds(nx, ny)) continue;
                        int c = CoarseMin(nf.CoarseCost, mru, NavGrid.BigIndex(nx, ny));
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
        var players = _unitQuery.ToComponentDataArray<Player>(Allocator.Temp);
        var targets = _unitQuery.ToComponentDataArray<CombatTarget>(Allocator.Temp);
        var dests = _unitQuery.ToComponentDataArray<DesiredDestination>(Allocator.Temp);

        // Selected-unit surface readout. Pull NavContext + the cell under it from
        // the ObstacleField, so we can see live whether context matches the tile
        // it's standing on (and whether it jitters between roof/transition).
        _selHas = false;
        _selCount = 0;
        _selBankG = 0; _selBankW = 0; _selBankF = 0; _selHasBank = false;
        bool hasObs = _obstacleQuery.TryGetSingleton<ObstacleField>(out var obsField);
        for (int i = 0; i < entities.Length; i++)
        {
            if (!(_em.HasComponent<Selected>(entities[i]) && _em.IsComponentEnabled<Selected>(entities[i]))) continue;
            _selCount++;
            // Bank totals: SUMMED over the whole selection (3 loaded peasants show
            // the combined cargo; a selected castle shows its stores).
            if (_em.HasComponent<ResourceBank>(entities[i]))
            {
                var ba = _em.GetComponentData<ResourceBank>(entities[i]).Amounts;
                _selBankG += ba.Gold; _selBankW += ba.Wood; _selBankF += ba.Food;
                _selHasBank = true;
            }
            if (_selHas) continue;   // detail readout shows the FIRST selected; keep counting the rest
            float3 sp = xforms[i].Position;
            int2 c = NavGrid.Cell(new float2(sp.x, sp.z));
            _selHas = true;
            _selWorld = new Vector3(sp.x, sp.y, sp.z);
            _selCell = new Vector2Int(c.x, c.y);
            _selY = sp.y;
            _selCtx = _em.HasComponent<NavContext>(entities[i])
                ? _em.GetComponentData<NavContext>(entities[i]).Value : (byte)255;
            if (hasObs && NavGrid.InBounds(c.x, c.y))
            {
                _selCellType = obsField.CellType[NavGrid.Index(c.x, c.y)];
                _selNavH = obsField.NavHeight[NavGrid.Index(c.x, c.y)];
            }
            else { _selCellType = 255; _selNavH = 0f; }

            // Economy readout for the selected unit: harvest/haul phase, the node
            // it's working, its cargo, and any resource bank it carries. This is
            // what surfaces HarvestPhase and ResourceBank.Amounts that were
            // previously invisible in the overlay.
            _selHasHarvest = _em.HasComponent<HarvestTask>(entities[i]);
            if (_selHasHarvest)
            {
                var ht = _em.GetComponentData<HarvestTask>(entities[i]);
                _selHarvestPhase = ht.Phase.ToString();
                _selHarvestNode = ht.NodeStableId;
                _selHarvestType = ht.Carrying.ToString();
            }
            _selHasHaul = _em.HasComponent<HaulTask>(entities[i]);
            if (_selHasHaul)
            {
                var hl = _em.GetComponentData<HaulTask>(entities[i]);
                _selHaulPhase = hl.Phase.ToString(); _selHaulTimer = hl.Timer;
            }

            // Bank-pipeline diagnostics: pending deposits/requests on THIS entity
            // and its production queue length — pinpoints which link in a
            // colony->cart or pay->produce chain is dead.
            _selDeposits = _em.HasBuffer<BankDeposit>(entities[i]) ? _em.GetBuffer<BankDeposit>(entities[i]).Length : -1;
            _selRequests = _em.HasBuffer<BankRequest>(entities[i]) ? _em.GetBuffer<BankRequest>(entities[i]).Length : -1;
            _selProdQueue = _em.HasBuffer<ProductionItem>(entities[i]) ? _em.GetBuffer<ProductionItem>(entities[i]).Length : -1;

            // Construction site: progress + what's been paid so far.
            _selConstrInfo = null;
            if (_em.HasComponent<BlueprintTag>(entities[i]))
                _selConstrInfo = "Blueprint (plan) — awaiting a tasked builder in range";
            else if (_em.HasComponent<Construction>(entities[i]))
            {
                var cst = _em.GetComponentData<Construction>(entities[i]);
                float pct = cst.BuildTime > 0f ? 100f * cst.Progress / cst.BuildTime : 0f;
                _selConstrInfo = $"Constr {pct:0}%  paid G{cst.Paid.Gold}/{cst.Cost.Gold} " +
                                 $"W{cst.Paid.Wood}/{cst.Cost.Wood} F{cst.Paid.Food}/{cst.Cost.Food}";
            }

            // Production head: what's actually paid and how far along the build is
            // (a producer has NO bank — its escrow lives on the queue item).
            _selProdInfo = null;
            if (_selProdQueue > 0)
            {
                var h = _em.GetBuffer<ProductionItem>(entities[i])[0];
                float pct = h.BuildTime > 0f ? 100f * h.Progress / h.BuildTime : 0f;
                _selProdInfo = $"head paid G{h.Paid.Gold}/{h.Cost.Gold} W{h.Paid.Wood}/{h.Cost.Wood} " +
                               $"F{h.Paid.Food}/{h.Cost.Food}  {pct:0}%";
            }

            // (bank totals accumulated at the top of the loop across the whole selection)

            // Resolve what this unit is heading to, for the world highlight:
            // its harvest node (by StableId) if harvesting, else its MoveTarget.
            _selHasTarget = false;
            if (_selHasHarvest && _selHarvestNode >= 0 &&
                _stableRegQuery.TryGetSingleton<StableIdRegistry>(out var reg) &&
                reg.Map.TryGetValue(_selHarvestNode, out var nodeE) &&
                _em.HasComponent<LocalTransform>(nodeE))
            {
                var np = _em.GetComponentData<LocalTransform>(nodeE).Position;
                _selTargetWorld = new Vector3(np.x, np.y, np.z);
                _selTargetIsNode = true; _selHasTarget = true;
            }
            else if (_em.HasComponent<MoveTarget>(entities[i]))
            {
                var mt = _em.GetComponentData<MoveTarget>(entities[i]);
                if (mt.HasTarget)
                {
                    _selTargetWorld = new Vector3(mt.Value.x, sp.y, mt.Value.y);
                    _selTargetIsNode = false; _selHasTarget = true;
                }
            }
            // no break: keep iterating to count every selected unit (_selCount)
        }

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
                Player = players[i].Value,
                HasTarget = targets[i].Has,
                TargetPos = new Vector3(targets[i].Info.Position.x, gizmoY, targets[i].Info.Position.y),
                HasDest = dests[i].Has,
                DestPos = new Vector3(dests[i].Value.x, gizmoY, dests[i].Value.y),
                Selected = _em.HasComponent<Selected>(entities[i]) && _em.IsComponentEnabled<Selected>(entities[i]),
            });
        }

        entities.Dispose(); xforms.Dispose(); players.Dispose(); targets.Dispose(); dests.Dispose();
    }

    // Cheapest component cost of a big tile (display-grade summary of the
    // per-component coarse layout).
    private static int CoarseMin(Unity.Collections.NativeArray<int> coarse, int slot, int bi)
    {
        int baseC = (slot * NavGrid.BigCount + bi) * NavGrid.MaxComp;
        int m = int.MaxValue;
        for (int c = 0; c < NavGrid.MaxComp; c++) m = Mathf.Min(m, coarse[baseC + c]);
        return m;
    }

    private void OnGUI()
    {
        if (!showHud) return;

        // World-space TARGET HIGHLIGHT: mark where the selected unit is heading
        // (its harvest node = green, a move destination = cyan) and draw a line
        // from the unit to it. This makes "which node is it going to" obvious.
        if (worldReady && _selHas && _selHasTarget)
        {
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 tsp = cam.WorldToScreenPoint(_selTargetWorld);
                Vector3 usp = cam.WorldToScreenPoint(_selWorld);
                if (tsp.z > 0f)
                {
                    Color col = _selTargetIsNode ? new Color(0.2f, 1f, 0.3f) : new Color(0.3f, 0.9f, 1f);
                    float ty = Screen.height - tsp.y;
                    var box = new Rect(tsp.x - 14, ty - 14, 28, 28);
                    var prev = GUI.color;
                    GUI.color = col;
                    GUI.Box(box, _selTargetIsNode ? "NODE" : "GO");
                    // simple connecting line via a thin rotated box is overkill in IMGUI;
                    // a small label at the midpoint keeps it dependency-free.
                    if (usp.z > 0f)
                    {
                        float uy = Screen.height - usp.y;
                        GUI.color = new Color(col.r, col.g, col.b, 0.5f);
                        GUI.Label(new Rect((tsp.x + usp.x) * 0.5f - 20, (ty + uy) * 0.5f - 8, 40, 16), "→target");
                    }
                    GUI.color = prev;
                }
            }
        }

        const int w = 260, h = 320;
        GUI.Box(new Rect(Screen.width - w - 10, 10, w, h), "SIM DEBUG");
        var r = new Rect(Screen.width - w, 35, w - 15, 18);
        void Line(string s) { GUI.Label(r, s); r.y += 17; }

        if (!worldReady) { Line("NO ECS WORLD"); return; }
        Line($"FPS: {fps:0}");
        Line($"Units  P0:{unitsPlayer0}  P1:{unitsPlayer1}  alive:{aliveTotal}");
        Line($"Dead:{deadTotal}   Projectiles:{projectiles}");
        Line($"Flags  Wall:{wallFormers} Tuck:{tuckers} Kite:{kiters} Adv:{advancers}");
        Line($"Overridden by hero: {overridden}");
        Line($"Combat firing:{firing}  inContact:{inContact}");
        Line($"Selected:{selected}");
        Line($"Flow paths:{flowFieldCount}/{NavGrid.MaxPaths}  fine blocks:{flowBlocks}");
        Line($"Obstacles ver:{obstacleVersion}  blocked:{blockedCells}");

        if (_selHas)
        {
            string ctxName = _selCtx == 1 ? "Ground" : _selCtx == 2 ? "Roof" : _selCtx == 3 ? "Transition" : _selCtx.ToString();
            string typeName = _selCellType == 0 ? "Impassable" : _selCellType == 1 ? "Ground" : _selCellType == 2 ? "Roof" : _selCellType == 3 ? "Transition" : _selCellType.ToString();
            Line($"Sel cell:({_selCell.x},{_selCell.y}) y:{_selY:0.0} navH:{_selNavH:0.0}");
            if (_selCount > 1) Line($"({_selCount} selected — details show the FIRST)");
            Line($"Sel ctx:{ctxName}  tile:{typeName}");
            // A unit on a Transition tile should have Transition context and be
            // repelled by nothing. Flag any mismatch so jitter is visible.
            bool mismatch = (_selCellType <= 3) && (_selCtx != _selCellType)
                            && !(_selCellType == 0);
            if (mismatch) Line($"  >> CTX/TILE MISMATCH <<");

            // Economy state (only shown when the selected unit has it).
            if (_selHasHarvest)
                Line($"Harvest: {_selHarvestPhase}  node:{_selHarvestNode}  type:{_selHarvestType}");
            if (_selHasHaul)
                Line($"Haul: {_selHaulPhase} t:{_selHaulTimer:0.00}");
            if (_selDeposits >= 0 || _selRequests >= 0 || _selProdQueue >= 0)
                Line($"dep:{_selDeposits} req:{_selRequests} prodQ:{_selProdQueue}");
            if (_selProdInfo != null)
                Line(_selProdInfo);
            if (_selConstrInfo != null)
                Line(_selConstrInfo);
            if (_selHasBank)
                Line($"Bank Σ G:{_selBankG} W:{_selBankW} F:{_selBankF} ({_selCount} sel)");
        }
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

            // Walkable wall surfaces: cyan roof tops, green ramps, at their height.
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.55f);
            foreach (var c in _roof) Gizmos.DrawCube(c, new Vector3(0.85f, 0.3f, 0.85f) * NavGrid.CellSize);
            Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.6f);
            foreach (var c in _ramp) Gizmos.DrawCube(c, new Vector3(0.85f, 0.3f, 0.85f) * NavGrid.CellSize);
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
                Gizmos.color = u.Player == 0 ? new Color(0.3f, 0.6f, 1f) : new Color(1f, 0.5f, 0.3f);
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
