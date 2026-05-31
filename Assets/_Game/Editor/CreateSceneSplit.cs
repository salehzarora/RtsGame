using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// One-click "split SampleScene into MainMenuScene + GameMapScene" editor
/// tool. Safe-by-default — backs up the source scene first, never destroys
/// the original.
///
/// Menu: Tools → RTS → Scenes → Create Scene Split
///
/// Pipeline:
///   1. <b>Backup</b> — copies the source scene to <c>SampleScene_Backup.unity</c>
///      next to the original. You can revert at any time by deleting the new
///      scenes and renaming the backup.
///   2. <b>Create MainMenuScene</b> — copies the source, opens the copy, then
///      strips every <i>gameplay-world</i> object (Environment, GameplayWorldRoot
///      container, HUDCanvas, etc.). Saves.
///   3. <b>Create GameMapScene</b> — copies the source, opens the copy, then
///      strips every <i>menu</i> object (LobbyCanvas, MainMenuCanvas,
///      OptionsCanvas, MultiplayerDebugCanvas). Saves.
///   4. <b>Build Settings</b> — puts MainMenuScene at index 0, GameMapScene at
///      index 1. Other scenes pushed back.
///   5. Opens MainMenuScene at the end so you can press Play immediately.
///
/// This does NOT change runtime gameplay logic. To actually use the split at
/// runtime, set <c>NetworkManagerRTS.useSceneSplit = true</c> in both new
/// scenes (the tool tries to do this automatically when the component exists).
///
/// Re-running is safe: any pre-existing MainMenuScene/GameMapScene are
/// overwritten by the new copies (with a confirmation prompt). The backup is
/// only created on the first run — subsequent runs preserve it.
/// </summary>
public static class CreateSceneSplit
{
    private const string ScenesFolder       = "Assets/Scenes";
    private const string SourceScenePath    = "Assets/Scenes/SampleScene.unity";
    private const string BackupScenePath    = "Assets/Scenes/SampleScene_Backup.unity";
    private const string MainMenuScenePath  = "Assets/Scenes/MainMenuScene.unity";
    private const string GameMapScenePath   = "Assets/Scenes/GameMapScene.unity";

    // Root GameObjects whose presence belongs to GAMEPLAY only — strip from
    // MainMenuScene. Name-based, root-only. Add to this list if the project
    // adds another gameplay-only root.
    //
    // 'MultiplayerDebugCanvas' is also here so it does NOT bleed into the
    // menu's normal flow. (It's also in MenuOnlyRoots → stripped from
    // GameMapScene too, so the debug panel is gone from BOTH new scenes.)
    private static readonly string[] GameplayOnlyRoots =
    {
        "Environment",
        "ResourceNodes",
        "PlayerStart",
        "EnemyStart",
        "GameplayWorldRoot",      // baker's container holding CornerBases
        "Player0Base",            // legacy 2-base layout
        "Player1Base",
        "HUDCanvas",
        "EscapeMenuCanvas",
        "SelectionCanvas",
        "MinimapCameraGO",
        "MatchManager",
        "MultiplayerDebugCanvas", // never part of the real menu flow
    };

    // Root GameObjects whose presence belongs to MENU/LOBBY only — strip from
    // GameMapScene.
    private static readonly string[] MenuOnlyRoots =
    {
        "LobbyCanvas",
        "MainMenuCanvas",
        "OptionsCanvas",
        "MultiplayerDebugCanvas", // not part of the gameplay scene either
    };

