using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// <summary>
/// Re-bakes the scene's NavMesh and warps every NavMeshAgent onto the freshly
/// baked surface so units no longer hover above (or fall through) the ground.
///
/// Menu: Tools → RTS → Environment → Rebuild NavMesh And Snap Units
///
/// What it does:
///   1. Finds the <c>NavMeshSurface</c> on the Environment root (or any active
///      NavMeshSurface in the scene). Adds one to the Environment root if none
///      exist yet.
///   2. Strips any stray <c>NavMeshAgent</c> from the Ground GameObject — the
///      ground is walked-on, not a walker.
///   3. Calls <see cref="NavMeshSurface.BuildNavMesh"/> synchronously.
///   4. Iterates every <see cref="NavMeshAgent"/> in the active scene and
///      <see cref="NavMeshAgent.Warp"/>s it to the nearest NavMesh point.
///      Units already on the mesh are left untouched.
///
/// Use this after running <c>Setup Clean Match Map</c> if the auto-bake at the
/// end of that tool failed for any reason (e.g. agent type missing), or after
/// hand-editing Mountains / Ground in the scene.
///
/// What it does NOT do:
///   • Modify ANY prefab on disk.
///   • Touch player units, resources, power, HUD, buildings, combat, aircraft,
///     construction, production, or selection logic.
///   • Re-bake the NavMesh via the legacy global pipeline. NavMeshSurface owns
///     the bake; the legacy <c>NavMeshBuilder</c> path is not used.
/// </summary>
public static class RebuildNavMeshAndSnapUnits
{
    [MenuItem("Tools/RTS/Environment/Rebuild NavMesh And Snap Units")]
    public static void Run()
    {
        Debug.Log("[MatchSetup] ── Rebuilding NavMesh ──");

        // ── 1. Locate the NavMeshSurface ────────────────────────────── //
        NavMeshSurface surface = ResolveOrCreateSurface();
        if (surface == null)
        {
            Debug.LogError("[MatchSetup] ✗ Could not create a NavMeshSurface. " +
                           "Run Tools → RTS → Match → Setup Clean Match Map first.");
            return;
        }

        // ── 2. Strip stray NavMeshAgent on the Ground ──────────────── //
        StripGroundAgents();

        // ── 3. Bake ─────────────────────────────────────────────────── //
        try
        {
            surface.BuildNavMesh();
            EditorUtility.SetDirty(surface);
            Debug.Log("[MatchSetup] ✓ NavMesh baked.");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[MatchSetup] ✗ NavMesh bake failed: {ex.Message}\n" +
                           "Please bake NavMesh after setup (Window → AI → Navigation → Bake).");
            return;
        }

        // ── 4. Snap every agent onto the new surface ───────────────── //
        int warped = SnapAllAgents();
        Debug.Log($"[MatchSetup] ✓ Warped {warped} NavMeshAgent(s) onto the new surface.");

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[MatchSetup] ── Done. ──");
    }

    // ------------------------------------------------------------------ //

    /// <summary>
    /// Looks for a <see cref="NavMeshSurface"/> on a GameObject named
    /// "Environment". Falls back to any active NavMeshSurface in the scene.
    /// Creates one on Environment if absent — and creates the Environment root
    /// if even that doesn't exist (rare; usually Setup Clean Match Map made it).
    /// </summary>
    private static NavMeshSurface ResolveOrCreateSurface()
    {
        GameObject env = GameObject.Find("Environment");
        if (env != null)
        {
            NavMeshSurface s = env.GetComponent<NavMeshSurface>();
            if (s == null)
            {
                s = Undo.AddComponent<NavMeshSurface>(env);
                s.collectObjects = CollectObjects.Children;
                s.useGeometry    = NavMeshCollectGeometry.PhysicsColliders;
                Debug.Log("[MatchSetup]   Added NavMeshSurface to Environment.");
            }
            return s;
        }

        NavMeshSurface any = Object.FindAnyObjectByType<NavMeshSurface>(FindObjectsInactive.Include);
        if (any != null) return any;

        Debug.LogWarning("[MatchSetup]   No Environment root in the scene — " +
                         "creating one with a NavMeshSurface. Run Setup Clean Match Map " +
                         "for a full environment build.");

        GameObject newEnv = new GameObject("Environment");
        Undo.RegisterCreatedObjectUndo(newEnv, "Create Environment");
        NavMeshSurface fresh = Undo.AddComponent<NavMeshSurface>(newEnv);
        fresh.collectObjects = CollectObjects.Children;
        fresh.useGeometry    = NavMeshCollectGeometry.PhysicsColliders;
        return fresh;
    }

    /// <summary>
    /// Removes any <see cref="NavMeshAgent"/> from objects on the Ground layer —
    /// the ground itself should never be an agent. This is a defensive sweep
    /// in case a future tool or hand-edit accidentally added one.
    /// </summary>
    private static void StripGroundAgents()
    {
        int groundLayer = LayerMask.NameToLayer("Ground");
        if (groundLayer < 0) return;

        NavMeshAgent[] all = Object.FindObjectsByType<NavMeshAgent>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        int stripped = 0;
        foreach (NavMeshAgent agent in all)
        {
            if (agent == null) continue;
            if (agent.gameObject.layer != groundLayer) continue;

            Undo.DestroyObjectImmediate(agent);
            stripped++;
        }

        if (stripped > 0)
            Debug.Log($"[MatchSetup]   Stripped {stripped} stray NavMeshAgent(s) from Ground objects.");
    }

    /// <summary>
    /// Walks every <see cref="NavMeshAgent"/> in the scene, finds the nearest
    /// NavMesh point within a 6-unit radius, and warps the agent onto it. Skips
    /// agents that are already on the mesh (<see cref="NavMeshAgent.isOnNavMesh"/>).
    /// Returns the number warped.
    /// </summary>
    private static int SnapAllAgents()
    {
        NavMeshAgent[] all = Object.FindObjectsByType<NavMeshAgent>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        int warped = 0;
        foreach (NavMeshAgent agent in all)
        {
            if (agent == null) continue;
            if (agent.isOnNavMesh) continue;

            if (NavMesh.SamplePosition(agent.transform.position, out NavMeshHit hit, 6f, NavMesh.AllAreas))
            {
                Undo.RecordObject(agent.transform, "Warp Agent To NavMesh");
                if (agent.Warp(hit.position))
                    warped++;
            }
            else
            {
                Debug.LogWarning($"[MatchSetup]   {agent.name}: no NavMesh point within 6u — " +
                                 "manually move this unit closer to walkable ground.");
            }
        }
        return warped;
    }
}
