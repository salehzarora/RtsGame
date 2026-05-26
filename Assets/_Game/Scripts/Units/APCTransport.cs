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
        // Runs once per instance — Start respects [DisallowMultipleComponent]
        // and Unity guarantees single-fire. The internal flag guards against
        // re-fills if something disables/re-enables this component.
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

        // Friendly-team check: refuse passengers from the opposing team.
        Health unitHealth = unit.GetComponent<Health>();
        if (unitHealth != null && ownHealth != null && unitHealth.team != ownHealth.team)
            return false;

        return true;
    }

    /// <summary>
    /// Attempts to load <paramref name="unit"/>. Returns true on success.
    /// Logs the "transport full" case once per attempt.
    /// </summary>
    public bool LoadUnit(GameObject unit)
    {
        if (!CanLoad(unit)) return false;
        if (!HasSpace())
        {
            Debug.Log($"[APC] Transport full ({passengers.Count}/{capacity}).");
            return false;
        }

        // Remove from selection so the player can't issue commands to a
        // unit that no longer exists on the battlefield.
        SelectableUnit su = unit.GetComponent<SelectableUnit>();
        if (su != null)
        {
            UnitSelector selector = UnitSelector.Instance;
            if (selector != null)
                selector.RemoveFromSelection(su);
            else
                su.Deselect();
        }

        // Cancel any boarding agent now that the unit is inside — don't want
        // it to immediately try to re-board on a future unload.
        InfantryBoardingAgent ba = unit.GetComponent<InfantryBoardingAgent>();
        if (ba != null) Destroy(ba);

        passengers.Add(unit);
        unit.SetActive(false);

        string label = ResolvePassengerLabel(unit);
        Debug.Log($"[APC] Loaded {label}. Seats: {passengers.Count}/{capacity}");
        OnPassengersChanged?.Invoke();
        return true;
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

        Health.Team team = ownHealth != null ? ownHealth.team : Health.Team.Player;

        // Derive a deterministic id prefix from the APC's own entity id so
        // the spawned default passengers get the same ids on every client.
        // Falls back to empty (random GUID per client) when the APC has no
        // GameEntity yet — single-player still works either way.
        GameEntity apcEntity = GetComponent<GameEntity>();
        string apcId = apcEntity != null ? apcEntity.EntityId : null;

        for (int i = 0; i < wanted; i++)
        {
            // Push a per-passenger deterministic id BEFORE Instantiate so the
            // passenger's GameEntity.Awake adopts it.
            string passengerId = !string.IsNullOrEmpty(apcId) ? $"{apcId}-pax-{i}" : null;
            GameEntity.SetNextSpawnId(passengerId);
            GameObject p;
            try
            {
                // Instantiate at the APC's position then immediately deactivate —
                // Unity processes Awake/OnEnable synchronously, so the GameObject
                // never renders a visible frame on the ground.
                p = Instantiate(defaultPassengerPrefab, transform.position, Quaternion.identity);
            }
            finally
            {
                GameEntity.SetNextSpawnId(null);
            }
            p.SetActive(false);

            // Match the APC's team (player APC → player soldier passengers).
            Health h = p.GetComponent<Health>();
            if (h != null) h.team = team;

            passengers.Add(p);
        }

        Debug.Log($"[APC] Spawned with {passengers.Count} default passengers " +
                  $"(id prefix '{apcId ?? "<none>"}').");
        OnPassengersChanged?.Invoke();
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
        passengers.RemoveAt(passengerIndex);
        if (p == null) return;

        // 1. Resolve exit pose (position + forward direction).
        ResolveExitPose(spreadIndex, out Vector3 exitPos, out Vector3 forwardDir);

        // 2. Place + orient the passenger BEFORE activation. SetActive(true)
        //    fires OnEnable, which re-places the NavMeshAgent on the mesh.
        p.transform.position = exitPos;
        if (forwardDir.sqrMagnitude > 0.0001f)
            p.transform.rotation = Quaternion.LookRotation(forwardDir);

        p.SetActive(true);

        if (applyDeathDamage)
        {
            Health h = p.GetComponent<Health>();
            if (h != null && h.CurrentHealth > 0f)
                h.TakeDamage(h.maxHealth * 0.5f);
        }

        // 3. Compute the deploy position the soldier walks to after exit.
        Vector3 deployPos = movePassengerAfterUnload
            ? ComputeDeployPosition(exitPos, forwardDir, spreadIndex)
            : exitPos;

        // 4. Order the short move-away. UnitMovement.MoveTo handles a freshly-
        //    re-enabled NavMeshAgent fine because OnEnable already snapped it
        //    onto the mesh at exitPos.
        if (movePassengerAfterUnload)
        {
            UnitMovement um = p.GetComponent<UnitMovement>();
            if (um != null) um.MoveTo(deployPos);
        }

        // 5. Reset the auto-defense guard to the deploy position + force an
        //    immediate scan. No suppression window — the soldier engages
        //    enemies in its detection radius right away.
        GroundAutoAttackController guard = p.GetComponent<GroundAutoAttackController>();
        if (guard != null) guard.OnUnloadedFromTransport(deployPos);

        OnPassengersChanged?.Invoke();
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
