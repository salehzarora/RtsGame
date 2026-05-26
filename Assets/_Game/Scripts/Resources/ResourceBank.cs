using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Static service that resolves a per-owner <see cref="PlayerResourceManager"/>.
///
/// Single-player: one PlayerResourceManager in the scene (the existing one)
/// — it registers itself for <c>ownerId = 0</c> and that's the only bank.
/// All existing call sites (which used to <c>FindAnyObjectByType</c>) still
/// resolve correctly.
///
/// Multiplayer: <see cref="SetupMultiplayerMatchMap"/> places a second
/// PlayerResourceManager with <c>ownerPlayerId = 1</c>; it registers under
/// that owner, and the producers' new owner-aware spend path routes to the
/// matching bank. Player 0 spends never touch Player 1's pool and vice versa.
///
/// Look-up rule: if a specific owner isn't registered, fall back to any
/// PlayerResourceManager in the scene (for resilience during the transition
/// when older scenes haven't been re-stamped).
/// </summary>
public static class ResourceBank
{
    private static readonly Dictionary<int, PlayerResourceManager> s_byOwner
        = new Dictionary<int, PlayerResourceManager>(2);

    /// <summary>
    /// Registers <paramref name="mgr"/> under <paramref name="ownerId"/>.
    /// Called from <see cref="PlayerResourceManager.Awake"/>.
    /// </summary>
    public static void Register(int ownerId, PlayerResourceManager mgr)
    {
        if (mgr == null) return;
        s_byOwner[ownerId] = mgr;
    }

    /// <summary>Drops the registration if it points to <paramref name="mgr"/>.</summary>
    public static void Unregister(int ownerId, PlayerResourceManager mgr)
    {
        if (s_byOwner.TryGetValue(ownerId, out PlayerResourceManager current) && current == mgr)
            s_byOwner.Remove(ownerId);
    }

    /// <summary>
    /// Returns the bank for <paramref name="ownerId"/>, or the first
    /// PlayerResourceManager found in the scene as a fallback. Null only when
    /// there's no PlayerResourceManager in the scene at all.
    /// </summary>
    public static PlayerResourceManager For(int ownerId)
    {
        if (s_byOwner.TryGetValue(ownerId, out PlayerResourceManager mgr) && mgr != null)
            return mgr;

        // Fallback: any manager in the scene. Maintains backward-compatibility
        // with scenes that pre-date Phase 3 (no ownerId baked yet).
        return Object.FindAnyObjectByType<PlayerResourceManager>();
    }

    // ------------------------------------------------------------------ //
    // Convenience wrappers for the hot path
    // ------------------------------------------------------------------ //

    public static int Current(int ownerId)
        => For(ownerId)?.CurrentResources ?? 0;

    public static bool CanAfford(int ownerId, int amount)
        => For(ownerId)?.CanAfford(amount) ?? false;

    /// <summary>Attempt to spend. Returns true on success.</summary>
    public static bool Spend(int ownerId, int amount)
    {
        PlayerResourceManager m = For(ownerId);
        if (m == null) return false;
        return m.SpendResources(amount);
    }

    public static void Add(int ownerId, int amount)
    {
        PlayerResourceManager m = For(ownerId);
        if (m != null) m.AddResources(amount);
    }

    /// <summary>Hard-clear, used by tests.</summary>
    public static void Clear() => s_byOwner.Clear();
}
