using System.Collections.Generic;
using UnityEngine;

#if PHOTON_UNITY_NETWORKING
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
#endif

/// <summary>
/// The SHARED match-start coordinator for 1–4 player matches.
///
///   1. <b>Synchronised start</b> — the Master broadcasts a
///      <see cref="MatchStartEventCode"/> event; every client runs its local
///      setup only after receiving it.
///   2. <b>Deterministic slot mapping</b> — slots 0..N-1 are assigned by sorted
///      <c>ActorNumber</c> and sent in the payload so every client agrees.
///   3. <b>Per-slot colours</b> — each slot's colour is broadcast so the local
///      menu pick doesn't recolour opponents.
///   4. <b>Corner assignment</b> — the Master reads each player's chosen
///      <c>startSlot</c> (lobby A/B/C/D picker), de-duplicates, randomly fills
///      unchosen players into free corners, guarantees uniqueness, and
///      broadcasts the final corner per slot. Only the corners assigned to an
///      active player are revealed + owned; unused corners stay hidden, so
///      nothing spawns for empty slots.
///
/// Single-player path: <see cref="RequestMatchStart"/> fires
/// <see cref="OnMatchStarted"/> synchronously with one slot (slot 0, corner 0)
/// and does NOT touch corners/reveal (SP scenes have no CornerBase), preserving
/// the existing single-player flow.
///
/// Network event payload (object[]):
///   [0] byte    version (= 3)
///   [1] int     playerCount N
///   [2] int     startingResources
///   then for each slot i: [3 + i*3 + 0] int actor, [+1] Vector3 colour, [+2] int corner
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

    // v3 generalised the payload from 2 fixed players to N players + corners.
    private const byte PayloadVersion = 3;

    // Fallback per-slot colours (A blue, B red, C green, D yellow) when a player
    // never pushed a colour property.
    private static readonly Color[] DefaultSlotColors =
    {
        new Color(0.20f, 0.55f, 1.00f),
        new Color(0.92f, 0.20f, 0.20f),
        new Color(0.30f, 0.80f, 0.35f),
        new Color(0.95f, 0.80f, 0.20f),
    };

    private static Color DefaultColor(int slot) =>
        DefaultSlotColors[Mathf.Clamp(slot, 0, DefaultSlotColors.Length - 1)];

    // ------------------------------------------------------------------ //
    // Public read-only API
    // ------------------------------------------------------------------ //

    public static NetworkMatchCoordinator Instance { get; private set; }

    /// <summary>True once a MatchStart event has been received (or SP started).</summary>
    public bool IsMatchStarted { get; private set; }

    /// <summary>Number of active players in the current match (1..4).</summary>
    public int PlayerCount { get; private set; }

    /// <summary>
    /// Fired once on the local client when the match starts. Subscribers:
    ///   • <see cref="GameplayWorldRoot"/> — reveals the gameplay world.
    ///   • <see cref="MultiplayerMatchStarter"/> — snaps the camera to the
    ///     local player's assigned corner.
    ///   • <see cref="MainMenuController"/> — hides the menu, shows the HUD.
    /// </summary>
    public static event System.Action OnMatchStarted;

    // Slot mapping for the current match.
    private readonly Dictionary<int, int> actorToSlot = new Dictionary<int, int>(4);
    private int[] slotToActor  = System.Array.Empty<int>();
    private int[] slotToCorner = System.Array.Empty<int>();

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

    /// <summary>
    /// Gameplay scene startup. After PhotonNetwork.LoadLevel completes, the
    /// FRESH coordinator instance reads the match payload from the room's
    /// custom properties (the master wrote it before calling LoadLevel) and
    /// runs the local apply pass. Same logic applies whichever scene name
    /// is the gameplay target — we no longer gate on
    /// <c>useSceneSplit</c>, because that flag could be false on the
    /// gameplay scene's NetworkManager just because the migration tool only
    /// updated the menu scene's serialized value. The presence of a payload
    /// in room properties is the authoritative signal that we're in
    /// scene-split flow.
    ///
    /// Runs in Start so every GameplayWorldRoot/MatchStarter has had its
    /// OnEnable, guaranteeing the subscriber list is ready before
    /// <see cref="OnMatchStarted"/> fires inside ApplyMatchStartLocally.
    ///
    /// Dev-mode fallback: if Photon isn't connected (direct-play of the
    /// gameplay scene from the Editor) AND CornerBases exist, we synthesise
    /// a 1-player local match so the scene is testable without going
    /// through the lobby. Editor-only.
    /// </summary>
    private void Start()
    {
        if (IsMatchStarted) return;
        StartCoroutine(StartCoroutineImpl());
    }

    /// <summary>
    /// Wait up to N frames for the room-properties payload to arrive after
    /// PhotonNetwork.LoadLevel. The master's write is locally cached
    /// immediately (it goes through the local Room object's optimistic
    /// update), but cross-client property sync via the server can land in
    /// the same frame as scene-loaded, OR just after — that race produced
    /// the "Orange in lobby, Blue at A in gameplay" symptom because the
    /// first-frame read returned no payload and the fallback fired with
    /// MultiplayerColors.DefaultPlayer0Color + corner 0.
    /// </summary>
    private System.Collections.IEnumerator StartCoroutineImpl()
    {
        string scenePrefix = "[" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "]";

#if PHOTON_UNITY_NETWORKING
        if (PhotonNetwork.InRoom)
        {
            const int MaxAttempts = 30;     // ~½ second at 60 fps — plenty
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (TryReadMatchStateFromRoomProperties(out int[] actors, out Color[] colors,
                                                        out int[] corners, out int startingResources))
                {
                    string roomName = PhotonNetwork.CurrentRoom != null
                        ? PhotonNetwork.CurrentRoom.Name : "<unknown>";
                    string matchId = string.IsNullOrEmpty(MatchSessionManager.CurrentMatchId)
                        ? "<none>" : MatchSessionManager.CurrentMatchId;

                    Debug.Log($"{scenePrefix} Loaded as gameplay scene. Payload arrived on attempt {attempt + 1}.");
                    Debug.Log($"{scenePrefix} Room='{roomName}', matchId='{matchId}'");
                    Debug.Log($"{scenePrefix} Applying payload: {actors.Length} players");
                    for (int i = 0; i < actors.Length; i++)
                    {
                        char letter = corners[i] >= 0 ? (char)('A' + corners[i]) : '?';
                        Debug.Log($"{scenePrefix} Actor #{actors[i]} -> playerSlot {i} -> corner {letter}");
                    }

                    ApplyMatchStartLocally(actors, colors, corners, startingResources);
                    yield break;
                }
                yield return null;     // wait one frame, try again
            }

            Debug.LogWarning($"{scenePrefix} No match payload found in room properties after " +
                             $"{MaxAttempts} frames. Falling back to dev mode (which now honours " +
                             "your LocalPlayer Photon properties so your lobby pick still wins).");
        }
