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
/// One-click editor tool that builds and wires the entire RTS gameplay HUD —
/// the polished bottom command bar with three boxed sections (Selected Info,
/// Command grid, Resources + Minimap) plus the floating boarding-cursor and
/// a top-down minimap camera in the scene.
///
/// Menu: Tools → RTS → Setup → Setup Gameplay HUD
/// Legacy alias: Tools → RTS → Setup HUD (kept for muscle-memory; calls
/// the new path).
///
/// Safe to re-run — the HUDCanvas is destroyed and rebuilt from scratch, and
/// the MinimapCamera GameObject is reused if it already exists.
///
/// What it creates
///   HUDCanvas                  Screen Space Overlay, sort 999, 1920×1080
///     BottomBar               full-width strip, 260 px tall
///       LeftSection           320×240 — selected-object info box
///         SinglePanel         portrait + name + HP bar + category + extras
///         GroupPanel          "X SELECTED" + type breakdown
///         EmptyPanel          neutral "No Selection" placeholder
///       CenterSection         1020×240 — command/production/transport grid
///         NeutralPanel        "No available commands" placeholder
///         ProductionPanel     9 production buttons (5+4 grid)
///         DozerBuildPanel     5 construction buttons (5-wide row)
///         TransportPanel      6 slot buttons + Unload All
///       RightSection          420×240 — resources/power + minimap
///         ResourcePowerPanel  Resources + Power text + low-power indicator
///         MinimapPanel        RawImage bound to MinimapCamera.OutputTexture
///     BoardingCursorIndicator 80×80, follows mouse during APC hover
///   MinimapCameraGO           orthographic top-down render camera
///   EventSystem               only if missing
///   GameManager               RTSHUD + TransportHoverIndicator added
/// </summary>
public static class SetupRTSHUD
{
    // ------------------------------------------------------------------ //
    // Palette — military command theme
    // ------------------------------------------------------------------ //

    // Outer frame around the whole bottom bar — thin olive border feel.
    private static readonly Color BarBorder     = new Color(0.30f, 0.36f, 0.22f, 1.00f);
    // Bar inner background — almost black with a green tint.
    private static readonly Color BarBg         = new Color(0.06f, 0.07f, 0.06f, 0.93f);
    // Each section box (left/center/right) sits inside the bar.
    private static readonly Color SectionBorder = new Color(0.40f, 0.46f, 0.30f, 1.00f);
    private static readonly Color SectionBg     = new Color(0.10f, 0.12f, 0.10f, 0.92f);
    // Sub-panel inset (portrait frame, HP bar, etc.).
    private static readonly Color InnerBg       = new Color(0.14f, 0.17f, 0.13f, 1.00f);
    private static readonly Color InnerBorder   = new Color(0.28f, 0.34f, 0.22f, 1.00f);

    private static readonly Color TitleAmber    = new Color(0.78f, 0.88f, 0.38f, 1.00f); // section titles
    private static readonly Color BodyText      = new Color(0.92f, 0.92f, 0.84f, 1.00f); // labels
    private static readonly Color DimText       = new Color(0.60f, 0.60f, 0.55f, 1.00f); // hp empty, secondary

    // Button category colours (preserved from prior pass so existing players
    // recognise the production icons).
    private static readonly Color BtnBarracksColor       = new Color(0.18f, 0.45f, 0.82f, 1f);
    private static readonly Color BtnPowerColor          = new Color(0.82f, 0.60f, 0.08f, 1f);
    private static readonly Color BtnVehicleFactoryColor = new Color(0.40f, 0.40f, 0.45f, 1f);
    private static readonly Color BtnAirfieldColor       = new Color(0.32f, 0.50f, 0.70f, 1f);
    private static readonly Color BtnMGDefenseColor      = new Color(0.55f, 0.25f, 0.20f, 1f);
    private static readonly Color BtnSoldierColor        = new Color(0.30f, 0.65f, 0.30f, 1f);
    private static readonly Color BtnRPGSoldierColor     = new Color(0.62f, 0.35f, 0.18f, 1f);
    private static readonly Color BtnWorkerColor         = new Color(0.78f, 0.50f, 0.18f, 1f);
    private static readonly Color BtnDozerColor          = new Color(0.92f, 0.72f, 0.12f, 1f);
    private static readonly Color BtnHumveeColor         = new Color(0.28f, 0.36f, 0.22f, 1f);
    private static readonly Color BtnTankColor           = new Color(0.18f, 0.28f, 0.16f, 1f);
    private static readonly Color BtnAPCColor            = new Color(0.36f, 0.44f, 0.24f, 1f);
    private static readonly Color BtnMissileLauncherColor = new Color(0.32f, 0.38f, 0.20f, 1f);
    private static readonly Color BtnStrikeJetColor      = new Color(0.45f, 0.55f, 0.65f, 1f);
    private static readonly Color BtnSlotColor           = new Color(0.30f, 0.40f, 0.20f, 1f);
    private static readonly Color BtnUnloadAllColor      = new Color(0.55f, 0.18f, 0.10f, 1f);

    // Bar layout constants (px in 1920×1080 reference).
    private const float BarHeight        = 260f;
    private const float LeftSectionWidth = 340f;
    private const float RightSectionWidth = 420f;
    private const float SectionGap       = 15f;
    private const float SectionPad       = 10f;
    private const float ButtonW          = 160f;
    private const float ButtonH          = 84f;
    private const float ButtonGapX       = 12f;
    private const float ButtonGapY       = 8f;
    // Top space reserved for the section title strip inside center-section panels.
    private const float CenterTitleStrip = 32f;

