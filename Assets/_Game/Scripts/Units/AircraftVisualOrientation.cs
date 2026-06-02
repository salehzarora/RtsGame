using UnityEngine;

/// <summary>
/// Drives aircraft yaw / pitch / bank from movement so the jet actually
/// "flies" instead of sliding through the air. <see cref="AirUnitController"/>
/// updates position only for many flight states (TakeoffRoll, Climbing,
/// FlyingToTarget, AttackRun, AttackEgress, WideReturnTurn, Returning, …);
/// this component closes that gap by inferring orientation each frame from
/// the position delta and the current <see cref="AirUnitController.State"/>.
///
/// <para>Architecture</para>
/// <list type="bullet">
///   <item><b>Root yaw</b> tracks horizontal velocity, smoothed with
///         <see cref="yawTurnSpeed"/>. Updated only on the locally controlled
///         owner — remote clients receive the rotation through the existing
///         transform sync / <see cref="RemoteTransformInterpolator"/>, so we
///         must not fight that on non-owners.</item>
///   <item><b>Visual pitch / bank</b> are written every frame on every client
///         to <see cref="visual"/>'s <c>localRotation</c>, COMPOSED with the
///         <see cref="modelOffset"/> captured at <see cref="Awake"/>. The
///         model offset (set by Replace StrikeJet Visual With Jet2 ≈ -96.7°)
///         is therefore preserved exactly — pitch + bank ride on top of it,
///         in the parent (root) frame.</item>
///   <item><b>Pitch target</b> is state-driven (Climbing → <see cref="takeoffPitch"/>,
///         LandingApproach / FinalLanding → <see cref="landingPitch"/>, ground states → 0,
///         everything else airborne → <see cref="cruisePitch"/>).</item>
///   <item><b>Bank target</b> is derived from the actual yaw rate so turns
///         visibly lean. Sign-flipped so a right turn rolls the wings right.</item>
///   <item><b>Parked snap</b>: when the FSM is Parked, root rotation is re-asserted
///         to <see cref="AirUnitController.HomeSlot"/>.rotation each frame. That
///         matches what <see cref="AirUnitController.AssignHome"/> writes and
///         keeps the apron layout consistent if pitch/bank were carried in mid-air.</item>
/// </list>
///
/// <para>Setup</para>
/// <list type="number">
///   <item>Attach to StrikeJetPrefab root (same GameObject as
///         <see cref="AirUnitController"/>). The editor tool
///         <c>Tools → RTS → Aircraft → Add Aircraft Visual Orientation</c>
///         does this and saves the prefab.</item>
///   <item>Ensure a child named <c>Visual</c> exists with the model-offset
///         rotation already applied (~-96.7° on Y).</item>
///   <item>Defaults are tuned for the StrikeJet — adjust per profile if needed.</item>
/// </list>
///
/// <para>Multiplayer</para>
/// On the owner the FSM moves the position; this component derives orientation
/// from that motion and the result is broadcast as part of the regular unit
/// transform sync. Non-owner clients receive both position and rotation
/// already smoothed by <see cref="RemoteTransformInterpolator"/>, so we skip
/// yaw on remote — but still apply pitch+bank to the Visual so banking looks
/// right when only positions are interpolated. Pitch+bank are deterministic
/// from local velocity, so the two clients converge.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(AirUnitController))]
public class AircraftVisualOrientation : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector — references
    // ------------------------------------------------------------------ //

    [Header("References")]
    [Tooltip("Child holding the LODs / mesh. If null, auto-located by name " +
             "'Visual' under the root at Awake. Its starting localRotation " +
             "is captured as the model-forward offset and preserved.")]
    public Transform visual;

    [Tooltip("Optional override for the model-forward offset. When this is " +
             "Vector3.zero the Awake-captured Visual.localRotation is used " +
             "verbatim, which is the recommended path. Set non-zero only if " +
             "you want to FORCE a different starting orientation.")]
    public Vector3 modelForwardOffsetEulerOverride = Vector3.zero;

    // ------------------------------------------------------------------ //
    // Inspector — yaw
    // ------------------------------------------------------------------ //

    [Header("Yaw (root heading from velocity)")]
    [Tooltip("How aggressively root yaw chases the velocity direction. " +
             "Higher = faster snap; 4–8 reads as a fighter jet, 2–3 reads as " +
             "a heavier transport.")]
    [Range(0.5f, 20f)]
    public float yawTurnSpeed = 6f;

    [Tooltip("Horizontal speed (m/s) below which yaw is NOT updated from " +
             "velocity (otherwise rounding noise spins the parked jet). " +
             "Pitch/bank also relax to neutral below this.")]
    public float minVelocityForRotation = 0.1f;

    // ------------------------------------------------------------------ //
    // Inspector — pitch
    // ------------------------------------------------------------------ //

    [Header("Pitch (Visual nose up/down) — HYBRID: state baseline + vertical velocity")]
    [Tooltip("Climb pitch in degrees. Unity convention: NEGATIVE = nose UP " +
             "when the visual nose points along root +Z. Used during " +
             "FlightState.Climbing and as the lower clamp for the " +
             "vertical-velocity contribution. Defaults bumped to -18° because " +
             "Climbing only lasts ~1.5s — at the previous -12° the pitch " +
             "barely settled before FlyingToTarget flattened it.")]
    public float takeoffPitch = -18f;

    [Tooltip("Approach pitch in degrees. POSITIVE = nose down. Used during " +
             "FinalLanding (the actual descent) and as the upper clamp for " +
             "the vertical-velocity contribution. Default bumped from 6° to " +
             "10° for visibility under the RTS top-down camera.")]
    public float landingPitch = 10f;

    [Tooltip("Level-flight pitch baseline for FlyingToTarget / FlyingToPoint / " +
             "Returning / Patrol / AttackRun / AttackEgress / WideReturnTurn / " +
             "LandingApproach (level glide before descent). Vertical-velocity " +
             "blending still nudges this if useVerticalVelocityForPitch is on.")]
    public float cruisePitch = 0f;

    [Tooltip("Smoothing rate for pitch toward its target. Higher = snappier " +
             "nose response. 3–5 reads as a fighter; lower for transports.")]
    [Range(0.5f, 20f)]
    public float pitchResponsiveness = 4f;

    [Tooltip("HYBRID mode (recommended). When ON, pitch combines: " +
             "(a) the state-based baseline (Climbing → takeoffPitch, " +
             "FinalLanding → landingPitch, LandingApproach → cruisePitch, " +
             "etc.), and (b) actual vertical velocity scaled by " +
             "verticalVelocityToPitchScale. The combined target is clamped to " +
             "[takeoffPitch, landingPitch]. When OFF, pitch is purely " +
             "state-based (legacy behaviour).")]
    public bool useVerticalVelocityForPitch = true;

    [Tooltip("Degrees of pitch per 1 m/s of vertical velocity. With default " +
             "2.0: a +6 m/s climb adds -12° to pitch (nose up), a -5 m/s " +
             "descent adds +10° (nose down). The result is clamped to " +
             "[takeoffPitch, landingPitch].")]
    public float verticalVelocityToPitchScale = 2f;

    [Tooltip("Vertical-velocity deadband (m/s). Vertical motion smaller than " +
             "this contributes 0° — kills jitter when FlyingToTarget holds " +
             "altitude within a few cm.")]
    public float verticalVelocityDeadband = 0.2f;

    // ------------------------------------------------------------------ //
    // Inspector — test / force
    // ------------------------------------------------------------------ //

    [Header("Force-test pitch (rotation pipeline verification)")]
    [Tooltip("When ON, ignores all state/velocity logic and writes the value " +
             "in forcedPitchDegrees directly to the Visual every frame. Use " +
             "this on a PARKED aircraft to confirm pitch reaches the correct " +
             "Transform with the correct ModelOffset composition. Turn back " +
             "OFF after verification — leaving it on freezes the visual nose.")]
    public bool forceTestPitch = false;

    [Tooltip("Pitch value (degrees) written when forceTestPitch is ON. Try " +
             "-25 (nose up) or +15 (nose down) to clearly see the rotation " +
             "land on the Visual child.")]
    public float forcedPitchDegrees = -25f;

    // ------------------------------------------------------------------ //
    // Inspector — bank
    // ------------------------------------------------------------------ //

    [Header("Bank (roll from yaw rate)")]
    [Tooltip("Maximum bank angle in degrees. Real fighters bank 30–60°; 30° " +
             "keeps the jet readable in an RTS top-down view.")]
    [Range(0f, 60f)]
    public float maxBankAngle = 30f;

    [Tooltip("Yaw rate (deg/s) at which the jet hits full bank. Lower = more " +
             "sensitive (any turn looks dramatic); higher = only hard turns " +
             "bank fully.")]
    public float bankYawRateForFullBank = 45f;

    [Tooltip("Smoothing rate for bank toward its target. Higher = snappier " +
             "roll-in / roll-out.")]
    [Range(0.5f, 20f)]
    public float bankResponsiveness = 5f;

    // ------------------------------------------------------------------ //
    // Inspector — diagnostics
    // ------------------------------------------------------------------ //

    [Header("Diagnostics")]
    [Tooltip("Logs a per-frame line with state / yaw / pitch / bank / speeds. " +
             "Spammy — leave OFF in builds; flip on for a single test sortie.")]
    public bool debugAircraftOrientation = false;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private AirUnitController controller;

    /// <summary>
    /// The Visual child's starting localRotation, captured once at Awake.
    /// Every LateUpdate composes pitch+bank with THIS so the existing model
    /// offset (-96.7° from Replace StrikeJet Visual With Jet2) is preserved.
    /// Public read-only so the validation tool can audit it.
    /// </summary>
    public Quaternion ModelOffset { get; private set; } = Quaternion.identity;

    private Vector3 lastPosition;
    private float   lastYawDeg;
    private float   smoothedPitch;
    private float   smoothedBank;
    private bool    initialized;

    // --- diagnostics (read by Validate Aircraft Orientation when running) ---
    /// <summary>Most recently observed FlightState that produced a non-zero target pitch from STATE only.</summary>
    public AirUnitController.FlightState LastNonZeroStatePitchState { get; private set; }
    /// <summary>Most recent vertical velocity (m/s) sampled in LateUpdate.</summary>
    public float LastVerticalVelocity { get; private set; }
    /// <summary>Most recent state-only pitch component (before velocity blend).</summary>
    public float LastStatePitch { get; private set; }
    /// <summary>Most recent vertical-velocity pitch contribution.</summary>
    public float LastVelocityPitch { get; private set; }
    /// <summary>Most recent combined+clamped pitch target (what the smoothing chases).</summary>
    public float LastTargetPitch { get; private set; }

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        controller = GetComponent<AirUnitController>();

        if (visual == null)
        {
            Transform t = transform.Find("Visual");
            if (t != null) visual = t;
        }

        if (visual == null)
        {
            Debug.LogWarning($"[AircraftOrientation:{name}] No 'Visual' child found — " +
                             "pitch/bank will be skipped. Yaw on the root still works.");
            ModelOffset = Quaternion.identity;
        }
        else if (modelForwardOffsetEulerOverride.sqrMagnitude > 0.000001f)
        {
            ModelOffset = Quaternion.Euler(modelForwardOffsetEulerOverride);
            visual.localRotation = ModelOffset;
        }
        else
        {
            ModelOffset = visual.localRotation;
        }

        lastPosition = transform.position;
        lastYawDeg   = transform.eulerAngles.y;
        initialized  = true;
    }

    // ------------------------------------------------------------------ //
    // Per-frame
    // ------------------------------------------------------------------ //

    private void LateUpdate()
    {
        if (!initialized) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // ============================================================== //
        // FORCE-TEST PITCH — pipeline verifier
        // Bypasses all state / velocity logic so the user can confirm the
        // pitch rotation IS landing on the Visual transform with the correct
        // ModelOffset composition. Leave OFF in normal play.
        // ============================================================== //
        if (forceTestPitch && visual != null)
        {
            smoothedPitch = forcedPitchDegrees;
            smoothedBank  = 0f;
            visual.localRotation = Quaternion.Euler(forcedPitchDegrees, 0f, 0f) * ModelOffset;
            if (debugAircraftOrientation)
            {
                Debug.Log($"[AircraftOrientation:{name}] FORCE-TEST pitch = {forcedPitchDegrees:F1}° " +
                          $"applied to Visual='{visual.name}'. ModelOffset(euler)={ModelOffset.eulerAngles}. " +
                          "Turn forceTestPitch OFF after verifying.");
            }
            return;
        }

        // --- velocity ----------------------------------------------------
        Vector3 curPos   = transform.position;
        Vector3 vel      = (curPos - lastPosition) / dt;
        lastPosition     = curPos;

        Vector2 horiz    = new Vector2(vel.x, vel.z);
        float   horizSp  = horiz.magnitude;
        float   vy       = vel.y;
        LastVerticalVelocity = vy;

        // --- state -------------------------------------------------------
        AirUnitController.FlightState s = controller != null
            ? controller.State
            : AirUnitController.FlightState.Parked;
        bool ground       = IsGroundState(s);
        bool parked       = (s == AirUnitController.FlightState.Parked);
        bool ownerDrives  = controller == null || controller.LocallyControlled;

        // --- yaw (owner only) -------------------------------------------
        // Remote clients receive smoothed rotation from the owner via
        // RemoteTransformInterpolator — fighting that here causes a
        // double-smoothed jitter. Owner derives yaw from velocity and that
        // becomes the broadcasted rotation, so remote ends up correct too.
        if (ownerDrives)
        {
            if (parked && controller != null && controller.HomeSlot != null)
            {
                // Snap to slot rotation — matches AssignHome's one-shot write
                // and survives mid-air pitch/bank carry-over.
                transform.rotation = controller.HomeSlot.rotation;
            }
            else if (horizSp > minVelocityForRotation)
            {
                float desiredYaw      = Mathf.Atan2(horiz.x, horiz.y) * Mathf.Rad2Deg;
                Quaternion targetRot  = Quaternion.Euler(0f, desiredYaw, 0f);
                float t               = 1f - Mathf.Exp(-yawTurnSpeed * dt);
                transform.rotation    = Quaternion.Slerp(transform.rotation, targetRot, t);
            }
        }

        // --- yaw rate (for bank) ----------------------------------------
        float curYawDeg = transform.eulerAngles.y;
        float yawDelta  = Mathf.DeltaAngle(lastYawDeg, curYawDeg);
        float yawRate   = yawDelta / dt; // deg/s, signed
        lastYawDeg      = curYawDeg;

        // ============================================================== //
        // HYBRID PITCH TARGET
        //   statePitch    : Climbing → takeoffPitch, FinalLanding → landingPitch,
        //                   everything else airborne → cruisePitch (0).
        //   velocityPitch : -vy * verticalVelocityToPitchScale, deadbanded.
        //                   Negative because nose-UP is NEGATIVE pitch in Unity
        //                   convention when nose points along root +Z, and
        //                   POSITIVE vy means climbing → we want nose up.
        //   target        : clamp(statePitch + velocityPitch, takeoffPitch, landingPitch).
        //   ground states : forced to 0 (parked, taxi, alignment, roll all stay level).
        //
        // Why hybrid: Climbing only lasts ~1.5s before the FSM hands off to
        // FlyingToTarget, so a pure state lookup flattens pitch the instant
        // the jet finishes climbing — even though visually it's still pulling
        // away from the runway. Vertical velocity carries the tilt through
        // the transition. Same for descent: LandingApproach is level cruise,
        // but FinalLanding actually descends — velocity-based pitch lights up
        // exactly when vy goes negative, regardless of which named state we're in.
        // ============================================================== //
        float statePitch    = ResolveStatePitchBaseline(s);
        float velocityPitch = 0f;
        if (useVerticalVelocityForPitch && Mathf.Abs(vy) > verticalVelocityDeadband)
            velocityPitch = -vy * verticalVelocityToPitchScale;

        float pitchMin = Mathf.Min(takeoffPitch, landingPitch);
        float pitchMax = Mathf.Max(takeoffPitch, landingPitch);
        float targetPitch = Mathf.Clamp(statePitch + velocityPitch, pitchMin, pitchMax);
        if (ground) targetPitch = 0f;

        LastStatePitch    = statePitch;
        LastVelocityPitch = velocityPitch;
        LastTargetPitch   = targetPitch;
        if (Mathf.Abs(statePitch) > 0.01f) LastNonZeroStatePitchState = s;

        // --- bank target -------------------------------------------------
        float targetBank;
        if (ground || horizSp <= minVelocityForRotation)
        {
            targetBank = 0f;
        }
        else
        {
            // Sign flip: a positive yawDelta (turning right / clockwise from
            // above) should ROLL the right wing DOWN. Unity Z-rotation:
            // negative Z on the Visual lowers the right wing — hence the
            // minus sign so a right turn produces negative Z bank.
            float ratio = Mathf.Clamp(yawRate / Mathf.Max(bankYawRateForFullBank, 0.01f), -1f, 1f);
            targetBank  = -ratio * maxBankAngle;
        }

        // --- smooth -------------------------------------------------------
        float pitchLerp = 1f - Mathf.Exp(-pitchResponsiveness * dt);
        float bankLerp  = 1f - Mathf.Exp(-bankResponsiveness  * dt);
        smoothedPitch   = Mathf.Lerp(smoothedPitch, targetPitch, pitchLerp);
        smoothedBank    = Mathf.Lerp(smoothedBank,  targetBank,  bankLerp);

        // --- apply to Visual --------------------------------------------
        // ORDER MATTERS: Euler(pitch, 0, bank) * ModelOffset.
        // Read right-to-left when applied to a vertex v:
        //   v_root = Euler(pitch, 0, bank) * ModelOffset * v
        // 1. ModelOffset first rotates v from the asset's natural frame
        //    (model nose along asset +X) into a root-aligned frame
        //    (nose now along root +Z).
        // 2. Euler(pitch, 0, bank) then rotates that result in PARENT (root)
        //    space — pitch about root-X = body lateral axis (nose up/down),
        //    bank about root-Z = body forward axis (right wing down/up).
        //
        // The REVERSED order — ModelOffset * Euler(pitch, 0, bank) — would
        // apply pitch/bank in ASSET space first, where asset-X is the nose
        // direction. Rotating about your own nose axis is ROLL, not pitch.
        // So bank would become pitch and pitch would become roll. Tested,
        // confirmed wrong — keep the current order.
        if (visual != null)
        {
            Quaternion flightTilt = Quaternion.Euler(smoothedPitch, 0f, smoothedBank);
            visual.localRotation  = flightTilt * ModelOffset;
        }

        if (debugAircraftOrientation)
        {
            Debug.Log($"[AircraftOrientation:{name}] state={s} " +
                      $"speed={horizSp:F2} verticalVel={vy:F2} " +
                      $"statePitch={statePitch:F1}° velPitch={velocityPitch:F1}° " +
                      $"targetPitch={targetPitch:F1}° currentPitch={smoothedPitch:F1}° " +
                      $"bank={smoothedBank:F1}° owner={(ownerDrives ? "yes" : "no")}");
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// State-based pitch BASELINE. Vertical-velocity blending is added on
    /// top in LateUpdate, then the sum is clamped to [takeoffPitch, landingPitch].
    ///
    /// State mapping confirmed against AirUnitController:
    ///   • Climbing       — actually airborne, climbing to flightAltitude  → takeoffPitch (nose UP)
    ///   • FinalLanding   — actually descending from flightAltitude to ground → landingPitch (nose DOWN)
    ///   • LandingApproach — flying LEVEL at flightAltitude toward LandingStart,
    ///                       not yet descending. Baseline = cruisePitch (0); the
    ///                       vertical-velocity blend keeps pitch level here too
    ///                       since vy ≈ 0.
    ///   • Everything else airborne → cruisePitch (0). Vertical-velocity blend
    ///     will still tilt if something changes altitude.
    /// </summary>
    private float ResolveStatePitchBaseline(AirUnitController.FlightState s)
    {
        switch (s)
        {
            case AirUnitController.FlightState.Climbing:
                return takeoffPitch;

            case AirUnitController.FlightState.FinalLanding:
                return landingPitch;

            // Airborne level-cruise group (LandingApproach included — it's level
            // flight at flightAltitude; the descent doesn't start until FinalLanding).
            case AirUnitController.FlightState.LandingApproach:
            case AirUnitController.FlightState.FlyingToTarget:
            case AirUnitController.FlightState.RepositioningForAttack:
            case AirUnitController.FlightState.AttackRun:
            case AirUnitController.FlightState.AttackEgress:
            case AirUnitController.FlightState.WideReturnTurn:
            case AirUnitController.FlightState.FlyingToPoint:
            case AirUnitController.FlightState.PatrollingPoint:
            case AirUnitController.FlightState.Returning:
            case AirUnitController.FlightState.WaitingForLandingClearance:
                return cruisePitch;

            // Ground / pre-flight states — IsGroundState forces 0 in LateUpdate,
            // but return 0 here as a defensive default too.
            default:
                return 0f;
        }
    }

    private static bool IsGroundState(AirUnitController.FlightState s)
    {
        switch (s)
        {
            case AirUnitController.FlightState.Parked:
            case AirUnitController.FlightState.WaitingForTakeoffClearance:
            case AirUnitController.FlightState.TaxiingToRunway:
            case AirUnitController.FlightState.AligningForTakeoff:
            case AirUnitController.FlightState.WaitingForBatchTakeoff:
            case AirUnitController.FlightState.TakeoffRoll:
            case AirUnitController.FlightState.TaxiingToSlot:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Returns "state", "vertical-velocity", or "hybrid" depending on which
    /// inputs feed the pitch target. Used by Validate Aircraft Orientation.
    /// </summary>
    public string PitchMode
    {
        get
        {
            if (forceTestPitch)                return "FORCE-TEST (pipeline verifier)";
            if (useVerticalVelocityForPitch)   return "hybrid (state baseline + vertical velocity)";
            return "state-only";
        }
    }

    /// <summary>
    /// Editor-tool read-out. Returns the captured model offset (Euler) plus
    /// the current smoothed pitch/bank and last observed targets.
    /// </summary>
    public string DescribeForValidation()
    {
        Vector3 e = ModelOffset.eulerAngles;
        return $"ModelOffset(euler) = ({e.x:F2}, {e.y:F2}, {e.z:F2})  " +
               $"smoothedPitch = {smoothedPitch:F2}°  smoothedBank = {smoothedBank:F2}°  " +
               $"targetPitch = {LastTargetPitch:F2}°  vy = {LastVerticalVelocity:F2} m/s  " +
               $"lastNonZeroStatePitchState = {LastNonZeroStatePitchState}";
    }
}
