using UnityEngine;

/// <summary>
/// Drives the pre-game main menu. On Awake the gameplay HUD is hidden and the
/// menu canvas is shown. On Single Player / Online the menu either starts a
/// local match (Single Player) or hands off to the multiplayer lobby UI
/// (Online). On MatchStart the menu hides and the HUD appears.
///
/// Hierarchy expected (built by Tools → RTS → Setup → Setup Main Menu):
///   MainMenuCanvas                (Screen Space Overlay)
///     ├── Background              (full-screen dark panel)
///     ├── Title                   ("RTS Prototype")
///     ├── BtnSinglePlayer         ("Single Player")
///     └── BtnOnline               ("Online")
///
/// Phase 10.9 — the legacy color picker (Blue / Red / Green / Yellow / Orange
/// / Purple buttons + "Army: …" + "Color: …" labels) was REMOVED. Multiplayer
/// army colors now come exclusively from the lobby UI's color selection
/// (which writes to Photon player custom properties) and are broadcast in the
/// MatchStart payload. Letting the main menu pick "Blue" by default was
/// silently overwriting the lobby pick via PlayerFactionManager.SetColor.
/// Single-player still uses PlayerFactionManager's Inspector defaultColor.
/// </summary>
[DisallowMultipleComponent]
public class MainMenuController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector — references assigned by Setup Main Menu
    // ------------------------------------------------------------------ //

    [Header("Canvas References")]
    [Tooltip("The main menu's own Canvas root GameObject. Active on Awake; hidden on Play.")]
    public GameObject menuCanvas;

    [Tooltip("The gameplay HUDCanvas built by Tools → RTS → Setup HUD. " +
             "Hidden on Awake; activated on Play.")]
    public GameObject hudCanvas;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        // Runtime fallback: if the Inspector reference to HUDCanvas got
        // wiped (e.g. Setup Gameplay HUD was re-run AFTER Setup Main Menu
        // and the user didn't re-wire), recover it by scene-name lookup.
        if (hudCanvas == null)
        {
            GameObject found = GameObject.Find("HUDCanvas");
            if (found != null)
            {
                hudCanvas = found;
                Debug.Log("[MainMenu] hudCanvas Inspector ref was missing — recovered " +
                          "by name lookup ('HUDCanvas').");
            }
            else
            {
                Debug.LogWarning("[MainMenu] hudCanvas reference is null and no " +
                                 "GameObject named 'HUDCanvas' was found. The gameplay HUD " +
                                 "won't be hidden while the menu is open. Run " +
                                 "Tools → RTS → Setup → Setup Gameplay HUD to rebuild it.");
            }
        }

        if (menuCanvas != null) menuCanvas.SetActive(true);
        if (hudCanvas  != null) hudCanvas.SetActive(false);

        Debug.Log("[MainMenu] Boot — main menu shown. Awaiting player input.");
        Debug.Log("[MainMenu] Old color selector removed/disabled.");
    }

    // ------------------------------------------------------------------ //
    // MatchStart subscription
    // ------------------------------------------------------------------ //

    private bool matchStartSubscribed;

    private void OnEnable()
    {
        if (!matchStartSubscribed)
        {
            NetworkMatchCoordinator.OnMatchStarted += HandleMatchStarted;
            matchStartSubscribed = true;
        }
    }

    private void OnDisable()
    {
        if (matchStartSubscribed)
        {
            NetworkMatchCoordinator.OnMatchStarted -= HandleMatchStarted;
            matchStartSubscribed = false;
        }
    }

    // ------------------------------------------------------------------ //
    // Buttons
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Phase 6 — "Online" button. Flips multiplayer mode on and opens the
    /// MultiplayerLobbyUI's OnlineMenuPanel. The lobby handles Connect →
    /// Create/Join → Lobby → Start Match; color selection lives there now.
    /// </summary>
    public void OnClickOnline()
    {
        Debug.Log("[MainMenu] Online clicked.");

        if (NetworkManagerRTS.Instance != null)
            NetworkManagerRTS.Instance.multiplayerMode = true;

        if (MultiplayerLobbyUI.Instance == null)
        {
            Debug.LogWarning("[MainMenu] No MultiplayerLobbyUI found in scene. " +
                             "Run Tools → RTS → Multiplayer → Setup Multiplayer Lobby UI " +
                             "(or the All-In-One tool).");
            return;
        }

        Debug.Log("[MainMenu] Opening MultiplayerLobbyUI.");
        MultiplayerLobbyUI.Instance.ShowOnlineMenu();
    }

    /// <summary>
    /// Single-player Play. Forces multiplayerMode = false so the SP
    /// coordinator path runs, then triggers MatchStart.
    /// </summary>
    public void OnClickSinglePlayer()
    {
        if (NetworkManagerRTS.Instance != null)
            NetworkManagerRTS.Instance.multiplayerMode = false;

        OnClickPlay();
    }

    public void OnClickPlay()
    {
        Debug.Log("[MainMenu] Play pressed.");

        // Phase 10.9 — color is NOT pushed from here any more. SP uses
        // PlayerFactionManager's Inspector defaultColor (set at its own
        // Awake). MP uses the lobby's per-player Photon properties read by
        // NetworkMatchCoordinator at MatchStart. Touching SetColor here
        // would overwrite the lobby pick with the main menu's default.

        if (NetworkMatchCoordinator.Instance != null)
        {
            // Pass the PFM default color so the SP fallback path inside the
            // coordinator has SOMETHING to use. The MP path reads colors
            // from Photon player properties and ignores this argument.
            Color spColor = PlayerFactionManager.Instance != null
                ? PlayerFactionManager.Instance.SelectedColor
                : MultiplayerColors.DefaultPlayer0Color;
            bool willFire = NetworkMatchCoordinator.Instance.RequestMatchStart(spColor);
            if (!willFire)
            {
                // Coordinator already logged the reason (not in room / waiting
                // for player 2 / waiting for master). OnMatchStarted will
                // arrive via the network event later.
                return;
            }
            return;
        }

        // Fallback path — no coordinator in scene (legacy single-player setup).
        Debug.LogWarning("[MainMenu] No NetworkMatchCoordinator in scene — " +
                         "using legacy single-player Play flow. Run " +
                         "Tools → RTS → Multiplayer → Setup Network Manager " +
                         "to add it.");
        FinishLocalMatchStart();
    }

    private void HandleMatchStarted()
    {
        Debug.Log("[MainMenu] OnMatchStarted received — completing local match boot.");
        FinishLocalMatchStart();
    }

    private void FinishLocalMatchStart()
    {
        if (menuCanvas != null) menuCanvas.SetActive(false);
        if (hudCanvas  != null) hudCanvas.SetActive(true);

        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.StartGame();
            Debug.Log("[MainMenu] Match starting — gameplay HUD shown, " +
                      "your army is now active on the battlefield.");
        }
        else
        {
            Debug.LogWarning("[MainMenu] No GameStateManager in scene — gameplay " +
                             "input will be live by default. Run Tools → RTS → Setup → Setup Main Menu.");
        }
    }
}
