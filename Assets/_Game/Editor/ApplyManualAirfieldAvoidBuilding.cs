using UnityEditor;
using UnityEngine;

/// <summary>
/// Reroutes the takeoff + landing taxi paths so aircraft no longer clip
/// through the airfield's decorative buildings, and cuts the takeoff
/// preparation delay roughly in half. Pure transform / serialized-field
/// edits — no script logic changes.
///
/// WHAT WAS WRONG (before this tool):
///   • Per-slot <c>Taxi_N</c> at Z = -7 (south of central tower).
///   • Lane corridor <c>TaxiPoint_X_Mid</c> / <c>LaneX_GoAround</c> at
///     Z = -8 / -10.5 (further south).
///   • RunwayQueue / TakeoffStart at Z = -1.87 (north of those buildings).
///   • Takeoff path went south to the corridor, then NORTH ~9 m back
///     through the south building to reach the queue. → clip.
///   • LandingExit at Z = -11 (south end). After touchdown at Z = +11.49,
///     the jet had to taxi south the entire length of the runway to reach
///     the exit, crossing the central tower. → clip.
///   • <c>Airfield.takeoffSpacingSeconds = 1</c> and
///     <c>batchWaitTimeout = 5</c> → first pair was 5–6 s from command to
///     roll start.
///
/// WHAT THIS TOOL DOES:
///   1. Moves per-slot <c>Taxi_N</c> to (slot.x, 0, -1) — just south of
///      RunwayQueue but NORTH of the central tower / south building. All
///      slots reach the queue without a southern detour.
///   2. Shortens the lane corridor:
///        TaxiPoint_A_Mid → (-2.5, 0, -1.5)
///        TaxiPoint_B_Mid → ( 2.5, 0, -1.5)
///        LaneA_GoAround  → (-2.5, 0.05, -2.2)
///        LaneB_Link      → ( 2.5, 0.05, -2.2)
///      All four sit just south of the QueuePoints, NORTH of the south
///      building — Lane A jets converge on the west lane, Lane B on the
///      east lane, no jet ever ventures into the building zone.
///   3. Moves LandingExit_A/B to (0, 0, +6) — between the touchdown
///      (Z = +11.49) and the central tower (assumed ~Z = -2..-8). After
///      touchdown, the jet rolls a short 5.5 m south to the exit, then
///      taxis on its own apron to its slot — no central-tower crossing.
///   4. Sets <c>Airfield.takeoffSpacingSeconds</c> = 0.5 (was 1.0) and
///      <c>batchWaitTimeout</c> = 2.5 (was 5). The pair-takeoff rule
///      (<c>maxConcurrentTakeoffs = 2</c>, <c>synchronizedGroupTakeoff =
///      true</c>) is preserved unchanged.
///
/// WHAT IS NOT TOUCHED:
///   • Slot_0..5 (user-captured pad positions).
///   • RunwayQueuePoint_A/B, TakeoffStart_A/B, TakeoffEnd_A/B (user-captured).
///   • LandingApproachPoint, LandingStart_A/B, LandingEnd_A/B (user-captured).
///   • Airfield.cs, AirUnitController.cs.
///   • maxConcurrentTakeoffs, synchronizedGroupTakeoff (pair-takeoff rule).
///   • Visual_NewAirfield, root BoxCollider, root scale.
///
/// Re-runnable: idempotent.
///
/// Menu: Tools → RTS → Buildings → Apply Manual Airfield Avoid Building Path
/// </summary>
public static class ApplyManualAirfieldAvoidBuilding
{
    private const string PrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";

    // ───────────────────────── TIMING DIALS ───────────────────────── //
    /// <summary>Was 1.0 — now 0.5 (50% reduction).</summary>
    public const float NewTakeoffSpacingSeconds = 0.5f;
    /// <summary>Was 5 — now 2.5 (50% reduction). Single-jet commands hit this
    /// timeout when no batch partner exists.</summary>
    public const float NewBatchWaitTimeout      = 2.5f;
    // ──────────────────────────────────────────────────────────────── //

    // Per-slot Taxi target Z (just south of RunwayQueue at -1.87, NORTH of
    // central tower / south building). X stays at each slot's apron X so
    // the jet pulls out straight south along its own apron.
    private const float PerSlotTaxiZ = -1f;
    private const float PerSlotTaxiY = 0f;

