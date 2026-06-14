using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// Lockstep core: a fixed simulation tick, a fixed-timestep driver, and a
// per-tick state checksum. None of this networks anything yet — it makes the
// existing sim advance in discrete, reproducible ticks so we can test whether
// FloatMode.Deterministic actually gives us bit-identical results.
// ===========================================================================

public static class LockstepConfig
{
    public const int   TickRate        = 30;          // simulation ticks per second
    public const float FixedDt         = 1f / TickRate;
    public const int   InputDelayTicks = 2;           // commands execute this many ticks after issue
}

// The current simulation tick. Incremented once, first thing, each fixed step.
public struct SimClock : IComponentData { public uint Tick; }

// Rolling, order-independent hash of the simulation state at a given tick.
public struct SimChecksum : IComponentData { public uint Tick; public uint Value; }

// THE per-unit state hash. One formula, three callers: SimChecksumSystem (live,
// per tick, Burst job), SimSnapshot.ComputeStateHash (managed, after a restore),
// and the snapshot self-verify. Keeping it in one place is what lets a freshly
// restored world be VERIFIED against the live checksum — if the formulas drifted
// apart, resync verification would report false desyncs (or worse, miss real
// ones). navCtx (surface context: a unit on a wall-top vs the ground) is folded
// in so a roof/ground divergence surfaces in the hash like any other state.
[BurstCompile]
public static class LockstepHash
{
    public static uint Unit(float3 pos, float hp, float2 vel, int team, int stableId, byte navCtx)
    {
        uint a = math.hash(new uint4(
            math.asuint(pos.x),
            math.asuint(pos.z),
            math.asuint(hp),
            math.asuint(vel.x)));
        uint b = math.hash(new uint4(
            math.asuint(vel.y),
            (uint)team,
            (uint)stableId,
            navCtx));          // surface context — a roof/ground divergence shows here
        return a ^ b;
    }
}

// Per-tick checksum history (managed, ring buffer). The desync detector needs
// the hash of EVERY executed tick, not just the latest — peers run at slightly
// different ticks, so the host compares a client's report against its own hash
// AT THAT TICK. Sampling the SimChecksum singleton once per frame would skip
// ticks whenever the sim steps more than once per frame; ChecksumHistorySystem
// records every step instead. Cleared on snapshot restore: pre-restore hashes
// belong to a dead timeline.
public static class ChecksumHistory
{
    public const int Capacity = 1024;   // ~34 s at 30 ticks/s

    private static readonly uint[] _ticks  = new uint[Capacity];
    private static readonly uint[] _values = new uint[Capacity];

    public static uint LatestTick  { get; private set; }   // 0 = nothing recorded yet
    public static uint LatestValue { get; private set; }

    public static void Record(uint tick, uint value)
    {
        int i = (int)(tick % Capacity);
        _ticks[i]  = tick;
        _values[i] = value;
        LatestTick  = tick;
        LatestValue = value;
    }

    // True iff this exact tick is still in the window (ticks start at 1, so a
    // zeroed slot can never false-positive).
    public static bool TryGet(uint tick, out uint value)
    {
        int i = (int)(tick % Capacity);
        if (tick != 0 && _ticks[i] == tick) { value = _values[i]; return true; }
        value = 0;
        return false;
    }

    public static void Clear()
    {
        Array.Clear(_ticks, 0, Capacity);
        Array.Clear(_values, 0, Capacity);
        LatestTick = 0;
        LatestValue = 0;
    }
}

// Custom rate manager: advances the sim at a fixed real-time rate using an
// accumulator, regardless of frame rate.
//
// Single-player / Phase 1-2 (no LockstepNet present):
//   Accumulates real elapsed time and fires up to MaxCatchupSteps ticks per frame
//   when behind, giving correct ~30 Hz simulation speed at any frame rate.
//
// Networked (LockstepNet present):
//   Same accumulator, PLUS requires the next network turn to be ready. The sim is
//   capped to real-time pace (so a fast-network game doesn't run faster than 30 Hz)
//   and stalls when the network can't keep up.
//
// Why the old toggle was wrong: it ran one tick per frame, so 60 fps = 60 ticks/s =
// 2× real-time speed. The accumulator is what Time.deltaTime was implicitly providing
// for variable-dt integration — we just make it explicit now.
public class LockstepRateManager : Unity.Entities.IRateManager
{
    public const int MaxCatchupSteps = 8;

