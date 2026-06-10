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
    [Tooltip("Output file under Application.persistentDataPath. Mode and tick are appended to the name.")]
    public string dumpFileName = "lockstep_result.txt";
    public bool logChecksumEachSecond = true;

    [Tooltip("If > 0: the sim HALTS exactly at this tick and the report is written automatically. " +
             "Set the SAME value for the record run and the playback run — that makes the two dump " +
             "files tick-exact comparable (manual F9 in two runs lands on different ticks). 0 = off.")]
    public uint autoDumpAtTick = 0;

    private float _logTimer;
    private uint _lastLoggedTick;
    private float _lastLogTime;
    private bool _autoDumped;

    private void Start()
    {
        LockstepRateManager.HaltAtTick = autoDumpAtTick;   // 0 = no halt
    }

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

        // Sim is frozen exactly at the halt tick (rate-manager halt, or turn
        // starvation when networked), so this dump captures identical-tick state
        // in every run AND on every peer. HaltAtTick rather than the local
        // Inspector value: in network sessions the host distributes it via the
        // Start message, overriding stale scene values on clients.
        uint haltTick = LockstepRateManager.HaltAtTick;
        if (haltTick > 0 && !_autoDumped && SimClockSystem.LastCompletedTick >= haltTick)
        {
            _autoDumped = true;
            DumpReport(w);
            Debug.Log($"[Lockstep] sim halted at tick {SimClockSystem.LastCompletedTick}; auto-dump written.");
        }

        if (Input.GetKeyDown(dumpKey))
            DumpReport(w);
    }

    private void LogChecksum(World w)
    {
        var q = w.EntityManager.CreateEntityQuery(typeof(SimChecksum));
        if (q.HasSingleton<SimChecksum>())
        {
            var cs = q.GetSingleton<SimChecksum>();

            // Measured sim rate since the last log line. Healthy = ~TickRate (30).
            // ~frame rate (e.g. 170+) = the lockstep rate manager is NOT installed.
            float now = Time.realtimeSinceStartup;
            float tps = _lastLogTime > 0f && now > _lastLogTime
                ? (cs.Tick - _lastLoggedTick) / (now - _lastLogTime) : 0f;
            _lastLoggedTick = cs.Tick;
            _lastLogTime = now;

            Debug.Log($"[Lockstep] tick {cs.Tick}  checksum {cs.Value:X8}  (~{tps:0.0} ticks/s, target {LockstepConfig.TickRate})");
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

        // Encode mode + instance + tick in the filename. The instance tag matters:
        // MPPM virtual players share the SAME persistentDataPath (same company/
        // product identity), so without it host and client dumps overwrite each
        // other. Same-tick files are the comparable pairs.
        string instance = "";
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm != null && (nm.IsServer || nm.IsClient))
            instance = nm.IsServer ? "_host" : $"_client{nm.LocalClientId}";

        string baseName = Path.GetFileNameWithoutExtension(dumpFileName);
        string ext = Path.GetExtension(dumpFileName);
        if (string.IsNullOrEmpty(ext)) ext = ".txt";
        string path = Path.Combine(Application.persistentDataPath, $"{baseName}_{Commander.Mode}{instance}_t{tick}{ext}");
        File.WriteAllText(path, sb.ToString());
        Debug.Log($"[Lockstep] dumped {rows.Count} units (HP {totalHp:R}, checksum {checksum:X8}) to {path}");

        ids.Dispose(); xforms.Dispose(); hps.Dispose(); teams.Dispose();
    }
}
