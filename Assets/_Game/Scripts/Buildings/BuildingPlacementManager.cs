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

    // Phase 10 — CommandCenter buildable via the Dozer build menu. In MP
    // each player now starts with just a Dozer and constructs their own CC
    // before producing Worker / Dozer.
    [Tooltip("CommandCenter prefab — built by a Dozer in the new MP flow. " +
             "Create via Tools → RTS → Construction → Create CommandCenter Prefab.")]
    public GameObject commandCenterPrefab;
    [Tooltip("Resource cost for placing a CommandCenter")]
    public int commandCenterCost = 1000;

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
    // Phase 3: per-spend bank lookup via ResourceBank.For(owner). The local
    // PlayerResourceManager reference is gone — placement charges the dozer's
    // owner bank in DozerSite mode and the local player's bank in Instant.
    private GameObject    ghost;
    private Renderer[]    ghostRenderers;
    private bool          isValid;

    private Material      runtimeValidMat;
    private Material      runtimeInvalidMat;

    // Phase 10.12 — reusable MaterialPropertyBlock applied to every ghost
    // renderer each frame so the ghost is painted by the LOCAL player's
    // team color (valid) or red (invalid), independent of whatever
    // ownership-driven MPB the prefab's now-destroyed TeamColorMarker left
    // behind. See TryEnterPlacementMode / UpdateGhostColor.
    private static readonly int GhostBaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int GhostColorId     = Shader.PropertyToID("_Color");
    private MaterialPropertyBlock ghostMpb;

    // Active placement session — set by TryEnterPlacementMode
    private GameObject    activePrefab;
    private int           activeCost;
    private string        activeLabel;
    private PlacementMode activeMode = PlacementMode.Instant;
    private DozerBuilder  activeDozer;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        mainCam = Camera.main;
    }

    /// <summary>
    /// Owner whose bank should be charged for placement-mode actions. In
    /// DozerSite mode this comes from the active dozer's GameEntity; in
    /// Instant mode (debug) it defaults to local player 0 — the legacy
    /// single-player behaviour.
    /// </summary>
    private int GetActiveOwnerId()
    {
        if (activeDozer != null)
        {
            GameEntity ge = activeDozer.GetComponent<GameEntity>();
            if (ge != null) return ge.ownerPlayerId;
        }
        // Instant / fallback: use the local player in MP, 0 in SP.
        int localPid = NetworkManagerRTS.LocalPlayerId;
        return localPid >= 0 ? localPid : 0;
    }

    private PlayerResourceManager ActiveBank() => ResourceBank.For(GetActiveOwnerId());

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

        // Phase 10.1 — strict local-owner gate. The dozer MUST belong to this
        // client's LocalPlayerId in multiplayer; the HUD already only shows
        // the build panel for locally-owned dozers (UnitSelector filter), but
        // this is the defense-in-depth check that prevents a stale reference
        // or a fabricated UI event from starting a build with someone else's
        // dozer.
        GameEntity dozerEntity = dozer.GetComponent<GameEntity>();
        if (NetworkManagerRTS.IsMultiplayerEnabled)
        {
            int localId = NetworkManagerRTS.LocalPlayerId;
            int dozerOwner = dozerEntity != null ? dozerEntity.ownerPlayerId : GameEntity.NeutralOwnerId;
            if (dozerEntity == null || dozerOwner != localId)
            {
                Debug.LogWarning($"[Placement] Rejected {label} build — dozer '{dozer.name}' " +
                                 $"owner={dozerOwner} but LocalPlayerId={localId}. " +
                                 "A client may only build with its own Dozer.");
                return;
            }
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

    /// <summary>Convenience wrapper — CommandCenter build by Dozer (Phase 10).</summary>
    public void StartDozerBuildCommandCenter(DozerBuilder dozer) =>
        StartDozerBuildingPlacement(dozer, commandCenterPrefab, commandCenterCost, "CommandCenter");

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

        // Phase 10.12 — the prefab's TeamColorMarker.OnEnable ran during
        // Instantiate (before the strip above destroyed it). It wrote a
        // MaterialPropertyBlock to every renderer using
        // MultiplayerColors[GameEntity.ownerPlayerId] — which on the prefab
        // is the default 0 = slot 0 = Player 1's color. The MPB persists
        // through the component destroy and overrides whatever color we
        // later set on the ghost's material. Clear it now so the ghost
        // tint comes from UpdateGhostColor below, not from a stale
        // ownership-driven paint.
        MaterialPropertyBlock empty = new MaterialPropertyBlock();
        foreach (Renderer r in ghostRenderers)
            if (r != null) r.SetPropertyBlock(empty);
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

        PlayerResourceManager activeBank = ActiveBank();
        bool affordable = activeBank != null && activeBank.CanAfford(activeCost);
        bool clearGround = IsFootprintClear(ghost.transform.position);

        isValid = affordable && clearGround;

        // Phase 10.12 — ghost paint:
        //   Valid   → LOCAL player's team color (per MultiplayerColors lookup
        //              for the active owner; SP falls back to PFM.SelectedColor).
        //   Invalid → red.
        // The runtime material (alpha-blended transparent) provides the
        // see-through preview look; the MaterialPropertyBlock owns the color
        // override so the LOCAL client always sees the LOCAL team color
        // regardless of the prefab's stamped ownerPlayerId. This is what
        // stops Player 2 from seeing the ghost in Player 1's color.
        int    localOwner = GetActiveOwnerId();         // dozer-owner in DozerSite, local player in Instant
        Color  teamColor  = PlayerFactionManager.GetColorForOwner(localOwner);
        teamColor.a = 0.50f;

        Color tint = isValid
            ? teamColor
            : new Color(1.00f, 0.15f, 0.05f, 0.50f);    // red-invalid

        Material mat = isValid ? runtimeValidMat : runtimeInvalidMat;

        if (ghostMpb == null) ghostMpb = new MaterialPropertyBlock();
        ghostMpb.SetColor(GhostBaseColorId, tint);      // URP Lit
        ghostMpb.SetColor(GhostColorId,     tint);      // Standard / Sprites

        foreach (Renderer r in ghostRenderers)
        {
            if (r == null) continue;
            r.material = mat;                           // instance material (transparent shader)
            r.SetPropertyBlock(ghostMpb);               // team-color (or red-invalid) override
        }
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

        PlayerResourceManager activeBank = ActiveBank();
        if (activeBank == null || !activeBank.CanAfford(activeCost))
        {
            Debug.LogWarning($"[Placement] Cannot place {activeLabel}: owner " +
                             $"{GetActiveOwnerId()} needs {activeCost} resources, " +
                             $"have {(activeBank != null ? activeBank.CurrentResources : 0)}.");
            return;
        }

        Vector3 pos = ghost.transform.position;

        if (activeMode == PlacementMode.DozerSite)
        {
            // Route the confirmed Dozer placement through CommandDispatcher.
            // BPM owns the placement-mode UX (ghost preview, mouse tracking,
            // validation just done above); the dispatcher owns the canonical
            // "place site here" intent that a future networked layer will
            // need to replicate. ExecuteBuild calls back into
            // ExecuteConfirmedDozerPlacement with the same position + ids.
            //
            // Phase 10.1 — owner authority. The dozer MUST have a GameEntity
            // with a non-neutral owner. If it doesn't, we refuse to issue
            // the command at all (no LocalCommandPlayerId fallback) — that
            // fallback could place the building under whichever client
            // happened to confirm it, bypassing the actual dozer's
            // ownership.
            GameEntity dozerEntity = activeDozer != null
                ? GameEntity.EnsureOn(activeDozer.gameObject) : null;
            if (dozerEntity == null
                || dozerEntity.ownerPlayerId == GameEntity.NeutralOwnerId)
            {
                Debug.LogError($"[Placement] Refused to issue {activeLabel} build — " +
                               "active dozer has no valid GameEntity / ownerPlayerId. " +
                               "Cancelling placement to avoid creating an unowned building.");
                CancelPlacement();
                return;
            }

            string dozerId      = dozerEntity.EntityId;
            int    dozerOwnerId = dozerEntity.ownerPlayerId;

            // Mint two ids in one go so they don't interleave with other
            // allocations: one for the construction site itself, one for the
            // final building the site spawns on Complete().
            string[] ids = NetworkEntityIdAllocator.AllocateBatch(2);
            string siteId  = ids[0];
            string finalId = ids[1];

            Debug.Log($"[BPM] Build {activeLabel} using dozer entityId='{dozerId}' " +
                      $"owner={dozerOwnerId} cmd.playerId={dozerOwnerId}");

            // cmd.playerId IS the dozer's owner, full stop. Dispatcher will
            // re-validate this against the dozer it resolves by id, so a
            // forged command from a misbehaving client is rejected on the
            // receiver too.
            CommandDispatcher.Issue(PlayerCommand.Build(
                dozerOwnerId, dozerId, activeLabel, pos, siteId, finalId));
        }
        else
        {
            PlaceFinalBuildingInstant(pos);
        }

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

        PlayerResourceManager instBank = ActiveBank();
        if (instBank != null) instBank.SpendResources(activeCost);
        Debug.Log($"[Placement] {activeLabel} placed at {pos:F1} (instant). " +
                  $"Remaining resources (owner {GetActiveOwnerId()}): " +
                  $"{(instBank != null ? instBank.CurrentResources : 0)}");
    }

    /// <summary>
    /// Public entry point used by <see cref="CommandDispatcher"/> to execute
    /// a confirmed Dozer-driven placement. Fully self-contained — does NOT
    /// rely on BPM's placement-mode state. The originating client AND any
    /// remote replay client can call this; both end up with a construction
    /// site at <paramref name="pos"/> sharing
    /// <paramref name="siteEntityId"/>, and the dozer (if resolvable)
    /// assigned to it.
    ///
    /// <paramref name="ownerPlayerId"/> is the canonical owner the site (and
    /// the final building it spawns) must adopt. The dispatcher passes
    /// <c>cmd.playerId</c> here after validating it against the dozer's
    /// GameEntity, so the spawn path no longer guesses ownership.
    /// </summary>
    public void ExecuteConfirmedDozerPlacement(
        Vector3 pos,
        string  buildingType,
        string  dozerEntityId,
        string  siteEntityId,
        string  finalBuildingEntityId,
        int     ownerPlayerId)
    {
        // Resolve prefab + cost from the building-type string. Lets the
        // remote replay path work without needing BPM's placement state.
        GameObject prefab = ResolveBuildingPrefab(buildingType, out int cost);
        if (prefab == null)
        {
            Debug.LogWarning($"[Placement] Unknown buildingType '{buildingType}' — ignoring Build command.");
            return;
        }

        // Resolve dozer via EntityRegistry. On the originating client this
        // will match activeDozer; on a remote client this is the only way to
        // identify the dozer that should drive the site.
        DozerBuilder dozer = null;
        if (!string.IsNullOrEmpty(dozerEntityId))
        {
            GameEntity dozerEntity = EntityRegistry.Find(dozerEntityId);
            if (dozerEntity != null)
                dozer = dozerEntity.GetComponent<DozerBuilder>();
        }

        SpawnConstructionSite(prefab, cost, buildingType, pos, dozer,
                              siteEntityId, finalBuildingEntityId, ownerPlayerId);
    }

    /// <summary>
    /// Builds a (prefab, cost) pair for <paramref name="buildingType"/> using
    /// BPM's Inspector-assigned prefab/cost fields. Returns (null, 0) for
    /// unknown types. Must mirror the labels passed into
    /// <see cref="StartDozerBuildingPlacement"/> by the public wrappers above.
    /// </summary>
    private GameObject ResolveBuildingPrefab(string buildingType, out int cost)
    {
        switch (buildingType)
        {
            case "Barracks":             cost = barracksCost;          return barracksPrefab;
            case "PowerPlant":           cost = powerPlantCost;        return powerPlantPrefab;
            case "VehicleFactory":       cost = vehicleFactoryCost;    return vehicleFactoryPrefab;
            case "Airfield":             cost = airfieldCost;          return airfieldPrefab;
            case "MachineGunDefense":
            case "Machine Gun Defense":  cost = machineGunDefenseCost; return machineGunDefensePrefab;
            case "CommandCenter":        cost = commandCenterCost;     return commandCenterPrefab;
        }
        cost = 0;
        return null;
    }

    /// <summary>
    /// Self-contained construction-site spawn. Used by
    /// <see cref="ExecuteConfirmedDozerPlacement"/> for both the local-issue
    /// path and the remote replay path. Pushes the network-allocated id via
    /// <see cref="GameEntity.SetNextSpawnId"/> so the spawned site's
    /// GameEntity adopts it during Awake.
    ///
    /// <paramref name="ownerPlayerId"/> is THE canonical owner — both the
    /// site's <see cref="ConstructionSite.OwnerPlayerId"/> (used by
    /// <see cref="ConstructionSite.Complete"/> to stamp the final building)
    /// AND the bank that gets charged for <paramref name="cost"/> derive
    /// from this parameter. The dispatcher validates the value against the
    /// dozer's <see cref="GameEntity.ownerPlayerId"/> before getting here,
    /// so there's no further guess-work to do in SpawnConstructionSite.
    /// </summary>
    private void SpawnConstructionSite(
        GameObject finalPrefab,
        int        cost,
        string     label,
        Vector3    pos,
        DozerBuilder dozer,
        string     siteEntityId,
        string     finalBuildingEntityId,
        int        ownerPlayerId)
    {
        if (constructionSitePrefab == null)
        {
            Debug.LogError("[Placement] constructionSitePrefab is not assigned.");
            return;
        }

        // Instantiate the construction site (prefab carries no GameEntity by
        // default). We push the spawn-id slot LATER, right before EnsureOn
        // adds the GameEntity at runtime — that AddComponent triggers Awake
        // which consumes the preset. Pushing before Instantiate (the legacy
        // ordering) wouldn't help here because no GameEntity exists yet to
        // consume the slot.
        GameObject siteGO = Instantiate(constructionSitePrefab, pos, Quaternion.identity);
        siteGO.name = $"{label} (Construction Site)";

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
            Debug.LogError("[Placement] constructionSitePrefab is missing the ConstructionSite component.");
            Destroy(siteGO);
            return;
        }

        // OWNERSHIP — phase 10.1 fix. Stamp BOTH the GameEntity (so
        // EntityRegistry can find the site by id) AND the ConstructionSite's
        // own OwnerPlayerId field (canonical, used by Complete()) before any
        // other side-effect can read them. This is what makes the final
        // building inherit the correct owner on every client, even when the
        // construction site prefab carries no GameEntity by default.
        //
        // Order matters: push the network-allocated id INTO the slot, then
        // call EnsureOn — EnsureOn's AddComponent<GameEntity>() fires
        // Awake, which consumes the slot. Always clear the slot in finally
        // so no leak into the next spawn.
        GameEntity.SetNextSpawnId(siteEntityId);
        GameEntity siteEntity;
        try
        {
            siteEntity = GameEntity.EnsureOn(siteGO);
        }
        finally
        {
            GameEntity.SetNextSpawnId(null);
        }
        if (siteEntity != null)
        {
            siteEntity.entityType   = EntityType.Building;
            siteEntity.prefabTypeId = label + "Site";
            siteEntity.ApplyOwnership(ownerPlayerId);
        }
        site.SetOwner(ownerPlayerId);

        site.Initialise(finalPrefab, cost, dozerBuildTime, label, Quaternion.identity);
        // Phase 2: also hand the site the final-building id so that, when it
        // completes, the spawned building can adopt the same id on every client.
        site.SetFinalBuildingEntityId(finalBuildingEntityId);

        if (dozer != null) dozer.AssignBuildOrder(site);

        // Charge the bank that matches the canonical owner. Both clients
        // execute this on Build replay; each charges the same owner's bank,
        // so the per-player balance stays consistent across clients.
        PlayerResourceManager bank = ResourceBank.For(ownerPlayerId);
        if (bank != null) bank.SpendResources(cost);

        Debug.Log($"[NetworkSpawn] Construction site '{label}' placed at {pos:F1} " +
                  $"with siteId='{siteEntityId}', finalId='{finalBuildingEntityId}', " +
                  $"owner={ownerPlayerId}. Remaining resources (owner {ownerPlayerId}): " +
                  $"{(bank != null ? bank.CurrentResources : 0)}");
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
