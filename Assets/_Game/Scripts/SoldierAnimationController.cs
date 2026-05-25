using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Drives a Mixamo-style Animator from gameplay state. Attach to the GameObject
/// that owns the Animator (typically SoldierPrefab → SoldierVisualRoot → character).
///
/// Animator parameters expected on the controller:
///   • Speed  (float)   — set each frame from the parent NavMeshAgent's velocity.
///   • Attack (trigger) — fired by external scripts via <see cref="PlayAttack"/>.
///   • Die    (trigger) — fired by external scripts via <see cref="PlayDeath"/>.
///
/// Why GetComponentInParent for NavMeshAgent?
///   The agent lives on SoldierPrefab (the root), one level above the character
///   GameObject. We walk up the hierarchy so this script keeps working if you
///   move the character mesh into a deeper visual sub-tree.
///
/// What it does NOT do:
///   • Decide WHEN the soldier should attack or die. Other gameplay scripts
///     (e.g. UnitCombat / Health) call <see cref="PlayAttack"/> / <see cref="PlayDeath"/>.
///   • Destroy the GameObject. Death cleanup is owned by Health.cs.
///   • Modify the Animator Controller or any clip data at runtime.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
public class SoldierAnimationController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Parameter names — change here if your controller uses different names.
    // ------------------------------------------------------------------ //

    [Header("Animator Parameter Names")]
    [Tooltip("Float parameter driven from the NavMeshAgent's velocity magnitude.")]
    public string speedParam = "Speed";

    [Tooltip("Trigger parameter fired by PlayAttack().")]
    public string attackTriggerParam = "Attack";

    [Tooltip("Trigger parameter fired by PlayDeath().")]
    public string dieTriggerParam = "Die";

    // ------------------------------------------------------------------ //
    // Tuning
    // ------------------------------------------------------------------ //

    [Header("Tuning")]
    [Tooltip("Velocity (m/s) below this is treated as zero — prevents idle/walk " +
             "flicker while the agent settles at a destination.")]
    public float stationaryThreshold = 0.05f;

    [Tooltip("If true, Speed is normalised by NavMeshAgent.speed so it stays in 0..1. " +
             "Recommended for blend trees keyed on 0 (idle) → 1 (run).")]
    public bool normaliseSpeed = true;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private Animator animator;
    private NavMeshAgent agent;       // may be null — script tolerates that

    private int speedId;
    private int attackTriggerId;
    private int dieTriggerId;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        animator = GetComponent<Animator>();
        agent    = GetComponentInParent<NavMeshAgent>();   // null-safe lookup

        speedId         = Animator.StringToHash(speedParam);
        attackTriggerId = Animator.StringToHash(attackTriggerParam);
        dieTriggerId    = Animator.StringToHash(dieTriggerParam);

        if (animator == null)
            Debug.LogWarning($"[SoldierAnimationController] '{name}' has no Animator. " +
                             "Add one and assign SoldierAnimatorController_REAL.controller to it.");
    }

    private void Update()
    {
        if (animator == null) return;

        float v = agent != null ? agent.velocity.magnitude : 0f;
        if (v < stationaryThreshold) v = 0f;

        float outValue = (normaliseSpeed && agent != null && agent.speed > 0.01f)
            ? v / agent.speed
            : v;

        animator.SetFloat(speedId, outValue);
    }

    // ------------------------------------------------------------------ //
    // Public API — called by UnitCombat / Health hooks.
    // ------------------------------------------------------------------ //

    /// <summary>Fires the Attack trigger. Safe no-op if the Animator is missing.</summary>
    public void PlayAttack()
    {
        if (animator == null) return;
        animator.SetTrigger(attackTriggerId);
    }

    /// <summary>Fires the Die trigger. Safe no-op if the Animator is missing.</summary>
    public void PlayDeath()
    {
        if (animator == null) return;
        animator.SetTrigger(dieTriggerId);
    }
}
