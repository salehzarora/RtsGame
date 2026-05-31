using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Targeted diagnosis and repair for the Online menu's button click path
/// in MainMenuScene (and any scene that hosts the lobby canvas).
///
/// Three things can make the Online buttons "do nothing":
///   1. <b>EventSystem / GraphicRaycaster / raycast blocker</b> — the click
///      never reaches the Button at all. (No <c>[OnlineUI] X clicked</c> log.)
///   2. <b>Null / wrong references on MultiplayerLobbyUI</b> — Awake's
///      WireButtons sees a null Button and silently skips AddListener, so the
///      click reaches a Button but has no listener. (Still no log.)
///   3. <b>Button.interactable == false</b> — the Button refuses pointer events.
///
/// The Validate menu reports each of these. The Repair menu fixes them in
/// place without rebuilding the canvas (so existing button positions, colour
/// pickers, lobby slot wiring etc. survive).
///
/// Strategy: name-based child lookup using <see cref="Transform.GetComponentsInChildren{T}(bool)"/>
/// inactive-inclusive — exactly what the canvas builder names, so re-pointing
/// is deterministic.
/// </summary>
public static class RepairMainMenuButtons
{
    [MenuItem("Tools/RTS/UI/Validate Main Menu Buttons")]
    public static void ValidateMenu() => Run(repair: false);

    [MenuItem("Tools/RTS/UI/Repair Main Menu Button Wiring")]
    public static void RepairMenu() => Run(repair: true);

    private static void Run(bool repair)
    {
        string label = repair ? "Repair" : "Validate";
        Debug.Log($"[{label}MMB] ─── {label} MainMenu button wiring ───");
        int issues = 0, fixes = 0;

        // ---- 1. EventSystem --------------------------------------------- //
        EventSystem[] eventSystems = Object.FindObjectsByType<EventSystem>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int esCount = 0;
        for (int i = 0; i < eventSystems.Length; i++) if (eventSystems[i] != null) esCount++;

        if (esCount == 0)
        {
            Debug.LogError("[MMB] ✗ No EventSystem in scene — UI clicks won't fire.");
            issues++;
            if (repair)
            {
                GameObject es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
                Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
                Debug.Log("[MMB]   ✓ Created EventSystem + StandaloneInputModule.");
                fixes++;
            }
        }
        else if (esCount > 1)
        {
            Debug.LogWarning($"[MMB] ✗ {esCount} EventSystems (must be 1).");
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
                Debug.Log("[MMB]   ✓ Removed duplicate EventSystem(s).");
            }
        }
        else
        {
            Debug.Log("[MMB] ✓ Exactly 1 EventSystem.");
            EventSystem es = eventSystems[0];
            if (es != null && es.GetComponent<BaseInputModule>() == null)
            {
                Debug.LogWarning("[MMB] ✗ EventSystem has no input module.");
                issues++;
                if (repair)
                {
                    es.gameObject.AddComponent<StandaloneInputModule>();
                    Debug.Log("[MMB]   ✓ Added StandaloneInputModule.");
                    fixes++;
                }
            }
        }

        // ---- 2. LobbyCanvas + GraphicRaycaster + ScreenDim -------------- //
        GameObject lobby = FindRootByName("LobbyCanvas");
        if (lobby == null)
        {
            Debug.LogError("[MMB] ✗ LobbyCanvas missing — run Setup Multiplayer Lobby UI.");
            issues++;
        }
        else
        {
            Debug.Log($"[MMB] ✓ LobbyCanvas present (activeSelf={lobby.activeSelf}).");

            GraphicRaycaster gr = lobby.GetComponent<GraphicRaycaster>();
            if (gr == null)
            {
                Debug.LogWarning("[MMB] ✗ LobbyCanvas has no GraphicRaycaster — clicks never hit.");
                issues++;
                if (repair)
                {
                    lobby.AddComponent<GraphicRaycaster>();
                    Debug.Log("[MMB]   ✓ Added GraphicRaycaster to LobbyCanvas.");
                    fixes++;
                }
            }

            CanvasGroup cg = lobby.GetComponent<CanvasGroup>();
            if (cg != null && (!cg.interactable || !cg.blocksRaycasts))
            {
                Debug.LogWarning("[MMB] ✗ LobbyCanvas CanvasGroup blocks input " +
                                 $"(interactable={cg.interactable}, blocksRaycasts={cg.blocksRaycasts}).");
                issues++;
                if (repair)
                {
                    cg.interactable = true;
                    cg.blocksRaycasts = true;
                    EditorUtility.SetDirty(cg);
                    Debug.Log("[MMB]   ✓ CanvasGroup → interactable+blocksRaycasts true.");
                    fixes++;
                }
            }

            // ScreenDim — must NOT block clicks. It's a full-screen overlay
            // sibling of the panels; if its Image.raycastTarget is true, every
            // click hits the dim instead of the buttons.
            Transform dim = lobby.transform.Find("ScreenDim");
            if (dim != null)
            {
                Image img = dim.GetComponent<Image>();
                if (img != null && img.raycastTarget)
                {
                    Debug.LogError("[MMB] ✗ ScreenDim Image.raycastTarget=true — BLOCKS ALL CLICKS.");
                    issues++;
                    if (repair)
                    {
                        img.raycastTarget = false;
                        EditorUtility.SetDirty(img);
                        Debug.Log("[MMB]   ✓ ScreenDim.raycastTarget = false.");
                        fixes++;
                    }
                }
            }
        }

