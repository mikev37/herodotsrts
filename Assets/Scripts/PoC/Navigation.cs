using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// EXPERIMENTAL NAVIGATION — identical tiled hierarchical engine as Navigation.cs
// EXCEPT the fine fields are solved with the EIKONAL equation (Fast Marching +
// Godunov upwind update) instead of an octile BFS/Dijkstra.
//
// Why: an incremental graph distance (cost = neighbor + step) produces diamond-
// shaped iso-cost contours, so even with a gradient readout the headings bias
// toward the axes. The Eikonal equation |grad T| = F solves for true arrival
// distance, giving round contours and smoother, more accurate gradients —
// especially around obstacles inside a tile.
//
// DROP-IN: this file REDEFINES the same public types as Navigation.cs, so the
// steering/behavior/debug systems consume it unchanged. It therefore CANNOT be
// compiled at the same time as Navigation.cs (duplicate types). Exclude one to
// test the other. The only internal change vs Navigation.cs is FineCost: float
// here (Eikonal distances) rather than int. No consumer reads FineCost, so the
// public surface is unchanged.
//
// NOTE ON SCALE: subtiles are small (SubPerAxis^2), so the Eikonal win is
// partly bounded by tile size — within one 8x8 tile the heading is dominated by
// the border seeds (which carry the coarse direction). The improvement shows up
// most around in-tile obstacles and on diagonals. Bump SubPerAxis to give the
// solver more room if you want to see the difference clearly.
//
// COARSE CONNECTIVITY (components): the coarse graph nodes are (bigTile,
// component) pairs, where components are 4-connected regions of passable cells
// within a tile (labeled incrementally in ObstacleGridSystem). Edges exist only
// where adjacent border cells are passable on both sides. Without this, a wall
// or cliff that bisects a tile leaves the tile "passable" on both halves and
// the coarse search routes straight through it — the fine field then can't
// connect and units fall back to steering into the wall. With it, the coarse
// path correctly loops around long walls/cliffs.
// ===========================================================================

public static class NavTerrain
{
    public const float SlopeCut = 1.5f;

    public static float SampleHeight(in TerrainHeightField f, float2 worldPos)
    {
        float spacing = f.WorldSize / (f.Resolution - 1);
        float2 local = (worldPos - f.Origin) / spacing;
        int x = math.clamp((int)local.x, 0, f.Resolution - 2);
        int y = math.clamp((int)local.y, 0, f.Resolution - 2);
        float fx = math.saturate(local.x - x);
        float fy = math.saturate(local.y - y);
        float h00 = f.Heights[y * f.Resolution + x];
        float h10 = f.Heights[y * f.Resolution + x + 1];
        float h01 = f.Heights[(y + 1) * f.Resolution + x];
        float h11 = f.Heights[(y + 1) * f.Resolution + x + 1];
        return math.lerp(math.lerp(h00, h10, fx), math.lerp(h01, h11, fx), fy);
    }

    // Context-aware straight-line walkability: "can a unit currently on `context`
    // walk straight from a to b without leaving cells it can stand on?" Ground
    // units cross Ground+Transition; Roof units cross Roof+Transition; neither
    // crosses the other's pure type or Impassable. This is the signal Behavior
    // uses for straight-walk vs flow field, so ground->ramp->roof returns true
    // (walk it, steering ramps height) while ground->sheer-roof returns false.
    // Also closes diagonal seepage: a diagonal step is blocked unless both
    // orthogonal shoulder cells are standable.
    public static bool LineOfSight(float2 a, float2 b, in NativeArray<byte> cellType,
                                   int maxCells, in NativeArray<float> clearance = default, int width = 1)
    {
        int2 c0 = NavGrid.Cell(a), c1 = NavGrid.Cell(b);
        int dx = math.abs(c1.x - c0.x), dy = math.abs(c1.y - c0.y);
        if (dx + dy > maxCells) return false;

        // Width gate: a wide unit only has straight-line sight if every cell on
        // the ray has room for half its body. clearance already encodes distance
        // to the nearest wall, so the centreline test is sufficient — no need to
        // sample parallel offsets. Width 1 (or no clearance field supplied) skips
        // this entirely and behaves exactly as before.
        bool useW = width > 1 && clearance.IsCreated;
        float halfW = NavGrid.HalfWidth(width);

        int sx = c1.x >= c0.x ? 1 : -1;
        int sy = c1.y >= c0.y ? 1 : -1;
        int x = c0.x, y = c0.y, err = dx - dy;

        // Walkable sightline: every STEP along the ray must be a Connected edge
        // between adjacent cells. This is what keeps a unit on a ramp (context
        // Transition) from "seeing" straight across a roof to a ground goal —
        // CanStand(Transition,*) is true for everything, so a context test would
        // pass the whole ray and flip the unit into a direct march back over the
        // wall. Connectivity breaks the ray at the sheer roof<->ground face, so
        // the unit keeps following the flow field down the ramp and around.
        // ORIGIN cell: do NOT treat the starting cell's own impassability as a
        // sight blocker. A stationary attacker (a tower) sits ON its own footprint,
        // which is stamped Impassable — checking the origin as an occluder made a
        // tower fail LoS to everything and never fire. You can always see OUT of
        // the cell you occupy; occlusion is about cells BETWEEN you and the target,
        // which the per-step Connected() test below still enforces from step 1.
        // (Pathing callers pass unit centers, which are never impassable, so this
        // is a no-op for them — it only unblocks the on-footprint case.)
        if (!NavGrid.InBounds(x, y) || cellType[NavGrid.Index(x, y)] == NavCell.Impassable) return false;
        if (useW && clearance[NavGrid.Index(x, y)] < halfW) return false;
        byte prevType = cellType[NavGrid.Index(x, y)];

        for (int guard = 0; guard <= maxCells + 2; guard++)
        {
            if (x == c1.x && y == c1.y) return true;
            int e2 = 2 * err;
            bool stepX = e2 > -dy, stepY = e2 < dx;
            if (stepX && stepY)
            {
                int hx = x + sx, hy = y + sy;
                bool shoulderX = NavGrid.InBounds(hx, y) &&
                    NavCell.Connected(prevType, cellType[NavGrid.Index(hx, y)]) &&
                    (!useW || clearance[NavGrid.Index(hx, y)] >= halfW);
                bool shoulderY = NavGrid.InBounds(x, hy) &&
                    NavCell.Connected(prevType, cellType[NavGrid.Index(x, hy)]) &&
                    (!useW || clearance[NavGrid.Index(x, hy)] >= halfW);
                if (!shoulderX || !shoulderY) return false;
            }
            if (stepX) { err -= dy; x += sx; }
            if (stepY) { err += dx; y += sy; }
            if (!NavGrid.InBounds(x, y)) return false;
            byte t = cellType[NavGrid.Index(x, y)];
            if (!NavCell.Connected(prevType, t)) return false;   // ray breaks at a non-traversable edge
            if (useW && clearance[NavGrid.Index(x, y)] < halfW) return false;   // ...or at a sub-width pinch
            prevType = t;
        }
        return true;
    }

    // ---------------------------------------------------------------------------
    // SIGHT LINE — true 2.5D height occlusion, for VISION and RANGED TARGETING.
    // Fundamentally different from LineOfSight above (which is a walkability probe
    // that breaks at sheer Roof↔Ground edges — correct for pathing, wrong for
    // sight, since it would blind a tower to the ground below it).
    //
    // Given an eye at world (a, eyeHeight) and a target at (b, targetHeight), walk
    // the ray cell by cell and track the maximum ELEVATION ANGLE any intervening
    // column's occluder top subtends from the eye. Sight is open iff no
    // intervening column rises above the straight eye→target line — i.e. the
    // angle to the target is >= every occluder angle along the way. Because the
    // test is by angle from a specific eye height, a RAISED shooter (a tall tower,
    // a unit on a parapet, a flyer) sees and shoots OVER a lower wall, while a
    // ground unit behind the same wall is blocked. Height blocks sight; pathing
    // impassability is irrelevant to it (a lake blocks movement but not vision).
    //
    // occluderHeight = ObstacleField.OccluderHeight (terrain surface + any building
    // occluder / wall parapet, baked at grid rebuild). eyeHeight/targetHeight are
    // absolute world Y (surface height + eye offset). innerRadiusCells cells around
    // the eye are always visible (you always see your immediate surroundings; a
    // corner can't hide an adjacent enemy). maxCells caps the ray length.
    // ---------------------------------------------------------------------------
    public static bool SightLine(float2 a, float2 b, float eyeHeight, float targetHeight,
                                 in NativeArray<float> occluderHeight, int maxCells,
                                 int innerRadiusCells = 2)
    {
        int2 c0 = NavGrid.Cell(a), c1 = NavGrid.Cell(b);
        int dx = math.abs(c1.x - c0.x), dy = math.abs(c1.y - c0.y);
        if (dx + dy > maxCells) return false;
        if (dx == 0 && dy == 0) return true;

        int sx = c1.x >= c0.x ? 1 : -1;
        int sy = c1.y >= c0.y ? 1 : -1;
        int x = c0.x, y = c0.y, err = dx - dy;

        // Ground distance eye→target, for converting occluder heights to angles.
        float totalGround = math.max(1e-3f, math.distance(a, b));
        // Slope (rise per unit ground) of the straight eye→target sightline.
        float targetSlope = (targetHeight - eyeHeight) / totalGround;

        int steps = 0;
        int guard = maxCells + 2;
        while (guard-- > 0)
        {
            if (x == c1.x && y == c1.y) return true;   // reached target column: open

            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x += sx; }
            if (e2 <  dx) { err += dx; y += sy; }
            steps++;

            if (x == c1.x && y == c1.y) return true;
            if (!NavGrid.InBounds(x, y)) return false;
            if (steps <= innerRadiusCells) continue;   // always-visible inner radius

            // Elevation angle (as slope) of THIS column's occluder top from the eye.
            float2 cellC = NavGrid.CellCenter(x, y);
            float ground = math.max(1e-3f, math.distance(a, cellC));
            float occl = occluderHeight[NavGrid.Index(x, y)];
            float occlSlope = (occl - eyeHeight) / ground;

            // Blocked if this occluder rises above the eye→target line at this point.
            // Small epsilon so a wall exactly at sightline height doesn't false-block.
            if (occlSlope > targetSlope + 1e-3f) return false;
        }
        return false;
    }
}

