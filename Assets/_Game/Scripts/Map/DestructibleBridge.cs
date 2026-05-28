using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// PHASE C — destructible bridge. Extends <see cref="DestructibleMapObject"/>:
/// while intact, units cross it on the baked NavMesh; once destroyed it swaps
/// to a collapsed visual and BLOCKS the crossing.
///
/// How blocking works (and why):
///   • NavMeshAgents ignore plain colliders — they only avoid baked NavMesh
///     holes and active carving <see cref="NavMeshObstacle"/>s. So the real
///     blocker is a carving NavMeshObstacle enabled on destruction; it cuts a
///     hole in the NavMesh so new paths route around (or fail to cross) the
///     gap. The optional physical blocker collider stops non-agent physics.
///   • We deliberately do NOT rebuild the NavMesh at runtime (expensive and
///     would desync). Carving is local + deterministic and produces the same
///     result on every client.
///
/// Multiplayer:
///   • Destruction replicates through the existing EntityDestroyed + health-
///     snapshot path (see base class). Each client enables ITS OWN obstacle +
///     blocker, so the path is blocked identically everywhere with no extra
///     network traffic and no position desync.
///
/// Units caught on the deck when it collapses are stopped (so they don't keep
/// trying to walk the now-carved gap) — a minimal safe behaviour; they can be
/// re-ordered normally afterwards.
///
/// Setup (or use Tools → RTS → Map → Create Destructible Bridge):
///   1. Sibling Health + GameEntity (entityType = MapObject).
///   2. Intact Visual = the deck mesh; Destroyed Visual = the collapsed mesh.
///   3. Disable Colliders On Destroy = [deck walk collider].
///   4. Enable Colliders On Destroy  = [physical blocker] (optional).
///   5. NavMeshObstacle = a child obstacle covering the deck, carving ON,
///      component DISABLED at edit time (it's enabled on destruction). The deck
///      must sit on the baked NavMesh for crossing to work while intact.
/// </summary>
public class DestructibleBridge : DestructibleMapObject
{
    [Header("Bridge — Path Blocking")]
    [Tooltip("Carving NavMeshObstacle that cuts the crossing out of the NavMesh " +
             "when the bridge is destroyed. Should cover the deck footprint and " +
             "start DISABLED. Without it, NavMeshAgents will still path across " +
             "the gap (agents ignore plain colliders).")]
    public NavMeshObstacle navMeshObstacle;

    [Tooltip("Master switch — if false the bridge collapses visually but does " +
             "not block movement (useful while tuning visuals).")]
    public bool blocksPathWhenDestroyed = true;

    [Tooltip("On collapse, stop any units standing within this radius of the " +
             "bridge centre so they don't keep trying to cross the carved gap. " +
             "0 disables the sweep.")]
    public float stopUnitsOnDeckRadius = 6f;

    [Header("Bridge — Future Hooks")]
    [Tooltip("Reserved for a future repair/rebuild feature. Not implemented yet — " +
             "kept so the data/intent is captured. See ResetToIntact for the " +
             "restore path used by match reset.")]
    public bool repairableFutureHook = false;

    private static readonly Collider[] s_deckSweep = new Collider[64];

    // ------------------------------------------------------------------ //
    // Editor defaults
    // ------------------------------------------------------------------ //

    private void Reset()
    {
        maxHealth             = 400f;   // bridges are tough — siege weapons / aircraft
        persistAfterDestroyed = true;   // the collapsed deck + blocker persist
        isTargetable          = true;
    }

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    protected override void Awake()
    {
        base.Awake();

        // Make sure the obstacle starts inert while the bridge is intact.
        if (navMeshObstacle != null) navMeshObstacle.enabled = false;

        OnDestroyed += HandleBridgeCollapsed;
    }

    // ------------------------------------------------------------------ //
    // Collapse
    // ------------------------------------------------------------------ //

    private void HandleBridgeCollapsed()
    {
        if (blocksPathWhenDestroyed && navMeshObstacle != null)
        {
            navMeshObstacle.carving = true;
            navMeshObstacle.enabled = true;
            Debug.Log($"[Bridge] '{name}' collapsed — NavMesh carving enabled, crossing blocked.");
        }
        else
        {
            Debug.Log($"[Bridge] '{name}' collapsed (no path block).");
        }

        StopUnitsOnDeck();
    }

    /// <summary>
    /// Stops units standing on the deck so they don't keep pathing into the
    /// carved gap. Minimal + safe: it only issues Stop on the units' own
    /// movement — it does not teleport or destroy them.
    /// </summary>
    private void StopUnitsOnDeck()
    {
        if (stopUnitsOnDeckRadius <= 0f) return;

        int n = Physics.OverlapSphereNonAlloc(transform.position, stopUnitsOnDeckRadius, s_deckSweep);
        for (int i = 0; i < n; i++)
        {
            Collider c = s_deckSweep[i];
            if (c == null) continue;
            UnitMovement mv = c.GetComponent<UnitMovement>() ?? c.GetComponentInParent<UnitMovement>();
            if (mv != null && mv.LocallyControlled) mv.Stop();
        }
    }

    // ------------------------------------------------------------------ //
    // Reset (match restart)
    // ------------------------------------------------------------------ //

    public override void ResetToIntact()
    {
        base.ResetToIntact();
        if (navMeshObstacle != null) navMeshObstacle.enabled = false;
    }
}
