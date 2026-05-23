using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// One-click editor tool that builds a clean, larger RTS test map under a single
/// "Environment" root GameObject.
///
/// Menu: Tools → RTS → Setup Environment
///
/// What it creates / replaces
///   Environment/                 (root — deleted and rebuilt on every run)
///     Ground/                    – 160×160 plane on the "Ground" layer, NavMesh static
///     TreeGroup/                 – low-poly trees scattered in the outer ring
///     RockGroup/                 – rocks scattered in the outer ring
///     MountainBorders/           – ring of large blocks around the perimeter
///     Decorations/               – reserved for future props
///
/// Safe to run multiple times. Only objects this tool created are destroyed —
/// gameplay objects (CommandCenter, units, ResourceNode, GameManager, HUDCanvas,
/// CameraRig) are never touched. Existing root-level Plane/Ground objects on the
/// Ground layer are removed so the new larger ground can take their place.
///
/// After running, rebake the NavMesh:  Window → AI → Navigation → Bake.
/// </summary>
public static class SetupEnvironment
{
    // ------------------------------------------------------------------ //
    // Tuning
    // ------------------------------------------------------------------ //

    private const float MapSize         = 160f;   // total side length of the playable plane
    private const float SafeZoneRadius  = 35f;    // central radius kept clear of decoration
    private const float PerimeterInset  = 10f;    // distance from the map edge where decoration starts thinning
    private const int   TreeCount       = 80;
    private const int   RockCount       = 40;
    private const int   MountainPerSide = 6;

