using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Idempotent repair pass for every <see cref="CommandCenter"/> in the
/// active scene AND for <c>Assets/_Game/Prefabs/CommandCenterPrefab.prefab</c>
/// on disk. Guarantees that Worker + Dozer production stays wired even
/// after editor rebuilds or HUD-rebuild churn.
///
/// Menu: Tools → RTS → Production → Repair CommandCenter Production
///
/// What it does (per CommandCenter found in the scene):
///   • Ensures a <see cref="CommandCenterProducer"/> component is present.
///   • Loads <c>WorkerPrefab.prefab</c> and <c>DozerPrefab.prefab</c> from
///     the project and assigns them to the producer.
///   • Re-applies the canonical costs (Worker 75, Dozer 150) if the field
///     somehow got cleared. Costs that were intentionally edited to a
///     non-zero non-default value are LEFT ALONE (we only overwrite 0s).
///
/// What it does for the prefab on disk (Phase 10):
///   • Loads <c>CommandCenterPrefab.prefab</c> via PrefabUtility, ensures
///     <see cref="CommandCenterProducer"/> exists, re-wires Worker + Dozer,
///     and saves the asset. This catches the case where the prefab was
///     re-created without the producer set up, so every Dozer-constructed
///     CC gets Worker + Dozer buttons immediately.
///
/// What it does NOT touch:
///   • <see cref="GameEntity.ownerPlayerId"/> — that's the multiplayer
///     match-setup tool's job. The CC prefab's owner is overwritten at
///     spawn time by the dispatcher's ApplyOwnership.
///   • Other producers (UnitProducer, VehicleFactoryProducer, Airfield).
///   • The RTSHUD canvas. The Dozer button is already created by
///     SetupRTSHUD as a permanent fixture; it auto-visibility-toggles
///     based on <see cref="CommandCenterProducer.CanProduceDozer"/>.
/// </summary>
public static class RepairCommandCenterProduction
{
    private const int DefaultWorkerCost = 75;
    private const int DefaultDozerCost  = 150;

    [MenuItem("Tools/RTS/Production/Repair CommandCenter Production")]
    public static void Run()
    {
        Debug.Log("[RepairProduction] ── Repairing CommandCenter Worker/Dozer production ──");

        // Load the source prefabs once.
        GameObject workerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/_Game/Prefabs/WorkerPrefab.prefab");
        GameObject dozerPrefab  = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/_Game/Prefabs/DozerPrefab.prefab");

        if (workerPrefab == null)
            Debug.LogError("[RepairProduction] ✗ Assets/_Game/Prefabs/WorkerPrefab.prefab not found.");
        if (dozerPrefab == null)
            Debug.LogError("[RepairProduction] ✗ Assets/_Game/Prefabs/DozerPrefab.prefab not found. " +
                           "Run Tools → RTS → Construction → Repair Construction System first.");

        // Walk every CommandCenter in the open scene (includes inactive ones,
        // so a GameplayWorldRoot-hidden Player1Base is still found). On Phase
        // 10 the MP map no longer spawns CCs at startup — that's expected, so
        // an empty scene-side result is a log not an error.
        CommandCenter[] all = Object.FindObjectsByType<CommandCenter>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        int repaired = 0;
        if (all.Length == 0)
        {
            Debug.Log("[RepairProduction] No CommandCenter in scene (Phase 10 Dozer-only " +
                      "start is normal) — repairing the prefab on disk instead.");
        }
        else
        {
            foreach (CommandCenter cc in all)
            {
                if (cc == null) continue;

                CommandCenterProducer producer = cc.GetComponent<CommandCenterProducer>();
                if (producer == null)
                {
                    producer = cc.gameObject.AddComponent<CommandCenterProducer>();
                    Debug.Log($"[RepairProduction]   added CommandCenterProducer to '{cc.name}'.");
                }

                if (workerPrefab != null) producer.workerPrefab = workerPrefab;
                if (dozerPrefab  != null) producer.dozerPrefab  = dozerPrefab;

                if (producer.workerCost <= 0) producer.workerCost = DefaultWorkerCost;
                if (producer.dozerCost  <= 0) producer.dozerCost  = DefaultDozerCost;

                EditorUtility.SetDirty(producer);
                repaired++;

                int ownerId = cc.OwnerPlayerId;
                Debug.Log($"[RepairProduction]   ✓ '{cc.name}' (owner {ownerId}) — " +
                          $"Worker={(producer.workerPrefab != null ? "OK" : "MISSING")}, " +
                          $"Dozer={(producer.dozerPrefab != null ? "OK" : "MISSING")}, " +
                          $"costs W={producer.workerCost} / D={producer.dozerCost}.");
            }

            if (repaired > 0)
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        // Phase 10 — also repair the CommandCenterPrefab.prefab asset on disk
        // so EVERY Dozer-built CC inherits Worker + Dozer wiring even if no
        // scene-baked CC exists to copy from.
        RepairPrefabOnDisk(workerPrefab, dozerPrefab);

        Debug.Log($"[RepairProduction] CommandCenter Worker/Dozer production repaired " +
                  $"({repaired} scene CC(s) processed + prefab refreshed).");
    }

    /// <summary>
    /// Loads <c>Assets/_Game/Prefabs/CommandCenterPrefab.prefab</c> via
    /// <see cref="PrefabUtility.LoadPrefabContents"/>, ensures a
    /// <see cref="CommandCenterProducer"/> is present with Worker + Dozer
    /// prefabs and default costs wired, then saves the prefab back.
    ///
    /// No-op (with a warning) if the prefab file doesn't exist — use
    /// Tools → RTS → Construction → Create CommandCenter Prefab first.
    /// </summary>
    private static void RepairPrefabOnDisk(GameObject workerPrefab, GameObject dozerPrefab)
    {
        string path = CreateCommandCenterPrefab.PrefabPath;
        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (asset == null)
        {
            Debug.LogWarning($"[RepairProduction] {path} not found on disk. Run " +
                             "Tools → RTS → Construction → Create CommandCenter Prefab " +
                             "(or the All-In-One tool) to build it first.");
            return;
        }

        GameObject contents = PrefabUtility.LoadPrefabContents(path);
        try
        {
            CommandCenterProducer producer = contents.GetComponent<CommandCenterProducer>();
            bool added = false;
            if (producer == null)
            {
                producer = contents.AddComponent<CommandCenterProducer>();
                added = true;
            }

            if (workerPrefab != null) producer.workerPrefab = workerPrefab;
            if (dozerPrefab  != null) producer.dozerPrefab  = dozerPrefab;
            if (producer.workerCost <= 0) producer.workerCost = DefaultWorkerCost;
            if (producer.dozerCost  <= 0) producer.dozerCost  = DefaultDozerCost;

            PrefabUtility.SaveAsPrefabAsset(contents, path);
            Debug.Log($"[RepairProduction]   ✓ prefab '{path}' " +
                      $"{(added ? "added" : "updated")} CommandCenterProducer — " +
                      $"Worker={(producer.workerPrefab != null ? "OK" : "MISSING")}, " +
                      $"Dozer={(producer.dozerPrefab != null ? "OK" : "MISSING")}, " +
                      $"costs W={producer.workerCost} / D={producer.dozerCost}.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
        AssetDatabase.SaveAssets();
    }
}
