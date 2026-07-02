using UnityEngine;

// A researched upgrade (e.g. Knight -> Paladin). Researched ONCE at a building
// (cost + time); on completion every existing unit of fromUnit owned by the
// researching player auto-morphs to toUnit (free, since the tech was paid), and
// future production of fromUnit yields toUnit. A pure tech (no unit swap) just
// leaves from/to null and is used as a flag others read.
[CreateAssetMenu(menuName = "MarbleCombat/Tech Definition")]
public class TechDefinition : ScriptableObject
{
    public string displayName = "Upgrade";

    [Header("Unit auto-upgrade (optional)")]
    public UnitDefinition fromUnit;     // existing units of this type upgrade...
    public UnitDefinition toUnit;       // ...into this type
    [Tooltip("Per-unit transition length when the tech completes (visual morph). Small = near-instant.")]
    public int upgradeMorphTicks = 8;

    [Header("Research cost & time")]
    public int costGold = 0;
    public int costWood = 0;
    public int costFood = 0;
    [Tooltip("Research duration in build-ticks (fixed timestep; pay-as-you-build).")]
    public float researchTime = 60f;
}
