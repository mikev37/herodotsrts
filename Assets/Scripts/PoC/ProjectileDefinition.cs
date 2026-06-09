using UnityEngine;

// ===========================================================================
// Defines a projectile's flight and look, independent of who fires it. A unit's
// UnitDefinition references one of these; the firing unit supplies the damage,
// the projectile supplies speed, arc, collision, and the view prefab.
//
// Arc: the shot launches at launchHeight, bulges up by riseHeight, and comes
// down to the ground exactly as it reaches the point it was aimed at.
// ===========================================================================
[CreateAssetMenu(menuName = "MarbleCombat/Projectile Definition")]
public class ProjectileDefinition : ScriptableObject
{
    public string displayName = "Projectile";

    [Tooltip("Visual prefab for the projectile (no logic required on it).")]
    public GameObject viewPrefab;

    [Tooltip("Horizontal travel speed.")]
    public float speed = 22f;

    [Tooltip("How far the arc bulges above the straight launch->land line at its peak.")]
    public float riseHeight = 2f;

    [Tooltip("Height the shot launches from (~shooter chest height).")]
    public float launchHeight = 1.2f;

    [Tooltip("Collision radius against units.")]
    public float hitRadius = 0.6f;

    [Tooltip("Only collides at or below this height, so a high arc clears nearer units " +
             "and connects as it descends.")]
    public float collisionHeight = 1.4f;
}
