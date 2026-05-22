using UnityEngine;

/// <summary>
/// A gatherable resource node in the world.
/// Workers right-click this to start a gather trip.
/// Destroys itself when fully depleted.
///
/// Setup:
///   1. Create a cube/sphere in the scene and name it "ResourceNode".
///   2. Set its Layer to "Resource" (add this layer in Project Settings).
///   3. Attach this script.
///   4. Give it a yellow/gold material so it is visually distinct.
///   5. Ensure it has a Collider (default Box/Sphere collider is fine).
/// </summary>
public class ResourceNode : MonoBehaviour
{
    [Header("Resource Settings")]
    [Tooltip("Total resources this node starts with")]
    public int maxResources = 200;

    [Tooltip("How much a worker collects per trip")]
    public int gatherAmountPerTrip = 20;

    // ------------------------------------------------------------------ //

    public int CurrentResources { get; private set; }

    public bool IsDepleted => CurrentResources <= 0;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        CurrentResources = maxResources;
    }

    // ------------------------------------------------------------------ //

    /// <summary>
    /// Called by a WorkerGatherer to collect one trip's worth of resources.
    /// Returns the actual amount gathered (may be less on the final trip).
    /// Destroys the node when fully depleted.
    /// </summary>
    public int Gather()
    {
        if (IsDepleted) return 0;

        int amount = Mathf.Min(gatherAmountPerTrip, CurrentResources);
        CurrentResources -= amount;

        if (IsDepleted)
        {
            Debug.Log($"[ResourceNode] {name} fully depleted.");
            Destroy(gameObject);
        }

        return amount;
    }
}
