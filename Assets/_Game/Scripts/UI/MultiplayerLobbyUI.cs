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
        // Smart duplicate resolution. The old first-wins guard could destroy
        // the GOOD instance (the one whose canvasRoot pointed at the visible
        // LobbyCanvas) just because the other one happened to Awake first.
        // Now we PREFER the instance whose serialized canvasRoot lives in
        // the currently-active scene (i.e. is actually the visible lobby),
        // and fall back to scene-of-component, then to first-found.
        if (Instance != null && Instance != this)
        {
            if (PreferThisOverInstance())
            {
                Debug.Log($"[LobbyUI] Replacing previous Instance " +
                          $"(this='{gameObject.name}' in scene '{gameObject.scene.name}' wins). " +
                          $"Old Instance was on '{Instance.gameObject.name}' " +
                          $"in scene '{Instance.gameObject.scene.name}'.");
                Destroy(Instance);
                Instance = this;
            }
            else
            {
                Debug.LogWarning($"[LobbyUI] Duplicate MultiplayerLobbyUI destroyed on " +
                                 $"'{gameObject.name}' (scene='{gameObject.scene.name}', " +
                                 $"persistent={IsPersistent(gameObject)}); " +
                                 $"keeping winner on '{Instance.gameObject.name}'.");
                Destroy(this);
                return;
            }
        }
        else
        {
            Instance = this;
        }

        Debug.Log($"[LobbyUI] Instance ready on '{gameObject.name}' " +
                  $"(scene='{gameObject.scene.name}', " +
                  $"persistent={IsPersistent(gameObject)}, " +
                  $"canvasRoot='{(canvasRoot != null ? canvasRoot.name : "<null>")}').");

        // Hide the entire lobby canvas at scene start. The main menu's
        // "Online" button will call ShowOnlineMenu() to reveal it.
        if (canvasRoot != null) canvasRoot.SetActive(false);

        WireButtons();
    }

    /// <summary>
    /// True if this instance is a better fit than the current static Instance.
    /// Preference order: canvasRoot in active scene → component in active
    /// scene → otherwise no swap.
    /// </summary>
    private bool PreferThisOverInstance()
    {
        if (Instance == null) return true;
        UnityEngine.SceneManagement.Scene active =
            UnityEngine.SceneManagement.SceneManager.GetActiveScene();

        bool thisCanvasInActive = canvasRoot != null && canvasRoot.scene == active;
        bool inCanvasInActive   = Instance.canvasRoot != null && Instance.canvasRoot.scene == active;
        if (thisCanvasInActive && !inCanvasInActive) return true;
        if (!thisCanvasInActive && inCanvasInActive) return false;

        bool thisGoInActive = gameObject.scene == active;
        bool inGoInActive   = Instance.gameObject.scene == active;
        if (thisGoInActive && !inGoInActive) return true;
        return false;
    }

    /// <summary>True if <paramref name="go"/> is in the DontDestroyOnLoad pseudo-scene.</summary>
    private static bool IsPersistent(GameObject go)
    {
        return go != null && go.scene.buildIndex == -1 && go.scene.name == "DontDestroyOnLoad";
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

        // F7 = raycast-all under the cursor and log the top 5 hits. Use this
        // to find a hidden raycast blocker when the Online buttons look fine
        // but no [ClickProbe] log fires.
        if (Input.GetKeyDown(KeyCode.F7))
        {
            UnityEngine.EventSystems.EventSystem es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null)
            {
                Debug.LogWarning("[RaycastProbe] No active EventSystem — clicks can't be dispatched.");
                return;
            }
            var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = Input.mousePosition };
            var results = new List<UnityEngine.EventSystems.RaycastResult>();
            es.RaycastAll(ped, results);
            if (results.Count == 0)
            {
                Debug.LogWarning("[RaycastProbe] No UI hit under cursor — GraphicRaycaster missing " +
                                 "on Canvas, or pointer is outside any UI.");
                return;
            }
            Debug.Log($"[RaycastProbe] {results.Count} hit(s) under cursor (top {Mathf.Min(5, results.Count)}):");
            int max = Mathf.Min(5, results.Count);
            for (int i = 0; i < max; i++)
            {
                var r = results[i];
                Debug.Log($"[RaycastProbe]   [{i}] '{r.gameObject.name}' " +
                          $"(depth={r.depth}, sortingOrder={r.sortingOrder}, " +
                          $"distance={r.distance:F1})");
            }
        }
    }

    // ------------------------------------------------------------------ //
    // Runtime self-heal — runs ONCE in Start, after every Awake has fired
    // (so the duplicate-resolution dust has settled and the winner is final).
    // Re-points canvasRoot at the visible LobbyCanvas in the active scene
    // and force-wires every Online button by NAME. This rescues:
    //   • the case where Awake's duplicate guard destroyed the "right"
    //     instance and the survivor's refs point at a stale canvas;
    //   • the case where canvas refs were left null by a cleanup pass;
    //   • the case where the visible canvas was rebuilt after Awake ran.
    // Idempotent: RemoveAllListeners + AddListener guarantees one handler
    // per button regardless of how many times this fires.
    // ------------------------------------------------------------------ //

    private void Start()
    {
        RuntimeSelfHealOnlineWiring();
        LogRuntimeInstanceCensus();
    }

    private void RuntimeSelfHealOnlineWiring()
    {
        // Find the visible LobbyCanvas in the active scene. If multiple
        // remain (cleanup not yet run), prefer the most-populated one — a
        // stale duplicate is usually empty.
        UnityEngine.SceneManagement.Scene activeScene =
            UnityEngine.SceneManagement.SceneManager.GetActiveScene();

        GameObject best = null;
        int bestChildren = -1;
        Transform[] all = FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null || t.parent != null) continue;
            if (t.name != "LobbyCanvas") continue;
            if (t.gameObject.scene != activeScene) continue;

            int kids = t.GetComponentsInChildren<Transform>(true).Length;
            if (kids > bestChildren)
            {
                bestChildren = kids;
                best = t.gameObject;
            }
        }

        if (best == null)
        {
            Debug.LogWarning("[OnlineUI] Runtime self-heal: no LobbyCanvas found in active scene " +
                             $"'{activeScene.name}'. Buttons cannot be wired.");
            return;
        }

        if (canvasRoot != best)
        {
            Debug.LogWarning($"[OnlineUI] Runtime self-heal: canvasRoot re-pointed " +
                             $"to '{best.name}' (was '{(canvasRoot != null ? canvasRoot.name : "null")}').");
            canvasRoot = best;
        }

        GameObject omp = FindChildByName(canvasRoot, "OnlineMenuPanel");
        if (omp == null)
        {
            Debug.LogError("[OnlineUI] Runtime self-heal: 'OnlineMenuPanel' not found inside LobbyCanvas.");
            return;
        }
        onlineMenuPanel = omp;

        ForceWire(omp, "ConnectButton",     ref onlineConnectButton,     OnClickConnect,        "Connect");
        ForceWire(omp, "CreateRoomButton",  ref onlineCreateRoomButton,  OnClickShowCreateRoom, "Create Room");
        ForceWire(omp, "JoinRandomButton",  ref onlineJoinRandomButton,  OnClickJoinRandom,     "Join Random");
        ForceWire(omp, "BrowseRoomsButton", ref onlineBrowseRoomsButton, OnClickBrowseRooms,    "Browse Rooms");
        ForceWire(omp, "BackButton",        ref onlineBackButton,        OnClickBackToMainMenu, "Back");

        // ---- CreateRoomPanel — the form opened by the Online "Create Room" button.
        GameObject crp = FindChildByName(canvasRoot, "CreateRoomPanel");
        if (crp != null)
        {
            createRoomPanel = crp;
            ForceWire(crp, "CreateButton",            ref createRoomConfirmButton,           OnClickCreateRoomConfirm,      "CreateRoomSubmit");
            ForceWire(crp, "BackButton",              ref createRoomBackButton,              OnClickCreateRoomBack,         "CreateRoomBack");
            ForceWire(crp, "StartingResourcesButton", ref createRoomStartingResourcesButton, OnClickCycleStartingResources, "StartingResources");

            // TMP_InputField + MapLabel — re-resolve refs if null.
            if (createRoomNameInput == null)
            {
                GameObject inputGO = FindChildByName(crp, "RoomNameInput");
                TMPro.TMP_InputField input = inputGO != null ? inputGO.GetComponent<TMPro.TMP_InputField>() : null;
                if (input != null)
                {
                    createRoomNameInput = input;
                    Debug.Log("[OnlineUI] Re-attached createRoomNameInput.");
                }
            }
            if (createRoomMapLabel == null)
            {
                GameObject mapGO = FindChildByName(crp, "MapLabel");
                TMPro.TextMeshProUGUI lbl = mapGO != null ? mapGO.GetComponent<TMPro.TextMeshProUGUI>() : null;
                if (lbl != null) createRoomMapLabel = lbl;
            }
            if (createRoomStartingResourcesLabel == null && createRoomStartingResourcesButton != null)
            {
                createRoomStartingResourcesLabel =
                    createRoomStartingResourcesButton.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            }
            RefreshStartingResourcesLabel();
        }
        else
        {
            Debug.LogError("[OnlineUI] ERROR: CreateRoomPanel missing under LobbyCanvas.");
        }

        // ---- RoomListPanel — the room browser.
        GameObject rlp = FindChildByName(canvasRoot, "RoomListPanel");
        if (rlp != null)
        {
            roomListPanel = rlp;
            ForceWire(rlp, "RefreshButton", ref roomListRefreshButton, RefreshRoomListRows, "RoomListRefresh");
            ForceWire(rlp, "BackButton",    ref roomListBackButton,    OnClickRoomListBack, "RoomListBack");
        }

        // ---- LobbyPanel — shown after joining a room.
        GameObject lp = FindChildByName(canvasRoot, "LobbyPanel");
        if (lp != null)
        {
            lobbyPanel = lp;
            ForceWire(lp, "StartMatchButton", ref lobbyStartMatchButton, OnClickStartMatch, "LobbyStartMatch");
            ForceWire(lp, "LeaveButton",      ref lobbyLeaveRoomButton,  OnClickLeaveRoom,  "LobbyLeave");
        }
    }

    private void ForceWire(GameObject parent, string buttonName,
                           ref Button field, UnityEngine.Events.UnityAction handler,
                           string humanLabel)
    {
        GameObject go = FindChildByName(parent, buttonName);
        if (go == null)
        {
            Debug.LogWarning($"[OnlineUI] Force-wire skipped — '{buttonName}' not found under '{parent.name}'.");
            return;
        }
        Button btn = go.GetComponent<Button>();
        if (btn == null)
        {
            Debug.LogWarning($"[OnlineUI] Force-wire skipped — '{buttonName}' has no Button component.");
            return;
        }
        if (field != btn) field = btn;
        btn.interactable = true;
        btn.onClick.RemoveAllListeners();     // nuke stale listeners pointing at a destroyed instance
        btn.onClick.AddListener(handler);

        // Click probe — added once per button per Play.
        if (go.GetComponent<OnlineButtonClickProbe>() == null)
        {
            OnlineButtonClickProbe probe = go.AddComponent<OnlineButtonClickProbe>();
            probe.label = humanLabel;
        }

        Debug.Log($"[OnlineUI] Runtime wired {humanLabel} button.");
    }

    private void LogRuntimeInstanceCensus()
    {
        MultiplayerLobbyUI[] all = FindObjectsByType<MultiplayerLobbyUI>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int live = 0;
        for (int i = 0; i < all.Length; i++) if (all[i] != null) live++;
        Debug.Log($"[OnlineUI] Live MultiplayerLobbyUI instance count: {live}. " +
                  $"Instance is on '{(Instance != null ? Instance.gameObject.name : "<null>")}'.");
        if (live > 1)
        {
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                Debug.LogWarning($"[OnlineUI]   instance '{all[i].gameObject.name}' " +
                                 $"(scene='{all[i].gameObject.scene.name}', " +
                                 $"persistent={IsPersistent(all[i].gameObject)}, " +
                                 $"isInstance={all[i] == Instance}).");
            }
        }
    }

    // ------------------------------------------------------------------ //
    // Public entry — called by the Main Menu's "Online" button
    // ------------------------------------------------------------------ //

    public void ShowOnlineMenu()
    {
        Debug.Log("[LobbyUI] ShowOnlineMenu.");

        // Self-heal canvasRoot if a cleanup pass (or scene reload) invalidated
        // the Inspector reference. Without this, clicking Online does nothing
        // visible — the classic "Online button hang" symptom — because the
        // setactive(true) call below silently no-ops on a null reference.
        if (canvasRoot == null)
        {
            canvasRoot = FindRootByName("LobbyCanvas");
            if (canvasRoot != null)
                Debug.LogWarning("[LobbyUI] canvasRoot was null — late-resolved to scene 'LobbyCanvas'.");
        }

        // Late-resolve mainMenuCanvas (it might be inactive, so use the
        // inactive-inclusive scan too).
        if (mainMenuCanvas == null)
            mainMenuCanvas = FindRootByName("MainMenuCanvas");

        if (mainMenuCanvas != null) mainMenuCanvas.SetActive(false);
        if (canvasRoot != null)
        {
            canvasRoot.SetActive(true);
        }
        else
        {
            Debug.LogError("[LobbyUI] ShowOnlineMenu: no LobbyCanvas found in scene. " +
                           "Re-run Tools → RTS → Multiplayer → Setup Multiplayer Lobby UI.");
            return;
        }

        ShowPanel(onlineMenuPanel);
        RefreshOnlineStatusLabel();
    }

    // Inactive-inclusive root-by-name lookup. `GameObject.Find` skips inactive
    // objects, which bites every time a canvas was hidden by Awake — exactly
    // the path that produced the Online-button hang after the cleanup pass.
    private static GameObject FindRootByName(string name)
    {
        Transform[] all = Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null) continue;
            if (t.parent != null) continue;
            if (t.name != name) continue;
            return t.gameObject;
        }
        return null;
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
        Debug.Log("[OnlineUI] Connect clicked.");
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
        Debug.Log("[OnlineUI] Create Room clicked.");
        Debug.Log("[Lobby] Create Room clicked — kicking off Connect + opening form.");

        // Auto-connect now so the form's Create button has a live connection
        // by the time the user types a name. NetworkManagerRTS.CreateRoom
        // will queue (pendingCreateRoom) if connect isn't done yet, but
        // starting earlier shortens the visible delay.
        if (NetworkManagerRTS.Instance != null)
        {
            NetworkManagerRTS.Instance.multiplayerMode = true;
#if PHOTON_UNITY_NETWORKING
            if (!Photon.Pun.PhotonNetwork.IsConnected)
                NetworkManagerRTS.Instance.Connect();
#endif
        }

        // Self-heal createRoomPanel — a previous cleanup pass may have
        // invalidated the Inspector reference. CreateRoomPanel is a CHILD of
        // the lobby canvas, so we search the canvas subtree (inactive-inclusive).
        if (createRoomPanel == null && canvasRoot != null)
        {
            createRoomPanel = FindChildByName(canvasRoot, "CreateRoomPanel");
            if (createRoomPanel != null)
                Debug.LogWarning("[Lobby] createRoomPanel was null — late-resolved from canvasRoot subtree.");
        }

        if (createRoomMapLabel != null)
            createRoomMapLabel.text = "Map: " + MapRegistry.DisplayNameOrId(MapRegistry.DefaultMapId);
        if (createRoomNameInput != null && string.IsNullOrEmpty(createRoomNameInput.text))
            createRoomNameInput.text = "Room " + Random.Range(100, 1000);

        // Hard fallback — if the form panel still can't be resolved, don't
        // leave the user staring at a non-responding button. Create a room
        // with default settings so SOMETHING happens, and log the cause.
        if (createRoomPanel == null)
        {
            Debug.LogError("[Lobby] Create Room: 'CreateRoomPanel' not found in the lobby canvas. " +
                           "Falling back to direct CreateRoom with default settings. " +
                           "Re-run Tools → RTS → Multiplayer → Setup Multiplayer Lobby UI to fix the canvas.");
            string fallbackName = "Room " + Random.Range(100, 1000);
            NetworkManagerRTS.Instance?.CreateRoom(
                fallbackName, MapRegistry.DefaultMapId, NetworkManagerRTS.DefaultStartingResources);
            return;
        }

        ShowPanel(createRoomPanel);
    }

    private void OnClickJoinRandom()
    {
        Debug.Log("[OnlineUI] Join Random clicked.");
        if (NetworkManagerRTS.Instance == null) return;
        NetworkManagerRTS.Instance.multiplayerMode = true;
        NetworkManagerRTS.Instance.JoinRandomRoom();     // already auto-Connects + queues
    }

    private void OnClickBrowseRooms()
    {
        Debug.Log("[OnlineUI] Browse Rooms clicked.");

        // Auto-connect so the list actually populates.
        if (NetworkManagerRTS.Instance != null)
        {
            NetworkManagerRTS.Instance.multiplayerMode = true;
#if PHOTON_UNITY_NETWORKING
            if (!Photon.Pun.PhotonNetwork.IsConnected)
                NetworkManagerRTS.Instance.Connect();
#endif
        }

        if (roomListPanel == null && canvasRoot != null)
        {
            roomListPanel = FindChildByName(canvasRoot, "RoomListPanel");
            if (roomListPanel != null)
                Debug.LogWarning("[Lobby] roomListPanel was null — late-resolved from canvasRoot subtree.");
        }

        if (roomListPanel == null)
        {
            Debug.LogError("[Lobby] Browse Rooms: 'RoomListPanel' not found. " +
                           "Re-run Tools → RTS → Multiplayer → Setup Multiplayer Lobby UI.");
            return;
        }

        ShowPanel(roomListPanel);
        RefreshRoomListRows();
    }

    /// <summary>
    /// Canonical mapping from cornerIndex (0..3) → GAMEPLAY-CAMERA-VIEW
    /// quadrant label (NOT raw-world quadrant). Must match the lobby preview's
    /// anchor placement and the baker's <c>SetupMultiplayerMatchMap.CornerPositions</c>.
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

    // Subtree-search by name (inactive-inclusive). Used by the self-heal
    // fallbacks above. The lobby's CreateRoomPanel / RoomListPanel /
    // LobbyPanel are CHILDREN of LobbyCanvas, not root objects, so the
    // root-by-name lookup that ShowOnlineMenu uses doesn't find them.
    private static GameObject FindChildByName(GameObject root, string name)
    {
        if (root == null) return null;
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && all[i].name == name)
                return all[i].gameObject;
        return null;
    }

    private void OnClickBackToMainMenu()
    {
        Debug.Log("[OnlineUI] Back clicked.");
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
        if (NetworkManagerRTS.Instance == null)
        {
            Debug.LogError("[OnlineUI] ERROR: NetworkManagerRTS not found; cannot create room.");
            return;
        }

        string roomName;
        if (createRoomNameInput == null)
        {
            Debug.LogWarning("[OnlineUI] WARNING: Room name input missing; using default room name.");
            roomName = "Room " + Random.Range(100, 1000);
        }
        else
        {
            roomName = createRoomNameInput.text.Trim();
            if (string.IsNullOrEmpty(roomName))
                roomName = "Room " + Random.Range(100, 1000);
        }

        int startingResources = GetSelectedStartingResources();

#if PHOTON_UNITY_NETWORKING
        bool connected = Photon.Pun.PhotonNetwork.IsConnected;
        bool inLobby   = Photon.Pun.PhotonNetwork.InLobby;
#else
        bool connected = false;
        bool inLobby   = false;
#endif
        Debug.Log($"[OnlineUI] CreateRoomSubmit clicked: room='{roomName}', " +
                  $"resources={startingResources}, connected={connected}, inLobby={inLobby}.");

        NetworkManagerRTS.Instance.multiplayerMode = true;
        NetworkManagerRTS.Instance.CreateRoom(
            roomName, MapRegistry.DefaultMapId, startingResources);
        // CreateRoom auto-queues if not connected (pendingCreateRoom + Connect).
        // OnJoinedRoom will fire and HandleRoomJoined switches to LobbyPanel.
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
    private void HandlePlayerPropertiesUpdated(Player p)
    {
        if (p != null && p.CustomProperties != null)
        {
            string colorName = "?";
            if (p.CustomProperties.TryGetValue(NetworkManagerRTS.ColorNamePropKey, out object cn) &&
                cn is string s && !string.IsNullOrEmpty(s))
                colorName = s;

            string slotStr = "-";
            if (NetworkManagerRTS.TryGetPlayerStartSlot(p.ActorNumber, out int sc))
                slotStr = $"{sc} ({(char)('A' + sc)})";

            Debug.Log($"[Lobby] PlayerPropertiesUpdate actor #{p.ActorNumber} " +
                      $"armyColorName={colorName} startSlot={slotStr}");
        }
        RefreshLobby();
    }
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

        Debug.Log($"[Lobby] Local selected color: {name} (RGB {c.r:F2},{c.g:F2},{c.b:F2}).");

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

        if (next >= 0)
        {
            string q = QuadrantOf(next);
            Debug.Log($"[Lobby] Button {(char)('A' + next)} visual={q} -> actual startSlot={next}");
            Debug.Log($"[Lobby] startSlot {next} = gameplay-view {q} corner " +
                      "(world +Z appears at BOTTOM of screen, -Z at TOP).");
        }
        else
        {
            Debug.Log($"[Lobby] Local deselected corner (was {(char)('A' + corner)} " +
                      $"visual={QuadrantOf(corner)}); startSlot=-1.");
        }

        NetworkManagerRTS.Instance?.SetLocalStartSlot(next);

        RefreshLobby();
#endif
    }

    private void OnClickStartMatch()
    {
        Debug.Log("[MatchStart] Host clicked Start Match.");
        if (NetworkMatchCoordinator.Instance == null)
        {
            Debug.LogWarning("[Lobby] Start clicked but no NetworkMatchCoordinator in scene.");
            return;
        }

        // PRE-BROADCAST SAFETY NET — the actual fix.
        //
        // The "Orange lobby pick → Blue gameplay spawn" bug happens when
        // `SetLocalPlayerColor` set the Photon property but, by the time
        // BroadcastMatchStart reads it back, the property either didn't land
        // (Photon write hadn't materialised on the local Player object yet) or
        // was overwritten somewhere. Re-pushing the LOCAL faction-manager's
        // current pick directly to Photon RIGHT BEFORE the broadcast guarantees
        // master reads it. SetLocalPlayerColor → TryFlushPendingColor performs
        // an immediate SetCustomProperties (Photon's LocalPlayer property writes
        // are visible to local reads instantly, before the server even ACKs),
        // so the subsequent BroadcastMatchStart's TryGetPlayerColor gets it.
        ForcePushLocalSelectionsToPhoton();

        // Print exactly what master is about to read so we can prove
        // UI ↔ Photon ↔ payload alignment WITHOUT having to wait for the
        // gameplay scene to load.
        LogPayloadPreview();

        Color local = PlayerFactionManager.Instance != null
            ? PlayerFactionManager.Instance.SelectedColor
            : MultiplayerColors.DefaultPlayer0Color;
        bool willFire = NetworkMatchCoordinator.Instance.RequestMatchStart(local);
        Debug.Log($"[Lobby] Start Match requested — coordinator returned {willFire}.");

        if (willFire)
            ShowLoadingPanel("Loading match — please wait...");
    }

    /// <summary>
    /// Re-push the local player's current lobby selections into Photon Player
    /// Custom Properties immediately before the master broadcasts MatchStart.
    /// Eliminates the race where the lobby UI shows Orange but master reads
    /// stale/missing properties and falls back to DefaultColor(slot).
    /// </summary>
    private void ForcePushLocalSelectionsToPhoton()
    {
#if PHOTON_UNITY_NETWORKING
        if (NetworkManagerRTS.Instance == null) return;
        if (!Photon.Pun.PhotonNetwork.InRoom) return;

        // COLOR — re-push the canonical local pick stored in
        // PlayerFactionManager. SetLocalPlayerColor → TryFlushPendingColor
        // does an immediate SetCustomProperties when we're in a room.
        if (PlayerFactionManager.Instance != null)
        {
            string n = PlayerFactionManager.Instance.SelectedColorName;
            Color  c = PlayerFactionManager.Instance.SelectedColor;
            if (!string.IsNullOrEmpty(n))
            {
                Debug.Log($"[Lobby] Pre-broadcast: re-pushing local color '{n}' to Photon armyColorName.");
                NetworkManagerRTS.Instance.SetLocalPlayerColor(c, n);
            }
            else
            {
                Debug.Log("[Lobby] Pre-broadcast: PlayerFactionManager has no SelectedColorName " +
                          "— master will use DefaultColor(playerSlot).");
            }
        }

        // STARTSLOT — SetLocalStartSlot already writes directly to
        // PhotonNetwork.LocalPlayer.SetCustomProperties without a pending
        // queue, so the lobby corner pick is already in Photon. No re-push
        // needed; ComputeCornerAssignment reads it immediately.
#endif
    }

