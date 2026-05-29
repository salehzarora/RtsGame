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
/// One-click tool that builds the reusable Options / Settings menu and adds an
/// "Options" button to BOTH the main menu and the in-game ESC menu.
///
/// Menu: Tools → RTS → UI → Setup Options Menu
///
/// What it creates / does (idempotent — re-running rebuilds the OptionsCanvas
/// and re-adds the buttons without duplicating them):
///   • OptionsCanvas (Screen Space Overlay, sort order 1200 — above ESC's 1100)
///       └ OptionsPanel (toggled)            ← OptionsMenuController.optionsPanel
///           ├ Background (dim, click-blocking)
///           └ Card  (title + Audio section + Display section + buttons)
///   • OptionsMenuController on the OptionsCanvas root, references wired.
///   • A "BtnOptions" on MainMenuCanvas → OptionsMenuController.OpenOptionsFromMainMenu.
///   • A "BtnOptions" on EscapeMenuCanvas → OptionsMenuController.OpenOptionsFromPauseMenu.
///
/// Sliders/dropdowns/toggle/back/reset bind their own listeners at runtime in
/// OptionsMenuController; this tool only assigns the serialized references and
/// wires the two external open-buttons (persistent listeners).
/// </summary>
public static class SetupOptionsMenu
{
    // Palette — consistent with SetupMainMenu / SetupEscapeMenu.
    private static readonly Color OverlayColor = new Color(0f, 0f, 0f, 0.55f);
    private static readonly Color CardColor    = new Color(0.10f, 0.13f, 0.16f, 0.98f);
    private static readonly Color TitleColor   = new Color(1.00f, 0.95f, 0.85f);
    private static readonly Color HeaderColor  = new Color(0.55f, 0.78f, 1.00f);
    private static readonly Color LabelColor   = new Color(0.86f, 0.89f, 0.93f);
    private static readonly Color BackColor    = new Color(0.32f, 0.50f, 0.70f);
    private static readonly Color ApplyColor   = new Color(0.30f, 0.62f, 0.34f);
    private static readonly Color ResetColor   = new Color(0.55f, 0.45f, 0.30f);
    private static readonly Color OptionsBtn   = new Color(0.40f, 0.44f, 0.52f);

    [MenuItem("Tools/RTS/UI/Setup Options Menu")]
    public static void Run()
    {
        Debug.Log("[SetupOptions] ── Building Options menu ──");

        EnsureEventSystem();

        GameObject mainMenu = FindCanvas("MainMenuCanvas");
        GameObject escMenu  = FindCanvas("EscapeMenuCanvas");

        // ---- Rebuild OptionsCanvas -------------------------------------- //
        GameObject existing = FindCanvas("OptionsCanvas");
        if (existing != null) Undo.DestroyObjectImmediate(existing);

        GameObject canvasGO = new GameObject("OptionsCanvas");
        Undo.RegisterCreatedObjectUndo(canvasGO, "Create OptionsCanvas");
        Canvas canvas        = canvasGO.AddComponent<Canvas>();
        canvas.renderMode    = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder  = 1200;                 // above ESC (1100) + Main (1000)
        CanvasScaler scaler  = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode   = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight  = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();

        OptionsMenuController controller = canvasGO.AddComponent<OptionsMenuController>();
        controller.mainMenuReturn   = mainMenu;
        controller.escapeMenuReturn = escMenu;

        // ---- OptionsPanel (the toggled container) ----------------------- //
        RectTransform panel = CreatePanel(canvasGO.transform, "OptionsPanel", new Color(0, 0, 0, 0), raycast: false);
        Stretch(panel);
        controller.optionsPanel = panel.gameObject;

        // Dim, click-blocking background.
        RectTransform bg = CreatePanel(panel, "Background", OverlayColor, raycast: true);
        Stretch(bg);

        // Centered card.
        RectTransform card = CreatePanel(panel, "Card", CardColor, raycast: true);
        card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f);
        card.pivot = new Vector2(0.5f, 0.5f);
        card.anchoredPosition = Vector2.zero;
        card.sizeDelta = new Vector2(760f, 860f);

        // ---- Title ------------------------------------------------------ //
        CreateText(card, "Title", "Options", 0f, 385f, 700f, 60f, 44, TitleColor,
                   TextAlignmentOptions.Center, bold: true);

        // ---- Audio section --------------------------------------------- //
        CreateText(card, "AudioHeader", "Audio", -330f, 315f, 300f, 40f, 30, HeaderColor,
                   TextAlignmentOptions.Left, bold: true);

