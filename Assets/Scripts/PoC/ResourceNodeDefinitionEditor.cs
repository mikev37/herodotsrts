using UnityEditor;

// Custom inspector for ResourceNodeDefinition. Draws the shared building
// allowlist, then the node-specific fields (resourceType lives on the node now,
// not on BuildingDefinition — a depot accepts ALL types, so type only means
// something for a node's yield).
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
        EditorGUILayout.PropertyField(serializedObject.FindProperty("resourceType"), true);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("amount"), true);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("despawnWhenDepleted"), true);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("huskLingerSeconds"), true);

        serializedObject.ApplyModifiedProperties();
    }
}
