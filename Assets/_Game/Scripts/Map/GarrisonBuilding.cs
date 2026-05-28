using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// PHASE D — enterable / garrison building. Infantry can occupy it; while
/// inside they are hidden, protected, and (optionally) the building fires at
/// nearby enemies with strength scaling on occupant count. A neutral garrison
/// is CLAIMED by the first occupant's owner and released when it empties.
///
/// Mirrors the proven APC transport occupancy model:
///   • A LOCAL-apply method (<see cref="EnterUnitLocal"/> / <see cref="ExitUnitLocal"/>)
///     does the hide/show + bookkeeping with NO networking.
///   • The command path (owner client) calls the local apply AND broadcasts a
///     GarrisonEnter / GarrisonExit event so every other client mirrors it.
///   • The receive path (<see cref="MapInteractableNetworkEvents"/>) calls ONLY
///     the local apply, so there's no echo and occupants never double-process.
///
/// Authority:
///   • Enter/exit is driven by the COMMANDING player's client (any-client gate)
///     — only the owner of the units can order them in/out.
///   • Fire-from-building damage and destroy-time occupant casualties are
///     applied on the AUTHORITATIVE side only (<see cref="MapInteractable.Authoritative"/>);
///     the resulting health/death replicates through the existing events.
///
/// Vehicles/tanks are rejected unless <see cref="allowVehicles"/> is set.
/// Garrison enter is INSTANT on command (no walk-in animation yet — a
/// documented future polish). Occupant ids are stored so the occupancy is
/// reconstructable; full late-join occupant restore is a TODO.
/// </summary>
public class GarrisonBuilding : MapInteractable
{
    // ------------------------------------------------------------------ //
    // Inspector — Capacity / eligibility
    // ------------------------------------------------------------------ //

    [Header("Garrison — Capacity")]
    [Tooltip("Maximum number of units that can occupy this building.")]
    public int capacity = 5;

    [Tooltip("If true, only infantry (UnitCategory.Infantry) may enter. Workers " +
             "count as infantry.")]
    public bool infantryOnly = true;

    [Tooltip("If true, vehicles may also enter (overrides Infantry Only for " +
             "vehicles). Aircraft can never garrison.")]
    public bool allowVehicles = false;

    [Tooltip("How close (world units) a unit must be to the building to enter. " +
             "The router moves far units toward the building first.")]
    public float enterRadius = 6f;

    [Tooltip("Where occupants are placed when they exit. If empty, they spawn " +
             "in a ring around the building.")]
    public Transform[] exitPoints;

    // ------------------------------------------------------------------ //
    // Inspector — Combat (fire from building)
    // ------------------------------------------------------------------ //

    [Header("Garrison — Fire From Building")]
    [Tooltip("If true and occupied, the building fires at nearby enemies of the " +
             "occupying player.")]
    public bool canFireFromBuilding = true;

    [Tooltip("Engagement range while garrisoned. Usually longer than infantry on " +
             "open ground — the height/cover bonus.")]
    public float fireRange = 20f;

    [Tooltip("Damage per shot contributed by EACH occupant. Total per shot = " +
             "damagePerOccupant × occupant count.")]
    public float damagePerOccupant = 5f;

    [Tooltip("Seconds between shots.")]
    public float fireCooldown = 0.6f;

    [Tooltip("Damage type for the garrison's fire (drives the DamageRules modifier).")]
    public DamageType fireDamageType = DamageType.Bullet;

    [Tooltip("Muzzle origin for the tracer. If null, the building top is used.")]
    public Transform firePoint;

    // ------------------------------------------------------------------ //
    // Inspector — Protection / destruction
    // ------------------------------------------------------------------ //

    [Header("Garrison — Protection / Destruction")]
    [Tooltip("Fraction of damage occupants take when EJECTED by a building " +
             "collapse (0 = unharmed, 1 = full eject damage). While safely " +
             "inside they take no damage at all.")]
    [Range(0f, 1f)] public float occupantProtectionMultiplier = 0.5f;

    [Tooltip("Base damage applied to occupants ejected by a building collapse, " +
             "before occupantProtectionMultiplier.")]
    public float ejectDamage = 60f;

    [Tooltip("On building destruction, eject occupants (with eject damage). " +
             "Ignored if Kill Occupants On Destroy is set.")]
    public bool ejectOnDestroy = true;

    [Tooltip("On building destruction, kill all occupants outright. Takes " +
             "precedence over Eject On Destroy.")]
    public bool killOccupantsOnDestroy = false;

