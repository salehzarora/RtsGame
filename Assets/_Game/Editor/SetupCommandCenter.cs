using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click editor tool that wires the CommandCenter for Worker production.
///
/// Menu: Tools → RTS → Setup Command Center
///
/// What it does (idempotent — safe to re-run):
///   1. Locates the CommandCenter in the active scene.
///   2. Ensures its Layer = "Building" so left-click selection raycasts hit it.
///   3. Adds SelectableBuilding if missing.
///   4. Removes any legacy UnitProducer from the CommandCenter (UnitProducer
///      is Barracks-only / Soldier-only — putting one on the CC would offer
///      Soldier production from the wrong building).
///   5. Adds CommandCenterProducer if missing, sets Worker Cost = 75 and a
///      sensible spawn offset (4 units along X), and assigns the Worker prefab.
///   6. If no WorkerPrefab.prefab exists in the project, creates one by
///      cloning SoldierPrefab and:
///         • removing UnitCombat (workers don't fight)
///         • removing the FirePoint child (only meaningful for ranged combat)
///         • adding WorkerGatherer
///      The cloned worker keeps NavMeshAgent, UnitMovement, SelectableUnit,
///      Health, HealthBar, and UnitColorMarker (which auto-greens the worker
///      via the new WorkerGatherer it now has).
///
/// What it does NOT touch:
///   • Existing CommandCenter fields (it just adds missing components).
///   • The Barracks or SoldierPrefab.
///   • Soldier production wiring on the Barracks.
/// </summary>
public static class SetupCommandCenter
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    private const string BuildingLayerName  = "Building";
    private const string WorkerPrefabPath   = "Assets/_Game/Prefabs/WorkerPrefab.prefab";
    private const int    WorkerCost         = 75;
    private static readonly Vector3 SpawnOffset = new Vector3(4f, 0f, 0f);

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Setup Command Center")]
    public static void Setup()
    {
        Debug.Log("[SetupCommandCenter] ── Running ──");

        // ── 1. Locate CommandCenter ─────────────────────────────────── //
        CommandCenter cc = Object.FindAnyObjectByType<CommandCenter>(FindObjectsInactive.Include);
        if (cc == null)
        {
            Debug.LogError("[SetupCommandCenter] ✗ No CommandCenter found in the active scene.\n" +
                           "  Add a CommandCenter cube to the scene and attach the CommandCenter " +
                           "script, then re-run this tool.");
            return;
        }

        GameObject ccGO = cc.gameObject;

        // ── 2. Building layer ──────────────────────────────────────── //
        int buildingLayer = LayerMask.NameToLayer(BuildingLayerName);
        if (buildingLayer < 0)
        {
            Debug.LogError($"[SetupCommandCenter] ✗ Layer '{BuildingLayerName}' does not exist.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add 'Building' as a User Layer.");
            return;
        }
        if (ccGO.layer != buildingLayer)
        {
            Undo.RecordObject(ccGO, "Set CommandCenter Layer");
            ccGO.layer = buildingLayer;
            Debug.Log($"[SetupCommandCenter] ✓ CommandCenter layer set to '{BuildingLayerName}'.");
        }

        // ── 3. SelectableBuilding ──────────────────────────────────── //
        SelectableBuilding sb = ccGO.GetComponent<SelectableBuilding>();
        if (sb == null)
        {
            sb = Undo.AddComponent<SelectableBuilding>(ccGO);
            Debug.Log("[SetupCommandCenter] ✓ Added SelectableBuilding to CommandCenter.");
        }
        else
        {
            Debug.Log("[SetupCommandCenter] = SelectableBuilding already present.");
        }

        // ── 4. Migrate away from any legacy UnitProducer on the CC ───── //
        // UnitProducer is now Barracks-only (Soldiers). The CommandCenter must
        // NOT have it — having one would offer Soldier production from the CC.
        UnitProducer legacy = ccGO.GetComponent<UnitProducer>();
        if (legacy != null)
        {
            Debug.LogWarning("[SetupCommandCenter] ⚠ Removing legacy UnitProducer from CommandCenter " +
                             "(UnitProducer is now Barracks-only; CommandCenter uses CommandCenterProducer).");
            Undo.DestroyObjectImmediate(legacy);
        }

        // ── 5. CommandCenterProducer + worker stats ────────────────── //
        CommandCenterProducer ccp = ccGO.GetComponent<CommandCenterProducer>();
        if (ccp == null)
        {
            ccp = Undo.AddComponent<CommandCenterProducer>(ccGO);
            Debug.Log("[SetupCommandCenter] ✓ Added CommandCenterProducer to CommandCenter.");
        }
        else
        {
            Debug.Log("[SetupCommandCenter] = CommandCenterProducer already present.");
        }
        ccp.workerCost = WorkerCost;
        if (ccp.spawnOffset == Vector3.zero) ccp.spawnOffset = SpawnOffset;

        // ── 6. Worker prefab — load existing or bootstrap from Soldier ─ //
        GameObject workerPrefab = LoadOrCreateWorkerPrefab();
        if (workerPrefab == null)
        {
            Debug.LogError("[SetupCommandCenter] ✗ Could not load or create WorkerPrefab.\n" +
                           "  Assign one manually in CommandCenter → CommandCenterProducer → Worker Prefab.");
        }
        else
        {
            ccp.workerPrefab = workerPrefab;
            Debug.Log($"[SetupCommandCenter] ✓ Worker Prefab assigned: {AssetDatabase.GetAssetPath(workerPrefab)}");
        }

        // ── 7. Persist ─────────────────────────────────────────────── //
        EditorUtility.SetDirty(ccp);
        EditorUtility.SetDirty(ccGO);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log("[SetupCommandCenter] ✓ Done. Save the scene (Ctrl+S) and re-run " +
                  "Tools → RTS → Setup HUD if you have not added the Worker button yet.");
    }

    // ------------------------------------------------------------------ //
    // WorkerPrefab loader / bootstrapper
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the existing WorkerPrefab.prefab if one is on disk. Otherwise
    /// clones SoldierPrefab.prefab into WorkerPrefab.prefab and strips combat
    /// + adds WorkerGatherer so the result is a valid worker.
    /// </summary>
    private static GameObject LoadOrCreateWorkerPrefab()
    {
        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(WorkerPrefabPath);
        if (existing != null) return existing;

        // No worker prefab yet — clone the soldier and convert it.
        string soldierPath = AssetDatabase.FindAssets("SoldierPrefab t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith("/SoldierPrefab.prefab"));

        if (string.IsNullOrEmpty(soldierPath))
        {
            Debug.LogError("[SetupCommandCenter] ✗ Cannot bootstrap WorkerPrefab — " +
                           "SoldierPrefab.prefab not found in the project.");
            return null;
        }

        if (!AssetDatabase.CopyAsset(soldierPath, WorkerPrefabPath))
        {
            Debug.LogError($"[SetupCommandCenter] ✗ Failed to copy {soldierPath} → {WorkerPrefabPath}.");
            return null;
        }
        Debug.Log($"[SetupCommandCenter] ✓ Cloned SoldierPrefab → {WorkerPrefabPath}. Converting to Worker…");

        // ── Edit the clone ─────────────────────────────────────────── //
        GameObject root = PrefabUtility.LoadPrefabContents(WorkerPrefabPath);
        try
        {
            // Remove combat — workers don't fight.
            UnitCombat uc = root.GetComponent<UnitCombat>();
            if (uc != null) Object.DestroyImmediate(uc);

            // Remove FirePoint child — only meaningful for ranged combat.
            Transform fp = root.transform.Find("FirePoint");
            if (fp != null) Object.DestroyImmediate(fp.gameObject);

            // Add WorkerGatherer if not already there. UnitColorMarker.Start
            // will detect this and paint the worker green at runtime.
            if (root.GetComponent<WorkerGatherer>() == null)
                root.AddComponent<WorkerGatherer>();

            PrefabUtility.SaveAsPrefabAsset(root, WorkerPrefabPath);
            Debug.Log("[SetupCommandCenter] ✓ WorkerPrefab.prefab: combat stripped, WorkerGatherer added.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        return AssetDatabase.LoadAssetAtPath<GameObject>(WorkerPrefabPath);
    }
}
