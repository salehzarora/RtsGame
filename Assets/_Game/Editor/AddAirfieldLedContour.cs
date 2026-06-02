using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Replaces the previous rectangular LED border on AirfieldPrefab with a
/// HULL-FOLLOWING ring: samples the actual base-level vertices of the
/// imported airfield FBX, computes a polar silhouette (12 segments at 30°
/// apart), and lays down one short LED strip GameObject per segment so
/// the glow traces the building's real outer outline instead of a generic
/// bounding box.
///
/// HOW THE HULL IS COMPUTED
///   1. Walk every MeshFilter under <c>Visual_NewAirfield</c>.
///   2. Sample up to ~2000 vertices per mesh (every Nth vertex if larger).
///   3. Transform each vertex into AirfieldPrefab-local space via the
///      MeshFilter's <c>localToWorldMatrix</c>. Since
///      <see cref="PrefabUtility.LoadPrefabContents"/> loads the prefab
///      with the root at world origin / identity / scale 1, "world" in
///      that temporary scene equals airfield-local. The Y axis is the
///      airfield's vertical (pivot is bottom-center per the FBX docs).
///   4. Keep only vertices whose airfield-local Y is in [0, BaseSampleMaxY]
///      so the hull is taken from the BASE of the structure rather than
///      the tall central tower.
///   5. For each of <see cref="SegmentCount"/> directions evenly spaced
///      around the Y axis (default 12 → every 30°), find the vertex with
///      the maximum projection onto that direction. That vertex is the
///      hull point for that direction.
///   6. The hull point gets pulled inward by <see cref="HullInset"/>
///      (default 0.97 = 3 % shrink) so the LED sits clearly ON the
///      structure rather than on the outermost vertex.
///   7. Connect each pair of consecutive hull points with a thin emissive
///      strip primitive at Y = <see cref="ContourY"/> = 0.08.
///
/// IDEMPOTENCY / CLEAN-UP
///   • Deletes every child whose name starts with "LedStrip_" (the old
///     square-border tool) AND "LedContour_" (the previous run of this
///     tool) before creating fresh ones.
///   • Re-uses the same M_AirfieldLED.mat the previous LED tool created.
///     Creates it inline if missing — same shader / emission setup as the
///     other tool, so no duplicate materials.
///   • Re-wires the existing TeamColorMarker on the root to paint the new
///     segment renderers via MPB emission. Same pipeline as before; team
///     colour flows through MultiplayerColors / PlayerFactionManager.
///
/// WHAT IS NOT TOUCHED
///   • Airfield.cs, AirUnitController.cs, BuildingPlacementManager.cs,
///     ConstructionSite.cs — no aircraft / production / network logic.
///   • Slot_*, Taxi_*, runway / lane / landing markers, the BoxCollider,
///     the FBX visual itself, root scale, the team-colour scripts.
///
/// Menu: Tools → RTS → Buildings → Add Airfield LED Contour
/// </summary>
public static class AddAirfieldLedContour
{
    private const string PrefabPath        = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";
    private const string MaterialFolder    = "Assets/_Game/Materials/Airfield";
    private const string MaterialPath      = "Assets/_Game/Materials/Airfield/M_AirfieldLED.mat";
    private const string VisualChildName   = "Visual_NewAirfield";
    private const string BuildingLayerName = "Building";

    // ───── Tuning ──────────────────────────────────────────────────── //
    /// <summary>How many segments make up the ring (12 = 30° spacing).</summary>
    public const int   SegmentCount      = 12;
    /// <summary>Strip Y in airfield-local space (above the base, below the slot Y).</summary>
    public const float ContourY          = 0.08f;
    /// <summary>Inset factor on each hull point (0.95 = 5 % toward centre).</summary>
    public const float HullInset         = 0.97f;
    /// <summary>Only sample base-level vertices (airfield-local Y ≤ this).</summary>
    public const float BaseSampleMaxY    = 2.0f;
    /// <summary>Max vertices per mesh to sample (caps cost for the LOD0 mesh).</summary>
    public const int   MaxSamplesPerMesh = 2000;
    /// <summary>Cross-section width of the strip (perpendicular to its length).</summary>
    public const float StripThickness    = 0.15f;
    /// <summary>Strip vertical height.</summary>
    public const float StripHeight       = 0.1f;
    /// <summary>Skip segments shorter than this — usually a duplicate-hull-point artefact.</summary>
    public const float MinSegmentLength  = 0.05f;
    /// <summary>Glow strength (same convention as the old square-border tool).</summary>
    public const float EmissionIntensity = 2.0f;
    // ──────────────────────────────────────────────────────────────── //

