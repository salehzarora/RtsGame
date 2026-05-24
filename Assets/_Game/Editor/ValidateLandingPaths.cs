using UnityEditor;
using UnityEngine;

/// <summary>
/// Read-only sanity check for the runway landing markers introduced by the
/// runway-landing-queue cleanup:
///
///   • Lane A: LandingApproachPoint, LandingStart_A, LandingEnd_A, LandingExit_A
///     (all required for the v1 landing path)
///   • Lane B: LandingStart_B / End_B / Exit_B (optional — only warned about
///     if some-but-not-all are present, since Lane B is reserved)
///   • LandingEnd_A must be DIFFERENT from TakeoffStart_A (they're at opposite
///     ends of the runway in the new layout — if they line up the runway has
///     collapsed in the prefab).
///   • LandingExit_A should not sit inside the Hangar or Tower XZ footprint.
///
/// Nothing is modified — fix issues with
/// Tools → RTS → Air System → Repair Airfield Layout.
///
/// Menu: Tools → RTS → Air System → Validate Landing Paths
/// </summary>
public static class ValidateLandingPaths
{
    [MenuItem("Tools/RTS/Air System/Validate Landing Paths")]
    public static void Validate()
    {
        Debug.Log("[ValidateLandingPaths] ── Scanning ──");

        int ok  = 0;
        int bad = 0;

        // --- Prefab assets --------------------------------------------- //
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
        foreach (string guid in prefabGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) continue;
            Airfield af = root.GetComponent<Airfield>();
            if (af == null) continue;
            if (Report(af, path)) ok++; else bad++;
        }

        // --- Scene instances ------------------------------------------- //
        Airfield[] scene = Object.FindObjectsByType<Airfield>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (Airfield af in scene)
        {
            if (af == null) continue;
            if (Report(af, $"[scene] {af.gameObject.name}")) ok++; else bad++;
        }

        Debug.Log($"[ValidateLandingPaths] ✓ Done. Healthy: {ok}. Problems: {bad}.");
    }

    private static bool Report(Airfield af, string label)
    {
        int problems = 0;

        // Lane A — required for v1 landing flow.
        if (af.landingApproachPoint == null) { Warn(label, "landingApproachPoint is null."); problems++; }
        if (af.landingStartA        == null) { Warn(label, "landingStartA is null.");        problems++; }
        if (af.landingEndA          == null) { Warn(label, "landingEndA is null.");          problems++; }
        if (af.landingExitA         == null) { Warn(label, "landingExitA is null.");         problems++; }

        // Lane B — optional, but if some present, all should be.
        int laneBCount =
            (af.landingStartB != null ? 1 : 0) +
            (af.landingEndB   != null ? 1 : 0) +
            (af.landingExitB  != null ? 1 : 0);
        if (laneBCount != 0 && laneBCount != 3)
        {
            Warn(label, "Lane B landing markers are partial (need all of " +
                        "LandingStart_B / LandingEnd_B / LandingExit_B or none).");
            problems++;
        }

        // Sanity: LandingStart_A and LandingEnd_A should be at different Z
        // positions — they're opposite ends of the runway.
        if (af.landingStartA != null && af.landingEndA != null)
        {
            float zGap = Mathf.Abs(af.landingStartA.localPosition.z -
                                   af.landingEndA.localPosition.z);
            if (zGap < 5f)
            {
                Warn(label, $"Lane A landing path is only {zGap:F1}u long " +
                            "(start and end too close — aircraft can't descend cleanly).");
                problems++;
            }
        }

        // Sanity: LandingEnd_A should NOT coincide with TakeoffStart_A.
        // (In the v1 layout they're at opposite Z ends — collapsing them
        // means landings and takeoffs would conflict spatially as well as
        // temporally.)
        if (af.landingEndA != null && af.takeoffStartA != null)
        {
            float zGap = Mathf.Abs(af.landingEndA.localPosition.z -
                                   af.takeoffStartA.localPosition.z);
            if (zGap < 0.5f)
            {
                Warn(label, "landingEndA and takeoffStartA are at the same Z. " +
                            "Aircraft will queue for takeoff on top of the landing rollout.");
                problems++;
            }
        }

        // Sanity: LandingExit_A should not sit inside the Hangar/Tower box.
        problems += CheckExitClearOfBuildings(af, label, af.landingExitA, "landingExitA");

        if (problems == 0)
        {
            Debug.Log($"[ValidateLandingPaths]   ✓ {label}: landing paths OK.");
            return true;
        }
        return false;
    }

    private static int CheckExitClearOfBuildings(Airfield af, string label, Transform exit, string field)
    {
        if (exit == null) return 0;
        Vector3 p = exit.localPosition;

        foreach (Transform child in af.transform)
        {
            if (child == null) continue;
            string n = child.name;
            if (n != "Hangar" && n != "Tower") continue;

            Vector3 c = child.localPosition;
            float halfX = Mathf.Abs(child.localScale.x) * 0.5f + 0.5f;
            float halfZ = Mathf.Abs(child.localScale.z) * 0.5f + 0.5f;

            if (Mathf.Abs(p.x - c.x) <= halfX && Mathf.Abs(p.z - c.z) <= halfZ)
            {
                Warn(label, $"{field} sits inside the '{n}' XZ footprint — " +
                            "taxi-back will clip the building.");
                return 1;
            }
        }
        return 0;
    }

    private static void Warn(string label, string msg)
    {
        Debug.LogWarning($"[ValidateLandingPaths] ⚠ {label}: {msg} " +
                         "Run Tools → RTS → Air System → Repair Airfield Layout.");
    }
}
