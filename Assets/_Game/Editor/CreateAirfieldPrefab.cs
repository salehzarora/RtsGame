using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds AirfieldPrefab.prefab from primitives: a long grey runway, a small
/// concrete apron with six parking pads, a hangar block, and a control tower.
/// Each parking pad has an empty "Slot_X" Transform child that the Airfield
/// component uses as the aircraft spawn / parked position.
///
/// Menu: Tools → RTS → Air System → Create Airfield Prefab
///
/// Resulting hierarchy:
///   AirfieldPrefab/                 (BoxCollider, Building, SelectableBuilding,
///     │                                PowerConsumer (25), Airfield (with 6 slots),
///     │                                UnitCategory.Building)
///     ├── Runway                    (long dark-grey cube)
///     ├── Apron                     (lighter concrete cube along one side)
///     ├── Pad_0..Pad_5              (small flat squares on the apron)
///     ├── Slot_0..Slot_5            (empty Transforms — Airfield.slots[])
///     ├── Hangar                    (chunky grey block at one runway end)
///     ├── Tower                     (tall thin tower beside the hangar)
///     ├── SelectionRing             (cyan flat cylinder, hidden by default)
///     └── (StrikeJet prefab ref assigned by Repair Airfield Setup)
///
/// IMPORTANT — also tries to assign HumveePrefab-style references where
/// available: if StrikeJetPrefab.prefab exists, it is auto-wired into
/// Airfield.strikeJetPrefab. Use Repair Airfield Setup if missing.
/// </summary>
public static class CreateAirfieldPrefab
{
    private const string PrefabPath  = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";
    private const string JetPath     = "Assets/_Game/Prefabs/StrikeJetPrefab.prefab";
    private const string MatFolder   = "Assets/_Game/Materials/Airfield";
    private const string BuildingLayer = "Building";

    // Stats
    private const int BuildingCost = 600;
    private const int PowerDemand  = 40;
    private const int StrikeJetCost = 450;

    // Visual sizes
    private static readonly Vector3 RunwaySize = new Vector3(4f, 0.10f, 16f);
    private static readonly Vector3 ApronSize  = new Vector3(6f, 0.06f, 14f);
    private static readonly Vector3 HangarSize = new Vector3(4f, 3.0f, 4f);
    private static readonly Vector3 TowerSize  = new Vector3(1.4f, 4.5f, 1.4f);

    // Palette
    private static readonly Color RunwayDark    = new Color(0.18f, 0.18f, 0.20f);
    private static readonly Color ApronConcrete = new Color(0.55f, 0.55f, 0.55f);
    private static readonly Color PadTan        = new Color(0.62f, 0.58f, 0.42f);
    private static readonly Color HangarGrey    = new Color(0.42f, 0.42f, 0.45f);
    private static readonly Color TowerGrey     = new Color(0.32f, 0.36f, 0.40f);
    private static readonly Color RingCyan      = new Color(0.20f, 0.85f, 1.00f);

