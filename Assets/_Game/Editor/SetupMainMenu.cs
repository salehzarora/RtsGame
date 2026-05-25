using System;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// One-click editor tool that builds the pre-game main menu.
///
/// Menu: Tools → RTS → Setup → Setup Main Menu
///
/// Idempotent — destroys and rebuilds MainMenuCanvas from scratch each run,
/// so re-running cannot leave duplicate menus in the scene.
///
/// What it creates:
///   • MainMenuCanvas (Screen Space Overlay, sort order 1000 — above HUD)
///     ├── Background          (full-screen dark panel)
///     ├── Title               ("RTS Prototype")
///     ├── ArmyLabel           ("Army: Default Army")
///     ├── ColorLabel          ("Color: Blue")
///     ├── ColorButtonRow      (Blue / Red / Green / Yellow / Orange / Purple)
///     └── PlayButton          ("Play")
///
/// Also ensured on the GameManager GameObject (created if missing):
///   • PlayerFactionManager
///   • GameStateManager
///   • MainMenuController (with references wired)
///
/// The HUDCanvas (built by Tools → RTS → Setup HUD) is assigned to the
/// controller so it can hide it at Awake and reveal it on Play.
/// </summary>
public static class SetupMainMenu
{
    // ------------------------------------------------------------------ //
    // Palette
    // ------------------------------------------------------------------ //

    private static readonly Color BgColor       = new Color(0.05f, 0.07f, 0.10f, 0.95f);
    private static readonly Color TitleColor    = new Color(1.00f, 0.95f, 0.85f);
    private static readonly Color LabelColor    = new Color(0.90f, 0.90f, 0.95f);
    private static readonly Color PlayBtnColor  = new Color(0.30f, 0.65f, 0.30f);

    // Color-button swatches (must match MainMenuController.OnClickColor* values).
    private static readonly (string label, Color color, string method)[] ColorSwatches =
    {
        ("Blue",   new Color(0.20f, 0.55f, 1.00f), nameof(MainMenuController.OnClickColorBlue)),
        ("Red",    new Color(0.92f, 0.20f, 0.20f), nameof(MainMenuController.OnClickColorRed)),
        ("Green",  new Color(0.25f, 0.80f, 0.32f), nameof(MainMenuController.OnClickColorGreen)),
        ("Yellow", new Color(1.00f, 0.85f, 0.18f), nameof(MainMenuController.OnClickColorYellow)),
        ("Orange", new Color(1.00f, 0.55f, 0.10f), nameof(MainMenuController.OnClickColorOrange)),
        ("Purple", new Color(0.65f, 0.30f, 0.85f), nameof(MainMenuController.OnClickColorPurple)),
    };

    // ------------------------------------------------------------------ //
    // Entry
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Setup/Setup Main Menu")]
    public static void Run()
    {
        Debug.Log("[SetupMainMenu] ── Building MainMenuCanvas ──");

        EnsureEventSystem();

        // Make sure the gameplay HUD exists; we need to assign its GameObject
        // to the menu controller. If it's missing, warn the user.
        Canvas hudCanvas = FindCanvasByName("HUDCanvas");
        if (hudCanvas == null)
        {
            Debug.LogWarning("[SetupMainMenu] ⚠ HUDCanvas not found. Run Tools → RTS → Setup HUD first, " +
                             "then re-run this tool, otherwise the HUD won't auto-hide while the menu is open.");
        }

        // GameManager + managers
        GameObject gm = GetOrCreateGameManager();
        PlayerFactionManager pfm  = GetOrAddComponent<PlayerFactionManager>(gm);
        GameStateManager     gsm  = GetOrAddComponent<GameStateManager>(gm);
        _ = gsm;
        EditorUtility.SetDirty(gm);

        // Rebuild canvas
        Canvas canvas = RebuildMenuCanvas();

        // Full-screen background
        RectTransform bg = CreateImagePanel(canvas.transform, "Background", BgColor);
        bg.anchorMin = Vector2.zero;
        bg.anchorMax = Vector2.one;
        bg.offsetMin = Vector2.zero;
        bg.offsetMax = Vector2.zero;

        // Title
        TextMeshProUGUI title = CreateTMPText(
            (RectTransform)canvas.transform,
            "Title", "RTS Prototype",
            anchorMin: new Vector2(0.5f, 1f), anchorMax: new Vector2(0.5f, 1f),
            anchoredPos: new Vector2(0f, -160f),
            size: new Vector2(800f, 90f),
            fontSize: 64,
            color: TitleColor,
            alignment: TextAlignmentOptions.Center);
        title.fontStyle = FontStyles.Bold;

        // Army label
        TextMeshProUGUI armyLabel = CreateTMPText(
            (RectTransform)canvas.transform,
            "ArmyLabel", "Army: Default Army",
            anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
            anchoredPos: new Vector2(0f, 140f),
            size: new Vector2(600f, 50f),
            fontSize: 32,
            color: LabelColor,
            alignment: TextAlignmentOptions.Center);

        // Color label
        TextMeshProUGUI colorLabel = CreateTMPText(
            (RectTransform)canvas.transform,
            "ColorLabel", "Color: Blue",
            anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
            anchoredPos: new Vector2(0f, 80f),
            size: new Vector2(600f, 50f),
            fontSize: 28,
            color: LabelColor,
            alignment: TextAlignmentOptions.Center);

        // Color button row (6 swatches centered horizontally)
        const float swatchSize = 80f;
        const float swatchGap  = 16f;
        int   count = ColorSwatches.Length;
        float rowWidth = count * swatchSize + (count - 1) * swatchGap;
        float startX   = -rowWidth * 0.5f + swatchSize * 0.5f;

        // MainMenuController has to exist before we can wire buttons.
        MainMenuController controller = GetOrAddComponent<MainMenuController>(gm);
        controller.menuCanvas       = canvas.gameObject;
        controller.hudCanvas        = hudCanvas != null ? hudCanvas.gameObject : null;
        controller.armyLabel        = armyLabel;
        controller.colorLabel       = colorLabel;
        controller.defaultColor     = ColorSwatches[0].color; // Blue
        controller.defaultColorName = ColorSwatches[0].label;
        EditorUtility.SetDirty(controller);
        _ = pfm; // suppress unused warning — manager will be discovered by controller at runtime

        for (int i = 0; i < count; i++)
        {
            (string label, Color color, string method) sw = ColorSwatches[i];
            float x = startX + i * (swatchSize + swatchGap);

            Button swatchBtn = CreateButton(
                (RectTransform)canvas.transform,
                $"BtnColor{sw.label}", sw.label,
                sw.color,
                anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
                anchoredPos: new Vector2(x, -20f),
                size:        new Vector2(swatchSize, swatchSize));

            WireButton(swatchBtn, controller, sw.method);
        }

        // Play button
        Button play = CreateButton(
            (RectTransform)canvas.transform,
            "BtnPlay", "Play",
            PlayBtnColor,
            anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
            anchoredPos: new Vector2(0f, -160f),
            size:        new Vector2(300f, 80f));
        play.GetComponentInChildren<TextMeshProUGUI>(true).fontSize = 36;

        WireButton(play, controller, nameof(MainMenuController.OnClickPlay));

        // Ensure the menu renders above the gameplay HUD (sort order 1000 > 999).
        canvas.transform.SetAsLastSibling();

        EditorUtility.SetDirty(canvas.gameObject);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log("[SetupMainMenu] ── Done. ──\n" +
                  "  • Press Play in Unity → menu shows first.\n" +
                  "  • Pick a color, press Play → HUD appears, gameplay starts.\n" +
                  "  • For team-color accents to apply, also run:\n" +
                  "      Tools → RTS → Setup → Apply Team Color Markers To Prefabs");
    }

