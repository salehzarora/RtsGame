using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// One-click editor tool that resets the active scene to a clean two-base RTS
/// match layout — a large flat map with the player base on one side, the enemy
/// base on the opposite side, and resources near each.
///
/// Menu: Tools → RTS → Match → Setup Clean Match Map
///
/// Pipeline (safe / idempotent — re-run anytime):
///   1. Sweep all known test/dressing roots and stray scene-root match objects
///      (Environment, EnemyTestUnits, PlayerStart, EnemyStart, ResourceNodes,
///      TestObjects, Gameplay, stray ResourceNode / ConstructionSite / dummies).
///   2. Build a 240 × 240 Environment with a ground plane, mountain border, and
///      light tree / rock decoration. Ground is on the "Ground" layer + marked
///      NavMesh static. A NavMeshSurface lives on the Environment root.
///   3. Preserve and reposition the existing player CommandCenter (or create a
///      fresh one if none exists). Spawn a Worker next to it.
///   4. Build a fresh enemy CommandCenter (Health Team = Enemy) opposite the
///      player. Spawn a stationary enemy Worker. NO SelectableBuilding —
///      players can't click-select enemy buildings.
///   5. Sprinkle 3–5 ResourceNodes near each start position and 2 neutral
///      ResourceNodes near the centre.
///   6. Add a <see cref="MatchManager"/> to the scene root and wire the start
///      positions + CC references.
///   7. Point the CameraRig at the player base.
///   8. Bake the NavMesh via the Environment's NavMeshSurface so the player
///      can press Play and command units immediately.
///
/// What this tool deliberately preserves (never deleted):
///   • GameManager + all its managers (PlayerResourceManager, PowerManager,
///     BuildingPlacementManager, UnitSelector, RTSHUD, PlayerFactionManager).
///   • Main Camera / CameraRig.
///   • HUDCanvas / MainMenuCanvas / EventSystem.
///   • Directional Light(s) and scene lighting setup.
///   • Existing player CommandCenter (repositioned, kept for its
///     SetupCommandCenter wiring — worker/dozer prefab assignments).
///
/// What this tool deliberately does NOT do:
///   • Touch any prefab asset on disk. All edits are scene-local.
///   • Enable EnemyWaveSpawner or EnemyAIController.
///   • Implement enemy bot AI. The enemy base is set dressing for now.
///   • Add SelectableBuilding to enemy structures.
///   • Drop construction sites; both bases start with a finished CC.
/// </summary>
public static class SetupCleanMatchMap
{
    // ------------------------------------------------------------------ //
    // Map tuning
    // ------------------------------------------------------------------ //

    private const float MapSize         = 240f;   // total side length of the playable plane
    private const float SafeZoneRadius  = 60f;    // central radius kept clear of decoration
    private const float PerimeterInset  = 12f;    // distance from the map edge where decoration starts thinning
    private const int   TreeCount       = 120;
    private const int   RockCount       = 50;
    private const int   MountainPerSide = 8;

    private static readonly Vector3 PlayerStartPos = new Vector3(-80f, 0f, -70f);
    private static readonly Vector3 EnemyStartPos  = new Vector3( 80f, 0f,  70f);

    // Worker offset relative to its CommandCenter at spawn.
    private static readonly Vector3 WorkerOffset = new Vector3(6f, 0f, 0f);

    // Resource layout — points are offsets from each base's centre.
    private static readonly Vector3[] PlayerResourceOffsets =
    {
        new Vector3(  10f, 0f,   6f),
        new Vector3(  10f, 0f,  -6f),
        new Vector3(  16f, 0f,   0f),
        new Vector3(   5f, 0f, -10f),
    };
    private static readonly Vector3[] EnemyResourceOffsets =
    {
        new Vector3( -10f, 0f,  -6f),
        new Vector3( -10f, 0f,   6f),
        new Vector3( -16f, 0f,   0f),
        new Vector3(  -5f, 0f,  10f),
    };
    private static readonly Vector3[] NeutralResourcePositions =
    {
        new Vector3(  0f, 0f,  18f),
        new Vector3(  0f, 0f, -18f),
    };

    // ------------------------------------------------------------------ //
    // Palette
    // ------------------------------------------------------------------ //

