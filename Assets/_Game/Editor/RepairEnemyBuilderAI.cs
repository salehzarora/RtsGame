using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One-click repair tool that guarantees every prefab + scene reference the
/// <see cref="EnemyBuildAI"/> needs to function. Creates missing prefabs,
/// wires the in-scene <see cref="EnemyBuildAI"/> field-by-field, and verifies
/// the existing player-side ConstructionSite prefab is loadable. Safe to
/// re-run — every step checks before creating.
///
/// Menu: Tools → RTS → Enemy → Repair Enemy Builder AI
///
/// What it does:
///   1. Ensures EnemyDozerPrefab exists (calls CreateEnemyDozerPrefab.Create).
///   2. Ensures EnemyMachineGunDefensePrefab exists (calls
///      CreateEnemyMachineGunDefensePrefab.Create).
///   3. Ensures ConstructionSitePrefab exists (calls
///      CreateConstructionSitePrefab.Create).
///   4. Creates EnemyPowerPlantPrefab + EnemyBarracksPrefab inline as primitive
///      cube prefabs (Building + Health(Enemy,800) + UnitCategory.Building +
///      HealthBar). NO SelectableBuilding, NO PowerPlant component, NO
///      UnitProducer — these are pure set-dressing for the build order.
///   5. Finds the in-scene EnemyBuildAI and wires:
///        - constructionSitePrefab
///        - enemyPowerPlantPrefab / enemyBarracksPrefab / enemyMGDefensePrefab
///        - enemyDozer  (auto-resolves any Health=Enemy DozerBuilder in the scene)
///
/// What it does NOT touch:
///   • Player prefabs / scripts.
///   • The HUD, PowerManager, PlayerResourceManager, BuildingPlacementManager.
///   • Any in-scene units beyond the Enemy Dozer reference assignment.
/// </summary>
public static class RepairEnemyBuilderAI
{
    private const string EnemyDozerPath        = "Assets/_Game/Prefabs/EnemyDozerPrefab.prefab";
    private const string EnemyPowerPlantPath   = "Assets/_Game/Prefabs/EnemyPowerPlantPrefab.prefab";
    private const string EnemyBarracksPath     = "Assets/_Game/Prefabs/EnemyBarracksPrefab.prefab";
    private const string EnemyMGDPath          = "Assets/_Game/Prefabs/EnemyMachineGunDefensePrefab.prefab";
    private const string ConstructionSitePath  = "Assets/_Game/Prefabs/ConstructionSitePrefab.prefab";

    private const string MatFolder = "Assets/_Game/Materials/EnemyDressBuildings";

    // Same dress-building stats EnemyBuildAI used in its earlier instant-spawn version.
    private const float DressHealth = 800f;

    private static readonly Color PowerPlantRed = new Color(0.55f, 0.18f, 0.10f);
    private static readonly Color BarracksRed   = new Color(0.40f, 0.12f, 0.10f);

