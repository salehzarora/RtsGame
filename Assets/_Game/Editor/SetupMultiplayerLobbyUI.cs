using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Builds (or rebuilds) the multiplayer lobby canvas with all four panels:
/// OnlineMenuPanel, CreateRoomPanel, RoomListPanel, LobbyPanel. Wires the
/// <see cref="MultiplayerLobbyUI"/> component to every reference it needs.
///
/// Menu: Tools → RTS → Multiplayer → Setup Multiplayer Lobby UI
///
/// Idempotent — destroys any previous <c>LobbyCanvas</c> and rebuilds.
/// </summary>
public static class SetupMultiplayerLobbyUI
{
    // ------------------------------------------------------------------ //
    // Palette + constants
    // ------------------------------------------------------------------ //

    private const string CanvasName = "LobbyCanvas";

    private static readonly Color BgDim     = new Color(0.04f, 0.05f, 0.06f, 0.95f);
    private static readonly Color PanelBg   = new Color(0.10f, 0.12f, 0.14f, 0.92f);
    private static readonly Color PanelBdr  = new Color(0.40f, 0.46f, 0.30f, 1.00f);
    private static readonly Color TitleAmb  = new Color(0.78f, 0.88f, 0.38f, 1.00f);
    private static readonly Color BodyText  = new Color(0.92f, 0.92f, 0.84f, 1.00f);
    private static readonly Color DimText   = new Color(0.60f, 0.60f, 0.55f, 1.00f);

    private static readonly Color BtnPrimary   = new Color(0.18f, 0.45f, 0.82f, 1f);
    private static readonly Color BtnSuccess   = new Color(0.30f, 0.65f, 0.30f, 1f);
    private static readonly Color BtnDanger    = new Color(0.55f, 0.20f, 0.15f, 1f);
    private static readonly Color BtnNeutral   = new Color(0.40f, 0.40f, 0.45f, 1f);
    private static readonly Color BtnHighlight = new Color(0.82f, 0.60f, 0.08f, 1f);

    // Lobby color picker — must match MultiplayerLobbyUI.lobbyColorNames order.
    private static readonly (string name, Color color)[] LobbyColors = {
        ("Blue",   new Color(0.20f, 0.55f, 1.00f)),
        ("Red",    new Color(0.92f, 0.20f, 0.20f)),
        ("Green",  new Color(0.25f, 0.80f, 0.32f)),
        ("Yellow", new Color(1.00f, 0.85f, 0.18f)),
        ("Orange", new Color(1.00f, 0.55f, 0.10f)),
        ("Purple", new Color(0.65f, 0.30f, 0.85f)),
    };

