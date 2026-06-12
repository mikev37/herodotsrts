using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

// ---------------------------------------------------------------------------
// MANA REGEN — ticks every living unit's mana toward Max at its Regen rate.
// Sim time only (SystemAPI.Time.DeltaTime under the lockstep rate manager), so
// it's deterministic like every other resource change. Consumption happens at
// cast commit in CommandApplySystem; this system only ever adds.
// ---------------------------------------------------------------------------
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ManaRegenSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new RegenJob { Dt = SystemAPI.Time.DeltaTime }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct RegenJob : IJobEntity
    {
        public float Dt;

        private void Execute(ref Mana mana)
        {
            if (mana.Current >= mana.Max) return;
            mana.Current = math.min(mana.Max, mana.Current + mana.Regen * Dt);
        }
    }
}
