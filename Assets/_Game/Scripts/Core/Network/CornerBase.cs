using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Marks one of the four map-corner starting areas (A / B / C / D) and holds
/// references to everything that area contains: a visible start marker, the
/// starting Dozer, the resource cluster, and the per-owner resource bank.
///
/// DECOUPLING: the corner is a FIXED location with a FIXED <see cref="cornerIndex"/>
/// (0=A, 1=B, 2=C, 3=D). It is NOT permanently owned by any player. At match
/// start the coordinator decides which player (slot 0..3) is assigned to which
/// corner and calls <see cref="AssignOwner"/>; unused corners are simply left
/// hidden and never assigned. This is what lets the host land on any corner
/// instead of always corner A.
///
/// Built by <c>Tools → RTS → Match → Setup Multiplayer Match Map</c>. In the
/// editor the corners are ALWAYS visible (the gizmos below draw the labelled
/// A/B/C/D markers in the Scene view) so you can verify the layout without
/// pressing Play. Runtime reveal/hide is handled separately by
/// <see cref="GameplayWorldRoot"/>.
/// </summary>
[DisallowMultipleComponent]
public class CornerBase : MonoBehaviour
{
    [Header("Identity")]
    [Tooltip("Fixed corner index: 0=A, 1=B, 2=C, 3=D. Set by the setup tool.")]
    public int cornerIndex;

    [Header("References (auto-wired by Setup Multiplayer Match Map)")]
    [Tooltip("Visible flag/marker primitive at the corner centre.")]
    public Transform startMarker;

    [Tooltip("Starting Dozer/builder for whoever is assigned this corner. May " +
             "be a placeholder cube if the Dozer prefab was missing at bake time.")]
    public GameObject dozer;

    [Tooltip("Parent of this corner's ResourceNode cluster.")]
    public Transform resourceCluster;

    [Tooltip("Per-owner PlayerResourceManager for this corner.")]
    public PlayerResourceManager bank;

    [Header("Runtime state")]
    [Tooltip("Player slot (0..3) currently assigned to this corner THIS match; " +
             "-1 = unassigned/empty (nothing spawns here).")]
    public int assignedOwnerId = -1;

    // Per-corner identity colours (A blue, B red, C green, D yellow).
    private static readonly Color[] CornerColors =
    {
        new Color(0.20f, 0.55f, 1.00f), // A
        new Color(0.92f, 0.20f, 0.20f), // B
        new Color(0.30f, 0.80f, 0.35f), // C
        new Color(0.95f, 0.80f, 0.20f), // D
    };

    /// <summary>Letter label for this corner ('A'..'D').</summary>
    public char Letter => (char)('A' + Mathf.Clamp(cornerIndex, 0, 25));

    /// <summary>Identity colour for this corner.</summary>
    public Color CornerColor =>
        CornerColors[Mathf.Clamp(cornerIndex, 0, CornerColors.Length - 1)];

    // ------------------------------------------------------------------ //
    // Ownership assignment (called at match start by the coordinator)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Assign this corner to a player slot. Re-stamps the bank + every owned
    /// child <see cref="GameEntity"/> (Dozer, future buildings) with
    /// <paramref name="playerId"/>. Resource nodes stay neutral. Does NOT call
    /// ApplyOwnership here — the caller runs
    /// <see cref="GameEntity.ReinitializeAllForNewMatch"/> afterwards so team
    /// perspective / colour / movement gates all re-evaluate together.
    /// </summary>
    public void AssignOwner(int playerId)
    {
        assignedOwnerId = playerId;

        if (bank != null) bank.ownerPlayerId = playerId;

        GameEntity[] ents = GetComponentsInChildren<GameEntity>(true);
        for (int i = 0; i < ents.Length; i++)
        {
            GameEntity ge = ents[i];
            if (ge == null) continue;
            if (ge.entityType == EntityType.Resource) continue; // resources stay neutral
            ge.ownerPlayerId = playerId;
            ge.teamId        = playerId;
        }

        Debug.Log($"[CornerBase] Corner {Letter} (index {cornerIndex}) assigned to player {playerId}.");
    }

    /// <summary>Mark this corner unassigned (no player this match).</summary>
    public void ClearOwner() => assignedOwnerId = -1;

    // ------------------------------------------------------------------ //
    // Always-on Scene-view gizmos so the 4 corners are obvious in the editor
    // ------------------------------------------------------------------ //

    private void OnDrawGizmos()
    {
        Color c = CornerColor;
        Gizmos.color = new Color(c.r, c.g, c.b, 0.9f);

        Vector3 baseP = transform.position;
        Vector3 topP  = baseP + Vector3.up * 7f;

        Gizmos.DrawWireSphere(baseP, 6f);   // footprint ring
        Gizmos.DrawLine(baseP, topP);       // flag pole
        Gizmos.DrawSphere(topP, 1.5f);      // flag head

#if UNITY_EDITOR
        var style = new GUIStyle
        {
            fontSize  = 22,
            fontStyle = FontStyle.Bold,
        };
        style.normal.textColor = c;
        Handles.Label(topP + Vector3.up * 2f, $"Corner {Letter}", style);
#endif
    }
}
