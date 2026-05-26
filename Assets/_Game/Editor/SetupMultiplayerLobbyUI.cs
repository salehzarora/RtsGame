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

        ui.roomListRefreshButton = list.refreshBtn;
        ui.roomListBackButton    = list.backBtn;
        ui.roomListRowButtons    = list.rowButtons;
        ui.roomListRowLabels     = list.rowLabels;

        ui.lobbyRoomNameLabel       = lobby.roomNameLabel;
        ui.lobbyMapLabel            = lobby.mapLabel;
        ui.lobbyPlayer0Label        = lobby.p0Label;
        ui.lobbyPlayer1Label        = lobby.p1Label;
        ui.lobbyPlayer0ColorSwatch  = lobby.p0Swatch;
        ui.lobbyPlayer1ColorSwatch  = lobby.p1Swatch;
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
    }

    private static CreateRoomRefs BuildCreateRoomPanel(Transform parent)
    {
        CreateRoomRefs r = new CreateRoomRefs();
        r.root = CreateBorderedPanel(parent, "CreateRoomPanel", w: 600f, h: 400f);

        _ = CreateLabel(r.root, "Title", "CREATE ROOM", topY: 30f, height: 40f,
            fontSize: 26, color: TitleAmb, bold: true);

        _ = CreateLabel(r.root, "NameInputLabel", "Room name:",
            topY: 100f, height: 24f, fontSize: 16, color: BodyText, bold: false);
        r.nameInput = CreateInputField(r.root, "RoomNameInput", placeholder: "My Room",
            topY: 128f, height: 40f);

        r.mapLabel = CreateLabel(r.root, "MapLabel",
            "Map: " + MapRegistry.DisplayNameOrId(MapRegistry.DefaultMapId),
            topY: 190f, height: 24f, fontSize: 18, color: BodyText, bold: false);

        r.createBtn = MakeBtn(r.root, "CreateButton", "Create",         BtnSuccess, 240f, 280f, 50f);

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
        public TextMeshProUGUI roomNameLabel, mapLabel;
        public TextMeshProUGUI p0Label, p1Label, statusLabel;
        public Image p0Swatch, p1Swatch;
        public Button[]  colorBtns;
        public string[]  colorNames;
        public Color[]   colorValues;
        public Button startMatchBtn, leaveBtn;
    }

    private static LobbyPanelRefs BuildLobbyPanel(Transform parent)
    {
        LobbyPanelRefs r = new LobbyPanelRefs();
        r.root = CreateBorderedPanel(parent, "LobbyPanel", w: 800f, h: 620f);

        _ = CreateLabel(r.root, "Title", "LOBBY", topY: 25f, height: 36f,
            fontSize: 28, color: TitleAmb, bold: true);

        r.roomNameLabel = CreateLabel(r.root, "RoomName", "Room: —",
            topY: 75f, height: 22f, fontSize: 16, color: BodyText, bold: false);
        r.mapLabel      = CreateLabel(r.root, "MapName", "Map: —",
            topY: 100f, height: 22f, fontSize: 16, color: BodyText, bold: false);

        // Player slot 0
        r.p0Swatch = CreateSwatch(r.root, "Player0Swatch", topLeft: new Vector2(60f, 145f), size: 36f);
        r.p0Label  = CreateLabel(r.root, "Player0Label",
            "Player 0: <empty>", topY: 145f, height: 36f, fontSize: 18, color: BodyText, bold: false);
        r.p0Label.rectTransform.anchorMin = new Vector2(0f, 1f);
        r.p0Label.rectTransform.anchorMax = new Vector2(0f, 1f);
        r.p0Label.rectTransform.pivot     = new Vector2(0f, 1f);
        r.p0Label.rectTransform.anchoredPosition = new Vector2(110f, -145f);
        r.p0Label.rectTransform.sizeDelta = new Vector2(640f, 36f);
        r.p0Label.alignment = TextAlignmentOptions.MidlineLeft;

        // Player slot 1
        r.p1Swatch = CreateSwatch(r.root, "Player1Swatch", topLeft: new Vector2(60f, 195f), size: 36f);
        r.p1Label  = CreateLabel(r.root, "Player1Label",
            "Player 1: <empty>", topY: 195f, height: 36f, fontSize: 18, color: BodyText, bold: false);
        r.p1Label.rectTransform.anchorMin = new Vector2(0f, 1f);
        r.p1Label.rectTransform.anchorMax = new Vector2(0f, 1f);
        r.p1Label.rectTransform.pivot     = new Vector2(0f, 1f);
        r.p1Label.rectTransform.anchoredPosition = new Vector2(110f, -195f);
        r.p1Label.rectTransform.sizeDelta = new Vector2(640f, 36f);
        r.p1Label.alignment = TextAlignmentOptions.MidlineLeft;

        // Color picker
        _ = CreateLabel(r.root, "PickColorLabel", "Your color:",
            topY: 270f, height: 22f, fontSize: 16, color: BodyText, bold: false);

        r.colorBtns   = new Button[LobbyColors.Length];
        r.colorNames  = new string[LobbyColors.Length];
        r.colorValues = new Color[LobbyColors.Length];
        float swatchSize = 60f;
        float gap = 12f;
        float totalW = LobbyColors.Length * swatchSize + (LobbyColors.Length - 1) * gap;
        float startX = -totalW * 0.5f + swatchSize * 0.5f;
        for (int i = 0; i < LobbyColors.Length; i++)
        {
            Button b = MakeColorSwatchButton(
                r.root, $"ColorBtn_{LobbyColors[i].name}", LobbyColors[i].color,
                anchoredPos: new Vector2(startX + i * (swatchSize + gap), -320f),
                size: swatchSize);
            r.colorBtns[i]   = b;
            r.colorNames[i]  = LobbyColors[i].name;
            r.colorValues[i] = LobbyColors[i].color;
        }

        // Status text
        r.statusLabel = CreateLabel(r.root, "StatusLabel", "Waiting for player 2...",
            topY: 410f, height: 30f, fontSize: 20, color: TitleAmb, bold: true);
        r.statusLabel.alignment = TextAlignmentOptions.Center;

        // Start + Leave buttons
        r.startMatchBtn = MakeBtn(r.root, "StartMatchButton", "START MATCH",
            BtnSuccess, topY: 470f, w: 320f, h: 60f);

        r.leaveBtn = MakeBtnBottom(r.root, "LeaveButton", "Leave Room", BtnDanger, 280f, 50f);

        return r;
    }

    // ================================================================== //
    // Canvas + EventSystem
    // ================================================================== //

    private static Canvas RebuildCanvas()
    {
        GameObject existing = GameObject.Find(CanvasName);
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing);
            Debug.Log("[SetupLobby] Removed previous LobbyCanvas — rebuilding.");
        }

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
}
