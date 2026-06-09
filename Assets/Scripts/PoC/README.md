# Marble Combat — RTS DOTS Vertical Slice

A Unity 6 / Entities **1.4** proof-of-concept for a large-scale, behavior-driven
RTS with **no physics engine**. The simulation runs as ECS entities (cache-
coherent, Burst, parallel); visuals are ordinary pooled GameObjects slaved to
those entities. Heroes and UI live in the managed (GameObject) world and talk to
the sim through data.

This slice demonstrates, end to end:

- **Flow-field pathfinding** around obstacles — terrain doodads *and* buildings
  created/destroyed during play, with units re-routing automatically.
- **Ranged units with simulated projectiles** (entities, not physics bodies —
  the thing the old GrabPass sim choked on).
- **A GameObject hero** that changes nearby unit behavior — the GO↔DOTS bridge,
  driven both directions.
- **Woven role behaviors** that produce emergent formations (shield wall, spears
  behind shields, skirmisher kiting, attackers seeking the best target).
- **An RTS control scheme** (box-select, move, attack) built on one abstract
  `Commander` the **AI shares**.

> ⚠️ Structured scaffold, **not compiler-tested**. Targets current API
> (`ISystem`, `IJobEntity`, `SystemAPI`, Bakers, SubScenes). Expect a few small
> fix-ups — likely spots are listed at the bottom.

---

## Architecture in one breath

Two worlds that never reference each other directly:

- **Simulation = entities.** Lightweight data + systems. This is what scales.
- **Visuals/heroes/UI = GameObjects.** A `UnitViewManager` copies each entity's
  transform onto a pooled view and pushes its `AnimState` into an Animator.

The only link is data: `UnitTypeId` (which prefab) + `AnimState` (which clip).
That keeps every system Burst-compiled and parallel.

---

## The simulation pipeline (system order)

Everything runs in `SimulationSystemGroup`, ordered by `[UpdateAfter]`:

```
SpatialHashSystem      build the neighbor grid (core of scaling)
  ObstacleGridSystem   rasterize buildings/doodads -> passability grid
  FlowFieldSystem      BFS flow field toward the current goal (rebuilt on change)
  TargetingSystem      every unit picks its best enemy from the hash
  HeroAuraSystem       heroes stamp a BehaviorMode onto units in range
  BehaviorSystem       THE resolver: one DesiredDestination per unit
  SlopeSystem          terrain speed multiplier (downhill hits harder, emergent)
  SteeringSystem       flow-follow + separation + obstacle repulsion + integrate
  ContactCombatSystem  melee impact damage + knockback (no physics)
  RangedAttackSystem   spawn projectile entities on cooldown
  ProjectileSystem     move projectiles, hit enemies, apply damage
  AnimationStateSystem map sim signals -> Idle/Walk/Block/Attack/Die
  DeathSystem          let the Die clip play, then destroy the entity
```

---

## Files

| File | Role |
|------|------|
| `Components.cs` | Core components + spatial-hash & spawner singletons |
| `SliceComponents.cs` | Roles, orders, navigation grid, projectiles, hero aura |
| `UnitDefinition.cs` | `UnitDefinition` ScriptableObject (per-role stats) |
| `UnitAuthoring.cs` | `UnitAuthoring` component + its Baker + `UnitTypeId` |
| `UnitSpawnerAuthoring.cs` | `UnitSpawnerAuthoring` component + its Baker |
| `SpawnSystem.cs` | Spawns a mixed composition per team, once |
| `SpatialHashSystem.cs` | Per-frame parallel neighbor grid — **the scaling core** |
| `Navigation.cs` | `ObstacleGridSystem` + `FlowFieldSystem` (pathfinding) |
| `TargetingSystem.cs` | Per-unit best-enemy selection (nearest, weighted weak) |
| `HeroAuraSystem.cs` | Applies hero auras to friendly units in range |
| `HeroLink.cs` | `HeroLink` MonoBehaviour — the GO↔DOTS hero bridge |
| `BehaviorSystem.cs` | Role resolver → emergent formations |
| `SlopeSystem.cs` + `TerrainFieldBootstrap.cs` | Terrain slope speed modifier |
| `SteeringSystem.cs` | Locomotion: flow field + separation + obstacle avoidance |
| `ContactCombatSystem.cs` | Melee impact damage + knockback |
| `Projectiles.cs` | `RangedAttackSystem` + `ProjectileSystem` |
| `ProjectileViewManager.cs` | Pooled projectile visuals |
| `AnimationStateSystem.cs` | Sim → animation state |
| `DeathSystem.cs` | Death lifecycle |
| `Commander.cs` | abstract `Commander` — the shared order API |
| `PlayerCommander.cs` | RTS mouse input on the shared verbs |
| `AICommander.cs` | Timer-driven AI on the same verbs |
| `BuildingManager.cs` | Runtime place/remove obstacle buildings |
| `DebugComponents.cs` | `SimDebug` singleton + `SimDebugSystem` (live counts) |
| `DebugOverlay.cs` | HUD + Scene-view gizmos; live read-only inspector stats |
| `UnitView.cs` / `UnitViewManager.cs` | Pooled animated GameObject views |

