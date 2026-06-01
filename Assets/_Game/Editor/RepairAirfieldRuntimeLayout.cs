using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Final pass on the new-FBX Airfield prefab. Three responsibilities:
///   1. Reposition the 6 aircraft <see cref="Airfield.slots"/> and the 6
///      matching <see cref="Airfield.taxiPoints"/> into a clean 4+2 layout
///      around the new model — 4 slots on the east apron, 2 on the west,
///      every slot rotated so the parked nose faces the runway.
///   2. Hide the two leftover visible "gray squares" (LaneA_GoAround /
///      LaneB_Link). These are real <see cref="Airfield.laneATaxiPoints"/>
///      / <see cref="Airfield.laneBTaxiPoints"/> waypoints, so we ONLY
///      disable their MeshRenderer — the Transforms stay in place so the
///      taxi route is unchanged.
///   3. Wire the airfield root's existing <see cref="TeamColorMarker"/> to
///      paint EMISSION (not base color) on every renderer inside
///      <c>Visual_NewFbx</c>. The blue glow on the FBX is driven by
///      hardcoded <c>_EmissionColor</c> in the imported materials; an
///      emission-only paint via MaterialPropertyBlock makes it follow the
///      owner's team color while leaving the dark base materials alone.
///
/// What this tool deliberately does NOT do:
///   • Replace, recompile, or modify any gameplay script
///     (Airfield, Building, SelectableBuilding, PowerConsumer, Health,
///     GameEntity, AirUnitController).
///   • Touch the runway / takeoff / landing markers — those already
///     match the new FBX runway.
///   • Move <see cref="Airfield.laneATaxiPoints"/> / laneB waypoints.
///   • Re-bake the BoxCollider or the root scale.
///   • Re-activate <c>OldVisual_Backup</c>.
///   • Re-route or rewrite the aircraft state machine.
///
/// Re-run safe: every step finds existing children by name and rewrites
/// only what it needs to.
///
/// Menus:
///   Tools → RTS → Buildings → Repair Airfield Layout
///   Tools → RTS → Buildings → Validate Airfield Layout
/// </summary>
public static class RepairAirfieldRuntimeLayout
{
    private const string PrefabPath        = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";
    private const string VisualChildName   = "Visual_NewFbx";

    // Gray squares the user reported — visible flat plates that are also
    // gameplay waypoints. We disable their MeshRenderer but KEEP the
    // Transform so the taxi route the Airfield script computes still
    // walks through them.
    private static readonly string[] HideRenderersOn =
    {
        "LaneA_GoAround",
        "LaneB_Link",
    };

    // Visible-design pass — the prior subtle tweaks didn't read in-game.
    // This Repair pass now performs an OBVIOUS enlargement of the airfield:
    //   • Visual_NewFbx.localScale  : (1,1,1)  →  (1.5, 1.5, 1.5)
    //   • Root BoxCollider size     : (20,2,28) → (30, 2, 42)
    //   • Slots / taxi / runway / landing transforms repositioned to fit
    //     the new larger visual, so the parked jets clearly sit ON the
    //     enlarged apron and the takeoff lanes hug the centerline of the
    //     wider runway strip.
    //
    // Why 1.5× and not bigger: the documented freeze threshold was at
    // ×10 (cascading with internal FBX child scales). 1.5× is well below
    // that — the safety check at the bottom of Repair re-verifies that no
    // single renderer in Visual_NewFbx exceeds the building envelope ×2
    // after scaling, and aborts otherwise.
    //
    // Y-rotation 270° = nose toward -X (parked facing west); 90° = nose
    // toward +X (parked facing east). The runway lies along X≈0 between
    // Z≈-15 and Z≈+18, so every slot's nose points toward the runway
    // centerline before the jet pulls out.
    private const float VisualScale = 1.5f;
    private static readonly Vector3 NewColliderSize   = new Vector3(30f, 2f, 42f);
    private static readonly Vector3 NewColliderCenter = new Vector3( 0f, 1f,  0f);

