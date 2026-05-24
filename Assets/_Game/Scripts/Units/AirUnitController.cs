using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Drives an aircraft through its full sortie loop:
///
///   Parked → TakingOff → FlyingToTarget → Attacking → Returning → Landing → Parked
///
/// The aircraft does NOT use NavMeshAgent / UnitMovement — it moves by direct
/// transform manipulation at a fixed flight altitude. That keeps it entirely
/// out of the ground-unit pathfinding system.
///
/// Setup (done automatically by Tools → RTS → Air System → Create Strike Jet Prefab):
///   1. Attach to the aircraft root GameObject (with Health, SelectableAircraft,
///      UnitCategory = Aircraft, and a BoxCollider for selection).
///   2. Assign a child Transform "FirePoint" near the missile pods.
///   3. Tune attack range / ammo / damage / altitude in the Inspector.
///   4. The Airfield calls AssignHome(...) at spawn time — do NOT call it yourself.
/// </summary>
public class AirUnitController : MonoBehaviour
{
    // Match the runtime layer used for the missile tracer line so it never
    // intercepts gameplay raycasts.
    private const int IgnoreRaycastLayer = 2;

    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    public enum FlightState
    {
        Parked,
        TakingOff,
        FlyingToTarget,
        Attacking,
        Returning,
        Landing
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

    [Tooltip("XZ distance below which the Returning state hands off to Landing.")]
    public float landingApproachThreshold = 0.5f;

    // ------------------------------------------------------------------ //
    // Inspector — Attack
    // ------------------------------------------------------------------ //

    [Header("Attack")]
    [Tooltip("World-unit radius within which this aircraft can fire missiles.")]
    public float attackRange = 18f;

    [Tooltip("Maximum missiles per sortie. After firing this many, the aircraft returns.")]
    public int maxAmmo = 2;

    [Tooltip("Base damage per missile, before category modifier in DamageRules.")]
    public float missileDamage = 120f;

    [Tooltip("Seconds between missile shots.")]
    public float missileCooldown = 1.0f;

    [Tooltip("Damage type used for the modifier lookup. Missile is strong vs Vehicle/Building.")]
    public DamageType damageType = DamageType.Missile;

    [Header("Visual")]
    [Tooltip("Muzzle origin for the missile tracer. Falls back to transform.position if null.")]
    public Transform firePoint;

    [Tooltip("Colour of the missile tracer line.")]
    [ColorUsage(false)] public Color tracerColor = new Color(1f, 0.45f, 0.15f);

    [Tooltip("Seconds the tracer stays visible per shot.")]
    public float tracerDuration = 0.10f;

    [Tooltip("Width of the tracer line.")]
    public float tracerWidth = 0.08f;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    public FlightState State { get; private set; } = FlightState.Parked;
    public int CurrentAmmo { get; private set; }
    public Airfield HomeAirfield { get; private set; }

    private Transform homeSlot;
    private Health    target;
    private float     fireTimer;

    private LineRenderer tracer;
    private float        tracerTimer;

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
        State       = FlightState.Parked;
    }

    // ------------------------------------------------------------------ //
    // Public API — called by UnitSelector when the player right-clicks an enemy
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Order the aircraft to attack <paramref name="enemy"/>. If parked it
    /// takes off; if already airborne it retargets. Ignores friendlies.
    /// </summary>
    public void AttackTarget(Health enemy)
    {
        if (enemy == null || enemy.team == Health.Team.Player)
        {
            Debug.LogWarning($"[Aircraft:{name}] Invalid attack target — ignoring.");
            return;
        }

        target = enemy;
        switch (State)
        {
            case FlightState.Parked:
                State = FlightState.TakingOff;
                Debug.Log($"[Aircraft:{name}] Takeoff — heading to attack '{enemy.name}'.");
                break;

            case FlightState.Landing:
                // Mid-landing override — abort the descent and climb back up.
                State = FlightState.TakingOff;
                Debug.Log($"[Aircraft:{name}] Aborting landing — new target '{enemy.name}'.");
                break;

            default:
                // Already in the air — just swap targets.
                State = FlightState.FlyingToTarget;
                Debug.Log($"[Aircraft:{name}] Retargeting to '{enemy.name}'.");
                break;
        }
    }

