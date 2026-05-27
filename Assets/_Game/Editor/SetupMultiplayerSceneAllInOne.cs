using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Phase 7 / Phase 10 — one-click "set up everything" for a multiplayer
/// scene. Runs the underlying setup tools in the order Photon + the lobby
/// flow need:
///
///   1. SetupRTSHUD                  — gameplay HUD (so MatchStart has something to show)
///   2. SetupMainMenu                — main menu canvas with Single Player + Online buttons
///   3. SetupNetworkManager          — NetworkManagerRTS + NetworkCommandRelay + NetworkMatchCoordinator
///   4. SetupMultiplayerDebugUI      — optional dev panel (hidden by default per Phase 7)
///   5. SetupMultiplayerLobbyUI      — LobbyCanvas + 4 panels + MultiplayerLobbyUI wiring
///   6. CreateCommandCenterPrefab    — Phase 10: rebuild Assets/_Game/Prefabs/CommandCenterPrefab.prefab
///                                     so the Dozer "Command Center" build button has a fresh prefab
///   7. SetupMultiplayerMatchMap     — Player0Base + Player1Base (Dozer-only start) + GameplayWorldRoot
///   8. RepairCommandCenterProduction — sweep every scene CC + the CC prefab to keep Worker+Dozer wired
///   9. AddGameEntityToSceneObjects  — deterministic ids stamped + saved
///
/// Menu: Tools → RTS → Multiplayer → Setup Multiplayer Scene (All-In-One)
///
/// Why this exists: the user kept hitting issues where one of the tools was
/// forgotten / run in the wrong order. The lobby panel didn't exist; the
/// network manager wasn't wired; the bases weren't stamped with ownership;
/// the world wasn't hidden; the CC prefab was missing. This tool removes
/// the foot-gun.
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
        // Phase 10.10 — ESC pause menu (Resume / Main Menu / Quit). Must run
        // AFTER SetupMainMenu so MainMenuCanvas / HUDCanvas references can be
        // auto-wired by name lookup.
        RunStep("Setup Escape Menu",           SetupEscapeMenu.Run);
        RunStep("Setup Network Manager",       SetupNetworkManager.Run);
        RunStep("Setup Multiplayer Debug UI",  SetupMultiplayerDebugUI.Run);
        RunStep("Setup Multiplayer Lobby UI",  SetupMultiplayerLobbyUI.Run);

        // Phase 10 — rebuild the CommandCenter prefab on disk BEFORE the match
        // map step so SetupMultiplayerMatchMap can auto-bind it onto the
        // scene's BuildingPlacementManager. The Dozer "Command Center" build
        // button reads from BPM.commandCenterPrefab, so this prefab must
        // exist before any Dozer is allowed to construct one.
        RunStep("Create CommandCenter Prefab", CreateCommandCenterPrefab.Create);

        RunStep("Setup Multiplayer Match Map", SetupMultiplayerMatchMap.Run);

        // Phase 8 — Repair runs AFTER the match map so it sweeps every
        // CommandCenter the map tool just created and any pre-existing ones,
        // making sure Worker + Dozer prefabs are wired permanently. If a
        // future HUD/setup rebuild ever clears those refs, re-running the
        // All-In-One restores them.
        RunStep("Repair CommandCenter Production", RepairCommandCenterProduction.Run);

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
