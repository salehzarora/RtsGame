using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Phase 10.7 — desync safety net. Periodically the MasterClient walks
/// every important <see cref="GameEntity"/> in the local scene and emits
/// a small snapshot (owner / active state / current health) for each.
/// Receivers reconcile their local copy to the master's value — fixing
/// the long-term drift the command-replay layer can't catch (boarding
/// state, color-on-reactivation, dead-on-one-client-alive-on-other).
///
/// Design intent — what this does:
///   • Every <see cref="snapshotInterval"/> seconds (default 0.5s), the
///     master client iterates entities of type Unit / Building / Aircraft
///     in <see cref="EntityRegistry"/>.
///   • For each, it sends an EntityStateSnapshot event with:
///         entityId, ownerPlayerId, isActive (GameObject.activeSelf),
///         currentHealth.
///   • Non-master clients <see cref="ApplyEntityStateSnapshot"/> the
///     incoming values. Mismatches get reconciled; matches are no-ops.
///
/// Design intent — what this does NOT do (deliberately):
///   • Sync POSITION/ROTATION. Those are very chatty and constant
///     correction creates rubber-banding. Command-replay keeps both
///     clients running the same MoveTo paths; small drift is acceptable.
///   • Spawn missing entities. If an entity is in the master's registry
///     but not the client's, we log a warning but don't fabricate one —
///     spawn divergence is a deeper bug (command path missed) and
///     fabricating a unit at half its real position would feel worse.
///   • Touch resource nodes / projectiles. Resources are scene-baked and
///     synced via deposit broadcasts; projectiles are pure visuals.
///
/// Setup: attach to the same GameObject as <see cref="NetworkManagerRTS"/>
/// (the editor tool Tools → RTS → Multiplayer → Setup Network Manager
/// does this automatically). The component is dormant outside MP play.
/// </summary>
[DisallowMultipleComponent]
public class NetworkEntityStateSync : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Tooltip("Seconds between snapshot ticks. Faster = more network traffic " +
             "and tighter correction; slower = more drift but cheaper.")]
    [Range(0.1f, 2f)] public float snapshotInterval = 0.5f;

    [Tooltip("How many entities to send per tick (chunk size). Limits a single " +
             "Photon event's payload size. Lower for huge unit counts.")]
    [Range(8, 128)] public int maxEntitiesPerTick = 64;

    [Tooltip("Seconds between UNIT TRANSFORM broadcasts. Faster = smoother " +
             "remote movement at the cost of more bandwidth. 0.1s (10 Hz) is the " +
             "Phase 10.15 default — at 5 Hz the rotation looked choppy because " +
             "samples were 200ms apart and the owner could turn 90°+ between " +
             "them. 10 Hz halves the per-sample delta and combines with the " +
             "raised RotateTowards speed in UnitMovement to produce smooth " +
             "remote turning.")]
    [Range(0.05f, 1f)] public float transformInterval = 0.1f;

    // ------------------------------------------------------------------ //
    // Static singleton — receive-side dispatch in NetworkMatchEvents
    // routes through ApplyEntityStateSnapshot which is static for
    // call-site simplicity.
    // ------------------------------------------------------------------ //

    public static NetworkEntityStateSync Instance { get; private set; }

    private Coroutine senderRoutine;
    private Coroutine transformRoutine;
    private int       cursor;     // round-robin starting point across ticks

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[NetSnap] Duplicate NetworkEntityStateSync destroyed.");
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void OnEnable()
    {
        senderRoutine    = StartCoroutine(SenderLoop());
        transformRoutine = StartCoroutine(TransformLoop());
    }

    private void OnDisable()
    {
        if (senderRoutine    != null) StopCoroutine(senderRoutine);
        if (transformRoutine != null) StopCoroutine(transformRoutine);
        senderRoutine    = null;
        transformRoutine = null;
    }

    // ================================================================== //
    // Owner-authoritative transform broadcast — runs on EVERY client at
    // ~5 Hz. For each entity this client owns AND that has a UnitMovement
    // (so we know it's a mobile ground unit), emit a UnitTransform event.
    // The receive path on the other client lerps its local copy. NavMesh
    // simulation runs only on the owner; non-owner clients have agents
    // disabled (see UnitMovement.RefreshOwnershipGate).
    // ================================================================== //

    private System.Collections.IEnumerator TransformLoop()
    {
        while (true)
        {
            float wait = Mathf.Max(0.05f, transformInterval);
            yield return new WaitForSeconds(wait);

            if (!NetworkManagerRTS.IsMultiplayerEnabled) continue;
            int localId = NetworkManagerRTS.LocalPlayerId;
            if (localId < 0) continue;     // before MatchStart

            foreach (GameEntity ge in EntityRegistry.All())
            {
                if (ge == null) continue;
                if (ge.ownerPlayerId != localId) continue;     // only broadcast OUR units
                if (!ge.gameObject.activeInHierarchy) continue; // hidden APC passengers skip

                // Mobile entity types only. Ground units (Unit) carry
                // UnitMovement; aircraft (Aircraft) carry AirUnitController and
                // move by direct transform manipulation — both are broadcast so
                // non-owner clients can interpolate them. Buildings / Projectiles
                // / Resources have no remote movement to sync and are skipped.
                bool isGroundUnit = ge.entityType == EntityType.Unit
                                 && ge.GetComponent<UnitMovement>() != null;
                bool isAircraft   = ge.entityType == EntityType.Aircraft
                                 && ge.GetComponent<AirUnitController>() != null;
                if (!isGroundUnit && !isAircraft) continue;

                NetworkMatchEvents.BroadcastUnitTransform(
                    ge.EntityId,
                    ge.transform.position,
                    ge.transform.rotation,
                    Time.timeAsDouble);
            }
        }
    }

    // ================================================================== //
    // Sender — runs forever on the master client. Iterates a chunk of
    // EntityRegistry per tick and emits one snapshot event per entity.
    // ================================================================== //

    private IEnumerator SenderLoop()
    {
        while (true)
        {
            float wait = Mathf.Max(0.1f, snapshotInterval);
            yield return new WaitForSeconds(wait);

            if (!NetworkManagerRTS.IsMultiplayerEnabled) continue;
#if PHOTON_UNITY_NETWORKING
            if (!Photon.Pun.PhotonNetwork.IsMasterClient) continue;
#else
            continue;
#endif

            EmitSnapshotChunk();
        }
    }

    private void EmitSnapshotChunk()
    {
        // Materialise the registry once per tick — EntityRegistry exposes
        // an iterator-friendly snapshot via All(). Round-robin via cursor
        // so a large entity count still gets covered evenly across ticks.
        var all = new List<GameEntity>();
        foreach (GameEntity ge in EntityRegistry.All())
            if (ge != null) all.Add(ge);

        if (all.Count == 0) return;

        int sent = 0;
        int n = all.Count;
        for (int step = 0; step < n && sent < maxEntitiesPerTick; step++)
        {
            int idx = (cursor + step) % n;
            GameEntity ge = all[idx];
            if (ge == null) continue;
            if (!ShouldSyncEntity(ge)) continue;

            Health h = ge.GetComponent<Health>();
            float hp = h != null ? h.CurrentHealth : -1f;
            bool active = ge.gameObject.activeInHierarchy;

            NetworkMatchEvents.BroadcastEntityStateSnapshot(
                ge.EntityId, ge.ownerPlayerId, active, hp);
            sent++;
        }
        cursor = (cursor + sent) % Mathf.Max(1, n);
    }

    private static bool ShouldSyncEntity(GameEntity ge)
    {
        // Skip resource nodes (neutral, scene-baked, no per-frame state)
        // and projectiles (pure visual transient).
        if (ge.entityType == EntityType.Resource)   return false;
        if (ge.entityType == EntityType.Projectile) return false;
        return true;
    }

    // ================================================================== //
    // Receive-side apply
    // ================================================================== //

    /// <summary>
    /// Called by <see cref="NetworkMatchEvents.HandleEntityStateSnapshot"/>
    /// with the master-broadcast canonical values. Reconciles owner,
    /// active state, and health on the local copy. Position is NOT
    /// touched — command replay is responsible for that.
    /// </summary>
    public static void ApplyEntityStateSnapshot(string entityId, int ownerPlayerId,
                                                bool isActive, float currentHealth)
    {
        if (string.IsNullOrEmpty(entityId)) return;

        GameEntity ge = EntityRegistry.Find(entityId);
        if (ge == null)
        {
            // Don't fabricate — a missing entity is a deeper bug. Log so
            // future diagnostics can catch it.
            Debug.LogWarning($"[NetSnap] Snapshot for entity '{entityId}' (owner {ownerPlayerId}) " +
                             "but it's not in local EntityRegistry. Possible missed spawn.");
            return;
        }

        // --- Owner correction ---------------------------------------- //
        if (ge.ownerPlayerId != ownerPlayerId && ownerPlayerId != GameEntity.NeutralOwnerId)
        {
            Debug.Log($"[NetSnap] Correct owner entity={entityId} {ge.ownerPlayerId} -> {ownerPlayerId}");
            ge.ApplyOwnership(ownerPlayerId);     // also repaints color via OwnerColorApplier
        }

        // --- Color top-up -------------------------------------------- //
        // Even when ownerPlayerId already matched, a TeamColorMarker that
        // initialised before MultiplayerColors was populated can still be
        // wearing the prefab default. Cheap to re-apply each snapshot.
        OwnerColorApplier.ApplyToEntity(ge.gameObject);

        // --- Active state correction --------------------------------- //
        // We only reconcile when the divergence is the bad direction:
        //   master says active, we have it inactive → activate.
        //   master says inactive, we have it active AND it isn't a
        //     transport-passenger (transport state has its own event
        //     path) → leave alone, because the local APCTransport may
        //     have it in a "pending hide" pose that the snapshot lost a
        //     race with. The transport-load broadcast already handled it.
        if (isActive && !ge.gameObject.activeSelf)
        {
            ge.gameObject.SetActive(true);
            Debug.Log($"[NetSnap] Correct active entity={entityId} false -> true");
        }

        // --- Health correction --------------------------------------- //
        if (currentHealth >= 0f)
        {
            Health h = ge.GetComponent<Health>();
            if (h != null && !Mathf.Approximately(h.CurrentHealth, currentHealth))
            {
                // Reuse the network-apply path so the dying flag /
                // OnDeath chain is consistent with normal damage events.
                h.ApplyDamageFromNetwork(0f, currentHealth);
            }
        }
    }
}
