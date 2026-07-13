using Unity.Entities;
using Unity.Mathematics;

// ===========================================================================
// THE resource vector. ONE place to add/remove a resource: add a field, add it
// to the indexer + Count, done — every bank, cost, request and the checksum pick
// it up. Named fields (cost.Wood, not cost.y) so authoring/readers never memorize
// slot order; an indexer + Count let the rest of the code treat it like a buffer
// (loop 0..Count). Blittable struct (NOT a managed Dictionary) so it lives in
// IComponentData, rides the snapshot, and Bursts.
//
// To add "Stone": add `public int Stone;`, a case to the indexer, bump Count,
// add it to ResourceType. Nothing else changes.
// ===========================================================================
public enum ResourceType : byte { Gold = 0, Wood = 1, Food = 2 }   // keep in sync with the indexer

public struct ResourceAmount
{
    public int Gold;
    public int Wood;
    public int Food;

    public const int Count = 3;

    public int this[int i]
    {
        get => i == 0 ? Gold : i == 1 ? Wood : Food;
        set { if (i == 0) Gold = value; else if (i == 1) Wood = value; else Food = value; }
    }
    public int this[ResourceType t] { get => this[(int)t]; set => this[(int)t] = value; }

    public static ResourceAmount operator +(ResourceAmount a, ResourceAmount b)
        => new ResourceAmount { Gold = a.Gold + b.Gold, Wood = a.Wood + b.Wood, Food = a.Food + b.Food };
    public static ResourceAmount operator -(ResourceAmount a, ResourceAmount b)
        => new ResourceAmount { Gold = a.Gold - b.Gold, Wood = a.Wood - b.Wood, Food = a.Food - b.Food };

    public bool Any => Gold != 0 || Wood != 0 || Food != 0;
    public int Total => Gold + Wood + Food;

    public static ResourceAmount Max0(ResourceAmount a)
        => new ResourceAmount { Gold = math.max(0, a.Gold), Wood = math.max(0, a.Wood), Food = math.max(0, a.Food) };

    // True iff `have` covers every component of `cost` (the affordability test).
    public static bool Covers(in ResourceAmount have, in ResourceAmount cost)
        => have.Gold >= cost.Gold && have.Wood >= cost.Wood && have.Food >= cost.Food;

    // Largest rational frac (num/den, <=1) of `want` that `have` can satisfy across
    // ALL non-zero components — the proportional-grant limiter. Integer-only.
    public static void AffordableFraction(in ResourceAmount have, in ResourceAmount want, out int num, out int den)
    {
        num = 1; den = 1;   // start at "all of it"
        for (int i = 0; i < Count; i++)
        {
            int w = want[i]; if (w <= 0) continue;
            int h = math.max(0, have[i]);
            if ((long)h * den < (long)num * w) { num = h; den = w; }   // h/w < num/den
        }
        if (num > den) { num = 1; den = 1; }                           // clamp to <= 1
    }

    // Scale by num/den (integer, floor). The limiting component lands exactly on `have`.
    public ResourceAmount Scaled(int num, int den) => new ResourceAmount
    {
        Gold = den == 0 ? 0 : (int)((long)Gold * num / den),
        Wood = den == 0 ? 0 : (int)((long)Wood * num / den),
        Food = den == 0 ? 0 : (int)((long)Food * num / den),
    };

    public uint Hash() => math.hash(new uint3((uint)Gold, (uint)Wood, (uint)Food));
}
