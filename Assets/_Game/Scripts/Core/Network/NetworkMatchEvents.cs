using UnityEngine;

#if PHOTON_UNITY_NETWORKING
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
#endif

/// <summary>
/// Phase 10.3 — authoritative MATCH-STATE event bus. Sits next to
/// <see cref="NetworkCommandRelay"/> on the NetworkManager GameObject and
/// reconciles per-frame combat / resource state across clients.
///
/// Why this exists, in plain terms:
///   • <see cref="NetworkCommandRelay"/> already replays player INTENT
///     (Move / Attack / Produce / Build / Unload) deterministically — both
///     clients run the same command, so unit spawns and construction site
///     creation are already in sync once starting resources match.
///   • What the command relay does NOT cover is the per-frame OUTCOME of
///     combat: projectile timing, hit registration, exact damage values,
///     death moments. Each client runs combat locally and tiny timing
///     differences cause workers-dead-on-one-client-alive-on-the-other.
///   • This service syncs the three outcome streams that diverged today:
///     damage, death, and resource bank changes.
///
/// Authority model — <b>MasterClient is authoritative</b>:
///   • Every client runs local combat / mutation code as before (no
///     latency added).
///   • The master client ALSO broadcasts the resulting newHealth /
///     destroyed / newAmount via Photon's RaiseEvent.
///   • Non-master clients SNAP their local state to the master's value on
///     receive. If their local computation matched, the snap is a no-op;
///     if it diverged, master wins.
///   • If the master disconnects, Photon switches master automatically and
///     the new master takes over broadcasting.
///
/// Event code map (Photon-reserved range is 200+, custom range is 1..199):
///   1 — PlayerCommand (NetworkCommandRelay)
///   2 — MatchStart    (NetworkMatchCoordinator)
///   3 — ApplyDamage        (this file)
///   4 — EntityDestroyed    (this file)
///   5 — ResourceChanged    (this file)
///
/// Setup:
///   Attach to the NetworkManager GameObject alongside
///   <see cref="NetworkManagerRTS"/> and <see cref="NetworkCommandRelay"/>.
///   The editor tool Tools → RTS → Multiplayer → Setup Network Manager
///   does this for you.
/// </summary>
[DisallowMultipleComponent]
public class NetworkMatchEvents : MonoBehaviour
#if PHOTON_UNITY_NETWORKING
    , IOnEventCallback
