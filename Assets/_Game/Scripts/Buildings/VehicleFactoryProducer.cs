using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Vehicle production component for the VehicleFactory building.
///
/// Parallel sibling to UnitProducer (Soldier, Barracks) and
/// CommandCenterProducer (Worker, CommandCenter). Each producer type is
/// scoped to a single building so a structure cannot accidentally advertise
/// production it isn't supposed to support.
///
/// Today it knows one vehicle:
///   • Humvee  — hotkey H, default cost 150
///
/// Adding Tank / Artillery / AA / RocketLauncher later: one new prefab/cost
/// pair + one Produce* method + one CanProduce* property + one HUD button.
///
/// Setup — VehicleFactory:
///   1. Attach this component (with Building + SelectableBuilding + PowerConsumer).
///   2. Drag the Humvee prefab into Humvee Prefab.
///   3. Tune Humvee Cost (default 150).
///   4. Optionally assign Spawn Point; otherwise vehicles spawn at Spawn Offset.
///
/// Production controls (while the VehicleFactory is selected):
///   • Click the Humvee button in the bottom-left production panel
///   • Or press H
///
/// One-click setup: Tools → RTS → Create Vehicle Factory Prefab.
/// </summary>
[RequireComponent(typeof(SelectableBuilding))]
public class VehicleFactoryProducer : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector — Humvee
    // ------------------------------------------------------------------ //

    [Header("Humvee Production")]
    [Tooltip("The Humvee prefab to instantiate")]
    public GameObject humveePrefab;

    [Tooltip("Resource cost per Humvee")]
    public int humveeCost = 150;

    [Tooltip("Keyboard shortcut to produce a Humvee (UnitSelector hardcodes H)")]
    public KeyCode produceHumveeKey = KeyCode.H;

    [Header("Artillery Tank Production")]
    [Tooltip("The Artillery Tank prefab to instantiate")]
    public GameObject artilleryTankPrefab;

    [Tooltip("Resource cost per Artillery Tank")]
    public int artilleryTankCost = 350;

    [Tooltip("Keyboard shortcut to produce an Artillery Tank (UnitSelector hardcodes T)")]
    public KeyCode produceTankKey = KeyCode.T;

    [Header("Missile Launcher Production")]
    [Tooltip("The Missile Launcher prefab — long-range artillery truck with " +
             "MissileLauncherCombat. Created by Tools → RTS → Vehicles → Create " +
             "Missile Launcher Prefab.")]
    public GameObject missileLauncherPrefab;

    [Tooltip("Resource cost per Missile Launcher")]
    public int missileLauncherCost = 1100;

    [Tooltip("Keyboard shortcut to produce a Missile Launcher (UnitSelector hardcodes M)")]
    public KeyCode produceMissileLauncherKey = KeyCode.M;

    [Header("APC Production")]
    [Tooltip("The APC prefab — 8-wheeled armored troop carrier with primary MG + " +
             "short-range AA. Created by Tools → RTS → Vehicles → Create APC Prefab.")]
    public GameObject apcPrefab;

    [Tooltip("Resource cost per APC")]
    public int apcCost = 600;

    [Tooltip("Keyboard shortcut to produce an APC (UnitSelector hardcodes A)")]
    public KeyCode produceAPCKey = KeyCode.A;

    // ------------------------------------------------------------------ //
    // Inspector — Shared spawn location
    // ------------------------------------------------------------------ //

    [Header("Spawn Location")]
    [Tooltip("Explicit spawn point (child Transform). Leave empty to use spawnOffset.")]
    public Transform spawnPoint;

    [Tooltip("World-space offset from this building used when spawnPoint is not assigned")]
    public Vector3 spawnOffset = new Vector3(5f, 0f, 0f);

    [Tooltip("Max distance NavMesh.SamplePosition searches for a walkable surface near the spawn point")]
    public float navMeshSnapRadius = 6f;

    // ------------------------------------------------------------------ //
    // Capability flags — used by the HUD to decide which buttons to show
    // ------------------------------------------------------------------ //

    /// <summary>True when this producer has a Humvee prefab assigned.</summary>
    public bool CanProduceHumvee => humveePrefab != null;

    /// <summary>True when this producer has an Artillery Tank prefab assigned.</summary>
    public bool CanProduceArtilleryTank => artilleryTankPrefab != null;

    /// <summary>True when this producer has a Missile Launcher prefab assigned.</summary>
    public bool CanProduceMissileLauncher => missileLauncherPrefab != null;

    /// <summary>True when this producer has an APC prefab assigned.</summary>
    public bool CanProduceAPC => apcPrefab != null;

    // ------------------------------------------------------------------ //
    // Private
    // ------------------------------------------------------------------ //

    // Phase 3: owner-aware bank lookup via ResourceBank on each spend.
    private GameEntity selfEntity;
    private int OwnerId => (selfEntity ?? (selfEntity = GetComponent<GameEntity>())) != null
        ? selfEntity.ownerPlayerId : 0;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        selfEntity = GetComponent<GameEntity>();
    }

    // ------------------------------------------------------------------ //
    // Public — called by UnitSelector / RTSHUD
    // ------------------------------------------------------------------ //

    /// <summary>Spawn one Humvee. No-op (logs info) if no Humvee prefab is assigned.</summary>
    public void ProduceHumvee()
    {
        if (!CanProduceHumvee)
        {
            Debug.Log($"[VehicleFactory] '{name}' has no Humvee prefab assigned — ignoring.");
            return;
        }
        SpawnUnit(humveePrefab, humveeCost, "Humvee");
    }

    /// <summary>Spawn one Artillery Tank. No-op (logs info) if no Tank prefab is assigned.</summary>
    public void ProduceArtilleryTank()
    {
        if (!CanProduceArtilleryTank)
        {
            Debug.Log($"[VehicleFactory] '{name}' has no Artillery Tank prefab assigned — ignoring.");
            return;
        }
        SpawnUnit(artilleryTankPrefab, artilleryTankCost, "Artillery Tank");
    }

    /// <summary>Spawn one Missile Launcher. No-op (logs info) if no prefab is assigned.</summary>
    public void ProduceMissileLauncher()
    {
        if (!CanProduceMissileLauncher)
        {
            Debug.Log($"[VehicleFactory] '{name}' has no Missile Launcher prefab assigned — ignoring. " +
                      "Run Tools → RTS → Vehicles → Create Missile Launcher Prefab.");
            return;
        }
        SpawnUnit(missileLauncherPrefab, missileLauncherCost, "Missile Launcher");
    }

    /// <summary>Spawn one APC. No-op (logs info) if no prefab is assigned.</summary>
    public void ProduceAPC()
    {
        if (!CanProduceAPC)
        {
            Debug.Log($"[VehicleFactory] '{name}' has no APC prefab assigned — ignoring. " +
                      "Run Tools → RTS → Vehicles → Create APC Prefab.");
            return;
        }
        SpawnUnit(apcPrefab, apcCost, "APC");
    }

    // ------------------------------------------------------------------ //
    // Core production method
    // ------------------------------------------------------------------ //

    private void SpawnUnit(GameObject prefab, int cost, string unitLabel)
    {
        // --- Validation ---------------------------------------------------

        int ownerId = OwnerId;
        PlayerResourceManager bank = ResourceBank.For(ownerId);
        if (bank == null)
        {
            Debug.LogError($"[VehicleFactory] Cannot produce {unitLabel}: " +
                           $"no PlayerResourceManager registered for owner {ownerId}.");
            return;
        }

        // Vehicle factory is a power consumer — block production while the
        // grid is over capacity.
        PowerConsumer power = GetComponent<PowerConsumer>();
        if (power != null && !power.IsPowered)
        {
            Debug.LogWarning($"[Power] Not enough power. {unitLabel} production paused. " +
                             "Build a PowerPlant (P) to restore power.");
            return;
        }

        if (!bank.CanAfford(cost))
        {
            Debug.LogWarning($"[VehicleFactory] Not enough resources to produce {unitLabel}. " +
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
            Debug.LogWarning($"[VehicleFactory] Could not find NavMesh near spawn point for {unitLabel}. " +
                             $"Placing at {spawnPos:F1}. Check that the NavMesh is baked near '{name}'.");
        }

        // --- Instantiate --------------------------------------------------

        GameObject unit = Instantiate(prefab, spawnPos, Quaternion.identity);
        unit.name = unitLabel;

        bank.SpendResources(cost);

        Debug.Log($"[VehicleFactory] {unitLabel} produced by '{name}' at {spawnPos:F1}. " +
                  $"Remaining resources (owner {ownerId}): {bank.CurrentResources}");
    }
}
