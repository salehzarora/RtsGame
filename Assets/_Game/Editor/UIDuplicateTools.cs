using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Two editor tools for keeping the scene's UI roots and key managers free of
/// duplicates:
///
///   • <b>Validate UI Duplicates</b> — counts the known UI canvases / managers
///     in the OPEN scene and warns when more than one exists.
///   • <b>Cleanup Duplicate Runtime UI</b> — safely removes the extras,
///     keeping ONE of each (preferring the one referenced by GameManager
///     controllers when possible). Logs every removal. Only operates on
///     root-level GameObjects in the open scene; never touches prefab assets
///     or files on disk.
///
/// Background: several setup tools save their canvas INACTIVE at the end of
/// Run(). On a re-run, <c>GameObject.Find</c> can't see inactive objects, so
/// the previous canvas was being left in place while a new one was created
/// next to it — compounding duplicates with every re-run. The lobby + escape
/// builders are now fixed at the source, but existing scenes with the leak
/// need a cleanup pass.
/// </summary>
public static class UIDuplicateTools
{
    /// <summary>Root GameObject names this tool considers "single-instance".</summary>
    private static readonly string[] KnownUIRootNames =
    {
        "LobbyCanvas",
        "EscapeMenuCanvas",
        "OptionsCanvas",
        "MainMenuCanvas",
        "HUDCanvas",
        "MultiplayerDebugCanvas",
        "SelectionCanvas",
        "GameManager",
        "NetworkManager",
        "AudioManager",
        "EventSystem",
    };

    // ================================================================== //
    // Validate
    // ================================================================== //

    [MenuItem("Tools/RTS/UI/Validate UI Duplicates")]
    public static void ValidateMenu()
    {
        Debug.Log("[ValidateUI] ─── Counting UI duplicates ───");
        int issues = 0;

        for (int i = 0; i < KnownUIRootNames.Length; i++)
            issues += ReportRootCount(KnownUIRootNames[i]);

        // Controllers too — there should be exactly one of each.
        issues += ReportComponentCount<MultiplayerLobbyUI>();
        issues += ReportComponentCount<EscapeMenuController>();
        issues += ReportComponentCount<MainMenuController>();
        issues += ReportComponentCount<NetworkManagerRTS>();
        issues += ReportComponentCount<NetworkMatchCoordinator>();
        issues += ReportComponentCount<MultiplayerMatchStarter>();
        issues += ReportComponentCount<GameplayWorldRoot>();
        issues += ReportComponentCount<DevCommanderPanel>();
        issues += ReportComponentCount<MultiplayerDevBootstrap>();
        issues += ReportComponentCount<AudioManager>();
        issues += ReportComponentCount<EventSystem>();

        Debug.Log(issues == 0
            ? "[ValidateUI] ✓ No duplicates detected."
            : $"[ValidateUI] ✗ {issues} duplicate issue(s) found — run " +
              "Tools → RTS → UI → Cleanup Duplicate Runtime UI.");
        Debug.Log("[ValidateUI] ────────────────────────────────");
    }

    private static int ReportRootCount(string name)
    {
        List<GameObject> matches = FindRootGameObjects(name);
        int n = matches.Count;
        if (n == 0)
        {
            Debug.Log($"[ValidateUI]   '{name}' x{n} (not present)");
            return 0;
        }
        if (n == 1)
        {
            Debug.Log($"[ValidateUI] ✓ '{name}' x1");
            return 0;
        }
        Debug.LogWarning($"[ValidateUI] ✗ '{name}' x{n} — DUPLICATES.");
        return 1;
    }

    private static int ReportComponentCount<T>() where T : Component
    {
        T[] all = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        int n = 0;
        for (int i = 0; i < all.Length; i++) if (all[i] != null) n++;
        string typeName = typeof(T).Name;
        if (n == 0)
        {
            Debug.Log($"[ValidateUI]   {typeName} x{n} (not present)");
            return 0;
        }
        if (n == 1)
        {
            Debug.Log($"[ValidateUI] ✓ {typeName} x1");
            return 0;
        }
        Debug.LogWarning($"[ValidateUI] ✗ {typeName} x{n} — DUPLICATES.");
        return 1;
    }

    // ================================================================== //
    // Cleanup
    // ================================================================== //

    [MenuItem("Tools/RTS/UI/Cleanup Duplicate Runtime UI")]
    public static void CleanupMenu()
    {
        Debug.Log("[CleanupUI] ─── Removing duplicate UI root(s) ───");
        int totalRemoved = 0;
        for (int i = 0; i < KnownUIRootNames.Length; i++)
            totalRemoved += RemoveExtras(KnownUIRootNames[i]);

        if (totalRemoved > 0)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log($"[CleanupUI] Removed {totalRemoved} duplicate UI object(s). " +
                  "Run Validate UI Duplicates to confirm and Ctrl+S to save.");
        Debug.Log("[CleanupUI] ──────────────────────────────────────");
    }

    private static int RemoveExtras(string name)
    {
        List<GameObject> matches = FindRootGameObjects(name);
        if (matches.Count <= 1) return 0;

        GameObject keeper = ChooseKeeper(matches, name);
        int removed = 0;
        for (int i = 0; i < matches.Count; i++)
        {
            GameObject go = matches[i];
            if (go == null || go == keeper) continue;
            string instanceLabel = $"'{go.name}' (instanceId {go.GetInstanceID()})";
            Undo.DestroyObjectImmediate(go);
            removed++;
            Debug.Log($"[CleanupUI]   Removed duplicate {instanceLabel}.");
        }
        if (removed > 0)
            Debug.Log($"[CleanupUI] ✓ Kept 1 '{name}', removed {removed} duplicate(s).");
        return removed;
    }

    /// <summary>
    /// Pick which duplicate to keep. Strategy: prefer the one a known
    /// controller already references (e.g. <see cref="MultiplayerLobbyUI.canvasRoot"/>);
    /// otherwise prefer the active one; otherwise the first.
    /// </summary>
    private static GameObject ChooseKeeper(List<GameObject> matches, string name)
    {
        // Controller-referenced canvases.
        if (name == "LobbyCanvas")
        {
            var ui = Object.FindFirstObjectByType<MultiplayerLobbyUI>(FindObjectsInactive.Include);
            if (ui != null && ui.canvasRoot != null && matches.Contains(ui.canvasRoot))
                return ui.canvasRoot;
        }
        if (name == "MainMenuCanvas")
        {
            var mm = Object.FindFirstObjectByType<MainMenuController>(FindObjectsInactive.Include);
            if (mm != null && mm.gameObject != null && matches.Contains(mm.gameObject))
                return mm.gameObject;
        }

        // Otherwise prefer an active one (so a stale-inactive duplicate is the loser).
        for (int i = 0; i < matches.Count; i++)
            if (matches[i] != null && matches[i].activeSelf)
                return matches[i];

        return matches[0];
    }

    // ================================================================== //
    // Shared helper
    // ================================================================== //

    /// <summary>
    /// All ROOT-level GameObjects in the open scene whose name exactly matches
    /// <paramref name="name"/>, including inactive ones. Root-only filter
    /// avoids matching same-named children inside prefabs (e.g. a child
    /// "EventSystem" used by a nested template).
    /// </summary>
    private static List<GameObject> FindRootGameObjects(string name)
    {
        var matches = new List<GameObject>();
        Transform[] all = Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null) continue;
            if (t.parent != null) continue;           // root only
            if (t.name != name) continue;
            matches.Add(t.gameObject);
        }
        return matches;
    }
}
