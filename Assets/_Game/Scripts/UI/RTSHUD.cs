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

    [Tooltip("Strike Jet button GameObject. Visibility is toggled per producer " +
             "(visible only when the selected Airfield.CanProduceStrikeJet).")]
    public GameObject strikeJetButton;

    [Tooltip("The TMP label inside the Strike Jet button — updated to show cost " +
             "when a producer is selected. Leave null to keep the static label.")]
    public TextMeshProUGUI strikeJetButtonLabel;

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
    }

    private void LateUpdate()
    {
        RefreshResources();
        RefreshPower();
    }

    // ------------------------------------------------------------------ //
    // Text refresh
    // ------------------------------------------------------------------ //

    private void RefreshResources()
    {
        if (resourcesText == null || resourceManager == null) return;
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

        int supply = powerManager.TotalSupply;
        int demand = powerManager.TotalDemand;
        bool powered = powerManager.IsPowered;

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

        bool showSoldier    = soldierProd != null && soldierProd.CanProduceSoldier;
        bool showRPGSoldier = soldierProd != null && soldierProd.CanProduceRPGSoldier;
        bool showWorker     = workerProd  != null && workerProd.CanProduceWorker;
        bool showHumvee     = vehicleProd != null && vehicleProd.CanProduceHumvee;
        bool showTank       = vehicleProd != null && vehicleProd.CanProduceArtilleryTank;
        bool showStrikeJet  = airfield    != null && airfield.CanProduceStrikeJet;

        if (!showSoldier && !showRPGSoldier && !showWorker && !showHumvee && !showTank && !showStrikeJet)
        {
            HideProductionPanel();
            return;
        }

        // Soldier producer drives both Soldier and RPG Soldier buttons.
        currentSoldierProducer = (showSoldier || showRPGSoldier) ? soldierProd : null;
        currentWorkerProducer  = showWorker                       ? workerProd  : null;
        // Vehicle producer is bound when either of its outputs is available.
        currentVehicleProducer = (showHumvee || showTank)         ? vehicleProd : null;
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

        // --- Humvee button --------------------------------------------- //
        if (humveeButton != null)
            humveeButton.SetActive(showHumvee);

        if (showHumvee && humveeButtonLabel != null)
            humveeButtonLabel.text = $"Humvee - {vehicleProd.humveeCost}";

        // --- Artillery Tank button ------------------------------------- //
        if (tankButton != null)
            tankButton.SetActive(showTank);

        if (showTank && tankButtonLabel != null)
            tankButtonLabel.text = $"Artillery Tank - {vehicleProd.artilleryTankCost}";

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
        if (currentSoldierProducer == null)
        {
            Debug.LogWarning("[RTSHUD] Soldier button clicked but no Soldier producer is selected. " +
                             "Select a Barracks first.");
            return;
        }

        currentSoldierProducer.ProduceSoldier();
    }

    /// <summary>Called by the RPG Soldier button. Produces from the bound UnitProducer.</summary>
    public void OnClickProduceRPGSoldier()
    {
        if (currentSoldierProducer == null)
        {
            Debug.LogWarning("[RTSHUD] RPG Soldier button clicked but no producer is selected. " +
                             "Select a Barracks first.");
            return;
        }

        currentSoldierProducer.ProduceRPGSoldier();
    }

    /// <summary>Called by the Worker button. Produces from the bound CommandCenterProducer.</summary>
    public void OnClickProduceWorker()
    {
        if (currentWorkerProducer == null)
        {
            Debug.LogWarning("[RTSHUD] Worker button clicked but no Worker producer is selected. " +
                             "Select a CommandCenter first.");
            return;
        }

        currentWorkerProducer.ProduceWorker();
    }

    /// <summary>Called by the Humvee button. Produces from the bound VehicleFactoryProducer.</summary>
    public void OnClickProduceHumvee()
    {
        if (currentVehicleProducer == null)
        {
            Debug.LogWarning("[RTSHUD] Humvee button clicked but no Vehicle producer is selected. " +
                             "Select a VehicleFactory first.");
            return;
        }

        currentVehicleProducer.ProduceHumvee();
    }

    /// <summary>Called by the Artillery Tank button. Produces from the bound VehicleFactoryProducer.</summary>
    public void OnClickProduceArtilleryTank()
    {
        if (currentVehicleProducer == null)
        {
            Debug.LogWarning("[RTSHUD] Artillery Tank button clicked but no Vehicle producer is selected. " +
                             "Select a VehicleFactory first.");
            return;
        }

        currentVehicleProducer.ProduceArtilleryTank();
    }

    /// <summary>Called by the Strike Jet button. Produces from the bound Airfield.</summary>
    public void OnClickProduceStrikeJet()
    {
        if (currentAirfield == null)
        {
            Debug.LogWarning("[RTSHUD] Strike Jet button clicked but no Airfield is selected. " +
                             "Select an Airfield first.");
            return;
        }

        currentAirfield.ProduceStrikeJet();
    }
}