#endif

        // Dev-mode fallback for direct-play of the gameplay scene from the
        // Editor (no Photon room, no payload). Synthesise a 1-player local
        // setup so the scene is testable without the lobby flow.
        TryApplyDevModeFallback(scenePrefix);
    }

    /// <summary>
    /// Editor-only: when the gameplay scene is opened directly and Play is
    /// pressed without going through the lobby, create a synthetic 1-player
    /// match (corner A, default colour, default starting resources) so the
    /// scene is exercisable end-to-end during dev iteration.
    /// </summary>
    private void TryApplyDevModeFallback(string scenePrefix)
    {
        if (!Application.isEditor) return;

        CornerBase[] cbs = Object.FindObjectsByType<CornerBase>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (cbs == null || cbs.Length == 0)
        {
            Debug.LogWarning($"{scenePrefix} DevMode skipped — no CornerBase components in scene. " +
                             "Run Tools → RTS → Match → Setup Multiplayer Match Map first.");
            return;
        }

        // Force multiplayerMode so ApplyMatchStartLocally's corner-reveal +
        // resource-set + reinit branch runs. Cosmetic — no Photon traffic
        // happens because we're not connected.
        if (NetworkManagerRTS.Instance != null)
            NetworkManagerRTS.Instance.multiplayerMode = true;

        int   devActor          = 1;
        Color devColor          = MultiplayerColors.DefaultPlayer0Color;
        string devColorLabel    = "default Blue";
        int   devCorner         = 0;
        string devCornerLabel   = "A (default)";
        int   devStartResources = NetworkManagerRTS.DefaultStartingResources;

#if PHOTON_UNITY_NETWORKING
        // CRITICAL: even when the room-properties payload didn't reach this
        // client, the LocalPlayer object survives the scene transition and
        // ITS CustomProperties were written immediately when the user clicked
        // colour / corner in the lobby. Read those directly so the dev
        // fallback uses the actual lobby selection instead of slot-0 defaults.
        if (PhotonNetwork.LocalPlayer != null)
        {
            devActor = PhotonNetwork.LocalPlayer.ActorNumber;

            if (PhotonNetwork.LocalPlayer.CustomProperties != null)
            {
                if (PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(
                        NetworkManagerRTS.ColorPropKey, out object rgbObj) && rgbObj is Vector3 v)
                {
                    devColor = new Color(v.x, v.y, v.z, 1f);
                    devColorLabel = $"LocalPlayer.armyColor RGB({v.x:F2},{v.y:F2},{v.z:F2})";
                }
                if (PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(
                        NetworkManagerRTS.ColorNamePropKey, out object cnObj) &&
                    cnObj is string cnStr && !string.IsNullOrEmpty(cnStr))
                {
                    devColorLabel = $"'{cnStr}' RGB({devColor.r:F2},{devColor.g:F2},{devColor.b:F2})";
                }
            }
            if (NetworkManagerRTS.TryGetPlayerStartSlot(devActor, out int sc) && sc >= 0 && sc < 4)
            {
                devCorner = sc;
                devCornerLabel = $"LocalPlayer.startSlot {sc} ({(char)('A' + sc)})";
            }
        }
#endif

        Debug.Log($"{scenePrefix} DevMode: No Photon payload arrived. Using LOCAL player props " +
                  $"(or defaults if missing): actor #{devActor}, color={devColorLabel}, corner={devCornerLabel}.");

        ApplyMatchStartLocally(
            new[] { devActor },
            new[] { devColor },
            new[] { devCorner },
            devStartResources);
    }

    // ------------------------------------------------------------------ //
    // Public — called by the lobby / main menu Start button
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Decide whether/how to start the match.
    ///   • Single-player → fire <see cref="OnMatchStarted"/> immediately (slot 0).
    ///   • Not in a room → return false.
    ///   • In a room with &lt; 1 player → return false (never happens).
    ///   • I'm Master → broadcast MatchStart for ALL players (1–4).
    ///   • I'm not Master → wait for the Master's broadcast.
    /// </summary>
    public bool RequestMatchStart(Color localColor)
    {
        if (NetworkManagerRTS.Instance == null || !NetworkManagerRTS.Instance.multiplayerMode)
        {
            Debug.Log("[MultiplayerMatch] Single-player Play — firing OnMatchStarted immediately.");
            ApplyMatchStartLocally(new[] { 1 }, new[] { localColor }, new[] { 0 }, -1);
            return true;
        }

#if PHOTON_UNITY_NETWORKING
        if (!PhotonNetwork.IsConnected || !PhotonNetwork.InRoom)
        {
            Debug.LogWarning("[Multiplayer] Cannot start match — not in room.");
            return false;
        }

        int count = PhotonNetwork.CurrentRoom.PlayerCount;
        if (count < 1)
        {
            Debug.Log("[MultiplayerMatch] Waiting for at least one player...");
            return false;
        }

        if (!PhotonNetwork.IsMasterClient)
        {
            Debug.Log("[MultiplayerMatch] Waiting for host. Master will broadcast MatchStart.");
            return false;
        }

        Debug.Log($"[MultiplayerMatch] Start allowed because currentPlayers ({count}) >= 1.");
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
        int[] actors = GetSortedActorNumbers();
        if (actors.Length < 1)
        {
            Debug.LogWarning("[MultiplayerMatch] BroadcastMatchStart aborted — no actors.");
            return;
        }
        int n = actors.Length;

        // Per-slot colour: each player's pushed colour, else the slot default.
        // Also keep the colour NAME for diagnostic logging — proves that what
        // the player picked in the lobby is what's going into the payload.
        Color[]  colors     = new Color[n];
        string[] colorNames = new string[n];
        for (int i = 0; i < n; i++)
        {
            bool got = NetworkManagerRTS.TryGetPlayerColor(
                actors[i], DefaultColor(i), out Color col, out string nm);
            colors[i]     = got ? col : DefaultColor(i);
            colorNames[i] = got && !string.IsNullOrEmpty(nm) ? nm : "default";
        }

        int[] corners = ComputeCornerAssignment(actors);
        int startingResources = ReadRoomStartingResources();

        Debug.Log($"[MultiplayerMatch] Starting match with {n} player(s). " +
                  $"startingResources={startingResources}.");
        for (int i = 0; i < n; i++)
            Debug.Log($"[MultiplayerMatch] Final corner assignment: slot {i} actor " +
                      $"#{actors[i]} → corner {(char)('A' + corners[i])} (index {corners[i]}).");

        // Per-actor diagnostic — proves the payload that's about to be written
        // to room properties uses each player's LATEST Photon-property values
        // (lobby picks), and shows the selected→final corner mapping when the
        // master had to random-fill duplicates or unchosen players.
        for (int i = 0; i < n; i++)
        {
            int selected = NetworkManagerRTS.TryGetPlayerStartSlot(actors[i], out int sc) ? sc : -1;
            string selStr = selected >= 0
                ? $"{selected} ({(char)('A' + selected)} visual={QuadrantOf(selected)})"
                : "none";
            char finalLetter = (char)('A' + corners[i]);
            string finalVisual = QuadrantOf(corners[i]);
            Debug.Log($"[MatchStart] Actor #{actors[i]}: playerSlot={i}, " +
                      $"color={colorNames[i]}, selectedStartSlot={selStr}, " +
                      $"visualCorner={finalLetter} -> finalStartSlot={corners[i]} " +
                      $"(visual={finalVisual}).");
        }

        // ---- Scene-split path: stash payload + LoadLevel ---------------- //
        // The new scene's coordinator reads it from room properties in Start.
        // This is the recommended Photon flow when matches happen in a
        // separate scene from the menu.
        bool useSplit = NetworkManagerRTS.Instance != null && NetworkManagerRTS.Instance.useSceneSplit;
        if (useSplit)
        {
            WriteMatchStateToRoomProperties(actors, colors, corners, startingResources);

            string roomName = PhotonNetwork.CurrentRoom != null
                ? PhotonNetwork.CurrentRoom.Name : "<unknown>";
            string matchId = string.IsNullOrEmpty(MatchSessionManager.CurrentMatchId)
                ? "<none>" : MatchSessionManager.CurrentMatchId;
            Debug.Log($"[MatchStart] Finalized payload for room '{roomName}', matchId '{matchId}'. " +
                      $"({n} player(s), startingResources={startingResources})");

            string mapScene = NetworkManagerRTS.Instance.gameMapSceneName;
            Debug.Log($"[MatchStart] Loading GameMapScene via PhotonNetwork.LoadLevel('{mapScene}') " +
                      "(AutomaticallySyncScene=true so all clients follow).");
            PhotonNetwork.AutomaticallySyncScene = true;
            PhotonNetwork.LoadLevel(mapScene);
            return;
        }

        // ---- Single-scene path: RaiseEvent in place --------------------- //
        object[] payload = new object[3 + n * 3];
        payload[0] = PayloadVersion;
        payload[1] = n;
        payload[2] = startingResources;
        for (int i = 0; i < n; i++)
        {
            payload[3 + i * 3 + 0] = actors[i];
            payload[3 + i * 3 + 1] = ColorToVec3(colors[i]);
            payload[3 + i * 3 + 2] = corners[i];
        }

        PhotonNetwork.RaiseEvent(
            MatchStartEventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.All },     // include sender
            SendOptions.SendReliable);
    }

    // ------------------------------------------------------------------ //
    // Scene-split: match payload serialised into the room's custom props
    // ------------------------------------------------------------------ //

    // Keys are deliberately short — Photon room properties are sent over the
    // wire on every join, so terse keys reduce overhead.
    private const string PropKeyVersion           = "m.ver";
    private const string PropKeyPlayerCount       = "m.n";
    private const string PropKeyStartingResources = "m.sr";
    private const string PropKeyActors            = "m.actors";
    private const string PropKeyCorners           = "m.corners";
    private const string PropKeyColorsR           = "m.cR";
    private const string PropKeyColorsG           = "m.cG";
    private const string PropKeyColorsB           = "m.cB";

    private static void WriteMatchStateToRoomProperties(
        int[] actors, Color[] colors, int[] corners, int startingResources)
    {
        int n = actors.Length;
        float[] cR = new float[n], cG = new float[n], cB = new float[n];
        for (int i = 0; i < n; i++) { cR[i] = colors[i].r; cG[i] = colors[i].g; cB[i] = colors[i].b; }

        Hashtable props = new Hashtable
        {
            { PropKeyVersion,           (byte)PayloadVersion },
            { PropKeyPlayerCount,       n },
            { PropKeyStartingResources, startingResources },
            { PropKeyActors,            actors },
            { PropKeyCorners,           corners },
            { PropKeyColorsR,           cR },
            { PropKeyColorsG,           cG },
            { PropKeyColorsB,           cB },
        };
        PhotonNetwork.CurrentRoom.SetCustomProperties(props);
        Debug.Log($"[MultiplayerMatch] Wrote match state to room properties ({n} player(s)).");
    }

    private static bool TryReadMatchStateFromRoomProperties(
        out int[] actors, out Color[] colors, out int[] corners, out int startingResources)
    {
        actors = null; colors = null; corners = null; startingResources = 0;
        if (PhotonNetwork.CurrentRoom == null || PhotonNetwork.CurrentRoom.CustomProperties == null) return false;
        var p = PhotonNetwork.CurrentRoom.CustomProperties;

        if (!p.TryGetValue(PropKeyPlayerCount,       out object nObj)  || !(nObj is int n)  || n < 1) return false;
        if (!p.TryGetValue(PropKeyStartingResources, out object srObj) || !(srObj is int sr))        return false;
        if (!p.TryGetValue(PropKeyActors,  out object aObj) || !(aObj is int[]   aArr))              return false;
        if (!p.TryGetValue(PropKeyCorners, out object cObj) || !(cObj is int[]   cArr))              return false;
        if (!p.TryGetValue(PropKeyColorsR, out object rObj) || !(rObj is float[] rArr))              return false;
        if (!p.TryGetValue(PropKeyColorsG, out object gObj) || !(gObj is float[] gArr))              return false;
        if (!p.TryGetValue(PropKeyColorsB, out object bObj) || !(bObj is float[] bArr))              return false;

        if (aArr.Length != n || cArr.Length != n || rArr.Length != n ||
            gArr.Length != n || bArr.Length != n)
        {
            Debug.LogWarning($"[MultiplayerMatch] Room props inconsistent — array length != playerCount {n}.");
            return false;
        }

        actors = aArr;
        corners = cArr;
        colors = new Color[n];
        for (int i = 0; i < n; i++) colors[i] = new Color(rArr[i], gArr[i], bArr[i], 1f);
        startingResources = sr;
        return true;
    }

    /// <summary>
    /// Master-side corner assignment: honour each player's chosen corner
    /// (lobby A/B/C/D picker), drop duplicates, then randomly fill the
    /// remaining players into free corners. Guarantees a unique corner per
    /// active player.
    /// </summary>
    private int[] ComputeCornerAssignment(int[] sortedActors)
    {
        int n = sortedActors.Length;
        int[] corners = new int[n];
        bool[] taken  = new bool[4];
        for (int i = 0; i < n; i++) corners[i] = -1;

        // Pass 1 — honour valid, unique chosen corners.
        for (int i = 0; i < n; i++)
        {
            if (NetworkManagerRTS.TryGetPlayerStartSlot(sortedActors[i], out int sc) &&
                sc >= 0 && sc < 4 && !taken[sc])
            {
                corners[i] = sc;
                taken[sc]  = true;
            }
        }

        // Pass 2 — random-fill unchosen players from the free corners.
        var free = new List<int>(4);
        for (int c = 0; c < 4; c++) if (!taken[c]) free.Add(c);
        for (int i = 0; i < n; i++)
        {
            if (corners[i] >= 0) continue;
            if (free.Count == 0) { corners[i] = i % 4; continue; }  // safety; n<=4
            int pick = Random.Range(0, free.Count);
            corners[i] = free[pick];
            free.RemoveAt(pick);
        }
        return corners;
    }

    private int ReadRoomStartingResources()
    {
        int startingResources = NetworkManagerRTS.DefaultStartingResources;
        if (PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.CustomProperties != null &&
            PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(
                NetworkManagerRTS.RoomStartingResourcesPropKey, out object srObj) &&
            srObj is int srInt)
        {
            startingResources = srInt;
        }
        return startingResources;
    }

    public void OnEvent(EventData ev)
    {
        if (ev.Code != MatchStartEventCode) return;

        if (!(ev.CustomData is object[] payload) || payload.Length < 3)
        {
            Debug.LogError("[MultiplayerMatch] MatchStart payload invalid.");
            return;
        }

        try
        {
            byte ver = (byte)payload[0];
            if (ver != PayloadVersion)
                Debug.LogWarning($"[MultiplayerMatch] MatchStart payload version {ver} " +
                                 $"differs from local {PayloadVersion} — proceeding anyway.");

            int n = (int)payload[1];
            int startingResources = (int)payload[2];
            if (n < 1 || payload.Length < 3 + n * 3)
            {
                Debug.LogError($"[MultiplayerMatch] MatchStart payload truncated (n={n}, len={payload.Length}).");
                return;
            }

            int[]   actors  = new int[n];
            Color[] colors  = new Color[n];
            int[]   corners = new int[n];
            for (int i = 0; i < n; i++)
            {
                actors[i]  = (int)payload[3 + i * 3 + 0];
                colors[i]  = Vec3ToColor((Vector3)payload[3 + i * 3 + 1]);
                corners[i] = (int)payload[3 + i * 3 + 2];
            }

            Debug.Log($"[MultiplayerMatch] Received MatchStart for {n} player(s).");
            ApplyMatchStartLocally(actors, colors, corners, startingResources);
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
        ResetForNewMatch();
    }
#endif

    /// <summary>
    /// Public reset entry used by <see cref="MatchSessionResetter"/> when the
    /// player returns to the main menu. Clears the slot/corner mapping.
    /// </summary>
    public void ResetForNewMatch()
    {
        IsMatchStarted = false;
        PlayerCount    = 0;
        actorToSlot.Clear();
        slotToActor  = System.Array.Empty<int>();
        slotToCorner = System.Array.Empty<int>();
        Debug.Log("[MultiplayerMatch] Coordinator reset for new match.");
    }

#if PHOTON_UNITY_NETWORKING
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
#endif

    private static Vector3 ColorToVec3(Color c) => new Vector3(c.r, c.g, c.b);
    private static Color   Vec3ToColor(Vector3 v) => new Color(v.x, v.y, v.z, 1f);

    // ------------------------------------------------------------------ //
    // Local-apply — pushes match-start state, fires OnMatchStarted
    // ------------------------------------------------------------------ //

    private void ApplyMatchStartLocally(int[] actors, Color[] colors, int[] corners, int startingResources)
    {
        PlayerCount  = actors.Length;
        actorToSlot.Clear();
        slotToActor  = new int[PlayerCount];
        slotToCorner = new int[PlayerCount];
        for (int i = 0; i < PlayerCount; i++)
        {
            actorToSlot[actors[i]] = i;
            slotToActor[i]  = actors[i];
            slotToCorner[i] = (i < corners.Length) ? corners[i] : -1;
        }
        IsMatchStarted = true;

        // Per-slot mapping log — one line per active player, deterministic
        // across all clients in this room (because they all read the same
        // payload from PhotonNetwork.CurrentRoom.CustomProperties).
        string applyPrefix = "[" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "]";
        for (int i = 0; i < PlayerCount; i++)
        {
            char letter = slotToCorner[i] >= 0 ? (char)('A' + slotToCorner[i]) : '?';
            Debug.Log($"{applyPrefix} Actor #{slotToActor[i]} -> playerSlot {i} -> " +
                      $"corner {letter} (index {slotToCorner[i]}).");
        }

        // Push slot colours BEFORE revealing so TeamColorMarkers repaint on the
        // same frame as the reveal.
        string applyScenePrefix = "[" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "]";
        for (int i = 0; i < PlayerCount; i++)
        {
            MultiplayerColors.SetForOwner(i, colors[i]);
            int actor  = slotToActor[i];
            int corner = slotToCorner[i];
            char letter = (corner >= 0 && corner < 26) ? (char)('A' + corner) : '?';
            Debug.Log($"{applyScenePrefix} Applying actor #{actor}: " +
                      $"color=RGB({colors[i].r:F2},{colors[i].g:F2},{colors[i].b:F2}), corner={letter}");
            Debug.Log($"[TeamColor] Applying RGB({colors[i].r:F2},{colors[i].g:F2}," +
                      $"{colors[i].b:F2}) to actor #{actor} / playerSlot {i}.");
        }

        Debug.Log($"[MultiplayerMatch] MatchStart startingResources={startingResources}, " +
                  $"players={PlayerCount}, LocalPlayerId={NetworkManagerRTS.LocalPlayerId}.");

        bool mp = NetworkManagerRTS.Instance != null && NetworkManagerRTS.Instance.multiplayerMode;

        // Reveal-only-assigned: set each corner's activeSelf BEFORE the world
        // reveal so only assigned corners come alive. (MP only — SP scenes
        // have no CornerBase and keep their own layout.)
        if (mp) ApplyCornerAssignments();

        // Reveal the world + snap the camera (GameplayWorldRoot, MatchStarter…).
        OnMatchStarted?.Invoke();

        if (mp)
        {
            // Assigned corners are active now → re-stamp every entity for this
            // match (ownership / team perspective / colour / movement gates /
            // spawn pose / MatchId), and reset resource nodes fresh.
            GameEntity.ReinitializeAllForNewMatch(MatchSessionManager.CurrentMatchId);
            ResourceNode.ResetAllForNewMatch();

            if (startingResources >= 0)
            {
                for (int i = 0; i < PlayerCount; i++)
                {
                    ResourceBank.SetCurrent(i, startingResources);
                    Debug.Log($"[Resources] Player {i} starting resources set to {ResourceBank.Current(i)}.");
                }
            }
        }

        Debug.Log("[MultiplayerMatch] Applied ownership and local perspective.");

        LogMatchStartSummary();
    }

    /// <summary>
    /// Per-slot post-spawn audit. Lists, for every active player slot, the
    /// final actor / corner / activation state of every spawn-critical piece
    /// (CornerBase, dozer, bank, resource cluster, current resource balance).
    /// Any missing piece is logged as an ERROR so the 4-player spawn bug
    /// (and any future spawn regression) is immediately diagnosable from
    /// the Console — no breakpoints required.
    /// </summary>
    private void LogMatchStartSummary()
    {
        Debug.Log($"[MultiplayerMatch] ─── Match-start summary ({PlayerCount} player(s)) ───");

        CornerBase[] allCorners = Object.FindObjectsByType<CornerBase>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        for (int i = 0; i < PlayerCount; i++)
        {
            int actor  = slotToActor[i];
            int corner = slotToCorner[i];
            char letter = (corner >= 0 && corner < 26) ? (char)('A' + corner) : '?';

            CornerBase cb = null;
            for (int k = 0; k < allCorners.Length; k++)
                if (allCorners[k] != null && allCorners[k].cornerIndex == corner) { cb = allCorners[k]; break; }

            bool cbActive   = cb != null && cb.gameObject.activeInHierarchy;
            bool dozerOK    = cb != null && cb.dozer != null && cb.dozer.activeInHierarchy;
            bool bankOK     = cb != null && cb.bank  != null && cb.bank.gameObject.activeInHierarchy;
            int  resNodes   = (cb != null && cb.resourceCluster != null) ? cb.resourceCluster.childCount : 0;
            int  balance    = ResourceBank.Current(i);

            Debug.Log($"[MultiplayerMatch]   slot {i} actor #{actor} → corner {letter} " +
                      $"(index {corner}): cb={cbActive} dozer={dozerOK} bank={bankOK} " +
                      $"resNodes={resNodes} resources={balance}");

            if (cb == null)
                Debug.LogError($"[MultiplayerMatch] ✗ slot {i} corner {letter}: CornerBase " +
                               $"with cornerIndex={corner} NOT FOUND in scene. " +
                               "Re-run Tools → RTS → Match → Setup Multiplayer Match Map.");
            else
            {
                if (!cbActive) Debug.LogError($"[MultiplayerMatch] ✗ slot {i} corner {letter}: CornerBase not active in hierarchy.");
                if (cb.dozer == null) Debug.LogError($"[MultiplayerMatch] ✗ slot {i} corner {letter}: dozer reference is NULL.");
                else if (!dozerOK)    Debug.LogError($"[MultiplayerMatch] ✗ slot {i} corner {letter}: dozer not active in hierarchy.");
                if (cb.bank == null)  Debug.LogError($"[MultiplayerMatch] ✗ slot {i} corner {letter}: bank reference is NULL.");
                else if (!bankOK)     Debug.LogError($"[MultiplayerMatch] ✗ slot {i} corner {letter}: bank not active in hierarchy.");
                if (resNodes == 0)    Debug.LogError($"[MultiplayerMatch] ✗ slot {i} corner {letter}: resource cluster has 0 nodes.");
            }
        }

        // Also list unused (correctly-hidden) corners so it's obvious nothing was
        // left over for an empty slot.
        for (int k = 0; k < allCorners.Length; k++)
        {
            CornerBase cb = allCorners[k];
            if (cb == null) continue;
            if (SlotForCorner(cb.cornerIndex) >= 0) continue;     // assigned, already logged
            Debug.Log($"[MultiplayerMatch]   corner {cb.Letter} (index {cb.cornerIndex}) → " +
                      $"unassigned, active={cb.gameObject.activeInHierarchy} " +
                      $"(expected: inactive — nothing spawns for empty slots).");
        }

        Debug.Log("[MultiplayerMatch] ─── End summary ───");
    }

    /// <summary>
    /// Reveal-only-assigned corners. For every <see cref="CornerBase"/> in the
    /// scene (including currently-inactive ones): if its corner is assigned to
    /// an active player slot, stamp ownership and mark it active; otherwise
    /// mark it inactive so nothing spawns there. Runs BEFORE the world reveal,
    /// so when <see cref="GameplayWorldRoot"/> activates the container only the
    /// assigned corners come alive.
    /// </summary>
    private void ApplyCornerAssignments()
    {
        CornerBase[] cbs = Object.FindObjectsByType<CornerBase>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (cbs == null || cbs.Length == 0)
        {
            Debug.Log("[MultiplayerMatch] No CornerBase objects found — skipping per-corner " +
                      "reveal (legacy/SP scene). Run Tools → RTS → Match → Setup Multiplayer Match Map.");
            return;
        }

        for (int i = 0; i < cbs.Length; i++)
        {
            CornerBase cb = cbs[i];
            if (cb == null) continue;

            string activatePrefix = "[" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "]";
            int slot = SlotForCorner(cb.cornerIndex);
            Vector3 pos = cb.transform.position;
            if (slot >= 0)
            {
                cb.AssignOwner(slot);
                cb.gameObject.SetActive(true);
                int actor = (slot < slotToActor.Length) ? slotToActor[slot] : -1;
                int nodes = (cb.resourceCluster != null) ? cb.resourceCluster.childCount : 0;
                Debug.Log($"{activatePrefix} Activated visualCorner={cb.Letter} " +
                          $"(index={cb.cornerIndex}, visual={cb.QuadrantLabel}) at " +
                          $"world position=({pos.x:F1},{pos.y:F1},{pos.z:F1}) — gameplay-view " +
                          $"{cb.QuadrantLabel} as seen by the player — for actor #{actor} " +
                          $"(slot {slot}, resourceNodes={nodes}, " +
                          $"dozer={(cb.dozer != null ? "yes" : "MISSING")}).");
            }
            else
            {
                cb.ClearOwner();
                cb.gameObject.SetActive(false);
                Debug.Log($"{activatePrefix} Skipped visualCorner={cb.Letter} " +
                          $"(index={cb.cornerIndex}, visual={cb.QuadrantLabel}): unassigned.");
            }
        }
    }

    private int SlotForCorner(int cornerIndex)
    {
        if (slotToCorner == null) return -1;
        for (int i = 0; i < slotToCorner.Length; i++)
            if (slotToCorner[i] == cornerIndex) return i;
        return -1;
    }

    /// <summary>
    /// Canonical mapping from cornerIndex (0..3) → GAMEPLAY-CAMERA-VIEW
    /// quadrant label (NOT raw-world quadrant). Must match the lobby preview
    /// and the baker's <c>SetupMultiplayerMatchMap.CornerPositions</c>.
    /// In this game's camera, world +Z appears at the BOTTOM of the screen
    /// and world -Z at the TOP — see <c>CornerBase.QuadrantLabel</c>.
    /// </summary>
    private static string QuadrantOf(int cornerIndex)
    {
        switch (cornerIndex)
        {
            case 0: return "TopLeft";
            case 1: return "TopRight";
            case 2: return "BottomLeft";
            case 3: return "BottomRight";
            default: return "Unknown";
        }
    }

    // ------------------------------------------------------------------ //
    // Slot / corner lookups
    // ------------------------------------------------------------------ //

    /// <summary>Slot id (0..N-1) for an ActorNumber, or -1 if not in the match.</summary>
    public int GetPlayerIdForActor(int actorNumber)
    {
        return actorToSlot.TryGetValue(actorNumber, out int slot) ? slot : -1;
    }

    /// <summary>Corner index (0..3) assigned to a player slot, or -1.</summary>
    public int GetCornerForPlayer(int slot)
    {
        if (slotToCorner != null && slot >= 0 && slot < slotToCorner.Length)
            return slotToCorner[slot];
        return -1;
    }
}
