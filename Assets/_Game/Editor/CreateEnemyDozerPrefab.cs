using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// Creates EnemyDozerPrefab.prefab — the enemy mirror of the player DozerPrefab.
/// Same builder behaviour (drives <see cref="DozerBuilder"/> to navigate to a
/// <see cref="ConstructionSite"/> and contribute progress) but on the Enemy
/// team, with no <see cref="SelectableUnit"/> (the player can't click it),
/// no team-colour components, and a deep-red palette.
///
/// Menu: Tools → RTS → Enemy → Create Enemy Dozer Prefab
///
/// Resulting hierarchy:
///   EnemyDozerPrefab/                  (BoxCollider, NavMeshAgent, UnitMovement,
///     │                                 Health (Enemy, 140 HP), DozerBuilder,
///     │                                 UnitCategory.Vehicle)
///     ├── Body / Cabin / Blade        (red boxes — silhouette mirrors the player)
///     ├── BladeArm L / R              (grey arms)
///     ├── TrackL / TrackR             (black tracks)
///     ├── ExhaustStack                (grey cylinder)
///     └── HealthBar                   (HealthBar component — runtime visual)
///
/// Differences vs the player Dozer:
///   • Health.team = Enemy.
///   • Red palette.
///   • NO SelectableUnit (no player-side selection).
///   • NO TeamColorMarker / UnitColorMarker (enemy stays red).
///   • Adds UnitCategory.Vehicle so DamageRules scales incoming damage like
///     other vehicles.
///   • Build stats (move speed, agent radius, etc.) are unchanged.
///
/// Safe to re-run: overwrites the existing prefab in place.
/// </summary>
public static class CreateEnemyDozerPrefab
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    public  const string PrefabPath = "Assets/_Game/Prefabs/EnemyDozerPrefab.prefab";
    private const string MatFolder  = "Assets/_Game/Materials/EnemyDozer";
    private const string UnitLayer  = "Unit";

    private const float MaxHealth    = 140f;
    private const float MoveSpeed    = 4.5f;
    private const float StopDistance = 1f;
    private const float AgentRadius  = 1.0f;
    private const float AgentHeight  = 1.8f;

    private static readonly Color BodyRed     = new Color(0.55f, 0.15f, 0.15f);
    private static readonly Color CabinRed    = new Color(0.42f, 0.12f, 0.12f);
    private static readonly Color BladeRed    = new Color(0.65f, 0.18f, 0.15f);
    private static readonly Color ArmGrey     = new Color(0.30f, 0.30f, 0.32f);
    private static readonly Color TrackBlack  = new Color(0.08f, 0.08f, 0.08f);
    private static readonly Color ExhaustGrey = new Color(0.22f, 0.22f, 0.22f);

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Enemy/Create Enemy Dozer Prefab")]
    public static void Create()
    {
        int unitLayer = LayerMask.NameToLayer(UnitLayer);
        if (unitLayer < 0)
        {
            Debug.LogError($"[CreateEnemyDozerPrefab] ✗ Layer '{UnitLayer}' does not exist.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add 'Unit' user layer.");
            return;
        }

        Debug.Log("[CreateEnemyDozerPrefab] ── Building EnemyDozerPrefab ──");

        Material bodyMat    = LoadOrCreateMat("EnemyDozerBody",    BodyRed);
        Material cabinMat   = LoadOrCreateMat("EnemyDozerCabin",   CabinRed);
        Material bladeMat   = LoadOrCreateMat("EnemyDozerBlade",   BladeRed);
        Material armMat     = LoadOrCreateMat("EnemyDozerArm",     ArmGrey);
        Material trackMat   = LoadOrCreateMat("EnemyDozerTrack",   TrackBlack);
        Material exhaustMat = LoadOrCreateMat("EnemyDozerExhaust", ExhaustGrey);
        AssetDatabase.SaveAssets();

        GameObject root = new GameObject("EnemyDozerPrefab");
        try
        {
            root.layer = unitLayer;
            BuildDozer(root, unitLayer, bodyMat, cabinMat, bladeMat, armMat, trackMat, exhaustMat);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateEnemyDozerPrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Stats: HP={MaxHealth}, Team=Enemy, Builder, no SelectableUnit.\n" +
                      "  Place via Tools → RTS → Match → Setup Clean Match Map " +
                      "(it auto-spawns one next to the EnemyCommandCenter).");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // ------------------------------------------------------------------ //

    private static void BuildDozer(
        GameObject root, int unitLayer,
        Material body, Material cabin, Material blade,
        Material arm, Material track, Material exhaust)
    {
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
        hp.team          = Health.Team.Enemy;
        hp.maxHealth     = MaxHealth;

        UnitCategory cat = root.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Vehicle;

        // Builder component — same script as the player Dozer; team-neutral.
        root.AddComponent<DozerBuilder>();

        // --- Visual children (mirror the player layout) ----------------- //

        Spawn(root, "Body",
              PrimitiveType.Cube, body,
              new Vector3(0f, 0.55f, -0.05f),
              new Vector3(1.5f, 0.70f, 2.0f));

        Spawn(root, "Cabin",
              PrimitiveType.Cube, cabin,
              new Vector3(0f, 1.15f, -0.55f),
              new Vector3(1.2f, 0.55f, 0.9f));

        Spawn(root, "Blade",
              PrimitiveType.Cube, blade,
              new Vector3(0f, 0.45f, 1.20f),
              new Vector3(2.0f, 0.65f, 0.20f));

        Spawn(root, "BladeArmL",
              PrimitiveType.Cube, arm,
              new Vector3(-0.55f, 0.45f, 0.85f),
              new Vector3(0.12f, 0.18f, 0.80f));
        Spawn(root, "BladeArmR",
              PrimitiveType.Cube, arm,
              new Vector3( 0.55f, 0.45f, 0.85f),
              new Vector3(0.12f, 0.18f, 0.80f));

        Spawn(root, "TrackL",
              PrimitiveType.Cube, track,
              new Vector3(-0.85f, 0.25f, 0f),
              new Vector3(0.35f, 0.45f, 2.4f));
        Spawn(root, "TrackR",
              PrimitiveType.Cube, track,
              new Vector3( 0.85f, 0.25f, 0f),
              new Vector3(0.35f, 0.45f, 2.4f));

        Spawn(root, "ExhaustStack",
              PrimitiveType.Cylinder, exhaust,
              new Vector3(-0.45f, 1.65f, -0.65f),
              new Vector3(0.12f, 0.25f, 0.12f));

        // Enemy doesn't get a SelectionCircle — the player can't select it.

        // HealthBar — child GameObject; the component builds visuals at runtime.
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(root.transform, false);
        bar.AddComponent<HealthBar>();

        SetLayerRecursive(root.transform, unitLayer);
    }

    // ------------------------------------------------------------------ //
    // Helpers (same pattern as CreateDozerPrefab)
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
