using UnityEngine;

/// <summary>
/// PHASE E — watch tower. A small-capacity <see cref="GarrisonBuilding"/> that
/// is NEUTRAL until infantry occupy it, then it's "captured" by that player:
///   • A capture indicator renderer is tinted to the occupying owner's color
///     (neutral grey when empty).
///   • Occupants fire from height with a LONGER range than open-ground infantry
///     (the garrison fire system, with a bigger fireRange).
///   • <see cref="visionRadius"/> is a placeholder for a future fog-of-war
///     reveal — there is no FoW system in the project yet, so this only draws a
///     gizmo and logs the intended reveal radius. TODO: wire to FoW when added.
///
/// Capture / occupancy syncs through the same GarrisonEnter / GarrisonExit
/// events as a normal garrison, so both clients agree on who holds the tower.
///
/// Setup (or use Tools → RTS → Map → Create Watch Tower):
///   Add alongside GameEntity (entityType = MapObject, owner = −1). Optionally
///   add a DestructibleMapObject sibling to make the tower destructible.
/// </summary>
public class WatchTower : GarrisonBuilding
{
    [Header("Watch Tower — Vision (placeholder)")]
    [Tooltip("Intended sight radius granted to the occupying player. PLACEHOLDER " +
             "— there is no fog-of-war system yet, so this only draws a gizmo " +
             "and is logged on capture. TODO: reveal this radius when FoW exists.")]
    public float visionRadius = 30f;

    [Header("Watch Tower — Capture Indicator")]
    [Tooltip("Renderer tinted to the occupying owner's color (neutral when " +
             "empty). Optional — a flag mesh or a band on the tower works well.")]
    public Renderer ownerIndicatorRenderer;

    [Tooltip("Color shown on the indicator while the tower is unoccupied / neutral.")]
    [ColorUsage(false)] public Color neutralColor = new Color(0.6f, 0.6f, 0.6f);

    private MaterialPropertyBlock mpb;
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId     = Shader.PropertyToID("_Color");

    // ------------------------------------------------------------------ //
    // Editor defaults
    // ------------------------------------------------------------------ //

    private void Reset()
    {
        capacity            = 2;
        infantryOnly        = true;
        allowVehicles       = false;
        canFireFromBuilding = true;
        fireRange           = 26f;   // height advantage over open-ground infantry
        damagePerOccupant   = 6f;
    }

    protected override void Awake()
    {
        base.Awake();
        RefreshIndicator();   // start neutral
    }

    // ------------------------------------------------------------------ //
    // Capture indicator
    // ------------------------------------------------------------------ //

    protected override void OnOccupancyChanged()
    {
        RefreshIndicator();

        if (IsOccupied)
            Debug.Log($"[WatchTower] '{name}' captured by player {OccupyingOwnerId}. " +
                      $"(Vision radius {visionRadius} — placeholder; no FoW system yet.)");
        else
            Debug.Log($"[WatchTower] '{name}' is now neutral (empty).");
    }

    private void RefreshIndicator()
    {
        if (ownerIndicatorRenderer == null) return;

        Color c = IsOccupied
            ? MultiplayerColors.ForOwnerOrDefault(OccupyingOwnerId)
            : neutralColor;

        if (mpb == null) mpb = new MaterialPropertyBlock();
        ownerIndicatorRenderer.GetPropertyBlock(mpb);
        mpb.SetColor(BaseColorId, c);   // URP Lit
        mpb.SetColor(ColorId, c);       // Standard / built-in
        ownerIndicatorRenderer.SetPropertyBlock(mpb);
    }

    // ------------------------------------------------------------------ //
    // Gizmo — visualise the (placeholder) vision radius
    // ------------------------------------------------------------------ //

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        UnityEditor.Handles.color = new Color(0.3f, 0.8f, 1f, 0.25f);
        UnityEditor.Handles.DrawWireDisc(transform.position, Vector3.up, visionRadius);
    }
#endif
}
