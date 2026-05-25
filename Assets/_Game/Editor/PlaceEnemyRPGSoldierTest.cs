using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Drops a single <c>EnemyRPGSoldierPrefab</c> into the active scene at a
/// sensible test location — in front of the Scene-view camera if possible,
/// snapped to the NavMesh so it stands on the ground.
///
/// Menu: Tools → RTS → Units → Place Enemy RPG Soldier Test
///
/// What it does:
///   1. Locates EnemyRPGSoldierPrefab.prefab via AssetDatabase. If missing,
///      points the user at Create Enemy RPG Soldier Prefab and bails.
///   2. Picks a spawn point — Scene-view pivot if open, otherwise world
///      origin offset by (12, 0, 12) so it doesn't overlap an existing base.
///   3. Snaps the spawn point to the NavMesh (15-unit search radius) so the
///      unit stands on walkable ground.
///   4. Instantiates the prefab, registers Undo, selects + frames the new
///      instance in the Scene view.
///
/// What it does NOT do:
///   • Re-enable EnemyWaveSpawner or EnemyAIController.
///   • Modify player units, resources, power, or HUD.
///   • Persist a permanent enemy presence — the placed unit is a normal
///     scene object the user can move or delete.
/// </summary>
public static class PlaceEnemyRPGSoldierTest
{
    private const string PrefabName = "EnemyRPGSoldierPrefab";

    [MenuItem("Tools/RTS/Units/Place Enemy RPG Soldier Test")]
    public static void Place()
    {
        string path = AssetDatabase
            .FindAssets($"{PrefabName} t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith($"/{PrefabName}.prefab"));

        if (string.IsNullOrEmpty(path))
        {
            Debug.LogError($"[PlaceEnemyRPGSoldierTest] ✗ {PrefabName}.prefab not found.\n" +
                           "  Run Tools → RTS → Units → Create Enemy RPG Soldier Prefab first.");
            return;
        }

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
        {
            Debug.LogError($"[PlaceEnemyRPGSoldierTest] ✗ Could not load asset at {path}.");
            return;
        }

        // 1. Pick a starting point — Scene-view camera pivot if available,
        //    otherwise a fixed offset from world origin.
        Vector3 desired;
        SceneView sv = SceneView.lastActiveSceneView;
        if (sv != null)
        {
            desired = sv.pivot;
            desired.y = 0f;
        }
        else
        {
            desired = new Vector3(12f, 0f, 12f);
        }

        // 2. Snap to the NavMesh so the unit doesn't float / clip into ground.
        Vector3 spawnPos = desired;
        if (NavMesh.SamplePosition(desired, out NavMeshHit hit, 15f, NavMesh.AllAreas))
        {
            spawnPos = hit.position;
        }
        else
        {
            Debug.LogWarning("[PlaceEnemyRPGSoldierTest] ⚠ Could not find NavMesh near " +
                             $"{desired:F1}. Placing at the unsampled position. " +
                             "Bake a NavMesh near your test area for clean spawning.");
        }

        // 3. Instantiate as a scene instance (keeps the prefab link).
        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        instance.transform.position = spawnPos;
        instance.transform.rotation = Quaternion.identity;
        instance.name = $"{PrefabName}_Test";

        Undo.RegisterCreatedObjectUndo(instance, "Place Enemy RPG Soldier Test");
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        // 4. Select + frame so the user immediately sees it.
        Selection.activeGameObject = instance;
        if (sv != null) sv.FrameSelected();

        Debug.Log($"[PlaceEnemyRPGSoldierTest] ✓ Placed '{instance.name}' at {spawnPos:F1}. " +
                  "It will auto-attack any Player units that enter its scan radius (default 18u).");
    }
}
