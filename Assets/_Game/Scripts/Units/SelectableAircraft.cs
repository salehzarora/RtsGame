using UnityEngine;

/// <summary>
/// Marks an aircraft GameObject as left-click selectable and manages its
/// selection circle visual.
///
/// Parallel to <see cref="SelectableUnit"/>, but with no UnitMovement
/// requirement — aircraft don't use NavMeshAgent. UnitSelector picks up
/// SelectableAircraft via raycast on the Unit layer alongside SelectableUnit.
///
/// Setup:
///   1. Attach to your aircraft GameObject (the AirUnitController root).
///   2. Create a child GameObject named "SelectionCircle"
///      (a flat Cylinder works well; scale Y to ~0.05).
///   3. Drag that child into the selectionCircle field.
///   4. The circle is hidden at start and shown only when selected.
/// </summary>
public class SelectableAircraft : MonoBehaviour
{
    [Header("Visuals")]
    [Tooltip("Child GameObject that acts as the selection circle (shown when selected)")]
    public GameObject selectionCircle;

    public bool IsSelected { get; private set; }

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        SetCircleVisible(false);
    }

    // ------------------------------------------------------------------ //

    /// <summary>Called by UnitSelector when this aircraft is clicked.</summary>
    public void Select()
    {
        IsSelected = true;
        SetCircleVisible(true);
    }

    /// <summary>Called by UnitSelector when another unit is clicked or empty space.</summary>
    public void Deselect()
    {
        IsSelected = false;
        SetCircleVisible(false);
    }

    // ------------------------------------------------------------------ //

    private void SetCircleVisible(bool visible)
    {
        if (selectionCircle != null)
            selectionCircle.SetActive(visible);
    }
}
