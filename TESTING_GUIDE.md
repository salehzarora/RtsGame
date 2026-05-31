# Local Multiplayer Testing Guide

Practical workflows for testing 1–4 player Photon matches without re-clicking
through the main menu / lobby every time.

The project ships with a `MultiplayerDevBootstrap` component that reads a per-
instance config from **command-line args** (standalone dev builds), **MPPM
Player Tags** (Unity Multiplayer Play Mode), or **Inspector overrides** and
drives the existing lobby + coordinator automatically.

Defaults are inert — with no values, the project boots into the main menu as
normal. Gameplay logic is unchanged.

---

## TL;DR

1. Run **Tools → RTS → Multiplayer → Setup Dev Bootstrap** once. Save the scene.
2. Pick a workflow:
   - **A.** Install **Unity Multiplayer Play Mode** (MPPM) and use Player Tags.
     Each virtual player runs inside the same Editor process — fastest iteration.
   - **B.** Editor + N standalone dev builds. Build once, launch many times with
     different CLI args. Works today without installing anything.
3. Use the **CLI / tag / Inspector reference** below to give each instance a
   unique identity (`playerName`, `color`, `room`, `startSlot`) and the auto-
   actions you want (`autoConnect`, `autoCreateRoom`, `autoJoinRoom`, `autoStart`).

---

## Does Photon work with Multiplayer Play Mode?

**Yes.** MPPM was originally written around Netcode for GameObjects but it does
not block other transports. Each virtual player runs its own Photon connection
to the cloud, exactly like separate processes do. Per-instance state lives in
that virtual player's player loop, so two MPPM players in the same Editor are
independent clients from Photon's perspective.

Caveats:

- The Photon **AppId / GameVersion** must match across players. They already do,
  because they share the project.
- Each virtual player counts as **one Photon CCU**. The free tier (20 CCU) is
  enough for 4-player local testing.
- MPPM is **optional**. If you do not want to install it, Workflow B (standalone
  dev builds) is fully supported and is what `MultiplayerDevBootstrap`'s CLI
  args were designed for.

---

## One-time setup

In Unity:

1. **Tools → RTS → Match → Setup Multiplayer Match Map** — bakes the 4 corner
   bases. (Idempotent.)
2. **Tools → RTS → Multiplayer → Setup Multiplayer Lobby UI** — builds the
   lobby with 4 player rows + the A/B/C/D map preview.
3. **Tools → RTS → Multiplayer → Setup Dev Bootstrap** — adds
   `MultiplayerDevBootstrap` to `GameManager`.
4. **Tools → RTS → Multiplayer → Setup Dev Commander** — adds
   `DevCommanderPanel` to `GameManager` (dev-only IMGUI panel for one-window
   testing, see below).
5. Ctrl+S to save the scene.
6. Confirm `Tools → RTS → Match → Validate 4 Player Map Setup` and
   `Tools → RTS → UI → Validate 4 Player Lobby UI` both print a PASS line.
7. Run `Tools → RTS → UI → Validate UI Duplicates` and
   `Tools → RTS → Scenes → Validate Multiplayer Scene Setup` — both should
   report no duplicates and no missing managers. If duplicates exist, run
   the cleanup tool below first.

---

## Workflow A — Unity Multiplayer Play Mode (recommended for iteration)

### Install MPPM

1. **Window → Package Manager**.
2. Top-left dropdown: **Unity Registry**.
3. Search for **Multiplayer Play Mode**, click **Install**.
4. A new window opens at **Window → Multiplayer → Multiplayer Play Mode**.

### Configure virtual players

Open **Window → Multiplayer → Multiplayer Play Mode**. Enable up to **3 virtual
players** in addition to the main Editor (total = 4, which matches the map).
For each virtual player, expand its row and add **Tags** like:

```
Main editor          Player 2 (virtual)   Player 3 (virtual)   Player 4 (virtual)
playerName=Alpha     playerName=Bravo     playerName=Charlie   playerName=Delta
color=blue           color=red            color=green          color=yellow
room=DevRoom         room=DevRoom         room=DevRoom         room=DevRoom
startSlot=A          startSlot=B          startSlot=C          startSlot=D
autoCreateRoom       autoJoinRoom         autoJoinRoom         autoJoinRoom
autoStart
```

Notes:

- Use **`autoCreateRoom`** on exactly one player (the host) and **`autoJoinRoom`**
  on the others.
- **`autoStart`** only needs to be set on the host. Non-master clients ignore it.
- Press **Play**. All four instances will connect, the host creates the room,
  the others join, each picks their corner, and after `autoStartDelaySec` (3s
  default) the host requests MatchStart.

### Smaller matches

For a 2- or 3-player test, just enable fewer virtual players. The host's
`autoStart` will fire as soon as the delay elapses, regardless of how many
clients are in the room (1–4 are all valid).

---

## Workflow B — Editor + standalone dev builds (no MPPM required)

### Make a dev build

**File → Build Settings → Build And Run** (or just Build). A few notes:

- **Development Build** ticked = console + faster builds.
- Place the build in a stable folder; you'll launch the `.exe` multiple times.
- Re-build only when **scripts** change. Asset/scene changes are picked up by
  the Editor instance directly; for the standalone build you do need a rebuild.

### Launch a session

Launch the standalone with CLI args. The Editor instance can be a player too —
configure its identity by either selecting `GameManager` and filling in the
`MultiplayerDevBootstrap` Inspector overrides, OR by simply using the lobby UI
manually before pressing Start.

Example 2-player session on Windows (run each line in its own terminal):

```
RtsGame.exe -playerName Alpha   -color blue   -room DevRoom -startSlot A -autoCreateRoom -autoStart
RtsGame.exe -playerName Bravo   -color red    -room DevRoom -startSlot B -autoJoinRoom
```

Same idea for 3 and 4 players — just add more terminals with `Charlie` /
`Delta`, `color green` / `yellow`, `startSlot C` / `D`, `-autoJoinRoom`.

### Launch the Editor as the host

If you want the Editor to be the host, fill the bootstrap Inspector fields
on `GameManager`:

| Field            | Value          |
|------------------|----------------|
| playerName       | Editor         |
| colorName        | blue           |
| roomName         | DevRoom        |
| startSlot        | 0              |
| autoCreateRoom   | true           |
| autoStart        | true           |

…then launch one or more standalone builds with `-autoJoinRoom -room DevRoom`.

---

## CLI / Tag / Inspector reference

All three sources accept the same set of keys. Priority order (highest wins):

1. Command-line args
2. MPPM Player Tags
3. Inspector overrides

| Key              | Type    | CLI example                | MPPM tag example   | Effect |
|------------------|---------|----------------------------|--------------------|--------|
| `playerName`     | string  | `-playerName Alpha`        | `playerName=Alpha` | Photon `NickName` (shown in lobby) |
| `color`          | string  | `-color blue`              | `color=blue`       | One of: blue / red / green / yellow / orange / purple |
| `room`           | string  | `-room DevRoom`            | `room=DevRoom`     | Room name for create/join (default: `DevRoom`) |
| `startSlot`      | int / letter | `-startSlot A` or `-startSlot 0` | `startSlot=A` | Corner pick — 0..3 or A..D. -1 = leave unchosen (host will random-fill) |
| `autoConnect`    | flag    | `-autoConnect`             | `autoConnect`      | Enable multiplayerMode + `Connect()` at Start |
| `autoCreateRoom` | flag    | `-autoCreateRoom`          | `autoCreateRoom`   | After connect, create `room`. Implies `autoConnect` |
| `autoJoinRoom`   | flag    | `-autoJoinRoom`            | `autoJoinRoom`     | After connect, join `room`. Implies `autoConnect`. `autoCreateRoom` wins if both are set |
| `autoStart`      | flag    | `-autoStart`               | `autoStart`        | After joining the room, request a MatchStart (non-master clients ignore) |

The Inspector also has `autoStartDelaySec` (default 3 s) — the delay between
joining the room and requesting MatchStart, so other clients can join +
sync their colour/startSlot first.

---

## Dev Commander panel (one-window testing)

A dev-only IMGUI overlay that lets a single client drive both sides of a
1–4 player match. The panel is **only shown when**:

- You're running a **Development Build** (`Debug.isDebugBuild == true`), OR
- The component's `devMode` flag is `true` on `GameManager`.

In a release build with `devMode` off it is fully invisible and inert.

### Toggle

Press **F12** (configurable) inside Play mode to show / hide the panel.

### What it shows

One row per player in the current room (or the local player in single-player):

```
Player 1: Alpha   actor #1   playerId=0   color=Blue   startSlot=A
   resources = 10000    units = 3
   [Select] [+1000 Res] [Spawn Inf] [Spawn Tank] [Spawn Aircraft] [-100 CC] [Kill Units]
```

