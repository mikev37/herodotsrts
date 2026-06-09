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
}
