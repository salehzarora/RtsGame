using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Drives a unit's attack behaviour: chase → stop at range → face target → deal
/// damage on cooldown. Supports ranged combat with an optional muzzle tracer.
///
/// State machine:
///   Idle           — no target, unit does nothing.
///   ChasingTarget  — moving toward target until within attack range.
///   Attacking      — stopped, rotating to face target, dealing damage on cooldown.
///
/// If the target is destroyed Unity sets the reference to null automatically,
/// and the unit returns to Idle.
///
/// Setup:
///   Add this component to PLAYER units only (alongside Health and UnitMovement).
///   Enemy dummy units only need Health — they do not fight back yet.
///
///   For ranged units (Soldier):
///     • Set Attack Range to something like 8 so the unit stops at rifle distance.
///     • Optionally drag a FirePoint child Transform into the Fire Point field;
///       if left empty, the tracer originates at chest height (transform + 1.2 up).
///     • Run Tools → RTS → Apply Soldier Ranged Combat Stats to one-click
///       configure SoldierPrefab.
///
///   The yellow muzzle tracer is built procedurally at runtime — no Inspector
///   reference is required and there is no asset dependency. Removing the
///   tracer fields' values (zero width, zero duration) silently disables it.
/// </summary>
[RequireComponent(typeof(UnitMovement))]
[RequireComponent(typeof(Health))]
public class UnitCombat : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    private enum CombatState { Idle, ChasingTarget, Attacking }

    // ------------------------------------------------------------------ //
    // Inspector fields
    // ------------------------------------------------------------------ //

    [Header("Attack Stats")]
    [Tooltip("World-unit radius within which this unit can hit its target")]
    public float attackRange = 8f;

    [Tooltip("Base damage dealt per hit, before the category modifier in DamageRules.")]
    public float attackDamage = 10f;

    [Tooltip("Seconds between hits")]
    public float attackCooldown = 0.4f;

    [Tooltip("Damage type. Bullet → strong vs Infantry, weak vs Vehicle/Building. " +
             "Cannon → strong vs Vehicle/Building, weak vs Infantry. See DamageRules.")]
    public DamageType damageType = DamageType.Bullet;

    [Header("Ranged Setup")]
    [Tooltip("Marks this unit as ranged. Reserved for future logic (e.g. enemy AI " +
             "deciding kite distance). Combat currently behaves the same either way " +
             "— the gameplay change is Attack Range.")]
    public bool isRanged = true;

    [Tooltip("Muzzle point used as the tracer origin. If null, the chest position " +
             "(transform.position + Vector3.up * 1.2) is used instead.")]
    public Transform firePoint;

    [Tooltip("Degrees per second this unit can rotate to face its target while attacking")]
    public float rotationSpeed = 540f;

    [Header("Fire While Moving (vehicle-style)")]
    [Tooltip("If true, the unit can release shots while its body is moving. The " +
             "turret (if a VehicleTurretController is present) still aims; the " +
             "body does NOT stop on entering range when this is enabled. Default " +
             "false so infantry behaviour is unchanged.")]
    public bool canFireWhileMoving = false;

    [Tooltip("Cooldown multiplier while moving. >1 means slower fire on the move; " +
             "1.4 is a sensible tank value, 1.1 for a fast vehicle. Stationary fire " +
             "always uses 1.0× cooldown.")]
    public float movingCooldownMultiplier = 1.4f;

    [Tooltip("Probability (0..1) that a moving shot hits. On a miss the tracer still " +
             "fires for visual feedback but no damage is applied. 1.0 = always hits, " +
             "0.65 ≈ tank on the move, 0.85 ≈ light vehicle on the move.")]
    [Range(0f, 1f)] public float movingAccuracy = 0.65f;

    [Tooltip("Probability (0..1) that a stationary shot hits. Usually 1.0 for hitscan; " +
             "set lower if you want generic miss chance regardless of motion.")]
    [Range(0f, 1f)] public float stationaryAccuracy = 1.0f;

    [Tooltip("NavMeshAgent velocity threshold (units/sec) above which the unit is " +
             "considered to be 'moving' for the purposes of accuracy / cooldown " +
             "penalties. Below this it counts as stationary.")]
    public float movingSpeedThreshold = 0.2f;

    [Header("Tracer Visual (optional, lightweight)")]
    [Tooltip("Colour of the muzzle-to-target tracer line")]
    [ColorUsage(false)] public Color tracerColor = new Color(1f, 0.85f, 0.3f);

    [Tooltip("Seconds the tracer line stays visible per shot. 0 disables the tracer.")]
    public float tracerDuration = 0.06f;

    [Tooltip("World-unit thickness of the tracer line. 0 disables the tracer.")]
    public float tracerWidth = 0.05f;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private CombatState state = CombatState.Idle;
    private Health target;
    private UnitMovement movement;
    private float attackTimer;

    // Optional sibling components — null on infantry, non-null on vehicles wired
    // up via the Repair Vehicle Turrets tool.
    private VehicleTurretController     turretController;
    private UnityEngine.AI.NavMeshAgent agent;

    private LineRenderer tracer;
    private float tracerTimer;

    /// <summary>True when the unit has no active target (state == Idle).
    /// EnemyAIController uses this to detect an immediately-dead target and
    /// retarget without waiting for the periodic scan.</summary>
    public bool IsIdle => state == CombatState.Idle;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        movement         = GetComponent<UnitMovement>();
        turretController = GetComponent<VehicleTurretController>();
        agent            = GetComponent<UnityEngine.AI.NavMeshAgent>();
        BuildTracer();
    }

    private void Update()
    {
        TickTracer();

        if (state == CombatState.Idle) return;

        // Target was destroyed — Unity null-check on MonoBehaviour handles this
        if (target == null)
        {
            state = CombatState.Idle;
            if (turretController != null) turretController.ClearAim();
            return;
        }

        float dist     = Vector3.Distance(transform.position, target.transform.position);
        bool  isMoving = agent != null &&
                         agent.velocity.sqrMagnitude >
                             movingSpeedThreshold * movingSpeedThreshold;

        // The turret tracks the target regardless of body motion. UnitCombat
        // gates firing on turretController.IsAimed below; if no turret is
        // present, the body rotates via FaceTarget() instead.
        if (turretController != null) turretController.AimAt(target.transform);

        if (dist > attackRange)
        {
            // Out of range. Fire-while-moving vehicles don't auto-chase — the
            // body keeps doing whatever the player ordered. Other units chase.
            if (canFireWhileMoving && isMoving)
            {
                // Just wait — the body is going somewhere; we'll fire if the
                // path happens to bring the target into range.
                state = CombatState.ChasingTarget;
            }
            else
            {
                state = CombatState.ChasingTarget;
                movement.MoveTo(target.transform.position);
            }
            return;
        }

        // In range. Decide whether to stop or fire on the move.
        bool fireOnMove = canFireWhileMoving && isMoving;

        if (state != CombatState.Attacking)
        {
            state = CombatState.Attacking;
            if (!fireOnMove) movement.Stop();
            attackTimer = attackCooldown;     // brief wind-up before the first hit
        }

        // Body facing — only rotate the WHOLE body when there's no turret to
        // do the aim. A turret-equipped vehicle leaves its body to NavMeshAgent.
        if (turretController == null && !fireOnMove) FaceTarget();

        attackTimer -= Time.deltaTime;
        if (attackTimer <= 0f)
        {
            // Hold fire until the turret has slewed onto target. No-op when
            // there's no turret (infantry path).
            if (turretController != null && !turretController.IsAimed)
            {
                // Tiny re-check delay so we don't spin the timer to -∞ while
                // the cannon swings around.
                attackTimer = 0.05f;
                return;
            }

            // Apply moving-vs-stationary accuracy. On a miss, the tracer still
            // flashes but no damage is applied — feels like a wide round.
            float hitChance = fireOnMove ? movingAccuracy : stationaryAccuracy;
            bool  willHit   = Random.value <= hitChance;

            if (willHit) ApplyDamage();
            ShowTracer(target.transform.position);

            attackTimer = attackCooldown * (fireOnMove ? movingCooldownMultiplier : 1f);

            // Target may have just been destroyed by that hit
            if (target == null)
            {
                state = CombatState.Idle;
                if (turretController != null) turretController.ClearAim();
            }
        }
    }

    // ------------------------------------------------------------------ //
    // Public API — called by UnitSelector
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Assign a target Health component to attack.
    /// Works for both player-controlled and AI-controlled units — no team check.
    /// The unit will immediately begin chasing.
    /// </summary>
    public void SetTarget(Health enemyHealth)
    {
        target = enemyHealth;
        state = CombatState.ChasingTarget;
        attackTimer = attackCooldown;

        // Hand the aim to the turret immediately so it starts slewing while
        // the body decides whether to chase or keep going.
        if (turretController != null && enemyHealth != null)
            turretController.AimAt(enemyHealth.transform);
    }

    /// <summary>
    /// Cancel the current attack and return to Idle.
    /// Call this when a move command overrides the attack.
    /// </summary>
    public void ClearTarget()
    {
        target = null;
        state = CombatState.Idle;
        if (turretController != null) turretController.ClearAim();
    }

    // ------------------------------------------------------------------ //
    // Combat helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Rotates the unit on the Y axis toward <see cref="target"/>. Called only
    /// while stopped in the Attacking state so we don't fight NavMeshAgent.
    /// </summary>
    private void FaceTarget()
    {
        Vector3 toTarget = target.transform.position - transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.0001f) return;

        Quaternion want = Quaternion.LookRotation(toTarget);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, want, rotationSpeed * Time.deltaTime);
    }

    /// <summary>
    /// Resolves the target's category, looks up the damage modifier for our
    /// <see cref="damageType"/>, applies the scaled damage, and logs the hit.
    /// Logs are one line per hit and gated by the attack cooldown, so they
    /// stay readable even with several attackers in play.
    /// </summary>
    private void ApplyDamage()
    {
        UnitCategory.Category cat = DamageRules.Resolve(target.gameObject);
        float modifier            = DamageRules.Modifier(damageType, cat);
        float finalDamage         = attackDamage * modifier;

        target.TakeDamage(finalDamage);

        Debug.Log($"[Combat] {name} hit {target.name} ({cat}): " +
                  $"base {attackDamage}, {damageType} ×{modifier:F2}, final {finalDamage:F1}");
    }

    // ------------------------------------------------------------------ //
    // Tracer — runtime-built LineRenderer, flashed per shot
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Builds a child LineRenderer used as the per-shot tracer. The renderer
    /// is disabled by default and only enabled briefly via ShowTracer().
    /// </summary>
    private void BuildTracer()
    {
        // Treat width 0 OR duration 0 as "tracer disabled" — combat keeps
        // working, just no visual.
        if (tracerWidth <= 0f || tracerDuration <= 0f) return;

        GameObject tg = new GameObject("Tracer");
        tg.transform.SetParent(transform, worldPositionStays: false);

        tracer = tg.AddComponent<LineRenderer>();
        tracer.positionCount     = 2;
        tracer.useWorldSpace     = true;
        tracer.startWidth        = tracerWidth;
        tracer.endWidth          = tracerWidth;
        tracer.numCapVertices    = 0;
        tracer.shadowCastingMode = ShadowCastingMode.Off;
        tracer.receiveShadows    = false;

        // Sprites/Default ships in every pipeline and respects vertex colour,
        // so the line takes our colour without any per-pipeline shader pick.
        Shader shader = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Unlit/Color")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Hidden/InternalErrorShader");

        tracer.material   = new Material(shader) { color = tracerColor };
        tracer.startColor = tracerColor;
        tracer.endColor   = tracerColor;
        tracer.enabled    = false;
    }

    private void ShowTracer(Vector3 targetPos)
    {
        if (tracer == null) return;

        // Prefer the turret's muzzle so tracers leave the rotating barrel, not
        // the body. Falls back to UnitCombat's own firePoint, then chest height.
        Transform muzzle = (turretController != null && turretController.firePoint != null)
            ? turretController.firePoint
            : firePoint;

        Vector3 start = muzzle != null
            ? muzzle.position
            : transform.position + Vector3.up * 1.2f;

        // Aim at the target's chest, not its feet — feels less like shooting
        // into the ground.
        Vector3 end = targetPos + Vector3.up * 1.0f;

        tracer.SetPosition(0, start);
        tracer.SetPosition(1, end);
        tracer.enabled = true;
        tracerTimer    = tracerDuration;

        // Positional gunfire — fires on the owner client (combat FSM is gated
        // off on non-owners). The SoundEvent's minInterval keeps a squad firing
        // together from turning into a wall of noise.
        AudioManager.SfxAt(GameSound.Gunfire, start);
    }

    private void TickTracer()
    {
        if (tracerTimer <= 0f) return;

        tracerTimer -= Time.deltaTime;
        if (tracerTimer <= 0f && tracer != null)
            tracer.enabled = false;
    }

    // ------------------------------------------------------------------ //
    // Gizmos — helps visualise attack range in Scene view
    // ------------------------------------------------------------------ //

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        UnityEditor.Handles.color = new Color(1f, 0.2f, 0.2f, 0.3f);
        UnityEditor.Handles.DrawWireDisc(transform.position, Vector3.up, attackRange);
    }
#endif
}
