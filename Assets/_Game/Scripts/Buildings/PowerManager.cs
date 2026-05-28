using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tracks power supply and demand PER PLAYER (keyed by ownerPlayerId).
/// Singleton — one instance lives on the GameManager.
///
/// Each owner has an independent grid:
///   Supply(owner)  = sum of that owner's active PowerPlant buildings.
///   Demand(owner)  = sum of that owner's active PowerConsumer buildings.
///   Powered(owner) = Supply(owner) >= Demand(owner).
///
/// This mirrors the per-owner resource model (<see cref="ResourceBank"/> /
/// <see cref="PlayerResourceManager"/>): Player 0's power never affects
/// Player 1's and vice versa. Because each client deterministically spawns
/// every building with its <see cref="GameEntity.ownerPlayerId"/> stamped,
/// the per-owner totals come out identical on every client with no extra
/// network traffic — power is derived from building presence, which the
/// custom RaiseEvent construction events already replicate.
///
/// Production buildings read <see cref="PowerConsumer.IsPowered"/> (which
/// resolves to <see cref="IsPoweredFor"/> for that building's owner) before
/// allowing unit production.
///
/// Setup:
///   Add this component to your GameManager GameObject.
///   No Inspector fields required.
/// </summary>
public class PowerManager : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Singleton
    // ------------------------------------------------------------------ //

    public static PowerManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("PowerManager: duplicate instance destroyed.");
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ------------------------------------------------------------------ //
    // Per-owner state
    // ------------------------------------------------------------------ //

    private readonly Dictionary<int, int> supplyByOwner = new Dictionary<int, int>(2);
    private readonly Dictionary<int, int> demandByOwner = new Dictionary<int, int>(2);

    /// <summary>Total supply registered for <paramref name="owner"/> (0 if none).</summary>
    public int SupplyFor(int owner)
        => supplyByOwner.TryGetValue(owner, out int v) ? v : 0;

    /// <summary>Total demand registered for <paramref name="owner"/> (0 if none).</summary>
    public int DemandFor(int owner)
        => demandByOwner.TryGetValue(owner, out int v) ? v : 0;

    /// <summary>True when <paramref name="owner"/>'s supply covers its own demand.</summary>
    public bool IsPoweredFor(int owner) => SupplyFor(owner) >= DemandFor(owner);

    // ------------------------------------------------------------------ //
    // Registration — called by PowerPlant and PowerConsumer (owner-keyed)
    // ------------------------------------------------------------------ //

    public void AddSupply(int owner, int amount, string source)
    {
        int total = SupplyFor(owner) + amount;
        supplyByOwner[owner] = total;
        LogChange(owner, +amount, total, source);
    }

    public void RemoveSupply(int owner, int amount, string source)
    {
        int total = Mathf.Max(0, SupplyFor(owner) - amount);
        supplyByOwner[owner] = total;
        LogChange(owner, -amount, total, source);
    }

    public void AddDemand(int owner, int amount, string source)
    {
        int total = DemandFor(owner) + amount;
        demandByOwner[owner] = total;
        LogChange(owner, +amount, total, source);
    }

    public void RemoveDemand(int owner, int amount, string source)
    {
        int total = Mathf.Max(0, DemandFor(owner) - amount);
        demandByOwner[owner] = total;
        LogChange(owner, -amount, total, source);
    }

    // ------------------------------------------------------------------ //
    // Internal
    // ------------------------------------------------------------------ //

    private static void LogChange(int owner, int delta, int total, string source)
    {
        Debug.Log($"[Power] owner={owner} delta={delta} total={total} " +
                  $"source={(string.IsNullOrEmpty(source) ? "?" : source)}");
    }
}
