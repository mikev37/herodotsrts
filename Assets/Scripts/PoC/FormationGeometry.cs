using Unity.Collections;
using Unity.Mathematics;

// ===========================================================================
// FORMATION GEOMETRY
//
// Slot encoding is ROW-MAJOR: row 0 = front rank; col 0 = leftmost in row.
// Row widths are shape-dependent (see BuildRowWidths).
//
//   Grid / Wall   — all rows `cols` wide (last row may be shorter)
//   Wedge         — row 0 = 1 unit (tip), row r = r+1 units (widens back)
//
// Two consumers must agree on the encoding:
//   FormationSystem (ASSIGNMENT): calls BuildRowWidths to split units into
//     rows, sorts each row by lateral, writes the resulting 1D slot index.
//   BehaviorSystem  (PLACEMENT):  calls Offset(slotIndex, …) to find its
//     world point each tick — which decodes back to the same (row, col).
//
// FRAME: `fwd` = forward (toward goal), `right` = rightward (fwd rotated 90°
// clockwise). Depth along fwd, lateral along right.
// Col 0 = leftmost = most-negative right projection.
// ===========================================================================
public enum FormationShape : byte { Grid, Wall, Wedge }

public static class FormationGeometry
{
    public const int MaxWidth = 8;

    // Automatic column count when no explicit width is given.
    public static int Cols(FormationShape shape, int count)
    {
        if (count <= 1) return 1;
        if (shape == FormationShape.Wall)  return math.min(count, MaxWidth);
        if (shape == FormationShape.Wedge) return 1;   // Wedge ignores cols; row widths are 1,2,3,…
        return math.min((int)math.ceil(math.sqrt(count)), MaxWidth);
    }

    // Per-row slot widths, front-to-back (row index 0 = front/tip).
    // Used by FormationSystem to bucket units into rows before lateral-sorting.
    public static void BuildRowWidths(FormationShape shape, int count, int cols, NativeList<int> rowWidths)
    {
        rowWidths.Clear();
        cols = math.max(1, cols);
        int remaining = count;
        switch (shape)
        {
            case FormationShape.Wedge:
                // Tip = 1 unit, every successive rank is 1 wider.
                for (int r = 0; remaining > 0; r++)
                {
                    int w = math.min(r + 1, remaining);
                    rowWidths.Add(w);
                    remaining -= w;
                }
                break;
            default:   // Grid / Wall
                while (remaining > 0)
                {
                    int w = math.min(cols, remaining);
                    rowWidths.Add(w);
                    remaining -= w;
                }
                break;
        }
    }

    // World-space offset of slot `idx` from the formation anchor.
    // Row and column are decoded from idx the same way they were assigned
    // (BuildRowWidths + depth/lateral bucket sort in FormationSystem), so
    // BehaviorSystem always resolves the correct position.
    public static float2 Offset(FormationShape shape, int idx, int count, int cols,
                                float2 fwd, float2 right, float spacing)
    {
        if (count <= 1) return float2.zero;
        cols = math.max(1, cols);

        int row, col, rowWidth, numRows;

        if (shape == FormationShape.Wedge)
        {
            // Row r starts at cumulative index r*(r+1)/2 and has r+1 slots.
            // Given idx, find row: floor((-1+sqrt(1+8*idx))/2).
            row      = (int)((-1f + math.sqrt(1f + 8f * idx)) / 2f);
            col      = idx - row * (row + 1) / 2;
            rowWidth = row + 1;
            // Total row count for this group size (smallest numRows s.t. numRows*(numRows+1)/2 >= count).
            numRows  = (int)math.ceil((-1f + math.sqrt(1f + 8f * count)) / 2f);
        }
        else   // Grid / Wall
        {
            row      = idx / cols;
            col      = idx % cols;
            int fullRows = count / cols;
            int tail     = count % cols;
            numRows  = fullRows + (tail > 0 ? 1 : 0);
            rowWidth = (row < fullRows) ? cols : tail;
        }

        // Depth: centred so the anchor is mid-depth of the formation.
        // Row 0 (front) is at +(numRows-1)*spacing/2, last row at mirror.
        float depth   = ((numRows - 1) * 0.5f - row) * spacing;
        // Lateral: col 0 = leftmost (most-negative right). Centred in row.
        float lateral = (col - (rowWidth - 1) * 0.5f) * spacing;

        return fwd * depth + right * lateral;
    }

    // Stable per-unit scatter off the ideal slot. Seeded by StableId so it
    // is consistent across ticks. looseness 0 = exactly on slot,
    // looseness 1 = up to ±1.5 spacings (enough to dissolve the grid entirely).
    public static float2 Scatter(int stableId, float looseness, float spacing)
    {
        if (looseness <= 0f) return float2.zero;
        uint h = (uint)stableId * 2654435761u;
        float ax = (h & 0xFFFF)        / 65535f * 2f - 1f;
        float ay = ((h >> 16) & 0xFFFF) / 65535f * 2f - 1f;
        return new float2(ax, ay) * (looseness * spacing * 1.5f);
    }
}
