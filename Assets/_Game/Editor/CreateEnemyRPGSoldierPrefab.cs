using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// Builds EnemyRPGSoldierPrefab.prefab — a stationary enemy guard unit that
/// auto-attacks nearby Player targets with RPG rockets. Mirrors the player
/// <c>RPGSoldierPrefab</c> but on the Enemy team, with no SelectableUnit, no
/// UnitColorMarker, and a <see cref="GroundAutoAttackController"/> driving
/// the existing <see cref="RocketCombat"/> instead of player commands. The
/// auto-attack controller is team-aware (Enemy unit hunts Player team), so
/// the same component powers player and enemy guards.
///
/// Menu: Tools → RTS → Units → Create Enemy RPG Soldier Prefab
///
/// Resulting hierarchy:
///   EnemyRPGSoldierPrefab/              (CapsuleCollider, Health (Enemy, 90 HP),
///     │                                  UnitCategory.Infantry, RocketCombat,
///     │                                  GroundAutoAttackController,
///     │                                  UnitMovement, NavMeshAgent, HealthBar)
///     ├── SoldierVisualRoot/
///     │     └── PrimitivePlaceholder/   (red uniform, dark helmet, etc.)
///     │           └── EnemyRPGLauncher  (shoulder-mounted tube)
///     │           └── EnemyRPGNose      (tip of the tube)
///     └── FirePoint                     (Transform at the launcher tip — rocket origin)
///
/// IMPORTANT — no SelectableUnit. The player cannot click on enemy units to
/// give them orders; that's a deliberate gameplay decision. UnitMovement and
/// NavMeshAgent are present because RocketCombat's RequireComponent demands
/// them, but the guard never auto-chases — GroundAutoAttackController drops
/// targets that leave the lose-radius instead of pursuing.
/// </summary>
public static class CreateEnemyRPGSoldierPrefab
{
    private const string PrefabPath = "Assets/_Game/Prefabs/EnemyRPGSoldierPrefab.prefab";
    private const string MatFolder  = "Assets/_Game/Materials/EnemyRPGSoldier";
    private const string UnitLayer  = "Unit";

    // Stats — same numbers as the player RPG Soldier so the matchup is symmetric.
    private const float MaxHealth        = 90f;
    private const float MoveSpeed        = 3.5f;
    private const float AttackRange      = 12f;
    private const float AntiAirRange     = 14f;
    private const float AttackDamage     = 70f;
    private const float AttackCooldown   = 2.5f;
    private const float MinRange         = 3f;
    private const float ProjectileSpeed  = 16f;
    private const float HomingTurnRate   = 90f;
    private const float RocketLifetime   = 4.5f;
    private const float AircraftHitRadius = 1.8f;
    private const float GroundHitRadius   = 0.8f;
    private const float BarHeight         = 1.7f;
    private const float ScanRadius        = 18f;
    private const float ScanInterval      = 0.1f;   // snappy auto-acquire — see RocketCombat first-shot path

    // Palette — red / black so the enemy reads at a glance against the player's
    // olive-green infantry.
    private static readonly Color UniformRed     = new Color(0.55f, 0.12f, 0.10f);
    private static readonly Color HelmetBlack    = new Color(0.10f, 0.07f, 0.07f);
    private static readonly Color SkinTone       = new Color(0.82f, 0.66f, 0.50f);
    private static readonly Color BootsBlack     = new Color(0.10f, 0.08f, 0.08f);
    private static readonly Color GearBlack      = new Color(0.14f, 0.14f, 0.14f);
    private static readonly Color LauncherColor  = new Color(0.20f, 0.13f, 0.10f);
    private static readonly Color LauncherTip    = new Color(0.70f, 0.20f, 0.10f);

