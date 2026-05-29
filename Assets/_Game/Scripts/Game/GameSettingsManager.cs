using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Static owner of DISPLAY settings (resolution / fullscreen / quality) plus
/// persistence + startup apply. Audio volume is owned by <see cref="AudioManager"/>
/// (single source of truth — never touched here), so there's no duplicate logic.
///
/// Display values are only changed when the Options menu's Apply button calls
/// <see cref="ApplyAndSave"/> — nothing is applied while the player is just
/// browsing dropdowns. Everything is local + cosmetic: no network, no scene
/// reload, no gameplay impact. Null-safe and editor-safe (Screen.SetResolution
/// is partially ignored inside the editor Game view — a Unity limitation that
/// resolves in a real build).
///
/// The resolution list is built ONLY from <see cref="Screen.resolutions"/> (the
/// player's actual monitor), deduped to unique width×height — never a hardcoded
/// list.
///
/// PlayerPrefs keys: Settings_ResolutionWidth / Settings_ResolutionHeight /
/// Settings_Fullscreen / Settings_QualityLevel. Volume keys live in AudioManager.
/// </summary>
public static class GameSettingsManager
{
    public const string KeyResWidth   = "Settings_ResolutionWidth";
    public const string KeyResHeight  = "Settings_ResolutionHeight";
    public const string KeyFullscreen = "Settings_Fullscreen";
    public const string KeyQuality    = "Settings_QualityLevel";

    // Deduped (width,height) list, built once from Screen.resolutions, ascending.
    private static List<Resolution> resolutions;

    /// <summary>Monitor-supported resolutions, deduped by width×height (ascending).</summary>
    public static IReadOnlyList<Resolution> AvailableResolutions
    {
        get { EnsureResolutions(); return resolutions; }
    }

    // ------------------------------------------------------------------ //
    // Startup apply — runs once before the first scene, so saved display
    // settings take effect even if the Options menu is never opened.
    // ------------------------------------------------------------------ //

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ApplyOnStartup()
    {
        if (PlayerPrefs.HasKey(KeyQuality))
            QualitySettings.SetQualityLevel(GetSavedQualityLevel(), true);

        FullScreenMode mode = GetSavedFullscreen()
            ? FullScreenMode.FullScreenWindow
            : FullScreenMode.Windowed;

        if (PlayerPrefs.HasKey(KeyResWidth) && PlayerPrefs.HasKey(KeyResHeight))
        {
            int w = PlayerPrefs.GetInt(KeyResWidth);
            int h = PlayerPrefs.GetInt(KeyResHeight);
            if (w > 0 && h > 0) Screen.SetResolution(w, h, mode);
            else                Screen.fullScreenMode = mode;
        }
        else
        {
            Screen.fullScreenMode = mode;
        }

        Debug.Log($"[Settings] Startup display applied (mode={mode}, " +
                  $"quality={(PlayerPrefs.HasKey(KeyQuality) ? GetSavedQualityLevel().ToString() : "default")}).");
    }

    // ------------------------------------------------------------------ //
    // Resolution list (monitor-only, deduped)
    // ------------------------------------------------------------------ //

    private static void EnsureResolutions()
    {
        if (resolutions != null) return;
        resolutions = new List<Resolution>();

        Resolution[] all = Screen.resolutions;
        if (all == null || all.Length == 0)
        {
            // Editor / headless — Screen.resolutions can be empty. Fall back to
            // the current screen size so the dropdown still has one valid entry.
            resolutions.Add(new Resolution { width = Mathf.Max(1, Screen.width),
                                             height = Mathf.Max(1, Screen.height) });
            return;
        }

        // Keep one entry per unique width×height (highest refresh rate is the
        // last occurrence since Screen.resolutions is ascending — we ignore the
        // rate and just store the size).
        var seen = new HashSet<long>();
        foreach (Resolution r in all)
        {
            long key = ((long)r.width << 20) ^ (uint)r.height;
            if (seen.Add(key))
                resolutions.Add(new Resolution { width = r.width, height = r.height });
        }
    }

