using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds (or repairs) the AmmoIndicator child on the existing StrikeJetPrefab.
///
/// Menu: Tools → RTS → Air System → Repair Strike Jet Ammo Indicator
///
/// What it does (idempotent — safe to re-run):
///   1. Locates StrikeJetPrefab.prefab via AssetDatabase.
///   2. Loads the prefab contents.
///   3. Ensures a direct child named "AmmoIndicator" exists.
///   4. Ensures that child has an AircraftAmmoIndicator component.
///   5. Sets a sensible heightOffset that clears the HealthBar.
///   6. Saves the prefab back.
///
/// Existing dot children (Dot_0, Dot_1) are not authored at editor time —
/// AircraftAmmoIndicator.Awake builds them procedurally at runtime, mirroring
/// how HealthBar handles its own visuals.
///
/// Use when:
///   • You already have a StrikeJetPrefab.prefab from a previous phase that
///     pre-dates the ammo indicator, AND you don't want to rebuild the
///     whole prefab from scratch via Create Strike Jet Prefab.
/// </summary>
public static class RepairStrikeJetAmmoIndicator
{
    private const string PrefabName    = "StrikeJetPrefab";
    private const string IndicatorName = "AmmoIndicator";

    [MenuItem("Tools/RTS/Air System/Repair Strike Jet Ammo Indicator")]
    public static void Repair()
    {
        Debug.Log("[RepairStrikeJetAmmoIndicator] ── Running ──");

        // ── 1. Locate the prefab ─────────────────────────────────────── //
        string prefabPath = AssetDatabase
            .FindAssets($"{PrefabName} t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith($"/{PrefabName}.prefab"));

        if (string.IsNullOrEmpty(prefabPath))
        {
            Debug.LogError($"[RepairStrikeJetAmmoIndicator] ✗ {PrefabName}.prefab not found.\n" +
                           "  Run Tools → RTS → Air System → Create Strike Jet Prefab first.");
            return;
        }

        // ── 2. Edit the prefab ──────────────────────────────────────── //
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            // Find the existing indicator child or create a fresh one as a
            // direct child of the prefab root.
            Transform indicator = root.transform.Find(IndicatorName);
            if (indicator == null)
            {
                GameObject indicatorGO = new GameObject(IndicatorName);
                indicatorGO.transform.SetParent(root.transform, worldPositionStays: false);
                indicator = indicatorGO.transform;
                Debug.Log($"[RepairStrikeJetAmmoIndicator]   ✓ Added '{IndicatorName}' child.");
            }
            else
            {
                Debug.Log($"[RepairStrikeJetAmmoIndicator]   = '{IndicatorName}' child already present.");
            }

            // Ensure the AircraftAmmoIndicator component exists. We don't
            // overwrite any tuned values; the user may have customised
            // heightOffset / colours.
            AircraftAmmoIndicator ai = indicator.GetComponent<AircraftAmmoIndicator>();
            if (ai == null)
            {
                ai = indicator.gameObject.AddComponent<AircraftAmmoIndicator>();
                ai.heightOffset = 2.6f; // a touch above the HealthBar (2.0)
                Debug.Log("[RepairStrikeJetAmmoIndicator]   ✓ Added AircraftAmmoIndicator component.");
            }
            else
            {
                Debug.Log("[RepairStrikeJetAmmoIndicator]   = AircraftAmmoIndicator already present.");
            }

            // The dot children are generated at runtime; the editor tool does
            // not seed them. Any stray non-Dot_N children are left alone so
            // the user can park their own visuals here if desired.

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Debug.Log($"[RepairStrikeJetAmmoIndicator] ✓ {prefabPath} updated.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        Debug.Log("[RepairStrikeJetAmmoIndicator] ✓ Done. Press Play to see the dots above each jet.");
    }
}