public static class NavGrid
{
    public const int BigTilesPerAxis = 20;
    public const int SubPerAxis      = 50;
    public const float CellSize      = 2f;

    public const int Res       = BigTilesPerAxis * SubPerAxis;
    public const int CellCount = Res * Res;
    public const int BigCount  = BigTilesPerAxis * BigTilesPerAxis;
    public const int SubCells  = SubPerAxis * SubPerAxis;
    public const float WorldSize = Res * CellSize;

    public const int MaxPaths      = 128;
    public const int MaxFineBlocks = 4096;
    public const int MaxComp       = 8;   // raised from 4: the width-eroded graph splits union components

    // ---- variable-width pathing: width in cells, 1 = point (original behaviour) ----
    public const int   MaxWidth      = 64;    // largest supported width in cells (key packing + cache)
    public const int   MaxWidthSlots = 8;     // distinct widths kept resident in the component cache (LRU)
    public const float MaxClearance  = 16f;   // clearance cap in cells; bounds the seam-bleed a near-edge
                                              // structure can have on a tile's checksum (Trap-2 fix)

    public static float2 Origin => new float2(-WorldSize * 0.5f, -WorldSize * 0.5f);

    // Clearance threshold for a width-W body: wall-adjacent cells read 0.5, so width 1 admits all passable.
    public static float HalfWidth(int width) => 0.5f * math.max(1, width);

    // Does a width-W body fit centred on this cell? The one context-free passability test.
    public static bool Fits(in NativeArray<float> clearance, int cellIndex, int width) =>
        clearance[cellIndex] >= HalfWidth(width);

    // Flow-field cache key: (goal cell index, width) -> slot. width is clamped
    // into [1, MaxWidth] so the packing never collides across goals.
    public static int PathKey(int goalCellIndex, int width) =>
        goalCellIndex * MaxWidth + math.clamp(width, 1, MaxWidth);

    // Uphill direction of the clearance field at `cell`, for walking a stranded
    // wide unit back to room its body fits. Samples only Connected neighbours so
    // it never points through a wall.
    public static float2 ClearanceGradient(in NativeArray<float> clearance,
                                           in NativeArray<byte> cellType, int2 cell)
    {
        byte t = cellType[Index(cell.x, cell.y)];
        float h = clearance[Index(cell.x, cell.y)];
        float r = ClearanceAt(clearance, cellType, cell.x + 1, cell.y, t, h);
        float l = ClearanceAt(clearance, cellType, cell.x - 1, cell.y, t, h);
        float u = ClearanceAt(clearance, cellType, cell.x, cell.y + 1, t, h);
        float d = ClearanceAt(clearance, cellType, cell.x, cell.y - 1, t, h);
        return math.normalizesafe(new float2(r - l, u - d));
    }

    private static float ClearanceAt(in NativeArray<float> clearance, in NativeArray<byte> cellType,
                                     int x, int y, byte fromType, float fallback)
    {
        if (!InBounds(x, y)) return fallback;
        int i = Index(x, y);
        return NavCell.Connected(fromType, cellType[i]) ? clearance[i] : fallback;
    }

    public static int2 Cell(float2 world)
    {
        float2 local = (world - Origin) / CellSize;
        return new int2((int)math.floor(local.x), (int)math.floor(local.y));
    }
    public static int Index(int x, int y) => y * Res + x;
    public static int Index(int2 c) => c.y * Res + c.x;
    public static bool InBounds(int x, int y) => x >= 0 && x < Res && y >= 0 && y < Res;
    public static float2 CellCenter(int x, int y) =>
        Origin + new float2(x + 0.5f, y + 0.5f) * CellSize;

    public static int2 BigOf(int2 cell) => cell / SubPerAxis;
    public static int  BigIndex(int bx, int by) => by * BigTilesPerAxis + bx;
    public static int  BigIndex(int2 b) => b.y * BigTilesPerAxis + b.x;
    public static bool BigInBounds(int bx, int by) =>
        bx >= 0 && bx < BigTilesPerAxis && by >= 0 && by < BigTilesPerAxis;
    public static int2 BigCellOrigin(int2 b) => b * SubPerAxis;
    public static int  SubIndex(int2 cell)
    {
        int2 l = cell - BigOf(cell) * SubPerAxis;
        return l.y * SubPerAxis + l.x;
    }
    public static float2 BigCenter(int2 b) =>
        Origin + (new float2(b.x, b.y) * SubPerAxis + SubPerAxis * 0.5f) * CellSize;
}

public struct ObstacleField : IComponentData
{
    public NativeArray<byte> Passable;   // UNION view: 0 = Impassable, 1 = walkable by someone (Ground/Roof/Transition)
    public NativeArray<byte> CellType;   // NavCell.* per cell — the typed surface (read by LoS + steering repulsion)
    public NativeArray<float> NavHeight; // walk-surface Y for Roof/Transition cells (Ground cells use terrain)
    public NativeArray<float> OccluderHeight; // per-cell SIGHT-blocking top height: terrain surface, plus a
                                         // building's occluderHeight on its footprint, or a wall's RoofHeight
                                         // on Roof cells. Read by NavTerrain.SightLine (2.5D height occlusion).
                                         // Distinct from NavHeight (walk surface) — a tall keep blocks sight
                                         // from its full height while its walk surface is irrelevant to vision.
    public NativeArray<float> Clearance; // CELL distance from each cell centre to the nearest BROKEN edge
                                         // (impassable neighbour, sheer ground<->roof face, or map border).
                                         // Context-free; thresholded by HalfWidth(W) to test fit for any
                                         // width. Capped at MaxClearance. Derived; rebuilt with the grid.
    public NativeArray<byte> CellComp;   // component id of each cell WITHIN its big tile (255 = impassable)
    public NativeArray<byte> CompCount;  // number of components per big tile
    public NativeArray<int>  BigVersion;
    public int Version;
    public int CoarseVersion;
}

public struct PathSlot
{
    public int2 GoalCell;
    public int2 GoalBig;
    public int  Width;                  // path width in cells this slot's field was solved for
    public int  BuiltCoarseVersion;
    public int  UsedTick;
    public byte Valid;
}

// Same as Navigation.cs but FineCost is FLOAT (Eikonal arrival distances).
public struct NavFields : IComponentData
{
    public NativeArray<int>      CoarseCost;
    public NativeArray<PathSlot> Slots;
    public NativeArray<float2>   FineDir;
    public NativeArray<float>    FineCost;     // <-- float (Eikonal T), was int
    public NativeArray<int>      FineBigVer;
    public NativeArray<int>      BlockOf;
    public NativeArray<int>      BlockKey;
    public NativeArray<int>      BlockUsed;
    public int Tick;
}

public struct PathLookup : IComponentData
{
    public NativeParallelHashMap<int, int> Map;
}

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SpatialHashSystem))]
public partial struct ObstacleGridSystem : ISystem
{
    private NativeArray<byte> _passable;
    private NativeArray<byte> _cellType;
    private NativeArray<float> _navHeight;
    private NativeArray<float> _occluderHeight;
    private NativeArray<float> _clearance;
    private NativeArray<byte> _cellComp;
    private NativeArray<byte> _compCount;
    private NativeArray<int>  _bigVersion;
    private NativeArray<int>  _bigChecksum;
    private bool              _slopeStamped;

    // Structural dirty tracking. The full grid rebuild (reset + stamp every
    // obstacle/wall + re-checksum a million cells) only needs to run when the
    // SET of structures changes — a build, a death, or (defensively) a move.
    // We hash the obstacle+wall set cheaply each frame and skip the entire
    // rebuild when it matches last frame. _forceRebuild covers the first frame
    // and the one-time slope/water bake.
    private uint              _structSignature;
    private bool              _forceRebuild;
    // Cells marked impassable by terrain slope, computed once at first valid
    // terrain and OR'd into passable[] each frame after the obstacle pass.
    private NativeArray<byte> _slopeBlock;

    public void OnCreate(ref SystemState state)
    {
        _passable    = new NativeArray<byte>(NavGrid.CellCount, Allocator.Persistent);
        _cellType    = new NativeArray<byte>(NavGrid.CellCount, Allocator.Persistent);
        _navHeight   = new NativeArray<float>(NavGrid.CellCount, Allocator.Persistent);
        _occluderHeight = new NativeArray<float>(NavGrid.CellCount, Allocator.Persistent);
        _clearance   = new NativeArray<float>(NavGrid.CellCount, Allocator.Persistent);
        _cellComp    = new NativeArray<byte>(NavGrid.CellCount, Allocator.Persistent);
        _compCount   = new NativeArray<byte>(NavGrid.BigCount, Allocator.Persistent);
        _bigVersion  = new NativeArray<int>(NavGrid.BigCount, Allocator.Persistent);
        _bigChecksum = new NativeArray<int>(NavGrid.BigCount, Allocator.Persistent);
        _slopeBlock  = new NativeArray<byte>(NavGrid.CellCount, Allocator.Persistent);
        for (int i = 0; i < _bigChecksum.Length; i++) _bigChecksum[i] = int.MinValue;
        _forceRebuild = true;   // first frame always builds

        state.EntityManager.AddComponentData(state.EntityManager.CreateEntity(),
            new ObstacleField
            {
                Passable = _passable, CellType = _cellType, NavHeight = _navHeight, OccluderHeight = _occluderHeight,
                Clearance = _clearance,
                CellComp = _cellComp, CompCount = _compCount,
                BigVersion = _bigVersion, Version = 0, CoarseVersion = 0,
            });
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_passable.IsCreated)    _passable.Dispose();
        if (_cellType.IsCreated)    _cellType.Dispose();
        if (_navHeight.IsCreated)   _navHeight.Dispose();
        if (_occluderHeight.IsCreated) _occluderHeight.Dispose();
        if (_clearance.IsCreated)   _clearance.Dispose();
        if (_cellComp.IsCreated)    _cellComp.Dispose();
        if (_compCount.IsCreated)   _compCount.Dispose();
        if (_bigVersion.IsCreated)  _bigVersion.Dispose();
        if (_bigChecksum.IsCreated) _bigChecksum.Dispose();
        if (_slopeBlock.IsCreated)  _slopeBlock.Dispose();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var fieldRef = SystemAPI.GetSingletonRW<ObstacleField>();
        var passable = fieldRef.ValueRO.Passable;
        var cellType = fieldRef.ValueRO.CellType;
        var navHeight = fieldRef.ValueRO.NavHeight;
        var clearance = fieldRef.ValueRO.Clearance;
        var cellComp = fieldRef.ValueRO.CellComp;
        var compCount = fieldRef.ValueRO.CompCount;
        var bigVer   = fieldRef.ValueRO.BigVersion;