    private static readonly Color GrassColor    = new Color(0.36f, 0.55f, 0.30f);
    private static readonly Color TrunkColor    = new Color(0.36f, 0.23f, 0.13f);
    private static readonly Color FoliageColor  = new Color(0.20f, 0.45f, 0.20f);
    private static readonly Color RockColor     = new Color(0.50f, 0.50f, 0.52f);
    private static readonly Color MountainColor = new Color(0.40f, 0.36f, 0.32f);

    private static readonly Color CCPlayerBlue  = new Color(0.30f, 0.55f, 0.95f);
    private static readonly Color CCEnemyRed    = new Color(0.55f, 0.15f, 0.15f);
    private static readonly Color EnemyWorker   = new Color(0.85f, 0.20f, 0.20f);
    private static readonly Color ResourceGold  = new Color(0.95f, 0.78f, 0.18f);

    // ------------------------------------------------------------------ //
    // Layer names
    // ------------------------------------------------------------------ //

    private const string GroundLayerName   = "Ground";
    private const string UnitLayerName     = "Unit";
    private const string BuildingLayerName = "Building";
    private const string ResourceLayerName = "Resource";

    // Old roots to wipe on every run. Order doesn't matter — DestroyImmediate
    // is safe per object.
    private static readonly string[] WipedRootNames =
    {
        "Environment",
        "EnemyTestUnits",
        "ResourceNodes",
        "PlayerStart",
        "EnemyStart",
        "TestObjects",
        "Gameplay",
    };

    // ------------------------------------------------------------------ //
    // Asset paths used to bootstrap units when no in-scene instance exists.
    // ------------------------------------------------------------------ //

    private const string WorkerPrefabPath = "Assets/_Game/Prefabs/WorkerPrefab.prefab";

    // ================================================================== //
    // Entry point
    // ================================================================== //

    [MenuItem("Tools/RTS/Match/Setup Clean Match Map")]
    public static void Setup()
    {
        Debug.Log("[MatchSetup] ── Starting clean-match setup ─────────────────");

        if (!ResolveLayers(out int groundLayer, out int unitLayer,
                           out int buildingLayer, out int resourceLayer)) return;

        Scene scene = SceneManager.GetActiveScene();

        // 1. Sweep test/dressing objects ------------------------------------
        CleanScene(groundLayer, buildingLayer);
        Debug.Log("[MatchSetup] Old test objects removed.");

        // 2. Build environment ----------------------------------------------
        GameObject env = BuildEnvironment(groundLayer);
        Debug.Log("[MatchSetup] Large map created.");

        // 3. Player base -----------------------------------------------------
        CommandCenter playerCC = BuildOrRepositionPlayerBase(buildingLayer, unitLayer);
        Debug.Log("[MatchSetup] Player start created.");

        // 4. Enemy base ------------------------------------------------------
        GameObject enemyCC = BuildEnemyBase(buildingLayer, unitLayer);
        Debug.Log("[MatchSetup] Enemy start created.");

        // 5. Resources -------------------------------------------------------
        BuildResources(resourceLayer);
        Debug.Log("[MatchSetup] Resource nodes placed.");

        // 6. MatchManager ----------------------------------------------------
        MatchManager mm = EnsureMatchManager();
        mm.playerStartPosition = PlayerStartPos;
        mm.enemyStartPosition  = EnemyStartPos;
        mm.playerCommandCenter = playerCC;
        mm.enemyCommandCenter  = enemyCC;
        EditorUtility.SetDirty(mm);

        // 6b. EnemyResourceManager — separate from PlayerResourceManager.
        // Co-located on the MatchManager GameObject so the scene stays tidy;
        // its singleton lookup doesn't care where the component lives.
        EnsureEnemyResourceManager(mm.gameObject);

        // 6c. Wire EnemyBuildAI to its prefab references (creating any missing
        // dress-building prefabs on the fly). Runs LAST among the gameplay
        // setup steps so the in-scene EnemyBuildAI exists when it's wired.
        RepairEnemyBuilderAI.Run();

        // 7. Camera ----------------------------------------------------------
        AimCameraAtPlayerStart();

        // 8. NavMesh ---------------------------------------------------------
        BakeNavMeshOnEnvironment(env);

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log("[MatchSetup] Clean match setup complete.");
        Debug.Log("[MatchSetup] ── Done. Save the scene (Ctrl+S) and press Play. ──");
    }

    // ================================================================== //
    // 1. Cleaning
    // ================================================================== //

