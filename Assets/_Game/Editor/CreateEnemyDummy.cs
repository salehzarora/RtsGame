using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One-click editor tool that spawns a stationary enemy target into the active
/// scene so player Soldier units can be tested against the existing combat
/// system.
///
/// Menu: Tools → RTS → Create Enemy Dummy
///
/// The dummy has the minimum components required to be a valid attack target:
///   • Health  (Team = Enemy, Max Health = 100)
///   • CapsuleCollider  (so the right-click raycast on the Unit layer can hit it)
///   • Layer = Unit
///   • Red material on the body
///
/// It deliberately does NOT have:
///   • SelectableUnit       — the player cannot select it
///   • UnitMovement         — it does not move
///   • UnitCombat           — it does not fight back
///   • NavMeshAgent         — no pathfinding
///   • EnemyAIController    — no AI
///   • WorkerGatherer / SelectableBuilding / etc.
///
/// Safe to re-run: each invocation creates a fresh "EnemyDummy" (or
/// "EnemyDummy (1)", "EnemyDummy (2)", … if one already exists) so previous
/// dummies are never overwritten or deleted.
/// </summary>
public static class CreateEnemyDummy
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    private const string DummyName       = "EnemyDummy";
    private const string UnitLayerName   = "Unit";
    private const float  DummyMaxHealth  = 100f;
    private static readonly Vector3 DefaultSpawnPos = new Vector3(15f, 1f, 15f);
    private static readonly Color   EnemyRed        = new Color(0.90f, 0.18f, 0.18f);

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Create Enemy Dummy")]
    public static void Create()
    {
        Debug.Log("[CreateEnemyDummy] ── Spawning enemy dummy ──");

        // ── 1. Resolve the "Unit" layer up-front so we fail fast if missing ──
        int unitLayer = LayerMask.NameToLayer(UnitLayerName);
        if (unitLayer < 0)
        {
            Debug.LogError(
                $"[CreateEnemyDummy] ✗ Layer '{UnitLayerName}' does not exist.\n" +
                "  Fix: Edit → Project Settings → Tags and Layers → " +
                "add a User Layer named 'Unit', then re-run this tool.");
            return;
        }

        // ── 2. Body — capsule primitive in the scene ──────────────────── //
        GameObject dummy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        dummy.name = GetUniqueName(DummyName);
        Undo.RegisterCreatedObjectUndo(dummy, "Create Enemy Dummy");

        // Position: hover at y = 1 so the capsule sits on the ground plane.
        dummy.transform.position = DefaultSpawnPos;
        dummy.layer              = unitLayer;

        // The primitive ships with a CapsuleCollider — leave it; it's the
        // hit surface the right-click raycast on the Unit layer needs.

        // ── 3. Red material on the body ───────────────────────────────── //
        Renderer bodyRenderer = dummy.GetComponent<Renderer>();
        if (bodyRenderer != null)
        {
            // sharedMaterial = new instance is fine in the editor; this is a
            // scene object, not a prefab asset, so we do not need to persist
            // the material as an asset.
            Material redMat = new Material(ResolveLitShader())
            {
                name = "EnemyDummy_Red",
                color = EnemyRed
            };
            if (redMat.HasProperty("_BaseColor"))
                redMat.SetColor("_BaseColor", EnemyRed);

            bodyRenderer.sharedMaterial = redMat;
        }

        // ── 4. Health (Team = Enemy, 100 HP) ──────────────────────────── //
        Health health     = dummy.AddComponent<Health>();
        health.team       = Health.Team.Enemy;
        health.maxHealth  = DummyMaxHealth;

        // ── 5. Finalise: select it & mark scene dirty so the save sticks ──
        Selection.activeGameObject = dummy;
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log(
            $"[CreateEnemyDummy] ✓ '{dummy.name}' created at {DefaultSpawnPos} " +
            $"(Layer = Unit, Health = {DummyMaxHealth}, Team = Enemy).\n" +
            "  Test: produce a Soldier, select it, right-click the dummy.\n" +
            "  The dummy is a static target — it does not move or fight back.");
    }

    // ------------------------------------------------------------------ //
    // Cleanup tool — strips legacy "EnemyMarker" children from existing dummies
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Old versions of this tool added a small floating red cube ("EnemyMarker")
    /// as a child of every dummy. The AttackTargetMarker now owns all in-world
    /// red-cube visuals, so the static one is redundant and confusing.
    ///
    /// This menu walks the open scene and deletes every child GameObject named
    /// "EnemyMarker". Idempotent — safe to run when none exist.
    /// </summary>
    [MenuItem("Tools/RTS/Strip Legacy Enemy Markers")]
    public static void StripLegacyMarkers()
    {
        Transform[] all = Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        int removed = 0;
        foreach (Transform t in all)
        {
            if (t == null) continue;
            if (t.name != "EnemyMarker") continue;

            Undo.DestroyObjectImmediate(t.gameObject);
            removed++;
        }

        if (removed > 0)
        {
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[CreateEnemyDummy] ✓ Removed {removed} legacy 'EnemyMarker' " +
                      "child(ren). Save the scene (Ctrl+S) to persist.");
        }
        else
        {
            Debug.Log("[CreateEnemyDummy] No legacy 'EnemyMarker' objects found — nothing to do.");
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns <paramref name="baseName"/> if no scene object has that name,
    /// otherwise "baseName (1)", "baseName (2)", … so re-running the tool
    /// never overwrites a previous dummy.
    /// </summary>
    private static string GetUniqueName(string baseName)
    {
        if (GameObject.Find(baseName) == null) return baseName;

        for (int i = 1; i < 1000; i++)
        {
            string candidate = $"{baseName} ({i})";
            if (GameObject.Find(candidate) == null) return candidate;
        }
        return baseName; // hard fallback — Unity will append its own suffix
    }

    /// <summary>
    /// Picks the right Lit shader for the active render pipeline so the
    /// material never appears magenta. Mirrors the resolver used in
    /// <c>UpgradeSoldierVisual</c>.
    /// </summary>
    private static Shader ResolveLitShader()
    {
        RenderPipelineAsset rp = GraphicsSettings.defaultRenderPipeline;
        bool isURP = rp != null && rp.GetType().Name.Contains("Universal");

        Shader urp = Shader.Find("Universal Render Pipeline/Lit");
        Shader std = Shader.Find("Standard");

        if (isURP && urp != null) return urp;
        if (!isURP && std != null) return std;
        return urp ?? std ?? Shader.Find("Hidden/InternalErrorShader");
    }
}