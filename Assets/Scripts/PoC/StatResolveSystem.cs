using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

// ===========================================================================
// STAT RESOLVE — recompute each unit's LIVE stat components from BaseStats plus
// the currently-active reverting offsets and flag modifiers, every frame. This
// is why nothing else changed: steering/combat/behavior keep reading Speed,
// Attack.Damage, Defense, UnitTuning, and BehaviorOverride as before — those are
// just recomputed here. Health is excluded (it's a resource, mutated directly by
// ModifierTickSystem). Also replaces the old HeroAuraSystem: flag modifiers now
// drive BehaviorOverride.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ModifierTickSystem))]
[UpdateBefore(typeof(InformationGatherSystem))]
public partial struct StatResolveSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new ResolveJob().ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct ResolveJob : IJobEntity
    {
        private void Execute(
            in DynamicBuffer<ActiveModifier> mods,
            in BaseStats baseS,
            ref Speed speed,
            ref UnitTuning tuning,
            ref Attack atk,
            ref Defense def)
        {
            float dSpeed = 0, dTurn = 0, dRange = 0, dDmg = 0, dArmor = 0, dShield = 0, agg = 0, loose = 0, sep = 0;

            for (int i = 0; i < mods.Length; i++)
            {
                var m = mods[i];
                switch (m.Target)
                {
                    case ModTarget.Speed:        dSpeed += m.Offset; break;
                    case ModTarget.TurnSpeed:    dTurn += m.Offset; break;
                    case ModTarget.MeleeRange:   dRange += m.Offset; break;
                    case ModTarget.AttackDamage: dDmg += m.Offset; break;
                    case ModTarget.Armor:        dArmor += m.Offset; break;
                    case ModTarget.Shield:       dShield += m.Offset; break;
                    case ModTarget.Aggression:   agg += m.Offset; break;
                    case ModTarget.Looseness:    loose += m.Offset; break;
                    case ModTarget.Separation:   sep += m.Offset; break;
                }
            }

            speed.Value       = math.max(0f, baseS.Speed + dSpeed);
            tuning.TurnSpeed  = math.max(0f, baseS.TurnSpeed + dTurn);
            tuning.MeleeRange = math.max(0f, baseS.MeleeRange + dRange);
            atk.Damage        = math.max(0f, baseS.AttackDamage + dDmg);
            def.Armor         = math.max(0f, baseS.Armor + dArmor);
            def.Shield        = math.max(0f, baseS.Shield + dShield);
            /*//TODO
            member.Aggression = agg; 
            member.Looseness = math.saturate(loose); 
            member.Separation = math.max(0f, sep);
            em.SetComponentData(e, member);   // FormationSystem + BehaviorSystem read this same tick*/
        }
    }
}
