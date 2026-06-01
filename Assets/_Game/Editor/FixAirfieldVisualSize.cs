using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// All-in-one repair after a botched <see cref="ReplaceAirportVisual"/> run.
/// The symptom this targets: <c>Visual_NewFbx</c> is at an extreme local
/// scale (e.g. ×10 → freeze), or has been clamped to ×3 by the runtime
/// repair (→ much too small), AND there's a leftover team-colour cube +
/// possibly the old primitive "ground pieces" still visible.
///
/// The right fix is to scale the FBX at the IMPORTER level (Scale Factor)
/// so the imported model is the correct size on its own — then
/// <c>Visual_NewFbx.localScale</c> stays at (1,1,1) and there is no
/// instance-side multiplication of internal FBX child scales (the original
/// cause of the "giant wall" cascade). This tool does that automatically:
///
///   1. Measures the FBX's current world-space bounds at its current
///      <see cref="ModelImporter.globalScale"/>.
///   2. Computes the multiplier needed for the model to fill the target
///      airfield footprint envelope (~18×26 m, sitting inside the 20×28 m
///      BoxCollider).
///   3. Sets <see cref="ModelImporter.globalScale"/> to that absolute
///      value (current × multiplier) and reimports — the FBX prefab is
///      now natively the right size.
///   4. Opens AirfieldPrefab and:
///        • Resets <c>Visual_NewFbx</c> localScale → (1,1,1), localPosition → 0,
///          localRotation → identity (so the inherited FBX size shows through).
///        • Re-activates every previously-disabled descendant of
///          <c>Visual_NewFbx</c> (so meshes that <see cref="RepairAirfieldRuntimePrefab"/>
///          turned off when bounds blew up at ×10 come back at the corrected
///          scale).
///        • Hard-strips Lights / Cameras / Animators / non-root Colliders
///          INSIDE the visual subtree (these were the real freeze culprits).
///        • Disables any descendant whose renderer bounds STILL exceed the
///          envelope × 2 — final safety net against any one freakishly-
///          scaled child.
///        • Deletes the <c>TeamColorAccent_New</c> cube ("leftover Cube").
///        • Ensures <c>OldVisual_Backup</c> is inactive AND deactivates every
///          child inside it (defensive — guards against the old "two flat
///          ground pieces" leak).
///        • Verifies the root BoxCollider is (20, 2, 28) / centre (0, 1, 0)
///          and resets if drifted.
///        • Saves.
///   5. Prints a full report (root scale, visual scale, combined bounds,
///      collider size, renderer count, leftover backup objects, leftover
///      cubes, non-root colliders, lights, cameras, animators).
///
/// What this tool does NOT do:
///   • Move, rename or delete any gameplay anchor (Slot_*, Taxi_*, runway
///     markers, landing markers, SelectionRing).
///   • Touch the root MonoBehaviour stack (Building, SelectableBuilding,
///     PowerConsumer, Airfield, Health, GameEntity, TeamColorMarker).
///   • Re-enable <c>OldVisual_Backup</c> — that is intentionally left
///     inactive so only the new FBX shows.
///
/// Menu: Tools → RTS → Buildings → Fix Airfield Visual Size
/// </summary>
public static class FixAirfieldVisualSize
{
    private const string AirfieldPrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";
    private static readonly string[] NewFbxCandidatePaths =
    {
        "Assets/_Game/Art/Models/Airport_New.fbx",
        "Assets/_Game/Art/Models/Airport/Airport_New.fbx",
    };

    private const string NewVisualChildName = "Visual_NewFbx";
    private const string TeamColorChildName = "TeamColorAccent_New";
    private const string BackupParentName   = "OldVisual_Backup";

    // The new FBX should fill MOST of the BoxCollider's 20×28 footprint but
    // stay a touch inside it (so the placement overlap check has slack and
    // the team-colour marker / SelectionRing don't clip the visible meshes).
    private const float TargetEnvelopeX = 18f;
    private const float TargetEnvelopeZ = 26f;

