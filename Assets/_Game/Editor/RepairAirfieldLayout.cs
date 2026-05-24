using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Rebuilds the AirfieldPrefab layout (runway + apron + slots + taxi points +
/// runway lane markers + landing approach) without losing the user's tuned
/// production settings.
///
/// Menu: Tools → RTS → Air System → Repair Airfield Layout
///
/// What it does:
///   1. Loads AirfieldPrefab.prefab (running the builder first if missing).
///   2. Snapshots production fields the user might have customised
///      (strikeJetPrefab, strikeJetCost, maxConcurrentTakeoffs,
///      takeoffSpacingSeconds, queueDebugLogs).
///   3. Re-runs CreateAirfieldPrefab.Create() which rebuilds the prefab
///      from scratch with the new layout.
///   4. Reloads the prefab and restores the snapshotted values.
///
/// Use when:
///   • A prior phase's prefab is missing the taxi/queue/runway markers
///     introduced by the takeoff-queue system.
///   • Slot rotations or pad positions have been wiped/misconfigured.
///
/// Existing instances in the scene get the rebuilt layout automatically
/// because they are prefab-connected.
/// </summary>
public static class RepairAirfieldLayout
{
    private const string PrefabName = "AirfieldPrefab";

    [MenuItem("Tools/RTS/Air System/Repair Airfield Layout")]
    public static void Repair()
    {
        Debug.Log("[RepairAirfieldLayout] ── Running ──");

        string path = LocatePrefab(PrefabName);
        if (path == null)
        {
            Debug.Log("[RepairAirfieldLayout]   AirfieldPrefab missing — running builder.");
            CreateAirfieldPrefab.Create();
            path = LocatePrefab(PrefabName);
            if (path == null)
            {
                Debug.LogError("[RepairAirfieldLayout] ✗ Builder did not produce a prefab — aborting.");
                return;
            }
            Debug.Log($"[RepairAirfieldLayout] ✓ Created {path}.");
            return;
        }

        // --- 1. Snapshot production / queue settings ------------------ //
        Snapshot snap;
        GameObject existing = PrefabUtility.LoadPrefabContents(path);
        try
        {
            Airfield af = existing.GetComponent<Airfield>();
            snap = (af != null)
                ? new Snapshot
                {
                    strikeJetPrefab        = af.strikeJetPrefab,
                    strikeJetCost          = af.strikeJetCost,
                    maxConcurrentTakeoffs  = af.maxConcurrentTakeoffs,
                    takeoffSpacingSeconds  = af.takeoffSpacingSeconds,
                    queueDebugLogs         = af.queueDebugLogs,
                }
                : Snapshot.Default;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(existing);
        }

        // --- 2. Rebuild from scratch ---------------------------------- //
        CreateAirfieldPrefab.Create();

        // --- 3. Reapply the snapshot ---------------------------------- //
        GameObject rebuilt = PrefabUtility.LoadPrefabContents(path);
        try
        {
            Airfield af = rebuilt.GetComponent<Airfield>();
            if (af != null)
            {
                if (snap.strikeJetPrefab != null) af.strikeJetPrefab = snap.strikeJetPrefab;
                if (snap.strikeJetCost   > 0)      af.strikeJetCost   = snap.strikeJetCost;
                af.maxConcurrentTakeoffs = snap.maxConcurrentTakeoffs;
                af.takeoffSpacingSeconds = snap.takeoffSpacingSeconds;
                af.queueDebugLogs        = snap.queueDebugLogs;
                PrefabUtility.SaveAsPrefabAsset(rebuilt, path);
                Debug.Log($"[RepairAirfieldLayout] ✓ Layout rebuilt at {path}.\n" +
                          $"  Restored production: cost={af.strikeJetCost}, " +
                          $"queue maxConcurrent={af.maxConcurrentTakeoffs}, " +
                          $"spacing={af.takeoffSpacingSeconds}s, debugLogs={af.queueDebugLogs}.");
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(rebuilt);
        }

        AssetDatabase.SaveAssets();
        Debug.Log("[RepairAirfieldLayout] ✓ Done.");
    }

    private struct Snapshot
    {
        public GameObject strikeJetPrefab;
        public int        strikeJetCost;
        public int        maxConcurrentTakeoffs;
        public float      takeoffSpacingSeconds;
        public bool       queueDebugLogs;

        public static Snapshot Default => new Snapshot
        {
            strikeJetPrefab       = null,
            strikeJetCost         = 450,
            maxConcurrentTakeoffs = 2,
            takeoffSpacingSeconds = 1f,
            queueDebugLogs        = true,
        };
    }

    private static string LocatePrefab(string baseName)
    {
        return AssetDatabase.FindAssets($"{baseName} t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith($"/{baseName}.prefab"));
    }
}
