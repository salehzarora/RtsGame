using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Gives a unit worker behaviour: gather from a ResourceNode and deposit at the CommandCenter.
///
/// Loop:  MovingToResource → Gathering (wait) → MovingToBase → Deposit → repeat
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

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        movement = GetComponent<UnitMovement>();
        agent    = GetComponent<NavMeshAgent>();

        if (commandCenter == null)
            commandCenter = FindAnyObjectByType<CommandCenter>();

        if (commandCenter == null)
            Debug.LogWarning($"[Worker:{name}] No CommandCenter found. " +
                             "Worker cannot deposit until one exists.");

        if (FindAnyObjectByType<PlayerResourceManager>() == null)
            Debug.LogWarning($"[Worker:{name}] No PlayerResourceManager found. " +
                             "Deposits will be ignored until one exists.");
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
    /// </summary>
    public void SetGatherTarget(ResourceNode node)
    {
        if (node == null || node.IsDepleted) return;

        targetNode = node;
        carryAmount = 0;
        state = WorkerState.MovingToResource;
        movement.MoveTo(node.transform.position);
        Debug.Log($"[Worker:{name}] Going to resource at {node.transform.position:F1}.");
    }

    /// <summary>
    /// Interrupt gathering. Called when a move command overrides the current task.
    /// </summary>
    public void CancelGathering()
    {
        targetNode = null;
        carryAmount = 0;
        state = WorkerState.Idle;
        // Movement is handled by the caller (UnitSelector sets the new destination)
    }

    // ------------------------------------------------------------------ //
    // State updates
    // ------------------------------------------------------------------ //

    private void UpdateMovingToResource()
    {
        // Node destroyed while we were walking to it
        if (targetNode == null)
        {
            state = WorkerState.Idle;
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
        // Node destroyed while we were gathering (e.g. another worker finished it)
        if (targetNode == null)
        {
            state = WorkerState.Idle;
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

        // Resume the loop if the node still has resources
        if (targetNode != null && !targetNode.IsDepleted)
        {
            movement.MoveTo(targetNode.transform.position);
            state = WorkerState.MovingToResource;
            Debug.Log($"[Worker:{name}] Returning to resource for next trip.");
        }
        else
        {
            targetNode = null;
            state = WorkerState.Idle;
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
}
