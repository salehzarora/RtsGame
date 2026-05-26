using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Gives a unit worker behaviour: gather from a ResourceNode and deposit at the CommandCenter.
///
/// Loop:  MovingToResource → Gathering (wait) → MovingToBase → Deposit → repeat
///
/// Auto-find behaviour:
///   When the current ResourceNode runs out (becomes depleted, destroyed, or
///   disappears mid-trip) the worker checks <see cref="autoFindNewResource"/>
///   and whether it is still on an active gather job. If both are true it
///   searches the scene for the NEAREST non-depleted ResourceNode within
///   <see cref="resourceSearchRadius"/> and resumes the loop on that node.
///   If no eligible node is found, or the player cancelled the job (e.g. a
///   ground-move right-click), the worker goes idle.
///
///   Carrying resources when the node depletes is handled gracefully: the
///   worker first walks to the CommandCenter and deposits its load, THEN
///   triggers the auto-find. Workers that were empty when the node was
///   lost search immediately.
///
/// Setup:
///   Add to the Worker capsule alongside Health, UnitMovement, SelectableUnit.
///   Do NOT add UnitCombat — workers don't fight.
///   Optionally assign CommandCenter in the Inspector; if left empty it is auto-found.
/// </summary>
[RequireComponent(typeof(UnitMovement))]
public class WorkerGatherer : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    private enum WorkerState
    {
        Idle,
        MovingToResource,
        Gathering,          // standing at node, waiting for gather timer
        MovingToBase,
        Depositing          // single-frame state: call Deposit then transition
    }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Gathering")]
    [Tooltip("Seconds the worker spends at the node per trip")]
    public float gatherTime = 1.5f;

    [Header("Navigation")]
    [Tooltip("Distance at which the worker considers itself 'arrived' at a ResourceNode")]
    public float arrivalThreshold = 1.5f;

    [Tooltip("Larger arrival distance used for the CommandCenter. The CC is a wide cube; " +
             "the NavMeshAgent stops at the cube's surface, not its center, so we need slack " +
             "here for the fallback distance check to register arrival.")]
    public float baseArrivalThreshold = 4f;

    [Header("Auto Gathering")]
    [Tooltip("If true, when the current ResourceNode is depleted the worker searches for the " +
             "nearest non-depleted ResourceNode and continues the gather loop. Only fires while " +
             "the worker is on an active gather job (player previously assigned a ResourceNode).")]
    public bool autoFindNewResource = true;

    [Tooltip("World-unit radius searched for the next available ResourceNode when the current " +
             "one is depleted. Worker goes idle if no eligible node exists within this distance.")]
    public float resourceSearchRadius = 40f;

    [Header("References")]
    [Tooltip("Leave empty — auto-found from scene on Awake")]
    public CommandCenter commandCenter;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private WorkerState state = WorkerState.Idle;
    private ResourceNode targetNode;
    private UnitMovement movement;
    private NavMeshAgent agent;
    private int carryAmount;
    private float gatherTimer;

    // True between SetGatherTarget() and the next CancelGathering() / "no more
    // resources found". Gates auto-find so a freshly-spawned worker or a
    // worker moved with a ground right-click does NOT start gathering on its
    // own — auto-find only kicks in if the player explicitly assigned a node
    // first.
    private bool hasActiveGatherJob;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        movement = GetComponent<UnitMovement>();
        agent    = GetComponent<NavMeshAgent>();

        // Auto-find a CommandCenter that matches THIS worker's owner. In
        // single-player there's only one CC, so this resolves identically to
        // the legacy behaviour. In multiplayer there are two; pick the
        // closest one whose owner matches this worker.
        if (commandCenter == null)
            commandCenter = ResolveOwnerCommandCenter();

        if (commandCenter == null)
            Debug.LogWarning($"[Worker:{name}] No CommandCenter found for owner " +
                             $"{GetWorkerOwnerId()}. Worker cannot deposit until one exists.");

        if (FindAnyObjectByType<PlayerResourceManager>() == null)
            Debug.LogWarning($"[Worker:{name}] No PlayerResourceManager found. " +
                             "Deposits will be ignored until one exists.");
    }

    private int GetWorkerOwnerId()
    {
        GameEntity ge = GetComponent<GameEntity>();
        return ge != null ? ge.ownerPlayerId : 0;
    }

    /// <summary>
    /// Pick the closest <see cref="CommandCenter"/> whose owner matches this
    /// worker. Falls back to ANY CommandCenter (for the single-player /
    /// transition path where ids may not be stamped yet).
    /// </summary>
    private CommandCenter ResolveOwnerCommandCenter()
    {
        int myOwner = GetWorkerOwnerId();
        CommandCenter[] all = FindObjectsByType<CommandCenter>(FindObjectsSortMode.None);
        CommandCenter best = null;
        float bestDist = float.MaxValue;
        foreach (CommandCenter cc in all)
        {
            if (cc == null) continue;
            if (cc.OwnerPlayerId != myOwner) continue;
            float d = (cc.transform.position - transform.position).sqrMagnitude;
            if (d < bestDist) { best = cc; bestDist = d; }
        }
        // Fallback: any CC. Keeps single-player working even if a designer
        // hasn't run the multiplayer scene tool.
        if (best == null && all.Length > 0) best = all[0];
        return best;
    }

    private void Update()
    {
        switch (state)
        {
            case WorkerState.Idle:             /* nothing */                    break;
            case WorkerState.MovingToResource: UpdateMovingToResource();        break;
            case WorkerState.Gathering:        UpdateGathering();               break;
            case WorkerState.MovingToBase:     UpdateMovingToBase();            break;
            case WorkerState.Depositing:       ExecuteDeposit();                break;
        }
    }

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Assign a resource node as the gather target.
    /// Called by UnitSelector when the player right-clicks a ResourceNode.
    /// Re-enables auto-find for the worker.
    /// </summary>
    public void SetGatherTarget(ResourceNode node)
    {
        if (node == null || node.IsDepleted) return;

        targetNode = node;
        carryAmount = 0;
        hasActiveGatherJob = true;
        state = WorkerState.MovingToResource;
        movement.MoveTo(node.transform.position);
        Debug.Log($"[Worker:{name}] Going to resource at {node.transform.position:F1}.");
    }

    /// <summary>
    /// Interrupt gathering. Called when a move command overrides the current task.
    /// Also disables auto-find — a manually-moved worker must NOT randomly
    /// start gathering on its own.
    /// </summary>
    public void CancelGathering()
    {
        targetNode = null;
        carryAmount = 0;
        hasActiveGatherJob = false;
        state = WorkerState.Idle;
        // Movement is handled by the caller (UnitSelector sets the new destination)
    }

    // ------------------------------------------------------------------ //
    // State updates
    // ------------------------------------------------------------------ //

    private void UpdateMovingToResource()
    {
        // Node destroyed while we were walking to it — search for another.
        if (targetNode == null)
        {
            TryContinueWithNewResource();
            return;
        }

        if (HasArrived(targetNode.transform.position, arrivalThreshold))
        {
            movement.Stop();
            gatherTimer = gatherTime;
            state = WorkerState.Gathering;
            Debug.Log($"[Worker:{name}] Arrived at resource — gathering for {gatherTime:F1}s.");
        }
    }

    private void UpdateGathering()
    {
        // Node destroyed mid-gather (e.g. another worker finished it). Carry
        // is still 0 because we hadn't completed this trip yet — search now.
        if (targetNode == null)
        {
            TryContinueWithNewResource();
            return;
        }

        gatherTimer -= Time.deltaTime;
        if (gatherTimer > 0f) return;

        // Collect resources — Gather() destroys the node if fully depleted
        carryAmount = targetNode.Gather();
        Debug.Log($"[Worker:{name}] Gathered {carryAmount}.");

        if (commandCenter == null)
        {
            Debug.LogWarning($"[Worker:{name}] No CommandCenter — dropping {carryAmount} and going idle.");
            carryAmount = 0;
            state = WorkerState.Idle;
            return;
        }

        movement.MoveTo(commandCenter.transform.position);
        state = WorkerState.MovingToBase;
        Debug.Log($"[Worker:{name}] Returning to CommandCenter at " +
                  $"{commandCenter.transform.position:F1} carrying {carryAmount}.");
    }

    private void UpdateMovingToBase()
    {
        if (commandCenter == null)
        {
            Debug.LogWarning($"[Worker:{name}] CommandCenter disappeared mid-trip. " +
                             $"Discarding {carryAmount}.");
            carryAmount = 0;
            state = WorkerState.Idle;
            return;
        }

        if (HasArrived(commandCenter.transform.position, baseArrivalThreshold))
        {
            movement.Stop();
            state = WorkerState.Depositing; // handled next frame in ExecuteDeposit
            Debug.Log($"[Worker:{name}] Arrived at CommandCenter — depositing {carryAmount}.");
        }
    }

    private void ExecuteDeposit()
    {
        if (carryAmount <= 0)
        {
            Debug.LogWarning($"[Worker:{name}] Deposit skipped — carrying 0.");
        }
        else if (commandCenter == null)
        {
            Debug.LogWarning($"[Worker:{name}] Deposit skipped — CommandCenter is null.");
        }
        else
        {
            int amount = carryAmount;
            commandCenter.Deposit(amount);
            Debug.Log($"[Worker:{name}] Deposited {amount}.");
        }

        carryAmount = 0;

        // Resume the loop if the node still has resources, otherwise auto-find.
        if (targetNode != null && !targetNode.IsDepleted)
        {
            movement.MoveTo(targetNode.transform.position);
            state = WorkerState.MovingToResource;
            Debug.Log($"[Worker:{name}] Returning to resource for next trip.");
        }
        else
        {
            // Node ran out during our last trip — we've now deposited our
            // load (point 7 of the spec) and can look for a new node.
            targetNode = null;
            TryContinueWithNewResource();
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// True when the worker has reached <paramref name="destination"/>.
    ///
    /// Primary signal: NavMeshAgent.remainingDistance once the path is computed —
    /// this is the reliable answer when the destination sits inside an obstacle
    /// (e.g. the CommandCenter cube), because the agent pathed to the building's
    /// surface and reports remainingDistance from there.
    ///
    /// Fallback: straight-line Vector3.Distance with the supplied threshold —
    /// handles the case where the agent has no path at all.
    /// </summary>
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

    // ------------------------------------------------------------------ //
    // Auto-find — pick the next ResourceNode when the current one is gone
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Called when the current <see cref="targetNode"/> has been lost (depleted /
    /// destroyed / missing). If the worker is on an active gather job and
    /// auto-find is enabled, switches to the nearest eligible ResourceNode
    /// within <see cref="resourceSearchRadius"/> and resumes the loop.
    /// Otherwise clears the job and goes idle.
    /// </summary>
    private void TryContinueWithNewResource()
    {
        if (!hasActiveGatherJob || !autoFindNewResource)
        {
            ClearJobAndGoIdle();
            return;
        }

        Debug.Log($"[Worker:{name}] Current node depleted — " +
                  $"searching for nearby resource within {resourceSearchRadius:F0}m.");

        ResourceNode next = FindNearestAvailableResource();
        if (next == null)
        {
            Debug.Log($"[Worker:{name}] No resources found within {resourceSearchRadius:F0}m — " +
                      "worker idle.");
            ClearJobAndGoIdle();
            return;
        }

        Debug.Log($"[Worker:{name}] Found new resource node '{next.name}' at " +
                  $"{next.transform.position:F1}.");

        targetNode = next;
        state = WorkerState.MovingToResource;
        movement.MoveTo(next.transform.position);
    }

    /// <summary>
    /// Linear scan of every ResourceNode in the scene. Filters out null /
    /// inactive / depleted entries and beyond-radius candidates, then returns
    /// the closest one. ResourceNode counts are small in this prototype so
    /// the O(N) sweep is fine; cache it later if it ever becomes hot.
    /// </summary>
    private ResourceNode FindNearestAvailableResource()
    {
        ResourceNode[] all = FindObjectsByType<ResourceNode>(FindObjectsSortMode.None);

        ResourceNode best = null;
        float        bestDist = float.MaxValue;
        Vector3      myPos    = transform.position;
        float        maxDist  = resourceSearchRadius;

        foreach (ResourceNode n in all)
        {
            if (n == null) continue;
            if (!n.isActiveAndEnabled) continue;
            if (n.IsDepleted) continue;                  // CurrentResources <= 0

            float d = Vector3.Distance(myPos, n.transform.position);
            if (d > maxDist) continue;
            if (d >= bestDist) continue;

            bestDist = d;
            best     = n;
        }

        return best;
    }

    private void ClearJobAndGoIdle()
    {
        targetNode         = null;
        carryAmount        = 0;
        hasActiveGatherJob = false;
        state              = WorkerState.Idle;
    }
}
