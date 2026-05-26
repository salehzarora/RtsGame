using UnityEngine;

/// <summary>
/// Stores the enemy faction's current resource total. Parallel sibling of
/// <see cref="PlayerResourceManager"/> — kept SEPARATE so player and enemy
/// economies never share a counter, and so this script can grow into a
/// "bot bank" (queued building costs, reserved funds, etc.) without
/// touching the player's flow.
///
/// Setup:
///   • Attach to a single scene object — created automatically by
///     <c>Tools → RTS → Match → Setup Clean Match Map</c>.
///   • No Inspector fields.
///
/// What this script intentionally does NOT do:
///   • Drive HUD output. The player HUD only reads PlayerResourceManager.
///   • Spend on the enemy side. Spending hooks land when enemy bot AI lands.
///   • Touch player resources, units, buildings, combat, or any other system.
/// </summary>
[DisallowMultipleComponent]
public class EnemyResourceManager : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Singleton — kept simple. Workers / future bot code resolve via Instance.
    // ------------------------------------------------------------------ //

    public static EnemyResourceManager Instance { get; private set; }

    // ------------------------------------------------------------------ //
    // State
    // ------------------------------------------------------------------ //

    public int CurrentResources { get; private set; }

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        // Phase 3: enemy AI bot is replaced by a human player 1 in multiplayer.
        // Disable the bot's resource bank entirely so the per-player
        // ResourceBank.For(1) lookup goes to PlayerResourceManager's owner-1
        // instance instead of this AI bag of resources.
        if (NetworkManagerRTS.Instance != null && NetworkManagerRTS.Instance.multiplayerMode)
        {
            Debug.Log("[EnemyResources] Disabled — multiplayer mode is on.");
            enabled = false;
            return;
        }

        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("EnemyResourceManager: duplicate instance destroyed.");
            Destroy(this);
            return;
        }
        Instance = this;
        CurrentResources = 0;
        Debug.Log("[EnemyResources] Starting resources: 0");
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ------------------------------------------------------------------ //
    // Public API — called by EnemyCommandCenter.Deposit on each gather trip.
    // ------------------------------------------------------------------ //

    /// <summary>Add resources to the enemy total. Negative / zero amounts are no-ops.</summary>
    public void AddResources(int amount)
    {
        if (amount <= 0) return;
        CurrentResources += amount;
        Debug.Log($"[EnemyResources] +{amount}  →  Total: {CurrentResources}");
    }

    /// <summary>Returns true if the enemy currently has at least <paramref name="amount"/> resources.</summary>
    public bool CanAfford(int amount) => CurrentResources >= amount;

    /// <summary>
    /// Attempt to spend resources. Returns true and deducts on success; returns
    /// false on insufficient funds. Reserved for future enemy bot AI — no
    /// gameplay code calls this today.
    /// </summary>
    public bool SpendResources(int amount)
    {
        if (amount <= 0) return true;
        if (CurrentResources < amount)
        {
            Debug.Log($"[EnemyResources] Not enough resources (need {amount}, have {CurrentResources}).");
            return false;
        }

        CurrentResources -= amount;
        Debug.Log($"[EnemyResources] -{amount}  →  Total: {CurrentResources}");
        return true;
    }
}
