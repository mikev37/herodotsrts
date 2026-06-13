using Unity.Entities;
using Unity.Mathematics;

// ===========================================================================
// Building-related tags and footprint math.
//
// Three ORTHOGONAL tags — deliberately not one "building" catch-all, so each
// system gates on the property it actually cares about:
//
//   BuildingTag   — identity. Perception/targeting use it to weigh buildings
//                   differently and to measure range to the footprint surface.
//   Immobile      — movement. Behavior, slope, and steering skip the entity;
//                   contact combat skips knockback. Position and rotation are
//                   whatever spawn set, forever (until the tag is removed —
//                   which, like all structural changes, must happen inside the
//                   sim, e.g. from CommandApplySystem; that is the future
//                   unit<->building transform hook).
//   AbilityImmune — ability fields never stamp modifiers onto this entity.
//                   Data-driven from UnitDefinition.receivesAbilities.
//
// All three are added at spawn (UnitManager.SpawnUnit), which only ever runs
// from Start or from CommandApplySystem at a command's execution tick — both
// deterministic points, so the structural changes are lockstep-safe.
// ===========================================================================

public struct BuildingTag : IComponentData { }
public struct Immobile : IComponentData { }
public struct AbilityImmune : IComponentData { }

// Footprint geometry shared by UnitManager (snap at spawn), CommandApplySystem
// (placement validation), and ObstacleGridSystem (rasterization). One source of
// truth so the stamped cells, the snapped position, and the validated cells can
// never disagree. Burst-callable (pure static math).
public static class BuildingFootprint
{
    // Lowest-corner cell of an extents-sized footprint centered nearest to
    // `center`. Odd extents land the center on a cell center, even extents on a
    // cell corner — both fall out of the same rounding.
    public static int2 MinCell(float2 center, int2 extents)
    {
        float2 local = (center - NavGrid.Origin) / NavGrid.CellSize;
        return new int2(
            (int)math.round(local.x - extents.x * 0.5f),
            (int)math.round(local.y - extents.y * 0.5f));
    }

    // World-space center of the snapped footprint.
    public static float2 SnappedCenter(int2 minCell, int2 extents)
        => NavGrid.Origin +
           (new float2(minCell.x, minCell.y) + new float2(extents.x, extents.y) * 0.5f)
           * NavGrid.CellSize;

    // True for the four corner cells that the rounded rectangle cuts off.
    // Footprints thinner than 3 cells on either axis keep their corners (a 2x2
    // with cut corners would have no cells at all).
    public static bool CornerCut(int lx, int ly, int2 extents)
        => extents.x >= 3 && extents.y >= 3 &&
           (lx == 0 || lx == extents.x - 1) &&
           (ly == 0 || ly == extents.y - 1);

    // THE placement rule, shared by the PlaceBuilding command and spawn
    // abilities so they can never disagree. Per non-corner footprint cell: in
    // grid bounds, currently passable (covers obstacles AND slope-blocked
    // cells), and terrain height spread <= maxHeightDelta. On success,
    // spawnPos is the snapped center at the HIGHEST sampled cell height (the
    // model's basement skirt covers the lower side). Returns a verdict so
    // callers can report WHY a placement was rejected — silent rejection made
    // failures undiagnosable.
    public static PlacementVerdict ValidatePlacement(
        float2 desiredCenter, int2 extents, float maxHeightDelta,
        in Unity.Collections.NativeArray<byte> passable,
        bool hasTerrain, in TerrainHeightField terrain,
        out float3 spawnPos)
    {
        spawnPos = default;
        int2 min = MinCell(desiredCenter, extents);

        float minH = float.MaxValue, maxH = float.MinValue;
        for (int ly = 0; ly < extents.y; ly++)
        for (int lx = 0; lx < extents.x; lx++)
        {
            if (CornerCut(lx, ly, extents)) continue;
            int x = min.x + lx, y = min.y + ly;
            if (!NavGrid.InBounds(x, y)) return PlacementVerdict.OffGrid;
            if (passable[NavGrid.Index(x, y)] == 0) return PlacementVerdict.Blocked;
            float h = hasTerrain ? NavTerrain.SampleHeight(terrain, NavGrid.CellCenter(x, y)) : 0f;
            minH = math.min(minH, h);
            maxH = math.max(maxH, h);
        }
        if (maxH - minH > maxHeightDelta) return PlacementVerdict.TooSteep;

        float2 snapped = SnappedCenter(min, extents);
        spawnPos = new float3(snapped.x, maxH, snapped.y);
        return PlacementVerdict.Ok;
    }
}

public enum PlacementVerdict : byte
{
    Ok = 0,
    OffGrid = 1,    // a footprint cell is outside the nav grid
    Blocked = 2,    // a footprint cell is impassable (obstacle or slope-blocked)
    TooSteep = 3,   // terrain height spread across the footprint exceeds maxHeightDelta
}
