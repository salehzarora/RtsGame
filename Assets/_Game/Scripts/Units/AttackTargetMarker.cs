using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Floating red diamond that hovers above the player's current attack target.
/// One marker total — new attack commands simply re-target the same instance.
///
/// Visibility contract (the entire reason this script exists):
///   • Hidden on Awake AND on Start (belt-and-suspenders).
///   • Hidden every frame the tracked target is null.
///   • Visible only between Show(target) and Hide() while the target lives.
///
/// Lifecycle, driven by UnitSelector:
///   • Show(targetTransform)   — start tracking that target.
///   • Hide()                  — stop tracking. Called by UnitSelector on
///                               ground-move, resource-gather, and deselect.
///   • Auto-hides itself when the tracked GameObject is destroyed (e.g. the
///     EnemyDummy reaches 0 HP).
///
/// Setup:
///   1. Attach this component to your GameManager GameObject.
///   2. Optional — pre-build a child GameObject (cube, quad, custom mesh, etc.)
///      and drag it into the "Marker Visual" Inspector field. Leave the field
///      empty to have this script build a procedural red diamond at runtime.
///   3. UnitSelector finds this component via FindAnyObjectByType in Awake.
///
/// What it deliberately does NOT do:
///   • Add a Collider — colliders on the visual are stripped at runtime so
///     the marker never intercepts the right-click raycast.
///   • Add SelectableUnit — the marker is not selectable.
///   • Touch the NavMesh — no NavMeshAgent / NavMeshObstacle.
///   • Modify Health, UnitCombat, or any combat logic — pure feedback.
/// </summary>
public class AttackTargetMarker : MonoBehaviour
{
    // Unity's built-in IgnoreRaycast layer index. Setting the visual to this
    // layer is defence-in-depth: even if some future code re-adds a collider,
    // Physics.Raycast still ignores it.
    private const int IgnoreRaycastLayer = 2;

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Visual (optional)")]
    [Tooltip("Pre-built child GameObject to use as the marker visual. " +
             "Leave empty to procedurally build a red diamond at runtime.")]
    public GameObject markerVisual;

    [Header("Position")]
    [Tooltip("World-unit height above the target's pivot at which the marker hovers")]
    public float heightOffset = 2.5f;

    [Header("Bob Animation")]
    [Tooltip("Peak vertical offset added by the bob, in world units (0 disables bobbing)")]
    public float bobAmplitude = 0.15f;

    [Tooltip("Bob cycles per second")]
    public float bobFrequency = 2f;

    [Header("Spin Animation")]
    [Tooltip("Y-axis rotation speed in degrees per second (0 disables spinning)")]
    public float spinSpeed = 120f;

    [Header("Procedural Visual")]
    [Tooltip("Colour of the procedurally-built marker (ignored if Marker Visual is assigned)")]
    [ColorUsage(false)] public Color markerColor = new Color(1f, 0.15f, 0.15f);

    [Tooltip("Edge length of the procedural diamond cube before rotation")]
    public float markerSize = 0.45f;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private Transform target;
    private float bobPhase;