    // ------------------------------------------------------------------ //
    // Unity lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        BuildTracer();
    }

    private void Update()
    {
        TickTracer();

        switch (State)
        {
            case FlightState.Parked:          /* idle */                 break;
            case FlightState.TakingOff:       UpdateTakingOff();         break;
            case FlightState.FlyingToTarget:  UpdateFlyingToTarget();    break;
            case FlightState.Attacking:       UpdateAttacking();         break;
            case FlightState.Returning:       UpdateReturning();         break;
            case FlightState.Landing:         UpdateLanding();           break;
        }
    }

    // ------------------------------------------------------------------ //
    // State updates
    // ------------------------------------------------------------------ //

    private void UpdateTakingOff()
    {
        Vector3 pos = transform.position;
        pos.y = Mathf.MoveTowards(pos.y, flightAltitude, verticalSpeed * Time.deltaTime);
        transform.position = pos;

        if (pos.y >= flightAltitude - 0.05f)
        {
            State = target != null ? FlightState.FlyingToTarget : FlightState.Returning;
            Debug.Log($"[Aircraft:{name}] Reached cruise altitude.");
        }
    }

    private void UpdateFlyingToTarget()
    {
        if (target == null)
        {
            State = FlightState.Returning;
            return;
        }

        Vector3 wantedXZ = new Vector3(target.transform.position.x, flightAltitude, target.transform.position.z);
        Vector3 dir      = wantedXZ - transform.position;
        float distXZ     = new Vector2(dir.x, dir.z).magnitude;

        if (distXZ <= attackRange)
        {
            State = FlightState.Attacking;
            fireTimer = 0f; // fire immediately on arrival
            return;
        }

        Vector3 step = dir.normalized * cruiseSpeed * Time.deltaTime;
        transform.position += step;

        FaceVelocity(step);
    }

    private void UpdateAttacking()
    {
        // Lost the target mid-attack — return.
        if (target == null)
        {
            Debug.Log($"[Aircraft:{name}] Target lost — returning.");
            State = FlightState.Returning;
            return;
        }

        // Out of ammo — return.
        if (CurrentAmmo <= 0)
        {
            Debug.Log($"[Aircraft:{name}] Out of missiles — returning.");
            State = FlightState.Returning;
            return;
        }

        // Target moved out of range — chase.
        Vector3 wantedXZ = new Vector3(target.transform.position.x, flightAltitude, target.transform.position.z);
        Vector3 dir      = wantedXZ - transform.position;
        float distXZ     = new Vector2(dir.x, dir.z).magnitude;

        if (distXZ > attackRange)
        {
            State = FlightState.FlyingToTarget;
            return;
        }

        // Hover and face the target while firing.
        if (dir.sqrMagnitude > 0.0001f)
        {
            Quaternion want = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.z));
            transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turnSpeed * Time.deltaTime);
        }

        fireTimer -= Time.deltaTime;
        if (fireTimer <= 0f)
        {
            FireMissile();
            fireTimer    = missileCooldown;
            CurrentAmmo -= 1;
        }
    }

    private void UpdateReturning()
    {
        if (homeSlot == null)
        {
            // Airfield destroyed mid-flight. Keep parked-but-airborne so the
            // user can still see the aircraft and self-destruct or salvage.
            Debug.LogWarning($"[Aircraft:{name}] Home slot lost — holding in air.");
            State = FlightState.Parked;
            return;
        }

        Vector3 wantedXZ = new Vector3(homeSlot.position.x, flightAltitude, homeSlot.position.z);
        Vector3 dir      = wantedXZ - transform.position;
        float distXZ     = new Vector2(dir.x, dir.z).magnitude;

        if (distXZ <= landingApproachThreshold)
        {
            State = FlightState.Landing;
            Debug.Log($"[Aircraft:{name}] Final approach — landing.");
            return;
        }

        Vector3 step = dir.normalized * cruiseSpeed * Time.deltaTime;
        transform.position += step;

        FaceVelocity(step);
    }

    private void UpdateLanding()
    {
        if (homeSlot == null)
        {
            State = FlightState.Parked;
            return;
        }

        // Descend toward slot position; rotate toward slot orientation.
        transform.position = Vector3.MoveTowards(
            transform.position, homeSlot.position, verticalSpeed * Time.deltaTime);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, homeSlot.rotation, turnSpeed * Time.deltaTime);

        if (Vector3.Distance(transform.position, homeSlot.position) < 0.05f)
        {
            transform.position = homeSlot.position;
            transform.rotation = homeSlot.rotation;
            CurrentAmmo        = maxAmmo;     // re-arm on touchdown
            target             = null;
            State              = FlightState.Parked;
            Debug.Log($"[Aircraft:{name}] Landed and re-armed ({maxAmmo} missiles).");
        }
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

    /// <summary>
    /// Applies damage with the DamageRules modifier and shows the tracer.
    /// </summary>
    private void FireMissile()
    {
        if (target == null) return;

        UnitCategory.Category cat = DamageRules.Resolve(target.gameObject);
        float modifier            = DamageRules.Modifier(damageType, cat);
        float finalDamage         = missileDamage * modifier;

        target.TakeDamage(finalDamage);

        Debug.Log($"[Combat] {name} fired Missile at {target.name} ({cat}): " +
                  $"base {missileDamage}, ×{modifier:F2}, final {finalDamage:F1}. " +
                  $"Ammo left: {CurrentAmmo - 1}.");

        ShowTracer(target.transform.position);
    }

    // ------------------------------------------------------------------ //
    // Tracer — runtime LineRenderer, flashes per shot (mirrors UnitCombat)
    // ------------------------------------------------------------------ //

    private void BuildTracer()
    {
        if (tracerWidth <= 0f || tracerDuration <= 0f) return;

        GameObject tg = new GameObject("MissileTracer");
        tg.transform.SetParent(transform, worldPositionStays: false);
        tg.layer = IgnoreRaycastLayer;

        tracer = tg.AddComponent<LineRenderer>();
        tracer.positionCount     = 2;
        tracer.useWorldSpace     = true;
        tracer.startWidth        = tracerWidth;
        tracer.endWidth          = tracerWidth;
        tracer.numCapVertices    = 0;
        tracer.shadowCastingMode = ShadowCastingMode.Off;
        tracer.receiveShadows    = false;

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

        Vector3 start = firePoint != null ? firePoint.position : transform.position;
        Vector3 end   = targetPos + Vector3.up * 0.5f;

        tracer.SetPosition(0, start);
        tracer.SetPosition(1, end);
        tracer.enabled = true;
        tracerTimer    = tracerDuration;
    }

    private void TickTracer()
    {
        if (tracerTimer <= 0f) return;

        tracerTimer -= Time.deltaTime;
        if (tracerTimer <= 0f && tracer != null)
            tracer.enabled = false;
    }
}
