using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

// player -> that player's (multi-type) bank entity. Rebuilt each tick. Derived -> not snapshotted.
public struct PlayerBankRegistry : IComponentData { public NativeParallelHashMap<int, Entity> Map; }

[UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
[UpdateAfter(typeof(StableIdRegistrySystem))]
public partial struct PlayerBankRegistrySystem : ISystem
{
    private NativeParallelHashMap<int, Entity> _map;
    public void OnCreate(ref SystemState state)
    {
        _map = new NativeParallelHashMap<int, Entity>(32, Allocator.Persistent);
        state.EntityManager.AddComponentData(state.EntityManager.CreateEntity(), new PlayerBankRegistry { Map = _map });
    }
    public void OnDestroy(ref SystemState state) { if (_map.IsCreated) _map.Dispose(); }
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _map.Clear();
        foreach (var (player, e) in SystemAPI.Query<RefRO<Player>>().WithAll<PlayerBankTag>().WithEntityAccess())
            _map[player.ValueRO.Value] = e;
    }
}
