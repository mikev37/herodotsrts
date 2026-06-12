using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// COMMANDER — the shared order API and THE single channel into the simulation.
// (Absorbs the former CommandController; that class is deleted.)
//
// The player UI and the AI are both just Commanders that decide WHICH units get
// WHICH order. Verbs no longer write MoveTarget/AttackOrder into ECS directly —
// they emit tick-stamped SimCommands into a shared stream, which
// CommandApplySystem executes on the scheduled tick. That one change is what
// makes every order recordable (replays), replayable (Playback), and
// networkable (lockstep) with identical behavior in all three.
//
// The STREAM (outbox / recording / mode) is static — shared by all Commander
// instances — because the sim has exactly one command timeline. Each instance
// stamps its own team as PlayerId. Inspector mode/fileName on the instance with
// configuresStream ticked configures the shared stream (use your PlayerCommander).
//
// In Network mode, run AI commanders on ONE machine only: their orders enter
// the stream and are distributed to all peers like a player's.
// ===========================================================================
public abstract class Commander : MonoBehaviour
{
    public enum LockstepMode { Live, Record, Playback, Network }

    [Header("Commander")]
    [SerializeField] protected int team = 0;
    public int Team => team;

    [Header("Lockstep stream (shared; configure on ONE instance)")]
    [Tooltip("Live = play normally; Record = play + save commands; Playback = replay a saved file; Network = LockstepNet distributes commands.")]
    [SerializeField] private LockstepMode mode = LockstepMode.Live;
    [Tooltip("Recording file under Application.persistentDataPath.")]
    [SerializeField] private string fileName = "lockstep_commands.bin";
    [Tooltip("Apply this instance's mode/fileName to the shared stream.")]
    [SerializeField] private bool configuresStream = false;

    [Header("Debug (runtime, read-only)")]
    [Tooltip("Last order this commander issued.")]
    public string lastOrder = "(none)";
    [Tooltip("True once the ECS world was found.")]
    public bool worldReady;

    protected EntityManager Em;
    protected EntityQuery AllUnitsQuery;     // UnitTag + Team + LocalTransform
    private EntityQuery _clockQuery;
    private bool _ready;

    // Per-commander selection, captured (as StableIds) into each order it issues.
    private readonly List<int> _selection = new();

    // --- the shared command stream ------------------------------------------
    public static LockstepMode Mode { get; private set; } = LockstepMode.Live;
    public static string FileName { get; private set; } = "lockstep_commands.bin";
    public static readonly Queue<SimCommand> Outbox = new();
    private static readonly List<SimCommand> RecordedList = new();
    public static IReadOnlyList<SimCommand> Recorded => RecordedList;

