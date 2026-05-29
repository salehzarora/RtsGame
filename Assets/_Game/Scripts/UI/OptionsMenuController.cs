using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Reusable Options / Settings panel, shared by the main menu and the in-game
/// ESC menu. Uses a PENDING-settings model with a real Apply button:
///
///   • Audio sliders preview LIVE while dragging (so you hear the change) but
///     are only persisted when Apply is pressed. Pressing Back/Cancel restores
///     the audio that was active when the panel opened.
///   • Display (resolution / fullscreen / quality) is NOT touched while you
///     browse — it's held as pending values and applied + saved only on Apply.
///     Back/Cancel discards pending display changes (nothing was applied).
///
/// Everything is local-only and null-safe: no network events, no scene reload,
/// no Photon impact, no time pausing. Opening it in-game leaves the match and
/// the Photon room untouched.
///
/// Lives on the always-active OptionsCanvas root and toggles a child
/// <see cref="optionsPanel"/>, so it stays alive to receive the next open call.
/// </summary>
[DisallowMultipleComponent]
public class OptionsMenuController : MonoBehaviour
{
    [Header("Panel (child object this controller shows/hides)")]
    [Tooltip("The Options card/panel GameObject. Toggled active on open/close. " +
             "Must NOT be this controller's own GameObject.")]
    public GameObject optionsPanel;

    [Header("Return targets (the panel to re-show on Back)")]
    public GameObject mainMenuReturn;     // MainMenuCanvas
    public GameObject escapeMenuReturn;   // EscapeMenuCanvas

    [Header("Audio")]
    public Slider masterSlider;
    public Slider sfxSlider;
    public Slider musicSlider;
    public TextMeshProUGUI masterPercent;
    public TextMeshProUGUI sfxPercent;
    public TextMeshProUGUI musicPercent;

    [Header("Display")]
    public TMP_Dropdown resolutionDropdown;
    public Toggle fullscreenToggle;
    public TMP_Dropdown qualityDropdown;

    [Header("Buttons")]
    public Button applyButton;
    public Button backButton;
    public Button resetButton;

    // ---- Pending (shown in UI, not yet committed) --------------------- //
    private float pendingMaster, pendingSfx, pendingMusic;
    private int   pendingResIndex;
    private bool  pendingFullscreen;
    private int   pendingQuality;

    // Audio that was active when the panel opened — restored on Cancel.
    private float origMaster, origSfx, origMusic;

