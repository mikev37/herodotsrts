using UnityEngine;

// ===========================================================================
// A wall is a building whose footprint is a WALKABLE ROOF rather than a solid
// obstacle. It inherits everything a building has (health, armor, selection,
// footprint, placement validation), and adds a wall height. At spawn it gets a
// Wall component instead of an Obstacle, so ObstacleGridSystem stamps a Roof
// top with a Transition skirt — units climb on from any adjacent ground, fight
// from the parapet, and can't be pushed off (the edge cells fence them in).
//
// Walls are solid rectangles (no corner cut): a 3xN wall would lose its ends to
// the >=3x3 corner-cut rule, so placement/stamping treat walls as full rects.
// The custom inspector hides the unit-only bloat, same as BuildingDefinition.
// ===========================================================================
[CreateAssetMenu(menuName = "MarbleCombat/Wall Definition")]
public class WallDefinition : BuildingDefinition
{
    [Header("Wall")]
    [Tooltip("Height of the walkable top above the footprint's highest terrain cell. Units on the parapet stand at this elevation.")]
    public float wallHeight = 4f;

    private void Reset()
    {
        displayName = "Wall";
        receivesAbilities = false;
        maxHealth = 1500f;
        mass = 200f;
        speed = 0f;
        attackDamage = 0f;
        attackNearby = false;
        advanceIndividual = false;
        isRanged = false;
        isHero = false;
        footprintX = 3;
        footprintZ = 8;
        maxHeightDelta = 1.0f;
    }
}
