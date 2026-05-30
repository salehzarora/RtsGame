using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

#if PHOTON_UNITY_NETWORKING
using Photon.Pun;
#endif

/// <summary>
/// Editor sanity check for the multiplayer scene wiring. Lists the Build
/// Settings scenes, the open scene, the Photon scene-sync setting, and
/// whether the key managers exist exactly once in the open scene.
///
/// This does NOT modify anything — it only reports. The recommendation
/// printed at the end nudges you toward a cleaner scene flow (Bootstrap
/// scene → GameMapScene), but the current single-scene layout is supported
/// and won't be re-arranged by this tool.
///
/// Menu: Tools → RTS → Scenes → Validate Multiplayer Scene Setup
/// </summary>
public static class ValidateMultiplayerSceneSetup
{
    [MenuItem("Tools/RTS/Scenes/Validate Multiplayer Scene Setup")]
    public static void Run()
    {
        Debug.Log("[ValidateScenes] ─── Multiplayer scene setup ───");

        // ---- Build Settings scenes ------------------------------------ //
        EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
        Debug.Log($"[ValidateScenes] Build Settings has {scenes.Length} scene(s):");
        if (scenes.Length == 0)
            Debug.LogWarning("[ValidateScenes] ✗ No scenes in Build Settings. Add at least one (File → Build Settings).");
        for (int i = 0; i < scenes.Length; i++)
        {
            Debug.Log($"[ValidateScenes]   [{i}] {(scenes[i].enabled ? "ON " : "OFF")} {scenes[i].path}");
        }

        if (scenes.Length > 0)
        {
            string firstPath = scenes[0].path;
            string firstName = System.IO.Path.GetFileNameWithoutExtension(firstPath);
            Debug.Log($"[ValidateScenes] First (boot) scene: '{firstName}' ({firstPath}).");
        }

        // ---- Active / open scene -------------------------------------- //
        Scene active = EditorSceneManager.GetActiveScene();
        Debug.Log($"[ValidateScenes] Active scene: '{active.name}' " +
                  $"({(string.IsNullOrEmpty(active.path) ? "UNSAVED" : active.path)}).");

        // ---- Photon scene-sync ---------------------------------------- //
#if PHOTON_UNITY_NETWORKING
        Debug.Log($"[ValidateScenes] PhotonNetwork.AutomaticallySyncScene = " +
                  $"{PhotonNetwork.AutomaticallySyncScene} " +
                  "(false = clients reveal world via GameplayWorldRoot; true = master drives PhotonNetwork.LoadLevel).");
#else
        Debug.Log("[ValidateScenes] Photon PUN not installed — skipping scene-sync check.");
#endif

        // ---- Single-instance managers in this scene ------------------- //
        int issues = 0;
        issues += ExpectOne<NetworkManagerRTS>("NetworkManagerRTS");
        issues += ExpectOne<NetworkMatchCoordinator>("NetworkMatchCoordinator");
        issues += ExpectOne<MultiplayerMatchStarter>("MultiplayerMatchStarter");
        issues += ExpectOne<GameplayWorldRoot>("GameplayWorldRoot (component)");
        issues += ExpectOne<MultiplayerLobbyUI>("MultiplayerLobbyUI");
        issues += ExpectOne<MainMenuController>("MainMenuController");
        issues += ExpectOne<EscapeMenuController>("EscapeMenuController");
        issues += ExpectOne<AudioManager>("AudioManager");
        issues += ExpectOne<UnityEngine.EventSystems.EventSystem>("EventSystem");

        // The visible GameplayWorldRoot container GameObject should also exist exactly once.
        issues += ExpectOneRootGO("GameplayWorldRoot (container GO)", "GameplayWorldRoot");

        Debug.Log(issues == 0
            ? "[ValidateScenes] ✓ Scene setup OK — every key manager is present exactly once."
            : $"[ValidateScenes] ✗ {issues} scene-setup issue(s). For UI/manager duplicates, " +
              "run Tools → RTS → UI → Cleanup Duplicate Runtime UI.");

        // ---- Recommendation ------------------------------------------- //
        Debug.Log("[ValidateScenes] Note: this project currently runs everything inside one " +
                  "scene. GameplayWorldRoot hides the gameplay world until MatchStart, so " +
                  "the lobby + map can co-exist. A future Bootstrap/MainMenu → GameMapScene " +
                  "split is supported but optional — keep the single-scene layout for now.");
        Debug.Log("[ValidateScenes] ────────────────────────────────");
    }

    private static int ExpectOne<T>(string label) where T : Component
    {
        T[] all = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        int n = 0;
        for (int i = 0; i < all.Length; i++) if (all[i] != null) n++;

        if (n == 1)      { Debug.Log($"[ValidateScenes] ✓ {label} x1");           return 0; }
        if (n == 0)      { Debug.LogWarning($"[ValidateScenes] ✗ {label} MISSING."); return 1; }
                          Debug.LogWarning($"[ValidateScenes] ✗ {label} x{n} — DUPLICATES.");
        return 1;
    }

    private static int ExpectOneRootGO(string label, string goName)
    {
        Transform[] all = Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int n = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null) continue;
            if (all[i].parent != null) continue;
            if (all[i].name != goName) continue;
            n++;
        }
        if (n == 1)      { Debug.Log($"[ValidateScenes] ✓ {label} x1");           return 0; }
        if (n == 0)      { Debug.LogWarning($"[ValidateScenes] ✗ {label} MISSING."); return 1; }
                          Debug.LogWarning($"[ValidateScenes] ✗ {label} x{n} — DUPLICATES.");
        return 1;
    }
}
