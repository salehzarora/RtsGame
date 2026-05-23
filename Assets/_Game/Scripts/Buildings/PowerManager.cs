using UnityEngine;

/// <summary>
/// Tracks the global power supply and demand for all player buildings.
/// Singleton — one instance lives on the GameManager.
///
/// Supply  = sum of all active PowerPlant buildings.
/// Demand  = sum of all active PowerConsumer buildings.
/// Powered = Supply >= Demand.
///
/// Production buildings read IsPowered before allowing unit production.
/// Console-only for now; UI overlay comes in a later phase.
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
    // State
    // ------------------------------------------------------------------ //

    public int TotalSupply { get; private set; }
    public int TotalDemand { get; private set; }

    /// <summary>True when supply covers demand. All powered buildings are operational.</summary>
    public bool IsPowered => TotalSupply >= TotalDemand;

    // ------------------------------------------------------------------ //
    // Registration — called by PowerPlant and PowerConsumer
    // ------------------------------------------------------------------ //

    public void AddSupply(int amount)
    {
        TotalSupply += amount;
        LogStatus($"PowerPlant added +{amount} supply");
    }

    public void RemoveSupply(int amount)
    {
        TotalSupply = Mathf.Max(0, TotalSupply - amount);
        LogStatus($"PowerPlant removed -{amount} supply");
    }

    public void AddDemand(int amount)
    {
        TotalDemand += amount;
        LogStatus($"Building added +{amount} demand");
    }

    public void RemoveDemand(int amount)
    {
        TotalDemand = Mathf.Max(0, TotalDemand - amount);
        LogStatus($"Building removed -{amount} demand");
    }

    // ------------------------------------------------------------------ //
    // Internal
    // ------------------------------------------------------------------ //

    private void LogStatus(string reason)
    {
        string status = IsPowered
            ? $"OK ({TotalSupply - TotalDemand} surplus)"
            : $"⚠ INSUFFICIENT ({TotalDemand - TotalSupply} short)";

        Debug.Log($"[Power] {reason}. Supply: {TotalSupply} / Demand: {TotalDemand} — {status}");
    }
}
