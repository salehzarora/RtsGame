using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click editor tool that guarantees there is exactly one
/// AttackTargetMarker component in the active scene, attached to GameManager.
///
/// Menu: Tools → RTS → Setup Attack Target Marker
///
/// What it does (idempotent — safe to re-run):
///   • Finds the GameManager (creates one if missing).
///   • Adds AttackTargetMarker to it if not already present.
///   • Removes any duplicate AttackTargetMarker components elsewhere in the
///     scene so there is exactly ONE active marker controller.
///   • Logs a summary to the Console.
///
/// What it does NOT do:
///   • Pre-build a marker visual GameObject — AttackTargetMarker creates its
///     own procedurally in Awake. Leave the "Marker Visual" Inspector field
///     empty unless you want to use a custom mesh.
/// </summary>
public static class SetupAttackTargetMarker
{
    [MenuItem("Tools/RTS/Setup Attack Target Marker")]
    public static void Setup()
    {
        Debug.Log("[SetupAttackTargetMarker] ── Running ──");

        // ── 1. GameManager ───────────────────────────────────────────── //
        GameObject gm = GameObject.Find("GameManager");
        if (gm == null)
        {
            gm = new GameObject("GameManager");
            Undo.RegisterCreatedObjectUndo(gm, "Create GameManager");
            Debug.LogWarning(
                "[SetupAttackTargetMarker] ⚠ GameManager was not found — created a new one.\n" +
                "  If your project already has a GameManager under a different name, " +
                "  manually move the AttackTargetMarker component onto it and delete this duplicate.");
        }

        // ── 2. Find all existing AttackTargetMarker components ───────── //
        AttackTargetMarker[] existing = Object.FindObjectsByType<AttackTargetMarker>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        // ── 3. Ensure the GameManager has one ────────────────────────── //
        AttackTargetMarker primary = gm.GetComponent<AttackTargetMarker>();
        if (primary == null)
        {
            primary = Undo.AddComponent<AttackTargetMarker>(gm);
            Debug.Log("[SetupAttackTargetMarker] ✓ Added AttackTargetMarker to GameManager.");
        }
        else
        {
            Debug.Log("[SetupAttackTargetMarker] ✓ AttackTargetMarker already on GameManager.");
        }

        // ── 4. Strip every OTHER AttackTargetMarker so there is exactly one ──
        int removed = 0;
        foreach (AttackTargetMarker m in existing)
        {
            if (m == null || m == primary) continue;
            Debug.LogWarning(
                $"[SetupAttackTargetMarker] ⚠ Found duplicate AttackTargetMarker on " +
                $"'{m.gameObject.name}' — removing it.");
            Undo.DestroyObjectImmediate(m);
            removed++;
        }

        // ── 5. Finalise ─────────────────────────────────────────────── //
        EditorUtility.SetDirty(primary);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log(
            $"[SetupAttackTargetMarker] ✓ Done. Primary marker is on '{primary.gameObject.name}'. " +
            $"Removed {removed} duplicate component(s).\n" +
            "  Press Play, select a Soldier, right-click the EnemyDummy — " +
            "  you should see '[AttackMarker] Show target: …' in the Console.");
    }
}
