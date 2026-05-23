using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Worker production component for the CommandCenter.
///
/// Parallel sibling to UnitProducer (which makes Soldiers on the Barracks):
/// keeping the two component types separate means a single building can only
/// produce the units intended for it — there is no shared field that could
/// accidentally enable cross-production.
///
/// Setup — CommandCenter:
///   1. Attach this component (with CommandCenter + SelectableBuilding) to
///      the CommandCenter GameObject.
///   2. Drag the Worker prefab into Worker Prefab.
///   3. Tune Worker Cost (default 75).
///   4. Optionally assign Spawn Point; otherwise workers spawn at Spawn Offset
///      relative to the CommandCenter.
///
/// Production controls (while the CommandCenter is selected):
///   • Click the Worker button in the bottom-left production panel
///   • Or press W
///
/// One-click setup: Tools → RTS → Setup Command Center configures everything
/// and bootstraps a WorkerPrefab from SoldierPrefab if you don't have one yet.
/// </summary>
[RequireComponent(typeof(SelectableBuilding))]
public class CommandCenterProducer : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Worker Production")]
    [Tooltip("The Worker prefab to instantiate")]
    public GameObject workerPrefab;

    [Tooltip("Resource cost per Worker")]
    public int workerCost = 75;

    [Tooltip("Keyboard shortcut to produce a Worker (UnitSelector hardcodes W)")]
    public KeyCode produceWorkerKey = KeyCode.W;

    [Header("Spawn Location")]
    [Tooltip("Explicit spawn point (child Transform). Leave empty to use spawnOffset.")]
    public Transform spawnPoint;

    [Tooltip("World-space offset from this building used when spawnPoint is not assigned")]
    public Vector3 spawnOffset = new Vector3(4f, 0f, 0f);

    [Tooltip("Max distance NavMesh.SamplePosition searches for a walkable surface near the spawn point")]
    public float navMeshSnapRadius = 5f;

    // ------------------------------------------------------------------ //
    // Capability flag — used by the HUD to decide whether to show the Worker button
    // ------------------------------------------------------------------ //

    /// <summary>True when this producer has a Worker prefab assigned.</summary>
    public bool CanProduceWorker => workerPrefab != null;

    // ------------------------------------------------------------------ //
    // Private
    // ------------------------------------------------------------------ //

    private PlayerResourceManager resourceManager;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        resourceManager = FindAnyObjectByType<PlayerResourceManager>();

        if (resourceManager == null)
            Debug.LogError($"CommandCenterProducer on '{name}': No PlayerResourceManager found in scene.");
    }

    // ------------------------------------------------------------------ //
    // Public — called by UnitSelector / RTSHUD
    // ------------------------------------------------------------------ //

    /// <summary>Spawn one Worker. No-op (logs info) if no Worker prefab is assigned.</summary>
    public void ProduceWorker()
    {
        if (!CanProduceWorker)
        {
            Debug.Log($"[CommandCenter] '{name}' has no Worker prefab assigned — ignoring.");
            return;
        }
        SpawnUnit(workerPrefab, workerCost, "Worker");
    }

    // ------------------------------------------------------------------ //
    // Core production method
    // ------------------------------------------------------------------ //

    private void SpawnUnit(GameObject prefab, int cost, string unitLabel)
    {
        // --- Validation ---------------------------------------------------

        if (resourceManager == null)
        {
            Debug.LogError($"[CommandCenter] Cannot produce {unitLabel}: PlayerResourceManager missing.");
            return;
        }

        // CommandCenter does not consume power today; check anyway in case a
        // PowerConsumer is added later.
        PowerConsumer power = GetComponent<PowerConsumer>();
        if (power != null && !power.IsPowered)
        {
            Debug.LogWarning($"[Power] Not enough power. {unitLabel} production paused. " +
                             "Build a PowerPlant (P) to restore power.");
            return;
        }

        if (!resourceManager.CanAfford(cost))
        {
            Debug.LogWarning($"[CommandCenter] Not enough resources to produce {unitLabel}. " +
                             $"Need {cost}, have {resourceManager.CurrentResources}.");
            return;
        }

        // --- Find spawn position on the NavMesh ---------------------------

        Vector3 desired = spawnPoint != null
            ? spawnPoint.position
            : transform.position + spawnOffset;

        Vector3 spawnPos;
        if (NavMesh.SamplePosition(desired, out NavMeshHit navHit, navMeshSnapRadius, NavMesh.AllAreas))
        {
            spawnPos = navHit.position;
        }
        else
        {
            spawnPos = desired;
            Debug.LogWarning($"[CommandCenter] Could not find NavMesh near spawn point for {unitLabel}. " +
                             $"Placing at {spawnPos:F1}. Check that the NavMesh is baked near '{name}'.");
        }

        // --- Instantiate --------------------------------------------------

        GameObject unit = Instantiate(prefab, spawnPos, Quaternion.identity);
        unit.name = unitLabel;

        resourceManager.SpendResources(cost);

        Debug.Log($"[CommandCenter] {unitLabel} produced by '{name}' at {spawnPos:F1}. " +
                  $"Remaining resources: {resourceManager.CurrentResources}");
    }
}
