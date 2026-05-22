using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Drives a unit's NavMeshAgent to a target position.
///
/// Setup:
///   1. Attach to your unit GameObject (auto-added via RequireComponent).
///   2. A NavMeshAgent component will be required automatically.
///   3. Tune Speed, Angular Speed, and Stopping Distance on the NavMeshAgent in the Inspector.
///   4. Make sure the scene has a baked NavMesh (see Unity setup instructions).
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class UnitMovement : MonoBehaviour
{
    private NavMeshAgent agent;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
    }

    // ------------------------------------------------------------------ //

    /// <summary>
    /// Sends the unit to the given world-space position.
    /// Called by UnitSelector on right-click.
    /// </summary>
    public void MoveTo(Vector3 destination)
    {
        agent.SetDestination(destination);
    }

    /// <summary>
    /// Immediately stops the unit in place.
    /// </summary>
    public void Stop()
    {
        agent.ResetPath();
    }

    /// <summary>Returns true while the agent is still navigating toward its destination.</summary>
    public bool IsMoving =>
        agent.hasPath && agent.remainingDistance > agent.stoppingDistance;
}
