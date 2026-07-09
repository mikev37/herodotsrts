# Integration Test Guide
**HeroDOTSRTS — Economy & Building Systems**

All tests are playable in the Unity Editor. No test runner required — they are
observation-based ("you see X happen") supported by the existing `DebugOverlay`
HUD, `SimResultDumper`, and `SnapshotDebug` harness. Run the snapshot round-trip
(F6) after every new feature is wired.

---

## Scene objects required for ALL tests

Place these once; every test below assumes they exist.

| GameObject | Component | Key fields |
|---|---|---|
| `Factory` | `UnitFactory` | `startingGold/Wood/Food`, `playerCount` (roster auto-resolves — no field) |
| `ViewManager` | `UnitViewManager` | `playerColors[0/1]` (roster auto-resolves — no field) |
| `ProjectileVFX` | `ProjectileViewManager` | *(nothing — roster auto-resolves)* |
| `Commander_P0` | `PlayerCommander` | `player = 0` |
| `Commander_AI` | `AICommander` | `player = 1` |
| `Debug` | `DebugOverlay` | all on |
| `Debug` | `SnapshotDebug` | defaults |
| *(one per placement)* | `MapBootstrap` | `definition`, `ownerPlayer`, `count`, `spacing` — see below |

**Placing map objects — one `MapBootstrap` per placement:**

`MapBootstrap` is a component you drop on an empty GameObject and **move to where
you want the thing to spawn — its own `transform.position` is the spawn point.**
Scatter as many as you like across the map, each responsible for one unit /
building / node / obstacle (or a small grid via `count`). Gizmos show each one
(colored by owner, labeled by definition) in the Scene view before you press
Play. There is no list and no marker reference — the GameObject's transform *is*
the position.

You never place a coordinator: the first `MapBootstrap` auto-creates a hidden
`MapBootstrapCoordinator` that spawns them all in a **deterministic global order**
(by `order`, then name, then position) so `StableId`s match on every peer. The
`order` field is only needed if one placement must exist before another; leave
it 0 otherwise.

**The `RosterDefinition` asset — one per project, fully auto-maintained:**

1. `Create → MarbleCombat → Roster`. Name it **`Roster`** and put it in a
   folder named **`Resources`** (e.g. `Assets/Resources/Roster.asset`) —
   required for builds, since the factory/view-manager/projectile-manager
   resolve it via `RosterDefinition.Get()` (`Resources.Load`). It is **never
   hand-wired into any component.**
2. That's it. From then on it maintains itself: creating a
   unit/building/node/wall definition **auto-appends** it to the roster (via an
   `AssetPostprocessor`), deleting one **tombstones** its id, and the list stays
   append-only and deterministic (index = network def id, GUID-ordered, stable
   across the whole team). **You never open or edit it.** A "Resync Now" button
   exists in its inspector for peace of mind, but you shouldn't need it.

---

## 0. Scene sanity (do this first)

**Goal:** the existing combat sim still runs after the rename + new files are
dropped in.

Setup: reproduce your old test scene by scattering `MapBootstrap` GameObjects
instead of the old SubScene spawner. Create two empty GameObjects:

```
GameObject "P0 Army" at (0,0,-10):  MapBootstrap { definition: Warrior, ownerPlayer: 0, count: 20 }
GameObject "P1 Army" at (0,0, 10):  MapBootstrap { definition: Warrior, ownerPlayer: 1, count: 20 }
```

