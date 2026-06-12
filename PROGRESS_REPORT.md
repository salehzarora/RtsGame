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

## Addendum (2026-06-11, second pass) — full in-editor improvement pass via FileBridge

Driven entirely through the FileBridge (play mode, screenshots, console,
menu/exec). New ops live in `Assets/_Game/Editor/BridgeOps.cs`.

### Verified live in Play Mode (DevSandboxScene)
- Combat works both ways: spawned Soldier auto-engaged and **killed** an
  EnemyRPGSoldier, then took return fire from EnemyVehicleDummy (51/100).
- All 7 NavMeshAgents on NavMesh; movement orders work.
- Economy loop works (Resources 20000 → 20015 from worker deposits).
- HUD: Selected/Command/Economy panels correct, **minimap renders live**.
- Team colors correct (blue accents on player units); building Health from
  pass 1 live in-game (Airfield 1000/1000, PowerPlant 400/400).
- Soldiers stand upright with correct visuals — an earlier "lying down"
  suspicion was top-down foreshortening; per-instance inspection confirmed
  rot/scale/active-states all correct on every scene soldier.

### Changes this pass
1. **Building health bars** — `HealthBar` child added to Barracks (h 3.2),
   PowerPlant (3.6), VehicleFactory (3.0), Airfield (4.0); wide bar
   (2.4×0.22), `hideWhenFull=true`.
2. **All 5 scenes converted to TEXT serialization.** Root cause of the
   binary fallback: `NavMeshSurface.BuildNavMesh()` leaves NavMeshData
   embedded in the scene (binary-only object). Fixed by externalizing the
   data to `Assets/Scenes/NavMeshData/*.asset` (BridgeOps.ExternalizeNavMeshData),
   then re-saving.
3. **Validator false positive fixed** — MinimapCamera allocates its
   RenderTexture at runtime, so edit-mode scene validation no longer counts
   it as a second screen camera.
4. **Removed `com.unity.ai.assistant`** (backup: Packages/manifest.json.bak)
   — its Unity-ID token exchange failed in a loop, flooding the console;
   its MCP server was unusable (0 entitlements). FileBridge replaces it.
5. **New bridge ops** (BridgeOps.cs): OpenDevSandbox/OpenSampleScene/
   OpenMainMenu, ConvertScenesToTextSilent, ExternalizeNavMeshData,
   AddBuildingHealthBars, FixSceneSoldierOrientations, InspectSceneSoldiers,
   FocusCameraOnSoldiers, ScreenshotFull, Smoke{SpawnCombat,OrderMove,
   Report,Cleanup}.

### Notes / leftovers (deliberate)
- DevSandboxScene contains debug debris: one `SoldierPrefab_OLD_Backup`
  instance and three raw `Soldier_LOD0/1/2` FBX drops (no gameplay
  components, red default accents). Harmless; left in place — delete from
  the Hierarchy whenever convenient.
- `GameEntity.s_nextSpawnId` watch item CLOSED: all three spawn sites
  already use the set → Instantiate → clear pattern.
- Vehicle turn rates (150–200°/s) left as-is — heavier turning is intended
  for vehicles; only infantry was tuned (720°/s).

## Addendum (2026-06-11, third pass) — visible combat VFX & game-feel

New procedural VFX system, verified live in Play Mode with screenshots.

### New system: `Assets/_Game/Scripts/VFX/CombatVFX.cs`
Static factory — builds all effects from code (built-in soft-circle texture,
Sprites/Default material): muzzle flashes (+point light), impact sparks,
3-size explosions (fireball + smoke column + light), building damage smoke
(looping child, heavier below 25% HP, cleared on repair), projectile trails.
Zero prefab/scene wiring; purely local visuals (MP-safe); auto-destroyed;
small particle budgets (7–48 per event).

### Hooks added (one-liners)
- `Health.TakeDamage` → hit sparks + building smoke thresholds
- `Health.Heal` → smoke cleared on repair
- `Health.Die` → death explosion sized by category (building L, vehicle/
  aircraft M, infantry S)
