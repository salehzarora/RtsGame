using UnityEditor;
using UnityEngine;

/// <summary>
/// Read-only diagnostic for AirfieldPrefab. Walks every descendant and
/// prints renderers, colliders, lights, cameras, animators, child scales,
/// pink materials. Flags ERRORs for anything that would explain a runtime
/// freeze or a "giant wall" — too-large renderer bounds, non-Box colliders,
/// extreme child scales, etc. Does NOT modify the prefab.
///
/// Menu: Tools → RTS → Buildings → Validate Airfield Prefab
/// </summary>
public static class ValidateAirfieldPrefab
{
    private const string PrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";

    // Reasonable airfield-shaped envelope. Anything substantially larger
    // than this is a likely "giant wall / block" suspect.
    //   Expected X: 20–35
    //   Expected Y:  2–8
    //   Expected Z: 25–45
    private const float ExpectMaxX = 35f;
    private const float ExpectMaxY = 8f;
    private const float ExpectMaxZ = 45f;
    private const float HardErrorScale = 1.5f;   // > expected × this => ERROR

    private const float ScaleTolerance = 0.001f; // child scale != 1 → flag

    [MenuItem("Tools/RTS/Buildings/Validate Airfield Prefab")]
    public static void Run()
    {
        Debug.Log("[ValidateAirfield] ─── Airfield prefab audit ───");
        Debug.Log($"[ValidateAirfield] Prefab path: {PrefabPath}");

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogError($"[ValidateAirfield] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            ReportRootStats(root);
            ReportVisualNewFbx(root);
            ReportRenderers(root);
            ReportColliders(root);
            ReportSuspectComponents(root);
            ReportChildScales(root.transform, depth: 0);
            ReportPinkMaterials(root);
            ReportLeftoverCubes(root);
            ReportOldVisualBackup(root);
            ReportCombinedActiveBounds(root);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[ValidateAirfield] ─── End of audit ───");
    }

    // ================================================================== //
    // Sections
    // ================================================================== //

    private static void ReportRootStats(GameObject root)
    {
        Vector3 s = root.transform.localScale;
        Debug.Log($"[ValidateAirfield] Root: name='{root.name}', localScale={F3(s)}, " +
                  $"layer={LayerMask.LayerToName(root.layer)} ({root.layer}).");
        if (!Approx(s, Vector3.one))
        {
            Debug.LogError($"[ValidateAirfield] ✗ Root scale {F3(s)} != (1,1,1). " +
                           "All footprint maths assume root scale 1. Reset it in the prefab " +
                           "or run Tools → RTS → Buildings → Repair Airfield Runtime Prefab.");
        }

        Renderer[] rends = root.GetComponentsInChildren<Renderer>(includeInactive: true);
        Collider[] cols  = root.GetComponentsInChildren<Collider>(includeInactive: true);
        Light[]    lts   = root.GetComponentsInChildren<Light>(includeInactive: true);
        Camera[]   cams  = root.GetComponentsInChildren<Camera>(includeInactive: true);
        Animator[] ans   = root.GetComponentsInChildren<Animator>(includeInactive: true);
        Debug.Log($"[ValidateAirfield] Counts: renderers={rends.Length}, colliders={cols.Length}, " +
                  $"lights={lts.Length}, cameras={cams.Length}, animators={ans.Length}.");
    }

    private static void ReportVisualNewFbx(GameObject root)
    {
        Transform vis = root.transform.Find("Visual_NewFbx");
        if (vis == null)
        {
            Debug.LogWarning("[ValidateAirfield] ⚠ No 'Visual_NewFbx' child — the airfield is using only " +
                             "primitive visuals (or you haven't run Replace Airport Visual With New FBX yet).");
            return;
        }
        Vector3 s = vis.localScale;
        bool dangerous = s.x > 3f || s.y > 3f || s.z > 3f;
        string line = $"[ValidateAirfield]   Visual_NewFbx.localScale = {F3(s)}, localPosition = {F1(vis.localPosition)}, " +
                      $"active = {vis.gameObject.activeInHierarchy}.";
        if (dangerous)
            Debug.LogError(line + " ✗ Scale > 3 multiplies with internal FBX child scales and can cause a freeze. " +
                           "Run Tools → RTS → Buildings → Fix Airfield Visual Size to rebake at the FBX importer level instead.");
        else
            Debug.Log(line);
    }

    private static void ReportLeftoverCubes(GameObject root)
    {
        int strayCubes = 0;
        for (int i = 0; i < root.transform.childCount; i++)
        {
            Transform c = root.transform.GetChild(i);
            if (c == null) continue;
            if (c.name == "Cube" || c.name.StartsWith("Cube (") || c.name.StartsWith("Cube_"))
            {
                Debug.LogWarning($"[ValidateAirfield]   ⚠ Leftover '{c.name}' child on the root. " +
                                 "Likely debris from a previous tool run.");
                strayCubes++;
            }
            if (c.name == "TeamColorAccent_New")
            {
                Debug.LogWarning($"[ValidateAirfield]   ⚠ '{c.name}' present (small team-colour cube). " +
                                 "Run Fix Airfield Visual Size to delete it if you don't want it.");
                strayCubes++;
            }
        }
        if (strayCubes == 0)
            Debug.Log("[ValidateAirfield]   No leftover Cube / TeamColorAccent_New children on the root.");
    }

    private static void ReportOldVisualBackup(GameObject root)
    {
        Transform backup = root.transform.Find("OldVisual_Backup");
        if (backup == null)
        {
            Debug.Log("[ValidateAirfield]   No 'OldVisual_Backup' present.");
            return;
        }
        if (backup.gameObject.activeInHierarchy)
        {
            int liveKids = backup.GetComponentsInChildren<Transform>(includeInactive: false).Length - 1;
            Debug.LogError($"[ValidateAirfield]   ✗ 'OldVisual_Backup' is ACTIVE with {liveKids} live child(ren). " +
                           "The user reported 'two flat ground pieces' — that's the cause. Run Fix Airfield Visual Size " +
                           "(or Repair Airfield Runtime Prefab) to deactivate it.");
        }
        else
        {
            Debug.Log("[ValidateAirfield]   'OldVisual_Backup' present and inactive ✓.");
        }
    }

    private static void ReportCombinedActiveBounds(GameObject root)
    {
        Renderer[] rs = root.GetComponentsInChildren<Renderer>(includeInactive: false);
        if (rs == null || rs.Length == 0)
        {
            Debug.LogWarning("[ValidateAirfield]   ⚠ No ACTIVE renderers — the airfield will be invisible.");
            return;
        }
        Bounds combined = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++)
            if (rs[i] != null) combined.Encapsulate(rs[i].bounds);
        Vector3 sz = combined.size;
        Debug.Log($"[ValidateAirfield]   Combined ACTIVE renderer bounds size = {F1(sz)}.  " +
                  "Target: roughly (20, _, 28) to fill the BoxCollider footprint.");
        if (sz.x < 8f || sz.z < 10f)
            Debug.LogWarning($"[ValidateAirfield]   ⚠ Combined bounds {F1(sz)} are MUCH smaller than the " +
                             "(20, _, 28) footprint. The airfield will look miniature. Run " +
                             "Tools → RTS → Buildings → Fix Airfield Visual Size to rebake at the FBX importer.");
        if (sz.x > ExpectMaxX * HardErrorScale || sz.z > ExpectMaxZ * HardErrorScale)
            Debug.LogError($"[ValidateAirfield]   ✗ Combined bounds {F1(sz)} dwarf the building envelope. " +
                           "Run Fix Airfield Visual Size or Repair Airfield Runtime Prefab.");
    }

