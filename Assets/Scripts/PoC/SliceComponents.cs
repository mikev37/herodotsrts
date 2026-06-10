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
public struct CombatTarget : IComponentData
{
    public Entity Value;
    public float2 Position;
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
}

// ---------------------------------------------------------------------------
// Obstacles (buildings + terrain doodads). Created/destroyed at runtime.
// ---------------------------------------------------------------------------
public struct Obstacle : IComponentData { public float Radius; }

// ---------------------------------------------------------------------------
// Projectiles. Travel straight horizontally; the vertical position follows an
// arc that launches at LaunchHeight, bulges up by Rise, and lands at 0 exactly
// at end of life (which is set so that point is where it was aimed).
// ---------------------------------------------------------------------------
public struct Projectile : IComponentData
{
    public float2 Velocity;       // horizontal velocity
    public float Damage;
    public int Team;
    public float Life;            // seconds remaining
    public float TotalLife;       // life at launch (for arc progress)
    public float Rise;            // arc bulge height
    public float StartY;          // world launch height (shooter terrain + launch offset)
    public float EndY;            // world ground height at the aimed point
    public float HitRadius;
    public float CollisionHeight; // only collide at/below this height
}
public struct ProjectileTag : IComponentData { }

// ---------------------------------------------------------------------------
// Hero marker. The hero is a normal unit entity that also carries HeroTag, so
// the HeroController can find it and cast its abilities at/around it. Its
// command "aura" is just a hero-anchored ability field (see the ability system),
// not a special component.
// ---------------------------------------------------------------------------
public struct HeroTag : IComponentData { }