    // Hard safety: ERROR if a single child renderer ends up larger than this.
    // This is what catches the "giant wall" scenario for FBXes whose internal
    // hierarchy has a child at extreme local scale that multiplies with the
    // importer scale to produce a wall-sized mesh.
    private const float EnvelopeOversizeFactor = 2.0f;
    private const float EnvelopeX = 35f;
    private const float EnvelopeY =  8f;
    private const float EnvelopeZ = 45f;

    // Don't reimport for a tiny tweak — saves user time and editor reload churn.
    private const float ReimportScaleEpsilon = 0.01f;

    private static readonly Vector3 ExpectedColliderSize   = new Vector3(20f, 2f, 28f);
    private static readonly Vector3 ExpectedColliderCenter = new Vector3( 0f, 1f,  0f);

    // ================================================================== //
    // Menu entry
    // ================================================================== //

    [MenuItem("Tools/RTS/Buildings/Fix Airfield Visual Size")]
    public static void Run()
    {
        Debug.Log("[FixAirfieldVisual] ─── Fixing Airfield visual size + leftovers ───");

        // 0. Locate assets.
        string fbxPath = FindFbxPath();
        if (string.IsNullOrEmpty(fbxPath))
        {
            string tried = string.Join(", ", NewFbxCandidatePaths);
            Debug.LogError($"[FixAirfieldVisual] ✗ Airport FBX not found. Looked in: {tried}.");
            return;
        }
        if (AssetDatabase.LoadAssetAtPath<GameObject>(AirfieldPrefabPath) == null)
        {
            Debug.LogError($"[FixAirfieldVisual] ✗ AirfieldPrefab not found at '{AirfieldPrefabPath}'.");
            return;
        }
        Debug.Log($"[FixAirfieldVisual]   Prefab path: {AirfieldPrefabPath}");
        Debug.Log($"[FixAirfieldVisual]   FBX path:    {fbxPath}");

        // 1. Resize FBX at the importer level so Visual_NewFbx stays at scale 1.
        ResizeFbxImporter(fbxPath);

        // 2. Apply prefab-side cleanup.
        ApplyPrefabCleanup();

        // 3. Print a final report so the user can see the end state.
        ReportFinalState();

        Debug.Log("[FixAirfieldVisual] ─── Done. Test by building the Airfield in-game. ───");
    }

    // ================================================================== //
    // Step 1 — FBX importer resize
    // ================================================================== //

    /// <summary>
    /// Reads the FBX's bounds AT ITS CURRENT importer scale by instantiating
    /// it temporarily in the prefab-contents staging scene, computes the
    /// multiplier that would make it fill the airfield envelope, and writes
    /// the new <see cref="ModelImporter.globalScale"/> back. Then triggers
    /// a reimport. This is the lever that actually changes the FBX's
    /// effective size at runtime — applying it here means the AirfieldPrefab
    /// can keep <c>Visual_NewFbx.localScale = (1,1,1)</c>.
    /// </summary>
    private static void ResizeFbxImporter(string fbxPath)
    {
        ModelImporter mi = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
        if (mi == null)
        {
            Debug.LogError($"[FixAirfieldVisual] ✗ '{fbxPath}' is not a ModelImporter asset. " +
                           "Cannot resize at import time.");
            return;
        }

        Vector3 nativeSize = MeasureFbxNativeBoundsSize(fbxPath);
        if (nativeSize == Vector3.zero)
        {
            Debug.LogError("[FixAirfieldVisual] ✗ FBX renderer bounds are zero — cannot compute " +
                           "scale. Is the model empty?");
            return;
        }
        Debug.Log($"[FixAirfieldVisual]   FBX native bounds at current importer Scale Factor " +
                  $"({mi.globalScale:F4}) = ({nativeSize.x:F2}, {nativeSize.y:F2}, {nativeSize.z:F2}).");

        float fitX = TargetEnvelopeX / Mathf.Max(nativeSize.x, 0.0001f);
        float fitZ = TargetEnvelopeZ / Mathf.Max(nativeSize.z, 0.0001f);
        float fit  = Mathf.Min(fitX, fitZ);

        float newGlobalScale = mi.globalScale * fit;
        if (newGlobalScale <= 0f || float.IsInfinity(newGlobalScale) || float.IsNaN(newGlobalScale))
        {
            Debug.LogError($"[FixAirfieldVisual] ✗ Computed new Scale Factor ({newGlobalScale}) is invalid. Aborting import resize.");
            return;
        }

        if (Mathf.Abs(newGlobalScale - mi.globalScale) < ReimportScaleEpsilon)
        {
            Debug.Log($"[FixAirfieldVisual]   FBX already sized correctly ({mi.globalScale:F4} ≈ {newGlobalScale:F4}). " +
                      "Skipping reimport.");
            return;
        }

        Debug.Log($"[FixAirfieldVisual]   Setting FBX ModelImporter.globalScale: {mi.globalScale:F4} → {newGlobalScale:F4} " +
                  $"(fit factor ×{fit:F3} so the FBX fills ~{TargetEnvelopeX:F0}×{TargetEnvelopeZ:F0} m inside the " +
                  "(20×28) BoxCollider). After reimport, Visual_NewFbx will sit at localScale (1,1,1) and look the right size.");

        mi.globalScale = newGlobalScale;
        mi.SaveAndReimport();
    }

