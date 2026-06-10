using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Netcode;
using UnityEngine;

// ===========================================================================
// LockstepNet — Phase 3 network layer.
//
// Host-relayed deterministic lockstep over Netcode for GameObjects (NGO). NGO is
// used ONLY as a connection manager + reliable message channel (CustomMessaging);
// none of its state-replication / NetworkVariable / spawn machinery touches the
// simulation. The sim stays our deterministic ECS world.
//
// Protocol (per execution tick T):
//   1. Every peer submits its commands for tick T to the host (empty submission
//      still sent, as a "ready" signal). Peers run their submissions InputDelay
//      ticks AHEAD of execution, which is the budget for network latency.
//   2. The host collects submissions from ALL participants for T, concatenates
//      them in a deterministic order, and broadcasts the combined turn to everyone
//      (itself included).
//   3. A peer may execute tick T only once it holds the combined turn for T. The
//      LockstepRateManager calls TryBeginNextTurn(), which injects T's commands
//      into the ECS command buffer and lets the sim advance exactly one tick.
//
// Because every peer applies the identical command set at the identical tick to an
// identical starting state with deterministic float math, all peers stay in sync.
//
// Requires packages: com.unity.netcode.gameobjects and com.unity.transport.
// Scene setup + Multiplayer Play Mode steps are in README_LOCKSTEP.md.
// ===========================================================================
public class LockstepNet : MonoBehaviour
{
    private const string MSG_INPUT = "ls_input";   // client -> server: my commands for a tick
    private const string MSG_TURN  = "ls_turn";    // server -> clients: combined commands for a tick
    private const string MSG_START = "ls_start";   // server -> clients: begin the lockstep loop

    public static LockstepNet Instance { get; private set; }

    public bool IsRunning { get; private set; }

    // Execution side (all peers).
    private uint _execTick = 1;     // next tick to execute
    private uint _sentUpTo;         // highest tick we've submitted input for
    private readonly Dictionary<uint, List<SimCommand>> _turns = new();   // confirmed turns awaiting execution

    // Host side.
    private readonly Dictionary<uint, Dictionary<ulong, List<SimCommand>>> _inbox = new();
    private List<ulong> _participants = new();

    private bool _handlersRegistered;
    private EntityQuery _queueQuery;
    private bool _queueQueryReady;

    private void Awake() => Instance = this;

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // --- connection control (wired to the IMGUI below) ----------------------

    private void StartHost()
    {
        if (NetworkManager.Singleton.StartHost())
        {
            RegisterHandlers();
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
        }
    }

    private void StartClient()
    {
        if (NetworkManager.Singleton.StartClient())
            RegisterHandlers();
    }

    private void RegisterHandlers()
    {
        if (_handlersRegistered) return;
        var cmm = NetworkManager.Singleton.CustomMessagingManager;
        cmm.RegisterNamedMessageHandler(MSG_INPUT, OnInputMsg);
        cmm.RegisterNamedMessageHandler(MSG_TURN,  OnTurnMsg);
        cmm.RegisterNamedMessageHandler(MSG_START, OnStartMsg);
        _handlersRegistered = true;
    }

