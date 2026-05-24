using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds StrikeJetPrefab.prefab from primitives. The result is a fully wired
/// aircraft that the Airfield can spawn into a parking slot.
///
/// Menu: Tools → RTS → Air System → Create Strike Jet Prefab
///
/// Resulting hierarchy:
///   StrikeJetPrefab/           (BoxCollider, Health (Player, 260 HP),
///     │                          SelectableAircraft, UnitCategory.Aircraft,
///     │                          AirUnitController)
///     ├── Fuselage             (grey thin long cube)
///     ├── Nose                 (grey small cone-like cube)
///     ├── WingL / WingR        (wide thin cubes — main wings)
///     ├── Tail                 (small upright fin)
///     ├── PodL / PodR          (small missile pods under each wing)
///     ├── FirePoint            (empty Transform near pods — tracer origin)
///     ├── SelectionCircle      (cyan flat cylinder — hidden by default)
///     └── HealthBar            (HealthBar component, raised height offset)
///
/// IMPORTANT — no NavMeshAgent, no UnitMovement, no UnitCombat.
/// The aircraft is steered entirely by AirUnitController.
/// </summary>
public static class CreateStrikeJetPrefab
{
    private const string PrefabPath  = "Assets/_Game/Prefabs/StrikeJetPrefab.prefab";
    private const string MatFolder   = "Assets/_Game/Materials/StrikeJet";
    private const string UnitLayer   = "Unit";

    // Stats — match the user spec
    private const float MaxHealth     = 260f;
    private const float FlightAltitude = 12f;
    private const float CruiseSpeed   = 14f;
    private const float AttackRange         = 18f;   // missile release range
    private const int   MaxAmmo             = 2;
    private const float MissileDamage       = 120f;
    private const float MissileFireDelay    = 0.75f; // seconds between the two missile releases
    private const float MissileProjectileSpeed = 30f;
    private const float AttackEgressDistance   = 15f; // straight forward distance before turning is allowed
    private const float MaxTurnRateDegrees     = 45f; // smooth recovery arc — wider when home is behind
    private const float ReturnAlignmentAngle   = 8f;  // angle (deg) below which WideReturnTurn exits
    private const float ImpactFlashDuration    = 0.2f;
    private const float BarHeight              = 2.0f;

    // Palette
    private static readonly Color FuselageGrey = new Color(0.55f, 0.58f, 0.62f);
    private static readonly Color WingGrey     = new Color(0.50f, 0.53f, 0.57f);
    private static readonly Color NoseDark     = new Color(0.20f, 0.22f, 0.26f);
    private static readonly Color PodDark      = new Color(0.18f, 0.18f, 0.20f);
    private static readonly Color RingCyan     = new Color(0.20f, 0.85f, 1.00f);

