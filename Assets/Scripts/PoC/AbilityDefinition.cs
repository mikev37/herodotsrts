using System;
using System.Collections.Generic;
using UnityEngine;

// ===========================================================================
// One ability, authored as a single asset. The designer edits everything in one
// place: shape/anchor/mode, the cast economy (range, charge-up, cooldown, mana,
// commander resources), an optional unit/building spawn, and the modifiers.
//
// Modifiers are split into TWO lists so each row only shows fields that mean
// something: numeric modifiers (delta/mode/revert/cap) and flag modifiers
// (which behavior flag, on/off). Assets authored against the old single
// `modifiers` list are migrated automatically (OnValidate in the editor, and
// again at runtime registration as a safety net for unre-saved assets).
//
// CAST PIPELINE (all deterministic, all tick-based):
//   commit (CommandApplySystem, execution tick): slot/cooldown/mana/resources/
//     range/spawn-placement are checked; on success costs are consumed, the
//     cooldown starts, and a PendingCast is armed on the caster.
//   fire (AbilityCastSystem, FireTick = commit + chargeUp ticks): the field is
//     spawned from the caster's geometry AT THE FIRE TICK; spawn-unit abilities
//     spawn their unit; the cast VFX event is emitted. chargeUp = 0 fires the
//     same tick it commits.
// ===========================================================================
[CreateAssetMenu(menuName = "MarbleCombat/Ability Definition")]
public class AbilityDefinition : ScriptableObject
{
    public string displayName = "Ability";

    [Header("Targeting")]
    public ShapeType shape = ShapeType.Circle;
    [Tooltip("Circle radius.")] public float radius = 5f;
    [Tooltip("Line width (full).")] public float width = 2f;
    [Tooltip("Line length. Lines run FROM the caster TOWARD the clicked point (WorldPoint) or along the caster's facing (Hero).")]
    public float length = 10f;

    [Tooltip("Hero = centered on (and following) the caster. WorldPoint = where you click.")]
    public AnchorType anchor = AnchorType.Hero;

    [Tooltip("CastOnce = stamp everyone in the shape now. PersistentArea = while inside; removed on leave.")]
    public ApplyMode applyMode = ApplyMode.CastOnce;

    [Tooltip("Who it affects, relative to the caster's player.")]
    public AffectFilter affects = AffectFilter.Enemies;

    [Tooltip("Seconds the area persists (PersistentArea only).")]
    public float lifetime = 5f;

    [Header("Cast economy")]
    [Tooltip("Max distance from the caster to the clicked point for WorldPoint abilities; farther casts fizzle at commit. 0 = unlimited. Ignored for Hero anchors.")]
    public float castRange = 0f;
    [Tooltip("Seconds of wind-up between commit and fire (like an attack's charge-up). 0 = fires the same tick.")]
    public float chargeUp = 0f;
    [Tooltip("Cooldown before it can be cast again. Measured from the FIRE tick.")]
    public float cooldown = 1f;
    [Tooltip("Mana consumed from the caster at commit; the cast fails (nothing consumed) if the caster has less.")]
    public float manaCost = 0f;

    [Header("Commander resource cost (player bank)")]
    public int costGold = 0;
    public int costWood = 0;
    public int costFood = 0;

    [Header("Spawn (optional)")]
    [Tooltip("Unit or building spawned at the cast point when the ability fires. MUST be in the RosterDefinition asset. Buildings validate their footprint at commit; units need a passable cell.")]
    public UnitDefinition spawnUnit;
    [Tooltip("PersistentArea only: the field follows the spawned unit (banner/totem). The field dies when the unit dies, and the unit dies when the field's lifetime expires.")]
    public bool anchorFieldToSpawn = false;

    [Header("View effects (visual only — never touch the sim)")]
    [Tooltip("Instantiated once at the cast center (e.g. an explosion). Auto-destroyed after castEffectSeconds.")]
    public GameObject castEffectPrefab;
    [Tooltip("Seconds before the cast effect is destroyed.")]
    public float castEffectSeconds = 3f;
    [Tooltip("Attached to each affected unit's view while a modifier from this ability is active on it (e.g. a poison emitter); destroyed when the modifier ends.")]
    public GameObject attachedEffectPrefab;

    [Header("Numeric effects (deltas to stats / health)")]
    public List<NumericModifierDef> numericModifiers = new();
}

// Authoring-side enums: same members, same order as the corresponding ModTarget
// ranges, so the bake is a plain offset cast. Splitting them is what keeps each
// inspector row free of fields that don't apply to it.
public enum NumericTarget : byte
{
    Health, Speed, TurnSpeed, MeleeRange, AttackDamage, Armor, Shield,
    Aggression, Looseness, Separation,
    CombatSpacing, AttackNearbyRange, AvoidMeleeRange, PursueDistance,
    CohesionRadius, RetreatHealthPct, ReEngageHealthPct,
}
public enum FlagTarget : byte { FormWall, StandBehindFriend, AvoidMelee, AdvanceIndividual, AdvanceOnEnemy, SeparateIdle }

// One numeric effect within an ability.
[Serializable]
public class NumericModifierDef
{
    public NumericTarget target = NumericTarget.Health;
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
}