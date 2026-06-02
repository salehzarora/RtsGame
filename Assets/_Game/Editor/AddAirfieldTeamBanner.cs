using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Adds a single team-coloured feather-flag-style banner to the
/// AirfieldPrefab. Pure visual decoration — no gameplay touch.
///
/// HIERARCHY (all under one parent group on AirfieldPrefab root)
///   TeamBanner                      — empty pivot at the NW edge of the
///                                     building
///     BannerPole                    — thin dark-gray cube acting as the
///                                     pole; fixed colour, NEVER team-tinted
///                                     so the structure reads as "metal
///                                     pole + coloured fabric" instead of
///                                     "single coloured stick"
///     BannerFabric                  — slim vertical panel attached east
///                                     of the pole. Uses the existing
///                                     M_AirfieldLED.mat. A small
///                                     TeamColorMarker on this GameObject
///                                     paints _EmissionColor with the
///                                     owner's team colour at low
///                                     intensity (~1.2) so it reads as a
///                                     subtly-glowing coloured banner —
///                                     not a neon sign.
///     BannerLedTrim                 — even thinner vertical strip just
///                                     past the fabric's outer edge,
///                                     same material but a SECOND
///                                     TeamColorMarker on this child at
///                                     higher intensity (~2.5) so it
///                                     reads as a bright LED line edging
///                                     the banner.
///
/// WHY TWO TeamColorMarkers (one per child renderer)
///   The shared <c>TeamColorMarker</c> class is
///   <c>[DisallowMultipleComponent]</c> — one per GameObject. Putting one
///   on BannerFabric and one on BannerLedTrim is fine (different GOs) and
///   gives each its own <c>emissionIntensity</c>. That's how we make the
///   trim brighter than the fabric while reusing the existing team-colour
///   system. Both markers walk up the hierarchy to find the AirfieldPrefab's
///   <c>GameEntity</c>, so the owner colour resolves correctly on every
///   client (same multiplayer-safe path the LED contour uses).
///
/// PLACEMENT
///   Default attach point is the building's NW corner area at
///   <see cref="BannerLocalPos"/>. This is clear of:
///     • the runway centerline (X ≈ 0),
///     • parking slots (X = ±8, ±5),
///     • taxi paths (X centerline area, Z ≤ -1),
///     • landing approach (Z > 12).
///   You can drag the <c>TeamBanner</c> child in the Scene view to relocate
///   the whole assembly without touching scripts; everything else is
///   parented to it.
///
/// MATERIALS
///   • <c>M_BannerPole.mat</c>  — created on first run, dark gray, fixed
///     (never team-tinted).
///   • <c>M_AirfieldLED.mat</c>  — shared with the existing LED contour
///     tool. Created on first run if missing. Emission keyword ON so MPB
///     <c>_EmissionColor</c> overrides take effect.
///
/// WHAT IS NOT TOUCHED
///   • Slot_*, Taxi_*, runway / lane / landing markers, BoxCollider,
///     Visual_NewAirfield (the FBX), the existing LED contour, the root
///     TeamColorMarker (which still drives the LED contour).
///   • Airfield.cs, AirUnitController.cs, BuildingPlacementManager.cs,
///     ConstructionSite.cs — zero gameplay touch.
///
/// Re-running deletes the existing TeamBanner subtree and rebuilds. Safe.
///
/// Menu: Tools → RTS → Buildings → Add Airfield Team Banner
/// </summary>
public static class AddAirfieldTeamBanner
{
    private const string PrefabPath          = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";
    private const string MaterialFolder      = "Assets/_Game/Materials/Airfield";
    private const string LedMaterialPath     = "Assets/_Game/Materials/Airfield/M_AirfieldLED.mat";
    private const string PoleMaterialPath    = "Assets/_Game/Materials/Airfield/M_BannerPole.mat";
    private const string BuildingLayerName   = "Building";
    /// <summary>Old single-banner name from the previous version — deleted on every run.</summary>
    private const string LegacyBannerGroupName = "TeamBanner";

    /// <summary>One mounted banner. Each becomes a top-level child group of AirfieldPrefab.</summary>
    private struct BannerConfig
    {
        public string  Name;       // Child object name on AirfieldPrefab root
        public Vector3 LocalPos;   // localPosition under the AirfieldPrefab root
        public float   RotationY;  // localRotation Y — 180° mirrors the fabric+LED to the opposite side of the pole
    }

    /// <summary>
    /// User-specified placement: exactly two banners at the user-given
    /// local coordinates. Banner_A is rotated 180° around Y so its fabric
    /// + LED trim mirror to the WEST of its pole; Banner_B keeps identity
    /// rotation so its fabric + LED mirror to the EAST of its pole. The
    /// result is a symmetric pair flanking the north edge of the
    /// airfield, both fabrics facing outward.
    /// </summary>
    private static readonly BannerConfig[] Banners =
    {
        new BannerConfig { Name = "TeamBanner_A", LocalPos = new Vector3(-3.75f, 0f, 7.25f), RotationY = 180f },
        new BannerConfig { Name = "TeamBanner_B", LocalPos = new Vector3( 3f,    0f, 7.25f), RotationY =   0f },
    };