    // ------------------------------------------------------------------ //
    // Runtime occupancy
    // ------------------------------------------------------------------ //

    private readonly List<GameObject> occupants   = new List<GameObject>();
    private readonly List<string>     occupantIds = new List<string>();

    /// <summary>Owner id that currently controls this garrison (the first
    /// occupant's owner). Neutral when empty.</summary>
    public int OccupyingOwnerId { get; private set; } = GameEntity.NeutralOwnerId;

    public int  OccupantCount => occupants.Count;
    public bool HasSpace      => occupants.Count < Mathf.Max(0, capacity);
    public bool IsOccupied    => occupants.Count > 0;

    private float fireTimer;
    private LineRenderer tracer;
    private static readonly Collider[] s_scan = new Collider[64];

    private DestructibleMapObject destructible;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    protected override void Awake()
    {
        base.Awake();

        destructible = GetComponent<DestructibleMapObject>();
        if (destructible != null) destructible.OnDestroyed += HandleBuildingDestroyed;
    }

    protected virtual void OnDestroy()
    {
        if (destructible != null) destructible.OnDestroyed -= HandleBuildingDestroyed;
    }

    private void Update()
    {
        if (!canFireFromBuilding || !IsOccupied) return;
        TickFire();
    }

    // ------------------------------------------------------------------ //
    // Eligibility
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Whether <paramref name="unit"/> may enter right now: correct unit class,
    /// space available, and (if occupied) owned by the same player who already
    /// holds the building.
    /// </summary>
    public bool CanEnter(GameObject unit)
    {
        if (unit == null || !HasSpace) return false;
        if (destructible != null && destructible.IsDestroyed) return false;

        // Class check.
        bool infantry = IsInfantry(unit);
        bool vehicle  = !infantry && !IsAircraft(unit);
        if (!infantry && !(allowVehicles && vehicle)) return false;
        if (infantryOnly && !infantry && !allowVehicles) return false;

        // Ownership: while occupied, only the holding player may add more.
        int unitOwner = OwnerOf(unit);
        if (IsOccupied && OccupyingOwnerId != GameEntity.NeutralOwnerId && unitOwner != OccupyingOwnerId)
            return false;

        return true;
    }

    private static bool IsAircraft(GameObject go)
    {
        UnitCategory uc = go.GetComponent<UnitCategory>() ?? go.GetComponentInParent<UnitCategory>();
        return uc != null && uc.category == UnitCategory.Category.Aircraft;
    }

    // ------------------------------------------------------------------ //
    // Enter — command path (owner) vs network apply
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Owner-side entry: hides the unit locally AND broadcasts so other clients
    /// mirror it. Returns true if the unit entered.
    /// </summary>
    public bool TryCommandEnter(GameObject unit)
    {
        if (!CanEnter(unit)) return false;

        int unitOwner = OwnerOf(unit);
        EnterUnitLocal(unit, unitOwner);

        GameEntity uge = unit.GetComponent<GameEntity>();
        if (uge != null)
            MapInteractableNetworkEvents.BroadcastGarrisonEnter(EntityId, uge.EntityId, unitOwner);

        return true;
    }

    /// <summary>Receive-side entry (no re-broadcast).</summary>
    public void ApplyEnterFromNetwork(GameObject unit, int occupyingOwnerId)
    {
        if (unit == null) return;
        if (occupants.Contains(unit)) return;
        EnterUnitLocal(unit, occupyingOwnerId);
    }

    private void EnterUnitLocal(GameObject unit, int unitOwner)
    {
        if (occupants.Contains(unit)) return;

        if (!IsOccupied) OccupyingOwnerId = unitOwner;

        GameEntity uge = unit.GetComponent<GameEntity>();
        occupants.Add(unit);
        occupantIds.Add(uge != null ? uge.EntityId : string.Empty);

        // Drop it from the local selection so the player can't keep commanding
        // a unit that's now inside.
        SelectableUnit su = unit.GetComponent<SelectableUnit>();
        if (su != null && UnitSelector.Instance != null) UnitSelector.Instance.RemoveFromSelection(su);

        // Hide / disable — deactivating stops movement, combat, auto-attack,
        // and removes it from physics scans (so it's untargetable inside).
        unit.SetActive(false);

        Debug.Log($"[Garrison] '{name}' +1 occupant ({OccupantCount}/{capacity}), " +
                  $"owner={OccupyingOwnerId}, unit='{unit.name}'.");

        OnOccupancyChanged();
    }

