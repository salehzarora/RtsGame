# Match Session Isolation

Guarantees that **no data from one room/match can leak into another**. Fixes the
contamination bug where a client that returns to the menu (without restarting the
process) and joins a new room still shows the previous match's resources / UI /
units / state.

---

## Root cause

The game is single-scene (menu/lobby/HUD are canvases, not separate scenes), so
manager singletons and static caches persist across the menu round-trip. The
client that **fully restarts** the process (Player 1) always starts clean; the
client that only **returns to the menu** (Player 2) kept stale state because:

1. The back-to-menu flow tore down UI **synchronously before `OnLeftRoom`** and
   never reset Photon player properties or resources.
2. There was **no per-room MatchId**, so nothing scoped gameplay state/events to
   a specific room.

---

## The system

### `MatchSessionManager` (new, static) — `Scripts/Core/Network/MatchSessionManager.cs`
The single owner of one online match session. Static, so it can never be
duplicated (satisfies "prevent duplicate managers") and is reachable from the
static event handlers.

- **`CurrentMatchId`** — the room's unique id (empty = no active match).
- **`StartNewMatchSession(matchId)`** — cleans up any previous session, then
  stamps the new id.
- **`CleanupPreviousMatch()`** — idempotent master teardown. Calls, in order:
  `ResetSpawnedUnitsAndBuildings()` · `ResetResources()` · `ResetSelectionState()`
  · `ResetUIState()` · `ResetLocalRuntimeState()` · `ResetPhotonPlayerProperties()`,
  then clears `CurrentMatchId`. Re-entrancy guarded.
- **`Raise(code, payload, receivers, send)`** — sends a gameplay event with
  `CurrentMatchId` appended as the final payload element.
- **`AcceptEvent(ev, context)`** — returns false (and logs) for events whose
  trailing MatchId tag != `CurrentMatchId`. Untagged events are accepted
  (backward compatible).
- **`[RuntimeInitializeOnLoadMethod]` Bootstrap** — at game boot clears all
  runtime match state (so "close + reopen" starts empty) and subscribes to
  `NetworkManagerRTS.OnRoomLeftEvent` so leaving a room always cleans up.

It **integrates with the existing reset systems** rather than duplicating them:
`MatchSessionResetter` (entity teardown + color slots + id allocator +
coordinator flags), `PowerManager.ResetForNewMatch()` (new), `ResourceBank` /
`PlayerResourceManager.SetResourcesLocal()` (new, non-broadcasting).

### MatchId per room
- **Create** (`NetworkManagerRTS.DoCreateRoom`) — a `Guid` is generated and
  stored in the room's custom properties under `"matchId"`.
- **Join** (`NetworkManagerRTS.OnJoinedRoom`) — reads `"matchId"` from the room
  properties (falls back to `"room-"+RoomName`, identical on both clients) and
  calls `StartNewMatchSession`.

### Event tagging + filtering (defense-in-depth)
Every gameplay RaiseEvent now flows through `MatchSessionManager.Raise` and is
tagged with the MatchId; every bus drops mismatched events via `AcceptEvent`:
- `NetworkCommandRelay` (PlayerCommand, code 1)
- `NetworkMatchEvents` (damage/death/resources/transform/etc., codes 3–13)
- `MapInteractableNetworkEvents` (garrison/tunnel, codes 20–22)

`MatchStart` (code 2) is intentionally **not** tagged — it establishes the
session.

### Leave / disconnect / boot
- **ESC → Main Menu** (`EscapeMenuController.OnClickMainMenu`) — immediate local
  UI teardown, `LeaveRoom()`, and the main menu is shown only after
  `OnLeftRoom` (with a 2.5 s timeout fallback). Full cleanup runs on
  `OnLeftRoom`.
- **Lobby → Leave Room / Back to Main Menu** — route through cleanup.
- **`OnDisconnected`** — calls `CleanupPreviousMatch()`.
- **Before joining** — `CreateRoom` / `JoinRoomByName` / `JoinRandomRoom` call
  `CleanupPreviousMatch()` first.

### Resources (the "missing/old resources" symptom)
The HUD polls `ResourceBank.For(LocalPlayerId)` each frame. `ResetResources()`
re-seeds every bank to its configured seed using the new **non-broadcasting**
`SetResourcesLocal` (so a leaving client never resets the *staying* player's
bank); the authoritative per-match value is then re-applied by `MatchStart`.

### Photon player properties
`ResetPhotonPlayerProperties()` resets the match-state keys (`matchId`, `team`,
`slot`, `ready`, `army`, `money`, `resources`) to neutral defaults on
leave/disconnect/before-join. The chosen **army colour is intentionally
preserved** — it's a persistent preference, not stale match state, and MatchStart
re-broadcasts the authoritative per-slot colour anyway.

---

