using Unity.Entities;
using UnityEngine;

// Put this on a GameObject in the scene (e.g. next to UnitFactory). It installs a
// rate manager that drives the SimulationSystemGroup at a FIXED real-time rate
// (30 Hz wall-clock via an accumulator), so SystemAPI.Time.DeltaTime is constant
// on every machine — a hard requirement for determinism.
//
// Behaviour:
//   * No LockstepNet present  -> ~30 ticks per real second at any frame rate
//     (single-player; the Phase 1/2 determinism test).
//   * LockstepNet present      -> the sim is FROZEN until "Start Game", then each
//     tick additionally requires that tick's networked turn (Phase 3).
//
// DIAGNOSTIC: if the sim is ticking at your frame rate (e.g. ~170/s) instead of
// 30/s, the rate manager is NOT installed — a SimulationSystemGroup with no rate
// manager free-runs one step per frame. This component now logs loudly on
// install/failure and re-installs if anything clears or replaces the manager.
public class LockstepBootstrap : MonoBehaviour
{
    private LockstepRateManager _manager;
    private SimulationSystemGroup _sim;

    private void Start() => Install();

    private void Install()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated)
        {
            Debug.LogError("[Lockstep] No ECS world — rate manager NOT installed; sim will free-run at frame rate.");
            return;
        }
        _sim = world.GetExistingSystemManaged<SimulationSystemGroup>();
        if (_sim == null)
        {
            Debug.LogError("[Lockstep] SimulationSystemGroup not found — rate manager NOT installed.");
            return;
        }
        _manager = new LockstepRateManager(LockstepConfig.FixedDt);
        _sim.RateManager = _manager;
        Debug.Log($"[Lockstep] Fixed-rate manager installed: {LockstepConfig.TickRate} ticks/s (dt {LockstepConfig.FixedDt:0.0000}s).");
    }

    private void Update()
    {
        // Watchdog: anything that clears or replaces the rate manager makes the
        // sim silently free-run at one tick per frame. Detect, complain, repair.
        if (_sim != null && !ReferenceEquals(_sim.RateManager, _manager))
        {
            Debug.LogWarning("[Lockstep] SimulationSystemGroup.RateManager was replaced or cleared — reinstalling.");
            Install();
        }
    }
}
