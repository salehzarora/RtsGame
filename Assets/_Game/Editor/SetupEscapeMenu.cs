using System;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// One-click editor tool that builds the in-game ESC pause menu.
///
/// Menu: Tools → RTS → Setup → Setup Escape Menu
///
/// What it creates:
///   • EscapeMenuCanvas (Screen Space Overlay, sort order 1100 — above the
///     Main Menu canvas at 1000 so a pause menu pop while the main menu was
///     still visible would render on top; in practice the controller only
///     opens during gameplay anyway).
///     ├── Background        (dim full-screen overlay)
///     ├── Panel             (centred dark card)
///     │     ├── TitleLabel  ("Paused")
///     │     ├── BtnResume   ("Resume")
///     │     ├── BtnMainMenu ("Main Menu")
///     │     └── BtnQuit     ("Quit")
///
/// Wires <see cref="EscapeMenuController"/> on the GameManager GameObject and
/// links menuCanvas / mainMenuCanvas / hudCanvas / lobbyCanvas references via
/// scene-name lookup.
///
/// Idempotent — re-running destroys the existing EscapeMenuCanvas and
/// rebuilds. Safe to run multiple times.
/// </summary>
public static class SetupEscapeMenu
{
    // ------------------------------------------------------------------ //
    // Palette
    // ------------------------------------------------------------------ //

