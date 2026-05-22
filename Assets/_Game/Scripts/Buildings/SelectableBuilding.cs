using UnityEngine;

/// <summary>
/// Marks a building as left-click selectable.
/// Mirrors the SelectableUnit pattern so both feel consistent.
///
/// Setup:
///   1. Add to the Barracks prefab root (alongside Building and UnitProducer).
///   2. Optional: create a child GameObject named "BuildingSelectionRing".
///      - Use a flat Cylinder scaled to (4, 0.02, 4) as a floor indicator.
///      - Give it a cyan/teal material.
///      - Drag it into the selectionIndicator field.
///   3. UnitSelector detects this component when left-clicking the Building layer.
/// </summary>
public class SelectableBuilding : MonoBehaviour
{
    [Header("Visual")]
    [Tooltip("Optional child object shown while this building is selected (e.g. a flat ring)")]
    public GameObject selectionIndicator;

    public bool IsSelected { get; private set; }

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        SetIndicatorVisible(false);
    }

    // ------------------------------------------------------------------ //

    public void Select()
    {
        IsSelected = true;
        SetIndicatorVisible(true);
    }

    public void Deselect()
    {
        IsSelected = false;
        SetIndicatorVisible(false);
    }

    // ------------------------------------------------------------------ //

    private void SetIndicatorVisible(bool visible)
    {
        if (selectionIndicator != null)
            selectionIndicator.SetActive(visible);
    }
}
