using Unity.Entities;
using UnityEngine;

// Put this on a GameObject in the scene (e.g. next to UnitManager). It switches
// the SimulationSystemGroup to a FIXED timestep so SystemAPI.Time.DeltaTime is a
// constant on every machine — a hard requirement for determinism, and it makes
// every sim system that reads DeltaTime deterministic at once.
//
// NOTE: FixedRateSimpleManager advances the group one fixed step per frame. For
// the real networked loop (Phase 3) you'd instead drive the group manually and
// gate each tick on all players' inputs having arrived. For the determinism test
// this is all we need: per-tick state is reproducible regardless of frame rate.
public class LockstepBootstrap : MonoBehaviour {
    private void Start() {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;
        var sim = world.GetExistingSystemManaged<SimulationSystemGroup>();
        if (sim != null)
            sim.RateManager = new Unity.Entities.RateUtils.FixedRateSimpleManager(LockstepConfig.FixedDt);
    }
}

