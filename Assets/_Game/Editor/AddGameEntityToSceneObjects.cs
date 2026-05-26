using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Stamps a <see cref="GameEntity"/> component onto every selectable
/// unit/building/aircraft already in the open scene that doesn't already
/// have one. Critical for the in-scene CommandCenter and any units the
/// designer placed manually before the prefab pass.
///
/// Menu: Tools → RTS → Multiplayer Prep → Add GameEntity To Scene Objects
///
/// Selection criteria (any one of these qualifies a GameObject):
///   • Has <see cref="SelectableUnit"/>      → entityType = Unit
///   • Has <see cref="SelectableAircraft"/>  → entityType = Aircraft
///   • Has <see cref="SelectableBuilding"/>  → entityType = Building
///   • Has <see cref="ResourceNode"/>        → entityType = Resource
///
/// Owner / team is auto-derived from the sibling <see cref="Health"/> if any
/// (Player → 0, Enemy → 1). Resource nodes default to NeutralOwnerId.
///
/// Phase 2 (multiplayer prep): each stamped entity gets a DETERMINISTIC id
/// derived from its hierarchy position
/// (<c>scene/&lt;rootName&gt;_&lt;rootIdx&gt;/&lt;childName&gt;_&lt;childIdx&gt;/...</c>).
/// Two clients running the same scene file get the same id for the same
/// object — required for cross-client command resolution.
/// </summary>
public static class AddGameEntityToSceneObjects
{
    [MenuItem("Tools/RTS/Multiplayer Prep/Add GameEntity To Scene Objects")]
    public static void Run()
    {
        Debug.Log("[AddGameEntityToScene] ── Stamping GameEntity onto scene objects ──");

        // Per-run dup tracker — guards against two GameObjects ending up
        // with the same deterministic id (would happen if a designer placed
        // two objects at exactly the same hierarchy slot, which Unity allows).
        var idsSeen = new HashSet<string>();

        int added   = 0;
        int updated = 0;

        added   += StampAll<SelectableUnit>(EntityType.Unit,     defaultPrefix: "Unit",     idsSeen, out int upd1); updated += upd1;
        added   += StampAll<SelectableAircraft>(EntityType.Aircraft, defaultPrefix: "Aircraft", idsSeen, out int upd2); updated += upd2;
        added   += StampAll<SelectableBuilding>(EntityType.Building, defaultPrefix: "Building", idsSeen, out int upd3); updated += upd3;
        added   += StampAll<ResourceNode>(EntityType.Resource,   defaultPrefix: "Resource", idsSeen, out int upd4); updated += upd4;

        if (added > 0 || updated > 0)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log($"[AddGameEntityToScene] ── Done. added {added}, updated {updated}, " +
                  $"deterministic ids assigned: {idsSeen.Count} ──");
    }

    private static int StampAll<T>(EntityType entityType, string defaultPrefix,
                                   HashSet<string> idsSeen, out int updated)
        where T : Component
    {
        T[] all = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        int added = 0;
        updated = 0;

        foreach (T comp in all)
        {
            if (comp == null) continue;
            GameObject go = comp.gameObject;

            GameEntity ge = go.GetComponent<GameEntity>();
            bool isNew = false;
            if (ge == null)
            {
                ge = go.AddComponent<GameEntity>();
                isNew = true;
            }

            // Always normalise the metadata so re-runs converge to the same
            // canonical values regardless of who edited the Inspector last.
            ge.entityType = entityType;
            if (string.IsNullOrEmpty(ge.prefabTypeId))
                ge.prefabTypeId = $"{defaultPrefix}:{go.name}";

            // Phase 4 fix — DO NOT clobber `overrideTeamFromHealth` unconditionally.
            // Upstream tools (Setup Multiplayer Match Map) stamp explicit
            // ownership and set `overrideTeamFromHealth=false` to signal "owner
            // is authoritative, don't re-derive from Health.team". Overwriting
            // that flag here and calling SyncFromHealth would flip Player 1's
            // Worker back to owner=0 (because its WorkerPrefab clone has
            // Health.team=Player), defeating the multi-player setup.

            // Phase 2: bake a DETERMINISTIC scene id so both clients agree on
            // the same id for the same GameObject.
            string deterministicId = BuildDeterministicSceneId(go);
            if (!idsSeen.Add(deterministicId))
            {
                Debug.LogWarning($"[AddGameEntityToScene] Duplicate deterministic id " +
                                 $"'{deterministicId}' for '{go.name}' — appending instance suffix.");
                deterministicId += $"-dup{go.GetInstanceID()}";
            }
            ge.EditorSetEntityId(deterministicId);

            // Resource nodes are always neutral — overwrite any stale owner.
            if (entityType == EntityType.Resource)
            {
                ge.ownerPlayerId          = GameEntity.NeutralOwnerId;
                ge.teamId                 = GameEntity.NeutralOwnerId;
                ge.overrideTeamFromHealth = false;
            }
            else if (ge.overrideTeamFromHealth)
            {
                // Default path: this entity hasn't been explicitly stamped, so
                // derive owner/team from sibling Health (SP / legacy flow).
                ge.SyncFromHealth();
            }
            else
            {
                // overrideTeamFromHealth==false → upstream tool stamped explicit
                // ownership. Preserve it.
                Debug.Log($"[AddGameEntityToScene]   Preserving explicit ownership on " +
                          $"'{go.name}' (owner {ge.ownerPlayerId}).");
            }

            EditorUtility.SetDirty(ge);
            if (isNew)
            {
                added++;
                Debug.Log($"[AddGameEntityToScene]   added → '{go.name}' ({entityType}, " +
                          $"owner {ge.ownerPlayerId}, id '{deterministicId}')");
            }
            else
            {
                updated++;
            }
        }

        return added;
    }

    /// <summary>
    /// Build a deterministic id from <paramref name="go"/>'s hierarchy path.
    /// Format: <c>scene/&lt;name&gt;_&lt;siblingIndex&gt;/&lt;name&gt;_&lt;siblingIndex&gt;/…</c>.
    /// Name embedded for readability; sibling index breaks ties when two
    /// siblings share a name (which Unity allows).
    /// </summary>
    private static string BuildDeterministicSceneId(GameObject go)
    {
        var parts = new List<string>();
        Transform t = go.transform;
        while (t != null)
        {
            parts.Add($"{t.name}_{t.GetSiblingIndex()}");
            t = t.parent;
        }
        parts.Reverse();
        return "scene/" + string.Join("/", parts);
    }
}