    // Host presses "Start Game" once all clients are connected. Freezes the roster
    // of participants, tells everyone to go, and begins locally. The Start message
    // carries the HOST's halt tick: virtual players / clients load Inspector values
    // from the saved scene and can be stale, so the halt (and therefore tick-exact
    // auto-dumps) must be a networked decision, not a per-instance one.
    private void HostStartGame()
    {
        _participants = new List<ulong>(NetworkManager.Singleton.ConnectedClientsIds);
        _participants.Sort();

        foreach (var cid in NetworkManager.Singleton.ConnectedClientsIds)
        {
            if (cid == NetworkManager.Singleton.LocalClientId) continue;
            using var w = new FastBufferWriter(8, Allocator.Temp);
            w.WriteValueSafe(LockstepRateManager.HaltAtTick);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                MSG_START, cid, w, NetworkDelivery.Reliable);
        }
        BeginLocal();
    }

    private void BeginLocal()
    {
        _execTick = 1;
        _sentUpTo = 0;
        _turns.Clear();
        _inbox.Clear();
        IsRunning = true;
    }

    // --- per-frame input pump -----------------------------------------------

    private void Update()
    {
        if (!IsRunning) return;

        // Keep our submitted inputs InputDelay ticks ahead of execution. That gap
        // is what absorbs network latency. Never submit past the halt tick: the
        // host's sim freezes there but its pump would otherwise keep manufacturing
        // turns up to halt+delay, letting peers without a local halt spill past
        // (observed as a client stopping at 403 with a halt of 400 and delay 2).
        // With the cap, turns simply run out at the halt and every peer starves
        // to a stop at exactly the same tick.
        uint target = _execTick + (uint)LockstepConfig.InputDelayTicks;
        if (LockstepRateManager.HaltAtTick > 0 && target > LockstepRateManager.HaltAtTick)
            target = LockstepRateManager.HaltAtTick;
        while (_sentUpTo < target)
        {
            _sentUpTo++;
            SubmitInput(_sentUpTo);
        }
    }

    private void SubmitInput(uint tick)
    {
        // Drain whatever the local commanders issued since last submit; stamp it
        // onto this submission tick (the net layer owns final tick assignment).
        var list = new List<SimCommand>();
        while (Commander.Outbox.Count > 0)
        {
            var c = Commander.Outbox.Dequeue();
            c.Tick = tick;
            list.Add(c);
        }

        if (NetworkManager.Singleton.IsServer)
        {
            ServerReceiveInput(tick, list, NetworkManager.Singleton.LocalClientId);
        }
        else
        {
            using var w = new FastBufferWriter(2048, Allocator.Temp, 256 * 1024);
            w.WriteValueSafe(tick);
            w.WriteValueSafe(list.Count);
            for (int i = 0; i < list.Count; i++) w.WriteValueSafe(list[i]);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                MSG_INPUT, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableFragmentedSequenced);
        }
    }

    // --- host: aggregate inputs, broadcast turns ----------------------------

    private void OnInputMsg(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out uint tick);
        reader.ReadValueSafe(out int count);
        var list = new List<SimCommand>(count);
        for (int i = 0; i < count; i++) { reader.ReadValueSafe(out SimCommand c); list.Add(c); }
        ServerReceiveInput(tick, list, sender);
    }

    private void ServerReceiveInput(uint tick, List<SimCommand> cmds, ulong sender)
    {
        if (!_inbox.TryGetValue(tick, out var bySender))
        {
            bySender = new Dictionary<ulong, List<SimCommand>>();
            _inbox[tick] = bySender;
        }
        bySender[sender] = cmds;

        if (!AllSubmitted(tick)) return;

        // Concatenate in deterministic participant order so the combined turn is
        // identical for everyone (the host then broadcasts it verbatim).
        var combined = new List<SimCommand>();
        for (int i = 0; i < _participants.Count; i++)
            if (bySender.TryGetValue(_participants[i], out var c)) combined.AddRange(c);

        _inbox.Remove(tick);
        BroadcastTurn(tick, combined);
    }

    private bool AllSubmitted(uint tick)
    {
        if (!_inbox.TryGetValue(tick, out var bySender)) return false;
        for (int i = 0; i < _participants.Count; i++)
            if (!bySender.ContainsKey(_participants[i])) return false;
        return true;
    }

    private void BroadcastTurn(uint tick, List<SimCommand> combined)
    {
        _turns[tick] = combined;   // host executes from its own copy

        foreach (var cid in NetworkManager.Singleton.ConnectedClientsIds)
        {
            if (cid == NetworkManager.Singleton.LocalClientId) continue;
            using var w = new FastBufferWriter(2048, Allocator.Temp, 256 * 1024);
            w.WriteValueSafe(tick);
            w.WriteValueSafe(combined.Count);
            for (int i = 0; i < combined.Count; i++) w.WriteValueSafe(combined[i]);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                MSG_TURN, cid, w, NetworkDelivery.ReliableFragmentedSequenced);
        }
    }

    // --- client: receive turns / start --------------------------------------

    private void OnTurnMsg(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out uint tick);
        reader.ReadValueSafe(out int count);
        var list = new List<SimCommand>(count);
        for (int i = 0; i < count; i++) { reader.ReadValueSafe(out SimCommand c); list.Add(c); }
        _turns[tick] = list;
    }

    private void OnStartMsg(ulong sender, FastBufferReader reader)
    {
        // Adopt the host's halt tick — overrides any stale local Inspector value
        // so every peer halts (and auto-dumps) at the exact same tick.
        reader.ReadValueSafe(out uint haltAtTick);
        LockstepRateManager.HaltAtTick = haltAtTick;
        BeginLocal();
    }

    // --- execution gate (called by LockstepRateManager) ---------------------

    // Returns true and advances one tick iff the next turn is confirmed. Injects
    // that turn's commands into the ECS buffer first so CommandApplySystem fires
    // them on the tick the sim is about to run.
    public bool TryBeginNextTurn()
    {
        if (!_turns.TryGetValue(_execTick, out var cmds)) return false;
        InjectToBuffer(cmds);
        _turns.Remove(_execTick);
        _execTick++;
        return true;
    }

    private void InjectToBuffer(List<SimCommand> cmds)
    {
        if (cmds.Count == 0) return;
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;

        if (!_queueQueryReady)
        {
            _queueQuery = world.EntityManager.CreateEntityQuery(typeof(CommandQueueTag));
            _queueQueryReady = true;
        }
        if (!_queueQuery.HasSingleton<CommandQueueTag>()) return;

        var e = _queueQuery.GetSingletonEntity();
        var buf = world.EntityManager.GetBuffer<SimCommand>(e);
        for (int i = 0; i < cmds.Count; i++) buf.Add(cmds[i]);
    }

    // --- host: handle a dropped participant so we don't stall forever --------

    private void OnClientDisconnect(ulong clientId)
    {
        _participants.Remove(clientId);
        // A pending tick may now be complete with the remaining participants.
        var ready = new List<uint>();
        foreach (var kv in _inbox) if (AllSubmitted(kv.Key)) ready.Add(kv.Key);
        ready.Sort();
        foreach (var t in ready)
        {
            var bySender = _inbox[t];
            var combined = new List<SimCommand>();
            for (int i = 0; i < _participants.Count; i++)
                if (bySender.TryGetValue(_participants[i], out var c)) combined.AddRange(c);
            _inbox.Remove(t);
            BroadcastTurn(t, combined);
        }
    }

    // --- minimal IMGUI (works with Multiplayer Play Mode) -------------------

    private void OnGUI()
    {
        var nm = NetworkManager.Singleton;
        GUILayout.BeginArea(new Rect(10, 10, 240, 160), GUI.skin.box);
        if (nm == null)
        {
            GUILayout.Label("No NetworkManager in scene");
        }
        else if (!nm.IsClient && !nm.IsServer)
        {
            if (GUILayout.Button("Host")) StartHost();
            if (GUILayout.Button("Client")) StartClient();
        }
        else
        {
            GUILayout.Label(nm.IsServer ? "ROLE: HOST" : "ROLE: CLIENT");
            GUILayout.Label($"connected: {nm.ConnectedClientsIds.Count}");
            GUILayout.Label($"execTick: {_execTick}   running: {IsRunning}");
            if (nm.IsServer && !IsRunning && GUILayout.Button("Start Game"))
                HostStartGame();
        }
        GUILayout.EndArea();
    }
}
