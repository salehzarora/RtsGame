using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click tool that creates (or refreshes) the NetworkManager GameObject
/// in the active scene with both <see cref="NetworkManagerRTS"/> and
/// <see cref="NetworkCommandRelay"/> attached.
///
/// Menu: Tools → RTS → Multiplayer → Setup Network Manager
///
/// Safe to run multiple times — the existing GameObject is reused if found,
/// only missing components are added. No Inspector values are clobbered.
///
/// Photon installation is checked but NOT required: the tool runs the same
/// either way. With PUN missing, the scripts compile dormant and log a
/// warning at runtime when multiplayerMode is flipped on.
/// </summary>
public static class SetupNetworkManager
{
    private const string GameObjectName = "NetworkManager";

    [MenuItem("Tools/RTS/Multiplayer/Setup Network Manager")]
    public static void Run()
    {
        Debug.Log("[SetupNetworkManager] ── Ensuring NetworkManager GameObject ──");

        GameObject go = GameObject.Find(GameObjectName);
        bool created = false;
        if (go == null)
        {
            go = new GameObject(GameObjectName);
            Undo.RegisterCreatedObjectUndo(go, "Create NetworkManager");
            created = true;
        }

        bool addedManager     = EnsureComponent<NetworkManagerRTS>(go, out NetworkManagerRTS _);
        bool addedRelay       = EnsureComponent<NetworkCommandRelay>(go, out NetworkCommandRelay _);
        bool addedCoordinator = EnsureComponent<NetworkMatchCoordinator>(go, out NetworkMatchCoordinator _);
        // Phase 10.3 — match-state event bus (damage / death / resource sync).
        bool addedMatchEvents = EnsureComponent<NetworkMatchEvents>(go, out NetworkMatchEvents _);
        // Phase 10.7 — periodic state snapshot + runtime sync validator.
        bool addedStateSync   = EnsureComponent<NetworkEntityStateSync>(go, out NetworkEntityStateSync _);
        bool addedValidator   = EnsureComponent<NetworkSyncValidator>(go, out NetworkSyncValidator _);

        EditorUtility.SetDirty(go);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log($"[SetupNetworkManager] ✓ '{GameObjectName}' GameObject " +
                  $"{(created ? "created" : "found")}. " +
                  $"NetworkManagerRTS: {(addedManager ? "added" : "already present")}, " +
                  $"NetworkCommandRelay: {(addedRelay ? "added" : "already present")}, " +
                  $"NetworkMatchCoordinator: {(addedCoordinator ? "added" : "already present")}, " +
                  $"NetworkMatchEvents: {(addedMatchEvents ? "added" : "already present")}, " +
                  $"NetworkEntityStateSync: {(addedStateSync ? "added" : "already present")}, " +
                  $"NetworkSyncValidator: {(addedValidator ? "added" : "already present")}.");

#if PHOTON_UNITY_NETWORKING
        Debug.Log("[SetupNetworkManager] Photon PUN detected (PHOTON_UNITY_NETWORKING " +
                  "is defined). Set an App ID in PhotonServerSettings, tick " +
                  "NetworkManagerRTS.multiplayerMode, and press Play to test.");
#else
        Debug.LogWarning("[SetupNetworkManager] Photon PUN is NOT installed yet — the " +
                         "scripts will compile dormant. See PHOTON_SETUP.md for the " +
                         "Asset Store install + App ID steps.");
#endif
    }

    private static bool EnsureComponent<T>(GameObject go, out T comp) where T : Component
    {
        comp = go.GetComponent<T>();
        if (comp != null) return false;
        comp = go.AddComponent<T>();
        return true;
    }
}
