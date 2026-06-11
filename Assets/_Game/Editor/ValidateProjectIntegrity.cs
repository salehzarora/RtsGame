using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// <summary>
/// Project-wide integrity validation suite. Read-only except the scene
/// re-serializer. Complements the per-system validators (Validate Soldier
/// Prefab, Validate Aircraft Orientation, Validate Dev Sandbox, …) with a
/// single pass over EVERY gameplay prefab and the open scene.
///
/// Menus (Tools → RTS → Validation):
///   • Validate All Gameplay Prefabs — per-prefab audit: missing scripts,
///     collider sanity (flags MeshColliders), unit/building/aircraft required
///     components, UnitCombat.firePoint wiring, GameEntity.prefabTypeId,
///     LODGroup health, TeamColorApplier slot ranges.
///   • Validate Open Scene Setup — exactly-one checks (camera, AudioListener,
///     EventSystem), manager presence/duplicates, NavMesh presence, duplicate
///     GameEntity ids.
///   • Find Missing Script Components — scans every prefab for the classic
///     "Missing (Mono Script)" component left by deleted scripts.
///   • Convert Scenes To Force-Text — re-saves every scene under Assets/Scenes
///     so stale binary-format scenes become text (the project's serialization
///     mode is already Force Text; three scenes predate it). The ONLY writing
///     menu in this file, and it only re-saves — no content changes.
///   • Validate All — prefabs + missing scripts in one run.
///
/// All results print as ✓/✗/⚠ console lines prefixed [ValidateAll].
/// </summary>
public static class ValidateProjectIntegrity
{
    private const string PrefabFolder = "Assets/_Game/Prefabs";

    private static int _problems;

    private static void P(string msg)  { Debug.Log("[ValidateAll]   ✓ " + msg); }
    private static void F(string msg)  { Debug.LogWarning("[ValidateAll]   ✗ " + msg); _problems++; }
    private static void W(string msg)  { Debug.LogWarning("[ValidateAll]   ⚠ " + msg); }

    // ================================================================== //
    // 1. Validate All Gameplay Prefabs
    // ================================================================== //

    [MenuItem("Tools/RTS/Validation/Validate All Gameplay Prefabs")]
    public static void ValidatePrefabs()
    {
        _problems = 0;
        Debug.Log("[ValidateAll] ─── Gameplay prefab audit ───");

        foreach (string path in Directory.GetFiles(PrefabFolder, "*.prefab").OrderBy(p => p))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Contains("_OLD_Backup")) continue;

            GameObject go = AssetDatabase.LoadAssetAtPath<GameObject>(path.Replace('\\', '/'));
            if (go == null) { F(name + ": failed to load"); continue; }

