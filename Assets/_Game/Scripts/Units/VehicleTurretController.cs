using UnityEngine;

/// <summary>
/// Independent turret rotation for a vehicle. The vehicle body keeps driving
/// via NavMeshAgent / UnitMovement, while this component rotates a child
/// <see cref="turret"/> Transform on the Y axis toward a designated target.
/// <see cref="UnitCombat"/> reads <see cref="IsAimed"/> as a fire gate so
/// rounds don't spawn while the cannon is still slewing.
///
/// Hierarchy convention (set up by Tools → RTS → Vehicles → Repair Vehicle Turrets):
///   VehicleRoot                  (NavMeshAgent, UnitMovement, UnitCombat, this)
///     └── Turret                 (the rotating sub-piece — assigned to <see cref="turret"/>)
///           ├── Cannon / Gun     (visual barrel, parented to Turret so it rotates with it)
///           └── FirePoint        (Transform at muzzle tip — assigned to <see cref="firePoint"/>)
///
/// What it does NOT do:
///   • Tilt the barrel (pitch). Y-axis yaw only; barrel pitch is reserved for
///     a later milestone.
///   • Rotate the body. The vehicle's facing is owned by UnitMovement /
///     NavMeshAgent's own angular rotation.
///   • Fire the weapon. UnitCombat owns trigger + cooldown; it just asks
///     <see cref="IsAimed"/> before spawning a shot.
///   • Apply itself to aircraft (those use AirUnitController + AircraftWeapon
///     for their own pivot-and-fire flow).
/// </summary>
[DisallowMultipleComponent]
public class VehicleTurretController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("References")]
    [Tooltip("The rotating turret child. Yaw-only rotation is applied here; " +
             "the body Transform is left alone for NavMeshAgent.")]
    public Transform turret;

    [Tooltip("Muzzle origin for shots / tracers. Should be a child of the turret " +
             "so it tracks the turret's facing. UnitCombat will prefer this over " +
             "its own firePoint when this controller is present.")]
    public Transform firePoint;

    [Header("Aim")]
    [Tooltip("Yaw rotation speed (degrees / second) for the turret. " +
             "120 = tank, 200+ = light vehicle.")]
    public float turretTurnSpeed = 120f;

    [Tooltip("Angle (degrees) within which the turret counts as aimed. The combat " +
             "component holds fire until this gate is satisfied. Wider = snappier " +
             "engagements; tighter = more cinematic aim settle.")]
    public float aimToleranceDegrees = 10f;

    [Header("Idle")]
    [Tooltip("If true, the turret slowly rotates back to face the vehicle's forward " +
             "direction when there's no target. If false, it holds the last aim.")]
    public bool returnToForwardWhenIdle = true;

    [Tooltip("Yaw speed (degrees / second) used when returning to forward. " +
             "Usually slower than turretTurnSpeed for a relaxed-idle feel.")]
    public float idleReturnSpeed = 60f;

    // ------------------------------------------------------------------ //
    // Public runtime state
    // ------------------------------------------------------------------ //

    /// <summary>True while the turret's current yaw is within <see cref="aimToleranceDegrees"/>
    /// of the aim direction AND a target is active. UnitCombat reads this each frame.</summary>
    public bool IsAimed { get; private set; }

    public bool HasTurret => turret != null;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private Transform aimTarget;

    // ------------------------------------------------------------------ //
    // Public API — called by UnitCombat
    // ------------------------------------------------------------------ //

    /// <summary>Begin tracking <paramref name="target"/>. Pass null to release the turret.</summary>
    public void AimAt(Transform target) => aimTarget = target;

    public void ClearAim()
    {
        aimTarget = null;
        IsAimed   = false;
    }

    // ------------------------------------------------------------------ //
    // Update — yaw the turret toward the aim point
    // ------------------------------------------------------------------ //

    private void LateUpdate()
    {
        if (turret == null) return;

        if (aimTarget != null)
        {
            Vector3 to = aimTarget.position - turret.position;
            to.y = 0f;
            if (to.sqrMagnitude < 0.0001f) { IsAimed = false; return; }

            Quaternion want = Quaternion.LookRotation(to.normalized, Vector3.up);
            turret.rotation = Quaternion.RotateTowards(
                turret.rotation, want, turretTurnSpeed * Time.deltaTime);

            float angle = Quaternion.Angle(turret.rotation, want);
            IsAimed = angle <= aimToleranceDegrees;
            return;
        }

        // Idle path — gently return to body-forward, or hold last aim.
        IsAimed = false;
        if (!returnToForwardWhenIdle) return;

        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) return;

        Quaternion idle = Quaternion.LookRotation(fwd.normalized, Vector3.up);
        turret.rotation = Quaternion.RotateTowards(
            turret.rotation, idle, idleReturnSpeed * Time.deltaTime);
    }
}
