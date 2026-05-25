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
/// One-click editor tool that builds and wires the entire RTS HUD.
///
/// Menu: Tools → RTS → Setup HUD
///
/// Safe to run multiple times — the HUDCanvas is destroyed and rebuilt
/// from scratch on every run, so there are no stale child layouts.
///
/// What it creates
///   HUDCanvas               – Screen Space Overlay, sort order 999, 1920×1080
///     TopBar                – full-width strip at the top (height 60)
///       ResourcesText       – "Resources: 0"  (TMP, white, 26pt)
///       PowerText           – "Power: 0 / 0"  (TMP, white, 26pt)
///     BuildPanel            – 250×150 panel bottom-right
///       BtnBarracks         – blue button → RTSHUD.OnClickBuildBarracks
///       BtnPowerPlant       – amber button → RTSHUD.OnClickBuildPowerPlant
///   EventSystem             – created only if one does not exist
///   GameManager             – RTSHUD component added / reconfigured
/// </summary>
public static class SetupRTSHUD
{
    // ------------------------------------------------------------------ //
    // Debug switch — legacy global Build panel
    // ------------------------------------------------------------------ //

    /// <summary>
    /// When false (the default), the legacy bottom-right "BuildPanel" with the
    /// Barracks / PowerPlant / VehicleFactory / Airfield instant-build buttons
    /// is NOT created. Normal gameplay routes all construction through a Dozer.
    /// Flip to true only for debugging, then re-run Tools → RTS → Setup HUD.
    /// </summary>
    private const bool DebugInstantBuildEnabled = false;

    // ------------------------------------------------------------------ //
    // Palette
    // ------------------------------------------------------------------ //

