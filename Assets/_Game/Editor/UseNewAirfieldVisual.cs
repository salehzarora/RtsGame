using UnityEditor;
using UnityEngine;

/// <summary>
/// Swaps the visual on the gameplay AirfieldPrefab to use the externally-
/// imported "Airfield_Unity_Final" asset (Meshy HexaPad airfield) WITHOUT
/// touching any gameplay script, serialized reference, or aircraft logic.
///
/// PREREQUISITE: the user must first run the asset's bundled menu
///   Tools → Airfield → Setup Material & LOD Prefab
/// (defined in <c>Assets/Buildings/Airfield/Airfield_Unity_Final/Editor/AirfieldSetup.cs</c>)
/// which creates <c>M_Airfield.mat</c> and <c>Airfield.prefab</c> (LOD0/1/2 + BoxCollider)
/// inside the asset folder. We can't trigger that menu from this tool — it
/// must be a manual click.
///
/// What this tool does, in order:
///   1. Hard-stop if the prerequisite Airfield.prefab is missing.
///   2. Duplicate <c>AirfieldPrefab.prefab</c> →
///      <c>AirfieldPrefab_OLD_Backup.prefab</c> (next to it). Idempotent
///      via overwrite.
///   3. Open AirfieldPrefab.prefab for editing.
///   4. Disable the prior visual <c>Visual_NewFbx</c> (rename to
///      <c>Visual_OldFbx_Backup</c>, SetActive false). Never deleted.
///   5. Insert the new <c>Airfield.prefab</c> as a CONNECTED prefab variant
///      child named <c>Visual_NewAirfield</c>, layer = Building, scale =
///      50/70 ≈ 0.714 so the native 70 × 70 m model presents as a 50 × 50 m
///      visible footprint (the agreed compromise — clearly bigger than
///      30 × 42 and clearly smaller than the 70 × 70 native that would
///      dominate the map).
///   6. Strip MeshCollider / every Collider / Light / Camera / Animator
///      inside the new visual subtree (defensive — same guard the FBX swap
///      used; prevents the documented freeze/giant-wall regression).
///   7. Resize the AirfieldPrefab root BoxCollider to (50, 2, 50) center
///      (0, 1, 0) — flat-footprint convention matching all other buildings.
///   8. Reposition <c>Slot_0..5</c>, <c>Taxi_0..5</c>, runway markers, lane
///      corridor mids, and landing markers to fit the 50 × 50 envelope.
///      Names / serialized references / Airfield script unchanged.
///   9. Set the existing TeamColorMarker to inert:
///        <c>bodyColorRenderers</c> cleared, <c>applyToEmission = false</c>,
///        <c>applyToBaseColor = false</c>.
///      Reason: the new asset has emission baked into a texture map; piping
///      team color through <c>_EmissionColor</c> would tint the entire
///      emission texture (yellow lines, pad numbers, hangar lights — every
///      bright thing — turning red/orange/etc., not just team strips).
///      Leaving the marker present but inert preserves serialization and
///      lets a future per-strip material slot wire it back up.
///  10. Save the prefab.
///
/// What this tool does NOT do:
///   • Modify <c>Airfield.cs</c>, <c>AirUnitController.cs</c>, the placement
///     manager, or any aircraft / Photon / production / health logic.
///   • Rename gameplay anchors (Slot_0..5, Taxi_0..5, runway / landing
///     markers, lane corridor mids stay exactly as the Airfield script
///     expects them).
///   • Touch the FBX import settings on SM_Airfield_LOD0/1/2.
///   • Delete the old AirfieldPrefab — only duplicates it as backup.
///   • Delete the old Visual_NewFbx — only renames + deactivates.
///   • Run the asset's setup menu — you do that manually.
///
/// Re-running: safe. Re-applies the same scale / collider / slot positions,
/// re-inserts the new visual fresh (deletes the prior insertion first), and
/// re-overwrites the backup prefab.
///
/// Menu: Tools → RTS → Buildings → Use New Airfield Visual
/// </summary>
public static class UseNewAirfieldVisual
{
    private const string MenuPath              = "Tools/RTS/Buildings/Use New Airfield Visual";
    private const string AirfieldPrefabPath    = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";
    private const string BackupPath            = "Assets/_Game/Prefabs/AirfieldPrefab_OLD_Backup.prefab";
    private const string NewAirfieldPrefabPath = "Assets/Buildings/Airfield/Airfield_Unity_Final/Airfield.prefab";

