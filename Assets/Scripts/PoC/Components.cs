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
    public float2 Value;        // actual velocity this frame (back-calculated from step taken)
    public float2 desiredValue; // acceleration-ramped locomotion; bled down when blocked
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

// Persistent knockback impulse accumulated by ContactCombatSystem and decayed
// each tick by SteeringSystem. Units feel the push for multiple frames after
// the impact, not just while directly in contact.
public struct KnockbackVelocity : IComponentData {
    public float2 Value;
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
    public int    DefId;          // unit type (e.g. "form a wall with units of my type")
    public int    Team;
    public float2 Position;
    public float  Height;         // terrain height under the unit
    public float2 Velocity;
    public float2 Facing;         // normalized XZ forward
    public float  Radius;         // body radius (physical contact)
    public float  Mass;
    public float  Health;
    public float  Damage;         // its attack damage (danger/exposure scoring)
    public float  Armor;
    public float  Shield;
    public uint   Flags;          // BehaviorFlags
    public bool   IsAttacking;    // behavior committed to an attack this tick
    public Entity AttackTarget;   // who it is attacking (single-target strikes)
    public float  StrikeDamage;   // melee pulse this tick (0 except on the strike tick)
    public float  AttackRange;    // weapon reach (melee) / fire range (ranged)
    public float  StrikeArcDot;   // cos(arc/2) for cleave strikes
    public bool   Cleave;         // strike hits everyone in the arc, not just the target
    public bool   IsBuilding;     // entity carries BuildingTag; Radius is then the
                                  // footprint's inscribed radius and consumers
                                  // measure range to the surface, not the center
}

// Nearby friendlies (full snapshots), gather-written. Separate from the
// ContactList: this is the FORMATION neighborhood (wedge/wall/cardinal/align),
// the ContactList is the PHYSICAL one (separation/impacts).
[InternalBufferCapacity(0)]
public struct FriendlyUnit : IBufferElementData
{
    public UnitInfo Info;
}

// Per-unit perception, written ONLY by InformationGatherSystem, read by behavior
// and combat. One scan, one truth: there is exactly one definition of "my
// target" / "my wall buddy" in the whole sim.
public struct Perception : IComponentData
{
    // Group structure (centers of mass are outlier-trimmed; Clustered=false
    // means the group is spread apart and its CoM is a weak signal).
    public bool   HasEnemies;
    public bool   EnemiesClustered;
    public float2 EnemyCenter;
    public bool   HasFriendlies;
    public bool   FriendliesClustered;
    public float2 FriendlyCenter;

    // Candidate enemies (full snapshots; behavior picks the actual target).
    public bool     HasClosestEnemy;
    public UnitInfo ClosestEnemy;
    public bool     HasMostDangerous;
    public UnitInfo MostDangerousEnemy;   // would do the most damage to ME (my armor/facing applied)
    public bool     HasMostExposed;
    public UnitInfo MostExposedEnemy;     // I would do the most damage to THEM

    // Friendly structure for formation/alignment behaviors.
    public bool     HasClosestFriendly;
    public UnitInfo ClosestFriendly;
    public float2   FriendlyAvgFacing;         // facing consensus (normalized, zero if none)
    public float2   FriendlyAvgVelocity;       // movement consensus (all friendlies)
    public float2   FriendlyMovingAvgVelocity; // movement consensus — only friendlies with HasTarget
}

public struct SpatialHash : IComponentData
{
    // Key = cell hash, Value = a neighbor in that cell.
    public NativeParallelMultiHashMap<int, UnitInfo> Map;
    public float CellSize;
}
