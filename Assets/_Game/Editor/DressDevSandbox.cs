using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Battlefield dressing for DevSandboxScene — turns the flat green test plane
/// into a composed RTS battlefield: dirt roads, concrete pads, a fortified
/// combat zone at the crossroads, base-area props around the PowerPlant,
/// airfield surroundings, destructible fuel barrels, scatter debris, warmer
/// sun + subtle distance fog.
///
/// Design rules:
///   • Everything procedural — primitives + shared material assets under
///     Assets/_Game/Art/Environment/. No external packs.
///   • Decorative items (roads, pads, stripes, scorch patches) have NO
///     colliders → invisible to NavMesh bake and selection rays.
///   • Tactical items (barriers, sandbags, tanks, generator) get simple
///     BoxColliders + static flags → they block movement after a NavMesh
///     rebake and batch statically.
///   • Destructible barrels: Health(30, Enemy team so players can target) +
///     ExplodeOnDeath. Not static.
///   • Idempotent: everything lives under a "BattlefieldDressing" root that
///     is wiped and rebuilt each run.
///
/// Run via FileBridge: exec DressDevSandbox.Run  (or the menu).
/// After running, rebake the NavMesh so barriers carve into pathfinding:
/// Tools → RTS → Environment → Rebuild NavMesh And Snap Units.
/// </summary>
public static class DressDevSandbox
{
    private const string RootName = "BattlefieldDressing";
    private const string MatDir   = "Assets/_Game/Art/Environment";

    private static Material _concrete, _dirt, _metal, _sandbag, _warn, _warnDark, _fuelRed, _scorch, _crate;

