using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click repair for APC infantry-transport wiring. Same shape as the other
/// "Repair X" tools: rebuilds the prefab from its canonical builder (which is
/// the single source of truth for stats + components), then patches in-scene
/// APC instances and reruns Setup HUD if the transport panel isn't present.
///
/// Menu: Tools → RTS → Vehicles → Repair APC Transport
///
/// What it does (idempotent — safe to re-run):
///   1. Rebuilds APCPrefab via <see cref="CreateAPCPrefab.Create"/> so the
///      <see cref="APCTransport"/> component + spec field values are present.
///   2. Walks every in-scene APC and force-applies APCTransport's spec values
///      (capacity = 6, heal rate = 5, enter range = 2.5) — addresses cases
///      where the field was hand-edited on a scene instance after the prefab
///      was instantiated.
///   3. If the in-scene RTSHUD lacks a <c>transportPanel</c> reference,
///      reruns <see cref="SetupRTSHUD.SetupHUD"/> to build the panel.
///
/// What it does NOT touch:
///   • Humvee / Tank / Missile Launcher prefabs.
///   • Non-APC scene units (filter is APCTransport presence).
///   • PlayerResourceManager / PowerManager / HUD layouts other than the
///     transport panel.
/// </summary>
public static class RepairAPCTransport
{
    [MenuItem("Tools/RTS/Vehicles/Repair APC Transport")]
    public static void Repair()
    {
        Debug.Log("[RepairAPCTransport] ── Running ──");

        // 1. Rebuild the APCPrefab from its canonical builder.
        CreateAPCPrefab.Create();
        AssetDatabase.Refresh();
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CreateAPCPrefab.PrefabPath);
        if (prefab == null)
        {
            Debug.LogError("[RepairAPCTransport] ✗ APCPrefab could not be created. Aborting.");
            return;
        }

        // 2. Patch in-scene APC instances. The new APCTransport component is
        //    added to existing instances automatically by prefab linkage, but
        //    Inspector-overridden field values (capacity / heal rate etc.) can
        //    drift — force them back to the canonical spec values.
        int patched = PatchSceneInstances();
        Debug.Log($"[RepairAPCTransport]   ✓ In-scene APC instances: {patched} updated.");

        // 3. HUD transport panel — rerun Setup HUD if the references aren't bound.
        EnsureHUDHasTransportPanel();

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        Debug.Log("[RepairAPCTransport] ✓ Done. APC spawns with 6 default Soldier " +
                  "passengers, heals at 5 HP/sec inside, emergency-unloads at 50% damage " +
                  "on death, and shows a 6-slot overhead indicator when selected.");
    }

    // ------------------------------------------------------------------ //

    private static int PatchSceneInstances()
    {
        int patched = 0;

        GameObject soldierPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/_Game/Prefabs/SoldierPrefab.prefab");

        APCTransport[] all =
            Object.FindObjectsByType<APCTransport>(FindObjectsSortMode.None);

        foreach (APCTransport t in all)
        {
            if (t == null) continue;
            bool dirty = false;

            if (t.capacity != 6)                              { t.capacity = 6; dirty = true; }
            if (!Mathf.Approximately(t.enterRange, 2.5f))     { t.enterRange = 2.5f; dirty = true; }
            if (!Mathf.Approximately(t.unloadSpacing, 1.5f))  { t.unloadSpacing = 1.5f; dirty = true; }
            if (!t.healPassengers)                            { t.healPassengers = true; dirty = true; }
            if (!Mathf.Approximately(t.passengerHealRate, 5f)) { t.passengerHealRate = 5f; dirty = true; }

            // Default-passenger wiring — SoldierPrefab reference + spawn flags.
            if (!t.spawnWithDefaultPassengers) { t.spawnWithDefaultPassengers = true; dirty = true; }
            if (t.defaultPassengerCount != 6)  { t.defaultPassengerCount = 6; dirty = true; }
            if (!t.fillToCapacityOnSpawn)      { t.fillToCapacityOnSpawn = true; dirty = true; }
            if (t.defaultPassengerPrefab == null && soldierPrefab != null)
            {
                t.defaultPassengerPrefab = soldierPrefab;
                dirty = true;
            }

            // Staggered-unload wiring — newly-added fields default to false on
            // pre-existing scene instances. Force the canonical values.
            if (!t.staggerUnload)                                    { t.staggerUnload = true; dirty = true; }
            if (!Mathf.Approximately(t.unloadInterval, 0.18f))       { t.unloadInterval = 0.18f; dirty = true; }
            if (!Mathf.Approximately(t.unloadMoveDistance, 2.5f))    { t.unloadMoveDistance = 2.5f; dirty = true; }
            if (!Mathf.Approximately(t.unloadSpreadAngle, 70f))      { t.unloadSpreadAngle = 70f; dirty = true; }
            if (!t.movePassengerAfterUnload)                         { t.movePassengerAfterUnload = true; dirty = true; }

            // Overhead passenger indicator child — add if missing.
            Transform existingIndicator = t.transform.Find("PassengerIndicator");
            if (existingIndicator == null)
            {
                GameObject indicator = new GameObject("PassengerIndicator");
                indicator.transform.SetParent(t.transform, worldPositionStays: false);
                APCPassengerIndicator pax = indicator.AddComponent<APCPassengerIndicator>();
                pax.transport            = t;
                pax.heightOffset         = 1.9f;
                pax.onlyShowWhenSelected = true;
                pax.hideWhenEmpty        = true;
                Undo.RegisterCreatedObjectUndo(indicator, "Add PassengerIndicator");
                dirty = true;
            }

            if (dirty) { EditorUtility.SetDirty(t); patched++; }
        }
        return patched;
    }

    private static void EnsureHUDHasTransportPanel()
    {
        RTSHUD hud = Object.FindAnyObjectByType<RTSHUD>(FindObjectsInactive.Include);
        if (hud == null)
        {
            Debug.LogWarning("[RepairAPCTransport] ⚠ No RTSHUD in the scene — " +
                             "run Tools → RTS → Setup HUD to create one.");
            return;
        }

        if (hud.transportPanel != null && hud.transportUnloadAllButton != null)
        {
            Debug.Log("[RepairAPCTransport]   = HUD already has the Transport panel.");
            return;
        }

        Debug.Log("[RepairAPCTransport]   Re-running Tools → RTS → Setup HUD to add the Transport panel.");
        SetupRTSHUD.SetupHUD();
    }
}