    // Natural, readable palette — replaces the old neon look.
    private static readonly Color GrassColor    = new Color(0.36f, 0.55f, 0.30f); // soft green
    private static readonly Color TrunkColor    = new Color(0.36f, 0.23f, 0.13f); // bark brown
    private static readonly Color FoliageColor  = new Color(0.20f, 0.45f, 0.20f); // forest green
    private static readonly Color RockColor     = new Color(0.50f, 0.50f, 0.52f); // grey
    private static readonly Color MountainColor = new Color(0.40f, 0.36f, 0.32f); // warm stone

    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Setup Environment")]
    public static void SetupEnv()
    {
        Debug.Log("[SetupEnvironment] ── Starting environment rebuild ────────────");

        int groundLayer = LayerMask.NameToLayer("Ground");
        if (groundLayer < 0)
        {
            Debug.LogError("[SetupEnvironment] ✗ No 'Ground' layer exists. " +
                           "Add it under Project Settings → Tags and Layers, then re-run.");
            return;
        }

        // ── 1. Wipe previous Environment root ────────────────────────── //
        GameObject oldEnv = GameObject.Find("Environment");
        if (oldEnv != null)
        {
            Undo.DestroyObjectImmediate(oldEnv);
            Debug.Log("[SetupEnvironment]   Removed old 'Environment' root.");
        }

        // ── 2. Wipe stray root-level ground planes ───────────────────── //
        Scene scene = SceneManager.GetActiveScene();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root == null) continue;
            bool isGround = (root.name == "Ground" || root.name == "Plane")
                            && root.layer == groundLayer;
            if (isGround)
            {
                Undo.DestroyObjectImmediate(root);
                Debug.Log($"[SetupEnvironment]   Removed stray ground at scene root: '{root.name}'.");
            }
        }

        // ── 3. Create Environment hierarchy ──────────────────────────── //
        GameObject env = new GameObject("Environment");
        Undo.RegisterCreatedObjectUndo(env, "Create Environment");

        GameObject groundGroup     = CreateGroup(env, "Ground");
        GameObject treeGroup       = CreateGroup(env, "TreeGroup");
        GameObject rockGroup       = CreateGroup(env, "RockGroup");
        GameObject mountainGroup   = CreateGroup(env, "MountainBorders");
        CreateGroup(env, "Decorations"); // empty container for future props

        // ── 4. Build shared materials (runtime, not saved as assets) ─── //
        Material grassMat    = CreateLitMaterial("EnvGrass",    GrassColor);
        Material trunkMat    = CreateLitMaterial("EnvTrunk",    TrunkColor);
        Material foliageMat  = CreateLitMaterial("EnvFoliage",  FoliageColor);
        Material rockMat     = CreateLitMaterial("EnvRock",     RockColor);
        Material mountainMat = CreateLitMaterial("EnvMountain", MountainColor);

        // ── 5. Build content ─────────────────────────────────────────── //
        int groundCount    = BuildGround(groundGroup, grassMat, groundLayer);
        int mountainCount  = BuildMountains(mountainGroup, mountainMat);
        int treeCount      = BuildTrees(treeGroup, trunkMat, foliageMat);
        int rockCount      = BuildRocks(rockGroup, rockMat);

        // ── 6. Finalise ──────────────────────────────────────────────── //
        EditorSceneManager.MarkSceneDirty(scene);

        Debug.Log($"[SetupEnvironment] ✓ Ground         : {groundCount} (160×160, NavMesh static)");
        Debug.Log($"[SetupEnvironment] ✓ MountainBorders: {mountainCount}");
        Debug.Log($"[SetupEnvironment] ✓ TreeGroup      : {treeCount}");
        Debug.Log($"[SetupEnvironment] ✓ RockGroup      : {rockCount}");
        Debug.LogWarning("[SetupEnvironment] Rebake the NavMesh now: " +
                         "Window → AI → Navigation → Bake. Movement will not work until you do.");
        Debug.Log("[SetupEnvironment] ── Done. Press Ctrl+S to save the scene. ────────");
    }

    // ================================================================== //
    // Hierarchy helpers
    // ================================================================== //

    private static GameObject CreateGroup(GameObject parent, string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    // ================================================================== //
    // Material factory — picks URP Lit when available, else Standard
    // ================================================================== //

    private static Material CreateLitMaterial(string name, Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        Material m = new Material(shader) { name = name };
        m.color = color;
        if (shader.name.Contains("Universal"))
            m.SetColor("_BaseColor", color);
        return m;
    }

    // ================================================================== //
    // Ground
    // ================================================================== //

    private static int BuildGround(GameObject parent, Material mat, int groundLayer)
    {
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.SetParent(parent.transform, false);
        ground.transform.localScale    = new Vector3(MapSize / 10f, 1f, MapSize / 10f);
        ground.layer                   = groundLayer;
        ground.GetComponent<Renderer>().sharedMaterial = mat;

        // Mark as NavMesh-eligible so the player can bake walkable ground.
        GameObjectUtility.SetStaticEditorFlags(ground, StaticEditorFlags.NavigationStatic);
        return 1;
    }

    // ================================================================== //
    // Mountains — perimeter ring of large blocks (visual borders)
    // ================================================================== //

    private static int BuildMountains(GameObject parent, Material mat)
    {
        const float ringInset = 6f;
        float half     = MapSize * 0.5f;
        float ringPos  = half - ringInset;
        int   created  = 0;

        // Deterministic seed so layout is consistent between runs
        Random.InitState(20260523);

        for (int side = 0; side < 4; side++)
        {
            for (int i = 0; i < MountainPerSide; i++)
            {
                float t      = (i + 0.5f) / MountainPerSide;
                float along  = Mathf.Lerp(-half + ringInset, half - ringInset, t);

                Vector3 pos;
                switch (side)
                {
                    case 0:  pos = new Vector3(along,    0f,  ringPos); break; // north
                    case 1:  pos = new Vector3(along,    0f, -ringPos); break; // south
                    case 2:  pos = new Vector3( ringPos, 0f, along);    break; // east
                    default: pos = new Vector3(-ringPos, 0f, along);    break; // west
                }

                float width  = Random.Range(10f, 16f);
                float height = Random.Range( 9f, 18f);
                float depth  = Random.Range(10f, 16f);

                GameObject m = GameObject.CreatePrimitive(PrimitiveType.Cube);
                m.name = $"Mountain_{side}_{i:D2}";
                m.transform.SetParent(parent.transform, false);
                m.transform.localPosition = new Vector3(pos.x, height * 0.5f, pos.z);
                m.transform.localScale    = new Vector3(width, height, depth);
                m.transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                m.GetComponent<Renderer>().sharedMaterial = mat;

                // Mountains keep their colliders so they read as obstacles to NavMesh bake
                GameObjectUtility.SetStaticEditorFlags(m, StaticEditorFlags.NavigationStatic);
                created++;
            }
        }
        return created;
    }

    // ================================================================== //
    // Trees — outer ring, no colliders (don't block movement or placement)
    // ================================================================== //

    private static int BuildTrees(GameObject parent, Material trunkMat, Material foliageMat)
    {
        Random.InitState(31415);

        float half        = MapSize * 0.5f;
        float outerLimit  = half - PerimeterInset - 3f;
        int   placed = 0, tries = 0, maxTries = TreeCount * 20;

        while (placed < TreeCount && tries < maxTries)
        {
            tries++;
            float x = Random.Range(-outerLimit, outerLimit);
            float z = Random.Range(-outerLimit, outerLimit);

            // Keep the central play zone clear
            if (new Vector2(x, z).magnitude < SafeZoneRadius) continue;

            CreateTree(parent.transform, new Vector3(x, 0f, z), placed, trunkMat, foliageMat);
            placed++;
        }
        return placed;
    }

    private static void CreateTree(Transform parent, Vector3 pos, int index,
                                   Material trunkMat, Material foliageMat)
    {
        GameObject tree = new GameObject($"Tree_{index:D3}");
        tree.transform.SetParent(parent, false);
        tree.transform.localPosition = pos;
        tree.transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

        // Trunk
        GameObject trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        trunk.name = "Trunk";
        trunk.transform.SetParent(tree.transform, false);
        trunk.transform.localPosition = new Vector3(0f, 1.2f, 0f);
        trunk.transform.localScale    = new Vector3(0.35f, 1.2f, 0.35f);
        trunk.GetComponent<Renderer>().sharedMaterial = trunkMat;
        Object.DestroyImmediate(trunk.GetComponent<Collider>()); // no collision

        // Foliage
        GameObject foliage = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        foliage.name = "Foliage";
        foliage.transform.SetParent(tree.transform, false);
        float fsize = Random.Range(2.0f, 3.0f);
        foliage.transform.localPosition = new Vector3(0f, 3.0f, 0f);
        foliage.transform.localScale    = new Vector3(fsize, fsize, fsize);
        foliage.GetComponent<Renderer>().sharedMaterial = foliageMat;
        Object.DestroyImmediate(foliage.GetComponent<Collider>());
    }

    // ================================================================== //
    // Rocks — outer ring, no colliders
    // ================================================================== //

    private static int BuildRocks(GameObject parent, Material mat)
    {
        Random.InitState(2718);

        float half       = MapSize * 0.5f;
        float outerLimit = half - PerimeterInset - 3f;
        int   placed = 0, tries = 0, maxTries = RockCount * 20;

        while (placed < RockCount && tries < maxTries)
        {
            tries++;
            float x = Random.Range(-outerLimit, outerLimit);
            float z = Random.Range(-outerLimit, outerLimit);

            if (new Vector2(x, z).magnitude < SafeZoneRadius * 0.9f) continue;

            GameObject rock = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            rock.name = $"Rock_{placed:D3}";
            rock.transform.SetParent(parent.transform, false);

            float s = Random.Range(0.8f, 1.8f);
            rock.transform.localPosition = new Vector3(x, s * 0.30f, z);
            rock.transform.localScale    = new Vector3(s * 1.4f, s * 0.7f, s * 1.1f);
            rock.transform.localRotation = Quaternion.Euler(
                Random.Range(-15f, 15f), Random.Range(0f, 360f), Random.Range(-15f, 15f));
            rock.GetComponent<Renderer>().sharedMaterial = mat;
            Object.DestroyImmediate(rock.GetComponent<Collider>());

            placed++;
        }
        return placed;
    }
}
