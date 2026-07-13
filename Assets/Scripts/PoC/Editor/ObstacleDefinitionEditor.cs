using UnityEditor;

// Lean inspector for a terrain ObstacleDefinition (rock, tree, boulder). An
// obstacle inherits BuildingDefinition for spawn/roster plumbing, but a rock
// has no combat, economy, abilities, mana, or upgrades — so this editor shows
// ONLY what an obstacle actually needs and hides all of that baggage. This is
// the whole point of ObstacleDefinition: authoring a rock shouldn't confront
// you with a barracks' worth of fields.
[CustomEditor(typeof(ObstacleDefinition))]
public class ObstacleDefinitionEditor : Editor
{
    // Only the fields a dumb obstacle cares about.
    private static readonly string[] Fields =
    {
        "displayName", "viewPrefab",
        "footprintX", "footprintZ", "maxHeightDelta",
        "mass",
    };

    public override void OnInspectorGUI()
    {
        var so = serializedObject;
        so.Update();

        void Field(string n) { var p = so.FindProperty(n); if (p != null) EditorGUILayout.PropertyField(p, true); }

        EditorGUILayout.LabelField("Obstacle", EditorStyles.boldLabel);
        foreach (var name in Fields) Field(name);

        // Invulnerability (NonCombatant) + destructible health only when NOT.
        EditorGUILayout.Space();
        var invuln = so.FindProperty("invulnerable");
        if (invuln != null) EditorGUILayout.PropertyField(invuln, true);
        if (invuln != null && !invuln.boolValue)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                Field("maxHealth");
                Field("armor");
            }
            EditorGUILayout.HelpBox("Destructible obstacle: it can be attacked and destroyed. " +
                                    "Give it a sensible maxHealth.", MessageType.None);
        }
        else
        {
            EditorGUILayout.HelpBox("Invulnerable: tagged NonCombatant at spawn — never targeted, never " +
                                    "damaged. (This is the invulnerability mechanism; no health needed.)",
                                    MessageType.None);
        }

        // Vision — a rock/tree blocks sight up to occluderHeight (2.5D LoS).
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Vision (2.5D line of sight)", EditorStyles.boldLabel);
        Field("occluderHeight");
        EditorGUILayout.HelpBox("How tall this obstacle blocks line of sight. A boulder blocks; low " +
                                "scrub might not (set 0 to block movement but not sight).", MessageType.None);

        // Deterministic view variants.
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("View variants", EditorStyles.boldLabel);
        Field("viewPrefabVariants");
        EditorGUILayout.HelpBox("Optional pool of meshes. Each spawned obstacle deterministically picks " +
                                "one from its StableId — the field looks varied (six rock shapes, several " +
                                "trees) while every client renders the identical mesh per entity. Empty = " +
                                "use the single viewPrefab above.", MessageType.Info);

        so.ApplyModifiedProperties();
    }
}
