using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// type -> all live (non-depleted) nodes of that type, for harvester re-acquire.
// Nodes are neutral (no Player). Rebuilt each tick. Derived -> not snapshotted.
public struct NodeInfo { public int StableId; public float2 Pos; }
public struct NodeRegistry : IComponentData { public NativeParallelMultiHashMap<int, NodeInfo> Map; }   // key = (int)ResourceType

[UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
[UpdateAfter(typeof(StableIdRegistrySystem))]
public partial struct NodeRegistrySystem : ISystem
{
    private NativeParallelMultiHashMap<int, NodeInfo> _map;
    public void OnCreate(ref SystemState state)
    {
        _map = new NativeParallelMultiHashMap<int, NodeInfo>(256, Allocator.Persistent);
        state.EntityManager.AddComponentData(state.EntityManager.CreateEntity(), new NodeRegistry { Map = _map });
    }
    public void OnDestroy(ref SystemState state) { if (_map.IsCreated) _map.Dispose(); }
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _map.Clear();
        foreach (var (sid, node, bank, xf) in
                 SystemAPI.Query<RefRO<StableId>, RefRO<NodeTag>, RefRO<ResourceBank>, RefRO<LocalTransform>>())
        {
            if (bank.ValueRO.Amounts[node.ValueRO.Yield] <= 0) continue;   // depleted -> not a target
            _map.Add((int)node.ValueRO.Yield, new NodeInfo
            { StableId = sid.ValueRO.Value, Pos = new float2(xf.ValueRO.Position.x, xf.ValueRO.Position.z) });
        }
    }
}
