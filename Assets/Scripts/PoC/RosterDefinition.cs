using System.Collections.Generic;
using UnityEngine;

// ===========================================================================
// ROSTER DEFINITION — the authoritative def-id registry, as a single project
// asset. INDEX = network def id; the order MUST be byte-for-byte identical on
// every peer (it's authored data, versioned with the project) and stable across
// saves (a def id baked into a snapshot must resolve to the same definition
// forever).
//
// AUTHORING: the `entries` list is NOT hand-maintained. The editor tool
// (RosterDefinitionEditor "Rebuild From Project") scans every UnitDefinition /
// BuildingDefinition / ResourceNodeDefinition / WallDefinition asset in the
// project and fills the list in a DETERMINISTIC, APPEND-ONLY order:
//   - existing entries keep their current index (never reordered — that would
//     invalidate every existing save/replay),
//   - new assets are appended, sorted by asset GUID (stable across machines),
//   - a removed asset leaves a tombstone (null) so following ids don't shift;
//     the tool warns about tombstones so you can decide whether a save-breaking
//     compaction is safe.
// This gives "add a unit, it just appears" without the desync risk of scanning
// at runtime (AssetDatabase order is platform-dependent and unavailable in a
// build). The frozen, versioned asset is the whole point.
//
// ONE global roster: the def id is global; which units a given player can make
// is gated by the build menus (BuildingDefinition.produces / UnitDefinition.builds),
// not by separate per-faction lists. Everything that spawns through
// UnitFactory.Create — units, buildings, resource nodes, obstacles — lives here.
// ===========================================================================
[CreateAssetMenu(menuName = "MarbleCombat/Roster")]
public class RosterDefinition : ScriptableObject
{
    [Tooltip("Auto-maintained by the 'Rebuild From Project' button (do not hand-edit order). " +
             "Index = network def id. Append-only; never reorder existing entries.")]
    public List<UnitDefinition> entries = new();

    private Dictionary<UnitDefinition, int> _idOf;
    private List<ProjectileDefinition> _projectileDefs;
    private Dictionary<ProjectileDefinition, int> _projIndex;

    public int Count => entries.Count;

    public void EnsureBuilt()
    {
        if (_idOf != null) return;
        _idOf = new Dictionary<UnitDefinition, int>(entries.Count);
        _projectileDefs = new List<ProjectileDefinition>();
        _projIndex = new Dictionary<ProjectileDefinition, int>();
        for (int i = 0; i < entries.Count; i++)
        {
            var d = entries[i];
            if (d == null) continue;   // tombstone slot (a removed asset) — id preserved, skipped
            if (!_idOf.TryAdd(d, i))
                Debug.LogError($"[Roster] duplicate '{d.displayName}' at index {i} (already id {_idOf[d]}).");
            if (d.isRanged && d.projectile != null) ResolveProjectileId(d.projectile);
        }
    }

    // Forces a rebuild of the runtime lookup (call after the editor tool mutates
    // `entries`, so play-mode-in-editor sees the new ids without a domain reload).
    public void Invalidate() { _idOf = null; _projectileDefs = null; _projIndex = null; }

    public UnitDefinition GetDefinition(int id) => (id >= 0 && id < entries.Count) ? entries[id] : null;

    public int GetId(UnitDefinition def)
    {
        EnsureBuilt();
        return def != null && _idOf.TryGetValue(def, out var i) ? i : -1;
    }

    public int ResolveProjectileId(ProjectileDefinition pd)
    {
        EnsureBuilt();
        if (pd == null) return -1;
        if (_projIndex.TryGetValue(pd, out var idx)) return idx;
        idx = _projectileDefs.Count;
        _projectileDefs.Add(pd);
        _projIndex[pd] = idx;
        return idx;
    }

    public GameObject GetProjectileViewPrefab(int projectileId)
        => (projectileId >= 0 && _projectileDefs != null && projectileId < _projectileDefs.Count)
            ? _projectileDefs[projectileId].viewPrefab : null;

    // --- runtime auto-resolve -------------------------------------------------
    // So the factory/view-manager never need the asset hand-wired: they call
    // this to find the one roster in the project. Cached after first load.
    private static RosterDefinition _cached;
    public static RosterDefinition Get()
    {
        if (_cached != null) return _cached;
        // Resources folder is the only runtime-available load path in a build.
        _cached = Resources.Load<RosterDefinition>("Roster");
        if (_cached == null)
        {
            var all = Resources.LoadAll<RosterDefinition>("");
            if (all != null && all.Length > 0)
            {
                _cached = all[0];
                if (all.Length > 1)
                    Debug.LogWarning($"[Roster] {all.Length} RosterDefinition assets found in Resources; " +
                                     $"using '{_cached.name}'. There should be exactly one.");
            }
        }
#if UNITY_EDITOR
        // Editor convenience: if it isn't in a Resources folder yet, still find it
        // anywhere in the project so play-in-editor works. A build REQUIRES it in
        // Resources (AssetDatabase doesn't exist at runtime) — warn so it's caught
        // before shipping.
        if (_cached == null)
        {
            var guids = UnityEditor.AssetDatabase.FindAssets("t:RosterDefinition");
            if (guids.Length > 0)
            {
                _cached = UnityEditor.AssetDatabase.LoadAssetAtPath<RosterDefinition>(
                    UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]));
                Debug.LogWarning("[Roster] Found the roster outside a Resources folder. " +
                                 "Move it into one (e.g. Assets/Resources/Roster.asset) — a built player " +
                                 "cannot load it otherwise.");
            }
        }
#endif
        if (_cached == null)
            Debug.LogError("[Roster] No RosterDefinition found. Create one via " +
                           "Create → MarbleCombat → Roster and place it in a Resources folder. " +
                           "It then auto-populates with every definition in the project.");
        return _cached;
    }
}
