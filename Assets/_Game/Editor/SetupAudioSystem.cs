using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click wiring for the audio system. Menu: Tools → RTS → Audio → Setup
/// Audio System.
///
/// What it does (all idempotent — safe to run repeatedly):
///   1. Ensures the Audio folder structure exists under Assets/_Game/Audio.
///   2. Creates a <see cref="SoundLibrary"/> asset (if missing) and populates a
///      row for every <see cref="GameSound"/> so you just drop clips in.
///   3. Ensures an "AudioManager" GameObject with an <see cref="AudioManager"/>
///      component exists, and assigns the SoundLibrary to it.
///   4. Adds <see cref="UIAudioHooks"/> to the known Canvases (MainMenuCanvas,
///      HUDCanvas, EscapeMenuCanvas, LobbyCanvas) so every button gets a click
///      sound with no per-button wiring.
///
/// No clips are imported — you drop your own (royalty-free) audio into the SFX /
/// Music / Ambience folders and assign them in the SoundLibrary + AudioManager.
/// The game runs fine with everything still empty.
/// </summary>
public static class SetupAudioSystem
{
    private const string AudioRoot      = "Assets/_Game/Audio";
    private const string LibraryPath    = "Assets/_Game/Audio/GameSoundLibrary.asset";
    private const string ManagerObjName = "AudioManager";

    private static readonly string[] CanvasNames =
    {
        "MainMenuCanvas", "HUDCanvas", "EscapeMenuCanvas", "LobbyCanvas",
    };

    [MenuItem("Tools/RTS/Audio/Setup Audio System")]
    public static void Run()
    {
        Debug.Log("[SetupAudio] ── Setting up the audio system ──");

        EnsureFolders();
        SoundLibrary lib   = EnsureLibrary();
        AudioManager mgr   = EnsureManager(lib);
        int          hooks = EnsureCanvasHooks();

        // Proactively wire whatever clips already exist in Assets (never
        // overwrites a clip you assigned by hand).
        int assigned = AutoAssignClips(lib, mgr, overwrite: false, log: true);

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log($"[SetupAudio] ✓ Done. SoundLibrary '{lib.name}' ({lib.entries.Count} entries), " +
                  $"AudioManager '{mgr.name}', UIAudioHooks on {hooks} canvas(es), " +
                  $"auto-assigned {assigned} sound(s) from existing clips.\n" +
                  "Next: drop your audio clips into Assets/_Game/Audio/{SFX,Music,Ambience}, then " +
                  "re-run this tool (or 'Auto Assign Existing Audio Clips') to wire them by name. " +
                  "Anything unmatched can be assigned by hand in the SoundLibrary / AudioManager.");
    }

    // ------------------------------------------------------------------ //

    private static void EnsureFolders()
    {
        EnsureFolder("Assets/_Game", "Audio");
        foreach (string sub in new[] { "Scripts", "SFX", "Music", "Ambience", "Mixers" })
            EnsureFolder(AudioRoot, sub);
    }

    private static void EnsureFolder(string parent, string child)
    {
        string full = parent + "/" + child;
        if (!AssetDatabase.IsValidFolder(full))
            AssetDatabase.CreateFolder(parent, child);
    }

    private static SoundLibrary EnsureLibrary()
    {
        SoundLibrary lib = AssetDatabase.LoadAssetAtPath<SoundLibrary>(LibraryPath);
        if (lib == null)
        {
            lib = ScriptableObject.CreateInstance<SoundLibrary>();
            AssetDatabase.CreateAsset(lib, LibraryPath);
            Debug.Log($"[SetupAudio] Created SoundLibrary at {LibraryPath}.");
        }

        lib.PopulateMissingEntries();
        EditorUtility.SetDirty(lib);
        AssetDatabase.SaveAssets();
        return lib;
    }

    private static AudioManager EnsureManager(SoundLibrary lib)
    {
        GameObject go = GameObject.Find(ManagerObjName);
        if (go == null)
        {
            go = new GameObject(ManagerObjName);
            Undo.RegisterCreatedObjectUndo(go, "Create AudioManager");
        }

        AudioManager mgr = go.GetComponent<AudioManager>();
        if (mgr == null) mgr = go.AddComponent<AudioManager>();

        if (mgr.library == null)
        {
            mgr.library = lib;
            Debug.Log("[SetupAudio] Assigned SoundLibrary to AudioManager.");
        }

        EditorUtility.SetDirty(go);
        return mgr;
    }

