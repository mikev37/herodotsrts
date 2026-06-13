using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// PLAYER COMMANDER — classic RTS input on the shared verbs. Left-drag box
// select, right-click ground = move, right-click enemy = attack.
//
// Absorbs the former HeroController's ability input: Q/W/E/R arms a slot of the
// CASTER (the selected unit with the most abilities — heroes in practice);
// right-click then casts at the clicked point / on the caster, via the
// IssueAbility verb. Cooldowns are sim-state (AbilityCooldowns, tick-based);
// this class only READS them for the HUD and to avoid arming dead slots.
// ===========================================================================
public class PlayerCommander : Commander
{
    [Header("Buildings")]
    [Tooltip("Building placed with B. Must appear in this team's UnitManager roster (countPerTeam 0 is fine).")]
    [SerializeField] private BuildingDefinition placeBuilding;

    [Header("Player debug (runtime, read-only)")]
    public int selectedCount;
    public bool dragging;
    public int armedIndex = -1;

    private EntityQuery _selectedQuery;
    private EntityQuery _clockQuery2;
    private EntityQuery _buildingQuery;
    private EntityQuery _resourceQuery;
    private Vector2 _dragStart;

    private static readonly KeyCode[] SlotKeys = { KeyCode.Q, KeyCode.W, KeyCode.E, KeyCode.R };

    protected override void Start()
    {
        base.Start();
        if (!worldReady) return;
        _selectedQuery = Em.CreateEntityQuery(ComponentType.ReadOnly<Selected>());
        _clockQuery2 = Em.CreateEntityQuery(ComponentType.ReadOnly<SimClock>());
        _buildingQuery = Em.CreateEntityQuery(
            ComponentType.ReadOnly<BuildingTag>(),
            ComponentType.ReadOnly<Team>(),
            ComponentType.ReadOnly<StableId>(),
            ComponentType.ReadOnly<LocalTransform>());
        _resourceQuery = Em.CreateEntityQuery(ComponentType.ReadOnly<ResourcePoolTag>());
    }

    private void Update()
    {
        if (!WorldOk) return;

        if (Input.GetMouseButtonDown(0)) { _dragStart = Input.mousePosition; dragging = true; }
        if (Input.GetMouseButtonUp(0) && dragging) { dragging = false; BoxSelect(); }

        // Q/W/E/R toggles an armed ability slot (only if the current caster has it).
        for (int i = 0; i < SlotKeys.Length; i++)
            if (Input.GetKeyDown(SlotKeys[i]))
                armedIndex = (armedIndex == i) ? -1 : (CasterHasSlot(i) ? i : -1);

        if (Input.GetKeyDown(KeyCode.S))
            SaveNow();

        // B = place the configured building under the cursor; N = demolish the
        // nearest own building. Both are commands (tick-scheduled, validated at
        // apply), replacing the old BuildingManager's direct entity creation.
        if (Input.GetKeyDown(KeyCode.B) && GroundPoint(out float2 buildPos))
            TryPlaceBuilding(buildPos);
        if (Input.GetKeyDown(KeyCode.N) && GroundPoint(out float2 demoPos))
            TryDemolishNearest(demoPos);

        if (Input.GetMouseButtonDown(1))
        {
            if (armedIndex >= 0) { TryCastArmed(); armedIndex = -1; }
            else RightClick();
        }

        selectedCount = _selectedQuery.CalculateEntityCount();
    }

    // --- ability casting -----------------------------------------------------

    // The caster is the selected unit with the most non-empty ability slots
    // (lowest StableId wins ties). Returns Entity.Null when nothing selected has
    // abilities.
    private Entity FindCaster()
    {
        Entity best = Entity.Null; int bestCount = 0; int bestSid = int.MaxValue;
        var arr = _selectedQuery.ToEntityArray(Allocator.Temp);
        foreach (var e in arr)
        {
            if (!Em.HasComponent<AbilitySlots>(e) || !Em.HasComponent<StableId>(e)) continue;
            var ids = Em.GetComponentData<AbilitySlots>(e).Ids;
            int n = 0;
            for (int s = 0; s < 4; s++) if (ids[s] >= 0) n++;
            if (n == 0) continue;
            int sid = Em.GetComponentData<StableId>(e).Value;
            if (n > bestCount || (n == bestCount && sid < bestSid))
            {
                best = e; bestCount = n; bestSid = sid;
            }
        }
        arr.Dispose();
        return best;
    }

