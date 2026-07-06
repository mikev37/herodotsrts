using UnityEngine;

// ===========================================================================
// A building is a unit definition plus a footprint. Subclassing keeps every
// shared system (spawn, registry, view pooling, abilities) working unchanged;
// the spawn path branches on `def is BuildingDefinition` to add BuildingTag,
// Immobile, Obstacle and to snap the position to the nav grid.
//
// INSPECTOR: BuildingDefinitionEditor shows only what applies to an Immobile
// entity. Locomotion, formation, behavior-range fields (inherited from
// UnitDefinition) are hidden — those systems never run on buildings. The custom
// editor groups economy fields clearly and leaves attack/ranged/abilities
// visible because towers and caster-buildings are valid authoring targets.
//
// Reset() sets the hidden inherited fields to inert defaults so a fresh
// asset is sane even before the inspector is opened.
// ===========================================================================
[CreateAssetMenu(menuName = "MarbleCombat/Building Definition")]
public class BuildingDefinition : UnitDefinition
{
    // -------------------------------------------------------------------------
    // Footprint
    // -------------------------------------------------------------------------
    [Header("Footprint (nav-grid cells)")]
    [Tooltip("Footprint width in nav-grid cells (X axis). " +
             "One cell is cut from each corner when both extents are >= 3.")]
    public int footprintX = 4;
    [Tooltip("Footprint depth in nav-grid cells (Z axis).")]
    public int footprintZ = 4;

    [Header("Placement")]
    [Tooltip("Max terrain-height difference across the footprint cells; placement is rejected " +
             "above this. The model should carry a basement skirt to cover the allowed delta.")]
    public float maxHeightDelta = 1.0f;

    [Tooltip("Sight-blocking height above the ground for line-of-sight (2.5D vision). A tall keep " +
             "(large value) blocks sight; a low wall (small value) can be seen over by a raised shooter. " +
             "Independent of pathing — a building always blocks movement, but only blocks SIGHT up to this " +
             "height. 0 = blocks pathing but never sight (you see right over it).")]
    public float occluderHeight = 6f;

    // -------------------------------------------------------------------------
    // Combat — OFF by default. Most buildings do not fight; only a defensive
    // structure (tower, gate-gun, keep with arrow slits) opts in. When off, the
    // attack fields are hidden in the inspector and the attack is zeroed at spawn
    // so the building is never considered a threat and never fires.
    // -------------------------------------------------------------------------
    [Header("Combat")]
    [Tooltip("Off = this building never attacks (the default — a house, a farm, a barracks). " +
             "On = a defensive structure: reveals the attack fields (melee or ranged, damage, range, " +
             "projectile) and the building will engage enemies in range via TowerTargetingSystem.")]
    public bool canAttack = false;

    [Tooltip("Spikes / palisade: passive damage PER SECOND dealt to any enemy unit touching this " +
             "building. No target or order needed — press against it, take damage. Independent of " +
             "canAttack (a palisade bites without 'attacking'). 0 = no contact damage. The building " +
             "itself never TAKES contact/ram damage — only real attacks (strikes, projectiles) hurt it.")]
    public float contactDamage = 0f;

    // -------------------------------------------------------------------------
    // Economy — role (what this building does in the resource loop)
    // -------------------------------------------------------------------------
    [Header("Economy — role")]
    [Tooltip("Harvesters can deliver resources here. A depot accepts ALL resource types " +
             "(there is no per-depot type restriction — the harvester's cargo is deposited as-is).")]
    public bool isDepot = false;

    [Tooltip("CAPITAL: a depot whose holdings stream directly to the player bank each tick. " +
             "Set isDepot = true as well.")]
    public bool isIntake = false;

    [Tooltip("COLONY: a depot that does NOT stream to the player bank. It accumulates, then " +
             "auto-dispatches a hauler unit to the nearest capital. Set isDepot = true as well.")]
    public bool isColony = false;

    [Tooltip("Can queue units for production (Barracks, Stable, etc.).")]
    public bool isProducer = false;

