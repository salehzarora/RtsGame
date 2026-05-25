using UnityEngine;

/// <summary>
/// Scene-level singleton that pins the canonical match layout — start positions
/// for both teams and references to the active CommandCenters. Future enemy
/// bot AI will read from here to decide where its base is and which buildings
/// it owns; this milestone only populates the data, it does not act on it.
///
/// Setup:
///   • Created automatically by <c>Tools → RTS → Match → Setup Clean Match Map</c>.
///   • Lives on a dedicated <c>MatchManager</c> GameObject at the scene root.
///   • <see cref="playerCommandCenter"/> / <see cref="enemyCommandCenter"/> are
///     assigned by the same tool; safe to leave blank for ad-hoc test scenes.
///
/// What this script intentionally does NOT do:
///   • Win / lose detection. There is no enemy bot yet, so there is nothing to
///     win or lose against.
///   • Bot AI. Comes in a later milestone.
///   • Auto-spawn extra units or buildings.
///   • Touch resources, power, HUD, combat, aircraft, vehicles, construction,
///     or any other gameplay system.
/// </summary>
[DisallowMultipleComponent]
public class MatchManager : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Singleton — convenience accessor for future bot AI code.
    // ------------------------------------------------------------------ //

    public static MatchManager Instance { get; private set; }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Start Positions (world space)")]
    [Tooltip("Where the player's CommandCenter is placed when the match starts.")]
    public Vector3 playerStartPosition = new Vector3(-80f, 0f, -70f);

    [Tooltip("Where the enemy's CommandCenter is placed when the match starts.")]
    public Vector3 enemyStartPosition  = new Vector3( 80f, 0f,  70f);

    [Header("Base References")]
    [Tooltip("Player CommandCenter scene instance. Assigned by Setup Clean Match Map. " +
             "Safe to leave null if the player base hasn't been placed yet.")]
    public CommandCenter playerCommandCenter;

    [Tooltip("Enemy CommandCenter scene instance (no CommandCenter script attached; " +
             "this stores the GameObject only). Assigned by Setup Clean Match Map. " +
             "Reserved for future enemy bot AI; not consumed by any gameplay system today.")]
    public GameObject enemyCommandCenter;

    // ------------------------------------------------------------------ //
    // Future-bot notes
    // ------------------------------------------------------------------ //
    //
    // ROADMAP (not implemented in this milestone):
    //   • Enemy base exists at enemyStartPosition with a CC + idle worker.
    //   • Enemy AI is not active. There is no resource gathering, no production,
    //     no building, no scouting, no attack-move from the enemy side yet.
    //   • Future: an EnemyBotController MonoBehaviour will read this manager to
    //     locate its own base, claim nearby ResourceNodes, gate production on
    //     resources, dispatch units toward playerStartPosition, etc.
    //   • Future: a win/lose condition (all CCs on one team destroyed) can
    //     subscribe to Health.OnDeath on both CommandCenter references.

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("MatchManager: duplicate instance destroyed.");
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
