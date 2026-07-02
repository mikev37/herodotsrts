using System.Collections.Generic;
using UnityEngine;

// ===========================================================================
// ROSTER DEFINITION — the authoritative def-id registry, as an ASSET instead of
// a field on a MonoBehaviour. INDEX = network def id; the order must be byte-for-
// byte identical on every peer (it's authored data, versioned with the project).
// Built into a def->id dictionary once, with validation that rejects nulls and
// duplicates (the failure mode that silently desynced the old List<SpawnEntry>).
//
// With teams gone there is ONE global roster: the def id is global, and which
// units a given player can actually make is gated by the build menus
// (BuildingDefinition.produces / UnitDefinition.builds), not by separate lists.
// ===========================================================================
[CreateAssetMenu(menuName = "MarbleCombat/Roster")]
public class RosterDefinition : ScriptableObject
{
    [Tooltip("Ordered master list of every unit/building/hero/projectile-carrier definition. " +
             "Index = network def id. Append-only across released builds; never reorder.")]
    public List<UnitDefinition> entries = new();

    private Dictionary<UnitDefinition, int> _idOf;

    // Projectile-view registry: a SEPARATE id space from the unit def id. Built by
    // walking `entries` in roster order (deterministic on every peer, since the
    // roster itself is authored/append-only) and deduping by ProjectileDefinition
    // reference — several unit types sharing one arrow/bolt asset get one id.
    // This mirrors the old UnitManager._projectileDefs/_projIndex; only the
    // home moved. Attack.ProjectileId is an index into THIS list, never `entries`.
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
            if (d == null) { Debug.LogError($"[Roster] null entry at index {i} — fix the asset."); continue; }
            if (!_idOf.TryAdd(d, i)) Debug.LogError($"[Roster] duplicate '{d.displayName}' at index {i} (already id {_idOf[d]}).");
            if (d.isRanged && d.projectile != null) ResolveProjectileId(d.projectile);   // deterministic pre-registration
        }
    }

    public UnitDefinition GetDefinition(int id) => (id >= 0 && id < entries.Count) ? entries[id] : null;

    public int GetId(UnitDefinition def)
    {
        EnsureBuilt();
        return def != null && _idOf.TryGetValue(def, out var i) ? i : -1;
    }

    // Dedup index for a projectile definition (builds the registry entry on first
    // sight — safe to call again later for an ability-driven projectile not on any
    // unit's def, since the append order for THOSE is itself deterministic: they
    // are resolved in ability-registration order, which is roster-order too).
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

    // Resolve a projectile VIEW prefab by its registry id (Attack.ProjectileId /
    // ProjectileView.Id space) — NOT a unit def id. Consumed by ProjectileViewManager.
    public GameObject GetProjectileViewPrefab(int projectileId)
        => (projectileId >= 0 && _projectileDefs != null && projectileId < _projectileDefs.Count)
            ? _projectileDefs[projectileId].viewPrefab : null;
}
