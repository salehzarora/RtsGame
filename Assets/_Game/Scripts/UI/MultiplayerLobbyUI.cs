using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

#if PHOTON_UNITY_NETWORKING
using Photon.Pun;
using Photon.Realtime;
#endif

/// <summary>
/// Phase 6 — single runtime controller that owns the four lobby panels.
/// Each panel is a child GameObject; this script toggles them via
/// <see cref="ShowPanel"/> based on the user's navigation.
///
/// Panels (Inspector-wired by <c>SetupMultiplayerLobbyUI</c>):
///   • OnlineMenuPanel — Connect / Create Room / Join Random / Browse / Back.
///   • CreateRoomPanel — RoomName input + map label + Create + Back.
///   • RoomListPanel   — Refresh + N row buttons + Back.
///   • LobbyPanel      — Player slots, colour picker, Start (host),
///                       Waiting label (slave), Leave.
///
/// Lifecycle:
///   • Lives on a separate Canvas ("LobbyCanvas") so it's independent of the
///     Main Menu and the gameplay HUD.
///   • Visible only after the player clicks "Online" on the Main Menu —
///     <see cref="ShowOnlineMenu"/> is the entry point.
///   • Hidden when the match starts (<see cref="HandleMatchStarted"/>) or
///     when the player clicks Back on the Online menu.
///
/// Single-player path: this controller doesn't run any logic until the
/// Main Menu's Online button shows the canvas; SP is unaffected.
/// </summary>
[DisallowMultipleComponent]
public class MultiplayerLobbyUI : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector — set up by SetupMultiplayerLobbyUI
    // ------------------------------------------------------------------ //

    [Header("Canvas root (toggled on Online click + off on MatchStart)")]
    public GameObject canvasRoot;

    [Header("Main menu canvas (hidden while lobby is shown)")]
    public GameObject mainMenuCanvas;

    [Header("Panels")]
    public GameObject onlineMenuPanel;
    public GameObject createRoomPanel;
    public GameObject roomListPanel;
    public GameObject lobbyPanel;

    [Header("Online menu controls")]
    public Button onlineConnectButton;
    public Button onlineCreateRoomButton;
    public Button onlineJoinRandomButton;
    public Button onlineBrowseRoomsButton;
    public Button onlineBackButton;
    public TextMeshProUGUI onlineStatusLabel;

    [Header("Create room controls")]
    public TMP_InputField createRoomNameInput;
    public TextMeshProUGUI createRoomMapLabel;
    public Button createRoomConfirmButton;
    public Button createRoomBackButton;

    [Tooltip("Phase 8 — cycle button. Each click advances through " +
             "startingResourcesOptions and updates createRoomStartingResourcesLabel.")]
    public Button createRoomStartingResourcesButton;
    public TextMeshProUGUI createRoomStartingResourcesLabel;

    [Tooltip("Allowed values for the host's starting-resources picker. " +
             "MUST match the spec: 5000 / 10000 / 20000 / 30000 / 40000 / 50000.")]
    public int[] startingResourcesOptions = { 5000, 10000, 20000, 30000, 40000, 50000 };

    /// <summary>Currently-selected index into <see cref="startingResourcesOptions"/>.</summary>
    private int startingResourcesIndex = 1;     // default → 10000

    [Header("Room list controls")]
    public Button roomListRefreshButton;
    public Button roomListBackButton;
    /// <summary>
    /// Pre-built row buttons (one per visible room). The editor tool
    /// creates a fixed number (e.g. 8). MultiplayerLobbyUI populates each
    /// row's label + click handler from the cached room list.
    /// </summary>
    public Button[] roomListRowButtons;
    public TextMeshProUGUI[] roomListRowLabels;

    [Header("Lobby controls")]
    public TextMeshProUGUI lobbyRoomNameLabel;
    public TextMeshProUGUI lobbyMapLabel;
    [Tooltip("Phase 8 — read-only label showing the host's chosen starting " +
             "resources value (read from the Photon room's custom properties).")]
    public TextMeshProUGUI lobbyStartingResourcesLabel;
    [Tooltip("One label per player slot (up to 4). Slot index = sorted-actor " +
             "order. Shows actor #, colour, (you), and chosen corner.")]
    public TextMeshProUGUI[] lobbyPlayerLabels;
    [Tooltip("One colour swatch per player slot (up to 4), index-aligned with " +
             "lobbyPlayerLabels.")]
    public Image[]           lobbyPlayerSwatches;

    [Header("Start-position picker (A/B/C/D corners)")]
    [Tooltip("The 'Choose Start Position' preview panel (toggled for visibility only).")]
    public GameObject mapPreviewPanel;
    [Tooltip("Four corner buttons, index-aligned to corner 0=A,1=B,2=C,3=D.")]
    public Button[] cornerButtons;
    [Tooltip("Four corner button labels (A/B/C/D + owner), index-aligned to cornerButtons.")]
    public TextMeshProUGUI[] cornerLabels;

    public Button[] lobbyColorButtons;                  // 6 colour buttons
    public string[] lobbyColorNames = { "Blue", "Red", "Green", "Yellow", "Orange", "Purple" };
    public Color[]  lobbyColorValues;                   // same length, set by editor tool
    public Button   lobbyStartMatchButton;
    public Button   lobbyLeaveRoomButton;
    public TextMeshProUGUI lobbyStatusLabel;            // "Waiting for player 2..." / "Waiting for host..."

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    public static MultiplayerLobbyUI Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[LobbyUI] Duplicate MultiplayerLobbyUI destroyed.");
            Destroy(this);
            return;
        }
        Instance = this;
        Debug.Log("[LobbyUI] Instance ready — Online button can now open the lobby.");

        // Hide the entire lobby canvas at scene start. The main menu's
        // "Online" button will call ShowOnlineMenu() to reveal it.
        // Note this component now lives on GameManager (Phase 7 fix), so
        // Awake fires regardless of the canvas being inactive in the scene.
        if (canvasRoot != null) canvasRoot.SetActive(false);

        WireButtons();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void OnEnable()
    {
#if PHOTON_UNITY_NETWORKING
        NetworkManagerRTS.OnRoomListChanged              += HandleRoomListChanged;
        NetworkManagerRTS.OnRoomJoinedEvent              += HandleRoomJoined;
        NetworkManagerRTS.OnRoomLeftEvent                += HandleRoomLeft;
        NetworkManagerRTS.OnPlayerPropertiesUpdatedEvent += HandlePlayerPropertiesUpdated;
#endif
        NetworkMatchCoordinator.OnMatchStarted += HandleMatchStarted;
    }

    private void OnDisable()
    {
#if PHOTON_UNITY_NETWORKING
        NetworkManagerRTS.OnRoomListChanged              -= HandleRoomListChanged;
        NetworkManagerRTS.OnRoomJoinedEvent              -= HandleRoomJoined;
        NetworkManagerRTS.OnRoomLeftEvent                -= HandleRoomLeft;
        NetworkManagerRTS.OnPlayerPropertiesUpdatedEvent -= HandlePlayerPropertiesUpdated;
#endif
        NetworkMatchCoordinator.OnMatchStarted -= HandleMatchStarted;
    }

    private void Update()
    {
        // Lightweight per-frame refresh of online-menu status text.
        // (Photon doesn't fire a callback for every connection state tick.)
        if (canvasRoot != null && canvasRoot.activeSelf &&
            onlineMenuPanel != null && onlineMenuPanel.activeSelf)
        {
            RefreshOnlineStatusLabel();
        }
    }

    // ------------------------------------------------------------------ //
    // Public entry — called by the Main Menu's "Online" button
    // ------------------------------------------------------------------ //

    public void ShowOnlineMenu()
    {
        Debug.Log("[LobbyUI] ShowOnlineMenu.");

        // Late-resolve mainMenuCanvas if the Inspector ref is missing —
        // makes the Online button work even when the lobby was set up
        // before the main menu in the scene.
        if (mainMenuCanvas == null)
            mainMenuCanvas = GameObject.Find("MainMenuCanvas");

        if (mainMenuCanvas != null) mainMenuCanvas.SetActive(false);
        if (canvasRoot != null)     canvasRoot.SetActive(true);

        ShowPanel(onlineMenuPanel);
        RefreshOnlineStatusLabel();
    }

    // ------------------------------------------------------------------ //
    // Panel routing
    // ------------------------------------------------------------------ //

    private void ShowPanel(GameObject panel)
    {
        if (onlineMenuPanel != null) onlineMenuPanel.SetActive(panel == onlineMenuPanel);
        if (createRoomPanel != null) createRoomPanel.SetActive(panel == createRoomPanel);
        if (roomListPanel != null)   roomListPanel.SetActive(panel == roomListPanel);
        if (lobbyPanel != null)      lobbyPanel.SetActive(panel == lobbyPanel);
    }

    // ------------------------------------------------------------------ //
    // Button wiring
    // ------------------------------------------------------------------ //

    private void WireButtons()
    {
        // Online menu
        if (onlineConnectButton    != null) onlineConnectButton.onClick.AddListener(OnClickConnect);
        if (onlineCreateRoomButton != null) onlineCreateRoomButton.onClick.AddListener(OnClickShowCreateRoom);
        if (onlineJoinRandomButton != null) onlineJoinRandomButton.onClick.AddListener(OnClickJoinRandom);
        if (onlineBrowseRoomsButton!= null) onlineBrowseRoomsButton.onClick.AddListener(OnClickBrowseRooms);
        if (onlineBackButton       != null) onlineBackButton.onClick.AddListener(OnClickBackToMainMenu);

        // Create room
        if (createRoomConfirmButton != null) createRoomConfirmButton.onClick.AddListener(OnClickCreateRoomConfirm);
        if (createRoomBackButton    != null) createRoomBackButton.onClick.AddListener(() => ShowPanel(onlineMenuPanel));
        if (createRoomStartingResourcesButton != null)
            createRoomStartingResourcesButton.onClick.AddListener(OnClickCycleStartingResources);
        RefreshStartingResourcesLabel();

        // Room list
        if (roomListRefreshButton != null) roomListRefreshButton.onClick.AddListener(RefreshRoomListRows);
        if (roomListBackButton    != null) roomListBackButton.onClick.AddListener(() => ShowPanel(onlineMenuPanel));

        // Lobby
        if (lobbyStartMatchButton != null) lobbyStartMatchButton.onClick.AddListener(OnClickStartMatch);
        if (lobbyLeaveRoomButton  != null) lobbyLeaveRoomButton.onClick.AddListener(OnClickLeaveRoom);

        // Colour swatches — wire each by index.
        if (lobbyColorButtons != null)
        {
            for (int i = 0; i < lobbyColorButtons.Length; i++)
            {
                int idx = i;     // capture
                if (lobbyColorButtons[i] != null)
                    lobbyColorButtons[i].onClick.AddListener(() => OnClickLobbyColor(idx));
            }
        }

        // Start-position corner buttons — wire each to its corner index.
        if (cornerButtons != null)
        {
            for (int i = 0; i < cornerButtons.Length; i++)
            {
                int corner = i;     // capture
                if (cornerButtons[i] != null)
                    cornerButtons[i].onClick.AddListener(() => OnClickCorner(corner));
            }
        }
    }

    // ------------------------------------------------------------------ //
    // Online menu actions
    // ------------------------------------------------------------------ //

    private void OnClickConnect()
    {
        if (NetworkManagerRTS.Instance == null)
        {
            Debug.LogWarning("[Lobby] Cannot connect — no NetworkManagerRTS in scene.");
            return;
        }
        // Force multiplayer mode on — opening the lobby implies the player
        // wants to play online. SP path doesn't go through here.
        NetworkManagerRTS.Instance.multiplayerMode = true;
        NetworkManagerRTS.Instance.Connect();
    }

    private void OnClickShowCreateRoom()
    {
        if (createRoomMapLabel != null)
            createRoomMapLabel.text = "Map: " + MapRegistry.DisplayNameOrId(MapRegistry.DefaultMapId);
        if (createRoomNameInput != null && string.IsNullOrEmpty(createRoomNameInput.text))
            createRoomNameInput.text = "Room " + Random.Range(100, 1000);
        ShowPanel(createRoomPanel);
    }

    private void OnClickJoinRandom()
    {
        if (NetworkManagerRTS.Instance == null) return;
        NetworkManagerRTS.Instance.multiplayerMode = true;
        NetworkManagerRTS.Instance.JoinRandomRoom();
    }

    private void OnClickBrowseRooms()
    {
        ShowPanel(roomListPanel);
        RefreshRoomListRows();
    }

    private void OnClickBackToMainMenu()
    {
        if (canvasRoot != null)     canvasRoot.SetActive(false);
        if (mainMenuCanvas != null) mainMenuCanvas.SetActive(true);

        // Full session cleanup (entities + resources + power + selection + UI +
        // color slots + coordinator + Photon player props). Covers backing out
        // of the lobby (or a hosted room without starting a match) so a future
        // match starts completely clean. Idempotent.
        MatchSessionManager.CleanupPreviousMatch();
    }

    private void RefreshOnlineStatusLabel()
    {
        if (onlineStatusLabel == null) return;
#if PHOTON_UNITY_NETWORKING
        if (!PhotonNetwork.IsConnected)
            onlineStatusLabel.text = "Status: Disconnected";
        else if (PhotonNetwork.InRoom)
            onlineStatusLabel.text = $"Status: In room ({PhotonNetwork.CurrentRoom.Name})";
        else if (PhotonNetwork.InLobby)
            onlineStatusLabel.text = "Status: Connected (lobby)";
        else
            onlineStatusLabel.text = $"Status: {PhotonNetwork.NetworkClientState}";
#else
        onlineStatusLabel.text = "Photon PUN not installed.";
#endif
    }

    // ------------------------------------------------------------------ //
    // Create-room confirm
    // ------------------------------------------------------------------ //

    private void OnClickCreateRoomConfirm()
    {
        if (NetworkManagerRTS.Instance == null) return;

        string roomName = createRoomNameInput != null ? createRoomNameInput.text.Trim() : null;
        if (string.IsNullOrEmpty(roomName)) roomName = "Room " + Random.Range(100, 1000);

        int startingResources = GetSelectedStartingResources();

        NetworkManagerRTS.Instance.multiplayerMode = true;
        NetworkManagerRTS.Instance.CreateRoom(
            roomName, MapRegistry.DefaultMapId, startingResources);
        // OnJoinedRoom will fire and HandleRoomJoined switches us to the LobbyPanel.
    }

    // ------------------------------------------------------------------ //
    // Starting-resources picker
    // ------------------------------------------------------------------ //

    private void OnClickCycleStartingResources()
    {
        if (startingResourcesOptions == null || startingResourcesOptions.Length == 0) return;
        startingResourcesIndex =
            (startingResourcesIndex + 1) % startingResourcesOptions.Length;
        RefreshStartingResourcesLabel();
    }

    private void RefreshStartingResourcesLabel()
    {
        if (createRoomStartingResourcesLabel == null) return;
        createRoomStartingResourcesLabel.text =
            "Starting Resources: " + GetSelectedStartingResources();
    }

    private int GetSelectedStartingResources()
    {
        if (startingResourcesOptions == null || startingResourcesOptions.Length == 0)
            return NetworkManagerRTS.DefaultStartingResources;
        int idx = Mathf.Clamp(startingResourcesIndex, 0, startingResourcesOptions.Length - 1);
        return startingResourcesOptions[idx];
    }

    // ------------------------------------------------------------------ //
    // Room list
    // ------------------------------------------------------------------ //

    private void HandleRoomListChanged() => RefreshRoomListRows();

    private void RefreshRoomListRows()
    {
        if (roomListRowButtons == null || roomListRowLabels == null) return;

#if PHOTON_UNITY_NETWORKING
        var rooms = NetworkManagerRTS.CachedRoomList;
        int max = Mathf.Min(roomListRowButtons.Length, rooms.Count);

        for (int i = 0; i < roomListRowButtons.Length; i++)
        {
            Button b = roomListRowButtons[i];
            if (b == null) continue;

            if (i < max)
            {
                RoomInfo info = rooms[i];
                string mapName = "Unknown Map";
                if (info.CustomProperties != null &&
                    info.CustomProperties.TryGetValue(NetworkManagerRTS.RoomMapPropKey, out object o) &&
                    o is string mapId)
                {
                    mapName = MapRegistry.DisplayNameOrId(mapId);
                }
                bool full = info.PlayerCount >= info.MaxPlayers && info.MaxPlayers > 0;

                int srVal = NetworkManagerRTS.DefaultStartingResources;
                if (info.CustomProperties != null &&
                    info.CustomProperties.TryGetValue(
                        NetworkManagerRTS.RoomStartingResourcesPropKey, out object srObj) &&
                    srObj is int srInt)
                {
                    srVal = srInt;
                }

                if (i < roomListRowLabels.Length && roomListRowLabels[i] != null)
                    roomListRowLabels[i].text =
                        $"{info.Name}   {info.PlayerCount}/{info.MaxPlayers}   [{mapName}]   " +
                        $"Resources {srVal}" + (full ? "   (FULL)" : "");

                string capturedName = info.Name;
                b.onClick.RemoveAllListeners();
                b.onClick.AddListener(() =>
                {
                    Debug.Log($"[Lobby] Joining '{capturedName}' from list.");
                    NetworkManagerRTS.Instance.multiplayerMode = true;
                    NetworkManagerRTS.Instance.JoinRoomByName(capturedName);
                });
                b.interactable = !full;
                b.gameObject.SetActive(true);
            }
            else
            {
                b.gameObject.SetActive(false);
            }
        }
#endif
    }

    // ------------------------------------------------------------------ //
    // Lobby panel
    // ------------------------------------------------------------------ //

    private void HandleRoomJoined()
    {
        Debug.Log("[Lobby] Room joined — switching to LobbyPanel.");
        if (canvasRoot != null)     canvasRoot.SetActive(true);
        if (mainMenuCanvas != null) mainMenuCanvas.SetActive(false);
        ShowPanel(lobbyPanel);
        RefreshLobby();
    }

    private void HandleRoomLeft()
    {
        Debug.Log("[Lobby] Room left — returning to online menu.");
        ShowPanel(onlineMenuPanel);
    }