    [MenuItem("Tools/RTS/Scenes/Create Scene Split")]
    public static void Run()
    {
        Debug.Log("[SceneSplit] ════════════════════════════════════════════════");
        Debug.Log("[SceneSplit] Creating MainMenuScene + GameMapScene from SampleScene...");

        // ---- Source sanity ------------------------------------------------ //
        if (!File.Exists(SourceScenePath))
        {
            Debug.LogError($"[SceneSplit] ✗ Source scene not found at '{SourceScenePath}'. " +
                           "This tool expects the existing SampleScene to live there.");
            return;
        }

        if (!Directory.Exists(ScenesFolder))
        {
            Debug.LogError($"[SceneSplit] ✗ '{ScenesFolder}' folder missing. Aborting.");
            return;
        }

        // ---- Save current scene if dirty so we don't lose anything ------- //
        Scene activeScene = EditorSceneManager.GetActiveScene();
        if (activeScene.isDirty)
        {
            bool save = EditorUtility.DisplayDialog("Save current scene?",
                $"Current scene '{activeScene.name}' has unsaved changes. " +
                "Save before continuing?", "Save", "Discard");
            if (save) EditorSceneManager.SaveScene(activeScene);
        }

        // ---- Backup source ----------------------------------------------- //
        if (!File.Exists(BackupScenePath))
        {
            if (!AssetDatabase.CopyAsset(SourceScenePath, BackupScenePath))
            {
                Debug.LogError($"[SceneSplit] ✗ Failed to back up '{SourceScenePath}' to '{BackupScenePath}'. Aborting.");
                return;
            }
            Debug.Log($"[SceneSplit] ✓ Backed up source to '{BackupScenePath}'.");
        }
        else
        {
            Debug.Log($"[SceneSplit]   Backup already exists at '{BackupScenePath}' — preserving it.");
        }

        // ---- Confirm overwrite of any pre-existing split scenes ---------- //
        bool mainExists = File.Exists(MainMenuScenePath);
        bool mapExists  = File.Exists(GameMapScenePath);
        if (mainExists || mapExists)
        {
            string msg = "These scenes already exist and will be OVERWRITTEN:\n\n"
                       + (mainExists ? $"  • {MainMenuScenePath}\n" : "")
                       + (mapExists  ? $"  • {GameMapScenePath}\n"  : "")
                       + "\nThe backup at SampleScene_Backup.unity is preserved.";
            if (!EditorUtility.DisplayDialog("Overwrite split scenes?", msg, "Overwrite", "Cancel"))
            {
                Debug.Log("[SceneSplit] User cancelled.");
                return;
            }
            if (mainExists) AssetDatabase.DeleteAsset(MainMenuScenePath);
            if (mapExists)  AssetDatabase.DeleteAsset(GameMapScenePath);
        }

        // ---- Create + strip MainMenuScene ------------------------------- //
        if (!AssetDatabase.CopyAsset(SourceScenePath, MainMenuScenePath))
        {
            Debug.LogError($"[SceneSplit] ✗ Failed to copy source to '{MainMenuScenePath}'. Aborting.");
            return;
        }
        StripScene(MainMenuScenePath, "MainMenuScene", stripList: GameplayOnlyRoots,
                   setUseSceneSplit: true);

        // ---- Create + strip GameMapScene -------------------------------- //
        if (!AssetDatabase.CopyAsset(SourceScenePath, GameMapScenePath))
        {
            Debug.LogError($"[SceneSplit] ✗ Failed to copy source to '{GameMapScenePath}'. Aborting.");
            return;
        }
        StripScene(GameMapScenePath, "GameMapScene", stripList: MenuOnlyRoots,
                   setUseSceneSplit: true);

        // ---- Build Settings --------------------------------------------- //
        UpdateBuildSettings();

        // ---- Open MainMenuScene at the end ------------------------------ //
        EditorSceneManager.OpenScene(MainMenuScenePath, OpenSceneMode.Single);

        Debug.Log("[SceneSplit] ════════════════════════════════════════════════");
        Debug.Log("[SceneSplit] ✓ Done. MainMenuScene + GameMapScene created. " +
                  "Press Play — you should see only the main menu.");
        Debug.Log("[SceneSplit] Run Tools → RTS → Scenes → Validate Scene Split to confirm, " +
                  "and see TESTING_GUIDE.md for the new flow.");
        Debug.Log("[SceneSplit] ════════════════════════════════════════════════");
    }

    // ------------------------------------------------------------------ //
    // Strip helpers
    // ------------------------------------------------------------------ //

    private static void StripScene(string scenePath, string label, string[] stripList,
                                   bool setUseSceneSplit)
    {
        Debug.Log($"[SceneSplit] ── Stripping {label} ──");
        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

        int totalRemoved = 0;
        for (int i = 0; i < stripList.Length; i++)
            totalRemoved += DestroyRootByName(stripList[i]);

        Debug.Log($"[SceneSplit] {label}: removed {totalRemoved} root object(s) " +
                  $"({string.Join(", ", stripList)}).");

        if (setUseSceneSplit) TryEnableSceneSplitFlag();

        bool saved = EditorSceneManager.SaveScene(scene);
        Debug.Log($"[SceneSplit] {label}: saved = {saved}.");
    }

    /// <summary>Destroy every root GameObject in the open scene whose name matches.</summary>
    private static int DestroyRootByName(string name)
    {
        Transform[] all = Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int removed = 0;
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null) continue;
            if (t.parent != null) continue;          // root only
            if (t.name != name) continue;
            Object.DestroyImmediate(t.gameObject);
            removed++;
        }
        if (removed > 0)
            Debug.Log($"[SceneSplit]   Removed {removed}× '{name}'.");
        return removed;
    }

    /// <summary>
    /// Best-effort: set <c>NetworkManagerRTS.useSceneSplit = true</c> on every
    /// NetworkManagerRTS in the just-stripped scene so the new runtime flow
    /// kicks in. Tolerates the field missing (older builds) — guarded behind a
    /// reflection lookup so this editor tool compiles regardless of the
    /// NetworkManagerRTS schema.
    /// </summary>
    private static void TryEnableSceneSplitFlag()
    {
        var managers = Object.FindObjectsByType<NetworkManagerRTS>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < managers.Length; i++)
        {
            if (managers[i] == null) continue;
            var f = typeof(NetworkManagerRTS).GetField("useSceneSplit",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (f == null) continue;
            f.SetValue(managers[i], true);
            EditorUtility.SetDirty(managers[i]);
            Debug.Log($"[SceneSplit]   Set NetworkManagerRTS.useSceneSplit = true on '{managers[i].gameObject.name}'.");
        }
    }

    // ------------------------------------------------------------------ //
    // Build Settings
    // ------------------------------------------------------------------ //

    private static void UpdateBuildSettings()
    {
        EditorBuildSettingsScene[] current = EditorBuildSettings.scenes;
        var ordered = new List<EditorBuildSettingsScene>();

        ordered.Add(new EditorBuildSettingsScene(MainMenuScenePath, enabled: true));
        ordered.Add(new EditorBuildSettingsScene(GameMapScenePath,  enabled: true));

        for (int i = 0; i < current.Length; i++)
        {
            string p = current[i].path;
            if (string.IsNullOrEmpty(p)) continue;
            if (p == MainMenuScenePath || p == GameMapScenePath) continue;
            ordered.Add(current[i]);
        }

        EditorBuildSettings.scenes = ordered.ToArray();
        Debug.Log($"[SceneSplit] ✓ Build Settings updated: [0]={MainMenuScenePath}, " +
                  $"[1]={GameMapScenePath} (total {ordered.Count}).");
    }
}
