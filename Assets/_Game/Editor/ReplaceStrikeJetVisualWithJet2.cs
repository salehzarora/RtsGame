using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One-click visual swap for the existing StrikeJetPrefab — replaces the
/// old cube-built jet meshes (Fuselage, Nose, WingL/R, Tail, WingStripeL/R,
/// PodL/R, etc.) with the new Meshy "Azure Vortex" model in
/// Assets/_Game/Art/Aircraft/jet2 while leaving every gameplay component
/// on the prefab root untouched (AirUnitController, AircraftWeapon,
/// AircraftAmmoIndicator, SelectableAircraft, UnitCategory, Health,
/// GameEntity, TeamColorMarker, BoxCollider, FirePoint).
///
/// HOW IT WORKS, IN ORDER
///   1. Verifies all three LOD FBXes + four textures exist.
///   2. Configures the texture importers (BaseColor sRGB on, Normal as
///      Normal map, Metallic / Roughness sRGB off).
///   3. Configures each LOD FBX importer (scale factor 1.263 so the jet
///      ends up ~2.4 m long — matches the existing BoxCollider depth;
///      addColliders OFF).
///   4. Creates four materials in Assets/_Game/Materials/Aircraft/Jet2/:
///        M_Jet_MainBody, M_Jet_DarkPanels, M_Jet_Canopy, M_TeamColor.
///      Per the asset's README, the fourth material has no base texture
///      and is the per-team tinted slot.
///   5. Remaps each LOD FBX's four material slots by name to those new
///      assets so editing the .mat updates the rendered material.
///   6. Duplicates StrikeJetPrefab.prefab to
///      StrikeJetPrefab_OLD_Backup.prefab (idempotent — overwrite each run).
///   7. Opens the live prefab and:
///        • Reparents the old visual children (Fuselage, Nose, WingL/R,
///          Tail, WingStripeL/R, TeamColorAccent_DISABLED, PodL/R) into
///          an inactive OldVisual_Backup group. NOT deleted.
///        • Creates a new Visual child with a LODGroup and three LOD
///          instances of the new FBX prefabs.
///        • Strips every Collider / Light / Camera / Animator from inside
///          the new visual subtree (defensive — same protection the
///          airfield FBX swap uses).
///        • Creates a Markers group with empty GameObjects for
///          NosePoint, Hardpoint_Left / _Right, EngineFX_Left / _Right.
///        • Re-wires team colour: clears the root TeamColorMarker's
///          renderer lists (its fileIDs point at the now-inactive old
///          children) and adds a TeamColorApplier that paints only the
///          M_TeamColor slot (slot index 3) on the new visual's renderers.
///          TeamColorApplier is the right tool because TeamColorMarker
///          paints _BaseColor on the renderer (all material slots), but
///          we only want the team color on slot 3.
///   8. Saves.
///
/// WHAT IT DOES NOT TOUCH
///   • Prefab root components (AirUnitController, AircraftWeapon, Health,
///     Selectable, GameEntity, BoxCollider).
///   • FirePoint, HealthBar, SelectionCircle, AmmoIndicator children.
///   • Root transform, prefab name, prefab path.
///   • The aircraft scripts, networking, or any logic — purely a visual
///     swap and the team-color slot wiring.
///
/// Re-running is safe: backup is re-copied, OldVisual_Backup is reused if
/// present, the Visual / Markers groups are rebuilt fresh.
///
/// Menu: Tools → RTS → Aircraft → Replace StrikeJet Visual With Jet2
/// </summary>
public static class ReplaceStrikeJetVisualWithJet2
{
    // Asset paths
    private const string PrefabPath       = "Assets/_Game/Prefabs/StrikeJetPrefab.prefab";
    private const string BackupPath       = "Assets/_Game/Prefabs/StrikeJetPrefab_OLD_Backup.prefab";
    private const string AssetFolder      = "Assets/_Game/Art/Aircraft/jet2";
    private const string TextureFolder    = "Assets/_Game/Art/Aircraft/jet2/Textures";
    private const string MaterialFolder   = "Assets/_Game/Materials/Aircraft/Jet2";

