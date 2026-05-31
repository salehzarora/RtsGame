using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Companion tools for the MainMenuScene/GameMapScene split:
///
///   • <b>Cleanup MainMenuScene</b> — strips gameplay-world roots from
///     whichever scene you have open. Use this if you opened MainMenuScene
///     and want to re-clean it after dragging gameplay objects into it.
///   • <b>Cleanup GameMapScene</b> — strips menu/lobby roots from the open
///     scene. Use after a re-merge or accidental drag of lobby UI in.
///   • <b>Validate Scene Split</b> — checks that both scenes exist, are in
///     Build Settings at the right indices, and lists any leftover
///     wrong-category roots in each. Read-only.
///
/// All cleanup ops are name-based and root-only (same logic as the splitter)
/// so they're easy to reason about and never touch nested prefab children
/// that happen to share a name.
/// </summary>
public static class SceneSplitTools
{
    private const string MainMenuScenePath = "Assets/Scenes/MainMenuScene.unity";
    private const string GameMapScenePath  = "Assets/Scenes/GameMapScene.unity";
    private const string SampleScenePath   = "Assets/Scenes/SampleScene.unity";

    // ================================================================== //
    // Quick-open menus — the single most common reason "the menu still
    // shows the map" is that the user opened SampleScene by accident. One
    // click here jumps to the right scene.
    // ================================================================== //

    [MenuItem("Tools/RTS/Scenes/Open MainMenuScene")]
    public static void OpenMainMenuScene() => OpenSceneAt(MainMenuScenePath, "MainMenuScene");

    [MenuItem("Tools/RTS/Scenes/Open GameMapScene")]
    public static void OpenGameMapScene() => OpenSceneAt(GameMapScenePath, "GameMapScene");

    private static void OpenSceneAt(string path, string label)
    {
        if (!File.Exists(path))
        {
            Debug.LogError($"[SceneSplit] ✗ '{path}' not found. " +
                           "Run Tools → RTS → Scenes → Create Scene Split first.");
            return;
        }
        Scene cur = EditorSceneManager.GetActiveScene();
        if (cur.isDirty)
        {
            int choice = EditorUtility.DisplayDialogComplex(
                "Save current scene?",
                $"'{cur.name}' has unsaved changes. Save before opening {label}?",
                "Save", "Cancel", "Discard");
            if (choice == 1) return;
            if (choice == 0) EditorSceneManager.SaveScene(cur);
        }
        EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        Debug.Log($"[SceneSplit] ✓ Opened '{path}'. Press Play to test.");
    }

    // Mirrors CreateSceneSplit so both sources of truth never drift apart.
    private static readonly string[] GameplayOnlyRoots =
    {
        "Environment", "ResourceNodes", "PlayerStart", "EnemyStart",
        "GameplayWorldRoot", "Player0Base", "Player1Base",
        "HUDCanvas", "EscapeMenuCanvas", "SelectionCanvas",
        "MinimapCameraGO", "MatchManager",
        "MultiplayerDebugCanvas", // not part of the real menu flow
    };

    private static readonly string[] MenuOnlyRoots =
    {
        "LobbyCanvas", "MainMenuCanvas", "OptionsCanvas", "MultiplayerDebugCanvas",
    };

    // ================================================================== //
    // Simplest-path migration: skip the GameMapScene indirection and point
    // PhotonNetwork.LoadLevel at SampleScene directly. SampleScene is the
    // single source of truth for the gameplay map; this removes the
    // "rebuild after every edit" friction by making the runtime read
    // SampleScene as-is. MainMenuScene continues to host the menu/lobby.
    // ================================================================== //

