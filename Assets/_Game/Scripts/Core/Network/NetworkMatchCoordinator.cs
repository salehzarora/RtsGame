using UnityEngine;

#if PHOTON_UNITY_NETWORKING
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
#endif

/// <summary>
/// Phase 4 — the SHARED match-start coordinator. Solves three problems at once:
///   1. <b>Synchronised match start</b> — the Master client broadcasts a
///      <see cref="MatchStartEventCode"/> event to all clients; every client
///      runs its local setup only after receiving it. Stops Client A from
///      entering gameplay while Client B is still in the menu.
///   2. <b>Deterministic player slot mapping</b> — slots are assigned by
///      sorted <c>ActorNumber</c> (lowest = player 0, next = player 1) and
///      sent in the event payload so every client agrees on which actor
///      owns which slot, regardless of rejoin order.
///   3. <b>Per-slot colours</b> — the colour for each slot is broadcast in
///      the same event, so the LOCAL player's menu colour choice doesn't
///      recolour the opponent's army on this client.
///
/// Single-player path: <see cref="RequestMatchStart"/> in SP mode bypasses
/// Photon entirely and fires <see cref="OnMatchStarted"/> synchronously with
/// a slot mapping of [local = player 0]. Existing menu/HUD/StartGame flow
/// continues unchanged.
///
/// Network event payload (object[]):
///   0: byte    version  (= 1)
///   1: int     player0Actor
///   2: int     player1Actor
///   3: Vector3 player0Color (r,g,b boxed as Vector3 — Photon's built-in)
///   4: Vector3 player1Color
/// </summary>
[DisallowMultipleComponent]
public class NetworkMatchCoordinator : MonoBehaviour
#if PHOTON_UNITY_NETWORKING
    , IOnEventCallback, IMatchmakingCallbacks
#endif
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    /// <summary>Photon event code reserved for MatchStart broadcasts.</summary>
    public const byte MatchStartEventCode = 2;     // 1 is PlayerCommand

    private const byte PayloadVersion = 1;

    // ------------------------------------------------------------------ //
    // Public read-only API
    // ------------------------------------------------------------------ //

    public static NetworkMatchCoordinator Instance { get; private set; }

    /// <summary>True once a MatchStart event has been received (or SP started).</summary>
    public bool IsMatchStarted { get; private set; }

    /// <summary>ActorNumber for the player who owns slot 0. Defaults to 1 in SP.</summary>
    public int Player0ActorNumber { get; private set; } = 1;

    /// <summary>ActorNumber for the player who owns slot 1. Defaults to 2 in SP.</summary>
    public int Player1ActorNumber { get; private set; } = 2;

    /// <summary>
    /// Fired once on the local client when the match starts. Subscribers:
    ///   • <see cref="MultiplayerMatchStarter"/> — positions camera + remaps perspective.
    ///   • <see cref="MainMenuController"/> — hides the menu, shows the HUD.
    ///   • Future scoreboard / minimap blip controllers.
    /// </summary>
    public static event System.Action OnMatchStarted;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[MultiplayerMatch] Duplicate NetworkMatchCoordinator destroyed.");
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

#if PHOTON_UNITY_NETWORKING
    private void OnEnable()  { PhotonNetwork.AddCallbackTarget(this); }
    private void OnDisable() { PhotonNetwork.RemoveCallbackTarget(this); }
