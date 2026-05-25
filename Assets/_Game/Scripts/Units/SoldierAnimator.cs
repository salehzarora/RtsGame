using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Drives a Mixamo-style Animator from gameplay state. Attach to the unit root
/// (same GameObject as NavMeshAgent / UnitCombat / Health). The Animator itself
/// lives on the visual-root child (e.g. SoldierVisualRoot/character) and is
/// auto-found via GetComponentInChildren if not assigned.
///
/// Animator parameters (must exist on the Animator Controller):
///   • Speed       (float)   — NavMeshAgent.velocity magnitude (optionally normalised by agent.speed)
///   • IsAttacking (bool)    — true while UnitCombat has a live target
///   • Attack      (trigger) — fired once when IsAttacking transitions false → true
///   • Die         (trigger) — fired once when Health.OnDeath fires
///
/// What it does NOT do:
///   • Modify UnitCombat / NavMeshAgent / Health behaviour. It only reads their
///     state and writes Animator parameters.
///   • Add or remove components on the unit.
///   • Manage ragdolls or corpses. It only sets the Die trigger and (optionally)
///     disables the agent / combat component so the death animation plays cleanly.
/// </summary>
[DisallowMultipleComponent]
public class SoldierAnimator : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // References — auto-resolved at Awake if left empty.
    // ------------------------------------------------------------------ //

    [Header("References (auto-found if empty)")]
    [Tooltip("The Mixamo Animator. Lives on the visual root (e.g. SoldierVisualRoot/character). " +
             "Auto-located via GetComponentInChildren if you leave this null.")]
    public Animator animator;

    [Tooltip("NavMeshAgent on this unit. Read each frame for the Speed parameter.")]
    public NavMeshAgent agent;

    [Tooltip("UnitCombat on this unit. Used to drive IsAttacking and the Attack trigger.")]
    public UnitCombat unitCombat;

    [Tooltip("Health on this unit. The script subscribes to OnDeath to fire the Die trigger.")]
    public Health health;

    // ------------------------------------------------------------------ //
    // Animator parameter names — change here if your controller uses different names.
    // ------------------------------------------------------------------ //

    [Header("Animator Parameter Names")]
    public string speedParam         = "Speed";
    public string isAttackingParam   = "IsAttacking";
    public string attackTriggerParam = "Attack";
    public string dieTriggerParam    = "Die";

    // ------------------------------------------------------------------ //
    // Tuning
    // ------------------------------------------------------------------ //

    [Header("Tuning")]
    [Tooltip("If true, Speed is normalised by NavMeshAgent.speed so it stays in 0..1. " +
             "Recommended for blend trees keyed on 0 (idle) → 1 (run). " +
             "If false, Speed is the raw velocity magnitude (m/s).")]
    public bool normaliseSpeed = true;

    [Tooltip("Velocity below this is treated as zero (avoids jitter while the agent " +
             "settles at its destination).")]
    public float stationaryThreshold = 0.05f;

    [Tooltip("On death, disable the NavMeshAgent and UnitCombat so the death " +
             "animation plays without the unit still pathing or shooting.")]
    public bool disableComponentsOnDeath = true;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private bool wasAttacking;
    private bool dead;

    private int speedId, isAttackingId, attackTriggerId, dieTriggerId;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (animator   == null) animator   = GetComponentInChildren<Animator>(includeInactive: true);
        if (agent      == null) agent      = GetComponent<NavMeshAgent>();
        if (unitCombat == null) unitCombat = GetComponent<UnitCombat>();
        if (health     == null) health     = GetComponent<Health>();

        speedId         = Animator.StringToHash(speedParam);
        isAttackingId   = Animator.StringToHash(isAttackingParam);
        attackTriggerId = Animator.StringToHash(attackTriggerParam);
        dieTriggerId    = Animator.StringToHash(dieTriggerParam);

        if (animator == null)
            Debug.LogError($"SoldierAnimator on '{name}': no Animator found in children. " +
                           "Assign the Animator on Mixamo's character root manually.");
    }

    private void OnEnable()
    {
        if (health != null) health.OnDeath += HandleDeath;
    }

    private void OnDisable()
    {
        if (health != null) health.OnDeath -= HandleDeath;
    }

    private void Update()
    {
        if (animator == null || dead) return;

        // --- Speed -------------------------------------------------------
        float v = agent != null ? agent.velocity.magnitude : 0f;
        if (v < stationaryThreshold) v = 0f;
        float outValue = (normaliseSpeed && agent != null && agent.speed > 0.01f)
            ? v / agent.speed
            : v;
        animator.SetFloat(speedId, outValue);

        // --- Attacking ---------------------------------------------------
        // UnitCombat.IsIdle is the only public state flag; treat "not idle" as
        // "engaging a target" (covers both Chasing and Attacking sub-states).
        bool attacking = unitCombat != null && !unitCombat.IsIdle;
        if (attacking && !wasAttacking)
            animator.SetTrigger(attackTriggerId);
        animator.SetBool(isAttackingId, attacking);
        wasAttacking = attacking;
    }

    // ------------------------------------------------------------------ //
    // Death — fires the trigger and freezes gameplay components.
    // The actual GameObject destruction is owned by Health (see destroyDelay).
    // ------------------------------------------------------------------ //

    private void HandleDeath()
    {
        if (dead) return;
        dead = true;

        if (animator != null) animator.SetTrigger(dieTriggerId);

        if (!disableComponentsOnDeath) return;

        if (agent != null && agent.enabled)
        {
            agent.isStopped = true;
            agent.ResetPath();
            agent.enabled = false;
        }
        if (unitCombat != null) unitCombat.enabled = false;
    }
}
