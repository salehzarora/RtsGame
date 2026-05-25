using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Long-range artillery combat for a truck-mounted missile launcher. Parallel
/// sibling to <see cref="UnitCombat"/> and <see cref="RocketCombat"/>:
/// exposes the same <see cref="SetTarget"/> / <see cref="ClearTarget"/> /
/// <see cref="IsIdle"/> API so the existing manual-attack flow in
/// <see cref="UnitSelector"/> and the auto-acquire pass in
/// <see cref="GroundAutoAttackController"/> can drive it.
///
/// State machine:
///   Idle             — no target.
///   ChasingTarget    — target outside <see cref="attackRange"/>; close the
///                      distance via <see cref="UnitMovement"/>.
///   Repositioning    — target INSIDE <see cref="minRange"/>; back away along
///                      the vector from target → self until we're at least
///                      <see cref="repositionBuffer"/> outside minRange.
///   Attacking        — target inside [minRange, attackRange]; stop, face,
///                      fire on cooldown.
///
/// Key differences vs UnitCombat:
///   • Explicit MINIMUM firing range. Targets too close trigger a back-up
///     instead of point-blank fire.
///   • Fires a parabolic-arc <see cref="MissileProjectile"/> with splash
///     damage — not hitscan, not direct-flight rocket.
///   • REJECTS Aircraft targets. Manual right-click on an aircraft logs and
///     no-ops; auto-acquire never picks one (see GroundAutoAttackController).
///   • Stops while firing — never fires while moving (artillery stance).
///
/// Setup:
///   Add to the missile launcher root alongside Health, UnitMovement,
///   NavMeshAgent, UnitCategory.Vehicle. The <c>CreateMissileLauncherPrefab</c>
///   editor tool wires this automatically.
/// </summary>
[RequireComponent(typeof(UnitMovement))]
[RequireComponent(typeof(Health))]
public class MissileLauncherCombat : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    private enum CombatState { Idle, ChasingTarget, Repositioning, Attacking }

    // ------------------------------------------------------------------ //
    // Inspector — combat stats
    // ------------------------------------------------------------------ //

    [Header("Attack Stats")]
    [Tooltip("Maximum firing range (world units). Should be 2-3× the range of " +
             "infantry and tanks for the artillery feel.")]
    public float attackRange = 30f;

    [Tooltip("Minimum firing range. Targets closer than this trigger a back-up " +
             "manoeuvre instead of a point-blank shot.")]
    public float minRange = 8f;

    [Tooltip("Extra distance beyond minRange the launcher backs up to before " +
             "re-engaging. Stops oscillation right at the boundary.")]
    public float repositionBuffer = 4f;

    [Tooltip("Base damage per missile, BEFORE the DamageRules Artillery modifier " +
             "(0.65 vs Infantry / 1.00 vs Vehicle / 1.10 vs Building / 0.00 vs Aircraft).")]
    public float attackDamage = 90f;

    [Tooltip("Seconds between salvos. Long on purpose so the launcher feels like " +
             "support, not DPS.")]
    public float attackCooldown = 4.5f;

    [Tooltip("Splash radius applied at impact via Physics.OverlapSphere. " +
             "Small-medium so a group of infantry gets punished but one shot " +
             "doesn't wipe a whole army.")]
    public float splashRadius = 3.5f;

    [Tooltip("Damage type used for the DamageRules lookup. Default Artillery: " +
             "0.65/1.00/1.10/0.00 across Infantry/Vehicle/Building/Aircraft.")]
    public DamageType damageType = DamageType.Artillery;

    // ------------------------------------------------------------------ //
    // Inspector — aim
    // ------------------------------------------------------------------ //

    [Header("Aim")]
    [Tooltip("Body yaw rotation speed (degrees/sec) while attacking.")]
    public float rotationSpeed = 90f;

    [Tooltip("Optional launcher pivot — if assigned, this child tilts up by " +
             "launcherPitchDegrees during the firing animation.")]
    public Transform launcherPivot;

    [Tooltip("Resting pitch (degrees) for the launcher pivot at rest. Negative " +
             "values mean the rack lies flat against the chassis.")]
    public float launcherRestPitch = 0f;

    [Tooltip("Tilt-up angle (degrees) applied to launcherPivot while firing.")]
    public float launcherFiringPitch = -25f;

    [Tooltip("Speed (degrees/sec) the pivot slews between rest and firing pitch.")]
    public float launcherPitchSpeed = 60f;

    // ------------------------------------------------------------------ //
    // Inspector — projectile
    // ------------------------------------------------------------------ //

    [Header("Projectile")]
    [Tooltip("Seconds the missile spends in flight. Slower = more telegraphed; " +
             "matches the artillery feel.")]
    public float missileTravelTime = 1.4f;

    [Tooltip("Peak height (world units) of the parabolic arc above the launch / " +
             "impact midpoint. Bigger = more dramatic arc.")]
    public float missileArcHeight = 8f;

    [Tooltip("Visual size of the missile body (cube primitive).")]
    public Vector3 missileSize = new Vector3(0.30f, 0.30f, 1.40f);

    [Tooltip("Tint for the missile body + impact flash sphere.")]
    [ColorUsage(false)] public Color missileColor = new Color(0.85f, 0.85f, 0.85f);

    [Tooltip("Seconds the impact flash sphere stays visible after detonation.")]
    public float impactFlashDuration = 0.35f;

    [Tooltip("Muzzle origin for the missile. If null, the launcher's chest height " +
             "(transform.position + Vector3.up * 1.6) is used instead.")]
    public Transform firePoint;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private CombatState  state = CombatState.Idle;
    private Health       target;
    private UnitMovement movement;
    private Health       ownHealth;
    private float        attackTimer;
    private float        pitchTimer;          // hold the firing pitch briefly after launch

    /// <summary>True when the launcher has no active target. Used by
    /// <see cref="GroundAutoAttackController"/> to detect a clean idle state.</summary>
    public bool IsIdle => state == CombatState.Idle;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        movement  = GetComponent<UnitMovement>();
        ownHealth = GetComponent<Health>();

        if (launcherPivot != null)
            launcherPivot.localRotation = Quaternion.Euler(launcherRestPitch, 0f, 0f);
    }

    private void Update()
    {
        TickPivot();

        if (state == CombatState.Idle) return;

        // Target died / was cleared — return to idle.
        if (target == null)
        {
            state = CombatState.Idle;
            return;
        }

        // Defensive: a target may change category mid-engagement (rare, but
        // possible if a future patch lets things morph). Re-validate.
        UnitCategory.Category cat = DamageRules.Resolve(target.gameObject);
        if (cat == UnitCategory.Category.Aircraft)
        {
            Debug.Log("[MissileLauncher] Target became aircraft — clearing.");
            ClearTarget();
            return;
        }

        float dist = Vector3.Distance(transform.position, target.transform.position);

        // Out of max range → close the distance.
        if (dist > attackRange)
        {
            state = CombatState.ChasingTarget;
            movement.MoveTo(target.transform.position);
            return;
        }

        // Inside minimum range → back away.
        if (dist < minRange)
        {
            EnterRepositioning();
            return;
        }

        // In the firing band → stop, face, fire.
        if (state != CombatState.Attacking)
        {
            state = CombatState.Attacking;
            movement.Stop();
            attackTimer = attackCooldown;     // brief wind-up before the first salvo
        }

        FaceTarget();

        attackTimer -= Time.deltaTime;
        if (attackTimer <= 0f)
        {
            FireMissile();
            attackTimer = attackCooldown;

            if (target == null) state = CombatState.Idle;
        }
    }

    // ------------------------------------------------------------------ //
    // Public API — matches UnitCombat / RocketCombat shape
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Begin attacking <paramref name="enemyHealth"/>. Aircraft targets are
    /// rejected with a console log — the launcher cannot engage air at all.
    /// </summary>
    public void SetTarget(Health enemyHealth)
    {
        if (enemyHealth == null) return;

        UnitCategory.Category cat = DamageRules.Resolve(enemyHealth.gameObject);
        if (cat == UnitCategory.Category.Aircraft)
        {
            Debug.Log("[MissileLauncher] No valid ground target — refusing aircraft order.");
            return;
        }

        target      = enemyHealth;
        state       = CombatState.ChasingTarget;
        attackTimer = attackCooldown;
    }

    /// <summary>Drop the current target and return to Idle.</summary>
    public void ClearTarget()
    {
        target = null;
        state  = CombatState.Idle;
    }

    // ------------------------------------------------------------------ //
    // States
    // ------------------------------------------------------------------ //

    private void EnterRepositioning()
    {
        if (state != CombatState.Repositioning)
        {
            Debug.Log($"[MissileLauncher] Target too close — repositioning.");
            state = CombatState.Repositioning;
        }

        // Back away along the vector from target → self, by (minRange + buffer).
        Vector3 awayDir = transform.position - target.transform.position;
        awayDir.y = 0f;
        if (awayDir.sqrMagnitude < 0.0001f)
        {
            // Target sitting on top of us (degenerate); pick the unit's back arc.
            awayDir = -transform.forward;
            awayDir.y = 0f;
        }

        Vector3 dest = target.transform.position
                     + awayDir.normalized * (minRange + repositionBuffer);

        movement.MoveTo(dest);
    }

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
    /// Lazily slews the launcher pivot between rest and firing pitch. While
    /// the attack timer is winding down ahead of a shot we tilt UP; after the
    /// shot the timer holds the elevated pose for a few moments via pitchTimer.
    /// </summary>
    private void TickPivot()
    {
        if (launcherPivot == null) return;

        bool wantUp = state == CombatState.Attacking || pitchTimer > 0f;
        float wantPitch = wantUp ? launcherFiringPitch : launcherRestPitch;

        Quaternion desired = Quaternion.Euler(wantPitch, 0f, 0f);
        launcherPivot.localRotation = Quaternion.RotateTowards(
            launcherPivot.localRotation, desired, launcherPitchSpeed * Time.deltaTime);

        if (pitchTimer > 0f) pitchTimer -= Time.deltaTime;
    }

    // ------------------------------------------------------------------ //
    // Firing
    // ------------------------------------------------------------------ //

    private void FireMissile()
    {
        if (target == null) return;

        Vector3 origin = firePoint != null
            ? firePoint.position
            : transform.position + Vector3.up * 1.6f;

        // Snapshot the impact at the target's CURRENT ground position so the
        // missile feels committed — moving targets can dodge by relocating
        // during the flight time, which is a feature of artillery.
        Vector3 impactPoint = target.transform.position;

        GameObject missileGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
        missileGO.name                 = "ArtilleryMissile";
        missileGO.transform.position   = origin;
        missileGO.transform.localScale = missileSize;
        missileGO.layer                = 2;   // IgnoreRaycast

        Collider col = missileGO.GetComponent<Collider>();
        if (col != null) Destroy(col);

        Renderer r = missileGO.GetComponent<Renderer>();
        if (r != null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard")
                         ?? Shader.Find("Sprites/Default");
            Material m = new Material(shader) { color = missileColor };
            if (m.HasProperty("_BaseColor"))     m.SetColor("_BaseColor",     missileColor);
            r.sharedMaterial    = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows    = false;
        }

        MissileProjectile proj = missileGO.AddComponent<MissileProjectile>();
        proj.Launch(
            origin:        origin,
            impactPoint:   impactPoint,
            primaryTarget: target,
            team:          ownHealth != null ? ownHealth.team : Health.Team.Player,
            damage:        attackDamage,
            splash:        splashRadius,
            travelTime:    missileTravelTime,
            arc:           missileArcHeight,
            color:         missileColor,
            flashDuration: impactFlashDuration);

        // Hold the firing pitch briefly after launch so the rack visibly tracks
        // the salvo before settling back to rest.
        pitchTimer = 0.5f;

        Debug.Log($"[MissileLauncher] Fired missile at {target.name}.");
    }

    // ------------------------------------------------------------------ //
    // Gizmos
    // ------------------------------------------------------------------ //

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        // Attack range — large red ring.
        UnityEditor.Handles.color = new Color(1f, 0.25f, 0.10f, 0.30f);
        UnityEditor.Handles.DrawWireDisc(transform.position, Vector3.up, attackRange);

        // Minimum range — small dead-zone ring inside.
        UnityEditor.Handles.color = new Color(1f, 0.85f, 0.10f, 0.30f);
        UnityEditor.Handles.DrawWireDisc(transform.position, Vector3.up, minRange);

        // Splash radius preview at the unit's own feet (just for size sense).
        UnityEditor.Handles.color = new Color(0.20f, 0.85f, 1f, 0.20f);
        UnityEditor.Handles.DrawWireDisc(transform.position, Vector3.up, splashRadius);
    }
#endif
}