> **One MonoBehaviour/ScriptableObject per file**, named to match the class —
> Unity won't load a component otherwise. ECS `ISystem` and `IComponentData`
> structs have no such rule, so those stay grouped; `Baker<T>` classes can sit
> beside their authoring component.

---

## How each requested feature works

### Pathfinding around dynamic obstacles (flow field)
`ObstacleGridSystem` rasterizes every `Obstacle` entity into a passability grid
each frame and bumps a version when the set changes. `FlowFieldSystem` runs a BFS
from the goal cell over passable cells and derives a per-cell direction toward
the goal — **rebuilt only when the goal or obstacles change**, so it's cheap.
Units on a long commanded move follow the field (routing around buildings);
local role movement steers straight. `BuildingManager` adds/removes obstacle
entities at runtime (B / N keys), and the field recomputes on its next tick —
buildings and terrain doodads are the same `Obstacle` component.

*PoC limit:* one shared field toward the latest commanded goal. Multiple
simultaneous group destinations want a small cache of fields keyed by goal cell —
same algorithm, N of them.

### Ranged units + projectiles
Skirmishers carry a `RangedAttack`. `RangedAttackSystem` fires a real
`Projectile` **entity** at the target when in range and off cooldown;
`ProjectileSystem` flies it, checks the hash for an enemy hit, applies damage,
and despawns. Projectiles are pure data — no rigidbodies, no GrabPass sampling —
so thousands are fine.

### Hero ↔ DOTS bridge
`HeroLink` (MonoBehaviour) is your rich GameObject hero. It owns a lightweight
hero **entity** carrying a `HeroAura`. Each frame it pushes its transform +
current ability mode **into** the entity (GO→ECS), and reads the count of units
it's affecting **back** (ECS→GO). `HeroAuraSystem` does the heavy per-unit work
in Burst: stamping `Aggressive`/`Defensive`/`Default` onto friendly units in
radius. Keys 1/2/3 toggle the mode; WASD moves the hero.

### Woven role behaviors (Game-of-Life style)
`BehaviorSystem` is the single decision point. After order/hero overrides, each
role applies one simple rule and the patterns emerge from interaction:
- **Shield** — slides laterally to line up with nearby friendly shields, facing
  the enemy → a **wall** forms.
- **Spear** — tucks in just behind the nearest friendly shield.
- **Skirmisher** — keeps a preferred distance from the nearest enemy (kite).
- **Attacker** — advances onto its best target (from `TargetingSystem`).

No behavior "knows" about a phalanx; tune the constants and the formations change.

### RTS UI on a shared Commander
`Commander` (abstract) owns the order verbs — `IssueMove`, `IssueAttack`,
`IssueStop` — which write ECS components and set the flow-field goal.
`PlayerCommander` drives them from mouse input (left-drag box select, right-click
ground = move, right-click enemy = attack). `AICommander` drives the *same verbs*
on a timer. Add a smarter AI by overriding `Tick()` — the order plumbing is shared.

---

## Setup (~20 min)

1. **Unity 6.x**, 3D project (URP or built-in). Units live on the `y = 0` plane.
2. **Package Manager → install** `com.unity.entities` (Burst/Collections/
   Mathematics/Jobs come as deps). Entities Graphics is **not** needed — views
   are GameObjects.
3. Drop all scripts into `Assets/Scripts/`.
4. **Stats → ScriptableObjects.** Create → MarbleCombat → Unit Definition. Make
   four: Shield, Spear, Skirmisher (`isRanged = true`), Attacker. Give each a
   distinct `viewTypeId` (0/1/2/3) and `role`.
5. **View prefabs** (the art), one per role: model + `Animator` with an int param
   **"State"** (0 Idle,1 Walk,2 Block,3 Attack,4 Die) + the `UnitView` component.
   *No art yet?* Use a Capsule with no Animator — units still move/fight, just
   without animation.
6. **Sim prefabs** (art-free), one per role: empty GameObject + `UnitAuthoring`
   pointing at the matching SO. These get baked; no renderer needed.
7. **SubScene** → add `UnitSpawnerAuthoring`, assign the 4 **sim** prefabs, set
   `countPerTeam`.
