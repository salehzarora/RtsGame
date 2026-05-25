using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Mouse-following cursor indicator that appears when the player hovers a
/// friendly APC with at least one boardable infantry unit selected.
///
/// Three states:
///   • Hidden     — selection has no boardable infantry, hover isn't on an APC,
///                  or input is gated (UI / placement mode / menu).
///   • Boardable  — friendly APC with free seats AND ≥1 valid passenger selected.
///                  Indicator is green, "ENTER" label, pulsing arrow above cursor.
///   • Full       — friendly APC with valid passenger selected but no seats.
///                  Indicator turns red and the label reads "FULL".
///
/// Validation is delegated to <see cref="APCTransport.CanLoad"/> so the hover
/// state stays in lock-step with the right-click boarding rule (infantry only,
/// no Workers, friendly team, not already loaded).
///
/// What this script intentionally does NOT do:
///   • Drive boarding itself — the right-click path in UnitSelector still
///     owns the command. This is a UI cue only.
///   • Touch the standard cursor (Cursor.SetCursor) — uses a UI element so
///     overlay rules work consistently across the project.
///   • Add a per-APC world-space marker. Cursor-following per spec.
/// </summary>
[DisallowMultipleComponent]
public class TransportHoverIndicator : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // States
    // ------------------------------------------------------------------ //

    private enum State { Hidden, Boardable, Full }

    // ------------------------------------------------------------------ //
    // Inspector — references (wired by SetupRTSHUD)
    // ------------------------------------------------------------------ //

    [Header("Indicator references (set up by SetupRTSHUD)")]
    [Tooltip("Root RectTransform of the boarding cursor. Its parent is the " +
             "HUDCanvas. SetActive() drives the visibility flip.")]
    public RectTransform indicatorRoot;

    [Tooltip("Background Image — recoloured per state (green = boardable, " +
             "red = full).")]
    public Image indicatorBackground;

    [Tooltip("TMP arrow glyph ('▲') that bobs vertically when the indicator " +
             "is in Boardable state.")]
    public TextMeshProUGUI indicatorArrow;

    [Tooltip("TMP label — 'ENTER' for Boardable, 'FULL' for Full.")]
    public TextMeshProUGUI indicatorLabel;

    // ------------------------------------------------------------------ //
    // Inspector — visuals + tuning
    // ------------------------------------------------------------------ //

    [Header("Colours")]
    [Tooltip("Background colour when boarding is possible.")]
    public Color boardingColor = new Color(0.20f, 0.75f, 0.30f, 0.85f);

    [Tooltip("Background colour when the APC is full.")]
    public Color fullColor = new Color(0.75f, 0.20f, 0.20f, 0.85f);

    // No cursor offset — the boarding logo REPLACES the OS cursor and must
    // follow Input.mousePosition exactly. Pivot of the indicator RectTransform
    // is (0.5, 0.5), so transform.position = mouse pixel coords centres the
    // logo on the pointer in a Screen Space Overlay canvas.

    [Header("Hover stability")]
    [Tooltip("Seconds the boarding cursor stays shown after the hover stops being " +
             "valid. Smooths over one-frame raycast misses (e.g. mouse passing " +
             "over a hull seam between two colliders). 0 to disable the grace.")]
    public float hoverGraceTime = 0.12f;

    [Header("Animation")]
    [Tooltip("Speed of the pulse-scale animation (radians/sec input to sin).")]
    public float pulseSpeed = 6f;

    [Tooltip("Amplitude of the pulse-scale animation (delta around 1.0×).")]
    public float pulseAmount = 0.10f;

    [Tooltip("Vertical bob speed of the arrow glyph (radians/sec input to sin).")]
    public float arrowBobSpeed = 8f;

    [Tooltip("Vertical bob amplitude of the arrow glyph (pixels).")]
    public float arrowBobAmount = 4f;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private Camera     mainCam;
    private LayerMask  raycastMask;
    private bool       raycastMaskResolved;
    private State      state = State.Hidden;
    private Vector2    arrowBasePos;

    // Tracks whether we currently own the OS cursor's visibility. Used to
    // restore Cursor.visible exactly once on hide / disable / destroy without
    // stomping unrelated Cursor.visible changes from other systems.
    private bool       cursorHidden;

    // Most recent APC under the mouse that passed every validation gate,
    // plus the Time.time stamp of that frame. Together they drive the grace
    // window — we keep showing the cursor for hoverGraceTime seconds after
    // the last valid frame to absorb one-frame raycast misses.
    private APCTransport currentHoverApc;
    private float        lastValidHoverTime = float.NegativeInfinity;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        mainCam = Camera.main;
        if (indicatorRoot != null)
        {
            // Critical: every Graphic under the cursor logo must have
            // raycastTarget = false. Otherwise EventSystem.IsPointerOverGameObject
            // returns true the frame the logo appears under the mouse, which
            // hides the logo, which makes EventSystem return false, which
            // re-shows it… classic flicker loop. Sweep recursively so any
            // future child (e.g. extra icon overlay) is safe by default.
            DisableRaycastTargetsRecursive(indicatorRoot);

            indicatorRoot.gameObject.SetActive(false);
        }

        if (indicatorArrow != null)
            arrowBasePos = indicatorArrow.rectTransform.anchoredPosition;
    }

    private static void DisableRaycastTargetsRecursive(RectTransform root)
    {
        Graphic[] graphics = root.GetComponentsInChildren<Graphic>(includeInactive: true);
        for (int i = 0; i < graphics.Length; i++)
            if (graphics[i] != null) graphics[i].raycastTarget = false;

        CanvasGroup cg = root.GetComponent<CanvasGroup>();
        if (cg != null)
        {
            cg.blocksRaycasts = false;
            cg.interactable   = false;
        }
    }

    private void OnDisable()
    {
        // Restore the OS cursor in case we hid it while the indicator was visible.
        // Defensive: if the player disables this component mid-hover, we don't
        // want them stranded without a pointer.
        RestoreCursor();
    }

    private void OnDestroy()
    {
        RestoreCursor();
    }

    private void Update()
    {
        // Lazy-resolve the layer mask once the layers exist in the project.
        if (!raycastMaskResolved) ResolveRaycastMask();

        // Re-cache the camera if the scene swapped it (e.g. mid-play).
        if (mainCam == null) mainCam = Camera.main;

        bool hoverIsValid = ComputeHoverValid();

        if (hoverIsValid)
        {
            // Valid hover this frame — refresh the grace timestamp and show
            // the cursor (no-op if already shown).
            lastValidHoverTime = Time.time;
            SetState(State.Boardable);
        }
        else if (state == State.Boardable &&
                 Time.time - lastValidHoverTime <= hoverGraceTime)
        {
            // Grace window — hover went invalid this frame but the last valid
            // frame was very recent. Keep the cursor shown to smooth over a
            // single-frame raycast miss. Don't transition state.
        }
        else
        {
            // Either we never showed the cursor, or the grace window has
            // elapsed without a re-validation. Hide cleanly.
            SetState(State.Hidden);
            currentHoverApc = null;
        }

        if (state == State.Boardable)
        {
            UpdatePosition();
            UpdateAnimation();
        }
    }

    /// <summary>
    /// Runs every precondition + raycast + validation check. Returns true
    /// when the player is currently hovering a friendly APC with space and
    /// has at least one boardable infantry selected. Side effect: caches the
    /// hit APC in <see cref="currentHoverApc"/> for diagnostic access.
    /// </summary>
    private bool ComputeHoverValid()
    {
        if (mainCam == null)                              return false;
        if (indicatorRoot == null)                        return false;
        if (!GameStateManager.IsPlaying)                  return false;
        if (BuildingPlacementManager.IsPlacing)           return false;
        if (EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject()) return false;

        UnitSelector sel = UnitSelector.Instance;
        if (sel == null || sel.SelectedUnits == null || sel.SelectedUnits.Count == 0)
            return false;

        APCTransport apc = ResolveHoveredAPC();
        if (apc == null) return false;

        Health apcHealth = apc.GetComponent<Health>();
        if (apcHealth == null || apcHealth.team != Health.Team.Player) return false;

        if (!AnySelectedCanBoard(sel, apc)) return false;

        // Full APC counts as invalid — spec: "no boarding possible = normal
        // cursor". The grace window still applies so the indicator doesn't
        // flicker if the APC briefly becomes full during a multi-load.
        if (!apc.HasSpace()) return false;

        currentHoverApc = apc;
        return true;
    }

    // ------------------------------------------------------------------ //
    // Hover resolution
    // ------------------------------------------------------------------ //

    private APCTransport ResolveHoveredAPC()
    {
        Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, raycastMask)) return null;

        return hit.collider.GetComponent<APCTransport>()
            ?? hit.collider.GetComponentInParent<APCTransport>();
    }

    /// <summary>
    /// Returns true if at least one currently-selected unit passes
    /// <see cref="APCTransport.CanLoad"/>. The check matches the right-click
    /// boarding path exactly — same component, same rule.
    /// </summary>
    private static bool AnySelectedCanBoard(UnitSelector sel, APCTransport apc)
    {
        var units = sel.SelectedUnits;
        for (int i = 0; i < units.Count; i++)
        {
            SelectableUnit u = units[i];
            if (u == null) continue;
            if (apc.CanLoad(u.gameObject)) return true;
        }
        return false;
    }

    private void ResolveRaycastMask()
    {
        int unit = LayerMask.NameToLayer("Unit");
        int bld  = LayerMask.NameToLayer("Building");
        int mask = 0;
        if (unit >= 0) mask |= 1 << unit;
        if (bld  >= 0) mask |= 1 << bld;
        raycastMask = mask != 0 ? (LayerMask)mask : ~0;
        raycastMaskResolved = true;
    }

    // ------------------------------------------------------------------ //
    // State + visuals
    // ------------------------------------------------------------------ //

    private void SetState(State next)
    {
        if (next == state) return;
        state = next;

        bool visible = next != State.Hidden;

        // Toggle the indicator GameObject ↔ OS cursor in lock-step. The
        // boarding logo REPLACES the system pointer while it's shown so the
        // player sees a single, unambiguous icon at the cursor position.
        // indicatorRoot null-guard: if the UI was destroyed (scene reload,
        // HUD rebuild) we still want the cursor restore to fire.
        if (indicatorRoot != null && indicatorRoot.gameObject.activeSelf != visible)
            indicatorRoot.gameObject.SetActive(visible);

        if (visible)
        {
            HideCursor();
        }
        else
        {
            RestoreCursor();
            Debug.Log("[TransportHover] Boarding cursor hidden.");
            return;
        }

        // Boardable is currently the only non-Hidden state actually entered;
        // Full kept in the enum for future expansion but unreachable today.
        Color tint = next == State.Boardable ? boardingColor : fullColor;
        if (indicatorBackground != null) indicatorBackground.color = tint;

        if (indicatorArrow != null)
        {
            indicatorArrow.text  = "▲";
            indicatorArrow.color = Color.white;
        }

        if (indicatorLabel != null)
            indicatorLabel.text = "ENTER";

        Debug.Log("[TransportHover] Boarding cursor shown.");
    }

    /// <summary>
    /// Centres the indicator's pivot on the OS pointer. For a Screen Space
    /// Overlay canvas, transform.position is screen-pixel coordinates — no
    /// canvas-scaler math required. The indicator's pivot is (0.5, 0.5), so
    /// the logo's centre lands exactly on Input.mousePosition.
    /// </summary>
    private void UpdatePosition()
    {
        if (indicatorRoot == null) return;

        Vector3 mouse = Input.mousePosition;
        mouse.z = 0f;     // overlay canvas sits at Z = 0
        indicatorRoot.position = mouse;
    }

    // ------------------------------------------------------------------ //
    // OS cursor visibility — hidden while the boarding logo is shown
    // ------------------------------------------------------------------ //

    private void HideCursor()
    {
        if (cursorHidden) return;
        Cursor.visible = false;
        cursorHidden = true;
    }

    private void RestoreCursor()
    {
        if (!cursorHidden) return;
        Cursor.visible = true;
        cursorHidden = false;
    }

    private void UpdateAnimation()
    {
        float t = Time.time;

        // Whole indicator pulses subtly.
        float scale = 1.0f + pulseAmount * Mathf.Sin(t * pulseSpeed);
        indicatorRoot.localScale = new Vector3(scale, scale, 1f);

        // Arrow bobs vertically (only when Boardable — Full state hides motion).
        if (indicatorArrow != null)
        {
            float bob = state == State.Boardable
                ? arrowBobAmount * Mathf.Sin(t * arrowBobSpeed)
                : 0f;
            indicatorArrow.rectTransform.anchoredPosition = arrowBasePos + new Vector2(0f, bob);
        }
    }
}