    [MenuItem("Tools/RTS/Scenes/Use SampleScene As Gameplay Target")]
    public static void UseSampleSceneAsGameplayTarget()
    {
        Debug.Log("[UseSampleScene] ─── Switching gameplay target to SampleScene ───");

        // ---- Source sanity ---------------------------------------------- //
        if (!System.IO.File.Exists(SampleScenePath))
        {
            Debug.LogError($"[UseSampleScene] ✗ '{SampleScenePath}' not found. Aborting.");
            return;
        }

        // ---- Confirm with the user -------------------------------------- //
        if (!EditorUtility.DisplayDialog(
                "Use SampleScene as the gameplay target?",
                "This will:\n\n" +
                "  • Set NetworkManagerRTS.gameMapSceneName = 'SampleScene' " +
                "(useSceneSplit = true) on the NetworkManager in the open scene.\n" +
                "  • Update Build Settings: [0] = MainMenuScene, [1] = SampleScene. " +
                "GameMapScene is REMOVED from Build Settings (the file is kept on disk).\n" +
                "  • Optionally strip menu UI roots (LobbyCanvas / MainMenuCanvas / " +
                "OptionsCanvas / MultiplayerDebugCanvas) from SampleScene so it's " +
                "gameplay-only at runtime.\n\n" +
                "Continue?",
                "Switch", "Cancel"))
        {
            Debug.Log("[UseSampleScene] User cancelled.");
            return;
        }

        // ---- 1. NetworkManagerRTS: set gameMapSceneName + useSceneSplit - //
        NetworkManagerRTS[] managers = Object.FindObjectsByType<NetworkManagerRTS>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int touched = 0;
        for (int i = 0; i < managers.Length; i++)
        {
            NetworkManagerRTS nm = managers[i];
            if (nm == null) continue;
            nm.gameMapSceneName = "SampleScene";
            nm.useSceneSplit = true;
            EditorUtility.SetDirty(nm);
            touched++;
            Debug.Log($"[UseSampleScene] NetworkManagerRTS.gameMapSceneName = 'SampleScene' " +
                      $"on '{nm.gameObject.name}'. useSceneSplit = true.");
        }
        if (touched == 0)
        {
            Debug.LogWarning("[UseSampleScene] ⚠ No NetworkManagerRTS in this scene. " +
                             "Open MainMenuScene and re-run so the live NetworkManager is updated.");
        }

        // ---- 2. Build Settings: [0] MainMenuScene, [1] SampleScene ------ //
        EditorBuildSettingsScene[] current = EditorBuildSettings.scenes;
        var ordered = new System.Collections.Generic.List<EditorBuildSettingsScene>();
        ordered.Add(new EditorBuildSettingsScene(MainMenuScenePath, enabled: true));
        ordered.Add(new EditorBuildSettingsScene(SampleScenePath,   enabled: true));
        for (int i = 0; i < current.Length; i++)
        {
            string p = current[i].path;
            if (string.IsNullOrEmpty(p)) continue;
            if (p == MainMenuScenePath) continue;
            if (p == SampleScenePath) continue;
            if (p == GameMapScenePath) continue;   // explicitly remove from Build Settings
            ordered.Add(current[i]);
        }
        EditorBuildSettings.scenes = ordered.ToArray();
        Debug.Log($"[UseSampleScene] Build Settings: [0]=MainMenuScene, [1]=SampleScene " +
                  $"(GameMapScene removed; file kept on disk).");

        // ---- 3. Mark scene dirty so the manager change persists --------- //
        if (touched > 0)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        // ---- 4. Optionally strip menu UI from SampleScene --------------- //
        bool strip = EditorUtility.DisplayDialog(
            "Strip menu UI from SampleScene?",
            "Open SampleScene now and remove the menu/lobby roots " +
            $"({string.Join(", ", MenuOnlyRoots)}) so they don't appear over the " +
            "gameplay map after Start Match loads it?\n\n" +
            "Recommended: yes. SampleScene becomes gameplay-only; MainMenuScene " +
            "keeps the menu.",
            "Strip", "Skip");

        if (strip)
        {
            // Save any dirty changes in the current scene first.
            Scene activeNow = EditorSceneManager.GetActiveScene();
            if (activeNow.isDirty) EditorSceneManager.SaveScene(activeNow);

            Scene sample = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
            int removed = 0;
            for (int i = 0; i < MenuOnlyRoots.Length; i++)
                removed += DestroyRootByName(MenuOnlyRoots[i]);
            Debug.Log($"[UseSampleScene] Stripped {removed} menu root(s) from SampleScene.");
            EditorSceneManager.SaveScene(sample);

            // Re-open MainMenuScene so the user lands where Play starts from.
            if (System.IO.File.Exists(MainMenuScenePath))
                EditorSceneManager.OpenScene(MainMenuScenePath, OpenSceneMode.Single);
        }

        Debug.Log("[UseSampleScene] ✓ Done. Press Play in MainMenuScene → Online → " +
                  "Create Room → Start Match. PhotonNetwork.LoadLevel will now load " +
                  "SampleScene directly.");
        Debug.Log("[UseSampleScene] ──────────────────────────────────────────────");
    }

