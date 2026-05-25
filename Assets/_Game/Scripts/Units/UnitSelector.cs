using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Handles single-click selection, drag-box multi-selection, and group move commands.
///
/// Setup:
///   1. Attach to your GameManager GameObject (already in scene).
///   2. Assign Unit Layer, Ground Layer in the Inspector.
///   3. Drag the SelectionBoxUI GameObject (a UI Image under a Screen Space - Overlay
///      Canvas) into the Selection Box Rect field.
///      - Its RectTransform must use the CENTER anchor preset (matches the Canvas
///        pivot) and pivot (0, 0). See the field tooltip for why.
/// </summary>
public class UnitSelector : MonoBehaviour
{
    [Header("Raycasting Layers")]
    [Tooltip("Layer(s) that unit colliders are on")]
    public LayerMask unitLayer;

    [Tooltip("Layer(s) that the ground/terrain is on")]
    public LayerMask groundLayer;

    [Tooltip("Layer(s) that ResourceNode objects are on (e.g. 'Resource' layer)")]
    public LayerMask resourceLayer;

    [Tooltip("Layer(s) that placed buildings are on (e.g. 'Building' layer)")]
    public LayerMask buildingLayer;

    [Header("Drag Selection")]
    [Tooltip("Minimum pixel distance before a left-click becomes a drag")]
    public float dragThreshold = 10f;

    [Header("Group Movement")]
    [Tooltip("World-unit spacing between units in the move formation")]
    public float formationSpacing = 2f;

    [Header("References")]
    [Tooltip("Drag the UI Image GameObject used for the drag rectangle here. " +
             "Any UI Image qualifies because it has a RectTransform. " +
             "Anchor preset: CENTER (must match the parent Canvas pivot of 0.5,0.5), " +
             "pivot (0, 0). A bottom-left anchor causes a half-screen offset.")]
    public RectTransform selectionBoxRect;

    // ------------------------------------------------------------------ //

    private readonly List<SelectableUnit>     selectedUnits     = new List<SelectableUnit>();
    private readonly List<SelectableAircraft> selectedAircraft  = new List<SelectableAircraft>();
    private SelectableBuilding                selectedBuilding;

    // Monotonically incrementing launch-group ID for multi-aircraft commands.
    // Static so the value is shared across UnitSelector lifetimes if the
    // scene reloads — collisions don't matter, only "do these aircraft share
    // the same id this command?" does.
    private static long nextLaunchGroupId = 0L;

    /// <summary>
    /// Returns 0 for a single-aircraft selection (solo command, no batch
    /// synchronization needed), or a unique non-zero ID shared across every
    /// aircraft in this multi-select command.
    /// </summary>
    private long AllocateAircraftGroupId(int aircraftCount)
    {
        if (aircraftCount <= 1) return 0L;
        long id = ++nextLaunchGroupId;
        Debug.Log($"[AircraftCommand] Group command issued: {id}, aircraft count: {aircraftCount}");
        return id;
    }
    private Camera mainCam;
    private Vector2 dragStartPos;
    private bool isDragging;
    // True only when the current mouse hold was started on the gameplay surface
    // (not over UI and not while placing a building). Without this gate, the
    // held-block re-evaluates Vector2.Distance against a stale dragStartPos and
    // spuriously opens the selection box after clicking HUD buttons.
    private bool clickStartedOnGameplay;
    private RectTransform selectionBoxParent;
    private RTSHUD hud;
    private AttackTargetMarker attackMarker;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        mainCam      = Camera.main;
        hud          = FindAnyObjectByType<RTSHUD>();
        attackMarker = FindAnyObjectByType<AttackTargetMarker>();

        if (attackMarker == null)
            Debug.LogError("UnitSelector: No AttackTargetMarker found in the scene.\n" +
                           "  Fix: Tools → RTS → Setup Attack Target Marker\n" +
                           "  (or manually Add Component → AttackTargetMarker on GameManager).\n" +
                           "  Combat still works without it, but no attack-target visual will show.");

