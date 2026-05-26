# Windows Build Instructions — RTS Prototype

This document explains how to build a playable **singleplayer Windows** executable
of the RTS Prototype from the Unity Editor.

The project is currently a **singleplayer** prototype (per `CLAUDE.md`). Photon
PUN is **not** installed; there is no multiplayer code path. Treat this build
as a local-only `.exe`.

---

## 0. Prerequisites

1. **Unity 6** (matches the version that ships with this project — open the
   project in the same version that produced the `Library/` folder to avoid
   asset re-imports).
2. **Windows Build Support** module installed for that Unity version.
   - Verify in Unity Hub → *Installs* → *⋯* → *Add Modules* → tick
     **Windows Build Support (IL2CPP)** (recommended) **and**
     **Windows Build Support (Mono)** if the IL2CPP one is missing.
3. **`Assets/Scenes/SampleScene.unity`** must be the entry scene (it already is
   in `ProjectSettings/EditorBuildSettings.asset`).

---

## 1. Open the project

1. Launch Unity Hub.
2. *Open* → select this repo's root folder.
3. Wait for the asset database to finish importing. The Console should
   report no compile errors. If you see red errors, **stop here** and fix
   them before building — Unity will refuse to build with compile errors.

---

## 2. Pre-build sanity check (inside the Editor)

1. Open `Assets/Scenes/SampleScene.unity`.
2. Run **Tools → RTS → Setup → Setup Gameplay HUD** (rebuilds the bottom
   command bar + minimap).
3. Run **Tools → RTS → Setup → Setup Main Menu** if the main menu canvas
   was ever removed/modified — restores the Play button wiring.
4. Press *Play* once in the Editor and verify:
   - Main menu shows on boot. Console logs `[MainMenu] Boot — main menu shown...`
   - Pick a color → Console logs `[MainMenu] Color selected: <name>.`
   - Press Play → Console logs `[MainMenu] Play pressed...`,
     `[GameState] Game started.`, and `[MainMenu] Match starting...`
   - Your starting CommandCenter / Workers appear in the team color you
     picked.

If any of those logs are missing, the Editor preview will fail the same way
the built `.exe` will — fix in the Editor first.

---

## 3. Configure Build Settings

1. *File → Build Settings…*
2. Confirm **Scenes In Build** has exactly one entry, enabled:
   - `Assets/Scenes/SampleScene.unity`
   If you ever see TextMesh Pro example scenes here, untick / remove them.
   The 33 TMP example scenes exist on disk (`Assets/TextMesh Pro/...`) but
   must **not** be in the build list — they would inflate the build with
   sample content.
3. **Platform**: select **Windows, Mac, Linux** → click *Switch Platform* if
   it isn't already active.
4. **Target Platform**: **Windows**.
5. **Architecture**: **Intel 64-bit** (x86_64) — the standard 64-bit Windows
   target.
6. **Server Build**: unchecked. **Development Build**: unchecked for the
   final ship build (check it temporarily while debugging — it preserves
   stack traces and enables the in-game profiler).

---

## 4. Player Settings (optional but recommended)

1. From the Build Settings window: *Player Settings…* (bottom-left).
2. Set **Company Name** and **Product Name** — these end up in the `.exe`
   filename and the `AppData` folder used for PlayerPrefs.
3. **Resolution and Presentation**:
   - **Fullscreen Mode**: *Windowed* during testing, *Exclusive Fullscreen*
     for the ship build.
   - **Default Screen Width / Height**: e.g. `1920 × 1080` (matches the HUD
     reference resolution; smaller windows still work because of the
     CanvasScaler).
4. **Other Settings → Scripting Backend**: **IL2CPP** is recommended (faster,
   harder to reverse-engineer). Falls back to Mono if IL2CPP support isn't
   installed.
5. **Other Settings → Api Compatibility Level**: **.NET Standard 2.1**
   (Unity's default for Unity 6).

---

## 5. Build the executable

1. *File → Build Settings…* → **Build**.
2. Pick an **empty** folder, e.g. `Builds/Win64-vX.Y.Z/`.
   **Important**: never build into the repo's `Assets/` or `Library/`
   folders. Use a sibling folder.
3. Unity will compile scripts, package assets, and write the build. First
   builds take longer because IL2CPP cross-compiles every script to C++.
4. When it finishes, Unity opens the output folder. You should see:
   - `RtsGame.exe` (or whatever you set the Product Name to)
   - `RtsGame_Data/` (assets, scripts, resources)
   - `UnityCrashHandler64.exe`
   - `UnityPlayer.dll`
   - `MonoBleedingEdge/` (only if you built with the Mono backend)

---

## 6. Smoke test the build

1. Double-click `RtsGame.exe`.
2. The main menu should appear in the chosen resolution.
3. Pick a color and press *Play*.
4. Verify:
   - HUD bottom bar appears (Selected Info on the left, Command in the
     middle, Resources + Power + Tactical Map on the right).
   - You can left-click and drag-select units.
   - You can right-click ground to move them.
   - Right-clicking an enemy starts combat.
   - The minimap shows a live top-down view.

The build's runtime log (when you need it) is at:
`%USERPROFILE%\AppData\LocalLow\<Company Name>\<Product Name>\Player.log`

This is where the `Debug.Log` messages from `MainMenuController`,
`GameStateManager`, `RTSHUD`, etc. end up after the build is launched.

---

## 7. Common errors and fixes

| Symptom | Likely cause | Fix |
|---|---|---|
| `Build failed: Unable to load Player.dll for target Windows` | Windows Build Support module missing | Unity Hub → *Add Modules* → install Windows Build Support (IL2CPP). |
| Build succeeds but the `.exe` opens to a black screen | Wrong starting scene (e.g. an empty TMP example scene is first in Scenes In Build) | Open *Build Settings* → ensure `SampleScene.unity` is the only / first entry. |
| Build succeeds, menu shows, Play does nothing | `GameStateManager` missing in the scene | Editor → *Tools → RTS → Setup → Setup Main Menu*, then re-build. |
| HUD missing in the built `.exe` only | HUD was rebuilt after the scene was last saved — Editor still had the rebuilt canvas in memory, but the scene file on disk does not | Save the scene (`Ctrl+S`) AFTER running *Setup Gameplay HUD*, then rebuild. |
| Console error: `TMP Essentials not imported` | TextMesh Pro essentials never imported in this Unity install | *Window → TextMeshPro → Import TMP Essential Resources*, then rebuild. |
| Build is huge (> 1.5 GB) | TMP example scenes accidentally ticked in *Scenes In Build* | Untick them in *Build Settings*. |
| Crash on launch with `il2cpp.exe` errors | Mid-build IL2CPP failure (often disk-full or antivirus quarantining `il2cpp.exe`) | Whitelist the project folder in your antivirus, free disk space, rebuild. |

---

## 8. Not in this build (yet)

These items in your original request map to features the project does **not**
currently support — they require code that doesn't exist in the repo:

- **Online / Photon multiplayer.** Photon PUN is not installed. There is no
  `PhotonServerSettings`, no networked prefab pool, no `PhotonNetwork.Instantiate`
  call sites. Adding multiplayer is a separate, multi-day rewrite.
- **Lobby / Create Room / Join Random Room buttons.** Not present — the
  main menu is local-only.
- **Networked player prefabs / PhotonView / PhotonTransformView.** No prefab
  in `Assets/_Game/Prefabs/` carries a PhotonView component.

The `.exe` you produce from these instructions is a **standalone singleplayer
Windows build**. If you later decide to add multiplayer, that's a separate
ticket — please confirm in writing before any networking package is added so
we don't accidentally break the existing prototype.
