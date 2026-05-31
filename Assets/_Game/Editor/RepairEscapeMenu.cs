using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Repair + validate the in-game ESC pause menu in the gameplay scene
/// (typically SampleScene).
///
/// Same root cause as the MainMenuCanvas and LobbyCanvas wiring fixes:
/// <see cref="EscapeMenuController.Update"/> reads <c>menuCanvas</c> and
/// returns early when it's null (and silently no-ops on null inside
/// <see cref="EscapeMenuController.ShowPauseMenu"/> too). A cleanup or
/// scene-rebuild pass that destroyed the panel the ref pointed at leaves
/// the reference as fake-null — ESC presses are read by Update but every
/// downstream call swallows them.
///
/// Repair (`Tools → RTS → UI → Repair Escape Menu`) runs in edit mode:
///   • Ensures exactly one EventSystem.
///   • Re-points <see cref="EscapeMenuController.menuCanvas"/> →
///     EscapeMenuCanvas (root, inactive-inclusive).
///   • Re-points <see cref="EscapeMenuController.mainMenuCanvas"/>,
///     <see cref="EscapeMenuController.hudCanvas"/>,
///     <see cref="EscapeMenuController.lobbyCanvas"/> when those roots
///     exist in the scene.
///   • Verifies BtnResume / BtnMainMenu / BtnQuit exist as descendants
///     under EscapeMenuCanvas; warns if BtnOptions is missing (optional).
///   • Marks scene dirty.
///
/// Validate (`Tools → RTS → UI → Validate Escape Menu`) is read-only.
/// </summary>
public static class RepairEscapeMenu
{
    [MenuItem("Tools/RTS/UI/Validate Escape Menu")]
    public static void ValidateMenu() => Run(repair: false);

    [MenuItem("Tools/RTS/UI/Repair Escape Menu")]
    public static void RepairMenu() => Run(repair: true);

