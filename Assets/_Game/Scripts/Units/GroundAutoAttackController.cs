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
             "(e.g. 12 / 18) gives the unit room to chase a fleeing enemy a short distance.")]
    public float leashRadius = 18f;

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
    private UnitCombat    unitCombat;     // null on RPG units
    private RocketCombat  rocketCombat;   // null on rifle / cannon units
    private UnitMovement  movement;       // used for ReturningToGuard MoveTo

    private AutoState state = AutoState.Guarding;
    private Vector3   guardPosition;
    private bool      hasGuardPosition;

    // The auto-acquired target, if any. Null in Guarding / ReturningToGuard / ManualCommand.
    private Health currentAutoTarget;

    // Time after which auto-scan may resume (post-manual-move suppression).
    private float scanResumeTime;
    private float scanTimer;

    // OverlapSphere buffer — sized for typical scenes; overflow truncates safely.
    private readonly Collider[] scanBuffer = new Collider[32];

    private bool noCombatWarned;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        ownHealth    = GetComponent<Health>();
        unitCombat   = GetComponent<UnitCombat>();
        rocketCombat = GetComponent<RocketCombat>();
        movement     = GetComponent<UnitMovement>();

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

        // Manual-move suppression — applies regardless of state.
        if (Time.time < scanResumeTime) return;

        switch (state)
        {
            case AutoState.Guarding:         UpdateGuarding();         break;
            case AutoState.AutoChasing:      UpdateAutoChasing();      break;
            case AutoState.ReturningToGuard: UpdateReturningToGuard(); break;
            case AutoState.ManualCommand:    UpdateManualCommand();    break;
        }
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
        // Combat went idle on its own (target died / cleared). Decide what to
        // do next: return home if we wandered, otherwise just resume guarding.
        if (currentAutoTarget == null || CombatIsIdle())
        {
            currentAutoTarget = null;
            EnterPostEngagement();
            return;
        }

        // Leash check — the hard limit on how far an enemy can pull us. Run
        // every frame so a fleeing target is dropped immediately, not on the
        // next 0.35s scan tick.
        float distFromGuard = Vector3.Distance(
            currentAutoTarget.transform.position, guardPosition);

        if (distFromGuard > leashRadius)
        {
            Debug.Log($"[AutoAttack:{name}] Target left leash range — returning to guard.");
            ClearAutoTargetOnCombat();
            currentAutoTarget = null;
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
        if (unitCombat == null && rocketCombat == null)
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
    /// Hands <paramref name="target"/> to the combat component as an auto-acquired
    /// target and transitions to AutoChasing.
    /// </summary>
    private void AssignAutoTarget(Health target)
    {
        currentAutoTarget = target;
        state             = AutoState.AutoChasing;

        if (unitCombat   != null) unitCombat.SetTarget(target);
        if (rocketCombat != null) rocketCombat.SetTarget(target);

        Debug.Log($"[AutoAttack:{name}] Acquired target inside guard radius: {target.name}.");
    }

    private void ClearAutoTargetOnCombat()
    {
        if (unitCombat   != null) unitCombat.ClearTarget();
        if (rocketCombat != null) rocketCombat.ClearTarget();
    }

    /// <summary>
    /// Reads the combat component's intent: <see cref="UnitCombat.IsIdle"/> /
    /// <see cref="RocketCombat.IsIdle"/>. Treats missing combat as idle.
    /// </summary>
    private bool CombatIsIdle()
    {
        if (unitCombat   != null) return unitCombat.IsIdle;
        if (rocketCombat != null) return rocketCombat.IsIdle;
        return true;
    }

    /// <summary>
    /// Asks the weapon "do you have any business shooting this category?".
    /// RocketCombat says yes to everything (rockets are universal); UnitCombat
    /// skips Aircraft for Bullet and Cannon damage types so the Soldier /
    /// Humvee / Artillery Tank don't waste shots on jets they can barely scratch.
    /// </summary>
    private bool CanEngageCategory(UnitCategory.Category cat)
    {
        if (rocketCombat != null) return true;
        if (unitCombat   == null) return false;

        if (cat == UnitCategory.Category.Aircraft)
            return unitCombat.damageType == DamageType.Missile;

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
