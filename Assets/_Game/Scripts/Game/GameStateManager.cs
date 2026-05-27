using UnityEngine;

/// <summary>
/// Tracks the macro game state — currently just "menu open" vs. "playing".
/// Consumed by gameplay input components (UnitSelector, BuildingPlacementManager,
/// RTS camera if wired) so they skip their Update logic while the main menu
/// is showing.
///
/// Lifecycle:
///   • Awake — state defaults to NOT-started so input is gated.
///   • The main menu calls <see cref="StartGame"/> when the player presses
///     Play, which flips the state and fires <see cref="OnGameStarted"/>.
///
/// Why not just hide the HUD?
///   The HUD is hidden too, but a player can still right-click in the world
///   while a menu is open. Gating the input components keeps unit selection
///   and camera pan disabled until the player finishes the menu.
/// </summary>
[DisallowMultipleComponent]
public class GameStateManager : MonoBehaviour
{
    public static GameStateManager Instance { get; private set; }

    /// <summary>True after <see cref="StartGame"/> has been called.</summary>
    public bool IsGameStarted { get; private set; }

    /// <summary>Fired once when <see cref="StartGame"/> first runs.</summary>
    public event System.Action OnGameStarted;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[GameState] duplicate instance destroyed.");
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ------------------------------------------------------------------ //
    // Convenience static check — null-safe for components that may run
    // before this manager exists (treats "no manager" as "game started" so
    // the prototype keeps working in scenes without the menu).
    // ------------------------------------------------------------------ //

    public static bool IsPlaying => Instance == null || Instance.IsGameStarted;

    // ------------------------------------------------------------------ //

    public void StartGame()
    {
        if (IsGameStarted) return;
        IsGameStarted = true;
        Debug.Log("[GameState] Game started.");
        OnGameStarted?.Invoke();
    }

    /// <summary>
    /// Fired when <see cref="ResetToMenu"/> brings gameplay back to the
    /// pre-match state. Subscribers (UnitSelector, building placement,
    /// HUD) clear their per-match local state in response.
    /// </summary>
    public event System.Action OnGameReset;

    /// <summary>
    /// Brings the game state back to its pre-match condition so that the
    /// main menu can be shown again and a new match can be started later.
    /// Triggered by the ESC pause menu's Return-to-Main-Menu button.
    /// Idempotent — calling twice is safe.
    /// </summary>
    public void ResetToMenu()
    {
        if (!IsGameStarted) return;
        IsGameStarted = false;
        Debug.Log("[GameState] Reset to menu.");
        OnGameReset?.Invoke();
    }
}