        controller.masterSlider = AudioRow(card, "Master Volume", 250f,
                                           out controller.masterPercent);
        controller.sfxSlider    = AudioRow(card, "SFX Volume", 195f,
                                           out controller.sfxPercent);
        controller.musicSlider  = AudioRow(card, "Music Volume", 140f,
                                           out controller.musicPercent);

        // ---- Display section ------------------------------------------- //
        CreateText(card, "DisplayHeader", "Display", -330f, 70f, 300f, 40f, 30, HeaderColor,
                   TextAlignmentOptions.Left, bold: true);

        CreateText(card, "ResolutionLabel", "Resolution", -250f, 15f, 280f, 40f, 24, LabelColor,
                   TextAlignmentOptions.Left, bold: false);
        controller.resolutionDropdown = CreateDropdown(card, "ResolutionDropdown", 130f, 15f, 320f, 46f);

        CreateText(card, "FullscreenLabel", "Fullscreen", -250f, -45f, 280f, 40f, 24, LabelColor,
                   TextAlignmentOptions.Left, bold: false);
        controller.fullscreenToggle = CreateToggle(card, "FullscreenToggle", -10f, -45f);

        CreateText(card, "QualityLabel", "Quality", -250f, -105f, 280f, 40f, 24, LabelColor,
                   TextAlignmentOptions.Left, bold: false);
        controller.qualityDropdown = CreateDropdown(card, "QualityDropdown", 130f, -105f, 320f, 46f);

        // ---- Buttons: Reset | Apply | Back ------------------------------ //
        controller.resetButton = CreateButton(card, "BtnReset", "Reset Defaults", ResetColor,
                                              -250f, -210f, 230f, 64f);
        controller.applyButton = CreateButton(card, "BtnApply", "Apply", ApplyColor,
                                              0f, -210f, 230f, 64f);
        controller.backButton  = CreateButton(card, "BtnBack", "Back", BackColor,
                                              250f, -210f, 230f, 64f);

        panel.gameObject.SetActive(false);   // hidden until opened
        EditorUtility.SetDirty(controller);
        canvasGO.transform.SetAsLastSibling();

        // ---- Add the two Options buttons -------------------------------- //
        int added = 0;
        if (mainMenu != null) added += AddOptionsButtonToMainMenu(mainMenu, controller) ? 1 : 0;
        else Debug.LogWarning("[SetupOptions] MainMenuCanvas not found — run Setup Main Menu first, then re-run.");

