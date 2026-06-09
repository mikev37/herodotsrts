using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

// ===========================================================================
// Stable, deterministic unit identity.
//
// Raw Entity values differ between worlds/clients/runs, so a recorded order like
// "units 4,5,6 move here" must reference units by something stable. StableId is
// assigned in spawn order (deterministic — see UnitManager's formation spawn), so
// id N is the same logical unit on every machine and in every replay.
// ===========================================================================

public struct StableId : IComponentData { public int Value; }

// StableId -> Entity, rebuilt every tick. Command application uses it to resolve
// recorded ids back to live entities. Rebuilding each tick keeps it correct as
// units die; cheap for hundreds of units.
public struct StableIdRegistry : IComponentData
{
    public NativeParallelHashMap<int, Entity> Map;
}

[UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
[UpdateAfter(typeof(SimClockSystem))]
public partial struct StableIdRegistrySystem : ISystem
{
    private NativeParallelHashMap<int, Entity> _map;

    public void OnCreate(ref SystemState state)
    {
        _map = new NativeParallelHashMap<int, Entity>(4096, Allocator.Persistent);
        state.EntityManager.AddComponentData(state.EntityManager.CreateEntity(),
            new StableIdRegistry { Map = _map });
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_map.IsCreated) _map.Dispose();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _map.Clear();
        foreach (var (sid, e) in SystemAPI.Query<RefRO<StableId>>().WithEntityAccess())
            _map.TryAdd(sid.ValueRO.Value, e);
    }
}