    private static void ReportRenderers(GameObject root)
    {
        Renderer[] rends = root.GetComponentsInChildren<Renderer>(includeInactive: true);
        for (int i = 0; i < rends.Length; i++)
        {
            Renderer r = rends[i];
            if (r == null) continue;

            // bounds is world-space. For prefab-contents loaded into the
            // temporary scene, root is at world (0,0,0), so this also
            // reflects the renderer's expected size at runtime.
            Bounds b = r.bounds;
            Vector3 sz = b.size;
            string path = HierarchyPath(r.transform, root.transform);
            bool tooBig = sz.x > ExpectMaxX * HardErrorScale
                       || sz.y > ExpectMaxY * HardErrorScale
                       || sz.z > ExpectMaxZ * HardErrorScale;
            string mat = (r.sharedMaterials != null && r.sharedMaterials.Length > 0 && r.sharedMaterials[0] != null)
                       ? r.sharedMaterials[0].name : "(NONE → PINK)";
            string state = r.gameObject.activeInHierarchy ? "active" : "INACTIVE";
            string line = $"[ValidateAirfield]   Renderer '{path}' bounds size={F1(sz)} " +
                          $"center={F1(b.center)} mat0='{mat}' {state}.";
            if (tooBig)
                Debug.LogError(line + $"  ✗ HUGE — exceeds expected envelope " +
                               $"({ExpectMaxX*HardErrorScale:F0},{ExpectMaxY*HardErrorScale:F0},{ExpectMaxZ*HardErrorScale:F0}). " +
                               "Likely the 'giant wall' source.");
            else
                Debug.Log(line);
        }
    }