    /// <summary>
    /// Called after any change to the occupant list / occupying owner. Override
    /// in subclasses (e.g. <see cref="WatchTower"/>) to update capture
    /// indicators. Base implementation is empty.
    /// </summary>
    protected virtual void OnOccupancyChanged() { }

    // ------------------------------------------------------------------ //
    // Exit
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Owner-side: eject every occupant and broadcast each exit. Used by the
    /// right-click router when the holding player re-clicks the building.
    /// </summary>
    public void CommandExitAll()
    {
        if (!IsOccupied) return;

        // Snapshot — ExitUnitLocal mutates the list.
        var snapshot = new List<GameObject>(occupants);
        for (int i = 0; i < snapshot.Count; i++)
        {
            GameObject u = snapshot[i];
            if (u == null) continue;

            Vector3 exitPos     = GetExitPosition(i);
            Vector3 forward     = (exitPos - transform.position).normalized;
            ExitUnitLocal(u, exitPos, forward);

            GameEntity uge = u.GetComponent<GameEntity>();
            if (uge != null)
                MapInteractableNetworkEvents.BroadcastGarrisonExit(EntityId, uge.EntityId, exitPos, forward);
        }
    }

    /// <summary>Receive-side exit (no re-broadcast).</summary>
    public void ApplyExitFromNetwork(string unitId, Vector3 exitPos, Vector3 forward)
    {
        GameObject u = FindOccupant(unitId);
        if (u == null)
        {
            // Not in our local occupant list — the unit may exist but we never
            // recorded it (missed enter). Try the registry as a fallback.
            GameEntity ge = EntityRegistry.Find(unitId);
            u = ge != null ? ge.gameObject : null;
            if (u == null) return;
        }
        ExitUnitLocal(u, exitPos, forward);
    }

    private void ExitUnitLocal(GameObject unit, Vector3 exitPos, Vector3 forward)
    {
        int idx = occupants.IndexOf(unit);
        if (idx >= 0)
        {
            occupants.RemoveAt(idx);
            if (idx < occupantIds.Count) occupantIds.RemoveAt(idx);
        }

        if (unit != null)
        {
            unit.transform.position = exitPos;
            if (forward.sqrMagnitude > 0.0001f)
                unit.transform.rotation = Quaternion.LookRotation(forward);

            unit.SetActive(true);

            // Snap a NavMeshAgent onto the mesh at the exit so it doesn't warp.
            NavMeshAgent agent = unit.GetComponent<NavMeshAgent>();
            if (agent != null && agent.enabled && agent.isOnNavMesh) agent.Warp(exitPos);

            // Let the auto-defense re-anchor at the deploy point if present.
            GroundAutoAttackController aa = unit.GetComponent<GroundAutoAttackController>();
            if (aa != null) aa.OnUnloadedFromTransport(exitPos);
        }

        if (!IsOccupied) OccupyingOwnerId = GameEntity.NeutralOwnerId;

        Debug.Log($"[Garrison] '{name}' -1 occupant ({OccupantCount}/{capacity}), " +
                  $"unit='{(unit != null ? unit.name : "?")}'.");

        OnOccupancyChanged();
    }

    private GameObject FindOccupant(string unitId)
    {
        for (int i = 0; i < occupantIds.Count; i++)
            if (occupantIds[i] == unitId) return occupants[i];
        return null;
    }

    private Vector3 GetExitPosition(int index)
    {
        if (exitPoints != null && exitPoints.Length > 0)
        {
            Transform t = exitPoints[index % exitPoints.Length];
            if (t != null) return t.position;
        }
        // Fallback ring around the building.
        float angle = index * Mathf.PI * 2f / Mathf.Max(1, OccupantCount);
        Vector3 ring = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (enterRadius + 1.5f);
        return transform.position + ring;
    }

    // ------------------------------------------------------------------ //
    // Fire from building (authoritative damage, local tracer)
    // ------------------------------------------------------------------ //

    private void TickFire()
    {
        fireTimer -= Time.deltaTime;
        if (fireTimer > 0f) return;
        fireTimer = fireCooldown;

        Health target = FindNearestEnemy();
        if (target == null) return;

        // Tracer is cosmetic — show it on every client that can see a target.
        ShowTracer(target.transform.position);

        // Damage is applied once on the authoritative side; Health then
        // broadcasts the resulting value so remotes snap.
        if (!Authoritative) return;

        float baseDamage = damagePerOccupant * OccupantCount;
        UnitCategory.Category cat = DamageRules.Resolve(target.gameObject);
        float dmg = baseDamage * DamageRules.Modifier(fireDamageType, cat);
        target.TakeDamage(dmg);
    }