    private bool CasterHasSlot(int slot)
    {
        var c = FindCaster();
        return c != Entity.Null && Em.GetComponentData<AbilitySlots>(c).Ids[slot] >= 0;
    }

    private void TryCastArmed()
    {
        var caster = FindCaster();
        if (caster == Entity.Null) { lastOrder = "(cast ignored: no caster selected)"; return; }

        // Anchor decides whether we need a ground point; hero-anchored abilities
        // cast on the caster, so the click point is ignored (but harmless to send).
        float2 castPos = default;
        if (!GroundPoint(out castPos))
        {
            var xf = Em.GetComponentData<LocalTransform>(caster);
            castPos = new float2(xf.Position.x, xf.Position.z);
        }
        IssueAbility(caster, armedIndex, castPos);
    }

    // --- buildings ------------------------------------------------------------

    private void TryPlaceBuilding(float2 pos)
    {
        if (placeBuilding == null)
        {
            Debug.LogWarning("[Building] B ignored: no placeBuilding assigned on PlayerCommander.");
            lastOrder = "(B ignored: no placeBuilding assigned)";
            return;
        }
        int defId = UnitManager.Instance != null ? UnitManager.Instance.GetDefId(team, placeBuilding) : -1;
        if (defId < 0)
        {
            Debug.LogWarning($"[Building] B ignored: '{placeBuilding.displayName}' is not in team {team}'s UnitManager roster. " +
                             "Add a roster entry for it (countPerTeam 0 is fine) — the roster index IS the network def id.");
            lastOrder = $"(B ignored: '{placeBuilding.displayName}' not in team {team} roster)";
            return;
        }
        IssuePlaceBuilding(defId, pos);
    }

    private void TryDemolishNearest(float2 pos)
    {
        var entities = _buildingQuery.ToEntityArray(Allocator.Temp);
        var teams = _buildingQuery.ToComponentDataArray<Team>(Allocator.Temp);
        var sids = _buildingQuery.ToComponentDataArray<StableId>(Allocator.Temp);
        var xforms = _buildingQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < entities.Length; i++)
        {
            if (teams[i].Value != team) continue;
            float d = math.distancesq(pos, new float2(xforms[i].Position.x, xforms[i].Position.z));
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best >= 0) IssueDemolishBuilding(sids[best].Value);
        else lastOrder = "(N ignored: no own building found)";