Below the list: a "Selected player actions" block with **Move to point**
(x / z text + button), **Order attack on Player N** (cycle victim button), and
**Reset Match State**.

### Network safety

Every action routes through an existing network-safe path — no remote-only
state is mutated locally:

| Action               | Routed via                                                      |
|----------------------|-----------------------------------------------------------------|
| Give resources       | Master applies `PlayerResourceManager.AddResources`; the existing `ResourceChanged` event broadcasts the absolute total to every client. |
| Damage base          | Master applies `Health.TakeDamage`; existing `ApplyDamage` event broadcasts the new HP. |
| Kill all units       | Master applies `TakeDamage(huge)` per target-owned unit; existing `EntityDestroyed` event broadcasts. |
| Spawn infantry/tank/aircraft | Photon `DevCommanderEvent` is sent to every client; **only the target player's client** acts on it by issuing `PlayerCommand.Produce` through `CommandDispatcher.Issue` — the dispatcher's ownership check passes (target == local) and `NetworkCommandRelay` broadcasts the produce to others. |
| Move units / Attack player | Same pattern as spawn: the target player's client issues a real `PlayerCommand.Move` / `PlayerCommand.Attack` via `CommandDispatcher.Issue`. |
| Reset match state    | Every client calls `MatchSessionManager.CleanupPreviousMatch` locally. |

This is the only correct way to "command another player's units" in this
project's owner-authoritative architecture — the dispatcher rejects any local
command where `cmd.playerId != LocalPlayerId`, so we have to bounce the
intent off the owning client.

### Spawn requirements

`Spawn Inf` needs a Barracks owned by the target player; `Spawn Tank` needs a
Vehicle Factory; `Spawn Aircraft` needs an Airfield. With no producer the
action logs a warning and no-ops. Order them via the player's Dozer first (or
give yourself resources with `+1000 Res` until you can).

### Every action logs

```
[DevCommander] Requested SpawnUnit for playerId=1 arg=ArtilleryTank
[DevCommander] Command sent through network path (Photon RaiseEvent).
[DevCommander] Issuing Produce(ArtilleryTank) for player 1 via 'VehicleFactory_P1'. spawnId=abc…
```

Grep `[DevCommander]` in the Console to confirm anything you click was sent.

---

## Corner mapping (lobby preview ↔ gameplay-camera view)

The lobby map preview anchors are baked into the canvas:
| Button | Preview position | cornerIndex |
|---|---|---|
| A | top-left of the preview rect | 0 |
| B | top-right | 1 |
| C | bottom-left | 2 |
| D | bottom-right | 3 |

> ⚠️ **Convention — gameplay camera, not raw world.** In this game's tilted
> top-down RTS camera, world `+Z` appears at the **bottom** of the player's
> screen and world `-Z` at the **top**. The lobby preview must match what
> the player *sees*, not the mathematical world axis. So the canonical
> mapping is *visually* labelled and the `Z` signs are inverted relative to
> what a naive "top = +Z" reading would give:

