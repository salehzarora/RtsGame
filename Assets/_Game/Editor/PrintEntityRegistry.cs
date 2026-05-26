using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Diagnostic — dumps every entity currently in <see cref="EntityRegistry"/>
/// to the Console, grouped by owner. Use this during Play mode to confirm
/// that units/buildings registered correctly after pressing Play, or after
/// running the prefab/scene stamp tools.
///
/// Menu: Tools → RTS → Multiplayer Prep → Print Entity Registry
///
/// Edit-mode behaviour: the registry only fills during Play (Awake fires on
/// scene-instance components, not on asset components), so calling this
/// while not in Play prints a single warning line.
/// </summary>
public static class PrintEntityRegistry
{
    [MenuItem("Tools/RTS/Multiplayer Prep/Print Entity Registry")]
    public static void Run()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[PrintEntityRegistry] Editor is not in Play mode — the registry " +
                             "only populates at runtime (GameEntity.Awake). Enter Play and try again.");
            return;
        }

        List<GameEntity> all = EntityRegistry.All();
        if (all.Count == 0)
        {
            Debug.Log("[PrintEntityRegistry] Registry is empty. Have any GameEntity-bearing " +
                      "objects spawned this session?");
            return;
        }

        // Sort: owner ascending, then prefabTypeId, then name — gives a
        // predictable, scan-friendly listing.
        all.Sort((a, b) =>
        {
            if (a.ownerPlayerId != b.ownerPlayerId) return a.ownerPlayerId.CompareTo(b.ownerPlayerId);
            int t = string.Compare(a.prefabTypeId, b.prefabTypeId, System.StringComparison.Ordinal);
            if (t != 0) return t;
            return string.Compare(a.name, b.name, System.StringComparison.Ordinal);
        });

        StringBuilder sb = new StringBuilder(256);
        sb.AppendLine($"[PrintEntityRegistry] {all.Count} entities live:");

        int currentOwner = int.MinValue;
        foreach (GameEntity e in all)
        {
            if (e.ownerPlayerId != currentOwner)
            {
                currentOwner = e.ownerPlayerId;
                sb.AppendLine($"  ── Owner {currentOwner} ──");
            }
            sb.AppendLine($"    {e.EntityId}  {e.entityType,-9}  '{e.prefabTypeId}'  " +
                          $"team={e.teamId}  name='{e.name}'");
        }

        Debug.Log(sb.ToString());
    }
}
