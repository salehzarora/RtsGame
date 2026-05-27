using UnityEngine;

/// <summary>
/// Stores ONE faction's resource total. In single-player there's exactly one
/// instance (player 0); in multiplayer the match-setup tool also spawns a
/// second instance for player 1 with <see cref="ownerPlayerId"/> = 1.
///
/// Look-up is handled by <see cref="ResourceBank.For"/> — call that instead
/// of <c>FindAnyObjectByType&lt;PlayerResourceManager&gt;</c> so you always
/// get the bank that matches the spending entity's owner.
///
/// Setup:
///   1. Attach to a GameObject in the scene (typically a GameManager or a
///      per-player BaseRoot).
///   2. Set <see cref="ownerPlayerId"/> to the faction id this bank belongs
///      to. Default 0 keeps single-player working without any wiring change.
///   3. Optionally set <see cref="startingResources"/> for non-zero seed.
/// </summary>
public class PlayerResourceManager : MonoBehaviour
{
    [Header("Ownership")]
    [Tooltip("Faction id this bank belongs to. Player = 0, Enemy AI / Player 2 = 1. " +
             "ResourceBank routes producer spends to the matching bank.")]
    public int ownerPlayerId = 0;

    [Header("Starting balance")]
    [Tooltip("Resources this bank holds at scene start. 0 by default (the " +
             "legacy single-player setup).")]
    public int startingResources = 0;

    public int CurrentResources { get; private set; }

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        CurrentResources = Mathf.Max(0, startingResources);

        // Register with the bank service so per-owner lookups resolve.
        ResourceBank.Register(ownerPlayerId, this);

        Debug.Log($"[Resources] Player {ownerPlayerId} starting resources: {CurrentResources}");
    }

    private void OnDestroy()
    {
        ResourceBank.Unregister(ownerPlayerId, this);
    }

    // ------------------------------------------------------------------ //

    /// <summary>Add resources to this bank. Called by CommandCenter.Deposit.</summary>
    public void AddResources(int amount)
    {
        if (amount <= 0) return;
        CurrentResources += amount;
        Debug.Log($"[Resources] Player {ownerPlayerId} +{amount}  →  Total: {CurrentResources}");

        // Phase 10.3 — master-authoritative broadcast. Local mutation has
        // already run identically on every client (both run AddResources via
        // CommandCenter.Deposit at the same point in their local sim); the
        // master ALSO broadcasts the absolute newAmount so any drift gets
        // snapped on the non-master side. Non-master + suppressed callers
        // are gated inside NetworkMatchEvents.ShouldBroadcast.
        NetworkMatchEvents.BroadcastResourceChanged(
            ownerPlayerId, CurrentResources, amount, "WorkerDeposit");
    }

    /// <summary>Returns true if this bank currently has at least <paramref name="amount"/>.</summary>
    public bool CanAfford(int amount) => CurrentResources >= amount;

    /// <summary>
    /// Attempt to spend resources. Returns true and deducts the amount.
    /// Returns false (without deducting) if insufficient funds.
    /// </summary>
    public bool SpendResources(int amount)
    {
        if (amount <= 0) return true;
        if (CurrentResources < amount)
        {
            Debug.Log($"[Resources] Player {ownerPlayerId}: not enough " +
                      $"(need {amount}, have {CurrentResources}).");
            return false;
        }

        CurrentResources -= amount;
        Debug.Log($"[Resources] Player {ownerPlayerId} -{amount}  →  Total: {CurrentResources}");

        NetworkMatchEvents.BroadcastResourceChanged(
            ownerPlayerId, CurrentResources, -amount, "Spend");
        return true;
    }

    /// <summary>
    /// Authoritative reset of <see cref="CurrentResources"/> to
    /// <paramref name="amount"/>. Used at MatchStart so both clients agree
    /// on the host-selected starting balance. Clamps negative values to 0.
    ///
    /// Defensive: also writes <see cref="startingResources"/> so that if
    /// this is called BEFORE <see cref="Awake"/> runs (e.g. on a base that
    /// is still hidden by <see cref="GameplayWorldRoot"/> at MatchStart),
    /// the subsequent Awake initialises <see cref="CurrentResources"/> to
    /// the right value instead of the prefab default.
    /// </summary>
    public void SetResources(int amount)
    {
        int next = Mathf.Max(0, amount);
        startingResources = next;
        CurrentResources  = next;
        Debug.Log($"[Resources] Player {ownerPlayerId} starting resources set to {next}");

        // Phase 10.3 — broadcast the authoritative new amount so any non-
        // master client that missed the lobby's room property still snaps
        // to the right value. The master gate inside NetworkMatchEvents
        // means we only broadcast from the canonical side.
        NetworkMatchEvents.BroadcastResourceChanged(
            ownerPlayerId, next, 0, "SetResources");
    }

    // ------------------------------------------------------------------ //
    // Phase 10.3 — inbound apply (called by NetworkMatchEvents)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Snap <see cref="CurrentResources"/> to the master-broadcast amount.
    /// Treats <paramref name="newAmount"/> as absolute (not relative) — the
    /// master is the canonical source. <paramref name="delta"/> and
    /// <paramref name="reason"/> are used only for diagnostic logs.
    /// </summary>
    public void ApplyFromNetwork(int newAmount, int delta, string reason)
    {
        int next = Mathf.Max(0, newAmount);
        if (next == CurrentResources) return;        // already matches — no log spam

        Debug.Log($"[NetResources] Apply owner={ownerPlayerId} {CurrentResources} -> {next} " +
                  $"(delta={delta}, reason={reason})");
        CurrentResources = next;
    }
}
