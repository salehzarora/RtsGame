using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Repair + validate the startup state of MainMenuScene.
///
/// The most common reason "press Play and don't see the menu" is that
/// <see cref="MainMenuController.menuCanvas"/> got nulled by an earlier
/// cleanup pass (UI duplicates pass / scene split / etc.), so its Awake
/// can't `SetActive(true)` the menu canvas — even though the canvas is
/// still sitting in the scene, inactive. Same root cause as the LobbyCanvas
/// fix; same shape of repair.
///
/// Repair (`Tools → RTS → UI → Repair MainMenuScene UI`) runs in edit mode:
///   • Ensures exactly one EventSystem.
///   • Re-points <see cref="MainMenuController.menuCanvas"/> →
///     MainMenuCanvas (root, inactive-inclusive lookup).
///   • Re-points <see cref="MainMenuController.hudCanvas"/> → HUDCanvas if
///     it exists; null is fine in MainMenuScene (gameplay HUD lives in the
///     gameplay scene).
///   • Re-points <see cref="MultiplayerLobbyUI.canvasRoot"/> → LobbyCanvas
///     and <see cref="MultiplayerLobbyUI.mainMenuCanvas"/> → MainMenuCanvas.
///   • Sets startup state: MainMenuCanvas ACTIVE, LobbyCanvas /
///     OptionsCanvas / MultiplayerDebugCanvas INACTIVE.
///   • Marks scene dirty.
///
/// Validate (`Tools → RTS → UI → Validate MainMenuScene UI`) is read-only.
/// </summary>
public static class RepairMainMenuScene
{
    private const string MainMenuScenePath = "Assets/Scenes/MainMenuScene.unity";

    [MenuItem("Tools/RTS/UI/Validate MainMenuScene UI")]
    public static void ValidateMenu() => Run(repair: false);

    [MenuItem("Tools/RTS/UI/Repair MainMenuScene UI")]
    public static void RepairMenu() => Run(repair: true);

    private static void Run(bool repair)
    {
        string label = repair ? "Repair" : "Validate";
        Debug.Log($"[{label}MainMenu] ─── {label} MainMenuScene UI ───");

        var active = EditorSceneManager.GetActiveScene();
        Debug.Log($"[{label}MainMenu] Active scene: '{active.name}' ({active.path})");
        if (active.path != MainMenuScenePath && active.name != "MainMenuScene")
            Debug.LogWarning($"[{label}MainMenu] ⚠ Open scene is '{active.name}', not " +
                             "MainMenuScene. Repair will still operate on the open scene " +
                             "(name-based), but the typical home is MainMenuScene.");

        int issues = 0, fixes = 0;

        // ---- 1. EventSystem -------------------------------------------- //
        EventSystem[] eventSystems = Object.FindObjectsByType<EventSystem>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int esCount = 0;
        for (int i = 0; i < eventSystems.Length; i++) if (eventSystems[i] != null) esCount++;

        if (esCount == 0)
        {
            Debug.LogError("[MainMenu] ✗ No EventSystem — UI clicks won't fire.");
            issues++;
            if (repair)
            {
                GameObject es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
                Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
                Debug.Log("[MainMenu]   ✓ Created EventSystem.");
                fixes++;
            }
        }
        else if (esCount > 1)
        {
            Debug.LogWarning($"[MainMenu] ✗ {esCount} EventSystems (must be 1).");
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
            }
        }
        else
        {
            Debug.Log("[MainMenu] ✓ Exactly 1 EventSystem.");
        }

        // ---- 2. Find the three canvas roots ---------------------------- //
        GameObject mainMenuCanvas = FindRoot("MainMenuCanvas");
        GameObject lobbyCanvas    = FindRoot("LobbyCanvas");
        GameObject optionsCanvas  = FindRoot("OptionsCanvas");
        GameObject hudCanvas      = FindRoot("HUDCanvas");
        GameObject debugCanvas    = FindRoot("MultiplayerDebugCanvas");

