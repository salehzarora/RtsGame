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
