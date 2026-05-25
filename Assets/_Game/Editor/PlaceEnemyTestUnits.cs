using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// Editor-only quick-spawn tools for dropping enemy test units into the active
/// scene. Used to test combat (Soldier, Humvee, Tank, MG Defense, RPG, Strike
/// Jet) without manually building enemy prefabs each time.
///
/// All entries live under <c>Tools → RTS → Enemy</c>:
///   • Place Enemy Infantry         — single dummy infantry capsule
///   • Place Enemy RPG Soldier      — instantiates EnemyRPGSoldierPrefab
///   • Place Enemy Vehicle Dummy    — single 400-HP vehicle target (cube)
///   • Place Enemy Infantry Group   — 3 infantry + 2 RPG (if prefab exists)
///   • Place Enemy Test Squad       — 4 infantry + 2 RPG + 1 vehicle dummy
///
/// What these tools deliberately do NOT do:
///   • Re-enable EnemyWaveSpawner or EnemyAIController.
///   • Add SelectableUnit (players can't click-select enemy units).
///   • Build full enemy AI — basic infantry are static dummies; only the
///     EnemyRPGSoldier prefab carries its own auto-attack controller.
///   • Touch player units, resources, power, HUD, buildings, aircraft, etc.
///   • Apply the player army color (TeamColorMarker / PlayerFactionManager).
///
/// Scene organisation:
///   All spawned units are parented to a single <c>EnemyTestUnits</c> root in
///   the scene (created on first use). This keeps the Hierarchy uncluttered
///   and lets you bulk-delete test units by deleting the parent.
///
/// Placement rules (shared by every tool):
///   1. If a GameObject is currently selected in the Hierarchy / Scene view,
///      spawn near it (small XZ offset).
///   2. Otherwise use the Scene-view camera pivot if available.
///   3. Otherwise fall back to <see cref="DefaultSpawnPos"/>.
///   4. Try NavMesh.SamplePosition first; fall back to the raw position with
///      a logged warning if no NavMesh was found.
/// </summary>
public static class PlaceEnemyTestUnits
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    private const string EnemyParentName = "EnemyTestUnits";
    private const string UnitLayerName   = "Unit";
    private const string RPGPrefabName   = "EnemyRPGSoldierPrefab";

    private static readonly Vector3 DefaultSpawnPos = new Vector3(15f, 0f, 15f);

    // Default offset from selection when "near selected" is the resolved
    // origin. Small enough to stay close, large enough not to overlap.
    private static readonly Vector3 SelectionOffset = new Vector3(3f, 0f, 3f);

    // Group spacing.
    private const float GroupSpacing = 2f;

    // Palette
    private static readonly Color EnemyInfantryRed   = new Color(0.85f, 0.18f, 0.18f);
    private static readonly Color EnemyVehicleRed    = new Color(0.55f, 0.12f, 0.10f);
    private static readonly Color EnemyVehicleAccent = new Color(0.30f, 0.08f, 0.07f);

    // Stats
    private const float InfantryHealth = 80f;
    private const float VehicleHealth  = 400f;

    // ------------------------------------------------------------------ //
    // Public menu items
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Enemy/Place Enemy Infantry")]
    public static void PlaceEnemyInfantry()
    {
        Debug.Log("[EnemyTools] ── Place Enemy Infantry ──");
        if (!ResolveUnitLayer(out int unitLayer)) return;

        Transform parent = GetOrCreateEnemyParent();
        Vector3   pos    = ResolveSpawnPos();

        GameObject go = SpawnInfantryDummy(parent, pos, unitLayer);
        FinishSpawn(go, "Placed Enemy Infantry.");
    }

    [MenuItem("Tools/RTS/Enemy/Place Enemy RPG Soldier")]
    public static void PlaceEnemyRPGSoldier()
    {
        Debug.Log("[EnemyTools] ── Place Enemy RPG Soldier ──");

        GameObject prefab = LoadRPGPrefab();
        if (prefab == null) return;     // LoadRPGPrefab logs the missing-prefab error

        Transform parent = GetOrCreateEnemyParent();
        Vector3   pos    = ResolveSpawnPos();

        GameObject go = SpawnRPGSoldier(prefab, parent, pos);
        FinishSpawn(go, "Placed Enemy RPG Soldier.");
    }

    [MenuItem("Tools/RTS/Enemy/Place Enemy Machine Gun Defense")]
    public static void PlaceEnemyMachineGunDefense()
    {
        Debug.Log("[EnemyTools] ── Place Enemy Machine Gun Defense ──");

        GameObject prefab = LoadEnemyMGDPrefab();
        if (prefab == null) return;     // helper logged the missing-prefab error

        Transform parent = GetOrCreateEnemyParent();
        Vector3   pos    = ResolveSpawnPos();

        GameObject go = SpawnEnemyMGD(prefab, parent, pos);
        FinishSpawn(go, "Placed Enemy Machine Gun Defense.");
    }

    [MenuItem("Tools/RTS/Enemy/Place Enemy Vehicle Dummy")]
    public static void PlaceEnemyVehicleDummy()
    {
        Debug.Log("[EnemyTools] ── Place Enemy Vehicle Dummy ──");
        if (!ResolveUnitLayer(out int unitLayer)) return;

        Transform parent = GetOrCreateEnemyParent();
        Vector3   pos    = ResolveSpawnPos();

        GameObject go = SpawnVehicleDummy(parent, pos, unitLayer);
        FinishSpawn(go, "Placed Enemy Vehicle Dummy.");
    }

    [MenuItem("Tools/RTS/Enemy/Place Enemy Infantry Group")]
    public static void PlaceEnemyInfantryGroup()
    {
        Debug.Log("[EnemyTools] ── Place Enemy Infantry Group ──");
        if (!ResolveUnitLayer(out int unitLayer)) return;

        Transform  parent = GetOrCreateEnemyParent();
        Vector3    centre = ResolveSpawnPos();
        GameObject rpg    = LoadRPGPrefab(silent: true);

        // 3 infantry + 2 RPGs, formation: 3 across the front, 2 behind.
        SpawnInfantryDummy(parent, centre + new Vector3(-GroupSpacing,    0f,  0f), unitLayer);
        SpawnInfantryDummy(parent, centre + new Vector3( 0f,              0f,  0f), unitLayer);
        SpawnInfantryDummy(parent, centre + new Vector3( GroupSpacing,    0f,  0f), unitLayer);

        if (rpg != null)
        {
            SpawnRPGSoldier(rpg, parent, centre + new Vector3(-GroupSpacing * 0.5f, 0f, -GroupSpacing));
            SpawnRPGSoldier(rpg, parent, centre + new Vector3( GroupSpacing * 0.5f, 0f, -GroupSpacing));
        }
        else
        {
            Debug.LogWarning($"[EnemyTools] {RPGPrefabName} missing — group placed without RPGs.");
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"[EnemyTools] Placed Enemy Infantry Group ({(rpg != null ? "3 infantry + 2 RPG" : "3 infantry")}).");
    }

    [MenuItem("Tools/RTS/Enemy/Place Enemy Test Squad")]
    public static void PlaceEnemyTestSquad()
    {
        Debug.Log("[EnemyTools] ── Place Enemy Test Squad ──");
        if (!ResolveUnitLayer(out int unitLayer)) return;

        Transform  parent = GetOrCreateEnemyParent();
        Vector3    centre = ResolveSpawnPos();
        GameObject rpg    = LoadRPGPrefab(silent: true);

        // Two ranks of infantry up front (4 total), 2 RPGs flanking the rear,
        // 1 vehicle dummy farthest back to test high-damage modifiers.
        SpawnInfantryDummy(parent, centre + new Vector3(-GroupSpacing * 1.5f, 0f, GroupSpacing), unitLayer);
        SpawnInfantryDummy(parent, centre + new Vector3(-GroupSpacing * 0.5f, 0f, GroupSpacing), unitLayer);
        SpawnInfantryDummy(parent, centre + new Vector3( GroupSpacing * 0.5f, 0f, GroupSpacing), unitLayer);
        SpawnInfantryDummy(parent, centre + new Vector3( GroupSpacing * 1.5f, 0f, GroupSpacing), unitLayer);

        if (rpg != null)
        {
            SpawnRPGSoldier(rpg, parent, centre + new Vector3(-GroupSpacing, 0f, -GroupSpacing));
            SpawnRPGSoldier(rpg, parent, centre + new Vector3( GroupSpacing, 0f, -GroupSpacing));
        }
        else
        {
            Debug.LogWarning($"[EnemyTools] {RPGPrefabName} missing — squad placed without RPGs.");
        }

        SpawnVehicleDummy(parent, centre + new Vector3(0f, 0f, -GroupSpacing * 2.5f), unitLayer);

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        string composition = rpg != null
            ? "4 infantry + 2 RPG + 1 vehicle dummy"
            : "4 infantry + 1 vehicle dummy (RPG prefab missing)";
        Debug.Log($"[EnemyTools] Placed Enemy Test Squad ({composition}).");
    }

    // ================================================================== //
    // Spawn primitives
    // ================================================================== //

    /// <summary>
    /// Spawns a stationary enemy infantry dummy: red capsule + Health(Enemy, 80) +
    /// UnitCategory.Infantry + HealthBar. No SelectableUnit, no UnitCombat — it's
    /// a hit target, nothing more.
    /// </summary>
    private static GameObject SpawnInfantryDummy(Transform parent, Vector3 desiredPos, int unitLayer)
    {
        Vector3 pos = SnapToNavMesh(desiredPos);

        GameObject dummy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        dummy.name = GetUniqueChildName(parent, "EnemyInfantry");
        dummy.transform.SetParent(parent, worldPositionStays: true);

        // Capsule pivot is at the body centre; capsule height is 2u by default.
        // Lift by 1 so feet sit on the ground plane.
        dummy.transform.position = pos + Vector3.up * 1.0f;
        dummy.layer              = unitLayer;

        ApplyMaterial(dummy.GetComponent<Renderer>(), EnemyInfantryRed, "EnemyInfantry_Red");

        Health health    = dummy.AddComponent<Health>();
        health.team      = Health.Team.Enemy;
        health.maxHealth = InfantryHealth;

        UnitCategory cat = dummy.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Infantry;

        AttachHealthBar(dummy, heightOffset: 1.4f);

        Undo.RegisterCreatedObjectUndo(dummy, "Place Enemy Infantry");
        return dummy;
    }

    /// <summary>
    /// Instantiates the EnemyMachineGunDefensePrefab as a scene instance (keeps
    /// the prefab link). The prefab already carries Health(Enemy, 650),
    /// UnitCategory.Building, BuildingTurretCombat — we just place + parent it.
    /// No NavMesh snap: it's a stationary building, the prefab's pivot already
    /// sits at ground y so the raw spawn pos works.
    /// </summary>
    private static GameObject SpawnEnemyMGD(GameObject prefab, Transform parent, Vector3 desiredPos)
    {
        Vector3 pos = desiredPos;
        pos.y = 0f;

        GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.name = GetUniqueChildName(parent, "EnemyMachineGunDefense");
        go.transform.SetParent(parent, worldPositionStays: true);
        go.transform.position = pos;
        go.transform.rotation = Quaternion.identity;

        Undo.RegisterCreatedObjectUndo(go, "Place Enemy Machine Gun Defense");
        return go;
    }

    /// <summary>
    /// Instantiates the EnemyRPGSoldierPrefab as a scene instance (keeps the
    /// prefab link). The prefab already carries Health(Enemy), RocketCombat,
    /// GroundAutoAttackController, HealthBar — we just place + parent it.
    /// </summary>
    private static GameObject SpawnRPGSoldier(GameObject prefab, Transform parent, Vector3 desiredPos)
    {
        Vector3 pos = SnapToNavMesh(desiredPos);

        GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.name = GetUniqueChildName(parent, "EnemyRPGSoldier");
        go.transform.SetParent(parent, worldPositionStays: true);
        go.transform.position = pos;
        go.transform.rotation = Quaternion.identity;

        Undo.RegisterCreatedObjectUndo(go, "Place Enemy RPG Soldier");
        return go;
    }

    /// <summary>
    /// Spawns a stationary enemy vehicle dummy: dark-red cube + Health(Enemy, 400) +
    /// UnitCategory.Vehicle + HealthBar. No movement, no combat — for testing the
    /// vehicle-damage modifier path.
    /// </summary>
    private static GameObject SpawnVehicleDummy(Transform parent, Vector3 desiredPos, int unitLayer)
    {
        Vector3 pos = SnapToNavMesh(desiredPos);

        GameObject dummy = GameObject.CreatePrimitive(PrimitiveType.Cube);
        dummy.name = GetUniqueChildName(parent, "EnemyVehicleDummy");
        dummy.transform.SetParent(parent, worldPositionStays: true);

        // Cube is 1x1x1 by default. Stretch slightly into a tank-ish silhouette.
        dummy.transform.localScale = new Vector3(2.0f, 1.0f, 2.8f);
        dummy.transform.position   = pos + Vector3.up * 0.5f;
        dummy.layer                = unitLayer;

        ApplyMaterial(dummy.GetComponent<Renderer>(), EnemyVehicleRed, "EnemyVehicleDummy_Red");

        // Small accent cube on top — visual cue it's a vehicle, not infantry.
        GameObject turret = GameObject.CreatePrimitive(PrimitiveType.Cube);
        turret.name = "DummyTurret";
        Object.DestroyImmediate(turret.GetComponent<Collider>());
        turret.transform.SetParent(dummy.transform, false);
        turret.transform.localScale    = new Vector3(0.55f, 0.55f, 0.55f);
        turret.transform.localPosition = new Vector3(0f, 0.65f, 0f);
        turret.layer                   = unitLayer;
        ApplyMaterial(turret.GetComponent<Renderer>(), EnemyVehicleAccent, "EnemyVehicleDummy_Accent");

        Health health    = dummy.AddComponent<Health>();
        health.team      = Health.Team.Enemy;
        health.maxHealth = VehicleHealth;

        UnitCategory cat = dummy.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Vehicle;

        AttachHealthBar(dummy, heightOffset: 1.6f);

        Undo.RegisterCreatedObjectUndo(dummy, "Place Enemy Vehicle Dummy");
        return dummy;
    }

    // ================================================================== //
    // Shared helpers
    // ================================================================== //

    /// <summary>
    /// Resolves the spawn origin using this priority:
    ///   1. Currently selected GameObject (offset slightly so it doesn't overlap).
    ///   2. SceneView pivot (the camera focus point).
    ///   3. <see cref="DefaultSpawnPos"/>.
    /// Returns the raw world position; <see cref="SnapToNavMesh"/> is applied
    /// per-spawn so each unit in a group can snap independently.
    /// </summary>
    private static Vector3 ResolveSpawnPos()
    {
        if (Selection.activeGameObject != null)
        {
            Vector3 p = Selection.activeGameObject.transform.position + SelectionOffset;
            p.y = 0f;
            return p;
        }

        SceneView sv = SceneView.lastActiveSceneView;
        if (sv != null)
        {
            Vector3 p = sv.pivot;
            p.y = 0f;
            return p;
        }

        return DefaultSpawnPos;
    }

    /// <summary>
    /// Snaps <paramref name="desired"/> onto the NavMesh within a 15-unit
    /// search radius. Returns the raw position with a warning log if no
    /// NavMesh was found.
    /// </summary>
    private static Vector3 SnapToNavMesh(Vector3 desired)
    {
        if (NavMesh.SamplePosition(desired, out NavMeshHit hit, 15f, NavMesh.AllAreas))
            return hit.position;

        Debug.LogWarning($"[EnemyTools] No NavMesh near {desired:F1}. Placing at the raw position " +
                         "— bake a NavMesh around your test area for clean spawning.");
        return desired;
    }

    /// <summary>
    /// Returns (or creates) the scene-root parent named <see cref="EnemyParentName"/>.
    /// First call per scene creates it and logs once.
    /// </summary>
    private static Transform GetOrCreateEnemyParent()
    {
        GameObject existing = GameObject.Find(EnemyParentName);
        if (existing != null) return existing.transform;

        GameObject go = new GameObject(EnemyParentName);
        Undo.RegisterCreatedObjectUndo(go, $"Create {EnemyParentName}");
        Debug.Log($"[EnemyTools] Created {EnemyParentName} parent.");
        return go.transform;
    }

    /// <summary>
    /// Returns a "BaseName_01" / "_02" / ... slot under <paramref name="parent"/>
    /// that doesn't collide with an existing child name. Two-digit zero-pad so
    /// they sort naturally in the Hierarchy.
    /// </summary>
    private static string GetUniqueChildName(Transform parent, string baseName)
    {
        for (int i = 1; i < 1000; i++)
        {
            string candidate = $"{baseName}_{i:D2}";
            if (parent.Find(candidate) == null) return candidate;
        }
        return baseName;
    }

    /// <summary>
    /// Loads <c>EnemyMachineGunDefensePrefab</c> from <c>Assets/_Game/Prefabs/</c>.
    /// If missing, runs <see cref="CreateEnemyMachineGunDefensePrefab.Create"/>
    /// to build it on the fly — the placement tool should "just work" without
    /// requiring a separate prep step.
    /// </summary>
    private static GameObject LoadEnemyMGDPrefab()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            CreateEnemyMachineGunDefensePrefab.PrefabPath);

        if (prefab == null)
        {
            Debug.Log("[EnemyTools] EnemyMachineGunDefensePrefab not found — creating it.");
            CreateEnemyMachineGunDefensePrefab.Create();
            AssetDatabase.Refresh();
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                CreateEnemyMachineGunDefensePrefab.PrefabPath);
        }

        if (prefab == null)
            Debug.LogError("[EnemyTools] EnemyMachineGunDefensePrefab could not be created.");

        return prefab;
    }

    /// <summary>
    /// Loads <c>EnemyRPGSoldierPrefab</c> from <c>Assets/_Game/Prefabs/</c>.
    /// When <paramref name="silent"/> is false (default), logs a clear error
    /// if the asset is missing; the caller bails after a null return.
    /// </summary>
    private static GameObject LoadRPGPrefab(bool silent = false)
    {
        string path = AssetDatabase
            .FindAssets($"{RPGPrefabName} t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith($"/{RPGPrefabName}.prefab"));

        if (string.IsNullOrEmpty(path))
        {
            if (!silent)
                Debug.LogError($"[EnemyTools] {RPGPrefabName} not found.\n" +
                               "  Run Tools → RTS → Units → Create Enemy RPG Soldier Prefab first.");
            return null;
        }

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null && !silent)
            Debug.LogError($"[EnemyTools] Could not load asset at {path}.");
        return prefab;
    }

    private static bool ResolveUnitLayer(out int unitLayer)
    {
        unitLayer = LayerMask.NameToLayer(UnitLayerName);
        if (unitLayer >= 0) return true;

        Debug.LogError($"[EnemyTools] ✗ Layer '{UnitLayerName}' does not exist.\n" +
                       "  Fix: Edit → Project Settings → Tags and Layers → add 'Unit' user layer.");
        return false;
    }

    /// <summary>
    /// Adds a child GameObject named "HealthBar" carrying a <see cref="HealthBar"/>
    /// component. HealthBar finds Health on transform.parent and builds its own
    /// visuals at runtime.
    /// </summary>
    private static void AttachHealthBar(GameObject owner, float heightOffset)
    {
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(owner.transform, false);
        HealthBar hb = bar.AddComponent<HealthBar>();
        hb.heightOffset = heightOffset;
    }

    /// <summary>
    /// Builds a fresh URP-Lit / Standard material and assigns it to
    /// <paramref name="r"/>'s sharedMaterial. Scene objects don't need the
    /// material persisted as an asset — Unity keeps the inline instance with
    /// the scene save.
    /// </summary>
    private static void ApplyMaterial(Renderer r, Color color, string matName)
    {
        if (r == null) return;
        Material m = new Material(ResolveLitShader()) { name = matName, color = color };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        r.sharedMaterial = m;
        r.shadowCastingMode = ShadowCastingMode.On;
    }

    /// <summary>Selects + frames the spawned unit and marks the scene dirty.</summary>
    private static void FinishSpawn(GameObject go, string logTail)
    {
        if (go == null) return;
        Selection.activeGameObject = go;

        SceneView sv = SceneView.lastActiveSceneView;
        if (sv != null) sv.FrameSelected();

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"[EnemyTools] {logTail}");
    }

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