(The GameObject's transform position IS the spawn point — move it in the Scene view.)

Press Play. Expected:

- DebugOverlay HUD shows `P0: 20  P1: 20` (not "T0/T1").
- Units move toward each other and fight.
- Console: `[Snapshot] restored tick …` on F6 with hashes matching.
- No `NullReferenceException` mentioning `UnitManager` (it's retired; every
  reference routes through `UnitFactory` or `UnitViewManager` now).

---

## 1. Units with the new system

**What changed:** `UnitManager.SpawnUnit` → `UnitFactory.Create`. The roster is
global (one def-id space, no per-player lists). `Player` component replaces
`Team`.

**Setup:**

Add your unit defs to the project, open the `Roster` asset, and click "Rebuild From Project". Add a `MapBootstrap` GameObject per unit type, positioned where you want them.

**Verify:**

- Inspector on a live unit entity (Entities window) shows `Player { Value: 0 }`
  (not `Team`).
- `UnitViewManager` tints units with the `playerColors` entry matching their
  `Player.Value`.
- Adding a new unit def at the END of the roster compiles and spawns with no
  id collision.
- Snapshot round-trip (F6) restores units to exact positions.

---

## 2. Buildings (obstacle + nav)

**What changed:** buildings are still `BuildingDefinition : UnitDefinition`,
spawned by `UnitFactory.Create`. The footprint snaps, `Obstacle` is stamped, the
nav grid rebuilds.

**Setup:**

Create `TowerDef` (`Create → MarbleCombat → Building Definition`):

```
displayName:   Tower
footprintX/Z:  2
maxHealth:     500
```

Add to the roster (Rebuild From Project). Add a `MapBootstrap` GameObject with
`ownerPlayer: -1` (neutral obstacle).

**Verify:**

- Building appears snapped to the nav grid.
- Units pathfind around it (flow-field gizmo arrows curve around the footprint).
- `[Snapshot] restored tick …` on F6: building reappears at correct position.
- Demolish command (`PlayerCommander` → select building → demolish key) removes
  it; nav grid clears on next tick.

---

## 3. Abilities

**What changed:** `AbilitySpec.Cost` is now `ResourceAmount` (Gold/Wood/Food
struct). Cost is checked against and debited from the **player's economy bank**
(not the old `TeamResources` pool) atomically at cast-commit.
`AbilityDefinition.costFood` replaces `costStone`.

**Setup:**

Create `FireballDef` (`Create → MarbleCombat → Ability Definition`):

```
displayName:   Fireball
costGold:      50
costFood:      0
manaCost:      20
castRange:     12
```

Add to a unit's `abilities[0]` in its `UnitDefinition`. Ensure
`UnitFactory.startingGold = 200`.

**Verify:**

- Selecting the unit and pressing ability key spends 50 gold from the HUD
  readout (`Gold 200 → 150`).
- Casting with `Gold = 0` fizzles silently (no effect, no crash).
- `costStone` field is gone from the inspector; only `costGold/Wood/Food`
  appear.
- Snapshot round-trip mid-ability-cooldown restores the cooldown timer.

---

## 4. Obstacles (rocks, trees — terrain, not buildings)

**What is tested:** a dumb terrain obstacle that blocks movement and sight but is
never owned, never attacked, and never damaged — with no combat/economy/ability
baggage in its inspector.

**Use `ObstacleDefinition`, NOT `BuildingDefinition`.** An obstacle is its own
lean asset type (`Create → MarbleCombat → Obstacle Definition`). It derives from
BuildingDefinition only for spawn/roster plumbing; its custom editor hides all the
combat/economy/mana/upgrade fields, so authoring a rock shows only footprint,
vision, invulnerability, and view variants.

**Invulnerability is NonCombatant — never a giant maxHealth.** With
`invulnerable = true` (the default), the spawn tags the entity `NonCombatant`;
targeting and combat skip NonCombatant entirely, so the rock literally cannot be
selected as a target or take damage. There is no health hack. (Untick
`invulnerable` only for a destructible obstacle, which then reveals a real
maxHealth.)

**Setup:**

Create `RockDef` (`Obstacle Definition`):

```
displayName:        Rock
footprintX/Z:       3
invulnerable:       true          (→ NonCombatant at spawn; this is the invuln mechanism)
occluderHeight:     4             (blocks sight up to 4 units)
viewPrefabVariants: [rock1..rock6] (optional — deterministic per-entity mesh pick)
```

Add to roster (auto-appended — `t:UnitDefinition` matches the subclass).
MapBootstrap placement `ownerPlayer: -1`.

**Verify:**

- Units path around the rock; no unit ever walks through it.
- Neither player can select or issue an attack order on it (targeting skips
  `NonCombatant`), and it takes zero damage from anything.
- If `viewPrefabVariants` is set, each rock instance shows one of the meshes,
  and the SAME entity shows the SAME mesh on every client (deterministic from
  StableId — lockstep-safe).
- Sightlines are blocked by the rock up to `occluderHeight` (a low `occluderHeight`
  or 0 lets raised shooters see over it).
- Rock survives a snapshot round-trip at the same grid cell (all state is
  def-derived, regenerated via `UnitFactory.Create`).

---

## 5. Resource Node

**What is tested:** a `ResourceNodeDefinition` that holds a finite amount of
Gold or Wood, depletes as harvesters pull from it, and optionally despawns when
empty.

**Setup:**

Create `GoldNodeDef` (`Create → MarbleCombat → Resource Node Definition`):

```
displayName:        Gold Node
resourceType:       Gold
amount:             1000
despawnWhenDepleted: true
huskLingerSeconds:  3
footprintX/Z:       2
```

Add to roster. a `MapBootstrap` GameObject at (10, 0, 0) with `ownerPlayer: -1`.

**Verify:**

- Node appears in the scene. `DebugOverlay` or Entities window shows it has a
  `ResourceBank` with `Amounts.Gold = 1000`.
- After harvesters deplete it (see test 8), the entity despawns after 3 seconds.
- A node with `despawnWhenDepleted = false` stays as a permanent husk.

---

## 6. Forest (Wood resource node)

**What is tested:** a wood node set up as a cluster with small per-node capacity
— abundant, consumable, spread across the map.

**Setup:**

Create `WoodNodeDef` (`ResourceNodeDefinition`):

```
displayName:        Tree
resourceType:       Wood
amount:             200
despawnWhenDepleted: true
huskLingerSeconds:  1
footprintX/Z:       1
```

Add to roster. Add a `MapBootstrap` GameObject with `count: 8, spacing: 3` to simulate a forest
(or scatter several single-tree bootstraps).

**Verify:**

- Eight trees appear.
- Harvesters hitting an empty tree automatically re-path to the nearest
  remaining tree of the same type (`HarvestTask.NodeStableId` updates in the
  Entities window when one depletes).
- Trees disappear one by one as harvesters work.
- The last tree despawns; harvesters enter `HarvestPhase.Idle`.

---

## 7. Capitol / Player Bank

**What is tested:** the capital building that acts as depot + intake, feeding
harvested resources into the player's `ResourceBank`. The HUD reads from this.

**Setup:**

Create `CapitalDef` (`BuildingDefinition`):

```
displayName:  Capital
isDepot:      true
isIntake:     true
footprintX/Z: 6
maxHealth:    2000
isProducer:   true        (optional — produces Peasants later)
produces:     [PeasantDef]
```

Add to roster. a `MapBootstrap` GameObject at (0,0,-25) with `ownerPlayer: 0`.

Set `UnitFactory.startingGold = 200, startingWood = 200, startingFood = 100`.

**Verify:**

- HUD shows `Gold 200  Wood 200  Food 100` immediately on Play.
- When a harvester delivers to the Capital (see test 8), HUD gold count rises.
- Pausing the bank (`ToggleBankPause` command on the capital's bank entity)
  stops intake accumulation; unpause resumes it.
- Snapshot round-trip preserves the exact bank amounts (`BankRecord` in the blob).

---

## 8. Harvesters bringing resources back

**What is tested:** the full harvest loop — Peasant walks to a Gold Node,
pulls resources, returns to the Capital, delivers. HUD count rises.

**Setup (builds on 5 + 7):**

Create `PeasantDef` (`UnitDefinition`):

```
displayName:    Peasant
speed:          2.5
harvestRate:    5          ← resources/tick pulled from a node
carryCapacity:  50         ← triggers return-to-depot when full
buildPower:     10         ← also a builder
prodCostGold:   0
prodCostFood:   1
productionTime: 8
```

Add to roster. Add 3 `MapBootstrap` GameObjects (`ownerPlayer: 0`, or one with `count: 3`) near the Gold Node.

**Issue the Harvest command:**
In `PlayerCommander`: select peasants → right-click the Gold Node (this sends
`CommandKind.Harvest` with `TargetStableId` = node's `StableId`).

**Verify (step by step):**

1. `HarvestTask.Phase` in Entities window changes: `Idle → ToNode → Gathering → ToDepot → Depositing → ToNode …`
2. Node's `ResourceBank.Amounts.Gold` decreases as peasants pull.
3. HUD `Gold` count increases each time a peasant delivers.
4. When node depletes, peasants auto-route to the nearest other Gold Node. If
   none remain, `Phase = Idle`.
5. Snapshot round-trip mid-delivery: peasant resumes its phase from the same
   position.

---

## 9. Colony and Hauler

**What is tested:** a Colony building that acts as a local depot, accumulates
resources from nearby harvesters, and automatically builds a free Hauler unit
when full, which then walks the resources to the Capital and dies on delivery.

**Setup:**

Create `CartDef` (`UnitDefinition`):

```
displayName:   Ox Cart
isHauler:      true
carryCapacity: 300
speed:         5
foodCost:      0       ← haulers are free to sustain
mass:          5
```

Create `ColonyDef` (`BuildingDefinition`):

```
displayName:    Colony
isDepot:        true
isColony:       true
isIntake:       false    ← does NOT feed the bank directly
haulerUnit:     CartDef
haulThreshold:  200      ← triggers a hauler when holdings reach this
footprintX/Z:   4
```

Add both to roster. Place a `MapBootstrap` GameObject at (20,0,0) with the Colony def, `ownerPlayer: 0`.
Place peasants near a resource node at `(30, 0, 0)`.

Command peasants to harvest the node.

**Verify:**

1. Peasants deliver to the Colony (nearest depot), not the Capital.
2. Colony's `ResourceBank.Amounts` accumulates in the Entities window.
3. When Colony holdings reach `haulThreshold (200)`, a Cart entity spawns at
   the Colony. Its `HarvestTask.Phase = ToDepot`, `DepotStableId` = Capital's
   `StableId`.
4. Cart walks to the Capital. On arrival, Capital bank rises by the cart's
   cargo. Cart entity is destroyed.
5. Colony can immediately begin accumulating again.
6. Snapshot mid-transit: Cart resumes its walk from the same position.

**Relay faction variant:** set `haulerUnit` to null. The Colony itself should
morph into a mobile unit and walk. (Currently routed through `HaulSystem`'s
relay path rather than `ColonyOxCartSystem`'s spawn path — verify the Colony
entity gains a `MoveTarget` toward the Capital and loses `BuildingTag` on
trigger.)

---

## 10. Barracks that build units

**What is tested:** a producer building with a unit queue, production cost, build
time, and a rally point.

**Setup:**

Create `InfantryDef` (`UnitDefinition`):

```
displayName:    Infantry
prodCostGold:   50
prodCostFood:   1
productionTime: 10
speed:          4
```

Create `BarracksDef` (`BuildingDefinition`):

```
displayName:   Barracks
isProducer:    true
produces:      [InfantryDef]
costGold:      150
buildTime:     60
footprintX/Z:  4
```

Add both to roster. Place a `MapBootstrap` GameObject with the Barracks def (owned by player 0)
— or build it via Peasant (test 11).

**Verify:**

1. Select Barracks → press `Q` (or the first produce key). `ProductionItem` is
   appended to the building's buffer in the Entities window.
2. `EconomyQuery.GetActivity(em, barracksEntity)` returns
   `Kind = Production, Progress01 = 0..1, DisplayDefId = InfantryDef id`.
3. After `productionTime` ticks, an Infantry unit spawns at the Barracks.
4. If a `RallyPoint` is set (`G` key on the Barracks in `PlayerCommander`), the
   unit walks to the rally position.
5. Queue 3 Infantry. Second only starts when the first completes. Cancel the
   second in queue via the cancel command — it disappears without charge.
6. HUD `Gold` drops by 50 per unit produced.

---

## 11. Peasants that build Barracks

**What is tested:** the construction pipeline — Peasant issues a `PlaceBlueprint`
command, a scaffold entity appears at cost, Peasant walks up and applies
`BuildPower`, the building completes when `Construction.Progress >= BuildTime`.

**Prerequisite:** `PeasantDef` has `buildPower: 10` and `builds: [BarracksDef]`.

**Verify (step by step):**

1. Select a Peasant. Press the build key for Barracks (first entry in `builds`).
   A ghost preview appears; click to place.
2. A Barracks entity spawns with `Construction` component:
   `Progress = 0, BuildTime = 60, Health.Current = 1`.
3. `EconomyQuery.GetActivity` on the Barracks returns `Kind = Construction,
   Progress01 = 0..1`.
4. Peasant walks to the Barracks. When within contact radius, `BuildPower`
   contributes to `Construction.Progress` each tick.
5. Multiple Peasants stack their `BuildPower`; the bar fills faster.
6. Player bank is drawn incrementally (`ResourceBank.Amounts.Gold` falls as
   construction proceeds — pay-as-you-build).
7. At `Progress = BuildTime`, `Construction` component is removed. Building is
   fully operational (can accept production queue).
8. **Mutual exclusion:** while under construction, pressing the production key
   on the scaffold has no effect (`BuildingBusy` returns `Construction`).
9. Snapshot round-trip mid-construction: `Construction.Progress` and
   `Construction.Paid` survive exactly; build resumes.

---

## 12. Buildings that transform into units (Morph — free)

**What is tested:** a building-to-unit (or unit-to-building) free morph.
The canonical case is a trebuchet that sieges in place.

**Setup:**

Create `TrebuchetMobileDef` (`UnitDefinition`):

```
displayName:  Trebuchet (Mobile)
morphTarget:  TrebuchetSiegedDef
morphTicks:   20
speed:        2
```

Create `TrebuchetSiegedDef` (`BuildingDefinition`):

```
displayName:  Trebuchet (Sieged)
morphTarget:  TrebuchetMobileDef    ← back-reference for toggle
morphTicks:   20
isRanged:     true
footprintX/Z: 2
costGold:     0   ← free morph
```

Add both to roster. Place a `MapBootstrap` GameObject with the `TrebuchetMobile` def for player 0.

**Verify:**

1. Select Trebuchet → press `G` (morph key in `PlayerCommander`). `MorphState`
   component appears on the entity: `TargetDefId = TrebuchetSiegedDef id,
   BuildTime = 20, Cost = zero`.
2. `EconomyQuery.GetActivity` returns `Kind = Upgrade, Progress01 = 0..1,
   DisplayDefId = TrebuchetSiegedDef id`.
3. After 20 ticks, `MorphSystem` calls `ApplyStats` with `preserveVitals: true`.
   The entity now has `BuildingTag`, `Immobile`, `Obstacle`. `UnitViewManager`
   detects `UnitDefId` change and swaps the view prefab.
4. Press `G` again: it morphs back to mobile. `BuildingTag`/`Immobile`/`Obstacle`
   are removed; entity moves again.
5. **Mutual exclusion:** during the morph, no other command (produce, research)
   takes effect on the entity.
6. Snapshot during morph: `MorphState.Progress` survives; morph completes
   correctly on restore.

---

## 13. Units that upgrade to other units (Paid upgrade via Research)

**What is tested:** Knight → Paladin. A tech is researched at a building
(paid, takes time). On completion: all existing Knights auto-morph to Paladins
(free), and future Knights produced at any Barracks come out as Paladins.

**Setup:**

Create `KnightDef` (`UnitDefinition`):

```
displayName:    Knight
prodCostGold:   80
productionTime: 12
```

Create `PaladinDef` (`UnitDefinition`):

```
displayName:  Paladin
prodCostGold: 0   ← production cost irrelevant; produced via substitution
attackDamage: 35
```

Create `KnightToPaladin` (`Create → MarbleCombat → Tech Definition`):

```
displayName:      Knight → Paladin
fromUnit:         KnightDef
toUnit:           PaladinDef
upgradeMorphTicks: 8
costGold:         200
researchTime:     60
```

Create `BlacksmithDef` (`BuildingDefinition`):

```
displayName: Blacksmith
researches:  [KnightToPaladin]
footprintX/Z: 3
```

Add all to roster. Place: Capital (player 0), Blacksmith (player 0), 3 Knights
(player 0) in `MapBootstrap`.

**Verify:**

1. Select Blacksmith → press the research key for `KnightToPaladin`.
   `ResearchTask` component appears on the Blacksmith.
2. `EconomyQuery.GetActivity` returns `Kind = Research, Progress01 = 0..1,
   DisplayDefId = PaladinDef id`.
3. Player bank is drawn over `researchTime` (pay-as-you-build). Gold drops by
   200 total.
4. On completion: all 3 Knights gain a `MorphState` (free, 8 ticks). They
   become Paladins. `UnitDefId` changes; views swap.
5. `ResearchedTech { FromDefId = Knight, ToDefId = Paladin }` appears in the
   player bank entity's buffer.
6. Queue a Knight at the Barracks. It spawns as a **Paladin** (production
   substitution via `ProductionSystem.SubstituteTech`).
7. Snapshot round-trip: `ResearchedTech` buffer survives; a Knight queued
   after restore still comes out as a Paladin.
8. **Mutual exclusion:** pressing the produce key on the Blacksmith while it's
   researching has no effect.

---

## 14. Relay Tower (resource transport without haulers)

**What is tested:** a relay-faction setup where a chain of Relay buildings
streams resources from a Colony to a Capital without hauler units.

**Setup:**

Create `RelayTowerDef` (`BuildingDefinition`):

```
displayName: Relay Tower
isRelay:     true
relayRate:   20     ← resources/tick streamed along the chain
relayRange:  25     ← how far it reaches to the next relay or capital
footprintX/Z: 2
```

Leave `ColonyDef.haulerUnit` blank (no hauler unit = relay path).
Place: Capital at `(0,0,-30)`, Relay at `(0,0,-10)`, Colony at `(0,0,10)`.

**Verify:**

1. `RelaySystem` builds a union-find graph each tick. Capital + Relay + Colony
   form one connected component.
2. Colony harvests accumulate. On each tick, `relayRate` resources flow from
   Colony → Relay → Capital automatically (no unit movement).
3. Move the Relay out of range: the chain breaks. Capital stops receiving. Move
   it back: resumes on the next tick.
4. Snapshot: Relay component values survive; relay chain reconstructs on restore.

---

## 15. Mutual exclusion across all building states

**What is tested:** a building can do exactly ONE of: construction, production,
upgrade, research — at any given time. All other commands are rejected until the
current job finishes.

**Setup:** use the Barracks from test 10 and the Knight→Paladin Blacksmith from
test 13.

**Verify (each combination):**

| Building state | Command attempted | Expected result |
|---|---|---|
| Under `Construction` | Queue Infantry | Rejected. `EconomyQuery.BuildingBusy` returns `Construction`. |
| Under `Construction` | Start research | Rejected. |
| Producing Infantry | Start research | Rejected. `BuildingBusy` returns `Production`. |
| Producing Infantry | Start upgrade/morph | Rejected. |
| Researching | Queue production | Rejected. `BuildingBusy` returns `Research`. |
| Morphing | Any command | Rejected. `BuildingBusy` returns `Upgrade`. |
| Idle | Queue production | Accepted. |

Also verify the **sim-layer guard**: `ProductionSystem` query is
`WithNone<Construction, MorphState, ResearchTask>` — even if a command slips
through (e.g. from a replay), the building never advances production while
building/upgrading/researching.

---

## 16. Progress bar, queue, and UI data

**What is tested:** `EconomyQuery.GetActivity` and `EconomyQuery.GetQueue`
return correct data for all building states.

**No UI required** — call from a test `MonoBehaviour` in `Update` and log:

```csharp
var info = EconomyQuery.GetActivity(em, barracksEntity);
Debug.Log($"Kind={info.Kind} Progress={info.Progress01:P0} " +
          $"DefId={info.DisplayDefId} Queue={info.QueueCount}");

using var ids = new NativeList<int>(4, Allocator.Temp);
EconomyQuery.GetQueue(em, barracksEntity, ids);
for (int i = 0; i < ids.Length; i++)
    Debug.Log($"  Queue[{i}] = {roster.GetDefinition(ids[i])?.displayName}");
```

**Verify each state:**

| State | Kind | Progress01 | DisplayDefId |
|---|---|---|---|
| Under construction | `Construction` | 0→1 | The building's own def id |
| Producing Infantry | `Production` | 0→1 per unit | Infantry def id |
| Morphing | `Upgrade` | 0→1 | Target def id |
| Researching | `Research` | 0→1 | `toUnit` def id |
| Idle | `None` | 0 | -1 |

Queue log shows each queued unit's `displayName` in order.

---

## 17. Snapshot round-trip (economy state)

**This is the netcode-correctness gate. Run it after every new feature is wired.**

Uses the existing `SnapshotDebug` (F6 = round-trip self-test).

**Setup:** get the sim into a rich mid-economy state — harvesters in transit, a
building under construction, a unit queued, a morph in progress, a research task
running, player bank at a non-zero amount.

Press **F6**.

**Expected console output:**

```
[Snapshot] round-trip OK at tick NNN: hash XXXXXXXX == XXXXXXXX (YYYYY bytes).
```

**If hashes differ**, the serializer is missing a field. The component most
likely to be missing is named in the error. Cross-check against the
`EconUnitRecord` struct in `SimSnapshot.cs` — every flagged component must have
a matching `Has*` flag, structural add, and `SetComponentData` in `Restore`.

**Also test:**

- F10 (save) → change something → F11 (load): sim restores to the saved state.
- Mid-construction save/load: `Construction.Progress` resumes exactly.
- Mid-research save/load: `ResearchTask.Progress` resumes exactly.
- Mid-morph save/load: `MorphState.Progress` resumes exactly.
- `ProductionItem` buffer survives: queued units are still queued after restore.
- `ResearchedTech` buffer survives: Knight still produces as Paladin after restore.

---

## 18. Checksum / desync detection

**What is tested:** the per-tick lockstep checksum now covers `ResourceBank`
amounts (via `LockstepHash.Bank` / `BankChecksumJob`), so a divergent bank
between two peers is caught within one tick.

**Single-editor test (uses `SnapshotDebug.corruptKey = F8`):**

1. Press F8: corrupts one unit's health locally by 1. The next `SimChecksum`
   logged will differ from what a second peer (or a replay) would produce.
2. In network mode (two MPPM virtual players): corrupt on the client → the
   host's next `ls_ack` disagrees → resync fires → snapshot restores.

**Verify:**

- Console logs `[Lockstep] checksum mismatch at tick N` within one tick of
  corruption.
- Economy divergence test: use a debug key to manually add 1 Gold to the
  player bank on one peer. The bank hash (`LockstepHash.Bank`) should catch
  this on the same tick. (The bank is covered by `BankChecksumJob`; this
  proves it works.)

---

## 19. Any other new functionality

### Self-building (Protoss-style auto-build)

Create a `PylonDef` (`BuildingDefinition`) with `selfBuildPower: 50`. Place it
via `PlaceBlueprint`. It contributes its own `BuildPower` to itself in
`ConstructionSystem.SelfPower`; no Peasant required. Verify `Progress` advances
without any builder unit nearby.

### Sacrifice-to-build

Create a `TotemDef` (`BuildingDefinition`) with `sacrifice: true`. When a
Peasant walks onto the scaffold, `ConstructionSystem` destroys the Peasant and
the Totem's `SelfPower` completes it instantly. Verify: Peasant entity is
destroyed at arrival; Totem becomes operational.

### Bank pause

Select a Capital. Issue `ToggleBankPause`. `ResourceBank.Paused = 1`. Harvesters
still deliver, but the bank refuses `BankRequest`s — nothing drains to the
player bank. Intake stops accumulating. Unpause resumes it.

### Spend priority

Select a building. Issue `ToggleSpendPriority`. `SpendPriority.High` toggles.
High-priority buildings (construction sites, producers on High) are funded first
when the bank is tight. Low-priority items wait. Verify with a starved bank: the
High building advances while the Low building stalls.

### Cancel + refund

Mid-construction: issue a `CancelProduction` command. `Construction.Paid` is
refunded to the player bank exactly (`bank.Amounts += construction.Paid`).
Mid-production: cancel the head item after `Started = 1` and cost is partially
charged — the charged portion is refunded.

### Node view (depletion visual)

Place a `ResourceNodeDefinition` with a `NodeView` component on its view prefab.
`NodeView.Fill` is pushed by `UnitViewManager` each frame (`Amounts / Capacity`
ratio). At 0, the view should play the `husk` transition (model swap or
particle). Verify the fill ratio decreases as harvesters work.

### Construction view (progress visual)

Place a `ConstructionView` on the building view prefab. Verify its `progress`
field (`0→1`) is pushed by `UnitViewManager` during construction, and the visual
scaffold fades/rises correctly. After completion, the view reverts to the fully
built model.

---

## Quick reference: SO fields by feature

| Feature | SO type | Key fields |
|---|---|---|
| Harvester | `UnitDefinition` | `harvestRate`, `carryCapacity` |
| Builder | `UnitDefinition` | `buildPower`, `builds` list |
| Production unit | `UnitDefinition` | `prodCostGold/Wood/Food`, `productionTime`, `foodCost` |
| Hauler | `UnitDefinition` | `isHauler`, `carryCapacity`, `foodCost: 0` |
| Free morph (toggle) | `UnitDefinition` | `morphTarget`, `morphTicks` |
| Resource node | `ResourceNodeDefinition` | `resourceType`, `amount`, `despawnWhenDepleted` |
| Capital | `BuildingDefinition` | `isDepot: true`, `isIntake: true` |
| Colony | `BuildingDefinition` | `isDepot: true`, `isColony: true`, `haulerUnit`, `haulThreshold` |
| Producer | `BuildingDefinition` | `isProducer: true`, `produces` list |
| Constructible building | `BuildingDefinition` | `costGold/Wood/Food`, `buildTime` |
| Paid upgrade / morph | `BuildingDefinition` | `upgrades` list (target provides cost + buildTime) |
| Relay | `BuildingDefinition` | `isRelay: true`, `relayRate`, `relayRange` |
| Tech upgrade | `TechDefinition` | `fromUnit`, `toUnit`, `costGold/Wood/Food`, `researchTime`, `upgradeMorphTicks` |
| Research building | `BuildingDefinition` | `researches` list → add `TechDefinition` assets |

---

## Likely fix-up spots

- **HUD shows no resources:** `UnitFactory.startingGold/Wood/Food` is 0 — set
  non-zero defaults. `PlayerBankRegistrySystem` must run before the HUD reads
  (`PlayerCommander.Update` fires after ECS frame, so timing is fine).
- **Harvesters idle at node:** `harvestRate = 0` or `carryCapacity = 0` in the
  `UnitDefinition`. Both must be > 0.
- **Colony never triggers a hauler:** check `Colony.Threshold` vs actual bank
  accumulation rate; also verify `haulerUnit` is assigned and is in the roster.
- **Research completes but no Knight→Paladin morph:** `TechDefinition.fromUnit`
  and `toUnit` must both be in the roster. `ResearchSystem.GatherByPlayerType`
  uses `UnitDefId` — confirm the Knights were spawned with the correct def id.
- **Snapshot hash mismatch after economy features:** a component is not in
  `EconUnitRecord`. Check `SimSnapshot.cs` — the `Has*` flag, structural add in
  `Restore`, and `SetComponentData` call must all be present for every flagged
  component.
- **Building accepts commands while busy:** `EconomyQuery.BuildingBusy` is not
  being called in the command handler. Every handler for Produce/Upgrade/Research
  must call `BuildingBusy` and return early if not `None`.

---

## Design notes (answers to authoring questions)

### Resource type: why it's on the node, not the depot

A **depot accepts every resource type** — a harvester arrives with whatever
cargo it's carrying and the depot deposits it as-is (`DepotJob` requests the full
cargo `ResourceAmount`, no type filter). So a per-depot "resource type" would be
meaningless and was removed from `BuildingDefinition`. **`resourceType` now lives
only on `ResourceNodeDefinition`**, where it means the single type that node
*yields*. Units also carry any type (cargo is a full `ResourceAmount`; a
harvester's `HarvestTask.Carrying` is just a selector for which node it's
currently working). There is no "this building only handles Gold" concept — and
you don't need one.

### Producer / Researcher role flags (consistency, resolved)

`isProducer` and `isResearcher` are parallel role flags. Each reveals and gates
its list in the inspector: `isProducer` → the produces list (and stamps the
`ProducerTag` the runtime query iterates each tick); `isResearcher` → the
`researches` list (gated at the research command and the build-menu). A building
can be both. Building upgrades remain capability-by-list (`buildingUpgrades`
non-empty). This replaced the earlier "no isResearcher, capability = list
non-empty" rule — the explicit flag is what you asked for and what ships, so the
two economy roles now read uniformly in the inspector.

### Colony vs Relay — they solve the SAME problem two ways

Both move a colony's harvested resources back to a capital. The difference is the
transport mechanism:

- **Colony** is a depot with **no intake** (it doesn't feed the player bank
  directly). It accumulates, and when its holdings reach `haulThreshold` it
  **builds a `haulerUnit` cart** that physically drives the resources to the
  nearest capital and despawns. Transport = **moving units**. Costs unit supply,
  can be intercepted, needs pathable terrain.

- **Relay** replaces the carts with a **stationary graph**. Relay towers are
  wires: a colony connected (through a chain of towers within `relayRange`) to a
  capital **streams `relayRate`/tick** straight into that capital's bank — no unit
  moves. Transport = **network of buildings**. Can't be intercepted, ignores
  terrain, but costs the towers and only works while the chain is intact.

You pick per faction: give the colony a `haulerUnit` for the cart model, or leave
it blank and place relay towers for the streaming model. A capital itself is the
third role: a depot **with** intake (`isIntake`), which is the only thing that
actually deposits into the player bank. So:

| Building | isDepot | isIntake | feeds bank | transport out |
|---|---|---|---|---|
| Capital  | yes | yes | directly | — (it's the sink) |
| Colony (cart) | yes | no | via haulers | builds `haulerUnit` carts |
| Colony (relay) | yes | no | via relay chain | streamed by relay towers |
| Relay tower | no | no | no | is the wire |

So your mental model was right: a colony *is* "a depot with no intake." The relay
is just an alternative to carts for getting a colony's holdings to the capital.

---

## Combat: buildings and structures (answers + behavior)

### Most buildings don't attack — canAttack is opt-in

`BuildingDefinition.canAttack` defaults to **false**. A house, farm, barracks,
depot, or wall never attacks and is never treated as a threat. The attack fields
(damage, range, melee/ranged, projectile) are **hidden in the inspector** until
you tick `canAttack`, and the attack is **zeroed at spawn** when it's off — so a
non-combat building can't fire even if damage values were left over from a
duplicated asset. Only a deliberate defensive structure (tower, gate-gun, keep
with arrow slits) opts in.

### A defensive building CAN melee or ranged attack (ranged = KNOWN ISSUE)

When `canAttack` is on, a building is meant to fight via `TowerTargetingSystem`
(it's Immobile, so the normal mobile `BehaviorSystem` targeting skips it): pick
the nearest in-range enemy, commit, and let `AttackTimerSystem` run the charge/
fire cycle — projectile if `isRanged`, melee strike if not. Towers can't rotate,
so the facing gate mobile units obey is bypassed for them.

Ranged towers fire via true 2.5D height-occlusion line of sight (NavTerrain.
SightLine), NOT the walkability probe. Each cell has an OccluderHeight (terrain
surface + a building's occluderHeight or a wall's parapet), and sight from an eye
at (viewer surface + eyeOffset) to a target is open iff no intervening column
rises above the straight eye→target line. Set a tower's eyeOffset ABOVE its own
building occluderHeight and it sees and shoots over lower walls; a ground unit
behind the same wall is blocked. Perception range and the sight-ray cap grow with
a ranged unit's attackRange so a long-range tower actually perceives what it can
hit. This replaced the earlier walkability LoS, which broke at sheer roof edges
and blinded a tower to its own surroundings.

### Spikes and palisades — buildings deal contact damage, never take it

A building takes damage ONLY from real attacks (melee strikes and projectiles) —
never from ramming or bodies brushing past. A ram is not a unit driving at speed
into a wall; units milling next to a structure must not chip it, so ramming
(mass × closing speed) is mobile-vs-mobile only and buildings are exempt as both
rammer and victim.

A building can DEAL contact damage: set `BuildingDefinition.contactDamage`
(per second) to make it a spike wall or palisade. Any enemy unit touching it
takes that damage per second, applied receiver-side (the unit reads the building
in its ContactList exactly as it reads a neighbor's melee strike — no new
system, no ECB). Independent of `canAttack`: a palisade bites without attacking.

Buildings also take damage FLAT — armor only. A building has no facing (it
doesn't rotate), so shield-arc mitigation and backstab bonuses are meaningless
for it; `shield` is hidden in the building inspector and zeroed at spawn. Units
still get full directional shield/backstab.


### Buildings are rectangles — range is measured to the footprint edge

A building's nav footprint is an axis-aligned RECTANGLE (extents × cell size),
not a circle. All range checks against a building — perception distance, melee
engage range, the contact-strike test, and the contact-add gate — measure the
true distance to the footprint EDGE (CombatMath.DistanceToFootprint), carried as
UnitInfo.HalfExtents. This fixes melee on non-square buildings: a unit at the
middle of a 4×8 keep's long wall is AT the edge (distance ~0) and strikes it,
instead of being told it's "inscribed-radius" units short and walking into the
wall. It's symmetric — a building attacker (palisade/tower) reaches from its
own footprint edge too. The box distance is the single primitive to extend when
footprints become non-rectangular (planned).

### Units attacking buildings — melee and ranged

Ordering a unit to attack a building now works for both. The building is resolved
through the unit's ContactList (structures are never auto-picked by instinct —
you must give the order — but once ordered, the unit advances onto the building,
and a ranged unit fires while a melee unit strikes on contact). A unit ordered
onto a **distant** building walks to it until it's close enough to engage, rather
than standing still. Passive body-ramming does not apply against buildings (a
unit doesn't take chip damage for standing against a wall); damage to a structure
comes only from deliberate attacks.

---

## Steering: units no longer bounce off buildings/obstacles

Symptom: a unit walking *near* a tower (not attacking, not pressed against the
red footprint cells) was flung away as if struck, and the shove *persisted after
the unit had left contact* — reading exactly like a melee knockback.

Cause: the obstacle-avoidance push in SteeringSystem scaled with mere *proximity*
to blocked cells (a quadratic of the summed cell normals × a large constant),
applied as a raw velocity injection every frame. A unit passing tangentially near
a big flat wall got a full outward shove, and because that velocity fed the
seek-momentum integrator, the unit kept coasting outward for several frames after
clearing the wall.

Fix (all obstacles, not just buildings):
- Penetration is now TRUE overlap depth in world units (`touchDist - dist`, where
  `touchDist = cellHalfWidth + bodyRadius`), so a unit merely *passing near* a
  wall has zero penetration and skips the push entirely — no shove.
- SLIDE (cancel the into-wall motion component) is the primary response; a
  tangentially-moving unit slides freely.
- PUSH corrects only genuine overlap, clamped to exactly clear it that frame
  (`min(depth/Dt, locomotion)`), so it can't fling a unit past the wall.
- MOMENTUM DAMP zeroes wall-directed seek momentum, so the response dies the
  instant the body is clear — no post-contact coasting.

Walls still stop units (overlap is caught a full body-radius out, before any
tunneling); they simply no longer bounce. No new constants — the depth-based push
is self-scaling, so the old ObstacleStrength/SurfaceBand tunables were removed.
