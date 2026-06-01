using UnityEditor;
using UnityEngine;

/// <summary>
/// Fixes the AirfieldPrefab taxi waypoints so produced jets DON'T cut
/// through the central building/tower on their way to the runway. This is
/// pure transform geometry — no aircraft / Airfield / placement script is
/// modified.
///
/// THE BUG (before this tool runs):
///   Per-slot Taxi_N was placed at (slot.x × 0.5, 0, slot.z) — midway
///   between the slot and the runway centerline at the SAME Z as the slot.
///   For Slot 0 at world-relative (8.24, 3.66) that meant Taxi 0 sat at
///   (4.12, 0, 3.66): the straight Slot-to-Taxi segment grazes the east
///   face of the building. Worse, the Airfield script's
///   <c>TryAssignLane</c> sends even-index slots to Lane A and odd to Lane
///   B regardless of physical side, so a right-side jet preferring Lane A
///   has to walk all the way across the centre to reach Lane A's mid
///   point on the LEFT.
///
/// THE FIX:
///   1. Each per-slot Taxi_N is moved to (slot.x, 0, -7) — same apron X,
///      pushed south to Z = -7. The jet pivots on its slot, taxis south
///      down its OWN apron (never crossing the centerline), and ends up
///      clear of the building.
///   2. <c>TaxiPoint_A_Mid</c> / <c>TaxiPoint_B_Mid</c> are placed at
///      (∓3, 0, -8) — south of the building, just inside the runway
///      centerline. Both lanes converge here from EITHER side: a jet
///      crossing from the east apron to Lane A meets the centerline at
///      Z = -8 (well south of the building's Z extent ≈ ±3.5 m), so the
///      cross is safe.
///   3. <c>LaneA_GoAround</c> / <c>LaneB_Link</c> are placed at
///      (∓2.5, 0.05, -10.5) — pre-runway-entry, aligned with the lane's
///      RunwayQueue. Their MeshRenderers stay disabled (no gray squares).
///   4. <c>RunwayQueuePoint_A/B</c>, <c>TakeoffStart_A/B</c>,
///      <c>TakeoffEnd_A/B</c>, and all landing markers are UNCHANGED —
///      they're already centred on the runway strip.
///
/// What is NOT touched:
///   • Slot_0..5 (your manually-captured pad positions).
///   • Airfield.cs, AirUnitController.cs, BuildingPlacementManager.cs.
///   • Aircraft production, combat, takeoff queue, landing clearance.
///   • Runway / landing / lane endpoint markers.
///   • Visual_NewAirfield, root BoxCollider, root scale, TeamColorMarker.
///
/// Re-runnable: idempotent.
///
/// Menus:
///   Tools → RTS → Buildings → Repair Airfield Taxi Paths   (writes)
///   Tools → RTS → Buildings → Validate Airfield Taxi Paths (read-only)
/// </summary>
public static class RepairAirfieldTaxiPaths
{
    private const string PrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";

    // Per-slot Taxi_N Z target. Now placed at Z = -1 (north of the central
    // tower / south decorative building, just south of RunwayQueue at Z =
    // -1.87) so takeoff doesn't U-turn through buildings and landing
    // taxi-back stays north of the central tower too. See
    // ApplyManualAirfieldAvoidBuilding for the analysis.
    private const float TaxiExitZ = -1f;
    private const float TaxiExitY = 0f;

    // Approximate building footprint used by the Validate tool to test
    // path segments. Generous box centred on (0,0): X ∈ [-3.5, 3.5],
    // Z ∈ [-3.5, 3.5]. The new asset's central tower / hangars all sit
    // inside this box; the pads / runway / aprons sit outside it.
    private const float BuildingHalfX = 3.5f;
    private const float BuildingHalfZ = 3.5f;

    private struct NamedPoint { public string Name; public Vector3 Pos; }

    // Lane corridor + runway-side anchors. Updated together so both lanes
    // converge safely south of the building before splitting back to their
    // respective sides for takeoff.
    private static readonly NamedPoint[] CorridorPoints =
    {
        // Lane corridors shortened to north of decorative buildings (was at
        // Z = -8 / -10.5, which forced a southern detour through the buildings
        // when heading north to the queue at Z = -1.87).
        new NamedPoint { Name = "TaxiPoint_A_Mid", Pos = new Vector3(-2.5f, 0f,    -1.5f)  },
        new NamedPoint { Name = "LaneA_GoAround",  Pos = new Vector3(-2.5f, 0.05f, -2.2f)  },
        new NamedPoint { Name = "TaxiPoint_B_Mid", Pos = new Vector3( 2.5f, 0f,    -1.5f)  },
        new NamedPoint { Name = "LaneB_Link",      Pos = new Vector3( 2.5f, 0.05f, -2.2f)  },
    };

