using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// One-click developer/testing scene. Builds and maintains
/// <c>Assets/Scenes/DevSandboxScene.unity</c> — a fast local sandbox that
/// boots straight into gameplay with a Dozer, nearby resources, full HUD,
/// and a high starting balance. No MainMenu, no lobby, no Photon room.
///
/// <para>Menus</para>
/// <list type="bullet">
///   <item><b>Tools → RTS → Scenes → Open Dev Sandbox</b> — open the scene
///         (creates+saves first if missing).</item>
///   <item><b>Tools → RTS → Scenes → Create or Repair Dev Sandbox</b> —
///         create the scene if absent, then run a full idempotent repair pass:
///         ground, lighting, EventSystem, CameraRig + Main Camera + RTSCamera,
///         GameManager (PlayerResourceManager / PlayerFactionManager /
///         UnitSelector / BuildingPlacementManager / PowerManager /
///         AttackTargetMarker), HUD (via SetupRTSHUD), Dozer, three
///         ResourceNodes. Appends the scene to Build Settings (does NOT
///         change index 0 — MainMenuScene stays the normal startup).</item>
///   <item><b>Tools → RTS → Scenes → Validate Dev Sandbox</b> — read-only
///         audit: lists every required object and flags anything missing or
///         misconfigured.</item>
/// </list>
///
/// <para>What this tool intentionally does NOT do</para>
/// <list type="bullet">
///   <item>Touch <c>SampleScene</c>, <c>MainMenuScene</c>, or
///         <c>GameMapScene</c>.</item>
///   <item>Add a <see cref="NetworkManagerRTS"/> — its absence is what makes
///         <see cref="NetworkManagerRTS.IsMultiplayerEnabled"/> stay false and
///         <see cref="GameEntity.LocalCommandPlayerId"/> fall back to
///         <see cref="GameEntity.PlayerOwnerId"/> (0). That is exactly the
///         "local single-player" gate the codebase already supports.</item>
///   <item>Add a <see cref="GameStateManager"/>. Without one,
///         <see cref="GameStateManager.IsPlaying"/> evaluates to true on the
///         "Instance == null" path, so RTSCamera / UnitSelector / placement
///         all run immediately on Play with no menu gate.</item>
///   <item>Replace any scene already in Build Settings. DevSandboxScene is
///         appended at the END so MainMenuScene at index 0 keeps booting the
///         normal multiplayer flow.</item>
/// </list>
///
/// <para>Why no NetworkManagerRTS / MainMenu / Lobby?</para>
/// The whole point: pressing Play in this scene must drop you into gameplay
/// with no waiting on rooms or matchmaking. The runtime gates that normally
/// hold gameplay back (multiplayer mode, GameStateManager menu state, match
/// payload) are all opt-in — leaving them out is the supported way to run
/// a local-only test scene.
/// </summary>
public static class SetupDevSandbox
{
    // ------------------------------------------------------------------ //
    // Paths / layout
    // ------------------------------------------------------------------ //

    public const string ScenePath = "Assets/Scenes/DevSandboxScene.unity";
    public const string SceneName = "DevSandboxScene";

    private const string DozerPrefabPath = "Assets/_Game/Prefabs/DozerPrefab.prefab";

    // Buildable prefab paths — exact disk locations from
    // Assets/_Game/Prefabs/. Used to fill the four prefab references on
    // BuildingPlacementManager that RepairBuildingPrefabs.Repair() does not
    // touch (airfield / machineGunDefense / commandCenter / constructionSite),
    // plus a defensive re-fill of the three it does cover so dev-sandbox
    // wiring is complete even if the repair tool is later changed.
    private const string BarracksPrefabPath           = "Assets/_Game/Prefabs/Barracks.prefab";
    private const string PowerPlantPrefabPath         = "Assets/_Game/Prefabs/PowerPlantPrefab.prefab";
    private const string VehicleFactoryPrefabPath     = "Assets/_Game/Prefabs/VehicleFactoryPrefab.prefab";
    private const string AirfieldPrefabPath           = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";
    private const string MachineGunDefensePrefabPath  = "Assets/_Game/Prefabs/MachineGunDefensePrefab.prefab";
    private const string CommandCenterPrefabPath      = "Assets/_Game/Prefabs/CommandCenterPrefab.prefab";
    private const string ConstructionSitePrefabPath   = "Assets/_Game/Prefabs/ConstructionSitePrefab.prefab";

    private const string GroundLayerName   = "Ground";
    private const string ResourceLayerName = "Resource";
    private const string UnitLayerName     = "Unit";
    private const string BuildingLayerName = "Building";

    private const float GroundSize         = 200f;   // half-extent of the sandbox map
    private const int   StartingResources  = 20000;