        bool hasTerrain = SystemAPI.TryGetSingleton<TerrainHeightField>(out var terrain) && terrain.IsValid;

        // ---- structural dirty check -------------------------------------------
        // Hash the obstacle+wall SET (position cell + extents + shape). This is a
        // walk over a handful of structure entities, not the million-cell grid.
        // If it matches last frame and nothing forces a rebuild (first frame, or
        // the one-time slope/water bake becoming available), skip the entire
        // reset/stamp/checksum/relabel pass — the grid can't have changed.
        uint sig = 2166136261u;   // FNV-1a seed
        foreach (var (xform, obs) in SystemAPI.Query<RefRO<LocalTransform>, RefRO<Obstacle>>().WithNone<Dead>())
        {
            int2 c = NavGrid.Cell(new float2(xform.ValueRO.Position.x, xform.ValueRO.Position.z));
            sig = Fnv(sig, (uint)c.x); sig = Fnv(sig, (uint)c.y);
            sig = Fnv(sig, (uint)obs.ValueRO.Extents.x); sig = Fnv(sig, (uint)obs.ValueRO.Extents.y);
            sig = Fnv(sig, math.asuint(obs.ValueRO.Radius));
            sig = Fnv(sig, math.asuint(obs.ValueRO.OccluderHeight));
        }
        foreach (var (xform, wall) in SystemAPI.Query<RefRO<LocalTransform>, RefRO<Wall>>().WithNone<Dead>())
        {
            int2 c = NavGrid.Cell(new float2(xform.ValueRO.Position.x, xform.ValueRO.Position.z));
            sig = Fnv(sig, (uint)c.x); sig = Fnv(sig, (uint)c.y);
            sig = Fnv(sig, (uint)wall.ValueRO.Extents.x); sig = Fnv(sig, (uint)wall.ValueRO.Extents.y);
            sig = Fnv(sig, math.asuint(wall.ValueRO.RoofHeight));
            sig = Fnv(sig, (uint)wall.ValueRO.RampSide);
            sig = Fnv(sig, (uint)wall.ValueRO.RampCells);
            sig = Fnv(sig, 0x5A5A5A5Au);   // domain-separate walls from obstacles
        }
        // The slope/water bake becomes available the first frame terrain exists;
        // force one rebuild then so it gets applied.
        bool slopeNowAvailable = !_slopeStamped && hasTerrain;
        if (!_forceRebuild && !slopeNowAvailable && sig == _structSignature)
            return;   // nothing changed — the expensive rebuild is skipped entirely

        _structSignature = sig;
        _forceRebuild = false;

        // ---- full rebuild (only reached on a structural change) ----------------
        // Reset: every cell is plain Ground with no nav-height override. Occluder
        // height starts at the terrain surface — the natural ground blocks sight
        // from below it (hills occlude); structures add to it below.
        var occl = fieldRef.ValueRO.OccluderHeight;
        for (int i = 0; i < cellType.Length; i++) { cellType[i] = NavCell.Ground; navHeight[i] = 0f; }
        for (int y = 0; y < NavGrid.Res; y++)
            for (int x = 0; x < NavGrid.Res; x++)
            {
                int i = NavGrid.Index(x, y);
                occl[i] = hasTerrain ? NavTerrain.SampleHeight(terrain, NavGrid.CellCenter(x, y)) : 0f;
            }

        // Dead buildings stop blocking immediately (the corpse lingers for the
        // death anim, but pathing opens up the tick health hits zero).
        foreach (var (xform, obs) in SystemAPI.Query<RefRO<LocalTransform>, RefRO<Obstacle>>().WithNone<Dead>())
        {
            float2 p = new float2(xform.ValueRO.Position.x, xform.ValueRO.Position.z);
            int2 e = obs.ValueRO.Extents;

            if (e.x > 0 && e.y > 0)
            {
                int2 min = BuildingFootprint.MinCell(p, e);
                float obsOccl = obs.ValueRO.OccluderHeight;
                for (int ly = 0; ly < e.y; ly++)
                for (int lx = 0; lx < e.x; lx++)
                {
                    if (BuildingFootprint.CornerCut(lx, ly, e)) continue;
                    int x = min.x + lx, y = min.y + ly;
                    if (!NavGrid.InBounds(x, y)) continue;
                    int idx = NavGrid.Index(x, y);
                    cellType[idx] = NavCell.Impassable;
                    // Sight-block up to occluderHeight above this cell's ground. A tall
                    // keep occludes; a 0-height footprint blocks pathing but not sight.
                    occl[idx] = occl[idx] + obsOccl;
                }
                continue;
            }

            // Circle (doodads / legacy).
            int2 c = NavGrid.Cell(p);
            int r = (int)math.ceil(obs.ValueRO.Radius / NavGrid.CellSize);
            for (int oy = -r; oy <= r; oy++)
            for (int ox = -r; ox <= r; ox++)
            {
                int x = c.x + ox, y = c.y + oy;
                if (!NavGrid.InBounds(x, y)) continue;
                if (ox * ox + oy * oy <= r * r) cellType[NavGrid.Index(x, y)] = NavCell.Impassable;
            }
        }

        // Walls: a Roof top (walkable) with a Transition skirt one cell out on
        // every cardinal side, so units climb on from any approachable ground
        // cell with no designated entrance. Stamped AFTER obstacles so a wall
        // wins over an overlapping plain building footprint.
        foreach (var (xform, wall) in SystemAPI.Query<RefRO<LocalTransform>, RefRO<Wall>>().WithNone<Dead>())
        {
            float2 p = new float2(xform.ValueRO.Position.x, xform.ValueRO.Position.z);
            int2 e = wall.ValueRO.Extents;
            int2 min = BuildingFootprint.MinCell(p, e);
            float topY = wall.ValueRO.RoofHeight;

            for (int ly = 0; ly < e.y; ly++)
            for (int lx = 0; lx < e.x; lx++)
            {
                int x = min.x + lx, y = min.y + ly;
                if (!NavGrid.InBounds(x, y)) continue;
                int idx = NavGrid.Index(x, y);
                cellType[idx] = NavCell.Roof;
                navHeight[idx] = topY;
                occl[idx] = math.max(occl[idx], topY);   // parapet blocks sight up to its top
            }

            // Transition skirt: rampCells concentric cardinal rings stepping out
            // from the footprint, each at a graduated height so the climb eases
            // from ground up to the roof instead of jumping. Ring 1 (closest to
            // the wall) is highest, the outermost ring lowest. Only Ground cells
            // become ramp (don't carve through another wall's roof/footprint).
            int ramp = math.max(1, wall.ValueRO.RampCells);
            byte rampSide = wall.ValueRO.RampSide;
            const byte SIDE_ALL = 0, SIDE_PX = 1, SIDE_MX = 2, SIDE_PZ = 3, SIDE_MZ = 4, SIDE_NONE = 5;
            if (rampSide != SIDE_NONE)
            for (int r = 1; r <= ramp; r++)
            {
                // Height for this ring: fraction 1.0 just inside the wall, down
                // toward the ground at the outer edge.
                float inner = (ramp - r + 1) / (float)(ramp + 1);
                for (int ly = -r; ly <= e.y - 1 + r; ly++)
                for (int lx = -r; lx <= e.x - 1 + r; lx++)
                {
                    int dxOut = lx < 0 ? -lx : (lx >= e.x ? lx - (e.x - 1) : 0);
                    int dyOut = ly < 0 ? -ly : (ly >= e.y ? ly - (e.y - 1) : 0);
                    int ringDist = math.max(dxOut, dyOut);
                    if (ringDist != r) continue;
                    if (dxOut > 0 && dyOut > 0) continue;   // skip diagonal corners

                    // Which face is this skirt cell on? (cardinal, so exactly one)
                    bool onPX = lx >= e.x, onMX = lx < 0, onPZ = ly >= e.y, onMZ = ly < 0;
                    bool sideAllowed = rampSide == SIDE_ALL
                        || (rampSide == SIDE_PX && onPX)
                        || (rampSide == SIDE_MX && onMX)
                        || (rampSide == SIDE_PZ && onPZ)
                        || (rampSide == SIDE_MZ && onMZ);
                    if (!sideAllowed) continue;

                    int x = min.x + lx, y = min.y + ly;
                    if (!NavGrid.InBounds(x, y)) continue;
                    int idx = NavGrid.Index(x, y);
                    if (cellType[idx] != NavCell.Ground) continue;
                    float groundY = hasTerrain ? NavTerrain.SampleHeight(terrain, NavGrid.CellCenter(x, y)) : 0f;
                    cellType[idx] = NavCell.Transition;
                    navHeight[idx] = math.lerp(groundY, topY, inner);
                }
            }
        }

        // Stamp steep-slope AND below-waterline cells once (terrain never changes
        // after construction). _slopeBlock[i] == 1 means terrain-blocked; applied
        // below as Impassable, after the obstacle/wall pass.
        if (!_slopeStamped && hasTerrain)
        {
            float waterLevel = terrain.WaterLevel;
            for (int y = 0; y < NavGrid.Res; y++)
            for (int x = 0; x < NavGrid.Res; x++)
            {
                float hC = NavTerrain.SampleHeight(terrain, NavGrid.CellCenter(x, y));
                if (hC < waterLevel) { _slopeBlock[NavGrid.Index(x, y)] = 1; continue; }
                float hR = NavTerrain.SampleHeight(terrain, NavGrid.CellCenter(math.min(x + 1, NavGrid.Res - 1), y));
                float hL = NavTerrain.SampleHeight(terrain, NavGrid.CellCenter(math.max(x - 1, 0), y));
                float hU = NavTerrain.SampleHeight(terrain, NavGrid.CellCenter(x, math.min(y + 1, NavGrid.Res - 1)));
                float hD = NavTerrain.SampleHeight(terrain, NavGrid.CellCenter(x, math.max(y - 1, 0)));
                float grad = math.max(math.abs(hR - hL), math.abs(hU - hD)) * 0.5f;
                if (grad > NavTerrain.SlopeCut)
                    _slopeBlock[NavGrid.Index(x, y)] = 1;
            }
            _slopeStamped = true;
        }

