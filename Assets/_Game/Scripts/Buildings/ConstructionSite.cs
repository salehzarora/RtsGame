using UnityEngine;

/// <summary>
/// Temporary "foundation" placeholder spawned by <see cref="BuildingPlacementManager"/>
/// when a Dozer is ordered to build a new structure. The site sits inert on the
/// ground (no PowerPlant supply, no PowerConsumer demand, no production) until a
/// <see cref="DozerBuilder"/> drives up and feeds it progress.
///
/// Lifecycle:
///   1. <see cref="BuildingPlacementManager"/> instantiates the site via the
///      <see cref="constructionSitePrefab"/> path, then calls <see cref="Initialise"/>
///      with the final building prefab + cost + label + Dozer reference.
///   2. The assigned Dozer (or any later resume order) calls
///      <see cref="AddBuildProgress"/> each frame while in range.
///   3. When <see cref="currentProgress"/> reaches 1, <see cref="Complete"/>
///      spawns the real building prefab at the same position + rotation and
///      destroys the construction site.
///
/// Visual:
///   • Root GameObject carries this script + a BoxCollider (Building layer).
///   • Child "Foundation" — flat grey cube (sized to the BPM footprint).
///   • Child "ProgressBar" — small world-space cube above the foundation; its
///     X scale is driven from 0 → 1 as construction advances.
///
/// What this script does NOT do:
///   • Block placement of OTHER construction sites — overlap checks in BPM
///     already see the Building-layer collider here.
///   • Register power / demand / production — only the FINAL building does that,
///     after <see cref="Complete"/> swaps it in.
///   • Take damage. Health is intentionally absent: a half-built site cannot
///     be destroyed by enemies in this milestone. Future enemy AI can add a
///     Health component to this prefab and the same flow will still work.
/// </summary>
[DisallowMultipleComponent]
public class ConstructionSite : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector — prefab references
    // ------------------------------------------------------------------ //

    [Header("Visual References (assigned by Create Construction Site Prefab tool)")]
    [Tooltip("Foundation child — flat grey/translucent cube that represents the site footprint. " +
             "Destroyed alongside the site root when construction completes.")]
    public GameObject foundationVisual;

    [Tooltip("Child Transform used to drive the progress bar fill. Its X scale is animated " +
             "from 0 to <see cref=\"progressBarMaxScaleX\"/> as construction advances. " +
             "Tooltips assume the bar is left-anchored via its parent pivot offset.")]
    public Transform progressBarFill;

    [Tooltip("Max world-space X scale of the progress bar fill at 100%. The bar grows from 0 " +
             "to this value. Match to the bar background's X scale set by the prefab tool.")]
    public float progressBarMaxScaleX = 1.6f;

    // ------------------------------------------------------------------ //
    // Public read-only runtime state
    // ------------------------------------------------------------------ //

    /// <summary>Prefab spawned via Instantiate when construction completes.</summary>
    public GameObject FinalBuildingPrefab { get; private set; }

    /// <summary>Cost already deducted by BPM when the site was placed. Stored for logs only.</summary>
    public int BuildCost { get; private set; }

    /// <summary>How many seconds of dozer contact it takes to complete this site.</summary>
    public float BuildTime { get; private set; } = 1f;

    /// <summary>Normalised progress 0..1.</summary>
    public float CurrentProgress { get; private set; }

    /// <summary>True once <see cref="Complete"/> swaps in the final building.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Display label for HUD / Console logs (e.g. "Barracks", "Airfield").</summary>
    public string BuildingLabel { get; private set; } = "Building";

    /// <summary>Read by external code (e.g. <see cref="DozerBuilder"/>) to detect a dead site.</summary>
    public bool IsAlive => this != null && !IsComplete && gameObject != null;

    /// <summary>The dozer currently working on this site, or null if abandoned.</summary>
    public DozerBuilder AssignedDozer { get; private set; }

    // ------------------------------------------------------------------ //
    // Private
    // ------------------------------------------------------------------ //

    // Stored at Initialise so Complete() spawns the building facing the same way the ghost did.
    private Quaternion finalRotation = Quaternion.identity;

    // ------------------------------------------------------------------ //
    // Setup — called by BuildingPlacementManager right after Instantiate
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Wires the site to its final building. Called by
    /// <see cref="BuildingPlacementManager"/> immediately after Instantiate.
    /// </summary>
    public void Initialise(GameObject finalPrefab, int cost, float buildTime, string label, Quaternion rotation)
    {
        FinalBuildingPrefab = finalPrefab;
        BuildCost           = cost;
        BuildTime           = Mathf.Max(0.05f, buildTime);
        BuildingLabel       = string.IsNullOrEmpty(label) ? "Building" : label;
        finalRotation       = rotation;

        // Reset the progress bar visual to zero.
        RefreshProgressVisual();
    }

    // ------------------------------------------------------------------ //
    // Dozer ↔ Site
    // ------------------------------------------------------------------ //

    /// <summary>Records the dozer as the active builder. Idempotent.</summary>
    public void AssignDozer(DozerBuilder dozer)
    {
        AssignedDozer = dozer;
    }

    /// <summary>Clears the assigned dozer (if it matches). Does NOT destroy the site.</summary>
    public void ReleaseDozer(DozerBuilder dozer)
    {
        if (AssignedDozer == dozer)
            AssignedDozer = null;
    }

    /// <summary>
    /// Adds <paramref name="deltaSeconds"/> of dozer contact to this site. When
    /// <see cref="CurrentProgress"/> hits 1, <see cref="Complete"/> fires automatically.
    /// Called by <see cref="DozerBuilder"/> each frame while in range.
    /// </summary>
    public void AddBuildProgress(float deltaSeconds, DozerBuilder dozer)
    {
        if (IsComplete) return;
        if (deltaSeconds <= 0f) return;

        // The first dozer to reach the site is registered as the active builder
        // even if BPM didn't pre-assign one (e.g. resume-by-right-click).
        if (AssignedDozer == null) AssignedDozer = dozer;

        float deltaProgress = deltaSeconds / BuildTime;
        CurrentProgress = Mathf.Clamp01(CurrentProgress + deltaProgress);

        RefreshProgressVisual();

        if (CurrentProgress >= 1f)
            Complete();
    }

    // ------------------------------------------------------------------ //
    // Completion — swap the placeholder for the final building
    // ------------------------------------------------------------------ //

    private void Complete()
    {
        if (IsComplete) return;
        IsComplete = true;

        if (FinalBuildingPrefab == null)
        {
            Debug.LogError($"[Construction] {BuildingLabel} site at {transform.position:F1} has no final prefab " +
                           "— cannot complete. Site destroyed without spawning.");
            Destroy(gameObject);
            return;
        }

        Vector3 pos = transform.position;
        GameObject placed = Instantiate(FinalBuildingPrefab, pos, finalRotation);
        placed.name = BuildingLabel;

        // Ensure placed building is on the Building layer (mirrors BPM behaviour).
        int buildingLayer = LayerMask.NameToLayer("Building");
        if (buildingLayer >= 0)
        {
            placed.layer = buildingLayer;
            foreach (Transform child in placed.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = buildingLayer;
        }

        Debug.Log($"[Construction] {BuildingLabel} complete.");

        // Release the dozer reference so it's free to take a new build order.
        if (AssignedDozer != null)
        {
            AssignedDozer.ReleaseBuildAssignment();
            AssignedDozer = null;
        }

        Destroy(gameObject);
    }

    // ------------------------------------------------------------------ //
    // Visuals
    // ------------------------------------------------------------------ //

    private void RefreshProgressVisual()
    {
        if (progressBarFill == null) return;

        Vector3 s = progressBarFill.localScale;
        s.x = progressBarMaxScaleX * CurrentProgress;
        progressBarFill.localScale = s;

        // Left-anchor trick — keep the left edge fixed by shifting position
        // by (progress - 1) * width * 0.5.
        Vector3 p = progressBarFill.localPosition;
        // The prefab sets the bar's "neutral" position at x = -progressBarMaxScaleX * 0.5
        // so it grows from the LEFT. Re-apply each refresh.
        p.x = -progressBarMaxScaleX * 0.5f + (s.x * 0.5f);
        progressBarFill.localPosition = p;
    }
}
