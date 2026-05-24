using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// Builds RPGSoldierPrefab.prefab from primitives. The result is a fully-wired
/// anti-vehicle / anti-air infantry unit that can be assigned to a Barracks
/// via the rpgSoldierPrefab field on UnitProducer.
///
/// Menu: Tools → RTS → Units → Create RPG Soldier Prefab
///
/// Resulting hierarchy:
///   RPGSoldierPrefab/                   (CapsuleCollider, Health (Player, 90 HP),
///     │                                  SelectableUnit, UnitCategory.Infantry,
///     │                                  UnitMovement, NavMeshAgent, RocketCombat,
///     │                                  UnitColorMarker, HealthBar)
///     ├── SoldierVisualRoot/
///     │     └── PrimitivePlaceholder/   (body, head, helmet, arms, legs, backpack)
///     │           └── RPGLauncher       (shoulder-mounted tube, distinct from rifle)
///     │           └── RPGNose           (tip of the tube)
///     ├── FirePoint                     (Transform at the launcher tip — rocket origin)
///     └── SelectionCircle               (rust-orange ring, hidden by default)
///
/// IMPORTANT — no UnitCombat. RocketCombat is the projectile-based replacement
/// and lives on the root alongside Health / UnitMovement. The visual is similar
/// to the regular Soldier but uses a darker uniform and adds a shoulder-mounted
/// rocket tube so the two infantry types are immediately distinguishable.
/// </summary>
public static class CreateRPGSoldierPrefab
{
    private const string PrefabPath = "Assets/_Game/Prefabs/RPGSoldierPrefab.prefab";
    private const string MatFolder  = "Assets/_Game/Materials/RPGSoldier";
    private const string UnitLayer  = "Unit";

    // Stats per spec
    private const float MaxHealth      = 90f;
    private const float MoveSpeed      = 3.5f;
    private const float AttackRange    = 12f;
    private const float AntiAirRange   = 14f;
    private const float AttackDamage   = 70f;
    private const float AttackCooldown = 2.5f;
    private const float MinRange       = 3f;
    private const float ProjectileSpeed = 16f;
    private const float HomingTurnRate  = 90f;
    private const float RocketLifetime  = 4.5f;
    private const float AircraftHitRadius = 1.8f;
    private const float GroundHitRadius   = 0.8f;
    private const float BarHeight         = 1.7f;

    // Palette — slightly darker than regular Soldier so the two infantry types
    // are visually distinct in a crowded base view.
    private static readonly Color UniformDark    = new Color(0.22f, 0.28f, 0.16f);  // deeper olive
    private static readonly Color HelmetDark     = new Color(0.14f, 0.20f, 0.10f);
    private static readonly Color SkinTone       = new Color(0.85f, 0.70f, 0.55f);
    private static readonly Color BootsDark      = new Color(0.13f, 0.11f, 0.08f);
    private static readonly Color GearDark       = new Color(0.18f, 0.18f, 0.18f);
    private static readonly Color LauncherColor  = new Color(0.30f, 0.18f, 0.10f);  // rust-brown tube
    private static readonly Color LauncherTip    = new Color(0.55f, 0.30f, 0.12f);  // brighter tip
    private static readonly Color SelectionRust  = new Color(1.00f, 0.55f, 0.20f);  // RPG accent

