using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One-click editor tool that creates the VehicleFactory prefab and wires it
/// into the scene's BuildingPlacementManager.
///
/// Menu: Tools → RTS → Create Vehicle Factory Prefab
///
/// What it does (idempotent — safe to re-run):
///   • Builds VehicleFactoryPrefab.prefab from a primitive cube, with:
///       - Building (cost 300, name "VehicleFactory")
///       - SelectableBuilding
///       - PowerConsumer (demand 25)
///       - VehicleFactoryProducer (humveePrefab auto-assigned if available;
///         humveeCost 150; spawnOffset (5, 0, 0))
///       - BoxCollider sized to the cube
///       - Layer = Building (applied at runtime by BuildingPlacementManager
///         on placement, but also set on the prefab so direct scene-drops work)
///       - SelectionRing child (flat cyan cylinder under the building)
///       - HealthBar child (optional — gives the building a visible HP bar
///         when Health is added later; the script tolerates missing Health
///         on Awake and disables itself).
///
///   • If HumveePrefab.prefab doesn't exist yet, runs the Humvee builder
///     first so the VehicleFactoryProducer can be fully configured.
///
///   • Wires the new prefab into the scene's BuildingPlacementManager
///     (vehicleFactoryPrefab + vehicleFactoryCost), so the HUD's Vehicle
///     Factory button works immediately.
/// </summary>
public static class CreateVehicleFactoryPrefab
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    private const string PrefabPath  = "Assets/_Game/Prefabs/VehicleFactoryPrefab.prefab";
    private const string HumveePath  = "Assets/_Game/Prefabs/HumveePrefab.prefab";
    private const string MatFolder   = "Assets/_Game/Materials/VehicleFactory";
    private const string BuildingLayer = "Building";

    // Stats
    private const int   BuildingCost = 300;
    private const int   HumveeCost   = 150;
    private const int   PowerDemand  = 25;
    private static readonly Vector3 SpawnOffset = new Vector3(5f, 0f, 0f);

    // Visual
    private static readonly Color HullGray    = new Color(0.42f, 0.42f, 0.46f);
    private static readonly Color RoofDark    = new Color(0.22f, 0.22f, 0.25f);
    private static readonly Color StripeAmber = new Color(0.85f, 0.65f, 0.10f);
    private static readonly Color RingCyan    = new Color(0.20f, 0.85f, 1.00f);
    private static readonly Vector3 BuildingSize = new Vector3(3.2f, 2.0f, 3.2f);

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Create Vehicle Factory Prefab")]
    public static void Create()
    {
        Debug.Log("[CreateVehicleFactoryPrefab] ── Building VehicleFactory ──");

        // ── 1. Ensure HumveePrefab exists so we can assign it. ────────── //
        GameObject humvee = AssetDatabase.LoadAssetAtPath<GameObject>(HumveePath);
        if (humvee == null)
        {
            Debug.Log("[CreateVehicleFactoryPrefab]   HumveePrefab missing — running Humvee builder first.");
            CreateHumveePrefab.Create();
            humvee = AssetDatabase.LoadAssetAtPath<GameObject>(HumveePath);
            if (humvee == null)
            {
                Debug.LogError("[CreateVehicleFactoryPrefab] ✗ HumveePrefab still missing after build — aborting.");
                return;
            }
        }

        int buildingLayer = LayerMask.NameToLayer(BuildingLayer);
        if (buildingLayer < 0)
        {
            Debug.LogError($"[CreateVehicleFactoryPrefab] ✗ Layer '{BuildingLayer}' does not exist.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add 'Building' user layer.");
            return;
        }

        // ── 2. Materials ─────────────────────────────────────────────── //
        Material hullMat   = LoadOrCreateMat("FactoryHull",   HullGray);
        Material roofMat   = LoadOrCreateMat("FactoryRoof",   RoofDark);
        Material stripeMat = LoadOrCreateMat("FactoryStripe", StripeAmber);
        Material ringMat   = LoadOrCreateMat("FactoryRing",   RingCyan);
        AssetDatabase.SaveAssets();

        // ── 3. Build the temp root in the scene, then save as prefab ─── //
        GameObject root = new GameObject("VehicleFactoryPrefab");
        try
        {
            root.layer = buildingLayer;
            BuildVehicleFactory(root, buildingLayer, humvee,
                hullMat, roofMat, stripeMat, ringMat);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateVehicleFactoryPrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Cost={BuildingCost}, Power={PowerDemand}, HumveeCost={HumveeCost}.");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }

        // ── 4. Wire into BuildingPlacementManager so the HUD works immediately ──
        GameObject saved = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        BuildingPlacementManager bpm = Object.FindAnyObjectByType<BuildingPlacementManager>(
            FindObjectsInactive.Include);
        if (bpm == null)
        {
            Debug.LogWarning("[CreateVehicleFactoryPrefab] ⚠ No BuildingPlacementManager in scene. " +
                             "Drag VehicleFactoryPrefab into BuildingPlacementManager.vehicleFactoryPrefab manually.");
        }
        else
        {
            Undo.RecordObject(bpm, "Assign VehicleFactory Prefab");
            bpm.vehicleFactoryPrefab = saved;
            bpm.vehicleFactoryCost   = BuildingCost;
            EditorUtility.SetDirty(bpm);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[CreateVehicleFactoryPrefab] ✓ Assigned to BuildingPlacementManager " +
                      "(vehicleFactoryPrefab + vehicleFactoryCost).");
        }

        Debug.Log("[CreateVehicleFactoryPrefab] ✓ Done. Re-run Tools → RTS → Setup HUD if you " +
                  "have not added the Vehicle Factory build button yet.");
    }

    // ------------------------------------------------------------------ //
    // VehicleFactory construction
    // ------------------------------------------------------------------ //

    private static void BuildVehicleFactory(
        GameObject root, int buildingLayer, GameObject humveePrefab,
        Material hull, Material roof, Material stripe, Material ring)
    {
        // --- Gameplay components on the root --------------------------- //

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size   = BuildingSize;
        col.center = new Vector3(0f, BuildingSize.y * 0.5f, 0f);

        Building b = root.AddComponent<Building>();
        b.buildingName = "VehicleFactory";
        b.cost         = BuildingCost;

        root.AddComponent<SelectableBuilding>();

        PowerConsumer pc = root.AddComponent<PowerConsumer>();
        pc.demandAmount = PowerDemand;

        VehicleFactoryProducer vfp = root.AddComponent<VehicleFactoryProducer>();
        vfp.humveePrefab = humveePrefab;
        vfp.humveeCost   = HumveeCost;
        vfp.spawnOffset  = SpawnOffset;

        // --- Visual children ------------------------------------------- //

        // Main hull — gunmetal box.
        Spawn(root, "Hull",
              PrimitiveType.Cube, hull,
              new Vector3(0f, BuildingSize.y * 0.5f, 0f),
              BuildingSize);

        // Sloped front "garage door" — amber stripe on the +Z face for visual identity.
        Spawn(root, "DoorStripe",
              PrimitiveType.Cube, stripe,
              new Vector3(0f, 0.65f, BuildingSize.z * 0.5f + 0.02f),
              new Vector3(BuildingSize.x * 0.65f, 0.6f, 0.05f));

        // Flat roof slab — slightly inset, darker.
        Spawn(root, "Roof",
              PrimitiveType.Cube, roof,
              new Vector3(0f, BuildingSize.y + 0.05f, 0f),
              new Vector3(BuildingSize.x * 0.95f, 0.10f, BuildingSize.z * 0.95f));

        // Two roof vents — small dark cubes for silhouette interest.
        Spawn(root, "Vent1",
              PrimitiveType.Cube, roof,
              new Vector3(-0.7f, BuildingSize.y + 0.30f, -0.4f),
              new Vector3(0.45f, 0.40f, 0.45f));
        Spawn(root, "Vent2",
              PrimitiveType.Cube, roof,
              new Vector3( 0.7f, BuildingSize.y + 0.30f,  0.4f),
              new Vector3(0.45f, 0.40f, 0.45f));

        // SelectionRing — flat cyan disc under the building, hidden by default.
        GameObject ringGO = Spawn(root, "SelectionRing",
              PrimitiveType.Cylinder, ring,
              new Vector3(0f, 0.02f, 0f),
              new Vector3(BuildingSize.x + 1.2f, 0.02f, BuildingSize.z + 1.2f));
        ringGO.SetActive(false); // SelectableBuilding hides it by default; mirror that
        root.GetComponent<SelectableBuilding>().selectionIndicator = ringGO;

        // --- Layer everything to "Building" ---------------------------- //
        SetLayerRecursive(root.transform, buildingLayer);
    }

    // ------------------------------------------------------------------ //
    // Primitive helpers (shared shape with CreateHumveePrefab)
    // ------------------------------------------------------------------ //

    private static GameObject Spawn(GameObject parent, string name, PrimitiveType type,
                                    Material mat, Vector3 localPos, Vector3 localScale)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = localPos;
        go.transform.localScale    = localScale;

        // Visual-only children: strip the auto-added collider — selection
        // raycasts only hit the root BoxCollider.
        Collider c = go.GetComponent<Collider>();
        if (c != null) Object.DestroyImmediate(c);

        Renderer r = go.GetComponent<Renderer>();
        if (r != null)
        {
            r.sharedMaterial    = mat;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows    = true;
        }
        return go;
    }

    private static void SetLayerRecursive(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++)
            SetLayerRecursive(t.GetChild(i), layer);
    }

    // ------------------------------------------------------------------ //
    // Material factory — persistent .mat assets, render-pipeline aware
    // ------------------------------------------------------------------ //

    private static Material LoadOrCreateMat(string name, Color color)
    {
        EnsureFolder(MatFolder);
        string path = $"{MatFolder}/{name}.mat";

        Material m  = AssetDatabase.LoadAssetAtPath<Material>(path);
        Shader target = ResolveShader();

        if (m == null)
        {
            m = new Material(target) { name = name };
            AssetDatabase.CreateAsset(m, path);
            Debug.Log($"[CreateVehicleFactoryPrefab]   Created material: {path}");
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
