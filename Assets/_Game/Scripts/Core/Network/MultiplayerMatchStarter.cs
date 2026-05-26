using UnityEngine;

/// <summary>
/// Reacts to <see cref="NetworkMatchCoordinator.OnMatchStarted"/> once the
/// shared MatchStart event has fired. Snaps the gameplay camera over the
/// local player's base and re-applies the local-perspective team remap on
/// every <see cref="GameEntity"/> in the scene.
///
/// Phase 4 change: dropped the polling loop that watched
/// <see cref="NetworkManagerRTS.LocalPlayerId"/> tick from -1 → 0/1. Now the
/// coordinator pushes a single event the moment the slot mapping is
/// authoritative, so we apply once and stop.
///
/// Single-player: the coordinator fires synchronously when the menu Play
/// runs, so the camera snap still happens — just with slot 0 = local.
/// </summary>
[DisallowMultipleComponent]
public class MultiplayerMatchStarter : MonoBehaviour
{
    [Header("Per-player camera positions (set up by SetupMultiplayerMatchMap)")]
    [Tooltip("Where the gameplay camera rig should be placed for the local " +
             "player when LocalPlayerId == 0.")]
    public Vector3 player0CameraPos = new Vector3(-80f, 0f, -70f);

    [Tooltip("Where the gameplay camera rig should be placed for the local " +
             "player when LocalPlayerId == 1.")]
    public Vector3 player1CameraPos = new Vector3(80f, 0f, 70f);

    private bool applied;

    private void OnEnable()
    {
        NetworkMatchCoordinator.OnMatchStarted += HandleMatchStarted;
    }

    private void OnDisable()
    {
        NetworkMatchCoordinator.OnMatchStarted -= HandleMatchStarted;
    }

    private void HandleMatchStarted()
    {
        // Single-fire guard. The coordinator is intended to invoke
        // OnMatchStarted once per match, but defensive in case a future
        // re-entry happens.
        if (applied) return;

        // Phase 4: skip when this is a single-player session — the camera
        // and team-perspective remap would otherwise stomp the SP scene
        // setup (which doesn't have Player0Base/Player1Base at our
        // hard-coded coordinates).
        bool mp = NetworkManagerRTS.Instance != null
               && NetworkManagerRTS.Instance.multiplayerMode;
        if (!mp)
        {
            Debug.Log("[MultiplayerMatch] Starter: single-player session, skipping " +
                      "camera + perspective remap (SP scene uses its own layout).");
            applied = true;
            return;
        }

        int local = NetworkManagerRTS.LocalPlayerId;
        if (local < 0)
        {
            Debug.LogWarning("[MultiplayerMatch] Starter fired but LocalPlayerId " +
                             "is still -1 — slot mapping isn't set. Defaulting to slot 0.");
            local = 0;
        }

        // Camera snap.
        RTSCamera rig = FindAnyObjectByType<RTSCamera>();
        if (rig != null)
        {
            Vector3 target = local == 0 ? player0CameraPos : player1CameraPos;
            rig.TeleportTo(target);
            Debug.Log($"[MultiplayerMatch] Local player {local} camera positioned at {target}.");
        }
        else
        {
            Debug.LogWarning("[MultiplayerMatch] No RTSCamera in scene — skipping camera snap.");
        }

        // Team perspective: each client maps own owner → Player, opponent → Enemy.
        GameEntity.RemapAllForLocalPerspective();
        Debug.Log($"[MultiplayerMatch] Local-perspective team remap applied " +
                  $"for {EntityRegistry.Count} registered entities.");

        applied = true;
    }
}
