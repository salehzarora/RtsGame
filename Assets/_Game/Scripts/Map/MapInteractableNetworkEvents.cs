using UnityEngine;

#if PHOTON_UNITY_NETWORKING
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
#endif

/// <summary>
/// Network event bus for Interactive Tactical Map OCCUPANCY / TRAVEL state —
/// garrison enter/exit and tunnel travel. Kept SEPARATE from
/// <see cref="NetworkMatchEvents"/> so the proven combat/resource sync file is
/// untouched; this component sits next to it on the NetworkManager GameObject
/// and registers its own Photon callback.
///
/// DESTRUCTION of map objects is NOT handled here — destructible/explosive/bridge
/// objects reuse the existing master-authoritative ApplyDamage / EntityDestroyed
/// pipeline through their <see cref="Health"/> component. This bus only carries
/// the occupancy/travel state that has no existing event.
///
/// Event codes (custom range &lt;200, disjoint from NetworkMatchEvents' 1..13):
///   20 — GarrisonEnter
///   21 — GarrisonExit
///   22 — TunnelTravel
///
/// Pattern (mirrors the APC transport in NetworkMatchEvents):
///   • A broadcast is sent by the COMMANDING client (any-client gate) after it
///     applies the action locally.
///   • Receivers apply the LOCAL-only path (no re-broadcast) so there's no echo.
///   • Idempotency lives in the apply methods (occupant list de-dup), so a
///     duplicate event is harmless.
///
/// Setup: add to the NetworkManager GameObject (Tools → RTS → Map → Setup Map
/// Network Events, or add manually). Dormant in single-player.
/// </summary>
[DisallowMultipleComponent]
public class MapInteractableNetworkEvents : MonoBehaviour
#if PHOTON_UNITY_NETWORKING
    , IOnEventCallback
