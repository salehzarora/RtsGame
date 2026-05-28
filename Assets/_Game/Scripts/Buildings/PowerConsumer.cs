using UnityEngine;

/// <summary>
/// Registers a power demand on its OWNER's grid in <see cref="PowerManager"/>
/// when active, and unregisters it when the building is destroyed or disabled.
///
/// Owner-aware (per-player power): demand is charged to the sibling
/// <see cref="GameEntity.ownerPlayerId"/>, and <see cref="IsPowered"/> reports
/// whether THAT owner's grid is covered — not a global pool. Like
/// <see cref="PowerPlant"/>, OnEnable can run before ApplyOwnership stamps the
/// real owner, so we re-key the demand on
/// <see cref="GameEntity.OnOwnershipApplied"/>.
///
/// Add to any production building (Barracks, Vehicle Factory, etc.).
/// UnitProducer reads <see cref="IsPowered"/> before allowing production.
///
/// Setup:
///   1. Add this component to the Barracks prefab (alongside GameEntity).
///   2. Set demandAmount = 10 (or as required by the building type).
///   3. That's it — PowerManager tracks the per-owner total automatically.
///
/// Future: add to Vehicle Factory (20 demand), Turret (5 demand), etc.
/// </summary>
public class PowerConsumer : MonoBehaviour
{
    [Header("Power Demand")]
    [Tooltip("How much power this building draws from its owner's grid when active")]
    public int demandAmount = 10;

    // ------------------------------------------------------------------ //

    private GameEntity entity;
    private bool       registered;
    private int        registeredOwner;     // owner the demand is currently charged to

    private int CurrentOwner => entity != null ? entity.ownerPlayerId : GameEntity.PlayerOwnerId;

    /// <summary>
    /// True when THIS building's owner has enough supply to cover its own
    /// demand. If false, production buildings should halt output. Preserves
    /// the prior "no PowerManager → not powered" behaviour.
    /// </summary>
    public bool IsPowered =>
        PowerManager.Instance != null && PowerManager.Instance.IsPoweredFor(CurrentOwner);

    // ------------------------------------------------------------------ //

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

    // Ownership stamped/changed after spawn — move our demand to the new owner.
    private void HandleOwnershipApplied(int newOwner)
    {
        if (registered && newOwner == registeredOwner) return;
        Unregister();
        Register();
    }

    private void Register()
    {
        if (registered) return;
        if (PowerManager.Instance == null) return;

        int owner = CurrentOwner;
        if (owner < 0) return;     // neutral entity — no grid to charge

        PowerManager.Instance.AddDemand(owner, demandAmount, gameObject.name);
        registeredOwner = owner;
        registered      = true;
    }

    private void Unregister()
    {
        if (!registered) return;
        if (PowerManager.Instance != null)
            PowerManager.Instance.RemoveDemand(registeredOwner, demandAmount, gameObject.name);
        registered = false;
    }
}
