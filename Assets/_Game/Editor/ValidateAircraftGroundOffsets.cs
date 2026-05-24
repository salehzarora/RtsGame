using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Read-only sanity check for the aircraft / airfield surface alignment:
///
///   • slot.y matches AirUnitController.groundHeightOffset
///     (so a parked jet doesn't snap into the air the moment it starts taxiing)
///   • taxi point Y matches groundHeightOffset
///   • takeoff start / end Y matches groundHeightOffset
///   • Lane A queue + go-around aren't inside the Hangar/Tower XZ footprint
///     (the most common clipping the user reported)
///   • Lane A vs Lane B X separation is at least 3 units
///   • parking slots aren't inside the Hangar XZ footprint
///
/// Nothing is modified — fix issues with
/// Tools → RTS → Air System → Repair Airfield Layout.
///
/// Menu: Tools → RTS → Air System → Validate Aircraft Ground Offsets
/// </summary>
public static class ValidateAircraftGroundOffsets
{
    private const float HeightTolerance  = 0.05f; // allow tiny float drift
    private const float MinLaneSpacing   = 3f;    // X distance between Lane A and Lane B starts
    private const float BuildingPadding  = 0.5f;  // extra XZ margin around buildings

    [MenuItem("Tools/RTS/Air System/Validate Aircraft Ground Offsets")]
    public static void Validate()
    {
        Debug.Log("[ValidateAircraftGroundOffsets] ── Scanning ──");

        int ok = 0;
        int bad = 0;

        // Pick a reference groundHeightOffset by sampling the Strike Jet
        // prefab if one exists; otherwise fall back to the spec default.
        float refOffset = ResolveReferenceGroundOffset();
        Debug.Log($"[ValidateAircraftGroundOffsets]   Reference aircraft ground offset: {refOffset:F2}");

        // --- Prefab assets --------------------------------------------- //
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
        foreach (string guid in prefabGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) continue;

            Airfield af = root.GetComponent<Airfield>();
            if (af == null) continue;

            if (Report(af, path, refOffset)) ok++; else bad++;
        }

        // --- Scene instances ------------------------------------------- //
        Airfield[] scene = Object.FindObjectsByType<Airfield>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (Airfield af in scene)
        {
            if (af == null) continue;
            string label = $"[scene] {af.gameObject.name}";
            if (Report(af, label, refOffset)) ok++; else bad++;
        }

