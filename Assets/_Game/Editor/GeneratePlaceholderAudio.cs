using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Generates SIMPLE, royalty-free PLACEHOLDER audio (.wav) entirely in code so
/// the audio pipeline can be heard immediately — no downloads, no external
/// files, no PackageCache audio. Everything is synthesised from sine tones and
/// white noise at modest volume.
///
/// Menus (Tools → RTS → Audio):
///   • Generate Placeholder Test Audio            — write the WAVs + refresh.
///   • Generate And Assign Placeholder Test Audio — write + refresh + run the
///       full Setup Audio System wiring (library + AudioManager + auto-assign)
///       + add the F8/F9 debug keys. One click to hear sound.
///   • Play Test Sound In Editor                  — preview ui_click in edit mode.
///
/// These are TEMPORARY. Replace them anytime by dropping real clips into the
/// same folders (same/similar names) and re-running Auto Assign — see the report
/// printed at the end of generation.
/// </summary>
public static class GeneratePlaceholderAudio
{
    private const int   SampleRate = 22050;          // small files, plenty for placeholders
    private const string SfxDir      = "Assets/_Game/Audio/SFX";
    private const string MusicDir    = "Assets/_Game/Audio/Music";
    private const string AmbienceDir = "Assets/_Game/Audio/Ambience";

    private static System.Random rng;

    // ------------------------------------------------------------------ //
    // Menu entry points
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Audio/Generate Placeholder Test Audio")]
    public static void GenerateOnly()
    {
        int n = GenerateAll();
        AssetDatabase.Refresh();
        Debug.Log($"[PlaceholderAudio] Generated {n} placeholder WAV file(s). " +
                  "Now run Tools → RTS → Audio → Auto Assign Existing Audio Clips " +
                  "(or use 'Generate And Assign Placeholder Test Audio' next time).");
    }

    [MenuItem("Tools/RTS/Audio/Generate And Assign Placeholder Test Audio")]
    public static void GenerateAndAssign()
    {
        int n = GenerateAll();
        AssetDatabase.Refresh();

        // Run the full audio setup: ensures the SoundLibrary + AudioManager exist
        // and auto-assigns every clip (including menu/gameplay music + ambience)
        // by filename keyword. Non-overwriting, so any manual assignments stay.
        SetupAudioSystem.Run();

        AddDebugKeysToManager();

        Debug.Log($"[PlaceholderAudio] ✓ Generated {n} WAV(s), refreshed, and ran Setup Audio " +
                  "System (library + AudioManager + auto-assign). Press Play and listen — or use " +
                  "F8 (UI click) / F9 (explosion). Replace the WAVs in Assets/_Game/Audio/* with " +
                  "real clips anytime and re-run Auto Assign.");
    }

    [MenuItem("Tools/RTS/Audio/Play Test Sound In Editor")]
    public static void PlayTestSound()
    {
        AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(SfxDir + "/ui_click.wav");
        if (clip == null)
        {
            Debug.LogWarning("[PlaceholderAudio] No 'ui_click.wav' found. Run 'Generate Placeholder " +
                             "Test Audio' first.");
            return;
        }
        if (!EditorPreviewClip(clip))
            Debug.Log("[PlaceholderAudio] Editor preview not available on this Unity version — " +
                      "enter Play Mode and press F8 (UI click) / F9 (explosion) instead.");
    }

    // ------------------------------------------------------------------ //
    // Generation
    // ------------------------------------------------------------------ //

