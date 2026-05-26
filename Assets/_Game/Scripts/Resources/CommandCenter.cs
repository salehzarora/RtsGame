using UnityEngine;

/// <summary>
/// A faction's resource drop-off building. Workers return here after each
/// gather trip; <see cref="Deposit"/> routes the gathered amount into the
/// owner's <see cref="PlayerResourceManager"/> via <see cref="ResourceBank"/>.
///
/// Ownership: the GameEntity attached to this CommandCenter declares the
/// owner. <see cref="WorkerGatherer"/> picks the nearest CC whose owner
/// matches the worker — preventing cross-team deposits.
///
/// Setup:
///   1. Create a cube in the scene and name it "CommandCenter" (or per-player
///      "CommandCenter_P0" / "_P1" in multiplayer).
///   2. Attach this script plus <see cref="GameEntity"/>.
///   3. Set GameEntity.ownerPlayerId to the faction the building belongs to.
///   4. Make sure at least one <see cref="PlayerResourceManager"/> with the
///      matching ownerPlayerId exists in the scene.
/// </summary>
public class CommandCenter : MonoBehaviour
{
    // Cached on demand — we don't bind in Awake because the GameEntity's
    // ownerPlayerId can be updated by the spawn pipeline (Phase 3 ownership
    // inheritance) after CommandCenter.Awake.
    private GameEntity selfEntity;

    private void Awake()
    {
        selfEntity = GetComponent<GameEntity>();
    }

    /// <summary>
    /// Deposit gathered resources into this CommandCenter's owner bank.
    /// Called automatically by <see cref="WorkerGatherer"/> on arrival.
    /// </summary>
    public void Deposit(int amount)
    {
        if (amount <= 0) return;

        // Resolve owner each call so a late ownership stamp (from the spawn
        // pipeline) still routes deposits to the right bank.
        if (selfEntity == null) selfEntity = GetComponent<GameEntity>();
        int ownerId = selfEntity != null ? selfEntity.ownerPlayerId : 0;

        PlayerResourceManager bank = ResourceBank.For(ownerId);
        if (bank == null)
        {
            Debug.LogWarning($"[CommandCenter:{name}] Deposit({amount}) ignored — " +
                             $"no PlayerResourceManager registered for owner {ownerId}.");
            return;
        }

        bank.AddResources(amount);
    }

    /// <summary>
    /// Public accessor used by <see cref="WorkerGatherer"/> to find an
    /// owner-matching drop-off without depending on a sibling GameEntity
    /// reference layout.
    /// </summary>
    public int OwnerPlayerId
    {
        get
        {
            if (selfEntity == null) selfEntity = GetComponent<GameEntity>();
            return selfEntity != null ? selfEntity.ownerPlayerId : 0;
        }
    }
}
