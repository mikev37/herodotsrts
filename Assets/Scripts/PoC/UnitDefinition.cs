using UnityEngine;

// ===========================================================================
// THE SINGLE SOURCE OF TRUTH for a unit. Stats, the view/projectile prefabs,
// per-unit tuning, and behavior toggles all live here. At spawn UnitFactory.Create
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
    [Tooltip("Sight/shoot eye height above this unit's own surface. A tower with a tall eyeOffset " +
             "(set it above the building's own occluderHeight) sees and fires OVER lower walls; a ground " +
             "soldier uses a small offset. This is what lets a raised shooter clear a nearby parapet.")]
    public float eyeOffset = 1.5f;

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

    [Header("Economy — production cost")]
    [Tooltip("Resources drawn from the player bank, pay-as-you-build, when this unit is queued.")]
    public int prodCostGold = 0, prodCostWood = 0, prodCostFood = 0;
    [Tooltip("Ticks to complete production at a building (multiplied by tick rate).")]
    public float productionTime = 5f;
    [Tooltip("Food consumed while this unit is alive (population cap cost).")]
    public int foodCost = 0;

    [Header("Economy — builder")]
    [Tooltip("Build power contributed per tick to adjacent scaffolds. >0 = this unit is a builder.")]
    public float buildPower = 0f;
    [Tooltip("Buildings this unit (as a builder) can place. Drives the in-game build menu keys.")]
    public System.Collections.Generic.List<BuildingDefinition> builds = new();

    [Header("Economy — harvester")]
    [Tooltip("Resources pulled per tick from a node. >0 = this unit can harvest.")]
    public int harvestRate = 0;
    [Tooltip("Cargo capacity. Triggers return-to-depot when full.")]
    public int carryCapacity = 0;

    [Header("Economy — hauler")]
    [Tooltip("A hauler carries a colony's holdings to the nearest capital then is destroyed. Free to sustain (foodCost should be 0).")]
    public bool isHauler = false;

    [Header("UI")]
    [Tooltip("Icon shown in build/produce menus, progress bar, and queue strip.")]
    public Sprite icon;

    [Tooltip("One-shot VFX spawned (unparented, at the unit's transform) when this unit is the RESULT " +
             "of a morph/upgrade — so a Keep→Castle, a siege tank deploying, and a Knight→Paladin can each " +
             "have their own effect. Null = no effect. Should self-destruct.")]
    public GameObject morphEffectPrefab;

    [Header("Morph (free toggle — e.g. siege/unsiege, building settles into unit)")]
    [Tooltip("The OTHER form this unit toggles to (siege/unsiege, etc.). Null = cannot morph. G key triggers it.")]
    public UnitDefinition morphTarget;
    [Tooltip("Transition duration in ticks.")]
    public int morphTicks = 30;

    [Header("Upgrade (one-way, paid — e.g. Knight → Paladin via a TechDefinition)")]
    [Tooltip("Forms this unit can upgrade INTO. Cost + time come from the target's own costGold/Wood/Food + buildTime.")]
    public System.Collections.Generic.List<UnitDefinition> upgrades = new();


    [Header("Behavior ranges")]
    [Tooltip("Break formation and attack range")]
    public float attackNearbyRange = 8;
    [Tooltip("AvoidMelee back-off radius.")]
    public float avoidMeleeRange = 6f;
    [Tooltip("RetreatLowHealth triggers below this health fraction.")]
    [Range(0f, 1f)] public float retreatHealthFraction = 0.25f;
    [Tooltip("Engage the enemy range")]
    public float pursueDistance = 40;
    [Tooltip("GroupCohesion pulls toward the friendly center of mass when the unit is beyond this distance from it.")]
    public float cohesionRadius = 20f;
    [Header("Formation")]
    public int frontPriority = 0;     // higher = front rank
    [Range(0f, 1f)]
    public float looseness = 0f;    // 0 = rigid grid, 1 = loose smattering
    [Range(0f, 2f)]
    public float aggression = 1f;    // reserved
    [Tooltip("member.Separation` should be ≤ slot pitch, or the push fights")]
    public float formationSpacing = 3;    // personal-space radius for the soft push

}
