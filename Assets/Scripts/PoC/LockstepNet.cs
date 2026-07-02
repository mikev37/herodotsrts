using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.Netcode;
using UnityEngine;

// ===========================================================================
// LockstepNet — Phase 3 turn relay + Phase 4 snapshot sync.
//
// Host-relayed deterministic lockstep over Netcode for GameObjects (NGO). NGO is
// used ONLY as a connection manager + reliable message channel (CustomMessaging);
// none of its state-replication / NetworkVariable / spawn machinery touches the
// simulation. The sim stays our deterministic ECS world.
//
// Turn protocol (per execution tick T), unchanged from Phase 3:
//   1. Every peer submits its commands for tick T to the host (empty submission
//      still sent, as a "ready" signal). Peers run their submissions InputDelay
//      ticks AHEAD of execution, which is the budget for network latency.
//   2. The host collects submissions from ALL participants for T, concatenates
//      them in a deterministic order, and broadcasts the combined turn to everyone
//      (itself included).
//   3. A peer may execute tick T only once it holds the combined turn for T.
//
// Phase 4 layers snapshot distribution on top. ONE mechanism covers game start,
// load, and desync recovery, because in network mode a sim world only ever comes
// into existence by restoring a host snapshot (see SimSnapshot.cs for why every
// peer — host included — must rebuild from the same blob):
//
//   * Lobby: peers pick a player (MSG_PLAYER). Nobody spawns anything.
//   * Start: host spawns its world, captures it, and distributes (epoch N+1).
//   * Load:  host reads the save file and distributes it the same way.
//   * Desync: clients piggyback their latest per-tick checksum on every input
//     submission; the host compares against its own ChecksumHistory at that
//     tick. On mismatch it logs the first divergent tick and redistributes its
//     current state — a pause-resync that heals every peer.
//
// Distribution (HostDistribute): bump the epoch, pause, restore the host's own
// world from the blob first (canonical layout + reference hash), stream the blob
// to each client in chunks, collect MSG_ACK hashes, and on unanimous agreement
// broadcast MSG_RESUME. Every message carries the epoch; anything stamped with
// an old epoch is a straggler from a dead timeline and is dropped. A hash
// mismatch at ack time means the restore itself is nondeterministic — that is a
// bug to root-cause, so the game stays paused and says so rather than retrying.
//
// Player authority: the host stamps PlayerId on every remote command from the
// sender's lobby-assigned player, and CommandApplySystem rejects commands aimed at
// units of another player — a client can't order the other side around.
//
// Requires packages: com.unity.netcode.gameobjects and com.unity.transport.
// Scene setup + Multiplayer Play Mode steps are in README_LOCKSTEP.md.
// ===========================================================================
public class LockstepNet : MonoBehaviour
{
    private const string MSG_INPUT  = "ls_input";    // client -> server: epoch, checksum report, commands
    private const string MSG_TURN   = "ls_turn";     // server -> clients: epoch, combined commands for a tick
    private const string MSG_PLAYER   = "ls_player";     // client -> server: lobby player choice
    private const string MSG_SNAP_B = "ls_snap_b";   // server -> client: snapshot header + player assignments
    private const string MSG_SNAP_C = "ls_snap_c";   // server -> client: one snapshot chunk
    private const string MSG_ACK    = "ls_ack";      // client -> server: restored, here's my state hash
    private const string MSG_RESUME = "ls_resume";   // server -> clients: all verified, run from tick T

    // global: snapshot transfer chunk size. Comfortably under NGO/UTP fragmented
    // message limits; ~600 units is ~15 chunks.
    private const int SnapshotChunkBytes = 16 * 1024;

    public static LockstepNet Instance { get; private set; }

    public bool IsRunning { get; private set; }
    public bool IsPaused  { get; private set; }   // frozen for a snapshot sync (rate manager gates on this)

    // Execution side (all peers).
    private uint _epoch;            // bumped by every snapshot distribution; stamps all input/turn traffic
    private uint _execTick = 1;     // next tick to execute
    private uint _sentUpTo;         // highest tick we've submitted input for
    private readonly Dictionary<uint, List<SimCommand>> _turns = new();   // confirmed turns awaiting execution