8. **Main scene objects:**
   - top-down `Camera`;
   - `UnitViewManager` with `viewPrefabsByType[0..3]` = the 4 **view** prefabs;
   - `ProjectileViewManager` with a small projectile prefab;
   - `PlayerCommander` (team 0) and `AICommander` (team 1);
   - a hero GameObject with `HeroLink` (team 0);
   - `BuildingManager` with a building prefab (or it falls back to a cube);
   - a `DebugOverlay` GameObject (HUD + Scene-view gizmos — highly recommended);
   - *(optional)* a Unity `Terrain` + `TerrainFieldBootstrap` for slopes.
9. Press Play. Start at `countPerTeam = 200`, then crank it.

**Mental model for any new unit:** ScriptableObject = numbers, view prefab =
looks, sim prefab = the thing that gets baked. Sim and visuals meet only through
`UnitTypeId` + `AnimState`.

---

## Controls

| Input | Action |
|-------|--------|
| Left-drag | Box-select your units |
| Right-click ground | Move order (routes via flow field) |
| Right-click enemy | Attack order |
| WASD | Move hero |
| 1 / 2 / 3 | Hero aura: Aggressive / Defensive / Default |
| B | Place a building under the cursor |
| N | Remove the nearest building |

---

## Debugging (added so you can report specifics)

Because this is coded blind, there's a full instrumentation layer:

- **`DebugOverlay`** — drop on one GameObject. On-screen HUD (Game view) with
  live counts: units per team, alive/dead, projectiles, role breakdown, units
  under Aggressive/Defensive hero modes, firing/in-contact, selection count,
  flow-field validity + goal cell, obstacle version + blocked-cell count. It also
  draws **Scene-view gizmos** (each individually toggleable): flow-field arrows,
  blocked obstacle cells, per-unit team/facing, lines to each unit's target,
  lines to each desired destination, and selection rings. Gizmo data is
  snapshotted after the sim frame, so it never touches a NativeArray mid-job.
- **Per-component runtime fields** — every MonoBehaviour (`HeroLink`,
  `PlayerCommander`, `AICommander`, `BuildingManager`, both view managers,
  `TerrainFieldBootstrap`) exposes read-only `Debug (runtime)` fields that update
  live in the inspector during play (affected-unit count, selected count, last
  order issued, active/pooled views, building count, world-ready flags…).
- **`SimDebug` singleton** — `SimDebugSystem` fills it each frame; the HUD reads
  it. You can also inspect it directly in the Entities window.

**When something looks wrong, tell me:** the HUD numbers, which gizmo layer is
off (e.g. "flow arrows point into the building", "no target lines", "shields
don't line up"), and any console warnings. That localizes almost any issue.

> Gizmos draw in the **Scene** view during play, not the Game view — keep a
> Scene view visible while testing.

---

## Fixes applied in this pass

- Dead units leave the spatial hash, so corpses are no longer targeted or able
  to deal contact damage.
- The hero no longer creates an `EntityQuery` per frame (leak); it caches one and
  owns its aura entity via `OnEnable`/`OnDisable`.
- All managed scripts guard against a missing/destroyed ECS world and a missing
  `Camera.main`.
- `BuildingManager` and `TerrainFieldBootstrap` destroy the entities they create;
  navigation systems dispose persistent arrays via stored handles.
- The spawner skips unassigned role prefabs (Bakers warn at bake time); ranged
  fire guards against zero projectile speed.

---

## Likely fix-up spots

- **Nothing appears:** confirm `UnitViewManager` has all 4 view prefabs assigned
  and the SubScene baked (Window → Entities → Hierarchy shows units).
- **`LocalTransform.FromPosition` / `FromPositionRotationScale`** — names are
  stable in 1.x; verify against your exact package version.
- **Rewindable allocator** (`state.WorldUpdateAllocator`) is used for transient
  per-frame arrays; on lifetime errors switch to `Allocator.TempJob` + Dispose.
- **ECB singletons** — combat/projectile/death use
  `EndSimulationEntityCommandBufferSystem.Singleton`; present in the default
  world, but verify if you customize bootstrap.
- **`HeroLink.CountAffected`** creates an `EntityQuery` per frame for clarity —
  cache it in `Start` before shipping.
- **Flow field is single-goal** — see the note under pathfinding above.

---

## Scaling caveat

The sim scales well past your target; the ceiling is **one Animator per unit**,
not the simulation — comfortable into the low thousands with off-screen view
culling. Past that, keep this exact bridge but swap the visual backend for
GPU-animated instanced meshes (animation baked to a texture). Unity ships no
built-in entity animation, so plan that swap if counts get large.

---

## Natural next steps

1. **Multi-goal flow-field cache** so separate groups route to separate places.
2. **Facing-aware combat** — more damage from behind (facing is already stored).
3. **Hero civ-swap ability** — structural change via ECB (infantry → knights).
4. **GPU-animated views** for large unit counts.
