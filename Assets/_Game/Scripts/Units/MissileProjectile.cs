using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Parabolic-arc artillery missile fired by <see cref="MissileLauncherCombat"/>.
/// Travels from launch position to a snapshotted impact position with a
/// time-based arc (no homing — once airborne, the missile commits to the
/// impact point). On arrival it deals splash damage via
/// <see cref="DamageRules.Modifier"/> with <see cref="DamageType.Artillery"/>
/// to every <see cref="Health"/> within <see cref="splashRadius"/> on the
/// opposing team.
///
/// Why a separate projectile (not RocketProjectile):
///   • RocketProjectile hard-codes DamageType.Rocket and applies damage only
///     to the original target. The artillery launcher needs the Artillery
///     modifier table AND area-of-effect.
///   • A parabolic arc reads as "long-range artillery" rather than a flat
///     rocket trajectory.
///
/// No physics, no NavMesh, no colliders. Layer is IgnoreRaycast so the
/// projectile never blocks gameplay raycasts.
///
/// Setup is automatic — <see cref="MissileLauncherCombat"/> builds the
/// GameObject procedurally and calls <see cref="Launch"/> once. Do NOT add
/// this component to a prefab by hand.
/// </summary>
public class MissileProjectile : MonoBehaviour
{
    private const int IgnoreRaycastLayer = 2;

    // ------------------------------------------------------------------ //
    // Launch parameters — captured in Launch()
    // ------------------------------------------------------------------ //

    private Vector3 startPos;
    private Vector3 impactPos;     // snapshotted at launch — missile commits here
    private Health  primaryTarget; // for "intended target" diagnostics only
    private Health.Team launcherTeam;

    private float   baseDamage;
    private float   splashRadius;
    private float   travelDuration;
    private float   arcHeight;
    private float   impactFlashDuration;
    private Color   flashColor;

    private float   launchTime;
    private bool    launched;

    // Allocated lazily on first impact to avoid per-shot GC.
    private static readonly Collider[] SplashBuffer = new Collider[32];
    private static int splashLayerMask;
    private static bool splashLayerMaskResolved;

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Begin flying from <paramref name="origin"/> to <paramref name="impactPoint"/>
    /// over <paramref name="travelTime"/> seconds, peaking at
    /// <paramref name="arc"/> world-units above the midpoint.
    /// </summary>
    public void Launch(
        Vector3 origin, Vector3 impactPoint, Health primaryTarget, Health.Team team,
        float damage, float splash, float travelTime, float arc,
        Color color, float flashDuration)
    {
        startPos             = origin;
        impactPos            = impactPoint;
        this.primaryTarget   = primaryTarget;
        launcherTeam         = team;
        baseDamage           = damage;
        splashRadius         = Mathf.Max(0.1f, splash);
        travelDuration       = Mathf.Max(0.05f, travelTime);
        arcHeight            = Mathf.Max(0f, arc);
        impactFlashDuration  = flashDuration;
        flashColor           = color;

        // Snap the missile to launch position + orient along initial heading.
        transform.position = origin;
        Vector3 toImpact = impactPoint - origin;
        toImpact.y = 0f;
        if (toImpact.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(toImpact.normalized);

        launchTime = Time.time;
        launched   = true;
    }

    // ------------------------------------------------------------------ //
    // Flight
    // ------------------------------------------------------------------ //

    private void Update()
    {
        if (!launched) return;

        float t = (Time.time - launchTime) / travelDuration;
        if (t >= 1f)
        {
            Detonate();
            return;
        }

        // Linear interpolation in XZ + sinusoidal arc on Y. Mathf.Sin(π·t) peaks
        // at t=0.5 with value 1, returns to 0 at t=1 — a clean ballistic arc.
        Vector3 flatNow = Vector3.Lerp(startPos, impactPos, t);
        flatNow.y = Mathf.Lerp(startPos.y, impactPos.y, t)
                  + arcHeight * Mathf.Sin(Mathf.PI * t);

        // Aim along the velocity vector so the missile nose tracks the curve.
        Vector3 next = NextPosition(t + 0.01f);
        Vector3 heading = next - flatNow;
        if (heading.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(heading.normalized);

        transform.position = flatNow;
    }

    private Vector3 NextPosition(float tNext)
    {
        tNext = Mathf.Clamp01(tNext);
        Vector3 flat = Vector3.Lerp(startPos, impactPos, tNext);
        flat.y = Mathf.Lerp(startPos.y, impactPos.y, tNext)
               + arcHeight * Mathf.Sin(Mathf.PI * tNext);
        return flat;
    }

    // ------------------------------------------------------------------ //
    // Detonation + splash
    // ------------------------------------------------------------------ //

    private void Detonate()
    {
        transform.position = impactPos;
        launched = false;

        // Resolve the scan mask once. Unit + Building covers every legitimate
        // ground target in this project; Aircraft are also on Unit but the
        // Artillery modifier table is 0.00 against them so they take no damage.
        if (!splashLayerMaskResolved)
        {
            int unit = LayerMask.NameToLayer("Unit");
            int bld  = LayerMask.NameToLayer("Building");
            int mask = 0;
            if (unit >= 0) mask |= 1 << unit;
            if (bld  >= 0) mask |= 1 << bld;
            splashLayerMask         = mask != 0 ? mask : ~0;
            splashLayerMaskResolved = true;
        }

        int count = Physics.OverlapSphereNonAlloc(
            impactPos, splashRadius, SplashBuffer, splashLayerMask);

        Health.Team hostileTeam = launcherTeam == Health.Team.Player
            ? Health.Team.Enemy
            : Health.Team.Player;

        int hits = 0;
        for (int i = 0; i < count; i++)
        {
            Collider c = SplashBuffer[i];
            if (c == null) continue;

            Health h = c.GetComponent<Health>() ?? c.GetComponentInParent<Health>();
            if (h == null) continue;
            if (h.team != hostileTeam) continue;

            UnitCategory.Category cat = DamageRules.Resolve(h.gameObject);
            float modifier  = DamageRules.Modifier(DamageType.Artillery, cat);
            float damage    = baseDamage * modifier;
            if (damage <= 0f) continue;     // Aircraft modifier is 0 by design

            h.TakeDamage(damage);
            hits++;
        }

        string primaryName = primaryTarget != null ? primaryTarget.name : "(no primary)";
        Debug.Log($"[MissileLauncher] Missile detonated at {impactPos:F1}. " +
                  $"Targets hit: {hits} (primary: {primaryName}, splash radius {splashRadius:F1}).");

        SpawnImpactFlash(impactPos);
        Destroy(gameObject);
    }

    private void SpawnImpactFlash(Vector3 worldPos)
    {
        AudioManager.SfxAt(GameSound.Impact, worldPos);

        if (impactFlashDuration <= 0f) return;

        GameObject flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flash.name = "MissileImpactFlash";
        flash.transform.position   = worldPos;
        // Slightly larger than the rocket flash so it reads as artillery-class.
        flash.transform.localScale = Vector3.one * Mathf.Max(1.5f, splashRadius * 0.6f);
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
            if (m.HasProperty("_BaseColor"))     m.SetColor("_BaseColor", flashColor);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", flashColor * 1.6f);
            }
            r.sharedMaterial    = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows    = false;
        }

        Destroy(flash, impactFlashDuration);
    }
}
