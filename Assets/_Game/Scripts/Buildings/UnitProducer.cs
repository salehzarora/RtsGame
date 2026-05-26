using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Soldier production component for the Barracks.
///
/// One responsibility: spawn Soldier units. The CommandCenter has its own
/// component (CommandCenterProducer) for Workers — by design these are
/// separate types so a building cannot accidentally be configured to produce
/// both.
///
/// Setup — Barracks:
///   1. Attach this component (with Building + SelectableBuilding).
///   2. Drag the Soldier prefab into Soldier Prefab.
///   3. Tune Soldier Cost (default 50).
///   4. Optionally assign Spawn Point (a child Transform beside the building);
///      otherwise units spawn at Spawn Offset relative to the building.
///
/// Production controls (while the Barracks is selected):
///   • Click the Soldier button in the bottom-left production panel
///   • Or press S
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

    [Tooltip("Keyboard shortcut to produce a Soldier (UnitSelector hardcodes S)")]
    public KeyCode produceKey = KeyCode.S;

    [Header("RPG Soldier Production")]
    [Tooltip("The RPG Soldier prefab to instantiate. Anti-vehicle / anti-air infantry " +
             "with a rocket launcher.")]
    public GameObject rpgSoldierPrefab;

    [Tooltip("Resource cost per RPG Soldier.")]
    public int rpgSoldierCost = 120;

    [Tooltip("Keyboard shortcut to produce an RPG Soldier (UnitSelector hardcodes R).")]
    public KeyCode produceRPGSoldierKey = KeyCode.R;

    [Header("Spawn Location")]
    [Tooltip("Explicit spawn point (child Transform). Leave empty to use spawnOffset.")]
    public Transform spawnPoint;

    [Tooltip("World-space offset from this building used when spawnPoint is not assigned")]
    public Vector3 spawnOffset = new Vector3(4f, 0f, 0f);

    [Tooltip("Max distance NavMesh.SamplePosition searches for a walkable surface near the spawn point")]
    public float navMeshSnapRadius = 5f;

    // ------------------------------------------------------------------ //
    // Capability flag — used by the HUD to decide whether to show the Soldier button
    // ------------------------------------------------------------------ //

    /// <summary>True when this producer has a Soldier prefab assigned.</summary>
    public bool CanProduceSoldier => soldierPrefab != null;

    /// <summary>True when this producer has an RPG Soldier prefab assigned.</summary>
    public bool CanProduceRPGSoldier => rpgSoldierPrefab != null;

    // ------------------------------------------------------------------ //
    // Private
    // ------------------------------------------------------------------ //

    private SelectableBuilding selectableBuilding;
    // No cached PlayerResourceManager — Phase 3 resources go through
    // ResourceBank.For(owner) so producers always charge the right pool.

    private GameEntity SelfEntity => selfEntityCache != null
        ? selfEntityCache
        : (selfEntityCache = GetComponent<GameEntity>());
    private GameEntity selfEntityCache;

    private int OwnerId => SelfEntity != null ? SelfEntity.ownerPlayerId : 0;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        selectableBuilding = GetComponent<SelectableBuilding>();
    }

    // ------------------------------------------------------------------ //
    // Public — called by UnitSelector / RTSHUD
    // ------------------------------------------------------------------ //

    /// <summary>Spawn one Soldier. No-op (logs info) if no Soldier prefab is assigned.</summary>
    public void ProduceSoldier()
    {
        if (!CanProduceSoldier)
        {
            Debug.Log($"[UnitProducer] '{name}' has no Soldier prefab assigned — ignoring.");
            return;
        }
        SpawnUnit(soldierPrefab, soldierCost, "Soldier");
    }

    /// <summary>
    /// Spawn one RPG Soldier. No-op (logs info) if no RPG Soldier prefab is
    /// assigned — typical for a Barracks that pre-dates this unit. Run
    /// Tools → RTS → Units → Repair Barracks RPG Production to wire it up.
    /// </summary>
    public void ProduceRPGSoldier()
    {
        if (!CanProduceRPGSoldier)
        {
            Debug.Log($"[UnitProducer] '{name}' has no RPG Soldier prefab assigned — ignoring. " +
                      "Run Tools → RTS → Units → Repair Barracks RPG Production.");
            return;
        }
        SpawnUnit(rpgSoldierPrefab, rpgSoldierCost, "RPG Soldier");
        Debug.Log($"[Barracks] Produced RPG Soldier.");
    }

    // ------------------------------------------------------------------ //
    // Core production method
    // ------------------------------------------------------------------ //

    private void SpawnUnit(GameObject prefab, int cost, string unitLabel)
    {
        // --- Validation ---------------------------------------------------

        // Phase 3: resolve THIS producer's owner bank via ResourceBank.
        // Single-player → returns the only PlayerResourceManager in the
        // scene. Multiplayer → returns the bank matching the producer's
        // owner so Player 0 spends never touch Player 1's pool.
        int ownerId = OwnerId;
        PlayerResourceManager bank = ResourceBank.For(ownerId);
        if (bank == null)
        {
            Debug.LogError($"[UnitProducer] Cannot produce {unitLabel}: " +
                           $"no PlayerResourceManager registered for owner {ownerId}.");
            return;
        }

        PowerConsumer power = GetComponent<PowerConsumer>();
        if (power != null && !power.IsPowered)
        {
            Debug.LogWarning($"[Power] Not enough power. Production paused. " +
                             $"Build a PowerPlant (P) to restore power.");
            return;
        }

        if (!bank.CanAfford(cost))
        {
            Debug.LogWarning($"[{name}] Not enough resources to produce {unitLabel}. " +
                             $"Need {cost}, have {bank.CurrentResources} (owner {ownerId}).");
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
            Debug.LogWarning($"[UnitProducer] Could not find NavMesh near spawn point for {unitLabel}. " +
                             $"Placing at {spawnPos:F1}. Check that the NavMesh is baked near '{name}'.");
        }

        // --- Instantiate --------------------------------------------------

        GameObject unit = Instantiate(prefab, spawnPos, Quaternion.identity);
        unit.name = unitLabel;

        bank.SpendResources(cost);

        Debug.Log($"[UnitProducer] {unitLabel} produced by '{name}' at {spawnPos:F1}. " +
                  $"Remaining resources (owner {ownerId}): {bank.CurrentResources}");
    }
}
