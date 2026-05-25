using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Combat behaviour for the RPG Soldier (and any future projectile-rocket
/// unit). Parallel sibling to <see cref="UnitCombat"/> — kept separate so
/// the rifle/cannon hitscan path stays untouched and rocket-specific tuning
/// (longer cooldown, projectile spawn, no tracer) doesn't bleed into it.
///
/// State machine:
///   Idle           — no target.
///   ChasingTarget  — moving toward target until inside <see cref="attackRange"/>.
///   Attacking      — stopped, facing target, firing rockets on cooldown.
///
/// Each shot spawns a <see cref="RocketProjectile"/> that handles its own
/// flight, hit detection, damage, and self-destruction. The projectile decides
/// whether to home (Aircraft category) or direct-track (everything else)
/// based on the target's <see cref="UnitCategory"/>.
///
/// Compatible with <see cref="UnitSelector"/> via the public
/// <see cref="SetTarget"/> / <see cref="ClearTarget"/> API — identical to
/// UnitCombat — so right-clicking an enemy on a selected RPG Soldier Just Works.
///
/// Setup:
///   1. Attach to a PLAYER infantry unit (alongside Health, UnitMovement,
///      SelectableUnit, UnitCategory). Created automatically by
///      Tools → RTS → Units → Create RPG Soldier Prefab.
///   2. Drop a FirePoint child Transform near the rocket tube nozzle and
///      drag it into <see cref="firePoint"/> — falls back to chest height
///      if null.
///   3. Tune ranges / damage / cooldown in the Inspector.
/// </summary>
[RequireComponent(typeof(UnitMovement))]
[RequireComponent(typeof(Health))]
public class RocketCombat : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    private enum CombatState { Idle, ChasingTarget, Attacking }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Attack Stats")]
    [Tooltip("World-unit radius within which this unit can fire on a ground or " +
             "building target.")]
    public float attackRange = 12f;

    [Tooltip("Maximum XZ range at which the unit will engage an Aircraft target. " +
             "Beyond this the unit ignores the order. Usually slightly higher than " +
             "ground range to model lock-on reach.")]
    public float antiAirRange = 14f;

    [Tooltip("Base damage per rocket, before category modifier in DamageRules. " +
             "Rocket vs Vehicle = 1.00× by default; see DamageRules table.")]
    public float attackDamage = 70f;

    [Tooltip("Seconds between rocket releases.")]
    public float attackCooldown = 2.5f;

    [Tooltip("If true, the first rocket on a freshly-acquired target fires after " +
             "firstShotDelay instead of the full attackCooldown. Makes auto-attack " +
             "feel responsive when an enemy walks into the guard radius. Subsequent " +
             "shots respect attackCooldown.")]
    public bool fireImmediatelyOnNewTarget = true;

    [Tooltip("Seconds before the first rocket on a new target. Very small values " +
             "(0.05) feel near-instant; 0 fires on the same tick the target enters " +
             "range, which can look like a teleport-shot. Only used when " +
             "fireImmediatelyOnNewTarget is true.")]
    public float firstShotDelay = 0.05f;

    [Tooltip("Optional inner dead-zone — targets closer than this are ignored so " +
             "the unit doesn't blast its own feet. 0 disables.")]
    public float minRange = 3f;

    [Tooltip("Degrees / second this unit can rotate to face its target while attacking.")]
    public float rotationSpeed = 540f;

    [Header("Projectile")]
    [Tooltip("Forward speed of the spawned rocket (world units / second).")]
    public float projectileSpeed = 16f;

    [Tooltip("Max yaw / pitch the rocket can turn per second while chasing an Aircraft. " +
             "Lower → fast jets escape; higher → rocket catches more often.")]
    public float homingTurnRateDegrees = 90f;

    [Tooltip("Rocket self-destructs after this many seconds in flight. Aircraft chases " +
             "use this to decide miss-by-expiry; ground shots almost always hit first.")]
    public float rocketLifetime = 4.5f;

    [Tooltip("Distance from the rocket to the target's pivot under which the rocket " +
             "counts as a hit. Larger = more forgiving (esp. vs aircraft).")]
    public float aircraftHitRadius = 1.8f;

    [Tooltip("Tighter hit radius for ground / building targets — they don't move much, " +
             "so we can use a small precise window.")]
    public float groundHitRadius = 0.8f;

    [Tooltip("Muzzle origin for the rocket. If null, chest height " +
             "(transform.position + Vector3.up * 1.2) is used.")]
    public Transform firePoint;

    [Header("Visual")]
    [Tooltip("Length × width × scale of the spawned rocket body. Cosmetic only — does not affect hit radius.")]
    public Vector3 rocketSize = new Vector3(0.18f, 0.18f, 0.85f);

    [Tooltip("Body and impact-flash colour for the rocket.")]
    [ColorUsage(false)] public Color rocketColor = new Color(1.00f, 0.55f, 0.10f);

    [Tooltip("Seconds the impact flash sphere stays visible.")]
    public float impactFlashDuration = 0.18f;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private CombatState  state = CombatState.Idle;
    private Health       target;
    private UnitMovement movement;
    private float        attackTimer;

    /// <summary>True from SetTarget until the first rocket on that target releases.
    /// Used to skip the wind-up cooldown when the player wants an instant first shot.</summary>
    private bool firstShotPending;

    /// <summary>True when the unit has no active target. Used by future enemy-AI scans.</summary>
    public bool IsIdle => state == CombatState.Idle;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        movement = GetComponent<UnitMovement>();
    }

    private void Update()
    {
        if (state == CombatState.Idle) return;

        if (target == null)
        {
            state = CombatState.Idle;
            return;
        }

        float dist           = Vector3.Distance(transform.position, target.transform.position);
        bool  targetIsAir    = DamageRules.Resolve(target.gameObject) == UnitCategory.Category.Aircraft;
        float effectiveRange = targetIsAir ? antiAirRange : attackRange;

        if (dist > effectiveRange)
        {
            state = CombatState.ChasingTarget;
            movement.MoveTo(target.transform.position);
            return;
        }

        // Inside the dead-zone — back off-attack but don't move. Players can
        // reposition the unit manually if they want.
        if (minRange > 0f && dist < minRange)
        {
            if (state != CombatState.Attacking)
            {
                movement.Stop();
                state = CombatState.Attacking;
            }
            FaceTarget();
            return;
        }

        if (state != CombatState.Attacking)
        {
            state = CombatState.Attacking;
            movement.Stop();
            // Honour the fast-first-shot timer SetTarget already loaded;
            // only fall back to the full cooldown wind-up if this isn't a
            // fresh acquisition.
            if (!firstShotPending)
                attackTimer = attackCooldown;
        }

        FaceTarget();

        attackTimer -= Time.deltaTime;
        if (attackTimer <= 0f)
        {
            FireRocket(targetIsAir);
            attackTimer = attackCooldown;

            if (target == null) state = CombatState.Idle;
        }
    }

    // ------------------------------------------------------------------ //
    // Public API — UnitSelector calls these
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Begin attacking <paramref name="enemyHealth"/>. Works regardless of category.
    /// When <see cref="fireImmediatelyOnNewTarget"/> is true the wind-up timer is
    /// set to <see cref="firstShotDelay"/> (typically 0.05 s) instead of the full
    /// attackCooldown, so the auto-attack controller's "enemy walked into radius"
    /// flow produces a near-instant first rocket.
    /// </summary>
    public void SetTarget(Health enemyHealth)
    {
        target           = enemyHealth;
        state            = CombatState.ChasingTarget;
        firstShotPending = fireImmediatelyOnNewTarget && enemyHealth != null;
        attackTimer      = firstShotPending ? Mathf.Max(0f, firstShotDelay) : attackCooldown;

        if (firstShotPending)
            Debug.Log($"[RPG:{name}] Target acquired — immediate fire ready.");
    }

    /// <summary>Drop the current target; the unit returns to Idle. Called when the player gives a move order.</summary>
    public void ClearTarget()
    {
        target           = null;
        firstShotPending = false;
        state            = CombatState.Idle;
    }

    // ------------------------------------------------------------------ //
    // Combat helpers
    // ------------------------------------------------------------------ //

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
    /// Spawns a <see cref="RocketProjectile"/> aimed at the current target.
    /// Aircraft → homing flight (limited turn rate + lifetime); other categories
    /// → direct flight with a higher steering rate.
    /// </summary>
    private void FireRocket(bool targetIsAir)
    {
        Vector3 origin = firePoint != null
            ? firePoint.position
            : transform.position + Vector3.up * 1.2f;

        if (firstShotPending)
        {
            Debug.Log($"[RPG:{name}] First rocket fired immediately.");
            firstShotPending = false;
        }
        else
        {
            Debug.Log($"[RPG] {name} fired rocket at {target.name}.");
        }

        // Build the visual body — same primitive-cube pattern as StrikeMissile.
        GameObject rocketGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
        rocketGO.name                 = "RPGRocket";
        rocketGO.transform.position   = origin;
        rocketGO.transform.localScale = rocketSize;
        rocketGO.layer                = 2;   // IgnoreRaycast

        Collider col = rocketGO.GetComponent<Collider>();
        if (col != null) Destroy(col);

        Renderer r = rocketGO.GetComponent<Renderer>();
        if (r != null)
        {
            Shader shader = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Standard");
            Material m = new Material(shader) { color = rocketColor };
            if (m.HasProperty("_BaseColor"))     m.SetColor("_BaseColor",     rocketColor);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", rocketColor * 1.4f);
            }
            r.sharedMaterial    = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows    = false;
        }

        float hitRadius = targetIsAir ? aircraftHitRadius : groundHitRadius;

        RocketProjectile rocket = rocketGO.AddComponent<RocketProjectile>();
        rocket.Launch(origin, target,
                      homing: targetIsAir,
                      damage: attackDamage,
                      projectileSpeed: projectileSpeed,
                      turnRate: homingTurnRateDegrees,
                      maxLifetime: rocketLifetime,
                      hitRadiusXZ: hitRadius,
                      color: rocketColor,
                      flashDuration: impactFlashDuration);
    }

    // ------------------------------------------------------------------ //
    // Gizmos — range visualisation in Scene view
    // ------------------------------------------------------------------ //

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        UnityEditor.Handles.color = new Color(1f, 0.4f, 0.1f, 0.3f);
        UnityEditor.Handles.DrawWireDisc(transform.position, Vector3.up, attackRange);
        UnityEditor.Handles.color = new Color(0.5f, 0.8f, 1f, 0.3f);
        UnityEditor.Handles.DrawWireDisc(transform.position, Vector3.up, antiAirRange);
    }
#endif
}