    // Names baked into the FBX material slots — used for remap.
    // (See README: "Slot 0 = M_Jet_MainBody, Slot 1 = M_Jet_DarkPanels, …")
    private const string MatNameMainBody   = "M_Jet_MainBody";
    private const string MatNameDarkPanels = "M_Jet_DarkPanels";
    private const string MatNameCanopy     = "M_Jet_Canopy";
    private const string MatNameTeamColor  = "M_TeamColor";

    // The slot index we paint with team color on the imported renderers.
    private const int TeamColorMaterialSlot = 3;

    // Length math: the prefab BoxCollider is 2.4 m deep on Z. Asset README
    // says the native FBX length is 1.9 units. Scale factor = 2.4 / 1.9.
    private const float TargetScaleFactor = 1.263f;

    /// <summary>
    /// Y rotation offset applied to the <c>Visual</c> child so the jet's
    /// mesh-local forward axis lines up with the prefab root's +Z (the
    /// gameplay forward direction). Empirically derived from the user's
    /// captured world rotations:
    ///   parked-jet rot Y = 304.041199°  (the gameplay root rotation)
    ///   desired visual rot Y = 207.341324°  (the rotation that makes the
    ///                                       nose face the right way)
    /// Offset = 207.341 - 304.041 = -96.7° (round). The Visual child's
    /// local Y rotation absorbs the model's authoring-axis offset; the
    /// gameplay root rotation is untouched, so flight / taxi / projectile
    /// logic keeps working unmodified.
    /// </summary>
    public const float VisualRotationOffsetY = -96.7f;

    // Three LODs. Indexes line up with LodFbxNames / LodThresholds.
    private static readonly string[] LodFbxNames =
    {
        "FighterJet_LOD0", "FighterJet_LOD1", "FighterJet_LOD2",
    };
    private static readonly float[] LodScreenThresholds =
    {
        0.5f, 0.18f, 0.04f,
    };

    private const string OldVisualBackupName = "OldVisual_Backup";
    private const string VisualGroupName     = "Visual";
    private const string MarkersGroupName    = "Markers";

    // Old visual children we relocate to the backup group.
    private static readonly string[] OldVisualChildNames =
    {
        "Fuselage", "Nose", "WingL", "WingR", "Tail",
        "WingStripeL", "WingStripeR", "TeamColorAccent_DISABLED",
        "PodL", "PodR",
    };

    // Children that stay where they are — gameplay-critical.
    // Listed for documentation; tool just leaves them alone.
    private static readonly string[] PreservedChildren =
    {
        "FirePoint", "HealthBar", "SelectionCircle", "AmmoIndicator",
    };

    // ────────────────────────────────────────────────────────────────── //

    [MenuItem("Tools/RTS/Aircraft/Replace StrikeJet Visual With Jet2")]
    public static void Run()
    {
        Debug.Log("[Jet2Swap] ─── Replacing StrikeJet visual with jet2 asset ───");

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
        {
            Debug.LogError($"[Jet2Swap] ✗ Prefab not found: {PrefabPath}");
            return;
        }
        for (int i = 0; i < LodFbxNames.Length; i++)
        {
            string p = $"{AssetFolder}/{LodFbxNames[i]}.fbx";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(p) == null)
            {
                Debug.LogError($"[Jet2Swap] ✗ FBX not found: {p}");
                return;
            }
        }

        // 1. Texture importers.
        ConfigureTextureImports();

        // 2. Materials.
        bool urp = GraphicsSettings.currentRenderPipeline != null
                && GraphicsSettings.currentRenderPipeline.GetType().Name.Contains("Universal");
        Shader litShader = Shader.Find(urp ? "Universal Render Pipeline/Lit" : "Standard");
        if (litShader == null) litShader = Shader.Find("Standard");

        Material mainBody   = CreateOrConfigureMaterial(MatNameMainBody,   litShader, urp);
        Material darkPanels = CreateOrConfigureMaterial(MatNameDarkPanels, litShader, urp);
        Material canopy     = CreateOrConfigureMaterial(MatNameCanopy,     litShader, urp);
        Material teamColor  = CreateOrConfigureMaterial(MatNameTeamColor,  litShader, urp);

        // 3. FBX importers (scale + material remap).
        ConfigureFbxImports(mainBody, darkPanels, canopy, teamColor);

