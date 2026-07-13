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
    // Always-shown building fields (identity → footprint → survivability).
    // Referenced by ResourceNodeDefinitionEditor / WallDefinitionEditor so the
    // three inspectors can't drift. Attack fields are NOT here — they're gated
    // behind canAttack (see AttackFields + OnInspectorGUI). Shield is NOT here —
    // a building has no facing, so shield-arc mitigation is meaningless for it
    // (see MitigateFlat in CombatMath; building damage is armor-only).
    public static readonly string[] BuildingFields =
    {
        "displayName", "viewPrefab", "icon",
        "footprintX", "footprintZ", "maxHeightDelta",
        "maxHealth", "deathAnimSeconds", "armor", "receivesAbilities",
        "mass",
        // abilities (caster buildings) — harmless when unused
        "maxMana", "manaRegen", "abilities",
    };

    // Vision (2.5D line of sight) — every building blocks/participates in sight.
    // Placement price + construction time — CHARGED by blueprint placement but
    // previously shown in no editor (walls looked priceless). Shared with the
    // wall editor so they can't drift.
    public static readonly string[] CostFields =
    {
        "costGold", "costWood", "costFood",   // one-time blueprint price (pay-as-you-build)
        "buildTime",                          // Construction build-ticks to complete
        "selfBuildPower",                     // >0 = builds itself, no worker needed
    };

    // Vision (2.5D sight) fields — reused by node/obstacle editors so every
    // structure can set its occluder height.
    public static readonly string[] VisionFields =
    {
        "occluderHeight",   // how tall this building blocks sight
        "eyeOffset",        // shooter eye height (set ABOVE occluderHeight so a tower sees over its own walls)
    };

    // Attack fields — shown ONLY when canAttack is on (a defensive structure).
    private static readonly string[] AttackFields =
    {
        "attackInterval", "attackCooldown", "attackDamage", "meleeCleave",
        "meleeStrikeArc", "meleeRange", "isRanged", "projectile", "attackRange",
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

        // ---- vision (2.5D line of sight) ----
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Vision (2.5D line of sight)", EditorStyles.boldLabel);
        foreach (var name in VisionFields) Field(so, name);
        EditorGUILayout.HelpBox("occluderHeight = how tall this building blocks sight. eyeOffset = the " +
                                "shooter's eye height. For a working tower, set eyeOffset ABOVE occluderHeight " +
                                "so it sees and fires over its own walls (otherwise it's blind to everything).",
                                MessageType.None);

        // ---- construction: what placing this blueprint costs and how long it takes ----
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Construction (blueprint price / build time)", EditorStyles.boldLabel);
        foreach (var name in BuildingDefinitionEditor.CostFields) Field(so, name);

        // ---- combat: opt-in. Most buildings never attack. ----
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Combat", EditorStyles.boldLabel);

        Field(so, "canAttack");
        if (Flag(so, "canAttack"))
        {
            using (new EditorGUI.IndentLevelScope())
                foreach (var name in AttackFields) Field(so, name);
        }
        else
        {
            EditorGUILayout.HelpBox("This building does not actively attack. Enable 'Can Attack' to make it a " +
                                    "defensive structure (tower / gate-gun).", MessageType.None);
        }

        // Passive contact damage (spikes / palisade) — independent of canAttack.
        Field(so, "contactDamage");
        EditorGUILayout.HelpBox("contactDamage > 0 makes this a spike/palisade: enemy units touching it take " +
                                "that damage per second. The building itself never takes contact or ram damage " +
                                "— only real attacks (strikes, projectiles) hurt it.", MessageType.None);

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
        Field(so, "isResearcher");
        Field(so, "isRelay");

        bool intake  = depot && Flag(so, "isIntake");
        bool colony  = depot && Flag(so, "isColony");
        bool producer = Flag(so, "isProducer");
        bool researcher = Flag(so, "isResearcher");
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
            Field(so, "rallyPrefab");
        }

        // ---- research: gated by isResearcher (parallels isProducer) ----
        if (researcher)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Research", EditorStyles.miniBoldLabel);
            Field(so, "researches");
        }

        // ---- building upgrades: capability-by-list (always available) ----
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Upgrades", EditorStyles.miniBoldLabel);
        Field(so, "buildingUpgrades");

        so.ApplyModifiedProperties();
    }
}