        if (mainMenuCanvas == null)
        {
            Debug.LogError("[MainMenu] ✗ No MainMenuCanvas in scene. Run " +
                           "Tools → RTS → Setup → Setup Main Menu to rebuild it.");
            issues++;
        }
        else
        {
            Debug.Log($"[MainMenu] ✓ MainMenuCanvas present (activeSelf={mainMenuCanvas.activeSelf}).");
        }
        if (lobbyCanvas == null)
            Debug.LogWarning("[MainMenu] ⚠ No LobbyCanvas in scene — Online button " +
                             "won't have anything to open. Run Setup Multiplayer Lobby UI.");

        // ---- 3. MainMenuController references -------------------------- //
        MainMenuController mmc = Object.FindFirstObjectByType<MainMenuController>(
            FindObjectsInactive.Include);
        if (mmc == null)
        {
            Debug.LogError("[MainMenu] ✗ No MainMenuController in scene. Run Setup Main Menu.");
            issues++;
        }
        else
        {
            Debug.Log($"[MainMenu] ✓ MainMenuController on '{mmc.gameObject.name}'.");

            if (mmc.menuCanvas != mainMenuCanvas && mainMenuCanvas != null)
            {
                Debug.LogWarning("[MainMenu] ✗ MainMenuController.menuCanvas ref null/wrong.");
                issues++;
                if (repair)
                {
                    mmc.menuCanvas = mainMenuCanvas;
                    EditorUtility.SetDirty(mmc);
                    Debug.Log("[MainMenu]   ✓ Re-attached MainMenuController.menuCanvas.");
                    fixes++;
                }
            }
            if (mmc.hudCanvas == null && hudCanvas != null)
            {
                Debug.Log("[MainMenu]   (MainMenuController.hudCanvas is null; HUDCanvas " +
                          "found in scene. Re-attaching.)");
                if (repair)
                {
                    mmc.hudCanvas = hudCanvas;
                    EditorUtility.SetDirty(mmc);
                    fixes++;
                }
            }
            else if (mmc.hudCanvas == null && hudCanvas == null)
            {
                Debug.Log("[MainMenu]   (MainMenuController.hudCanvas null and no HUDCanvas " +
                          "in scene — fine for MainMenuScene; gameplay HUD lives in the gameplay scene.)");
            }
        }

        // ---- 4. MultiplayerLobbyUI references -------------------------- //
        MultiplayerLobbyUI lobbyUi = Object.FindFirstObjectByType<MultiplayerLobbyUI>(
            FindObjectsInactive.Include);
        if (lobbyUi != null)
        {
            if (lobbyUi.canvasRoot != lobbyCanvas && lobbyCanvas != null)
            {
                Debug.LogWarning("[MainMenu] ✗ MultiplayerLobbyUI.canvasRoot ref null/wrong.");
                issues++;
                if (repair)
                {
                    lobbyUi.canvasRoot = lobbyCanvas;
                    EditorUtility.SetDirty(lobbyUi);
                    Debug.Log("[MainMenu]   ✓ Re-attached MultiplayerLobbyUI.canvasRoot.");
                    fixes++;
                }
            }
            if (lobbyUi.mainMenuCanvas != mainMenuCanvas && mainMenuCanvas != null)
            {
                Debug.LogWarning("[MainMenu] ✗ MultiplayerLobbyUI.mainMenuCanvas ref null/wrong.");
                issues++;
                if (repair)
                {
                    lobbyUi.mainMenuCanvas = mainMenuCanvas;
                    EditorUtility.SetDirty(lobbyUi);
                    Debug.Log("[MainMenu]   ✓ Re-attached MultiplayerLobbyUI.mainMenuCanvas.");
                    fixes++;
                }
            }
        }