    private static void CleanScene(int groundLayer, int buildingLayer)
    {
        Scene scene = SceneManager.GetActiveScene();
        GameObject[] roots = scene.GetRootGameObjects();

        // 1a — well-known dressing/test roots
        var toRemove = new List<GameObject>();
        foreach (GameObject root in roots)
        {
            if (root == null) continue;
            if (System.Array.IndexOf(WipedRootNames, root.name) >= 0)
                toRemove.Add(root);
        }

        // 1b — stray root-level ResourceNodes, ConstructionSites, dummies, old
        // ground planes. The collectors below run on the SAME root list so we
        // don't enumerate scene roots multiple times.
        foreach (GameObject root in roots)
        {
            if (root == null) continue;
            if (toRemove.Contains(root)) continue;

            // Preserve list — these are critical scene objects we MUST keep.
            if (IsPreservedRoot(root)) continue;

            // Stray gameplay debris at scene root: ResourceNode, ConstructionSite,
            // EnemyDummy, EnemyInfantry / EnemyVehicleDummy / EnemyMG defenses, etc.
            // Each match a single Health-bearing / ResourceNode-bearing object we
            // built earlier and is now stale.
            bool isStrayResource     = root.GetComponent<ResourceNode>()       != null;
            bool isStraySite         = root.GetComponent<ConstructionSite>()   != null;
            bool isStrayHealth       = root.GetComponent<Health>()             != null;
            bool isStrayOldGround    = (root.name == "Ground" || root.name == "Plane") && root.layer == groundLayer;
            bool isStrayBuilding     = root.GetComponent<Building>()           != null && root.layer == buildingLayer;

            if (isStrayResource || isStraySite || isStrayHealth || isStrayOldGround || isStrayBuilding)
            {
                // Preserve the in-scene PLAYER CommandCenter — we'll reposition
                // it later instead of destroying it. Enemy CC is always rebuilt.
                CommandCenter cc = root.GetComponent<CommandCenter>();
                if (cc != null) continue;

                toRemove.Add(root);
            }
        }

        foreach (GameObject go in toRemove)
        {
            Debug.Log($"[MatchSetup]   Removing: {go.name}");
            Undo.DestroyObjectImmediate(go);
        }
    }

    /// <summary>
    /// Returns true if <paramref name="root"/> is a critical object that must
    /// survive the cleanup pass: managers, cameras, lighting, canvases, the
    /// EventSystem, and any other infrastructure not owned by the match layout.
    /// </summary>
    private static bool IsPreservedRoot(GameObject root)
    {
        // Manager singletons — anything carrying a critical system component.
        if (root.GetComponent<PlayerResourceManager>()     != null) return true;
        if (root.GetComponent<PowerManager>()              != null) return true;
        if (root.GetComponent<BuildingPlacementManager>()  != null) return true;
        if (root.GetComponent<UnitSelector>()              != null) return true;
        if (root.GetComponent<RTSHUD>()                    != null) return true;
        if (root.GetComponent<PlayerFactionManager>()      != null) return true;
        if (root.GetComponent<GameStateManager>()          != null) return true;
        if (root.GetComponent<AttackTargetMarker>()        != null) return true;

        // Cameras + camera rig
        if (root.GetComponentInChildren<Camera>(includeInactive: true) != null) return true;
        if (root.GetComponent<RTSCamera>()                 != null) return true;

        // UI / event system
        if (root.GetComponent<Canvas>()                    != null) return true;
        if (root.GetComponent<UnityEngine.EventSystems.EventSystem>() != null) return true;

        // Lighting (Directional Light, area light, etc.)
        if (root.GetComponent<Light>()                     != null) return true;

        // Friendly named guards in case the user has manually renamed
        // critical infrastructure.
        string n = root.name;
        if (n == "GameManager")     return true;
        if (n == "HUDCanvas")       return true;
        if (n == "MainMenuCanvas")  return true;
        if (n == "EventSystem")     return true;
        if (n == "Directional Light") return true;
        if (n == "MatchManager")    return true;

        return false;
    }

    // ================================================================== //
    // 2. Environment
    // ================================================================== //