- `UnitCombat.ShowTracer` / `BuildingTurretCombat.ShowTracer` → muzzle flash
- `MissileProjectile/RocketProjectile/StrikeMissile.Launch` → trails;
  their impacts → particle explosions (alongside the existing flash spheres)
- `AircraftWeapon.SpawnMissile` → wing-hardpoint launch flash

### Verified by screenshot in live combat
- Rocket impact explosion with glowing point light on terrain ✓
- Enemy RPG launch flash ✓  • impact sparks on hit ✓
- Damaged PowerPlant: health bar showing damage + soft smoke ✓
- Vehicle death explosion + dissipating smoke ✓

### Bugs found & fixed during the pass
- CombatVFX ternary int→short compile errors (initially masked by console
  timing — Editor.log is the reliable source for compile errors).
- URP Particles/Unlit configured via code rendered opaque untextured quads →
  switched factory to Sprites/Default.
- **DevSandboxScene had a Missing-Prefab instance** (`SoldierPrefab_OLD_Backup`
  with a stale GUID — the backup prefab is delete+recopied by the swap tool,
  changing its GUID) erroring on every scene load → removed via new
  `BridgeOps.CleanMissingPrefabInstances`, scene saved.

### New bridge ops
FocusCameraOnFight / FocusCameraOnPowerPlant, SmokeDamagePowerPlant,
SmokeKillDummyVehicle, CleanMissingPrefabInstances.

### Tuning
All feel values live at the top of CombatVFX.cs (lifetimes, colors) and in
each method's MinMaxCurves. Explosion fireball lifetime/size and smoke
brightness were bumped once already for RTS readability.

## Addendum (2026-06-11, fourth pass) — whole-game polish pass

### Battlefield aftermath (new, in CombatVFX)
- **Scorch marks**: dark soft ground decal at vehicle/building death sites,
  holds 12 s then fades over 14 s (`ScorchMark`, FadeAndDie helper).
- **Lingering smoke**: wreck smoke keeps emitting 8 s (vehicles) / 12 s
  (buildings) after the death explosion (`LingeringSmoke`).
- DeathFeedback now: building → big explosion + scorch + 12 s smoke;
  vehicle/aircraft → medium + scorch + 8 s smoke; infantry unchanged.

### Combat readability
- **Tracers clamped + tapered** (UnitCombat + BuildingTurretCombat):
  serialized prefab widths produced fat white beams at RTS zoom; now
  max 0.08 wide at the muzzle tapering to 35% at the target — thin,
  directional, readable.

### Aircraft
- **StrikeJet engine trail** (`BridgeOps.AddJetEngineTrail`): tail
  TrailRenderer (white-blue fade, 0.55 s) — emits only while moving, so
  parked jets stay clean. Material asset: M_EngineTrail.mat.

### Selection feedback
- **All 9 player unit prefabs**: SelectionCircle's squashed-cylinder visual
  replaced with a clean flat ring (annulus mesh asset
  `Assets/_Game/Art/VFX/SelectionRing.mesh` + bright green
  `M_SelectionRing.mat`); stray colliders stripped; same GameObject so
  SelectableUnit's toggle logic is untouched.
  ⚠ VERIFY in play: click a unit — if the ring hides under the unit's feet,
  raise the SelectionCircle child's X/Z localScale on that prefab.

### Observed during verification
- Death animation confirmed in the field (prone soldier corpse on the
  battlefield screenshot, SampleScene).
- Console clean at pass end.

## Addendum (2026-06-11, fifth pass) — battlefield structure overhaul (DevSandboxScene)

New tool: `Tools → RTS → Map → Dress DevSandbox Battlefield`
(`Assets/_Game/Editor/DressDevSandbox.cs`, idempotent — wipes/rebuilds the
"BattlefieldDressing" root). Verified in Play Mode with screenshots; NavMesh
rebaked so blocking props carve pathing; console clean.

### What the scene now has
- **Dirt road network**: N-S + E-W crossroads (intersection at ~(4,2)) plus
  a spur to the airfield; flat, collider-free, walkable.