    // ================================================================== //
    // Canvas
    // ================================================================== //

    private static Canvas RebuildMenuCanvas()
    {
        GameObject existing = GameObject.Find("MainMenuCanvas");
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing);
            Debug.Log("[SetupMainMenu]   Removed old MainMenuCanvas — rebuilding from scratch.");
        }

        GameObject go = new GameObject("MainMenuCanvas");
        Undo.RegisterCreatedObjectUndo(go, "Create MainMenuCanvas");

        Canvas canvas       = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;             // above HUD (999)
        canvas.pixelPerfect = false;

        CanvasScaler scaler        = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight  = 0.5f;

        go.AddComponent<GraphicRaycaster>();
        go.SetActive(true);

        Debug.Log("[SetupMainMenu] ✓ MainMenuCanvas created (sort order 1000)");
        return canvas;
    }

    private static Canvas FindCanvasByName(string n)
    {
        GameObject go = GameObject.Find(n);
        return go != null ? go.GetComponent<Canvas>() : null;
    }

    // ================================================================== //
    // EventSystem
    // ================================================================== //

    private static void EnsureEventSystem()
    {
        if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null) return;
        GameObject esGO = new GameObject("EventSystem");
        esGO.AddComponent<EventSystem>();
        esGO.AddComponent<StandaloneInputModule>();
        Undo.RegisterCreatedObjectUndo(esGO, "Create EventSystem");
        Debug.Log("[SetupMainMenu] ✓ EventSystem created");
    }

    // ================================================================== //
    // GameManager helpers
    // ================================================================== //

    private static GameObject GetOrCreateGameManager()
    {
        GameObject gm = GameObject.Find("GameManager");
        if (gm != null) return gm;

        gm = new GameObject("GameManager");
        Undo.RegisterCreatedObjectUndo(gm, "Create GameManager");
        Debug.LogWarning("[SetupMainMenu] ⚠ GameManager was not found — created a new one.");
        return gm;
    }

    private static T GetOrAddComponent<T>(GameObject go) where T : Component
    {
        T comp = go.GetComponent<T>();
        if (comp != null) return comp;
        comp = go.AddComponent<T>();
        Debug.Log($"[SetupMainMenu] ✓ Added {typeof(T).Name} to '{go.name}'");
        return comp;
    }

    // ================================================================== //
    // UI factories
    // ================================================================== //

    private static RectTransform CreateImagePanel(Transform parent, string n, Color color)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {n}");

        RectTransform rt = go.AddComponent<RectTransform>();
        Image         img = go.AddComponent<Image>();
        img.color           = color;
        img.raycastTarget   = false;
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
        tmp.fontSize      = 22;
        tmp.color         = Color.white;
        tmp.alignment     = TextAlignmentOptions.Center;
        tmp.fontStyle     = FontStyles.Bold;
        tmp.raycastTarget = false;
        return btn;
    }

    // ================================================================== //
    // Wire button → controller method
    // ================================================================== //

    private static void WireButton(Button btn, MainMenuController controller, string methodName)
    {
        SerializedObject   so    = new SerializedObject(btn);
        SerializedProperty calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
        if (calls != null) { calls.ClearArray(); so.ApplyModifiedProperties(); }

        MethodInfo method = typeof(MainMenuController).GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.Public);
        if (method == null)
        {
            Debug.LogError($"[SetupMainMenu] ✗ Method '{methodName}' not found on MainMenuController.");
            return;
        }

        UnityAction action = (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), controller, method);
        UnityEventTools.AddPersistentListener(btn.onClick, action);
        EditorUtility.SetDirty(btn);
        Debug.Log($"[SetupMainMenu]   ✓ {btn.name}.onClick → MainMenuController.{methodName}");
    }
}
