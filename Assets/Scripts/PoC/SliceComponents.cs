using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

// ===========================================================================
// VERTICAL-SLICE COMPONENTS
// ===========================================================================

// Formation/role is no longer an enum — behavior is composed from BehaviorFlags
// (see UnitDataComponents.cs). A unit's runtime behavior set is its base flags
// modified by a BehaviorOverride mask (driven by hero abilities).

// The enemy this unit currently considers its target (snapshot each frame).
// The CHOSEN target — selected by BehaviorSystem from the gather system's
// candidates (perception supplies facts; behavior makes the decision). Carries
// the full snapshot so consumers (attack cycle, anim, debug) share one copy.
public struct CombatTarget : IComponentData
{
    public UnitInfo Info;
    public bool Has;
}

// Explicit player/AI attack order (attack-move toward a specific enemy).
public struct AttackOrder : IComponentData
{
    public Entity Target;
    public bool Has;
}

// Output of the behavior resolver, consumed by steering. The single place that
// decides "where do I want to be" — your single-decision-point pattern.
public struct DesiredDestination : IComponentData
{
    public float2 Value;
    public bool Has;
    public bool UseFlowField;   // long-range commanded move -> route via field

    // Width in cells for flow-field routing and LoS. <=1 = point unit (original behaviour).
    public int PathWidth;

    // Desired facing, decided by BEHAVIOR (face target / facing consensus / ...).
    // Steering only executes it (turn rate); it does not choose. When HasFace is
    // false, steering falls back to facing the movement heading.
    public float2 Face;
    public bool HasFace;
}

// ---------------------------------------------------------------------------
// Obstacles (buildings + terrain doodads). Created/destroyed at runtime.
// Two shapes share the struct:
//   * Extents > 0  -> rounded rectangle: Extents.x by Extents.y nav-grid cells,
//     one cell cut from each corner (see BuildingFootprint). Buildings use this.
//     The owning entity's position must be footprint-snapped (BuildingFootprint
//     does the snapping at spawn) so the stamp is grid-aligned.
//   * Extents == 0 -> circle of Radius world units (doodads, legacy path).
// ---------------------------------------------------------------------------
public struct Obstacle : IComponentData
{
    public float Radius;     // circle path (used only when Extents is zero)
    public int2  Extents;    // rect footprint in cells; zero = circle
    public float OccluderHeight; // sight-blocking height ABOVE the footprint's terrain (0 = see over it freely).
                                 // A tall keep uses a large value; a low wall a small one, so a raised shooter
                                 // can see over it. Fed into ObstacleField.OccluderHeight at grid rebuild.
}

// ---------------------------------------------------------------------------
// Projectiles. Travel straight horizontally; the vertical position follows an
// arc that launches at LaunchHeight, bulges up by Rise, and lands at 0 exactly
// at end of life (which is set so that point is where it was aimed).
//
// Stale is set by the receiver-side hit pass in ContactCombatSystem. Two threads
// may race to set Stale=true on the same projectile; both write the same value
// so the outcome is always correct. ProjectileSystem destroys stale entities
// after ContactCombatSystem runs.
// ---------------------------------------------------------------------------
public struct Projectile : IComponentData
{
    public float2 Velocity;       // horizontal velocity
    public float Damage;
    public int Player;            // owning player id
    public float Life;            // seconds remaining
    public float TotalLife;       // life at launch (for arc progress)
    public float Rise;            // arc bulge height
    public float StartY;          // world launch height (shooter terrain + launch offset)
    public float EndY;            // world ground height at the aimed point
    public float HitRadius;
    public float CollisionHeight; // only collide at/below this height
    public bool Stale;            // hit this frame; ProjectileSystem will destroy it
}
public struct ProjectileTag : IComponentData { }

// ---------------------------------------------------------------------------
// Per-frame projectile snapshot, filled by InformationGatherSystem into each
// unit's IncomingProjectile buffer. Mirrors the UnitInfo/ContactList pattern so
// ContactCombatSystem can apply hits receiver-side (parallel, no cross-entity
// Health writes). Also exposes velocity so future behaviors can dodge slow shots.
// ---------------------------------------------------------------------------
public struct IncomingProjectile : IBufferElementData
{
    public Entity Entity;
    public float2 Position;   // XZ position this frame
    public float2 Velocity;   // horizontal velocity (for dodge behaviors)
    public float2 Direction;  // normalized travel direction (for backstab mitigation)
    public float  Damage;
    public float  HitRadius;
    public int    Player;         // owning player id
}

// Per-frame projectile spatial hash. Built by ProjectileSystem before
// InformationGatherSystem runs; consumed by GatherJob to fill IncomingProjectile
// buffers. Mirrors SpatialHash so the same cell-walk pattern works.
public struct ProjectileHash : IComponentData
{
    public NativeParallelMultiHashMap<int, IncomingProjectile> Map;
    public float CellSize;
}

// ---------------------------------------------------------------------------
// Hero marker. The hero is a normal unit entity that also carries HeroTag, so
// the HeroController can find it and cast its abilities at/around it. Its
// command "aura" is just a hero-anchored ability field (see the ability system),
// not a special component.
// ---------------------------------------------------------------------------
public struct HeroTag : IComponentData { }
