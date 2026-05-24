using UnityEditor;
using UnityEngine;

/// <summary>
/// Read-only sanity check for the airfield takeoff-queue plumbing. Verifies
/// that every Airfield (prefab asset or scene instance) has all the marker
/// Transforms the takeoff queue requires:
///
///   • slots[0..5]              (parking pads — same check as the slot validator)
///   • taxiPoints[0..5]         (first waypoint per slot)
///   • runwayQueuePointA / B    (hold-short positions on each lane)
///   • takeoffStartA   / B      (start of the takeoff roll on each lane)
///   • takeoffEndA     / B      (end of the runway — Lane release point)
///   • landingApproachPoint     (reserved for future landing logic)
///   • maxConcurrentTakeoffs == 2 (the spec calls for exactly two lanes)
///
/// Nothing is modified — fix issues with
/// Tools → RTS → Air System → Repair Airfield Layout.
///
/// Menu: Tools → RTS → Air System → Validate Takeoff Queue
/// </summary>
public static class ValidateTakeoffQueue
{
    [MenuItem("Tools/RTS/Air System/Validate Takeoff Queue")]
    public static void Validate()
    {
        Debug.Log("[ValidateTakeoffQueue] ── Scanning ──");

        int ok = 0;
        int bad = 0;

        // --- Prefab assets ----------------------------------------------- //
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

        // --- Scene instances --------------------------------------------- //
        Airfield[] scene = Object.FindObjectsByType<Airfield>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (Airfield af in scene)
        {
            if (af == null) continue;
            string label = $"[scene] {af.gameObject.name}";
            if (Report(af, label)) ok++; else bad++;
        }

        Debug.Log($"[ValidateTakeoffQueue] ✓ Done. Healthy Airfields: {ok}. Problems: {bad}.");
    }

    /// <summary>Returns true when every required marker on <paramref name="af"/> is non-null.</summary>
    private static bool Report(Airfield af, string label)
    {
        int problems = 0;

        // Slots
        if (af.slots == null || af.slots.Length != Airfield.MaxSlots)
        {
            Warn(label, $"slots[] has {(af.slots?.Length ?? 0)} entries (expected {Airfield.MaxSlots}).");
            problems++;
        }
        else
        {
            int missing = 0;
            for (int i = 0; i < af.slots.Length; i++)
                if (af.slots[i] == null) missing++;
            if (missing > 0)
            {
                Warn(label, $"{missing}/{Airfield.MaxSlots} slot Transforms are unassigned.");
                problems++;
            }
        }

        // Taxi points
        if (af.taxiPoints == null || af.taxiPoints.Length != Airfield.MaxSlots)
        {
            Warn(label, $"taxiPoints[] has {(af.taxiPoints?.Length ?? 0)} entries (expected {Airfield.MaxSlots}).");
            problems++;
        }
        else
        {
            int missing = 0;
            for (int i = 0; i < af.taxiPoints.Length; i++)
                if (af.taxiPoints[i] == null) missing++;
            if (missing > 0)
            {
                Warn(label, $"{missing}/{Airfield.MaxSlots} taxiPoint Transforms are unassigned.");
                problems++;
            }
        }

        // Lane markers
        if (af.runwayQueuePointA == null) { Warn(label, "runwayQueuePointA is null."); problems++; }
        if (af.runwayQueuePointB == null) { Warn(label, "runwayQueuePointB is null."); problems++; }
        if (af.takeoffStartA     == null) { Warn(label, "takeoffStartA is null.");     problems++; }
        if (af.takeoffStartB     == null) { Warn(label, "takeoffStartB is null.");     problems++; }
        if (af.takeoffEndA       == null) { Warn(label, "takeoffEndA is null.");       problems++; }
        if (af.takeoffEndB       == null) { Warn(label, "takeoffEndB is null.");       problems++; }
        if (af.landingApproachPoint == null) { Warn(label, "landingApproachPoint is null."); problems++; }

        // Lane corridor arrays — at least one waypoint per lane, no null
        // entries inside the array.
        problems += CheckLaneCorridor(label, "laneATaxiPoints", af.laneATaxiPoints);
        problems += CheckLaneCorridor(label, "laneBTaxiPoints", af.laneBTaxiPoints);

        // Lane separation sanity check — Lane A and Lane B takeoff starts
        // must be far enough apart that jet wings (~2u wide) don't clip.
        if (af.takeoffStartA != null && af.takeoffStartB != null)
        {
            float laneSpacing = Mathf.Abs(af.takeoffStartA.position.x - af.takeoffStartB.position.x);
            if (laneSpacing < 3f)
            {
                Warn(label, $"Lane spacing is only {laneSpacing:F1}u (expected ≥ 3u). " +
                            "Jet wings may visually overlap during simultaneous takeoff.");
                problems++;
            }
        }

        // Queue sizing
        if (af.maxConcurrentTakeoffs != 2)
        {
            Warn(label, $"maxConcurrentTakeoffs = {af.maxConcurrentTakeoffs} (spec calls for exactly 2).");
            problems++;
        }
        if (af.takeoffSpacingSeconds < 0f)
        {
            Warn(label, $"takeoffSpacingSeconds = {af.takeoffSpacingSeconds} (must be ≥ 0).");
            problems++;
        }

        if (problems == 0)
        {
            Debug.Log($"[ValidateTakeoffQueue]   ✓ {label}: layout OK.");
            return true;
        }
        return false;
    }

    private static int CheckLaneCorridor(string label, string fieldName, Transform[] corridor)
    {
        if (corridor == null || corridor.Length == 0)
        {
            Warn(label, $"{fieldName} is empty (expected ≥ 1 waypoint).");
            return 1;
        }

        int missing = 0;
        for (int i = 0; i < corridor.Length; i++)
            if (corridor[i] == null) missing++;

        if (missing > 0)
        {
            Warn(label, $"{fieldName} has {missing}/{corridor.Length} null waypoint(s).");
            return 1;
        }
        return 0;
    }

    private static void Warn(string label, string msg)
    {
        Debug.LogWarning($"[ValidateTakeoffQueue] ⚠ {label}: {msg} " +
                         "Run Tools → RTS → Air System → Repair Airfield Layout.");
    }
}