    private static void ReportColliders(GameObject root)
    {
        Collider[] cols = root.GetComponentsInChildren<Collider>(includeInactive: true);
        for (int i = 0; i < cols.Length; i++)
        {
            Collider c = cols[i];
            if (c == null) continue;

            string path = HierarchyPath(c.transform, root.transform);
            string type = c.GetType().Name;
            Bounds b = c.bounds;
            Vector3 sz = b.size;
            bool isRoot = c.transform == root.transform;
            string state = c.gameObject.activeInHierarchy ? "active" : "INACTIVE";
            bool tooBig = sz.x > ExpectMaxX * HardErrorScale
                       || sz.y > ExpectMaxY * HardErrorScale
                       || sz.z > ExpectMaxZ * HardErrorScale;

            string line = $"[ValidateAirfield]   Collider '{path}' type={type} bounds size={F1(sz)} " +
                          $"center={F1(b.center)} isTrigger={c.isTrigger} {state}.";

            if (!isRoot && c is MeshCollider)
                Debug.LogError(line + "  ✗ Non-root MeshCollider — these are EXPENSIVE and routinely " +
                               "the cause of placement freezes. Strip with Repair Airfield Runtime Prefab.");
            else if (!isRoot && !(c is BoxCollider))
                Debug.LogWarning(line + "  ⚠ Non-Box collider on a child — prefer a BoxCollider on the root only.");
            else if (tooBig)
                Debug.LogError(line + "  ✗ HUGE bounds — far exceeds the expected building envelope.");
            else
                Debug.Log(line);
        }
    }

    private static void ReportSuspectComponents(GameObject root)
    {
        Light[] lts = root.GetComponentsInChildren<Light>(includeInactive: true);
        for (int i = 0; i < lts.Length; i++)
        {
            Light l = lts[i];
            if (l == null) continue;
            Debug.LogWarning($"[ValidateAirfield]   ⚠ Light '{HierarchyPath(l.transform, root.transform)}' " +
                             $"type={l.type} intensity={l.intensity} range={l.range}. " +
                             "Lights baked into the FBX can stack up — consider stripping.");
        }

        Camera[] cams = root.GetComponentsInChildren<Camera>(includeInactive: true);
        for (int i = 0; i < cams.Length; i++)
        {
            Camera c = cams[i];
            if (c == null) continue;
            Debug.LogError($"[ValidateAirfield]   ✗ Camera '{HierarchyPath(c.transform, root.transform)}' " +
                           $"depth={c.depth}. FBX-imported Cameras WILL break gameplay. Strip them.");
        }

        Animator[] ans = root.GetComponentsInChildren<Animator>(includeInactive: true);
        for (int i = 0; i < ans.Length; i++)
        {
            Animator a = ans[i];
            if (a == null) continue;
            Debug.LogWarning($"[ValidateAirfield]   ⚠ Animator '{HierarchyPath(a.transform, root.transform)}' " +
                             $"controller={(a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "none")}. " +
                             "FBX Animators rarely have a controller assigned — usually safe but worth knowing.");
        }
    }

