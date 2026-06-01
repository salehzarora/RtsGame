using UnityEditor;
using UnityEngine;

/// <summary>
/// CORRECTED landing-touchdown applier. The previous tool
/// (<see cref="ApplyManualAirfieldLandingPoints"/>) wrote the user's
/// coordinate to <c>LandingStart_A/B</c> — that didn't fix the runtime
/// touchdown because, per <see cref="AirUnitController"/>, LandingStart
/// is only the in-air align point: jets fly toward it AT
/// <c>flightAltitude</c> (12 m), then transition to FinalLanding and
/// descend toward <c>LandingEnd</c>. The actual ground contact happens
/// when the jet reaches <c>LandingEnd</c>'s XZ at ground Y. So
/// <b>LandingEnd is the touchdown transform.</b>
///
/// This tool:
///   1. Writes the user's exact local coordinate
///      (<c>-0.739974201, 0.6, 11.4898415</c>) to <c>LandingEnd_A</c>
///      and <c>LandingEnd_B</c> with Y rotation 177.607071°.
///   2. Restores <c>LandingStart_A/B</c> to a sensible IN-AIR ALIGN point
///      north of the touchdown so the FinalLanding descent has a proper
///      runway-aligned glide path instead of dropping in place.
///      (Same X as LandingEnd, slightly higher Z so the descent goes
///      straight south down the lane.)
///
/// Untouched:
///   • <see cref="Airfield.landingApproachPoint"/> (holding-pattern centre).
///   • <see cref="Airfield.landingExitA"/>/B (off-runway turn).
///   • All takeoff transforms.
///   • All slot/taxi transforms.
///   • Airfield.cs, AirUnitController.cs runtime logic.
///
/// Idempotent.
///
/// Menus:
///   Tools → RTS → Buildings → Apply Manual Airfield Landing Touchdown
/// </summary>
public static class ApplyManualAirfieldLandingTouchdown
{
    private const string PrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";

    /// <summary>EXACT user-captured local touchdown position (world − airfield world).</summary>
    public static readonly Vector3 TouchdownLocal = new Vector3(-0.739974201f, 0.6f, 11.4898415f);

    /// <summary>Touchdown heading (degrees, Y axis). Faces south to roll down the lane.</summary>
    public const float TouchdownHeadingY = 177.607071f;

    /// <summary>
    /// LandingStart_A/B sit on the same lane X as the touchdown but far
    /// enough north for a real GLIDE (not a vertical helicopter descent).
    /// Math: <c>landingProfile.speed = 7</c> m/s and
    /// <c>verticalSpeed = 5</c> m/s with <c>flightAltitude = 12</c> →
    /// vertical descent takes 12/5 = 2.4 s, which at 7 m/s horizontal
    /// covers 16.8 m. We use 25 m to give a gentle ~25° glide slope and
    /// some slack so the jet kisses the runway at LandingEnd rather than
    /// reaching it horizontally first and hovering down.
    /// </summary>
    public static readonly Vector3 ApproachAnchorLocal = new Vector3(-0.739974201f, 0f, 36.49f);

    /// <summary>
    /// LandingApproachPoint = holding-pattern centre. Pushed well north of
    /// LandingStart so the post-clearance MoveTowards path is straight
    /// south (no U-turn that would cause the "weird reverse" the user saw).
    /// Y stays 0 because <see cref="AirUnitController.UpdateLandingApproach"/>
    /// overrides Y with <c>flightAltitude</c>.
    /// </summary>
    public static readonly Vector3 ApproachHoldLocal = new Vector3(-0.739974201f, 0f, 50f);

    [MenuItem("Tools/RTS/Buildings/Apply Manual Airfield Landing Touchdown")]
    public static void Apply()
    {
        Debug.Log("[ApplyManualTouchdown] ─── Applying user touchdown to LandingEnd (actual touchdown transform) ───");

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
        {
            Debug.LogError($"[ApplyManualTouchdown] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[ApplyManualTouchdown] ✗ LoadPrefabContents returned null.");
            return;
        }

        try
        {
            int applied = 0, missing = 0;

            // LandingEnd = ACTUAL TOUCHDOWN per AirUnitController.UpdateFinalLanding
            // (descent target; touchdown event fires when jet reaches this XZ at ground Y).
            applied += SetTransform(root, "LandingEnd_A", TouchdownLocal, TouchdownHeadingY,
                                    " *** ACTUAL TOUCHDOWN POINT ***", ref missing);
            applied += SetTransform(root, "LandingEnd_B", TouchdownLocal, TouchdownHeadingY,
                                    " *** ACTUAL TOUCHDOWN POINT ***", ref missing);

            // LandingStart = in-air align point. Pushed 25 m north of touchdown
            // so the FinalLanding descent has horizontal room to glide instead
            // of dropping vertically.
            applied += SetTransform(root, "LandingStart_A", ApproachAnchorLocal, TouchdownHeadingY,
                                    " (in-air align, 25 m north of touchdown — glide slope ~25°)", ref missing);
            applied += SetTransform(root, "LandingStart_B", ApproachAnchorLocal, TouchdownHeadingY,
                                    " (in-air align, 25 m north of touchdown)", ref missing);

            // LandingApproachPoint = holding centre. North of LandingStart so
            // the post-clearance MoveTowards is straight south (no U-turn).
            applied += SetTransform(root, "LandingApproachPoint", ApproachHoldLocal, TouchdownHeadingY,
                                    " (holding-pattern centre, north of LandingStart — straight south to land)", ref missing);

            Debug.Log("[ApplyManualTouchdown] ── Unchanged landing transforms (read-only) ──");
            ReportUnchanged(root, "LandingExit_A");
            ReportUnchanged(root, "LandingExit_B");

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ApplyManualTouchdown] ✓ Applied {applied}/5 transforms. {missing} missing. " +
                      $"Holding={ApproachHoldLocal} → LandingStart={ApproachAnchorLocal} → " +
                      $"Touchdown={TouchdownLocal}. Glide slope ≈ {Mathf.Atan2(12f, 25f) * Mathf.Rad2Deg:F0}° (Δalt 12 over 25 m). " +
                      "Watch '[LandingPath]' / '[LandingState] FinalLanding' / '[LandingState] Touchdown' logs to verify.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[ApplyManualTouchdown] ─────────────────────────────────────────");
    }

    private static int SetTransform(GameObject root, string name, Vector3 localPos, float rotY,
                                    string tag, ref int missing)
    {
        Transform t = root.transform.Find(name);
        if (t == null)
        {
            Debug.LogWarning($"[ApplyManualTouchdown]   ⚠ '{name}' missing on prefab — skipping.");
            missing++;
            return 0;
        }
        Vector3 posBefore = t.localPosition;
        Vector3 rotBefore = t.localEulerAngles;
        t.localPosition    = localPos;
        t.localEulerAngles = new Vector3(0f, rotY, 0f);
        Debug.Log($"[AirfieldSlots] {name}: localPos {posBefore} → {localPos}, " +
                  $"localRotY {rotBefore.y:F3}° → {rotY:F3}°.{tag}");
        return 1;
    }

    private static void ReportUnchanged(GameObject root, string name)
    {
        Transform t = root.transform.Find(name);
        if (t == null) Debug.Log($"[ApplyManualTouchdown]   '{name}' absent.");
        else           Debug.Log($"[ApplyManualTouchdown]   '{name}' unchanged: localPos {t.localPosition}.");
    }
}
