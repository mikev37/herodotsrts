using UnityEngine;

// ===========================================================================
// A building is a unit definition plus a footprint. Subclassing keeps every
// shared system (spawn copy, registry-by-roster-index, view pooling, abilities)
// working on the parent type unchanged; the spawn path branches on
// `def is BuildingDefinition` to add BuildingTag / Immobile / Obstacle and to
// snap the position to the nav grid.
//
// The inherited locomotion/behavior/ranged fields are meaningless for a
// building (Immobile entities never run behavior or steering); the custom
// inspector (Editor/BuildingDefinitionEditor) hides them so the asset only
// shows what matters. Reset() sets building-appropriate defaults for the
// hidden fields so a fresh asset is inert even before the inspector is opened.
// ===========================================================================
[CreateAssetMenu(menuName = "MarbleCombat/Building Definition")]
public class BuildingDefinition : UnitDefinition
{
    [Header("Footprint (nav-grid cells)")]
    [Tooltip("Footprint width in nav-grid cells (X axis). One cell is cut from each corner when both extents are >= 3.")]
    public int footprintX = 4;
    [Tooltip("Footprint depth in nav-grid cells (Z axis).")]
    public int footprintZ = 4;

    [Header("Placement")]
    [Tooltip("Max terrain height difference across the footprint cells; placement is rejected above this. The model should carry a basement skirt to cover the allowed delta.")]
    public float maxHeightDelta = 1.0f;

    private void Reset()
    {
        // Hidden-field defaults: a building neither moves, fights, nor receives
        // ability fields unless someone deliberately changes that.
        displayName = "Building";
        receivesAbilities = false;
        maxHealth = 500f;
        mass = 50f;
        speed = 0f;
        attackDamage = 0f;
        isRanged = false;
        isHero = false;
    }
}
