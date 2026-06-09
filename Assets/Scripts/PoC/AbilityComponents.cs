using Unity.Entities;
using Unity.Mathematics;

// ===========================================================================
// ABILITY / STAT-MODIFIER runtime data. Designers author AbilityDefinition
// assets (a list of modifiers + a shape); at cast a caster spawns an AbilityField
// entity carrying the shape and the modifier payloads. AbilityFieldSystem stamps
// those payloads onto units inside the shape as ActiveModifier buffer entries
// (recipient-side, stacking). ModifierTickSystem applies/ticks them; StatResolve
// recomputes the live stat components from BaseStats + active modifiers.
// ===========================================================================

// Every modifiable thing. Numeric ones come first, behavior bools after.
public enum ModTarget : byte
{
    Health, Speed, TurnSpeed, MeleeRange, AttackDamage, Armor, Shield,   // numeric
    FlagFormShieldWall, FlagStayBehindWall, FlagKiteEnemies,             // bool
    FlagAdvanceToTarget, FlagHoldWhenDefensive, FlagIdleSpread,
}

public enum ModMode : byte { Instant, PerSecond }
public enum CapMode : byte { None, Min, Max }       // clamp result to a floor / ceiling
public enum CapRef  : byte { Absolute, Base }       // cap value is absolute, or relative to the base stat
public enum ShapeType : byte { Circle, Line }
public enum AnchorType : byte { Hero, WorldPoint }
public enum ApplyMode : byte { CastOnce, PersistentArea }
public enum AffectFilter : byte { Enemies, Allies, All }

public static class AbilityUtil
{
    public static bool IsBool(ModTarget t) => t >= ModTarget.FlagFormShieldWall;

    public static uint FlagBit(ModTarget t) => t switch
    {
        ModTarget.FlagFormShieldWall   => (uint)BehaviorFlag.FormShieldWall,
        ModTarget.FlagStayBehindWall   => (uint)BehaviorFlag.StayBehindWall,
        ModTarget.FlagKiteEnemies      => (uint)BehaviorFlag.KiteEnemies,
        ModTarget.FlagAdvanceToTarget  => (uint)BehaviorFlag.AdvanceToTarget,
        ModTarget.FlagHoldWhenDefensive=> (uint)BehaviorFlag.HoldWhenDefensive,
        ModTarget.FlagIdleSpread       => (uint)BehaviorFlag.IdleSpread,
        _ => 0u,
    };
}

// The unmodified authored stats, kept so StatResolve can recompute live values
// each frame as base + active offsets (so all other systems read live components
// unchanged). Health is NOT here — it's a resource mutated directly.
public struct BaseStats : IComponentData
{
    public float Speed, TurnSpeed, MeleeRange, AttackDamage, Armor, Shield;
}

// One active effect on a unit (stacking buffer). Identity (Source, Slot) lets a
// persistent field refresh its own entry instead of duplicating it.
public struct ActiveModifier : IBufferElementData
{
    public int Source;        // field id that applied it (deterministic FieldIdSeq)
    public int AbilityId;     // which ability it came from (attached-VFX lookup; -1 = none)
    public int Slot;          // which modifier within that field
    public ModTarget Target;
    public float Delta;
    public ModMode Mode;
    public byte Revert;       // 1 = temporary offset (reverts), 0 = permanent value change
    public byte BoolValue;    // for bool targets: set flag on (1) / off (0)
    public CapMode CapMode;
    public CapRef CapRef;
    public float CapValue;
    public float Remaining;   // seconds left
    public byte Applied;      // instant effects fire once
    public float Offset;      // current contribution for revert numerics
}

// A live area-of-effect in the world. Carries the shape; its modifier payloads
// live in a DynamicBuffer<FieldModifier> on the same entity.
public struct AbilityField : IComponentData
{
    public int FieldId;
    public int AbilityId;           // index into AbilityManager's registry (VFX lookup)
    public int Team;
    public AffectFilter Affects;
    public ShapeType Shape;
    public float Radius, Width, Length;
    public float2 Center, Dir;
    public AnchorType Anchor;
    public Entity AnchorEntity;     // for Hero-anchored fields (follows it)
    public ApplyMode Mode;
    public float Lifetime;          // seconds the field persists (PersistentArea)
    public float RefreshWindow;     // how long a stamped modifier survives after leaving
}

// One modifier payload carried by a field (copied from the AbilityDefinition).
public struct FieldModifier : IBufferElementData
{
    public ModTarget Target;
    public float Delta;
    public ModMode Mode;
    public byte Revert;
    public byte BoolValue;
    public CapMode CapMode;
    public CapRef CapRef;
    public float CapValue;
    public float Duration;
}

// --- per-unit ability state (lockstep-deterministic) -----------------------

// Which abilities this unit has, as AbilityManager registry ids (-1 = empty
// slot). Populated at spawn from UnitDefinition.abilities.
public struct AbilitySlots : IComponentData
{
    public int4 Ids;
}

// Tick-based cooldowns: slot is castable when SimClock.Tick >= ReadyTick[slot].
// Replaces the old Time.time-based cooldowns in HeroController — sim-state, so
// it's identical on every client and in every replay.
public struct AbilityCooldowns : IComponentData
{
    public uint4 ReadyTick;
}