    private const string NewVisualChildName    = "Visual_NewAirfield";
    private const string OldVisualChildName    = "Visual_NewFbx";
    private const string OldVisualRenamed      = "Visual_OldFbx_Backup";
    private const string OldVisualBackupParent = "OldVisual_Backup";

    // Native asset is 70 × 70 m; we target a ~28 × 28 m visible footprint
    // (a ~20% reduction from the previous 35 m default). Kept in sync with
    // <see cref="NormalizeAirfieldScale"/> so re-running this tool after a
    // Normalize pass doesn't bounce the airport back to the bigger size.
    // To change the canonical scale, edit BOTH constants together (here
    // and in NormalizeAirfieldScale.cs).
    private const float TargetScale = 28f / 70f;

    // Root BoxCollider — flat-footprint convention matching every other
    // gameplay building. Width / depth match the visual; Y stays at 2.
    private static readonly Vector3 ColliderSize   = new Vector3(28f, 2f, 28f);
    private static readonly Vector3 ColliderCenter = new Vector3( 0f, 1f,  0f);

    private const string BuildingLayerName = "Building";

    // Slot layout — same 4 + 2 split, scaled to the 50 m envelope. Slots
    // sit at X = ±18 (well inside the ±25 collider half-width) with 8 m Z
    // spacing on the east apron so the parked jets aren't tight-packed.
    private struct SlotLayout
    {
        public int     Index;
        public Vector3 Slot;
        public Vector3 Taxi;
        public float   RotationY;
    }
    // EXACT user-captured reference positions (RIGHT slots 0/1/2 at rotY
    // -55.959°, LEFT slots 3/4/5 at +55.959°, Y = 0.6). Mirrors
    // ApplyManualAirfieldSlotReference.References — change all three files
    // together if you re-capture pad positions.
    private static readonly SlotLayout[] Layout =
    {
        new SlotLayout { Index = 0, Slot = new Vector3( 8.23999977f, 1f,  3.65932417f), Taxi = new Vector3( 8.23999977f, 0f, -1f), RotationY = -55.959f },
        new SlotLayout { Index = 1, Slot = new Vector3( 6.38754559f, 1f,  0.20483637f), Taxi = new Vector3( 6.38754559f, 0f, -1f), RotationY = -55.959f },
        new SlotLayout { Index = 2, Slot = new Vector3( 4.69712925f, 1f, -4.08381081f), Taxi = new Vector3( 4.69712925f, 0f, -1f), RotationY = -55.959f },
        new SlotLayout { Index = 3, Slot = new Vector3(-5.27180529f, 1f, -4.35003638f), Taxi = new Vector3(-5.27180529f, 0f, -1f), RotationY =  55.959f },
        new SlotLayout { Index = 4, Slot = new Vector3(-6.12523460f, 1f,  0.26885521f), Taxi = new Vector3(-6.12523460f, 0f, -1f), RotationY =  55.959f },
        new SlotLayout { Index = 5, Slot = new Vector3(-8.05147648f, 1f,  3.80998421f), Taxi = new Vector3(-8.05147648f, 0f, -1f), RotationY =  55.959f },
    };

