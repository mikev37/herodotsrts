using Unity.Entities;

// ===========================================================================
// Components for the non-standard building types.
// ===========================================================================

// RELAY TOWER — a stationary alternative to haulers. Each tick it transmits up to
// Rate of every owner colony within Range straight to the nearest owner capital
// (no physical cart). Reuses the bank protocol: it appends a grouped Transfer
// request on the colony's bank, paid to the capital. Added from BuildingDefinition
// (isRelay / relayRate / relayRange).
public struct Relay : IComponentData
{
    public int   Rate;    // max resources/tick pulled from each colony in range
    public float Range;   // colony pickup radius
}

// MORPH / UPGRADE — an in-place form swap for the SAME entity (keeps StableId).
// A FREE morph (trebuchet siege, dino settle) has Cost = 0 and just runs its
// Timer. A paid UPGRADE (Keep -> Castle, Hydralisk -> Lurker) sets Cost/BuildTime
// from the target def and is gated pay-as-you-build exactly like Construction —
// MorphSystem advances Progress only as fast as it's funded, then does the swap.
// One mechanism covers both.
public struct MorphState : IComponentData
{
    public int           TargetDefId;   // roster id of the form to become
    public byte          ToBuilding;    // 1 = becoming a building, 0 = a mobile unit
    public float         Progress;      // transition build-ticks done
    public float         BuildTime;     // total transition build-ticks
    public ResourceAmount Cost;         // 0 = free morph; >0 = paid upgrade (proportional gating)
    public ResourceAmount Paid;         // cumulative consumed (refund on cancel)
}


// Resource nodes (and other passive props) are not valid combat targets. Combat
// target-acquisition and damage application skip anything tagged NonCombatant, so
// a gold mine can't be auto-attacked (it's already excluded from mobile contacts)
// OR force-attacked.
public struct NonCombatant : IComponentData { }


// A research in progress on a building. Single slot, paid pay-as-you-build like
// Construction. On completion ResearchSystem auto-upgrades the player's units and
// records the tech. From/To are roster def ids (-1 = pure flag tech, no swap).
public struct ResearchTask : IComponentData
{
    public int            FromDefId, ToDefId, MorphTicks;
    public float          Progress, BuildTime;
    public ResourceAmount Cost, Paid;
}

// Per-player record of completed unit upgrades, kept on the player's bank entity.
// ProductionSystem reads it to substitute produced units (Knight -> Paladin), and
// it rides the snapshot so the substitution survives save/restore.
public struct ResearchedTech : IBufferElementData { public int FromDefId, ToDefId; }