        if (escMenu != null) added += AddOptionsButtonToEscapeMenu(escMenu, controller) ? 1 : 0;
        else Debug.LogWarning("[SetupOptions] EscapeMenuCanvas not found — run Setup Escape Menu first, then re-run.");

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"[SetupOptions] ✓ Done. OptionsCanvas built, {added} Options button(s) wired. " +
                  "Audio sliders apply immediately; resolution/fullscreen/quality apply immediately. " +
                  "Make sure an AudioManager exists (Tools → RTS → Audio → Setup Audio System).");
    }

    // ================================================================== //
    // Targeted fix: ensure the Options button exists in the ESC pause menu
    // ================================================================== //

    /// <summary>
    /// Idempotently add + wire the "Options" button inside the in-game ESC
    /// pause menu (Resume / Options / Main Menu / Quit) and (re)point the
    /// controller's escape-return reference at the current EscapeMenuCanvas.
    /// Safe to call repeatedly and after the pause menu is rebuilt. Exposed as a
    /// standalone menu item AND called from SetupEscapeMenu so the button can't
    /// silently go missing.
    /// </summary>
    public static bool EnsurePauseMenuOptionsButton()
    {
        OptionsMenuController controller =
            UnityEngine.Object.FindFirstObjectByType<OptionsMenuController>(FindObjectsInactive.Include);
        if (controller == null)
        {
            Debug.LogWarning("[SetupOptions] No OptionsMenuController in the scene — run " +
                             "Tools → RTS → UI → Setup Options Menu first.");
            return false;
        }

        GameObject escMenu = FindCanvas("EscapeMenuCanvas");
        if (escMenu == null)
        {
            Debug.LogWarning("[SetupOptions] EscapeMenuCanvas not found — run " +
                             "Tools → RTS → Setup → Setup Escape Menu first, then re-run this.");
            return false;
        }

        // Make sure Back from in-game Options returns to the pause menu.
        controller.escapeMenuReturn = escMenu;
        EditorUtility.SetDirty(controller);

        bool ok = AddOptionsButtonToEscapeMenu(escMenu, controller);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[SetupOptions] ✓ Options button ensured in the ESC pause menu " +
                  "(Resume / Options / Main Menu / Quit).");
        return ok;
    }

    [MenuItem("Tools/RTS/UI/Add Options Button To Pause Menu")]
    public static void AddOptionsToPauseMenuMenu() => EnsurePauseMenuOptionsButton();

    // ================================================================== //
    // Section row builders
    // ================================================================== //

    private static Slider AudioRow(RectTransform card, string label, float y, out TextMeshProUGUI percent)
    {
        CreateText(card, label + "Label", label, -250f, y, 280f, 40f, 24, LabelColor,
                   TextAlignmentOptions.Left, bold: false);
        Slider s = CreateSlider(card, label + "Slider", 95f, y, 270f, 26f);
        percent  = CreateText(card, label + "Percent", "100%", 305f, y, 80f, 40f, 22, LabelColor,
                              TextAlignmentOptions.Center, bold: false);
        return s;
    }

    // ================================================================== //
    // UI factories
    // ================================================================== //

    private static RectTransform CreatePanel(Transform parent, string n, Color color, bool raycast)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {n}");
        RectTransform rt = go.AddComponent<RectTransform>();
        Image img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = raycast;
        return rt;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    private static TextMeshProUGUI CreateText(
        RectTransform parent, string n, string text, float x, float y, float w, float h,
        int fontSize, Color color, TextAlignmentOptions alignment, bool bold)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {n}");
        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);

        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = alignment;
        tmp.raycastTarget = false;
        if (bold) tmp.fontStyle = FontStyles.Bold;
        return tmp;
    }

    private static Button CreateButton(
        RectTransform parent, string n, string label, Color btnColor,
        float x, float y, float w, float h)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {n}");
        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);

        Image bgImg = go.AddComponent<Image>();
        bgImg.color = btnColor;

        Button btn = go.AddComponent<Button>();
        ColorBlock cb = ColorBlock.defaultColorBlock;
        cb.normalColor = btnColor;
        cb.highlightedColor = btnColor * 1.20f;
        cb.pressedColor = btnColor * 0.75f;
        cb.selectedColor = btnColor;
        cb.fadeDuration = 0.08f;
        btn.colors = cb;

        GameObject t = new GameObject("Text");
        t.transform.SetParent(go.transform, false);
        RectTransform trt = t.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        TextMeshProUGUI tmp = t.AddComponent<TextMeshProUGUI>();
        tmp.text = label; tmp.fontSize = 24; tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center; tmp.fontStyle = FontStyles.Bold;
        tmp.raycastTarget = false;
        return btn;
    }

    private static Slider CreateSlider(RectTransform parent, string n, float x, float y, float w, float h)
    {
        GameObject go = DefaultControls.CreateSlider(BuiltinUIResources());
        go.name = n;
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {n}");
        PlaceCentered((RectTransform)go.transform, x, y, w, h);

        Slider s = go.GetComponent<Slider>();
        s.minValue = 0f; s.maxValue = 1f; s.value = 1f;
        return s;
    }

    private static Toggle CreateToggle(RectTransform parent, string n, float x, float y)
    {
        GameObject go = DefaultControls.CreateToggle(BuiltinUIResources());
        go.name = n;
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {n}");
        PlaceCentered((RectTransform)go.transform, x, y, 40f, 40f);

        // Clear the built-in "Toggle" legacy-Text label — we use our own TMP label.
        Transform lbl = go.transform.Find("Label");
        if (lbl != null) lbl.gameObject.SetActive(false);

        return go.GetComponent<Toggle>();
    }

    private static TMP_Dropdown CreateDropdown(RectTransform parent, string n, float x, float y, float w, float h)
    {
        GameObject go = TMP_DefaultControls.CreateDropdown(BuiltinTMPResources());
        go.name = n;
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {n}");
        PlaceCentered((RectTransform)go.transform, x, y, w, h);
        return go.GetComponent<TMP_Dropdown>();
    }

    private static void PlaceCentered(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    private static DefaultControls.Resources BuiltinUIResources()
    {
        return new DefaultControls.Resources
        {
            standard   = Builtin("UI/Skin/UISprite.psd"),
            background = Builtin("UI/Skin/Background.psd"),
            inputField = Builtin("UI/Skin/InputFieldBackground.psd"),
            knob       = Builtin("UI/Skin/Knob.psd"),
            checkmark  = Builtin("UI/Skin/Checkmark.psd"),
            dropdown   = Builtin("UI/Skin/DropdownArrow.psd"),
            mask       = Builtin("UI/Skin/UIMask.psd"),
        };
    }

    private static TMP_DefaultControls.Resources BuiltinTMPResources()
    {
        return new TMP_DefaultControls.Resources
        {
            standard   = Builtin("UI/Skin/UISprite.psd"),
            background = Builtin("UI/Skin/Background.psd"),
            inputField = Builtin("UI/Skin/InputFieldBackground.psd"),
            knob       = Builtin("UI/Skin/Knob.psd"),
            checkmark  = Builtin("UI/Skin/Checkmark.psd"),
            dropdown   = Builtin("UI/Skin/DropdownArrow.psd"),
            mask       = Builtin("UI/Skin/UIMask.psd"),
        };
    }

    private static Sprite Builtin(string path) => AssetDatabase.GetBuiltinExtraResource<Sprite>(path);

    // ================================================================== //
    // Add Options buttons to existing menus
    // ================================================================== //

    private static bool AddOptionsButtonToMainMenu(GameObject mainMenu, OptionsMenuController controller)
    {
        RectTransform root = (RectTransform)mainMenu.transform;
        Transform existing = root.Find("BtnOptions");
        Button btn = existing != null
            ? existing.GetComponent<Button>()
            : CreateButton(root, "BtnOptions", "Options", OptionsBtn, 0f, -140f, 360f, 70f);
        if (btn.GetComponentInChildren<TextMeshProUGUI>(true) is TextMeshProUGUI t) t.fontSize = 28;

        WireOpenButton(btn, controller, nameof(OptionsMenuController.OpenOptionsFromMainMenu));
        EditorUtility.SetDirty(mainMenu);
        return true;
    }

    private static bool AddOptionsButtonToEscapeMenu(GameObject escMenu, OptionsMenuController controller)
    {
        // ESC buttons live under a "Panel" card. Find it (fall back to canvas root).
        Transform panel = escMenu.transform.Find("Panel");
        RectTransform host = panel != null ? (RectTransform)panel : (RectTransform)escMenu.transform;

        // Grow the card and re-space the existing 3 buttons to fit a 4th.
        if (panel != null)
        {
            RectTransform pr = (RectTransform)panel;
            pr.sizeDelta = new Vector2(Mathf.Max(420f, pr.sizeDelta.x), 470f);
            MoveButtonY(host, "BtnResume",    120f);
            MoveButtonY(host, "BtnMainMenu",  -30f);
            MoveButtonY(host, "BtnQuit",     -105f);
        }

        Transform existing = host.Find("BtnOptions");
        Button btn = existing != null
            ? existing.GetComponent<Button>()
            : CreateButton(host, "BtnOptions", "Options", OptionsBtn, 0f, 45f, 320f, 60f);

        WireOpenButton(btn, controller, nameof(OptionsMenuController.OpenOptionsFromPauseMenu));
        EditorUtility.SetDirty(escMenu);
        return true;
    }

    private static void MoveButtonY(RectTransform host, string name, float y)
    {
        Transform b = host.Find(name);
        if (b == null) return;
        RectTransform rt = (RectTransform)b;
        rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, y);
    }

    // ================================================================== //
    // Wire button → OptionsMenuController method (persistent listener)
    // ================================================================== //

    private static void WireOpenButton(Button btn, OptionsMenuController controller, string methodName)
    {
        SerializedObject so = new SerializedObject(btn);
        SerializedProperty calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
        if (calls != null) { calls.ClearArray(); so.ApplyModifiedProperties(); }

        MethodInfo method = typeof(OptionsMenuController).GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.Public);
        if (method == null)
        {
            Debug.LogError($"[SetupOptions] ✗ Method '{methodName}' not found on OptionsMenuController.");
            return;
        }

        UnityAction action = (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), controller, method);
        UnityEventTools.AddPersistentListener(btn.onClick, action);
        EditorUtility.SetDirty(btn);
        Debug.Log($"[SetupOptions]   ✓ {btn.name}.onClick → OptionsMenuController.{methodName}");
    }

    // ================================================================== //
    // Helpers
    // ================================================================== //

    private static GameObject FindCanvas(string n)
    {
        // Include inactive — EscapeMenuCanvas is disabled in the scene at edit time.
        Canvas[] all = UnityEngine.Object.FindObjectsByType<Canvas>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (Canvas c in all)
            if (c != null && c.gameObject.name == n) return c.gameObject;
        return null;
    }

    private static void EnsureEventSystem()
    {
        if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null) return;
        GameObject es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
        es.AddComponent<StandaloneInputModule>();
        Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
        Debug.Log("[SetupOptions] ✓ EventSystem created");
    }
}
