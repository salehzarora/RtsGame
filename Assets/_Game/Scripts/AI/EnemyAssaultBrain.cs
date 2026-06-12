using UnityEngine;

/// <summary>
/// Lightweight attack-move brain attached AT RUNTIME by <see cref="SkirmishDirector"/>
/// to spawned enemy units (no prefab changes — multiplayer-safe because the
/// director itself is gated to non-MP play).
///
/// Behaviour loop (1 s scan interval, no per-frame work):
///   1. March along assigned waypoints (spawn lane → central crossroads →
///      player base) via UnitMovement.MoveTo.
///   2. Periodic scan: pick the best hostile target in scanRadius using
///      role-based priorities (RPG: Vehicle > Building > Aircraft > Infantry;
///      rifle infantry: Infantry > RPG > Vehicle > Building) with a distance
///      tie-breaker, then SetTarget on RocketCombat/UnitCombat.
///   3. Cover-seek v1: on first acquiring a target, infantry not already in
///      cover hops to the nearest CoverObject within 6 m (10 s cooldown,
///      never overrides an explicit player command — these are AI units).
///   4. When combat goes idle and nothing is in range, resume the march.
///
/// Deliberately simple: deterministic scoring, interval scanning, no global
/// searches, no pathfinding tricks. GroundAutoAttackController still provides
/// its own guard-radius reactions between scans.
/// </summary>
public class EnemyAssaultBrain : MonoBehaviour
{
    [Tooltip("Hostile-search radius per scan. Larger than auto-attack detection " +
             "so assault units push INTO fights rather than strolling past them.")]
    public float scanRadius = 14f;

    [Tooltip("Seconds between target scans. Keep ≥0.5 for perf with many units.")]
    public float scanInterval = 1.0f;

    [Tooltip("Cover hop search distance when first engaging (infantry only).")]
    public float coverSeekRadius = 6f;

    private Vector3[] waypoints = System.Array.Empty<Vector3>();
    private int wpIndex;

    private UnitMovement movement;
    private RocketCombat rocket;
    private UnitCombat   gun;
    private Health       myHealth;
    private UnitCategory myCat;
    private float        nextCoverSeek;
    private Health       currentTarget;

    /// <summary>Assign the march route (called by the director right after spawn).</summary>
    public void SetPath(params Vector3[] points)
    {
        waypoints = points ?? System.Array.Empty<Vector3>();
        wpIndex = 0;
    }

    private void Start()
    {
        movement = GetComponent<UnitMovement>();
        rocket   = GetComponent<RocketCombat>();
        gun      = GetComponent<UnitCombat>();
        myHealth = GetComponent<Health>();
        myCat    = GetComponent<UnitCategory>();
        MarchToCurrentWaypoint();
        InvokeRepeating(nameof(Scan), Random.Range(0.2f, 0.8f), scanInterval);
    }

    private bool CombatIdle =>
        (rocket == null || rocket.IsIdle) && (gun == null || gun.IsIdle);

    private void Scan()
    {
        if (myHealth == null || myHealth.CurrentHealth <= 0f) return;

        // Already fighting a live target → stay on it (no retarget flicker).
        if (!CombatIdle && currentTarget != null && currentTarget.CurrentHealth > 0f)
            return;

        Health best = null;
        float bestScore = float.MinValue;
        foreach (Health h in FindObjectsByType<Health>(FindObjectsSortMode.None))
        {
            if (h == null || h == myHealth || h.team == myHealth.team) continue;
            if (h.CurrentHealth <= 0f) continue;
            float dist = Vector3.Distance(transform.position, h.transform.position);
            if (dist > scanRadius) continue;
            float score = PriorityScore(h) - dist * 0.5f;
            if (score > bestScore) { bestScore = score; best = h; }
        }

        if (best != null)
        {
            if (best != currentTarget)
            {
                currentTarget = best;
                if (rocket != null) rocket.SetTarget(best);
                else if (gun != null) gun.SetTarget(best);
                TrySeekCover();
            }
            return;
        }

        currentTarget = null;
        // Nothing in range — resume the march.
        if (CombatIdle) AdvanceMarch();
    }

    /// <summary>Role-based target value. Higher = preferred.</summary>
    private float PriorityScore(Health h)
    {
        bool isBuilding = h.GetComponent<Building>() != null;
        var cat = h.GetComponent<UnitCategory>();
        bool iAmRpg = rocket != null;

        if (iAmRpg)
        {
            if (cat != null && cat.category == UnitCategory.Category.Vehicle)  return 100f;
            if (isBuilding)                                                    return 80f;
            if (cat != null && cat.category == UnitCategory.Category.Aircraft) return 60f;
            return 40f;   // infantry — only when nothing juicier is near
        }
        // Rifle infantry: shred infantry first, buildings last.
        if (cat != null && cat.category == UnitCategory.Category.Infantry) return 100f;
        if (cat != null && cat.category == UnitCategory.Category.Vehicle)  return 55f;
        if (isBuilding)                                                    return 35f;
        return 50f;
    }

    private void TrySeekCover()
    {
        if (Time.time < nextCoverSeek) return;
        if (myCat == null || myCat.category != UnitCategory.Category.Infantry) return;
        if (CoverObject.TryGetReduction(transform.position, out _)) return; // already covered
        if (CoverObject.FindNearestPoint(transform.position, coverSeekRadius, out Vector3 spot)
            && movement != null)
        {
            movement.MoveTo(spot);
            nextCoverSeek = Time.time + 10f;
        }
    }

    private void MarchToCurrentWaypoint()
    {
        if (movement == null || wpIndex >= waypoints.Length) return;
        movement.MoveTo(waypoints[wpIndex]);
    }

    private void AdvanceMarch()
    {
        if (wpIndex >= waypoints.Length) return;
        if (Vector3.Distance(transform.position, waypoints[wpIndex]) < 4f)
            wpIndex++;
        MarchToCurrentWaypoint();
    }
}
