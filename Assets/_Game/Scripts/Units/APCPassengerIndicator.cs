using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Small overhead 6-slot indicator showing how many passengers an APC is
/// currently carrying. Built procedurally at runtime (6 small cube quads),
/// billboards toward Camera.main, hides itself when the APC isn't selected
/// or when it's empty.
///
/// Hierarchy convention (set up by <c>CreateAPCPrefab</c> /
/// <c>RepairAPCTransport</c>):
///   APCRoot (has APCTransport + SelectableUnit)
///     └── PassengerIndicator (this script + 6 generated PaxSlot_N children)
///
/// Visual rules:
///   • Slot 0..N-1 = occupied → <see cref="occupiedColor"/> (green).
///   • Slot N..5   = empty     → <see cref="emptyColor"/> (grey).
///   • Indicator hidden when <see cref="onlyShowWhenSelected"/> is true and
///     the APC isn't selected.
///   • Indicator hidden when <see cref="hideWhenEmpty"/> is true and there
///     are zero passengers.
///
/// What this script intentionally does NOT do:
///   • Add colliders / physics — purely visual. Slots are on IgnoreRaycast.
///   • Refresh per-frame on a polling loop. It subscribes to
///     <see cref="APCTransport.OnPassengersChanged"/> and recolors only when
///     the passenger list actually changes.
///   • Touch the bottom transport panel in the HUD — that's RTSHUD's job.
/// </summary>
[DisallowMultipleComponent]
public class APCPassengerIndicator : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("References")]
    [Tooltip("APCTransport this indicator visualises. If null, auto-found via " +
             "transform.parent on Awake.")]
    public APCTransport transport;

    [Header("Layout")]
    [Tooltip("World-unit height above the APC pivot where the indicator floats. " +
             "Positioned slightly under the HealthBar so the two stack vertically.")]
    public float heightOffset = 1.9f;

    [Tooltip("Horizontal spacing between consecutive slot quads.")]
    public float slotSpacing = 0.18f;

    [Tooltip("Width × height of each slot quad, in world units.")]
    public Vector2 slotSize = new Vector2(0.14f, 0.10f);

    [Tooltip("Z-depth of each slot quad — slim so they sit flat against the camera.")]
    public float depth = 0.04f;

    [Header("Colours")]
    [ColorUsage(false)] public Color occupiedColor = new Color(0.22f, 0.82f, 0.22f); // green
    [ColorUsage(false)] public Color emptyColor    = new Color(0.50f, 0.50f, 0.50f); // grey

    [Header("Visibility")]
    [Tooltip("If true, the indicator is only shown while the APC is selected.")]
    public bool onlyShowWhenSelected = true;

    [Tooltip("If true, the indicator hides entirely when no passengers are aboard.")]
    public bool hideWhenEmpty = true;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private const int   SlotCount          = 6;
    private const int   IgnoreRaycastLayer = 2;

    private Transform[]    slotTransforms;
    private Material[]     slotMaterials;
    private Transform      owner;       // APC root
    private SelectableUnit selectable;
    private Camera         cam;

    private bool subscribedToTransport;
    private bool visibleLastFrame;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        owner = transform.parent;
        if (owner != null)
        {
            if (transport == null) transport = owner.GetComponent<APCTransport>();
            selectable = owner.GetComponent<SelectableUnit>();
        }

        BuildSlots();
        cam = Camera.main;

        if (transport != null)
        {
            transport.OnPassengersChanged += RefreshSlotColors;
            subscribedToTransport = true;
        }
        else
        {
            Debug.LogWarning($"[APCPassengerIndicator on '{name}']: " +
                             "no APCTransport on parent — indicator will stay grey.");
        }

        RefreshSlotColors();
    }

    private void OnDestroy()
    {
        if (subscribedToTransport && transport != null)
            transport.OnPassengersChanged -= RefreshSlotColors;
    }

    private void LateUpdate()
    {
        if (owner == null) return;

        // Re-cache Camera.main lazily — scene loads may create the camera
        // after this component's Awake.
        if (cam == null)
        {
            cam = Camera.main;
            if (cam == null) return;
        }

        // Position the indicator above the APC, billboarded to the camera.
        transform.position = owner.position + Vector3.up * heightOffset;
        transform.rotation = cam.transform.rotation;

        // Visibility rules — only toggle SetActive when state actually changes
        // so we don't churn dirty flags every frame.
        bool selected      = !onlyShowWhenSelected || (selectable != null && selectable.IsSelected);
        bool hasPassengers = transport != null && transport.PassengerCount > 0;
        bool visible       = selected && (!hideWhenEmpty || hasPassengers);

        if (visible == visibleLastFrame) return;
        visibleLastFrame = visible;
        for (int i = 0; i < slotTransforms.Length; i++)
            if (slotTransforms[i] != null)
                slotTransforms[i].gameObject.SetActive(visible);
    }

    // ------------------------------------------------------------------ //
    // Slot refresh — recolour based on current passenger count
    // ------------------------------------------------------------------ //

    private void RefreshSlotColors()
    {
        int count = transport != null ? transport.PassengerCount : 0;

        for (int i = 0; i < slotMaterials.Length; i++)
        {
            if (slotMaterials[i] == null) continue;
            Color c = i < count ? occupiedColor : emptyColor;
            slotMaterials[i].color = c;
            if (slotMaterials[i].HasProperty(BaseColorId))
                slotMaterials[i].SetColor(BaseColorId, c);
        }
    }

    // ------------------------------------------------------------------ //
    // Slot construction — runtime, mirrors the HealthBar build pattern
    // ------------------------------------------------------------------ //

    private void BuildSlots()
    {
        slotTransforms = new Transform[SlotCount];
        slotMaterials  = new Material[SlotCount];

        Shader sh = ResolveShader();

        // Centre the row around x=0 so it sits directly above the APC.
        float startX = -((SlotCount - 1) * 0.5f) * slotSpacing;

        for (int i = 0; i < SlotCount; i++)
        {
            // Re-use an existing child if it's there (Repair tool may have
            // pre-built it); otherwise create one.
            Transform existing = transform.Find($"PaxSlot_{i}");
            GameObject slot;
            if (existing != null)
            {
                slot = existing.gameObject;
            }
            else
            {
                slot = GameObject.CreatePrimitive(PrimitiveType.Cube);
                slot.name = $"PaxSlot_{i}";
                slot.transform.SetParent(transform, worldPositionStays: false);
                slot.transform.localPosition = new Vector3(startX + i * slotSpacing, 0f, 0f);

                Collider col = slot.GetComponent<Collider>();
                if (col != null) Destroy(col);
            }

            slot.transform.localScale = new Vector3(slotSize.x, slotSize.y, depth);
            slot.layer = IgnoreRaycastLayer;

            Renderer r = slot.GetComponent<Renderer>();
            if (r != null)
            {
                Material m = new Material(sh) { color = emptyColor };
                if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, emptyColor);
                r.sharedMaterial    = m;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows    = false;
                slotMaterials[i] = m;
            }

            slotTransforms[i] = slot.transform;
        }

        // Layer every child onto IgnoreRaycast (defence in depth).
        SetLayerRecursive(transform, IgnoreRaycastLayer);
    }

    private static void SetLayerRecursive(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++)
            SetLayerRecursive(t.GetChild(i), layer);
    }

    private static Shader ResolveShader()
    {
        Shader urp = Shader.Find("Universal Render Pipeline/Lit");
        Shader std = Shader.Find("Standard");
        if (urp != null) return urp;
        if (std != null) return std;
        return Shader.Find("Hidden/InternalErrorShader");
    }
}
