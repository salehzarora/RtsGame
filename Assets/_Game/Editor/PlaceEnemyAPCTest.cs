using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// Drops one Enemy APC into the scene for testing. Reuses the player APCPrefab
/// (creating it if missing) then converts the instance to the Enemy team:
///   • Health.team = Enemy
///   • SelectableUnit removed (player can't click-select enemy units)
///   • TeamColorMarker removed (no player-color repaint)
///   • Body renderers repainted red
///   • Keeps UnitCombat + APCAntiAirAuto + GroundAutoAttackController so the
///     enemy APC auto-acquires player units / aircraft.
///
/// Menu: Tools → RTS → Enemy → Place Enemy APC
///
/// Spawn rules (mirror existing enemy test tools):
///   • Selection.activeGameObject → spawn near it.
///   • SceneView pivot.
///   • Fallback (15, 0, 15).
///   • NavMesh-snap (15 u search).
///
/// What it does NOT do:
///   • Touch the player APCPrefab on disk — only the scene instance is mutated.
///   • Enable EnemyWaveSpawner or EnemyAIController.
///   • Add SelectableBuilding or any production component.
/// </summary>
public static class PlaceEnemyAPCTest
{
    private const string EnemyParentName = "EnemyTestUnits";
    private static readonly Vector3 DefaultSpawnPos  = new Vector3(15f, 0f, 15f);
    private static readonly Vector3 SelectionOffset  = new Vector3(3f, 0f, 3f);
    private static readonly Color   EnemyRedHull     = new Color(0.55f, 0.15f, 0.15f);

    [MenuItem("Tools/RTS/Enemy/Place Enemy APC")]
    public static void Place()
    {
        Debug.Log("[EnemyTools] ── Place Enemy APC ──");

        GameObject prefab = LoadOrCreateAPCPrefab();
        if (prefab == null)
        {
            Debug.LogError("[EnemyTools] APCPrefab could not be created.");
            return;
        }

        Transform parent = GetOrCreateEnemyParent();
        Vector3   pos    = SnapToNavMesh(ResolveSpawnPos());

        GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.name = GetUniqueChildName(parent, "EnemyAPC");
        go.transform.SetParent(parent, worldPositionStays: true);
        go.transform.position = pos;
        go.transform.rotation = Quaternion.identity;

        ConvertToEnemy(go);

        Undo.RegisterCreatedObjectUndo(go, "Place Enemy APC");
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Selection.activeGameObject = go;
        SceneView sv = SceneView.lastActiveSceneView;
        if (sv != null) sv.FrameSelected();

        Debug.Log($"[EnemyTools] Placed Enemy APC at {pos:F1}. " +
                  "It auto-attacks Player ground in MG range and engages nearby Player aircraft with AA.");
    }

    // ------------------------------------------------------------------ //
    // Conversion
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Mutates the instance into an enemy unit: team flip, strip player-side
    /// components, repaint body renderers red. Combat components (UnitCombat /
    /// APCAntiAirAuto / GroundAutoAttackController) are team-aware and need no
    /// changes — they hunt the opposite team automatically.
    /// </summary>
    private static void ConvertToEnemy(GameObject apc)
    {
        Health hp = apc.GetComponent<Health>();
        if (hp != null) hp.team = Health.Team.Enemy;

        SelectableUnit su = apc.GetComponent<SelectableUnit>();
        if (su != null) Object.DestroyImmediate(su);

        TeamColorMarker tcm = apc.GetComponent<TeamColorMarker>();
        if (tcm != null) Object.DestroyImmediate(tcm);

        TeamColorApplier tca = apc.GetComponent<TeamColorApplier>();
        if (tca != null) Object.DestroyImmediate(tca);

        UnitColorMarker ucm = apc.GetComponent<UnitColorMarker>();
        if (ucm != null) Object.DestroyImmediate(ucm);

        // Repaint body renderers red. We paint every renderer that isn't a
        // LineRenderer (tracers) or the SelectionCircle (which is now inactive
        // anyway since SelectableUnit is gone) — wheels and gun barrels also
        // get tinted slightly red, which reads fine as a "different faction"
        // silhouette.
        foreach (Renderer r in apc.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            if (r == null) continue;
            if (r is LineRenderer) continue;
            string n = r.gameObject.name;
            // Leave wheels / gun mounts / selection ring intact for visual contrast.
            if (n == "WheelFL" || n == "WheelFR" || n == "WheelMFL" || n == "WheelMFR"
                || n == "WheelMRL" || n == "WheelMRR" || n == "WheelRL" || n == "WheelRR")
                continue;
            if (n == "MG_Mount" || n == "AA_Mount" || n == "SideSkirtL" || n == "SideSkirtR")
                continue;
            if (n == "SelectionCircle")
                continue;

            ApplyColor(r, EnemyRedHull);
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static GameObject LoadOrCreateAPCPrefab()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CreateAPCPrefab.PrefabPath);
        if (prefab != null) return prefab;

        Debug.Log("[EnemyTools] APCPrefab not found — creating it.");
        CreateAPCPrefab.Create();
        AssetDatabase.Refresh();
        return AssetDatabase.LoadAssetAtPath<GameObject>(CreateAPCPrefab.PrefabPath);
    }

    private static Transform GetOrCreateEnemyParent()
    {
        GameObject existing = GameObject.Find(EnemyParentName);
        if (existing != null) return existing.transform;

        GameObject go = new GameObject(EnemyParentName);
        Undo.RegisterCreatedObjectUndo(go, $"Create {EnemyParentName}");
        Debug.Log($"[EnemyTools] Created {EnemyParentName} parent.");
        return go.transform;
    }

    private static Vector3 ResolveSpawnPos()
    {
        if (Selection.activeGameObject != null)
        {
            Vector3 p = Selection.activeGameObject.transform.position + SelectionOffset;
            p.y = 0f;
            return p;
        }
        SceneView sv = SceneView.lastActiveSceneView;
        if (sv != null) { Vector3 p = sv.pivot; p.y = 0f; return p; }
        return DefaultSpawnPos;
    }

    private static Vector3 SnapToNavMesh(Vector3 desired)
    {
        if (NavMesh.SamplePosition(desired, out NavMeshHit hit, 15f, NavMesh.AllAreas))
            return hit.position;
        Debug.LogWarning($"[EnemyTools] No NavMesh near {desired:F1}. Placing at the raw position.");
        return desired;
    }

    private static string GetUniqueChildName(Transform parent, string baseName)
    {
        for (int i = 1; i < 1000; i++)
        {
            string candidate = $"{baseName}_{i:D2}";
            if (parent.Find(candidate) == null) return candidate;
        }
        return baseName;
    }

    private static void ApplyColor(Renderer r, Color color)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        Material m = new Material(sh) { color = color };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        r.sharedMaterial = m;
        r.shadowCastingMode = ShadowCastingMode.On;
    }
}