        // ---- 3. MultiplayerLobbyUI controller references ---------------- //
        MultiplayerLobbyUI ui = Object.FindFirstObjectByType<MultiplayerLobbyUI>(FindObjectsInactive.Include);
        if (ui == null)
        {
            Debug.LogError("[MMB] ✗ MultiplayerLobbyUI missing — run Setup Multiplayer Lobby UI.");
            issues++;
        }
        else
        {
            Debug.Log($"[MMB] ✓ MultiplayerLobbyUI on '{ui.gameObject.name}'.");

            // canvasRoot
            if (ui.canvasRoot != lobby && lobby != null)
            {
                Debug.LogWarning("[MMB] ✗ canvasRoot ref null/wrong.");
                issues++;
                if (repair)
                {
                    ui.canvasRoot = lobby;
                    EditorUtility.SetDirty(ui);
                    Debug.Log("[MMB]   ✓ Re-attached canvasRoot → LobbyCanvas.");
                    fixes++;
                }
            }

            GameObject canvasRoot = ui.canvasRoot != null ? ui.canvasRoot : lobby;

            // mainMenuCanvas
            GameObject mm = FindRootByName("MainMenuCanvas");
            if (ui.mainMenuCanvas != mm && mm != null)
            {
                Debug.LogWarning("[MMB] ✗ mainMenuCanvas ref null/wrong.");
                issues++;
                if (repair)
                {
                    ui.mainMenuCanvas = mm;
                    EditorUtility.SetDirty(ui);
                    Debug.Log("[MMB]   ✓ Re-attached mainMenuCanvas → MainMenuCanvas.");
                    fixes++;
                }
            }

            // Panel refs
            FixPanelRef(ui, canvasRoot, "OnlineMenuPanel", () => ui.onlineMenuPanel, v => ui.onlineMenuPanel = v, repair, ref issues, ref fixes);
            FixPanelRef(ui, canvasRoot, "CreateRoomPanel", () => ui.createRoomPanel, v => ui.createRoomPanel = v, repair, ref issues, ref fixes);
            FixPanelRef(ui, canvasRoot, "RoomListPanel",   () => ui.roomListPanel,   v => ui.roomListPanel = v,   repair, ref issues, ref fixes);
            FixPanelRef(ui, canvasRoot, "LobbyPanel",      () => ui.lobbyPanel,      v => ui.lobbyPanel = v,      repair, ref issues, ref fixes);

            // Online buttons
            if (ui.onlineMenuPanel != null)
            {
                FixButton(ui, ui.onlineMenuPanel, "ConnectButton",     () => ui.onlineConnectButton,     v => ui.onlineConnectButton = v,     repair, ref issues, ref fixes);
                FixButton(ui, ui.onlineMenuPanel, "CreateRoomButton",  () => ui.onlineCreateRoomButton,  v => ui.onlineCreateRoomButton = v,  repair, ref issues, ref fixes);
                FixButton(ui, ui.onlineMenuPanel, "JoinRandomButton",  () => ui.onlineJoinRandomButton,  v => ui.onlineJoinRandomButton = v,  repair, ref issues, ref fixes);
                FixButton(ui, ui.onlineMenuPanel, "BrowseRoomsButton", () => ui.onlineBrowseRoomsButton, v => ui.onlineBrowseRoomsButton = v, repair, ref issues, ref fixes);
                FixButton(ui, ui.onlineMenuPanel, "BackButton",        () => ui.onlineBackButton,        v => ui.onlineBackButton = v,        repair, ref issues, ref fixes);

                // Status label
                GameObject statusGO = FindChildByName(ui.onlineMenuPanel, "StatusLabel");
                if (statusGO != null)
                {
                    TextMeshProUGUI tmp = statusGO.GetComponent<TextMeshProUGUI>();
                    if (tmp != null && ui.onlineStatusLabel != tmp)
                    {
                        Debug.LogWarning("[MMB] ✗ onlineStatusLabel ref null/wrong.");
                        issues++;
                        if (repair) { ui.onlineStatusLabel = tmp; EditorUtility.SetDirty(ui); fixes++; Debug.Log("[MMB]   ✓ Re-attached onlineStatusLabel."); }
                    }
                }
            }
        }

