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

    // Cached visuals/colliders so a depleted node can be HIDDEN (not destroyed)
    // and later restored on a new match. Destroying scene-baked nodes was the
    // cause of "resources missing for one client" — a destroyed scene object
    // can't come back for the non-restarting client.
    private Renderer[] renderers;
    private Collider[] colliders;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        CurrentResources = maxResources;
        renderers = GetComponentsInChildren<Renderer>(true);
        colliders = GetComponentsInChildren<Collider>(true);
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
            Debug.Log($"[ResourceNode] {name} fully depleted — hidden (not destroyed) so it " +
                      "can be reset for the next match.");
            SetVisible(false);
        }

        return amount;
    }

    // ------------------------------------------------------------------ //
    // Match-scoped reset (Bug 1 fix)
    // ------------------------------------------------------------------ //

    private void SetVisible(bool visible)
    {
        if (renderers != null)
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null) renderers[i].enabled = visible;
        if (colliders != null)
            for (int i = 0; i < colliders.Length; i++)
                if (colliders[i] != null) colliders[i].enabled = visible;
    }

    /// <summary>
    /// Restore this node to a full, visible, gatherable state for a new match.
    /// Idempotent.
    /// </summary>
    public void ResetForNewMatch()
    {
        CurrentResources = maxResources;
        SetVisible(true);
    }

    /// <summary>
    /// Reset EVERY resource node in the scene to fresh (full + visible) for a
    /// new match — including inactive ones (bases hidden by GameplayWorldRoot).
    /// Called from match cleanup so resource state never leaks between rooms.
    /// </summary>
    public static void ResetAllForNewMatch()
    {
        ResourceNode[] all = FindObjectsByType<ResourceNode>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null) all[i].ResetForNewMatch();
        Debug.Log($"[ResourceNode] Reset {all.Length} node(s) to fresh for new match.");
    }
}
