using System;
using System.Collections.Generic;
using UnityEngine;

// ===========================================================================
// One ability, authored as a single asset. The designer edits everything in one
// place: the shape/anchor/mode, and the LIST of modifiers — each of which can
// target a different field in a different way (one an offset, one over-time,
// one a flag), all applied together when the ability hits a unit.
// ===========================================================================
[CreateAssetMenu(menuName = "MarbleCombat/Ability Definition")]
public class AbilityDefinition : ScriptableObject
{
    public string displayName = "Ability";

    [Header("Targeting")]
    public ShapeType shape = ShapeType.Circle;
    [Tooltip("Circle radius.")] public float radius = 5f;
    [Tooltip("Line width (full).")] public float width = 2f;
    [Tooltip("Line length (forward from the anchor).")] public float length = 10f;

    [Tooltip("Hero = centered on (and following) the caster. WorldPoint = where you click.")]
    public AnchorType anchor = AnchorType.Hero;

    [Tooltip("CastOnce = stamp everyone in the shape now. PersistentArea = while inside; removed on leave.")]
    public ApplyMode applyMode = ApplyMode.CastOnce;

    [Tooltip("Who it affects, relative to the caster's team.")]
    public AffectFilter affects = AffectFilter.Enemies;

    [Tooltip("Seconds the area persists (PersistentArea only).")]
    public float lifetime = 5f;

    [Tooltip("Cooldown before it can be cast again.")]
    public float cooldown = 1f;

    [Header("Effects (all applied together)")]
    public List<StatModifierDef> modifiers = new();
}

// One effect within an ability. Inline & serializable so the whole ability is
// edited in a single inspector.
[Serializable]
public class StatModifierDef
{
    public ModTarget target = ModTarget.Health;

    [Header("Numeric (ignored for flag targets)")]
    [Tooltip("Amount. With PerSecond this is per second; with Instant it's applied once.")]
    public float delta = -10f;
    public ModMode mode = ModMode.Instant;
    [Tooltip("ON = temporary offset that reverts when it ends (buffs). OFF = permanent change to the value (health damage/heal).")]
    public bool revert = false;

    [Header("Cap (clamps the resulting value)")]
    public CapMode capMode = CapMode.Min;
    public CapRef capRef = CapRef.Absolute;
    [Tooltip("Cap value. With Base, it's added to the base/Max (e.g. Max+0 = full health).")]
    public float capValue = 0f;

    [Header("Flag targets")]
    [Tooltip("For Flag* targets: set the behavior flag to this while active (reverts after).")]
    public bool boolValue = true;

    [Header("Lifetime")]
    [Tooltip("Seconds this modifier stays on a unit (CastOnce). PersistentArea keeps it alive while inside.")]
    public float duration = 1f;

    [Tooltip("Optional view effect to attach while active (wiring pending in the view layer).")]
    public GameObject viewEffect;
}