        // Apply terrain mask: a slope/water cell is Impassable unless a wall or
        // ramp deliberately made it Roof/Transition (a wall may cross water).
        if (_slopeStamped)
        {
            for (int i = 0; i < cellType.Length; i++)
                if (_slopeBlock[i] == 1 && cellType[i] == NavCell.Ground)
                    cellType[i] = NavCell.Impassable;
        }

        // Derive the UNION passability the connectivity machinery consumes.
        for (int i = 0; i < passable.Length; i++) passable[i] = NavCell.ToPassable(cellType[i]);

        // Wall-distance clearance: for each cell, the cell-distance from its
        // centre to the nearest BROKEN edge (a neighbour it isn't NavCell.
        // Connected to — impassable, the sheer side of a wall — or the map
        // border). One context-free scalar; thresholded per query by HalfWidth.
        // Folded into the per-tile checksum below so a structure near a tile
        // SEAM (whose clearance bleeds up to MaxClearance cells into the
        // neighbour without changing that neighbour's union passability) still
        // bumps the neighbour's BigVersion and rebuilds wide-unit fields there.
        ComputeClearance(cellType, clearance);

        bool anyMoved = false;
        var floodStack = new NativeArray<int>(NavGrid.SubCells, Allocator.Temp);
        for (int by = 0; by < NavGrid.BigTilesPerAxis; by++)
        for (int bx = 0; bx < NavGrid.BigTilesPerAxis; bx++)
        {
            int b = NavGrid.BigIndex(bx, by);
            int2 origin = new int2(bx, by) * NavGrid.SubPerAxis;
            int sum = 0;
            for (int ly = 0; ly < NavGrid.SubPerAxis; ly++)
            for (int lx = 0; lx < NavGrid.SubPerAxis; lx++)
            {
                int ci = NavGrid.Index(origin.x + lx, origin.y + ly);
                sum = sum * 31 + passable[ci];
                // Quantise clearance (0.5-cell steps, capped) into the same hash
                // so any change to the eroded graph — not just the union graph —
                // dirties this tile.
                sum = sum * 31 + (int)(math.min(clearance[ci], NavGrid.MaxClearance) * 2f);
            }
            if (sum != _bigChecksum[b])
            {
                _bigChecksum[b] = sum;
                bigVer[b]++;
                anyMoved = true;
                LabelTile(cellType, cellComp, compCount, origin, b, floodStack);
            }
        }
        floodStack.Dispose();

        if (anyMoved)
        {
            fieldRef.ValueRW.Version++;
            // Any cell-level change can alter component structure or border
            // connectivity, both of which the coarse component graph depends
            // on, so the coarse fields must rebuild.
            fieldRef.ValueRW.CoarseVersion++;
        }
    }

    // Label 4-connected components of passable cells within one big tile.
    // Component ids are LOCAL to the tile (0..MaxComp-1); 255 = impassable.
    // More than MaxComp components in an 8x8 tile is pathological; overflow
    // regions merge into the last id (may create false intra-tile connectivity
    // there, never false blockage).
    // FNV-1a step — folds one uint into the running structural signature hash.
    private static uint Fnv(uint h, uint v)
    {
        h ^= v & 0xFF;          h *= 16777619u;
        h ^= (v >> 8) & 0xFF;   h *= 16777619u;
        h ^= (v >> 16) & 0xFF;  h *= 16777619u;
        h ^= (v >> 24) & 0xFF;  h *= 16777619u;
        return h;
    }

    // Two-pass chamfer distance transform giving each passable cell the
    // Euclidean-ish CELL distance from its centre to the nearest broken edge.
    // A "broken edge" is a boundary to a cell this one is NOT NavCell.Connected
    // to (impassable, or the sheer face of a wall) or the map border. Seeds sit
    // at 0.5 (half a cell from the touched face); impassable cells are 0.
    // Propagation crosses only Connected edges so distance never leaks THROUGH a
    // wall, and the diagonal cost is sqrt(2) so diagonal gaps aren't over-wide
    // (Manhattan would read a sqrt(2) gap as 2). Deterministic raster order.
    private static void ComputeClearance(NativeArray<byte> cellType, NativeArray<float> dist)
    {
        const float INF = 1e9f;
        float d1 = 1f, d2 = math.sqrt(2f);
        int res = NavGrid.Res;

        for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
        {
            int i = NavGrid.Index(x, y);
            byte t = cellType[i];
            if (t == NavCell.Impassable) { dist[i] = 0f; continue; }
            float d = INF;
            if (x == 0       || !NavCell.Connected(t, cellType[NavGrid.Index(x - 1, y)])) d = 0.5f;
            if (x == res - 1 || !NavCell.Connected(t, cellType[NavGrid.Index(x + 1, y)])) d = 0.5f;
            if (y == 0       || !NavCell.Connected(t, cellType[NavGrid.Index(x, y - 1)])) d = 0.5f;
            if (y == res - 1 || !NavCell.Connected(t, cellType[NavGrid.Index(x, y + 1)])) d = 0.5f;
            dist[i] = d;
        }

        // forward pass: pull from already-settled W, NW, N, NE neighbours
        for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
        {
            int i = NavGrid.Index(x, y);
            byte t = cellType[i];
            if (t == NavCell.Impassable) continue;
            float d = dist[i];
            d = ClearRelax(d, t, x - 1, y,     d1, cellType, dist, res);
            d = ClearRelax(d, t, x - 1, y - 1, d2, cellType, dist, res);
            d = ClearRelax(d, t, x,     y - 1, d1, cellType, dist, res);
            d = ClearRelax(d, t, x + 1, y - 1, d2, cellType, dist, res);
            dist[i] = d;
        }
        // backward pass: pull from E, SE, S, SW; cap at MaxClearance
        for (int y = res - 1; y >= 0; y--)
        for (int x = res - 1; x >= 0; x--)
        {
            int i = NavGrid.Index(x, y);
            byte t = cellType[i];
            if (t == NavCell.Impassable) continue;
            float d = dist[i];
            d = ClearRelax(d, t, x + 1, y,     d1, cellType, dist, res);
            d = ClearRelax(d, t, x + 1, y + 1, d2, cellType, dist, res);
            d = ClearRelax(d, t, x,     y + 1, d1, cellType, dist, res);
            d = ClearRelax(d, t, x - 1, y + 1, d2, cellType, dist, res);
            dist[i] = math.min(d, NavGrid.MaxClearance);
        }
    }

    private static float ClearRelax(float d, byte fromType, int nx, int ny, float cost,
                                    NativeArray<byte> cellType, NativeArray<float> dist, int res)
    {
        if (nx < 0 || nx >= res || ny < 0 || ny >= res) return d;
        int ni = NavGrid.Index(nx, ny);
        if (!NavCell.Connected(fromType, cellType[ni])) return d;   // never flow across a wall
        return math.min(d, dist[ni] + cost);
    }

    private static void LabelTile(NativeArray<byte> cellType, NativeArray<byte> cellComp,
                                  NativeArray<byte> compCount, int2 origin, int b,
                                  NativeArray<int> stack)
    {
        int sub = NavGrid.SubPerAxis;
        for (int ly = 0; ly < sub; ly++)
        for (int lx = 0; lx < sub; lx++)
            cellComp[NavGrid.Index(origin.x + lx, origin.y + ly)] = 255;

        byte comps = 0;
        for (int ly = 0; ly < sub; ly++)
        for (int lx = 0; lx < sub; lx++)
        {
            int idx = NavGrid.Index(origin.x + lx, origin.y + ly);
            // Seed a new component from any standable, unlabelled cell. Impassable
            // stays 255. The flood below only crosses Connected edges, so a Roof
            // region and a Ground region in the same tile get DIFFERENT ids —
            // they're joined only where a Transition bridges them. This is what
            // makes one shared field route ground units around a wall and roof
            // units along it: they live in different components of the graph.
            if (cellType[idx] == NavCell.Impassable || cellComp[idx] != 255) continue;
            byte id = comps < NavGrid.MaxComp ? comps : (byte)(NavGrid.MaxComp - 1);
            if (comps < NavGrid.MaxComp) comps++;

            int top = 0;
            stack[top++] = ly * sub + lx;
            cellComp[idx] = id;
            while (top > 0)
            {
                int si = stack[--top];
                int cx = si % sub, cy = si / sub;
                byte fromType = cellType[NavGrid.Index(origin.x + cx, origin.y + cy)];
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || nx >= sub || ny < 0 || ny >= sub) continue;
                    int nidx = NavGrid.Index(origin.x + nx, origin.y + ny);
                    if (cellComp[nidx] != 255) continue;
                    if (!NavCell.Connected(fromType, cellType[nidx])) continue;   // type-aware edge
                    cellComp[nidx] = id;
                    stack[top++] = ny * sub + nx;
                }
            }
        }
        compCount[b] = comps;
    }
}

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ObstacleGridSystem))]
public partial struct FlowFieldSystem : ISystem
{
    private NativeArray<int>      _coarse;
    private NativeArray<PathSlot> _slots;
    private NativeArray<float2>   _fineDir;
    private NativeArray<float>    _fineCost;
    private NativeArray<int>      _fineBigVer;
    private NativeArray<int>      _blockOf;
    private NativeArray<int>      _blockKey;
    private NativeArray<int>      _blockUsed;
    private NativeParallelHashMap<int, int> _pathMap;

