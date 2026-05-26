# Photon PUN 2 Setup — RTS Prototype

This document is the one-time manual setup needed to activate the optional
multiplayer command relay. Until you complete it, the Photon-aware scripts
(`NetworkManagerRTS`, `NetworkCommandRelay`) compile dormant; single-player
keeps working unchanged.

> **What this phase is**: a *command relay* foundation only. When two clients
> join the same Photon room and one issues a Move / Attack / Produce / Build /
> Unload command, the command travels to the other client and replays via
> `CommandDispatcher.IssueRemote`. **No unit transform sync, no deterministic
> spawning, no team enforcement yet.** Those land in later phases.

---

## 1. Install PUN 2 from the Asset Store

Photon PUN 2 is not on Unity's default Package Manager registry. You install
it once via the Asset Store:

1. In Unity Editor: **Window → Asset Store** (or open
   https://assetstore.unity.com/packages/tools/network/pun-2-free-119922 in a
   browser).
2. Click **Add to My Assets** (sign in with the Unity account that owns the
   project).
3. Back in Unity: **Window → Package Manager → Packages: My Assets**, find
   **PUN 2 - FREE** by Exit Games, click **Download** then **Import**.
4. In the import dialog, leave everything ticked and click **Import**.

Unity will recompile. The scripting define symbol `PHOTON_UNITY_NETWORKING`
gets added automatically — that's what wakes up `NetworkManagerRTS` and
`NetworkCommandRelay`.

**Verify the symbol is defined**: *Edit → Project Settings → Player →
Other Settings → Scripting Define Symbols* should contain
`PHOTON_UNITY_NETWORKING`. If not, add it manually and the scripts will
activate.

---

## 2. Get a free App ID from Photon

1. Register a free account at https://dashboard.photonengine.com/.
2. **Create a New App** → type: **Photon PUN**. Give it any name (e.g.
   "RtsGame-Dev").
3. Copy the **App ID** (a GUID-looking string).
4. In Unity: **Window → Photon Unity Networking → Highlight Server Settings**
   (or open `Assets/Photon/PhotonUnityNetworking/Resources/PhotonServerSettings.asset`).
5. Paste the App ID into **App Id PUN**. Leave the region empty (lets Photon
   pick the closest one automatically).
6. Save the scene.

The PUN free tier gives you 20 concurrent users — plenty for development.

---

## 3. Add the NetworkManager to your scene

One-click: **Tools → RTS → Multiplayer → Setup Network Manager**.

That creates a `NetworkManager` GameObject with two components:
- `NetworkManagerRTS` — Photon connect / lobby / room.
- `NetworkCommandRelay` — bridges `CommandDispatcher` ↔ Photon `RaiseEvent`.

Save the scene afterwards.

---

## 4. Enable multiplayer mode

On the `NetworkManager` GameObject, tick **`Multiplayer Mode`** in the
Inspector. Leave **`Auto Connect On Start`** off if you want to drive the
connection manually via the context menu (right-click the component header):

| Context menu | What it does |
|---|---|
| `Connect`            | Connects to the Photon master server. |
| `Create Room`        | Creates a new room (auto-named). |
| `Join Random Room`   | Joins any available room; if none exist, creates one. |
| `Leave Room`         | Disconnects from the current room. |

You can also call those methods from any script — e.g.
`NetworkManagerRTS.Instance.CreateRoom()`.

---

## 5. Two-instance local test

To verify the relay end-to-end without a second machine:

1. Build a Windows player from **File → Build Settings → Build** (see
   `BUILD_INSTRUCTIONS.md`). Output goes to e.g. `Builds/Win64-mp/`.
2. Launch the `.exe`. In its main menu, pick a color and press Play.
3. With the Editor STILL open on the same scene, press Play in the Editor too.
4. On both clients: tick `Multiplayer Mode` (Editor) and use the context menu
   to **Connect** → **Create Room** on one, **Connect** → **Join Random
   Room** on the other.

Logs to look for (in order):

**Client A** (room creator):
```
[NetworkRTS] Connecting to Photon (settings) — ConnectUsingSettings returned True.
[NetworkRTS] Connected to Photon master server.
[NetworkRTS] CreateRoom — returned True.
[NetworkRTS] Room created.
[NetworkRTS] Joined room 'XYZ' as actor #1 (playerId=0). Players in room: 1/2
[NetworkRTS] Joined room 'XYZ' as actor #1 (playerId=0). Players in room: 2/2   ← when B joins
```

**Client B** (joiner):
```
[NetworkRTS] Connected to Photon master server.
[NetworkRTS] JoinRandomRoom — returned True.
[NetworkRTS] Joined room 'XYZ' as actor #2 (playerId=1). Players in room: 2/2
```

Once both are in the room, on Client A select some units and right-click the
ground. Expect on **Client B**:
```
[NetworkCommandRelay] Received command Move (#3) from player 1.
[CommandDispatcher] Executing remote command #3 (Move) from player 0.
[CommandDispatcher] Issue Cmd#3 p0 Move (2 src) → (...)
```

…possibly followed by:
```
[NetworkCommandRelay] Remote command references unknown source entity id '...'.
Phase 1 limitation: ids are per-client and not yet deterministic.
```

That warning is **expected** in Phase 1 — both clients have their own scene
units with their own GUIDs, so the IDs don't match across clients yet. The
command transport itself is working; entity resolution comes in Phase 2.

---

## 6. Turning it off

* Untick `Multiplayer Mode` → the relay is dormant. `Connect` / `CreateRoom`
  context-menu calls become explicit no-ops.
* You can leave `NetworkManager` in the scene during single-player; it costs
  one MonoBehaviour Awake and nothing else.

---

## 7. Common errors

| Symptom | Likely cause | Fix |
|---|---|---|
| `Photon not installed — multiplayer disabled` at runtime | PUN 2 import didn't add the scripting define | Add `PHOTON_UNITY_NETWORKING` manually under Player Settings → Scripting Define Symbols. |
| `[NetworkRTS] CreateRoom failed (-3): RoomNameAlreadyExists` | Re-running CreateRoom with the same auto-name in fast succession | Wait a second, try again — Photon auto-names rooms; collisions are rare. |
| `App ID is empty` red banner in Editor | Step 2 not done | Paste your App ID into `PhotonServerSettings.asset`. |
| `OnDisconnected: ServerTimeout` | App ID set to the wrong product type (Voice / Fusion instead of PUN) | Create a fresh App ID with type "Photon PUN" on the dashboard. |
| Compile error `error CS0246: The type or namespace name 'Photon' could not be found` | PUN imported but Unity hasn't refreshed yet | Save scene, reimport `Assets/Photon/`, restart the Editor. |
| Both clients connect but commands don't cross | Different `Photon App Version` strings on the two clients | Make sure both clients use the same `photonAppVersion` value on `NetworkManagerRTS`. |

---

## 8. Next phase — what this doc does NOT cover

* **Deterministic spawning** so `GameEntity.EntityId`s match between clients.
* **Team / ownership enforcement** so Client B can't issue a command for
  Client A's CommandCenter (today the dispatcher will happily execute it).
* **Unit transform sync** — today each client replays the command and the
  resulting NavMeshAgent path is computed locally, so positions drift if
  pathfinding diverges.
* **Lobby UI** — Create Room / Join buttons in the actual menu. The current
  test path is context-menu only.

Those are Phase 2 / 3 work. Open a new task when ready.