    private static GameObject BuildEnvironment(int groundLayer)
    {
        GameObject env = new GameObject("Environment");
        Undo.RegisterCreatedObjectUndo(env, "Create Environment");

        Material grassMat    = CreateLitMaterial("EnvGrass",    GrassColor);
        Material trunkMat    = CreateLitMaterial("EnvTrunk",    TrunkColor);
        Material foliageMat  = CreateLitMaterial("EnvFoliage",  FoliageColor);
        Material rockMat     = CreateLitMaterial("EnvRock",     RockColor);
        Material mountainMat = CreateLitMaterial("EnvMountain", MountainColor);

        GameObject groundGroup = MakeChild(env, "Ground");
        BuildGround(groundGroup, grassMat, groundLayer);

        GameObject mountains = MakeChild(env, "MountainBorders");
        BuildMountains(mountains, mountainMat);

        GameObject decor = MakeChild(env, "Decorations");
        BuildTrees(decor, trunkMat, foliageMat);
        BuildRocks(decor, rockMat);

        MakeChild(env, "ResourceAreas"); // empty container; ResourceNodes live at root

        return env;
    }

    private static void BuildGround(GameObject parent, Material mat, int groundLayer)
    {
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.SetParent(parent.transform, false);
        ground.transform.localScale    = new Vector3(MapSize / 10f, 1f, MapSize / 10f);
        ground.layer                   = groundLayer;
        ground.GetComponent<Renderer>().sharedMaterial = mat;

        // Guard against the spec note: "no NavMeshAgent on ground" — primitives
        // never have one, but strip if a future template adds it.
        NavMeshAgent stray = ground.GetComponent<NavMeshAgent>();
        if (stray != null) Object.DestroyImmediate(stray);

        GameObjectUtility.SetStaticEditorFlags(ground, StaticEditorFlags.NavigationStatic);
    }

    private static void BuildMountains(GameObject parent, Material mat)
    {
        const float ringInset = 8f;
        float half     = MapSize * 0.5f;
        float ringPos  = half - ringInset;

        Random.InitState(20260601);

        for (int side = 0; side < 4; side++)
        {
            for (int i = 0; i < MountainPerSide; i++)
            {
                float t     = (i + 0.5f) / MountainPerSide;
                float along = Mathf.Lerp(-half + ringInset, half - ringInset, t);

                Vector3 pos;
                switch (side)
                {
                    case 0:  pos = new Vector3(along,    0f,  ringPos); break;
                    case 1:  pos = new Vector3(along,    0f, -ringPos); break;
                    case 2:  pos = new Vector3( ringPos, 0f, along);    break;
                    default: pos = new Vector3(-ringPos, 0f, along);    break;
                }

                float width  = Random.Range(14f, 22f);
                float height = Random.Range(10f, 20f);
                float depth  = Random.Range(14f, 22f);

                GameObject m = GameObject.CreatePrimitive(PrimitiveType.Cube);
                m.name = $"Mountain_{side}_{i:D2}";
                m.transform.SetParent(parent.transform, false);
                m.transform.localPosition = new Vector3(pos.x, height * 0.5f, pos.z);
                m.transform.localScale    = new Vector3(width, height, depth);
                m.transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                m.GetComponent<Renderer>().sharedMaterial = mat;
                GameObjectUtility.SetStaticEditorFlags(m, StaticEditorFlags.NavigationStatic);
            }
        }
    }

    private static void BuildTrees(GameObject parent, Material trunkMat, Material foliageMat)
    {
        Random.InitState(31415);
        float half       = MapSize * 0.5f;
        float outerLimit = half - PerimeterInset - 4f;
        int   placed = 0, tries = 0, maxTries = TreeCount * 20;

        while (placed < TreeCount && tries < maxTries)
        {
            tries++;
            float x = Random.Range(-outerLimit, outerLimit);
            float z = Random.Range(-outerLimit, outerLimit);

            // Keep the central play zone and both base centres clear.
            if (new Vector2(x, z).magnitude < SafeZoneRadius) continue;
            if (DistanceXZ(x, z, PlayerStartPos) < 25f) continue;
            if (DistanceXZ(x, z, EnemyStartPos)  < 25f) continue;

            CreateTree(parent.transform, new Vector3(x, 0f, z), placed, trunkMat, foliageMat);
            placed++;
        }
    }

