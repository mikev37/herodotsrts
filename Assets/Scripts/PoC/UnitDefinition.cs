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
    [Tooltip("Distance at which an enemy is 'in our face' -> hold and fight.")]
    public float meleeRange = 1.2f;
    [Tooltip("Preferred shoulder-to-shoulder gap when forming a wall.")]
    public float wallSpacing = 1.3f;

    [Header("Idle")]
    [Tooltip("How far apart to spread when no enemy is near.")]
    public float spreadRadius = 3f;
    [Tooltip("Step size taken each frame while dispersing.")]
    public float spreadStrength = 2f;

    [Header("Survivability")]
    public float maxHealth = 100f;
    public float deathAnimSeconds = 2.0f;
    [Tooltip("Flat damage subtracted from each incoming melee strike (min 1 always gets through).")]
    public float armor = 0f;
    [Tooltip("Extra armor that only applies when the attacker is in this unit's front half-arc.")]
    public float shield = 0f;

    [Header("Melee")]
    [Tooltip("Seconds between melee strikes; the attack timer loops while engaged.")]
    public float meleeAttackInterval = 1.0f;
    [Tooltip("Wind-up seconds before a melee strike lands. The attack cycle is " +
             "charge-up -> strike -> cooldown(=interval) -> charge-up, and only runs while " +
             "the unit is committed to attacking (standing on its target).")]
    public float meleeChargeUpSeconds = 0.3f;
    [Tooltip("Cleave: the strike hits EVERY enemy inside the strike arc. Off = the strike " +
             "lands only on the unit's declared target (first body in line can still block).")]
    public bool meleeCleave = false;
    [Tooltip("Damage of one melee strike — a discrete bash applied on the strike frame.")]
    public float meleeAttackDamage = 25f;
    [Tooltip("Full angle (degrees) of the strike cone in front of the attacker. " +
             "A strike only lands on defenders inside this arc. 360 = cleave (hits all around).")]
    [Range(0f, 360f)] public float meleeStrikeArc = 120f;

    [Header("Ranged")]
    public bool isRanged = false;
    [Tooltip("Projectile this unit fires (defines speed, arc, view). Required if isRanged.")]
    public ProjectileDefinition projectile;
    [Tooltip("Standoff distance the unit kites to keep from the nearest enemy.")]
    public float kiteRadius = 7f;
    public float attackRange = 10f;
    public float attackInterval = 1.2f;
    [Tooltip("Wind-up seconds before each shot. Same predictable cycle as melee.")]
    public float rangedChargeUpSeconds = 0.5f;
    public float attackDamage = 18f;

    [Header("Hero")]
    [Tooltip("If set, this unit is spawned as a hero (gets HeroTag). It's a normal unit " +
             "in every other way — selected, ordered, hit, and killed like one.")]
    public bool isHero = false;

    [Header("Abilities (Q/W/E/R slots; null = empty slot)")]
    [Tooltip("Abilities this unit can cast. When a selection is ordered to cast, the " +
             "selected unit with the MOST abilities is the caster.")]
    public AbilityDefinition[] abilities = new AbilityDefinition[4];

    [Header("Behaviors (compose freely)")]
    [Tooltip("Slide sideways to line up with nearby friendly wall-formers → a wall.")]
    public bool formShieldWall = false;
    [Tooltip("Tuck in just behind the nearest friendly wall-former.")]
    public bool stayBehindWall = false;
    [Tooltip("Keep a preferred distance from the nearest enemy (kite).")]
    public bool kiteEnemies = false;
    [Tooltip("Advance onto the best target chosen by the targeting system.")]
    public bool advanceToTarget = true;
    [Tooltip("Hold ground while inside a friendly Defensive hero aura.")]
    public bool holdWhenDefensive = false;
    [Tooltip("When no enemy is near, drift apart to spread out; re-form when enemies return.")]
    public bool idleSpread = false;
}
