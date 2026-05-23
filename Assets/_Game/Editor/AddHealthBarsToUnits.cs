using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click editor tool that adds a HealthBar child to every unit-like object
/// that has a Health component:
///
///   • SoldierPrefab.prefab        (so all future Barracks-spawned soldiers
///                                  inherit the bar)
///   • Every scene GameObject with Health that lives on the "Unit" layer
///     (e.g. the EnemyDummy, scene-placed workers)
///
/// Menu: Tools → RTS → Add Health Bars To Units
///
/// Idempotent — safe to re-run. If a "HealthBar" child already exists, it is
/// kept and re-configured rather than recreated.
///
/// Buildings (CommandCenter etc.) are intentionally skipped because they live
/// on the "Building" layer, not "Unit". Re-run this tool after you create new
/// unit prefabs and it will pick them up via the scene pass when an instance
/// is present, or extend the prefab list below to handle them at asset level.
/// </summary>
public static class AddHealthBarsToUnits
{
    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Add Health Bars To Units")]
    public static void Apply()
    {
        Debug.Log("[AddHealthBarsToUnits] ── Running ──");

        int unitLayer = LayerMask.NameToLayer("Unit");
        if (unitLayer < 0)
        {
            Debug.LogError("[AddHealthBarsToUnits] ✗ Layer 'Unit' does not exist. Aborting.");
            return;
        }

        // ── 1. SoldierPrefab ────────────────────────────────────────── //
        int prefabUpdated = ApplyToPrefab("SoldierPrefab");

        // ── 2. Scene objects with Health on the Unit layer ──────────── //
        int sceneUpdated = 0;
        Health[] all = Object.FindObjectsByType<Health>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (Health h in all)
        {
            if (h == null) continue;
            if (h.gameObject.layer != unitLayer)
            {
                Debug.Log($"[AddHealthBarsToUnits]   Skipping '{h.name}' " +
                          $"(layer = '{LayerMask.LayerToName(h.gameObject.layer)}', not 'Unit').");
                continue;
            }

            // Skip prefab instances whose changes would be lost — only edit
            // the asset itself for those. (Connected instances inherit the
            // change once the prefab is saved.)
            if (PrefabUtility.IsPartOfPrefabInstance(h.gameObject) &&
                PrefabUtility.GetCorrespondingObjectFromSource(h.gameObject) != null)
            {
                Debug.Log($"[AddHealthBarsToUnits]   Skipping '{h.name}' — prefab instance " +
                          "(inherits bar from the saved prefab).");
                continue;
            }

            if (EnsureHealthBarChild(h.gameObject))
                sceneUpdated++;
        }

        // ── 3. Persist ──────────────────────────────────────────────── //
        if (sceneUpdated > 0)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log($"[AddHealthBarsToUnits] ✓ Done. Prefabs updated: {prefabUpdated}. " +
                  $"Scene objects updated: {sceneUpdated}.\n" +
                  "  Press Play, attack the EnemyDummy — bars should fill/colour/shrink as it takes damage.");
    }

    // ------------------------------------------------------------------ //
    // Prefab pass
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Loads the named prefab, ensures it has a HealthBar child, and saves it
    /// back. Returns 1 on success, 0 if the prefab was not found.
    /// </summary>
    private static int ApplyToPrefab(string prefabName)
    {
        string prefabPath = AssetDatabase
            .FindAssets($"{prefabName} t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith($"/{prefabName}.prefab"));

        if (string.IsNullOrEmpty(prefabPath))
        {
            Debug.LogWarning($"[AddHealthBarsToUnits] ⚠ Prefab '{prefabName}.prefab' not found — skipping.");
            return 0;
        }

        Debug.Log($"[AddHealthBarsToUnits] ── Editing prefab {prefabPath} ──");

        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            if (root.GetComponent<Health>() == null)
            {
                Debug.LogWarning($"[AddHealthBarsToUnits] ⚠ '{prefabName}' has no Health component — skipping.");
                return 0;
            }

            EnsureHealthBarChild(root);
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Debug.Log($"[AddHealthBarsToUnits] ✓ '{prefabName}.prefab' updated.");
            return 1;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ------------------------------------------------------------------ //
    // Common helper — works on both prefab roots and scene roots
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Ensures <paramref name="unit"/> has a child named "HealthBar" with a
    /// HealthBar component attached. Returns true if anything changed.
    /// </summary>
    private static bool EnsureHealthBarChild(GameObject unit)
    {
        Transform existing = unit.transform.Find("HealthBar");
        bool changed = false;

        if (existing == null)
        {
            GameObject barGO = new GameObject("HealthBar");
            barGO.transform.SetParent(unit.transform, worldPositionStays: false);
            barGO.AddComponent<HealthBar>();
            Debug.Log($"[AddHealthBarsToUnits]   ✓ Added HealthBar child to '{unit.name}'.");
            changed = true;
        }
        else if (existing.GetComponent<HealthBar>() == null)
        {
            existing.gameObject.AddComponent<HealthBar>();
            Debug.Log($"[AddHealthBarsToUnits]   ✓ Added HealthBar component to existing child of '{unit.name}'.");
            changed = true;
        }
        else
        {
            Debug.Log($"[AddHealthBarsToUnits]   = '{unit.name}' already has a HealthBar child (no changes).");
        }

        if (changed)
            EditorUtility.SetDirty(unit);

        return changed;
    }
}
