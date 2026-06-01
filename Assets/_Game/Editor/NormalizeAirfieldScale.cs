using UnityEditor;
using UnityEngine;

/// <summary>
/// Sole authoritative knob for the Airfield's visible size after the
/// Airfield_Unity_Final asset has been swapped in. The model itself
/// imports at ~70 × 70 m, which dominates the RTS map; this tool sets
/// <c>Visual_NewAirfield.localScale</c> to a single normalized value and
/// then proportionally updates the BoxCollider, SelectionRing, all six
/// parking slots + taxi points, the runway / lane corridor / landing
/// markers, so production / takeoff / landing / selection / placement all
/// agree with the visible airport size in one pass.
///
/// PRINCIPLES (don't break these in future tweaks):
///   • Root transform stays at <c>(1,1,1)</c>. Only the VISUAL child gets
///     a non-1 scale.
///   • One BoxCollider on the root, flat-footprint convention (Y size 2,
///     centre Y 1) — same as every other building.
///   • No MeshCollider, no per-mesh colliders inside the visual subtree.
///   • Slot / taxi / runway / landing names + Airfield.cs serialized refs
///     are NEVER renamed. The Airfield script's API stays binary-compatible.
///   • Aircraft state machine (<see cref="AirUnitController"/>) is not touched.
///
/// USER-FACING DIAL:
///   <see cref="TargetVisibleSize"/> is the only number you need to edit
///   to change the airport's size. Defaults to **35 m** — a clear
///   reduction from the previous 50 m default that still leaves the airport
///   visibly bigger than a tank / Barracks / VehicleFactory. Bump it back
///   up (e.g. 40, 45) or lower it (e.g. 30) and re-run the menu.
///   Constraints on values:
///     • Must be > 12 (six aircraft slots won't fit otherwise).
///     • Must be < 60 (above that we're back to "dominates the map").
///   The Validate menu reports if the current state is outside this band.
///
/// Menus:
///   Tools → RTS → Buildings → Normalize Airfield Scale   (writes)
///   Tools → RTS → Buildings → Validate Airfield Scale    (read-only)
/// </summary>
public static class NormalizeAirfieldScale
{
    private const string PrefabPath        = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";
    private const string VisualChildName   = "Visual_NewAirfield";
    private const string SelectionRingName = "SelectionRing";

    // ───────────────────────── USER-FACING DIAL ───────────────────────── //
    private const float TargetVisibleSize  = 28f;    // ← change me to resize (was 35; 20% smaller pass)
    private const float NativeAssetSize    = 70f;    // Airfield_Unity_Final native footprint
    private const float MinReasonableSize  = 12f;
    private const float MaxReasonableSize  = 60f;
    // ─────────────────────────────────────────────────────────────────── //

