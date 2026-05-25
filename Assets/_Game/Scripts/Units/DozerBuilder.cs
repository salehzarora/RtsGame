using UnityEngine;

/// <summary>
/// Builder behaviour for the Dozer unit. Receives a <see cref="ConstructionSite"/>
/// build order, drives the unit to the site via <see cref="UnitMovement"/>, and
/// once inside <see cref="buildRange"/> feeds construction progress to the site
/// at <see cref="buildSpeedMultiplier"/> × the site's natural speed.
///
/// Hierarchy expected:
///   DozerRoot                  (NavMeshAgent, UnitMovement, SelectableUnit, Health, this)
///
/// What this component does:
///   • Holds the currently-assigned <see cref="ConstructionSite"/> reference.
///   • Pathing-tics: while a site is assigned and the dozer is farther than
///     <see cref="buildRange"/>, calls UnitMovement.MoveTo each scan tick so the
///     dozer keeps re-issuing the destination if it gets bumped off course.
///   • In-range tics: when within <see cref="buildRange"/>, stops moving and
///     calls <see cref="ConstructionSite.AddBuildProgress"/> with deltaTime ×
///     speed multiplier each frame.
///   • Manual move cancel: if the player issues a manual move command via
///     <see cref="ReleaseBuildAssignment"/>, the site is forgotten but NOT
///     destroyed — the player can re-order the dozer back to the site to resume.
///
/// What this component does NOT do:
///   • Combat. The Dozer carries no UnitCombat / RocketCombat / auto-attack
///     controller — it is unarmed.
///   • Build-cost validation. The cost is deducted by
///     <see cref="BuildingPlacementManager"/> at the moment the construction
///     site is placed; from there the cost is sunk and the dozer just builds.
///   • Power-supply validation. PowerPlant supply / PowerConsumer demand are
///     only registered when the final building instantiates after construction
///     completes — see <see cref="ConstructionSite.Complete"/>.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(UnitMovement))]
public class DozerBuilder : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Build Behaviour")]
    [Tooltip("Distance (world units) from the construction site's centre at which the dozer counts " +
             "as 'in build range' and starts contributing progress. Larger values = builds " +
             "from further away (looks lazy but is robust to NavMesh stopping distance jitter).")]
    public float buildRange = 2.5f;

    [Tooltip("Multiplier applied to the construction site's natural build speed. 1 = normal, " +
             "2 = the dozer builds twice as fast. Used later for upgrades.")]
    public float buildSpeedMultiplier = 1f;

    [Tooltip("Master switch — uncheck to disable build behaviour entirely (debug).")]
    public bool canBuild = true;

    [Tooltip("Seconds between path re-issues while approaching the site. Cheap re-issues " +
             "guarantee the dozer keeps moving if it gets bumped or path-stuck.")]
    public float pathRefreshInterval = 0.5f;

    // ------------------------------------------------------------------ //
    // Public runtime state — read by UnitSelector / HUD / debugging
    // ------------------------------------------------------------------ //

    /// <summary>The construction site this dozer is currently building, or null.</summary>
    public ConstructionSite CurrentSite { get; private set; }

    /// <summary>True while the dozer is within <see cref="buildRange"/> of <see cref="CurrentSite"/>.</summary>
    public bool IsBuilding => CurrentSite != null && IsWithinBuildRange(CurrentSite);

    // ------------------------------------------------------------------ //
    // Private runtime
    // ------------------------------------------------------------------ //

    private UnitMovement movement;
    private float        nextPathRefresh;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        movement = GetComponent<UnitMovement>();
    }

    private void Update()
    {
        if (!canBuild || CurrentSite == null) return;

        // Site finished or was destroyed mid-build — clear and bail.
        if (!CurrentSite.IsAlive)
        {
            CurrentSite = null;
            return;
        }

        if (IsWithinBuildRange(CurrentSite))
        {
            // Stop pathing so the dozer settles at the site, then contribute.
            if (movement != null) movement.Stop();
            CurrentSite.AddBuildProgress(Time.deltaTime * buildSpeedMultiplier, this);
        }
        else if (Time.time >= nextPathRefresh)
        {
            // Re-issue the path periodically so we don't get stuck if knocked off course.
            nextPathRefresh = Time.time + pathRefreshInterval;
            if (movement != null) movement.MoveTo(CurrentSite.transform.position);
        }
    }

    // ------------------------------------------------------------------ //
    // Public API — called by BuildingPlacementManager + UnitSelector
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Assigns <paramref name="site"/> as the dozer's current build target and
    /// dispatches an immediate MoveTo. Replaces any prior assignment.
    /// </summary>
    public void AssignBuildOrder(ConstructionSite site)
    {
        if (site == null || !canBuild) return;

        CurrentSite = site;
        site.AssignDozer(this);

        if (movement != null) movement.MoveTo(site.transform.position);
        nextPathRefresh = Time.time + pathRefreshInterval;

        Debug.Log($"[Dozer] '{name}' assigned to build {site.BuildingLabel} at {site.transform.position:F1}.");
    }

    /// <summary>
    /// Forgets the current site (player issued a manual move / attack / etc.).
    /// The site itself is NOT destroyed — the player can right-click it again
    /// to resume building.
    /// </summary>
    public void ReleaseBuildAssignment()
    {
        if (CurrentSite == null) return;

        Debug.Log($"[Dozer] '{name}' released from build order ({CurrentSite.BuildingLabel}).");
        CurrentSite.ReleaseDozer(this);
        CurrentSite = null;
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private bool IsWithinBuildRange(ConstructionSite site)
    {
        Vector3 a = transform.position;
        Vector3 b = site.transform.position;
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return (dx * dx + dz * dz) <= buildRange * buildRange;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.85f, 0.15f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, buildRange);
    }
#endif
}
