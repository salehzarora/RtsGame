using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Drops a single MissileLauncherPrefab into the active scene at a sensible
/// test location — the SceneView pivot if open, otherwise an offset from the
/// origin. Snaps to the NavMesh so the agent stands on walkable ground.
///
/// Menu: Tools → RTS → Vehicles → Place Missile Launcher
///
/// What it does:
///   1. Loads MissileLauncherPrefab.prefab via AssetDatabase. If missing,
///      builds it on the fly via <see cref="CreateMissileLauncherPrefab.Create"/>.
///   2. Picks a spawn point — Scene-view pivot if available, otherwise (10, 0, 10).
///   3. NavMesh.SamplePosition snap (15 u search radius). Falls back to the raw
///      position with a warning if no NavMesh is found.
///   4. Instantiates the prefab as a scene object, registers Undo, selects +
///      frames the new instance in the Scene view.
///
/// What it does NOT do:
///   • Modify VehicleFactoryProducer wiring.
///   • Spend resources.
///   • Touch other player units or systems.
/// </summary>
public static class PlaceMissileLauncherTest
{
    [MenuItem("Tools/RTS/Vehicles/Place Missile Launcher")]
    public static void Place()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            CreateMissileLauncherPrefab.PrefabPath);

        if (prefab == null)
        {
            Debug.Log("[PlaceMissileLauncherTest] MissileLauncherPrefab missing — creating it.");
            CreateMissileLauncherPrefab.Create();
            AssetDatabase.Refresh();
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                CreateMissileLauncherPrefab.PrefabPath);
        }

        if (prefab == null)
        {
            Debug.LogError("[PlaceMissileLauncherTest] ✗ Could not load or create MissileLauncherPrefab.");
            return;
        }

        // Spawn near the SceneView focus point so the user sees the placement
        // immediately. Falls back to a fixed offset from origin.
        Vector3 desired;
        SceneView sv = SceneView.lastActiveSceneView;
        if (sv != null)
        {
            desired = sv.pivot;
            desired.y = 0f;
        }
        else
        {
            desired = new Vector3(10f, 0f, 10f);
        }

        Vector3 spawnPos = desired;
        if (NavMesh.SamplePosition(desired, out NavMeshHit hit, 15f, NavMesh.AllAreas))
        {
            spawnPos = hit.position;
        }
        else
        {
            Debug.LogWarning("[PlaceMissileLauncherTest] ⚠ No NavMesh near " +
                             $"{desired:F1}. Placing at the raw position. " +
                             "Bake a NavMesh around your test area for clean spawning.");
        }

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        instance.name = "MissileLauncher_Test";
        instance.transform.position = spawnPos;
        instance.transform.rotation = Quaternion.identity;

        Undo.RegisterCreatedObjectUndo(instance, "Place Missile Launcher Test");
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Selection.activeGameObject = instance;
        if (sv != null) sv.FrameSelected();

        Debug.Log($"[PlaceMissileLauncherTest] ✓ Placed '{instance.name}' at {spawnPos:F1}. " +
                  "Right-click an enemy ground target inside its 30u range to fire.");
    }
}