    [MenuItem("Tools/RTS/Buildings/Add Airfield LED Contour")]
    public static void Add()
    {
        Debug.Log("[AddAirfieldLEDContour] ─── Building hull-following LED contour ───");

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
        {
            Debug.LogError($"[AddAirfieldLEDContour] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        Material ledMat = LoadOrCreateLedMaterial();
        if (ledMat == null) return;

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[AddAirfieldLEDContour] ✗ LoadPrefabContents returned null.");
            return;
        }

        int buildingLayer = LayerMask.NameToLayer(BuildingLayerName);
        if (buildingLayer < 0) buildingLayer = root.layer;

        try
        {
            // 1. Find Visual_NewAirfield (the FBX prefab variant child).
            Transform vis = root.transform.Find(VisualChildName);
            if (vis == null)
            {
                Debug.LogError($"[AddAirfieldLEDContour] ✗ '{VisualChildName}' child missing. " +
                               "Run Tools → RTS → Buildings → Use New Airfield Visual first.");
                return;
            }

            // 2. Sample base-level vertices in airfield-local space.
            List<Vector3> basePoints = SampleBaseVertices(vis);
            if (basePoints.Count < SegmentCount)
            {
                Debug.LogError($"[AddAirfieldLEDContour] ✗ Found only {basePoints.Count} base-level " +
                               $"vertices (need ≥ {SegmentCount}). Check the FBX's pivot and base height — " +
                               $"only vertices with airfield-local Y ≤ {BaseSampleMaxY} are considered.");
                return;
            }
            Debug.Log($"[AddAirfieldLEDContour]   Sampled {basePoints.Count} base vertices from " +
                      $"{VisualChildName} (Y ≤ {BaseSampleMaxY}).");

            // 3. Polar-sweep hull.
            Vector3[] hull = ComputePolarHull(basePoints, SegmentCount, HullInset);
            for (int i = 0; i < SegmentCount; i++)
            {
                Debug.Log($"[AddAirfieldLEDContour]   Hull[{i:D2}] " +
                          $"(angle {i * (360f / SegmentCount):F0}°) = " +
                          $"({hull[i].x:F2}, {hull[i].z:F2}).");
            }

            // 4. Delete old strip / contour children (idempotent).
            int deleted = 0;
            deleted += DeleteChildrenByPrefix(root, "LedStrip_");
            deleted += DeleteChildrenByPrefix(root, "LedContour_");
            if (deleted > 0)
                Debug.Log($"[AddAirfieldLEDContour]   Deleted {deleted} old strip/contour child(ren).");

            // 5. Build segments between consecutive hull points.
            List<Renderer> segmentRenderers = new List<Renderer>(SegmentCount);
            int segmentsCreated = 0;
            for (int i = 0; i < SegmentCount; i++)
            {
                Vector3 a = hull[i];
                Vector3 b = hull[(i + 1) % SegmentCount];
                a.y = ContourY;
                b.y = ContourY;

                MeshRenderer mr = CreateSegment(
                    name: $"LedContour_{i:D2}",
                    parent: root.transform,
                    a: a, b: b,
                    material: ledMat,
                    layer: buildingLayer);
                if (mr != null)
                {
                    segmentRenderers.Add(mr);
                    segmentsCreated++;
                }
            }

            // 6. Re-wire TeamColorMarker.
            WireTeamColorMarker(root, segmentRenderers);

            // 7. Save.
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AddAirfieldLEDContour] ✓ Done. {segmentsCreated}/{SegmentCount} contour segments " +
                      $"placed at Y={ContourY} (3 % inset from outermost hull vertex). " +
                      "Team colour applies via existing TeamColorMarker (MPB emission, multiplayer-safe).");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[AddAirfieldLEDContour] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // Vertex sampling
    // ================================================================== //

    private static List<Vector3> SampleBaseVertices(Transform visual)
    {
        var result = new List<Vector3>(4096);
        var mfs = visual.GetComponentsInChildren<MeshFilter>(includeInactive: false);
        for (int m = 0; m < mfs.Length; m++)
        {
            MeshFilter mf = mfs[m];
            if (mf == null || mf.sharedMesh == null) continue;
            Mesh mesh = mf.sharedMesh;
            Vector3[] verts;
            try { verts = mesh.vertices; }
            catch { continue; }   // very rare — mesh not readable
            if (verts == null || verts.Length == 0) continue;

            int step = Mathf.Max(1, verts.Length / MaxSamplesPerMesh);
            Matrix4x4 l2w = mf.transform.localToWorldMatrix;
            for (int i = 0; i < verts.Length; i += step)
            {
                Vector3 p = l2w.MultiplyPoint3x4(verts[i]);
                if (p.y >= 0f && p.y <= BaseSampleMaxY) result.Add(p);
            }
        }
        return result;
    }

    /// <summary>
    /// For each of <paramref name="segments"/> evenly-spaced angles around
    /// the Y axis, find the vertex with the largest projection onto that
    /// direction. The result is a polar approximation of the convex hull
    /// of <paramref name="points"/> projected onto the XZ plane.
    /// </summary>
    private static Vector3[] ComputePolarHull(List<Vector3> points, int segments, float inset)
    {
        var hull   = new Vector3[segments];
        var maxDot = new float[segments];
        var dirs   = new Vector3[segments];

        for (int s = 0; s < segments; s++)
        {
            float a = (s / (float)segments) * Mathf.PI * 2f;
            dirs[s] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            maxDot[s] = float.NegativeInfinity;
        }

        for (int p = 0; p < points.Count; p++)
        {
            Vector3 v = points[p];
            for (int s = 0; s < segments; s++)
            {
                float d = v.x * dirs[s].x + v.z * dirs[s].z;
                if (d > maxDot[s])
                {
                    maxDot[s] = d;
                    hull[s] = v;
                }
            }
        }

        // Inset toward (0, _, 0) on the XZ plane — keep Y as-sampled (we
        // override to ContourY when building segments anyway).
        for (int s = 0; s < segments; s++)
        {
            hull[s].x *= inset;
            hull[s].z *= inset;
        }

        return hull;
    }

    // ================================================================== //
    // GameObject construction
    // ================================================================== //

    private static int DeleteChildrenByPrefix(GameObject root, string prefix)
    {
        int n = 0;
        for (int i = root.transform.childCount - 1; i >= 0; i--)
        {
            Transform c = root.transform.GetChild(i);
            if (c.name.StartsWith(prefix))
            {
                Object.DestroyImmediate(c.gameObject);
                n++;
            }
        }
        return n;
    }

    private static MeshRenderer CreateSegment(string name, Transform parent,
                                              Vector3 a, Vector3 b,
                                              Material material, int layer)
    {
        Vector3 dir   = b - a;
        float   len   = dir.magnitude;
        if (len < MinSegmentLength)
        {
            Debug.LogWarning($"[AddAirfieldLEDContour]   '{name}' segment too short ({len:F3} m) — skipping.");
            return null;
        }

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;

        Collider col = go.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col, true);

        go.transform.SetParent(parent, worldPositionStays: false);
        go.transform.localPosition = (a + b) * 0.5f;
        go.transform.localRotation = Quaternion.LookRotation(dir / len, Vector3.up);
        go.transform.localScale    = new Vector3(StripThickness, StripHeight, len);
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
        return mr;
    }

