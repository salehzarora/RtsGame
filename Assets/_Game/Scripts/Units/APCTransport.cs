using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Infantry transport component for the APC. Stores loaded passengers as
/// inactive GameObjects (preserves Health, ammo, components — no destroy /
/// re-spawn cycle), exposes a slot view for the HUD, heals passengers slowly
/// while inside, and emergency-unloads survivors when the APC is destroyed.
///
/// Loading flow:
///   • Player right-clicks a friendly APC with infantry selected.
///   • <see cref="InfantryBoardingAgent"/> drives each soldier toward the APC.
///   • Within <see cref="enterRange"/>, the agent calls <see cref="LoadUnit"/>.
///   • LoadUnit deactivates the passenger GameObject and records it.
///
/// What this script intentionally does NOT do:
///   • Let passengers fire from inside. Reserved for a future milestone (see
///     <see cref="allowPassengersToFire"/> — wired but unused).
///   • Carry vehicles, aircraft, buildings, Dozers, or Workers. Only
///     <see cref="UnitCategory.Category.Infantry"/> units WITHOUT a
///     <see cref="WorkerGatherer"/> are allowed.
///   • Force a specific formation on unload. Passengers are placed in a
///     simple ring around the APC + NavMesh-snapped.
/// </summary>
[DisallowMultipleComponent]
public class APCTransport : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Capacity")]
    [Tooltip("Maximum number of infantry passengers the APC can hold.")]
    public int capacity = 6;

    [Header("Loading")]
    [Tooltip("World-unit distance at which a boarding infantry counts as " +
             "'arrived' and is loaded into the transport.")]
    public float enterRange = 2.5f;

    [Header("Unloading")]
    [Tooltip("Optional explicit unload spawn points (child Transforms). If empty, " +
             "passengers are placed in a ring around the APC at unloadSpacing.")]
    public Transform[] unloadPoints;

    [Tooltip("Ring radius scale when unloadPoints is empty. Final radius is " +
             "unloadSpacing × 2 so passengers don't all stack on the APC.")]
    public float unloadSpacing = 1.5f;

    [Header("Healing")]
    [Tooltip("If true, passengers heal while inside the APC.")]
    public bool healPassengers = true;

    [Tooltip("HP per second restored to each damaged passenger while inside.")]
    public float passengerHealRate = 5f;

    [Header("Unload Sequence")]
    [Tooltip("If true, Unload All releases passengers one-by-one with a brief " +
             "delay between each instead of dumping them in a single frame.")]
    public bool staggerUnload = true;

    [Tooltip("Seconds between consecutive passengers in a staggered unload " +
             "sequence. ~0.15–0.2 feels like a fast-deployment squad exit.")]
    public float unloadInterval = 0.18f;

    [Tooltip("How far each unloaded passenger walks away from the APC after " +
             "exiting, in world units. Gives the unit a believable 'exited the " +
             "vehicle and ran a few steps' look.")]
    public float unloadMoveDistance = 2.5f;

    [Tooltip("Total spread (degrees) of the deployment cone in front/behind the " +
             "APC. Passengers fan out across this arc so a 6-soldier squad " +
             "doesn't form one tight line.")]
    public float unloadSpreadAngle = 70f;

    [Tooltip("If true, each unloaded passenger is given a short MoveTo order " +
             "to their deploy position. If false, they stand at the exit point.")]
    public bool movePassengerAfterUnload = true;

    [Header("Default Passengers (on spawn)")]
    [Tooltip("If true, the APC instantiates default infantry on Start and loads " +
             "them straight into its passenger list — no visible spawn on the " +
             "ground. Used so newly-produced APCs arrive carrying troops.")]
    public bool spawnWithDefaultPassengers = true;

    [Tooltip("How many default passengers to spawn. Clamped to capacity. " +
             "Overridden by fillToCapacityOnSpawn when that flag is true.")]
    public int defaultPassengerCount = 6;

    [Tooltip("If true, ignores defaultPassengerCount and fills every seat " +
             "(min of capacity and prefab availability).")]
    public bool fillToCapacityOnSpawn = true;

    [Tooltip("Prefab instantiated for each default passenger. Wired by " +
             "Tools → RTS → Vehicles → Create APC Prefab / Repair APC Transport " +
             "to SoldierPrefab.")]
    public GameObject defaultPassengerPrefab;

    [Header("Future-ready (not yet implemented)")]
    [Tooltip("Reserved for a future milestone — passengers firing through the " +
             "APC's hull. Has NO effect today.")]
    public bool allowPassengersToFire = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private readonly List<GameObject> passengers = new List<GameObject>();

    /// <summary>Read-only view for HUD slot rendering.</summary>
    public IReadOnlyList<GameObject> Passengers => passengers;

    public int PassengerCount => passengers.Count;
    public bool HasSpace()     => passengers.Count < capacity;
    public bool IsEmpty        => passengers.Count == 0;

    private Health ownHealth;
    private bool   subscribedToDeath;

    // True after the on-Start default-passenger fill has run. Guards against
    // double-fill on saved-scene loads or duplicated OnEnable cycles.
    private bool defaultPassengersInitialized;

    /// <summary>
    /// Fires whenever the passenger list changes — load, unload, default fill,
    /// emergency unload on death. Listeners (HUD, overhead indicator) refresh
    /// their visuals from this event instead of polling.
    /// </summary>
    public event System.Action OnPassengersChanged;

    /// <summary>True while a staggered Unload All coroutine is running.</summary>
    public bool IsUnloading => isUnloading;

    private bool      isUnloading;
    private Coroutine unloadRoutine;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        ownHealth = GetComponent<Health>();
        if (ownHealth != null)
        {
            ownHealth.OnDeath += HandleAPCDeath;
            subscribedToDeath = true;
        }
    }

    private void Start()
    {
        // Defer the default-passenger fill by one frame so the dispatcher's
        // ApplyOwnership(cmd.playerId) call — which fires in the SAME frame
        // AFTER Instantiate returns — has set our GameEntity.ownerPlayerId
        // to the correct value before we stamp passengers with it. Without
        // this delay, passengers inherited the prefab default (owner 0)
        // and ended up uncontrollable on the wrong client (Bug 3).
        StartCoroutine(InitialiseDefaultPassengersDeferred());
    }

    private IEnumerator InitialiseDefaultPassengersDeferred()
    {
        // One full frame is enough — dispatcher.ExecuteProduce calls
        // ApplyOwnership immediately after Instantiate, all on the same
        // frame, well before WaitForEndOfFrame fires.
        yield return null;
        InitialiseDefaultPassengers();
    }

    private void OnDestroy()
    {
        if (subscribedToDeath && ownHealth != null)
            ownHealth.OnDeath -= HandleAPCDeath;
    }

    private void Update()
    {
        if (!healPassengers || passengers.Count == 0) return;

        float delta = passengerHealRate * Time.deltaTime;
        for (int i = 0; i < passengers.Count; i++)
        {
            GameObject p = passengers[i];
            if (p == null) continue;
            Health h = p.GetComponent<Health>();
            if (h == null) continue;
            if (h.CurrentHealth <= 0f) continue;          // already dead — leave alone
            if (h.CurrentHealth >= h.maxHealth) continue;
            h.Heal(delta);
        }
    }

    // ------------------------------------------------------------------ //
    // Public API — loading
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns true if <paramref name="unit"/> is eligible to enter this APC:
    /// infantry category, not a Worker, not already loaded.
    /// </summary>
    public bool CanLoad(GameObject unit)
    {
        if (unit == null) return false;
        if (passengers.Contains(unit)) return false;

        UnitCategory cat = unit.GetComponent<UnitCategory>();
        if (cat == null || cat.category != UnitCategory.Category.Infantry) return false;

        // Workers carry UnitCategory.Infantry too — exclude them for now
        // (spec defers worker transport to a later milestone).
        if (unit.GetComponent<WorkerGatherer>() != null) return false;

        // Owner-strict check: refuse passengers from another player. Uses
        // GameEntity.ownerPlayerId (canonical, identical on every client)
        // rather than Health.team (local-perspective, differs per client).
        // Falls back to Health.team only when no GameEntity is present
        // (legacy SP scenes where ids may not be stamped yet).
        GameEntity unitEntity = unit.GetComponent<GameEntity>();
        GameEntity apcEntity  = GetComponent<GameEntity>();
        if (unitEntity != null && apcEntity != null)
        {
            if (unitEntity.ownerPlayerId != apcEntity.ownerPlayerId)
            {
                Debug.Log($"[APC] Reject load: passenger owner={unitEntity.ownerPlayerId}, " +
                          $"apc owner={apcEntity.ownerPlayerId}");
                return false;
            }
        }
        else
        {
            Health unitHealth = unit.GetComponent<Health>();
            if (unitHealth != null && ownHealth != null && unitHealth.team != ownHealth.team)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Attempts to load <paramref name="unit"/>. Returns true on success.
    /// Logs the "transport full" case once per attempt. Funnels through
    /// <see cref="ForcePassengerInside"/> so the local + network paths
    /// converge on a single state transition (Phase 10.8).
    /// </summary>
    public bool LoadUnit(GameObject unit)
    {
        if (!CanLoad(unit)) return false;
        if (!HasSpace())
        {
            Debug.Log($"[APC] Transport full ({passengers.Count}/{capacity}).");
            return false;
        }

        ForcePassengerInside(unit, "local");

        // Mirror the load to other clients. Receivers run the same
        // ForcePassengerInside via ApplyLoadFromNetwork.
        GameEntity apcGe = GetComponent<GameEntity>();
        GameEntity paxGe = unit.GetComponent<GameEntity>();
        if (apcGe != null && paxGe != null)
            NetworkMatchEvents.BroadcastPassengerLoaded(apcGe.EntityId, paxGe.EntityId);

        return true;
    }

    /// <summary>
    /// Network-driven load. Applied by <see cref="NetworkMatchEvents"/> when
    /// a PassengerLoaded event arrives from another client. Mirrors the
    /// state changes <see cref="LoadUnit"/> would have made locally:
    /// deactivate the passenger, remove it from selection, append it to
    /// the passenger list. Idempotent — duplicate events are silently
    /// ignored.
    /// </summary>
    public void ApplyLoadFromNetwork(GameObject unit)
    {
        ForcePassengerInside(unit, "network");
    }

    /// <summary>
    /// Authoritatively forces <paramref name="unit"/> into the APC,
    /// regardless of what local state it was in before. Idempotent — calling
    /// twice is safe. Used by both the local <see cref="LoadUnit"/> success
    /// path AND by the network <see cref="ApplyLoadFromNetwork"/> path so
    /// every load funnels through one place.
    ///
    /// Steps:
    ///   1. Defensively remove the passenger from any OTHER APC's list
    ///      (duplicate-entityId guard — prevents the same soldier being
    ///      "loaded" into two transports if a race lets both clients
    ///      decide).
    ///   2. Remove the passenger from <see cref="UnitSelector"/> selection.
    ///   3. Destroy any in-flight <see cref="InfantryBoardingAgent"/>.
    ///   4. Disable gameplay components (Combat, AutoAttack, WorkerGatherer)
    ///      so a hidden passenger can't keep firing / gathering / draining
    ///      banks on the local sim while it's marked inside.
    ///   5. Snap to APC position, then SetActive(false).
    ///   6. Re-apply ownership (color + Health.team perspective).
    ///   7. Add to this APC's passenger list if not already present.
    /// </summary>
    public void ForcePassengerInside(GameObject unit, string source)
    {
        if (unit == null) return;

        // Resolve canonical id once; we use it for logs + duplicate guard.
        GameEntity paxGe = unit.GetComponent<GameEntity>();
        string     paxId = paxGe != null ? paxGe.EntityId : unit.name;

        // 1. Duplicate-APC guard. If this unit was previously stuck in
        //    another APC's passenger list (e.g. an event applied half-way
        //    on this client), evict it from there before we add it here.
        EvictFromOtherTransports(unit);

        // 2. Deselect.
        SelectableUnit su = unit.GetComponent<SelectableUnit>();
        if (su != null)
        {
            UnitSelector selector = UnitSelector.Instance;
            if (selector != null) selector.RemoveFromSelection(su);
            else                  su.Deselect();
        }

        // 3. Cancel any boarding driver — it's done its job.
        InfantryBoardingAgent ba = unit.GetComponent<InfantryBoardingAgent>();
        if (ba != null) Destroy(ba);

        // 4. Disable behaviour components so a still-active passenger can't
        //    keep firing / gathering during the brief moment between this
        //    method and SetActive(false). Some of these auto-restart when
        //    the GameObject is re-activated; we rely on that for unload.
        SetPassengerComponentsEnabled(unit, enabled: false);

        // 5. Snap to APC position and hide.
        unit.transform.position = transform.position;

        // 6. Owner re-stamp (also triggers OwnerColorApplier internally).
        GameEntity apcGe = GetComponent<GameEntity>();
        if (apcGe != null && paxGe != null && apcGe.ownerPlayerId >= 0)
            paxGe.ApplyOwnership(apcGe.ownerPlayerId);

        unit.SetActive(false);

        // 7. Add to passenger list if not already present.
        if (!passengers.Contains(unit))
            passengers.Add(unit);

        Debug.Log($"[APC] Forced passenger inside: passenger={paxId} apc=" +
                  $"{(apcGe != null ? apcGe.EntityId : name)} seat={passengers.IndexOf(unit)} " +
                  $"(source={source})");
        OnPassengersChanged?.Invoke();
    }

    // ------------------------------------------------------------------ //
    // Default-passenger fill (called once from Start)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Instantiates default infantry and loads them straight into the
    /// passenger list — never visible on the ground. Runs once per instance,
    /// gated by <see cref="defaultPassengersInitialized"/>. Refuses to fill
    /// if any passengers are already present (saved-scene case).
    /// </summary>
    private void InitialiseDefaultPassengers()
    {
        if (defaultPassengersInitialized) return;
        defaultPassengersInitialized = true;

        if (!spawnWithDefaultPassengers) return;
        if (passengers.Count > 0) return;

        if (defaultPassengerPrefab == null)
        {
            Debug.LogWarning("[APC] SoldierPrefab missing — spawned empty. " +
                             "Run Tools → RTS → Vehicles → Repair APC Transport.");
            return;
        }

        int wanted = fillToCapacityOnSpawn ? capacity : defaultPassengerCount;
        wanted = Mathf.Clamp(wanted, 0, capacity);

        // Resolve the canonical owner from the APC's GameEntity. By the
        // time this runs (deferred one frame), the dispatcher has applied
        // ownership, so apcEntity.ownerPlayerId is the real owner.
        // Health.team is NOT trusted here — it's local-perspective and
        // differs per client.
        GameEntity apcEntity  = GetComponent<GameEntity>();
        string     apcId      = apcEntity != null ? apcEntity.EntityId      : null;
        int        apcOwnerId = apcEntity != null ? apcEntity.ownerPlayerId : GameEntity.NeutralOwnerId;

        for (int i = 0; i < wanted; i++)
        {
            string passengerId = !string.IsNullOrEmpty(apcId) ? $"{apcId}-pax-{i}" : null;

            // Phase 10.8 — duplicate guard. If a passenger with this exact
            // id already exists in the registry (e.g. this client already
            // ran InitialiseDefaultPassengers, or a snapshot pre-spawned
            // it), reuse it instead of instantiating a second one. Without
            // this guard a re-entrant InitialiseDefaultPassengers (or a
            // future spawn-repair pass) would double the squad.
            GameObject p = null;
            if (!string.IsNullOrEmpty(passengerId))
            {
                GameEntity existing = EntityRegistry.Find(passengerId);
                if (existing != null)
                {
                    p = existing.gameObject;
                    Debug.LogWarning($"[APC] Duplicate passenger prevented id={passengerId} " +
                                     "(reusing existing entity).");
                }
            }

            if (p == null)
            {
                // Push the deterministic id BEFORE Instantiate so the
                // passenger's GameEntity.Awake adopts it.
                GameEntity.SetNextSpawnId(passengerId);
                try
                {
                    p = Instantiate(defaultPassengerPrefab, transform.position, Quaternion.identity);
                }
                finally
                {
                    GameEntity.SetNextSpawnId(null);
                }
            }

            p.SetActive(false);

            // Stamp canonical owner. Goes through GameEntity.ApplyOwnership,
            // which writes ownerPlayerId/teamId, re-keys Health.team to the
            // local client's perspective, and calls
            // OwnerColorApplier.ApplyToEntity to repaint via MultiplayerColors.
            GameEntity pge = p.GetComponent<GameEntity>();
            if (pge != null && apcOwnerId != GameEntity.NeutralOwnerId)
            {
                pge.ApplyOwnership(apcOwnerId);
                string colorTag = ColorTagForOwner(apcOwnerId);
                Debug.Log($"[APC] Init default passenger apc={apcId} seat={i} " +
                          $"passenger={pge.EntityId} owner={apcOwnerId} color={colorTag}");
            }
            else
            {
                Debug.LogWarning($"[APC] Default passenger had no GameEntity / no canonical " +
                                 "APC owner — passenger ownership will be wrong on this client.");
            }

            // Add via the authoritative path so component-enabled state and
            // selection/boarding-agent cleanup is consistent with later
            // re-loads.
            if (!passengers.Contains(p)) passengers.Add(p);
            SetPassengerComponentsEnabled(p, enabled: false);
        }

        Debug.Log($"[APC] Spawned with {passengers.Count} default passengers " +
                  $"(id prefix '{apcId ?? "<none>"}', owner {apcOwnerId}).");
        OnPassengersChanged?.Invoke();
    }

    private static string ColorTagForOwner(int ownerId)
    {
        if (!MultiplayerColors.HasForOwner(ownerId)) return "(unregistered)";
        Color c = MultiplayerColors.ForOwnerOrDefault(ownerId);
        return $"RGB({c.r:F2},{c.g:F2},{c.b:F2})";
    }

    // ------------------------------------------------------------------ //
    // Public API — unloading
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Unload every passenger. When <see cref="staggerUnload"/> is true, runs
    /// a coroutine that releases one passenger per <see cref="unloadInterval"/>
    /// seconds; otherwise drops them all in a single frame.
    /// </summary>
    public void UnloadAll()
    {
        if (passengers.Count == 0)
        {
            Debug.Log("[APC] Unload All requested but transport is empty.");
            return;
        }

        if (isUnloading)
        {
            Debug.Log("[APC] Unload sequence already in progress — ignoring duplicate Unload All.");
            return;
        }

        if (!staggerUnload)
        {
            // Single-frame dump — preserves old behaviour if the user disables
            // the stagger flag in the Inspector.
            int count = passengers.Count;
            for (int i = 0; i < count; i++)
                UnloadInternal(0, spreadIndex: i, applyDeathDamage: false);
            Debug.Log($"[APC] Unloaded all {count} passenger(s) (instant).");
            return;
        }

        unloadRoutine = StartCoroutine(UnloadSequenceCoroutine());
    }

    /// <summary>Unload one specific passenger by slot index.</summary>
    public void UnloadPassengerAtIndex(int index)
    {
        if (index < 0 || index >= passengers.Count)
        {
            Debug.Log($"[APC] UnloadPassengerAtIndex({index}) ignored — out of range " +
                      $"(0..{passengers.Count - 1}).");
            return;
        }

        if (isUnloading)
        {
            Debug.Log("[APC] Slot unload ignored — a sequence is already running.");
            return;
        }

        UnloadInternal(index, spreadIndex: 0, applyDeathDamage: false);
        Debug.Log($"[APC] Unloaded passenger at slot {index}. " +
                  $"Seats: {passengers.Count}/{capacity}");
    }

    /// <summary>
    /// Coroutine driving the staggered unload sequence. Pops index 0 each tick
    /// (the list shrinks behind us) and waits <see cref="unloadInterval"/>
    /// between passengers. Yields out cleanly if the APC is destroyed mid-
    /// sequence — HandleAPCDeath takes over with the emergency-damage path.
    /// </summary>
    private IEnumerator UnloadSequenceCoroutine()
    {
        isUnloading = true;
        int total = passengers.Count;
        Debug.Log($"[APC] Starting unload sequence. Count: {total}");

        int unloaded = 0;
        while (passengers.Count > 0)
        {
            UnloadInternal(0, spreadIndex: unloaded, applyDeathDamage: false);
            unloaded++;
            Debug.Log($"[APC] Unloaded Soldier {unloaded}/{total}");

            if (passengers.Count > 0)
                yield return new WaitForSeconds(unloadInterval);
        }

        Debug.Log("[APC] Unload sequence complete.");
        isUnloading   = false;
        unloadRoutine = null;
    }

    // ------------------------------------------------------------------ //
    // Internal — unload one passenger with full deployment behaviour
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Removes the passenger at <paramref name="passengerIndex"/>, reactivates
    /// it at the resolved exit pose, orders a short walk-away to its deploy
    /// position, and forces an immediate auto-defense scan so it engages any
    /// nearby enemies without the normal scan-interval delay.
    /// <paramref name="spreadIndex"/> controls the deploy cone — pass 0..N-1
    /// across a sequence so soldiers fan out rather than stacking.
    /// </summary>
    private void UnloadInternal(int passengerIndex, int spreadIndex, bool applyDeathDamage)
    {
        GameObject p = passengers[passengerIndex];
        if (p == null)
        {
            passengers.RemoveAt(passengerIndex);
            return;
        }

        ResolveExitPose(spreadIndex, out Vector3 exitPos, out Vector3 forwardDir);

        // Funnel through the authoritative ForcePassengerOutside so the
        // local-unload path and the network apply path produce the same
        // state. ForcePassengerOutside also removes from the passenger
        // list, so we skip the manual RemoveAt above.
        ForcePassengerOutside(p, exitPos, forwardDir, spreadIndex, "local");

        if (applyDeathDamage)
        {
            Health h = p.GetComponent<Health>();
            if (h != null && h.CurrentHealth > 0f)
                h.TakeDamage(h.maxHealth * 0.5f);
        }

        // Broadcast the exact exit pose so the other client unloads the
        // same passenger at the same place.
        GameEntity apcGe = GetComponent<GameEntity>();
        GameEntity paxGe = p.GetComponent<GameEntity>();
        if (apcGe != null && paxGe != null)
            NetworkMatchEvents.BroadcastPassengerUnloaded(
                apcGe.EntityId, paxGe.EntityId, exitPos, forwardDir);
    }

    /// <summary>
    /// Network-driven unload. Applied by <see cref="NetworkMatchEvents"/>
    /// when a PassengerUnloaded event arrives. Locates the passenger by
    /// entity id within this APC's local passenger list, drops it from the
    /// list, and activates it at the exact <paramref name="exitPos"/> /
    /// <paramref name="forwardDir"/> the broadcasting client used.
    /// Idempotent — if the passenger isn't in our local list (we already
    /// unloaded it ourselves, or never had it) the event is silently
    /// dropped.
    /// </summary>
    public void ApplyUnloadFromNetwork(string passengerEntityId, Vector3 exitPos, Vector3 forwardDir)
    {
        if (string.IsNullOrEmpty(passengerEntityId)) return;

        // Look the passenger up by id. We accept any of:
        //   a) Currently in this APC's passenger list (the normal case).
        //   b) Outside in world (we already unloaded it locally, but the
        //      broadcast is reaffirming position).
        //   c) Inside SOME OTHER APC (split-brain race; force-evict).
        GameObject pax = FindPassengerInThisApcById(passengerEntityId);
        if (pax == null)
        {
            // Try the EntityRegistry — covers the "outside or in another
            // transport" cases.
            GameEntity ge = EntityRegistry.Find(passengerEntityId);
            if (ge != null) pax = ge.gameObject;
        }
        if (pax == null)
        {
            Debug.Log($"[NetAPC] Apply unload: passenger '{passengerEntityId}' not in local " +
                      "registry — possibly destroyed or never spawned here, ignoring.");
            return;
        }

        // Spread index is the position in OUR local list (or 0 if it came
        // from elsewhere). It only affects the ResolveExitPose-style spread
        // slot, which the broadcaster also computed locally — exact match
        // isn't critical for correctness, just for the visual fan-out.
        int spreadIdx = passengers.IndexOf(pax);
        if (spreadIdx < 0) spreadIdx = 0;

        ForcePassengerOutside(pax, exitPos, forwardDir, spreadIdx, "network");
    }

    /// <summary>
    /// Authoritatively pushes <paramref name="unit"/> back out into the
    /// world at the given exit pose, regardless of where it was before.
    /// Idempotent. Used by both the local <see cref="UnloadInternal"/> path
    /// AND the network <see cref="ApplyUnloadFromNetwork"/> path.
    /// </summary>
    public void ForcePassengerOutside(GameObject unit, Vector3 exitPos, Vector3 forwardDir,
                                      int spreadIndex, string source)
    {
        if (unit == null) return;

        GameEntity paxGe = unit.GetComponent<GameEntity>();
        string     paxId = paxGe != null ? paxGe.EntityId : unit.name;

        // Remove from this APC's seat AND from any other APC's seat
        // (split-brain guard).
        passengers.Remove(unit);
        EvictFromOtherTransports(unit);

        // Place + orient BEFORE activation.
        unit.transform.position = exitPos;
        if (forwardDir.sqrMagnitude > 0.0001f)
            unit.transform.rotation = Quaternion.LookRotation(forwardDir);

        // Owner re-stamp BEFORE SetActive(true) so the marker OnEnable
        // repaint pulls the correct color.
        GameEntity apcGe = GetComponent<GameEntity>();
        if (apcGe != null && paxGe != null && apcGe.ownerPlayerId >= 0)
        {
            paxGe.ApplyOwnership(apcGe.ownerPlayerId);
            Debug.Log($"[APC] Apply passenger color id={paxId} owner={apcGe.ownerPlayerId}");
        }

        // Re-enable gameplay components before SetActive(true) so the unit
        // is fully operational on the very first frame it's visible.
        SetPassengerComponentsEnabled(unit, enabled: true);

        unit.SetActive(true);

        // Deploy walk-away (mirrors the legacy UnloadInternal behaviour).
        Vector3 deployPos = movePassengerAfterUnload
            ? ComputeDeployPosition(exitPos, forwardDir, spreadIndex)
            : exitPos;
        if (movePassengerAfterUnload)
        {
            UnitMovement um = unit.GetComponent<UnitMovement>();
            if (um != null) um.MoveTo(deployPos);
        }

        GroundAutoAttackController guard = unit.GetComponent<GroundAutoAttackController>();
        if (guard != null) guard.OnUnloadedFromTransport(deployPos);

        Debug.Log($"[APC] Forced passenger outside: passenger={paxId} apc=" +
                  $"{(apcGe != null ? apcGe.EntityId : name)} at {exitPos:F1} (source={source})");
        OnPassengersChanged?.Invoke();
    }

    // ------------------------------------------------------------------ //
    // Internal helpers shared by the Force methods
    // ------------------------------------------------------------------ //

    private GameObject FindPassengerInThisApcById(string entityId)
    {
        for (int i = 0; i < passengers.Count; i++)
        {
            GameObject p = passengers[i];
            if (p == null) continue;
            GameEntity pge = p.GetComponent<GameEntity>();
            if (pge != null && pge.EntityId == entityId) return p;
        }
        return null;
    }

    /// <summary>
    /// Removes <paramref name="unit"/> from the passenger list of every
    /// OTHER APCTransport in the scene. Used at the start of
    /// <see cref="ForcePassengerInside"/> / <see cref="ForcePassengerOutside"/>
    /// to guarantee a passenger can only belong to at most one APC at any
    /// instant — the "split-brain" guard.
    /// </summary>
    private void EvictFromOtherTransports(GameObject unit)
    {
        APCTransport[] all = Object.FindObjectsByType<APCTransport>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            APCTransport other = all[i];
            if (other == null || other == this) continue;
            if (other.passengers.Remove(unit))
            {
                Debug.LogWarning($"[APCValidation] Duplicate passenger id=" +
                                 $"{(unit.GetComponent<GameEntity>()?.EntityId ?? unit.name)} " +
                                 $"-> duplicate removed (was inside '{other.name}', moving to '{name}').");
                other.OnPassengersChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Enable / disable the passenger's "alive in world" component set:
    /// NavMeshAgent, UnitMovement, UnitCombat / RocketCombat /
    /// MissileLauncherCombat, GroundAutoAttackController, SelectableUnit.
    /// Called from <see cref="ForcePassengerInside"/> (disable) and
    /// <see cref="ForcePassengerOutside"/> (enable). Most components also
    /// react correctly to SetActive(false), but explicit toggling makes the
    /// authoritative state crisp and survives one-frame races where
    /// SetActive hasn't propagated yet.
    /// </summary>
    private static void SetPassengerComponentsEnabled(GameObject unit, bool enabled)
    {
        UnityEngine.AI.NavMeshAgent agent = unit.GetComponent<UnityEngine.AI.NavMeshAgent>();
        if (agent != null) agent.enabled = enabled;

        UnitCombat            uc  = unit.GetComponent<UnitCombat>();
        if (uc  != null) uc.enabled = enabled;
        RocketCombat          rc  = unit.GetComponent<RocketCombat>();
        if (rc  != null) rc.enabled = enabled;
        MissileLauncherCombat mlc = unit.GetComponent<MissileLauncherCombat>();
        if (mlc != null) mlc.enabled = enabled;

        GroundAutoAttackController guard = unit.GetComponent<GroundAutoAttackController>();
        if (guard != null) guard.enabled = enabled;

        // SelectableUnit is what UnitSelector uses to add to the selection.
        // Keeping it disabled while inside means even if a drag-select
        // somehow hits the (active) passenger, the selector skips it.
        SelectableUnit su = unit.GetComponent<SelectableUnit>();
        if (su != null) su.enabled = enabled;
    }

    /// <summary>
    /// Picks an exit position + forward direction for the unloading passenger.
    /// Prefers <see cref="unloadPoints"/> if assigned; otherwise alternates
    /// behind-left / behind-right of the APC based on <paramref name="spreadIndex"/>.
    /// Always NavMesh-snaps the result.
    /// </summary>
    private void ResolveExitPose(int spreadIndex, out Vector3 position, out Vector3 forwardDir)
    {
        // Explicit unload points win — fewer surprises for hand-placed prefabs.
        if (unloadPoints != null && unloadPoints.Length > 0)
        {
            Transform slot = unloadPoints[spreadIndex % unloadPoints.Length];
            if (slot != null)
            {
                Vector3 raw = slot.position;
                position    = NavMesh.SamplePosition(raw, out NavMeshHit hit1, 3f, NavMesh.AllAreas)
                              ? hit1.position : raw;
                forwardDir  = slot.forward;
                return;
            }
        }

        // Fallback: alternate behind-left / behind-right of the APC so two
        // adjacent soldiers don't spawn on top of each other.
        Vector3 apcForward = transform.forward;
        Vector3 apcRight   = transform.right;
        float   side       = (spreadIndex % 2 == 0) ? -1f : 1f;

        Vector3 desired = transform.position
                        + apcForward * (-0.5f)                       // step back a bit
                        + apcRight   * (side * unloadSpacing);       // alternate sides

        position = NavMesh.SamplePosition(desired, out NavMeshHit hit2, 3f, NavMesh.AllAreas)
                   ? hit2.position : desired;

        forwardDir = apcForward;     // face away from the APC
    }

    /// <summary>
    /// Computes the final deploy position the soldier walks toward after
    /// exiting. Fans soldiers across a <see cref="unloadSpreadAngle"/> cone
    /// centred on the APC's forward direction so a squad disperses naturally.
    /// </summary>
    private Vector3 ComputeDeployPosition(Vector3 exitPos, Vector3 forwardDir, int spreadIndex)
    {
        // Even spread across the cone: index 0 → far left, index (capacity-1) → far right.
        float half = (capacity - 1) * 0.5f;
        float t    = (spreadIndex - half) / Mathf.Max(0.5f, half);
        float angleDeg = t * (unloadSpreadAngle * 0.5f);

        Vector3 dir = Quaternion.Euler(0f, angleDeg, 0f) * forwardDir;
        Vector3 target = exitPos + dir.normalized * unloadMoveDistance;

        if (NavMesh.SamplePosition(target, out NavMeshHit hit, 3f, NavMesh.AllAreas))
            return hit.position;

        Debug.LogWarning($"[APC] Deploy position {target:F1} is off the NavMesh — " +
                         "soldier will idle at the exit point.");
        return exitPos;
    }

    // ------------------------------------------------------------------ //
    // APC death — emergency unload + damage burst
    // ------------------------------------------------------------------ //

    private void HandleAPCDeath()
    {
        if (passengers.Count == 0) return;

        int count = passengers.Count;
        // Walk the list back to front so removing each element doesn't shift
        // later indices. Pass each spread slot so survivors fan out across
        // the wreck instead of stacking on a single side.
        for (int i = count - 1; i >= 0; i--)
            UnloadInternal(0, spreadIndex: count - 1 - i, applyDeathDamage: true);

        Debug.Log($"[APC] Destroyed — passengers emergency unloaded ({count} survivors, " +
                  "each took 50% max-HP damage).");
    }

    // ------------------------------------------------------------------ //
    // Helpers — label resolution for HUD + logs
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Short label for HUD slots / console logs. Prefers SelectableUnit's
    /// implicit name; falls back to "Soldier" / "RPG" by component signature.
    /// </summary>
    public string ResolvePassengerLabel(GameObject unit)
    {
        if (unit == null) return "<empty>";
        if (unit.GetComponent<RocketCombat>() != null) return "RPG";
        if (unit.GetComponent<UnitCombat>()   != null) return "Soldier";
        return unit.name;
    }
}
