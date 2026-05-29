using UnityEngine;

#if PHOTON_UNITY_NETWORKING
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
#endif

/// <summary>
/// Photon-based room/lobby controller for the RTS prototype.
///
/// IMPORTANT: every Photon API call in this file is wrapped in
/// <c>#if PHOTON_UNITY_NETWORKING</c>. PUN 2 defines that scripting symbol
/// automatically when the package is imported, so:
///   • Without PUN installed → all multiplayer methods log "Photon not
///     installed" and return. Single-player keeps working untouched.
///   • With PUN installed   → the same calls do real networking.
///
/// Setup (one-time, manual):
///   1. Asset Store: import "PUN 2 - FREE" by Exit Games.
///   2. Photon dashboard: register a free account, create an App ID
///      (https://dashboard.photonengine.com/) and paste it into the
///      <c>PhotonServerSettings</c> asset (Window → Photon Unity Networking →
///      Highlight Server Settings).
///   3. In your scene, add a GameObject called "NetworkManager" with this
///      component (or run Tools → RTS → Multiplayer → Setup Network Manager).
///   4. Tick <see cref="multiplayerMode"/> in the Inspector to opt-in. With
///      it disabled the script is dormant — useful for local play.
///
/// Phase 1 limitation: <see cref="GameEntity"/> ids are generated per-instance
/// per-client. Two clients won't agree on the same id for the same scene
/// object unless deterministic spawning is wired up (next phase). For now the
/// relay forwards commands verbatim and logs unresolved ids — useful enough
/// to verify the transport works end-to-end.
/// </summary>
[DisallowMultipleComponent]
public class NetworkManagerRTS : MonoBehaviour
#if PHOTON_UNITY_NETWORKING
    , IConnectionCallbacks, IMatchmakingCallbacks, ILobbyCallbacks, IInRoomCallbacks
#endif
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Multiplayer mode")]
    [Tooltip("Master switch. When false, this script does nothing — the game " +
             "behaves identically to single-player. When true, Connect() runs " +
             "at Start() and command relay is active once we're in a room.")]
    public bool multiplayerMode = false;

    [Header("Room settings")]
    [Tooltip("Maximum players per room. 1–4 player matches are supported; the " +
             "map has four corner start positions (A/B/C/D). Forced to " +
             "MaxPlayersSupported (4) at Awake + room creation so a stale " +
             "serialized value from an older scene can't cap rooms at 2.")]
    public byte maxPlayersPerRoom = 4;

    /// <summary>Hard cap — the 4-corner map supports up to four players.</summary>
    public const byte MaxPlayersSupported = 4;

    [Tooltip("App version sent to Photon. Clients only match if their app " +
             "versions are equal. Bump when the network payload schema changes.")]
    public string photonAppVersion = "0.1";

    [Tooltip("If true, immediately Connect() on Start when multiplayerMode is " +
             "also true. Otherwise leave dormant and call Connect() via the " +
             "Inspector context menu / a UI button.")]
    public bool autoConnectOnStart = false;

    // ------------------------------------------------------------------ //
    // Public static API — read by the relay + future HUD pieces.
    // ------------------------------------------------------------------ //

    /// <summary>The singleton — single NetworkManagerRTS per scene.</summary>
    public static NetworkManagerRTS Instance { get; private set; }

    /// <summary>
    /// True only when multiplayerMode is on AND the relay should be wiring
    /// commands across the network. False if Photon is missing OR the toggle
    /// is off OR we're not yet in a room.
    /// </summary>
    public static bool IsMultiplayerEnabled =>
#if PHOTON_UNITY_NETWORKING
        Instance != null && Instance.multiplayerMode &&
        PhotonNetwork.IsConnected && PhotonNetwork.InRoom;
#else
        false;