- **Combat zone at the crossroads**: 3 concrete barriers + 2 sandbag walls
  (single box collider each → real cover that blocks pathing), 3 destructible
  red fuel barrels + 2 ammo crates (Health, team Enemy → player-attackable;
  `ExplodeOnDeath` → explosion + scorch + smoke via CombatVFX), pre-placed
  scorch patches.
- **PowerPlant base area**: concrete pad, fences, barrels, generator,
  antenna mast (warning-yellow dish), supply crates, floodlight pole with a
  real spot light.
- **Airfield surroundings**: two banded fuel tanks, crates, warning-stripe
  row (clear of runway/taxi paths), cones.
- **Scatter**: supply crates across the mid-map (roads kept clear).
- **Atmosphere**: warm sun (1.0/0.95/0.85, 1.2 intensity, 52°/-28°), linear
  fog 90→240 m (subtle haze, readability preserved).

### New assets
Runtime: `ExplodeOnDeath.cs` (hazard props). Materials (9, shared, under
`Assets/_Game/Art/Environment/`): M_Concrete, M_DirtRoad, M_MetalDark,
M_Sandbag, M_WarnYellow, M_WarnDark, M_FuelRed, M_ScorchDark, M_CrateGreen.

### Safety/perf properties
Decorative items have no colliders (invisible to NavMesh + selection);
blocking props are NavigationStatic + BatchingStatic with simple box
colliders; destructible props are non-static; one spot light total; all
materials shared assets.

### Known follow-ups
- Barrel AREA damage intentionally omitted (visual explosion only) — add a
  small splash via MissileProjectile-style overlap when wanted.
- SampleScene (match map) untouched this pass — `Dress Battlefield (Polish
  Pass)` exists for it; the new prop builders can be ported.

## Addendum (2026-06-12) — gameplay systems expansion

### Hazard area damage (PROVEN numerically in play mode)
`ExplodeOnDeath` now deals real area damage: radius 4, 35 dmg with linear
falloff to 40% at the edge, parent-deduped, skips dead/self, team-agnostic
(hazards hurt everyone — placement is the tactic). Test result: barrel
detonation damaged 4 targets, test soldier 100 → 76.55 HP ✓ (barrels also
chain-react).

### Cover system (PROVEN numerically)
New `CoverObject` (static registry, damage-time check only — zero per-frame
cost). Infantry within `coverRadius` of sandbags (2.2 m / 30%) or concrete
barriers (2.4 m / 35%) takes reduced damage via a hook at the top of
`Health.TakeDamage` (deterministic on all clients). Test: identical 20 dmg →
86 HP in cover vs 80 in open ✓. Editor gizmo on selection.

### RPG soldier visual upgrade
`BridgeOps.UpgradeRPGSoldierVisual` swapped the primitive body for the rigged
Sandstorm Sentinel model (same recipe: −90°X, ground −0.1, scale 1, explicit
LODGroup bounds, TeamColorApplier slot-3 on 3 LODs, legacy marker disabled).
RPGLauncher + FirePoint preserved (reparented to root, shoulder position) —
RocketCombat wiring untouched. Backup: RPGSoldierPrefab_PRIMITIVE_Backup.
Prefab audit passes. ⚠ Visual check still recommended in play (launcher
alignment on the new shoulder).

### SampleScene battlefield dressing
`Tools → RTS → Map → Dress SampleScene Battlefield`: central contested
crossroads (4 barriers + 2 sandbag walls with cover, 4 chainable barrels,
2 ammo crates, scorch), NE/SW flank outposts, road cross, fog 120–320 m.
Parented under Environment so the scene's NavMeshSurface (Children mode)
bakes the blockers — NavMesh rebaked ✓. All content within |x|,|z| < 45;
corner spawns untouched.

### Bugs found & fixed during the pass
- ParticleSystem "Velocity curves must all be in the same mode" — x/z axes
  now set to the same TwoConstants mode as y in all three smoke systems.
- `Default-Particle.psd` builtin fails to load in Unity 6 — replaced with a
  procedurally generated 64×64 radial-gradient texture (zero dependencies).

