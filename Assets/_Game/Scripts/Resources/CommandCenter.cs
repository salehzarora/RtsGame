using UnityEngine;

/// <summary>
/// The player's resource drop-off building.
/// Workers return here after each gather trip.
///
/// Setup:
///   1. Create a cube in the scene and name it "CommandCenter".
///   2. Scale it to something visible, e.g. (3, 2, 3).
///   3. Attach this script.
///   4. Give it a blue material so workers can navigate toward it visually.
///   5. Make sure a PlayerResourceManager exists in the scene (on GameManager).
///   6. The CommandCenter does NOT need to be on any special layer.
///      Workers navigate to it by direct reference, not by raycast.
/// </summary>
public class CommandCenter : MonoBehaviour
{
    // Auto-found at startup — no need to drag in Inspector
    private PlayerResourceManager resourceManager;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        resourceManager = FindAnyObjectByType<PlayerResourceManager>();

        if (resourceManager == null)
            Debug.LogError("CommandCenter: No PlayerResourceManager found in the scene. " +
                           "Add it to your GameManager.");
    }

    // ------------------------------------------------------------------ //

    /// <summary>
    /// Deposit gathered resources into the player's total.
    /// Called automatically by WorkerGatherer on arrival.
    /// </summary>
    public void Deposit(int amount)
    {
        if (amount <= 0) return;
        resourceManager?.AddResources(amount);
    }
}