#endif
{
    // ------------------------------------------------------------------ //
    // Event codes — keep numerically below 200 (Photon reserves >=200).
    // ------------------------------------------------------------------ //

    public const byte ApplyDamageEventCode           = 3;
    public const byte EntityDestroyedEventCode       = 4;
    public const byte ResourceChangedEventCode       = 5;
    public const byte PassengerLoadedEventCode       = 6;
    public const byte PassengerUnloadedEventCode     = 7;
    public const byte WorkerGatherEventCode          = 8;
    public const byte ConstructionCompleteEventCode  = 9;
    public const byte BoardingStartedEventCode       = 10;
    public const byte EntityStateSnapshotEventCode   = 11;

    // ------------------------------------------------------------------ //
    // Reentry guard — set while we're applying a received event so the
    // local mutation (e.g. Health.TakeDamage, PlayerResourceManager.SetResources)
    // doesn't re-broadcast and create an echo loop.
    //
    // Static field is safe — Unity is single-threaded; Photon events are
    // dispatched from the main thread via PhotonNetwork's Update pump.
    // ------------------------------------------------------------------ //

    /// <summary>
    /// True while a received network event is being applied locally.
    /// Mutation paths in Health / PlayerResourceManager check this and skip
    /// their broadcast step so we don't bounce the same event back out.
    /// </summary>
    public static bool IsApplyingNetworkEvent { get; private set; }

    /// <summary>Singleton — one per scene, lives on the NetworkManager GameObject.</summary>
    public static NetworkMatchEvents Instance { get; private set; }

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[NetMatchEvents] Duplicate NetworkMatchEvents destroyed.");
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
    // Public broadcast API
    //
    // Each Broadcast* method short-circuits when networking is dormant or
    // this client isn't the master — only the master is allowed to push
    // authoritative state. Local mutation code calls these AFTER applying
    // the change locally; the broadcast just tells everyone else what
    // value to snap to.
    // ================================================================== //

    /// <summary>
    /// Tell every other client what <paramref name="targetEntityId"/>'s
    /// health is now. Receivers <see cref="Health.ApplyDamageFromNetwork"/>
    /// snap to <paramref name="newHealth"/> directly — no relative subtraction.
    /// </summary>
    public static void BroadcastApplyDamage(string targetEntityId, string attackerEntityId,
                                            float damageAmount, float newHealth)
    {
#if PHOTON_UNITY_NETWORKING
        if (!ShouldBroadcast()) return;

        object[] payload =
        {
            targetEntityId   ?? string.Empty,
            attackerEntityId ?? string.Empty,
            damageAmount,
            newHealth,
        };
        bool ok = PhotonNetwork.RaiseEvent(
            ApplyDamageEventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);

        if (ok)
            Debug.Log($"[NetDamage] Broadcast target={targetEntityId} damage={damageAmount} newHealth={newHealth}");
#endif
    }

    /// <summary>
    /// Tell every other client that <paramref name="entityId"/> is gone.
    /// Receivers call <see cref="Health.DestroyFromNetwork"/> which destroys
    /// the GameObject if still alive locally, or no-ops if already destroyed.
    ///
    /// Authority — death uses <see cref="ShouldBroadcastAnyClient"/> instead
    /// of the master-only gate so that a non-master client that locally
    /// kills a unit always tells the rest of the room. Without this, a
    /// non-master kill that the master didn't see (combat timing drift)
    /// would leave the unit alive forever on the master's side.
    /// Idempotency on the receive side (<see cref="Health.dying"/>) makes
    /// duplicate broadcasts safe.
    /// </summary>
    public static void BroadcastEntityDestroyed(string entityId, string killerEntityId)
    {
#if PHOTON_UNITY_NETWORKING
        if (!ShouldBroadcastAnyClient()) return;

        object[] payload =
        {
            entityId       ?? string.Empty,
            killerEntityId ?? string.Empty,
        };
        bool ok = PhotonNetwork.RaiseEvent(
            EntityDestroyedEventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);

        if (ok)
            Debug.Log($"[NetDeath] Broadcast destroy entity={entityId} killer={killerEntityId}");
#endif
    }

    /// <summary>
    /// Notify every other client that <paramref name="passengerId"/> just
    /// boarded <paramref name="apcId"/>. Receivers hide / disable that
    /// passenger and append it to the APC's local passenger list. Sent by
    /// the owning client (the one whose <see cref="UnitSelector"/> issued
    /// the board command) so the load mirrors even when the master client
    /// isn't involved.
    /// </summary>
    public static void BroadcastPassengerLoaded(string apcId, string passengerId)
    {
#if PHOTON_UNITY_NETWORKING
        if (!ShouldBroadcastAnyClient()) return;

        object[] payload = { apcId ?? string.Empty, passengerId ?? string.Empty };
        bool ok = PhotonNetwork.RaiseEvent(
            PassengerLoadedEventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);
        if (ok)
            Debug.Log($"[NetAPC] Broadcast load apc={apcId} passenger={passengerId}");
#endif
    }

    /// <summary>
    /// Notify every other client that <paramref name="passengerId"/> just
    /// exited <paramref name="apcId"/> at <paramref name="exitPos"/> facing
    /// <paramref name="forwardDir"/>. Receivers re-activate the passenger
    /// at the exact exit pose so the squad lands in the same place on both
    /// clients.
    /// </summary>
    public static void BroadcastPassengerUnloaded(string apcId, string passengerId,
                                                  Vector3 exitPos, Vector3 forwardDir)
    {
#if PHOTON_UNITY_NETWORKING
        if (!ShouldBroadcastAnyClient()) return;

        object[] payload =
        {
            apcId       ?? string.Empty,
            passengerId ?? string.Empty,
            exitPos,
            forwardDir,
        };
        bool ok = PhotonNetwork.RaiseEvent(
            PassengerUnloadedEventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);
        if (ok)
            Debug.Log($"[NetAPC] Broadcast unload apc={apcId} passenger={passengerId} at {exitPos:F1}");
#endif
    }

    /// <summary>
    /// Notify every other client that <paramref name="siteEntityId"/>'s
    /// construction has reached 100%. Receivers find their local copy of
    /// the site and force it to complete (spawn the final building, swap
    /// ids, apply ownership). Idempotent — sites already completed
    /// locally drop the event.
    ///
    /// Authority: any client may broadcast — first-to-complete wins. The
    /// receive side's "site already gone" check prevents a duplicate final
    /// building from being spawned.
    /// </summary>
    /// <summary>
    /// Notify every other client that <paramref name="passengerId"/> has
    /// been ordered to board <paramref name="apcId"/>. Receivers add an
    /// <see cref="InfantryBoardingAgent"/> to their local copy of the
    /// passenger so it visibly walks to the APC instead of just
    /// disappearing into invisibility at PassengerLoaded time.
    /// </summary>
    public static void BroadcastBoardingStarted(string passengerId, string apcId)
    {
#if PHOTON_UNITY_NETWORKING
        if (!ShouldBroadcastAnyClient()) return;

        object[] payload = { passengerId ?? string.Empty, apcId ?? string.Empty };
        bool ok = PhotonNetwork.RaiseEvent(
            BoardingStartedEventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);
        if (ok)
            Debug.Log($"[NetAPC] Broadcast boarding started passenger={passengerId} apc={apcId}");
#endif
    }

    /// <summary>
    /// Master-only authoritative snapshot of an entity's owner / active /
    /// health state. Receivers call <see cref="ApplyEntityStateSnapshot"/>
    /// to reconcile drift. Used by <see cref="NetworkEntityStateSync"/> as
    /// the desync safety net.
    /// </summary>
    public static void BroadcastEntityStateSnapshot(string entityId, int ownerPlayerId,
                                                    bool isActive, float currentHealth)
    {
#if PHOTON_UNITY_NETWORKING
        if (!ShouldBroadcast()) return;     // master-only — snapshots are canonical state

        object[] payload =
        {
            entityId ?? string.Empty,
            ownerPlayerId,
            isActive,
            currentHealth,
        };
        // SendUnreliable — snapshots are sent at 0.5s cadence so losing one is fine,
        // the next will resync. Saves Photon throughput.
        PhotonNetwork.RaiseEvent(
            EntityStateSnapshotEventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendUnreliable);
#endif
    }

    public static void BroadcastConstructionComplete(string siteEntityId, int ownerPlayerId,
                                                     string finalEntityId, string buildingLabel)
    {
#if PHOTON_UNITY_NETWORKING
        if (!ShouldBroadcastAnyClient()) return;

        object[] payload =
        {
            siteEntityId   ?? string.Empty,
            ownerPlayerId,
            finalEntityId  ?? string.Empty,
            buildingLabel  ?? string.Empty,
        };
        bool ok = PhotonNetwork.RaiseEvent(
            ConstructionCompleteEventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);
        if (ok)
            Debug.Log($"[ConstructionNet] Broadcast complete site={siteEntityId} " +
                      $"final={finalEntityId} owner={ownerPlayerId} type={buildingLabel}");
#endif
    }

    /// <summary>
    /// Notify every other client that <paramref name="workerId"/> has been
    /// ordered to gather <paramref name="resourceNodeId"/>. Receivers call
    /// the same SetGatherTarget on their local copy of the worker so the
    /// gather animation / state machine mirrors across clients.
    /// </summary>
    public static void BroadcastWorkerGather(string workerId, string resourceNodeId)
    {
#if PHOTON_UNITY_NETWORKING
        if (!ShouldBroadcastAnyClient()) return;

        object[] payload = { workerId ?? string.Empty, resourceNodeId ?? string.Empty };
        bool ok = PhotonNetwork.RaiseEvent(
            WorkerGatherEventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);
        if (ok)
            Debug.Log($"[NetWorker] Broadcast gather worker={workerId} node={resourceNodeId}");
#endif
    }

    /// <summary>
    /// Tell every other client the new resource amount for
    /// <paramref name="ownerPlayerId"/>. Receivers
    /// <see cref="PlayerResourceManager.ApplyFromNetwork"/> snap to
    /// <paramref name="newAmount"/> — no relative delta application.
    /// </summary>
    public static void BroadcastResourceChanged(int ownerPlayerId, int newAmount,
                                                int delta, string reason)
    {
#if PHOTON_UNITY_NETWORKING
        if (!ShouldBroadcast()) return;

        object[] payload =
        {
            ownerPlayerId,
            newAmount,
            delta,
            reason ?? string.Empty,
        };
        bool ok = PhotonNetwork.RaiseEvent(
            ResourceChangedEventCode, payload,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);

        if (ok)
            Debug.Log($"[NetResources] Broadcast owner={ownerPlayerId} newAmount={newAmount} delta={delta} reason={reason}");
#endif
    }

    // ------------------------------------------------------------------ //
    // Broadcast gate
    // ------------------------------------------------------------------ //

#if PHOTON_UNITY_NETWORKING
    /// <summary>
    /// Master-only broadcast gate. Used for state where a single canonical
    /// source matters and only one client should push: damage values
    /// (avoid double-subtract) and resource bank totals (avoid double-spend
    /// reconciliation).
    /// </summary>
    private static bool ShouldBroadcast()
    {
        if (IsApplyingNetworkEvent) return false;
        if (!NetworkManagerRTS.IsMultiplayerEnabled) return false;
        if (!PhotonNetwork.IsMasterClient)           return false;
        return true;
    }

    /// <summary>
    /// Any-client broadcast gate. Used for state where the receive-side
    /// already has idempotency, so duplicate broadcasts are harmless and
    /// "first-to-act wins" produces the right outcome: death (one client
    /// kills a unit the other client missed), APC load/unload (owner
    /// client drives the transport), worker gather order (owner client
    /// drove the right-click).
    /// </summary>
    private static bool ShouldBroadcastAnyClient()
    {
        if (IsApplyingNetworkEvent) return false;
        if (!NetworkManagerRTS.IsMultiplayerEnabled) return false;
        return true;
    }
#endif

    // ================================================================== //
    // Inbound dispatch
    // ================================================================== //

#if PHOTON_UNITY_NETWORKING
    public void OnEvent(EventData ev)
    {
        switch (ev.Code)
        {
            case ApplyDamageEventCode:       HandleApplyDamage(ev);       break;
            case EntityDestroyedEventCode:   HandleEntityDestroyed(ev);   break;
            case ResourceChangedEventCode:   HandleResourceChanged(ev);   break;
            case PassengerLoadedEventCode:      HandlePassengerLoaded(ev);      break;
            case PassengerUnloadedEventCode:    HandlePassengerUnloaded(ev);    break;
            case WorkerGatherEventCode:         HandleWorkerGather(ev);         break;
            case ConstructionCompleteEventCode: HandleConstructionComplete(ev); break;
            case BoardingStartedEventCode:      HandleBoardingStarted(ev);      break;
            case EntityStateSnapshotEventCode:  HandleEntityStateSnapshot(ev);  break;
            // Other codes (1=PlayerCommand, 2=MatchStart) are handled by
            // their owner components.
        }
    }

    private static void HandlePassengerLoaded(EventData ev)
    {
        if (!(ev.CustomData is object[] payload) || payload.Length < 2)
        {
            Debug.LogWarning("[NetAPC] PassengerLoaded payload invalid.");
            return;
        }

        string apcId       = payload[0] as string ?? string.Empty;
        string passengerId = payload[1] as string ?? string.Empty;

        GameEntity apcGe = EntityRegistry.Find(apcId);
        GameEntity paxGe = EntityRegistry.Find(passengerId);
        if (apcGe == null || paxGe == null)
        {
            Debug.Log($"[NetAPC] PassengerLoaded dropped — apc='{apcId}' (found={apcGe != null}), " +
                      $"passenger='{passengerId}' (found={paxGe != null}).");
            return;
        }

        APCTransport apc = apcGe.GetComponent<APCTransport>();
        if (apc == null) return;

        Debug.Log($"[NetAPC] Apply load apc={apcId} passenger={passengerId}");

        bool prev = IsApplyingNetworkEvent;
        IsApplyingNetworkEvent = true;
        try
        {
            apc.ApplyLoadFromNetwork(paxGe.gameObject);
        }
        finally
        {
            IsApplyingNetworkEvent = prev;
        }
    }

    private static void HandlePassengerUnloaded(EventData ev)
    {
        if (!(ev.CustomData is object[] payload) || payload.Length < 4)
        {
            Debug.LogWarning("[NetAPC] PassengerUnloaded payload invalid.");
            return;
        }

        string  apcId       = payload[0] as string ?? string.Empty;
        string  passengerId = payload[1] as string ?? string.Empty;
        Vector3 exitPos     = (Vector3)payload[2];
        Vector3 forwardDir  = (Vector3)payload[3];

        GameEntity apcGe = EntityRegistry.Find(apcId);
        if (apcGe == null)
        {
            Debug.Log($"[NetAPC] PassengerUnloaded dropped — apc '{apcId}' not in registry.");
            return;
        }
        APCTransport apc = apcGe.GetComponent<APCTransport>();
        if (apc == null) return;

        // Passenger may not be in the registry yet on the remote — that's
        // fine, APC carries it in its passenger list by direct reference.
        // We pass the id so the APC can locate by name+id if needed.
        Debug.Log($"[NetAPC] Apply unload apc={apcId} passenger={passengerId} at {exitPos:F1}");

        bool prev = IsApplyingNetworkEvent;
        IsApplyingNetworkEvent = true;
        try
        {
            apc.ApplyUnloadFromNetwork(passengerId, exitPos, forwardDir);
        }
        finally
        {
            IsApplyingNetworkEvent = prev;
        }
    }

    private static void HandleBoardingStarted(EventData ev)
    {
        if (!(ev.CustomData is object[] payload) || payload.Length < 2)
        {
            Debug.LogWarning("[NetAPC] BoardingStarted payload invalid.");
            return;
        }

        string passengerId = payload[0] as string ?? string.Empty;
        string apcId       = payload[1] as string ?? string.Empty;

        GameEntity paxGe = EntityRegistry.Find(passengerId);
        GameEntity apcGe = EntityRegistry.Find(apcId);
        if (paxGe == null || apcGe == null)
        {
            Debug.Log($"[NetAPC] BoardingStarted dropped — pax='{passengerId}' " +
                      $"(found={paxGe != null}), apc='{apcId}' (found={apcGe != null}).");
            return;
        }

        APCTransport apc = apcGe.GetComponent<APCTransport>();
        if (apc == null) return;

        // Idempotent: if the passenger already has a boarding agent driving
        // it, just retarget instead of doubling up.
        InfantryBoardingAgent existing = paxGe.GetComponent<InfantryBoardingAgent>();
        InfantryBoardingAgent ba = existing != null
            ? existing
            : paxGe.gameObject.AddComponent<InfantryBoardingAgent>();
        ba.StartBoarding(apc);

        Debug.Log($"[NetAPC] Apply boarding started passenger={passengerId} apc={apcId}");
    }

    private static void HandleEntityStateSnapshot(EventData ev)
    {
        if (!(ev.CustomData is object[] payload) || payload.Length < 4)
        {
            Debug.LogWarning("[NetSnap] EntityStateSnapshot payload invalid.");
            return;
        }

        string entityId      = payload[0] as string ?? string.Empty;
        int    ownerPlayerId = (int)payload[1];
        bool   isActive      = (bool)payload[2];
        float  currentHealth = (float)payload[3];

        bool prev = IsApplyingNetworkEvent;
        IsApplyingNetworkEvent = true;
        try
        {
            NetworkEntityStateSync.ApplyEntityStateSnapshot(
                entityId, ownerPlayerId, isActive, currentHealth);
        }
        finally
        {
            IsApplyingNetworkEvent = prev;
        }
    }

    private static void HandleConstructionComplete(EventData ev)
    {
        if (!(ev.CustomData is object[] payload) || payload.Length < 4)
        {
            Debug.LogWarning("[ConstructionNet] ConstructionComplete payload invalid.");
            return;
        }

        string siteId      = payload[0] as string ?? string.Empty;
        int    ownerId     = (int)payload[1];
        string finalId     = payload[2] as string ?? string.Empty;
        string label       = payload[3] as string ?? string.Empty;

        // If the site already completed locally (we finished construction
        // first), the final building exists at finalId — nothing to do.
        if (!string.IsNullOrEmpty(finalId) && EntityRegistry.Find(finalId) != null)
        {
            Debug.Log($"[ConstructionNet] Complete event for site={siteId} dropped — " +
                      $"final building '{finalId}' already exists locally (idempotent).");
            return;
        }

        GameEntity siteGe = EntityRegistry.Find(siteId);
        if (siteGe == null)
        {
            Debug.LogWarning($"[ConstructionNet] Complete event for site={siteId} — site not " +
                             "in local registry. Either it was never built locally (lost Build " +
                             "command) or already removed. Cannot force-complete.");
            return;
        }

        ConstructionSite site = siteGe.GetComponent<ConstructionSite>();
        if (site == null)
        {
            Debug.LogWarning($"[ConstructionNet] Entity '{siteId}' has no ConstructionSite " +
                             "component — cannot complete.");
            return;
        }

        Debug.Log($"[ConstructionNet] Apply complete site={siteId} final={finalId} " +
                  $"owner={ownerId} type={label}");

        bool prev = IsApplyingNetworkEvent;
        IsApplyingNetworkEvent = true;
        try
        {
            site.ForceCompleteFromNetwork();
        }
        finally
        {
            IsApplyingNetworkEvent = prev;
        }
    }

    private static void HandleWorkerGather(EventData ev)
    {
        if (!(ev.CustomData is object[] payload) || payload.Length < 2)
        {
            Debug.LogWarning("[NetWorker] WorkerGather payload invalid.");
            return;
        }

        string workerId = payload[0] as string ?? string.Empty;
        string nodeId   = payload[1] as string ?? string.Empty;

        GameEntity wGe = EntityRegistry.Find(workerId);
        GameEntity nGe = EntityRegistry.Find(nodeId);
        if (wGe == null || nGe == null)
        {
            Debug.Log($"[NetWorker] Gather dropped — worker='{workerId}' (found={wGe != null}), " +
                      $"node='{nodeId}' (found={nGe != null}).");
            return;
        }

        WorkerGatherer w = wGe.GetComponent<WorkerGatherer>();
        ResourceNode   r = nGe.GetComponent<ResourceNode>();
        if (w == null || r == null) return;

        Debug.Log($"[NetWorker] Apply gather worker={workerId} node={nodeId}");

        bool prev = IsApplyingNetworkEvent;
        IsApplyingNetworkEvent = true;
        try
        {
            w.SetGatherTarget(r);
        }
        finally
        {
            IsApplyingNetworkEvent = prev;
        }
    }

    private static void HandleApplyDamage(EventData ev)
    {
        if (!(ev.CustomData is object[] payload) || payload.Length < 4)
        {
            Debug.LogWarning("[NetDamage] ApplyDamage payload invalid.");
            return;
        }

        string targetId   = payload[0] as string ?? string.Empty;
        string attackerId = payload[1] as string ?? string.Empty;
        float  amount     = (float)payload[2];
        float  newHealth  = (float)payload[3];

        GameEntity target = EntityRegistry.Find(targetId);
        if (target == null)
        {
            Debug.Log($"[NetDamage] target '{targetId}' not in local registry — dropping " +
                      "(unit may have been destroyed locally before the event arrived).");
            return;
        }

        Health h = target.GetComponent<Health>();
        if (h == null) return;

        bool prev = IsApplyingNetworkEvent;
        IsApplyingNetworkEvent = true;
        try
        {
            h.ApplyDamageFromNetwork(amount, newHealth, attackerId);
        }
        finally
        {
            IsApplyingNetworkEvent = prev;
        }
    }

    private static void HandleEntityDestroyed(EventData ev)
    {
        if (!(ev.CustomData is object[] payload) || payload.Length < 2)
        {
            Debug.LogWarning("[NetDeath] EntityDestroyed payload invalid.");
            return;
        }

        string entityId = payload[0] as string ?? string.Empty;
        string killerId = payload[1] as string ?? string.Empty;

        GameEntity ge = EntityRegistry.Find(entityId);
        if (ge == null)
        {
            Debug.Log($"[NetDeath] entity '{entityId}' not in local registry — already " +
                      "destroyed locally, ignoring (idempotent).");
            return;
        }

        Debug.Log($"[NetDeath] Apply destroy entity={entityId} killer={killerId}");

        Health h = ge.GetComponent<Health>();
        bool prev = IsApplyingNetworkEvent;
        IsApplyingNetworkEvent = true;
        try
        {
            if (h != null) h.DestroyFromNetwork();
            else if (ge.gameObject != null) Destroy(ge.gameObject);
        }
        finally
        {
            IsApplyingNetworkEvent = prev;
        }
    }

    private static void HandleResourceChanged(EventData ev)
    {
        if (!(ev.CustomData is object[] payload) || payload.Length < 4)
        {
            Debug.LogWarning("[NetResources] ResourceChanged payload invalid.");
            return;
        }

        int    ownerId   = (int)payload[0];
        int    newAmount = (int)payload[1];
        int    delta     = (int)payload[2];
        string reason    = payload[3] as string ?? string.Empty;

        PlayerResourceManager bank = ResourceBank.For(ownerId);
        if (bank == null || bank.ownerPlayerId != ownerId)
        {
            // ResourceBank.For has a legacy any-bank fallback — we don't
            // want to write Player 1's amount into Player 0's bank, so we
            // re-check ownerPlayerId before applying.
            Debug.LogWarning($"[NetResources] No bank with ownerPlayerId={ownerId} found — " +
                             "dropping ResourceChanged event.");
            return;
        }

        bool prev = IsApplyingNetworkEvent;
        IsApplyingNetworkEvent = true;
        try
        {
            bank.ApplyFromNetwork(newAmount, delta, reason);
        }
        finally
        {
            IsApplyingNetworkEvent = prev;
        }
    }
#endif
}
