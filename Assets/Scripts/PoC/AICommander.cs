using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// AI COMMANDER — same verbs, no mouse. Every few seconds it throws its whole
// army at the nearest enemy. Override Tick() for smarter behavior.
// ===========================================================================
public class AICommander : Commander
{
    [Header("AI")]
    [SerializeField] private float decisionInterval = 3f;

    [Header("AI debug (runtime, read-only)")]
    public float nextDecisionIn;
    public int commandedUnits;
    public string lastDecision = "(none)";

    private float _timer;

    private void Update()
    {
        if (!WorldOk) return;
        _timer -= Time.deltaTime;
        nextDecisionIn = Mathf.Max(0f, _timer);
        if (_timer > 0f) return;
        _timer = decisionInterval;
        Tick();
    }

    protected virtual void Tick()
    {
        var mine = GetTeamUnits();
        commandedUnits = mine.Count;
        if (mine.Count == 0) { lastDecision = "no units"; return; }

        float2 center = float2.zero; int valid = 0;
        foreach (var e in mine)
            if (Em.Exists(e)) { center += Pos(e); valid++; }
        if (valid == 0) { lastDecision = "no live units"; return; }
        center /= valid;

        var entities = AllUnitsQuery.ToEntityArray(Allocator.Temp);
        var teams = AllUnitsQuery.ToComponentDataArray<Team>(Allocator.Temp);
        var xforms = AllUnitsQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        Entity tgt = Entity.Null; float2 tgtPos = default; float bestD = float.MaxValue;
        for (int i = 0; i < entities.Length; i++)
        {
            if (teams[i].Value == team) continue;
            float2 p = new float2(xforms[i].Position.x, xforms[i].Position.z);
            float d = math.distancesq(center, p);
            if (d < bestD) { bestD = d; tgt = entities[i]; tgtPos = p; }
        }
        entities.Dispose(); teams.Dispose(); xforms.Dispose();

        if (tgt != Entity.Null) { IssueAttack(mine, tgt, tgtPos); lastDecision = $"attack {tgt.Index}"; }
        else lastDecision = "no enemies";
    }

    private float2 Pos(Entity e)
    {
        var t = Em.GetComponentData<LocalTransform>(e);
        return new float2(t.Position.x, t.Position.z);
    }
}
