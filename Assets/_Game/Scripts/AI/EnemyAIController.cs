using UnityEngine;

/// <summary>
/// Drives an enemy unit's autonomous behaviour.
///
/// Target priority (re-evaluated every retargetInterval seconds):
///   1. CommandCenter (if it has a Health component on the Player team)
///   2. Closest living Health component on the Player team (units or buildings)
///
/// Uses the existing UnitCombat component for movement + attacking,
/// so no duplicate logic is needed.
///
/// Setup:
///   Add to the EnemySoldier prefab alongside UnitCombat, UnitMovement, and Health.
///   Do NOT add SelectableUnit — enemies are not player-selectable.
/// </summary>
[RequireComponent(typeof(UnitCombat))]
[RequireComponent(typeof(Health))]
public class EnemyAIController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("AI Settings")]
    [Tooltip("How often (seconds) the enemy scans for a new or better target")]
    public float retargetInterval = 2f;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private UnitCombat unitCombat;
    private Health     currentTarget;
    private bool       hasLoggedNoTarget;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        unitCombat = GetComponent<UnitCombat>();
    }

    private void Start()
    {
        // Stagger the scan across all enemies so they don't all call
        // FindObjectsByType on the exact same frame.
        float jitter = Random.Range(0.3f, 1.2f);
        InvokeRepeating(nameof(PeriodicRetarget), jitter, retargetInterval);
    }

    private void Update()
    {
        // React immediately when the current target dies instead of
        // waiting up to retargetInterval seconds.
        if (currentTarget == null && !unitCombat.IsIdle == false)
            return; // already idle, periodic scan will handle it

        if (currentTarget == null && unitCombat.IsIdle)
            PeriodicRetarget();
    }

    // ------------------------------------------------------------------ //
    // Target management
    // ------------------------------------------------------------------ //

    private void PeriodicRetarget()
    {
        Health best = FindBestTarget();

        if (best == null)
        {
            if (!hasLoggedNoTarget)
            {
                Debug.Log($"[EnemyAI] {name}: No player target found — waiting.");
                hasLoggedNoTarget = true;
            }
            currentTarget = null;
            return;
        }

        hasLoggedNoTarget = false;

        // Only update UnitCombat when the target actually changes
        if (best != currentTarget)
        {
            currentTarget = best;
            unitCombat.SetTarget(currentTarget);
            Debug.Log($"[EnemyAI] {name} acquired target: {currentTarget.name}.");
        }
    }

    /// <summary>
    /// Finds the best player-team Health to attack.
    /// Priority: CommandCenter → closest player Health.
    /// </summary>
    private Health FindBestTarget()
    {
        // --- Priority 1: CommandCenter -----------------------------------
        CommandCenter cc = FindAnyObjectByType<CommandCenter>();
        if (cc != null)
        {
            Health ccHealth = cc.GetComponent<Health>();
            if (ccHealth != null && ccHealth.team == Health.Team.Player)
                return ccHealth;
        }

        // --- Priority 2: Closest player-team Health ----------------------
        // This covers player units, workers, and any other building with Health.
        Health[]  allHealth  = FindObjectsByType<Health>(FindObjectsSortMode.None);
        Health    closest    = null;
        float     closestDist = float.MaxValue;

        foreach (Health h in allHealth)
        {
            if (h == null)              continue;
            if (h.team != Health.Team.Player) continue;

            float dist = Vector3.Distance(transform.position, h.transform.position);
            if (dist < closestDist)
            {
                closestDist = dist;
                closest     = h;
            }
        }

        return closest;
    }
}
