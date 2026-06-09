using Unity.Burst;

// ===========================================================================
// Assembly-wide deterministic Burst float mode (Burst 1.8+, 64-bit only).
//
// This is THE switch that lets the existing float-based simulation run under
// deterministic lockstep without a fixed-point rewrite. It forces every Burst
// job in this assembly to:
//   - use deterministic implementations of math functions (sqrt, trig, etc.),
//   - disable platform-specific float optimizations (FMA contraction, etc.),
//   - flush subnormals to zero on all platforms.
//
// Trade-off: some float optimizations are disabled, so Burst code is a bit
// slower. With only hundreds of units that's a non-issue.
//
// IMPORTANT: this applies to the WHOLE assembly. Since all our gameplay is in
// Assembly-CSharp right now, that's fine for the test. For production, move the
// simulation into its own .asmdef so only sim code pays this cost (view/UI code
// doesn't need determinism).
//
// A job that sets its own FloatMode (e.g. FloatMode.Fast) would OVERRIDE this —
// Phase 0 confirmed there are none.
// ===========================================================================
[assembly: BurstCompile(FloatMode = FloatMode.Deterministic, FloatPrecision = FloatPrecision.High)]
