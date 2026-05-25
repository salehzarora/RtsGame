using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Creates EnemyMachineGunDefensePrefab.prefab — the enemy mirror of the player
/// Machine Gun Defense. Shares the same <see cref="BuildingTurretCombat"/>
/// component (which is team-aware), so the turret automatically hunts Player
/// units once spawned.
///
/// Menu: Tools → RTS → Buildings → Create Enemy Machine Gun Defense Prefab
///
/// Differences vs the player MachineGunDefensePrefab:
///   • Health.team        = Enemy.
///   • Palette            = red / dark red (matches other enemy units).
///   • NO SelectableBuilding (player cannot click-select enemy buildings).
///   • NO TeamColorMarker  (enemy stays red — never repainted to player color).
///   • NO PowerConsumer    (enemy has no power grid; turret always fires).
///   • BuildingTurretCombat.requirePower = false (defence-in-depth: even if a
///     future tool adds a PowerConsumer, the turret still fires).
///   • Stats (650 HP, 16u range, 8 dmg, 0.15 s cooldown) are unchanged so the
///     matchup against the player MGD is symmetric.
///
/// Resulting hierarchy:
///   EnemyMachineGunDefensePrefab/         (BoxCollider on Building layer,
///     │                                    Building, Health (Enemy, 650 HP),
///     │                                    UnitCategory.Building,
///     │                                    BuildingTurretCombat)
///     ├── Base                            (dark-red bunker base)
///     ├── Platform                        (deeper-red platform slab)
///     ├── GunPivot                        (empty Transform — rotates on Y)
///     │     ├── GunBody                   (dark cube — receiver)
///     │     ├── Barrel                    (dark cylinder)
///     │     ├── ShieldL / ShieldR         (thin angled shields)
///     │     └── FirePoint                 (Transform at muzzle tip)
///     ├── Operator                        (small enemy soldier)
///     │     ├── Body                      (red capsule torso)
///     │     └── Head                      (dark-helmet sphere)
///     └── HealthBar                       (HealthBar component, builds at runtime)
///
/// Safe to re-run — overwrites the existing asset in place.
/// </summary>
public static class CreateEnemyMachineGunDefensePrefab
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    public  const string PrefabPath    = "Assets/_Game/Prefabs/EnemyMachineGunDefensePrefab.prefab";
    private const string MatFolder     = "Assets/_Game/Materials/EnemyMGDefense";
    private const string BuildingLayer = "Building";

    // Stats — same numbers as the player MGD so the matchup is symmetric.
    private const float MaxHealth       = 650f;
    private const float AttackRange     = 16f;
    private const float AttackDamage    = 8f;
    private const float AttackCooldown  = 0.15f;
    private const float TurretTurnSpeed = 220f;
    private const float AimTolerance    = 20f;

    // Palette — red / dark-red so the enemy reads at a glance against the
    // player's olive/concrete colour scheme.
    private static readonly Color BunkerDarkRed  = new Color(0.45f, 0.18f, 0.18f);   // base bunker
    private static readonly Color PlatformRed    = new Color(0.30f, 0.12f, 0.12f);   // metal platform
    private static readonly Color GunDark        = new Color(0.18f, 0.18f, 0.18f);
    private static readonly Color BarrelBlack    = new Color(0.10f, 0.10f, 0.10f);
    private static readonly Color OperatorRed    = new Color(0.55f, 0.12f, 0.10f);   // soldier uniform
    private static readonly Color HelmetBlack    = new Color(0.10f, 0.07f, 0.07f);   // soldier helmet

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Buildings/Create Enemy Machine Gun Defense Prefab")]
    public static void Create()
    {
        int buildingLayer = LayerMask.NameToLayer(BuildingLayer);
        if (buildingLayer < 0)
        {
            Debug.LogError($"[CreateEnemyMachineGunDefensePrefab] ✗ Layer '{BuildingLayer}' does not exist.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add 'Building' user layer.");
            return;
        }

        Debug.Log("[CreateEnemyMachineGunDefensePrefab] ── Building EnemyMachineGunDefensePrefab ──");

        Material bunkerMat   = LoadOrCreateMat("EnemyMGD_Bunker",   BunkerDarkRed);
        Material platformMat = LoadOrCreateMat("EnemyMGD_Platform", PlatformRed);
        Material gunMat      = LoadOrCreateMat("EnemyMGD_Gun",      GunDark);
        Material barrelMat   = LoadOrCreateMat("EnemyMGD_Barrel",   BarrelBlack);
        Material operatorMat = LoadOrCreateMat("EnemyMGD_Operator", OperatorRed);
        Material helmetMat   = LoadOrCreateMat("EnemyMGD_Helmet",   HelmetBlack);
        AssetDatabase.SaveAssets();

        GameObject root = new GameObject("EnemyMachineGunDefensePrefab");
        try
        {
            root.layer = buildingLayer;
            BuildTurret(root, buildingLayer,
                        bunkerMat, platformMat, gunMat, barrelMat, operatorMat, helmetMat);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateEnemyMachineGunDefensePrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Stats: HP={MaxHealth}, Range={AttackRange}, Damage={AttackDamage}, " +
                      $"Cooldown={AttackCooldown}, Team=Enemy.\n" +
                      "  Place via Tools → RTS → Enemy → Place Enemy Machine Gun Defense.");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // ------------------------------------------------------------------ //
    // Construction — mirrors the player MGD layout / scale exactly
    // ------------------------------------------------------------------ //

    private static void BuildTurret(
        GameObject root, int buildingLayer,
        Material bunker, Material platformMat, Material gun, Material barrel,
        Material operatorMat, Material helmet)
    {
        // --- Gameplay components on the root --------------------------- //

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size   = new Vector3(2.0f, 2.0f, 2.0f);
        col.center = new Vector3(0f,   1.0f, 0f);

        Building b   = root.AddComponent<Building>();
        b.buildingName = "Enemy Machine Gun Defense";
        b.cost         = 0;   // not constructible — placed via editor tool

        Health hp     = root.AddComponent<Health>();
        hp.team       = Health.Team.Enemy;
        hp.maxHealth  = MaxHealth;

        UnitCategory cat = root.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Building;

        BuildingTurretCombat combat = root.AddComponent<BuildingTurretCombat>();
        combat.attackRange         = AttackRange;
        combat.attackDamage        = AttackDamage;
        combat.attackCooldown      = AttackCooldown;
        combat.damageType          = DamageType.MachineGun;
        combat.scanInterval        = 0.2f;
        combat.turretTurnSpeed     = TurretTurnSpeed;
        combat.aimToleranceDegrees = AimTolerance;
        combat.tracerColor         = new Color(1f, 0.55f, 0.15f);   // warmer enemy tracer
        combat.tracerDuration      = 0.04f;
        combat.tracerWidth         = 0.045f;
        combat.requirePower        = false;                          // enemy has no power grid

        // Explicitly NO SelectableBuilding, TeamColorMarker, PowerConsumer —
        // see the class-level summary for the reasoning.

        // --- Visual children ------------------------------------------- //

        // 1. Dark-red bunker base
        Spawn(root, "Base",
              PrimitiveType.Cube, bunker,
              new Vector3(0f, 0.40f, 0f),
              new Vector3(2.0f, 0.80f, 2.0f));

        // 2. Deeper-red platform on top of the base
        Spawn(root, "Platform",
              PrimitiveType.Cube, platformMat,
              new Vector3(0f, 0.90f, 0f),
              new Vector3(1.6f, 0.20f, 1.6f));

        // 3. Rotating turret pivot — BuildingTurretCombat rotates this.
        GameObject pivot = new GameObject("GunPivot");
        pivot.transform.SetParent(root.transform, false);
        pivot.transform.localPosition = new Vector3(0f, 1.10f, 0f);
        combat.turretPivot = pivot.transform;

        // 4. Gun body
        Spawn(pivot, "GunBody",
              PrimitiveType.Cube, gun,
              new Vector3(0f, 0.15f, -0.05f),
              new Vector3(0.45f, 0.35f, 0.85f));

        // 5. Barrel — cylinder pointing +Z (rotate cylinder default +Y axis to +Z)
        GameObject barrelGO = Spawn(pivot, "Barrel",
              PrimitiveType.Cylinder, barrel,
              new Vector3(0f, 0.20f, 0.85f),
              new Vector3(0.12f, 0.55f, 0.12f));
        barrelGO.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        // 6. Side shields — thin slabs flanking the gun
        GameObject shieldL = Spawn(pivot, "ShieldL",
              PrimitiveType.Cube, platformMat,
              new Vector3(-0.35f, 0.20f, 0.10f),
              new Vector3(0.06f, 0.45f, 0.55f));
        shieldL.transform.localRotation = Quaternion.Euler(0f, -10f, 0f);

        GameObject shieldR = Spawn(pivot, "ShieldR",
              PrimitiveType.Cube, platformMat,
              new Vector3(0.35f, 0.20f, 0.10f),
              new Vector3(0.06f, 0.45f, 0.55f));
        shieldR.transform.localRotation = Quaternion.Euler(0f, 10f, 0f);

        // 7. FirePoint — Transform at the muzzle tip
        GameObject firePoint = new GameObject("FirePoint");
        firePoint.transform.SetParent(pivot.transform, false);
        firePoint.transform.localPosition = new Vector3(0f, 0.20f, 1.20f);
        combat.firePoint = firePoint.transform;

        // 8. Enemy operator parented to the pivot so he rotates with the gun.
        GameObject operatorGO = new GameObject("Operator");
        operatorGO.transform.SetParent(pivot.transform, false);
        operatorGO.transform.localPosition = new Vector3(0f, 0.00f, -0.65f);

        Spawn(operatorGO, "Body",
              PrimitiveType.Capsule, operatorMat,
              new Vector3(0f, 0.45f, 0f),
              new Vector3(0.32f, 0.40f, 0.32f));

        Spawn(operatorGO, "Head",
              PrimitiveType.Sphere, helmet,
              new Vector3(0f, 0.95f, 0f),
              new Vector3(0.26f, 0.26f, 0.26f));

        // 9. HealthBar — same as other enemy units; HealthBar builds its own
        //    visuals at runtime by reading parent's Health.
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(root.transform, false);
        bar.transform.localPosition = Vector3.zero;
        HealthBar hb = bar.AddComponent<HealthBar>();
        hb.heightOffset = 2.4f;

        // --- Layer everything to "Building" ---------------------------- //
        SetLayerRecursive(root.transform, buildingLayer);
    }

    // ------------------------------------------------------------------ //
    // Primitive + material helpers (same pattern as the player MGD builder)
    // ------------------------------------------------------------------ //

    private static GameObject Spawn(GameObject parent, string name, PrimitiveType type,
                                    Material mat, Vector3 localPos, Vector3 localScale)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = localPos;
        go.transform.localScale    = localScale;

        // Strip the auto-added collider — only the root BoxCollider should
        // accept selection / scan raycasts.
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

        Material m    = AssetDatabase.LoadAssetAtPath<Material>(path);
        Shader target = ResolveShader();

        if (m == null)
        {
            m = new Material(target) { name = name };
            AssetDatabase.CreateAsset(m, path);
            Debug.Log($"[CreateEnemyMachineGunDefensePrefab]   Created material: {path}");
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
        RenderPipelineAsset rp = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
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
