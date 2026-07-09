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
            float dSpeed = 0, dTurn = 0, dRange = 0, dDmg = 0, dArmor = 0, dShield = 0;
            // Behavior-tuning deltas (UnitTuning).
            float dAtkNear = 0, dIdleSpace = 0, dSep = 0, dCombatSpace = 0,
                  dAvoid = 0, dPursue = 0, dCohesion = 0, dRetreat = 0, dReEngage = 0;

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

                    // Aggression and AttackNearbyRange both drive the same param.
                    case ModTarget.Aggression:
                    case ModTarget.AttackNearbyRange: dAtkNear += m.Offset; break;
                    case ModTarget.Looseness:         dIdleSpace += m.Offset; break;
                    case ModTarget.Separation:        dSep += m.Offset; break;
                    case ModTarget.CombatSpacing:     dCombatSpace += m.Offset; break;
                    case ModTarget.AvoidMeleeRange:   dAvoid += m.Offset; break;
                    case ModTarget.PursueDistance:    dPursue += m.Offset; break;
                    case ModTarget.CohesionRadius:    dCohesion += m.Offset; break;
                    case ModTarget.RetreatHealthPct:  dRetreat += m.Offset; break;
                    case ModTarget.ReEngageHealthPct: dReEngage += m.Offset; break;
                }
            }

            speed.Value       = math.max(0f, baseS.Speed + dSpeed);
            tuning.TurnSpeed  = math.max(0f, baseS.TurnSpeed + dTurn);
            tuning.MeleeRange = math.max(0f, baseS.MeleeRange + dRange);
            atk.Damage        = math.max(0f, baseS.AttackDamage + dDmg);
            def.Armor         = math.max(0f, baseS.Armor + dArmor);
            def.Shield        = math.max(0f, baseS.Shield + dShield);

            // Live behavior tuning = base + active offsets. FormationSystem and
            // BehaviorSystem read these same components the same frame, so an
            // ability that (e.g.) raises aggression or forbids retreat takes hold
            // immediately and reverts cleanly when it ends.
            tuning.AttackNearbyRange  = math.max(0f, baseS.AttackNearbyRange  + dAtkNear);
            tuning.IdleSpacing        = math.max(0f, baseS.IdleSpacing        + dIdleSpace);
            tuning.SeparationStrength = math.max(0f, baseS.SeparationStrength + dSep);
            tuning.CombatSpacing      = math.max(0f, baseS.CombatSpacing      + dCombatSpace);
            tuning.AvoidMeleeRange    = math.max(0f, baseS.AvoidMeleeRange    + dAvoid);
            tuning.PursueDistance     = math.max(0f, baseS.PursueDistance     + dPursue);
            tuning.CohesionRadius     = math.max(0f, baseS.CohesionRadius     + dCohesion);
            // Retreat thresholds are fractions [0,1]. "Retreat at full health" =
            // set RetreatHealthPct to 1; "never retreat" = 0 (or below-25% guard =
            // set it to 0.25 and ReEngage above it). Clamped so offsets stay sane.
            tuning.RetreatHealthPct   = math.saturate(baseS.RetreatHealthPct  + dRetreat);
            tuning.ReEngageHealthPct  = math.saturate(baseS.ReEngageHealthPct + dReEngage);
        }
    }
}
