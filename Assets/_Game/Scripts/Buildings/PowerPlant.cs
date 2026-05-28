using UnityEngine;

/// <summary>
/// Adds power supply to its OWNER's grid in <see cref="PowerManager"/> when
/// active, and removes it when destroyed or disabled.
///
/// Owner-aware (per-player power): the supply is credited to the sibling
/// <see cref="GameEntity.ownerPlayerId"/>, not a global pool. Because a
/// freshly-built plant runs OnEnable during Instantiate — BEFORE
/// <see cref="ConstructionSite"/> calls ApplyOwnership — we register under
/// whatever owner is known now and RE-KEY when ownership is applied
/// (<see cref="GameEntity.OnOwnershipApplied"/>), moving the supply from the
/// provisional owner to the canonical one. Same pattern UnitMovement /
/// TeamColorApplier use to survive that ordering.
///
/// Setup:
///   1. Create a cube in the scene, name it "PowerPlant".
///   2. Scale: (3, 2, 3) or similar.
///   3. Layer: Building.
///   4. Add components: Building, SelectableBuilding, GameEntity, PowerPlant.
///   5. Give it a bright yellow/white material so it's visually distinct.
///   6. Save as a prefab in Assets/_Game/Prefabs/.
///   7. Assign the prefab to BuildingPlacementManager → Power Plant Prefab.
///   8. Press P in Play mode to place it.
/// </summary>
public class PowerPlant : MonoBehaviour
{
    [Header("Power Output")]
    [Tooltip("How much power supply this plant contributes to its owner's grid when active")]
    public int supplyAmount = 100;

    // ------------------------------------------------------------------ //

    private GameEntity entity;
    private bool       registered;
    private int        registeredOwner;     // owner the supply is currently credited to

    private void Awake()
    {
        entity = GetComponent<GameEntity>();
    }

    private void OnEnable()
    {
        if (entity != null) entity.OnOwnershipApplied += HandleOwnershipApplied;
        Register();
    }

    private void OnDisable()
    {
        if (entity != null) entity.OnOwnershipApplied -= HandleOwnershipApplied;
        Unregister();
    }

    // Ownership stamped/changed after spawn — move our supply to the new owner.
    private void HandleOwnershipApplied(int newOwner)
    {
        if (registered && newOwner == registeredOwner) return;
        Unregister();
        Register();
    }

    private int CurrentOwner => entity != null ? entity.ownerPlayerId : GameEntity.PlayerOwnerId;

    private void Register()
    {
        if (registered) return;
        if (PowerManager.Instance == null) return;

        int owner = CurrentOwner;
        if (owner < 0) return;     // neutral entity — no grid to credit

        PowerManager.Instance.AddSupply(owner, supplyAmount, gameObject.name);
        registeredOwner = owner;
        registered      = true;
    }

    private void Unregister()
    {
        if (!registered) return;
        if (PowerManager.Instance != null)
            PowerManager.Instance.RemoveSupply(registeredOwner, supplyAmount, gameObject.name);
        registered = false;
    }
}
