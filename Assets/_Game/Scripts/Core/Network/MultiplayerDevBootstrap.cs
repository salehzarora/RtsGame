using System.Collections;
using UnityEngine;

#if PHOTON_UNITY_NETWORKING
using Photon.Pun;
#endif

/// <summary>
/// Local-multiplayer testing aide. Reads the local player's identity + auto-start
/// intent from THREE sources (in priority order: highest wins) and drives the
/// existing lobby/coordinator so a dev build / Editor / MPPM virtual player
/// can spin up directly into a match without clicking through the menu.
///
///   1. Command-line args (standalone dev builds — fastest iteration):
///        -playerName "Alpha"  -color blue|red|green|yellow|orange|purple
///        -room "DevRoom"      -startSlot 0..3   (also accepts A/B/C/D)
///        -autoConnect         -autoCreateRoom   -autoJoinRoom   -autoStart
///
///   2. MPPM Player Tags (Unity Multiplayer Play Mode — virtual players in
///      the same Editor). Format: one tag per setting, same keys as CLI:
///        playerName=Alpha    color=blue       room=DevRoom    startSlot=A
///        autoConnect         autoCreateRoom   autoJoinRoom    autoStart
///      Read via reflection so the MPPM package is OPTIONAL — if it's not
///      installed, nothing breaks.
///
///   3. Inspector overrides on this component (slowest to change per-instance
///      but useful for a single Editor session).
///
/// All inputs are advisory. With no values, the bootstrap is COMPLETELY INERT:
/// the project boots into the main menu as normal. Gameplay logic is unchanged.
/// </summary>
[DisallowMultipleComponent]
public class MultiplayerDevBootstrap : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector overrides
    // ------------------------------------------------------------------ //

    [Header("Identity (empty = read from CLI / MPPM tag)")]
    [Tooltip("Photon nickname shown in the lobby + room list.")]
    public string playerName;

    [Tooltip("Blue / Red / Green / Yellow / Orange / Purple (case-insensitive). " +
             "Sets PlayerFactionManager.SetColor + NetworkManagerRTS.SetLocalPlayerColor.")]
    public string colorName;

    [Header("Room")]
    [Tooltip("Room name used by autoCreateRoom / autoJoinRoom. Empty → 'DevRoom'.")]
    public string roomName;

    [Tooltip("0..3 or A/B/C/D. -1 = leave unchosen (master will random-fill at MatchStart).")]
    public int startSlot = -1;

    [Header("Auto-actions (all default off)")]
    [Tooltip("Enable multiplayerMode and call NetworkManagerRTS.Connect() at Start.")]
    public bool autoConnect;

    [Tooltip("After autoConnect, create the room named in 'roomName'. Implies autoConnect.")]
    public bool autoCreateRoom;

    [Tooltip("After autoConnect, join the room named in 'roomName'. Implies autoConnect. " +
             "If both autoCreateRoom and autoJoinRoom are set, autoCreateRoom wins.")]
    public bool autoJoinRoom;

    [Tooltip("After joining the room, request a MatchStart from the master client. " +
             "Non-master clients ignore this (only the master can start).")]
    public bool autoStart;

    [Tooltip("Delay in seconds between joining the room and auto-starting, so other " +
             "clients have a chance to join + sync their colour/startSlot first.")]
    [Min(0f)] public float autoStartDelaySec = 3f;

    [Header("Diagnostics")]
    public bool verboseLogs = true;

    // ------------------------------------------------------------------ //
    // Constants — same palette as the lobby colour picker.
    // ------------------------------------------------------------------ //

    private static readonly (string name, Color color)[] Palette =
    {
        ("blue",   new Color(0.20f, 0.55f, 1.00f)),
        ("red",    new Color(0.92f, 0.20f, 0.20f)),
        ("green",  new Color(0.25f, 0.80f, 0.32f)),
        ("yellow", new Color(1.00f, 0.85f, 0.18f)),
        ("orange", new Color(1.00f, 0.55f, 0.10f)),
        ("purple", new Color(0.65f, 0.30f, 0.85f)),
    };

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        // Priority: CLI > MPPM tags > Inspector. Apply lowest priority first
        // (it's already in the fields), then overwrite with higher priorities.
        ReadMppmTags();
        ParseCommandLineArgs();
        ApplyIdentity();
    }

    private IEnumerator Start()
    {
        NetworkManagerRTS.OnRoomJoinedEvent += HandleRoomJoined;

        if (!autoConnect && !autoCreateRoom && !autoJoinRoom)
            yield break;     // inert — no auto-actions requested

        if (NetworkManagerRTS.Instance == null)
        {
            Log("autoConnect requested but no NetworkManagerRTS in scene.", warn: true);
            yield break;
        }

        Log("autoConnect → enabling multiplayerMode + Connect().");
        NetworkManagerRTS.Instance.multiplayerMode = true;

        // Reveal the lobby canvas so the player sees status text + can intervene.
        if (MultiplayerLobbyUI.Instance != null)
            MultiplayerLobbyUI.Instance.ShowOnlineMenu();

        NetworkManagerRTS.Instance.Connect();

        // NetworkManagerRTS queues create/join intents until OnConnectedToMaster,
        // so we don't have to wait here.
        string rn = string.IsNullOrWhiteSpace(roomName) ? "DevRoom" : roomName;
        if (autoCreateRoom)
        {
            Log($"autoCreateRoom → CreateRoom('{rn}').");
            NetworkManagerRTS.Instance.CreateRoom(
                rn, MapRegistry.DefaultMapId, NetworkManagerRTS.DefaultStartingResources);
        }
        else if (autoJoinRoom)
        {
            Log($"autoJoinRoom → JoinRoomByName('{rn}').");
            NetworkManagerRTS.Instance.JoinRoomByName(rn);
        }
    }

    private void OnDestroy()
    {
        NetworkManagerRTS.OnRoomJoinedEvent -= HandleRoomJoined;
    }

    // ------------------------------------------------------------------ //
    // Room-joined → start-slot push + (optional) auto-start
    // ------------------------------------------------------------------ //

    private void HandleRoomJoined()
    {
        if (startSlot >= 0 && startSlot <= 3 && NetworkManagerRTS.Instance != null)
        {
            Log($"applying startSlot = {startSlot} ({(char)('A' + startSlot)}).");
            NetworkManagerRTS.Instance.SetLocalStartSlot(startSlot);
        }
        if (autoStart) StartCoroutine(AutoStartAfter(autoStartDelaySec));
    }

    private IEnumerator AutoStartAfter(float sec)
    {
        if (sec > 0f) yield return new WaitForSeconds(sec);

#if PHOTON_UNITY_NETWORKING
        if (!PhotonNetwork.InRoom)
        {
            Log("autoStart skipped — not in a room.", warn: true);
            yield break;
        }
        if (!PhotonNetwork.IsMasterClient)
        {
            Log("autoStart skipped — not master client (only master can start).");
            yield break;
        }
#endif
        if (NetworkMatchCoordinator.Instance == null)
        {
            Log("autoStart skipped — no NetworkMatchCoordinator.", warn: true);
            yield break;
        }

        Color c = ResolveColor(colorName);
        Log("autoStart → RequestMatchStart.");
        NetworkMatchCoordinator.Instance.RequestMatchStart(c);
    }

    // ------------------------------------------------------------------ //
    // Identity application
    // ------------------------------------------------------------------ //

    private void ApplyIdentity()
    {
        if (!string.IsNullOrEmpty(playerName))
        {
#if PHOTON_UNITY_NETWORKING
            PhotonNetwork.NickName = playerName;
#endif
            Log($"NickName = '{playerName}'.");
        }

        if (!string.IsNullOrEmpty(colorName))
        {
            Color c = ResolveColor(colorName);
            string proper = ProperName(colorName);

            if (PlayerFactionManager.Instance != null)
                PlayerFactionManager.Instance.SetColor(c, proper);

            if (NetworkManagerRTS.Instance != null)
                NetworkManagerRTS.Instance.SetLocalPlayerColor(c, proper);

            Log($"color = {proper}.");
        }
    }

    private static Color ResolveColor(string n)
    {
        if (string.IsNullOrEmpty(n)) return Palette[0].color;
        string k = n.Trim().ToLowerInvariant();
        for (int i = 0; i < Palette.Length; i++)
            if (Palette[i].name == k) return Palette[i].color;
        return Palette[0].color;
    }

    private static string ProperName(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        n = n.Trim();
        return char.ToUpperInvariant(n[0]) + n.Substring(1).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ //
    // Command-line args  (-key value, or flags like -autoStart)
    // ------------------------------------------------------------------ //

    private void ParseCommandLineArgs()
    {
        string[] args;
        try { args = System.Environment.GetCommandLineArgs(); }
        catch { return; }

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-playerName":     if (i + 1 < args.Length) playerName = args[++i]; break;
                case "-color":          if (i + 1 < args.Length) colorName  = args[++i]; break;
                case "-room":           if (i + 1 < args.Length) roomName   = args[++i]; break;
                case "-startSlot":      if (i + 1 < args.Length) startSlot  = ParseStartSlot(args[++i]); break;
                case "-autoConnect":    autoConnect    = true; break;
                case "-autoCreateRoom": autoCreateRoom = true; autoConnect = true; break;
                case "-autoJoinRoom":   autoJoinRoom   = true; autoConnect = true; break;
                case "-autoStart":      autoStart      = true; break;
            }
        }
    }

    private static int ParseStartSlot(string s)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        s = s.Trim();
        if (s.Length == 1)
        {
            char ch = char.ToUpperInvariant(s[0]);
            if (ch >= 'A' && ch <= 'D') return ch - 'A';
        }
        return int.TryParse(s, out int v) ? v : -1;
    }

    // ------------------------------------------------------------------ //
    // MPPM Player Tags — read via reflection so the package is OPTIONAL.
    //
    // Tag formats supported (one tag per setting):
    //   "playerName=Alpha"   "color=blue"   "room=DevRoom"   "startSlot=A"
    //   "autoConnect"   "autoCreateRoom"   "autoJoinRoom"   "autoStart"
    // ------------------------------------------------------------------ //

    private void ReadMppmTags()
    {
        System.Collections.Generic.IReadOnlyList<string> tags = TryReadMppmTags();
        if (tags == null || tags.Count == 0) return;

        Log($"MPPM tags detected: [{string.Join(", ", tags)}]");
        for (int i = 0; i < tags.Count; i++) ApplyKeyValue(tags[i]);
    }

    private void ApplyKeyValue(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        string key, val;
        int eq = tag.IndexOf('=');
        if (eq >= 0) { key = tag.Substring(0, eq).Trim(); val = tag.Substring(eq + 1).Trim(); }
        else         { key = tag.Trim();                  val = "";                          }

        switch (key)
        {
            case "playerName":     playerName     = val; break;
            case "color":          colorName      = val; break;
            case "room":           roomName       = val; break;
            case "startSlot":      startSlot      = ParseStartSlot(val); break;
            case "autoConnect":    autoConnect    = true; break;
            case "autoCreateRoom": autoCreateRoom = true; autoConnect = true; break;
            case "autoJoinRoom":   autoJoinRoom   = true; autoConnect = true; break;
            case "autoStart":      autoStart      = true; break;
        }
    }

    /// <summary>
    /// Try to read MPPM CurrentPlayer.ReadOnlyTags() without a hard reference to
    /// the package. Returns null if the package isn't installed (the type
    /// doesn't resolve), or if the call throws.
    /// </summary>
    private static System.Collections.Generic.IReadOnlyList<string> TryReadMppmTags()
    {
        try
        {
            var type = System.Type.GetType(
                "Unity.Multiplayer.Playmode.CurrentPlayer, Unity.Multiplayer.Playmode");
            if (type == null) return null;

            var method = type.GetMethod("ReadOnlyTags",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method == null) return null;

            object result = method.Invoke(null, null);
            return result as System.Collections.Generic.IReadOnlyList<string>;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ //

    private void Log(string msg, bool warn = false)
    {
        if (!verboseLogs && !warn) return;
        if (warn) Debug.LogWarning($"[DevBootstrap] {msg}");
        else      Debug.Log($"[DevBootstrap] {msg}");
    }
}
