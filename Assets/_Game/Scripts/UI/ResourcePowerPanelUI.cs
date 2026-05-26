using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Bottom-right info box that owns the styled Resources + Power display.
/// Lives above the minimap inside the right column of the bottom HUD bar.
///
/// <see cref="RTSHUD"/> already drives the actual text content via its
/// <c>resourcesText</c> / <c>powerText</c> Inspector references. The point of
/// this component is purely visual:
///   • Holds extra references so future styling (icon recolour, low-power
///     flash) has somewhere to hook in without bloating RTSHUD.
///   • Owns the "LOW POWER" warning Graphic that's hidden until demand
///     exceeds supply.
///
/// Setup tool: SetupGameplayHUD creates the panel, wires this component to
/// the same TMP fields as RTSHUD, and assigns the warning Graphic.
/// </summary>
[DisallowMultipleComponent]
public class ResourcePowerPanelUI : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Text references (also bound to RTSHUD)")]
    [Tooltip("Same TMP as RTSHUD.resourcesText. RTSHUD writes the text; this " +
             "component handles tint/blink.")]
    public TextMeshProUGUI resourcesText;

    [Tooltip("Same TMP as RTSHUD.powerText.")]
    public TextMeshProUGUI powerText;

    [Header("Low-power warning indicator")]
    [Tooltip("Image that pulses red when power demand > supply. Optional — " +
             "if null, the warning is text-only via RTSHUD's powerText tint.")]
    public Image lowPowerIndicator;

    [Tooltip("How fast the low-power indicator pulses (Hz). Cosmetic.")]
    public float pulseSpeed = 3f;

    [Header("Pulse colours")]
    public Color lowPowerDim    = new Color(0.85f, 0.20f, 0.15f, 0.20f);
    public Color lowPowerBright = new Color(0.95f, 0.30f, 0.20f, 0.85f);

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private PowerManager powerManager;
    private bool         lookupAttempted;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (lowPowerIndicator != null)
            lowPowerIndicator.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (lowPowerIndicator == null) return;

        if (!lookupAttempted)
        {
            powerManager   = FindAnyObjectByType<PowerManager>();
            lookupAttempted = true;
        }

        bool low = powerManager != null && !powerManager.IsPowered;
        if (lowPowerIndicator.gameObject.activeSelf != low)
            lowPowerIndicator.gameObject.SetActive(low);

        if (low)
        {
            // 0..1 sin curve → lerp between dim and bright.
            float t = (Mathf.Sin(Time.time * pulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f;
            lowPowerIndicator.color = Color.Lerp(lowPowerDim, lowPowerBright, t);
        }
    }
}
