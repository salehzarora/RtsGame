using System.Collections;
using System.Linq;
using UnityEngine;

/// <summary>
/// Client-B automation for TRUE two-client PvP smoke tests. Completely inert
/// unless the player was launched with the <c>-autotest</c> command-line
/// argument — normal players and the editor never run this.
///
/// Flow (all steps logged with an [AutoTest] prefix to the player log):
///   1. Enable multiplayerMode, Connect() to Photon.
///   2. Poll until in lobby, then JoinRoomByName("BridgeSmokeTest") with
///      retries (the editor client creates that room).
///   3. Set local color red, wait for MatchStart (scene loads via Photon).
///   4. After spawn: every 8 s, order own units to alternate between two
///      offsets near their base (movement-sync traffic for the editor to
///      observe), and log a full Health census (own + enemy entities with
///      hp values) so damage sync can be verified from the log file.
///
/// The editor side reads this client's log from
/// %USERPROFILE%/AppData/LocalLow/&lt;company&gt;/&lt;product&gt;/Player.log
/// (or the -logFile path passed at launch).
/// </summary>
public static class TwoClientAutoTester
{
    private const string RoomName = "BridgeSmokeTest";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (!System.Environment.GetCommandLineArgs().Any(a => a == "-autotest")) return;
        var go = new GameObject("TwoClientAutoTester");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<AutoTestRunner>();
        Debug.Log("[AutoTest] Activated by -autotest arg. Target room: " + RoomName);
    }

    private class AutoTestRunner : MonoBehaviour
    {
        private IEnumerator Start()
        {
            yield return new WaitForSeconds(3f);

            var nm = FindFirstObjectByType<NetworkManagerRTS>(FindObjectsInactive.Include);
            if (nm == null) { Debug.LogError("[AutoTest] No NetworkManagerRTS — aborting."); yield break; }

            nm.multiplayerMode = true;
            nm.SetLocalPlayerColor(new Color(0.92f, 0.2f, 0.2f), "Red");
            nm.Connect();
            Debug.Log("[AutoTest] Connect() issued, color=Red.");

#if PHOTON_UNITY_NETWORKING
            // Join loop — retry until the editor's room exists and we're in.
            float deadline = Time.time + 120f;
            while (Time.time < deadline && !Photon.Pun.PhotonNetwork.InRoom)
            {
                if (Photon.Pun.PhotonNetwork.InLobby)
                {
                    Debug.Log("[AutoTest] In lobby — trying JoinRoomByName('" + RoomName + "')...");
                    nm.JoinRoomByName(RoomName);
                    yield return new WaitForSeconds(5f);
                }
                else yield return new WaitForSeconds(2f);
            }
            if (!Photon.Pun.PhotonNetwork.InRoom)
            { Debug.LogError("[AutoTest] ✗ Could not join room within 120 s."); yield break; }

            Debug.Log("[AutoTest] ✓ JOINED room '" + Photon.Pun.PhotonNetwork.CurrentRoom.Name +
                      "' players=" + Photon.Pun.PhotonNetwork.CurrentRoom.PlayerCount +
                      " actor=" + Photon.Pun.PhotonNetwork.LocalPlayer.ActorNumber);

            // Wait for MatchStart (coordinator flips IsMatchStarted after the
            // master broadcasts; scene switches via PhotonNetwork.LoadLevel).
            deadline = Time.time + 300f;
            while (Time.time < deadline &&
                   (NetworkMatchCoordinator.Instance == null ||
                    !NetworkMatchCoordinator.Instance.IsMatchStarted))
                yield return new WaitForSeconds(2f);

            if (NetworkMatchCoordinator.Instance == null || !NetworkMatchCoordinator.Instance.IsMatchStarted)
            { Debug.LogError("[AutoTest] ✗ MatchStart not received within 300 s."); yield break; }

            yield return new WaitForSeconds(6f);   // let spawn/ownership settle
            Debug.Log("[AutoTest] ✓ MATCH STARTED. localPlayerId=" + NetworkManagerRTS.LocalPlayerId +
                      " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            Census("post-start");

            // Movement traffic loop + periodic census.
            int tick = 0;
            while (Photon.Pun.PhotonNetwork.InRoom)
            {
                tick++;
                OrderOwnUnits(tick % 2 == 0 ? new Vector3(6f, 0f, 0f) : new Vector3(0f, 0f, 6f));
                yield return new WaitForSeconds(8f);
                if (tick % 2 == 0) Census("tick" + tick);
            }
            Debug.Log("[AutoTest] Room left / closed — auto-test loop ended.");
#else
            yield break;
#endif
        }

        private void OrderOwnUnits(Vector3 offset)
        {
            int moved = 0;
            foreach (var ge in FindObjectsByType<GameEntity>(FindObjectsSortMode.None))
            {
                if (ge.ownerPlayerId != NetworkManagerRTS.LocalPlayerId) continue;
                var mv = ge.GetComponent<UnitMovement>();
                if (mv == null) continue;
                mv.MoveTo(ge.transform.position + offset);
                moved++;
                if (moved >= 3) break;   // a few units are enough traffic
            }
            Debug.Log("[AutoTest] Move order to " + moved + " own unit(s), offset " + offset + ".");
        }

        private void Census(string label)
        {
            int own = 0, enemy = 0;
            var sb = new System.Text.StringBuilder("[AutoTest] ── census(" + label + ") pid=" +
                                                   NetworkManagerRTS.LocalPlayerId + " ──\n");
            foreach (var h in FindObjectsByType<Health>(FindObjectsSortMode.None))
            {
                var ge = h.GetComponent<GameEntity>();
                int owner = ge != null ? ge.ownerPlayerId : -9;
                bool mine = owner == NetworkManagerRTS.LocalPlayerId;
                if (mine) own++; else enemy++;
                sb.Append(mine ? "  [own]  " : "  [other]")
                  .Append(h.gameObject.name)
                  .Append(" owner=").Append(owner)
                  .Append(" hp=").Append(h.CurrentHealth.ToString("F0")).Append('/').Append(h.maxHealth)
                  .Append(" pos=").Append(h.transform.position.ToString("F1")).AppendLine();
            }
            sb.Append("  totals: own=").Append(own).Append(" other=").Append(enemy);
            Debug.Log(sb.ToString());
        }
    }
}