#if PHOTON_UNITY_NETWORKING
    /// <summary>
    /// Per-actor preview showing UI (PlayerFactionManager) vs the Photon
    /// values BroadcastMatchStart will actually read. ERRORs on mismatch
    /// for the local actor (the only side we can compare locally).
    /// </summary>
    private void LogPayloadPreview()
    {
        if (!Photon.Pun.PhotonNetwork.InRoom) return;

        var room = Photon.Pun.PhotonNetwork.CurrentRoom;
        var sorted = new List<Player>(room.Players.Values);
        sorted.Sort((a, b) => a.ActorNumber.CompareTo(b.ActorNumber));

        Debug.Log("[PayloadPreview] ───── Start Match preview (what master will read) ─────");

        for (int i = 0; i < sorted.Count; i++)
        {
            Player p = sorted[i];

            string localPfmColor = "(remote — UI lives on that client)";
            if (p.IsLocal && PlayerFactionManager.Instance != null)
            {
                localPfmColor = string.IsNullOrEmpty(PlayerFactionManager.Instance.SelectedColorName)
                    ? "(none)" : PlayerFactionManager.Instance.SelectedColorName;
            }

            string photonColorName = "(missing — master will fall back to DefaultColor(playerSlot))";
            if (p.CustomProperties != null &&
                p.CustomProperties.TryGetValue(NetworkManagerRTS.ColorNamePropKey, out object cn) &&
                cn is string s && !string.IsNullOrEmpty(s))
            {
                photonColorName = s;
            }

            string photonStartSlot = "(missing — master will random-fill)";
            if (NetworkManagerRTS.TryGetPlayerStartSlot(p.ActorNumber, out int sc))
                photonStartSlot = $"{sc} ({(char)('A' + sc)})";

            Debug.Log($"[PayloadPreview] Actor #{p.ActorNumber}{(p.IsLocal ? " (local)" : "")}, playerSlot={i}:");
            Debug.Log($"[PayloadPreview]   Local PFM color  = {localPfmColor}");
            Debug.Log($"[PayloadPreview]   Photon armyColor = {photonColorName}     ← master reads THIS into payload");
            Debug.Log($"[PayloadPreview]   Photon startSlot = {photonStartSlot}     ← master reads THIS into payload");

            if (p.IsLocal && localPfmColor != "(none)" && photonColorName != localPfmColor)
            {
                Debug.LogError($"[PayloadPreview] ✗ MISMATCH actor #{p.ActorNumber}: " +
                               $"local PFM color '{localPfmColor}' ≠ Photon armyColorName '{photonColorName}'. " +
                               "The pre-broadcast flush didn't materialise. Master will use the Photon " +
                               "value (or DefaultColor if missing).");
            }
        }

        Debug.Log("[PayloadPreview] ────────────────────────────────────────────────────");
    }
