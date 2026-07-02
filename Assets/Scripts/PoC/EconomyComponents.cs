using Unity.Entities;
using Unity.Mathematics;

// ===========================================================================
// ECONOMY COMPONENTS. No "team" anywhere — every unit/building belongs to a
// Player (the single ownership axis, == SimCommand.PlayerId). Added per-entity at
// spawn from the definition. Cross-entity addressing is by StableId (snapshot-safe).
// ===========================================================================

// role tags
public struct DepotTag      : IComponentData { }
public struct IntakeTag     : IComponentData { }
public struct ProducerTag   : IComponentData { }
public struct PlayerBankTag : IComponentData { }
public struct NodeTag : IComponentData { public ResourceType Yield; public byte DespawnWhenEmpty; public float HuskLinger; }

// builder capability (>0 = builder)
public struct BuildPower : IComponentData { public float Value; }

// harvester round-trip (no Amounts writes here; all transfers via the bank job)
public enum HarvestPhase : byte { Idle, ToNode, Gathering, ToDepot, Depositing }
public struct HarvestTask : IComponentData
{
    public int          NodeStableId;    // -1 = none
    public int          DepotStableId;   // cached nearest own depot; -1 = recompute
    public HarvestPhase Phase;
    public int          Rate;            // resources/tick this harvester pulls (gather speed)
    public ResourceType Carrying;        // the ONE type currently in cargo (a selector, not an amount)
}

// Building under construction, paid PROPORTIONALLY to progress. Paid is the
// cumulative amount actually consumed; progress can never exceed the fraction
// paid (so a missing resource caps progress instead of being hoarded). Paid is
// also the exact refund on voluntary cancel.
public struct Construction : IComponentData
{
    public float          Progress;          // in build-ticks
    public float          BuildTime;          // total build-ticks (fixed timestep; no Dt)
    public ResourceAmount Cost;               // TOTAL cost
    public ResourceAmount Paid;               // cumulative consumed == cancel refund
    public float          HealthPerProgress;
    public float          SelfPower;          // >0 = builds itself with no worker (Protoss-style), stacks with builders
    public int            SacrificeDefId;     // >=0: gate progress until a unit of this def arrives & is consumed; else -1
}

// Player toggle: HIGH-priority builds/producers win bank contention over LOW ones
// (band dominates category). Absent component == low. Flipped at runtime.
public struct SpendPriority : IComponentData { public byte High; }

public struct RallyPoint : IComponentData { public float2 Value; public byte Has; }

// --- colony + haulers -------------------------------------------------------
// A colony is a DEPOT (harvesters deliver here) that does NOT feed the player
// bank (no IntakeTag). While its total holdings are at/above Threshold it builds
// haulers continuously (one every BuildTimer cycle), each carrying holdings to
// the nearest capital. No in-flight cap — a full colony keeps dispatching carts.
public struct Colony : IComponentData
{
    public int   HaulerDefId;   // roster id of the hauler unit to auto-build
    public int   Threshold;     // total stored amount that keeps haulers dispatching
    public float BuildTimer;    // counts down the hauler's productionTime between dispatches
}

// A hauler: spawned by a colony, loads the colony's holdings, delivers them to a
// pre-assigned capital, then DESPAWNS (success anim, NOT death) on delivery.
public enum HaulPhase : byte { ToSource, Loading, ToSink, Unloading, Done }
public struct HaulTask : IComponentData
{
    public int       SourceStableId;   // the colony to load from
    public int       SinkStableId;     // the capital to deliver to
    public HaulPhase Phase;
    public float     Timer;
}

// Non-death removal: plays the unit's success/vanish anim, then DespawnSystem
// destroys it. Separate from Dead so death can later grow corpses/loot without
// haulers (or other vanishers) inheriting it.
public struct Despawn : IComponentData { public float Seconds; }

// Build animation signal: ConstructionSystem stamps the current tick onto each
// builder contributing to a site this tick (the builder can't see the site in
// its own ContactList — buildings are excluded — so the site stamps the builder).
// EconomyAnimSystem reads it: stamped-this-tick => Build anim. Self-clearing.
public struct BuildSignal : IComponentData { public uint LastTick; }

public struct ProductionItem : IBufferElementData
{
    public int            UnitDefId;
    public float          Progress;           // in build-ticks
    public float          BuildTime;          // total build-ticks
    public ResourceAmount Cost;
    public ResourceAmount Paid;               // cumulative consumed == cancel refund
    public byte           Loop;
}

// One entity per player: PlayerState + a multi-type ResourceBank + PlayerBankTag.
public struct PlayerState : IComponentData
{
    public int  HeroStableId;
    public byte Age;
}
