using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One-click repair tool — guarantees every building prefab the placement
/// system needs exists, has the right components/layer, and is wired into
/// the scene's BuildingPlacementManager.
///
/// Menu: Tools → RTS → Repair Prefabs And Building Placement
///
/// What it does (idempotent — safe to re-run):
///   1. Resolves required user layers: Building / Unit / Resource / Ground.
///   2. Finds (or via the Humvee builder, creates) HumveePrefab so the
///      VehicleFactory has something to wire.
///   3. Repairs the existing Barracks.prefab:
///        - layer = Building (recursive)
///        - Building (name "Barracks", cost 100)
///        - SelectableBuilding
///        - UnitProducer (soldierPrefab assigned if null)
///        - PowerConsumer (demand = 10)
///   4. Creates PowerPlantPrefab.prefab from primitives if missing, else
///      repairs it:
///        - layer = Building
///        - Building (name "PowerPlant", cost 150)
///        - SelectableBuilding
///        - PowerPlant (supply = 100)
///   5. Repairs the VehicleFactoryPrefab.prefab the same way:
///        - layer = Building
///        - Building (name "VehicleFactory", cost 300)
///        - SelectableBuilding
///        - VehicleFactoryProducer (humveePrefab assigned if null)
///        - PowerConsumer (demand = 25)
///   6. Finds (or creates) GameManager, adds BuildingPlacementManager if
///      missing, then assigns:
///        - barracksPrefab + barracksCost = 100
///        - powerPlantPrefab + powerPlantCost = 150
///        - vehicleFactoryPrefab + vehicleFactoryCost = 300
///        - groundLayer  = Ground
///        - obstacleLayer = Unit | Resource | Building (Ground intentionally excluded)
///
/// What it does NOT touch:
///   - SoldierPrefab / WorkerPrefab / HumveePrefab internals (use the
///     dedicated builders for those).
///   - HUD / RTSHUD (use Tools → RTS → Setup HUD).
///   - Existing soldierCost / workerCost / humveeCost fields the user has
///     already tuned. Only structural prefab fields are overwritten.
/// </summary>
public static class RepairBuildingPrefabs
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    private const string PrefabFolder         = "Assets/_Game/Prefabs";
    private const string PowerPlantPath       = PrefabFolder + "/PowerPlantPrefab.prefab";
    private const string PowerPlantMatFolder  = "Assets/_Game/Materials/PowerPlant";

    private const int    BarracksCost         = 100;
    private const int    PowerPlantCost       = 150;
    private const int    VehicleFactoryCost   = 300;
    private const int    BarracksPower        = 10;
    private const int    VehicleFactoryPower  = 25;
    private const int    PowerPlantSupply     = 100;

    // PowerPlant visual
    private static readonly Color PlantYellow = new Color(0.95f, 0.80f, 0.15f);
    private static readonly Color PlantGrey   = new Color(0.45f, 0.45f, 0.48f);
    private static readonly Color RingCyan    = new Color(0.20f, 0.85f, 1.00f);
    private static readonly Vector3 PlantSize = new Vector3(2.6f, 2.0f, 2.6f);

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Repair Prefabs And Building Placement")]
    public static void Repair()
    {
        Debug.Log("[RepairBuildingPrefabs] ── Running ──");

        // ── 1. Layers ────────────────────────────────────────────────── //
        int buildingLayer = LayerMask.NameToLayer("Building");
        int unitLayer     = LayerMask.NameToLayer("Unit");
        int resourceLayer = LayerMask.NameToLayer("Resource");
        int groundLayer   = LayerMask.NameToLayer("Ground");

        if (buildingLayer < 0 || unitLayer < 0 || resourceLayer < 0 || groundLayer < 0)
        {
            Debug.LogError("[RepairBuildingPrefabs] ✗ Required layers missing — need Building/Unit/Resource/Ground.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add the missing User Layers.");
            return;
        }

        // ── 2. Dependency prefabs ────────────────────────────────────── //
        GameObject soldierPrefab = FindPrefab("SoldierPrefab");
        if (soldierPrefab == null)
            Debug.LogWarning("[RepairBuildingPrefabs] ⚠ SoldierPrefab missing — Barracks UnitProducer will be left unwired.");

        GameObject humveePrefab = FindPrefab("HumveePrefab");
        if (humveePrefab == null)
        {
            Debug.Log("[RepairBuildingPrefabs]   HumveePrefab missing — running Humvee builder.");
            CreateHumveePrefab.Create();
            humveePrefab = FindPrefab("HumveePrefab");
        }

        // ── 3. Building prefabs ──────────────────────────────────────── //
        GameObject barracks       = RepairBarracks(buildingLayer, soldierPrefab);
        GameObject powerPlant     = EnsurePowerPlant(buildingLayer);
        GameObject vehicleFactory = RepairVehicleFactory(buildingLayer, humveePrefab);

        // ── 4. GameManager + BuildingPlacementManager ──────────────── //
        GameObject gm = GetOrCreateGameManager();
        BuildingPlacementManager bpm = gm.GetComponent<BuildingPlacementManager>();
        if (bpm == null)
        {
            bpm = Undo.AddComponent<BuildingPlacementManager>(gm);
            Debug.Log("[RepairBuildingPrefabs] ✓ Added BuildingPlacementManager to GameManager.");
        }

        Undo.RecordObject(bpm, "Repair BuildingPlacementManager");
        bpm.barracksPrefab       = barracks;
        bpm.barracksCost         = BarracksCost;
        bpm.powerPlantPrefab     = powerPlant;
        bpm.powerPlantCost       = PowerPlantCost;
        bpm.vehicleFactoryPrefab = vehicleFactory;
        bpm.vehicleFactoryCost   = VehicleFactoryCost;

        // LayerMask has an implicit int conversion; assign masks directly.
        bpm.groundLayer   = 1 << groundLayer;
        bpm.obstacleLayer = (1 << unitLayer) | (1 << resourceLayer) | (1 << buildingLayer);
        EditorUtility.SetDirty(bpm);

        // ── 5. Persist ────────────────────────────────────────────── //
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();

        Debug.Log("[RepairBuildingPrefabs] ✓ Done. " +
                  $"Barracks={(barracks!=null?"OK":"MISSING")}, " +
                  $"PowerPlant={(powerPlant!=null?"OK":"MISSING")}, " +
                  $"VehicleFactory={(vehicleFactory!=null?"OK":"MISSING")}.\n" +
                  "  Save the scene (Ctrl+S) and re-run Tools → RTS → Setup HUD if buttons " +
                  "are stale. Press Play and click any of the three build buttons.");
    }

    // ------------------------------------------------------------------ //
    // Barracks repair
    // ------------------------------------------------------------------ //

    private static GameObject RepairBarracks(int buildingLayer, GameObject soldierPrefab)
    {
        GameObject prefab = FindPrefab("Barracks");
        if (prefab == null)
        {
            Debug.LogError("[RepairBuildingPrefabs] ✗ Barracks.prefab not found — skipping Barracks repair.");
            return null;
        }

        string path = AssetDatabase.GetAssetPath(prefab);
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            // Order matters — UnitProducer requires SelectableBuilding.
            Building b = EnsureComponent<Building>(root);
            b.buildingName = "Barracks";
            b.cost         = BarracksCost;

            EnsureComponent<SelectableBuilding>(root);

            UnitProducer up = EnsureComponent<UnitProducer>(root);
            if (up.soldierPrefab == null && soldierPrefab != null)
                up.soldierPrefab = soldierPrefab;

            PowerConsumer pc = EnsureComponent<PowerConsumer>(root);
            pc.demandAmount = BarracksPower;

            SetLayerRecursive(root.transform, buildingLayer);

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log($"[RepairBuildingPrefabs] ✓ Barracks repaired (cost={BarracksCost}, " +
                      $"power={BarracksPower}, soldier={(up.soldierPrefab!=null?"wired":"UNWIRED")}).");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }

        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    // ------------------------------------------------------------------ //
    // PowerPlant — create from primitives if missing, else repair
    // ------------------------------------------------------------------ //

    private static GameObject EnsurePowerPlant(int buildingLayer)
    {
        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(PowerPlantPath);

        // Materials are needed either way (existing-with-stripped-mats or fresh).
        Material hullMat = LoadOrCreateMat(PowerPlantMatFolder, "PlantHull",   PlantYellow);
        Material trimMat = LoadOrCreateMat(PowerPlantMatFolder, "PlantTrim",   PlantGrey);
        Material ringMat = LoadOrCreateMat(PowerPlantMatFolder, "PlantRing",   RingCyan);
        AssetDatabase.SaveAssets();

        if (existing == null)
        {
            // Build a fresh PowerPlant prefab from scratch.
            GameObject root = new GameObject("PowerPlantPrefab");
            try
            {
                BuildPowerPlantContents(root, buildingLayer, hullMat, trimMat, ringMat);
                EnsureFolder(PrefabFolder);
                PrefabUtility.SaveAsPrefabAsset(root, PowerPlantPath);
                Debug.Log($"[RepairBuildingPrefabs] ✓ Created {PowerPlantPath} " +
                          $"(cost={PowerPlantCost}, supply={PowerPlantSupply}).");
            }
            finally { Object.DestroyImmediate(root); }
        }
        else
        {
            // Repair the existing prefab in place.
            GameObject root = PrefabUtility.LoadPrefabContents(PowerPlantPath);
            try
            {
                Building b = EnsureComponent<Building>(root);
                b.buildingName = "PowerPlant";
                b.cost         = PowerPlantCost;

                EnsureComponent<SelectableBuilding>(root);

                PowerPlant pp = EnsureComponent<PowerPlant>(root);
                pp.supplyAmount = PowerPlantSupply;

                // Ensure it has at least one collider for selection raycasts.
                if (root.GetComponentInChildren<Collider>(true) == null)
                {
                    BoxCollider bc = root.AddComponent<BoxCollider>();
                    bc.size   = PlantSize;
                    bc.center = new Vector3(0f, PlantSize.y * 0.5f, 0f);
                }

                SetLayerRecursive(root.transform, buildingLayer);

                PrefabUtility.SaveAsPrefabAsset(root, PowerPlantPath);
                Debug.Log($"[RepairBuildingPrefabs] ✓ PowerPlant repaired " +
                          $"(cost={PowerPlantCost}, supply={PowerPlantSupply}).");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        return AssetDatabase.LoadAssetAtPath<GameObject>(PowerPlantPath);
    }

    /// <summary>
    /// Builds the PowerPlant geometry + gameplay components on a fresh root.
    /// Same primitive-stack pattern used by CreateVehicleFactoryPrefab.
    /// </summary>
    private static void BuildPowerPlantContents(
        GameObject root, int buildingLayer,
        Material hull, Material trim, Material ring)
    {
        // Gameplay components
        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size   = PlantSize;
        col.center = new Vector3(0f, PlantSize.y * 0.5f, 0f);

        Building b = root.AddComponent<Building>();
        b.buildingName = "PowerPlant";
        b.cost         = PowerPlantCost;

        SelectableBuilding sb = root.AddComponent<SelectableBuilding>();

        PowerPlant pp = root.AddComponent<PowerPlant>();
        pp.supplyAmount = PowerPlantSupply;

        // Visual children
        Spawn(root, "Hull",
              PrimitiveType.Cube, hull,
              new Vector3(0f, PlantSize.y * 0.5f, 0f),
              PlantSize);

        // Big grey reactor cap on top
        Spawn(root, "ReactorCap",
              PrimitiveType.Cylinder, trim,
              new Vector3(0f, PlantSize.y + 0.45f, 0f),
              new Vector3(1.4f, 0.45f, 1.4f));

        // Two small smokestacks
        Spawn(root, "Stack1",
              PrimitiveType.Cylinder, trim,
              new Vector3(-0.7f, PlantSize.y + 1.05f, -0.5f),
              new Vector3(0.30f, 0.60f, 0.30f));
        Spawn(root, "Stack2",
              PrimitiveType.Cylinder, trim,
              new Vector3( 0.7f, PlantSize.y + 1.05f,  0.5f),
              new Vector3(0.30f, 0.60f, 0.30f));

        // Selection ring (hidden by default; SelectableBuilding toggles it on Select)
        GameObject ringGO = Spawn(root, "SelectionRing",
              PrimitiveType.Cylinder, ring,
              new Vector3(0f, 0.02f, 0f),
              new Vector3(PlantSize.x + 1.2f, 0.02f, PlantSize.z + 1.2f));
        ringGO.SetActive(false);
        sb.selectionIndicator = ringGO;

        SetLayerRecursive(root.transform, buildingLayer);
    }

    // ------------------------------------------------------------------ //
    // VehicleFactory repair
    // ------------------------------------------------------------------ //

    private static GameObject RepairVehicleFactory(int buildingLayer, GameObject humveePrefab)
    {
        GameObject prefab = FindPrefab("VehicleFactoryPrefab");
        if (prefab == null)
        {
            Debug.Log("[RepairBuildingPrefabs]   VehicleFactoryPrefab missing — running Vehicle Factory builder.");
            CreateVehicleFactoryPrefab.Create();
            prefab = FindPrefab("VehicleFactoryPrefab");
            if (prefab == null)
            {
                Debug.LogError("[RepairBuildingPrefabs] ✗ Vehicle Factory builder did not produce a prefab.");
                return null;
            }
        }

        string path = AssetDatabase.GetAssetPath(prefab);
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            Building b = EnsureComponent<Building>(root);
            b.buildingName = "VehicleFactory";
            b.cost         = VehicleFactoryCost;

            EnsureComponent<SelectableBuilding>(root);

            PowerConsumer pc = EnsureComponent<PowerConsumer>(root);
            pc.demandAmount = VehicleFactoryPower;

            VehicleFactoryProducer vfp = EnsureComponent<VehicleFactoryProducer>(root);
            if (vfp.humveePrefab == null && humveePrefab != null)
                vfp.humveePrefab = humveePrefab;

            // Ensure at least one collider for selection raycasts.
            if (root.GetComponentInChildren<Collider>(true) == null)
            {
                BoxCollider bc = root.AddComponent<BoxCollider>();
                bc.size   = new Vector3(3.2f, 2.0f, 3.2f);
                bc.center = new Vector3(0f, 1.0f, 0f);
            }

            SetLayerRecursive(root.transform, buildingLayer);

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log($"[RepairBuildingPrefabs] ✓ VehicleFactory repaired (cost={VehicleFactoryCost}, " +
                      $"power={VehicleFactoryPower}, humvee={(vfp.humveePrefab!=null?"wired":"UNWIRED")}).");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }

        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    // ------------------------------------------------------------------ //
    // Shared helpers
    // ------------------------------------------------------------------ //

    private static GameObject GetOrCreateGameManager()
    {
        GameObject gm = GameObject.Find("GameManager");
        if (gm != null) return gm;

        gm = new GameObject("GameManager");
        Undo.RegisterCreatedObjectUndo(gm, "Create GameManager");
        Debug.LogWarning("[RepairBuildingPrefabs] ⚠ Created a new GameManager — " +
                         "move components to your existing one if you have a differently-named manager.");
        return gm;
    }

    private static GameObject FindPrefab(string baseName)
    {
        string path = AssetDatabase.FindAssets($"{baseName} t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith($"/{baseName}.prefab"));
        if (string.IsNullOrEmpty(path)) return null;
        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    /// <summary>GetComponent-or-AddComponent on the prefab root.</summary>
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

    private static GameObject Spawn(GameObject parent, string name, PrimitiveType type,
                                    Material mat, Vector3 localPos, Vector3 localScale)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = localPos;
        go.transform.localScale    = localScale;

        Collider c = go.GetComponent<Collider>();
        if (c != null) Object.DestroyImmediate(c);

        Renderer r = go.GetComponent<Renderer>();
        if (r != null) r.sharedMaterial = mat;

        return go;
    }

    // ------------------------------------------------------------------ //
    // Material factory — persistent .mat assets, render-pipeline aware
    // ------------------------------------------------------------------ //

    private static Material LoadOrCreateMat(string folder, string name, Color color)
    {
        EnsureFolder(folder);
        string path = $"{folder}/{name}.mat";

        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        Shader target = ResolveShader();

        if (m == null)
        {
            m = new Material(target) { name = name };
            AssetDatabase.CreateAsset(m, path);
        }
        else if (m.shader == null || m.shader.name != target.name)
        {
            m.shader = target;
        }

        m.color = color;
        if (m.HasProperty("_BaseColor"))
            m.SetColor("_BaseColor", color);
        EditorUtility.SetDirty(m);
        return m;
    }

    private static Shader ResolveShader()
    {
        RenderPipelineAsset rp = GraphicsSettings.defaultRenderPipeline;
        bool isURP = rp != null && rp.GetType().Name.Contains("Universal");

        Shader urp = Shader.Find("Universal Render Pipeline/Lit");
        Shader std = Shader.Find("Standard");

        if (isURP && urp != null) return urp;
        if (!isURP && std != null) return std;
        return urp ?? std ?? Shader.Find("Hidden/InternalErrorShader");
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;

        string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        string leaf   = Path.GetFileName(folder);

        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);

        AssetDatabase.CreateFolder(parent, leaf);
    }
}