### Continuation (same day)
- **Launch flashes added to RocketCombat + MissileLauncherCombat** — the two
  projectile-weapon paths that had no muzzle feedback (hitscan + aircraft
  already had it). RPG/artillery fire now flashes at the muzzle/rack.
- **Validators extended** (scene audit): hazards have Health + sane
  radius/damage, cover values in range, exactly one BattlefieldDressing
  root. DevSandbox audit: 12 hazards ✓, 5 covers ✓, all green.
- **RPG soldier verified by screenshot in play**: rigged body upright,
  shoulder launcher reads as distinct silhouette, health bar OK.

### Known limits / next
- Enemy RPG variant still primitive (kept red-identity; same recipe applies).
- Cover is target-position based (no directionality) — documented simple v1.
- Tactical AI items (hazard targeting, cover-seeking) deferred — both now
  have clean component hooks (CoverObject registry, hazard Health objects).
- RPG launcher angle is serviceable; fine-tune its localRotation on the
  prefab if a flatter carry pose is preferred.

## Addendum (2026-06-12, second pass) — Enemy AI + skirmish loop

### New systems (verified live in DevSandbox)
- **`SkirmishDirector`** (`Scripts/AI/`): escalating enemy waves (size =
  base + waveNumber, cap 10 active), two spawn lanes rotating per wave,
  route = lane → central crossroads → player base. **Hard MP gate**: disables
  itself in a Photon room unless `allowInMultiplayer` is ticked. AutoStart
  12 s, interval 35 s. Installed in DevSandboxScene
  (`BridgeOps.AddSkirmishDirector`, idempotent).
- **`EnemyAssaultBrain`** (attached at RUNTIME by the director — zero prefab
  changes): 1 s interval scans, role-based target priority (RPG: Vehicle 100 >
  Building 80 > Aircraft 60 > Infantry 40; rifle: Infantry first, buildings
  last; −0.5/m distance tiebreak), no-retarget-while-fighting (anti-flicker),
  cover-seek v1 (infantry hops to nearest CoverObject ≤6 m on first engage,
  10 s cooldown), resumes waypoint march when idle.
- `CoverObject.FindNearestPoint` — AI cover query helper.

### Live test results
Wave 1 (2 units) spawned lane 1, marched through the crossroads, acquired
player squad, chased + fired ("Target acquired — immediate fire ready").
Wave 2 (3 units) spawned lane 2 — escalation + lane rotation confirmed.

### Console hygiene
`UnitMovement.MoveTo` log now dedupes (only when destination moves >2 m) —
combat chase was re-issuing MoveTo every tick and flooding the console.
(First patch attempt hit the wrong log line and broke compile; fixed.)

### Test tools (Tools → RTS → Test)
Install Skirmish Director In Open Scene / Spawn Enemy Wave Now (Play) /
Clear Skirmish Enemies (Play); bridge ops: SkirmishStatus, AddSkirmishDirector.

### Deliberate scope notes
- Hazard-aware ENEMY targeting skipped with cause: barrels carry
  `team=Enemy` (so the player can attack them) — enemy units can't target
  same-team objects. Hazards are a player-side tactic; flipping to a neutral
  team enum is the future enabler.