    // Subtle intensity for the fabric (soft team-colour wash). Brighter
    // intensity for the LED trim. Both flow through MPB onto the same
    // shared M_AirfieldLED material; only intensities differ per
    // TeamColorMarker.
    private const float FabricEmissionIntensity = 1.2f;
    private const float LedEmissionIntensity    = 2.5f;

    [MenuItem("Tools/RTS/Buildings/Add Airfield Team Banner")]
    public static void Add()
    {
        Debug.Log("[AddAirfieldBanner] ─── Building team banner on AirfieldPrefab ───");

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
        {
            Debug.LogError($"[AddAirfieldBanner] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        Material ledMat  = LoadOrCreateLedMaterial();
        Material poleMat = LoadOrCreatePoleMaterial();
        if (ledMat == null || poleMat == null) return;

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[AddAirfieldBanner] ✗ LoadPrefabContents returned null.");
            return;
        }

        int buildingLayer = LayerMask.NameToLayer(BuildingLayerName);
        if (buildingLayer < 0) buildingLayer = root.layer;

        try
        {
            // Idempotency — wipe the old single-banner group from v1 and
            // any prior versions of TeamBanner_A / TeamBanner_B before
            // rebuilding. Ensures we end with exactly the two banners
            // declared in <see cref="Banners"/>.
            int removed = 0;
            removed += DeleteChildByName(root, LegacyBannerGroupName);
            for (int i = 0; i < Banners.Length; i++)
                removed += DeleteChildByName(root, Banners[i].Name);
            if (removed > 0)
                Debug.Log($"[AddAirfieldBanner]   Removed {removed} previous banner group(s) before rebuild.");

            int built = 0;
            for (int i = 0; i < Banners.Length; i++)
            {
                BuildOneBanner(root.transform, Banners[i], poleMat, ledMat, buildingLayer);
                built++;
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AddAirfieldBanner] ✓ Done. Built {built} banner(s) as direct children of AirfieldPrefab. " +
                      "Each is a group (pole + fabric + LED trim) with two TeamColorMarkers (fabric soft, " +
                      "LED bright). Drag TeamBanner_A / TeamBanner_B in Scene view to nudge; intensities are " +
                      "Inspector sliders on each marker.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[AddAirfieldBanner] ─────────────────────────────────────────");
    }

    // ================================================================== //
    // Per-banner build
    // ================================================================== //

    /// <summary>
    /// Builds one banner group with pole + fabric + LED trim, mounted to
    /// <paramref name="root"/> at the config's exact local position and
    /// rotation. All sub-object local transforms remain identical between
    /// banners; the per-banner config only controls where the GROUP sits
    /// and which way it faces (0° = fabric east, 180° = fabric west).
    /// </summary>
    private static void BuildOneBanner(Transform root, BannerConfig cfg,
                                       Material poleMat, Material ledMat, int layer)
    {
        GameObject group = new GameObject(cfg.Name);
        group.transform.SetParent(root, false);
        group.transform.localPosition    = cfg.LocalPos;
        group.transform.localEulerAngles = new Vector3(0f, cfg.RotationY, 0f);
        group.transform.localScale       = Vector3.one;
        group.layer = layer;
        Debug.Log($"[AddAirfieldBanner]   Created '{cfg.Name}' at localPos {cfg.LocalPos}, " +
                  $"localRotY = {cfg.RotationY:F0}°.");

        // ── Pole — thin dark cube, fixed colour, never team-tinted. ── //
        CreateChildCube(
            name:       "BannerPole",
            parent:     group.transform,
            localPos:   new Vector3(0f,   2f,  0f),
            localScale: new Vector3(0.10f, 4f, 0.10f),
            material:   poleMat,
            layer:      layer);

        // ── Banner fabric — soft team-color glow ─────────────────── //
        // Mounted on the +X face of the pole. Rotation on the group can
        // flip this to -X (mirrored banner) without changing the local
        // numbers below.
        GameObject fabric = CreateChildCube(
            name:       "BannerFabric",
            parent:     group.transform,
            localPos:   new Vector3(0.30f, 2.5f, 0f),
            localScale: new Vector3(0.50f, 3f,  0.04f),
            material:   ledMat,
            layer:      layer);
        AttachTeamColorMarker(fabric, FabricEmissionIntensity);

        // ── LED trim — bright team-color line, just past fabric's edge. //
        GameObject led = CreateChildCube(
            name:       "BannerLedTrim",
            parent:     group.transform,
            localPos:   new Vector3(0.575f, 2.5f, 0f),
            localScale: new Vector3(0.02f,  3f,  0.06f),
            material:   ledMat,
            layer:      layer);
        AttachTeamColorMarker(led, LedEmissionIntensity);
    }

    private static int DeleteChildByName(GameObject root, string name)
    {
        Transform t = root.transform.Find(name);
        if (t == null) return 0;
        Object.DestroyImmediate(t.gameObject);
        return 1;
    }

    // ================================================================== //
    // Helpers
    // ================================================================== //

    private static GameObject CreateChildCube(string name, Transform parent,
                                              Vector3 localPos, Vector3 localScale,
                                              Material material, int layer)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;

        // Strip the default cube collider. The banner must NOT affect
        // selection / placement / NavMesh.
        Collider col = go.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col, true);

