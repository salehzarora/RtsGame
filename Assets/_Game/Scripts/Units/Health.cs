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

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    public float CurrentHealth { get; private set; }

    /// <summary>Fired after health changes. Args: (currentHealth, maxHealth).</summary>
    public event System.Action<float, float> OnHealthChanged;

    /// <summary>Fired once, just before the GameObject is destroyed.</summary>
    public event System.Action OnDeath;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        CurrentHealth = maxHealth;
    }

    // ------------------------------------------------------------------ //

    /// <summary>Apply <paramref name="amount"/> damage. Triggers death at 0.</summary>
    public void TakeDamage(float amount)
    {
        if (CurrentHealth <= 0f) return;            // already dead

        CurrentHealth = Mathf.Max(0f, CurrentHealth - amount);
        OnHealthChanged?.Invoke(CurrentHealth, maxHealth);

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

    private void Die()
    {
        OnDeath?.Invoke();

        if (destroyDelay > 0f)
            StartCoroutine(DelayedDestroy(destroyDelay));
        else
            Destroy(gameObject);
    }

    private System.Collections.IEnumerator DelayedDestroy(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        Destroy(gameObject);
    }
}
