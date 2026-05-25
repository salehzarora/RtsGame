using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One-click editor tool that creates the MachineGunDefensePrefab from scratch
/// using built-in Unity primitives. The resulting prefab is a defensive building
/// the Dozer can construct (cost 250, power demand 15):
///
///   MachineGunDefensePrefab/                (BoxCollider on Building layer,
///     │                                      Building, SelectableBuilding,
///     │                                      Health (Player, 650 HP),
///     │                                      UnitCategory.Building,
///     │                                      PowerConsumer (15),
///     │                                      BuildingTurretCombat,
///     │                                      TeamColorMarker)
///     ├── Base                              (squat concrete cube — bunker base)
///     ├── Platform                          (slimmer metal cube on top of base)
///     ├── GunPivot                          (empty Transform, rotates on Y)
///     │     ├── GunBody                     (dark cube — receiver housing)
///     │     ├── Barrel                      (dark cylinder pointing +Z)
///     │     ├── ShieldL / ShieldR           (thin angled side shields)
///     │     └── FirePoint                   (empty Transform at muzzle tip)
///     ├── Operator                          (small soldier behind the gun)
///     │     ├── Body                        (team-colored capsule torso)
///     │     └── Helmet                      (small team-colored sphere on top)
///     ├── SelectionRing                     (cyan flat cylinder under the base)
///     └── HealthBar                         (HealthBar component, builds visuals at runtime)
///
/// Menu: Tools → RTS → Buildings → Create Machine Gun Defense Prefab
///
/// Safe to re-run: an existing MachineGunDefensePrefab.prefab is overwritten in
/// place so all references (BuildingPlacementManager.machineGunDefensePrefab,
/// HUD button label, etc.) keep working.
/// </summary>
public static class CreateMachineGunDefensePrefab
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    private const string PrefabPath    = "Assets/_Game/Prefabs/MachineGunDefensePrefab.prefab";
    private const string MatFolder     = "Assets/_Game/Materials/MGDefense";
    private const string BuildingLayer = "Building";

    // Stats
    private const float MaxHealth      = 650f;
    private const int   BuildingCost   = 250;
    private const int   PowerDemand    = 15;
    private const float AttackRange    = 16f;
    private const float AttackDamage   = 8f;
    private const float AttackCooldown = 0.15f;
    private const float TurretTurnSpeed = 220f;
    private const float AimTolerance    = 20f;

    // Palette
    private static readonly Color ConcreteGrey   = new Color(0.55f, 0.55f, 0.52f);
    private static readonly Color MetalGrey      = new Color(0.40f, 0.42f, 0.44f);
    private static readonly Color GunDark        = new Color(0.18f, 0.18f, 0.18f);
    private static readonly Color BarrelBlack    = new Color(0.10f, 0.10f, 0.10f);
    private static readonly Color OperatorBody   = new Color(0.30f, 0.40f, 0.25f);   // fallback before team color paints over
    private static readonly Color HelmetGreen    = new Color(0.22f, 0.30f, 0.18f);   // fallback before team color paints over
    private static readonly Color RingCyan       = new Color(0.20f, 0.85f, 1.00f);

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Buildings/Create Machine Gun Defense Prefab")]
    public static void Create()
    {
        int buildingLayer = LayerMask.NameToLayer(BuildingLayer);
        if (buildingLayer < 0)
        {
            Debug.LogError($"[CreateMachineGunDefensePrefab] ✗ Layer '{BuildingLayer}' does not exist.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add 'Building' user layer.");
            return;
        }

        Debug.Log("[CreateMachineGunDefensePrefab] ── Building MachineGunDefensePrefab ──");

        Material concreteMat = LoadOrCreateMat("MGD_Concrete", ConcreteGrey);
        Material metalMat    = LoadOrCreateMat("MGD_Metal",    MetalGrey);
        Material gunMat      = LoadOrCreateMat("MGD_Gun",      GunDark);
        Material barrelMat   = LoadOrCreateMat("MGD_Barrel",   BarrelBlack);
        Material operatorMat = LoadOrCreateMat("MGD_Operator", OperatorBody);
        Material helmetMat   = LoadOrCreateMat("MGD_Helmet",   HelmetGreen);
        Material ringMat     = LoadOrCreateMat("MGD_Ring",     RingCyan);
        AssetDatabase.SaveAssets();

        GameObject root = new GameObject("MachineGunDefensePrefab");
        try
        {
            root.layer = buildingLayer;
            BuildTurret(root, buildingLayer,
                        concreteMat, metalMat, gunMat, barrelMat, operatorMat, helmetMat, ringMat);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateMachineGunDefensePrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Stats: HP={MaxHealth}, Cost={BuildingCost}, Power={PowerDemand}, " +
                      $"Range={AttackRange}, Damage={AttackDamage}, Cooldown={AttackCooldown}.\n" +
                      "  Run Tools → RTS → Construction → Repair Dozer Build Options to wire it " +
                      "into BuildingPlacementManager + Dozer build panel.");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // ------------------------------------------------------------------ //
    // MG Defense construction
    // ------------------------------------------------------------------ //

    private static void BuildTurret(
        GameObject root, int buildingLayer,
        Material concrete, Material metal, Material gun, Material barrel,
        Material operatorMat, Material helmet, Material ring)
    {
        // --- Gameplay components on the root --------------------------- //

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size   = new Vector3(2.0f, 2.0f, 2.0f);
        col.center = new Vector3(0f,   1.0f, 0f);

        Building b   = root.AddComponent<Building>();
        b.buildingName = "Machine Gun Defense";
        b.cost         = BuildingCost;

        SelectableBuilding sb = root.AddComponent<SelectableBuilding>();

        Health hp     = root.AddComponent<Health>();
        hp.team       = Health.Team.Player;
        hp.maxHealth  = MaxHealth;

        UnitCategory cat = root.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Building;

        PowerConsumer pc = root.AddComponent<PowerConsumer>();
        pc.demandAmount  = PowerDemand;

        BuildingTurretCombat combat = root.AddComponent<BuildingTurretCombat>();
        combat.attackRange         = AttackRange;
        combat.attackDamage        = AttackDamage;
        combat.attackCooldown      = AttackCooldown;
        combat.damageType          = DamageType.MachineGun;
        combat.scanInterval        = 0.2f;
        combat.turretTurnSpeed     = TurretTurnSpeed;
        combat.aimToleranceDegrees = AimTolerance;
        combat.tracerColor         = new Color(1f, 0.85f, 0.3f);
        combat.tracerDuration      = 0.04f;
        combat.tracerWidth         = 0.045f;
        combat.requirePower        = true;

        // Team color marker — paints the operator body + helmet with the player's
        // selected army color. Concrete base / metal platform / gun stay neutral.
        TeamColorMarker tcm = root.AddComponent<TeamColorMarker>();
        tcm.team = TeamColorMarker.Team.Player;
        tcm.bodyColorRenderers = new List<Renderer>();
        tcm.detailRenderers    = new List<Renderer>();
        tcm.ignoreRenderers    = new List<Renderer>();

        // --- Visual children ------------------------------------------- //

        // 1. Concrete bunker base (sandbag look via squat cube)
        GameObject baseGO = Spawn(root, "Base",
              PrimitiveType.Cube, concrete,
              new Vector3(0f, 0.40f, 0f),
              new Vector3(2.0f, 0.80f, 2.0f));
        tcm.detailRenderers.Add(baseGO.GetComponent<Renderer>());

        // 2. Metal platform on top of the base — slightly inset
        GameObject platform = Spawn(root, "Platform",
              PrimitiveType.Cube, metal,
              new Vector3(0f, 0.90f, 0f),
              new Vector3(1.6f, 0.20f, 1.6f));
        tcm.detailRenderers.Add(platform.GetComponent<Renderer>());

        // 3. Rotating turret pivot (empty Transform) — BuildingTurretCombat
        //    rotates this on Y to track the target.
        GameObject pivot = new GameObject("GunPivot");
        pivot.transform.SetParent(root.transform, false);
        pivot.transform.localPosition = new Vector3(0f, 1.10f, 0f);
        combat.turretPivot = pivot.transform;

        // 4. Gun receiver / housing — short dark cube on the pivot
        GameObject gunBody = Spawn(pivot, "GunBody",
              PrimitiveType.Cube, gun,
              new Vector3(0f, 0.15f, -0.05f),
              new Vector3(0.45f, 0.35f, 0.85f));
        tcm.detailRenderers.Add(gunBody.GetComponent<Renderer>());

        // 5. Barrel — thin dark cylinder pointing +Z (the pivot's forward).
        //    Default cylinder axis = +Y, so rotate 90° on X.
        GameObject barrelGO = Spawn(pivot, "Barrel",
              PrimitiveType.Cylinder, barrel,
              new Vector3(0f, 0.20f, 0.85f),
              new Vector3(0.12f, 0.55f, 0.12f));
        barrelGO.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        tcm.detailRenderers.Add(barrelGO.GetComponent<Renderer>());

        // 6. Side shields — thin slabs flanking the gun
        GameObject shieldL = Spawn(pivot, "ShieldL",
              PrimitiveType.Cube, metal,
              new Vector3(-0.35f, 0.20f, 0.10f),
              new Vector3(0.06f, 0.45f, 0.55f));
        shieldL.transform.localRotation = Quaternion.Euler(0f, -10f, 0f);
        tcm.detailRenderers.Add(shieldL.GetComponent<Renderer>());

        GameObject shieldR = Spawn(pivot, "ShieldR",
              PrimitiveType.Cube, metal,
              new Vector3(0.35f, 0.20f, 0.10f),
              new Vector3(0.06f, 0.45f, 0.55f));
        shieldR.transform.localRotation = Quaternion.Euler(0f, 10f, 0f);
        tcm.detailRenderers.Add(shieldR.GetComponent<Renderer>());

        // 7. FirePoint — empty Transform at the muzzle tip
        GameObject firePoint = new GameObject("FirePoint");
        firePoint.transform.SetParent(pivot.transform, false);
        firePoint.transform.localPosition = new Vector3(0f, 0.20f, 1.20f);
        combat.firePoint = firePoint.transform;

        // 8. Operator — small soldier silhouette parented to the pivot so he
        //    rotates with the gun. Body uses TeamColorMarker for army color.
        GameObject operatorGO = new GameObject("Operator");
        operatorGO.transform.SetParent(pivot.transform, false);
        operatorGO.transform.localPosition = new Vector3(0f, 0.00f, -0.65f);

        GameObject body = Spawn(operatorGO, "Body",
              PrimitiveType.Capsule, operatorMat,
              new Vector3(0f, 0.45f, 0f),
              new Vector3(0.32f, 0.40f, 0.32f));
        tcm.bodyColorRenderers.Add(body.GetComponent<Renderer>());

        GameObject head = Spawn(operatorGO, "Head",
              PrimitiveType.Sphere, helmet,
              new Vector3(0f, 0.95f, 0f),
              new Vector3(0.26f, 0.26f, 0.26f));
        tcm.bodyColorRenderers.Add(head.GetComponent<Renderer>());

        // 9. SelectionRing — flat cyan cylinder under the base
        GameObject circle = Spawn(root, "SelectionRing",
              PrimitiveType.Cylinder, ring,
              new Vector3(0f, 0.02f, 0f),
              new Vector3(2.6f, 0.02f, 2.6f));
        circle.SetActive(false);
        sb.selectionIndicator = circle;
        tcm.ignoreRenderers.Add(circle.GetComponent<Renderer>());

        // 10. HealthBar — child GameObject; HealthBar builds its own primitives
        //     at runtime.
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(root.transform, false);
        bar.transform.localPosition = Vector3.zero;
        HealthBar hb = bar.AddComponent<HealthBar>();
        hb.heightOffset = 2.4f;       // sit above the turret silhouette

        // --- Layer everything to "Building" ---------------------------- //
        SetLayerRecursive(root.transform, buildingLayer);
    }

    // ------------------------------------------------------------------ //
    // Primitive helpers
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
        // accept selection / scan hits.
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

        Material m    = AssetDatabase.LoadAssetAtPath<Material>(path);
        Shader target = ResolveShader();

        if (m == null)
        {
            m = new Material(target) { name = name };
            AssetDatabase.CreateAsset(m, path);
            Debug.Log($"[CreateMachineGunDefensePrefab]   Created material: {path}");
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