        entities.Dispose(); teams.Dispose(); sids.Dispose(); xforms.Dispose();
    }

    // --- selection / orders ----------------------------------------------------

    private void BoxSelect()
    {
        var cam = Camera.main; if (cam == null) return;
        Vector2 end = Input.mousePosition;
        var rect = Rect.MinMaxRect(
            Mathf.Min(_dragStart.x, end.x), Mathf.Min(_dragStart.y, end.y),
            Mathf.Max(_dragStart.x, end.x), Mathf.Max(_dragStart.y, end.y));

        var entities = AllUnitsQuery.ToEntityArray(Allocator.Temp);
        var teams = AllUnitsQuery.ToComponentDataArray<Team>(Allocator.Temp);
        var xforms = AllUnitsQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        for (int i = 0; i < entities.Length; i++)
        {
            bool sel = false;
            if (teams[i].Value == team)
            {
                Vector3 sp = cam.WorldToScreenPoint(xforms[i].Position);
                sel = sp.z > 0 && rect.Contains(new Vector2(sp.x, sp.y));
            }
            if (Em.IsComponentEnabled<Selected>(entities[i]) != sel)
                Em.SetComponentEnabled<Selected>(entities[i], sel);
        }
        entities.Dispose(); teams.Dispose(); xforms.Dispose();
        armedIndex = -1;   // selection changed; disarm
    }

    private void RightClick()
    {
        var cam = Camera.main; if (cam == null) return;
        var entities = AllUnitsQuery.ToEntityArray(Allocator.Temp);
        var teams = AllUnitsQuery.ToComponentDataArray<Team>(Allocator.Temp);
        var xforms = AllUnitsQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        Entity enemy = Entity.Null; float2 enemyPos = default; float best = 30f; // px
        Vector2 mouse = Input.mousePosition;
        for (int i = 0; i < entities.Length; i++)
        {
            if (teams[i].Value == team) continue;
            Vector3 sp = cam.WorldToScreenPoint(xforms[i].Position);
            if (sp.z <= 0) continue;
            float d = Vector2.Distance(mouse, new Vector2(sp.x, sp.y));
            if (d < best) { best = d; enemy = entities[i]; enemyPos = new float2(xforms[i].Position.x, xforms[i].Position.z); }
        }
        entities.Dispose(); teams.Dispose(); xforms.Dispose();

        var selected = GetSelected();
        if (selected.Count == 0) { lastOrder = "(right-click ignored: nothing selected)"; return; }
        if (enemy != Entity.Null) IssueAttack(selected, enemy, enemyPos);
        else if (GroundPoint(out float2 gp)) IssueMove(selected, gp);
    }

    private List<Entity> GetSelected()
    {
        var arr = _selectedQuery.ToEntityArray(Allocator.Temp);
        var list = new List<Entity>(arr.Length);
        foreach (var e in arr) list.Add(e);
        arr.Dispose();
        return list;
    }

    private bool GroundPoint(out float2 p)
    {
        p = default;
        var cam = Camera.main; if (cam == null) return false;
        var ray = cam.ScreenPointToRay(Input.mousePosition);
        // Prefer the real ground: on raised terrain the y=0 plane lands the
        // point long of where the cursor visually sits — moves merely arrive
        // slightly off, but building placement then validates the WRONG cells.
        // View-side only (the resulting point goes into the command), so this
        // is lockstep-safe.
        if (Physics.Raycast(ray, out RaycastHit hitInfo, 5000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            p = new float2(hitInfo.point.x, hitInfo.point.z);
            return true;
        }
        if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float enter)) return false;
        Vector3 hit = ray.GetPoint(enter);
        p = new float2(hit.x, hit.z);
        return true;
    }

    // --- HUD --------------------------------------------------------------------

    private void OnGUI()
    {
        // Ability bar for the current caster (replaces the HeroController HUD).
        var caster = WorldOk ? FindCaster() : Entity.Null;
        if (caster != Entity.Null)
        {
            var ids = Em.GetComponentData<AbilitySlots>(caster).Ids;
            var cds = Em.GetComponentData<AbilityCooldowns>(caster).ReadyTick;
            uint tick = _clockQuery2.HasSingleton<SimClock>() ? _clockQuery2.GetSingleton<SimClock>().Tick : 0u;
            float hp = 0f, hpMax = 0f, mp = 0f, mpMax = 0f;
            if (Em.HasComponent<Health>(caster))
            {
                var h = Em.GetComponentData<Health>(caster);
                hp = h.Current; hpMax = h.Max;
            }
            if (Em.HasComponent<Mana>(caster))
            {
                var m = Em.GetComponentData<Mana>(caster);
                mp = m.Current; mpMax = m.Max;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append($"Caster HP {hp:0}/{hpMax:0}  MP {mp:0}/{mpMax:0}  |  ");
            var mgr = AbilityManager.Instance;
            for (int s = 0; s < 4; s++)
            {
                if (ids[s] < 0) continue;
                string name = mgr != null && mgr.GetDefinition(ids[s]) != null
                    ? mgr.GetDefinition(ids[s]).displayName : $"#{ids[s]}";
                float cd = cds[s] > tick ? (cds[s] - tick) * LockstepConfig.FixedDt : 0f;
                sb.Append(armedIndex == s ? "[" : " ");
                sb.Append($"{SlotKeys[s]}:{name}{(cd > 0f ? $" {cd:0.0}s" : "")}");
                sb.Append(armedIndex == s ? "] " : "  ");
            }
            GUI.Label(new Rect(10, Screen.height - 30, 900, 22), sb.ToString());
        }

        // Team resource readout (always shown; reads the sim's pool directly).
        if (WorldOk && !_resourceQuery.IsEmpty)
        {
            var poolEntity = _resourceQuery.GetSingletonEntity();
            var pool = Em.GetBuffer<TeamResources>(poolEntity);
            if (team >= 0 && team < pool.Length)
            {
                var res = pool[team].Amounts;
                GUI.Label(new Rect(10, Screen.height - 52, 900, 22),
                          $"Gold {res.x}   Wood {res.y}   Stone {res.z}");
            }
        }

        if (!dragging) return;
        Vector2 cur = Input.mousePosition;
        Vector2 a = new Vector2(_dragStart.x, Screen.height - _dragStart.y);
        Vector2 b = new Vector2(cur.x, Screen.height - cur.y);
        var r = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                                Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        GUI.color = new Color(0.4f, 1f, 0.4f, 0.25f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
    }
}
