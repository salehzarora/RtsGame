using UnityEditor;
using UnityEngine;

#if PHOTON_UNITY_NETWORKING
using Photon.Pun;
using Photon.Realtime;
#endif

/// <summary>
/// Diagnostic dump of the current Photon room state — useful in Play mode
/// while the lobby is open or right before clicking Start Match. Tells you
/// exactly which Photon Player Custom Properties each actor has set, so you
/// can confirm whether color / corner selections actually reached Photon
/// (vs. only the local UI).
///
/// Menu: Tools → RTS → UI → Print Lobby State
///
/// Output per actor:
///   • ActorNumber + NickName + (master/local) flag
///   • armyColorName (what the lobby picker stored)
///   • armyColor RGB (the canonical Vector3)
///   • startSlot value + corner letter
/// Plus a per-corner occupancy summary so you can see, before clicking
/// Start Match, whether A/B/C/D are claimed and by whom.
/// </summary>
public static class PrintLobbyState
{
    [MenuItem("Tools/RTS/UI/Print Lobby State")]
    public static void Print()
    {
        Debug.Log("[LobbyState] ────────────── Photon room snapshot ──────────────");

#if PHOTON_UNITY_NETWORKING
        if (!PhotonNetwork.IsConnected)
        {
            Debug.LogWarning("[LobbyState] Photon NOT connected. Nothing to print.");
            Debug.Log("[LobbyState] ───────────────────────────────────────────────");
            return;
        }
        if (!PhotonNetwork.InRoom)
        {
            Debug.LogWarning($"[LobbyState] Photon connected (state={PhotonNetwork.NetworkClientState}) " +
                             "but NOT in a room. Nothing to print.");
            Debug.Log("[LobbyState] ───────────────────────────────────────────────");
            return;
        }

        Room room = PhotonNetwork.CurrentRoom;
        Debug.Log($"[LobbyState] Room: '{room.Name}'  Players: {room.PlayerCount}/{room.MaxPlayers}  " +
                  $"MasterActor: #{room.MasterClientId}  LocalActor: #{PhotonNetwork.LocalPlayer.ActorNumber}");

        // Sort actors so the slot index (0..N-1) is deterministic — matches
        // exactly what BroadcastMatchStart will do at match start.
        var sorted = new System.Collections.Generic.List<Player>(room.Players.Values);
        sorted.Sort((a, b) => a.ActorNumber.CompareTo(b.ActorNumber));

        // Per-actor dump.
        bool[] cornerTaken = new bool[4];
        int[]  cornerBy    = { -1, -1, -1, -1 };

        for (int i = 0; i < sorted.Count; i++)
        {
            Player p = sorted[i];
            string nick = string.IsNullOrEmpty(p.NickName) ? "<no nick>" : p.NickName;
            string flags = $"{(p.IsMasterClient ? " master" : "")}{(p.IsLocal ? " local" : "")}";

            string colorName = "?";
            string colorRgb  = "?";
            if (p.CustomProperties != null)
            {
                if (p.CustomProperties.TryGetValue(NetworkManagerRTS.ColorNamePropKey, out object cn) &&
                    cn is string s && !string.IsNullOrEmpty(s))
                    colorName = s;
                if (p.CustomProperties.TryGetValue(NetworkManagerRTS.ColorPropKey, out object rgb) &&
                    rgb is Vector3 v)
                    colorRgb = $"RGB({v.x:F2},{v.y:F2},{v.z:F2})";
            }

            string slotStr = "-";
            if (NetworkManagerRTS.TryGetPlayerStartSlot(p.ActorNumber, out int sc))
            {
                slotStr = $"{sc} ({(char)('A' + sc)})";
                if (sc >= 0 && sc < 4 && !cornerTaken[sc])
                {
                    cornerTaken[sc] = true;
                    cornerBy[sc] = p.ActorNumber;
                }
            }

            Debug.Log($"[LobbyState]   Actor #{p.ActorNumber} '{nick}'{flags}  " +
                      $"playerSlot={i}  armyColorName={colorName}  armyColor={colorRgb}  " +
                      $"startSlot={slotStr}");
        }

        // Per-corner occupancy summary.
        Debug.Log("[LobbyState] Corner occupancy:");
        for (int c = 0; c < 4; c++)
        {
            char letter = (char)('A' + c);
            if (cornerTaken[c])
                Debug.Log($"[LobbyState]   Corner {letter}: actor #{cornerBy[c]}.");
            else
                Debug.Log($"[LobbyState]   Corner {letter}: free.");
        }

        Debug.Log("[LobbyState] If you click Start Match now, the master will:");
        Debug.Log("[LobbyState]   • read each actor's armyColor + armyColorName (those above);");
        Debug.Log("[LobbyState]   • read each actor's startSlot (those above);");
        Debug.Log("[LobbyState]   • dedupe + random-fill any unchosen players into the free corners;");
        Debug.Log("[LobbyState]   • write the resulting payload to PhotonNetwork.CurrentRoom.CustomProperties;");
        Debug.Log("[LobbyState]   • PhotonNetwork.LoadLevel(gameMapSceneName) to all clients.");
        Debug.Log("[LobbyState] Watch the Console for [MatchStart] Actor #X: ... lines to confirm.");
#else
        Debug.LogError("[LobbyState] Photon PUN not installed.");
#endif

        Debug.Log("[LobbyState] ───────────────────────────────────────────────");
    }
}
