using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Prepares SoldierPrefab for a future imported low-poly soldier model while
/// leaving a clean, properly-coloured primitive placeholder in the meantime.
///
/// Menu: Tools → RTS → Upgrade Soldier Visual
///
/// What it does (idempotent — safe to re-run):
///   • Disables the root MeshRenderer (the old plain capsule visual).
///   • Removes any previous SoldierVisual / SoldierVisualRoot children.
///   • Creates/refreshes five material assets under Assets/_Game/Materials/Soldier/
///     so renders are NEVER magenta. Shader is picked from the active pipeline.
///   • Builds:
///       SoldierPrefab/
///         ├── SelectionCircle              (untouched)
///         └── SoldierVisualRoot            (container; has UnitVisualModelSlot)
///             ├── PrimitivePlaceholder     (the recoloured low-poly soldier)
///             │     Body, Head, Helmet, LeftArm, RightArm,
///             │     LeftLeg, RightLeg, Backpack, RiflePlaceholder
///             └── ImportedModelGoesHere    (empty Transform — drop your model here)
///
/// What it does NOT touch:
///   • Root gameplay components: NavMeshAgent, UnitMovement, SelectableUnit,
///     Health, UnitCombat, CapsuleCollider.
///   • The prefab's GUID — every existing reference (UnitProducer.soldierPrefab,
///     etc.) keeps working.
/// </summary>
public static class UpgradeSoldierVisual
{
    // ------------------------------------------------------------------ //
    // Paths & palette
    // ------------------------------------------------------------------ //

    private const string MatFolder = "Assets/_Game/Materials/Soldier";

