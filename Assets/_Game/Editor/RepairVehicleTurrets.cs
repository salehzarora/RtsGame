using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Wires up independent turret rotation + fire-while-moving on the existing
/// vehicle prefabs. Idempotent — re-runs safely refresh references and
/// settings without duplicating children.
///
/// Menu: Tools → RTS → Vehicles → Repair Vehicle Turrets
///
/// What it does on each known vehicle:
///   1. Locates the "Turret" child (already exists in the current prefabs).
///   2. Re-parents the barrel ("Cannon" on the tank, "MachineGun" on the
///      Humvee) and the "FirePoint" Transform under the turret so they yaw
///      with it instead of staying body-locked. SetParent uses
///      worldPositionStays=true, so the visual stays at the same world spot.
///   3. Adds VehicleTurretController to the root and assigns turret + firePoint.
///   4. Sets vehicle-appropriate turret turn speed / aim tolerance.
///   5. Flips UnitCombat.canFireWhileMoving on and applies cooldown / accuracy
///      multipliers tuned per vehicle class (tank = sluggish, humvee = nimble).
///
/// Targets (only these — no other prefab is touched):
///   • ArtilleryTankPrefab   (90 deg/s turret, 8° tolerance, accuracy 0.65 / cooldown ×1.4)
///   • HumveePrefab          (180 deg/s turret, 15° tolerance, accuracy 0.85 / cooldown ×1.1)
///
/// What it does NOT touch:
///   • Aircraft, infantry, workers, buildings.
///   • Existing NavMeshAgent / Health / SelectableUnit / collider sizes.
///   • Combat stats other than the fire-while-moving fields.
///   • Prefab GUIDs (so VehicleFactoryProducer's existing references survive).
/// </summary>
public static class RepairVehicleTurrets
{
    // Per-vehicle tuning bundle.
    private struct VehicleSpec
    {
        public string PrefabName;
        public string BarrelChildName;   // "Cannon" / "MachineGun"
        public float  TurretTurnSpeed;
        public float  AimToleranceDegrees;
        public float  MovingCooldownMultiplier;
        public float  MovingAccuracy;
    }

    private static readonly VehicleSpec[] Specs =
    {
        new VehicleSpec
        {
            PrefabName               = "ArtilleryTankPrefab",
            BarrelChildName          = "Cannon",
            TurretTurnSpeed          = 90f,
            AimToleranceDegrees      = 8f,
            MovingCooldownMultiplier = 1.4f,
            MovingAccuracy           = 0.65f,
        },
        new VehicleSpec
        {
            PrefabName               = "HumveePrefab",
            BarrelChildName          = "MachineGun",
            TurretTurnSpeed          = 180f,
            AimToleranceDegrees      = 15f,
            MovingCooldownMultiplier = 1.1f,
            MovingAccuracy           = 0.85f,
        },
    };

    [MenuItem("Tools/RTS/Vehicles/Repair Vehicle Turrets")]
    public static void Run()
    {
        Debug.Log("[RepairVehicleTurrets] ── Running ──");

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
                Debug.LogWarning($"[RepairVehicleTurrets]   ⚠ {spec.PrefabName}.prefab not found — skipping.");
                missing++;
                continue;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (!ApplyTo(root, spec))
                {
                    Debug.LogWarning($"[RepairVehicleTurrets]   ⚠ {spec.PrefabName}: skipped " +
                                     "(missing Turret/FirePoint — re-run Create *Prefab tools first).");
                    continue;
                }

                PrefabUtility.SaveAsPrefabAsset(root, path);
                patched++;
                Debug.Log($"[RepairVehicleTurrets]   ✓ {spec.PrefabName}: turret + fire-while-moving wired " +
                          $"(turn {spec.TurretTurnSpeed} deg/s, tol {spec.AimToleranceDegrees}°, " +
                          $"moveCD ×{spec.MovingCooldownMultiplier}, moveAcc {spec.MovingAccuracy:F2}).");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[RepairVehicleTurrets] ✓ Done. Patched: {patched}, missing: {missing}.");
    }

    // ------------------------------------------------------------------ //
    // Per-prefab patch
    // ------------------------------------------------------------------ //

    private static bool ApplyTo(GameObject root, VehicleSpec spec)
    {
        Transform turret    = root.transform.Find("Turret");
        Transform barrel    = root.transform.Find(spec.BarrelChildName);
        Transform firePoint = root.transform.Find("FirePoint");

        // Barrel may have been re-parented under Turret on a previous run.
        if (barrel == null && turret != null) barrel = turret.Find(spec.BarrelChildName);
        if (firePoint == null && turret != null) firePoint = turret.Find("FirePoint");

        if (turret == null || firePoint == null) return false;

        // Re-parent barrel + firepoint under the turret so they yaw with it.
        // worldPositionStays=true keeps the visual exactly where it was before.
        if (barrel != null && barrel.parent != turret)
            barrel.SetParent(turret, worldPositionStays: true);

        if (firePoint.parent != turret)
            firePoint.SetParent(turret, worldPositionStays: true);

        // Attach / refresh the turret controller.
        VehicleTurretController tc = root.GetComponent<VehicleTurretController>();
        if (tc == null) tc = root.AddComponent<VehicleTurretController>();

        tc.turret              = turret;
        tc.firePoint           = firePoint;
        tc.turretTurnSpeed     = spec.TurretTurnSpeed;
        tc.aimToleranceDegrees = spec.AimToleranceDegrees;
        tc.returnToForwardWhenIdle = true;
        tc.idleReturnSpeed     = 60f;

        // Tune UnitCombat for fire-while-moving. Preserve damage / range / etc.
        UnitCombat uc = root.GetComponent<UnitCombat>();
        if (uc != null)
        {
            uc.canFireWhileMoving       = true;
            uc.movingCooldownMultiplier = spec.MovingCooldownMultiplier;
            uc.movingAccuracy           = spec.MovingAccuracy;
            uc.stationaryAccuracy       = 1.0f;
            uc.movingSpeedThreshold     = 0.2f;

            // Prefer the turret's firePoint for tracers (UnitCombat reads it
            // automatically when the turret controller is present, but we
            // also clear UnitCombat.firePoint so old null-falls-through paths
            // don't accidentally fire from the body).
            uc.firePoint = firePoint;
        }
        else
        {
            Debug.LogWarning($"[RepairVehicleTurrets]   ⚠ {root.name}: no UnitCombat on root — " +
                             "fire-while-moving settings skipped.");
        }

        return true;
    }
}
