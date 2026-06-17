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
                                   int maxCells)
    {
        int2 c0 = NavGrid.Cell(a), c1 = NavGrid.Cell(b);
        int dx = math.abs(c1.x - c0.x), dy = math.abs(c1.y - c0.y);
        if (dx + dy > maxCells) return false;

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
        if (!NavGrid.InBounds(x, y) || cellType[NavGrid.Index(x, y)] == NavCell.Impassable) return false;
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
                    NavCell.Connected(prevType, cellType[NavGrid.Index(hx, y)]);
                bool shoulderY = NavGrid.InBounds(x, hy) &&
                    NavCell.Connected(prevType, cellType[NavGrid.Index(x, hy)]);
                if (!shoulderX || !shoulderY) return false;
            }
            if (stepX) { err -= dy; x += sx; }
            if (stepY) { err += dx; y += sy; }
            if (!NavGrid.InBounds(x, y)) return false;
            byte t = cellType[NavGrid.Index(x, y)];
            if (!NavCell.Connected(prevType, t)) return false;   // ray breaks at a non-traversable edge
            prevType = t;
        }
        return true;
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
    public const int MaxComp       = 4;   // global: max tracked connected components per big tile

    public static float2 Origin => new float2(-WorldSize * 0.5f, -WorldSize * 0.5f);

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
                Passable = _passable, CellType = _cellType, NavHeight = _navHeight,
                CellComp = _cellComp, CompCount = _compCount,
                BigVersion = _bigVersion, Version = 0, CoarseVersion = 0,
            });
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_passable.IsCreated)    _passable.Dispose();
        if (_cellType.IsCreated)    _cellType.Dispose();
        if (_navHeight.IsCreated)   _navHeight.Dispose();
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
        // Reset: every cell is plain Ground with no nav-height override.
        for (int i = 0; i < cellType.Length; i++) { cellType[i] = NavCell.Ground; navHeight[i] = 0f; }

        // Dead buildings stop blocking immediately (the corpse lingers for the
        // death anim, but pathing opens up the tick health hits zero).
        foreach (var (xform, obs) in SystemAPI.Query<RefRO<LocalTransform>, RefRO<Obstacle>>().WithNone<Dead>())
        {
            float2 p = new float2(xform.ValueRO.Position.x, xform.ValueRO.Position.z);
            int2 e = obs.ValueRO.Extents;

            if (e.x > 0 && e.y > 0)
            {
                int2 min = BuildingFootprint.MinCell(p, e);
                for (int ly = 0; ly < e.y; ly++)
                for (int lx = 0; lx < e.x; lx++)
                {
                    if (BuildingFootprint.CornerCut(lx, ly, e)) continue;
                    int x = min.x + lx, y = min.y + ly;
                    if (!NavGrid.InBounds(x, y)) continue;
                    cellType[NavGrid.Index(x, y)] = NavCell.Impassable;
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
                sum = sum * 31 + passable[NavGrid.Index(origin.x + lx, origin.y + ly)];
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

        var goals = new NativeList<int2>(Allocator.Temp);
        foreach (var dest in SystemAPI.Query<RefRO<DesiredDestination>>())
        {
            if (!dest.ValueRO.Has || !dest.ValueRO.UseFlowField) continue;
            int2 gc = NavGrid.Cell(dest.ValueRO.Value);
            if (!NavGrid.InBounds(gc.x, gc.y)) continue;
            bool seen = false;
            for (int i = 0; i < goals.Length; i++) if (math.all(goals[i] == gc)) { seen = true; break; }
            if (!seen && goals.Length < NavGrid.MaxPaths) goals.Add(gc);
        }

        for (int g = 0; g < goals.Length; g++)
        {
            int2 gc = goals[g];
            int slot = FindSlot(slots, gc);
            if (slot < 0) slot = AllocSlot(slots, tick);
            var sl = slots[slot];
            bool fresh = sl.Valid != 0 && math.all(sl.GoalCell == gc);
            sl.GoalCell = gc; sl.GoalBig = NavGrid.BigOf(gc); sl.UsedTick = tick; sl.Valid = 1;

            if (!fresh || sl.BuiltCoarseVersion != obs.CoarseVersion)
            {
                BuildCoarse(coarse, slot, gc, obs.CellComp, obs.CompCount);
                sl.BuiltCoarseVersion = obs.CoarseVersion;
                int baseK = slot * NavGrid.BigCount;
                for (int b = 0; b < NavGrid.BigCount; b++)
                {
                    int blk = blockOf[baseK + b];
                    if (blk >= 0) { blockKey[blk] = -1; blockOf[baseK + b] = -1; }
                }
            }
            slots[slot] = sl;
            map.TryAdd(NavGrid.Index(gc), slot);
        }

        var seenNodes = new NativeHashSet<int>(256, Allocator.Temp);
        var seenTiles = new NativeHashSet<int>(256, Allocator.Temp);
        var keys  = new NativeList<int>(Allocator.Temp);
        foreach (var (xform, dest) in SystemAPI.Query<RefRO<LocalTransform>, RefRO<DesiredDestination>>())
        {
            if (!dest.ValueRO.Has || !dest.ValueRO.UseFlowField) continue;
            int gi = NavGrid.Index(NavGrid.Cell(dest.ValueRO.Value));
            if (!map.TryGetValue(gi, out int slot)) continue;
            int2 cell = NavGrid.Cell(new float2(xform.ValueRO.Position.x, xform.ValueRO.Position.z));
            if (!NavGrid.InBounds(cell.x, cell.y)) continue;
            MarkCorridor(seenNodes, seenTiles, keys, coarse, obs.CellComp, obs.CompCount, slot, cell);
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

            new EikonalFineBuildJob
            {
                Requests   = ra,
                Coarse     = coarse,
                Slots      = slots,
                Passable   = obs.Passable,
                CellType   = obs.CellType,
                CellComp   = obs.CellComp,
                BlockOf    = blockOf,
                FineDir    = nf.ValueRW.FineDir,
                FineCost   = nf.ValueRW.FineCost,
                FineBigVer = fineBigVer,
                BigVersion = obs.BigVersion,
            }.Run();   // sequential build; Run() executes the Burst job on this thread
                       // (synchronous like the old Complete(), and Temp-safe)
        }

        goals.Dispose(); seenNodes.Dispose(); seenTiles.Dispose(); keys.Dispose(); requests.Dispose();
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

    private static int FindSlot(NativeArray<PathSlot> slots, int2 gc)
    {
        for (int s = 0; s < NavGrid.MaxPaths; s++)
            if (slots[s].Valid != 0 && math.all(slots[s].GoalCell == gc)) return s;
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
        [ReadOnly] public NativeArray<byte>      CellComp;
        [ReadOnly] public NativeArray<int>       BigVersion;
        [ReadOnly] public NativeArray<int>       BlockOf;    // slot*BigCount+big -> block (neighbour lookup)

        public NativeArray<float2> FineDir;
        public NativeArray<float>  FineCost;
        public NativeArray<int>    FineBigVer;

        // Slowness per cell. Set so a cardinal step ~ 10 (diagonal ~14.1 falls out
        // of the solver), matching the coarse octile scale + SeedScale.
        private const float EikF = 10f;
        private const float INF  = 1e15f;

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
            if (math.all(sl.GoalBig == bcoord))
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
                    if (CellType[NavGrid.Index(cell)] == NavCell.Impassable) continue;

                    int2 ncell = new int2(e == 0 ? cell.x - 1 : e == 1 ? cell.x + 1 : cell.x,
                                          e == 2 ? cell.y - 1 : e == 3 ? cell.y + 1 : cell.y);
                    if (!NavGrid.InBounds(ncell.x, ncell.y)) continue;
                    // Type-aware border edge: only connect components across the
                    // tile boundary when the two cells are actually traversable
                    // between (same surface or Transition-bridged), never across
                    // a sheer ground<->roof step.
                    if (!NavCell.Connected(CellType[NavGrid.Index(cell)], CellType[NavGrid.Index(ncell)])) continue;

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
                byte ct = CellType[NavGrid.Index(cellOrigin.x + lx, cellOrigin.y + ly)];
                if (ct == NavCell.Impassable) continue;

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
                // Type-aware: cost only flows between Connected cells, so the
                // field never propagates across a sheer ground<->roof step.
                if (!NavCell.Connected(CellType[NavGrid.Index(cellOrigin.x + lx, cellOrigin.y + ly)],
                                       CellType[NavGrid.Index(ncell)])) continue;

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
                if (!NavCell.Connected(CellType[NavGrid.Index(cellOrigin.x + lx, cellOrigin.y + ly)],
                                       CellType[NavGrid.Index(ncell)])) continue;
                best = math.min(best, FineCost[baseCost + nly * sub + nlx]);
            }
            return best;
        }

        private float SubCostOr(int baseCost, int lx, int ly, int sub, int2 cellOrigin, byte fromType, float fallback)
        {
            if (lx < 0 || lx >= sub || ly < 0 || ly >= sub) return fallback;
            // Across a non-connected boundary (sheer ground<->roof), treat the
            // neighbour as if it were this cell's own cost, so it adds nothing to
            // the gradient — the direction follows only reachable cells.
            byte nType = CellType[NavGrid.Index(cellOrigin.x + lx, cellOrigin.y + ly)];
            if (!NavCell.Connected(fromType, nType)) return fallback;
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