    [MenuItem("Tools/RTS/Units/Create RPG Soldier Prefab")]
    public static void Create()
    {
        int unitLayer = LayerMask.NameToLayer(UnitLayer);
        if (unitLayer < 0)
        {
            Debug.LogError($"[CreateRPGSoldierPrefab] ✗ Layer '{UnitLayer}' does not exist. " +
                           "Add it via Project Settings → Tags and Layers.");
            return;
        }

        Debug.Log("[CreateRPGSoldierPrefab] ── Building RPGSoldierPrefab ──");

        Material matUniform   = LoadOrCreateMat("RPGUniform",   UniformDark);
        Material matHelmet    = LoadOrCreateMat("RPGHelmet",    HelmetDark);
        Material matSkin      = LoadOrCreateMat("RPGSkin",      SkinTone);
        Material matBoots     = LoadOrCreateMat("RPGBoots",     BootsDark);
        Material matGear      = LoadOrCreateMat("RPGGear",      GearDark);
        Material matLauncher  = LoadOrCreateMat("RPGLauncher",  LauncherColor);
        Material matNose      = LoadOrCreateMat("RPGLauncherTip", LauncherTip);
        Material matRing      = LoadOrCreateMat("RPGRing",      SelectionRust);
        AssetDatabase.SaveAssets();

        GameObject root = new GameObject("RPGSoldierPrefab");
        try
        {
            root.layer = unitLayer;
            BuildRoot(root, unitLayer,
                      matUniform, matHelmet, matSkin, matBoots, matGear,
                      matLauncher, matNose, matRing);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateRPGSoldierPrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Stats: HP={MaxHealth}, MoveSpeed={MoveSpeed}, AttackRange={AttackRange}, " +
                      $"AntiAirRange={AntiAirRange}, Damage={AttackDamage}, Cooldown={AttackCooldown}.\n" +
                      "  Next: Tools → RTS → Units → Repair Barracks RPG Production to wire it into Barracks.");
        }
        finally { Object.DestroyImmediate(root); }
    }

    // ------------------------------------------------------------------ //
    // Hierarchy
    // ------------------------------------------------------------------ //

    private static void BuildRoot(
        GameObject root, int unitLayer,
        Material uniform, Material helmet, Material skin, Material boots, Material gear,
        Material launcher, Material launcherTip, Material ring)
    {
        // --- Gameplay components --------------------------------------- //

        CapsuleCollider col = root.AddComponent<CapsuleCollider>();
        col.center = new Vector3(0f, 1.0f, 0f);
        col.radius = 0.45f;
        col.height = 2.0f;

        Health hp     = root.AddComponent<Health>();
        hp.team       = Health.Team.Player;
        hp.maxHealth  = MaxHealth;

        NavMeshAgent agent = root.AddComponent<NavMeshAgent>();
        agent.speed         = MoveSpeed;
        agent.acceleration  = 12f;
        agent.angularSpeed  = 720f;
        agent.radius        = 0.4f;
        agent.height        = 2.0f;
        agent.stoppingDistance = 0.1f;

        root.AddComponent<UnitMovement>();
        SelectableUnit sel = root.AddComponent<SelectableUnit>();

        UnitCategory cat = root.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Infantry;

        RocketCombat rc = root.AddComponent<RocketCombat>();
        rc.attackRange         = AttackRange;
        rc.antiAirRange        = AntiAirRange;
        rc.attackDamage        = AttackDamage;
        rc.attackCooldown      = AttackCooldown;
        rc.minRange            = MinRange;
        rc.projectileSpeed     = ProjectileSpeed;
        rc.homingTurnRateDegrees = HomingTurnRate;
        rc.rocketLifetime      = RocketLifetime;
        rc.aircraftHitRadius   = AircraftHitRadius;
        rc.groundHitRadius     = GroundHitRadius;

        // --- Visual children -------------------------------------------- //

        GameObject visualRoot = new GameObject("SoldierVisualRoot");
        visualRoot.transform.SetParent(root.transform, false);

        GameObject placeholder = new GameObject("PrimitivePlaceholder");
        placeholder.transform.SetParent(visualRoot.transform, false);
        // Lift the visual so the capsule's bottom sits at y=0.
        placeholder.transform.localPosition = new Vector3(0f, 1.0f, 0f);

        // Soldier body (same primitive shape as the regular Soldier, just
        // recoloured to darker olive).
        Spawn(placeholder.transform, "Body", PrimitiveType.Cube, uniform,
              new Vector3(0f,  0.15f, 0f),  new Vector3(0.60f, 0.70f, 0.35f));
        Spawn(placeholder.transform, "Head", PrimitiveType.Sphere, skin,
              new Vector3(0f,  0.70f, 0f),  new Vector3(0.32f, 0.32f, 0.32f));
        Spawn(placeholder.transform, "Helmet", PrimitiveType.Sphere, helmet,
              new Vector3(0f,  0.78f, 0f),  new Vector3(0.42f, 0.22f, 0.42f));
        Spawn(placeholder.transform, "LeftArm", PrimitiveType.Capsule, uniform,
              new Vector3(-0.42f, 0.10f, 0f),  new Vector3(0.18f, 0.40f, 0.18f));
        Spawn(placeholder.transform, "RightArm", PrimitiveType.Capsule, uniform,
              new Vector3( 0.42f, 0.10f, 0f),  new Vector3(0.18f, 0.40f, 0.18f));
        Spawn(placeholder.transform, "LeftLeg", PrimitiveType.Capsule, boots,
              new Vector3(-0.18f, -0.60f, 0f), new Vector3(0.20f, 0.40f, 0.20f));
        Spawn(placeholder.transform, "RightLeg", PrimitiveType.Capsule, boots,
              new Vector3( 0.18f, -0.60f, 0f), new Vector3(0.20f, 0.40f, 0.20f));
        Spawn(placeholder.transform, "Backpack", PrimitiveType.Cube, gear,
              new Vector3(0f,  0.10f, -0.25f), new Vector3(0.40f, 0.50f, 0.18f));

        // RPG launcher — long rust-brown tube on the right shoulder, angled
        // forward and slightly up. Visibly distinct from the rifle on the
        // standard Soldier.
        GameObject launcherTube = Spawn(placeholder.transform, "RPGLauncher",
              PrimitiveType.Cube, launcher,
              new Vector3(0.30f, 0.35f, 0.10f),
              new Vector3(0.16f, 0.16f, 1.20f));
        launcherTube.transform.localRotation = Quaternion.Euler(-10f, 8f, 0f);

        // Bright orange nose at the front of the tube so the rocket origin
        // reads clearly in screenshots.
        Spawn(launcherTube.transform, "RPGNose", PrimitiveType.Sphere, launcherTip,
              new Vector3(0f, 0f, 0.50f),  new Vector3(0.55f, 0.55f, 0.55f));

        // FirePoint — Transform aligned with the launcher's muzzle, used by
        // RocketCombat as the rocket spawn origin. Place it on the ROOT so its
        // world position tracks the unit pivot cleanly (placeholder offsets
        // would otherwise compound).
        GameObject fp = new GameObject("FirePoint");
        fp.transform.SetParent(root.transform, false);
        fp.transform.localPosition = new Vector3(0.32f, 1.45f, 0.60f);
        fp.transform.localRotation = Quaternion.identity;
        rc.firePoint = fp.transform;

        // SelectionCircle — flat rust-orange disc beneath the unit, hidden by default.
        GameObject circle = Spawn(root.transform, "SelectionCircle",
              PrimitiveType.Cylinder, ring,
              new Vector3(0f, 0.05f, 0f),
              new Vector3(1.5f, 0.02f, 1.5f));
        circle.SetActive(false);
        sel.selectionCircle = circle;

        // HealthBar — same pattern as the existing units.
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(root.transform, false);
        HealthBar hb    = bar.AddComponent<HealthBar>();
        hb.heightOffset = BarHeight;

        SetLayerRecursive(root.transform, unitLayer);
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static GameObject Spawn(Transform parent, string name, PrimitiveType type,
                                    Material mat, Vector3 localPos, Vector3 localScale)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
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
