using UnityEngine;

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
    [Tooltip("Distance at which the worker considers itself 'arrived' at a destination")]
    public float arrivalThreshold = 1.5f;

    [Header("References")]
    [Tooltip("Leave empty — auto-found from scene on Awake")]
    public CommandCenter commandCenter;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private WorkerState state = WorkerState.Idle;
    private ResourceNode targetNode;
    private UnitMovement movement;
    private int carryAmount;
    private float gatherTimer;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        movement = GetComponent<UnitMovement>();

        if (commandCenter == null)
            commandCenter = FindAnyObjectByType<CommandCenter>();

        if (commandCenter == null)
            Debug.LogWarning($"WorkerGatherer on {name}: No CommandCenter found. " +
                             "Worker cannot deposit until one exists.");
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

        if (HasArrived(targetNode.transform.position))
        {
            movement.Stop();
            gatherTimer = gatherTime;
            state = WorkerState.Gathering;
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

        if (commandCenter == null)
        {
            // No base to return to — drop carry and go idle
            carryAmount = 0;
            state = WorkerState.Idle;
            return;
        }

        movement.MoveTo(commandCenter.transform.position);
        state = WorkerState.MovingToBase;
    }

    private void UpdateMovingToBase()
    {
        if (commandCenter == null)
        {
            state = WorkerState.Idle;
            return;
        }

        if (HasArrived(commandCenter.transform.position))
        {
            movement.Stop();
            state = WorkerState.Depositing; // handled next frame in ExecuteDeposit
        }
    }

    private void ExecuteDeposit()
    {
        commandCenter.Deposit(carryAmount);
        carryAmount = 0;

        // Resume the loop if the node still has resources
        if (targetNode != null && !targetNode.IsDepleted)
        {
            movement.MoveTo(targetNode.transform.position);
            state = WorkerState.MovingToResource;
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

    /// <summary>True when within arrivalThreshold of the destination.</summary>
    private bool HasArrived(Vector3 destination)
    {
        return Vector3.Distance(transform.position, destination) <= arrivalThreshold;
    }
}
