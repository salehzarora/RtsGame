using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Automated cleanup for AirfieldPrefab so the building places without
/// freezing and without a "giant wall" appearing. Targets the failure modes
/// introduced by importing an unfamiliar FBX into the airport visual:
///
///   • Non-root MeshColliders (FBX models sometimes carry them even when
///     "Generate Colliders" is off in the importer — and a multi-megapoly
///     mesh collider is the classic source of placement freezes when the
///     ghost stays in the scene every frame).
///   • Internal FBX child scales that multiply with the Visual_NewFbx
///     auto-fit and become a huge wall.
///   • FBX Lights / Cameras that survive the ghost's MonoBehaviour strip
///     in BuildingPlacementManager and pollute gameplay rendering.
///   • Animators on FBX subtrees (rarely fatal, disabled defensively).
///   • Root scale drifting away from (1,1,1).
///
/// What this tool does NOT do:
///   • Delete any GameObject. Every "fix" is a SetActive(false) +
///     component disable so the scene/prefab can be reverted by hand.
///   • Move, delete, or re-parent gameplay anchors (Slot_*, Taxi_*,
///     runway / landing markers, SelectionRing). Those are checked but
///     left alone.
///   • Bypass gameplay scripts (Building, SelectableBuilding, Airfield,
///     PowerConsumer, Health, GameEntity, TeamColorMarker, Selectable).
///
/// Menus:
///   Tools → RTS → Buildings → Repair Airfield Runtime Prefab
///       Repairs the current visual (Visual_NewFbx + accent) in place.
///
///   Tools → RTS → Buildings → Revert Airfield To Old Visual
///       Emergency rollback — disables Visual_NewFbx + TeamColorAccent_New
///       and re-activates the original primitive visuals under
///       OldVisual_Backup. The airport falls back to the old cube-and-slab
///       look but gameplay is guaranteed to work.
/// </summary>
public static class RepairAirfieldRuntimePrefab
{
    private const string PrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";

    private const string NewVisualChildName = "Visual_NewFbx";
    private const string TeamColorChildName = "TeamColorAccent_New";
    private const string BackupParentName   = "OldVisual_Backup";

    // Max scale clamp applied to Visual_NewFbx itself. Anything above this
    // creates oversized FBX-internal cascading scales. The old visual fit
    // comfortably at root scale 1, so 3× is a generous upper bound.
    private const float MaxVisualScale = 3.0f;

    // Expected building envelope (matches ValidateAirfieldPrefab).
    private const float ExpectMaxX = 35f;
    private const float ExpectMaxY = 8f;
    private const float ExpectMaxZ = 45f;

    // Expected root BoxCollider size — the gameplay footprint. Restored if
    // the prefab somehow lost or resized its collider.
    private static readonly Vector3 ExpectedRootColliderSize   = new Vector3(20f, 2f, 28f);
    private static readonly Vector3 ExpectedRootColliderCenter = new Vector3( 0f, 1f,  0f);

    // ================================================================== //
    // Repair Airfield Runtime Prefab
    // ================================================================== //