        // ---- 5. Startup activation state ------------------------------- //
        // MainMenuCanvas should be ACTIVE at startup; everything else INACTIVE.
        // Repair sets the SCENE-baked activeSelf so runtime Awakes inherit
        // the correct state.
        if (mainMenuCanvas != null && !mainMenuCanvas.activeSelf)
        {
            Debug.LogWarning("[MainMenu] ✗ MainMenuCanvas.activeSelf = false at scene start.");
            issues++;
            if (repair)
            {
                mainMenuCanvas.SetActive(true);
                EditorUtility.SetDirty(mainMenuCanvas);
                Debug.Log("[MainMenu]   ✓ MainMenuCanvas.activeSelf = true.");
                fixes++;
            }
        }
        SetInactiveIfActive(lobbyCanvas,    "LobbyCanvas",            repair, ref issues, ref fixes);
        SetInactiveIfActive(optionsCanvas,  "OptionsCanvas",          repair, ref issues, ref fixes);
        SetInactiveIfActive(debugCanvas,    "MultiplayerDebugCanvas", repair, ref issues, ref fixes);

        // ---- 6. Title + buttons under MainMenuCanvas ------------------- //
        if (mainMenuCanvas != null)
        {
            CheckChild(mainMenuCanvas, "Title",            "Title text",          ref issues);
            CheckChild(mainMenuCanvas, "BtnSinglePlayer",  "Single Player button", ref issues);
            CheckChild(mainMenuCanvas, "BtnOnline",        "Online button",       ref issues);
            // BtnOptions is optional in current builds; only warn.
            GameObject optBtn = FindChild(mainMenuCanvas, "BtnOptions");
            if (optBtn == null)
                Debug.Log("[MainMenu]   (BtnOptions not present — optional; Options " +
                          "menu has its own access path.)");
        }

        // ---- 7. No gameplay roots leaked in --------------------------- //
        string[] gameplayRoots =
        {
            "Environment", "ResourceNodes", "PlayerStart", "EnemyStart",
            "GameplayWorldRoot", "HUDCanvas", "EscapeMenuCanvas",
            "SelectionCanvas", "MinimapCameraGO", "MatchManager",
        };
        for (int i = 0; i < gameplayRoots.Length; i++)
        {
            if (FindRoot(gameplayRoots[i]) != null)
            {
                Debug.LogWarning($"[MainMenu] ⚠ Gameplay root '{gameplayRoots[i]}' " +
                                 "present in MainMenuScene. It belongs to the gameplay scene. " +
                                 "Run Tools → RTS → Scenes → Cleanup MainMenuScene.");
                issues++;
            }
        }

        // ---- 8. Persist + summarise ----------------------------------- //
        if (repair && fixes > 0)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log($"[{label}MainMenu] " + (issues == 0
            ? "✓ MainMenuScene UI healthy — press Play to see the menu."
            : (repair ? $"Fixed {fixes}/{issues} issue(s). Ctrl+S, then press Play."
                      : $"✗ {issues} issue(s). Run Tools → RTS → UI → Repair MainMenuScene UI.")));
        Debug.Log($"[{label}MainMenu] ─────────────────────────────────");
    }

    // ------------------------------------------------------------------ //

    private static void SetInactiveIfActive(GameObject go, string label, bool repair,
                                            ref int issues, ref int fixes)
    {
        if (go == null) return;
        if (!go.activeSelf) { Debug.Log($"[MainMenu] ✓ {label} hidden at startup."); return; }
        Debug.LogWarning($"[MainMenu] ✗ {label} is ACTIVE at startup — it should be hidden " +
                         "and shown by user action (Online / Options).");
        issues++;
        if (repair)
        {
            go.SetActive(false);
            EditorUtility.SetDirty(go);
            Debug.Log($"[MainMenu]   ✓ {label}.activeSelf = false.");
            fixes++;
        }
    }

    private static void CheckChild(GameObject parent, string name, string label, ref int issues)
    {
        GameObject child = FindChild(parent, name);
        if (child != null) { Debug.Log($"[MainMenu] ✓ {label} ('{name}') present."); return; }
        Debug.LogError($"[MainMenu] ✗ {label} ('{name}') missing under MainMenuCanvas. " +
                       "Run Tools → RTS → Setup → Setup Main Menu to rebuild.");
        issues++;
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