    private static int GenerateAll()
    {
        rng = new System.Random(20240517);   // deterministic noise → reproducible files
        EnsureDir(SfxDir);
        EnsureDir(MusicDir);
        EnsureDir(AmbienceDir);

        int count = 0;

        // --- UI -------------------------------------------------------- //
        count += Write(SfxDir, "ui_click",        Click(1200f, 0.06f, 0.30f));
        count += Write(SfxDir, "ui_hover",        Click(900f,  0.04f, 0.16f));
        count += Write(SfxDir, "ui_error",        Buzz(140f,  0.28f, 0.30f));
        count += Write(SfxDir, "ui_confirm",      TwoTone(660f, 880f, 0.18f, 0.28f));
        count += Write(SfxDir, "ui_open",         TwoTone(500f, 760f, 0.12f, 0.22f));
        count += Write(SfxDir, "ui_close",        TwoTone(760f, 500f, 0.12f, 0.22f));

        // --- Unit feedback -------------------------------------------- //
        count += Write(SfxDir, "unit_select",       Blip(1500f, 0.09f, 0.26f));
        count += Write(SfxDir, "unit_move_order",   TwoTone(720f, 1000f, 0.14f, 0.26f));
        count += Write(SfxDir, "unit_attack_order", TwoTone(900f, 600f,  0.14f, 0.28f));
        count += Write(SfxDir, "resource_gather",   TwoTone(880f, 1320f, 0.12f, 0.24f));

        // --- Combat ---------------------------------------------------- //
        count += Write(SfxDir, "gunfire",       NoisePop(0.09f, 0.34f, 9f,  1600f));
        count += Write(SfxDir, "turret_fire",   NoisePop(0.13f, 0.40f, 7f,  900f));
        count += Write(SfxDir, "rocket_launch",   Whoosh(0.40f, 220f, 1300f, 0.34f));
        count += Write(SfxDir, "missile_launch",  Whoosh(0.30f, 300f, 1500f, 0.32f));
        count += Write(SfxDir, "artillery_launch",Whoosh(0.36f, 150f, 700f,  0.38f));
        count += Write(SfxDir, "explosion",     Boom(0.55f, 0.50f, 6f));
        count += Write(SfxDir, "impact",        Thud(0.13f, 130f, 0.40f));

        // --- Destruction ---------------------------------------------- //
        count += Write(SfxDir, "unit_death",        Boom(0.28f, 0.40f, 12f));
        count += Write(SfxDir, "building_destroyed",Boom(0.75f, 0.55f, 4f));

        // --- Construction --------------------------------------------- //
        count += Write(SfxDir, "building_place",        Thud(0.18f, 180f, 0.34f));
        count += Write(SfxDir, "construction_loop",     BuildLoop(0.60f, 0.22f));
        count += Write(SfxDir, "construction_complete", Chime(0.45f, 0.30f));

        // --- Ambience + music (quiet, loopable) ----------------------- //
        count += Write(AmbienceDir, "battlefield_ambience",     Wind(3.0f, 0.14f));
        count += Write(MusicDir,    "menu_music_placeholder",   MusicLoop(4.0f, 0.13f, false));
        count += Write(MusicDir,    "gameplay_music_placeholder",MusicLoop(4.0f, 0.15f, true));

        return count;
    }

    // ------------------------------------------------------------------ //
    // Synth primitives — all return float[] samples in [-1, 1]
    // ------------------------------------------------------------------ //

