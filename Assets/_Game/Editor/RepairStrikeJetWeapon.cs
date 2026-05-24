using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds (or repairs) the AircraftWeapon component on the existing StrikeJetPrefab.
///
/// Menu: Tools → RTS → Air System → Repair Strike Jet Weapon
///
/// What it does (idempotent — safe to re-run):
///   1. Locates StrikeJetPrefab.prefab via AssetDatabase.
///   2. Loads the prefab contents.
///   3. Ensures an AircraftWeapon component exists on the prefab root.
///   4. Sets sensible default firing values (matches the spec used by
///      Create Strike Jet Prefab — close + cone-tolerant).
///   5. Saves the prefab back.
///
/// Use when:
///   • You already have a StrikeJetPrefab.prefab from a previous phase that
///     pre-dates the AircraftWeapon extraction (the old monolithic
///     AirUnitController owned the missile state), AND you don't want to
///     rebuild the whole prefab from scratch via Create Strike Jet Prefab.
///   • The controller's [RequireComponent(typeof(AircraftWeapon))] will
///     auto-add a default weapon at runtime if missing, but those defaults
///     may not match the tuned values. Running this tool persists the
///     correct values into the prefab asset.
/// </summary>
public static class RepairStrikeJetWeapon
{
    private const string PrefabName = "StrikeJetPrefab";

    // Tuned defaults — match CreateStrikeJetPrefab so the two tools stay in sync.
    private const int   MaxAmmo                 = 2;
    private const float ReloadSecondsPerMissile = 3f;
    private const float MissileDamage           = 120f;
    private const float MissileProjectileSpeed  = 30f;
    private const float ImpactFlashDuration     = 0.2f;
    private const float MissileFireDelay        = 0.75f;
    private const float MinReleaseDistance      = 3.5f;
    private const float MaxReleaseDistance      = 24f;
    private const float ForwardConeDegrees      = 90f;

    [MenuItem("Tools/RTS/Air System/Repair Strike Jet Weapon")]
    public static void Repair()
    {
        Debug.Log("[RepairStrikeJetWeapon] ── Running ──");

        string prefabPath = AssetDatabase
            .FindAssets($"{PrefabName} t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith($"/{PrefabName}.prefab"));

        if (string.IsNullOrEmpty(prefabPath))
        {
            Debug.LogError($"[RepairStrikeJetWeapon] ✗ {PrefabName}.prefab not found.\n" +
                           "  Run Tools → RTS → Air System → Create Strike Jet Prefab first.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            AircraftWeapon weapon = root.GetComponent<AircraftWeapon>();
            if (weapon == null)
            {
                weapon = root.AddComponent<AircraftWeapon>();
                Debug.Log("[RepairStrikeJetWeapon]   ✓ Added AircraftWeapon component.");
            }
            else
            {
                Debug.Log("[RepairStrikeJetWeapon]   = AircraftWeapon already present — refreshing values.");
            }

            weapon.maxAmmo                 = MaxAmmo;
            weapon.reloadSecondsPerMissile = ReloadSecondsPerMissile;
            weapon.missileDamage           = MissileDamage;
            weapon.damageType              = DamageType.Missile;
            weapon.missileProjectileSpeed  = MissileProjectileSpeed;
            weapon.impactFlashDuration     = ImpactFlashDuration;
            weapon.missileFireDelay        = MissileFireDelay;
            weapon.minReleaseDistance      = MinReleaseDistance;
            weapon.maxReleaseDistance      = MaxReleaseDistance;
            weapon.forwardConeDegrees      = ForwardConeDegrees;

            EditorUtility.SetDirty(weapon);
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Debug.Log($"[RepairStrikeJetWeapon] ✓ {prefabPath} updated.\n" +
                      $"  Ammo {MaxAmmo}, ReleaseWindow=[{MinReleaseDistance},{MaxReleaseDistance}], " +
                      $"Cone={ForwardConeDegrees}°, FireDelay={MissileFireDelay}.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
    }
}
