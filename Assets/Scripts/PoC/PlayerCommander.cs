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
// Right-drag sets formation WIDTH: press to lock the destination, drag across
// the terrain, release. The drag length (in world units) becomes the grid
// width in columns; a negligible drag is a plain click and leaves the width
// auto-fit. Holding BOTH buttons is the camera's orbit chord, so while both are
// down we suppress box-select and the move commit (see _orbitChordLatched).
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
    [Tooltip("Building placed with B. Must appear in the RosterDefinition asset.")]
    [SerializeField] private BuildingDefinition placeBuilding;
    [Tooltip("Wall placed with V. A WallDefinition; must appear in the RosterDefinition asset.")]
    [SerializeField] private WallDefinition placeWall;

    [Header("Formation (right-drag to set grid width)")]
    [Tooltip("World distance between adjacent columns; converts a right-drag length into a column count.")]
    [SerializeField] private float columnSpacing = 2f;
    [Tooltip("Smallest grid width (in columns) a deliberate right-drag can produce.")]
    [SerializeField] private int   minDragColumns = 2;
    [Tooltip("Right-drags shorter than this (world units) count as a plain click: width stays auto-fit.")]
    [SerializeField] private float dragDeadzone = 1.5f;

    [Header("Player debug (runtime, read-only)")]
    public int selectedCount;
    public bool dragging;
    public int armedIndex = -1;

    private EntityQuery _selectedQuery;
    private EntityQuery _clockQuery2;
    private EntityQuery _buildingQuery;
    private EntityQuery _resourceQuery;
    private Vector2 _dragStart;

    // Right-drag (formation order) state.
    private bool    _rmbDragging;
    private float2  _rmbStart;       // terrain point under the press = move destination
    private Entity  _rmbEnemy;       // enemy under the press point, if any
    private float2  _rmbEnemyPos;
    private int     _rmbNode = -1;   // resource-node StableId under the press point, if any
    private int     _rmbDepot = -1;  // OWN depot StableId under the press point, if any
    private int     _rmbBuild = -1;  // OWN blueprint/scaffold StableId under the press point, if any

    // Latched while the orbit chord (both buttons) is engaged, so the button-ups
    // that end an orbit don't also fire a box-select or a move order. Cleared
    // only once neither button is held.
    private bool _orbitChordLatched;

    private static readonly KeyCode[] SlotKeys = { KeyCode.Q, KeyCode.W, KeyCode.E, KeyCode.R };

    protected override void Start()
    {
        base.Start();
        if (!worldReady) return;
        _selectedQuery = Em.CreateEntityQuery(ComponentType.ReadOnly<Selected>());
        _clockQuery2 = Em.CreateEntityQuery(ComponentType.ReadOnly<SimClock>());
        _buildingQuery = Em.CreateEntityQuery(
            ComponentType.ReadOnly<BuildingTag>(),
            ComponentType.ReadOnly<Player>(),
            ComponentType.ReadOnly<StableId>(),
            ComponentType.ReadOnly<LocalTransform>());
        _resourceQuery = Em.CreateEntityQuery(ComponentType.ReadOnly<PlayerBankTag>());
    }

    private void Update()
    {
        if (!WorldOk) return;

        bool leftDown  = Input.GetMouseButton(0);
        bool rightDown = Input.GetMouseButton(1);

        // Both buttons = camera orbit chord. Latch it so the releases that end the
        // orbit are not mistaken for a box-select / move commit.
        if (leftDown && rightDown) _orbitChordLatched = true;

        // ---- build ghost (placement preview) ----
        UpdateGhost();

        // ---- left mouse: box select (never while placing a blueprint) ----
        if (Input.GetMouseButtonDown(0) && _ghostDefId < 0) { _dragStart = Input.mousePosition; dragging = true; }
        if (Input.GetMouseButtonUp(0) && dragging)
        {
            dragging = false;
            if (_swallowSelect) _swallowSelect = false;          // that click placed a blueprint
            else if (!_orbitChordLatched) BoxSelect();
        }

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
            TryPlaceBuilding(placeBuilding, buildPos);
        if (Input.GetKeyDown(KeyCode.V) && GroundPoint(out float2 wallPos))
            TryPlaceBuilding(placeWall, wallPos);
        if (Input.GetKeyDown(KeyCode.N) && GroundPoint(out float2 demoPos))
            TryDemolishNearest(demoPos);

        // G = morph selected units (free toggle, e.g. trebuchet siege/unsiege)
        if (Input.GetKeyDown(KeyCode.G))
            IssueMorph(GetPlayerUnits().FindAll(e => Em.IsComponentEnabled<Selected>(e)));

        // X = cancel production head on the selected building; Shift+X = cancel tail
        if (Input.GetKeyDown(KeyCode.X))
            TryBuildingAction(e => IssueCancelProduction(Em.GetComponentData<StableId>(e).Value,
                                                         fromHead: !Input.GetKey(KeyCode.LeftShift)));

        // L = toggle producer loop on selected building
        if (Input.GetKeyDown(KeyCode.L))
            TryBuildingAction(e => IssueToggleProducerLoop(Em.GetComponentData<StableId>(e).Value));

        // P = pause/unpause selected building's bank
        if (Input.GetKeyDown(KeyCode.P))
            TryBuildingAction(e => IssueToggleBankPause(Em.GetComponentData<StableId>(e).Value));

        // L = emergency cart launch: the selected colony builds and dispatches a
        // hauler now (normal build time) even below its threshold — the raid-
        // survivor case where a half-full colony has no more peasants coming.
        if (Input.GetKeyDown(KeyCode.L))
            TryBuildingAction(e => { if (Em.HasComponent<Colony>(e)) IssueLaunchCart(Em.GetComponentData<StableId>(e).Value); });

        // Shift = queue modifier: Move/AttackMove append as waypoints.
        QueueModifier = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        // Control groups: Ctrl+0-9 assigns the current selection, 0-9 recalls it.
        // Digits do NOTHING else — they are reserved for groups, as an RTS demands.
        HandleControlGroups();

        // ---- context-sensitive build/produce/upgrade/research menus ----------
        // When a single building is selected, the asdf row (and qwer for
        // buildings with no ability slots) drives the building's economy menus.
        // Priority: produce > build(for builders) > upgrade > research.
        // Q/W/E/R drive the building menus (digits are control groups).
        TryBuildingMenuKeys();

        // ---- right mouse: armed cast on press, else a formation drag ----
        if (Input.GetMouseButtonDown(1))
        {
            if (_ghostCancelledThisFrame) _ghostCancelledThisFrame = false;   // this RMB cancelled the ghost; consume it
            else if (armedIndex >= 0) { TryCastArmed(); armedIndex = -1; }
            else BeginRightDrag();
        }
        if (Input.GetMouseButtonUp(1) && _rmbDragging)
        {
            if (_orbitChordLatched) _rmbDragging = false;   // orbit consumed this gesture
            else EndRightDrag();
        }

        // Release the orbit latch only once BOTH buttons are up.
        if (!leftDown && !rightDown) _orbitChordLatched = false;

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

    private void TryPlaceBuilding(BuildingDefinition def, float2 pos)
    {
        if (def == null) { lastOrder = "(place ignored: no definition assigned)"; return; }
        int defId = UnitFactory.Instance != null ? UnitFactory.Instance.Roster.GetId(def) : -1;
        if (defId < 0)
        {
            Debug.LogWarning($"[Place] ignored: '{def.displayName}' is not in the roster asset. " +
                             "Add it to the RosterDefinition — the roster index is the network def id.");
            lastOrder = $"(place ignored: '{def.displayName}' not in roster)";
            return;
        }
        IssuePlaceBuilding(defId, pos);
    }

    private void TryDemolishNearest(float2 pos)
    {
        var entities = _buildingQuery.ToEntityArray(Allocator.Temp);
        var players = _buildingQuery.ToComponentDataArray<Player>(Allocator.Temp);
        var sids = _buildingQuery.ToComponentDataArray<StableId>(Allocator.Temp);
        var xforms = _buildingQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < entities.Length; i++)
        {
            if (players[i].Value != player) continue;
            float d = math.distancesq(pos, new float2(xforms[i].Position.x, xforms[i].Position.z));
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best >= 0) IssueDemolishBuilding(sids[best].Value);
        else lastOrder = "(N ignored: no own building found)";

        entities.Dispose(); players.Dispose(); sids.Dispose(); xforms.Dispose();
    }

    // Invoke an action on the single selected own building, if exactly one is selected.
    private void TryBuildingAction(System.Action<Entity> act)
    {
        var sel = _selectedQuery.ToEntityArray(Allocator.Temp);
        Entity found = Entity.Null;
        for (int i = 0; i < sel.Length; i++)
            if (Em.HasComponent<BuildingTag>(sel[i]) && Em.HasComponent<Player>(sel[i]) &&
                Em.GetComponentData<Player>(sel[i]).Value == player)
            { found = sel[i]; break; }
        sel.Dispose();
        if (found != Entity.Null) act(found);
    }

    // Find the nearest NodeTag entity to a world point; returns its StableId or -1.

    // Context-sensitive building economy menu.
    // With a single own building selected, the number row 1-4 drives produce/
    // build/upgrade/research slots in that priority order.
    // ---- control groups (0-9). Pure client-side selection state: groups store
    // StableIds (dead members simply stop matching), assignment replaces the
    // group, recall reproduces the selection exactly. No lockstep impact.
    private readonly Dictionary<int, List<int>> _ctrlGroups = new();

    private void HandleControlGroups()
    {
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        for (int g = 0; g <= 9; g++)
        {
            if (!Input.GetKeyDown(KeyCode.Alpha0 + g)) continue;
            if (ctrl)
            {
                var sids = new List<int>();
                var sel = _selectedQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < sel.Length; i++)
                    if (Em.IsComponentEnabled<Selected>(sel[i]) && Em.HasComponent<StableId>(sel[i]))
                        sids.Add(Em.GetComponentData<StableId>(sel[i]).Value);
                sel.Dispose();
                _ctrlGroups[g] = sids;                    // empty selection clears the group
                lastOrder = $"group {g} = {sids.Count} units";
            }
            else if (_ctrlGroups.TryGetValue(g, out var group) && group.Count > 0)
            {
                var want = new HashSet<int>(group);
                RecallOver(AllUnitsQuery, want);
                RecallOver(StructuresQuery, want);
                lastOrder = $"recall group {g}";
            }
        }
    }

    private void RecallOver(EntityQuery q, HashSet<int> want)
    {
        var ents = q.ToEntityArray(Allocator.Temp);
        var players = q.ToComponentDataArray<Player>(Allocator.Temp);
        for (int i = 0; i < ents.Length; i++)
        {
            if (!Em.HasComponent<Selected>(ents[i]) || !Em.HasComponent<StableId>(ents[i])) continue;
            bool sel = players[i].Value == player && want.Contains(Em.GetComponentData<StableId>(ents[i]).Value);
            if (Em.IsComponentEnabled<Selected>(ents[i]) != sel)
                Em.SetComponentEnabled<Selected>(ents[i], sel);
        }
        ents.Dispose(); players.Dispose();
    }

    // ---- build-placement ghost: armed by the builds menu, follows the cursor
    // (footprint-snapped), LMB places the blueprint, RMB/Esc cancels. The
    // placement click is swallowed so it doesn't also box-select.
    private int _ghostDefId = -1;
    private BuildingDefinition _ghostDef;
    private GameObject _ghost;
    private bool _swallowSelect;
    private bool _ghostCancelledThisFrame;

    private void ArmGhost(int defId, BuildingDefinition def)
    {
        CancelGhost();
        _ghostDefId = defId; _ghostDef = def;
        if (def.viewPrefab != null)
        {
            _ghost = Instantiate(def.viewPrefab);
            foreach (var col in _ghost.GetComponentsInChildren<Collider>()) col.enabled = false;
        }
        else
        {
            // No view prefab authored: a placement ghost must STILL be visible —
            // fall back to a footprint-sized cube so arming is never silent.
            _ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(_ghost.GetComponent<Collider>());
            _ghost.transform.localScale = new Vector3(def.footprintX * NavGrid.CellSize, 2f, def.footprintZ * NavGrid.CellSize);
        }
        lastOrder = $"placing {def.displayName} (LMB place, RMB/Esc cancel, Shift=line, Shift+Alt=square)";
    }

    private void CancelGhost()
    {
        if (_ghost != null) Destroy(_ghost);
        _ghost = null; _ghostDefId = -1; _ghostDef = null;
        _placeDragging = false;
        ClearLineGhosts();
    }

    private void UpdateGhost()
    {
        if (_ghostDefId < 0) return;
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)) { CancelGhost(); _ghostCancelledThisFrame = true; return; }
        if (!GroundPoint3(out float3 gp3)) return;
        float2 gp = new float2(gp3.x, gp3.z);

        var extents = new int2(math.max(1, _ghostDef.footprintX), math.max(1, _ghostDef.footprintZ));
        float2 snapped = BuildingFootprint.SnappedCenter(BuildingFootprint.MinCell(gp, extents), extents);
        if (_ghost != null)
            _ghost.transform.position = new Vector3(snapped.x, gp3.y, snapped.y);   // terrain height, not buried

        // Validity = the REAL sim verdict (cells, height delta/TooSteep) + no unit
        // in the footprint — the ghost can never show green for a click the sim
        // would reject.
        bool valid = GhostPlacementValid(snapped, extents);
        TintGhost(valid ? new Color(0.4f, 1f, 0.4f, 0.6f) : new Color(1f, 0.3f, 0.3f, 0.6f));

        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool alt   = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        if (Input.GetMouseButtonDown(0)) { _placeAnchor = snapped; _placeDragging = true; _swallowSelect = true; }

        // live multi-preview while shift-dragging: one clone per planned spot,
        // each tinted by ITS validity
        if (_placeDragging && shift)
        {
            BuildDragPositions(snapped, alt, extents, _dragPositions);
            SyncLineGhosts(gp3.y, extents);
        }
        else ClearLineGhosts();

        if (Input.GetMouseButtonUp(0) && _placeDragging)
        {
            _placeDragging = false;
            var builders = GetSelected().FindAll(e => Em.HasComponent<BuildPower>(e) &&
                                                      Em.GetComponentData<BuildPower>(e).Value > 0f);
            if (shift)
            {
                BuildDragPositions(snapped, alt, extents, _dragPositions);
                int placed = 0;
                foreach (var p in _dragPositions)
                {
                    if (!GhostPlacementValid(p, extents)) continue;   // invalid spots discarded
                    IssuePlaceBlueprint(_ghostDefId, p, builders);
                    placed++;
                }
                lastOrder = $"placed {placed} blueprints";
                CancelGhost();
            }
            else if (valid)
            {
                IssuePlaceBlueprint(_ghostDefId, snapped, builders);
                CancelGhost();
            }
            else lastOrder = "cannot place here (blocked / too steep / unit in footprint)";
        }
    }

    private readonly List<float2> _dragPositions = new();
    private readonly List<GameObject> _lineGhosts = new();

    private void BuildDragPositions(float2 snapped, bool alt, int2 extents, List<float2> outList)
    {
        outList.Clear();
        float2 pitch = (float2)extents * NavGrid.CellSize;   // one footprint per step
        float2 d = snapped - _placeAnchor;
        int nx, ny;
        if (alt) { nx = (int)math.round(math.abs(d.x) / pitch.x); ny = (int)math.round(math.abs(d.y) / pitch.y); }
        else
        {
            bool alongX = math.abs(d.x) >= math.abs(d.y);
            nx = alongX ? (int)math.round(math.abs(d.x) / pitch.x) : 0;
            ny = alongX ? 0 : (int)math.round(math.abs(d.y) / pitch.y);
        }
        float2 sx = new float2(d.x >= 0 ? pitch.x : -pitch.x, 0);
        float2 sy = new float2(0, d.y >= 0 ? pitch.y : -pitch.y);
        for (int iy = 0; iy <= ny && outList.Count < 32; iy++)
        for (int ix = 0; ix <= nx && outList.Count < 32; ix++)
        {
            float2 p = _placeAnchor + sx * ix + sy * iy;
            outList.Add(BuildingFootprint.SnappedCenter(BuildingFootprint.MinCell(p, extents), extents));
        }
    }

    private void SyncLineGhosts(float y, int2 extents)
    {
        while (_lineGhosts.Count < _dragPositions.Count && _ghost != null)
        {
            var g = Instantiate(_ghost);
            g.transform.localScale = _ghost.transform.localScale;
            _lineGhosts.Add(g);
        }
        while (_lineGhosts.Count > _dragPositions.Count)
        { Destroy(_lineGhosts[^1]); _lineGhosts.RemoveAt(_lineGhosts.Count - 1); }
        for (int i = 0; i < _lineGhosts.Count; i++)
        {
            // each clone sits on ITS ground, not the cursor's
            float gy = y;
            if (Physics.Raycast(new Vector3(_dragPositions[i].x, 1000f, _dragPositions[i].y), Vector3.down,
                                out RaycastHit hit, 2000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                gy = hit.point.y;
            _lineGhosts[i].transform.position = new Vector3(_dragPositions[i].x, gy, _dragPositions[i].y);
            bool ok = GhostPlacementValid(_dragPositions[i], extents);
            TintObject(_lineGhosts[i], ok ? new Color(0.4f, 1f, 0.4f, 0.6f) : new Color(1f, 0.3f, 0.3f, 0.6f));
        }
    }

    private void ClearLineGhosts()
    {
        foreach (var g in _lineGhosts) if (g != null) Destroy(g);
        _lineGhosts.Clear();
    }

    private float2 _placeAnchor;
    private bool _placeDragging;

    private bool GroundPoint3(out float3 p)
    {
        p = default;
        var cam = Camera.main; if (cam == null) return false;
        var ray = cam.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 5000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        { p = hit.point; return true; }
        return false;
    }

    private bool GhostPlacementValid(float2 center, int2 extents)
    {
        // the exact sim verdict (cells + height/TooSteep)
        var oq = Em.CreateEntityQuery(ComponentType.ReadOnly<ObstacleField>());
        var tq = Em.CreateEntityQuery(ComponentType.ReadOnly<TerrainHeightField>());
        if (!oq.IsEmptyIgnoreFilter)
        {
            var obs = oq.GetSingleton<ObstacleField>();
            bool hasTerrain = !tq.IsEmptyIgnoreFilter;
            var terrain = hasTerrain ? tq.GetSingleton<TerrainHeightField>() : default;
            bool cutCorners = !(_ghostDef is WallDefinition);
            var verdict = BuildingFootprint.ValidatePlacement(center, extents, _ghostDef.maxHeightDelta,
                                                              obs.CellType, hasTerrain, terrain, cutCorners, out _);
            if (verdict != PlacementVerdict.Ok) return false;
        }
        // units: nobody standing in the footprint
        float2 half = (float2)extents * (NavGrid.CellSize * 0.5f);
        var ents = AllUnitsQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        bool clear = true;
        for (int i = 0; i < ents.Length && clear; i++)
        {
            float2 p = new float2(ents[i].Position.x, ents[i].Position.z);
            float2 d = math.abs(p - center) - half;
            if (math.length(math.max(d, 0f)) < 0.6f) clear = false;
        }
        ents.Dispose();
        return clear;
    }

    [Tooltip("Optional: materials the placement ghost renders with (assign transparent green/red). " +
             "Unset = a tint is applied via property block (shader-dependent).")]
    public Material ghostValidMaterial, ghostInvalidMaterial;

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private void TintGhost(Color c) => TintObject(_ghost, c);
    private void TintObject(GameObject go, Color c)
    {
        if (go == null) return;
        // assigned materials win (proper transparency); tint is the fallback
        bool valid = c.g > c.r;
        var mat = valid ? ghostValidMaterial : ghostInvalidMaterial;
        if (mat != null)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var mats = r.sharedMaterials;
                bool differs = false;
                for (int m = 0; m < mats.Length; m++) if (mats[m] != mat) { differs = true; break; }
                if (differs)
                {
                    var swap = new Material[mats.Length];
                    for (int m = 0; m < swap.Length; m++) swap[m] = mat;
                    r.sharedMaterials = swap;
                }
            }
            return;
        }
        var mpb = new MaterialPropertyBlock();
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            r.GetPropertyBlock(mpb);
            mpb.SetColor(ColorId, c);
            mpb.SetColor(BaseColorId, c);
            r.SetPropertyBlock(mpb);
        }
    }

    private void TryBuildingMenuKeys()
    {
        var roster = UnitFactory.Instance?.Roster;
        if (roster == null) return;

        var sel = _selectedQuery.ToEntityArray(Allocator.Temp);

        // ---- BUILDING context: single selected own building ------------------
        Entity bld = Entity.Null;
        for (int i = 0; i < sel.Length; i++)
            if (Em.HasComponent<BuildingTag>(sel[i]) && Em.HasComponent<Player>(sel[i]) &&
                Em.GetComponentData<Player>(sel[i]).Value == player && Em.HasComponent<UnitDefId>(sel[i]))
            { if (bld != Entity.Null) { bld = Entity.Null; break; }   // more than one — ambiguous
              bld = sel[i]; }

        if (bld != Entity.Null)
        {
            int sid   = Em.GetComponentData<StableId>(bld).Value;
            int defId = Em.GetComponentData<UnitDefId>(bld).Value;
            var bdef  = roster.GetDefinition(defId) as BuildingDefinition;

            if (bdef != null)
            {
                var busy = EconomyQuery.BuildingBusy(Em, bld, queueingProduction: true);

                for (int i = 0; i < SlotKeys.Length; i++)
                {
                    // Q/W/E/R drive the building menu. The number row is NEVER used
                    // here — digits are reserved for control groups.
                    if (!Input.GetKeyDown(SlotKeys[i])) continue;

                    // Produce
                    if (bdef.isProducer && bdef.produces != null && i < bdef.produces.Count &&
                        (busy == EconomyQuery.ActivityKind.None || busy == EconomyQuery.ActivityKind.Production))
                    {
                        int uid = roster.GetId(bdef.produces[i]);
                        if (uid >= 0) { IssueQueueProduction(sid, uid); sel.Dispose(); return; }
                    }
                    // Building upgrade (Keep → Castle)
                    if (bdef.buildingUpgrades != null && i < bdef.buildingUpgrades.Count &&
                        busy == EconomyQuery.ActivityKind.None)
                    {
                        int uid = roster.GetId(bdef.buildingUpgrades[i]);
                        if (uid >= 0) { IssueUpgrade(sid, uid); sel.Dispose(); return; }
                    }
                    // Research tech (requires isResearcher, parallel to isProducer)
                    if (bdef.isResearcher && bdef.researches != null && i < bdef.researches.Count &&
                        busy == EconomyQuery.ActivityKind.None)
                    { IssueResearch(sid, i); sel.Dispose(); return; }
                }

                // R = set rally (producer buildings only; doesn't conflict with ability key
                // since buildings rarely have abilities)
                if (Input.GetKeyDown(KeyCode.R) && bdef.isProducer && GroundPoint(out float2 rallyPos))
                    IssueSetRally(sid, rallyPos);
            }
        }

        // ---- UNIT context: builder unit pressing 1-4 to place a building ----
        // Find a representative builder from the selection (the one with the most
        // builds entries, to handle mixed selections gracefully — use its list).
        BuildingDefinition[] buildsMenu = null;
        int buildMenuLen = 0;
        for (int i = 0; i < sel.Length; i++)
        {
            if (!Em.HasComponent<Player>(sel[i])) continue;
            if (Em.GetComponentData<Player>(sel[i]).Value != player) continue;
            if (!Em.HasComponent<UnitDefId>(sel[i])) continue;
            var udef = roster.GetDefinition(Em.GetComponentData<UnitDefId>(sel[i]).Value);
            if (udef?.builds == null || udef.builds.Count == 0) continue;
            if (udef.builds.Count > buildMenuLen)
            { buildMenuLen = udef.builds.Count; buildsMenu = udef.builds.ToArray(); }
        }

        if (buildsMenu != null && buildsMenu.Length > 0)
        {
            for (int i = 0; i < SlotKeys.Length && i < buildsMenu.Length; i++)
            {
                if (!Input.GetKeyDown(SlotKeys[i])) continue;
                var bdef2 = buildsMenu[i];
                if (bdef2 == null) { lastOrder = $"builds[{i}] is EMPTY on the selected unit's definition"; continue; }
                int uid = roster.GetId(bdef2);
                if (uid < 0) { lastOrder = $"'{bdef2.displayName}' is not in the roster (add it to RosterDefinition)"; continue; }
                // ARM the ghost: a preview follows the cursor, LMB places, RMB/Esc cancels.
                ArmGhost(uid, bdef2); sel.Dispose(); return;
            }
        }
        else if (bld == Entity.Null)
        {
            // A builder is selected but its def lists nothing to build: pressing a
            // slot key must SAY so, not silently do nothing (the doc prerequisite
            // is `builds: [BarracksDef]` on the unit's definition asset).
            for (int i = 0; i < SlotKeys.Length; i++)
                if (Input.GetKeyDown(SlotKeys[i]))
                    lastOrder = "selected unit's `builds` list is empty — author it on the UnitDefinition asset";
        }

        sel.Dispose();
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
        var players = AllUnitsQuery.ToComponentDataArray<Player>(Allocator.Temp);
        var xforms = AllUnitsQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        // A bare click has a ~zero-area rect that contains nothing — grow it to a
        // small pick box so single-click selection works (units AND buildings).
        if (rect.width < 8f && rect.height < 8f)
            rect = new Rect(rect.center.x - 14f, rect.center.y - 14f, 28f, 28f);

        for (int i = 0; i < entities.Length; i++)
        {
            bool sel = false;
            if (players[i].Value == player)
            {
                Vector3 sp = cam.WorldToScreenPoint(xforms[i].Position);
                sel = sp.z > 0 && rect.Contains(new Vector2(sp.x, sp.y));
            }
            if (Em.IsComponentEnabled<Selected>(entities[i]) != sel)
                Em.SetComponentEnabled<Selected>(entities[i], sel);
        }
        entities.Dispose(); players.Dispose(); xforms.Dispose();

        // Structures too: colonies, barracks, castles are selectable so their
        // banks/production can be inspected and building actions (P pause,
        // L launch, produce/research menus) have a target. Same rect test.
        var sEnts = StructuresQuery.ToEntityArray(Allocator.Temp);
        var sPlayers = StructuresQuery.ToComponentDataArray<Player>(Allocator.Temp);
        var sXforms = StructuresQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        for (int i = 0; i < sEnts.Length; i++)
        {
            bool sel = false;
            if (sPlayers[i].Value == player)
            {
                Vector3 sp = cam.WorldToScreenPoint(sXforms[i].Position);
                sel = sp.z > 0 && rect.Contains(new Vector2(sp.x, sp.y));
            }
            if (Em.HasComponent<Selected>(sEnts[i]) &&
                Em.IsComponentEnabled<Selected>(sEnts[i]) != sel)
                Em.SetComponentEnabled<Selected>(sEnts[i], sel);
        }
        sEnts.Dispose(); sPlayers.Dispose(); sXforms.Dispose();
        armedIndex = -1;   // selection changed; disarm
    }

    // Press: lock in the order's destination (the terrain point under the cursor)
    // and the enemy under it, if any. Width is decided on release from how far
    // the cursor is dragged across the terrain.
    private void BeginRightDrag()
    {
        _rmbDragging = true;
        _rmbEnemy    = Entity.Null;
        _rmbNode     = -1;
        _rmbDepot    = -1;
        _rmbBuild    = -1;

        // Destination = the terrain point under the press. GroundPoint raycasts
        // the real terrain (see its note), so the drag measures true world
        // distance over the ground.
        if (!GroundPoint(out _rmbStart)) { _rmbDragging = false; return; }

        var cam = Camera.main; if (cam == null) return;
        Vector2 mouse = Input.mousePosition;

        // --- Pass 1: mobile enemy units (attack) ---
        var entities = AllUnitsQuery.ToEntityArray(Allocator.Temp);
        var players = AllUnitsQuery.ToComponentDataArray<Player>(Allocator.Temp);
        var xforms = AllUnitsQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        float best = 30f; // px
        for (int i = 0; i < entities.Length; i++)
        {
            if (players[i].Value == player) continue;
            Vector3 sp = cam.WorldToScreenPoint(xforms[i].Position);
            if (sp.z <= 0) continue;
            float d = Vector2.Distance(mouse, new Vector2(sp.x, sp.y));
            if (d < best) { best = d; _rmbEnemy = entities[i]; _rmbEnemyPos = new float2(xforms[i].Position.x, xforms[i].Position.z); }
        }
        entities.Dispose(); players.Dispose(); xforms.Dispose();

        // --- Pass 2: structures (resource node -> harvest; enemy building ->
        //     attack; a NEUTRAL obstacle (player -1, no NodeTag) is IGNORED so a
        //     right-click near a rock is a plain move, never an attack-the-rock). ---
        var sEnts = StructuresQuery.ToEntityArray(Allocator.Temp);
        var sPlayers = StructuresQuery.ToComponentDataArray<Player>(Allocator.Temp);
        var sXforms = StructuresQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        float bestNode = 30f, bestBldg = 30f;
        for (int i = 0; i < sEnts.Length; i++)
        {
            Vector3 sp = cam.WorldToScreenPoint(sXforms[i].Position);
            if (sp.z <= 0) continue;
            float d = Vector2.Distance(mouse, new Vector2(sp.x, sp.y));

            if (Em.HasComponent<NodeTag>(sEnts[i]))                       // resource node -> harvest
            {
                if (d < bestNode) { bestNode = d; _rmbNode = Em.GetComponentData<StableId>(sEnts[i]).Value; }
            }
            else if (sPlayers[i].Value == player &&
                     (Em.HasComponent<BlueprintTag>(sEnts[i]) || Em.HasComponent<Construction>(sEnts[i])))
            {
                if (d < bestBldg) { bestBldg = d; _rmbBuild = Em.GetComponentData<StableId>(sEnts[i]).Value; }
            }
            else if (sPlayers[i].Value == player && Em.HasComponent<DepotTag>(sEnts[i]))   // OWN depot -> deliver
            {
                if (d < bestBldg) { bestBldg = d; _rmbDepot = Em.GetComponentData<StableId>(sEnts[i]).Value; }
            }
            else if (sPlayers[i].Value != player && sPlayers[i].Value >= 0 // ENEMY building -> attack
                     && !Em.HasComponent<NonCombatant>(sEnts[i]))          // (a neutral/invulnerable structure is never attacked)
            {
                if (d < bestBldg) { bestBldg = d; _rmbEnemy = sEnts[i]; _rmbEnemyPos = new float2(sXforms[i].Position.x, sXforms[i].Position.z); }
            }
            // else: neutral obstacle (rock) -> ignore, so EndRightDrag falls through to a move.
        }
        sEnts.Dispose(); sPlayers.Dispose(); sXforms.Dispose();
    }

    // Release: commit the order. Drag length across the terrain (press point ->
    // release point) becomes the grid width in columns; a negligible drag is a
    // plain click and leaves the width auto-fit (0). An enemy under the original
    // press point wins and is attacked (auto width).
    private void EndRightDrag()
    {
        _rmbDragging = false;

        var all = GetSelected();
        // Split: buildings never take unit orders, and rally fires whenever the
        // selection has NO mobile units (GetSelected includes buildings, so the
        // old "Count == 0" gate could never pass with a barracks selected).
        var selected = all.FindAll(e => !Em.HasComponent<Immobile>(e));

        if (selected.Count == 0)
        {
            bool ralled = false;
            var roster = UnitFactory.Instance?.Roster;
            foreach (var b in all)
            {
                if (!Em.HasComponent<BuildingTag>(b)) continue;
                if (Em.GetComponentData<Player>(b).Value != player) continue;
                var bdef = roster?.GetDefinition(Em.GetComponentData<UnitDefId>(b).Value) as BuildingDefinition;
                if (bdef == null || !bdef.isProducer) continue;
                if (GroundPoint(out float2 rp))
                {
                    IssueSetRally(Em.GetComponentData<StableId>(b).Value, rp);
                    ralled = true;
                }
            }
            lastOrder = ralled ? "set rally" : "(right-click ignored: nothing selected)";
            return;
        }

        // A resource node under the press → harvest with any selected harvesters.
        // Takes priority over attack: clicking a tree should harvest it, not pick
        // a fight with something behind it.
        if (_rmbNode >= 0)
        {
            var harvesters = selected.FindAll(e => Em.HasComponent<HarvestTask>(e));
            if (harvesters.Count > 0)
            {
                IssueHarvest(harvesters, _rmbNode);
                lastOrder = $"harvest node {_rmbNode} ({harvesters.Count})";
                return;
            }
            // No harvesters selected → fall through (attack if enemy, else move).
        }

        // An OWN blueprint/scaffold under the press → assign selected builders.
        if (_rmbBuild >= 0)
        {
            var crews = selected.FindAll(e => Em.HasComponent<BuildPower>(e) &&
                                              Em.GetComponentData<BuildPower>(e).Value > 0f);
            if (crews.Count > 0)
            {
                IssueBuild(crews, _rmbBuild);
                lastOrder = $"build site {_rmbBuild} ({crews.Count})";
                return;
            }
        }

        // An OWN depot under the press → send selected harvesters to drop their
        // cargo there (explicit drop-off, overriding the auto-nearest depot).
        if (_rmbDepot >= 0)
        {
            var carriers = selected.FindAll(e => Em.HasComponent<HarvestTask>(e) || Em.HasComponent<HaulTask>(e));
            if (carriers.Count > 0)
            {
                IssueDeliver(carriers, _rmbDepot);
                lastOrder = $"deliver to depot {_rmbDepot} ({carriers.Count})";
                return;
            }
        }

        if (_rmbEnemy != Entity.Null) { IssueAttack(selected, _rmbEnemy, _rmbEnemyPos); return; }

        int width = 0;   // 0 => CommandApplySystem auto-fits (current default)
        if (GroundPoint(out float2 end))
        {
            float dragDist = math.distance(_rmbStart, end);
            if (dragDist >= dragDeadzone)
                width = math.max(minDragColumns, (int)math.round(dragDist / math.max(0.01f, columnSpacing)));
        }
        IssueMove(selected, _rmbStart, false, width);
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

        // Resource readout — reads the local player's economy bank (ResourceAmount).
        if (WorldOk && !_resourceQuery.IsEmpty && EconomyQuery.TryGetBank(Em, player, out var res))
        {
            GUI.Label(new Rect(10, Screen.height - 52, 900, 22),
                      $"Gold {res.Gold}   Wood {res.Wood}   Food {res.Food}");
        }

        // Selection rectangle — hidden while the orbit chord is engaged.
        if (!dragging || _orbitChordLatched) return;
        Vector2 cur = Input.mousePosition;
        Vector2 a = new Vector2(_dragStart.x, Screen.height - _dragStart.y);
        Vector2 b = new Vector2(cur.x, Screen.height - cur.y);
        var r = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                                Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        GUI.color = new Color(0.4f, 1f, 0.4f, 0.25f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
    }
}
