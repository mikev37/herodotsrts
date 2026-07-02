using Unity.Entities;

// ===========================================================================
// RESOURCE BANK — a multi-type store (ResourceAmount) plus a deferred, PRIORITY-
// ORDERED, PROPORTIONAL transfer protocol. The bank job is the only writer of
// Amounts; everyone else appends grouped requests/deposits via an ECB.
//
// A request is now ONE grouped multi-type ask (not one per resource), so the bank
// can grant it PROPORTIONALLY: if it can only cover 40% of the limiting resource,
// it grants 40% of every resource in that request — which is what lets a build
// consume in step with its progress instead of hoarding the resource it can get
// while starving on the one it can't.
//
// Requests are served in priority order: Class, then CastTick (abilities, cast
// order), then StableId (the tiebreak, as before).
// ===========================================================================

// Lower value = served first. Band dominates category; producers beat construction.
public enum SpendClass : byte
{
    Ability          = 0,   // always first (sorted among themselves by CastTick)
    ProducerHigh     = 1,
    ConstructionHigh = 2,
    ProducerLow      = 3,
    ConstructionLow  = 4,
    Transfer         = 5,   // harvest/intake on non-player banks — no spend contention there
}

public struct ResourceBank : IComponentData
{
    public ResourceAmount Amounts;
    public ResourceAmount Capacity;   // per-slot cap; 0 = uncapped
    public byte Paused;               // 1 = refuses to satisfy requests (player toggle)
}

// One grouped ask. Amount is the whole multi-type request; granted proportionally.
public struct BankRequest : IBufferElementData
{
    public ResourceAmount Amount;
    public int            RequesterStableId;
    public byte           Class;       // SpendClass
    public uint           CastTick;    // ability cast tick for cast-order (0 otherwise)
}

// One grouped grant/income. Folded (summed) into Amounts.
public struct BankDeposit : IBufferElementData { public ResourceAmount Amount; }