    private static void Run(bool repair)
    {
        string label = repair ? "Repair" : "Validate";
        Debug.Log($"[{label}EscMenu] ─── {label} Escape Menu ───");

        var active = EditorSceneManager.GetActiveScene();
        Debug.Log($"[{label}EscMenu] Active scene: '{active.name}' ({active.path})");
        if (active.name == "MainMenuScene")
            Debug.LogWarning($"[{label}EscMenu] ⚠ ESC menu lives in the gameplay scene " +
                             "(typically SampleScene), not MainMenuScene. Open the gameplay " +
                             "scene and re-run, or proceed if you know what you're doing.");

        int issues = 0, fixes = 0;

        // ---- 1. EventSystem ------------------------------------------- //
        EventSystem[] eventSystems = Object.FindObjectsByType<EventSystem>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int esCount = 0;
        for (int i = 0; i < eventSystems.Length; i++) if (eventSystems[i] != null) esCount++;

        if (esCount == 0)
        {
            Debug.LogError("[EscMenu] ✗ No EventSystem — pause-menu buttons won't fire.");
            issues++;
            if (repair)
            {
                GameObject es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
                Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
                Debug.Log("[EscMenu]   ✓ Created EventSystem.");
                fixes++;
            }
        }
        else if (esCount > 1)
        {
            Debug.LogWarning($"[EscMenu] ✗ {esCount} EventSystems (must be 1).");
            issues++;
            if (repair)
            {
                bool kept = false;
                for (int i = 0; i < eventSystems.Length; i++)
                {
                    if (eventSystems[i] == null) continue;
                    if (!kept) { kept = true; continue; }
                    Undo.DestroyObjectImmediate(eventSystems[i].gameObject);
                    fixes++;
                }
                Debug.Log("[EscMenu]   ✓ Removed duplicate EventSystem(s).");
            }
        }
        else
        {
            Debug.Log("[EscMenu] ✓ Exactly 1 EventSystem.");
        }

        // ---- 2. EscapeMenuCanvas root --------------------------------- //
        GameObject escCanvas = FindRoot("EscapeMenuCanvas");
        if (escCanvas == null)
        {
            Debug.LogError("[EscMenu] ✗ No EscapeMenuCanvas in scene. " +
                           "Run Tools → RTS → Setup → Setup Escape Menu to rebuild it.");
            issues++;
        }
        else
        {
            Debug.Log($"[EscMenu] ✓ EscapeMenuCanvas present (activeSelf={escCanvas.activeSelf}).");
            // EscapeMenuController.Awake hides it; scene state can be either,
            // but ACTIVE is the typical bake (Awake flips it off at runtime).
            // We don't force here — let Awake handle it.

            GraphicRaycaster gr = escCanvas.GetComponent<GraphicRaycaster>();
            if (gr == null)
            {
                Debug.LogWarning("[EscMenu] ✗ EscapeMenuCanvas has no GraphicRaycaster — " +
                                 "Resume/MainMenu/Quit clicks won't reach the buttons.");
                issues++;
                if (repair)
                {
                    escCanvas.AddComponent<GraphicRaycaster>();
                    Debug.Log("[EscMenu]   ✓ Added GraphicRaycaster to EscapeMenuCanvas.");
                    fixes++;
                }
            }
        }

        // ---- 3. EscapeMenuController + reference re-wire -------------- //
        EscapeMenuController emc = Object.FindFirstObjectByType<EscapeMenuController>(
            FindObjectsInactive.Include);
        if (emc == null)
        {
            Debug.LogError("[EscMenu] ✗ No EscapeMenuController in scene. " +
                           "Run Tools → RTS → Setup → Setup Escape Menu.");
            issues++;
        }
        else
        {
            Debug.Log($"[EscMenu] ✓ EscapeMenuController on '{emc.gameObject.name}'.");

            // menuCanvas → EscapeMenuCanvas
            if (emc.menuCanvas != escCanvas && escCanvas != null)
            {
                Debug.LogWarning("[EscMenu] ✗ EscapeMenuController.menuCanvas ref null/wrong " +
                                 "— this is why ESC silently does nothing.");
                issues++;
                if (repair)
                {
                    emc.menuCanvas = escCanvas;
                    EditorUtility.SetDirty(emc);
                    Debug.Log("[EscMenu]   ✓ Re-attached EscapeMenuController.menuCanvas.");
                    fixes++;
                }
            }

            // mainMenuCanvas (optional — only in scenes where it exists)
            GameObject mmCanvas = FindRoot("MainMenuCanvas");
            if (emc.mainMenuCanvas != mmCanvas && mmCanvas != null)
            {
                Debug.LogWarning("[EscMenu] ✗ EscapeMenuController.mainMenuCanvas ref null/wrong.");
                issues++;
                if (repair)
                {
                    emc.mainMenuCanvas = mmCanvas;
                    EditorUtility.SetDirty(emc);
                    Debug.Log("[EscMenu]   ✓ Re-attached mainMenuCanvas.");
                    fixes++;
                }
            }
            else if (mmCanvas == null && active.name != "MainMenuScene")
            {
                Debug.Log("[EscMenu]   (MainMenuCanvas not in this scene — fine for the " +
                          "gameplay scene; ESC → Main Menu uses SceneManager.LoadScene instead.)");
            }

            // hudCanvas
            GameObject hud = FindRoot("HUDCanvas");
            if (emc.hudCanvas != hud && hud != null)
            {
                Debug.LogWarning("[EscMenu] ✗ EscapeMenuController.hudCanvas ref null/wrong.");
                issues++;
                if (repair)
                {
                    emc.hudCanvas = hud;
                    EditorUtility.SetDirty(emc);
                    Debug.Log("[EscMenu]   ✓ Re-attached hudCanvas.");
                    fixes++;
                }
            }

            // lobbyCanvas (optional)
            GameObject lobby = FindRoot("LobbyCanvas");
            if (emc.lobbyCanvas != lobby && lobby != null)
            {
                Debug.LogWarning("[EscMenu] ✗ EscapeMenuController.lobbyCanvas ref null/wrong.");
                issues++;
                if (repair)
                {
                    emc.lobbyCanvas = lobby;
                    EditorUtility.SetDirty(emc);
                    Debug.Log("[EscMenu]   ✓ Re-attached lobbyCanvas.");
                    fixes++;
                }
            }
        }

        // ---- 4. Buttons under EscapeMenuCanvas ------------------------ //
        if (escCanvas != null)
        {
            CheckButton(escCanvas, "BtnResume",   "Resume button",     required: true,  ref issues);
            CheckButton(escCanvas, "BtnMainMenu", "Main Menu button",  required: true,  ref issues);
            CheckButton(escCanvas, "BtnQuit",     "Quit button",       required: true,  ref issues);
            CheckButton(escCanvas, "BtnOptions",  "Options button",    required: false, ref issues);
        }

        // ---- 5. GameStateManager presence ----------------------------- //
        // EscapeMenuController.Update gates on GameStateManager.IsPlaying.
        // Without a GameStateManager, IsPlaying is always false and ESC is
        // ignored even when everything else is wired correctly.
        GameStateManager gsm = Object.FindFirstObjectByType<GameStateManager>(
            FindObjectsInactive.Include);
        if (gsm == null)
        {
            Debug.LogWarning("[EscMenu] ⚠ No GameStateManager in scene — " +
                             "EscapeMenuController.Update gates on GameStateManager.IsPlaying " +
                             "so ESC will be ignored. This is fine for direct-play dev mode " +
                             "(coordinator's dev-mode fallback triggers OnMatchStarted → " +
                             "IsPlaying flips), but flag it if ESC is dead during normal flow.");
        }
        else
        {
            Debug.Log("[EscMenu] ✓ GameStateManager present.");
        }

        // ---- 6. Persist + summarise ----------------------------------- //
        if (repair && fixes > 0)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log($"[{label}EscMenu] " + (issues == 0
            ? "✓ Escape menu wiring healthy. Press Play → start a match → press ESC."
            : (repair ? $"Fixed {fixes}/{issues} issue(s). Ctrl+S, then test in Play mode."
                      : $"✗ {issues} issue(s). Run Tools → RTS → UI → Repair Escape Menu.")));
        Debug.Log($"[{label}EscMenu] ─────────────────────────");
    }

