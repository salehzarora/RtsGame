using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Removes the floating "team color stripe" children left behind by the
/// previous TeamColor accent tool from any GameObject currently in the open
/// scene. Safe to run multiple times — it only disables / deactivates
/// objects whose name clearly matches the legacy accent list.
///
/// Menu: Tools → RTS → Setup → Strip Old Team Accent Bars From Scene
///
/// What it touches:
///   • Any Transform with a name matching one of:
///       - TeamColorAccent
///       - TeamColorStripe
///       - ColorAccent
///       - AccentBar
///     The object is set inactive and renamed with a "_DISABLED" suffix so a
///     human can spot it in the Hierarchy.
///
/// What it deliberately does NOT touch:
///   • HealthBar children (kept).
///   • SelectionCircle / SelectionRing children (kept).
///   • Any renderer that isn't sitting under one of the listed names.
///   • Prefab assets on disk — use Tools → RTS → Setup → Apply Team Colors
///     To Prefabs for that.
/// </summary>
public static class StripOldTeamAccentBarsFromScene
{
    private static readonly string[] AccentNames =
    {
        "TeamColorAccent",
        "TeamColorStripe",
        "ColorAccent",
        "AccentBar",
    };

    [MenuItem("Tools/RTS/Setup/Strip Old Team Accent Bars From Scene")]
    public static void Run()
    {
        Debug.Log("[StripAccentBars] ── Running ──");

        int disabled = 0;
        Transform[] all = Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (Transform t in all)
        {
            if (t == null) continue;
            string n = t.name;

            bool isAccent = false;
            for (int i = 0; i < AccentNames.Length; i++)
            {
                if (n == AccentNames[i]) { isAccent = true; break; }
            }
            if (!isAccent) continue;

            // Hard-skip on HealthBar / SelectionCircle even if a parent matches —
            // we never want to disable those by accident. (Belt + suspenders;
            // they don't share names with the accent list.)
            if (n.IndexOf("HealthBar",       System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (n.IndexOf("SelectionCircle", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (n.IndexOf("SelectionRing",   System.StringComparison.OrdinalIgnoreCase) >= 0) continue;

            Undo.RegisterCompleteObjectUndo(t.gameObject, "Strip legacy team accent");
            t.gameObject.SetActive(false);
            t.name = n + "_DISABLED";
            disabled++;
        }

        if (disabled > 0)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log($"[StripAccentBars] ✓ Done. Disabled {disabled} legacy accent child(ren).");
    }
}
