using UnityEngine;

/// <summary>
/// Marks a building as the enemy team's resource drop-off. Parallel sibling of
/// the player-side <see cref="CommandCenter"/> script. The presence of this
/// component is the canonical way for <see cref="EnemyWorkerAI"/> (and any
/// future enemy bot AI) to locate the enemy base in the scene.
///
/// Setup:
///   • Added automatically by <c>Tools → RTS → Match → Setup Clean Match Map</c>
///     to the enemy CommandCenter GameObject.
///   • No Inspector fields. <see cref="EnemyResourceManager"/> is resolved via
///     its singleton, so no scene reference is needed.
///
/// What this script intentionally does NOT do:
///   • Make the enemy CC selectable by the player. No SelectableBuilding here.
///   • Run enemy production. No CommandCenterProducer / unit spawning yet.
///   • Touch the player HUD or PlayerResourceManager. The two economies are
///     entirely separate (see <see cref="EnemyResourceManager"/>).
/// </summary>
[DisallowMultipleComponent]
public class EnemyCommandCenter : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Public API — called by EnemyWorkerAI on arrival.
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Deposit gathered resources into the enemy total. No-op (with a one-time
    /// warning) when no <see cref="EnemyResourceManager"/> is in the scene.
    /// </summary>
    public void Deposit(int amount)
    {
        if (amount <= 0) return;

        EnemyResourceManager mgr = EnemyResourceManager.Instance;
        if (mgr == null)
        {
            if (!warnedNoManager)
            {
                Debug.LogWarning($"[EnemyCC:{name}] Deposit({amount}) ignored — " +
                                 "no EnemyResourceManager in the scene.");
                warnedNoManager = true;
            }
            return;
        }

        mgr.AddResources(amount);
    }

    // ------------------------------------------------------------------ //
    // Internal — one-shot guard so we don't spam the log every deposit
    // when EnemyResourceManager is missing.
    // ------------------------------------------------------------------ //

    private bool warnedNoManager;
}