| cornerIndex | Letter | Visual quadrant (on player's screen) | World (X, Z) |
|---|---|---|---|
| 0 | A | TopLeft     | (-80, **-70**) |
| 1 | B | TopRight    | (+80, **-70**) |
| 2 | C | BottomLeft  | (-80, **+70**) |
| 3 | D | BottomRight | (+80, **+70**) |

[`SetupMultiplayerMatchMap.CornerPositions`](Assets/_Game/Editor/SetupMultiplayerMatchMap.cs)
uses this exact mapping, so any FUTURE bake produces the right layout.

### History of this bug
| Era | cornerIndex 0 sat at | Result |
|---|---|---|
| Pre-fix (very first) | `(-80, -70)` | Diagonal-swapped vs. lobby (A button spawned player at world bottom-left). |
| First fix (raw-world "top = +Z") | `(-80, +70)` | Lobby button A spawned player at world `+Z`, but the camera makes `+Z` the BOTTOM of the screen → **vertically flipped** vs. what the player sees. |
| Current fix (gameplay-camera "top = -Z") | `(-80, -70)` | A button (visual top-left) spawns at world `-Z` = visual top of screen. ✓ |

### Fix an existing scene (one click)

```
1. Open SampleScene (or whatever gameplay scene)
2. Tools → RTS → Match → Validate Corner Mapping    ← shows mismatches
3. Tools → RTS → Match → Fix Corner Mapping         ← renames + reindexes
4. Tools → RTS → Match → Validate Corner Mapping    ← should show all ✓
5. Ctrl+S
```

`Fix Corner Mapping` doesn't move anything — it just renames each
`CornerBase_*` GameObject and updates its `cornerIndex` to match the
quadrant its `transform.position` already sits in. So the map decorations,
dozer spawn points, resource clusters and bank components stay where they
are; only the LABELS / INDICES change to match the lobby preview.

### Runtime log signature (Orange + Corner B test)

```
[Lobby] Button B visual=TopRight -> actual startSlot=1
[Lobby] startSlot 1 = gameplay-view TopRight corner (world +Z appears at BOTTOM of screen, -Z at TOP).
[Lobby] Set Photon property startSlot=1 (B).
...
[MatchStart] Actor #1: playerSlot=0, color=Orange, selectedStartSlot=1 (B visual=TopRight), visualCorner=B -> finalStartSlot=1 (visual=TopRight).
...
[SampleScene] Activated visualCorner=B (index=1, visual=TopRight) at world position=(80.0, …, -70.0) — gameplay-view TopRight as seen by the player — for actor #1 (slot 0, resourceNodes=4, dozer=yes).
[SampleScene] Skipped visualCorner=A (index=0, visual=TopLeft): unassigned.
[SampleScene] Skipped visualCorner=C (index=2, visual=BottomLeft): unassigned.
[SampleScene] Skipped visualCorner=D (index=3, visual=BottomRight): unassigned.
```

The three layers (Lobby, MatchStart, SampleScene) must all agree on the
**visual** quadrant. If any line disagrees, the scene wasn't re-fixed under
the new convention — re-run `Fix Corner Mapping`. Specifically:
- Activated CornerBase's `position.z` should be **negative** for A/B and
  **positive** for C/D.
- `visual=TopLeft` corner must have world position `(-X, -Z)`.

---

## "Lobby shows Orange + B but the game spawns Blue at A" — the real fix

The Photon property write inside `SetLocalPlayerColor` *can* race with the
Start Match click — when that happens, `BroadcastMatchStart`'s
`TryGetPlayerColor` read finds no `armyColor` on the master's own Player
object and falls back to `DefaultColor(playerSlot)` (blue for slot 0). The
lobby UI looks correct because `RefreshLobby` re-reads after the property
update arrives later; the broadcast doesn't get that second chance.

**Fix**: `MultiplayerLobbyUI.OnClickStartMatch` now does two things before
calling `RequestMatchStart`:

1. **Force-push** the LOCAL `PlayerFactionManager`'s current pick directly
   to Photon via `NetworkManagerRTS.SetLocalPlayerColor`. Photon's
   `LocalPlayer.SetCustomProperties` updates the LocalPlayer object
   IMMEDIATELY (before the server even ACKs), so the subsequent
   `BroadcastMatchStart` reads the new value.
2. **Print a payload preview** so you can verify UI ↔ Photon alignment
   *before* the broadcast — not after the gameplay scene loads.

`startSlot` doesn't need a force-push: `SetLocalStartSlot` already writes
directly to `LocalPlayer.SetCustomProperties` without a pending queue, so
the lobby corner pick is already in Photon by the time you click Start.

### Expected log sequence after the fix — Orange + Corner B, solo host

```
[MatchStart] Host clicked Start Match.
[Lobby] Pre-broadcast: re-pushing local color 'Orange' to Photon armyColorName.
[Lobby] SetLocalPlayerColor: Orange (RGB 1.00,0.55,0.10). Will push to Photon when in room.
[Lobby] Set Photon property armyColorName=Orange (also armyColor=Vec3(1.00,0.55,0.10)).
[PayloadPreview] ───── Start Match preview (what master will read) ─────
[PayloadPreview] Actor #1 (local), playerSlot=0:
[PayloadPreview]   Local PFM color  = Orange
[PayloadPreview]   Photon armyColor = Orange     ← master reads THIS into payload
[PayloadPreview]   Photon startSlot = 1 (B)      ← master reads THIS into payload
[PayloadPreview] ────────────────────────────────────────────────────
[MultiplayerMatch] Starting match with 1 player(s). startingResources=10000.
[MultiplayerMatch] Final corner assignment: slot 0 actor #1 → corner B (index 1).
[MatchStart] Actor #1: playerSlot=0, color=Orange, selectedStartSlot=1 (B), finalStartSlot=1 (B).
[MatchStart] Loading GameMapScene via PhotonNetwork.LoadLevel('SampleScene') ...
─── SCENE LOAD ───
[SampleScene] Applying actor #1: color=RGB(1.00,0.55,0.10), corner=B
[TeamColor] Applying RGB(1.00,0.55,0.10) to actor #1 / playerSlot 0.
[SampleScene] Activated CornerBase B for actor #1 (slot 0, index 1, resourceNodes=4, dozer=yes).
```

### If you still see Blue at A

The new `[PayloadPreview]` block tells you the exact stage where it went wrong:

| Symptom | Meaning | Action |
|---|---|---|
| `Local PFM color = (none)` | The colour swatch click never reached `PlayerFactionManager.SetColor`. | Re-click the swatch in the lobby. |
| `Local PFM color = Orange` but `Photon armyColor = (missing …)` | The force-flush didn't materialise — bug in `SetLocalPlayerColor` / `TryFlushPendingColor`. | Paste the Console output; this is the next bug to chase. |
| `✗ MISMATCH actor #1: local PFM color 'Orange' ≠ Photon armyColorName 'Blue'` | The Photon property was overwritten by something between the click and the broadcast. | Grep for a stray `armyColor` write outside `NetworkManagerRTS`. |
| `Photon startSlot = (missing …)` | Corner click didn't write to Photon. | Re-click the corner in the lobby; check `SetLocalStartSlot` log fires. |
| All preview values look correct but `[MatchStart] Actor #1: color=default` | Master's `TryGetPlayerColor` reads its own properties and still misses — different bug in `NetworkManagerRTS.TryGetPlayerColor`. Paste logs. |
| `[MatchStart] Actor #1: color=Orange ... finalStartSlot=1 (B)` but gameplay still shows blue at A | Bug in `ApplyMatchStartLocally` or `MultiplayerColors` — the `[TeamColor] Applying RGB(...) to actor #X / playerSlot Y` line will tell you what got applied. Paste logs. |

---

## Lobby color/corner picks — what's actually going into Photon

The full data flow is now logged at every step with a uniform `[Lobby]` /
`[MatchStart]` / `[<SceneName>]` / `[TeamColor]` prefix so you can prove
end-to-end that the lobby pick is what gameplay uses.

**Photon property keys (canonical, single source of truth):**

| Key | Type | Set by | Read by |
|---|---|---|---|
| `armyColor` | `Vector3` (r,g,b) | `NetworkManagerRTS.SetLocalPlayerColor` → `TryFlushPendingColor` | `BroadcastMatchStart` (master) + lobby UI render |
| `armyColorName` | `string` | same | lobby UI render + diagnostic logs |
| `startSlot` | `int` (0..3, -1=none) | `NetworkManagerRTS.SetLocalStartSlot` | `ComputeCornerAssignment` (master) + lobby UI render |

No `color` / `teamColor` / `playerColor` / `selectedColor` / `finalStartSlot`
fallback keys are read anywhere — verified with a project-wide grep. The
keys are exported as `const string` from `NetworkManagerRTS` so every
caller imports the same name.

### Expected log sequence — single player flow

```
Click a colour swatch in the lobby:
  [Lobby] Local selected color: Yellow (RGB 1.00,0.85,0.18).
  [Lobby] SetLocalPlayerColor: Yellow (RGB 1.00,0.85,0.18). Will push to Photon when in room.
  [Lobby] Set Photon property armyColorName=Yellow (also armyColor=Vec3(1.00,0.85,0.18)).
  [Lobby] PlayerPropertiesUpdate actor #1 armyColorName=Yellow startSlot=-

Click corner B in the map preview:
  [Lobby] Local selected corner: B / startSlot=1.
  [Lobby] Set Photon property startSlot=1 (B).
  [Lobby] PlayerPropertiesUpdate actor #1 armyColorName=Yellow startSlot=1 (B)

Click Start Match (host only):
  [MatchStart] Host clicked Start Match.
  [MultiplayerMatch] Starting match with 1 player(s). startingResources=10000.
  [MultiplayerMatch] Final corner assignment: slot 0 actor #1 → corner B (index 1).
  [MatchStart] Actor #1: playerSlot=0, color=Yellow, selectedStartSlot=1 (B), finalStartSlot=1 (B).
  [MatchStart] Finalized payload for room 'X', matchId '<guid>'. (1 player(s), startingResources=10000)
  [MatchStart] Loading GameMapScene via PhotonNetwork.LoadLevel('SampleScene') ...

After SampleScene loads:
  [SampleScene] Loaded as gameplay scene.
  [SampleScene] Applying payload: 1 players
  [SampleScene] Actor #1 -> playerSlot 0 -> corner B
  [SampleScene] Applying actor #1: color=RGB(1.00,0.85,0.18), corner=B
  [TeamColor] Applying RGB(1.00,0.85,0.18) to actor #1 / playerSlot 0.
  [SampleScene] Activated CornerBase B for actor #1 (slot 0, index 1, resourceNodes=4, dozer=yes).
```

If `[MatchStart] Actor #1: ... color=default` instead of `color=Yellow`, the
Photon property never landed before the master clicked Start Match — usually
because the click on the swatch happened before joining the room and the
`pendingColorPushHas` flush either didn't fire or the master read before
the cross-client sync arrived. Re-click the colour while in the lobby
panel — the second click pushes immediately because the local player IS in
the room then.

### Print Lobby State — runtime diagnostic dump

`Tools → RTS → UI → Print Lobby State` (runs in Edit OR Play mode while
connected to Photon) dumps the current room state with one line per actor:

```
[LobbyState] Room: 'Test'  Players: 2/4  MasterActor: #1  LocalActor: #1
[LobbyState]   Actor #1 'Alpha' master local  playerSlot=0  armyColorName=Yellow  armyColor=RGB(1.00,0.85,0.18)  startSlot=1 (B)
[LobbyState]   Actor #2 'Bravo'               playerSlot=1  armyColorName=Red     armyColor=RGB(0.92,0.20,0.20)  startSlot=3 (D)
[LobbyState] Corner occupancy:
[LobbyState]   Corner A: free.
[LobbyState]   Corner B: actor #1.
[LobbyState]   Corner C: free.
[LobbyState]   Corner D: actor #2.
```

If `armyColorName=?` for an actor, that actor never pushed the property —
have them click a colour. If `startSlot=-`, they haven't picked a corner —
the master will random-fill one for them at MatchStart. Use this BEFORE
Start Match to verify each player's lobby selection actually reached
Photon, instead of finding out post-MatchStart when units spawn the wrong
colour.

### Cleanup on leave

`MatchSessionManager.ResetPhotonPlayerProperties` already clears
`startSlot = -1` whenever the local player leaves a room (any path: ESC →
Main Menu, lobby Leave, kicked, disconnect). `armyColor` / `armyColorName`
are intentionally NOT cleared — they're persistent player preferences. So
re-joining a different room remembers your colour pick from the menu but
starts fresh on corners.

---

## ESC menu auto-heals at runtime (no editor tool needed)

`EscapeMenuController` now does the same name-based reference repair the
editor tool does — except it runs in `Start` on every scene load, so the
player never has to pop into the menu bar to make ESC work.

What runs in `Start`:

- Resolves `menuCanvas` → `EscapeMenuCanvas` (root, inactive-inclusive).
- Resolves `hudCanvas` → `HUDCanvas` (if present).
- Resolves `mainMenuCanvas` → `MainMenuCanvas` (if present; the gameplay
  scene usually doesn't have one — `OnClickMainMenu` falls back to
  `SceneManager.LoadScene`).
- Resolves `lobbyCanvas` → `LobbyCanvas` (optional).
- Adds `GraphicRaycaster` to `EscapeMenuCanvas` if missing.
- Reports listener counts on `BtnResume` / `BtnOptions` / `BtnMainMenu`
  / `BtnQuit` so you can see at a glance whether `SetupEscapeMenu` ran.

Plus the input gate was relaxed:

- ESC was previously silently ignored when `GameStateManager.IsPlaying`
  was false — which happens when `GameStateManager` is absent or the
  match payload hadn't flipped the state yet. Both are real conditions in
  dev direct-play and edge cases of the lobby → match transition.
- Now `Update` only refuses ESC in `MainMenuScene` (where you should use
  the menu UI, not a pause panel). Every other scene — gameplay scene,
  dev direct-play, future scenes — always permits ESC.
- Every ESC press logs `[EscapeMenu] ESC key detected.` so you can
  immediately tell if the keystroke is reaching the controller. If it
  doesn't fire, the input never reached the script (different problem).

### What you should see on scene load

```
[EscapeMenu] Runtime self-heal started.
[EscapeMenu] menuCanvas resolved: EscapeMenuCanvas
[EscapeMenu] hudCanvas resolved: HUDCanvas
[EscapeMenu] Resume button wired (1 listener(s)).
[EscapeMenu] Options button wired (1 listener(s)).
[EscapeMenu] Main Menu button wired (1 listener(s)).
[EscapeMenu] Quit button wired (1 listener(s)).
[EscapeMenu] Runtime self-heal complete.
```

If any reference fails to resolve, you see an explicit ERROR with the
exact GameObject name it was looking for.

### What you should see when pressing ESC

```
[EscapeMenu] ESC key detected.
[PauseMenu] ESC menu opened.    (first press)
[EscapeMenu] ESC key detected.
[PauseMenu] ESC menu closed.    (second press, or Resume click)
```

Failure modes log explicitly — never silent:

- `[EscapeMenu] ESC swallowed — typing in an input field.`
- `[EscapeMenu] ESC ignored — in MainMenuScene (use the menu UI).`
- `[EscapeMenu] ESC blocked — menuCanvas is null even after Start self-heal. …`
- `[EscapeMenu] (Note: GameStateManager.IsPlaying=false — allowing ESC anyway …)`

### Editor tools still available

`Tools → RTS → UI → Validate Escape Menu` and `Tools → RTS → UI → Repair
Escape Menu` are still there for editor-time auditing, but the runtime no
longer depends on them — they're purely defensive.

---

## "ESC does nothing in the gameplay scene"

Open the gameplay scene (SampleScene) and run:

```
1. Tools → RTS → UI → Validate Escape Menu       (read-only audit)
2. Tools → RTS → UI → Repair Escape Menu          (fix what it found)
3. Ctrl+S
4. Press Play → start a match → press ESC
```

`EscapeMenuController.Update` reads `menuCanvas` and silently returns when
it's null, AND `ShowPauseMenu` early-outs on a null `menuCanvas` too. So
when a cleanup pass nulls the reference, ESC presses ARE detected by
`Update` but every downstream call swallows them. The repair re-points
references by name:

- `EscapeMenuController.menuCanvas` → `EscapeMenuCanvas` (root,
  inactive-inclusive).
- `EscapeMenuController.mainMenuCanvas` → `MainMenuCanvas` (if the scene
  has one; in SampleScene it doesn't and that's fine — ESC → Main Menu
  uses `SceneManager.LoadScene` instead).
- `EscapeMenuController.hudCanvas` → `HUDCanvas`.
- `EscapeMenuController.lobbyCanvas` → `LobbyCanvas` (optional).

It also:

- Ensures exactly one EventSystem (creates / dedupes).
- Ensures the EscapeMenuCanvas has a GraphicRaycaster (so Resume / Main
  Menu / Quit clicks actually land).
- Confirms `BtnResume`, `BtnMainMenu`, `BtnQuit` exist and have ≥1
  persistent OnClick listener. `BtnOptions` is treated as optional.
- Warns if there's no `GameStateManager` in the scene — that's the OTHER
  gate `Update` checks (`GameStateManager.IsPlaying`). Without one, ESC is
  ignored even when everything else is wired.

### What you should see on a healthy run

```
[EscMenu] ✓ Exactly 1 EventSystem.
[EscMenu] ✓ EscapeMenuCanvas present (activeSelf=True).
[EscMenu] ✓ EscapeMenuController on '<host>'.
[EscMenu] ✓ Resume button OK (1 listener(s)).
[EscMenu] ✓ Main Menu button OK (1 listener(s)).
[EscMenu] ✓ Quit button OK (1 listener(s)).
[EscMenu] ✓ Options button OK (1 listener(s)).        (if SetupOptionsMenu was run)
[EscMenu] ✓ GameStateManager present.
```

Then in Play (after Start Match has actually started the match):

```
[PauseMenu] ESC menu opened.    (first ESC press)
[PauseMenu] ESC menu closed.    (second ESC press, or click Resume)
[PauseMenu] Returning to Main Menu.   (Main Menu button)
[PauseMenu] Leaving Photon room — main menu will show after OnLeftRoom.   (if in a room)
[PauseMenu] Quit pressed.   (Quit button — exits Play in the editor, Application.Quit in a build)
```

If `Repair Escape Menu` prints `✗ BtnResume / BtnMainMenu / BtnQuit missing
under EscapeMenuCanvas`, the buttons themselves were destroyed — run
`Tools → RTS → Setup → Setup Escape Menu` to rebuild the canvas with its
buttons, then re-run Repair Escape Menu to wire the controller references.

---

## "I press Play in MainMenuScene and don't see the menu"

Same root cause as the LobbyCanvas wiring issue: a cleanup pass or scene-split
move can null `MainMenuController.menuCanvas`. `MainMenuController.Awake`
only calls `menuCanvas.SetActive(true)` when that ref is non-null, so the
MainMenuCanvas asset stays inactive — invisible in Game View even though
it's there in the Hierarchy.

```
1. Tools → RTS → UI → Validate MainMenuScene UI       (read-only audit)
2. Tools → RTS → UI → Repair MainMenuScene UI         (fix what it found)
3. Ctrl+S
4. Press Play
```

Repair does:

- Ensures exactly one EventSystem (creates / dedupes).
- Re-points `MainMenuController.menuCanvas` → `MainMenuCanvas` (root,
  inactive-inclusive lookup).
- Re-points `MainMenuController.hudCanvas` → `HUDCanvas` if present
  (null is fine in MainMenuScene; the gameplay HUD lives in SampleScene).
- Re-points `MultiplayerLobbyUI.canvasRoot` → `LobbyCanvas` and
  `MultiplayerLobbyUI.mainMenuCanvas` → `MainMenuCanvas`.
- Sets the startup activation state:
  - `MainMenuCanvas.activeSelf = true`
  - `LobbyCanvas.activeSelf = false`
  - `OptionsCanvas.activeSelf = false`
  - `MultiplayerDebugCanvas.activeSelf = false`
- Verifies the title text + `BtnSinglePlayer` + `BtnOnline` exist under
  MainMenuCanvas.
- Warns if any gameplay-only root (Environment, GameplayWorldRoot,
  HUDCanvas, etc.) leaked into MainMenuScene.

After repair the Awake chain works as designed:

```
MainMenuController.Awake → menuCanvas.SetActive(true)         (menu shown)
MultiplayerLobbyUI.Awake → canvasRoot.SetActive(false)        (lobby hidden)
OptionsMenuController.Awake → optionsPanel.SetActive(false)   (options hidden)
[MainMenu] Boot — main menu shown. Awaiting player input.
```

Click `Online` → `MultiplayerLobbyUI.ShowOnlineMenu` hides MainMenuCanvas
and reveals the LobbyCanvas online panel. `Back` returns to MainMenuCanvas.
Standard flow restored.

If the validator reports `✗ Title (...) missing` or `✗ BtnSinglePlayer
(...) missing` under MainMenuCanvas, the buttons themselves were destroyed
— run `Tools → RTS → Setup → Setup Main Menu` to rebuild the canvas
from scratch, then re-run Repair MainMenuScene UI to wire the controllers.

---

## SampleScene loaded but Game View looks empty

If `PhotonNetwork.LoadLevel("SampleScene")` runs (you see `[MatchStart]
Loading … PhotonNetwork.LoadLevel('SampleScene')` in the Console) but the
gameplay scene starts empty (no corner activated, no camera snap, the player
doesn't feel spawned), the most common cause is `NetworkManagerRTS
.useSceneSplit` being **false on SampleScene's NetworkManager** — the
migration tool only updates the open scene, so MainMenuScene's NetworkManager
gets the flag but SampleScene's may still be stale.

**Fixed at the source.** `NetworkMatchCoordinator.Start` no longer gates on
`useSceneSplit`. The presence of a payload in `PhotonNetwork.CurrentRoom
.CustomProperties` is the authoritative signal. So as long as the master
wrote the payload (which it does whenever `useSceneSplit` is true on
MainMenuScene's NetworkManager — the only one that matters for the write
side), every client's coordinator reads it on Start, regardless of their
local `useSceneSplit` value.

You should see this log sequence after Start Match:

```
[MatchStart] Loading GameMapScene via PhotonNetwork.LoadLevel('SampleScene') ...
─── SCENE LOAD ───
[SampleScene] Loaded as gameplay scene.
[SampleScene] Room='Test', matchId='<guid>'
[SampleScene] Applying payload: 1 players
[SampleScene] Actor #1 -> playerSlot 0 -> corner A
[SampleScene] Activated CornerBase A for actor #1 (slot 0, index 0, resourceNodes=4, dozer=yes).
[SampleScene] Skipped CornerBase B: unassigned (index 1).
[SampleScene] Skipped CornerBase C: unassigned (index 2).
[SampleScene] Skipped CornerBase D: unassigned (index 3).
[GameplayWorldRoot] Activated 2 gameplay root(s) after MatchStart.
[MultiplayerMatch] Local player 0 camera positioned at (-80, 0, -70).
```

The `[SampleScene]` prefix is dynamic — it reads the active scene name at
runtime — so if you ever rename the gameplay scene, the logs follow.

### Direct-Play of SampleScene (no lobby)

Press Play with SampleScene as the active scene (no `PhotonNetwork.LoadLevel`
involved). The new Editor-only dev-mode fallback kicks in:

```
[SampleScene] No match payload found in room properties. If you opened
   this scene directly, use dev mode. If you loaded from the lobby, this
   is a bug.
[SampleScene] DevMode: No Photon payload found. Creating local 1-player
   dev setup (corner A).
[SampleScene] Activated CornerBase A for actor #1 …
```

You get a synthetic 1-player match — corner A active, dozer + resources
spawned, camera snapped to that corner — without needing the lobby flow.
Only runs in `Application.isEditor`, so builds are unaffected.

### Validate SampleScene gameplay setup

`Tools → RTS → Match → Validate SampleScene Gameplay Setup` (alias for the
existing gameplay-content audit, tolerant of the SampleScene name):

- `GameplayWorldRoot` present.
- Exactly 4 `CornerBase` components with unique indices 0..3.
- Each CornerBase has non-null `dozer`, `bank`, `resourceCluster` (with
  ≥1 `ResourceNode`).
- `HUDCanvas`, `SelectionCanvas`, `EscapeMenuCanvas`, `CameraRig`,
  `MinimapCameraGO`, `MatchManager`, `Environment` all present.
- No forbidden menu roots leaked in.
- `NetworkManagerRTS.useSceneSplit = true` (warned, not required — the
  coordinator no longer depends on it).

---

## Simplest path — SampleScene IS the gameplay scene

The recommended setup now skips the `GameMapScene` indirection entirely.
SampleScene was always the single source of truth for the gameplay map;
keeping a stripped copy in `GameMapScene.unity` just meant rebuilding after
every SampleScene edit. Point `PhotonNetwork.LoadLevel` at SampleScene
directly:

```
Tools → RTS → Scenes → Use SampleScene As Gameplay Target
```

One click and:

1. `NetworkManagerRTS.gameMapSceneName` is set to `"SampleScene"` and
   `useSceneSplit` to `true` on every NetworkManager in the open scene.
2. **Build Settings** become `[0] MainMenuScene`, `[1] SampleScene`.
   `GameMapScene` is removed from Build Settings (the `.unity` file is
   kept on disk as a backup).
3. (Prompt) menu UI roots (`LobbyCanvas`, `MainMenuCanvas`, `OptionsCanvas`,
   `MultiplayerDebugCanvas`) are stripped from SampleScene so it's
   gameplay-only at runtime.

After running it: open MainMenuScene, press Play, click **Online → Create
Room → Start Match**. Photon `LoadLevel("SampleScene")` fires. SampleScene
becomes the gameplay scene for that room — and the same SampleScene asset
is reused for every other room (Photon room properties + `matchId` provide
all the per-room isolation).

There's also a runtime self-heal: if any older scene still has
`gameMapSceneName = "GameMapScene"` baked in and that scene isn't in Build
Settings, `NetworkManagerRTS.Awake` logs a notice and forces it to
`"SampleScene"`. So existing scenes upgrade automatically on next Play;
only the editor tool needs running if you want the Inspector value
persisted on disk.

The older `GameMapScene` flow below still works if you prefer the explicit
split — but it's no longer the recommended path.

---

## Scene split (MainMenuScene + GameMapScene) — legacy / optional

The recommended architecture: a menu/lobby scene separate from the gameplay
map scene, with `PhotonNetwork.LoadLevel` carrying the room from one to the
other when the host clicks Start Match. This keeps the menu free of gameplay
objects (no map visible behind the title) and gives every match its own
clean instance of the world.

The project still supports the single-scene layout — flip the runtime flag
to choose. Default is OFF (single scene) so anyone upgrading isn't surprised.

### Quick-open shortcuts

After the split is created, jump between scenes with one click:

- `Tools → RTS → Scenes → Open MainMenuScene` — opens the menu scene (your normal Play entry point).
- `Tools → RTS → Scenes → Open GameMapScene` — opens the gameplay scene for direct dev-mode testing.

If you press Play while `SampleScene` is the active scene, you'll see the OLD
single-scene layout (map behind menu, MultiplayerDebugCanvas in the corner).
**Validate Scene Split** now front-loads an error when this happens and
points you at the Open menu.

### One-time setup

1. **Tools → RTS → Scenes → Create Scene Split.** This:
   - Copies `Assets/Scenes/SampleScene.unity` to `SampleScene_Backup.unity`
     (only on the first run; subsequent runs preserve the backup).
   - Copies the source into `MainMenuScene.unity` and strips gameplay-world
     roots (Environment, GameplayWorldRoot container, HUDCanvas,
     EscapeMenuCanvas, ResourceNodes, PlayerStart, EnemyStart, MatchManager,
     SelectionCanvas, MinimapCameraGO, legacy Player0Base/Player1Base).
   - Copies the source into `GameMapScene.unity` and strips menu roots
     (LobbyCanvas, MainMenuCanvas, OptionsCanvas, MultiplayerDebugCanvas).
   - Sets `NetworkManagerRTS.useSceneSplit = true` on the NetworkManager in
     both new scenes (best-effort via reflection — falls back silently if
     the field isn't present yet).
   - Updates Build Settings so MainMenuScene is index 0 and GameMapScene is
     index 1.
   - Opens MainMenuScene at the end so you can press Play immediately.
2. `Tools → RTS → Scenes → Validate Scene Split` — confirm both files exist
   and Build Settings is correct. Open each scene in turn and re-run to scan
   for leftover wrong-category roots.
3. Ctrl+S in whichever scene you finish editing in.

### Runtime flow with `useSceneSplit = true`

```
MainMenuScene (active at boot)
   │
   ├── Online / Create Room → lobby UI shows
   ├── Pick colour + corner
   └── Host clicks Start Match
            │
            ├── Coordinator computes corner assignment + colours
            ├── Writes "match payload" into PhotonNetwork.CurrentRoom.CustomProperties
            ├── PhotonNetwork.AutomaticallySyncScene = true
            └── PhotonNetwork.LoadLevel("GameMapScene")
                       │
                       ├── All clients sync-load GameMapScene
                       └── Each client's new NetworkMatchCoordinator.Start
                            ├── reads match payload from room properties
                            └── ApplyMatchStartLocally(...) → reveal-only-assigned corners
                                                              → set colours / banks
                                                              → fire OnMatchStarted
```

Leaving a room (lobby Leave button, ESC menu Main Menu, kicked, disconnect)
fires `NetworkManagerRTS.OnLeftRoom` which — when `useSceneSplit` is true —
`SceneManager.LoadScene("MainMenuScene")`s you back. The existing
`MatchSessionManager.CleanupPreviousMatch` runs as before so MatchId / colour
slots / banks reset cleanly.

### Per-scene cleanup tools

If you ever drag a gameplay root into MainMenuScene by accident, or a lobby
canvas into GameMapScene, the cleanup tools strip it:

- `Tools → RTS → Scenes → Cleanup MainMenuScene` — operates on the OPEN
  scene; removes any gameplay-world root listed above.
- `Tools → RTS → Scenes → Cleanup GameMapScene` — operates on the OPEN
  scene; removes any menu root.

Both warn if the open scene's name doesn't match what they expect, then
strip anyway (the strip list is name-based, so it's safe to run on either
scene if you know what you're doing).

### Direct-Play safety

If you open `GameMapScene` in the Editor and press Play *without* going
through the lobby, the new coordinator's `Start` reads the room properties
and finds no match payload. It logs:

```
[MultiplayerMatch] Scene-split: no match payload in room properties.
   Either you opened GameMapScene directly (dev mode) or you joined a
   room with no pending match. The lobby remains active.
```

…and nothing reveals. The world stays hidden behind `GameplayWorldRoot`,
exactly as before. No crashes. To actually test gameplay, go through
MainMenuScene → Create Room → Start Match.

### "GameMapScene is missing the actual map" — re-sync from SampleScene

`Create Scene Split` does the split once. If you keep editing `SampleScene`
afterwards (re-baking CornerBases, dropping new map decorations, adding
managers), `GameMapScene` falls behind. Re-sync without touching the
already-working `MainMenuScene`:

1. Open and save `SampleScene` so your latest edits are on disk.
2. `Tools → RTS → Scenes → Rebuild GameMapScene From SampleScene`.
   - Backs out of any unsaved scene first (Save / Cancel / Discard prompt).
   - Deletes the old `GameMapScene.unity`.
   - Copies `SampleScene.unity` → `GameMapScene.unity`.
   - Opens the new copy and strips ONLY the menu/lobby roots
     (`LobbyCanvas`, `MainMenuCanvas`, `OptionsCanvas`,
     `MultiplayerDebugCanvas`). Everything else from SampleScene is kept:
     Environment, GameplayWorldRoot, CornerBase_A/B/C/D, ResourceNodes,
     HUDCanvas, EscapeMenuCanvas, SelectionCanvas, MinimapCameraGO,
     MatchManager, NetworkManager, AudioManager, EventSystem, etc.
   - Sets `NetworkManagerRTS.useSceneSplit = true` so match start uses
     `PhotonNetwork.LoadLevel`.
   - Saves `GameMapScene`.
3. `Tools → RTS → Scenes → Validate GameMapScene Content` — open
   GameMapScene first, then run. Audits:
   - `GameplayWorldRoot`, `HUDCanvas`, `SelectionCanvas`, `EscapeMenuCanvas`,
     `CameraRig`, `MinimapCameraGO`, `MatchManager`, `Environment` all
     present as root GameObjects.
   - **Exactly 4 `CornerBase` components** with unique `cornerIndex 0..3`.
   - Every `CornerBase` has non-null `dozer`, `bank`, and `resourceCluster`
     (and the cluster contains at least one `ResourceNode`).
   - Total `ResourceNode` count > 0.
   - `NetworkManagerRTS` exists and `useSceneSplit == true`.
   - No forbidden menu roots leaked in.
4. `Tools → RTS → Scenes → Validate Multiplayer Scene Setup` — Build
   Settings order check.
5. `Tools → RTS → Scenes → Open MainMenuScene` → press Play → Create Room →
   Start Match. Console should show
   `[GameMapScene] Activated CornerBase A (slot 0, …)` etc. and the actual
   map appears.

Symmetric tool for the menu: `Tools → RTS → Scenes → Rebuild MainMenuScene
From SampleScene` — same idea, strips gameplay roots instead.

### Multiplayer Debug Canvas

The `MultiplayerDebugCanvas` (top-right Connect / Create Room / Join Random
/ Leave Room overlay) is a dev artifact, not part of the player flow:

- The Scene Split tool now strips it from **both** new scenes.
- For the legacy `SampleScene` (or anywhere you still have it):
  - `Tools → RTS → UI → Toggle Multiplayer Debug Canvas` — show/hide without
    destroying.
  - `Tools → RTS → UI → Remove Multiplayer Debug Canvas` — destroy (with a
    confirm dialog). Re-add later via
    `Tools → RTS → Multiplayer → Setup Multiplayer Debug UI` if needed.

### "Create button in the form does nothing" / Back buttons don't work

The previous fix only force-wired the top-level OnlineMenuPanel buttons.
The runtime self-heal now ALSO covers every child panel by NAME inside
LobbyCanvas, so all of these get a working OnClick listener + ClickProbe
on every Play:

| Panel | Buttons re-wired |
|---|---|
| OnlineMenuPanel | Connect, CreateRoom, JoinRandom, BrowseRooms, Back |
| CreateRoomPanel | **CreateButton (submit), BackButton, StartingResourcesButton** |
| RoomListPanel   | **RefreshButton, BackButton** |
| LobbyPanel      | **StartMatchButton, LeaveButton** |

The CreateRoomPanel also re-resolves `RoomNameInput` (`TMP_InputField`),
`MapLabel`, and the StartingResources button's text label, so the form is
fully usable even if all those refs were null after a cleanup pass.

### Logs to look for when you click Create inside the form

```
[ClickProbe] PointerDown on CreateRoomSubmit
[ClickProbe] PointerClick on CreateRoomSubmit
[OnlineUI] CreateRoomSubmit clicked: room='Test123', resources=10000, connected=True, inLobby=True.
[NetworkRTS] CreateRoom(name='Test123', mapId='DefaultMap', startingResources=10000) — returned True.
[RoomRules] Created room with MaxPlayers=4, startingResources=10000
[NetworkRTS] Joined room 'Test123' as actor #1 (playerId=0). Players in room: 1/4
[Lobby] Room joined — switching to LobbyPanel.
```

If you click Create and see no `[OnlineUI] CreateRoomSubmit clicked` line,
the wiring is broken — press **F7** while hovering the button to see what
UI is actually under your cursor (the raycast probe will name the
intercepting GameObject).

### Back button logs

Each Back has a unique log so you know which one fired:

```
[OnlineUI] Back clicked.                           ← OnlineMenuPanel → MainMenuCanvas
[OnlineUI] Back clicked from CreateRoomPanel.      ← CreateRoomPanel → OnlineMenuPanel
[OnlineUI] Back clicked from RoomListPanel.        ← RoomListPanel   → OnlineMenuPanel
[OnlineUI] Leave Lobby clicked.                    ← LobbyPanel      → LeaveRoom
```

If a Back doesn't fire its log, the panel button isn't reachable —
re-enter Play (the Start self-heal runs again and re-wires fresh).

### "[LobbyUI] Duplicate MultiplayerLobbyUI destroyed" and clicks still don't fire

This is a specific case of the button-wiring problem: the scene has TWO
`MultiplayerLobbyUI` instances (typically because a setup tool ran twice
against an inactive `GameManager`, or a scene-split duplicate slipped
through). The original Awake guard kept whichever woke up first — which
might NOT be the one whose serialized refs point at the visible
LobbyCanvas. So the survivor's `WireButtons` adds listeners to the wrong
canvas; the user clicks the visible canvas and nothing fires.

This is now fixed in three layers; the diagnostic log lines walk you
through what happened on this Play:

1. **Smarter Awake.** When a duplicate is detected, the new logic picks the
   instance whose `canvasRoot` lives in the **active scene** (not a stale
   one or one parked in DontDestroyOnLoad). Console line:
   `[LobbyUI] Replacing previous Instance (this='X' in scene 'MainMenuScene' wins).`
   or `[LobbyUI] Duplicate MultiplayerLobbyUI destroyed on 'X' (scene=..., persistent=...); keeping winner on 'Y'.`

2. **Start-time self-heal.** In `MultiplayerLobbyUI.Start` (which runs after
   every Awake), the surviving instance:
   - Finds the visible `LobbyCanvas` in the active scene (prefers the
     most-populated one).
   - Re-points `canvasRoot` at it (logs if changed).
   - Finds `OnlineMenuPanel`, then `ConnectButton`, `CreateRoomButton`,
     `JoinRandomButton`, `BrowseRoomsButton`, `BackButton` BY NAME.
   - **Removes all listeners** (nuking any stuck listener on a destroyed
     instance) and `AddListener`s the correct handler. Logs one line per
     button: `[OnlineUI] Runtime wired Connect button.`, etc.
   - Adds an `OnlineButtonClickProbe` component to each button.

3. **Click probe.** Each Online button now has a `OnlineButtonClickProbe`
   that implements `IPointerDownHandler` + `IPointerClickHandler`. When you
   click in Play:
   ```
   [ClickProbe] PointerDown on Connect
   [ClickProbe] PointerClick on Connect
   [OnlineUI] Connect clicked.
   ```
   - **All three lines** → wiring is healthy.
   - **PointerDown but no `[OnlineUI]`** → click reaches the UI but the
     OnClick listener is broken (rerun the self-heal: exit Play, enter Play).
   - **No lines at all** → click never reached the UI. Use the next probe:

4. **F7 raycast probe.** With Play running and the Online panel open, press
   **F7**. The lobby controller logs the top 5 UI objects hit by an
   `EventSystem.RaycastAll` under the cursor:
   ```
   [RaycastProbe] 3 hit(s) under cursor (top 3):
   [RaycastProbe]   [0] 'ConnectButton' (depth=4, sortingOrder=1020, distance=0.0)
   [RaycastProbe]   [1] 'OnlineMenuPanel' (depth=3, sortingOrder=1020, distance=0.0)
   [RaycastProbe]   [2] 'LobbyCanvas' (depth=0, sortingOrder=1020, distance=0.0)
   ```
   - If the top hit is `ConnectButton` (or your button name) → raycast is
     fine; the issue was OnClick wiring (see step 2).
   - If the top hit is `ScreenDim` or some panel that ISN'T the button →
     that overlay is blocking. Set its `Image.raycastTarget = false`
     (Repair Main Menu Button Wiring does this automatically for
     ScreenDim).
   - If `No UI hit under cursor` or `No active EventSystem` → the
     EventSystem / GraphicRaycaster is the culprit. Run **Repair Main Menu
     Button Wiring**.

5. **Live instance census.** Start also prints:
   ```
   [OnlineUI] Live MultiplayerLobbyUI instance count: 1. Instance is on 'GameManager'.
   ```
   If `count > 1`, every duplicate is logged with its scene + persistence
   state so you can identify and remove the stragglers (e.g. run
   `Tools → RTS → UI → Cleanup Duplicate Runtime UI`).

### If the Online buttons (Connect / Create Room / Join Random / Browse / Back) don't react

The buttons live in `LobbyCanvas → OnlineMenuPanel`. Three things can stop
them from firing — diagnose in this order with the new logs:

1. **Press Play, click a button, watch the Console.** Each click now logs
   `[OnlineUI] Connect clicked` / `[OnlineUI] Create Room clicked` etc.
   - If you DO see the log, the click is reaching the OnClick method — any
     subsequent failure is in the controller/network logic, not the wiring.
   - If you DON'T see the log, the click never reaches the method. That's a
     wiring issue → run the repair tool below.

2. **`Tools → RTS → UI → Validate Main Menu Buttons`** — read-only audit:
   - EventSystem count + input module.
   - LobbyCanvas + GraphicRaycaster.
   - ScreenDim Image.raycastTarget (a full-screen blocker if accidentally true).
   - Every panel + button reference on `MultiplayerLobbyUI`.
   - Each button's `interactable` flag.
   - `NetworkManagerRTS` presence.
   Each line is `✓` or `✗`. Summary tells you how many issues to fix.

3. **`Tools → RTS → UI → Repair Main Menu Button Wiring`** — same checks,
   but actively fixes:
   - Creates a missing EventSystem (with StandaloneInputModule).
   - Destroys duplicate EventSystems (keeps the first).
   - Adds the missing GraphicRaycaster to LobbyCanvas.
   - Forces `ScreenDim.raycastTarget = false` if it was blocking.
   - Forces any blocking `CanvasGroup` to `interactable + blocksRaycasts = true`.
   - Re-resolves every panel + button reference on `MultiplayerLobbyUI`
     by NAME within the LobbyCanvas subtree (inactive-inclusive lookup):
     `OnlineMenuPanel`, `CreateRoomPanel`, `RoomListPanel`, `LobbyPanel`,
     `ConnectButton`, `CreateRoomButton`, `JoinRandomButton`,
     `BrowseRoomsButton`, `BackButton`, `StatusLabel`, `MainMenuCanvas`.
   - Forces `Button.interactable = true` on every Online button.
   - Marks the scene dirty (Ctrl+S to save).

   The repair never destroys the canvas (so existing positions / colour
   pickers / lobby slot wiring survive). On the next Play, `Awake →
   WireButtons` finds the freshly-attached refs and `AddListener`s the
   handlers — so the buttons start working.

Run order if you're stuck:

```
1. Tools → RTS → UI → Validate Main Menu Buttons    (see the issues)
2. Tools → RTS → UI → Repair Main Menu Button Wiring (fix them)
3. Ctrl+S
4. Press Play → click an Online button → look for [OnlineUI] X clicked.
```

### If Create Room doesn't do anything

The Online menu's Create Room button now:

- Auto-starts the Photon Connect so you don't have to click Connect first.
- Self-heals the `CreateRoomPanel` reference if a cleanup pass invalidated it.
- Falls back to a direct `NetworkManagerRTS.CreateRoom(default name, default
  resources)` if the form panel still can't be resolved, and logs the cause
  at ERROR level — so the click never silently no-ops.

Same hardening applied to Join Random and Browse Rooms. If you still see no
effect, search the Console for `[Lobby]` lines from the click — the path
that ran is always logged.

### Rolling back

`Assets/Scenes/SampleScene_Backup.unity` is the original SampleScene at the
time you first ran the splitter. To revert:

1. Delete `MainMenuScene.unity` and `GameMapScene.unity`.
2. Rename `SampleScene_Backup.unity` back to `SampleScene.unity`.
3. Open it. Set `NetworkManagerRTS.useSceneSplit = false` in the Inspector.
4. File → Build Settings: remove the split scenes, add SampleScene at index 0.

---

## Scene & UI hygiene tools

Three tools keep the scene tidy. None of them touch prefab assets on disk —
they only operate on root GameObjects in the OPEN scene.

### Tools → RTS → UI → Validate UI Duplicates

Counts every "single-instance" UI root and manager in the scene
(LobbyCanvas, EscapeMenuCanvas, OptionsCanvas, MainMenuCanvas, HUDCanvas,
MultiplayerDebugCanvas, SelectionCanvas, GameManager, EventSystem, etc.)
and the matching controller components (`MultiplayerLobbyUI`,
`EscapeMenuController`, `NetworkManagerRTS`, `AudioManager`, …). Prints
`✓ Name x1` for healthy entries and `✗ Name xN — DUPLICATES` for anything
that needs cleanup.

### Tools → RTS → UI → Cleanup Duplicate Runtime UI

Safely removes the extras. For each duplicated root it picks ONE to keep —
preferring the GameObject that a controller actually references (e.g. the
`LobbyCanvas` the `MultiplayerLobbyUI.canvasRoot` points at), otherwise the
active one — and `DestroyImmediate`s the rest with a log line per removal.
Marks the scene dirty. Re-run `Validate UI Duplicates` afterwards; you
should see only `✓` lines.

**Why duplicates happen.** The lobby + escape menu setup tools used to save
their canvas INACTIVE at the end of `Run()`. On a re-run,
`GameObject.Find("LobbyCanvas")` only finds ACTIVE objects, so the old
canvas was invisible to the destroy step — the tool created a fresh one
beside it. After N re-runs the Hierarchy had N stacked LobbyCanvases. Both
builders are now fixed at the source (`FindObjectsByType(Include)`), but
scenes with pre-existing duplicates need this one-time cleanup pass.

### Tools → RTS → Scenes → Validate Multiplayer Scene Setup

Read-only audit of how the scene is wired for multiplayer:

- Lists every entry in **Build Settings** and identifies the first (boot) scene.
- Logs `PhotonNetwork.AutomaticallySyncScene` (this project keeps it `false`
  and reveals the world via `GameplayWorldRoot` instead of a real scene load).
- Confirms every single-instance manager is present exactly once
  (`NetworkManagerRTS`, `NetworkMatchCoordinator`, `MultiplayerMatchStarter`,
  `GameplayWorldRoot`, `MultiplayerLobbyUI`, `MainMenuController`,
  `EscapeMenuController`, `AudioManager`, `EventSystem`).

Use it after the cleanup pass to confirm the scene is healthy before testing.

### Match-start per-slot summary (runtime)

`NetworkMatchCoordinator.ApplyMatchStartLocally` now prints a per-slot audit
to the Console immediately after the match starts. Example for a 4-player
start:

```
[MultiplayerMatch] ─── Match-start summary (4 player(s)) ───
[MultiplayerMatch]   slot 0 actor #1 → corner A (index 0): cb=True dozer=True bank=True resNodes=4 resources=10000
[MultiplayerMatch]   slot 1 actor #2 → corner B (index 1): cb=True dozer=True bank=True resNodes=4 resources=10000
[MultiplayerMatch]   slot 2 actor #3 → corner C (index 2): cb=True dozer=True bank=True resNodes=4 resources=10000
[MultiplayerMatch]   slot 3 actor #4 → corner D (index 3): cb=True dozer=True bank=True resNodes=4 resources=10000
[MultiplayerMatch] ─── End summary ───
```

Anything missing (no CornerBase for the index, dozer reference null, bank
not active, zero resource nodes) is logged at **ERROR** level with the
exact slot + corner — so the 4-player bug ("the 4th player sometimes has
no resources / no spawn") is immediately diagnosable from one Console
glance, without breakpoints. If you see a row with `cb=False` or
`dozer=False` for slot 3, re-run **Validate 4 Player Map Setup**: corner D
likely isn't fully configured.

---

## Recipes

### Clean up a scene with stacked LobbyCanvases (one-time)

If your Hierarchy already shows many `LobbyCanvas` (or `EscapeMenuCanvas`)
entries — like the screenshot many users hit after re-running the All-In-One
tool multiple times — do this once:

1. `Tools → RTS → UI → Validate UI Duplicates`. Note the `✗ x N` lines.
2. `Tools → RTS → UI → Cleanup Duplicate Runtime UI`. Console will list
   every removal: `[CleanupUI]   Removed duplicate 'LobbyCanvas' (instanceId …)`.
3. `Tools → RTS → UI → Validate UI Duplicates` again — confirm every row
   is `✓ Name x1`.
4. `Tools → RTS → Scenes → Validate Multiplayer Scene Setup` — confirm
   `✓ Scene setup OK`.
5. Ctrl+S.

Future re-runs of `Setup Multiplayer Lobby UI` / `Setup Escape Menu` are now
idempotent at the source, so duplicates won't recur.

### 1-player sandbox match

Bootstrap on the Editor's `GameManager`:

```
playerName       = Solo
colorName        = blue
roomName         = SoloRoom
startSlot        = 0
autoCreateRoom   = true
autoStart        = true
autoStartDelaySec= 0.5
```

Press Play. The Editor connects, creates a 1/4 room, picks corner A and starts.
Only Corner A reveals; the other three corners stay hidden. One bulldozer, one
resource ring, one bank. Confirms the reveal-only-assigned path works.

### 2-player match (Editor host + 1 standalone client)

Editor `GameManager` Inspector:

```
playerName=Alpha  colorName=blue   roomName=DevRoom  startSlot=0
autoCreateRoom=true  autoStart=true
```

Standalone:

```
RtsGame.exe -playerName Bravo -color red -room DevRoom -startSlot B -autoJoinRoom
```

### 3-player match (MPPM)

Main + 2 virtual players, tags as in **Workflow A**, host has `autoCreateRoom`
+ `autoStart`. The fourth corner stays empty.

### 4-player match (MPPM)

Same as above, with 3 virtual players enabled. All four corners come alive.

### 1v1 quick combat test (one window, Dev Commander)

Use this to validate the combat loop end-to-end without launching a second
client.

1. Start a 2-player match using Workflow A (MPPM, main editor + 1 virtual
   player) so two players are actually in the room.
2. As the host, build a Vehicle Factory with your Dozer (Player 1) and let the
   virtual player do the same (Player 2).
3. Press **F12** in the main Editor to open the Dev Commander panel.
4. On the **Player 2** row click `+1000 Res` until they can build, then
   `Spawn Tank` a couple of times for Player 2.
5. On the **Player 1** row click `Spawn Tank` once or twice.
6. Click **Select** on Player 1's row, then click **Order attack on Player 2**.
7. Watch combat. Confirm:
   - Both Editor and virtual player render damage simultaneously.
   - Health bars decrement in lockstep.
   - When a tank dies it disappears on both clients on the same frame.

Console signature on the host:

```
[DevCommander] Requested AttackPlayer for playerId=0 arg=1
[DevCommander] Issuing Attack: 2 unit(s) of player 0 → entity 'scene/…' (owner 1).
[CommandDispatcher] Issue Cmd#… p0 Attack (2 src) → '…'
[NetDamage] Apply target health 1000 -> 925 (attacker …)
```

### Resource sync test

Use this to verify `ResourceBank` broadcasts cleanly across clients.

1. In a multi-player room with at least two players, press **F12** on the host
   to open the panel.
2. Note each player's `resources = X` line.
3. Click `+1000 Res` on **Player 2**'s row five times.
4. Confirm on **Player 2**'s client (other Editor / virtual player / build):
   - Their HUD resource counter ticks up by 5000.
   - The Dev Commander panel on their side (if open) shows the same total.
5. Verify the host's panel and Player 2's panel agree.

Failure modes worth catching:

- One client increments but the other does not → broadcast gating may be off.
- Increment doubles on the host → an event handler is running on both the
  master apply AND the receive path. Check `IsMasterClient` guards.

### Unit spawn sync test

Use this to confirm produced units carry the same `EntityId` on every client.

1. Both players should have a Barracks built (or a Vehicle Factory).
2. Open the Dev Commander panel on the host. Select **Player 2**.
3. Click `Spawn Inf` (or `Spawn Tank` if VF is built) three times.
4. Confirm on Player 2's client that three new units appear under their owner
   color, at the same world positions as on the host.
5. Compare in the Console — search both clients for `[Produce] Spawned …
   id=<spawnId>`. The `id=` value must match across clients for each spawn.
6. Select a freshly-spawned unit on Player 2's client and right-click an empty
   floor tile. Verify the unit moves on both clients (proves the
   `EntityRegistry` resolved the same id on both, so subsequent commands work).

If a spawn appears on one client but not the other, the most common cause is
the producer not being registered with `GameEntity` on the other client — run
`Tools → RTS → Multiplayer Prep → Add GameEntity To Scene Objects`.

### Attack order sync test

Use this to verify attack commands replicate via `NetworkCommandRelay`.

1. With both players having at least one combat unit, open the Dev Commander
   panel on the host.
2. Click **Select** on **Player 1**'s row.
3. Click the victim cycle button until it reads `Player 2`, then click
   **Order attack on Player 2**.
4. Confirm on Player 2's client:
   - Player 1's units physically turn and approach a Player-2 entity.
   - When in range, damage applies and the existing tracer / explosion VFX
     play on both clients.
5. Optionally test the reverse: **Select Player 2**, victim **Player 1**,
   `Order attack on Player 1`. The host should see Player 2's units attack
   even though the **host did not own them** — the host's request was just
   relayed; Player 2's client issued the actual command.

Expected Console sequence on the host:

```
[DevCommander] Requested AttackPlayer for playerId=1 arg=0
[DevCommander] Command sent through network path (Photon RaiseEvent).
```

…and on Player 2's client (LocalPlayerId == 1):

```
[DevCommander] Issuing Attack: 1 unit(s) of player 1 → entity '…' (owner 0).
[CommandDispatcher] Issue Cmd#… p1 Attack (1 src) → '…'
```

---

## What you should see in the console

The expected log sequence on a host starting a 1-player match:

```
[DevBootstrap] autoConnect → enabling multiplayerMode + Connect().
[NetworkRTS] Connecting to Photon (settings) — ConnectUsingSettings returned True.
[NetworkRTS] Connected to Photon master server.
[NetworkRTS] CreateRoom(name='DevRoom', mapId='DefaultMap', startingResources=10000) — returned True.
[RoomRules] Created room with MaxPlayers=4, startingResources=10000
[NetworkRTS] Joined room 'DevRoom' as actor #1 (playerId=0). Players in room: 1/4
[DevBootstrap] applying startSlot = 0 (A).
[StartSlot] Local player chose corner 0 (A).
[DevBootstrap] autoStart → RequestMatchStart.
[MultiplayerMatch] Start allowed because currentPlayers (1) >= 1.
[MultiplayerMatch] Starting match with 1 player(s). startingResources=10000.
[MultiplayerMatch] Final corner assignment: slot 0 actor #1 → corner A (index 0).
[MultiplayerMatch] Spawning player slot 0 at corner A (index 0).
[MultiplayerMatch] Skipped empty corner B (index 1).
[MultiplayerMatch] Skipped empty corner C (index 2).
[MultiplayerMatch] Skipped empty corner D (index 3).
[GameplayWorldRoot] Activated 2 gameplay root(s) after MatchStart.
[Resources] Player 0 starting resources set to 10000.
```

---

## Troubleshooting

- **Lobby still says `1 / 2`.** The `NetworkManagerRTS.maxPlayersPerRoom`
  serialized value was 2. The Awake self-heal forces it to 4 at runtime and
  `DoCreateRoom` always uses `MaxPlayersSupported (4)`, but you must **create a
  fresh room** — rejoining a stale room that was created at 2 will still show
  2/4. Quit, reconnect, create a new room.

- **All 4 corner bases appear for a 1-player match.** Either the
  `NetworkMatchCoordinator` isn't in the scene (re-run the all-in-one setup),
  or the corners weren't baked by `Setup Multiplayer Match Map` — run
  `Validate 4 Player Map Setup` to confirm.

- **`autoStart` fires before the second client has joined.** Raise
  `autoStartDelaySec` on the host (default 3 s).

- **Standalone build can't see the Editor session in the room list.** The
  Photon `GameVersion` must match. It's set from `NetworkManagerRTS
  .photonAppVersion` (default `"0.1"`) — make sure both the Editor and the
  build are on the same value.

- **MPPM tags aren't being read.** The bootstrap uses reflection. Open the
  MPPM window, expand the virtual player and confirm Tags are saved. If you
  installed MPPM after the scene was loaded, exit Play mode, re-enter Play
  mode so the bootstrap re-runs.

- **No console output starting with `[DevBootstrap]`.** Either the component
  isn't on `GameManager` (run **Tools → RTS → Multiplayer → Setup Dev
  Bootstrap**) or `verboseLogs` is off in the Inspector.

- **F12 does nothing — Dev Commander panel won't open.** Either the component
  isn't on `GameManager` (run **Tools → RTS → Multiplayer → Setup Dev
  Commander**) or you're in a release build with `devMode` off — the panel
  is only shown when `Debug.isDebugBuild` is true OR `devMode == true`.

- **`Spawn Inf` / `Spawn Tank` / `Spawn Aircraft` does nothing.** The target
  player has no matching producer yet. The Dev Commander does not bypass the
  production system — build a Barracks / Vehicle Factory / Airfield first
  (use the existing Dozer build menu, or click `+1000 Res` to afford it).

- **A Dev Commander action fires twice.** Most likely you opened the panel on
  both clients and clicked the same row on both. Master / target gating
  guarantees a single applier, but each click sends its own event. Use it on
  one client.
