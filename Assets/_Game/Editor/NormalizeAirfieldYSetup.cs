using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Editor tool that enforces the Y=0 baseline introduced by the cleanup pass:
///
///   • Every scene Airfield root has its transform.position.y set to 0.
///   • The AirfieldPrefab asset has Building.placementYOffsetOverride == 0
///     so future placements land at exactly y=0 too.
///   • The AirUnitController template default groundHeightOffset is 0 (slot-
///     relative ground baseline rather than absolute world Y).
///
/// Nothing about the gameplay logic changes — slot, taxi, lane, runway, and
/// landing markers all live at local y=0 in the prefab and are positioned
/// at the slot's world Y at runtime via AirUnitController.GetGroundY().
/// This tool only fixes the world-space Y of the root transforms.
///
/// Menu: Tools → RTS → Air System → Normalize Airfield Y Setup
///
/// Safe to re-run. Aircraft are runtime-spawned so there's no scene state to
/// migrate; just stop Play mode before running to be safe.
/// </summary>
public static class NormalizeAirfieldYSetup
{
    private const float HeightEpsilon = 0.001f;

    [MenuItem("Tools/RTS/Air System/Normalize Airfield Y Setup")]
    public static void Normalize()
    {
        if (Application.isPlaying)
        {
            Debug.LogWarning("[Airfield] Normalize Airfield Y Setup: exit Play mode first " +
                             "so spawned aircraft don't desync from their slot positions.");
            return;
        }

        Debug.Log("[Airfield] ── Normalizing Airfield Y baseline ──");

        int rootsFixed     = 0;
        int prefabsFixed   = 0;
        int prefabsHealthy = 0;
        int scenesHealthy  = 0;

        // ── 1. Scene roots: snap transform.position.y → 0 ────────────── //
        Airfield[] sceneAirfields = Object.FindObjectsByType<Airfield>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (Airfield af in sceneAirfields)
        {
            if (af == null) continue;
            Vector3 pos = af.transform.position;
            if (Mathf.Abs(pos.y) > HeightEpsilon)
            {
                Undo.RecordObject(af.transform, "Normalize Airfield Y");
                Debug.Log($"[Airfield] Root normalized to Y=0 on '{af.gameObject.name}' " +
                          $"(was {pos.y:F2}).");
                pos.y = 0f;
                af.transform.position = pos;
                rootsFixed++;
            }
            else
            {
                scenesHealthy++;
            }
        }

        // ── 2. Asset prefabs: ensure placementYOffsetOverride == 0 ───── //
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
        foreach (string guid in prefabGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) continue;
            if (asset.GetComponent<Airfield>() == null) continue;

            // Open the prefab in a sandbox so we can mutate it safely.
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Building b = root.GetComponent<Building>();
                if (b == null)
                {
                    Debug.LogWarning($"[Airfield] {path}: no Building component — skipping.");
                    continue;
                }

                if (!Mathf.Approximately(b.placementYOffsetOverride, 0f))
                {
                    Debug.Log($"[Aircraft] Root/visual Y setup normalized — " +
                              $"set Building.placementYOffsetOverride=0 on '{path}' " +
                              $"(was {b.placementYOffsetOverride:F2}).");
                    b.placementYOffsetOverride = 0f;
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    prefabsFixed++;
                }
                else
                {
                    prefabsHealthy++;
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ── 3. Persist scene changes ─────────────────────────────────── //
        if (rootsFixed > 0)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        if (prefabsFixed > 0)
            AssetDatabase.SaveAssets();

        Debug.Log($"[Airfield] Y baseline cleanup complete — " +
                  $"scene roots fixed: {rootsFixed} (already OK: {scenesHealthy}); " +
                  $"prefabs fixed: {prefabsFixed} (already OK: {prefabsHealthy}).");
    }
}
