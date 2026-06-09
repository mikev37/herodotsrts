
// -------------------------------------------------------------------------
// CommandController: the API your input/AI code calls. Attach to a GameObject.
// Replace any code that sets MoveTarget/AttackOrder directly with calls to
// Select(...) + OrderMove(...) etc. so EVERYTHING goes through here.
// -------------------------------------------------------------------------
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public class CommandController : MonoBehaviour {
    public enum LockstepMode { Live, Record, Playback }

    [Tooltip("Live = play normally; Record = play + save commands; Playback = replay a saved file.")]
    public LockstepMode mode = LockstepMode.Live;
    [Tooltip("File under Application.persistentDataPath.")]
    public string fileName = "lockstep_commands.bin";
    public int localPlayerId = 0;

    public static CommandController Instance { get; private set; }

    // Live/Record: orders waiting to be handed to ECS. Playback: filled from file.
    public readonly Queue<SimCommand> Outbox = new();
    private readonly List<SimCommand> _recorded = new();
    private readonly List<int> _selection = new();

    public IReadOnlyList<SimCommand> Recorded => _recorded;

    private void Awake() { Instance = this; }

    private void Start() {
        if (mode == LockstepMode.Playback) LoadRecording();
    }

    private void OnApplicationQuit() {
        if (mode == LockstepMode.Record) SaveRecording();
    }

    // --- input-facing API ---------------------------------------------------

    // Selection is LOCAL state (it isn't networked) — each order captures the
    // current selection's StableIds, and that unit set is what gets recorded.
    public void Select(IEnumerable<int> stableIds) {
        _selection.Clear();
        if (stableIds != null) _selection.AddRange(stableIds);
    }

    public void OrderMove(float2 pos, bool attackMove)
        => Issue(attackMove ? CommandKind.AttackMove : CommandKind.Move, pos, -1);

    public void OrderStop()
        => Issue(CommandKind.Stop, default, -1);

    public void OrderAttack(int targetStableId)
        => Issue(CommandKind.AttackTarget, default, targetStableId);

    // Call this from a hotkey when recording to flush the file mid-session.
    public void SaveNow() {
        if (mode == LockstepMode.Record) SaveRecording();
    }

    private void Issue(CommandKind kind, float2 pos, int targetId) {
        if (mode == LockstepMode.Playback) return;   // live input is ignored during playback

        var cmd = new SimCommand {
            Tick = CurrentTick() + (uint)LockstepConfig.InputDelayTicks,
            PlayerId = localPlayerId,
            Kind = kind,
            TargetPos = pos,
            TargetStableId = targetId,
            Units = ToFixed(_selection),
        };
        Outbox.Enqueue(cmd);
        if (mode == LockstepMode.Record) _recorded.Add(cmd);
    }

    // --- helpers ------------------------------------------------------------

    private static uint CurrentTick() {
        var w = World.DefaultGameObjectInjectionWorld;
        if (w == null) return 0;
        var q = w.EntityManager.CreateEntityQuery(typeof(SimClock));
        uint t = q.HasSingleton<SimClock>() ? q.GetSingleton<SimClock>().Tick : 0u;
        q.Dispose();
        return t;
    }

    private static FixedList512Bytes<int> ToFixed(List<int> ids) {
        var f = new FixedList512Bytes<int>();
        int n = math.min(ids.Count, f.Capacity);
        for (int i = 0; i < n; i++) f.Add(ids[i]);
        if (ids.Count > f.Capacity)
            Debug.LogWarning($"[Lockstep] selection of {ids.Count} exceeds command capacity {f.Capacity}; extra units dropped from this order.");
        return f;
    }

    private string FilePath => Path.Combine(Application.persistentDataPath, fileName);

    private void SaveRecording() {
        using var w = new BinaryWriter(File.Open(FilePath, FileMode.Create));
        w.Write(_recorded.Count);
        foreach (var c in _recorded) {
            w.Write(c.Tick);
            w.Write(c.PlayerId);
            w.Write((byte)c.Kind);
            w.Write(c.TargetPos.x);
            w.Write(c.TargetPos.y);
            w.Write(c.TargetStableId);
            w.Write(c.Units.Length);
            for (int i = 0; i < c.Units.Length; i++) w.Write(c.Units[i]);
        }
        Debug.Log($"[Lockstep] saved {_recorded.Count} commands to {FilePath}");
    }

    private void LoadRecording() {
        if (!File.Exists(FilePath)) {
            Debug.LogWarning($"[Lockstep] no recording at {FilePath}");
            return;
        }
        using var r = new BinaryReader(File.Open(FilePath, FileMode.Open));
        int n = r.ReadInt32();
        _recorded.Clear();
        for (int k = 0; k < n; k++) {
            var c = new SimCommand {
                Tick = r.ReadUInt32(),
                PlayerId = r.ReadInt32(),
                Kind = (CommandKind)r.ReadByte(),
                TargetPos = new float2(r.ReadSingle(), r.ReadSingle()),
                TargetStableId = r.ReadInt32(),
            };
            int uc = r.ReadInt32();
            for (int i = 0; i < uc; i++) c.Units.Add(r.ReadInt32());
            _recorded.Add(c);
        }
        Debug.Log($"[Lockstep] loaded {_recorded.Count} commands from {FilePath}");
    }
}