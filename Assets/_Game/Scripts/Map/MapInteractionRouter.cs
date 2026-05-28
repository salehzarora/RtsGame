using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Right-click router for Interactive Tactical Map objects. Called from
/// <see cref="UnitSelector"/> as a single contained hook — it inspects what's
/// under the cursor and, if it's a map interactable the current selection can
/// act on, issues the matching command and returns true (consuming the click).
/// Returning false lets UnitSelector's normal attack / gather / move logic run.
///
/// Routing priority for a map object under the cursor:
///   1. Garrison / Watch Tower — selected infantry enter (near ones instantly,
///      far ones are sent walking to it); a re-click on a garrison you hold
///      ejects everyone.
///   2. Tunnel — selected infantry travel to the linked tunnel.
///   3. Destructible (incl. fuel tanks) flagged Targetable — attack it (the
///      ordinary attack command, which is team-agnostic for explicit targets).
///
/// This is the ONLY integration point into selection input; everything else is
/// self-contained in the Map system.
/// </summary>
public static class MapInteractionRouter
{
    /// <summary>
    /// Try to handle a right-click against a map interactable. Returns true if
    /// the click was consumed.
    /// </summary>
    public static bool TryRouteRightClick(Ray ray)
    {
        UnitSelector sel = UnitSelector.Instance;
        if (sel == null) return false;

        // Need at least one selected unit/aircraft to do anything here.
        if (sel.SelectedUnits.Count == 0 && sel.SelectedAircraftList.Count == 0) return false;

        if (!Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity)) return false;

        MapInteractable interactable =
            hit.collider.GetComponent<MapInteractable>() ?? hit.collider.GetComponentInParent<MapInteractable>();
        if (interactable == null) return false;

        // 1. Garrison / watch tower.
        GarrisonBuilding garrison = interactable as GarrisonBuilding;
        if (garrison != null && TryGarrison(sel, garrison)) return true;

        // 2. Tunnel.
        TunnelEntrance tunnel = interactable as TunnelEntrance;
        if (tunnel != null && TryTunnel(sel, tunnel)) return true;

        // 3. Attack a targetable destructible (fuel tank, bridge, …). A garrison
        //    or tunnel falls through to here if its own interaction didn't apply
        //    (e.g. enemy-held garrison with combat units selected).
        DestructibleMapObject destructible =
            interactable.GetComponent<DestructibleMapObject>();
        if (destructible != null && destructible.isTargetable && !destructible.IsDestroyed)
            return TryAttack(sel, destructible);

        return false;
    }

    // ------------------------------------------------------------------ //
    // Garrison enter / exit
    // ------------------------------------------------------------------ //

    private static bool TryGarrison(UnitSelector sel, GarrisonBuilding garrison)
    {
        int localPid = GameEntity.LocalCommandPlayerId;

        // Gather eligible enterers from the selection.
        var eligible = new List<SelectableUnit>();
        foreach (SelectableUnit u in sel.SelectedUnits)
        {
            if (u == null) continue;
            if (garrison.CanEnter(u.gameObject)) eligible.Add(u);
        }

        if (eligible.Count > 0)
        {
            foreach (SelectableUnit u in eligible)
            {
                float dist = Vector3.Distance(u.transform.position, garrison.transform.position);
                if (dist <= garrison.enterRadius)
                {
                    garrison.TryCommandEnter(u.gameObject);
                }
                else
                {
                    // Walk it to the garrison; the player re-clicks when close.
                    UnitMovement mv = u.GetComponent<UnitMovement>();
                    if (mv != null && mv.LocallyControlled) mv.MoveTo(garrison.transform.position);
                }
            }
            Debug.Log($"[MapInput] Garrison order on '{garrison.name}' — {eligible.Count} eligible unit(s).");
            return true;
        }

        // No eligible enterers — if WE hold this garrison, a right-click ejects.
        if (garrison.IsOccupied && garrison.OccupyingOwnerId == localPid)
        {
            garrison.CommandExitAll();
            Debug.Log($"[MapInput] Garrison '{garrison.name}' eject order.");
            return true;
        }

        return false;   // let attack / move handling take over
    }

    // ------------------------------------------------------------------ //
    // Tunnel travel
    // ------------------------------------------------------------------ //

    private static bool TryTunnel(UnitSelector sel, TunnelEntrance tunnel)
    {
        var eligible = new List<SelectableUnit>();
        foreach (SelectableUnit u in sel.SelectedUnits)
        {
            if (u == null) continue;
            if (tunnel.CanUse(u.gameObject)) eligible.Add(u);
        }
        if (eligible.Count == 0) return false;

        const float useRadius = 4f;
        foreach (SelectableUnit u in eligible)
        {
            float dist = Vector3.Distance(u.transform.position, tunnel.transform.position);
            if (dist <= useRadius)
            {
                tunnel.TryCommandTravel(u.gameObject);
            }
            else
            {
                UnitMovement mv = u.GetComponent<UnitMovement>();
                if (mv != null && mv.LocallyControlled) mv.MoveTo(tunnel.transform.position);
            }
        }
        Debug.Log($"[MapInput] Tunnel order on '{tunnel.name}' — {eligible.Count} eligible unit(s).");
        return true;
    }

    // ------------------------------------------------------------------ //
    // Attack a targetable map object
    // ------------------------------------------------------------------ //

    private static bool TryAttack(UnitSelector sel, DestructibleMapObject target)
    {
        Health targetHealth = target.GetComponent<Health>();
        if (targetHealth == null) return false;

        string[] attackerIds = CollectSelectionIds(sel);
        if (attackerIds.Length == 0) return false;

        GameEntity targetEntity = target.GetComponent<GameEntity>();
        CommandDispatcher.IssueAttack(
            GameEntity.LocalCommandPlayerId, attackerIds, targetEntity, targetHealth);

        Debug.Log($"[MapInput] Attack order on map object '{target.name}'.");
        return true;
    }

    private static string[] CollectSelectionIds(UnitSelector sel)
    {
        var ids = new List<string>(sel.SelectedUnits.Count + sel.SelectedAircraftList.Count);
        foreach (SelectableUnit u in sel.SelectedUnits)
            if (u != null) ids.Add(GameEntity.EnsureOn(u.gameObject).EntityId);
        foreach (SelectableAircraft a in sel.SelectedAircraftList)
            if (a != null) ids.Add(GameEntity.EnsureOn(a.gameObject).EntityId);
        return ids.ToArray();
    }
}
