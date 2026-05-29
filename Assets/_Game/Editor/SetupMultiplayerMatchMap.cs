using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click editor tool that prepares the active scene for a 1–4 player Photon
/// match. Builds FOUR corner starting areas (A / B / C / D), each with a
/// visible start marker, a starting Dozer/builder, a resource cluster, and a
/// per-owner resource bank, all under one clearly-named root in the Hierarchy:
///
///   GameplayWorldRoot
///     CornerBases
///       CornerBase_A
///         StartMarker_A   Dozer_A   ResourceCluster_A   Bank_A
///       CornerBase_B  …  CornerBase_C  …  CornerBase_D
///
/// Menu: Tools → RTS → Match → Setup Multiplayer Match Map
///
/// DESIGN: the corner is a FIXED location with a FIXED cornerIndex. It is NOT
/// permanently owned — at match start the coordinator assigns each active
/// player to a corner and reveals only the assigned ones (unused corners stay
/// hidden). See <see cref="CornerBase"/>. In the EDITOR all four corners are
/// always visible so you can verify the layout without pressing Play.
///
/// VISIBILITY + VALIDATION: everything is created in the OPEN scene (not on
/// prefabs), placed on the ground via a downward raycast, registered with Undo,
/// dirtied, and the scene is marked dirty. The tool prints a full report and
/// runs validation automatically. Missing prefabs DON'T fail silently — the
/// tool logs exactly what was missing and drops a coloured placeholder cube so
/// the corner is still visible/verifiable.
///
/// Re-running is safe: it removes the previous GameplayWorldRoot/CornerBases
/// (and any legacy Player0Base/Player1Base) and rebuilds from scratch.
///
/// After running, run <c>Tools → RTS → Multiplayer Prep → Add GameEntity To
/// Scene Objects</c> so the new units/resources get deterministic ids.
/// </summary>
public static class SetupMultiplayerMatchMap
{
    // ------------------------------------------------------------------ //
    // Layout constants
    // ------------------------------------------------------------------ //

    // Fallback corner coordinates (X,Z). Y is resolved per-corner by raycasting
    // down onto the ground/terrain. A=bottom-left, B=bottom-right, C=top-left,
    // D=top-right.
    public static readonly Vector3[] CornerPositions =
    {
        new Vector3(-80f, 0f, -70f), // A (index 0)
        new Vector3( 80f, 0f, -70f), // B (index 1)
        new Vector3(-80f, 0f,  70f), // C (index 2)
        new Vector3( 80f, 0f,  70f), // D (index 3)
    };

    private static readonly Color[] CornerColors =
    {
        new Color(0.20f, 0.55f, 1.00f), // A blue
        new Color(0.92f, 0.20f, 0.20f), // B red
        new Color(0.30f, 0.80f, 0.35f), // C green
        new Color(0.95f, 0.80f, 0.20f), // D yellow
    };

    private static readonly Color ResourceColor = new Color(0.85f, 0.78f, 0.20f); // gold

    // Back-compat: a couple of older callers still read these two. Map them to
    // the diagonal corners A and D so existing camera wiring keeps working.
    public static Vector3 Player0BasePos => CornerPositions[0];
    public static Vector3 Player1BasePos => CornerPositions[3];

    private const int   StartingResources    = 1000;
    private const int   ResourceNodesPerBase = 4;
    private const float ResourceNodeRadius   = 12f;
    private const string DozerPrefabPath     = "Assets/_Game/Prefabs/DozerPrefab.prefab";