    // ---- per-width connected-component cache (lazily built) ----------------
    // The union CellComp/CompCount in ObstacleField label the grid for a point
    // unit. A width-W unit sees an ERODED graph (cells with clearance < W/2 are
    // walls), which can split a union component into several. We label that
    // eroded graph per distinct width, ON DEMAND, and cache up to MaxWidthSlots
    // widths (LRU). Each resident width keeps a full-grid CellComp, a per-big-
    // tile CompCount, and the BigVersion each tile was last labelled against —
    // so a structural change relabels only the dirtied tiles, only for widths
    // actually in use. This is what lets formations of arbitrary, caller-chosen
    // width path the whole map without precomputing every size.
    private NativeArray<byte> _wComp;       // [MaxWidthSlots * CellCount]
    private NativeArray<byte> _wCompCount;  // [MaxWidthSlots * BigCount]
    private NativeArray<int>  _wTileVer;    // [MaxWidthSlots * BigCount]  (BigVersion labelled against)
    private NativeArray<int>  _wWidth;      // [MaxWidthSlots]             (width resident here; -1 empty)
    private NativeArray<int>  _wUsed;       // [MaxWidthSlots]             (LRU tick)

    private const int SeedScale = NavGrid.SubPerAxis * 14;

    public void OnCreate(ref SystemState state)
    {
        _coarse     = new NativeArray<int>(NavGrid.MaxPaths * NavGrid.BigCount * NavGrid.MaxComp, Allocator.Persistent);
        _slots      = new NativeArray<PathSlot>(NavGrid.MaxPaths, Allocator.Persistent);
        _fineDir    = new NativeArray<float2>(NavGrid.MaxFineBlocks * NavGrid.SubCells, Allocator.Persistent);
        _fineCost   = new NativeArray<float>(NavGrid.MaxFineBlocks * NavGrid.SubCells, Allocator.Persistent);
        _fineBigVer = new NativeArray<int>(NavGrid.MaxFineBlocks, Allocator.Persistent);
        _blockOf    = new NativeArray<int>(NavGrid.MaxPaths * NavGrid.BigCount, Allocator.Persistent);
        _blockKey   = new NativeArray<int>(NavGrid.MaxFineBlocks, Allocator.Persistent);
        _blockUsed  = new NativeArray<int>(NavGrid.MaxFineBlocks, Allocator.Persistent);
        _pathMap    = new NativeParallelHashMap<int, int>(NavGrid.MaxPaths * 2, Allocator.Persistent);
        for (int i = 0; i < _blockOf.Length; i++)  _blockOf[i]  = -1;
        for (int i = 0; i < _blockKey.Length; i++) _blockKey[i] = -1;

        _wComp      = new NativeArray<byte>(NavGrid.MaxWidthSlots * NavGrid.CellCount, Allocator.Persistent);
        _wCompCount = new NativeArray<byte>(NavGrid.MaxWidthSlots * NavGrid.BigCount, Allocator.Persistent);
        _wTileVer   = new NativeArray<int>(NavGrid.MaxWidthSlots * NavGrid.BigCount, Allocator.Persistent);
        _wWidth     = new NativeArray<int>(NavGrid.MaxWidthSlots, Allocator.Persistent);
        _wUsed      = new NativeArray<int>(NavGrid.MaxWidthSlots, Allocator.Persistent);
        for (int i = 0; i < _wWidth.Length; i++) _wWidth[i] = -1;

        state.EntityManager.AddComponentData(state.EntityManager.CreateEntity(), new NavFields
        {
            CoarseCost = _coarse, Slots = _slots, FineDir = _fineDir, FineCost = _fineCost,
            FineBigVer = _fineBigVer, BlockOf = _blockOf, BlockKey = _blockKey, BlockUsed = _blockUsed, Tick = 0,
        });
        state.EntityManager.AddComponentData(state.EntityManager.CreateEntity(),
            new PathLookup { Map = _pathMap });
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_coarse.IsCreated)     _coarse.Dispose();
        if (_slots.IsCreated)      _slots.Dispose();
        if (_fineDir.IsCreated)    _fineDir.Dispose();
        if (_fineCost.IsCreated)   _fineCost.Dispose();
        if (_fineBigVer.IsCreated) _fineBigVer.Dispose();
        if (_blockOf.IsCreated)    _blockOf.Dispose();
        if (_blockKey.IsCreated)   _blockKey.Dispose();
        if (_blockUsed.IsCreated)  _blockUsed.Dispose();
        if (_pathMap.IsCreated)    _pathMap.Dispose();
        if (_wComp.IsCreated)      _wComp.Dispose();
        if (_wCompCount.IsCreated) _wCompCount.Dispose();
        if (_wTileVer.IsCreated)   _wTileVer.Dispose();
        if (_wWidth.IsCreated)     _wWidth.Dispose();
        if (_wUsed.IsCreated)      _wUsed.Dispose();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var obs = SystemAPI.GetSingleton<ObstacleField>();
        var nf  = SystemAPI.GetSingletonRW<NavFields>();
        var lookup = SystemAPI.GetSingletonRW<PathLookup>();

        int tick = nf.ValueRO.Tick + 1;
        nf.ValueRW.Tick = tick;

        var slots     = nf.ValueRW.Slots;
        var coarse    = nf.ValueRW.CoarseCost;
        var blockOf   = nf.ValueRW.BlockOf;
        var blockKey  = nf.ValueRW.BlockKey;
        var blockUsed = nf.ValueRW.BlockUsed;
        var fineBigVer = nf.ValueRW.FineBigVer;
        var map = lookup.ValueRW.Map;
        map.Clear();

        // Distinct (goalCell, width) pairs in flight this tick. Width travels on
        // DesiredDestination.PathWidth (the explicit caller input); <=1 means a
        // point unit and reproduces the original behaviour exactly.
        var goals  = new NativeList<int3>(Allocator.Temp);   // (gc.x, gc.y, width)
        var widths = new NativeList<int>(Allocator.Temp);    // distinct active widths
        foreach (var dest in SystemAPI.Query<RefRO<DesiredDestination>>())
        {
            if (!dest.ValueRO.Has || !dest.ValueRO.UseFlowField) continue;
            int2 gc = NavGrid.Cell(dest.ValueRO.Value);
            if (!NavGrid.InBounds(gc.x, gc.y)) continue;
            int w = math.clamp(math.max(1, dest.ValueRO.PathWidth), 1, NavGrid.MaxWidth);

            // A goal's width must be resident in the component cache this tick.
            // Bound distinct widths to MaxWidthSlots so the lazy cache never has
            // to evict a width another goal still needs within the same tick.
            // Excess distinct widths spill (those units fall back to straight
            // line for a tick), exactly as excess goals spill past MaxPaths.
            bool wseen = false;
            for (int i = 0; i < widths.Length; i++) if (widths[i] == w) { wseen = true; break; }
            if (!wseen && widths.Length >= NavGrid.MaxWidthSlots) continue;

            bool seen = false;
            for (int i = 0; i < goals.Length; i++)
                if (goals[i].x == gc.x && goals[i].y == gc.y && goals[i].z == w) { seen = true; break; }
            if (!seen && goals.Length < NavGrid.MaxPaths) goals.Add(new int3(gc.x, gc.y, w));
            if (!wseen) widths.Add(w);
        }

        // Bring every active width's eroded-graph labels up to date (only the
        // tiles whose BigVersion moved are relabelled — see EnsureWidthLabels).
        var labelStack = new NativeArray<int>(NavGrid.SubCells, Allocator.Temp);
        for (int i = 0; i < widths.Length; i++)
            GetWidthCache(widths[i], tick, obs, labelStack);

        for (int g = 0; g < goals.Length; g++)
        {
            int2 gc = new int2(goals[g].x, goals[g].y);
            int  w  = goals[g].z;
            int ci  = FindWidthCache(w);
            if (ci < 0) continue;   // width not resident (shouldn't happen: capped above)
            var wComp      = _wComp.GetSubArray(ci * NavGrid.CellCount, NavGrid.CellCount);
            var wCompCount = _wCompCount.GetSubArray(ci * NavGrid.BigCount, NavGrid.BigCount);

            int slot = FindSlot(slots, gc, w);
            bool fresh = slot >= 0;
            if (slot < 0) slot = AllocSlot(slots, tick);
            var sl = slots[slot];
            sl.GoalCell = gc; sl.GoalBig = NavGrid.BigOf(gc); sl.Width = w; sl.UsedTick = tick; sl.Valid = 1;

            if (!fresh || sl.BuiltCoarseVersion != obs.CoarseVersion)
            {
                BuildCoarse(coarse, slot, gc, wComp, wCompCount);
                sl.BuiltCoarseVersion = obs.CoarseVersion;
                int baseK = slot * NavGrid.BigCount;
                for (int b = 0; b < NavGrid.BigCount; b++)
                {
                    int blk = blockOf[baseK + b];
                    if (blk >= 0) { blockKey[blk] = -1; blockOf[baseK + b] = -1; }
                }
            }
            slots[slot] = sl;
            map.TryAdd(NavGrid.PathKey(NavGrid.Index(gc), w), slot);
        }

        var seenNodes = new NativeHashSet<int>(256, Allocator.Temp);
        var seenTiles = new NativeHashSet<int>(256, Allocator.Temp);
        var keys  = new NativeList<int>(Allocator.Temp);
        foreach (var (xform, dest) in SystemAPI.Query<RefRO<LocalTransform>, RefRO<DesiredDestination>>())
        {
            if (!dest.ValueRO.Has || !dest.ValueRO.UseFlowField) continue;
            int w  = math.clamp(math.max(1, dest.ValueRO.PathWidth), 1, NavGrid.MaxWidth);
            int gi = NavGrid.Index(NavGrid.Cell(dest.ValueRO.Value));
            if (!map.TryGetValue(NavGrid.PathKey(gi, w), out int slot)) continue;
            int2 cell = NavGrid.Cell(new float2(xform.ValueRO.Position.x, xform.ValueRO.Position.z));
            if (!NavGrid.InBounds(cell.x, cell.y)) continue;
            int ci = FindWidthCache(w);
            if (ci < 0) continue;
            var wComp      = _wComp.GetSubArray(ci * NavGrid.CellCount, NavGrid.CellCount);
            var wCompCount = _wCompCount.GetSubArray(ci * NavGrid.BigCount, NavGrid.BigCount);
            MarkCorridor(seenNodes, seenTiles, keys, coarse, wComp, wCompCount, slot, cell);
        }

        var requests = new NativeList<int3>(Allocator.TempJob);   // passed to a job
        for (int i = 0; i < keys.Length; i++)
        {
            int key  = keys[i];
            int slot = key / NavGrid.BigCount;
            int big  = key % NavGrid.BigCount;
            int block = blockOf[key];
            int bv = obs.BigVersion[big];
            bool needs = block < 0 || fineBigVer[block] != bv;
            if (block < 0) block = AllocBlock(blockKey, blockUsed, blockOf, key, tick);
            blockUsed[block] = tick;
            if (needs) requests.Add(new int3(block, slot, big));
        }