#endif

    // ------------------------------------------------------------------ //
    // Public — called by MainMenuController.OnClickPlay
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Decide whether/how to start the match.
    ///
    /// Single-player path (multiplayerMode off):
    ///   • Fire <see cref="OnMatchStarted"/> synchronously. Caller proceeds
    ///     with the usual single-player flow.
    ///
    /// Multiplayer path:
    ///   • Not in a room → log + return false (caller stays on menu).
    ///   • In a room with &lt; 2 players → log "waiting" + return false.
    ///   • Room full + I'm Master → broadcast MatchStart (fires the event on
    ///     this client AND all others).
    ///   • Room full + I'm NOT Master → log "waiting for master" and return
    ///     false. The Master's broadcast eventually arrives and fires
    ///     <see cref="OnMatchStarted"/> here.
    /// </summary>
    /// <param name="localColor">
    /// Colour the LOCAL player chose in the menu. The master uses this when
    /// composing the broadcast. Non-master callers can pass anything — only
    /// the master's choice ends up in the broadcast payload for now.
    /// </param>
    /// <returns>True if MatchStart will fire (now or imminently).</returns>
    public bool RequestMatchStart(Color localColor)
    {
        // Single-player or no NetworkManager: fire immediately.
        if (NetworkManagerRTS.Instance == null || !NetworkManagerRTS.Instance.multiplayerMode)
        {
            Debug.Log("[MultiplayerMatch] Single-player Play — firing OnMatchStarted immediately.");
            ApplyMatchStartLocally(
                player0Actor: 1, player1Actor: 2,
                player0Color: localColor,
                player1Color: MultiplayerColors.DefaultPlayer1Color);
            return true;
        }

#if PHOTON_UNITY_NETWORKING
        if (!PhotonNetwork.IsConnected || !PhotonNetwork.InRoom)
        {
            Debug.LogWarning("[Multiplayer] Cannot start match — not in room.");
            return false;
        }

        if (PhotonNetwork.CurrentRoom.PlayerCount < 2)
        {
            Debug.Log("[MultiplayerMatch] Waiting for player 2...");
            return false;
        }

        if (!PhotonNetwork.IsMasterClient)
        {
            Debug.Log("[MultiplayerMatch] Waiting for room. Master will broadcast MatchStart.");
            return false;
        }

        // We're the master + room is ready. Compose + broadcast.
        BroadcastMatchStart(localColor);
        return true;
#else
        Debug.LogWarning("[Multiplayer] Cannot start match — Photon PUN not installed.");
        return false;
#endif
    }

    // ------------------------------------------------------------------ //
    // Master broadcast
    // ------------------------------------------------------------------ //

#if PHOTON_UNITY_NETWORKING
    private void BroadcastMatchStart(Color localColor)
    {
        // Slot mapping by sorted ActorNumber — same on every client.
        int[] sortedActors = GetSortedActorNumbers();
        if (sortedActors.Length < 2)
        {
            Debug.LogWarning("[MultiplayerMatch] BroadcastMatchStart aborted — fewer than 2 actors.");
            return;
        }
        int player0Actor = sortedActors[0];
        int player1Actor = sortedActors[1];

        // Phase 5: read EACH player's chosen colour from Photon custom
        // properties. Falls back to slot defaults if a player never pushed a
        // colour (the colour buttons in the menu push immediately; the
        // pending-on-join flush covers the pre-room pick case). The master
        // is one of the two players, so its own choice is already in its
        // properties via the same code path.
        bool gotP0 = NetworkManagerRTS.TryGetPlayerColor(
            player0Actor, MultiplayerColors.DefaultPlayer0Color,
            out Color p0Color, out string p0Name);
        bool gotP1 = NetworkManagerRTS.TryGetPlayerColor(
            player1Actor, MultiplayerColors.DefaultPlayer1Color,
            out Color p1Color, out string p1Name);

        if (!gotP0) p0Name = "Blue (default)";
        if (!gotP1) p1Name = "Red (default)";

        Debug.Log($"[MultiplayerMatch] Player0 color = {p0Name}");
        Debug.Log($"[MultiplayerMatch] Player1 color = {p1Name}");

        object[] payload = new object[]
        {
            PayloadVersion,
            player0Actor,
            player1Actor,
            ColorToVec3(p0Color),
            ColorToVec3(p1Color),
        };

        Debug.Log($"[MultiplayerMatch] Master starting match. " +
                  $"Player0 actor = {player0Actor}, Player1 actor = {player1Actor}.");

        PhotonNetwork.RaiseEvent(
            MatchStartEventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.All },     // include sender
            SendOptions.SendReliable);
    }

    public void OnEvent(EventData ev)
    {
        if (ev.Code != MatchStartEventCode) return;

        object[] payload = ev.CustomData as object[];
        if (payload == null || payload.Length < 5)
        {
            Debug.LogError("[MultiplayerMatch] MatchStart payload invalid.");
            return;
        }

        try
        {
            byte ver = (byte)payload[0];
            if (ver != PayloadVersion)
                Debug.LogWarning($"[MultiplayerMatch] MatchStart payload version " +
                                 $"{ver} differs from local {PayloadVersion} — proceeding anyway.");

            int p0Actor = (int)payload[1];
            int p1Actor = (int)payload[2];
            Color p0Col = Vec3ToColor((Vector3)payload[3]);
            Color p1Col = Vec3ToColor((Vector3)payload[4]);

            Debug.Log($"[MultiplayerMatch] Received MatchStart.");
            ApplyMatchStartLocally(p0Actor, p1Actor, p0Col, p1Col);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[MultiplayerMatch] MatchStart payload deserialise failed: {e.Message}");
        }
    }

    // ---- IMatchmakingCallbacks stubs (only the ones we need) --------- //
    public void OnFriendListUpdate(System.Collections.Generic.List<FriendInfo> friendList) { }
    public void OnCreatedRoom()                                           { }
    public void OnCreateRoomFailed(short returnCode, string message)      { }
    public void OnJoinedRoom()                                            { }
    public void OnJoinRoomFailed(short returnCode, string message)        { }
    public void OnJoinRandomFailed(short returnCode, string message)      { }
    public void OnLeftRoom()
    {
        // If we leave mid-match, reset coordinator state so a re-join can
        // run a fresh MatchStart sequence.
        IsMatchStarted = false;
    }

    private static int[] GetSortedActorNumbers()
    {
        if (!PhotonNetwork.InRoom) return System.Array.Empty<int>();
        var players = PhotonNetwork.CurrentRoom.Players;
        var arr = new int[players.Count];
        int i = 0;
        foreach (var kv in players) arr[i++] = kv.Value.ActorNumber;
        System.Array.Sort(arr);
        return arr;
    }

    private static Vector3 ColorToVec3(Color c) => new Vector3(c.r, c.g, c.b);
    private static Color   Vec3ToColor(Vector3 v) => new Color(v.x, v.y, v.z, 1f);
