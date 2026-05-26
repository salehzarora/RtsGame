using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Production + parking + takeoff-queue management for the Airfield building.
///
/// Each Airfield has exactly <see cref="MaxSlots"/> aircraft slots, a 6-entry
/// taxi-point array (one taxi waypoint per slot), two runway lanes (A/B) with
/// queue + start + end markers, and a landing approach point.
///
/// Aircraft do NOT take off whenever they please — they ask the Airfield for
/// clearance via <see cref="RequestTakeoffClearance"/>. The Airfield runs a
/// FIFO queue with at most two concurrent takeoff slots (one per runway lane),
/// so a six-jet attack order taxis out in three pairs.
///
/// Setup (done automatically by Tools → RTS → Air System → Create Airfield Prefab):
///   1. Attach to the Airfield root (alongside Building, SelectableBuilding,
///      PowerConsumer, and a Collider).
///   2. Drag the Strike Jet prefab into Strike Jet Prefab.
///   3. Assign six child Transforms into Slots[0..5] (parking pads).
///   4. Assign six child Transforms into TaxiPoints[0..5] (one taxi waypoint
///      per slot, aligned with the apron edge).
///   5. Assign the runway markers: RunwayQueuePoint A/B, TakeoffStart A/B,
///      TakeoffEnd A/B, LandingApproachPoint.
///
/// Production controls (while the Airfield is selected):
///   • Click the Strike Jet button in the bottom-left production panel
///   • Or press J
/// </summary>
[RequireComponent(typeof(SelectableBuilding))]
public class Airfield : MonoBehaviour
{
    /// <summary>Hard cap on parking slots per Airfield, per spec.</summary>
    public const int MaxSlots = 6;

    // ------------------------------------------------------------------ //
    // Inspector — Strike Jet
    // ------------------------------------------------------------------ //

    [Header("Strike Jet Production")]
    [Tooltip("The Strike Jet prefab to instantiate")]
    public GameObject strikeJetPrefab;

    [Tooltip("Resource cost per Strike Jet")]
    public int strikeJetCost = 450;

    [Tooltip("Keyboard shortcut to produce a Strike Jet (UnitSelector hardcodes J)")]
    public KeyCode produceStrikeJetKey = KeyCode.J;

    // ------------------------------------------------------------------ //
    // Inspector — Parking
    // ------------------------------------------------------------------ //

    [Header("Parking Slots (exactly 6)")]
    [Tooltip("Transform children that mark each aircraft parking position. " +
             "Their rotation is used as the parked rotation.")]
    public Transform[] slots = new Transform[MaxSlots];

    [Header("Taxi Points (one per slot)")]
    [Tooltip("First taxi waypoint for each slot — usually a point on the apron " +
             "just outside the parking pad, facing the runway. Index aligns with Slots.")]
    public Transform[] taxiPoints = new Transform[MaxSlots];

    // ------------------------------------------------------------------ //
    // Inspector — Runway Lanes
    // ------------------------------------------------------------------ //

    [Header("Runway Lane A (left lane, hosts even-index slots)")]
    [Tooltip("Ordered list of shared taxi waypoints between the per-slot taxi point and the " +
             "Lane-A runway queue point. Use 1–2 entries to space Lane A jets away from Lane B " +
             "so their wings don't clip.")]
    public Transform[] laneATaxiPoints;
    public Transform runwayQueuePointA;
    public Transform takeoffStartA;
    public Transform takeoffEndA;

    [Header("Runway Lane B (right lane, hosts odd-index slots)")]
    [Tooltip("Ordered list of shared taxi waypoints between the per-slot taxi point and the " +
             "Lane-B runway queue point. Sits on a different X than Lane A so the two paths " +
             "stay visually separate.")]
    public Transform[] laneBTaxiPoints;
    public Transform runwayQueuePointB;
    public Transform takeoffStartB;
    public Transform takeoffEndB;

    [Header("Landing — Lane A (primary)")]
    [Tooltip("Final-approach waypoint. Returning aircraft fly here at altitude, then " +
             "request landing clearance.")]
    public Transform landingApproachPoint;

    [Tooltip("Point on the runway where descent begins. Aircraft transitions to FinalLanding " +
             "when it arrives here at altitude and begins descending toward LandingEnd_A.")]
    public Transform landingStartA;

    [Tooltip("Point on the runway where the landing roll ends. Aircraft is on the ground here.")]
    public Transform landingEndA;

    [Tooltip("Off-runway turn point. Aircraft exits the runway here and begins taxiing back " +
             "to its assigned parking slot.")]
    public Transform landingExitA;

    [Header("Landing — Lane B (reserved, not yet used in v1)")]
    public Transform landingStartB;
    public Transform landingEndB;
    public Transform landingExitB;

    // ------------------------------------------------------------------ //
    // Inspector — Takeoff Queue
    // ------------------------------------------------------------------ //

