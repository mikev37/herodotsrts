using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

// ===========================================================================
// SIM DEBUG — a singleton the (inspector-less) systems fill each frame so the
// DebugOverlay HUD can show live counts. Role counts are gone; we now tally by
// behavior flag, plus how many units are currently under a hero override.
// ===========================================================================
public struct SimDebug : IComponentData
{
    public int UnitsTeam0, UnitsTeam1;
    public int AliveTotal, DeadTotal;
    public int Projectiles;
    public int WallFormers, Tuckers, Kiters, Advancers;   // by base flag, alive
    public int Overridden;                                 // units with a hero override applied
    public int Firing, InContact;
    public int Selected;
    public int ObstacleVersion, BlockedCells;
    public byte FlowValid, FlowGoalHas;
    public int FlowBlocks;
    public int2 FlowGoalCell;
}

// Gate: SimDebugSystem only runs while this singleton exists. DebugOverlay
// creates it while the overlay component is enabled and destroys it on disable,
// so the (main-thread, scan-heavy) debug tally never runs when nothing is
// watching it.
public struct SimDebugRequest : IComponentData { }

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(DeathSystem))]
public partial struct SimDebugSystem : ISystem
{
    // Cached blocked-cell count: rescanning the whole passability grid (Res^2)
    // every frame dominated this system's cost. The count only changes when
    // obstacles change, which bumps ObstacleField.Version — so we rescan only
    // on a version change and serve the cached value otherwise.
    private int _cachedBlockedCells;
    private int _cachedObstacleVersion;

    public void OnCreate(ref SystemState state)
    {
        state.EntityManager.AddComponentData(state.EntityManager.CreateEntity(), new SimDebug());
        // Don't run unless something (the DebugOverlay) is asking for stats.
        state.RequireForUpdate<SimDebugRequest>();
        _cachedObstacleVersion = -1;
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var d = new SimDebug();

        foreach (var (team, status) in
                 SystemAPI.Query<RefRO<Team>, RefRO<CombatStatus>>()
                          .WithAll<UnitTag>().WithNone<Dead>())
        {
            d.AliveTotal++;
            if (team.ValueRO.Value == 0) d.UnitsTeam0++; else d.UnitsTeam1++;

            if (status.ValueRO.InContactWithEnemy) d.InContact++;
        }

        d.DeadTotal = SystemAPI.QueryBuilder().WithAll<UnitTag, Dead>().Build().CalculateEntityCount();
        d.Projectiles = SystemAPI.QueryBuilder().WithAll<ProjectileTag>().Build().CalculateEntityCount();
        d.Selected = SystemAPI.QueryBuilder().WithAll<Selected>().Build().CalculateEntityCount();

        if (SystemAPI.TryGetSingleton<ObstacleField>(out var obs))
        {
            d.ObstacleVersion = obs.Version;
            // Rescan only when obstacles actually changed; serve cache otherwise.
            if (obs.Version != _cachedObstacleVersion && obs.Passable.IsCreated)
            {
                int blocked = 0;
                for (int i = 0; i < obs.Passable.Length; i++) if (obs.Passable[i] == 0) blocked++;
                _cachedBlockedCells = blocked;
                _cachedObstacleVersion = obs.Version;
            }
            d.BlockedCells = _cachedBlockedCells;
        }
        if (SystemAPI.TryGetSingleton<NavFields>(out var nf))
        {
            int active = 0, mru = -1, mruTick = int.MinValue;
            for (int s = 0; s < NavGrid.MaxPaths; s++)
            {
                if (nf.Slots[s].Valid == 0) continue;
                active++;
                if (nf.Slots[s].UsedTick > mruTick) { mruTick = nf.Slots[s].UsedTick; mru = s; }
            }
            int blocks = 0;
            for (int b = 0; b < NavGrid.MaxFineBlocks; b++) if (nf.BlockKey[b] != -1) blocks++;
            d.FlowValid = (byte)(active > 0 ? 1 : 0);
            d.FlowGoalHas = (byte)math.min(active, 255);   // active paths
            d.FlowBlocks = blocks;                          // built fine fields
            d.FlowGoalCell = mru >= 0 ? nf.Slots[mru].GoalCell : new int2(-1, -1);
        }

        SystemAPI.GetSingletonRW<SimDebug>().ValueRW = d;
    }
}
