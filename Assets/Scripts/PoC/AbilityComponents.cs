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
// NOTE: the behavior-flag entries were renamed in place (serialized indices
// preserved) when the flag set was rebuilt — RE-CHECK any AbilityDefinition
// assets that toggle behavior flags; the semantics shifted with the rename.
public enum ModTarget : byte
{
    Health, Speed, TurnSpeed, MeleeRange, AttackDamage, Armor, Shield,   // numeric stats
    // Behavior tuning (UnitTuning params). Aggression/Looseness/Separation kept
    // at their original indices so already-baked ability assets don't shift.
    Aggression,        // -> AttackNearbyRange  (how far it leaves formation to engage)
    Looseness,         // -> IdleSpacing        (formation spread at rest)
    Separation,        // -> SeparationStrength (how hard it pushes off neighbors)
    CombatSpacing,     // formation spread with enemies near
    AttackNearbyRange, // explicit alias of Aggression for clarity in authoring
    AvoidMeleeRange,   // kite back-off radius
    PursueDistance,    // how far it will chase from an objective
    CohesionRadius,    // distance from friendly center before it regroups
    RetreatHealthPct,  // retreats below this Current/Max (0 = never retreat)
    ReEngageHealthPct, // stops retreating above this Current/Max
}

public enum ModMode : byte { Instant, PerSecond }
public enum CapMode : byte { None, Min, Max }       // clamp result to a floor / ceiling
public enum CapRef  : byte { Absolute, Base }       // cap value is absolute, or relative to the base stat
public enum ShapeType : byte { Circle, Line }
public enum AnchorType : byte { Hero, WorldPoint }
public enum ApplyMode : byte { CastOnce, PersistentArea }
public enum AffectFilter : byte { Enemies, Allies, All }

// The unmodified authored stats, kept so StatResolve can recompute live values
// each frame as base + active offsets (so all other systems read live components
// unchanged). Health is NOT here — it's a resource mutated directly.
public struct BaseStats : IComponentData
{
    public float Speed, TurnSpeed, MeleeRange, AttackDamage, Armor, Shield;
    // Behavior tuning bases — so ability offsets to these revert cleanly to the
    // unit's authored values (StatResolveSystem recomputes live UnitTuning each frame).
    public float AttackNearbyRange, IdleSpacing, SeparationStrength, CombatSpacing,
                 AvoidMeleeRange, PursueDistance, CohesionRadius,
                 RetreatHealthPct, ReEngageHealthPct;
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
    public int Player;              // owning player id (ally/enemy resolution)
    public AffectFilter Affects;
    public ShapeType Shape;
    public float Radius, Width, Length;
    public float2 Center, Dir;
    public AnchorType Anchor;
    public Entity AnchorEntity;     // for Hero-anchored fields (follows it) and spawn-bound fields
    public byte BoundToSpawn;       // 1 = banner/totem: follows AnchorEntity, dies when it dies,
                                    //     and kills it when the field's lifetime expires
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
}

// --- per-unit ability state (lockstep-deterministic) -----------------------

// Caster resource. Regenerated by ManaRegenSystem; consumed at commit by
// CommandApplySystem (the cast fails — nothing consumed — if Current < cost).
public struct Mana : IComponentData
{
    public float Current;
    public float Max;
    public float Regen;     // per second
}

// An armed cast, set at COMMIT (CommandApplySystem) and resolved at FIRE
// (AbilityCastSystem) when SimClock.Tick reaches FireTick. Lives permanently on
// every unit (Active flag) so casting never causes a structural change. One
// pending cast per caster: committing while one is armed is rejected.
public struct PendingCast : IComponentData
{
    public byte   Active;
    public byte   Slot;
    public int    AbilityId;
    public uint   FireTick;
    public uint   CastTick;    // tick at which the cast was committed (for spend-class ordering)
    public float2 TargetPos;   // clicked point at commit (WorldPoint geometry)
}

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
