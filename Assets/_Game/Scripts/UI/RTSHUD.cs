using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// RTS HUD — top bar showing Resources and Power, plus a Build Panel
/// with buttons to start building placement.
///
/// Setup:
///   1. Create a Screen Space - Overlay Canvas named "HUDCanvas".
///   2. Inside it, create a child Panel named "TopBar" (stretch-top anchor,
///      ~50px tall). Add two TextMeshProUGUI children: "ResourcesText" and
///      "PowerText". Drag them into this component's Inspector fields.
///   3. Create another child Panel named "BuildPanel" (anchored bottom-right or
///      wherever you like). Add two Button children:
///         - "BtnBarracks"   — label text "Barracks (B)"
///         - "BtnPowerPlant" — label text "Power Plant (P)"
///      Wire each button's OnClick() to this component:
///         BtnBarracks.OnClick   → RTSHUD.OnClickBuildBarracks
///         BtnPowerPlant.OnClick → RTSHUD.OnClickBuildPowerPlant
///   4. Add RTSHUD to your GameManager (or any scene object).
///   5. Assign the two text references in the Inspector.
/// </summary>
public class RTSHUD : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Top Bar Text")]
    [Tooltip("TextMeshProUGUI that shows current resources")]
    public TextMeshProUGUI resourcesText;

    [Tooltip("TextMeshProUGUI that shows current power supply / demand")]
    public TextMeshProUGUI powerText;

    [Header("Colors")]
    [Tooltip("Power text color when supply >= demand")]
    public Color powerOkColor = new Color(0.4f, 1f, 0.4f);     // light green

    [Tooltip("Power text color when supply < demand")]
    public Color powerLowColor = new Color(1f, 0.3f, 0.3f);    // red

    [Header("Production Panel (selected-building command panel)")]
    [Tooltip("Bottom-left panel container. Shown only when a building with " +
             "UnitProducer is selected. Set up automatically by Tools → RTS → Setup HUD.")]
    public GameObject productionPanel;

    [Tooltip("Soldier button GameObject. Visibility is toggled per producer " +
             "(visible only when the selected building's UnitProducer.CanProduceSoldier).")]
    public GameObject soldierButton;

    [Tooltip("The TMP label inside the Soldier button — updated to show cost " +
             "when a producer is selected. Leave null to keep the static label.")]
    public TextMeshProUGUI soldierButtonLabel;

    [Tooltip("RPG Soldier button GameObject. Visibility is toggled per producer " +
             "(visible only when the selected building's UnitProducer.CanProduceRPGSoldier).")]
    public GameObject rpgSoldierButton;

    [Tooltip("The TMP label inside the RPG Soldier button — updated to show cost " +
             "when a producer is selected. Leave null to keep the static label.")]
    public TextMeshProUGUI rpgSoldierButtonLabel;

    [Tooltip("Worker button GameObject. Visibility is toggled per producer " +
             "(visible only when the selected building's UnitProducer.CanProduceWorker).")]
    public GameObject workerButton;

    [Tooltip("The TMP label inside the Worker button — updated to show cost " +
             "when a producer is selected. Leave null to keep the static label.")]
    public TextMeshProUGUI workerButtonLabel;

    [Tooltip("Dozer button GameObject. Visibility is toggled per producer " +
             "(visible only when the selected CommandCenter's CanProduceDozer).")]
    public GameObject dozerButton;

    [Tooltip("The TMP label inside the Dozer button — updated to show cost " +
             "when a producer is selected. Leave null to keep the static label.")]
    public TextMeshProUGUI dozerButtonLabel;

    [Tooltip("Humvee button GameObject. Visibility is toggled per producer " +
             "(visible only when the selected building's VehicleFactoryProducer.CanProduceHumvee).")]
    public GameObject humveeButton;

    [Tooltip("The TMP label inside the Humvee button — updated to show cost " +
             "when a producer is selected. Leave null to keep the static label.")]
    public TextMeshProUGUI humveeButtonLabel;

    [Tooltip("Artillery Tank button GameObject. Visibility is toggled per producer " +
             "(visible only when the selected VehicleFactoryProducer.CanProduceArtilleryTank).")]
    public GameObject tankButton;

    [Tooltip("The TMP label inside the Artillery Tank button — updated to show cost " +
             "when a producer is selected. Leave null to keep the static label.")]
    public TextMeshProUGUI tankButtonLabel;

    [Tooltip("Missile Launcher button GameObject. Visibility is toggled per producer " +
             "(visible only when the selected VehicleFactoryProducer.CanProduceMissileLauncher).")]
    public GameObject missileLauncherButton;

    [Tooltip("The TMP label inside the Missile Launcher button — updated to show cost " +
             "when a producer is selected. Leave null to keep the static label.")]
    public TextMeshProUGUI missileLauncherButtonLabel;

    [Tooltip("APC button GameObject. Visibility is toggled per producer " +
             "(visible only when the selected VehicleFactoryProducer.CanProduceAPC).")]
    public GameObject apcButton;

    [Tooltip("The TMP label inside the APC button — updated to show cost " +
             "when a producer is selected. Leave null to keep the static label.")]
    public TextMeshProUGUI apcButtonLabel;

    [Tooltip("Strike Jet button GameObject. Visibility is toggled per producer " +
             "(visible only when the selected Airfield.CanProduceStrikeJet).")]
    public GameObject strikeJetButton;

    [Tooltip("The TMP label inside the Strike Jet button — updated to show cost " +
             "when a producer is selected. Leave null to keep the static label.")]
    public TextMeshProUGUI strikeJetButtonLabel;

    [Header("Dozer Build Panel (shown when a Dozer is selected)")]
    [Tooltip("Bottom-left panel container shown ONLY when one or more selected units " +
             "carry the DozerBuilder component. Holds the four construction buttons.")]
    public GameObject dozerBuildPanel;

    [Tooltip("Construction button — Barracks. Wired to OnClickDozerBuildBarracks.")]
    public GameObject dozerBuildBarracksButton;
    public TextMeshProUGUI dozerBuildBarracksLabel;

    [Tooltip("Construction button — Power Plant. Wired to OnClickDozerBuildPowerPlant.")]
    public GameObject dozerBuildPowerPlantButton;
    public TextMeshProUGUI dozerBuildPowerPlantLabel;

    [Tooltip("Construction button — Vehicle Factory. Wired to OnClickDozerBuildVehicleFactory.")]
    public GameObject dozerBuildVehicleFactoryButton;
    public TextMeshProUGUI dozerBuildVehicleFactoryLabel;

    [Tooltip("Construction button — Airfield. Wired to OnClickDozerBuildAirfield.")]
    public GameObject dozerBuildAirfieldButton;
    public TextMeshProUGUI dozerBuildAirfieldLabel;

    [Tooltip("Construction button — Machine Gun Defense. Wired to OnClickDozerBuildMachineGunDefense.")]
    public GameObject dozerBuildMachineGunDefenseButton;
    public TextMeshProUGUI dozerBuildMachineGunDefenseLabel;

    [Tooltip("Construction button — CommandCenter (Phase 10). Wired to OnClickDozerBuildCommandCenter.")]
    public GameObject dozerBuildCommandCenterButton;
    public TextMeshProUGUI dozerBuildCommandCenterLabel;

    [Header("Transport Panel (shown when an APC is selected)")]
    [Tooltip("Bottom-left panel shown only when at least one selected unit carries " +
             "an APCTransport. Holds the title, 6 passenger slots, and the Unload All " +
             "button.")]
    public GameObject transportPanel;

    [Tooltip("Title label at the top of the transport panel — refreshed with " +
             "'Transport (current/capacity)' each frame the panel is visible.")]
    public TextMeshProUGUI transportTitleLabel;

    [Tooltip("Six passenger slot buttons (must be exactly 6 entries). Index 0..5. " +
             "Clicking an occupied slot unloads that one passenger via " +
             "OnClickUnloadSlot{N}.")]
    public GameObject[] transportSlotButtons = new GameObject[6];

    [Tooltip("Labels inside the slot buttons (must be exactly 6 entries) — refreshed " +
             "each frame with the passenger's short name or 'Empty'.")]
    public TextMeshProUGUI[] transportSlotLabels = new TextMeshProUGUI[6];

    [Tooltip("Unload All button. Wired to OnClickUnloadAll.")]
    public GameObject transportUnloadAllButton;

    // ------------------------------------------------------------------ //
    // New HUD modules (Selected Info + Resource/Power + Minimap)
    // ------------------------------------------------------------------ //

    [Header("New HUD modules (set up by Tools → RTS → Setup → Setup Gameplay HUD)")]
    [Tooltip("The full bottom command bar that hosts every panel. Hide this " +
             "to suppress the entire gameplay HUD (e.g. during a cinematic).")]
    public GameObject bottomBarRoot;

    [Tooltip("Left-section module — selected-object portrait + name + HP + " +
             "category + group breakdown. Self-driven (polls UnitSelector).")]
    public SelectedInfoPanelUI selectedInfoPanel;

    [Tooltip("Right-section module — resources / power display with low-power " +
             "warning pulse. Owns no text content; RTSHUD writes the strings.")]
    public ResourcePowerPanelUI resourcePowerPanel;

    [Tooltip("Right-section module — top-down minimap. Binds the MinimapCamera's " +
             "RenderTexture to a RawImage. Optional click-to-recentre.")]
    public MiniMapPanelUI miniMapPanel;

    // ------------------------------------------------------------------ //
    // Private references — found at runtime
    // ------------------------------------------------------------------ //

    private PlayerResourceManager resourceManager;
    private PowerManager           powerManager;
    private BuildingPlacementManager placementManager;

    // Currently bound producers — at most one of each, and at least one must
    // be non-null while the production panel is visible.
    private UnitProducer           currentSoldierProducer;
    private CommandCenterProducer  currentWorkerProducer;
    private VehicleFactoryProducer currentVehicleProducer;
    private Airfield               currentAirfield;

    // Currently bound Dozer — the "primary" dozer in a multi-select. Set by
    // UnitSelector via ShowDozerBuildPanel. The Dozer build panel is hidden
    // when this reference is null OR when the dozer's GameObject is destroyed.
    private DozerBuilder           currentDozer;

    // Currently bound APC transport — set by UnitSelector via ShowTransportPanel.
    // Drives the per-frame slot refresh in LateUpdate.
    private APCTransport           currentTransport;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        resourceManager  = FindAnyObjectByType<PlayerResourceManager>();
        powerManager     = FindAnyObjectByType<PowerManager>();
        placementManager = FindAnyObjectByType<BuildingPlacementManager>();

        if (resourceManager == null)
            Debug.LogError("RTSHUD: No PlayerResourceManager found.");
        if (powerManager == null)
            Debug.LogWarning("RTSHUD: No PowerManager found — power display will be blank.");
        if (placementManager == null)
            Debug.LogError("RTSHUD: No BuildingPlacementManager found — build buttons will not work.");

        // Production panel is hidden until a producer is selected
        if (productionPanel != null)
            productionPanel.SetActive(false);

        // Dozer build panel is hidden until a dozer unit is selected
        if (dozerBuildPanel != null)
            dozerBuildPanel.SetActive(false);

        // Transport panel is hidden until an APC is selected
        if (transportPanel != null)
            transportPanel.SetActive(false);
    }

    private bool gameStartedListenerSubscribed;

    private void Start()
    {
        // Auto-hide the gameplay HUD while the main menu is showing. We do
        // this in Start (not Awake) so MainMenuController and GameStateManager
        // — which usually live on the same GameObject — have a chance to
        // initialise their Awake-time state first.
        //
        // Safety: in test scenes WITHOUT a MainMenuController, we leave the
        // HUD visible. Authoring/preview workflows keep working as before.
        MainMenuController menu = FindAnyObjectByType<MainMenuController>(FindObjectsInactive.Include);
        if (menu == null) return;

        // Menu present, but no GameStateManager: behave defensively and hide
        // the HUD anyway — if the menu is in the scene, the player hasn't
        // pressed Play yet by definition.
        bool gameAlreadyStarted = GameStateManager.Instance != null
                               && GameStateManager.Instance.IsGameStarted;
        if (gameAlreadyStarted) return;

        SetGameplayHudVisible(false);

        // Subscribe to OnGameStarted so the HUD comes back when Play is pressed.
        if (GameStateManager.Instance != null && !gameStartedListenerSubscribed)
        {
            GameStateManager.Instance.OnGameStarted += HandleGameStarted;
            gameStartedListenerSubscribed = true;
        }
    }

    private void OnDestroy()
    {
        if (gameStartedListenerSubscribed && GameStateManager.Instance != null)
        {
            GameStateManager.Instance.OnGameStarted -= HandleGameStarted;
            gameStartedListenerSubscribed = false;
        }
    }

    private void HandleGameStarted()
    {
        Debug.Log("[RTSHUD] OnGameStarted received — showing gameplay HUD.");
        SetGameplayHudVisible(true);

        // One-shot — unsubscribe so a future re-fire (e.g. scene reload that
        // didn't tear us down) doesn't double-show.
        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.OnGameStarted -= HandleGameStarted;
            gameStartedListenerSubscribed = false;
        }
    }

    /// <summary>
    /// Toggle the entire gameplay HUD (HUDCanvas root if reachable, otherwise
    /// the bottom command bar). Called by:
    ///   • <see cref="HandleGameStarted"/> when the player presses Play.
    ///   • Externally by editor tools or cinematic systems that need to hide
    ///     the HUD wholesale.
    ///
    /// No-op if neither <see cref="bottomBarRoot"/> nor a parent Canvas is
    /// reachable — fail-silent so this method is safe to call before SetupHUD
    /// has finished running.
    /// </summary>
    public void SetGameplayHudVisible(bool visible)
    {
        // Prefer toggling the whole HUDCanvas — that also hides the floating
        // boarding-cursor indicator while the menu is up. Walk from the bottom
        // bar up to the Canvas; that's how the bar was parented during setup.
        if (bottomBarRoot != null)
        {
            Canvas canvas = bottomBarRoot.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.gameObject.activeSelf != visible)
            {
                canvas.gameObject.SetActive(visible);
                Debug.Log($"[RTSHUD] HUDCanvas → {(visible ? "shown" : "hidden")}");
                return;
            }

            // Canvas not found (HUD may have been re-parented). Fall back to
            // toggling just the bottom bar GameObject.
            if (bottomBarRoot.activeSelf != visible)
            {
                bottomBarRoot.SetActive(visible);
                Debug.Log($"[RTSHUD] BottomBar → {(visible ? "shown" : "hidden")} (canvas fallback)");
            }
        }
    }

    private void LateUpdate()
    {
        RefreshResources();
        RefreshPower();

        // Per-frame transport slot refresh — covers passenger join/leave
        // events without a dedicated observer hookup.
        if (currentTransport != null && !currentTransport)
        {
            HideTransportPanel();   // APC died with the panel open
        }
        else if (currentTransport != null && transportPanel != null && transportPanel.activeSelf)
        {
            RefreshTransportSlots();
        }
    }

    // ------------------------------------------------------------------ //
    // Text refresh
    // ------------------------------------------------------------------ //

    private void RefreshResources()
    {
        if (resourcesText == null) return;

        // Phase 4: in multiplayer, show the LOCAL player's bank — owner 0 on
        // Client A, owner 1 on Client B. Before MatchStart fires (or in
        // single-player), fall back to the legacy single-bank reference.
        int localPid = NetworkManagerRTS.LocalPlayerId;
        if (localPid >= 0)
        {
            PlayerResourceManager bank = ResourceBank.For(localPid);
            if (bank != null)
            {
                resourcesText.text = $"Resources: {bank.CurrentResources}";
                return;
            }
        }

        // SP / pre-match fallback.
        if (resourceManager != null)
            resourcesText.text = $"Resources: {resourceManager.CurrentResources}";
    }

    private void RefreshPower()
    {
        if (powerText == null) return;

        if (powerManager == null)
        {
            powerText.text  = "Power: —";
            powerText.color = powerOkColor;
            return;
        }

        // Per-player power: show only the LOCAL player's grid. In SP /
        // pre-MatchStart, LocalPlayerId is -1 → fall back to owner 0 (the
        // single-player faction). Mirrors RefreshResources' local-bank logic.
        int localPid = NetworkManagerRTS.LocalPlayerId;
        if (localPid < 0) localPid = GameEntity.PlayerOwnerId;

        int supply = powerManager.SupplyFor(localPid);
        int demand = powerManager.DemandFor(localPid);
        bool powered = powerManager.IsPoweredFor(localPid);

        string warning = powered ? "" : " ⚠ LOW";
        powerText.text  = $"Power: {supply} / {demand}{warning}";
        powerText.color = powered ? powerOkColor : powerLowColor;
    }

    // ------------------------------------------------------------------ //
    // Button callbacks — wire these to UI Button OnClick events
    // ------------------------------------------------------------------ //

    /// <summary>Called by the Barracks button. Starts Barracks placement mode.</summary>
    public void OnClickBuildBarracks()
    {
        if (placementManager == null) return;
        placementManager.StartBarracksPlacement();
    }

    /// <summary>Called by the PowerPlant button. Starts PowerPlant placement mode.</summary>
    public void OnClickBuildPowerPlant()
    {
        if (placementManager == null) return;
        placementManager.StartPowerPlantPlacement();
    }

    /// <summary>Called by the Vehicle Factory button. Starts VehicleFactory placement mode.</summary>
    public void OnClickBuildVehicleFactory()
    {
        if (placementManager == null) return;
        placementManager.StartVehicleFactoryPlacement();
    }

    /// <summary>Called by the Airfield button. Starts Airfield placement mode.</summary>
    public void OnClickBuildAirfield()
    {
        if (placementManager == null) return;
        placementManager.StartAirfieldPlacement();
    }

    // ------------------------------------------------------------------ //
    // Production panel — driven by UnitSelector when buildings are selected
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Shows the production panel for <paramref name="building"/>. The HUD
    /// queries the building for UnitProducer (Soldier) and CommandCenterProducer
    /// (Worker) and shows exactly the buttons that match the components found.
    /// Called by UnitSelector when a building is left-clicked.
    /// </summary>
    public void ShowProductionFor(SelectableBuilding building)
    {
        if (building == null)
        {
            HideProductionPanel();
            return;
        }

        UnitProducer           soldierProd = building.GetComponent<UnitProducer>();
        CommandCenterProducer  workerProd  = building.GetComponent<CommandCenterProducer>();
        VehicleFactoryProducer vehicleProd = building.GetComponent<VehicleFactoryProducer>();
        Airfield               airfield    = building.GetComponent<Airfield>();

        bool showSoldier         = soldierProd != null && soldierProd.CanProduceSoldier;
        bool showRPGSoldier      = soldierProd != null && soldierProd.CanProduceRPGSoldier;
        bool showWorker          = workerProd  != null && workerProd.CanProduceWorker;
        bool showDozer           = workerProd  != null && workerProd.CanProduceDozer;
        bool showHumvee          = vehicleProd != null && vehicleProd.CanProduceHumvee;
        bool showAPC             = vehicleProd != null && vehicleProd.CanProduceAPC;
        bool showTank            = vehicleProd != null && vehicleProd.CanProduceArtilleryTank;
        bool showMissileLauncher = vehicleProd != null && vehicleProd.CanProduceMissileLauncher;
        bool showStrikeJet       = airfield    != null && airfield.CanProduceStrikeJet;

        if (!showSoldier && !showRPGSoldier && !showWorker && !showDozer
            && !showHumvee && !showAPC && !showTank && !showMissileLauncher && !showStrikeJet)
        {
            HideProductionPanel();
            return;
        }

        // Soldier producer drives both Soldier and RPG Soldier buttons.
        currentSoldierProducer = (showSoldier || showRPGSoldier) ? soldierProd : null;
        // CommandCenter producer drives both Worker and Dozer buttons.
        currentWorkerProducer  = (showWorker  || showDozer)       ? workerProd  : null;
        // Vehicle producer is bound when any of its outputs is available.
        currentVehicleProducer = (showHumvee || showAPC || showTank || showMissileLauncher) ? vehicleProd : null;
        currentAirfield        = showStrikeJet                    ? airfield    : null;

        if (productionPanel != null)
            productionPanel.SetActive(true);

        // --- Soldier button -------------------------------------------- //
        if (soldierButton != null)
            soldierButton.SetActive(showSoldier);

        if (showSoldier && soldierButtonLabel != null)
            soldierButtonLabel.text = $"Soldier - {soldierProd.soldierCost}";

        // --- RPG Soldier button ---------------------------------------- //
        if (rpgSoldierButton != null)
            rpgSoldierButton.SetActive(showRPGSoldier);

        if (showRPGSoldier && rpgSoldierButtonLabel != null)
            rpgSoldierButtonLabel.text = $"RPG Soldier - {soldierProd.rpgSoldierCost}";

        // --- Worker button --------------------------------------------- //
        if (workerButton != null)
            workerButton.SetActive(showWorker);

        if (showWorker && workerButtonLabel != null)
            workerButtonLabel.text = $"Worker - {workerProd.workerCost}";

        // --- Dozer button ---------------------------------------------- //
        if (dozerButton != null)
            dozerButton.SetActive(showDozer);

        if (showDozer && dozerButtonLabel != null)
            dozerButtonLabel.text = $"Dozer - {workerProd.dozerCost}";

        // --- Humvee button --------------------------------------------- //
        if (humveeButton != null)
            humveeButton.SetActive(showHumvee);

        if (showHumvee && humveeButtonLabel != null)
            humveeButtonLabel.text = $"Humvee - {vehicleProd.humveeCost}";

        // --- APC button ------------------------------------------------ //
        if (apcButton != null)
            apcButton.SetActive(showAPC);

        if (showAPC && apcButtonLabel != null)
            apcButtonLabel.text = $"APC - {vehicleProd.apcCost}";

        // --- Artillery Tank button ------------------------------------- //
        if (tankButton != null)
            tankButton.SetActive(showTank);

        if (showTank && tankButtonLabel != null)
            tankButtonLabel.text = $"Artillery Tank - {vehicleProd.artilleryTankCost}";

        // --- Missile Launcher button ----------------------------------- //
        if (missileLauncherButton != null)
            missileLauncherButton.SetActive(showMissileLauncher);

        if (showMissileLauncher && missileLauncherButtonLabel != null)
            missileLauncherButtonLabel.text = $"Missile Launcher - {vehicleProd.missileLauncherCost}";

        // --- Strike Jet button ----------------------------------------- //
        if (strikeJetButton != null)
            strikeJetButton.SetActive(showStrikeJet);

        if (showStrikeJet && strikeJetButtonLabel != null)
            strikeJetButtonLabel.text = $"Strike Jet - {airfield.strikeJetCost}";
    }

    /// <summary>Hides the production panel and forgets the bound producers.</summary>
    public void HideProductionPanel()
    {
        currentSoldierProducer = null;
        currentWorkerProducer  = null;
        currentVehicleProducer = null;
        currentAirfield        = null;

        if (productionPanel != null)
            productionPanel.SetActive(false);
    }

    /// <summary>Called by the Soldier button. Produces from the bound UnitProducer.</summary>
    public void OnClickProduceSoldier()
    {
        IssueProduceCommand(currentSoldierProducer, "Soldier", "Barracks");
    }

    /// <summary>Called by the RPG Soldier button. Produces from the bound UnitProducer.</summary>
    public void OnClickProduceRPGSoldier()
    {
        IssueProduceCommand(currentSoldierProducer, "RPGSoldier", "Barracks");
    }

    /// <summary>Called by the Worker button. Produces from the bound CommandCenterProducer.</summary>
    public void OnClickProduceWorker()
    {
        IssueProduceCommand(currentWorkerProducer, "Worker", "CommandCenter");
    }

    /// <summary>Called by the Dozer button. Produces from the bound CommandCenterProducer.</summary>
    public void OnClickProduceDozer()
    {
        IssueProduceCommand(currentWorkerProducer, "Dozer", "CommandCenter");
    }

    /// <summary>Called by the Humvee button. Produces from the bound VehicleFactoryProducer.</summary>
    public void OnClickProduceHumvee()
    {
        IssueProduceCommand(currentVehicleProducer, "Humvee", "VehicleFactory");
    }

    /// <summary>Called by the Artillery Tank button. Produces from the bound VehicleFactoryProducer.</summary>
    public void OnClickProduceArtilleryTank()
    {
        IssueProduceCommand(currentVehicleProducer, "ArtilleryTank", "VehicleFactory");
    }

    /// <summary>Called by the Missile Launcher button. Produces from the bound VehicleFactoryProducer.</summary>
    public void OnClickProduceMissileLauncher()
    {
        IssueProduceCommand(currentVehicleProducer, "MissileLauncher", "VehicleFactory");
    }

    /// <summary>Called by the APC button. Produces from the bound VehicleFactoryProducer.</summary>
    public void OnClickProduceAPC()
    {
        IssueProduceCommand(currentVehicleProducer, "APC", "VehicleFactory");
    }

    /// <summary>Called by the Strike Jet button. Produces from the bound Airfield.</summary>
    public void OnClickProduceStrikeJet()
    {
        IssueProduceCommand(currentAirfield, "StrikeJet", "Airfield");
    }

    /// <summary>
    /// Shared validation + dispatch path for the nine Produce buttons. The
    /// command record carries the producer's <see cref="GameEntity.EntityId"/>
    /// plus a fresh network-allocated <c>spawnEntityId</c> for the spawned
    /// unit. Both clients (issuer + remote replay) see the same spawn id and
    /// the resulting unit registers under that id in <see cref="EntityRegistry"/>.
    ///
    /// Phase 9 — <c>cmd.playerId</c> is derived from the PRODUCER's
    /// <see cref="GameEntity.ownerPlayerId"/>, not from the local client's
    /// id. This way a Player 1 worker produced from Player 1's CommandCenter
    /// always carries <c>playerId = 1</c>, even if a later refactor moved
    /// LocalCommandPlayerId resolution around.
    /// </summary>
    private void IssueProduceCommand(Component producer, string productionType, string expectedBuilding)
    {
        if (producer == null)
        {
            Debug.LogWarning($"[RTSHUD] {productionType} button clicked but no " +
                             $"{expectedBuilding} is selected. Select a {expectedBuilding} first.");
            return;
        }

        GameEntity ge = GameEntity.EnsureOn(producer.gameObject);
        int producerOwner = ge.ownerPlayerId;

        // MP guard: refuse to issue a Produce command for a building you
        // don't own. UnitSelector.IsLocallyOwned already prevents selecting
        // a non-owned producer; this is the second line of defense.
        if (NetworkManagerRTS.IsMultiplayerEnabled)
        {
            int local = NetworkManagerRTS.LocalPlayerId;
            if (producerOwner != local)
            {
                Debug.LogWarning($"[RTSHUD] Produce {productionType} blocked — " +
                                 $"producer '{ge.name}' owned by player {producerOwner}, " +
                                 $"local player is {local}.");
                return;
            }
        }

        Debug.Log($"[RTSHUD] Produce {productionType} from {expectedBuilding} owner=" +
                  $"{producerOwner} localPlayer={NetworkManagerRTS.LocalPlayerId}");

        // Pre-allocate the id the spawned unit will adopt so the produce
        // command can carry it to remote clients.
        string spawnId = NetworkEntityIdAllocator.Allocate();
        CommandDispatcher.Issue(
            PlayerCommand.Produce(producerOwner, ge.EntityId, productionType, spawnId));
    }

    // ================================================================== //
    // Dozer build panel — shown when a Dozer unit is selected
    // ================================================================== //

    /// <summary>
    /// Shows the construction-build panel for <paramref name="dozer"/> (the
    /// primary selected Dozer). Hides the building production panel so the
    /// two never overlap visually. Called by UnitSelector when a Dozer is
    /// added to the selection.
    /// </summary>
    public void ShowDozerBuildPanel(DozerBuilder dozer)
    {
        if (dozer == null)
        {
            HideDozerBuildPanel();
            return;
        }

        // Only log the transition (off → on) so re-selecting the same dozer
        // each frame doesn't spam the console.
        if (currentDozer != dozer)
            Debug.Log("[RTSHUD] Showing Dozer build panel.");

        currentDozer = dozer;

        // Hide the building production panel while the Dozer panel is up — the
        // two share the bottom-left corner and the player can't have both
        // selections active at once (building OR unit selection).
        HideProductionPanel();

        if (dozerBuildPanel != null)
            dozerBuildPanel.SetActive(true);

        // Refresh labels from BPM costs so they stay in sync if the player
        // tunes them in the inspector.
        if (placementManager != null)
        {
            if (dozerBuildBarracksLabel           != null) dozerBuildBarracksLabel.text           = $"Barracks - {placementManager.barracksCost}";
            if (dozerBuildPowerPlantLabel         != null) dozerBuildPowerPlantLabel.text         = $"Power Plant - {placementManager.powerPlantCost}";
            if (dozerBuildVehicleFactoryLabel     != null) dozerBuildVehicleFactoryLabel.text     = $"Vehicle Factory - {placementManager.vehicleFactoryCost}";
            if (dozerBuildAirfieldLabel           != null) dozerBuildAirfieldLabel.text           = $"Airfield - {placementManager.airfieldCost}";
            if (dozerBuildMachineGunDefenseLabel  != null) dozerBuildMachineGunDefenseLabel.text  = $"MG Defense - {placementManager.machineGunDefenseCost}";
            if (dozerBuildCommandCenterLabel      != null) dozerBuildCommandCenterLabel.text      = $"Command Center - {placementManager.commandCenterCost}";
        }
    }

    /// <summary>Hides the Dozer build panel and forgets the bound Dozer.</summary>
    public void HideDozerBuildPanel()
    {
        // Only log the transition (on → off).
        if (currentDozer != null)
            Debug.Log("[RTSHUD] Hiding Dozer build panel.");

        currentDozer = null;
        if (dozerBuildPanel != null)
            dozerBuildPanel.SetActive(false);
    }

    /// <summary>Called by the Dozer-build Barracks button.</summary>
    public void OnClickDozerBuildBarracks()
    {
        if (!ValidateDozerBuildClick("Barracks")) return;
        placementManager.StartDozerBuildBarracks(currentDozer);
    }

    /// <summary>Called by the Dozer-build Power Plant button.</summary>
    public void OnClickDozerBuildPowerPlant()
    {
        if (!ValidateDozerBuildClick("Power Plant")) return;
        placementManager.StartDozerBuildPowerPlant(currentDozer);
    }

    /// <summary>Called by the Dozer-build Vehicle Factory button.</summary>
    public void OnClickDozerBuildVehicleFactory()
    {
        if (!ValidateDozerBuildClick("Vehicle Factory")) return;
        placementManager.StartDozerBuildVehicleFactory(currentDozer);
    }

    /// <summary>Called by the Dozer-build Airfield button.</summary>
    public void OnClickDozerBuildAirfield()
    {
        if (!ValidateDozerBuildClick("Airfield")) return;
        placementManager.StartDozerBuildAirfield(currentDozer);
    }

    /// <summary>Called by the Dozer-build Machine Gun Defense button.</summary>
    public void OnClickDozerBuildMachineGunDefense()
    {
        if (!ValidateDozerBuildClick("Machine Gun Defense")) return;
        placementManager.StartDozerBuildMachineGunDefense(currentDozer);
    }

    /// <summary>Called by the Dozer-build Command Center button (Phase 10).</summary>
    public void OnClickDozerBuildCommandCenter()
    {
        if (!ValidateDozerBuildClick("Command Center")) return;
        placementManager.StartDozerBuildCommandCenter(currentDozer);
    }

    private bool ValidateDozerBuildClick(string label)
    {
        if (currentDozer == null)
        {
            Debug.LogWarning($"[RTSHUD] {label} build clicked but no Dozer is selected. " +
                             "Select a Dozer first.");
            return false;
        }
        if (placementManager == null)
        {
            Debug.LogWarning($"[RTSHUD] {label} build clicked but no BuildingPlacementManager is in the scene.");
            return false;
        }

        // Phase 10.1 — defense-in-depth. UnitSelector already only adds
        // locally-owned dozers to the selection, so currentDozer is
        // normally already local-owned by the time we get here. But a stale
        // reference (dozer destroyed mid-frame, scene reload, etc.) could
        // sneak through; double-check the GameEntity in MP and reject if
        // the dozer's owner isn't this client's LocalPlayerId.
        if (NetworkManagerRTS.IsMultiplayerEnabled)
        {
            GameEntity ge = currentDozer.GetComponent<GameEntity>();
            int local = NetworkManagerRTS.LocalPlayerId;
            int dozerOwner = ge != null ? ge.ownerPlayerId : GameEntity.NeutralOwnerId;
            if (ge == null || dozerOwner != local)
            {
                Debug.LogWarning($"[RTSHUD] {label} build click rejected — selected dozer " +
                                 $"owner={dozerOwner} but LocalPlayerId={local}. " +
                                 "Only the dozer's owner can issue build commands.");
                return false;
            }
        }
        return true;
    }

    // ================================================================== //
    // Transport panel — shown when an APC is selected
    // ================================================================== //

    /// <summary>
    /// Bind the panel to <paramref name="apc"/> and show it. Called by
    /// UnitSelector when at least one selected unit carries an APCTransport.
    /// </summary>
    public void ShowTransportPanel(APCTransport apc)
    {
        if (apc == null)
        {
            HideTransportPanel();
            return;
        }

        if (currentTransport != apc)
            Debug.Log($"[RTSHUD] Showing transport panel for '{apc.name}'.");

        currentTransport = apc;

        if (transportPanel != null)
            transportPanel.SetActive(true);

        RefreshTransportSlots();
    }

    /// <summary>Hide the transport panel and drop the bound APC reference.</summary>
    public void HideTransportPanel()
    {
        if (currentTransport != null)
            Debug.Log("[RTSHUD] Hiding transport panel.");

        currentTransport = null;
        if (transportPanel != null)
            transportPanel.SetActive(false);
    }

    /// <summary>
    /// Refresh the 6 slot labels + title based on the bound APC's passenger
    /// list. Called every LateUpdate while the panel is visible so passengers
    /// joining or leaving are reflected without an extra event hookup.
    /// </summary>
    private void RefreshTransportSlots()
    {
        if (currentTransport == null) return;

        int count = currentTransport.PassengerCount;
        int cap   = currentTransport.capacity;

        if (transportTitleLabel != null)
            transportTitleLabel.text = $"Transport ({count}/{cap})";

        for (int i = 0; i < transportSlotButtons.Length; i++)
        {
            bool occupied = i < count;
            GameObject slotGO = transportSlotButtons[i];

            if (transportSlotLabels != null && i < transportSlotLabels.Length && transportSlotLabels[i] != null)
            {
                if (occupied)
                {
                    GameObject p = currentTransport.Passengers[i];
                    transportSlotLabels[i].text = currentTransport.ResolvePassengerLabel(p);
                }
                else
                {
                    transportSlotLabels[i].text = "Empty";
                }
            }

            // Visual feedback: dim empty slots, brighten occupied ones via
            // child Image color if present. (The Button itself stays
            // interactable so the player can click empty slots — they no-op.)
            if (slotGO != null)
            {
                UnityEngine.UI.Image img = slotGO.GetComponent<UnityEngine.UI.Image>();
                if (img != null)
                {
                    Color c = img.color;
                    c.a = occupied ? 1.0f : 0.40f;
                    img.color = c;
                }
            }
        }
    }

    // ------------------------------------------------------------------ //
    // Transport button callbacks — wired by SetupRTSHUD
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Called by the Unload All button. Collects every selected friendly APC
    /// (plus the panel-bound APC as a fallback) into a single
    /// <see cref="PlayerCommand.UnloadTransport"/> and routes it through
    /// <see cref="CommandDispatcher"/>. The dispatcher walks the id array
    /// and calls <c>APCTransport.UnloadAll</c> on each — preserving the
    /// multi-APC staggered drop behaviour that's already in place.
    /// </summary>
    public void OnClickUnloadAll()
    {
        // Collect the set of APCs the player wants to unload. Mirror the old
        // behaviour exactly: selected APCs first, fall back to the panel-bound
        // one if the player has clicked into the HUD and lost selection focus.
        System.Collections.Generic.List<APCTransport> apcs = null;
        UnitSelector sel = UnitSelector.Instance;
        if (sel != null) apcs = sel.GetSelectedPlayerAPCs();
        if (apcs == null) apcs = new System.Collections.Generic.List<APCTransport>();

        if (apcs.Count == 0 && currentTransport != null)
            apcs.Add(currentTransport);

        if (apcs.Count == 0)
        {
            Debug.LogWarning("[RTSHUD] Unload All clicked but no APC is selected.");
            return;
        }

        // Snapshot the entity IDs. EnsureOn guarantees an APC that was placed
        // in-scene without going through the prefab tool still gets an id at
        // the point of the first command.
        string[] ids = new string[apcs.Count];
        for (int i = 0; i < apcs.Count; i++)
            ids[i] = GameEntity.EnsureOn(apcs[i].gameObject).EntityId;

        CommandDispatcher.Issue(
            PlayerCommand.UnloadTransport(GameEntity.LocalCommandPlayerId, ids));
    }

    /// <summary>
    /// Called when the player clicks a passenger slot. The slot index is
    /// hard-coded per button (6 buttons → 6 methods) because Unity's
    /// persistent button listeners can't pass arbitrary integer args.
    /// Empty slots silently no-op.
    /// </summary>
    public void OnClickUnloadSlot0() => UnloadSlotIfValid(0);
    public void OnClickUnloadSlot1() => UnloadSlotIfValid(1);
    public void OnClickUnloadSlot2() => UnloadSlotIfValid(2);
    public void OnClickUnloadSlot3() => UnloadSlotIfValid(3);
    public void OnClickUnloadSlot4() => UnloadSlotIfValid(4);
    public void OnClickUnloadSlot5() => UnloadSlotIfValid(5);

    private void UnloadSlotIfValid(int index)
    {
        if (currentTransport == null) return;
        if (index < 0 || index >= currentTransport.PassengerCount) return;     // empty slot
        currentTransport.UnloadPassengerAtIndex(index);
    }
}
