using UnityEditor;
using UnityEngine;

// Inspector for WallDefinition: the building fields plus the wall height. Shares
// BuildingDefinitionEditor.BuildingFields so the two can't drift apart.
[CustomEditor(typeof(WallDefinition))]
public class WallDefinitionEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        foreach (var name in BuildingDefinitionEditor.BuildingFields)
        {
            var prop = serializedObject.FindProperty(name);
            if (prop != null) EditorGUILayout.PropertyField(prop, true);
        }
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Construction (blueprint price / build time)", EditorStyles.boldLabel);
        foreach (var name in BuildingDefinitionEditor.CostFields)
        {
            var prop = serializedObject.FindProperty(name);
            if (prop != null) EditorGUILayout.PropertyField(prop, true);
        }
        EditorGUILayout.Space();
        foreach (var name in new[] { "wallHeight", "rampCells", "rampSide" })
        {
            var prop = serializedObject.FindProperty(name);
            if (prop != null) EditorGUILayout.PropertyField(prop, true);
        }
        serializedObject.ApplyModifiedProperties();
    }
}
