using Unity.Entities;
using Unity.Mathematics;

// ===========================================================================
// Commander resources — one per-team pool, pure sim state (deterministic; it
// only changes inside the tick, from commands). Stored as a buffer on a
// singleton entity, indexed by team. UnitManager creates and seeds it at Start.
//
// Abilities consume from it at commit (CommandApplySystem): all three amounts
// are checked first; if any is short the cast fails and NOTHING is consumed.
// That is the whole "peasant builds a farm for 100 wood" loop: a build ability
// with a building spawnUnit and a resource cost.
//
// Nothing produces resources yet — income (mines, lumber camps, trickle) is a
// future system that writes this same buffer from inside the sim.
// ===========================================================================

public enum ResourceType : byte { Gold = 0, Wood = 1, Stone = 2 }

// Buffer element; buffer index = team. Amounts maps x=Gold, y=Wood, z=Stone
// (the ResourceType order).
public struct TeamResources : IBufferElementData
{
    public int3 Amounts;
}

// Marks the singleton entity carrying the TeamResources buffer.
public struct ResourcePoolTag : IComponentData { }
