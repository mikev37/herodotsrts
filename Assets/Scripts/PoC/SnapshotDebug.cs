using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// ===========================================================================
// SnapshotDebug — Phase 4 test harness. Add to any scene object; everything is
// keyboard-driven and testable offline (single editor, or one MPPM virtual
// player per role):
//
//   F6  : ROUND-TRIP SELF-TEST — capture the live world, restore it in place,
//         and compare the state hash before/after. Equal hashes prove the
//         serializer covers every hashed component bit-exactly; this is the
//         offline 4b verification, no network needed.
//   F10 : save the current sim to the save file.
//   F11 : restore from the save file. Single-player convenience — in a network
//         session use the host's Load Save button so every peer is rebuilt.
//   F8  : corrupt one unit's health by 1 LOCALLY. On a client this injects a
//         real desync: the next checksum report disagrees, the host logs the
//         divergent tick, and the resync pipeline heals everyone — the
//         end-to-end 4d test.
//
// MPPM note: virtual players share persistentDataPath, so F10's file is the
// same file the host's Load Save reads.
// ===========================================================================
public class SnapshotDebug : MonoBehaviour
{
    public KeyCode roundTripKey = KeyCode.F6;
    public KeyCode corruptKey   = KeyCode.F8;
    public KeyCode saveKey      = KeyCode.F10;
    public KeyCode loadKey      = KeyCode.F11;

    private void Update()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) return;

        if (Input.GetKeyDown(roundTripKey)) RoundTrip(world);
        if (Input.GetKeyDown(corruptKey))   CorruptOne(world);
        if (Input.GetKeyDown(saveKey))      SimSnapshot.SaveToFile(world);
        if (Input.GetKeyDown(loadKey))
        {
            var data = SimSnapshot.LoadFile();
            if (data == null) { Debug.LogWarning($"[Snapshot] no save file at {SimSnapshot.DefaultSavePath}"); return; }
            SimSnapshot.Restore(world, data, out uint tick, out uint hash);
            Debug.Log($"[Snapshot] loaded save at tick {tick} (hash {hash:X8}).");
        }
    }

    private static void RoundTrip(World world)
    {
        uint before = SimSnapshot.ComputeStateHash(world.EntityManager);
        var data = SimSnapshot.Capture(world);
        bool ok = SimSnapshot.Restore(world, data, out uint tick, out uint after);

        if (ok && after == before)
            Debug.Log($"[Snapshot] round-trip OK at tick {tick}: hash {before:X8} == {after:X8} " +
                      $"({data.Length} bytes).");
        else
            Debug.LogError($"[Snapshot] ROUND-TRIP FAILED at tick {tick}: before {before:X8}, " +
                           $"after {after:X8}, ok={ok} — the serializer is missing state.");
    }

    private static void CorruptOne(World world)
    {
        var em = world.EntityManager;
        using var q = em.CreateEntityQuery(ComponentType.ReadOnly<UnitTag>(), typeof(Health));
        var ents = q.ToEntityArray(Allocator.Temp);
        if (ents.Length == 0) { ents.Dispose(); Debug.LogWarning("[Snapshot] no units to corrupt."); return; }

        var e = ents[0];
        ents.Dispose();
        var hp = em.GetComponentData<Health>(e);
        hp.Current = math.max(1f, hp.Current - 1f);
        em.SetComponentData(e, hp);
        Debug.Log("[Snapshot] corrupted one unit's health locally — if this peer is a client in a " +
                  "network session, expect a host DESYNC report followed by a resync.");
    }
}
