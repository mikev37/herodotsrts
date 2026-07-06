# Herodotus RTS — Deterministic ECS Battle Simulation

A large-scale real-time-strategy battle sim built on **Unity 6 DOTS** (Entities 1.2). The simulation is a pure, deterministic, Burst-compiled ECS world; rendering, input, and networking are thin layers bolted onto its edges. The whole thing is engineered around one property — **bit-exact determinism** — because that property is what makes lockstep networking, instant replay, save/load, and desync recovery all fall out of the *same* mechanism.

> If you read one thing, read [The determinism contract](#the-determinism-contract). Every non-obvious design choice in this codebase exists to protect it.

---

## Table of contents

- [What this is](#what-this-is)
- [The determinism contract](#the-determinism-contract)
- [Architecture at a glance](#architecture-at-a-glance)
- [The tick pipeline](#the-tick-pipeline)
- [Subsystems in depth](#subsystems-in-depth)
- [The data model](#the-data-model)
- [Abilities & modifiers](#abilities--modifiers)
- [Navigation](#navigation)
- [The commander layer](#the-commander-layer)
- [Networking (lockstep)](#networking-lockstep)
- [Snapshots: one mechanism, four jobs](#snapshots-one-mechanism-four-jobs)
- [The view layer](#the-view-layer)
- [Running & testing](#running--testing)
- [File map](#file-map)
- [Extending the sim](#extending-the-sim)

---

## What this is

Thousands of units push, ram, shoot, and cast on heightmapped terrain, organized into formations, commanded either by a human or an AI, and kept perfectly in sync across the network without replicating a single position. Design goals, in priority order:

1. **Determinism first.** Identical inputs ⇒ identical state, bit for bit, on every machine and every replay.
2. **Emergent physicality.** Downhill chargers hit harder, shields brace and hold a line, units slide along walls — these are consequences of the math, not special cases.
3. **Data-driven content.** Units, abilities, buildings, walls, and projectiles are authored as `ScriptableObject` definitions; no per-unit code.
4. **Clean layer separation.** The sim never references an `Animator`, a `Camera`, a `GameObject`, or a network variable. Those live outside and read the sim.

**Environment:** Unity `6000.0.3f1` · Entities & Entities Graphics `1.2.4` · Netcode for GameObjects `2.12.0` · URP `17.0.3`.

---

## The determinism contract

The simulation is **iteration-order-sensitive**. Neighbor loops sum floats (separation, contact impulses, obstacle normals), and floating-point addition isn't associative, so the *order* units are visited perturbs the result. Chunk layout — which is affected by structural changes (spawns, deaths, tag adds) — determines that order. From this single fact, everything follows:

- **Every peer performs identical structural changes in the same tick.** Spawns and deaths are driven by deterministic sim state, never by local events.
- **No wall-clock time in the sim.** `SystemAPI.Time.DeltaTime` is a fixed `1/30 s`, forced by a rate manager (`LockstepBootstrap`). No `Time.time`, no `UnityEngine.Random`, no frame-rate coupling.
- **Identity is stable, not `Entity`.** Raw `Entity` values differ per world/run, so units are referenced by `StableId` (assigned in deterministic spawn order). Orders and replays name units by `StableId`; a registry rebuilt each tick maps it back to the live entity.
- **Derived state never enters the checksum.** The checksum exists to detect desync of *source* state cheaply and totally. Anything recomputable from source state (perception, animation, formation slots) is excluded — if it diverged, its inputs already diverged and were already caught.
- **When anyone desyncs, everyone restores.** Because the world is order-sensitive, you can't patch just the one bad unit on the one bad peer. A network sim world only ever *comes into existence* by restoring a snapshot, and recovery re-restores everyone from the host's baseline. (See [Snapshots](#snapshots-one-mechanism-four-jobs).)

The single guardrail that catches violations: `SimChecksumSystem` folds each unit's `(pos, hp, vel, team, StableId, navCtx)` into an **order-independent** hash every tick, and `ChecksumHistorySystem` keeps ~34 s of history so the *first* divergent tick is identifiable, not just the fact of divergence.

---

## Architecture at a glance

```
┌─────────────────────────────────────────────────────────────────────┐
│  OUTSIDE THE SIM (managed, per-client, never hashed)                  │
│                                                                       │
│  PlayerCommander / AICommander ─┐         UnitView · ProjectileView   │
│  (intent: mouse, AI timers)     │         TeamColorTarget · Animator  │
│                                 │              ▲  (reads AnimState)    │
│  LockstepNet (NGO relay) ───────┤              │                      │
│                                 ▼              │                      │
│                          Commander.Outbox ─────┼──────────────┐       │
└─────────────────────────────────┼─────────────┼──────────────┼───────┘
                                  │ commands     │ read-only    │
┌─────────────────────────────────▼─────────────┴──────────────▼───────┐
│  THE SIM (Burst ECS, deterministic, SimulationSystemGroup @ 30 Hz)    │
│                                                                       │
│  SimClock → StableIds → Commands → Sensing/Fields → Decision →        │
│  Locomotion → Combat → Bookkeeping → Checksum                         │
│                                                                       │
│  State lives only in components. Snapshots (de)serialize it whole.    │
└───────────────────────────────────────────────────────────────────────┘
```

Two rules define the boundary:

- **Intent flows in** through the `Commander` stream (stamped to execute `InputDelayTicks` ahead, relayed over the network). Everything *reactive* — targeting, formation, steering, auto-behavior — is a deterministic sim system, not intent.
- **State flows out** read-only to views. Views translate `AnimState`, position, and team color into `GameObject`s; the sim never calls back.

---

## The tick pipeline

Every system lives in `SimulationSystemGroup`, driven at a fixed 30 Hz. Ordering is expressed with `[UpdateBefore/After]` and the `OrderFirst/OrderLast` bands. In dependency order:

**Frame open**
1. **`SimClockSystem`** *(OrderFirst)* — advances the authoritative tick counter.
2. **`StableIdRegistrySystem`** *(OrderFirst)* — rebuilds the `StableId → Entity` map for this tick.

**Input**
3. **`CommandIngestSystem`** — pulls this tick's commands (local + networked) into the `SimCommand` buffer.
4. **`CommandApplySystem`** — applies orders: move/attack/stop, ability *commit* (mana + resource checks, arms `PendingCast`), and building placement (the one structural op driven by a command).

**Sensing & world fields**
5. **`AttackTimerSystem`** *(before hash)* — runs the charge→fire→cooldown cycle; melee sets a one-tick `Pulse`, ranged spawns a projectile. Ordered before the hash so this tick's `Pulse` is visible this tick.
6. **`SpatialHashSystem`** — buckets units and publishes the `UnitInfo` snapshot every neighbor query reads.
7. **`ObstacleGridSystem`** — rebuilds cell passability from `Immobile` footprints, terrain slope, and water.
8. **`FlowFieldSystem`** — maintains the hierarchical navigation fields (lazily, per requested path).
9. **`ProjectileSystem`** — advances each projectile's arc and builds the `ProjectileHash`.
10. **`AbilityFieldSystem`** — each unit tests which active ability fields it's inside and stamps their modifiers into its `ActiveModifier` buffer.
11. **`ModifierTickSystem`** — applies value effects (damage/heal), ticks modifier timers, drops expired ones.
12. **`StatResolveSystem`** — recomputes every unit's *live* stats (`Speed`, `Attack`, `Defense`, `UnitTuning`, `BehaviorOverride`) from `BaseStats` + surviving modifiers. This is why the rest of the sim never had to change when modifiers were added.
13. **`InformationGatherSystem`** — the one perception sweep: fills each unit's `Perception`, its `ContactList` neighbor snapshot, and its `IncomingProjectile` buffer. One scan, one truth — physics and combat can't disagree about who's touching whom.

**Decision**
14. **`FormationSystem`** — rebuilds formation slot assignments every tick (handles attrition, new orders, reorientation).
15. **`AbilityCastSystem`** — fires armed casts whose `FireTick` is now (spawns ability fields / spawn-units).
16. **`BehaviorSystem`** — the positional decision layer. Produces a *desired position* per unit (slot + offset + scatter) and sets `CombatStatus`. Never a velocity — steering owns that.

**Locomotion & combat**
17. **`SlopeSystem`** — samples terrain height/slope into `GroundSpeedMultiplier` and each unit's `Height`.
18. **`SteeringSystem`** — turns the desired position into motion: flow-field following for long moves, separation, obstacle sliding, integration, and facing.
19. **`ContactCombatSystem`** — receiver-side melee/contact/projectile resolution. `impact = enemyMass × closingSpeed`; downhill units close faster and hit harder. Each unit writes only its own components (parallel, Burst-safe).
20. **`ProjectileCleanupSystem`** — destroys projectiles marked stale (hit) or expired.
21. **`AnimationStateSystem`** — derives each unit's `AnimState` purely from sim data.

**Bookkeeping**
22. **`DeathSystem`** — lingers dead units for their death-anim duration, then destroys via an end-of-frame ECB.
23. **`ManaRegenSystem`** — regenerates mana (add-only; consumption is at cast commit).
24. **`SimDebugSystem`** — services debug requests.

**Frame close**
25. **`SimChecksumSystem`** *(OrderLast)* — folds source state into the order-independent tick checksum.
26. **`ChecksumHistorySystem`** *(OrderLast)* — records it into the ring buffer for divergence detection.

---

## Subsystems in depth

### Perception — one sweep, shared by everyone
`InformationGatherSystem` is the sim's single sensing pass. From the spatial hash it computes, per unit: nearest/most-dangerous/most-exposed enemy, nearest friendly, enemy & friendly centers of mass, a reusable **ContactList** (the exact neighbor set steering and contact combat both consume), and incoming projectiles. Line-of-sight currently applies a *soft priority penalty* (`NoLosMultiplier`) rather than a hard visibility gate.

### Behavior — positions, not velocities
`BehaviorSystem` answers only "where do I want to be this tick?" — `slotWorld = anchor + offset(shape, index) + scatter(looseness)`. Formation slots come fresh each tick from `FormationSystem`; `hasSlot` gates every formation-dependent decision. Ranges are measured to the target, with height adding reach for ranged units. Everything about *how we actually get there* belongs to steering.

### Formations — a living maintainer
`FormationSystem` rebuilds slot assignment every tick: depth-sort members along the facing axis, slice into shape-dependent rows (`Grid`/`Wall` uniform, `Wedge` = 1,2,3,…), lateral-sort within each row, `StableId` as the final tiebreak. The shared anchor advances at the slowest member's pace, gated by a straggler tolerance scaled by `Looseness`. `FormationGeometry` is the single shared encoding both assignment and placement decode against, so a unit's world slot is always consistent.

### Steering — locomotion & collision
Heads toward the destination (sampling the flow field for long commanded moves, straight for short local goals), adds neighbor separation, responds to obstacles with a composite surface normal and smooth falloff (cancels the into-wall velocity component to slide along edges), then integrates. External forces move position but don't corrupt `vel.desiredValue`; `vel.Value` is back-calculated from the actual step so combat's closing-speed math stays honest. Facing: attackers face their target, movers face their motion, otherwise hold.

### Combat — emergent, receiver-side
`AttackTimerSystem` runs a clean charge→fire→recover cycle that resets on break-off, so no unit ever starts mid-swing. `ContactCombatSystem` resolves both physical ramming and declared strikes receiver-side over the shared ContactList, so physics and damage never disagree. Knockback pushes away from the rammer, scaled by `impact / ownMass`.

### Stats & modifiers — recomputed, not mutated
Ability effects are `ActiveModifier` entries. `ModifierTickSystem` applies value changes and expiry; `StatResolveSystem` recomputes live stats from `BaseStats` + surviving modifiers each frame. Buffs revert cleanly because nothing is destructively edited — the resolved value simply stops including an expired modifier.

---

## The data model

Everything is a component; there is no hidden state. Key groups:

| Group | Components |
|---|---|
| **Kinematics** | `Velocity`, `Speed`, `Mass`, `KnockbackVelocity`, `UnitRadius`, `GroundSpeedMultiplier`, `MoveTarget`, `DesiredDestination` |
| **Identity / team** | `UnitTag`, `Team`, `StableId`, `UnitDefId`, `Selected`, `HeroTag` |
| **Combat** | `Health`, `Attack`, `Defense`, `UnitTuning`, `CombatStatus`, `CombatTarget`, `AttackOrder`, `Ranged`, `Dead`, `DeathTimer` |
| **Formation** | `FormationMember`, `FormationSlot` |
| **Perception** | `UnitInfo`, `Perception`, `SpatialHash`, `IncomingProjectile` |
| **Abilities** | `BaseStats`, `ActiveModifier`, `Mana`, `PendingCast`, `AbilitySlots`, `AbilityCooldowns`, `AbilityField`, `FieldModifier` |
| **Projectiles** | `Projectile`, `ProjectileTag`, `ProjectileHash`, `ProjectileView` |
| **Structures** | `BuildingTag`, `Immobile`, `AbilityImmune`, `Wall`, `NavContext`, `Obstacle` |
| **Economy** | `TeamResources`, `ResourcePoolTag` |
| **View / anim** | `UnitAnim`, `AnimState` |

Units are authored as `UnitDefinition` `ScriptableObject`s and instantiated directly by `UnitManager` (no baking, no SubScene): behaviors pack into a bitmask, and each entity remembers its definition index as `UnitDefId` for view lookup.

---

## Abilities & modifiers

A fully data-driven effects system. An `AbilityDefinition` compiles to a blittable `AbilitySpec` plus a set of `FieldModifier`s. The lifecycle:

1. **Commit** (`CommandApplySystem`): checks cooldown, mana, and (optionally) team resources — all-or-nothing — then arms `PendingCast` with a `FireTick`.
2. **Fire** (`AbilityCastSystem`): at `FireTick`, spawns an `AbilityField` (shaped `ShapeType`, anchored by `AnchorType` — including hero-follow) and/or a spawn-unit (this is the "peasant builds a farm for 100 wood" path — a build ability with a building spawn-unit and a resource cost).
3. **Apply** (`AbilityFieldSystem`): units inside a field stamp its modifiers into `ActiveModifier`. `PersistentArea` refreshes while inside and expires on leave; `CastOnce` stamps exactly once then self-destructs.
4. **Resolve** (`ModifierTick` + `StatResolve`): value effects apply and timers tick; live stats recompute from what survives.

Modifiers are expressive: `ModTarget` × `ModMode` (add/mul/override) × `CapMode`/`CapRef` (clamping), with `AffectFilter` for who's eligible. Flag modifiers even drive `BehaviorOverride`, which is how auras influence decisions (this replaced the old bespoke hero-aura system).

---

## Navigation

A tiled **hierarchical flow-field** engine, not per-unit A*:

- **`ObstacleGridSystem`** rebuilds a passability grid each tick from `Immobile` footprints, slope, and water level.
- **`FlowFieldSystem`** maintains a two-level structure: a **coarse portal graph** over big tiles (with connected-component analysis, so long walls and cliffs are routed *around* rather than into), and **fine per-block direction fields** solved lazily only for blocks a live path actually needs, cached and invalidated by tile version. Fine fields are solved with the **Eikonal equation** (fast-marching, Godunov upwind) for smooth true-distance gradients rather than blocky octile steps. Results are published in `NavFields`; steering samples the field for long moves and falls back to straight-line + obstacle-sliding for short ones.

The grid also carries `NavContext` (Ground / Roof / Transition) and per-cell surface height, which is what lets units stand and fight on walls.

---

## The commander layer

`Commander` is the abstract intent API — a small shared verb set (`IssueMove`, `IssueAttack`, `IssueStop`, `IssueAbility`, `PlaceBuilding`) that stamps each command to execute `InputDelayTicks` ahead and pushes it onto the outbox (which the network relay drains). Two implementations:

- **`PlayerCommander`** — classic RTS input: left-drag box select, right-click to move/attack, right-drag to set formation width, `Q/W/E/R` to arm a caster's ability slots. (Holding both mouse buttons is the camera orbit chord, suppressing select/commit.)
- **`AICommander`** — timer-driven strategic intent, issuing the same verbs no differently than a human.

Because commander output is the *only* thing that enters the sim from outside, and it enters as timestamped commands, the AI and the player are interchangeable and fully replayable.

---

## Networking (lockstep)

`LockstepNet` implements host-relayed deterministic lockstep on top of **Netcode for GameObjects** — but NGO is used *only* as a connection manager and reliable message channel (`CustomMessaging`). None of its replication, `NetworkVariable`, or spawn machinery touches the sim. The sim stays our deterministic ECS world; only **commands** cross the wire.

The turn protocol, per execution tick `T`:

1. Every peer submits its commands for tick `T` to the host (an empty submission is still sent as a "ready" signal). Peers run `InputDelayTicks` (2) ahead of execution — that gap is the latency budget.
2. The host collects all peers' submissions and relays the complete command set for `T` back out.
3. Every peer executes tick `T` with the identical command set. Identical inputs + deterministic sim ⇒ identical state.

`LockstepBootstrap` installs the fixed-rate manager that makes this sound: it drives `SimulationSystemGroup` at a wall-clock 30 Hz via an accumulator, so `DeltaTime` is constant everywhere. Without it a system group free-runs one step per frame — the component logs loudly if that happens.

---

## Snapshots: one mechanism, four jobs

`SimSnapshot` serializes the **complete** simulation state to a byte blob and rebuilds a world from one. Because the sim is order-sensitive, this single mechanism *is*:

- **Game start / late join** — a joining peer receives the host's baseline and restores it; that's how its sim world is born.
- **Save / load** — the same blob, to disk.
- **Desync recovery** — when a checksum mismatch is detected, *every* peer (host included) re-restores from the host's baseline, because you can't selectively patch an order-sensitive world.

The invariant that makes it trustworthy: a capture→restore round-trip must reproduce the pre-restore state hash bit-for-bit, proving the serializer covers every hashed bit. That's the `F6` self-test below.

---

## The view layer

Entirely downstream and per-client:

- **`UnitManager`** — owns units. Backs entities from the roster on start; each `LateUpdate` slaves a pooled `viewPrefab` (looked up by `UnitDefId`) to each entity and tints team color via `TeamColorTarget`.
- **`UnitView`** — lives on the prefab, translates the entity's `AnimState` into a single Animator `State` int (`0 Idle · 1 Walk · 2 Block · 3 Attack · 4 Die`). The sim never touches an Animator, which is the whole reason it stays Burst-compiled.
- **`ProjectileViewManager`** — pools a visual per projectile, resolved per firing unit's definition.
- **`TerrainFieldBootstrap`** — samples the Unity `Terrain` into the ECS `TerrainHeightField` once so Burst systems read elevation without managed calls.

Swapping art, rigs, or the entire animation backend touches only this layer.

---

## Running & testing

**Play (single-player):** open the scene, ensure a `LockstepBootstrap`, `UnitManager`, and `TerrainFieldBootstrap` are present, and press Play. With no `LockstepNet` in the scene the sim free-runs at a fixed 30 ticks/s at any frame rate — this is the determinism test bed.

**Play (networked):** add `LockstepNet`. The sim stays frozen until *Start Game*, then each tick additionally requires that tick's networked turn. Use Unity's Multiplayer Play Mode (MPPM) to run host + client roles in one editor.

**Determinism & desync harness** (`SimResultDumper` + `SnapshotDebug`, keyboard-driven, most work offline):

| Key | Action |
|---|---|
| `F6` | **Round-trip self-test** — capture the live world, restore in place, compare hashes. Equal ⇒ the serializer is complete. No network needed. |
| `F8` | **Inject desync** — corrupt one unit's health by 1 locally. On a client this forces a real divergence: the next checksum report disagrees, the host logs the divergent tick, and the resync pipeline heals everyone. |
| `F10` / `F11` | Save / restore the sim to/from the save file. (In a session, use the host's Load Save so every peer rebuilds.) |
| *(dump hotkey)* | `SimResultDumper` writes a full end-state report (sorted by `StableId`) to a file. Diff two dumps to confirm a run reproduced exactly. |

The classic verification loops: run twice + diff (proves deterministic math); record once + play back + diff (proves replay); corrupt on a client (proves detection + recovery).

---

## File map

**Lockstep & determinism**
`Lockstep.cs` (clock, checksum, history) · `LockstepNet.cs` (turn relay + snapshot sync) · `LockstepBootstrap.cs` (fixed-rate manager) · `StableId.cs` · `SimSnapshot.cs` · `SimResultDumper.cs` · `SnapshotDebug.cs`

**Sim pipeline**
`CommandSystem.cs` (ingest + apply) · `SpatialHashSystem.cs` · `InformationGatherSystem.cs` · `FormationSystem.cs` / `FormationGeometry.cs` · `BehaviorSystem.cs` · `SlopeSystem.cs` · `SteeringSystem.cs` · `AttackTimerSystem.cs` · `ContactCombatSystem.cs` · `Projectiles.cs` · `AnimationStateSystem.cs` · `DeathSystem.cs` · `ManaRegenSystem.cs`

**Abilities & stats**
`AbilityDefinition.cs` · `AbilityComponents.cs` · `AbilityManager.cs` · `AbilityCastSystem.cs` · `AbilityFieldSystem.cs` · `ModifierTickSystem.cs` · `StatResolveSystem.cs`

**Navigation**
`Navigation.cs` (obstacle grid + hierarchical Eikonal flow fields) · `NavCell.cs`

**Data / definitions**
`Components.cs` · `SliceComponents.cs` · `UnitDataComponents.cs` · `UnitDefinition.cs` · `BuildingComponents.cs` · `BuildingDefinition.cs` · `WallDefinition.cs` · `ProjectileDefinition.cs` · `ResourceComponents.cs` · `CombatMath.cs`

**Intent (commanders)**
`Commander.cs` · `PlayerCommander.cs` · `AICommander.cs`

**View & runtime**
`UnitManager.cs` · `UnitView.cs` · `ProjectileViewManager.cs` · `TeamColorTarget.cs` · `TerrainFieldBootstrap.cs`

**Debug**
`DebugComponents.cs` · `DebugOverlay.cs` · `Editor/*`

---

## Extending the sim

A few rules keep contributions from breaking determinism:

- **New reactive behavior is a sim system, not a command.** If it's a pure function of sim state (targeting, an aura, auto-cast), add an `ISystem` and slot it by dependency — no networking needed; it'll be identical on every peer for free. Only genuine outside *intent* goes through `Commander`.
- **Read neighbors from the ContactList / spatial hash, not a fresh scan.** One scan, one truth. Extend `InformationGatherSystem`'s single sweep instead of adding a second one.
- **Only mutate a unit's own components in parallel jobs.** Cross-entity writes are the receiver-side pattern (see `ContactCombatSystem`) or go through an ECB.
- **Structural changes (spawn/die/add tag) must be driven by sim state**, applied identically on all peers, and confined to deterministic points (e.g. an ECB at end of frame).
- **Never put wall-clock time, `UnityEngine.Random`, or managed calls in a Burst job.** Use `SystemAPI.Time.DeltaTime` (the fixed sim step) and blittable data.
- **New state that affects gameplay must be added to `SimSnapshot` and, if it's *source* state, folded into the checksum.** Derived state stays out of both.
- **Author content as `ScriptableObject` definitions**; let `UnitManager` back it. Avoid per-unit code paths.

When in doubt: if two runs from the same inputs could disagree, it's a determinism bug — the `F6`/dump-and-diff harness will find it.
