using UnityEditor;
using UnityEngine;

/// <summary>
/// Read-only: prints the WORLD positions of every transform that
/// <see cref="AirUnitController"/> actually uses during the landing
/// sequence. Use this when a jet still lands in the wrong spot — the
/// output tells you which Transform is governing each phase, so you
/// don't have to guess.
///
/// Source-of-truth references (per AirUnitController code review):
///   Phase 1 — WaitingForLandingClearance / hold:
///     centred on <c>Airfield.landingApproachPoint</c>.
///   Phase 2 — LandingApproach:
///     jet MoveTowards <c>landingClearance.LandingStart</c> at altitude.
///   Phase 3 — FinalLanding:
///     jet descends from where it transitioned (≈ LandingStart XZ) toward
///     <c>landingClearance.LandingEnd</c>. Touchdown event fires when jet
///     reaches LandingEnd XZ at ground Y. ← LandingEnd = the visible touchdown.
///   Phase 4 — TaxiingToSlot:
///     follows <c>landingClearance.TaxiBackRoute</c> = [LandingExit,
///     per-slot Taxi, Slot].
///
/// Menu: Tools → RTS → Buildings → Print Airfield Runtime Landing Path
/// </summary>
public static class PrintAirfieldRuntimeLandingPath
{
    private const string PrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";

    [MenuItem("Tools/RTS/Buildings/Print Airfield Runtime Landing Path")]
    public static void Print()
    {
        Debug.Log("[RuntimeLandingPath] ─── AirfieldPrefab landing path (world positions) ───");

        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
        {
            Debug.LogError($"[RuntimeLandingPath] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[RuntimeLandingPath] ✗ LoadPrefabContents returned null.");
            return;
        }

        try
        {
            Airfield af = root.GetComponent<Airfield>();
            if (af == null)
            {
                Debug.LogError("[RuntimeLandingPath] ✗ No Airfield component on prefab root.");
                return;
            }

            // The prefab's root sits at world (0,0,0) in the prefab-contents
            // staging scene, so local == world here. In the live scene the
            // numbers will be shifted by the airfield's scene position, but
            // the OFFSETS between waypoints are identical. We print local
            // (which is what the prefab serializes) so the user can correlate
            // 1:1 with the values in editor tools like
            // ApplyManualAirfieldLandingTouchdown.
            Debug.Log($"[RuntimeLandingPath] Prefab path: {PrefabPath}");
            Debug.Log($"[RuntimeLandingPath] Phase 1 — WaitingForLandingClearance (holds around this point):");
            Debug.Log($"[RuntimeLandingPath]   landingApproachPoint = {LocPos(af.landingApproachPoint)}");
            Debug.Log($"[RuntimeLandingPath] Phase 2 — LandingApproach (in-air align target at flightAltitude):");
            Debug.Log($"[RuntimeLandingPath]   landingStartA = {LocPos(af.landingStartA)}");
            Debug.Log($"[RuntimeLandingPath]   landingStartB = {LocPos(af.landingStartB)} (reserved)");
            Debug.Log($"[RuntimeLandingPath] Phase 3 — FinalLanding (descent target = ACTUAL TOUCHDOWN):");
            Debug.Log($"[RuntimeLandingPath]   landingEndA = {LocPos(af.landingEndA)}  *** TOUCHDOWN ***");
            Debug.Log($"[RuntimeLandingPath]   landingEndB = {LocPos(af.landingEndB)} (reserved)");
            Debug.Log($"[RuntimeLandingPath] Phase 4 — TaxiingToSlot (TaxiBackRoute = [Exit, per-slot Taxi, Slot]):");
            Debug.Log($"[RuntimeLandingPath]   landingExitA = {LocPos(af.landingExitA)}");
            Debug.Log($"[RuntimeLandingPath]   landingExitB = {LocPos(af.landingExitB)} (reserved)");

            // Per-slot taxi-back chain that will be assembled by
            // Airfield.BuildLandingClearance for each parked aircraft.
            Debug.Log($"[RuntimeLandingPath] Per-slot taxi-back chain (Exit → Taxi_N → Slot_N):");
            if (af.slots != null && af.taxiPoints != null)
            {
                for (int i = 0; i < af.slots.Length; i++)
                {
                    Transform slot = af.slots[i];
                    Transform taxi = (i < af.taxiPoints.Length) ? af.taxiPoints[i] : null;
                    Debug.Log($"[RuntimeLandingPath]   Slot {i}: " +
                              $"LandingExit_A {LocPos(af.landingExitA)} " +
                              $"→ Taxi_{i} {LocPos(taxi)} " +
                              $"→ Slot_{i} {LocPos(slot)}");
                }
            }

            // Building-clipping sanity for the touchdown.
            if (af.landingEndA != null)
            {
                Vector3 td = af.landingEndA.localPosition;
                bool insideBox = Mathf.Abs(td.x) < 3.5f && Mathf.Abs(td.z) < 3.5f;
                if (insideBox)
                    Debug.LogError($"[RuntimeLandingPath]   ✗ Touchdown {td} is inside the building box " +
                                   "(|X|<3.5, |Z|<3.5) — jet will touch down inside the building.");
                else
                    Debug.Log($"[RuntimeLandingPath]   Touchdown {td} is OUTSIDE the building box ✓.");
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[RuntimeLandingPath] ─────────────────────────────────────────");
    }

    private static string LocPos(Transform t)
    {
        if (t == null) return "<missing>";
        Vector3 p = t.localPosition;
        return $"({p.x:F3}, {p.y:F3}, {p.z:F3})";
    }
}
