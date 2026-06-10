using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

// ---------------------------------------------------------------------------
// All component data for the PoC. In ECS, components are PURE DATA (no logic).
// Logic lives in systems. Empty "tag" components classify entities cheaply.
// ---------------------------------------------------------------------------

// Per-unit movement intent. Set by player orders OR by AI behaviors.
public struct MoveTarget : IComponentData
{
    public float2 Value;   // world-space XZ destination (we flatten 3D -> plane)
    public bool HasTarget;
    public bool AttackMove; // if true, engage enemies encountered en route; if false, ignore them
}

// Accumulated velocity for this frame, integrated by the steering system.
public struct Velocity : IComponentData
{
    public float2 Value;
}

public struct Speed : IComponentData
{
    public float Value;    // units / second
}

// Used by separation (body-blocking push) and neighbor queries.
public struct UnitRadius : IComponentData
{
    public float Value;
}

public struct Team : IComponentData
{
    public int Value;      // 0 = player, 1 = enemy, etc.
}

public struct Health : IComponentData
{
    public float Current;
    public float Max;
}

// Used for collision-impact damage and pushback (replaces rigidbody mass).
public struct Mass : IComponentData
{
    public float Value;
}

// Set by the slope system each frame: <1 uphill, >1 downhill. Steering reads
// it to scale locomotion. Because contact damage scales with actual velocity,
// the "downhill hits harder" buff falls out for free.
public struct GroundSpeedMultiplier : IComponentData
{
    public float Value;     // default 1
    public float Height;
}

// Tag: this entity is a unit (vs. hero, projectile, etc.).
public struct UnitTag : IComponentData { }

// ---------------------------------------------------------------------------
// Presentation bridge. The SIM owns this enum; the GameObject view layer reads
// it and drives the Animator. Sim never touches an Animator directly.
// ---------------------------------------------------------------------------
public enum AnimState : byte { Idle, Walk, Block, Attack, Die }

public struct UnitAnim : IComponentData
{
    public AnimState State;
}

// Per-frame combat signals written by the combat system, read by the anim
// system. Keeps "what is happening" (sim) separate from "what to play" (view).
public struct CombatStatus : IComponentData
{
    public bool InContactWithEnemy;
    public bool IsAttacking;      // behavior holds position and commits to attacking its target
    public bool IsBlocking;       // shield unit facing an enemy
    public bool IsFiring;         // ranged unit shooting at a target
}

// Marks a unit as dead: movement/combat stop, the view plays Die, then the
// DeathTimer expires and the entity is destroyed.
public struct Dead : IComponentData { }

public struct DeathTimer : IComponentData
{
    public float Seconds;        // counts down once Dead is added
}

// Combat profile carried by EVERY unit (melee sets IsRanged = false). Kept
// non-optional so the steering job can read it as a required parameter.
public struct Ranged : IComponentData
{
    public bool IsRanged;
    public float KiteRadius;   // distance at which ranged units start backing up
}

// Tag: currently selected by the player. Added/removed by the input bridge.
// Enableable on purpose: selection is toggled by live player input, and
// add/remove would be a STRUCTURAL change — re-chunking entities and changing
// job iteration order, which perturbs float summation order in neighbor loops
// and desyncs record/playback (and, later, lockstep peers with different local
// selections). Toggling the enabled bit moves nothing. Queries filter on the
// bit automatically; raw HasComponent does NOT (use IsComponentEnabled).
public struct Selected : IComponentData, IEnableableComponent { }
// ---------------------------------------------------------------------------
// Spatial hash singleton. One map per frame, shared by every system that
// needs neighbor lookups. THIS is the core of scaling to thousands of units:
// O(1) neighbor queries instead of O(n^2).
// ---------------------------------------------------------------------------
// THE canonical per-unit snapshot. Filled once per tick by SpatialHashSystem
// (into the hash) and copied into each unit's ContactList by the gather system.
// Every consumer reads the SAME data — no system re-derives its own version.
public struct UnitInfo : IBufferElementData
{
    public Entity Entity;
    public int    StableId;       // deterministic identity (tie-breaking, debugging)
    public int    Team;
    public float2 Position;
    public float  Height;         // terrain height under the unit
    public float2 Velocity;
    public float2 Facing;         // normalized XZ forward
    public float  Radius;         // body radius (physical contact)
    public float  Mass;
    public float  Health;
    public uint   Flags;          // BehaviorFlags
    public byte   IsAttacking;    // behavior committed to an attack this tick
    public Entity AttackTarget;   // who it is attacking (single-target strikes)
    public float  StrikeDamage;   // melee pulse this tick (0 except on the strike tick)
    public float  AttackRange;    // weapon reach (melee) / fire range (ranged)
    public float  StrikeArcDot;   // cos(arc/2) for cleave strikes
    public byte   Cleave;         // strike hits everyone in the arc, not just the target
}

// Per-unit perception, written ONLY by InformationGatherSystem, read by behavior
// and combat. One scan, one truth: there is exactly one definition of "my
// target" / "my wall buddy" in the whole sim.
public struct Perception : IComponentData
{
    public byte   HasTarget;
    public float  TargetDist;
    public float  TargetHeight;
    public byte   TargetLos;      // line of sight to the target (passability grid)

    public byte   HasWallAlly;    // nearest friendly shield-wall former (rungs 5/6)
    public float2 WallAllyPos;
    public float  WallAllyDist;

    public float2 SpreadPush;     // accumulated idle-dispersion push from nearby friendlies
}

public struct SpatialHash : IComponentData
{
    // Key = cell hash, Value = a neighbor in that cell.
    public NativeParallelMultiHashMap<int, UnitInfo> Map;
    public float CellSize;
}