    // 4 + 2 layout — slots ±10 m from the airfield centerline (well inside
    // the new 30 m-wide collider) so the parked jets visibly belong to the
    // enlarged apron. Z spread is 18 m on the east apron (4 slots × 6 m
    // spacing) so the jets aren't tightly packed.
    private struct SlotLayout
    {
        public int     Index;
        public Vector3 Slot;
        public Vector3 Taxi;
        public float   RotationY;
    }
    private static readonly SlotLayout[] Layout =
    {
        // East apron — 4 slots, indices 0..3 (even prefer Lane A, odd Lane B)
        new SlotLayout { Index = 0, Slot = new Vector3( 10f, 0f, -9f), Taxi = new Vector3( 7f, 0f, -9f), RotationY = 270f },
        new SlotLayout { Index = 1, Slot = new Vector3( 10f, 0f, -3f), Taxi = new Vector3( 7f, 0f, -3f), RotationY = 270f },
        new SlotLayout { Index = 2, Slot = new Vector3( 10f, 0f,  3f), Taxi = new Vector3( 7f, 0f,  3f), RotationY = 270f },
        new SlotLayout { Index = 3, Slot = new Vector3( 10f, 0f,  9f), Taxi = new Vector3( 7f, 0f,  9f), RotationY = 270f },
        // West apron — 2 slots, indices 4..5 (4 → Lane A, 5 → Lane B)
        new SlotLayout { Index = 4, Slot = new Vector3(-10f, 0f, -3f), Taxi = new Vector3(-7f, 0f, -3f), RotationY =  90f },
        new SlotLayout { Index = 5, Slot = new Vector3(-10f, 0f,  3f), Taxi = new Vector3(-7f, 0f,  3f), RotationY =  90f },
    };

    // Runway / lane corridor / landing positions, named the same as the
    // child Transforms on AirfieldPrefab. The two lanes used to sit at
    // X=±2 — the user reported the takeoff start "is not aligned correctly
    // with the central runway/road strip in the middle of the airfield".
    // Pulled them to X=±1.5 so a paired takeoff stays visually centered on
    // the FBX's runway strip while the two jets still have 3 m of lateral
    // separation (enough to render side-by-side without overlap).
    //
    // The Z coordinates (runway runs Z=-10..+12, approach at Z=+16) are
    // unchanged — those already match the FBX runway's actual extent.
    //
    // landingExitA was at X=+3 (east-only world the old prefab assumed).
    // Now centred at X=0 so jets returning to a west-side slot don't
    // have to taxi all the way across the runway first.
    private struct NamedPoint { public string Name; public Vector3 Pos; public float RotY; }
    private static readonly NamedPoint[] RunwayPoints =
    {
        // Lane A — slight west of centerline, scaled to the enlarged runway extent
        new NamedPoint { Name = "RunwayQueuePoint_A", Pos = new Vector3(-3f,   0f, -15f), RotY = 0f },
        new NamedPoint { Name = "TakeoffStart_A",     Pos = new Vector3(-1.5f, 0f, -15f), RotY = 0f },
        new NamedPoint { Name = "TakeoffEnd_A",       Pos = new Vector3(-1.5f, 0f,  18f), RotY = 0f },

        // Lane B — slight east of centerline
        new NamedPoint { Name = "RunwayQueuePoint_B", Pos = new Vector3( 3f,   0f, -15f), RotY = 0f },
        new NamedPoint { Name = "TakeoffStart_B",     Pos = new Vector3( 1.5f, 0f, -15f), RotY = 0f },
        new NamedPoint { Name = "TakeoffEnd_B",       Pos = new Vector3( 1.5f, 0f,  18f), RotY = 0f },

        // Lane corridor waypoints (used by Airfield.laneATaxiPoints / laneBTaxiPoints).
        // Sit between the per-slot Taxi and the RunwayQueuePoint so the route stays
        // clear of the parking apron. LaneA_GoAround / LaneB_Link keep their renderer
        // disabled (handled separately) — only the Transform is used now.
        new NamedPoint { Name = "TaxiPoint_A_Mid",    Pos = new Vector3(-3f,   0f, -12f),  RotY = 0f },
        new NamedPoint { Name = "TaxiPoint_B_Mid",    Pos = new Vector3( 3f,   0f, -12f),  RotY = 0f },
        new NamedPoint { Name = "LaneA_GoAround",     Pos = new Vector3(-3f,   0.05f, -16f), RotY = 0f },
        new NamedPoint { Name = "LaneB_Link",         Pos = new Vector3( 3f,   0.05f, -15f), RotY = 0f },

        // Landing — Lane A is the primary; Lane B is reserved (kept consistent).
        new NamedPoint { Name = "LandingApproachPoint", Pos = new Vector3( 0f,    0f,  24f), RotY = 0f },
        new NamedPoint { Name = "LandingStart_A",       Pos = new Vector3(-1.5f, 0f,  18f), RotY = 0f },
        new NamedPoint { Name = "LandingEnd_A",         Pos = new Vector3(-1.5f, 0f, -15f), RotY = 0f },
        new NamedPoint { Name = "LandingExit_A",        Pos = new Vector3( 0f,    0f, -16f), RotY = 0f },
        new NamedPoint { Name = "LandingStart_B",       Pos = new Vector3( 1.5f, 0f,  18f), RotY = 0f },
        new NamedPoint { Name = "LandingEnd_B",         Pos = new Vector3( 1.5f, 0f, -15f), RotY = 0f },
        new NamedPoint { Name = "LandingExit_B",        Pos = new Vector3( 0f,    0f, -16f), RotY = 0f },
    };

