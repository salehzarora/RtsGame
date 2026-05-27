using UnityEngine;
using UnityEngine.EventSystems;

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
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (!Input.GetKeyDown(KeyCode.Escape)) return;

        // If we're typing into a UI input field, swallow the press — ESC there
        // usually means "stop editing", not "open pause".
        if (IsTypingInInputField()) return;

        // Gameplay-active gate. ESC during the main menu / lobby / pre-match
        // is ignored so the pause panel doesn't overlap menu UI.
        if (!GameStateManager.IsPlaying) return;

        // If the pause menu is already open, ESC closes it (same as Resume).
        if (menuCanvas != null && menuCanvas.activeSelf)
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
        Debug.Log("[PauseMenu] ESC menu opened.");
    }

    private void HidePauseMenu()
    {
        if (menuCanvas == null) return;
        menuCanvas.SetActive(false);
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

        // 2. Multiplayer cleanup — leave the room first so any in-flight
        //    Photon callbacks fire BEFORE we tear down UI / state. The
        //    coordinator's OnLeftRoom resets IsMatchStarted; that lets the
        //    next match boot fresh.
        if (NetworkManagerRTS.IsMultiplayerEnabled)
        {
            Debug.Log("[PauseMenu] Leaving Photon room.");
            NetworkManagerRTS.Instance.LeaveRoom();
        }
        // Flip multiplayerMode off so a subsequent Single Player click
        // doesn't accidentally route through the MP coordinator path.
        if (NetworkManagerRTS.Instance != null)
            NetworkManagerRTS.Instance.multiplayerMode = false;

        // 3. Clear gameplay UI surfaces.
        UnitSelector selector = UnitSelector.Instance;
        if (selector != null) selector.ClearSelection();

        // 4. Hide gameplay world. GameplayWorldRoot's ReHide deactivates
        //    Player0Base / Player1Base / Environment / etc. so the menu
        //    sits over a clean black canvas.
        GameplayWorldRoot worldRoot = Object.FindFirstObjectByType<GameplayWorldRoot>();
        if (worldRoot != null) worldRoot.ReHide();

        // 5. Hide gameplay HUD.
        if (hudCanvas != null) hudCanvas.SetActive(false);

        // 6. Hide lobby canvas (defensive — usually already hidden).
        if (lobbyCanvas != null) lobbyCanvas.SetActive(false);

        // 7. Reset game state. Future StartGame call will fire OnGameStarted
        //    again so subscribers like HUD bars re-bind.
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.ResetToMenu();

        // 8. Show the main menu canvas.
        if (mainMenuCanvas == null)
            mainMenuCanvas = GameObject.Find("MainMenuCanvas");
        if (mainMenuCanvas != null)
        {
            mainMenuCanvas.SetActive(true);
            // Make sure cursor is visible / unlocked. Some camera-pan modes
            // hide it; restore so the player can click menu buttons.
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
