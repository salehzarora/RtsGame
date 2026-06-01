using UnityEditor;
using UnityEngine;

/// <summary>
/// Read-only tool that prints the CURRENT local-space positions and
/// rotations of every Airfield aircraft slot, taxi point, and runway /
/// landing marker. Lets you nail the parking pads visually in the Scene
/// view (drag Slot_0..5 onto the yellow pad textures of Visual_NewAirfield,
/// re-rotate if needed, save the prefab) and then capture the final numbers
/// for persisting back into <see cref="NormalizeAirfieldScale.Layout"/> /
/// <see cref="UseNewAirfieldVisual.Layout"/>.
///
/// The output is intentionally formatted as a paste-able C# array literal
/// so you can hand it back to the dev (or paste it into Layout[] yourself)
/// and the next Normalize run will lock in the manually-tuned positions.
///
/// Menu: Tools → RTS → Buildings → Report Airfield Slot Positions
/// </summary>
public static class ReportAirfieldSlotPositions
{
    private const string PrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";

    private static readonly string[] RunwayMarkerNames =
    {
        "RunwayQueuePoint_A", "TakeoffStart_A", "TakeoffEnd_A",
        "RunwayQueuePoint_B", "TakeoffStart_B", "TakeoffEnd_B",
        "TaxiPoint_A_Mid",    "TaxiPoint_B_Mid",
        "LaneA_GoAround",     "LaneB_Link",
        "LandingApproachPoint",
        "LandingStart_A", "LandingEnd_A", "LandingExit_A",
        "LandingStart_B", "LandingEnd_B", "LandingExit_B",
    };

    [MenuItem("Tools/RTS/Buildings/Report Airfield Slot Positions")]
    public static void Report()
    {
        Debug.Log("[ReportAirfieldSlots] ─── Current AirfieldPrefab transforms ───");

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
        {
            Debug.LogError($"[ReportAirfieldSlots] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[ReportAirfieldSlots] ✗ LoadPrefabContents returned null.");
            return;
        }

        try
        {
            // Per-slot summary first (the thing you care about most).
            Debug.Log("[ReportAirfieldSlots] ── Slots / Taxi ──");
            for (int i = 0; i < 6; i++)
            {
                Transform slot = root.transform.Find($"Slot_{i}");
                Transform taxi = root.transform.Find($"Taxi_{i}");
                Debug.Log($"[AirfieldSlots] Slot {i}: " +
                          $"pos = {F(slot, true)}, rotY = {RotY(slot)}° | " +
                          $"Taxi {i}: pos = {F(taxi, true)}, rotY = {RotY(taxi)}°.");
            }

            // Runway / lane / landing.
            Debug.Log("[ReportAirfieldSlots] ── Runway / lane / landing ──");
            for (int i = 0; i < RunwayMarkerNames.Length; i++)
            {
                Transform t = root.transform.Find(RunwayMarkerNames[i]);
                Debug.Log($"[AirfieldSlots] {RunwayMarkerNames[i]}: pos = {F(t, true)}.");
            }

            // Paste-able C# array literal so you can give us the captured
            // positions back and we lock them in by overwriting Layout[]
            // verbatim. Easy diff in code review.
            Debug.Log("[ReportAirfieldSlots] ── Paste-able Layout[] snapshot ──");
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("private static readonly SlotLayout[] Layout =");
            sb.AppendLine("{");
            for (int i = 0; i < 6; i++)
            {
                Transform slot = root.transform.Find($"Slot_{i}");
                Transform taxi = root.transform.Find($"Taxi_{i}");
                Vector3 sp = slot != null ? slot.localPosition : Vector3.zero;
                Vector3 tp = taxi != null ? taxi.localPosition : Vector3.zero;
                float roty = slot != null ? slot.localEulerAngles.y : 0f;
                sb.AppendLine($"    new SlotLayout {{ Index = {i}, " +
                              $"Slot = new Vector3({sp.x:F2}f, {sp.y:F2}f, {sp.z:F2}f), " +
                              $"Taxi = new Vector3({tp.x:F2}f, {tp.y:F2}f, {tp.z:F2}f), " +
                              $"RotationY = {roty:F0}f }},");
            }
            sb.Append("};");
            Debug.Log(sb.ToString());

            Debug.Log("[ReportAirfieldSlots] ─── End of report ───");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static string F(Transform t, bool local)
    {
        if (t == null) return "null";
        Vector3 v = local ? t.localPosition : t.position;
        return $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
    }

    private static string RotY(Transform t)
    {
        if (t == null) return "null";
        return $"{t.localEulerAngles.y:F0}";
    }
}