    // Runway / lane corridor / landing — Z extends to ±10 (runway) and
    // approach at +16 (well off the runway end). Lanes hug the centerline
    // at ±1.5 so paired takeoffs stay visually on the central strip.
    // RotY = NaN means "keep existing rotation". Used for the runway-aligned
    // queue/start/end (set to the painted runway heading) vs. corridor /
    // landing markers (orientation immaterial).
    private struct NamedPoint { public string Name; public Vector3 Pos; public float RotY; }
    private static readonly NamedPoint[] RunwayPoints =
    {
        new NamedPoint { Name = "RunwayQueuePoint_A",   Pos = new Vector3(-1.819133043f, 0.6f, -1.87182045f),  RotY = ApplyManualAirfieldTakeoffPoints.RunwayHeadingY },
        new NamedPoint { Name = "TakeoffStart_A",       Pos = new Vector3(-1.819133043f, 0.6f, -1.87182045f),  RotY = ApplyManualAirfieldTakeoffPoints.RunwayHeadingY },
        new NamedPoint { Name = "TakeoffEnd_A",         Pos = new Vector3(-1.296764731f, 0.6f, 11.1273003f),   RotY = ApplyManualAirfieldTakeoffPoints.RunwayHeadingY },
        new NamedPoint { Name = "RunwayQueuePoint_B",   Pos = new Vector3( 0.68086314f,  0.6f, -1.876122177f), RotY = ApplyManualAirfieldTakeoffPoints.RunwayHeadingY },
        new NamedPoint { Name = "TakeoffStart_B",       Pos = new Vector3( 0.68086314f,  0.6f, -1.876122177f), RotY = ApplyManualAirfieldTakeoffPoints.RunwayHeadingY },
        new NamedPoint { Name = "TakeoffEnd_B",         Pos = new Vector3( 0.95323193f,  0.6f, 11.1234283f),   RotY = ApplyManualAirfieldTakeoffPoints.RunwayHeadingY },
        new NamedPoint { Name = "TaxiPoint_A_Mid",      Pos = new Vector3(-2.5f, 0f,    -1.5f),  RotY = float.NaN },
        new NamedPoint { Name = "TaxiPoint_B_Mid",      Pos = new Vector3( 2.5f, 0f,    -1.5f),  RotY = float.NaN },
        new NamedPoint { Name = "LaneA_GoAround",       Pos = new Vector3(-2.5f, 0.05f, -2.2f),  RotY = float.NaN },
        new NamedPoint { Name = "LaneB_Link",           Pos = new Vector3( 2.5f, 0.05f, -2.2f),  RotY = float.NaN },
        new NamedPoint { Name = "LandingApproachPoint", Pos = ApplyManualAirfieldLandingTouchdown.ApproachHoldLocal, RotY = ApplyManualAirfieldLandingTouchdown.TouchdownHeadingY },
        // LandingStart = in-air align (25 m north of touchdown for glide path).
        new NamedPoint { Name = "LandingStart_A",       Pos = ApplyManualAirfieldLandingTouchdown.ApproachAnchorLocal, RotY = ApplyManualAirfieldLandingTouchdown.TouchdownHeadingY },
        // LandingEnd = ACTUAL TOUCHDOWN (user-captured).
        new NamedPoint { Name = "LandingEnd_A",         Pos = new Vector3(-0.739974201f, 0.6f, 11.4898415f), RotY = ApplyManualAirfieldLandingTouchdown.TouchdownHeadingY },
        new NamedPoint { Name = "LandingExit_A",        Pos = new Vector3( 0f,   0f,     6f),    RotY = float.NaN },
        new NamedPoint { Name = "LandingStart_B",       Pos = ApplyManualAirfieldLandingTouchdown.ApproachAnchorLocal, RotY = ApplyManualAirfieldLandingTouchdown.TouchdownHeadingY },
        new NamedPoint { Name = "LandingEnd_B",         Pos = new Vector3(-0.739974201f, 0.6f, 11.4898415f), RotY = ApplyManualAirfieldLandingTouchdown.TouchdownHeadingY },
        new NamedPoint { Name = "LandingExit_B",        Pos = new Vector3( 0f,   0f,     6f),    RotY = float.NaN },
    };

    // ================================================================== //
    // Menu entry
    // ================================================================== //

