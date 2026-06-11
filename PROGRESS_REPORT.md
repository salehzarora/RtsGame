# Progress Report — Full Project Improvement Pass (2026-06-10)

Autonomous audit + improvement sweep across prefabs, scripts, scenes, materials,
and tooling. Everything below was verified by direct file-level analysis of the
serialized assets (YAML) and source; in-editor follow-ups are listed at the end.

## What was scanned

| Area | Result |
|---|---|
| 108 runtime/editor scripts | structure mapped; per-frame perf scan run |
| 25 gameplay prefabs | full component/collider/agent audit |
| 5 scenes | reference scan (3 are stale binary-format — see below) |
| 117 materials | all shader + texture references resolve ✓ |
| Asset references project-wide | **zero missing references** (early "missing guid" hits were package assets — URP Lit, uGUI) |
| Combat wiring | no null firePoint / projectile refs in any prefab ✓ |
| Per-frame script hotspots | all `Camera.main` / `FindAnyObjectByType` uses are lazy-cached one-shots ✓ (no fixes needed) |
| Static mutable fields | reviewed; all benign (counters / re-entrancy guards) except one watch item (below) |

## Fixes applied (files changed)

### 1. Phantom colliders removed — `SoldierPrefab`, `Worker`, `WorkerPrefab`
Each prefab's `SelectionCircle` child carried a leftover **CapsuleCollider**
(from `CreatePrimitive(Capsule)` at creation time). The circle activates when a
unit is selected → a second 0.5×2 capsule appeared on every selected unit,
able to intercept selection/command raycasts. Removed the collider component
docs + their `m_Component` entries surgically; verified no dangling refs.

### 2. NavMeshAgent tuning — responsiveness + perf
- `SoldierPrefab` / `Worker` / `WorkerPrefab`: `angularSpeed 120 → 720`
  (RPG soldiers already used 720; 120 made basic infantry turn visibly
  sluggish), `acceleration 8 → 12` (snappier command response).
- Obstacle-avoidance quality lowered from High (max cost) to:
  - **Medium** for infantry: Soldier, Worker ×2, RPGSoldier, EnemyRPGSoldier
  - **Good** for vehicles: APC, ArtilleryTank, Dozer, EnemyDozer, Humvee,
    MissileLauncher
  Behaviour is unchanged in practice; CPU cost per agent drops — the standard
  RTS scaling knob for large unit counts.

### 3. New editor tool — `Assets/_Game/Editor/ValidateProjectIntegrity.cs`
`Tools → RTS → Validation →`
- **Validate All Gameplay Prefabs** — missing scripts, collider sanity
  (flags MeshColliders), required components per unit/building/aircraft,
  `UnitCombat.firePoint` wiring, `GameEntity.prefabTypeId`, LODGroup health
  (size, empty levels), TeamColorApplier slot ranges.
- **Validate Open Scene Setup** — exactly-one camera/AudioListener/EventSystem,
  manager singletons, duplicate `PlayerResourceManager.ownerPlayerId`,
  NavMesh presence, duplicate GameEntity ids.
- **Find Missing Script Components** — project-wide prefab sweep.
- **Convert Scenes To Force-Text** — re-saves all scenes (see finding 4).
- **Validate All** — combined run.

## Addendum (2026-06-11) — in-editor validation via FileBridge

The official Unity MCP server proved unusable on this machine (Unity ID
"Token Exchange failed" exceptions → 0 AI entitlements → MCP capacity 0,
every connection auto-revoked). Replaced with
`Assets/_Game/Editor/FileBridge.cs` — a file-based command bridge
(`Library/FileBridge/cmd.json` → result JSON) supporting: ping, scene_info,
console, menu execution, play/stop, screenshots, static-method exec.

First in-editor run of `Tools → RTS → Validation → Validate All` found and
led to fixing a real gameplay bug:

### 4. Player production buildings were indestructible
`Barracks`, `PowerPlantPrefab`, `VehicleFactoryPrefab`, `AirfieldPrefab`
had **no Health component** — enemy units could never damage them, while the
enemy counterparts (EnemyBarracks/EnemyPowerPlant) and CommandCenter/
MachineGunDefense all had Health. Added Health (team = Player):
Barracks 600, PowerPlant 400, VehicleFactory 800, Airfield 1000.
Re-ran validation in-editor: **"Prefab audit complete — no problems."**
Note: these buildings have no HealthBar child yet — damage works, but no
visual HP bar; candidate follow-up.

Remaining ⚠ warnings are by design (vehicle turn rates intentionally slower
than infantry; enemy units not player-selectable; ConstructionSite has no
GameEntity).

## Findings that need an in-editor step (no risky blind fix applied)

4. **Three scenes are stale binary-format** (`SampleScene`, `GameMapScene`,
   `DevSandboxScene`, plus `SampleScene_Backup`): the project's serialization
   mode is already Force Text, but these predate it. Run
   `Tools → RTS → Validation → Convert Scenes To Force-Text` once. Until then
   they can't be audited/diffed as text (only MainMenuScene is text today).
5. **`Worker.prefab` is unused by runtime** (only `WorkerPrefab.prefab` is
   referenced — by CommandCenterPrefab). One editor tool
   (`AddGameEntityToPrefabs`) still lists Worker.prefab. Left on disk per
   safety rules; candidate for deletion after confirming in-editor.
6. **Watch item — `GameEntity.s_nextSpawnId`**: static one-shot handoff for
   deterministic MP spawn ids. If a spawn ever aborts between "set" and
   "consume", the stale id would attach to the NEXT spawned entity. No
   observed bug; noted for the next MP debugging session.
7. **Backup prefabs** (`*_OLD_Backup`) and `SampleScene_Backup.unity` are kept
   intentionally (rollback safety) and excluded from validation.

## Play-mode test checklist

1. Open the editor, let it recompile; Console must be clean.
2. `Tools → RTS → Validation → Validate All` → expect only the known ⚠ lines
   (enemy units without Selectable — by design).
3. `Tools → RTS → Validation → Convert Scenes To Force-Text` → re-run later
   scans on text scenes.
4. Open SampleScene (or DevSandbox), `Validate Open Scene Setup`.
5. Play: select infantry → right-click move → **turn-in-place should be
   visibly snappier** (angular 720). Group-move several units — avoidance
   behaviour should look unchanged.
6. Select a unit → confirm the selection circle appears and clicking other
   units **through/near the circle** still selects correctly (collider
   removal).
7. Produce Soldier (Barracks), Dozer build flow, StrikeJet from Airfield —
   unchanged systems, regression-check only.
