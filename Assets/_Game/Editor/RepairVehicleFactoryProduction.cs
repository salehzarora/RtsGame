using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click repair for Vehicle Factory production + the damage-category system.
///
/// Menu: Tools → RTS → Repair Vehicle Factory Production
///
/// What it does (idempotent — safe to re-run):
///   1. Ensures HumveePrefab and ArtilleryTankPrefab assets exist; runs their
///      respective builders if missing.
///   2. Loads VehicleFactoryPrefab and wires BOTH outputs into its
///      VehicleFactoryProducer (humveePrefab + artilleryTankPrefab) with the
///      spec'd costs (150 / 350) if either is unassigned. Existing user
///      assignments are preserved.
///   3. Stamps every standard prefab with the correct UnitCategory:
///         SoldierPrefab          → Infantry
///         WorkerPrefab           → Infantry
///         HumveePrefab           → Vehicle
///         ArtilleryTankPrefab    → Vehicle
///         Barracks               → Building
///         PowerPlantPrefab       → Building
///         VehicleFactoryPrefab   → Building
///      Existing UnitCategory components are updated in place (no duplicate).
///   4. Patches the Soldier's UnitCombat cooldown to the spec value 0.5s
///      (older builds set 0.4).
///
/// What it does NOT touch:
///   - HUD / RTSHUD (use Tools → RTS → Setup HUD).
///   - BuildingPlacementManager scene wiring (use
///     Tools → RTS → Repair Prefabs And Building Placement).
///   - CommandCenter scene object (it's a scene-only object, not a prefab —
///     add UnitCategory(Building) manually if Enemy AI starts attacking it).
/// </summary>
public static class RepairVehicleFactoryProduction
{
    [MenuItem("Tools/RTS/Repair Vehicle Factory Production")]
    public static void Repair()
    {
        Debug.Log("[RepairVehicleFactoryProduction] ── Running ──");

        // ── 1. Ensure both vehicle prefabs exist ─────────────────────── //
        GameObject humvee = LoadPrefab("HumveePrefab");
        if (humvee == null)
        {
            Debug.Log("[RepairVehicleFactoryProduction]   HumveePrefab missing — running Humvee builder.");
            CreateHumveePrefab.Create();
            humvee = LoadPrefab("HumveePrefab");
        }

        GameObject tank = LoadPrefab("ArtilleryTankPrefab");
        if (tank == null)
        {
            Debug.Log("[RepairVehicleFactoryProduction]   ArtilleryTankPrefab missing — running Tank builder.");
            CreateArtilleryTankPrefab.Create();
            tank = LoadPrefab("ArtilleryTankPrefab");
        }

        // ── 2. Wire VehicleFactoryProducer ───────────────────────────── //
        GameObject vf = LoadPrefab("VehicleFactoryPrefab");
        if (vf == null)
        {
            Debug.LogWarning("[RepairVehicleFactoryProduction] ⚠ VehicleFactoryPrefab missing. Run " +
                             "Tools → RTS → Create Vehicle Factory Prefab, then re-run this repair.");
        }
        else
        {
            string path = AssetDatabase.GetAssetPath(vf);
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                VehicleFactoryProducer vfp = root.GetComponent<VehicleFactoryProducer>();
                if (vfp == null) vfp = root.AddComponent<VehicleFactoryProducer>();

                if (vfp.humveePrefab == null && humvee != null)
                {
                    vfp.humveePrefab = humvee;
                    Debug.Log("[RepairVehicleFactoryProduction]   ✓ Assigned humveePrefab.");
                }
                if (vfp.humveeCost <= 0) vfp.humveeCost = 150;

                if (vfp.artilleryTankPrefab == null && tank != null)
                {
                    vfp.artilleryTankPrefab = tank;
                    Debug.Log("[RepairVehicleFactoryProduction]   ✓ Assigned artilleryTankPrefab.");
                }
                if (vfp.artilleryTankCost <= 0) vfp.artilleryTankCost = 350;

                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[RepairVehicleFactoryProduction] ✓ VehicleFactoryPrefab wired " +
                          $"(humvee {(vfp.humveePrefab!=null?"OK":"NULL")}, tank {(vfp.artilleryTankPrefab!=null?"OK":"NULL")}).");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // ── 3. UnitCategory on every standard prefab ───────────────── //
        StampCategory("SoldierPrefab",        UnitCategory.Category.Infantry);
        StampCategory("WorkerPrefab",         UnitCategory.Category.Infantry);
        StampCategory("HumveePrefab",         UnitCategory.Category.Vehicle);
        StampCategory("ArtilleryTankPrefab",  UnitCategory.Category.Vehicle);
        StampCategory("Barracks",             UnitCategory.Category.Building);
        StampCategory("PowerPlantPrefab",     UnitCategory.Category.Building);
        StampCategory("VehicleFactoryPrefab", UnitCategory.Category.Building);

        // ── 4. Soldier cooldown to spec (0.5) ────────────────────────── //
        PatchSoldierCooldown();

        AssetDatabase.SaveAssets();
        Debug.Log("[RepairVehicleFactoryProduction] ✓ Done. Re-run Tools → RTS → Setup HUD if " +
                  "the Artillery Tank button is missing from the production panel.");
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static GameObject LoadPrefab(string baseName)
    {
        string path = AssetDatabase.FindAssets($"{baseName} t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith($"/{baseName}.prefab"));
        if (string.IsNullOrEmpty(path)) return null;
        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    private static void StampCategory(string prefabName, UnitCategory.Category cat)
    {
        GameObject prefab = LoadPrefab(prefabName);
        if (prefab == null)
        {
            Debug.LogWarning($"[RepairVehicleFactoryProduction] ⚠ Prefab '{prefabName}' not found — skipping category.");
            return;
        }

        string path = AssetDatabase.GetAssetPath(prefab);
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            UnitCategory uc = root.GetComponent<UnitCategory>();
            if (uc == null) uc = root.AddComponent<UnitCategory>();

            if (uc.category != cat)
            {
                uc.category = cat;
                Debug.Log($"[RepairVehicleFactoryProduction]   ✓ {prefabName} → UnitCategory.{cat}.");
            }

            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    private static void PatchSoldierCooldown()
    {
        GameObject soldier = LoadPrefab("SoldierPrefab");
        if (soldier == null) return;

        string path = AssetDatabase.GetAssetPath(soldier);
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            UnitCombat uc = root.GetComponent<UnitCombat>();
            if (uc == null) return;

            // Only nudge when noticeably off-spec — leaves intentional tunings alone.
            if (!Mathf.Approximately(uc.attackCooldown, 0.5f))
            {
                Debug.Log($"[RepairVehicleFactoryProduction]   Adjusting Soldier attackCooldown " +
                          $"{uc.attackCooldown:F2} → 0.50 (spec value).");
                uc.attackCooldown = 0.5f;
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }
}
