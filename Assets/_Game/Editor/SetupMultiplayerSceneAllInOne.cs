using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Phase 7 — one-click "set up everything" for a multiplayer scene. Runs the
/// six existing setup tools in the order Photon + the lobby flow need:
///
///   1. SetupRTSHUD          — gameplay HUD (so MatchStart has something to show)
///   2. SetupMainMenu        — main menu canvas with Single Player + Online buttons
///   3. SetupNetworkManager  — NetworkManagerRTS + NetworkCommandRelay + NetworkMatchCoordinator
///   4. SetupMultiplayerDebugUI    — optional dev panel (hidden by default per Phase 7)
///   5. SetupMultiplayerLobbyUI    — LobbyCanvas + 4 panels + MultiplayerLobbyUI wiring
///   6. SetupMultiplayerMatchMap   — Player0Base + Player1Base + GameplayWorldRoot
///   7. AddGameEntityToSceneObjects — deterministic ids stamped + saved
///
/// Menu: Tools → RTS → Multiplayer → Setup Multiplayer Scene (All-In-One)
///
/// Why this exists: the user kept hitting issues where one of the six tools
/// was forgotten / run in the wrong order. The lobby panel didn't exist; the
/// network manager wasn't wired; the bases weren't stamped with ownership;
/// the world wasn't hidden. This tool removes the foot-gun.
///
/// Safe to re-run — each underlying tool is idempotent (destroys + rebuilds
/// its canvas or component cleanly).
/// </summary>
public static class SetupMultiplayerSceneAllInOne
{
    [MenuItem("Tools/RTS/Multiplayer/Setup Multiplayer Scene (All-In-One)")]
    public static void Run()
    {
        Debug.Log("[SetupMPSceneAllInOne] ══════════════════════════════════════════════════");
        Debug.Log("[SetupMPSceneAllInOne] Running every multiplayer setup step in order...");
        Debug.Log("[SetupMPSceneAllInOne] ══════════════════════════════════════════════════");

        RunStep("Setup Gameplay HUD",          SetupRTSHUD.SetupHUD);
        RunStep("Setup Main Menu",             SetupMainMenu.Run);
        RunStep("Setup Network Manager",       SetupNetworkManager.Run);
        RunStep("Setup Multiplayer Debug UI",  SetupMultiplayerDebugUI.Run);
        RunStep("Setup Multiplayer Lobby UI",  SetupMultiplayerLobbyUI.Run);
        RunStep("Setup Multiplayer Match Map", SetupMultiplayerMatchMap.Run);
        RunStep("Add GameEntity To Scene Objects", AddGameEntityToSceneObjects.Run);

        // Save the scene so the freshly-stamped GameEntity ids + the
        // GameplayWorldRoot targets persist on disk.
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);

        Debug.Log("[SetupMPSceneAllInOne] ══════════════════════════════════════════════════");
        Debug.Log($"[SetupMPSceneAllInOne] All-In-One complete. Scene saved: {saved}.");
        Debug.Log("[SetupMPSceneAllInOne] Ready to test: press Play → Online → Connect → Create Room → Lobby → Start.");
        Debug.Log("[SetupMPSceneAllInOne] ══════════════════════════════════════════════════");
    }

    private static void RunStep(string label, System.Action action)
    {
        Debug.Log($"[SetupMPSceneAllInOne] ── Step: {label} ──");
        try
        {
            action();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[SetupMPSceneAllInOne] ✗ '{label}' threw: {e.Message}\n{e.StackTrace}");
        }
    }
}
