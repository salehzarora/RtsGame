using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Bottom-right minimap viewer. Binds a <see cref="MinimapCamera"/>'s
/// RenderTexture to the panel's RawImage so the player sees a live top-down
/// view of the map.
///
/// Also supports click-to-recentre: clicking inside the minimap moves the
/// gameplay <see cref="RTSCameraController"/> (if present) to the clicked
/// world point. Falls back gracefully when no controller is in the scene.
///
/// What this component intentionally does NOT do:
///   • Draw unit blips. Future work — a sibling MinimapBlipController will
///     project unit positions into the panel rect.
///   • Take focus from gameplay. The RawImage is on the HUD canvas with
///     raycastTarget = true ONLY for clicks; mouse drags are not consumed.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
public class MiniMapPanelUI : MonoBehaviour, IPointerClickHandler
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Wiring")]
    [Tooltip("The RawImage that displays the minimap RenderTexture. Lives as " +
             "a child of this panel.")]
    public RawImage display;

    [Tooltip("Source camera. SetupGameplayHUD assigns this to the scene's " +
             "MinimapCamera so the RT is hot-bound on play.")]
    public MinimapCamera source;

    [Header("Click-to-recentre")]
    [Tooltip("If true, left-clicking inside the minimap moves the RTS camera " +
             "to the clicked world point. Disable if the project uses a custom " +
             "camera that can't accept ExternalTeleportTo.")]
    public bool clickToRecentre = true;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private RectTransform selfRect;
    private RTSCamera     gameplayCamera;
    private bool          gameplayCameraLookupAttempted;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        selfRect = (RectTransform)transform;
    }

    private void Start()
    {
        TryBindRenderTexture();
    }

    private void Update()
    {
        // The minimap RT is allocated lazily in MinimapCamera.Awake. If the
        // bind raced past it (e.g. RTSHUD.Awake before MinimapCamera.Awake),
        // pick it up the first frame it's available.
        if (display != null && display.texture == null) TryBindRenderTexture();
    }

    private void TryBindRenderTexture()
    {
        if (display == null) return;
        if (source  == null) return;
        if (source.OutputTexture == null) return;

        display.texture = source.OutputTexture;
    }

    // ------------------------------------------------------------------ //
    // Click-to-recentre
    // ------------------------------------------------------------------ //

    public void OnPointerClick(PointerEventData eventData)
    {
        if (!clickToRecentre) return;
        if (source == null)   return;

        // Convert click position from canvas-local space to a 0..1 normalised
        // point inside the minimap RectTransform.
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                selfRect, eventData.position, eventData.pressEventCamera, out Vector2 local))
            return;

        Vector2 size = selfRect.rect.size;
        if (size.x <= 0f || size.y <= 0f) return;

        // Pivot of the panel is (0.5, 0.5), so local ranges from (-w/2..+w/2).
        float nx = (local.x + size.x * 0.5f) / size.x;
        float ny = (local.y + size.y * 0.5f) / size.y;
        nx = Mathf.Clamp01(nx);
        ny = Mathf.Clamp01(ny);

        // Map the normalised point into world XZ via the minimap camera's
        // orthographic frustum. The camera sits at (cx, h, cz) facing down;
        // half-extent on X and Z is orthographicHalfSize.
        Vector3 camPos    = source.transform.position;
        float   halfExtent = source.orthographicHalfSize;
        float worldX = camPos.x + (nx - 0.5f) * (halfExtent * 2f);
        float worldZ = camPos.z + (ny - 0.5f) * (halfExtent * 2f);
        Vector3 worldTarget = new Vector3(worldX, 0f, worldZ);

        // Lazy-locate the gameplay camera rig (lookup once, cache).
        if (!gameplayCameraLookupAttempted)
        {
            gameplayCamera = FindAnyObjectByType<RTSCamera>();
            gameplayCameraLookupAttempted = true;
        }

        if (gameplayCamera == null)
        {
            Debug.LogWarning("[MiniMapPanelUI] No RTSCamera found — minimap " +
                             "click-to-recentre has nothing to drive.");
            return;
        }

        gameplayCamera.TeleportTo(worldTarget);
    }
}
