using UnityEditor;

// Custom inspector for ResourceNodeDefinition. Draws the shared building
// allowlist (which already includes "resourceType" via BuildingDefinitionEditor)
// plus the node-specific fields: amount, despawnWhenDepleted, huskLingerSeconds.
[CustomEditor(typeof(ResourceNodeDefinition))]
public class ResourceNodeDefinitionEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // Shared building fields (footprint, health, visuals, economy role, etc.)
        foreach (var name in BuildingDefinitionEditor.BuildingFields)
        {
            var prop = serializedObject.FindProperty(name);
            if (prop != null) EditorGUILayout.PropertyField(prop, true);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Resource Node", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("amount"), true);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("despawnWhenDepleted"), true);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("huskLingerSeconds"), true);

        serializedObject.ApplyModifiedProperties();
    }
}
