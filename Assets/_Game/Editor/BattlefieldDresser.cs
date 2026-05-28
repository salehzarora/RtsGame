using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// One-click battlefield polish pass for the EXISTING match map. Transforms the
/// flat prototype plane into a believable semi-arid border-conflict battlefield:
/// warm late-afternoon lighting + atmosphere, a procedurally-generated dirt /
/// sand / scorch ground texture, a road network, point-symmetric landmarks
/// (a capturable central watch-tower outpost, mirrored supply depots, radar
/// stations, and a flank tunnel pair — reusing the Interactive Map system),
/// cover lines, and battlefield debris.
///
/// Menu: Tools → RTS → Map → Dress Battlefield (Polish Pass)
///
/// Design guarantees:
///   • FAIR — everything except the dead-centre outpost is placed in
///     point-symmetric pairs (rotate 180° about the origin maps Player base ↔
///     Enemy base), so neither side is favoured.
///   • READABLE — props use a warm neutral palette (tan / rust / concrete) that
///     never competes with the blue/red team colors; the main lane and both
///     bases are kept clear; decorative props have their colliders stripped.
///   • PATHFINDING-SAFE — only intentional blockers (barriers, tank traps,
///     landmark buildings) get a carving NavMeshObstacle so agents route around
///     them WITHOUT a NavMesh rebake. Nothing else touches navigation.
///   • NON-DESTRUCTIVE — never touches bases, command centres, resource nodes,
///     units, managers, camera, or UI. All dressing lives under a single
///     "Battlefield" root that is wiped + rebuilt on every run (idempotent).
///
/// Run AFTER your map is set up (Setup Clean Match Map / Setup Multiplayer
/// Match Map). Safe to re-run anytime.
/// </summary>
public static class BattlefieldDresser
{
    // ------------------------------------------------------------------ //
    // Layout constants — mirror the existing match map (see SetupCleanMatchMap)
    // ------------------------------------------------------------------ //

    private const float MapSize       = 240f;
    private const float PlayableRadius = 104f;   // inside the mountain border ring

    private static readonly Vector3 PlayerBase = new Vector3(-80f, 0f, -70f);
    private static readonly Vector3 EnemyBase  = new Vector3( 80f, 0f,  70f);

    private const string DressingRootName = "Battlefield";
    private const string GroundTexturePath = "Assets/_Game/Art/Generated/BattlefieldGround.png";
    private const string SoftParticlePath  = "Assets/_Game/Art/Generated/SoftDust.png";

    // ------------------------------------------------------------------ //
    // Shared palette (battlefield neutral — never blue/red so teams read clearly)
    // ------------------------------------------------------------------ //

    private static readonly Color RoadColor     = new Color(0.34f, 0.30f, 0.24f);
    private static readonly Color MarkingColor  = new Color(0.78f, 0.74f, 0.60f);
    private static readonly Color ConcreteColor = new Color(0.60f, 0.58f, 0.53f);
    private static readonly Color SandbagColor  = new Color(0.68f, 0.58f, 0.40f);
    private static readonly Color WoodColor     = new Color(0.43f, 0.31f, 0.17f);
    private static readonly Color RustColor     = new Color(0.43f, 0.27f, 0.18f);
    private static readonly Color MetalColor    = new Color(0.27f, 0.27f, 0.29f);
    private static readonly Color ScorchColor   = new Color(0.10f, 0.09f, 0.08f);
    private static readonly Color DeadWoodColor = new Color(0.34f, 0.28f, 0.19f);
    private static readonly Color TarpColor     = new Color(0.40f, 0.42f, 0.34f);

    // Shared materials — created once per run, reused across hundreds of props
    // so static batching can fold them and draw calls stay low.
    private static Material roadMat, markMat, concreteMat, sandbagMat, woodMat,
                            rustMat, metalMat, scorchMat, deadWoodMat, tarpMat;

    private const int IgnoreRaycastLayer = 2;

    private static System.Random rng;

    // ================================================================== //
    // Entry point
    // ================================================================== //

