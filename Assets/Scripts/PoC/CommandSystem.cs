using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;
using Unity.Transforms;

// ===========================================================================
// Command pipeline — data + ECS side. (The issuing API lives in Commander.cs.)
//
//   PlayerCommander / AICommander / network  ->  Commander stream (static)
//        ->  CommandIngestSystem  ->  pending buffer  ->  CommandApplySystem
//
// A command carries an EXECUTION tick (issue tick + InputDelay) and the units it
// targets (by StableId). The same command, applied at the same tick on every
// client/replay, produces the same sim.
// ===========================================================================

public enum CommandKind : byte { None = 0, Move = 1, AttackMove = 2, Stop = 3, AttackTarget = 4, Ability = 5 }

// One order. The struct is fully unmanaged/blittable (FixedList included), so it
// uses NGO's INetworkSerializeByMemcpy contract: a marker interface (no methods)
// that unlocks the public WriteValueSafe/ReadValueSafe ForStructs overloads —
// NGO memcpys the whole struct. No hand-written serializer to drift out of sync.
// (Wire cost: full 512-byte Units capacity is sent even for small selections —
// trivial at RTS command rates; optimize with manual packing only if it matters.)
public struct SimCommand : IBufferElementData, INetworkSerializeByMemcpy
{
    public uint                  Tick;            // execution tick
    public int                   PlayerId;
    public CommandKind           Kind;
    public float2                TargetPos;       // Move/AttackMove destination, or Ability cast point
    public int                   TargetStableId;  // AttackTarget victim
    public byte                  AbilitySlot;     // Ability: which slot (0..3); caster = Units[0]
    public FixedList512Bytes<int> Units;          // affected units (StableIds); up to ~125
}

// Marks the entity that owns the pending-command buffer (and the cast-event buffer).
public struct CommandQueueTag : IComponentData { }

// Deterministic sequence for AbilityField ids (replaces entity-index FieldIds,
// which aren't guaranteed identical across clients).
public struct FieldIdSeq : IComponentData { public int Next; }

// View-layer event: "ability X was cast at P this tick". Drained by
// AbilityManager to spawn cast VFX. Pure output — the sim never reads it.
public struct AbilityCastEvent : IBufferElementData
{
    public int    AbilityId;
    public float2 Pos;
}

// -------------------------------------------------------------------------
// Ingest: moves commands from the static Commander stream into the ECS pending
// buffer. Managed because it touches the stream. In Network mode, LockstepNet
// owns injection instead. Playback loads the whole recorded stream once (each
// command carries its own execution tick).
// -------------------------------------------------------------------------
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SimClockSystem))]
[UpdateBefore(typeof(CommandApplySystem))]
public partial class CommandIngestSystem : SystemBase
{
    private bool _playbackLoaded;

    protected override void OnUpdate()
    {
        if (Commander.Mode == Commander.LockstepMode.Network) return;  // LockstepNet injects turns directly
        if (!SystemAPI.HasSingleton<CommandQueueTag>()) return;

        var qe = SystemAPI.GetSingletonEntity<CommandQueueTag>();
        var buf = EntityManager.GetBuffer<SimCommand>(qe);

        if (Commander.Mode == Commander.LockstepMode.Playback)
        {
            if (!_playbackLoaded)
            {
                var rec = Commander.Recorded;
                for (int i = 0; i < rec.Count; i++) buf.Add(rec[i]);
                _playbackLoaded = true;
            }
        }
        else
        {
            while (Commander.Outbox.Count > 0) buf.Add(Commander.Outbox.Dequeue());
        }
    }
}