    private static void CreateTree(Transform parent, Vector3 pos, int index,
                                   Material trunkMat, Material foliageMat)
    {
        GameObject tree = new GameObject($"Tree_{index:D3}");
        tree.transform.SetParent(parent, false);
        tree.transform.localPosition = pos;
        tree.transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

        GameObject trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        trunk.name = "Trunk";
        trunk.transform.SetParent(tree.transform, false);
        trunk.transform.localPosition = new Vector3(0f, 1.2f, 0f);
        trunk.transform.localScale    = new Vector3(0.35f, 1.2f, 0.35f);
        trunk.GetComponent<Renderer>().sharedMaterial = trunkMat;
        Object.DestroyImmediate(trunk.GetComponent<Collider>());

        GameObject foliage = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        foliage.name = "Foliage";
        foliage.transform.SetParent(tree.transform, false);
        float fsize = Random.Range(2.0f, 3.0f);
        foliage.transform.localPosition = new Vector3(0f, 3.0f, 0f);
        foliage.transform.localScale    = new Vector3(fsize, fsize, fsize);
        foliage.GetComponent<Renderer>().sharedMaterial = foliageMat;
        Object.DestroyImmediate(foliage.GetComponent<Collider>());
    }

    private static void BuildRocks(GameObject parent, Material mat)
    {
        Random.InitState(2718);
        float half       = MapSize * 0.5f;
        float outerLimit = half - PerimeterInset - 4f;
        int   placed = 0, tries = 0, maxTries = RockCount * 20;

        while (placed < RockCount && tries < maxTries)
        {
            tries++;
            float x = Random.Range(-outerLimit, outerLimit);
            float z = Random.Range(-outerLimit, outerLimit);

            if (new Vector2(x, z).magnitude < SafeZoneRadius * 0.9f) continue;
            if (DistanceXZ(x, z, PlayerStartPos) < 22f) continue;
            if (DistanceXZ(x, z, EnemyStartPos)  < 22f) continue;

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
    }

    // ================================================================== //
    // 3. Player base — reposition existing CC, or build a fresh one.
    // ================================================================== //

    private static CommandCenter BuildOrRepositionPlayerBase(int buildingLayer, int unitLayer)
    {
        GameObject playerStart = new GameObject("PlayerStart");
        Undo.RegisterCreatedObjectUndo(playerStart, "Create PlayerStart");
        playerStart.transform.position = PlayerStartPos;

        // 3a — player CommandCenter. Preserve the one in scene if it exists —
        // SetupCommandCenter has likely wired worker/dozer prefabs onto it.
        // For a fresh CC, run SetupCommandCenter so it gets the same wiring.
        CommandCenter playerCC = Object.FindAnyObjectByType<CommandCenter>(FindObjectsInactive.Include);
        bool createdFresh = false;
        if (playerCC == null)
        {
            playerCC = CreatePlayerCommandCenter(buildingLayer);
            createdFresh = true;
            Debug.Log("[MatchSetup]   No existing CommandCenter — built a fresh one.");
        }
        else
        {
            Debug.Log("[MatchSetup]   Preserving existing player CommandCenter — repositioning.");
        }

        Undo.RecordObject(playerCC.transform, "Move Player CommandCenter");
        playerCC.transform.position = PlayerStartPos;
        playerCC.transform.SetParent(playerStart.transform, worldPositionStays: true);
        EditorUtility.SetDirty(playerCC.transform);

        // 3a-fresh — wire Worker/Dozer/SelectableBuilding production via the
        // existing one-click CC tool. Skipped when we reused an in-scene CC
        // because its prefab assignments are already complete.
        if (createdFresh)
        {
            Debug.Log("[MatchSetup]   Running SetupCommandCenter to wire fresh CC.");
            SetupCommandCenter.Setup();
        }

        // 3b — player Worker. Always spawn a fresh one from the WorkerPrefab so
        // we don't carry stale state from a previous match.
        GameObject workerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(WorkerPrefabPath);
        if (workerPrefab != null)
        {
            GameObject worker = (GameObject)PrefabUtility.InstantiatePrefab(workerPrefab);
            worker.name = "Worker";
            worker.transform.SetParent(playerStart.transform, true);
            worker.transform.position = PlayerStartPos + WorkerOffset;
            Undo.RegisterCreatedObjectUndo(worker, "Spawn Player Worker");
        }
        else
        {
            Debug.LogWarning("[MatchSetup]   WorkerPrefab.prefab missing — player has no starting Worker. " +
                             "Run Tools → RTS → Setup Command Center to bootstrap one.");
        }

        return playerCC;
    }

    /// <summary>
    /// Builds a minimal player CommandCenter when the scene doesn't already
    /// have one. Mirrors the shape SetupCommandCenter expects: cube body +
    /// CommandCenter + SelectableBuilding + CommandCenterProducer + Health +
    /// UnitCategory.Building, on the Building layer.
    /// </summary>
    private static CommandCenter CreatePlayerCommandCenter(int buildingLayer)
    {
        GameObject ccGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ccGO.name = "CommandCenter";
        ccGO.transform.localScale = new Vector3(3f, 2f, 3f);
        ccGO.transform.position   = PlayerStartPos + new Vector3(0f, 1f, 0f);
        ccGO.layer = buildingLayer;

        Renderer r = ccGO.GetComponent<Renderer>();
        if (r != null)
            ApplyColor(r, CCPlayerBlue);

        ccGO.AddComponent<CommandCenter>();
        ccGO.AddComponent<SelectableBuilding>();
        ccGO.AddComponent<CommandCenterProducer>();

        Health hp = ccGO.AddComponent<Health>();
        hp.team   = Health.Team.Player;
        hp.maxHealth = 1500f;

        UnitCategory cat = ccGO.AddComponent<UnitCategory>();
        cat.category = UnitCategory.Category.Building;

        return ccGO.GetComponent<CommandCenter>();
    }

    // ================================================================== //
    // 4. Enemy base — fresh build every run, primitive visual.
    // ================================================================== //

    private static GameObject BuildEnemyBase(int buildingLayer, int unitLayer)
    {
        GameObject enemyStart = new GameObject("EnemyStart");
        Undo.RegisterCreatedObjectUndo(enemyStart, "Create EnemyStart");
        enemyStart.transform.position = EnemyStartPos;

        // 4a — enemy CommandCenter visual. Same dimensions as the player CC
        // but RED, Health.team = Enemy, NO SelectableBuilding (per spec).
        GameObject enemyCC = GameObject.CreatePrimitive(PrimitiveType.Cube);
        enemyCC.name = "EnemyCommandCenter";
        enemyCC.transform.SetParent(enemyStart.transform, false);
        enemyCC.transform.localPosition = new Vector3(0f, 1f, 0f);
        enemyCC.transform.localScale    = new Vector3(3f, 2f, 3f);
        enemyCC.layer                   = buildingLayer;

        Renderer r = enemyCC.GetComponent<Renderer>();
        if (r != null)
            ApplyColor(r, CCEnemyRed);

        // Building marker + collider (auto). NO CommandCenter component (only
        // the player uses it to deposit resources), NO CommandCenterProducer
        // (no enemy production yet), NO SelectableBuilding (player can't
        // click-select enemy buildings). EnemyCommandCenter is the enemy-side
        // deposit endpoint, parallel sibling to CommandCenter on the player.
        Building b   = enemyCC.AddComponent<Building>();
        b.buildingName = "Enemy CommandCenter";
        b.cost         = 0;

        enemyCC.AddComponent<EnemyCommandCenter>();

        // Attach the build bot. It auto-starts on Play, spends from
        // EnemyResourceManager, and drops the three set-dressing buildings
        // around the CC. Toggle off via the component's enemyBuildAIEnabled
        // field in the Inspector for clean economy-only tests.
        enemyCC.AddComponent<EnemyBuildAI>();

        Health hp = enemyCC.AddComponent<Health>();
        hp.team   = Health.Team.Enemy;
        hp.maxHealth = 1500f;

        UnitCategory cat = enemyCC.AddComponent<UnitCategory>();
        cat.category = UnitCategory.Category.Building;

        AttachHealthBar(enemyCC, heightOffset: 2.4f);
        Undo.RegisterCreatedObjectUndo(enemyCC, "Spawn Enemy CommandCenter");

        // 4b — enemy Worker. Spawn the WorkerPrefab, then convert team + strip
        // WorkerGatherer so it doesn't try to gather for the PLAYER's resource
        // manager. Spec says enemy worker doesn't need to gather yet.
        GameObject workerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(WorkerPrefabPath);
        if (workerPrefab != null)
        {
            GameObject worker = (GameObject)PrefabUtility.InstantiatePrefab(workerPrefab);
            worker.name = "EnemyWorker";
            worker.transform.SetParent(enemyStart.transform, true);
            worker.transform.position = EnemyStartPos - WorkerOffset;
            ConvertWorkerToEnemy(worker);
            Undo.RegisterCreatedObjectUndo(worker, "Spawn Enemy Worker");
        }
        else
        {
            Debug.LogWarning("[MatchSetup]   WorkerPrefab.prefab missing — no enemy worker spawned.");
        }

        // NOTE: NO Enemy Dozer at match start. EnemyBuildAI must EARN 150
        // resources via EnemyWorkerAI gathering, then produce its own Dozer
        // before any construction begins. Same code path handles replacement
        // after the dozer is destroyed.

        return enemyCC;
    }

    /// <summary>
    /// Turns a freshly-instantiated player Worker into a passive enemy worker:
    ///   • Health.team → Enemy
    ///   • WorkerGatherer removed (enemy doesn't gather yet)
    ///   • SelectableUnit removed (player can't select enemy units)
    ///   • UnitColorMarker / TeamColorMarker removed (don't repaint with player color)
    ///   • Apply enemy-red material to every body renderer for clarity
    /// </summary>
    private static void ConvertWorkerToEnemy(GameObject worker)
    {
        Health hp = worker.GetComponent<Health>();
        if (hp != null) hp.team = Health.Team.Enemy;

        WorkerGatherer wg = worker.GetComponent<WorkerGatherer>();
        if (wg != null) Object.DestroyImmediate(wg);

        SelectableUnit su = worker.GetComponent<SelectableUnit>();
        if (su != null) Object.DestroyImmediate(su);

        // Strip team-color components — these would otherwise paint the enemy
        // worker with the PLAYER's selected army color on Awake.
        TeamColorMarker tcm = worker.GetComponent<TeamColorMarker>();
        if (tcm != null) Object.DestroyImmediate(tcm);

        TeamColorApplier tca = worker.GetComponent<TeamColorApplier>();
        if (tca != null) Object.DestroyImmediate(tca);

        UnitColorMarker ucm = worker.GetComponent<UnitColorMarker>();
        if (ucm != null) Object.DestroyImmediate(ucm);

        // Paint every body renderer red so the enemy worker is visually obvious.
        foreach (Renderer r in worker.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            if (r == null) continue;
            if (r is LineRenderer) continue;
            ApplyColor(r, EnemyWorker);
        }

        // Attach the enemy gather-loop AI. It auto-starts on Play and walks
        // the existing UnitMovement + NavMeshAgent — no extra setup needed.
        if (worker.GetComponent<EnemyWorkerAI>() == null)
            worker.AddComponent<EnemyWorkerAI>();
    }

    // ================================================================== //
    // 5. Resources
    // ================================================================== //

    private static void BuildResources(int resourceLayer)
    {
        GameObject root = new GameObject("ResourceNodes");
        Undo.RegisterCreatedObjectUndo(root, "Create ResourceNodes");

        foreach (Vector3 offset in PlayerResourceOffsets)
            CreateResourceNode(root.transform, PlayerStartPos + offset, resourceLayer);

        foreach (Vector3 offset in EnemyResourceOffsets)
            CreateResourceNode(root.transform, EnemyStartPos + offset, resourceLayer);

        foreach (Vector3 pos in NeutralResourcePositions)
            CreateResourceNode(root.transform, pos, resourceLayer);
    }

    private static void CreateResourceNode(Transform parent, Vector3 worldPos, int resourceLayer)
    {
        GameObject node = GameObject.CreatePrimitive(PrimitiveType.Cube);
        node.name = "ResourceNode";
        node.transform.SetParent(parent, false);
        node.transform.position   = new Vector3(worldPos.x, 0.5f, worldPos.z);
        node.transform.localScale = new Vector3(1.4f, 1.0f, 1.4f);
        node.layer                = resourceLayer;

        Renderer r = node.GetComponent<Renderer>();
        if (r != null) ApplyColor(r, ResourceGold);

        node.AddComponent<ResourceNode>();
        // BoxCollider is auto-added by CreatePrimitive; that's the WorkerGatherer
        // raycast surface.
    }

    // ================================================================== //
    // 6. MatchManager
    // ================================================================== //

    private static MatchManager EnsureMatchManager()
    {
        MatchManager existing = Object.FindAnyObjectByType<MatchManager>(FindObjectsInactive.Include);
        if (existing != null) return existing;

        GameObject go = new GameObject("MatchManager");
        Undo.RegisterCreatedObjectUndo(go, "Create MatchManager");
        return go.AddComponent<MatchManager>();
    }

    /// <summary>
    /// Guarantees the scene has exactly one <see cref="EnemyResourceManager"/>.
    /// Reuses an existing instance if present; otherwise attaches one to
    /// <paramref name="host"/> (the MatchManager GameObject) so the scene
    /// hierarchy stays clean.
    /// </summary>
    private static void EnsureEnemyResourceManager(GameObject host)
    {
        EnemyResourceManager existing = Object.FindAnyObjectByType<EnemyResourceManager>(FindObjectsInactive.Include);
        if (existing != null) return;

        Undo.AddComponent<EnemyResourceManager>(host);
        Debug.Log("[MatchSetup]   Added EnemyResourceManager to MatchManager.");
    }

    // ================================================================== //
    // 7. Camera
    // ================================================================== //

    private static void AimCameraAtPlayerStart()
    {
        RTSCamera rig = Object.FindAnyObjectByType<RTSCamera>(FindObjectsInactive.Include);
        if (rig == null)
        {
            Debug.LogWarning("[MatchSetup]   No RTSCamera in scene — skipping camera placement.");
            return;
        }

        Undo.RecordObject(rig.transform, "Move CameraRig");
        rig.transform.position = new Vector3(PlayerStartPos.x, 20f, PlayerStartPos.z - 18f);
    }

    // ================================================================== //
    // 8. NavMesh
    // ================================================================== //

    private static void BakeNavMeshOnEnvironment(GameObject env)
    {
        if (env == null)
        {
            Debug.LogWarning("[MatchSetup] Please bake NavMesh after setup.");
            return;
        }

        NavMeshSurface surface = env.GetComponent<NavMeshSurface>();
        if (surface == null) surface = env.AddComponent<NavMeshSurface>();

        // Collect every renderer/collider under the Environment root — ground
        // counts as walkable, mountains as obstacles via their colliders.
        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry    = NavMeshCollectGeometry.PhysicsColliders;

        try
        {
            surface.BuildNavMesh();
            EditorUtility.SetDirty(surface);
            Debug.Log("[MatchSetup] NavMesh baked via NavMeshSurface.BuildNavMesh().");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[MatchSetup] NavMesh bake failed: {ex.Message}\n" +
                             "[MatchSetup] Please bake NavMesh after setup " +
                             "(Tools → RTS → Environment → Rebuild NavMesh And Snap Units).");
        }
    }