#if PHOTON_UNITY_NETWORKING
    private void HandlePlayerPropertiesUpdated(Player _) => RefreshLobby();
#endif

    private void RefreshLobby()
    {
#if PHOTON_UNITY_NETWORKING
        if (!PhotonNetwork.InRoom) return;
        Room room = PhotonNetwork.CurrentRoom;

        if (lobbyRoomNameLabel != null)
            lobbyRoomNameLabel.text = "Room: " + room.Name;

        string mapId = MapRegistry.DefaultMapId;
        if (room.CustomProperties != null &&
            room.CustomProperties.TryGetValue(NetworkManagerRTS.RoomMapPropKey, out object o) &&
            o is string s)
            mapId = s;
        if (lobbyMapLabel != null)
            lobbyMapLabel.text = "Map: " + MapRegistry.DisplayNameOrId(mapId);

        // Phase 8 — starting-resources lobby display.
        if (lobbyStartingResourcesLabel != null)
        {
            int startingResources = NetworkManagerRTS.DefaultStartingResources;
            if (room.CustomProperties != null &&
                room.CustomProperties.TryGetValue(
                    NetworkManagerRTS.RoomStartingResourcesPropKey, out object srObj) &&
                srObj is int srInt)
            {
                startingResources = srInt;
            }
            lobbyStartingResourcesLabel.text = "Starting Resources: " + startingResources;
        }

        // Sort players by ActorNumber → slot 0..N-1.
        var sorted = new List<Player>(room.Players.Values);
        sorted.Sort((a, b) => a.ActorNumber.CompareTo(b.ActorNumber));

        // Render up to 4 player rows; absent slots show "<empty>".
        int slotCount = lobbyPlayerLabels != null ? lobbyPlayerLabels.Length : 0;
        for (int i = 0; i < slotCount; i++)
        {
            Player p = i < sorted.Count ? sorted[i] : null;
            SetSlotUi(i, p);
        }

        RefreshCornerButtons(sorted);

        int count = room.PlayerCount;
        int max   = room.MaxPlayers > 0 ? room.MaxPlayers : 4;
        bool localIsHost = PhotonNetwork.IsMasterClient;

        // Start button — host can start with 1–4 players; non-host doesn't see it.
        if (lobbyStartMatchButton != null)
        {
            lobbyStartMatchButton.gameObject.SetActive(localIsHost);
            lobbyStartMatchButton.interactable = localIsHost && count >= 1;
        }

        // Status label — no longer blocks on player count.
        if (lobbyStatusLabel != null)
        {
            lobbyStatusLabel.text = localIsHost
                ? $"Players: {count} / {max} — Ready to start"
                : $"Players: {count} / {max} — Waiting for host";
        }
#endif
    }