    /// <summary>Sharp short click (sine + tiny noise edge), exp decay.</summary>
    private static float[] Click(float freq, float dur, float amp)
    {
        int n = N(dur);
        var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float env = Mathf.Exp(-t * 45f);
            b[i] = amp * env * (Sine(t, freq) * 0.8f + Noise() * 0.2f * Mathf.Exp(-t * 120f));
        }
        return Smooth(b);
    }

    /// <summary>Soft radio-ish blip — tone with a touch of amplitude wobble.</summary>
    private static float[] Blip(float freq, float dur, float amp)
    {
        int n = N(dur);
        var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float env = Mathf.Exp(-t * 30f);
            float wobble = 1f + 0.15f * Sine(t, 60f);
            b[i] = amp * env * Sine(t, freq * wobble);
        }
        return Smooth(b);
    }

    /// <summary>Low square-ish buzz for error/denied.</summary>
    private static float[] Buzz(float freq, float dur, float amp)
    {
        int n = N(dur);
        var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float env = AR(t, dur, 0.005f, 0.05f);
            float sq = Mathf.Sign(Sine(t, freq));               // square wave
            float sq2 = Mathf.Sign(Sine(t, freq * 1.01f));      // slight detune beat
            b[i] = amp * env * 0.5f * (sq + sq2) * 0.6f;
        }
        return b;
    }

    /// <summary>Two sequential tones (rising or falling) — command/confirm feel.</summary>
    private static float[] TwoTone(float f1, float f2, float dur, float amp)
    {
        int n = N(dur);
        var b = new float[n];
        int half = n / 2;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float f = i < half ? f1 : f2;
            float localT = i < half ? t : t - half / (float)SampleRate;
            float env = AR(localT, half / (float)SampleRate, 0.006f, 0.03f);
            b[i] = amp * env * Sine(t, f);
        }
        return Smooth(b);
    }

    /// <summary>Ascending 3-note success chime.</summary>
    private static float[] Chime(float dur, float amp)
    {
        float[] notes = { 523.25f, 659.25f, 783.99f };   // C5 E5 G5
        int n = N(dur);
        var b = new float[n];
        int seg = n / notes.Length;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            int idx = Mathf.Min(notes.Length - 1, i / seg);
            float localT = t - (idx * seg) / (float)SampleRate;
            float env = Mathf.Exp(-localT * 6f);
            b[i] = amp * env * Sine(t, notes[idx]);
        }
        return Smooth(b);
    }

    /// <summary>Short noisy pop (gunfire/MG). lowPass via one-pole emphasises body.</summary>
    private static float[] NoisePop(float dur, float amp, float decayK, float cutoff)
    {
        int n = N(dur);
        var b = new float[n];
        float lp = 0f;
        float a = Mathf.Clamp01(cutoff / SampleRate);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float env = Mathf.Exp(-t * decayK);
            float raw = Noise();
            lp += a * (raw - lp);                            // simple low-pass
            float mixed = Mathf.Lerp(lp, raw, 0.5f);
            b[i] = amp * env * mixed;
        }
        return Smooth(b);
    }

    /// <summary>Rising whoosh — noise + a sine sweeping fromHz→toHz, amp swells then fades.</summary>
    private static float[] Whoosh(float dur, float fromHz, float toHz, float amp)
    {
        int n = N(dur);
        var b = new float[n];
        float phase = 0f;
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float k = t / dur;
            float f = Mathf.Lerp(fromHz, toHz, k);
            phase += 2f * Mathf.PI * f / SampleRate;
            float swell = Mathf.Sin(Mathf.PI * k);           // 0→1→0 over the duration
            float raw = Noise();
            lp += 0.08f * (raw - lp);                        // airy low-passed noise
            float body = Mathf.Sin(phase) * 0.4f + lp * 0.8f;
            b[i] = amp * swell * body;
        }
        return Smooth(b);
    }

    /// <summary>Low noisy boom for explosions/destruction. Bigger decayK = snappier.</summary>
    private static float[] Boom(float dur, float amp, float decayK)
    {
        int n = N(dur);
        var b = new float[n];
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float env = Mathf.Exp(-t * decayK);
            float raw = Noise();
            lp += 0.06f * (raw - lp);                        // heavy low-pass = low rumble
            float sub = Sine(t, 60f) * 0.5f;                 // sub-bass thump
            b[i] = amp * env * (lp * 1.4f + sub);
        }
        return Smooth(b);
    }

    /// <summary>Short percussive hit — low sine + click of noise.</summary>
    private static float[] Thud(float dur, float freq, float amp)
    {
        int n = N(dur);
        var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float env = Mathf.Exp(-t * 22f);
            float click = Noise() * Mathf.Exp(-t * 90f) * 0.4f;
            b[i] = amp * (env * Sine(t, freq) + click);
        }
        return Smooth(b);
    }

    /// <summary>Loopable mechanical chug — a couple of low pulses per loop.</summary>
    private static float[] BuildLoop(float dur, float amp)
    {
        int n = N(dur);
        var b = new float[n];
        float pulseHz = 4f;                                  // chugs per second
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float pulse = Mathf.Pow(Mathf.Max(0f, Sine(t, pulseHz)), 6f);  // sharp rhythmic gate
            float tone = Sine(t, 110f) * 0.7f + Sine(t, 165f) * 0.3f;      // low mechanical body
            b[i] = amp * pulse * tone;
        }
        return b;                                            // already starts/ends near zero
    }

    /// <summary>Quiet looping wind — slowly modulated low-passed noise.</summary>
    private static float[] Wind(float dur, float amp)
    {
        int n = N(dur);
        var b = new float[n];
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float raw = Noise();
            lp += 0.015f * (raw - lp);                       // very dark noise
            float gust = 0.6f + 0.4f * Sine(t, 0.25f);       // slow swell
            b[i] = amp * lp * 3.0f * gust;
        }
        return b;
    }

    /// <summary>
    /// Very simple, quiet looping music bed — a slow arpeggio of low sine notes.
    /// <paramref name="driving"/> adds a soft pulse so gameplay feels a touch
    /// more active than the menu.
    /// </summary>
    private static float[] MusicLoop(float dur, float amp, bool driving)
    {
        // A minor-ish arpeggio (A2 C3 E3 A3) cycling over the loop.
        float[] notes = { 110.00f, 130.81f, 164.81f, 220.00f };
        int n = N(dur);
        var b = new float[n];
        int seg = Mathf.Max(1, n / 8);                       // 8 steps over the loop
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            int step = (i / seg) % notes.Length;
            float note = notes[step];
            float tone = Sine(t, note) * 0.6f + Sine(t, note * 2f) * 0.2f;
            float pad = Sine(t, 110f) * 0.15f;               // quiet drone under everything
            float pulse = driving ? (0.8f + 0.2f * Mathf.Pow(Mathf.Max(0f, Sine(t, 2f)), 4f)) : 1f;
            b[i] = amp * pulse * (tone + pad);
        }
        return b;
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static int   N(float seconds) => Mathf.Max(1, Mathf.RoundToInt(SampleRate * seconds));
    private static float Sine(float t, float freq) => Mathf.Sin(2f * Mathf.PI * freq * t);
    private static float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);

    /// <summary>Attack/Release envelope (linear) over a clip of length <paramref name="dur"/>.</summary>
    private static float AR(float t, float dur, float attack, float release)
    {
        float up = attack > 0f ? Mathf.Clamp01(t / attack) : 1f;
        float dn = release > 0f ? Mathf.Clamp01((dur - t) / release) : 1f;
        return Mathf.Min(up, dn);
    }

    /// <summary>Tiny fade-in/out to kill start/end clicks on one-shots.</summary>
    private static float[] Smooth(float[] b)
    {
        int fade = Mathf.Min(64, b.Length / 4);
        for (int i = 0; i < fade; i++)
        {
            float k = i / (float)fade;
            b[i] *= k;
            b[b.Length - 1 - i] *= k;
        }
        return b;
    }

    private static void EnsureDir(string assetDir)
    {
        string full = Path.Combine(Application.dataPath, assetDir.Substring("Assets/".Length));
        Directory.CreateDirectory(full);
    }

    /// <summary>Writes a mono 16-bit PCM WAV asset and imports it. Returns 1 on success.</summary>
    private static int Write(string assetDir, string fileName, float[] samples)
    {
        string assetPath = $"{assetDir}/{fileName}.wav";
        string fullPath  = Path.Combine(Application.dataPath,
                                        assetPath.Substring("Assets/".Length));
        try
        {
            WriteWav(fullPath, samples);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return 1;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[PlaceholderAudio] Failed to write '{assetPath}': {ex.Message}");
            return 0;
        }
    }

    private static void WriteWav(string fullPath, float[] samples)
    {
        int n = samples.Length;
        int dataSize = n * 2;
        using (var fs = new FileStream(fullPath, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));

            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);                       // Subchunk1Size (PCM)
            bw.Write((short)1);                 // AudioFormat = PCM
            bw.Write((short)1);                 // Channels = mono
            bw.Write(SampleRate);               // SampleRate
            bw.Write(SampleRate * 2);           // ByteRate = SR * channels * bytesPerSample
            bw.Write((short)2);                 // BlockAlign = channels * bytesPerSample
            bw.Write((short)16);                // BitsPerSample

            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);
            for (int i = 0; i < n; i++)
            {
                float s = Mathf.Clamp(samples[i], -1f, 1f);
                bw.Write((short)Mathf.RoundToInt(s * 32767f));
            }
        }
    }

    // ------------------------------------------------------------------ //
    // Add F8/F9 debug keys to the AudioManager (test aid)
    // ------------------------------------------------------------------ //

    private static void AddDebugKeysToManager()
    {
        AudioManager mgr = Object.FindFirstObjectByType<AudioManager>(FindObjectsInactive.Include);
        if (mgr == null)
        {
            Debug.LogWarning("[PlaceholderAudio] No AudioManager in scene to attach debug keys to. " +
                             "Run Tools → RTS → Audio → Setup Audio System first.");
            return;
        }
        if (mgr.GetComponent<AudioDebugKeys>() == null)
        {
            mgr.gameObject.AddComponent<AudioDebugKeys>();
            EditorUtility.SetDirty(mgr.gameObject);
            Debug.Log("[PlaceholderAudio] Added AudioDebugKeys (F8 = UI click, F9 = explosion) to " +
                      "the AudioManager. Delete it when you're done testing.");
        }
    }

    // ------------------------------------------------------------------ //
    // Edit-mode preview via reflection (AudioUtil API name varies by version)
    // ------------------------------------------------------------------ //

    private static bool EditorPreviewClip(AudioClip clip)
    {
        try
        {
            System.Type util = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");
            if (util == null) return false;

            foreach (MethodInfo m in util.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "PlayPreviewClip" && m.Name != "PlayClip") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 0 || ps[0].ParameterType != typeof(AudioClip)) continue;

                object[] args = new object[ps.Length];
                args[0] = clip;
                for (int i = 1; i < ps.Length; i++)
                {
                    if (ps[i].HasDefaultValue)            args[i] = ps[i].DefaultValue;
                    else if (ps[i].ParameterType == typeof(bool)) args[i] = false;
                    else if (ps[i].ParameterType.IsValueType)     args[i] = System.Activator.CreateInstance(ps[i].ParameterType);
                    else                                          args[i] = null;
                }
                m.Invoke(null, args);
                return true;
            }
        }
        catch { /* fall through to the Play-mode hint */ }
        return false;
    }
}
