using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Local in-game pause menu. Pressing ESC during gameplay toggles a small
/// dark panel with three buttons:
///
///   • Resume       — hides the panel.
///   • Main Menu    — leaves the current match (and the Photon room in MP),
///                    hides the gameplay world, shows the main menu canvas.
///   • Quit         — Application.Quit (in builds; logs in the editor).
///
/// Gating rules:
///   • Only opens while <see cref="GameStateManager.IsGameStarted"/> is true.
///     ESC is ignored while the main menu / online menu / lobby / room list
///     is up.
///   • ESC presses while typing in an active <see cref="TMPro.TMP_InputField"/>
///     (or legacy InputField) are ignored so they don't pop the menu mid-type.
///
/// Single-player vs multiplayer:
///   • The pause menu itself is purely local — pressing ESC on one client in
///     a Photon room does NOT pause the other side. Only that client sees
///     the panel.
///   • Main Menu in MP calls <see cref="NetworkManagerRTS.LeaveRoom"/> and
///     toggles multiplayerMode off so a subsequent Single Player press starts
///     a clean SP match.
///
/// Setup: built and wired by Tools → RTS → Setup → Setup Escape Menu.
/// </summary>
[DisallowMultipleComponent]
public class EscapeMenuController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Canvas References")]
    [Tooltip("The whole EscapeMenuCanvas GameObject — toggled active/inactive " +
             "by ESC. Hidden at scene start.")]
    public GameObject menuCanvas;

    [Tooltip("Reference to MainMenuCanvas so Main Menu can re-show it.")]
    public GameObject mainMenuCanvas;

    [Tooltip("Reference to HUDCanvas so Main Menu can hide it.")]
    public GameObject hudCanvas;

    [Tooltip("Optional reference to LobbyCanvas so Main Menu can hide it if " +
             "the player somehow returns from a state where it was visible.")]
    public GameObject lobbyCanvas;

    public static EscapeMenuController Instance { get; private set; }

    // Set true between LeaveRoom() and OnLeftRoom so the main menu is shown
    // only AFTER the Photon room has actually been left.
    private bool pendingReturnToMenu;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[PauseMenu] Duplicate EscapeMenuController destroyed.");
            Destroy(this);
            return;
        }
        Instance = this;

        // Always start hidden — the menu is only relevant DURING gameplay.
        if (menuCanvas != null) menuCanvas.SetActive(false);

#if PHOTON_UNITY_NETWORKING
        // Show the main menu only once the room-leave actually completes.
        NetworkManagerRTS.OnRoomLeftEvent += HandleRoomLeftShowMenu;
#endif
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
#if PHOTON_UNITY_NETWORKING
        NetworkManagerRTS.OnRoomLeftEvent -= HandleRoomLeftShowMenu;
