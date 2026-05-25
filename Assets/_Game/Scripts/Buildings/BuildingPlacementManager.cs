using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Manages placing buildings on the ground. Supports two placement modes:
///
///   • Instant (debug) — left-over hotkeys B / P / V / F. Spawns the FINAL
///     building immediately. Gated behind <see cref="debugInstantBuildEnabled"/>
///     so normal gameplay (Dozer-driven construction) is the default.
///   • Dozer construction — called by the HUD when a Dozer is selected. Spawns
///     a <see cref="ConstructionSite"/> placeholder, deducts cost, and hands
///     the site to the dozer for incremental build progress.
///
/// Controls:
///   Left click  — confirm place (if valid and affordable)
///   Right click / Escape — cancel
///   B / P / V / F — instant-build hotkeys (only when debugInstantBuildEnabled)
///
/// Setup:
///   1. Add to GameManager.
///   2. Assign barracksPrefab, powerPlantPrefab, vehicleFactoryPrefab, airfieldPrefab.
///   3. Assign constructionSitePrefab (the placeholder spawned during Dozer builds).
///   4. Assign groundLayer, obstacleLayer.
///   5. Optionally assign ghostValidMaterial / ghostInvalidMaterial.
///
/// Important: UnitSelector.Update checks BuildingPlacementManager.IsPlacing
/// and skips all input processing while placement is active — no click conflicts.
/// </summary>
public class BuildingPlacementManager : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Buildings")]
    [Tooltip("The Barracks prefab — Building component, Building layer. Hotkey: B")]
    public GameObject barracksPrefab;
    [Tooltip("Resource cost for placing a Barracks")]
    public int barracksCost = 100;

    [Tooltip("The PowerPlant prefab — Building + PowerPlant components, Building layer. Hotkey: P")]
    public GameObject powerPlantPrefab;
    [Tooltip("Resource cost for placing a PowerPlant")]
    public int powerPlantCost = 150;

    [Tooltip("The VehicleFactory prefab — Building + SelectableBuilding + PowerConsumer + VehicleFactoryProducer. Hotkey: V")]
    public GameObject vehicleFactoryPrefab;
    [Tooltip("Resource cost for placing a VehicleFactory")]
    public int vehicleFactoryCost = 300;

    [Tooltip("The Airfield prefab — Building + SelectableBuilding + PowerConsumer + Airfield. Hotkey: F")]
    public GameObject airfieldPrefab;
    [Tooltip("Resource cost for placing an Airfield")]
    public int airfieldCost = 600;

    [Tooltip("The Machine Gun Defense prefab — Building + BuildingTurretCombat + PowerConsumer. " +
             "Anti-infantry / anti-air defensive turret. No instant-build hotkey by design.")]
    public GameObject machineGunDefensePrefab;
    [Tooltip("Resource cost for placing a Machine Gun Defense")]
    public int machineGunDefenseCost = 250;

    [Tooltip("Y offset so the building sits on the ground surface (half the building's world height)")]
    public float placementHeightOffset = 0.75f;

    [Header("Dozer Construction")]
    [Tooltip("Placeholder prefab spawned at the target location when a Dozer is ordered to build. " +
             "Carries the ConstructionSite component, a Building-layer BoxCollider sized to the " +
             "footprint, a flat foundation visual, and a small progress bar above the foundation.")]
    public GameObject constructionSitePrefab;

    [Tooltip("How long (seconds) it takes a single dozer with buildSpeedMultiplier=1 to construct a " +
             "building from 0% to 100%. Set very fast (1.0) during prototyping; raise for real play.")]
    public float dozerBuildTime = 1f;

    [Header("Debug — Instant Build")]
    [Tooltip("When true, the legacy hotkeys (B/P/V/F) and the bottom-right Build buttons place the " +
             "FINAL building immediately, bypassing the Dozer. Use during development only — " +
             "production gameplay uses Dozer-driven construction via StartDozerBuildingPlacement.")]
    public bool debugInstantBuildEnabled = false;

    [Header("Ghost Materials (optional — auto-created if empty)")]
    [Tooltip("Green transparent material for valid placement")]
    public Material ghostValidMaterial;

    [Tooltip("Red transparent material for invalid placement")]
    public Material ghostInvalidMaterial;

    [Header("Layers")]
    [Tooltip("Layer the ground/terrain is on")]
    public LayerMask groundLayer;

    [Tooltip("Layers that block placement: include Unit + Resource + Building")]
    public LayerMask obstacleLayer;

    [Header("Footprint Check")]
    [Tooltip("Half-extents of the overlap box used to test for obstacles (match to building footprint)")]
    public Vector3 footprintHalfExtents = new Vector3(1.3f, 1.0f, 1.3f);

    [Header("Grid Snapping")]
    public bool snapToGrid = true;
    [Tooltip("Grid cell size in world units")]
    public float gridSize = 1f;

    // ------------------------------------------------------------------ //
    // Static state (read by UnitSelector to block input during placement)
    // ------------------------------------------------------------------ //

    public static bool IsPlacing { get; private set; }

    // ------------------------------------------------------------------ //
    // Private runtime
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Two placement modes share the ghost / overlap / cost validation flow.
    ///   • Instant   — old debug path: spawn the final building on confirm.
    ///   • DozerSite — gameplay path:   spawn a ConstructionSite and assign a Dozer.
    /// </summary>
    private enum PlacementMode { Instant, DozerSite }

    private Camera        mainCam;
    private PlayerResourceManager resourceManager;

    private GameObject    ghost;
    private Renderer[]    ghostRenderers;
    private bool          isValid;

    private Material      runtimeValidMat;
    private Material      runtimeInvalidMat;

    // Active placement session — set by TryEnterPlacementMode
    private GameObject    activePrefab;
    private int           activeCost;
    private string        activeLabel;
    private PlacementMode activeMode = PlacementMode.Instant;
    private DozerBuilder  activeDozer;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        mainCam         = Camera.main;
        resourceManager = FindAnyObjectByType<PlayerResourceManager>();

        if (resourceManager == null)
            Debug.LogError("BuildingPlacementManager: No PlayerResourceManager found in scene.");
    }

    private void Start()
    {
        // Build transparent fallback materials if none assigned in Inspector
        runtimeValidMat   = ghostValidMaterial   ?? CreateTransparentMaterial(new Color(0.0f, 1.0f, 0.1f, 0.40f));
        runtimeInvalidMat = ghostInvalidMaterial ?? CreateTransparentMaterial(new Color(1.0f, 0.1f, 0.0f, 0.40f));
    }

    private void Update()
    {
        // Gate everything while the main menu is still up.
        if (!GameStateManager.IsPlaying)
        {
            if (IsPlacing) CancelPlacement();
            return;
        }

        // Enter instant-placement mode via hotkeys — gated on debug switch.
        // Normal gameplay enters DozerSite placement via HUD callbacks.
        if (!IsPlacing)
        {
            if (!debugInstantBuildEnabled) return;

            if (Input.GetKeyDown(KeyCode.B))
                TryEnterPlacementMode(barracksPrefab,       barracksCost,       "Barracks");
            else if (Input.GetKeyDown(KeyCode.P))
                TryEnterPlacementMode(powerPlantPrefab,     powerPlantCost,     "PowerPlant");
            else if (Input.GetKeyDown(KeyCode.V))
                TryEnterPlacementMode(vehicleFactoryPrefab, vehicleFactoryCost, "VehicleFactory");
            else if (Input.GetKeyDown(KeyCode.F))
                TryEnterPlacementMode(airfieldPrefab,       airfieldCost,       "Airfield");
            return;
        }

        if (!IsPlacing) return;

        UpdateGhostTransform();
        UpdateGhostColor();

        // Confirm placement — skip if the click landed on a UI element
        if (Input.GetMouseButtonDown(0))
        {
            if (!EventSystem.current.IsPointerOverGameObject())
                TryPlace();
            return;
        }

        // Cancel
        if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape))
            CancelPlacement();
    }

    // ------------------------------------------------------------------ //
    // Public API — called by RTSHUD buttons (or keyboard shortcuts)
    // ------------------------------------------------------------------ //

    /// <summary>Starts Barracks ghost placement (instant mode — debug only).</summary>
    public void StartBarracksPlacement()
    {
        if (IsPlacing) return;
        if (!debugInstantBuildEnabled)
        {
            Debug.LogWarning("[Placement] Direct Barracks placement is disabled. " +
                             "Select a Dozer and click Barracks in its build panel.");
            return;
        }
        TryEnterPlacementMode(barracksPrefab, barracksCost, "Barracks");
    }

    /// <summary>Starts PowerPlant ghost placement (instant mode — debug only).</summary>
    public void StartPowerPlantPlacement()
    {
        if (IsPlacing) return;
        if (!debugInstantBuildEnabled)
        {
            Debug.LogWarning("[Placement] Direct PowerPlant placement is disabled. " +
                             "Select a Dozer and click Power Plant in its build panel.");
            return;
        }
        TryEnterPlacementMode(powerPlantPrefab, powerPlantCost, "PowerPlant");
    }

    /// <summary>Starts VehicleFactory ghost placement (instant mode — debug only).</summary>
    public void StartVehicleFactoryPlacement()
    {
        if (IsPlacing) return;
        if (!debugInstantBuildEnabled)
        {
            Debug.LogWarning("[Placement] Direct VehicleFactory placement is disabled. " +
                             "Select a Dozer and click Vehicle Factory in its build panel.");
            return;
        }
        TryEnterPlacementMode(vehicleFactoryPrefab, vehicleFactoryCost, "VehicleFactory");
    }

    /// <summary>Starts Airfield ghost placement (instant mode — debug only).</summary>
    public void StartAirfieldPlacement()
    {
        if (IsPlacing) return;
        if (!debugInstantBuildEnabled)
        {
            Debug.LogWarning("[Placement] Direct Airfield placement is disabled. " +
                             "Select a Dozer and click Airfield in its build panel.");
            return;
        }
        TryEnterPlacementMode(airfieldPrefab, airfieldCost, "Airfield");
    }

    // ------------------------------------------------------------------ //
    // Dozer-driven construction-site placement
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Enters DOZER placement mode. The ghost shows the final building's silhouette
    /// but on confirm, instead of spawning the building, a <see cref="ConstructionSite"/>
    /// is created and <paramref name="dozer"/> is dispatched to it.
    ///
    /// Use this from the HUD when the player has a Dozer selected and clicks
    /// one of the build-panel buttons (Barracks / PowerPlant / VehicleFactory / Airfield).
    /// </summary>
    public void StartDozerBuildingPlacement(DozerBuilder dozer, GameObject buildingPrefab, int cost, string label)
    {
        if (IsPlacing)
        {
            Debug.LogWarning("[Placement] Already placing — ignoring duplicate request.");
            return;
        }

        if (dozer == null)
        {
            Debug.LogWarning("[Placement] Cannot start Dozer build placement: dozer is null.");
            return;
        }

        if (constructionSitePrefab == null)
        {
            Debug.LogError("[Placement] constructionSitePrefab is not assigned. " +
                           "Run Tools → RTS → Construction → Repair Construction System.");
            return;
        }

        activeMode  = PlacementMode.DozerSite;
        activeDozer = dozer;
        TryEnterPlacementMode(buildingPrefab, cost, label);
    }

    /// <summary>Convenience wrapper — Barracks build by Dozer.</summary>
    public void StartDozerBuildBarracks(DozerBuilder dozer) =>
        StartDozerBuildingPlacement(dozer, barracksPrefab, barracksCost, "Barracks");

    /// <summary>Convenience wrapper — PowerPlant build by Dozer.</summary>
    public void StartDozerBuildPowerPlant(DozerBuilder dozer) =>
        StartDozerBuildingPlacement(dozer, powerPlantPrefab, powerPlantCost, "PowerPlant");

    /// <summary>Convenience wrapper — VehicleFactory build by Dozer.</summary>
    public void StartDozerBuildVehicleFactory(DozerBuilder dozer) =>
        StartDozerBuildingPlacement(dozer, vehicleFactoryPrefab, vehicleFactoryCost, "VehicleFactory");

    /// <summary>Convenience wrapper — Airfield build by Dozer.</summary>
    public void StartDozerBuildAirfield(DozerBuilder dozer) =>
        StartDozerBuildingPlacement(dozer, airfieldPrefab, airfieldCost, "Airfield");

    /// <summary>Convenience wrapper — Machine Gun Defense build by Dozer.</summary>
    public void StartDozerBuildMachineGunDefense(DozerBuilder dozer) =>
        StartDozerBuildingPlacement(dozer, machineGunDefensePrefab, machineGunDefenseCost, "Machine Gun Defense");

    // ------------------------------------------------------------------ //
    // Placement mode entry / exit
    // ------------------------------------------------------------------ //

    private void TryEnterPlacementMode(GameObject prefab, int cost, string label)
    {
        if (prefab == null)
        {
            Debug.LogError($"BuildingPlacementManager: {label} prefab is not assigned.");
            activeDozer = null;
            activeMode  = PlacementMode.Instant;
            return;
        }

        // activeMode is set by the caller in the DozerSite path; default to Instant otherwise.
        // (Hotkeys + StartXxxPlacement always go through here without touching activeMode,
        // so we explicitly default the field for the legacy/instant entry points.)
        if (activeDozer == null) activeMode = PlacementMode.Instant;

        activePrefab = prefab;
        activeCost   = cost;
        activeLabel  = label;
        IsPlacing    = true;

        // Make sure any in-progress drag selection is aborted and its UI hidden.
        // Otherwise a HUD button click can leave a stale selection rectangle on screen.
        UnitSelector selector = FindAnyObjectByType<UnitSelector>();
        if (selector != null)
            selector.CancelDragSelection();

        // Spawn ghost — a visual-only copy of the prefab
        ghost = Instantiate(activePrefab);
        ghost.name = $"_Ghost{activeLabel}";

        // Strip ALL colliders so the ghost is invisible to raycasts and NavMesh
        foreach (Collider col in ghost.GetComponentsInChildren<Collider>(true))
            Destroy(col);

        // Strip gameplay scripts to prevent side-effects
        foreach (MonoBehaviour mb in ghost.GetComponentsInChildren<MonoBehaviour>(true))
            Destroy(mb);

        ghostRenderers = ghost.GetComponentsInChildren<Renderer>(true);
    }

    private void CancelPlacement()
    {
        if (ghost != null)
        {
            Destroy(ghost);
            ghost = null;
        }

        ghostRenderers = null;
        isValid        = false;
        IsPlacing      = false;
        activeMode     = PlacementMode.Instant;
        activeDozer    = null;
    }

    // ------------------------------------------------------------------ //
    // Ghost update
    // ------------------------------------------------------------------ //

    private void UpdateGhostTransform()
    {
        Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);

        if (!Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, groundLayer))
        {
            ghost.SetActive(false);
            isValid = false;
            return;
        }

        ghost.SetActive(true);

        // Position — optionally snapped to grid on XZ, height offset on Y
        Vector3 pos = hit.point;
        if (snapToGrid)
        {
            pos.x = Mathf.Round(pos.x / gridSize) * gridSize;
            pos.z = Mathf.Round(pos.z / gridSize) * gridSize;
        }
        pos.y = hit.point.y + GetActivePlacementOffset();

        ghost.transform.position = pos;
    }

    /// <summary>
    /// Picks the right Y offset for the currently-active placement. If the
    /// prefab's Building component sets placementYOffsetOverride ≥ 0 we use
    /// that; otherwise we fall back to the global <see cref="placementHeightOffset"/>.
    /// This lets the Airfield place its root at exactly ground level (offset 0)
    /// while all other buildings keep the original 0.75 lift.
    /// </summary>
    private float GetActivePlacementOffset()
    {
        if (activePrefab != null)
        {
            Building b = activePrefab.GetComponent<Building>();
            if (b != null && b.placementYOffsetOverride >= 0f)
                return b.placementYOffsetOverride;
        }
        return placementHeightOffset;
    }

    private void UpdateGhostColor()
    {
        if (!ghost.activeSelf) return;

        bool affordable = resourceManager != null && resourceManager.CanAfford(activeCost);
        bool clearGround = IsFootprintClear(ghost.transform.position);

        isValid = affordable && clearGround;

        Material mat = isValid ? runtimeValidMat : runtimeInvalidMat;

        foreach (Renderer r in ghostRenderers)
            r.material = mat;
    }

    // ------------------------------------------------------------------ //
    // Placement
    // ------------------------------------------------------------------ //

    private void TryPlace()
    {
        if (!ghost.activeSelf)
        {
            Debug.LogWarning("[Placement] Cannot place: mouse is not over the ground.");
            return;
        }

        if (!IsFootprintClear(ghost.transform.position))
        {
            Debug.LogWarning("[Placement] Cannot place: location is blocked by a unit, resource, or building.");
            return;
        }

        if (resourceManager == null || !resourceManager.CanAfford(activeCost))
        {
            Debug.LogWarning($"[Placement] Cannot place {activeLabel}: need {activeCost} resources, " +
                             $"have {(resourceManager != null ? resourceManager.CurrentResources : 0)}.");
            return;
        }

        Vector3 pos = ghost.transform.position;

        if (activeMode == PlacementMode.DozerSite)
            PlaceConstructionSite(pos);
        else
            PlaceFinalBuildingInstant(pos);

        CancelPlacement();
    }

    /// <summary>Instant-mode confirm — spawns the final building straight away (debug path).</summary>
    private void PlaceFinalBuildingInstant(Vector3 pos)
    {
        GameObject placed = Instantiate(activePrefab, pos, Quaternion.identity);
        placed.name = activeLabel;

        int buildingLayer = LayerMask.NameToLayer("Building");
        if (buildingLayer >= 0)
        {
            placed.layer = buildingLayer;
            foreach (Transform child in placed.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = buildingLayer;
        }

        resourceManager.SpendResources(activeCost);
        Debug.Log($"[Placement] {activeLabel} placed at {pos:F1} (instant). " +
                  $"Remaining resources: {resourceManager.CurrentResources}");
    }

    /// <summary>Dozer-mode confirm — spawns a ConstructionSite and assigns the dozer to it.</summary>
    private void PlaceConstructionSite(Vector3 pos)
    {
        GameObject siteGO = Instantiate(constructionSitePrefab, pos, Quaternion.identity);
        siteGO.name = $"{activeLabel} (Construction Site)";

        int buildingLayer = LayerMask.NameToLayer("Building");
        if (buildingLayer >= 0)
        {
            siteGO.layer = buildingLayer;
            foreach (Transform child in siteGO.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = buildingLayer;
        }

        ConstructionSite site = siteGO.GetComponent<ConstructionSite>();
        if (site == null)
        {
            Debug.LogError("[Placement] constructionSitePrefab is missing the ConstructionSite component. " +
                           "Run Tools → RTS → Construction → Create Construction Site Prefab.");
            Destroy(siteGO);
            return;
        }

        site.Initialise(activePrefab, activeCost, dozerBuildTime, activeLabel, Quaternion.identity);

        if (activeDozer != null)
            activeDozer.AssignBuildOrder(site);

        resourceManager.SpendResources(activeCost);
        Debug.Log($"[Construction] {activeLabel} site placed at {pos:F1} — dozer dispatched. " +
                  $"Remaining resources: {resourceManager.CurrentResources}");
    }

    // ------------------------------------------------------------------ //
    // Overlap / validity
    // ------------------------------------------------------------------ //

    /// <summary>True when no obstacle colliders overlap the building footprint.</summary>
    private bool IsFootprintClear(Vector3 worldPos)
    {
        // Center the check at the building's approximate center (not offset by height)
        Vector3 checkCenter = new Vector3(worldPos.x, worldPos.y, worldPos.z);
        return !Physics.CheckBox(checkCenter, footprintHalfExtents, Quaternion.identity, obstacleLayer);
    }

    // ------------------------------------------------------------------ //
    // Transparent material factory (URP + Standard fallback)
    // ------------------------------------------------------------------ //

    private static Material CreateTransparentMaterial(Color color)
    {
        // Try URP Lit first; fall back to Standard for non-URP projects
        Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                     ?? Shader.Find("Standard");

        if (shader == null)
        {
            Debug.LogWarning("BuildingPlacementManager: Could not find a suitable shader for ghost material.");
            return new Material(Shader.Find("Sprites/Default")) { color = color };
        }

        Material mat = new Material(shader);

        if (shader.name.Contains("Universal"))
        {
            // URP transparent settings
            mat.SetFloat("_Surface",       1f);  // Transparent
            mat.SetFloat("_Blend",         0f);  // Alpha
            mat.SetFloat("_AlphaClip",     0f);
            mat.SetInt("_SrcBlend",        5);   // SrcAlpha
            mat.SetInt("_DstBlend",        10);  // OneMinusSrcAlpha
            mat.SetInt("_ZWrite",          0);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.renderQueue = 3000;
        }
        else
        {
            // Standard shader transparent settings
            mat.SetFloat("_Mode",          3f);  // Transparent
            mat.SetInt("_SrcBlend",        5);
            mat.SetInt("_DstBlend",        10);
            mat.SetInt("_ZWrite",          0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }

        mat.color = color;
        return mat;
    }

    // ------------------------------------------------------------------ //
    // Scene gizmo — shows the footprint size in the Scene view
    // ------------------------------------------------------------------ //

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!IsPlacing || ghost == null || !ghost.activeSelf) return;
        Gizmos.color = isValid ? new Color(0, 1, 0, 0.3f) : new Color(1, 0, 0, 0.3f);
        Gizmos.DrawWireCube(ghost.transform.position, footprintHalfExtents * 2f);
    }
#endif
}
