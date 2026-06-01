// AirfieldSetup.cs  —  one-click Unity setup for the Meshy Airfield asset.
// Place the whole Airfield_Unity_Final folder anywhere under Assets/ (recommended:
// Assets/Buildings/Airfield/). Then in Unity:  Tools > Airfield > Setup Material & LOD Prefab.
//
// What it does (no mesh/design changes):
//   1. Sets correct import settings (Normal=NormalMap, MetallicSmoothness/Normal=linear, Albedo/Emission=sRGB).
//   2. Creates M_Airfield (URP Lit if the project uses URP, else Standard).
//   3. Wires Albedo, Normal, MetallicSmoothness (RGB=metallic, A=smoothness=1-roughness), Emission.
//   4. Remaps M_Airfield onto SM_Airfield_LOD0/1/2 FBX importers.
//   5. Builds Airfield.prefab with a configured LOD Group (LOD0/LOD1/LOD2).
//
// Safe to run multiple times (idempotent).

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class AirfieldSetup
{
    const string ALBEDO   = "Airfield_Albedo";
    const string NORMAL   = "Airfield_Normal";
    const string MAOS     = "Airfield_MetallicSmoothness";
    const string EMISSION = "Airfield_Emission";
    static readonly string[] LODS = { "SM_Airfield_LOD0", "SM_Airfield_LOD1", "SM_Airfield_LOD2" };

    [MenuItem("Tools/Airfield/Setup Material & LOD Prefab")]
    public static void Run()
    {
        string albedoPath = FindAsset(ALBEDO, "Texture2D");
        if (string.IsNullOrEmpty(albedoPath))
        {
            EditorUtility.DisplayDialog("Airfield Setup",
                "Could not find Airfield_Albedo.png. Make sure the Airfield_Unity_Final folder is inside Assets/.",
                "OK");
            return;
        }
        string folder = System.IO.Path.GetDirectoryName(albedoPath).Replace('\\', '/');

        // ---------- 1. Texture import settings ----------
        SetTexture(albedoPath,                 srgb: true,  normal: false);
        SetTexture(FindAsset(EMISSION, "Texture2D"), srgb: true,  normal: false);
        SetTexture(FindAsset(NORMAL,   "Texture2D"), srgb: false, normal: true);
        SetTexture(FindAsset(MAOS,     "Texture2D"), srgb: false, normal: false, keepAlpha: true);
        AssetDatabase.Refresh();

        var albedo   = Load<Texture2D>(albedoPath);
        var normal   = Load<Texture2D>(FindAsset(NORMAL, "Texture2D"));
        var maos     = Load<Texture2D>(FindAsset(MAOS, "Texture2D"));
        var emission = Load<Texture2D>(FindAsset(EMISSION, "Texture2D"));

        // ---------- 2/3. Material ----------
        bool urp = GraphicsSettings.currentRenderPipeline != null &&
                   GraphicsSettings.currentRenderPipeline.GetType().Name.Contains("Universal");
        Shader sh = Shader.Find(urp ? "Universal Render Pipeline/Lit" : "Standard");
        if (sh == null) sh = Shader.Find("Standard");

        string matPath = folder + "/M_Airfield.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null) { mat = new Material(sh); AssetDatabase.CreateAsset(mat, matPath); }
        else mat.shader = sh;

        if (urp)
        {
            mat.SetTexture("_BaseMap", albedo);
            mat.SetColor("_BaseColor", Color.white);
            if (normal) { mat.SetTexture("_BumpMap", normal); mat.EnableKeyword("_NORMALMAP"); mat.SetFloat("_BumpScale", 1f); }
            if (maos)   { mat.SetTexture("_MetallicGlossMap", maos); mat.EnableKeyword("_METALLICSPECGLOSSMAP"); }
            mat.SetFloat("_Metallic", 1f);
            mat.SetFloat("_Smoothness", 1f);
            mat.SetFloat("_SmoothnessTextureChannel", 0f); // 0 = metallic-map alpha
        }
        else
        {
            mat.SetTexture("_MainTex", albedo);
            mat.SetColor("_Color", Color.white);
            if (normal) { mat.SetTexture("_BumpMap", normal); mat.EnableKeyword("_NORMALMAP"); }
            if (maos)   { mat.SetTexture("_MetallicGlossMap", maos); mat.EnableKeyword("_METALLICGLOSSMAP"); }
            mat.SetFloat("_GlossMapScale", 1f);
            mat.SetFloat("_SmoothnessTextureChannel", 0f);
        }
        if (emission)
        {
            mat.SetTexture("_EmissionMap", emission);
            mat.SetColor("_EmissionColor", Color.white);
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();

        // ---------- 4. Remap material onto the FBX importers ----------
        foreach (var lod in LODS)
        {
            string fbx = FindAsset(lod, "Model");
            if (string.IsNullOrEmpty(fbx)) continue;
            var mi = AssetImporter.GetAtPath(fbx) as ModelImporter;
            if (mi == null) continue;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            // Blender exported the material as "M_Airfield"; remap that identifier to our asset.
            mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "M_Airfield"), mat);
            mi.SaveAndReimport();
        }

        // ---------- 5. LOD prefab ----------
        var root = new GameObject("Airfield");
        var group = root.AddComponent<LODGroup>();
        float[] cut = { 0.5f, 0.18f, 0.04f }; // LOD0->1, 1->2, 2->cull (screen-relative height)
        var lodArr = new LOD[3];
        bool haveBounds = false;
        Bounds footprint = new Bounds();
        for (int i = 0; i < LODS.Length; i++)
        {
            string fbx = FindAsset(LODS[i], "Model");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            inst.transform.SetParent(root.transform, false);
            inst.transform.localPosition = Vector3.zero;
            var rends = inst.GetComponentsInChildren<Renderer>();
            lodArr[i] = new LOD(cut[i], rends);

            // Mark the visual meshes Static (static building).
            foreach (var r in rends) GameObjectUtility.SetStaticEditorFlags(r.gameObject, (StaticEditorFlags)~0);
            GameObjectUtility.SetStaticEditorFlags(inst, (StaticEditorFlags)~0);

            if (i == 0) // use LOD0 (full detail) for the collider footprint
                foreach (var r in rends)
                {
                    if (!haveBounds) { footprint = r.bounds; haveBounds = true; }
                    else footprint.Encapsulate(r.bounds);
                }
        }
        group.SetLODs(lodArr);
        group.RecalculateBounds();
        GameObjectUtility.SetStaticEditorFlags(root, (StaticEditorFlags)~0);

        // ---------- Single BoxCollider footprint (NO MeshCollider) ----------
        var box = root.GetComponent<BoxCollider>();
        if (box == null) box = root.AddComponent<BoxCollider>();
        if (haveBounds)
        {
            // root sits at origin (identity), so world bounds == local.
            box.center = footprint.center;
            box.size = footprint.size;
        }

        string prefabPath = folder + "/Airfield.prefab";
        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        EditorGUIUtility.PingObject(Selection.activeObject);
        Debug.Log($"[AirfieldSetup] Done. Pipeline={(urp ? "URP Lit" : "Standard")}. " +
                  $"Material: {matPath}  Prefab: {prefabPath}  " +
                  $"(LOD Group + BoxCollider footprint added, meshes marked Static).");
    }

    static void SetTexture(string path, bool srgb, bool normal, bool keepAlpha = false)
    {
        if (string.IsNullOrEmpty(path)) return;
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) return;
        ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        if (!normal) ti.sRGBTexture = srgb;
        if (keepAlpha) { ti.alphaSource = TextureImporterAlphaSource.FromInput; ti.alphaIsTransparency = false; }
        ti.SaveAndReimport();
    }

    static string FindAsset(string name, string type)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var guids = AssetDatabase.FindAssets($"{name} t:{type}");
        foreach (var g in guids)
        {
            var p = AssetDatabase.GUIDToAssetPath(g);
            if (System.IO.Path.GetFileNameWithoutExtension(p) == name) return p;
        }
        return guids.Length > 0 ? AssetDatabase.GUIDToAssetPath(guids[0]) : null;
    }

    static T Load<T>(string path) where T : Object =>
        string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
}