    private static int EnsureCanvasHooks()
    {
        // Include inactive — menu/HUD canvases are often toggled off in-scene.
        Canvas[] canvases = Object.FindObjectsByType<Canvas>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        int count = 0;
        foreach (Canvas c in canvases)
        {
            if (c == null) continue;
            if (System.Array.IndexOf(CanvasNames, c.gameObject.name) < 0) continue;

            if (c.GetComponent<UIAudioHooks>() == null)
            {
                c.gameObject.AddComponent<UIAudioHooks>();
                EditorUtility.SetDirty(c.gameObject);
                Debug.Log($"[SetupAudio] Added UIAudioHooks to '{c.gameObject.name}'.");
            }
            count++;
        }

        if (count == 0)
            Debug.LogWarning("[SetupAudio] No known UI canvases found (MainMenuCanvas / " +
                             "HUDCanvas / EscapeMenuCanvas / LobbyCanvas). Run the menu/HUD " +
                             "setup tools first, then re-run Setup Audio System — or add " +
                             "UIAudioHooks to a Canvas by hand.");
        return count;
    }

    // ================================================================== //
    // Audio-clip inventory + keyword auto-assignment
    //
    // Scope: ONLY Assets/ is searched (never Packages / PackageCache), so we
    // never pull a package sample or AI-voice preview into game audio.
    // ================================================================== //

    [MenuItem("Tools/RTS/Audio/Print Audio Clip Inventory")]
    public static void PrintInventory()
    {
        AudioClip[] clips = FindProjectClips();
        if (clips.Length == 0)
        {
            Debug.LogWarning("[AudioInventory] No AudioClip assets found under Assets/. " +
                             "Drop royalty-free clips into Assets/_Game/Audio/{SFX,Music,Ambience}, " +
                             "then run 'Auto Assign Existing Audio Clips'.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[AudioInventory] {clips.Length} AudioClip asset(s) under Assets/:");
        sb.AppendLine("clip name | path | suggested GameSound");
        foreach (AudioClip c in clips)
        {
            string path = AssetDatabase.GetAssetPath(c);
            List<GameSound> matches = MatchesFor(c.name);
            string suggestion = matches.Count > 0 ? string.Join(", ", matches) : "(no keyword match — assign by hand)";
            string music = MusicSlotFor(c.name);
            if (!string.IsNullOrEmpty(music)) suggestion += (matches.Count > 0 ? ", " : "") + music;
            sb.AppendLine($"  • {c.name} | {path} | {suggestion}");
        }
        Debug.Log(sb.ToString());
    }

    [MenuItem("Tools/RTS/Audio/Auto Assign Existing Audio Clips")]
    public static void AutoAssignMenu()
    {
        SoundLibrary lib = EnsureLibrary();
        AudioManager mgr = Object.FindFirstObjectByType<AudioManager>(FindObjectsInactive.Include);
        int n = AutoAssignClips(lib, mgr, overwrite: false, log: true);
        if (mgr != null) EditorUtility.SetDirty(mgr);
        EditorUtility.SetDirty(lib);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"[AudioAutoAssign] Done — filled {n} empty sound/music slot(s). " +
                  "Existing assignments were left untouched.");
    }

