using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds <see cref="GroundAutoAttackController"/> to the ground combat prefabs
/// that should patrol their own radius. Idempotent — re-runs only attach the
/// component when it's missing, leaving any tuned Inspector values alone.
///
/// Menu: Tools → RTS → Units → Add Ground Auto Attack To Prefabs
///
/// Targets (one each):
///   • SoldierPrefab          (Bullet — no anti-air auto-engage)
///   • RPGSoldierPrefab       (Rocket — engages aircraft when they wander into radius)
///   • HumveePrefab           (Bullet — no anti-air auto-engage)
///   • ArtilleryTankPrefab    (Cannon — no anti-air auto-engage)
///   • EnemyRPGSoldierPrefab  (Rocket — engages aircraft when they wander into radius)
///
/// Explicit exclusions (never receives the component):
///   • WorkerPrefab           (worker is a gathering unit, not a combat unit)
///   • StrikeJetPrefab        (aircraft — has its own AirUnitController flow)
///   • Buildings              (Barracks, CommandCenter, PowerPlant, VehicleFactory,
///                             Airfield — out of scope for this feature)
///
/// What it does NOT touch:
///   • Existing UnitCombat / RocketCombat / Health / NavMeshAgent settings.
///   • Inspector values on a controller that's already present (re-running
///     never overwrites tuning).
///   • Prefab GUIDs / scene-instance overrides.
/// </summary>
public static class AddGroundAutoAttackToPrefabs
{
    /// <summary>Prefab names that should receive the auto-attack controller.</summary>
    private static readonly string[] TargetPrefabs =
    {
        "SoldierPrefab",
        "RPGSoldierPrefab",
        "HumveePrefab",
        "ArtilleryTankPrefab",
        "EnemyRPGSoldierPrefab",
    };

    [MenuItem("Tools/RTS/Units/Add Ground Auto Attack To Prefabs")]
    public static void Run()
    {
        Debug.Log("[AddGroundAutoAttack] ── Running ──");

        int added    = 0;
        int already  = 0;
        int migrated = 0;
        int missing  = 0;

        foreach (string prefabName in TargetPrefabs)
        {
            string path = AssetDatabase
                .FindAssets($"{prefabName} t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith($"/{prefabName}.prefab"));

            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning($"[AddGroundAutoAttack]   ⚠ {prefabName}.prefab not found — skipping.");
                missing++;
                continue;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            bool dirty = false;
            try
            {
                // Aircraft fail-safe — if somehow this list ever includes an
                // air unit, the controller would self-disable on Awake. Skip
                // here too so we don't even dirty the prefab.
                UnitCategory cat = root.GetComponent<UnitCategory>();
                if (cat != null && cat.category == UnitCategory.Category.Aircraft)
                {
                    Debug.LogWarning($"[AddGroundAutoAttack]   ⚠ {prefabName} is an Aircraft — skipping.");
                    continue;
                }

                // Migration: EnemyRPGSoldierPrefab was built with the older
                // EnemyAutoAttackController. The new GroundAutoAttackController
                // is a strict superset (team-aware via Health.team flip), so
                // remove the legacy component and let the unified one take over.
                EnemyAutoAttackController legacy = root.GetComponent<EnemyAutoAttackController>();
                if (legacy != null)
                {
                    Object.DestroyImmediate(legacy, allowDestroyingAssets: true);
                    dirty = true;
                    migrated++;
                    Debug.Log($"[AddGroundAutoAttack]   ↻ {prefabName}: removed legacy EnemyAutoAttackController.");
                }

                if (root.GetComponent<GroundAutoAttackController>() != null)
                {
                    if (!dirty)
                    {
                        Debug.Log($"[AddGroundAutoAttack]   = {prefabName}: already has GroundAutoAttackController.");
                        already++;
                        continue;
                    }
                }
                else
                {
                    root.AddComponent<GroundAutoAttackController>();
                    dirty = true;
                    added++;
                    Debug.Log($"[AddGroundAutoAttack]   ✓ {prefabName}: added GroundAutoAttackController.");
                }

                if (dirty)
                    PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[AddGroundAutoAttack] ✓ Done. Added: {added}, migrated-from-legacy: {migrated}, " +
                  $"already-present: {already}, missing: {missing}. " +
                  "Player units now auto-engage enemies that wander into their radius; " +
                  "manual right-click commands still take priority.");
    }
}
