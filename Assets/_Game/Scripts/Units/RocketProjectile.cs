using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Self-driving rocket fired by an <see cref="RocketCombat"/> component (RPG
/// Soldier and any future rocket unit). Two flavours of behaviour:
///
///   • Ground / building target → direct flight toward the target's live
///     position. Steers gently each frame so a target that walks a little
///     still gets hit, but no aggressive homing. If the target dies mid-flight
///     the rocket flies on to the last known position and detonates without
///     damage.
///
///   • Aircraft target (isHoming = true) → limited-turn-rate homing chase
///     with a finite lifetime. If the rocket can't catch the target before
///     <see cref="lifetime"/> expires, it self-destructs as a miss. Fast
///     aircraft naturally evade because the turn rate ceiling can't keep up;
///     slow or landing aircraft are easy hits.
///
/// Damage is resolved through <see cref="DamageRules"/> so the Rocket damage
/// type is correctly scaled against the target category. Spawns a brief
/// impact flash and destroys itself either on hit or on lifetime expiry.
///
/// No physics, no NavMesh, no colliders. Layer is IgnoreRaycast so the
/// rocket never blocks gameplay raycasts.
///
/// Setup is automatic — <see cref="RocketCombat"/> builds the GameObject
/// procedurally and calls <see cref="Launch"/> once. Do NOT add this
/// component to a prefab by hand.
/// </summary>
public class RocketProjectile : MonoBehaviour
{
    private const int IgnoreRaycastLayer = 2;

    // ------------------------------------------------------------------ //
    // Launch parameters — captured in Launch()
    // ------------------------------------------------------------------ //

    private Health  targetHealth;
    private bool    isHoming;
    private float   damageBase;
    private float   speed;
    private float   turnRateDegrees;
    private float   lifetime;
    private float   hitRadius;
    private float   impactFlashDuration;
    private Color   flashColor;

    /// <summary>Snapshotted at launch — used as the fallback impact point if the target dies mid-flight.</summary>
    private Vector3 lastKnownTargetPosition;

    private float spawnTime;
    private bool  launched;
    private bool  loggedTrackingOnce;

    // ------------------------------------------------------------------ //
    // Public — called once after instantiate
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Begin flying from <paramref name="startPos"/> toward <paramref name="target"/>.
    /// For <paramref name="homing"/> = true the rocket steers each frame at up
    /// to <paramref name="turnRate"/> deg/s; for false it still tracks but at
    /// a much higher rate so ground targets that strafe lightly still get hit.
    /// </summary>
    public void Launch(Vector3 startPos, Health target, bool homing,
                       float damage, float projectileSpeed, float turnRate,
                       float maxLifetime, float hitRadiusXZ,
                       Color color, float flashDuration)
    {
        transform.position  = startPos;
        targetHealth        = target;
        isHoming            = homing;
        damageBase          = damage;
        speed               = Mathf.Max(0.01f, projectileSpeed);
        turnRateDegrees     = Mathf.Max(0f, turnRate);
        lifetime            = Mathf.Max(0.1f, maxLifetime);
        hitRadius           = Mathf.Max(0.05f, hitRadiusXZ);
        impactFlashDuration = flashDuration;
        flashColor          = color;

        // Snapshot the launch-time target position. Used if the target dies
        // mid-flight (we fly on to where they were and explode).
        lastKnownTargetPosition = (target != null)
            ? target.transform.position
            : startPos + transform.forward * 2f;

        // Initial orientation along the launch direction toward the target.
        Vector3 initialAim = lastKnownTargetPosition - startPos;
        if (initialAim.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(initialAim);

        spawnTime          = Time.time;
        launched           = true;
        loggedTrackingOnce = false;
    }

    // ------------------------------------------------------------------ //
    // Flight
    // ------------------------------------------------------------------ //

    private void Update()
    {
        if (!launched) return;

        // Lifetime expired — count as a miss and clean up.
        if (Time.time - spawnTime > lifetime)
        {
            if (isHoming) Debug.Log("[Rocket] Missed aircraft — lifetime expired.");
            DetonateAt(transform.position, applyDamage: false);
            return;
        }

        // Keep the last-known-position fresh while the target is alive. After
        // it dies we keep flying toward the snapshot.
        bool targetAlive = targetHealth != null;
        Vector3 aimPoint = targetAlive
            ? targetHealth.transform.position
            : lastKnownTargetPosition;

        if (targetAlive) lastKnownTargetPosition = aimPoint;

        // Hit check — XZ distance to the current aim point (target's live
        // position when alive; snapshot when dead). For aircraft this is also
        // a Y-tolerant proximity check via the same hitRadius.
        float dist = Vector3.Distance(transform.position, aimPoint);
        if (dist <= hitRadius)
        {
            DetonateAt(aimPoint, applyDamage: targetAlive);
            return;
        }

        // Steering: rotate the forward direction toward the aim point at the
        // configured turn rate. isHoming aircraft use the spec value (90 deg/s
        // by default); ground rockets use a faster rate so light strafing still
        // resolves to a hit.
        Vector3 toAim = aimPoint - transform.position;
        if (toAim.sqrMagnitude > 0.0001f)
        {
            Quaternion want = Quaternion.LookRotation(toAim.normalized);
            float rate = isHoming ? turnRateDegrees : Mathf.Max(turnRateDegrees, 360f);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, want, rate * Time.deltaTime);
        }

        // One-shot "tracking" log so a homing chase shows up in the console
        // without per-frame spam.
        if (isHoming && targetAlive && !loggedTrackingOnce)
        {
            Debug.Log($"[Rocket] Tracking aircraft '{targetHealth.name}'.");
            loggedTrackingOnce = true;
        }

        // Advance along current heading.
        transform.position += transform.forward * speed * Time.deltaTime;
    }

    // ------------------------------------------------------------------ //
    // Impact / miss
    // ------------------------------------------------------------------ //

    private void DetonateAt(Vector3 worldPos, bool applyDamage)
    {
        transform.position = worldPos;

        if (applyDamage && targetHealth != null)
        {
            UnitCategory.Category cat = DamageRules.Resolve(targetHealth.gameObject);
            float modifier            = DamageRules.Modifier(DamageType.Rocket, cat);
            float finalDamage         = damageBase * modifier;

            targetHealth.TakeDamage(finalDamage);

            Debug.Log($"[Rocket] Hit {targetHealth.name} ({cat}) for {finalDamage:F1} damage " +
                      $"(base {damageBase}, Rocket ×{modifier:F2}).");
        }
        else if (!applyDamage && !isHoming)
        {
            Debug.Log("[Rocket] Impacted — target already destroyed (no damage).");
        }

        SpawnImpactFlash(worldPos);
        Destroy(gameObject);
    }

    private void SpawnImpactFlash(Vector3 worldPos)
    {
        if (impactFlashDuration <= 0f) return;

        GameObject flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flash.name = "RocketImpactFlash";
        flash.transform.position   = worldPos;
        flash.transform.localScale = Vector3.one * 1.3f;
        flash.layer = IgnoreRaycastLayer;

        Collider col = flash.GetComponent<Collider>();
        if (col != null) Destroy(col);

        Renderer r = flash.GetComponent<Renderer>();
        if (r != null)
        {
            Shader shader = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Standard");

            Material m = new Material(shader) { color = flashColor };
            if (m.HasProperty("_BaseColor"))     m.SetColor("_BaseColor",     flashColor);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", flashColor * 1.5f);
            }
            r.sharedMaterial    = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows    = false;
        }

        Destroy(flash, impactFlashDuration);
    }
}
