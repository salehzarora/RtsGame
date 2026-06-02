using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Adds (or refreshes) a small set of glowing LED-strip children on
/// AirfieldPrefab and wires them into the existing
/// <see cref="TeamColorMarker"/> on the prefab root so the glow always
/// matches the owning player's team color.
///
/// DESIGN
///   • Each strip is a thin <see cref="PrimitiveType.Cube"/> sized like a
///     bar (e.g. 0.15 × 0.15 × 10 m). No collider, no shadow casting —
///     purely visual.
///   • Strips share a single material asset
///     <c>Assets/_Game/Materials/Airfield/M_AirfieldLED.mat</c> (created
///     by this tool on first run). The material's base colour is dark
///     grey + smooth low; the <c>_EMISSION</c> keyword is on so the
///     <c>_EmissionColor</c> property responds to MaterialPropertyBlock
///     overrides.
///   • At gameplay time the existing TeamColorMarker on AirfieldPrefab
///     paints _EmissionColor on each strip renderer via MPB. The marker
///     is already wired to MultiplayerColors / PlayerFactionManager — so
///     red player ⇒ red strips, blue ⇒ blue, etc., on every client. No
///     new team-color system, no script changes.
///   • <see cref="TeamColorMarker.applyToBaseColor"/> is set to FALSE so
///     the FBX airfield meshes are NOT recoloured (their baked airport
///     markings stay intact). Only the strips' _EmissionColor — which is
///     the only emission keyword in the team-color list — visibly changes.
///
/// LAYOUT
///   Four strips form a rectangle perimeter on top of the central
///   building area at Y = 4 m. East/west rails run along Z; north/south
///   crossbeams run along X. Positions are stored in <see cref="Strips"/>
///   and easy to tune: edit the constants and re-run the menu (idempotent).
///
/// WHAT IS NOT TOUCHED
///   • Airfield.cs, AirUnitController.cs, production / takeoff / landing.
///   • Slot_*, Taxi_*, runway / lane / landing markers.
///   • Visual_NewAirfield (the imported FBX) — strips are direct children
///     of the prefab root, NOT of the FBX prefab variant.
///   • Root BoxCollider, root scale, network components, GameEntity.
///   • <see cref="TeamColorMarker"/>.team / fallbackColor.
///
/// Menu: Tools → RTS → Buildings → Add Airfield LED Strips
/// </summary>
public static class AddAirfieldLedStrips
{
    private const string PrefabPath     = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";
    private const string MaterialFolder = "Assets/_Game/Materials/Airfield";
    private const string MaterialPath   = "Assets/_Game/Materials/Airfield/M_AirfieldLED.mat";

    // Glow strength. 1.0 = strip emission = pure team colour.
    // 2.0–2.5 = bright neon. >3 = very bright. The user can tweak this in
    // the Inspector on the AirfieldPrefab's TeamColorMarker
    // (Emission Intensity slider) without re-running this menu.
    private const float EmissionIntensity = 2.0f;

    private const string BuildingLayerName = "Building";

    /// <summary>One LED strip definition. Edit these and re-run the menu to retune.</summary>
    private struct Strip
    {
        public string Name;
        public Vector3 LocalPos;
        public Vector3 LocalScale;
    }

    // GROUND-LEVEL PERIMETER. The previous Y=4 rectangle looked like a
    // floating ring above the building. These four strips now hug the
    // outer ±13.5 m edge of the 28 × 28 m visible footprint (just inside
    // the 14 m collider half-width), sitting at Y = 0.08 — 8 cm above
    // ground, enough to clear the airfield's surface meshes without
    // z-fighting. Each strip is ~27 m long on its dominant axis so the
    // four together draw a continuous rectangle perimeter around the
    // building base. Slight overlap at the corners is intentional and
    // reads as a thicker "corner block" — pleasant rather than awkward.
    //
    // Why ±13.5 instead of ±14: the BoxCollider half-width is 14 m and the
    // visible apron extends to roughly there. Inset by 0.5 m so the strip
    // is unambiguously ON the airfield surface, not poking off the edge.
    private static readonly Strip[] Strips =
    {
        new Strip { Name = "LedStrip_East",       LocalPos = new Vector3( 13.5f, 0.08f,  0f),    LocalScale = new Vector3(0.15f, 0.1f, 27f)   },
        new Strip { Name = "LedStrip_West",       LocalPos = new Vector3(-13.5f, 0.08f,  0f),    LocalScale = new Vector3(0.15f, 0.1f, 27f)   },
        new Strip { Name = "LedStrip_North",      LocalPos = new Vector3( 0f,    0.08f,  13.5f), LocalScale = new Vector3(27f,   0.1f, 0.15f) },
        new Strip { Name = "LedStrip_South",      LocalPos = new Vector3( 0f,    0.08f, -13.5f), LocalScale = new Vector3(27f,   0.1f, 0.15f) },
    };