    private const int RoomListMaxRows = 8;

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Multiplayer/Setup Multiplayer Lobby UI")]
    public static void Run()
    {
        Debug.Log("[SetupLobby] ── Building lobby canvas ──");

        EnsureEventSystem();

        Canvas canvas = RebuildCanvas();

        // Full-screen darkener so the lobby reads as a discrete screen.
        RectTransform dim = CreatePanel(canvas.transform, "ScreenDim", BgDim);
        dim.anchorMin = Vector2.zero; dim.anchorMax = Vector2.one;
        dim.offsetMin = Vector2.zero; dim.offsetMax = Vector2.zero;
        dim.GetComponent<Image>().raycastTarget = false;

        // ----- Build the four panels --------------------------------------
        OnlineMenuRefs    online   = BuildOnlineMenu(canvas.transform);
        CreateRoomRefs    create   = BuildCreateRoomPanel(canvas.transform);
        RoomListRefs      list     = BuildRoomListPanel(canvas.transform);
        LobbyPanelRefs    lobby    = BuildLobbyPanel(canvas.transform);

        // Default state — only the online menu is visible.
        create.root.gameObject.SetActive(false);
        list.root.gameObject.SetActive(false);
        lobby.root.gameObject.SetActive(false);

        // ----- MultiplayerLobbyUI component --------------------------------
        // Phase 7 fix: put the component on GameManager, NOT on the canvas.
        // The canvas starts inactive (so the lobby is hidden at launch);
        // Unity doesn't fire Awake on components of inactive GameObjects,
        // so attaching MultiplayerLobbyUI to the canvas would leave
        // Instance = null forever. GameManager is always active.
        GameObject gm = GameObject.Find("GameManager");
        if (gm == null)
        {
            gm = new GameObject("GameManager");
            Undo.RegisterCreatedObjectUndo(gm, "Create GameManager");
            Debug.LogWarning("[SetupLobby] No GameManager in scene — created one.");
        }
        MultiplayerLobbyUI ui = gm.GetComponent<MultiplayerLobbyUI>();
        if (ui == null) ui = gm.AddComponent<MultiplayerLobbyUI>();

        // If a previous build left a MultiplayerLobbyUI on the canvas (the
        // old broken layout), strip it so we don't end up with duplicates.
        MultiplayerLobbyUI strayOnCanvas = canvas.gameObject.GetComponent<MultiplayerLobbyUI>();
        if (strayOnCanvas != null && strayOnCanvas != ui)
        {
            Undo.DestroyObjectImmediate(strayOnCanvas);
            Debug.Log("[SetupLobby] Removed stray MultiplayerLobbyUI on canvas (legacy layout).");
        }

        ui.canvasRoot     = canvas.gameObject;
        ui.mainMenuCanvas = GameObject.Find("MainMenuCanvas");

        ui.onlineMenuPanel = online.root.gameObject;
        ui.createRoomPanel = create.root.gameObject;
        ui.roomListPanel   = list.root.gameObject;
        ui.lobbyPanel      = lobby.root.gameObject;

        ui.onlineConnectButton    = online.connectBtn;
        ui.onlineCreateRoomButton = online.createBtn;
        ui.onlineJoinRandomButton = online.joinRandomBtn;
        ui.onlineBrowseRoomsButton = online.browseBtn;
        ui.onlineBackButton       = online.backBtn;
        ui.onlineStatusLabel      = online.statusLabel;

        ui.createRoomNameInput     = create.nameInput;
        ui.createRoomMapLabel      = create.mapLabel;
        ui.createRoomConfirmButton = create.createBtn;
        ui.createRoomBackButton    = create.backBtn;
        ui.createRoomStartingResourcesButton = create.startingResourcesBtn;
        ui.createRoomStartingResourcesLabel  = create.startingResourcesLabel;

        ui.roomListRefreshButton = list.refreshBtn;
        ui.roomListBackButton    = list.backBtn;
        ui.roomListRowButtons    = list.rowButtons;
        ui.roomListRowLabels     = list.rowLabels;

        ui.lobbyRoomNameLabel       = lobby.roomNameLabel;
        ui.lobbyMapLabel            = lobby.mapLabel;
        ui.lobbyStartingResourcesLabel = lobby.startingResourcesLabel;
        ui.lobbyPlayerLabels        = lobby.playerLabels;
        ui.lobbyPlayerSwatches      = lobby.playerSwatches;
        ui.mapPreviewPanel          = lobby.mapPreviewPanel;
        ui.cornerButtons            = lobby.cornerButtons;
        ui.cornerLabels             = lobby.cornerLabels;
        ui.lobbyColorButtons        = lobby.colorBtns;
        ui.lobbyColorValues         = lobby.colorValues;
        ui.lobbyColorNames          = lobby.colorNames;
        ui.lobbyStartMatchButton    = lobby.startMatchBtn;
        ui.lobbyLeaveRoomButton     = lobby.leaveBtn;
        ui.lobbyStatusLabel         = lobby.statusLabel;
        EditorUtility.SetDirty(ui);

        canvas.gameObject.SetActive(false);     // hidden at scene start

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[SetupLobby] ✓ Done. Click the Main Menu's 'Online' button to reveal the lobby.");
    }

    // ================================================================== //
    // OnlineMenuPanel
    // ================================================================== //

    private class OnlineMenuRefs
    {
        public RectTransform root;
        public Button connectBtn, createBtn, joinRandomBtn, browseBtn, backBtn;
        public TextMeshProUGUI statusLabel;
    }