    [MenuItem("Tools/RTS/Enemy/Repair Enemy Builder AI")]
    public static void Run()
    {
        Debug.Log("[RepairEnemyBuilderAI] ── Running ──");

        // 1. Enemy Dozer ----------------------------------------------------
        GameObject dozerPrefab = LoadOrCreate(EnemyDozerPath, CreateEnemyDozerPrefab.Create);
        if (dozerPrefab == null)
        {
            Debug.LogError("[RepairEnemyBuilderAI] ✗ EnemyDozerPrefab could not be created.");
            return;
        }
        Debug.Log($"[RepairEnemyBuilderAI]   ✓ {EnemyDozerPath}");

        // 2. Enemy MG Defense ---------------------------------------------
        GameObject mgdPrefab = LoadOrCreate(EnemyMGDPath, CreateEnemyMachineGunDefensePrefab.Create);
        if (mgdPrefab == null)
            Debug.LogWarning("[RepairEnemyBuilderAI] ⚠ EnemyMachineGunDefensePrefab could not be created.");
        else
            Debug.Log($"[RepairEnemyBuilderAI]   ✓ {EnemyMGDPath}");

        // 3. ConstructionSite prefab --------------------------------------
        GameObject sitePrefab = LoadOrCreate(ConstructionSitePath, CreateConstructionSitePrefab.Create);
        if (sitePrefab == null)
            Debug.LogWarning("[RepairEnemyBuilderAI] ⚠ ConstructionSitePrefab could not be created.");
        else
            Debug.Log($"[RepairEnemyBuilderAI]   ✓ {ConstructionSitePath}");

        // 4. Enemy dress buildings ----------------------------------------
        GameObject powerPrefab    = LoadOrCreateDressBuilding(EnemyPowerPlantPath,
                                        "EnemyPowerPlant",  new Vector3(3f, 2f,   3f),
                                        PowerPlantRed, "EnemyPP_Mat");
        GameObject barracksPrefab = LoadOrCreateDressBuilding(EnemyBarracksPath,
                                        "EnemyBarracks",   new Vector3(3f, 1.8f, 4f),
                                        BarracksRed,   "EnemyBarracks_Mat");

        // 5. Wire the in-scene EnemyBuildAI -------------------------------
        EnemyBuildAI ai = Object.FindAnyObjectByType<EnemyBuildAI>(FindObjectsInactive.Include);
        if (ai == null)
        {
            Debug.LogWarning("[RepairEnemyBuilderAI] ⚠ No EnemyBuildAI in the scene — " +
                             "run Tools → RTS → Match → Setup Clean Match Map first.");
        }
        else
        {
            bool dirty = false;

            if (ai.constructionSitePrefab != sitePrefab)
            {
                ai.constructionSitePrefab = sitePrefab;
                dirty = true;
            }
            if (ai.enemyPowerPlantPrefab != powerPrefab)
            {
                ai.enemyPowerPlantPrefab = powerPrefab;
                dirty = true;
            }
            if (ai.enemyBarracksPrefab != barracksPrefab)
            {
                ai.enemyBarracksPrefab = barracksPrefab;
                dirty = true;
            }
            if (ai.enemyMGDefensePrefab != mgdPrefab)
            {
                ai.enemyMGDefensePrefab = mgdPrefab;
                dirty = true;
            }

            // The Dozer is now produced by EnemyBuildAI at runtime — there is
            // no in-scene Dozer to wire at match start. We only need the
            // prefab reference so the bot can instantiate one when it can
            // afford it.
            if (ai.enemyDozerPrefab != dozerPrefab)
            {
                ai.enemyDozerPrefab = dozerPrefab;
                dirty = true;
            }

            // Defensive: clear any stale in-scene Dozer reference left over
            // from a previous setup pass that spawned one at start.
            if (ai.enemyDozer != null && FindEnemyDozerInScene() == null)
            {
                ai.enemyDozer = null;
                dirty = true;
            }

            if (dirty)
            {
                EditorUtility.SetDirty(ai);
                Debug.Log("[RepairEnemyBuilderAI]   ✓ Wired EnemyBuildAI prefab references.");
            }
            else
            {
                Debug.Log("[RepairEnemyBuilderAI]   = EnemyBuildAI references already complete.");
            }
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        Debug.Log("[RepairEnemyBuilderAI] ── Done. ──");
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Loads <paramref name="path"/>. If null, runs <paramref name="creator"/>
    /// (the existing prefab builder for that file) and re-loads. Returns the
    /// resulting asset, or null if creation also failed.
    /// </summary>
    private static GameObject LoadOrCreate(string path, System.Action creator)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab != null) return prefab;

        Debug.Log($"[RepairEnemyBuilderAI]   {Path.GetFileName(path)} missing — creating it.");
        creator();
        AssetDatabase.Refresh();
        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    /// <summary>
    /// Loads an enemy dress-building prefab from disk; builds a minimal one if
    /// missing. The on-disk prefab is a cube primitive on the Building layer
    /// with Health(Enemy, 800), Building, UnitCategory.Building, and HealthBar.
    /// No SelectableBuilding, no PowerPlant component, no UnitProducer — pure
    /// attackable set-dressing.
    /// </summary>
    private static GameObject LoadOrCreateDressBuilding(
        string path, string name, Vector3 scale, Color color, string matName)
    {
        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing != null)
        {
            Debug.Log($"[RepairEnemyBuilderAI]   ✓ {path}");
            return existing;
        }

        Debug.Log($"[RepairEnemyBuilderAI]   {Path.GetFileName(path)} missing — building it from primitives.");

        int buildingLayer = LayerMask.NameToLayer("Building");
        if (buildingLayer < 0)
        {
            Debug.LogError("[RepairEnemyBuilderAI] ✗ Building layer is missing. " +
                           "Add it in Project Settings → Tags and Layers.");
            return null;
        }

        Material mat = LoadOrCreateMat(matName, color);
        AssetDatabase.SaveAssets();

        // Empty root with pivot at ground; cube visual sits on top so the
        // building's pivot lines up with the ConstructionSite (which spawns
        // the final building at site.position, ground level). Matches the
        // EnemyMachineGunDefensePrefab's "pivot at ground" convention.
        GameObject root = new GameObject(name);
        try
        {
            root.layer = buildingLayer;
            root.transform.position = Vector3.zero;

            // Cube visual — half-Y offset so the bottom touches the root pivot.
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Body";
            visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = new Vector3(0f, scale.y * 0.5f, 0f);
            visual.transform.localScale    = scale;
            // Strip the auto-collider — root holds the hit-test BoxCollider.
            Object.DestroyImmediate(visual.GetComponent<Collider>());

            Renderer r = visual.GetComponent<Renderer>();
            if (r != null)
            {
                r.sharedMaterial    = mat;
                r.shadowCastingMode = ShadowCastingMode.On;
                r.receiveShadows    = true;
            }

            // Root-level box collider sized to the cube body, centred so the
            // collider fills the visible volume from ground up.
            BoxCollider col = root.AddComponent<BoxCollider>();
            col.size   = scale;
            col.center = new Vector3(0f, scale.y * 0.5f, 0f);

            Building b = root.AddComponent<Building>();
            b.buildingName = name;
            b.cost = 0;

            Health hp = root.AddComponent<Health>();
            hp.team   = Health.Team.Enemy;
            hp.maxHealth = DressHealth;

            UnitCategory cat = root.AddComponent<UnitCategory>();
            cat.category     = UnitCategory.Category.Building;

            // HealthBar — child GameObject; the component builds visuals at runtime.
            GameObject bar = new GameObject("HealthBar");
            bar.transform.SetParent(root.transform, false);
            HealthBar hb = bar.AddComponent<HealthBar>();
            hb.heightOffset = scale.y + 0.5f;

            // Layer everything to Building (cube child + bar children).
            SetLayerRecursive(root.transform, buildingLayer);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log($"[RepairEnemyBuilderAI]   ✓ Created {path}.");

            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static void SetLayerRecursive(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++)
            SetLayerRecursive(t.GetChild(i), layer);
    }

    private static DozerBuilder FindEnemyDozerInScene()
    {
        DozerBuilder[] all = Object.FindObjectsByType<DozerBuilder>(FindObjectsSortMode.None);
        foreach (DozerBuilder d in all)
        {
            if (d == null) continue;
            Health hp = d.GetComponent<Health>();
            if (hp != null && hp.team == Health.Team.Enemy)
                return d;
        }
        return null;
    }

    private static Material LoadOrCreateMat(string name, Color color)
    {
        EnsureFolder(MatFolder);
        string path = $"{MatFolder}/{name}.mat";

        Material m  = AssetDatabase.LoadAssetAtPath<Material>(path);
        Shader sh   = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

        if (m == null)
        {
            m = new Material(sh) { name = name };
            AssetDatabase.CreateAsset(m, path);
        }
        else if (m.shader == null || m.shader.name != sh.name)
        {
            m.shader = sh;
        }

        m.color = color;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        EditorUtility.SetDirty(m);
        return m;
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;

        string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        string leaf   = Path.GetFileName(folder);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