    [MenuItem("Tools/RTS/Audio/Auto Assign Existing Audio Clips (Overwrite All)")]
    public static void AutoAssignOverwriteMenu()
    {
        if (!EditorUtility.DisplayDialog("Overwrite all audio assignments?",
            "This re-assigns clips by filename keyword and REPLACES clips you may " +
            "have set by hand. Continue?", "Overwrite", "Cancel"))
            return;

        SoundLibrary lib = EnsureLibrary();
        AudioManager mgr = Object.FindFirstObjectByType<AudioManager>(FindObjectsInactive.Include);
        int n = AutoAssignClips(lib, mgr, overwrite: true, log: true);
        if (mgr != null) EditorUtility.SetDirty(mgr);
        EditorUtility.SetDirty(lib);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"[AudioAutoAssign] Done (overwrite) — assigned {n} slot(s).");
    }

    [MenuItem("Tools/RTS/Audio/Apply Recommended World-Sound Distances")]
    public static void ApplyRecommendedDistancesMenu()
    {
        AudioManager mgr = Object.FindFirstObjectByType<AudioManager>(FindObjectsInactive.Include);
        if (mgr == null)
        {
            Debug.LogWarning("[SetupAudio] No AudioManager in the scene. Run " +
                             "Tools → RTS → Audio → Setup Audio System first.");
            return;
        }

        mgr.ApplyRecommendedWorldDistances();
        EditorUtility.SetDirty(mgr);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log("[SetupAudio] Applied recommended world-sound distances — " +
                  $"combat={mgr.combat3DMaxDistance}, projectile={mgr.projectile3DMaxDistance}, " +
                  $"explosion={mgr.explosion3DMaxDistance}, impact={mgr.impact3DMaxDistance}, " +
                  $"construction={mgr.construction3DMaxDistance}, min={mgr.default3DMinDistance}. " +
                  "Save the scene to keep them.");
    }

    [MenuItem("Tools/RTS/Audio/Populate Missing Library Entries")]
    public static void PopulateMissingMenu()
    {
        SoundLibrary lib = EnsureLibrary();
        lib.PopulateMissingEntries();
        EditorUtility.SetDirty(lib);
        AssetDatabase.SaveAssets();
    }

    /// <summary>
    /// Assign Assets-folder AudioClips to SoundLibrary rows + AudioManager music
    /// fields by matching filename keywords. When <paramref name="overwrite"/>
    /// is false, only empty slots are filled (manual assignments preserved).
    /// Multiple clips matching one sound are all added (random variation).
    /// Returns the number of slots filled.
    /// </summary>
    private static int AutoAssignClips(SoundLibrary lib, AudioManager mgr, bool overwrite, bool log)
    {
        if (lib == null) return 0;
        lib.PopulateMissingEntries();   // guarantee a row per GameSound

        AudioClip[] clips = FindProjectClips();
        if (clips.Length == 0)
        {
            if (log)
                Debug.LogWarning("[AudioAutoAssign] No AudioClip assets under Assets/ — nothing to " +
                                 "assign. Add clips to Assets/_Game/Audio/{SFX,Music,Ambience} and re-run.");
            return 0;
        }

        // Bucket clips per GameSound by keyword.
        var bySound = new Dictionary<GameSound, List<AudioClip>>();
        foreach (AudioClip c in clips)
        {
            foreach (GameSound id in MatchesFor(c.name))
            {
                if (!bySound.TryGetValue(id, out List<AudioClip> list))
                    bySound[id] = list = new List<AudioClip>();
                if (!list.Contains(c)) list.Add(c);
            }
        }

        int filled = 0;
        var report = new StringBuilder();

        foreach (var kv in bySound)
        {
            SoundLibrary.Entry entry = FindEntry(lib, kv.Key);
            if (entry == null || entry.sound == null) continue;

            bool hasClips = entry.sound.clips != null && entry.sound.clips.Length > 0;
            if (hasClips && !overwrite) continue;     // respect manual assignment

            entry.sound.clips = kv.Value.ToArray();
            filled++;
            report.AppendLine($"  • {kv.Key} ← {Names(kv.Value)}");
        }

        // Music + ambience on the AudioManager.
        if (mgr != null)
        {
            AudioClip menu     = FindMusicClip(clips, "menu");
            AudioClip gameplay = FindMusicClip(clips, "gameplay");
            AudioClip ambience = FindMusicClip(clips, "ambience");

            if (menu != null && (overwrite || mgr.menuMusic == null))
            { mgr.menuMusic = menu; filled++; report.AppendLine($"  • MenuMusic ← {menu.name}"); }
            if (gameplay != null && (overwrite || mgr.gameplayMusic == null))
            { mgr.gameplayMusic = gameplay; filled++; report.AppendLine($"  • GameplayMusic ← {gameplay.name}"); }
            if (ambience != null && (overwrite || mgr.battlefieldAmbience == null))
            { mgr.battlefieldAmbience = ambience; filled++; report.AppendLine($"  • BattlefieldAmbience ← {ambience.name}"); }
        }

        EditorUtility.SetDirty(lib);
        if (mgr != null) EditorUtility.SetDirty(mgr);

        if (log)
        {
            if (filled > 0)
                Debug.Log($"[AudioAutoAssign] Filled {filled} slot(s):\n{report}");
            else
                Debug.Log("[AudioAutoAssign] No new matches (clips found but no filenames matched " +
                          "the keyword lists, or all matched slots are already assigned).");
        }
        return filled;
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static AudioClip[] FindProjectClips()
    {
        string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets" });
        var list = new List<AudioClip>(guids.Length);
        foreach (string g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            AudioClip c = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (c != null) list.Add(c);
        }
        return list.ToArray();
    }

    private static SoundLibrary.Entry FindEntry(SoundLibrary lib, GameSound id)
    {
        for (int i = 0; i < lib.entries.Count; i++)
            if (lib.entries[i] != null && lib.entries[i].id == id) return lib.entries[i];
        return null;
    }

    private static string Names(List<AudioClip> clips)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < clips.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(clips[i].name);
        }
        return sb.ToString();
    }

    /// <summary>Returns every GameSound whose keyword list matches this filename.</summary>
    private static List<GameSound> MatchesFor(string clipName)
    {
        string n = clipName.ToLowerInvariant();
        var result = new List<GameSound>();
        foreach (var kv in Keywords)
            if (ContainsAny(n, kv.Value)) result.Add(kv.Key);
        return result;
    }

    private static string MusicSlotFor(string clipName)
    {
        string n = clipName.ToLowerInvariant();
        if (ContainsAny(n, MenuMusicKeys))      return "MenuMusic";
        if (ContainsAny(n, GameplayMusicKeys))  return "GameplayMusic";
        if (ContainsAny(n, AmbienceKeys))       return "BattlefieldAmbience";
        return null;
    }

    private static AudioClip FindMusicClip(AudioClip[] clips, string slot)
    {
        string[] keys = slot == "menu" ? MenuMusicKeys
                      : slot == "gameplay" ? GameplayMusicKeys
                      : AmbienceKeys;
        foreach (AudioClip c in clips)
            if (ContainsAny(c.name.ToLowerInvariant(), keys)) return c;
        return null;
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        for (int i = 0; i < needles.Length; i++)
            if (haystack.Contains(needles[i])) return true;
        return false;
    }

    // ------------------------------------------------------------------ //
    // Keyword tables — lowercase substrings matched against clip filenames.
    // Overlaps are intentional (e.g. an "explosion" clip can seed Explosion,
    // UnitDeath and BuildingDestroyed for instant coverage).
    // ------------------------------------------------------------------ //

    private static readonly string[] MenuMusicKeys     = { "menu_music", "menumusic", "menu_theme", "maintheme", "main_theme", "title_music", "titletheme" };
    private static readonly string[] GameplayMusicKeys = { "gameplay_music", "gameplaymusic", "battle_music", "combat_music", "ingame_music", "game_theme", "battletheme" };
    private static readonly string[] AmbienceKeys      = { "ambience", "ambient", "amb_", "_amb", "wind", "atmosphere", "battlefield_amb", "drone_loop" };

    private static readonly Dictionary<GameSound, string[]> Keywords = new Dictionary<GameSound, string[]>
    {
        { GameSound.UIButtonClick,        new[] { "click", "button", "tap", "ui_select", "menu_click", "press" } },
        { GameSound.UIButtonHover,        new[] { "hover", "rollover", "blip", "ui_hover", "mouseover" } },
        { GameSound.UIOpenPanel,          new[] { "open", "panel_open", "slide_in", "ui_open", "whoosh_ui" } },
        { GameSound.UIClosePanel,         new[] { "close", "panel_close", "slide_out", "ui_close" } },
        { GameSound.UIError,              new[] { "error", "denied", "invalid", "negative", "wrong", "buzz", "nope" } },
        { GameSound.UIConfirm,            new[] { "confirm", "accept", "positive", "success_ui", "ok_" } },

        { GameSound.UnitSelect,           new[] { "select", "unit_select", "ack", "acknowledge", "yessir", "selected", "reporting" } },
        { GameSound.UnitMoveOrder,        new[] { "move", "moving", "roger", "order_move", "movecommand", "onmyway", "affirm" } },
        { GameSound.UnitAttackOrder,      new[] { "attack", "engage", "attack_order", "fire_command", "target", "weaponsfree" } },
        { GameSound.UnitGatherOrder,      new[] { "gather", "harvest", "mine_order", "collect_order" } },
        { GameSound.UnitDamaged,          new[] { "hurt", "pain", "damage", "hit_unit", "ouch", "injured" } },
        { GameSound.UnitDeath,            new[] { "death", "die", "unit_death", "killed", "destroyed_unit", "explosion_small", "small_explosion" } },

        { GameSound.Gunfire,              new[] { "rifle", "gun", "gunfire", "shot", "pistol", "shoot", "bullet", "smallarms" } },
        { GameSound.TurretFire,           new[] { "mg", "machinegun", "machine_gun", "turret", "minigun", "heavygun", "heavy_gun", "rapidfire" } },
        { GameSound.RocketLaunch,         new[] { "rocket", "rpg", "launch_rocket", "rocketlaunch" } },
        { GameSound.MissileLaunch,        new[] { "missile", "missilelaunch", "launch_missile", "whoosh_missile" } },
        { GameSound.ArtilleryLaunch,      new[] { "artillery", "cannon", "mortar", "howitzer", "tank_fire", "launch_heavy" } },
        { GameSound.Explosion,            new[] { "explosion", "explode", "boom", "blast", "detonate" } },
        { GameSound.Impact,               new[] { "impact", "hit", "thud", "splat", "crash", "ricochet" } },

        { GameSound.BuildingPlace,        new[] { "build_start", "place", "construct_start", "foundation", "placing", "deploy" } },
        { GameSound.ConstructionLoop,     new[] { "build_loop", "construction_loop", "construct_loop", "building_loop", "hammer", "welding", "mechanical_loop" } },
        { GameSound.ConstructionComplete, new[] { "complete", "build_done", "ready", "construction_complete", "finished", "success" } },
        { GameSound.BuildingDestroyed,    new[] { "collapse", "building_destroy", "large_explosion", "explosion_large", "rubble", "destroy_building" } },

        { GameSound.ResourceGather,       new[] { "gather", "collect", "pickup", "coin", "resource", "ore", "harvest", "cash" } },
    };
}
