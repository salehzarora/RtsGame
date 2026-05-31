using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Make every CornerBase in the open scene match the canonical mapping the
/// lobby preview uses, IN GAMEPLAY-CAMERA SPACE (NOT raw-world space):
///
///   A (cornerIndex 0) = visual TopLeft     (-X, -Z)
///   B (cornerIndex 1) = visual TopRight    (+X, -Z)
///   C (cornerIndex 2) = visual BottomLeft  (-X, +Z)
///   D (cornerIndex 3) = visual BottomRight (+X, +Z)
///
/// IMPORTANT — gameplay-camera convention:
/// In this game's tilted top-down RTS camera, world +Z appears at the
/// BOTTOM of the player's screen and world -Z appears at the TOP.
/// (The camera sits at +Z high looking toward -Z; -Z is the far side of the
///  frustum = top-of-screen.) The lobby preview must match what the player
/// SEES, not the mathematical world axis. An earlier version of this file
/// used the raw-world convention (+Z = top), which made the actual gameplay
/// spawn vertically flipped vs the lobby preview.
///
/// The lobby map preview anchors corner A at top-left of the preview rect,
/// B at top-right, C at bottom-left, D at bottom-right — meaning the
/// top-left of the gameplay screen as the player sees it.
///
/// This tool fixes scenes baked before the convention flip by:
///   1. Reading each CornerBase's transform.position to determine its
///      visual quadrant (sign of X and Z, with Z INVERTED relative to
///      raw-world labels — see <see cref="QuadrantFromPosition"/>).
///   2. Renaming the GameObject to "CornerBase_{letter}" for that quadrant.
///   3. Setting <see cref="CornerBase.cornerIndex"/> to that quadrant's
///      index.
/// No object is moved — only relabeled/reindexed.
///
/// Menus:
///   Tools → RTS → Match → Fix Corner Mapping        (writes — modifies the scene)
///   Tools → RTS → Match → Validate Corner Mapping   (read-only audit)
/// </summary>
public static class FixCornerMapping
{
    private static readonly string[] LetterByIndex = { "A", "B", "C", "D" };
    private static readonly string[] QuadrantByIndex =
        { "TopLeft", "TopRight", "BottomLeft", "BottomRight" };

    // ================================================================== //
    // Validate
    // ================================================================== //

    [MenuItem("Tools/RTS/Match/Validate Corner Mapping")]
    public static void ValidateMapping()
    {
        Debug.Log("[ValidateCornerMap] ─── Corner mapping audit ───");
        Debug.Log("[ValidateCornerMap] Convention: world +Z appears at the BOTTOM " +
                  "of the gameplay camera view (and world -Z at the TOP). " +
                  "Quadrants below are GAMEPLAY-VIEW quadrants, not raw-world " +
                  "quadrants. The lobby preview matches what the player SEES.");

        var ui = LobbyButtonMapping();
        for (int i = 0; i < 4; i++)
        {
            Debug.Log($"[ValidateCornerMap]   UI Button {LetterByIndex[i]} → " +
                      $"startSlot {i} → visual quadrant {QuadrantByIndex[i]}.");
        }
        _ = ui;

        CornerBase[] corners = Object.FindObjectsByType<CornerBase>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (corners == null || corners.Length == 0)
        {
            Debug.LogError("[ValidateCornerMap] ✗ No CornerBase components in scene.");
            Debug.Log("[ValidateCornerMap] ─────────────────────────────");
            return;
        }

        int mismatches = 0;
        for (int i = 0; i < corners.Length; i++)
        {
            CornerBase cb = corners[i];
            if (cb == null) continue;

            Vector3 p = cb.transform.position;
            int physicalQuadrant = QuadrantFromPosition(p);
            string physicalLabel = physicalQuadrant >= 0
                ? QuadrantByIndex[physicalQuadrant] : "?";
            string expectedName = physicalQuadrant >= 0
                ? "CornerBase_" + LetterByIndex[physicalQuadrant] : "?";

            string status = "✓";
            if (cb.cornerIndex != physicalQuadrant)
            {
                status = "✗ INDEX MISMATCH";
                mismatches++;
            }
            else if (cb.gameObject.name != expectedName)
            {
                status = "✗ NAME MISMATCH";
                mismatches++;
            }

            Debug.Log($"[ValidateCornerMap] {status}  '{cb.gameObject.name}' " +
                      $"cornerIndex={cb.cornerIndex} (visual {QuadrantByIndex[Mathf.Clamp(cb.cornerIndex, 0, 3)]})  " +
                      $"position=({p.x:F1},{p.y:F1},{p.z:F1})  " +
                      $"actualVisualQuadrant={physicalLabel}  expectedName={expectedName}");
        }

        if (mismatches == 0)
            Debug.Log("[ValidateCornerMap] ✓ All CornerBases match the canonical " +
                      "lobby preview mapping (gameplay-camera view).");
        else
            Debug.LogError($"[ValidateCornerMap] ✗ {mismatches} mismatch(es). " +
                           "Run Tools → RTS → Match → Fix Corner Mapping to repair. " +
                           "Reminder: 'top' = world -Z (toward the back of the gameplay " +
                           "camera), 'bottom' = world +Z.");
        Debug.Log("[ValidateCornerMap] ─────────────────────────────");
    }

