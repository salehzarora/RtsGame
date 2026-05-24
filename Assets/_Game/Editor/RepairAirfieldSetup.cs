using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click setup/repair for the Airfield + Strike Jet pipeline.
///
/// Menu: Tools → RTS → Air System → Repair Airfield Setup
///
/// What it does (idempotent — safe to re-run):
///   1. Ensures StrikeJetPrefab.prefab exists; runs its builder if missing.
///   2. Ensures AirfieldPrefab.prefab exists; runs its builder if missing.
///   3. Loads AirfieldPrefab and verifies:
///        - Building / SelectableBuilding / PowerConsumer / Airfield / UnitCategory present
///        - Airfield.strikeJetPrefab assigned (wires it if null)
///        - Exactly 6 slot Transforms in Airfield.slots[]
///        - UnitCategory.Building
///        - Building layer recursive
///   4. Wires BuildingPlacementManager.airfieldPrefab + airfieldCost on the
///      scene's GameManager so the HUD's Airfield button works.
///
/// What it does NOT touch:
///   - HUD (use Tools → RTS → Setup HUD).
///   - Existing Airfield instances in the scene (the prefab change propagates
///     to connected instances automatically).
/// </summary>
public static class RepairAirfieldSetup
{
    [MenuItem("Tools/RTS/Air System/Repair Airfield Setup")]
    public static void Repair()
    {
        Debug.Log("[RepairAirfieldSetup] ── Running ──");

        // ── 1. Strike Jet prefab ─────────────────────────────────────── //
        GameObject jet = LoadPrefab("StrikeJetPrefab");
        if (jet == null)
        {
            Debug.Log("[RepairAirfieldSetup]   StrikeJetPrefab missing — building it.");
            CreateStrikeJetPrefab.Create();
            jet = LoadPrefab("StrikeJetPrefab");
        }
        if (jet == null)
        {
            Debug.LogError("[RepairAirfieldSetup] ✗ Could not produce StrikeJetPrefab — aborting.");
            return;
        }

        // ── 2. Airfield prefab ───────────────────────────────────────── //
        GameObject airfield = LoadPrefab("AirfieldPrefab");
        if (airfield == null)
        {
            Debug.Log("[RepairAirfieldSetup]   AirfieldPrefab missing — building it.");
            CreateAirfieldPrefab.Create();
            airfield = LoadPrefab("AirfieldPrefab");
        }
        if (airfield == null)
        {
            Debug.LogError("[RepairAirfieldSetup] ✗ Could not produce AirfieldPrefab — aborting.");
            return;
        }

        // ── 3. Verify Airfield prefab contents ───────────────────────── //
        string path = AssetDatabase.GetAssetPath(airfield);
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            int buildingLayer = LayerMask.NameToLayer("Building");

            Building b = EnsureComponent<Building>(root);
            b.buildingName = "Airfield";
            if (b.cost <= 0) b.cost = 600;

            EnsureComponent<SelectableBuilding>(root);

            PowerConsumer pc = EnsureComponent<PowerConsumer>(root);
            if (pc.demandAmount <= 0) pc.demandAmount = 40;

            UnitCategory cat = EnsureComponent<UnitCategory>(root);
            cat.category     = UnitCategory.Category.Building;

            Airfield af = EnsureComponent<Airfield>(root);
            if (af.strikeJetPrefab == null) af.strikeJetPrefab = jet;
            if (af.strikeJetCost <= 0)     af.strikeJetCost   = 450;

            // Slot array must have exactly MaxSlots entries. We don't rebuild
            // the geometry — that's the prefab builder's job — but we report
            // any missing slot references.
            if (af.slots == null || af.slots.Length != Airfield.MaxSlots)
            {
                Debug.LogWarning($"[RepairAirfieldSetup] ⚠ Airfield.slots had {(af.slots?.Length ?? 0)} entries — " +
                                 $"resizing to {Airfield.MaxSlots}. Re-run Create Airfield Prefab if slot Transforms are missing.");
                System.Array.Resize(ref af.slots, Airfield.MaxSlots);
            }
            int missing = 0;
            for (int i = 0; i < af.slots.Length; i++)
                if (af.slots[i] == null) missing++;
            if (missing > 0)
                Debug.LogWarning($"[RepairAirfieldSetup] ⚠ {missing}/6 Airfield slots are unassigned. " +
                                 "Re-run Tools → RTS → Air System → Create Airfield Prefab to rebuild the geometry.");

            if (buildingLayer >= 0) SetLayerRecursive(root.transform, buildingLayer);

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log($"[RepairAirfieldSetup] ✓ AirfieldPrefab repaired " +
                      $"(cost={b.cost}, power={pc.demandAmount}, jet={(af.strikeJetPrefab!=null?"OK":"NULL")}, " +
                      $"slots={6-missing}/6).");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }

        // ── 4. Wire BuildingPlacementManager ─────────────────────────── //
        BuildingPlacementManager bpm = Object.FindAnyObjectByType<BuildingPlacementManager>(
            FindObjectsInactive.Include);
        if (bpm == null)
        {
            Debug.LogWarning("[RepairAirfieldSetup] ⚠ No BuildingPlacementManager in scene. " +
                             "Run Tools → RTS → Repair Prefabs And Building Placement first, then re-run this.");
        }
        else
        {
            Undo.RecordObject(bpm, "Assign Airfield Prefab");
            bpm.airfieldPrefab = airfield;
            if (bpm.airfieldCost <= 0) bpm.airfieldCost = 600;
            EditorUtility.SetDirty(bpm);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[RepairAirfieldSetup] ✓ BuildingPlacementManager.airfieldPrefab assigned (cost 600).");
        }

        AssetDatabase.SaveAssets();
        Debug.Log("[RepairAirfieldSetup] ✓ Done. Re-run Tools → RTS → Setup HUD if the " +
                  "Airfield / Strike Jet buttons are missing.");
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

    private static T EnsureComponent<T>(GameObject go) where T : Component
    {
        T c = go.GetComponent<T>();
        if (c == null) c = go.AddComponent<T>();
        return c;
    }

    private static void SetLayerRecursive(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++)
            SetLayerRecursive(t.GetChild(i), layer);
    }
}