        // ---- 4. NetworkManagerRTS --------------------------------------- //
        NetworkManagerRTS nm = Object.FindFirstObjectByType<NetworkManagerRTS>(FindObjectsInactive.Include);
        if (nm == null)
        {
            Debug.LogWarning("[MMB] ✗ NetworkManagerRTS missing — Online actions will no-op even with wiring fixed.");
            issues++;
        }
        else
        {
            Debug.Log("[MMB] ✓ NetworkManagerRTS present.");
        }

        if (repair && fixes > 0)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log($"[{label}MMB] " + (issues == 0
            ? "✓ All checks passed — Online buttons should work."
            : (repair ? $"Fixed {fixes}/{issues} issue(s). Ctrl+S, then press Play and click an Online button. " +
                       "You should see '[OnlineUI] X clicked.' in the Console."
                     : $"✗ {issues} issue(s). Run Tools → RTS → UI → Repair Main Menu Button Wiring.")));
        Debug.Log($"[{label}MMB] ─────────────────────────");
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static void FixPanelRef(MultiplayerLobbyUI ui, GameObject canvasRoot, string panelName,
                                    System.Func<GameObject> getter, System.Action<GameObject> setter,
                                    bool repair, ref int issues, ref int fixes)
    {
        GameObject found = FindChildByName(canvasRoot, panelName);
        if (found == null)
        {
            Debug.LogWarning($"[MMB] ✗ '{panelName}' not found under canvasRoot.");
            issues++;
            return;
        }
        if (getter() == found) { Debug.Log($"[MMB] ✓ {panelName} ref OK."); return; }
        Debug.LogWarning($"[MMB] ✗ {panelName} ref null/wrong.");
        issues++;
        if (repair)
        {
            setter(found);
            EditorUtility.SetDirty(ui);
            Debug.Log($"[MMB]   ✓ Re-attached {panelName}.");
            fixes++;
        }
    }

    private static void FixButton(MultiplayerLobbyUI ui, GameObject parent, string buttonName,
                                  System.Func<Button> getter, System.Action<Button> setter,
                                  bool repair, ref int issues, ref int fixes)
    {
        GameObject go = FindChildByName(parent, buttonName);
        Button found = go != null ? go.GetComponent<Button>() : null;
        if (found == null)
        {
            Debug.LogWarning($"[MMB] ✗ '{buttonName}' Button missing under {parent.name}.");
            issues++;
            return;
        }

        bool refOk = getter() == found;
        bool interactOk = found.interactable;

        if (refOk && interactOk)
        {
            Debug.Log($"[MMB] ✓ {buttonName} OK.");
            return;
        }

        if (!refOk)
        {
            Debug.LogWarning($"[MMB] ✗ {buttonName} ref null/wrong.");
            issues++;
            if (repair) { setter(found); EditorUtility.SetDirty(ui); fixes++; Debug.Log($"[MMB]   ✓ Re-attached {buttonName}."); }
        }
        if (!interactOk)
        {
            Debug.LogWarning($"[MMB] ✗ {buttonName}.interactable = false.");
            issues++;
            if (repair) { found.interactable = true; EditorUtility.SetDirty(found); fixes++; Debug.Log($"[MMB]   ✓ {buttonName}.interactable = true."); }
        }
    }

    private static GameObject FindRootByName(string name)
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

    private static GameObject FindChildByName(GameObject root, string name)
    {
        if (root == null) return null;
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && all[i].name == name)
                return all[i].gameObject;
        return null;
    }
}
