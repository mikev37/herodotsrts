using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ===========================================================================
// WAYPOINT SYSTEM — advances shift-queued destinations. When a unit has queued
// Waypoints and no active MoveTarget (the previous leg arrived or it was idle),
// pop the front into a SOFT slotless move: BehaviorSystem's direct-drive tier
// takes it straight there, and survival/engagement tiers can still interrupt on
// an attack-move leg. Queued chains are per-unit (no formation slots); a fresh
// unqueued order clears the buffer (CommandSystem).
//
// Arrival is detected HERE: slotless soft moves have no FormationSystem arrival
// handling, so this system clears the leg within ArriveRadius and pops the next.
// ===========================================================================
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(CommandApplySystem))]
[UpdateBefore(typeof(BehaviorSystem))]
public partial struct WaypointSystem : ISystem
{
    private const float ArriveRadius = 1.5f;

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (wps, move, xf) in
                 SystemAPI.Query<DynamicBuffer<Waypoint>, RefRW<MoveTarget>, RefRO<LocalTransform>>()
                          .WithNone<Dead>())
        {
            ref var mv = ref move.ValueRW;
            float2 pos = new float2(xf.ValueRO.Position.x, xf.ValueRO.Position.z);

            // Complete the current slotless leg on arrival (only legs this system
            // issued: FormationId 0 + the buffer present).
            if (mv.HasTarget && mv.FormationId == 0 &&
                math.distance(pos, mv.Value) <= ArriveRadius)
                mv.HasTarget = false;

            if (wps.Length == 0 || mv.HasTarget) continue;

            var wp = wps[0];
            wps.RemoveAt(0);
            mv.Value = wp.Pos;
            mv.HasTarget = true;
            mv.AttackMove = true;                          // soft: direct-drive + combat tiers can interrupt
            mv.FormationId = 0;                            // slotless — individual travel
        }
    }
}
