using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Process-wide directory of every live <see cref="GameEntity"/>.
///
/// GameEntity instances self-register on Awake and self-unregister on
/// OnDestroy — no scene setup is needed; the registry just exists. Future
/// network code will call <see cref="Find"/> to resolve incoming commands
/// (e.g. "move entity 4a8b… to point P") back to the local <see cref="GameEntity"/>.
///
/// Design choice — static class, not MonoBehaviour singleton: the registry
/// is pure metadata, has no Update tick, and needs no Inspector. Keeping it
/// static avoids "what scene owns it?" questions and dodges the singleton-
/// teardown ordering pitfalls that bite when a scene reloads.
///
/// Scene-reload behaviour: every GameEntity's OnDestroy fires, which calls
/// <see cref="Unregister"/>, so the table empties before the new scene's
/// Awake pass starts re-populating it. If you need a hard reset (e.g. after
/// an Editor-only state leak), call <see cref="Clear"/>.
/// </summary>
public static class EntityRegistry
{
    // ------------------------------------------------------------------ //
    // Internal store
    // ------------------------------------------------------------------ //

    private static readonly Dictionary<string, GameEntity> byId
        = new Dictionary<string, GameEntity>(256);

    // ------------------------------------------------------------------ //
    // Public read-only count for diagnostics.
    // ------------------------------------------------------------------ //

    public static int Count => byId.Count;

    // ------------------------------------------------------------------ //
    // Mutation — only called by GameEntity itself
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Adds <paramref name="entity"/> to the registry. Logs a warning if the
    /// entity id collides with an existing one (should be impossible with
    /// GUIDs but the check guards against editor-time copy/paste duplicates).
    /// </summary>
    public static void Register(GameEntity entity)
    {
        if (entity == null) return;

        string id = entity.EntityId;
        if (string.IsNullOrEmpty(id))
        {
            Debug.LogWarning($"[EntityRegistry] '{entity.name}' tried to register " +
                             "with an empty EntityId. Did Awake fail to generate one?");
            return;
        }

        if (byId.TryGetValue(id, out GameEntity existing) && existing != null && existing != entity)
        {
            Debug.LogWarning($"[EntityRegistry] Duplicate entity id '{id}' detected. " +
                             $"Existing: '{existing.name}', incoming: '{entity.name}'. " +
                             "The incoming entity is overwriting the slot — check for a baked " +
                             "EntityId on a duplicated prefab. Run Tools → RTS → Multiplayer " +
                             "Prep → Add GameEntity To Prefabs to refresh ids.");
        }

        byId[id] = entity;
    }

    /// <summary>
    /// Removes <paramref name="entity"/> from the registry. Safe to call
    /// even if the entity was never registered or has been replaced by a
    /// different one sharing the same id (we only unset the slot if it
    /// currently holds this specific entity).
    /// </summary>
    public static void Unregister(GameEntity entity)
    {
        if (entity == null) return;

        string id = entity.EntityId;
        if (string.IsNullOrEmpty(id)) return;

        if (byId.TryGetValue(id, out GameEntity current) && current == entity)
            byId.Remove(id);
    }

    /// <summary>
    /// Hard-clears the registry. Intended for editor-only state recovery —
    /// gameplay code should rely on per-entity OnDestroy to keep the table tidy.
    /// </summary>
    public static void Clear() => byId.Clear();

    // ------------------------------------------------------------------ //
    // Lookup
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the entity matching <paramref name="id"/>, or null if no live
    /// entity carries that id. A destroyed-but-not-yet-collected Unity object
    /// is also treated as null (overloaded == check).
    /// </summary>
    public static GameEntity Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (!byId.TryGetValue(id, out GameEntity e)) return null;
        return e != null ? e : null;
    }

    /// <summary>
    /// Allocates and returns the subset of registered entities owned by
    /// <paramref name="ownerId"/>. Allocates a new list each call — fine for
    /// debug tooling; cache the result if you'll call this in a tight loop.
    /// </summary>
    public static List<GameEntity> GetByOwner(int ownerId)
    {
        var list = new List<GameEntity>();
        foreach (GameEntity e in byId.Values)
            if (e != null && e.ownerPlayerId == ownerId) list.Add(e);
        return list;
    }

    /// <summary>Same as <see cref="GetByOwner"/> but filtered on team.</summary>
    public static List<GameEntity> GetByTeam(int teamId)
    {
        var list = new List<GameEntity>();
        foreach (GameEntity e in byId.Values)
            if (e != null && e.teamId == teamId) list.Add(e);
        return list;
    }

    /// <summary>
    /// Snapshot of every live entity. Used by the Print Entity Registry tool.
    /// </summary>
    public static List<GameEntity> All()
    {
        var list = new List<GameEntity>(byId.Count);
        foreach (GameEntity e in byId.Values)
            if (e != null) list.Add(e);
        return list;
    }
}
