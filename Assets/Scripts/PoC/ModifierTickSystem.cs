using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

// ===========================================================================
// MODIFIER TICK — per unit, applies the "value-changing" effects (health
// damage/heal) and ticks every active modifier's timer, dropping expired ones.
// Reverting offsets and flag overrides are NOT applied here; they're recomputed
// each frame by StatResolveSystem from the surviving modifiers.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(AbilityFieldSystem))]
public partial struct ModifierTickSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new TickJob { Dt = SystemAPI.Time.DeltaTime }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct TickJob : IJobEntity
    {
        public float Dt;

        private void Execute(ref DynamicBuffer<ActiveModifier> mods, ref Health health)
        {
            for (int i = mods.Length - 1; i >= 0; i--)
            {
                var m = mods[i];
                bool remove = false;

                if (m.Target == ModTarget.Health)
                {
                    // "stays": directly change the health value (capped).
                    if (m.Mode == ModMode.Instant)
                    {
                        if (m.Applied == 0)
                            health.Current = Cap(health.Current + m.Delta, m, health.Max);
                        remove = true;            // instant value change is one-shot
                    }
                    else
                    {
                        health.Current = Cap(health.Current + m.Delta * Dt, m, health.Max);
                    }
                }
                else if (m.Revert == 1 && !AbilityUtil.IsBool(m.Target))
                {
                    // "reverts": maintain an offset for StatResolve to read.
                    if (m.Mode == ModMode.Instant) { if (m.Applied == 0) m.Offset = m.Delta; }
                    else m.Offset += m.Delta * Dt;
                }
                // flag targets: nothing to apply here.

                m.Applied = 1;
                m.Remaining -= Dt;
                if (m.Remaining <= 0f) remove = true;

                if (remove) mods.RemoveAtSwapBack(i);
                else mods[i] = m;
            }
        }

        private static float Cap(float v, in ActiveModifier m, float baseRef)
        {
            if (m.CapMode == CapMode.None) return v;
            float cap = m.CapRef == CapRef.Base ? baseRef + m.CapValue : m.CapValue;
            return m.CapMode == CapMode.Min ? math.max(v, cap) : math.min(v, cap);
        }
    }
}
