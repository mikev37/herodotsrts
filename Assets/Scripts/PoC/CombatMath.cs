using Unity.Mathematics;

// ===========================================================================
// Shared damage resolution so melee strikes and projectile hits mitigate the
// same way. Armor is flat reduction (floored so 1 always lands); Shield adds to
// armor but only when the threat is in the defender's front half-arc; Backstab
// multiplies when the threat is in the rear half.
// ===========================================================================
public static class CombatMath
{
    // Damage multiplier when a hit lands on a defender's rear arc.
    public const float Backstab = 2f;

    // Distance from a point to the EDGE of an axis-aligned building footprint
    // (0 if the point is inside the box). This is the one true way to measure
    // range to a building — buildings are rectangles, not circles, so a melee
    // unit at the middle of a long wall is at the edge (~0), not "inscribed-
    // radius" units away. `halfExtents` is the box half-size in world units
    // (UnitInfo.HalfExtents). Extend here when footprints become non-rectangular.
    public static float DistanceToFootprint(float2 point, float2 center, float2 halfExtents)
    {
        float2 d = math.abs(point - center) - halfExtents;
        float outside = math.length(math.max(d, 0f));       // distance when outside the box
        float inside  = math.min(math.max(d.x, d.y), 0f);   // negative when inside
        return math.max(0f, outside + inside);
    }

    // raw            : incoming damage before mitigation
    // defenderForward: the victim's facing (XZ, normalized)
    // toThreat       : direction from the victim toward the source of the hit (XZ)
    public static float Mitigate(float raw, float2 defenderForward, float2 toThreat,
                                 float armor, float shield)
    {
        bool facing = math.dot(defenderForward, math.normalizesafe(toThreat)) >= 0f;
        float effectiveArmor = armor + (facing ? shield : 0f);
        float mult = facing ? 1f : Backstab;
        return math.max(raw * mult - effectiveArmor, 1f);
    }

    // Flat mitigation for defenders with NO meaningful facing — buildings. They
    // don't rotate, so shield-arc and backstab (both direction-based) make no
    // sense: a building takes the same damage from every side. Armor only.
    public static float MitigateFlat(float raw, float armor)
        => math.max(raw - armor, 1f);
}
