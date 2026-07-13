using System;
using UnityEngine;
using UnityEngine.Serialization;

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
    [SerializeField, FormerlySerializedAs("teamColor")] private PlayerColorTarget playerColor;

    [Tooltip("If the prefab has no PlayerColorTarget, tint ALL renderers with the player color as a " +
             "fallback (so units are colored out of the box). Turn off if a prefab should never be tinted " +
             "(e.g. a neutral resource node) — or add a PlayerColorTarget to tint only specific slots.")]
    [SerializeField] private bool autoTintAllRenderers = true;

    private static readonly int StateParam = Animator.StringToHash("State");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId     = Shader.PropertyToID("_Color");
    private int _lastState = -1;
    private MaterialPropertyBlock _mpb;
    private Renderer[] _fallbackRenderers;

    public float HP;

    private void Reset()
    {
        animator = GetComponentInChildren<Animator>();
        playerColor = GetComponentInChildren<PlayerColorTarget>();
    }

    public void Bind() // called when pulled from the pool for reuse
    {
        _lastState = -1;
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (playerColor == null) playerColor = GetComponentInChildren<PlayerColorTarget>();
    }

    public void Apply(AnimState state)
    {
        int s = (int)state;
        if (s == _lastState || animator == null) return;
        _lastState = s;
        animator.SetInteger(StateParam, s);
    }

    // Tints the player/commander color. Priority:
    //   1. a PlayerColorTarget on the prefab (tints only its marked slots) — the
    //      precise, artist-controlled path;
    //   2. otherwise, if autoTintAllRenderers, a MaterialPropertyBlock tint on
    //      every renderer — so units are colored with zero prefab setup.
    // Uses a property block (never instantiates materials, no per-unit leak).
    // A neutral owner (isNeutral) leaves the prefab's own materials untouched —
    // resource nodes / obstacles keep their authored look, not a gray wash.
    public void SetPlayerColor(Color color, bool isNeutral = false)
    {
        if (isNeutral) return;                       // neutral: keep authored materials
        if (playerColor != null) { playerColor.Apply(color); return; }
        if (!autoTintAllRenderers) return;

        _mpb ??= new MaterialPropertyBlock();
        if (_fallbackRenderers == null || _fallbackRenderers.Length == 0)
            _fallbackRenderers = GetComponentsInChildren<Renderer>(true);

        foreach (var r in _fallbackRenderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor(BaseColorId, color);   // URP lit
            _mpb.SetColor(ColorId, color);       // built-in / other
            r.SetPropertyBlock(_mpb);
        }
    }

	internal void setHP(float current) {
        HP = current;
	}
}
