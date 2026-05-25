using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// Builds MissileLauncherPrefab.prefab from primitives — a truck chassis with
/// a rear-mounted missile rack. Mirrors the structure of the other vehicle
/// builders (Humvee / Artillery Tank) so adding it to the project is one
/// menu click.
///
/// Menu: Tools → RTS → Vehicles → Create Missile Launcher Prefab
///
/// Resulting hierarchy:
///   MissileLauncherPrefab/             (BoxCollider, NavMeshAgent, UnitMovement,
///     │                                 SelectableUnit, Health (Player, 380 HP),
///     │                                 MissileLauncherCombat, UnitCategory.Vehicle,
///     │                                 TeamColorMarker)
///     ├── Chassis                       (long dark truck bed, team color via TeamColorMarker)
///     ├── Cab                           (smaller box at the front — driver's cab)
///     ├── Wheel FL / FR / ML / MR / RL / RR  (six dark cylinders)
///     ├── LauncherPivot                 (empty Transform that pitches up to fire)
///     │     ├── LauncherBase            (mounting platform)
///     │     ├── LauncherRack            (angled box housing the tubes)
///     │     ├── Tube 1..4               (four white cylinders pointing forward)
///     │     └── FirePoint               (Transform at the rack's nose)
///     ├── SelectionCircle               (cyan flat cylinder under the hull)
///     └── HealthBar                     (HealthBar component — runtime visuals)
///
/// Safe to re-run — overwrites the prefab in place so any references in the
/// VehicleFactoryProducer keep working.
/// </summary>
public static class CreateMissileLauncherPrefab
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    public  const string PrefabPath = "Assets/_Game/Prefabs/MissileLauncherPrefab.prefab";
    private const string MatFolder  = "Assets/_Game/Materials/MissileLauncher";
    private const string UnitLayer  = "Unit";

    // Stats — match the spec
    private const float MaxHealth      = 380f;
    private const float MoveSpeed      = 3.0f;     // slower than Humvee, slightly slower than tank
    private const float StopDistance   = 1.2f;
    private const float AgentRadius    = 1.1f;
    private const float AgentHeight    = 2.0f;
    private const float AttackRange    = 30f;
    private const float MinRange       = 8f;
    private const float AttackDamage   = 90f;
    private const float AttackCooldown = 4.5f;
    private const float SplashRadius   = 3.5f;
    private const float BarHeight      = 2.6f;

    // Palette
    private static readonly Color ChassisOlive   = new Color(0.30f, 0.36f, 0.20f);  // body — repainted by TeamColorMarker
    private static readonly Color CabOlive       = new Color(0.36f, 0.42f, 0.24f);  // cab — repainted by TeamColorMarker
    private static readonly Color WheelBlack     = new Color(0.08f, 0.08f, 0.08f);
    private static readonly Color RackDark       = new Color(0.18f, 0.20f, 0.18f);
    private static readonly Color TubeWhite      = new Color(0.86f, 0.86f, 0.88f);
    private static readonly Color BaseGrey       = new Color(0.32f, 0.32f, 0.32f);
    private static readonly Color RingCyan       = new Color(0.20f, 0.85f, 1.00f);

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Vehicles/Create Missile Launcher Prefab")]
    public static void Create()
    {
        int unitLayer = LayerMask.NameToLayer(UnitLayer);
        if (unitLayer < 0)
        {
            Debug.LogError($"[CreateMissileLauncherPrefab] ✗ Layer '{UnitLayer}' does not exist.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add 'Unit' user layer.");
            return;
        }

        Debug.Log("[CreateMissileLauncherPrefab] ── Building MissileLauncherPrefab ──");

        Material chassisMat = LoadOrCreateMat("MLChassis",  ChassisOlive);
        Material cabMat     = LoadOrCreateMat("MLCab",      CabOlive);
        Material wheelMat   = LoadOrCreateMat("MLWheel",    WheelBlack);
        Material rackMat    = LoadOrCreateMat("MLRack",     RackDark);
        Material tubeMat    = LoadOrCreateMat("MLTube",     TubeWhite);
        Material baseMat    = LoadOrCreateMat("MLBase",     BaseGrey);
        Material ringMat    = LoadOrCreateMat("MLRing",     RingCyan);
        AssetDatabase.SaveAssets();

        GameObject root = new GameObject("MissileLauncherPrefab");
        try
        {
            root.layer = unitLayer;
            BuildLauncher(root, unitLayer,
                          chassisMat, cabMat, wheelMat, rackMat, tubeMat, baseMat, ringMat);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateMissileLauncherPrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Stats: HP={MaxHealth}, Range={AttackRange}, MinRange={MinRange}, " +
                      $"Damage={AttackDamage}, Splash={SplashRadius}, Cooldown={AttackCooldown}, " +
                      "Type=Artillery (no aircraft).\n" +
                      "  Drop into VehicleFactoryProducer → Missile Launcher Prefab.");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // ------------------------------------------------------------------ //
    // Construction
    // ------------------------------------------------------------------ //

    private static void BuildLauncher(
        GameObject root, int unitLayer,
        Material chassis, Material cab, Material wheel,
        Material rack, Material tube, Material baseMat, Material ring)
    {
        // ── Gameplay components on the root ────────────────────────────── //

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size   = new Vector3(2.0f, 1.2f, 3.6f);
        col.center = new Vector3(0f,  0.6f, 0f);

        NavMeshAgent agent     = root.AddComponent<NavMeshAgent>();
        agent.speed            = MoveSpeed;
        agent.angularSpeed     = 150f;
        agent.acceleration     = 10f;
        agent.stoppingDistance = StopDistance;
        agent.radius           = AgentRadius;
        agent.height           = AgentHeight;

        root.AddComponent<UnitMovement>();

        Health hp        = root.AddComponent<Health>();
        hp.team          = Health.Team.Player;
        hp.maxHealth     = MaxHealth;

        SelectableUnit sel = root.AddComponent<SelectableUnit>();

        UnitCategory cat = root.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Vehicle;

        MissileLauncherCombat mlc = root.AddComponent<MissileLauncherCombat>();
        mlc.attackRange        = AttackRange;
        mlc.minRange           = MinRange;
        mlc.attackDamage       = AttackDamage;
        mlc.attackCooldown     = AttackCooldown;
        mlc.splashRadius       = SplashRadius;
        mlc.damageType         = DamageType.Artillery;
        mlc.rotationSpeed      = 90f;
        mlc.missileTravelTime  = 1.4f;
        mlc.missileArcHeight   = 8f;
        mlc.impactFlashDuration = 0.35f;
        mlc.missileColor       = new Color(0.92f, 0.92f, 0.92f);
        mlc.missileSize        = new Vector3(0.30f, 0.30f, 1.40f);

        // Long-range guard behaviour matches RPG units — wide detection + leash.
        GroundAutoAttackController guard = root.AddComponent<GroundAutoAttackController>();
        guard.detectionRadius = AttackRange + 2f;     // see slightly outside the firing band
        guard.leashRadius     = AttackRange + 6f;     // give a small chase grace
        guard.scanInterval    = 0.5f;

        // Team color — repaint the body + cab with the player's selected color.
        TeamColorMarker tcm = root.AddComponent<TeamColorMarker>();
        tcm.team = TeamColorMarker.Team.Player;
        tcm.bodyColorRenderers = new List<Renderer>();
        tcm.detailRenderers    = new List<Renderer>();
        tcm.ignoreRenderers    = new List<Renderer>();

        // ── Visual children ────────────────────────────────────────────── //

        // Chassis — long, low bed (team-colored)
        GameObject chassisGO = Spawn(root, "Chassis",
              PrimitiveType.Cube, chassis,
              new Vector3(0f, 0.50f, 0f),
              new Vector3(1.8f, 0.40f, 3.4f));
        tcm.bodyColorRenderers.Add(chassisGO.GetComponent<Renderer>());

        // Driver cab — smaller box at the front (team-colored, contrast against rear rack)
        GameObject cabGO = Spawn(root, "Cab",
              PrimitiveType.Cube, cab,
              new Vector3(0f, 0.95f, 1.20f),
              new Vector3(1.5f, 0.60f, 1.00f));
        tcm.bodyColorRenderers.Add(cabGO.GetComponent<Renderer>());

        // Six wheels — three per side, evenly spaced along the chassis length.
        SpawnWheel(root, "WheelFL", wheel, new Vector3(-0.95f, 0.30f,  1.20f));
        SpawnWheel(root, "WheelFR", wheel, new Vector3( 0.95f, 0.30f,  1.20f));
        SpawnWheel(root, "WheelML", wheel, new Vector3(-0.95f, 0.30f,  0.00f));
        SpawnWheel(root, "WheelMR", wheel, new Vector3( 0.95f, 0.30f,  0.00f));
        SpawnWheel(root, "WheelRL", wheel, new Vector3(-0.95f, 0.30f, -1.20f));
        SpawnWheel(root, "WheelRR", wheel, new Vector3( 0.95f, 0.30f, -1.20f));

        // Launcher pivot — empty Transform on the REAR half of the chassis.
        // MissileLauncherCombat pitches this up by ~25° while firing.
        GameObject pivot = new GameObject("LauncherPivot");
        pivot.transform.SetParent(root.transform, false);
        pivot.transform.localPosition = new Vector3(0f, 0.95f, -0.80f);
        pivot.transform.localRotation = Quaternion.identity;
        mlc.launcherPivot         = pivot.transform;
        mlc.launcherRestPitch     = 0f;
        mlc.launcherFiringPitch   = -25f;
        mlc.launcherPitchSpeed    = 60f;

        // Mounting base on the chassis under the rack — neutral grey, not painted.
        GameObject baseBoxGO = Spawn(pivot, "LauncherBase",
              PrimitiveType.Cube, baseMat,
              new Vector3(0f, 0.10f, 0f),
              new Vector3(1.2f, 0.20f, 1.20f));
        tcm.detailRenderers.Add(baseBoxGO.GetComponent<Renderer>());

        // Launcher rack — angled box that houses the tubes.
        GameObject rackGO = Spawn(pivot, "LauncherRack",
              PrimitiveType.Cube, rack,
              new Vector3(0f, 0.45f, 0.10f),
              new Vector3(1.20f, 0.35f, 1.40f));
        tcm.detailRenderers.Add(rackGO.GetComponent<Renderer>());

        // Four missile tubes on top of the rack — pointing forward (+Z relative
        // to the pivot). Default cylinder axis is +Y, so we rotate 90° on X.
        // Layout: 2x2 grid on top of the rack.
        for (int row = 0; row < 2; row++)
        for (int colIdx = 0; colIdx < 2; colIdx++)
        {
            float x = (colIdx == 0 ? -0.30f : 0.30f);
            float y = 0.75f + (row == 0 ? 0f : 0.30f);
            GameObject tubeGO = Spawn(pivot, $"Tube_{row}{colIdx}",
                  PrimitiveType.Cylinder, tube,
                  new Vector3(x, y, 0.30f),
                  new Vector3(0.18f, 0.65f, 0.18f));
            tubeGO.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            tcm.detailRenderers.Add(tubeGO.GetComponent<Renderer>());
        }

        // FirePoint — Transform at the nose of the rack so missiles spawn from
        // a believable muzzle position (and inherit the pivot's pitch).
        GameObject firePoint = new GameObject("FirePoint");
        firePoint.transform.SetParent(pivot.transform, false);
        firePoint.transform.localPosition = new Vector3(0f, 0.95f, 1.00f);
        mlc.firePoint = firePoint.transform;

        // Selection circle just above the ground plane.
        GameObject circle = Spawn(root, "SelectionCircle",
              PrimitiveType.Cylinder, ring,
              new Vector3(0f, 0.02f, 0f),
              new Vector3(3.0f, 0.02f, 3.0f));
        circle.SetActive(false);
        sel.selectionCircle = circle;
        tcm.ignoreRenderers.Add(circle.GetComponent<Renderer>());

        // Health bar — raised so it clears the launcher silhouette.
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(root.transform, false);
        HealthBar hb     = bar.AddComponent<HealthBar>();
        hb.heightOffset  = BarHeight;

        // Final pass — everything on the Unit layer.
        SetLayerRecursive(root.transform, unitLayer);
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static void SpawnWheel(GameObject parent, string name, Material mat, Vector3 localPos)
    {
        GameObject w = Spawn(parent, name,
                             PrimitiveType.Cylinder, mat,
                             localPos,
                             new Vector3(0.40f, 0.20f, 0.40f));
        // Default cylinder axis = +Y. Rotate 90° on Z so the axis runs along X.
        w.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
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
    // Material factory
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
