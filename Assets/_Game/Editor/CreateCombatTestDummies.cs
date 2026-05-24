using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Creates two scene-only enemy dummies for testing the damage-category system:
///
///   EnemyInfantryDummy   capsule, red,    Health=100, UnitCategory=Infantry
///   EnemyVehicleDummy    boxy,    red,    Health=300, UnitCategory=Vehicle
///
/// Both sit on the Unit layer with a collider so the existing UnitSelector
/// right-click attack path picks them up unchanged. Neither has movement,
/// combat, NavMeshAgent, or AI — they are static targets only.
///
/// Menu: Tools → RTS → Create Combat Test Dummies
///
/// Safe to re-run: each call creates fresh, uniquely-named dummies; previous
/// ones are not touched.
///
/// Use case:
///   • Soldier (Bullet) on Infantry dummy:  1.00×  ⇒ ~10 dmg/shot
///   • Soldier (Bullet) on Vehicle dummy:   0.35×  ⇒ ~3.5 dmg/shot
///   • Tank   (Cannon) on Infantry dummy:   0.25×  ⇒ 20 dmg/shot
///   • Tank   (Cannon) on Vehicle dummy:    1.00×  ⇒ 80 dmg/shot
/// </summary>
public static class CreateCombatTestDummies
{
    private const string UnitLayerName = "Unit";
    private static readonly Color EnemyRed = new Color(0.90f, 0.18f, 0.18f);

    private static readonly Vector3 InfantrySpawn = new Vector3(12f, 1f, 12f);
    private static readonly Vector3 VehicleSpawn  = new Vector3(18f, 1f, 12f);

    [MenuItem("Tools/RTS/Create Combat Test Dummies")]
    public static void Create()
    {
        int unitLayer = LayerMask.NameToLayer(UnitLayerName);
        if (unitLayer < 0)
        {
            Debug.LogError($"[CreateCombatTestDummies] ✗ Layer '{UnitLayerName}' does not exist.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add 'Unit'.");
            return;
        }

        Debug.Log("[CreateCombatTestDummies] ── Spawning dummies ──");

        GameObject inf = BuildInfantryDummy(unitLayer);
        GameObject veh = BuildVehicleDummy(unitLayer);

        Selection.activeGameObject = inf;
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log($"[CreateCombatTestDummies] ✓ '{inf.name}' (Infantry, 100 HP) at {InfantrySpawn} and " +
                  $"'{veh.name}' (Vehicle, 300 HP) at {VehicleSpawn} created.\n" +
                  "  Save the scene (Ctrl+S). Right-click each with a Soldier/Humvee/Tank to compare damage.");
    }

    // ------------------------------------------------------------------ //

    private static GameObject BuildInfantryDummy(int unitLayer)
    {
        GameObject dummy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        dummy.name = GetUniqueName("EnemyInfantryDummy");
        Undo.RegisterCreatedObjectUndo(dummy, "Create Enemy Infantry Dummy");

        dummy.transform.position = InfantrySpawn;
        dummy.layer = unitLayer;

        AssignRedMaterial(dummy, "EnemyInfantryDummy_Red");

        Health h          = dummy.AddComponent<Health>();
        h.team            = Health.Team.Enemy;
        h.maxHealth       = 100f;

        UnitCategory uc = dummy.AddComponent<UnitCategory>();
        uc.category     = UnitCategory.Category.Infantry;

        AddHealthBarChild(dummy);

        return dummy;
    }

    private static GameObject BuildVehicleDummy(int unitLayer)
    {
        // Boxy enemy "vehicle" — visually distinct from the infantry capsule.
        GameObject dummy = GameObject.CreatePrimitive(PrimitiveType.Cube);
        dummy.name = GetUniqueName("EnemyVehicleDummy");
        Undo.RegisterCreatedObjectUndo(dummy, "Create Enemy Vehicle Dummy");

        dummy.transform.position   = VehicleSpawn;
        dummy.transform.localScale = new Vector3(2.6f, 1.4f, 3.4f);
        dummy.layer                = unitLayer;

        AssignRedMaterial(dummy, "EnemyVehicleDummy_Red");

        Health h          = dummy.AddComponent<Health>();
        h.team            = Health.Team.Enemy;
        h.maxHealth       = 300f;

        UnitCategory uc = dummy.AddComponent<UnitCategory>();
        uc.category     = UnitCategory.Category.Vehicle;

        AddHealthBarChild(dummy);

        return dummy;
    }

    // ------------------------------------------------------------------ //

    private static void AddHealthBarChild(GameObject dummy)
    {
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(dummy.transform, false);
        bar.AddComponent<HealthBar>();
    }

    private static void AssignRedMaterial(GameObject go, string matName)
    {
        Renderer r = go.GetComponent<Renderer>();
        if (r == null) return;

        Material m = new Material(ResolveLitShader()) { name = matName, color = EnemyRed };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", EnemyRed);
        r.sharedMaterial = m;
    }

    private static string GetUniqueName(string baseName)
    {
        if (GameObject.Find(baseName) == null) return baseName;
        for (int i = 1; i < 1000; i++)
        {
            string candidate = $"{baseName} ({i})";
            if (GameObject.Find(candidate) == null) return candidate;
        }
        return baseName;
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