    [MenuItem("Tools/RTS/Buildings/Add Airfield LED Strips")]
    public static void Add()
    {
        Debug.Log("[AddAirfieldLED] ─── Adding team-coloured LED strips to AirfieldPrefab ───");

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
        {
            Debug.LogError($"[AddAirfieldLED] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        // ── 1. Material ────────────────────────────────────────────── //
        Material ledMat = LoadOrCreateLedMaterial();
        if (ledMat == null)
        {
            Debug.LogError("[AddAirfieldLED] ✗ Failed to create or load LED material — aborting.");
            return;
        }

        // ── 2. Prefab contents ─────────────────────────────────────── //
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[AddAirfieldLED] ✗ LoadPrefabContents returned null.");
            return;
        }

        int buildingLayer = LayerMask.NameToLayer(BuildingLayerName);
        if (buildingLayer < 0) buildingLayer = root.layer;

        try
        {
            List<Renderer> stripRenderers = new List<Renderer>(Strips.Length);

            // ── 3. Create / refresh strip GameObjects ─────────────── //
            for (int i = 0; i < Strips.Length; i++)
            {
                Strip s = Strips[i];
                GameObject go = EnsureStripGameObject(root, s, ledMat, buildingLayer);
                MeshRenderer mr = go.GetComponent<MeshRenderer>();
                if (mr != null) stripRenderers.Add(mr);
            }

            // ── 4. Wire the existing TeamColorMarker ───────────────── //
            WireTeamColorMarker(root, stripRenderers);

            // ── 5. Save ────────────────────────────────────────────── //
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AddAirfieldLED] ✓ Done. {stripRenderers.Count} strip(s) wired to TeamColorMarker " +
                      $"(applyToBaseColor=false, applyToEmission=true, intensity={EmissionIntensity}). " +
                      "Strips will pick up the owning team's colour at runtime via MultiplayerColors / PlayerFactionManager " +
                      "exactly like every other team-coloured unit.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[AddAirfieldLED] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // Material
    // ================================================================== //

    private static Material LoadOrCreateLedMaterial()
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (existing != null)
        {
            EnsureLedMaterialConfig(existing);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        if (!AssetDatabase.IsValidFolder(MaterialFolder))
        {
            // Walk up and create whichever segments don't exist.
            string parent = "Assets/_Game/Materials";
            if (!AssetDatabase.IsValidFolder(parent))
                AssetDatabase.CreateFolder("Assets/_Game", "Materials");
            AssetDatabase.CreateFolder(parent, "Airfield");
        }

        Shader sh = DetectLitShader();
        Material m = new Material(sh) { name = "M_AirfieldLED" };
        EnsureLedMaterialConfig(m);
        AssetDatabase.CreateAsset(m, MaterialPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[AddAirfieldLED]   Created material '{MaterialPath}' (shader: {sh.name}).");
        return m;
    }

    /// <summary>
    /// Idempotent material config — sets base/metallic/smoothness/emission
    /// the same way every time, so re-running this tool can't drift the
    /// material into a broken state. Works on URP Lit (recommended) and
    /// Built-in Standard.
    /// </summary>
    private static void EnsureLedMaterialConfig(Material m)
    {
        bool urp = m.shader != null && m.shader.name.Contains("Universal Render Pipeline");

        if (urp)
        {
            if (m.HasProperty("_BaseColor"))   m.SetColor("_BaseColor", new Color(0.05f, 0.05f, 0.05f));
            if (m.HasProperty("_Metallic"))    m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Smoothness"))  m.SetFloat("_Smoothness", 0.25f);
        }
        else
        {
            if (m.HasProperty("_Color"))     m.SetColor("_Color", new Color(0.05f, 0.05f, 0.05f));
            if (m.HasProperty("_Metallic"))  m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.25f);
        }

        // Emission ON — required so the per-renderer MPB _EmissionColor
        // takes effect at runtime. Default colour is white so the strip
        // is visibly emissive even before TeamColorMarker runs (e.g. in
        // the Scene view / play-mode-first-frame).
        m.EnableKeyword("_EMISSION");
        if (m.HasProperty("_EmissionColor"))
            m.SetColor("_EmissionColor", Color.white);
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
    }

    private static Shader DetectLitShader()
    {
        bool urp = GraphicsSettings.currentRenderPipeline != null
                && GraphicsSettings.currentRenderPipeline.GetType().Name.Contains("Universal");
        Shader sh = Shader.Find(urp ? "Universal Render Pipeline/Lit" : "Standard");
        if (sh == null) sh = Shader.Find("Standard");
        return sh;
    }

    // ================================================================== //
    // GameObject creation
    // ================================================================== //

    private static GameObject EnsureStripGameObject(GameObject root, Strip s, Material ledMat, int buildingLayer)
    {
        Transform existing = root.transform.Find(s.Name);
        GameObject go;
        if (existing != null)
        {
            go = existing.gameObject;
        }
        else
        {
            // CreatePrimitive briefly puts the cube in the active scene
            // (not the prefab-contents temp scene). SetParent moves it into
            // the prefab's scene — exactly what we want before SaveAsPrefab.
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = s.Name;

            // Strip the default cube collider — purely visual, must not
            // intercept clicks or NavMesh.
            Collider c = go.GetComponent<Collider>();
            if (c != null) Object.DestroyImmediate(c, true);

            go.transform.SetParent(root.transform, worldPositionStays: false);
            Debug.Log($"[AddAirfieldLED]   Created '{s.Name}' as child of AirfieldPrefab.");
        }

        // Reset transform to the canonical strip values (idempotent).
        go.transform.localPosition = s.LocalPos;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale    = s.LocalScale;
        go.layer = buildingLayer;

        // Material assignment. sharedMaterial (not material) keeps a single
        // asset reference — per-instance team-color override flows through
        // the MPB the TeamColorMarker writes at runtime.
        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial      = ledMat;
            mr.shadowCastingMode   = ShadowCastingMode.Off;
            mr.receiveShadows      = false;
            mr.lightProbeUsage     = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }
        return go;
    }

    // ================================================================== //
    // TeamColorMarker wiring
    // ================================================================== //

    private static void WireTeamColorMarker(GameObject root, List<Renderer> stripRenderers)
    {
        TeamColorMarker marker = root.GetComponent<TeamColorMarker>();
        if (marker == null)
        {
            marker = root.AddComponent<TeamColorMarker>();
            Debug.Log("[AddAirfieldLED]   AirfieldPrefab had no TeamColorMarker — added one.");
        }

        marker.bodyColorRenderers.Clear();
        for (int i = 0; i < stripRenderers.Count; i++)
            marker.bodyColorRenderers.Add(stripRenderers[i]);

        // Emission-only. The base colour of the FBX (markings, hangars,
        // runway numbers) MUST stay untouched — only the emission keyword
        // on the strip material responds, so this naturally targets the
        // strips alone even though the renderer list is on the marker.
        marker.applyToBaseColor   = false;
        marker.applyToEmission    = true;
        marker.emissionIntensity  = EmissionIntensity;

        EditorUtility.SetDirty(marker);

        Debug.Log($"[AddAirfieldLED]   TeamColorMarker.bodyColorRenderers = {stripRenderers.Count} LED strip(s). " +
                  $"applyToBaseColor=false, applyToEmission=true, emissionIntensity={EmissionIntensity}.");
    }
}
