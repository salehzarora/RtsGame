using UnityEngine;

/// <summary>
/// Guard-radius auto-attack for any ground combat unit. The unit defends a
/// stable <see cref="guardPosition"/> (initialised on Awake, updated only on
/// manual move orders) — NOT its current chased position. This prevents an
/// enemy from luring the unit across the whole map by skirting the edge of
/// the detection circle.
///
/// State machine:
///   • Guarding          — idle at/near guard. Periodically scans an
///                          OverlapSphere centred on <see cref="guardPosition"/>
///                          for hostile targets.
///   • AutoChasing       — engaging an auto-acquired target. Each frame
///                          checks the target's distance from guardPosition;
///                          if it exceeds <see cref="leashRadius"/> the unit
///                          drops the target and returns home.
///   • ReturningToGuard  — moving back to guardPosition via UnitMovement.
///                          Scans are suppressed so the unit doesn't get
///                          re-yanked into another chase mid-return.
///   • ManualCommand     — player issued a right-click attack. Sticky target;
///                          all auto logic suspended until combat goes Idle.
///                          On end, transitions to ReturningToGuard if the
///                          unit ended up far from home.
///
/// Manual move (right-click ground) updates <see cref="guardPosition"/> to
/// the new destination when <see cref="updateGuardPositionOnManualMove"/> is
/// true, so the unit henceforth defends THAT spot.
///
/// Setup:
///   1. Add via Tools → RTS → Units → Add Ground Auto Attack To Prefabs.
///   2. Tune detection / leash / return threshold in the Inspector.
///   3. Aircraft self-disable on Awake — this is a ground-only system; the
///      <see cref="AirUnitController"/> drives air targeting independently.
///
/// What it does NOT do:
///   • Pathfinding strategy. Combat components own the chase within their
///     own attack range; this controller only picks the target.
///   • Override manual commands — those are sticky until combat clears.
///   • Re-engage Aircraft from non-anti-air weapons (Bullet / Cannon).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Health))]
public class GroundAutoAttackController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    private enum AutoState
    {
        Guarding,
        AutoChasing,
        LookingForNextTarget,   // brief continuation scan after a target dies before returning
        ReturningToGuard,
        ManualCommand,
    }

    // ------------------------------------------------------------------ //
    // Inspector — Detection
    // ------------------------------------------------------------------ //

    [Header("Detection")]
    [Tooltip("Master switch — uncheck to make the unit purely manual.")]
    public bool autoAttackEnabled = true;

    [Tooltip("XZ radius (world units) inside which a hostile target around the GUARD POSITION " +
             "triggers an auto-acquire. Stable: this radius is centred on guardPosition, not the " +
             "unit's chased position.")]
    public float detectionRadius = 12f;

    [Tooltip("Hard outer limit. An auto-acquired target outside this radius from guardPosition " +
             "is dropped and the unit returns home. Must be ≥ detectionRadius — a small gap " +
             "(e.g. 12 / 20) gives the unit room to chase a fleeing enemy a short distance.")]
    public float leashRadius = 20f;

    [Tooltip("Small grace distance added on top of leashRadius before a chase is " +
             "actually dropped. Prevents flicker when a target sits exactly on the " +
             "leash boundary. Also used as the radius for continuation-scan after a kill.")]
    public float leashTolerance = 2f;

    [Tooltip("After a target dies, the unit re-scans for hostiles near its current " +
             "fight position before returning to guard. Targets within this radius of " +
             "either the unit OR the last fight position count as 'nearby' and are " +
             "engaged even if they sit just outside the standard detection radius. " +
             "Stops a unit from walking past a second enemy 1-2 m away to return home.")]
    public float continueFightRadius = 7f;

    [Tooltip("Seconds between re-scans. Smaller = more responsive, larger = cheaper.")]
    public float scanInterval = 0.35f;

    [Tooltip("Layers to scan for targets. Default leaves this empty so the controller " +
             "auto-resolves Unit + Building on Awake. Override for non-standard scenes.")]
    public LayerMask targetLayerMask;

    [Tooltip("Draw the detection + leash circles in the Scene view when this unit is selected. " +
             "Editor only — no runtime cost.")]
    public bool debugDrawRadius = false;

    // ------------------------------------------------------------------ //
    // Inspector — Guard + Return
    // ------------------------------------------------------------------ //

    [Header("Guard / Return")]
    [Tooltip("If true, the unit walks back to guardPosition after a chase / manual command ends. " +
             "If false, it stays wherever combat finished and resumes guarding from there.")]
    public bool returnToGuardAfterChase = true;

    [Tooltip("XZ distance (world units) below which the unit is considered 'home' and " +
             "transitions from ReturningToGuard back to Guarding.")]
    public float returnToGuardThreshold = 1.5f;

    [Tooltip("Seconds the unit spends scanning for a nearby follow-up target after " +
             "a kill before committing to a return-to-guard. Small (0.25-0.5) is " +
             "enough — long enough to absorb a one-frame combat-idle blip, short " +
             "enough that lone kills don't visibly stall the unit.")]
    public float returnToGuardDelay = 0.35f;

    [Tooltip("If true, NotifyManualMove updates guardPosition to the move destination. " +
             "If false, the guard position is locked at the spawn point and the unit " +
             "always returns to its original spot after manual moves end.")]
    public bool updateGuardPositionOnManualMove = true;

    // ------------------------------------------------------------------ //
    // Inspector — Manual command suppression
    // ------------------------------------------------------------------ //

    [Header("Manual Command Handling")]
    [Tooltip("Seconds the auto-scan is paused after the player issues a manual move " +
             "(right-click ground). Prevents the unit from instantly re-engaging and " +
             "cancelling the move order.")]
    public float autoAttackPausedAfterManualMoveSeconds = 1.0f;

    // ------------------------------------------------------------------ //
    // Inspector — Fire While Moving (turret vehicles)
    // ------------------------------------------------------------------ //

    [Header("Fire While Moving (turret vehicles)")]
    [Tooltip("If true, the unit keeps scanning + engaging targets while its body " +
             "is moving toward a manual destination. The scan centre switches from " +
             "guardPosition to the unit's current position so en-route hostiles are " +
             "actually seen. Auto-acquired targets are NOT chased — the body keeps " +
             "moving toward its original destination; only the turret engages. " +
             "Default false so infantry behaves unchanged.")]
    public bool autoFireWhileMoving = false;

    [Tooltip("Lose multiplier for in-transit targets. Target is dropped when " +
             "distance from the unit exceeds attackRange × this. 1.15 gives a small " +
             "grace window so a single laggy frame doesn't cancel an engagement.")]
    public float transitTargetLoseMultiplier = 1.15f;

    // ------------------------------------------------------------------ //
    // Public runtime state
    // ------------------------------------------------------------------ //

    /// <summary>The world position the unit defends. Updated only by NotifyManualMove
    /// (and only when updateGuardPositionOnManualMove is true). NOT moved by chase.</summary>
    public Vector3 GuardPosition => guardPosition;

    public bool HasGuardPosition => hasGuardPosition;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private Health        ownHealth;
    private UnitCombat            unitCombat;     // null on RPG / missile launcher units
    private RocketCombat          rocketCombat;   // null on rifle / cannon units
    private MissileLauncherCombat missileCombat;  // null on all but the Missile Launcher
    private UnitMovement  movement;       // used for ReturningToGuard MoveTo
    private UnityEngine.AI.NavMeshAgent agent;  // for transit-fire motion check

    // True while the unit is in the transit-fire branch (autoFireWhileMoving +
    // body actually moving). Tracked so we can log "Suppression ignored…" once
    // per entry instead of every frame.
    private bool transitLoggedSuppression;

    private AutoState state = AutoState.Guarding;
    private Vector3   guardPosition;
    private bool      hasGuardPosition;

    // The auto-acquired target, if any. Null in Guarding / ReturningToGuard / ManualCommand.
    private Health currentAutoTarget;

    // Time after which auto-scan may resume (post-manual-move suppression).
    private float scanResumeTime;
    private float scanTimer;

    // Continuation-scan bookkeeping. lastFightPosition is captured when the
    // current target dies so the next-target scan can prefer enemies near
    // where the fight just happened. lookForNextDeadline is the absolute time
    // at which the continuation gives up and we head back to guard.
    private Vector3 lastFightPosition;
    private float   lookForNextDeadline;

    // OverlapSphere buffer — sized for typical scenes; overflow truncates safely.
    private readonly Collider[] scanBuffer = new Collider[32];

    private bool noCombatWarned;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        ownHealth    = GetComponent<Health>();
        unitCombat    = GetComponent<UnitCombat>();
        rocketCombat  = GetComponent<RocketCombat>();
        missileCombat = GetComponent<MissileLauncherCombat>();
        movement     = GetComponent<UnitMovement>();
        agent        = GetComponent<UnityEngine.AI.NavMeshAgent>();

        // Aircraft self-exclusion. Spec: this is a GROUND auto-attack system;
        // air units have their own targeting via AirUnitController.
        UnitCategory cat = GetComponent<UnitCategory>();
        if (cat != null && cat.category == UnitCategory.Category.Aircraft)
        {
            Debug.Log($"[AutoAttack:{name}] Disabled on aircraft.");
            enabled = false;
            return;
        }

        if (ownHealth == null)
        {
            Debug.LogError($"[AutoAttack:{name}] No Health on this GameObject — disabling.");
            enabled = false;
            return;
        }

        // Resolve target mask if not set in Inspector. Unit + Building covers
        // every standard target category in this project.
        if (targetLayerMask.value == 0)
        {
            int unitLayer     = LayerMask.NameToLayer("Unit");
            int buildingLayer = LayerMask.NameToLayer("Building");
            int mask = 0;
            if (unitLayer     >= 0) mask |= 1 << unitLayer;
            if (buildingLayer >= 0) mask |= 1 << buildingLayer;
            targetLayerMask = mask != 0 ? mask : ~0;
        }

        // Initial guard position is the spawn point.
        guardPosition    = transform.position;
        hasGuardPosition = true;
        Debug.Log($"[AutoAttack:{name}] Guard position set.");

        // Stagger first scan so many units placed at once don't all
        // OverlapSphere on the same frame.
        scanTimer = Random.Range(0f, scanInterval);
    }

    private void Update()
    {
        if (!autoAttackEnabled) return;
        if (ownHealth == null) return;

        // Transit-fire: turret vehicles with autoFireWhileMoving keep scanning
        // while the body is en route to a manual destination. Scan centre
        // becomes the vehicle's current position, NOT the (possibly distant)
        // guard position. Manual-attack state is left alone so the player's
        // explicit target doesn't get overridden.
        if (IsInTransitFire())
        {
            // Ignore the post-manual-move suppression timer — that timer is
            // for stationary guards. A turret vehicle should fire en route.
            if (Time.time < scanResumeTime)
            {
                if (!transitLoggedSuppression)
                {
                    Debug.Log($"[AutoAttack:{name}] Suppression ignored for vehicle fire-while-moving.");
                    transitLoggedSuppression = true;
                }
                scanResumeTime = 0f;
            }

            UpdateTransitFire();
            return;
        }
        transitLoggedSuppression = false;   // re-arm so next entry logs once

        // Manual-move suppression — applies to non-transit-fire units (infantry, etc.).
        if (Time.time < scanResumeTime) return;

        switch (state)
        {
            case AutoState.Guarding:             UpdateGuarding();             break;
            case AutoState.AutoChasing:          UpdateAutoChasing();          break;
            case AutoState.LookingForNextTarget: UpdateLookingForNextTarget(); break;
            case AutoState.ReturningToGuard:     UpdateReturningToGuard();     break;
            case AutoState.ManualCommand:        UpdateManualCommand();        break;
        }
    }

    /// <summary>
    /// True when the unit should currently run the transit-fire branch:
    /// <see cref="autoFireWhileMoving"/> is on, the NavMeshAgent has measurable
    /// velocity, and we're not in a manual-attack engagement (manual fire
    /// uses the standard chase + leash flow).
    /// </summary>
    private bool IsInTransitFire()
    {
        if (!autoFireWhileMoving) return false;
        if (state == AutoState.ManualCommand) return false;
        if (agent == null) return false;

        // Reuse the existing movement threshold concept (0.2 u/s) — matches
        // UnitCombat's isMoving check so the two stay consistent.
        const float MovingThresholdSqr = 0.2f * 0.2f;
        return agent.velocity.sqrMagnitude > MovingThresholdSqr;
    }

    /// <summary>
    /// Transit-fire path. The vehicle is moving toward a manual destination;
    /// the body is not stopped, never chases, and engages opportunistically
    /// only when a hostile sits inside the actual weapon range right now.
    /// </summary>
    private void UpdateTransitFire()
    {
        // Step 1 — validate the current target.
        if (currentAutoTarget != null)
        {
            bool stillEngageable = !CombatIsIdle();

            if (stillEngageable)
            {
                float dist  = Vector3.Distance(currentAutoTarget.transform.position, transform.position);
                float range = GetWeaponRangeFor(currentAutoTarget);
                if (dist > range * transitTargetLoseMultiplier)
                    stillEngageable = false;
            }

            if (!stillEngageable)
            {
                Debug.Log($"[VehicleAutoFire:{name}] Target lost — continuing route.");
                ClearAutoTargetOnCombat();
                currentAutoTarget = null;
            }
        }

        // Step 2 — if we already have a target, keep firing; no rescan needed.
        if (currentAutoTarget != null) return;

        // Step 3 — periodic re-scan from the unit's current position.
        scanTimer -= Time.deltaTime;
        if (scanTimer > 0f) return;
        scanTimer = scanInterval;

        if (TryAcquireTransitTarget(out Health next))
        {
            Debug.Log($"[VehicleAutoFire:{name}] Acquired target while moving: {next.name}.");

            currentAutoTarget = next;
            state             = AutoState.AutoChasing;   // for state-machine compat when we stop

            if (unitCombat    != null) unitCombat.SetTarget(next);
            if (rocketCombat  != null) rocketCombat.SetTarget(next);
            if (missileCombat != null) missileCombat.SetTarget(next);

            Debug.Log($"[VehicleAutoFire:{name}] Firing while continuing move order.");
        }
    }

    /// <summary>
    /// Scans from the unit's current position and returns the closest hostile
    /// already inside our actual weapon range. We deliberately exclude targets
    /// that are visible (detectionRadius) but out of range — engaging them
    /// would trigger UnitCombat's chase path and cancel the move order.
    /// </summary>
    private bool TryAcquireTransitTarget(out Health best)
    {
        best = null;
        if (unitCombat == null && rocketCombat == null && missileCombat == null) return false;

        float bestDist = float.PositiveInfinity;

        // Scan with the larger of detection / anti-air range so air targets
        // close enough to fire on still surface. Per-candidate filter below
        // narrows to the actual weapon range for the target's category.
        float scanRadius = detectionRadius;
        if (rocketCombat != null) scanRadius = Mathf.Max(scanRadius, rocketCombat.antiAirRange);

        int count = Physics.OverlapSphereNonAlloc(
            transform.position, scanRadius, scanBuffer, targetLayerMask.value);

        Health.Team hostileTeam = (ownHealth.team == Health.Team.Player)
            ? Health.Team.Enemy
            : Health.Team.Player;

        for (int i = 0; i < count; i++)
        {
            Collider c = scanBuffer[i];
            if (c == null) continue;

            Health h = c.GetComponent<Health>() ?? c.GetComponentInParent<Health>();
            if (h == null || h == ownHealth) continue;
            if (h.team != hostileTeam) continue;

            UnitCategory.Category cat = DamageRules.Resolve(h.gameObject);
            if (!CanEngageCategory(cat)) continue;

            float distFromUnit = Vector3.Distance(h.transform.position, transform.position);
            float weaponRange  = GetWeaponRangeFor(h);
            if (distFromUnit > weaponRange) continue;     // outside actual weapon range — would trigger chase

            if (distFromUnit < bestDist)
            {
                bestDist = distFromUnit;
                best     = h;
            }
        }

        return best != null;
    }

    /// <summary>
    /// Resolves the right attack-range field for <paramref name="target"/>:
    /// RocketCombat exposes a separate antiAirRange for Aircraft, UnitCombat
    /// uses one attackRange for everything.
    /// </summary>
    private float GetWeaponRangeFor(Health target)
    {
        if (target == null) return 0f;
        UnitCategory.Category cat = DamageRules.Resolve(target.gameObject);

        if (rocketCombat != null)
            return cat == UnitCategory.Category.Aircraft ? rocketCombat.antiAirRange : rocketCombat.attackRange;

        if (missileCombat != null) return missileCombat.attackRange;
        if (unitCombat    != null) return unitCombat.attackRange;
        return 0f;
    }

    // ------------------------------------------------------------------ //
    // State updates
    // ------------------------------------------------------------------ //

    private void UpdateGuarding()
    {
        scanTimer -= Time.deltaTime;
        if (scanTimer > 0f) return;
        scanTimer = scanInterval;

        TryAcquireTarget();
    }

    private void UpdateAutoChasing()
    {
        // Combat went idle on its own (target died / cleared). Snapshot the
        // last fight position, then briefly look for another nearby enemy
        // BEFORE committing to a return-to-guard. Otherwise a unit will walk
        // past a second hostile sitting 1-2 m away.
        if (currentAutoTarget == null || CombatIsIdle())
        {
            // Use the live target position if we still have a reference,
            // otherwise our own position as a fallback anchor.
            lastFightPosition = currentAutoTarget != null
                ? currentAutoTarget.transform.position
                : transform.position;
            currentAutoTarget = null;
            EnterLookingForNextTarget();
            return;
        }

        // Leash check — the hard limit on how far an enemy can pull us. Apply
        // the small tolerance so a target jittering exactly on the boundary
        // doesn't yank us back. Run every frame so a fleeing target is
        // dropped immediately, not on the next 0.35 s scan tick.
        float distFromGuard = Vector3.Distance(
            currentAutoTarget.transform.position, guardPosition);

        if (distFromGuard > leashRadius + leashTolerance)
        {
            Debug.Log($"[AutoAttack:{name}] Target left leash range — returning to guard.");
            ClearAutoTargetOnCombat();
            lastFightPosition = currentAutoTarget.transform.position;
            currentAutoTarget = null;
            EnterReturningToGuard();
        }
    }

    /// <summary>
    /// Brief scan window between a kill and the return-to-guard walk. Looks
    /// for a follow-up target near the last fight position (or near the unit)
    /// that's still inside the guard's leash boundary. If found, jump straight
    /// back into AutoChasing; if the deadline passes with nothing eligible,
    /// commit to ReturningToGuard.
    /// </summary>
    private void UpdateLookingForNextTarget()
    {
        // Scan every tick — this is a short window (≤ returnToGuardDelay) so
        // the cost is one OverlapSphere per frame for at most ~half a second.
        if (TryAcquireContinuationTarget(out Health next))
        {
            Debug.Log($"[AutoAttack:{name}] Continuing fight with nearby target: {next.name}.");
            AssignAutoTarget(next);   // transitions back to AutoChasing
            return;
        }

        if (Time.time >= lookForNextDeadline)
        {
            Debug.Log($"[AutoAttack:{name}] No nearby enemies — returning to guard.");
            EnterReturningToGuard();
        }
    }

    private void UpdateReturningToGuard()
    {
        // Periodically re-issue the move command so a transient NavMesh hiccup
        // doesn't strand the unit. Cheap — UnitMovement.MoveTo just calls
        // NavMeshAgent.SetDestination which no-ops on a duplicate destination.
        scanTimer -= Time.deltaTime;
        if (scanTimer <= 0f)
        {
            scanTimer = scanInterval;
            if (movement != null) movement.MoveTo(guardPosition);
        }

        if (Vector3.Distance(transform.position, guardPosition) <= returnToGuardThreshold)
        {
            Debug.Log($"[AutoAttack:{name}] Returned to guard position.");
            if (movement != null) movement.Stop();
            state = AutoState.Guarding;
            scanTimer = 0f;     // scan immediately on next tick
        }
    }

    private void UpdateManualCommand()
    {
        // Manual commands are sticky until combat finishes with them. Once
        // combat goes idle (target died / out of reach), transition out.
        if (CombatIsIdle())
        {
            EnterPostEngagement();
        }
    }

    // ------------------------------------------------------------------ //
    // Transitions
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Called when a chase / manual command ends naturally. If we wandered far
    /// from the guard, head back; otherwise just resume scanning where we are.
    /// </summary>
    private void EnterPostEngagement()
    {
        currentAutoTarget = null;

        if (returnToGuardAfterChase &&
            Vector3.Distance(transform.position, guardPosition) > returnToGuardThreshold)
        {
            EnterReturningToGuard();
        }
        else
        {
            state = AutoState.Guarding;
        }
    }

    private void EnterReturningToGuard()
    {
        state = AutoState.ReturningToGuard;
        if (movement != null) movement.MoveTo(guardPosition);
        scanTimer = scanInterval;   // re-issue MoveTo every scanInterval as a safety pulse
    }

    /// <summary>
    /// Enter the brief look-around window after a kill. The deadline gives
    /// the unit ~returnToGuardDelay seconds to find a follow-up target before
    /// it gives up and walks home.
    /// </summary>
    private void EnterLookingForNextTarget()
    {
        state               = AutoState.LookingForNextTarget;
        lookForNextDeadline = Time.time + Mathf.Max(0f, returnToGuardDelay);

        Debug.Log($"[AutoAttack:{name}] Target down — scanning for nearby enemies.");

        // Body should hold its current position during the look-around so it
        // doesn't drift forward / backward while we decide.
        if (movement != null) movement.Stop();
    }

    // ------------------------------------------------------------------ //
    // Public API — called by UnitSelector when the player issues a command
    // ------------------------------------------------------------------ //

    /// <summary>
    /// The player just right-clicked an enemy. The manual target overrides the
    /// leash — combat will chase to wherever, and we leave it alone. After the
    /// target dies (or combat otherwise drops it) the unit returns to guard.
    /// </summary>
    public void NotifyManualAttack(Health manualTarget)
    {
        if (manualTarget == null) return;

        currentAutoTarget = null;
        state             = AutoState.ManualCommand;
    }

    /// <summary>
    /// The player just right-clicked the ground (move order). Pause auto-scan
    /// for <see cref="autoAttackPausedAfterManualMoveSeconds"/>, update the
    /// guard position to the new destination (if enabled), and return the FSM
    /// to Guarding so scanning resumes around the new spot after the pause.
    /// </summary>
    public void NotifyManualMove(Vector3 destination)
    {
        currentAutoTarget = null;
        scanResumeTime    = Time.time + autoAttackPausedAfterManualMoveSeconds;
        state             = AutoState.Guarding;

        if (updateGuardPositionOnManualMove)
        {
            guardPosition = destination;
            Debug.Log($"[AutoAttack:{name}] Guard position updated by manual move.");
        }
    }

    // ------------------------------------------------------------------ //
    // Scan + acquire
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Picks the nearest valid hostile target inside the detection radius
    /// centred on <see cref="guardPosition"/>. Stable centre — the scan
    /// doesn't drift as the unit chases.
    /// </summary>
    private void TryAcquireTarget()
    {
        if (unitCombat == null && rocketCombat == null && missileCombat == null)
        {
            if (!noCombatWarned)
            {
                Debug.LogWarning($"[AutoAttack:{name}] No combat component found.");
                noCombatWarned = true;
            }
            return;
        }

        Health best     = null;
        float  bestDist = float.PositiveInfinity;

        // Scan from the guard position so the detection circle is stable.
        int count = Physics.OverlapSphereNonAlloc(
            guardPosition, detectionRadius, scanBuffer, targetLayerMask.value);

        Health.Team hostileTeam = (ownHealth.team == Health.Team.Player)
            ? Health.Team.Enemy
            : Health.Team.Player;

        for (int i = 0; i < count; i++)
        {
            Collider c = scanBuffer[i];
            if (c == null) continue;

            Health h = c.GetComponent<Health>() ?? c.GetComponentInParent<Health>();
            if (h == null || h == ownHealth) continue;
            if (h.team != hostileTeam) continue;

            UnitCategory.Category cat = DamageRules.Resolve(h.gameObject);
            if (!CanEngageCategory(cat)) continue;

            float distFromGuard = Vector3.Distance(h.transform.position, guardPosition);
            if (distFromGuard > detectionRadius) continue;     // capsule colliders may leak slightly

            // Pick the candidate nearest to the unit (not the guard), so the
            // unit engages whatever is closest to its barrel.
            float distFromUnit = Vector3.Distance(h.transform.position, transform.position);
            if (distFromUnit < bestDist)
            {
                bestDist = distFromUnit;
                best     = h;
            }
        }

        if (best == null) return;          // nothing eligible; stay idle

        AssignAutoTarget(best);
    }

    /// <summary>
    /// Like <see cref="TryAcquireTarget"/> but with relaxed inclusion criteria
    /// for the post-kill continuation window. A candidate is eligible if it's
    /// inside the absolute leash boundary (leashRadius + leashTolerance) AND
    /// either:
    ///   • inside detectionRadius from guardPosition (normal scan range), OR
    ///   • inside continueFightRadius of the last fight position, OR
    ///   • inside continueFightRadius of the unit itself.
    /// Picks the candidate nearest to the unit. Returns false if nothing
    /// eligible exists, signalling the caller to commit to ReturningToGuard.
    /// </summary>
    private bool TryAcquireContinuationTarget(out Health best)
    {
        best = null;
        if (unitCombat == null && rocketCombat == null && missileCombat == null) return false;

        float bestDist     = float.PositiveInfinity;
        float searchRadius = Mathf.Max(leashRadius + leashTolerance, continueFightRadius);

        // OverlapSphere from the unit's position so a target sitting next to
        // us but slightly outside guard-detection still surfaces; we apply
        // the leash filter below.
        int count = Physics.OverlapSphereNonAlloc(
            transform.position, searchRadius, scanBuffer, targetLayerMask.value);

        Health.Team hostileTeam = (ownHealth.team == Health.Team.Player)
            ? Health.Team.Enemy
            : Health.Team.Player;

        bool sawLeashRejection = false;

        for (int i = 0; i < count; i++)
        {
            Collider c = scanBuffer[i];
            if (c == null) continue;

            Health h = c.GetComponent<Health>() ?? c.GetComponentInParent<Health>();
            if (h == null || h == ownHealth) continue;
            if (h.team != hostileTeam) continue;

            UnitCategory.Category cat = DamageRules.Resolve(h.gameObject);
            if (!CanEngageCategory(cat)) continue;

            Vector3 hp = h.transform.position;
            float distFromGuard     = Vector3.Distance(hp, guardPosition);
            float distFromUnit      = Vector3.Distance(hp, transform.position);
            float distFromLastFight = Vector3.Distance(hp, lastFightPosition);

            // Hard leash gate — nothing outside the absolute boundary qualifies.
            if (distFromGuard > leashRadius + leashTolerance)
            {
                sawLeashRejection = true;
                continue;
            }

            // Relaxed inclusion: regular detection OR nearby to fight / unit.
            bool inDetection    = distFromGuard     <= detectionRadius;
            bool nearFight      = distFromLastFight <= continueFightRadius;
            bool nearUnit       = distFromUnit      <= continueFightRadius;

            if (!inDetection && !nearFight && !nearUnit) continue;

            if (distFromUnit < bestDist)
            {
                bestDist = distFromUnit;
                best     = h;
            }
        }

        if (best == null && sawLeashRejection)
            Debug.Log($"[AutoAttack:{name}] Candidate rejected: outside leash.");

        return best != null;
    }

    /// <summary>
    /// Hands <paramref name="target"/> to the combat component as an auto-acquired
    /// target and transitions to AutoChasing.
    /// </summary>
    private void AssignAutoTarget(Health target)
    {
        currentAutoTarget = target;
        state             = AutoState.AutoChasing;

        if (unitCombat    != null) unitCombat.SetTarget(target);
        if (rocketCombat  != null) rocketCombat.SetTarget(target);
        if (missileCombat != null) missileCombat.SetTarget(target);

        Debug.Log($"[AutoAttack:{name}] Acquired target inside guard radius: {target.name}.");
    }

    private void ClearAutoTargetOnCombat()
    {
        if (unitCombat    != null) unitCombat.ClearTarget();
        if (rocketCombat  != null) rocketCombat.ClearTarget();
        if (missileCombat != null) missileCombat.ClearTarget();
    }

    /// <summary>
    /// Reads the combat component's intent: <see cref="UnitCombat.IsIdle"/> /
    /// <see cref="RocketCombat.IsIdle"/>. Treats missing combat as idle.
    /// </summary>
    private bool CombatIsIdle()
    {
        if (unitCombat    != null) return unitCombat.IsIdle;
        if (rocketCombat  != null) return rocketCombat.IsIdle;
        if (missileCombat != null) return missileCombat.IsIdle;
        return true;
    }

    /// <summary>
    /// Asks the weapon "do you have any business shooting this category?".
    /// RocketCombat says yes to everything (rockets are universal); UnitCombat
    /// skips Aircraft for Bullet and Cannon damage types so the Soldier /
    /// Humvee / Artillery Tank don't waste shots on jets they can barely scratch.
    /// MachineGun and Missile damage types CAN engage Aircraft.
    /// </summary>
    private bool CanEngageCategory(UnitCategory.Category cat)
    {
        if (rocketCombat != null) return true;

        // Missile launcher is ground-only — never auto-acquire aircraft.
        if (missileCombat != null)
            return cat != UnitCategory.Category.Aircraft;

        if (unitCombat == null) return false;

        if (cat == UnitCategory.Category.Aircraft)
            return unitCombat.damageType == DamageType.Missile
                || unitCombat.damageType == DamageType.MachineGun;

        return true;
    }

    // ------------------------------------------------------------------ //
    // Gizmos
    // ------------------------------------------------------------------ //

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!debugDrawRadius) return;

        // Use guardPosition at runtime; transform.position at edit-time
        // (Awake hasn't initialised yet in the editor preview).
        Vector3 centre = Application.isPlaying && hasGuardPosition
            ? guardPosition
            : transform.position;

        // Detection radius — yellow disc.
        UnityEditor.Handles.color = new Color(1f, 0.85f, 0.2f, 0.25f);
        UnityEditor.Handles.DrawWireDisc(centre, Vector3.up, detectionRadius);

        // Leash radius — red disc (outer).
        UnityEditor.Handles.color = new Color(1f, 0.2f, 0.2f, 0.20f);
        UnityEditor.Handles.DrawWireDisc(centre, Vector3.up, leashRadius);

        // Guard pivot pip.
        UnityEditor.Handles.color = new Color(0.4f, 1f, 0.4f, 0.6f);
        UnityEditor.Handles.DrawSolidDisc(centre, Vector3.up, 0.25f);
    }
#endif
}