    // Slot / runway / landing positions for the 28 m envelope. Z spacing of
    // 4 m on the east apron keeps four parked jets close but non-overlapping.
    // Kept in sync with UseNewAirfieldVisual.cs so re-running Normalize alone
    // is sufficient to reset the layout to this size.
    private struct SlotLayout
    {
        public int     Index;
        public Vector3 Slot;
        public Vector3 Taxi;
        public float   RotationY;
    }
    // EXACT user-captured positions. The user manually placed 6 StrikeJetPrefabs
    // on the painted parking pads of the Airfield_Unity_Final model and gave us
    // the world positions; with the AirfieldPrefab root at world (1, ~0, 1)
    // those resolve to the local positions below. Rotation: right-side jets
    // observed at Y = -55.959° (nose toward (-X, +Z) — diagonal inward + north
    // toward the takeoff end); left side mirrored to +55.959°. Y = 0.6 matches
    // the user's reference jet height.
    //
    // Single source of truth — <see cref="ApplyManualAirfieldSlotReference"/>
    // also commits these same values. Re-running EITHER tool produces the
    // identical result. If you tweak the layout later, drag slots in the
    // prefab, then run Tools → RTS → Buildings → Report Airfield Slot Positions
    // and paste the captured block over this one.
    //
    // Taxi points sit midway between each slot and the runway centerline
    // (X = slot.x × 0.5, same Z, Y = 0) — the ordered taxi waypoint walk
    // (per-slot Taxi → lane corridor mid → runway queue → start) keeps the
    // jet rotating naturally as it heads inboard.
    private static readonly SlotLayout[] Layout =
    {
        // Per-slot Taxi at Z = -1 (north of decorative buildings, just south
        // of RunwayQueue at Z = -1.87). Same Taxi point serves takeoff
        // pull-out AND landing taxi-back. See ApplyManualAirfieldAvoidBuilding.
        new SlotLayout { Index = 0, Slot = new Vector3( 8.23999977f, 0.6f,  3.65932417f), Taxi = new Vector3( 8.23999977f, 0f, -1f), RotationY = -55.959f },
        new SlotLayout { Index = 1, Slot = new Vector3( 6.38754559f, 0.6f,  0.20483637f), Taxi = new Vector3( 6.38754559f, 0f, -1f), RotationY = -55.959f },
        new SlotLayout { Index = 2, Slot = new Vector3( 4.69712925f, 0.6f, -4.08381081f), Taxi = new Vector3( 4.69712925f, 0f, -1f), RotationY = -55.959f },
        new SlotLayout { Index = 3, Slot = new Vector3(-5.27180529f, 0.6f, -4.35003638f), Taxi = new Vector3(-5.27180529f, 0f, -1f), RotationY =  55.959f },
        new SlotLayout { Index = 4, Slot = new Vector3(-6.12523460f, 0.6f,  0.26885521f), Taxi = new Vector3(-6.12523460f, 0f, -1f), RotationY =  55.959f },
        new SlotLayout { Index = 5, Slot = new Vector3(-8.05147648f, 0.6f,  3.80998421f), Taxi = new Vector3(-8.05147648f, 0f, -1f), RotationY =  55.959f },
    };

    // <c>RotY</c> = NaN means "don't touch the existing rotation" — used for
    // waypoints whose orientation doesn't matter (lane corridor mids, landing
    // markers). The six runway-aligned points (queue + start + end per lane)
    // all share <see cref="ApplyManualAirfieldTakeoffPoints.RunwayHeadingY"/>
    // so the painted runway direction is honoured.
    private struct NamedPoint { public string Name; public Vector3 Pos; public float RotY; }
    private static readonly NamedPoint[] RunwayPoints =
    {
        // Queue + Start at the SAME local position per lane (user-captured).
        // RunwayQueuePoint and TakeoffStart sharing a point is intentional:
        // the Airfield script enqueues both waypoints, but with the same Pos
        // the jet just transitions states without moving — exactly the
        // hold-short → roll behaviour we want.
        new NamedPoint { Name = "RunwayQueuePoint_A",   Pos = new Vector3(-1.819133043f, 0.6f, -1.87182045f),  RotY = ApplyManualAirfieldTakeoffPoints.RunwayHeadingY },
        new NamedPoint { Name = "TakeoffStart_A",       Pos = new Vector3(-1.819133043f, 0.6f, -1.87182045f),  RotY = ApplyManualAirfieldTakeoffPoints.RunwayHeadingY },
        new NamedPoint { Name = "TakeoffEnd_A",         Pos = new Vector3(-1.296764731f, 0.6f, 11.1273003f),   RotY = ApplyManualAirfieldTakeoffPoints.RunwayHeadingY },
        new NamedPoint { Name = "RunwayQueuePoint_B",   Pos = new Vector3( 0.68086314f,  0.6f, -1.876122177f), RotY = ApplyManualAirfieldTakeoffPoints.RunwayHeadingY },
        new NamedPoint { Name = "TakeoffStart_B",       Pos = new Vector3( 0.68086314f,  0.6f, -1.876122177f), RotY = ApplyManualAirfieldTakeoffPoints.RunwayHeadingY },
        new NamedPoint { Name = "TakeoffEnd_B",         Pos = new Vector3( 0.95323193f,  0.6f, 11.1234283f),   RotY = ApplyManualAirfieldTakeoffPoints.RunwayHeadingY },
        // Lane corridor convergence (south of building) — orientation immaterial.
        // Lane corridor shortened to north of decorative buildings.
        new NamedPoint { Name = "TaxiPoint_A_Mid",      Pos = new Vector3(-2.5f, 0f,    -1.5f),  RotY = float.NaN },
        new NamedPoint { Name = "TaxiPoint_B_Mid",      Pos = new Vector3( 2.5f, 0f,    -1.5f),  RotY = float.NaN },
        new NamedPoint { Name = "LaneA_GoAround",       Pos = new Vector3(-2.5f, 0.05f, -2.2f),  RotY = float.NaN },
        new NamedPoint { Name = "LaneB_Link",           Pos = new Vector3( 2.5f, 0.05f, -2.2f),  RotY = float.NaN },
        // Landing chain — unchanged from prior pass.
        // LandingApproachPoint = holding-pattern centre. Far north of
        // LandingStart so post-clearance MoveTowards is straight south.
        new NamedPoint { Name = "LandingApproachPoint", Pos = ApplyManualAirfieldLandingTouchdown.ApproachHoldLocal, RotY = ApplyManualAirfieldLandingTouchdown.TouchdownHeadingY },
        // LandingStart = in-air align (25 m north of touchdown so the
        // FinalLanding descent has room to glide instead of dropping in place).
        new NamedPoint { Name = "LandingStart_A",       Pos = ApplyManualAirfieldLandingTouchdown.ApproachAnchorLocal, RotY = ApplyManualAirfieldLandingTouchdown.TouchdownHeadingY },
        // LandingEnd = ACTUAL TOUCHDOWN (user-captured). Per UpdateFinalLanding,
        // the jet descends from finalLandingStartXZ toward landingEndA and the
        // touchdown event fires when the jet reaches this XZ at ground Y.
        new NamedPoint { Name = "LandingEnd_A",         Pos = new Vector3(-0.739974201f, 0.6f, 11.4898415f), RotY = ApplyManualAirfieldLandingTouchdown.TouchdownHeadingY },
        // LandingExit moved north of central tower (was at Z=-11 which made
        // post-touchdown taxi cross the central tower).
        new NamedPoint { Name = "LandingExit_A",        Pos = new Vector3( 0f,   0f,     6f),    RotY = float.NaN },
        new NamedPoint { Name = "LandingStart_B",       Pos = ApplyManualAirfieldLandingTouchdown.ApproachAnchorLocal, RotY = ApplyManualAirfieldLandingTouchdown.TouchdownHeadingY },
        new NamedPoint { Name = "LandingEnd_B",         Pos = new Vector3(-0.739974201f, 0.6f, 11.4898415f), RotY = ApplyManualAirfieldLandingTouchdown.TouchdownHeadingY },
        new NamedPoint { Name = "LandingExit_B",        Pos = new Vector3( 0f,   0f,     6f),    RotY = float.NaN },
    };

