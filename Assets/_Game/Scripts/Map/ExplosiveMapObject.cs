using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// PHASE B — explosive map object (fuel tanks, ammo dumps). Extends
/// <see cref="DestructibleMapObject"/>: when destroyed it detonates, dealing
/// AREA damage to nearby units / buildings / other map objects and spawning a
/// blast flash. A blast that damages another explosive chains naturally —
/// that object's own death triggers its own explosion.
///
/// Multiplayer authority:
///   • The blast FLASH is cosmetic and runs on every client (driven by the
///     replicated destroyed-state transition — see <see cref="DestructibleMapObject"/>).
///   • AREA DAMAGE is applied EXACTLY ONCE, on the authoritative side
///     (<see cref="MapInteractable.Authoritative"/> — single-player or Photon
///     master). Each victim's resulting health / death then replicates through
///     the existing master-authoritative ApplyDamage / EntityDestroyed events,
///     so remote clients never double-apply.
///   • Chain reactions terminate safely: a victim explosive that is already
///     dying ignores further damage (Health's <c>dying</c> latch +
///     <see cref="DestructibleMapObject.IsDestroyed"/>).
///
/// Setup (or use Tools → RTS → Map → Create Fuel Tank):
///   Add alongside <see cref="Health"/> + <see cref="GameEntity"/>. Tune the
///   radius / damage. Lower Max Health so weapons can pop it; a tank shell or
///   missile should destroy it in a couple of hits.
/// </summary>
public class ExplosiveMapObject : DestructibleMapObject
{
    // ------------------------------------------------------------------ //
    // Inspector — Explosion
    // ------------------------------------------------------------------ //

    [Header("Explosion")]
    [Tooltip("World-unit radius of the blast.")]
    public float explosionRadius = 9f;

    [Tooltip("Damage at the centre of the blast (before distance falloff).")]
    public float explosionDamage = 140f;

    [Tooltip("Seconds between destruction and the blast resolving. 0 = instant. " +
             "A short delay (0.1–0.3) reads as a fuse and staggers chain reactions.")]
    public float explosionDelay = 0f;

    [Tooltip("If true, damage falls off linearly from full at the centre to 0 at " +
             "the radius edge. If false, every target in radius takes full damage.")]
    public bool linearFalloff = true;

    [Header("Explosion — Targets")]
    [Tooltip("Blast damages mobile units (infantry, vehicles, aircraft on the ground).")]
    public bool affectsUnits = true;

    [Tooltip("Blast damages buildings.")]
    public bool affectsBuildings = true;

    [Tooltip("Blast damages other map objects (bridges, garrisons, other tanks).")]
    public bool affectsMapObjects = true;

    [Tooltip("If true, the blast can detonate OTHER ExplosiveMapObjects even when " +
             "Affects Map Objects is off — the classic chain-reaction of fuel tanks.")]
    public bool chainReactionEnabled = true;

    [Header("Explosion — Physics (optional)")]
    [Tooltip("Impulse applied to Rigidbodies caught in the blast. 0 disables. " +
             "Most units use NavMeshAgents (no Rigidbody) so this only affects " +
             "physics props.")]
    public float explosionForce = 0f;

    [Header("Explosion — Flash (placeholder)")]
    [Tooltip("Colour of the procedural blast flash.")]
    [ColorUsage(false)] public Color flashColor = new Color(1f, 0.6f, 0.15f);

    [Tooltip("Seconds the procedural blast flash stays visible before fading out.")]
    public float flashDuration = 0.45f;

    // ------------------------------------------------------------------ //
    // Editor defaults — only applied when the component is first added.
    // ------------------------------------------------------------------ //

    private void Reset()
    {
        maxHealth = 90f;
        // Persist as a scorched husk so the existing 0.5s health-snapshot loop
        // gives late-join clients the destroyed state and a match restart can
        // reset it. The husk's collider is disabled on destruction so it stops
        // being a re-attack target.
        persistAfterDestroyed = true;
        isTargetable          = true;
    }

    // ------------------------------------------------------------------ //
    // Destruction hook
    // ------------------------------------------------------------------ //

    protected override void Awake()
    {
        base.Awake();
        OnDestroyed += HandleExploded;
    }

    private void HandleExploded()
    {
        // Always show the cosmetic flash locally.
        SpawnBlastFlash(transform.position);

        if (explosionDelay <= 0f)
        {
            ResolveBlast(transform.position);
        }
        else
        {
            // Spawn a standalone runner so the delayed blast resolves even when
            // this (non-persistent) GameObject is destroyed by Health.Die right
            // after the destroyed-state transition.
            DelayedBlastRunner.Spawn(this, transform.position, explosionDelay);
        }
    }

