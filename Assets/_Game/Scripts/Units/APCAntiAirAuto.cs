using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Short-range autonomous anti-air for the APC. Independent of the primary
/// <see cref="UnitCombat"/> (which handles ground combat with Bullet damage):
/// this component scans only for hostile Aircraft and fires a hitscan tracer
/// when one enters <see cref="aaRange"/>. The two weapons fire concurrently
/// — primary MG keeps engaging ground targets while AA opportunistically
/// chips passing aircraft.
///
/// Why a separate component:
///   • UnitCombat is single-weapon and team-aware via DamageType. Layering a
///     second weapon on top would require restructuring the state machine.
///   • Keeping AA orthogonal means the existing GroundAutoAttackController
///     keeps working unchanged for the APC's primary MG (Bullet damage type
///     auto-skips Aircraft via CanEngageCategory).
///   • Manual right-click attacks still route through UnitCombat; this
///     component never receives manual orders.
///
/// Setup:
///   Add to a unit alongside Health, NavMeshAgent. The
///   <c>CreateAPCPrefab</c> editor tool wires it automatically.
///
/// What this script intentionally does NOT do:
///   • Engage ground targets. The scan filters strictly to
///     UnitCategory.Aircraft.
///   • Move the unit. Pure stationary-while-firing scanner; the chassis
///     keeps doing whatever UnitMovement / NavMeshAgent / UnitCombat says.
///   • Inhibit the primary weapon. Both can shoot in the same tick.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Health))]
public class APCAntiAirAuto : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector — combat stats
    // ------------------------------------------------------------------ //

    [Header("Anti-Air Stats")]
    [Tooltip("Maximum 3D distance at which the AA mount can engage aircraft. " +
             "Short on purpose — the APC is a support vehicle, not a dedicated " +
             "anti-air system.")]
    public float aaRange = 10f;

    [Tooltip("Base damage per AA burst, BEFORE the DamageRules MachineGun modifier " +
             "(0.55× vs Aircraft).")]
    public float aaDamage = 40f;

    [Tooltip("Seconds between AA bursts.")]
    public float aaCooldown = 1.3f;

    [Tooltip("Damage type used for the DamageRules lookup. MachineGun's 0.55× vs " +
             "Aircraft modifier is the intended balance (decent chip damage). Other " +
             "categories are never hit because the scan filters to Aircraft only.")]
    public DamageType aaDamageType = DamageType.MachineGun;

    // ------------------------------------------------------------------ //
    // Inspector — scanning
    // ------------------------------------------------------------------ //

    [Header("Scanning")]
    [Tooltip("Seconds between aircraft scans. Smaller = more responsive, larger = cheaper.")]
    public float scanInterval = 0.3f;

    [Tooltip("Layers scanned for aircraft. Leave empty to auto-resolve the 'Unit' layer. " +
             "Aircraft live on the Unit layer in this project; the category filter below " +
             "narrows the hit list further.")]
    public LayerMask targetLayerMask;

    [Tooltip("OverlapSphere buffer size. Overflowing scans are truncated safely.")]
    public int scanBufferSize = 16;

    // ------------------------------------------------------------------ //
    // Inspector — visual
    // ------------------------------------------------------------------ //

    [Header("Aim + Tracer")]
    [Tooltip("Optional muzzle origin for tracers. If null, the AA shot originates " +
             "at chest height (transform.position + Vector3.up * 1.6).")]
    public Transform firePoint;

    [Tooltip("Tracer color. Cyan-tinted by default to read distinct from the primary " +
             "MG's yellow tracer.")]
    [ColorUsage(false)] public Color tracerColor = new Color(0.55f, 0.9f, 1.0f);

    [Tooltip("Seconds the tracer line stays visible. 0 disables the tracer.")]
    public float tracerDuration = 0.06f;

    [Tooltip("World-unit thickness of the tracer line. 0 disables the tracer.")]
    public float tracerWidth = 0.06f;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private Health     ownHealth;
    private Collider[] scanBuffer;
    private float      scanTimer;
    private float      attackTimer;

    private LineRenderer tracer;
    private float        tracerTimer;

    private bool warnedNoLayer;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId     = Shader.PropertyToID("_Color");

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        ownHealth = GetComponent<Health>();

        if (targetLayerMask.value == 0)
        {
            int unitLayer = LayerMask.NameToLayer("Unit");
            targetLayerMask = unitLayer >= 0 ? (1 << unitLayer) : ~0;
        }

        scanBuffer = new Collider[Mathf.Max(4, scanBufferSize)];

        BuildTracer();

        // Stagger first scan so several APCs placed at once don't all
        // OverlapSphere on the same frame.
        scanTimer = Random.Range(0f, scanInterval);
    }

    private void Update()
    {
        TickTracer();

        if (ownHealth == null) return;

        // Tick attack cooldown regardless of whether we have a target so the
        // first shot on a fresh engagement isn't delayed by a stale timer.
        if (attackTimer > 0f) attackTimer -= Time.deltaTime;

        scanTimer -= Time.deltaTime;
        if (scanTimer > 0f) return;
        scanTimer = scanInterval;

        Health aircraft = FindNearestHostileAircraft();
        if (aircraft == null) return;
        if (attackTimer > 0f) return;

        FireAt(aircraft);
        attackTimer = aaCooldown;
    }

    // ------------------------------------------------------------------ //
    // Aircraft acquisition — Aircraft category + opposing team only
    // ------------------------------------------------------------------ //

    private Health FindNearestHostileAircraft()
    {
        int count = Physics.OverlapSphereNonAlloc(
            transform.position, aaRange, scanBuffer, targetLayerMask.value);

        Health.Team hostileTeam = ownHealth.team == Health.Team.Player
            ? Health.Team.Enemy
            : Health.Team.Player;

        Health best     = null;
        float  bestDist = float.PositiveInfinity;

        for (int i = 0; i < count; i++)
        {
            Collider c = scanBuffer[i];
            if (c == null) continue;

            Health h = c.GetComponent<Health>() ?? c.GetComponentInParent<Health>();
            if (h == null || h == ownHealth) continue;
            if (h.team != hostileTeam) continue;

            UnitCategory.Category cat = DamageRules.Resolve(h.gameObject);
            if (cat != UnitCategory.Category.Aircraft) continue;

            float d = Vector3.Distance(transform.position, h.transform.position);
            if (d > aaRange) continue;
            if (d >= bestDist) continue;

            bestDist = d;
            best     = h;
        }

        return best;
    }

    // ------------------------------------------------------------------ //
    // Firing
    // ------------------------------------------------------------------ //

    private void FireAt(Health aircraft)
    {
        UnitCategory.Category cat = DamageRules.Resolve(aircraft.gameObject);
        float modifier            = DamageRules.Modifier(aaDamageType, cat);
        float finalDamage         = aaDamage * modifier;

        // Modifier could theoretically be 0 (defensive); skip the damage call
        // but still show the tracer so the AA visibly "tried".
        if (finalDamage > 0f)
            aircraft.TakeDamage(finalDamage);

        ShowTracer(aircraft.transform.position);

        Debug.Log($"[APC-AA:{name}] Fired at {aircraft.name} for {finalDamage:F1} damage " +
                  $"({aaDamageType} ×{modifier:F2}).");
    }

    // ------------------------------------------------------------------ //
    // Tracer — mirrors UnitCombat's pattern
    // ------------------------------------------------------------------ //

    private void BuildTracer()
    {
        if (tracerWidth <= 0f || tracerDuration <= 0f) return;

        GameObject tg = new GameObject("AATracer");
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
            : transform.position + Vector3.up * 1.6f;

        tracer.SetPosition(0, start);
        tracer.SetPosition(1, targetPos);
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
        UnityEditor.Handles.color = new Color(0.4f, 0.7f, 1f, 0.25f);
        UnityEditor.Handles.DrawWireDisc(transform.position, Vector3.up, aaRange);
    }
#endif
}
