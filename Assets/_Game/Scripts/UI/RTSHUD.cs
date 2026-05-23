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

    [Tooltip("Optional. The TMP label inside the Soldier button — updated to show " +
             "cost when a producer is selected. Leave null to keep the static label.")]
    public TextMeshProUGUI soldierButtonLabel;

    // ------------------------------------------------------------------ //
    // Private references — found at runtime
    // ------------------------------------------------------------------ //

    private PlayerResourceManager resourceManager;
    private PowerManager           powerManager;
    private BuildingPlacementManager placementManager;

    // Currently selected building's UnitProducer (null when nothing producible is selected)
    private UnitProducer currentProducer;

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

    // ------------------------------------------------------------------ //
    // Production panel — driven by UnitSelector when buildings are selected
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Shows the bottom-left production panel and binds its Soldier button to the
    /// given producer. Called by UnitSelector when a Barracks is selected.
    /// </summary>
    public void ShowProductionPanel(UnitProducer producer)
    {
        if (producer == null)
        {
            HideProductionPanel();
            return;
        }

        currentProducer = producer;

        if (productionPanel != null)
            productionPanel.SetActive(true);

        if (soldierButtonLabel != null)
            soldierButtonLabel.text = $"Soldier - {producer.soldierCost}";
    }

    /// <summary>Hides the production panel and forgets the bound producer.</summary>
    public void HideProductionPanel()
    {
        currentProducer = null;

        if (productionPanel != null)
            productionPanel.SetActive(false);
    }

    /// <summary>Called by the Soldier button. Produces from the bound UnitProducer.</summary>
    public void OnClickProduceSoldier()
    {
        if (currentProducer == null)
        {
            Debug.LogWarning("[RTSHUD] Soldier button clicked but no producer is selected. " +
                             "Select a Barracks first.");
            return;
        }

        currentProducer.ProduceSoldier();
    }
}
