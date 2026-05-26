using TMPro;
using UnityEngine;
using UnityEngine.UI;

#if PHOTON_UNITY_NETWORKING
using Photon.Pun;
#endif

/// <summary>
/// On-screen debug panel for Photon room testing. Replaces the
/// Inspector-context-menu workflow on <see cref="NetworkManagerRTS"/> with
/// four buttons (Connect / Create Room / Join Random Room / Leave Room) plus
/// a live status readout.
///
/// Visibility rules:
///   • <see cref="NetworkManagerRTS.multiplayerMode"/> is on → always show.
///   • In the Editor with <see cref="alwaysShowInEditor"/> = true → show
///     even when multiplayerMode is off (handy for dev).
///   • Otherwise (built player, single-player) → hidden so it doesn't bleed
///     into a normal player's view.
///
/// This component lives on its own Canvas (sort order 1100 — above HUDCanvas
/// at 999 and MainMenuCanvas at 1000) so it remains usable during both the
/// main menu and active gameplay. The intended workflow:
///
///   1. Tick <c>multiplayerMode</c> on NetworkManagerRTS.
///   2. Press Play.
///   3. Click Connect → wait for "Connected to Master" status.
///   4. Either click Create Room (host) or Join Random (peer).
///   5. Once "In Room — Players: N/M" status appears, gameplay commands relay
///      to the other client automatically.
///
/// Photon-soft dependency: every PUN call is wrapped in
/// <c>#if PHOTON_UNITY_NETWORKING</c>. Without PUN installed the status
/// shows "Photon PUN not installed" and clicks log a warning.
/// </summary>
[DisallowMultipleComponent]
public class MultiplayerDebugPanel : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Visibility")]
    [Tooltip("Phase 7: defaults to false so the top-right debug panel doesn't " +
             "bleed into the normal main-menu / lobby flow. Tick this ONLY for " +
             "manual dev testing (it lets you Connect / Create Room from the " +
             "Inspector without going through the lobby UI). Normal players " +
             "should use Main Menu → Online → Lobby.")]
    public bool alwaysShowInEditor = false;

    [Header("Wiring (set up by SetupMultiplayerDebugUI)")]
    [Tooltip("Root GameObject of the panel — toggled active/inactive based on " +
             "visibility rules.")]
    public GameObject panelRoot;

    [Tooltip("Multi-line status label updated each frame.")]
    public TextMeshProUGUI statusText;

    [Tooltip("Buttons wired to the four NetworkManagerRTS public methods.")]
    public Button connectButton;
    public Button createRoomButton;
    public Button joinRandomButton;
    public Button leaveRoomButton;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        WireButtonOrLog(connectButton,    OnClickConnect,    "Connect");
        WireButtonOrLog(createRoomButton, OnClickCreateRoom, "Create Room");
        WireButtonOrLog(joinRandomButton, OnClickJoinRandom, "Join Random Room");
        WireButtonOrLog(leaveRoomButton,  OnClickLeaveRoom,  "Leave Room");
    }

    private void Update()
    {
        RefreshVisibility();
        RefreshStatus();
    }

    // ------------------------------------------------------------------ //
    // Visibility — gates the panel root on or off
    // ------------------------------------------------------------------ //

    private void RefreshVisibility()
    {
        if (panelRoot == null) return;

        bool mpMode = NetworkManagerRTS.Instance != null
                   && NetworkManagerRTS.Instance.multiplayerMode;
        bool editorShow = alwaysShowInEditor && Application.isEditor;

        bool shouldShow = mpMode || editorShow;
        if (panelRoot.activeSelf != shouldShow)
            panelRoot.SetActive(shouldShow);
    }

    // ------------------------------------------------------------------ //
    // Status — polls Photon state each frame
    // ------------------------------------------------------------------ //

    private void RefreshStatus()
    {
        if (statusText == null) return;

#if PHOTON_UNITY_NETWORKING
        // PhotonNetwork.NetworkClientState gives a fine-grained enum
        // (Disconnected, PeerCreated, ConnectingToMasterServer, ConnectedToMaster,
        //  JoiningLobby, JoinedLobby, Joining, Joined, ...). We collapse it into
        // the five labels in the spec for readability.
        string state = SummariseClientState();

        string roomLine;
        if (PhotonNetwork.InRoom)
        {
            var room = PhotonNetwork.CurrentRoom;
            roomLine = $"Room: {room.Name}\nPlayers: {room.PlayerCount}/{room.MaxPlayers}";
        }
        else
        {
            roomLine = "Not in a room.";
        }

        int localPid = NetworkManagerRTS.LocalPlayerId;
        string idLine = localPid >= 0 ? $"LocalPlayerId: {localPid}" : "LocalPlayerId: —";

        statusText.text = $"State: {state}\n{roomLine}\n{idLine}";
#else
        statusText.text = "Photon PUN not installed.\nSee PHOTON_SETUP.md.";
#endif
    }

