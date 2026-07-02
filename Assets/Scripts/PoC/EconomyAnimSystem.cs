using Unity.Burst;
using Unity.Entities;

// Sim -> animation for economy units. Runs AFTER AnimationStateSystem (which sets
// the base Idle/Walk/Attack/Die) and OVERRIDES UnitAnim.State for units that are
// harvesting / hauling / building / delivering. The view maps the new AnimState
// values to Animator clips exactly like the rest.
//
// Builders can't see the site in their own ContactList (buildings are excluded),
// so ConstructionSystem stamps BuildSignal.LastTick on contributors; "stamped
// this tick" => Build.
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(AnimationStateSystem))]
[UpdateAfter(typeof(ConstructionSystem))]
public partial struct EconomyAnimSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state) => state.RequireForUpdate<SimClock>();

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        uint tick = SystemAPI.GetSingleton<SimClock>().Tick;

        // harvesters working
        foreach (var (anim, task) in SystemAPI.Query<RefRW<UnitAnim>, RefRO<HarvestTask>>().WithNone<Dead, Despawn>())
            if (task.ValueRO.Phase == HarvestPhase.Gathering || task.ValueRO.Phase == HarvestPhase.Depositing)
                anim.ValueRW.State = AnimState.Harvest;

        // haulers loading/unloading (in transit stays Walk from the base system)
        foreach (var (anim, haul) in SystemAPI.Query<RefRW<UnitAnim>, RefRO<HaulTask>>().WithNone<Dead, Despawn>())
            if (haul.ValueRO.Phase == HaulPhase.Loading || haul.ValueRO.Phase == HaulPhase.Unloading)
                anim.ValueRW.State = AnimState.Harvest;

        // builders stamped by ConstructionSystem this tick
        foreach (var (anim, sig) in SystemAPI.Query<RefRW<UnitAnim>, RefRO<BuildSignal>>().WithNone<Dead, Despawn>())
            if (sig.ValueRO.LastTick == tick)
                anim.ValueRW.State = AnimState.Build;

        // morphing (transition animation) and delivering/vanishing win over the rest
        foreach (var anim in SystemAPI.Query<RefRW<UnitAnim>>().WithAll<MorphState>().WithNone<Dead>())
            anim.ValueRW.State = AnimState.Morph;
        foreach (var anim in SystemAPI.Query<RefRW<UnitAnim>>().WithAll<Despawn>())
            anim.ValueRW.State = AnimState.Deliver;
    }
}
