using UnityEditor;
using UnityEngine;

// ===========================================================================
// Custom inspector for BuildingDefinition (editorForChildClasses: false so
// Wall / ResourceNode each get their own editor that calls BuildingFields).
//
// WHAT IS SHOWN — every field that can meaningfully vary per building:
//   Identity, visuals, footprint, placement, survivability (buildings get hit),
//   mass (ramming physics), attack / ranged (towers are valid), mana / abilities
//   (caster buildings are valid), all economy fields.
//
// WHAT IS HIDDEN — fields that apply only to MOBILE units and that Reset()
//   sets to safe inert defaults:
//   speed, turnSpeed, radius (derived from footprint at spawn),
//   separationStrength, combatSpacing, idleSpacing,
//   attackNearbyRange, avoidMeleeRange, retreatHealthFraction,
//   pursueDistance, cohesionRadius, frontPriority, looseness, aggression,
//   formationSpacing, isHero, buildPower, builds, harvestRate, carryCapacity,
//   isHauler, prodCostGold/Wood/Food, productionTime, foodCost,
//   morphTarget / morphTicks / upgrades (unit upgrade path — irrelevant for
//   buildings; building tier upgrades live in buildingUpgrades instead).
//
// ResourceNodeDefinitionEditor and WallDefinitionEditor both reference
// BuildingFields so the three lists can't drift apart.
// ===========================================================================
[CustomEditor(typeof(BuildingDefinition), editorForChildClasses: false)]
public class BuildingDefinitionEditor : Editor
{
    /// <summary>
    /// Canonical field allowlist for all building-type inspectors.
    /// Referenced by ResourceNodeDefinitionEditor and WallDefinitionEditor.
    /// Covers every field a content author needs to set on a building asset.
    /// </summary>
    public static readonly string[] BuildingFields =
    {
        // ---- Identity & visuals ----
        "displayName",
        "viewPrefab",
        "icon",

        // ---- Footprint & placement ----
        "footprintX",
        "footprintZ",
        "maxHeightDelta",

        // ---- Survivability ----
        // Buildings take damage; all three matter (armor/shield for tough towers).
        "maxHealth",
        "deathAnimSeconds",
        "armor",
        "shield",
        "receivesAbilities",

        // ---- Physical ----
        // Mass used when a unit rams into the building footprint.
        "mass",

        // ---- Attack (towers, gate-guns, defensive structures) ----
        // A building with attackDamage > 0 and a melee or ranged attack is valid.
        // All of these are inert when left at 0/false by Reset().
        "attackInterval",
        "attackCooldown",
        "attackDamage",
        "meleeCleave",
        "meleeStrikeArc",
        "meleeRange",
        "isRanged",
        "projectile",
        "attackRange",

        // ---- Mana & abilities ----
        // A building can have abilities (e.g. a caster tower). If it has none,
        // all these default to 0/empty and are harmless.
        "maxMana",
        "manaRegen",
        "abilities",

        // ---- Economy — role ----
        "isDepot",
        "isIntake",
        "isColony",
        "isProducer",
        "isRelay",

        // ---- Economy — construction ----
        "buildTime",
        "costGold",
        "costWood",
        "costFood",
        "selfBuildPower",
        "sacrifice",

        // ---- Economy — resource type ----
        "resourceType",

        // ---- Economy — colony ----
        "haulerUnit",
        "haulThreshold",

        // ---- Economy — relay ----
        "relayRate",
        "relayRange",

        // ---- Economy — production / research / upgrade ----
        "produces",
        "researches",
        "buildingUpgrades",
    };

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        foreach (var name in BuildingFields)
        {
            var prop = serializedObject.FindProperty(name);
            if (prop != null)
                EditorGUILayout.PropertyField(prop, true);
        }
        serializedObject.ApplyModifiedProperties();
    }
}
