using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Adds (or finds) a <see cref="MultiplayerDevBootstrap"/> on the scene's
/// GameManager so a dev build / Editor / MPPM virtual player can drive
/// auto-connect, room create/join, colour, name and corner pick from CLI
/// args, MPPM tags or Inspector overrides. No gameplay logic is changed —
/// the component is inert until at least one auto-action is set.
///
/// Menu: Tools → RTS → Multiplayer → Setup Dev Bootstrap
/// </summary>
public static class SetupMultiplayerDevBootstrap
{
    [MenuItem("Tools/RTS/Multiplayer/Setup Dev Bootstrap")]
    public static void Run()
    {
        GameObject gm = GameObject.Find("GameManager");
        if (gm == null)
        {
            gm = new GameObject("GameManager");
            Undo.RegisterCreatedObjectUndo(gm, "Create GameManager");
            Debug.LogWarning("[SetupDevBootstrap] No GameManager in scene — created one.");
        }

        MultiplayerDevBootstrap b = gm.GetComponent<MultiplayerDevBootstrap>();
        bool added = false;
        if (b == null)
        {
            b = gm.AddComponent<MultiplayerDevBootstrap>();
            added = true;
        }
        EditorUtility.SetDirty(b);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log("[SetupDevBootstrap] " +
                  (added ? "✓ Added MultiplayerDevBootstrap to GameManager."
                         : "✓ MultiplayerDevBootstrap already present on GameManager.") +
                  " Inspector overrides + CLI args + MPPM tags are all supported. " +
                  "Defaults are inert — set autoConnect / autoCreateRoom / autoStart " +
                  "to drive the flow. See TESTING_GUIDE.md.");

        Selection.activeGameObject = gm;
        EditorGUIUtility.PingObject(b);
    }
}
