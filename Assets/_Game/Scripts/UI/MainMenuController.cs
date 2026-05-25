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

        if (menuCanvas != null) menuCanvas.SetActive(true);
        if (hudCanvas  != null) hudCanvas.SetActive(false);

        RefreshLabels();
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

        // Apply IMMEDIATELY to any markers already in the scene so the player
        // sees the result without having to press Play first.
        if (PlayerFactionManager.Instance != null)
            PlayerFactionManager.Instance.SetColor(pendingColor, pendingColorName);

        RefreshLabels();
    }

    // ------------------------------------------------------------------ //
    // Play button
    // ------------------------------------------------------------------ //

    public void OnClickPlay()
    {
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

        if (menuCanvas != null) menuCanvas.SetActive(false);
        if (hudCanvas  != null) hudCanvas.SetActive(true);

        if (GameStateManager.Instance != null)
            GameStateManager.Instance.StartGame();
        else
            Debug.LogWarning("[MainMenu] No GameStateManager in scene — gameplay " +
                             "input will be live by default. Run Tools → RTS → Setup → Setup Main Menu.");
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