    // When non-zero, the sim freezes once this tick completes. Used by
    // SimResultDumper.autoDumpAtTick so record and playback runs dump at the
    // EXACT same tick (the sim can advance several ticks per frame, so sampling
    // from Update without halting could land on different ticks per run).
    public static uint HaltAtTick;

    private readonly float _dt;
    private double _elapsed;
    private bool _pushed;
    private double _accumulator;
    private int _stepsThisFrame;
    private int _lastFrame = -1;

    public LockstepRateManager(float dt) { _dt = dt; }
    public float Timestep { get => _dt; set { } }

    public bool ShouldGroupUpdate(ComponentSystemGroup group)
    {
        if (_pushed) { group.World.PopTime(); _pushed = false; }

        // Accumulate real time once per frame (ShouldGroupUpdate is called in a
        // loop until we return false, so we'd double-count without the frame guard).
        if (UnityEngine.Time.frameCount != _lastFrame)
        {
            _accumulator += UnityEngine.Time.deltaTime;
            _lastFrame = UnityEngine.Time.frameCount;
            _stepsThisFrame = 0;
        }

        // Lobby / resync gate: networked but not started, OR paused for a
        // snapshot sync -> sim frozen, AND the bank is cleared. Lobby/pause
        // wall-time is not owed simulation time; without this, the accumulator
        // grows during Host/Connect/Start-Game (or during a resync) and the
        // match opens with a multi-second fast-forward at the catch-up cap
        // (observed as a sustained ~45-55 ticks/s after Start until the bank
        // drained).
        var net = LockstepNet.Instance;
        if (net != null && (!net.IsRunning || net.IsPaused))
        {
            _accumulator = 0;
            return false;
        }

        // Hard per-frame cap — prevents spiral-of-death on a slow machine.
        if (_stepsThisFrame >= MaxCatchupSteps)
        {
            _accumulator = System.Math.Min(_accumulator, _dt);   // bleed off excess so next frame is normal
            return false;
        }

        // Halt gate: state frozen exactly at HaltAtTick (tick-exact dumps).
        if (HaltAtTick > 0 && SimClockSystem.LastCompletedTick >= HaltAtTick) return false;

        // Real-time gate: not enough wall-clock time has passed for the next tick.
        if (_accumulator < _dt) return false;

        // Network gate: also need the next confirmed turn. Unlike the lobby,
        // a mid-game turn stall KEEPS the bank — when the turn arrives we catch
        // up (bounded by MaxCatchupSteps) to stay at real-time pace.
        if (net != null && !net.TryBeginNextTurn()) return false;

        _accumulator -= _dt;
        _elapsed += _dt;
        group.World.PushTime(new Unity.Core.TimeData(_elapsed, _dt));
        _pushed = true;
        _stepsThisFrame++;
        return true;
    }
}