    [Header("Takeoff Queue")]
    [Tooltip("Maximum aircraft that may be in the taxi/roll/climb phase at the " +
             "same time. Capped to 2 (one per runway lane).")]
    [Range(1, 2)] public int maxConcurrentTakeoffs = 2;

    [Tooltip("Minimum seconds between two consecutive clearance grants. " +
             "Prevents two aircraft from sharing the exact same takeoff moment.")]
    public float takeoffSpacingSeconds = 1.0f;

    [Tooltip("If true, queue events log to the Console (queued / granted / released).")]
    public bool queueDebugLogs = true;

    [Header("Synchronized Group Takeoff")]
    [Tooltip("When true, aircraft sharing a non-zero LaunchGroupId wait for their batch " +
             "partner to be aligned at the runway before both roll together. " +
             "Single-aircraft commands (LaunchGroupId = 0) always launch immediately.")]
    public bool synchronizedGroupTakeoff = true;

    [Tooltip("Hard cap on aircraft per synchronized launch batch. Effectively 2 (one per lane).")]
    [Range(1, 2)] public int maxAircraftPerLaunchBatch = 2;

    [Tooltip("Safety timeout in seconds. A ready aircraft waiting for its batch partner this " +
             "long is launched solo so the queue cannot deadlock if the partner is destroyed.")]
    public float batchWaitTimeout = 5f;

    [Tooltip("Optional extra delay between the two aircraft's takeoff-roll start within the " +
             "same batch. Default 0 — both roll on the same frame.")]
    public float takeoffPairSpacingSeconds = 0f;

    [Header("Runway Traffic Control")]
    [Tooltip("If true, takeoffs and landings share one runway lock: an active landing " +
             "blocks new takeoff clearances, and an active takeoff-roll/pre-roll blocks " +
             "new landing clearances. v1 uses this single shared lock.")]
    public bool landingUsesSharedRunway = true;

    [Tooltip("If true, runway-busy / runway-released events log to the Console.")]
    public bool runwayBusyDebugLogs = true;

    // ------------------------------------------------------------------ //
    // Runway ownership model (used by debug tools + landing deadlock guard)
    // ------------------------------------------------------------------ //

    /// <summary>What kind of operation currently owns the runway, if any.</summary>
    public enum RunwayMode { None, Takeoff, Landing }

    /// <summary>
    /// Derived from existing state — the runway is owned for Landing while
    /// <see cref="activeLandingJet"/> is non-null, for Takeoff while any lane
    /// has an aircraft in pre-roll or rolling, otherwise None.
    /// </summary>
    public RunwayMode CurrentRunwayMode
    {
        get
        {
            if (activeLandingJet != null)       return RunwayMode.Landing;
            if (IsTakeoffPreRollOrRollActive()) return RunwayMode.Takeoff;
            return RunwayMode.None;
        }
    }

    /// <summary>
    /// Single point-of-truth for "which aircraft holds the runway". For Landing
    /// this is <see cref="activeLandingJet"/>; for Takeoff it's the first lane
    /// found in pre-roll/roll (lane A preferred). Null when CurrentRunwayMode == None.
    /// </summary>
    public AirUnitController CurrentRunwayOwner
    {
        get
        {
            if (activeLandingJet != null) return activeLandingJet;
            if (IsConflictingTakeoffState(activeJetA)) return activeJetA;
            if (IsConflictingTakeoffState(activeJetB)) return activeJetB;
            return null;
        }
    }

    // ------------------------------------------------------------------ //
    // Public types
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Bundle of waypoints handed to an aircraft when it receives takeoff
    /// clearance. <see cref="TaxiRoute"/> is the full ordered path the jet
    /// follows: per-slot pull-out point → lane corridor → runway queue point
    /// → takeoff start. The last entry is always the takeoff start, so the
    /// aircraft knows when the taxi is done.
    ///
    /// After the taxi ends the aircraft pivots in place toward
    /// <see cref="TakeoffStart"/> → <see cref="TakeoffEnd"/>, then rolls to
    /// <see cref="TakeoffEnd"/> and releases the lane.
    /// </summary>
    public class TakeoffClearance
    {
        public Transform[] TaxiRoute;        // ordered list (per-slot → lane corridor → queue → start)
        public Transform   TakeoffStart;     // last waypoint of TaxiRoute; also alignment anchor
        public Transform   TakeoffEnd;       // alignment direction + roll end
        public int         Lane;             // 0 = A, 1 = B
    }

    /// <summary>
    /// Bundle of landing waypoints + taxi-back route handed to an aircraft when
    /// it receives landing clearance.
    ///   LandingApproach → LandingStart (descent begins) → LandingEnd (touchdown)
    ///   → LandingExit (off-runway turn) → TaxiBackRoute (per-slot taxi to home).
    /// </summary>
    public class LandingClearance
    {
        public Transform   LandingApproach;
        public Transform   LandingStart;
        public Transform   LandingEnd;
        public Transform   LandingExit;
        public Transform[] TaxiBackRoute;   // exit → slot's taxi point → slot
        public int         Lane;            // 0 = A (only A in v1)
    }

