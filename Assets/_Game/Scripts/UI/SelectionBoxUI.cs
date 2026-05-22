using UnityEngine;

/// <summary>
/// Draws the drag-select rectangle on screen using a UI Image.
///
/// Setup:
///   1. Create a Canvas: GameObject → UI → Canvas.
///      - Render Mode: Screen Space - Overlay
///      - No Graphic Raycaster needed — delete it to save overhead.
///   2. Inside the Canvas create an empty child: right-click Canvas → Create Empty.
///      Name it "SelectionBox".
///   3. Add an Image component to SelectionBox.
///      - Color: white with ~40 alpha (semi-transparent fill).
///      - Source Image: leave None (solid colour is fine).
///   4. Add THIS script (SelectionBoxUI) to SelectionBox.
///   5. In the RectTransform of SelectionBox:
///      - Set Anchor Preset to BOTTOM-LEFT (click the anchor icon → hold Alt → pick bottom-left).
///      - Pivot: (0, 0)
///   6. Drag this SelectionBox GameObject into the UnitSelector → selectionBoxUI field.
/// </summary>
[RequireComponent(typeof(UnityEngine.UI.Image))]
public class SelectionBoxUI : MonoBehaviour
{
    private RectTransform rectTransform;
    private Vector2 startScreenPos;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        rectTransform = GetComponent<RectTransform>();
        gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------ //

    /// <summary>Call when the left mouse button is first pressed.</summary>
    public void StartBox(Vector2 screenPos)
    {
        startScreenPos = screenPos;
        gameObject.SetActive(true);
        Redraw(screenPos);
    }

    /// <summary>Call every frame while the mouse button is held.</summary>
    public void UpdateBox(Vector2 currentScreenPos)
    {
        Redraw(currentScreenPos);
    }

    /// <summary>Call when the mouse button is released. Hides the box.</summary>
    public void EndBox()
    {
        gameObject.SetActive(false);
    }

    /// <summary>
    /// Returns the screen-space Rect for the current drag.
    /// Pass the current mouse position as <paramref name="currentScreenPos"/>.
    /// Origin is bottom-left of the screen — matches Physics raycasts and
    /// Camera.WorldToScreenPoint coordinates.
    /// </summary>
    public Rect GetScreenRect(Vector2 currentScreenPos)
    {
        Vector2 min = Vector2.Min(startScreenPos, currentScreenPos);
        Vector2 max = Vector2.Max(startScreenPos, currentScreenPos);
        return new Rect(min, max - min);
    }

    // ------------------------------------------------------------------ //

    private void Redraw(Vector2 currentScreenPos)
    {
        Vector2 min = Vector2.Min(startScreenPos, currentScreenPos);
        Vector2 max = Vector2.Max(startScreenPos, currentScreenPos);

        // anchoredPosition is the bottom-left corner of the box,
        // measured from the bottom-left of the screen (our anchor).
        rectTransform.anchoredPosition = min;
        rectTransform.sizeDelta = max - min;
    }
}