    [MenuItem("Tools/RTS/Air System/Create Airfield Prefab")]
    public static void Create()
    {
        Debug.Log("[CreateAirfieldPrefab] ── Building AirfieldPrefab ──");

        int buildingLayer = LayerMask.NameToLayer(BuildingLayer);
        if (buildingLayer < 0)
        {
            Debug.LogError($"[CreateAirfieldPrefab] ✗ Layer '{BuildingLayer}' does not exist.");
            return;
        }

        // Ensure StrikeJetPrefab exists so we can auto-wire it.
        GameObject jet = AssetDatabase.LoadAssetAtPath<GameObject>(JetPath);
        if (jet == null)
        {
            Debug.Log("[CreateAirfieldPrefab]   StrikeJetPrefab missing — running its builder.");
            CreateStrikeJetPrefab.Create();
            jet = AssetDatabase.LoadAssetAtPath<GameObject>(JetPath);
        }

        Material runwayMat = LoadOrCreateMat("AirRunway", RunwayDark);
        Material apronMat  = LoadOrCreateMat("AirApron",  ApronConcrete);
        Material padMat    = LoadOrCreateMat("AirPad",    PadTan);
        Material hangarMat = LoadOrCreateMat("AirHangar", HangarGrey);
        Material towerMat  = LoadOrCreateMat("AirTower",  TowerGrey);
        Material ringMat   = LoadOrCreateMat("AirRing",   RingCyan);
        AssetDatabase.SaveAssets();

        GameObject root = new GameObject("AirfieldPrefab");
        try
        {
            root.layer = buildingLayer;
            BuildAirfield(root, buildingLayer, jet,
                runwayMat, apronMat, padMat, hangarMat, towerMat, ringMat);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateAirfieldPrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Cost={BuildingCost}, Power={PowerDemand}, StrikeJetCost={StrikeJetCost}.\n" +
                      "  Run Tools → RTS → Air System → Repair Airfield Setup to wire " +
                      "BuildingPlacementManager.airfieldPrefab.");
        }
        finally { Object.DestroyImmediate(root); }
    }

    private static void BuildAirfield(
        GameObject root, int buildingLayer, GameObject jetPrefab,
        Material runway, Material apron, Material pad, Material hangar, Material tower, Material ring)
    {
        // --- Gameplay components --------------------------------------- //

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size   = new Vector3(10f, 2.0f, 16f);   // covers runway + apron footprint
        col.center = new Vector3(0f, 1.0f, 0f);

        Building b = root.AddComponent<Building>();
        b.buildingName = "Airfield";
        b.cost         = BuildingCost;

        SelectableBuilding sb = root.AddComponent<SelectableBuilding>();

        PowerConsumer pc = root.AddComponent<PowerConsumer>();
        pc.demandAmount  = PowerDemand;

        UnitCategory cat = root.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Building;

        Airfield airfield      = root.AddComponent<Airfield>();
        airfield.strikeJetPrefab = jetPrefab;
        airfield.strikeJetCost   = StrikeJetCost;
        airfield.slots           = new Transform[Airfield.MaxSlots];

        // --- Runway down the centreline (along +Z) -------------------- //
        Spawn(root, "Runway",
              PrimitiveType.Cube, runway,
              new Vector3(0f, RunwaySize.y * 0.5f, 0f),
              RunwaySize);

        // --- Apron to the right of the runway ------------------------- //
        Spawn(root, "Apron",
              PrimitiveType.Cube, apron,
              new Vector3(5f, ApronSize.y * 0.5f, 0f),
              ApronSize);

        // --- 6 parking pads + 6 slot Transforms ----------------------- //
        // Arranged in a single column on the apron, evenly spaced along Z.
        // Slots face -X (toward the runway centreline).
        float[] zPositions = { -5f, -3f, -1f, 1f, 3f, 5f };
        for (int i = 0; i < 6; i++)
        {
            Vector3 padPos  = new Vector3(5f, 0.05f, zPositions[i]);
            Spawn(root, $"Pad_{i}",
                  PrimitiveType.Cube, pad,
                  padPos,
                  new Vector3(2.4f, 0.06f, 1.4f));

            GameObject slotGO = new GameObject($"Slot_{i}");
            slotGO.transform.SetParent(root.transform, false);
            slotGO.transform.localPosition = new Vector3(5f, 0.10f, zPositions[i]);
            slotGO.transform.localRotation = Quaternion.LookRotation(Vector3.left); // face runway
            airfield.slots[i] = slotGO.transform;
        }

        // --- Hangar at the south end of the runway -------------------- //
        Spawn(root, "Hangar",
              PrimitiveType.Cube, hangar,
              new Vector3(-3f, HangarSize.y * 0.5f, -7f),
              HangarSize);

        // --- Control tower beside the hangar -------------------------- //
        Spawn(root, "Tower",
              PrimitiveType.Cube, tower,
              new Vector3(-5f, TowerSize.y * 0.5f, -3f),
              TowerSize);

        // --- Selection ring under the whole footprint ----------------- //
        GameObject ringGO = Spawn(root, "SelectionRing",
              PrimitiveType.Cylinder, ring,
              new Vector3(0f, 0.02f, 0f),
              new Vector3(12f, 0.02f, 18f));
        ringGO.SetActive(false);
        sb.selectionIndicator = ringGO;

        SetLayerRecursive(root.transform, buildingLayer);
    }

    // ------------------------------------------------------------------ //
    // Shared helpers (mirror Humvee / Vehicle Factory builders)
    // ------------------------------------------------------------------ //

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

    private static Material LoadOrCreateMat(string name, Color color)
    {
        EnsureFolder(MatFolder);
        string path = $"{MatFolder}/{name}.mat";

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
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
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