    // ------------------------------------------------------------------ //
    // Capability flag — used by the HUD
    // ------------------------------------------------------------------ //

    public bool CanProduceStrikeJet => strikeJetPrefab != null;

    public int FreeSlotCount
    {
        get
        {
            int free = 0;
            for (int i = 0; i < parked.Length; i++)
                if (parked[i] == null) free++;
            return free;
        }
    }

    // ------------------------------------------------------------------ //
    // Runtime — parking
    // ------------------------------------------------------------------ //

    private readonly GameObject[] parked = new GameObject[MaxSlots];
    // Phase 3: owner-aware bank lookup via ResourceBank.For(OwnerId) on spend.
    private GameEntity selfEntity;
    private int OwnerId => (selfEntity ?? (selfEntity = GetComponent<GameEntity>())) != null
        ? selfEntity.ownerPlayerId : 0;

    // ------------------------------------------------------------------ //
    // Runtime — takeoff queue
    // ------------------------------------------------------------------ //

    // The jet currently occupying each lane (anywhere in Taxi→Roll→Climb).
    // Unity-null counts as "lane free" via the overloaded == operator, so a
    // destroyed jet auto-frees its lane without explicit cleanup.
    private AirUnitController activeJetA;
    private AirUnitController activeJetB;

    // FIFO queue of jets waiting for a lane to free up.
    private readonly Queue<AirUnitController> takeoffQueue = new Queue<AirUnitController>();

    // Earliest time the next clearance grant may fire. Updated on every grant.
    private float nextClearanceTime;

    // Batch-sync state. readyJetA/B are set when each lane's aircraft has
    // finished alignment and notified us via NotifyReadyForTakeoffRoll().
    // jetAReadyTime / jetBReadyTime drive the partner-timeout safety net.
    private bool  readyJetA;
    private bool  readyJetB;
    private float jetAReadyTime;
    private float jetBReadyTime;

    // Deferred Lane B release if takeoffPairSpacingSeconds > 0.
    private AirUnitController pendingLaneBJet;
    private float             pendingLaneBReleaseTime;

    // The aircraft currently using the runway for landing. Non-null while the
    // jet is in LandingApproach / FinalLanding; cleared when it transitions
    // to TaxiingToSlot (the runway is then clear even though the jet is still
    // on the airfield, since taxi-back uses the apron taxiways).
    private AirUnitController activeLandingJet;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        selfEntity = GetComponent<GameEntity>();

        if (slots == null || slots.Length != MaxSlots)
        {
            Debug.LogWarning($"Airfield on '{name}': Slots array has {(slots?.Length ?? 0)} entries " +
                             $"(expected {MaxSlots}). Re-run Air System → Validate Airfield Slots.");
        }

