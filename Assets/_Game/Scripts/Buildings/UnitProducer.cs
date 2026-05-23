using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Attached to a Barracks to allow producing Soldier units.
///
/// Controls (while the building is selected):
///   S — produce one Soldier (costs soldierCost resources)
///
/// Expandable:
///   The private SpawnUnit(prefab, cost, label) method is the single
///   production core. Adding a new unit type in a future phase is one
///   new key-check + one SpawnUnit call — nothing else changes.
///
/// Setup:
///   1. Add to the Barracks prefab alongside Building and SelectableBuilding.
///   2. Assign soldierPrefab.
///   3. Optionally assign spawnPoint (an empty child Transform placed beside
///      the building). If left empty, soldiers spawn at spawnOffset from the
///      building's world position.
/// </summary>
[RequireComponent(typeof(SelectableBuilding))]
public class UnitProducer : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Soldier Production")]
    [Tooltip("The Soldier prefab to instantiate")]
    public GameObject soldierPrefab;

    [Tooltip("Resource cost per Soldier")]
    public int soldierCost = 50;

    [Tooltip("Keyboard shortcut to produce a Soldier (default S)")]
    public KeyCode produceKey = KeyCode.S;

    [Header("Spawn Location")]
    [Tooltip("Explicit spawn point (child Transform). Leave empty to use spawnOffset.")]
    public Transform spawnPoint;

    [Tooltip("World-space offset from this building used when spawnPoint is not assigned")]
    public Vector3 spawnOffset = new Vector3(4f, 0f, 0f);

    [Tooltip("Max distance NavMesh.SamplePosition searches for a walkable surface near the spawn point")]
    public float navMeshSnapRadius = 5f;

    // ------------------------------------------------------------------ //
    // Private
    // ------------------------------------------------------------------ //

    private SelectableBuilding selectableBuilding;
    private PlayerResourceManager resourceManager;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        selectableBuilding = GetComponent<SelectableBuilding>();
        resourceManager    = FindAnyObjectByType<PlayerResourceManager>();

        if (resourceManager == null)
            Debug.LogError($"UnitProducer on '{name}': No PlayerResourceManager found in scene.");
    }

    // Input is driven by UnitSelector (single, centralized place that knows the
    // currently selected building). UnitSelector calls ProduceSoldier() directly,
    // which prevents double-firing if multiple producers are in the scene.

    /// <summary>Public entry point for the Soldier hotkey. Called by UnitSelector.</summary>
    public void ProduceSoldier()
    {
        SpawnUnit(soldierPrefab, soldierCost, "Soldier");
    }

    // Future unit types: add another public Produce* method here that calls SpawnUnit
    // with their prefab/cost, and add a matching key-check in UnitSelector.

    // ------------------------------------------------------------------ //
    // Core production method — reused for every future unit type
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Validates resources, finds a walkable spawn position, and instantiates
    /// <paramref name="prefab"/>. Logs success and failure to the Console.
    /// </summary>
    private void SpawnUnit(GameObject prefab, int cost, string unitLabel)
    {
        // --- Validation ---------------------------------------------------

        if (prefab == null)
        {
            Debug.LogError($"[UnitProducer] {unitLabel} prefab is not assigned on '{name}'. " +
                           "Drag the prefab into the Inspector.");
            return;
        }

        if (resourceManager == null)
        {
            Debug.LogError($"[UnitProducer] Cannot produce {unitLabel}: PlayerResourceManager missing.");
            return;
        }

        // Check power before spending resources
        PowerConsumer power = GetComponent<PowerConsumer>();
        if (power != null && !power.IsPowered)
        {
            Debug.LogWarning($"[Power] Not enough power. Production paused. " +
                             $"Build a PowerPlant (P) to restore power.");
            return;
        }

        if (!resourceManager.CanAfford(cost))
        {
            Debug.LogWarning($"[UnitProducer] Not enough resources to produce {unitLabel}. " +
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
            // Fallback — place at the building and warn; the agent will try to recover
            spawnPos = desired;
            Debug.LogWarning($"[UnitProducer] Could not find NavMesh near spawn point for {unitLabel}. " +
                             $"Placing at {spawnPos:F1}. Check that the NavMesh is baked near the Barracks.");
        }

        // --- Instantiate --------------------------------------------------

        GameObject unit = Instantiate(prefab, spawnPos, Quaternion.identity);
        unit.name = unitLabel;

        // Spend resources after successful instantiation
        resourceManager.SpendResources(cost);

        Debug.Log($"[UnitProducer] {unitLabel} produced at {spawnPos:F1}. " +
                  $"Remaining resources: {resourceManager.CurrentResources}");
    }
}
