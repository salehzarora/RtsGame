using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Central, scalable audio service for the RTS. One instance lives in the scene
/// (created by Tools → RTS → Audio → Setup Audio System) and survives scene
/// changes via a duplicate-guarded <see cref="DontDestroyOnLoad"/>, so music
/// never doubles up and no second manager is ever spawned.
///
/// Responsibilities:
///   • Separate Master / SFX / Music volume buses (persisted to PlayerPrefs).
///   • 2D one-shot SFX for UI (<see cref="Sfx"/>) and positional 3D SFX for
///     world events (<see cref="SfxAt"/>), both drawn from a reusable pool of
///     <see cref="AudioSource"/>s — no per-shot GameObject churn.
///   • Random clip / pitch / volume variation + a per-event spam guard, all
///     defined on the <see cref="SoundEvent"/> in the <see cref="SoundLibrary"/>.
///   • Menu vs gameplay music with crossfade + a battlefield ambience bed,
///     driven automatically by <see cref="GameStateManager"/> events.
///
/// Multiplayer: this manager is COSMETIC and 100% local. Nothing here sends a
/// network event or touches gameplay state. UI / command-confirm sounds are
/// triggered only on the client that issued them (the call sites live in local
/// input code); world combat sounds are triggered from the cosmetic
/// flash/projectile code that already runs on every client, so both players
/// hear nearby battle events without any extra RPCs.
///
/// Everything is null-safe. With no <see cref="library"/> or no clips assigned,
/// calls are silently ignored and the game runs normally.
/// </summary>
[DisallowMultipleComponent]
public class AudioManager : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Singleton
    // ------------------------------------------------------------------ //

    public static AudioManager Instance { get; private set; }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Sound Library")]
    [Tooltip("The asset mapping each GameSound to its clips. Create via " +
             "Assets → Create → RTS → Audio → Sound Library, or let the " +
             "Setup Audio System tool create + assign one.")]
    public SoundLibrary library;

    [Header("Volume buses (0..1) — persisted to PlayerPrefs")]
    [Range(0f, 1f)] public float masterVolume = 1f;
    [Range(0f, 1f)] public float sfxVolume    = 1f;
    [Range(0f, 1f)] public float musicVolume  = 0.6f;

    [Header("SFX pool")]
    [Tooltip("Number of pooled AudioSources for one-shot SFX. ~16 comfortably " +
             "covers a busy battle; raise if you hear shots getting cut off.")]
    public int sfxPoolSize = 16;

    // Recommended RTS distances (single source of truth for both the field
    // defaults below and the editor "Apply Recommended World-Sound Distances"
    // tool, so they can never drift apart).
    public const float DefaultMin            = 12f;
    public const float DefaultWorldMax       = 80f;
    public const float DefaultCombatMax      = 80f;   // gunfire / turret
    public const float DefaultProjectileMax  = 105f;  // rocket / missile / artillery
    public const float DefaultExplosionMax   = 150f;  // explosion / building destruction
    public const float DefaultImpactMax      = 70f;   // impact / unit death
    public const float DefaultConstructionMax = 55f;  // placement / loop / complete

    [Header("3D world-sound distance falloff (RTS-tuned)")]
    [Tooltip("Distance within which a world sound is near full volume. World " +
             "sounds use a Custom rolloff that reaches ZERO at their category " +
             "max distance, so far-away combat goes fully silent (unlike the " +
             "default Logarithmic rolloff, which never quite hits zero).")]
    public float default3DMinDistance = DefaultMin;

    [Tooltip("Fallback max distance for any world sound that has no specific " +
             "category. Beyond this the sound is silent.")]
    public float default3DMaxDistance = DefaultWorldMax;

    [Tooltip("Gunfire / turret fire — silent past this many world units.")]
    public float combat3DMaxDistance = DefaultCombatMax;

    [Tooltip("Rockets / missiles / artillery launch — silent past this distance.")]
    public float projectile3DMaxDistance = DefaultProjectileMax;

    [Tooltip("Explosions / building destruction — heard from farther, silent past this distance.")]
    public float explosion3DMaxDistance = DefaultExplosionMax;

    [Tooltip("Small impacts / unit death — silent past this distance.")]
    public float impact3DMaxDistance = DefaultImpactMax;

    [Tooltip("Construction (placement / build loop / complete) — silent past this distance.")]
    public float construction3DMaxDistance = DefaultConstructionMax;

    [Tooltip("If true, a world one-shot triggered farther than its max distance " +
             "from the AudioListener is skipped entirely (no voice spent, " +
             "guaranteed silent). Recommended ON.")]
    public bool skipDistantWorldSounds = true;

    [Tooltip("Log the chosen 3D max distance each time a world sound plays. " +
             "Debug only — spammy, leave OFF in normal play.")]
    public bool logWorldSoundDistance = false;

    [Header("Music (long looping tracks — assign clips here)")]
    [Tooltip("Played on the main menu / between matches.")]
    public AudioClip menuMusic;

    [Tooltip("Played once a match starts (GameStateManager.OnGameStarted).")]
    public AudioClip gameplayMusic;

    [Range(0f, 1f)]
    [Tooltip("Per-track trim applied on top of the Music bus.")]
    public float musicTrackVolume = 1f;

    [Tooltip("Seconds to crossfade between two music tracks.")]
    public float musicCrossfadeSeconds = 1.5f;

    [Header("Ambience (looping battlefield bed during a match)")]
    public AudioClip battlefieldAmbience;

    [Range(0f, 1f)]
    [Tooltip("Ambience level. Scaled by Master × SFX so the slider that lowers " +
             "effects also lowers the bed.")]
    public float ambienceVolume = 0.5f;

    [Header("Debug")]
    [Tooltip("Log every distinct sound the first time it can't find a clip. " +
             "Off by default to keep the console clean.")]
    public bool logMissingClips = false;

    // ------------------------------------------------------------------ //
    // PlayerPrefs keys
    // ------------------------------------------------------------------ //

    private const string PrefMaster = "audio.master";
    private const string PrefSfx    = "audio.sfx";
    private const string PrefMusic  = "audio.music";

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private AudioSource[] sfxPool;
    private int           sfxCursor;

    private AudioSource musicA;
    private AudioSource musicB;
    private bool        musicAActive = true;     // which of A/B is the current track
    private Coroutine   crossfadeRoutine;

    private AudioSource ambienceSource;

    // Spam-guard: last unscaled time each SoundEvent was played.
    private readonly Dictionary<SoundEvent, float> lastPlayed = new Dictionary<SoundEvent, float>();

    // One-time "missing clip" warning gate per id.
    private readonly HashSet<GameSound> warnedMissing = new HashSet<GameSound>();

    private bool gameStateSubscribed;

    // Shared Custom rolloff curve for ALL world sounds. X is normalized distance
    // (0 = at the source, 1 = maxDistance); Y is volume. It reaches 0 at the max
    // distance so far-away sounds are truly silent — the whole point of this pass.
    // (Unity's default Logarithmic rolloff only ever approaches zero.)
    private static readonly AnimationCurve FalloffCurve = new AnimationCurve(
        new Keyframe(0f,    1f),
        new Keyframe(0.25f, 0.75f),
        new Keyframe(0.5f,  0.35f),
        new Keyframe(0.75f, 0.08f),
        new Keyframe(1f,    0f));

    // Cached AudioListener (usually on the gameplay Main Camera). Re-found if null.
    private AudioListener cachedListener;

    // ------------------------------------------------------------------ //
    // Convenience scalars
    // ------------------------------------------------------------------ //

    /// <summary>Master × SFX. Multiply a SoundEvent's volume by this.</summary>
    public float SfxScalar => Mathf.Clamp01(masterVolume) * Mathf.Clamp01(sfxVolume);

    /// <summary>Master × Music × per-track trim.</summary>
    public float MusicScalar => Mathf.Clamp01(masterVolume) * Mathf.Clamp01(musicVolume)
                                * Mathf.Clamp01(musicTrackVolume);

    /// <summary>Ambience rides the Master × SFX buses.</summary>
    public float AmbienceScalar => SfxScalar * Mathf.Clamp01(ambienceVolume);

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // A manager already survived from before — keep it, drop this one.
            // This is what prevents duplicate music when re-entering the scene.
            Debug.Log("[Audio] Duplicate AudioManager destroyed — keeping the existing one.");
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        LoadVolumes();
        BuildSfxPool();
        BuildMusicSources();
        BuildAmbienceSource();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        UnsubscribeGameState();
    }

    private void Start()
    {
        // The AudioListener is what 3D attenuation (and our distance cull) is
        // measured against — in an RTS it should sit on the gameplay camera.
        cachedListener = FindAnyObjectByType<AudioListener>();
        if (cachedListener == null)
            Debug.LogWarning("[Audio] No AudioListener in the scene — positional SFX will be " +
                             "silent. Add one to your gameplay Main Camera.");
        else
            Debug.Log($"[Audio] AudioListener found on '{cachedListener.gameObject.name}'. " +
                      "World-sound distance is measured from here — make sure this is the " +
                      "gameplay camera, not a minimap/secondary camera.");

        SubscribeGameState();

        // Open on whatever state we're in: menu music before Play, gameplay
        // music + ambience if a match is already running.
        bool playing = GameStateManager.Instance != null && GameStateManager.Instance.IsGameStarted;
        if (playing) PlayGameplayMusic();
        else         PlayMenuMusic();
    }

    // ------------------------------------------------------------------ //
    // Build runtime sources
    // ------------------------------------------------------------------ //

    private void BuildSfxPool()
    {
        int size = Mathf.Max(4, sfxPoolSize);
        sfxPool = new AudioSource[size];
        for (int i = 0; i < size; i++)
        {
            var go = new GameObject($"SFX_{i}");
            go.transform.SetParent(transform, false);
            AudioSource src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            // Custom rolloff that hits zero at maxDistance. The curve is set once
            // here; per-play we only adjust maxDistance (the curve is normalized
            // to it), so there's no per-shot allocation.
            src.rolloffMode = AudioRolloffMode.Custom;
            src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, FalloffCurve);
            src.dopplerLevel = 0f;       // no pitch shift from camera motion
            sfxPool[i] = src;
        }
    }

    private void BuildMusicSources()
    {
        musicA = NewLoopSource("Music_A");
        musicB = NewLoopSource("Music_B");
    }

    private void BuildAmbienceSource()
    {
        ambienceSource = NewLoopSource("Ambience");
    }

    private AudioSource NewLoopSource(string label)
    {
        var go = new GameObject(label);
        go.transform.SetParent(transform, false);
        AudioSource src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.loop = true;
        src.spatialBlend = 0f;           // 2D — fills the soundstage
        src.volume = 0f;
        return src;
    }

    // ------------------------------------------------------------------ //
    // Public SFX API
    // ------------------------------------------------------------------ //

    /// <summary>Play a 2D (non-positional) one-shot — for UI and local feedback.</summary>
    public void Play(GameSound id) => PlayInternal(id, spatial: false, position: Vector3.zero);

    /// <summary>Play a positional 3D one-shot at <paramref name="position"/> — for world events.</summary>
    public void PlayAt(GameSound id, Vector3 position) => PlayInternal(id, spatial: true, position: position);

    /// <summary>
    /// Look up the raw <see cref="SoundEvent"/> for <paramref name="id"/> (or
    /// null). Used by systems that manage their own looping AudioSource (e.g.
    /// the construction-site loop) and just need the clip + base volume.
    /// </summary>
    public SoundEvent GetEvent(GameSound id) => library != null ? library.Get(id) : null;

    // ---- Static null-safe facade (call these from gameplay code) ------ //

    /// <summary>Static, null-safe 2D play. No-op if no AudioManager exists yet.</summary>
    public static void Sfx(GameSound id)
    {
        if (Instance != null) Instance.Play(id);
    }

    /// <summary>Static, null-safe 3D play. No-op if no AudioManager exists yet.</summary>
    public static void SfxAt(GameSound id, Vector3 position)
    {
        if (Instance != null) Instance.PlayAt(id, position);
    }

    // ------------------------------------------------------------------ //
    // Core playback
    // ------------------------------------------------------------------ //

    private void PlayInternal(GameSound id, bool spatial, Vector3 position)
    {
        if (library == null) return;

        SoundEvent ev = library.Get(id);
        if (ev == null || !ev.HasClip)
        {
            if (logMissingClips && warnedMissing.Add(id))
                Debug.Log($"[Audio] No clip assigned for '{id}' — skipping (assign one in the Sound Library).");
            return;
        }

        // 3D distance cull — for world sounds, if the trigger point is farther
        // than this sound's max distance from the listener, don't play at all.
        // Guarantees far-away combat is silent AND saves a voice + the spam-guard
        // slot for sounds the player could actually hear.
        float maxDist = spatial ? ResolveMaxDistance(id, ev) : 0f;
        if (spatial && skipDistantWorldSounds && IsBeyond(position, maxDist))
        {
            if (logWorldSoundDistance)
                Debug.Log($"[Audio] '{id}' skipped — beyond {maxDist:F0}u from the listener.");
            return;
        }

        // Spam guard — collapse a burst of the same event into one play.
        float now = Time.unscaledTime;
        if (ev.minInterval > 0f
            && lastPlayed.TryGetValue(ev, out float last)
            && now - last < ev.minInterval)
            return;
        lastPlayed[ev] = now;

        AudioClip clip = ev.PickClip();
        if (clip == null) return;

        float vol = ev.PickVolume() * SfxScalar;
        if (vol <= 0.0001f) return;       // muted — don't burn a voice

        AudioSource src = GetFreeSource();
        if (src == null) return;

        src.Stop();
        src.clip         = clip;
        src.pitch        = ev.PickPitch();
        src.volume       = vol;
        src.spatialBlend = spatial ? 1f : 0f;
        if (spatial)
        {
            // Custom rolloff curve is already set on the pooled source; we only
            // tune the distances. The curve is normalized to maxDistance, so this
            // is all that's needed to make the sound vanish by maxDist.
            src.transform.position = position;
            src.rolloffMode = AudioRolloffMode.Custom;
            src.minDistance = ResolveMinDistance(ev);
            src.maxDistance = maxDist;
            if (logWorldSoundDistance)
                Debug.Log($"[Audio] '{id}' 3D play — min={src.minDistance:F0}u max={maxDist:F0}u.");
        }
        src.Play();
    }

    // ------------------------------------------------------------------ //
    // 3D distance resolution + listener helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Overwrite all world-sound distances with the recommended RTS values
    /// (the <c>Default*</c> constants). Called by the editor tool so an
    /// AudioManager that already had OLDER values serialized onto it picks up
    /// the new, longer ranges — a plain code-default bump can't do that once a
    /// value is baked into the scene/prefab.
    /// </summary>
    public void ApplyRecommendedWorldDistances()
    {
        default3DMinDistance      = DefaultMin;
        default3DMaxDistance      = DefaultWorldMax;
        combat3DMaxDistance       = DefaultCombatMax;
        projectile3DMaxDistance   = DefaultProjectileMax;
        explosion3DMaxDistance    = DefaultExplosionMax;
        impact3DMaxDistance       = DefaultImpactMax;
        construction3DMaxDistance = DefaultConstructionMax;
    }

    /// <summary>Per-sound override → else per-category max distance.</summary>
    private float ResolveMaxDistance(GameSound id, SoundEvent ev)
    {
        if (ev != null && ev.useCustom3DDistance) return Mathf.Max(1f, ev.customMaxDistance);
        return CategoryMaxDistance(id);
    }

    private float ResolveMinDistance(SoundEvent ev)
    {
        if (ev != null && ev.useCustom3DDistance) return Mathf.Max(0.1f, ev.customMinDistance);
        return Mathf.Max(0.1f, default3DMinDistance);
    }

    /// <summary>
    /// Max audible distance for a world sound by category. Public so other
    /// self-managed spatial sources (e.g. the construction loop) can match.
    /// </summary>
    public float CategoryMaxDistance(GameSound id)
    {
        switch (id)
        {
            case GameSound.Gunfire:
            case GameSound.TurretFire:
                return combat3DMaxDistance;

            case GameSound.RocketLaunch:
            case GameSound.MissileLaunch:
            case GameSound.ArtilleryLaunch:
                return projectile3DMaxDistance;

            case GameSound.Explosion:
            case GameSound.BuildingDestroyed:
                return explosion3DMaxDistance;

            case GameSound.Impact:
            case GameSound.UnitDamaged:
            case GameSound.UnitDeath:
                return impact3DMaxDistance;

            case GameSound.BuildingPlace:
            case GameSound.ConstructionLoop:
            case GameSound.ConstructionComplete:
            case GameSound.ResourceGather:
                return construction3DMaxDistance;

            default:
                return default3DMaxDistance;
        }
    }

    /// <summary>
    /// Configure a caller-owned looping AudioSource (e.g. the construction-site
    /// loop) with the same RTS falloff as one-shots, so a distant loop also
    /// fades to silence at its category max distance.
    /// </summary>
    public void ConfigureSpatialLoop(AudioSource src, GameSound id)
    {
        if (src == null) return;
        src.spatialBlend = 1f;
        src.rolloffMode  = AudioRolloffMode.Custom;
        src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, FalloffCurve);
        src.minDistance  = Mathf.Max(0.1f, default3DMinDistance);
        src.maxDistance  = CategoryMaxDistance(id);
        src.dopplerLevel = 0f;
    }

    /// <summary>True when <paramref name="pos"/> is farther than <paramref name="maxDist"/>
    /// from the AudioListener. False (don't cull) if no listener can be resolved.</summary>
    private bool IsBeyond(Vector3 pos, float maxDist)
    {
        if (!TryGetListenerPosition(out Vector3 lp)) return false;
        return (pos - lp).sqrMagnitude > maxDist * maxDist;
    }

    private bool TryGetListenerPosition(out Vector3 pos)
    {
        if (cachedListener == null)
            cachedListener = FindAnyObjectByType<AudioListener>();

        if (cachedListener != null) { pos = cachedListener.transform.position; return true; }
        if (Camera.main != null)    { pos = Camera.main.transform.position;    return true; }

        pos = Vector3.zero;
        return false;
    }

    /// <summary>
    /// Returns the first idle pooled source, or the oldest one (round-robin)
    /// when every source is busy — interrupting the longest-playing voice keeps
    /// recent, more relevant sounds audible under load.
    /// </summary>
    private AudioSource GetFreeSource()
    {
        if (sfxPool == null || sfxPool.Length == 0) return null;

        for (int i = 0; i < sfxPool.Length; i++)
        {
            AudioSource s = sfxPool[i];
            if (s != null && !s.isPlaying) return s;
        }

        // All busy — steal via round-robin.
        AudioSource chosen = sfxPool[sfxCursor];
        sfxCursor = (sfxCursor + 1) % sfxPool.Length;
        return chosen;
    }

    // ------------------------------------------------------------------ //
    // Volume control (call from a settings UI; persisted)
    // ------------------------------------------------------------------ //

    public void SetMasterVolume(float v) { masterVolume = Mathf.Clamp01(v); ApplyBusVolumes(); SaveVolumes(); }
    public void SetSfxVolume(float v)    { sfxVolume    = Mathf.Clamp01(v); ApplyBusVolumes(); SaveVolumes(); }
    public void SetMusicVolume(float v)  { musicVolume  = Mathf.Clamp01(v); ApplyBusVolumes(); SaveVolumes(); }

    private void LoadVolumes()
    {
        masterVolume = PlayerPrefs.GetFloat(PrefMaster, masterVolume);
        sfxVolume    = PlayerPrefs.GetFloat(PrefSfx,    sfxVolume);
        musicVolume  = PlayerPrefs.GetFloat(PrefMusic,  musicVolume);
    }

    private void SaveVolumes()
    {
        PlayerPrefs.SetFloat(PrefMaster, masterVolume);
        PlayerPrefs.SetFloat(PrefSfx,    sfxVolume);
        PlayerPrefs.SetFloat(PrefMusic,  musicVolume);
        PlayerPrefs.Save();
    }

    /// <summary>Re-apply music + ambience levels live (one-shot SFX read the scalar per play).</summary>
    private void ApplyBusVolumes()
    {
        // Only touch the source that isn't mid-crossfade; the crossfade routine
        // reads MusicScalar every frame so it picks up changes on its own.
        if (crossfadeRoutine == null)
        {
            AudioSource active = musicAActive ? musicA : musicB;
            if (active != null && active.clip != null) active.volume = MusicScalar;
        }
        if (ambienceSource != null && ambienceSource.isPlaying)
            ambienceSource.volume = AmbienceScalar;
    }

    // ------------------------------------------------------------------ //
    // Music
    // ------------------------------------------------------------------ //

    /// <summary>Crossfade to the menu track and stop the ambience bed.</summary>
    public void PlayMenuMusic()
    {
        CrossfadeTo(menuMusic);
        StopAmbience();
    }

    /// <summary>Crossfade to the gameplay track and start the ambience bed.</summary>
    public void PlayGameplayMusic()
    {
        CrossfadeTo(gameplayMusic);
        StartAmbience();
    }

    /// <summary>
    /// Crossfade the music bus to <paramref name="clip"/> (null = fade to
    /// silence). No-op when the requested clip is already the active track —
    /// this is what stops returning to the menu / restarting a match from
    /// retriggering or layering the same track.
    /// </summary>
    public void CrossfadeTo(AudioClip clip)
    {
        AudioSource active = musicAActive ? musicA : musicB;
        if (active != null && active.clip == clip && (clip == null || active.isPlaying))
            return;     // already playing this track — don't restart/duplicate

        if (crossfadeRoutine != null) StopCoroutine(crossfadeRoutine);
        crossfadeRoutine = StartCoroutine(CrossfadeRoutine(clip));
    }

    private IEnumerator CrossfadeRoutine(AudioClip clip)
    {
        AudioSource from = musicAActive ? musicA : musicB;
        AudioSource to   = musicAActive ? musicB : musicA;

        if (to != null)
        {
            to.clip   = clip;
            to.volume = 0f;
            if (clip != null) to.Play();
        }

        float dur = Mathf.Max(0.01f, musicCrossfadeSeconds);
        float t = 0f;
        float fromStart = from != null ? from.volume : 0f;

        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / dur);
            float target = MusicScalar;     // live — respects slider changes mid-fade

            if (to != null && clip != null) to.volume = target * k;
            if (from != null)               from.volume = fromStart * (1f - k);
            yield return null;
        }

        if (from != null) { from.Stop(); from.volume = 0f; }
        if (to != null && clip != null) to.volume = MusicScalar;

        musicAActive = !musicAActive;
        crossfadeRoutine = null;
    }

    // ------------------------------------------------------------------ //
    // Ambience
    // ------------------------------------------------------------------ //

    private void StartAmbience()
    {
        if (ambienceSource == null || battlefieldAmbience == null) return;

        // Already running this bed → just refresh volume (no restart).
        if (ambienceSource.isPlaying && ambienceSource.clip == battlefieldAmbience)
        {
            ambienceSource.volume = AmbienceScalar;
            return;
        }
        ambienceSource.clip   = battlefieldAmbience;
        ambienceSource.volume = AmbienceScalar;
        ambienceSource.Play();
    }

    private void StopAmbience()
    {
        if (ambienceSource != null && ambienceSource.isPlaying)
            ambienceSource.Stop();
    }

    // ------------------------------------------------------------------ //
    // Game-state-driven music
    // ------------------------------------------------------------------ //

    private void SubscribeGameState()
    {
        if (gameStateSubscribed || GameStateManager.Instance == null) return;
        GameStateManager.Instance.OnGameStarted += HandleGameStarted;
        GameStateManager.Instance.OnGameReset   += HandleGameReset;
        gameStateSubscribed = true;
    }

    private void UnsubscribeGameState()
    {
        if (!gameStateSubscribed || GameStateManager.Instance == null) return;
        GameStateManager.Instance.OnGameStarted -= HandleGameStarted;
        GameStateManager.Instance.OnGameReset   -= HandleGameReset;
        gameStateSubscribed = false;
    }

    private void HandleGameStarted() => PlayGameplayMusic();
    private void HandleGameReset()   => PlayMenuMusic();
}
