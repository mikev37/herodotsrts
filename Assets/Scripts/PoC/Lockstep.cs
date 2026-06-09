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


// Creates the SimClock singleton and ticks it. Runs first in the sim group so
// every other system sees the new tick number.
[UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
public partial struct SimClockSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<SimClock>())
            state.EntityManager.CreateEntity(typeof(SimClock));
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        SystemAPI.GetSingletonRW<SimClock>().ValueRW.Tick++;
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
