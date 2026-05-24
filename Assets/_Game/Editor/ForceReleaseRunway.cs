using UnityEditor;
using UnityEngine;

/// <summary>
/// Debug-only runway unlocker. Scans every Airfield in the active scene and
/// asks each one to drop any lingering runway-busy state.
///
/// Menu: Tools → RTS → Air System → Force Release Runway
///
/// What it does:
///   1. Finds all Airfield instances in the scene (active + inactive).
///   2. Reports each one's current runway mode + owner BEFORE the release.
///   3. Calls <see cref="Airfield.DebugForceReleaseRunway"/> to clear the
///      landing lock + zombie batch-ready flags.
///   4. Reports the state AFTER, plus a summary count.
///
/// This is a recovery tool for when the landing queue has visibly deadlocked
/// (aircraft circling forever, runway permanently busy). It is NOT a normal
/// gameplay action — the runway logic now has timeouts + orphaned-owner
/// detection that handle real deadlocks automatically. Use this only when
/// you've observed a stuck state and want to unstick it without restarting
/// the scene.
///
/// Safe to run from the Editor while in Play mode (the operation is just
/// public field writes on the MonoBehaviour). Safe to re-run.
/// </summary>
public static class ForceReleaseRunway
{
    [MenuItem("Tools/RTS/Air System/Force Release Runway")]
    public static void Run()
    {
        Debug.Log("[ForceReleaseRunway] ── Scanning Airfields ──");

        Airfield[] fields = Object.FindObjectsByType<Airfield>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        if (fields == null || fields.Length == 0)
        {
            Debug.LogWarning("[ForceReleaseRunway] No Airfields found in the active scene. " +
                             "Open a scene with one or more Airfield instances first.");
            return;
        }

        int cleared = 0;
        foreach (Airfield af in fields)
        {
            if (af == null) continue;

            Airfield.RunwayMode  beforeMode  = af.CurrentRunwayMode;
            AirUnitController    beforeOwner = af.CurrentRunwayOwner;
            string ownerName = beforeOwner != null ? beforeOwner.name : "(none)";

            Debug.Log($"[ForceReleaseRunway]   '{af.name}' — before: mode={beforeMode}, owner={ownerName}");

            if (beforeMode != Airfield.RunwayMode.None)
            {
                af.DebugForceReleaseRunway();
                cleared++;

                Airfield.RunwayMode afterMode = af.CurrentRunwayMode;
                Debug.Log($"[ForceReleaseRunway]   '{af.name}' — after:  mode={afterMode}");
            }
            else
            {
                Debug.Log($"[ForceReleaseRunway]   '{af.name}' — runway already free; nothing to do.");
            }
        }

        Debug.Log($"[ForceReleaseRunway] ✓ Done. Cleared runway lock on {cleared}/{fields.Length} Airfield(s). " +
                  "Queued/holding aircraft will pick up the next clearance on their normal retry tick.");
    }
}
