using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Drives a unit's NavMeshAgent to a target position.
///
/// Phase 10.14 — multiplayer authoritative movement.
///   • In MP the NavMeshAgent runs only on the OWNER client (where
///     <see cref="GameEntity.ownerPlayerId"/> equals
///     <see cref="NetworkManagerRTS.LocalPlayerId"/>). On non-owner
///     clients the agent is disabled and the unit's transform is
///     reconciled from <see cref="NetworkMatchEvents"/>
///     UnitTransform events via <see cref="SetRemoteTransform"/>.
///   • <see cref="MoveTo"/> is a no-op on non-owner clients (defense in
///     depth — by the time it could be called, the agent is already off).
///   • SP and pre-MatchStart: the agent runs as before so the prototype's
///     single-player path is unchanged.
///
/// Setup unchanged: attach alongside <see cref="NavMeshAgent"/> on every
/// mobile unit prefab.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class UnitMovement : MonoBehaviour
{
    private NavMeshAgent agent;
    private GameEntity   selfEntity;

    // Playback clock for remote interpolation — lives in the SENDER's time
    // domain (snapshot timestamps are the sender's Time.timeAsDouble). It
    // free-runs by real deltaTime so motion stays smooth between discrete
    // snapshot arrivals, and is re-anchored to (newestSenderTime −
    // interpolationDelay) only when it drifts outside a healthy band.
    private double playbackTime;
    private bool   playbackInitialized;

    /// <summary>
    /// True when THIS client should run the NavMeshAgent for this unit
    /// (single-player, or this is our owned unit in MP, or MP hasn't
    /// started yet). False when this is a remote unit whose authoritative
    /// transform comes from the network.
    /// </summary>
    public bool LocallyControlled { get; private set; } = true;

    [Header("Remote interpolation (MP)")]
    [Tooltip("When true (default), remote units render against a playback " +
             "clock at (newestSenderTime − interpolationDelay), interpolating " +
             "between the two buffered snapshots that bracket that virtual " +
             "time. Because the buffer is keyed off the SENDER's timestamp, " +
             "network jitter / burst delivery no longer warps the timeline — " +
             "this is what removes the stepwise 'low-FPS' look. When false, " +
             "the remote hard-follows the newest sample (debug/comparison).")]
    public bool useSnapshotInterpolation = true;

    [Tooltip("Seconds the remote rendering trails the newest received sample. " +
             "Must exceed the broadcast interval (~0.1s at 10 Hz) so both " +
             "bracketing snapshots are already buffered when we render. 0.18s " +
             "default ≈ 1.8 intervals: enough headroom to ride out one dropped " +
             "unreliable packet without starving the interpolator.")]
    [Range(0.05f, 0.30f)] public float interpolationDelay = 0.18f;

    [Tooltip("Maximum number of past snapshots kept per unit. At 10 Hz a " +
             "buffer of 5 covers ~500ms of history — enough for the " +
             "interpolator plus one packet loss, no more.")]
    [Range(2, 16)] public int maxSnapshotBufferSize = 5;

    [Tooltip("Max seconds of dead-reckoning when the buffer is starved during " +
             "STRAIGHT movement. Kept small so a brief packet gap can't overshoot " +
             "the unit's real path. Turn detection scales this toward 0 during " +
             "sharp direction/heading changes, so the unit holds near the newest " +
             "sample instead of predicting past a corner — that overshoot was " +
             "what caused the split-second backtrack when the next snapshot " +
             "landed on the turned path.")]
    [Range(0f, 0.5f)] public float maxExtrapolationSeconds = 0.10f;

    [Tooltip("If the next received position differs from the current local " +
             "transform by more than this many world units, SNAP position AND " +
             "rotation in one frame and clear the snapshot buffer. Catches " +
             "respawns / teleports / large desyncs without dragging the unit " +
             "across the map and without letting the interpolator try to " +
             "smooth over an actual discontinuity.")]
    public float snapDistance = 8f;

    // ------------------------------------------------------------------ //
    // Snapshot buffer — one entry per received UnitTransform event.
    // ------------------------------------------------------------------ //

    private readonly struct Snapshot
    {
        public readonly double     T;
        public readonly Vector3    Pos;
        public readonly Quaternion Rot;
        public Snapshot(double t, Vector3 p, Quaternion r) { T = t; Pos = p; Rot = r; }
    }

    private readonly System.Collections.Generic.List<Snapshot> snapshots =
        new System.Collections.Generic.List<Snapshot>(8);

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        agent       = GetComponent<NavMeshAgent>();
        selfEntity  = GetComponent<GameEntity>();
        if (selfEntity != null)
            selfEntity.OnOwnershipApplied += HandleOwnershipApplied;

        // Initial gate evaluation. On Instantiate the prefab default owner
        // is 0; the dispatcher will call ApplyOwnership immediately after
        // Instantiate returns, which fires OnOwnershipApplied below and
        // re-evaluates. For SP / scene-baked units this initial eval is
        // the authoritative one.
        RefreshOwnershipGate();
    }

    private void OnDestroy()
    {
        if (selfEntity != null)
            selfEntity.OnOwnershipApplied -= HandleOwnershipApplied;
    }

    private void HandleOwnershipApplied(int newOwner)
    {
        RefreshOwnershipGate();
    }

    /// <summary>
    /// Recomputes <see cref="LocallyControlled"/> and toggles the
    /// NavMeshAgent accordingly. In MP non-owner clients have the agent
    /// off so they don't run their own pathfinding (the source of all the
    /// movement drift in the prior architecture).
    /// </summary>
    public void RefreshOwnershipGate()
    {
        bool mp = NetworkManagerRTS.IsMultiplayerEnabled;
        if (!mp)
        {
            LocallyControlled = true;
            EnsureAgentEnabled(true);
            return;
        }

        int local = NetworkManagerRTS.LocalPlayerId;
        int owner = selfEntity != null ? selfEntity.ownerPlayerId : GameEntity.PlayerOwnerId;

        // Before MatchStart fires, LocalPlayerId is -1. Treat as locally
        // controlled so SP scene-baked units in pre-match state still
        // function (they're not actually moving anyway).
        bool locallyOwned = (local < 0) || (owner == local);

        if (locallyOwned != LocallyControlled || !LocallyControlled)
        {
            Debug.Log($"[Movement] Owner gate '{name}': ownerPlayerId={owner} local={local} → " +
                      $"{(locallyOwned ? "locally controlled (agent enabled)" : "remote-only (agent disabled)")}");
        }

        LocallyControlled = locallyOwned;
        EnsureAgentEnabled(LocallyControlled);

        // Either direction of transition invalidates buffered remote samples
        // and the playback clock. Clear both so the interpolator restarts
        // cleanly from the next received snapshot (and a freshly remote unit
        // doesn't snap to origin before its first sample arrives).
        snapshots.Clear();
        playbackInitialized = false;
    }

    private void EnsureAgentEnabled(bool on)
    {
        if (agent == null) return;
        if (agent.enabled == on) return;
        agent.enabled = on;
    }

    // ------------------------------------------------------------------ //

    /// <summary>
    /// Sends the unit to the given world-space position. No-op on non-
    /// owner clients in MP — those clients receive transform updates from
    /// the owner instead.
    /// </summary>
    public void MoveTo(Vector3 destination)
    {
        if (!LocallyControlled)
        {
            // Belt-and-braces: command dispatcher already gates this in MP,
            // but if anything inside the local sim (auto-attack, boarding
            // agent, dozer builder) still calls MoveTo on a remote unit,
            // it must not start a divergent path.
            Debug.Log($"[Movement] MoveTo suppressed on remote unit '{name}' " +
                      $"(owner={(selfEntity != null ? selfEntity.ownerPlayerId : -1)}, " +
                      $"local={NetworkManagerRTS.LocalPlayerId}). Authoritative " +
                      "movement comes from the owner via UnitTransform broadcasts.");
            return;
        }
        if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;
        agent.SetDestination(destination);
        Debug.Log($"[Movement] MoveTo '{name}' → {destination:F1} (owner runs NavMeshAgent locally).");
    }

    /// <summary>
    /// Immediately stops the unit in place.
    /// </summary>
    public void Stop()
    {
        if (!LocallyControlled) return;
        if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;
        agent.ResetPath();
    }

    /// <summary>Returns true while the agent is still navigating toward its destination.</summary>
    public bool IsMoving =>
        agent != null && agent.enabled && agent.hasPath
        && agent.remainingDistance > agent.stoppingDistance;

    // ------------------------------------------------------------------ //
    // Remote-receive path — called from NetworkMatchEvents.HandleUnitTransform
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Apply an owner-broadcast transform to this remote unit, tagged with the
    /// SENDER's <paramref name="senderTime"/> (its Time.timeAsDouble at
    /// broadcast). If the delta exceeds <see cref="snapDistance"/>, snap
    /// immediately (respawn / teleport) and purge the buffer. Otherwise the
    /// sample is buffered and <see cref="Update"/> interpolates against the
    /// playback clock.
    /// </summary>
    public void SetRemoteTransform(Vector3 pos, Quaternion rot, double senderTime)
    {
        if (LocallyControlled)
        {
            // We own this unit but received a transform anyway — could happen
            // briefly during ownership transitions. Ignore; the local
            // simulation is authoritative.
            return;
        }

        float dist = Vector3.Distance(transform.position, pos);
        if (dist > snapDistance)
        {
            // Snapping is a discontinuity — purge the buffer and re-anchor the
            // playback clock so the interpolator doesn't smooth across the
            // teleport on its next tick.
            transform.position = pos;
            transform.rotation = rot;
            snapshots.Clear();
            playbackInitialized = false;
        }

        // Reject stale / out-of-order samples. Unreliable transport can deliver
        // a packet late; keeping the buffer strictly increasing in sender time
        // is what lets the bracket scan and the playback clock stay correct.
        if (snapshots.Count > 0 && senderTime <= snapshots[snapshots.Count - 1].T)
            return;

        snapshots.Add(new Snapshot(senderTime, pos, rot));
        if (maxSnapshotBufferSize < 2) maxSnapshotBufferSize = 2;     // defensive
        while (snapshots.Count > maxSnapshotBufferSize)
            snapshots.RemoveAt(0);
    }

    private void Update()
    {
        if (LocallyControlled) return;

        int count = snapshots.Count;
        if (count == 0) return;     // nothing received yet — hold seeded pose

        // Debug/comparison path: hard-follow the newest sample, no smoothing.
        if (!useSnapshotInterpolation)
        {
            Snapshot latest = snapshots[count - 1];
            transform.position = latest.Pos;
            transform.rotation = latest.Rot;
            return;
        }

        double newest = snapshots[count - 1].T;

        // ---- Advance the playback clock (sender-time domain) ----------- //
        // Free-running by real deltaTime so motion is smooth between discrete
        // arrivals. Re-anchored to (newest − delay) only when it drifts
        // outside a healthy band — never on the small jitter that used to
        // cause the stepwise look (that came from keying off arrival time).
        if (!playbackInitialized)
        {
            playbackTime        = newest - interpolationDelay;
            playbackInitialized = true;
        }
        else
        {
            playbackTime += Time.deltaTime;

            double target = newest - interpolationDelay;
            //   playbackTime > newest            → ran into the future (long
            //                                       stall, then resumed): starved.
            //   target − playbackTime > delay    → fell > 2× delay behind
            //                                       (huge burst): too laggy.
            if (playbackTime > newest || target - playbackTime > interpolationDelay)
                playbackTime = target;
        }

        // ---- Warm-up: render time older than everything buffered ------- //
        if (playbackTime <= snapshots[0].T)
        {
            transform.position = snapshots[0].Pos;
            transform.rotation = snapshots[0].Rot;
            return;
        }

        // ---- Bracket scan + Lerp/Slerp --------------------------------- //
        // O(N), N <= maxSnapshotBufferSize (default 5), so trivial.
        for (int i = 0; i < count - 1; i++)
        {
            Snapshot a = snapshots[i];
            Snapshot b = snapshots[i + 1];
            if (a.T <= playbackTime && b.T >= playbackTime)
            {
                double span = b.T - a.T;
                float  t    = span > 0.0001 ? (float)((playbackTime - a.T) / span) : 1f;
                t = Mathf.Clamp01(t);

                transform.position = Vector3.Lerp(a.Pos,  b.Pos, t);
                transform.rotation = Quaternion.Slerp(a.Rot, b.Rot, t);
                return;
            }
        }

        // ---- Starved: playbackTime is newer than the newest sample ----- //
        // Dead-reckon forward at the velocity implied by the last two samples
        // so straight movement keeps its real speed during a brief packet gap.
        // Turn-aware: a sharp direction/heading change scales the prediction
        // toward 0 so we never shoot past a corner (the cause of the visible
        // backtrack when the next on-the-turn snapshot arrives).
        ExtrapolateBeyondNewest();
    }

    private void ExtrapolateBeyondNewest()
    {
        int n = snapshots.Count;
        Snapshot newest = snapshots[n - 1];

        // Need two samples for a velocity; with fewer (or a degenerate span)
        // just hold the newest pose.
        if (n < 2)
        {
            transform.position = newest.Pos;
            transform.rotation = newest.Rot;
            return;
        }

        Snapshot prev     = snapshots[n - 2];
        double   lastSpan = newest.T - prev.T;
        if (lastSpan <= 0.0001)
        {
            transform.position = newest.Pos;
            transform.rotation = newest.Rot;
            return;
        }

        Vector3 vLast = (newest.Pos - prev.Pos) / (float)lastSpan;   // newest-segment velocity

        // ---- Turn detection: 1 = straight (full small extrapolation), ----- //
        // 0 = sharp turn (hold at newest, predict nothing). Two independent
        // signals; take the most conservative.
        float turnFactor = 1f;

        // (a) Direction change between the two most recent move segments
        //     (needs three samples). dot: 1 straight, 0 at 90°, <0 reversing.
        if (n >= 3)
        {
            Snapshot prev2    = snapshots[n - 3];
            double   prevSpan = prev.T - prev2.T;
            if (prevSpan > 0.0001)
            {
                Vector3 vPrev = (prev.Pos - prev2.Pos) / (float)prevSpan;
                if (vPrev.sqrMagnitude > 0.0001f && vLast.sqrMagnitude > 0.0001f)
                {
                    float dot = Vector3.Dot(vPrev.normalized, vLast.normalized);
                    turnFactor = Mathf.Min(turnFactor, Mathf.Clamp01(dot));
                }
            }
        }

        // (b) Heading change between the two most recent rotations. Below
        //     RotFullDeg: no penalty; above RotZeroDeg: no extrapolation.
        const float RotFullDeg = 8f;
        const float RotZeroDeg = 35f;
        float rotDelta  = Quaternion.Angle(prev.Rot, newest.Rot);
        float rotFactor = Mathf.Clamp01(1f - (rotDelta - RotFullDeg) / (RotZeroDeg - RotFullDeg));
        turnFactor = Mathf.Min(turnFactor, rotFactor);

        // Capped, turn-scaled forward prediction. During a turn turnFactor → 0,
        // so ahead → 0 and the unit holds at newest.Pos; the next snapshot then
        // moves it FORWARD onto the turned path (no backward correction).
        float rawAhead = Mathf.Min((float)(playbackTime - newest.T), maxExtrapolationSeconds);
        float ahead    = rawAhead * turnFactor;

        transform.position = newest.Pos + vLast * ahead;
        transform.rotation = newest.Rot;     // hold newest heading — never predict rotation past the sample
    }
}
