using TMPro;
using UnityEngine;

/// <summary>
/// Drives the pre-game main menu. On Awake the gameplay HUD is hidden and the
/// menu canvas is shown; on Play, the menu hides, the HUD appears, the chosen
/// color is committed to <see cref="PlayerFactionManager"/>, and
/// <see cref="GameStateManager.StartGame"/> unlocks gameplay input.
///
/// Hierarchy expected (built automatically by Tools → RTS → Setup → Setup Main Menu):
///   MainMenuCanvas                (Screen Space Overlay)
///     ├── Title                   ("RTS Prototype")
///     ├── ArmyLabel               ("Army: Default Army")
///     ├── ColorLabel              ("Color: Blue")
///     ├── ColorButtons            (Blue / Red / Green / Yellow / Orange / Purple)
///     └── PlayButton              ("Play")
///
/// Wiring done in the editor tool:
///   • Each color button → OnClickColorBlue / Red / Green / Yellow / Orange / Purple
///   • Play button       → OnClickPlay
///
/// What this controller does NOT do:
///   • Spawn the army or start the simulation directly. It only sets state on
///     PlayerFactionManager + GameStateManager. Gameplay systems (UnitSelector,
///     BuildingPlacementManager, etc.) read those managers themselves.
///   • Re-show the menu after the game starts. The first Play press is the
///     last — there is no in-game pause menu yet.
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

    [Header("Labels")]
    [Tooltip("TMP text that displays the currently-selected army.")]
    public TextMeshProUGUI armyLabel;

    [Tooltip("TMP text that displays the currently-selected color name.")]
    public TextMeshProUGUI colorLabel;

    [Header("Defaults")]
    [Tooltip("Color committed to PlayerFactionManager when Play is pressed if " +
             "the player never touches a color button. Should match the menu's " +
             "initial visual highlight.")]
    [ColorUsage(false)] public Color defaultColor = new Color(0.20f, 0.55f, 1.00f);
    public string defaultColorName = "Blue";

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private Color  pendingColor;
    private string pendingColorName;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        // Default selection — overridden by color-button clicks.
        pendingColor     = defaultColor;
        pendingColorName = defaultColorName;

        // Runtime fallback: if the Inspector reference to HUDCanvas got
        // wiped (e.g. Setup Gameplay HUD was re-run AFTER Setup Main Menu
        // and the user didn't re-wire), recover it by scene-name lookup.
        // Without this, the gameplay HUD would stay visible behind the menu.
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

        RefreshLabels();

        Debug.Log("[MainMenu] Boot — main menu shown. Awaiting player input " +
                  $"(default color: {pendingColorName}).");
    }

    // ------------------------------------------------------------------ //
    // Public color-button callbacks — wired by Setup Main Menu
    // ------------------------------------------------------------------ //

    public void OnClickColorBlue()    => SelectColor(new Color(0.20f, 0.55f, 1.00f), "Blue");
    public void OnClickColorRed()     => SelectColor(new Color(0.92f, 0.20f, 0.20f), "Red");
    public void OnClickColorGreen()   => SelectColor(new Color(0.25f, 0.80f, 0.32f), "Green");
    public void OnClickColorYellow()  => SelectColor(new Color(1.00f, 0.85f, 0.18f), "Yellow");
    public void OnClickColorOrange()  => SelectColor(new Color(1.00f, 0.55f, 0.10f), "Orange");
    public void OnClickColorPurple()  => SelectColor(new Color(0.65f, 0.30f, 0.85f), "Purple");

    private void SelectColor(Color c, string label)
    {
        pendingColor     = c;
        pendingColorName = label;

        Debug.Log($"[MainMenu] Color selected: {label}.");

        // Apply IMMEDIATELY to any markers already in the scene so the player
        // sees the result without having to press Play first.
        if (PlayerFactionManager.Instance != null)
            PlayerFactionManager.Instance.SetColor(pendingColor, pendingColorName);

        // Phase 5: push the pick into NetworkManagerRTS. It writes the colour
        // to our Photon custom player properties when we're in a room; if we
        // haven't joined yet, it queues the value and flushes on OnJoinedRoom.
        // The master reads both players' properties when broadcasting the
        // MatchStart event, so each owner ends up with the colour its own
        // player picked.
        if (NetworkManagerRTS.Instance != null)
            NetworkManagerRTS.Instance.SetLocalPlayerColor(pendingColor, pendingColorName);

        RefreshLabels();
    }

    // ------------------------------------------------------------------ //
    // Play button
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

    /// <summary>
    /// Phase 6 — "Online" button on the Main Menu. Hides this menu and shows
    /// the MultiplayerLobbyUI's OnlineMenuPanel. The lobby system handles
    /// everything from Connect → Create/Join → Lobby → Start Match. No
    /// MatchStart fires until the lobby's Start button is clicked.
    /// </summary>
    public void OnClickOnline()
    {
        Debug.Log("[MainMenu] Online clicked.");

        // Make sure multiplayerMode is on so command relay + ownership checks
        // activate. The legacy SP toggle stays in the NetworkManagerRTS
        // Inspector for explicit override; we just flip it on for this session.
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
    /// Single-player Play. Same as the existing <see cref="OnClickPlay"/>.
    /// Kept as a named alias so the SetupMainMenu tool can wire a new
    /// "Single Player" button to this without breaking older scenes whose
    /// existing Play button still calls <see cref="OnClickPlay"/>.
    /// </summary>
    public void OnClickSinglePlayer()
    {
        // Belt-and-braces: make sure MP is OFF for the SP path. Even if the
        // player toggled it on earlier in the session by exploring the lobby
        // and backing out, this forces the SP coordinator path.
        if (NetworkManagerRTS.Instance != null)
            NetworkManagerRTS.Instance.multiplayerMode = false;

        OnClickPlay();
    }

    public void OnClickPlay()
    {
        Debug.Log($"[MainMenu] Play pressed. Committing color={pendingColorName} " +
                  "and Default Army selection.");

        // Commit selection — covers the case where the player pressed Play
        // without touching a color button (manager still gets default applied).
        if (PlayerFactionManager.Instance != null)
        {
            PlayerFactionManager.Instance.SetColor(pendingColor, pendingColorName);
            // SetArmy is a no-op today but ready for future army-id selection.
            PlayerFactionManager.Instance.SetArmy(
                PlayerFactionManager.ArmyId.DefaultArmy, "Default Army");
        }
        else
        {
            Debug.LogWarning("[MainMenu] No PlayerFactionManager in scene — " +
                             "team colors will fall back to per-prefab defaults.");
        }

        // Phase 4: route through NetworkMatchCoordinator. In single-player it
        // fires OnMatchStarted synchronously; in multiplayer it either
        // broadcasts MatchStart (master + room ready) or logs a waiting
        // message and returns false (we stay on the menu).
        if (NetworkMatchCoordinator.Instance != null)
        {
            bool willFire = NetworkMatchCoordinator.Instance.RequestMatchStart(pendingColor);
            if (!willFire)
            {
                // Menu stays open. The coordinator already logged the reason
                // (not in room / waiting for player 2 / waiting for master).
                // OnMatchStarted will fire later via the network event; until
                // then, the player just sees the menu and the debug panel.
                return;
            }
            // willFire == true means OnMatchStarted has either fired
            // synchronously (SP path) or will fire on the same frame from
            // Photon's receive callback (MP master path). HandleMatchStarted
            // takes it from here.
            return;
        }

        // Fallback path — no coordinator in scene (legacy single-player setup).
        // Behaves exactly like before this phase: hide menu, show HUD, start game.
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

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private void RefreshLabels()
    {
        if (armyLabel  != null) armyLabel.text  = "Army: Default Army";
        if (colorLabel != null) colorLabel.text = $"Color: {pendingColorName}";
    }
}