    private static char Letter(int i) => (char)('A' + i);

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Match/Setup Multiplayer Match Map")]
    public static void Run()
    {
        var report = new List<string>();

        UnityEngine.SceneManagement.Scene scene = EditorSceneManager.GetActiveScene();
        Debug.Log("[MultiplayerMatch] ════════════════════════════════════════════════");
        Debug.Log($"[MultiplayerMatch] Building 4-corner match layout in scene: " +
                  $"'{scene.name}' ({(string.IsNullOrEmpty(scene.path) ? "UNSAVED scene" : scene.path)}).");

        // ---- Scene sanity check ---------------------------------------- //
        bool hasGameManager = GameObject.Find("GameManager") != null;
        bool hasGround      = SceneHasGround();
        if (!hasGameManager)
            Debug.LogWarning("[MultiplayerMatch] ⚠ No 'GameManager' object found in this scene. " +
                             "This may be the WRONG scene (the gameplay scene normally has a " +
                             "GameManager). The tool will still build the corners, but the " +
                             "match-starter / BPM wiring may be incomplete. Open your gameplay " +
                             "scene if this looks wrong.");
        if (!hasGround)
            Debug.LogWarning("[MultiplayerMatch] ⚠ No ground/terrain collider detected. Corner " +
                             "objects will be placed at Y=0 (raycast-to-ground found nothing). " +
                             "If your map has a ground plane, make sure it has a Collider.");

        int buildingLayer = LayerMask.NameToLayer("Building");
        int unitLayer     = LayerMask.NameToLayer("Unit");
        int resourceLayer = LayerMask.NameToLayer("Resource");
        if (unitLayer < 0)     Debug.LogWarning("[MultiplayerMatch] ⚠ 'Unit' layer not defined.");
        if (resourceLayer < 0) Debug.LogWarning("[MultiplayerMatch] ⚠ 'Resource' layer not defined.");
        _ = buildingLayer;

        // ---- Load the Dozer prefab (clearly report if missing) --------- //
        GameObject dozerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DozerPrefabPath);
        if (dozerPrefab == null)
            Debug.LogWarning($"[MultiplayerMatch] ⚠ Dozer prefab NOT FOUND at '{DozerPrefabPath}'. " +
                             "Each corner will get a coloured PLACEHOLDER cube instead. Run " +
                             "Tools → RTS → Construction → Create Dozer Prefab to get real Dozers.");
        else
            Debug.Log($"[MultiplayerMatch] ✓ Dozer prefab found at '{DozerPrefabPath}'.");

        // ---- Remove any previous layout (idempotent) ------------------- //
        RemoveIfExists("GameplayWorldRoot");
        RemoveIfExists("Player0Base");   // legacy 2-base layout
        RemoveIfExists("Player1Base");

        // ---- Build the visible root hierarchy -------------------------- //
        GameObject worldRoot = new GameObject("GameplayWorldRoot");
        Undo.RegisterCreatedObjectUndo(worldRoot, "Create GameplayWorldRoot");
        worldRoot.transform.position = Vector3.zero;
        worldRoot.SetActive(true);     // ALWAYS visible in edit mode

        GameObject cornerBasesRoot = new GameObject("CornerBases");
        cornerBasesRoot.transform.SetParent(worldRoot.transform, false);

