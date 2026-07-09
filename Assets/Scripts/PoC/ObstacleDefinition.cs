using UnityEngine;

// ===========================================================================
// A dumb terrain OBSTACLE — a rock, a tree, a boulder pile. It blocks movement
// and (optionally) sight, and that's ALL it does. It is NOT a building: it has
// no combat, no economy, no abilities, no mana, no upgrades, no production.
//
// It still derives from UnitDefinition because the roster, UnitFactory.Create,
// and the snapshot pipeline are all keyed on UnitDefinition — deriving keeps an
// obstacle a first-class spawnable with zero plumbing changes. The baggage from
// the base is simply neutralized in Reset() and hidden by ObstacleDefinitionEditor,
// so the inspector shows only what a rock actually needs.
//
// INVULNERABILITY: an obstacle is invulnerable because it is a NonCombatant
// (combat/targeting skip NonCombatant entirely — see ContactCombatSystem and
// InformationGatherSystem). That is the real, single invulnerability mechanism —
// NOT a giant maxHealth number. `invulnerable` (default true) drives whether the
// spawn adds the NonCombatant tag. An obstacle you WANT choppable (a destructible
// tree that yields nothing) can untick it and give it a real maxHealth.
//
// VARIANTS: `viewPrefabVariants` lets one definition present several meshes (six
// rock shapes, a handful of tree models) chosen deterministically per-entity from
// the unit's StableId — so the field looks varied but every client picks the
// exact same mesh for the exact same entity (lockstep-safe). Leave it empty to
// use the single base `viewPrefab`.
// ===========================================================================
[CreateAssetMenu(menuName = "MarbleCombat/Obstacle Definition")]
public class ObstacleDefinition : BuildingDefinition
{
    [Header("Obstacle")]
    [Tooltip("Invulnerable: the obstacle can never be attacked or damaged (it's tagged NonCombatant " +
             "at spawn). This IS the invulnerability mechanism — not a huge health value. Untick only " +
             "if you want a destructible obstacle, then set a real maxHealth.")]
    public bool invulnerable = true;

    [Tooltip("Optional pool of view meshes. When non-empty, each spawned obstacle deterministically " +
             "picks one variant from its StableId (same entity → same mesh on every client, so the " +
             "field looks varied while staying lockstep-safe). Empty = use the single viewPrefab above.")]
    public GameObject[] viewPrefabVariants = System.Array.Empty<GameObject>();

    // Deterministic per-entity variant pick. stableId is the unit's network id
    // (assigned in spawn order), so every client resolves the identical mesh.
    // Falls back to the base viewPrefab when no variants are authored.
    public GameObject ResolveView(int stableId)
    {
        if (viewPrefabVariants == null || viewPrefabVariants.Length == 0) return viewPrefab;
        // unsigned modulo so negative ids (shouldn't happen) can't throw.
        uint idx = (uint)stableId % (uint)viewPrefabVariants.Length;
        var pick = viewPrefabVariants[idx];
        return pick != null ? pick : viewPrefab;
    }

    private void Reset()
    {
        displayName       = "Obstacle";
        // A rock isn't a combatant, producer, researcher, caster, or depot.
        receivesAbilities = false;
        canAttack         = false;
        contactDamage     = 0f;
        isProducer        = false;
        isResearcher      = false;
        isDepot           = false;
        isRelay           = false;
        // No meaningful health when invulnerable; kept small & honest, not 9999.
        maxHealth         = 100f;
        mass              = 1000f;   // immovable
        speed             = 0f;
        armor             = 0f;
        shield            = 0f;
        attackDamage      = 0f;
        isRanged          = false;
        isHero            = false;
        // A rock DOES block sight by default (that's its point on a battlefield).
        occluderHeight    = 4f;
        footprintX        = 2;
        footprintZ        = 2;
    }
}
