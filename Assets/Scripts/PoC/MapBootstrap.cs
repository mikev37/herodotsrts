using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unity.Netcode;

// ===========================================================================
// MAP BOOTSTRAP — authoring utility to drop objects onto the terrain at Start:
// resource nodes, obstacle buildings, neutral doodads, or initial owned units.
//
// Nodes & obstacle buildings are just BuildingDefinitions, so placing them via
// the factory makes them integrate with pathfinding automatically: SpawnUnit
// stamps their Obstacle footprint, ObstacleGridSystem rasterizes it each tick,
// and FlowFieldSystem routes units around them — exactly like BuildingManager's
// runtime B/N placement, but authored.
//
// Neutral things (nodes, scenery) take ownerPlayer = -1; owned starting units/
// buildings take a real player id. On a networked CLIENT these come from the
// host snapshot instead, so placement is skipped there — but the HOST still runs
// it locally (its world IS the one that gets captured and distributed), and so
// does single-player. Distinguishing host from client (not just "networked at
// all") is the point: skipping for both would ship every networked game with an
// empty map.
// ===========================================================================
public class MapBootstrap : MonoBehaviour
{
    [Serializable]
    public class Placement
    {
        public UnitDefinition definition;     // ResourceNodeDefinition, obstacle BuildingDefinition, or a unit
        public Vector3        position;
        [Tooltip("-1 = neutral (nodes/scenery). Set a player id for owned starting buildings/units.")]
        public int            ownerPlayer = -1;
        [Tooltip("How many to place (grid-spread around position).")]
        public int            count = 1;
        public float          spacing = 3f;
    }

    [Tooltip("Objects to place once the world + factory are ready.")]
    public List<Placement> placements = new();

    [Tooltip("Skip on a networked CLIENT (it materializes from the host snapshot). " +
             "Host and single-player both still place normally.")]
    public bool skipOnNetworkClient = true;

    // Set once PlaceWhenReady finishes (or immediately if skipped) — LockstepNet
    // can poll this to avoid capturing a snapshot before the host's map exists.
    public bool PlacementsDone { get; private set; }

    private void Start()
    {
        if (skipOnNetworkClient && IsNetworkClient())
        {
            PlacementsDone = true;   // nothing to do; the client's world arrives via snapshot restore
            return;
        }
        StartCoroutine(PlaceWhenReady());
    }

    // True only for an actual connected client (not the host, not single-player,
    // not before a network session exists at all).
    private static bool IsNetworkClient()
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        return nm != null && nm.IsListening && nm.IsClient && !nm.IsServer;
    }

    private IEnumerator PlaceWhenReady()
    {
        while (UnitFactory.Instance == null || !UnitFactory.Instance.Ready) yield return null;
        var roster = UnitFactory.Instance.Roster;

        foreach (var p in placements)
        {
            if (p.definition == null) continue;
            int id = roster.GetId(p.definition);
            if (id < 0) { Debug.LogError($"[MapBootstrap] '{p.definition.displayName}' is not in the roster asset."); continue; }

            int n = Mathf.Max(1, p.count);
            int cols = Mathf.CeilToInt(Mathf.Sqrt(n));
            for (int i = 0; i < n; i++)
            {
                int cx = i % cols, cz = i / cols;
                var pos = p.position + new Vector3(cx * p.spacing, 0f, cz * p.spacing);
                UnitFactory.Instance.Create(p.definition, id, p.ownerPlayer, (float3)pos);
            }
        }
        PlacementsDone = true;
    }
}
