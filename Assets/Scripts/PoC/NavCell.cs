using Unity.Collections;
using Unity.Mathematics;

// ===========================================================================
// NAV-CELL TYPES — the multi-surface layer (walkable wall-tops, ramps).
//
// DESIGN. The proven flood/coarse/fine machinery in Navigation.cs reads a
// single byte array where 0 = blocked, nonzero = open. We keep that contract
// untouched by giving it the UNION graph: ObstacleField.Passable is nonzero
// wherever ANY unit could stand (Ground, Roof, or Transition), and 0 only for
// Impassable. So the flow field floods ground->transition->roof as one
// connected space automatically — a goal on a wall-top produces a field that
// descends ramps to the ground, and a goal on the ground produces a field that
// climbs over a wall when (and only when) that is genuinely the cheaper route.
// This is the "unified field serves both contexts" result: one field per goal,
// no per-context flood.
//
// The per-context distinction — "can MY unit stand on THIS cell" — only matters
// in the two places a unit physically reads a cell:
//   * steering obstacle repulsion (a wrong-type cell is a wall to me; this is
//     also what keeps a roof unit ON the wall: the ground/impassable cells at
//     the edge repel it, so it never walks off — requirement "units don't fall")
//   * LineOfSight (can I walk straight there, or must I route via the field)
// Both read the parallel CellType array below through Connected().
//
// A unit carries a NavContext (Ground or Roof). Transition cells bridge the two
// and are walkable from either context; a unit's context flips when it steps
// off a Transition onto a pure Ground/Roof cell (see SteeringSystem). Because
// there is ONE cell per (x,z), this model cannot represent walkable ground
// UNDER a walkable roof (an archway/void) — an accepted scope limit. Walls are
// Roof on top with no ground passage beneath, which is exactly the intent.
//
// Y comes from SlopeSystem, NOT from nav: Ground context samples terrain, Roof
// samples NavHeight, Transition interpolates. Nav height (terrain slope baked
// into Passable) is a navigation concern only and is unrelated to this.
// ===========================================================================
public static class NavCell
{
    // Stored per cell in ObstacleField.CellType. Passable (the union view) is
    // derived: Passable[c] = (CellType[c] == Impassable) ? 0 : 1.
    public const byte Impassable = 0;
    public const byte Ground     = 1;
    public const byte Roof       = 2;
    public const byte Transition = 3;   // ramp/stairs/wall-entry: walkable from either context

    // A unit's current surface context (sim state on the unit).
    public const byte ContextGround = NavCell.Ground;
    public const byte ContextRoof   = NavCell.Roof;

    // Union-view passability for the connectivity machinery: open to SOMEONE.
    public static byte ToPassable(byte type) => (byte)(type == Impassable ? 0 : 1);

    // THE per-context rule. "May a unit currently in `context` occupy a cell of
    // `cellType`?" Transition is yes from either context; the opposite pure type
    // is the sheer face (never). Centralized so LoS and steering agree exactly.
    public static bool CanStand(byte context, byte cellType)
    {
        if (cellType == Impassable) return false;
        if (cellType == Transition) return true;
        return cellType == context;     // Ground unit on Ground, Roof unit on Roof
    }

    // Mutual traversability between two adjacent cells, independent of context.
    // (Reserved for any future per-type component labelling; the current union
    // flood does not need it, but LoS path-validity is expressed via CanStand.)
    public static bool Connected(byte a, byte b)
    {
        if (a == Impassable || b == Impassable) return false;
        if (a == Transition || b == Transition) return true;
        return a == b;                  // Ground<->Ground, Roof<->Roof; Ground<->Roof never
    }

    // Sight/projectiles: a Roof cell is a solid wall to a ground-level ray, a
    // Transition (ramp) is not, Ground is open. Used by the context-aware LoS
    // and available to projectile blocking.
    public static bool BlocksGroundSight(byte type) => type == Impassable || type == Roof;

}
