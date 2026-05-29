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
        Color[] colors = new Color[n];
        for (int i = 0; i < n; i++)
        {
            bool got = NetworkManagerRTS.TryGetPlayerColor(
                actors[i], DefaultColor(i), out Color col, out _);
            colors[i] = got ? col : DefaultColor(i);
        }

        int[] corners = ComputeCornerAssignment(actors);
        int startingResources = ReadRoomStartingResources();

        Debug.Log($"[MultiplayerMatch] Starting match with {n} player(s). " +
                  $"startingResources={startingResources}.");
        for (int i = 0; i < n; i++)
            Debug.Log($"[MultiplayerMatch] Final corner assignment: slot {i} actor " +
                      $"#{actors[i]} → corner {(char)('A' + corners[i])} (index {corners[i]}).");

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

        // Push slot colours BEFORE revealing so TeamColorMarkers repaint on the
        // same frame as the reveal.
        for (int i = 0; i < PlayerCount; i++)
        {
            MultiplayerColors.SetForOwner(i, colors[i]);
            Debug.Log($"[TeamColor] Applied color RGB({colors[i].r:F2},{colors[i].g:F2}," +
                      $"{colors[i].b:F2}) to slot {i}.");
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

            int slot = SlotForCorner(cb.cornerIndex);
            if (slot >= 0)
            {
                cb.AssignOwner(slot);
                cb.gameObject.SetActive(true);
                Debug.Log($"[MultiplayerMatch] Spawning player slot {slot} at corner " +
                          $"{cb.Letter} (index {cb.cornerIndex}).");
            }
            else
            {
                cb.ClearOwner();
                cb.gameObject.SetActive(false);
                Debug.Log($"[MultiplayerMatch] Skipped empty corner {cb.Letter} (index {cb.cornerIndex}).");
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
