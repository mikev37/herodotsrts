using UnityEditor;
using UnityEngine;

// ===========================================================================
// Inspector for RosterDefinition. The list is maintained AUTOMATICALLY by
// RosterAutoMaintainer (an AssetPostprocessor) on any definition create/delete —
// you normally never touch anything here.
//
// This inspector does NOT auto-sync on open (that contributed to an editor hang
// via SetDirty -> repaint loops). Use "Resync Now" if you ever need a manual
// pass (e.g. after bulk external file changes while the postprocessor was off).
// ===========================================================================
[CustomEditor(typeof(RosterDefinition))]
public class RosterDefinitionEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var roster = (RosterDefinition)target;

        EditorGUILayout.HelpBox(
            "Auto-maintained. Creating/deleting a unit/building/node/wall definition updates this " +
            "list automatically (append-only; index = network def id). You never edit it by hand.\n\n" +
            "Keep exactly one roster, named \"Roster\", in a Resources folder.",
            MessageType.Info);

        EditorGUILayout.Space();
        if (GUILayout.Button("Resync Now", GUILayout.Height(24)))
        {
            if (RosterAutoMaintainer.Sync(roster))
            {
                EditorUtility.SetDirty(roster);
                AssetDatabase.SaveAssetIfDirty(roster);   // explicit user action -> safe to save
            }
            else Debug.Log("[Roster] already in sync.");
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField($"Entries ({roster.entries.Count})", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(true))
        {
            for (int i = 0; i < roster.entries.Count; i++)
                EditorGUILayout.ObjectField($"id {i}", roster.entries[i], typeof(UnitDefinition), false);
        }
    }
}