            ValidateOnePrefab(go, name);
        }

        Debug.Log(_problems == 0
            ? "[ValidateAll] ✓ Prefab audit complete — no problems."
            : $"[ValidateAll] ⚠ Prefab audit complete — {_problems} problem(s) above.");
    }

    private static void ValidateOnePrefab(GameObject go, string name)
    {
        // --- missing scripts anywhere in the hierarchy ----------------- //
        int missingScripts = 0;
        foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
        if (missingScripts > 0) F($"{name}: {missingScripts} missing-script component(s)");

        // --- collider sanity ------------------------------------------- //
        var meshCols = go.GetComponentsInChildren<MeshCollider>(true);
        if (meshCols.Length > 0)
            W($"{name}: {meshCols.Length} MeshCollider(s) — prefer Box/Capsule for RTS objects " +
              $"({string.Join(", ", meshCols.Select(c => c.gameObject.name))})");
        if (go.GetComponent<Collider>() == null && go.GetComponentInChildren<Collider>(true) == null)
            F($"{name}: no collider anywhere — not clickable/selectable");

        // --- classification-driven checks ------------------------------ //
        bool isUnit     = go.GetComponent<NavMeshAgent>() != null;
        bool isAircraft = go.GetComponent<AirUnitController>() != null;
        bool isBuilding = go.GetComponent<Building>() != null && !isUnit && !isAircraft;

        var ge = go.GetComponent<GameEntity>();
        if (ge == null) W($"{name}: no GameEntity (fine for pure-visual prefabs, wrong for gameplay objects)");
        else if (string.IsNullOrEmpty(ge.prefabTypeId))
            F($"{name}: GameEntity.prefabTypeId is EMPTY — networked spawn lookups will fail");

        if (isUnit)
        {
            if (go.GetComponent<Health>() == null)         F($"{name}: unit without Health");
            if (go.GetComponent<SelectableUnit>() == null && go.GetComponent<SelectableAircraft>() == null)
                W($"{name}: unit without Selectable component (enemy units are exempt by design)");
            var combat = go.GetComponent<UnitCombat>();
            if (combat != null && combat.firePoint == null)
                W($"{name}: UnitCombat.firePoint null — projectiles fall back to chest height");
            var agent = go.GetComponent<NavMeshAgent>();
            if (agent.speed <= 0f)                          F($"{name}: NavMeshAgent.speed is 0");
            if (agent.angularSpeed < 240f)
                W($"{name}: NavMeshAgent.angularSpeed {agent.angularSpeed} is sluggish for RTS (recommend ≥ 360)");
        }

        if (isAircraft)
        {
            var weapon = go.GetComponent<AircraftWeapon>();
            if (weapon == null) F($"{name}: aircraft without AircraftWeapon");
            if (go.GetComponent<AircraftVisualOrientation>() == null)
                W($"{name}: aircraft without AircraftVisualOrientation (no banking/pitch visuals)");
        }

        if (isBuilding)
        {
            if (go.GetComponent<Health>() == null) F($"{name}: building without Health");
        }

        // --- LODGroup health ------------------------------------------- //
        foreach (LODGroup lod in go.GetComponentsInChildren<LODGroup>(true))
        {
            if (lod.size < 0.5f)
                F($"{name}: LODGroup on '{lod.gameObject.name}' size={lod.size:F3} — too small, will cull (set ~2)");
            LOD[] lods = lod.GetLODs();
            for (int i = 0; i < lods.Length; i++)
                if (lods[i].renderers == null || lods[i].renderers.Length == 0 ||
                    lods[i].renderers.All(r => r == null))
                    F($"{name}: LODGroup on '{lod.gameObject.name}' LOD{i} has no renderers");
        }

        // --- TeamColorApplier slot ranges ------------------------------- //
        foreach (TeamColorApplier tca in go.GetComponentsInChildren<TeamColorApplier>(true))
        {
            foreach (var slot in tca.teamColorSlots)
            {
                if (slot == null || slot.renderer == null)
                { F($"{name}: TeamColorApplier has a null renderer slot"); continue; }
                int slots = slot.renderer.sharedMaterials?.Length ?? 0;
                foreach (int idx in slot.materialIndexes)
                    if (idx >= slots)
                        F($"{name}: TeamColorApplier targets material index {idx} but '{slot.renderer.name}' has only {slots} slot(s)");
            }
        }
    }

    // ================================================================== //
    // 2. Validate Open Scene Setup
    // ================================================================== //

    [MenuItem("Tools/RTS/Validation/Validate Open Scene Setup")]
    public static void ValidateScene()
    {
        _problems = 0;
        Scene scene = SceneManager.GetActiveScene();
        Debug.Log($"[ValidateAll] ─── Scene audit: '{scene.name}' ───");

        int cams = Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                         .Count(c => c.targetTexture == null); // render-texture cams (minimap) excluded
        if (cams == 0) F("no active screen camera");
        else if (cams > 1) W($"{cams} active screen cameras — RTS expects exactly one main view");
        else P("exactly one screen camera");

        var listeners = Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (listeners.Length != 1) F($"{listeners.Length} AudioListeners (must be exactly 1)");
        else P("exactly one AudioListener");

        var eventSystems = Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (eventSystems.Length > 1) F($"{eventSystems.Length} EventSystems — duplicates fight for input");
        else if (eventSystems.Length == 1) P("exactly one EventSystem");
        else W("no EventSystem — UI clicks dead (fine for menu-less test scenes)");

        CheckSingleton<UnitSelector>("UnitSelector");
        CheckSingleton<BuildingPlacementManager>("BuildingPlacementManager");

        var prms = Object.FindObjectsByType<PlayerResourceManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var dupOwners = prms.GroupBy(p => p.ownerPlayerId).Where(g => g.Count() > 1).ToList();
        foreach (var g in dupOwners)
            F($"{g.Count()} PlayerResourceManagers share ownerPlayerId={g.Key} — bank registration will collide");
        if (dupOwners.Count == 0 && prms.Length > 0) P($"{prms.Length} PlayerResourceManager(s), unique owner ids");

        if (NavMesh.SamplePosition(Vector3.zero, out _, 100f, NavMesh.AllAreas)) P("NavMesh present near origin");
        else W("no NavMesh within 100 m of origin — units cannot path (bake it)");

        // duplicate GameEntity ids (scene-baked stamping errors)
        var seen = new Dictionary<string, string>();
        foreach (GameEntity e in Object.FindObjectsByType<GameEntity>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (string.IsNullOrEmpty(e.EntityId)) continue;
            if (seen.TryGetValue(e.EntityId, out string other))
                F($"duplicate GameEntity id '{e.EntityId}' on '{e.name}' and '{other}'");
            else seen[e.EntityId] = e.name;
        }

        Debug.Log(_problems == 0
            ? "[ValidateAll] ✓ Scene audit complete — no problems."
            : $"[ValidateAll] ⚠ Scene audit complete — {_problems} problem(s) above.");
    }

    private static void CheckSingleton<T>(string label) where T : Object
    {
        int n = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        if (n > 1) F($"{n} {label} instances — should be exactly one");
        else if (n == 1) P($"one {label}");
        else W($"no {label} in scene (fine for menu scenes)");
    }

    // ================================================================== //
    // 3. Find Missing Script Components (project-wide prefabs)
    // ================================================================== //

    [MenuItem("Tools/RTS/Validation/Find Missing Script Components")]
    public static void FindMissingScripts()
    {
        _problems = 0;
        Debug.Log("[ValidateAll] ─── Missing-script sweep (all prefabs under Assets/_Game) ───");
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Game" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            {
                int n = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                if (n > 0) F($"{path} → '{t.name}': {n} missing script(s)");
            }
        }
        Debug.Log(_problems == 0
            ? "[ValidateAll] ✓ No missing scripts in any prefab."
            : $"[ValidateAll] ⚠ {_problems} object(s) with missing scripts.");
    }

    // ================================================================== //
    // 4. Convert Scenes To Force-Text (the only WRITE operation here)
    // ================================================================== //

    [MenuItem("Tools/RTS/Validation/Convert Scenes To Force-Text")]
    public static void ConvertScenesToText()
    {
        if (!EditorUtility.DisplayDialog(
                "Re-save all scenes?",
                "Opens and re-saves every scene in Assets/Scenes so stale binary-format " +
                "scenes are rewritten as text (project serialization mode is already Force Text).\n\n" +
                "No content is changed — Unity just re-serializes. The currently open scene " +
                "is restored afterwards. Continue?",
                "Re-save", "Cancel"))
            return;

        string restore = SceneManager.GetActiveScene().path;
        Scene cur = SceneManager.GetActiveScene();
        if (cur.isDirty && !EditorSceneManager.SaveScene(cur))
        { Debug.LogError("[ValidateAll] ✗ Could not save current scene — aborting."); return; }

        foreach (string path in Directory.GetFiles("Assets/Scenes", "*.unity"))
        {
            string p = path.Replace('\\', '/');
            Scene s = EditorSceneManager.OpenScene(p, OpenSceneMode.Single);
            EditorSceneManager.MarkSceneDirty(s);
            bool ok = EditorSceneManager.SaveScene(s);
            Debug.Log($"[ValidateAll]   {(ok ? "✓ re-saved" : "✗ SAVE FAILED")}: {p}");
        }

        if (!string.IsNullOrEmpty(restore) && File.Exists(restore))
            EditorSceneManager.OpenScene(restore, OpenSceneMode.Single);
        Debug.Log("[ValidateAll] ✓ Scene re-serialization pass complete.");
    }

    // ================================================================== //
    // 5. Validate All
    // ================================================================== //

    [MenuItem("Tools/RTS/Validation/Validate All")]
    public static void ValidateEverything()
    {
        ValidatePrefabs();
        FindMissingScripts();
        Debug.Log("[ValidateAll] ── Done. Also run 'Validate Open Scene Setup' with your gameplay scene open. ──");
    }
}