#if PHOTON_UNITY_NETWORKING
    /// <summary>Maps Photon's fine-grained client state to the five labels in the spec.</summary>
    private static string SummariseClientState()
    {
        var s = PhotonNetwork.NetworkClientState;
        if (PhotonNetwork.InRoom)               return "In Room";
        if (s == Photon.Realtime.ClientState.JoinedLobby) return "In Lobby";
        if (PhotonNetwork.IsConnected)          return "Connected";
        if (PhotonNetwork.IsConnectedAndReady)  return "Connected";

        // States that count as "Connecting": connecting to NS/MS, authenticating, etc.
        switch (s)
        {
            case Photon.Realtime.ClientState.ConnectingToNameServer:
            case Photon.Realtime.ClientState.ConnectingToMasterServer:
            case Photon.Realtime.ClientState.ConnectingToGameServer:
            case Photon.Realtime.ClientState.Authenticating:
            case Photon.Realtime.ClientState.ConnectedToNameServer:
            case Photon.Realtime.ClientState.JoiningLobby:
            case Photon.Realtime.ClientState.Joining:
                return "Connecting";

            case Photon.Realtime.ClientState.Disconnected:
            case Photon.Realtime.ClientState.PeerCreated:
                return "Disconnected";

            default:
                return s.ToString();   // unknown state — show raw label
        }
    }
#endif

    // ------------------------------------------------------------------ //
    // Button handlers — thin wrappers; the actual work lives on NetworkManagerRTS
    // ------------------------------------------------------------------ //

    public void OnClickConnect()
    {
        Debug.Log("[MultiplayerDebugPanel] Click: Connect");
        if (!HasManager()) return;
        NetworkManagerRTS.Instance.Connect();
    }

    public void OnClickCreateRoom()
    {
        Debug.Log("[MultiplayerDebugPanel] Click: Create Room");
        if (!HasManager()) return;
        NetworkManagerRTS.Instance.CreateRoom();
    }

    public void OnClickJoinRandom()
    {
        Debug.Log("[MultiplayerDebugPanel] Click: Join Random Room");
        if (!HasManager()) return;
        NetworkManagerRTS.Instance.JoinRandomRoom();
    }

    public void OnClickLeaveRoom()
    {
        Debug.Log("[MultiplayerDebugPanel] Click: Leave Room");
        if (!HasManager()) return;
        NetworkManagerRTS.Instance.LeaveRoom();
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static bool HasManager()
    {
        if (NetworkManagerRTS.Instance == null)
        {
            Debug.LogWarning("[MultiplayerDebugPanel] No NetworkManagerRTS in the scene. " +
                             "Run Tools → RTS → Multiplayer → Setup Network Manager.");
            return false;
        }
        return true;
    }

    private static void WireButtonOrLog(Button btn, UnityEngine.Events.UnityAction handler, string label)
    {
        if (btn == null)
        {
            Debug.LogWarning($"[MultiplayerDebugPanel] '{label}' button not wired in the Inspector. " +
                             "Re-run Tools → RTS → Multiplayer → Setup Multiplayer Debug UI.");
            return;
        }
        btn.onClick.AddListener(handler);
    }
}