    private static OnlineMenuRefs BuildOnlineMenu(Transform parent)
    {
        OnlineMenuRefs r = new OnlineMenuRefs();
        r.root = CreateBorderedPanel(parent, "OnlineMenuPanel", w: 600f, h: 500f);

        TextMeshProUGUI title = CreateLabel(r.root, "Title", "ONLINE", topY: 30f, height: 40f,
            fontSize: 32, color: TitleAmb, bold: true);
        _ = title;

        float y = 100f;
        r.connectBtn    = MakeBtn(r.root, "ConnectButton",     "Connect",          BtnPrimary,   y, 360f, 50f); y += 60f;
        r.createBtn     = MakeBtn(r.root, "CreateRoomButton",  "Create Room",      BtnSuccess,   y, 360f, 50f); y += 60f;
        r.joinRandomBtn = MakeBtn(r.root, "JoinRandomButton",  "Join Random Room", BtnHighlight, y, 360f, 50f); y += 60f;
        r.browseBtn     = MakeBtn(r.root, "BrowseRoomsButton", "Browse Rooms",     BtnNeutral,   y, 360f, 50f); y += 70f;

        r.statusLabel = CreateLabel(r.root, "StatusLabel", "Status: Disconnected",
            topY: y, height: 24f, fontSize: 16, color: DimText, bold: false);

        r.backBtn = MakeBtnBottom(r.root, "BackButton", "Back", BtnDanger, 360f, 50f);
        return r;
    }

    // ================================================================== //
    // CreateRoomPanel
    // ================================================================== //

    private class CreateRoomRefs
    {
        public RectTransform root;
        public TMP_InputField nameInput;
        public TextMeshProUGUI mapLabel;
        public Button createBtn, backBtn;
        public Button startingResourcesBtn;
        public TextMeshProUGUI startingResourcesLabel;
    }

    private static CreateRoomRefs BuildCreateRoomPanel(Transform parent)
    {
        CreateRoomRefs r = new CreateRoomRefs();
        // Phase 8: panel taller to fit the starting-resources cycle button.
        r.root = CreateBorderedPanel(parent, "CreateRoomPanel", w: 600f, h: 480f);

        _ = CreateLabel(r.root, "Title", "CREATE ROOM", topY: 30f, height: 40f,
            fontSize: 26, color: TitleAmb, bold: true);

        _ = CreateLabel(r.root, "NameInputLabel", "Room name:",
            topY: 90f, height: 24f, fontSize: 16, color: BodyText, bold: false);
        r.nameInput = CreateInputField(r.root, "RoomNameInput", placeholder: "My Room",
            topY: 118f, height: 40f);

        r.mapLabel = CreateLabel(r.root, "MapLabel",
            "Map: " + MapRegistry.DisplayNameOrId(MapRegistry.DefaultMapId),
            topY: 180f, height: 24f, fontSize: 18, color: BodyText, bold: false);

        // Starting-resources cycle button. The label text is set by
        // MultiplayerLobbyUI.RefreshStartingResourcesLabel at runtime; the
        // initial "Starting Resources: 10000" is just the editor preview.
        r.startingResourcesBtn = MakeBtn(r.root, "StartingResourcesButton",
            "Starting Resources: 10000", BtnHighlight, topY: 230f, w: 360f, h: 44f);
        r.startingResourcesLabel = r.startingResourcesBtn.GetComponentInChildren<TextMeshProUGUI>(true);

        r.createBtn = MakeBtn(r.root, "CreateButton", "Create",         BtnSuccess, 320f, 280f, 50f);

        r.backBtn = MakeBtnBottom(r.root, "BackButton", "Back", BtnDanger, 280f, 50f);
        return r;
    }

    // ================================================================== //
    // RoomListPanel
    // ================================================================== //

    private class RoomListRefs
    {
        public RectTransform root;
        public Button refreshBtn, backBtn;
        public Button[] rowButtons;
        public TextMeshProUGUI[] rowLabels;
    }