// Creates the SimClock singleton and ticks it. Runs first in the sim group so
// every other system sees the new tick number. Not Burst-compiled: it also
// publishes the tick to a managed static (read by the rate manager's halt gate
// and by SimResultDumper) — trivial work either way.
[UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
public partial struct SimClockSystem : ISystem
{
    // Last completed tick, readable from managed code without an EntityQuery.
    // SimSnapshot.Restore writes this directly when it rewinds/forwards the clock.
    public static uint LastCompletedTick;

    public void OnCreate(ref SystemState state)
    {
        LastCompletedTick = 0;
        if (!SystemAPI.HasSingleton<SimClock>())
            state.EntityManager.CreateEntity(typeof(SimClock));
    }

    public void OnUpdate(ref SystemState state)
    {
        var clock = SystemAPI.GetSingletonRW<SimClock>();
        clock.ValueRW.Tick++;
        LastCompletedTick = clock.ValueRO.Tick;
    }
}

// Computes an order-independent checksum of unit state every tick and stores it
// in the SimChecksum singleton. Order-independent (a commutative sum of per-unit
// hashes) so it does NOT depend on ECS iteration/job order — only on the actual
// state. Identity (StableId) is folded in, so two units swapping places changes
// the hash. Floats are hashed by their exact bit pattern, so ANY drift shows up.
//
// Runs as a parallel job rather than a main-thread foreach: the old foreach read
// LocalTransform/Velocity/Health on the main thread at OrderLast, which forced a
// sync of all in-flight tick jobs (the profiler then blamed this system for that
// wait). Each worker accumulates a per-thread partial; a tiny finalize job sums
// the partials and writes the singleton. Integer add is associative/commutative
// mod 2^32, so the combined result is bit-identical to the serial sum.
[UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
public partial struct SimChecksumSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<SimChecksum>())
            state.EntityManager.CreateEntity(typeof(SimChecksum));
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        uint tick = SystemAPI.HasSingleton<SimClock>() ? SystemAPI.GetSingleton<SimClock>().Tick : 0u;

        // One slot per possible worker thread; zero-initialized, auto-freed.
        // Custom (rewindable) allocators require CollectionHelper, not new NativeArray.
        var partials = CollectionHelper.CreateNativeArray<uint>(
            JobsUtility.ThreadIndexCount, state.WorldUpdateAllocator, NativeArrayOptions.ClearMemory);

        state.Dependency = new ChecksumJob { Partials = partials }.ScheduleParallel(state.Dependency);

        state.Dependency = new FinalizeJob
        {
            Partials = partials,
            Tick = tick,
            ChecksumEntity = SystemAPI.GetSingletonEntity<SimChecksum>(),
            ChecksumLookup = SystemAPI.GetComponentLookup<SimChecksum>(false),
        }.Schedule(state.Dependency);
    }

    [BurstCompile]
    private partial struct ChecksumJob : IJobEntity
    {
        // Each worker writes only its own slot (indexed by thread), so the
        // shared-array write is safe despite the parallel-for restriction.
        [NativeDisableParallelForRestriction] public NativeArray<uint> Partials;
        [NativeSetThreadIndex] private int _threadIndex;

        private void Execute(
            in LocalTransform xform,
            in Health health,
            in Velocity velocity,
            in Team team,
            in StableId stableId,
            in NavContext navCtx)
        {
            Partials[_threadIndex] += LockstepHash.Unit(
                xform.Position, health.Current, velocity.Value,
                team.Value, stableId.Value, navCtx.Value);
        }
    }

    [BurstCompile]
    private struct FinalizeJob : IJob
    {
        [ReadOnly] public NativeArray<uint> Partials;
        public uint Tick;
        public Entity ChecksumEntity;
        public ComponentLookup<SimChecksum> ChecksumLookup;

        public void Execute()
        {
            uint sum = 0;
            for (int i = 0; i < Partials.Length; i++) sum += Partials[i];

            var cs = ChecksumLookup[ChecksumEntity];
            cs.Tick = Tick;
            cs.Value = sum;
            ChecksumLookup[ChecksumEntity] = cs;
        }
    }
}

// Records every executed tick's checksum into the managed ChecksumHistory ring.
// Managed (SystemBase, no Burst) because it writes a managed static; runs after
// SimChecksumSystem inside the same OrderLast band so the singleton is fresh.
// This runs once per TICK (not per frame), so multi-tick catch-up frames record
// every tick — the property the desync detector depends on.
[UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
[UpdateAfter(typeof(SimChecksumSystem))]
public partial class ChecksumHistorySystem : SystemBase
{
    protected override void OnUpdate()
    {
        if (!SystemAPI.HasSingleton<SimChecksum>()) return;

        // SimChecksumSystem writes SimChecksum from a scheduled FinalizeJob and
        // leaves it in flight (it's parallel so it doesn't force-sync the tick
        // pipeline). We read that value on the main thread to copy it into the
        // managed ring, so complete the writer first. CompleteDependency()
        // finishes only the jobs touching components THIS system reads
        // (SimChecksum) — a scoped sync at the tail of the sim group, not a
        // world-wide stall.
        CompleteDependency();

        var cs = SystemAPI.GetSingleton<SimChecksum>();
        if (cs.Tick > 0) ChecksumHistory.Record(cs.Tick, cs.Value);
    }
}
