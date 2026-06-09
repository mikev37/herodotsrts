using System;
using UnityEngine;

// ---------------------------------------------------------------------------
// Lives on the VISUAL PREFAB (the 3D model + Animator), NOT on the entity.
// The view manager hands it the entity's current AnimState each frame; it
// translates that into an Animator parameter, only when it actually changes.
//
// Animator Controller setup (one int parameter named "State"):
//   0 Idle | 1 Walk | 2 Block | 3 Attack | 4 Die
// Make transitions keyed off that int. Idle/Walk/Block loop; Attack can loop
// or one-shot; Die is one-shot (no exit) — the entity is destroyed after its
// deathAnimSeconds anyway.
//
// This decoupling is the whole point: simulation logic never references an
// Animator, so it stays Burst-compiled and parallel. Swapping the art, the
// rig, or even the whole animation backend touches only this layer.
// ---------------------------------------------------------------------------
public class UnitView : MonoBehaviour
{
    [SerializeField] private Animator animator;
    [SerializeField] private TeamColorTarget teamColor;

    private static readonly int StateParam = Animator.StringToHash("State");
    private int _lastState = -1;

    public float HP;

    private void Reset()
    {
        animator = GetComponentInChildren<Animator>();
        teamColor = GetComponentInChildren<TeamColorTarget>();
    }

    public void Bind() // called when pulled from the pool for reuse
    {
        _lastState = -1;
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (teamColor == null) teamColor = GetComponentInChildren<TeamColorTarget>();
    }

    public void Apply(AnimState state)
    {
        int s = (int)state;
        if (s == _lastState || animator == null) return;
        _lastState = s;
        animator.SetInteger(StateParam, s);
    }

    // Tints the team/commander color onto the prefab's marked slots only.
    // No-op (and materials untouched) if the prefab has no TeamColorTarget.
    public void SetTeamColor(Color color)
    {
        if (teamColor != null) teamColor.Apply(color);
    }

	internal void setHP(float current) {
        HP = current;
	}
}