    private const float TakeoffCenterTolerance = 2f;

    // ================================================================== //
    // Normalize
    // ================================================================== //

    [MenuItem("Tools/RTS/Buildings/Normalize Airfield Scale")]
    public static void Normalize()
    {
        Debug.Log($"[NormalizeAirfield] ─── Normalizing to {TargetVisibleSize} m visible footprint ───");

        if (TargetVisibleSize <= MinReasonableSize || TargetVisibleSize >= MaxReasonableSize)
        {
            Debug.LogError($"[NormalizeAirfield] ✗ TargetVisibleSize {TargetVisibleSize} is outside the " +
                           $"safe band [{MinReasonableSize}, {MaxReasonableSize}]. Edit the constant and retry.");
            return;
        }

        GameObject root = LoadPrefab();
        if (root == null) return;

        try
        {
            float scale = TargetVisibleSize / NativeAssetSize;
            int changes = 0;

            changes += ApplyVisualScale(root, scale);
            changes += ApplyBoxCollider(root);
            changes += ApplySelectionRing(root);
            changes += RepositionSlotsAndTaxi(root);
            changes += RepositionRunwayAndLanding(root);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[NormalizeAirfield] ✓ Applied {changes} change(s). " +
                      "Run Tools → RTS → Buildings → Validate Airfield Scale to confirm.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ================================================================== //
    // Validate
    // ================================================================== //

    [MenuItem("Tools/RTS/Buildings/Validate Airfield Scale")]
    public static void Validate()
    {
        Debug.Log("[ValidateAirfieldScale] ─── Scale audit ───");

        GameObject root = LoadPrefab();
        if (root == null) return;

        try
        {
            Debug.Log($"[ValidateAirfieldScale] Prefab: {PrefabPath}.");
            Debug.Log($"[ValidateAirfieldScale] Root.localScale = {root.transform.localScale}  " +
                      (root.transform.localScale == Vector3.one ? "✓" : "  ✗ root scale should be (1,1,1)!"));

            ReportVisualAndBounds(root);
            ReportCollider(root);
            ReportSelectionRing(root);
            ReportSlots(root);
            ReportRunwayAndLanding(root);
            ReportComparisonHint();
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[ValidateAirfieldScale] ─── End of audit ───");
    }

    // ================================================================== //
    // Normalize step helpers
    // ================================================================== //

    private static int ApplyVisualScale(GameObject root, float scale)
    {
        Transform vis = root.transform.Find(VisualChildName);
        if (vis == null)
        {
            Debug.LogError($"[NormalizeAirfield] ✗ No '{VisualChildName}' child found. " +
                           "Run Tools → RTS → Buildings → Use New Airfield Visual first.");
            return 0;
        }
        Vector3 before = vis.localScale;
        Vector3 after  = new Vector3(scale, scale, scale);
        vis.localPosition = Vector3.zero;
        vis.localRotation = Quaternion.identity;
        vis.localScale    = after;
        Debug.Log($"[NormalizeAirfield]   Visual_NewAirfield.localScale {before} → {after} " +
                  $"(native 70 m → {TargetVisibleSize} m visible).");
        return 1;
    }

    private static int ApplyBoxCollider(GameObject root)
    {
        BoxCollider bc = root.GetComponent<BoxCollider>();
        if (bc == null)
        {
            bc = root.AddComponent<BoxCollider>();
            Debug.LogWarning("[NormalizeAirfield]   Root BoxCollider was missing — recreated.");
        }
        Vector3 sizeBefore   = bc.size;
        Vector3 centerBefore = bc.center;
        bc.size   = new Vector3(TargetVisibleSize, 2f, TargetVisibleSize);
        bc.center = new Vector3(0f, 1f, 0f);
        Debug.Log($"[NormalizeAirfield]   Root BoxCollider: size {sizeBefore} → {bc.size}, " +
                  $"center {centerBefore} → {bc.center}.");
        return 1;
    }

    private static int ApplySelectionRing(GameObject root)
    {
        Transform ring = root.transform.Find(SelectionRingName);
        if (ring == null)
        {
            Debug.Log($"[NormalizeAirfield]   No '{SelectionRingName}' child — skipping.");
            return 0;
        }
        Vector3 posBefore   = ring.localPosition;
        Vector3 scaleBefore = ring.localScale;
        // Centred on the building; slightly smaller than the collider so the
        // ring sits just inside the footprint rather than poking past it.
        float ringSize = TargetVisibleSize * 0.95f;
        ring.localPosition = new Vector3(0f, 0.02f, 0f);
        ring.localScale    = new Vector3(ringSize, 0.02f, ringSize);
        Debug.Log($"[NormalizeAirfield]   SelectionRing: localPosition {posBefore} → {ring.localPosition}, " +
                  $"localScale {scaleBefore} → {ring.localScale} (95% of footprint).");
        return 1;
    }

    private static int RepositionSlotsAndTaxi(GameObject root)
    {
        int done = 0;
        for (int i = 0; i < Layout.Length; i++)
        {
            SlotLayout L = Layout[i];
            Transform slot = root.transform.Find($"Slot_{L.Index}");
            Transform taxi = root.transform.Find($"Taxi_{L.Index}");
            if (slot == null || taxi == null)
            {
                Debug.LogError($"[NormalizeAirfield] ✗ Slot_{L.Index} or Taxi_{L.Index} missing. " +
                               "Run Tools → RTS → Air System → Repair Airfield Layout (legacy) to recreate anchors.");
                continue;
            }
            slot.localPosition    = L.Slot;
            slot.localEulerAngles = new Vector3(0f, L.RotationY, 0f);
            taxi.localPosition    = L.Taxi;
            taxi.localEulerAngles = new Vector3(0f, L.RotationY, 0f);
            done++;
        }
        Debug.Log($"[NormalizeAirfield]   Repositioned {done}/6 Slot_* / Taxi_* pairs (user-captured reference). " +
                  "RIGHT slots 0/1/2 rotY = -55.959° (nose NW).  LEFT slots 3/4/5 rotY = +55.959° (nose NE). " +
                  $"Y = 0.6 to match the reference jet height.  Inside the {TargetVisibleSize/2:F1} m collider half-width.");
        return done;
    }

    private static int RepositionRunwayAndLanding(GameObject root)
    {
        int done = 0;
        for (int i = 0; i < RunwayPoints.Length; i++)
        {
            NamedPoint p = RunwayPoints[i];
            Transform t = root.transform.Find(p.Name);
            if (t == null)
            {
                Debug.LogWarning($"[NormalizeAirfield] ⚠ '{p.Name}' missing — skipping.");
                continue;
            }
            t.localPosition = p.Pos;
            if (!float.IsNaN(p.RotY))
                t.localEulerAngles = new Vector3(0f, p.RotY, 0f);
            done++;
        }
        Debug.Log($"[NormalizeAirfield]   Repositioned {done}/{RunwayPoints.Length} runway / lane / landing points. " +
                  $"Queue/Start co-located per lane at user-captured spot; runway heading Y = " +
                  $"{ApplyManualAirfieldTakeoffPoints.RunwayHeadingY:F4}°.");
        return done;
    }

    // ================================================================== //
    // Validate sections
    // ================================================================== //

    private static void ReportVisualAndBounds(GameObject root)
    {
        Transform vis = root.transform.Find(VisualChildName);
        if (vis == null)
        {
            Debug.LogError($"[ValidateAirfieldScale] ✗ '{VisualChildName}' missing. Run Use New Airfield Visual.");
            return;
        }
        float expected = TargetVisibleSize / NativeAssetSize;
        bool scaleOK = Mathf.Abs(vis.localScale.x - expected) < 0.01f
                    && Mathf.Abs(vis.localScale.y - expected) < 0.01f
                    && Mathf.Abs(vis.localScale.z - expected) < 0.01f;

        Renderer[] rs = vis.GetComponentsInChildren<Renderer>(includeInactive: false);
        Bounds combined = default;
        bool found = false;
        for (int i = 0; i < rs.Length; i++)
        {
            Renderer r = rs[i];
            if (r == null) continue;
            if (!found) { combined = r.bounds; found = true; }
            else        { combined.Encapsulate(r.bounds); }
        }
        string boundsStr = found ? combined.size.ToString("F1") : "(no active renderers)";

        Debug.Log($"[ValidateAirfieldScale]   {VisualChildName}.localScale = {vis.localScale} " +
                  $"(expected ≈ ({expected:F3},{expected:F3},{expected:F3}))  {(scaleOK ? "✓" : "✗")}.");
        Debug.Log($"[ValidateAirfieldScale]   Combined visible bounds size = {boundsStr}  (target ≈ " +
                  $"({TargetVisibleSize},_,{TargetVisibleSize})).");

        if (found && (combined.size.x > TargetVisibleSize * 1.3f || combined.size.z > TargetVisibleSize * 1.3f))
            Debug.LogWarning($"[ValidateAirfieldScale]   ⚠ Bounds {combined.size:F1} more than 30% over target — " +
                             $"still too big. Lower TargetVisibleSize in NormalizeAirfieldScale.cs and re-run Normalize.");
        if (found && (combined.size.x < TargetVisibleSize * 0.5f || combined.size.z < TargetVisibleSize * 0.5f))
            Debug.LogWarning($"[ValidateAirfieldScale]   ⚠ Bounds {combined.size:F1} less than half the target — " +
                             "the airport looks miniature. Raise TargetVisibleSize.");
    }

    private static void ReportCollider(GameObject root)
    {
        BoxCollider bc = root.GetComponent<BoxCollider>();
        if (bc == null)
        {
            Debug.LogError("[ValidateAirfieldScale] ✗ Root BoxCollider missing.");
            return;
        }
        Vector3 expectedSize   = new Vector3(TargetVisibleSize, 2f, TargetVisibleSize);
        Vector3 expectedCenter = new Vector3(0f, 1f, 0f);
        bool ok = bc.size == expectedSize && bc.center == expectedCenter;
        Debug.Log($"[ValidateAirfieldScale]   Root BoxCollider: size = {bc.size}, center = {bc.center}  " +
                  $"(expected size {expectedSize}, center {expectedCenter})  {(ok ? "✓" : "✗ run Normalize")}.");

        // No MeshCollider / non-root colliders should remain.
        Collider[] cols = root.GetComponentsInChildren<Collider>(includeInactive: true);
        int nonRoot = 0, mesh = 0;
        for (int i = 0; i < cols.Length; i++)
        {
            if (cols[i] == null) continue;
            if (cols[i].transform != root.transform) nonRoot++;
            if (cols[i] is MeshCollider) mesh++;
        }
        if (nonRoot > 0)
            Debug.LogError($"[ValidateAirfieldScale]   ✗ {nonRoot} non-root collider(s) present. Only the root " +
                           "BoxCollider should exist. Run Tools → RTS → Buildings → Repair Airfield Runtime Prefab.");
        if (mesh > 0)
            Debug.LogError($"[ValidateAirfieldScale]   ✗ {mesh} MeshCollider(s) present — these can cause placement freezes.");
    }

    private static void ReportSelectionRing(GameObject root)
    {
        Transform ring = root.transform.Find(SelectionRingName);
        if (ring == null) { Debug.Log("[ValidateAirfieldScale]   SelectionRing: absent."); return; }
        Debug.Log($"[ValidateAirfieldScale]   SelectionRing.localScale = {ring.localScale} " +
                  $"(target ≈ ({TargetVisibleSize * 0.95f:F1}, 0.02, {TargetVisibleSize * 0.95f:F1})).");
    }

    private static void ReportSlots(GameObject root)
    {
        float halfX = TargetVisibleSize / 2f;
        float halfZ = TargetVisibleSize / 2f;
        int outside = 0;
        int leftCount = 0, rightCount = 0;
        Transform[] taxis = new Transform[6];
        for (int i = 0; i < 6; i++)
        {
            Transform s = root.transform.Find($"Slot_{i}");
            Transform t = root.transform.Find($"Taxi_{i}");
            taxis[i] = t;
            if (s == null) { Debug.LogError($"[ValidateAirfieldScale] ✗ Slot_{i} missing."); continue; }
            Vector3 lp = s.localPosition;
            float rotY = s.localEulerAngles.y;
            bool oob = Mathf.Abs(lp.x) > halfX - 1f || Mathf.Abs(lp.z) > halfZ - 1f;
            if (oob) outside++;
            string side = lp.x < -0.1f ? "LEFT " : lp.x > 0.1f ? "RIGHT" : "MID  ";
            if (lp.x < -0.1f) leftCount++; else if (lp.x > 0.1f) rightCount++;
            string flag = oob ? "  ✗ outside footprint" : "";
            Vector3 tp = t != null ? t.localPosition : Vector3.zero;
            Debug.Log($"[AirfieldSlots] Slot {i} [{side}] localPos = {lp:F1} rotY = {rotY:F0}° | " +
                      $"Taxi {i} localPos = {tp:F1}.{flag}");
        }

        // Grouping check — user expects 3 LEFT + 3 RIGHT.
        if (leftCount == 3 && rightCount == 3)
            Debug.Log("[ValidateAirfieldScale]   Grouping: 3 LEFT + 3 RIGHT ✓.");
        else
            Debug.LogWarning($"[ValidateAirfieldScale]   ⚠ Grouping is {leftCount} LEFT + {rightCount} RIGHT " +
                             "(user-preferred is 3 + 3). Run Normalize Airfield Scale.");

        // Rotation check — RIGHT slots (positive X) should be at Y = -55.959°
        // (equivalent 304.041°) per user reference; LEFT slots mirrored to
        // +55.959°. Allow a 15° tolerance for manual nudges.
        int badRot = 0;
        for (int i = 0; i < 6; i++)
        {
            Transform s = root.transform.Find($"Slot_{i}");
            if (s == null) continue;
            Vector3 lp = s.localPosition;
            float rotY = s.localEulerAngles.y;
            if (lp.x > 0.1f && Mathf.Abs(Mathf.DeltaAngle(rotY, -55.959f)) > 15f)
            {
                Debug.LogWarning($"[ValidateAirfieldScale]   ⚠ RIGHT slot {i} rotY={rotY:F2}° is > 15° " +
                                 "off the user-reference -55.959° (nose NW).");
                badRot++;
            }
            if (lp.x < -0.1f && Mathf.Abs(Mathf.DeltaAngle(rotY, 55.959f)) > 15f)
            {
                Debug.LogWarning($"[ValidateAirfieldScale]   ⚠ LEFT slot {i} rotY={rotY:F2}° is > 15° " +
                                 "off the mirrored +55.959° (nose NE).");
                badRot++;
            }
        }
        if (badRot == 0)
            Debug.Log("[ValidateAirfieldScale]   All slot rotations within 15° of the ±55.959° reference ✓.");

        // Y-height check — user's reference sat at Y = 0.6. Warn if any slot
        // strayed below 0 (below ground) or above 1.5 (floating).
        int badY = 0;
        for (int i = 0; i < 6; i++)
        {
            Transform s = root.transform.Find($"Slot_{i}");
            if (s == null) continue;
            float y = s.localPosition.y;
            if (y < 0f)
            {
                Debug.LogError($"[ValidateAirfieldScale]   ✗ Slot {i} Y = {y:F2} (BELOW ground). Jet will sink under the airfield.");
                badY++;
            }
            else if (y > 1.5f)
            {
                Debug.LogWarning($"[ValidateAirfieldScale]   ⚠ Slot {i} Y = {y:F2} (above 1.5 m). Jet may visibly float over the apron.");
                badY++;
            }
        }
        if (badY == 0)
            Debug.Log("[ValidateAirfieldScale]   All 6 slot Y values inside (0, 1.5) ✓.");

        if (outside > 0)
            Debug.LogError($"[ValidateAirfieldScale]   ✗ {outside} slot(s) outside the {TargetVisibleSize:F0} m " +
                           "footprint. Run Normalize Airfield Scale.");
        else
            Debug.Log($"[ValidateAirfieldScale]   All 6 slots sit inside the {TargetVisibleSize:F0} m footprint ✓.");

        // Pad-centering check is purely visual — we can't read FBX mesh pad
        // coordinates from script. Surface this clearly so the user knows
        // it's their last verification step.
        Debug.Log("[ValidateAirfieldScale]   PAD CENTERING: cannot be auto-checked (pad markings are " +
                  "baked into the FBX mesh). Open AirfieldPrefab, eye each Slot_* gizmo against the " +
                  "yellow pad below it, drag onto the pad if off, then run Report Airfield Slot Positions.");
    }

    private static void ReportRunwayAndLanding(GameObject root)
    {
        for (int i = 0; i < RunwayPoints.Length; i++)
        {
            NamedPoint p = RunwayPoints[i];
            Transform t = root.transform.Find(p.Name);
            if (t == null) { Debug.LogWarning($"[ValidateAirfieldScale]   ⚠ '{p.Name}' missing."); continue; }
            Debug.Log($"[AirfieldSlots]   {p.Name} localPos = {t.localPosition:F1}.");
        }

        // Centerline check.
        Transform sA = root.transform.Find("TakeoffStart_A");
        Transform sB = root.transform.Find("TakeoffStart_B");
        if (sA != null && Mathf.Abs(sA.localPosition.x) > TakeoffCenterTolerance)
            Debug.LogError($"[ValidateAirfieldScale] ✗ TakeoffStart_A.x = {sA.localPosition.x:F2} (limit ±{TakeoffCenterTolerance}).");
        if (sB != null && Mathf.Abs(sB.localPosition.x) > TakeoffCenterTolerance)
            Debug.LogError($"[ValidateAirfieldScale] ✗ TakeoffStart_B.x = {sB.localPosition.x:F2} (limit ±{TakeoffCenterTolerance}).");
    }

    private static void ReportComparisonHint()
    {
        // Same-order-of-magnitude comparison so the user can eyeball this
        // against other prefabs without opening Inspector for each.
        Debug.Log($"[ValidateAirfieldScale]   Comparison ballpark — Tank/Soldier ≈ 3–4 m, " +
                  "Barracks / PowerPlant ≈ 4–6 m, VehicleFactory ≈ 6–8 m, CommandCenter ≈ 8–10 m, " +
                  $"AIRFIELD target ≈ {TargetVisibleSize} m. The airport is intentionally the LARGEST " +
                  "non-superweapon building.");
    }

    // ================================================================== //

    private static GameObject LoadPrefab()
    {
        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (asset == null)
        {
            Debug.LogError($"[NormalizeAirfield] ✗ Prefab not found at '{PrefabPath}'.");
            return null;
        }
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
            Debug.LogError($"[NormalizeAirfield] ✗ Failed to load prefab contents for '{PrefabPath}'.");
        return root;
    }
}
