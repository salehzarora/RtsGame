using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Brings the APCPrefab + every in-scene APC up to the latest turret design:
/// independent turret rotation via <see cref="VehicleTurretController"/>, plus
/// fire-while-moving on <see cref="UnitCombat"/> and
/// <see cref="GroundAutoAttackController"/>.
///
/// Menu: Tools → RTS → Vehicles → Repair APC Turret
///
/// What it does (idempotent — safe to re-run):
///   1. Rebuilds APCPrefab via <see cref="CreateAPCPrefab.Create"/>. The
///      builder is the single source of truth for the turret hierarchy
///      (empty pivot + child gun + fire points) and all the fire-while-moving
///      field values.
///   2. Walks every in-scene APC and force-applies the same field values on
///      its UnitCombat / GroundAutoAttackController / VehicleTurretController,
///      so vehicles produced from a stale prefab earlier in the session pick
///      up the new behaviour without being re-built.
///   3. Logs the resulting field values so a misconfigured Inspector is
///      visible in the console.
///
/// What it does NOT touch:
///   • Player resources / power / HUD.
///   • Humvee / Tank / Missile Launcher prefabs.
///   • Any non-APC unit in the scene.
/// </summary>
public static class RepairAPCTurret
{
    [MenuItem("Tools/RTS/Vehicles/Repair APC Turret")]
    public static void Repair()
    {
        Debug.Log("[RepairAPCTurret] ── Running ──");

        // ── 1. Rebuild the APCPrefab from the canonical builder ────────── //
        CreateAPCPrefab.Create();
        AssetDatabase.Refresh();

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CreateAPCPrefab.PrefabPath);
        if (prefab == null)
        {
            Debug.LogError("[RepairAPCTurret] ✗ APCPrefab could not be created. Aborting.");
            return;
        }

        // ── 2. Patch in-scene APC instances ───────────────────────────── //
        // VehicleTurretController is found by GetComponent in Awake — but
        // existing in-scene instances may already have an UnitCombat that ran
        // Awake before the prefab gained the controller. Force-resync by
        // ensuring the component + turret/firePoint refs + fire-while-moving
        // values are correct on every live APC.
        int patched = PatchSceneInstances();
        Debug.Log($"[RepairAPCTurret]   ✓ In-scene APC instances: {patched} updated.");

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        Debug.Log("[RepairAPCTurret] ✓ Done. APC now rotates its turret independently and " +
                  "fires while moving (cooldown ×1.10, accuracy 0.85).");
    }

    // ------------------------------------------------------------------ //
    // Scene instance patching
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Walks every <see cref="VehicleTurretController"/> in the scene that
    /// also has a sibling <see cref="UnitCombat"/> + APCAntiAirAuto (i.e. the
    /// APC signature) and force-applies the spec values. Also re-finds the
    /// "Turret" + "FirePoint_MG" children so the controller's references are
    /// valid even if a previous edit cleared them.
    /// </summary>
    private static int PatchSceneInstances()
    {
        int patched = 0;

        VehicleTurretController[] all =
            Object.FindObjectsByType<VehicleTurretController>(FindObjectsSortMode.None);

        foreach (VehicleTurretController turret in all)
        {
            if (turret == null) continue;

            // APC signature: has both UnitCombat (primary MG) and APCAntiAirAuto.
            // Other vehicles (Humvee/Tank/Missile Launcher) only carry a single
            // combat component — skip them so this tool only touches APCs.
            UnitCombat       combat = turret.GetComponent<UnitCombat>();
            APCAntiAirAuto   aa     = turret.GetComponent<APCAntiAirAuto>();
            if (combat == null || aa == null) continue;

            bool dirty = false;

            // Re-bind the turret child if the reference was lost.
            if (turret.turret == null)
            {
                Transform t = turret.transform.Find("Turret");
                if (t != null) { turret.turret = t; dirty = true; }
            }

            if (turret.firePoint == null && turret.turret != null)
            {
                Transform fp = turret.turret.Find("FirePoint_MG");
                if (fp != null) { turret.firePoint = fp; dirty = true; }
            }

            // Turret slew + tolerance.
            if (!Mathf.Approximately(turret.turretTurnSpeed, 220f))
            { turret.turretTurnSpeed = 220f; dirty = true; }
            if (!Mathf.Approximately(turret.aimToleranceDegrees, 18f))
            { turret.aimToleranceDegrees = 18f; dirty = true; }

            // UnitCombat fire-while-moving values.
            if (!combat.canFireWhileMoving)
            { combat.canFireWhileMoving = true; dirty = true; }
            if (!Mathf.Approximately(combat.movingCooldownMultiplier, 1.10f))
            { combat.movingCooldownMultiplier = 1.10f; dirty = true; }
            if (!Mathf.Approximately(combat.movingAccuracy, 0.85f))
            { combat.movingAccuracy = 0.85f; dirty = true; }
            if (!Mathf.Approximately(combat.stationaryAccuracy, 1.00f))
            { combat.stationaryAccuracy = 1.00f; dirty = true; }

            // GroundAutoAttackController fire-while-moving + detection radius.
            GroundAutoAttackController guard = turret.GetComponent<GroundAutoAttackController>();
            if (guard != null)
            {
                if (!guard.autoFireWhileMoving)
                { guard.autoFireWhileMoving = true; dirty = true; }
                if (!Mathf.Approximately(guard.detectionRadius, 14f))
                { guard.detectionRadius = 14f; dirty = true; }
            }

            if (dirty)
            {
                EditorUtility.SetDirty(turret);
                if (combat != null) EditorUtility.SetDirty(combat);
                if (guard  != null) EditorUtility.SetDirty(guard);
                patched++;
            }
        }

        return patched;
    }
}