    // ------------------------------------------------------------------ //
    // Unity lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        EnsureVisualReady();
        InternalHide();   // hide #1 — guarantees no flash before first frame
    }

    private void Start()
    {
        InternalHide();   // hide #2 — overrides anything else that may have
                          // re-enabled the visual during scene Awake/Start
    }

    /// <summary>
    /// LateUpdate so the marker follows AFTER the target's own movement /
    /// physics for the frame, avoiding visible lag on a moving target.
    /// </summary>
    private void LateUpdate()
    {
        if (target == null)
        {
            // No target → marker MUST be hidden. Covers both the "never shown
            // yet" case and the "target was just destroyed" case (Unity's
            // overloaded == null returns true for destroyed objects).
            if (markerVisual != null && markerVisual.activeSelf)
                InternalHide();
            return;
        }

        // Bob using a phase counter (not Time.time) so successive Show() calls
        // restart the cycle at zero — feels snappier than mid-cycle continuity.
        bobPhase += Time.deltaTime * bobFrequency * Mathf.PI * 2f;
        float bob = Mathf.Sin(bobPhase) * bobAmplitude;

        markerVisual.transform.position =
            target.position + Vector3.up * (heightOffset + bob);

        markerVisual.transform.Rotate(Vector3.up, spinSpeed * Time.deltaTime, Space.World);
    }

    // ------------------------------------------------------------------ //
    // Public API — called by UnitSelector
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Begin tracking <paramref name="newTarget"/>. Passing null is equivalent
    /// to Hide(). Safe to call repeatedly — the marker simply re-targets.
    /// </summary>
    public void Show(Transform newTarget)
    {
        if (newTarget == null) { Hide(); return; }
        EnsureVisualReady();           // defensive — Awake should have run

        target   = newTarget;
        bobPhase = 0f;
        markerVisual.SetActive(true);

        // Place the marker at the target immediately so it does not flash
        // at the visual's last position for one frame before LateUpdate runs.
        markerVisual.transform.position =
            newTarget.position + Vector3.up * heightOffset;

        // TEMPORARY debug — remove once attack-marker wiring is verified.
        Debug.Log($"[AttackMarker] Show target: {newTarget.name}");
    }

    /// <summary>Stop tracking and hide the marker. Safe to call when already hidden.</summary>
    public void Hide()
    {
        // TEMPORARY debug — only log when actually transitioning from visible
        // so a constantly-hidden marker doesn't spam the Console.
        if (markerVisual != null && markerVisual.activeSelf)
            Debug.Log("[AttackMarker] Hide");

        InternalHide();
    }

    // ------------------------------------------------------------------ //
    // Internals
    // ------------------------------------------------------------------ //

    private void InternalHide()
    {
        target = null;
        if (markerVisual != null) markerVisual.SetActive(false);
    }

    /// <summary>
    /// Guarantees <see cref="markerVisual"/> exists, has no colliders, and
    /// lives on the IgnoreRaycast layer. Builds a procedural diamond if no
    /// visual was assigned in the Inspector. Idempotent.
    /// </summary>
    private void EnsureVisualReady()
    {
        if (markerVisual == null)
            markerVisual = BuildProceduralVisual();

        // Strip every collider in the visual subtree so the marker can never
        // block selection or attack raycasts.
        foreach (Collider c in markerVisual.GetComponentsInChildren<Collider>(true))
            Destroy(c);

        // IgnoreRaycast layer for the whole subtree.
        SetLayerRecursive(markerVisual.transform, IgnoreRaycastLayer);
    }

    /// <summary>
    /// Builds a red diamond as a child of this GameObject so its lifetime is
    /// bound to the AttackTargetMarker. We still write WORLD positions to it
    /// in LateUpdate, so the parenting does not interfere with tracking.
    /// </summary>
    private GameObject BuildProceduralVisual()
    {
        GameObject v = GameObject.CreatePrimitive(PrimitiveType.Cube);
        v.name = "AttackTargetMarkerVisual";
        v.transform.SetParent(transform, worldPositionStays: false);
        v.transform.localScale = Vector3.one * markerSize;
        // 45° on Y + a tilt on X reads as a spinning diamond above the target.
        v.transform.rotation   = Quaternion.Euler(45f, 45f, 0f);

        Renderer r = v.GetComponent<Renderer>();
        if (r != null)
        {
            Material m = new Material(ResolveLitShader())
            {
                name  = "AttackTargetMarker_Red",
                color = markerColor
            };
            if (m.HasProperty("_BaseColor"))
                m.SetColor("_BaseColor", markerColor);

            // Soft glow if the shader supports emission so the marker pops
            // even against dark terrain. Silently ignored if unsupported.
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", markerColor * 0.6f);
            }
            r.sharedMaterial = m;
        }
        return v;
    }

    private static void SetLayerRecursive(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++)
            SetLayerRecursive(t.GetChild(i), layer);
    }

    /// <summary>
    /// Picks the right Lit shader for the active render pipeline at runtime so
    /// the marker is never magenta. Mirrors the editor-side resolver used in
    /// UpgradeSoldierVisual.cs / CreateEnemyDummy.cs.
    /// </summary>
    private static Shader ResolveLitShader()
    {
        RenderPipelineAsset rp = GraphicsSettings.defaultRenderPipeline;
        bool isURP = rp != null && rp.GetType().Name.Contains("Universal");

        Shader urp = Shader.Find("Universal Render Pipeline/Lit");
        Shader std = Shader.Find("Standard");

        if (isURP && urp != null) return urp;
        if (!isURP && std != null) return std;
        return urp ?? std ?? Shader.Find("Hidden/InternalErrorShader");
    }
}