    private static readonly Color MilitaryGreenColor  = new Color(0.30f, 0.36f, 0.20f); // olive uniform
    private static readonly Color DarkHelmetColor     = new Color(0.18f, 0.24f, 0.13f); // dark green
    private static readonly Color SkinToneColor       = new Color(0.85f, 0.70f, 0.55f); // neutral tan
    private static readonly Color DarkBootsColor      = new Color(0.15f, 0.13f, 0.10f); // dark boots
    private static readonly Color WeaponDarkGreyColor = new Color(0.20f, 0.20f, 0.20f); // gear / rifle

    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Upgrade Soldier Visual")]
    public static void Upgrade()
    {
        string prefabPath = AssetDatabase
            .FindAssets("SoldierPrefab t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith("/SoldierPrefab.prefab"));

        if (string.IsNullOrEmpty(prefabPath))
        {
            Debug.LogError("[UpgradeSoldierVisual] ✗ SoldierPrefab.prefab not found.");
            return;
        }

        Debug.Log($"[UpgradeSoldierVisual] ── Upgrading {prefabPath} ──");

        // 1. Persistent material assets — created once, reused on subsequent runs
        Material militaryGreen  = LoadOrCreateMat("MilitaryGreen",  MilitaryGreenColor);
        Material darkHelmet     = LoadOrCreateMat("DarkHelmet",     DarkHelmetColor);
        Material skinTone       = LoadOrCreateMat("SkinTone",       SkinToneColor);
        Material darkBoots      = LoadOrCreateMat("DarkBoots",      DarkBootsColor);
        Material weaponDarkGrey = LoadOrCreateMat("WeaponDarkGrey", WeaponDarkGreyColor);
        AssetDatabase.SaveAssets();

        // 2. Edit the prefab
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            // Disable the old purple capsule renderer
            MeshRenderer rootRenderer = root.GetComponent<MeshRenderer>();
            if (rootRenderer != null && rootRenderer.enabled)
            {
                rootRenderer.enabled = false;
                Debug.Log("[UpgradeSoldierVisual]   Root MeshRenderer disabled.");
            }

            // Remove any previous visual subtree (idempotent re-run)
            RemoveChild(root.transform, "SoldierVisual");      // older name
            RemoveChild(root.transform, "SoldierVisualRoot");  // current name

            // Build the new hierarchy
            GameObject visualRoot = new GameObject("SoldierVisualRoot");
            visualRoot.transform.SetParent(root.transform, false);

            UnitVisualModelSlot slot = visualRoot.AddComponent<UnitVisualModelSlot>();

            GameObject placeholder = new GameObject("PrimitivePlaceholder");
            placeholder.transform.SetParent(visualRoot.transform, false);

            BuildPrimitiveSoldier(placeholder.transform,
                militaryGreen, darkHelmet, skinTone, darkBoots, weaponDarkGrey);

            GameObject importSlot = new GameObject("ImportedModelGoesHere");
            importSlot.transform.SetParent(visualRoot.transform, false);
            slot.model = importSlot.transform;

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Debug.Log("[UpgradeSoldierVisual] ✓ SoldierPrefab updated. " +
                      "Existing references and gameplay components are preserved.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ================================================================== //
    // Primitive soldier — same layout as before, now using saved .mat assets
    // ================================================================== //

    private static void BuildPrimitiveSoldier(Transform parent,
        Material uniform, Material helmet, Material skin, Material boots, Material gear)
    {
        // All positions are inside the root capsule's local Y range (-1 → +1).
        Spawn(parent, "Body",            PrimitiveType.Cube,    uniform,
              new Vector3(0f,   0.15f,  0f),    new Vector3(0.60f, 0.70f, 0.35f));

        Spawn(parent, "Head",            PrimitiveType.Sphere,  skin,
              new Vector3(0f,   0.70f,  0f),    new Vector3(0.32f, 0.32f, 0.32f));

        Spawn(parent, "Helmet",          PrimitiveType.Sphere,  helmet,
              new Vector3(0f,   0.78f,  0f),    new Vector3(0.42f, 0.22f, 0.42f));

        Spawn(parent, "LeftArm",         PrimitiveType.Capsule, uniform,
              new Vector3(-0.42f, 0.10f, 0f),   new Vector3(0.18f, 0.40f, 0.18f));

        Spawn(parent, "RightArm",        PrimitiveType.Capsule, uniform,
              new Vector3( 0.42f, 0.10f, 0f),   new Vector3(0.18f, 0.40f, 0.18f));

        Spawn(parent, "LeftLeg",         PrimitiveType.Capsule, boots,
              new Vector3(-0.18f, -0.60f, 0f),  new Vector3(0.20f, 0.40f, 0.20f));

        Spawn(parent, "RightLeg",        PrimitiveType.Capsule, boots,
              new Vector3( 0.18f, -0.60f, 0f),  new Vector3(0.20f, 0.40f, 0.20f));

        Spawn(parent, "Backpack",        PrimitiveType.Cube,    gear,
              new Vector3(0f,   0.10f, -0.25f), new Vector3(0.40f, 0.50f, 0.18f));

        Spawn(parent, "RiflePlaceholder", PrimitiveType.Cube,   gear,
              new Vector3(0.35f, 0.10f, 0.25f), new Vector3(0.08f, 0.08f, 0.60f));
    }

    private static GameObject Spawn(Transform parent, string name, PrimitiveType type,
                                    Material mat, Vector3 localPos, Vector3 localScale)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale    = localScale;

        // Strip the auto-created collider — gameplay clicks only hit the root capsule
        Collider col = go.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col);

        Renderer r = go.GetComponent<Renderer>();
        if (r != null) r.sharedMaterial = mat;
        return go;
    }

    private static void RemoveChild(Transform parent, string name)
    {
        Transform t = parent.Find(name);
        if (t != null) Object.DestroyImmediate(t.gameObject);
    }

    // ================================================================== //
    // Material asset factory — persistent .mat assets, render-pipeline aware
    // ================================================================== //

    private static Material LoadOrCreateMat(string name, Color color)
    {
        EnsureFolder(MatFolder);
        string path = $"{MatFolder}/{name}.mat";

        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        Shader   target = ResolveShader();

        if (m == null)
        {
            m = new Material(target) { name = name };
            AssetDatabase.CreateAsset(m, path);
            Debug.Log($"[UpgradeSoldierVisual]   Created material asset: {path}");
        }
        else if (m.shader == null || m.shader.name != target.name)
        {
            // Heal materials whose shader can't be resolved in this project
            m.shader = target;
            Debug.Log($"[UpgradeSoldierVisual]   Repaired shader on: {path}");
        }

        // Set colour on both Standard (_Color) and URP (_BaseColor) property names
        m.color = color;
        if (m.HasProperty("_BaseColor"))
            m.SetColor("_BaseColor", color);

        EditorUtility.SetDirty(m);
        return m;
    }

    /// <summary>
    /// Picks the right Lit shader for the active render pipeline. URP is detected
    /// via GraphicsSettings; falls back to Standard for the Built-in pipeline, and
    /// finally to whatever Lit shader Unity can find as a last resort.
    /// </summary>
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
