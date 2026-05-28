using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Reusable owner-authoritative transform receiver for NON-OWNER clients.
///
/// Buffers timestamped snapshots (the sender's <c>Time.timeAsDouble</c>) and,
/// each frame, writes an interpolated pose to a target <see cref="Transform"/>
/// rendered at <c>(newestSenderTime − interpolationDelay)</c>. It mirrors the
/// smoothing proven on ground units in <see cref="UnitMovement"/>:
///   • a sender-time playback clock that free-runs by real deltaTime, so motion
///     is smooth between discrete arrivals and immune to network jitter / burst
///     delivery (which is what caused the stepwise "low-FPS" look);
///   • a small, TURN-AWARE dead-reckoning fallback for brief packet gaps that
///     scales toward zero during sharp direction/heading changes, so it never
///     overshoots a corner and snaps back.
///
/// Pure runtime helper — NOT a MonoBehaviour and NOT serialized. A component
/// owns an instance, feeds it samples via <see cref="Receive"/> while remote,
/// and calls <see cref="Apply"/> each frame. All transform writes happen here
/// so each consumer (UnitMovement keeps its own inline copy; AirUnitController
/// uses this) doesn't re-derive the math. Authority/ownership decisions stay
/// in the consumer — this class only plays back what it's given.
/// </summary>
public sealed class RemoteTransformInterpolator
{
    // ---- Config (defaults match UnitMovement's tuned ground values) ----- //
    public bool  useInterpolation        = true;
    public float interpolationDelay      = 0.18f;
    public int   maxSnapshotBufferSize   = 5;
    public float maxExtrapolationSeconds = 0.10f;
    public float snapDistance            = 8f;

    private readonly struct Snapshot
    {
        public readonly double     T;
        public readonly Vector3    Pos;
        public readonly Quaternion Rot;
        public Snapshot(double t, Vector3 p, Quaternion r) { T = t; Pos = p; Rot = r; }
    }

    private readonly List<Snapshot> snapshots = new List<Snapshot>(8);
    private double playbackTime;
    private bool   playbackInitialized;

    /// <summary>Drop all buffered samples and reset the playback clock.</summary>
    public void Reset()
    {
        snapshots.Clear();
        playbackInitialized = false;
    }

    /// <summary>
    /// Buffer a received owner transform, tagged with the sender's
    /// <paramref name="senderTime"/>. Snaps <paramref name="t"/> immediately
    /// (and purges the buffer) when the positional delta exceeds
    /// <see cref="snapDistance"/> — a real teleport/respawn. Stale/out-of-order
    /// samples (unreliable transport) are rejected so the buffer stays
    /// monotonically increasing in sender time.
    /// </summary>
    public void Receive(Transform t, Vector3 pos, Quaternion rot, double senderTime)
    {
        if (t == null) return;

        if (Vector3.Distance(t.position, pos) > snapDistance)
        {
            t.position = pos;
            t.rotation = rot;
            snapshots.Clear();
            playbackInitialized = false;
        }

        if (snapshots.Count > 0 && senderTime <= snapshots[snapshots.Count - 1].T)
            return;

        snapshots.Add(new Snapshot(senderTime, pos, rot));
        if (maxSnapshotBufferSize < 2) maxSnapshotBufferSize = 2;     // defensive
        while (snapshots.Count > maxSnapshotBufferSize)
            snapshots.RemoveAt(0);
    }

    /// <summary>
    /// Write this frame's interpolated pose to <paramref name="t"/>. No-op until
    /// the first sample arrives (the consumer's last-known pose is left intact).
    /// </summary>
    public void Apply(Transform t)
    {
        if (t == null) return;
        int count = snapshots.Count;
        if (count == 0) return;

        // Debug/comparison path: hard-follow the newest sample, no smoothing.
        if (!useInterpolation)
        {
            Snapshot latest = snapshots[count - 1];
            t.position = latest.Pos;
            t.rotation = latest.Rot;
            return;
        }

        double newest = snapshots[count - 1].T;

        // ---- Advance the playback clock (sender-time domain) ----------- //
        if (!playbackInitialized)
        {
            playbackTime        = newest - interpolationDelay;
            playbackInitialized = true;
        }
        else
        {
            playbackTime += Time.deltaTime;

            double target = newest - interpolationDelay;
            if (playbackTime > newest || target - playbackTime > interpolationDelay)
                playbackTime = target;
        }

        // ---- Warm-up: render time older than everything buffered ------- //
        if (playbackTime <= snapshots[0].T)
        {
            t.position = snapshots[0].Pos;
            t.rotation = snapshots[0].Rot;
            return;
        }

        // ---- Bracket scan + Lerp/Slerp --------------------------------- //
        for (int i = 0; i < count - 1; i++)
        {
            Snapshot a = snapshots[i];
            Snapshot b = snapshots[i + 1];
            if (a.T <= playbackTime && b.T >= playbackTime)
            {
                double span = b.T - a.T;
                float  tt   = span > 0.0001 ? (float)((playbackTime - a.T) / span) : 1f;
                tt = Mathf.Clamp01(tt);

                t.position = Vector3.Lerp(a.Pos,  b.Pos, tt);
                t.rotation = Quaternion.Slerp(a.Rot, b.Rot, tt);
                return;
            }
        }

        // ---- Starved: turn-aware forward dead-reckoning ---------------- //
        ExtrapolateBeyondNewest(t);
    }

    private void ExtrapolateBeyondNewest(Transform t)
    {
        int n = snapshots.Count;
        Snapshot newest = snapshots[n - 1];

        if (n < 2)
        {
            t.position = newest.Pos;
            t.rotation = newest.Rot;
            return;
        }

        Snapshot prev     = snapshots[n - 2];
        double   lastSpan = newest.T - prev.T;
        if (lastSpan <= 0.0001)
        {
            t.position = newest.Pos;
            t.rotation = newest.Rot;
            return;
        }

        Vector3 vLast = (newest.Pos - prev.Pos) / (float)lastSpan;

        // Turn factor: 1 = straight (full small extrapolation), 0 = sharp turn
        // (hold at newest, predict nothing). Most conservative of two signals.
        float turnFactor = 1f;

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

        const float RotFullDeg = 8f;
        const float RotZeroDeg = 35f;
        float rotDelta  = Quaternion.Angle(prev.Rot, newest.Rot);
        float rotFactor = Mathf.Clamp01(1f - (rotDelta - RotFullDeg) / (RotZeroDeg - RotFullDeg));
        turnFactor = Mathf.Min(turnFactor, rotFactor);

        float rawAhead = Mathf.Min((float)(playbackTime - newest.T), maxExtrapolationSeconds);
        float ahead    = rawAhead * turnFactor;

        t.position = newest.Pos + vLast * ahead;
        t.rotation = newest.Rot;     // hold newest heading — never predict rotation past the sample
    }
}
