using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// RELAY NETWORK — a stationary hauler replacement that forms a GRAPH. Each tick:
//   1. collect this player's capitals, colonies, and relay towers as graph nodes;
//   2. union any node within a RELAY's Range to that relay (relays are the wires —
//      colonies/capitals don't connect directly, only through relays, and relays
//      chain to other relays in range);
//   3. every colony connected (transitively) to a capital streams Rate/tick to the
//      nearest connected capital, via the normal grouped transfer request.
// Rebuilt from live entities every tick, so placing/destroying a tower re-wires the
// network automatically. A faction with no carts just leaves the colony's
// haulerUnit blank (HaulerDefId = -1 -> ProductionSystem dispatches none) and
// drains colonies through relays instead.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(ResourceBankSystem))]
public partial struct RelaySystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
        => state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                           .CreateCommandBuffer(state.WorldUnmanaged);

        // ---- collect graph nodes: kind 0 capital, 1 colony, 2 relay ----
        var kind = new NativeList<byte>(64, Allocator.Temp);
        var player = new NativeList<int>(64, Allocator.Temp);
        var pos = new NativeList<float2>(64, Allocator.Temp);
        var sid = new NativeList<int>(64, Allocator.Temp);
        var ent = new NativeList<Entity>(64, Allocator.Temp);
        var range = new NativeList<float>(64, Allocator.Temp);          // relay connect radius (0 for non-relays)
        var rate  = new NativeList<int>(64, Allocator.Temp);            // relay transmit rate (0 for non-relays)
        var hold = new NativeList<ResourceAmount>(64, Allocator.Temp);  // colony holdings (0 otherwise)

        foreach (var (pl, s, xf) in SystemAPI.Query<RefRO<Player>, RefRO<StableId>, RefRO<LocalTransform>>().WithAll<IntakeTag>())
            Add(0, pl.ValueRO.Value, P(xf), s.ValueRO.Value, Entity.Null, 0f, 0, default, kind, player, pos, sid, ent, range, rate, hold);
        foreach (var (col, bank, pl, s, xf, e) in
                 SystemAPI.Query<RefRO<Colony>, RefRO<ResourceBank>, RefRO<Player>, RefRO<StableId>, RefRO<LocalTransform>>().WithEntityAccess())
            Add(1, pl.ValueRO.Value, P(xf), s.ValueRO.Value, e, 0f, 0, bank.ValueRO.Amounts, kind, player, pos, sid, ent, range, rate, hold);
        foreach (var (relay, pl, s, xf) in SystemAPI.Query<RefRO<Relay>, RefRO<Player>, RefRO<StableId>, RefRO<LocalTransform>>())
            Add(2, pl.ValueRO.Value, P(xf), s.ValueRO.Value, Entity.Null, relay.ValueRO.Range, relay.ValueRO.Rate, default, kind, player, pos, sid, ent, range, rate, hold);

        int n = kind.Length;
        if (n > 0)
        {
            // ---- union-find: connect each relay to same-player nodes within its range ----
            var parent = new NativeArray<int>(n, Allocator.Temp);
            for (int i = 0; i < n; i++) parent[i] = i;
            for (int i = 0; i < n; i++)
            {
                if (kind[i] != 2) continue;                 // edges originate at relays
                float r2 = range[i] * range[i];
                for (int j = 0; j < n; j++)
                {
                    if (j == i || player[j] != player[i]) continue;
                    if (math.distancesq(pos[i], pos[j]) <= r2) Union(parent, i, j);
                }
            }

            // ---- each colony streams to the nearest capital in its component ----
            for (int i = 0; i < n; i++)
            {
                if (kind[i] != 1) continue;                 // colonies only
                int ri = Find(parent, i);
                int capSid = -1; float best = float.MaxValue;
                for (int j = 0; j < n; j++)
                {
                    if (kind[j] != 0 || player[j] != player[i] || Find(parent, j) != ri) continue;
                    float d = math.distancesq(pos[i], pos[j]);
                    if (d < best || (d == best && (capSid < 0 || sid[j] < capSid))) { best = d; capSid = sid[j]; }
                }
                if (capSid < 0) continue;                   // colony not wired to any capital
                int useRate = 0; float br = float.MaxValue;  // rate of nearest relay in this colony's component
                for (int j = 0; j < n; j++)
                {
                    if (kind[j] != 2 || Find(parent, j) != ri) continue;
                    float d = math.distancesq(pos[i], pos[j]);
                    if (d < br) { br = d; useRate = rate[j]; }
                }
                var send = ClampTotal(hold[i], useRate);
                if (send.Any)
                    ecb.AppendToBuffer(ent[i], new BankRequest
                    { Amount = send, RequesterStableId = capSid, Class = (byte)SpendClass.Transfer, CastTick = 0 });
            }
            parent.Dispose();
        }

        kind.Dispose(); player.Dispose(); pos.Dispose(); sid.Dispose(); ent.Dispose(); range.Dispose(); rate.Dispose(); hold.Dispose();
    }

    private static float2 P(RefRO<LocalTransform> xf) => new float2(xf.ValueRO.Position.x, xf.ValueRO.Position.z);

    private static void Add(byte k, int pl, float2 p, int s, Entity e, float rng, int rt, ResourceAmount h,
        NativeList<byte> kind, NativeList<int> player, NativeList<float2> pos, NativeList<int> sid,
        NativeList<Entity> ent, NativeList<float> range, NativeList<int> rate, NativeList<ResourceAmount> hold)
    { kind.Add(k); player.Add(pl); pos.Add(p); sid.Add(s); ent.Add(e); range.Add(rng); rate.Add(rt); hold.Add(h); }

    private static int Find(NativeArray<int> p, int x) { while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; } return x; }
    private static void Union(NativeArray<int> p, int a, int b) { int ra = Find(p, a), rb = Find(p, b); if (ra != rb) p[math.max(ra, rb)] = math.min(ra, rb); }


    private static ResourceAmount ClampTotal(ResourceAmount have, int rate)
    {
        var o = new ResourceAmount(); int budget = rate;
        for (int i = 0; i < ResourceAmount.Count && budget > 0; i++) { int take = math.min(have[i], budget); o[i] = take; budget -= take; }
        return o;
    }
}
