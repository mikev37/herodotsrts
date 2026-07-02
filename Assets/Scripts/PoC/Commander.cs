using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Serialization;

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
// stamps its own player id as PlayerId. Inspector mode/fileName on the instance with
// configuresStream ticked configures the shared stream (use your PlayerCommander).
//
// In Network mode, run AI commanders on ONE machine only: their orders enter
// the stream and are distributed to all peers like a player's.
// ===========================================================================
public abstract class Commander : MonoBehaviour
{
    public enum LockstepMode { Live, Record, Playback, Network }

    [Header("Commander")]
    [SerializeField, FormerlySerializedAs("team")] protected int player = 0;
    public int Player => player;

    // Runtime player assignment (Phase 4): in a network session the local
    // PlayerCommander's id comes from the lobby (host-assigned via
    // LockstepNet), not the serialized Inspector value.
    public void SetPlayer(int newPlayer) => player = newPlayer;

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
    protected EntityQuery AllUnitsQuery;     // UnitTag + Player + LocalTransform
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
            ComponentType.ReadOnly<Player>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.Exclude<Immobile>());   // buildings/walls aren't selectable/orderable units
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

    // formationWidth: grid columns for the resulting formation. 0 => the apply
    // system auto-fits (sqrt of the count). PlayerCommander passes a value
    // derived from the right-drag length; AICommander leaves it 0.
    protected void IssueMove(List<Entity> units, float2 dest, bool attackMove = false, int formationWidth = 0)
    {
        Select(units);
        Issue(attackMove ? CommandKind.AttackMove : CommandKind.Move, dest, -1, 0, formationWidth);
        lastOrder = $"{(attackMove ? "AttackMove" : "Move")} {_selection.Count} -> ({dest.x:0.#},{dest.y:0.#})" +
                    (formationWidth > 0 ? $" w{formationWidth}" : "");
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

    // Place building `defId` (this player's roster index; must be a
    // BuildingDefinition) centered near `pos`. Snapping and validation happen
    // deterministically at the execution tick (CommandApplySystem).
    protected void IssuePlaceBuilding(int defId, float2 pos)
    {
        _selection.Clear();
        Issue(CommandKind.PlaceBuilding, pos, defId, 0);
        lastOrder = $"PlaceBuilding def {defId} @ ({pos.x:0.#},{pos.y:0.#})";
    }

    // Demolish an own building by StableId (sets its health to 0 at the
    // execution tick; the normal death pipeline does the rest).
    protected void IssueDemolishBuilding(int stableId)
    {
        _selection.Clear();
        Issue(CommandKind.DemolishBuilding, default, stableId, 0);
        lastOrder = $"Demolish building {stableId}";
    }

    private void Issue(CommandKind kind, float2 pos, int targetId, byte abilitySlot, int formationWidth = 0)
    {
        if (Mode == LockstepMode.Playback) return;   // live input ignored during playback

        var cmd = new SimCommand
        {
            Tick           = CurrentTick() + (uint)LockstepConfig.InputDelayTicks,
            PlayerId       = player,
            Kind           = kind,
            TargetPos      = pos,
            TargetStableId = targetId,
            AbilitySlot    = abilitySlot,
            FormationWidth = formationWidth,
            Units          = ToFixed(_selection),
        };
        Outbox.Enqueue(cmd);
        if (Mode == LockstepMode.Record) RecordedList.Add(cmd);
    }

    // Overload for commands that carry a secondary id (production unit defId, upgrade target defId).
    private void Issue(CommandKind kind, float2 pos, int targetId, int targetId2, byte abilitySlot)
    {
        if (Mode == LockstepMode.Playback) return;
        var cmd = new SimCommand
        {
            Tick            = CurrentTick() + (uint)LockstepConfig.InputDelayTicks,
            PlayerId        = player,
            Kind            = kind,
            TargetPos       = pos,
            TargetStableId  = targetId,
            TargetStableId2 = targetId2,
            AbilitySlot     = abilitySlot,
            Units           = ToFixed(_selection),
        };
        Outbox.Enqueue(cmd);
        if (Mode == LockstepMode.Record) RecordedList.Add(cmd);
    }

    // ---- economy verbs -------------------------------------------------------

    protected void IssueHarvest(List<Entity> unitList, int nodeStableId)
    {
        if (unitList.Count == 0) return;
        _selection.Clear();
        foreach (var e in unitList)
            if (Em.HasComponent<StableId>(e)) _selection.Add(Em.GetComponentData<StableId>(e).Value);
        Issue(CommandKind.Harvest, default, nodeStableId, 0);
        lastOrder = $"Harvest node {nodeStableId}";
    }

    protected void IssueSetRally(int buildingStableId, float2 pos)
    {
        Issue(CommandKind.SetRally, pos, buildingStableId, 0);
        lastOrder = $"Rally → {pos}";
    }

    protected void IssueQueueProduction(int buildingStableId, int unitDefId)
    {
        Issue(CommandKind.QueueProduction, default, buildingStableId, unitDefId, 0);
        lastOrder = $"Queue unit {unitDefId} at building {buildingStableId}";
    }

    protected void IssueCancelProduction(int buildingStableId, bool fromHead)
    {
        Issue(CommandKind.CancelProduction, default, buildingStableId, (byte)(fromHead ? 0 : 1));
        lastOrder = $"Cancel production ({(fromHead ? "head" : "tail")})";
    }

    protected void IssueToggleBankPause(int bankStableId)
    {
        Issue(CommandKind.ToggleBankPause, default, bankStableId, 0);
        lastOrder = "Toggle bank pause";
    }

    protected void IssuePlaceBlueprint(int defId, float2 pos)
    {
        Issue(CommandKind.PlaceBlueprint, pos, defId, 0);
        lastOrder = $"Blueprint def {defId} @ {pos}";
    }

    protected void IssueToggleProducerLoop(int buildingStableId)
    {
        Issue(CommandKind.ToggleProducerLoop, default, buildingStableId, 0);
        lastOrder = "Toggle loop";
    }

    protected void IssueToggleSpendPriority(int buildingStableId)
    {
        Issue(CommandKind.ToggleSpendPriority, default, buildingStableId, 0);
        lastOrder = "Toggle spend priority";
    }

    protected void IssueMorph(List<Entity> unitList)
    {
        if (unitList.Count == 0) return;
        _selection.Clear();
        foreach (var e in unitList)
            if (Em.HasComponent<StableId>(e)) _selection.Add(Em.GetComponentData<StableId>(e).Value);
        Issue(CommandKind.Morph, default, 0, 0);
        lastOrder = "Morph";
    }

    protected void IssueUpgrade(int buildingStableId, int targetDefId)
    {
        Issue(CommandKind.Upgrade, default, buildingStableId, targetDefId, 0);
        lastOrder = $"Upgrade building {buildingStableId} → def {targetDefId}";
    }

    protected void IssueResearch(int buildingStableId, int techIndex)
    {
        Issue(CommandKind.Research, default, buildingStableId, (byte)techIndex);
        lastOrder = $"Research [{techIndex}] at building {buildingStableId}";
    }

	private void OnDestroy() {
        SaveNow();
	}

	// --- helpers ------------------------------------------------------------

	protected List<Entity> GetPlayerUnits()
    {
        var list = new List<Entity>();
        if (!WorldOk) return list;
        var entities = AllUnitsQuery.ToEntityArray(Allocator.Temp);
        var players = AllUnitsQuery.ToComponentDataArray<Player>(Allocator.Temp);
        for (int i = 0; i < entities.Length; i++)
            if (players[i].Value == player) list.Add(entities[i]);
        entities.Dispose(); players.Dispose();
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
            w.Write(c.FormationWidth);
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
                FormationWidth = r.ReadInt32(),
            };
            int uc = r.ReadInt32();
            for (int i = 0; i < uc; i++) c.Units.Add(r.ReadInt32());
            RecordedList.Add(c);
        }
        Debug.Log($"[Lockstep] loaded {RecordedList.Count} commands from {FilePath}");
    }
}
