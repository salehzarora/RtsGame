using UnityEngine;

/// <summary>
/// RTS Camera Controller
///
/// Setup:
///   1. Create an empty GameObject named "CameraRig" at position (0, 10, 0).
///   2. Attach this script to CameraRig.
///   3. Make the Main Camera a child of CameraRig.
///   4. Set Camera local position to (0, 0, 0) and local rotation to (45, 0, 0) — tilted down.
///   5. Assign the Camera reference in the Inspector.
/// </summary>
public class RTSCamera : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Drag the child Camera here")]
    public Camera cam;

    [Header("Pan")]
    [Tooltip("XZ movement speed via WASD / edge scroll")]
    public float panSpeed = 25f;

    [Header("Edge Scrolling")]
    public bool edgeScrollEnabled = true;
    [Tooltip("Pixel distance from screen border that triggers edge scroll")]
    public float edgeThreshold = 20f;

    [Header("Zoom (rig height)")]
    public float zoomSpeed = 15f;
    public float minHeight = 5f;
    public float maxHeight = 50f;

    [Header("Rotation")]
    [Tooltip("Degrees per second for Q/E keys and middle-mouse drag")]
    public float rotationSpeed = 90f;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (cam == null)
            cam = GetComponentInChildren<Camera>();
    }

    private void Update()
    {
        // Yield input while the main menu is open. Null-safe — if no
        // GameStateManager is in the scene we fall through to normal behaviour.
        if (!GameStateManager.IsPlaying) return;

        HandlePan();
        HandleZoom();
        HandleRotation();
    }

    // ------------------------------------------------------------------ //

    private void HandlePan()
    {
        Vector3 move = Vector3.zero;

        // WASD
        if (Input.GetKey(KeyCode.W)) move += transform.forward;
        if (Input.GetKey(KeyCode.S)) move -= transform.forward;
        if (Input.GetKey(KeyCode.D)) move += transform.right;
        if (Input.GetKey(KeyCode.A)) move -= transform.right;

        // Edge scrolling (only when cursor is inside the window)
        if (edgeScrollEnabled && Application.isFocused)
        {
            Vector3 mouse = Input.mousePosition;
            if (mouse.x >= 0 && mouse.x <= Screen.width &&
                mouse.y >= 0 && mouse.y <= Screen.height)
            {
                if (mouse.x < edgeThreshold)              move -= transform.right;
                if (mouse.x > Screen.width - edgeThreshold) move += transform.right;
                if (mouse.y < edgeThreshold)              move -= transform.forward;
                if (mouse.y > Screen.height - edgeThreshold) move += transform.forward;
            }
        }

        // Kill vertical drift so the rig always glides on XZ
        move.y = 0f;

        if (move.sqrMagnitude > 1f)
            move.Normalize();

        transform.position += move * panSpeed * Time.deltaTime;
    }

    private void HandleZoom()
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) < 0.001f) return;

        Vector3 pos = transform.position;
        pos.y -= scroll * zoomSpeed;
        pos.y = Mathf.Clamp(pos.y, minHeight, maxHeight);
        transform.position = pos;
    }

    private void HandleRotation()
    {
        float yaw = 0f;

        if (Input.GetKey(KeyCode.Q))
            yaw -= rotationSpeed * Time.deltaTime;

        if (Input.GetKey(KeyCode.E))
            yaw += rotationSpeed * Time.deltaTime;

        // Middle-mouse drag rotation
        if (Input.GetMouseButton(2))
            yaw += Input.GetAxis("Mouse X") * rotationSpeed * Time.deltaTime;

        transform.Rotate(Vector3.up, yaw, Space.World);
    }
}
