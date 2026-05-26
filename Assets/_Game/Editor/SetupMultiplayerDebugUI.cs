using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// One-click tool that builds (or rebuilds) the on-screen Photon debug panel
/// in the active scene.
///
/// Menu: Tools → RTS → Multiplayer → Setup Multiplayer Debug UI
///
/// Idempotent — destroys any existing <c>MultiplayerDebugCanvas</c> and
/// rebuilds it from scratch, so re-runs cannot leave stale children. The
/// canvas lives at sort order 1100 so it draws on TOP of the HUD (999) and
/// the main menu (1000), making it usable in both states.
///
/// What it creates:
///   MultiplayerDebugCanvas       — Screen Space Overlay, sort order 1100
///     Panel (350×280, top-right) — dark backing
///       Title          ("MULTIPLAYER DEBUG")
///       StatusText     (multi-line, updated each frame)
///       BtnConnect
///       BtnCreateRoom
///       BtnJoinRandom
///       BtnLeaveRoom
///   MultiplayerDebugPanel        — wired on the canvas root
/// </summary>
public static class SetupMultiplayerDebugUI
{
    // ------------------------------------------------------------------ //
    // Palette — kept consistent with the gameplay HUD's military look.
    // ------------------------------------------------------------------ //

    private static readonly Color PanelBg     = new Color(0.05f, 0.07f, 0.05f, 0.92f);
    private static readonly Color PanelBorder = new Color(0.40f, 0.46f, 0.30f, 1.00f);
    private static readonly Color TitleAmber  = new Color(0.78f, 0.88f, 0.38f, 1.00f);
    private static readonly Color BodyText    = new Color(0.92f, 0.92f, 0.84f, 1.00f);

    private static readonly Color BtnConnectColor    = new Color(0.18f, 0.45f, 0.82f, 1f);   // steel blue
    private static readonly Color BtnCreateRoomColor = new Color(0.30f, 0.65f, 0.30f, 1f);   // green
    private static readonly Color BtnJoinRandomColor = new Color(0.82f, 0.60f, 0.08f, 1f);   // amber
    private static readonly Color BtnLeaveRoomColor  = new Color(0.55f, 0.20f, 0.15f, 1f);   // brick red

    private const string CanvasName = "MultiplayerDebugCanvas";

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Multiplayer/Setup Multiplayer Debug UI")]
    public static void Run()
    {
        Debug.Log("[SetupMultiplayerDebugUI] ── Building debug canvas ──");

        EnsureEventSystem();

        // Sanity-check the manager exists. Not blocking — we still build the
        // UI; the panel buttons will surface a warning if clicked without a
        // manager.
        if (Object.FindAnyObjectByType<NetworkManagerRTS>() == null)
        {
            Debug.LogWarning("[SetupMultiplayerDebugUI] No NetworkManagerRTS in the scene. " +
                             "Run Tools → RTS → Multiplayer → Setup Network Manager first if " +
                             "you want the buttons to actually do anything.");
        }

        // Rebuild canvas
        Canvas canvas = RebuildCanvas();

        // Panel — anchored top-right, ~350×280.
        RectTransform panelBorder = CreatePanel(canvas.transform, "Panel", PanelBorder);
        panelBorder.anchorMin = new Vector2(1f, 1f);
        panelBorder.anchorMax = new Vector2(1f, 1f);
        panelBorder.pivot     = new Vector2(1f, 1f);
        panelBorder.anchoredPosition = new Vector2(-20f, -20f);
        panelBorder.sizeDelta = new Vector2(350f, 290f);

        RectTransform panelInner = CreatePanel(panelBorder, "Inner", PanelBg);
        panelInner.anchorMin = Vector2.zero;
        panelInner.anchorMax = Vector2.one;
        panelInner.offsetMin = new Vector2(2f, 2f);
        panelInner.offsetMax = new Vector2(-2f, -2f);
        panelInner.GetComponent<Image>().raycastTarget = false;

        // Title strip
        TextMeshProUGUI title = CreateText(panelInner, "Title", "MULTIPLAYER DEBUG",
            anchorMin: new Vector2(0f, 1f),
            anchorMax: new Vector2(1f, 1f),
            pivot:     new Vector2(0.5f, 1f),
            anchoredPos: new Vector2(0f, -8f),
            size:        new Vector2(0f, 24f),
            fontSize:    16,
            color:       TitleAmber,
            alignment:   TextAlignmentOptions.Center);
        title.fontStyle = FontStyles.Bold;
        title.rectTransform.offsetMin = new Vector2(8f, title.rectTransform.offsetMin.y);
        title.rectTransform.offsetMax = new Vector2(-8f, title.rectTransform.offsetMax.y);

        // Status text — three-line block under the title.
        TextMeshProUGUI status = CreateText(panelInner, "StatusText",
            "State: Disconnected\nNot in a room.\nLocalPlayerId: —",
            anchorMin: new Vector2(0f, 1f),
            anchorMax: new Vector2(1f, 1f),
            pivot:     new Vector2(0.5f, 1f),
            anchoredPos: new Vector2(0f, -38f),
            size:        new Vector2(0f, 64f),
            fontSize:    13,
            color:       BodyText,
            alignment:   TextAlignmentOptions.TopLeft);
        status.rectTransform.offsetMin = new Vector2(10f, status.rectTransform.offsetMin.y);
        status.rectTransform.offsetMax = new Vector2(-10f, status.rectTransform.offsetMax.y);

        // Four stacked buttons — each 50px tall, centred horizontally.
        // Use anchor (0.5, 0) so they pin to the bottom of the panel and
        // tolerate panel-height tweaks.
        Button btnConnect    = CreateButton(panelInner, "BtnConnect",    "Connect",          BtnConnectColor,    bottomOffset:  10f);
        Button btnCreateRoom = CreateButton(panelInner, "BtnCreateRoom", "Create Room",      BtnCreateRoomColor, bottomOffset:  62f);
        Button btnJoinRandom = CreateButton(panelInner, "BtnJoinRandom", "Join Random Room", BtnJoinRandomColor, bottomOffset: 114f);
        Button btnLeaveRoom  = CreateButton(panelInner, "BtnLeaveRoom",  "Leave Room",       BtnLeaveRoomColor,  bottomOffset: 166f);

        // Wire MultiplayerDebugPanel to the canvas root.
        MultiplayerDebugPanel panel = canvas.gameObject.GetComponent<MultiplayerDebugPanel>();
        if (panel == null) panel = canvas.gameObject.AddComponent<MultiplayerDebugPanel>();
        panel.panelRoot         = panelBorder.gameObject;
        panel.statusText        = status;
        panel.connectButton     = btnConnect;
        panel.createRoomButton  = btnCreateRoom;
        panel.joinRandomButton  = btnJoinRandom;
        panel.leaveRoomButton   = btnLeaveRoom;
        EditorUtility.SetDirty(panel);

        // Ensure the debug canvas renders on top of HUD + menu.
        canvas.transform.SetAsLastSibling();
        EditorUtility.SetDirty(canvas.gameObject);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

#if PHOTON_UNITY_NETWORKING
        Debug.Log("[SetupMultiplayerDebugUI] ✓ Debug canvas built. Photon detected — " +
                  "tick multiplayerMode on NetworkManagerRTS and press Play to see the panel.");
#else
        Debug.Log("[SetupMultiplayerDebugUI] ✓ Debug canvas built. Photon NOT installed — " +
                  "the panel will show but buttons will log warnings until PUN 2 is imported.");
#endif
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
            Debug.Log("[SetupMultiplayerDebugUI]   Removed old MultiplayerDebugCanvas — rebuilding.");
        }

