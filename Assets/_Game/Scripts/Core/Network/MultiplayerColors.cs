using UnityEngine;

/// <summary>
/// Static slot → <see cref="Color"/> registry. Keeps the per-player faction
/// colour consistent across BOTH clients in a multiplayer match.
///
/// Why this exists:
///   <see cref="PlayerFactionManager"/> holds ONE local colour (what THIS
///   client picked in the menu). Without intervention, a
///   <see cref="TeamColorMarker"/> would paint every "Player team" unit
///   with the local colour — so Player 0 on Client A would be blue (A's
///   pick) and Player 0 on Client B would be red (B's pick). The user's
///   Test F requirement: Player 0's army must look identical on both
///   clients.
///
/// How it's used:
///   • <see cref="NetworkMatchCoordinator"/> calls <see cref="SetForOwner"/>
///     for each slot when the shared MatchStart event arrives.
///   • <see cref="TeamColorMarker"/> calls <see cref="TryGetForOwner"/> at
///     Awake-and-on-event to paint itself with the slot's colour.
///
/// Single-player: only one slot (0) is registered with the local player's
/// chosen colour. Old TeamColorMarker behaviour preserved.
/// </summary>
public static class MultiplayerColors
{
    private static readonly System.Collections.Generic.Dictionary<int, Color> s_byOwner
        = new System.Collections.Generic.Dictionary<int, Color>(2);

    /// <summary>
    /// Fired when ANY slot's colour changes. Subscribers (TeamColorMarker)
    /// repaint themselves. Cheap to fire — only happens at match start.
    /// </summary>
    public static event System.Action OnColorsChanged;

    /// <summary>
    /// Default fallback when a slot has no registered colour. Phase 4 keeps
    /// the legacy behaviour: blue for the local player when nothing else
    /// is set. <see cref="TeamColorMarker.fallbackColor"/> still wins when
    /// neither this nor PlayerFactionManager has anything.
    /// </summary>
    public static readonly Color DefaultPlayer0Color = new Color(0.20f, 0.55f, 1.00f); // blue
    public static readonly Color DefaultPlayer1Color = new Color(0.92f, 0.20f, 0.20f); // red

    /// <summary>
    /// Register <paramref name="color"/> under <paramref name="ownerId"/>.
    /// Idempotent — same colour twice is a no-op. Fires
    /// <see cref="OnColorsChanged"/> exactly once on any actual change.
    /// </summary>
    public static void SetForOwner(int ownerId, Color color)
    {
        if (s_byOwner.TryGetValue(ownerId, out Color existing) && existing == color)
            return;
        s_byOwner[ownerId] = color;
        Debug.Log($"[MultiplayerColors] Slot {ownerId} → " +
                  $"RGB({color.r:F2},{color.g:F2},{color.b:F2})");
        OnColorsChanged?.Invoke();
    }

    /// <summary>True if a colour is registered for <paramref name="ownerId"/>.</summary>
    public static bool HasForOwner(int ownerId) => s_byOwner.ContainsKey(ownerId);

    /// <summary>
    /// Out-parameter lookup. Returns true and writes the colour on hit;
    /// returns false (color = Color.clear) on miss. Used by
    /// <see cref="TeamColorMarker"/> so it can fall back to PlayerFactionManager
    /// for the single-player path.
    /// </summary>
    public static bool TryGetForOwner(int ownerId, out Color color)
    {
        return s_byOwner.TryGetValue(ownerId, out color);
    }

    /// <summary>
    /// Returns the colour for <paramref name="ownerId"/>, or the appropriate
    /// default if nothing is registered. Convenience for code paths that
    /// don't care about the registered/default distinction.
    /// </summary>
    public static Color ForOwnerOrDefault(int ownerId)
    {
        if (s_byOwner.TryGetValue(ownerId, out Color c)) return c;
        return ownerId == 1 ? DefaultPlayer1Color : DefaultPlayer0Color;
    }

    /// <summary>Hard-clear, used by tests / scene reloads.</summary>
    public static void Clear()
    {
        s_byOwner.Clear();
        OnColorsChanged?.Invoke();
    }
}