## Files changed
| File | Change |
|---|---|
| `Core/Network/MatchSessionManager.cs` | **NEW** — the whole system. |
| `Core/Network/NetworkManagerRTS.cs` | MatchId room prop on create; read on join → `StartNewMatchSession`; `OnDisconnected` cleanup; before-join cleanup. |
| `Core/Network/NetworkMatchEvents.cs` | 11 sends routed through `Raise`; `AcceptEvent` guard in `OnEvent`. |
| `Core/Network/NetworkCommandRelay.cs` | Send tagged via `Raise`; `AcceptEvent` guard on receive. |
| `Map/MapInteractableNetworkEvents.cs` | 3 sends routed through `Raise`; `AcceptEvent` guard in `OnEvent`. |
| `Buildings/PowerManager.cs` | New `ResetForNewMatch()`. |
| `Resources/PlayerResourceManager.cs` | New non-broadcasting `SetResourcesLocal()`. |
| `UI/EscapeMenuController.cs` | Defer menu to `OnLeftRoom`; route through `CleanupPreviousMatch`. |
| `UI/MultiplayerLobbyUI.cs` | Back-to-menu routes through `CleanupPreviousMatch`. |

No gameplay systems were removed; existing reset code (`MatchSessionResetter`,
coordinator reset) is reused, not replaced.

---

## Diagnostic logs
Search the Console for `[MatchSession]`:
- `Boot — runtime match data starts empty`
- `New MatchId created for room: '<guid>'`
- `Joined room MatchId '<id>'`
- `Started new match session — MatchId '<id>'`
- `CleanupPreviousMatch called` / `... complete — MatchId cleared`
- `Spawned units/buildings reset` · `Resources reset on N bank(s)` ·
  `Selection state reset` · `UI state reset` · `Local runtime state reset` ·
  `Photon player properties reset (color preserved)`
- `Ignored <bus> event (code N) — event MatchId '...' != current '...'`
- `OnLeftRoom received — running cleanup` / `OnLeftRoom cleanup completed`
- `OnDisconnected cleanup completed`
- (`[Power] Reset for new match …`)

---

## How to test

### Test 1 — the reported bug (close + return-to-menu + rejoin)
1. **Player A** (host) creates a room; **Player B** joins; A starts the match.
   - Console (both): `New MatchId created …` (A), `Joined room MatchId 'X'` (both).
2. Both play a bit (gather/spend so resources differ from the start value).
3. **Player A fully closes the game** and reopens it.
4. **Player B** opens the ESC menu → **Main Menu**.
   - B Console: `Leaving Photon room …`, then `OnLeftRoom received — running
     cleanup`, `CleanupPreviousMatch …`, `Resources reset …`, `OnLeftRoom
     cleanup completed`, then `OnLeftRoom received — showing main menu`.
5. Both reconnect and **create/join a NEW room**; A starts the match.
   - Console: a **different** `New MatchId created …` and matching
     `Joined room MatchId 'Y'` on both.
6. **Verify on Player B:** the resource HUD shows the **new match's** starting
   value (not the old total), no leftover units/buildings from match 1, power
   readout correct, selection empty.
7. **Verify on Player A:** normal fresh match (it always was — full restart).

### Test 2 — multiple simultaneous rooms don't share data
1. Open **4 clients**. A+B create/join **Room 1**; C+D create/join **Room 2**.
2. Confirm Room 1 and Room 2 have **different** `New MatchId created` GUIDs.
3. Play in both. Gather resources in Room 1; build units in Room 2.
4. **Verify** neither room's resources, units, or events appear in the other.
   If any cross-room event ever arrived it would be logged as
   `Ignored … event … MatchId mismatch` and dropped.

### Test 3 — disconnect cleanup
1. In a match, kill Player B's network (or stop Photon).
2. B Console: `Disconnected from Photon …`, `OnDisconnected cleanup completed`.
3. Reconnect B and join a new room → fresh state, new MatchId.

### Test 4 — single-player regression
1. From the menu, start **Single Player**. Confirm resources/units/power behave
   exactly as before (SP raises no networked events; `CurrentMatchId` stays empty
   and `AcceptEvent` accepts everything).
2. Return to menu via ESC → Main Menu, start SP again → clean fresh match.

---

## Known limitations / notes
- A **transient** Photon disconnect is treated as match-over (cleanup runs), per
  the requirement. If you later want auto-reconnect to resume a match, gate the
  `OnDisconnected` cleanup on the disconnect cause.
- Army **colour** is deliberately preserved across rooms (persistent preference).
  To wipe it too, clear `armyColor`/`armyColorName` in
  `ResetPhotonPlayerProperties` and re-push from `PlayerFactionManager` on join.
- MatchId tagging appends one trailing element to each gameplay payload; existing
  index-based handlers are unaffected (they read fixed indices; only
  `AcceptEvent` reads the tail).
- This is editor/runtime code; verify by entering Play mode (and a 2-instance
  build) and running the tests above.
```