    private static readonly Color OverlayColor = new Color(0f, 0f, 0f, 0.55f);
    private static readonly Color CardColor    = new Color(0.10f, 0.13f, 0.16f, 0.97f);
    private static readonly Color TitleColor   = new Color(1.00f, 0.95f, 0.85f);
    private static readonly Color ResumeColor  = new Color(0.30f, 0.65f, 0.30f);
    private static readonly Color MenuBtnColor = new Color(0.32f, 0.50f, 0.70f);
    private static readonly Color QuitColor    = new Color(0.70f, 0.30f, 0.30f);

    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Setup/Setup Escape Menu")]
    public static void Run()
    {
        Debug.Log("[SetupEscapeMenu] ── Building EscapeMenuCanvas ──");

        // GameManager hosts the controller component alongside other singletons.
        GameObject gm = GameObject.Find("GameManager");
        if (gm == null)
        {
            gm = new GameObject("GameManager");
            Undo.RegisterCreatedObjectUndo(gm, "Create GameManager");
            Debug.LogWarning("[SetupEscapeMenu] ⚠ GameManager not found — created a new one.");
        }

        // Rebuild canvas from scratch.
        GameObject existing = GameObject.Find("EscapeMenuCanvas");
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing);
            Debug.Log("[SetupEscapeMenu]   Removed old EscapeMenuCanvas — rebuilding.");
        }

        GameObject canvasGO = new GameObject("EscapeMenuCanvas");
        Undo.RegisterCreatedObjectUndo(canvasGO, "Create EscapeMenuCanvas");
        Canvas canvas       = canvasGO.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1100;
        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode  = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight  = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();
        canvasGO.SetActive(false);     // hidden by default

        // Full-screen dim background — also blocks clicks behind the panel.
        RectTransform bg = CreatePanel(canvas.transform, "Background", OverlayColor);
        bg.anchorMin = Vector2.zero;
        bg.anchorMax = Vector2.one;
        bg.offsetMin = Vector2.zero;
        bg.offsetMax = Vector2.zero;

        // Centred card.
        RectTransform card = CreatePanel(canvas.transform, "Panel", CardColor);
        card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f);
        card.pivot     = new Vector2(0.5f, 0.5f);
        card.anchoredPosition = Vector2.zero;
        card.sizeDelta = new Vector2(420f, 380f);

        // Title.
        TextMeshProUGUI title = CreateTMPText(card, "TitleLabel", "Paused",
            anchorMin: new Vector2(0.5f, 1f), anchorMax: new Vector2(0.5f, 1f),
            anchoredPos: new Vector2(0f, -50f),
            size: new Vector2(380f, 60f),
            fontSize: 42,
            color: TitleColor,
            alignment: TextAlignmentOptions.Center);
        title.fontStyle = FontStyles.Bold;
        _ = title;

        // Add controller before we wire buttons.
        EscapeMenuController controller = gm.GetComponent<EscapeMenuController>();
        if (controller == null) controller = gm.AddComponent<EscapeMenuController>();
        controller.menuCanvas     = canvasGO;
        controller.mainMenuCanvas = GameObject.Find("MainMenuCanvas");
        controller.hudCanvas      = GameObject.Find("HUDCanvas");
        controller.lobbyCanvas    = GameObject.Find("LobbyCanvas");
        EditorUtility.SetDirty(controller);

        // Buttons.
        Button resume = CreateButton(card, "BtnResume", "Resume", ResumeColor,
            anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
            anchoredPos: new Vector2(0f, 60f), size: new Vector2(320f, 60f));
        WireButton(resume, controller, nameof(EscapeMenuController.OnClickResume));

        Button mainMenu = CreateButton(card, "BtnMainMenu", "Main Menu", MenuBtnColor,
            anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
            anchoredPos: new Vector2(0f, -20f), size: new Vector2(320f, 60f));
        WireButton(mainMenu, controller, nameof(EscapeMenuController.OnClickMainMenu));

        Button quit = CreateButton(card, "BtnQuit", "Quit", QuitColor,
            anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
            anchoredPos: new Vector2(0f, -100f), size: new Vector2(320f, 60f));
        WireButton(quit, controller, nameof(EscapeMenuController.OnClickQuit));

        // Make sure the EscapeMenu canvas renders last (above HUD + MainMenu).
        canvasGO.transform.SetAsLastSibling();

        EditorUtility.SetDirty(canvasGO);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log("[SetupEscapeMenu] ── Done. EscapeMenuCanvas ready (sort order 1100). ──\n" +
                  "  • Press Play → during gameplay press ESC to open the pause menu.\n" +
                  "  • Resume / Main Menu / Quit are wired to EscapeMenuController.");
    }

    // ================================================================== //
    // UI factories
    // ================================================================== //

    private static RectTransform CreatePanel(Transform parent, string n, Color color)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {n}");

        RectTransform rt = go.AddComponent<RectTransform>();
        Image img        = go.AddComponent<Image>();
        img.color        = color;
        img.raycastTarget = true;
        return rt;
    }

    private static TextMeshProUGUI CreateTMPText(
        RectTransform parent, string n, string text,
        Vector2 anchorMin, Vector2 anchorMax,
        Vector2 anchoredPos, Vector2 size,
        int fontSize, Color color, TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {n}");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin        = anchorMin;
        rt.anchorMax        = anchorMax;
        rt.pivot            = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta        = size;

        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text            = text;
        tmp.fontSize        = fontSize;
        tmp.color           = color;
        tmp.alignment       = alignment;
        tmp.raycastTarget   = false;
        return tmp;
    }

    private static Button CreateButton(
        RectTransform parent, string n, string label, Color btnColor,
        Vector2 anchorMin, Vector2 anchorMax,
        Vector2 anchoredPos, Vector2 size)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {n}");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin        = anchorMin;
        rt.anchorMax        = anchorMax;
        rt.pivot            = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta        = size;
        rt.localScale       = Vector3.one;

        Image bgImg     = go.AddComponent<Image>();
        bgImg.color     = btnColor;
        bgImg.raycastTarget = true;

        Button btn = go.AddComponent<Button>();
        ColorBlock cb       = ColorBlock.defaultColorBlock;
        cb.normalColor      = btnColor;
        cb.highlightedColor = btnColor * 1.20f;
        cb.pressedColor     = btnColor * 0.75f;
        cb.selectedColor    = btnColor;
        cb.fadeDuration     = 0.08f;
        btn.colors          = cb;

        // Label child
        GameObject textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        RectTransform textRT = textGO.AddComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero; textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero; textRT.offsetMax = Vector2.zero;
        TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text          = label;
        tmp.fontSize      = 26;
        tmp.color         = Color.white;
        tmp.alignment     = TextAlignmentOptions.Center;
        tmp.fontStyle     = FontStyles.Bold;
        tmp.raycastTarget = false;
        return btn;
    }

    // ================================================================== //
    // Wire button → controller method (mirrors SetupMainMenu's helper)
    // ================================================================== //

    private static void WireButton(Button btn, EscapeMenuController controller, string methodName)
    {
        SerializedObject   so    = new SerializedObject(btn);
        SerializedProperty calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
        if (calls != null) { calls.ClearArray(); so.ApplyModifiedProperties(); }

        MethodInfo method = typeof(EscapeMenuController).GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.Public);
        if (method == null)
        {
            Debug.LogError($"[SetupEscapeMenu] ✗ Method '{methodName}' not found on EscapeMenuController.");
            return;
        }

        UnityAction action = (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), controller, method);
        UnityEventTools.AddPersistentListener(btn.onClick, action);
        EditorUtility.SetDirty(btn);
        Debug.Log($"[SetupEscapeMenu]   ✓ {btn.name}.onClick → EscapeMenuController.{methodName}");
    }
}