    /// <summary>"1920 x 1080" labels for the dropdown.</summary>
    public static string[] ResolutionLabels()
    {
        EnsureResolutions();
        var labels = new string[resolutions.Count];
        for (int i = 0; i < resolutions.Count; i++)
            labels[i] = $"{resolutions[i].width} x {resolutions[i].height}";
        return labels;
    }

    /// <summary>
    /// Dropdown index to preselect: saved size if still supported → closest
    /// supported size → current screen size → last entry. Guarantees a valid
    /// index even if the saved resolution isn't available on this monitor.
    /// </summary>
    public static int CurrentResolutionIndex()
    {
        EnsureResolutions();

        if (PlayerPrefs.HasKey(KeyResWidth) && PlayerPrefs.HasKey(KeyResHeight))
        {
            int sw = PlayerPrefs.GetInt(KeyResWidth);
            int sh = PlayerPrefs.GetInt(KeyResHeight);
            int exact = IndexOf(sw, sh);
            if (exact >= 0) return exact;

            int closest = ClosestIndex(sw, sh);
            if (closest >= 0) return closest;
        }

        int cur = IndexOf(Screen.width, Screen.height);
        if (cur >= 0) return cur;

        int curClosest = ClosestIndex(Screen.width, Screen.height);
        return curClosest >= 0 ? curClosest : Mathf.Max(0, resolutions.Count - 1);
    }

    /// <summary>Index of the monitor's native/current size (or last entry).</summary>
    public static int NativeResolutionIndex()
    {
        EnsureResolutions();
        int cur = IndexOf(Screen.width, Screen.height);
        if (cur >= 0) return cur;
        int closest = ClosestIndex(Screen.width, Screen.height);
        return closest >= 0 ? closest : Mathf.Max(0, resolutions.Count - 1);
    }

    private static int IndexOf(int w, int h)
    {
        EnsureResolutions();
        for (int i = 0; i < resolutions.Count; i++)
            if (resolutions[i].width == w && resolutions[i].height == h) return i;
        return -1;
    }

    private static int ClosestIndex(int w, int h)
    {
        EnsureResolutions();
        int best = -1;
        long bestDist = long.MaxValue;
        for (int i = 0; i < resolutions.Count; i++)
        {
            long dw = resolutions[i].width - w;
            long dh = resolutions[i].height - h;
            long d = dw * dw + dh * dh;
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // ------------------------------------------------------------------ //
    // Getters for UI initialisation
    // ------------------------------------------------------------------ //

    public static bool GetSavedFullscreen()
        => PlayerPrefs.GetInt(KeyFullscreen, Screen.fullScreen ? 1 : 0) == 1;

    public static int GetSavedQualityLevel()
    {
        int max = Mathf.Max(0, QualitySettings.names.Length - 1);
        return Mathf.Clamp(PlayerPrefs.GetInt(KeyQuality, QualitySettings.GetQualityLevel()), 0, max);
    }

    public static int DefaultQualityIndex() => Mathf.Max(0, QualitySettings.names.Length - 1);

    // ------------------------------------------------------------------ //
    // Commit (called only by the Options menu's Apply button)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Apply AND persist resolution + fullscreen + quality together. This is the
    /// single commit path — display settings change only here, never while the
    /// player is browsing the dropdowns.
    /// </summary>
    public static void ApplyAndSave(int resolutionIndex, bool fullscreen, int qualityIndex)
    {
        EnsureResolutions();

        resolutionIndex = Mathf.Clamp(resolutionIndex, 0, Mathf.Max(0, resolutions.Count - 1));
        Resolution r = resolutions[resolutionIndex];

        int maxQ = Mathf.Max(0, QualitySettings.names.Length - 1);
        qualityIndex = Mathf.Clamp(qualityIndex, 0, maxQ);

        FullScreenMode mode = fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;

        QualitySettings.SetQualityLevel(qualityIndex, true);
        Screen.SetResolution(r.width, r.height, mode);

        PlayerPrefs.SetInt(KeyResWidth,  r.width);
        PlayerPrefs.SetInt(KeyResHeight, r.height);
        PlayerPrefs.SetInt(KeyFullscreen, fullscreen ? 1 : 0);
        PlayerPrefs.SetInt(KeyQuality,    qualityIndex);
        PlayerPrefs.Save();
    }
}