    private static readonly Color PanelBg          = new Color(0.00f, 0.00f, 0.00f, 0.75f);
    private static readonly Color BtnBarracksColor      = new Color(0.18f, 0.45f, 0.82f, 1.00f); // steel blue
    private static readonly Color BtnPowerColor         = new Color(0.82f, 0.60f, 0.08f, 1.00f); // amber
    private static readonly Color BtnVehicleFactoryColor = new Color(0.40f, 0.40f, 0.45f, 1.00f); // gunmetal
    private static readonly Color BtnAirfieldColor      = new Color(0.32f, 0.50f, 0.70f, 1.00f); // sky-blue grey
    private static readonly Color BtnSoldierColor       = new Color(0.30f, 0.65f, 0.30f, 1.00f); // green
    private static readonly Color BtnRPGSoldierColor    = new Color(0.62f, 0.35f, 0.18f, 1.00f); // rust orange
    private static readonly Color BtnWorkerColor        = new Color(0.78f, 0.50f, 0.18f, 1.00f); // tan/orange
    private static readonly Color BtnDozerColor         = new Color(0.92f, 0.72f, 0.12f, 1.00f); // dozer yellow
    private static readonly Color BtnHumveeColor        = new Color(0.28f, 0.36f, 0.22f, 1.00f); // olive drab
    private static readonly Color BtnTankColor          = new Color(0.18f, 0.28f, 0.16f, 1.00f); // dark olive
    private static readonly Color BtnStrikeJetColor     = new Color(0.45f, 0.55f, 0.65f, 1.00f); // air-force blue

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Setup HUD")]
    public static void SetupHUD()
    {
        _tmpWarningShown = false;
        Debug.Log("[SetupRTSHUD] ── Starting HUD setup ──────────────────────");

        // ── 1. EventSystem ────────────────────────────────────────────── //
        EnsureEventSystem();

        // ── 2. HUDCanvas (destroy old, rebuild from scratch) ─────────── //
        Canvas canvas = RebuildCanvas();

        // ── 3. TopBar ────────────────────────────────────────────────── //
        RectTransform topBar = CreatePanel(canvas.transform, "TopBar", PanelBg);

        // Anchor: top-stretch, full width, height = 60 px
        topBar.anchorMin  = new Vector2(0f, 1f);
        topBar.anchorMax  = new Vector2(1f, 1f);
        topBar.pivot      = new Vector2(0.5f, 1f);
        topBar.offsetMin  = new Vector2(0f, -60f); // left=0, bottom of bar
        topBar.offsetMax  = new Vector2(0f,   0f); // right=0, top of bar
        topBar.localScale = Vector3.one;

        // Disable raycast target on the bar itself — it must not eat gameplay clicks
        topBar.GetComponent<Image>().raycastTarget = false;
        topBar.gameObject.SetActive(true);
        Debug.Log("[SetupRTSHUD] ✓ TopBar created (height 60, top-stretch, alpha 0.75)");

        TextMeshProUGUI resourcesText = CreateTMPText(
            topBar, "ResourcesText", "Resources: 0",
            anchoredPos: new Vector2(140f,  0f),
            size:        new Vector2(280f, 45f),
            fontSize:    26,
            alignment:   TextAlignmentOptions.MidlineLeft);
        Debug.Log("[SetupRTSHUD] ✓ ResourcesText assigned (pos 140,0, size 280x45, 26pt)");

        TextMeshProUGUI powerText = CreateTMPText(
            topBar, "PowerText", "Power: 0 / 0",
            anchoredPos: new Vector2(460f,  0f),
            size:        new Vector2(320f, 45f),
            fontSize:    26,
            alignment:   TextAlignmentOptions.MidlineLeft);
        Debug.Log("[SetupRTSHUD] ✓ PowerText assigned (pos 460,0, size 320x45, 26pt)");

        // ── 4. BuildPanel (LEGACY — instant build, gated on debug flag) ── //
        // Default: NOT created. Normal gameplay routes building construction
        // through the Dozer. Flip DebugInstantBuildEnabled to bring back the
        // bottom-right global build buttons.
        RectTransform buildPanel       = null;
        Button        btnBarracks      = null;
        Button        btnPowerPlant    = null;
        Button        btnVehicleFactory = null;
        Button        btnAirfield      = null;

        if (DebugInstantBuildEnabled)
        {
            buildPanel = CreatePanel(canvas.transform, "BuildPanel", PanelBg);

            // Anchor: bottom-right corner, pivot bottom-right so offset is from corner
            buildPanel.anchorMin        = new Vector2(1f, 0f);
            buildPanel.anchorMax        = new Vector2(1f, 0f);
            buildPanel.pivot            = new Vector2(1f, 0f);
            buildPanel.anchoredPosition = new Vector2(-25f,  25f);
            buildPanel.sizeDelta        = new Vector2(250f, 270f);
            buildPanel.localScale       = Vector3.one;
            buildPanel.gameObject.SetActive(true);
            Debug.Log("[SetupRTSHUD] ✓ BuildPanel created (DEBUG — instant build, 250x270, bottom-right)");

            // Four buttons stacked inside the BuildPanel (50px tall, 10px gap).
            btnBarracks = CreateButton(
                buildPanel, "BtnBarracks", "Barracks",
                BtnBarracksColor,
                anchoredPos: new Vector2(0f,  90f),
                size:        new Vector2(220f, 50f));

            btnPowerPlant = CreateButton(
                buildPanel, "BtnPowerPlant", "Power Plant",
                BtnPowerColor,
                anchoredPos: new Vector2(0f,  30f),
                size:        new Vector2(220f, 50f));

            btnVehicleFactory = CreateButton(
                buildPanel, "BtnVehicleFactory", "Vehicle Factory",
                BtnVehicleFactoryColor,
                anchoredPos: new Vector2(0f, -30f),
                size:        new Vector2(220f, 50f));

            btnAirfield = CreateButton(
                buildPanel, "BtnAirfield", "Airfield",
                BtnAirfieldColor,
                anchoredPos: new Vector2(0f, -90f),
                size:        new Vector2(220f, 50f));
        }
        else
        {
            Debug.Log("[RTSHUD] Global instant build panel disabled.");
        }

        // ── 4b. ProductionPanel (bottom-left) ─────────────────────────── //
        RectTransform productionPanel = CreatePanel(canvas.transform, "ProductionPanel", PanelBg);
        productionPanel.anchorMin        = new Vector2(0f, 0f);
        productionPanel.anchorMax        = new Vector2(0f, 0f);
        productionPanel.pivot            = new Vector2(0f, 0f);
        productionPanel.anchoredPosition = new Vector2( 25f, 25f);
        productionPanel.sizeDelta        = new Vector2(250f, 450f);
        productionPanel.localScale       = Vector3.one;
        productionPanel.gameObject.SetActive(false); // hidden until a producer is selected
        Debug.Log("[SetupRTSHUD] ✓ ProductionPanel created (250x450, bottom-left, hidden by default)");

        // Seven buttons stacked inside the ProductionPanel (50px tall, 10px gap).
        // RTSHUD.ShowProductionFor toggles each one based on the selected
        // building's producer capabilities — Barracks shows Soldier + RPG Soldier
        // together, CommandCenter shows Worker + Dozer, VehicleFactory shows
        // Humvee + Tank, Airfield shows only Strike Jet, etc.
        Button btnSoldier = CreateButton(
            productionPanel, "BtnSoldier", "Soldier - 50",
            BtnSoldierColor,
            anchoredPos: new Vector2(0f, 180f),
            size:        new Vector2(220f, 50f));

        Button btnRPGSoldier = CreateButton(
            productionPanel, "BtnRPGSoldier", "RPG Soldier - 120",
            BtnRPGSoldierColor,
            anchoredPos: new Vector2(0f, 120f),
            size:        new Vector2(220f, 50f));

        Button btnWorker = CreateButton(
            productionPanel, "BtnWorker", "Worker - 75",
            BtnWorkerColor,
            anchoredPos: new Vector2(0f,  60f),
            size:        new Vector2(220f, 50f));

        Button btnDozer = CreateButton(
            productionPanel, "BtnDozer", "Dozer - 150",
            BtnDozerColor,
            anchoredPos: new Vector2(0f,   0f),
            size:        new Vector2(220f, 50f));

        Button btnHumvee = CreateButton(
            productionPanel, "BtnHumvee", "Humvee - 150",
            BtnHumveeColor,
            anchoredPos: new Vector2(0f, -60f),
            size:        new Vector2(220f, 50f));

        Button btnTank = CreateButton(
            productionPanel, "BtnArtilleryTank", "Artillery Tank - 350",
            BtnTankColor,
            anchoredPos: new Vector2(0f, -120f),
            size:        new Vector2(220f, 50f));

        Button btnStrikeJet = CreateButton(
            productionPanel, "BtnStrikeJet", "Strike Jet - 450",
            BtnStrikeJetColor,
            anchoredPos: new Vector2(0f,-180f),
            size:        new Vector2(220f, 50f));

        // ── 4c. Dozer Build Panel (bottom-left, shown when Dozer selected) ─ //
        RectTransform dozerBuildPanel = CreatePanel(canvas.transform, "DozerBuildPanel", PanelBg);
        dozerBuildPanel.anchorMin        = new Vector2(0f, 0f);
        dozerBuildPanel.anchorMax        = new Vector2(0f, 0f);
        dozerBuildPanel.pivot            = new Vector2(0f, 0f);
        dozerBuildPanel.anchoredPosition = new Vector2( 25f, 25f);
        dozerBuildPanel.sizeDelta        = new Vector2(250f, 270f);
        dozerBuildPanel.localScale       = Vector3.one;
        dozerBuildPanel.gameObject.SetActive(false); // hidden until a Dozer is selected
        Debug.Log("[SetupRTSHUD] ✓ DozerBuildPanel created (250x270, bottom-left, hidden by default)");

        // Four construction buttons inside the Dozer build panel.
        Button btnDozerBuildBarracks = CreateButton(
            dozerBuildPanel, "BtnDozerBuildBarracks", "Barracks - 100",
            BtnBarracksColor,
            anchoredPos: new Vector2(0f,  90f),
            size:        new Vector2(220f, 50f));

        Button btnDozerBuildPower = CreateButton(
            dozerBuildPanel, "BtnDozerBuildPowerPlant", "Power Plant - 150",
            BtnPowerColor,
            anchoredPos: new Vector2(0f,  30f),
            size:        new Vector2(220f, 50f));

        Button btnDozerBuildVF = CreateButton(
            dozerBuildPanel, "BtnDozerBuildVehicleFactory", "Vehicle Factory - 300",
            BtnVehicleFactoryColor,
            anchoredPos: new Vector2(0f, -30f),
            size:        new Vector2(220f, 50f));

        Button btnDozerBuildAirfield = CreateButton(
            dozerBuildPanel, "BtnDozerBuildAirfield", "Airfield - 600",
            BtnAirfieldColor,
            anchoredPos: new Vector2(0f, -90f),
            size:        new Vector2(220f, 50f));

        // ── 5. GameManager + RTSHUD ───────────────────────────────────── //
        GameObject gm  = GetOrCreateGameManager();
        RTSHUD     hud = GetOrAddComponent<RTSHUD>(gm);

        // Warn if BuildingPlacementManager is absent — RTSHUD will fail silently
        // at runtime if it can't find one via FindAnyObjectByType.
        if (UnityEngine.Object.FindAnyObjectByType<BuildingPlacementManager>() == null)
            Debug.LogWarning(
                "[SetupRTSHUD] ⚠ No BuildingPlacementManager found in the scene.\n" +
                "  Add it to GameManager and assign its Inspector fields (prefabs, layers).\n" +
                "  Build buttons will log an error at runtime until this is fixed.");

        // Assign TMP text references directly (fields are public, SetDirty persists them)
        hud.resourcesText       = resourcesText;
        hud.powerText           = powerText;
        hud.productionPanel     = productionPanel.gameObject;
        hud.soldierButton       = btnSoldier.gameObject;
        hud.soldierButtonLabel  = btnSoldier.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.rpgSoldierButton    = btnRPGSoldier.gameObject;
        hud.rpgSoldierButtonLabel = btnRPGSoldier.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.workerButton        = btnWorker.gameObject;
        hud.workerButtonLabel   = btnWorker.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.dozerButton         = btnDozer.gameObject;
        hud.dozerButtonLabel    = btnDozer.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.humveeButton        = btnHumvee.gameObject;
        hud.humveeButtonLabel   = btnHumvee.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.tankButton          = btnTank.gameObject;
        hud.tankButtonLabel     = btnTank.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.strikeJetButton     = btnStrikeJet.gameObject;
        hud.strikeJetButtonLabel = btnStrikeJet.GetComponentInChildren<TextMeshProUGUI>(true);

        // Dozer build panel — separate panel shown when a Dozer unit is selected.
        hud.dozerBuildPanel                  = dozerBuildPanel.gameObject;
        hud.dozerBuildBarracksButton         = btnDozerBuildBarracks.gameObject;
        hud.dozerBuildBarracksLabel          = btnDozerBuildBarracks.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.dozerBuildPowerPlantButton       = btnDozerBuildPower.gameObject;
        hud.dozerBuildPowerPlantLabel        = btnDozerBuildPower.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.dozerBuildVehicleFactoryButton   = btnDozerBuildVF.gameObject;
        hud.dozerBuildVehicleFactoryLabel    = btnDozerBuildVF.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.dozerBuildAirfieldButton         = btnDozerBuildAirfield.gameObject;
        hud.dozerBuildAirfieldLabel          = btnDozerBuildAirfield.GetComponentInChildren<TextMeshProUGUI>(true);
        EditorUtility.SetDirty(hud);

        // Wire buttons to RTSHUD callback methods.
        // Legacy instant-build buttons are only wired when the panel exists
        // (i.e. DebugInstantBuildEnabled = true).
        if (DebugInstantBuildEnabled)
        {
            WireButton(btnBarracks,       hud, nameof(RTSHUD.OnClickBuildBarracks));
            WireButton(btnPowerPlant,     hud, nameof(RTSHUD.OnClickBuildPowerPlant));
            WireButton(btnVehicleFactory, hud, nameof(RTSHUD.OnClickBuildVehicleFactory));
            WireButton(btnAirfield,       hud, nameof(RTSHUD.OnClickBuildAirfield));
        }
        WireButton(btnSoldier,        hud, nameof(RTSHUD.OnClickProduceSoldier));
        WireButton(btnRPGSoldier,     hud, nameof(RTSHUD.OnClickProduceRPGSoldier));
        WireButton(btnWorker,         hud, nameof(RTSHUD.OnClickProduceWorker));
        WireButton(btnDozer,          hud, nameof(RTSHUD.OnClickProduceDozer));
        WireButton(btnHumvee,         hud, nameof(RTSHUD.OnClickProduceHumvee));
        WireButton(btnTank,           hud, nameof(RTSHUD.OnClickProduceArtilleryTank));
        WireButton(btnStrikeJet,      hud, nameof(RTSHUD.OnClickProduceStrikeJet));
        WireButton(btnDozerBuildBarracks,       hud, nameof(RTSHUD.OnClickDozerBuildBarracks));
        WireButton(btnDozerBuildPower,          hud, nameof(RTSHUD.OnClickDozerBuildPowerPlant));
        WireButton(btnDozerBuildVF,             hud, nameof(RTSHUD.OnClickDozerBuildVehicleFactory));
        WireButton(btnDozerBuildAirfield,       hud, nameof(RTSHUD.OnClickDozerBuildAirfield));
        string buildSegment = DebugInstantBuildEnabled
            ? "Build(DEBUG): Barracks+PowerPlant+VehicleFactory+Airfield, "
            : "Build: <disabled>, ";
        Debug.Log("[SetupRTSHUD] ✓ Buttons wired — " + buildSegment +
                  "Production: Soldier+RPGSoldier+Worker+Dozer+Humvee+ArtilleryTank+StrikeJet, " +
                  "DozerBuild: Barracks+PowerPlant+VehicleFactory+Airfield");

        // Ensure the HUD renders on top of any other scene UI
        canvas.transform.SetAsLastSibling();

        // ── 6. Finalise ───────────────────────────────────────────────── //
        EditorUtility.SetDirty(canvas.gameObject);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        // ── 7. Debug dump — RectTransform values + canvas state ──────── //
        DumpHUDDebug(canvas, topBar, buildPanel);

        Debug.Log("[SetupRTSHUD] ── Done. Press Ctrl+S to save the scene. ────────");
    }

