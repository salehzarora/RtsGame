using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Phase 10.11 — single canonical place that tears down a finished match so
/// the next one starts fresh. Triggered from the ESC pause menu's
/// "Main Menu" button and from the lobby's "Back to Main Menu" button.
///
/// What it resets, in order:
///   1. Destroys every RUNTIME-spawned match entity (units that came from
///      Produce / Build / APC unload / construction completion). Scene-baked
///      entities under Player0Base / Player1Base / ResourceNodes are kept so
///      the existing scene survives — they get re-hidden by GameplayWorldRoot.
///   2. Clears the per-owner slot color map (<see cref="MultiplayerColors"/>)
///      so a new match's MatchStart re-populates without stale colors
///      bleeding in.
///   3. Resets the deterministic entity-id allocator counter
///      (<see cref="NetworkEntityIdAllocator"/>) so the next match's ids
///      start from a clean low range.
///   4. Resets <see cref="NetworkMatchCoordinator"/>'s match flags so
///      <c>IsMatchStarted</c> is false again.
///
/// What it deliberately does NOT do:
///   • Reload the scene. Manager singletons (NetworkManagerRTS, command
///     relay, lobby UI) need to survive — a scene reload kills them.
///   • Clear <see cref="ResourceBank"/>. The per-base PlayerResourceManager
///     instances are scene-baked; deactivating + re-activating them does NOT
///     re-fire their Awake registration, so clearing the bank would orphan
///     them. Banks are re-seeded by MatchStart's
///     <c>ResourceBank.SetCurrent</c> calls in <see cref="NetworkMatchCoordinator.ApplyMatchStartLocally"/>.
///   • Disconnect from the Photon master. We only leave the ROOM (handled
///     by the menu return path before this resetter runs).
/// </summary>
public static class MatchSessionResetter
{
    /// <summary>
    /// Tear down the current match's runtime state. Idempotent — calling
    /// twice is safe.
    /// </summary>
    public static void ResetForNewMatch()
    {
        Debug.Log("[MatchReset] ── Reset for new match ──");

        int destroyed = DestroyRuntimeSpawnedEntities();
        Debug.Log($"[MatchReset] Destroyed {destroyed} runtime match objects.");

        // Clear per-slot color map. The next match's MatchStart event will
        // re-populate this from the lobby's Photon player properties, so
        // no stale "last match's red" survives.
        MultiplayerColors.Clear();
        Debug.Log("[MatchReset] Cleared MultiplayerColors slot map.");

        // Reset deterministic id allocator so the next match's spawn ids
        // (p0-1, p1-1, …) don't carry counter offsets from the previous
        // match. Helps logs / cross-client debugging stay readable.
        NetworkEntityIdAllocator.ResetForTests();
        Debug.Log("[MatchReset] Reset NetworkEntityIdAllocator counter.");

        // Reset match coordinator flags so a future StartMatch fires fresh.
        if (NetworkMatchCoordinator.Instance != null)
            NetworkMatchCoordinator.Instance.ResetForNewMatch();

        Debug.Log("[MatchReset] Cleared ownership/color registries.");
    }

    /// <summary>
    /// Walks every registered <see cref="GameEntity"/> and destroys the ones
    /// that look like runtime spawns (produced units, constructed buildings,
    /// APC passengers, projectiles). Returns the count destroyed.
    ///
    /// Heuristic: an entity is a "runtime spawn" if it's NOT under one of
    /// the scene-baked roots (Player0Base / Player1Base / ResourceNodes /
    /// Environment / EnemyStart / PlayerStart). Anything that survives the
    /// match (the bases themselves, the original Worker spawned inside
    /// them, scene-baked CommandCenters) is preserved so the existing scene
    /// can serve the next match too.
    /// </summary>
    private static int DestroyRuntimeSpawnedEntities()
    {
        // Names of roots whose children are SCENE-BAKED and should survive
        // a match reset. Anything outside these is a runtime spawn.
        var sceneRoots = new HashSet<string>
        {
            "Player0Base", "Player1Base",
            "Environment", "ResourceNodes",
            "PlayerStart", "EnemyStart",
        };

        List<GameEntity> all = new List<GameEntity>(EntityRegistry.All());
        int destroyed = 0;
        foreach (GameEntity ge in all)
        {
            if (ge == null) continue;

            // Resources stay (they're scene props).
            if (ge.entityType == EntityType.Resource) continue;

            // Map objects (destructible bridges, fuel tanks, garrison buildings,
            // watch towers, tunnels) are NEUTRAL, scene-baked battlefield
            // landmarks — like resources, they must SURVIVE a match reset
            // (their per-match state is reset separately by
            // MatchSessionManager.ResetMapInteractables). Destroying them here
            // would wipe the battlefield permanently for the play session.
            if (ge.entityType == EntityType.MapObject) continue;

            if (IsUnderSceneRoot(ge.transform, sceneRoots))
            {
                // Scene-baked unit/building. Keep — the scene root will be
                // re-hidden by GameplayWorldRoot.ReHide(), and a future
                // MatchStart will re-show + re-apply ownership/color via
                // OwnerColorApplier.
                continue;
            }

            // Runtime spawn — produced unit, constructed building, APC
            // passenger that was instantiated by APCTransport, etc.
            Debug.Log($"[MatchReset] Destroying runtime entity '{ge.name}' " +
                      $"(id={ge.EntityId}, owner={ge.ownerPlayerId}, type={ge.entityType})");
            Object.Destroy(ge.gameObject);
            destroyed++;
        }
        return destroyed;
    }

    private static bool IsUnderSceneRoot(Transform t, HashSet<string> rootNames)
    {
        Transform cur = t;
        while (cur != null)
        {
            if (rootNames.Contains(cur.name)) return true;
            cur = cur.parent;
        }
        return false;
    }
}
