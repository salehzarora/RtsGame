using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// One-click editor tool that creates the DozerPrefab from scratch using
/// built-in Unity primitives. The Dozer is an unarmed construction vehicle
/// that builds <see cref="ConstructionSite"/> placeholders into final buildings.
///
///   DozerPrefab/                       (BoxCollider, NavMeshAgent, UnitMovement,
///     │                                 SelectableUnit, Health (Player, 140 HP),
///     │                                 DozerBuilder)
///     ├── Body                          (yellow cube)
///     ├── Cabin                         (smaller yellow cube — back)
///     ├── Blade                         (wide yellow slab — front)
///     ├── BladeArm L / R                (small grey cubes — blade pistons)
///     ├── TrackL / TrackR               (black box "tracks")
///     ├── ExhaustStack                  (small grey cylinder on cabin)
///     ├── SelectionCircle               (cyan flat cylinder under the hull)
///     └── HealthBar                     (HealthBar component, builds visuals at runtime)
///
/// Menu: Tools → RTS → Units → Create Dozer Prefab
///
/// Safe to re-run: an existing DozerPrefab.prefab is overwritten in place,
/// so all references (CommandCenterProducer.dozerPrefab) keep working.
/// </summary>
public static class CreateDozerPrefab
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    private const string PrefabPath = "Assets/_Game/Prefabs/DozerPrefab.prefab";
    private const string MatFolder  = "Assets/_Game/Materials/Dozer";
    private const string UnitLayer  = "Unit";

    private const float MaxHealth    = 140f;
    private const float MoveSpeed    = 4.5f;
    private const float StopDistance = 1f;
    private const float AgentRadius  = 1.0f;
    private const float AgentHeight  = 1.8f;

    private static readonly Color BodyYellow   = new Color(0.92f, 0.72f, 0.12f);
    private static readonly Color CabinYellow  = new Color(0.78f, 0.60f, 0.10f);
    private static readonly Color BladeYellow  = new Color(0.95f, 0.78f, 0.18f);
    private static readonly Color ArmGrey      = new Color(0.30f, 0.30f, 0.32f);
    private static readonly Color TrackBlack   = new Color(0.08f, 0.08f, 0.08f);
    private static readonly Color ExhaustGrey  = new Color(0.22f, 0.22f, 0.22f);
    private static readonly Color RingCyan     = new Color(0.20f, 0.85f, 1.00f);

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Units/Create Dozer Prefab")]
    public static void Create()
    {
        int unitLayer = LayerMask.NameToLayer(UnitLayer);
        if (unitLayer < 0)
        {
            Debug.LogError($"[CreateDozerPrefab] ✗ Layer '{UnitLayer}' does not exist.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add 'Unit' user layer.");
            return;
        }

        Debug.Log("[CreateDozerPrefab] ── Building DozerPrefab ──");

        Material bodyMat    = LoadOrCreateMat("DozerBody",    BodyYellow);
        Material cabinMat   = LoadOrCreateMat("DozerCabin",   CabinYellow);
        Material bladeMat   = LoadOrCreateMat("DozerBlade",   BladeYellow);
        Material armMat     = LoadOrCreateMat("DozerArm",     ArmGrey);
        Material trackMat   = LoadOrCreateMat("DozerTrack",   TrackBlack);
        Material exhaustMat = LoadOrCreateMat("DozerExhaust", ExhaustGrey);
        Material ringMat    = LoadOrCreateMat("DozerRing",    RingCyan);
        AssetDatabase.SaveAssets();

        GameObject root = new GameObject("DozerPrefab");
        try
        {
            root.layer = unitLayer;
            BuildDozer(root, unitLayer, bodyMat, cabinMat, bladeMat, armMat, trackMat, exhaustMat, ringMat);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateDozerPrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Stats: HP={MaxHealth}, MoveSpeed={MoveSpeed}, Builder (no combat).\n" +
                      "  Drop into CommandCenter → CommandCenterProducer → Dozer Prefab, " +
                      "or run Tools → RTS → Construction → Repair Construction System.");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // ------------------------------------------------------------------ //
    // Construction
    // ------------------------------------------------------------------ //

    private static void BuildDozer(
        GameObject root, int unitLayer,
        Material body, Material cabin, Material blade,
        Material arm, Material track, Material exhaust, Material ring)
    {
        // Root colliders / movement / health / selection.
        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size   = new Vector3(1.8f, 1.4f, 2.8f);
        col.center = new Vector3(0f, 0.7f, 0f);

        NavMeshAgent agent     = root.AddComponent<NavMeshAgent>();
        agent.speed            = MoveSpeed;
        agent.angularSpeed     = 200f;
        agent.acceleration     = 12f;
        agent.stoppingDistance = StopDistance;
        agent.radius           = AgentRadius;
        agent.height           = AgentHeight;

        root.AddComponent<UnitMovement>();

        Health hp        = root.AddComponent<Health>();
        hp.team          = Health.Team.Player;
        hp.maxHealth     = MaxHealth;

        SelectableUnit sel = root.AddComponent<SelectableUnit>();

        // Builder component — drives dozer-to-site navigation + progress.
        root.AddComponent<DozerBuilder>();

        // --- Visual children ------------------------------------------- //

        // Main hull — chunky yellow box.
        Spawn(root, "Body",
              PrimitiveType.Cube, body,
              new Vector3(0f, 0.55f, -0.05f),
              new Vector3(1.5f, 0.70f, 2.0f));

        // Cabin — taller box behind the hood, where the driver would sit.
        Spawn(root, "Cabin",
              PrimitiveType.Cube, cabin,
              new Vector3(0f, 1.15f, -0.55f),
              new Vector3(1.2f, 0.55f, 0.9f));

        // Front blade — wide flat slab in front of the body.
        Spawn(root, "Blade",
              PrimitiveType.Cube, blade,
              new Vector3(0f, 0.45f, 1.20f),
              new Vector3(2.0f, 0.65f, 0.20f));

        // Blade arms — two grey cubes connecting blade to hull.
        Spawn(root, "BladeArmL",
              PrimitiveType.Cube, arm,
              new Vector3(-0.55f, 0.45f, 0.85f),
              new Vector3(0.12f, 0.18f, 0.80f));
        Spawn(root, "BladeArmR",
              PrimitiveType.Cube, arm,
              new Vector3( 0.55f, 0.45f, 0.85f),
              new Vector3(0.12f, 0.18f, 0.80f));

        // Tracks — long black boxes alongside the body.
        Spawn(root, "TrackL",
              PrimitiveType.Cube, track,
              new Vector3(-0.85f, 0.25f, 0f),
              new Vector3(0.35f, 0.45f, 2.4f));
        Spawn(root, "TrackR",
              PrimitiveType.Cube, track,
              new Vector3( 0.85f, 0.25f, 0f),
              new Vector3(0.35f, 0.45f, 2.4f));

        // Exhaust — small grey cylinder.
        GameObject stack = Spawn(root, "ExhaustStack",
              PrimitiveType.Cylinder, exhaust,
              new Vector3(-0.45f, 1.65f, -0.65f),
              new Vector3(0.12f, 0.25f, 0.12f));
        // (Cylinders' default Y-up axis is what we want here.)
        _ = stack;

        // SelectionCircle — flat cyan cylinder just above the ground plane.
        GameObject circle = Spawn(root, "SelectionCircle",
              PrimitiveType.Cylinder, ring,
              new Vector3(0f, 0.02f, 0f),
              new Vector3(2.5f, 0.02f, 2.5f));
        circle.SetActive(false);
        sel.selectionCircle = circle;

        // HealthBar — child GameObject; the component builds its own visuals.
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(root.transform, false);
        bar.AddComponent<HealthBar>();

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
            Debug.Log($"[CreateDozerPrefab]   Created material: {path}");
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
