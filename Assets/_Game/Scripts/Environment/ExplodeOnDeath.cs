using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Battlefield hazard prop behaviour — when the sibling <see cref="Health"/>
/// dies, play a medium explosion (CombatVFX) AND deal real area damage to
/// nearby units/buildings. Used by destructible props (fuel barrels, ammo
/// crates) placed by the battlefield dressing tools.
///
/// Area damage rules:
///   • Linear falloff: full damage at the centre → 40% at the radius edge.
///   • Hits each Health at most once (parent-deduped), skips itself and
///     anything already dead.
///   • Team-agnostic: a hazard hurts everyone near it — that's what makes
///     barrel placement tactical.
///   • Multiplayer-safe the same way all damage here is: every client runs
///     the same local damage, the master's broadcast snaps divergence.
///
/// Setup: attach next to a Health component; tune radius/damage per prop.
/// </summary>
[RequireComponent(typeof(Health))]
public class ExplodeOnDeath : MonoBehaviour
{
    [Tooltip("Explosion size passed to CombatVFX: 0 small, 1 medium, 2 large.")]
    public int explosionSize = 1;

    [Tooltip("Radius of the scorch decal left on the ground.")]
    public float scorchRadius = 1.1f;

    [Header("Area damage")]
    [Tooltip("Gameplay damage radius. 0 disables area damage (visual only).")]
    public float explosionRadius = 4f;

    [Tooltip("Damage at the centre of the blast; falls off linearly to 40% at the edge.")]
    public float explosionDamage = 35f;

    [Tooltip("Damage mobile units (anything with Health that isn't a Building).")]
    public bool affectUnits = true;

    [Tooltip("Damage buildings caught in the blast.")]
    public bool affectBuildings = true;

    private Health health;
    private static readonly Collider[] OverlapBuf = new Collider[64];

    private void OnEnable()
    {
        health = GetComponent<Health>();
        if (health != null) health.OnDeath += HandleDeath;
    }

    private void OnDisable()
    {
        if (health != null) health.OnDeath -= HandleDeath;
    }

    private void HandleDeath()
    {
        Vector3 pos = transform.position;

        // Visuals first (they don't depend on what the damage below destroys).
        CombatVFX.Explosion(pos + Vector3.up * 0.4f, explosionSize);
        CombatVFX.ScorchMark(pos, scorchRadius);
        CombatVFX.LingeringSmoke(pos, 5f, 0.7f);

        if (explosionRadius <= 0f || explosionDamage <= 0f) return;

        int n = Physics.OverlapSphereNonAlloc(pos, explosionRadius, OverlapBuf);
        var hit = new HashSet<Health>();
        for (int i = 0; i < n; i++)
        {
            Health h = OverlapBuf[i] != null ? OverlapBuf[i].GetComponentInParent<Health>() : null;
            if (h == null || h == health || hit.Contains(h)) continue;
            if (h.CurrentHealth <= 0f) continue;

            bool isBuilding = h.GetComponent<Building>() != null;
            if (isBuilding && !affectBuildings) continue;
            if (!isBuilding && !affectUnits) continue;

            hit.Add(h);
            float dist = Vector3.Distance(pos, h.transform.position);
            float k = Mathf.Lerp(1f, 0.4f, Mathf.Clamp01(dist / explosionRadius));
            h.TakeDamage(explosionDamage * k);
        }
        if (hit.Count > 0)
            Debug.Log("[Hazard] '" + name + "' exploded — damaged " + hit.Count + " target(s) in r=" + explosionRadius + ".");
    }
}
