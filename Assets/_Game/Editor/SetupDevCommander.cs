using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Adds the dev-only <see cref="DevCommanderPanel"/> to the scene's GameManager
/// so a single client can drive both sides of a multiplayer test from one
/// window. Inert in non-dev builds and when the component's <c>devMode</c> is
/// off (Inspector flag).
///
/// Menu: Tools → RTS → Multiplayer → Setup Dev Commander
/// </summary>
public static class SetupDevCommander
{
    [MenuItem("Tools/RTS/Multiplayer/Setup Dev Commander")]
    public static void Run()
    {
        GameObject gm = GameObject.Find("GameManager");
        if (gm == null)
        {
            gm = new GameObject("GameManager");
            Undo.RegisterCreatedObjectUndo(gm, "Create GameManager");
            Debug.LogWarning("[SetupDevCommander] No GameManager in scene — created one.");
        }

        DevCommanderPanel p = gm.GetComponent<DevCommanderPanel>();
        bool added = false;
        if (p == null)
        {
            p = gm.AddComponent<DevCommanderPanel>();
            added = true;
        }
        EditorUtility.SetDirty(p);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log("[SetupDevCommander] " +
                  (added ? "✓ Added DevCommanderPanel to GameManager."
                         : "✓ DevCommanderPanel already present on GameManager.") +
                  $" Toggle visibility at runtime with {p.toggleKey}. " +
                  "Panel only appears in Development Builds or when 'devMode' is true. " +
                  "See TESTING_GUIDE.md for recipes.");

        Selection.activeGameObject = gm;
        EditorGUIUtility.PingObject(p);
    }
}