    private static RoomListRefs BuildRoomListPanel(Transform parent)
    {
        RoomListRefs r = new RoomListRefs();
        r.root = CreateBorderedPanel(parent, "RoomListPanel", w: 800f, h: 600f);

        _ = CreateLabel(r.root, "Title", "AVAILABLE ROOMS", topY: 25f, height: 36f,
            fontSize: 26, color: TitleAmb, bold: true);

        r.refreshBtn = MakeBtn(r.root, "RefreshButton", "Refresh", BtnPrimary, 75f, 200f, 38f);

        r.rowButtons = new Button[RoomListMaxRows];
        r.rowLabels  = new TextMeshProUGUI[RoomListMaxRows];

        float rowStartTop = 130f;
        float rowH        = 44f;
        float rowGap      = 4f;
        for (int i = 0; i < RoomListMaxRows; i++)
        {
            float top = rowStartTop + i * (rowH + rowGap);
            Button btn = MakeBtn(r.root, $"RoomRow_{i}", "(empty)", BtnNeutral, top, 720f, rowH);
            btn.gameObject.SetActive(false);
            r.rowButtons[i] = btn;
            r.rowLabels[i]  = btn.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        r.backBtn = MakeBtnBottom(r.root, "BackButton", "Back", BtnDanger, 280f, 50f);
        return r;
    }

    // ================================================================== //
    // LobbyPanel
    // ================================================================== //

    private class LobbyPanelRefs
    {
        public RectTransform root;
        public TextMeshProUGUI roomNameLabel, mapLabel, startingResourcesLabel, statusLabel;
        public TextMeshProUGUI[] playerLabels;     // 4 rows
        public Image[]           playerSwatches;   // 4 rows
        public GameObject        mapPreviewPanel;
        public Button[]          cornerButtons;    // 4 (A,B,C,D)
        public TextMeshProUGUI[] cornerLabels;     // 4
        public Button[]  colorBtns;
        public string[]  colorNames;
        public Color[]   colorValues;
        public Button startMatchBtn, leaveBtn;
    }

    private static LobbyPanelRefs BuildLobbyPanel(Transform parent)
    {
        LobbyPanelRefs r = new LobbyPanelRefs();
        r.root = CreateBorderedPanel(parent, "LobbyPanel", w: 980f, h: 760f);

        _ = CreateLabel(r.root, "Title", "LOBBY", topY: 22f, height: 36f,
            fontSize: 28, color: TitleAmb, bold: true);

        r.roomNameLabel = CreateLabel(r.root, "RoomName", "Room: —",
            topY: 60f, height: 22f, fontSize: 16, color: BodyText, bold: false);
        r.mapLabel      = CreateLabel(r.root, "MapName", "Map: —",
            topY: 86f, height: 22f, fontSize: 16, color: BodyText, bold: false);
        r.startingResourcesLabel = CreateLabel(r.root, "StartingResources", "Starting Resources: —",
            topY: 112f, height: 22f, fontSize: 16, color: BodyText, bold: false);

        // ----- Four player rows (left column) ---------------------------- //
        _ = CreateLeftLabel(r.root, "PlayersHeader", "PLAYERS (1–4)", x: 40f, topY: 150f,
            width: 380f, fontSize: 16, color: TitleAmb);

        r.playerLabels   = new TextMeshProUGUI[4];
        r.playerSwatches = new Image[4];
        float rowTop = 182f, rowStep = 42f;
        for (int i = 0; i < 4; i++)
        {
            float top = rowTop + i * rowStep;
            r.playerSwatches[i] = CreateSwatch(r.root, $"Player{i}Swatch",
                topLeft: new Vector2(40f, top + 2f), size: 30f);
            r.playerLabels[i] = CreateLeftLabel(r.root, $"Player{i}Label",
                $"Player {i + 1}: <empty>", x: 82f, topY: top, width: 380f,
                fontSize: 17, color: BodyText);
        }

        // ----- Colour picker (left column, below rows) ------------------- //
        _ = CreateLeftLabel(r.root, "PickColorLabel", "Your color:", x: 40f, topY: 372f,
            width: 200f, fontSize: 16, color: BodyText);

        r.colorBtns   = new Button[LobbyColors.Length];
        r.colorNames  = new string[LobbyColors.Length];
        r.colorValues = new Color[LobbyColors.Length];
        float swatchSize = 50f, gap = 10f, colorStartX = 40f, colorTop = 402f;
        for (int i = 0; i < LobbyColors.Length; i++)
        {
            Button b = MakeColorSwatchButton(
                r.root, $"ColorBtn_{LobbyColors[i].name}", LobbyColors[i].color,
                anchoredPos: Vector2.zero, size: swatchSize);
            // Re-anchor to the panel's top-left for the left column layout.
            RectTransform rt = b.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot     = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(colorStartX + i * (swatchSize + gap), -colorTop);
            r.colorBtns[i]   = b;
            r.colorNames[i]  = LobbyColors[i].name;
            r.colorValues[i] = LobbyColors[i].color;
        }

        // ----- Map preview + corner picker (right column) ---------------- //
        BuildMapPreview(r);

        // ----- Status + Start + Leave (bottom) --------------------------- //
        r.statusLabel = CreateLabel(r.root, "StatusLabel", "Players: 0 / 4",
            topY: 600f, height: 30f, fontSize: 20, color: TitleAmb, bold: true);
        r.statusLabel.alignment = TextAlignmentOptions.Center;

        r.startMatchBtn = MakeBtn(r.root, "StartMatchButton", "START MATCH",
            BtnSuccess, topY: 640f, w: 320f, h: 56f);

        r.leaveBtn = MakeBtnBottom(r.root, "LeaveButton", "Leave Room", BtnDanger, 280f, 46f);

        return r;
    }

    /// <summary>
    /// Builds the "Choose Start Position" preview box (right column) with four
    /// clickable corner dots laid out A=top-left, B=top-right, C=bottom-left,
    /// D=bottom-right. Buttons use Transition.None so MultiplayerLobbyUI can
    /// drive their colour directly (available / mine / taken).
    /// </summary>
    private static void BuildMapPreview(LobbyPanelRefs r)
    {
        RectTransform preview = CreatePanel(r.root, "MapPreviewPanel", new Color(0.07f, 0.10f, 0.13f, 1f));
        preview.anchorMin = new Vector2(1f, 1f);
        preview.anchorMax = new Vector2(1f, 1f);
        preview.pivot     = new Vector2(1f, 1f);
        preview.anchoredPosition = new Vector2(-30f, -150f);
        preview.sizeDelta = new Vector2(400f, 360f);
        r.mapPreviewPanel = preview.gameObject;

        _ = CreateLabel(preview, "PreviewTitle", "Choose Start Position",
            topY: 10f, height: 26f, fontSize: 18, color: TitleAmb, bold: true);

        r.cornerButtons = new Button[4];
        r.cornerLabels  = new TextMeshProUGUI[4];

        // anchor (within preview box), offset from that anchor.
        Vector2[] anchors = {
            new Vector2(0f, 1f),   // A top-left
            new Vector2(1f, 1f),   // B top-right
            new Vector2(0f, 0f),   // C bottom-left
            new Vector2(1f, 0f),   // D bottom-right
        };
        Vector2[] offsets = {
            new Vector2( 60f, -80f),
            new Vector2(-60f, -80f),
            new Vector2( 60f,  60f),
            new Vector2(-60f,  60f),
        };
        for (int i = 0; i < 4; i++)
        {
            Button b = MakeCornerButton(preview, $"CornerBtn_{(char)('A' + i)}",
                ((char)('A' + i)).ToString(), anchors[i], offsets[i], size: 80f,
                out TextMeshProUGUI lbl);
            r.cornerButtons[i] = b;
            r.cornerLabels[i]  = lbl;
        }
    }

    // ================================================================== //
    // Canvas + EventSystem
    // ================================================================== //

    private static Canvas RebuildCanvas()
    {
        // BUG FIX: this builder saves the canvas INACTIVE at the end of Run, so
        // GameObject.Find (which only finds ACTIVE objects) can't see the
        // previous canvas on a re-run. We'd then leave the inactive one in
        // place and append a new one — and that compounds every re-run until
        // the Hierarchy is full of duplicate LobbyCanvas objects. Use a
        // FindObjectsByType pass that INCLUDES inactive objects so the
        // re-run is actually idempotent. Root-only filter (parent == null)
        // avoids accidentally matching a same-named child of a prefab.
        Transform[] all = Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int removed = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null) continue;
            if (all[i].parent != null) continue;
            if (all[i].name != CanvasName) continue;
            Undo.DestroyObjectImmediate(all[i].gameObject);
            removed++;
        }
        if (removed > 0)
            Debug.Log($"[SetupLobby] Removed {removed} existing LobbyCanvas object(s) — rebuilding clean.");