    // Validation bounds — slot must sit comfortably inside the new (30 × 42)
    // collider. ±13 X / ±19 Z leaves a ~2 m safety margin on every edge so a
    // 3 m-wingspan jet sitting at the slot doesn't overlap the collider boundary.
    private const float SlotMaxAbsX = 13f;
    private const float SlotMaxAbsZ = 19f;

    // After scaling, every renderer in Visual_NewFbx must stay under
    // (envelope × this factor) or we abort with an error — that's the
    // safety net against re-triggering the "giant wall" cascade if the FBX
    // happens to have an internal child at extreme local scale.
    private const float EnvelopeX = 35f;
    private const float EnvelopeY =  8f;
    private const float EnvelopeZ = 45f;
    private const float SafetyEnvelopeFactor = 2f;

    // Centerline tolerance — TakeoffStart_A / TakeoffStart_B should each
    // be no further than this from world X = 0. The FBX runway strip is
    // ~4 m wide; ±2 keeps both lanes on the visible strip.
    private const float TakeoffCenterTolerance = 2f;

    // ================================================================== //
    // Repair
    // ================================================================== //

    [MenuItem("Tools/RTS/Buildings/Repair Airfield Layout")]
    public static void Repair()
    {
        Debug.Log("[RepairAirfieldLayout/B] ─── Repairing airfield runtime layout ───");

        GameObject root = LoadPrefab();
        if (root == null) return;

        try
        {
            // ---- 1. Visible enlargement of the airport ---------------- //
            // Order matters: scale FIRST so renderer-bounds safety check has
            // accurate world-space numbers; resize collider next; THEN move
            // the gameplay anchors that depend on the new envelope.
            bool   scaleOK     = EnlargeVisualNewFbx(root);
            int    colliderUp  = GrowRootBoxCollider(root);

            // ---- 2. Reposition gameplay anchors ----------------------- //
            int repositioned = RepositionSlotsAndTaxiPoints(root);
            int runwayMoved  = RepositionRunwayAndLandingPoints(root);

            // ---- 3. Final cleanup ------------------------------------- //
            int hidden       = HideGraySquareRenderers(root);
            int wired        = WireTeamColorEmission(root);

            // ---- 4. Safety: no oversized renderers after scaling ------ //
            int oversized    = AbortIfOversizedRenderers(root);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[RepairAirfieldLayout/B] ✓ Enlarged Visual_NewFbx ({(scaleOK ? "OK" : "skipped")}), " +
                      $"resized collider ({(colliderUp > 0 ? "changed" : "unchanged")}), " +
                      $"repositioned {repositioned} slot/taxi pair(s), moved {runwayMoved} runway/lane/landing point(s), " +
                      $"hid {hidden} gray-square renderer(s), wired team-color emission on {wired} FBX renderer(s), " +
                      $"oversized renderers in Visual_NewFbx after enlargement: {oversized}. " +
                      "Run Tools → RTS → Buildings → Validate Airfield Layout to confirm.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ================================================================== //
    // Validate
    // ================================================================== //

    [MenuItem("Tools/RTS/Buildings/Validate Airfield Layout")]
    public static void Validate()
    {
        Debug.Log("[ValidateAirfieldLayout/B] ─── Layout audit ───");

        GameObject root = LoadPrefab();
        if (root == null) return;

        try
        {
            Debug.Log($"[ValidateAirfieldLayout/B] Prefab: {PrefabPath}");

            Airfield af = root.GetComponent<Airfield>();
            if (af == null)
            {
                Debug.LogError("[ValidateAirfieldLayout/B] ✗ AirfieldPrefab has no Airfield script.");
                return;
            }
            Debug.Log("[ValidateAirfieldLayout/B] Airfield script present ✓");

            ReportVisualAndCollider(root);
            ReportSlotsAndTaxi(af);
            ReportRunwayAndLanding(af);
            ReportGraySquares(root);
            ReportTeamColorMarker(root);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[ValidateAirfieldLayout/B] ─── End of audit ───");
    }

    // ================================================================== //
    // Repair steps
    // ================================================================== //

    /// <summary>
    /// Sets <c>Visual_NewFbx.localScale</c> to (1.5, 1.5, 1.5) — the central
    /// visible change in this Repair pass. This is well under the documented
    /// freeze threshold (×10), and <see cref="AbortIfOversizedRenderers"/>
    /// re-validates the renderer bounds after the prefab is saved.
    /// </summary>
    private static bool EnlargeVisualNewFbx(GameObject root)
    {
        Transform vis = root.transform.Find(VisualChildName);
        if (vis == null)
        {
            Debug.LogError($"[RepairAirfieldLayout/B] ✗ No '{VisualChildName}' child — run " +
                           "Tools → RTS → Buildings → Replace Airport Visual With New FBX first.");
            return false;
        }
        Vector3 before = vis.localScale;
        Vector3 after  = new Vector3(VisualScale, VisualScale, VisualScale);
        vis.localPosition = Vector3.zero;
        vis.localRotation = Quaternion.identity;
        vis.localScale    = after;
        Debug.Log($"[AirfieldVisual] Visual_NewFbx.localScale {before} → {after}. " +
                  $"Visible airport size grows by ~{(VisualScale - 1f) * 100f:F0}% in every axis.");
        return true;
    }

    /// <summary>
    /// Grows the root BoxCollider to (30, 2, 42) so the new larger visual
    /// stays inside the placement / selection footprint. Center is parked
    /// at (0, 1, 0) to match. Idempotent — already-correct values produce
    /// no change.
    /// </summary>
    private static int GrowRootBoxCollider(GameObject root)
    {
        BoxCollider bc = root.GetComponent<BoxCollider>();
        if (bc == null)
        {
            bc = root.AddComponent<BoxCollider>();
            bc.size   = NewColliderSize;
            bc.center = NewColliderCenter;
            Debug.LogWarning($"[AirfieldVisual] Root BoxCollider was missing — recreated " +
                             $"with size={NewColliderSize}, center={NewColliderCenter}.");
            return 1;
        }
        int changes = 0;
        Vector3 sizeBefore   = bc.size;
        Vector3 centerBefore = bc.center;
        if (bc.size != NewColliderSize)
        {
            bc.size = NewColliderSize;
            changes++;
        }
        if (bc.center != NewColliderCenter)
        {
            bc.center = NewColliderCenter;
            changes++;
        }
        if (changes > 0)
            Debug.Log($"[AirfieldVisual] Root BoxCollider: size {sizeBefore} → {NewColliderSize}, " +
                      $"center {centerBefore} → {NewColliderCenter}. " +
                      "Placement / selection footprint now matches the enlarged visual.");
        else
            Debug.Log($"[AirfieldVisual] Root BoxCollider already at target {NewColliderSize} / {NewColliderCenter}.");
        return changes;
    }

    /// <summary>
    /// After scaling, walk every renderer inside Visual_NewFbx and ensure
    /// no single mesh ended up larger than the safety envelope. If one
    /// does, we DON'T silently disable it (that would risk silently
    /// breaking the visual) — we log a hard error and let the user roll
    /// back via Revert Airfield To Old Visual. This is the same guard
    /// FixAirfieldVisualSize uses.
    /// </summary>
    private static int AbortIfOversizedRenderers(GameObject root)
    {
        Transform vis = root.transform.Find(VisualChildName);
        if (vis == null) return 0;
        int oversized = 0;
        Renderer[] rs = vis.GetComponentsInChildren<Renderer>(includeInactive: false);
        for (int i = 0; i < rs.Length; i++)
        {
            Renderer r = rs[i];
            if (r == null) continue;
            Vector3 sz = r.bounds.size;
            bool tooBig = sz.x > EnvelopeX * SafetyEnvelopeFactor
                       || sz.y > EnvelopeY * SafetyEnvelopeFactor
                       || sz.z > EnvelopeZ * SafetyEnvelopeFactor;
            if (!tooBig) continue;
            oversized++;
            Debug.LogError($"[AirfieldVisual] ✗ AFTER ENLARGE: renderer '{r.name}' bounds size {sz} " +
                           $"exceeds envelope × {SafetyEnvelopeFactor:F1}. This is the giant-wall warning. " +
                           "Roll back via Tools → RTS → Buildings → Revert Airfield To Old Visual, then " +
                           "lower VisualScale in RepairAirfieldRuntimeLayout.cs.");
        }
        return oversized;
    }

    private static int RepositionSlotsAndTaxiPoints(GameObject root)
    {
        int done = 0;
        for (int i = 0; i < Layout.Length; i++)
        {
            SlotLayout L = Layout[i];

            Transform slot = root.transform.Find($"Slot_{L.Index}");
            Transform taxi = root.transform.Find($"Taxi_{L.Index}");
            if (slot == null || taxi == null)
            {
                Debug.LogError($"[RepairAirfieldLayout/B] ✗ Missing Slot_{L.Index} or Taxi_{L.Index}. " +
                               "The Airfield prefab is incomplete — re-run Tools → RTS → Air System → " +
                               "Repair Airfield Layout (legacy) to recreate them first.");
                continue;
            }

            Vector3 oldSlot = slot.localPosition;
            Vector3 oldTaxi = taxi.localPosition;

            slot.localPosition    = L.Slot;
            slot.localEulerAngles = new Vector3(0f, L.RotationY, 0f);
            taxi.localPosition    = L.Taxi;
            taxi.localEulerAngles = new Vector3(0f, L.RotationY, 0f);

            Debug.Log($"[AirfieldSlots] Slot {L.Index}: localPos {oldSlot} → {L.Slot}, rotY={L.RotationY:F0}°. " +
                      $"Taxi {L.Index}: localPos {oldTaxi} → {L.Taxi}.");
            done++;
        }
        Debug.Log($"[AirfieldSlots] Repositioned {done}/{Layout.Length} slot/taxi pairs into 4+2 layout " +
                  "(4 east apron at X=+9, 2 west apron at X=-9).");
        return done;
    }

    /// <summary>
    /// Snaps the runway lane markers, lane corridor mid points, and landing
    /// markers to the positions in <see cref="RunwayPoints"/>. Keeps the
    /// same Transforms (so Airfield's serialized references stay valid) —
    /// only their localPosition changes. Re-runnable: every call rewrites
    /// the same destination values, so it's idempotent.
    ///
    /// Why this matters: the takeoff start used to sit at X=±2, which on
    /// the new FBX puts each lane visibly OFF the central runway strip.
    /// X=±1.5 keeps both lanes inside the strip while preserving the 3 m
    /// lateral separation paired takeoffs need to render two jets clearly.
    /// </summary>
    private static int RepositionRunwayAndLandingPoints(GameObject root)
    {
        int done = 0;
        for (int i = 0; i < RunwayPoints.Length; i++)
        {
            NamedPoint p = RunwayPoints[i];
            Transform t = root.transform.Find(p.Name);
            if (t == null)
            {
                Debug.LogWarning($"[AirfieldSlots] '{p.Name}' not found on AirfieldPrefab — skipping. " +
                                 "If a runway / lane / landing waypoint is missing, the Airfield's serialized " +
                                 "reference is broken; rebuild via Tools → RTS → Air System → Repair Airfield Setup.");
                continue;
            }
            Vector3 oldPos = t.localPosition;
            t.localPosition = p.Pos;
            // Lane / landing points use identity rotation by design (only Slots have rotY).
            t.localEulerAngles = new Vector3(0f, p.RotY, 0f);
            Debug.Log($"[AirfieldSlots] {p.Name}: localPos {oldPos} → {p.Pos}.");
            done++;
        }
        Debug.Log($"[AirfieldSlots] Moved {done}/{RunwayPoints.Length} runway / lane / landing point(s). " +
                  "Takeoff lanes now at X=±1.5 (centered on runway strip), landing exit at X=0 " +
                  "(centered so jets can return to east OR west slots cleanly).");
        return done;
    }

    private static int HideGraySquareRenderers(GameObject root)
    {
        int hidden = 0;
        for (int i = 0; i < HideRenderersOn.Length; i++)
        {
            string n = HideRenderersOn[i];
            Transform t = root.transform.Find(n);
            if (t == null)
            {
                Debug.Log($"[AirfieldCleanup] '{n}' not found — nothing to hide.");
                continue;
            }
            MeshRenderer mr = t.GetComponent<MeshRenderer>();
            if (mr == null)
            {
                Debug.Log($"[AirfieldCleanup] '{n}' has no MeshRenderer — already invisible.");
                continue;
            }
            if (!mr.enabled)
            {
                Debug.Log($"[AirfieldCleanup] '{n}' MeshRenderer already disabled — no change.");
                continue;
            }
            mr.enabled = false;
            hidden++;
            Debug.Log($"[AirfieldCleanup] Hidden visible marker: '{n}' MeshRenderer.enabled = false. " +
                      "Kept Transform — Airfield.laneTaxiPoints still walks through it.");
        }
        return hidden;
    }

    /// <summary>
    /// Replaces the airfield root's <see cref="TeamColorMarker.bodyColorRenderers"/>
    /// (currently pointing at the old Hangar/Tower renderers that now live
    /// inside the inactive OldVisual_Backup) with every renderer inside
    /// <c>Visual_NewFbx</c>. Flips the marker into emission-only mode so
    /// the dark base materials are preserved — only renderers with their
    /// <c>_EMISSION</c> keyword enabled (i.e. the actual glowing parts)
    /// change colour at runtime. This is the path that drives owner-aware
    /// team color through the existing MultiplayerColors / PlayerFactionManager
    /// pipeline, with no new script and no new component.
    /// </summary>
    private static int WireTeamColorEmission(GameObject root)
    {
        TeamColorMarker marker = root.GetComponent<TeamColorMarker>();
        if (marker == null)
        {
            Debug.LogWarning("[TeamColor] AirfieldPrefab has no TeamColorMarker — adding one. " +
                             "(Will not affect production gameplay; it's purely visual.)");
            marker = root.AddComponent<TeamColorMarker>();
        }

        Transform vis = root.transform.Find(VisualChildName);
        if (vis == null)
        {
            Debug.LogError($"[TeamColor] ✗ No '{VisualChildName}' child — run Tools → RTS → Buildings → " +
                           "Replace Airport Visual With New FBX (and Fix Airfield Visual Size) first.");
            return 0;
        }

        Renderer[] rends = vis.GetComponentsInChildren<Renderer>(includeInactive: false);
        List<Renderer> wired = new List<Renderer>(rends.Length);
        for (int i = 0; i < rends.Length; i++)
        {
            Renderer r = rends[i];
            if (r == null) continue;
            wired.Add(r);
        }

        marker.bodyColorRenderers = wired;
        marker.applyToBaseColor   = false;   // keep dark/metal base materials untouched
        marker.applyToEmission    = true;    // ONLY the team-colored glow follows the owner
        if (marker.emissionIntensity <= 0f) marker.emissionIntensity = 1f;
        EditorUtility.SetDirty(marker);

        Debug.Log($"[TeamColor] Airfield TeamColorMarker wired to {wired.Count} FBX renderer(s). " +
                  $"applyToBaseColor=false, applyToEmission=true, emissionIntensity={marker.emissionIntensity}. " +
                  "Only materials whose _EMISSION keyword is on (the blue glow parts) will repaint to the " +
                  "owner's team color; non-emissive renderers ignore the property block silently.");
        Debug.Log("[TeamColor] At runtime, MultiplayerColors.OnColorsChanged → TeamColorMarker.RepaintFromContext → " +
                  "ApplyColor — same flow every existing unit uses. Red player ⇒ red glow, orange ⇒ orange, etc.");
        return wired.Count;
    }

    // ================================================================== //
    // Validate sections
    // ================================================================== //

    private static void ReportVisualAndCollider(GameObject root)
    {
        // Visual_NewFbx scale + combined renderer bounds (the "visible airport
        // size" the user sees on screen).
        Transform vis = root.transform.Find(VisualChildName);
        if (vis == null)
        {
            Debug.LogWarning("[ValidateAirfieldLayout/B] ⚠ Visual_NewFbx missing — no enlarged airport visual present.");
        }
        else
        {
            Renderer[] vrs = vis.GetComponentsInChildren<Renderer>(includeInactive: false);
            Bounds visB = default;
            bool found = false;
            for (int i = 0; i < vrs.Length; i++)
            {
                Renderer r = vrs[i];
                if (r == null) continue;
                if (!found) { visB = r.bounds; found = true; }
                else        { visB.Encapsulate(r.bounds); }
            }
            string sizeStr = found ? visB.size.ToString("F1") : "(no renderers)";
            Debug.Log($"[ValidateAirfieldLayout/B] Visual_NewFbx localScale = {vis.localScale} " +
                      $"(target = {new Vector3(VisualScale, VisualScale, VisualScale)}). " +
                      $"Combined visible renderer bounds size = {sizeStr}.  " +
                      $"Active renderer count = {vrs.Length}.");
            bool needsEnlarge = Mathf.Abs(vis.localScale.x - VisualScale) > 0.01f;
            if (needsEnlarge)
                Debug.LogError($"[ValidateAirfieldLayout/B] ✗ Visual_NewFbx is NOT enlarged. " +
                               "Run Tools → RTS → Buildings → Repair Airfield Layout to grow it to " +
                               $"{VisualScale}×.");
        }

        // Root BoxCollider — the placement / selection footprint.
        BoxCollider bc = root.GetComponent<BoxCollider>();
        if (bc == null)
        {
            Debug.LogError("[ValidateAirfieldLayout/B] ✗ Root BoxCollider missing.");
        }
        else
        {
            Debug.Log($"[ValidateAirfieldLayout/B] Root BoxCollider size = {bc.size} center = {bc.center} " +
                      $"(target = {NewColliderSize} / {NewColliderCenter}).");
            if (bc.size != NewColliderSize || bc.center != NewColliderCenter)
                Debug.LogError($"[ValidateAirfieldLayout/B] ✗ BoxCollider does not match the enlarged " +
                               "target — placement footprint is wrong for the new visual size. " +
                               "Run Repair Airfield Layout.");
        }
    }

    private static void ReportSlotsAndTaxi(Airfield af)
    {
        int slotCount = af.slots != null ? af.slots.Length : 0;
        int taxiCount = af.taxiPoints != null ? af.taxiPoints.Length : 0;
        Debug.Log($"[ValidateAirfieldLayout/B] Slots array length = {slotCount} (expected {Airfield.MaxSlots}). " +
                  $"TaxiPoints array length = {taxiCount} (expected {Airfield.MaxSlots}).");
        if (slotCount != Airfield.MaxSlots)
            Debug.LogError($"[ValidateAirfieldLayout/B] ✗ Slot count {slotCount} != {Airfield.MaxSlots}. " +
                           "Aircraft production will partially fail.");
        int outside = 0;
        for (int i = 0; i < slotCount; i++)
        {
            Transform s = af.slots[i];
            if (s == null) { Debug.LogError($"[ValidateAirfieldLayout/B] ✗ Slot {i} is NULL."); continue; }
            Vector3 lp = s.localPosition;
            bool oob = Mathf.Abs(lp.x) > SlotMaxAbsX || Mathf.Abs(lp.z) > SlotMaxAbsZ;
            string flag = oob ? "  ✗ OUTSIDE footprint" : "";
            if (oob) outside++;
            Debug.Log($"[AirfieldSlots] Slot {i} localPos={lp:F1} rotY={s.localEulerAngles.y:F0}° " +
                      $"world={s.position:F1}.{flag}");
        }
        if (outside > 0)
            Debug.LogError($"[ValidateAirfieldLayout/B] ✗ {outside} slot(s) sit outside the safe footprint " +
                           $"(|X|≤{SlotMaxAbsX}, |Z|≤{SlotMaxAbsZ} inside the {SlotMaxAbsX*2+2}×{SlotMaxAbsZ*2+2} " +
                           "BoxCollider). Aircraft will visually hang off the airport edge. " +
                           "Run Tools → RTS → Buildings → Repair Airfield Layout.");
        else
            Debug.Log($"[ValidateAirfieldLayout/B] All {slotCount} slot(s) sit inside the safe footprint ✓.");
        for (int i = 0; i < taxiCount; i++)
        {
            Transform t = af.taxiPoints[i];
            if (t == null) { Debug.LogError($"[ValidateAirfieldLayout/B] ✗ TaxiPoint {i} is NULL."); continue; }
            Debug.Log($"[AirfieldSlots]   Taxi {i}  localPos={t.localPosition:F1} world={t.position:F1}.");
        }
    }

    private static void ReportRunwayAndLanding(Airfield af)
    {
        Debug.Log($"[AirfieldSlots] Takeoff point A: " +
                  $"start={V(af.takeoffStartA)} end={V(af.takeoffEndA)} queue={V(af.runwayQueuePointA)}.");
        Debug.Log($"[AirfieldSlots] Takeoff point B: " +
                  $"start={V(af.takeoffStartB)} end={V(af.takeoffEndB)} queue={V(af.runwayQueuePointB)}.");
        Debug.Log($"[AirfieldSlots] Landing point  : approach={V(af.landingApproachPoint)} | " +
                  $"A start={V(af.landingStartA)} end={V(af.landingEndA)} exit={V(af.landingExitA)} | " +
                  $"B start={V(af.landingStartB)} end={V(af.landingEndB)} exit={V(af.landingExitB)}.");

        // Centerline check — the user reported the takeoff start was visibly
        // off the central runway strip. Both lanes should sit within the
        // tolerance band straddling X=0.
        CheckCenterline("TakeoffStart_A", af.takeoffStartA);
        CheckCenterline("TakeoffStart_B", af.takeoffStartB);
        CheckCenterline("TakeoffEnd_A",   af.takeoffEndA);
        CheckCenterline("TakeoffEnd_B",   af.takeoffEndB);
        CheckCenterline("LandingApproachPoint", af.landingApproachPoint);
    }

    private static void CheckCenterline(string label, Transform t)
    {
        if (t == null) return;
        float x = Mathf.Abs(t.localPosition.x);
        if (x > TakeoffCenterTolerance)
            Debug.LogError($"[AirfieldSlots] ✗ {label} is {x:F2} m off centerline (limit ±{TakeoffCenterTolerance}). " +
                           "Takeoff/landing will visibly slide off the central runway strip. " +
                           "Run Tools → RTS → Buildings → Repair Airfield Layout.");
    }

    private static void ReportGraySquares(GameObject root)
    {
        for (int i = 0; i < HideRenderersOn.Length; i++)
        {
            string n = HideRenderersOn[i];
            Transform t = root.transform.Find(n);
            if (t == null) { Debug.Log($"[AirfieldCleanup] '{n}': absent (no transform). OK."); continue; }
            MeshRenderer mr = t.GetComponent<MeshRenderer>();
            if (mr == null) { Debug.Log($"[AirfieldCleanup] '{n}': present, no MeshRenderer. OK."); continue; }
            if (mr.enabled)
                Debug.LogError($"[AirfieldCleanup] ✗ '{n}' MeshRenderer is ENABLED — that's a visible gray plate. " +
                               "Run Tools → RTS → Buildings → Repair Airfield Layout.");
            else
                Debug.Log($"[AirfieldCleanup] '{n}' MeshRenderer disabled ✓ (Transform still used by taxi route).");
        }
    }

    private static void ReportTeamColorMarker(GameObject root)
    {
        TeamColorMarker m = root.GetComponent<TeamColorMarker>();
        if (m == null)
        {
            Debug.LogWarning("[TeamColor] ⚠ No TeamColorMarker on AirfieldPrefab root — the owner color will not show.");
            return;
        }

        int wired = 0, nullSlot = 0, oldBackupRefs = 0;
        for (int i = 0; i < m.bodyColorRenderers.Count; i++)
        {
            Renderer r = m.bodyColorRenderers[i];
            if (r == null) { nullSlot++; continue; }
            wired++;
            // Detect renderer that lives inside OldVisual_Backup — the legacy
            // wiring that paints inactive primitives. Logs as warning so the
            // user knows to re-run Repair.
            Transform p = r.transform;
            while (p != null)
            {
                if (p.name == "OldVisual_Backup") { oldBackupRefs++; break; }
                p = p.parent;
            }
        }
        Debug.Log($"[TeamColor] TeamColorMarker.bodyColorRenderers = {wired} renderer(s), {nullSlot} null slot(s). " +
                  $"applyToBaseColor={m.applyToBaseColor}, applyToEmission={m.applyToEmission}, " +
                  $"emissionIntensity={m.emissionIntensity}.");
        if (oldBackupRefs > 0)
            Debug.LogError($"[TeamColor] ✗ {oldBackupRefs} renderer(s) live inside OldVisual_Backup — they will " +
                           "never paint at runtime. Re-run Repair Airfield Layout.");
        if (!m.applyToEmission)
            Debug.LogWarning("[TeamColor] ⚠ applyToEmission is OFF — the blue glow will NOT follow team color.");
        if (m.applyToBaseColor)
            Debug.LogWarning("[TeamColor] ⚠ applyToBaseColor is ON — every painted renderer's base color will be " +
                             "team-tinted (might recolour metal/grey panels). For the airfield, prefer OFF.");
    }

    // ================================================================== //
    // Helpers
    // ================================================================== //

    private static GameObject LoadPrefab()
    {
        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (asset == null)
        {
            Debug.LogError($"[RepairAirfieldLayout/B] ✗ Prefab not found at '{PrefabPath}'.");
            return null;
        }
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
            Debug.LogError($"[RepairAirfieldLayout/B] ✗ Failed to load prefab contents for '{PrefabPath}'.");
        return root;
    }

    private static string V(Transform t) => t == null ? "null" : t.position.ToString("F1");
}
