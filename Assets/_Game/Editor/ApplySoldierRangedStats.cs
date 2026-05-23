using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click editor tool that retunes SoldierPrefab for ranged-rifle combat.
///
/// Menu: Tools → RTS → Apply Soldier Ranged Combat Stats
///
/// What it does (idempotent — safe to re-run):
///   • Sets UnitCombat.attackRange   = 8
///   • Sets UnitCombat.attackDamage  = 10
///   • Sets UnitCombat.attackCooldown = 0.4
///   • Sets UnitCombat.isRanged      = true
///   • Sets UnitCombat.rotationSpeed = 540
///   • Adds a child Transform called "FirePoint" at the approximate rifle
///     muzzle location (0.35, 0.15, 0.5) if it does not already exist, and
///     wires it into UnitCombat.firePoint.
///   • Persists the prefab via PrefabUtility so the changes survive scene
///     reloads and apply to every instance in the scene.
///
/// What it does NOT touch:
///   • Other components (Health, UnitMovement, SelectableUnit, NavMeshAgent,
///     UnitColorMarker, UnitVisualModelSlot, etc.).
///   • The prefab's GUID — every existing reference (UnitProducer.soldierPrefab,
///     instances already in the scene) keeps working.
///   • Tracer fields — UnitCombat builds the runtime LineRenderer itself.
/// </summary>
public static class ApplySoldierRangedStats
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    private const float AttackRange     = 8f;
    private const float AttackDamage    = 10f;
    private const float AttackCooldown  = 0.4f;
    private const float RotationSpeed   = 540f;
    private const string FirePointName  = "FirePoint";

    // Approximate rifle-muzzle position in the soldier prefab's local space.
    // The PrimitivePlaceholder rifle is at (0.35, 0.10, 0.25) with depth 0.60,
    // so its tip is around (0.35, 0.10, 0.55). We bump Y slightly so the
    // tracer originates around chest/rifle height rather than waist.
    private static readonly Vector3 FirePointLocalPos = new Vector3(0.35f, 0.15f, 0.50f);

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Apply Soldier Ranged Combat Stats")]
    public static void Apply()
    {
        // ── 1. Locate SoldierPrefab.prefab ──────────────────────────── //
        string prefabPath = AssetDatabase
            .FindAssets("SoldierPrefab t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith("/SoldierPrefab.prefab"));

        if (string.IsNullOrEmpty(prefabPath))
        {
            Debug.LogError("[ApplySoldierRangedStats] ✗ SoldierPrefab.prefab not found.\n" +
                           "  Expected to find a prefab asset named 'SoldierPrefab'.");
            return;
        }

        Debug.Log($"[ApplySoldierRangedStats] ── Editing {prefabPath} ──");

        // ── 2. Load the prefab into a sandboxed root for editing ────── //
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            UnitCombat uc = root.GetComponent<UnitCombat>();
            if (uc == null)
            {
                Debug.LogError("[ApplySoldierRangedStats] ✗ SoldierPrefab has no UnitCombat " +
                               "component. Aborting — add UnitCombat manually first.");
                return;
            }

            // ── 3. Stat values ─────────────────────────────────────── //
            uc.attackRange     = AttackRange;
            uc.attackDamage    = AttackDamage;
            uc.attackCooldown  = AttackCooldown;
            uc.rotationSpeed   = RotationSpeed;
            uc.isRanged        = true;

            // ── 4. FirePoint child ─────────────────────────────────── //
            Transform fp = FindChildByName(root.transform, FirePointName);
            if (fp == null)
            {
                GameObject fpGO = new GameObject(FirePointName);
                fpGO.transform.SetParent(root.transform, worldPositionStays: false);
                fpGO.transform.localPosition = FirePointLocalPos;
                fpGO.transform.localRotation = Quaternion.identity;
                fp = fpGO.transform;
                Debug.Log($"[ApplySoldierRangedStats] ✓ Created child '{FirePointName}' " +
                          $"at local {FirePointLocalPos}.");
            }
            else
            {
                Debug.Log($"[ApplySoldierRangedStats] ✓ Reusing existing '{FirePointName}' " +
                          $"at local {fp.localPosition}.");
            }
            uc.firePoint = fp;

            // ── 5. Save back to disk ───────────────────────────────── //
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Debug.Log(
                "[ApplySoldierRangedStats] ✓ SoldierPrefab updated. " +
                $"Range={AttackRange}, Damage={AttackDamage}, Cooldown={AttackCooldown}.\n" +
                "  Existing references and gameplay components are preserved.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the first DIRECT child Transform named <paramref name="name"/>,
    /// or null. We don't recurse — the FirePoint belongs on the soldier root
    /// so its world transform tracks the unit pivot.
    /// </summary>
    private static Transform FindChildByName(Transform parent, string name)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform c = parent.GetChild(i);
            if (c.name == name) return c;
        }
        return null;
    }
}
