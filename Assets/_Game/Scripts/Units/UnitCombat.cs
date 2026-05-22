using UnityEngine;

/// <summary>
/// Drives a unit's attack behaviour: chase → stop in range → deal damage on cooldown.
///
/// State machine:
///   Idle           — no target, unit does nothing.
///   ChasingTarget  — moving toward target every frame until within attack range.
///   Attacking      — stopped, dealing damage on cooldown.
///
/// If the target is destroyed Unity sets the reference to null automatically,
/// and the unit returns to Idle.
///
/// Setup:
///   Add this component to PLAYER units only (alongside Health and UnitMovement).
///   Enemy dummy units only need Health — they do not fight back yet.
/// </summary>
[RequireComponent(typeof(UnitMovement))]
[RequireComponent(typeof(Health))]
public class UnitCombat : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    private enum CombatState { Idle, ChasingTarget, Attacking }

    // ------------------------------------------------------------------ //
    // Inspector fields
    // ------------------------------------------------------------------ //

    [Header("Attack Stats")]
    [Tooltip("World-unit radius within which this unit can hit its target")]
    public float attackRange = 3f;

    [Tooltip("Damage dealt per hit")]
    public float attackDamage = 20f;

    [Tooltip("Seconds between hits")]
    public float attackCooldown = 1f;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private CombatState state = CombatState.Idle;
    private Health target;
    private UnitMovement movement;
    private float attackTimer;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        movement = GetComponent<UnitMovement>();
    }

    private void Update()
    {
        if (state == CombatState.Idle) return;

        // Target was destroyed — Unity null-check on MonoBehaviour handles this
        if (target == null)
        {
            state = CombatState.Idle;
            return;
        }

        float dist = Vector3.Distance(transform.position, target.transform.position);

        if (dist > attackRange)
        {
            // Close the gap
            state = CombatState.ChasingTarget;
            movement.MoveTo(target.transform.position);
        }
        else
        {
            // In range — stop moving and attack
            if (state != CombatState.Attacking)
            {
                state = CombatState.Attacking;
                movement.Stop();
                attackTimer = attackCooldown; // brief wind-up before first hit
            }

            attackTimer -= Time.deltaTime;
            if (attackTimer <= 0f)
            {
                target.TakeDamage(attackDamage);
                attackTimer = attackCooldown;

                // Target may have just been destroyed by that hit
                if (target == null)
                    state = CombatState.Idle;
            }
        }
    }

    // ------------------------------------------------------------------ //
    // Public API — called by UnitSelector
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Assign an enemy Health as the attack target.
    /// The unit will immediately begin chasing.
    /// </summary>
    public void SetTarget(Health enemyHealth)
    {
        target = enemyHealth;
        state = CombatState.ChasingTarget;
        attackTimer = attackCooldown;
    }

    /// <summary>
    /// Cancel the current attack and return to Idle.
    /// Call this when a move command overrides the attack.
    /// </summary>
    public void ClearTarget()
    {
        target = null;
        state = CombatState.Idle;
    }

    // ------------------------------------------------------------------ //
    // Gizmos — helps visualise attack range in Scene view
    // ------------------------------------------------------------------ //

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        UnityEditor.Handles.color = new Color(1f, 0.2f, 0.2f, 0.3f);
        UnityEditor.Handles.DrawWireDisc(transform.position, Vector3.up, attackRange);
    }
#endif
}