    // ------------------------------------------------------------------ //
    // Blast resolution
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Apply area damage around <paramref name="center"/>. Authoritative-only —
    /// non-authoritative clients render the flash but never apply damage, so a
    /// victim is never double-hit. Public so <see cref="DelayedBlastRunner"/>
    /// can call it after the fuse.
    /// </summary>
    public void ResolveBlast(Vector3 center)
    {
        if (!Authoritative) return;

        Health selfHealth = health;
        int hitCount = 0;

        // Allocating overlap (fresh array per blast) — required because a chain
        // reaction re-enters ResolveBlast synchronously via TakeDamage, which
        // would corrupt a shared static buffer mid-iteration.
        Collider[] hits = Physics.OverlapSphere(center, explosionRadius);
        var processed = new HashSet<Health>();

        for (int i = 0; i < hits.Length; i++)
        {
            Collider c = hits[i];
            if (c == null) continue;

            Health h = c.GetComponent<Health>() ?? c.GetComponentInParent<Health>();
            if (h == null || h == selfHealth) continue;
            if (!processed.Add(h)) continue;          // dedup multi-collider targets
            if (!ShouldDamage(h.gameObject)) continue;

            float dist     = Vector3.Distance(center, h.transform.position);
            float falloff  = linearFalloff ? Mathf.Clamp01(1f - dist / Mathf.Max(0.01f, explosionRadius)) : 1f;
            float dmg      = explosionDamage * falloff;
            if (dmg <= 0f) continue;

            h.TakeDamage(dmg);
            hitCount++;

            if (explosionForce > 0f)
            {
                Rigidbody rb = h.GetComponent<Rigidbody>() ?? h.GetComponentInParent<Rigidbody>();
                if (rb != null && !rb.isKinematic)
                    rb.AddExplosionForce(explosionForce, center, explosionRadius, 0.5f, ForceMode.Impulse);
            }
        }

        Debug.Log($"[MapExplosion] '{name}' detonated at {center:F1} r={explosionRadius} " +
                  $"dmg={explosionDamage} — hit {hitCount} target(s). (authoritative)");
    }

    /// <summary>
    /// Decides whether <paramref name="targetGO"/> is in scope for this blast,
    /// based on its <see cref="GameEntity.entityType"/> and the affects* flags.
    /// </summary>
    private bool ShouldDamage(GameObject targetGO)
    {
        GameEntity ge = targetGO.GetComponent<GameEntity>() ?? targetGO.GetComponentInParent<GameEntity>();

        // Map objects (incl. other explosives).
        bool isMapObject = (ge != null && ge.entityType == EntityType.MapObject)
                        || targetGO.GetComponentInParent<DestructibleMapObject>() != null;
        if (isMapObject)
        {
            bool isExplosive = targetGO.GetComponentInParent<ExplosiveMapObject>() != null;
            if (isExplosive && chainReactionEnabled) return true;
            return affectsMapObjects;
        }

        // Buildings.
        if (ge != null && ge.entityType == EntityType.Building) return affectsBuildings;

        // Everything else (units, aircraft, or untyped) counts as a unit.
        return affectsUnits;
    }

    // ------------------------------------------------------------------ //
    // Procedural blast flash — no asset dependency
    // ------------------------------------------------------------------ //

    private void SpawnBlastFlash(Vector3 center)
    {
        const int IgnoreRaycastLayer = 2;

        GameObject flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flash.name = "ExplosionFlash";
        flash.transform.position   = center;
        flash.transform.localScale = Vector3.one * (explosionRadius * 1.4f);
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
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", flashColor);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", flashColor * 2f);
            }
            r.sharedMaterial    = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows    = false;
        }

        // A tiny self-fading driver so the flash expands + disappears without
        // any animation asset.
        ExplosionFlashFx fx = flash.AddComponent<ExplosionFlashFx>();
        fx.Init(explosionRadius * 1.4f, Mathf.Max(0.05f, flashDuration));
    }
}

/// <summary>
/// Standalone fuse runner so a delayed blast resolves even after the source
/// (non-persistent) GameObject has been destroyed.
/// </summary>
public class DelayedBlastRunner : MonoBehaviour
{
    private ExplosiveMapObject source;
    private Vector3 center;
    private float   fuse;

    public static void Spawn(ExplosiveMapObject source, Vector3 center, float fuse)
    {
        GameObject go = new GameObject("DelayedBlastRunner");
        go.transform.position = center;
        DelayedBlastRunner r = go.AddComponent<DelayedBlastRunner>();
        r.source = source;
        r.center = center;
        r.fuse   = fuse;
    }

    private float timer;

    private void Update()
    {
        timer += Time.deltaTime;
        if (timer < fuse) return;

        if (source != null) source.ResolveBlast(center);
        Destroy(gameObject);
    }
}

/// <summary>
/// Cosmetic self-fading flash: grows slightly and fades its material alpha,
/// then destroys itself. Pure code, no asset dependency.
/// </summary>
public class ExplosionFlashFx : MonoBehaviour
{
    private float maxScale;
    private float duration;
    private float t;
    private Renderer rend;
    private Color baseColor;

    public void Init(float maxScale, float duration)
    {
        this.maxScale = maxScale;
        this.duration = duration;
        rend = GetComponent<Renderer>();
        if (rend != null) baseColor = rend.sharedMaterial != null ? rend.sharedMaterial.color : Color.white;
    }

    private void Update()
    {
        t += Time.deltaTime;
        float k = duration > 0f ? Mathf.Clamp01(t / duration) : 1f;

        float scale = Mathf.Lerp(maxScale * 0.4f, maxScale, k);
        transform.localScale = Vector3.one * scale;

        if (rend != null && rend.sharedMaterial != null)
        {
            Color c = baseColor;
            c.a = 1f - k;
            rend.sharedMaterial.color = c;
            if (rend.sharedMaterial.HasProperty("_BaseColor"))
                rend.sharedMaterial.SetColor("_BaseColor", c);
        }

        if (k >= 1f) Destroy(gameObject);
    }
}
