using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One-click prefab patcher for the final soldier color setup. Idempotent —
/// safe to re-run.
///
/// Menu: Tools → RTS → Setup → Setup Soldier Final Colors
///
/// What it does:
///   1. Loads <c>Assets/_Game/Prefabs/SoldierPrefab.prefab</c>.
///   2. Walks to <c>SoldierVisualRoot</c> and finds the existing
///      <see cref="TeamColorApplier"/> component (it is REQUIRED — this tool
///      will not add one because the soldier setup steps earlier in the
///      project tree already do that).
///   3. Finds the Ch15 <c>SkinnedMeshRenderer</c> beneath the applier.
///   4. If material slot 0 is null (the "pink fallback" case), assigns a
///      shared <c>SoldierFixedDark.mat</c> material so the prefab no longer
///      shows pink in the editor preview. The asset is created once at
///      <c>Assets/_Game/Materials/SoldierFixedDark.mat</c> if it doesn't exist.
///   5. Configures the applier's renderer-slot lists:
///        teamColorSlots[0]  → Ch15, indexes = [1]
///        fixedColorSlots[0] → Ch15, indexes = [0], color = dark gray
///   6. Saves the prefab.
///
/// What it does NOT do:
///   • Touch UnitProducer, Barracks, NavMesh, Animator, Health, UnitCombat.
///   • Modify SoldierPrefab's gameplay scripts or component list.
///   • Delete the existing Element 1 material (TankCannon) — it is left in
///     place but will be tinted by the live team color at runtime.
/// </summary>
public static class SetupSoldierFinalColors
{
    // ------------------------------------------------------------------ //
    // Paths
    // ------------------------------------------------------------------ //

    private const string SoldierPrefabPath = "Assets/_Game/Prefabs/SoldierPrefab.prefab";
    private const string MaterialsFolder   = "Assets/_Game/Materials";
    private const string FixedDarkMatPath  = "Assets/_Game/Materials/SoldierFixedDark.mat";
    private const string Ch15ChildName     = "Ch15";

    // ------------------------------------------------------------------ //
    // Tunable defaults
    // ------------------------------------------------------------------ //

    private static readonly Color FixedDarkColor = new Color(0.10f, 0.10f, 0.10f);

    // ------------------------------------------------------------------ //
    // Entry
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Setup/Setup Soldier Final Colors")]
    public static void Run()
    {
        Debug.Log("[SetupSoldierFinalColors] ── Running ──");

        if (!File.Exists(SoldierPrefabPath))
        {
            Debug.LogError($"[SetupSoldierFinalColors] ✗ SoldierPrefab not found at '{SoldierPrefabPath}'.");
            return;
        }

        // 1. Make sure the shared fixed-dark material exists. We assign it to
        //    Ch15's null slot so the editor preview stops showing pink.
        Material darkMat = LoadOrCreateFixedDarkMaterial();

        GameObject root = PrefabUtility.LoadPrefabContents(SoldierPrefabPath);
        try
        {
            TeamColorApplier applier = root.GetComponentInChildren<TeamColorApplier>(includeInactive: true);
            if (applier == null)
            {
                Debug.LogError("[SetupSoldierFinalColors] ✗ No TeamColorApplier found anywhere inside " +
                               "SoldierPrefab. Add one to SoldierVisualRoot first.");
                return;
            }

            SkinnedMeshRenderer ch15 = FindCh15(root);
            if (ch15 == null)
            {
                Debug.LogError($"[SetupSoldierFinalColors] ✗ No SkinnedMeshRenderer named '{Ch15ChildName}' " +
                               "found beneath the applier. Make sure the Mixamo character is still in the prefab.");
                return;
            }

            // 2. Heal null slot 0 if needed. We use sharedMaterials so the
            //    assignment is serialized into the prefab override (instead
            //    of creating a one-shot runtime instance).
            Material[] mats = ch15.sharedMaterials;
            if (mats.Length > 0 && mats[0] == null)
            {
                mats[0] = darkMat;
                ch15.sharedMaterials = mats;
                Debug.Log($"[SetupSoldierFinalColors]   ✓ Healed Ch15 material slot 0 (was null) → " +
                          $"{Path.GetFileName(FixedDarkMatPath)}.");
            }
            else if (mats.Length > 0)
            {
                Debug.Log($"[SetupSoldierFinalColors]   = Ch15 material slot 0 already assigned " +
                          $"('{mats[0].name}') — left as-is.");
            }
            else
            {
                Debug.LogWarning("[SetupSoldierFinalColors]   ⚠ Ch15 has zero material slots — " +
                                 "nothing to fix on the renderer. Continuing with applier setup.");
            }

            // 3. Reconfigure the applier:
            //    - teamColorSlots[0]   : Ch15, indexes = [0]   ← dynamic team color
            //    - fixedColorSlots[0]  : Ch15, indexes = [1], color = dark gray
            applier.teamColorSlots = new List<RendererMaterialSlot>
            {
                new RendererMaterialSlot
                {
                    renderer = ch15,
                    materialIndexes = new List<int> { 0 },
                },
            };
            applier.fixedColorSlots = new List<FixedColorSlot>
            {
                new FixedColorSlot
                {
                    renderer = ch15,
                    materialIndexes = new List<int> { 1 },
                    fixedColor = FixedDarkColor,
                },
            };
            EditorUtility.SetDirty(applier);

            PrefabUtility.SaveAsPrefabAsset(root, SoldierPrefabPath);
            Debug.Log("[SetupSoldierFinalColors] ✓ Done.\n" +
                      $"  teamColorSlots[0]  → Ch15 material slot [0]  (dynamic team color)\n" +
                      $"  fixedColorSlots[0] → Ch15 material slot [1]  (fixed dark gray)\n" +
                      "  Enter Play with any menu color — slot 0 takes that color, slot 1 stays dark.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static SkinnedMeshRenderer FindCh15(GameObject root)
    {
        // Prefer the exact-name match for clarity. Fall back to the first
        // SkinnedMeshRenderer in the hierarchy so the tool still works if
        // the user renamed Ch15 later.
        SkinnedMeshRenderer[] all = root.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
        foreach (SkinnedMeshRenderer s in all)
        {
            if (s != null && s.gameObject.name == Ch15ChildName) return s;
        }
        return all.Length > 0 ? all[0] : null;
    }

    private static Material LoadOrCreateFixedDarkMaterial()
    {
        EnsureFolder(MaterialsFolder);

        Material existing = AssetDatabase.LoadAssetAtPath<Material>(FixedDarkMatPath);
        if (existing != null) return existing;

        Shader sh = ResolveLitShader();
        Material m = new Material(sh) { name = "SoldierFixedDark" };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", FixedDarkColor);
        if (m.HasProperty("_Color"))     m.SetColor("_Color",     FixedDarkColor);

        AssetDatabase.CreateAsset(m, FixedDarkMatPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[SetupSoldierFinalColors]   ✓ Created shared fixed-dark material at {FixedDarkMatPath}.");
        return m;
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