        GameObject go = new GameObject(CanvasName);
        Undo.RegisterCreatedObjectUndo(go, "Create LobbyCanvas");

        Canvas canvas       = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1020;     // above HUD (999) + main menu (1000); below debug (1100)
        canvas.pixelPerfect = false;

        CanvasScaler scaler        = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight  = 0.5f;

        go.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    private static void EnsureEventSystem()
    {
        if (Object.FindAnyObjectByType<EventSystem>() != null) return;
        GameObject esGO = new GameObject("EventSystem");
        esGO.AddComponent<EventSystem>();
        esGO.AddComponent<StandaloneInputModule>();
        Undo.RegisterCreatedObjectUndo(esGO, "Create EventSystem");
    }

    // ================================================================== //
    // UI factories
    // ================================================================== //

    private static RectTransform CreatePanel(Transform parent, string name, Color color)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt = go.AddComponent<RectTransform>();
        Image img = go.AddComponent<Image>();
        img.color = color;
        return rt;
    }

    private static RectTransform CreateBorderedPanel(Transform parent, string name, float w, float h)
    {
        // Outer = border. Inner = bg. Centred on screen.
        RectTransform outer = CreatePanel(parent, name, PanelBdr);
        outer.anchorMin = new Vector2(0.5f, 0.5f);
        outer.anchorMax = new Vector2(0.5f, 0.5f);
        outer.pivot     = new Vector2(0.5f, 0.5f);
        outer.anchoredPosition = Vector2.zero;
        outer.sizeDelta = new Vector2(w, h);

        RectTransform inner = CreatePanel(outer, name + "_Inner", PanelBg);
        inner.anchorMin = Vector2.zero; inner.anchorMax = Vector2.one;
        inner.offsetMin = new Vector2( 2f,  2f);
        inner.offsetMax = new Vector2(-2f, -2f);
        inner.GetComponent<Image>().raycastTarget = false;
        return outer;
    }

    private static TextMeshProUGUI CreateLabel(RectTransform parent, string name, string text,
                                               float topY, float height, int fontSize,
                                               Color color, bool bold)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot     = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -topY);
        rt.sizeDelta = new Vector2(parent.rect.width - 40f, height);

        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text          = text;
        tmp.fontSize      = fontSize;
        tmp.color         = color;
        tmp.alignment     = TextAlignmentOptions.Center;
        tmp.fontStyle     = bold ? FontStyles.Bold : FontStyles.Normal;
        tmp.raycastTarget = false;
        return tmp;
    }

    private static Button MakeBtn(RectTransform parent, string name, string label,
                                  Color color, float topY, float w, float h)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot     = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -topY);
        rt.sizeDelta = new Vector2(w, h);

        Image bg = go.AddComponent<Image>();
        bg.color = color;

        Button btn = go.AddComponent<Button>();
        ColorBlock cb = ColorBlock.defaultColorBlock;
        cb.normalColor      = color;
        cb.highlightedColor = color * 1.20f;
        cb.pressedColor     = color * 0.75f;
        cb.disabledColor    = color * 0.40f;
        cb.fadeDuration     = 0.08f;
        btn.colors = cb;

        GameObject textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        RectTransform trt = textGO.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        TextMeshProUGUI t = textGO.AddComponent<TextMeshProUGUI>();
        t.text = label; t.fontSize = 18; t.color = Color.white;
        t.alignment = TextAlignmentOptions.Center; t.fontStyle = FontStyles.Bold;
        t.raycastTarget = false;

        return btn;
    }

    /// <summary>Anchored to BOTTOM of its parent panel — used for Back buttons.</summary>
    private static Button MakeBtnBottom(RectTransform parent, string name, string label,
                                        Color color, float w, float h)
    {
        Button b = MakeBtn(parent, name, label, color, topY: 0f, w: w, h: h);
        RectTransform rt = b.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0f);
        rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot     = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, 20f);
        return b;
    }

    private static Button MakeColorSwatchButton(RectTransform parent, string name, Color color,
                                                Vector2 anchoredPos, float size)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot     = new Vector2(0.5f, 1f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = new Vector2(size, size);

        Image bg = go.AddComponent<Image>();
        bg.color = color;

        Button btn = go.AddComponent<Button>();
        ColorBlock cb = ColorBlock.defaultColorBlock;
        cb.normalColor      = color;
        cb.highlightedColor = color * 1.25f;
        cb.pressedColor     = color * 0.70f;
        cb.fadeDuration     = 0.08f;
        btn.colors = cb;
        return btn;
    }

    private static Image CreateSwatch(RectTransform parent, string name,
                                      Vector2 topLeft, float size)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot     = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
        rt.sizeDelta = new Vector2(size, size);

        Image img = go.AddComponent<Image>();
        img.color = new Color(0.2f, 0.2f, 0.2f, 1f);
        img.raycastTarget = false;
        return img;
    }

    private static TMP_InputField CreateInputField(RectTransform parent, string name,
                                                   string placeholder, float topY, float height)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot     = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -topY);
        rt.sizeDelta = new Vector2(480f, height);

        Image bg = go.AddComponent<Image>();
        bg.color = new Color(0.18f, 0.20f, 0.22f, 1f);

        TMP_InputField input = go.AddComponent<TMP_InputField>();

        // Text component (child)
        GameObject textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        RectTransform trt = textGO.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(10f, 6f); trt.offsetMax = new Vector2(-10f, -6f);
        TextMeshProUGUI text = textGO.AddComponent<TextMeshProUGUI>();
        text.color     = BodyText;
        text.fontSize  = 18;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.raycastTarget = false;

        // Placeholder (child)
        GameObject phGO = new GameObject("Placeholder");
        phGO.transform.SetParent(go.transform, false);
        RectTransform prt = phGO.AddComponent<RectTransform>();
        prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
        prt.offsetMin = new Vector2(10f, 6f); prt.offsetMax = new Vector2(-10f, -6f);
        TextMeshProUGUI ph = phGO.AddComponent<TextMeshProUGUI>();
        ph.text       = placeholder;
        ph.fontSize   = 18;
        ph.color      = DimText;
        ph.fontStyle  = FontStyles.Italic;
        ph.alignment  = TextAlignmentOptions.MidlineLeft;
        ph.raycastTarget = false;

        input.textComponent  = text;
        input.placeholder    = ph;
        input.lineType       = TMP_InputField.LineType.SingleLine;

        return input;
    }

    // ---- Left-aligned label (player rows / headers) ------------------- //
    private static TextMeshProUGUI CreateLeftLabel(RectTransform parent, string name, string text,
                                                   float x, float topY, float width,
                                                   int fontSize, Color color)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot     = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -topY);
        rt.sizeDelta = new Vector2(width, 34f);

        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text          = text;
        tmp.fontSize      = fontSize;
        tmp.color         = color;
        tmp.alignment     = TextAlignmentOptions.MidlineLeft;
        tmp.raycastTarget = false;
        return tmp;
    }

    // ---- Corner picker dot (Transition.None; runtime drives colour) --- //
    private static Button MakeCornerButton(RectTransform parent, string name, string label,
                                           Vector2 anchor, Vector2 anchoredPos, float size,
                                           out TextMeshProUGUI labelTmp)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = new Vector2(size, size);

        Image bg = go.AddComponent<Image>();
        bg.color = new Color(0.25f, 0.55f, 0.30f, 1f);   // "available" green

        Button btn = go.AddComponent<Button>();
        btn.transition = Selectable.Transition.None;     // runtime sets image.color directly

        GameObject t = new GameObject("Text");
        t.transform.SetParent(go.transform, false);
        RectTransform trt = t.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        labelTmp = t.AddComponent<TextMeshProUGUI>();
        labelTmp.text          = label;
        labelTmp.fontSize      = 22;
        labelTmp.color         = Color.white;
        labelTmp.alignment     = TextAlignmentOptions.Center;
        labelTmp.fontStyle     = FontStyles.Bold;
        labelTmp.raycastTarget = false;
        return btn;
    }

    // ================================================================== //
    // Validation
    // ================================================================== //

    [MenuItem("Tools/RTS/UI/Validate 4 Player Lobby UI")]
    public static void Validate()
    {
        Debug.Log("[SetupLobby] ───────── VALIDATE 4 PLAYER LOBBY UI ─────────");
        bool ok = true;

        GameObject gm = GameObject.Find("GameManager");
        MultiplayerLobbyUI ui = gm != null ? gm.GetComponent<MultiplayerLobbyUI>() : null;
        if (ui == null)
            ui = Object.FindFirstObjectByType<MultiplayerLobbyUI>(FindObjectsInactive.Include);

        if (ui == null)
        {
            Debug.LogError("[SetupLobby] ✗ No MultiplayerLobbyUI found — run Setup Multiplayer Lobby UI.");
            Debug.Log("[SetupLobby] ──────────────────────────────────────────────");
            return;
        }
        Debug.Log($"[SetupLobby] ✓ Found MultiplayerLobbyUI on '{ui.gameObject.name}'.");

        ok &= Check("lobbyPanel assigned", ui.lobbyPanel != null);
        ok &= Check("canvasRoot assigned", ui.canvasRoot != null);

        int labels   = ui.lobbyPlayerLabels   != null ? CountNonNull(ui.lobbyPlayerLabels)   : 0;
        int swatches = ui.lobbyPlayerSwatches != null ? CountNonNull(ui.lobbyPlayerSwatches) : 0;
        ok &= Check($"4 player row labels ({labels}/4)",     labels   == 4);
        ok &= Check($"4 player row swatches ({swatches}/4)", swatches == 4);

        ok &= Check("map preview panel assigned", ui.mapPreviewPanel != null);

        int corners = ui.cornerButtons != null ? CountNonNull(ui.cornerButtons) : 0;
        ok &= Check($"4 corner buttons A/B/C/D ({corners}/4)", corners == 4);
        Debug.Log("[SetupLobby]   (corner buttons are wired to startSlot selection at runtime in " +
                  "MultiplayerLobbyUI.WireButtons.)");

        ok &= Check("color buttons assigned",      ui.lobbyColorButtons != null && ui.lobbyColorButtons.Length > 0);
        ok &= Check("start match button assigned", ui.lobbyStartMatchButton != null);
        ok &= Check("leave room button assigned",  ui.lobbyLeaveRoomButton  != null);
        ok &= Check("status label assigned",       ui.lobbyStatusLabel      != null);

        Debug.Log(ok
            ? "[SetupLobby] ✓ Validation PASSED — lobby has 4 rows + map preview + A/B/C/D picker, all wired."
            : "[SetupLobby] ✗ Validation FAILED — re-run Tools → RTS → Multiplayer → Setup Multiplayer Lobby UI.");
        Debug.Log("[SetupLobby] ──────────────────────────────────────────────");
    }

    private static bool Check(string label, bool pass)
    {
        Debug.Log($"[SetupLobby]   {(pass ? "✓" : "✗")} {label}");
        return pass;
    }

    private static int CountNonNull<T>(T[] arr) where T : Object
    {
        int n = 0;
        for (int i = 0; i < arr.Length; i++) if (arr[i] != null) n++;
        return n;
    }
}
