using UnityEngine;

/// <summary>
/// Lightweight top-down camera that renders the playable area into a
/// <see cref="RenderTexture"/> for the HUD minimap.
///
/// This component:
///   • Lives on its own GameObject (created by SetupGameplayHUD) high above
///     the map, pointing straight down.
///   • Owns a Camera tagged Untagged (NOT MainCamera) so it never competes
///     with the gameplay camera tag lookup.
///   • Allocates its own RenderTexture and assigns it to <c>Camera.targetTexture</c>.
///     <see cref="MiniMapPanelUI"/> reads <see cref="OutputTexture"/> and binds
///     it to a <c>RawImage</c> on the HUD.
///   • Renders <i>only</i> non-Ignore layers (a culling mask the user can edit
///     to hide e.g. clouds or VFX-only layers from the minimap).
///
/// What this component intentionally does NOT do:
///   • Track units, draw blips, or layer selection markers. That belongs to
///     a future MinimapBlipController. The minimap shows the literal world.
///   • Pause when the game is paused. It piggybacks on the gameplay camera's
///     rendering loop.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public class MinimapCamera : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Render target")]
    [Tooltip("Side length of the minimap RenderTexture (pixels). 256 is a good " +
             "sweet spot — sharp enough at HUD sizes and cheap to render.")]
    public int textureSize = 256;

    [Tooltip("Bit depth of the depth buffer attached to the RenderTexture. " +
             "16 is plenty for an orthographic top-down view.")]
    public int depthBufferBits = 16;

    [Header("Framing")]
    [Tooltip("How wide an orthographic slice of the world fits in the view. " +
             "Half-extent — actual horizontal coverage is 2× this value. " +
             "Default 60 covers a 120-unit-square map; tune for your terrain.")]
    public float orthographicHalfSize = 60f;

    [Tooltip("How high above the world the minimap camera floats. Should be " +
             "tall enough to clear the highest building.")]
    public float cameraHeight = 80f;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    /// <summary>
    /// The RenderTexture this camera draws into. Read by MiniMapPanelUI to
    /// bind a RawImage at HUD setup time.
    /// </summary>
    public RenderTexture OutputTexture { get; private set; }

    private Camera cam;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        cam = GetComponent<Camera>();

        // Allocate the RT lazily — Awake runs on every play, but only the
        // first time per session do we actually need to alloc. Re-using
        // OutputTexture across scene loads is OK because we never write to
        // it ourselves (the Camera owns the write side).
        if (OutputTexture == null || OutputTexture.width != textureSize)
        {
            if (OutputTexture != null) OutputTexture.Release();

            OutputTexture = new RenderTexture(textureSize, textureSize, depthBufferBits, RenderTextureFormat.Default)
            {
                name           = "MinimapRT",
                wrapMode       = TextureWrapMode.Clamp,
                filterMode     = FilterMode.Bilinear,
                antiAliasing   = 1
            };
            OutputTexture.Create();
        }

        cam.targetTexture     = OutputTexture;
        cam.orthographic      = true;
        cam.orthographicSize  = orthographicHalfSize;
        cam.clearFlags        = CameraClearFlags.SolidColor;
        cam.backgroundColor   = new Color(0.05f, 0.07f, 0.06f, 1f);
        cam.allowHDR          = false;
        cam.allowMSAA         = false;

        // Point straight down. Position is set by SetupGameplayHUD relative
        // to the playable map centre.
        transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        Debug.Log($"[MinimapCamera] Initialised RT {textureSize}x{textureSize}, " +
                  $"half-size {orthographicHalfSize}, height {cameraHeight}");
    }

    private void OnDestroy()
    {
        if (OutputTexture != null)
        {
            OutputTexture.Release();
            OutputTexture = null;
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers used by SetupGameplayHUD
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Re-positions the minimap camera so it floats <paramref name="cameraHeight"/>
    /// units above <paramref name="worldCenter"/>. Called once by the editor
    /// setup tool so the camera is aimed at the map regardless of where the
    /// GameObject was instantiated.
    /// </summary>
    public void CentreOn(Vector3 worldCenter)
    {
        transform.position = new Vector3(worldCenter.x, cameraHeight, worldCenter.z);
    }
}
