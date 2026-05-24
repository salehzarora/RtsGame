using UnityEngine;

/// <summary>
/// Production + parking management for the Airfield building.
///
/// Each Airfield has exactly <see cref="MaxSlots"/> aircraft slots (Transform
/// children, assigned in the Inspector). Producing a Strike Jet finds the
/// first free slot, spawns an aircraft there, and binds the aircraft to that
/// slot via <see cref="AirUnitController.AssignHome"/>. When the aircraft is
/// destroyed (Unity's overloaded == null catches that), the slot becomes free
/// again automatically — no event subscription required.
///
/// Setup (done automatically by Tools → RTS → Air System → Create Airfield Prefab):
///   1. Attach to the Airfield root (alongside Building, SelectableBuilding,
///      PowerConsumer, and a Collider).
///   2. Drag the Strike Jet prefab into Strike Jet Prefab.
///   3. Assign 6 child Transforms into Slots[0..5] — each represents one
///      parking pad on the apron. Their rotation drives the parked direction.
///   4. Tune costs in the Inspector.
///
/// Production controls (while the Airfield is selected):
///   • Click the Strike Jet button in the bottom-left production panel
///   • Or press J
/// </summary>
[RequireComponent(typeof(SelectableBuilding))]
public class Airfield : MonoBehaviour
{
    /// <summary>Hard cap on parking slots per Airfield, per spec.</summary>
    public const int MaxSlots = 6;

    // ------------------------------------------------------------------ //
    // Inspector — Strike Jet
    // ------------------------------------------------------------------ //

    [Header("Strike Jet Production")]
    [Tooltip("The Strike Jet prefab to instantiate")]
    public GameObject strikeJetPrefab;

    [Tooltip("Resource cost per Strike Jet")]
    public int strikeJetCost = 450;

    [Tooltip("Keyboard shortcut to produce a Strike Jet (UnitSelector hardcodes J)")]
    public KeyCode produceStrikeJetKey = KeyCode.J;

    // ------------------------------------------------------------------ //
    // Inspector — Slots
    // ------------------------------------------------------------------ //

    [Header("Parking Slots (exactly 6)")]
    [Tooltip("Transform children that mark each aircraft parking position. " +
             "Their rotation is used as the parked rotation.")]
    public Transform[] slots = new Transform[MaxSlots];

    // ------------------------------------------------------------------ //
    // Capability flag — used by the HUD
    // ------------------------------------------------------------------ //

    /// <summary>True when this airfield can produce a Strike Jet.</summary>
    public bool CanProduceStrikeJet => strikeJetPrefab != null;

    /// <summary>Number of slots that currently have no aircraft assigned (Unity-null counts as free).</summary>
    public int FreeSlotCount
    {
        get
        {
            int free = 0;
            for (int i = 0; i < parked.Length; i++)
                if (parked[i] == null) free++;
            return free;
        }
    }

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    /// <summary>Aircraft currently bound to each slot. Index aligns with <see cref="slots"/>.</summary>
    private readonly GameObject[] parked = new GameObject[MaxSlots];

    private PlayerResourceManager resourceManager;

    // ------------------------------------------------------------------ //

    private void Awake()
    {
        resourceManager = FindAnyObjectByType<PlayerResourceManager>();

        if (resourceManager == null)
            Debug.LogError($"Airfield on '{name}': No PlayerResourceManager found in scene.");

        if (slots == null || slots.Length != MaxSlots)
        {
            Debug.LogWarning($"Airfield on '{name}': Slots array has {(slots?.Length ?? 0)} entries " +
                             $"(expected {MaxSlots}). Re-run Air System → Validate Airfield Slots.");
        }
    }

    // ------------------------------------------------------------------ //
    // Public — called by RTSHUD / UnitSelector
    // ------------------------------------------------------------------ //

    /// <summary>Spawn one Strike Jet in the first free slot. No-op (logs) on failure.</summary>
    public void ProduceStrikeJet()
    {
        // --- Capability ---------------------------------------------------
        if (!CanProduceStrikeJet)
        {
            Debug.Log($"[Airfield] '{name}' has no Strike Jet prefab assigned — ignoring.");
            return;
        }

        if (resourceManager == null)
        {
            Debug.LogError($"[Airfield] Cannot produce Strike Jet: PlayerResourceManager missing.");
            return;
        }

        // --- Power --------------------------------------------------------
        PowerConsumer power = GetComponent<PowerConsumer>();
        if (power != null && !power.IsPowered)
        {
            Debug.LogWarning("[Power] Not enough power. Strike Jet production paused. " +
                             "Build a PowerPlant (P) to restore power.");
            return;
        }

        // --- Slot ---------------------------------------------------------
        int slotIndex = FindFreeSlotIndex();
        if (slotIndex < 0)
        {
            Debug.LogWarning("[Airfield] No free aircraft slots.");
            return;
        }
        Transform slot = slots[slotIndex];
        if (slot == null)
        {
            Debug.LogError($"[Airfield] Slot {slotIndex} Transform is missing — " +
                           "run Air System → Validate Airfield Slots.");
            return;
        }

        // --- Resources ----------------------------------------------------
        if (!resourceManager.CanAfford(strikeJetCost))
        {
            Debug.LogWarning($"[Airfield] Not enough resources to produce Strike Jet. " +
                             $"Need {strikeJetCost}, have {resourceManager.CurrentResources}.");
            return;
        }

        // --- Instantiate --------------------------------------------------
        GameObject jet = Instantiate(strikeJetPrefab, slot.position, slot.rotation);
        jet.name = $"StrikeJet_{slotIndex}";

        AirUnitController controller = jet.GetComponent<AirUnitController>();
        if (controller != null)
        {
            controller.AssignHome(this, slot);
        }
        else
        {
            Debug.LogWarning($"[Airfield] Strike Jet prefab is missing AirUnitController — " +
                             "aircraft will have no flight behaviour.");
        }

        parked[slotIndex] = jet;
        resourceManager.SpendResources(strikeJetCost);

        Debug.Log($"[Airfield] Strike Jet produced in slot {slotIndex} at {slot.position:F1}. " +
                  $"Free slots: {FreeSlotCount}/{MaxSlots}. " +
                  $"Remaining resources: {resourceManager.CurrentResources}.");
    }

    // ------------------------------------------------------------------ //
    // Slot bookkeeping
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the index of the first slot whose <see cref="parked"/> entry is
    /// null (Unity-null counts — destroyed aircraft auto-free their slot).
    /// Returns -1 if every slot is occupied.
    /// </summary>
    private int FindFreeSlotIndex()
    {
        for (int i = 0; i < parked.Length; i++)
        {
            if (slots[i] == null) continue;          // unassigned slot Transform — skip
            if (parked[i] == null) return i;          // Unity-null aircraft or never-assigned
        }
        return -1;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (slots == null) return;
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null) continue;
            Gizmos.DrawWireCube(slots[i].position, new Vector3(2f, 0.1f, 2f));
            UnityEditor.Handles.Label(slots[i].position + Vector3.up * 0.5f, $"Slot {i}");
        }
    }
#endif
}
