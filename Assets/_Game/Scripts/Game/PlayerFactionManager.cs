using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Singleton that holds the player's army + team-color selection. Owned by the
/// GameManager and consulted by <see cref="TeamColorMarker"/> components on
/// player-owned units and buildings.
///
/// Lifecycle:
///   • The main menu calls <see cref="SetArmy"/> and <see cref="SetColor"/>
///     before the player clicks Play.
///   • When a prefab carrying <see cref="TeamColorMarker"/> instantiates, its
///     OnEnable registers with this manager and pulls the current color.
///   • If the player ever changes color again (debug, future settings menu),
///     call <see cref="ApplyColorToAllExistingMarkers"/> to re-paint all live
///     player accents.
///
/// What it does NOT do:
///   • Apply colors to Health bars, selection circles, or enemy units —
///     <see cref="TeamColorMarker"/> only ever paints the small accent
///     children explicitly assigned to it.
///   • Persist across scene loads — the manager is a scene singleton; if
///     loading a new scene later, the menu should re-create it.
/// </summary>
[DisallowMultipleComponent]
public class PlayerFactionManager : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    /// <summary>Placeholder army id. Only one army for the prototype, but the
    /// enum + name pair lets later code branch on faction without refactoring.</summary>
    public enum ArmyId { DefaultArmy }

    // ------------------------------------------------------------------ //
    // Singleton
    // ------------------------------------------------------------------ //

    public static PlayerFactionManager Instance { get; private set; }

    // ------------------------------------------------------------------ //
    // Inspector — defaults applied at Awake before the menu writes its choice
    // ------------------------------------------------------------------ //

    [Header("Default Selection")]
    [Tooltip("Army id selected when the menu first opens. Only DefaultArmy exists today.")]
    public ArmyId defaultArmyId = ArmyId.DefaultArmy;

    [Tooltip("Display name for the default army shown in the menu (e.g. 'Default Army').")]
    public string defaultArmyName = "Default Army";

    [Tooltip("Default team color applied if the menu is bypassed or the player " +
             "presses Play without changing the swatch.")]
    [ColorUsage(false)]
    public Color defaultColor = new Color(0.20f, 0.55f, 1.00f);

    [Tooltip("Display name for the default color (e.g. 'Blue').")]
    public string defaultColorName = "Blue";

    // ------------------------------------------------------------------ //
    // Public read-only state
    // ------------------------------------------------------------------ //

    public ArmyId SelectedArmyId    { get; private set; }
    public string SelectedArmyName  { get; private set; }
    public Color  SelectedColor     { get; private set; }
    public string SelectedColorName { get; private set; }

    /// <summary>Fired whenever <see cref="SetColor"/> changes the team color.</summary>
    public event System.Action<Color> OnColorChanged;

    // ------------------------------------------------------------------ //
    // Marker registry — populated by TeamColorMarker.OnEnable / OnDisable
    // ------------------------------------------------------------------ //

    private readonly List<TeamColorMarker> markers = new List<TeamColorMarker>();

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[PlayerFactionManager] duplicate instance destroyed.");
            Destroy(this);
            return;
        }
        Instance = this;

        SelectedArmyId    = defaultArmyId;
        SelectedArmyName  = defaultArmyName;
        SelectedColor     = defaultColor;
        SelectedColorName = defaultColorName;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ------------------------------------------------------------------ //
    // Menu API
    // ------------------------------------------------------------------ //

    public void SetArmy(ArmyId id, string displayName)
    {
        SelectedArmyId   = id;
        SelectedArmyName = string.IsNullOrEmpty(displayName) ? id.ToString() : displayName;
        Debug.Log($"[PlayerFaction] Army selected: {SelectedArmyName}");
    }

    /// <summary>
    /// Sets the team color and immediately re-applies it to every live
    /// <see cref="TeamColorMarker"/>. Safe to call from the main menu in SP.
    ///
    /// Phase 10.9 — MULTIPLAYER GUARD. In multiplayer, color selection
    /// comes from each player's Photon custom properties (set by the
    /// lobby UI) and is broadcast via the MatchStart payload into
    /// <see cref="MultiplayerColors"/>. The local main-menu pick must
    /// not override that — doing so would silently recolour the opponent's
    /// units on this client and was the source of the "APC passengers
    /// sometimes blue instead of red" bug. So in MP this method records
    /// the value (for UI/debug consistency) but skips the global repaint.
    /// </summary>
    public void SetColor(Color color, string displayName)
    {
        SelectedColor     = color;
        SelectedColorName = string.IsNullOrEmpty(displayName) ? "Custom" : displayName;

        if (NetworkManagerRTS.IsMultiplayerEnabled)
        {
            Debug.Log("[Color] Multiplayer mode: using owner color mapping, " +
                      "not main-menu selected color.");
            return;
        }

        Debug.Log($"[PlayerFaction] Color selected: {SelectedColorName}");
        ApplyColorToAllExistingMarkers();
        OnColorChanged?.Invoke(color);
    }

    // ------------------------------------------------------------------ //
    // Marker registration — called by TeamColorMarker
    // ------------------------------------------------------------------ //

    public void Register(TeamColorMarker marker)
    {
        if (marker == null) return;
        if (!markers.Contains(marker))
            markers.Add(marker);
    }

    public void Unregister(TeamColorMarker marker)
    {
        if (marker == null) return;
        markers.Remove(marker);
    }

    /// <summary>Repaints every registered player-side marker. Cheap — small list.</summary>
    public void ApplyColorToAllExistingMarkers()
    {
        // Iterate in reverse so we can drop dead references safely.
        for (int i = markers.Count - 1; i >= 0; i--)
        {
            TeamColorMarker m = markers[i];
            if (m == null) { markers.RemoveAt(i); continue; }
            m.ApplyColor(SelectedColor);
        }
    }

    /// <summary>Alias for <see cref="ApplyColorToAllExistingMarkers"/> — kept
    /// to match the verb-noun naming used elsewhere in the design notes.</summary>
    public void ApplyColorToAllPlayerTeamMarkers() => ApplyColorToAllExistingMarkers();

    // ------------------------------------------------------------------ //
    // Phase 5 — canonical per-owner color lookup
    //
    // Other systems (TeamColorMarker, future minimap blips, scoreboard)
    // should NOT hard-bind to <see cref="SelectedColor"/> in multiplayer —
    // that's the LOCAL client's choice and would recolour the opponent's
    // army on this client. Call this helper instead.
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the canonical colour for <paramref name="ownerPlayerId"/>.
    ///   • Multiplayer (slot registered) → <see cref="MultiplayerColors"/>
    ///     entry for that slot. Identical on every client.
    ///   • Single-player (slot 0, nothing registered) →
    ///     <see cref="SelectedColor"/>.
    ///   • Anything else → the slot's static default.
    /// </summary>
    public static Color GetColorForOwner(int ownerPlayerId)
    {
        if (MultiplayerColors.TryGetForOwner(ownerPlayerId, out Color slotColor))
            return slotColor;

        // Owner 0 in single-player should match the local pick so the
        // existing menu→army colour pipeline keeps working.
        if (ownerPlayerId == 0 && Instance != null)
            return Instance.SelectedColor;

        return MultiplayerColors.ForOwnerOrDefault(ownerPlayerId);
    }

    /// <summary>
    /// Triggers an owner-aware re-paint pass on every registered
    /// <see cref="TeamColorMarker"/>. Internally calls the existing marker
    /// iteration; each marker's <see cref="TeamColorMarker.ApplyColor"/>
    /// uses the slot palette via <see cref="MultiplayerColors"/> first,
    /// falling back to <see cref="SelectedColor"/> in single-player. Useful
    /// after a runtime swap or ownership change.
    /// </summary>
    public static void ApplyColorsToAllByOwner()
    {
        if (Instance != null)
            Instance.ApplyColorToAllExistingMarkers();
    }
}