        if (mainCam == null)
            Debug.LogError("UnitSelector: No Main Camera found. Tag your camera as MainCamera.");

        if (selectionBoxRect == null)
        {
            Debug.LogWarning("UnitSelector: selectionBoxRect is not assigned. Drag selection box will not appear.");
        }
        else
        {
            // We position the box in its parent's local space, so cache the parent
            // RectTransform. ScreenPointToLocalPointInRectangle converts the mouse
            // into that space, which is correct under any Canvas Scaler setting.
            selectionBoxParent = selectionBoxRect.parent as RectTransform;

            if (selectionBoxParent == null)
                Debug.LogWarning("UnitSelector: selectionBoxRect's parent is not a RectTransform. " +
                                 "Place the selection box under a UI Canvas so its parent is a RectTransform.");

            selectionBoxRect.gameObject.SetActive(false);
        }
    }

    private void Update()
    {
        // Yield all input to the placement system while a building is being placed
        if (BuildingPlacementManager.IsPlacing) return;

        HandleLeftMouse();
        HandleRightClick();
        HandleProductionHotkeys();
    }

    // ------------------------------------------------------------------ //
    // PRODUCTION HOTKEYS — routed to the currently selected building
    // ------------------------------------------------------------------ //

    private void HandleProductionHotkeys()
    {
        bool sPressed = Input.GetKeyDown(KeyCode.S);
        bool wPressed = Input.GetKeyDown(KeyCode.W);
        bool hPressed = Input.GetKeyDown(KeyCode.H);
        bool tPressed = Input.GetKeyDown(KeyCode.T);
        bool jPressed = Input.GetKeyDown(KeyCode.J);
        bool rPressed = Input.GetKeyDown(KeyCode.R);
        if (!sPressed && !wPressed && !hPressed && !tPressed && !jPressed && !rPressed) return;

        if (selectedBuilding == null)
        {
            string pressed = sPressed ? "S" : wPressed ? "W" : hPressed ? "H" :
                             tPressed ? "T" : jPressed ? "J" : "R";
            Debug.LogWarning($"[UnitSelector] Pressed {pressed} but no building is selected. " +
                             "Click a Barracks (S/R), CommandCenter (W), VehicleFactory (H/T), or Airfield (J) first.");
            return;
        }

        // S → Soldier (Barracks only). Silent no-op otherwise.
        if (sPressed)
        {
            UnitProducer up = selectedBuilding.GetComponent<UnitProducer>();
            if (up != null && up.CanProduceSoldier) up.ProduceSoldier();
        }

        // R → RPG Soldier (Barracks only). Silent no-op otherwise.
        if (rPressed)
        {
            UnitProducer up = selectedBuilding.GetComponent<UnitProducer>();
            if (up != null && up.CanProduceRPGSoldier) up.ProduceRPGSoldier();
        }

        // W → Worker (CommandCenter only). Silent no-op otherwise.
        if (wPressed)
        {
            CommandCenterProducer wp = selectedBuilding.GetComponent<CommandCenterProducer>();
            if (wp != null && wp.CanProduceWorker) wp.ProduceWorker();
        }

        // H → Humvee (VehicleFactory only). Silent no-op otherwise.
        if (hPressed)
        {
            VehicleFactoryProducer vp = selectedBuilding.GetComponent<VehicleFactoryProducer>();
            if (vp != null && vp.CanProduceHumvee) vp.ProduceHumvee();
        }

        // T → Artillery Tank (VehicleFactory only). Silent no-op otherwise.
        if (tPressed)
        {
            VehicleFactoryProducer vp = selectedBuilding.GetComponent<VehicleFactoryProducer>();
            if (vp != null && vp.CanProduceArtilleryTank) vp.ProduceArtilleryTank();
        }

        // J → Strike Jet (Airfield only). Silent no-op otherwise.
        if (jPressed)
        {
            Airfield af = selectedBuilding.GetComponent<Airfield>();
            if (af != null && af.CanProduceStrikeJet) af.ProduceStrikeJet();
        }
    }

    // ------------------------------------------------------------------ //
    // LEFT MOUSE — single click OR drag box
    // ------------------------------------------------------------------ //

    private void HandleLeftMouse()
    {
        // Record drag start
        if (Input.GetMouseButtonDown(0))
        {
            // Block drag/click when the pointer is over a UI element, or when the
            // placement manager has already entered placement mode this frame.
            bool overUI    = EventSystem.current != null &&
                             EventSystem.current.IsPointerOverGameObject();
            bool placing   = BuildingPlacementManager.IsPlacing;

            if (overUI || placing)
            {
                clickStartedOnGameplay = false;
                return;
            }

            dragStartPos           = Input.mousePosition;
            isDragging             = false;
            clickStartedOnGameplay = true;
        }

        // If the mouse-down did not land on the gameplay surface, ignore the rest
        // of the hold. This prevents stale dragStartPos values from re-triggering
        // the box when the user clicks a HUD button.
        if (!clickStartedOnGameplay) return;

        // While held: check if we crossed the drag threshold
        if (Input.GetMouseButton(0))
        {
            // If placement started mid-hold (e.g. button click handler enabled it),
            // abort the drag immediately and hide the box.
            if (BuildingPlacementManager.IsPlacing)
            {
                CancelDragSelection();
                return;
            }

            if (!isDragging &&
                Vector2.Distance(Input.mousePosition, dragStartPos) > dragThreshold)
            {
                isDragging = true;
                if (selectionBoxRect != null)
                    selectionBoxRect.gameObject.SetActive(true);
            }

            if (isDragging)
                RedrawBox(Input.mousePosition);
        }

        // On release: either box-select or single-click
        if (Input.GetMouseButtonUp(0))
        {
            if (isDragging)
            {
                Rect selectionRect = BuildScreenRect(dragStartPos, Input.mousePosition);

                if (selectionBoxRect != null)
                    selectionBoxRect.gameObject.SetActive(false);
                isDragging = false;
                SelectUnitsInRect(selectionRect);
            }
            else
            {
                HandleSingleClick();
            }

            clickStartedOnGameplay = false;
        }
    }

    // ------------------------------------------------------------------ //
    // PUBLIC — called by BuildingPlacementManager when placement begins
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Aborts any in-progress drag selection and hides the selection-box UI.
    /// Safe to call at any time; no-op when not dragging.
    /// </summary>
    public void CancelDragSelection()
    {
        isDragging             = false;
        clickStartedOnGameplay = false;

        if (selectionBoxRect != null && selectionBoxRect.gameObject.activeSelf)
            selectionBoxRect.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------ //
    // RIGHT CLICK — attack enemy  /  gather resource  /  move to ground
    // ------------------------------------------------------------------ //

    private void HandleRightClick()
    {
        if (!Input.GetMouseButtonDown(1)) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (selectedUnits.Count == 0 && selectedAircraft.Count == 0) return;

        Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);

        // --- Priority 1: enemy unit → attack -----------------------------
        if (Physics.Raycast(ray, out RaycastHit unitHit, Mathf.Infinity, unitLayer))
        {
            Health targetHealth = unitHit.collider.GetComponent<Health>()
                ?? unitHit.collider.GetComponentInParent<Health>();

            if (targetHealth != null && targetHealth.team == Health.Team.Enemy)
            {
                // Ground units use UnitCombat (chase + shoot on NavMesh) OR
                // RocketCombat (chase + fire RocketProjectile). A given unit
                // only has one of the two — null-conditional skips the other.
                // Also notify the auto-attack controller so it marks this as
                // a manual target and stops trying to override it.
                foreach (SelectableUnit unit in selectedUnits)
                {
                    unit.GetComponent<UnitCombat>()?.SetTarget(targetHealth);
                    unit.GetComponent<RocketCombat>()?.SetTarget(targetHealth);
                    unit.GetComponent<GroundAutoAttackController>()?.NotifyManualAttack(targetHealth);
                }

                // Aircraft use AirUnitController (takeoff + fly + missile + return).
                // Multi-aircraft selections share one launch group ID so the
                // Airfield synchronizes batch pair takeoff. Solo selections
                // get groupId = 0 (no sync, immediate roll after alignment).
                long aircraftGroupId = AllocateAircraftGroupId(selectedAircraft.Count);
                foreach (SelectableAircraft jet in selectedAircraft)
                    jet.GetComponent<AirUnitController>()?.AttackTarget(targetHealth, aircraftGroupId);

                if (attackMarker != null)
                {
                    attackMarker.Show(targetHealth.transform);
                    // TEMPORARY debug — remove once attack-marker wiring is verified.
                    Debug.Log("[UnitSelector] Attack command marker shown");
                }
                else
                {
                    Debug.LogError("[UnitSelector] Attack command but no AttackTargetMarker — " +
                                   "run Tools → RTS → Setup Attack Target Marker.");
                }
                return;
            }
        }

        // --- Priority 2: resource node → gather (workers only) ----------
        if (Physics.Raycast(ray, out RaycastHit resourceHit, Mathf.Infinity, resourceLayer))
        {
            ResourceNode node = resourceHit.collider.GetComponent<ResourceNode>()
                ?? resourceHit.collider.GetComponentInParent<ResourceNode>();

            if (node != null && !node.IsDepleted)
            {
                foreach (SelectableUnit unit in selectedUnits)
                    unit.GetComponent<WorkerGatherer>()?.SetGatherTarget(node);

                attackMarker?.Hide();
                return;
            }
        }

        // --- Priority 3: ground → move formation / fly-to-point patrol ---
        if (Physics.Raycast(ray, out RaycastHit groundHit, Mathf.Infinity, groundLayer))
        {
            foreach (SelectableUnit unit in selectedUnits)
            {
                unit.GetComponent<UnitCombat>()?.ClearTarget();
                unit.GetComponent<RocketCombat>()?.ClearTarget();
                unit.GetComponent<WorkerGatherer>()?.CancelGathering();
            }

            attackMarker?.Hide();

            // Compute formation slots, dispatch MoveTo + tell the auto-attack
            // controller this is the unit's new guard position so it scans
            // around the move destination, not its old spawn point.
            Vector3[] positions = GetFormationPositions(groundHit.point, selectedUnits.Count);
            for (int i = 0; i < selectedUnits.Count; i++)
            {
                SelectableUnit unit = selectedUnits[i];
                unit.GetComponent<UnitMovement>().MoveTo(positions[i]);
                unit.GetComponent<GroundAutoAttackController>()?.NotifyManualMove(positions[i]);
            }

            // Aircraft: fly to the clicked point and circle it before
            // returning home. AirUnitController ignores the call while mid-
            // attack, so an active strike isn't interrupted. Group ID matches
            // the attack path so synchronized batch takeoff still applies.
            long aircraftPatrolGroupId = AllocateAircraftGroupId(selectedAircraft.Count);
            foreach (SelectableAircraft jet in selectedAircraft)
                jet.GetComponent<AirUnitController>()?.FlyToPoint(groundHit.point, aircraftPatrolGroupId);
        }
    }

    // ------------------------------------------------------------------ //
    // SELECTION HELPERS
    // ------------------------------------------------------------------ //

    private void HandleSingleClick()
    {
        Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);

        // --- Priority 1: unit -------------------------------------------
        if (Physics.Raycast(ray, out RaycastHit unitHit, Mathf.Infinity, unitLayer))
        {
            SelectableUnit unit = unitHit.collider.GetComponent<SelectableUnit>()
                ?? unitHit.collider.GetComponentInParent<SelectableUnit>();

            if (unit != null)
            {
                DeselectBuilding();
                DeselectAll();
                AddToSelection(unit);
                return;
            }

            // Aircraft live on the Unit layer too but use SelectableAircraft.
            SelectableAircraft aircraft = unitHit.collider.GetComponent<SelectableAircraft>()
                ?? unitHit.collider.GetComponentInParent<SelectableAircraft>();

            if (aircraft != null)
            {
                DeselectBuilding();
                DeselectAll();
                AddAircraftToSelection(aircraft);
                return;
            }
        }

        // --- Priority 2: building ---------------------------------------
        if (Physics.Raycast(ray, out RaycastHit buildingHit, Mathf.Infinity, buildingLayer))
        {
            SelectableBuilding building = buildingHit.collider.GetComponent<SelectableBuilding>()
                ?? buildingHit.collider.GetComponentInParent<SelectableBuilding>();

            if (building != null)
            {
                DeselectAll();
                SelectBuilding(building);
                return;
            }
        }

        // --- Nothing hit — clear everything -----------------------------
        DeselectBuilding();
        DeselectAll();
    }

    private void SelectBuilding(SelectableBuilding building)
    {
        if (selectedBuilding != null && selectedBuilding != building)
            selectedBuilding.Deselect();

        selectedBuilding = building;
        selectedBuilding.Select();

        UnitProducer           soldierProd = building.GetComponent<UnitProducer>();
        CommandCenterProducer  workerProd  = building.GetComponent<CommandCenterProducer>();
        VehicleFactoryProducer vehicleProd = building.GetComponent<VehicleFactoryProducer>();
        Airfield               airfield    = building.GetComponent<Airfield>();
        bool canSoldier    = soldierProd != null && soldierProd.CanProduceSoldier;
        bool canRPGSoldier = soldierProd != null && soldierProd.CanProduceRPGSoldier;
        bool canWorker     = workerProd  != null && workerProd.CanProduceWorker;
        bool canHumvee     = vehicleProd != null && vehicleProd.CanProduceHumvee;
        bool canTank       = vehicleProd != null && vehicleProd.CanProduceArtilleryTank;
        bool canStrikeJet  = airfield    != null && airfield.CanProduceStrikeJet;

        string hint;
        if (canSoldier && canRPGSoldier)    hint = " (press S/R or click the Soldier/RPG Soldier button).";
        else if (canSoldier)                hint = " (press S or click the Soldier button).";
        else if (canRPGSoldier)             hint = " (press R or click the RPG Soldier button).";
        else if (canWorker)                 hint = " (press W or click the Worker button).";
        else if (canHumvee && canTank)      hint = " (press H/T or click the Humvee/Artillery Tank button).";
        else if (canHumvee)                 hint = " (press H or click the Humvee button).";
        else if (canTank)                   hint = " (press T or click the Artillery Tank button).";
        else if (canStrikeJet)              hint = $" (press J or click the Strike Jet button; free slots: {airfield.FreeSlotCount}/{Airfield.MaxSlots}).";
        else                                hint = " (no production options attached).";

        Debug.Log($"[UnitSelector] Building selected: '{building.name}'{hint}");

        if (hud != null)
        {
            if (canSoldier || canRPGSoldier || canWorker || canHumvee || canTank || canStrikeJet)
                hud.ShowProductionFor(building);
            else
                hud.HideProductionPanel();
        }
    }

    private void DeselectBuilding()
    {
        if (selectedBuilding == null) return;
        selectedBuilding.Deselect();
        selectedBuilding = null;

        if (hud != null)
            hud.HideProductionPanel();
    }

    private void SelectUnitsInRect(Rect screenRect)
    {
        DeselectBuilding();
        DeselectAll();

        // Ground units
        SelectableUnit[] allUnits =
            FindObjectsByType<SelectableUnit>(FindObjectsSortMode.None);
        foreach (SelectableUnit unit in allUnits)
        {
            Vector3 screenPos = mainCam.WorldToScreenPoint(unit.transform.position);
            if (screenPos.z > 0f && screenRect.Contains(screenPos))
                AddToSelection(unit);
        }

        // Aircraft — same drag-box rules so the player can multi-select jets.
        SelectableAircraft[] allAircraft =
            FindObjectsByType<SelectableAircraft>(FindObjectsSortMode.None);
        foreach (SelectableAircraft jet in allAircraft)
        {
            Vector3 screenPos = mainCam.WorldToScreenPoint(jet.transform.position);
            if (screenPos.z > 0f && screenRect.Contains(screenPos))
                AddAircraftToSelection(jet);
        }
    }

    private void AddToSelection(SelectableUnit unit)
    {
        if (selectedUnits.Contains(unit)) return;
        selectedUnits.Add(unit);
        unit.Select();
    }

    private void AddAircraftToSelection(SelectableAircraft aircraft)
    {
        if (selectedAircraft.Contains(aircraft)) return;
        selectedAircraft.Add(aircraft);
        aircraft.Select();
    }

    private void DeselectAll()
    {
        foreach (SelectableUnit unit in selectedUnits)
            unit.Deselect();
        selectedUnits.Clear();

        foreach (SelectableAircraft jet in selectedAircraft)
            jet.Deselect();
        selectedAircraft.Clear();

        // Clearing the selection cancels the player's intent to attack —
        // hide the marker even though the units already in flight will
        // continue their current command.
        attackMarker?.Hide();
    }

    // ------------------------------------------------------------------ //
    // FORMATION
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns world positions arranged in a centred grid around <paramref name="center"/>.
    /// </summary>
    private Vector3[] GetFormationPositions(Vector3 center, int count)
    {
        Vector3[] positions = new Vector3[count];
        int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(count)));

        for (int i = 0; i < count; i++)
        {
            int row = i / cols;
            int col = i % cols;
            float offsetX = (col - (cols - 1) * 0.5f) * formationSpacing;
            float offsetZ = -row * formationSpacing;
            positions[i] = center + new Vector3(offsetX, 0f, offsetZ);
        }

        return positions;
    }

    // ------------------------------------------------------------------ //
    // UTILITY
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Screen-space Rect for the current drag. Origin is bottom-left of the screen,
    /// matching Camera.WorldToScreenPoint coordinates used in SelectUnitsInRect.
    /// </summary>
    private static Rect BuildScreenRect(Vector2 a, Vector2 b)
    {
        Vector2 min = Vector2.Min(a, b);
        Vector2 max = Vector2.Max(a, b);
        return new Rect(min, max - min);
    }

    /// <summary>
    /// Positions and sizes the selection-box RectTransform to match the drag.
    /// Assumes a Screen Space - Overlay Canvas with a bottom-left anchor and (0,0) pivot,
    /// so anchoredPosition equals the box's bottom-left corner in screen pixels.
    /// </summary>
    private void RedrawBox(Vector2 currentScreenPos)
    {
        if (selectionBoxRect == null || selectionBoxParent == null) return;

        // Screen Space - Overlay → pass null as the camera. This maps both screen
        // points into the parent's local coordinate space, accounting for canvas
        // scale, reference resolution, and position automatically.
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            selectionBoxParent, dragStartPos, null, out Vector2 startLocal);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            selectionBoxParent, currentScreenPos, null, out Vector2 currentLocal);

        Vector2 min = Vector2.Min(startLocal, currentLocal);
        Vector2 max = Vector2.Max(startLocal, currentLocal);

        selectionBoxRect.anchoredPosition = min;
        selectionBoxRect.sizeDelta = max - min;
    }
}