#endif
    }

    private void HandleRoomLeftShowMenu()
    {
        if (!pendingReturnToMenu) return;
        pendingReturnToMenu = false;
        Debug.Log("[PauseMenu] OnLeftRoom received — showing main menu.");
        ShowMainMenuCanvas();
    }

    /// <summary>
    /// Runtime self-heal — mirrors the editor's "Repair Escape Menu" tool so
    /// the player never has to touch the menu bar to make ESC work. Runs in
    /// Start so every other Awake/OnEnable in the freshly-loaded gameplay
    /// scene has finished, then re-resolves any null/wrong inspector
    /// reference by NAME against the active scene (inactive-inclusive).
    /// Idempotent — safe to call repeatedly.
    /// </summary>
    private void Start()
    {
        Debug.Log("[EscapeMenu] Runtime self-heal started.");

        // menuCanvas → EscapeMenuCanvas (REQUIRED — without this, ESC silently no-ops).
        if (menuCanvas == null)
        {
            menuCanvas = FindRoot("EscapeMenuCanvas");
            if (menuCanvas != null) Debug.Log("[EscapeMenu] menuCanvas resolved: EscapeMenuCanvas");
            else Debug.LogError("[EscapeMenu] menuCanvas resolution FAILED — no 'EscapeMenuCanvas' " +
                                "root in the active scene. Run Setup Escape Menu first.");
        }

        // Awake's hide ran before serialized refs were resolved here; ensure
        // it's hidden again now that we know which GO it is.
        if (menuCanvas != null && menuCanvas.activeSelf)
            menuCanvas.SetActive(false);

        // hudCanvas → HUDCanvas (used to hide the HUD on Main Menu).
        if (hudCanvas == null)
        {
            hudCanvas = FindRoot("HUDCanvas");
            if (hudCanvas != null) Debug.Log("[EscapeMenu] hudCanvas resolved: HUDCanvas");
        }

        // mainMenuCanvas → MainMenuCanvas (often absent in the gameplay scene;
        // OnClickMainMenu falls back to SceneManager.LoadScene then).
        if (mainMenuCanvas == null)
        {
            mainMenuCanvas = FindRoot("MainMenuCanvas");
            if (mainMenuCanvas != null) Debug.Log("[EscapeMenu] mainMenuCanvas resolved: MainMenuCanvas");
        }

        // lobbyCanvas → LobbyCanvas (optional cleanup target).
        if (lobbyCanvas == null)
        {
            lobbyCanvas = FindRoot("LobbyCanvas");
            if (lobbyCanvas != null) Debug.Log("[EscapeMenu] lobbyCanvas resolved: LobbyCanvas");
        }

        // Ensure the EscapeMenuCanvas can actually receive clicks.
        if (menuCanvas != null && menuCanvas.GetComponent<GraphicRaycaster>() == null)
        {
            menuCanvas.AddComponent<GraphicRaycaster>();
            Debug.Log("[EscapeMenu] GraphicRaycaster added to EscapeMenuCanvas.");
        }

        // Button presence + listener count (listeners are persistent — set by
        // SetupEscapeMenu via UnityEventTools.AddPersistentListener — so they
        // survive scene save/load. We don't re-add at runtime; we just report.)
        if (menuCanvas != null)
        {
            ReportButton(menuCanvas, "BtnResume",   "Resume");
            ReportButton(menuCanvas, "BtnOptions",  "Options");
            ReportButton(menuCanvas, "BtnMainMenu", "Main Menu");
            ReportButton(menuCanvas, "BtnQuit",     "Quit");
        }

        Debug.Log("[EscapeMenu] Runtime self-heal complete.");
    }

    /// <summary>Inactive-inclusive root GameObject search by name in the active scene.</summary>
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

    private static void ReportButton(GameObject root, string childName, string humanLabel)
    {
        Transform t = FindDescendant(root.transform, childName);
        if (t == null)
        {
            Debug.LogError($"[EscapeMenu] {humanLabel} button ('{childName}') MISSING under EscapeMenuCanvas. " +
                           "Run Tools → RTS → Setup → Setup Escape Menu.");
            return;
        }
        Button btn = t.GetComponent<Button>();
        if (btn == null)
        {
            Debug.LogError($"[EscapeMenu] {humanLabel} '{childName}' has no Button component.");
            return;
        }
        int listeners = btn.onClick.GetPersistentEventCount();
        if (listeners == 0)
            Debug.LogError($"[EscapeMenu] {humanLabel} button has 0 persistent OnClick listeners. " +
                           "Run Setup Escape Menu to re-wire.");
        else
            Debug.Log($"[EscapeMenu] {humanLabel} button wired ({listeners} listener(s)).");
    }

    private static Transform FindDescendant(Transform root, string name)
    {
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && all[i].name == name)
                return all[i];
        return null;
    }

    private void Update()
    {
        if (!Input.GetKeyDown(KeyCode.Escape)) return;
        Debug.Log("[EscapeMenu] ESC key detected.");

        // If we're typing into a UI input field, swallow the press — ESC there
        // usually means "stop editing", not "open pause".
        if (IsTypingInInputField())
        {
            Debug.Log("[EscapeMenu] ESC swallowed — typing in an input field.");
            return;
        }

        // Relaxed scene gate: ESC is ignored only in MainMenuScene (where the
        // player should use the menu UI, not pause). In every other scene —
        // gameplay, dev direct-play, future scenes — ESC always works.
        // (Previous behaviour gated on GameStateManager.IsPlaying, which
        // returned silently when GameStateManager was missing or the match
        // payload hadn't flipped the state — making ESC look broken.)
        string sceneName = SceneManager.GetActiveScene().name;
        if (sceneName == "MainMenuScene")
        {
            Debug.Log("[EscapeMenu] ESC ignored — in MainMenuScene (use the menu UI).");
            return;
        }

        if (menuCanvas == null)
        {
            Debug.LogError("[EscapeMenu] ESC blocked — menuCanvas is null even after Start " +
                           "self-heal. EscapeMenuCanvas missing from scene? Run " +
                           "Tools → RTS → Setup → Setup Escape Menu.");
            return;
        }

        // GameStateManager.IsPlaying is informational only now — log it for
        // diagnostics but don't gate on it.
        if (GameStateManager.Instance != null && !GameStateManager.IsPlaying)
            Debug.Log("[EscapeMenu] (Note: GameStateManager.IsPlaying=false — allowing ESC anyway " +
                      "because we're in a gameplay scene.)");

        if (menuCanvas.activeSelf)
            HidePauseMenu();
        else
            ShowPauseMenu();
    }

    private static bool IsTypingInInputField()
    {
        EventSystem es = EventSystem.current;
        if (es == null || es.currentSelectedGameObject == null) return false;

        if (es.currentSelectedGameObject.GetComponent<TMPro.TMP_InputField>() != null) return true;
        if (es.currentSelectedGameObject.GetComponent<UnityEngine.UI.InputField>() != null) return true;
        return false;
    }

    // ------------------------------------------------------------------ //
    // Show / hide
    // ------------------------------------------------------------------ //

    private void ShowPauseMenu()
    {
        if (menuCanvas == null) return;
        menuCanvas.SetActive(true);
        AudioManager.Sfx(GameSound.UIOpenPanel);
        Debug.Log("[PauseMenu] ESC menu opened.");
    }

    private void HidePauseMenu()
    {
        if (menuCanvas == null) return;
        menuCanvas.SetActive(false);
        AudioManager.Sfx(GameSound.UIClosePanel);
        Debug.Log("[PauseMenu] ESC menu closed.");
    }

    // ------------------------------------------------------------------ //
    // Button callbacks — wired by SetupEscapeMenu
    // ------------------------------------------------------------------ //

    /// <summary>Hides the pause panel and returns control to gameplay.</summary>
    public void OnClickResume()
    {
        HidePauseMenu();
    }

    /// <summary>
    /// Tear down the local match view and bring the player back to the main
    /// menu. In multiplayer this also leaves the Photon room — the OTHER
    /// player just sees their normal player-left behaviour (Photon callback)
    /// and stays in the room.
    /// </summary>
    public void OnClickMainMenu()
    {
        Debug.Log("[PauseMenu] Returning to Main Menu.");

        // 1. Close the pause panel itself.
        HidePauseMenu();

        // 2. Immediate local UI teardown so the player isn't left staring at
        //    the running match while the room-leave completes.
        UnitSelector.Instance?.ClearSelection();

        GameplayWorldRoot worldRoot = Object.FindFirstObjectByType<GameplayWorldRoot>();
        if (worldRoot != null) worldRoot.ReHide();

        if (hudCanvas != null)   hudCanvas.SetActive(false);
        if (lobbyCanvas != null) lobbyCanvas.SetActive(false);

        if (GameStateManager.Instance != null)
            GameStateManager.Instance.ResetToMenu();

        // 3. Leave the room (if any) and defer showing the main menu until
        //    OnLeftRoom — that's when MatchSessionManager (subscribed to
        //    OnRoomLeftEvent) runs the full, broadcast-safe cleanup. A timeout
        //    fallback shows the menu anyway if the callback is delayed.
        bool leavingRoom = NetworkManagerRTS.IsMultiplayerEnabled &&
                           NetworkManagerRTS.Instance != null;
        if (leavingRoom)
        {
            Debug.Log("[PauseMenu] Leaving Photon room — main menu will show after OnLeftRoom.");
            pendingReturnToMenu = true;
            NetworkManagerRTS.Instance.LeaveRoom();
            StartCoroutine(ShowMenuTimeoutFallback());
        }
        else
        {
            // Single-player / not in a room — clean + show now (no OnLeftRoom).
            MatchSessionManager.CleanupPreviousMatch();
            ShowMainMenuCanvas();
        }

        // Flip multiplayerMode off so a subsequent Single Player click doesn't
        // route through the MP coordinator path. Done AFTER capturing
        // leavingRoom so the branch above is correct.
        if (NetworkManagerRTS.Instance != null)
            NetworkManagerRTS.Instance.multiplayerMode = false;
    }

    private System.Collections.IEnumerator ShowMenuTimeoutFallback()
    {
        yield return new WaitForSecondsRealtime(2.5f);
        if (pendingReturnToMenu)
        {
            pendingReturnToMenu = false;
            Debug.LogWarning("[PauseMenu] OnLeftRoom not received within timeout — " +
                             "showing main menu anyway.");
            MatchSessionManager.CleanupPreviousMatch();   // ensure cleanup ran
            ShowMainMenuCanvas();
        }
    }

    private void ShowMainMenuCanvas()
    {
        if (mainMenuCanvas == null)
            mainMenuCanvas = GameObject.Find("MainMenuCanvas");
        if (mainMenuCanvas != null)
        {
            mainMenuCanvas.SetActive(true);
            // Restore cursor so menu buttons are clickable.
            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;
        }
        else
        {
            Debug.LogWarning("[PauseMenu] MainMenuCanvas not found in scene — " +
                             "cannot show main menu. Run Tools → RTS → Setup → Setup Main Menu.");
        }
    }

    /// <summary>
    /// Quit the application. In the editor we don't actually quit (Unity
    /// would have to exit Play mode), but we log so the path is testable.
    /// </summary>
    public void OnClickQuit()
    {
        Debug.Log("[PauseMenu] Quit pressed.");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
