using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Manages placing buildings on the ground.
///
/// Controls:
///   B          — enter Barracks placement mode    (cost: barracksCost)
///   P          — enter PowerPlant placement mode  (cost: powerPlantCost)
///   Left click — confirm place (if valid and affordable)
///   Right click / Escape — cancel
///
/// Adding more building types later: add a new prefab/cost pair and a key-check
/// in Update() that calls TryEnterPlacementMode(prefab, cost, label).
///
/// Setup:
///   1. Add to GameManager.
///   2. Assign barracksPrefab and powerPlantPrefab.
///   3. Assign groundLayer, obstacleLayer.
///   4. Optionally assign ghostValidMaterial / ghostInvalidMaterial.
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

    [Tooltip("Y offset so the building sits on the ground surface (half the building's world height)")]
    public float placementHeightOffset = 0.75f;

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
        // Enter placement mode — one key per building type
        if (!IsPlacing)
        {
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

    /// <summary>Starts Barracks ghost placement. Safe to call from UI buttons.</summary>
    public void StartBarracksPlacement()
    {
        if (!IsPlacing)
            TryEnterPlacementMode(barracksPrefab, barracksCost, "Barracks");
    }

    /// <summary>Starts PowerPlant ghost placement. Safe to call from UI buttons.</summary>
    public void StartPowerPlantPlacement()
    {
        if (!IsPlacing)
            TryEnterPlacementMode(powerPlantPrefab, powerPlantCost, "PowerPlant");
    }

    /// <summary>Starts VehicleFactory ghost placement. Safe to call from UI buttons.</summary>
    public void StartVehicleFactoryPlacement()
    {
        if (!IsPlacing)
            TryEnterPlacementMode(vehicleFactoryPrefab, vehicleFactoryCost, "VehicleFactory");
    }

    /// <summary>Starts Airfield ghost placement. Safe to call from UI buttons.</summary>
    public void StartAirfieldPlacement()
    {
        if (!IsPlacing)
            TryEnterPlacementMode(airfieldPrefab, airfieldCost, "Airfield");
    }

    // ------------------------------------------------------------------ //
    // Placement mode entry / exit
    // ------------------------------------------------------------------ //

    private void TryEnterPlacementMode(GameObject prefab, int cost, string label)
    {
        if (prefab == null)
        {
            Debug.LogError($"BuildingPlacementManager: {label} prefab is not assigned.");
            return;
        }

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
        pos.y = hit.point.y + placementHeightOffset;

        ghost.transform.position = pos;
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

        // Instantiate the real building
        Vector3 pos = ghost.transform.position;
        GameObject placed = Instantiate(activePrefab, pos, Quaternion.identity);
        placed.name = activeLabel;

        // Ensure placed building is on the Building layer
        int buildingLayer = LayerMask.NameToLayer("Building");
        if (buildingLayer >= 0)
        {
            placed.layer = buildingLayer;
            foreach (Transform child in placed.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = buildingLayer;
        }

        resourceManager.SpendResources(activeCost);
        Debug.Log($"[Placement] {activeLabel} placed at {pos:F1}. Remaining resources: {resourceManager.CurrentResources}");

        CancelPlacement();
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