    [MenuItem("Tools/RTS/Map/Dress Battlefield (Polish Pass)")]
    public static void Dress()
    {
        Debug.Log("[Battlefield] ── Dressing the map into a battlefield ──");
        rng = new System.Random(20260601);

        BuildSharedMaterials();

        // 1. Lighting + atmosphere (global render settings + directional light).
        SetupLightingAndAtmosphere();

        // 2. Ground reskin (generate + assign a battlefield ground texture).
        ReskinGround();

        // 3. Build all dressing under a single, wiped-and-rebuilt root.
        GameObject root = GameObject.Find(DressingRootName);
        if (root != null) Undo.DestroyObjectImmediate(root);
        root = new GameObject(DressingRootName);
        Undo.RegisterCreatedObjectUndo(root, "Create Battlefield");

        BuildRoads(root.transform);
        BuildCentralOutpost(root.transform);
        BuildSupplyDepots(root.transform);
        BuildRadarStations(root.transform);
        BuildFlankTunnels(root.transform);
        BuildCoverLines(root.transform);
        ScatterDebris(root.transform);

        // 4. Multiplayer: hide with the rest of the world until match start, and
        //    make sure occupancy/destruction events have a receiver.
        RegisterWithGameplayWorldRoot(root);
        EnsureMapNetworkEvents();

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[Battlefield] ── Done. Save the scene (Ctrl+S). No NavMesh rebake needed " +
                  "(blockers use carving NavMeshObstacles). ──");
    }

    // ================================================================== //
    // 1. Lighting + atmosphere
    // ================================================================== //

    private static void SetupLightingAndAtmosphere()
    {
        // --- Directional light: warm, low late-afternoon sun --------------
        Light sun = FindMainDirectionalLight();
        if (sun == null)
        {
            GameObject go = new GameObject("Directional Light");
            Undo.RegisterCreatedObjectUndo(go, "Create Directional Light");
            sun = go.AddComponent<Light>();
            sun.type = LightType.Directional;
        }

        Undo.RecordObject(sun, "Battlefield Light");
        Undo.RecordObject(sun.transform, "Battlefield Light Xform");
        sun.transform.rotation = Quaternion.Euler(38f, 35f, 0f);   // low warm sun from the NE
        sun.color     = new Color(1.0f, 0.92f, 0.78f);
        sun.intensity = 1.35f;
        sun.shadows   = LightShadows.Soft;
        sun.shadowStrength = 0.7f;
        EditorUtility.SetDirty(sun);

        // --- Ambient: warm tri-light so shadows aren't dead black ----------
        RenderSettings.ambientMode      = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor    = new Color(0.52f, 0.58f, 0.66f);
        RenderSettings.ambientEquatorColor = new Color(0.55f, 0.50f, 0.42f);
        RenderSettings.ambientGroundColor  = new Color(0.30f, 0.27f, 0.22f);
        RenderSettings.ambientIntensity    = 1.0f;

        // --- Fog: distant warm dust haze only (gameplay zone stays crisp) --
        RenderSettings.fog        = true;
        RenderSettings.fogMode    = FogMode.Linear;
        RenderSettings.fogColor   = new Color(0.80f, 0.74f, 0.62f);
        RenderSettings.fogStartDistance = 110f;
        RenderSettings.fogEndDistance   = 340f;

        // --- Procedural skybox: warm hazy sky ------------------------------
        Shader skyShader = Shader.Find("Skybox/Procedural");
        if (skyShader != null)
        {
            Material sky = new Material(skyShader) { name = "BattlefieldSky" };
            if (sky.HasProperty("_SkyTint"))           sky.SetColor("_SkyTint", new Color(0.74f, 0.72f, 0.66f));
            if (sky.HasProperty("_GroundColor"))       sky.SetColor("_GroundColor", new Color(0.45f, 0.40f, 0.33f));
            if (sky.HasProperty("_AtmosphereThickness")) sky.SetFloat("_AtmosphereThickness", 1.35f);
            if (sky.HasProperty("_Exposure"))          sky.SetFloat("_Exposure", 1.15f);
            if (sky.HasProperty("_SunSize"))           sky.SetFloat("_SunSize", 0.045f);
            RenderSettings.skybox = sky;
        }
        RenderSettings.sun = sun;
        DynamicGI.UpdateEnvironment();

        Debug.Log("[Battlefield]   Lighting set: warm late-afternoon sun, tri-light ambient, distant dust fog.");
    }

    private static Light FindMainDirectionalLight()
    {
        Light best = null;
        foreach (Light l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (l != null && l.type == LightType.Directional)
                if (best == null || l.intensity > best.intensity) best = l;
        return best;
    }

    // ================================================================== //
    // 2. Ground reskin
    // ================================================================== //

    private static void ReskinGround()
    {
        Renderer ground = FindGroundRenderer();
        if (ground == null)
        {
            Debug.LogWarning("[Battlefield]   No ground plane found on the 'Ground' layer — " +
                             "skipping ground reskin. Run Setup Clean Match Map first.");
            return;
        }

        Texture2D tex = null;
        try { tex = GenerateGroundTexture(GroundTexturePath, 1024); }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Battlefield]   Ground texture generation failed ({ex.Message}); " +
                             "falling back to a tinted dirt material.");
        }

        Material mat = new Material(ResolveLitShader()) { name = "BattlefieldGround" };
        Color baseTint = new Color(0.55f, 0.50f, 0.37f);
        mat.color = baseTint;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseTint);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.05f);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.05f);
        if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic", 0f);

        if (tex != null)
        {
            Vector2 tiling = new Vector2(14f, 14f);
            if (mat.HasProperty("_BaseMap")) { mat.SetTexture("_BaseMap", tex); mat.SetTextureScale("_BaseMap", tiling); }
            if (mat.HasProperty("_MainTex")) { mat.SetTexture("_MainTex", tex); mat.SetTextureScale("_MainTex", tiling); }
            mat.color = Color.white;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        }

        Undo.RecordObject(ground, "Reskin Ground");
        ground.sharedMaterial = mat;
        EditorUtility.SetDirty(ground);
        Debug.Log("[Battlefield]   Ground reskinned with a generated dirt/sand/scorch texture.");
    }

    private static Renderer FindGroundRenderer()
    {
        int groundLayer = LayerMask.NameToLayer("Ground");
        Renderer best = null;
        float bestArea = 0f;
        foreach (MeshRenderer r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (r == null) continue;
            if (groundLayer >= 0 && r.gameObject.layer != groundLayer) continue;
            Vector3 s = r.bounds.size;
            float area = s.x * s.z;
            if (area > bestArea) { bestArea = area; best = r; }
        }
        return best;
    }

    /// <summary>
    /// Builds a tiling battlefield ground texture from layered Perlin noise:
    /// dry-grass / dirt / sand blend with darker worn tracks and scorch marks.
    /// Saved as a repeating, mip-mapped PNG asset.
    /// </summary>
    private static Texture2D GenerateGroundTexture(string assetPath, int size)
    {
        Color dryGrass = new Color(0.46f, 0.46f, 0.28f);
        Color dirt     = new Color(0.49f, 0.41f, 0.28f);
        Color sand     = new Color(0.66f, 0.58f, 0.40f);
        Color darkDirt = new Color(0.33f, 0.28f, 0.20f);
        Color scorch   = new Color(0.13f, 0.11f, 0.09f);

        Texture2D tex = new Texture2D(size, size, TextureFormat.RGB24, true);
        float inv = 1f / size;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = x * inv, v = y * inv;

                // Macro patches (which surface dominates here).
                float macro = Mathf.PerlinNoise(u * 3f + 11f, v * 3f + 7f);
                // Mid detail.
                float mid   = Mathf.PerlinNoise(u * 9f + 21f, v * 9f + 3f);
                // Fine grain.
                float fine  = Mathf.PerlinNoise(u * 34f, v * 34f);

                Color c;
                if (macro < 0.40f)        c = Color.Lerp(dirt, dryGrass, mid);
                else if (macro < 0.72f)   c = Color.Lerp(dirt, sand, mid);
                else                      c = Color.Lerp(sand, darkDirt, mid * 0.6f);

                // Fine grain modulation.
                c = Color.Lerp(c, c * 0.82f, fine * 0.5f);

                // Worn tracks — low, stretched noise reads as compacted earth.
                float track = Mathf.PerlinNoise(u * 5f + 50f, v * 1.5f + 90f);
                if (track > 0.78f) c = Color.Lerp(c, darkDirt, 0.6f);

                // Scorch blotches — rare dark burn marks.
                float burn = Mathf.PerlinNoise(u * 7f + 130f, v * 7f + 170f);
                if (burn > 0.86f) c = Color.Lerp(c, scorch, (burn - 0.86f) / 0.14f);

                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply();

        string dir = Path.GetDirectoryName(assetPath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(assetPath, tex.EncodeToPNG());
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

        if (AssetImporter.GetAtPath(assetPath) is TextureImporter ti)
        {
            ti.wrapMode       = TextureWrapMode.Repeat;
            ti.maxTextureSize = 1024;
            ti.mipmapEnabled  = true;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    // ================================================================== //
    // 3a. Roads
    // ================================================================== //

    private static void BuildRoads(Transform root)
    {
        Transform roads = MakeChild(root, "Roads").transform;

        // Main supply road: base → centre → base along the diagonal.
        BuildRoadSegment(roads, PlayerBase, EnemyBase, 9f);

        // Cross road through the centre (perpendicular), to the two neutral
        // corners — gives flanking routes and breaks up the open plain.
        Vector3 perp = Vector3.Cross(Vector3.up, (EnemyBase - PlayerBase).normalized) * 78f;
        BuildRoadSegment(roads, -perp, perp, 7f);

        Debug.Log("[Battlefield]   Road network laid (main diagonal + cross road).");
    }

    private static void BuildRoadSegment(Transform parent, Vector3 a, Vector3 b, float width)
    {
        a.y = 0f; b.y = 0f;
        Vector3 mid = (a + b) * 0.5f;
        float len = Vector3.Distance(a, b);
        Vector3 dir = (b - a).normalized;

        GameObject road = GameObject.CreatePrimitive(PrimitiveType.Cube);
        road.name = "Road";
        StripCollider(road);
        road.transform.SetParent(parent, false);
        road.transform.position = mid + Vector3.up * 0.03f;
        road.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        road.transform.localScale = new Vector3(width, 0.06f, len);
        SetMat(road, roadMat);
        MarkDecorStatic(road);

        // Faint centre line.
        GameObject line = GameObject.CreatePrimitive(PrimitiveType.Cube);
        line.name = "RoadLine";
        StripCollider(line);
        line.transform.SetParent(parent, false);
        line.transform.position = mid + Vector3.up * 0.05f;
        line.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        line.transform.localScale = new Vector3(0.35f, 0.04f, len);
        SetMat(line, markMat);
        MarkDecorStatic(line);
    }

    // ================================================================== //
    // 3b. Central outpost — capturable watch tower + cover + hazards
    // ================================================================== //

    private static void BuildCentralOutpost(Transform root)
    {
        Transform o = MakeChild(root, "CentralOutpost").transform;

        // Capturable watch tower at dead centre — the marquee landmark.
        GameObject tower = BuildWatchTower(o, new Vector3(0f, 0f, 0f));
        AddCarveObstacle(tower, new Vector3(3f, 6f, 3f), new Vector3(0f, 3f, 0f));

        // A broken outer sandbag ring with gaps so units can fight inside.
        int seg = 12;
        for (int i = 0; i < seg; i++)
        {
            if (i % 3 == 0) continue;     // gaps for entry / readability
            float ang = i * Mathf.PI * 2f / seg;
            Vector3 p = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * 9f;
            BuildSandbagWall(o, p, Mathf.Atan2(p.x, p.z) * Mathf.Rad2Deg, carve: false);
        }

        // Concrete barriers at the four cardinal approaches (real cover).
        BuildConcreteBarrier(o, new Vector3( 12f, 0f, 0f), 90f, carve: true);
        BuildConcreteBarrier(o, new Vector3(-12f, 0f, 0f), 90f, carve: true);
        BuildConcreteBarrier(o, new Vector3(0f, 0f,  12f), 0f,  carve: true);
        BuildConcreteBarrier(o, new Vector3(0f, 0f, -12f), 0f,  carve: true);

        // Two explosive fuel barrels + a wrecked vehicle = environmental story.
        BuildFuelBarrel(o, new Vector3(4.5f, 0f, 5f));
        BuildFuelBarrel(o, new Vector3(-5f, 0f, -4f));
        BuildWreckedVehicle(o, new Vector3(6f, 0f, -6f), 40f);

        // Debris + scorch decals around the contested core.
        for (int i = 0; i < 6; i++)
        {
            float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
            float rad = 6f + (float)rng.NextDouble() * 7f;
            Vector3 p = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * rad;
            BuildDebris(o, p);
        }
        BuildScorchDecal(o, new Vector3(0f, 0f, 0f), 7f);

        // A wisp of dust/smoke for atmosphere.
        AddDust(o, new Vector3(6f, 0.5f, -6f), new Color(0.5f, 0.45f, 0.38f, 0.5f), 5f, 3f, 4f);

        Debug.Log("[Battlefield]   Central outpost built (capturable watch tower + cover + hazards).");
    }

    // ================================================================== //
    // 3c. Supply depots (mirrored pair)
    // ================================================================== //

    private static void BuildSupplyDepots(Transform root)
    {
        Transform d = MakeChild(root, "SupplyDepots").transform;

        // Midway between each base and the centre, pushed to the flank side so
        // the depot sits OFF the main lane.
        Vector3 perp = Vector3.Cross(Vector3.up, (EnemyBase - PlayerBase).normalized);
        Vector3 playerDepot = PlayerBase * 0.5f + perp * 20f;
        playerDepot.y = 0f;

        BuildDepotCluster(d, playerDepot);
        BuildDepotCluster(d, Mirror(playerDepot));
        Debug.Log("[Battlefield]   Two mirrored supply depots built (garrison shed + fuel + crates).");
    }

    private static void BuildDepotCluster(Transform parent, Vector3 center)
    {
        Transform c = MakeChild(parent, "Depot").transform;
        c.position = center;

        // Garrisonable shed.
        GameObject shed = BuildGarrison(c, center);
        AddCarveObstacle(shed, new Vector3(5f, 5f, 5f), new Vector3(0f, 2.5f, 0f));

        // Fuel tanks (explosive) + crates + sandbags around it.
        BuildFuelTankLarge(c, center + new Vector3(6f, 0f, 2f));
        BuildFuelBarrel(c, center + new Vector3(5f, 0f, -3f));
        BuildCrateStack(c, center + new Vector3(-5f, 0f, 3f));
        BuildCrateStack(c, center + new Vector3(-4f, 0f, -4f));
        BuildSandbagWall(c, center + new Vector3(0f, 0f, 6f), 0f, carve: true);
        BuildSandbagWall(c, center + new Vector3(-6f, 0f, 0f), 90f, carve: true);
        BuildDebris(c, center + new Vector3(7f, 0f, 5f));
    }

    // ================================================================== //
    // 3d. Radar stations (mirrored pair) — decorative identity landmarks
    // ================================================================== //

    private static void BuildRadarStations(Transform root)
    {
        Transform r = MakeChild(root, "RadarStations").transform;
        Vector3 a = new Vector3(-68f, 0f, 66f);     // NW neutral corner
        BuildRadar(r, a);
        BuildRadar(r, Mirror(a));
        Debug.Log("[Battlefield]   Two mirrored radar stations built.");
    }

    private static void BuildRadar(Transform parent, Vector3 pos)
    {
        Transform c = MakeChild(parent, "Radar").transform;
        c.position = pos;

        // Concrete base.
        GameObject baseBlock = Prop(c, "RadarBase", PrimitiveType.Cube, pos + new Vector3(0f, 0.75f, 0f),
            new Vector3(4f, 1.5f, 4f), Quaternion.identity, concreteMat, keepCollider: true);
        AddCarveObstacle(baseBlock, new Vector3(4f, 1.5f, 4f), Vector3.zero);

        // Mast.
        Prop(c, "Mast", PrimitiveType.Cylinder, pos + new Vector3(0f, 5f, 0f),
            new Vector3(0.4f, 3.5f, 0.4f), Quaternion.identity, metalMat, keepCollider: false);

        // Dish (tilted cylinder).
        Prop(c, "Dish", PrimitiveType.Cylinder, pos + new Vector3(0f, 8.5f, 0f),
            new Vector3(3.5f, 0.2f, 3.5f), Quaternion.Euler(55f, 30f, 0f), metalMat, keepCollider: false);

        // A couple of sandbags + a barrel for life.
        BuildSandbagWall(c, pos + new Vector3(3.5f, 0f, 0f), 90f, carve: false);
        BuildFuelBarrel(c, pos + new Vector3(-3f, 0f, 2f));
    }

    // ================================================================== //
    // 3e. Flank tunnels (one linked pair)
    // ================================================================== //

    private static void BuildFlankTunnels(Transform root)
    {
        Transform t = MakeChild(root, "FlankTunnels").transform;
        Vector3 aPos = new Vector3(-92f, 0f, -8f);
        Vector3 bPos = Mirror(aPos);

        TunnelEntrance a = BuildTunnel(t, aPos);
        TunnelEntrance b = BuildTunnel(t, bPos);
        a.linkedTunnel = b;
        b.linkedTunnel = a;
        EditorUtility.SetDirty(a);
        EditorUtility.SetDirty(b);
        Debug.Log("[Battlefield]   Flank tunnel pair built (infantry shortcut across the map).");
    }

    // ================================================================== //
    // 3f. Cover lines (mirrored) — tank traps + barriers flanking the lane
    // ================================================================== //

    private static void BuildCoverLines(Transform root)
    {
        Transform c = MakeChild(root, "CoverLines").transform;

        // A defensive line on each player's approach side of the centre,
        // offset to the flank so it suggests a frontline without walling the lane.
        Vector3[] line =
        {
            new Vector3(-34f, 0f, -14f),
            new Vector3(-30f, 0f, -22f),
            new Vector3(-26f, 0f, -30f),
        };
        foreach (Vector3 p in line)
        {
            BuildTankTrap(c, p);
            BuildTankTrap(c, Mirror(p));
        }

        Vector3[] barriers =
        {
            new Vector3(-22f, 0f, -8f),
            new Vector3(-8f, 0f, -22f),
        };
        foreach (Vector3 p in barriers)
        {
            BuildConcreteBarrier(c, p, 30f, carve: true);
            BuildConcreteBarrier(c, Mirror(p), 30f, carve: true);
        }
        Debug.Log("[Battlefield]   Mirrored cover lines built (tank traps + barriers).");
    }

    // ================================================================== //
    // 3g. Scattered debris / fences / dead trees / wrecks (mirrored)
    // ================================================================== //

    private static void ScatterDebris(Transform root)
    {
        Transform s = MakeChild(root, "Scatter").transform;
        Vector3 laneDir = (EnemyBase - PlayerBase).normalized;

        int pairs = 46, placed = 0, tries = 0;
        while (placed < pairs && tries < pairs * 30)
        {
            tries++;
            float x = (float)(rng.NextDouble() * 2 - 1) * PlayableRadius;
            float z = (float)(rng.NextDouble() * 2 - 1) * PlayableRadius;
            Vector3 p = new Vector3(x, 0f, z);

            if (new Vector2(x, z).magnitude > PlayableRadius) continue;
            if (new Vector2(x, z).magnitude < 16f) continue;                 // keep centre core readable
            if (DistXZ(p, PlayerBase) < 30f || DistXZ(p, EnemyBase) < 30f) continue;  // clear of bases
            if (DistanceToLane(p, PlayerBase, laneDir) < 11f) continue;      // keep the main lane open

            int kind = rng.Next(0, 6);
            switch (kind)
            {
                case 0: BuildDebris(s, p); break;
                case 1: BuildDeadTree(s, p); break;
                case 2: BuildFence(s, p, (float)rng.NextDouble() * 180f); break;
                case 3: BuildCrateStack(s, p); break;
                case 4: BuildBarrelCluster(s, p); break;
                default: BuildSandbagWall(s, p, (float)rng.NextDouble() * 180f, carve: false); break;
            }
            // Mirror for fairness.
            Vector3 m = Mirror(p);
            switch (kind)
            {
                case 0: BuildDebris(s, m); break;
                case 1: BuildDeadTree(s, m); break;
                case 2: BuildFence(s, m, (float)rng.NextDouble() * 180f); break;
                case 3: BuildCrateStack(s, m); break;
                case 4: BuildBarrelCluster(s, m); break;
                default: BuildSandbagWall(s, m, (float)rng.NextDouble() * 180f, carve: false); break;
            }
            placed++;
        }
        Debug.Log($"[Battlefield]   Scattered {placed * 2} mirrored detail props (debris, dead trees, fences, crates, barrels).");
    }

    // ================================================================== //
    // Prop builders
    // ================================================================== //

    private static void BuildSandbagWall(Transform parent, Vector3 pos, float yaw, bool carve)
    {
        GameObject wall = new GameObject("SandbagWall");
        wall.transform.SetParent(parent, false);
        wall.transform.position = pos;
        wall.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        int bags = 6;
        for (int i = 0; i < bags; i++)
        {
            float bx = (i - (bags - 1) * 0.5f) * 0.7f;
            // Two stacked rows.
            for (int row = 0; row < 2; row++)
            {
                GameObject bag = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                StripCollider(bag);
                bag.name = "Bag";
                bag.transform.SetParent(wall.transform, false);
                bag.transform.localPosition = new Vector3(bx + (row * 0.3f), 0.35f + row * 0.55f, 0f);
                bag.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                bag.transform.localScale    = new Vector3(0.55f, 0.42f, 0.55f);
                SetMat(bag, sandbagMat);
            }
        }
        MarkDecorStatic(wall, recursive: true);

        if (carve)
        {
            BoxCollider col = wall.AddComponent<BoxCollider>();
            col.size = new Vector3(bags * 0.7f + 0.4f, 1.2f, 1f);
            col.center = new Vector3(0f, 0.6f, 0f);
            col.enabled = false;     // visual only; carving obstacle does the blocking
            AddCarveObstacle(wall, new Vector3(bags * 0.7f, 1.2f, 0.9f), new Vector3(0f, 0.6f, 0f));
        }
    }

    private static void BuildConcreteBarrier(Transform parent, Vector3 pos, float yaw, bool carve)
    {
        GameObject b = Prop(parent, "ConcreteBarrier", PrimitiveType.Cube, pos + new Vector3(0f, 0.6f, 0f),
            new Vector3(3.2f, 1.2f, 0.9f), Quaternion.Euler(0f, yaw, 0f), concreteMat, keepCollider: false);
        if (carve) AddCarveObstacle(b, new Vector3(3.2f, 1.2f, 0.9f), Vector3.zero);
    }

    private static void BuildTankTrap(Transform parent, Vector3 pos)
    {
        GameObject trap = new GameObject("TankTrap");
        trap.transform.SetParent(parent, false);
        trap.transform.position = pos;
        trap.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

        for (int i = 0; i < 3; i++)
        {
            GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            StripCollider(bar);
            bar.name = "Bar";
            bar.transform.SetParent(trap.transform, false);
            bar.transform.localPosition = new Vector3(0f, 1f, 0f);
            bar.transform.localScale    = new Vector3(0.35f, 3.2f, 0.35f);
            bar.transform.localRotation = Quaternion.Euler(i == 0 ? 45f : -45f, i * 60f, i == 2 ? 45f : -45f);
            SetMat(bar, metalMat);
        }
        MarkDecorStatic(trap, recursive: true);
        AddCarveObstacle(trap, new Vector3(2f, 1.5f, 2f), new Vector3(0f, 0.75f, 0f));
    }

    private static void BuildCrateStack(Transform parent, Vector3 pos)
    {
        GameObject stack = new GameObject("CrateStack");
        stack.transform.SetParent(parent, false);
        stack.transform.position = pos;
        stack.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 90f, 0f);

        int n = 2 + rng.Next(0, 3);
        for (int i = 0; i < n; i++)
        {
            float s = 1.0f + (float)rng.NextDouble() * 0.4f;
            GameObject crate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            StripCollider(crate);
            crate.name = "Crate";
            crate.transform.SetParent(stack.transform, false);
            crate.transform.localPosition = new Vector3(
                (float)(rng.NextDouble() - 0.5) * 0.6f, s * 0.5f + i * 0.9f, (float)(rng.NextDouble() - 0.5) * 0.6f);
            crate.transform.localScale    = Vector3.one * s;
            crate.transform.localRotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 20f, 0f);
            SetMat(crate, woodMat);
        }
        MarkDecorStatic(stack, recursive: true);
    }

    private static void BuildBarrelCluster(Transform parent, Vector3 pos)
    {
        GameObject cluster = new GameObject("BarrelCluster");
        cluster.transform.SetParent(parent, false);
        cluster.transform.position = pos;

        int n = 2 + rng.Next(0, 3);
        for (int i = 0; i < n; i++)
        {
            Vector3 off = new Vector3((float)(rng.NextDouble() - 0.5) * 1.6f, 0.7f, (float)(rng.NextDouble() - 0.5) * 1.6f);
            GameObject barrel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            StripCollider(barrel);
            barrel.name = "Barrel";
            barrel.transform.SetParent(cluster.transform, false);
            barrel.transform.localPosition = off;
            barrel.transform.localScale    = new Vector3(0.7f, 0.7f, 0.7f);
            SetMat(barrel, rng.Next(0, 2) == 0 ? rustMat : metalMat);
        }
        MarkDecorStatic(cluster, recursive: true);
    }

    private static void BuildDebris(Transform parent, Vector3 pos)
    {
        GameObject debris = new GameObject("Debris");
        debris.transform.SetParent(parent, false);
        debris.transform.position = pos;

        int n = 3 + rng.Next(0, 4);
        for (int i = 0; i < n; i++)
        {
            GameObject chunk = GameObject.CreatePrimitive(PrimitiveType.Cube);
            StripCollider(chunk);
            chunk.name = "Chunk";
            chunk.transform.SetParent(debris.transform, false);
            chunk.transform.localPosition = new Vector3(
                (float)(rng.NextDouble() - 0.5) * 2.4f, 0.12f, (float)(rng.NextDouble() - 0.5) * 2.4f);
            chunk.transform.localScale = new Vector3(
                0.3f + (float)rng.NextDouble() * 0.7f, 0.18f, 0.3f + (float)rng.NextDouble() * 0.7f);
            chunk.transform.localRotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
            SetMat(chunk, rng.Next(0, 2) == 0 ? concreteMat : rustMat);
        }
        MarkDecorStatic(debris, recursive: true);
    }

    private static void BuildScorchDecal(Transform parent, Vector3 pos, float size)
    {
        GameObject decal = GameObject.CreatePrimitive(PrimitiveType.Quad);
        StripCollider(decal);
        decal.name = "Scorch";
        decal.transform.SetParent(parent, false);
        decal.transform.position = pos + Vector3.up * 0.04f;
        decal.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        decal.transform.localScale = new Vector3(size, size, 1f);
        SetMat(decal, scorchMat);
        MarkDecorStatic(decal);
    }

    private static void BuildFence(Transform parent, Vector3 pos, float yaw)
    {
        GameObject fence = new GameObject("Fence");
        fence.transform.SetParent(parent, false);
        fence.transform.position = pos;
        fence.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        int posts = 4;
        for (int i = 0; i < posts; i++)
        {
            float fx = (i - (posts - 1) * 0.5f) * 1.6f;
            GameObject post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            StripCollider(post);
            post.name = "Post";
            post.transform.SetParent(fence.transform, false);
            post.transform.localPosition = new Vector3(fx, 0.7f, 0f);
            post.transform.localScale    = new Vector3(0.12f, 1.4f, 0.12f);
            SetMat(post, deadWoodMat);
        }
        // Two rails.
        for (int row = 0; row < 2; row++)
        {
            GameObject rail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            StripCollider(rail);
            rail.name = "Rail";
            rail.transform.SetParent(fence.transform, false);
            rail.transform.localPosition = new Vector3(0f, 0.5f + row * 0.6f, 0f);
            rail.transform.localScale    = new Vector3(posts * 1.6f, 0.1f, 0.08f);
            SetMat(rail, deadWoodMat);
        }
        MarkDecorStatic(fence, recursive: true);
    }

    private static void BuildDeadTree(Transform parent, Vector3 pos)
    {
        GameObject tree = new GameObject("DeadTree");
        tree.transform.SetParent(parent, false);
        tree.transform.position = pos;
        tree.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

        GameObject trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        StripCollider(trunk);
        trunk.name = "Trunk";
        trunk.transform.SetParent(tree.transform, false);
        trunk.transform.localPosition = new Vector3(0f, 1.6f, 0f);
        trunk.transform.localScale    = new Vector3(0.3f, 1.6f, 0.3f);
        trunk.transform.localRotation = Quaternion.Euler((float)(rng.NextDouble() - 0.5) * 10f, 0f, (float)(rng.NextDouble() - 0.5) * 10f);
        SetMat(trunk, deadWoodMat);

        int branches = 3;
        for (int i = 0; i < branches; i++)
        {
            GameObject br = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            StripCollider(br);
            br.name = "Branch";
            br.transform.SetParent(tree.transform, false);
            br.transform.localPosition = new Vector3(0f, 2.6f + i * 0.4f, 0f);
            br.transform.localScale    = new Vector3(0.12f, 0.8f, 0.12f);
            br.transform.localRotation = Quaternion.Euler(45f + i * 10f, i * 120f, 0f);
            SetMat(br, deadWoodMat);
        }
        MarkDecorStatic(tree, recursive: true);
    }

    private static void BuildWreckedVehicle(Transform parent, Vector3 pos, float yaw)
    {
        GameObject wreck = new GameObject("WreckedVehicle");
        wreck.transform.SetParent(parent, false);
        wreck.transform.position = pos;
        wreck.transform.rotation = Quaternion.Euler(0f, yaw, 6f);

        GameObject hull = GameObject.CreatePrimitive(PrimitiveType.Cube);
        StripCollider(hull);
        hull.name = "Hull";
        hull.transform.SetParent(wreck.transform, false);
        hull.transform.localPosition = new Vector3(0f, 0.7f, 0f);
        hull.transform.localScale    = new Vector3(2.6f, 1.0f, 4.5f);
        SetMat(hull, rustMat);

        GameObject turret = GameObject.CreatePrimitive(PrimitiveType.Cube);
        StripCollider(turret);
        turret.name = "Turret";
        turret.transform.SetParent(wreck.transform, false);
        turret.transform.localPosition = new Vector3(0.3f, 1.4f, -0.4f);
        turret.transform.localScale    = new Vector3(1.6f, 0.7f, 1.8f);
        turret.transform.localRotation = Quaternion.Euler(0f, 28f, 0f);
        SetMat(turret, rustMat);

        GameObject scorch = GameObject.CreatePrimitive(PrimitiveType.Quad);
        StripCollider(scorch);
        scorch.name = "Scorch";
        scorch.transform.SetParent(wreck.transform, false);
        scorch.transform.localPosition = new Vector3(0f, -0.65f, 0f);
        scorch.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        scorch.transform.localScale    = new Vector3(8f, 8f, 1f);
        SetMat(scorch, scorchMat);

        MarkDecorStatic(wreck, recursive: true);
        AddCarveObstacle(wreck, new Vector3(3f, 1.5f, 5f), new Vector3(0f, 0.7f, 0f));
    }

    // ================================================================== //
    // Interactive Map landmark builders (reuse the Map system components)
    // ================================================================== //

    private static GameObject BuildWatchTower(Transform parent, Vector3 pos)
    {
        GameObject root = NewMapEntity(parent, "WatchTower_Outpost", pos);

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size = new Vector3(2.5f, 8f, 2.5f); col.center = new Vector3(0f, 4f, 0f);

        GameObject alive = AddChildVisual(root, "AliveVisual", PrimitiveType.Cube,
            new Vector3(0f, 4f, 0f), new Vector3(2.5f, 8f, 2.5f), concreteMat, true);
        GameObject dead = AddChildVisual(root, "DestroyedVisual", PrimitiveType.Cube,
            new Vector3(0f, 1f, 0f), new Vector3(2.7f, 2f, 2.7f), scorchMat, false);
        GameObject flag = AddChildVisual(root, "OwnerFlag", PrimitiveType.Cube,
            new Vector3(0f, 8.4f, 0f), new Vector3(2.8f, 0.8f, 2.8f), metalMat, true);

        Transform[] exits = MakeExitRing(root.transform, 3, 3.5f);

        Health h = root.AddComponent<Health>(); h.maxHealth = 220f;

        DestructibleMapObject dmo = root.AddComponent<DestructibleMapObject>();
        dmo.maxHealth = 220f; dmo.persistAfterDestroyed = true; dmo.isTargetable = true;
        dmo.aliveVisual = alive; dmo.destroyedVisual = dead;
        dmo.disableCollidersOnDestroy = new Collider[] { col };

        WatchTower t = root.AddComponent<WatchTower>();
        t.capacity = 2; t.infantryOnly = true; t.canFireFromBuilding = true;
        t.fireRange = 26f; t.damagePerOccupant = 6f; t.exitPoints = exits;
        t.visionRadius = 30f; t.ownerIndicatorRenderer = flag.GetComponent<Renderer>();
        EditorUtility.SetDirty(t);
        return root;
    }

    private static GameObject BuildGarrison(Transform parent, Vector3 pos)
    {
        GameObject root = NewMapEntity(parent, "GarrisonShed", pos);

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size = new Vector3(5f, 4f, 5f); col.center = new Vector3(0f, 2f, 0f);

        GameObject alive = AddChildVisual(root, "AliveVisual", PrimitiveType.Cube,
            new Vector3(0f, 2f, 0f), new Vector3(5f, 4f, 5f), concreteMat, true);
        GameObject dead = AddChildVisual(root, "DestroyedVisual", PrimitiveType.Cube,
            new Vector3(0f, 0.8f, 0f), new Vector3(5f, 1.6f, 5f), scorchMat, false);

        Transform[] exits = MakeExitRing(root.transform, 4, 4f);

        Health h = root.AddComponent<Health>(); h.maxHealth = 300f;

        DestructibleMapObject dmo = root.AddComponent<DestructibleMapObject>();
        dmo.maxHealth = 300f; dmo.persistAfterDestroyed = true; dmo.isTargetable = true;
        dmo.aliveVisual = alive; dmo.destroyedVisual = dead;
        dmo.disableCollidersOnDestroy = new Collider[] { col };

        GarrisonBuilding g = root.AddComponent<GarrisonBuilding>();
        g.capacity = 5; g.infantryOnly = true; g.canFireFromBuilding = true;
        g.fireRange = 20f; g.damagePerOccupant = 5f; g.exitPoints = exits; g.ejectOnDestroy = true;
        EditorUtility.SetDirty(g);
        return root;
    }

    private static void BuildFuelTankLarge(Transform parent, Vector3 pos)
    {
        GameObject root = NewMapEntity(parent, "FuelTank", pos);

        CapsuleCollider col = root.AddComponent<CapsuleCollider>();
        col.height = 3f; col.radius = 1.3f; col.center = new Vector3(0f, 1.5f, 0f);

        GameObject alive = AddChildVisual(root, "AliveVisual", PrimitiveType.Cylinder,
            new Vector3(0f, 1.5f, 0f), new Vector3(2.4f, 1.5f, 2.4f), rustMat, true);
        GameObject dead = AddChildVisual(root, "DestroyedVisual", PrimitiveType.Cylinder,
            new Vector3(0f, 0.3f, 0f), new Vector3(2.6f, 0.3f, 2.6f), scorchMat, false);

        Health h = root.AddComponent<Health>(); h.maxHealth = 90f;

        ExplosiveMapObject ex = root.AddComponent<ExplosiveMapObject>();
        ex.maxHealth = 90f; ex.persistAfterDestroyed = true; ex.isTargetable = true;
        ex.aliveVisual = alive; ex.destroyedVisual = dead;
        ex.disableCollidersOnDestroy = new Collider[] { col };
        ex.explosionRadius = 9f; ex.explosionDamage = 140f;
        ex.affectsUnits = true; ex.affectsBuildings = true; ex.affectsMapObjects = true; ex.chainReactionEnabled = true;
        EditorUtility.SetDirty(ex);
    }

    /// <summary>Smaller explosive barrel — same component, lighter stats.</summary>
    private static void BuildFuelBarrel(Transform parent, Vector3 pos)
    {
        GameObject root = NewMapEntity(parent, "FuelBarrel", pos);

        CapsuleCollider col = root.AddComponent<CapsuleCollider>();
        col.height = 1.4f; col.radius = 0.5f; col.center = new Vector3(0f, 0.7f, 0f);

        GameObject alive = AddChildVisual(root, "AliveVisual", PrimitiveType.Cylinder,
            new Vector3(0f, 0.7f, 0f), new Vector3(0.9f, 0.7f, 0.9f), rustMat, true);
        GameObject dead = AddChildVisual(root, "DestroyedVisual", PrimitiveType.Cylinder,
            new Vector3(0f, 0.15f, 0f), new Vector3(1.0f, 0.15f, 1.0f), scorchMat, false);

        Health h = root.AddComponent<Health>(); h.maxHealth = 45f;

        ExplosiveMapObject ex = root.AddComponent<ExplosiveMapObject>();
        ex.maxHealth = 45f; ex.persistAfterDestroyed = true; ex.isTargetable = true;
        ex.aliveVisual = alive; ex.destroyedVisual = dead;
        ex.disableCollidersOnDestroy = new Collider[] { col };
        ex.explosionRadius = 6f; ex.explosionDamage = 80f;
        ex.affectsUnits = true; ex.affectsBuildings = true; ex.affectsMapObjects = true; ex.chainReactionEnabled = true;
        EditorUtility.SetDirty(ex);
    }

    private static TunnelEntrance BuildTunnel(Transform parent, Vector3 pos)
    {
        GameObject root = NewMapEntity(parent, "TunnelEntrance", pos);

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size = new Vector3(3f, 3f, 3f); col.center = new Vector3(0f, 1.5f, 0f);

        AddChildVisual(root, "Mouth", PrimitiveType.Cube,
            new Vector3(0f, 1.5f, 0f), new Vector3(3f, 3f, 3f), new Material(concreteMat) { color = new Color(0.2f, 0.2f, 0.22f) }, true);

        GameObject exit = new GameObject("ExitPoint");
        exit.transform.SetParent(root.transform, false);
        exit.transform.localPosition = new Vector3(0f, 0f, 3f);

        TunnelEntrance t = root.AddComponent<TunnelEntrance>();
        t.infantryOnly = true; t.allowVehicles = false; t.travelDelay = 1.2f; t.cooldown = 0.5f;
        t.exitPoints = new[] { exit.transform };
        EditorUtility.SetDirty(t);
        return t;
    }

    // ================================================================== //
    // Dust / smoke
    // ================================================================== //

    private static void AddDust(Transform parent, Vector3 localPos, Color color, float rate, float size, float radius)
    {
        GameObject go = new GameObject("Dust");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.startLifetime = 7f;
        main.startSpeed    = 0.35f;
        main.startSize     = size;
        main.startColor    = color;
        main.maxParticles  = 30;
        main.gravityModifier = -0.015f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var em = ps.emission;
        em.rateOverTime = rate;

        var sh = ps.shape;
        sh.shapeType = ParticleSystemShapeType.Box;
        sh.scale = new Vector3(radius, 0.2f, radius);

        ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
        Shader pShader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (pShader != null)
        {
            Material m = new Material(pShader);
            Texture2D soft = LoadOrCreateSoftSprite();
            if (soft != null) m.mainTexture = soft;
            r.material = m;
        }
    }

    private static Texture2D LoadOrCreateSoftSprite()
    {
        Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(SoftParticlePath);
        if (existing != null) return existing;

        try
        {
            int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            Vector2 c = new Vector2(size * 0.5f, size * 0.5f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), c) / (size * 0.5f);
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            string dir = Path.GetDirectoryName(SoftParticlePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(SoftParticlePath, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(SoftParticlePath, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(SoftParticlePath) is TextureImporter ti)
            {
                ti.textureType = TextureImporterType.Default;
                ti.alphaIsTransparency = true;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(SoftParticlePath);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Battlefield]   Soft dust sprite generation failed: {ex.Message}");
            return null;
        }
    }

    // ================================================================== //
    // MP wiring
    // ================================================================== //

    private static void RegisterWithGameplayWorldRoot(GameObject dressing)
    {
        GameplayWorldRoot wr = Object.FindFirstObjectByType<GameplayWorldRoot>(FindObjectsInactive.Include);
        if (wr == null) return;     // single-player / no MP setup — dressing stays visible

        var list = new System.Collections.Generic.List<GameObject>();
        if (wr.targets != null) list.AddRange(wr.targets);
        if (!list.Contains(dressing)) list.Add(dressing);
        wr.targets = list.ToArray();
        EditorUtility.SetDirty(wr);
        Debug.Log("[Battlefield]   Registered 'Battlefield' with GameplayWorldRoot (hidden until MatchStart in MP).");
    }

    private static void EnsureMapNetworkEvents()
    {
        GameObject nm = GameObject.Find("NetworkManager");
        if (nm == null) return;
        if (nm.GetComponent<MapInteractableNetworkEvents>() == null)
        {
            nm.AddComponent<MapInteractableNetworkEvents>();
            EditorUtility.SetDirty(nm);
            Debug.Log("[Battlefield]   Added MapInteractableNetworkEvents to NetworkManager (garrison/tunnel sync).");
        }
    }

    // ================================================================== //
    // Low-level helpers
    // ================================================================== //

    private static void BuildSharedMaterials()
    {
        roadMat     = Mat("BF_Road",     RoadColor,     0.02f);
        markMat     = Mat("BF_Marking",  MarkingColor,  0.05f);
        concreteMat = Mat("BF_Concrete", ConcreteColor, 0.05f);
        sandbagMat  = Mat("BF_Sandbag",  SandbagColor,  0.03f);
        woodMat     = Mat("BF_Wood",     WoodColor,     0.06f);
        rustMat     = Mat("BF_Rust",     RustColor,     0.10f);
        metalMat    = Mat("BF_Metal",    MetalColor,    0.35f);
        scorchMat   = Mat("BF_Scorch",   ScorchColor,   0.02f);
        deadWoodMat = Mat("BF_DeadWood", DeadWoodColor, 0.04f);
        tarpMat     = Mat("BF_Tarp",     TarpColor,     0.06f);
        _ = tarpMat;
    }

    private static Material Mat(string name, Color c, float smoothness)
    {
        Material m = new Material(ResolveLitShader()) { name = name, color = c };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
        if (m.HasProperty("_Metallic"))   m.SetFloat("_Metallic", 0f);
        return m;
    }

    /// <summary>Generic primitive prop with a shared material, optional collider, decor-static flagged.</summary>
    private static GameObject Prop(Transform parent, string name, PrimitiveType type, Vector3 pos,
                                   Vector3 scale, Quaternion rot, Material mat, bool keepCollider)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position    = pos;
        go.transform.rotation    = rot;
        go.transform.localScale  = scale;
        if (!keepCollider) StripCollider(go);
        SetMat(go, mat);
        MarkDecorStatic(go);
        return go;
    }

    private static GameObject AddChildVisual(GameObject parent, string name, PrimitiveType type,
                                             Vector3 localPos, Vector3 localScale, Material mat, bool active)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = localPos;
        go.transform.localScale    = localScale;
        StripCollider(go);
        SetMat(go, mat);
        go.SetActive(active);
        return go;
    }

    /// <summary>Creates a neutral map-object root (GameEntity, baked id) for an interactable landmark.</summary>
    private static GameObject NewMapEntity(Transform parent, string name, Vector3 pos)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = pos;

        GameEntity ge = go.AddComponent<GameEntity>();
        ge.ownerPlayerId          = GameEntity.NeutralOwnerId;
        ge.teamId                 = GameEntity.NeutralOwnerId;
        ge.entityType             = EntityType.MapObject;
        ge.prefabTypeId           = name;
        ge.overrideTeamFromHealth = false;
        ge.EditorSetEntityId("map-" + System.Guid.NewGuid().ToString("N"));
        EditorUtility.SetDirty(ge);
        return go;
    }

    private static Transform[] MakeExitRing(Transform parent, int count, float radius)
    {
        var exits = new Transform[count];
        for (int i = 0; i < count; i++)
        {
            float ang = i * Mathf.PI * 2f / count;
            GameObject e = new GameObject($"ExitPoint_{i}");
            e.transform.SetParent(parent, false);
            e.transform.localPosition = new Vector3(Mathf.Cos(ang) * radius, 0f, Mathf.Sin(ang) * radius);
            exits[i] = e.transform;
        }
        return exits;
    }

    private static void AddCarveObstacle(GameObject go, Vector3 size, Vector3 center)
    {
        NavMeshObstacle obs = go.GetComponent<NavMeshObstacle>();
        if (obs == null) obs = go.AddComponent<NavMeshObstacle>();
        obs.shape   = NavMeshObstacleShape.Box;
        obs.size    = size;
        obs.center  = center;
        obs.carving = true;
        obs.enabled = true;
    }

    private static void StripCollider(GameObject go)
    {
        Collider c = go.GetComponent<Collider>();
        if (c != null) Object.DestroyImmediate(c);
    }

    private static void SetMat(GameObject go, Material mat)
    {
        Renderer r = go.GetComponent<Renderer>();
        if (r != null)
        {
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.On;
        }
    }

    private static void MarkDecorStatic(GameObject go, bool recursive = false)
    {
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        if (recursive)
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                if (t.gameObject != go)
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic);
    }

    private static GameObject MakeChild(Transform parent, string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go;
    }

    private static Vector3 Mirror(Vector3 p) => new Vector3(-p.x, p.y, -p.z);

    private static float DistXZ(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static float DistanceToLane(Vector3 p, Vector3 lineOrigin, Vector3 lineDir)
    {
        Vector3 d = p - lineOrigin; d.y = 0f;
        Vector3 dir = new Vector3(lineDir.x, 0f, lineDir.z).normalized;
        Vector3 proj = dir * Vector3.Dot(d, dir);
        return (d - proj).magnitude;
    }

    private static Shader ResolveLitShader()
    {
        RenderPipelineAsset rp = GraphicsSettings.defaultRenderPipeline;
        bool isURP = rp != null && rp.GetType().Name.Contains("Universal");
        Shader urp = Shader.Find("Universal Render Pipeline/Lit");
        Shader std = Shader.Find("Standard");
        if (isURP && urp != null) return urp;
        if (!isURP && std != null) return std;
        return urp ?? std ?? Shader.Find("Hidden/InternalErrorShader");
    }
}
