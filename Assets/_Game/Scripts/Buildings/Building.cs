using UnityEngine;

/// <summary>
/// Marker component placed on every built structure.
/// Used by overlap checks to block placement on top of existing buildings.
/// Will hold production logic in a later phase.
///
/// Setup:
///   1. Add to your Barracks prefab root.
///   2. Set Building Name and Cost to match the BuildingPlacementManager values.
///   3. Set the prefab's Layer to "Building" in the Inspector.
/// </summary>
public class Building : MonoBehaviour
{
    [Header("Info")]
    [Tooltip("Display name for future UI")]
    public string buildingName = "Barracks";

    [Tooltip("Resource cost — should match BuildingPlacementManager.barracksCost")]
    public int cost = 100;

    [Header("Placement Override")]
    [Tooltip("If ≥ 0, BuildingPlacementManager uses THIS Y offset instead of its " +
             "global placementHeightOffset when placing this prefab. Set to 0 for " +
             "buildings whose root pivot should sit exactly at ground level (e.g. " +
             "Airfield, whose internal runway/apron meshes already supply visual " +
             "height). Leave negative (default -1) to use the global value.")]
    public float placementYOffsetOverride = -1f;
}
