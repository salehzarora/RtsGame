using UnityEngine;

/// <summary>
/// Stores a unit's health and team affiliation.
/// Handles damage, death, and destruction.
///
/// Setup — Player unit:
///   1. Add this component to your player capsule.
///   2. Set Team = Player in the Inspector.
///   3. Set Max Health (e.g. 100).
///
/// Setup — Enemy unit:
///   1. Add this component to your enemy capsule.
///   2. Set Team = Enemy in the Inspector.
///   3. Set Max Health (e.g. 80).
/// </summary>
public class Health : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    public enum Team { Player, Enemy }

    // ------------------------------------------------------------------ //
    // Inspector fields
    // ------------------------------------------------------------------ //

    [Header("Team")]
    public Team team = Team.Player;

    [Header("Health")]
    public float maxHealth = 100f;

    [Header("Destruction")]
    [Tooltip("Seconds to wait between OnDeath firing and the GameObject being destroyed. " +
             "Leave 0 to destroy immediately (default — unchanged behaviour). " +
             "Set ~1.5–2.0 on units that play a death animation so the clip has time to play.")]
    public float destroyDelay = 0f;

    [Tooltip("If true (default), the GameObject is destroyed when health reaches 0 — the " +
             "normal behaviour for units and buildings. Set FALSE for persistent map " +
             "objects (e.g. a destructible bridge) that must survive 'death' to show a " +
             "destroyed visual / act as a path blocker. When false, OnDeath still fires " +
             "(once) so a DestructibleMapObject can run its destroyed-state transition, " +
             "but the GameObject and its GameEntity stay registered for network sync.")]
    public bool destroyObjectOnDeath = true;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    public float CurrentHealth { get; private set; }

    /// <summary>Fired after health changes. Args: (currentHealth, maxHealth).</summary>
    public event System.Action<float, float> OnHealthChanged;

    /// <summary>Fired once, just before the GameObject is destroyed.</summary>
    public event System.Action OnDeath;

    // Phase 10.3 — once death has fired (or been applied from the network)
    // we ignore further damage / death events. Prevents double-Destroy and
    // double-OnDeath when a local hit and a network EntityDestroyed event
    // race.
    private bool dying;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        CurrentHealth = maxHealth;
    }

    // ------------------------------------------------------------------ //

    /// <summary>Apply <paramref name="amount"/> damage. Triggers death at 0.</summary>
    public void TakeDamage(float amount)
    {
        if (dying) return;
        if (CurrentHealth <= 0f) return;            // already dead

        CurrentHealth = Mathf.Max(0f, CurrentHealth - amount);
        OnHealthChanged?.Invoke(CurrentHealth, maxHealth);

        // Phase 10.3 — master-authoritative damage sync. Every client runs
        // local damage independently (no latency added); the master also
        // broadcasts the resulting newHealth so any divergence (timing,
        // projectile miss, etc.) gets snapped on receive. Non-master
        // broadcasts are gated inside NetworkMatchEvents.ShouldBroadcast.
        // Also gated when this method is reached via an inbound network
        // event (IsApplyingNetworkEvent=true) so we don't echo.
        BroadcastDamageIfMaster(amount);

        if (CurrentHealth <= 0f)
            Die();
    }

    /// <summary>Restore health, capped at maxHealth.</summary>
    public void Heal(float amount)
    {
        CurrentHealth = Mathf.Min(maxHealth, CurrentHealth + amount);
        OnHealthChanged?.Invoke(CurrentHealth, maxHealth);
    }

    // ------------------------------------------------------------------ //
    // Phase 10.3 — network-driven apply paths
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Snap <see cref="CurrentHealth"/> to <paramref name="newHealth"/> from
    /// a master-broadcast ApplyDamage event. We don't subtract relative
    /// amounts here — master is authoritative for the resulting value, so
    /// we trust it absolutely. If the snap brings us to zero, run the local
    /// death path (which will NOT re-broadcast because
    /// <see cref="NetworkMatchEvents.IsApplyingNetworkEvent"/> is on).
    /// </summary>
    public void ApplyDamageFromNetwork(float amountForLog, float newHealth)
    {
        ApplyDamageFromNetwork(amountForLog, newHealth, null);
    }

    /// <summary>
    /// Overload that also logs the attacker entity id for diagnostics.
    /// </summary>
    public void ApplyDamageFromNetwork(float amountForLog, float newHealth, string attackerId)
    {
        if (dying) return;

        float clamped = Mathf.Clamp(newHealth, 0f, maxHealth);
        Debug.Log($"[NetDamage] Apply target health {CurrentHealth} -> {clamped}" +
                  (string.IsNullOrEmpty(attackerId) ? "" : $" (attacker {attackerId})"));
        CurrentHealth = clamped;
        OnHealthChanged?.Invoke(CurrentHealth, maxHealth);

        if (CurrentHealth <= 0f)
            Die();
    }

    /// <summary>
    /// Destroy this entity in response to a network EntityDestroyed event.
    /// Idempotent — calling twice (because a local Die already ran and the
    /// broadcast then arrived, or vice versa) is safe.
    /// </summary>
    public void DestroyFromNetwork()
    {
        if (dying) return;
        Die();
    }

    // ------------------------------------------------------------------ //

    private void Die()
    {
        if (dying) return;
        dying = true;

        // Broadcast destruction BEFORE the OnDeath chain — listeners may
        // unregister entity ids that the broadcast helper would otherwise
        // need to look up. NetworkMatchEvents itself gates non-master and
        // suppress-flag broadcasts internally.
        BroadcastDestroyIfMaster();

        OnDeath?.Invoke();

        // Persistent map objects opt out of GameObject destruction so they can
        // present a destroyed visual / blocker. They stay registered in
        // EntityRegistry so network state sync still reaches them.
        if (!destroyObjectOnDeath) return;

        if (destroyDelay > 0f)
            StartCoroutine(DelayedDestroy(destroyDelay));
        else
            Destroy(gameObject);
    }

    /// <summary>
    /// Restore this entity to full health and clear the <see cref="dying"/>
    /// latch so it can take damage and die again. Intended for persistent map
    /// objects (<see cref="destroyObjectOnDeath"/> == false) that need to be
    /// reset to their intact state on a match restart / repair. No-op style
    /// safe to call on a healthy object. Fires <see cref="OnHealthChanged"/>
    /// so any health bar refreshes.
    /// </summary>
    public void ReviveFull()
    {
        dying         = false;
        CurrentHealth = maxHealth;
        OnHealthChanged?.Invoke(CurrentHealth, maxHealth);
    }

    private System.Collections.IEnumerator DelayedDestroy(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        Destroy(gameObject);
    }

    // ------------------------------------------------------------------ //
    // Broadcast helpers — small wrappers so the call sites above stay
    // readable. All MP gating lives inside NetworkMatchEvents.
    // ------------------------------------------------------------------ //

    private void BroadcastDamageIfMaster(float amount)
    {
        GameEntity ge = GetComponent<GameEntity>();
        if (ge == null) return;     // no id → can't address on the wire
        NetworkMatchEvents.BroadcastApplyDamage(
            ge.EntityId, attackerEntityId: string.Empty, amount, CurrentHealth);
    }

    private void BroadcastDestroyIfMaster()
    {
        GameEntity ge = GetComponent<GameEntity>();
        if (ge == null) return;
        NetworkMatchEvents.BroadcastEntityDestroyed(ge.EntityId, killerEntityId: string.Empty);
    }
}
