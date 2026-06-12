using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lightweight tactical cover marker. Infantry standing within
/// <see cref="coverRadius"/> of any CoverObject takes reduced damage —
/// checked ONLY at damage time (see the hook in <see cref="Health.TakeDamage"/>),
/// never per-frame.
///
/// Setup: attached automatically to barriers and sandbag walls by the
/// battlefield dressing tools. Registry is static and self-maintaining via
/// OnEnable/OnDisable — multiplayer-safe because cover positions are scene
/// content identical on every client, and the reduction math is deterministic.
/// </summary>
public class CoverObject : MonoBehaviour
{
    [Tooltip("Units within this distance of the cover piece count as 'in cover'.")]
    public float coverRadius = 2.2f;

    [Tooltip("Fraction of incoming damage removed for infantry in cover (0.3 = 30%).")]
    [Range(0f, 0.8f)]
    public float damageReduction = 0.3f;

    private static readonly List<CoverObject> Registry = new List<CoverObject>();

    private void OnEnable()  { Registry.Add(this); }
    private void OnDisable() { Registry.Remove(this); }

    /// <summary>
    /// Returns true (with the strongest applicable reduction) when
    /// <paramref name="position"/> is inside any cover radius. O(n) over a
    /// handful of registered covers, called only when damage lands.
    /// </summary>
    public static bool TryGetReduction(Vector3 position, out float reduction)
    {
        reduction = 0f;
        for (int i = 0; i < Registry.Count; i++)
        {
            CoverObject c = Registry[i];
            if (c == null) continue;
            float r = c.coverRadius;
            if ((c.transform.position - position).sqrMagnitude <= r * r && c.damageReduction > reduction)
                reduction = c.damageReduction;
        }
        return reduction > 0f;
    }

    /// <summary>
    /// Finds the nearest registered cover within <paramref name="maxDist"/> and
    /// returns a stand-at point on its near side. Used by AI cover-seeking.
    /// </summary>
    public static bool FindNearestPoint(Vector3 from, float maxDist, out Vector3 point)
    {
        point = from;
        CoverObject best = null;
        float bestD = maxDist;
        for (int i = 0; i < Registry.Count; i++)
        {
            CoverObject c = Registry[i];
            if (c == null) continue;
            float d = Vector3.Distance(from, c.transform.position);
            if (d < bestD) { bestD = d; best = c; }
        }
        if (best == null) return false;
        Vector3 dir = (from - best.transform.position).normalized;
        point = best.transform.position + dir * Mathf.Min(0.9f, best.coverRadius * 0.5f);
        return true;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.3f, 0.8f, 0.4f, 0.4f);
        Gizmos.DrawWireSphere(transform.position, coverRadius);
    }
#endif
}