    private GameObject currentReturn;
    private bool initialized;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (optionsPanel != null) optionsPanel.SetActive(false);
    }

    private void Start() => Initialize();

    /// <summary>Bind listeners + populate dropdowns once. Idempotent.</summary>
    private void Initialize()
    {
        if (initialized) return;
        initialized = true;

        if (masterSlider != null) { masterSlider.minValue = 0f; masterSlider.maxValue = 1f; masterSlider.onValueChanged.AddListener(OnMasterChanged); }
        if (sfxSlider    != null) { sfxSlider.minValue    = 0f; sfxSlider.maxValue    = 1f; sfxSlider.onValueChanged.AddListener(OnSfxChanged); }
        if (musicSlider  != null) { musicSlider.minValue  = 0f; musicSlider.maxValue  = 1f; musicSlider.onValueChanged.AddListener(OnMusicChanged); }

        PopulateResolutionDropdown();
        PopulateQualityDropdown();

        if (resolutionDropdown != null) resolutionDropdown.onValueChanged.AddListener(i => pendingResIndex = i);
        if (fullscreenToggle   != null) fullscreenToggle.onValueChanged.AddListener(b => pendingFullscreen = b);
        if (qualityDropdown    != null) qualityDropdown.onValueChanged.AddListener(i => pendingQuality = i);

        if (applyButton != null) applyButton.onClick.AddListener(ApplySettings);
        if (backButton  != null) backButton.onClick.AddListener(CloseOptionsWithoutApplying);
        if (resetButton != null) resetButton.onClick.AddListener(ResetDefaults);
    }

    private void PopulateResolutionDropdown()
    {
        if (resolutionDropdown == null) return;
        resolutionDropdown.ClearOptions();
        resolutionDropdown.AddOptions(new List<string>(GameSettingsManager.ResolutionLabels()));
    }

    private void PopulateQualityDropdown()
    {
        if (qualityDropdown == null) return;
        qualityDropdown.ClearOptions();
        qualityDropdown.AddOptions(new List<string>(QualitySettings.names));
    }

    // ------------------------------------------------------------------ //
    // Open / close — wired to the two Options buttons
    // ------------------------------------------------------------------ //

    public void OpenOptionsFromMainMenu() => Open(mainMenuReturn);
    public void OpenOptionsFromPauseMenu() => Open(escapeMenuReturn);

    private void Open(GameObject returnTarget)
    {
        Initialize();
        currentReturn = returnTarget;

        // Snapshot the audio that's live right now so Cancel can restore it.
        AudioManager am = AudioManager.Instance;
        origMaster = am != null ? am.GetMasterVolume() : 1f;
        origSfx    = am != null ? am.GetSfxVolume()    : 1f;
        origMusic  = am != null ? am.GetMusicVolume()  : 0.6f;

        // Pending starts from the currently saved/applied values.
        pendingMaster = origMaster;
        pendingSfx    = origSfx;
        pendingMusic  = origMusic;
        pendingResIndex   = GameSettingsManager.CurrentResolutionIndex();
        pendingFullscreen = GameSettingsManager.GetSavedFullscreen();
        pendingQuality    = GameSettingsManager.GetSavedQualityLevel();

        RefreshUIFromPending();

        if (returnTarget != null) returnTarget.SetActive(false);
        if (optionsPanel != null) optionsPanel.SetActive(true);
        AudioManager.Sfx(GameSound.UIOpenPanel);
    }

    /// <summary>Back / Cancel — discard pending display, restore previewed audio, return.</summary>
    public void CloseOptionsWithoutApplying()
    {
        AudioManager am = AudioManager.Instance;
        if (am != null)
        {
            // Restore the audio that was live on open (preview only — no save).
            am.SetMasterVolume(origMaster, false);
            am.SetSfxVolume(origSfx, false);
            am.SetMusicVolume(origMusic, false);
        }
        // Display was never applied (Apply is the only commit path), so there's
        // nothing to revert in the engine.

        if (optionsPanel != null) optionsPanel.SetActive(false);
        if (currentReturn != null) currentReturn.SetActive(true);
        currentReturn = null;
        AudioManager.Sfx(GameSound.UIClosePanel);
    }

    // ------------------------------------------------------------------ //
    // Apply — the single commit path
    // ------------------------------------------------------------------ //

    public void ApplySettings()
    {
        AudioManager am = AudioManager.Instance;
        if (am != null)
        {
            am.SetMasterVolume(pendingMaster, true);   // apply + save
            am.SetSfxVolume(pendingSfx, true);
            am.SetMusicVolume(pendingMusic, true);
        }

        GameSettingsManager.ApplyAndSave(pendingResIndex, pendingFullscreen, pendingQuality);

        // New baseline so a later Cancel reverts to what we just applied.
        origMaster = pendingMaster;
        origSfx    = pendingSfx;
        origMusic  = pendingMusic;

        var res = GameSettingsManager.AvailableResolutions;
        string resStr = (pendingResIndex >= 0 && pendingResIndex < res.Count)
            ? $"{res[pendingResIndex].width}x{res[pendingResIndex].height}"
            : "?";
        string[] qNames = QualitySettings.names;
        string qStr = (pendingQuality >= 0 && pendingQuality < qNames.Length) ? qNames[pendingQuality] : "?";

        Debug.Log($"Settings applied: {resStr} Fullscreen={pendingFullscreen} Quality={qStr} " +
                  $"Master={pendingMaster:0.0#} SFX={pendingSfx:0.0#} Music={pendingMusic:0.0#}");

        AudioManager.Sfx(GameSound.UIConfirm);
    }

    /// <summary>Reset pending values to defaults and preview them (commit still needs Apply).</summary>
    public void ResetDefaults()
    {
        pendingMaster = 1f;
        pendingSfx    = 1f;
        pendingMusic  = 0.6f;
        pendingFullscreen = true;
        pendingQuality    = GameSettingsManager.DefaultQualityIndex();
        pendingResIndex   = GameSettingsManager.NativeResolutionIndex();

        // Preview the default audio live so the change is audible immediately.
        AudioManager am = AudioManager.Instance;
        if (am != null)
        {
            am.SetMasterVolume(pendingMaster, false);
            am.SetSfxVolume(pendingSfx, false);
            am.SetMusicVolume(pendingMusic, false);
        }

        RefreshUIFromPending();
        AudioManager.Sfx(GameSound.UIButtonClick);
    }

    // ------------------------------------------------------------------ //
    // Audio slider callbacks — preview live (no save), update pending + label
    // ------------------------------------------------------------------ //

    private void OnMasterChanged(float v)
    {
        pendingMaster = v;
        AudioManager.Instance?.SetMasterVolume(v, false);
        UpdatePercent(masterPercent, v);
    }

    private void OnSfxChanged(float v)
    {
        pendingSfx = v;
        AudioManager.Instance?.SetSfxVolume(v, false);
        UpdatePercent(sfxPercent, v);
        AudioManager.Sfx(GameSound.UIButtonHover);   // audible feedback at the new SFX level
    }

    private void OnMusicChanged(float v)
    {
        pendingMusic = v;
        AudioManager.Instance?.SetMusicVolume(v, false);
        UpdatePercent(musicPercent, v);
    }

    private static void UpdatePercent(TextMeshProUGUI label, float v)
    {
        if (label != null) label.text = Mathf.RoundToInt(Mathf.Clamp01(v) * 100f) + "%";
    }

    // ------------------------------------------------------------------ //
    // Push pending → controls (without firing callbacks)
    // ------------------------------------------------------------------ //

    private void RefreshUIFromPending()
    {
        if (masterSlider != null) masterSlider.SetValueWithoutNotify(pendingMaster);
        if (sfxSlider    != null) sfxSlider.SetValueWithoutNotify(pendingSfx);
        if (musicSlider  != null) musicSlider.SetValueWithoutNotify(pendingMusic);
        UpdatePercent(masterPercent, pendingMaster);
        UpdatePercent(sfxPercent,    pendingSfx);
        UpdatePercent(musicPercent,  pendingMusic);

        if (resolutionDropdown != null)
        {
            resolutionDropdown.SetValueWithoutNotify(pendingResIndex);
            resolutionDropdown.RefreshShownValue();
        }
        if (fullscreenToggle != null)
            fullscreenToggle.SetIsOnWithoutNotify(pendingFullscreen);
        if (qualityDropdown != null)
        {
            qualityDropdown.SetValueWithoutNotify(pendingQuality);
            qualityDropdown.RefreshShownValue();
        }
    }
}
