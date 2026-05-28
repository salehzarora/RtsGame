using UnityEngine;

/// <summary>
/// PHASE A — base destructible map feature. Wraps the project's existing
/// <see cref="Health"/> component so map objects get networked damage and
/// death FOR FREE (the master-authoritative ApplyDamage / EntityDestroyed
/// pipeline already replicates health and "this entity is gone" to every
/// client). On death this drives a one-time DESTROYED-STATE transition —
/// swap visuals, toggle colliders, spawn a VFX/SFX placeholder, and raise
/// <see cref="OnDestroyed"/> for subclasses (explosion, bridge collapse).
///
/// Why reuse Health instead of a parallel damage system:
///   • Existing weapons already deal damage by calling <c>Health.TakeDamage</c>;
///     reusing it means tanks / aircraft / explosions can damage map objects
///     with ZERO combat changes.
///   • Death already broadcasts once and is idempotent on receive
///     (<see cref="Health"/>'s <c>dying</c> latch), so destruction can't be
///     applied twice.
///
/// Authority:
///   • Damage VALUE is master-authoritative (Health broadcasts the resulting
///     health; non-master snaps).
///   • Destruction is idempotent and replicated via EntityDestroyed +
///     the 0.5s health snapshot — so even a dropped event self-heals.
///   • Any side-effect that must run exactly once (area damage in
///     <see cref="ExplosiveMapObject"/>) is gated on <see cref="MapInteractable.Authoritative"/>.
///
/// Setup (or use Tools → RTS → Map → Create …):
///   1. Sibling <see cref="Health"/> (set Max Health) and <see cref="GameEntity"/>
///      (entityType = MapObject, ownerPlayerId = −1 for neutral).
///   2. Assign Alive Visual / Destroyed Visual child objects (optional).
///   3. List colliders to disable / enable on destruction (optional).
///   4. Put the object on a NON-combat layer (e.g. Default) so friendly units
///      don't auto-fire at it; explicit right-click attacks still work via
///      <see cref="MapInteractionRouter"/> when <see cref="isTargetable"/> is on.
/// </summary>
[RequireComponent(typeof(Health))]
public class DestructibleMapObject : MapInteractable
{
    // ------------------------------------------------------------------ //
    // Inspector — Health
    // ------------------------------------------------------------------ //

    [Header("Destructible — Health")]
    [Tooltip("Max health pushed onto the sibling Health component on Awake. " +
             "Kept here too so the editor helper can configure it without " +
             "expanding the Health foldout.")]
    public float maxHealth = 250f;

    [Tooltip("If true the GameObject is kept alive after destruction so it can " +
             "act as a persistent destroyed visual / path blocker (bridges). " +
             "If false the GameObject is destroyed once the destroyed-state " +
             "transition has run (fuel tanks — the explosion + scorch are " +
             "spawned as standalone objects that outlive it). Drives " +
             "Health.destroyObjectOnDeath.")]
    public bool persistAfterDestroyed = true;

    // ------------------------------------------------------------------ //
    // Inspector — Targeting
    // ------------------------------------------------------------------ //

    [Header("Targeting")]
    [Tooltip("If true, the local player can right-click this object with units " +
             "selected to attack it (routed through MapInteractionRouter). If " +
             "false, the object can still be damaged by area explosions but is " +
             "not a manual attack target.")]
    public bool isTargetable = true;

    // ------------------------------------------------------------------ //
    // Inspector — Visuals / colliders
    // ------------------------------------------------------------------ //

    [Header("Visual States")]
    [Tooltip("Child shown while intact. Hidden on destruction. Optional.")]
    public GameObject aliveVisual;

    [Tooltip("Child shown after destruction. Hidden while intact. Optional. " +
             "For non-persistent objects this child is detached so it survives " +
             "the parent's destruction.")]
    public GameObject destroyedVisual;

    [Header("Colliders On Destroy")]
    [Tooltip("Colliders disabled the moment this object is destroyed (e.g. the " +
             "intact bridge deck so units stop walking on it).")]
    public Collider[] disableCollidersOnDestroy;

    [Tooltip("Colliders enabled the moment this object is destroyed (e.g. a " +
             "physical blocker that stops units crossing a collapsed bridge).")]
    public Collider[] enableCollidersOnDestroy;

    [Header("Destruction FX (placeholders OK)")]
    [Tooltip("Optional prefab/particle spawned at the object's position on " +
             "destruction. Spawned on EVERY client (cosmetic).")]
    public GameObject destroyedVfx;

    [Tooltip("Seconds the spawned destroyedVfx lives before auto-destroy. 0 = " +
             "never auto-destroy (you manage it).")]
    public float destroyedVfxLifetime = 4f;