#endif
{
    public const byte GarrisonEnterEventCode = 20;
    public const byte GarrisonExitEventCode  = 21;
    public const byte TunnelTravelEventCode  = 22;

    public static MapInteractableNetworkEvents Instance { get; private set; }

#if PHOTON_UNITY_NETWORKING
    // Reentry guard — set while applying a received event so the local apply
    // can never re-broadcast (defence-in-depth; the apply methods already avoid
    // broadcasting).
    private static bool s_applying;
#endif

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[MapNet] Duplicate MapInteractableNetworkEvents destroyed.");
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

#if PHOTON_UNITY_NETWORKING
    private void OnEnable()  { PhotonNetwork.AddCallbackTarget(this); }
    private void OnDisable() { PhotonNetwork.RemoveCallbackTarget(this); }
#endif

    // ================================================================== //
    // Broadcast API
    // ================================================================== //

    public static void BroadcastGarrisonEnter(string garrisonId, string unitId, int occupyingOwnerId)
    {
#if PHOTON_UNITY_NETWORKING
        if (!ShouldBroadcast()) return;
        object[] payload = { garrisonId ?? string.Empty, unitId ?? string.Empty, occupyingOwnerId };
        bool ok = MatchSessionManager.Raise(
            GarrisonEnterEventCode, payload,
            ReceiverGroup.Others, SendOptions.SendReliable);
        if (ok)
            Debug.Log($"[MapNet] Broadcast garrison-enter garrison={garrisonId} unit={unitId} owner={occupyingOwnerId}");
#endif
    }

    public static void BroadcastGarrisonExit(string garrisonId, string unitId,
                                             Vector3 exitPos, Vector3 forward)
    {
#if PHOTON_UNITY_NETWORKING
        if (!ShouldBroadcast()) return;
        object[] payload = { garrisonId ?? string.Empty, unitId ?? string.Empty, exitPos, forward };
        bool ok = MatchSessionManager.Raise(
            GarrisonExitEventCode, payload,
            ReceiverGroup.Others, SendOptions.SendReliable);
        if (ok)
            Debug.Log($"[MapNet] Broadcast garrison-exit garrison={garrisonId} unit={unitId} at {exitPos:F1}");
#endif
    }

    public static void BroadcastTunnelTravel(string fromTunnelId, string toTunnelId, string unitId,
                                             Vector3 exitPos, Vector3 forward)
    {
#if PHOTON_UNITY_NETWORKING
        if (!ShouldBroadcast()) return;
        object[] payload =
        {
            fromTunnelId ?? string.Empty,
            toTunnelId   ?? string.Empty,
            unitId       ?? string.Empty,
            exitPos,
            forward,
        };
        bool ok = MatchSessionManager.Raise(
            TunnelTravelEventCode, payload,
            ReceiverGroup.Others, SendOptions.SendReliable);
        if (ok)
            Debug.Log($"[MapNet] Broadcast tunnel-travel from={fromTunnelId} to={toTunnelId} unit={unitId}");
#endif
    }

#if PHOTON_UNITY_NETWORKING
    private static bool ShouldBroadcast()
    {
        if (s_applying) return false;
        if (!NetworkManagerRTS.IsMultiplayerEnabled) return false;
        return true;     // any-client — only the unit's owner reaches these call sites
    }

    // ================================================================== //
    // Inbound dispatch
    // ================================================================== //

    public void OnEvent(EventData ev)
    {
        // Session isolation — drop garrison/tunnel events from another match.
        if (!MatchSessionManager.AcceptEvent(ev, "MapInteractable")) return;

        switch (ev.Code)
        {
            case GarrisonEnterEventCode: HandleGarrisonEnter(ev); break;
            case GarrisonExitEventCode:  HandleGarrisonExit(ev);  break;
            case TunnelTravelEventCode:  HandleTunnelTravel(ev);  break;
        }
    }

    private static void HandleGarrisonEnter(EventData ev)
    {
        if (!(ev.CustomData is object[] p) || p.Length < 3) return;
        string garrisonId = p[0] as string ?? string.Empty;
        string unitId     = p[1] as string ?? string.Empty;
        int    owner      = (int)p[2];

        GameEntity gGe = EntityRegistry.Find(garrisonId);
        GameEntity uGe = EntityRegistry.Find(unitId);
        if (gGe == null || uGe == null)
        {
            Debug.Log($"[MapNet] garrison-enter dropped — garrison found={gGe != null}, unit found={uGe != null}.");
            return;
        }

        GarrisonBuilding garrison = gGe.GetComponent<GarrisonBuilding>();
        if (garrison == null) return;

        bool prev = s_applying;
        s_applying = true;
        try { garrison.ApplyEnterFromNetwork(uGe.gameObject, owner); }
        finally { s_applying = prev; }
    }

    private static void HandleGarrisonExit(EventData ev)
    {
        if (!(ev.CustomData is object[] p) || p.Length < 4) return;
        string  garrisonId = p[0] as string ?? string.Empty;
        string  unitId     = p[1] as string ?? string.Empty;
        Vector3 exitPos    = (Vector3)p[2];
        Vector3 forward    = (Vector3)p[3];

        GameEntity gGe = EntityRegistry.Find(garrisonId);
        if (gGe == null) return;
        GarrisonBuilding garrison = gGe.GetComponent<GarrisonBuilding>();
        if (garrison == null) return;

        bool prev = s_applying;
        s_applying = true;
        try { garrison.ApplyExitFromNetwork(unitId, exitPos, forward); }
        finally { s_applying = prev; }
    }

    private static void HandleTunnelTravel(EventData ev)
    {
        if (!(ev.CustomData is object[] p) || p.Length < 5) return;
        string  fromTunnelId = p[0] as string ?? string.Empty;
        string  toTunnelId   = p[1] as string ?? string.Empty;
        string  unitId       = p[2] as string ?? string.Empty;
        Vector3 exitPos      = (Vector3)p[3];
        Vector3 forward      = (Vector3)p[4];
        _ = toTunnelId;     // carried for logging/diagnostics; exit pose is authoritative

        GameEntity fGe = EntityRegistry.Find(fromTunnelId);
        GameEntity uGe = EntityRegistry.Find(unitId);
        if (fGe == null || uGe == null)
        {
            Debug.Log($"[MapNet] tunnel-travel dropped — tunnel found={fGe != null}, unit found={uGe != null}.");
            return;
        }

        TunnelEntrance tunnel = fGe.GetComponent<TunnelEntrance>();
        if (tunnel == null) return;

        bool prev = s_applying;
        s_applying = true;
        try { tunnel.ApplyTravelFromNetwork(uGe.gameObject, exitPos, forward); }
        finally { s_applying = prev; }
    }
#endif
}