        // 4. Backup the live prefab.
        BackupPrefab();

        // 5. Edit the prefab contents.
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[Jet2Swap] ✗ LoadPrefabContents returned null.");
            return;
        }

        int rootLayer = root.layer;

        try
        {
            BackupOldVisualChildren(root);
            GameObject visual = CreateVisualWithLods(root, rootLayer);
            StripDangerousComponentsInside(visual);
            CreateMarkersGroup(root, rootLayer);
            RewireTeamColor(root, visual);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[Jet2Swap] ✓ Done. Drag StrikeJetPrefab into the scene to verify nose direction, " +
                      "team-color slot, LOD swap, and BoxCollider fit. If the nose points backward, " +
                      "set Visual.localEulerAngles.y = 180 in the prefab Inspector.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[Jet2Swap] ─────────────────────────────────────────");
    }

    // ============================================================== //
    // 1. Texture import config
    // ============================================================== //

    private static void ConfigureTextureImports()
    {
        SetTextureImport("T_FighterJet_BaseColor",  srgb: true,  asNormalMap: false);
        SetTextureImport("T_FighterJet_Normal",     srgb: false, asNormalMap: true);
        SetTextureImport("T_FighterJet_Metallic",   srgb: false, asNormalMap: false);
        SetTextureImport("T_FighterJet_Roughness",  srgb: false, asNormalMap: false);
    }

    private static void SetTextureImport(string name, bool srgb, bool asNormalMap)
    {
        string path = $"{TextureFolder}/{name}.png";
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null)
        {
            Debug.LogWarning($"[Jet2Swap]   Texture importer missing: {path} (skipped).");
            return;
        }
        ti.textureType = asNormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
        if (!asNormalMap) ti.sRGBTexture = srgb;
        ti.SaveAndReimport();
        Debug.Log($"[Jet2Swap]   Texture '{name}': type={(asNormalMap ? "Normal" : "Default")}, sRGB={srgb}.");
    }

    // ============================================================== //
    // 2. Material creation
    // ============================================================== //

    private static Material CreateOrConfigureMaterial(string baseName, Shader shader, bool urp)
    {
        EnsureFolder("Assets/_Game/Materials", "Aircraft");
        EnsureFolder("Assets/_Game/Materials/Aircraft", "Jet2");

        string path = $"{MaterialFolder}/{baseName}.mat";
        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(shader) { name = baseName };
            AssetDatabase.CreateAsset(m, path);
            Debug.Log($"[Jet2Swap]   Created material '{path}'.");
        }
        else
        {
            m.shader = shader;
        }

        Texture2D baseTex     = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/T_FighterJet_BaseColor.png");
        Texture2D normalTex   = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/T_FighterJet_Normal.png");
        Texture2D metallicTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/T_FighterJet_Metallic.png");

        switch (baseName)
        {
            case MatNameMainBody:
                ApplyTexturedMat(m, urp, baseTex, normalTex, metallicTex,
                                 baseColor: Color.white, smoothness: 0.4f, metallicFallback: 0.4f);
                break;

            case MatNameDarkPanels:
                ApplyTexturedMat(m, urp, baseTex, normalTex, metallicTex,
                                 baseColor: new Color(0.7f, 0.7f, 0.75f),
                                 smoothness: 0.25f, metallicFallback: 0.6f);
                break;

            case MatNameCanopy:
                ApplyTexturedMat(m, urp, baseTex, normalTex, metallicTex: null,
                                 baseColor: new Color(0.85f, 0.85f, 0.9f),
                                 smoothness: 0.85f, metallicFallback: 0f);
                break;

            case MatNameTeamColor:
                // No texture maps — flat tintable albedo. White default; the
                // TeamColorApplier overrides at runtime per player.
                ApplyTexturedMat(m, urp, baseTex: null, normalTex: null, metallicTex: null,
                                 baseColor: Color.white, smoothness: 0.3f, metallicFallback: 0.1f);
                break;
        }

        EditorUtility.SetDirty(m);
        return m;
    }

    private static void ApplyTexturedMat(Material m, bool urp,
                                         Texture2D baseTex, Texture2D normalTex, Texture2D metallicTex,
                                         Color baseColor, float smoothness, float metallicFallback)
    {
        if (urp)
        {
            if (m.HasProperty("_BaseMap"))   m.SetTexture("_BaseMap", baseTex);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", baseColor);
            if (normalTex != null)
            {
                m.SetTexture("_BumpMap", normalTex);
                m.EnableKeyword("_NORMALMAP");
                if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", 1f);
            }
            if (metallicTex != null)
            {
                m.SetTexture("_MetallicGlossMap", metallicTex);
                m.EnableKeyword("_METALLICSPECGLOSSMAP");
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 1f);
            }
            else
            {
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallicFallback);
            }
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        }
        else
        {
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", baseTex);
            if (m.HasProperty("_Color"))   m.SetColor("_Color", baseColor);
            if (normalTex != null)
            {
                m.SetTexture("_BumpMap", normalTex);
                m.EnableKeyword("_NORMALMAP");
            }
            if (metallicTex != null)
            {
                m.SetTexture("_MetallicGlossMap", metallicTex);
                m.EnableKeyword("_METALLICGLOSSMAP");
            }
            else
            {
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallicFallback);
            }
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
        }
    }

    // ============================================================== //
    // 3. FBX import config + material remap
    // ============================================================== //

    private static void ConfigureFbxImports(Material mainBody, Material darkPanels,
                                            Material canopy, Material teamColor)
    {
        for (int i = 0; i < LodFbxNames.Length; i++)
        {
            string path = $"{AssetFolder}/{LodFbxNames[i]}.fbx";
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null) continue;

            mi.globalScale          = TargetScaleFactor;
            mi.addCollider          = false;
            mi.materialImportMode   = ModelImporterMaterialImportMode.ImportStandard;
            mi.materialLocation     = ModelImporterMaterialLocation.External;

            // Remap by source material name (set in the FBX). On the very
            // first re-import the FBX's own materials are extracted; on
            // subsequent runs the remap survives.
            mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), MatNameMainBody),   mainBody);
            mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), MatNameDarkPanels), darkPanels);
            mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), MatNameCanopy),     canopy);
            mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), MatNameTeamColor),  teamColor);
            mi.SaveAndReimport();
            Debug.Log($"[Jet2Swap]   FBX '{LodFbxNames[i]}': scale={TargetScaleFactor}, materials remapped.");
        }
    }

    // ============================================================== //
    // 4. Backup
    // ============================================================== //

    private static void BackupPrefab()
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(BackupPath) != null)
        {
            AssetDatabase.DeleteAsset(BackupPath);
        }
        bool ok = AssetDatabase.CopyAsset(PrefabPath, BackupPath);
        if (!ok)
        {
            Debug.LogError($"[Jet2Swap] ✗ Failed to copy '{PrefabPath}' → '{BackupPath}'.");
            return;
        }
        Debug.Log($"[Jet2Swap]   Backed up '{PrefabPath}' → '{BackupPath}'.");
    }

    // ============================================================== //
    // 5. Prefab editing
    // ============================================================== //

    private static void BackupOldVisualChildren(GameObject root)
    {
        Transform backup = root.transform.Find(OldVisualBackupName);
        if (backup == null)
        {
            GameObject bg = new GameObject(OldVisualBackupName);
            bg.transform.SetParent(root.transform, false);
            bg.SetActive(false);
            backup = bg.transform;
        }

        int moved = 0;
        var names = new HashSet<string>(OldVisualChildNames);
        // Snapshot — reparenting mutates childCount.
        var children = new List<Transform>();
        for (int i = 0; i < root.transform.childCount; i++)
            children.Add(root.transform.GetChild(i));

        foreach (var c in children)
        {
            if (c == null) continue;
            if (c == backup) continue;
            if (!names.Contains(c.gameObject.name)) continue;

            c.SetParent(backup, worldPositionStays: true);
            c.gameObject.SetActive(false);
            moved++;
            Debug.Log($"[Jet2Swap]     Backed up '{c.name}' → '{OldVisualBackupName}/{c.name}' (inactive).");
        }

        // Remove any prior Visual / Markers from a previous run.
        DestroyChildByName(root, VisualGroupName);
        DestroyChildByName(root, MarkersGroupName);

        Debug.Log($"[Jet2Swap]   Moved {moved} old visual child(ren) to '{OldVisualBackupName}'.");
    }

    private static GameObject CreateVisualWithLods(GameObject root, int rootLayer)
    {
        GameObject visual = new GameObject(VisualGroupName);
        visual.transform.SetParent(root.transform, false);
        // Apply the model-forward-axis correction immediately. Re-runs of
        // this tool therefore preserve the orientation fix (no need to
        // re-touch the Visual child via the Repair tool every time).
        visual.transform.localEulerAngles = new Vector3(0f, VisualRotationOffsetY, 0f);
        visual.layer = rootLayer;

        var lodGroup = visual.AddComponent<LODGroup>();
        var lods = new LOD[LodFbxNames.Length];

        for (int i = 0; i < LodFbxNames.Length; i++)
        {
            string fbxPath = $"{AssetFolder}/{LodFbxNames[i]}.fbx";
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            inst.name = $"LOD{i}";
            inst.transform.SetParent(visual.transform, false);
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.transform.localScale    = Vector3.one;
            SetLayerRecursive(inst, rootLayer);

            var rends = inst.GetComponentsInChildren<Renderer>(includeInactive: true);
            lods[i] = new LOD(LodScreenThresholds[i], rends);
        }

        lodGroup.SetLODs(lods);
        lodGroup.RecalculateBounds();

        // Defensive: log the combined renderer bounds so the user can
        // eyeball whether the swap landed at the right size.
        Renderer[] allR = visual.GetComponentsInChildren<Renderer>(includeInactive: false);
        Bounds combined = default;
        bool first = true;
        for (int i = 0; i < allR.Length; i++)
        {
            if (allR[i] == null) continue;
            if (first) { combined = allR[i].bounds; first = false; }
            else       { combined.Encapsulate(allR[i].bounds); }
        }
        if (!first)
            Debug.Log($"[Jet2Swap]   Visual combined renderer bounds size = {combined.size:F2} " +
                      "(BoxCollider is (2.20, 0.60, 2.40) — should be similar).");

        return visual;
    }

    private static void StripDangerousComponentsInside(GameObject visual)
    {
        int colliders = 0, lights = 0, cams = 0, anims = 0;
        foreach (var c in visual.GetComponentsInChildren<Collider>(true))
        {
            if (c == null) continue;
            Object.DestroyImmediate(c, true);
            colliders++;
        }
        foreach (var l in visual.GetComponentsInChildren<Light>(true))
        {
            if (l == null) continue;
            Object.DestroyImmediate(l, true);
            lights++;
        }
        foreach (var cam in visual.GetComponentsInChildren<Camera>(true))
        {
            if (cam == null) continue;
            Object.DestroyImmediate(cam, true);
            cams++;
        }
        foreach (var a in visual.GetComponentsInChildren<Animator>(true))
        {
            if (a == null) continue;
            Object.DestroyImmediate(a, true);
            anims++;
        }
        if (colliders + lights + cams + anims > 0)
            Debug.Log($"[Jet2Swap]   Stripped from new visual: Colliders={colliders}, " +
                      $"Lights={lights}, Cameras={cams}, Animators={anims}.");
    }

    private static void CreateMarkersGroup(GameObject root, int rootLayer)
    {
        var markers = new GameObject(MarkersGroupName);
        markers.transform.SetParent(root.transform, false);
        markers.layer = rootLayer;

        // Empty-marker positions tuned for a ~2.4 m jet. Nudge in the
        // Inspector if the visual lands slightly off — these are just
        // empties used by future VFX/weapons hooks, not gameplay.
        CreateMarker(markers.transform, "NosePoint",         new Vector3( 0f,   0.3f,  1.2f), rootLayer);
        CreateMarker(markers.transform, "Hardpoint_Left",    new Vector3(-0.6f, 0f,    0f),   rootLayer);
        CreateMarker(markers.transform, "Hardpoint_Right",   new Vector3( 0.6f, 0f,    0f),   rootLayer);
        CreateMarker(markers.transform, "EngineFX_Left",     new Vector3(-0.3f, 0.2f, -1.1f), rootLayer);
        CreateMarker(markers.transform, "EngineFX_Right",    new Vector3( 0.3f, 0.2f, -1.1f), rootLayer);
        Debug.Log("[Jet2Swap]   Created Markers group with NosePoint / Hardpoint_L/R / EngineFX_L/R.");
    }

    private static void CreateMarker(Transform parent, string name, Vector3 localPos, int layer)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale    = Vector3.one;
        go.layer = layer;
    }

    private static void RewireTeamColor(GameObject root, GameObject visual)
    {
        // Make the existing root TeamColorMarker inert — its serialized
        // fileIDs reference the now-inactive old children, and even if we
        // re-pointed them to the new LOD renderers, the MaterialPropertyBlock
        // path would tint ALL material slots (including M_Jet_MainBody's
        // textured slot) instead of only M_TeamColor.
        var marker = root.GetComponent<TeamColorMarker>();
        if (marker != null)
        {
            int cleared = marker.bodyColorRenderers.Count
                        + marker.detailRenderers.Count
                        + marker.ignoreRenderers.Count;
            marker.bodyColorRenderers.Clear();
            marker.detailRenderers.Clear();
            marker.ignoreRenderers.Clear();
            marker.applyToBaseColor = false;
            marker.applyToEmission  = false;
            EditorUtility.SetDirty(marker);
            Debug.Log($"[Jet2Swap]   TeamColorMarker on prefab root made inert ({cleared} stale renderer refs cleared).");
        }

        // Add TeamColorApplier — it's the right tool for per-slot painting
        // because it writes to instance materials at the listed slot index
        // only. We point it at every LOD renderer and target slot 3
        // (M_TeamColor) — the flat tintable material the README author
        // designed for team recolouring.
        var applier = root.GetComponent<TeamColorApplier>();
        if (applier == null)
        {
            applier = root.AddComponent<TeamColorApplier>();
        }
        applier.teamColorSlots.Clear();
        applier.fixedColorSlots.Clear();

        // Walk EVERY renderer (incl. inactive LOD1/LOD2 that the LODGroup
        // toggles at runtime), find each material slot whose name contains
        // "TeamColor" (case-insensitive substring), and wire those (renderer,
        // slot) pairs. Name-based is robust against FBX re-import producing
        // a slot order different from the README's expected index 3.
        int wired = 0, slotsTotal = 0;
        var lodRends = visual.GetComponentsInChildren<Renderer>(includeInactive: true);
        for (int i = 0; i < lodRends.Length; i++)
        {
            Renderer r = lodRends[i];
            if (r == null) continue;
            var mats = r.sharedMaterials;
            if (mats == null) continue;
            var indices = new List<int>();
            for (int mi = 0; mi < mats.Length; mi++)
            {
                if (mats[mi] == null) continue;
                if (mats[mi].name.IndexOf("TeamColor",
                                          System.StringComparison.OrdinalIgnoreCase) >= 0)
                    indices.Add(mi);
            }
            if (indices.Count == 0) continue;
            applier.teamColorSlots.Add(new RendererMaterialSlot
            {
                renderer        = r,
                materialIndexes = indices,
            });
            wired++;
            slotsTotal += indices.Count;
        }
        applier.applyOnStart = true;
        EditorUtility.SetDirty(applier);

        Debug.Log($"[Jet2Swap]   TeamColorApplier wired: {wired} renderer(s), {slotsTotal} team-color " +
                  "slot(s) detected by name match against 'TeamColor'.");
    }

    // ============================================================== //
    // Helpers
    // ============================================================== //

    private static void DestroyChildByName(GameObject root, string name)
    {
        Transform t = root.transform.Find(name);
        if (t != null) Object.DestroyImmediate(t.gameObject);
    }

    private static void SetLayerRecursive(GameObject go, int layer)
    {
        if (go == null) return;
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++)
            SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
    }

    private static void EnsureFolder(string parent, string sub)
    {
        string full = $"{parent}/{sub}";
        if (AssetDatabase.IsValidFolder(full)) return;
        if (!AssetDatabase.IsValidFolder(parent))
        {
            // Walk up one more level if needed.
            int slash = parent.LastIndexOf('/');
            if (slash > 0) EnsureFolder(parent.Substring(0, slash), parent.Substring(slash + 1));
        }
        AssetDatabase.CreateFolder(parent, sub);
    }
}