    // ------------------------------------------------------------------ //
    // Entry point — new menu path
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Setup/Setup Gameplay HUD")]
    public static void SetupHUD()
    {
        _tmpWarningShown = false;
        Debug.Log("[SetupRTSHUD] ── Building Gameplay HUD ──────────────────────");

        // ── 1. EventSystem ──────────────────────────────────────────── //
        EnsureEventSystem();

        // ── 2. HUDCanvas (destroy old, rebuild from scratch) ─────────── //
        Canvas canvas = RebuildCanvas();

        // ── 3. Bottom command bar (the persistent gameplay HUD strip) ── //
        // Simple single-panel bar (no nested border frame at the bar level —
        // each section inside gets its own border so the bar reads as a unified
        // dark strip with sectioned content rather than nested frames).
        RectTransform barRoot   = CreatePanel(canvas.transform, "BottomBar", BarBg);
        barRoot.anchorMin       = new Vector2(0f, 0f);
        barRoot.anchorMax       = new Vector2(1f, 0f);
        barRoot.pivot           = new Vector2(0.5f, 0f);
        barRoot.offsetMin       = new Vector2(0f,   0f);
        barRoot.offsetMax       = new Vector2(0f, BarHeight);
        barRoot.localScale      = Vector3.one;

        // The bar Image must NOT eat gameplay clicks — only the buttons inside
        // it should. EventSystem.IsPointerOverGameObject still returns true for
        // the buttons, so the click-blocker behaviour for UnitSelector remains
        // correct.
        DisableBarRaycast(barRoot);

        // Thin border accent strip along the top edge of the bar so the player
        // sees a clear separation between the gameplay viewport and the HUD.
        RectTransform topBorder = CreatePanel(barRoot, "TopBorder", BarBorder);
        topBorder.anchorMin = new Vector2(0f, 1f);
        topBorder.anchorMax = new Vector2(1f, 1f);
        topBorder.pivot     = new Vector2(0.5f, 1f);
        topBorder.offsetMin = new Vector2(0f, -2f);
        topBorder.offsetMax = new Vector2(0f, 0f);
        topBorder.GetComponent<Image>().raycastTarget = false;

        // All sections parent directly under the bar.
        Transform contentParent = barRoot;

        // ── 4. LEFT SECTION — Selected-object info box ───────────────── //
        RectTransform leftSection = CreateFramedSection(contentParent, "LeftSection",
                                                       SectionBorder, SectionBg, borderPx: 2f);
        AnchorAbsolute(leftSection,
                       x: SectionPad, y: SectionPad,
                       w: LeftSectionWidth, h: BarHeight - 2f * SectionPad);

        SelectedInfoPanelUI infoUI = BuildSelectedInfoPanel(leftSection);

        // ── 5. CENTER SECTION — command/production grid ──────────────── //
        // Width is whatever is left between left and right sections.
        // Anchor to stretch horizontally between them so widescreens benefit.
        float centerLeftPx  = SectionPad + LeftSectionWidth + SectionGap;
        float centerRightPx = SectionPad + RightSectionWidth + SectionGap;
        RectTransform centerSection = CreateFramedSection(contentParent, "CenterSection",
                                                         SectionBorder, SectionBg, borderPx: 2f);
        // Stretch horizontally inside the bar interior. offsetMin/Max set the
        // distance from the parent edges so the section auto-resizes if the
        // bar's reference resolution changes.
        centerSection.anchorMin = new Vector2(0f, 0f);
        centerSection.anchorMax = new Vector2(1f, 0f);
        centerSection.pivot     = new Vector2(0.5f, 0f);
        centerSection.offsetMin = new Vector2( centerLeftPx,  SectionPad);
        centerSection.offsetMax = new Vector2(-centerRightPx, SectionPad + (BarHeight - 2f * SectionPad));
        centerSection.localScale = Vector3.one;

        // Title strip — "COMMAND" header, top-anchored so it stays at the
        // top edge of the center section regardless of bar resizing.
        TextMeshProUGUI centerTitle = CreateTMPText(GetInner(centerSection), "Title", "COMMAND",
            anchoredPos: new Vector2(0f, 0f),
            size:        new Vector2(400f, 26f),
            fontSize:    16,
            alignment:   TextAlignmentOptions.Center);
        centerTitle.color    = TitleAmber;
        centerTitle.fontStyle = FontStyles.Bold;
        RectTransform centerTitleRT = centerTitle.rectTransform;
        centerTitleRT.anchorMin = new Vector2(0.5f, 1f);
        centerTitleRT.anchorMax = new Vector2(0.5f, 1f);
        centerTitleRT.pivot     = new Vector2(0.5f, 1f);
        centerTitleRT.anchoredPosition = new Vector2(0f, -4f);

        // -- 5a. NeutralPanel (drawn first, behind the others) -----------
        RectTransform neutralPanel = CreateInset(GetInner(centerSection), "NeutralPanel");
        StretchInside(neutralPanel, padding: 14f);
        InsetTitleStrip(neutralPanel);
        TextMeshProUGUI neutralLbl = CreateTMPText(neutralPanel, "Label", "No available commands",
            anchoredPos: Vector2.zero,
            size:        new Vector2(600f, 40f),
            fontSize:    22,
            alignment:   TextAlignmentOptions.Center);
        neutralLbl.color = DimText;

        // -- 5b. ProductionPanel ----------------------------------------
        RectTransform productionPanel = CreateInset(GetInner(centerSection), "ProductionPanel");
        StretchInside(productionPanel, padding: 14f);
        InsetTitleStrip(productionPanel);
        productionPanel.gameObject.SetActive(false);

        // 9 buttons in a 5×2 grid centred horizontally.
        // Row 0 (top): Soldier, RPG, Worker, Dozer, Humvee
        // Row 1 (bot): APC,     Tank, Missile, Strike
        Button btnSoldier         = CreateIconButton(productionPanel, "BtnSoldier",
                                        "Soldier", "50", BtnSoldierColor,        GridPos(0, 0, 5, 2));
        Button btnRPGSoldier      = CreateIconButton(productionPanel, "BtnRPGSoldier",
                                        "RPG Soldier", "120", BtnRPGSoldierColor, GridPos(1, 0, 5, 2));
        Button btnWorker          = CreateIconButton(productionPanel, "BtnWorker",
                                        "Worker", "75", BtnWorkerColor,           GridPos(2, 0, 5, 2));
        Button btnDozer           = CreateIconButton(productionPanel, "BtnDozer",
                                        "Dozer", "150", BtnDozerColor,            GridPos(3, 0, 5, 2));
        Button btnHumvee          = CreateIconButton(productionPanel, "BtnHumvee",
                                        "Humvee", "150", BtnHumveeColor,          GridPos(4, 0, 5, 2));
        Button btnAPC             = CreateIconButton(productionPanel, "BtnAPC",
                                        "APC", "600", BtnAPCColor,                GridPos(0, 1, 5, 2));
        Button btnTank            = CreateIconButton(productionPanel, "BtnArtilleryTank",
                                        "Artillery Tank", "350", BtnTankColor,    GridPos(1, 1, 5, 2));
        Button btnMissileLauncher = CreateIconButton(productionPanel, "BtnMissileLauncher",
                                        "Missile Launcher", "1100", BtnMissileLauncherColor, GridPos(2, 1, 5, 2));
        Button btnStrikeJet       = CreateIconButton(productionPanel, "BtnStrikeJet",
                                        "Strike Jet", "450", BtnStrikeJetColor,    GridPos(3, 1, 5, 2));
        Debug.Log("[SetupRTSHUD] ✓ ProductionPanel (9 icon buttons, 5×2 grid)");

        // -- 5c. DozerBuildPanel ----------------------------------------
        RectTransform dozerBuildPanel = CreateInset(GetInner(centerSection), "DozerBuildPanel");
        StretchInside(dozerBuildPanel, padding: 14f);
        InsetTitleStrip(dozerBuildPanel);
        dozerBuildPanel.gameObject.SetActive(false);

        // Phase 10: 6 buttons in a single row. CommandCenter at index 0 since
        // it's the canonical "build this first" entry in the new MP flow.
        Button btnDozerBuildCC       = CreateIconButton(dozerBuildPanel, "BtnDozerBuildCommandCenter",
                                          "Command Center", "1000", BtnBarracksColor,         GridPos(0, 0, 6, 1));
        Button btnDozerBuildBarracks = CreateIconButton(dozerBuildPanel, "BtnDozerBuildBarracks",
                                          "Barracks", "100", BtnBarracksColor,                GridPos(1, 0, 6, 1));
        Button btnDozerBuildPower    = CreateIconButton(dozerBuildPanel, "BtnDozerBuildPowerPlant",
                                          "Power Plant", "150", BtnPowerColor,                GridPos(2, 0, 6, 1));
        Button btnDozerBuildVF       = CreateIconButton(dozerBuildPanel, "BtnDozerBuildVehicleFactory",
                                          "Vehicle Factory", "300", BtnVehicleFactoryColor,   GridPos(3, 0, 6, 1));
        Button btnDozerBuildAirfield = CreateIconButton(dozerBuildPanel, "BtnDozerBuildAirfield",
                                          "Airfield", "600", BtnAirfieldColor,                GridPos(4, 0, 6, 1));
        Button btnDozerBuildMGD      = CreateIconButton(dozerBuildPanel, "BtnDozerBuildMachineGunDefense",
                                          "MG Defense", "250", BtnMGDefenseColor,             GridPos(5, 0, 6, 1));
        Debug.Log("[SetupRTSHUD] ✓ DozerBuildPanel (6 icon buttons, single row)");

        // -- 5d. TransportPanel -----------------------------------------
        RectTransform transportPanel = CreateInset(GetInner(centerSection), "TransportPanel");
        StretchInside(transportPanel, padding: 14f);
        InsetTitleStrip(transportPanel);
        transportPanel.gameObject.SetActive(false);

        TextMeshProUGUI transportTitle = CreateTMPText(transportPanel, "TransportTitle",
            "Transport (0/6)",
            anchoredPos: new Vector2(0f, 0f),
            size:        new Vector2(300f, 26f),
            fontSize:    18,
            alignment:   TextAlignmentOptions.Center);
        transportTitle.color    = TitleAmber;
        transportTitle.fontStyle = FontStyles.Bold;
        RectTransform transportTitleRT = transportTitle.rectTransform;
        transportTitleRT.anchorMin = new Vector2(0.5f, 1f);
        transportTitleRT.anchorMax = new Vector2(0.5f, 1f);
        transportTitleRT.pivot     = new Vector2(0.5f, 1f);
        transportTitleRT.anchoredPosition = new Vector2(0f, -4f);

        // 6 slot buttons (3×2 grid). Slot buttons get a tall narrow icon-on-top
        // layout via CreateSlotButton. Sizes are tuned to fit within the
        // inset alongside the title + Unload All button.
        Button[]          slotButtons = new Button[6];
        TextMeshProUGUI[] slotLabels  = new TextMeshProUGUI[6];
        for (int i = 0; i < 6; i++)
        {
            int col = i % 3;
            int row = i / 3;
            float x = (col - 1) * 90f;
            float y = row == 0 ? 28f : -25f;
            Button btn = CreateSlotButton(transportPanel, $"BtnSlot_{i}",
                                          BtnSlotColor,
                                          anchoredPos: new Vector2(x, y),
                                          size:        new Vector2(82f, 50f));
            slotButtons[i] = btn;
            slotLabels[i]  = btn.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        // Unload All beneath the slot grid — small bar form so it fits inside
        // the inset alongside title + slots.
        Button btnUnloadAll = CreateUnloadAllButton(transportPanel,
                                  anchoredPos: new Vector2(0f, -72f));
        Debug.Log("[SetupRTSHUD] ✓ TransportPanel (6 slots + Unload All)");

        // ── 6. RIGHT SECTION — Resource/Power + Minimap ──────────────── //
        RectTransform rightSection = CreateFramedSection(contentParent, "RightSection",
                                                        SectionBorder, SectionBg, borderPx: 2f);
        // Right-anchored to the bar interior.
        rightSection.anchorMin = new Vector2(1f, 0f);
        rightSection.anchorMax = new Vector2(1f, 0f);
        rightSection.pivot     = new Vector2(1f, 0f);
        rightSection.anchoredPosition = new Vector2(-SectionPad, SectionPad);
        rightSection.sizeDelta = new Vector2(RightSectionWidth, BarHeight - 2f * SectionPad);
        rightSection.localScale = Vector3.one;

        (ResourcePowerPanelUI rpUI, TextMeshProUGUI resourcesText, TextMeshProUGUI powerText)
            = BuildResourcePowerPanel(GetInner(rightSection));

        MiniMapPanelUI miniUI = BuildMinimapPanel(GetInner(rightSection));

        // ── 7. Boarding cursor indicator (free-floating on canvas) ───── //
        RectTransform boardingIndicator = CreatePanel(canvas.transform, "BoardingCursorIndicator",
            new Color(0.20f, 0.75f, 0.30f, 0.85f));
        boardingIndicator.anchorMin        = new Vector2(0f, 0f);
        boardingIndicator.anchorMax        = new Vector2(0f, 0f);
        boardingIndicator.pivot            = new Vector2(0.5f, 0.5f);
        boardingIndicator.sizeDelta        = new Vector2(80f, 80f);
        boardingIndicator.localScale       = Vector3.one;
        boardingIndicator.anchoredPosition = new Vector2(-200f, -200f);
        boardingIndicator.gameObject.SetActive(false);

        Image boardingBg = boardingIndicator.GetComponent<Image>();
        // Same flicker-safety as before — the cursor itself must not block raycasts.
        boardingBg.raycastTarget = false;

        TextMeshProUGUI boardingArrow = CreateTMPText(boardingIndicator, "Arrow", "▲",
            anchoredPos: new Vector2(0f, 16f),
            size:        new Vector2(60f, 36f),
            fontSize:    32,
            alignment:   TextAlignmentOptions.Center);
        boardingArrow.color = Color.white;

        TextMeshProUGUI boardingLabel = CreateTMPText(boardingIndicator, "Label", "ENTER",
            anchoredPos: new Vector2(0f, -18f),
            size:        new Vector2(70f, 24f),
            fontSize:    16,
            alignment:   TextAlignmentOptions.Center);
        boardingLabel.color     = Color.white;
        boardingLabel.fontStyle = FontStyles.Bold;
        Debug.Log("[SetupRTSHUD] ✓ BoardingCursorIndicator (80×80, hidden by default)");

        // ── 8. MinimapCamera in scene ────────────────────────────────── //
        MinimapCamera minimapCam = EnsureMinimapCamera();
        miniUI.source = minimapCam;

        // ── 9. GameManager wiring (RTSHUD + TransportHoverIndicator) ── //
        GameObject gm  = GetOrCreateGameManager();
        RTSHUD     hud = GetOrAddComponent<RTSHUD>(gm);

        if (UnityEngine.Object.FindAnyObjectByType<BuildingPlacementManager>() == null)
            Debug.LogWarning(
                "[SetupRTSHUD] ⚠ No BuildingPlacementManager found in the scene.\n" +
                "  Add it to GameManager and assign its Inspector fields.\n" +
                "  Build buttons will log an error at runtime until this is fixed.");

        // Text references (these fields already existed on RTSHUD; their content
        // is still owned by RTSHUD.RefreshResources/RefreshPower).
        hud.resourcesText       = resourcesText;
        hud.powerText           = powerText;

        // Production panel (group)
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
        hud.apcButton                  = btnAPC.gameObject;
        hud.apcButtonLabel             = btnAPC.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.tankButton                = btnTank.gameObject;
        hud.tankButtonLabel           = btnTank.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.missileLauncherButton      = btnMissileLauncher.gameObject;
        hud.missileLauncherButtonLabel = btnMissileLauncher.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.strikeJetButton           = btnStrikeJet.gameObject;
        hud.strikeJetButtonLabel      = btnStrikeJet.GetComponentInChildren<TextMeshProUGUI>(true);

        // Dozer build panel (group)
        hud.dozerBuildPanel                   = dozerBuildPanel.gameObject;
        hud.dozerBuildBarracksButton          = btnDozerBuildBarracks.gameObject;
        hud.dozerBuildBarracksLabel           = btnDozerBuildBarracks.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.dozerBuildPowerPlantButton        = btnDozerBuildPower.gameObject;
        hud.dozerBuildPowerPlantLabel         = btnDozerBuildPower.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.dozerBuildVehicleFactoryButton    = btnDozerBuildVF.gameObject;
        hud.dozerBuildVehicleFactoryLabel     = btnDozerBuildVF.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.dozerBuildAirfieldButton          = btnDozerBuildAirfield.gameObject;
        hud.dozerBuildAirfieldLabel           = btnDozerBuildAirfield.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.dozerBuildMachineGunDefenseButton = btnDozerBuildMGD.gameObject;
        hud.dozerBuildMachineGunDefenseLabel  = btnDozerBuildMGD.GetComponentInChildren<TextMeshProUGUI>(true);
        hud.dozerBuildCommandCenterButton     = btnDozerBuildCC.gameObject;
        hud.dozerBuildCommandCenterLabel      = btnDozerBuildCC.GetComponentInChildren<TextMeshProUGUI>(true);

        // Transport panel (group)
        hud.transportPanel             = transportPanel.gameObject;
        hud.transportTitleLabel        = transportTitle;
        hud.transportSlotButtons       = new GameObject[6];
        hud.transportSlotLabels        = new TextMeshProUGUI[6];
        for (int i = 0; i < 6; i++)
        {
            hud.transportSlotButtons[i] = slotButtons[i].gameObject;
            hud.transportSlotLabels[i]  = slotLabels[i];
        }
        hud.transportUnloadAllButton   = btnUnloadAll.gameObject;

        // New HUD modules
        hud.bottomBarRoot      = barRoot.gameObject;
        hud.selectedInfoPanel  = infoUI;
        hud.resourcePowerPanel = rpUI;
        hud.miniMapPanel       = miniUI;
        EditorUtility.SetDirty(hud);

        // TransportHoverIndicator — same wiring as before.
        TransportHoverIndicator hover = GetOrAddComponent<TransportHoverIndicator>(gm);
        hover.indicatorRoot       = boardingIndicator;
        hover.indicatorBackground = boardingBg;
        hover.indicatorArrow      = boardingArrow;
        hover.indicatorLabel      = boardingLabel;
        EditorUtility.SetDirty(hover);

        // ── 10. Wire all button onClick to RTSHUD methods ───────────── //
        WireButton(btnSoldier,         hud, nameof(RTSHUD.OnClickProduceSoldier));
        WireButton(btnRPGSoldier,      hud, nameof(RTSHUD.OnClickProduceRPGSoldier));
        WireButton(btnWorker,          hud, nameof(RTSHUD.OnClickProduceWorker));
        WireButton(btnDozer,           hud, nameof(RTSHUD.OnClickProduceDozer));
        WireButton(btnHumvee,          hud, nameof(RTSHUD.OnClickProduceHumvee));
        WireButton(btnAPC,             hud, nameof(RTSHUD.OnClickProduceAPC));
        WireButton(btnTank,            hud, nameof(RTSHUD.OnClickProduceArtilleryTank));
        WireButton(btnMissileLauncher, hud, nameof(RTSHUD.OnClickProduceMissileLauncher));
        WireButton(btnStrikeJet,       hud, nameof(RTSHUD.OnClickProduceStrikeJet));
        WireButton(btnDozerBuildBarracks, hud, nameof(RTSHUD.OnClickDozerBuildBarracks));
        WireButton(btnDozerBuildPower,    hud, nameof(RTSHUD.OnClickDozerBuildPowerPlant));
        WireButton(btnDozerBuildVF,       hud, nameof(RTSHUD.OnClickDozerBuildVehicleFactory));
        WireButton(btnDozerBuildAirfield, hud, nameof(RTSHUD.OnClickDozerBuildAirfield));
        WireButton(btnDozerBuildMGD,      hud, nameof(RTSHUD.OnClickDozerBuildMachineGunDefense));
        WireButton(btnDozerBuildCC,       hud, nameof(RTSHUD.OnClickDozerBuildCommandCenter));
        WireButton(btnUnloadAll,       hud, nameof(RTSHUD.OnClickUnloadAll));
        WireButton(slotButtons[0],     hud, nameof(RTSHUD.OnClickUnloadSlot0));
        WireButton(slotButtons[1],     hud, nameof(RTSHUD.OnClickUnloadSlot1));
        WireButton(slotButtons[2],     hud, nameof(RTSHUD.OnClickUnloadSlot2));
        WireButton(slotButtons[3],     hud, nameof(RTSHUD.OnClickUnloadSlot3));
        WireButton(slotButtons[4],     hud, nameof(RTSHUD.OnClickUnloadSlot4));
        WireButton(slotButtons[5],     hud, nameof(RTSHUD.OnClickUnloadSlot5));

        // Re-wire MainMenuController.hudCanvas → the freshly-built HUDCanvas.
        // Without this, re-running Setup Gameplay HUD destroys the old canvas
        // and leaves the menu controller's reference pointing at a missing
        // object — the HUD then fails to auto-hide while the menu is open.
        MainMenuController menu = UnityEngine.Object.FindAnyObjectByType<MainMenuController>(
            FindObjectsInactive.Include);
        if (menu != null)
        {
            menu.hudCanvas = canvas.gameObject;
            EditorUtility.SetDirty(menu);
            Debug.Log("[SetupRTSHUD] ✓ MainMenuController.hudCanvas re-wired to the new HUDCanvas.");
        }
        else
        {
            Debug.Log("[SetupRTSHUD]   No MainMenuController in scene — HUD will stay visible by " +
                      "default (test-scene mode).");
        }

        // Ensure the HUD renders on top of any other UI in the scene.
        canvas.transform.SetAsLastSibling();

        // ── 11. Finalise ────────────────────────────────────────────── //
        EditorUtility.SetDirty(canvas.gameObject);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log("[SetupRTSHUD] ── Done. Press Ctrl+S to save the scene. ────────");
    }

    // ------------------------------------------------------------------ //
    // Legacy menu — points to the new builder so existing muscle memory
    // (Tools → RTS → Setup HUD) continues to work without two divergent paths.
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Setup HUD")]
    public static void SetupHUD_LegacyAlias() => SetupHUD();

    // ================================================================== //
    // Repair tool
    // ================================================================== //

    [MenuItem("Tools/RTS/Setup/Repair Gameplay HUD References")]
    public static void RepairReferences()
    {
        Debug.Log("[SetupRTSHUD] ── Repair Gameplay HUD References ──");

        RTSHUD hud = UnityEngine.Object.FindAnyObjectByType<RTSHUD>();
        if (hud == null)
        {
            Debug.LogWarning("[SetupRTSHUD] No RTSHUD in scene — running full Setup instead.");
            SetupHUD();
            return;
        }

        // Re-binds module refs if they got nulled by a manual hierarchy edit.
        if (hud.selectedInfoPanel == null)
            hud.selectedInfoPanel  = UnityEngine.Object.FindAnyObjectByType<SelectedInfoPanelUI>();
        if (hud.resourcePowerPanel == null)
            hud.resourcePowerPanel = UnityEngine.Object.FindAnyObjectByType<ResourcePowerPanelUI>();
        if (hud.miniMapPanel == null)
            hud.miniMapPanel       = UnityEngine.Object.FindAnyObjectByType<MiniMapPanelUI>();

        MinimapCamera mc = UnityEngine.Object.FindAnyObjectByType<MinimapCamera>();
        if (mc != null && hud.miniMapPanel != null)
            hud.miniMapPanel.source = mc;

        EditorUtility.SetDirty(hud);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[SetupRTSHUD] Repair done.");
    }

    // ================================================================== //
    // Section: Selected Info Panel layout
    // ================================================================== //

    private static SelectedInfoPanelUI BuildSelectedInfoPanel(RectTransform leftSection)
    {
        RectTransform inner = GetInner(leftSection);

        // The component lives on the section itself (not on a child) so it
        // shows up in the Inspector right next to the section in the
        // hierarchy. The sub-views are children of `inner`.
        SelectedInfoPanelUI ui = leftSection.gameObject.GetComponent<SelectedInfoPanelUI>()
                              ?? leftSection.gameObject.AddComponent<SelectedInfoPanelUI>();

        // Section title — pinned to the top edge of the inner panel via
        // anchor (0.5, 1) so it stays put regardless of inner height.
        TextMeshProUGUI title = CreateTMPText(inner, "Title", "SELECTED",
            anchoredPos: new Vector2(0f, 0f),
            size:        new Vector2(300f, 24f),
            fontSize:    16,
            alignment:   TextAlignmentOptions.Center);
        title.color    = TitleAmber;
        title.fontStyle = FontStyles.Bold;
        // Re-anchor to top-centre so it doesn't drift off-panel if the parent
        // rect ever resizes. anchoredPosition becomes "offset down from top".
        RectTransform titleRT = title.rectTransform;
        titleRT.anchorMin = new Vector2(0.5f, 1f);
        titleRT.anchorMax = new Vector2(0.5f, 1f);
        titleRT.pivot     = new Vector2(0.5f, 1f);
        titleRT.anchoredPosition = new Vector2(0f, -4f);

        // -- Single view -----------------------------------------------
        RectTransform single = CreateInset(inner, "SinglePanel");
        StretchInside(single, padding: 8f);
        single.offsetMax = new Vector2(single.offsetMax.x, single.offsetMax.y - 28f); // leave room for title

        // Portrait frame (130×130) at top-left.
        RectTransform portraitWrap = CreatePanel(single, "PortraitFrame", InnerBorder);
        portraitWrap.anchorMin = new Vector2(0f, 1f);
        portraitWrap.anchorMax = new Vector2(0f, 1f);
        portraitWrap.pivot     = new Vector2(0f, 1f);
        portraitWrap.anchoredPosition = new Vector2(8f, -8f);
        portraitWrap.sizeDelta = new Vector2(110f, 110f);
        portraitWrap.GetComponent<Image>().raycastTarget = false;

        RectTransform portraitInner = CreatePanel(portraitWrap, "PortraitInner", new Color(0.30f, 0.40f, 0.30f, 1f));
        portraitInner.anchorMin = Vector2.zero;
        portraitInner.anchorMax = Vector2.one;
        portraitInner.offsetMin = new Vector2(3f, 3f);
        portraitInner.offsetMax = new Vector2(-3f, -3f);
        Image portraitImg = portraitInner.GetComponent<Image>();
        portraitImg.raycastTarget = false;

        // Name + category + HP to the right of the portrait. All positions
        // are relative to the single panel's CENTER (anchor 0.5,0.5). Right
        // half of the panel ≈ x ∈ [+0, +160] with portrait occupying the left.
        TextMeshProUGUI nameLbl = CreateTMPText(single, "Name", "Soldier",
            anchoredPos: new Vector2(55f, 55f),
            size:        new Vector2(180f, 28f),
            fontSize:    20,
            alignment:   TextAlignmentOptions.MidlineLeft);
        nameLbl.color    = BodyText;
        nameLbl.fontStyle = FontStyles.Bold;

        TextMeshProUGUI catLbl = CreateTMPText(single, "Category", "Category: Infantry",
            anchoredPos: new Vector2(55f, 28f),
            size:        new Vector2(180f, 22f),
            fontSize:    14,
            alignment:   TextAlignmentOptions.MidlineLeft);
        catLbl.color = DimText;

        // HP bar (frame + fill + value label).
        RectTransform hpFrame = CreatePanel(single, "HPFrame", InnerBorder);
        hpFrame.anchorMin = new Vector2(0.5f, 0.5f);
        hpFrame.anchorMax = new Vector2(0.5f, 0.5f);
        hpFrame.pivot     = new Vector2(0.5f, 0.5f);
        hpFrame.anchoredPosition = new Vector2(55f, -5f);
        hpFrame.sizeDelta = new Vector2(180f, 16f);
        hpFrame.GetComponent<Image>().raycastTarget = false;

        RectTransform hpFillRT = CreatePanel(hpFrame, "Fill", new Color(0.40f, 0.85f, 0.40f, 1f));
        hpFillRT.anchorMin = Vector2.zero;
        hpFillRT.anchorMax = Vector2.one;
        hpFillRT.offsetMin = new Vector2(2f, 2f);
        hpFillRT.offsetMax = new Vector2(-2f, -2f);
        Image hpFillImg = hpFillRT.GetComponent<Image>();
        hpFillImg.type        = Image.Type.Filled;
        hpFillImg.fillMethod  = Image.FillMethod.Horizontal;
        hpFillImg.fillOrigin  = (int)Image.OriginHorizontal.Left;
        hpFillImg.fillAmount  = 1f;
        hpFillImg.raycastTarget = false;

        TextMeshProUGUI hpValue = CreateTMPText(single, "HPValue", "100 / 100",
            anchoredPos: new Vector2(55f, -28f),
            size:        new Vector2(180f, 18f),
            fontSize:    13,
            alignment:   TextAlignmentOptions.MidlineLeft);
        hpValue.color = BodyText;

        // Extra info line below the portrait (passenger count, etc.).
        TextMeshProUGUI extraLbl = CreateTMPText(single, "Extra", "",
            anchoredPos: new Vector2(0f, -65f),
            size:        new Vector2(310f, 22f),
            fontSize:    13,
            alignment:   TextAlignmentOptions.Center);
        extraLbl.color = TitleAmber;
        extraLbl.gameObject.SetActive(false);

        // -- Group view -------------------------------------------------
        RectTransform group = CreateInset(inner, "GroupPanel");
        StretchInside(group, padding: 8f);
        group.offsetMax = new Vector2(group.offsetMax.x, group.offsetMax.y - 28f);
        group.gameObject.SetActive(false);

        TextMeshProUGUI groupHeader = CreateTMPText(group, "Header", "0 SELECTED",
            anchoredPos: new Vector2(0f, 70f),
            size:        new Vector2(300f, 32f),
            fontSize:    22,
            alignment:   TextAlignmentOptions.Center);
        groupHeader.color    = TitleAmber;
        groupHeader.fontStyle = FontStyles.Bold;

        TextMeshProUGUI groupSummary = CreateTMPText(group, "Summary", "",
            anchoredPos: new Vector2(0f, -10f),
            size:        new Vector2(300f, 130f),
            fontSize:    16,
            alignment:   TextAlignmentOptions.Center);
        groupSummary.color = BodyText;

        // -- Empty view -------------------------------------------------
        RectTransform empty = CreateInset(inner, "EmptyPanel");
        StretchInside(empty, padding: 8f);
        empty.offsetMax = new Vector2(empty.offsetMax.x, empty.offsetMax.y - 28f);

        TextMeshProUGUI emptyLbl = CreateTMPText(empty, "Label", "NO SELECTION",
            anchoredPos: Vector2.zero,
            size:        new Vector2(300f, 30f),
            fontSize:    18,
            alignment:   TextAlignmentOptions.Center);
        emptyLbl.color = DimText;

        // Wire references onto the component.
        ui.singleRoot       = single.gameObject;
        ui.groupRoot        = group.gameObject;
        ui.emptyRoot        = empty.gameObject;
        ui.portraitFrame    = portraitImg;
        ui.nameLabel        = nameLbl;
        ui.categoryLabel    = catLbl;
        ui.hpValueLabel     = hpValue;
        ui.hpBarFill        = hpFillImg;
        ui.extraInfoLabel   = extraLbl;
        ui.groupHeaderLabel = groupHeader;
        ui.groupSummaryLabel = groupSummary;
        EditorUtility.SetDirty(ui);

        Debug.Log("[SetupRTSHUD] ✓ SelectedInfoPanelUI built (Single + Group + Empty)");
        return ui;
    }

    // ================================================================== //
    // Section: Resource / Power panel layout
    // ================================================================== //

    private static (ResourcePowerPanelUI, TextMeshProUGUI, TextMeshProUGUI)
        BuildResourcePowerPanel(RectTransform rightInner)
    {
        // Top half of right column — Resources + Power.
        RectTransform rp = CreateInset(rightInner, "ResourcePowerPanel");
        rp.anchorMin = new Vector2(0f, 1f);
        rp.anchorMax = new Vector2(1f, 1f);
        rp.pivot     = new Vector2(0.5f, 1f);
        rp.offsetMin = new Vector2(8f, -78f);
        rp.offsetMax = new Vector2(-8f, -6f);

        TextMeshProUGUI title = CreateTMPText(rp, "Title", "ECONOMY",
            anchoredPos: new Vector2(0f, 22f),
            size:        new Vector2(300f, 22f),
            fontSize:    14,
            alignment:   TextAlignmentOptions.Center);
        title.color    = TitleAmber;
        title.fontStyle = FontStyles.Bold;

        TextMeshProUGUI resources = CreateTMPText(rp, "ResourcesText", "Resources: 0",
            anchoredPos: new Vector2(-90f, -5f),
            size:        new Vector2(200f, 24f),
            fontSize:    16,
            alignment:   TextAlignmentOptions.MidlineLeft);
        resources.color = BodyText;

        TextMeshProUGUI power = CreateTMPText(rp, "PowerText", "Power: 0 / 0",
            anchoredPos: new Vector2(-90f, -22f),
            size:        new Vector2(200f, 24f),
            fontSize:    16,
            alignment:   TextAlignmentOptions.MidlineLeft);
        power.color = BodyText;

        // Low-power indicator — a small red square that pulses when power < demand.
        RectTransform lowFrame = CreatePanel(rp, "LowPowerIndicator", new Color(0.85f, 0.2f, 0.15f, 0.85f));
        lowFrame.anchorMin = new Vector2(1f, 0.5f);
        lowFrame.anchorMax = new Vector2(1f, 0.5f);
        lowFrame.pivot     = new Vector2(1f, 0.5f);
        lowFrame.anchoredPosition = new Vector2(-12f, -12f);
        lowFrame.sizeDelta = new Vector2(24f, 24f);
        Image lowImg = lowFrame.GetComponent<Image>();
        lowImg.raycastTarget = false;
        lowFrame.gameObject.SetActive(false);

        ResourcePowerPanelUI ui = rp.gameObject.AddComponent<ResourcePowerPanelUI>();
        ui.resourcesText      = resources;
        ui.powerText          = power;
        ui.lowPowerIndicator  = lowImg;
        EditorUtility.SetDirty(ui);

        Debug.Log("[SetupRTSHUD] ✓ ResourcePowerPanelUI built");
        return (ui, resources, power);
    }

    // ================================================================== //
    // Section: Minimap panel layout
    // ================================================================== //

    private static MiniMapPanelUI BuildMinimapPanel(RectTransform rightInner)
    {
        // Bottom half of right column — Minimap.
        RectTransform mm = CreateInset(rightInner, "MinimapPanel");
        mm.anchorMin = new Vector2(0f, 0f);
        mm.anchorMax = new Vector2(1f, 0f);
        mm.pivot     = new Vector2(0.5f, 0f);
        mm.offsetMin = new Vector2(8f, 6f);
        mm.offsetMax = new Vector2(-8f, 158f);

        TextMeshProUGUI title = CreateTMPText(mm, "Title", "TACTICAL MAP",
            anchoredPos: new Vector2(0f, 60f),
            size:        new Vector2(300f, 20f),
            fontSize:    13,
            alignment:   TextAlignmentOptions.Center);
        title.color    = TitleAmber;
        title.fontStyle = FontStyles.Bold;

        // RawImage display sized to match the inner of the panel (minus title).
        GameObject rawGO = new GameObject("Display");
        rawGO.transform.SetParent(mm, false);
        Undo.RegisterCreatedObjectUndo(rawGO, "Create MinimapDisplay");
        RectTransform rawRT = rawGO.AddComponent<RectTransform>();
        rawRT.anchorMin = new Vector2(0f, 0f);
        rawRT.anchorMax = new Vector2(1f, 1f);
        rawRT.offsetMin = new Vector2(6f, 6f);
        rawRT.offsetMax = new Vector2(-6f, -22f); // leave 22 px at top for title

        RawImage raw = rawGO.AddComponent<RawImage>();
        raw.color = Color.white;
        raw.raycastTarget = true; // needed for click-to-recentre

        MiniMapPanelUI ui = mm.gameObject.AddComponent<MiniMapPanelUI>();
        ui.display = raw;
        // source is wired up by SetupHUD() right after this returns.
        EditorUtility.SetDirty(ui);

        Debug.Log("[SetupRTSHUD] ✓ MiniMapPanelUI built (RawImage display)");
        return ui;
    }

    // ================================================================== //
    // Scene: Minimap camera
    // ================================================================== //

    private static MinimapCamera EnsureMinimapCamera()
    {
        MinimapCamera existing = UnityEngine.Object.FindAnyObjectByType<MinimapCamera>();
        if (existing != null)
        {
            Debug.Log("[SetupRTSHUD]   MinimapCamera already in scene — reusing.");
            return existing;
        }

        GameObject go = new GameObject("MinimapCameraGO");
        Undo.RegisterCreatedObjectUndo(go, "Create MinimapCamera");

        Camera cam = go.AddComponent<Camera>();
        cam.tag = "Untagged";
        cam.clearFlags = CameraClearFlags.SolidColor;

        MinimapCamera mc = go.AddComponent<MinimapCamera>();

        // Centre on the scene's ground / origin so first-run framing is sane.
        Vector3 worldCentre = ResolveSceneCentre();
        mc.CentreOn(worldCentre);

        Debug.Log($"[SetupRTSHUD] ✓ MinimapCamera created at {go.transform.position}");
        return mc;
    }

    /// <summary>
    /// Best-effort guess of the playable map centre: terrain bounds if present,
    /// otherwise the first GameObject called "Ground", otherwise world origin.
    /// </summary>
    private static Vector3 ResolveSceneCentre()
    {
        Terrain t = UnityEngine.Object.FindAnyObjectByType<Terrain>();
        if (t != null)
        {
            Vector3 size = t.terrainData.size;
            return t.transform.position + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f);
        }
        GameObject ground = GameObject.Find("Ground");
        if (ground != null) return ground.transform.position;
        return Vector3.zero;
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

        Canvas canvas       = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 999;
        canvas.pixelPerfect = false;
        canvas.enabled      = true;

        CanvasScaler scaler        = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight  = 0.5f;

        go.AddComponent<GraphicRaycaster>();
        go.SetActive(true);
        go.transform.SetAsLastSibling();

        Debug.Log("[SetupRTSHUD] ✓ HUDCanvas created (Screen Space Overlay, sort 999)");
        return canvas;
    }

