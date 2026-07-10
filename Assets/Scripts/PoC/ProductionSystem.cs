using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// Non-Burst (spawns via UnitFactory). Two responsibilities:
//   (1) Producer queues: fold typed grants into the head item's Funded, request
//       the shortfall from the owner's bank, start the timer when funded, spawn
//       to the rally on completion. Never writes a bank Amounts.
//   (2) Colony auto-haul: when a colony's holdings reach its Threshold and it has
//       no hauler in flight, build the hauler (productionTime) and spawn it
//       pre-targeted at the nearest capital (HaulSystem does the trip).
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(AbilityCastSystem))]
public partial struct ProductionSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerBankRegistry>();
        state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var factory = UnitFactory.Instance;
        if (factory == null || !factory.Ready) return;
        var roster = factory.Roster;
        var em = state.EntityManager;
        var banks = SystemAPI.GetSingleton<PlayerBankRegistry>().Map;
        var PrioLk = SystemAPI.GetComponentLookup<SpendPriority>(true);
        const float TickRate = 30f;
        var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
        float dt = SystemAPI.Time.DeltaTime;

        // -------- (1) producer queues --------
        var toSpawn = new NativeList<SpawnReq>(8, Allocator.Temp);

        // Index-based loop: DOTS source generator marks the foreach iteration
        // variable as readonly, preventing DynamicBuffer element writes
        // (queue[0] = head). Iterating by entity index avoids that restriction.
        var prodQuery = SystemAPI.QueryBuilder()
            .WithAll<ProducerTag>()
            .WithNone<Construction, MorphState, ResearchTask>()
            .Build();
        var prodEntities = prodQuery.ToEntityArray(Allocator.Temp);

        for (int qi = 0; qi < prodEntities.Length; qi++)
        {
            var e        = prodEntities[qi];
            var queue    = SystemAPI.GetBuffer<ProductionItem>(e);
            var deposits = SystemAPI.GetBuffer<BankDeposit>(e);
            var player   = SystemAPI.GetComponent<Player>(e);
            var rally    = SystemAPI.GetComponent<RallyPoint>(e);
            var xf       = SystemAPI.GetComponent<LocalTransform>(e);
            var sidRef   = SystemAPI.GetComponent<StableId>(e);

            if (queue.Length > 0)
            {
                var h = queue[0];
                for (int i = 0; i < deposits.Length; i++) h.Paid += deposits[i].Amount;
                queue[0] = h;
            }
            deposits.Clear();
            if (queue.Length == 0) continue;

            int p = player.Value, sid = sidRef.Value;
            var head = queue[0];
            var def = roster.GetDefinition(head.UnitDefId);
            if (def == null) { queue.RemoveAt(0); continue; }

            if (head.BuildTime <= 0f)
            {
                head.Cost = new ResourceAmount { Gold = def.prodCostGold, Wood = def.prodCostWood, Food = def.prodCostFood };
                head.BuildTime = math.max(1f, def.productionTime * TickRate);
            }

            float paidFrac = 1f;
            for (int r = 0; r < ResourceAmount.Count; r++)
                if (head.Cost[r] > 0) paidFrac = math.min(paidFrac, (float)head.Paid[r] / head.Cost[r]);
            float allowed = head.BuildTime * paidFrac;
            head.Progress = math.min(math.min(head.Progress + 1f, allowed), head.BuildTime);

            if (head.Progress >= head.BuildTime)
            {
                queue[0] = head;
                float2 rp = rally.Has != 0 ? rally.Value : new float2(xf.Position.x, xf.Position.z);
                toSpawn.Add(new SpawnReq { Player = p, DefId = head.UnitDefId,
                                           Pos = Entrance(xf, SystemAPI.GetComponent<Obstacle>(e)),
                                           Rally = rp, Loop = head.Loop, Producer = e, IsHauler = false });
                queue.RemoveAt(0);
                continue;
            }

            if (banks.TryGetValue(p, out Entity bank))
            {
                float frac = math.min(1f, (head.Progress + 1f) / head.BuildTime);
                // CEIL, never round: the first installment is Cost/BuildTime per
                // tick (e.g. 50g over 150 ticks = 0.33), and round() truncated it
                // to ZERO — so no request was ever sent, nothing was ever paid,
                // progress never moved, and production deadlocked at tick one.
                // ceil asks for at least 1 of each still-unpaid type; min clamps
                // the total to exactly Cost.
                var required = new ResourceAmount {
                    Gold = math.min(head.Cost.Gold, (int)math.ceil(head.Cost.Gold * frac)),
                    Wood = math.min(head.Cost.Wood, (int)math.ceil(head.Cost.Wood * frac)),
                    Food = math.min(head.Cost.Food, (int)math.ceil(head.Cost.Food * frac)) };
                var deficit = ResourceAmount.Max0(required - head.Paid);
                if (deficit.Any)
                {
                    byte high = PrioLk.HasComponent(e) ? PrioLk[e].High : (byte)0;
                    ecb.AppendToBuffer(bank, new BankRequest {
                        Amount = deficit, RequesterStableId = sid,
                        Class = (byte)(high != 0 ? SpendClass.ProducerHigh : SpendClass.ProducerLow), CastTick = 0 });
                }
            }
            queue[0] = head;
        }
        prodEntities.Dispose();

        // -------- (2) colony auto-haul --------
        // gather capitals (intake depots) so each colony can target the nearest.
        var capPlayer = new NativeList<int>(8, Allocator.Temp);
        var capSid = new NativeList<int>(8, Allocator.Temp);
        var capPos = new NativeList<float2>(8, Allocator.Temp);
        foreach (var (player, sidRef, xf) in
                 SystemAPI.Query<RefRO<Player>, RefRO<StableId>, RefRO<LocalTransform>>().WithAll<IntakeTag>())
        { capPlayer.Add(player.ValueRO.Value); capSid.Add(sidRef.ValueRO.Value); capPos.Add(new float2(xf.ValueRO.Position.x, xf.ValueRO.Position.z)); }

        foreach (var (colony, bank, player, sidRef, xf, obstacle) in
                 SystemAPI.Query<RefRW<Colony>, RefRO<ResourceBank>, RefRO<Player>, RefRO<StableId>, RefRO<LocalTransform>, RefRO<Obstacle>>())
        {
            ref var col = ref colony.ValueRW;
            var a = bank.ValueRO.Amounts;
            int total = a.Total;
            var hdef = roster.GetDefinition(col.HaulerDefId);
            float prod = hdef != null ? hdef.productionTime : 5f;   // hauler dispatch interval

            bool wants = total >= col.Threshold                       // normal: full enough
                      || (col.ForceLaunch != 0 && total > 0);         // emergency: player-armed, anything stored
            if (wants && hdef != null)
            {
                col.BuildTimer -= dt;
                if (col.BuildTimer <= 0f)   // dispatch a cart; keeps dispatching at PROD intervals while full
                {
                    float2 cpos = new float2(xf.ValueRO.Position.x, xf.ValueRO.Position.z);
                    int capital = NearestCapital(player.ValueRO.Value, cpos, capPlayer, capSid, capPos);
                    if (capital >= 0)
                    {
                        toSpawn.Add(new SpawnReq { Player = player.ValueRO.Value, DefId = col.HaulerDefId,
                                                   Pos = Entrance(xf.ValueRO, obstacle.ValueRO),
                                                   Rally = cpos, Loop = 0, Producer = Entity.Null, IsHauler = true,
                                                   SourceSid = sidRef.ValueRO.Value, SinkSid = capital });
                        col.ForceLaunch = 0;   // consumed
                    }
                    col.BuildTimer = prod;
                }
            }
            else if (col.ForceLaunch == 0) col.BuildTimer = prod;      // armed colonies keep their countdown
        }

        // The building's entrance: one cell out from the middle of its FRONT face
        // (its facing direction), so fresh units appear at the doorway instead of
        // inside the impassable footprint. If that spot happens to be blocked,
        // UnitFactory's spawn snap finds the nearest standable cell from there.
        static float3 Entrance(in LocalTransform xf, in Obstacle obs)
        {
            float3 f3 = math.forward(xf.Rotation);
            float2 fw = math.normalizesafe(new float2(f3.x, f3.z), new float2(0f, 1f));
            float2 half = (float2)obs.Extents * (NavGrid.CellSize * 0.5f);
            float dist = math.abs(fw.x) * half.x + math.abs(fw.y) * half.y + NavGrid.CellSize * 0.75f;
            return xf.Position + new float3(fw.x, 0f, fw.y) * dist;
        }

        // -------- apply spawns --------
        for (int i = 0; i < toSpawn.Length; i++)
        {
            var s = toSpawn[i];
            int effDef = SubstituteTech(em, banks, s.Player, s.DefId);   // future Knights come out as Paladins
            var def = roster.GetDefinition(effDef);
            if (def == null) continue;
            var unit = factory.Create(def, effDef, s.Player, s.Pos);
            if (em.HasComponent<MoveTarget>(unit))
            {
                var mv = em.GetComponentData<MoveTarget>(unit);
                mv.Value = s.Rally; mv.HasTarget = true; mv.AttackMove = false; em.SetComponentData(unit, mv);
            }
            if (s.IsHauler && em.HasComponent<HaulTask>(unit))
                em.SetComponentData(unit, new HaulTask { SourceStableId = s.SourceSid, SinkStableId = s.SinkSid, Phase = HaulPhase.ToSource });
            if (s.Loop != 0 && s.Producer != Entity.Null && em.HasBuffer<ProductionItem>(s.Producer))
                em.GetBuffer<ProductionItem>(s.Producer).Add(new ProductionItem { UnitDefId = s.DefId, Loop = 1 });
        }
        toSpawn.Dispose(); capPlayer.Dispose(); capSid.Dispose(); capPos.Dispose();
    }

    private static int NearestCapital(int player, float2 from, NativeList<int> cp, NativeList<int> cs, NativeList<float2> pos)
    {
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < cp.Length; i++)
        {
            if (cp[i] != player) continue;
            float d = math.distancesq(from, pos[i]);
            if (d < bestD || (d == bestD && (best < 0 || cs[i] < best))) { bestD = d; best = cs[i]; }
        }
        return best;
    }

    // Follow the player's completed upgrade chain for a produced def (Knight->Paladin->...).
    private static int SubstituteTech(EntityManager em, Unity.Collections.NativeParallelHashMap<int, Entity> banks, int player, int defId)
    {
        if (!banks.TryGetValue(player, out var bankE) || !em.HasBuffer<ResearchedTech>(bankE)) return defId;
        var techs = em.GetBuffer<ResearchedTech>(bankE);
        int cur = defId, guard = 0;
        bool changed = true;
        while (changed && guard++ < 8)
        {
            changed = false;
            for (int t = 0; t < techs.Length; t++)
                if (techs[t].FromDefId == cur) { cur = techs[t].ToDefId; changed = true; break; }
        }
        return cur;
    }

    private struct SpawnReq
    {
        public int Player, DefId; public float3 Pos; public float2 Rally; public byte Loop;
        public Entity Producer; public bool IsHauler; public int SourceSid, SinkSid;
    }
}
