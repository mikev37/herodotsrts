using UnityEngine;

// ===========================================================================
// A building that holds a finite, depletable ResourceBank of a single type.
// `resourceType` and `amount` together seed NodeTag.Yield and the bank's
// Amounts/Capacity at spawn (see UnitFactory.AddEconomyRoles).
//
// Inherits footprint, placement, health, and visuals from BuildingDefinition;
// the custom editor (ResourceNodeDefinitionEditor) shows only the fields that
// matter for a node.
// ===========================================================================
[CreateAssetMenu(menuName = "MarbleCombat/Resource Node Definition")]
public class ResourceNodeDefinition : BuildingDefinition
{
    [Header("Resource node")]
    [Tooltip("The single resource type this node yields to harvesters.")]
    public ResourceType resourceType = ResourceType.Gold;
    [Tooltip("Initial bank amount (also the per-slot capacity). Depletes as harvesters pull; 0 = empty/despawn.")]
    public int amount = 1000;
    [Tooltip("When the node empties, despawn it after huskLingerSeconds instead of leaving a permanent husk/stump.")]
    public bool despawnWhenDepleted = false;
    [Tooltip("Seconds the husk lingers before the entity is destroyed (only when despawnWhenDepleted = true).")]
    public float huskLingerSeconds = 4f;

    private void Reset()
    {
        displayName     = "Resource Node";
        receivesAbilities = false;
        maxHealth       = 1000f;
        mass            = 1000f;
        speed           = 0f;
        attackDamage    = 0f;
        isRanged        = false;
        isHero          = false;
        footprintX      = 2;
        footprintZ      = 2;
    }
}
