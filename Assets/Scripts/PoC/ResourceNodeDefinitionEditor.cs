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

        // Vision — a node blocks sight too (a tree occludes). This is why an
        // occluder value now appears on nodes.
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Vision (2.5D line of sight)", EditorStyles.boldLabel);
        foreach (var name in BuildingDefinitionEditor.VisionFields)
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

        EditorGUILayout.HelpBox(
            "DEPLETION ANIMATION: put a NodeView component on this node's viewPrefab. The view " +
            "manager feeds it the remaining fraction each frame (NodeView.SetFill 1→0), so you can " +
            "swap husk meshes, shrink the tree, or blend a 'stump' as it empties. When it hits 0 the " +
            "node either lingers as a permanent husk or (if 'despawnWhenDepleted') plays the Deliver/" +
            "vanish state for huskLingerSeconds, then despawns.", MessageType.Info);

        serializedObject.ApplyModifiedProperties();
    }
}
