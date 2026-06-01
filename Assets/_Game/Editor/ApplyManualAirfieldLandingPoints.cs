using UnityEditor;
using UnityEngine;

/// <summary>
/// Commits the AirfieldPrefab landing touchdown point to the EXACT world
/// position the user captured by manually placing a StrikeJetPrefab at the
/// correct touchdown spot in the test scene (with the AirfieldPrefab at
/// world (1, ~0, 1)). World position was converted to AirfieldPrefab-local
/// by subtracting the airfield's world position; that local value is
/// hard-coded below.
///
/// WHICH TRANSFORM IS TOUCHDOWN?
///   <see cref="Airfield.landingStartA"/> per the script's own comment:
///   "Point on the runway where descent begins. Aircraft transitions to
///    FinalLanding when it arrives here AT ALTITUDE and begins descending
///    toward LandingEnd_A."
///   The user-captured Y is 0.6 (same as the parked-jet height = ground
///   level), so when the jet arrives at <c>LandingStart_A</c> it is
///   already touching the runway. The "descent" phase has effectively
///   zero altitude drop — the jet appears at the touchdown spot already
///   on the ground.
///   <see cref="Airfield.landingEndA"/> is documented as "the landing
///   roll ends, aircraft is on the ground here" — that's the END of the
///   rollout, not first ground contact. We DO NOT change it; the jet
///   keeps rolling south from LandingStart_A toward LandingEnd_A then
///   off-ramps at LandingExit_A as before.
///
/// THE BUG (before this tool runs):
///   <c>LandingStart_A</c> sat at (-1.5, 0, +12) — close to the right
///   spot in Z but with Y=0 (the jet teleported to the ground there) and
///   the AirUnitController FinalLanding state then descended *south* down
///   the runway, touching the ground for real near <c>LandingEnd_A</c> at
///   Z = -10 — right next to the small south-side building. Visually the
///   jet "landed" inside / near the building.
///
/// THE FIX:
///   Set <c>LandingStart_A</c> and <c>LandingStart_B</c> to the user's
///   captured local coordinate (-0.7399742, 0.6, 11.4898415) with Y
///   rotation = 177.607071° (the jet's heading at touchdown — pointed
///   roughly south, ready to roll south down the runway). The user's Y
///   value makes the jet arrive on the ground here instead of at altitude,
///   so the visible "land" event reads at this point.
///   Lane B is reserved (Airfield only uses Lane A for landing in v1) but
///   we set Lane B's LandingStart to the same value for symmetry — if v2
///   ever activates Lane B, it'll land at the right spot too.
///
/// What is NOT touched:
///   • LandingApproachPoint, LandingEnd_A/B, LandingExit_A/B (existing
///     positions kept — they govern post-touchdown rollout + off-ramp +
///     taxi-back-to-slot, which the user reports working correctly).
///   • Slot_0..5, Taxi_0..5, lane corridor mids.
///   • RunwayQueuePoint_A/B, TakeoffStart_A/B, TakeoffEnd_A/B — takeoff
///     points UNCHANGED per the user request.
///   • Airfield.cs, AirUnitController.cs, production / combat / queue /
///     batch / orphan-watchdog logic — all unchanged.
///
/// Re-runnable: idempotent.
///
/// Menu: Tools → RTS → Buildings → Apply Manual Airfield Landing Points
/// </summary>
public static class ApplyManualAirfieldLandingPoints
{
    private const string PrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";

    /// <summary>
    /// EXACT user-captured local touchdown position (world - airfield world).
    /// Public so the layout-source-of-truth files
    /// (NormalizeAirfieldScale, UseNewAirfieldVisual) reuse the same value.
    /// </summary>
    public static readonly Vector3 TouchdownLocal = new Vector3(-0.739974201f, 0.6f, 11.4898415f);

    /// <summary>
    /// Touchdown heading (degrees, Y axis). Jet faces ~south to roll
    /// south down the runway toward LandingEnd / LandingExit.
    /// </summary>
    public const float LandingHeadingY = 177.607071f;

    [MenuItem("Tools/RTS/Buildings/Apply Manual Airfield Landing Points")]
    public static void Apply()
    {
        // REDIRECT: this older tool used to write to LandingStart_A/B, which
        // turned out to be wrong — per AirUnitController the actual touchdown
        // is LandingEnd. We now forward to the corrected tool so re-running
        // this menu produces the same correct result.
        Debug.LogWarning("[ApplyManualLanding] This menu has been corrected — touchdown is LandingEnd " +
                         "(not LandingStart). Forwarding to Apply Manual Airfield Landing Touchdown.");
        ApplyManualAirfieldLandingTouchdown.Apply();
    }

}
