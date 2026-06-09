using System.Collections.Generic;
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// ===========================================================================
// Command pipeline — the single channel all command & control flows through.
//
//   input / AI / network  ->  CommandController  ->  pending command buffer
//                                                 ->  CommandApplySystem (per tick)
//
// A command carries an EXECUTION tick (issue tick + InputDelay) and the set of
// units it targets (by StableId). The same command, applied at the same tick on
// every client/replay, produces the same sim — which is the whole point.
//
// Record mode logs every issued command to a file. Playback mode ignores live
// input and feeds the recorded commands back at their original ticks. Because
// both paths go through the identical apply step, a correct deterministic sim
// makes a recording replay bit-for-bit.
// ===========================================================================

public enum CommandKind : byte { None = 0, Move = 1, AttackMove = 2, Stop = 3, AttackTarget = 4 }

// One order. Blittable, so it serializes trivially and is safe inside jobs.
public struct SimCommand : IBufferElementData
{
    public uint                  Tick;            // execution tick
    public int                   PlayerId;
    public CommandKind           Kind;
    public float2                TargetPos;       // Move / AttackMove destination
    public int                   TargetStableId;  // AttackTarget victim
    public FixedList512Bytes<int> Units;          // affected units (StableIds); up to ~127
}

// Marks the entity that owns the pending-command buffer.
public struct CommandQueueTag : IComponentData { }


// -------------------------------------------------------------------------
// Ingest: moves commands from the managed CommandController into the ECS pending
// buffer. Managed (not Burst) because it touches the MonoBehaviour. Runs after
// the clock, before apply. Live/Record drains the outbox; Playback loads the
// whole recorded stream once (each command carries its own execution tick).
// -------------------------------------------------------------------------
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SimClockSystem))]
[UpdateBefore(typeof(CommandApplySystem))]
public partial class CommandIngestSystem : SystemBase
{
    private bool _playbackLoaded;

    protected override void OnUpdate()
    {
        var cc = CommandController.Instance;
        if (cc == null) return;
        if (!SystemAPI.HasSingleton<CommandQueueTag>()) return;

        var qe = SystemAPI.GetSingletonEntity<CommandQueueTag>();
        var buf = EntityManager.GetBuffer<SimCommand>(qe);

        if (cc.mode == CommandController.LockstepMode.Playback)
        {
            if (!_playbackLoaded)
            {
                var rec = cc.Recorded;
                for (int i = 0; i < rec.Count; i++) buf.Add(rec[i]);
                _playbackLoaded = true;
            }
        }
        else
        {
            while (cc.Outbox.Count > 0) buf.Add(cc.Outbox.Dequeue());
        }
    }
}

// -------------------------------------------------------------------------
// Apply: each tick, fire pending commands whose execution tick == now, then drop
// consumed ones. Resolves unit StableIds to entities via the registry and writes
// MoveTarget / AttackOrder. Runs before BehaviorSystem so orders are visible the
// same tick. Burst-compiled and deterministic.
// -------------------------------------------------------------------------
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(StableIdRegistrySystem))]
[UpdateBefore(typeof(BehaviorSystem))]
public partial struct CommandApplySystem : ISystem
{
    private ComponentLookup<MoveTarget>  _moveLk;
    private ComponentLookup<AttackOrder> _atkLk;

    public void OnCreate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<CommandQueueTag>())
        {
            var e = state.EntityManager.CreateEntity(typeof(CommandQueueTag));
            state.EntityManager.AddBuffer<SimCommand>(e);
        }
        _moveLk = state.GetComponentLookup<MoveTarget>(false);
        _atkLk  = state.GetComponentLookup<AttackOrder>(false);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<SimClock>() || !SystemAPI.HasSingleton<StableIdRegistry>()) return;

        uint tick = SystemAPI.GetSingleton<SimClock>().Tick;
        var map = SystemAPI.GetSingleton<StableIdRegistry>().Map;
        var qe  = SystemAPI.GetSingletonEntity<CommandQueueTag>();
        var buf = SystemAPI.GetBuffer<SimCommand>(qe);

        _moveLk.Update(ref state);
        _atkLk.Update(ref state);

        for (int i = 0; i < buf.Length; i++)
        {
            SimCommand c = buf[i];
            if (c.Tick != tick) continue;

            Entity atkTarget = Entity.Null;
            if (c.Kind == CommandKind.AttackTarget) map.TryGetValue(c.TargetStableId, out atkTarget);

            for (int u = 0; u < c.Units.Length; u++)
            {
                if (!map.TryGetValue(c.Units[u], out Entity e)) continue;
                if (!_moveLk.HasComponent(e)) continue;

                MoveTarget mv = _moveLk[e];
                AttackOrder ao = _atkLk[e];
                switch (c.Kind)
                {
                    case CommandKind.Move:
                        mv.Value = c.TargetPos; mv.HasTarget = true; mv.AttackMove = false; ao.Has = false; break;
                    case CommandKind.AttackMove:
                        mv.Value = c.TargetPos; mv.HasTarget = true; mv.AttackMove = true;  ao.Has = false; break;
                    case CommandKind.Stop:
                        mv.HasTarget = false; ao.Has = false; break;
                    case CommandKind.AttackTarget:
                        mv.HasTarget = false; ao.Target = atkTarget; ao.Has = atkTarget != Entity.Null; break;
                }
                _moveLk[e] = mv;
                _atkLk[e]  = ao;
            }
        }

        // Drop consumed/expired commands (stable order preserved for any future ties).
        for (int i = buf.Length - 1; i >= 0; i--)
            if (buf[i].Tick <= tick) buf.RemoveAt(i);
    }
}
