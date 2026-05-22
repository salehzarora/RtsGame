using UnityEngine;

/// <summary>
/// Stores the player's current resource total.
/// Prints to Console on every change (UI display comes in a later phase).
///
/// Setup:
///   1. Attach to your GameManager GameObject (one per scene).
///   2. No Inspector fields required.
/// </summary>
public class PlayerResourceManager : MonoBehaviour
{
    public int CurrentResources { get; private set; }

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        CurrentResources = 0;
        Debug.Log("[Resources] Starting resources: 0");
    }

    // ------------------------------------------------------------------ //

    /// <summary>Add resources to the player's total. Called by CommandCenter.Deposit.</summary>
    public void AddResources(int amount)
    {
        if (amount <= 0) return;
        CurrentResources += amount;
        Debug.Log($"[Resources] +{amount}  →  Total: {CurrentResources}");
    }

    /// <summary>Returns true if the player currently has at least <paramref name="amount"/> resources.</summary>
    public bool CanAfford(int amount) => CurrentResources >= amount;

    /// <summary>
    /// Attempt to spend resources.
    /// Returns true and deducts the amount. Returns false if insufficient funds.
    /// </summary>
    public bool SpendResources(int amount)
    {
        if (amount <= 0) return true;
        if (CurrentResources < amount)
        {
            Debug.Log($"[Resources] Not enough resources (need {amount}, have {CurrentResources}).");
            return false;
        }

        CurrentResources -= amount;
        Debug.Log($"[Resources] -{amount}  →  Total: {CurrentResources}");
        return true;
    }
}
