using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// One-click editor tool that creates the HumveePrefab from scratch using
/// built-in Unity primitives. The resulting prefab is a fully wired RTS unit:
///
///   HumveePrefab/                       (BoxCollider, NavMeshAgent, UnitMovement,
///     │                                  SelectableUnit, Health (Player, 220 HP),
///     │                                  UnitCombat (range 10, dmg 8, cd 0.25))
///     ├── Body                          (dark green cube)
///     ├── Hood                          (small dark green cube — front)
///     ├── Cabin                         (smaller dark green cube — back)
///     ├── WheelFL / FR / RL / RR        (black cylinders, rotated 90° on Z)
///     ├── Turret                        (small olive cube on the roof)
///     ├── MachineGun                    (thin dark cylinder pointing +Z)
///     ├── FirePoint                     (empty Transform at the muzzle tip)
///     ├── SelectionCircle               (cyan flat cylinder under the hull)
///     └── HealthBar                     (HealthBar component, builds visuals at runtime)
///
/// Menu: Tools → RTS → Create Humvee Prefab
///
/// Safe to re-run: an existing HumveePrefab.prefab is overwritten in place,
/// so all references (VehicleFactoryProducer.humveePrefab) keep working.
/// </summary>
public static class CreateHumveePrefab
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    private const string PrefabPath  = "Assets/_Game/Prefabs/HumveePrefab.prefab";
    private const string MatFolder   = "Assets/_Game/Materials/Humvee";
    private const string UnitLayer   = "Unit";

    // Stats
    private const float MaxHealth      = 220f;
    private const float MoveSpeed      = 6f;
    private const float StopDistance   = 1f;
    private const float AgentRadius    = 1.0f;
    private const float AgentHeight    = 1.6f;
    private const float AttackRange    = 10f;
    private const float AttackDamage   = 8f;
    private const float AttackCooldown = 0.25f;

    // Palette
    private static readonly Color BodyGreen   = new Color(0.20f, 0.30f, 0.16f);
    private static readonly Color WheelBlack  = new Color(0.08f, 0.08f, 0.08f);
    private static readonly Color TurretOlive = new Color(0.28f, 0.36f, 0.22f);
    private static readonly Color GunDark     = new Color(0.18f, 0.18f, 0.18f);
    private static readonly Color RingCyan    = new Color(0.20f, 0.85f, 1.00f);

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Create Humvee Prefab")]
    public static void Create()
    {
        int unitLayer = LayerMask.NameToLayer(UnitLayer);
        if (unitLayer < 0)
        {
            Debug.LogError($"[CreateHumveePrefab] ✗ Layer '{UnitLayer}' does not exist.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add 'Unit' user layer.");
            return;
        }

        Debug.Log("[CreateHumveePrefab] ── Building HumveePrefab ──");

        // Materials persisted as assets so the prefab references survive Play→Stop.
        Material bodyMat   = LoadOrCreateMat("HumveeBody",   BodyGreen);
        Material wheelMat  = LoadOrCreateMat("HumveeWheel",  WheelBlack);
        Material turretMat = LoadOrCreateMat("HumveeTurret", TurretOlive);
        Material gunMat    = LoadOrCreateMat("HumveeGun",    GunDark);
        Material ringMat   = LoadOrCreateMat("HumveeRing",   RingCyan);
        AssetDatabase.SaveAssets();

        // ── Build the temp root in the scene then save as prefab ─────── //
        GameObject root = new GameObject("HumveePrefab");
        try
        {
            root.layer = unitLayer;
            BuildHumvee(root, unitLayer, bodyMat, wheelMat, turretMat, gunMat, ringMat);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateHumveePrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Stats: HP={MaxHealth}, Range={AttackRange}, Damage={AttackDamage}, Cooldown={AttackCooldown}.\n" +
                      "  Drop this prefab into VehicleFactory → VehicleFactoryProducer → Humvee Prefab.");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // ------------------------------------------------------------------ //
    // Humvee construction
    // ------------------------------------------------------------------ //

    private static void BuildHumvee(
        GameObject root, int unitLayer,
        Material body, Material wheel, Material turret, Material gun, Material ring)
    {
        // --- Gameplay components on the root --------------------------- //

        // BoxCollider sized to the body — the hit surface for selection / attack raycasts.
        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size   = new Vector3(1.6f, 1.0f, 2.6f);
        col.center = new Vector3(0f, 0.5f, 0f);

        // NavMeshAgent — gameplay movement
        NavMeshAgent agent     = root.AddComponent<NavMeshAgent>();
        agent.speed            = MoveSpeed;
        agent.angularSpeed     = 240f;
        agent.acceleration     = 16f;
        agent.stoppingDistance = StopDistance;
        agent.radius           = AgentRadius;
        agent.height           = AgentHeight;

        // Movement / selection / health (Health is required by UnitCombat)
        root.AddComponent<UnitMovement>();
        Health hp        = root.AddComponent<Health>();
        hp.team          = Health.Team.Player;
        hp.maxHealth     = MaxHealth;

        // SelectableUnit needs the circle child — assign it after creating
        // the visual subtree below.
        SelectableUnit sel = root.AddComponent<SelectableUnit>();

        // UnitCombat — ranged "machine gun" feel
        UnitCombat combat       = root.AddComponent<UnitCombat>();
        combat.attackRange      = AttackRange;
        combat.attackDamage     = AttackDamage;
        combat.attackCooldown   = AttackCooldown;
        combat.isRanged         = true;
        combat.rotationSpeed    = 240f;
        combat.tracerColor      = new Color(1f, 0.85f, 0.3f);
        combat.tracerDuration   = 0.05f;
        combat.tracerWidth      = 0.045f;

        // --- Visual children ------------------------------------------- //

        // Main hull — chunky dark green box. Y centre at 0.5 so the bottom
        // touches the agent's ground plane (NavMeshAgent treats y=0 as feet).
        Spawn(root, "Body",
              PrimitiveType.Cube, body,
              new Vector3(0f, 0.50f, 0f),
              new Vector3(1.6f, 0.55f, 2.6f));

        // Hood — slightly lower box at the front.
        Spawn(root, "Hood",
              PrimitiveType.Cube, body,
              new Vector3(0f, 0.40f, 1.05f),
              new Vector3(1.5f, 0.35f, 0.55f));

        // Cabin — taller, narrower box behind the hood for the driver/turret base.
        Spawn(root, "Cabin",
              PrimitiveType.Cube, body,
              new Vector3(0f, 0.95f, -0.10f),
              new Vector3(1.4f, 0.45f, 1.4f));

        // Wheels — cylinders rotated so their axis runs along X (left-right).
        SpawnWheel(root, "WheelFL", wheel, new Vector3(-0.90f, 0.30f,  0.85f));
        SpawnWheel(root, "WheelFR", wheel, new Vector3( 0.90f, 0.30f,  0.85f));
        SpawnWheel(root, "WheelRL", wheel, new Vector3(-0.90f, 0.30f, -0.85f));
        SpawnWheel(root, "WheelRR", wheel, new Vector3( 0.90f, 0.30f, -0.85f));

        // Roof turret — small olive cube on top of the cabin.
        Spawn(root, "Turret",
              PrimitiveType.Cube, turret,
              new Vector3(0f, 1.35f, -0.10f),
              new Vector3(0.55f, 0.30f, 0.55f));

        // Machine gun — thin dark cylinder pointing forward from the turret.
        // Default cylinder axis is +Y; rotate 90° on X so its long axis is +Z.
        GameObject mg = Spawn(root, "MachineGun",
              PrimitiveType.Cylinder, gun,
              new Vector3(0f, 1.40f, 0.55f),
              new Vector3(0.10f, 0.55f, 0.10f));
        mg.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        // FirePoint — empty Transform at the muzzle tip, used by UnitCombat
        // tracers (otherwise the tracer falls back to transform.position + up 1.2).
        GameObject firePoint = new GameObject("FirePoint");
        firePoint.transform.SetParent(root.transform, false);
        firePoint.transform.localPosition = new Vector3(0f, 1.40f, 1.20f);
        combat.firePoint = firePoint.transform;

        // SelectionCircle — flat cyan cylinder just above the ground plane.
        GameObject circle = Spawn(root, "SelectionCircle",
              PrimitiveType.Cylinder, ring,
              new Vector3(0f, 0.02f, 0f),
              new Vector3(2.4f, 0.02f, 2.4f));
        circle.SetActive(false); // SelectableUnit.Awake hides it; this is just belt-and-suspenders
        sel.selectionCircle = circle;

        // HealthBar — child GameObject; the HealthBar component builds its
        // own primitive cubes at runtime.
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(root.transform, false);
        bar.AddComponent<HealthBar>();

        // --- Layer everything to "Unit" -------------------------------- //
        SetLayerRecursive(root.transform, unitLayer);
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

        // Visual-only children: strip the auto-added collider so gameplay
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

    /// <summary>Wheel cylinder rotated so its disc faces left/right.</summary>
    private static void SpawnWheel(GameObject parent, string name, Material mat, Vector3 localPos)
    {
        GameObject w = Spawn(parent, name,
                             PrimitiveType.Cylinder, mat,
                             localPos,
                             new Vector3(0.35f, 0.18f, 0.35f));
        // Default cylinder axis = +Y. Rotate 90° on Z so the axis runs along X.
        w.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
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
            Debug.Log($"[CreateHumveePrefab]   Created material: {path}");
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