    // ================================================================== //
    // Fix
    // ================================================================== //

    [MenuItem("Tools/RTS/Match/Fix Corner Mapping")]
    public static void FixMapping()
    {
        Debug.Log("[FixCornerMap] ─── Fixing CornerBase names + indices ───");
        Debug.Log("[FixCornerMap] Using gameplay-camera convention: " +
                  "world -Z = visual TOP of screen (A/B), world +Z = visual BOTTOM (C/D). " +
                  "If this scene was fixed once with the OLD raw-world convention, " +
                  "this pass will swap A↔C and B↔D (no objects move).");

        CornerBase[] corners = Object.FindObjectsByType<CornerBase>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (corners == null || corners.Length == 0)
        {
            Debug.LogError("[FixCornerMap] ✗ No CornerBase components in scene.");
            return;
        }

        int changes = 0;
        for (int i = 0; i < corners.Length; i++)
        {
            CornerBase cb = corners[i];
            if (cb == null) continue;

            Vector3 p = cb.transform.position;
            int q = QuadrantFromPosition(p);
            if (q < 0)
            {
                Debug.LogWarning($"[FixCornerMap] ⚠ '{cb.gameObject.name}' position " +
                                 $"({p.x:F1},{p.y:F1},{p.z:F1}) doesn't fall into a quadrant " +
                                 "(X or Z is 0). Skipping.");
                continue;
            }

            string desiredName = "CornerBase_" + LetterByIndex[q];
            int    desiredIdx  = q;
            bool   nameChanged = cb.gameObject.name != desiredName;
            bool   idxChanged  = cb.cornerIndex     != desiredIdx;

            if (nameChanged || idxChanged)
            {
                Undo.RecordObject(cb.gameObject, "Rename CornerBase");
                Undo.RecordObject(cb,            "Re-index CornerBase");
                string was = $"'{cb.gameObject.name}' (cornerIndex={cb.cornerIndex})";
                cb.gameObject.name = desiredName;
                cb.cornerIndex     = desiredIdx;
                EditorUtility.SetDirty(cb);
                EditorUtility.SetDirty(cb.gameObject);
                Debug.Log($"[FixCornerMap]   {was} → '{desiredName}' " +
                          $"(cornerIndex={desiredIdx}, {QuadrantByIndex[q]}, " +
                          $"position=({p.x:F1},{p.y:F1},{p.z:F1})).");
                changes++;
            }
            else
            {
                Debug.Log($"[FixCornerMap]   '{cb.gameObject.name}' already " +
                          $"({QuadrantByIndex[q]}) — no change.");
            }
        }

        if (changes > 0)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log($"[FixCornerMap] Fixed {changes} CornerBase(s). " +
                  (changes > 0 ? "Ctrl+S to save the scene, then run " +
                                 "Validate Corner Mapping to confirm."
                               : "Already correct — nothing to do."));
        Debug.Log("[FixCornerMap] ─────────────────────────────────");
    }

    // ================================================================== //
    // Helpers
    // ================================================================== //

    // Visual-quadrant from world position.
    // Convention: world +Z = visual BOTTOM of gameplay screen, world -Z =
    // visual TOP (see class header). So "top" of the player's view is p.z<0,
    // not p.z>0. X is unchanged (+X = visual right, -X = visual left).
    private static int QuadrantFromPosition(Vector3 p)
    {
        bool right = p.x > 0f;
        bool top   = p.z < 0f; // INVERTED: world -Z reads as visual TOP in gameplay camera.
        if (Mathf.Approximately(p.x, 0f) || Mathf.Approximately(p.z, 0f)) return -1;
        if (!right &&  top) return 0;     // visual TopLeft     = A
        if ( right &&  top) return 1;     // visual TopRight    = B
        if (!right && !top) return 2;     // visual BottomLeft  = C
                            return 3;     // visual BottomRight = D
    }

    /// <summary>Lobby UI side reference (for the validator's output).</summary>
    private static (int idx, string label)[] LobbyButtonMapping()
    {
        return new[]
        {
            (0, "TopLeft"),
            (1, "TopRight"),
            (2, "BottomLeft"),
            (3, "BottomRight"),
        };
    }
}