#endif

    private void OnClickLeaveRoom()
    {
        Debug.Log("[OnlineUI] Leave Lobby clicked.");
        if (NetworkManagerRTS.Instance == null)
        {
            Debug.LogError("[OnlineUI] ERROR: NetworkManagerRTS not found; cannot leave room.");
            return;
        }
        NetworkManagerRTS.Instance.LeaveRoom();
    }

    /// <summary>Back button on the CreateRoomPanel — returns to OnlineMenuPanel.</summary>
    private void OnClickCreateRoomBack()
    {
        Debug.Log("[OnlineUI] Back clicked from CreateRoomPanel.");
        if (onlineMenuPanel != null) ShowPanel(onlineMenuPanel);
        else Debug.LogError("[OnlineUI] CreateRoomBack: onlineMenuPanel ref is null.");
    }

    /// <summary>Back button on the RoomListPanel — returns to OnlineMenuPanel.</summary>
    private void OnClickRoomListBack()
    {
        Debug.Log("[OnlineUI] Back clicked from RoomListPanel.");
        if (onlineMenuPanel != null) ShowPanel(onlineMenuPanel);
        else Debug.LogError("[OnlineUI] RoomListBack: onlineMenuPanel ref is null.");
    }

    private void HandleMatchStarted()
    {
        Debug.Log("[Lobby] MatchStart received — hiding lobby canvas.");
        HideLoadingPanel();
        if (canvasRoot != null) canvasRoot.SetActive(false);
    }

    // ------------------------------------------------------------------ //
    // Loading overlay (built lazily inside the lobby canvas)
    // ------------------------------------------------------------------ //

    private GameObject loadingPanelGO;
    private TextMeshProUGUI loadingPanelText;

    private void EnsureLoadingPanel()
    {
        if (loadingPanelGO != null) return;
        if (canvasRoot == null) return;

        loadingPanelGO = new GameObject("LoadingPanel");
        loadingPanelGO.transform.SetParent(canvasRoot.transform, false);

        Image bg = loadingPanelGO.AddComponent<Image>();
        bg.color = new Color(0.04f, 0.05f, 0.06f, 0.88f);
        bg.raycastTarget = true;     // swallow clicks so the user can't double-press Start
        RectTransform rt = bg.rectTransform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

        GameObject textGO = new GameObject("Text");
        textGO.transform.SetParent(loadingPanelGO.transform, false);
        loadingPanelText = textGO.AddComponent<TextMeshProUGUI>();
        loadingPanelText.text      = "Loading match — please wait...";
        loadingPanelText.fontSize  = 36;
        loadingPanelText.color     = new Color(0.92f, 0.92f, 0.84f, 1f);
        loadingPanelText.alignment = TextAlignmentOptions.Center;
        loadingPanelText.fontStyle = FontStyles.Bold;
        RectTransform trt = loadingPanelText.rectTransform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;

        loadingPanelGO.transform.SetAsLastSibling();     // render on top of the lobby
        loadingPanelGO.SetActive(false);
    }

    private void ShowLoadingPanel(string msg)
    {
        EnsureLoadingPanel();
        if (loadingPanelGO == null) return;
        if (loadingPanelText != null && !string.IsNullOrEmpty(msg)) loadingPanelText.text = msg;
        loadingPanelGO.SetActive(true);
    }

    private void HideLoadingPanel()
    {
        if (loadingPanelGO != null) loadingPanelGO.SetActive(false);
    }
}
