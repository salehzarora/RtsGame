using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click editor tool that wires the Machine Gun Defense building into the
/// existing Dozer build flow. Safe to re-run.
///
/// Menu: Tools → RTS → Construction → Repair Dozer Build Options
///
/// What it does:
///   1. Ensures MachineGunDefensePrefab exists at Assets/_Game/Prefabs/
///      MachineGunDefensePrefab.prefab (runs CreateMachineGunDefensePrefab.Create
///      if missing).
///   2. Verifies the prefab carries the required components: Building,
///      SelectableBuilding, Health, UnitCategory.Building, PowerConsumer (15),
///      BuildingTurretCombat. Logs warnings for anything missing.
///   3. Finds the BuildingPlacementManager in the OPEN scene and assigns the
///      prefab to its machineGunDefensePrefab field with cost 250.
///   4. Re-runs Tools → RTS → Setup HUD if the HUD lacks the MG Defense button
///      so the new Dozer-build button is created and wired automatically.
///
/// What it does NOT touch:
///   • Existing Barracks / PowerPlant / VehicleFactory / Airfield / CommandCenter
///     prefabs or in-scene instances.
///   • Existing units, resources, power state, combat, aircraft, selection.
///   • Any prefab GUIDs (Create* tool overwrites in place).
/// </summary>
public static class RepairDozerBuildOptions
{
    private const string MGDefensePath = "Assets/_Game/Prefabs/MachineGunDefensePrefab.prefab";
    private const int    ExpectedCost  = 250;
    private const int    ExpectedPower = 15;

