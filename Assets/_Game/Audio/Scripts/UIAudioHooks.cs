using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Drop this on a Canvas root to give EVERY <see cref="Button"/> under it a
/// click sound (and optional hover sound) — no per-button wiring required. It
/// re-scans on enable, so buttons revealed when a panel is shown also get
/// hooked.
///
/// These are purely local UI sounds: they play only on the machine that clicks,
/// never across the network, and never affect gameplay.
///
/// Setup: added automatically to MainMenuCanvas / HUDCanvas / EscapeMenuCanvas /
/// LobbyCanvas by Tools → RTS → Audio → Setup Audio System. You can also add it
/// by hand to any Canvas. Safe to run repeatedly — each button is wired once
/// (marked with a hidden tag component).
/// </summary>
[DisallowMultipleComponent]
public class UIAudioHooks : MonoBehaviour
{
    [Tooltip("Sound played when any child Button is clicked.")]
    public GameSound clickSound = GameSound.UIButtonClick;

    [Tooltip("If true, also play a hover sound when the pointer enters a Button.")]
    public bool playHoverSound = true;

    [Tooltip("Sound played on pointer-enter when Play Hover Sound is on.")]
    public GameSound hoverSound = GameSound.UIButtonHover;

    private void OnEnable()
    {
        // Re-scan whenever this canvas (or panel) becomes active so buttons that
        // were inactive at the previous scan still get wired.
        Rewire();
    }

    /// <summary>
    /// Wire every Button under this object that hasn't been wired yet. Idempotent:
    /// a hidden <see cref="UIAudioButtonTag"/> marks processed buttons so repeated
    /// calls don't stack listeners.
    /// </summary>
    public void Rewire()
    {
        Button[] buttons = GetComponentsInChildren<Button>(includeInactive: true);
        int wired = 0;

        for (int i = 0; i < buttons.Length; i++)
        {
            Button b = buttons[i];
            if (b == null) continue;
            if (b.GetComponent<UIAudioButtonTag>() != null) continue;   // already wired

            b.gameObject.AddComponent<UIAudioButtonTag>();

            // Click — captured locals so each button keeps its own ids.
            GameSound click = clickSound;
            b.onClick.AddListener(() => AudioManager.Sfx(click));

            // Hover — via an EventTrigger PointerEnter entry.
            if (playHoverSound)
                AddHover(b.gameObject, hoverSound);

            wired++;
        }

        if (wired > 0)
            Debug.Log($"[Audio] UIAudioHooks wired {wired} button(s) under '{name}'.");
    }

    private static void AddHover(GameObject go, GameSound sound)
    {
        EventTrigger trigger = go.GetComponent<EventTrigger>();
        if (trigger == null) trigger = go.AddComponent<EventTrigger>();

        var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        entry.callback.AddListener(_ => AudioManager.Sfx(sound));
        trigger.triggers.Add(entry);
    }
}

/// <summary>
/// Invisible marker so <see cref="UIAudioHooks.Rewire"/> only wires each Button
/// once, even across repeated OnEnable scans.
/// </summary>
[DisallowMultipleComponent]
public class UIAudioButtonTag : MonoBehaviour { }