#if PHOTON_UNITY_NETWORKING
    // Slot colour for a player: their chosen colour property, else slot default.
    private static Color SlotColor(Player player, int slotIndex)
    {
        Color color = MultiplayerColors.ForOwnerOrDefault(slotIndex);
        if (player != null && player.CustomProperties != null &&
            player.CustomProperties.TryGetValue(NetworkManagerRTS.ColorPropKey, out object rgb) &&
            rgb is Vector3 v)
        {
            color = new Color(v.x, v.y, v.z, 1f);
        }
        return color;
    }

    private void SetSlotUi(int slotIndex, Player player)
    {
        if (lobbyPlayerLabels == null || slotIndex >= lobbyPlayerLabels.Length) return;
        TextMeshProUGUI label = lobbyPlayerLabels[slotIndex];
        Image swatch = (lobbyPlayerSwatches != null && slotIndex < lobbyPlayerSwatches.Length)
            ? lobbyPlayerSwatches[slotIndex] : null;
        if (label == null) return;

        // Player N is 1-indexed for display ("Player 1".."Player 4").
        if (player == null)
        {
            label.text = $"Player {slotIndex + 1}: <empty>";
            if (swatch != null) swatch.color = new Color(0.18f, 0.18f, 0.20f, 1f);
            return;
        }

        string colorName = "(default)";
        Color  color     = SlotColor(player, slotIndex);
        if (player.CustomProperties != null &&
            player.CustomProperties.TryGetValue(NetworkManagerRTS.ColorNamePropKey, out object n) &&
            n is string s && !string.IsNullOrEmpty(s))
        {
            colorName = s;
        }

        bool isYou = PhotonNetwork.LocalPlayer.ActorNumber == player.ActorNumber;
        string youSuffix = isYou ? " — you" : "";
        string cornerSuffix = NetworkManagerRTS.TryGetPlayerStartSlot(player.ActorNumber, out int corner)
            ? $" — Corner {(char)('A' + corner)}"
            : " — (no corner)";

        label.text = $"Player {slotIndex + 1}: actor #{player.ActorNumber} — {colorName}{youSuffix}{cornerSuffix}";
        if (swatch != null) swatch.color = color;
    }

    // Available (unclaimed) corner colour.
    private static readonly Color CornerAvailableColor = new Color(0.25f, 0.55f, 0.30f, 1f);

    // Paint each A/B/C/D dot: green=available, your colour=yours, dimmed
    // other-colour + non-interactable=taken by someone else.
    private void RefreshCornerButtons(List<Player> sorted)
    {
        if (cornerButtons == null) return;

        int localActor = PhotonNetwork.LocalPlayer != null
            ? PhotonNetwork.LocalPlayer.ActorNumber : -1;

        for (int c = 0; c < cornerButtons.Length; c++)
        {
            Button btn = cornerButtons[c];
            if (btn == null) continue;
            char letter = (char)('A' + c);

            Player ownerP = null;
            int ownerSlot = -1;
            for (int j = 0; j < sorted.Count; j++)
            {
                if (NetworkManagerRTS.TryGetPlayerStartSlot(sorted[j].ActorNumber, out int sc) && sc == c)
                {
                    ownerP = sorted[j];
                    ownerSlot = j;
                    break;
                }
            }

            Image img = btn.image;
            TextMeshProUGUI lbl = (cornerLabels != null && c < cornerLabels.Length) ? cornerLabels[c] : null;

            if (ownerP == null)
            {
                if (img != null) img.color = CornerAvailableColor;
                btn.interactable = true;
                if (lbl != null) lbl.text = letter.ToString();
            }
            else if (ownerP.ActorNumber == localActor)
            {
                if (img != null) img.color = SlotColor(ownerP, ownerSlot);
                btn.interactable = true;            // click again to release
                if (lbl != null) lbl.text = $"{letter}\n(You)";
            }
            else
            {
                Color taken = SlotColor(ownerP, ownerSlot) * 0.6f; taken.a = 1f;
                if (img != null) img.color = taken;
                btn.interactable = false;           // blocked — taken by another player
                if (lbl != null) lbl.text = $"{letter}\nP{ownerSlot + 1}";
            }
        }
    }
