using UnityEngine;

/// <summary>
/// Visual-only placeholder slot. Lives on SoldierVisualRoot (a child of SoldierPrefab).
///
/// Purpose:
///   When you import a low-poly soldier model later, drop it under SoldierVisualRoot
///   and drag the imported model's Transform into the <see cref="model"/> field.
///   This makes the imported model easy to find from future visual-only scripts
///   (e.g. animator hookups, weapon attachments) without coupling them to gameplay.
///
/// Strict rules — keep this script visual-only:
///   • No gameplay logic.
///   • No movement, combat, selection, or health code.
///   • The gameplay root (SoldierPrefab) keeps NavMeshAgent, UnitMovement,
///     SelectableUnit, Health, UnitCombat, and the CapsuleCollider.
/// </summary>
public class UnitVisualModelSlot : MonoBehaviour
{
    [Tooltip("Drop your imported low-poly soldier model here. Visual-only — gameplay " +
             "components stay on the SoldierPrefab root, not on the imported model.")]
    public Transform model;
}
