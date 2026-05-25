using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Self-running gather loop for the enemy starting Worker. Parallel sibling of
/// <see cref="WorkerGatherer"/>: same Moving → Gathering → Returning → Deposit
/// state machine, but driven autonomously (no player commands) and bound to
/// the enemy economy via <see cref="EnemyCommandCenter"/> + <see cref="EnemyResourceManager"/>.
///
/// Loop:
///   MovingToResource → Gathering → MovingToBase → Depositing → repeat
///
/// On startup the worker:
///   1. Resolves its sibling components and the enemy CC (auto-find via
///      <see cref="EnemyCommandCenter"/> singleton lookup; assignable in the
///      Inspector if a scene has more than one).
///   2. Records the enemy base position as its "home" — the search centre when
///      picking the next ResourceNode. Stops the worker from wandering across
///      the map after a node depletes.
///   3. Finds the nearest non-depleted ResourceNode within
///      <see cref="resourceSearchRadius"/> of home and starts gathering.
///
/// Idle behaviour:
///   When no eligible ResourceNode exists within range the worker logs once
///   and goes <see cref="WorkerState.Idle"/>. It re-scans every
///   <see cref="idleRescanInterval"/> seconds in case the map state changes
///   (e.g. future enemy bot drops a new resource extractor).
///
/// What this script intentionally does NOT do:
///   • Accept player commands. There is no SetGatherTarget / CancelGathering;
///     the loop owns the worker for life.
///   • Engage in combat. The enemy worker is unarmed.
///   • Build or scout. Future bot AI scope, not this milestone.
///   • Touch player workers, player resource flow, or the HUD.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(UnitMovement))]
public class EnemyWorkerAI : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    private enum WorkerState
    {
        Idle,
        MovingToResource,
        Gathering,
        MovingToBase,
        Depositing,
    }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Gathering")]
    [Tooltip("Seconds the worker spends at the node per trip.")]
    public float gatherTime = 1.5f;

    [Header("Navigation")]
    [Tooltip("Distance at which the worker counts as 'arrived' at a ResourceNode.")]
    public float arrivalThreshold = 1.5f;

    [Tooltip("Larger arrival distance used for the enemy CommandCenter. The CC " +
             "is a wide cube; the NavMeshAgent stops at the cube's surface, not " +
             "its centre, so we need slack here for the fallback distance check.")]
    public float baseArrivalThreshold = 4f;

    [Header("Auto Gathering")]
    [Tooltip("World-unit radius searched (centred on home) for the next available " +
             "ResourceNode. Worker logs and idles if no eligible node exists in range.")]
    public float resourceSearchRadius = 40f;

    [Tooltip("Seconds between rescans while idle. Lets a worker that ran out of " +
             "resources pick a new node if one later becomes available (e.g. a " +
             "future enemy bot drops a new harvester).")]
    public float idleRescanInterval = 3f;

    [Header("References (auto-resolved)")]
    [Tooltip("Enemy CommandCenter drop-off. Leave empty to auto-find via " +
             "EnemyCommandCenter scene lookup.")]
    public EnemyCommandCenter enemyCommandCenter;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private WorkerState state = WorkerState.Idle;
    private ResourceNode targetNode;
    private UnitMovement movement;
    private NavMeshAgent agent;

    private Vector3 home;            // captured at startup; search centre + log only.
    private bool    homeKnown;

    private int   carryAmount;
    private float gatherTimer;
    private float nextIdleRescan;

    // One-shot log guards so a long no-resources stretch doesn't spam the console.
    private bool loggedStartingLoop;
    private bool loggedNoResources;
    private bool loggedNoBase;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        movement = GetComponent<UnitMovement>();
        agent    = GetComponent<NavMeshAgent>();

        // Force enemy team on the Health component so a worker that was cloned
        // from the player WorkerPrefab can never accidentally deposit to the
        // player. Defensive — SetupCleanMatchMap also sets this at edit time.
        Health hp = GetComponent<Health>();
        if (hp != null && hp.team != Health.Team.Enemy)
            hp.team = Health.Team.Enemy;
    }

    private void Start()
    {
        if (enemyCommandCenter == null)
            enemyCommandCenter = FindAnyObjectByType<EnemyCommandCenter>();

        if (enemyCommandCenter == null)
        {
            if (!loggedNoBase)
            {
                Debug.LogWarning($"[EnemyWorker:{name}] No EnemyCommandCenter in scene — " +
                                 "worker idle until one exists.");
                loggedNoBase = true;
            }
            state = WorkerState.Idle;
            return;
        }

        home      = enemyCommandCenter.transform.position;
        homeKnown = true;

        if (!loggedStartingLoop)
        {
            Debug.Log($"[EnemyWorker:{name}] Starting gather loop.");
            loggedStartingLoop = true;
        }

        BeginGatherFromHome();
    }

    private void Update()
    {
        switch (state)
        {
            case WorkerState.Idle:             UpdateIdle();                  break;
            case WorkerState.MovingToResource: UpdateMovingToResource();      break;
            case WorkerState.Gathering:        UpdateGathering();             break;
            case WorkerState.MovingToBase:     UpdateMovingToBase();          break;
            case WorkerState.Depositing:       ExecuteDeposit();              break;
        }
    }

    // ------------------------------------------------------------------ //
    // State updates
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Idle workers re-scan every <see cref="idleRescanInterval"/> seconds so a
    /// late-arriving ResourceNode (e.g. future enemy bot drop) wakes them.
    /// Cheap — one FindObjectsByType call per worker per few seconds.
    /// </summary>
    private void UpdateIdle()
    {
        if (!homeKnown) return;
        if (Time.time < nextIdleRescan) return;
        nextIdleRescan = Time.time + idleRescanInterval;

        ResourceNode next = FindNearestAvailableResource();
        if (next == null) return;

        loggedNoResources = false;     // re-arm the "no resources" log
        StartTrip(next);
    }

    private void UpdateMovingToResource()
    {
        if (targetNode == null)
        {
            BeginGatherFromHome();      // node died en route → search again
            return;
        }

        if (HasArrived(targetNode.transform.position, arrivalThreshold))
        {
            movement.Stop();
            gatherTimer = gatherTime;
            state = WorkerState.Gathering;
            Debug.Log($"[EnemyWorker:{name}] Gathering from ResourceNode.");
        }
    }

    private void UpdateGathering()
    {
        if (targetNode == null)
        {
            // Node was killed by another worker mid-gather. carryAmount is still 0.
            BeginGatherFromHome();
            return;
        }

        gatherTimer -= Time.deltaTime;
        if (gatherTimer > 0f) return;

        // Collect — Gather() destroys the node when fully depleted.
        carryAmount = targetNode.Gather();

        if (enemyCommandCenter == null)
        {
            Debug.LogWarning($"[EnemyWorker:{name}] Lost EnemyCommandCenter — dropping {carryAmount}.");
            carryAmount = 0;
            state = WorkerState.Idle;
            return;
        }

        movement.MoveTo(enemyCommandCenter.transform.position);
        state = WorkerState.MovingToBase;
        Debug.Log($"[EnemyWorker:{name}] Returning to Enemy CommandCenter.");
    }

    private void UpdateMovingToBase()
    {
        if (enemyCommandCenter == null)
        {
            Debug.LogWarning($"[EnemyWorker:{name}] EnemyCommandCenter destroyed mid-trip. " +
                             $"Discarding {carryAmount}.");
            carryAmount = 0;
            state = WorkerState.Idle;
            return;
        }

        if (HasArrived(enemyCommandCenter.transform.position, baseArrivalThreshold))
        {
            movement.Stop();
            state = WorkerState.Depositing;     // executes on next tick
        }
    }

    private void ExecuteDeposit()
    {
        if (carryAmount > 0 && enemyCommandCenter != null)
            enemyCommandCenter.Deposit(carryAmount);

        carryAmount = 0;

        // Continue the loop: same node if it still has resources, otherwise
        // find a new one near home.
        if (targetNode != null && !targetNode.IsDepleted)
        {
            movement.MoveTo(targetNode.transform.position);
            state = WorkerState.MovingToResource;
        }
        else
        {
            Debug.Log($"[EnemyWorker:{name}] Resource depleted, searching new node.");
            targetNode = null;
            BeginGatherFromHome();
        }
    }

    // ------------------------------------------------------------------ //
    // Trip lifecycle
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Picks the nearest ResourceNode within <see cref="resourceSearchRadius"/>
    /// of <see cref="home"/> and starts a trip. Falls through to idle (with one
    /// log) when nothing is in range.
    /// </summary>
    private void BeginGatherFromHome()
    {
        ResourceNode next = FindNearestAvailableResource();
        if (next == null)
        {
            if (!loggedNoResources)
            {
                Debug.Log($"[EnemyWorker:{name}] No nearby resources found.");
                loggedNoResources = true;
            }
            state          = WorkerState.Idle;
            nextIdleRescan = Time.time + idleRescanInterval;
            return;
        }

        loggedNoResources = false;
        StartTrip(next);
    }

    private void StartTrip(ResourceNode node)
    {
        targetNode  = node;
        carryAmount = 0;
        state       = WorkerState.MovingToResource;
        movement.MoveTo(node.transform.position);
    }

    // ------------------------------------------------------------------ //
    // Resource scan — centred on HOME, not the worker. Keeps the enemy
    // worker close to its base even if it ends up far from home (e.g. a
    // pathing detour, future bot expansion).
    // ------------------------------------------------------------------ //

    private ResourceNode FindNearestAvailableResource()
    {
        ResourceNode[] all = FindObjectsByType<ResourceNode>(FindObjectsSortMode.None);

        ResourceNode best = null;
        float        bestDist = float.MaxValue;
        Vector3      centre   = homeKnown ? home : transform.position;
        float        maxDist  = resourceSearchRadius;

        foreach (ResourceNode n in all)
        {
            if (n == null) continue;
            if (!n.isActiveAndEnabled) continue;
            if (n.IsDepleted) continue;

            float d = Vector3.Distance(centre, n.transform.position);
            if (d > maxDist) continue;
            if (d >= bestDist) continue;

            bestDist = d;
            best     = n;
        }

        return best;
    }

    // ------------------------------------------------------------------ //
    // Arrival helper — copy of WorkerGatherer's pattern. Primary signal is
    // NavMeshAgent.remainingDistance (reliable when the destination is inside
    // an obstacle); fallback is straight-line Vector3.Distance.
    // ------------------------------------------------------------------ //

    private bool HasArrived(Vector3 destination, float threshold)
    {
        if (agent != null && agent.enabled && agent.isOnNavMesh && !agent.pathPending && agent.hasPath)
        {
            float stop = Mathf.Max(agent.stoppingDistance, threshold);
            if (agent.remainingDistance <= stop)
                return true;
        }
        return Vector3.Distance(transform.position, destination) <= threshold;
    }
}
