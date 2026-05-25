using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tunes the existing RPG prefabs for snappy auto-attack reaction without
/// touching other unit types. Idempotent — re-runs simply re-apply the
/// values and report what changed.
///
/// Menu: Tools → RTS → Units → Tune RPG Reaction Speed
///
/// Targets (RPG units only):
///   • RPGSoldierPrefab
///   • EnemyRPGSoldierPrefab
///
/// What it applies:
///   • RocketCombat.fireImmediatelyOnNewTarget = true
///   • RocketCombat.firstShotDelay             = 0.05
///   • RocketCombat.attackCooldown             = 2.5  (only if currently 0; preserve tuning)
///   • GroundAutoAttackController.scanInterval = 0.10 (if the component is present)
///
/// Explicitly NOT touched:
///   • SoldierPrefab, HumveePrefab, ArtilleryTankPrefab — they keep the
///     default scanInterval (0.35 s). The RPG-specific tweak is meant to
///     make the rocket "snipe" feel responsive, not change every unit's
///     reaction profile.
///   • Aircraft, buildings, workers.
///
/// Use when:
///   • You imported pre-existing RPG prefabs and want the new fast-first-shot
///     behaviour persisted to disk instead of relying on the new field
///     defaults (which only apply to freshly-created components).
/// </summary>
public static class TuneRPGReactionSpeed
{
    private static readonly string[] TargetPrefabs =
    {
        "RPGSoldierPrefab",
        "EnemyRPGSoldierPrefab",
    };

    // Tuned values — keep in sync with CreateRPGSoldierPrefab / CreateEnemyRPGSoldierPrefab.
    private const bool  FireImmediatelyOnNewTarget = true;
    private const float FirstShotDelay             = 0.05f;
    private const float DefaultAttackCooldown      = 2.5f;
    private const float RPGScanInterval            = 0.10f;

    [MenuItem("Tools/RTS/Units/Tune RPG Reaction Speed")]
    public static void Run()
    {
        Debug.Log("[TuneRPGReactionSpeed] ── Running ──");

        int patched = 0;
        int missing = 0;

        foreach (string prefabName in TargetPrefabs)
        {
            string path = AssetDatabase
                .FindAssets($"{prefabName} t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith($"/{prefabName}.prefab"));

            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning($"[TuneRPGReactionSpeed]   ⚠ {prefabName}.prefab not found — skipping. " +
                                 "Run Tools → RTS → Units → Create RPG Soldier Prefab / Create Enemy RPG Soldier Prefab first.");
                missing++;
                continue;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            bool dirty = false;
            try
            {
                RocketCombat rc = root.GetComponent<RocketCombat>();
                if (rc == null)
                {
                    Debug.LogWarning($"[TuneRPGReactionSpeed]   ⚠ {prefabName}: no RocketCombat component. Skipping.");
                    continue;
                }

                if (rc.fireImmediatelyOnNewTarget != FireImmediatelyOnNewTarget)
                {
                    rc.fireImmediatelyOnNewTarget = FireImmediatelyOnNewTarget;
                    dirty = true;
                }

                if (!Mathf.Approximately(rc.firstShotDelay, FirstShotDelay))
                {
                    rc.firstShotDelay = FirstShotDelay;
                    dirty = true;
                }

                // Preserve a tuned cooldown — only restore the default if it's
                // currently zero (e.g. a half-initialised prefab).
                if (rc.attackCooldown <= 0f)
                {
                    rc.attackCooldown = DefaultAttackCooldown;
                    dirty = true;
                }

                // Snappier scan interval on the auto-attack controller, if present.
                // Other units (Soldier / Humvee / Tank) keep their default.
                GroundAutoAttackController ai = root.GetComponent<GroundAutoAttackController>();
                if (ai != null && !Mathf.Approximately(ai.scanInterval, RPGScanInterval))
                {
                    ai.scanInterval = RPGScanInterval;
                    dirty = true;
                }

                if (dirty)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    patched++;
                    Debug.Log($"[TuneRPGReactionSpeed]   ✓ {prefabName}: applied " +
                              $"fireImmediatelyOnNewTarget={FireImmediatelyOnNewTarget}, " +
                              $"firstShotDelay={FirstShotDelay}, " +
                              (ai != null ? $"scanInterval={RPGScanInterval}." : "(no auto-attack controller)."));
                }
                else
                {
                    Debug.Log($"[TuneRPGReactionSpeed]   = {prefabName}: already tuned.");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[TuneRPGReactionSpeed] ✓ Done. Patched: {patched}, missing: {missing}. " +
                  "RPG units now fire their first rocket ~0.1 s after acquiring a target.");
    }
}