    // Lane corridor positions — narrow, just south of the QueuePoints,
    // routes Lane A to the west of centerline and Lane B to the east.
    // LaneX_GoAround / Link MeshRenderers stay disabled (set previously) —
    // only the Transform matters here.
    private struct NamedPoint { public string Name; public Vector3 Pos; }
    private static readonly NamedPoint[] CorridorPoints =
    {
        new NamedPoint { Name = "TaxiPoint_A_Mid", Pos = new Vector3(-2.5f, 0f,    -1.5f) },
        new NamedPoint { Name = "TaxiPoint_B_Mid", Pos = new Vector3( 2.5f, 0f,    -1.5f) },
        new NamedPoint { Name = "LaneA_GoAround",  Pos = new Vector3(-2.5f, 0.05f, -2.2f) },
        new NamedPoint { Name = "LaneB_Link",      Pos = new Vector3( 2.5f, 0.05f, -2.2f) },
        // LandingExit — between touchdown (Z=+11.49) and the central tower
        // (assumed at Z < 0). Jet rolls 5.5 m south after touchdown, then
        // taxis east/west to its own apron.
        new NamedPoint { Name = "LandingExit_A",   Pos = new Vector3( 0f,   0f,     6f)   },
        new NamedPoint { Name = "LandingExit_B",   Pos = new Vector3( 0f,   0f,     6f)   },
    };

    [MenuItem("Tools/RTS/Buildings/Apply Manual Airfield Avoid Building Path")]
    public static void Apply()
    {
        Debug.Log("[ApplyAvoidBuilding] ─── Rerouting taxi paths around decorative buildings ───");

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
        {
            Debug.LogError($"[ApplyAvoidBuilding] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[ApplyAvoidBuilding] ✗ LoadPrefabContents returned null.");
            return;
        }

        try
        {
            int slotsMoved = 0, corridorMoved = 0;

            // 1. Per-slot Taxi → just south of RunwayQueue (NORTH of building).
            for (int i = 0; i < 6; i++)
            {
                Transform slot = root.transform.Find($"Slot_{i}");
                Transform taxi = root.transform.Find($"Taxi_{i}");
                if (slot == null || taxi == null)
                {
                    Debug.LogWarning($"[ApplyAvoidBuilding]   ⚠ Slot_{i} or Taxi_{i} missing — skipping.");
                    continue;
                }
                Vector3 sp = slot.localPosition;
                Vector3 newTaxi = new Vector3(sp.x, PerSlotTaxiY, PerSlotTaxiZ);
                Vector3 before = taxi.localPosition;
                taxi.localPosition = newTaxi;
                slotsMoved++;
                Debug.Log($"[AirfieldTaxi] Taxi_{i}: localPos {before} → {newTaxi} " +
                          $"(on slot's apron X={sp.x:F2}, NORTH of decorative buildings).");
            }

            // 2. Lane corridor + LandingExit.
            for (int i = 0; i < CorridorPoints.Length; i++)
            {
                NamedPoint p = CorridorPoints[i];
                Transform t = root.transform.Find(p.Name);
                if (t == null)
                {
                    Debug.LogWarning($"[ApplyAvoidBuilding]   ⚠ '{p.Name}' missing — skipping.");
                    continue;
                }
                Vector3 before = t.localPosition;
                t.localPosition = p.Pos;
                corridorMoved++;
                Debug.Log($"[AirfieldTaxi] {p.Name}: localPos {before} → {p.Pos}.");
            }

            // 3. Timing dials on the Airfield component.
            Airfield af = root.GetComponent<Airfield>();
            if (af != null)
            {
                float prevSpacing = af.takeoffSpacingSeconds;
                float prevBatch   = af.batchWaitTimeout;
                af.takeoffSpacingSeconds = NewTakeoffSpacingSeconds;
                af.batchWaitTimeout      = NewBatchWaitTimeout;
                EditorUtility.SetDirty(af);
                Debug.Log($"[AirfieldTakeoff] takeoffSpacingSeconds {prevSpacing} → {NewTakeoffSpacingSeconds}.");
                Debug.Log($"[AirfieldTakeoff] batchWaitTimeout {prevBatch} → {NewBatchWaitTimeout}.");
                Debug.Log($"[AirfieldTakeoff] maxConcurrentTakeoffs = {af.maxConcurrentTakeoffs} " +
                          $"(unchanged — pair-takeoff rule preserved).");
                Debug.Log($"[AirfieldTakeoff] synchronizedGroupTakeoff = {af.synchronizedGroupTakeoff} (unchanged).");
            }
            else
            {
                Debug.LogWarning("[ApplyAvoidBuilding] ⚠ No Airfield component on prefab root — " +
                                 "timing values not changed.");
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ApplyAvoidBuilding] ✓ Moved {slotsMoved}/6 per-slot Taxi points, " +
                      $"{corridorMoved}/{CorridorPoints.Length} corridor/exit points. " +
                      "Slot / runway queue / takeoff start/end / landing approach/start/end UNCHANGED. " +
                      "Run Tools → RTS → Buildings → Validate Airfield Taxi Paths to confirm.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[ApplyAvoidBuilding] ─────────────────────────────────────────");
    }
}
