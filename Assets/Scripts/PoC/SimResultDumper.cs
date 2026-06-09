using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// SimResultDumper — the helper for verifying a run.
//
//   * Logs the live per-tick checksum once a second (so you can spot the FIRST
//     tick where two runs diverge, not just that they did).
//   * On a hotkey, writes a full end-state report (sorted by StableId) to a file:
//     unit count, total HP, a checksum, and one line per unit. Diff two of these
//     files to confirm a run reproduced exactly.
//
// Phase 1: run twice, dump after each, diff -> proves FloatMode.Deterministic.
// Phase 2: record once, play back, dump after each, diff -> proves replay.
// ===========================================================================
public class SimResultDumper : MonoBehaviour
{
    [Tooltip("Press to write the end-state report.")]
    public KeyCode dumpKey = KeyCode.F9;
    [Tooltip("Output file under Application.persistentDataPath.")]
    public string dumpFileName = "lockstep_result.txt";
    public bool logChecksumEachSecond = true;

    private float _logTimer;

    private void Update()
    {
        var w = World.DefaultGameObjectInjectionWorld;
        if (w == null) return;

        if (logChecksumEachSecond)
        {
            _logTimer += Time.deltaTime;
            if (_logTimer >= 1f)
            {
                _logTimer = 0f;
                LogChecksum(w);
            }
        }

        if (Input.GetKeyDown(dumpKey))
            DumpReport(w);
    }

    private static void LogChecksum(World w)
    {
        var q = w.EntityManager.CreateEntityQuery(typeof(SimChecksum));
        if (q.HasSingleton<SimChecksum>())
        {
            var cs = q.GetSingleton<SimChecksum>();
            Debug.Log($"[Lockstep] tick {cs.Tick}  checksum {cs.Value:X8}");
        }
        q.Dispose();
    }

    private void DumpReport(World w)
    {
        var em = w.EntityManager;
        var q = em.CreateEntityQuery(
            ComponentType.ReadOnly<StableId>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<Health>(),
            ComponentType.ReadOnly<Team>());

        var ids    = q.ToComponentDataArray<StableId>(Allocator.Temp);
        var xforms = q.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var hps    = q.ToComponentDataArray<Health>(Allocator.Temp);
        var teams  = q.ToComponentDataArray<Team>(Allocator.Temp);

        uint tick = 0;
        var cq = em.CreateEntityQuery(typeof(SimClock));
        if (cq.HasSingleton<SimClock>()) tick = cq.GetSingleton<SimClock>().Tick;
        cq.Dispose();

        // Gather and sort by StableId so the report is order-independent and diffable.
        var rows = new List<(int id, int team, float x, float z, float hp)>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
            rows.Add((ids[i].Value, teams[i].Value, xforms[i].Position.x, xforms[i].Position.z, hps[i].Current));
        rows.Sort((a, b) => a.id.CompareTo(b.id));

        double totalHp = 0;
        uint checksum = 0;
        foreach (var r in rows)
        {
            totalHp += r.hp;
            uint a = math.hash(new uint4(math.asuint(r.x), math.asuint(r.z), math.asuint(r.hp), (uint)r.id));
            checksum += a ^ (uint)(r.team * 2654435761u);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"tick={tick}");
        sb.AppendLine($"units={rows.Count}");
        sb.AppendLine($"totalHP={totalHp:R}");
        sb.AppendLine($"checksum={checksum:X8}");
        sb.AppendLine("# id,team,x,z,hp  (x/z/hp printed as raw float bits for exact diff)");
        foreach (var r in rows)
            sb.AppendLine($"{r.id},{r.team},{math.asuint(r.x):X8},{math.asuint(r.z):X8},{math.asuint(r.hp):X8}");

        string path = Path.Combine(Application.persistentDataPath, dumpFileName);
        File.WriteAllText(path, sb.ToString());
        Debug.Log($"[Lockstep] dumped {rows.Count} units (HP {totalHp:R}, checksum {checksum:X8}) to {path}");

        ids.Dispose(); xforms.Dispose(); hps.Dispose(); teams.Dispose();
    }
}
