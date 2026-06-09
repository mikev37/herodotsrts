using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ===========================================================================
// PLAYER COMMANDER — classic RTS input on the shared verbs. Left-drag box
// select, right-click ground = move, right-click enemy = attack.
// ===========================================================================
public class PlayerCommander : Commander
{
    [Header("Player debug (runtime, read-only)")]
    public int selectedCount;
    public bool dragging;

    private EntityQuery _selectedQuery;
    private Vector2 _dragStart;

    protected override void Start()
    {
        base.Start();
        if (!worldReady) return;
        _selectedQuery = Em.CreateEntityQuery(ComponentType.ReadOnly<Selected>());
    }

    private void Update()
    {
        if (!WorldOk) return;
        if (Input.GetMouseButtonDown(0)) { _dragStart = Input.mousePosition; dragging = true; }
        if (Input.GetMouseButtonUp(0) && dragging) { dragging = false; BoxSelect(); }
        if (Input.GetMouseButtonDown(1) && !HeroAbilityInput.AbilityArmed) RightClick();
        selectedCount = _selectedQuery.CalculateEntityCount();
    }

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
            bool was = Em.HasComponent<Selected>(entities[i]);
            if (sel && !was) Em.AddComponent<Selected>(entities[i]);
            else if (!sel && was) Em.RemoveComponent<Selected>(entities[i]);
        }
        entities.Dispose(); teams.Dispose(); xforms.Dispose();
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
        if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float enter)) return false;
        Vector3 hit = ray.GetPoint(enter);
        p = new float2(hit.x, hit.z);
        return true;
    }

    private void OnGUI()
    {
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
