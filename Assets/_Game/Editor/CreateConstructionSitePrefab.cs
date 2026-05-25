using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One-click editor tool that creates the ConstructionSitePrefab from scratch.
/// This placeholder is spawned by <see cref="BuildingPlacementManager"/> when a
/// Dozer is ordered to build a new structure, and is replaced by the final
/// building prefab when construction completes.
///
///   ConstructionSitePrefab/             (BoxCollider on Building layer,
///     │                                  ConstructionSite)
///     ├── Foundation                    (flat dark grey cube — site footprint)
///     ├── ProgressBarBackground         (small dark cube above foundation)
///     └── ProgressBarFill               (small green cube, X-scale animated 0→1)
///
/// Menu: Tools → RTS → Construction → Create Construction Site Prefab
///
/// Safe to re-run: an existing ConstructionSitePrefab.prefab is overwritten in
/// place, so BuildingPlacementManager's reference keeps working.
/// </summary>
public static class CreateConstructionSitePrefab
{
    private const string PrefabPath = "Assets/_Game/Prefabs/ConstructionSitePrefab.prefab";
    private const string MatFolder  = "Assets/_Game/Materials/Construction";
    private const string BuildingLayer = "Building";

    private static readonly Color FoundationGrey = new Color(0.32f, 0.32f, 0.32f);
    private static readonly Color BarBgGrey      = new Color(0.10f, 0.10f, 0.10f);
    private static readonly Color BarFillGreen   = new Color(0.30f, 0.85f, 0.30f);

    private const float FootprintX = 2.6f;
    private const float FootprintZ = 2.6f;
    private const float BarMaxX    = 1.6f;
    private const float BarHeight  = 1.8f;   // world-units above the foundation

    [MenuItem("Tools/RTS/Construction/Create Construction Site Prefab")]
    public static void Create()
    {
        int buildingLayer = LayerMask.NameToLayer(BuildingLayer);
        if (buildingLayer < 0)
        {
            Debug.LogError($"[CreateConstructionSitePrefab] ✗ Layer '{BuildingLayer}' does not exist.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add 'Building' user layer.");
            return;
        }

        Debug.Log("[CreateConstructionSitePrefab] ── Building ConstructionSitePrefab ──");

        Material foundationMat = LoadOrCreateMat("ConstructionFoundation", FoundationGrey);
        Material barBgMat      = LoadOrCreateMat("ConstructionBarBg",      BarBgGrey);
        Material barFillMat    = LoadOrCreateMat("ConstructionBarFill",    BarFillGreen);
        AssetDatabase.SaveAssets();

        GameObject root = new GameObject("ConstructionSitePrefab");
        try
        {
            root.layer = buildingLayer;

            // Footprint collider — matches BPM.footprintHalfExtents fairly closely
            // (2.6 x 1.0 x 2.6 → half-extents ~1.3,0.5,1.3). isTrigger=true so the
            // dozer's NavMeshAgent can drive onto it without getting wedged, but
            // overlap checks (Physics.CheckBox on the Building layer) still see
            // it, blocking double-placement, and raycasts still hit it for
            // right-click resume.
            BoxCollider col = root.AddComponent<BoxCollider>();
            col.size      = new Vector3(FootprintX, 1.0f, FootprintZ);
            col.center    = new Vector3(0f, 0.5f, 0f);
            col.isTrigger = true;

            // Foundation — flat grey slab.
            GameObject foundation = Spawn(root, "Foundation",
                PrimitiveType.Cube, foundationMat,
                new Vector3(0f, 0.05f, 0f),
                new Vector3(FootprintX, 0.10f, FootprintZ));

            // Progress bar — background + fill above the foundation.
            // The bar is parented to an empty 'ProgressBar' transform that
            // sits at (0, BarHeight, 0). The fill's pivot offset starts at
            // x=-BarMaxX/2 so it grows from the LEFT as the site advances.
            GameObject barAnchor = new GameObject("ProgressBar");
            barAnchor.transform.SetParent(root.transform, false);
            barAnchor.transform.localPosition = new Vector3(0f, BarHeight, 0f);

            GameObject barBg = Spawn(barAnchor, "ProgressBarBackground",
                PrimitiveType.Cube, barBgMat,
                Vector3.zero,
                new Vector3(BarMaxX, 0.12f, 0.10f));

            // Fill: starts at zero width — the ConstructionSite animates X
            // scale 0 → BarMaxX. We pre-position it at x = -BarMaxX/2 so the
            // LEFT edge is anchored when its X scale grows.
            GameObject barFill = Spawn(barAnchor, "ProgressBarFill",
                PrimitiveType.Cube, barFillMat,
                new Vector3(-BarMaxX * 0.5f, 0f, -0.06f),
                new Vector3(0f, 0.10f, 0.08f));

            ConstructionSite site = root.AddComponent<ConstructionSite>();
            site.foundationVisual    = foundation;
            site.progressBarFill     = barFill.transform;
            site.progressBarMaxScaleX = BarMaxX;

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateConstructionSitePrefab] ✓ Saved {PrefabPath}.\n" +
                      "  Drop into BuildingPlacementManager → constructionSitePrefab " +
                      "(or run Tools → RTS → Construction → Repair Construction System).");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
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
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows    = true;
        }
        return go;
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
            Debug.Log($"[CreateConstructionSitePrefab]   Created material: {path}");
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
