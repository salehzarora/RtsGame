using UnityEngine;

/// <summary>
/// Stores ONE faction's resource total. In single-player there's exactly one
/// instance (player 0); in multiplayer the match-setup tool also spawns a
/// second instance for player 1 with <see cref="ownerPlayerId"/> = 1.
///
/// Look-up is handled by <see cref="ResourceBank.For"/> — call that instead
/// of <c>FindAnyObjectByType&lt;PlayerResourceManager&gt;</c> so you always
/// get the bank that matches the spending entity's owner.
///
/// Setup:
///   1. Attach to a GameObject in the scene (typically a GameManager or a
///      per-player BaseRoot).
///   2. Set <see cref="ownerPlayerId"/> to the faction id this bank belongs
///      to. Default 0 keeps single-player working without any wiring change.
///   3. Optionally set <see cref="startingResources"/> for non-zero seed.
/// </summary>
public class PlayerResourceManager : MonoBehaviour
{
    [Header("Ownership")]
    [Tooltip("Faction id this bank belongs to. Player = 0, Enemy AI / Player 2 = 1. " +
             "ResourceBank routes producer spends to the matching bank.")]
    public int ownerPlayerId = 0;

    [Header("Starting balance")]
    [Tooltip("Resources this bank holds at scene start. 0 by default (the " +
             "legacy single-player setup).")]
    public int startingResources = 0;

    public int CurrentResources { get; private set; }

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        CurrentResources = Mathf.Max(0, startingResources);

        // Register with the bank service so per-owner lookups resolve.
        ResourceBank.Register(ownerPlayerId, this);

        Debug.Log($"[Resources] Player {ownerPlayerId} starting resources: {CurrentResources}");
    }

    private void OnDestroy()
    {
        ResourceBank.Unregister(ownerPlayerId, this);
    }

    // ------------------------------------------------------------------ //

    /// <summary>Add resources to this bank. Called by CommandCenter.Deposit.</summary>
    public void AddResources(int amount)
    {
        if (amount <= 0) return;
        CurrentResources += amount;
        Debug.Log($"[Resources] Player {ownerPlayerId} +{amount}  →  Total: {CurrentResources}");
    }

    /// <summary>Returns true if this bank currently has at least <paramref name="amount"/>.</summary>
    public bool CanAfford(int amount) => CurrentResources >= amount;

    /// <summary>
    /// Attempt to spend resources. Returns true and deducts the amount.
    /// Returns false (without deducting) if insufficient funds.
    /// </summary>
    public bool SpendResources(int amount)
    {
        if (amount <= 0) return true;
        if (CurrentResources < amount)
        {
            Debug.Log($"[Resources] Player {ownerPlayerId}: not enough " +
                      $"(need {amount}, have {CurrentResources}).");
            return false;
        }

        CurrentResources -= amount;
        Debug.Log($"[Resources] Player {ownerPlayerId} -{amount}  →  Total: {CurrentResources}");
        return true;
    }
}
