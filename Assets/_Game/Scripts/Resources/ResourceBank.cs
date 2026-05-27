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

    /// <summary>
    /// Authoritatively set the owner's current resources to
    /// <paramref name="amount"/>. Used at MatchStart to apply the host-
    /// selected starting balance from the room property.
    ///
    /// Owner-strict (Phase 10.2). Does NOT use <see cref="For"/>'s
    /// any-bank fallback — that fallback could route Player 1's
    /// starting-resources write into Player 0's bank if Player 1's bank
    /// hadn't woken up yet (e.g. when GameplayWorldRoot still has
    /// Player1Base inactive). Resolution order:
    ///
    ///   1. <see cref="s_byOwner"/> registration matching <paramref name="ownerId"/>.
    ///   2. Scene scan (INCLUDING inactive objects) for a
    ///      <see cref="PlayerResourceManager"/> whose
    ///      <see cref="PlayerResourceManager.ownerPlayerId"/> matches.
    ///   3. Log a warning and no-op.
    /// </summary>
    public static void SetCurrent(int ownerId, int amount)
    {
        if (s_byOwner.TryGetValue(ownerId, out PlayerResourceManager registered) && registered != null)
        {
            registered.SetResources(amount);
            return;
        }

        // Scene fallback — include inactive so a base still hidden by
        // GameplayWorldRoot is still found. We pick the FIRST manager whose
        // ownerPlayerId field matches; the Awake-time registration that
        // happens later will fold this same instance into s_byOwner.
        PlayerResourceManager[] all = Object.FindObjectsByType<PlayerResourceManager>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null) continue;
            if (all[i].ownerPlayerId != ownerId) continue;
            all[i].SetResources(amount);
            return;
        }

        Debug.LogWarning($"[Resources] SetCurrent({ownerId}, {amount}) — no " +
                         "PlayerResourceManager in scene with matching ownerPlayerId. " +
                         "The MP match map tool should have created one per base; " +
                         "re-run Tools → RTS → Match → Setup Multiplayer Match Map.");
    }

    /// <summary>Hard-clear, used by tests.</summary>
    public static void Clear() => s_byOwner.Clear();
}
