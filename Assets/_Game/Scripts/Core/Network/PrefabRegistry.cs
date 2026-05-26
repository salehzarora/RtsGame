using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Static registry mapping <see cref="GameEntity.prefabTypeId"/> strings to
/// the prefab asset (GameObject) so future network code can spawn arbitrary
/// types from a serialised command without baking prefab references into
/// every system.
///
/// Phase 2 scope: this is a forward-looking placeholder. Today's spawn paths
/// (UnitProducer, VehicleFactoryProducer, Airfield, BuildingPlacementManager,
/// APCTransport) still hold direct prefab references because each system
/// only ever spawns one or two types. The registry is populated by
/// <see cref="PrefabRegistryBootstrapper"/> at scene boot and is available
/// to any code that needs <c>Lookup("Soldier")</c>.
///
/// Networking Phase 3 will use this registry to drive a generic remote
/// "spawn entity X" command path for things that don't originate from a
/// player-issued command (drops, abilities, etc.).
/// </summary>
public static class PrefabRegistry
{
    private static readonly Dictionary<string, GameObject> s_byTypeId
        = new Dictionary<string, GameObject>(32);

    /// <summary>
    /// Register <paramref name="prefab"/> under <paramref name="prefabTypeId"/>.
    /// Logs a warning if the id is already registered with a different prefab.
    /// </summary>
    public static void Register(string prefabTypeId, GameObject prefab)
    {
        if (string.IsNullOrEmpty(prefabTypeId))
        {
            Debug.LogWarning("[PrefabRegistry] Register called with empty prefabTypeId — ignoring.");
            return;
        }
        if (prefab == null)
        {
            Debug.LogWarning($"[PrefabRegistry] Register('{prefabTypeId}') called with null prefab — ignoring.");
            return;
        }

        if (s_byTypeId.TryGetValue(prefabTypeId, out GameObject existing) && existing != prefab)
        {
            Debug.LogWarning($"[PrefabRegistry] '{prefabTypeId}' already mapped to '{existing.name}'; " +
                             $"overwriting with '{prefab.name}'.");
        }

        s_byTypeId[prefabTypeId] = prefab;
    }

    /// <summary>
    /// Returns the prefab registered under <paramref name="prefabTypeId"/>,
    /// or null if none is registered. Logs a warning on miss so unknown ids
    /// surface during testing.
    /// </summary>
    public static GameObject Lookup(string prefabTypeId)
    {
        if (string.IsNullOrEmpty(prefabTypeId)) return null;
        if (s_byTypeId.TryGetValue(prefabTypeId, out GameObject prefab) && prefab != null)
            return prefab;

        Debug.LogWarning($"[PrefabRegistry] No prefab registered for typeId '{prefabTypeId}'. " +
                         "Add it to PrefabRegistryBootstrapper.");
        return null;
    }

    /// <summary>Hard-clears every registration. Used by tests.</summary>
    public static void Clear() => s_byTypeId.Clear();

    /// <summary>True if <paramref name="prefabTypeId"/> has a non-null mapping.</summary>
    public static bool Has(string prefabTypeId)
        => !string.IsNullOrEmpty(prefabTypeId)
           && s_byTypeId.TryGetValue(prefabTypeId, out GameObject p)
           && p != null;

    /// <summary>Snapshot of registered ids — for diagnostics.</summary>
    public static IEnumerable<string> AllIds() => s_byTypeId.Keys;
}

/// <summary>
/// Scene-side bootstrapper that registers the standard RTS prefabs into
/// <see cref="PrefabRegistry"/> on scene start. Add this component to the
/// GameManager (the editor tool Tools → RTS → Multiplayer Prep → Setup
/// Prefab Registry creates one for you). Inspector fields are nullable —
/// register only the prefabs your scene actually uses.
/// </summary>
[DisallowMultipleComponent]
public class PrefabRegistryBootstrapper : MonoBehaviour
{
    [Header("Player units")]
    public GameObject worker;
    public GameObject dozer;
    public GameObject soldier;
    public GameObject rpgSoldier;
    public GameObject humvee;
    public GameObject artilleryTank;
    public GameObject missileLauncher;
    public GameObject apc;
    public GameObject strikeJet;

    [Header("Player buildings")]
    public GameObject barracks;
    public GameObject powerPlant;
    public GameObject vehicleFactory;
    public GameObject airfield;
    public GameObject machineGunDefense;
    public GameObject constructionSite;

    [Header("Enemy")]
    public GameObject enemyDozer;
    public GameObject enemyRPGSoldier;
    public GameObject enemyBarracks;
    public GameObject enemyPowerPlant;
    public GameObject enemyMachineGunDefense;

    private void Awake()
    {
        int registered = 0;

        registered += TryRegister("Worker",          worker);
        registered += TryRegister("Dozer",           dozer);
        registered += TryRegister("Soldier",         soldier);
        registered += TryRegister("RPGSoldier",      rpgSoldier);
        registered += TryRegister("Humvee",          humvee);
        registered += TryRegister("ArtilleryTank",   artilleryTank);
        registered += TryRegister("MissileLauncher", missileLauncher);
        registered += TryRegister("APC",             apc);
        registered += TryRegister("StrikeJet",       strikeJet);

        registered += TryRegister("Barracks",          barracks);
        registered += TryRegister("PowerPlant",        powerPlant);
        registered += TryRegister("VehicleFactory",    vehicleFactory);
        registered += TryRegister("Airfield",          airfield);
        registered += TryRegister("MachineGunDefense", machineGunDefense);
        registered += TryRegister("ConstructionSite",  constructionSite);

        registered += TryRegister("EnemyDozer",             enemyDozer);
        registered += TryRegister("EnemyRPGSoldier",        enemyRPGSoldier);
        registered += TryRegister("EnemyBarracks",          enemyBarracks);
        registered += TryRegister("EnemyPowerPlant",        enemyPowerPlant);
        registered += TryRegister("EnemyMachineGunDefense", enemyMachineGunDefense);

        Debug.Log($"[PrefabRegistry] Bootstrap complete — {registered} prefab(s) registered.");
    }

    private static int TryRegister(string typeId, GameObject prefab)
    {
        if (prefab == null) return 0;
        PrefabRegistry.Register(typeId, prefab);
        return 1;
    }
}
