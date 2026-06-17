using Unity.Mathematics;

// ===========================================================================
// FORMATION GEOMETRY — the single source of slot shape, shared by:
//   * CommandSystem (ORDER TIME): generates the slot ordering to assign units
//     to slots by position, once, when an order is issued.
//   * BehaviorSystem (PER TICK): places a unit at its assigned slot via rank.
// Both MUST use identical offsets or the assignment won't match the placement,
// so the math lives here and nowhere else.
//
// Wall and cardinal are NOT separate mechanisms — both are a GRID, differing
// only in width (wall = wide/few rows, cardinal = squarish). Width is bounded
// (MaxWidth) so a large group wraps into ranks instead of an infinite line.
// Wedge is the one genuinely different shape.
//
// Frame: x = lateral (along `right`), y = depth (along `fwd`, +y = toward goal,
// so row 0 is the FRONT rank). Row-major fill: rank -> (col = rank % cols,
// row = rank / cols).
// ===========================================================================
public enum FormationShape : byte { Grid, Wall, Wedge }

public static class FormationGeometry
{
    // Max columns before a formation wraps to another rank. Bounds width; rows
    // (height) follow from count. (Global for now; promote to tuning later for
    // per-unit wide/tall preference.)
    public const int MaxWidth = 8;

    public static bool HasFormation(uint E) =>
        (E & ((uint)BehaviorFlag.FormWedge | (uint)BehaviorFlag.FormWall
            | (uint)BehaviorFlag.AlignCardinal
            | (uint)BehaviorFlag.StandFrontline | (uint)BehaviorFlag.StandBehindFriend)) != 0;

    // One shape per unit, by priority. Frontline/behind currently map to Grid
    // (their old "ahead/behind a friend" meaning is now just a grid position).
    public static FormationShape FromFlags(uint E)
    {
        if ((E & (uint)BehaviorFlag.FormWedge) != 0) return FormationShape.Wedge;
        if ((E & (uint)BehaviorFlag.FormWall)  != 0) return FormationShape.Wall;
        return FormationShape.Grid;   // AlignCardinal, frontline, behind
    }

    // Columns for a shape at a given count — the width bound.
    //   Wall : as wide as allowed (wraps past MaxWidth)
    //   Grid : squarish, also capped at MaxWidth
    public static int Cols(FormationShape shape, int count)
    {
        if (count <= 1) return 1;
        if (shape == FormationShape.Wall) return math.min(count, MaxWidth);
        return math.min((int)math.ceil(math.sqrt(count)), MaxWidth);   // Grid
    }

    // The slot offset for a rank, in the (fwd, right) frame. Identical inputs on
    // every unit and at order time, so assignment and placement agree exactly.
    public static float2 Offset(FormationShape shape, int rank, int count, int cols,
                                float2 fwd, float2 right, float spacing)
    {
        if (count <= 1) return float2.zero;

        if (shape == FormationShape.Wedge)
        {
            int side  = (rank & 1) == 1 ? 1 : -1;   // odd -> right, even -> left
            int depth = (rank + 1) / 2;             // 0,1,1,2,2,…
            return right * (side * depth * spacing) - fwd * (depth * spacing);
        }

        int rows = (count + cols - 1) / cols;
        int col  = rank % cols;
        int row  = rank / cols;
        float x = (col - (cols - 1) * 0.5f) * spacing;
        float y = ((rows - 1) * 0.5f - row) * spacing;   // row 0 = front rank
        return right * x + fwd * y;
    }
}