    [MenuItem("Tools/RTS/Map/Dress DevSandbox Battlefield")]
    public static void Run()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.name != "DevSandboxScene")
        {
            Debug.LogWarning("[DressSandbox] Active scene is '" + scene.name + "' — open DevSandboxScene first " +
                             "(BridgeOps.OpenDevSandbox). Aborting.");
            return;
        }

        BuildMaterials();

        // Idempotent root
        GameObject old = GameObject.Find(RootName);
        if (old != null) Object.DestroyImmediate(old);
        var root = new GameObject(RootName);

        // ---------- Phase: roads (decorative, walkable) ----------------- //
        var roads = Group(root, "Roads");
        // N-S road through x=4, E-W road through z=2 → crossroads at (4,2).
        Strip(roads, new Vector3(4f, 0.012f, 0f),  new Vector3(3.4f, 0.02f, 90f), _dirt, "Road_NS");
        Strip(roads, new Vector3(0f, 0.012f, 2f),  new Vector3(90f, 0.02f, 3.4f), _dirt, "Road_EW");
        // Spur to the airfield area
        Strip(roads, new Vector3(-2f, 0.012f, -20f), new Vector3(3f, 0.02f, 26f), _dirt, "Road_AirfieldSpur");

        // ---------- Phase: base pads ------------------------------------ //
        var pads = Group(root, "BasePads");
        Pad(pads, new Vector3(-2.2f, 0f, -16.2f), new Vector3(7f, 0.025f, 7f), "Pad_PowerPlant");
        Pad(pads, new Vector3(0f, 0f, 0f),        new Vector3(6f, 0.025f, 6f), "Pad_Staging");

        // ---------- Phase: combat zone at the crossroads ----------------- //
        var combat = Group(root, "CombatZone");
        // Concrete barriers forming a broken line (cover) — block movement.
        Barrier(combat, new Vector3(8.5f, 0f, 4.5f),  0f);
        Barrier(combat, new Vector3(11f,  0f, 4.5f),  0f);
        Barrier(combat, new Vector3(14.5f,0f, 0.5f), 90f);
        // Sandbag clusters
        Sandbags(combat, new Vector3(7f, 0f, -1.5f), 20f);
        Sandbags(combat, new Vector3(12.5f, 0f, 6.5f), -15f);
        // Destructible fuel barrels (player-attackable hazards)
        Barrel(combat, new Vector3(9.8f, 0f, 1.2f));
        Barrel(combat, new Vector3(10.4f, 0f, 1.8f));
        Barrel(combat, new Vector3(15.5f, 0f, 8f));
        // Ammo crates (destructible, smaller bang)
        Crate(combat, new Vector3(8f, 0f, 6.8f), true);
        Crate(combat, new Vector3(13.6f, 0f, -2f), true);
        // Pre-battle scars
        ScorchPatch(combat, new Vector3(11f, 0f, 2.5f), 2.2f);
        ScorchPatch(combat, new Vector3(6.5f, 0f, 3.5f), 1.4f);

        // ---------- Phase: PowerPlant base area -------------------------- //
        var baseA = Group(root, "PowerPlantArea");
        Fence(baseA, new Vector3(-6.5f, 0f, -13f), 0f, 4);
        Fence(baseA, new Vector3(2.2f, 0f, -13f), 0f, 4);
        Barrel(baseA, new Vector3(-5.2f, 0f, -17.5f));
        Barrel(baseA, new Vector3(-5.9f, 0f, -16.8f));
        Generator(baseA, new Vector3(1.6f, 0f, -18.5f));
        Antenna(baseA, new Vector3(-5.8f, 0f, -19.5f));
        Crate(baseA, new Vector3(1.8f, 0f, -14.2f), false);
        Crate(baseA, new Vector3(2.6f, 0f, -15.0f), false);
        Floodlight(baseA, new Vector3(-6.3f, 0f, -14.0f));

        // ---------- Phase: airfield surroundings ------------------------- //
        var air = Group(root, "AirfieldArea");
        Vector3 af = new Vector3(-7.1f, 0f, -30.9f);
        FuelTank(air, af + new Vector3(16f, 0f, 10f));
        FuelTank(air, af + new Vector3(18.5f, 0f, 10f));
        Crate(air, af + new Vector3(16.5f, 0f, 6.5f), false);
        Crate(air, af + new Vector3(17.6f, 0f, 7.3f), false);
        WarningStripes(air, af + new Vector3(0f, 0f, 15.2f), 10);
        Cone(air, af + new Vector3(14f, 0f, 3f));
        Cone(air, af + new Vector3(14f, 0f, -3f));

        // ---------- Phase: scatter debris -------------------------------- //
        var scatter = Group(root, "Scatter");
        Random.InitState(777);
        for (int i = 0; i < 7; i++)
        {
            Vector3 p = new Vector3(Random.Range(-35f, 35f), 0f, Random.Range(-10f, 35f));
            if (Mathf.Abs(p.x - 4f) < 4f || Mathf.Abs(p.z - 2f) < 4f) continue; // keep roads clear
            Crate(scatter, p, false);
        }

        // ---------- Phase: lighting + atmosphere ------------------------- //
        foreach (Light l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (l.type != LightType.Directional) continue;
            l.color = new Color(1f, 0.95f, 0.85f);          // warm sun
            l.intensity = 1.2f;
            l.transform.rotation = Quaternion.Euler(52f, -28f, 0f);
            l.shadows = LightShadows.Soft;
            EditorUtility.SetDirty(l);
            break;
        }
        RenderSettings.fog        = true;
        RenderSettings.fogMode    = FogMode.Linear;
        RenderSettings.fogStartDistance = 90f;
        RenderSettings.fogEndDistance   = 240f;
        RenderSettings.fogColor   = new Color(0.65f, 0.70f, 0.75f);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[DressSandbox] ✓ Battlefield dressed: roads, pads, combat zone (barriers/sandbags/" +
                  "destructible barrels/crates/scorch), PowerPlant base area, airfield surroundings, " +
                  "scatter, warm sun + linear fog. NOW REBAKE NAVMESH (Tools → RTS → Environment → " +
                  "Rebuild NavMesh And Snap Units) so barriers block pathing.");
    }

    // ================================================================== //
    // SampleScene (match map) variant — central contested zone + flank
    // outposts. Parented UNDER the Environment root so the scene's
    // NavMeshSurface (CollectObjects.Children) bakes the new blockers.
    // Stays well inside |x|,|z| < 45 — corner spawns (±80,±70) untouched.
    // ================================================================== //

    [MenuItem("Tools/RTS/Map/Dress SampleScene Battlefield")]
    public static void DressSampleScene()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.name != "SampleScene")
        {
            Debug.LogWarning("[DressSandbox] Active scene is '" + scene.name + "' — open SampleScene first. Aborting.");
            return;
        }
        GameObject env = GameObject.Find("Environment");
        if (env == null) { Debug.LogError("[DressSandbox] No 'Environment' root in SampleScene. Aborting."); return; }

        BuildMaterials();

        Transform oldRoot = env.transform.Find(RootName);
        if (oldRoot != null) Object.DestroyImmediate(oldRoot.gameObject);
        var root = new GameObject(RootName);
        root.transform.SetParent(env.transform, false);

        // Roads: central cross, full battlefield length.
        var roads = Group(root, "Roads");
        Strip(roads, new Vector3(0f, 0.012f, 0f), new Vector3(4f, 0.02f, 90f), _dirt, "Road_NS");
        Strip(roads, new Vector3(0f, 0.012f, 0f), new Vector3(90f, 0.02f, 4f), _dirt, "Road_EW");

        // Central contested zone at the crossroads.
        var center = Group(root, "CentralZone");
        Barrier(center, new Vector3(-4.5f, 0f, 5f),   0f);
        Barrier(center, new Vector3(-2f,   0f, 5f),   0f);
        Barrier(center, new Vector3( 5f,   0f, -4.5f), 90f);
        Barrier(center, new Vector3( 5f,   0f, -7f),  90f);
        Sandbags(center, new Vector3(-5.5f, 0f, -5.5f), 45f);
        Sandbags(center, new Vector3( 6f,   0f,  6f),  -135f);
        Barrel(center, new Vector3(-3.2f, 0f, -3.5f));
        Barrel(center, new Vector3(-3.8f, 0f, -2.9f));
        Barrel(center, new Vector3( 4.2f, 0f,  3.4f));
        Barrel(center, new Vector3( 4.8f, 0f,  4.0f));
        Crate(center, new Vector3(-6.5f, 0f, 3.2f), true);
        Crate(center, new Vector3( 3.4f, 0f, -6.2f), true);
        ScorchPatch(center, new Vector3(0f, 0f, 0f), 2.6f);
        ScorchPatch(center, new Vector3(-4f, 0f, 2f), 1.5f);

        // Flank outposts (off-road cover pockets).
        var f1 = Group(root, "Outpost_NE");
        Sandbags(f1, new Vector3(30f, 0f, 30f), -45f);
        Barrel(f1,  new Vector3(31.5f, 0f, 28.5f));
        Crate(f1,   new Vector3(28.6f, 0f, 31.4f), false);
        var f2 = Group(root, "Outpost_SW");
        Sandbags(f2, new Vector3(-30f, 0f, -30f), 135f);
        Barrel(f2,  new Vector3(-31.5f, 0f, -28.5f));
        Crate(f2,   new Vector3(-28.6f, 0f, -31.4f), false);

        // Subtle fog for depth (match-map sized).
        RenderSettings.fog        = true;
        RenderSettings.fogMode    = FogMode.Linear;
        RenderSettings.fogStartDistance = 120f;
        RenderSettings.fogEndDistance   = 320f;
        RenderSettings.fogColor   = new Color(0.65f, 0.70f, 0.75f);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[DressSandbox] ✓ SampleScene dressed: central contested crossroads (cover + hazards), " +
                  "NE/SW outposts, roads, fog. Corner spawns untouched. REBAKE NAVMESH next.");
    }

    // ================================================================== //
    // Prop builders — primitives + shared materials, static where blocking
    // ================================================================== //

    private static GameObject Group(GameObject parent, string name)
    {
        var g = new GameObject(name);
        g.transform.SetParent(parent.transform, false);
        return g;
    }

    private static void Strip(GameObject parent, Vector3 center, Vector3 size, Material m, string name)
    {
        // size.z used as length when rotated — encode rotation in size.z>size.x decision upstream;
        // here size = (xLen, height, zLen) directly.
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        go.transform.position   = center;
        go.transform.localScale = new Vector3(size.x, size.y, size.z == 90f ? 90f : size.z);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        Apply(go, m, true);
    }

    private static void Pad(GameObject parent, Vector3 center, Vector3 size, string name)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        go.transform.position   = new Vector3(center.x, 0.013f, center.z);
        go.transform.localScale = size;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        Apply(go, _concrete, true);
    }

    private static void Barrier(GameObject parent, Vector3 pos, float rotY)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "ConcreteBarrier";
        go.transform.SetParent(parent.transform, false);
        go.transform.position   = pos + Vector3.up * 0.45f;
        go.transform.rotation   = Quaternion.Euler(0f, rotY, 0f);
        go.transform.localScale = new Vector3(2.2f, 0.9f, 0.55f);
        Apply(go, _concrete, true);   // keeps its BoxCollider → blocks + carves NavMesh
        var cov = go.AddComponent<CoverObject>();
        cov.coverRadius = 2.4f; cov.damageReduction = 0.35f;
    }

    private static void Sandbags(GameObject parent, Vector3 pos, float rotY)
    {
        var grp = new GameObject("SandbagWall");
        grp.transform.SetParent(parent.transform, false);
        grp.transform.position = pos;
        grp.transform.rotation = Quaternion.Euler(0f, rotY, 0f);
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < (row == 0 ? 4 : 3); i++)
            {
                var bag = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                bag.name = "Bag";
                bag.transform.SetParent(grp.transform, false);
                bag.transform.localPosition = new Vector3((i - 1.5f + row * 0.5f) * 0.62f, 0.18f + row * 0.3f, 0f);
                bag.transform.localRotation = Quaternion.Euler(90f, 0f, 90f);
                bag.transform.localScale    = new Vector3(0.36f, 0.34f, 0.36f);
                Object.DestroyImmediate(bag.GetComponent<Collider>());
                Apply(bag, _sandbag, true);
            }
        var col = grp.AddComponent<BoxCollider>();   // one collider for the wall
        col.center = new Vector3(0f, 0.4f, 0f);
        col.size   = new Vector3(2.6f, 0.8f, 0.6f);
        var cov = grp.AddComponent<CoverObject>();
        cov.coverRadius = 2.2f; cov.damageReduction = 0.3f;
        GameObjectUtility.SetStaticEditorFlags(grp, StaticEditorFlags.NavigationStatic | StaticEditorFlags.BatchingStatic);
    }

    private static void Barrel(GameObject parent, Vector3 pos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "FuelBarrel";
        go.transform.SetParent(parent.transform, false);
        go.transform.position   = pos + Vector3.up * 0.42f;
        go.transform.localScale = new Vector3(0.55f, 0.42f, 0.55f);
        Apply(go, _fuelRed, false);   // NOT static — destructible
        var h = go.AddComponent<Health>();
        h.team = Health.Team.Enemy;   // player-targetable hazard
        h.maxHealth = 30f;
        var ex = go.AddComponent<ExplodeOnDeath>();
        ex.explosionSize = 1;
        ex.scorchRadius  = 1.1f;
    }

    private static void Crate(GameObject parent, Vector3 pos, bool destructible)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = destructible ? "AmmoCrate" : "SupplyCrate";
        go.transform.SetParent(parent.transform, false);
        go.transform.position   = pos + Vector3.up * 0.3f;
        go.transform.rotation   = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        go.transform.localScale = new Vector3(0.7f, 0.6f, 0.7f);
        Apply(go, _crate, !destructible);
        if (destructible)
        {
            var h = go.AddComponent<Health>();
            h.team = Health.Team.Enemy;
            h.maxHealth = 20f;
            var ex = go.AddComponent<ExplodeOnDeath>();
            ex.explosionSize = 0;
            ex.scorchRadius  = 0.7f;
        }
    }

    private static void Fence(GameObject parent, Vector3 start, float rotY, int segments)
    {
        var grp = new GameObject("Fence");
        grp.transform.SetParent(parent.transform, false);
        grp.transform.position = start;
        grp.transform.rotation = Quaternion.Euler(0f, rotY, 0f);
        for (int i = 0; i < segments; i++)
        {
            var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            post.name = "Post";
            post.transform.SetParent(grp.transform, false);
            post.transform.localPosition = new Vector3(i * 1.4f, 0.5f, 0f);
            post.transform.localScale    = new Vector3(0.08f, 0.5f, 0.08f);
            Object.DestroyImmediate(post.GetComponent<Collider>());
            Apply(post, _metal, true);
            var rail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rail.name = "Rail";
            rail.transform.SetParent(grp.transform, false);
            rail.transform.localPosition = new Vector3(i * 1.4f + 0.7f, 0.8f, 0f);
            rail.transform.localScale    = new Vector3(1.4f, 0.06f, 0.04f);
            Object.DestroyImmediate(rail.GetComponent<Collider>());
            Apply(rail, _metal, true);
        }
    }

    private static void Generator(GameObject parent, Vector3 pos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "SmallGenerator";
        go.transform.SetParent(parent.transform, false);
        go.transform.position   = pos + Vector3.up * 0.45f;
        go.transform.localScale = new Vector3(1.2f, 0.9f, 0.8f);
        Apply(go, _metal, true);  // keeps collider → blocks
    }

    private static void Antenna(GameObject parent, Vector3 pos)
    {
        var mast = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        mast.name = "AntennaMast";
        mast.transform.SetParent(parent.transform, false);
        mast.transform.position   = pos + Vector3.up * 1.8f;
        mast.transform.localScale = new Vector3(0.07f, 1.8f, 0.07f);
        Object.DestroyImmediate(mast.GetComponent<Collider>());
        Apply(mast, _metal, true);
        var tip = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        tip.name = "Tip";
        tip.transform.SetParent(mast.transform, false);
        tip.transform.localPosition = new Vector3(0f, 1.05f, 0f);
        tip.transform.localScale    = new Vector3(2.2f, 0.09f, 2.2f);
        Object.DestroyImmediate(tip.GetComponent<Collider>());
        Apply(tip, _warn, true);
    }

    private static void Floodlight(GameObject parent, Vector3 pos)
    {
        var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = "FloodlightPole";
        pole.transform.SetParent(parent.transform, false);
        pole.transform.position   = pos + Vector3.up * 1.4f;
        pole.transform.localScale = new Vector3(0.1f, 1.4f, 0.1f);
        Object.DestroyImmediate(pole.GetComponent<Collider>());
        Apply(pole, _metal, true);
        var head = new GameObject("LightHead");
        head.transform.SetParent(pole.transform, false);
        head.transform.localPosition = new Vector3(0f, 1f, 0f);
        var l = head.AddComponent<Light>();
        l.type = LightType.Spot;
        l.color = new Color(1f, 0.95f, 0.8f);
        l.intensity = 2.4f;
        l.range = 14f;
        l.spotAngle = 95f;
        l.shadows = LightShadows.None;
        head.transform.rotation = Quaternion.Euler(70f, 30f, 0f);
    }

    private static void FuelTank(GameObject parent, Vector3 pos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "FuelTank";
        go.transform.SetParent(parent.transform, false);
        go.transform.position   = pos + Vector3.up * 1.0f;
        go.transform.localScale = new Vector3(1.6f, 1.0f, 1.6f);
        Apply(go, _metal, true);  // collider kept → blocks
        var band = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        band.name = "Band";
        band.transform.SetParent(go.transform, false);
        band.transform.localPosition = new Vector3(0f, 0.3f, 0f);
        band.transform.localScale    = new Vector3(1.04f, 0.06f, 1.04f);
        Object.DestroyImmediate(band.GetComponent<Collider>());
        Apply(band, _warn, true);
    }

    private static void WarningStripes(GameObject parent, Vector3 center, int count)
    {
        var grp = new GameObject("WarningStripes");
        grp.transform.SetParent(parent.transform, false);
        for (int i = 0; i < count; i++)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Cube);
            s.name = "Stripe";
            s.transform.SetParent(grp.transform, false);
            s.transform.position   = center + new Vector3((i - count / 2f) * 1.1f, 0.014f, 0f);
            s.transform.localScale = new Vector3(0.9f, 0.02f, 0.45f);
            Object.DestroyImmediate(s.GetComponent<Collider>());
            Apply(s, i % 2 == 0 ? _warn : _warnDark, true);
        }
    }

    private static void Cone(GameObject parent, Vector3 pos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "WarningCone";
        go.transform.SetParent(parent.transform, false);
        go.transform.position   = pos + Vector3.up * 0.22f;
        go.transform.localScale = new Vector3(0.25f, 0.22f, 0.25f);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        Apply(go, _warn, true);
    }

    private static void ScorchPatch(GameObject parent, Vector3 pos, float radius)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "ScorchPatch";
        go.transform.SetParent(parent.transform, false);
        go.transform.position   = new Vector3(pos.x, 0.018f, pos.z);
        go.transform.rotation   = Quaternion.Euler(90f, Random.Range(0f, 360f), 0f);
        go.transform.localScale = Vector3.one * (radius * 2f);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        Apply(go, _scorch, true);
    }

    // ================================================================== //

    private static void Apply(GameObject go, Material m, bool makeStatic)
    {
        var r = go.GetComponent<Renderer>();
        if (r != null)
        {
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }
        if (makeStatic)
            GameObjectUtility.SetStaticEditorFlags(go,
                StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic);
    }

    private static void BuildMaterials()
    {
        if (!AssetDatabase.IsValidFolder(MatDir))
            AssetDatabase.CreateFolder("Assets/_Game/Art", "Environment");
        _concrete = Mat("M_Concrete",  new Color(0.55f, 0.55f, 0.56f));
        _dirt     = Mat("M_DirtRoad",  new Color(0.42f, 0.33f, 0.24f));
        _metal    = Mat("M_MetalDark", new Color(0.20f, 0.21f, 0.23f));
        _sandbag  = Mat("M_Sandbag",   new Color(0.56f, 0.51f, 0.38f));
        _warn     = Mat("M_WarnYellow",new Color(0.95f, 0.80f, 0.10f));
        _warnDark = Mat("M_WarnDark",  new Color(0.12f, 0.12f, 0.12f));
        _fuelRed  = Mat("M_FuelRed",   new Color(0.68f, 0.16f, 0.10f));
        _scorch   = Mat("M_ScorchDark",new Color(0.07f, 0.065f, 0.06f));
        _crate    = Mat("M_CrateGreen",new Color(0.33f, 0.40f, 0.28f));
    }

    private static Material Mat(string name, Color c)
    {
        string path = MatDir + "/" + name + ".mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            m = new Material(sh) { color = c };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            AssetDatabase.CreateAsset(m, path);
        }
        return m;
    }
}
