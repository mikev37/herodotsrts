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
public struct Selected : IComponentData { }

// ---------------------------------------------------------------------------
// Spatial hash singleton. One map per frame, shared by every system that
// needs neighbor lookups. THIS is the core of scaling to thousands of units:
// O(1) neighbor queries instead of O(n^2).
// ---------------------------------------------------------------------------
public struct NeighborData
{
    public float2 Position;
    public float2 Velocity;   // so a unit can compute closing speed of attackers
    public float Mass;        // so impact damage scales with the rammer's mass
    public float Health;      // so targeting can prefer the weakest enemy
    public float StrikeDamage; // discrete melee strike this frame (0 except on an attacker's strike frame)
    public float2 Forward;     // attacker's facing (XZ), so a defender can test the strike arc
    public float StrikeArcDot; // attacker's cos(arc/2)
    public float MeleeRange;   // attacker's reach (Attack.Range), so contact range scales with weapon length
    public uint Flags;        // BehaviorFlags, so behaviors can find e.g. friendly wall-formers
    public int Team;
    public Entity Entity;
}

public struct SpatialHash : IComponentData
{
    // Key = cell hash, Value = a neighbor in that cell.
    public NativeParallelMultiHashMap<int, NeighborData> Map;
    public float CellSize;
}