    // ================================================================== //
    // Debug dump
    // ================================================================== //

    private static void DumpHUDDebug(Canvas canvas, RectTransform topBar, RectTransform buildPanel)
    {
        Debug.Log($"[SetupRTSHUD][DEBUG] Canvas: renderMode={canvas.renderMode}, " +
                  $"sortingOrder={canvas.sortingOrder}, enabled={canvas.enabled}, " +
                  $"active={canvas.gameObject.activeInHierarchy}, " +
                  $"pixelPerfect={canvas.pixelPerfect}");

        Debug.Log($"[SetupRTSHUD][DEBUG] TopBar: anchorMin={topBar.anchorMin}, " +
                  $"anchorMax={topBar.anchorMax}, pivot={topBar.pivot}, " +
                  $"offsetMin={topBar.offsetMin}, offsetMax={topBar.offsetMax}, " +
                  $"sizeDelta={topBar.sizeDelta}, scale={topBar.localScale}, " +
                  $"active={topBar.gameObject.activeInHierarchy}");

        if (buildPanel == null)
        {
            Debug.Log("[SetupRTSHUD][DEBUG] BuildPanel: <not created — DebugInstantBuildEnabled = false>");
            return;
        }

        Debug.Log($"[SetupRTSHUD][DEBUG] BuildPanel: anchorMin={buildPanel.anchorMin}, " +
                  $"anchorMax={buildPanel.anchorMax}, pivot={buildPanel.pivot}, " +
                  $"anchoredPosition={buildPanel.anchoredPosition}, " +
                  $"sizeDelta={buildPanel.sizeDelta}, scale={buildPanel.localScale}, " +
                  $"active={buildPanel.gameObject.activeInHierarchy}");
    }

