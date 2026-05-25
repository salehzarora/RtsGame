using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Stationary defensive turret combat. Lives on a building (e.g. Machine Gun
/// Defense) and provides automatic target acquisition + hitscan firing without
/// any of the movement / chase / leash logic that <see cref="UnitCombat"/> +
/// <see cref="GroundAutoAttackController"/> bring along.
///
/// The turret:
///   • Never moves — it lives on a Building, no NavMeshAgent or UnitMovement
///     required.
///   • Scans an <see cref="attackRange"/> sphere every <see cref="scanInterval"/>
///     seconds for hostile <see cref="Health"/> components.
///   • Prefers targets by category: Infantry → Aircraft → Vehicle. Buildings
///     are skipped entirely (defensive turret has no business shooting walls).
///   • Rotates a child <see cref="turretPivot"/> on the Y axis at
///     <see cref="turretTurnSpeed"/> deg/sec until it is within
///     <see cref="aimToleranceDegrees"/> of the target.
///   • Fires a hitscan tracer from <see cref="firePoint"/> at
///     <see cref="attackCooldown"/>-second intervals and applies
///     <see cref="attackDamage"/> × DamageRules modifier to the target's Health.
///   • Reads an optional sibling <see cref="PowerConsumer"/> and refuses to fire
///     when the grid is underpowered.
///
/// What it deliberately does NOT do:
///   • Touch any unit-side scripts (UnitCombat, RocketCombat, GroundAutoAttack...).
///   • Add itself to UnitSelector's command flow — the player cannot manually
///     order a building to fire on a target.
///   • Tilt the barrel (yaw only).
///
/// Setup (assigned automatically by Create Machine Gun Defense Prefab):
///   1. Add to the MachineGunDefensePrefab root.
///   2. Assign <see cref="turretPivot"/> to the rotating GunPivot transform.
///   3. Assign <see cref="firePoint"/> to a child Transform at the muzzle tip.
///   4. Add an optional <see cref="PowerConsumer"/> to the same GameObject for
///      under-power gating; leave absent on prefabs that should always fire.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Health))]
public class BuildingTurretCombat : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector — references
    // ------------------------------------------------------------------ //

    [Header("References")]
    [Tooltip("The rotating turret pivot. Yaw-only rotation is applied here. " +
             "If null, the building's own transform is rotated — unsuitable for " +
             "most buildings, so always assign a child pivot.")]
    public Transform turretPivot;

    [Tooltip("Muzzle origin for tracers / hitscan. Should be a child of turretPivot " +
             "so it tracks with the rotating gun. Falls back to turretPivot.position " +
             "+ up 1.0 when null.")]
    public Transform firePoint;

    // ------------------------------------------------------------------ //
    // Inspector — stats
    // ------------------------------------------------------------------ //

    [Header("Attack Stats")]
    [Tooltip("World-unit radius within which the turret can fire.")]
    public float attackRange = 16f;

    [Tooltip("Base damage dealt per shot, BEFORE the DamageRules category modifier.")]
    public float attackDamage = 8f;

    [Tooltip("Seconds between shots. 0.12–0.18 gives the heavy-MG buzz-saw feel.")]
    public float attackCooldown = 0.15f;

    [Tooltip("Damage type used for the DamageRules lookup. Default MachineGun: " +
             "1.0× vs Infantry, 0.25× vs Vehicle, 0.10× vs Building, 0.55× vs Aircraft.")]
    public DamageType damageType = DamageType.MachineGun;

    // ------------------------------------------------------------------ //
    // Inspector — scanning
    // ------------------------------------------------------------------ //

    [Header("Scanning")]
    [Tooltip("Seconds between target re-scans. Smaller = more responsive, larger = cheaper.")]
    public float scanInterval = 0.2f;

    [Tooltip("Layers scanned for targets. Leave empty to auto-resolve Unit + Building " +
             "on Awake. Buildings are then filtered out by category so the turret doesn't " +
             "engage other structures, but the broad mask catches Aircraft / Vehicle " +
             "colliders that may live on either layer.")]
    public LayerMask targetLayerMask;

    [Tooltip("OverlapSphere buffer size. Overflowing scans are truncated safely.")]
    public int scanBufferSize = 32;

    // ------------------------------------------------------------------ //
    // Inspector — turret aim
    // ------------------------------------------------------------------ //

    [Header("Turret Aim")]
    [Tooltip("Yaw rotation speed in degrees / second.")]
    public float turretTurnSpeed = 220f;

    [Tooltip("Angle within which the turret counts as on-target and is allowed to fire.")]
    public float aimToleranceDegrees = 20f;

    // ------------------------------------------------------------------ //
    // Inspector — visual
    // ------------------------------------------------------------------ //

    [Header("Tracer Visual (optional)")]
    [Tooltip("Tracer line colour. Bright yellow reads well against most ground.")]
    [ColorUsage(false)] public Color tracerColor = new Color(1f, 0.85f, 0.3f);

    [Tooltip("Seconds the tracer line stays visible. 0 disables the tracer.")]
    public float tracerDuration = 0.05f;

    [Tooltip("World-unit thickness of the tracer line. 0 disables the tracer.")]
    public float tracerWidth = 0.05f;

    // ------------------------------------------------------------------ //
    // Inspector — power gating
    // ------------------------------------------------------------------ //

    [Header("Power Gating")]
    [Tooltip("If true, the turret refuses to fire when its sibling PowerConsumer reports " +
             "the global grid is under-supplied. Logs '[MGDefense] No power — turret offline.' " +
             "once per low-power transition. Leave true so the player can be punished for " +
             "skipping PowerPlants.")]
    public bool requirePower = true;

    [Header("Debug")]
    [Tooltip("Draw the attack range disc when the building is selected in the Scene view.")]
    public bool debugDrawRange = true;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private Health        ownHealth;
    private PowerConsumer powerConsumer;

    private Collider[] scanBuffer;
    private float      scanTimer;
    private float      attackTimer;

    private Health currentTarget;

    private LineRenderer tracer;
    private float        tracerTimer;

    // One-shot log gates so we don't spam every frame.
    private bool warnedNoPivot;
    private bool warnedNoPower;

    // Cached property IDs.
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId     = Shader.PropertyToID("_Color");

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        ownHealth     = GetComponent<Health>();
        powerConsumer = GetComponent<PowerConsumer>();

        // Auto-resolve target mask if not set — Unit + Building together cover
        // every player and enemy collider in this project.
        if (targetLayerMask.value == 0)
        {
            int unitLayer     = LayerMask.NameToLayer("Unit");
            int buildingLayer = LayerMask.NameToLayer("Building");
            int mask = 0;
            if (unitLayer     >= 0) mask |= 1 << unitLayer;
            if (buildingLayer >= 0) mask |= 1 << buildingLayer;
            targetLayerMask = mask != 0 ? mask : ~0;
        }

        scanBuffer = new Collider[Mathf.Max(8, scanBufferSize)];

        BuildTracer();

        // Stagger first scan so several turrets placed at once don't all
        // OverlapSphere on the same frame.
        scanTimer = Random.Range(0f, scanInterval);
    }

    private void Update()
    {
        TickTracer();

        if (ownHealth == null) return;

        // ── 1. Power gate ───────────────────────────────────────────── //
        if (requirePower && powerConsumer != null && !powerConsumer.IsPowered)
        {
            if (!warnedNoPower)
            {
                Debug.Log($"[MGDefense] No power — turret offline ({name}).");
                warnedNoPower = true;
            }
            // Drop any existing aim so the visual settles when power goes out.
            currentTarget = null;
            return;
        }
        if (warnedNoPower) warnedNoPower = false;

        // ── 2. Target validation / re-scan ──────────────────────────── //
        ValidateCurrentTarget();

        scanTimer -= Time.deltaTime;
        if (scanTimer <= 0f)
        {
            scanTimer = scanInterval;
            if (currentTarget == null)
                AcquireBestTarget();
        }

        if (currentTarget == null) return;

        // ── 3. Aim ──────────────────────────────────────────────────── //
        bool aimed = AimTurretAt(currentTarget.transform.position);

        // ── 4. Fire ─────────────────────────────────────────────────── //
        attackTimer -= Time.deltaTime;
        if (!aimed) return;
        if (attackTimer > 0f) return;

        attackTimer = attackCooldown;
        FireOnce();
    }

    // ------------------------------------------------------------------ //
    // Target management
    // ------------------------------------------------------------------ //

    /// <summary>Drops the current target if it died or wandered out of range.</summary>
    private void ValidateCurrentTarget()
    {
        if (currentTarget == null) return;

        // Unity null-check handles destroyed objects.
        if ((object)currentTarget == null || !currentTarget)
        {
            currentTarget = null;
            return;
        }

        if (Vector3.Distance(currentTarget.transform.position, transform.position) > attackRange)
        {
            currentTarget = null;
        }
    }

    /// <summary>
    /// Picks the nearest valid hostile target inside <see cref="attackRange"/>,
    /// with a per-category priority bias:
    ///   1. Infantry
    ///   2. Aircraft
    ///   3. Vehicle
    /// Buildings are skipped entirely — a defensive MG has no business shooting walls.
    /// </summary>
    private void AcquireBestTarget()
    {
        int count = Physics.OverlapSphereNonAlloc(
            transform.position, attackRange, scanBuffer, targetLayerMask.value);

        Health.Team hostileTeam = ownHealth.team == Health.Team.Player
            ? Health.Team.Enemy
            : Health.Team.Player;

        // Lower priority number = higher priority. Pick the lowest-priority
        // category we see; within that category, the nearest target wins.
        Health bestByCat       = null;
        int    bestPriority    = int.MaxValue;
        float  bestDistanceSqr = float.PositiveInfinity;

        for (int i = 0; i < count; i++)
        {
            Collider c = scanBuffer[i];
            if (c == null) continue;

            Health h = c.GetComponent<Health>() ?? c.GetComponentInParent<Health>();
            if (h == null || h == ownHealth) continue;
            if (h.team != hostileTeam) continue;

            UnitCategory.Category cat = DamageRules.Resolve(h.gameObject);
            int priority = CategoryPriority(cat);
            if (priority < 0) continue;             // skipped category (Building)

            float distSqr = (h.transform.position - transform.position).sqrMagnitude;
            if (distSqr > attackRange * attackRange) continue;

            // Pick by priority first, then by distance within the same priority.
            if (priority < bestPriority
                || (priority == bestPriority && distSqr < bestDistanceSqr))
            {
                bestByCat       = h;
                bestPriority    = priority;
                bestDistanceSqr = distSqr;
            }
        }

        if (bestByCat != null)
        {
            currentTarget = bestByCat;
            Debug.Log($"[MGDefense:{name}] Acquired target: {bestByCat.name}.");
        }
    }

    /// <summary>
    /// Returns the priority index for a target category. Lower = engages first.
    /// Negative = never engage.
    /// </summary>
    private static int CategoryPriority(UnitCategory.Category cat)
    {
        switch (cat)
        {
            case UnitCategory.Category.Infantry: return 0;
            case UnitCategory.Category.Aircraft: return 1;
            case UnitCategory.Category.Vehicle:  return 2;
            case UnitCategory.Category.Building: return -1;   // ignore
            default:                              return -1;
        }
    }

    // ------------------------------------------------------------------ //
    // Aiming
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Rotates <see cref="turretPivot"/> on the Y axis toward <paramref name="targetPos"/>.
    /// Returns true when the current yaw is within <see cref="aimToleranceDegrees"/>
    /// of the desired direction (gate for firing).
    /// </summary>
    private bool AimTurretAt(Vector3 targetPos)
    {
        if (turretPivot == null)
        {
            if (!warnedNoPivot)
            {
                Debug.LogWarning($"[MGDefense:{name}] turretPivot is not assigned — turret cannot aim.");
                warnedNoPivot = true;
            }
            // Be lenient: still allow firing so the design isn't blocked by a
            // missing reference, but the visual won't track.
            return true;
        }

        Vector3 to = targetPos - turretPivot.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.0001f) return false;

        Quaternion want = Quaternion.LookRotation(to.normalized, Vector3.up);
        turretPivot.rotation = Quaternion.RotateTowards(
            turretPivot.rotation, want, turretTurnSpeed * Time.deltaTime);

        return Quaternion.Angle(turretPivot.rotation, want) <= aimToleranceDegrees;
    }

    // ------------------------------------------------------------------ //
    // Firing
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Applies damage to the current target and flashes the tracer line.
    /// </summary>
    private void FireOnce()
    {
        if (currentTarget == null) return;

        UnitCategory.Category cat = DamageRules.Resolve(currentTarget.gameObject);
        float modifier            = DamageRules.Modifier(damageType, cat);
        float finalDamage         = attackDamage * modifier;

        currentTarget.TakeDamage(finalDamage);
        ShowTracer(currentTarget.transform.position);

        Debug.Log($"[MGDefense:{name}] Hit {currentTarget.name} ({cat}): " +
                  $"base {attackDamage}, {damageType} ×{modifier:F2}, final {finalDamage:F1}");

        if (currentTarget == null || !currentTarget)
            currentTarget = null;
    }

    // ------------------------------------------------------------------ //
    // Tracer — runtime LineRenderer, mirrors UnitCombat's pattern
    // ------------------------------------------------------------------ //

    private void BuildTracer()
    {
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

        Shader shader = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Unlit/Color")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Hidden/InternalErrorShader");

        Material m = new Material(shader) { color = tracerColor };
        if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, tracerColor);
        if (m.HasProperty(ColorId))     m.SetColor(ColorId,     tracerColor);

        tracer.material   = m;
        tracer.startColor = tracerColor;
        tracer.endColor   = tracerColor;
        tracer.enabled    = false;
    }

    private void ShowTracer(Vector3 targetPos)
    {
        if (tracer == null) return;

        Vector3 start = firePoint != null
            ? firePoint.position
            : (turretPivot != null
                ? turretPivot.position + Vector3.up * 0.5f
                : transform.position + Vector3.up * 1.4f);

        // Aim at chest height to feel less like shooting feet.
        Vector3 end = targetPos + Vector3.up * 1.0f;

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

    // ------------------------------------------------------------------ //
    // Gizmos
    // ------------------------------------------------------------------ //

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!debugDrawRange) return;
        UnityEditor.Handles.color = new Color(1f, 0.2f, 0.2f, 0.30f);
        UnityEditor.Handles.DrawWireDisc(transform.position, Vector3.up, attackRange);
    }
#endif
}
