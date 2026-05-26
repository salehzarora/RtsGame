using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plain data record for one playable map. Used by the lobby UI's map
/// dropdown and by <see cref="NetworkManagerRTS.CreateRoom"/> as the value
/// of the room's <c>mapId</c> custom property so every client agrees on
/// which map they're playing.
///
/// Phase 6 has exactly one map registered (<c>DefaultMap</c>) — the existing
/// 1v1 scene. Add new entries to <see cref="KnownMaps"/> as more maps come
/// online; nothing else needs to change.
/// </summary>
[System.Serializable]
public class MapDefinition
{
    /// <summary>Stable string id sent in Photon room properties.</summary>
    public string mapId;

    /// <summary>Human-readable label shown in the lobby UI.</summary>
    public string displayName;

    /// <summary>Maximum players this map supports.</summary>
    public int maxPlayers = 2;

    /// <summary>
    /// Optional scene name for a future multi-scene flow. Empty in Phase 6
    /// because every map uses the same scene with toggleable map roots.
    /// </summary>
    public string sceneName = "";

    /// <summary>Short description / blurb. Optional, displayed in lobby UI.</summary>
    public string description = "";

    public MapDefinition(string mapId, string displayName, int maxPlayers = 2,
                         string sceneName = "", string description = "")
    {
        this.mapId       = mapId;
        this.displayName = displayName;
        this.maxPlayers  = maxPlayers;
        this.sceneName   = sceneName;
        this.description = description;
    }
}

/// <summary>
/// Static directory of every playable map. The lobby reads this list; the
/// match coordinator validates that an incoming room's <c>mapId</c>
/// resolves here before letting players start.
/// </summary>
public static class MapRegistry
{
    public const string DefaultMapId = "DefaultMap";

    /// <summary>
    /// Canonical map list. Order is the display order in the lobby dropdown.
    /// </summary>
    public static readonly List<MapDefinition> KnownMaps = new List<MapDefinition>
    {
        new MapDefinition(
            mapId:       DefaultMapId,
            displayName: "Default RTS Map",
            maxPlayers:  2,
            description: "1v1 desert test arena. Each player starts on opposite corners."),
    };

    /// <summary>Lookup by id. Returns null when nothing matches.</summary>
    public static MapDefinition GetById(string mapId)
    {
        if (string.IsNullOrEmpty(mapId)) return null;
        for (int i = 0; i < KnownMaps.Count; i++)
            if (KnownMaps[i] != null && KnownMaps[i].mapId == mapId)
                return KnownMaps[i];
        return null;
    }

    /// <summary>Display name (falls back to the id) — for log lines and UI rows.</summary>
    public static string DisplayNameOrId(string mapId)
    {
        MapDefinition m = GetById(mapId);
        return m != null ? m.displayName : (mapId ?? "<unknown>");
    }
}