    [MenuItem("Tools/RTS/Units/Create Enemy RPG Soldier Prefab")]
    public static void Create()
    {
        int unitLayer = LayerMask.NameToLayer(UnitLayer);
        if (unitLayer < 0)
        {
            Debug.LogError($"[CreateEnemyRPGSoldierPrefab] ✗ Layer '{UnitLayer}' does not exist. " +
                           "Add it via Project Settings → Tags and Layers.");
            return;
        }

        Debug.Log("[CreateEnemyRPGSoldierPrefab] ── Building EnemyRPGSoldierPrefab ──");

        Material matUniform  = LoadOrCreateMat("EnemyRPGUniform",  UniformRed);
        Material matHelmet   = LoadOrCreateMat("EnemyRPGHelmet",   HelmetBlack);
        Material matSkin     = LoadOrCreateMat("EnemyRPGSkin",     SkinTone);
        Material matBoots    = LoadOrCreateMat("EnemyRPGBoots",    BootsBlack);
        Material matGear     = LoadOrCreateMat("EnemyRPGGear",     GearBlack);
        Material matLauncher = LoadOrCreateMat("EnemyRPGLauncher", LauncherColor);
        Material matNose     = LoadOrCreateMat("EnemyRPGLauncherTip", LauncherTip);
        AssetDatabase.SaveAssets();

        GameObject root = new GameObject("EnemyRPGSoldierPrefab");
        try
        {
            root.layer = unitLayer;
            BuildRoot(root, unitLayer,
                      matUniform, matHelmet, matSkin, matBoots, matGear,
                      matLauncher, matNose);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateEnemyRPGSoldierPrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Stats: HP={MaxHealth}, AttackRange={AttackRange}, " +
                      $"AntiAirRange={AntiAirRange}, Damage={AttackDamage}, " +
                      $"Cooldown={AttackCooldown}, ScanRadius={ScanRadius}.\n" +
                      "  Use Tools → RTS → Units → Place Enemy RPG Soldier Test to drop one into the scene.");
        }
        finally { Object.DestroyImmediate(root); }
    }

    // ------------------------------------------------------------------ //
    // Hierarchy
    // ------------------------------------------------------------------ //

    private static void BuildRoot(
        GameObject root, int unitLayer,
        Material uniform, Material helmet, Material skin, Material boots, Material gear,
        Material launcher, Material launcherTip)
    {
        // --- Gameplay components ---------------------------------------- //

        CapsuleCollider col = root.AddComponent<CapsuleCollider>();
        col.center = new Vector3(0f, 1.0f, 0f);
        col.radius = 0.45f;
        col.height = 2.0f;

        Health hp     = root.AddComponent<Health>();
        hp.team       = Health.Team.Enemy;       // critical — this is what makes it a hostile target
        hp.maxHealth  = MaxHealth;

        // NavMeshAgent + UnitMovement are required by RocketCombat. The guard
        // is stationary by default; these components exist to satisfy the
        // RequireComponent chain, not because we move.
        NavMeshAgent agent = root.AddComponent<NavMeshAgent>();
        agent.speed         = MoveSpeed;
        agent.acceleration  = 12f;
        agent.angularSpeed  = 720f;
        agent.radius        = 0.4f;
        agent.height        = 2.0f;
        agent.stoppingDistance = 0.1f;

        root.AddComponent<UnitMovement>();

        UnitCategory cat = root.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Infantry;

        // Same RocketCombat the player uses — no duplicate projectile logic.
        RocketCombat rc = root.AddComponent<RocketCombat>();
        rc.attackRange                = AttackRange;
        rc.antiAirRange               = AntiAirRange;
        rc.attackDamage               = AttackDamage;
        rc.attackCooldown             = AttackCooldown;
        rc.fireImmediatelyOnNewTarget = true;     // enemy guard reacts instantly when a player walks in
        rc.firstShotDelay             = 0.05f;
        rc.minRange                   = MinRange;
        rc.projectileSpeed            = ProjectileSpeed;
        rc.homingTurnRateDegrees      = HomingTurnRate;
        rc.rocketLifetime             = RocketLifetime;
        rc.aircraftHitRadius          = AircraftHitRadius;
        rc.groundHitRadius            = GroundHitRadius;

        // Local guard brain — uses the unified GroundAutoAttackController,
        // which is team-aware (a unit on team Enemy hunts team Player and
        // vice-versa). The older EnemyAutoAttackController is left in the
        // codebase for backward compat but new enemy units ship with the
        // unified component so they behave identically to player guards.
        GroundAutoAttackController ai = root.AddComponent<GroundAutoAttackController>();
        ai.autoAttackEnabled  = true;
        ai.detectionRadius    = ScanRadius;          // 18 — wider than player default to read as a guard
        ai.scanInterval       = ScanInterval;

        // --- Visual children -------------------------------------------- //

        GameObject visualRoot = new GameObject("SoldierVisualRoot");
        visualRoot.transform.SetParent(root.transform, false);

        GameObject placeholder = new GameObject("PrimitivePlaceholder");
        placeholder.transform.SetParent(visualRoot.transform, false);
        placeholder.transform.localPosition = new Vector3(0f, 1.0f, 0f);

        // Body + head + helmet + arms + legs + backpack — same primitive layout
        // as the player RPG Soldier, just recoloured red/black to read as hostile.
        Spawn(placeholder.transform, "Body",     PrimitiveType.Cube,    uniform,
              new Vector3(0f,  0.15f, 0f),       new Vector3(0.60f, 0.70f, 0.35f));
        Spawn(placeholder.transform, "Head",     PrimitiveType.Sphere,  skin,
              new Vector3(0f,  0.70f, 0f),       new Vector3(0.32f, 0.32f, 0.32f));
        Spawn(placeholder.transform, "Helmet",   PrimitiveType.Sphere,  helmet,
              new Vector3(0f,  0.78f, 0f),       new Vector3(0.42f, 0.22f, 0.42f));
        Spawn(placeholder.transform, "LeftArm",  PrimitiveType.Capsule, uniform,
              new Vector3(-0.42f, 0.10f, 0f),    new Vector3(0.18f, 0.40f, 0.18f));
        Spawn(placeholder.transform, "RightArm", PrimitiveType.Capsule, uniform,
              new Vector3( 0.42f, 0.10f, 0f),    new Vector3(0.18f, 0.40f, 0.18f));
        Spawn(placeholder.transform, "LeftLeg",  PrimitiveType.Capsule, boots,
              new Vector3(-0.18f, -0.60f, 0f),   new Vector3(0.20f, 0.40f, 0.20f));
        Spawn(placeholder.transform, "RightLeg", PrimitiveType.Capsule, boots,
              new Vector3( 0.18f, -0.60f, 0f),   new Vector3(0.20f, 0.40f, 0.20f));
        Spawn(placeholder.transform, "Backpack", PrimitiveType.Cube,    gear,
              new Vector3(0f,  0.10f, -0.25f),   new Vector3(0.40f, 0.50f, 0.18f));

        // RPG launcher on the right shoulder — same as the player version,
        // recoloured to a darker tube + red-orange tip.
        GameObject launcherTube = Spawn(placeholder.transform, "EnemyRPGLauncher",
              PrimitiveType.Cube, launcher,
              new Vector3(0.30f, 0.35f, 0.10f),
              new Vector3(0.16f, 0.16f, 1.20f));
        launcherTube.transform.localRotation = Quaternion.Euler(-10f, 8f, 0f);

        Spawn(launcherTube.transform, "EnemyRPGNose", PrimitiveType.Sphere, launcherTip,
              new Vector3(0f, 0f, 0.50f), new Vector3(0.55f, 0.55f, 0.55f));

        // FirePoint — Transform aligned with the launcher's muzzle, used by
        // RocketCombat as the rocket spawn origin. Sits on the root so its
        // world position tracks the unit pivot cleanly.
        GameObject fp = new GameObject("FirePoint");
        fp.transform.SetParent(root.transform, false);
        fp.transform.localPosition = new Vector3(0.32f, 1.45f, 0.60f);
        fp.transform.localRotation = Quaternion.identity;
        rc.firePoint = fp.transform;

        // HealthBar so the player can see the enemy's HP drop while testing.
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(root.transform, false);
        HealthBar hb    = bar.AddComponent<HealthBar>();
        hb.heightOffset = BarHeight;

        SetLayerRecursive(root.transform, unitLayer);
    }

    // ------------------------------------------------------------------ //
    // Helpers — same pattern as the rest of the editor tools
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