    private static void ReportChildScales(Transform t, int depth)
    {
        if (t == null) return;
        if (depth > 0) // skip root, already reported
        {
            Vector3 s = t.localScale;
            if (!ApproxOne(s))
            {
                // Visual_NewFbx is allowed a non-1 scale because it's the auto-fit
                // anchor. ERROR on any descendant of Visual_NewFbx with a non-1
                // scale (cascading internal FBX scale is the most common cause of
                // the "giant wall" once multiplied by the parent fit).
                bool inFbxSubtree = IsUnderName(t, "Visual_NewFbx");
                bool isFbxRoot    = t.name == "Visual_NewFbx";
                if (inFbxSubtree && !isFbxRoot)
                    Debug.LogError($"[ValidateAirfield]   ✗ Internal FBX scale: '{HierarchyPath(t, t.root)}' " +
                                   $"localScale={F3(s)} — this MULTIPLIES with the Visual_NewFbx parent " +
                                   "fit-scale and creates oversized meshes.");
                else if (isFbxRoot)
                    Debug.Log($"[ValidateAirfield]   Visual_NewFbx root localScale={F3(s)} " +
                              "(auto-fit anchor; clamp via Replace tool's MaxAutoFitScale).");
                else
                    Debug.LogWarning($"[ValidateAirfield]   ⚠ Non-1 child scale: '{HierarchyPath(t, t.root)}' " +
                                     $"localScale={F3(s)}.");
            }
        }
        for (int i = 0; i < t.childCount; i++)
            ReportChildScales(t.GetChild(i), depth + 1);
    }

    private static void ReportPinkMaterials(GameObject root)
    {
        Renderer[] rends = root.GetComponentsInChildren<Renderer>(includeInactive: true);
        int pink = 0;
        for (int i = 0; i < rends.Length; i++)
        {
            Material[] mats = rends[i].sharedMaterials;
            if (mats == null) continue;
            for (int m = 0; m < mats.Length; m++)
            {
                if (mats[m] == null)
                {
                    pink++;
                    Debug.LogWarning($"[ValidateAirfield]   ⚠ Pink slot: '{HierarchyPath(rends[i].transform, root.transform)}' " +
                                     $"material[{m}] is null — will render bright pink at runtime.");
                }
            }
        }
        if (pink == 0)
            Debug.Log("[ValidateAirfield]   No null material slots — no pink rendering expected.");
    }

    // ================================================================== //
    // Helpers
    // ================================================================== //

    private static bool Approx(Vector3 a, Vector3 b)
    {
        return Mathf.Abs(a.x - b.x) < ScaleTolerance
            && Mathf.Abs(a.y - b.y) < ScaleTolerance
            && Mathf.Abs(a.z - b.z) < ScaleTolerance;
    }

    private static bool ApproxOne(Vector3 a) => Approx(a, Vector3.one);

    private static bool IsUnderName(Transform t, string ancestorName)
    {
        Transform p = t.parent;
        while (p != null)
        {
            if (p.name == ancestorName) return true;
            p = p.parent;
        }
        return false;
    }

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

    private static string F1(Vector3 v) => $"({v.x:F1},{v.y:F1},{v.z:F1})";
    private static string F3(Vector3 v) => $"({v.x:F3},{v.y:F3},{v.z:F3})";
}