    // ================================================================== //
    // Rebuild tools — copy SampleScene → target scene, strip the
    // wrong-category roots. Use these when the SampleScene gameplay
    // setup has changed (new CornerBases, new resources, new managers)
    // and the existing GameMapScene / MainMenuScene is out of date.
    // ================================================================== //

    [MenuItem("Tools/RTS/Scenes/Rebuild GameMapScene From SampleScene")]
    public static void RebuildGameMapScene()
        => RebuildSplitScene(GameMapScenePath, "GameMapScene", MenuOnlyRoots,
                             "menu/lobby root(s) (these belong to MainMenuScene)");

    [MenuItem("Tools/RTS/Scenes/Rebuild MainMenuScene From SampleScene")]
    public static void RebuildMainMenuScene()
        => RebuildSplitScene(MainMenuScenePath, "MainMenuScene", GameplayOnlyRoots,
                             "gameplay-world root(s) (these belong to GameMapScene)");

    private static void RebuildSplitScene(string targetPath, string label,
                                          string[] stripList, string what)
    {
        Debug.Log($"[Rebuild{label}] ───────────────────────────────");

        // ---- Source sanity ---------------------------------------------- //
        if (!System.IO.File.Exists(SampleScenePath))
        {
            Debug.LogError($"[Rebuild{label}] ✗ '{SampleScenePath}' not found. " +
                           "Aborting — there's no source to copy from.");
            return;
        }

        // ---- Confirm overwrite ------------------------------------------ //
        if (!EditorUtility.DisplayDialog(
                $"Rebuild {label}?",
                $"This will REPLACE '{targetPath}' with a fresh copy of " +
                $"'{SampleScenePath}' and strip {what}. " +
                $"The OTHER split scene is left untouched. SampleScene_Backup is preserved.\n\n" +
                "Continue?",
                "Rebuild", "Cancel"))
        {
            Debug.Log($"[Rebuild{label}] User cancelled.");
            return;
        }

        // ---- Save the currently-open scene if dirty --------------------- //
        Scene activeNow = EditorSceneManager.GetActiveScene();
        if (activeNow.isDirty)
        {
            int dlg = EditorUtility.DisplayDialogComplex(
                "Save current scene?",
                $"'{activeNow.name}' has unsaved changes. Save before rebuilding?",
                "Save", "Cancel", "Discard");
            if (dlg == 1) return;
            if (dlg == 0) EditorSceneManager.SaveScene(activeNow);
        }

        // ---- Delete existing target + copy fresh ------------------------ //
        if (System.IO.File.Exists(targetPath))
            AssetDatabase.DeleteAsset(targetPath);

        if (!AssetDatabase.CopyAsset(SampleScenePath, targetPath))
        {
            Debug.LogError($"[Rebuild{label}] ✗ Failed to copy '{SampleScenePath}' → '{targetPath}'.");
            return;
        }
        Debug.Log($"[Rebuild{label}] ✓ Copied '{SampleScenePath}' → '{targetPath}'.");

        // ---- Open the new copy + strip ---------------------------------- //
        Scene target = EditorSceneManager.OpenScene(targetPath, OpenSceneMode.Single);
        Debug.Log($"[Rebuild{label}] Opened '{targetPath}'. Stripping {what}...");

        int totalRemoved = 0;
        for (int i = 0; i < stripList.Length; i++)
            totalRemoved += DestroyRootByName(stripList[i]);
        Debug.Log($"[Rebuild{label}] Removed {totalRemoved} root object(s).");

        // ---- Best-effort: set useSceneSplit=true on NetworkManagerRTS --- //
        TryEnableSceneSplitFlag();

        // ---- Save ------------------------------------------------------- //
        bool saved = EditorSceneManager.SaveScene(target);
        Debug.Log($"[Rebuild{label}] {(saved ? "✓" : "✗")} Saved '{targetPath}'.");

        Debug.Log($"[Rebuild{label}] Done. Run Tools → RTS → Scenes → Validate " +
                  $"{(label == "GameMapScene" ? "GameMapScene Content" : "Scene Split")} to confirm.");
        Debug.Log($"[Rebuild{label}] ───────────────────────────────");
    }