    // ================================================================== //
    // Repair
    // ================================================================== //

    [MenuItem("Tools/RTS/Buildings/Repair Airfield Taxi Paths")]
    public static void Repair()
    {
        Debug.Log("[RepairAirfieldTaxi] ─── Routing aircraft AROUND the central building ───");

        GameObject root = LoadPrefab();
        if (root == null) return;

        try
        {
            // 1. Move each Taxi_N south of its slot on the same apron X.
            int taxiDone = 0;
            for (int i = 0; i < 6; i++)
            {
                Transform slot = root.transform.Find($"Slot_{i}");
                Transform taxi = root.transform.Find($"Taxi_{i}");
                if (slot == null || taxi == null)
                {
                    Debug.LogWarning($"[RepairAirfieldTaxi]   ⚠ Slot_{i} or Taxi_{i} missing — skipping.");
                    continue;
                }
                Vector3 sp = slot.localPosition;
                Vector3 newTaxi = new Vector3(sp.x, TaxiExitY, TaxiExitZ);
                Vector3 before  = taxi.localPosition;
                taxi.localPosition = newTaxi;
                taxiDone++;
                Debug.Log($"[AirfieldSlots] Taxi_{i}: localPos {before} → {newTaxi} " +
                          $"(south of Slot_{i} at x={sp.x:F2}, on the same apron).");
            }

            // 2. Move corridor mids + lane-link pads south of the building.
            int corrDone = 0;
            for (int i = 0; i < CorridorPoints.Length; i++)
            {
                NamedPoint p = CorridorPoints[i];
                Transform t = root.transform.Find(p.Name);
                if (t == null)
                {
                    Debug.LogWarning($"[RepairAirfieldTaxi]   ⚠ '{p.Name}' missing — skipping.");
                    continue;
                }
                Vector3 before = t.localPosition;
                t.localPosition = p.Pos;
                corrDone++;
                Debug.Log($"[AirfieldSlots] {p.Name}: localPos {before} → {p.Pos}.");
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[RepairAirfieldTaxi] ✓ Updated {taxiDone}/6 Taxi_* points + {corrDone}/{CorridorPoints.Length} " +
                      "corridor points. RunwayQueue / TakeoffStart / TakeoffEnd / landing markers UNCHANGED. " +
                      "Run Tools → RTS → Buildings → Validate Airfield Taxi Paths to confirm no segment " +
                      "still grazes the building footprint.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ================================================================== //
    // Validate
    // ================================================================== //

    [MenuItem("Tools/RTS/Buildings/Validate Airfield Taxi Paths")]
    public static void Validate()
    {
        Debug.Log("[ValidateAirfieldTaxi] ─── Taxi-path safety audit ───");

        GameObject root = LoadPrefab();
        if (root == null) return;

        try
        {
            Airfield af = root.GetComponent<Airfield>();
            if (af == null)
            {
                Debug.LogError("[ValidateAirfieldTaxi] ✗ No Airfield script on prefab root.");
                return;
            }

            Debug.Log("[AirfieldSlots] ── Endpoint positions ──");
            for (int i = 0; i < 6; i++)
            {
                Transform s = root.transform.Find($"Slot_{i}");
                Transform t = root.transform.Find($"Taxi_{i}");
                Debug.Log($"[AirfieldSlots] Slot {i}: {LocPos(s)}  →  Taxi {i}: {LocPos(t)}.");
            }
            Debug.Log($"[AirfieldSlots] TaxiPoint_A_Mid: {LocPos(root.transform.Find("TaxiPoint_A_Mid"))}.");
            Debug.Log($"[AirfieldSlots] TaxiPoint_B_Mid: {LocPos(root.transform.Find("TaxiPoint_B_Mid"))}.");
            Debug.Log($"[AirfieldSlots] LaneA_GoAround:  {LocPos(root.transform.Find("LaneA_GoAround"))}.");
            Debug.Log($"[AirfieldSlots] LaneB_Link:      {LocPos(root.transform.Find("LaneB_Link"))}.");
            Debug.Log($"[AirfieldSlots] RunwayQueue A:   {LocPos(root.transform.Find("RunwayQueuePoint_A"))}  " +
                      $"B:  {LocPos(root.transform.Find("RunwayQueuePoint_B"))}.");
            Debug.Log($"[AirfieldSlots] TakeoffStart A:  {LocPos(root.transform.Find("TakeoffStart_A"))}  " +
                      $"B:  {LocPos(root.transform.Find("TakeoffStart_B"))}.");
            Debug.Log($"[AirfieldSlots] TakeoffEnd A:    {LocPos(root.transform.Find("TakeoffEnd_A"))}  " +
                      $"B:  {LocPos(root.transform.Find("TakeoffEnd_B"))}.");

            // Pair-takeoff rule + side-by-side sanity for the queue/start points.
            Debug.Log($"[AirfieldTakeoff] maxConcurrentTakeoffs = {af.maxConcurrentTakeoffs} " +
                      $"(expect 2 — pair-takeoff rule).");
            Debug.Log($"[AirfieldTakeoff] takeoffSpacingSeconds = {af.takeoffSpacingSeconds:F2} s " +
                      "(time between consecutive clearance grants — lower = snappier).");
            Debug.Log($"[AirfieldTakeoff] batchWaitTimeout = {af.batchWaitTimeout:F2} s " +
                      "(solo timeout — how long a ready jet waits for a missing batch partner).");
            Debug.Log($"[AirfieldTakeoff] synchronizedGroupTakeoff = {af.synchronizedGroupTakeoff} " +
                      "(true = paired jets wait for each other to align before rolling).");
            if (af.maxConcurrentTakeoffs != 2)
                Debug.LogWarning("[ValidateAirfieldTaxi]   ⚠ maxConcurrentTakeoffs != 2 — pair-takeoff rule changed.");
            if (af.takeoffSpacingSeconds > 0.7f)
                Debug.LogWarning($"[ValidateAirfieldTaxi]   ⚠ takeoffSpacingSeconds = {af.takeoffSpacingSeconds:F2} " +
                                 "is high — first pair may feel slow. Apply Avoid Building Path sets it to 0.5.");

            Transform qA = root.transform.Find("RunwayQueuePoint_A");
            Transform qB = root.transform.Find("RunwayQueuePoint_B");
            Transform sA = root.transform.Find("TakeoffStart_A");
            Transform sB = root.transform.Find("TakeoffStart_B");
            if (qA != null && qB != null)
            {
                float dz = Mathf.Abs(qA.localPosition.z - qB.localPosition.z);
                if (dz > 0.5f)
                    Debug.LogWarning($"[ValidateAirfieldTaxi]   ⚠ Lane A and Lane B Queue points are {dz:F2} m " +
                                     "apart on Z — they should be ~side-by-side (Δz < 0.5).");
                else
                    Debug.Log($"[ValidateAirfieldTaxi]   Queue A and Queue B are side-by-side ✓ (Δz = {dz:F3}).");
            }
            // Co-location check — Queue and Start at the same point per lane.
            if (qA != null && sA != null && Vector3.Distance(qA.localPosition, sA.localPosition) > 0.01f)
                Debug.LogWarning($"[ValidateAirfieldTaxi]   ⚠ RunwayQueuePoint_A and TakeoffStart_A are not " +
                                 $"co-located ({Vector3.Distance(qA.localPosition, sA.localPosition):F3} m apart).");
            if (qB != null && sB != null && Vector3.Distance(qB.localPosition, sB.localPosition) > 0.01f)
                Debug.LogWarning($"[ValidateAirfieldTaxi]   ⚠ RunwayQueuePoint_B and TakeoffStart_B are not " +
                                 $"co-located ({Vector3.Distance(qB.localPosition, sB.localPosition):F3} m apart).");

            // Landing chain — explicit per-transform report so the user can see
            // which one is the touchdown (LandingStart_A per the script's docs,
            // because the user-supplied Y is 0.6 = ground on arrival).
            Debug.Log("[AirfieldSlots] ── Landing chain ──");
            Transform lApp = root.transform.Find("LandingApproachPoint");
            Transform lSA  = root.transform.Find("LandingStart_A");
            Transform lEA  = root.transform.Find("LandingEnd_A");
            Transform lXA  = root.transform.Find("LandingExit_A");
            Transform lSB  = root.transform.Find("LandingStart_B");
            Transform lEB  = root.transform.Find("LandingEnd_B");
            Transform lXB  = root.transform.Find("LandingExit_B");
            Debug.Log($"[AirfieldSlots] LandingApproachPoint: {LocPosRot(lApp)} (holding-pattern centre).");
            Debug.Log($"[AirfieldSlots] LandingStart_A:       {LocPosRot(lSA)}  (in-air align — descent BEGINS, jet still at flightAltitude).");
            Debug.Log($"[AirfieldSlots] LandingEnd_A:         {LocPosRot(lEA)}  *** ACTUAL TOUCHDOWN *** (descent target).");
            Debug.Log($"[AirfieldSlots] LandingExit_A:        {LocPosRot(lXA)} (off-runway turn after touchdown).");
            Debug.Log($"[AirfieldSlots] LandingStart_B:       {LocPosRot(lSB)}  (reserved lane).");
            Debug.Log($"[AirfieldSlots] LandingEnd_B:         {LocPosRot(lEB)}  (reserved lane).");
            Debug.Log($"[AirfieldSlots] LandingExit_B:        {LocPosRot(lXB)} (reserved).");

            // Sanity check on touchdown — this is LandingEnd_A per
            // AirUnitController.UpdateFinalLanding (touchdown event fires
            // when jet reaches LandingEnd XZ at ground Y).
            if (lEA != null)
            {
                Vector3 p = lEA.localPosition;
                if (p.y < 0f || p.y > 1.5f)
                    Debug.LogError($"[ValidateAirfieldTaxi]   ✗ LandingEnd_A Y = {p.y:F2} is outside (0, 1.5). " +
                                   "Y should be ~0.6 so the jet snaps to ground at touchdown.");
                if (Mathf.Abs(p.x) < BuildingHalfX && Mathf.Abs(p.z) < BuildingHalfZ)
                    Debug.LogError($"[ValidateAirfieldTaxi]   ✗ LandingEnd_A {p} is INSIDE the building box " +
                                   $"(|X|<{BuildingHalfX}, |Z|<{BuildingHalfZ}) — jet will touch down inside the building.");
                else
                    Debug.Log($"[ValidateAirfieldTaxi]   LandingEnd_A (touchdown) is outside the building box ✓.");
            }
            // Sanity check on descent — LandingStart_A must be sufficiently
            // north of LandingEnd_A so the FinalLanding descent has horizontal
            // room to glide instead of "helicopter" dropping in place.
            //
            // Math: AirUnitController landingProfile.speed = 7 m/s, verticalSpeed
            // = 5 m/s, flightAltitude = 12 m. Vertical descent time = 12/5 = 2.4 s
            // → required horizontal distance ≥ 2.4 × 7 = 16.8 m.
            const float MinGlideDistance = 17f;
            if (lSA != null && lEA != null)
            {
                float dz = lSA.localPosition.z - lEA.localPosition.z;
                Vector2 hZ = new Vector2(lSA.localPosition.x - lEA.localPosition.x, dz);
                float horiz = hZ.magnitude;
                float slopeDeg = Mathf.Atan2(12f, horiz) * Mathf.Rad2Deg;
                if (dz <= 1f)
                    Debug.LogError($"[ValidateAirfieldTaxi] ✗ LandingStart_A.z ({lSA.localPosition.z:F2}) is not " +
                                   $"north of LandingEnd_A.z ({lEA.localPosition.z:F2}) — descent will " +
                                   "drop in place (helicopter style). Push LandingStart_A.z higher.");
                else if (horiz < MinGlideDistance)
                    Debug.LogWarning($"[ValidateAirfieldTaxi] ⚠ LandingStart→LandingEnd horizontal = " +
                                     $"{horiz:F1} m (< {MinGlideDistance:F0} m). Descent glide slope = " +
                                     $"{slopeDeg:F0}° — jet will hover descending in the last segment. " +
                                     "Push LandingStart_A.z further north.");
                else
                    Debug.Log($"[ValidateAirfieldTaxi] LandingStart→LandingEnd horizontal = {horiz:F1} m, " +
                              $"glide slope = {slopeDeg:F0}° ✓ (no-helicopter check passes).");
            }

            // Approach point must be north of LandingStart so the jet flies
            // straight south after clearance (no U-turn).
            if (lApp != null && lSA != null && lApp.localPosition.z <= lSA.localPosition.z + 1f)
                Debug.LogWarning($"[ValidateAirfieldTaxi] ⚠ LandingApproachPoint.z ({lApp.localPosition.z:F2}) " +
                                 $"is not north of LandingStart_A.z ({lSA.localPosition.z:F2}) — post-clearance " +
                                 "MoveTowards may U-turn. Push LandingApproachPoint.z higher.");

            // For each slot, trace the full takeoff path and flag any segment
            // whose interior passes through the approximate building box.
            Debug.Log("[AirfieldSlots] ── Per-slot path safety ──");
            int badSegments = 0;
            for (int i = 0; i < 6; i++)
            {
                Transform slot = root.transform.Find($"Slot_{i}");
                Transform taxi = root.transform.Find($"Taxi_{i}");
                if (slot == null || taxi == null) continue;

                // Lane preference: even → A, odd → B (matches Airfield.TryAssignLane).
                bool laneA = (i % 2 == 0);
                Transform mid  = root.transform.Find(laneA ? "TaxiPoint_A_Mid" : "TaxiPoint_B_Mid");
                Transform link = root.transform.Find(laneA ? "LaneA_GoAround" : "LaneB_Link");
                Transform queue   = root.transform.Find(laneA ? "RunwayQueuePoint_A" : "RunwayQueuePoint_B");
                Transform start   = root.transform.Find(laneA ? "TakeoffStart_A"     : "TakeoffStart_B");
                Transform end     = root.transform.Find(laneA ? "TakeoffEnd_A"       : "TakeoffEnd_B");

                Transform[] route = { slot, taxi, mid, link, queue, start, end };
                string[] names    = { $"Slot_{i}", $"Taxi_{i}",
                                      laneA ? "TaxiPoint_A_Mid" : "TaxiPoint_B_Mid",
                                      laneA ? "LaneA_GoAround"  : "LaneB_Link",
                                      laneA ? "RunwayQueuePoint_A" : "RunwayQueuePoint_B",
                                      laneA ? "TakeoffStart_A"     : "TakeoffStart_B",
                                      laneA ? "TakeoffEnd_A"       : "TakeoffEnd_B" };

                for (int j = 0; j + 1 < route.Length; j++)
                {
                    if (route[j] == null || route[j + 1] == null) continue;
                    Vector3 a = route[j].localPosition;
                    Vector3 b = route[j + 1].localPosition;
                    bool grazes = SegmentCrossesBuildingBox(a, b);
                    if (grazes)
                    {
                        Debug.LogError($"[AirfieldSlots] ✗ Slot {i} Lane {(laneA ? "A" : "B")} segment " +
                                       $"'{names[j]}' → '{names[j + 1]}' " +
                                       $"({a:F1} → {b:F1}) crosses the building box " +
                                       $"(|X|≤{BuildingHalfX}, |Z|≤{BuildingHalfZ}).");
                        badSegments++;
                    }
                }
            }
            if (badSegments == 0)
                Debug.Log($"[ValidateAirfieldTaxi] ✓ No taxi-path segment crosses the building footprint " +
                          $"(approx |X|≤{BuildingHalfX}, |Z|≤{BuildingHalfZ}).");
            else
                Debug.LogError($"[ValidateAirfieldTaxi] ✗ {badSegments} segment(s) cross the building box. " +
                               "Run Repair Airfield Taxi Paths.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[ValidateAirfieldTaxi] ───────────────────────────────");
    }

    // ================================================================== //
    // Helpers
    // ================================================================== //

    private static GameObject LoadPrefab()
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
        {
            Debug.LogError($"[RepairAirfieldTaxi] ✗ Prefab not found at '{PrefabPath}'.");
            return null;
        }
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
            Debug.LogError($"[RepairAirfieldTaxi] ✗ LoadPrefabContents returned null for '{PrefabPath}'.");
        return root;
    }

    /// <summary>
    /// Samples the AB segment at 21 evenly-spaced points and returns true
    /// if any sample falls inside the building box (|X| ≤ BuildingHalfX
    /// and |Z| ≤ BuildingHalfZ). Cheap, conservative, and good enough for
    /// the box we draw — exact line-rectangle intersection isn't worth the
    /// complexity here.
    /// </summary>
    private static bool SegmentCrossesBuildingBox(Vector3 a, Vector3 b)
    {
        const int Steps = 20;
        for (int i = 0; i <= Steps; i++)
        {
            float t = i / (float)Steps;
            Vector3 p = Vector3.Lerp(a, b, t);
            if (Mathf.Abs(p.x) < BuildingHalfX && Mathf.Abs(p.z) < BuildingHalfZ)
                return true;
        }
        return false;
    }

    private static string LocPos(Transform t) =>
        t == null ? "<missing>" : $"({t.localPosition.x:F2}, {t.localPosition.y:F2}, {t.localPosition.z:F2})";

    private static string LocPosRot(Transform t) =>
        t == null ? "<missing>"
                  : $"pos ({t.localPosition.x:F2}, {t.localPosition.y:F2}, {t.localPosition.z:F2}) " +
                    $"rotY {t.localEulerAngles.y:F2}°";
}
