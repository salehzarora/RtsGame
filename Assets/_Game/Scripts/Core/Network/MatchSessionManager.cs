using UnityEngine;

#if PHOTON_UNITY_NETWORKING
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
#endif

/// <summary>
/// Owns the lifetime of one ONLINE MATCH SESSION and guarantees that no data
/// from one room/match can leak into another. Fixes the "Player 2 returns to
/// menu, rejoins a new room, and still sees the old match's resources/UI/units"
/// contamination bug.
///
/// Two pillars:
///   1. <b>Local teardown</b> — <see cref="CleanupPreviousMatch"/> tears down
///      every piece of per-match runtime state (spawned units/buildings,
///      resources, power, selection, UI, color slots, id allocator, coordinator
///      flags, Photon player properties). Idempotent and safe to call any
///      number of times.
///   2. <b>MatchId isolation</b> — every room carries a unique
///      <see cref="CurrentMatchId"/> (a GUID stored in the room's custom
///      properties). Every gameplay RaiseEvent is tagged with it
///      (<see cref="Raise"/>), and inbound events whose MatchId doesn't match
///      the local one are dropped (<see cref="AcceptEvent"/>) — so a stray /
///      late event from a previous room can never mutate the new match.
///
/// Design notes:
///   • STATIC class — there is exactly one session at a time, so a static owner
///     can never be duplicated (directly satisfies the "prevent duplicate
///     managers" requirement) and is reachable from the static event handlers
///     without a scene reference.
///   • Self-bootstraps via <see cref="Bootstrap"/> at game start: clears all
///     runtime match state and subscribes to <c>OnRoomLeftEvent</c> so leaving
///     a room always cleans up — no scene wiring required.
///   • Integrates with the EXISTING reset systems
///     (<see cref="MatchSessionResetter"/>, <see cref="NetworkMatchCoordinator"/>,
///     <see cref="PowerManager"/>, <see cref="ResourceBank"/>) instead of
///     duplicating them.
/// </summary>
public static class MatchSessionManager
{
    // ------------------------------------------------------------------ //
    // MatchId — the room/session identity
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Unique id of the match the local client currently belongs to. Empty
    /// string means "no active match" (in the menu, between rooms, at boot).
    /// Set by <see cref="StartNewMatchSession"/> when a room is joined/created
    /// or a single-player match starts; cleared by <see cref="CleanupPreviousMatch"/>.
    /// </summary>
    public static string CurrentMatchId { get; private set; } = string.Empty;

    /// <summary>Photon room custom-property key under which the MatchId is stored.</summary>
    public const string MatchIdPropKey = "matchId";

    // Guards re-entrancy of the cleanup so a callback storm can't recurse.
    private static bool s_cleaning;