// -------------------------------------------------------------------------
// Apply: each tick, fire pending commands whose execution tick == now. Resolves
// StableIds via the registry and writes MoveTarget / AttackOrder; Ability
// commands spawn an AbilityField from the AbilityManager's baked specs, gated by
// the caster's tick-based cooldowns, and emit an AbilityCastEvent for VFX.
//
// NOT Burst-compiled: ability casts are structural changes and read the managed
// AbilityManager registry. This is a handful of commands per tick — determinism
// doesn't require Burst here (no float math beyond copying), and FloatMode only
// concerns Burst-compiled math anyway.
// -------------------------------------------------------------------------
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(StableIdRegistrySystem))]
[UpdateBefore(typeof(BehaviorSystem))]
public partial struct CommandApplySystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<CommandQueueTag>())
        {
            var e = state.EntityManager.CreateEntity(typeof(CommandQueueTag), typeof(FieldIdSeq));
            state.EntityManager.AddBuffer<SimCommand>(e);
            state.EntityManager.AddBuffer<AbilityCastEvent>(e);
            state.EntityManager.SetComponentData(e, new FieldIdSeq { Next = 1 });
        }
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.HasSingleton<SimClock>() || !SystemAPI.HasSingleton<StableIdRegistry>()) return;

        uint tick = SystemAPI.GetSingleton<SimClock>().Tick;
        var map = SystemAPI.GetSingleton<StableIdRegistry>().Map;
        var qe  = SystemAPI.GetSingletonEntity<CommandQueueTag>();
        var em  = state.EntityManager;

        // 1) Pull this tick's commands out of the buffer (and drop expired ones)
        //    BEFORE applying anything: ability casts are structural changes that
        //    would invalidate the buffer mid-iteration.
        var due = new NativeList<SimCommand>(8, Allocator.Temp);
        {
            var buf = em.GetBuffer<SimCommand>(qe);
            for (int i = 0; i < buf.Length; i++)
                if (buf[i].Tick == tick) due.Add(buf[i]);
            for (int i = buf.Length - 1; i >= 0; i--)
                if (buf[i].Tick <= tick) buf.RemoveAt(i);
        }

        for (int ci = 0; ci < due.Length; ci++)
        {
            SimCommand c = due[ci];

            if (c.Kind == CommandKind.Ability)
            {
                ApplyAbility(ref state, c, tick, map, qe);
                continue;
            }

            Entity atkTarget = Entity.Null;
            if (c.Kind == CommandKind.AttackTarget) map.TryGetValue(c.TargetStableId, out atkTarget);

            for (int u = 0; u < c.Units.Length; u++)
            {
                if (!map.TryGetValue(c.Units[u], out Entity e)) continue;
                if (!em.HasComponent<MoveTarget>(e) || !em.HasComponent<AttackOrder>(e)) continue;

                MoveTarget mv  = em.GetComponentData<MoveTarget>(e);
                AttackOrder ao = em.GetComponentData<AttackOrder>(e);
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
                em.SetComponentData(e, mv);
                em.SetComponentData(e, ao);
            }
        }

        due.Dispose();
    }

    // Spawns the AbilityField entity for an Ability command, exactly like the old
    // HeroController.TryCast — but on a deterministic tick, with tick-based
    // cooldowns, from the AbilityManager's baked spec.
    private void ApplyAbility(ref SystemState state, in SimCommand c, uint tick,
                              NativeParallelHashMap<int, Entity> map, Entity qe)
    {
        var em = state.EntityManager;
        var mgr = AbilityManager.Instance;
        if (mgr == null) return;
        if (c.Units.Length == 0) return;
        if (!map.TryGetValue(c.Units[0], out Entity caster)) return;        // caster died before execution
        if (!em.HasComponent<AbilitySlots>(caster) || !em.HasComponent<AbilityCooldowns>(caster)) return;

        int slot = c.AbilitySlot;
        if (slot < 0 || slot > 3) return;

        var slots = em.GetComponentData<AbilitySlots>(caster);
        int abilityId = slots.Ids[slot];
        if (abilityId < 0 || !mgr.TryGetSpec(abilityId, out var spec)) return;

        var cds = em.GetComponentData<AbilityCooldowns>(caster);
        if (cds.ReadyTick[slot] > tick) return;                             // still cooling down
        cds.ReadyTick[slot] = tick + spec.CooldownTicks;
        em.SetComponentData(caster, cds);

        // Anchor geometry, from sim state at the execution tick (deterministic).
        var xf = em.GetComponentData<LocalTransform>(caster);
        float2 casterPos = new float2(xf.Position.x, xf.Position.z);
        float3 fwd3 = math.forward(xf.Rotation);
        float2 casterFwd = math.normalizesafe(new float2(fwd3.x, fwd3.z), new float2(0f, 1f));

        float2 center, dir;
        if (spec.Anchor == AnchorType.Hero) { center = casterPos; dir = casterFwd; }
        else { center = c.TargetPos; dir = math.normalizesafe(c.TargetPos - casterPos, casterFwd); }

        int team = em.HasComponent<Team>(caster) ? em.GetComponentData<Team>(caster).Value : c.PlayerId;

        var seq = em.GetComponentData<FieldIdSeq>(qe);
        int fieldId = seq.Next++;
        em.SetComponentData(qe, seq);

        var fe = em.CreateEntity();
        em.AddComponentData(fe, new AbilityField
        {
            FieldId = fieldId,
            AbilityId = abilityId,
            Team = team,
            Affects = spec.Affects,
            Shape = spec.Shape,
            Radius = spec.Radius,
            Width = spec.Width,
            Length = spec.Length,
            Center = center,
            Dir = dir,
            Anchor = spec.Anchor,
            AnchorEntity = spec.Anchor == AnchorType.Hero ? caster : Entity.Null,
            Mode = spec.Mode,
            Lifetime = spec.Lifetime,
            RefreshWindow = 0.2f,
        });
        var fmods = em.AddBuffer<FieldModifier>(fe);
        var src = mgr.GetModifiers(abilityId);
        for (int i = 0; i < src.Length; i++) fmods.Add(src[i]);

        // View event (the field entity may die the same tick for CastOnce).
        em.GetBuffer<AbilityCastEvent>(qe).Add(new AbilityCastEvent { AbilityId = abilityId, Pos = center });
    }
}