    [Tooltip("Can research tech upgrades (e.g. Knight → Paladin). Parallels isProducer — " +
             "reveals the researches list. A building can be both a producer and a researcher.")]
    public bool isResearcher = false;

    [Tooltip("RELAY TOWER: passes resources along a chain of towers toward the nearest connected " +
             "capital. The colony's haulerUnit should be null when using relays.")]
    public bool isRelay = false;

    // -------------------------------------------------------------------------
    // Economy — construction
    // -------------------------------------------------------------------------
    [Header("Economy — construction cost & time")]
    [Tooltip("Total build-power ticks required to finish construction. " +
             "Each builder unit contributes its buildPower value per tick.")]
    public float buildTime = 50f;

    [Tooltip("Resources drawn proportionally from the player bank as construction progresses " +
             "(pay-as-you-build — progress is capped by the fraction paid).")]
    public int costGold = 0, costWood = 0, costFood = 0;

    [Tooltip("Self-build power added per tick with no worker present (Protoss-style). " +
             "0 = a builder unit is required. Stacks additively with any builders on site.")]
    public float selfBuildPower = 0f;

    [Tooltip("If true, the first builder unit that walks onto the scaffold is consumed — " +
             "its destruction instantly completes construction (sacrifice mechanic).")]
    public bool sacrifice = false;

    // -------------------------------------------------------------------------
    // Economy — colony / hauler
    // -------------------------------------------------------------------------
    [Header("Economy — colony")]
    [Tooltip("Unit auto-built and dispatched to the nearest capital when colony bank total " +
             "reaches haulThreshold. Leave null for relay-chain factions (no hauler needed).")]
    public UnitDefinition haulerUnit;

    [Tooltip("Total stored resources (across all types) that trigger a hauler dispatch.")]
    public int haulThreshold = 200;

    // -------------------------------------------------------------------------
    // Economy — relay
    // -------------------------------------------------------------------------
    [Header("Economy — relay")]
    [Tooltip("Resources per tick streamed along the relay chain toward the nearest capital " +
             "(only used when isRelay = true).")]
    public int relayRate = 20;

    [Tooltip("Max world-space distance to the next relay tower or capital in the relay chain.")]
    public float relayRange = 25f;

    // -------------------------------------------------------------------------
    // Economy — what this building can make / research / unlock
    // -------------------------------------------------------------------------
    [Header("Economy — production")]
    [Tooltip("Unit definitions this building can produce. Index matches the 1–4 keyboard slots. " +
             "Requires isProducer = true.")]
    public System.Collections.Generic.List<UnitDefinition> produces = new();

    [Header("Economy — research")]
    [Tooltip("Tech definitions this building can research (e.g. Knight → Paladin). " +
             "Each entry costs resources + time and auto-upgrades all existing units of fromUnit.")]
    public System.Collections.Generic.List<TechDefinition> researches = new();

    [Header("Economy — building upgrades")]
    [Tooltip("Building tiers this building can upgrade into (paid morph, e.g. Keep → Castle). " +
             "Cost and build time come from the TARGET building's own costGold/Wood/Food + buildTime.")]
    public System.Collections.Generic.List<BuildingDefinition> buildingUpgrades = new();

    // -------------------------------------------------------------------------
    // Reset — inert defaults for hidden UnitDefinition fields
    // -------------------------------------------------------------------------
    private void Reset()
    {
        // Inherited fields that make no sense for a stationary building:
        // leave them at zero / off so the system never accidentally processes them.
        displayName     = "Building";
        receivesAbilities = false;
        maxHealth       = 500f;
        mass            = 50f;
        speed           = 0f;
        turnSpeed       = 0f;
        radius          = 1f;     // overridden at spawn to the footprint inscribed radius
        attackDamage    = 0f;
        isRanged        = false;
        isHero          = false;
        buildPower      = 0f;
        harvestRate     = 0;
        carryCapacity   = 0;
        isHauler        = false;
        prodCostGold    = 0;
        prodCostWood    = 0;
        prodCostFood    = 0;
        productionTime  = 0f;
        foodCost        = 0;
    }
}