        // ---- Build the four corners ------------------------------------ //
        for (int i = 0; i < 4; i++)
        {
            try
            {
                BuildCornerBase(i, cornerBasesRoot.transform, dozerPrefab,
                                unitLayer, resourceLayer, report);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[MultiplayerMatch] ✗ Corner {Letter(i)} build threw: " +
                               $"{e.Message}\n{e.StackTrace}");
                report.Add($"  • Corner {Letter(i)}: FAILED — {e.Message}");
            }
        }

        // ---- Supporting wiring ----------------------------------------- //
        DisableEnemyBots();
        EnsureMatchStarterAndWorldRoot(worldRoot);
        BindCommandCenterPrefabOntoBPM();

        // ---- Persist --------------------------------------------------- //
        EditorUtility.SetDirty(worldRoot);
        EditorSceneManager.MarkSceneDirty(scene);

        // ---- Report + validate ----------------------------------------- //
        Debug.Log("[MultiplayerMatch] ─────────────── SETUP REPORT ───────────────");
        foreach (string line in report) Debug.Log("[MultiplayerMatch] " + line);
        Debug.Log("[MultiplayerMatch]   • Scene marked dirty (press Ctrl+S to save).");
        Debug.Log("[MultiplayerMatch] ────────────────────────────────────────────");

        bool ok = ValidateInternal(logHeader: true);

        Debug.Log("[MultiplayerMatch] ════════════════════════════════════════════════");
        Debug.Log(ok
            ? "[MultiplayerMatch] ✓ Setup completed successfully. Look for 'GameplayWorldRoot' " +
              "in the Hierarchy (CornerBases → CornerBase_A/B/C/D). Next: run " +
              "Tools → RTS → Multiplayer Prep → Add GameEntity To Scene Objects."
            : "[MultiplayerMatch] ✗ Setup completed WITH WARNINGS — see the validation lines above.");
        Debug.Log("[MultiplayerMatch] ════════════════════════════════════════════════");

        Selection.activeGameObject = worldRoot;     // focus it in the Hierarchy
        EditorGUIUtility.PingObject(worldRoot);
    }

    // ================================================================== //
    // Corner builder
    // ================================================================== //

    private static void BuildCornerBase(int cornerIndex, Transform parent,
                                        GameObject dozerPrefab,
                                        int unitLayer, int resourceLayer,
                                        List<string> report)
    {
        char letter = Letter(cornerIndex);
        Color color = CornerColors[cornerIndex];

        // Resolve the ground height at this corner BEFORE placing anything.
        Vector3 flat = CornerPositions[cornerIndex];
        float groundY = ResolveGroundY(flat, out bool hitGround);
        Vector3 cornerPos = new Vector3(flat.x, groundY, flat.z);

        GameObject cornerGO = new GameObject($"CornerBase_{letter}");
        cornerGO.transform.SetParent(parent, false);
        cornerGO.transform.position = cornerPos;
        Undo.RegisterCreatedObjectUndo(cornerGO, $"Create CornerBase_{letter}");

        CornerBase cb = cornerGO.AddComponent<CornerBase>();
        cb.cornerIndex     = cornerIndex;
        cb.assignedOwnerId = -1;

        // --- Start marker (always-visible coloured flag) ----------------- //
        Transform marker = BuildStartMarker(cornerGO.transform, cornerPos, letter, color);
        cb.startMarker = marker;

        // --- Bank (per-owner PlayerResourceManager) ---------------------- //
        GameObject bankGO = new GameObject($"Bank_{letter}");
        bankGO.transform.SetParent(cornerGO.transform, false);
        bankGO.transform.position = cornerPos;
        PlayerResourceManager bank = bankGO.AddComponent<PlayerResourceManager>();
        bank.ownerPlayerId     = cornerIndex;   // placeholder; reassigned at match start
        bank.startingResources = StartingResources;
        EditorUtility.SetDirty(bank);
        cb.bank = bank;

        // --- Dozer (real prefab or visible placeholder) ------------------ //
        bool dozerIsReal;
        GameObject dozer = BuildDozer(cornerGO.transform, cornerPos, cornerIndex,
                                      dozerPrefab, unitLayer, color, out dozerIsReal);
        cb.dozer = dozer;

        // --- Resource cluster -------------------------------------------- //
        Transform cluster = BuildResourceCluster(cornerGO.transform, cornerPos,
                                                 letter, resourceLayer);
        cb.resourceCluster = cluster;

        EditorUtility.SetDirty(cb);
        EditorUtility.SetDirty(cornerGO);

        string yNote = hitGround ? $"Y={groundY:F1} (on ground)" : "Y=0 (NO ground hit)";
        report.Add($"  • Created CornerBase_{letter} at ({cornerPos.x:F0}, {cornerPos.y:F0}, " +
                   $"{cornerPos.z:F0}) [{yNote}] — " +
                   $"Dozer={(dozerIsReal ? "real prefab" : "PLACEHOLDER cube")}, " +
                   $"{ResourceNodesPerBase} resource node(s), Bank owner {cornerIndex}.");

        Debug.Log($"[MultiplayerMatch] ✓ CornerBase_{letter} built at {cornerPos:F1} " +
                  $"(dozer {(dozerIsReal ? "real" : "placeholder")}).");
    }

    private static Transform BuildStartMarker(Transform parent, Vector3 pos, char letter, Color color)
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        marker.name = $"StartMarker_{letter}";
        marker.transform.SetParent(parent, true);
        marker.transform.position   = pos + Vector3.up * 2.5f;
        marker.transform.localScale = new Vector3(1.2f, 2.5f, 1.2f);

        // Purely visual — strip the collider so it never blocks NavMesh / clicks.
        Collider col = marker.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col);

        Renderer rend = marker.GetComponent<Renderer>();
        if (rend != null)
        {
            Material m = new Material(rend.sharedMaterial) { color = color };
            rend.sharedMaterial = m;
        }
        return marker.transform;
    }

    private static GameObject BuildDozer(Transform parent, Vector3 cornerPos, int cornerIndex,
                                         GameObject dozerPrefab, int unitLayer, Color color,
                                         out bool isReal)
    {
        char letter = Letter(cornerIndex);
        Vector3 dozerPos = cornerPos + new Vector3(0f, 0f, 4f);

        GameObject d;
        if (dozerPrefab != null)
        {
            d = (GameObject)PrefabUtility.InstantiatePrefab(dozerPrefab);
            d.transform.SetParent(parent, true);
            d.transform.position = dozerPos;
            isReal = true;
        }
        else
        {
            // Visible placeholder so the corner is still verifiable.
            d = GameObject.CreatePrimitive(PrimitiveType.Cube);
            d.transform.SetParent(parent, true);
            d.transform.position   = dozerPos + Vector3.up * 1f;
            d.transform.localScale = new Vector3(3f, 2f, 4f);
            Renderer rend = d.GetComponent<Renderer>();
            if (rend != null)
            {
                Material m = new Material(rend.sharedMaterial) { color = color * 0.8f };
                rend.sharedMaterial = m;
            }
            Debug.LogWarning($"[MultiplayerMatch]   Missing Dozer prefab — created PLACEHOLDER " +
                             $"cube for corner {letter} instead.");
            isReal = false;
        }

        d.name = $"Dozer_{letter}";
        if (unitLayer >= 0)
        {
            d.layer = unitLayer;
            foreach (Transform t in d.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = unitLayer;
        }

        // Ownership stamp — placeholder owner = cornerIndex; reassigned at match
        // start via CornerBase.AssignOwner. overrideTeamFromHealth=false keeps
        // the explicit owner from being re-derived by the scene stamper.
        GameEntity ge = d.GetComponent<GameEntity>();
        if (ge == null) ge = d.AddComponent<GameEntity>();
        ge.ownerPlayerId          = cornerIndex;
        ge.teamId                 = cornerIndex;
        ge.entityType             = EntityType.Unit;
        ge.prefabTypeId           = isReal ? "Dozer" : "DozerPlaceholder";
        ge.overrideTeamFromHealth = false;
        EditorUtility.SetDirty(ge);
        EditorUtility.SetDirty(d);
        return d;
    }

    private static Transform BuildResourceCluster(Transform parent, Vector3 cornerPos,
                                                  char letter, int resourceLayer)
    {
        GameObject cluster = new GameObject($"ResourceCluster_{letter}");
        cluster.transform.SetParent(parent, false);
        cluster.transform.position = cornerPos;

        for (int i = 0; i < ResourceNodesPerBase; i++)
        {
            float angle = i * Mathf.PI * 2f / ResourceNodesPerBase;
            Vector3 offset = new Vector3(
                Mathf.Cos(angle) * ResourceNodeRadius, 0f,
                Mathf.Sin(angle) * ResourceNodeRadius);
            Vector3 flat = cornerPos + offset;
            float y = ResolveGroundY(flat, out _);
            Vector3 pos = new Vector3(flat.x, y + 1f, flat.z);

            GameObject nodeGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nodeGO.name = $"ResourceNode_{i}";
            nodeGO.transform.SetParent(cluster.transform, true);
            nodeGO.transform.position   = pos;
            nodeGO.transform.localScale = new Vector3(2f, 2f, 2f);
            if (resourceLayer >= 0) nodeGO.layer = resourceLayer;

            Renderer rend = nodeGO.GetComponent<Renderer>();
            if (rend != null)
            {
                Material m = new Material(rend.sharedMaterial) { color = ResourceColor };
                rend.sharedMaterial = m;
            }

            nodeGO.AddComponent<ResourceNode>();   // self-initialises from defaults

            GameEntity ge = nodeGO.AddComponent<GameEntity>();
            ge.ownerPlayerId          = GameEntity.NeutralOwnerId;
            ge.teamId                 = GameEntity.NeutralOwnerId;
            ge.entityType             = EntityType.Resource;
            ge.prefabTypeId           = "ResourceNode";
            ge.overrideTeamFromHealth = false;
            EditorUtility.SetDirty(ge);
        }
        return cluster.transform;
    }

    // ================================================================== //
    // Ground raycast
    // ================================================================== //

    /// <summary>
    /// Raycast straight down onto whatever collider sits under
    /// <paramref name="flatXZ"/>. Returns the hit Y (or 0 if nothing was hit).
    /// Works in edit mode as long as the ground has a Collider.
    /// </summary>
    private static float ResolveGroundY(Vector3 flatXZ, out bool hit)
    {
        Physics.SyncTransforms();
        Vector3 origin = new Vector3(flatXZ.x, 500f, flatXZ.z);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit info, 2000f,
                            ~0, QueryTriggerInteraction.Ignore))
        {
            hit = true;
            return info.point.y;
        }
        hit = false;
        return 0f;
    }

    private static bool SceneHasGround()
    {
        // A Terrain or any collider near the map origin counts as "has ground".
        if (Object.FindFirstObjectByType<Terrain>(FindObjectsInactive.Include) != null)
            return true;
        Physics.SyncTransforms();
        return Physics.Raycast(new Vector3(0f, 500f, 0f), Vector3.down, 2000f,
                               ~0, QueryTriggerInteraction.Ignore);
    }

    // ================================================================== //
    // Validation
    // ================================================================== //

    [MenuItem("Tools/RTS/Match/Validate 4 Player Map Setup")]
    public static void ValidateMenu()
    {
        Debug.Log("[MultiplayerMatch] ───────── VALIDATE 4-PLAYER MAP SETUP ─────────");
        bool ok = ValidateInternal(logHeader: false);
        Debug.Log(ok
            ? "[MultiplayerMatch] ✓ Validation PASSED — 4 corner bases present and wired."
            : "[MultiplayerMatch] ✗ Validation FAILED — re-run Tools → RTS → Match → Setup " +
              "Multiplayer Match Map (see the ✗ lines above).");
        Debug.Log("[MultiplayerMatch] ──────────────────────────────────────────────");
    }

    private static bool ValidateInternal(bool logHeader)
    {
        if (logHeader)
            Debug.Log("[MultiplayerMatch] ─────────────── VALIDATION ───────────────");

        bool ok = true;

        GameObject worldRoot = GameObject.Find("GameplayWorldRoot");
        if (worldRoot == null)
        {
            Debug.LogError("[MultiplayerMatch] ✗ No 'GameplayWorldRoot' object in the scene.");
            return false;
        }
        Debug.Log("[MultiplayerMatch] ✓ Found GameplayWorldRoot.");

        Transform cornerBases = worldRoot.transform.Find("CornerBases");
        if (cornerBases == null)
        {
            Debug.LogError("[MultiplayerMatch] ✗ No 'CornerBases' child under GameplayWorldRoot.");
            return false;
        }
        Debug.Log("[MultiplayerMatch] ✓ Found CornerBases.");

        CornerBase[] corners = worldRoot.GetComponentsInChildren<CornerBase>(true);
        if (corners.Length != 4)
        {
            Debug.LogError($"[MultiplayerMatch] ✗ Expected 4 CornerBase objects, found {corners.Length}.");
            ok = false;
        }

        var seenIndices = new HashSet<int>();
        for (int i = 0; i < corners.Length; i++)
        {
            CornerBase cb = corners[i];
            if (cb == null) continue;

            string n = cb.gameObject.name;
            if (!seenIndices.Add(cb.cornerIndex))
            {
                Debug.LogError($"[MultiplayerMatch] ✗ {n}: duplicate cornerIndex {cb.cornerIndex}.");
                ok = false;
            }
            if (cb.cornerIndex < 0 || cb.cornerIndex > 3)
            {
                Debug.LogError($"[MultiplayerMatch] ✗ {n}: cornerIndex {cb.cornerIndex} out of range 0..3.");
                ok = false;
            }

            bool active   = cb.gameObject.activeInHierarchy;
            bool hasMarker = cb.startMarker != null;
            bool hasDozer  = cb.dozer != null;
            bool hasBank   = cb.bank != null;
            int  resCount  = cb.resourceCluster != null
                ? cb.resourceCluster.GetComponentsInChildren<ResourceNode>(true).Length : 0;

            if (!hasMarker) { Debug.LogError($"[MultiplayerMatch] ✗ {n}: missing StartMarker."); ok = false; }
            if (!hasDozer)  { Debug.LogError($"[MultiplayerMatch] ✗ {n}: missing Dozer.");        ok = false; }
            if (!hasBank)   { Debug.LogError($"[MultiplayerMatch] ✗ {n}: missing Bank.");         ok = false; }
            if (resCount == 0) { Debug.LogError($"[MultiplayerMatch] ✗ {n}: no ResourceNodes.");  ok = false; }
            if (!active)    Debug.LogWarning($"[MultiplayerMatch] ⚠ {n}: not active in editor (should be visible).");

            Vector3 p = cb.transform.position;
            Debug.Log($"[MultiplayerMatch]   {n} (Corner {cb.Letter}, index {cb.cornerIndex}) @ " +
                      $"({p.x:F0}, {p.y:F0}, {p.z:F0}) — marker={Yn(hasMarker)} dozer={Yn(hasDozer)} " +
                      $"bank={Yn(hasBank)} resources={resCount} active={Yn(active)}");
        }

        return ok;
    }

    private static string Yn(bool b) => b ? "yes" : "NO";

    // ================================================================== //
    // Enemy AI off (multiplayer)
    // ================================================================== //

    private static void DisableEnemyBots()
    {
        int disabled = 0;
        disabled += DisableInScene<EnemyBuildAI>();
        disabled += DisableInScene<EnemyWorkerAI>();
        disabled += DisableInScene<EnemyResourceManager>();
        disabled += DisableInScene<EnemyAIController>();
        disabled += DisableInScene<EnemyWaveSpawner>();

        GameObject enemyStart = GameObject.Find("EnemyStart");
        if (enemyStart != null && enemyStart.activeSelf)
        {
            enemyStart.SetActive(false);
            Debug.Log("[MultiplayerMatch]   EnemyStart root deactivated (not deleted).");
        }
        Debug.Log($"[MultiplayerMatch]   Disabled {disabled} enemy AI script(s).");
    }

    private static int DisableInScene<T>() where T : Behaviour
    {
        T[] all = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        int n = 0;
        foreach (T comp in all)
        {
            if (comp == null || !comp.enabled) continue;
            comp.enabled = false;
            EditorUtility.SetDirty(comp);
            n++;
        }
        return n;
    }

    // ================================================================== //
    // MatchStarter + GameplayWorldRoot (runtime hide/reveal) wiring
    // ================================================================== //

    private static void EnsureMatchStarterAndWorldRoot(GameObject worldRoot)
    {
        GameObject gm = GameObject.Find("GameManager");
        if (gm == null)
        {
            gm = new GameObject("GameManager");
            Undo.RegisterCreatedObjectUndo(gm, "Create GameManager");
            Debug.LogWarning("[MultiplayerMatch] No GameManager in scene — created one.");
        }

        MultiplayerMatchStarter starter = gm.GetComponent<MultiplayerMatchStarter>();
        if (starter == null) starter = gm.AddComponent<MultiplayerMatchStarter>();
        starter.player0CameraPos = CornerPositions[0];
        starter.player1CameraPos = CornerPositions[3];
        EditorUtility.SetDirty(starter);

        // GameplayWorldRoot COMPONENT hides the match world until MatchStart at
        // RUNTIME (its Awake). It does NOT touch edit mode, so all four corners
        // stay visible in the editor. Point it at the whole container so the
        // entire match world (all corners) hides until the match begins.
        GameplayWorldRoot hider = gm.GetComponent<GameplayWorldRoot>();
        if (hider == null) hider = gm.AddComponent<GameplayWorldRoot>();

        var targets = new List<GameObject> { worldRoot };
        GameObject env = GameObject.Find("Environment");
        if (env != null) targets.Add(env);
        hider.targets = targets.ToArray();
        hider.autoDiscoverNames = new[] { "GameplayWorldRoot", "Environment", "ResourceNodes" };
        EditorUtility.SetDirty(hider);

        Debug.Log($"[MultiplayerMatch]   MatchStarter + GameplayWorldRoot wired on GameManager " +
                  $"(hides {targets.Count} root(s) at runtime until MatchStart; visible in editor).");
    }

    // ================================================================== //
    // BPM auto-binding (Dozer needs a CommandCenterPrefab to build)
    // ================================================================== //

    private static void BindCommandCenterPrefabOntoBPM()
    {
        BuildingPlacementManager bpm = Object.FindFirstObjectByType<BuildingPlacementManager>(
            FindObjectsInactive.Include);
        if (bpm == null)
        {
            Debug.LogWarning("[MultiplayerMatch] No BuildingPlacementManager in scene — Dozer " +
                             "'Command Center' button won't work at runtime. Run the single-player " +
                             "Setup Clean Match Map first to create GameManager + BPM.");
            return;
        }

        GameObject ccPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            CreateCommandCenterPrefab.PrefabPath);
        if (ccPrefab == null)
        {
            Debug.LogError($"[MultiplayerMatch] ✗ {CreateCommandCenterPrefab.PrefabPath} not found. " +
                           "Run Tools → RTS → Construction → Create CommandCenter Prefab first.");
            return;
        }

        bpm.commandCenterPrefab = ccPrefab;
        EditorUtility.SetDirty(bpm);
        Debug.Log($"[MultiplayerMatch]   BuildingPlacementManager.commandCenterPrefab = '{ccPrefab.name}'.");
    }

    // ================================================================== //
    // Helpers
    // ================================================================== //

    private static void RemoveIfExists(string name)
    {
        GameObject go = GameObject.Find(name);
        if (go != null)
        {
            Undo.DestroyObjectImmediate(go);
            Debug.Log($"[MultiplayerMatch]   Removed previous '{name}' — rebuilding.");
        }
    }
}
