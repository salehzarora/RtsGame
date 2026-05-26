using UnityEngine;

/// <summary>
/// Phase 6: hides the gameplay-world GameObjects (Player0Base / Player1Base /
/// environment) until <see cref="NetworkMatchCoordinator.OnMatchStarted"/>
/// fires. This is what makes the player land on the Main Menu at scene
/// start instead of staring at a half-formed match.
///
/// Behaviour:
///   • <see cref="Awake"/> — set every entry in <see cref="targets"/> to
///     inactive. Their MonoBehaviours never get an Awake/Start call until
///     the match begins, which is what we want (no premature gathering,
///     no premature command relay).
///   • <see cref="HandleMatchStarted"/> — fired by the coordinator. Sets
///     every target active. The coordinator's <see cref="MultiplayerColors"/>
///     pass + <see cref="GameEntity.RemapAllForLocalPerspective"/> run on
///     the same frame, so colours and team perspective are correct by the
///     time the player sees any unit.
///
/// Single-player: this component is only added by the multiplayer match
/// setup tool. SP scenes don't get one, so SP gameplay is always active —
/// matches the pre-Phase-6 behaviour.
///
/// Multi-room cleanup: <see cref="HandleMatchLeft"/> re-hides every target
/// when the player leaves the room, so a future re-join lands them back
/// on the menu without stale state on the canvas.
/// </summary>
[DisallowMultipleComponent]
public class GameplayWorldRoot : MonoBehaviour
{
    [Header("Gameplay world objects (hidden until MatchStart)")]
    [Tooltip("GameObjects to set inactive at scene start and active at the " +
             "MatchStart event. Typical entries: Player0Base, Player1Base, " +
             "Environment. NOT HUDCanvas — the main menu already toggles that.")]
    public GameObject[] targets;

    [Header("Auto-discovery")]
    [Tooltip("When true (the default), Awake searches the scene for common " +
             "gameplay-root names (Player0Base, Player1Base, Environment, " +
             "ResourceNodes, PlayerStart, EnemyStart) and adds them to " +
             "targets if not already present. Keeps the hide-on-start " +
             "behaviour working even when the editor setup tool hasn't been " +
             "re-run after a scene change. Disable if you want strict " +
             "Inspector control.")]
    public bool autoDiscoverOnAwake = true;

    [Tooltip("Names auto-discovery looks for when autoDiscoverOnAwake is on. " +
             "Order doesn't matter; missing names are skipped silently.")]
    public string[] autoDiscoverNames = {
        "Player0Base", "Player1Base",
        "Environment", "ResourceNodes",
        "PlayerStart", "EnemyStart",
    };

    private bool revealed;

    private void Awake()
    {
        // Phase 7 — fold any auto-discovered roots into the Inspector list so
        // SetTargetsActive sees them too. Cheap; runs once.
        if (autoDiscoverOnAwake)
            AutoDiscoverTargets();

        // Hide every target IMMEDIATELY so MonoBehaviours inside them never
        // run their Awake/Start during the main-menu phase.
        SetTargetsActive(false);
    }

    private void AutoDiscoverTargets()
    {
        if (autoDiscoverNames == null || autoDiscoverNames.Length == 0) return;

        var found = new System.Collections.Generic.List<GameObject>(
            targets != null ? targets : System.Array.Empty<GameObject>());

        for (int i = 0; i < autoDiscoverNames.Length; i++)
        {
            string n = autoDiscoverNames[i];
            if (string.IsNullOrEmpty(n)) continue;

            GameObject go = GameObject.Find(n);
            if (go == null) continue;
            if (found.Contains(go)) continue;
            found.Add(go);
        }

        targets = found.ToArray();
        Debug.Log($"[GameplayWorldRoot] Auto-discovered {targets.Length} gameplay root(s).");
    }

    private void OnEnable()
    {
        NetworkMatchCoordinator.OnMatchStarted += HandleMatchStarted;
    }

    private void OnDisable()
    {
        NetworkMatchCoordinator.OnMatchStarted -= HandleMatchStarted;
    }

    private void HandleMatchStarted()
    {
        if (revealed) return;
        SetTargetsActive(true);
        revealed = true;
        Debug.Log($"[GameplayWorldRoot] Activated {CountNonNull(targets)} gameplay " +
                  $"root(s) after MatchStart.");
    }

    /// <summary>
    /// Re-hide the world when the player leaves the room. Called manually
    /// from the lobby UI; not on a Photon callback because the order
    /// between OnLeftRoom and this component's GameObject lifecycle isn't
    /// guaranteed.
    /// </summary>
    public void ReHide()
    {
        SetTargetsActive(false);
        revealed = false;
        Debug.Log($"[GameplayWorldRoot] Re-hid {CountNonNull(targets)} gameplay root(s).");
    }

    private void SetTargetsActive(bool on)
    {
        if (targets == null) return;
        for (int i = 0; i < targets.Length; i++)
            if (targets[i] != null && targets[i].activeSelf != on)
                targets[i].SetActive(on);
    }

    private static int CountNonNull(GameObject[] arr)
    {
        if (arr == null) return 0;
        int n = 0;
        for (int i = 0; i < arr.Length; i++) if (arr[i] != null) n++;
        return n;
    }
}
