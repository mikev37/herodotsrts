using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// COMMANDER — the shared order API. The player UI and the AI are both just
// Commanders that decide WHICH units get WHICH order; the mechanics of writing
// the order into ECS live here once. Add a new AI personality by subclassing
// and overriding Tick(); it gets the exact same verbs the player has.
//
// Abstract, so it won't appear in Add Component — only PlayerCommander and
// AICommander do. Queries are created once (cached).
// ===========================================================================
public abstract class Commander : MonoBehaviour
{
    [Header("Commander")]
    [SerializeField] protected int team = 0;
    public int Team => team;

    [Header("Debug (runtime, read-only)")]
    [Tooltip("Last order this commander issued.")]
    public string lastOrder = "(none)";
    [Tooltip("True once the ECS world was found.")]
    public bool worldReady;

    protected EntityManager Em;
    protected EntityQuery AllUnitsQuery;     // UnitTag + Team + LocalTransform
    private bool _ready;

    protected virtual void Start()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) { worldReady = false; return; }
        Em = world.EntityManager;
        AllUnitsQuery = Em.CreateEntityQuery(
            ComponentType.ReadOnly<UnitTag>(),
            ComponentType.ReadOnly<Team>(),
            ComponentType.ReadOnly<LocalTransform>());
        worldReady = _ready = true;
    }

    protected bool WorldOk => _ready && Em.World != null && Em.World.IsCreated;

    // --- order verbs (the abstraction the AI shares) ----------------------
    protected void IssueMove(List<Entity> units, float2 dest, bool attackMove = false)
    {
        if (!WorldOk) return;
        foreach (var e in units)
        {
            if (!Em.Exists(e)) continue;
            Em.SetComponentData(e, new MoveTarget { Value = dest, HasTarget = true, AttackMove = attackMove });
            Em.SetComponentData(e, new AttackOrder { Has = false });
        }
        lastOrder = $"{(attackMove ? "AttackMove" : "Move")} {units.Count} -> ({dest.x:0.#},{dest.y:0.#})";
    }

    protected void IssueAttack(List<Entity> units, Entity target, float2 targetPos)
    {
        if (!WorldOk) return;
        foreach (var e in units)
        {
            if (!Em.Exists(e)) continue;
            Em.SetComponentData(e, new AttackOrder { Target = target, Has = true });
            Em.SetComponentData(e, new MoveTarget { HasTarget = false });
        }
        lastOrder = $"Attack {units.Count} -> entity {target.Index}";
    }

    protected void IssueStop(List<Entity> units)
    {
        if (!WorldOk) return;
        foreach (var e in units)
        {
            if (!Em.Exists(e)) continue;
            Em.SetComponentData(e, new MoveTarget { HasTarget = false });
            Em.SetComponentData(e, new AttackOrder { Has = false });
        }
        lastOrder = $"Stop {units.Count}";
    }

    // --- helpers ----------------------------------------------------------
    protected List<Entity> GetTeamUnits()
    {
        var list = new List<Entity>();
        if (!WorldOk) return list;
        var entities = AllUnitsQuery.ToEntityArray(Allocator.Temp);
        var teams = AllUnitsQuery.ToComponentDataArray<Team>(Allocator.Temp);
        for (int i = 0; i < entities.Length; i++)
            if (teams[i].Value == team) list.Add(entities[i]);
        entities.Dispose(); teams.Dispose();
        return list;
    }

}
