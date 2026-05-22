using UnityEngine;

/// <summary>
/// Applies a team color to a unit's main body renderer on Start.
/// Automatically detects Player / Worker / Enemy based on Health.team
/// and the presence of WorkerGatherer.
///
/// Setup:
///   Add to EVERY unit capsule (player, worker, enemy).
///   Tune the three color fields once — they are shared defaults and can be
///   overridden per-unit if you need variation.
///
///   Do NOT add to the CommandCenter or ResourceNode — they have their own materials.
/// </summary>
[RequireComponent(typeof(Health))]
public class UnitColorMarker : MonoBehaviour
{
    [Header("Team Colors")]
    [Tooltip("Color for standard combat units (Team = Player, no WorkerGatherer)")]
    [ColorUsage(false)] public Color playerColor = new Color(0.20f, 0.48f, 1.00f);  // steel blue

    [Tooltip("Color for worker units (Team = Player, has WorkerGatherer)")]
    [ColorUsage(false)] public Color workerColor = new Color(0.25f, 0.78f, 0.32f);  // bright green

    [Tooltip("Color for enemy units (Team = Enemy)")]
    [ColorUsage(false)] public Color enemyColor  = new Color(0.90f, 0.18f, 0.18f);  // red

    [Header("Options")]
    [Tooltip("If true, auto-detects workers by checking for WorkerGatherer component")]
    public bool autoDetectWorker = true;

    // ------------------------------------------------------------------ //

    private void Start()
    {
        // Determine correct color
        Health health = GetComponent<Health>();
        Color chosen;

        if (health.team == Health.Team.Enemy)
        {
            chosen = enemyColor;
        }
        else if (autoDetectWorker && GetComponent<WorkerGatherer>() != null)
        {
            chosen = workerColor;
        }
        else
        {
            chosen = playerColor;
        }

        // Apply to the first renderer on this GameObject
        // (the Capsule's own MeshRenderer, not the SelectionCircle child)
        Renderer r = GetComponent<Renderer>();
        if (r != null)
        {
            r.material.color = chosen;
            return;
        }

        // Fallback: search children if no renderer on root (non-standard setups)
        r = GetComponentInChildren<Renderer>();
        if (r != null)
            r.material.color = chosen;
        else
            Debug.LogWarning($"UnitColorMarker on {name}: no Renderer found.");
    }
}