    // ================================================================== //
    // Layout primitives
    // ================================================================== //

    /// <summary>
    /// A "section" is two stacked panels — a thin border Image with an inner
    /// inset Image. Children of the inner panel get the bg color while the
    /// outer panel acts as the visible frame.
    /// </summary>
    private static RectTransform CreateFramedSection(Transform parent, string name,
                                                    Color borderColor, Color bgColor,
                                                    float borderPx)
    {
        RectTransform outer = CreatePanel(parent, name, borderColor);
        outer.localScale = Vector3.one;
        outer.GetComponent<Image>().raycastTarget = false;

        RectTransform inner = CreatePanel(outer, name + "_Inner", bgColor);
        inner.anchorMin = Vector2.zero;
        inner.anchorMax = Vector2.one;
        inner.offsetMin = new Vector2( borderPx,  borderPx);
        inner.offsetMax = new Vector2(-borderPx, -borderPx);
        inner.GetComponent<Image>().raycastTarget = false;

        return outer;
    }

    /// <summary>Returns the inner (background) child of a CreateFramedSection.</summary>
    private static RectTransform GetInner(RectTransform framedSection)
    {
        // Child 0 = the inner Image we just created in CreateFramedSection.
        return (RectTransform)framedSection.GetChild(0);
    }

    /// <summary>A tiny inset rectangle used as a host for grouped widgets.</summary>
    private static RectTransform CreateInset(Transform parent, string name)
    {
        RectTransform rt = CreatePanel(parent, name, InnerBg);
        rt.GetComponent<Image>().raycastTarget = false;
        return rt;
    }

