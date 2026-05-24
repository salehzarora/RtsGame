using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

/// <summary>
/// Drives an aircraft through its sortie loop:
///
///   Parked → WaitingForTakeoffClearance → TaxiingToRunway → TakeoffRoll
///                                                                  ↓
///                                                              Climbing
///                                                                  ↓
///                              FlyingToTarget ←──────────── (mission dispatch)
///                                    ↓                              ↓
///                              AttackRun → AttackEgress     FlyingToPoint
///                                              ↓                    ↓
///                                          WideReturnTurn ← ← PatrollingPoint
///                                              ↓
///                                          Returning → Landing → Parked
///
/// The Airfield owns the takeoff queue. A parked jet given a mission asks
/// for a runway lane via <see cref="Airfield.RequestTakeoffClearance"/> —
/// it either taxis immediately (lane free) or enters
/// <see cref="FlightState.WaitingForTakeoffClearance"/> until a lane frees.
/// At most two jets are airborne in the takeoff sequence at once (one per
/// lane), so a six-jet order departs in three pairs.
///
/// Attack mode is a real fly-by — the jet locks its forward direction when
/// it enters AttackRun, keeps moving while releasing missiles spaced by
/// <see cref="missileFireDelay"/>, then flies straight for
/// <see cref="attackEgressDistance"/> world units (AttackEgress) before
/// any turning is allowed. The recovery turn happens in WideReturnTurn,
/// which applies a max-yaw-rate steering arc toward the home approach
/// point — no 90° snaps, ever. Missiles are real projectiles (see
/// <see cref="StrikeMissile"/>), not instant tracer lines.
///
/// The aircraft does NOT use NavMeshAgent / UnitMovement — it moves by direct
/// transform manipulation. Ground states (TaxiingToRunway / AligningForTakeoff
/// / TakeoffRoll) hold the aircraft at the home slot's world Y (plus the
/// optional <see cref="groundHeightOffset"/> lift, default 0); Climbing onward
/// holds it at <see cref="flightAltitude"/>.
///
/// Setup (done automatically by Tools → RTS → Air System → Create Strike Jet Prefab):
///   1. Attach to the aircraft root GameObject (with Health, SelectableAircraft,
///      UnitCategory = Aircraft, and a BoxCollider for selection).
///   2. Assign a child Transform "FirePoint" under/near the missile pods.
///   3. Tune attack range / ammo / damage / altitude in the Inspector.
///   4. The Airfield calls AssignHome(...) at spawn time — do NOT call it yourself.
/// </summary>
public class AirUnitController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    public enum FlightState
    {
        Parked,
        WaitingForTakeoffClearance,   // queued — Airfield will grant when a lane is free
        TaxiingToRunway,              // following waypoints (taxi → lane corridor → queue → start)
        AligningForTakeoff,           // pivot in place at TakeoffStart, face TakeoffEnd direction
        WaitingForBatchTakeoff,       // aligned, holding for batch-partner ready signal from Airfield
        TakeoffRoll,                  // accelerating along the runway from Start to End
        Climbing,                     // airborne, gaining altitude after the roll
        FlyingToTarget,
        AttackRun,                    // diving past target, releasing missiles in flight
        AttackEgress,                 // post-strike straight run before any turning
        WideReturnTurn,               // smooth max-turn-rate arc back toward base (no 90° snap)
        FlyingToPoint,
        PatrollingPoint,
        Returning,                    // flying back to the airfield approach point
        WaitingForLandingClearance,   // holding pattern circle while the runway is busy
        LandingApproach,              // lined up with the runway, still at altitude
        FinalLanding,                 // descending from altitude to ground along the runway
        TaxiingToSlot                 // on the apron, taxiing back to the home slot
    }

    // ------------------------------------------------------------------ //
    // Inspector — Flight
    // ------------------------------------------------------------------ //

    [Header("Flight")]
    [Tooltip("Cruise altitude in world units while flying.")]
    public float flightAltitude = 12f;

    [Tooltip("Horizontal cruise speed (units / second).")]
    public float cruiseSpeed = 14f;

    [Tooltip("Vertical climb / descent speed (units / second).")]
    public float verticalSpeed = 6f;

    [Tooltip("Yaw rotation speed when turning toward a target (degrees / second).")]
    public float turnSpeed = 180f;

    [Tooltip("XZ distance below which the Returning state hands off to Landing (legacy " +
             "field; new flow uses landingApproachDistance for the approach handoff and " +
             "landingArrivalThreshold for tight waypoint arrival).")]
    public float landingApproachThreshold = 0.5f;

    [Header("Landing — Approach & Hold")]
    [Tooltip("XZ distance below which the Returning state considers itself 'at the airfield " +
             "approach point' and requests landing clearance.")]
    public float landingApproachDistance = 25f;

    [Tooltip("Radius of the holding-pattern circle flown around the airfield approach point " +
             "while the runway is busy.")]
    public float landingHoldingRadius = 14f;

    [Tooltip("Seconds between landing-clearance retries while in WaitingForLandingClearance.")]
    public float landingClearanceCheckInterval = 0.5f;

    [Header("Landing — Final & Taxi-back")]
    [Tooltip("Horizontal speed (world units / sec) along the runway during FinalLanding.")]
    public float landingRollSpeed = 7f;

    [Tooltip("Vertical descent rate (world units / sec) during FinalLanding. Combined with " +
             "landingRollSpeed and the runway length, determines the descent angle.")]
    public float landingDescentSpeed = 5f;

    [Tooltip("Speed (world units / sec) while taxiing back from the runway exit to the slot.")]
    public float taxiBackSpeed = 4f;

    [Tooltip("Tight XZ distance threshold below which a landing or taxi-back waypoint counts " +
             "as reached.")]
    public float landingArrivalThreshold = 0.4f;

    [Header("Taxi / Takeoff")]
    [Tooltip("Ground speed (units/sec) while taxiing from slot to runway.")]
    public float taxiSpeed = 4f;

    [Tooltip("Ground speed (units/sec) while rolling down the runway.")]
    public float takeoffRollSpeed = 10f;

    [Tooltip("Vertical climb rate (units/sec) once airborne, until cruise altitude.")]
    public float climbSpeed = 8f;

    [Tooltip("XZ distance below which a taxi waypoint counts as reached.")]
    public float taxiArrivalThreshold = 0.3f;

    [Tooltip("XZ distance from the TakeoffEnd point at which the lane is released " +
             "back to the Airfield and the aircraft transitions to Climbing.")]
    public float takeoffEndReleaseDistance = 2f;

    [Tooltip("Extra Y lift (world units) added on top of the home slot's Y while " +
             "taxiing / rolling. With the Y=0 baseline cleanup this is 0 — the " +
             "logic root sits at the slot's world Y when on the ground, and any " +
             "visual lift is handled by the StrikeJet's visual children (e.g. " +
             "the selection ring sits above the runway via its own local Y).")]
    public float groundHeightOffset = 0f;

    [Header("Takeoff Alignment")]
    [Tooltip("Yaw rate (degrees/sec) while AligningForTakeoff. The aircraft holds " +
             "its position at TakeoffStart and rotates in place toward TakeoffEnd.")]
    public float takeoffAlignmentTurnSpeed = 90f;

    [Tooltip("Angle (degrees) below which alignment is considered complete and " +
             "the aircraft transitions to TakeoffRoll.")]
    public float takeoffAlignmentAngleThreshold = 3f;

    // ------------------------------------------------------------------ //
    // Inspector — Attack
    // ------------------------------------------------------------------ //

    [Header("Attack")]
    [Tooltip("Missile release range — when the jet's XZ distance to the target drops below this, " +
             "it stops steering, locks its current forward direction, and starts the attack run.")]
    public float attackRange = 18f;

    [Tooltip("Maximum missiles per sortie. After firing this many, the aircraft exits and returns.")]
    public int maxAmmo = 2;

    [Tooltip("Base damage per missile, before category modifier in DamageRules.")]
    public float missileDamage = 120f;

    [Tooltip("Seconds between consecutive missile launches during an attack run.")]
    public float missileFireDelay = 0.75f;

    [Tooltip("Damage type used for the modifier lookup. Missile is strong vs Vehicle/Building.")]
    public DamageType damageType = DamageType.Missile;

    [Header("Attack Run")]
    [Tooltip("Missile projectile speed (world units / second).")]
    public float missileProjectileSpeed = 30f;

    [Tooltip("Forward distance (world units) the jet keeps flying straight after the last missile " +
             "before it is allowed to start any turn. Renamed from attackRunExitDistance.")]
    [FormerlySerializedAs("attackRunExitDistance")]
    public float attackEgressDistance = 15f;

    [Tooltip("If true, AttackTarget calls during an active attack run will swap the target. " +
             "Default false: the run completes on its current target before retargeting.")]
    public bool attackRunCanRetarget = false;

    [Header("Return Arc")]
    [Tooltip("Maximum yaw turn rate during WideReturnTurn, in degrees per second. " +
             "Combined with cruiseSpeed this determines the minimum turn radius — at " +
             "cruise 14 / rate 45, the radius is ~17.8 world units.")]
    public float maxTurnRateDegrees = 45f;

    [Tooltip("Angle (degrees) between current heading and the direction to the home slot " +
             "below which WideReturnTurn hands off to Returning. Smaller = tighter alignment.")]
    public float returnAlignmentAngle = 8f;

    [Tooltip("Seconds the bright impact sphere is visible at the missile's blast point.")]
    public float impactFlashDuration = 0.2f;

    [Tooltip("Colour of the missile body and impact flash.")]
    [ColorUsage(false)] public Color missileColor = new Color(1f, 0.45f, 0.10f);

    [Header("Reload")]
    [Tooltip("Seconds spent Parked between each automatic missile reload. " +
             "Reload only ticks while the aircraft is in the Parked state.")]
    public float reloadSecondsPerMissile = 3f;

    [Tooltip("If false, AttackTarget calls with 0 ammo are rejected with a warning. " +
             "If true, the jet still takes off and dives, but no missile releases.")]
    public bool canAttackWithNoAmmo = false;

    [Header("Patrol")]
    [Tooltip("Radius of the circle flown around a patrol point, in world units.")]
    public float patrolCircleRadius = 10f;

    [Tooltip("Number of full laps to fly around the patrol point before returning home.")]
    public int patrolCircleCount = 2;

    [Tooltip("Angular speed around the patrol circle, in degrees / second. " +
             "60 deg/s ≈ a full lap every 6 seconds.")]
    public float patrolAngularSpeed = 60f;

    [Header("Visual")]
    [Tooltip("Launch origin for spawned StrikeMissile projectiles. " +
             "Falls back to transform.position if null.")]
    public Transform firePoint;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    public FlightState State { get; private set; } = FlightState.Parked;
    public int CurrentAmmo { get; private set; }
    public Airfield HomeAirfield { get; private set; }
    /// <summary>The parking slot Transform this aircraft is bound to.
    /// Airfield reads this to identify the jet's index for the taxi route.</summary>
    public Transform HomeSlot => homeSlot;

    /// <summary>
    /// Identifier set by UnitSelector when the player issues the same right-click
    /// command to multiple selected aircraft. Non-zero means the aircraft is
    /// part of a synchronized launch group; 0 means a solo command.
    /// Airfield uses matching IDs to decide whether to sync-release Lane A and
    /// Lane B from <see cref="FlightState.WaitingForBatchTakeoff"/>.
    /// </summary>
    public long LaunchGroupId { get; private set; }

    private Transform homeSlot;
    private Health    target;

    // Attack-run state. fireDelayTimer counts down to the next missile;
    // missilesFiredThisRun caps total releases per pass; attackEgressStart
    // is the AttackEgress anchor for measuring travelled distance.
    private float   fireDelayTimer;
    private int     missilesFiredThisRun;
    private Vector3 attackEgressStart;

    // Reload-while-parked timer. Only ticks in the Parked state; reset to 0
    // on landing so a freshly-landed jet doesn't get a free first reload.
    private float reloadTimer;

    // Landing flow state. landingClearance is the bundle from Airfield;
    // taxiBackRoute is the post-touchdown waypoint list (exit → taxi-point → slot);
    // finalLandingStartXZ is captured on entry to FinalLanding so we can lerp
    // altitude as a fraction of the descent path.
    private Airfield.LandingClearance landingClearance;
    private Transform[] taxiBackRoute;
    private int         taxiBackIndex;
    private Vector3     finalLandingStartXZ;
    private float       landingClearanceRetryTimer;
    private float       holdingPatternAngle;

    // Takeoff sequence state. clearance holds the lane assignment + waypoints
    // handed back by Airfield.RequestTakeoffClearance. taxiRoute is the list
    // of points the aircraft visits in order; taxiRouteIndex is the next one.
    private Airfield.TakeoffClearance clearance;
    private Transform[] taxiRoute;
    private int         taxiRouteIndex;
    // True once we've handed the lane back to Airfield — prevents a double-release.
    private bool        laneReleased;

    // Patrol mission state. hasPatrolMission gates the Climbing dispatch and
    // FlyToPoint behaviour so an aircraft never patrols by accident.
    private bool    hasPatrolMission;
    private Vector3 patrolCenter;
    private float   patrolAngleSwept;     // total degrees flown around the centre this mission
    private float   patrolCurrentAngle;   // current angle on the circle (degrees, math convention)
    private int     lastReportedCircle;   // for the "circle N/M" log

    // ------------------------------------------------------------------ //
    // Wiring — called by Airfield at spawn
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Bind this aircraft to its parent Airfield and the parking-slot Transform
    /// it returns to between sorties. Called by Airfield.ProduceStrikeJet.
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

        CurrentAmmo = maxAmmo;
        reloadTimer = 0f;
        State       = FlightState.Parked;
    }

    // ------------------------------------------------------------------ //
    // Public API — called by UnitSelector when the player right-clicks an enemy
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Order the aircraft to attack <paramref name="enemy"/>. If parked it
    /// takes off; if already airborne it retargets. Ignores friendlies.
    ///
    /// <paramref name="groupId"/> identifies a multi-aircraft launch group:
    ///   • 0  → solo command, no batch synchronization at takeoff.
    ///   • &gt;0 → all aircraft sharing this ID belong to the same group; the
    ///         Airfield syncs Lane A + Lane B in-batch pairs at TakeoffRoll.
    /// </summary>
    public void AttackTarget(Health enemy, long groupId = 0L)
    {
        if (enemy == null || enemy.team == Health.Team.Player)
        {
            Debug.LogWarning($"[Aircraft:{name}] Invalid attack target — ignoring.");
            return;
        }

        // Don't disturb an active strike — wait for the run to complete unless
        // the operator explicitly opts in via attackRunCanRetarget.
        if ((State == FlightState.AttackRun || State == FlightState.AttackEgress) &&
            !attackRunCanRetarget)
        {
            Debug.Log($"[Aircraft:{name}] Cannot retarget during attack run.");
            return;
        }

        // No ammo — refuse the order outright (unless the operator allowed
        // empty-rack runs). Reload happens automatically while Parked.
        if (CurrentAmmo <= 0 && !canAttackWithNoAmmo)
        {
            Debug.LogWarning($"[Aircraft:{name}] Cannot attack — no missiles loaded.");
            return;
        }

        target = enemy;
        hasPatrolMission = false; // attack command overrides any pending patrol
        LaunchGroupId    = groupId;

        switch (State)
        {
            case FlightState.Parked:
                // Ask the Airfield for a runway lane. The mission state is
                // selected later, after Climbing finishes — see UpdateClimbing.
                RequestTakeoffClearanceFromAirfield(forAttack: true, enemyName: enemy.name);
                break;

            case FlightState.WaitingForTakeoffClearance:
            case FlightState.TaxiingToRunway:
            case FlightState.TakeoffRoll:
            case FlightState.Climbing:
                // Mid-departure — keep current clearance, just update mission.
                Debug.Log($"[Aircraft:{name}] Mid-departure — will attack '{enemy.name}' after climb-out.");
                break;

            case FlightState.WaitingForLandingClearance:
            case FlightState.LandingApproach:
            case FlightState.FinalLanding:
            case FlightState.TaxiingToSlot:
                // Don't try to abort mid-landing — runway state is committed.
                // Wait for Parked, then accept the new order.
                Debug.Log($"[Aircraft:{name}] Mid-landing — ignoring attack order until parked.");
                target = null;
                return;

            default:
                // Already in the air on a mission — just swap targets.
                State = FlightState.FlyingToTarget;
                Debug.Log($"[Aircraft:{name}] Retargeting to '{enemy.name}'.");
                break;
        }
    }

    /// <summary>
    /// Order the aircraft to fly to <paramref name="worldPos"/>, circle it
    /// <see cref="patrolCircleCount"/> times, then return to its home slot.
    /// Mid-attack the call is ignored so an active strike isn't interrupted.
    ///
    /// See <see cref="AttackTarget"/> for the <paramref name="groupId"/> semantics.
    /// </summary>
    public void FlyToPoint(Vector3 worldPos, long groupId = 0L)
    {
        // Don't interrupt an active strike — patrol commands are advisory.
        if (State == FlightState.AttackRun || State == FlightState.AttackEgress)
        {
            Debug.Log($"[Aircraft:{name}] Mid-attack — ignoring patrol command.");
            return;
        }

        // Patrol replaces any pending attack target.
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
                // Mid-departure — current taxi/roll/climb keeps going, mission
                // dispatch picks up the new patrolCenter when Climbing exits.
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
                // Airborne (FlyingToTarget, FlyingToPoint, PatrollingPoint, Returning).
                State = FlightState.FlyingToPoint;
                Debug.Log($"[Aircraft:{name}] Diverting to new patrol point.");
                break;
        }
    }

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
            // Immediate grant.
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
            // Queued — Airfield will call GrantTakeoffClearance when our turn comes up.
            State = FlightState.WaitingForTakeoffClearance;
        }
    }

    /// <summary>
    /// Copies the clearance's ordered TaxiRoute (per-slot pull-out → lane
    /// corridor → runway queue → takeoff start), filtering out any nulls so
    /// a partially-configured Airfield still produces a usable route. The
    /// last entry is the TakeoffStart, which is where AligningForTakeoff
    /// kicks in.
    /// </summary>
    private Transform[] BuildTaxiRoute(Airfield.TakeoffClearance c)
    {
        if (c == null || c.TaxiRoute == null) return new Transform[0];

        List<Transform> wps = new List<Transform>(c.TaxiRoute.Length);
        foreach (Transform wp in c.TaxiRoute)
            if (wp != null) wps.Add(wp);
        return wps.ToArray();
    }

    // ------------------------------------------------------------------ //
    // Unity lifecycle
    // ------------------------------------------------------------------ //

    private void Update()
    {
        switch (State)
        {
            case FlightState.Parked:                     UpdateParkedReload();      break;
            case FlightState.WaitingForTakeoffClearance: /* idle, Airfield drives  */ break;
            case FlightState.TaxiingToRunway:            UpdateTaxiingToRunway();   break;
            case FlightState.AligningForTakeoff:         UpdateAligningForTakeoff();break;
            case FlightState.WaitingForBatchTakeoff:     UpdateWaitingForBatchTakeoff(); break;
            case FlightState.TakeoffRoll:                UpdateTakeoffRoll();       break;
            case FlightState.Climbing:                   UpdateClimbing();          break;
            case FlightState.FlyingToTarget:             UpdateFlyingToTarget();    break;
            case FlightState.AttackRun:                  UpdateAttackRun();         break;
            case FlightState.AttackEgress:               UpdateAttackEgress();      break;
            case FlightState.WideReturnTurn:             UpdateWideReturnTurn();    break;
            case FlightState.FlyingToPoint:              UpdateFlyingToPoint();     break;
            case FlightState.PatrollingPoint:            UpdatePatrollingPoint();   break;
            case FlightState.Returning:                   UpdateReturning();                  break;
            case FlightState.WaitingForLandingClearance:  UpdateWaitingForLandingClearance(); break;
            case FlightState.LandingApproach:             UpdateLandingApproach();            break;
            case FlightState.FinalLanding:                UpdateFinalLanding();               break;
            case FlightState.TaxiingToSlot:               UpdateTaxiingToSlot();              break;
        }
    }

    // ------------------------------------------------------------------ //
    // State updates
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Reloads one missile every <see cref="reloadSecondsPerMissile"/> seconds
    /// while Parked. Stops reloading once the rack is full. Only ticks in
    /// the Parked state — flying / attacking / returning / landing all skip
    /// this method by virtue of the Update() switch.
    /// </summary>
    private void UpdateParkedReload()
    {
        if (CurrentAmmo >= maxAmmo)
        {
            reloadTimer = 0f;
            return;
        }

        reloadTimer += Time.deltaTime;
        if (reloadTimer >= reloadSecondsPerMissile)
        {
            reloadTimer = 0f;
            CurrentAmmo = Mathf.Min(maxAmmo, CurrentAmmo + 1);
            Debug.Log($"[Aircraft:{name}] Reloaded missile. Ammo: {CurrentAmmo}/{maxAmmo}");
        }
    }

    // ------------------------------------------------------------------ //
    // Taxi → Roll → Climb (replaces the old vertical TakingOff)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Drive the aircraft along <see cref="taxiRoute"/> at <see cref="taxiSpeed"/>,
    /// staying at <see cref="GetGroundY"/>. Once the last waypoint
    /// (the TakeoffStart) is reached, transition to AligningForTakeoff so
    /// the jet can pivot smoothly in place instead of sliding into the run.
    /// </summary>
    private void UpdateTaxiingToRunway()
    {
        float groundY = GetGroundY();
        if (taxiRoute == null || taxiRouteIndex >= taxiRoute.Length)
        {
            // Route ran out (or missing waypoints) — hand off to alignment
            // anyway so the jet still pivots before rolling. Real fix:
            // re-run Repair Airfield Layout.
            State = FlightState.AligningForTakeoff;
            return;
        }

        Transform wp = taxiRoute[taxiRouteIndex];
        if (wp == null)
        {
            taxiRouteIndex++;
            return;
        }

        Vector3 target = wp.position;
        target.y = groundY;

        Vector3 dir = target - transform.position;
        Vector2 distXZ = new Vector2(dir.x, dir.z);

        if (distXZ.magnitude <= taxiArrivalThreshold)
        {
            taxiRouteIndex++;
            if (taxiRouteIndex >= taxiRoute.Length)
            {
                State = FlightState.AligningForTakeoff;
                Debug.Log($"[Aircraft:{name}] Aligning for takeoff");
            }
            return;
        }

        // Move at taxi speed, hold ground height, face direction of motion.
        Vector3 step = dir.normalized * taxiSpeed * Time.deltaTime;
        if (step.magnitude > dir.magnitude) step = dir;
        transform.position += step;

        Vector3 pos = transform.position;
        pos.y = groundY;
        transform.position = pos;

        FaceVelocity(step);
    }

    /// <summary>
    /// Hold the aircraft on the takeoff start mark and pivot smoothly in
    /// place toward the runway direction (TakeoffStart → TakeoffEnd). The
    /// rotation is clamped by <see cref="takeoffAlignmentTurnSpeed"/>;
    /// position is hard-locked to TakeoffStart so the wheels don't slide.
    /// Transitions to TakeoffRoll once the yaw angle is below
    /// <see cref="takeoffAlignmentAngleThreshold"/>.
    /// </summary>
    private void UpdateAligningForTakeoff()
    {
        if (clearance == null || clearance.TakeoffStart == null || clearance.TakeoffEnd == null)
        {
            // Missing markers — fall through to rolling so the jet doesn't stall.
            State = FlightState.TakeoffRoll;
            return;
        }

        // Hard-lock position at the takeoff start so a finite rotation rate
        // doesn't appear as a side-skidding pivot.
        Vector3 holdPos = clearance.TakeoffStart.position;
        holdPos.y = GetGroundY();
        transform.position = holdPos;

        // Runway direction from the two lane markers, projected to the XZ plane.
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
            // Snap to exact heading so the roll is straight down the lane.
            transform.rotation = want;

            // Hand off to the Airfield instead of rolling immediately. For a
            // solo command (LaunchGroupId = 0 or no batch partner), Airfield
            // calls BeginTakeoffRoll back synchronously. For a synchronized
            // group launch, the jet holds in WaitingForBatchTakeoff until its
            // Lane-A / Lane-B partner is also ready.
            State = FlightState.WaitingForBatchTakeoff;
            Debug.Log($"[Aircraft:{name}] Ready at takeoff hold point.");

            if (HomeAirfield != null)
                HomeAirfield.NotifyReadyForTakeoffRoll(this);
            else
                BeginTakeoffRoll();   // no airfield — just roll
            return;
        }

        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, want, takeoffAlignmentTurnSpeed * Time.deltaTime);
    }

    /// <summary>
    /// Holding state at the takeoff start — orientation already locked to
    /// the runway direction, position re-locked each frame to the takeoff
    /// start mark. The Airfield calls <see cref="BeginTakeoffRoll"/> when
    /// the synchronized batch partner is also ready (or after the timeout).
    /// </summary>
    private void UpdateWaitingForBatchTakeoff()
    {
        if (clearance == null || clearance.TakeoffStart == null) return;

        Vector3 holdPos = clearance.TakeoffStart.position;
        holdPos.y = GetGroundY();
        transform.position = holdPos;
        // Rotation already locked from AligningForTakeoff.
    }

    /// <summary>
    /// Called by Airfield to release this aircraft into the takeoff roll.
    /// Safe to call from <see cref="FlightState.AligningForTakeoff"/> or
    /// <see cref="FlightState.WaitingForBatchTakeoff"/>; ignored otherwise.
    /// </summary>
    public void BeginTakeoffRoll()
    {
        if (State != FlightState.WaitingForBatchTakeoff &&
            State != FlightState.AligningForTakeoff)
            return;
        State = FlightState.TakeoffRoll;
        Debug.Log($"[Aircraft:{name}] Takeoff roll started");
    }

    /// <summary>
    /// Accelerate along the runway from <see cref="Airfield.TakeoffClearance.TakeoffStart"/>
    /// toward <see cref="Airfield.TakeoffClearance.TakeoffEnd"/>, still on the
    /// ground. Releases the lane (and switches to Climbing) when the jet is
    /// within <see cref="takeoffEndReleaseDistance"/> of the end marker.
    /// </summary>
    private void UpdateTakeoffRoll()
    {
        if (clearance == null || clearance.TakeoffEnd == null)
        {
            // No end marker — just rotate up and hope for the best.
            ReleaseLane();
            State = FlightState.Climbing;
            return;
        }

        float groundY = GetGroundY();
        Vector3 target = clearance.TakeoffEnd.position;
        target.y = groundY;
        Vector3 dir = target - transform.position;
        Vector2 distXZ = new Vector2(dir.x, dir.z);

        if (distXZ.magnitude <= takeoffEndReleaseDistance)
        {
            ReleaseLane();
            State = FlightState.Climbing;
            return;
        }

        Vector3 step = dir.normalized * takeoffRollSpeed * Time.deltaTime;
        transform.position += step;

        Vector3 pos = transform.position;
        pos.y = groundY;
        transform.position = pos;

        FaceVelocity(step);
    }

    /// <summary>
    /// Hold the runway heading and climb out at <see cref="climbSpeed"/> while
    /// moving forward at <see cref="cruiseSpeed"/>. Once at altitude, dispatch
    /// to the mission state (attack / patrol / wide return).
    /// </summary>
    private void UpdateClimbing()
    {
        Vector3 step = transform.forward * cruiseSpeed * Time.deltaTime;
        transform.position += step;

        Vector3 pos = transform.position;
        pos.y = Mathf.MoveTowards(pos.y, flightAltitude, climbSpeed * Time.deltaTime);
        transform.position = pos;

        if (pos.y >= flightAltitude - 0.05f)
        {
            if (target != null)            State = FlightState.FlyingToTarget;
            else if (hasPatrolMission)     State = FlightState.FlyingToPoint;
            else                           EnterWideReturnTurn();

            Debug.Log($"[Aircraft:{name}] Reached cruise altitude.");
        }
    }

    /// <summary>Returns the runway lane to the Airfield exactly once per mission.</summary>
    private void ReleaseLane()
    {
        if (laneReleased) return;
        laneReleased = true;
        if (HomeAirfield != null) HomeAirfield.ReleaseTakeoffSlot(this);
    }

    /// <summary>
    /// Resolves the ground Y the aircraft should hold while taxiing / rolling
    /// / aligning. Uses the home slot's world Y as the baseline (so the
    /// airfield can sit at any world Y — flat terrain or elevated), then
    /// adds <see cref="groundHeightOffset"/> as an optional lift. Defaults
    /// to slot.position.y when groundHeightOffset is the standard 0.
    /// </summary>
    private float GetGroundY()
    {
        float baseY = (homeSlot != null) ? homeSlot.position.y : 0f;
        return baseY + groundHeightOffset;
    }

    private void UpdateFlyingToTarget()
    {
        if (target == null)
        {
            // Target evaporated mid-cruise — head home via the arc, no snap.
            EnterWideReturnTurn();
            return;
        }

        Vector3 wantedXZ = new Vector3(target.transform.position.x, flightAltitude, target.transform.position.z);
        Vector3 dir      = wantedXZ - transform.position;
        float distXZ     = new Vector2(dir.x, dir.z).magnitude;

        if (distXZ <= attackRange)
        {
            // Enter the attack run with the jet's CURRENT forward direction
            // frozen — it should fly past the target, not over it.
            missilesFiredThisRun = 0;
            fireDelayTimer       = 0f;     // first missile releases immediately
            State                = FlightState.AttackRun;
            Debug.Log($"[Aircraft:{name}] Starting attack run on '{target.name}'");
            return;
        }

        Vector3 step = dir.normalized * cruiseSpeed * Time.deltaTime;
        transform.position += step;

        FaceVelocity(step);
    }

    /// <summary>
    /// Fly straight forward at cruise speed while releasing up to
    /// <see cref="maxAmmo"/> missiles, spaced by <see cref="missileFireDelay"/>.
    /// The jet does NOT steer toward the target during the run — that's the
    /// whole point: it's a fly-by, not a hover.
    /// </summary>
    private void UpdateAttackRun()
    {
        // Continue forward along whatever heading we entered the run with.
        Vector3 step = transform.forward * cruiseSpeed * Time.deltaTime;
        transform.position += step;
        // Lock altitude — small drift can accumulate from rotation otherwise.
        Vector3 p = transform.position;
        p.y = flightAltitude;
        transform.position = p;

        // Missile release on the delay timer.
        fireDelayTimer -= Time.deltaTime;
        if (fireDelayTimer <= 0f && missilesFiredThisRun < maxAmmo)
        {
            // Target died between missiles — skip the second and exit early.
            if (target == null)
            {
                Debug.Log($"[Aircraft:{name}] Target lost during attack run — exiting.");
                BeginAttackEgress();
                return;
            }

            missilesFiredThisRun += 1;
            CurrentAmmo           = Mathf.Max(0, CurrentAmmo - 1);
            fireDelayTimer        = missileFireDelay;

            SpawnMissileAt(target);
            Debug.Log($"[Aircraft:{name}] Missile {missilesFiredThisRun} launched. " +
                      $"Ammo: {CurrentAmmo}/{maxAmmo}");

            // All missiles released — start the exit run.
            if (missilesFiredThisRun >= maxAmmo)
            {
                Debug.Log($"[Aircraft:{name}] Attack run complete — exiting");
                BeginAttackEgress();
            }
        }
    }

    /// <summary>
    /// After the last missile, keep flying straight for
    /// <see cref="attackEgressDistance"/> world units before any turn is
    /// allowed. Hands off to the WideReturnTurn arc — never directly to
    /// Returning — so the jet can never snap a 90° corner toward base.
    /// </summary>
    private void UpdateAttackEgress()
    {
        Vector3 step = transform.forward * cruiseSpeed * Time.deltaTime;
        transform.position += step;
        Vector3 p = transform.position;
        p.y = flightAltitude;
        transform.position = p;

        Vector3 travelXZ = transform.position - attackEgressStart;
        travelXZ.y = 0f;
        if (travelXZ.magnitude >= attackEgressDistance)
        {
            target = null;                  // mission over
            EnterWideReturnTurn();
        }
    }

    private void BeginAttackEgress()
    {
        attackEgressStart = transform.position;
        State             = FlightState.AttackEgress;
        Debug.Log($"[Aircraft:{name}] Entering egress phase.");
    }

    /// <summary>
    /// Smooth max-turn-rate arc back toward the home approach point. The jet
    /// keeps moving forward at cruise speed every frame and yaws by at most
    /// <see cref="maxTurnRateDegrees"/> deg/s toward the home direction.
    /// Hands off to <see cref="FlightState.Returning"/> once the heading is
    /// within <see cref="returnAlignmentAngle"/> of the home bearing.
    ///
    /// The arc shape emerges from speed × rate: at cruise 14 / rate 45 deg/s
    /// the radius is ~17.8 world units. A jet whose home is dead behind
    /// therefore flies a ~half-circle of that radius before lining up —
    /// a natural wide recovery, not a corner snap.
    /// </summary>
    private void UpdateWideReturnTurn()
    {
        if (homeSlot == null)
        {
            // Airfield destroyed while we were turning — give up and idle.
            Debug.LogWarning($"[Aircraft:{name}] Home slot lost mid-return — holding in air.");
            State = FlightState.Parked;
            return;
        }

        // Direction we WANT to face: home approach at altitude.
        Vector3 approach = GetApproachPointToAirfield();
        Vector3 toApproachXZ = approach - transform.position;
        toApproachXZ.y = 0f;

        Vector3 forwardXZ = transform.forward;
        forwardXZ.y = 0f;

        if (toApproachXZ.sqrMagnitude < 0.0001f || forwardXZ.sqrMagnitude < 0.0001f)
        {
            // Degenerate — already on top of home (rare). Just hand off.
            State = FlightState.Returning;
            return;
        }

        toApproachXZ.Normalize();
        forwardXZ.Normalize();

        float angle = Vector3.Angle(forwardXZ, toApproachXZ);

        // Aligned enough — exit the arc and let Returning fly straight home.
        if (angle <= returnAlignmentAngle)
        {
            Debug.Log($"[Aircraft:{name}] Return arc complete. Heading to base.");
            State = FlightState.Returning;
            return;
        }

        // Choose turn direction from cross-product sign. At 180° the cross
        // is ≈ 0; default to a right turn so the jet doesn't sit there.
        float turnSign = Mathf.Sign(Vector3.Cross(forwardXZ, toApproachXZ).y);
        if (Mathf.Abs(turnSign) < 0.001f) turnSign = 1f;

        // Clamp this frame's yaw delta to the max turn rate. This is what
        // produces the smooth arc — no instant snap.
        float yawDelta = Mathf.Min(angle, maxTurnRateDegrees * Time.deltaTime) * turnSign;
        transform.Rotate(0f, yawDelta, 0f, Space.World);

        // Move forward at cruise speed, then re-lock altitude.
        Vector3 step = transform.forward * cruiseSpeed * Time.deltaTime;
        transform.position += step;
        Vector3 p = transform.position;
        p.y = flightAltitude;
        transform.position = p;
    }

    // ------------------------------------------------------------------ //
    // Return-arc helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Enter <see cref="FlightState.WideReturnTurn"/>, logging whether the
    /// airfield is behind (wide arc) or to the side/front (smaller arc).
    /// Called by both AttackEgress and PatrollingPoint completion.
    /// </summary>
    private void EnterWideReturnTurn()
    {
        State = FlightState.WideReturnTurn;

        if (homeSlot == null) return;

        if (IsAirfieldBehindCurrentHeading())
            Debug.Log($"[Aircraft:{name}] Airfield is behind aircraft — using wide return arc.");
        else
            Debug.Log($"[Aircraft:{name}] Airfield is ahead/side — using moderate return curve.");
    }

    /// <summary>
    /// True when the XZ direction from the aircraft to the home approach
    /// point is more than 90° away from the aircraft's current forward.
    /// (dot &lt; 0 ⇒ behind.) Used only for log selection — the turn rule
    /// itself is the same in either case; the wide arc emerges from the
    /// constant turn radius and 180° of rotation work.
    /// </summary>
    private bool IsAirfieldBehindCurrentHeading()
    {
        if (homeSlot == null) return false;

        Vector3 forwardXZ = transform.forward; forwardXZ.y = 0f;
        Vector3 toHomeXZ  = homeSlot.position - transform.position; toHomeXZ.y = 0f;

        if (forwardXZ.sqrMagnitude < 0.0001f || toHomeXZ.sqrMagnitude < 0.0001f)
            return false;

        return Vector3.Dot(forwardXZ.normalized, toHomeXZ.normalized) < 0f;
    }

    /// <summary>
    /// World-space approach waypoint: home slot's XZ at flight altitude.
    /// Returning then descends from this point during Landing.
    /// </summary>
    private Vector3 GetApproachPointToAirfield()
    {
        if (homeSlot == null) return transform.position;
        return new Vector3(homeSlot.position.x, flightAltitude, homeSlot.position.z);
    }

    /// <summary>
    /// Spawns one StrikeMissile projectile at <see cref="firePoint"/>, aimed
    /// at the target's current chest height. The missile manages its own
    /// flight, damage application, and impact flash.
    /// </summary>
    private void SpawnMissileAt(Health enemy)
    {
        Vector3 start = (firePoint != null)
            ? firePoint.position
            : transform.position + Vector3.down * 0.3f;

        // Build the visual body — a long thin cube along +Z so LookRotation
        // on the missile points its long axis along its travel vector.
        GameObject mGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
        mGO.name                    = "StrikeMissile";
        mGO.transform.position      = start;
        mGO.transform.localScale    = new Vector3(0.20f, 0.20f, 1.00f);
        mGO.layer                   = 2;   // IgnoreRaycast

        // Strip the auto-collider so the missile can't catch raycasts.
        Collider col = mGO.GetComponent<Collider>();
        if (col != null) Destroy(col);

        Renderer r = mGO.GetComponent<Renderer>();
        if (r != null)
        {
            Shader shader = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Standard");
            Material m = new Material(shader) { color = missileColor };
            if (m.HasProperty("_BaseColor"))
                m.SetColor("_BaseColor", missileColor);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", missileColor * 1.4f);
            }
            r.sharedMaterial    = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows    = false;
        }

        StrikeMissile missile = mGO.AddComponent<StrikeMissile>();
        missile.Launch(start, enemy, missileDamage, damageType,
                       missileProjectileSpeed, impactFlashDuration, missileColor);
    }

    /// <summary>
    /// Cruise toward <see cref="patrolCenter"/> at altitude. Hands off to
    /// <see cref="UpdatePatrollingPoint"/> once we enter the circle.
    /// </summary>
    private void UpdateFlyingToPoint()
    {
        Vector3 wantedXZ = new Vector3(patrolCenter.x, flightAltitude, patrolCenter.z);
        Vector3 dir      = wantedXZ - transform.position;
        float   distXZ   = new Vector2(dir.x, dir.z).magnitude;

        // Within the patrol radius — start circling. Initialise the current
        // angle from our actual position so the first lap is smooth, not a
        // sudden teleport onto the rim.
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

        Vector3 step = dir.normalized * cruiseSpeed * Time.deltaTime;
        transform.position += step;
        FaceVelocity(step);
    }

    /// <summary>
    /// Fly a circular pattern around <see cref="patrolCenter"/> at altitude.
    /// Counts laps and returns home after <see cref="patrolCircleCount"/>.
    /// </summary>
    private void UpdatePatrollingPoint()
    {
        // Advance the angle. Counter-clockwise (positive) by math convention;
        // the tangent at angle θ is (-sin θ, cos θ), used for facing direction.
        float deltaAngle = patrolAngularSpeed * Time.deltaTime;
        patrolCurrentAngle += deltaAngle;
        patrolAngleSwept   += deltaAngle;

        float rad           = patrolCurrentAngle * Mathf.Deg2Rad;
        Vector3 centreAtAlt = new Vector3(patrolCenter.x, flightAltitude, patrolCenter.z);
        Vector3 offset      = new Vector3(Mathf.Cos(rad) * patrolCircleRadius,
                                          0f,
                                          Mathf.Sin(rad) * patrolCircleRadius);
        Vector3 wantedPos   = centreAtAlt + offset;

        // Glide toward the next point on the circle at cruise speed. Using
        // MoveTowards (not direct teleport) keeps the path smooth if the
        // aircraft drifts inward from a high angular speed setting.
        transform.position = Vector3.MoveTowards(
            transform.position, wantedPos, cruiseSpeed * Time.deltaTime);

        Vector3 tangent = new Vector3(-Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        if (tangent.sqrMagnitude > 0.0001f)
        {
            Quaternion want = Quaternion.LookRotation(tangent);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, want, turnSpeed * Time.deltaTime);
        }

        // Lap-completion logs — emit once per crossed 360-degree boundary.
        int completedCircles = Mathf.FloorToInt(patrolAngleSwept / 360f);
        if (completedCircles > lastReportedCircle)
        {
            lastReportedCircle = completedCircles;
            Debug.Log($"[Aircraft:{name}] Patrol circle {completedCircles}/{patrolCircleCount}");
        }

        // Mission complete — go home via the wide arc so the jet doesn't
        // snap-corner out of its circle tangent into a straight return.
        if (patrolAngleSwept >= 360f * patrolCircleCount)
        {
            Debug.Log($"[Aircraft:{name}] Patrol complete — returning");
            hasPatrolMission = false;
            EnterWideReturnTurn();
        }
    }

    /// <summary>
    /// Returning to base — fly toward the airfield's <see cref="Airfield.landingApproachPoint"/>
    /// at altitude. Once close enough, request a landing clearance; the response
    /// decides whether we proceed to <see cref="FlightState.LandingApproach"/>
    /// or hold in <see cref="FlightState.WaitingForLandingClearance"/>.
    /// </summary>
    private void UpdateReturning()
    {
        if (homeSlot == null)
        {
            Debug.LogWarning($"[Aircraft:{name}] Home slot lost — holding in air.");
            State = FlightState.Parked;
            return;
        }

        Transform approach = HomeAirfield != null ? HomeAirfield.landingApproachPoint : null;
        // Fallback: no approach marker → aim at the home slot directly, but
        // we'll still go through the new state pipeline.
        Vector3 wantedXZ = approach != null
            ? new Vector3(approach.position.x, flightAltitude, approach.position.z)
            : new Vector3(homeSlot.position.x, flightAltitude, homeSlot.position.z);

        Vector3 dir   = wantedXZ - transform.position;
        float   distXZ = new Vector2(dir.x, dir.z).magnitude;

        if (distXZ <= landingApproachDistance)
        {
            // Arrived at the approach point — ask the tower for a slot.
            Debug.Log($"[Aircraft:{name}] Returning to Airfield approach.");
            TryRequestLandingClearance();
            return;
        }

        Vector3 step = dir.normalized * cruiseSpeed * Time.deltaTime;
        transform.position += step;
        FaceVelocity(step);
    }

    /// <summary>
    /// Asks <see cref="HomeAirfield"/> for landing clearance. If granted we
    /// proceed straight to <see cref="FlightState.LandingApproach"/>; if
    /// denied we drop into <see cref="FlightState.WaitingForLandingClearance"/>
    /// (holding pattern) and retry on a timer.
    /// </summary>
    private void TryRequestLandingClearance()
    {
        if (HomeAirfield == null)
        {
            // No airfield — fall through to the legacy descent-to-slot path
            // so the jet doesn't get stuck in the air.
            landingClearance = null;
            State = FlightState.FinalLanding;
            finalLandingStartXZ = transform.position;
            return;
        }

        if (HomeAirfield.RequestLandingClearance(this, out Airfield.LandingClearance c))
        {
            landingClearance = c;
            State            = FlightState.LandingApproach;
            holdingPatternAngle = 0f;
        }
        else
        {
            State = FlightState.WaitingForLandingClearance;
            landingClearanceRetryTimer = landingClearanceCheckInterval;
            // Seed the holding-pattern angle from current relative position so
            // the first lap starts smoothly.
            Transform approach = HomeAirfield.landingApproachPoint;
            if (approach != null)
            {
                Vector3 fromCenter = transform.position - approach.position;
                holdingPatternAngle = Mathf.Atan2(fromCenter.z, fromCenter.x) * Mathf.Rad2Deg;
            }
            Debug.Log($"[Aircraft:{name}] Waiting for landing clearance.");
        }
    }

    /// <summary>
    /// Holding pattern — circle around <see cref="Airfield.landingApproachPoint"/>
    /// at flight altitude. Periodically re-checks clearance.
    /// </summary>
    private void UpdateWaitingForLandingClearance()
    {
        if (HomeAirfield == null)
        {
            State = FlightState.FinalLanding;
            finalLandingStartXZ = transform.position;
            return;
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

        // Mirror PatrollingPoint's circle math at the approach point.
        float deltaAngle = patrolAngularSpeed * Time.deltaTime;
        holdingPatternAngle += deltaAngle;

        float rad           = holdingPatternAngle * Mathf.Deg2Rad;
        Vector3 centreAtAlt = new Vector3(approach.position.x, flightAltitude, approach.position.z);
        Vector3 offset      = new Vector3(Mathf.Cos(rad) * landingHoldingRadius,
                                          0f,
                                          Mathf.Sin(rad) * landingHoldingRadius);
        Vector3 wantedPos   = centreAtAlt + offset;

        transform.position = Vector3.MoveTowards(
            transform.position, wantedPos, cruiseSpeed * Time.deltaTime);

        Vector3 tangent = new Vector3(-Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        if (tangent.sqrMagnitude > 0.0001f)
        {
            Quaternion want = Quaternion.LookRotation(tangent);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, want, turnSpeed * Time.deltaTime);
        }
    }

    /// <summary>
    /// Lined up — fly from current position to <see cref="Airfield.LandingClearance.LandingStart"/>
    /// at altitude. When close enough, hand off to FinalLanding.
    /// </summary>
    private void UpdateLandingApproach()
    {
        Transform start = landingClearance?.LandingStart;
        if (start == null)
        {
            // No landing-start marker — go straight to FinalLanding so we
            // don't strand the jet.
            finalLandingStartXZ = transform.position;
            State = FlightState.FinalLanding;
            Debug.Log($"[Aircraft:{name}] Final landing started.");
            return;
        }

        Vector3 wantedXZ = new Vector3(start.position.x, flightAltitude, start.position.z);
        Vector3 dir      = wantedXZ - transform.position;
        float   distXZ   = new Vector2(dir.x, dir.z).magnitude;

        if (distXZ <= landingArrivalThreshold)
        {
            finalLandingStartXZ = transform.position;
            State = FlightState.FinalLanding;
            Debug.Log($"[Aircraft:{name}] Final landing started.");
            return;
        }

        Vector3 step = dir.normalized * cruiseSpeed * Time.deltaTime;
        transform.position += step;
        FaceVelocity(step);
    }

    /// <summary>
    /// Glide down the runway from LandingStart to LandingEnd. Y descends
    /// linearly with XZ progress so the touchdown happens at LandingEnd
    /// regardless of runway length.
    /// </summary>
    private void UpdateFinalLanding()
    {
        Transform end = landingClearance?.LandingEnd;
        if (end == null)
        {
            // No end marker — drop straight down to ground and continue.
            Vector3 fallback = transform.position;
            fallback.y = GetGroundY();
            transform.position = fallback;
            EnterTaxiingToSlot();
            return;
        }

        // XZ motion toward LandingEnd at landingRollSpeed.
        Vector3 endXZ = new Vector3(end.position.x, transform.position.y, end.position.z);
        Vector3 dir   = endXZ - transform.position;
        Vector2 distXZ = new Vector2(dir.x, dir.z);

        Vector3 step = (distXZ.sqrMagnitude > 0.0001f
            ? new Vector3(dir.x, 0f, dir.z).normalized * landingRollSpeed * Time.deltaTime
            : Vector3.zero);
        transform.position += step;

        // Altitude descent — linear by XZ-progress along the path.
        float totalDist = Vector2.Distance(
            new Vector2(finalLandingStartXZ.x, finalLandingStartXZ.z),
            new Vector2(end.position.x,        end.position.z));
        float coveredDist = Vector2.Distance(
            new Vector2(finalLandingStartXZ.x, finalLandingStartXZ.z),
            new Vector2(transform.position.x,  transform.position.z));
        float t = totalDist > 0.01f ? Mathf.Clamp01(coveredDist / totalDist) : 1f;

        float groundY    = GetGroundY();
        float targetY    = Mathf.Lerp(flightAltitude, groundY, t);
        // Also clamp by descent rate so very-short runways don't divebomb.
        targetY = Mathf.MoveTowards(transform.position.y, targetY,
                                    landingDescentSpeed * Time.deltaTime);

        Vector3 pos = transform.position;
        pos.y = targetY;
        transform.position = pos;

        // Face the landing direction.
        Vector3 face = new Vector3(dir.x, 0f, dir.z);
        if (face.sqrMagnitude > 0.0001f)
        {
            Quaternion want = Quaternion.LookRotation(face);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, want, turnSpeed * Time.deltaTime);
        }

        // Touchdown — XZ within threshold AND altitude on the ground.
        if (distXZ.magnitude <= landingArrivalThreshold && targetY <= groundY + 0.01f)
        {
            Debug.Log($"[Aircraft:{name}] Touchdown.");
            EnterTaxiingToSlot();
        }
    }

    /// <summary>
    /// Switch from FinalLanding to TaxiingToSlot. Releases the shared runway
    /// lock so other aircraft can use it.
    /// </summary>
    private void EnterTaxiingToSlot()
    {
        if (HomeAirfield != null)
            HomeAirfield.ReleaseLandingRunway(this);

        taxiBackRoute = landingClearance?.TaxiBackRoute ?? new Transform[0];
        taxiBackIndex = 0;
        landingClearance = null;

        // Snap altitude to the ground so the rest of the taxi runs flat.
        Vector3 pos = transform.position;
        pos.y = GetGroundY();
        transform.position = pos;

        State = FlightState.TaxiingToSlot;
        Debug.Log($"[Aircraft:{name}] Taxiing back to slot.");
    }

    /// <summary>
    /// Taxi the aircraft along <see cref="taxiBackRoute"/> (LandingExit →
    /// per-slot taxi point → slot) at <see cref="taxiBackSpeed"/>. On
    /// arrival at the final waypoint the jet is snapped to the slot and
    /// flagged Parked so the reload timer can start.
    /// </summary>
    private void UpdateTaxiingToSlot()
    {
        if (taxiBackRoute == null || taxiBackIndex >= taxiBackRoute.Length)
        {
            ParkAtHomeSlot();
            return;
        }

        Transform wp = taxiBackRoute[taxiBackIndex];
        if (wp == null) { taxiBackIndex++; return; }

        float groundY = GetGroundY();
        Vector3 target = wp.position;
        target.y = groundY;

        Vector3 dir   = target - transform.position;
        Vector2 distXZ = new Vector2(dir.x, dir.z);

        if (distXZ.magnitude <= landingArrivalThreshold)
        {
            taxiBackIndex++;
            if (taxiBackIndex >= taxiBackRoute.Length)
                ParkAtHomeSlot();
            return;
        }

        Vector3 step = dir.normalized * taxiBackSpeed * Time.deltaTime;
        if (step.magnitude > dir.magnitude) step = dir;
        transform.position += step;

        Vector3 pos = transform.position;
        pos.y = groundY;
        transform.position = pos;

        FaceVelocity(step);
    }

    /// <summary>Final snap to the home slot, reset reload, mark as parked.</summary>
    private void ParkAtHomeSlot()
    {
        if (homeSlot != null)
        {
            transform.position = homeSlot.position;
            transform.rotation = homeSlot.rotation;
        }
        target           = null;
        reloadTimer      = 0f;
        taxiBackRoute    = null;
        taxiBackIndex    = 0;
        State            = FlightState.Parked;
        Debug.Log($"[Aircraft:{name}] Parked at original slot. Ammo: {CurrentAmmo}/{maxAmmo} — reloading.");
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private void FaceVelocity(Vector3 step)
    {
        Vector3 flat = new Vector3(step.x, 0f, step.z);
        if (flat.sqrMagnitude < 0.0001f) return;

        Quaternion want = Quaternion.LookRotation(flat);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turnSpeed * Time.deltaTime);
    }

}