    // ================================================================== //
    // Shared helpers
    // ================================================================== //

    private static bool ResolveLayers(out int ground, out int unit, out int building, out int resource)
    {
        ground   = LayerMask.NameToLayer(GroundLayerName);
        unit     = LayerMask.NameToLayer(UnitLayerName);
        building = LayerMask.NameToLayer(BuildingLayerName);
        resource = LayerMask.NameToLayer(ResourceLayerName);

        bool ok = true;
        if (ground   < 0) { Debug.LogError($"[MatchSetup] ✗ Layer '{GroundLayerName}' missing.");   ok = false; }
        if (unit     < 0) { Debug.LogError($"[MatchSetup] ✗ Layer '{UnitLayerName}' missing.");     ok = false; }
        if (building < 0) { Debug.LogError($"[MatchSetup] ✗ Layer '{BuildingLayerName}' missing."); ok = false; }
        if (resource < 0) { Debug.LogError($"[MatchSetup] ✗ Layer '{ResourceLayerName}' missing."); ok = false; }

        if (!ok)
            Debug.LogError("[MatchSetup] Fix in Edit → Project Settings → Tags and Layers, then re-run.");
        return ok;
    }

    private static GameObject MakeChild(GameObject parent, string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    private static float DistanceXZ(float x, float z, Vector3 other)
    {
        float dx = x - other.x;
        float dz = z - other.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static Material CreateLitMaterial(string name, Color color)
    {
        Shader shader = ResolveLitShader();
        Material m = new Material(shader) { name = name, color = color };
        if (m.HasProperty("_BaseColor"))
            m.SetColor("_BaseColor", color);
        return m;
    }

    private static void ApplyColor(Renderer r, Color color)
    {
        Material m = new Material(ResolveLitShader()) { color = color };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        r.sharedMaterial = m;
        r.shadowCastingMode = ShadowCastingMode.On;
    }

    private static void AttachHealthBar(GameObject owner, float heightOffset)
    {
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(owner.transform, false);
        HealthBar hb = bar.AddComponent<HealthBar>();
        hb.heightOffset = heightOffset;
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
