using UnityEngine;

/// <summary>
/// A single, reusable sound definition — one or more interchangeable clips plus
/// the playback variation rules around them. Stored inside a
/// <see cref="SoundLibrary"/> and played by <see cref="AudioManager"/>.
///
/// Why a class and not just an AudioClip:
///   • Random clip variation (footsteps / gunfire feel alive when 1-of-N plays).
///   • Per-event pitch + volume jitter so repeated triggers don't sound robotic.
///   • A per-event spam guard (<see cref="minInterval"/>) so 30 units firing in
///     the same frame collapse to one audible shot instead of a wall of noise.
///
/// Everything is null-safe: an event with no clips simply never plays, so the
/// game runs fine before you have actual audio files to drop in.
/// </summary>
[System.Serializable]
public class SoundEvent
{
    [Tooltip("One or more clips. A random one is chosen on each play so the " +
             "sound doesn't feel repetitive. Leave empty to silently disable " +
             "this event (the game still runs).")]
    public AudioClip[] clips;

    [Range(0f, 1f)]
    [Tooltip("Base volume for this event (before the global SFX + Master sliders).")]
    public float volume = 0.9f;

    [Tooltip("Random pitch picked between X and Y on each play. Keep it tight " +
             "(0.95–1.05) for natural variation; widen for chaotic SFX.")]
    public Vector2 pitchRange = new Vector2(0.96f, 1.04f);

    [Range(0f, 0.5f)]
    [Tooltip("Random downward volume jitter. 0 = always full volume; 0.1 = each " +
             "play is 0–10% quieter. Adds life to bursts of the same sound.")]
    public float volumeJitter = 0.05f;

    [Tooltip("Minimum seconds between two plays of THIS event across the whole " +
             "game (spam guard). Many units triggering the same sound in one " +
             "frame collapse to a single play. 0 = no limit.")]
    public float minInterval = 0.04f;

    [Header("3D distance override (optional — world sounds only)")]
    [Tooltip("If true, this sound ignores the AudioManager's category max-distance " +
             "and uses the Min/Max below instead. Leave OFF to inherit the " +
             "category default (combat / explosion / construction / …). Has no " +
             "effect on 2D (UI / local) sounds.")]
    public bool useCustom3DDistance = false;

    [Tooltip("Custom near-distance (full volume within this radius). Used only " +
             "when Use Custom 3D Distance is on.")]
    public float customMinDistance = 12f;

    [Tooltip("Custom far-distance (sound is fully silent at and beyond this). " +
             "Used only when Use Custom 3D Distance is on.")]
    public float customMaxDistance = 60f;

    /// <summary>True when at least one non-null clip is assigned.</summary>
    public bool HasClip
    {
        get
        {
            if (clips == null) return false;
            for (int i = 0; i < clips.Length; i++)
                if (clips[i] != null) return true;
            return false;
        }
    }

    /// <summary>Returns a random non-null clip, or null when none are assigned.</summary>
    public AudioClip PickClip()
    {
        if (clips == null || clips.Length == 0) return null;

        // Fast path — single clip.
        if (clips.Length == 1) return clips[0];

        // Try a few random indices, then fall back to the first non-null.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            AudioClip c = clips[Random.Range(0, clips.Length)];
            if (c != null) return c;
        }
        for (int i = 0; i < clips.Length; i++)
            if (clips[i] != null) return clips[i];
        return null;
    }

    /// <summary>Random pitch within <see cref="pitchRange"/>.</summary>
    public float PickPitch()
    {
        float lo = Mathf.Min(pitchRange.x, pitchRange.y);
        float hi = Mathf.Max(pitchRange.x, pitchRange.y);
        if (hi <= 0f) return 1f;
        return Random.Range(lo, hi);
    }

    /// <summary>Base volume with the downward jitter applied (0..1).</summary>
    public float PickVolume()
    {
        return Mathf.Clamp01(volume - Random.Range(0f, volumeJitter));
    }
}