        if (taxiPoints == null || taxiPoints.Length != MaxSlots)
        {
            Debug.LogWarning($"Airfield on '{name}': TaxiPoints array has {(taxiPoints?.Length ?? 0)} entries " +
                             $"(expected {MaxSlots}). Re-run Air System → Repair Airfield Layout.");
        }
    }

    private void Update()
    {
        // Orphaned-owner detection: if the landing aircraft is destroyed or
        // has wandered into a non-landing state without releasing, force-clear
        // the lock so queued aircraft + holding aircraft can proceed.
        CheckOrphanedLandingOwner();

        // Try to dispatch the head of the queue every frame. The method
        // gates itself on lane availability and the spacing timer.
        TryGrantFromQueue();

        // Batch-takeoff bookkeeping: deferred Lane B release + partner timeout.
        TickDeferredLaneBRelease();
        CheckBatchTimeout();
    }

    /// <summary>
    /// Force-clear <see cref="activeLandingJet"/> when its owner is destroyed
    /// (Unity-null) or has somehow left the landing state machine without
    /// calling <see cref="ReleaseLandingRunway"/>. Without this guard, a single
    /// missed release call would deadlock the airfield permanently — every
    /// subsequent returning aircraft would circle in the holding pattern
    /// forever.
    /// </summary>
    private void CheckOrphanedLandingOwner()
    {
        if (activeLandingJet == null) return;

        // Unity-overloaded == catches destroyed Objects too.
        AirUnitController owner = activeLandingJet;

        // 1. Owner's home airfield got rebound elsewhere (rare, but defensive).
        if (owner.HomeAirfield != this)
        {
            Debug.LogWarning($"[Airfield] Runway owner lost — force releasing runway. " +
                             $"(Owner '{owner.name}' no longer belongs to this Airfield.)");
            activeLandingJet = null;
            return;
        }

        // 2. Owner is in a state outside the active landing chain. They must
        //    have transitioned out (e.g. snapped Parked via timeout) without
        //    calling ReleaseLandingRunway.
        if (!IsActiveLandingState(owner))
        {
            Debug.LogWarning($"[Airfield] Runway owner '{owner.name}' is in state " +
                             $"{owner.State} (not an active landing state) — force releasing runway.");
            activeLandingJet = null;
        }
    }

    /// <summary>
    /// True while the aircraft genuinely owns the runway for landing — from
    /// the moment landing clearance is granted up to (and including) the
    /// FinalLanding rollout. TaxiingToSlot, Parked, and any airborne / takeoff
    /// state are NOT landing-runway-owning.
    /// </summary>
    private static bool IsActiveLandingState(AirUnitController jet)
    {
        if (jet == null) return false;
        var s = jet.State;
        return s == AirUnitController.FlightState.WaitingForLandingClearance
            || s == AirUnitController.FlightState.LandingApproach
            || s == AirUnitController.FlightState.FinalLanding;
    }

    /// <summary>
    /// Public entry point for an aircraft (or a debug tool) to ask the Airfield
    /// to verify its runway owner. If the owner is dead / in a wrong state,
    /// the runway is force-released and queued aircraft can proceed on the
    /// next Update tick. <paramref name="reason"/> is logged for traceability.
    /// </summary>
    public void ForceReleaseRunwayIfOrphaned(string reason)
    {
        if (activeLandingJet == null) return;

        // Re-use the standard orphan check; it logs internally on success.
        AirUnitController before = activeLandingJet;
        CheckOrphanedLandingOwner();
        if (activeLandingJet == null && before != null)
        {
            Debug.LogWarning($"[Airfield] Runway force-released due to: {reason}.");
        }
    }

    /// <summary>
    /// Unconditional runway release — clears <see cref="activeLandingJet"/>
    /// and any pending batch state. Intended for the debug editor tool
    /// (Tools → RTS → Air System → Force Release Runway). Not for gameplay.
    /// </summary>
    public void DebugForceReleaseRunway()
    {
        if (activeLandingJet != null)
        {
            Debug.LogWarning($"[Airfield] DEBUG force-release: clearing landing owner " +
                             $"'{activeLandingJet.name}'.");
            activeLandingJet = null;
        }

        // Clear any zombie batch-ready flags whose owning jets are gone.
        if (activeJetA == null) readyJetA = false;
        if (activeJetB == null) readyJetB = false;
        if (pendingLaneBJet == null) pendingLaneBJet = null;
    }

    // ------------------------------------------------------------------ //
    // Public — production
    // ------------------------------------------------------------------ //

    /// <summary>Spawn one Strike Jet in the first free slot. No-op (logs) on failure.</summary>
    public void ProduceStrikeJet()
    {
        if (!CanProduceStrikeJet)
        {
            Debug.Log($"[Airfield] '{name}' has no Strike Jet prefab assigned — ignoring.");
            return;
        }

        // Phase 3: owner-aware bank resolution happens at the spend site below
        // — no early-bind PlayerResourceManager reference any more.

        PowerConsumer power = GetComponent<PowerConsumer>();
        if (power != null && !power.IsPowered)
        {
            Debug.LogWarning("[Power] Not enough power. Strike Jet production paused. " +
                             "Build a PowerPlant (P) to restore power.");
            return;
        }

        int slotIndex = FindFreeSlotIndex();
        if (slotIndex < 0)
        {
            Debug.LogWarning("[Airfield] No free aircraft slots.");
            return;
        }
        Transform slot = slots[slotIndex];
        if (slot == null)
        {
            Debug.LogError($"[Airfield] Slot {slotIndex} Transform is missing — " +
                           "run Air System → Validate Airfield Slots.");
            return;
        }

        int ownerId = OwnerId;
        PlayerResourceManager bank = ResourceBank.For(ownerId);
        if (bank == null)
        {
            Debug.LogError($"[Airfield] Cannot produce Strike Jet: " +
                           $"no PlayerResourceManager registered for owner {ownerId}.");
            return;
        }
        if (!bank.CanAfford(strikeJetCost))
        {
            Debug.LogWarning($"[Airfield] Not enough resources to produce Strike Jet. " +
                             $"Need {strikeJetCost}, have {bank.CurrentResources} (owner {ownerId}).");
            return;
        }

        GameObject jet = Instantiate(strikeJetPrefab, slot.position, slot.rotation);
        jet.name = $"StrikeJet_{slotIndex}";

        AirUnitController controller = jet.GetComponent<AirUnitController>();
        if (controller != null)
        {
            controller.AssignHome(this, slot);
        }
        else
        {
            Debug.LogWarning($"[Airfield] Strike Jet prefab is missing AirUnitController — " +
                             "aircraft will have no flight behaviour.");
        }

        parked[slotIndex] = jet;
        bank.SpendResources(strikeJetCost);

        Debug.Log($"[Airfield] Strike Jet produced in slot {slotIndex} at {slot.position:F1}. " +
                  $"Free slots: {FreeSlotCount}/{MaxSlots}. " +
                  $"Remaining resources (owner {ownerId}): {bank.CurrentResources}.");
    }

    // ------------------------------------------------------------------ //
    // Public — takeoff queue API (called by AirUnitController)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Aircraft entry point. Returns true and fills <paramref name="clearance"/>
    /// if a lane is immediately available; otherwise enqueues the aircraft and
    /// returns false. The Airfield calls back via
    /// <see cref="AirUnitController.GrantTakeoffClearance"/> once a lane frees.
    /// </summary>
    public bool RequestTakeoffClearance(AirUnitController jet, out TakeoffClearance clearance)
    {
        clearance = null;
        if (jet == null) return false;

        // Already queued or active — no double-counting.
        if (activeJetA == jet || activeJetB == jet) return false;
        foreach (AirUnitController queued in takeoffQueue)
            if (queued == jet) return false;

        // A landing is using the runway — defer the takeoff.
        if (landingUsesSharedRunway && activeLandingJet != null)
        {
            takeoffQueue.Enqueue(jet);
            if (queueDebugLogs)
                Debug.Log($"[Airfield] '{jet.name}' queued — runway busy with landing " +
                          $"(queue depth {takeoffQueue.Count}).");
            return false;
        }

        if (TryAssignLane(jet, out int lane))
        {
            clearance = BuildClearance(jet, lane);
            BindLane(lane, jet);
            nextClearanceTime = Time.time + takeoffSpacingSeconds;
            if (queueDebugLogs)
                Debug.Log($"[Airfield] Takeoff clearance granted to '{jet.name}' on lane " +
                          $"{(lane == 0 ? "A" : "B")} (immediate).");
            return true;
        }

        // No lane right now — queue the aircraft.
        takeoffQueue.Enqueue(jet);
        if (queueDebugLogs)
            Debug.Log($"[Airfield] Aircraft '{jet.name}' queued for takeoff " +
                      $"(queue depth {takeoffQueue.Count}).");
        return false;
    }

    /// <summary>
    /// Called by the aircraft once it has climbed out and is no longer using
    /// the runway lane. Frees the lane and triggers the next clearance grant.
    /// </summary>
    public void ReleaseTakeoffSlot(AirUnitController jet)
    {
        if (jet == null) return;

        // Detect which lane the jet was on BEFORE we null its slot so the
        // log line correctly names the lane.
        char laneName = '?';
        if (activeJetA == jet)
        {
            activeJetA = null;
            readyJetA  = false;   // any stale "ready" flag belonged to this jet
            laneName   = 'A';
        }
        else if (activeJetB == jet)
        {
            activeJetB = null;
            readyJetB  = false;
            laneName   = 'B';
        }

        if (pendingLaneBJet == jet) pendingLaneBJet = null;

        if (queueDebugLogs && laneName != '?')
            Debug.Log($"[Airfield] Lane {laneName} released by '{jet.name}'.");
    }

    // ------------------------------------------------------------------ //
    // Landing clearance — called by AirUnitController during return
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Aircraft entry point. Called when a returning jet reaches the airfield
    /// approach point. Returns true and fills <paramref name="clearance"/>
    /// (with landing waypoints + taxi-back route to the jet's home slot) when
    /// the runway is clear; otherwise returns false and the aircraft should
    /// enter <see cref="AirUnitController.FlightState.WaitingForLandingClearance"/>
    /// to hold and retry.
    /// </summary>
    public bool RequestLandingClearance(AirUnitController jet, out LandingClearance clearance)
    {
        clearance = null;
        if (jet == null) return false;

        if (runwayBusyDebugLogs)
            Debug.Log($"[Airfield] Landing requested by '{jet.name}'.");

        // Already cleared — return the same bundle so a re-request mid-approach
        // doesn't lose progress.
        if (activeLandingJet == jet)
        {
            clearance = BuildLandingClearance(jet);
            return true;
        }

        // Another jet is on the runway for landing.
        if (activeLandingJet != null)
        {
            if (runwayBusyDebugLogs)
                Debug.Log($"[Airfield] Landing denied — runway busy by '{activeLandingJet.name}'.");
            return false;
        }

        // A takeoff jet is in pre-roll or roll — runway is committed for departure.
        if (landingUsesSharedRunway && IsTakeoffPreRollOrRollActive())
        {
            if (runwayBusyDebugLogs)
                Debug.Log($"[Airfield] Landing denied — runway busy with takeoff.");
            return false;
        }

        activeLandingJet = jet;
        clearance = BuildLandingClearance(jet);
        Debug.Log($"[Airfield] Landing clearance granted to '{jet.name}'.");
        return true;
    }

    /// <summary>
    /// Called by the aircraft once it has touched down and is leaving the
    /// runway for the taxi-back. Frees the shared runway lock so queued
    /// takeoffs and other landings can proceed. The next holding aircraft's
    /// retry timer (or the takeoff queue's tick) picks up the freed runway
    /// automatically — no explicit notify needed.
    /// </summary>
    public void ReleaseLandingRunway(AirUnitController jet)
    {
        if (activeLandingJet != jet)
        {
            // Either already released, or the wrong jet is calling — both safe to ignore.
            if (runwayBusyDebugLogs && activeLandingJet != null && jet != null)
                Debug.Log($"[Airfield] Ignoring runway release from '{jet.name}' — " +
                          $"owner is '{activeLandingJet.name}'.");
            return;
        }
        activeLandingJet = null;
        if (runwayBusyDebugLogs)
            Debug.Log($"[Airfield] Runway released by '{jet.name}' after landing exit. " +
                      $"Next holding/queued aircraft will pick it up.");
    }

    /// <summary>True while a takeoff jet is taxiing onto, aligning at, or rolling on the runway.</summary>
    private bool IsTakeoffPreRollOrRollActive()
    {
        return IsConflictingTakeoffState(activeJetA) || IsConflictingTakeoffState(activeJetB);
    }

    private static bool IsConflictingTakeoffState(AirUnitController jet)
    {
        if (jet == null) return false;
        var s = jet.State;
        return s == AirUnitController.FlightState.TaxiingToRunway
            || s == AirUnitController.FlightState.AligningForTakeoff
            || s == AirUnitController.FlightState.WaitingForBatchTakeoff
            || s == AirUnitController.FlightState.TakeoffRoll;
    }

    private LandingClearance BuildLandingClearance(AirUnitController jet)
    {
        int slotIdx       = FindSlotIndexOf(jet);
        Transform taxiPt  = (slotIdx >= 0 && taxiPoints != null && slotIdx < taxiPoints.Length)
                            ? taxiPoints[slotIdx] : null;
        Transform slot    = (slotIdx >= 0 && slots != null      && slotIdx < slots.Length)
                            ? slots[slotIdx]      : null;

        List<Transform> backRoute = new List<Transform>(3);
        if (landingExitA != null) backRoute.Add(landingExitA);
        if (taxiPt       != null) backRoute.Add(taxiPt);
        if (slot         != null) backRoute.Add(slot);

        return new LandingClearance
        {
            LandingApproach = landingApproachPoint,
            LandingStart    = landingStartA,
            LandingEnd      = landingEndA,
            LandingExit     = landingExitA,
            TaxiBackRoute   = backRoute.ToArray(),
            Lane            = 0,
        };
    }

    // ------------------------------------------------------------------ //
    // Batch synchronization — called by AirUnitController when alignment ends
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Aircraft entry point. Called when the jet finishes
    /// <see cref="AirUnitController.FlightState.AligningForTakeoff"/> and is
    /// holding at the takeoff start. If the jet is part of a synchronized
    /// launch group with a partner also in pre-roll, we hold; otherwise we
    /// call <see cref="AirUnitController.BeginTakeoffRoll"/> immediately.
    /// </summary>
    public void NotifyReadyForTakeoffRoll(AirUnitController jet)
    {
        if (jet == null) return;

        if (jet == activeJetA)
        {
            readyJetA     = true;
            jetAReadyTime = Time.time;
        }
        else if (jet == activeJetB)
        {
            readyJetB     = true;
            jetBReadyTime = Time.time;
        }
        else
        {
            // Unknown jet (shouldn't happen) — just roll, don't deadlock.
            jet.BeginTakeoffRoll();
            return;
        }

        TryReleaseBatch();
    }

    /// <summary>
    /// Inspects which jets are ready / in pre-roll / in matching launch
    /// groups, and releases either a solo aircraft or a synchronized pair.
    /// </summary>
    private void TryReleaseBatch()
    {
        bool aActive    = activeJetA != null;
        bool bActive    = activeJetB != null;
        bool aReady     = readyJetA && aActive;
        bool bReady     = readyJetB && bActive;
        bool aPreRoll   = aActive && IsInPreRoll(activeJetA);
        bool bPreRoll   = bActive && IsInPreRoll(activeJetB);

        if (!aReady && !bReady) return;

        // Sync needed when both lanes hold jets that are still in pre-roll
        // AND they share a non-zero LaunchGroupId (zero = solo command).
        long groupA = aActive ? activeJetA.LaunchGroupId : 0L;
        long groupB = bActive ? activeJetB.LaunchGroupId : 0L;
        bool needSync = synchronizedGroupTakeoff
                     && aPreRoll && bPreRoll
                     && groupA != 0L && groupA == groupB;

        if (needSync)
        {
            if (aReady && bReady)
            {
                Debug.Log("[Airfield] Batch ready — launching 2 aircraft together.");
                ReleaseJetForRoll(activeJetA, 'A');
                // Lane B either rolls immediately or after the configured spacing.
                if (takeoffPairSpacingSeconds > 0f)
                {
                    pendingLaneBJet         = activeJetB;
                    pendingLaneBReleaseTime = Time.time + takeoffPairSpacingSeconds;
                }
                else
                {
                    ReleaseJetForRoll(activeJetB, 'B');
                }
            }
            else if (queueDebugLogs)
            {
                Debug.Log("[Airfield] Waiting for batch partner.");
            }
            return;
        }

        // Solo path — release whichever jet is ready (independent of the other lane).
        if (aReady)
        {
            if (queueDebugLogs) Debug.Log("[Airfield] Solo aircraft — takeoff without waiting.");
            ReleaseJetForRoll(activeJetA, 'A');
        }
        if (bReady)
        {
            if (queueDebugLogs) Debug.Log("[Airfield] Solo aircraft — takeoff without waiting.");
            ReleaseJetForRoll(activeJetB, 'B');
        }
    }

    /// <summary>True when the jet is in any taxi / align / hold pre-roll state.</summary>
    private static bool IsInPreRoll(AirUnitController jet)
    {
        if (jet == null) return false;
        var s = jet.State;
        return s == AirUnitController.FlightState.WaitingForTakeoffClearance
            || s == AirUnitController.FlightState.TaxiingToRunway
            || s == AirUnitController.FlightState.AligningForTakeoff
            || s == AirUnitController.FlightState.WaitingForBatchTakeoff;
    }

    /// <summary>Calls BeginTakeoffRoll on a lane jet and clears its ready flag.</summary>
    private void ReleaseJetForRoll(AirUnitController jet, char lane)
    {
        if (jet == null) return;
        jet.BeginTakeoffRoll();
        if (lane == 'A') readyJetA = false;
        else             readyJetB = false;
    }

    /// <summary>Drains the deferred Lane B release scheduled by <see cref="TryReleaseBatch"/>.</summary>
    private void TickDeferredLaneBRelease()
    {
        if (pendingLaneBJet == null) return;
        if (Time.time < pendingLaneBReleaseTime) return;

        // Only release if the jet is still parked at the hold and on Lane B.
        if (pendingLaneBJet == activeJetB && IsInPreRoll(pendingLaneBJet))
            ReleaseJetForRoll(pendingLaneBJet, 'B');

        pendingLaneBJet = null;
    }

    /// <summary>
    /// Safety net: if a jet has been ready for longer than
    /// <see cref="batchWaitTimeout"/> and the partner still isn't ready, the
    /// queue would deadlock — release the ready jet solo.
    /// </summary>
    private void CheckBatchTimeout()
    {
        if (!synchronizedGroupTakeoff || batchWaitTimeout <= 0f) return;

        if (readyJetA && !readyJetB && activeJetA != null &&
            Time.time - jetAReadyTime > batchWaitTimeout)
        {
            Debug.LogWarning("[Airfield] Batch partner missing/late — launching solo.");
            ReleaseJetForRoll(activeJetA, 'A');
        }

        if (readyJetB && !readyJetA && activeJetB != null &&
            Time.time - jetBReadyTime > batchWaitTimeout)
        {
            Debug.LogWarning("[Airfield] Batch partner missing/late — launching solo.");
            ReleaseJetForRoll(activeJetB, 'B');
        }
    }

    // ------------------------------------------------------------------ //
    // Queue internals
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Pops the next jet off the queue if the spacing timer has elapsed and a
    /// lane is available. Skips Unity-destroyed entries automatically.
    /// </summary>
    private void TryGrantFromQueue()
    {
        if (takeoffQueue.Count == 0) return;
        if (Time.time < nextClearanceTime) return;

        // Block takeoffs while a landing owns the runway.
        if (landingUsesSharedRunway && activeLandingJet != null) return;

        // Skip dead queue entries (jet destroyed while waiting).
        while (takeoffQueue.Count > 0 && takeoffQueue.Peek() == null)
            takeoffQueue.Dequeue();

        if (takeoffQueue.Count == 0) return;

        AirUnitController next = takeoffQueue.Peek();
        if (!TryAssignLane(next, out int lane)) return; // both lanes busy

        takeoffQueue.Dequeue();
        TakeoffClearance c = BuildClearance(next, lane);
        BindLane(lane, next);
        nextClearanceTime = Time.time + takeoffSpacingSeconds;

        if (queueDebugLogs)
            Debug.Log($"[Airfield] Takeoff clearance granted to '{next.name}' on lane " +
                      $"{(lane == 0 ? "A" : "B")} (from queue, depth now {takeoffQueue.Count}).");

        next.GrantTakeoffClearance(c);
    }

    /// <summary>
    /// Picks a lane for <paramref name="jet"/>, preferring the lane that
    /// matches its slot parity (even → A, odd → B) so paired slots taxi out
    /// side-by-side. Falls back to the other lane if the preferred one is
    /// busy. Returns false when both lanes are occupied.
    /// </summary>
    private bool TryAssignLane(AirUnitController jet, out int lane)
    {
        int slotIdx = FindSlotIndexOf(jet);
        int preferred = (slotIdx >= 0 && slotIdx % 2 == 1) ? 1 : 0;

        if (preferred == 0)
        {
            if (activeJetA == null) { lane = 0; return true; }
            if (activeJetB == null) { lane = 1; return true; }
        }
        else
        {
            if (activeJetB == null) { lane = 1; return true; }
            if (activeJetA == null) { lane = 0; return true; }
        }

        lane = -1;
        return false;
    }

    private void BindLane(int lane, AirUnitController jet)
    {
        if (lane == 0) activeJetA = jet;
        else           activeJetB = jet;
    }

    /// <summary>Returns the slot index for <paramref name="jet"/>, or -1 if not parked here.</summary>
    private int FindSlotIndexOf(AirUnitController jet)
    {
        if (jet == null) return -1;
        Transform jetHome = jet.HomeSlot;
        if (jetHome == null) return -1;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] == jetHome) return i;
        return -1;
    }

    /// <summary>
    /// Builds the full taxi route for a jet on the given lane:
    ///   per-slot pull-out → lane corridor waypoints → runway queue → takeoff start.
    /// Skips any null markers so a partially-configured Airfield still
    /// produces a (shorter) usable route.
    /// </summary>
    private TakeoffClearance BuildClearance(AirUnitController jet, int lane)
    {
        int slotIdx = FindSlotIndexOf(jet);

        Transform[]  laneCorridor = lane == 0 ? laneATaxiPoints   : laneBTaxiPoints;
        Transform    queue        = lane == 0 ? runwayQueuePointA : runwayQueuePointB;
        Transform    start        = lane == 0 ? takeoffStartA     : takeoffStartB;
        Transform    end          = lane == 0 ? takeoffEndA       : takeoffEndB;

        List<Transform> route = new List<Transform>(8);

        // 1. Personal pull-out from the parking slot.
        if (slotIdx >= 0 && taxiPoints != null && slotIdx < taxiPoints.Length && taxiPoints[slotIdx] != null)
            route.Add(taxiPoints[slotIdx]);

        // 2. Shared lane corridor — keeps Lane A jets clear of Lane B jets.
        if (laneCorridor != null)
            foreach (Transform wp in laneCorridor)
                if (wp != null) route.Add(wp);

        // 3. Runway hold-short point.
        if (queue != null) route.Add(queue);

        // 4. Final waypoint — takeoff start. The aircraft transitions to
        //    AligningForTakeoff once it arrives here.
        if (start != null) route.Add(start);

        return new TakeoffClearance
        {
            TaxiRoute    = route.ToArray(),
            TakeoffStart = start,
            TakeoffEnd   = end,
            Lane         = lane,
        };
    }

    // ------------------------------------------------------------------ //
    // Slot bookkeeping
    // ------------------------------------------------------------------ //

    private int FindFreeSlotIndex()
    {
        for (int i = 0; i < parked.Length; i++)
        {
            if (slots[i] == null) continue;
            if (parked[i] == null) return i;
        }
        return -1;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        // Slot pads
        if (slots != null)
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;
                Gizmos.DrawWireCube(slots[i].position, new Vector3(2f, 0.1f, 2f));
                UnityEditor.Handles.Label(slots[i].position + Vector3.up * 0.5f, $"Slot {i}");
            }
        }

        // Taxi points
        if (taxiPoints != null)
        {
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.6f);
            for (int i = 0; i < taxiPoints.Length; i++)
            {
                if (taxiPoints[i] == null) continue;
                Gizmos.DrawWireSphere(taxiPoints[i].position, 0.4f);
                UnityEditor.Handles.Label(taxiPoints[i].position + Vector3.up * 0.4f, $"Taxi {i}");
            }
        }

        // Lane endpoints
        DrawRunwayPoint(runwayQueuePointA, "Queue A", new Color(1f, 0.4f, 0.1f));
        DrawRunwayPoint(takeoffStartA,     "Start A", new Color(0.2f, 1f, 0.2f));
        DrawRunwayPoint(takeoffEndA,       "End A",   new Color(1f, 0.2f, 0.2f));
        DrawRunwayPoint(runwayQueuePointB, "Queue B", new Color(1f, 0.4f, 0.1f));
        DrawRunwayPoint(takeoffStartB,     "Start B", new Color(0.2f, 1f, 0.2f));
        DrawRunwayPoint(takeoffEndB,       "End B",   new Color(1f, 0.2f, 0.2f));
        DrawRunwayPoint(landingApproachPoint, "Landing", new Color(0.5f, 0.6f, 1f));

        // Lane corridor waypoints — separate colour per lane so the two
        // paths are obvious in the Scene view.
        if (laneATaxiPoints != null)
            for (int i = 0; i < laneATaxiPoints.Length; i++)
                DrawRunwayPoint(laneATaxiPoints[i], $"Lane A · {i}", new Color(0.3f, 0.8f, 1f));
        if (laneBTaxiPoints != null)
            for (int i = 0; i < laneBTaxiPoints.Length; i++)
                DrawRunwayPoint(laneBTaxiPoints[i], $"Lane B · {i}", new Color(1f, 0.7f, 0.3f));
    }

    private void DrawRunwayPoint(Transform t, string label, Color color)
    {
        if (t == null) return;
        Gizmos.color = color;
        Gizmos.DrawWireSphere(t.position, 0.6f);
        UnityEditor.Handles.Label(t.position + Vector3.up * 0.6f, label);
    }
#endif
}