    private Health FindNearestEnemy()
    {
        int n = Physics.OverlapSphereNonAlloc(transform.position, fireRange, s_scan);
        Health best = null;
        float bestSqr = float.PositiveInfinity;

        for (int i = 0; i < n; i++)
        {
            Collider c = s_scan[i];
            if (c == null) continue;

            Health h = c.GetComponent<Health>() ?? c.GetComponentInParent<Health>();
            if (h == null) continue;

            int targetOwner = OwnerOf(h.gameObject);
            if (targetOwner == OccupyingOwnerId || targetOwner == GameEntity.NeutralOwnerId) continue;

            // Don't shoot aircraft (garrison rifles are ground-only).
            if (IsAircraft(h.gameObject)) continue;

            float sqr = (h.transform.position - transform.position).sqrMagnitude;
            if (sqr < bestSqr) { bestSqr = sqr; best = h; }
        }
        return best;
    }

    private void ShowTracer(Vector3 targetPos)
    {
        if (tracer == null) BuildTracer();
        if (tracer == null) return;

        Vector3 start = firePoint != null ? firePoint.position : transform.position + Vector3.up * 2.5f;
        Vector3 end   = targetPos + Vector3.up * 1f;
        tracer.SetPosition(0, start);
        tracer.SetPosition(1, end);
        tracer.enabled = true;
        CancelInvoke(nameof(HideTracer));
        Invoke(nameof(HideTracer), 0.06f);
    }

    private void HideTracer() { if (tracer != null) tracer.enabled = false; }

    private void BuildTracer()
    {
        GameObject tg = new GameObject("GarrisonTracer");
        tg.transform.SetParent(transform, false);
        tracer = tg.AddComponent<LineRenderer>();
        tracer.positionCount     = 2;
        tracer.useWorldSpace     = true;
        tracer.startWidth        = 0.06f;
        tracer.endWidth          = 0.06f;
        tracer.shadowCastingMode = ShadowCastingMode.Off;
        tracer.receiveShadows    = false;
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color")
                     ?? Shader.Find("Universal Render Pipeline/Unlit");
        Color col = new Color(1f, 0.9f, 0.4f);
        tracer.material   = new Material(shader) { color = col };
        tracer.startColor = col;
        tracer.endColor   = col;
        tracer.enabled    = false;
    }

    // ------------------------------------------------------------------ //
    // Destruction — eject or kill occupants
    // ------------------------------------------------------------------ //

    private void HandleBuildingDestroyed()
    {
        if (!IsOccupied) return;

        var snapshot = new List<GameObject>(occupants);
        for (int i = 0; i < snapshot.Count; i++)
        {
            GameObject u = snapshot[i];
            if (u == null) continue;

            Vector3 exitPos = GetExitPosition(i);
            Vector3 forward = (exitPos - transform.position).normalized;

            if (killOccupantsOnDestroy)
            {
                // Eject first (so the unit is visible/registered), then kill on
                // the authoritative side — death replicates via the normal path.
                ExitUnitLocal(u, exitPos, forward);
                BroadcastExitIfOwner(u, exitPos, forward);
                if (Authoritative)
                {
                    Health h = u.GetComponent<Health>();
                    if (h != null) h.TakeDamage(h.maxHealth * 10f);
                }
            }
            else if (ejectOnDestroy)
            {
                ExitUnitLocal(u, exitPos, forward);
                BroadcastExitIfOwner(u, exitPos, forward);
                if (Authoritative)
                {
                    Health h = u.GetComponent<Health>();
                    if (h != null) h.TakeDamage(ejectDamage * occupantProtectionMultiplier);
                }
            }
        }
        Debug.Log($"[Garrison] '{name}' destroyed — occupants handled " +
                  $"(kill={killOccupantsOnDestroy}, eject={ejectOnDestroy}).");
    }

    private void BroadcastExitIfOwner(GameObject unit, Vector3 exitPos, Vector3 forward)
    {
        // The client that controls the units broadcasts the exit so the other
        // side mirrors. In SP this is a no-op inside the broadcaster.
        if (OwnerOf(unit) != GameEntity.LocalCommandPlayerId && NetworkManagerRTS.IsMultiplayerEnabled)
            return;
        GameEntity uge = unit.GetComponent<GameEntity>();
        if (uge != null)
            MapInteractableNetworkEvents.BroadcastGarrisonExit(EntityId, uge.EntityId, exitPos, forward);
    }
}