    // Host side.
    private readonly Dictionary<uint, Dictionary<ulong, List<SimCommand>>> _inbox = new();
    private List<ulong> _participants = new();
    private readonly Dictionary<ulong, int> _playerOf = new();                       // lobby player assignments
    private readonly Dictionary<ulong, (uint tick, uint hash)> _reports = new();   // latest client checksum reports
    private readonly HashSet<ulong> _pendingAcks = new();
    private uint _syncTick;         // tick of the snapshot being distributed
    private uint _hostHash;         // host's post-restore hash — the verification reference
    private bool _syncing;
    private bool _started;          // players are locked and a world exists once true
    private bool _syncFailed;       // a peer's restore hash disagreed — paused until root-caused

    // Client-side snapshot assembly.
    private byte[] _snapData;
    private int  _snapChunks, _snapGot;
    private uint _snapEpoch, _snapTick;

    private int _myPlayer = -1;       // local display + lobby choice

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
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            _playerOf[NetworkManager.Singleton.LocalClientId] = 0;   // host defaults to player 1
            _myPlayer = 0;
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
        cmm.RegisterNamedMessageHandler(MSG_INPUT,  OnInputMsg);
        cmm.RegisterNamedMessageHandler(MSG_TURN,   OnTurnMsg);
        cmm.RegisterNamedMessageHandler(MSG_PLAYER,   OnPlayerMsg);
        cmm.RegisterNamedMessageHandler(MSG_SNAP_B, OnSnapBegin);
        cmm.RegisterNamedMessageHandler(MSG_SNAP_C, OnSnapChunk);
        cmm.RegisterNamedMessageHandler(MSG_ACK,    OnAck);
        cmm.RegisterNamedMessageHandler(MSG_RESUME, OnResume);
        _handlersRegistered = true;
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!NetworkManager.Singleton.IsServer) return;
        if (clientId == NetworkManager.Singleton.LocalClientId) return;
        if (!_playerOf.ContainsKey(clientId)) _playerOf[clientId] = 1;   // new clients default to player 2
    }

    // --- lobby: player selection ------------------------------------------------

    private void ChoosePlayer(int player)
    {
        if (_started) return;   // locked once a world exists
        _myPlayer = player;
        var nm = NetworkManager.Singleton;
        if (nm.IsServer)
        {
            _playerOf[nm.LocalClientId] = player;
        }
        else
        {
            using var w = new FastBufferWriter(8, Allocator.Temp);
            w.WriteValueSafe(player);
            nm.CustomMessagingManager.SendNamedMessage(
                MSG_PLAYER, NetworkManager.ServerClientId, w, NetworkDelivery.Reliable);
        }
    }

    private void OnPlayerMsg(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out int player);
        if (_started) return;
        _playerOf[sender] = Mathf.Clamp(player, 0, 1);
    }

    private void ApplyLocalPlayer(int player)
    {
        _myPlayer = player;
        var pc = UnityEngine.Object.FindFirstObjectByType<PlayerCommander>();
        if (pc != null) pc.SetPlayer(player);
    }

    // --- host: start / load / resync — all the same distribution path ----------

    private void HostStartGame()
    {
        if (_started) return;
        var factory = UnitFactory.Instance;
        if (factory == null || !factory.Ready)
        {
            Debug.LogError("[Lockstep] no ready UnitFactory in scene — can't start.");
            return;
        }
        // The host's world is built by MapBootstrap (placements run at Start, long
        // before a lobby "Start Game" click). If it hasn't finished for some reason
        // — e.g. it was misconfigured to skip — fail loudly rather than capture and
        // distribute an empty map to every client.
        var bootstrap = FindFirstObjectByType<MapBootstrap>();
        if (bootstrap != null && !bootstrap.PlacementsDone)
        {
            Debug.LogError("[Lockstep] MapBootstrap hasn't finished placing the starting map yet — can't start.");
            return;
        }
        _started = true;
        HostDistribute(SimSnapshot.Capture(World.DefaultGameObjectInjectionWorld));
    }

    private void HostLoadGame()
    {
        var data = SimSnapshot.LoadFile();
        if (data == null) { Debug.LogWarning($"[Lockstep] no save file at {SimSnapshot.DefaultSavePath}"); return; }
        _started = true;
        HostDistribute(data);
    }

    // Called by the host when a client's reported checksum disagrees with the
    // host's own hash at the same tick. Public so debug tooling can force one.
    public void TriggerResync()
    {
        if (!NetworkManager.Singleton.IsServer || _syncing) return;
        HostDistribute(SimSnapshot.Capture(World.DefaultGameObjectInjectionWorld));
    }

    private void HostDistribute(byte[] data)
    {
        var nm = NetworkManager.Singleton;
        _epoch++;
        _syncing = true;
        _syncFailed = false;
        IsPaused = true;
        _reports.Clear();

        _participants = new List<ulong>(nm.ConnectedClientsIds);
        _participants.Sort();
        foreach (var cid in _participants)
            if (!_playerOf.ContainsKey(cid))
                _playerOf[cid] = cid == nm.LocalClientId ? 0 : 1;

        // The host restores its OWN world from the blob first: that produces the
        // canonical chunk layout (see SimSnapshot.cs) and the reference hash
        // every client ack is compared against.
        var world = World.DefaultGameObjectInjectionWorld;
        if (!SimSnapshot.Restore(world, data, out _syncTick, out _hostHash))
        {
            Debug.LogError("[Lockstep] host failed to restore its own snapshot — sync aborted.");
            _syncing = false;
            IsPaused = false;
            return;
        }
        ApplyLocalPlayer(_playerOf[nm.LocalClientId]);

        _pendingAcks.Clear();
        foreach (var cid in _participants)
        {
            if (cid == nm.LocalClientId) continue;
            _pendingAcks.Add(cid);
            SendSnapshot(cid, data);
        }

        // Solo host (no clients): nothing to wait for.
        if (_pendingAcks.Count == 0) FinishSync();
    }

    private void SendSnapshot(ulong clientId, byte[] data)
    {
        var nm = NetworkManager.Singleton;
        int chunkCount = (data.Length + SnapshotChunkBytes - 1) / SnapshotChunkBytes;

        using (var w = new FastBufferWriter(256 + _playerOf.Count * 16, Allocator.Temp))
        {
            w.WriteValueSafe(_epoch);
            w.WriteValueSafe(_syncTick);
            w.WriteValueSafe(LockstepRateManager.HaltAtTick);   // networked decision, as in Phase 3
            w.WriteValueSafe(data.Length);
            w.WriteValueSafe(chunkCount);
            w.WriteValueSafe(_playerOf.Count);
            foreach (var kv in _playerOf)
            {
                w.WriteValueSafe(kv.Key);
                w.WriteValueSafe(kv.Value);
            }
            nm.CustomMessagingManager.SendNamedMessage(
                MSG_SNAP_B, clientId, w, NetworkDelivery.ReliableFragmentedSequenced);
        }

        for (int i = 0; i < chunkCount; i++)
        {
            int off = i * SnapshotChunkBytes;
            int len = Math.Min(SnapshotChunkBytes, data.Length - off);
            using var w = new FastBufferWriter(len + 64, Allocator.Temp);
            w.WriteValueSafe(_epoch);
            w.WriteValueSafe(i);
            w.WriteValueSafe(len);
            w.WriteBytesSafe(data, len, off);
            nm.CustomMessagingManager.SendNamedMessage(
                MSG_SNAP_C, clientId, w, NetworkDelivery.ReliableFragmentedSequenced);
        }
    }

    // --- client: receive snapshot, restore, ack ---------------------------------

    private void OnSnapBegin(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out uint epoch);
        reader.ReadValueSafe(out uint tick);
        reader.ReadValueSafe(out uint haltAtTick);
        reader.ReadValueSafe(out int totalLen);
        reader.ReadValueSafe(out int chunkCount);
        reader.ReadValueSafe(out int playerCount);

        // Adopt the new epoch immediately: everything still in flight from the
        // old one (turns, our own queued submissions) belongs to a dead timeline.
        _epoch = epoch;
        _snapEpoch = epoch;
        _snapTick = tick;
        _started = true;
        IsPaused = true;
        LockstepRateManager.HaltAtTick = haltAtTick;

        var nm = NetworkManager.Singleton;
        for (int i = 0; i < playerCount; i++)
        {
            reader.ReadValueSafe(out ulong cid);
            reader.ReadValueSafe(out int player);
            if (cid == nm.LocalClientId) ApplyLocalPlayer(player);
        }

        _snapData = new byte[totalLen];
        _snapChunks = chunkCount;
        _snapGot = 0;
    }

    private void OnSnapChunk(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out uint epoch);
        if (epoch != _snapEpoch || _snapData == null) return;   // straggler from an aborted sync

        reader.ReadValueSafe(out int index);
        reader.ReadValueSafe(out int len);
        var bytes = new byte[len];
        reader.ReadBytesSafe(ref bytes, len);
        Buffer.BlockCopy(bytes, 0, _snapData, index * SnapshotChunkBytes, len);

        _snapGot++;
        if (_snapGot < _snapChunks) return;

        var data = _snapData;
        _snapData = null;
        bool ok = SimSnapshot.Restore(World.DefaultGameObjectInjectionWorld, data, out uint tick, out uint hash);

        using var w = new FastBufferWriter(16, Allocator.Temp);
        w.WriteValueSafe(_snapEpoch);
        w.WriteValueSafe(ok ? (byte)1 : (byte)0);
        w.WriteValueSafe(hash);
        NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
            MSG_ACK, NetworkManager.ServerClientId, w, NetworkDelivery.Reliable);
        // Stay paused; MSG_RESUME arrives once every peer's hash is verified.
    }

    private void OnAck(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out uint epoch);
        if (epoch != _epoch || !_syncing) return;
        reader.ReadValueSafe(out byte ok);
        reader.ReadValueSafe(out uint hash);

        if (ok == 0 || hash != _hostHash)
        {
            // Identical blob, different state: the snapshot restore itself is
            // nondeterministic (or structurally failed). Resending the same blob
            // would loop forever — this is a bug to root-cause, so stay paused
            // and say so.
            Debug.LogError($"[Lockstep] SYNC FAILED: client {sender} restored to hash {hash:X8}, " +
                           $"host has {_hostHash:X8} (ok={ok}). Game stays paused — root-cause the restore.");
            _syncFailed = true;
            return;
        }

        _pendingAcks.Remove(sender);
        if (_pendingAcks.Count == 0 && !_syncFailed) FinishSync();
    }

    private void FinishSync()
    {
        var nm = NetworkManager.Singleton;
        foreach (var cid in _participants)
        {
            if (cid == nm.LocalClientId) continue;
            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(_epoch);
            w.WriteValueSafe(_syncTick);
            nm.CustomMessagingManager.SendNamedMessage(
                MSG_RESUME, cid, w, NetworkDelivery.Reliable);
        }
        Debug.Log($"[Lockstep] sync complete — all peers verified at tick {_syncTick} " +
                  $"(hash {_hostHash:X8}, epoch {_epoch}); resuming.");
        ResumeAt(_syncTick);
    }

    private void OnResume(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out uint epoch);
        if (epoch != _epoch) return;
        reader.ReadValueSafe(out uint tick);
        ResumeAt(tick);
    }

    // Restart the lockstep pipeline from a freshly restored tick. Turns and
    // inbox entries from before the restore reference a dead timeline; clearing
    // them (plus the epoch stamp on all traffic) guarantees nothing stale leaks
    // into the new one.
    private void ResumeAt(uint tick)
    {
        _execTick = tick + 1;
        _sentUpTo = tick;
        _turns.Clear();
        _inbox.Clear();
        IsRunning = true;
        IsPaused = false;
        _syncing = false;
    }

    // --- per-frame input pump -----------------------------------------------

    private void Update()
    {
        var nm = NetworkManager.Singleton;

        // Desync watchdog (host, while running normally): compare each client's
        // latest reported checksum against our own hash AT THAT TICK.
        if (nm != null && nm.IsServer && IsRunning && !_syncing) CheckReports();

        if (!IsRunning || IsPaused) return;

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

    private void CheckReports()
    {
        foreach (var kv in _reports)
        {
            if (kv.Value.tick == 0) continue;                                  // nothing executed yet
            if (!ChecksumHistory.TryGet(kv.Value.tick, out uint mine)) continue;   // we haven't run that tick / out of window
            if (mine != kv.Value.hash)
            {
                Debug.LogError($"[Lockstep] DESYNC: client {kv.Key} at tick {kv.Value.tick} has hash " +
                               $"{kv.Value.hash:X8}, host has {mine:X8} — redistributing host state.");
                TriggerResync();
                return;
            }
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
            w.WriteValueSafe(_epoch);
            w.WriteValueSafe(ChecksumHistory.LatestTick);    // piggybacked desync report:
            w.WriteValueSafe(ChecksumHistory.LatestValue);   // "my state at tick T hashed to H"
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
        reader.ReadValueSafe(out uint epoch);
        if (epoch != _epoch) return;   // straggler from before a resync
        reader.ReadValueSafe(out uint rTick);
        reader.ReadValueSafe(out uint rHash);
        if (rTick > 0) _reports[sender] = (rTick, rHash);

        reader.ReadValueSafe(out uint tick);
        reader.ReadValueSafe(out int count);
        var list = new List<SimCommand>(count);
        for (int i = 0; i < count; i++) { reader.ReadValueSafe(out SimCommand c); list.Add(c); }

        // Player authority: the host stamps PlayerId from the sender's assigned
        // player. Whatever the client claims, its commands act as its lobby player.
        int player = _playerOf.TryGetValue(sender, out var t) ? t : 0;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            c.PlayerId = player;
            list[i] = c;
        }

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
            w.WriteValueSafe(_epoch);
            w.WriteValueSafe(tick);
            w.WriteValueSafe(combined.Count);
            for (int i = 0; i < combined.Count; i++) w.WriteValueSafe(combined[i]);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                MSG_TURN, cid, w, NetworkDelivery.ReliableFragmentedSequenced);
        }
    }

    // --- client: receive turns ------------------------------------------------

    private void OnTurnMsg(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out uint epoch);
        if (epoch != _epoch) return;   // turn from a dead timeline
        reader.ReadValueSafe(out uint tick);
        reader.ReadValueSafe(out int count);
        var list = new List<SimCommand>(count);
        for (int i = 0; i < count; i++) { reader.ReadValueSafe(out SimCommand c); list.Add(c); }
        _turns[tick] = list;
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
        _playerOf.Remove(clientId);
        _reports.Remove(clientId);

        // If we were waiting on this client's restore ack, stop waiting.
        if (_syncing && _pendingAcks.Remove(clientId) && _pendingAcks.Count == 0 && !_syncFailed)
            FinishSync();

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
        GUILayout.BeginArea(new Rect(10, 10, 250, 280), GUI.skin.box);
        if (nm == null)
        {
            GUILayout.Label("No NetworkManager in scene");
        }
        else if (!nm.IsClient && !nm.IsServer)
        {
            if (GUILayout.Button("Host")) StartHost();
            if (GUILayout.Button("Client")) StartClient();
        }
        else if (!_started)
        {
            GUILayout.Label(nm.IsServer ? "ROLE: HOST (lobby)" : "ROLE: CLIENT (lobby)");
            GUILayout.Label($"connected: {nm.ConnectedClientsIds.Count}");
            GUILayout.Label($"my player: {(_myPlayer < 0 ? "-" : (_myPlayer + 1).ToString())}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Play Player 1")) ChoosePlayer(0);
            if (GUILayout.Button("Play Player 2")) ChoosePlayer(1);
            GUILayout.EndHorizontal();

            if (nm.IsServer)
            {
                foreach (var kv in _playerOf)
                    GUILayout.Label($"  peer {kv.Key}: player {kv.Value + 1}");
                if (GUILayout.Button("Start Game")) HostStartGame();
                if (File.Exists(SimSnapshot.DefaultSavePath) && GUILayout.Button("Load Save"))
                    HostLoadGame();
            }
            else
            {
                GUILayout.Label("waiting for host to start...");
            }
        }
        else
        {
            GUILayout.Label(nm.IsServer ? "ROLE: HOST" : "ROLE: CLIENT");
            GUILayout.Label($"player: {_myPlayer + 1}   epoch: {_epoch}");
            GUILayout.Label($"execTick: {_execTick}   running: {IsRunning}");
            if (IsPaused) GUILayout.Label(_syncing ? "PAUSED — syncing..." : "PAUSED");
            if (_syncFailed) GUILayout.Label("SYNC FAILED — check log");
            if (nm.IsServer && IsRunning && !_syncing && GUILayout.Button("Save Game"))
                SimSnapshot.SaveToFile(World.DefaultGameObjectInjectionWorld);
        }
        GUILayout.EndArea();
    }
}
