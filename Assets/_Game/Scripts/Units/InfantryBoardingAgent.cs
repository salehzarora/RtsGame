using UnityEngine;

/// <summary>
/// Drives an infantry unit toward a target <see cref="APCTransport"/> until
/// the unit is within the APC's <see cref="APCTransport.enterRange"/>, at
/// which point the agent calls <see cref="APCTransport.LoadUnit"/> and
/// self-destructs.
///
/// Added on demand by <see cref="UnitSelector"/> when the player right-clicks
/// a friendly APC with infantry selected. Removed automatically when the
/// boarding completes, when the target APC dies, or when the player issues a
/// new move/attack order (UnitSelector calls <see cref="CancelBoarding"/>).
///
/// What this script intentionally does NOT do:
///   • Move other units in the squad. One agent per soldier, set up by
///     UnitSelector for each infantry in the selection.
///   • Forcibly interrupt combat. If the soldier was already attacking
///     something, the player-issued boarding command is the new intent —
///     UnitSelector clears UnitCombat / RocketCombat before adding this agent.
///   • Touch the APC's transport list. Only LoadUnit / UnloadAll mutate it.
/// </summary>
[DisallowMultipleComponent]
public class InfantryBoardingAgent : MonoBehaviour
{
    [Tooltip("Seconds between path re-issues while approaching the APC. Cheap " +
             "re-issues keep the unit on course if it gets knocked off path.")]
    public float pathRefreshInterval = 0.7f;

    private APCTransport target;
    private UnitMovement movement;
    private float        nextPathRefresh;

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Begin boarding <paramref name="apc"/>. Stores the target and dispatches
    /// the first MoveTo. Safe to re-call to retarget a different APC.
    /// </summary>
    public void StartBoarding(APCTransport apc)
    {
        target = apc;
        if (movement == null) movement = GetComponent<UnitMovement>();
        if (movement != null && apc != null)
        {
            movement.MoveTo(apc.transform.position);
            nextPathRefresh = Time.time + pathRefreshInterval;
        }
    }

    /// <summary>
    /// Player overrode boarding with a new command. Drop the target and
    /// self-destruct — the new command's MoveTo / SetTarget already took the
    /// movement reference.
    /// </summary>
    public void CancelBoarding()
    {
        target = null;
        Destroy(this);
    }

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        movement = GetComponent<UnitMovement>();
    }

    private void Update()
    {
        if (target == null) return;
        // APC died mid-approach — give up.
        if (!target) { Destroy(this); return; }

        float dist = Vector3.Distance(transform.position, target.transform.position);

        if (dist <= target.enterRange)
        {
            // Snapshot the target so LoadUnit's SetActive(false) doesn't race
            // with Destroy / null fields when the GameObject deactivates.
            APCTransport ride = target;
            target = null;

            // LoadUnit may refuse (transport full / no longer eligible). Either
            // way we hand off and stop trying — the player can re-issue a
            // boarding command if they really want.
            ride.LoadUnit(this.gameObject);
            Destroy(this);
            return;
        }

        // Re-issue the path periodically so collisions / detours don't strand
        // the unit between move ticks.
        if (Time.time >= nextPathRefresh)
        {
            nextPathRefresh = Time.time + pathRefreshInterval;
            if (movement != null) movement.MoveTo(target.transform.position);
        }
    }
}
