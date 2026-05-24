using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// Builds ArtilleryTankPrefab.prefab from primitives. Larger, slower, and
/// harder-hitting than the Humvee, with DamageType = Cannon so the modifier
/// table makes it strong vs Vehicle/Building and weak vs Infantry.
///
/// Menu: Tools → RTS → Create Artillery Tank Prefab
///
/// Resulting hierarchy:
///   ArtilleryTankPrefab/         (BoxCollider, NavMeshAgent, UnitMovement,
///     │                            SelectableUnit, Health (Player, 420 HP),
///     │                            UnitCombat (range 16, dmg 80, cd 2.2,
///     │                                        type Cannon),
///     │                            UnitCategory.Vehicle)
///     ├── Body                   (dark green chassis cube)
///     ├── TrackL / TrackR        (dark side tread blocks)
///     ├── Turret                 (smaller green cube on the roof)
///     ├── Cannon                 (long thin dark cylinder pointing +Z)
///     ├── FirePoint              (empty Transform at the muzzle tip)
///     ├── SelectionCircle        (cyan flat cylinder under the hull)
///     └── HealthBar              (HealthBar component, height offset raised
///                                  to clear the turret silhouette)
///
/// Safe to re-run — overwrites in place so VehicleFactoryProducer keeps its
/// reference.
/// </summary>
public static class CreateArtilleryTankPrefab
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    private const string PrefabPath = "Assets/_Game/Prefabs/ArtilleryTankPrefab.prefab";
    private const string MatFolder  = "Assets/_Game/Materials/ArtilleryTank";
    private const string UnitLayer  = "Unit";

    // Stats — match the user spec
    private const float MaxHealth      = 420f;
    private const float MoveSpeed      = 3.5f;
    private const float StopDistance   = 1.5f;
    private const float AgentRadius    = 1.3f;
    private const float AgentHeight    = 2.0f;
    private const float AttackRange    = 16f;
    private const float AttackDamage   = 80f;
    private const float AttackCooldown = 2.2f;
    private const float BarHeight      = 2.8f; // HealthBar height above pivot

    // Palette — darker, more military
    private static readonly Color HullGreen   = new Color(0.16f, 0.24f, 0.13f);
    private static readonly Color TrackDark   = new Color(0.10f, 0.10f, 0.10f);
    private static readonly Color TurretGreen = new Color(0.20f, 0.30f, 0.16f);
    private static readonly Color CannonDark  = new Color(0.14f, 0.14f, 0.14f);
    private static readonly Color RingCyan    = new Color(0.20f, 0.85f, 1.00f);

    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Create Artillery Tank Prefab")]
    public static void Create()
    {
        int unitLayer = LayerMask.NameToLayer(UnitLayer);
        if (unitLayer < 0)
        {
            Debug.LogError($"[CreateArtilleryTankPrefab] ✗ Layer '{UnitLayer}' does not exist.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add 'Unit'.");
            return;
        }

        Debug.Log("[CreateArtilleryTankPrefab] ── Building ArtilleryTankPrefab ──");

        Material hullMat   = LoadOrCreateMat("TankHull",   HullGreen);
        Material trackMat  = LoadOrCreateMat("TankTrack",  TrackDark);
        Material turretMat = LoadOrCreateMat("TankTurret", TurretGreen);
        Material cannonMat = LoadOrCreateMat("TankCannon", CannonDark);
        Material ringMat   = LoadOrCreateMat("TankRing",   RingCyan);
        AssetDatabase.SaveAssets();

        GameObject root = new GameObject("ArtilleryTankPrefab");
        try
        {
            root.layer = unitLayer;
            BuildTank(root, unitLayer, hullMat, trackMat, turretMat, cannonMat, ringMat);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateArtilleryTankPrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Stats: HP={MaxHealth}, Range={AttackRange}, Damage={AttackDamage}, " +
                      $"Cooldown={AttackCooldown}, DamageType=Cannon.\n" +
                      "  Drop into VehicleFactoryProducer → Artillery Tank Prefab.");
        }
        finally { Object.DestroyImmediate(root); }
    }

    // ------------------------------------------------------------------ //
    // Construction
    // ------------------------------------------------------------------ //

    private static void BuildTank(
        GameObject root, int unitLayer,
        Material hull, Material track, Material turret, Material cannon, Material ring)
    {
        // ── Gameplay components on the root ──────────────────────────── //

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size   = new Vector3(2.6f, 1.2f, 3.6f);
        col.center = new Vector3(0f, 0.6f, 0f);

        NavMeshAgent agent     = root.AddComponent<NavMeshAgent>();
        agent.speed            = MoveSpeed;
        agent.angularSpeed     = 180f;
        agent.acceleration     = 10f;
        agent.stoppingDistance = StopDistance;
        agent.radius           = AgentRadius;
        agent.height           = AgentHeight;

        root.AddComponent<UnitMovement>();

        Health hp        = root.AddComponent<Health>();
        hp.team          = Health.Team.Player;
        hp.maxHealth     = MaxHealth;

        SelectableUnit sel = root.AddComponent<SelectableUnit>();

        UnitCombat combat      = root.AddComponent<UnitCombat>();
        combat.attackRange     = AttackRange;
        combat.attackDamage    = AttackDamage;
        combat.attackCooldown  = AttackCooldown;
        combat.isRanged        = true;
        combat.rotationSpeed   = 120f;                  // tank turns slowly
        combat.damageType      = DamageType.Cannon;
        // Beefier tracer to read as a cannon shot rather than a rifle round.
        combat.tracerColor     = new Color(1f, 0.55f, 0.10f);
        combat.tracerDuration  = 0.12f;
        combat.tracerWidth     = 0.10f;

        // Stamp the target category so it benefits from Cannon damage rules
        // and takes reduced Bullet damage from other units.
        UnitCategory cat = root.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Vehicle;

        // ── Visual children ──────────────────────────────────────────── //

        // Main hull
        Spawn(root, "Body",
              PrimitiveType.Cube, hull,
              new Vector3(0f, 0.65f, 0f),
              new Vector3(2.2f, 0.70f, 3.2f));

        // Tracks — long dark blocks running along the sides
        Spawn(root, "TrackL",
              PrimitiveType.Cube, track,
              new Vector3(-1.20f, 0.30f, 0f),
              new Vector3(0.40f, 0.55f, 3.6f));
        Spawn(root, "TrackR",
              PrimitiveType.Cube, track,
              new Vector3( 1.20f, 0.30f, 0f),
              new Vector3(0.40f, 0.55f, 3.6f));

        // Turret on top of the hull
        Spawn(root, "Turret",
              PrimitiveType.Cube, turret,
              new Vector3(0f, 1.35f, -0.20f),
              new Vector3(1.60f, 0.65f, 1.60f));

        // Cannon barrel pointing +Z. Cylinder default axis is Y; rotate so the
        // long axis is +Z and place it forward of the turret.
        GameObject barrel = Spawn(root, "Cannon",
              PrimitiveType.Cylinder, cannon,
              new Vector3(0f, 1.45f, 1.20f),
              new Vector3(0.18f, 1.25f, 0.18f));
        barrel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        // FirePoint at the muzzle tip — UnitCombat tracer originates here.
        GameObject firePoint = new GameObject("FirePoint");
        firePoint.transform.SetParent(root.transform, false);
        firePoint.transform.localPosition = new Vector3(0f, 1.45f, 2.50f);
        combat.firePoint = firePoint.transform;

        // Selection circle just above the ground plane
        GameObject circle = Spawn(root, "SelectionCircle",
              PrimitiveType.Cylinder, ring,
              new Vector3(0f, 0.02f, 0f),
              new Vector3(3.2f, 0.02f, 3.2f));
        circle.SetActive(false); // SelectableUnit.Awake hides it; matches that
        sel.selectionCircle = circle;

        // Health bar — raise the offset so it clears the turret silhouette.
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(root.transform, false);
        HealthBar hb     = bar.AddComponent<HealthBar>();
        hb.heightOffset  = BarHeight;

        // Final pass — everything on the Unit layer
        SetLayerRecursive(root.transform, unitLayer);
    }

    // ------------------------------------------------------------------ //
    // Helpers (shared shape with other prefab builders)
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
