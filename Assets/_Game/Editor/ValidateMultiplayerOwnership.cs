using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Diagnostic sweep for ownership / id consistency in the active scene.
///
/// Menu: Tools → RTS → Multiplayer → Validate Multiplayer Ownership
///
/// Run it AT ANY TIME — works in Edit mode (reports the scene-baked state)
/// and in Play mode (reports the live runtime state including produced /
/// constructed entities). Reports:
///
///   • Total GameEntity count per owner.
///   • Any GameEntity with <see cref="GameEntity.NeutralOwnerId"/> that
///     isn't a resource node.
///   • Any GameEntity with an empty <see cref="GameEntity.EntityId"/>.
///   • Duplicate <see cref="GameEntity.EntityId"/> across two or more
///     objects (the registry can only ever hold one — the other was
///     silently shadowed).
///   • Any <see cref="WorkerGatherer"/> whose
///     <c>commandCenter.OwnerPlayerId</c> doesn't match the worker's owner
///     (the deposit-wrong-CC symptom from Bug 1).
///   • Any <see cref="APCTransport"/> passenger whose owner doesn't match
///     the APC's owner (the unloaded-soldiers-wrong-color symptom from Bug 3).
///   • In MP: any unit whose <see cref="Health"/>.team disagrees with its
///     local-perspective expected team (own → Player, opponent → Enemy).
///
/// Nothing is mutated — pure diagnostic. Re-run after fixes.
/// </summary>
public static class ValidateMultiplayerOwnership
{
    [MenuItem("Tools/RTS/Multiplayer/Validate Multiplayer Ownership")]
    public static void Run()
    {
        Debug.Log("[ValidateOwnership] ══════════════════════════════════════════════");
        Debug.Log("[ValidateOwnership] Sweeping scene for ownership / id consistency...");

        GameEntity[] all = Object.FindObjectsByType<GameEntity>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        Debug.Log($"[ValidateOwnership] Found {all.Length} GameEntity(s) in scene.");

        // -------- Tally by owner --------------------------------------- //
        Dictionary<int, int> perOwner = new Dictionary<int, int>();
        Dictionary<string, List<GameEntity>> perId = new Dictionary<string, List<GameEntity>>();
        int neutralUnit = 0;
        int emptyId     = 0;

        foreach (GameEntity ge in all)
        {
            if (ge == null) continue;

            int o = ge.ownerPlayerId;
            perOwner[o] = perOwner.TryGetValue(o, out int n) ? n + 1 : 1;

            string id = ge.EntityId ?? "";
            if (!perId.TryGetValue(id, out List<GameEntity> list))
            {
                list = new List<GameEntity>();
                perId[id] = list;
            }
            list.Add(ge);

            if (string.IsNullOrEmpty(id))
            {
                Debug.LogWarning($"[ValidateOwnership]   ✗ Empty entityId on '{ge.name}' " +
                                 $"(owner {o}, type {ge.entityType}).");
                emptyId++;
            }

            if (o == GameEntity.NeutralOwnerId
                && ge.entityType != EntityType.Resource
                && ge.entityType != EntityType.MapObject)
            {
                Debug.LogWarning($"[ValidateOwnership]   ✗ Neutral-owner non-resource: " +
                                 $"'{ge.name}' (type {ge.entityType}). Should this be claimed " +
                                 "by a player?");
                neutralUnit++;
            }
        }

        foreach (var kv in perOwner)
            Debug.Log($"[ValidateOwnership]   owner={kv.Key}: {kv.Value} entity(s)");

        // -------- Duplicate ids ----------------------------------------- //
        int duplicates = 0;
        foreach (var kv in perId)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            if (kv.Value.Count <= 1) continue;
            duplicates++;
            string names = string.Join(", ",
                kv.Value.ConvertAll(g => g != null ? g.name : "<null>").ToArray());
            Debug.LogWarning($"[ValidateOwnership]   ✗ Duplicate entityId '{kv.Key}' on " +
                             $"{kv.Value.Count} object(s): {names}");
        }

        // -------- Worker deposit target check --------------------------- //
        WorkerGatherer[] workers = Object.FindObjectsByType<WorkerGatherer>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int workerMismatch = 0;
        foreach (WorkerGatherer w in workers)
        {
            if (w == null) continue;
            int wOwner = w.GetComponent<GameEntity>()?.ownerPlayerId ?? GameEntity.NeutralOwnerId;
            if (w.commandCenter == null) continue;     // unbound, will lazy-resolve at runtime
            int ccOwner = w.commandCenter.OwnerPlayerId;
            if (ccOwner == wOwner) continue;
            Debug.LogWarning($"[ValidateOwnership]   ✗ Worker '{w.name}' (owner {wOwner}) " +
                             $"bound to CommandCenter owner {ccOwner}. Deposit would credit " +
                             "the wrong bank.");
            workerMismatch++;
        }

        // -------- APC passenger owner check ----------------------------- //
        APCTransport[] apcs = Object.FindObjectsByType<APCTransport>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int paxMismatch = 0;
        foreach (APCTransport apc in apcs)
        {
            if (apc == null) continue;
            int apcOwner = apc.GetComponent<GameEntity>()?.ownerPlayerId ?? GameEntity.NeutralOwnerId;
            IReadOnlyList<GameObject> pax = apc.Passengers;
            for (int i = 0; i < pax.Count; i++)
            {
                GameObject p = pax[i];
                if (p == null) continue;
                int pOwner = p.GetComponent<GameEntity>()?.ownerPlayerId ?? GameEntity.NeutralOwnerId;
                if (pOwner == apcOwner) continue;
                Debug.LogWarning($"[ValidateOwnership]   ✗ APC '{apc.name}' (owner {apcOwner}) " +
                                 $"carries passenger '{p.name}' with owner {pOwner}.");
                paxMismatch++;
            }
        }

        // -------- MP: Health.team vs local-perspective check ------------ //
        int teamMismatch = 0;
        if (Application.isPlaying && NetworkManagerRTS.IsMultiplayerEnabled)
        {
            int localPid = NetworkManagerRTS.LocalPlayerId;
            foreach (GameEntity ge in all)
            {
                if (ge == null) continue;
                if (ge.entityType == EntityType.Resource) continue;
                Health h = ge.GetComponent<Health>();
                if (h == null) continue;
                Health.Team expected = (ge.ownerPlayerId == localPid)
                    ? Health.Team.Player : Health.Team.Enemy;
                if (h.team == expected) continue;
                Debug.LogWarning($"[ValidateOwnership]   ✗ '{ge.name}' owner={ge.ownerPlayerId} " +
                                 $"local={localPid} expected Health.team={expected} but got {h.team}.");
                teamMismatch++;
            }
        }

        // -------- Summary ----------------------------------------------- //
        int issues = neutralUnit + emptyId + duplicates + workerMismatch + paxMismatch + teamMismatch;
        if (issues == 0)
        {
            Debug.Log("[ValidateOwnership] ✓ No issues found.");
        }
        else
        {
            Debug.LogWarning($"[ValidateOwnership] ✗ Found {issues} issue(s): " +
                             $"{neutralUnit} neutral-unit, {emptyId} empty-id, {duplicates} duplicate-id, " +
                             $"{workerMismatch} worker-cc-mismatch, {paxMismatch} apc-passenger-mismatch, " +
                             $"{teamMismatch} health-team-mismatch.");
        }
        Debug.Log("[ValidateOwnership] ══════════════════════════════════════════════");
    }
}
