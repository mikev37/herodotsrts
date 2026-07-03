using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// ===========================================================================
// Custom inspector for BuildingDefinition. Shows the always-relevant building
// fields, then reveals each ECONOMY section ONLY when its role flag is set —
// so a plain wall shows no economy clutter, and a Barracks shows exactly the
// production fields and nothing else.
//
// Role → section mapping:
//   isDepot                → (depots accept all types; no extra fields)
//   isIntake  (needs depot)→ capital: streams to the player bank (no extra fields)
//   isColony  (needs depot)→ hauler unit + threshold
//   isProducer             → produces[] list
//   isRelay                → relayRate + relayRange
//   researches[] non-empty → research is capability-by-list (no bool needed —
//                            same pattern producers SHOULD use; see note below)
//   buildingUpgrades[]     → shown under an "Upgrades" foldout, always available
//
// WHY produces has isProducer but research has no isResearcher: consistency was
// off. The clean rule is "a capability is present iff its list is non-empty."
// Research already follows it. Production is kept behind isProducer only because
// ProducerTag gates the runtime query cheaply; the editor now treats them
// alike — the produces list is shown whenever isProducer is on, and toggling
// isProducer reveals it. Research shows its list whenever the building has one
// or you expand the section to add one.
// ===========================================================================
[CustomEditor(typeof(BuildingDefinition), editorForChildClasses: false)]
public class BuildingDefinitionEditor : Editor
{
    // Always-shown building fields (identity → survivability → attack → abilities).
    // Referenced by ResourceNodeDefinitionEditor / WallDefinitionEditor so the
    // three inspectors can't drift.
    public static readonly string[] BuildingFields =
    {
        "displayName", "viewPrefab", "icon",
        "footprintX", "footprintZ", "maxHeightDelta",
        "maxHealth", "deathAnimSeconds", "armor", "shield", "receivesAbilities",
        "mass",
        // attack (towers): all inert at 0/false
        "attackInterval", "attackCooldown", "attackDamage", "meleeCleave",
        "meleeStrikeArc", "meleeRange", "isRanged", "projectile", "attackRange",
        // abilities (caster buildings)
        "maxMana", "manaRegen", "abilities",
    };

    private static void Field(SerializedObject so, string name)
    {
        var p = so.FindProperty(name);
        if (p != null) EditorGUILayout.PropertyField(p, true);
    }

    private static bool Flag(SerializedObject so, string name)
    {
        var p = so.FindProperty(name);
        return p != null && p.boolValue;
    }

    public override void OnInspectorGUI()
    {
        var so = serializedObject;
        so.Update();

        // ---- always-shown building fields ----
        foreach (var name in BuildingFields) Field(so, name);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Economy", EditorStyles.boldLabel);

        // ---- role flags (these drive what shows below) ----
        Field(so, "isDepot");
        bool depot = Flag(so, "isDepot");

        // Intake / colony only make sense on a depot.
        using (new EditorGUI.IndentLevelScope())
        {
            if (depot)
            {
                Field(so, "isIntake");
                Field(so, "isColony");
            }
        }
        Field(so, "isProducer");
        Field(so, "isRelay");

        bool intake  = depot && Flag(so, "isIntake");
        bool colony  = depot && Flag(so, "isColony");
        bool producer = Flag(so, "isProducer");
        bool relay   = Flag(so, "isRelay");

        // ---- construction cost & time (every building is built) ----
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Construction", EditorStyles.miniBoldLabel);
        Field(so, "buildTime");
        Field(so, "costGold"); Field(so, "costWood"); Field(so, "costFood");
        Field(so, "selfBuildPower");
        Field(so, "sacrifice");

        // ---- capital (intake): note only, no extra fields ----
        if (intake)
            EditorGUILayout.HelpBox("Capital: holdings stream to the player bank each tick.", MessageType.None);

        // ---- colony: hauler + threshold ----
        if (colony)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Colony", EditorStyles.miniBoldLabel);
            Field(so, "haulerUnit");
            Field(so, "haulThreshold");
            EditorGUILayout.HelpBox("Colony: a depot that does NOT feed the bank. It builds haulerUnit " +
                                    "carts to carry holdings to the nearest capital. Leave haulerUnit empty " +
                                    "to instead drain it through a relay-tower chain.", MessageType.None);
        }

        // ---- relay: rate + range ----
        if (relay)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Relay", EditorStyles.miniBoldLabel);
            Field(so, "relayRate");
            Field(so, "relayRange");
        }

        // ---- producer: produces[] ----
        if (producer)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Production", EditorStyles.miniBoldLabel);
            Field(so, "produces");
        }

        // ---- research: capability-by-list (always available to author) ----
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Research", EditorStyles.miniBoldLabel);
        Field(so, "researches");

        // ---- building upgrades: capability-by-list (always available) ----
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Upgrades", EditorStyles.miniBoldLabel);
        Field(so, "buildingUpgrades");

        so.ApplyModifiedProperties();
    }
}