    [MenuItem("Tools/RTS/Construction/Repair Dozer Build Options")]
    public static void Run()
    {
        Debug.Log("[RepairDozerBuildOptions] ── Running ──");

        // ── 1. Ensure MachineGunDefensePrefab exists ─────────────────── //
        GameObject mgPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MGDefensePath);
        if (mgPrefab == null)
        {
            Debug.Log("[RepairDozerBuildOptions]   MachineGunDefensePrefab not found — creating it.");
            CreateMachineGunDefensePrefab.Create();
            AssetDatabase.Refresh();
            mgPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MGDefensePath);
        }
        if (mgPrefab == null)
        {
            Debug.LogError("[RepairDozerBuildOptions] ✗ MachineGunDefensePrefab could not be created.");
            return;
        }
        Debug.Log($"[RepairDozerBuildOptions]   ✓ MachineGunDefensePrefab at {MGDefensePath}");

        // ── 2. Verify required components on the prefab ─────────────── //
        VerifyPrefabComponents();

        // ── 3. Wire into BuildingPlacementManager ───────────────────── //
        WireBuildingPlacementManager(mgPrefab);

        // ── 4. Re-run HUD setup if button is missing ────────────────── //
        EnsureHUDButton();

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();

        Debug.Log("[RepairDozerBuildOptions] ── Done. ──\n" +
                  "  • Produce a Dozer (CommandCenter → Dozer).\n" +
                  "  • Select the Dozer — bottom-left build panel shows Machine Gun Defense - 250.\n" +
                  "  • Place it on the ground; the Dozer drives over and constructs it.");
    }

    // ------------------------------------------------------------------ //

    /// <summary>
    /// Loads the prefab contents, walks the required components, logs warnings,
    /// and patches non-destructive fields (cost / power) if drifted.
    /// </summary>
    private static void VerifyPrefabComponents()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(MGDefensePath);
        try
        {
            bool dirty = false;

            Building b = root.GetComponent<Building>();
            if (b == null)
                Debug.LogWarning("[RepairDozerBuildOptions]   ⚠ Prefab missing Building — " +
                                 "rerun Create Machine Gun Defense Prefab.");
            else if (b.cost != ExpectedCost)
            {
                b.cost = ExpectedCost;
                dirty  = true;
                Debug.Log($"[RepairDozerBuildOptions]   ✓ Building.cost reset to {ExpectedCost}.");
            }

            if (root.GetComponent<SelectableBuilding>() == null)
                Debug.LogWarning("[RepairDozerBuildOptions]   ⚠ Prefab missing SelectableBuilding.");

            Health hp = root.GetComponent<Health>();
            if (hp == null)
                Debug.LogWarning("[RepairDozerBuildOptions]   ⚠ Prefab missing Health.");
            else if (hp.team != Health.Team.Player)
                Debug.LogWarning("[RepairDozerBuildOptions]   ⚠ Prefab Health.team is not Player.");

            UnitCategory cat = root.GetComponent<UnitCategory>();
            if (cat == null)
                Debug.LogWarning("[RepairDozerBuildOptions]   ⚠ Prefab missing UnitCategory.");
            else if (cat.category != UnitCategory.Category.Building)
            {
                cat.category = UnitCategory.Category.Building;
                dirty        = true;
                Debug.Log("[RepairDozerBuildOptions]   ✓ UnitCategory set to Building.");
            }

            PowerConsumer pc = root.GetComponent<PowerConsumer>();
            if (pc == null)
                Debug.LogWarning("[RepairDozerBuildOptions]   ⚠ Prefab missing PowerConsumer.");
            else if (pc.demandAmount != ExpectedPower)
            {
                pc.demandAmount = ExpectedPower;
                dirty           = true;
                Debug.Log($"[RepairDozerBuildOptions]   ✓ PowerConsumer.demandAmount reset to {ExpectedPower}.");
            }

            if (root.GetComponent<BuildingTurretCombat>() == null)
                Debug.LogWarning("[RepairDozerBuildOptions]   ⚠ Prefab missing BuildingTurretCombat.");

            if (dirty)
                PrefabUtility.SaveAsPrefabAsset(root, MGDefensePath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static void WireBuildingPlacementManager(GameObject mgPrefab)
    {
        BuildingPlacementManager bpm = Object.FindAnyObjectByType<BuildingPlacementManager>();
        if (bpm == null)
        {
            Debug.LogWarning("[RepairDozerBuildOptions]   ⚠ No BuildingPlacementManager in the scene. " +
                             "Add one to GameManager and re-run.");
            return;
        }

        bool dirty = false;

        if (bpm.machineGunDefensePrefab != mgPrefab)
        {
            bpm.machineGunDefensePrefab = mgPrefab;
            dirty = true;
            Debug.Log("[RepairDozerBuildOptions]   ✓ BuildingPlacementManager.machineGunDefensePrefab assigned.");
        }

        if (bpm.machineGunDefenseCost != ExpectedCost)
        {
            bpm.machineGunDefenseCost = ExpectedCost;
            dirty = true;
            Debug.Log($"[RepairDozerBuildOptions]   ✓ BuildingPlacementManager.machineGunDefenseCost = {ExpectedCost}.");
        }

        if (dirty) EditorUtility.SetDirty(bpm);
    }

    /// <summary>
    /// Re-runs Tools → RTS → Setup HUD when the new MG Defense button is not
    /// yet present on RTSHUD. Setup HUD destroys and rebuilds the entire
    /// HUDCanvas, so it's both safe and the canonical way to surface new
    /// build buttons.
    /// </summary>
    private static void EnsureHUDButton()
    {
        RTSHUD hud = Object.FindAnyObjectByType<RTSHUD>();
        if (hud == null)
        {
            Debug.LogWarning("[RepairDozerBuildOptions]   ⚠ No RTSHUD in the scene — " +
                             "run Tools → RTS → Setup HUD first.");
            return;
        }

        if (hud.dozerBuildMachineGunDefenseButton != null)
        {
            Debug.Log("[RepairDozerBuildOptions]   = MG Defense button already wired in HUD.");
            return;
        }

        Debug.Log("[RepairDozerBuildOptions]   Re-running Tools → RTS → Setup HUD to add MG Defense button.");
        SetupRTSHUD.SetupHUD();
    }
}
