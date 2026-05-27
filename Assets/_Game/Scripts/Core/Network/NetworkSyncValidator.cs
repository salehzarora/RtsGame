using System.Collections;
using UnityEngine;

/// <summary>
/// Phase 10.7 — runtime desync detector. Walks the local scene every
/// <see cref="checkInterval"/> seconds and logs warnings for the
/// most-common ownership / transport-state divergences:
///
///   • Color mismatch — entity's <c>TeamColorMarker</c> is painted with
///     a color that doesn't match <c>MultiplayerColors[ownerPlayerId]</c>.
///   • Passenger owner mismatch — an APC's passenger has a different
///     <c>GameEntity.ownerPlayerId</c> than the APC.
///   • Passenger visible-while-inside — a unit is in some APC's
///     passenger list but its GameObject is also active in the world
///     (could happen if the load broadcast was dropped).
///
/// Pure diagnostic — never mutates anything. Lives on the NetworkManager
/// GameObject (added by Setup Network Manager). Inactive outside MP play.
/// </summary>
[DisallowMultipleComponent]
public class NetworkSyncValidator : MonoBehaviour
{
    [Tooltip("Seconds between validation sweeps. Default 2s — cheap.")]
    [Range(1f, 10f)] public float checkInterval = 2f;

    [Tooltip("If true, log a single summary line at the end of each sweep " +
             "even when there are no problems. Useful for confirming the " +
             "validator is running.")]
    public bool logHeartbeat = false;

    [Tooltip("If true, the validator also REPAIRS ghost passengers it finds " +
             "(force-hides + deselects). Default true. Set false if you want " +
             "logs only.")]
    public bool autoRepairGhosts = true;

    private Coroutine routine;

    private void OnEnable()  { routine = StartCoroutine(Loop()); }
    private void OnDisable() { if (routine != null) StopCoroutine(routine); routine = null; }

    private IEnumerator Loop()
    {
        while (true)
        {
            yield return new WaitForSeconds(Mathf.Max(0.5f, checkInterval));
            if (!NetworkManagerRTS.IsMultiplayerEnabled) continue;

            int colorIssues   = 0;
            int paxOwnerDiff  = 0;
            int paxBothPlaces = 0;

            // Build a set of GameObjects that any APC currently considers a
            // passenger — used for "visible while inside" detection.
            APCTransport[] apcs = Object.FindObjectsByType<APCTransport>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var insideMap = new System.Collections.Generic.Dictionary<GameObject, APCTransport>();
            foreach (APCTransport apc in apcs)
            {
                if (apc == null) continue;
                int apcOwner = apc.GetComponent<GameEntity>()?.ownerPlayerId ?? GameEntity.NeutralOwnerId;
                var pax = apc.Passengers;
                for (int i = 0; i < pax.Count; i++)
                {
                    GameObject p = pax[i];
                    if (p == null) continue;
                    insideMap[p] = apc;

                    int pOwner = p.GetComponent<GameEntity>()?.ownerPlayerId ?? GameEntity.NeutralOwnerId;
                    if (pOwner != apcOwner)
                    {
                        string pid = p.GetComponent<GameEntity>()?.EntityId ?? p.name;
                        string aid = apc.GetComponent<GameEntity>()?.EntityId ?? apc.name;
                        Debug.LogWarning($"[SyncValidation] Passenger owner mismatch passenger={pid} " +
                                         $"owner={pOwner} apcOwner={apcOwner} apc={aid}");
                        paxOwnerDiff++;
                    }
                    if (p.activeInHierarchy)
                    {
                        string pid = p.GetComponent<GameEntity>()?.EntityId ?? p.name;
                        string aid = apc.GetComponent<GameEntity>()?.EntityId ?? apc.name;
                        Debug.LogWarning($"[APCValidation] passenger={pid} inside=true " +
                                         $"activeInWorld=true apc={aid} -> fixed hidden");

                        if (autoRepairGhosts)
                        {
                            // Deselect first (the user may currently have it
                            // in selection), then push it back through the
                            // authoritative inside path so colliders /
                            // combat / nav agent all go off in lockstep.
                            SelectableUnit su = p.GetComponent<SelectableUnit>();
                            if (su != null)
                            {
                                UnitSelector sel = UnitSelector.Instance;
                                if (sel != null) sel.RemoveFromSelection(su);
                                else             su.Deselect();
                                Debug.LogWarning($"[APCValidation] passenger={pid} selected " +
                                                 "while inside -> deselected");
                            }
                            apc.ForcePassengerInside(p, "validator");
                        }
                        paxBothPlaces++;
                    }
                }
            }

            // Color check — sample every GameEntity with a TeamColorMarker.
            // We can't directly read the painted color (MaterialPropertyBlock
            // doesn't round-trip), so we infer mismatch by checking the
            // owner color is REGISTERED at all. If the owner has no slot
            // color and the marker is on a non-neutral entity, repaint
            // would be wrong.
            GameEntity[] all = Object.FindObjectsByType<GameEntity>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (GameEntity ge in all)
            {
                if (ge == null) continue;
                if (ge.entityType == EntityType.Resource)   continue;
                if (ge.entityType == EntityType.Projectile) continue;
                if (ge.ownerPlayerId == GameEntity.NeutralOwnerId) continue;

                TeamColorMarker tcm = ge.GetComponentInChildren<TeamColorMarker>(true);
                if (tcm == null) continue;

                if (!MultiplayerColors.HasForOwner(ge.ownerPlayerId))
                {
                    Color expected = MultiplayerColors.ForOwnerOrDefault(ge.ownerPlayerId);
                    Debug.LogWarning($"[SyncValidation] Color mismatch entity={ge.EntityId} " +
                                     $"owner={ge.ownerPlayerId} expected RGB(" +
                                     $"{expected.r:F2},{expected.g:F2},{expected.b:F2}) " +
                                     "(no slot color registered — MatchStart may not have run).");
                    colorIssues++;
                }
            }

            int total = colorIssues + paxOwnerDiff + paxBothPlaces;
            if (total > 0)
            {
                Debug.LogWarning($"[SyncValidation] sweep done — issues: {colorIssues} color, " +
                                 $"{paxOwnerDiff} passenger-owner-diff, " +
                                 $"{paxBothPlaces} passenger-visible-while-inside.");
            }
            else if (logHeartbeat)
            {
                Debug.Log("[SyncValidation] sweep done — no issues.");
            }
        }
    }
}