    /// <summary>Stretch the rect to fill its parent with N-px padding on all sides.</summary>
    private static void StretchInside(RectTransform rt, float padding)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2( padding,  padding);
        rt.offsetMax = new Vector2(-padding, -padding);
    }

    /// <summary>
    /// Pushes the top edge of <paramref name="rt"/> down by <see cref="CenterTitleStrip"/>
    /// pixels so the inset doesn't overlap the section's title text. Call AFTER
    /// <see cref="StretchInside"/>.
    /// </summary>
    private static void InsetTitleStrip(RectTransform rt)
    {
        rt.offsetMax = new Vector2(rt.offsetMax.x, rt.offsetMax.y - CenterTitleStrip);
    }

    /// <summary>Set absolute anchored position + size with a bottom-left pivot.</summary>
    private static void AnchorAbsolute(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot     = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
        rt.localScale = Vector3.one;
    }

    /// <summary>
    /// Returns the centred anchoredPosition for a button in a
    /// <paramref name="columns"/> × <paramref name="rows"/> grid. Index
    /// <paramref name="col"/> starts at the left, <paramref name="row"/>
    /// starts at the top (row 0 = top row). The result puts the entire grid
    /// centred around (0, 0) in its parent.
    /// </summary>
    private static Vector2 GridPos(int col, int row, int columns, int rows)
    {
        float totalW = columns * ButtonW + (columns - 1) * ButtonGapX;
        float x      = (col + 0.5f) * (ButtonW + ButtonGapX) - totalW * 0.5f - ButtonGapX * 0.5f;
        float step   = ButtonH + ButtonGapY;
        float y      = ((rows - 1) * 0.5f - row) * step;
        return new Vector2(x, y);
    }

    private static void DisableBarRaycast(RectTransform barRoot)
    {
        Image img = barRoot.GetComponent<Image>();
        if (img != null) img.raycastTarget = false;
    }

    // ================================================================== //
    // Generic panel + button factories
    // ================================================================== //

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
        rt.anchorMin        = new Vector2(0.5f, 0.5f);
        rt.anchorMax        = new Vector2(0.5f, 0.5f);
        rt.pivot            = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta        = size;

        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text            = text;
        tmp.fontSize        = fontSize;
        tmp.color           = Color.white;
        tmp.alignment       = alignment;
        tmp.raycastTarget   = false;
        return tmp;
    }

    /// <summary>
    /// Icon-friendly production button. Lays out an icon placeholder (left)
    /// and a two-line label (right) inside a 160×92 frame. Future work: drop
    /// a Sprite into the icon Image when real art is available.
    /// </summary>
    private static Button CreateIconButton(
        RectTransform parent,
        string        name,
        string        label,
        string        cost,
        Color         btnColor,
        Vector2       position)
    {
        CheckTMPEssentials();

        // Root.
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = position;
        rt.sizeDelta = new Vector2(ButtonW, ButtonH);
        rt.localScale = Vector3.one;

        Image bg = go.AddComponent<Image>();
        bg.color = new Color(0.10f, 0.13f, 0.10f, 1f);    // dark frame
        bg.raycastTarget = true;

        Button btn = go.AddComponent<Button>();
        ColorBlock cb = ColorBlock.defaultColorBlock;
        cb.normalColor      = new Color(0.10f, 0.13f, 0.10f, 1f);
        cb.highlightedColor = new Color(0.16f, 0.20f, 0.14f, 1f);
        cb.pressedColor     = new Color(0.06f, 0.09f, 0.06f, 1f);
        cb.selectedColor    = new Color(0.10f, 0.13f, 0.10f, 1f);
        cb.fadeDuration     = 0.08f;
        btn.colors          = cb;

        // Icon placeholder — coloured square inside a thin border.
        RectTransform iconWrap = CreatePanel(rt, "IconFrame", new Color(0.05f, 0.07f, 0.05f, 1f));
        iconWrap.anchorMin = new Vector2(0f, 0.5f);
        iconWrap.anchorMax = new Vector2(0f, 0.5f);
        iconWrap.pivot     = new Vector2(0f, 0.5f);
        iconWrap.anchoredPosition = new Vector2(6f, 0f);
        iconWrap.sizeDelta = new Vector2(72f, 72f);
        iconWrap.GetComponent<Image>().raycastTarget = false;

        RectTransform icon = CreatePanel(iconWrap, "Icon", btnColor);
        icon.anchorMin = Vector2.zero;
        icon.anchorMax = Vector2.one;
        icon.offsetMin = new Vector2(3f, 3f);
        icon.offsetMax = new Vector2(-3f, -3f);
        icon.GetComponent<Image>().raycastTarget = false;

        // Label TMP (the main label — the one RTSHUD will mutate to
        // include the live cost). Spans the right two-thirds of the button.
        GameObject lblGO = new GameObject("Text");
        lblGO.transform.SetParent(rt, false);
        RectTransform lblRT = lblGO.AddComponent<RectTransform>();
        lblRT.anchorMin = new Vector2(0f, 0f);
        lblRT.anchorMax = new Vector2(1f, 1f);
        lblRT.offsetMin = new Vector2(84f, 6f);
        lblRT.offsetMax = new Vector2(-4f, -6f);

        TextMeshProUGUI tmp = lblGO.AddComponent<TextMeshProUGUI>();
        // RTSHUD overwrites this each frame with "Name - cost". Pre-fill with
        // the static label+cost so the editor preview is readable.
        tmp.text          = string.IsNullOrEmpty(cost) ? label : $"{label} - {cost}";
        tmp.fontSize      = 16;
        tmp.color         = BodyText;
        tmp.alignment     = TextAlignmentOptions.Center;
        tmp.fontStyle     = FontStyles.Bold;
        tmp.enableWordWrapping = true;
        tmp.raycastTarget = false;

        return btn;
    }

    /// <summary>
    /// Compact "Unload All" bar — wide, short button anchored beneath the
    /// transport slot grid. Smaller than the icon-button so it fits inside
    /// the transport inset alongside title + 6 slot buttons.
    /// </summary>
    private static Button CreateUnloadAllButton(RectTransform parent, Vector2 anchoredPos)
    {
        CheckTMPEssentials();

        GameObject go = new GameObject("BtnUnloadAll");
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, "Create BtnUnloadAll");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = new Vector2(240f, 28f);
        rt.localScale = Vector3.one;

        Image bg = go.AddComponent<Image>();
        bg.color = BtnUnloadAllColor;
        bg.raycastTarget = true;

        Button btn = go.AddComponent<Button>();
        ColorBlock cb = ColorBlock.defaultColorBlock;
        cb.normalColor      = BtnUnloadAllColor;
        cb.highlightedColor = BtnUnloadAllColor * 1.25f;
        cb.pressedColor     = BtnUnloadAllColor * 0.70f;
        cb.selectedColor    = BtnUnloadAllColor;
        cb.fadeDuration     = 0.08f;
        btn.colors          = cb;

        GameObject textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        RectTransform textRT = textGO.AddComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero;
        textRT.offsetMax = Vector2.zero;

        TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text          = "Unload All";
        tmp.fontSize      = 18;
        tmp.color         = Color.white;
        tmp.alignment     = TextAlignmentOptions.Center;
        tmp.fontStyle     = FontStyles.Bold;
        tmp.raycastTarget = false;
        return btn;
    }

    /// <summary>
    /// Tall narrow slot button used in the Transport panel. Icon area at top,
    /// "Soldier"/"Empty" label at bottom.
    /// </summary>
    private static Button CreateSlotButton(
        RectTransform parent,
        string        name,
        Color         btnColor,
        Vector2       anchoredPos,
        Vector2       size)
    {
        CheckTMPEssentials();

        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;

        Image bg = go.AddComponent<Image>();
        bg.color = btnColor;
        bg.raycastTarget = true;

        Button btn = go.AddComponent<Button>();
        ColorBlock cb = ColorBlock.defaultColorBlock;
        cb.normalColor      = btnColor;
        cb.highlightedColor = btnColor * 1.25f;
        cb.pressedColor     = btnColor * 0.70f;
        cb.selectedColor    = btnColor;
        cb.fadeDuration     = 0.08f;
        btn.colors          = cb;

        // Inner icon placeholder area (28x28 at top — small enough to leave
        // room for the label at the bottom of the 50-tall slot).
        RectTransform iconWrap = CreatePanel(rt, "IconFrame", new Color(0.05f, 0.07f, 0.05f, 1f));
        iconWrap.anchorMin = new Vector2(0.5f, 1f);
        iconWrap.anchorMax = new Vector2(0.5f, 1f);
        iconWrap.pivot     = new Vector2(0.5f, 1f);
        iconWrap.anchoredPosition = new Vector2(0f, -3f);
        iconWrap.sizeDelta = new Vector2(28f, 28f);
        iconWrap.GetComponent<Image>().raycastTarget = false;

        // Label at the bottom of the slot.
        GameObject textGO = new GameObject("Text");
        textGO.transform.SetParent(go.transform, false);
        RectTransform textRT = textGO.AddComponent<RectTransform>();
        textRT.anchorMin = new Vector2(0f, 0f);
        textRT.anchorMax = new Vector2(1f, 0f);
        textRT.offsetMin = new Vector2(2f, 2f);
        textRT.offsetMax = new Vector2(-2f, 18f);

        TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text          = "Empty";
        tmp.fontSize      = 14;
        tmp.color         = Color.white;
        tmp.alignment     = TextAlignmentOptions.Center;
        tmp.fontStyle     = FontStyles.Bold;
        tmp.raycastTarget = false;
        return btn;
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
        Debug.LogWarning(
            "[SetupRTSHUD] ⚠ GameManager was not found — created a new one.\n" +
            "  If your project already has a GameManager with a different name, " +
            "  manually move the RTSHUD component onto it and delete this duplicate.");
        return gm;
    }

    private static T GetOrAddComponent<T>(GameObject go) where T : Component
    {
        T comp = go.GetComponent<T>();
        if (comp != null) return comp;

        comp = go.AddComponent<T>();
        Debug.Log($"[SetupRTSHUD] ✓ Added {typeof(T).Name} to '{go.name}'");
        return comp;
    }

    // ================================================================== //
    // Button → RTSHUD wiring
    // ================================================================== //

    private static void WireButton(Button btn, RTSHUD hud, string methodName)
    {
        SerializedObject   so    = new SerializedObject(btn);
        SerializedProperty calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
        if (calls != null)
        {
            calls.ClearArray();
            so.ApplyModifiedProperties();
        }

        MethodInfo method = typeof(RTSHUD).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public);

        if (method == null)
        {
            Debug.LogError(
                $"[SetupRTSHUD] ✗ Method '{methodName}' not found on RTSHUD.");
            return;
        }

        UnityAction action =
            (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), hud, method);

        UnityEventTools.AddPersistentListener(btn.onClick, action);
        EditorUtility.SetDirty(btn);
    }

    // ================================================================== //
    // Utilities
    // ================================================================== //

    private static bool _tmpWarningShown;

    private static void CheckTMPEssentials()
    {
        if (_tmpWarningShown) return;
        if (TMP_Settings.defaultFontAsset == null)
        {
            Debug.LogWarning(
                "[SetupRTSHUD] ⚠ TMP Essentials not imported — text objects will be invisible.\n" +
                "  Fix: Window → TextMeshPro → Import TMP Essential Resources, then re-run.");
            _tmpWarningShown = true;
        }
    }
}