    [Tooltip("Optional one-shot clip played at the object's position on " +
             "destruction. Played on every client (cosmetic).")]
    public AudioClip destroyedSfx;

    [Range(0f, 1f)]
    [Tooltip("Volume for destroyedSfx.")]
    public float destroyedSfxVolume = 0.8f;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    /// <summary>Fired once, locally, the moment this object enters its destroyed
    /// state — on every client. Subclasses (explosion, bridge) and external
    /// listeners hook this. Authoritative-only side effects must re-check
    /// <see cref="MapInteractable.Authoritative"/>.</summary>
    public event System.Action OnDestroyed;

    /// <summary>True once the destroyed-state transition has run.</summary>
    public bool IsDestroyed { get; private set; }

    /// <summary>The sibling Health component driving damage/death for this object.</summary>
    protected Health health;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    protected override void Awake()
    {
        base.Awake();

        health = GetComponent<Health>();
        if (health != null)
        {
            health.maxHealth            = Mathf.Max(1f, maxHealth);
            health.destroyObjectOnDeath = !persistAfterDestroyed;
            // Force a clean intact init regardless of component Awake order.
            health.ReviveFull();
            health.OnDeath += HandleDeath;
        }

        // Reset the destroyed visual / blocker to the intact configuration so
        // a freshly-loaded scene reads correctly even if the prefab was saved
        // mid-edit.
        ApplyIntactVisualState();

        // Re-arm on a match restart (GameplayWorldRoot re-activates the scene
        // root rather than reloading), so a previously destroyed bridge/tank
        // returns to intact for the next match in the same Play session.
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnGameReset += ResetToIntact;
    }

    protected virtual void OnDestroy()
    {
        if (health != null) health.OnDeath -= HandleDeath;
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnGameReset -= ResetToIntact;
    }

    // ------------------------------------------------------------------ //
    // Death → destroyed-state transition
    // ------------------------------------------------------------------ //

    private void HandleDeath()
    {
        if (IsDestroyed) return;          // guard against double-fire
        IsDestroyed = true;

        Debug.Log($"[MapObject] '{name}' destroyed (entity {EntityId}). " +
                  $"persist={persistAfterDestroyed}, authoritative={Authoritative}.");

        // 1. Visual swap. For a non-persistent object, detach the destroyed
        //    visual so it outlives the imminent GameObject destruction.
        if (aliveVisual != null) aliveVisual.SetActive(false);
        if (destroyedVisual != null)
        {
            if (!persistAfterDestroyed)
                destroyedVisual.transform.SetParent(null, worldPositionStays: true);
            destroyedVisual.SetActive(true);
        }

        // 2. Colliders.
        SetColliders(disableCollidersOnDestroy, false);
        SetColliders(enableCollidersOnDestroy,  true);

        // 3. FX placeholders (cosmetic — every client runs them).
        SpawnDestructionFx();

        // 4. Notify subclasses / listeners BEFORE a non-persistent GameObject
        //    is torn down by Health.Die (which destroys it after OnDeath).
        try { OnDestroyed?.Invoke(); }
        catch (System.Exception ex)
        {
            Debug.LogError($"[MapObject] OnDestroyed handler threw on '{name}': {ex}");
        }
    }

    // ------------------------------------------------------------------ //
    // Reset (match restart / future repair)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Restore the object to its intact state. Used on match reset. Safe to
    /// call when already intact. Does not broadcast — every client resets its
    /// own scene-baked copy locally.
    /// </summary>
    public virtual void ResetToIntact()
    {
        IsDestroyed = false;
        if (health != null) health.ReviveFull();
        ApplyIntactVisualState();
    }

    private void ApplyIntactVisualState()
    {
        if (aliveVisual != null)     aliveVisual.SetActive(true);
        if (destroyedVisual != null) destroyedVisual.SetActive(false);
        SetColliders(disableCollidersOnDestroy, true);
        SetColliders(enableCollidersOnDestroy,  false);
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static void SetColliders(Collider[] colliders, bool enabled)
    {
        if (colliders == null) return;
        for (int i = 0; i < colliders.Length; i++)
            if (colliders[i] != null) colliders[i].enabled = enabled;
    }

    private void SpawnDestructionFx()
    {
        if (destroyedVfx != null)
        {
            GameObject fx = Instantiate(destroyedVfx, transform.position, Quaternion.identity);
            if (destroyedVfxLifetime > 0f) Destroy(fx, destroyedVfxLifetime);
        }
        if (destroyedSfx != null)
            AudioSource.PlayClipAtPoint(destroyedSfx, transform.position, destroyedSfxVolume);
    }
}