    // ================================================================== //
    // TeamColorMarker wiring
    // ================================================================== //

    private static void WireTeamColorMarker(GameObject root, List<Renderer> rends)
    {
        TeamColorMarker marker = root.GetComponent<TeamColorMarker>();
        if (marker == null)
        {
            marker = root.AddComponent<TeamColorMarker>();
            Debug.Log("[AddAirfieldLEDContour]   AirfieldPrefab had no TeamColorMarker — added one.");
        }
        marker.bodyColorRenderers.Clear();
        for (int i = 0; i < rends.Count; i++) marker.bodyColorRenderers.Add(rends[i]);
        marker.applyToBaseColor  = false;
        marker.applyToEmission   = true;
        marker.emissionIntensity = EmissionIntensity;
        EditorUtility.SetDirty(marker);

        Debug.Log($"[AddAirfieldLEDContour]   TeamColorMarker.bodyColorRenderers = {rends.Count} contour " +
                  $"segment(s). applyToBaseColor=false, applyToEmission=true, " +
                  $"emissionIntensity={EmissionIntensity}.");
    }

    // ================================================================== //
    // Material
    // ================================================================== //

    private static Material LoadOrCreateLedMaterial()
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (existing != null) return existing;

        if (!AssetDatabase.IsValidFolder(MaterialFolder))
        {
            string parent = "Assets/_Game/Materials";
            if (!AssetDatabase.IsValidFolder(parent))
                AssetDatabase.CreateFolder("Assets/_Game", "Materials");
            AssetDatabase.CreateFolder(parent, "Airfield");
        }

        bool urp = GraphicsSettings.currentRenderPipeline != null
                && GraphicsSettings.currentRenderPipeline.GetType().Name.Contains("Universal");
        Shader sh = Shader.Find(urp ? "Universal Render Pipeline/Lit" : "Standard");
        if (sh == null) sh = Shader.Find("Standard");
        Material m = new Material(sh) { name = "M_AirfieldLED" };

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

        AssetDatabase.CreateAsset(m, MaterialPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[AddAirfieldLEDContour]   Created material '{MaterialPath}'.");
        return m;
    }
}