    // ================================================================== //
    // EventSystem
    // ================================================================== //

    private static void EnsureEventSystem()
    {
        if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null)
            return;

        GameObject esGO = new GameObject("EventSystem");
        esGO.AddComponent<EventSystem>();
        esGO.AddComponent<StandaloneInputModule>();
        Undo.RegisterCreatedObjectUndo(esGO, "Create EventSystem");

        Debug.Log("[SetupRTSHUD] ✓ EventSystem created");
    }

    // ================================================================== //
    // Canvas
    // ================================================================== //

    /// <summary>
    /// Always destroys any existing HUDCanvas and builds a fresh one. This guarantees
    /// stale anchors/sizes/children from a previous run cannot survive into Game View.
    /// </summary>
    private static Canvas RebuildCanvas()
    {
        GameObject existing = GameObject.Find("HUDCanvas");
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing);
            Debug.Log("[SetupRTSHUD]   Removed old HUDCanvas — rebuilding from scratch.");
        }

        GameObject go = new GameObject("HUDCanvas");
        Undo.RegisterCreatedObjectUndo(go, "Create HUDCanvas");

        // Canvas — Screen Space Overlay, sort order 999, pixel perfect off
        Canvas canvas       = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 999;
        canvas.pixelPerfect = false;
        canvas.enabled      = true;

        // CanvasScaler — Scale With Screen Size at 1920×1080, match 0.5
        CanvasScaler scaler        = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight  = 0.5f;

        // GraphicRaycaster — needed for buttons to receive click events
        go.AddComponent<GraphicRaycaster>();

        go.SetActive(true);
        go.transform.SetAsLastSibling();

        Debug.Log("[SetupRTSHUD] ✓ HUDCanvas created (Screen Space Overlay, sort order 999)");
        return canvas;
    }

    // ================================================================== //
    // Panel factory
    // ================================================================== //

    /// <summary>Creates a full-RectTransform + Image panel. Caller sets anchors.</summary>
    private static RectTransform CreatePanel(Transform parent, string name, Color color)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt  = go.AddComponent<RectTransform>();
        Image         img = go.AddComponent<Image>();
        img.color = color;

        return rt;
    }

    // ================================================================== //
    // TMP text factory
    // ================================================================== //

    private static TextMeshProUGUI CreateTMPText(
        RectTransform parent,
        string        name,
        string        text,
        Vector2       anchoredPos,
        Vector2       size,
        int           fontSize,
        TextAlignmentOptions alignment)
    {
        CheckTMPEssentials();

        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt   = go.AddComponent<RectTransform>();
        rt.anchorMin        = new Vector2(0f, 0.5f); // left-center anchor
        rt.anchorMax        = new Vector2(0f, 0.5f);
        rt.pivot            = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta        = size;

        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text            = text;
        tmp.fontSize        = fontSize;
        tmp.color           = Color.white;
        tmp.alignment       = alignment;
        tmp.raycastTarget   = false; // text never needs to intercept clicks

        return tmp;
    }

    // ================================================================== //
    // Button factory
    // ================================================================== //

    private static Button CreateButton(
        RectTransform parent,
        string        name,
        string        label,
        Color         btnColor,
        Vector2       anchoredPos,
        Vector2       size)
    {
        CheckTMPEssentials();

        // ── Root ──────────────────────────────────────────────────────── //
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin        = new Vector2(0.5f, 0.5f); // center-anchored inside the panel
        rt.anchorMax        = new Vector2(0.5f, 0.5f);
        rt.pivot            = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta        = size;
        rt.localScale       = Vector3.one;
        go.SetActive(true);

        Image bgImg     = go.AddComponent<Image>();
        bgImg.color     = btnColor;
        bgImg.raycastTarget = true; // button hit area

        Button btn = go.AddComponent<Button>();
        ColorBlock cb       = ColorBlock.defaultColorBlock;
        cb.normalColor      = btnColor;
        cb.highlightedColor = btnColor * 1.25f;
        cb.pressedColor     = btnColor * 0.70f;
        cb.selectedColor    = btnColor;
        cb.fadeDuration     = 0.08f;
        btn.colors          = cb;

        // ── Label (TMP child) ─────────────────────────────────────────── //
        GameObject textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);

        RectTransform textRT = textGO.AddComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;  // stretch to fill button
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero;
        textRT.offsetMax = Vector2.zero;

        TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text          = label;
        tmp.fontSize      = 22;
        tmp.color         = Color.white;
        tmp.alignment     = TextAlignmentOptions.Center;
        tmp.fontStyle     = FontStyles.Bold;
        tmp.raycastTarget = false; // let the Button's Image handle the hit

        return btn;
    }

    // ================================================================== //
    // GameManager helpers
    // ================================================================== //

    private static GameObject GetOrCreateGameManager()
    {
        GameObject gm = GameObject.Find("GameManager");
        if (gm != null)
            return gm;

        gm = new GameObject("GameManager");
        Undo.RegisterCreatedObjectUndo(gm, "Create GameManager");
        Debug.LogWarning(
            "[SetupRTSHUD] ⚠ GameManager was not found — created a new one.\n" +
            "  If your project already has a GameManager with a different name, " +
            "  manually move the RTSHUD component onto it and delete this duplicate.");
        return gm;
    }

    private static T GetOrAddComponent<T>(GameObject go) where T : Component
    {
        T comp = go.GetComponent<T>();
        if (comp != null)
            return comp;

        comp = go.AddComponent<T>();
        Debug.Log($"[SetupRTSHUD] ✓ Added {typeof(T).Name} to '{go.name}'");
        return comp;
    }

    // ================================================================== //
    // Button → RTSHUD wiring
    // ================================================================== //

    private static void WireButton(Button btn, RTSHUD hud, string methodName)
    {
        // Clear any existing persistent listeners first so re-running is safe
        SerializedObject   so    = new SerializedObject(btn);
        SerializedProperty calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
        if (calls != null)
        {
            calls.ClearArray();
            so.ApplyModifiedProperties();
        }

        // Resolve the method and create a bound delegate
        MethodInfo method = typeof(RTSHUD).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public);

        if (method == null)
        {
            Debug.LogError(
                $"[SetupRTSHUD] ✗ Method '{methodName}' not found on RTSHUD. " +
                $"Ensure the method is public and the script has compiled.");
            return;
        }

        UnityAction action =
            (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), hud, method);

        UnityEventTools.AddPersistentListener(btn.onClick, action);
        EditorUtility.SetDirty(btn);

        Debug.Log($"[SetupRTSHUD] ✓ {btn.name}.onClick → RTSHUD.{methodName}");
    }

    // ================================================================== //
    // Utilities
    // ================================================================== //

    private static void DestroyChildByName(Transform parent, string name)
    {
        Transform child = parent.Find(name);
        if (child == null)
            return;

        Undo.DestroyObjectImmediate(child.gameObject);
        Debug.Log($"[SetupRTSHUD]   Removed old '{name}' — rebuilding.");
    }

    private static bool _tmpWarningShown;

    private static void CheckTMPEssentials()
    {
        if (_tmpWarningShown) return;

        // TMP_Settings.defaultFontAsset is null when TMP Essential Resources
        // have never been imported into this project.
        if (TMP_Settings.defaultFontAsset == null)
        {
            Debug.LogWarning(
                "[SetupRTSHUD] ⚠ TMP Essentials not imported — text objects will be " +
                "invisible in Game view.\n" +
                "  Fix: Window → TextMeshPro → Import TMP Essential Resources, " +
                "then run Tools → RTS → Setup HUD again.");
            _tmpWarningShown = true;
        }
    }
}
