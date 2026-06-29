using System;
using Unity.Entities;

// ===========================================================================
// Unmanaged data the entity carries in place of managed references.
// ===========================================================================

// Stable handle from an entity back to its UnitDefinition (= index in the
// UnitManager roster). Replaces the old hand-authored viewTypeId; the manager
// uses it to find the view prefab, so the visual link can't drift.
public struct UnitDefId : IComponentData { public int Value; }

// Per-unit tuning values copied from the UnitDefinition at spawn, so the Burst
// systems can read them without touching the managed asset. (Anything truly
// battlefield-global stays as a constant in its system.)
public struct UnitTuning : IComponentData
{
    public float TurnSpeed;
    public float SeparationStrength;
    public float MeleeRange;
    public float CombatSpacing;       // formation spacing with enemies near
    public float IdleSpacing;         // formation spacing at rest (looser)
    public float AttackNearbyRange;   // aggression radius for AttackNearby
    public float AvoidMeleeRange;     // back-off radius for AvoidMelee
    public float RetreatHealthPct;    // RetreatLowHealth triggers below Current/Max
    public float ReEngageHealthPct;   // RetreatLowHealth stops above Current/Max
    public float PursueDistance;      // AdvanceIndividual/AdvanceOnEnemy contribute only within this range
	public float CohesionRadius;      // GroupCohesion activates beyond this distance from friendly center
    public float RetreatTime;         // Time to retreat from enemies when hit and below X health
}

// Unified attack: ONE countdown->act->cooldown timer for both melee and ranged.
// The shared cadence (Range/Interval/Cooldown/Damage) drives both; the "act"
// differs by the unit's Ranged.IsRanged flag:
//   * melee  -> sets Pulse = Damage for one frame (rides the hash into the
//               defender's contact loop), gated by the ArcDot cleave cone.
//   * ranged -> spawns a projectile using the Proj* fields (copied from the
//               unit's ProjectileDefinition at spawn).
// Attack cycle: predictable charge-up -> fire -> cooldown -> charge-up...
// The cycle only runs while the unit is COMMITTED to attacking
// (CombatStatus.IsAttacking, decided by BehaviorSystem); breaking off resets to
// Ready, so every engagement starts from a known state — no arriving mid-cycle.
public enum AttackPhase : byte { Ready = 0, Charging = 1, Cooldown = 2 }

public struct Attack : IComponentData
{
    public bool isRange;
    // cadence (shared)
    public float Range;       // engage distance (meleeRange for melee, attackRange for ranged)
    public float ChargeUp;    // wind-up seconds before a strike/shot lands
    public float Cooldown;    // recovery seconds after firing
    public float Timer;       // counts down within the current phase
    public AttackPhase Phase;
    public float Damage;      // melee strike damage OR projectile damage

    // melee act
    public float ArcDot;      // cos(arc/2): cleave strikes land within this cone
    public bool  Cleave;      // strike hits all enemies in the arc, not just the target
    public float Pulse;       // = Damage on the strike tick, else 0 (read by the hash)

    // ranged act (copied from the referenced ProjectileDefinition)
    public int   ProjectileId;       // index into the projectile view registry
    public float ProjSpeed;
    public float ProjRise;
    public float ProjLaunchHeight;
    public float ProjHitRadius;
    public float ProjCollisionHeight;
}

// Defender-side mitigation. Armor is flat reduction always; Shield is extra
// reduction that only counts when the attacker is in the front half-arc.
public struct Defense : IComponentData
{
    public float Armor;
    public float Shield;
}

// On a projectile entity: which entry in the projectile view registry to draw.
public struct ProjectileView : IComponentData { public int Id; }