        GameObject go = new GameObject(CanvasName);
        Undo.RegisterCreatedObjectUndo(go, "Create MultiplayerDebugCanvas");

        Canvas canvas       = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1100;       // above HUD (999) AND main menu (1000)
        canvas.pixelPerfect = false;

        CanvasScaler scaler        = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight  = 0.5f;

        go.AddComponent<GraphicRaycaster>();
        go.SetActive(true);
        return canvas;
    }

    private static void EnsureEventSystem()
    {
        if (Object.FindAnyObjectByType<EventSystem>() != null) return;
        GameObject esGO = new GameObject("EventSystem");
        esGO.AddComponent<EventSystem>();
        esGO.AddComponent<StandaloneInputModule>();
        Undo.RegisterCreatedObjectUndo(esGO, "Create EventSystem");
        Debug.Log("[SetupMultiplayerDebugUI] ✓ EventSystem created");
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
        img.raycastTarget = false;     // the buttons handle clicks; the bg shouldn't intercept
        return rt;
    }

    private static TextMeshProUGUI CreateText(
        RectTransform parent, string name, string text,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
        Vector2 anchoredPos, Vector2 size,
        int fontSize, Color color, TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot     = pivot;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text          = text;
        tmp.fontSize      = fontSize;
        tmp.color         = color;
        tmp.alignment     = alignment;
        tmp.raycastTarget = false;
        return tmp;
    }

    private static Button CreateButton(
        RectTransform parent, string name, string label, Color color, float bottomOffset)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt = go.AddComponent<RectTransform>();
        // Anchor at bottom-stretch — keep buttons sticky to the bottom of the
        // panel regardless of size tweaks above. Width inherits from panel
        // minus margins via offset.
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot     = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, bottomOffset);
        rt.sizeDelta = new Vector2(0f, 44f);
        rt.offsetMin = new Vector2(12f, rt.offsetMin.y);
        rt.offsetMax = new Vector2(-12f, rt.offsetMax.y);

        Image bg = go.AddComponent<Image>();
        bg.color = color;
        bg.raycastTarget = true;

        Button btn = go.AddComponent<Button>();
        ColorBlock cb = ColorBlock.defaultColorBlock;
        cb.normalColor      = color;
        cb.highlightedColor = color * 1.25f;
        cb.pressedColor     = color * 0.70f;
        cb.selectedColor    = color;
        cb.fadeDuration     = 0.08f;
        btn.colors          = cb;

        // Label child — stretched to fill the button.
        GameObject textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        RectTransform textRT = textGO.AddComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero;
        textRT.offsetMax = Vector2.zero;

        TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text          = label;
        tmp.fontSize      = 18;
        tmp.color         = Color.white;
        tmp.alignment     = TextAlignmentOptions.Center;
        tmp.fontStyle     = FontStyles.Bold;
        tmp.raycastTarget = false;

        return btn;
    }
}
