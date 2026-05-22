using UnityEngine;

/// <summary>
/// Marks a GameObject as a selectable unit and manages the selection circle visual.
///
/// Setup:
///   1. Attach to your unit GameObject.
///   2. Create a child GameObject named "SelectionCircle":
///      - Add a Cylinder mesh (scale it flat, e.g. Y = 0.05).
///      - Or use a Quad with a circle decal material.
///   3. Drag that child into the selectionCircle field in the Inspector.
///   4. The circle is hidden at start and shown only when selected.
/// </summary>
[RequireComponent(typeof(UnitMovement))]
public class SelectableUnit : MonoBehaviour
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

    /// <summary>Called by UnitSelector when this unit is clicked.</summary>
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