        go.transform.SetParent(parent, worldPositionStays: false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale    = localScale;
        go.layer = layer;

        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial       = material;
            mr.shadowCastingMode    = ShadowCastingMode.Off;
            mr.receiveShadows       = false;
            mr.lightProbeUsage      = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }
        return go;
    }

    /// <summary>
    /// Adds a <see cref="TeamColorMarker"/> to <paramref name="go"/> with
    /// only its own MeshRenderer in the body list. The marker walks up to
    /// the AirfieldPrefab's <see cref="GameEntity"/> to resolve the owner
    /// id; same code path the LED contour markers use.
    /// </summary>
    private static void AttachTeamColorMarker(GameObject go, float intensity)
    {
        TeamColorMarker m = go.GetComponent<TeamColorMarker>();
        if (m == null) m = go.AddComponent<TeamColorMarker>();
        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        m.bodyColorRenderers.Clear();
        if (mr != null) m.bodyColorRenderers.Add(mr);
        m.applyToBaseColor  = false;
        m.applyToEmission   = true;
        m.emissionIntensity = intensity;
        EditorUtility.SetDirty(m);
    }

    // ================================================================== //
    // Materials
    // ================================================================== //

    private static Material LoadOrCreateLedMaterial()
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(LedMaterialPath);
        if (existing != null) return existing;
        EnsureMaterialFolder();

        Shader sh = DetectLitShader();
        Material m = new Material(sh) { name = "M_AirfieldLED" };
        ConfigureLedMaterial(m);
        AssetDatabase.CreateAsset(m, LedMaterialPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[AddAirfieldBanner]   Created shared LED material '{LedMaterialPath}'.");
        return m;
    }

    private static void ConfigureLedMaterial(Material m)
    {
        bool urp = m.shader != null && m.shader.name.Contains("Universal Render Pipeline");
        if (urp)
        {
            if (m.HasProperty("_BaseColor"))  m.SetColor("_BaseColor", new Color(0.05f, 0.05f, 0.05f));
            if (m.HasProperty("_Metallic"))   m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.25f);
        }
        else
        {
            if (m.HasProperty("_Color"))      m.SetColor("_Color", new Color(0.05f, 0.05f, 0.05f));
            if (m.HasProperty("_Metallic"))   m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.25f);
        }
        m.EnableKeyword("_EMISSION");
        if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.white);
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
    }

    private static Material LoadOrCreatePoleMaterial()
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(PoleMaterialPath);
        if (existing != null) return existing;
        EnsureMaterialFolder();

        Shader sh = DetectLitShader();
        Material m = new Material(sh) { name = "M_BannerPole" };
        bool urp = sh.name.Contains("Universal Render Pipeline");
        if (urp)
        {
            if (m.HasProperty("_BaseColor"))   m.SetColor("_BaseColor", new Color(0.18f, 0.18f, 0.20f));
            if (m.HasProperty("_Metallic"))    m.SetFloat("_Metallic", 0.6f);
            if (m.HasProperty("_Smoothness"))  m.SetFloat("_Smoothness", 0.45f);
        }
        else
        {
            if (m.HasProperty("_Color"))       m.SetColor("_Color", new Color(0.18f, 0.18f, 0.20f));
            if (m.HasProperty("_Metallic"))    m.SetFloat("_Metallic", 0.6f);
            if (m.HasProperty("_Glossiness"))  m.SetFloat("_Glossiness", 0.45f);
        }
        // No emission keyword — pole stays a steady metallic gray.
        AssetDatabase.CreateAsset(m, PoleMaterialPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[AddAirfieldBanner]   Created pole material '{PoleMaterialPath}'.");
        return m;
    }

    private static Shader DetectLitShader()
    {
        bool urp = GraphicsSettings.currentRenderPipeline != null
                && GraphicsSettings.currentRenderPipeline.GetType().Name.Contains("Universal");
        Shader sh = Shader.Find(urp ? "Universal Render Pipeline/Lit" : "Standard");
        if (sh == null) sh = Shader.Find("Standard");
        return sh;
    }

    private static void EnsureMaterialFolder()
    {
        if (AssetDatabase.IsValidFolder(MaterialFolder)) return;
        string parent = "Assets/_Game/Materials";
        if (!AssetDatabase.IsValidFolder(parent))
            AssetDatabase.CreateFolder("Assets/_Game", "Materials");
        AssetDatabase.CreateFolder(parent, "Airfield");
    }
}