        if (requests.Length > 0)
        {
            // Sort goal-outward (per slot, ascending coarse cost) so a tile's
            // downstream neighbour is always built before it -> its border can be
            // seeded from the neighbour's fresh fine costs. Insertion sort: the
            // request count is small (corridor tiles needing a rebuild).
            var ra = requests.AsArray();
            for (int i = 1; i < ra.Length; i++)
            {
                int3 cur = ra[i];
                long key = SortKey(cur, coarse);
                int j = i - 1;
                while (j >= 0 && SortKey(ra[j], coarse) > key) { ra[j + 1] = ra[j]; j--; }
                ra[j + 1] = cur;
            }

            // The Eikonal solver floods over the WIDTH-eroded graph, so its
            // CellComp (border seeding) and clearance gate depend on the slot's
            // width. Requests carry mixed widths; run one job per distinct width,
            // each fed that width's component labels and HalfWidth threshold. The
            // per-slot goal-outward ordering is preserved inside each width's
            // filtered sublist, and cross-seam seeding only ever reads a same-slot
            // (hence same-width) neighbour block, so the split is exact.
            var sub = new NativeList<int3>(Allocator.TempJob);
            for (int wi = 0; wi < widths.Length; wi++)
            {
                int w  = widths[wi];
                int ci = FindWidthCache(w);
                if (ci < 0) continue;
                sub.Clear();
                for (int i = 0; i < ra.Length; i++)
                    if (slots[ra[i].y].Width == w) sub.Add(ra[i]);
                if (sub.Length == 0) continue;

                new EikonalFineBuildJob
                {
                    Requests   = sub.AsArray(),
                    Coarse     = coarse,
                    Slots      = slots,
                    Passable   = obs.Passable,
                    CellType   = obs.CellType,
                    Clearance  = obs.Clearance,
                    HalfW      = NavGrid.HalfWidth(w),
                    CellComp   = _wComp.GetSubArray(ci * NavGrid.CellCount, NavGrid.CellCount),
                    BlockOf    = blockOf,
                    FineDir    = nf.ValueRW.FineDir,
                    FineCost   = nf.ValueRW.FineCost,
                    FineBigVer = fineBigVer,
                    BigVersion = obs.BigVersion,
                }.Run();   // sequential build; Run() executes the Burst job on this thread
                           // (synchronous like the old Complete(), and Temp-safe)
            }
            sub.Dispose();
        }

