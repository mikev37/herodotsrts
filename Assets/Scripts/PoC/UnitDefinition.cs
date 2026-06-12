using UnityEngine;

// ===========================================================================
// THE SINGLE SOURCE OF TRUTH for a unit. Stats, the view/projectile prefabs,
// per-unit tuning, and behavior toggles all live here. At spawn the UnitManager
// copies these onto the entity (stats + a UnitTuning component + a BehaviorFlags
// bitmask). Add a unit = make one of these and drop it in the manager roster.
//
// NOTE on what's here vs. global: values that define a unit's *identity* are
// here (per-unit). A few battlefield-wide constants stay in the systems and are
// called out in code comments (hash cell size, targeting search radius, flow
// arrive radius, impact/knockback physics scale). Move any of those here too if
// you want them per-unit.
// ===========================================================================
[CreateAssetMenu(menuName = "MarbleCombat/Unit Definition")]
public class UnitDefinition : ScriptableObject
{
    [Header("Identity & visuals")]
    public string displayName = "Unit";
    [Tooltip("Mesh + Animator prefab (needs a UnitView; manager adds one if missing).")]
    public GameObject viewPrefab;

    [Header("Locomotion")]
    public float speed = 4f;
    public float radius = 0.5f;
    public float mass = 1f;
    [Tooltip("Max turn rate in radians/sec. Lower = heavier, more deliberate turning.")]
    public float turnSpeed = 6f;

    [Header("Spacing & formation")]
    [Tooltip("How hard this unit pushes off neighbors (body-blocking).")]
    public float separationStrength = 8f;
    [Tooltip("Formation spacing (wall/wedge/cardinal/behind) when enemies are near.")]
    public float combatSpacing = 1.3f;
    [Tooltip("Formation spacing when no enemies are near (looser at rest).")]
    public float idleSpacing = 2.2f;

    [Header("Idle")]

    [Header("Survivability")]
    public float maxHealth = 100f;
    public float deathAnimSeconds = 2.0f;
    [Tooltip("Flat damage subtracted from each incoming melee strike (min 1 always gets through).")]
    public float armor = 0f;
    [Tooltip("Extra armor that only applies when the attacker is in this unit's front half-arc.")]
    public float shield = 0f;
    [Tooltip("Untick to make this entity ignore ability fields entirely (no modifiers are ever stamped onto it). Off by default for buildings.")]
    public bool receivesAbilities = true;

    [Header("Attack")]
    public float attackInterval = 1f;
    public float attackCooldown = 0;
    public float attackDamage = 18f;
    [Header("Melee")]
    [Tooltip("Cleave: the strike hits EVERY enemy inside the strike arc. Off = the strike " +
             "lands only on the unit's declared target (first body in line can still block).")]
    public bool meleeCleave = false;
    [Tooltip("Full angle (degrees) of the strike cone in front of the attacker. " +
             "A strike only lands on defenders inside this arc. 360 = cleave (hits all around).")]
    [Range(0f, 360f)] public float meleeStrikeArc = 120f;
    [Tooltip("Distance at which an enemy is 'in our face' -> hold and fight.")]
    public float meleeRange = 1.2f;

    [Header("Ranged")]
    public bool isRanged = false;
    [Tooltip("Projectile this unit fires (defines speed, arc, view). Required if isRanged.")]
    public ProjectileDefinition projectile;
    public float attackRange = 10f;

    [Header("Hero")]
    [Tooltip("If set, this unit is spawned as a hero (gets HeroTag). It's a normal unit " +
             "in every other way — selected, ordered, hit, and killed like one.")]
    public bool isHero = false;

    [Header("Mana")]
    [Tooltip("Caster resource pool. Abilities with a mana cost fail to cast (nothing consumed) when below their cost.")]
    public float maxMana = 100f;
    [Tooltip("Mana regenerated per second.")]
    public float manaRegen = 2f;

    [Header("Abilities (Q/W/E/R slots; null = empty slot)")]
    [Tooltip("Abilities this unit can cast. When a selection is ordered to cast, the " +
             "selected unit with the MOST abilities is the caster.")]
    public AbilityDefinition[] abilities = new AbilityDefinition[4];

    [Header("Behaviors (compose freely)")]
    [Tooltip("Engage enemies that come within Attack Nearby Range.")]
    public bool attackNearby = true;
    [Tooltip("Try to position behind the chosen target's facing.")]
    public bool flankTarget = false;
    [Tooltip("Stand between the chosen enemy and the friendly center of mass.")]
    public bool bodyBlock = false;
    [Tooltip("Hold the line between the friendly and enemy centers of mass.")]
    public bool formWall = false;
    [Tooltip("Tuck behind the closest friendly, relative to the enemy center of mass.")]
    public bool standBehindFriend = false;
    [Tooltip("March toward the enemy center of mass.")]
    public bool advanceOnEnemy = false;
    [Tooltip("March toward the chosen target.")]
    public bool advanceIndividual = true;
    [Tooltip("Back off when an enemy is within Avoid Melee Range.")]
    public bool avoidMelee = false;
    [Tooltip("Flee the enemy center of mass when health drops below Retreat Health Fraction.")]
    public bool retreatLowHealth = false;
    [Tooltip("Slot in behind-and-beside the friendly ahead (wedge formations).")]
    public bool formWedge = false;
    [Tooltip("Snap to 90-degree slots around the closest friendly, in their facing frame.")]
    public bool alignCardinal = false;
    [Tooltip("Face the friendly facing consensus when not attacking.")]
    public bool alignFacing = false;
    [Tooltip("Move with the friendly movement consensus when nothing else applies.")]
    public bool alignMovement = false;
    [Tooltip("Push apart from crowded allies at all times.")]
    public bool separate = false;
    [Tooltip("Push apart from crowded allies only when no enemies are near.")]
    public bool separateIdle = false;
    [Tooltip("Push apart into a rough line")]
    public bool separateLateral = false;

    [Header("Behavior ranges")]
    [Tooltip("Break formation and attack range")]
    public float attackNearbyRange = 8;
    [Tooltip("AvoidMelee back-off radius.")]
    public float avoidMeleeRange = 6f;
    [Tooltip("RetreatLowHealth triggers below this health fraction.")]
    [Range(0f, 1f)] public float retreatHealthFraction = 0.25f;
    [Tooltip("Engage the enemy range")]
    public float pursueDistance = 40;
    
}