    protected virtual void Start()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) { worldReady = false; return; }
        Em = world.EntityManager;
        AllUnitsQuery = Em.CreateEntityQuery(
            ComponentType.ReadOnly<UnitTag>(),
            ComponentType.ReadOnly<Team>(),
            ComponentType.ReadOnly<LocalTransform>());
        _clockQuery = Em.CreateEntityQuery(ComponentType.ReadOnly<SimClock>());
        worldReady = _ready = true;

        if (configuresStream)
        {
            Mode = mode;
            FileName = fileName;
            if (Mode == LockstepMode.Playback) LoadRecording();
        }
    }

    private void OnApplicationQuit()
    {
        if (configuresStream && Mode == LockstepMode.Record) SaveRecording();
    }

    protected bool WorldOk => _ready && Em.World != null && Em.World.IsCreated;

    // --- selection ----------------------------------------------------------

    // Selection is LOCAL per-commander state (it isn't networked) — each order
    // captures the current selection's StableIds; that unit set is what gets
    // recorded/sent.
    protected void Select(List<Entity> units)
    {
        _selection.Clear();
        if (!WorldOk || units == null) return;
        foreach (var e in units)
            if (Em.Exists(e) && Em.HasComponent<StableId>(e))
                _selection.Add(Em.GetComponentData<StableId>(e).Value);
    }

    protected int SelectionCount => _selection.Count;

    // --- order verbs (the abstraction the AI shares) ------------------------

    protected void IssueMove(List<Entity> units, float2 dest, bool attackMove = false)
    {
        Select(units);
        Issue(attackMove ? CommandKind.AttackMove : CommandKind.Move, dest, -1, 0);
        lastOrder = $"{(attackMove ? "AttackMove" : "Move")} {_selection.Count} -> ({dest.x:0.#},{dest.y:0.#})";
    }

    protected void IssueAttack(List<Entity> units, Entity target, float2 targetPos)
    {
        if (!WorldOk || !Em.Exists(target) || !Em.HasComponent<StableId>(target)) return;
        Select(units);
        int sid = Em.GetComponentData<StableId>(target).Value;
        Issue(CommandKind.AttackTarget, targetPos, sid, 0);
        lastOrder = $"Attack {_selection.Count} -> unit {sid}";
    }

    protected void IssueStop(List<Entity> units)
    {
        Select(units);
        Issue(CommandKind.Stop, default, -1, 0);
        lastOrder = $"Stop {_selection.Count}";
    }

    // Cast ability `slot` of `caster` at/toward `castPos`. The caster is the only
    // unit in the command; cooldown gating happens deterministically at apply.
    protected void IssueAbility(Entity caster, int slot, float2 castPos)
    {
        if (!WorldOk || !Em.Exists(caster) || !Em.HasComponent<StableId>(caster)) return;
        _selection.Clear();
        _selection.Add(Em.GetComponentData<StableId>(caster).Value);
        Issue(CommandKind.Ability, castPos, -1, (byte)slot);
        lastOrder = $"Ability slot {slot} @ ({castPos.x:0.#},{castPos.y:0.#})";
    }

    // Place building `defId` (this team's roster index; must be a
    // BuildingDefinition) centered near `pos`. Snapping and validation happen
    // deterministically at the execution tick (CommandApplySystem).
    protected void IssuePlaceBuilding(int defId, float2 pos)
    {
        _selection.Clear();
        Issue(CommandKind.PlaceBuilding, pos, defId, 0);
        lastOrder = $"PlaceBuilding def {defId} @ ({pos.x:0.#},{pos.y:0.#})";
    }

    // Demolish an own-team building by StableId (sets its health to 0 at the
    // execution tick; the normal death pipeline does the rest).
    protected void IssueDemolishBuilding(int stableId)
    {
        _selection.Clear();
        Issue(CommandKind.DemolishBuilding, default, stableId, 0);
        lastOrder = $"Demolish building {stableId}";
    }

    private void Issue(CommandKind kind, float2 pos, int targetId, byte abilitySlot)
    {
        if (Mode == LockstepMode.Playback) return;   // live input ignored during playback

        var cmd = new SimCommand
        {
            Tick           = CurrentTick() + (uint)LockstepConfig.InputDelayTicks,
            PlayerId       = team,
            Kind           = kind,
            TargetPos      = pos,
            TargetStableId = targetId,
            AbilitySlot    = abilitySlot,
            Units          = ToFixed(_selection),
        };
        Outbox.Enqueue(cmd);
        if (Mode == LockstepMode.Record) RecordedList.Add(cmd);
    }

	private void OnDestroy() {
        SaveNow();
	}

	// --- helpers ------------------------------------------------------------

	protected List<Entity> GetTeamUnits()
    {
        var list = new List<Entity>();
        if (!WorldOk) return list;
        var entities = AllUnitsQuery.ToEntityArray(Allocator.Temp);
        var teams = AllUnitsQuery.ToComponentDataArray<Team>(Allocator.Temp);
        for (int i = 0; i < entities.Length; i++)
            if (teams[i].Value == team) list.Add(entities[i]);
        entities.Dispose(); teams.Dispose();
        return list;
    }

    protected uint CurrentTick()
        => WorldOk && _clockQuery.HasSingleton<SimClock>() ? _clockQuery.GetSingleton<SimClock>().Tick : 0u;

    private static FixedList512Bytes<int> ToFixed(List<int> ids)
    {
        var f = new FixedList512Bytes<int>();
        int n = math.min(ids.Count, f.Capacity);
        for (int i = 0; i < n; i++) f.Add(ids[i]);
        if (ids.Count > f.Capacity)
            Debug.LogWarning($"[Lockstep] selection of {ids.Count} exceeds command capacity {f.Capacity}; extra units dropped from this order.");
        return f;
    }

    // Hotkey-friendly mid-session flush while recording.
    public static void SaveNow()
    {
        if (Mode == LockstepMode.Record) SaveRecording();
    }

    private static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

    private static void SaveRecording()
    {
        using var w = new BinaryWriter(File.Open(FilePath, FileMode.Create));
        w.Write(RecordedList.Count);
        foreach (var c in RecordedList)
        {
            w.Write(c.Tick);
            w.Write(c.PlayerId);
            w.Write((byte)c.Kind);
            w.Write(c.TargetPos.x);
            w.Write(c.TargetPos.y);
            w.Write(c.TargetStableId);
            w.Write(c.AbilitySlot);
            w.Write(c.Units.Length);
            for (int i = 0; i < c.Units.Length; i++) w.Write(c.Units[i]);
        }
        Debug.Log($"[Lockstep] saved {RecordedList.Count} commands to {FilePath}");
    }

    private static void LoadRecording()
    {
        if (!File.Exists(FilePath))
        {
            Debug.LogWarning($"[Lockstep] no recording at {FilePath}");
            return;
        }
        using var r = new BinaryReader(File.Open(FilePath, FileMode.Open));
        int n = r.ReadInt32();
        RecordedList.Clear();
        for (int k = 0; k < n; k++)
        {
            var c = new SimCommand
            {
                Tick           = r.ReadUInt32(),
                PlayerId       = r.ReadInt32(),
                Kind           = (CommandKind)r.ReadByte(),
                TargetPos      = new float2(r.ReadSingle(), r.ReadSingle()),
                TargetStableId = r.ReadInt32(),
                AbilitySlot    = r.ReadByte(),
            };
            int uc = r.ReadInt32();
            for (int i = 0; i < uc; i++) c.Units.Add(r.ReadInt32());
            RecordedList.Add(c);
        }
        Debug.Log($"[Lockstep] loaded {RecordedList.Count} commands from {FilePath}");
    }
}
