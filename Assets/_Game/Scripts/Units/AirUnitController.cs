using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Aircraft "brain" — runs the sortie state machine and dispatches movement to
/// the active <see cref="FlightProfile"/>. Inspired by the SAGE engine's
/// AIUpdate / LocomotorSet split:
///
///   • This file = brain (state transitions, target selection, mission logic).
///   • <see cref="FlightProfile"/> = body (speed / turn rate / vertical rate
///     per phase). Each state's Update reads its movement values from the
///     profile, not from scattered top-level fields.
///   • <see cref="AircraftWeapon"/> = ammo / fire cooldown / cone + range
///     validation / projectile spawning.
///   • <see cref="Airfield"/> = slot ownership, runway traffic control,
///     batch-takeoff synchronization, landing queue.
///
///   Parked → WaitingForTakeoffClearance → TaxiingToRunway → AligningForTakeoff
///         → WaitingForBatchTakeoff → TakeoffRoll → Climbing
///                                                       ↓
///                                       FlyingToTarget ←──── (mission dispatch)
///                                            ↓                       ↓
///                                  RepositioningForAttack       FlyingToPoint
///                                            ↓                       ↓
///                                       AttackRun           PatrollingPoint
///                                            ↓                       ↓
///                                       AttackEgress → WideReturnTurn ←
///                                                            ↓
///                                                       Returning
///                                                            ↓
///                                       WaitingForLandingClearance
///                                                            ↓
///                                                       LandingApproach
///                                                            ↓
///                                                       FinalLanding
///                                                            ↓
///                                                       TaxiingToSlot → Parked
///
/// Attack mode is a real fly-by — the jet locks its forward heading via
/// <see cref="attackRunProfile"/> (turnRateDegrees ≈ 0) and asks
/// <see cref="AircraftWeapon.TryFire"/> each tick. Fire / cooldown / cone
/// checks all live on the weapon; this file only decides whether to keep
/// running, egress, or reposition based on the returned <see cref="AircraftWeapon.FireResult"/>.
///
/// The aircraft does NOT use NavMeshAgent / UnitMovement — it moves by direct
/// transform manipulation. Ground states (TaxiingToRunway / AligningForTakeoff /
/// TakeoffRoll / TaxiingToSlot) hold the aircraft at the home slot's world Y
/// (plus the optional <see cref="groundHeightOffset"/> lift, default 0);
/// Climbing onward holds it at <see cref="flightAltitude"/>.
///
/// Setup (done automatically by Tools → RTS → Air System → Create Strike Jet Prefab):
///   1. Attach to the aircraft root GameObject (with Health, SelectableAircraft,
///      UnitCategory = Aircraft, BoxCollider, and an AircraftWeapon).
///   2. Assign a child Transform "FirePoint" near the missile pods.
///   3. Tune flight profiles + altitude + approach validation in the Inspector.
///   4. The Airfield calls AssignHome(...) at spawn time — do NOT call it yourself.
/// </summary>
[RequireComponent(typeof(AircraftWeapon))]
public class AirUnitController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    public enum FlightState
    {
        Parked,
        WaitingForTakeoffClearance,
        TaxiingToRunway,
        AligningForTakeoff,
        WaitingForBatchTakeoff,
        TakeoffRoll,
        Climbing,
        FlyingToTarget,
        RepositioningForAttack,
        AttackRun,
        AttackEgress,
        WideReturnTurn,
        FlyingToPoint,
        PatrollingPoint,
        Returning,
        WaitingForLandingClearance,
        LandingApproach,
        FinalLanding,
        TaxiingToSlot
    }

    // ------------------------------------------------------------------ //
    // Inspector — Flight Profiles
    // ------------------------------------------------------------------ //

    [Header("Flight Profiles")]
    [Tooltip("Slow ground roll between parking slot and runway start. " +
             "Also used for taxi-back after landing. Speed ≈ 4, turn ≈ 180.")]
    public FlightProfile taxiProfile = new FlightProfile
    {
        name = "Taxi", speed = 4f, turnRateDegrees = 180f,
        verticalSpeed = 0f, arrivalThreshold = 0.3f
    };

    [Tooltip("Takeoff roll along the runway. Forward speed ≈ 10; vertical " +
             "(used by Climbing as climb-out rate) ≈ 8.")]
    public FlightProfile takeoffProfile = new FlightProfile
    {
        name = "Takeoff", speed = 10f, turnRateDegrees = 180f,
        verticalSpeed = 8f, arrivalThreshold = 2f
    };

    [Tooltip("Standard level cruise — used by FlyingToTarget, FlyingToPoint, " +
             "Returning, Repositioning, WideReturnTurn. Speed 14, turn 85 deg/s.")]
    public FlightProfile cruiseProfile = new FlightProfile
    {
        name = "Cruise", speed = 14f, turnRateDegrees = 85f,
        verticalSpeed = 0f, arrivalThreshold = 0.5f
    };

    [Tooltip("Locked-heading attack run. Turn rate near zero so the jet flies " +
             "straight through the release window. Speed matches cruise.")]
    public FlightProfile attackRunProfile = new FlightProfile
    {
        name = "AttackRun", speed = 14f, turnRateDegrees = 0f,
        verticalSpeed = 0f, arrivalThreshold = 0f
    };

    [Tooltip("Final landing roll + descent. Forward speed 7 (slower than cruise), " +
             "verticalSpeed 5 = descent rate. Wider arrival threshold 0.4.")]
    public FlightProfile landingProfile = new FlightProfile
    {
        name = "Landing", speed = 7f, turnRateDegrees = 180f,
        verticalSpeed = 5f, arrivalThreshold = 0.4f
    };

    [Tooltip("Circling motion — patrol pattern and landing holding pattern. " +
             "Fast tangent rotation (180 deg/s) keeps the circle smooth.")]
    public FlightProfile holdingProfile = new FlightProfile
    {
        name = "Holding", speed = 14f, turnRateDegrees = 180f,
        verticalSpeed = 0f, arrivalThreshold = 0.4f
    };

    // ------------------------------------------------------------------ //
    // Inspector — Flight (non-profile state)
    // ------------------------------------------------------------------ //

    [Header("Flight")]
    [Tooltip("Cruise altitude in world units while flying.")]
    public float flightAltitude = 12f;

    [Tooltip("Extra Y lift (world units) added on top of the home slot's Y " +
             "while on the ground. 0 for the standard Y=0 baseline.")]
    public float groundHeightOffset = 0f;

    [Header("Takeoff Alignment")]
    [Tooltip("Yaw rate (degrees/sec) while AligningForTakeoff. The aircraft " +
             "holds its position at TakeoffStart and rotates in place.")]
    public float takeoffAlignmentTurnSpeed = 90f;

    [Tooltip("Angle (degrees) below which alignment is considered complete and " +
             "the aircraft transitions to WaitingForBatchTakeoff.")]
    public float takeoffAlignmentAngleThreshold = 3f;

    [Tooltip("XZ distance from the TakeoffEnd point at which the lane is " +
             "released back to the Airfield and the aircraft transitions to Climbing.")]
    public float takeoffEndReleaseDistance = 2f;

    [Header("Landing — Approach & Hold")]
    [Tooltip("XZ distance below which Returning hands off to the landing-clearance " +
             "request flow.")]
    public float landingApproachDistance = 25f;

    [Tooltip("Radius of the holding-pattern circle around the approach point.")]
    public float landingHoldingRadius = 14f;

    [Tooltip("Seconds between landing-clearance retries while holding.")]
    public float landingClearanceCheckInterval = 0.5f;

    [Header("Landing — Fail-safe Timeouts")]
    [Tooltip("Max seconds an aircraft may sit in WaitingForLandingClearance before " +
             "it asks the Airfield to force-release a stuck runway owner. Prevents " +
             "infinite circling when a previous landing aircraft never released.")]
    public float landingClearanceTimeout = 12f;

    [Tooltip("Max seconds an aircraft may spend in LandingApproach before snapping " +
             "to FinalLanding from its current position. Guards against overshoot " +
             "loops where cruise turn radius exceeds the LandingStart arrival window.")]
    public float landingApproachTimeout = 8f;

    [Tooltip("Max seconds an aircraft may spend in FinalLanding before force-touchdown " +
             "at the current XZ position. Guards against descent stalls.")]
    public float finalLandingTimeout = 15f;

    [Tooltip("Max seconds an aircraft may spend in TaxiingToSlot before snapping " +
             "to its home slot. Guards against waypoint-stuck aircraft.")]
    public float taxiToSlotTimeout = 15f;

    // ------------------------------------------------------------------ //
    // Inspector — Attack (non-weapon state)
    // ------------------------------------------------------------------ //

    [Header("Attack")]
    [Tooltip("Missile-release range. When XZ distance to target drops below this, " +
             "the jet stops steering, locks forward direction, and starts the run. " +
             "Actual missile gating happens on AircraftWeapon.")]
    public float attackRange = 18f;

    [Tooltip("Forward distance (world units) the jet keeps flying after the last " +
             "missile before any turn is allowed.")]
    public float attackEgressDistance = 15f;

    [Tooltip("If true, AttackTarget calls during an active attack run swap targets. " +
             "Default false: the current run completes before retargeting.")]
    public bool attackRunCanRetarget = false;

    [Tooltip("If false, AttackTarget calls with 0 ammo are rejected. " +
             "If true, the jet still takes off and dives without releases.")]
    public bool canAttackWithNoAmmo = false;

    [Header("Return Arc")]
    [Tooltip("Angle (degrees) between current heading and direction to home below " +
             "which WideReturnTurn hands off to Returning. Smaller = tighter alignment.")]
    public float returnAlignmentAngle = 8f;

    [Header("Attack Approach Validation")]
    [Tooltip("Minimum XZ distance the jet must be from a target to consider an attack " +
             "run. Inside this distance the approach is too steep — jet repositions.")]
    public float minAttackApproachDistance = 8f;

    [Tooltip("Minimum forward-vs-target dot product for a valid attack approach. " +
             "0.15 ≈ target within ±81° of forward.")]
    public float minAttackForwardDot = 0.15f;

    [Tooltip("Maximum angle (degrees) between forward and target direction at which " +
             "the approach is considered valid.")]
    public float minAttackAngle = 75f;

    [Tooltip("How far ahead of the aircraft a repositioning waypoint is placed.")]
    public float approachRepositionDistance = 14f;

    [Tooltip("Radius of the reattack orbit around the target during repositioning.")]
    public float reattackOrbitRadius = 14f;

    [Tooltip("Safety timeout (seconds) for RepositioningForAttack.")]
    public float maxRepositionTime = 5f;

    [Tooltip("Maximum reposition attempts before the jet commits to a relaxed attack " +
             "(any forward direction, within max range). Prevents endless loops.")]
    public int maxRepositionAttempts = 2;

    // ------------------------------------------------------------------ //
    // Inspector — Patrol
    // ------------------------------------------------------------------ //

    [Header("Patrol")]
    [Tooltip("Radius of the patrol circle, in world units.")]
    public float patrolCircleRadius = 10f;

    [Tooltip("Full laps before returning home.")]
    public int patrolCircleCount = 2;

    [Tooltip("Angular speed around the patrol circle (degrees/sec).")]
    public float patrolAngularSpeed = 60f;

    // ------------------------------------------------------------------ //
    // Inspector — Parked Repair
    // ------------------------------------------------------------------ //

    [Header("Parked Repair")]
    [Tooltip("If true, the aircraft slowly repairs its Health while in the Parked " +
             "state. Reload is independent and ticks regardless of this setting.")]
    public bool repairWhileParked = true;

    [Tooltip("HP restored per second while parked. Only ticks when Health is below " +
             "max AND the parent Health is on team Player.")]
    public float parkedRepairRate = 10f;

    // ------------------------------------------------------------------ //
    // Inspector — Visual
    // ------------------------------------------------------------------ //

    [Header("Visual")]
    [Tooltip("Launch origin for spawned StrikeMissile projectiles. " +
             "Falls back to transform.position if null.")]
    public Transform firePoint;

    // ------------------------------------------------------------------ //
    // Runtime state — public
    // ------------------------------------------------------------------ //

    public FlightState State { get; private set; } = FlightState.Parked;
    public Airfield    HomeAirfield { get; private set; }
    public Transform   HomeSlot     => homeSlot;
    public long        LaunchGroupId { get; private set; }

    /// <summary>Forwarding accessor — actual ammo lives on AircraftWeapon. Kept for callers (HUD, debug).</summary>
    public int CurrentAmmo => weapon != null ? weapon.CurrentAmmo : 0;

    /// <summary>Forwarding accessor — actual rack size lives on AircraftWeapon.</summary>
    public int maxAmmo => weapon != null ? weapon.maxAmmo : 0;

    // ------------------------------------------------------------------ //
    // Runtime state — private
    // ------------------------------------------------------------------ //

    private AircraftWeapon weapon;
    private Health         health;
    private Transform      homeSlot;
    private Health         target;

    // Parked-repair tracking: one log line when repair starts (damaged → first
    // tick), one when it completes (reaches max). repairingThisSession is true
    // while the aircraft is mid-repair so we don't re-log every parked frame.
    private bool repairingThisSession;

    // Attack-run state. missilesFiredThisRun caps releases per pass at maxAmmo
    // so a fresh rack doesn't dump an unlimited burst; attackEgressStart is the
    // anchor for measuring distance during AttackEgress.
    private int     missilesFiredThisRun;
    private Vector3 attackEgressStart;

    // Repositioning safety: timeout + attempt cap. After maxRepositionAttempts
    // a bad approach forces a relaxed attack instead of yet another reposition.
    private float repositionStartTime;
    private int   repositionAttempts;

    // Landing-flow state.
    private Airfield.LandingClearance landingClearance;
    private Transform[] taxiBackRoute;
    private int         taxiBackIndex;
    private Vector3     finalLandingStartXZ;
    private float       landingClearanceRetryTimer;
    private float       holdingPatternAngle;

    // Takeoff-flow state.
    private Airfield.TakeoffClearance clearance;
    private Transform[] taxiRoute;
    private int         taxiRouteIndex;
    private bool        laneReleased;

    // Patrol state.
    private bool    hasPatrolMission;
    private Vector3 patrolCenter;
    private float   patrolAngleSwept;
    private float   patrolCurrentAngle;
    private int     lastReportedCircle;

    // Profile-switch logging — single "Using profile: X" line per actual switch.
    private FlightProfile lastLoggedProfile;

    // State-entry timestamp — used by landing-state timeouts. Reset whenever
    // the FSM transitions to a new state. The Update() switch checks this
    // each frame; no need for explicit OnEnter/OnExit hooks.
    private FlightState lastObservedState;
    private float       stateEnteredTime;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        weapon = GetComponent<AircraftWeapon>();
        // [RequireComponent] enforces this in the editor for newly-built prefabs,
        // but a pre-refactor StrikeJetPrefab.prefab on disk may still lack one.
        // Auto-add a default so the aircraft is at least operational; warn the
        // user to persist the values via the repair tool.
        if (weapon == null)
        {
            Debug.LogWarning($"[Aircraft:{name}] AircraftWeapon missing — auto-adding with defaults. " +
                             "Run Tools → RTS → Air System → Repair Strike Jet Weapon to persist tuned values.");
            weapon = gameObject.AddComponent<AircraftWeapon>();
        }

        // Cached for the parked-repair tick. Null is acceptable (e.g. test
        // dummies without a Health component) — repair just no-ops in that case.
        health = GetComponent<Health>();
    }

    // ------------------------------------------------------------------ //
    // Wiring — called by Airfield at spawn
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Bind this aircraft to its parent Airfield and parking-slot Transform.
    /// Called by Airfield.ProduceStrikeJet.
    /// </summary>
    public void AssignHome(Airfield airfield, Transform slot)
    {
        HomeAirfield = airfield;
        homeSlot     = slot;

        if (slot != null)
        {
            transform.position = slot.position;
            transform.rotation = slot.rotation;
        }

        if (weapon != null) weapon.ResetAmmo();
        repositionAttempts = 0;
        State              = FlightState.Parked;
    }

    // ------------------------------------------------------------------ //
    // Public API — called by UnitSelector when the player right-clicks an enemy
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Order the aircraft to attack <paramref name="enemy"/>. Parked → takeoff;
    /// airborne → retarget. <paramref name="groupId"/> identifies a synchronized
    /// launch group (0 = solo).
    /// </summary>
    public void AttackTarget(Health enemy, long groupId = 0L)
    {
        if (enemy == null || enemy.team == Health.Team.Player)
        {
            Debug.LogWarning($"[Aircraft:{name}] Invalid attack target — ignoring.");
            return;
        }

        if ((State == FlightState.AttackRun || State == FlightState.AttackEgress) &&
            !attackRunCanRetarget)
        {
            Debug.Log($"[Aircraft:{name}] Cannot retarget during attack run.");
            return;
        }

        if (!HasAmmoForAttack() && !canAttackWithNoAmmo)
        {
            Debug.LogWarning($"[Aircraft:{name}] Cannot attack — no missiles loaded.");
            return;
        }

        // New target → fresh reposition budget.
        if (target != enemy) repositionAttempts = 0;

        target           = enemy;
        hasPatrolMission = false;
        LaunchGroupId    = groupId;

        switch (State)
        {
            case FlightState.Parked:
                RequestTakeoffClearanceFromAirfield(forAttack: true, enemyName: enemy.name);
                break;

            case FlightState.WaitingForTakeoffClearance:
            case FlightState.TaxiingToRunway:
            case FlightState.TakeoffRoll:
            case FlightState.Climbing:
                Debug.Log($"[Aircraft:{name}] Mid-departure — will attack '{enemy.name}' after climb-out.");
                break;

            case FlightState.WaitingForLandingClearance:
            case FlightState.LandingApproach:
            case FlightState.FinalLanding:
            case FlightState.TaxiingToSlot:
                Debug.Log($"[Aircraft:{name}] Mid-landing — ignoring attack order until parked.");
                target = null;
                return;

            default:
                State = FlightState.FlyingToTarget;
                Debug.Log($"[Aircraft:{name}] Smooth turn toward new command. Retargeting to '{enemy.name}'.");
                break;
        }
    }

    /// <summary>
    /// Order the aircraft to fly to <paramref name="worldPos"/>, circle it
    /// <see cref="patrolCircleCount"/> times, then return to its home slot.
    /// </summary>
    public void FlyToPoint(Vector3 worldPos, long groupId = 0L)
    {
        if (State == FlightState.AttackRun || State == FlightState.AttackEgress)
        {
            Debug.Log($"[Aircraft:{name}] Mid-attack — ignoring patrol command.");
            return;
        }

        target              = null;
        hasPatrolMission    = true;
        patrolCenter        = worldPos;
        patrolAngleSwept    = 0f;
        patrolCurrentAngle  = 0f;
        lastReportedCircle  = 0;
        LaunchGroupId       = groupId;

        Debug.Log($"[Aircraft:{name}] Patrol mission assigned at {worldPos:F1}");

        switch (State)
        {
            case FlightState.Parked:
                RequestTakeoffClearanceFromAirfield(forAttack: false);
                break;

            case FlightState.WaitingForTakeoffClearance:
            case FlightState.TaxiingToRunway:
            case FlightState.TakeoffRoll:
            case FlightState.Climbing:
                Debug.Log($"[Aircraft:{name}] Mid-departure — will patrol after climb-out.");
                break;

            case FlightState.WaitingForLandingClearance:
            case FlightState.LandingApproach:
            case FlightState.FinalLanding:
            case FlightState.TaxiingToSlot:
                Debug.Log($"[Aircraft:{name}] Mid-landing — ignoring patrol order until parked.");
                hasPatrolMission = false;
                return;

            default:
                State = FlightState.FlyingToPoint;
                Debug.Log($"[Aircraft:{name}] Diverting to new patrol point.");
                break;
        }
    }

    private bool HasAmmoForAttack() => weapon != null && weapon.HasAmmo;

    // ------------------------------------------------------------------ //
    // Public — called by Airfield when a runway lane frees up
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Airfield calls this when it has chosen a lane for this aircraft from
    /// the takeoff queue. The aircraft starts taxiing immediately.
    /// </summary>
    public void GrantTakeoffClearance(Airfield.TakeoffClearance c)
    {
        clearance      = c;
        taxiRoute      = BuildTaxiRoute(c);
        taxiRouteIndex = 0;
        laneReleased   = false;
        State          = FlightState.TaxiingToRunway;
        Debug.Log($"[Aircraft:{name}] Taxiing via Lane {(c.Lane == 0 ? "A" : "B")} (cleared from queue).");
    }

    private void RequestTakeoffClearanceFromAirfield(bool forAttack, string enemyName = null)
    {
        if (HomeAirfield == null)
        {
            Debug.LogWarning($"[Aircraft:{name}] No home airfield — cannot request takeoff clearance.");
            return;
        }

        if (HomeAirfield.RequestTakeoffClearance(this, out Airfield.TakeoffClearance c))
        {
            clearance      = c;
            taxiRoute      = BuildTaxiRoute(c);
            taxiRouteIndex = 0;
            laneReleased   = false;
            State          = FlightState.TaxiingToRunway;
            string mission = forAttack ? $"attack '{enemyName}'" : "patrol";
            Debug.Log($"[Aircraft:{name}] Taxiing via Lane {(c.Lane == 0 ? "A" : "B")} for {mission}.");
        }
        else
        {
            State = FlightState.WaitingForTakeoffClearance;
        }
    }

    /// <summary>Filters the clearance's TaxiRoute, dropping nulls.</summary>
    private Transform[] BuildTaxiRoute(Airfield.TakeoffClearance c)
    {
        if (c == null || c.TaxiRoute == null) return new Transform[0];

        List<Transform> wps = new List<Transform>(c.TaxiRoute.Length);
        foreach (Transform wp in c.TaxiRoute)
            if (wp != null) wps.Add(wp);
        return wps.ToArray();
    }

    // ------------------------------------------------------------------ //
    // Unity tick
    // ------------------------------------------------------------------ //

    private void Update()
    {
        // Stamp the moment any state change happened — landing-state timeouts
        // read elapsed time off this. Cheap: only one Time.time write per
        // actual transition. Initial value is recorded on the very first tick.
        if (State != lastObservedState)
        {
            // Leaving Parked → end the repair session so the next park logs
            // "Repairing while parked." fresh instead of staying silent.
            if (lastObservedState == FlightState.Parked) repairingThisSession = false;

            lastObservedState = State;
            stateEnteredTime  = Time.time;
        }

        switch (State)
        {
            case FlightState.Parked:                     UpdateParkedReload();           break;
            case FlightState.WaitingForTakeoffClearance:                                 break;
            case FlightState.TaxiingToRunway:            UpdateTaxiingToRunway();        break;
            case FlightState.AligningForTakeoff:         UpdateAligningForTakeoff();     break;
            case FlightState.WaitingForBatchTakeoff:     UpdateWaitingForBatchTakeoff(); break;
            case FlightState.TakeoffRoll:                UpdateTakeoffRoll();            break;
            case FlightState.Climbing:                   UpdateClimbing();               break;
            case FlightState.FlyingToTarget:             UpdateFlyingToTarget();         break;
            case FlightState.RepositioningForAttack:     UpdateRepositioningForAttack(); break;
            case FlightState.AttackRun:                  UpdateAttackRun();              break;
            case FlightState.AttackEgress:               UpdateAttackEgress();           break;
            case FlightState.WideReturnTurn:             UpdateWideReturnTurn();         break;
            case FlightState.FlyingToPoint:              UpdateFlyingToPoint();          break;
            case FlightState.PatrollingPoint:            UpdatePatrollingPoint();        break;
            case FlightState.Returning:                  UpdateReturning();              break;
            case FlightState.WaitingForLandingClearance: UpdateWaitingForLandingClearance(); break;
            case FlightState.LandingApproach:            UpdateLandingApproach();        break;
            case FlightState.FinalLanding:               UpdateFinalLanding();           break;
            case FlightState.TaxiingToSlot:              UpdateTaxiingToSlot();          break;
        }
    }

    // ------------------------------------------------------------------ //
    // Parked — reload + repair
    // ------------------------------------------------------------------ //

    private void UpdateParkedReload()
    {
        if (weapon != null) weapon.TickReload(Time.deltaTime);
        TickParkedRepair();
    }

    /// <summary>
    /// Slowly restores Health while the aircraft is Parked. No-op when:
    ///   • repairWhileParked is disabled in the Inspector,
    ///   • no Health component is attached (e.g. test dummies),
    ///   • Health.team is not Player (enemy aircraft never repair here),
    ///   • Health is already at max.
    ///
    /// Logs exactly twice per damaged session — once when the first heal tick
    /// fires ("Repairing while parked.") and once when max is reached
    /// ("Fully repaired."). The repairingThisSession flag is also cleared on
    /// every non-Parked tick (see <see cref="ClearRepairFlagOnExit"/>) so a
    /// re-parked aircraft logs the start message fresh.
    /// </summary>
    private void TickParkedRepair()
    {
        if (!repairWhileParked || health == null) return;
        if (health.team != Health.Team.Player) return;
        if (health.CurrentHealth >= health.maxHealth)
        {
            if (repairingThisSession)
            {
                Debug.Log($"[Aircraft:{name}] Fully repaired.");
                repairingThisSession = false;
            }
            return;
        }

        if (!repairingThisSession)
        {
            Debug.Log($"[Aircraft:{name}] Repairing while parked.");
            repairingThisSession = true;
        }

        health.Heal(parkedRepairRate * Time.deltaTime);
    }

    // ------------------------------------------------------------------ //
    // Taxi → Align → Roll → Climb
    // ------------------------------------------------------------------ //

    private void UpdateTaxiingToRunway()
    {
        UseProfile(taxiProfile);
        float groundY = GetGroundY();

        if (taxiRoute == null || taxiRouteIndex >= taxiRoute.Length)
        {
            State = FlightState.AligningForTakeoff;
            return;
        }

        Transform wp = taxiRoute[taxiRouteIndex];
        if (wp == null) { taxiRouteIndex++; return; }

        Vector3 wpPos = wp.position; wpPos.y = groundY;
        Vector3 dir   = wpPos - transform.position;
        Vector2 distXZ = new Vector2(dir.x, dir.z);

        if (distXZ.magnitude <= taxiProfile.arrivalThreshold)
        {
            taxiRouteIndex++;
            if (taxiRouteIndex >= taxiRoute.Length)
            {
                State = FlightState.AligningForTakeoff;
                Debug.Log($"[Aircraft:{name}] Aligning for takeoff");
            }
            return;
        }

        Vector3 step = dir.normalized * taxiProfile.speed * Time.deltaTime;
        if (step.magnitude > dir.magnitude) step = dir;
        transform.position += step;

        Vector3 pos = transform.position; pos.y = groundY; transform.position = pos;
        FaceVelocity(step, taxiProfile.turnRateDegrees);
    }

    private void UpdateAligningForTakeoff()
    {
        if (clearance == null || clearance.TakeoffStart == null || clearance.TakeoffEnd == null)
        {
            State = FlightState.TakeoffRoll;
            return;
        }

        Vector3 holdPos = clearance.TakeoffStart.position;
        holdPos.y = GetGroundY();
        transform.position = holdPos;

        Vector3 runwayDir = clearance.TakeoffEnd.position - clearance.TakeoffStart.position;
        runwayDir.y = 0f;
        if (runwayDir.sqrMagnitude < 0.0001f)
        {
            State = FlightState.TakeoffRoll;
            return;
        }

        Quaternion want = Quaternion.LookRotation(runwayDir);
        float angle = Quaternion.Angle(transform.rotation, want);

        if (angle <= takeoffAlignmentAngleThreshold)
        {
            transform.rotation = want;
            State = FlightState.WaitingForBatchTakeoff;
            Debug.Log($"[Aircraft:{name}] Ready at takeoff hold point.");

            if (HomeAirfield != null) HomeAirfield.NotifyReadyForTakeoffRoll(this);
            else                      BeginTakeoffRoll();
            return;
        }

        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, want, takeoffAlignmentTurnSpeed * Time.deltaTime);
    }

    private void UpdateWaitingForBatchTakeoff()
    {
        if (clearance == null || clearance.TakeoffStart == null) return;

        Vector3 holdPos = clearance.TakeoffStart.position;
        holdPos.y = GetGroundY();
        transform.position = holdPos;
    }

    /// <summary>Released into the takeoff roll by the Airfield (sync or solo).</summary>
    public void BeginTakeoffRoll()
    {
        if (State != FlightState.WaitingForBatchTakeoff &&
            State != FlightState.AligningForTakeoff)
            return;
        State = FlightState.TakeoffRoll;
        Debug.Log($"[Aircraft:{name}] Takeoff roll started");
    }

    private void UpdateTakeoffRoll()
    {
        UseProfile(takeoffProfile);

        if (clearance == null || clearance.TakeoffEnd == null)
        {
            ReleaseLane();
            State = FlightState.Climbing;
            return;
        }

        float groundY = GetGroundY();
        Vector3 endPos = clearance.TakeoffEnd.position; endPos.y = groundY;
        Vector3 dir    = endPos - transform.position;
        Vector2 distXZ = new Vector2(dir.x, dir.z);

        if (distXZ.magnitude <= takeoffEndReleaseDistance)
        {
            ReleaseLane();
            State = FlightState.Climbing;
            return;
        }

        Vector3 step = dir.normalized * takeoffProfile.speed * Time.deltaTime;
        transform.position += step;

        Vector3 pos = transform.position; pos.y = groundY; transform.position = pos;
        FaceVelocity(step, takeoffProfile.turnRateDegrees);
    }

    private void UpdateClimbing()
    {
        // Climbing is the transition between takeoff (ground) and cruise (in-air):
        // forward speed at cruise rate, vertical rate read from TakeoffProfile so
        // climb-out feel is tunable separately from cruise verticalSpeed.
        UseProfile(cruiseProfile);

        Vector3 step = transform.forward * cruiseProfile.speed * Time.deltaTime;
        transform.position += step;

        Vector3 pos = transform.position;
        pos.y = Mathf.MoveTowards(pos.y, flightAltitude,
                                  takeoffProfile.verticalSpeed * Time.deltaTime);
        transform.position = pos;

        if (pos.y >= flightAltitude - 0.05f)
        {
            if (target != null)            State = FlightState.FlyingToTarget;
            else if (hasPatrolMission)     State = FlightState.FlyingToPoint;
            else                           EnterWideReturnTurn();

            Debug.Log($"[Aircraft:{name}] Reached cruise altitude.");
        }
    }

    private void ReleaseLane()
    {
        if (laneReleased) return;
        laneReleased = true;
        if (HomeAirfield != null) HomeAirfield.ReleaseTakeoffSlot(this);
    }

    private float GetGroundY()
    {
        float baseY = (homeSlot != null) ? homeSlot.position.y : 0f;
        return baseY + groundHeightOffset;
    }

    // ------------------------------------------------------------------ //
    // Cruise → Attack
    // ------------------------------------------------------------------ //

    private void UpdateFlyingToTarget()
    {
        UseProfile(cruiseProfile);

        if (target == null) { EnterWideReturnTurn(); return; }

        ApproachQuality quality = EvaluateApproach(out float distXZ, out float forwardDot);

        switch (quality)
        {
            case ApproachQuality.Good:
                BeginAttackRun();
                return;

            case ApproachQuality.TooClose:
            case ApproachQuality.Behind:
            case ApproachQuality.BadAngle:
                EnterRepositioning(quality, forwardDot, distXZ);
                return;
        }

        // TooFar / NoTarget — keep cruising toward the target.
        StepForwardAtAltitude(cruiseProfile);

        Vector3 wantedXZ = new Vector3(
            target.transform.position.x, flightAltitude, target.transform.position.z);
        SmoothSteerInAir(wantedXZ, cruiseProfile.turnRateDegrees);
    }

    private void UpdateRepositioningForAttack()
    {
        UseProfile(cruiseProfile);

        if (target == null) { EnterWideReturnTurn(); return; }

        ApproachQuality quality = EvaluateApproach(out float distXZ, out float forwardDot);

        if (quality == ApproachQuality.Good) { BeginAttackRun(); return; }

        // Pulled out of effective range — cruise back toward the target normally.
        if (quality == ApproachQuality.TooFar)
        {
            State = FlightState.FlyingToTarget;
            return;
        }

        // Safety timeout — don't orbit forever.
        if (Time.time - repositionStartTime > maxRepositionTime)
        {
            if (forwardDot >= minAttackForwardDot && distXZ >= minAttackApproachDistance)
            {
                Debug.LogWarning($"[Aircraft:{name}] Reposition timeout — attempting safe attack.");
                BeginAttackRun();
            }
            else
            {
                Debug.LogWarning($"[Aircraft:{name}] Reposition timeout — returning.");
                target = null;
                EnterWideReturnTurn();
            }
            return;
        }

        StepForwardAtAltitude(cruiseProfile);

        Vector3 wantedXZ = new Vector3(
            target.transform.position.x, flightAltitude, target.transform.position.z);
        SmoothSteerInAir(wantedXZ, cruiseProfile.turnRateDegrees);
    }

    private void BeginAttackRun()
    {
        missilesFiredThisRun = 0;
        if (weapon != null) weapon.ResetFireCooldown();
        State = FlightState.AttackRun;
        Debug.Log($"[Aircraft:{name}] Approach acceptable — firing.");
    }

    /// <summary>
    /// Locked-heading fly-by. Tick the weapon cooldown, ask it to fire, react
    /// to the result. The weapon owns the actual cone / range / ammo checks —
    /// this method only decides whether to keep running or egress.
    /// </summary>
    private void UpdateAttackRun()
    {
        UseProfile(attackRunProfile);

        // Forward motion at locked heading (turnRate ≈ 0 in the profile).
        StepForwardAtAltitude(attackRunProfile);

        if (weapon == null) { BeginAttackEgress(); return; }

        weapon.TickFireCooldown(Time.deltaTime);

        // Cap releases at the rack size so a re-attack doesn't dump everything at once.
        if (missilesFiredThisRun >= weapon.maxAmmo)
        {
            BeginAttackEgress();
            return;
        }

        if (target == null)
        {
            Debug.Log($"[Aircraft:{name}] Target lost during attack run — exiting.");
            BeginAttackEgress();
            return;
        }

        AircraftWeapon.FireResult result = weapon.TryFire(target, firePoint, transform.forward);
        switch (result)
        {
            case AircraftWeapon.FireResult.Fired:
                missilesFiredThisRun++;
                if (!weapon.HasAmmo || missilesFiredThisRun >= weapon.maxAmmo)
                {
                    Debug.Log($"[Aircraft:{name}] Attack run complete — exiting");
                    BeginAttackEgress();
                }
                break;

            case AircraftWeapon.FireResult.TargetTooClose:
            case AircraftWeapon.FireResult.TargetBehind:
            case AircraftWeapon.FireResult.NoAmmo:
                BeginAttackEgress();
                break;

            case AircraftWeapon.FireResult.OffCone:
            case AircraftWeapon.FireResult.Cooldown:
            case AircraftWeapon.FireResult.NoTarget:
                // Continue forward — re-evaluate next tick.
                break;
        }
    }

    private void UpdateAttackEgress()
    {
        UseProfile(attackRunProfile);

        StepForwardAtAltitude(attackRunProfile);

        Vector3 travelXZ = transform.position - attackEgressStart;
        travelXZ.y = 0f;
        if (travelXZ.magnitude >= attackEgressDistance)
        {
            target = null;
            EnterWideReturnTurn();
        }
    }

    private void BeginAttackEgress()
    {
        attackEgressStart  = transform.position;
        repositionAttempts = 0;
        State              = FlightState.AttackEgress;
        Debug.Log($"[Aircraft:{name}] Entering egress phase.");
    }

    // ------------------------------------------------------------------ //
    // Return arc + helpers
    // ------------------------------------------------------------------ //

    private void UpdateWideReturnTurn()
    {
        UseProfile(cruiseProfile);

        if (homeSlot == null)
        {
            Debug.LogWarning($"[Aircraft:{name}] Home slot lost mid-return — holding in air.");
            State = FlightState.Parked;
            return;
        }

        Vector3 approach    = GetApproachPointToAirfield();
        Vector3 toApproachXZ = approach - transform.position; toApproachXZ.y = 0f;
        Vector3 forwardXZ   = transform.forward; forwardXZ.y = 0f;

        if (toApproachXZ.sqrMagnitude < 0.0001f || forwardXZ.sqrMagnitude < 0.0001f)
        {
            State = FlightState.Returning;
            return;
        }

        toApproachXZ.Normalize();
        forwardXZ.Normalize();
        float angle = Vector3.Angle(forwardXZ, toApproachXZ);

        if (angle <= returnAlignmentAngle)
        {
            Debug.Log($"[Aircraft:{name}] Return arc complete. Heading to base.");
            State = FlightState.Returning;
            return;
        }

        float turnSign = Mathf.Sign(Vector3.Cross(forwardXZ, toApproachXZ).y);
        if (Mathf.Abs(turnSign) < 0.001f) turnSign = 1f;

        float yawDelta = Mathf.Min(angle, cruiseProfile.turnRateDegrees * Time.deltaTime) * turnSign;
        transform.Rotate(0f, yawDelta, 0f, Space.World);

        StepForwardAtAltitude(cruiseProfile);
    }

    private void EnterWideReturnTurn()
    {
        State = FlightState.WideReturnTurn;
        if (homeSlot == null) return;

        if (IsAirfieldBehindCurrentHeading())
            Debug.Log($"[Aircraft:{name}] Airfield is behind aircraft — using wide return arc.");
        else
            Debug.Log($"[Aircraft:{name}] Airfield is ahead/side — using moderate return curve.");
    }

    private bool IsAirfieldBehindCurrentHeading()
    {
        if (homeSlot == null) return false;

        Vector3 forwardXZ = transform.forward; forwardXZ.y = 0f;
        Vector3 toHomeXZ  = homeSlot.position - transform.position; toHomeXZ.y = 0f;

        if (forwardXZ.sqrMagnitude < 0.0001f || toHomeXZ.sqrMagnitude < 0.0001f) return false;

        return Vector3.Dot(forwardXZ.normalized, toHomeXZ.normalized) < 0f;
    }

    private Vector3 GetApproachPointToAirfield()
    {
        if (homeSlot == null) return transform.position;
        return new Vector3(homeSlot.position.x, flightAltitude, homeSlot.position.z);
    }

    // ------------------------------------------------------------------ //
    // Patrol
    // ------------------------------------------------------------------ //

    private void UpdateFlyingToPoint()
    {
        UseProfile(cruiseProfile);

        Vector3 wantedXZ = new Vector3(patrolCenter.x, flightAltitude, patrolCenter.z);
        Vector3 dir      = wantedXZ - transform.position;
        float   distXZ   = new Vector2(dir.x, dir.z).magnitude;

        if (distXZ <= patrolCircleRadius + 0.5f)
        {
            Vector3 fromCenter = transform.position - wantedXZ;
            patrolCurrentAngle = Mathf.Atan2(fromCenter.z, fromCenter.x) * Mathf.Rad2Deg;
            patrolAngleSwept   = 0f;
            lastReportedCircle = 0;
            State = FlightState.PatrollingPoint;
            Debug.Log($"[Aircraft:{name}] Reached patrol area");
            return;
        }

        StepForwardAtAltitude(cruiseProfile);
        SmoothSteerInAir(wantedXZ, cruiseProfile.turnRateDegrees);
    }

    private void UpdatePatrollingPoint()
    {
        UseProfile(holdingProfile);

        float deltaAngle = patrolAngularSpeed * Time.deltaTime;
        patrolCurrentAngle += deltaAngle;
        patrolAngleSwept   += deltaAngle;

        float   rad         = patrolCurrentAngle * Mathf.Deg2Rad;
        Vector3 centreAtAlt = new Vector3(patrolCenter.x, flightAltitude, patrolCenter.z);
        Vector3 offset      = new Vector3(Mathf.Cos(rad) * patrolCircleRadius, 0f,
                                          Mathf.Sin(rad) * patrolCircleRadius);
        Vector3 wantedPos   = centreAtAlt + offset;

        transform.position = Vector3.MoveTowards(
            transform.position, wantedPos, holdingProfile.speed * Time.deltaTime);

        Vector3 tangent = new Vector3(-Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        if (tangent.sqrMagnitude > 0.0001f)
        {
            Quaternion want = Quaternion.LookRotation(tangent);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, want, holdingProfile.turnRateDegrees * Time.deltaTime);
        }

        int completedCircles = Mathf.FloorToInt(patrolAngleSwept / 360f);
        if (completedCircles > lastReportedCircle)
        {
            lastReportedCircle = completedCircles;
            Debug.Log($"[Aircraft:{name}] Patrol circle {completedCircles}/{patrolCircleCount}");
        }

        if (patrolAngleSwept >= 360f * patrolCircleCount)
        {
            Debug.Log($"[Aircraft:{name}] Patrol complete — returning");
            hasPatrolMission = false;
            EnterWideReturnTurn();
        }
    }

    // ------------------------------------------------------------------ //
    // Return → Landing
    // ------------------------------------------------------------------ //

    private void UpdateReturning()
    {
        UseProfile(cruiseProfile);

        if (homeSlot == null)
        {
            Debug.LogWarning($"[Aircraft:{name}] Home slot lost — holding in air.");
            State = FlightState.Parked;
            return;
        }

        Transform approach = HomeAirfield != null ? HomeAirfield.landingApproachPoint : null;
        Vector3 wantedXZ = approach != null
            ? new Vector3(approach.position.x, flightAltitude, approach.position.z)
            : new Vector3(homeSlot.position.x, flightAltitude, homeSlot.position.z);

        Vector3 dir   = wantedXZ - transform.position;
        float   distXZ = new Vector2(dir.x, dir.z).magnitude;

        if (distXZ <= landingApproachDistance)
        {
            Debug.Log($"[Aircraft:{name}] Returning to Airfield approach.");
            TryRequestLandingClearance();
            return;
        }

        StepForwardAtAltitude(cruiseProfile);
        SmoothSteerInAir(wantedXZ, cruiseProfile.turnRateDegrees);
    }

    private void TryRequestLandingClearance()
    {
        if (HomeAirfield == null)
        {
            landingClearance = null;
            State = FlightState.FinalLanding;
            finalLandingStartXZ = transform.position;
            return;
        }

        if (HomeAirfield.RequestLandingClearance(this, out Airfield.LandingClearance c))
        {
            landingClearance    = c;
            State               = FlightState.LandingApproach;
            holdingPatternAngle = 0f;
        }
        else
        {
            State = FlightState.WaitingForLandingClearance;
            landingClearanceRetryTimer = landingClearanceCheckInterval;

            Transform approach = HomeAirfield.landingApproachPoint;
            if (approach != null)
            {
                Vector3 fromCenter = transform.position - approach.position;
                holdingPatternAngle = Mathf.Atan2(fromCenter.z, fromCenter.x) * Mathf.Rad2Deg;
            }
            Debug.Log($"[Aircraft:{name}] Waiting for landing clearance.");
        }
    }

    private void UpdateWaitingForLandingClearance()
    {
        UseProfile(holdingProfile);

        if (HomeAirfield == null)
        {
            State = FlightState.FinalLanding;
            finalLandingStartXZ = transform.position;
            return;
        }

        // Fail-safe: if we've been holding for too long, an orphaned runway
        // owner may be blocking us. Ask the Airfield to force-release before
        // the next retry so this aircraft can land instead of looping forever.
        if (Time.time - stateEnteredTime > landingClearanceTimeout)
        {
            Debug.LogWarning($"[Aircraft:{name}] Landing clearance timeout — retrying after force-release.");
            HomeAirfield.ForceReleaseRunwayIfOrphaned($"holding aircraft '{name}' timeout");
            stateEnteredTime = Time.time;   // reset the timer so we don't spam every frame
        }

        landingClearanceRetryTimer -= Time.deltaTime;
        if (landingClearanceRetryTimer <= 0f)
        {
            landingClearanceRetryTimer = landingClearanceCheckInterval;
            if (HomeAirfield.RequestLandingClearance(this, out Airfield.LandingClearance c))
            {
                landingClearance = c;
                State            = FlightState.LandingApproach;
                return;
            }
        }

        Transform approach = HomeAirfield.landingApproachPoint;
        if (approach == null) return;

        float deltaAngle = patrolAngularSpeed * Time.deltaTime;
        holdingPatternAngle += deltaAngle;

        float   rad         = holdingPatternAngle * Mathf.Deg2Rad;
        Vector3 centreAtAlt = new Vector3(approach.position.x, flightAltitude, approach.position.z);
        Vector3 offset      = new Vector3(Mathf.Cos(rad) * landingHoldingRadius, 0f,
                                          Mathf.Sin(rad) * landingHoldingRadius);
        Vector3 wantedPos   = centreAtAlt + offset;

        transform.position = Vector3.MoveTowards(
            transform.position, wantedPos, holdingProfile.speed * Time.deltaTime);

        Vector3 tangent = new Vector3(-Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        if (tangent.sqrMagnitude > 0.0001f)
        {
            Quaternion want = Quaternion.LookRotation(tangent);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, want, holdingProfile.turnRateDegrees * Time.deltaTime);
        }
    }

    /// <summary>
    /// Lined up at altitude — slide onto the LandingStart anchor and align with
    /// the runway direction so FinalLanding starts the descent on-axis.
    ///
    /// IMPORTANT: This used to step-forward at cruise speed + smooth-steer toward
    /// LandingStart, which created an orbit-overshoot bug: at cruise 14 / turn
    /// 85 deg/s the minimum turn radius (~9.4u) was larger than the 0.4u arrival
    /// threshold, so any approach geometry where the jet wasn't already pointed
    /// at LandingStart caused it to circle forever. The deadlock then bled into
    /// the Airfield because activeLandingJet stayed set.
    ///
    /// New behaviour: clamp-move toward LandingStart at landingProfile.speed
    /// (guarantees arrival regardless of incoming angle) and rotate toward the
    /// runway-axis direction (Start → End). FinalLanding takes over from here
    /// once the XZ distance is within the landing arrival threshold.
    /// </summary>
    private void UpdateLandingApproach()
    {
        UseProfile(landingProfile);

        Transform start = landingClearance?.LandingStart;
        if (start == null)
        {
            finalLandingStartXZ = transform.position;
            State = FlightState.FinalLanding;
            Debug.Log($"[Aircraft:{name}] Final landing started.");
            return;
        }

        // Fail-safe: even with MoveTowards the state can stall (e.g. missing
        // markers, time-scale 0). Snap to FinalLanding from the current
        // position if we've been here too long.
        if (Time.time - stateEnteredTime > landingApproachTimeout)
        {
            Debug.LogWarning($"[Aircraft:{name}] LandingApproach timeout — snapping to FinalLanding.");
            finalLandingStartXZ = transform.position;
            State = FlightState.FinalLanding;
            return;
        }

        Vector3 wantedXZ = new Vector3(start.position.x, flightAltitude, start.position.z);
        Vector3 dir      = wantedXZ - transform.position;
        float   distXZ   = new Vector2(dir.x, dir.z).magnitude;

        if (distXZ <= landingProfile.arrivalThreshold)
        {
            finalLandingStartXZ = transform.position;
            State = FlightState.FinalLanding;
            Debug.Log($"[Aircraft:{name}] Final landing started.");
            return;
        }

        // Direct clamped move — guarantees arrival, no orbit overshoot.
        transform.position = Vector3.MoveTowards(
            transform.position, wantedXZ, landingProfile.speed * Time.deltaTime);

        // Align with the runway axis (Start → End direction) so by the time
        // we touch down we're flying straight down the lane.
        Transform end = landingClearance?.LandingEnd;
        Vector3 runwayDir = (end != null)
            ? (end.position - start.position)
            : (wantedXZ - transform.position);
        runwayDir.y = 0f;
        if (runwayDir.sqrMagnitude > 0.0001f)
        {
            Quaternion want = Quaternion.LookRotation(runwayDir.normalized);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, want, landingProfile.turnRateDegrees * Time.deltaTime);
        }
    }

    private void UpdateFinalLanding()
    {
        UseProfile(landingProfile);

        // Fail-safe: force-touchdown at current position if descent stalls.
        if (Time.time - stateEnteredTime > finalLandingTimeout)
        {
            Debug.LogWarning($"[Aircraft:{name}] FinalLanding timeout — force-touchdown at current XZ.");
            Vector3 stuck = transform.position; stuck.y = GetGroundY(); transform.position = stuck;
            EnterTaxiingToSlot();
            return;
        }

        Transform end = landingClearance?.LandingEnd;
        if (end == null)
        {
            Vector3 fallback = transform.position;
            fallback.y = GetGroundY();
            transform.position = fallback;
            EnterTaxiingToSlot();
            return;
        }

        Vector3 endXZ  = new Vector3(end.position.x, transform.position.y, end.position.z);
        Vector3 dir    = endXZ - transform.position;
        Vector2 distXZ = new Vector2(dir.x, dir.z);

        Vector3 step = (distXZ.sqrMagnitude > 0.0001f
            ? new Vector3(dir.x, 0f, dir.z).normalized * landingProfile.speed * Time.deltaTime
            : Vector3.zero);
        transform.position += step;

        // Altitude descent — linear by XZ progress along the path.
        float totalDist = Vector2.Distance(
            new Vector2(finalLandingStartXZ.x, finalLandingStartXZ.z),
            new Vector2(end.position.x,        end.position.z));
        float coveredDist = Vector2.Distance(
            new Vector2(finalLandingStartXZ.x, finalLandingStartXZ.z),
            new Vector2(transform.position.x,  transform.position.z));
        float t = totalDist > 0.01f ? Mathf.Clamp01(coveredDist / totalDist) : 1f;

        float groundY = GetGroundY();
        float targetY = Mathf.Lerp(flightAltitude, groundY, t);
        targetY = Mathf.MoveTowards(transform.position.y, targetY,
                                    landingProfile.verticalSpeed * Time.deltaTime);

        Vector3 pos = transform.position; pos.y = targetY; transform.position = pos;

        Vector3 face = new Vector3(dir.x, 0f, dir.z);
        if (face.sqrMagnitude > 0.0001f)
        {
            Quaternion want = Quaternion.LookRotation(face);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, want, landingProfile.turnRateDegrees * Time.deltaTime);
        }

        if (distXZ.magnitude <= landingProfile.arrivalThreshold && targetY <= groundY + 0.01f)
        {
            Debug.Log($"[Aircraft:{name}] Touchdown.");
            EnterTaxiingToSlot();
        }
    }

    private void EnterTaxiingToSlot()
    {
        if (HomeAirfield != null) HomeAirfield.ReleaseLandingRunway(this);

        taxiBackRoute    = landingClearance?.TaxiBackRoute ?? new Transform[0];
        taxiBackIndex    = 0;
        landingClearance = null;

        Vector3 pos = transform.position; pos.y = GetGroundY(); transform.position = pos;

        State = FlightState.TaxiingToSlot;
        Debug.Log($"[Aircraft:{name}] Taxiing back to slot.");
    }

    private void UpdateTaxiingToSlot()
    {
        UseProfile(taxiProfile);

        // Fail-safe: if a waypoint is unreachable for any reason, snap to slot.
        if (Time.time - stateEnteredTime > taxiToSlotTimeout)
        {
            Debug.LogWarning($"[Aircraft:{name}] TaxiingToSlot timeout — snapping to home slot.");
            ParkAtHomeSlot();
            return;
        }

        if (taxiBackRoute == null || taxiBackIndex >= taxiBackRoute.Length)
        {
            ParkAtHomeSlot();
            return;
        }

        Transform wp = taxiBackRoute[taxiBackIndex];
        if (wp == null) { taxiBackIndex++; return; }

        float groundY = GetGroundY();
        Vector3 wpPos = wp.position; wpPos.y = groundY;

        Vector3 dir   = wpPos - transform.position;
        Vector2 distXZ = new Vector2(dir.x, dir.z);

        if (distXZ.magnitude <= landingProfile.arrivalThreshold)
        {
            taxiBackIndex++;
            if (taxiBackIndex >= taxiBackRoute.Length) ParkAtHomeSlot();
            return;
        }

        Vector3 step = dir.normalized * taxiProfile.speed * Time.deltaTime;
        if (step.magnitude > dir.magnitude) step = dir;
        transform.position += step;

        Vector3 pos = transform.position; pos.y = groundY; transform.position = pos;
        FaceVelocity(step, taxiProfile.turnRateDegrees);
    }

    private void ParkAtHomeSlot()
    {
        if (homeSlot != null)
        {
            transform.position = homeSlot.position;
            transform.rotation = homeSlot.rotation;
        }
        target           = null;
        taxiBackRoute    = null;
        taxiBackIndex    = 0;
        State            = FlightState.Parked;
        Debug.Log($"[Aircraft:{name}] Parked at original slot. Ammo: {CurrentAmmo}/{maxAmmo} — reloading.");
    }

    // ------------------------------------------------------------------ //
    // Movement helpers
    // ------------------------------------------------------------------ //

    /// <summary>Move forward at the profile's speed and re-lock altitude to <see cref="flightAltitude"/>.</summary>
    private void StepForwardAtAltitude(FlightProfile profile)
    {
        Vector3 step = transform.forward * profile.speed * Time.deltaTime;
        transform.position += step;

        Vector3 pos = transform.position;
        pos.y = flightAltitude;
        transform.position = pos;
    }

    /// <summary>Rotate to face <paramref name="step"/>'s XZ direction at <paramref name="turnRate"/> deg/sec.</summary>
    private void FaceVelocity(Vector3 step, float turnRate)
    {
        Vector3 flat = new Vector3(step.x, 0f, step.z);
        if (flat.sqrMagnitude < 0.0001f) return;

        Quaternion want = Quaternion.LookRotation(flat);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turnRate * Time.deltaTime);
    }

    /// <summary>Yaw toward an XZ world position at <paramref name="turnRate"/> deg/sec. Only flat direction is used.</summary>
    private void SmoothSteerInAir(Vector3 wantedWorldPos, float turnRate)
    {
        Vector3 to = wantedWorldPos - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.0001f) return;

        Quaternion want = Quaternion.LookRotation(to.normalized);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, want, turnRate * Time.deltaTime);
    }

    /// <summary>Logs a single line on actual profile switches; cheap on repeat calls.</summary>
    private void UseProfile(FlightProfile p)
    {
        if (p == lastLoggedProfile) return;
        lastLoggedProfile = p;
        Debug.Log($"[Aircraft:{name}] Using profile: {p.name}");
    }

    // ------------------------------------------------------------------ //
    // Approach validation
    // ------------------------------------------------------------------ //

    private enum ApproachQuality
    {
        Good,
        TooClose,
        TooFar,
        BadAngle,
        Behind,
        NoTarget,
    }

    private ApproachQuality EvaluateApproach(out float distXZ, out float forwardDot)
    {
        distXZ     = 0f;
        forwardDot = 0f;
        if (target == null) return ApproachQuality.NoTarget;

        Vector3 toTarget   = target.transform.position - transform.position;
        Vector3 toTargetXZ = new Vector3(toTarget.x, 0f, toTarget.z);
        distXZ = toTargetXZ.magnitude;

        Vector3 fwdXZ = transform.forward; fwdXZ.y = 0f;
        if (fwdXZ.sqrMagnitude < 0.0001f) fwdXZ = Vector3.forward;
        fwdXZ.Normalize();

        Vector3 dirNorm = (distXZ > 0.0001f) ? toTargetXZ / distXZ : fwdXZ;
        forwardDot = Vector3.Dot(fwdXZ, dirNorm);
        float angleToTarget = Vector3.Angle(fwdXZ, dirNorm);

        if (forwardDot < 0f)                    return ApproachQuality.Behind;
        if (distXZ < minAttackApproachDistance) return ApproachQuality.TooClose;
        if (forwardDot < minAttackForwardDot)   return ApproachQuality.BadAngle;
        if (angleToTarget > minAttackAngle)     return ApproachQuality.BadAngle;
        if (distXZ > attackRange)               return ApproachQuality.TooFar;

        return ApproachQuality.Good;
    }

    /// <summary>
    /// Enter RepositioningForAttack — unless we've exhausted
    /// <see cref="maxRepositionAttempts"/>, in which case commit to a relaxed
    /// attack run (any forward direction, within max range) instead of looping.
    /// The weapon's TryFire still gates the actual missile spawn.
    /// </summary>
    private void EnterRepositioning(ApproachQuality reason, float forwardDot, float distXZ)
    {
        repositionAttempts++;

        if (repositionAttempts > maxRepositionAttempts)
        {
            if (forwardDot >= 0f && weapon != null && distXZ <= weapon.maxReleaseDistance)
            {
                Debug.Log($"[Aircraft:{name}] Relaxed attack rules after reposition.");
                BeginAttackRun();
            }
            else
            {
                Debug.Log($"[Aircraft:{name}] Reposition attempts spent — abandoning pass.");
                target = null;
                EnterWideReturnTurn();
            }
            return;
        }

        repositionStartTime = Time.time;
        State               = FlightState.RepositioningForAttack;
        Debug.Log($"[Aircraft:{name}] Repositioning for better approach " +
                  $"(attempt {repositionAttempts}/{maxRepositionAttempts}, reason: {reason}).");
    }
}