        Debug.Log($"[ValidateAircraftGroundOffsets] ✓ Done. Healthy Airfields: {ok}. Problems: {bad}.");
    }

    // ------------------------------------------------------------------ //

    /// <summary>Reads the AirUnitController.groundHeightOffset from the Strike Jet prefab.</summary>
    private static float ResolveReferenceGroundOffset()
    {
        string[] guids = AssetDatabase.FindAssets("StrikeJetPrefab t:Prefab");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith("/StrikeJetPrefab.prefab")) continue;
            GameObject jet = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (jet == null) continue;
            AirUnitController auc = jet.GetComponent<AirUnitController>();
            if (auc != null) return auc.groundHeightOffset;
        }
        return 0.55f; // spec fallback
    }

    /// <summary>Returns true if every check on <paramref name="af"/> passes.</summary>
    private static bool Report(Airfield af, string label, float refOffset)
    {
        int problems = 0;

        // ── 1. Surface Y matches aircraft ground offset ──────────────── //
        problems += CheckHeightArray(label, "slots",      af.slots,      refOffset);
        problems += CheckHeightArray(label, "taxiPoints", af.taxiPoints, refOffset);
        problems += CheckHeightArray(label, "laneATaxiPoints", af.laneATaxiPoints, refOffset);
        problems += CheckHeightArray(label, "laneBTaxiPoints", af.laneBTaxiPoints, refOffset);

        problems += CheckHeight(label, "runwayQueuePointA",    af.runwayQueuePointA,    refOffset);
        problems += CheckHeight(label, "runwayQueuePointB",    af.runwayQueuePointB,    refOffset);
        problems += CheckHeight(label, "takeoffStartA",        af.takeoffStartA,        refOffset);
        problems += CheckHeight(label, "takeoffStartB",        af.takeoffStartB,        refOffset);
        problems += CheckHeight(label, "takeoffEndA",          af.takeoffEndA,          refOffset);
        problems += CheckHeight(label, "takeoffEndB",          af.takeoffEndB,          refOffset);
        problems += CheckHeight(label, "landingApproachPoint", af.landingApproachPoint, refOffset);

        // ── 2. Lane spacing — wings must not clip on simultaneous takeoff //
        if (af.takeoffStartA != null && af.takeoffStartB != null)
        {
            // Use local positions so the check works on prefab assets too.
            float laneSpacing = Mathf.Abs(af.takeoffStartA.localPosition.x - af.takeoffStartB.localPosition.x);
            if (laneSpacing < MinLaneSpacing)
            {
                Warn(label, $"Lane spacing is only {laneSpacing:F1}u (expected ≥ {MinLaneSpacing}u). " +
                            "Jet wings may visually overlap.");
                problems++;
            }
        }

        // ── 3. Building clipping — collect Hangar/Tower XZ footprints ─ //
        List<BoxXZ> obstacles = CollectBuildingFootprints(af);

        problems += CheckPointAgainstBuildings(label, "runwayQueuePointA",    af.runwayQueuePointA,    obstacles);
        problems += CheckPointAgainstBuildings(label, "runwayQueuePointB",    af.runwayQueuePointB,    obstacles);
        problems += CheckPointAgainstBuildings(label, "takeoffStartA",        af.takeoffStartA,        obstacles);
        problems += CheckPointAgainstBuildings(label, "takeoffStartB",        af.takeoffStartB,        obstacles);
        problems += CheckPointArrayAgainstBuildings(label, "laneATaxiPoints", af.laneATaxiPoints,      obstacles);
        problems += CheckPointArrayAgainstBuildings(label, "laneBTaxiPoints", af.laneBTaxiPoints,      obstacles);
        problems += CheckPointArrayAgainstBuildings(label, "slots",           af.slots,                obstacles);

        if (problems == 0)
        {
            Debug.Log($"[ValidateAircraftGroundOffsets]   ✓ {label}: ground offsets OK, no building clipping.");
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ //
    // Height checks — every taxi/runway marker should sit at refOffset
    // ------------------------------------------------------------------ //

    private static int CheckHeight(string label, string fieldName, Transform t, float refOffset)
    {
        if (t == null) return 0; // missing-marker case is handled by ValidateTakeoffQueue
        if (Mathf.Abs(t.localPosition.y - refOffset) > HeightTolerance)
        {
            Warn(label, $"{fieldName}.localPosition.y = {t.localPosition.y:F2} " +
                        $"(expected ≈ {refOffset:F2}). Aircraft would snap-jump on transition.");
            return 1;
        }
        return 0;
    }

    private static int CheckHeightArray(string label, string fieldName, Transform[] arr, float refOffset)
    {
        if (arr == null) return 0;
        int problems = 0;
        for (int i = 0; i < arr.Length; i++)
            problems += CheckHeight(label, $"{fieldName}[{i}]", arr[i], refOffset);
        return problems;
    }

    // ------------------------------------------------------------------ //
    // Building-clipping checks — points should not sit inside the Hangar/Tower
    // ------------------------------------------------------------------ //

    private struct BoxXZ
    {
        public string Name;
        public Vector3 LocalCentre; // local to Airfield root
        public float HalfX, HalfZ;
    }

    private static List<BoxXZ> CollectBuildingFootprints(Airfield af)
    {
        List<BoxXZ> result = new List<BoxXZ>();
        if (af == null) return result;

        // Walk direct children looking for the named visual primitives.
        foreach (Transform child in af.transform)
        {
            if (child == null) continue;
            string n = child.name;
            if (n != "Hangar" && n != "Tower") continue;

            // Local-space AABB on XZ. The cube primitive's mesh is 1u; the
            // physical XZ extent is scaled by its localScale.
            Vector3 localPos = child.localPosition;
            Vector3 localScl = child.localScale;
            result.Add(new BoxXZ
            {
                Name = n,
                LocalCentre = localPos,
                HalfX = Mathf.Abs(localScl.x) * 0.5f + BuildingPadding,
                HalfZ = Mathf.Abs(localScl.z) * 0.5f + BuildingPadding,
            });
        }
        return result;
    }

    private static int CheckPointAgainstBuildings(string label, string fieldName,
                                                  Transform t, List<BoxXZ> obstacles)
    {
        if (t == null) return 0;
        Vector3 p = t.localPosition;
        foreach (BoxXZ box in obstacles)
        {
            float dx = Mathf.Abs(p.x - box.LocalCentre.x);
            float dz = Mathf.Abs(p.z - box.LocalCentre.z);
            if (dx <= box.HalfX && dz <= box.HalfZ)
            {
                Warn(label, $"{fieldName} sits inside the '{box.Name}' XZ footprint " +
                            $"(point ({p.x:F1},{p.z:F1}) inside box centred at " +
                            $"({box.LocalCentre.x:F1},{box.LocalCentre.z:F1})). " +
                            "Aircraft will clip the building.");
                return 1;
            }
        }
        return 0;
    }

    private static int CheckPointArrayAgainstBuildings(string label, string fieldName,
                                                       Transform[] arr, List<BoxXZ> obstacles)
    {
        if (arr == null) return 0;
        int problems = 0;
        for (int i = 0; i < arr.Length; i++)
            problems += CheckPointAgainstBuildings(label, $"{fieldName}[{i}]", arr[i], obstacles);
        return problems;
    }

    // ------------------------------------------------------------------ //

    private static void Warn(string label, string msg)
    {
        Debug.LogWarning($"[ValidateAircraftGroundOffsets] ⚠ {label}: {msg} " +
                         "Run Tools → RTS → Air System → Repair Airfield Layout.");
    }
}
