using UnityEngine;

/// <summary>
/// Registers a power demand with the global PowerManager when active,
/// and unregisters it when the building is destroyed or disabled.
///
/// Add to any production building (Barracks, Vehicle Factory, etc.).
/// UnitProducer reads IsPowered before allowing production.
///
/// Setup:
///   1. Add this component to the Barracks prefab.
///   2. Set demandAmount = 10 (or as required by the building type).
///   3. That's it — PowerManager tracks the total automatically.
///
/// Future: add to Vehicle Factory (20 demand), Turret (5 demand), etc.
/// </summary>
public class PowerConsumer : MonoBehaviour
{
    [Header("Power Demand")]
    [Tooltip("How much power this building draws from the grid when active")]
    public int demandAmount = 10;

    // ------------------------------------------------------------------ //

    /// <summary>
    /// True when the global grid has enough supply to cover all demand.
    /// If false, production buildings should halt output.
    /// </summary>
    public bool IsPowered =>
        PowerManager.Instance != null && PowerManager.Instance.IsPowered;

    // ------------------------------------------------------------------ //

    private void OnEnable()
    {
        if (PowerManager.Instance != null)
            PowerManager.Instance.AddDemand(demandAmount);
    }

    private void OnDisable()
    {
        if (PowerManager.Instance != null)
            PowerManager.Instance.RemoveDemand(demandAmount);
    }
}
