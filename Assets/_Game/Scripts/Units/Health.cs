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
        Destroy(gameObject);
    }
}
