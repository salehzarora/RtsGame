using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click editor tool that wires the entire Dozer-driven construction
/// system. Safe to re-run — each step checks for existing assets / scene
/// references before creating or modifying anything.
///
/// Menu: Tools → RTS → Construction → Repair Construction System
///
/// What it does:
///   1. Ensures DozerPrefab exists (creates it via CreateDozerPrefab.Create if missing).
///   2. Ensures ConstructionSitePrefab exists (creates it via CreateConstructionSitePrefab.Create if missing).
///   3. Finds the CommandCenter prefab and assigns DozerPrefab to its CommandCenterProducer.
///   4. Finds any CommandCenter in the OPEN SCENE and assigns the same prefab.
///   5. Finds the BuildingPlacementManager in the scene and assigns the
///      ConstructionSite prefab + sets debugInstantBuildEnabled = false.
///
/// What it does NOT touch:
///   • Existing Barracks / PowerPlant / VehicleFactory / Airfield prefabs.
///   • Existing units, resources, power state, HUD.
///   • Any prefab GUIDs (the Create* tools overwrite in place).
/// </summary>
public static class RepairConstructionSystem
{
    private const string DozerPrefabPath          = "Assets/_Game/Prefabs/DozerPrefab.prefab";
    private const string ConstructionSitePath     = "Assets/_Game/Prefabs/ConstructionSitePrefab.prefab";
    private const string CommandCenterPrefabPath  = "Assets/_Game/Prefabs/CommandCenterPrefab.prefab";

    [MenuItem("Tools/RTS/Construction/Repair Construction System")]
    public static void Run()
    {
        Debug.Log("[RepairConstructionSystem] ── Running ──");

        // ── 1. DozerPrefab ──────────────────────────────────────────── //
        GameObject dozerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DozerPrefabPath);
        if (dozerPrefab == null)
        {
            Debug.Log("[RepairConstructionSystem]   DozerPrefab not found — creating it.");
            CreateDozerPrefab.Create();
            AssetDatabase.Refresh();
            dozerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DozerPrefabPath);
        }
        if (dozerPrefab == null)
        {
            Debug.LogError("[RepairConstructionSystem] ✗ DozerPrefab could not be created.");
            return;
        }
        Debug.Log($"[RepairConstructionSystem]   ✓ DozerPrefab at {DozerPrefabPath}");

        // ── 2. ConstructionSitePrefab ───────────────────────────────── //
        GameObject sitePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ConstructionSitePath);
        if (sitePrefab == null)
        {
            Debug.Log("[RepairConstructionSystem]   ConstructionSitePrefab not found — creating it.");
            CreateConstructionSitePrefab.Create();
            AssetDatabase.Refresh();
            sitePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ConstructionSitePath);
        }
        if (sitePrefab == null)
        {
            Debug.LogError("[RepairConstructionSystem] ✗ ConstructionSitePrefab could not be created.");
            return;
        }
        Debug.Log($"[RepairConstructionSystem]   ✓ ConstructionSitePrefab at {ConstructionSitePath}");

        // ── 3. Assign DozerPrefab to the CommandCenter prefab ───────── //
        AssignDozerToCommandCenterPrefab(dozerPrefab);

        // ── 4. Assign DozerPrefab to any in-scene CommandCenter ─────── //
        AssignDozerToInSceneCommandCenters(dozerPrefab);

        // ── 5. Wire BuildingPlacementManager ────────────────────────── //
        WireBuildingPlacementManager(sitePrefab);

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();

        Debug.Log("[RepairConstructionSystem] ── Done. ──\n" +
                  "  • Select CommandCenter → Dozer button (or press D) to produce a Dozer.\n" +
                  "  • Select the Dozer → bottom-left construction panel to start a build.");
    }

    // ------------------------------------------------------------------ //

    private static void AssignDozerToCommandCenterPrefab(GameObject dozerPrefab)
    {
        // The CommandCenter is usually a scene object in this project, not a prefab.
        // We still check this path so future prefab-based setups stay wired.
        GameObject ccPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CommandCenterPrefabPath);
        if (ccPrefab == null)
        {
            Debug.Log($"[RepairConstructionSystem]   = No CommandCenterPrefab at {CommandCenterPrefabPath} — " +
                      "skipping prefab assignment (in-scene CommandCenter will still be wired).");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(CommandCenterPrefabPath);
        try
        {
            CommandCenterProducer ccp = root.GetComponent<CommandCenterProducer>();
            if (ccp == null)
            {
                Debug.LogWarning("[RepairConstructionSystem]   ⚠ CommandCenterPrefab has no CommandCenterProducer — " +
                                 "skipping.");
                return;
            }

            if (ccp.dozerPrefab != dozerPrefab)
            {
                ccp.dozerPrefab = dozerPrefab;
                PrefabUtility.SaveAsPrefabAsset(root, CommandCenterPrefabPath);
                Debug.Log("[RepairConstructionSystem]   ✓ CommandCenterPrefab → dozerPrefab assigned.");
            }
            else
            {
                Debug.Log("[RepairConstructionSystem]   = CommandCenterPrefab → dozerPrefab already set.");
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static void AssignDozerToInSceneCommandCenters(GameObject dozerPrefab)
    {
        CommandCenterProducer[] inScene = Object.FindObjectsByType<CommandCenterProducer>(FindObjectsSortMode.None);
        if (inScene.Length == 0)
        {
            Debug.Log("[RepairConstructionSystem]   = No in-scene CommandCenter — skipping.");
            return;
        }

        int patched = 0;
        foreach (CommandCenterProducer ccp in inScene)
        {
            if (ccp.dozerPrefab != dozerPrefab)
            {
                ccp.dozerPrefab = dozerPrefab;
                EditorUtility.SetDirty(ccp);
                patched++;
            }
        }

        Debug.Log($"[RepairConstructionSystem]   ✓ In-scene CommandCenters: {patched}/{inScene.Length} updated.");
    }

    private static void WireBuildingPlacementManager(GameObject sitePrefab)
    {
        BuildingPlacementManager bpm = Object.FindAnyObjectByType<BuildingPlacementManager>();
        if (bpm == null)
        {
            Debug.LogWarning("[RepairConstructionSystem]   ⚠ No BuildingPlacementManager in the scene — " +
                             "Dozer placement cannot run until one is added to GameManager.");
            return;
        }

        bool dirty = false;

        if (bpm.constructionSitePrefab != sitePrefab)
        {
            bpm.constructionSitePrefab = sitePrefab;
            dirty = true;
            Debug.Log("[RepairConstructionSystem]   ✓ BuildingPlacementManager.constructionSitePrefab assigned.");
        }

        if (bpm.debugInstantBuildEnabled)
        {
            bpm.debugInstantBuildEnabled = false;
            dirty = true;
            Debug.Log("[RepairConstructionSystem]   ✓ BuildingPlacementManager.debugInstantBuildEnabled disabled " +
                      "(switching to Dozer-driven construction).");
        }

        if (bpm.dozerBuildTime <= 0f)
        {
            bpm.dozerBuildTime = 1f;
            dirty = true;
        }

        if (dirty) EditorUtility.SetDirty(bpm);
    }
}
