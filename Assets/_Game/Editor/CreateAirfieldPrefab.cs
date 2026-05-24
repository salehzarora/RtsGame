using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds AirfieldPrefab.prefab from primitives with a realistic departure
/// layout: a long runway down the centreline, a parallel apron / taxiway to
/// the east, six parking pads arranged in three pairs along the apron, and
/// the markers the takeoff queue needs (one taxi point per slot, two runway
/// lanes with queue/start/end points, plus a landing approach point).
///
/// Menu: Tools → RTS → Air System → Create Airfield Prefab
///
/// Resulting hierarchy:
///   AirfieldPrefab/             (BoxCollider, Building, SelectableBuilding,
///     │                            PowerConsumer (40), UnitCategory.Building,
///     │                            Airfield (slots[6], taxiPoints[6],
///     │                            runwayQueue/start/end A&B, landingApproach))
///     ├── Runway                (long dark cube, +Z direction)
///     ├── Apron                 (lighter slab east of the runway)
///     ├── Pad_0 / Slot_0        \
///     ├── Pad_1 / Slot_1        | three pairs along the apron,
///     ├── Pad_2 / Slot_2        | even-index slots prefer Lane A,
///     ├── Pad_3 / Slot_3        | odd-index slots prefer Lane B
///     ├── Pad_4 / Slot_4        |
///     ├── Pad_5 / Slot_5        /
///     ├── Taxi_0 .. Taxi_5      (apron-edge waypoint per slot)
///     ├── Lane_A / Lane_B markers:
///     │     RunwayQueuePoint_A/B, TakeoffStart_A/B, TakeoffEnd_A/B
///     ├── LandingApproachPoint  (north of runway end, opposite takeoff dir)
///     ├── Hangar, Tower         (visual fluff, west of runway)
///     └── SelectionRing         (cyan disc, hidden by default)
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

    // Y baseline — all gameplay markers (slots, taxi points, lane corridors,
    // queue / start / end / landing-approach) live at GroundY = 0 in the
    // prefab's local space. The Airfield root itself is placed at world y=0
    // via Building.placementYOffsetOverride (= 0), so the entire ground
    // layer is at world y=0 too. Surface MESHES (runway, apron, pads, taxi
    // strips) supply the visible "concrete" thickness above this baseline.
    private const float GroundY        = 0f;
    private const float SurfaceThickness = 0.10f;
    private const float SurfaceTop     = SurfaceThickness; // top of the concrete = 0.10
    // Parking pads share the apron's thickness so there's no 3D lip the
    // aircraft would otherwise clip. Their colour distinguishes them visually.
    private const float PadThickness   = SurfaceThickness;
    private const float PadTopY        = SurfaceTop;

    // Sizes — runway widened by 30% (5 → 6.5) so aircraft bodies fit with
    // headroom on both lanes; apron widened by 25% (4 → 5).
    private static readonly Vector3 RunwaySize = new Vector3(6.5f, SurfaceThickness, 24f);
    private static readonly Vector3 ApronSize  = new Vector3(5f,   SurfaceThickness, 20f);
    private static readonly Vector3 HangarSize = new Vector3(4f,   3.0f,             4f);
    private static readonly Vector3 TowerSize  = new Vector3(1.4f, 4.5f,             1.4f);

    // Palette
    private static readonly Color RunwayDark    = new Color(0.18f, 0.18f, 0.20f);
    private static readonly Color ApronConcrete = new Color(0.55f, 0.55f, 0.55f);
    private static readonly Color PadTan        = new Color(0.62f, 0.58f, 0.42f);
    private static readonly Color HangarGrey    = new Color(0.42f, 0.42f, 0.45f);
    private static readonly Color TowerGrey     = new Color(0.32f, 0.36f, 0.40f);
    private static readonly Color RingCyan      = new Color(0.20f, 0.85f, 1.00f);

    // Slot Z positions: three pairs along the apron.
    // Pair 1 (slots 0/1) at z = -8/-6, pair 2 (2/3) at z = -1/+1, pair 3 (4/5) at z = +6/+8.
    private static readonly float[] SlotZ = { -8f, -6f, -1f, 1f, 6f, 8f };

    // X positions: apron pushed slightly east of the previous layout so the
    // wider runway + lane-B corridor still fits.
    private const float ApronX = 7.0f;
    private const float TaxiX  = 5.0f;

    // Runway lane endpoints — 4 units of X separation so jet wings don't clip.
    private const float LaneAX = -2.0f;
    private const float LaneBX =  2.0f;

    // Z positions
    private const float SouthRunwayZ = -10f;
    private const float NorthRunwayZ =  12f;

    // Lane corridor midpoints. Lane A jets taxi south *past* the runway end
    // along the west side, then turn east onto the runway. Lane B jets stay
    // east of the runway and turn west onto the runway. Both arrive at their
    // queue point heading perpendicular to the runway, so the alignment
    // phase performs a visible ~90° turn-onto-runway.
    private static readonly Vector3 LaneAMidPos = new Vector3(-4f,   0f, -13f);
    private static readonly Vector3 LaneBMidPos = new Vector3( 4f,   0f, -11f);

    // Hold-short positions. Lane A queue is WEST of Lane A start so the jet
    // arrives at start facing east; Lane B queue is EAST of Lane B start so
    // the jet arrives at start facing west. Either way, the alignment phase
    // pivots to north (runway direction) before the roll begins.
    private const float LaneAQueueX = -4f;
    private const float LaneBQueueX =  4f;

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
                      "  Run Tools → RTS → Air System → Repair Airfield Setup if you also need " +
                      "BuildingPlacementManager wiring.");
        }
        finally { Object.DestroyImmediate(root); }
    }

    // ------------------------------------------------------------------ //
    // Airfield construction
    // ------------------------------------------------------------------ //

    private static void BuildAirfield(
        GameObject root, int buildingLayer, GameObject jetPrefab,
        Material runway, Material apron, Material pad, Material hangar, Material tower, Material ring)
    {
        // --- Gameplay components --------------------------------------- //
        // Footprint covers the wider runway plus the far-west hangar/tower
        // and the Lane-A go-around at the south end.
        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size   = new Vector3(20f, 2.0f, 28f);
        col.center = new Vector3(0f,  1.0f, 0f);

        Building b = root.AddComponent<Building>();
        b.buildingName = "Airfield";
        b.cost         = BuildingCost;
        // Airfield supplies its own surface meshes — its root pivot must sit
        // exactly at ground level so internal slot/taxi Y values stay clean.
        b.placementYOffsetOverride = 0f;

        SelectableBuilding sb = root.AddComponent<SelectableBuilding>();

        PowerConsumer pc = root.AddComponent<PowerConsumer>();
        pc.demandAmount  = PowerDemand;

        UnitCategory cat = root.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Building;

        Airfield airfield      = root.AddComponent<Airfield>();
        airfield.strikeJetPrefab = jetPrefab;
        airfield.strikeJetCost   = StrikeJetCost;
        airfield.slots           = new Transform[Airfield.MaxSlots];
        airfield.taxiPoints      = new Transform[Airfield.MaxSlots];

        // --- Runway (along +Z) ---------------------------------------- //
        Spawn(root, "Runway",
              PrimitiveType.Cube, runway,
              new Vector3(0f, RunwaySize.y * 0.5f, 0f),
              RunwaySize);

        // --- Apron / taxiway (east of runway) ------------------------- //
        Spawn(root, "Apron",
              PrimitiveType.Cube, apron,
              new Vector3(ApronX, ApronSize.y * 0.5f, 0f),
              ApronSize);

        // --- South taxiway (shared south-of-runway strip) ------------- //
        // Long east-west strip both lanes pass over: Lane A continues west
        // around the runway end; Lane B turns north back onto the runway.
        // Now 2.6u wide so the jet's wingspan (~2u) fits comfortably.
        Spawn(root, "South_Taxiway",
              PrimitiveType.Cube, apron,
              new Vector3(0.5f, SurfaceTop * 0.5f, -11.5f),
              new Vector3(13f, SurfaceThickness, 2.6f));

        // --- Lane A go-around (south-of-runway strip west of the runway) //
        // Connects the south taxiway to Lane A's queue point at (-4, -10).
        // Widened to 2.6u for consistent wing clearance.
        Spawn(root, "LaneA_GoAround",
              PrimitiveType.Cube, apron,
              new Vector3(LaneAQueueX, SurfaceTop * 0.5f, -11.5f),
              new Vector3(2.6f, SurfaceThickness, 4.0f));

        // --- Lane B link (short pad east of the east lane) ------------ //
        Spawn(root, "LaneB_Link",
              PrimitiveType.Cube, apron,
              new Vector3(LaneBQueueX, SurfaceTop * 0.5f, -10.5f),
              new Vector3(2.6f, SurfaceThickness, 2.0f));

        // --- Six pads + six slot Transforms + six taxi points --------- //
        // Each pair (0/1, 2/3, 4/5) shares a Z midpoint; slots are 2u apart.
        // Slot rotation faces -X (toward the runway centreline).
        Quaternion faceRunway = Quaternion.LookRotation(Vector3.left);

        for (int i = 0; i < Airfield.MaxSlots; i++)
        {
            float z = SlotZ[i];

            // Visible parking pad — same height as the apron so a parked
            // aircraft visual sits flush; colour difference does the work.
            Spawn(root, $"Pad_{i}",
                  PrimitiveType.Cube, pad,
                  new Vector3(ApronX, SurfaceTop * 0.5f, z),
                  new Vector3(2.4f, PadThickness, 1.6f));

            // Slot Transform — the parked-jet pose
            GameObject slot = new GameObject($"Slot_{i}");
            slot.transform.SetParent(root.transform, false);
            slot.transform.localPosition = new Vector3(ApronX, GroundY, z);
            slot.transform.localRotation = faceRunway;
            airfield.slots[i] = slot.transform;

            // Taxi point — first waypoint when leaving the slot, between
            // apron and runway, aligned in Z with the slot.
            GameObject taxi = new GameObject($"Taxi_{i}");
            taxi.transform.SetParent(root.transform, false);
            taxi.transform.localPosition = new Vector3(TaxiX, GroundY, z);
            taxi.transform.localRotation = faceRunway;
            airfield.taxiPoints[i] = taxi.transform;
        }

        // --- Lane corridor mid-points (shared per lane) --------------- //
        // Each lane gets one intermediate Transform between the per-slot
        // taxi point and the runway queue. The two corridors sit at very
        // different X values so taxi paths never overlap.
        Transform laneAMid = MakeMarker(root, "TaxiPoint_A_Mid",
            new Vector3(LaneAMidPos.x, GroundY, LaneAMidPos.z),
            Quaternion.LookRotation(Vector3.left));
        Transform laneBMid = MakeMarker(root, "TaxiPoint_B_Mid",
            new Vector3(LaneBMidPos.x, GroundY, LaneBMidPos.z),
            Quaternion.LookRotation(Vector3.left));

        airfield.laneATaxiPoints = new[] { laneAMid };
        airfield.laneBTaxiPoints = new[] { laneBMid };

        // --- Runway lane markers -------------------------------------- //
        // Both lanes take off in +Z direction (south → north). Queue points
        // sit perpendicular to the runway so the jet arrives at TakeoffStart
        // facing crosswise and AligningForTakeoff performs a visible pivot.
        airfield.runwayQueuePointA = MakeMarker(root, "RunwayQueuePoint_A",
            new Vector3(LaneAQueueX, GroundY, SouthRunwayZ), Quaternion.LookRotation(Vector3.right));
        airfield.takeoffStartA     = MakeMarker(root, "TakeoffStart_A",
            new Vector3(LaneAX,      GroundY, SouthRunwayZ), Quaternion.LookRotation(Vector3.forward));
        airfield.takeoffEndA       = MakeMarker(root, "TakeoffEnd_A",
            new Vector3(LaneAX,      GroundY, NorthRunwayZ), Quaternion.LookRotation(Vector3.forward));

        airfield.runwayQueuePointB = MakeMarker(root, "RunwayQueuePoint_B",
            new Vector3(LaneBQueueX, GroundY, SouthRunwayZ), Quaternion.LookRotation(Vector3.left));
        airfield.takeoffStartB     = MakeMarker(root, "TakeoffStart_B",
            new Vector3(LaneBX,      GroundY, SouthRunwayZ), Quaternion.LookRotation(Vector3.forward));
        airfield.takeoffEndB       = MakeMarker(root, "TakeoffEnd_B",
            new Vector3(LaneBX,      GroundY, NorthRunwayZ), Quaternion.LookRotation(Vector3.forward));

        airfield.landingApproachPoint = MakeMarker(root, "LandingApproachPoint",
            new Vector3(0f, GroundY, NorthRunwayZ + 4f),    Quaternion.LookRotation(Vector3.back));

        // --- Landing path (Lane A, active in v1) ---------------------- //
        // Approach direction is opposite to takeoff: aircraft enters at the
        // north end of the runway at altitude and lands going south. Lane A
        // takeoffStart/end markers are at z = SouthRunwayZ / NorthRunwayZ,
        // so we re-use those Z values mirrored.
        airfield.landingStartA = MakeMarker(root, "LandingStart_A",
            new Vector3(LaneAX, GroundY, NorthRunwayZ),     Quaternion.LookRotation(Vector3.back));
        airfield.landingEndA   = MakeMarker(root, "LandingEnd_A",
            new Vector3(LaneAX, GroundY, SouthRunwayZ),     Quaternion.LookRotation(Vector3.back));
        // Off-runway turn point — east of LandingEnd_A, on the south taxiway,
        // so the taxi-back path stays on concrete.
        airfield.landingExitA  = MakeMarker(root, "LandingExit_A",
            new Vector3(3f,      GroundY, SouthRunwayZ - 1f), Quaternion.LookRotation(Vector3.right));

        // --- Landing path (Lane B, reserved for future use) ----------- //
        airfield.landingStartB = MakeMarker(root, "LandingStart_B",
            new Vector3(LaneBX, GroundY, NorthRunwayZ),     Quaternion.LookRotation(Vector3.back));
        airfield.landingEndB   = MakeMarker(root, "LandingEnd_B",
            new Vector3(LaneBX, GroundY, SouthRunwayZ),     Quaternion.LookRotation(Vector3.back));
        airfield.landingExitB  = MakeMarker(root, "LandingExit_B",
            new Vector3(5f,      GroundY, SouthRunwayZ - 1f), Quaternion.LookRotation(Vector3.right));

        // --- Hangar + tower ------------------------------------------- //
        // Moved 5 units west from the previous layout so they no longer sit
        // on top of Lane A's taxi corridor / queue point. Aircraft on the
        // left lane now have clear visual separation from the building.
        Spawn(root, "Hangar",
              PrimitiveType.Cube, hangar,
              new Vector3(-9f, HangarSize.y * 0.5f, -7f),
              HangarSize);

        Spawn(root, "Tower",
              PrimitiveType.Cube, tower,
              new Vector3(-10f, TowerSize.y * 0.5f, -2f),
              TowerSize);

        // --- Selection ring ------------------------------------------- //
        GameObject ringGO = Spawn(root, "SelectionRing",
              PrimitiveType.Cylinder, ring,
              new Vector3(2f, 0.02f, 0f),
              new Vector3(14f, 0.02f, 26f));
        ringGO.SetActive(false);
        sb.selectionIndicator = ringGO;

        SetLayerRecursive(root.transform, buildingLayer);
    }

    /// <summary>Creates a named empty Transform at a local pose, parented to root.</summary>
    private static Transform MakeMarker(GameObject parent, string name, Vector3 localPos, Quaternion localRot)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        return go.transform;
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