    [MenuItem("Tools/RTS/Air System/Create Strike Jet Prefab")]
    public static void Create()
    {
        int unitLayer = LayerMask.NameToLayer(UnitLayer);
        if (unitLayer < 0)
        {
            Debug.LogError($"[CreateStrikeJetPrefab] ✗ Layer '{UnitLayer}' does not exist.");
            return;
        }

        Debug.Log("[CreateStrikeJetPrefab] ── Building StrikeJetPrefab ──");

        Material fuselageMat = LoadOrCreateMat("JetFuselage", FuselageGrey);
        Material wingMat     = LoadOrCreateMat("JetWing",     WingGrey);
        Material noseMat     = LoadOrCreateMat("JetNose",     NoseDark);
        Material podMat      = LoadOrCreateMat("JetPod",      PodDark);
        Material ringMat     = LoadOrCreateMat("JetRing",     RingCyan);
        AssetDatabase.SaveAssets();

        GameObject root = new GameObject("StrikeJetPrefab");
        try
        {
            root.layer = unitLayer;
            BuildJet(root, unitLayer, fuselageMat, wingMat, noseMat, podMat, ringMat);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateStrikeJetPrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Stats: HP={MaxHealth}, ReleaseRange={AttackRange}, Damage={MissileDamage}, " +
                      $"FireDelay={MissileFireDelay}, MaxAmmo={MaxAmmo}, " +
                      $"EgressDistance={AttackEgressDistance}, TurnRate={MaxTurnRateDegrees}deg/s, " +
                      $"FlightAltitude={FlightAltitude}.\n" +
                      "  Drop into Airfield → Strike Jet Prefab.");
        }
        finally { Object.DestroyImmediate(root); }
    }

    private static void BuildJet(
        GameObject root, int unitLayer,
        Material fuselage, Material wing, Material nose, Material pod, Material ring)
    {
        // --- Gameplay components --------------------------------------- //

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size   = new Vector3(2.2f, 0.6f, 2.4f);
        col.center = new Vector3(0f, 0.3f, 0f);

        Health hp        = root.AddComponent<Health>();
        hp.team          = Health.Team.Player;
        hp.maxHealth     = MaxHealth;

        SelectableAircraft sel = root.AddComponent<SelectableAircraft>();

        UnitCategory cat = root.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Aircraft;

        AirUnitController controller    = root.AddComponent<AirUnitController>();
        controller.flightAltitude       = FlightAltitude;
        controller.cruiseSpeed          = CruiseSpeed;
        controller.attackRange          = AttackRange;
        controller.maxAmmo              = MaxAmmo;
        controller.missileDamage        = MissileDamage;
        controller.missileFireDelay     = MissileFireDelay;
        controller.missileProjectileSpeed = MissileProjectileSpeed;
        controller.attackEgressDistance = AttackEgressDistance;
        controller.maxTurnRateDegrees   = MaxTurnRateDegrees;
        controller.returnAlignmentAngle = ReturnAlignmentAngle;
        controller.impactFlashDuration  = ImpactFlashDuration;
        controller.damageType           = DamageType.Missile;

        // --- Visual children ------------------------------------------- //

        // Fuselage — long thin cube down the centre line
        Spawn(root, "Fuselage",
              PrimitiveType.Cube, fuselage,
              new Vector3(0f, 0.30f, 0f),
              new Vector3(0.45f, 0.40f, 2.20f));

        // Nose — small darker block at the front
        Spawn(root, "Nose",
              PrimitiveType.Cube, nose,
              new Vector3(0f, 0.30f, 1.25f),
              new Vector3(0.40f, 0.35f, 0.30f));

        // Wings — flat wide cubes either side
        Spawn(root, "WingL",
              PrimitiveType.Cube, wing,
              new Vector3(-0.95f, 0.30f, 0.10f),
              new Vector3(1.40f, 0.10f, 0.95f));
        Spawn(root, "WingR",
              PrimitiveType.Cube, wing,
              new Vector3( 0.95f, 0.30f, 0.10f),
              new Vector3(1.40f, 0.10f, 0.95f));

        // Tail — small vertical fin at the rear
        Spawn(root, "Tail",
              PrimitiveType.Cube, fuselage,
              new Vector3(0f, 0.65f, -0.95f),
              new Vector3(0.10f, 0.55f, 0.55f));

        // Missile pods — raised slightly so the bottom of the pod (local y
        // = 0.20 - 0.09 = 0.11) clears the runway top at world y = 0.10
        // when the aircraft root is at world y = 0.
        Spawn(root, "PodL",
              PrimitiveType.Cube, pod,
              new Vector3(-1.10f, 0.20f, 0.10f),
              new Vector3(0.18f, 0.18f, 0.85f));
        Spawn(root, "PodR",
              PrimitiveType.Cube, pod,
              new Vector3( 1.10f, 0.20f, 0.10f),
              new Vector3(0.18f, 0.18f, 0.85f));

        // FirePoint — tracer origin between the pods at the front (raised
        // to match the new pod height).
        GameObject firePoint = new GameObject("FirePoint");
        firePoint.transform.SetParent(root.transform, false);
        firePoint.transform.localPosition = new Vector3(0f, 0.22f, 0.80f);
        controller.firePoint = firePoint.transform;

        // SelectionCircle — flat cyan disc placed JUST above the runway top.
        // With the aircraft root at world y = 0 and the runway top at world
        // y = 0.10, the disc must live at local y >= 0.11 to stay visible
        // and not be hidden by the concrete.
        GameObject circle = Spawn(root, "SelectionCircle",
              PrimitiveType.Cylinder, ring,
              new Vector3(0f, 0.12f, 0f),
              new Vector3(2.6f, 0.02f, 2.6f));
        circle.SetActive(false); // SelectableAircraft.Awake hides it; mirror that
        sel.selectionCircle = circle;

        // HealthBar — raised so it clears the wings/tail silhouette
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(root.transform, false);
        HealthBar hb    = bar.AddComponent<HealthBar>();
        hb.heightOffset = BarHeight;

        // AmmoIndicator — sits a bit higher than the health bar so the dots
        // don't overlap. Dots are built at runtime by AircraftAmmoIndicator.
        GameObject ammo = new GameObject("AmmoIndicator");
        ammo.transform.SetParent(root.transform, false);
        AircraftAmmoIndicator ai = ammo.AddComponent<AircraftAmmoIndicator>();
        ai.heightOffset = BarHeight + 0.6f;

        SetLayerRecursive(root.transform, unitLayer);
    }

    // ------------------------------------------------------------------ //
    // Shared primitive / material helpers
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