    /// <summary>
    /// Returns the FBX prefab's combined renderer bounds size as it is RIGHT
    /// NOW (at whatever the importer's current globalScale value is).
    /// Instantiates the FBX prefab in a temporary edit context, measures,
    /// then immediately destroys the temp instance.
    /// </summary>
    private static Vector3 MeasureFbxNativeBoundsSize(string fbxPath)
    {
        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (asset == null) return Vector3.zero;

        // PrefabUtility.InstantiatePrefab into an isolated scene-less object —
        // works for measurement because Renderer.bounds is computed in world
        // space and the parent-less instance is at world origin / scale 1.
        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
        if (instance == null) return Vector3.zero;

        try
        {
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale    = Vector3.one;

            Renderer[] rs = instance.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (rs == null || rs.Length == 0) return Vector3.zero;

            bool   found = false;
            Bounds combined = default;
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                if (!found) { combined = rs[i].bounds; found = true; }
                else        { combined.Encapsulate(rs[i].bounds); }
            }
            return found ? combined.size : Vector3.zero;
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    // ================================================================== //
    // Step 2 — prefab-side cleanup
    // ================================================================== //

    private static void ApplyPrefabCleanup()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(AirfieldPrefabPath);
        if (root == null)
        {
            Debug.LogError($"[FixAirfieldVisual] ✗ Failed to load prefab contents for '{AirfieldPrefabPath}'.");
            return;
        }

        try
        {
            // Root scale must be 1. All footprint maths assume this.
            if (root.transform.localScale != Vector3.one)
            {
                Debug.LogWarning($"[FixAirfieldVisual]   Root scale was {root.transform.localScale} — resetting to (1,1,1).");
                root.transform.localScale = Vector3.one;
            }

            EnsureRootBoxCollider(root);
            ResetVisualNewFbxTransform(root);
            ReactivateVisualDescendants(root);
            StripDangerousComponentsInVisual(root);
            DisableOversizedDescendants(root);
            DeleteLeftoverTeamColorCube(root);
            DeactivateOldVisualBackup(root);

            PrefabUtility.SaveAsPrefabAsset(root, AirfieldPrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static void EnsureRootBoxCollider(GameObject root)
    {
        BoxCollider bc = root.GetComponent<BoxCollider>();
        if (bc == null)
        {
            bc = root.AddComponent<BoxCollider>();
            bc.size   = ExpectedColliderSize;
            bc.center = ExpectedColliderCenter;
            Debug.LogWarning($"[FixAirfieldVisual]   Root BoxCollider was missing — recreated " +
                             $"with size={ExpectedColliderSize}, centre={ExpectedColliderCenter}.");
            return;
        }
        if (bc.size != ExpectedColliderSize)
        {
            Debug.LogWarning($"[FixAirfieldVisual]   Root BoxCollider size {bc.size} drifted — resetting to {ExpectedColliderSize}.");
            bc.size = ExpectedColliderSize;
        }
        if (bc.center != ExpectedColliderCenter)
        {
            Debug.LogWarning($"[FixAirfieldVisual]   Root BoxCollider centre {bc.center} drifted — resetting to {ExpectedColliderCenter}.");
            bc.center = ExpectedColliderCenter;
        }
    }

    private static void ResetVisualNewFbxTransform(GameObject root)
    {
        Transform vis = root.transform.Find(NewVisualChildName);
        if (vis == null)
        {
            Debug.LogError($"[FixAirfieldVisual] ✗ No '{NewVisualChildName}' child found on AirfieldPrefab. " +
                           "Run Tools → RTS → Buildings → Replace Airport Visual With New FBX first.");
            return;
        }
        Vector3 oldScale = vis.localScale;
        vis.localPosition = Vector3.zero;
        vis.localRotation = Quaternion.identity;
        vis.localScale    = Vector3.one;
        Debug.Log($"[FixAirfieldVisual]   '{NewVisualChildName}' localScale {oldScale} → (1,1,1), " +
                  "localPosition reset to 0, localRotation reset to identity. The reimported FBX " +
                  "now provides its own correct size.");
    }

    /// <summary>
    /// Re-activates anything <see cref="RepairAirfieldRuntimePrefab.DisableHugeRenderers"/>
    /// turned off in a previous pass. Those were disabled because their
    /// bounds at the dangerous Visual_NewFbx ×10 scale exceeded the envelope.
    /// At the corrected importer scale they should all be within budget; if
    /// any one is still too big, <see cref="DisableOversizedDescendants"/>
    /// catches it after this pass.
    /// </summary>
    private static void ReactivateVisualDescendants(GameObject root)
    {
        Transform vis = root.transform.Find(NewVisualChildName);
        if (vis == null) return;
        int reactivated = 0;
        Transform[] all = vis.GetComponentsInChildren<Transform>(includeInactive: true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null || t == vis) continue;
            if (t.gameObject.activeSelf) continue;
            t.gameObject.SetActive(true);
            reactivated++;
        }
        if (reactivated > 0)
            Debug.Log($"[FixAirfieldVisual]   Re-activated {reactivated} previously-disabled descendant(s) " +
                      "inside Visual_NewFbx (they were disabled by a prior Repair pass at the broken scale).");
        else
            Debug.Log("[FixAirfieldVisual]   No disabled FBX descendants to re-activate.");
    }

    /// <summary>
    /// The real fix for the freeze: every Light / Camera / Animator / non-root
    /// Collider INSIDE the FBX visual subtree gets destroyed. These survive
    /// the BPM ghost strip (which only handles MonoBehaviour) and are the
    /// reason the placement preview locked up the editor when the FBX was
    /// dragged into the placement pipeline.
    /// </summary>
    private static void StripDangerousComponentsInVisual(GameObject root)
    {
        Transform vis = root.transform.Find(NewVisualChildName);
        if (vis == null) return;

        int colliders = 0, lights = 0, cams = 0, anims = 0;
        foreach (Collider c in vis.GetComponentsInChildren<Collider>(true))
        {
            if (c == null) continue;
            Object.DestroyImmediate(c, true);
            colliders++;
        }
        foreach (Light l in vis.GetComponentsInChildren<Light>(true))
        {
            if (l == null) continue;
            Object.DestroyImmediate(l, true);
            lights++;
        }
        foreach (Camera cam in vis.GetComponentsInChildren<Camera>(true))
        {
            if (cam == null) continue;
            Object.DestroyImmediate(cam, true);
            cams++;
        }
        foreach (Animator an in vis.GetComponentsInChildren<Animator>(true))
        {
            if (an == null) continue;
            Object.DestroyImmediate(an, true);
            anims++;
        }
        if (colliders + lights + cams + anims > 0)
            Debug.LogWarning($"[FixAirfieldVisual]   Stripped from Visual_NewFbx subtree: " +
                             $"Colliders={colliders}, Lights={lights}, Cameras={cams}, Animators={anims}. " +
                             "Only the root BoxCollider on AirfieldPrefab is used for gameplay.");
        else
            Debug.Log("[FixAirfieldVisual]   No dangerous components inside Visual_NewFbx.");
    }

    /// <summary>
    /// Last line of defence against the "giant wall" cascade. After the
    /// importer resize + scale-1 reset, no renderer SHOULD exceed the
    /// envelope. If one still does, it almost certainly has an FBX-internal
    /// local scale absurdly larger than its siblings — disable it rather
    /// than risk the freeze coming back.
    /// </summary>
    private static void DisableOversizedDescendants(GameObject root)
    {
        Transform vis = root.transform.Find(NewVisualChildName);
        if (vis == null) return;
        Renderer[] rs = vis.GetComponentsInChildren<Renderer>(includeInactive: true);
        int disabled = 0;
        for (int i = 0; i < rs.Length; i++)
        {
            Renderer r = rs[i];
            if (r == null || !r.gameObject.activeInHierarchy) continue;
            Vector3 sz = r.bounds.size;
            bool tooBig = sz.x > EnvelopeX * EnvelopeOversizeFactor
                       || sz.y > EnvelopeY * EnvelopeOversizeFactor
                       || sz.z > EnvelopeZ * EnvelopeOversizeFactor;
            if (!tooBig) continue;
            r.gameObject.SetActive(false);
            disabled++;
            Debug.LogError($"[FixAirfieldVisual]   ✗ Disabled '{Path(r.transform, root.transform)}' " +
                           $"— renderer bounds {sz} still exceed envelope × {EnvelopeOversizeFactor:F1}. " +
                           "Indicates an internal FBX child with absurd local scale — re-export the " +
                           "FBX with a clean transform hierarchy if you want this mesh visible.");
        }
        if (disabled == 0)
            Debug.Log("[FixAirfieldVisual]   No oversized renderers in Visual_NewFbx — clean.");
    }

    /// <summary>
    /// Removes the small primitive cube the previous Replace tool added as
    /// a stand-in team-colour marker. The user has decided the cube looks
    /// out of place; the airport will simply have no team-colour accent
    /// until a renderer on the FBX is wired into TeamColorMarker.bodyColorRenderers.
    /// </summary>
    private static void DeleteLeftoverTeamColorCube(GameObject root)
    {
        Transform t = root.transform.Find(TeamColorChildName);
        if (t != null)
        {
            Object.DestroyImmediate(t.gameObject);
            Debug.Log($"[FixAirfieldVisual]   Deleted leftover '{TeamColorChildName}' cube.");
        }

        // Also sweep for any other top-level "Cube" GameObject that may have
        // been left behind by earlier tool runs.
        int sweptCubes = 0;
        for (int i = root.transform.childCount - 1; i >= 0; i--)
        {
            Transform c = root.transform.GetChild(i);
            if (c == null) continue;
            if (c.name == "Cube" || c.name.StartsWith("Cube (") || c.name.StartsWith("Cube_"))
            {
                Object.DestroyImmediate(c.gameObject);
                sweptCubes++;
            }
        }
        if (sweptCubes > 0)
            Debug.Log($"[FixAirfieldVisual]   Swept {sweptCubes} stray 'Cube' object(s).");
    }

    /// <summary>
    /// Guarantees that the OldVisual_Backup container AND every child inside
    /// it are inactive — the user reported "two large flat ground pieces"
    /// still visible near the airfield. The backup parent being inactive
    /// should hide its descendants automatically, but a scene override on
    /// an instance could have re-activated a primitive (Runway / Apron /
    /// Pad_*). We force-disable each child too so this can't reoccur.
    /// </summary>
    private static void DeactivateOldVisualBackup(GameObject root)
    {
        Transform backup = root.transform.Find(BackupParentName);
        if (backup == null)
        {
            Debug.Log($"[FixAirfieldVisual]   No '{BackupParentName}' present — nothing to deactivate.");
            return;
        }
        if (backup.gameObject.activeSelf)
        {
            backup.gameObject.SetActive(false);
            Debug.Log($"[FixAirfieldVisual]   Set '{BackupParentName}' inactive.");
        }
        int kidsDisabled = 0;
        Transform[] kids = backup.GetComponentsInChildren<Transform>(includeInactive: true);
        for (int i = 0; i < kids.Length; i++)
        {
            Transform k = kids[i];
            if (k == null || k == backup) continue;
            if (!k.gameObject.activeSelf) continue;
            k.gameObject.SetActive(false);
            kidsDisabled++;
        }
        if (kidsDisabled > 0)
            Debug.Log($"[FixAirfieldVisual]   Defensive deactivate: turned off {kidsDisabled} child(ren) " +
                      $"under '{BackupParentName}'. (The container is inactive too — these wouldn't have " +
                      "rendered anyway, but belt + braces.)");
    }

    // ================================================================== //
    // Step 3 — final report
    // ================================================================== //

    private static void ReportFinalState()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(AirfieldPrefabPath);
        if (root == null) return;
        try
        {
            Debug.Log("[FixAirfieldVisual] ── Final state ──");
            Debug.Log($"[FixAirfieldVisual]   Root localScale = {root.transform.localScale}.");

            Transform vis = root.transform.Find(NewVisualChildName);
            Debug.Log($"[FixAirfieldVisual]   {NewVisualChildName}.localScale = " +
                      (vis != null ? vis.localScale.ToString() : "(none)") + ".");

            BoxCollider bc = root.GetComponent<BoxCollider>();
            Debug.Log($"[FixAirfieldVisual]   Root BoxCollider size = " +
                      (bc != null ? $"{bc.size} centre={bc.center}" : "MISSING") + ".");

            Renderer[] rs = root.GetComponentsInChildren<Renderer>(includeInactive: false);
            Bounds combined = default;
            bool gotOne = false;
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null) continue;
                if (!gotOne) { combined = r.bounds; gotOne = true; }
                else         { combined.Encapsulate(r.bounds); }
            }
            string boundsStr = gotOne ? $"{combined.size:F1}" : "(no active renderers)";
            Debug.Log($"[FixAirfieldVisual]   Combined ACTIVE renderer bounds size = {boundsStr}; " +
                      $"active renderer count = {rs.Length}.");

            Transform backup = root.transform.Find(BackupParentName);
            int backupActiveKids = 0;
            if (backup != null && backup.gameObject.activeInHierarchy)
            {
                Transform[] kids = backup.GetComponentsInChildren<Transform>(includeInactive: false);
                for (int i = 0; i < kids.Length; i++) if (kids[i] != backup) backupActiveKids++;
            }
            Debug.Log($"[FixAirfieldVisual]   {BackupParentName}: " +
                      (backup == null ? "absent" :
                       backup.gameObject.activeInHierarchy ? $"ACTIVE ({backupActiveKids} live child(ren))" :
                                                             "inactive ✓") + ".");

            // Stray cubes — same heuristic as DeleteLeftoverTeamColorCube.
            int strayCubes = 0;
            for (int i = 0; i < root.transform.childCount; i++)
            {
                Transform c = root.transform.GetChild(i);
                if (c == null) continue;
                if (c.name == "Cube" || c.name.StartsWith("Cube (") || c.name.StartsWith("Cube_"))
                    strayCubes++;
                if (c.name == TeamColorChildName) strayCubes++;
            }
            Debug.Log($"[FixAirfieldVisual]   Leftover cubes (Cube* / {TeamColorChildName}): {strayCubes}.");

            // Non-root colliders + suspect components.
            Collider[] cols = root.GetComponentsInChildren<Collider>(includeInactive: true);
            int nonRoot = 0;
            for (int i = 0; i < cols.Length; i++)
                if (cols[i] != null && cols[i].transform != root.transform) nonRoot++;
            int lights  = root.GetComponentsInChildren<Light>   (true).Length;
            int cams    = root.GetComponentsInChildren<Camera>  (true).Length;
            int anims   = root.GetComponentsInChildren<Animator>(true).Length;
            Debug.Log($"[FixAirfieldVisual]   Non-root colliders={nonRoot}, lights={lights}, cameras={cams}, animators={anims}.");
            Debug.Log("[FixAirfieldVisual] ────────────────");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ================================================================== //
    // Helpers
    // ================================================================== //

    private static string FindFbxPath()
    {
        for (int i = 0; i < NewFbxCandidatePaths.Length; i++)
            if (System.IO.File.Exists(NewFbxCandidatePaths[i])) return NewFbxCandidatePaths[i];
        return null;
    }

    private static string Path(Transform t, Transform root)
    {
        if (t == root) return t.name;
        string s = t.name;
        Transform p = t.parent;
        while (p != null && p != root)
        {
            s = p.name + "/" + s;
            p = p.parent;
        }
        return s;
    }
}
