using UnityEngine;

/// <summary>
/// Movement-tuning bundle for a single flight phase. AirUnitController owns
/// one profile per phase (Taxi, Takeoff, Cruise, AttackRun, Landing, Holding)
/// and each state's Update reads its speed / turn rate / vertical rate from
/// the active profile instead of from scattered top-level fields.
///
/// Inspired by the SAGE engine's LocomotorSet concept: the "brain" (AI state
/// machine) selects a goal, and the active profile determines how the body
/// can physically move toward it. Swapping profiles is how an aircraft feels
/// different during taxi vs. cruise vs. an attack run, without sprinkling
/// magic numbers across half a dozen Update methods.
///
/// Setup:
///   • Edit defaults via the AirUnitController Inspector under "Flight Profiles".
///   • For an attack-run feel, lower <see cref="turnRateDegrees"/> to commit
///     to forward motion (the jet won't snake while releasing missiles).
///   • For a holding pattern, raise <see cref="turnRateDegrees"/> so the
///     tangent-following stays smooth around the circle.
///
/// What it does NOT do:
///   • Apply physics — purely a data bag. Each AirUnitController state reads
///     it and integrates motion itself.
///   • Drive weapon behaviour — that lives on AircraftWeapon.
///   • Override altitude — flight altitude is a separate top-level field
///     because it's a target value, not a rate.
/// </summary>
[System.Serializable]
public class FlightProfile
{
    [Tooltip("Display name — shown when the controller logs a profile switch " +
             "(\"[Aircraft] Using profile: <name>\").")]
    public string name = "Profile";

    [Tooltip("Forward / horizontal movement speed (world units / sec). Meaning " +
             "depends on the owning state — taxi roll, cruise, attack run, " +
             "landing roll, etc.")]
    public float speed = 14f;

    [Tooltip("Maximum yaw rotation rate (degrees / sec). 0 = locked heading " +
             "(used by AttackRunProfile so the jet flies straight through the " +
             "release window). Higher = tighter turns; combined with speed " +
             "this determines the minimum turn radius.")]
    public float turnRateDegrees = 85f;

    [Tooltip("Vertical climb or descent rate (world units / sec). Used by states " +
             "that change altitude (Climbing's climb rate, FinalLanding's descent " +
             "rate). Level-flight states (cruise, attack run, holding) ignore it.")]
    public float verticalSpeed = 0f;

    [Tooltip("Optional: forward acceleration (units / sec²). 0 = instant — the " +
             "state runs at the profile's full speed each frame. Reserved for " +
             "future smoothing; most states currently treat speed as constant.")]
    public float acceleration = 0f;

    [Tooltip("Optional: minimum forward speed (units / sec). 0 = unrestricted. " +
             "Reserved for a future jet/helicopter split where jets stall below " +
             "a floor speed.")]
    public float minSpeed = 0f;

    [Tooltip("XZ distance below which a waypoint counts as 'reached' for states " +
             "that follow waypoint lists (taxi, landing roll). In-air cruise " +
             "states use their own distance gates, not this value.")]
    public float arrivalThreshold = 0.3f;
}
