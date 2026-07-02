using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

public struct DepotInfo { public int StableId; public float2 Pos; }
public struct DepotRegistry : IComponentData { public NativeParallelMultiHashMap<int, DepotInfo> Map; }   // key = player

[UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
[UpdateAfter(typeof(StableIdRegistrySystem))]
public partial struct DepotRegistrySystem : ISystem
{
    private NativeParallelMultiHashMap<int, DepotInfo> _map;
    public void OnCreate(ref SystemState state)
    {
        _map = new NativeParallelMultiHashMap<int, DepotInfo>(128, Allocator.Persistent);
        state.EntityManager.AddComponentData(state.EntityManager.CreateEntity(), new DepotRegistry { Map = _map });
    }
    public void OnDestroy(ref SystemState state) { if (_map.IsCreated) _map.Dispose(); }
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _map.Clear();
        foreach (var (sid, player, xf) in
                 SystemAPI.Query<RefRO<StableId>, RefRO<Player>, RefRO<LocalTransform>>().WithAll<DepotTag>())
            _map.Add(player.ValueRO.Value, new DepotInfo
            { StableId = sid.ValueRO.Value, Pos = new float2(xf.ValueRO.Position.x, xf.ValueRO.Position.z) });
    }
}