#endif

    /// <summary>
    /// 0-indexed player id resolved via <see cref="NetworkMatchCoordinator"/>'s
    /// slot mapping (lowest ActorNumber = 0, next = 1). Returns -1 when:
    ///   • not in a room, OR
    ///   • MatchStart hasn't been received yet (the slot mapping isn't
    ///     authoritative until then), OR
    ///   • this client's ActorNumber doesn't match either slot.
    ///
    /// Phase 4 fix: previously this returned <c>ActorNumber - 1</c>, which
    /// breaks when players join/leave/rejoin and ActorNumbers don't start
    /// at 1. Now driven by the shared MatchStart payload so both clients
    /// agree on slot assignment.
    /// </summary>
    public static int LocalPlayerId
    {
        get
        {
#if PHOTON_UNITY_NETWORKING
            if (!PhotonNetwork.InRoom || PhotonNetwork.LocalPlayer == null) return -1;

            // Authoritative path: coordinator has the broadcast slot mapping.
            if (NetworkMatchCoordinator.Instance != null &&
                NetworkMatchCoordinator.Instance.IsMatchStarted)
            {
                return NetworkMatchCoordinator.Instance.GetPlayerIdForActor(
                    PhotonNetwork.LocalPlayer.ActorNumber);
            }

            // Pre-MatchStart fallback: my slot is my rank in the sorted-actor
            // list (number of players with a lower ActorNumber). Works for
            // 1–4 players. Selection code still gates on IsMatchStarted so it
            // never acts on this preview.
            int local = PhotonNetwork.LocalPlayer.ActorNumber;
            int rank = 0;
            foreach (var kv in PhotonNetwork.CurrentRoom.Players)
                if (kv.Value.ActorNumber < local) rank++;
            return rank;
#else
            return -1;
#endif
        }
    }

    /// <summary>
    /// True when Photon considers this client the master (the room's
    /// authoritative client for MatchStart broadcasts). False in
    /// single-player or when not connected.
    /// </summary>
    public static bool IsMaster
    {
        get
        {
#if PHOTON_UNITY_NETWORKING
            return PhotonNetwork.IsConnected && PhotonNetwork.InRoom
                && PhotonNetwork.IsMasterClient;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// True when the room has at least ONE player (matches can now start with
    /// 1–4 players). Used by the menu to decide whether the host can broadcast
    /// MatchStart.
    /// </summary>
    public static bool IsRoomReady
    {
        get
        {
#if PHOTON_UNITY_NETWORKING
            return PhotonNetwork.IsConnected && PhotonNetwork.InRoom
                && PhotonNetwork.CurrentRoom.PlayerCount >= 1;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Resolves an ActorNumber to a player slot id via the coordinator's
    /// broadcast mapping. Returns -1 before MatchStart fires, or for an
    /// ActorNumber that isn't in the mapping.
    /// </summary>
    public static int GetPlayerIdForActor(int actorNumber)
    {
        if (NetworkMatchCoordinator.Instance == null) return -1;
        return NetworkMatchCoordinator.Instance.GetPlayerIdForActor(actorNumber);
    }

    // ------------------------------------------------------------------ //
    // Phase 5 — per-player colour sync via Photon custom player properties
    //
    // Each client writes its own selected colour into its Photon player
    // properties. At MatchStart the master reads both players' properties
    // and broadcasts the slot mapping → colour pairing, so every client
    // ends up showing the SAME colours for the same owner.
    // ------------------------------------------------------------------ //

    /// <summary>Photon player-property key for the colour as Vector3(r,g,b).</summary>
    public const string ColorPropKey     = "armyColor";

    /// <summary>Photon player-property key for the colour's human-readable name (debug).</summary>
    public const string ColorNamePropKey = "armyColorName";

    // Stored locally until we're in a room. Flushed in OnJoinedRoom.
    private bool   pendingColorPushHas;
    private Color  pendingColorPushValue;
    private string pendingColorPushName;

    /// <summary>
    /// Record this client's chosen army colour. If we're in a room, writes
    /// it to our Photon player properties immediately so the master can
    /// read it at MatchStart. If we're not connected yet, the value is
    /// queued and flushed automatically on <see cref="OnJoinedRoom"/>.
    ///
    /// Safe to call from the menu's colour buttons every click — Photon
    /// dedupes identical property writes.
    /// </summary>
    public void SetLocalPlayerColor(Color color, string colorName)
    {
        pendingColorPushHas   = true;
        pendingColorPushValue = color;
        pendingColorPushName  = colorName ?? "";

        Debug.Log($"[MultiplayerColor] Local selected color: " +
                  $"{(string.IsNullOrEmpty(colorName) ? "(unnamed)" : colorName)}");

#if PHOTON_UNITY_NETWORKING
        TryFlushPendingColor();
#endif
    }

#if PHOTON_UNITY_NETWORKING
    private void TryFlushPendingColor()
    {
        if (!pendingColorPushHas) return;
        if (!PhotonNetwork.IsConnected || !PhotonNetwork.InRoom) return;
        if (PhotonNetwork.LocalPlayer == null) return;

        Hashtable props = new Hashtable
        {
            { ColorPropKey,     new Vector3(pendingColorPushValue.r,
                                            pendingColorPushValue.g,
                                            pendingColorPushValue.b) },
            { ColorNamePropKey, pendingColorPushName ?? "" },
        };
        PhotonNetwork.LocalPlayer.SetCustomProperties(props);
        Debug.Log($"[MultiplayerColor] Set Photon property armyColorName=" +
                  $"{(string.IsNullOrEmpty(pendingColorPushName) ? "(unnamed)" : pendingColorPushName)}");
        pendingColorPushHas = false;
    }

    /// <summary>
    /// Reads an in-room Photon player's army colour. Returns true on hit;
    /// returns false (and the default <paramref name="fallbackColor"/>) when
    /// the player has not yet pushed a colour property. Used by
    /// <see cref="NetworkMatchCoordinator.BroadcastMatchStart"/>.
    /// </summary>
    public static bool TryGetPlayerColor(int actorNumber, Color fallbackColor,
                                         out Color color, out string colorName)
    {
        color     = fallbackColor;
        colorName = "";
        if (!PhotonNetwork.InRoom) return false;

        Player player = PhotonNetwork.CurrentRoom.GetPlayer(actorNumber);
        if (player == null || player.CustomProperties == null) return false;

        bool gotRgb = false;
        if (player.CustomProperties.TryGetValue(ColorPropKey, out object rgbObj) &&
            rgbObj is Vector3 v)
        {
            color = new Color(v.x, v.y, v.z, 1f);
            gotRgb = true;
        }
        if (player.CustomProperties.TryGetValue(ColorNamePropKey, out object nameObj) &&
            nameObj is string s)
        {
            colorName = s;
        }
        return gotRgb;
    }
#else
    /// <summary>Photon-less stub — always false in non-PUN builds.</summary>
    public static bool TryGetPlayerColor(int actorNumber, Color fallbackColor,
                                         out Color color, out string colorName)
    {
        color = fallbackColor;
        colorName = "";
        return false;
    }
#endif

    // ------------------------------------------------------------------ //
    // Start-position (corner) selection via Photon custom player properties.
    //
    // Each client writes its chosen corner (0..3) into the "startSlot" player
    // property. The lobby UI reads every player's value to render the A/B/C/D
    // picker, and the match coordinator finalises any unchosen players into
    // free corners at match start. Colour and start position are independent —
    // picking a colour never changes your corner and vice-versa.
    // ------------------------------------------------------------------ //

    /// <summary>Photon player-property key for the chosen start corner (int 0..3, -1 = none).</summary>
    public const string StartSlotPropKey = "startSlot";

    /// <summary>Sentinel value for "no corner chosen yet".</summary>
    public const int NoStartSlot = -1;

    /// <summary>
    /// Write the LOCAL player's chosen start corner into Photon player
    /// properties. No-op (with a log) when not in a room. Photon dedupes
    /// identical writes, so it's safe to call on every click. Because this is
    /// a single value, writing a new corner automatically frees the old one.
    /// </summary>
    public void SetLocalStartSlot(int corner)
    {
#if PHOTON_UNITY_NETWORKING
        if (!PhotonNetwork.InRoom || PhotonNetwork.LocalPlayer == null)
        {
            Debug.LogWarning("[StartSlot] SetLocalStartSlot ignored — not in a room.");
            return;
        }
        Hashtable props = new Hashtable { { StartSlotPropKey, corner } };
        PhotonNetwork.LocalPlayer.SetCustomProperties(props);
        Debug.Log($"[StartSlot] Local player chose corner {corner} " +
                  $"({(corner >= 0 ? ((char)('A' + corner)).ToString() : "none")}).");
#else
        Debug.LogWarning("[StartSlot] SetLocalStartSlot requested but Photon PUN not installed.");
#endif
    }

    /// <summary>
    /// Read a room player's chosen start corner. Returns true + the corner on
    /// hit; false + <see cref="NoStartSlot"/> when unset / not in a room.
    /// </summary>
    public static bool TryGetPlayerStartSlot(int actorNumber, out int corner)
    {
        corner = NoStartSlot;
#if PHOTON_UNITY_NETWORKING
        if (!PhotonNetwork.InRoom) return false;
        Player p = PhotonNetwork.CurrentRoom.GetPlayer(actorNumber);
        if (p == null || p.CustomProperties == null) return false;
        if (p.CustomProperties.TryGetValue(StartSlotPropKey, out object o) && o is int c)
        {
            corner = c;
            return c != NoStartSlot;
        }
        return false;
#else
        return false;
#endif
    }

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[NetworkRTS] Duplicate NetworkManagerRTS destroyed.");
            Destroy(this);
            return;
        }
        Instance = this;

        // Self-heal a stale serialized value. Older scenes baked
        // maxPlayersPerRoom = 2, and changing the code default does NOT update
        // an already-serialized component — so force it here so rooms are
        // always created for the 4-corner map.
        if (maxPlayersPerRoom != MaxPlayersSupported)
        {
            Debug.Log($"[NetworkRTS] maxPlayersPerRoom was {maxPlayersPerRoom}; " +
                      $"forcing to {MaxPlayersSupported} (4-corner map).");
            maxPlayersPerRoom = MaxPlayersSupported;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Start()
    {
        if (!multiplayerMode)
        {
            Debug.Log("[NetworkRTS] multiplayerMode is OFF — single-player.");
            return;
        }
        if (autoConnectOnStart) Connect();
    }

    // ------------------------------------------------------------------ //
    // Public methods (also exposed via Inspector context menu for testing)
    // ------------------------------------------------------------------ //

    [ContextMenu("Connect")]
    public void Connect()
    {
#if PHOTON_UNITY_NETWORKING
        if (PhotonNetwork.IsConnected)
        {
            Debug.Log("[NetworkRTS] Connect() — already connected, no-op.");
            return;
        }
        PhotonNetwork.AutomaticallySyncScene = false;
        PhotonNetwork.GameVersion            = photonAppVersion;
        bool ok = PhotonNetwork.ConnectUsingSettings();
        Debug.Log($"[NetworkRTS] Connecting to Photon (settings) — ConnectUsingSettings returned {ok}.");
#else
        Debug.LogWarning("[NetworkRTS] Connect() requested but Photon PUN is not installed. " +
                         "See PHOTON_SETUP.md.");
#endif
    }

    [ContextMenu("Create Room")]
    public void CreateRoom()
    {
        CreateRoom(roomName: null, mapId: MapRegistry.DefaultMapId);
    }

    /// <summary>
    /// Phase 6 overload — create a room with an explicit name AND a
    /// <c>mapId</c> stored in custom room properties.
    /// </summary>
    public void CreateRoom(string roomName, string mapId)
    {
        CreateRoom(roomName, mapId, DefaultStartingResources);
    }

    /// <summary>
    /// Phase 8 overload — also stores the lobby-selected
    /// <paramref name="startingResources"/> as a custom room property so
    /// <see cref="NetworkMatchCoordinator"/> can broadcast it in the
    /// MatchStart payload. Both clients end up with the same starting bank.
    /// </summary>
    public void CreateRoom(string roomName, string mapId, int startingResources)
    {
        // Session isolation — guarantee a clean slate before entering a new room.
        MatchSessionManager.CleanupPreviousMatch();
#if PHOTON_UNITY_NETWORKING
        pendingMapId             = string.IsNullOrEmpty(mapId) ? MapRegistry.DefaultMapId : mapId;
        pendingRoomName          = roomName;
        pendingStartingResources = Mathf.Max(0, startingResources);

        if (!PhotonNetwork.IsConnected)
        {
            Debug.LogWarning("[NetworkRTS] CreateRoom() called before Connect(). Connecting first.");
            Connect();
            pendingCreateRoom = true;
            return;
        }
        DoCreateRoom();
#else
        Debug.LogWarning("[NetworkRTS] CreateRoom() requested but Photon PUN is not installed.");
#endif
    }

    /// <summary>Default starting bank balance when the host doesn't pick one.</summary>
    public const int DefaultStartingResources = 10000;

    /// <summary>
    /// Phase 6 — join a room by its name (as opposed to JoinRandomRoom).
    /// Used by the lobby's Room List browser when the user clicks a row.
    /// </summary>
    public void JoinRoomByName(string roomName)
    {
        // Session isolation — clean slate before entering a new room.
        MatchSessionManager.CleanupPreviousMatch();
#if PHOTON_UNITY_NETWORKING
        if (string.IsNullOrEmpty(roomName))
        {
            Debug.LogWarning("[NetworkRTS] JoinRoomByName: empty room name.");
            return;
        }
        if (!PhotonNetwork.IsConnected)
        {
            Debug.LogWarning("[NetworkRTS] JoinRoomByName called before Connect(). Connecting first.");
            Connect();
            pendingJoinRoomName = roomName;
            return;
        }
        bool ok = PhotonNetwork.JoinRoom(roomName);
        Debug.Log($"[NetworkRTS] JoinRoom('{roomName}') — returned {ok}.");
#else
        Debug.LogWarning("[NetworkRTS] JoinRoomByName requested but Photon PUN is not installed.");
#endif
    }

    [ContextMenu("Join Random Room")]
    public void JoinRandomRoom()
    {
        // Session isolation — clean slate before entering a new room.
        MatchSessionManager.CleanupPreviousMatch();
#if PHOTON_UNITY_NETWORKING
        if (!PhotonNetwork.IsConnected)
        {
            Debug.LogWarning("[NetworkRTS] JoinRandomRoom() called before Connect(). Connecting first.");
            Connect();
            pendingJoinRandom = true;
            return;
        }
        DoJoinRandomRoom();
#else
        Debug.LogWarning("[NetworkRTS] JoinRandomRoom() requested but Photon PUN is not installed.");
#endif
    }

    [ContextMenu("Leave Room")]
    public void LeaveRoom()
    {
#if PHOTON_UNITY_NETWORKING
        if (!PhotonNetwork.InRoom)
        {
            Debug.Log("[NetworkRTS] LeaveRoom() — not in a room.");
            return;
        }
        PhotonNetwork.LeaveRoom();
        Debug.Log("[NetworkRTS] Leaving room.");
#else
        Debug.LogWarning("[NetworkRTS] LeaveRoom() requested but Photon PUN is not installed.");
#endif
    }

    // ------------------------------------------------------------------ //
    // Photon-only internals
    // ------------------------------------------------------------------ //

#if PHOTON_UNITY_NETWORKING
    // Set when CreateRoom / JoinRandomRoom was called before Connect finished —
    // OnConnectedToMaster picks the right action up afterwards.
    private bool   pendingCreateRoom;
    private bool   pendingJoinRandom;
    private string pendingJoinRoomName;
    private string pendingRoomName;
    private string pendingMapId = MapRegistry.DefaultMapId;

    // Photon room-properties keys. Use these constants when reading
    // CurrentRoom.CustomProperties or writing in CreateRoom.
    public const string RoomMapPropKey               = "mapId";
    public const string RoomStartingResourcesPropKey = "startingResources";

    // Phase 8 — startingResources pending value. Flushed into the room's
    // custom properties at DoCreateRoom time so every joining client can
    // read it before MatchStart.
    private int pendingStartingResources = DefaultStartingResources;

    // ------------------------------------------------------------------ //
    // Phase 6 — cached room list for the lobby UI's browser
    // ------------------------------------------------------------------ //

    private static readonly System.Collections.Generic.List<RoomInfo> s_cachedRoomList
        = new System.Collections.Generic.List<RoomInfo>();

    /// <summary>
    /// Read-only view of the lobby's most recent room-list update. Lobby UI
    /// re-binds its rows when <see cref="OnRoomListChanged"/> fires.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<RoomInfo> CachedRoomList
        => s_cachedRoomList;

    /// <summary>Fires after every Photon room-list update (lobby UI subscribes).</summary>
    public static event System.Action OnRoomListChanged;

    /// <summary>Fires after the local player successfully joins a room.</summary>
    public static event System.Action OnRoomJoinedEvent;

    /// <summary>Fires after the local player leaves a room.</summary>
    public static event System.Action OnRoomLeftEvent;

    /// <summary>Fires when ANY player's custom properties in the current room change.</summary>
    public static event System.Action<Player> OnPlayerPropertiesUpdatedEvent;

    private void OnEnable()
    {
        PhotonNetwork.AddCallbackTarget(this);
    }

    private void OnDisable()
    {
        PhotonNetwork.RemoveCallbackTarget(this);
    }

    private void DoCreateRoom()
    {
        // Custom room properties:
        //   • mapId — stored so every joining client can resolve the same
        //     MapDefinition and (later) load the right scene / world.
        //   • startingResources — host's chosen starting bank amount. Read
        //     by NetworkMatchCoordinator at MatchStart and broadcast to all
        //     clients so each ResourceBank initialises to the same value.
        // CustomRoomPropertiesForLobby exposes both to the room-list view
        // so the browser can show the map name + resources without joining.
        // Unique per-room MatchId so every room/match is isolated. Stored in
        // the room's custom properties so EVERY client (host + joiners) reads
        // the SAME id and tags/filters its gameplay events against it.
        string matchId = System.Guid.NewGuid().ToString();
        Debug.Log($"[MatchSession] New MatchId created for room: '{matchId}'.");

        Hashtable roomProps = new Hashtable
        {
            { RoomMapPropKey,                       pendingMapId ?? MapRegistry.DefaultMapId },
            { RoomStartingResourcesPropKey,         pendingStartingResources },
            { MatchSessionManager.MatchIdPropKey,   matchId },
        };

        RoomOptions opts = new RoomOptions
        {
            // Always create rooms for the full 4-corner map — never the stale
            // serialized value. This is the single source of truth for the cap.
            MaxPlayers                       = MaxPlayersSupported,
            IsVisible                        = true,
            IsOpen                           = true,
            PublishUserId                    = false,
            CustomRoomProperties             = roomProps,
            CustomRoomPropertiesForLobby     = new[]
            {
                RoomMapPropKey,
                RoomStartingResourcesPropKey,
            },
        };
        bool ok = PhotonNetwork.CreateRoom(
            pendingRoomName,     // null → Photon auto-names
            opts,
            TypedLobby.Default);
        Debug.Log($"[NetworkRTS] CreateRoom(name='{(pendingRoomName ?? "<auto>")}', " +
                  $"mapId='{pendingMapId}', startingResources={pendingStartingResources}) " +
                  $"— returned {ok}.");
        Debug.Log($"[RoomRules] Created room with MaxPlayers={MaxPlayersSupported}, " +
                  $"startingResources={pendingStartingResources}");
    }

    private void DoJoinRandomRoom()
    {
        bool ok = PhotonNetwork.JoinRandomRoom();
        Debug.Log($"[NetworkRTS] JoinRandomRoom — returned {ok}.");
    }

    // ---- IConnectionCallbacks ---------------------------------------- //

    public void OnConnected() { /* low-level — ignore */ }

    public void OnConnectedToMaster()
    {
        Debug.Log("[NetworkRTS] Connected to Photon master server.");

        // Join the default lobby so OnRoomListUpdate starts firing — this is
        // what populates the lobby UI's room browser. Photon doesn't auto-
        // join the lobby on connect.
        if (!PhotonNetwork.InLobby)
            PhotonNetwork.JoinLobby();

        // Drain any pending intent the user expressed before connection finished.
        if (pendingCreateRoom)
        {
            pendingCreateRoom = false;
            DoCreateRoom();
        }
        else if (pendingJoinRandom)
        {
            pendingJoinRandom = false;
            DoJoinRandomRoom();
        }
        else if (!string.IsNullOrEmpty(pendingJoinRoomName))
        {
            string n = pendingJoinRoomName;
            pendingJoinRoomName = null;
            PhotonNetwork.JoinRoom(n);
        }
    }

    public void OnDisconnected(DisconnectCause cause)
    {
        Debug.LogWarning($"[NetworkRTS] Disconnected from Photon — cause: {cause}.");

        // Session isolation — a disconnect (intentional or dropped) ends the
        // current match. Tear down all match state so a reconnect / new room
        // can never inherit it. Idempotent.
        MatchSessionManager.CleanupPreviousMatch();
        Debug.Log("[MatchSession] OnDisconnected cleanup completed.");
    }

    public void OnRegionListReceived(RegionHandler regionHandler)             { }
    public void OnCustomAuthenticationResponse(System.Collections.Generic.Dictionary<string, object> data) { }
    public void OnCustomAuthenticationFailed(string debugMessage)
    {
        Debug.LogError($"[NetworkRTS] Custom auth failed: {debugMessage}");
    }

    // ---- IMatchmakingCallbacks --------------------------------------- //

    public void OnFriendListUpdate(System.Collections.Generic.List<FriendInfo> friendList) { }

    public void OnCreatedRoom()
    {
        Debug.Log("[NetworkRTS] Room created.");
    }

    public void OnCreateRoomFailed(short returnCode, string message)
    {
        Debug.LogError($"[NetworkRTS] CreateRoom failed ({returnCode}): {message}");
    }

    public void OnJoinedRoom()
    {
        Debug.Log($"[NetworkRTS] Joined room '{PhotonNetwork.CurrentRoom.Name}' as " +
                  $"actor #{PhotonNetwork.LocalPlayer.ActorNumber} (playerId={LocalPlayerId}). " +
                  $"Players in room: {PhotonNetwork.CurrentRoom.PlayerCount}/{PhotonNetwork.CurrentRoom.MaxPlayers}");

        // Session isolation — start a fresh match session under THIS room's
        // MatchId. StartNewMatchSession cleans up any lingering previous-match
        // state first, then stamps the new id so gameplay events are scoped to
        // this room. Falls back to a room-name-derived id for rooms created by
        // an older build that didn't stamp a MatchId (still identical on both
        // clients because the room name is shared).
        string joinedMatchId = null;
        if (PhotonNetwork.CurrentRoom.CustomProperties != null &&
            PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(
                MatchSessionManager.MatchIdPropKey, out object midObj) &&
            midObj is string mid && !string.IsNullOrEmpty(mid))
        {
            joinedMatchId = mid;
        }
        if (string.IsNullOrEmpty(joinedMatchId))
            joinedMatchId = "room-" + PhotonNetwork.CurrentRoom.Name;

        MatchSessionManager.StartNewMatchSession(joinedMatchId);
        Debug.Log($"[MatchSession] Joined room MatchId '{joinedMatchId}'.");

        // Phase 5: if the player picked a colour BEFORE we joined the room
        // (the common case — colour is picked on the main menu, room join
        // happens later), push it now so the master can read it at MatchStart.
        TryFlushPendingColor();

        // Phase 6: notify the lobby UI to switch to the LobbyPanel.
        OnRoomJoinedEvent?.Invoke();
    }

    public void OnJoinRoomFailed(short returnCode, string message)
    {
        Debug.LogError($"[NetworkRTS] JoinRoom failed ({returnCode}): {message}");
    }

    public void OnJoinRandomFailed(short returnCode, string message)
    {
        Debug.LogWarning($"[NetworkRTS] JoinRandomRoom failed ({returnCode}: {message}) — " +
                         "creating a new room instead.");
        DoCreateRoom();
    }

    public void OnLeftRoom()
    {
        Debug.Log("[NetworkRTS] Left room.");
        OnRoomLeftEvent?.Invoke();
    }

    // ---- ILobbyCallbacks --------------------------------------------- //

    public void OnJoinedLobby()
    {
        Debug.Log("[NetworkRTS] Joined lobby.");
    }

    public void OnLeftLobby() { }

    public void OnRoomListUpdate(System.Collections.Generic.List<RoomInfo> roomList)
    {
        // Photon sends DELTAS (rooms removed + rooms updated). Merge into a
        // monotonically-maintained cache so the UI sees the full picture.
        for (int i = 0; i < roomList.Count; i++)
        {
            RoomInfo info = roomList[i];
            // Remove any existing entry with this name; we'll re-add unless
            // this update marks it as removed.
            for (int j = s_cachedRoomList.Count - 1; j >= 0; j--)
                if (s_cachedRoomList[j].Name == info.Name)
                    s_cachedRoomList.RemoveAt(j);

            if (!info.RemovedFromList && info.IsOpen && info.IsVisible)
                s_cachedRoomList.Add(info);
        }

        Debug.Log($"[NetworkRTS] Room list updated — {s_cachedRoomList.Count} room(s) visible.");
        OnRoomListChanged?.Invoke();
    }

    public void OnLobbyStatisticsUpdate(System.Collections.Generic.List<TypedLobbyInfo> lobbyStatistics) { }

    // ---- IInRoomCallbacks (Phase 6) ---------------------------------- //
    // Implemented so we get notified when ANY player in the room changes
    // their custom properties — colour pick, ready toggle, etc. The lobby
    // UI subscribes via OnPlayerPropertiesUpdatedEvent.

    public void OnPlayerEnteredRoom(Player newPlayer)
    {
        Debug.Log($"[NetworkRTS] Player entered room — actor #{newPlayer.ActorNumber}. " +
                  $"Total now: {PhotonNetwork.CurrentRoom.PlayerCount}/{PhotonNetwork.CurrentRoom.MaxPlayers}.");
        Debug.Log($"[RoomRules] Current players {PhotonNetwork.CurrentRoom.PlayerCount}/{MaxPlayersSupported}.");
        OnPlayerPropertiesUpdatedEvent?.Invoke(newPlayer);
    }

    public void OnPlayerLeftRoom(Player otherPlayer)
    {
        Debug.Log($"[NetworkRTS] Player left room — actor #{otherPlayer.ActorNumber}.");
        OnPlayerPropertiesUpdatedEvent?.Invoke(otherPlayer);
    }

    public void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged) { }

    public void OnPlayerPropertiesUpdate(Player targetPlayer, Hashtable changedProps)
    {
        OnPlayerPropertiesUpdatedEvent?.Invoke(targetPlayer);
    }

    public void OnMasterClientSwitched(Player newMasterClient)
    {
        Debug.Log($"[NetworkRTS] Master switched to actor #{newMasterClient.ActorNumber}.");
    }
#endif
}
