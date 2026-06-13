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
        var wh = serializedObject.FindProperty("wallHeight");
        if (wh != null) EditorGUILayout.PropertyField(wh, true);
        serializedObject.ApplyModifiedProperties();
    }
}
