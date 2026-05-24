using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Wires the freshly-built RPGSoldierPrefab into the Barracks production
/// component. Pre-existing BarracksPrefab assets don't know about the RPG
/// Soldier — this tool patches them in-place without disturbing any other
/// configuration on the building.
///
/// Menu: Tools → RTS → Units → Repair Barracks RPG Production
///
/// What it does (idempotent — safe to re-run):
///   1. Locates RPGSoldierPrefab.prefab via AssetDatabase.
///   2. Locates the Barracks prefab (any prefab with a UnitProducer).
///   3. Assigns rpgSoldierPrefab on each matching prefab's UnitProducer.
///   4. Refreshes rpgSoldierCost to the spec default (120) without overwriting
///      a custom higher value the user may have set.
///   5. Saves the prefab(s).
///
/// What it does NOT touch:
///   • Existing soldierPrefab / soldierCost.
///   • Other building configuration, spawn points, layers.
///   • Scene-instance overrides on Barracks (those re-resolve at Play time).
///
/// Use when:
///   • You ran Create RPG Soldier Prefab and now need the Barracks to see it.
///   • You imported a Barracks prefab from a pre-RPG-Soldier branch and the
///     button shows "RPG Soldier - 0" or is hidden.
/// </summary>
public static class RepairBarracksRPGProduction
{
    private const string RPGSoldierPrefabName = "RPGSoldierPrefab";
    private const int    DefaultRPGCost       = 120;

    [MenuItem("Tools/RTS/Units/Repair Barracks RPG Production")]
    public static void Repair()
    {
        Debug.Log("[RepairBarracksRPGProduction] ── Running ──");

        // 1. Locate RPGSoldierPrefab.
        string rpgPath = AssetDatabase
            .FindAssets($"{RPGSoldierPrefabName} t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith($"/{RPGSoldierPrefabName}.prefab"));

        if (string.IsNullOrEmpty(rpgPath))
        {
            Debug.LogError($"[RepairBarracksRPGProduction] ✗ {RPGSoldierPrefabName}.prefab not found.\n" +
                           "  Run Tools → RTS → Units → Create RPG Soldier Prefab first.");
            return;
        }

        GameObject rpgPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(rpgPath);
        if (rpgPrefab == null)
        {
            Debug.LogError($"[RepairBarracksRPGProduction] ✗ Could not load asset at {rpgPath}.");
            return;
        }

        // 2. Find every prefab that has a UnitProducer (Barracks-shaped buildings).
        //    UnitProducer is exclusively a Barracks component; CommandCenterProducer
        //    and VehicleFactoryProducer are separate types, so this match is precise.
        string[] guids = AssetDatabase.FindAssets("t:Prefab");
        int patched = 0;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;

            UnitProducer producer = prefab.GetComponent<UnitProducer>();
            if (producer == null) continue;

            // 3. Edit via PrefabUtility so the change persists.
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                UnitProducer rootProducer = root.GetComponent<UnitProducer>();
                if (rootProducer == null) continue;

                bool changed = false;

                if (rootProducer.rpgSoldierPrefab != rpgPrefab)
                {
                    rootProducer.rpgSoldierPrefab = rpgPrefab;
                    changed = true;
                }

                // Set the default cost only if the existing value is 0 or unset —
                // preserve a user-tuned higher/lower number.
                if (rootProducer.rpgSoldierCost <= 0)
                {
                    rootProducer.rpgSoldierCost = DefaultRPGCost;
                    changed = true;
                }

                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    patched++;
                    Debug.Log($"[RepairBarracksRPGProduction]   ✓ {path}: rpgSoldierPrefab set, " +
                              $"rpgSoldierCost = {rootProducer.rpgSoldierCost}.");
                }
                else
                {
                    Debug.Log($"[RepairBarracksRPGProduction]   = {path}: already configured.");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        AssetDatabase.SaveAssets();

        if (patched == 0)
            Debug.Log("[RepairBarracksRPGProduction] ✓ Done. No Barracks prefabs needed patching.");
        else
            Debug.Log($"[RepairBarracksRPGProduction] ✓ Done. Patched {patched} Barracks prefab(s). " +
                      "Re-select an existing Barracks in the scene to refresh the production panel.");
    }
}
