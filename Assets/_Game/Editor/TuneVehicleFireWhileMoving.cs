using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Enables the transit-fire branch on the turret vehicle prefabs so they can
/// auto-fire at en-route enemies during long manual move orders. Idempotent —
/// re-runs simply re-apply the per-prefab settings.
///
/// Menu: Tools → RTS → Vehicles → Tune Vehicle Fire While Moving
///
/// What it applies (only to the two vehicle prefabs):
///   • HumveePrefab          — detectionRadius = 13, autoFireWhileMoving = true
///   • ArtilleryTankPrefab   — detectionRadius = 16, autoFireWhileMoving = true
///
/// Infantry / aircraft / buildings are deliberately NOT touched. Soldier and
/// RPG Soldier keep the standard guard/leash behaviour; aircraft never had
/// the controller in the first place.
///
/// Prerequisites:
///   • Run Tools → RTS → Units → Add Ground Auto Attack To Prefabs first so
///     the GroundAutoAttackController component is present on each vehicle.
///   • Run Tools → RTS → Vehicles → Repair Vehicle Turrets so the turret
///     controller is wired up and UnitCombat.canFireWhileMoving = true.
///
/// What it does NOT touch:
///   • leashRadius / guardPosition logic — guard/leash still apply when the
///     vehicle is stationary.
///   • UnitCombat damage / cooldown / accuracy multipliers.
///   • Turret turn-speed / aim-tolerance.
///   • Prefab GUIDs.
/// </summary>
public static class TuneVehicleFireWhileMoving
{
    private struct VehicleSpec
    {
        public string PrefabName;
        public float  DetectionRadius;
    }

    private static readonly VehicleSpec[] Specs =
    {
        new VehicleSpec { PrefabName = "HumveePrefab",        DetectionRadius = 13f },
        new VehicleSpec { PrefabName = "ArtilleryTankPrefab", DetectionRadius = 16f },
    };

    [MenuItem("Tools/RTS/Vehicles/Tune Vehicle Fire While Moving")]
    public static void Run()
    {
        Debug.Log("[TuneVehicleFireWhileMoving] ── Running ──");

        int patched = 0;
        int missing = 0;

        foreach (VehicleSpec spec in Specs)
        {
            string path = AssetDatabase
                .FindAssets($"{spec.PrefabName} t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith($"/{spec.PrefabName}.prefab"));

            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning($"[TuneVehicleFireWhileMoving]   ⚠ {spec.PrefabName}.prefab not found — skipping.");
                missing++;
                continue;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            bool dirty = false;
            try
            {
                GroundAutoAttackController ai = root.GetComponent<GroundAutoAttackController>();
                if (ai == null)
                {
                    Debug.LogWarning($"[TuneVehicleFireWhileMoving]   ⚠ {spec.PrefabName}: " +
                                     "no GroundAutoAttackController. " +
                                     "Run Tools → RTS → Units → Add Ground Auto Attack To Prefabs first.");
                    continue;
                }

                if (!ai.autoFireWhileMoving)
                {
                    ai.autoFireWhileMoving = true;
                    dirty = true;
                }

                if (!Mathf.Approximately(ai.detectionRadius, spec.DetectionRadius))
                {
                    ai.detectionRadius = spec.DetectionRadius;
                    dirty = true;
                }

                // UnitCombat.canFireWhileMoving is what actually lets rounds release
                // mid-motion. The Repair Vehicle Turrets tool sets it, but defensive
                // here in case the user only ran this tool.
                UnitCombat uc = root.GetComponent<UnitCombat>();
                if (uc != null && !uc.canFireWhileMoving)
                {
                    uc.canFireWhileMoving = true;
                    dirty = true;
                    Debug.Log($"[TuneVehicleFireWhileMoving]   ↻ {spec.PrefabName}: " +
                              "also flipped UnitCombat.canFireWhileMoving = true.");
                }

                if (dirty)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    patched++;
                    Debug.Log($"[TuneVehicleFireWhileMoving]   ✓ {spec.PrefabName}: " +
                              $"autoFireWhileMoving=true, detectionRadius={spec.DetectionRadius}.");
                }
                else
                {
                    Debug.Log($"[TuneVehicleFireWhileMoving]   = {spec.PrefabName}: already tuned.");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[TuneVehicleFireWhileMoving] ✓ Done. Patched: {patched}, missing: {missing}. " +
                  "Vehicles will now fire at en-route enemies without cancelling their move order.");
    }
}