    // ------------------------------------------------------------------ //

    private static void CheckButton(GameObject root, string name, string label,
                                    bool required, ref int issues)
    {
        GameObject go = FindChild(root, name);
        if (go == null)
        {
            if (required)
            {
                Debug.LogError($"[EscMenu] ✗ {label} ('{name}') missing under EscapeMenuCanvas. " +
                               "Run Tools → RTS → Setup → Setup Escape Menu.");
                issues++;
            }
            else
            {
                Debug.Log($"[EscMenu]   (Optional '{name}' not present — skipped.)");
            }
            return;
        }

        Button btn = go.GetComponent<Button>();
        if (btn == null)
        {
            Debug.LogError($"[EscMenu] ✗ '{name}' has no Button component.");
            issues++;
            return;
        }
        if (!btn.interactable)
        {
            Debug.LogWarning($"[EscMenu] ✗ {label} interactable=false.");
            issues++;
        }
        // OnClick listeners are persistent (set by SetupEscapeMenu via
        // UnityEventTools.AddPersistentListener), so they survive scene
        // saves. We can't introspect runtime listeners but we CAN count
        // persistent ones.
        int persistent = btn.onClick.GetPersistentEventCount();
        if (persistent == 0)
        {
            Debug.LogError($"[EscMenu] ✗ {label} has 0 persistent OnClick listeners. " +
                           "Run Tools → RTS → Setup → Setup Escape Menu to re-wire.");
            issues++;
        }
        else
        {
            Debug.Log($"[EscMenu] ✓ {label} OK ({persistent} listener(s)).");
        }
    }

    private static GameObject FindRoot(string name)
    {
        Transform[] all = Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null || all[i].parent != null) continue;
            if (all[i].name == name) return all[i].gameObject;
        }
        return null;
    }

    private static GameObject FindChild(GameObject root, string name)
    {
        if (root == null) return null;
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && all[i].name == name)
                return all[i].gameObject;
        return null;
    }
}
