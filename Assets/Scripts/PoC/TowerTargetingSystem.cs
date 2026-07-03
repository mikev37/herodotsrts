using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// TOWER TARGETING — makes IMMOBILE attackers (defensive buildings, gate-guns)
// actually fire. BehaviorSystem — which normally sets CombatStatus.IsAttacking
// and picks CombatTarget from Perception — excludes Immobile entities (buildings
// don't move, steer, or hold formation), so without this a tower with isRanged +
// a projectile would perceive enemies but never commit to attacking them.
//
// This is the minimal stationary equivalent of BehaviorSystem's targeting:
//   - only runs on Immobile entities that CAN attack (attack damage > 0),
//   - picks the nearest perceived enemy within attack range,
//   - sets CombatTarget + IsAttacking so AttackTimerSystem runs its charge/fire
//     cycle (which spawns the projectile for ranged, applies melee for melee).
//
// Buildings can't rotate (SteeringSystem skips Immobile), so AttackTimerSystem's
// "must be facing the target" gate would block them forever. We spawn the
// building already facing... nothing in particular, so instead AttackTimerSystem
// treats Immobile attackers as always-facing (see the Immobile bypass there).
// Runs before AttackTimerSystem, after InformationGatherSystem (needs Perception).
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(InformationGatherSystem))]
[UpdateBefore(typeof(AttackTimerSystem))]
public partial struct TowerTargetingSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new TowerTargetJob().ScheduleParallel();
    }

    [BurstCompile]
    [WithAll(typeof(Immobile))]              // stationary attackers only
    [WithNone(typeof(Dead), typeof(NonCombatant))]   // nodes/neutral buildings never fight
    private partial struct TowerTargetJob : IJobEntity
    {
        private void Execute(in LocalTransform xform,
                             in Perception perception,
                             in Attack attack,
                             ref CombatTarget target,
                             ref CombatStatus status)
        {
            // No weapon -> never a threat (nodes, walls, pure economy buildings).
            if (attack.Damage <= 0f || attack.Range <= 0f)
            {
                target.Has = false;
                status.IsAttacking = false;
                return;
            }

            if (!perception.HasClosestEnemy)
            {
                target.Has = false;
                status.IsAttacking = false;
                return;
            }

            float2 pos  = xform.Position.xz;
            float2 epos = perception.ClosestEnemy.Position;
            float dist  = math.distance(pos, epos);

            // Enemy in range -> commit. AttackTimerSystem does the charge/cooldown
            // and spawns the projectile (or applies melee) from here.
            if (dist <= attack.Range)
            {
                target.Info = perception.ClosestEnemy;
                target.Has = true;
                status.IsAttacking = true;
                status.InContactWithEnemy = true;
            }
            else
            {
                target.Has = false;
                status.IsAttacking = false;
                status.InContactWithEnemy = false;
            }
        }
    }
}