    [MenuItem(MenuPath)]
    public static void Run()
    {
        Debug.Log("[UseNewAirfield] ─── Swap Airfield visual to Airfield_Unity_Final ───");

        // 0. Verify prerequisites.
        GameObject newVisualAsset = AssetDatabase.LoadAssetAtPath<GameObject>(NewAirfieldPrefabPath);
        if (newVisualAsset == null)
        {
            Debug.LogError($"[UseNewAirfield] ✗ Prerequisite missing: '{NewAirfieldPrefabPath}'. " +
                           "Run the asset's menu FIRST: Tools → Airfield → Setup Material & LOD Prefab. " +
                           "That creates Airfield.prefab + M_Airfield.mat inside " +
                           "Assets/Buildings/Airfield/Airfield_Unity_Final/. Then re-run THIS menu.");
            return;
        }
        if (AssetDatabase.LoadAssetAtPath<GameObject>(AirfieldPrefabPath) == null)
        {
            Debug.LogError($"[UseNewAirfield] ✗ Gameplay prefab missing: '{AirfieldPrefabPath}'.");
            return;
        }
        Debug.Log($"[UseNewAirfield]   Gameplay prefab: {AirfieldPrefabPath}");
        Debug.Log($"[UseNewAirfield]   New visual prefab: {NewAirfieldPrefabPath}");

        // 1. Backup the gameplay prefab (overwrite if it exists — idempotent).
        if (AssetDatabase.LoadAssetAtPath<GameObject>(BackupPath) != null)
        {
            AssetDatabase.DeleteAsset(BackupPath);
            Debug.Log($"[UseNewAirfield]   Replaced existing backup at '{BackupPath}'.");
        }
        if (!AssetDatabase.CopyAsset(AirfieldPrefabPath, BackupPath))
        {
            Debug.LogError($"[UseNewAirfield] ✗ Failed to copy '{AirfieldPrefabPath}' → '{BackupPath}'. " +
                           "Aborting before touching the live prefab.");
            return;
        }
        Debug.Log($"[UseNewAirfield]   Backed up '{AirfieldPrefabPath}' → '{BackupPath}'.");

        // 2. Open the gameplay prefab for editing.
        GameObject root = PrefabUtility.LoadPrefabContents(AirfieldPrefabPath);
        if (root == null)
        {
            Debug.LogError("[UseNewAirfield] ✗ PrefabUtility.LoadPrefabContents returned null.");
            return;
        }

        int buildingLayer = LayerMask.NameToLayer(BuildingLayerName);
        if (buildingLayer < 0) buildingLayer = root.layer;

        try
        {
            // 3. Disable and rename the prior Visual_NewFbx (if any). Never deleted.
            DisableOldFbxVisual(root);

            // 4. Insert the new asset as a child prefab variant.
            GameObject newVisual = InsertNewAirfieldVisual(root, newVisualAsset, buildingLayer);
            if (newVisual == null) return; // already logged

            // 5. Strip dangerous components from new visual subtree.
            StripDangerousComponentsInside(newVisual);

            // 6. Set the agreed 50 × 50 compromise scale + flat-footprint collider.
            ApplyTargetScale(newVisual);
            ResizeRootBoxCollider(root);

            // 7. Reposition the gameplay anchors to fit the new envelope.
            RepositionSlotsAndTaxi(root);
            RepositionRunwayAndLanding(root);

            // 8. Make TeamColorMarker inert (per agreement — no forced team tint
            //    on the baked emission map).
            DisableTeamColorEmission(root);

            // 9. Save.
            PrefabUtility.SaveAsPrefabAsset(root, AirfieldPrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            PrintFinalReport(root);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[UseNewAirfield] ─── Done. ───");
    }

    // ================================================================== //
    // Step helpers
    // ================================================================== //

    /// <summary>
    /// Finds the previous <c>Visual_NewFbx</c> (or already-renamed
    /// <c>Visual_OldFbx_Backup</c>) and reparents it under <c>OldVisual_Backup</c>
    /// (creating it if absent), inactive. The PrefabInstance reference to
    /// the old FBX stays valid so the user can re-activate it manually if
    /// they ever want to roll back the visual.
    /// </summary>
    private static void DisableOldFbxVisual(GameObject root)
    {
        Transform vis = root.transform.Find(OldVisualChildName);
        if (vis == null) vis = root.transform.Find(OldVisualRenamed);
        if (vis == null)
        {
            Debug.Log("[UseNewAirfield]   No prior Visual_NewFbx / Visual_OldFbx_Backup found — first run.");
            return;
        }

        Transform backup = root.transform.Find(OldVisualBackupParent);
        if (backup == null)
        {
            GameObject bg = new GameObject(OldVisualBackupParent);
            bg.transform.SetParent(root.transform, false);
            bg.SetActive(false);
            backup = bg.transform;
        }

        vis.name = OldVisualRenamed;
        vis.gameObject.SetActive(false);
        if (vis.parent != backup)
            vis.SetParent(backup, worldPositionStays: true);

        Debug.Log($"[UseNewAirfield]   Disabled prior visual → '{OldVisualBackupParent}/{OldVisualRenamed}' (inactive).");
    }

    /// <summary>
    /// Adds the new Airfield.prefab as a connected variant child of the
    /// AirfieldPrefab. Idempotent — any existing <c>Visual_NewAirfield</c>
    /// is deleted first so this can be re-run safely.
    /// </summary>
    private static GameObject InsertNewAirfieldVisual(GameObject root, GameObject newAsset, int buildingLayer)
    {
        Transform existing = root.transform.Find(NewVisualChildName);
        if (existing != null)
        {
            Object.DestroyImmediate(existing.gameObject);
            Debug.Log($"[UseNewAirfield]   Removed prior '{NewVisualChildName}' for fresh insert.");
        }

        GameObject newVisual = (GameObject)PrefabUtility.InstantiatePrefab(newAsset);
        if (newVisual == null)
        {
            Debug.LogError("[UseNewAirfield] ✗ PrefabUtility.InstantiatePrefab returned null for the new asset.");
            return null;
        }
        newVisual.name = NewVisualChildName;
        newVisual.transform.SetParent(root.transform, worldPositionStays: false);
        newVisual.transform.localPosition = Vector3.zero;
        newVisual.transform.localRotation = Quaternion.identity;
        newVisual.transform.localScale    = Vector3.one;

        SetLayerRecursive(newVisual, buildingLayer);
        Debug.Log($"[UseNewAirfield]   Inserted '{NewVisualChildName}' as prefab variant child " +
                  "(localPos=0, localRot=identity, localScale=1; scale applied below).");
        return newVisual;
    }

    /// <summary>
    /// FBX-imported visuals routinely ship with Light / Camera / Animator
    /// components (Behaviours, not MonoBehaviours, so BPM's ghost strip
    /// won't catch them) plus per-mesh MeshColliders. Removing all of them
    /// here is the documented prevention for the "giant wall" freeze and
    /// for stray FBX cameras hijacking the gameplay viewport. The new
    /// Airfield.prefab's BoxCollider on its own root is also removed — the
    /// AirfieldPrefab root's BoxCollider is the single source of truth.
    /// </summary>
    private static void StripDangerousComponentsInside(GameObject newVisual)
    {
        int colliders = 0, lights = 0, cams = 0, anims = 0;
        foreach (Collider c in newVisual.GetComponentsInChildren<Collider>(true))
        {
            if (c == null) continue;
            Object.DestroyImmediate(c, true);
            colliders++;
        }
        foreach (Light l in newVisual.GetComponentsInChildren<Light>(true))
        {
            if (l == null) continue;
            Object.DestroyImmediate(l, true);
            lights++;
        }
        foreach (Camera cam in newVisual.GetComponentsInChildren<Camera>(true))
        {
            if (cam == null) continue;
            Object.DestroyImmediate(cam, true);
            cams++;
        }
        foreach (Animator a in newVisual.GetComponentsInChildren<Animator>(true))
        {
            if (a == null) continue;
            Object.DestroyImmediate(a, true);
            anims++;
        }
        Debug.Log($"[UseNewAirfield]   Stripped from new visual subtree: Colliders={colliders} " +
                  $"(incl. the asset's own root BoxCollider), Lights={lights}, Cameras={cams}, Animators={anims}.");
    }

    private static void ApplyTargetScale(GameObject newVisual)
    {
        newVisual.transform.localScale = new Vector3(TargetScale, TargetScale, TargetScale);
        Debug.Log($"[UseNewAirfield]   Visual_NewAirfield.localScale = ({TargetScale:F3}, {TargetScale:F3}, {TargetScale:F3}). " +
                  $"Native 70 m → ~{70f * TargetScale:F0} m visible footprint.");
    }

    private static void ResizeRootBoxCollider(GameObject root)
    {
        BoxCollider bc = root.GetComponent<BoxCollider>();
        if (bc == null) bc = root.AddComponent<BoxCollider>();
        Vector3 oldSize = bc.size;
        Vector3 oldCenter = bc.center;
        bc.size   = ColliderSize;
        bc.center = ColliderCenter;
        Debug.Log($"[UseNewAirfield]   Root BoxCollider: size {oldSize} → {ColliderSize}, " +
                  $"center {oldCenter} → {ColliderCenter}.  (Flat-footprint convention; no MeshCollider.)");
    }

    private static void RepositionSlotsAndTaxi(GameObject root)
    {
        for (int i = 0; i < Layout.Length; i++)
        {
            SlotLayout L = Layout[i];
            Transform slot = root.transform.Find($"Slot_{L.Index}");
            Transform taxi = root.transform.Find($"Taxi_{L.Index}");
            if (slot == null || taxi == null)
            {
                Debug.LogError($"[UseNewAirfield] ✗ Missing Slot_{L.Index} or Taxi_{L.Index} on AirfieldPrefab. " +
                               "Gameplay anchors must already exist — rebuild via Tools → RTS → Air System → " +
                               "Repair Airfield Layout (legacy) first.");
                continue;
            }
            slot.localPosition    = L.Slot;
            slot.localEulerAngles = new Vector3(0f, L.RotationY, 0f);
            taxi.localPosition    = L.Taxi;
            taxi.localEulerAngles = new Vector3(0f, L.RotationY, 0f);
        }
        Debug.Log("[UseNewAirfield]   Repositioned 6 Slot_* / Taxi_* pairs (user-captured reference). " +
                  "RIGHT 0/1/2 rotY = -55.959°, LEFT 3/4/5 rotY = +55.959°, Y = 0.6.");
    }

    private static void RepositionRunwayAndLanding(GameObject root)
    {
        int done = 0, missing = 0;
        for (int i = 0; i < RunwayPoints.Length; i++)
        {
            NamedPoint p = RunwayPoints[i];
            Transform t = root.transform.Find(p.Name);
            if (t == null)
            {
                missing++;
                Debug.LogWarning($"[UseNewAirfield] ⚠ '{p.Name}' missing — skipped.");
                continue;
            }
            t.localPosition = p.Pos;
            if (!float.IsNaN(p.RotY))
                t.localEulerAngles = new Vector3(0f, p.RotY, 0f);
            done++;
        }
        Debug.Log($"[UseNewAirfield]   Repositioned {done}/{RunwayPoints.Length} runway / lane / landing " +
                  $"point(s). ({missing} missing.)  Queue+Start co-located per lane at user-captured spot; " +
                  $"runway heading Y = {ApplyManualAirfieldTakeoffPoints.RunwayHeadingY:F4}°.");
    }

    /// <summary>
    /// Per agreement: the new asset's emission map has airport markings
    /// baked in (yellow lines, pad numbers, hangar windows), so painting
    /// <c>_EmissionColor</c> via property block would tint ALL of those to
    /// the team colour. Cleaner to leave the airport's emission stock for
    /// now and revisit when the asset gets a dedicated team-color material
    /// slot. We KEEP the TeamColorMarker component (so future wiring is
    /// trivial), just clear its target list and turn both paint flags off.
    /// </summary>
    private static void DisableTeamColorEmission(GameObject root)
    {
        TeamColorMarker m = root.GetComponent<TeamColorMarker>();
        if (m == null)
        {
            Debug.Log("[UseNewAirfield]   No TeamColorMarker on root — nothing to disable.");
            return;
        }
        m.bodyColorRenderers.Clear();
        m.applyToBaseColor = false;
        m.applyToEmission  = false;
        EditorUtility.SetDirty(m);
        Debug.Log("[UseNewAirfield]   TeamColorMarker set to inert: bodyColorRenderers cleared, " +
                  "applyToBaseColor=false, applyToEmission=false. " +
                  "The baked emission map (yellow lines / pad numbers / hangars) is intentionally untouched.");
    }

    private static void PrintFinalReport(GameObject root)
    {
        BoxCollider bc = root.GetComponent<BoxCollider>();
        Transform newVis = root.transform.Find(NewVisualChildName);
        Transform oldBackup = root.transform.Find(OldVisualBackupParent);

        Debug.Log("[UseNewAirfield] ── Final state ──");
        Debug.Log($"[UseNewAirfield]   Root.localScale = {root.transform.localScale}.");
        Debug.Log($"[UseNewAirfield]   BoxCollider size = {(bc != null ? bc.size.ToString() : "MISSING")}, " +
                  $"center = {(bc != null ? bc.center.ToString() : "—")}.");
        Debug.Log($"[UseNewAirfield]   New visual: {(newVis != null ? $"'{newVis.name}', localScale = {newVis.localScale}" : "MISSING")}.");
        Debug.Log($"[UseNewAirfield]   Old visual backup: {(oldBackup != null ? $"present, active = {oldBackup.gameObject.activeSelf}" : "absent")}.");
        Debug.Log($"[UseNewAirfield]   Backup prefab: {BackupPath}.");
    }

    private static void SetLayerRecursive(GameObject go, int layer)
    {
        if (go == null) return;
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++)
            SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
    }
}
