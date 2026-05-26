using UnityEngine;

#if PHOTON_UNITY_NETWORKING
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
#endif

/// <summary>
/// Bridges <see cref="CommandDispatcher"/> to Photon's RaiseEvent transport.
///
/// Outbound:
///   Subscribes to <see cref="CommandDispatcher.OnLocalCommandIssued"/>.
///   Each fired command is serialised via
///   <see cref="PlayerCommand.ToNetworkPayload"/> and broadcast to all
///   OTHER clients in the room (not self — we already executed locally).
///
/// Inbound:
///   Implements <c>IOnEventCallback</c>. On receiving the
///   <see cref="PlayerCommandEventCode"/> event, deserialises the payload
///   and replays it via <see cref="CommandDispatcher.IssueRemote"/> — which
///   suppresses the local-command event so we don't echo back out.
///
/// Lifecycle:
///   • Add this script to the same GameObject as <see cref="NetworkManagerRTS"/>
///     (the editor tool Tools → RTS → Multiplayer → Setup Network Manager
///     does this for you).
///   • OnEnable subscribes to the dispatcher event and registers the Photon
///     callback target. OnDisable / OnDestroy unsubscribes. No state leaks
///     across scene reloads.
///
/// What this does NOT do:
///   • Synchronise unit transforms — we only relay commands. Each client
///     replays the command locally and the resulting unit movement happens
///     via the existing NavMeshAgent / UnitMovement code.
///   • Resolve entity-id mismatches between clients. Phase 1 limitation —
///     log a warning when a received command references an id that's
///     missing in our local <see cref="EntityRegistry"/>. Next phase will
///     fix this with deterministic spawning.
/// </summary>
[DisallowMultipleComponent]
public class NetworkCommandRelay : MonoBehaviour
#if PHOTON_UNITY_NETWORKING
    , IOnEventCallback
#endif
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Photon event code reserved for PlayerCommand broadcasts. Photon's
    /// own internal events use codes >= 200; we keep ours in the 1..199
    /// custom range.
    /// </summary>
    public const byte PlayerCommandEventCode = 1;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void OnEnable()
    {
        CommandDispatcher.OnLocalCommandIssued += HandleLocalCommand;
#if PHOTON_UNITY_NETWORKING
        PhotonNetwork.AddCallbackTarget(this);
#endif
        Debug.Log("[NetworkCommandRelay] Subscribed to CommandDispatcher.");
    }

    private void OnDisable()
    {
        CommandDispatcher.OnLocalCommandIssued -= HandleLocalCommand;
#if PHOTON_UNITY_NETWORKING
        PhotonNetwork.RemoveCallbackTarget(this);
#endif
    }

    // ------------------------------------------------------------------ //
    // Outbound — local command → remote clients
    // ------------------------------------------------------------------ //

    private void HandleLocalCommand(PlayerCommand cmd)
    {
        if (cmd == null) return;

        // No-op when networking is dormant. We still want the dispatcher
        // event to fire (other future subscribers may exist), so the gate
        // lives here, not in the dispatcher.
        if (!NetworkManagerRTS.IsMultiplayerEnabled) return;

#if PHOTON_UNITY_NETWORKING
        object[] payload = cmd.ToNetworkPayload();
        RaiseEventOptions opts = new RaiseEventOptions
        {
            Receivers = ReceiverGroup.Others,   // never echo to self — we already executed
        };
        bool ok = PhotonNetwork.RaiseEvent(
            PlayerCommandEventCode, payload, opts, SendOptions.SendReliable);

        if (ok)
            Debug.Log($"[NetworkCommandRelay] Sent command {cmd.commandType} (#{cmd.commandId}).");
        else
            Debug.LogWarning($"[NetworkCommandRelay] RaiseEvent returned false for {cmd}. " +
                             "Are we connected and in a room?");
#endif
    }

    // ------------------------------------------------------------------ //
    // Inbound — remote command → local replay
    // ------------------------------------------------------------------ //

#if PHOTON_UNITY_NETWORKING
    public void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != PlayerCommandEventCode) return;

        object[] payload = photonEvent.CustomData as object[];
        if (payload == null)
        {
            Debug.LogError("[NetworkCommandRelay] Received PlayerCommand event with " +
                           "non-object[] payload — dropping.");
            return;
        }

        PlayerCommand cmd = PlayerCommand.FromNetworkPayload(payload);
        if (cmd == null) return;

        Debug.Log($"[NetworkCommandRelay] Received command {cmd.commandType} (#{cmd.commandId}) " +
                  $"from player {photonEvent.Sender}.");

        // Diagnostic: warn early if the referenced entity ids don't resolve
        // locally. Phase 1 limitation — IDs are minted per-client, so this
        // will be a frequent log line until deterministic spawning lands.
        WarnIfUnresolvedEntityIds(cmd);

        CommandDispatcher.IssueRemote(cmd);
    }

    private static void WarnIfUnresolvedEntityIds(PlayerCommand cmd)
    {
        if (cmd.selectedEntityIds != null)
        {
            for (int i = 0; i < cmd.selectedEntityIds.Length; i++)
            {
                string id = cmd.selectedEntityIds[i];
                if (!string.IsNullOrEmpty(id) && EntityRegistry.Find(id) == null)
                {
                    Debug.LogWarning($"[NetworkCommandRelay] Remote command references " +
                                     $"unknown source entity id '{id}'. Phase 1 limitation: " +
                                     "ids are per-client and not yet deterministic. Next " +
                                     "phase will fix.");
                }
            }
        }

        if (!string.IsNullOrEmpty(cmd.targetEntityId) &&
            EntityRegistry.Find(cmd.targetEntityId) == null)
        {
            Debug.LogWarning($"[NetworkCommandRelay] Remote command references " +
                             $"unknown target entity id '{cmd.targetEntityId}'. " +
                             "Phase 1 limitation — see above.");
        }
    }
#endif
}