#endif

    // ------------------------------------------------------------------ //
    // Lobby buttons
    // ------------------------------------------------------------------ //

    private void OnClickLobbyColor(int idx)
    {
        if (idx < 0 || lobbyColorValues == null || idx >= lobbyColorValues.Length) return;

        Color c = lobbyColorValues[idx];
        string name = idx < lobbyColorNames.Length ? lobbyColorNames[idx] : "Custom";

        if (PlayerFactionManager.Instance != null)
            PlayerFactionManager.Instance.SetColor(c, name);

        if (NetworkManagerRTS.Instance != null)
            NetworkManagerRTS.Instance.SetLocalPlayerColor(c, name);

        RefreshLobby();
    }

    /// <summary>
    /// Corner dot click. Toggles off if you click your current corner; blocks
    /// if another player already holds it; otherwise selects it (which frees
    /// your previous corner automatically, since startSlot is a single value).
    /// </summary>
    private void OnClickCorner(int corner)
    {
#if PHOTON_UNITY_NETWORKING
        if (!PhotonNetwork.InRoom || PhotonNetwork.LocalPlayer == null) return;
        int localActor = PhotonNetwork.LocalPlayer.ActorNumber;

        // Blocked if another player already chose this corner.
        foreach (var kv in PhotonNetwork.CurrentRoom.Players)
        {
            Player p = kv.Value;
            if (p.ActorNumber == localActor) continue;
            if (NetworkManagerRTS.TryGetPlayerStartSlot(p.ActorNumber, out int sc) && sc == corner)
            {
                Debug.Log($"[StartSlot] Corner {(char)('A' + corner)} taken by actor " +
                          $"#{p.ActorNumber} — selection blocked.");
                return;
            }
        }

        NetworkManagerRTS.TryGetPlayerStartSlot(localActor, out int myCurrent);
        int next = (myCurrent == corner) ? NetworkManagerRTS.NoStartSlot : corner;
        NetworkManagerRTS.Instance?.SetLocalStartSlot(next);

        RefreshLobby();
#endif
    }

    private void OnClickStartMatch()
    {
        if (NetworkMatchCoordinator.Instance == null)
        {
            Debug.LogWarning("[Lobby] Start clicked but no NetworkMatchCoordinator in scene.");
            return;
        }
        Color local = PlayerFactionManager.Instance != null
            ? PlayerFactionManager.Instance.SelectedColor
            : MultiplayerColors.DefaultPlayer0Color;
        bool willFire = NetworkMatchCoordinator.Instance.RequestMatchStart(local);
        Debug.Log($"[Lobby] Start Match requested — coordinator returned {willFire}.");
    }

    private void OnClickLeaveRoom()
    {
        if (NetworkManagerRTS.Instance == null) return;
        NetworkManagerRTS.Instance.LeaveRoom();
    }

    private void HandleMatchStarted()
    {
        Debug.Log("[Lobby] MatchStart received — hiding lobby canvas.");
        if (canvasRoot != null) canvasRoot.SetActive(false);
    }
}