    // ------------------------------------------------------------------ //
    // Boot
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Runs automatically before the first scene loads. Guarantees runtime
    /// match data starts EMPTY on every game boot (covers the "close + reopen
    /// the game" case) and wires the leave-room → cleanup path once.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        CurrentMatchId = string.Empty;
        s_cleaning     = false;
        Debug.Log("[MatchSession] Boot — runtime match data starts empty.");

#if PHOTON_UNITY_NETWORKING
        // Leaving a room (any path: ESC menu, lobby Leave, kicked) funnels
        // through NetworkManagerRTS.OnLeftRoom which fires this static event.
        NetworkManagerRTS.OnRoomLeftEvent -= HandleRoomLeft;   // de-dupe on domain reload
        NetworkManagerRTS.OnRoomLeftEvent += HandleRoomLeft;
#endif
    }

    private static void HandleRoomLeft()
    {
        Debug.Log("[MatchSession] OnLeftRoom received — running cleanup.");
        CleanupPreviousMatch();
        Debug.Log("[MatchSession] OnLeftRoom cleanup completed.");
    }

    // ------------------------------------------------------------------ //
    // Session lifecycle
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Begin a new match session under <paramref name="matchId"/>. Runs a
    /// cleanup of any previous session FIRST so the new match starts from a
    /// guaranteed-clean slate, then stamps the new id. Pass null/empty to have
    /// a fresh GUID generated (single-player / fallback).
    /// </summary>
    public static void StartNewMatchSession(string matchId)
    {
        // Tear down whatever (if anything) lingered from a previous session.
        CleanupPreviousMatch();

        CurrentMatchId = string.IsNullOrEmpty(matchId)
            ? System.Guid.NewGuid().ToString()
            : matchId;

        Debug.Log($"[MatchSession] Started new match session — MatchId '{CurrentMatchId}'.");
    }

    /// <summary>
    /// Tear down ALL per-match runtime state so no data from the previous
    /// match/room survives. Idempotent — safe to call repeatedly and from
    /// multiple triggers (leave room, disconnect, before join, return to menu).
    /// </summary>
    public static void CleanupPreviousMatch()
    {
        if (s_cleaning) return;     // re-entrancy guard
        s_cleaning = true;
        try
        {
            Debug.Log($"[MatchSession] CleanupPreviousMatch called (clearing MatchId '{CurrentMatchId}').");

            ResetSpawnedUnitsAndBuildings();
            ResetMapInteractables();
            ResetResources();
            ResetSelectionState();
            ResetUIState();
            ResetLocalRuntimeState();
            ResetPhotonPlayerProperties();

            CurrentMatchId = string.Empty;
            Debug.Log("[MatchSession] CleanupPreviousMatch complete — MatchId cleared.");
        }
        finally
        {
            s_cleaning = false;
        }
    }

    // ------------------------------------------------------------------ //
    // Granular resets (each is independently callable + idempotent)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Destroy runtime-spawned match objects (produced units, constructed
    /// buildings, projectiles, APC passengers) and reset the color slot map,
    /// id allocator, and match coordinator flags. Delegates to the existing
    /// <see cref="MatchSessionResetter"/> so there's one teardown
    /// implementation, not two.
    /// </summary>
    public static void ResetSpawnedUnitsAndBuildings()
    {
        MatchSessionResetter.ResetForNewMatch();
        Debug.Log("[MatchSession] Spawned units/buildings reset.");
    }

    /// <summary>
    /// Reset the PER-MATCH state of scene-baked map landmarks (which survive a
    /// match reset): destructible/explosive/bridge objects back to intact, and
    /// garrison / watch-tower occupancy back to neutral. Runs AFTER
    /// <see cref="ResetSpawnedUnitsAndBuildings"/> so the (runtime) occupant
    /// units are already destroyed. Path-independent — covers leave, disconnect,
    /// lobby-back, and before-join, not just the ESC menu's OnGameReset.
    /// Includes inactive objects (map roots may be hidden by GameplayWorldRoot).
    /// </summary>
    public static void ResetMapInteractables()
    {
        int restored = 0;
        DestructibleMapObject[] destructibles = Object.FindObjectsByType<DestructibleMapObject>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < destructibles.Length; i++)
        {
            if (destructibles[i] == null) continue;
            destructibles[i].ResetToIntact();
            restored++;
        }

        int released = 0;
        GarrisonBuilding[] garrisons = Object.FindObjectsByType<GarrisonBuilding>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < garrisons.Length; i++)
        {
            if (garrisons[i] == null) continue;
            garrisons[i].ResetOccupancyForNewMatch();
            released++;
        }

        Debug.Log($"[MatchSession] Map interactables reset — {restored} restored to intact, " +
                  $"{released} garrison(s) released.");
    }

    /// <summary>
    /// Re-seed every per-owner resource bank to its configured starting value
    /// so the HUD never shows the previous match's totals. The authoritative
    /// per-match value is re-applied by MatchStart afterwards; this just clears
    /// stale state for the menu/transition window. Banks are NOT unregistered
    /// (they're scene-baked — clearing the registry would orphan them).
    /// </summary>
    public static void ResetResources()
    {
        PlayerResourceManager[] banks = Object.FindObjectsByType<PlayerResourceManager>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < banks.Length; i++)
        {
            if (banks[i] == null) continue;
            // Local-only re-seed — must NOT broadcast, or a leaving client would
            // reset the staying player's bank. MatchStart re-applies the real
            // per-match value authoritatively afterwards.
            banks[i].SetResourcesLocal(Mathf.Max(0, banks[i].startingResources));
        }

        // Ground resource nodes start fresh every match (full + visible). They
        // were previously DESTROYED on depletion and never came back for the
        // non-restarting client — that was the "resources missing for one
        // client" bug.
        ResourceNode.ResetAllForNewMatch();

        Debug.Log($"[MatchSession] Resources reset on {banks.Length} bank(s).");
    }

    /// <summary>Clear the local unit/building/aircraft selection.</summary>
    public static void ResetSelectionState()
    {
        if (UnitSelector.Instance != null)
            UnitSelector.Instance.ClearSelection();
        Debug.Log("[MatchSession] Selection state reset.");
    }

    /// <summary>
    /// Collapse transient HUD panels and force a HUD rebind so the resource /
    /// power readouts refresh against the (now clean) banks rather than showing
    /// stale text. The HUD polls each frame, so this mainly hides per-selection
    /// panels left open when the match ended.
    /// </summary>
    public static void ResetUIState()
    {
        RTSHUD hud = Object.FindFirstObjectByType<RTSHUD>(FindObjectsInactive.Include);
        if (hud != null)
        {
            hud.HideProductionPanel();
            hud.HideDozerBuildPanel();
            hud.HideTransportPanel();
        }
        Debug.Log("[MatchSession] UI state reset.");
    }

    /// <summary>
    /// Reset process-wide runtime caches that aren't tied to a specific
    /// GameObject: per-owner power grids. (Color slots / id allocator /
    /// coordinator flags are handled inside
    /// <see cref="ResetSpawnedUnitsAndBuildings"/>.)
    /// </summary>
    public static void ResetLocalRuntimeState()
    {
        if (PowerManager.Instance != null)
            PowerManager.Instance.ResetForNewMatch();

        // Re-arm the match starter so the NEXT MatchStart re-runs the camera
        // snap + per-match entity reinitialize. Its single-fire latch otherwise
        // stays true after match 1 and the non-restarting client never
        // re-applies ownership/gates — the stuck-bulldozer + jitter bug.
        MultiplayerMatchStarter starter = Object.FindFirstObjectByType<MultiplayerMatchStarter>(
            FindObjectsInactive.Include);
        if (starter != null) starter.ResetForNewMatch();

        Debug.Log("[MatchSession] Local runtime state reset.");
    }

    /// <summary>
    /// Reset the local Photon player's match-state custom properties so stale
    /// values can't bleed into the next room. Match-state keys (team, slot,
    /// ready, army, money, resources, matchId) are set back to neutral
    /// defaults. The chosen ARMY COLOUR is intentionally preserved — it's a
    /// persistent player preference, not stale match state, and MatchStart
    /// re-broadcasts the authoritative per-slot colour anyway.
    /// </summary>
    public static void ResetPhotonPlayerProperties()
    {
#if PHOTON_UNITY_NETWORKING
        if (!PhotonNetwork.IsConnected || PhotonNetwork.LocalPlayer == null)
        {
            Debug.Log("[MatchSession] Player properties reset skipped (offline).");
            return;
        }

        Hashtable props = new Hashtable
        {
            { MatchIdPropKey, string.Empty },
            { "team",      0 },
            { "slot",     -1 },
            { NetworkManagerRTS.StartSlotPropKey, NetworkManagerRTS.NoStartSlot },
            { "ready",     false },
            { "army",      string.Empty },
            { "money",     0 },
            { "resources", 0 },
        };
        PhotonNetwork.LocalPlayer.SetCustomProperties(props);
        Debug.Log("[MatchSession] Photon player properties reset (color preserved).");
#else
        Debug.Log("[MatchSession] Player properties reset skipped (Photon not installed).");
#endif
    }

    // ================================================================== //
    // Event MatchId tagging + filtering
    // ================================================================== //

