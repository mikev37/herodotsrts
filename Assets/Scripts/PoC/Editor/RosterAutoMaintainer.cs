using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// ===========================================================================
// ROSTER AUTO-MAINTAINER — keeps the single RosterDefinition asset in sync with
// the project automatically, through the asset pipeline. No manual button.
//
//   * Create a UnitDefinition (or Building/ResourceNode/Wall) -> appended with
//     the next network def id.
//   * Delete a definition asset -> its id becomes a null tombstone (retired,
//     never reused, so existing saves/replays stay valid).
//
// APPEND-ONLY + DETERMINISTIC: existing ids never move; new defs are appended in
// asset-GUID order (identical on every machine).
//
// RE-ENTRANCY / HANG SAFETY (this file previously hung the editor):
//   OnPostprocessAllAssets fires again after we save the roster. A simple bool
//   guard is not enough because import batches can nest and the inspector can
//   also request a sync. We therefore:
//     1. never save synchronously inside the postprocess callback — instead we
//        mutate the in-memory object, mark it dirty, and let Unity persist it on
//        its normal cycle (no SaveAssetIfDirty here => no re-trigger);
//     2. debounce via delayCall so multiple imports in one batch collapse into a
//        single sync on the next editor tick;
//     3. hard-guard with a reentrancy flag AND a "already scheduled" flag.
//   The result cannot recurse: the postprocess path does no AssetDatabase writes
//   that would re-invoke it.
// ===========================================================================
public class RosterAutoMaintainer : AssetPostprocessor
{
    private static bool _syncing;      // true while Sync mutates the asset
    private static bool _scheduled;    // a delayed sync is already queued

    private static void OnPostprocessAllAssets(
        string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        if (_syncing) return;

        bool relevant =
            imported.Any(IsDefinitionOrRoster) ||
            moved.Any(IsDefinitionOrRoster) ||
            deleted.Any(p => p.EndsWith(".asset"));   // deletes lose their type; check on sync
        if (!relevant) return;

        ScheduleSync();
    }

    // Debounced: collapse a burst of imports into one sync next tick.
    private static void ScheduleSync()
    {
        if (_scheduled) return;
        _scheduled = true;
        EditorApplication.delayCall += DelayedSync;
    }

    private static void DelayedSync()
    {
        EditorApplication.delayCall -= DelayedSync;
        _scheduled = false;

        var roster = FindRoster();
        if (roster == null) return;

        _syncing = true;
        try
        {
            if (Sync(roster))
            {
                EditorUtility.SetDirty(roster);
                // NOTE: intentionally NOT calling SaveAssetIfDirty here. Marking
                // dirty is enough; Unity writes it on its normal save cycle, and
                // avoiding an explicit save is what prevents re-triggering this
                // postprocessor (the cause of the earlier editor hang).
            }
        }
        finally { _syncing = false; }
    }

    private static bool IsDefinitionOrRoster(string path)
    {
        if (path == null || !path.EndsWith(".asset")) return false;
        var t = AssetDatabase.GetMainAssetTypeAtPath(path);
        return t != null && (typeof(UnitDefinition).IsAssignableFrom(t) ||
                             typeof(RosterDefinition).IsAssignableFrom(t));
    }

    private static RosterDefinition FindRoster()
    {
        var guids = AssetDatabase.FindAssets("t:RosterDefinition");
        if (guids.Length == 0) return null;
        if (guids.Length > 1)
            Debug.LogWarning($"[Roster] {guids.Length} RosterDefinition assets exist; maintaining the first. " +
                             "There should be exactly one (in a Resources folder).");
        return AssetDatabase.LoadAssetAtPath<RosterDefinition>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    // Idempotent: append new defs, tombstone removed ones, keep ids stable.
    // Returns true if the roster changed. Does NOT save (caller marks dirty).
    // Safe to call from the inspector too.
    internal static bool Sync(RosterDefinition roster)
    {
        if (roster == null) return false;

        var guids = AssetDatabase.FindAssets("t:UnitDefinition");
        var found = new List<(string guid, UnitDefinition def)>(guids.Length);
        foreach (var g in guids)
        {
            var def = AssetDatabase.LoadAssetAtPath<UnitDefinition>(AssetDatabase.GUIDToAssetPath(g));
            if (def != null) found.Add((g, def));
        }

        var foundSet = new HashSet<UnitDefinition>(found.Select(f => f.def));
        bool changed = false;

        for (int i = 0; i < roster.entries.Count; i++)
        {
            if (roster.entries[i] != null && !foundSet.Contains(roster.entries[i]))
            {
                Debug.Log($"[Roster] id {i} ('{roster.entries[i].name}') removed — retired as tombstone.");
                roster.entries[i] = null;
                changed = true;
            }
        }

        var existing = new HashSet<UnitDefinition>(roster.entries.Where(e => e != null));
        var toAdd = found.Where(f => !existing.Contains(f.def))
                         .OrderBy(f => f.guid, System.StringComparer.Ordinal)
                         .Select(f => f.def)
                         .ToList();
        if (toAdd.Count > 0)
        {
            int firstId = roster.entries.Count;
            roster.entries.AddRange(toAdd);
            changed = true;
            Debug.Log($"[Roster] appended {toAdd.Count} definition(s) at ids {firstId}..{roster.entries.Count - 1}: " +
                      string.Join(", ", toAdd.Select(d => d.name)));
        }

        if (changed) roster.Invalidate();
        return changed;
    }
}
