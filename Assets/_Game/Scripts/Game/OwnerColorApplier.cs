using UnityEngine;

/// <summary>
/// Phase 10.7 — single canonical point of "apply owner color to this object".
/// Replaces the scattered ad-hoc <c>TeamColorMarker.ApplyColor</c> calls so
/// every transition (spawn, ApplyOwnership, APC load/unload, construction
/// complete, reactivation) routes through the same lookup:
///
///     <see cref="GameEntity.ownerPlayerId"/>
///         → <see cref="MultiplayerColors.ForOwnerOrDefault"/>
///             → every sibling <see cref="TeamColorMarker"/>.ApplyColor.
///
/// What it deliberately does NOT do:
///   • Use <c>Health.Team</c> — that's local-perspective and produces
///     different colors per client.
///   • Use the local player's selected color — also per-client.
///   • Use <c>PlayerFactionManager.SelectedColor</c> in MP — same reason.
/// </summary>
public static class OwnerColorApplier
{
    /// <summary>
    /// Re-paint every <see cref="TeamColorMarker"/> beneath
    /// <paramref name="go"/> with the color registered for the object's
    /// <see cref="GameEntity.ownerPlayerId"/>. Safe to call on inactive
    /// GameObjects — the marker's <c>MaterialPropertyBlock</c> persists
    /// and takes effect when the object is later re-activated.
    /// </summary>
    public static void ApplyToEntity(GameObject go)
    {
        if (go == null) return;

        GameEntity ge = go.GetComponent<GameEntity>();
        if (ge == null) ge = go.GetComponentInParent<GameEntity>();
        if (ge == null)
        {
            Debug.LogWarning($"[OwnerColor] No GameEntity on '{go.name}' — can't resolve owner.");
            return;
        }

        int ownerId = ge.ownerPlayerId;
        if (ownerId == GameEntity.NeutralOwnerId)
        {
            // Neutral entities (resource nodes) intentionally have no owner
            // color. Skip silently.
            return;
        }

        Color color = MultiplayerColors.ForOwnerOrDefault(ownerId);

        TeamColorMarker[] markers = go.GetComponentsInChildren<TeamColorMarker>(true);
        for (int i = 0; i < markers.Length; i++)
        {
            if (markers[i] == null) continue;
            // ApplyColor itself contains an MP-aware override that re-reads
            // the slot color from MultiplayerColors when present, so the
            // value we pass is a safe fallback. We still pass it explicitly
            // so SP also works (no MP registry entries).
            markers[i].ApplyColor(color);
        }

        // Phase 10.13 — also push through to every TeamColorApplier in the
        // hierarchy. The applier (used by SoldierVisualRoot for the
        // SkinnedMeshRenderer body slots) previously read
        // PlayerFactionManager.SelectedColor at OnEnable, which made every
        // soldier paint with the local client's pick instead of the unit's
        // owner color. The applier is now owner-aware (resolves via
        // GetColorForOwner internally), and this hook makes sure the
        // post-spawn ApplyOwnership pass also triggers it — closing the
        // race where OnEnable runs with the prefab-default owner=0 before
        // the dispatcher stamps the real owner.
        TeamColorApplier[] appliers = go.GetComponentsInChildren<TeamColorApplier>(true);
        for (int i = 0; i < appliers.Length; i++)
        {
            if (appliers[i] == null) continue;
            appliers[i].ForceRepaint();
        }

        Debug.Log($"[OwnerColor] Applied color RGB({color.r:F2},{color.g:F2},{color.b:F2}) " +
                  $"to entity={ge.EntityId} owner={ownerId} ('{go.name}') — " +
                  $"markers={markers.Length} appliers={appliers.Length}");
    }
}