#if PHOTON_UNITY_NETWORKING
    /// <summary>
    /// Send a gameplay event with the current MatchId appended as the FINAL
    /// payload element. All gameplay buses (PlayerCommand relay, match events,
    /// map-interactable events) route their sends through here so every event
    /// is tagged consistently. Index-based receivers are unaffected — they read
    /// their fixed indices and ignore the trailing tag; only
    /// <see cref="AcceptEvent"/> reads it.
    /// </summary>
    public static bool Raise(byte eventCode, object[] payload,
                             ReceiverGroup receivers, SendOptions sendOptions)
    {
        object[] tagged = AppendMatchId(payload);
        return PhotonNetwork.RaiseEvent(
            eventCode, tagged,
            new RaiseEventOptions { Receivers = receivers },
            sendOptions);
    }

    private static object[] AppendMatchId(object[] payload)
    {
        int len = payload != null ? payload.Length : 0;
        object[] tagged = new object[len + 1];
        for (int i = 0; i < len; i++) tagged[i] = payload[i];
        tagged[len] = CurrentMatchId ?? string.Empty;
        return tagged;
    }

    /// <summary>
    /// Returns true if an inbound gameplay event belongs to the CURRENT match
    /// and should be processed. Drops (and logs) events whose trailing MatchId
    /// tag doesn't match the local <see cref="CurrentMatchId"/>. Untagged events
    /// (no trailing string, e.g. MatchStart or older payloads) are accepted for
    /// backward compatibility. Call at the top of each bus's <c>OnEvent</c>.
    /// </summary>
    public static bool AcceptEvent(EventData ev, string context)
    {
        if (!(ev.CustomData is object[] payload) || payload.Length == 0) return true;

        // The MatchId tag, when present, is the final element and is a string.
        string evMatchId = payload[payload.Length - 1] as string;
        if (string.IsNullOrEmpty(evMatchId)) return true;   // untagged / SP → accept

        // A tagged event must match the current session. If we have no active
        // session (left the room) or the ids differ, it's stale → drop.
        if (evMatchId == CurrentMatchId) return true;

        Debug.Log($"[MatchSession] Ignored {context} event (code {ev.Code}) — " +
                  $"event MatchId '{evMatchId}' != current '{(string.IsNullOrEmpty(CurrentMatchId) ? "<none>" : CurrentMatchId)}'.");
        return false;
    }
#endif
}
