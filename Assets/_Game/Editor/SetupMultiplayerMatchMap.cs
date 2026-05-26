using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click editor tool that prepares the active scene for a 1-vs-1 Photon
/// match. Creates two player bases on opposite corners of the map, each with
/// its own CommandCenter + Worker + nearby ResourceNodes + per-owner
/// <see cref="PlayerResourceManager"/>. Also disables enemy AI roots so the
/// AI bot doesn't run in multiplayer.
///
/// Menu: Tools → RTS → Match → Setup Multiplayer Match Map
///
/// Single-player tool (<c>Setup Clean Match Map</c>) is unchanged — both
/// tools coexist; use whichever matches your scene's intended mode.
///
/// What this tool DOES NOT touch:
///   • HUDCanvas, MainMenuCanvas, NetworkManager, MultiplayerDebugCanvas.
///   • Existing terrain / ground / NavMesh — assumes they were already set
///     up by the single-player Setup Clean Match Map tool first.
///   • Photon connection — that's the runtime/UI flow.
///
/// After running, re-run <c>Add GameEntity To Scene Objects</c> so the new
/// CommandCenters, Workers, and ResourceNodes get deterministic ids.
/// </summary>
public static class SetupMultiplayerMatchMap
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    public static readonly Vector3 Player0BasePos = new Vector3(-80f, 0f, -70f);
    public static readonly Vector3 Player1BasePos = new Vector3( 80f, 0f,  70f);

    private static readonly Color Player0Color = new Color(0.20f, 0.55f, 1.00f); // blue
    private static readonly Color Player1Color = new Color(0.92f, 0.20f, 0.20f); // red
    private static readonly Color ResourceColor = new Color(0.85f, 0.78f, 0.20f); // gold

    private const int StartingResources = 1000;
    private const int ResourceNodesPerBase = 4;
    private const float ResourceNodeRadius = 12f;

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Match/Setup Multiplayer Match Map")]
    public static void Run()
    {
        Debug.Log("[MultiplayerMatch] ── Building two-player match layout ──");

        int buildingLayer = LayerMask.NameToLayer("Building");
        int unitLayer     = LayerMask.NameToLayer("Unit");
        int resourceLayer = LayerMask.NameToLayer("Resource");

        // 1. Build the two bases.
        BuildPlayerBase(0, Player0BasePos, Player0Color, buildingLayer, unitLayer, resourceLayer);
        BuildPlayerBase(1, Player1BasePos, Player1Color, buildingLayer, unitLayer, resourceLayer);

        // 2. Disable enemy AI roots / scripts so the bot doesn't run.
        DisableEnemyBots();

        // 3. Add the MultiplayerMatchStarter to the GameManager (camera snap
        //    + team perspective remap on match start).
        EnsureMatchStarter();

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        // Post-run verification — surface any silent failures (missing prefabs,
        // typos, stale scene state) BEFORE the user pressed Play and wonders
        // why Player 1 has nothing on screen.
        VerifyBaseExists("Player0Base", 0);
        VerifyBaseExists("Player1Base", 1);

        Debug.Log("[MultiplayerMatch] ── Done. Next: run Tools → RTS → Multiplayer Prep → " +
                  "Add GameEntity To Scene Objects so the new units/buildings get " +
                  "deterministic ids that match across clients. ──");
    }

    private static void VerifyBaseExists(string baseName, int playerId)
    {
        GameObject root = GameObject.Find(baseName);
        if (root == null)
        {
            Debug.LogError($"[MultiplayerMatch] ✗ '{baseName}' is missing after setup. " +
                           "This shouldn't happen — re-run the tool.");
            return;
        }

        bool hasCC     = root.GetComponentInChildren<CommandCenter>(true)         != null;
        bool hasWorker = root.GetComponentInChildren<WorkerGatherer>(true)        != null;
        bool hasBank   = root.GetComponentInChildren<PlayerResourceManager>(true) != null;

        if (!hasCC)
            Debug.LogError($"[MultiplayerMatch] ✗ '{baseName}' missing a CommandCenter child.");
        if (!hasWorker)
            Debug.LogError($"[MultiplayerMatch] ✗ '{baseName}' missing a Worker child — Player " +
                           $"{playerId} will start without a Worker on every client.");
        if (!hasBank)
            Debug.LogError($"[MultiplayerMatch] ✗ '{baseName}' missing a PlayerResourceManager " +
                           $"with ownerPlayerId={playerId}.");

        if (hasCC && hasWorker && hasBank)
            Debug.Log($"[MultiplayerMatch] ✓ '{baseName}' verified: CC + Worker + bank present.");
    }

    // ================================================================== //
    // Player base builder
    // ================================================================== //

    private static void BuildPlayerBase(int playerId, Vector3 basePos, Color color,
                                        int buildingLayer, int unitLayer, int resourceLayer)
    {
        string rootName = $"Player{playerId}Base";

        // Reuse existing root if present (idempotent re-runs).
        GameObject root = GameObject.Find(rootName);
        if (root != null)
        {
            Undo.DestroyObjectImmediate(root);
            Debug.Log($"[MultiplayerMatch]   Removed previous '{rootName}' — rebuilding.");
        }
        root = new GameObject(rootName);
        root.transform.position = basePos;
        Undo.RegisterCreatedObjectUndo(root, $"Create {rootName}");

        // --- PlayerResourceManager for this player (per-owner bank) -------
        PlayerResourceManager bank = root.AddComponent<PlayerResourceManager>();
        bank.ownerPlayerId    = playerId;
        bank.startingResources = StartingResources;
        EditorUtility.SetDirty(bank);

        // --- CommandCenter -------------------------------------------------
        CommandCenter cc = BuildCommandCenter(root.transform, color, playerId, buildingLayer);
        Debug.Log($"[MultiplayerMatch] Player {playerId} base created at {basePos:F1} " +
                  $"with CommandCenter '{cc.name}'.");

        // --- Worker --------------------------------------------------------
        BuildWorker(root.transform, basePos, playerId, unitLayer);

        // --- Resource nodes ringed around the base -------------------------
        BuildResourceRing(root.transform, basePos, resourceLayer);

        EditorUtility.SetDirty(root);
    }

    private static CommandCenter BuildCommandCenter(Transform parent, Color color, int playerId, int buildingLayer)
    {
        GameObject ccGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ccGO.name = $"CommandCenter_P{playerId}";
        ccGO.transform.SetParent(parent, false);
        ccGO.transform.localPosition = Vector3.zero;
        ccGO.transform.localScale = new Vector3(6f, 4f, 6f);

        if (buildingLayer >= 0) ccGO.layer = buildingLayer;

        // Visual — set a unique material instance so we don't tint the shared one.
        Renderer rend = ccGO.GetComponent<Renderer>();
        if (rend != null)
        {
            Material instance = new Material(rend.sharedMaterial != null ? rend.sharedMaterial : new Material(Shader.Find("Universal Render Pipeline/Lit")));
            instance.color = color;
            rend.sharedMaterial = instance;
        }

        // Components (mirroring SetupCleanMatchMap's player CC pattern).
        ccGO.AddComponent<CommandCenter>();
        ccGO.AddComponent<SelectableBuilding>();
        ccGO.AddComponent<CommandCenterProducer>();

        Health hp = ccGO.AddComponent<Health>();
        // Phase 3 perspective: scene-baked team is just an initial value; the
        // MultiplayerMatchStarter will re-key it at runtime based on the local
        // player's perspective. We bake Player team for ALL CCs so single-
        // player playtest of this scene still works.
        hp.team = Health.Team.Player;
        hp.maxHealth = 500f;

        UnitCategory cat = ccGO.AddComponent<UnitCategory>();
        cat.category = UnitCategory.Category.Building;

        GameEntity ge = ccGO.AddComponent<GameEntity>();
        ge.ownerPlayerId          = playerId;
        ge.teamId                 = playerId;
        ge.entityType             = EntityType.Building;
        ge.prefabTypeId           = "CommandCenter";
        ge.overrideTeamFromHealth = false;     // owner is authoritative here
        EditorUtility.SetDirty(ge);

        Debug.Log($"[MultiplayerMatch] Player{playerId} CommandCenter stamped " +
                  $"with ownerPlayerId={playerId}.");

        return ccGO.GetComponent<CommandCenter>();
    }

    private static void BuildWorker(Transform parent, Vector3 basePos, int playerId, int unitLayer)
    {
        GameObject workerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/_Game/Prefabs/WorkerPrefab.prefab");
        if (workerPrefab == null)
        {
            Debug.LogError("[MultiplayerMatch] WorkerPrefab.prefab NOT FOUND at " +
                           "Assets/_Game/Prefabs/WorkerPrefab.prefab — Player " +
                           $"{playerId} starts WITHOUT a Worker. Run Tools → RTS → Units → " +
                           "Repair Worker Prefab and re-run Setup Multiplayer Match Map.");
            return;
        }

        Vector3 workerWorld = basePos + new Vector3(4f, 0f, 0f);
        GameObject w = (GameObject)PrefabUtility.InstantiatePrefab(workerPrefab);
        w.name = $"Worker_P{playerId}";
        w.transform.SetParent(parent, true);
        w.transform.position = workerWorld;

        if (unitLayer >= 0)
        {
            w.layer = unitLayer;
            foreach (Transform t in w.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = unitLayer;
        }

        // Tag ownership. The scene-stamper now respects overrideTeamFromHealth=false
        // and preserves these values, so Player 1's Worker keeps owner=1 even
        // though its prefab clone has Health.team=Player.
        GameEntity ge = w.GetComponent<GameEntity>();
        if (ge == null) ge = w.AddComponent<GameEntity>();
        ge.ownerPlayerId          = playerId;
        ge.teamId                 = playerId;
        ge.entityType             = EntityType.Unit;
        ge.prefabTypeId           = "Worker";
        ge.overrideTeamFromHealth = false;
        EditorUtility.SetDirty(ge);
        EditorUtility.SetDirty(w);

        Debug.Log($"[MultiplayerMatch] Player{playerId} Worker stamped " +
                  $"with ownerPlayerId={playerId}, name='{w.name}'.");
    }

    private static void BuildResourceRing(Transform parent, Vector3 basePos, int resourceLayer)
    {
        GameObject ring = new GameObject("Resources");
        ring.transform.SetParent(parent, false);

        for (int i = 0; i < ResourceNodesPerBase; i++)
        {
            float angle = i * Mathf.PI * 2f / ResourceNodesPerBase;
            Vector3 offset = new Vector3(
                Mathf.Cos(angle) * ResourceNodeRadius, 0f,
                Mathf.Sin(angle) * ResourceNodeRadius);
            Vector3 pos = basePos + offset;

            GameObject nodeGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nodeGO.name = $"ResourceNode_{i}";
            nodeGO.transform.SetParent(ring.transform, true);
            nodeGO.transform.position = pos;
            nodeGO.transform.localScale = new Vector3(2f, 2f, 2f);

            if (resourceLayer >= 0) nodeGO.layer = resourceLayer;

            Renderer rend = nodeGO.GetComponent<Renderer>();
            if (rend != null)
            {
                Material m = new Material(rend.sharedMaterial);
                m.color = ResourceColor;
                rend.sharedMaterial = m;
            }

            ResourceNode node = nodeGO.AddComponent<ResourceNode>();
            _ = node;     // ResourceNode self-initialises from Inspector defaults.

            GameEntity ge = nodeGO.AddComponent<GameEntity>();
            ge.ownerPlayerId          = GameEntity.NeutralOwnerId;
            ge.teamId                 = GameEntity.NeutralOwnerId;
            ge.entityType             = EntityType.Resource;
            ge.prefabTypeId           = "ResourceNode";
            ge.overrideTeamFromHealth = false;
            EditorUtility.SetDirty(ge);
        }
    }

    // ================================================================== //
    // Disable enemy AI in multiplayer
    // ================================================================== //

    private static void DisableEnemyBots()
    {
        int disabled = 0;
        disabled += DisableInScene<EnemyBuildAI>();
        disabled += DisableInScene<EnemyWorkerAI>();
        disabled += DisableInScene<EnemyResourceManager>();
        disabled += DisableInScene<EnemyAIController>();
        disabled += DisableInScene<EnemyWaveSpawner>();

        // ALSO hide the EnemyStart root if any — keeps it out of the scene view
        // but doesn't delete it (single-player playtest can re-enable manually).
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
            if (comp == null) continue;
            if (!comp.enabled) continue;
            comp.enabled = false;
            EditorUtility.SetDirty(comp);
            n++;
        }
        return n;
    }

    // ================================================================== //
    // MultiplayerMatchStarter wiring
    // ================================================================== //

    private static void EnsureMatchStarter()
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

        starter.player0CameraPos = Player0BasePos;
        starter.player1CameraPos = Player1BasePos;
        EditorUtility.SetDirty(starter);
        Debug.Log("[MultiplayerMatch]   MultiplayerMatchStarter wired on GameManager.");

        // Phase 6 / Phase 7: GameplayWorldRoot wraps every gameplay-world root
        // (Player0Base, Player1Base, Environment, any leftover SP roots) so
        // they start INACTIVE and only become visible after MatchStart.
        // Without this, the player launches the game and immediately sees
        // the bases behind the Main Menu.
        //
        // Auto-discovery in GameplayWorldRoot.Awake also handles this at
        // runtime, but we pre-populate the Inspector list at editor time
        // so the user can see exactly what's wired without pressing Play.
        GameplayWorldRoot worldRoot = gm.GetComponent<GameplayWorldRoot>();
        if (worldRoot == null) worldRoot = gm.AddComponent<GameplayWorldRoot>();

        // Pull in every well-known gameplay-root name that exists in the
        // scene right now. Filter out duplicates / missing.
        string[] candidates = {
            "Player0Base", "Player1Base",
            "Environment", "ResourceNodes",
            "PlayerStart", "EnemyStart",
        };
        var found = new System.Collections.Generic.List<GameObject>(candidates.Length);
        foreach (string name in candidates)
        {
            GameObject g = GameObject.Find(name);
            if (g != null && !found.Contains(g)) found.Add(g);
        }
        worldRoot.targets = found.ToArray();
        EditorUtility.SetDirty(worldRoot);

        string namesJoined = string.Join(", ",
            System.Array.ConvertAll(worldRoot.targets, g => g != null ? g.name : "<null>"));
        Debug.Log($"[MultiplayerMatch]   GameplayWorldRoot wired with {worldRoot.targets.Length} " +
                  $"gameplay root(s): {namesJoined} — hidden at scene start, revealed on MatchStart.");
    }
}
