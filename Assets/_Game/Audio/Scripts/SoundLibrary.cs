using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Inspector-friendly ScriptableObject that maps each <see cref="GameSound"/> id
/// to a <see cref="SoundEvent"/> (clips + variation rules). This is where you
/// assign your actual audio files — one asset, one place, expandable.
///
/// Create via: Assets → Create → RTS → Audio → Sound Library, OR let
/// Tools → RTS → Audio → Setup Audio System create + wire one for you.
///
/// Each entry is keyed by its enum id (NOT list order), so you can freely
/// reorder/insert rows without losing assignments. Use "Populate Missing
/// Entries" (context menu) after adding new <see cref="GameSound"/> values.
/// </summary>
[CreateAssetMenu(fileName = "GameSoundLibrary", menuName = "RTS/Audio/Sound Library", order = 0)]
public class SoundLibrary : ScriptableObject
{
    [System.Serializable]
    public class Entry
    {
        [Tooltip("Which logical sound this row defines.")]
        public GameSound id;

        [Tooltip("Clips + playback variation for this sound. Leave clips empty " +
                 "to keep it silent until you have an audio file.")]
        public SoundEvent sound = new SoundEvent();
    }

    [Tooltip("One row per sound. Keyed by id — order is cosmetic. Run the context " +
             "menu 'Populate Missing Entries' to add a row for every GameSound.")]
    public List<Entry> entries = new List<Entry>();

    // Built lazily from `entries` at runtime; invalidated by OnEnable / edits.
    private Dictionary<GameSound, SoundEvent> map;

    /// <summary>
    /// Returns the <see cref="SoundEvent"/> for <paramref name="id"/>, or null
    /// when no row exists (or its clips are empty). Null-safe callers simply
    /// skip playback.
    /// </summary>
    public SoundEvent Get(GameSound id)
    {
        if (map == null) RebuildMap();
        return map.TryGetValue(id, out SoundEvent ev) ? ev : null;
    }

    /// <summary>Rebuilds the id → event lookup from the serialized list.</summary>
    public void RebuildMap()
    {
        map = new Dictionary<GameSound, SoundEvent>();
        if (entries == null) return;
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if (e == null || e.sound == null) continue;
            map[e.id] = e.sound;     // last wins on accidental duplicates
        }
    }

    private void OnEnable()  => map = null;   // force rebuild on (re)load
    private void OnValidate() => map = null;   // force rebuild after edits

    /// <summary>
    /// Editor helper: add a default <see cref="Entry"/> for every
    /// <see cref="GameSound"/> value not already present. Existing rows (and
    /// their clip assignments) are left untouched. Safe to run repeatedly.
    /// </summary>
    [ContextMenu("Populate Missing Entries")]
    public void PopulateMissingEntries()
    {
        if (entries == null) entries = new List<Entry>();

        var present = new HashSet<GameSound>();
        for (int i = 0; i < entries.Count; i++)
            if (entries[i] != null) present.Add(entries[i].id);

        int added = 0;
        foreach (GameSound id in System.Enum.GetValues(typeof(GameSound)))
        {
            if (present.Contains(id)) continue;
            entries.Add(new Entry { id = id, sound = DefaultFor(id) });
            added++;
        }

        map = null;
        Debug.Log($"[SoundLibrary] Populate complete — added {added} missing entry(ies), " +
                  $"{entries.Count} total. Drop clips into the new rows.");
    }

    /// <summary>
    /// Sensible per-sound defaults so newly populated rows already have good
    /// spam-guard / variation values before clips are assigned.
    /// </summary>
    private static SoundEvent DefaultFor(GameSound id)
    {
        SoundEvent s = new SoundEvent();
        switch (id)
        {
            case GameSound.Gunfire:
            case GameSound.TurretFire:
                s.volume = 0.55f; s.minInterval = 0.05f; s.volumeJitter = 0.1f;
                s.pitchRange = new Vector2(0.92f, 1.08f);
                break;
            case GameSound.Explosion:
                s.volume = 0.9f; s.minInterval = 0.06f; s.pitchRange = new Vector2(0.9f, 1.05f);
                break;
            case GameSound.Impact:
                s.volume = 0.7f; s.minInterval = 0.05f;
                break;
            case GameSound.UnitDamaged:
                s.volume = 0.5f; s.minInterval = 0.2f;   // throttled hard — combat-heavy
                break;
            case GameSound.ConstructionLoop:
                s.volume = 0.5f; s.minInterval = 0f; s.volumeJitter = 0f;
                s.pitchRange = new Vector2(1f, 1f);
                break;
            case GameSound.UIButtonHover:
                s.volume = 0.35f; s.minInterval = 0.03f;
                break;
            default:
                s.volume = 0.85f; s.minInterval = 0.04f;
                break;
        }
        return s;
    }
}