    private static readonly Vector3 DozerSpawn         = new Vector3(0f, 0f, 0f);
    private static readonly Vector3 CameraRigPosition  = new Vector3(0f, 20f, -18f);
    private static readonly Vector3[] ResourceOffsets  =
    {
        new Vector3(  8f, 0f,  4f),
        new Vector3(-10f, 0f,  6f),
        new Vector3(  2f, 0f, -10f),
    };

    private static readonly Color GroundColor   = new Color(0.32f, 0.40f, 0.30f);
    private static readonly Color ResourceColor = new Color(0.95f, 0.78f, 0.18f);

    // ================================================================== //
    // 1. Open
    // ================================================================== //

    [MenuItem("Tools/RTS/Scenes/Open Dev Sandbox")]
    public static void OpenDevSandbox()
    {
        if (!File.Exists(ScenePath))
        {
            if (!EditorUtility.DisplayDialog(
                    "DevSandboxScene missing",
                    $"'{ScenePath}' does not exist yet. Create it now?\n\n" +
                    "This runs the same logic as " +
                    "Tools → RTS → Scenes → Create or Repair Dev Sandbox.",
                    "Create", "Cancel"))
            {
                Debug.Log("[DevSandbox] User cancelled creation — nothing opened.");
                return;
            }
            CreateOrRepair();
            return;
        }

        if (!PromptSaveCurrent("opening DevSandboxScene")) return;

        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Debug.Log($"[DevSandbox] ✓ Opened '{ScenePath}'. Press Play.");
    }

    // ================================================================== //
    // 2. Create or Repair
    // ================================================================== //

    [MenuItem("Tools/RTS/Scenes/Create or Repair Dev Sandbox")]
    public static void CreateOrRepair()
    {
        Debug.Log("[DevSandbox] ─── Create or Repair Dev Sandbox ───");

        if (!ResolveLayers(out int groundLayer, out int resourceLayer)) return;
        if (!PromptSaveCurrent("rebuilding DevSandboxScene")) return;

        // 1. Ensure the scene exists on disk and is the active scene.
        Scene scene;
        bool created = false;
        if (!File.Exists(ScenePath))
        {
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string dir = Path.GetDirectoryName(ScenePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            EditorSceneManager.SaveScene(scene, ScenePath);
            created = true;
            Debug.Log($"[DevSandbox]   Created empty scene at '{ScenePath}'.");
        }
        else
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[DevSandbox]   Opened existing scene '{ScenePath}' for repair.");
        }

        // 2. Lighting.
        EnsureDirectionalLight();

        // 3. Ground plane (large, flat, NavMesh-static).
        GameObject ground = EnsureGround(groundLayer);

        // 4. EventSystem (UI input).
        EnsureEventSystem();

        // 5. Camera rig.
        EnsureCameraRig();

        // 6. GameManager + managers.
        GameObject gm = EnsureGameManager();
        EnsurePlayerResourceManager(gm, StartingResources);
        EnsurePlayerFactionManager(gm);
        EnsureUnitSelector(gm);
        EnsureBuildingPlacementManager(gm);
        EnsurePowerManager(gm);
        EnsureAttackTargetMarker(gm);

        // 7. HUD — reuses the production tool so the dev scene reads exactly
        //    the same as SampleScene for selection / build buttons / resource bar.
        try
        {
            SetupRTSHUD.SetupHUD();
            Debug.Log("[DevSandbox]   HUD built via SetupRTSHUD.SetupHUD().");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[DevSandbox] ⚠ SetupRTSHUD.SetupHUD() failed: {ex.Message}. " +
                             "Run Tools → RTS → Setup → Setup Gameplay HUD manually.");
        }

