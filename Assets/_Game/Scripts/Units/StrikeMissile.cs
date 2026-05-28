using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Self-driving air-to-ground missile projectile spawned by AirUnitController.
/// The missile travels in a straight line from a launch point to a snapshotted
/// target position. On arrival it applies damage to the target Health (using
/// DamageRules), spawns a brief impact flash, then destroys itself.
///
/// Lifecycle:
///   - Spawned by AirUnitController.FireMissile.
///   - Caller invokes Launch(...) once. After that the missile manages itself.
///   - On impact the missile and its trail GameObject are destroyed.
///
/// The missile carries a snapshot of the target's position at launch. If the
/// target moves before impact the missile still lands at the snapshotted
/// position (the damage call still resolves against the live Health if the
/// target is alive). This is deliberate — a fire-and-forget feel matches the
/// "jet fires past target" gameplay better than perfect homing.
///
/// No physics, no NavMesh, no colliders. Layer is IgnoreRaycast so the
/// missile never blocks gameplay raycasts.
/// </summary>
public class StrikeMissile : MonoBehaviour
{
    private const int IgnoreRaycastLayer = 2;

    // Launch parameters — captured in Launch()
    private Vector3   endPos;
    private Health    targetHealth;
    private float     damageBase;
    private DamageType damageType;
    private float     projectileSpeed;
    private float     impactFlashDuration;
    private Color     flashColor;

    private bool launched;

    // ------------------------------------------------------------------ //
    // Public — called once after instantiate
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Begin flying from <paramref name="startPos"/> to a snapshot of
    /// <paramref name="target"/>'s position. Damage is applied at impact via
    /// <see cref="DamageRules.Modifier"/>. Safe to call when target is null —
    /// the missile then just flies to the last known point and explodes
    /// without damage.
    /// </summary>
    public void Launch(Vector3 startPos, Health target, float damage, DamageType type,
                       float speed, float flashDuration, Color flash)
    {
        transform.position = startPos;
        targetHealth        = target;
        damageBase          = damage;
        damageType          = type;
        projectileSpeed     = Mathf.Max(0.01f, speed);
        impactFlashDuration = flashDuration;
        flashColor          = flash;

        // Snapshot the impact point at the target's chest height. If target
        // is gone, drop on the missile's start position (rare — the caller
        // should null-check before spawning, but be defensive).
        endPos = (target != null)
            ? target.transform.position + Vector3.up * 0.5f
            : startPos;

        // Orient the missile body along its travel vector.
        Vector3 dir = endPos - startPos;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir);

        launched = true;
    }

    /// <summary>
    /// VISUAL-ONLY launch: flies <paramref name="startPos"/> → <paramref name="endPos"/>
    /// and flashes on arrival, but applies NO damage (<see cref="targetHealth"/>
    /// stays null, so <see cref="Impact"/> skips the TakeDamage branch). Spawned
    /// on NON-OWNER clients from the AircraftFired network event so remote players
    /// see the strike — the owner's authoritative missile is what actually deals
    /// damage. <paramref name="endPos"/> is the owner's snapshot impact point, so
    /// the visual lands where the real missile did even if the target has since
    /// died/moved.
    /// </summary>
    public void LaunchVisual(Vector3 startPos, Vector3 endPos, float speed,
                             float flashDuration, Color flash)
    {
        transform.position  = startPos;
        targetHealth        = null;     // ← guarantees Impact() deals no damage
        damageBase          = 0f;
        damageType          = DamageType.Missile;
        projectileSpeed     = Mathf.Max(0.01f, speed);
        impactFlashDuration = flashDuration;
        flashColor          = flash;
        this.endPos         = endPos;

        Vector3 dir = endPos - startPos;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir);

        launched = true;
    }

    // ------------------------------------------------------------------ //
    // Flight
    // ------------------------------------------------------------------ //

    private void Update()
    {
        if (!launched) return;

        Vector3 dir   = endPos - transform.position;
        float   dist  = dir.magnitude;
        float   step  = projectileSpeed * Time.deltaTime;

        // Don't overshoot on the final tick.
        if (step >= dist)
        {
            transform.position = endPos;
            Impact();
            return;
        }

        transform.position += dir.normalized * step;

        // Hold the orientation tangent to the trajectory — straight-line
        // motion means the rotation is essentially fixed, but if we add an
        // arc later this still reads as "missile pointing forward".
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir);
    }

    // ------------------------------------------------------------------ //
    // Impact
    // ------------------------------------------------------------------ //

    private void Impact()
    {
        if (targetHealth != null)
        {
            UnitCategory.Category cat = DamageRules.Resolve(targetHealth.gameObject);
            float modifier            = DamageRules.Modifier(damageType, cat);
            float finalDamage         = damageBase * modifier;

            targetHealth.TakeDamage(finalDamage);

            Debug.Log($"[Combat] Missile impacted {targetHealth.name} ({cat}): " +
                      $"base {damageBase}, {damageType} ×{modifier:F2}, final {finalDamage:F1}.");
        }
        else
        {
            Debug.Log("[Combat] Missile impacted — target already destroyed (no damage).");
        }

        SpawnImpactFlash();
        Destroy(gameObject);
    }

    /// <summary>
    /// Drops a quick bright sphere at the impact point. The flash is a
    /// stand-alone GameObject so it outlives this missile's Destroy().
    /// </summary>
    private void SpawnImpactFlash()
    {
        if (impactFlashDuration <= 0f) return;

        GameObject flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flash.name = "MissileImpactFlash";
        flash.transform.position   = endPos;
        flash.transform.localScale = Vector3.one * 1.6f;
        flash.layer = IgnoreRaycastLayer;

        // Strip the auto-collider so the flash can never block clicks.
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
            if (m.HasProperty("_BaseColor"))
                m.SetColor("_BaseColor", flashColor);
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