    /// <summary>
    /// Best-effort: enable <c>NetworkManagerRTS.useSceneSplit</c> on every
    /// NetworkManager in the just-opened scene so PhotonNetwork.LoadLevel
    /// flow kicks in at runtime. Reflection-guarded so the editor tool keeps
    /// compiling even if the field is renamed.
    /// </summary>
    private static void TryEnableSceneSplitFlag()
    {
        NetworkManagerRTS[] managers = Object.FindObjectsByType<NetworkManagerRTS>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        var field = typeof(NetworkManagerRTS).GetField("useSceneSplit",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (field == null) return;
        for (int i = 0; i < managers.Length; i++)
        {
            if (managers[i] == null) continue;
            field.SetValue(managers[i], true);
            EditorUtility.SetDirty(managers[i]);
            Debug.Log($"[Rebuild] Set NetworkManagerRTS.useSceneSplit = true on " +
                      $"'{managers[i].gameObject.name}'.");
        }
    }

    // ================================================================== //
    // Cleanup tools
    // ================================================================== //

    [MenuItem("Tools/RTS/Scenes/Cleanup MainMenuScene")]
    public static void CleanupMainMenu()
    {
        RunCleanup("MainMenuScene", GameplayOnlyRoots,
                   "gameplay-world root(s) (these belong to GameMapScene)");
    }

    [MenuItem("Tools/RTS/Scenes/Cleanup GameMapScene")]
    public static void CleanupGameMap()
    {
        RunCleanup("GameMapScene", MenuOnlyRoots,
                   "menu/lobby root(s) (these belong to MainMenuScene)");
    }

    private static void RunCleanup(string expectedSceneLabel, string[] stripList, string what)
    {
        Scene active = EditorSceneManager.GetActiveScene();
        Debug.Log($"[SceneSplit:Cleanup] ── Cleaning '{active.name}' (expected: {expectedSceneLabel}) ──");
        if (active.name != expectedSceneLabel)
            Debug.LogWarning($"[SceneSplit:Cleanup] ⚠ Open scene is '{active.name}', not '{expectedSceneLabel}'. " +
                             "Proceeding anyway — strip list is name-based.");

        int totalRemoved = 0;
        for (int i = 0; i < stripList.Length; i++)
            totalRemoved += DestroyRootByName(stripList[i]);

        if (totalRemoved > 0)
            EditorSceneManager.MarkSceneDirty(active);

        Debug.Log($"[SceneSplit:Cleanup] Removed {totalRemoved} {what} from '{active.name}'. " +
                  "Ctrl+S to save.");
    }

    private static int DestroyRootByName(string name)
    {
        Transform[] all = Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int removed = 0;
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null) continue;
            if (t.parent != null) continue;
            if (t.name != name) continue;
            Object.DestroyImmediate(t.gameObject);
            removed++;
        }
        if (removed > 0)
            Debug.Log($"[SceneSplit:Cleanup]   Removed {removed}× '{name}'.");
        return removed;
    }

    // ================================================================== //
    // Validator
    // ================================================================== //

    [MenuItem("Tools/RTS/Scenes/Validate Scene Split")]
    public static void Validate()
    {
        Debug.Log("[SceneSplit:Validate] ─── Scene split health ───");
        int issues = 0;

        // ---- Active scene check (most common foot-gun) ---------------- //
        Scene activeNow = EditorSceneManager.GetActiveScene();
        Debug.Log($"[SceneSplit:Validate] ACTIVE SCENE: '{activeNow.name}' " +
                  $"({(string.IsNullOrEmpty(activeNow.path) ? "UNSAVED" : activeNow.path)})");
        if (activeNow.path == SampleScenePath || activeNow.name == "SampleScene")
        {
            Debug.LogError("[SceneSplit:Validate] ✗✗✗ Active scene is 'SampleScene' — " +
                           "the SPLIT scenes are not in use. Press Play here and you'll see " +
                           "the OLD layout (map behind menu, MultiplayerDebugCanvas, etc.). " +
                           "Run Tools → RTS → Scenes → Open MainMenuScene before pressing Play.");
            issues++;
        }
        else if (activeNow.path == MainMenuScenePath)
        {
            Debug.Log("[SceneSplit:Validate] ✓ Active scene is MainMenuScene — correct entry point.");
        }
        else if (activeNow.path == GameMapScenePath)
        {
            Debug.LogWarning("[SceneSplit:Validate] ⚠ Active scene is GameMapScene. This is OK for " +
                             "direct-play testing, but the normal entry point is MainMenuScene. " +
                             "Run Tools → RTS → Scenes → Open MainMenuScene to go back.");
        }

        // Files exist?
        bool mainExists = File.Exists(MainMenuScenePath);
        bool mapExists  = File.Exists(GameMapScenePath);
        if (mainExists) Debug.Log($"[SceneSplit:Validate] ✓ '{MainMenuScenePath}' exists.");
        else            { Debug.LogWarning($"[SceneSplit:Validate] ✗ '{MainMenuScenePath}' missing — run Create Scene Split."); issues++; }
        if (mapExists)  Debug.Log($"[SceneSplit:Validate] ✓ '{GameMapScenePath}' exists.");
        else            { Debug.LogWarning($"[SceneSplit:Validate] ✗ '{GameMapScenePath}' missing — run Create Scene Split."); issues++; }

        // Build Settings order
        EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
        int mainIdx = -1, mapIdx = -1;
        for (int i = 0; i < scenes.Length; i++)
        {
            if (scenes[i].path == MainMenuScenePath) mainIdx = i;
            if (scenes[i].path == GameMapScenePath)  mapIdx  = i;
        }
        if (mainIdx == 0) Debug.Log("[SceneSplit:Validate] ✓ MainMenuScene is at Build Settings index 0.");
        else              { Debug.LogWarning($"[SceneSplit:Validate] ✗ MainMenuScene is at index {mainIdx} (expected 0)."); issues++; }
        if (mapIdx >= 0)  Debug.Log($"[SceneSplit:Validate] ✓ GameMapScene is in Build Settings (index {mapIdx}).");
        else              { Debug.LogWarning("[SceneSplit:Validate] ✗ GameMapScene not in Build Settings."); issues++; }

        // Open-scene category audit
        Scene active = EditorSceneManager.GetActiveScene();
        if (active.path == MainMenuScenePath)
            issues += ScanLeftoverRoots(active.name, GameplayOnlyRoots, "gameplay");
        else if (active.path == GameMapScenePath)
            issues += ScanLeftoverRoots(active.name, MenuOnlyRoots, "menu/lobby");
        else
            Debug.Log($"[SceneSplit:Validate]   Open scene '{active.name}' is neither split scene — " +
                      "open MainMenuScene/GameMapScene to audit their contents.");

        // ---- Multiplayer Debug Canvas in active scene ----------------- //
        Transform[] all = Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int debugCanvasCount = 0;
        bool debugCanvasActive = false;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null || all[i].parent != null) continue;
            if (all[i].name != "MultiplayerDebugCanvas") continue;
            debugCanvasCount++;
            if (all[i].gameObject.activeSelf) debugCanvasActive = true;
        }
        if (debugCanvasCount == 0)
        {
            Debug.Log("[SceneSplit:Validate] ✓ No MultiplayerDebugCanvas in the open scene.");
        }
        else
        {
            Debug.LogWarning($"[SceneSplit:Validate] ⚠ MultiplayerDebugCanvas x{debugCanvasCount} " +
                             $"in the open scene (active={debugCanvasActive}). " +
                             "It should not appear in the normal player flow. Run " +
                             "Tools → RTS → UI → Toggle Multiplayer Debug Canvas to hide it, or " +
                             "Tools → RTS → UI → Remove Multiplayer Debug Canvas to delete it.");
            issues++;
        }

        Debug.Log(issues == 0
            ? "[SceneSplit:Validate] ✓ Scene split looks healthy."
            : $"[SceneSplit:Validate] ✗ {issues} issue(s). Re-run Create Scene Split or the matching Cleanup tool.");
        Debug.Log("[SceneSplit:Validate] ─────────────────────────");
    }

    // ================================================================== //
    // Validate GameMapScene Content — full gameplay-roots audit
    // ================================================================== //

    [MenuItem("Tools/RTS/Match/Validate SampleScene Gameplay Setup")]
    public static void ValidateSampleSceneGameplay() => ValidateGameplayContent(expectedName: "SampleScene");

    [MenuItem("Tools/RTS/Scenes/Validate GameMapScene Content")]
    public static void ValidateGameMapContent() => ValidateGameplayContent(expectedName: "GameMapScene");

    private static void ValidateGameplayContent(string expectedName)
    {
        Debug.Log($"[ValidateGameplay] ─── Gameplay-scene content audit (expecting '{expectedName}') ───");

        Scene active = EditorSceneManager.GetActiveScene();
        Debug.Log($"[ValidateGameplay] Active scene: '{active.name}' ({active.path})");

        // Tolerant gate: accept either the explicitly-named scene OR the
        // scene the live NetworkManagerRTS.gameMapSceneName points at OR
        // SampleScene (the current default gameplay target).
        bool isExpected = active.name == expectedName ||
                          active.name == "SampleScene" ||
                          active.name == "GameMapScene";
        if (!isExpected)
        {
            Debug.LogError($"[ValidateGameplay] ✗ Open scene is '{active.name}', " +
                           $"not the gameplay scene. Open SampleScene (or whatever " +
                           "NetworkManagerRTS.gameMapSceneName points at) and re-run.");
            Debug.Log("[ValidateGameplay] ─────────────────────────────────");
            return;
        }

        int issues = 0;

        // ---- Required gameplay roots ---------------------------------- //
        issues += ExpectRoot("GameplayWorldRoot",  isGameplay: true);
        issues += ExpectRoot("HUDCanvas",          isGameplay: true);
        issues += ExpectRoot("SelectionCanvas",    isGameplay: true);
        issues += ExpectRoot("EscapeMenuCanvas",   isGameplay: true);
        issues += ExpectRoot("CameraRig",          isGameplay: true);
        issues += ExpectRoot("MinimapCameraGO",    isGameplay: true);
        issues += ExpectRoot("MatchManager",       isGameplay: true);
        issues += ExpectRoot("Environment",        isGameplay: true);

        // ---- CornerBases (must be 4 with unique indices 0..3) --------- //
        CornerBase[] corners = Object.FindObjectsByType<CornerBase>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (corners.Length < 4)
        {
            Debug.LogError($"[ValidateGameMap] ✗ Only {corners.Length} CornerBase(s) " +
                           "(need 4). Run Tools → RTS → Match → Setup Multiplayer Match Map.");
            issues++;
        }
        else if (corners.Length > 4)
        {
            Debug.LogWarning($"[ValidateGameMap] ⚠ {corners.Length} CornerBase(s) " +
                             "(expected 4). Extras may indicate stale objects.");
            issues++;
        }
        else
        {
            Debug.Log("[ValidateGameMap] ✓ 4 CornerBase components present.");
        }

        var seenIndices = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < corners.Length; i++)
        {
            CornerBase cb = corners[i];
            if (cb == null) continue;

            string n = cb.gameObject.name;
            if (cb.cornerIndex < 0 || cb.cornerIndex > 3)
            {
                Debug.LogError($"[ValidateGameMap] ✗ {n}: cornerIndex {cb.cornerIndex} out of range 0..3.");
                issues++;
            }
            else if (!seenIndices.Add(cb.cornerIndex))
            {
                Debug.LogError($"[ValidateGameMap] ✗ {n}: duplicate cornerIndex {cb.cornerIndex}.");
                issues++;
            }

            if (cb.dozer == null)
            {
                Debug.LogError($"[ValidateGameMap] ✗ {n}: dozer reference is NULL.");
                issues++;
            }
            if (cb.bank == null)
            {
                Debug.LogError($"[ValidateGameMap] ✗ {n}: bank (PlayerResourceManager) reference is NULL.");
                issues++;
            }
            if (cb.resourceCluster == null)
            {
                Debug.LogError($"[ValidateGameMap] ✗ {n}: resourceCluster reference is NULL.");
                issues++;
            }
            else if (cb.resourceCluster.GetComponentsInChildren<ResourceNode>(true).Length == 0)
            {
                Debug.LogError($"[ValidateGameMap] ✗ {n}: resourceCluster has no ResourceNodes.");
                issues++;
            }
        }

        // ---- ResourceNodes overall count ------------------------------- //
        int totalRN = Object.FindObjectsByType<ResourceNode>(
            FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        if (totalRN == 0)
        {
            Debug.LogError("[ValidateGameMap] ✗ No ResourceNodes in scene.");
            issues++;
        }
        else
        {
            Debug.Log($"[ValidateGameMap] ✓ {totalRN} ResourceNode(s) present.");
        }

        // ---- NetworkManagerRTS + useSceneSplit ------------------------ //
        NetworkManagerRTS nm = Object.FindFirstObjectByType<NetworkManagerRTS>(
            FindObjectsInactive.Include);
        if (nm == null)
        {
            Debug.LogError("[ValidateGameMap] ✗ NetworkManagerRTS missing — Photon flow won't initialise.");
            issues++;
        }
        else
        {
            Debug.Log("[ValidateGameMap] ✓ NetworkManagerRTS present.");
            var field = typeof(NetworkManagerRTS).GetField("useSceneSplit",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (field != null && field.GetValue(nm) is bool b && !b)
            {
                Debug.LogWarning("[ValidateGameMap] ⚠ NetworkManagerRTS.useSceneSplit = false. " +
                                 "Match start won't use PhotonNetwork.LoadLevel — set it true.");
                issues++;
            }
        }

        // ---- Forbidden menu roots ------------------------------------- //
        for (int i = 0; i < MenuOnlyRoots.Length; i++)
        {
            if (FindRoot(MenuOnlyRoots[i]) != null)
            {
                Debug.LogError($"[ValidateGameMap] ✗ Forbidden menu root '{MenuOnlyRoots[i]}' " +
                               "still present in GameMapScene. Run Cleanup GameMapScene or Rebuild.");
                issues++;
            }
        }

        Debug.Log(issues == 0
            ? "[ValidateGameMap] ✓ GameMapScene content OK — ready for match start."
            : $"[ValidateGameMap] ✗ {issues} issue(s). Run Rebuild GameMapScene From SampleScene " +
              "if the gameplay setup is missing.");
        Debug.Log("[ValidateGameMap] ─────────────────────────────────");
    }

    private static int ExpectRoot(string name, bool isGameplay)
    {
        GameObject go = FindRoot(name);
        if (go != null) { Debug.Log($"[ValidateGameMap] ✓ '{name}' present."); return 0; }
        Debug.LogError($"[ValidateGameMap] ✗ '{name}' missing from GameMapScene.");
        return 1;
    }

    private static GameObject FindRoot(string name)
    {
        Transform[] all = Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null || all[i].parent != null) continue;
            if (all[i].name == name) return all[i].gameObject;
        }
        return null;
    }

    private static int ScanLeftoverRoots(string sceneName, string[] forbidden, string category)
    {
        Transform[] all = Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int leftover = 0;
        var forbiddenSet = new HashSet<string>(forbidden);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null) continue;
            if (t.parent != null) continue;
            if (!forbiddenSet.Contains(t.name)) continue;
            Debug.LogWarning($"[SceneSplit:Validate] ✗ '{sceneName}' still contains {category} root '{t.name}'.");
            leftover++;
        }
        if (leftover == 0)
            Debug.Log($"[SceneSplit:Validate] ✓ '{sceneName}' has no leftover {category} roots.");
        return leftover;
    }
}
