using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ---------------------------------------------------------------------------
// Decides which animation each unit should be in, from pure sim data. The view
// layer turns this enum into an Animator parameter; the sim never talks to an
// Animator.
//
// Consistency choices (these were the bugs):
//  * DEATH locks on health<=0 (the Dead tag is queued via an end-of-frame ECB,
//    so it isn't present on the death frame and can't be relied on here).
//  * ENGAGED uses the stable "is my target within melee range" test, NOT the
//    physical-overlap CombatStatus.InContactWithEnemy, which flickers on/off as
//    separation jostles units across the contact boundary (that flicker is what
//    dropped attacks back to Idle).
//  * ATTACK vs BLOCK: an engaged melee unit attacks; a shield (FormWall)
//    that is holding the line braces (Block) instead.
//  * MOVING is "the behavior gave me a destination I'm not basically standing
//    on", not raw steering velocity (which includes separation jostle).
// ---------------------------------------------------------------------------
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ProjectileSystem))]
public partial struct AnimationStateSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        new AnimJob { MovingDistance = 0.5f }.ScheduleParallel();
    }

    [BurstCompile]
    [WithNone(typeof(Dead))]
    private partial struct AnimJob : IJobEntity
    {
        public float MovingDistance;

        private void Execute(ref UnitAnim anim,
                             in LocalTransform xform,
                             in Health health,
                             in CombatStatus status,
                             in Ranged ranged,
                             in CombatTarget target,
                             in BehaviorFlags flags,
                             in DesiredDestination dest)
        {
            // Dying this frame (health 0, Dead tag still queued) -> lock to Die.
            if (health.Current <= 0f) { anim.State = AnimState.Die; return; }

            float2 pos = new float2(xform.Position.x, xform.Position.z);
            bool moving = dest.Has &&
                          math.distancesq(pos, dest.Value) > MovingDistance * MovingDistance;


            if (status.IsAttacking)
            {
               
                anim.State =  AnimState.Attack;
                return;
            }

            bool shieldHolding = (flags.Value & (uint)BehaviorFlag.FormWall) != 0 && !moving;
            anim.State = moving ? AnimState.Walk : AnimState.Idle;
        }
    }
}