        goals.Dispose(); widths.Dispose(); labelStack.Dispose();
        seenNodes.Dispose(); seenTiles.Dispose(); keys.Dispose(); requests.Dispose();
    }

    // ---- per-width component cache ----------------------------------------
    // Return the cache row holding width w, allocating/evicting (LRU) if needed,
    // and relabel any tiles whose BigVersion has moved since this row last saw
    // them. On a fresh allocation every tile is stale, so the whole grid is
    // labelled for w once; thereafter only structurally-changed tiles relabel.
    private int GetWidthCache(int w, int tick, in ObstacleField obs, NativeArray<int> stack)
    {
        int ci = FindWidthCache(w);
        if (ci < 0)
        {
            int pick = 0, oldest = int.MaxValue;
            for (int s = 0; s < NavGrid.MaxWidthSlots; s++)
            {
                if (_wWidth[s] < 0) { pick = s; oldest = -1; break; }
                if (_wUsed[s] < oldest) { oldest = _wUsed[s]; pick = s; }
            }
            ci = pick;
            _wWidth[ci] = w;
            int vbase = ci * NavGrid.BigCount;
            for (int b = 0; b < NavGrid.BigCount; b++) _wTileVer[vbase + b] = int.MinValue;   // force full relabel
        }
        _wUsed[ci] = tick;

        float halfW = NavGrid.HalfWidth(w);
        int cbase = ci * NavGrid.CellCount;
        int bbase = ci * NavGrid.BigCount;
        var comp      = _wComp.GetSubArray(cbase, NavGrid.CellCount);
        var compCount = _wCompCount.GetSubArray(bbase, NavGrid.BigCount);
        for (int by = 0; by < NavGrid.BigTilesPerAxis; by++)
        for (int bx = 0; bx < NavGrid.BigTilesPerAxis; bx++)
        {
            int b = NavGrid.BigIndex(bx, by);
            if (_wTileVer[bbase + b] == obs.BigVersion[b]) continue;
            int2 origin = new int2(bx, by) * NavGrid.SubPerAxis;
            LabelTileW(obs.CellType, obs.Clearance, halfW, comp, compCount, origin, b, stack);
            _wTileVer[bbase + b] = obs.BigVersion[b];
        }
        return ci;
    }

    private int FindWidthCache(int w)
    {
        for (int s = 0; s < NavGrid.MaxWidthSlots; s++) if (_wWidth[s] == w) return s;
        return -1;
    }

    // Label 4-connected components of the WIDTH-ERODED graph within one big tile:
    // a cell is open iff it is standable (not Impassable) AND has clearance for
    // half the width. Identical to ObstacleGridSystem.LabelTile when halfW = 0.5
    // (every passable cell qualifies), so width 1 yields the union labelling.
    private static void LabelTileW(NativeArray<byte> cellType, NativeArray<float> clearance, float halfW,
                                   NativeArray<byte> cellComp, NativeArray<byte> compCount,
                                   int2 origin, int b, NativeArray<int> stack)
    {
        int sub = NavGrid.SubPerAxis;
        for (int ly = 0; ly < sub; ly++)
        for (int lx = 0; lx < sub; lx++)
            cellComp[NavGrid.Index(origin.x + lx, origin.y + ly)] = 255;

        byte comps = 0;
        for (int ly = 0; ly < sub; ly++)
        for (int lx = 0; lx < sub; lx++)
        {
            int idx = NavGrid.Index(origin.x + lx, origin.y + ly);
            bool open = cellType[idx] != NavCell.Impassable && clearance[idx] >= halfW;
            if (!open || cellComp[idx] != 255) continue;
            byte id = comps < NavGrid.MaxComp ? comps : (byte)(NavGrid.MaxComp - 1);
            if (comps < NavGrid.MaxComp) comps++;

            int top = 0;
            stack[top++] = ly * sub + lx;
            cellComp[idx] = id;
            while (top > 0)
            {
                int si = stack[--top];
                int cx = si % sub, cy = si / sub;
                byte fromType = cellType[NavGrid.Index(origin.x + cx, origin.y + cy)];
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || nx >= sub || ny < 0 || ny >= sub) continue;
                    int nidx = NavGrid.Index(origin.x + nx, origin.y + ny);
                    if (cellComp[nidx] != 255) continue;
                    if (cellType[nidx] == NavCell.Impassable || clearance[nidx] < halfW) continue;  // width-eroded
                    if (!NavCell.Connected(fromType, cellType[nidx])) continue;                     // type-aware edge
                    cellComp[nidx] = id;
                    stack[top++] = ny * sub + nx;
                }
            }
        }
        compCount[b] = comps;
    }

    private static long SortKey(int3 req, NativeArray<int> coarse)
    {
        // req = (block, slot, big). Group by slot, then ascending coarse cost
        // (the tile's cheapest component -> goal-outward build order).
        int baseI = (req.y * NavGrid.BigCount + req.z) * NavGrid.MaxComp;
        int cost = int.MaxValue;
        for (int c = 0; c < NavGrid.MaxComp; c++) cost = math.min(cost, coarse[baseI + c]);
        return (long)req.y * (1L << 20) + math.min(cost, (1 << 20) - 1);
    }

    private static int FindSlot(NativeArray<PathSlot> slots, int2 gc, int width)
    {
        for (int s = 0; s < NavGrid.MaxPaths; s++)
            if (slots[s].Valid != 0 && math.all(slots[s].GoalCell == gc) && slots[s].Width == width) return s;
        return -1;
    }
    private static int AllocSlot(NativeArray<PathSlot> slots, int tick)
    {
        int oldest = int.MaxValue, pick = 0;
        for (int s = 0; s < NavGrid.MaxPaths; s++)
        {
            if (slots[s].Valid == 0) return s;
            if (slots[s].UsedTick < oldest) { oldest = slots[s].UsedTick; pick = s; }
        }
        return pick;
    }
    private static int AllocBlock(NativeArray<int> blockKey, NativeArray<int> blockUsed,
                                  NativeArray<int> blockOf, int key, int tick)
    {
        int oldest = int.MaxValue, pick = 0, free = -1;
        for (int b = 0; b < NavGrid.MaxFineBlocks; b++)
        {
            if (blockKey[b] == -1) { free = b; break; }
            if (blockUsed[b] < oldest) { oldest = blockUsed[b]; pick = b; }
        }
        int blk = free >= 0 ? free : pick;
        if (free < 0 && blockKey[blk] >= 0) blockOf[blockKey[blk]] = -1;
        blockKey[blk] = key; blockOf[key] = blk; blockUsed[blk] = tick;
        return blk;
    }

    // Coarse search over (bigTile, component) nodes. Components are connected
    // regions of passable cells within one big tile (labeled in
    // ObstacleGridSystem); edges exist only where adjacent border cells are
    // passable on BOTH sides. This is what lets the coarse field route AROUND
    // a wall or cliff that bisects a tile (or runs along a tile seam) instead
    // of pretending the tile is open. Cardinal edges only: a diagonal tile
    // move always passes through one of the two orthogonal tiles anyway, and
    // unchecked diagonal hops could tunnel through wall corners. Uniform step
    // cost (10) makes the plain FIFO queue BFS-exact.
    private static void BuildCoarse(NativeArray<int> coarse, int slot, int2 goalCell,
                                    NativeArray<byte> cellComp, NativeArray<byte> compCount)
    {
        int baseI = slot * NavGrid.BigCount * NavGrid.MaxComp;
        for (int i = 0; i < NavGrid.BigCount * NavGrid.MaxComp; i++) coarse[baseI + i] = int.MaxValue;

        int2 goalBig = NavGrid.BigOf(goalCell);
        int gb = NavGrid.BigIndex(goalBig);
        var q = new NativeQueue<int3>(Allocator.Temp);            // (bx, by, comp)

        byte gcomp = cellComp[NavGrid.Index(goalCell)];
        if (gcomp != 255)
        {
            coarse[baseI + gb * NavGrid.MaxComp + gcomp] = 0;
            q.Enqueue(new int3(goalBig.x, goalBig.y, gcomp));
        }
        else
        {
            // Goal sits on a blocked cell: seed every component of the goal
            // tile so units approach it and local steering takes over nearby.
            for (int c = 0; c < compCount[gb]; c++)
            {
                coarse[baseI + gb * NavGrid.MaxComp + c] = 0;
                q.Enqueue(new int3(goalBig.x, goalBig.y, c));
            }
        }

        int sub = NavGrid.SubPerAxis;
        while (q.TryDequeue(out int3 node))
        {
            int2 b = new int2(node.x, node.y);
            int comp = node.z;
            int cb = coarse[baseI + NavGrid.BigIndex(b) * NavGrid.MaxComp + comp];
            int2 origin = b * sub;

            for (int e = 0; e < 4; e++)
            {
                int nbx = b.x + (e == 0 ? -1 : e == 1 ? 1 : 0);
                int nby = b.y + (e == 2 ? -1 : e == 3 ? 1 : 0);
                if (!NavGrid.BigInBounds(nbx, nby)) continue;
                int nbi = NavGrid.BigIndex(nbx, nby);

                for (int t = 0; t < sub; t++)
                {
                    int lx = e == 0 ? 0 : e == 1 ? sub - 1 : t;
                    int ly = e == 2 ? 0 : e == 3 ? sub - 1 : t;
                    int2 cell = origin + new int2(lx, ly);
                    if (cellComp[NavGrid.Index(cell)] != comp) continue;
                    int2 ncell = new int2(e == 0 ? cell.x - 1 : e == 1 ? cell.x + 1 : cell.x,
                                          e == 2 ? cell.y - 1 : e == 3 ? cell.y + 1 : cell.y);
                    byte nc = cellComp[NavGrid.Index(ncell)];
                    if (nc == 255) continue;
                    int ni = baseI + nbi * NavGrid.MaxComp + nc;
                    if (cb + 10 < coarse[ni])
                    {
                        coarse[ni] = cb + 10;
                        q.Enqueue(new int3(nbx, nby, nc));
                    }
                }
            }
        }
        q.Dispose();
    }

    // Walk downhill through the component graph from the unit's (tile,
    // component) toward the goal, marking each visited TILE for fine-field
    // build. The descent follows real border connections (the same edges
    // BuildCoarse used), so it can't hop a wall into a cheaper-but-unreachable
    // component. seenNodes dedups traversal across units; seenTiles dedups the
    // keys list (a corridor can pass through one tile twice via different
    // components, but the tile's fine field is built once and holds both).
    private static void MarkCorridor(NativeHashSet<int> seenNodes, NativeHashSet<int> seenTiles,
                                     NativeList<int> keys, NativeArray<int> coarse,
                                     NativeArray<byte> cellComp, NativeArray<byte> compCount,
                                     int slot, int2 startCell)
    {
        int baseI = slot * NavGrid.BigCount * NavGrid.MaxComp;
        int sub = NavGrid.SubPerAxis;
        int2 b = NavGrid.BigOf(startCell);
        int comp = cellComp[NavGrid.Index(startCell)];
        if (comp == 255)
        {
            // Unit is standing on a blocked cell (e.g. an obstacle stamped
            // under it this frame): start from the tile's cheapest component.
            int bi0 = NavGrid.BigIndex(b);
            int bestC0 = int.MaxValue;
            for (int c = 0; c < compCount[bi0]; c++)
            {
                int cc = coarse[baseI + bi0 * NavGrid.MaxComp + c];
                if (cc < bestC0) { bestC0 = cc; comp = c; }
            }
            if (comp == 255) return;   // tile fully blocked
        }

        for (int guard = 0; guard < NavGrid.BigCount; guard++)
        {
            int bi = NavGrid.BigIndex(b);
            int tileKey = slot * NavGrid.BigCount + bi;
            if (!seenNodes.Add(tileKey * NavGrid.MaxComp + comp)) break;  // rest already marked
            if (seenTiles.Add(tileKey)) keys.Add(tileKey);

            int myCost = coarse[baseI + bi * NavGrid.MaxComp + comp];
            if (myCost == 0 || myCost == int.MaxValue) break;

            int best = myCost; int2 nbBest = b; int compBest = comp;
            int2 origin = b * sub;
            for (int e = 0; e < 4; e++)
            {
                int nbx = b.x + (e == 0 ? -1 : e == 1 ? 1 : 0);
                int nby = b.y + (e == 2 ? -1 : e == 3 ? 1 : 0);
                if (!NavGrid.BigInBounds(nbx, nby)) continue;
                int nbi = NavGrid.BigIndex(nbx, nby);
                for (int t = 0; t < sub; t++)
                {
                    int lx = e == 0 ? 0 : e == 1 ? sub - 1 : t;
                    int ly = e == 2 ? 0 : e == 3 ? sub - 1 : t;
                    int2 cell = origin + new int2(lx, ly);
                    if (cellComp[NavGrid.Index(cell)] != comp) continue;
                    int2 ncell = new int2(e == 0 ? cell.x - 1 : e == 1 ? cell.x + 1 : cell.x,
                                          e == 2 ? cell.y - 1 : e == 3 ? cell.y + 1 : cell.y);
                    byte nc = cellComp[NavGrid.Index(ncell)];
                    if (nc == 255) continue;
                    int c = coarse[baseI + nbi * NavGrid.MaxComp + nc];
                    if (c < best) { best = c; nbBest = new int2(nbx, nby); compBest = nc; }
                }
            }
            if (best == myCost) break;
            b = nbBest; comp = compBest;
        }
    }

    // ---- EIKONAL fine-field builder (Fast Marching + Godunov upwind) ---------
    // SEQUENTIAL by design: Requests are pre-sorted goal-outward (ascending coarse
    // cost) per slot, so when a tile seeds its border it can read the already-built
    // fine costs of its downstream neighbour at the shared cells. That makes the
    // distance field continuous across big-tile seams (no flat equipotential edge),
    // which removes the cardinal collapse at boundaries. The price is that the build
    // can't parallelise across tiles within a corridor.
    [BurstCompile]
    private struct EikonalFineBuildJob : IJob
    {
        [ReadOnly] public NativeArray<int3>      Requests;   // (block, slot, big), sorted goal-outward
        [ReadOnly] public NativeArray<int>       Coarse;
        [ReadOnly] public NativeArray<PathSlot>  Slots;
        [ReadOnly] public NativeArray<byte>      Passable;
        [ReadOnly] public NativeArray<byte>      CellType;
        [ReadOnly] public NativeArray<float>     Clearance;  // wall-distance field (cells); width gate
        public float HalfW;                                  // HalfWidth(slot width) — the clearance threshold
        [ReadOnly] public NativeArray<byte>      CellComp;   // WIDTH-eroded components for this job's width
        [ReadOnly] public NativeArray<int>       BigVersion;
        [ReadOnly] public NativeArray<int>       BlockOf;    // slot*BigCount+big -> block (neighbour lookup)

        public NativeArray<float2> FineDir;
        public NativeArray<float>  FineCost;
        public NativeArray<int>    FineBigVer;

        // Slowness per cell. Set so a cardinal step ~ 10 (diagonal ~14.1 falls out
        // of the solver), matching the coarse octile scale + SeedScale.
        private const float EikF = 10f;
        private const float INF  = 1e15f;

        // Width-aware standability: a cell holds cost for this job iff a body of
        // the job's width fits centred on it. The one predicate that turns the
        // point-unit solver into a width-W solver; HalfW = 0.5 (width 1) admits
        // every non-impassable cell, so width 1 is byte-identical to before.
        private bool StandW(int2 c)
        {
            int i = NavGrid.Index(c);
            return CellType[i] != NavCell.Impassable && Clearance[i] >= HalfW;
        }
        // A traversable edge for this width: both ends fit AND the surfaces are
        // NavCell.Connected (no sheer ground<->roof step).
        private bool EdgeW(int2 a, int2 b) =>
            StandW(a) && StandW(b) &&
            NavCell.Connected(CellType[NavGrid.Index(a)], CellType[NavGrid.Index(b)]);

        public void Execute()
        {
            for (int r = 0; r < Requests.Length; r++)
                BuildTile(Requests[r]);
        }

        private void BuildTile(int3 req)
        {
            int block = req.x, slot = req.y, big = req.z;
            int2 bcoord = new int2(big % NavGrid.BigTilesPerAxis, big / NavGrid.BigTilesPerAxis);
            int2 cellOrigin = bcoord * NavGrid.SubPerAxis;
            int baseCost = block * NavGrid.SubCells;
            int baseKey  = slot * NavGrid.BigCount;                     // BlockOf indexing
            int baseC    = slot * NavGrid.BigCount * NavGrid.MaxComp;   // coarse cost indexing
            int sub = NavGrid.SubPerAxis;

            // state: 0 far, 1 trial (in heap), 2 frozen (final). Heap as parallel
            // (key,idx) arrays with lazy decrease-key (stale entries skipped).
            var stateArr = new NativeArray<byte>(NavGrid.SubCells, Allocator.Temp);
            int heapCap = NavGrid.SubCells * 8;
            var hk = new NativeArray<float>(heapCap, Allocator.Temp);
            var hv = new NativeArray<int>(heapCap, Allocator.Temp);
            int hn = 0;

            for (int i = 0; i < NavGrid.SubCells; i++) FineCost[baseCost + i] = INF;

            // --- seeds (frozen boundary conditions) ---
            PathSlot sl = Slots[slot];
            if (math.all(sl.GoalBig == bcoord) && StandW(sl.GoalCell))
            {
                int2 gl = sl.GoalCell - cellOrigin;
                int gi = gl.y * sub + gl.x;
                FineCost[baseCost + gi] = 0f;
                stateArr[gi] = 2;
            }
            // --- border seeds ---
            // For each border cell pair (this tile <-> cardinal neighbour),
            // compare the COMPONENT coarse costs of the two cells. Seed where
            // the neighbour side is strictly cheaper (closer to the goal). The
            // per-pair test replaces the old per-tile test so a bisected tile
            // seeds each component only from edges its component actually
            // connects through. If the neighbour's fine field is built, seed
            // from its adjacent cell cost (+ one step) so the heading carries
            // through the seam; otherwise fall back to the coarse scalar.
            for (int e = 0; e < 4; e++)
            {
                int nbx = bcoord.x + (e == 0 ? -1 : e == 1 ? 1 : 0);
                int nby = bcoord.y + (e == 2 ? -1 : e == 3 ? 1 : 0);
                if (!NavGrid.BigInBounds(nbx, nby)) continue;
                int nbBig = NavGrid.BigIndex(nbx, nby);

                int nbBlock = BlockOf[baseKey + nbBig];
                bool nbReady = nbBlock >= 0 && FineBigVer[nbBlock] == BigVersion[nbBig];
                int nbBase = nbBlock * NavGrid.SubCells;
                int2 nbOrigin = new int2(nbx, nby) * sub;

                for (int t = 0; t < sub; t++)
                {
                    int lx = e == 0 ? 0 : e == 1 ? sub - 1 : t;
                    int ly = e == 2 ? 0 : e == 3 ? sub - 1 : t;
                    int2 cell = cellOrigin + new int2(lx, ly);
                    if (!StandW(cell)) continue;

                    int2 ncell = new int2(e == 0 ? cell.x - 1 : e == 1 ? cell.x + 1 : cell.x,
                                          e == 2 ? cell.y - 1 : e == 3 ? cell.y + 1 : cell.y);
                    if (!NavGrid.InBounds(ncell.x, ncell.y)) continue;
                    // Width-aware border edge: connect components across the tile
                    // boundary only when a body of this width can actually pass
                    // between the two cells (both fit, surfaces Connected) — never
                    // across a sheer step or through a sub-width pinch.
                    if (!EdgeW(cell, ncell)) continue;

                    int myCost = Coarse[baseC + big   * NavGrid.MaxComp + CellComp[NavGrid.Index(cell)]];
                    int nbCost = Coarse[baseC + nbBig * NavGrid.MaxComp + CellComp[NavGrid.Index(ncell)]];
                    if (nbCost == int.MaxValue || nbCost >= myCost) continue;

                    float seed;
                    if (nbReady)
                    {
                        int nbSub = (ncell.y - nbOrigin.y) * sub + (ncell.x - nbOrigin.x);
                        float nbVal = FineCost[nbBase + nbSub];
                        if (nbVal >= INF) continue;     // neighbour cell unreachable -> not a real exit
                        seed = nbVal + EikF;            // one cardinal step further from the goal
                    }
                    else
                    {
                        seed = nbCost * (float)SeedScale;   // fallback: coarse scalar (old behaviour)
                    }

                    int si = ly * sub + lx;
                    if (seed < FineCost[baseCost + si]) { FineCost[baseCost + si] = seed; stateArr[si] = 2; }
                }
            }

            // --- initialise trial band from frozen seeds ---
            for (int si = 0; si < NavGrid.SubCells; si++)
            {
                if (stateArr[si] != 2) continue;
                RelaxNeighbours(si, sub, cellOrigin, baseCost, stateArr, hk, hv, ref hn);
            }

            // --- Fast Marching loop ---
            while (hn > 0)
            {
                HeapPop(hk, hv, ref hn, out float k, out int si);
                if (stateArr[si] == 2) continue;            // stale
                if (k > FineCost[baseCost + si] + 1e-3f) continue; // stale (better value pushed)
                stateArr[si] = 2;
                RelaxNeighbours(si, sub, cellOrigin, baseCost, stateArr, hk, hv, ref hn);
            }

            // --- direction = downhill gradient of the (smooth) distance field ---
            for (int ly = 0; ly < sub; ly++)
            for (int lx = 0; lx < sub; lx++)
            {
                int si = ly * sub + lx;
                FineDir[baseCost + si] = float2.zero;
                float cc = FineCost[baseCost + si];
                if (cc >= INF) continue;
                int2 here = new int2(cellOrigin.x + lx, cellOrigin.y + ly);
                byte ct = CellType[NavGrid.Index(here)];
                if (!StandW(here)) continue;   // no heading on a cell this width can't occupy

                // Gradient only over CONNECTED neighbours. Without this, a roof
                // edge cell sees the cheap ground cell across the sheer face
                // (flooded from the ground side toward the same goal) and points
                // the unit straight into the wall; it then presses the edge
                // instead of routing along the roof to a ramp.
                float cR = SubCostOr(baseCost, lx + 1, ly, sub, cellOrigin, ct, cc);
                float cL = SubCostOr(baseCost, lx - 1, ly, sub, cellOrigin, ct, cc);
                float cU = SubCostOr(baseCost, lx, ly + 1, sub, cellOrigin, ct, cc);
                float cD = SubCostOr(baseCost, lx, ly - 1, sub, cellOrigin, ct, cc);
                FineDir[baseCost + si] = -math.normalizesafe(new float2(cR - cL, cU - cD));
            }

            FineBigVer[block] = BigVersion[big];

            stateArr.Dispose(); hk.Dispose(); hv.Dispose();
        }

        // Recompute Eikonal T for each non-frozen, passable, non-cut neighbour of
        // a just-frozen cell; lower its cost and (re)push it onto the heap.
        private void RelaxNeighbours(int si, int sub, int2 cellOrigin, int baseCost,
                                     NativeArray<byte> stateArr,
                                     NativeArray<float> hk, NativeArray<int> hv, ref int hn)
        {
            int lx = si % sub, ly = si / sub;
            for (int d = 0; d < 4; d++)
            {
                int nlx = lx + (d == 0 ? 1 : d == 1 ? -1 : 0);
                int nly = ly + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nlx < 0 || nlx >= sub || nly < 0 || nly >= sub) continue;
                int nsi = nly * sub + nlx;
                if (stateArr[nsi] == 2) continue;
                int2 ncell = cellOrigin + new int2(nlx, nly);
                // Width-aware: cost flows only across an edge a body of this width
                // can traverse (both ends fit, surfaces Connected), so the field
                // never propagates through a sub-width pinch or a sheer step.
                if (!EdgeW(cellOrigin + new int2(lx, ly), ncell)) continue;

                float t = SolveEikonal(nlx, nly, sub, cellOrigin, baseCost, stateArr);
                if (t < FineCost[baseCost + nsi])
                {
                    FineCost[baseCost + nsi] = t;
                    stateArr[nsi] = 1;
                    HeapPush(hk, hv, ref hn, t, nsi);
                }
            }
        }

        // Godunov upwind solution of |grad T| = EikF at one cell, using the best
        // known (frozen-or-lower) neighbour in each axis. Cliff/blocked edges are
        // excluded so distance can't flow across them.
        private float SolveEikonal(int lx, int ly, int sub, int2 cellOrigin, int baseCost,
                                   NativeArray<byte> stateArr)
        {
            float a = AxisMin(lx, ly, sub, cellOrigin, baseCost, stateArr, true);   // horizontal
            float b = AxisMin(lx, ly, sub, cellOrigin, baseCost, stateArr, false);  // vertical
            if (a >= INF && b >= INF) return INF;

            float lo = math.min(a, b);
            if (math.abs(a - b) >= EikF) return lo + EikF;                // one-sided
            return 0.5f * (a + b + math.sqrt(2f * EikF * EikF - (a - b) * (a - b)));
        }

        private float AxisMin(int lx, int ly, int sub, int2 cellOrigin, int baseCost,
                              NativeArray<byte> stateArr, bool horizontal)
        {
            float best = INF;
            for (int s = -1; s <= 1; s += 2)
            {
                int nlx = lx + (horizontal ? s : 0);
                int nly = ly + (horizontal ? 0 : s);
                if (nlx < 0 || nlx >= sub || nly < 0 || nly >= sub) continue;
                if (stateArr[nly * sub + nlx] != 2) continue;            // upwind: frozen neighbors only
                int2 ncell = cellOrigin + new int2(nlx, nly);
                if (!EdgeW(cellOrigin + new int2(lx, ly), ncell)) continue;
                best = math.min(best, FineCost[baseCost + nly * sub + nlx]);
            }
            return best;
        }

        private float SubCostOr(int baseCost, int lx, int ly, int sub, int2 cellOrigin, byte fromType, float fallback)
        {
            if (lx < 0 || lx >= sub || ly < 0 || ly >= sub) return fallback;
            // Across a non-connected boundary (sheer ground<->roof) OR into a
            // cell too narrow for this width, treat the neighbour as this cell's
            // own cost so it adds nothing to the gradient — the heading follows
            // only cells the body can actually occupy.
            int ni = NavGrid.Index(cellOrigin.x + lx, cellOrigin.y + ly);
            byte nType = CellType[ni];
            if (!NavCell.Connected(fromType, nType) || Clearance[ni] < HalfW) return fallback;
            float c = FineCost[baseCost + ly * sub + lx];
            return c >= INF ? fallback : c;
        }

        // --- tiny binary min-heap (key,value) -------------------------------
        private static void HeapPush(NativeArray<float> hk, NativeArray<int> hv, ref int hn, float k, int v)
        {
            if (hn >= hk.Length) return;   // capacity guard (shouldn't hit for small tiles)
            int i = hn++; hk[i] = k; hv[i] = v;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (hk[p] <= hk[i]) break;
                float tk = hk[p]; hk[p] = hk[i]; hk[i] = tk;
                int tv = hv[p]; hv[p] = hv[i]; hv[i] = tv;
                i = p;
            }
        }
        private static void HeapPop(NativeArray<float> hk, NativeArray<int> hv, ref int hn, out float k, out int v)
        {
            k = hk[0]; v = hv[0];
            hn--;
            hk[0] = hk[hn]; hv[0] = hv[hn];
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, m = i;
                if (l < hn && hk[l] < hk[m]) m = l;
                if (r < hn && hk[r] < hk[m]) m = r;
                if (m == i) break;
                float tk = hk[m]; hk[m] = hk[i]; hk[i] = tk;
                int tv = hv[m]; hv[m] = hv[i]; hv[i] = tv;
                i = m;
            }
        }
    }
}
