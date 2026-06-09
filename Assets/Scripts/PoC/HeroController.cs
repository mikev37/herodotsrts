using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// Shared flag so the commander ignores the right-click that activates an ability.
public static class HeroAbilityInput { public static bool AbilityArmed; }

// ===========================================================================
// HERO CONTROLLER — the hero's input/HUD layer. The hero itself is a normal UNIT
// entity (selected/moved/ordered/hit like any unit, via the Commander). This
// only handles ABILITIES:
//   * QWER arms ability slots 0..3.
//   * Right-click activates the armed ability: anchored on the hero, or at the
//     clicked ground point, depending on the ability's anchor.
//   * Casting spawns an AbilityField entity (shape + modifier payloads); the
//     ability systems do the rest, recipient-side.
// The "command aura" (Charge/Hold) is now just a hero-anchored PersistentArea
// ability asset — no special-case code here.
// ===========================================================================
public class HeroController : MonoBehaviour
{
    [Header("Config")]
    [SerializeField] private int team = 0;
    [Tooltip("Ability slots, armed with Q/W/E/R in order.")]
    [SerializeField] private AbilityDefinition[] abilities = new AbilityDefinition[4];
    [SerializeField] private Camera cam;

    [Header("Debug (runtime, read-only)")]
    public bool worldReady, heroFound;
    public int armedIndex = -1;
    public float heroHealth, heroMaxHealth;

    private EntityManager _em;
    private EntityQuery _heroQuery;
    private Entity _hero = Entity.Null;
    private float[] _nextReady = new float[8];

    private static readonly KeyCode[] Slots = { KeyCode.Q, KeyCode.W, KeyCode.E, KeyCode.R };

    private void Start()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        worldReady = world != null && world.IsCreated;
        cam = Camera.main;
        if (!worldReady) { Debug.LogWarning("[HeroController] No ECS world."); return; }
        _em = world.EntityManager;
        if (cam == null) cam = Camera.main;
        _heroQuery = _em.CreateEntityQuery(ComponentType.ReadOnly<HeroTag>(), ComponentType.ReadOnly<Team>());
    }

    private void Update()
    {
        if (!worldReady || _em.World == null || !_em.World.IsCreated) return;

        // Reflect armed state for the commander BEFORE handling this frame's click.
        HeroAbilityInput.AbilityArmed = armedIndex >= 0;

        if (!_em.Exists(_hero) || !_em.HasComponent<HeroTag>(_hero))
        {
            _hero = FindHero();
            heroFound = _hero != Entity.Null;
            if (!heroFound) { armedIndex = -1; HeroAbilityInput.AbilityArmed = false; return; }
        }

        for (int i = 0; i < Slots.Length; i++)
            if (Input.GetKeyDown(Slots[i])) armedIndex = (armedIndex == i) ? -1 : i;

        if (armedIndex >= 0 && Input.GetMouseButtonDown(1))
        {
            TryCast(armedIndex);
            armedIndex = -1;
        }

        if (_em.HasComponent<Health>(_hero))
        {
            var hp = _em.GetComponentData<Health>(_hero);
            heroHealth = hp.Current; heroMaxHealth = hp.Max;
        }
    }

    private void TryCast(int slot)
    {
        if (slot < 0 || slot >= abilities.Length) return;
        var ad = abilities[slot];
        if (ad == null) return;
        if (Time.time < _nextReady[slot]) return;     // on cooldown

        var heroXform = _em.GetComponentData<LocalTransform>(_hero);
        float2 heroPos = new float2(heroXform.Position.x, heroXform.Position.z);
        float3 fwd3 = math.forward(heroXform.Rotation);
        float2 heroFwd = math.normalizesafe(new float2(fwd3.x, fwd3.z), new float2(0f, 1f));

        float2 center, dir;
        if (ad.anchor == AnchorType.Hero)
        {
            center = heroPos; dir = heroFwd;
        }
        else
        {
            if (!GroundPoint(out float2 gp)) return;   // need a valid click point
            center = gp;
            dir = math.normalizesafe(gp - heroPos, heroFwd);
        }

        var e = _em.CreateEntity();
        _em.AddComponentData(e, new AbilityField
        {
            FieldId = e.Index,
            Team = team,
            Affects = ad.affects,
            Shape = ad.shape,
            Radius = ad.radius,
            Width = ad.width,
            Length = ad.length,
            Center = center,
            Dir = dir,
            Anchor = ad.anchor,
            AnchorEntity = ad.anchor == AnchorType.Hero ? _hero : Entity.Null,
            Mode = ad.applyMode,
            Lifetime = ad.lifetime,
            RefreshWindow = 0.2f,
        });
        var buf = _em.AddBuffer<FieldModifier>(e);
        foreach (var m in ad.modifiers)
            buf.Add(new FieldModifier
            {
                Target = m.target,
                Delta = m.delta,
                Mode = m.mode,
                Revert = (byte)(m.revert ? 1 : 0),
                BoolValue = (byte)(m.boolValue ? 1 : 0),
                CapMode = m.capMode,
                CapRef = m.capRef,
                CapValue = m.capValue,
                Duration = m.duration,
            });

        _nextReady[slot] = Time.time + ad.cooldown;
    }

    private bool GroundPoint(out float2 p)
    {
        p = default;
        if (cam == null) return false;
        var ray = cam.ScreenPointToRay(Input.mousePosition);
        if (math.abs(ray.direction.y) < 1e-5f) return false;
        float t = -ray.origin.y / ray.direction.y;          // intersect y=0 plane
        if (t < 0f) return false;
        var hit = ray.origin + ray.direction * t;
        p = new float2(hit.x, hit.z);
        return true;
    }

    private Entity FindHero()
    {
        var es = _heroQuery.ToEntityArray(Allocator.Temp);
        var ts = _heroQuery.ToComponentDataArray<Team>(Allocator.Temp);
        Entity found = Entity.Null;
        for (int i = 0; i < es.Length; i++) if (ts[i].Value == team) { found = es[i]; break; }
        es.Dispose(); ts.Dispose();
        return found;
    }

    private void OnGUI()
    {
        if (!heroFound) return;
        string armed = armedIndex >= 0 && abilities[armedIndex] != null
            ? abilities[armedIndex].displayName : "none";
        GUI.Label(new Rect(10, 10, 600, 20),
            $"Hero[{team}]  HP {heroHealth:0}/{heroMaxHealth:0}  |  armed: {armed}  (Q/W/E/R arm, right-click cast)");
    }
}
