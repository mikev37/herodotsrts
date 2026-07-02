using Unity.Burst;
using Unity.Entities;

// Per-node despawn: a node flagged DespawnWhenEmpty gets a Despawn timer (its husk
// linger) once its bank empties, so it crumbles after the husk shows and is removed
// by DespawnSystem. Unflagged nodes just stay as a permanent husk/obstacle.
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ResourceBankSystem))]
public partial struct NodeDepletionSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
        => state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                           .CreateCommandBuffer(state.WorldUnmanaged);
        foreach (var (node, bank, e) in
                 SystemAPI.Query<RefRO<NodeTag>, RefRO<ResourceBank>>().WithNone<Despawn>().WithEntityAccess())
        {
            if (node.ValueRO.DespawnWhenEmpty == 0) continue;
            if (bank.ValueRO.Amounts[node.ValueRO.Yield] > 0) continue;
            ecb.AddComponent(e, new Despawn { Seconds = node.ValueRO.HuskLinger });
        }
    }
}
