# RTS Prototype — Project Rules

We are building a singleplayer 3D RTS prototype in Unity 6 using C# and URP.
Inspired by classic base-building RTS games. Must be fully original — no copied names, assets, factions, UI, or exact mechanics from any copyrighted title.

---

## Core coding rules
- Keep code modular. One responsibility per script.
- Do not create huge scripts.
- Do not rename existing public fields unless necessary.
- Do not delete existing files without explicit confirmation.
- Every new script must include clear setup instructions in the Unity Inspector (XML summary + tooltips).
- Prefer simple MonoBehaviour architecture first.
- Do not use DOTS/ECS unless explicitly requested.
- Do not add multiplayer.
- Do not add external packages unless explicitly requested.
- After every change, explain:
  1. What files changed
  2. What was added or modified
  3. How to test in Unity
  4. What can break

---

## Current player-side systems (all working)
| System | Status |
|---|---|
| RTS camera (WASD, edge scroll, zoom, rotation) | ✅ Active |
| Unit selection (single click, drag box, multi-select) | ✅ Active |
| Group movement (right-click ground, formation) | ✅ Active |
| Basic combat (right-click enemy, chase, attack, health) | ✅ Active |
| Worker resource gathering (→ ResourceNode → CommandCenter) | ✅ Active |
| PlayerResourceManager (tracks resources, CanAfford, SpendResources) | ✅ Active |
| Building placement (press B, ghost preview, overlap check) | ✅ Active |
| Barracks production (select Barracks → S → spawn Soldier) | ✅ Active |
| Visual polish (EnvironmentDresser, UnitColorMarker) | ✅ Active |

---

## PAUSED systems (scripts exist, not in scene)
| System | Script location | Why paused |
|---|---|---|
| Enemy wave spawning | `Assets/_Game/Scripts/AI/EnemyWaveSpawner.cs` | Focusing on player base first |
| Enemy AI controller | `Assets/_Game/Scripts/AI/EnemyAIController.cs` | Focusing on player base first |

Do not add these to the scene until explicitly requested.

---

## Planned tech tree (player-side, implement phase by phase)

### Phase 7 — Power system
- `PowerPlant` building: adds power supply (e.g. +20 per building)
- `PowerManager` singleton: tracks total supply vs. total demand
- Buildings have a `PowerConsumer` component with a power demand value
- If demand > supply: production buildings show a warning, production pauses
- No visual UI yet — Console logs only

### Phase 8 — Vehicle Factory
- New building type: `VehicleFactory`
- Produces a basic `Tank` unit (press V when selected)
- Tank: NavMeshAgent, UnitMovement, UnitCombat, Health (Player), higher HP/damage
- Requires power (uses PowerConsumer)

### Phase 9 — HUD / Resource UI
- Top-bar HUD: current resources, current power supply/demand
- Selected unit/building info panel (name, health bar)
- Hotkey hints for production buildings

### Phase 10 — Defenses
- `Turret` building: auto-attacks nearest enemy in range
- `Wall` segment: placeable obstacle, blocks movement

### Phase 11 — Advanced units & structures (later)
- Airfield → aircraft units
- Nuclear Power Plant → large power supply, high cost
- Missile system → area attack
- Lose condition: all player buildings destroyed

### Phase 12 — Enemy AI return
- Re-enable EnemyWaveSpawner and EnemyAIController
- Add enemy base that spawns waves
- Add enemy economy and building destruction win/lose conditions

---

## Scene layout (current)
- Ground plane (Ground layer)
- CameraRig + Main Camera
- GameManager (UnitSelector, BuildingPlacementManager, PlayerResourceManager)
- CommandCenter (blue cube + Health Player)
- ResourceNode (gold cube, Resource layer)
- Player unit(s): capsule, Unit layer
- Worker: capsule with WorkerGatherer, Unit layer
- Environment: trees, rocks via EnvironmentDresser
- NO enemy units or wave spawners in the scene

---

## Layer map
| Layer | Used for |
|---|---|
| Ground | Terrain / plane |
| Unit | All mobile units (player and enemy) |
| Resource | ResourceNode objects |
| Building | Placed buildings (Barracks, CommandCenter, future buildings) |
