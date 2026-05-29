using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Missile rack + firing logic for an aircraft. Owns ammo state, reload
/// timing, and the actual missile-release decision. AirUnitController calls
/// <see cref="TryFire"/> each tick during an attack run; the weapon answers
/// "Fired / Cooldown / NoAmmo / TargetTooClose / TargetBehind / OffCone /
/// NoTarget" and the controller decides whether to continue, egress, or
/// reposition.
///
/// Inspired by the SAGE engine's separation of Weapon module from AIUpdate:
/// the brain doesn't know about cone math, clip size, or projectile spawning —
/// it just asks the weapon to fire, and the weapon enforces its own rules.
/// This is what keeps AirUnitController focused on state transitions instead
/// of missile geometry.
///
/// Setup:
///   1. Attach to the aircraft root alongside AirUnitController. The
///      controller's Awake will warn (and auto-add a default weapon) if
///      this component is missing.
///   2. Tune ammo / reload / fire-cone in the Inspector.
///   3. The weapon does not need a reference to AirUnitController — the
///      controller drives it from outside via <see cref="TryFire"/>,
///      <see cref="TickReload"/>, and <see cref="TickFireCooldown"/>.
///
/// What it does NOT do:
///   • Decide whether the aircraft should be IN the attack-run state — the
///     controller owns that.
///   • Move the aircraft.
///   • Track per-pass burst counts — that's controller state.
///   • Apply damage directly — it spawns a <see cref="StrikeMissile"/>
///     projectile which handles its own flight + impact.
/// </summary>
[DisallowMultipleComponent]
public class AircraftWeapon : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Result codes
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Outcome of a <see cref="TryFire"/> call. The controller chooses how
    /// to react: Fired/Cooldown/OffCone keep the run going, Behind/TooClose
    /// trigger an egress, NoAmmo ends the pass.
    /// </summary>
    public enum FireResult
    {
        Fired,            // a missile was just released this tick
        Cooldown,         // fire delay hasn't elapsed yet — try again next tick
        NoAmmo,           // out of missiles — controller should egress
        NoTarget,         // target is null or destroyed
        TargetTooClose,   // distance < minReleaseDistance — overhead, abort run
        TargetBehind,     // forward·toTarget < 0 — abort run
        OffCone           // outside cone or past max range — wait for re-acquire
    }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Ammo")]
    [Tooltip("Maximum missiles per full rack. Reload tops out at this count.")]
    public int maxAmmo = 2;

    [Tooltip("Seconds spent Parked between each automatic missile reload. " +
             "Reload only ticks while the controller calls TickReload (i.e. " +
             "while the aircraft is in the Parked state).")]
    public float reloadSecondsPerMissile = 3f;

    [Header("Damage")]
    [Tooltip("Base damage per missile, before category modifier in DamageRules.")]
    public float missileDamage = 120f;

    [Tooltip("Damage type passed to the projectile. Missile is strong vs " +
             "Vehicle/Building per the standard damage table.")]
    public DamageType damageType = DamageType.Missile;

    [Header("Projectile")]
    [Tooltip("Missile projectile speed (world units / second).")]
    public float missileProjectileSpeed = 30f;

    [Tooltip("Colour of the missile body and impact flash.")]
    [ColorUsage(false)] public Color missileColor = new Color(1f, 0.45f, 0.10f);

    [Tooltip("Seconds the bright impact sphere is visible at the missile's blast point.")]
    public float impactFlashDuration = 0.2f;

    [Header("Firing Rules")]
    [Tooltip("Seconds between consecutive missile releases during an attack run.")]
    public float missileFireDelay = 0.75f;

    [Tooltip("Hard minimum XZ distance for a missile release. Below this the " +
             "target is essentially under the aircraft — the run aborts instead " +
             "of firing into the ground.")]
    public float minReleaseDistance = 3.5f;

    [Tooltip("Maximum XZ distance for a missile release. Targets past this are " +
             "treated as out-of-window and the run aborts.")]
    public float maxReleaseDistance = 24f;

    [Tooltip("Total cone angle (degrees) within which the target must lie for " +
             "a missile to fire. 90° = ±45° from forward. Wider = more permissive " +
             "second-missile shots.")]
    [Range(10f, 180f)] public float forwardConeDegrees = 90f;

    // ------------------------------------------------------------------ //
    // Public runtime state
    // ------------------------------------------------------------------ //

    /// <summary>Current rack contents. Read-only externally; the weapon owns this.</summary>
    public int CurrentAmmo { get; private set; }

    public bool HasAmmo => CurrentAmmo > 0;
    public bool IsFull  => CurrentAmmo >= maxAmmo;

    // ------------------------------------------------------------------ //
    // Internal timers
    // ------------------------------------------------------------------ //

    private float fireCooldown;   // seconds until next missile is eligible to release
    private float reloadTimer;    // seconds accumulated toward next reload tick

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        CurrentAmmo = Mathf.Max(0, maxAmmo);
    }

    // ------------------------------------------------------------------ //
    // Public API — driven by AirUnitController
    // ------------------------------------------------------------------ //

    /// <summary>Top up to a full rack and clear reload progress. Called by the controller on AssignHome.</summary>
    public void ResetAmmo()
    {
        CurrentAmmo = maxAmmo;
        reloadTimer = 0f;
    }

    /// <summary>Clear the fire cooldown — called on AttackRun entry so the first missile can release immediately.</summary>
    public void ResetFireCooldown()
    {
        fireCooldown = 0f;
    }

    /// <summary>Tick the inter-missile delay. The controller calls this every frame; cheap when timer is already zero.</summary>
    public void TickFireCooldown(float dt)
    {
        if (fireCooldown > 0f) fireCooldown = Mathf.Max(0f, fireCooldown - dt);
    }

    /// <summary>
    /// Tick the parked-reload timer. The controller calls this ONLY while the
    /// aircraft is Parked. Adds one missile per <see cref="reloadSecondsPerMissile"/>
    /// up to <see cref="maxAmmo"/>.
    /// </summary>
    public void TickReload(float dt)
    {
        if (IsFull) { reloadTimer = 0f; return; }

        reloadTimer += dt;
        if (reloadTimer >= reloadSecondsPerMissile)
        {
            reloadTimer = 0f;
            CurrentAmmo = Mathf.Min(maxAmmo, CurrentAmmo + 1);
            Debug.Log($"[AircraftWeapon:{name}] Reloaded missile. Ammo {CurrentAmmo}/{maxAmmo}");
        }
    }

    /// <summary>
    /// Try to release a missile at <paramref name="target"/> from
    /// <paramref name="firePoint"/>. The weapon checks ammo, fire cooldown,
    /// distance window, and forward cone — returning a <see cref="FireResult"/>
    /// the controller can act on. Only <see cref="FireResult.Fired"/> actually
    /// spawns a projectile and decrements <see cref="CurrentAmmo"/>.
    /// </summary>
    public FireResult TryFire(Health target, Transform firePoint, Vector3 aircraftForward)
    {
        if (target == null) return FireResult.NoTarget;

        if (!HasAmmo)
        {
            Debug.Log($"[AircraftWeapon:{name}] Blocked: no ammo");
            return FireResult.NoAmmo;
        }

        if (fireCooldown > 0f) return FireResult.Cooldown;

        // --- Geometry ----------------------------------------------- //
        Vector3 toTarget   = target.transform.position - transform.position;
        Vector3 toTargetXZ = new Vector3(toTarget.x, 0f, toTarget.z);
        float   distXZ     = toTargetXZ.magnitude;

        Vector3 fwdXZ = aircraftForward; fwdXZ.y = 0f;
        if (fwdXZ.sqrMagnitude > 0.0001f) fwdXZ.Normalize();
        else                              fwdXZ = Vector3.forward;

        Vector3 dirXZ = (distXZ > 0.0001f) ? toTargetXZ / distXZ : fwdXZ;
        float   dot   = Vector3.Dot(fwdXZ, dirXZ);
        float   angle = Vector3.Angle(fwdXZ, dirXZ);

        // --- Hard blocks (controller will egress) ------------------- //
        if (distXZ < minReleaseDistance)
        {
            Debug.Log($"[AircraftWeapon:{name}] Blocked: target extremely close ({distXZ:F1}u)");
            return FireResult.TargetTooClose;
        }
        if (dot < 0f)
        {
            Debug.Log($"[AircraftWeapon:{name}] Blocked: target behind");
            return FireResult.TargetBehind;
        }

        // --- Soft blocks (controller waits / continues forward) ----- //
        if (distXZ > maxReleaseDistance) return FireResult.OffCone;
        if (angle > forwardConeDegrees * 0.5f) return FireResult.OffCone;

        // --- Fire --------------------------------------------------- //
        SpawnMissile(firePoint, target);
        CurrentAmmo  = Mathf.Max(0, CurrentAmmo - 1);
        fireCooldown = missileFireDelay;
        Debug.Log($"[AircraftWeapon:{name}] Missile fired. Ammo {CurrentAmmo}/{maxAmmo}");
        return FireResult.Fired;
    }

    // ------------------------------------------------------------------ //
    // Projectile spawning
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Builds and launches a single damaging <see cref="StrikeMissile"/>. Runs
    /// ONLY on the owner (the FSM/weapon are gated off on non-owner clients), so
    /// it also broadcasts an AircraftFired event telling other clients to spawn
    /// a VISUAL-ONLY copy — that way remote players see the strike while damage
    /// stays single-sourced here.
    /// </summary>
    private void SpawnMissile(Transform firePoint, Health target)
    {
        Vector3 start = (firePoint != null)
            ? firePoint.position
            : transform.position + Vector3.down * 0.3f;

        StrikeMissile missile = BuildMissileObject(start);
        missile.Launch(start, target, missileDamage, damageType,
                       missileProjectileSpeed, impactFlashDuration, missileColor);

        // Replicate the strike as a visual-only missile on other clients. The
        // snapshot impact point mirrors StrikeMissile.Launch's own endPos so the
        // remote visual lands where the authoritative missile did.
        GameEntity ge = GetComponent<GameEntity>();
        if (ge != null && target != null)
        {
            Vector3 targetPos = target.transform.position + Vector3.up * 0.5f;
            NetworkMatchEvents.BroadcastAircraftFired(ge.EntityId, start, targetPos);
        }
    }

    /// <summary>
    /// Spawn a VISUAL-ONLY missile (no damage) flying <paramref name="start"/> →
    /// <paramref name="end"/>. Called on NON-OWNER clients from the AircraftFired
    /// network event so remote players see the strike. Never applies damage —
    /// authoritative damage is dealt once by the owner's <see cref="SpawnMissile"/>.
    /// </summary>
    public void SpawnVisualMissile(Vector3 start, Vector3 end)
    {
        StrikeMissile missile = BuildMissileObject(start);
        missile.LaunchVisual(start, end, missileProjectileSpeed, impactFlashDuration, missileColor);
    }

    /// <summary>
    /// Builds the missile GameObject (cube + material + <see cref="StrikeMissile"/>),
    /// positioned at <paramref name="start"/>. Shared by the damaging and
    /// visual-only spawn paths so the projectile looks identical on every client.
    /// </summary>
    private StrikeMissile BuildMissileObject(Vector3 start)
    {
        // Positional launch sound. BuildMissileObject is the ONE path shared by
        // the owner's damaging missile and the remotes' visual-only copy, so the
        // launch is heard on every client without a separate audio event.
        AudioManager.SfxAt(GameSound.MissileLaunch, start);

        GameObject mGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
        mGO.name                 = "StrikeMissile";
        mGO.transform.position   = start;
        mGO.transform.localScale = new Vector3(0.20f, 0.20f, 1.00f);
        mGO.layer                = 2;   // IgnoreRaycast

        Collider col = mGO.GetComponent<Collider>();
        if (col != null) Destroy(col);

        Renderer r = mGO.GetComponent<Renderer>();
        if (r != null)
        {
            Shader shader = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Standard");
            Material m = new Material(shader) { color = missileColor };
            if (m.HasProperty("_BaseColor"))
                m.SetColor("_BaseColor", missileColor);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", missileColor * 1.4f);
            }
            r.sharedMaterial    = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows    = false;
        }

        return mGO.AddComponent<StrikeMissile>();
    }
}
