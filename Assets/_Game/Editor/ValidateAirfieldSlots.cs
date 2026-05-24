using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Read-only sanity check for every Airfield (prefab asset + scene instance).
/// Reports any that don't have exactly <see cref="Airfield.MaxSlots"/> slot
/// Transforms assigned. Nothing is modified — fix issues with
/// Tools → RTS → Air System → Repair Airfield Setup or rebuild the prefab.
///
/// Menu: Tools → RTS → Air System → Validate Airfield Slots
/// </summary>
public static class ValidateAirfieldSlots
{
    [MenuItem("Tools/RTS/Air System/Validate Airfield Slots")]
    public static void Validate()
    {
        Debug.Log("[ValidateAirfieldSlots] ── Scanning ──");

        int ok = 0;
        int bad = 0;

        // --- Prefab assets ------------------------------------------------
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
        foreach (string guid in prefabGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) continue;

            Airfield af = root.GetComponent<Airfield>();
            if (af == null) continue;

            if (ReportSlots(af, path))  ok++;
            else                        bad++;
        }

        // --- Scene instances ---------------------------------------------
        Airfield[] sceneAirfields = Object.FindObjectsByType<Airfield>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (Airfield af in sceneAirfields)
        {
            if (af == null) continue;
            string label = $"[scene] {af.gameObject.name}";
            if (ReportSlots(af, label)) ok++;
            else                        bad++;
        }

        Debug.Log($"[ValidateAirfieldSlots] ✓ Done. Healthy Airfields: {ok}. Problems: {bad}.");
    }

    /// <summary>
    /// Returns true when the airfield has exactly <see cref="Airfield.MaxSlots"/>
    /// non-null slot Transforms; otherwise logs a warning per problem and returns false.
    /// </summary>
    private static bool ReportSlots(Airfield af, string label)
    {
        if (af.slots == null)
        {
            Debug.LogWarning($"[ValidateAirfieldSlots] ⚠ {label}: slots[] is null. " +
                             "Re-run Air System → Repair Airfield Setup.");
            return false;
        }

        if (af.slots.Length != Airfield.MaxSlots)
        {
            Debug.LogWarning($"[ValidateAirfieldSlots] ⚠ {label}: slots[] has {af.slots.Length} entries " +
                             $"(expected {Airfield.MaxSlots}).");
            return false;
        }

        int missing = 0;
        for (int i = 0; i < af.slots.Length; i++)
            if (af.slots[i] == null) missing++;

        if (missing > 0)
        {
            Debug.LogWarning($"[ValidateAirfieldSlots] ⚠ {label}: {missing}/{Airfield.MaxSlots} slot Transforms " +
                             "are unassigned. Re-run Air System → Create Airfield Prefab to rebuild geometry.");
            return false;
        }

        Debug.Log($"[ValidateAirfieldSlots]   ✓ {label}: {Airfield.MaxSlots}/{Airfield.MaxSlots} slots OK.");
        return true;
    }
}
