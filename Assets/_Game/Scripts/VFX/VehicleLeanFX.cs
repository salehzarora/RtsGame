using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Visual body lean for vehicles — pitches the hull back under acceleration,
/// forward under braking, and rolls into turns. Reads ONLY the transform's
/// frame-to-frame motion (not NavMeshAgent), so it works the same for locally
/// driven units and remote network ghosts. Rotates the direct visual children
/// (never the gameplay root), so pathing, colliders, turret aim and network
/// sync are untouched. Purely visual, multiplayer-safe.
///
/// Attached automatically by <see cref="UnitMovement"/> for Vehicle-category
/// units; can also be added to any prefab by hand.
/// </summary>
[DisallowMultipleComponent]
public class VehicleLeanFX : MonoBehaviour
{
    [Header("Tuning")]
    [Tooltip("Degrees of pitch per m/s² of forward acceleration (nose lifts " +
             "when speeding up, dips when braking).")]
    public float pitchPerAccel = 1.1f;

    [Tooltip("Degrees of roll per (deg/s of yaw × m/s of speed)/100 — leans " +
             "into turns harder at speed.")]
    public float rollPerYawSpeed = 1.6f;

    [Tooltip("Maximum lean on either axis, degrees.")]
    public float maxLean = 6f;

    [Tooltip("How quickly the lean follows the target (higher = stiffer).")]
    public float smoothing = 6f;

    private readonly List<Transform> visuals = new List<Transform>();
    private readonly List<Quaternion> baseRot = new List<Quaternion>();

    private Vector3 lastPos;
    private float   lastYaw;
    private float   lastSpeed;
    private float   pitch, roll;

    private void Awake()
    {
        foreach (Transform child in transform)
        {
            if (child.GetComponent<Canvas>() != null) continue;
            if (child.GetComponentInChildren<Renderer>(true) == null) continue;
            visuals.Add(child);
            baseRot.Add(child.localRotation);
        }
        lastPos = transform.position;
        lastYaw = transform.eulerAngles.y;
    }

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // Frame-to-frame kinematics from the transform alone.
        Vector3 delta  = transform.position - lastPos;
        float   speed  = delta.magnitude / dt;
        float   accel  = (speed - lastSpeed) / dt;
        float   yawNow = transform.eulerAngles.y;
        float   yawRate = Mathf.DeltaAngle(lastYaw, yawNow) / dt;

        lastPos = transform.position;
        lastYaw = yawNow;
        lastSpeed = speed;

        // Targets. Note: positive accel pitches the nose UP (rotation -X).
        float targetPitch = Mathf.Clamp(-accel * pitchPerAccel, -maxLean, maxLean);
        float targetRoll  = Mathf.Clamp(yawRate * speed * 0.01f * rollPerYawSpeed, -maxLean, maxLean);

        float k = 1f - Mathf.Exp(-smoothing * dt);
        pitch = Mathf.Lerp(pitch, targetPitch, k);
        roll  = Mathf.Lerp(roll,  targetRoll,  k);

        Quaternion lean = Quaternion.Euler(pitch, 0f, roll);
        for (int i = 0; i < visuals.Count; i++)
            if (visuals[i] != null)
                visuals[i].localRotation = baseRot[i] * lean;
    }
}