- Enemy roster is RPG-only (the project's only real enemy combat prefab);
  director takes more prefabs when they exist.
- SampleScene: dressing done previous pass; skirmish director NOT installed
  there (multiplayer scene) — installable per-test via the menu tool.

## DIRECTION (2026-06-12): Online PvP RTS — wave/skirmish development CANCELLED

The game is an online player-vs-player RTS. The SkirmishDirector /
EnemyAssaultBrain stack stays as a DEV-ONLY manual test tool:
- `autoStart = false` (code default AND the DevSandbox scene instance)
- hard-disabled in any Photon room unless `allowInMultiplayer` is ticked
- runs only via Tools → RTS → Test → Spawn Enemy Wave Now / Install / Clear

### Player-controlled completeness status (accumulated, all verified in play)
- **Infantry** (Soldier, RPG): rigged models, idle/run/attack/death anims,
  rings, bars, team colors, muzzle/launcher flashes, trails, FirePoints ✓
- **Vehicles** (Humvee/APC/Artillery/MissileLauncher/Dozer): tuned agents,
  turret fire VFX, artillery rack flash, death explosions + wrecks-scorch ✓
  (deep "heaviness feel" pass = future work)
- **Aircraft** (StrikeJet + Airfield): orientation/banking system, engine
  trail (moving only), hardpoint launch flash, missile trail + impact,
  parking/taxi/sortie logic ✓
- **Buildings**: Health + damage-showing bars, damage smoke states,
  destruction explosion + scorch + lingering smoke, production verified ✓
- **Command feel**: tapered tracers, impact sparks, cover (35%/30%) and
  hazards (35 dmg r4) as PLAYER tactics, snappy infantry turning ✓
- **Menu/online flow**: NOT deeply audited in these passes — top remaining
  item for the PvP direction, together with a two-client room smoke test.

### Known cosmetic item
NavMesh rebakes re-embed NavMeshData → scenes flip back to binary
serialization. Re-run BridgeOps.ExternalizeNavMeshData +
ConvertScenesToTextSilent after rebakes when text scenes are wanted.

## Addendum (2026-06-12, PvP flow pass) — REAL network path verified live

Driven through the bridge with the actual Photon stack (single client, which
the 1–4 player match design supports):
- Connect → master server (eu) → lobby ✓
- CreateRoom("BridgeSmokeTest") ✓ → RequestMatchStart ✓ →
  PhotonNetwork.LoadLevel → SampleScene loaded in play ✓
- MatchStart payload applied: slot 0 → corner A (cb=True dozer=True bank=True
  resNodes=4 resources=10000); corners B/C/D correctly INACTIVE ✓
- Local color (blue) applied to owned Dozer; camera at own base; HUD shown,
  lobby hidden ✓ — PvpCornerReport: 1/4 corners, 1 owned entity, pid=0 ✓
- LeaveRoom → MatchSessionManager.CleanupPreviousMatch ran (MatchId cleared),
  back to JoinedLobby, localPlayerId reset to -1 ✓

New bridge ops: PvpConnect/PvpStatus/PvpCreateRoom/PvpStartMatch/
PvpCornerReport/PvpLeave. TESTING_GUIDE has the full two-client procedure.

Observations (non-blocking):
- After LeaveRoom the active scene briefly remained SampleScene while lobby
  UI returned — verify menu-scene return timing manually.
- OwnerColor logs show unassigned-corner dozers tinted before deactivation
  (invisible — corners inactive) — cosmetic log noise only.
- TRUE two-client sync (remote visuals, double-damage, join-flow) still
  needs the build+editor procedure in TESTING_GUIDE — top remaining item.

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

---

# Addendum — TRUE Two-Client PvP Sync Test + Fix Pass (2026-06-12)

Editor (Client A, blue, bridge-driven) vs Development build (Client B, red,
driven by `TwoClientAutoTester` via `-autotest`). Three full matches run
end-to-end over real Photon EU.

## Bugs found and FIXED (all required a two-client setup to surface)

1. **Remote clients never loaded the match scene.**
   `NetworkManagerRTS.Connect()` set `PhotonNetwork.AutomaticallySyncScene =
   false`; the coordinator flipped it to true only on the MASTER right before
   `LoadLevel`. PUN requires the flag on the FOLLOWER at the moment the scene
   property arrives — so Client B stayed in the menu while the match started
   without it (it even received entity snapshots into the menu scene).
   Fix: set `AutomaticallySyncScene = true` in `Connect()` (every client).
2. **Snapshot warning flood on remote clients (944 warnings in one match).**
   The master broadcasts state snapshots for inactive-corner entities
   (active=false); remote registries never contain them (inactive objects
   don't register), so every snapshot tick logged "Possible missed spawn".
   Fix: `NetworkEntityStateSync.ApplyEntityStateSnapshot` silently ignores
   missing entities whose snapshot says inactive; still warns for ACTIVE ones.
3. **Stuck in SampleScene after LeaveRoom (menu UI overlaid on battlefield).**
   `OnLeftRoom`'s menu return was gated on `useSceneSplit`, which was false
   (stale serialized value) on the scene-baked NetworkManager instance.
   Fix: gate replaced with "active scene != menu scene AND menu scene
   loadable" (`Application.CanStreamedLevelBeLoaded`) — flag-independent.

## Verified working (per-check evidence in TESTING_GUIDE.md)

Join/room (3×), scene follow, corner assignment (random fill, opposite
corners), ownership isolation, team colors on both screens, movement sync in
both directions (owner-authoritative snapshots + networked Move command —
remote arrived at the exact commanded destination), damage exactly-once
(master-authoritative; verified at single-hit and 14-hit-kill granularity),
networked dozer Build chain with deterministic ids (PowerPlant, Barracks),
networked Produce (Soldier) with correct owner on both clients, power gate
replicating consistently, full cross-client combat kill (chase → 14 shots →
NetDeath destroy applied on owner), leave → menu return (<5 s), match
cleanup and second/third-match cleanliness (no stale entities/resources).

## Not yet covered

- Aircraft sync E2E (Airfield → jet in a live two-client match). The
  transform-broadcast path is shared with ground units, but no live test yet.
- Simultaneous production on BOTH clients in one match.
- 3–4 player matches (needs 2+ external clients; the auto-tester supports
  parallel instances if given distinct log files).

## New tooling

- `BridgeOps.BuildTwoClientPlayer / PvpEntityPositions / PvpProduceWorker /
  PvpProduceSoldier / PvpBuildBarracks / PvpBuildPowerPlant / PvpMoveOwnUnit
  / PvpDamageEnemyUnit / PvpAttackNearestEnemy` (play-mode PvP test ops).
- `Tools → RTS → Multiplayer → …` menu (MultiplayerValidationTools.cs) — one
  click for every op above plus status/corner reports and the player build.
- `Assets/_Game/Scripts/DevTools/TwoClientAutoTester.cs` — Client-B
  automation (join → wait match → auto-move + census loop). Inert without
  `-autotest`.

## Known cosmetic noise (non-blocking, logged for later)

- A particle [Assert] "Setting the duration while system is still playing"
  from CombatVFX hit feedback during combat.
- OwnerColor re-applies (identical color) every snapshot reconciliation tick
  on remote clients — duplicate log lines, no visual effect.
- Unassigned-corner dozers get tinted before deactivation at match start.

---

# Addendum — Game Feel / Motion / VFX / Presentation Pass (2026-06-12)

Creative upgrade pass focused on how the game LOOKS and FEELS under manual
control in DevSandbox. No gameplay-rule changes; everything below is
presentation, all multiplayer-safe (local visuals or transform-motion-driven).

## New feel systems (Assets/_Game/Scripts/VFX/)

1. **CameraShakeFX** — distance-attenuated positional camera shake (trauma²
   decay, Perlin offsets on the camera child; rig keeps authority). Hooked to
   explosions (0.12/0.3/0.55 by size), cannon shots, RPG and artillery launches.
2. **RecoilKickFX** — procedural firing recoil: spring-damped kick of a unit's
   VISUAL children (never the gameplay root — agent/colliders/network sync
   untouched). Rifle 0.05 m, cannon 0.14, RPG 0.18, artillery 0.22, turret
   0.03, aircraft missile-separation bump 0.08.
3. **GroundDustFX** — movement dust at every ground unit's feet, emitted BY
   DISTANCE (rate-over-distance): zero idle cost, zero per-frame script work,
   works identically for remote network ghosts. Vehicles get 2.2× puffs.
   Auto-attached in UnitMovement.Start.
4. **VehicleLeanFX** — vehicles pitch under accel/brake and roll into turns
   (reads transform deltas only → also animates remote ghosts). Auto-attached
   to Vehicle-category units. Max 6°, spring-smoothed.
5. **CommandMarkerVFX** — right-click feedback: expanding green ground ring +
   fading centre glow on move orders, red double-pulse on attack orders.
   Local-only (issuing client).

## Explosion overhaul (CombatVFX)

- White-hot core flash (0.1 s) sells the detonation moment.
- Dark debris chunks (mesh-cube particles, gravity, tumble) on medium/large.
- Expanding ground shockwave ring (0.45 s quad) on medium/large.
- Camera shake call per explosion.
- FIXED the long-standing "[Assert] Setting the duration while system is
  still playing" console spam: every runtime-built ParticleSystem now does
  Stop(StopEmittingAndClear) before configuration (7 sites in CombatVFX +
  GroundDustFX).

## Camera feel (RTSCamera)

- Smoothed pan: accelerates in, glides out (panSmoothTime 0.12 s, set 0 for
  legacy instant behaviour).
- Smoothed zoom: scroll sets a target height, rig eases (zoomSmoothTime
  0.18 s). All existing behaviours (edge scroll, Q/E rotate, minimap
  TeleportTo) preserved.

## Lighting / atmosphere (DevSandboxScene + SampleScene, saved)

- Sun: warm tint (1, .96, .88), intensity 1.15, SOFT shadows at 0.72 strength.
- Trilight ambient (sky .58/.65/.75, equator .46, ground .30) — kills the
  pitch-black shadow zones the audit found; units readable everywhere.
- Subtle linear fog 110–320 m for depth (starts beyond minimap range).

## Presentation cleanup

- PolishHealthBars op: 20 prefabs resized by class — buildings 2.4×0.22,
  vehicles 1.5×0.17, aircraft 1.4×0.16, infantry 1.0×0.14.
- TidyDevSandboxModels op: the oversized dev-reference soldiers
  (Soldier_Rigged / Soldier_LOD0-2) deactivated (kept in scene, disabled).

## Files changed

Scripts: CombatVFX.cs (+core/debris/shockwave/shake/assert-fix), UnitCombat.cs
(recoil hooks), RocketCombat.cs, MissileLauncherCombat.cs,
BuildingTurretCombat.cs, AircraftWeapon.cs (launch feel), UnitMovement.cs
(dust/lean auto-attach in Start), UnitSelector.cs (command markers),
RTSCamera.cs (smoothing). New: CameraShakeFX.cs, RecoilKickFX.cs,
GroundDustFX.cs (+ shared SoftCircleTex), VehicleLeanFX.cs, CommandMarkerVFX.cs.
Editor: BridgeOps.cs (+PolishSceneLighting, PolishHealthBars,
TidyDevSandboxModels ops).
Prefabs: 20 health-bar resizes. Scenes: lighting in DevSandbox + SampleScene;
dev models disabled in DevSandbox.

## Multiplayer safety review

- All new systems are local visuals or driven purely by observed transform
  motion (dust/lean animate remote ghosts correctly, no network traffic).
- Recoil/shake calls live inside owner-gated fire methods — no duplicated
  damage, no state changes, no new RPCs/PhotonViews.
- No skirmish/AI autostart touched; command markers fire only from local
  input so they can never mark enemy commands.

## Tuning knobs (all Inspector-exposed or single constants)

- RTSCamera.panSmoothTime / zoomSmoothTime (0 = legacy).
- VehicleLeanFX pitchPerAccel / rollPerYawSpeed / maxLean / smoothing.
- RecoilKickFX strengths at each call site; spring constants in KickRunner.
- CameraShakeFX MaxRange + per-event strengths.
- GroundDustFX rateOverDistance (1.6) + scale per category in UnitMovement.
- Explosion debris/shockwave counts and sizes in CombatVFX.Explosion.

## Manual tests

1. DevSandbox → Play → select soldiers → right-click ground far away: green
   ring marker, smooth camera, dust trail behind running soldiers.
2. Right-click the enemy dummy: red double-pulse + soldiers fire with visible
   body recoil; RPG soldier kicks back hard on rocket launch.
3. Kill the fuel barrels: explosion now has white core, debris, ground
   shockwave ring and a camera thump when close.
4. Order the Humvee/dozer around: body leans into turns, dust kicks up.
5. Scroll zoom + WASD: motion eases instead of stepping.
6. Console must stay clean (the particle duration assert is gone).
