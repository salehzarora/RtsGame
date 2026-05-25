using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click repair for APC production wiring. Mirrors the structure of
/// <c>RepairVehicleFactoryProduction</c>: ensures the prefab exists, wires it
/// into the VehicleFactoryPrefab on disk, patches in-scene VehicleFactory
/// instances, and triggers a HUD rebuild if the APC button is missing.
///
/// Menu: Tools → RTS → Vehicles → Repair APC Production
///
/// What it does (idempotent — safe to re-run):
///   1. Ensures APCPrefab asset exists; runs <see cref="CreateAPCPrefab.Create"/>
///      if missing.
///   2. Loads VehicleFactoryPrefab and assigns apcPrefab + cost (600) where
///      missing. Preserves existing user values.
///   3. Walks every in-scene <see cref="VehicleFactoryProducer"/> and applies
///      the same assignments so factories already built by a Dozer pick up
///      APC production without being rebuilt.
///   4. Stamps APCPrefab with UnitCategory.Vehicle (if not already set).
///   5. If the in-scene RTSHUD is missing the APC button, re-runs
///      <see cref="SetupRTSHUD.SetupHUD"/> to rebuild the production panel.
///
/// What it does NOT touch:
///   • Humvee / Tank / Missile Launcher production paths.
///   • BuildingPlacementManager scene wiring.
///   • PlayerResourceManager / PowerManager.
/// </summary>
public static class RepairAPCProduction
{
    private const int    ExpectedCost = 600;

    [MenuItem("Tools/RTS/Vehicles/Repair APC Production")]
    public static void Repair()
    {
        Debug.Log("[RepairAPCProduction] ── Running ──");

        // ── 1. Ensure APCPrefab exists ───────────────────────────────── //
        GameObject apc = AssetDatabase.LoadAssetAtPath<GameObject>(CreateAPCPrefab.PrefabPath);
        if (apc == null)
        {
            Debug.Log("[RepairAPCProduction]   APCPrefab missing — creating it.");
            CreateAPCPrefab.Create();
            AssetDatabase.Refresh();
            apc = AssetDatabase.LoadAssetAtPath<GameObject>(CreateAPCPrefab.PrefabPath);
        }

        if (apc == null)
        {
            Debug.LogError("[RepairAPCProduction] ✗ APCPrefab could not be created.");
            return;
        }
        Debug.Log($"[RepairAPCProduction]   ✓ {CreateAPCPrefab.PrefabPath}");

        // ── 2. Wire VehicleFactoryPrefab on disk ─────────────────────── //
        GameObject vfPrefab = LoadPrefab("VehicleFactoryPrefab");
        if (vfPrefab == null)
        {
            Debug.LogWarning("[RepairAPCProduction] ⚠ VehicleFactoryPrefab missing. " +
                             "Run Tools → RTS → Create Vehicle Factory Prefab first.");
        }
        else
        {
            string path = AssetDatabase.GetAssetPath(vfPrefab);
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                VehicleFactoryProducer vfp = root.GetComponent<VehicleFactoryProducer>();
                if (vfp == null)
                {
                    Debug.LogWarning("[RepairAPCProduction]   ⚠ VehicleFactoryPrefab has no " +
                                     "VehicleFactoryProducer. Run Repair Vehicle Factory Production first.");
                }
                else
                {
                    bool dirty = false;
                    if (vfp.apcPrefab != apc)
                    {
                        vfp.apcPrefab = apc;
                        dirty = true;
                        Debug.Log("[RepairAPCProduction]   ✓ Assigned apcPrefab on VehicleFactoryPrefab.");
                    }
                    if (vfp.apcCost <= 0)
                    {
                        vfp.apcCost = ExpectedCost;
                        dirty = true;
                    }

                    if (dirty) PrefabUtility.SaveAsPrefabAsset(root, path);
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // ── 3. Patch in-scene VehicleFactory instances ───────────────── //
        PatchInSceneVehicleFactories(apc);

        // ── 4. Stamp UnitCategory.Vehicle on the APC prefab if missing ── //
        StampCategoryIfMissing(apc);

        // ── 5. HUD button + re-run Setup HUD if missing ──────────────── //
        EnsureHUDHasAPCButton();

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[RepairAPCProduction] ✓ Done. VehicleFactory production now includes APC (600).");
    }

    // ------------------------------------------------------------------ //
    // Scene patches
    // ------------------------------------------------------------------ //

    private static void PatchInSceneVehicleFactories(GameObject apc)
    {
        VehicleFactoryProducer[] inScene =
            Object.FindObjectsByType<VehicleFactoryProducer>(FindObjectsSortMode.None);

        if (inScene.Length == 0)
        {
            Debug.Log("[RepairAPCProduction]   = No in-scene VehicleFactory — skipping scene patch.");
            return;
        }

        int patched = 0;
        foreach (VehicleFactoryProducer vfp in inScene)
        {
            bool dirty = false;
            if (vfp.apcPrefab == null && apc != null) { vfp.apcPrefab = apc; dirty = true; }
            if (vfp.apcCost <= 0) { vfp.apcCost = ExpectedCost; dirty = true; }
            if (dirty)
            {
                EditorUtility.SetDirty(vfp);
                patched++;
            }
        }

        Debug.Log($"[RepairAPCProduction]   ✓ In-scene VehicleFactory instances: " +
                  $"{patched}/{inScene.Length} updated.");
    }

    private static void StampCategoryIfMissing(GameObject prefab)
    {
        string path = AssetDatabase.GetAssetPath(prefab);
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            UnitCategory uc = root.GetComponent<UnitCategory>();
            if (uc == null)
            {
                uc = root.AddComponent<UnitCategory>();
                uc.category = UnitCategory.Category.Vehicle;
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log("[RepairAPCProduction]   ✓ Stamped UnitCategory.Vehicle on APCPrefab.");
            }
            else if (uc.category != UnitCategory.Category.Vehicle)
            {
                uc.category = UnitCategory.Category.Vehicle;
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log("[RepairAPCProduction]   ✓ Reset APCPrefab category to Vehicle.");
            }
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    // ------------------------------------------------------------------ //
    // HUD button check
    // ------------------------------------------------------------------ //

    private static void EnsureHUDHasAPCButton()
    {
        RTSHUD hud = Object.FindAnyObjectByType<RTSHUD>(FindObjectsInactive.Include);
        if (hud == null)
        {
            Debug.LogWarning("[RepairAPCProduction] ⚠ No RTSHUD in the scene — " +
                             "run Tools → RTS → Setup HUD to create one.");
            return;
        }

        if (hud.apcButton != null)
        {
            Debug.Log("[RepairAPCProduction]   = HUD already has the APC button.");
            return;
        }

        Debug.Log("[RepairAPCProduction]   Re-running Tools → RTS → Setup HUD to add the APC button.");
        SetupRTSHUD.SetupHUD();
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
}
