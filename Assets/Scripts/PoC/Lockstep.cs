using Unity.Burst;
using Unity.Entities;
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

        // Lobby gate: networked but not started -> sim frozen, AND the bank is
        // cleared. Lobby wall-time is not owed simulation time; without this,
        // the accumulator grows during Host/Connect/Start-Game and the match
        // opens with a multi-second fast-forward at the catch-up cap (observed
        // as a sustained ~45-55 ticks/s after Start until the bank drained).
        var net = LockstepNet.Instance;
        if (net != null && !net.IsRunning)
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
        uint sum = 0;
        foreach (var (xf, h, v, t, s) in
                 SystemAPI.Query<RefRO<LocalTransform>, RefRO<Health>, RefRO<Velocity>, RefRO<Team>, RefRO<StableId>>())
        {
            uint a = math.hash(new uint4(
                math.asuint(xf.ValueRO.Position.x),
                math.asuint(xf.ValueRO.Position.z),
                math.asuint(h.ValueRO.Current),
                math.asuint(v.ValueRO.Value.x)));
            uint b = math.hash(new uint3(
                math.asuint(v.ValueRO.Value.y),
                (uint)t.ValueRO.Value,
                (uint)s.ValueRO.Value));
            sum += a ^ b;   // '+' is commutative -> independent of iteration order
        }

        var cs = SystemAPI.GetSingletonRW<SimChecksum>();
        cs.ValueRW.Tick = tick;
        cs.ValueRW.Value = sum;
    }
}