        // 7b. Wire BuildingPlacementManager's barracks/powerPlant/vehicleFactory
        //     prefab references + ground/obstacle layer masks via the existing
        //     repair tool. Idempotent and required for "press Play → build
        //     immediately" to actually work.
        try
        {
            RepairBuildingPrefabs.Repair();
            Debug.Log("[DevSandbox]   BuildingPlacementManager: barracks/powerPlant/vehicleFactory + layer masks wired via RepairBuildingPrefabs.");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[DevSandbox] ⚠ RepairBuildingPrefabs.Repair() failed: {ex.Message}. " +
                             "Run Tools → RTS → Repair Prefabs And Building Placement manually.");
        }

        // 7b'. Fill the four prefab fields RepairBuildingPrefabs does NOT touch
        //      (airfield, machineGunDefense, commandCenter, constructionSite),
        //      plus a defensive re-fill of the three it does cover, so every
        //      Dozer build button on the HUD can resolve a prefab. This is what
        //      removes the "Airfield prefab is not assigned" / "CommandCenter
        //      prefab is not assigned" runtime errors in DevSandboxScene.
        WireAllBuildingPrefabs(gm.GetComponent<BuildingPlacementManager>());

        // 7c. SelectionBox UI under HUDCanvas — wires UnitSelector.selectionBoxRect.
        //     The drag-box rectangle visual is otherwise null (logs a warning at runtime).
        EnsureSelectionBoxUI(gm.GetComponent<UnitSelector>());

        // 7d. Dev bootstrap — runtime component that logs the local-player /
        //     ownership / mask / NavMesh state on Start so we get the verbose
        //     lifecycle diagnostics the user asked for.
        EnsureDevSandboxBootstrap(gm);

        // 8. Player Dozer (instantiated from prefab — already carries
        //    GameEntity.ownerPlayerId = 0 and team = Player).
        EnsureDozer();

        // 9. Resource nodes near the Dozer.
        EnsureResourceNodes(resourceLayer);

        // 10. NavMesh bake (Dozer needs it for placement movement).
        BakeNavMesh(ground);

        // 11. Save the scene.
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        // 12. Append (do not replace) DevSandboxScene to Build Settings.
        EnsureBuildSettingsEntry();

        Debug.Log("[DevSandbox] ✓ " + (created ? "Created" : "Repaired") +
                  $" {SceneName}. Starting resources = {StartingResources}. " +
                  "Press Play (the scene is already open).");
        Debug.Log("[DevSandbox] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // 3. Validate
    // ================================================================== //

    [MenuItem("Tools/RTS/Scenes/Validate Dev Sandbox")]
    public static void ValidateDevSandbox()
    {
        Debug.Log("[ValidateDevSandbox] ─── Audit ───");

        // -- file presence --
        if (!File.Exists(ScenePath))
        {
            Debug.LogError($"[ValidateDevSandbox] ✗ '{ScenePath}' missing. " +
                           "Run Tools → RTS → Scenes → Create or Repair Dev Sandbox.");
            return;
        }
        Debug.Log($"[ValidateDevSandbox]   ✓ Scene file exists at '{ScenePath}'.");

        // -- open the scene so we can inspect it --
        if (!PromptSaveCurrent("validating DevSandboxScene")) return;
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        int problems = 0;

        problems += Check("Directional Light",
            Object.FindAnyObjectByType<Light>(FindObjectsInactive.Include) != null);

        problems += Check("Ground (named 'Ground', Ground layer)",
            FindGround() != null);

        problems += Check("Main Camera",
            Camera.main != null);

        problems += Check("CameraRig with RTSCamera",
            Object.FindAnyObjectByType<RTSCamera>(FindObjectsInactive.Include) != null);

        problems += Check("EventSystem",
            Object.FindAnyObjectByType<EventSystem>(FindObjectsInactive.Include) != null);

        problems += Check("GameManager GameObject",
            GameObject.Find("GameManager") != null);

        PlayerResourceManager prm =
            Object.FindAnyObjectByType<PlayerResourceManager>(FindObjectsInactive.Include);
        problems += Check("PlayerResourceManager (ownerPlayerId == 0)",
            prm != null && prm.ownerPlayerId == 0);
        if (prm != null)
            Debug.Log($"[ValidateDevSandbox]     • startingResources = {prm.startingResources}");

        UnitSelector sel = Object.FindAnyObjectByType<UnitSelector>(FindObjectsInactive.Include);
        problems += Check("UnitSelector",                       sel != null);
        if (sel != null)
        {
            problems += Check($"  UnitSelector.groundLayer   != 0  (got 0x{sel.groundLayer.value:X})",
                sel.groundLayer.value   != 0);
            problems += Check($"  UnitSelector.unitLayer     != 0  (got 0x{sel.unitLayer.value:X})",
                sel.unitLayer.value     != 0);
            problems += Check($"  UnitSelector.resourceLayer != 0  (got 0x{sel.resourceLayer.value:X})",
                sel.resourceLayer.value != 0);
            problems += Check($"  UnitSelector.buildingLayer != 0  (got 0x{sel.buildingLayer.value:X})",
                sel.buildingLayer.value != 0);
            problems += Check("  UnitSelector.selectionBoxRect wired",
                sel.selectionBoxRect != null);
        }

        BuildingPlacementManager bpm =
            Object.FindAnyObjectByType<BuildingPlacementManager>(FindObjectsInactive.Include);
        problems += Check("BuildingPlacementManager", bpm != null);
        if (bpm != null)
        {
            problems += Check($"  BPM.groundLayer   != 0  (got 0x{bpm.groundLayer.value:X})",
                bpm.groundLayer.value   != 0);
            problems += Check($"  BPM.obstacleLayer != 0  (got 0x{bpm.obstacleLayer.value:X})",
                bpm.obstacleLayer.value != 0);

            // Every Dozer build button reads one of these fields. A null
            // prefab here is exactly the runtime error the user hit
            // ("Airfield prefab is not assigned").
            problems += CheckPrefab("BPM.barracksPrefab",          bpm.barracksPrefab);
            problems += CheckPrefab("BPM.powerPlantPrefab",        bpm.powerPlantPrefab);
            problems += CheckPrefab("BPM.vehicleFactoryPrefab",    bpm.vehicleFactoryPrefab);
            problems += CheckPrefab("BPM.airfieldPrefab",          bpm.airfieldPrefab);
            problems += CheckPrefab("BPM.machineGunDefensePrefab", bpm.machineGunDefensePrefab);
            problems += CheckPrefab("BPM.commandCenterPrefab",     bpm.commandCenterPrefab);
            problems += CheckPrefab("BPM.constructionSitePrefab",  bpm.constructionSitePrefab);
        }

        problems += Check("DevSandboxBootstrap",
            Object.FindAnyObjectByType<DevSandboxBootstrap>(FindObjectsInactive.Include) != null);

        problems += Check("HUDCanvas",
            GameObject.Find("HUDCanvas") != null);

        // -- Dozer presence + ownership + movement components --
        GameEntity dozerEntity = FindEntityByPrefabType("Dozer");
        problems += Check("Dozer (GameEntity.prefabTypeId == 'Dozer') exists",
            dozerEntity != null);
        if (dozerEntity != null)
        {
            problems += Check($"  Dozer.ownerPlayerId == 0  (got {dozerEntity.ownerPlayerId})",
                dozerEntity.ownerPlayerId == GameEntity.PlayerOwnerId);

            Health h = dozerEntity.GetComponent<Health>();
            problems += Check($"  Dozer.Health.team == Player  (got {(h != null ? h.team.ToString() : "<null>")})",
                h != null && h.team == Health.Team.Player);

            UnityEngine.AI.NavMeshAgent agent =
                dozerEntity.GetComponent<UnityEngine.AI.NavMeshAgent>();
            problems += Check("  Dozer NavMeshAgent present",  agent != null);
            if (agent != null)
            {
                problems += Check($"  Dozer NavMeshAgent.enabled  (got {agent.enabled})", agent.enabled);
                // isOnNavMesh is only authoritative in Play Mode. In Edit Mode we
                // can at least sample whether ANY NavMesh exists near the spawn.
                bool sampled = UnityEngine.AI.NavMesh.SamplePosition(
                    dozerEntity.transform.position,
                    out UnityEngine.AI.NavMeshHit _, 5f,
                    UnityEngine.AI.NavMesh.AllAreas);
                problems += Check("  NavMesh sampled within 5 m of Dozer spawn", sampled);
            }
        }

        // -- Resources --
        ResourceNode[] nodes = Object.FindObjectsByType<ResourceNode>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        problems += Check($"ResourceNodes ≥ 1  (found {nodes.Length})", nodes.Length >= 1);

        // -- No menu / lobby canvases in this scene --
        problems += CheckAbsent("LobbyCanvas", GameObject.Find("LobbyCanvas"));
        problems += CheckAbsent("MainMenuCanvas", GameObject.Find("MainMenuCanvas"));
        problems += CheckAbsent("OptionsCanvas", GameObject.Find("OptionsCanvas"));
        problems += CheckAbsent("MultiplayerDebugCanvas", GameObject.Find("MultiplayerDebugCanvas"));

        // -- No NetworkManagerRTS / GameStateManager (their absence is the
        //    point — keeps gameplay running without lobby/menu gates). --
        NetworkManagerRTS nm =
            Object.FindAnyObjectByType<NetworkManagerRTS>(FindObjectsInactive.Include);
        if (nm != null)
            Debug.LogWarning("[ValidateDevSandbox]   ⚠ NetworkManagerRTS present in DevSandboxScene. " +
                             "Recommended: leave it absent so IsMultiplayerEnabled stays false. " +
                             "Will not block gameplay if its multiplayerMode toggle is off.");

        GameStateManager gsm =
            Object.FindAnyObjectByType<GameStateManager>(FindObjectsInactive.Include);
        if (gsm != null)
            Debug.LogWarning("[ValidateDevSandbox]   ⚠ GameStateManager present. " +
                             "Without StartGame() called by a menu, RTSCamera / UnitSelector / " +
                             "BuildingPlacementManager will gate their input. Remove it for a " +
                             "true 'press-Play-and-go' sandbox.");

        // -- Build Settings membership --
        bool inBuild = false;
        foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            if (s.path == ScenePath) { inBuild = true; break; }
        problems += Check("In Build Settings (appended)", inBuild);
        if (inBuild)
        {
            // Confirm we did NOT promote ourselves to index 0.
            EditorBuildSettingsScene[] arr = EditorBuildSettings.scenes;
            int idx = -1;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i].path == ScenePath) { idx = i; break; }
            problems += Check($"  index > 0 (got {idx}; MainMenuScene must stay at 0)", idx > 0);
        }

        if (problems == 0)
            Debug.Log("[ValidateDevSandbox] ✓ All checks passed.");
        else
            Debug.LogWarning($"[ValidateDevSandbox] ⚠ {problems} problem(s). " +
                             "Re-run Create or Repair Dev Sandbox.");

        Debug.Log("[ValidateDevSandbox] ─────────────────────────────────────────");
    }

    private static int Check(string label, bool ok)
    {
        Debug.Log($"[ValidateDevSandbox]   {(ok ? "✓" : "✗")}  {label}");
        return ok ? 0 : 1;
    }

    private static int CheckAbsent(string label, Object obj)
    {
        if (obj == null)
        {
            Debug.Log($"[ValidateDevSandbox]   ✓  No '{label}' (correct).");
            return 0;
        }
        Debug.LogWarning($"[ValidateDevSandbox]   ⚠ '{label}' present — DevSandbox should be menu-free. " +
                         "Delete it from this scene.");
        return 1;
    }

    private static int CheckPrefab(string label, GameObject value)
    {
        bool ok = value != null;
        string detail = ok ? $"→ '{AssetDatabase.GetAssetPath(value)}'" : "→ NULL (Dozer build button will error at runtime)";
        Debug.Log($"[ValidateDevSandbox]   {(ok ? "✓" : "✗")}  {label}  {detail}");
        return ok ? 0 : 1;
    }

    // ================================================================== //
    // Builders / repair helpers
    // ================================================================== //

    private static void EnsureDirectionalLight()
    {
        Light existing = Object.FindAnyObjectByType<Light>(FindObjectsInactive.Include);
        if (existing != null && existing.type == LightType.Directional) return;

        GameObject go = new GameObject("Directional Light");
        Light l = go.AddComponent<Light>();
        l.type      = LightType.Directional;
        l.intensity = 1f;
        l.color     = Color.white;
        l.shadows   = LightShadows.Soft;
        go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        Debug.Log("[DevSandbox]   Directional Light created.");
    }

    private static GameObject EnsureGround(int groundLayer)
    {
        GameObject ground = FindGround();
        if (ground == null)
        {
            ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            Debug.Log("[DevSandbox]   Ground plane created.");
        }
        ground.layer = groundLayer;
        ground.transform.localScale    = new Vector3(GroundSize / 10f, 1f, GroundSize / 10f);
        ground.transform.position      = Vector3.zero;
        ground.transform.localRotation = Quaternion.identity;
        GameObjectUtility.SetStaticEditorFlags(ground, StaticEditorFlags.NavigationStatic);

        Renderer r = ground.GetComponent<Renderer>();
        if (r != null) ApplyColor(r, GroundColor);

        return ground;
    }

    private static GameObject FindGround()
    {
        int layer = LayerMask.NameToLayer(GroundLayerName);
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (root == null) continue;
            if (root.name == "Ground" && root.layer == layer) return root;
        }
        return null;
    }

    private static void EnsureEventSystem()
    {
        EventSystem es = Object.FindAnyObjectByType<EventSystem>(FindObjectsInactive.Include);
        if (es != null) return;

        GameObject go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        go.AddComponent<StandaloneInputModule>();
        Debug.Log("[DevSandbox]   EventSystem created.");
    }

    private static void EnsureCameraRig()
    {
        RTSCamera rig = Object.FindAnyObjectByType<RTSCamera>(FindObjectsInactive.Include);
        Camera cam   = Camera.main;

        if (rig == null)
        {
            GameObject rigGO = new GameObject("CameraRig");
            rigGO.transform.position = CameraRigPosition;
            rig = rigGO.AddComponent<RTSCamera>();
            Debug.Log("[DevSandbox]   CameraRig + RTSCamera created.");
        }
        else
        {
            rig.transform.position = CameraRigPosition;
        }

        if (cam == null)
        {
            GameObject camGO = new GameObject("Main Camera");
            camGO.tag = "MainCamera";
            cam       = camGO.AddComponent<Camera>();
            camGO.AddComponent<AudioListener>();
            Debug.Log("[DevSandbox]   Main Camera created.");
        }

        // Parent the camera to the rig, tilt 45° down, framing the spawn.
        cam.transform.SetParent(rig.transform, worldPositionStays: false);
        cam.transform.localPosition = Vector3.zero;
        cam.transform.localRotation = Quaternion.Euler(45f, 0f, 0f);
        rig.cam = cam;
    }

    private static GameObject EnsureGameManager()
    {
        GameObject gm = GameObject.Find("GameManager");
        if (gm != null) return gm;
        gm = new GameObject("GameManager");
        Debug.Log("[DevSandbox]   GameManager created.");
        return gm;
    }

    private static void EnsurePlayerResourceManager(GameObject host, int startingResources)
    {
        PlayerResourceManager prm = host.GetComponent<PlayerResourceManager>();
        if (prm == null)
        {
            prm = host.AddComponent<PlayerResourceManager>();
            Debug.Log("[DevSandbox]   PlayerResourceManager added to GameManager.");
        }
        prm.ownerPlayerId      = GameEntity.PlayerOwnerId; // 0
        prm.startingResources  = startingResources;
        EditorUtility.SetDirty(prm);
    }

    private static void EnsurePlayerFactionManager(GameObject host)
    {
        if (host.GetComponent<PlayerFactionManager>() == null)
        {
            host.AddComponent<PlayerFactionManager>();
            Debug.Log("[DevSandbox]   PlayerFactionManager added.");
        }
    }

    private static void EnsureUnitSelector(GameObject host)
    {
        UnitSelector sel = host.GetComponent<UnitSelector>();
        bool added = false;
        if (sel == null)
        {
            sel = host.AddComponent<UnitSelector>();
            added = true;
        }

        // CRITICAL: wire the four LayerMasks. Without these, right-click ground
        // raycasts return nothing (groundLayer = 0) and the Dozer never receives
        // a Move command. UnitSelector.cs has no Reset()/auto-wiring path — the
        // masks have to be set by either Inspector or this tool.
        int unitL     = LayerMask.NameToLayer(UnitLayerName);
        int groundL   = LayerMask.NameToLayer(GroundLayerName);
        int resourceL = LayerMask.NameToLayer(ResourceLayerName);
        int buildingL = LayerMask.NameToLayer(BuildingLayerName);
        if (unitL     >= 0) sel.unitLayer     = 1 << unitL;
        if (groundL   >= 0) sel.groundLayer   = 1 << groundL;
        if (resourceL >= 0) sel.resourceLayer = 1 << resourceL;
        if (buildingL >= 0) sel.buildingLayer = 1 << buildingL;
        EditorUtility.SetDirty(sel);

        Debug.Log($"[DevSandbox]   UnitSelector {(added ? "added" : "found")} — masks wired: " +
                  $"unit={(unitL >= 0 ? UnitLayerName : "MISSING")}, " +
                  $"ground={(groundL >= 0 ? GroundLayerName : "MISSING")}, " +
                  $"resource={(resourceL >= 0 ? ResourceLayerName : "MISSING")}, " +
                  $"building={(buildingL >= 0 ? BuildingLayerName : "MISSING")}.");
    }

    private static void EnsureSelectionBoxUI(UnitSelector sel)
    {
        if (sel == null) return;
        if (sel.selectionBoxRect != null) return;

        GameObject canvasGO = GameObject.Find("HUDCanvas");
        if (canvasGO == null)
        {
            Debug.LogWarning("[DevSandbox]   HUDCanvas not found — skipping SelectionBox UI wire. " +
                             "Drag-box selection will still work; only its rectangle visual is missing.");
            return;
        }

        // Match the field's documented setup: anchor CENTER, pivot (0, 0).
        // NOTE: CanvasRenderer lives in UnityEngine, not UnityEngine.UI — fully
        // qualifying it as UnityEngine.UI.CanvasRenderer is a CS0234 (the old
        // mistake that broke this assembly).
        GameObject boxGO = new GameObject("SelectionBox",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        boxGO.transform.SetParent(canvasGO.transform, false);

        RectTransform rt = boxGO.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0f, 0f);
        rt.sizeDelta = Vector2.zero;
        rt.anchoredPosition = Vector2.zero;

        UnityEngine.UI.Image img = boxGO.GetComponent<UnityEngine.UI.Image>();
        img.color = new Color(0.35f, 0.85f, 0.35f, 0.20f);
        img.raycastTarget = false;

        boxGO.SetActive(false);
        sel.selectionBoxRect = rt;
        EditorUtility.SetDirty(sel);
        Debug.Log("[DevSandbox]   SelectionBox UI created under HUDCanvas and wired to UnitSelector.selectionBoxRect.");
    }

    private static void EnsureDevSandboxBootstrap(GameObject host)
    {
        if (host.GetComponent<DevSandboxBootstrap>() == null)
        {
            host.AddComponent<DevSandboxBootstrap>();
            Debug.Log("[DevSandbox]   DevSandboxBootstrap added — will log local-player / Dozer / mask state on Play.");
        }
    }

    private static void EnsureBuildingPlacementManager(GameObject host)
    {
        if (host.GetComponent<BuildingPlacementManager>() == null)
        {
            host.AddComponent<BuildingPlacementManager>();
            Debug.Log("[DevSandbox]   BuildingPlacementManager added.");
        }
    }

    /// <summary>
    /// Walks every prefab field on <see cref="BuildingPlacementManager"/> and
    /// loads its corresponding asset from disk if the field is currently null.
    /// Does NOT overwrite a non-null reference (preserves whatever the user
    /// might have hand-wired in the Inspector). Logs a clear MISSING line for
    /// any prefab the asset database can't resolve so the user knows which
    /// disk file to create.
    /// </summary>
    private static void WireAllBuildingPrefabs(BuildingPlacementManager bpm)
    {
        if (bpm == null)
        {
            Debug.LogWarning("[DevSandbox]   ⚠ BuildingPlacementManager missing — cannot wire prefabs.");
            return;
        }

        int wired = 0, alreadyOk = 0, missingOnDisk = 0;

        wired += AssignIfNull(bpm, "barracksPrefab",           BarracksPrefabPath,
                              () => bpm.barracksPrefab,           v => bpm.barracksPrefab           = v,
                              ref alreadyOk, ref missingOnDisk);
        wired += AssignIfNull(bpm, "powerPlantPrefab",         PowerPlantPrefabPath,
                              () => bpm.powerPlantPrefab,         v => bpm.powerPlantPrefab         = v,
                              ref alreadyOk, ref missingOnDisk);
        wired += AssignIfNull(bpm, "vehicleFactoryPrefab",     VehicleFactoryPrefabPath,
                              () => bpm.vehicleFactoryPrefab,     v => bpm.vehicleFactoryPrefab     = v,
                              ref alreadyOk, ref missingOnDisk);
        wired += AssignIfNull(bpm, "airfieldPrefab",           AirfieldPrefabPath,
                              () => bpm.airfieldPrefab,           v => bpm.airfieldPrefab           = v,
                              ref alreadyOk, ref missingOnDisk);
        wired += AssignIfNull(bpm, "machineGunDefensePrefab",  MachineGunDefensePrefabPath,
                              () => bpm.machineGunDefensePrefab,  v => bpm.machineGunDefensePrefab  = v,
                              ref alreadyOk, ref missingOnDisk);
        wired += AssignIfNull(bpm, "commandCenterPrefab",      CommandCenterPrefabPath,
                              () => bpm.commandCenterPrefab,      v => bpm.commandCenterPrefab      = v,
                              ref alreadyOk, ref missingOnDisk);
        wired += AssignIfNull(bpm, "constructionSitePrefab",   ConstructionSitePrefabPath,
                              () => bpm.constructionSitePrefab,   v => bpm.constructionSitePrefab   = v,
                              ref alreadyOk, ref missingOnDisk);

        EditorUtility.SetDirty(bpm);
        Debug.Log($"[DevSandbox]   BuildingPlacementManager prefab wiring: " +
                  $"{wired} newly assigned, {alreadyOk} already correct, {missingOnDisk} missing on disk.");
    }

    private static int AssignIfNull(BuildingPlacementManager bpm, string fieldName, string path,
                                    System.Func<GameObject> getter, System.Action<GameObject> setter,
                                    ref int alreadyOk, ref int missingOnDisk)
    {
        if (getter() != null) { alreadyOk++; return 0; }

        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (asset == null)
        {
            Debug.LogWarning($"[DevSandbox]     ⚠ {fieldName}: '{path}' not found on disk — left null. " +
                             "If this prefab is required for your build flow, create it via its " +
                             "dedicated builder (Tools → RTS → … → Create … Prefab).");
            missingOnDisk++;
            return 0;
        }

        setter(asset);
        Debug.Log($"[DevSandbox]     ✓ {fieldName} ← '{path}'.");
        return 1;
    }

    private static void EnsurePowerManager(GameObject host)
    {
        if (host.GetComponent<PowerManager>() == null)
        {
            host.AddComponent<PowerManager>();
            Debug.Log("[DevSandbox]   PowerManager added.");
        }
    }

    private static void EnsureAttackTargetMarker(GameObject host)
    {
        if (host.GetComponent<AttackTargetMarker>() == null)
        {
            host.AddComponent<AttackTargetMarker>();
            Debug.Log("[DevSandbox]   AttackTargetMarker added.");
        }
    }

    private static void EnsureDozer()
    {
        GameEntity existing = FindEntityByPrefabType("Dozer");
        if (existing != null)
        {
            // Just snap it to the canonical spawn so re-running the tool relocates
            // a hand-moved Dozer back home without destroying scene work.
            existing.transform.position = DozerSpawn;
            EditorUtility.SetDirty(existing.transform);
            Debug.Log("[DevSandbox]   Dozer already present — repositioned to spawn.");
            return;
        }

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DozerPrefabPath);
        if (prefab == null)
        {
            Debug.LogError($"[DevSandbox] ✗ '{DozerPrefabPath}' missing — cannot spawn Dozer. " +
                           "Run Tools → RTS → Setup Dozer Prefab to bootstrap it.");
            return;
        }

        GameObject dozer = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        dozer.name = "Dozer";
        dozer.transform.position = DozerSpawn;
        Debug.Log("[DevSandbox]   Dozer spawned at (0,0,0). " +
                  "Prefab ships with GameEntity.ownerPlayerId=0 / team=Player, " +
                  "so it's owned by the local player automatically.");
    }

    private static void EnsureResourceNodes(int resourceLayer)
    {
        ResourceNode[] existing = Object.FindObjectsByType<ResourceNode>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (existing.Length >= ResourceOffsets.Length)
        {
            Debug.Log($"[DevSandbox]   {existing.Length} ResourceNode(s) already present — leaving them.");
            return;
        }

        GameObject root = GameObject.Find("ResourceNodes");
        if (root == null)
        {
            root = new GameObject("ResourceNodes");
            Debug.Log("[DevSandbox]   ResourceNodes container created.");
        }

        for (int i = existing.Length; i < ResourceOffsets.Length; i++)
        {
            Vector3 worldPos = DozerSpawn + ResourceOffsets[i];
            GameObject node = GameObject.CreatePrimitive(PrimitiveType.Cube);
            node.name = $"ResourceNode_{i:D2}";
            node.transform.SetParent(root.transform, worldPositionStays: false);
            node.transform.position   = new Vector3(worldPos.x, 0.5f, worldPos.z);
            node.transform.localScale = new Vector3(1.4f, 1.0f, 1.4f);
            node.layer = resourceLayer;

            Renderer r = node.GetComponent<Renderer>();
            if (r != null) ApplyColor(r, ResourceColor);

            node.AddComponent<ResourceNode>();
            Debug.Log($"[DevSandbox]   ResourceNode spawned at {worldPos}.");
        }
    }

    private static void BakeNavMesh(GameObject ground)
    {
        // Mount the NavMeshSurface on the ground so the bake collects exactly
        // the sandbox geometry, nothing else.
        if (ground == null) return;

        NavMeshSurface surface = ground.GetComponent<NavMeshSurface>();
        if (surface == null) surface = ground.AddComponent<NavMeshSurface>();
        surface.collectObjects = CollectObjects.All;
        surface.useGeometry    = NavMeshCollectGeometry.PhysicsColliders;

        try
        {
            surface.BuildNavMesh();
            EditorUtility.SetDirty(surface);
            Debug.Log("[DevSandbox]   NavMesh baked.");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[DevSandbox] ⚠ NavMesh bake failed: {ex.Message}. " +
                             "Run Tools → RTS → Environment → Rebuild NavMesh And Snap Units manually.");
        }
    }

    private static void EnsureBuildSettingsEntry()
    {
        var current = EditorBuildSettings.scenes;
        for (int i = 0; i < current.Length; i++)
            if (current[i].path == ScenePath)
            {
                Debug.Log($"[DevSandbox]   Build Settings already contains '{ScenePath}' at index {i}.");
                return;
            }

        var list = new List<EditorBuildSettingsScene>(current);
        list.Add(new EditorBuildSettingsScene(ScenePath, enabled: true));
        EditorBuildSettings.scenes = list.ToArray();
        Debug.Log($"[DevSandbox]   Build Settings: appended '{ScenePath}' at index {list.Count - 1}. " +
                  "MainMenuScene remains at index 0.");
    }

    // ================================================================== //
    // Misc utilities
    // ================================================================== //

    /// <summary>Asks the user to save the current scene if dirty. Returns false on Cancel.</summary>
    private static bool PromptSaveCurrent(string reason)
    {
        Scene cur = EditorSceneManager.GetActiveScene();
        if (!cur.isDirty) return true;

        int choice = EditorUtility.DisplayDialogComplex(
            "Save current scene?",
            $"'{cur.name}' has unsaved changes. Save before {reason}?",
            "Save", "Cancel", "Discard");
        if (choice == 1) return false;
        if (choice == 0) EditorSceneManager.SaveScene(cur);
        return true;
    }

    private static bool ResolveLayers(out int ground, out int resource)
    {
        ground   = LayerMask.NameToLayer(GroundLayerName);
        resource = LayerMask.NameToLayer(ResourceLayerName);
        bool ok = true;
        if (ground   < 0) { Debug.LogError($"[DevSandbox] ✗ Layer '{GroundLayerName}' missing.");   ok = false; }
        if (resource < 0) { Debug.LogError($"[DevSandbox] ✗ Layer '{ResourceLayerName}' missing."); ok = false; }
        if (!ok)
            Debug.LogError("[DevSandbox] Fix in Edit → Project Settings → Tags and Layers, then re-run.");
        return ok;
    }

    private static GameEntity FindEntityByPrefabType(string prefabTypeId)
    {
        foreach (GameEntity e in Object.FindObjectsByType<GameEntity>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (e != null && e.prefabTypeId == prefabTypeId) return e;
        }
        return null;
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

    private static void ApplyColor(Renderer r, Color color)
    {
        Material m = new Material(ResolveLitShader()) { color = color };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        r.sharedMaterial = m;
        r.shadowCastingMode = ShadowCastingMode.On;
    }
}