#endif

    // ------------------------------------------------------------------ //
    // Local-apply — pushes match-start state, fires OnMatchStarted
    // ------------------------------------------------------------------ //

    private void ApplyMatchStartLocally(
        int player0Actor, int player1Actor,
        Color player0Color, Color player1Color)
    {
        Player0ActorNumber = player0Actor;
        Player1ActorNumber = player1Actor;
        IsMatchStarted     = true;

        // Push slot colours into the registry BEFORE invoking subscribers
        // so TeamColorMarkers repaint themselves on the same frame as the
        // camera snap.
        MultiplayerColors.SetForOwner(0, player0Color);
        MultiplayerColors.SetForOwner(1, player1Color);

        Debug.Log($"[TeamColor] Applied color RGB({player0Color.r:F2}," +
                  $"{player0Color.g:F2},{player0Color.b:F2}) to owner 0.");
        Debug.Log($"[TeamColor] Applied color RGB({player1Color.r:F2}," +
                  $"{player1Color.g:F2},{player1Color.b:F2}) to owner 1.");

        Debug.Log($"[MultiplayerMatch] LocalPlayerId = {NetworkManagerRTS.LocalPlayerId}.");
        Debug.Log($"[MultiplayerMatch] Player0 actor = {player0Actor}, " +
                  $"Player1 actor = {player1Actor}.");

        // Diagnostic: sanity-check that the scene-baked Workers / CCs carry
        // the right ownerPlayerId. If either prints owner=0 for Player 1's
        // unit, the scene stamper bug (Phase 4 fix) hasn't been re-applied —
        // re-run SetupMultiplayerMatchMap then AddGameEntityToSceneObjects.
        LogBaseOwnership(0);
        LogBaseOwnership(1);

        OnMatchStarted?.Invoke();

        Debug.Log("[MultiplayerMatch] Applied ownership and local perspective.");
    }

    private static void LogBaseOwnership(int expectedOwner)
    {
        GameObject baseRoot = GameObject.Find($"Player{expectedOwner}Base");
        if (baseRoot == null)
        {
            Debug.LogWarning($"[MultiplayerMatch] No 'Player{expectedOwner}Base' " +
                             "in scene — run Tools → RTS → Match → Setup Multiplayer Match Map.");
            return;
        }
        WorkerGatherer w = baseRoot.GetComponentInChildren<WorkerGatherer>(true);
        if (w == null)
        {
            Debug.LogWarning($"[MultiplayerMatch] Player{expectedOwner}Base has no Worker.");
            return;
        }
        GameEntity ge = w.GetComponent<GameEntity>();
        int actualOwner = ge != null ? ge.ownerPlayerId : -1;
        Debug.Log($"[MultiplayerMatch] Player{expectedOwner} Worker owner = {actualOwner} " +
                  $"({(actualOwner == expectedOwner ? "OK" : "MISMATCH — re-run setup tools")}).");
    }

    // ------------------------------------------------------------------ //
    // Slot lookup (called by NetworkManagerRTS.LocalPlayerId)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns 0 if <paramref name="actorNumber"/> matches
    /// <see cref="Player0ActorNumber"/>, 1 if it matches
    /// <see cref="Player1ActorNumber"/>, -1 otherwise.
    /// </summary>
    public int GetPlayerIdForActor(int actorNumber)
    {
        if (actorNumber == Player0ActorNumber) return 0;
        if (actorNumber == Player1ActorNumber) return 1;
        return -1;
    }
}
