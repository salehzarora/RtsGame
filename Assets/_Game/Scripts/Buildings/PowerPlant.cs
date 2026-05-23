using UnityEngine;

/// <summary>
/// Adds power supply to the global PowerManager when active,
/// and removes it when destroyed or disabled.
///
/// Uses OnEnable / OnDisable so placement (instantiate = enable)
/// and destruction both register correctly without extra code.
///
/// Setup:
///   1. Create a cube in the scene, name it "PowerPlant".
///   2. Scale: (3, 2, 3) or similar.
///   3. Layer: Building.
///   4. Add components: Building, SelectableBuilding, PowerPlant.
///   5. Give it a bright yellow/white material so it's visually distinct.
///   6. Save as a prefab in Assets/_Game/Prefabs/.
///   7. Assign the prefab to BuildingPlacementManager → Power Plant Prefab.
///   8. Press P in Play mode to place it.
/// </summary>
public class PowerPlant : MonoBehaviour
{
    [Header("Power Output")]
    [Tooltip("How much power supply this plant contributes when active")]
    public int supplyAmount = 100;

    // ------------------------------------------------------------------ //

    private void OnEnable()
    {
        if (PowerManager.Instance != null)
            PowerManager.Instance.AddSupply(supplyAmount);
    }

    private void OnDisable()
    {
        if (PowerManager.Instance != null)
            PowerManager.Instance.RemoveSupply(supplyAmount);
    }
}
