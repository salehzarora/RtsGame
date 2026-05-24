using UnityEngine;

/// <summary>
/// Coarse damage type used by a UnitCombat. Drives the modifier lookup in
/// <see cref="DamageRules"/>.
/// </summary>
public enum DamageType
{
    Bullet,   // Soldier rifle / Humvee machine gun — strong vs infantry
    Cannon    // Artillery Tank cannon shell — strong vs vehicles and buildings
}

/// <summary>
/// Attaches a target category (Infantry / Vehicle / Building) to a unit or
/// building. Damage modifiers in <see cref="DamageRules"/> scale incoming damage
/// based on this category.
///
/// Setup:
///   1. Add this component to ANY unit or building that should receive
///      category-modified damage.
///   2. Set Category in the Inspector:
///         - Soldier, Worker, infantry enemies               → Infantry
///         - Humvee, Artillery Tank, vehicle enemies         → Vehicle
///         - Barracks, CommandCenter, PowerPlant, Vehicle Factory → Building
///   3. Targets with no UnitCategory are treated as Infantry (1× for Bullet).
///
/// Bulk attach: Tools → RTS → Repair Vehicle Factory Production walks every
/// standard prefab and stamps the correct UnitCategory.
/// </summary>
public class UnitCategory : MonoBehaviour
{
    public enum Category
    {
        Infantry,
        Vehicle,
        Building
    }

    [Header("Target Category")]
    [Tooltip("Damage modifiers scale incoming damage based on this category. " +
             "See DamageRules for the modifier table.")]
    public Category category = Category.Infantry;
}

/// <summary>
/// Damage-type × target-category modifier table. Pure static helper — no state,
/// no dependencies. Add a new <see cref="DamageType"/> by extending the switch.
///
///   Bullet vs (Infantry, Vehicle, Building) = 1.00 / 0.35 / 0.25
///   Cannon vs (Infantry, Vehicle, Building) = 0.25 / 1.00 / 1.20
/// </summary>
public static class DamageRules
{
    public static float Modifier(DamageType type, UnitCategory.Category cat)
    {
        switch (type)
        {
            case DamageType.Bullet:
                switch (cat)
                {
                    case UnitCategory.Category.Infantry: return 1.00f;
                    case UnitCategory.Category.Vehicle:  return 0.35f;
                    case UnitCategory.Category.Building: return 0.25f;
                }
                break;

            case DamageType.Cannon:
                switch (cat)
                {
                    case UnitCategory.Category.Infantry: return 0.25f;
                    case UnitCategory.Category.Vehicle:  return 1.00f;
                    case UnitCategory.Category.Building: return 1.20f;
                }
                break;
        }
        return 1.00f;
    }

    /// <summary>
    /// Resolves the category of <paramref name="targetGO"/>. Checks the
    /// GameObject itself, then walks up to a parent. Treats anything without a
    /// UnitCategory as Infantry so legacy units still take 100% Bullet damage.
    /// </summary>
    public static UnitCategory.Category Resolve(GameObject targetGO)
    {
        if (targetGO == null) return UnitCategory.Category.Infantry;

        UnitCategory uc = targetGO.GetComponent<UnitCategory>()
                       ?? targetGO.GetComponentInParent<UnitCategory>();

        return uc != null ? uc.category : UnitCategory.Category.Infantry;
    }
}
