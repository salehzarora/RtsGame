using UnityEditor;
using UnityEngine;

/// <summary>
/// Commits the AirfieldPrefab pre-takeoff and takeoff-end transforms to the
/// EXACT world positions the user captured by manually placing two
/// StrikeJetPrefabs at the correct queue/start spot in the test scene
/// (with the AirfieldPrefab at world (1, ~0, 1)). World positions were
/// converted to AirfieldPrefab-local by subtracting the airfield's world
/// position; those local values are hard-coded below and applied to all
/// six relevant Transforms. Aircraft logic is not touched — only the
/// waypoint positions used by <c>Airfield.BuildClearance</c>.
///
/// THE BUG (before this tool runs):
///   <c>RunwayQueuePoint_A/B</c> and <c>TakeoffStart_A/B</c> sat at
///   Z ≈ -10 — south end of the runway. On the new Airfield_Unity_Final
///   model that Z range clips a small building / control structure at the
///   south runway threshold. Jets reached the queue point INSIDE that
///   structure before lifting off.
///
/// THE FIX:
///   RunwayQueuePoint and TakeoffStart for each lane are moved to the
///   user-captured pre-takeoff spot just NORTH of the small building,
///   roughly the centre of the runway. TakeoffEnd is moved a little south
///   of its previous value to match the runway's actual painted end on
///   the new model. Rotation Y = 0.0985888913° is applied to all six so
///   the jets' aligned heading matches the painted runway direction.
///
/// Per the user request, RunwayQueuePoint and TakeoffStart share the SAME
/// position per lane — the jet hold-short and roll-start happen in one
/// place. The Airfield script doesn't require them to be distinct; it
/// just adds Queue then Start as separate waypoints in the taxi route.
/// Two consecutive waypoints at the same position is a no-op for the
/// pathing — the jet just transitions states without moving.
///
/// What is NOT touched:
///   • Slot_0..5 (your manually-captured pad positions).
///   • Taxi_0..5 (per-slot south-of-pad exits set by Repair Airfield
///     Taxi Paths — already safely clear of the building).
///   • TaxiPoint_A/B_Mid, LaneA_GoAround, LaneB_Link (lane corridor
///     convergence south of the building).
///   • Landing markers (LandingApproachPoint, LandingStart/End/Exit_A/B).
///   • Airfield.maxConcurrentTakeoffs (stays at 2 — pair-takeoff rule
///     preserved).
///   • Airfield.cs, AirUnitController.cs, BuildingPlacementManager.cs,
///     ConstructionSite.cs.
///
/// Re-runnable: idempotent.
///
/// Menu: Tools → RTS → Buildings → Apply Manual Airfield Takeoff Points
/// </summary>
public static class ApplyManualAirfieldTakeoffPoints
{
    private const string PrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";

    // EXACT user-captured local positions (world − airfield world (1, ~0, 1)).
    // Both lanes' queue and start are at the SAME position per lane per the
    // user request.
    public static readonly Vector3 LaneAQueueAndStart = new Vector3(-1.819133043f, 0.6f, -1.87182045f);
    public static readonly Vector3 LaneBQueueAndStart = new Vector3( 0.68086314f,  0.6f, -1.876122177f);
    public static readonly Vector3 LaneAEnd           = new Vector3(-1.296764731f, 0.6f, 11.1273003f);
    public static readonly Vector3 LaneBEnd           = new Vector3( 0.95323193f,  0.6f, 11.1234283f);

    // All six transforms share the same alignment heading — the runway's
    // painted direction. Y rotation is in degrees.
    public const float RunwayHeadingY = 0.0985888913f;

    [MenuItem("Tools/RTS/Buildings/Apply Manual Airfield Takeoff Points")]
    public static void Apply()
    {
        Debug.Log("[ApplyManualTakeoff] ─── Applying user-captured takeoff/queue waypoints ───");

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
        {
            Debug.LogError($"[ApplyManualTakeoff] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[ApplyManualTakeoff] ✗ LoadPrefabContents returned null.");
            return;
        }

        try
        {
            int applied = 0, missing = 0;
            applied += SetTransform(root, "RunwayQueuePoint_A", LaneAQueueAndStart, RunwayHeadingY, ref missing);
            applied += SetTransform(root, "TakeoffStart_A",     LaneAQueueAndStart, RunwayHeadingY, ref missing);
            applied += SetTransform(root, "RunwayQueuePoint_B", LaneBQueueAndStart, RunwayHeadingY, ref missing);
            applied += SetTransform(root, "TakeoffStart_B",     LaneBQueueAndStart, RunwayHeadingY, ref missing);
            applied += SetTransform(root, "TakeoffEnd_A",       LaneAEnd,           RunwayHeadingY, ref missing);
            applied += SetTransform(root, "TakeoffEnd_B",       LaneBEnd,           RunwayHeadingY, ref missing);

            // Report the Airfield's pair-takeoff rule so the user can confirm
            // it stayed at 2. This is read-only — we don't change it.
            Airfield af = root.GetComponent<Airfield>();
            if (af != null)
            {
                Debug.Log($"[ApplyManualTakeoff]   Airfield.maxConcurrentTakeoffs = {af.maxConcurrentTakeoffs} " +
                          $"(unchanged — pair-takeoff rule preserved).");
                Debug.Log($"[ApplyManualTakeoff]   Airfield.synchronizedGroupTakeoff = {af.synchronizedGroupTakeoff}, " +
                          $"maxAircraftPerLaunchBatch = {af.maxAircraftPerLaunchBatch} (unchanged).");
            }
            else
            {
                Debug.LogWarning("[ApplyManualTakeoff]   ⚠ Airfield script missing on prefab root — production / queue won't work.");
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ApplyManualTakeoff] ✓ Applied {applied}/6 transforms.  {missing} missing. " +
                      "Slot / Taxi / corridor / landing transforms UNCHANGED. " +
                      "Run Tools → RTS → Buildings → Validate Airfield Taxi Paths to verify.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[ApplyManualTakeoff] ─────────────────────────────────────");
    }

    private static int SetTransform(GameObject root, string name, Vector3 localPos, float rotY, ref int missing)
    {
        Transform t = root.transform.Find(name);
        if (t == null)
        {
            Debug.LogWarning($"[ApplyManualTakeoff]   ⚠ '{name}' missing on prefab — skipping.");
            missing++;
            return 0;
        }
        Vector3 posBefore  = t.localPosition;
        Vector3 rotBefore  = t.localEulerAngles;
        t.localPosition    = localPos;
        t.localEulerAngles = new Vector3(0f, rotY, 0f);
        Debug.Log($"[AirfieldSlots] {name}: localPos {posBefore} → {localPos}, " +
                  $"localRotY {rotBefore.y:F4}° → {rotY:F4}°.");
        return 1;
    }
}