    [MenuItem("Tools/RTS/Buildings/Repair Airfield Runtime Prefab")]
    public static void Repair()
    {
        Debug.Log("[RepairAirfield] ─── Repairing AirfieldPrefab for runtime ───");

        GameObject root = LoadPrefabContentsOrLog();
        if (root == null) return;

        int changes = 0;
        try
        {
            changes += FixRootScale(root);
            changes += FixRootCollider(root);
            changes += StripNonBoxChildColliders(root);
            changes += StripFbxLightsAndCameras(root);
            changes += DisableHugeRenderers(root);
            changes += ClampVisualNewFbxScale(root);
            changes += DisableAnimators(root);
            changes += EnsureOldBackupInactive(root);

            if (changes > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            Debug.Log($"[RepairAirfield] ✓ Done. Applied {changes} change(s). " +
                      "Run Tools → RTS → Buildings → Validate Airfield Prefab to confirm.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[RepairAirfield] ─────────────────────────────────────────");
    }

    // ================================================================== //
    // Revert Airfield To Old Visual
    // ================================================================== //

    [MenuItem("Tools/RTS/Buildings/Revert Airfield To Old Visual")]
    public static void Revert()
    {
        Debug.Log("[RepairAirfield] ─── Reverting Airfield to OLD primitive visual ───");

        GameObject root = LoadPrefabContentsOrLog();
        if (root == null) return;

        try
        {
            // 1. Disable the new FBX + accent so they don't render or interfere.
            Transform newVis = root.transform.Find(NewVisualChildName);
            if (newVis != null)
            {
                newVis.gameObject.SetActive(false);
                Debug.Log($"[RepairAirfield]   Disabled '{NewVisualChildName}'.");
            }
            Transform accent = root.transform.Find(TeamColorChildName);
            if (accent != null)
            {
                accent.gameObject.SetActive(false);
                Debug.Log($"[RepairAirfield]   Disabled '{TeamColorChildName}'.");
            }

            // 2. Re-activate the backup container AND every child inside it.
            //    Reparent each child back under the root so future Replace
            //    runs see them in the canonical layout.
            Transform backup = root.transform.Find(BackupParentName);
            if (backup == null)
            {
                Debug.LogError($"[RepairAirfield] ✗ No '{BackupParentName}' child found — there's nothing " +
                               "to revert TO. Either the original visuals were never backed up, or this " +
                               "prefab was made from scratch. Use 'Repair Airfield Runtime Prefab' instead.");
                return;
            }

            int reactivated = 0;
            List<Transform> kids = new List<Transform>(backup.childCount);
            for (int i = 0; i < backup.childCount; i++) kids.Add(backup.GetChild(i));
            for (int i = 0; i < kids.Count; i++)
            {
                Transform k = kids[i];
                if (k == null) continue;
                k.SetParent(root.transform, worldPositionStays: true);
                k.gameObject.SetActive(true);
                reactivated++;
                Debug.Log($"[RepairAirfield]     Restored '{k.name}' to root (active).");
            }

            // Keep the empty backup so the Replace tool can re-use it next time.
            backup.gameObject.SetActive(false);

            // 3. Repair anything that might still be wrong on the root.
            FixRootScale(root);
            FixRootCollider(root);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[RepairAirfield] ✓ Revert complete. Re-activated {reactivated} original child(ren). " +
                      "Run Validate Airfield Prefab to confirm.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[RepairAirfield] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // Step helpers
    // ================================================================== //

    private static GameObject LoadPrefabContentsOrLog()
    {
        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (asset == null)
        {
            Debug.LogError($"[RepairAirfield] ✗ Prefab not found at '{PrefabPath}'.");
            return null;
        }
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
            Debug.LogError($"[RepairAirfield] ✗ Failed to load prefab contents for '{PrefabPath}'.");
        return root;
    }

    private static int FixRootScale(GameObject root)
    {
        Vector3 s = root.transform.localScale;
        if (s == Vector3.one) return 0;
        Debug.LogWarning($"[RepairAirfield]   Root scale was {s} — resetting to (1,1,1).");
        root.transform.localScale = Vector3.one;
        return 1;
    }

    private static int FixRootCollider(GameObject root)
    {
        BoxCollider bc = root.GetComponent<BoxCollider>();
        if (bc == null)
        {
            bc = root.AddComponent<BoxCollider>();
            bc.size   = ExpectedRootColliderSize;
            bc.center = ExpectedRootColliderCenter;
            Debug.LogWarning($"[RepairAirfield]   Root BoxCollider was MISSING — recreated with " +
                             $"size={ExpectedRootColliderSize} center={ExpectedRootColliderCenter}.");
            return 1;
        }
        int changes = 0;
        if (bc.size != ExpectedRootColliderSize)
        {
            Debug.LogWarning($"[RepairAirfield]   Root BoxCollider size was {bc.size} — resetting to {ExpectedRootColliderSize}.");
            bc.size = ExpectedRootColliderSize;
            changes++;
        }
        if (bc.center != ExpectedRootColliderCenter)
        {
            Debug.LogWarning($"[RepairAirfield]   Root BoxCollider center was {bc.center} — resetting to {ExpectedRootColliderCenter}.");
            bc.center = ExpectedRootColliderCenter;
            changes++;
        }
        return changes;
    }

    /// <summary>
    /// Removes every Collider that is NOT the root BoxCollider. MeshColliders
    /// in particular are the classic source of placement-time freezes when
    /// the ghost stays mounted every frame and Physics.CheckBox queries the
    /// scene against a million-triangle mesh.
    /// </summary>
    private static int StripNonBoxChildColliders(GameObject root)
    {
        Collider[] cols = root.GetComponentsInChildren<Collider>(includeInactive: true);
        int removed = 0;
        for (int i = 0; i < cols.Length; i++)
        {
            Collider c = cols[i];
            if (c == null) continue;
            if (c.transform == root.transform && c is BoxCollider) continue; // keep root box
            string path = HierarchyPath(c.transform, root.transform);
            string type = c.GetType().Name;
            Object.DestroyImmediate(c);
            removed++;
            Debug.LogWarning($"[RepairAirfield]   Stripped {type} from '{path}' — only the root BoxCollider is used for footprint.");
        }
        if (removed == 0)
            Debug.Log("[RepairAirfield]   No non-root colliders to strip.");
        return removed;
    }

    /// <summary>
    /// FBX imports with <c>importLights:1</c> / <c>importCameras:1</c> drag
    /// editor-bake or scene-prep lights and cameras into the runtime build.
    /// They survive BPM's MonoBehaviour ghost strip (Light and Camera are
    /// Behaviour, not MonoBehaviour), and tens of overlapping Lights can
    /// stall URP rendering. We disable them defensively.
    /// </summary>
    private static int StripFbxLightsAndCameras(GameObject root)
    {
        int changes = 0;
        Light[] lts = root.GetComponentsInChildren<Light>(includeInactive: true);
        for (int i = 0; i < lts.Length; i++)
        {
            Light l = lts[i];
            if (l == null || !l.enabled) continue;
            l.enabled = false;
            changes++;
            Debug.LogWarning($"[RepairAirfield]   Disabled FBX-baked Light '{HierarchyPath(l.transform, root.transform)}' " +
                             $"(was {l.type}, intensity={l.intensity}).");
        }
        Camera[] cams = root.GetComponentsInChildren<Camera>(includeInactive: true);
        for (int i = 0; i < cams.Length; i++)
        {
            Camera c = cams[i];
            if (c == null || !c.enabled) continue;
            c.enabled = false;
            changes++;
            Debug.LogWarning($"[RepairAirfield]   Disabled FBX-baked Camera '{HierarchyPath(c.transform, root.transform)}' " +
                             "— FBX cameras must NEVER run at gameplay time.");
        }
        return changes;
    }

    /// <summary>
    /// Walks every renderer; deactivates the GameObject when the renderer's
    /// world bounds exceed 1.5× the expected airfield envelope. That's the
    /// concrete "giant wall" trigger — a mesh whose actual rendered size
    /// dwarfs the footprint.
    /// </summary>
    private static int DisableHugeRenderers(GameObject root)
    {
        Renderer[] rends = root.GetComponentsInChildren<Renderer>(includeInactive: true);
        int disabled = 0;
        for (int i = 0; i < rends.Length; i++)
        {
            Renderer r = rends[i];
            if (r == null) continue;
            if (!r.gameObject.activeInHierarchy) continue;
            Bounds b = r.bounds;
            Vector3 sz = b.size;
            bool tooBig = sz.x > ExpectMaxX * 1.5f
                       || sz.y > ExpectMaxY * 1.5f
                       || sz.z > ExpectMaxZ * 1.5f;
            if (!tooBig) continue;

            // Don't touch the team-colour cube; we made it ourselves.
            if (r.transform == root.transform.Find(TeamColorChildName)) continue;

            r.gameObject.SetActive(false);
            disabled++;
            Debug.LogError($"[RepairAirfield]   ✗ Disabled '{HierarchyPath(r.transform, root.transform)}' " +
                           $"— renderer bounds size {sz} > envelope ({ExpectMaxX*1.5f:F0},{ExpectMaxY*1.5f:F0},{ExpectMaxZ*1.5f:F0}). " +
                           "This was most likely the 'giant wall' you saw at build time.");
        }
        return disabled;
    }

    /// <summary>
    /// Caps Visual_NewFbx.localScale so any single axis does not exceed
    /// <see cref="MaxVisualScale"/>. The Replace tool's auto-fit can produce
    /// extreme scales (e.g. 10×) when the FBX is modelled in centimetres or
    /// millimetres; that scale then multiplies with internal FBX child
    /// scales and produces wall-sized geometry.
    /// </summary>
    private static int ClampVisualNewFbxScale(GameObject root)
    {
        Transform vis = root.transform.Find(NewVisualChildName);
        if (vis == null) return 0;
        Vector3 s = vis.localScale;
        Vector3 clamped = new Vector3(
            Mathf.Min(s.x, MaxVisualScale),
            Mathf.Min(s.y, MaxVisualScale),
            Mathf.Min(s.z, MaxVisualScale));
        if (s == clamped) return 0;
        vis.localScale = clamped;
        Debug.LogWarning($"[RepairAirfield]   Clamped '{NewVisualChildName}' localScale " +
                         $"from {s} to {clamped} (cap = {MaxVisualScale}). If the model now looks " +
                         "tiny, re-import the FBX with Scale Factor = 100 (or fix its source units) " +
                         "rather than relying on auto-fit.");
        return 1;
    }

    private static int DisableAnimators(GameObject root)
    {
        Animator[] ans = root.GetComponentsInChildren<Animator>(includeInactive: true);
        int changes = 0;
        for (int i = 0; i < ans.Length; i++)
        {
            Animator a = ans[i];
            if (a == null || !a.enabled) continue;
            a.enabled = false;
            changes++;
            Debug.Log($"[RepairAirfield]   Disabled Animator on '{HierarchyPath(a.transform, root.transform)}' " +
                      "(no FBX-driven animation is expected on an Airfield).");
        }
        return changes;
    }

    private static int EnsureOldBackupInactive(GameObject root)
    {
        Transform backup = root.transform.Find(BackupParentName);
        if (backup == null) return 0;
        if (!backup.gameObject.activeSelf) return 0;
        backup.gameObject.SetActive(false);
        Debug.Log($"[RepairAirfield]   Marked '{BackupParentName}' inactive.");
        return 1;
    }

    // ================================================================== //
    // Helpers
    // ================================================================== //

    private static string HierarchyPath(Transform t, Transform root)
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
