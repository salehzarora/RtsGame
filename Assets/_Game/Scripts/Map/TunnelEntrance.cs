using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// PHASE F — tunnel entrance. Two entrances are linked; infantry that enter one
/// disappear and re-emerge at the other after a travel delay. A flanking /
/// secret-path tool.
///
/// Travel model (deterministic + network-safe):
///   • The traveling unit is hidden (SetActive false) at the entrance, waits
///     <see cref="travelDelay"/>, then is repositioned to the linked tunnel's
///     exit point and shown again. The coroutine runs on the TUNNEL (not the
///     unit) because the unit is inactive mid-travel.
///   • The commanding (owner) client runs this locally AND broadcasts a
///     TunnelTravel event carrying the resolved exit pose, so every client runs
///     the IDENTICAL hide → wait → appear sequence and ends at the same spot.
///   • After re-emergence, the existing owner-authoritative transform sync keeps
///     positions aligned — no continuous position networking is added here.
///
/// Vehicles are blocked unless <see cref="allowVehicles"/> is set; aircraft can
/// never use tunnels. A per-tunnel <see cref="cooldown"/> rate-limits use.
///
/// Setup (or use Tools → RTS → Map → Create Tunnel Pair):
///   1. Two TunnelEntrance objects, each with a GameEntity (entityType =
///      MapObject). Assign each other's <see cref="linkedTunnel"/>.
///   2. Optionally assign exit points; otherwise units emerge just in front of
///      the linked tunnel.
/// </summary>
public class TunnelEntrance : MapInteractable
{
    [Header("Tunnel — Linking")]
    [Tooltip("The tunnel this one connects to. Units entering here emerge there. " +
             "Set on BOTH tunnels (A→B and B→A) for two-way travel.")]
    public TunnelEntrance linkedTunnel;

    [Header("Tunnel — Eligibility")]
    [Tooltip("If true, only infantry may use the tunnel.")]
    public bool infantryOnly = true;

    [Tooltip("If true, vehicles may also use the tunnel. Aircraft never can.")]
    public bool allowVehicles = false;

    [Header("Tunnel — Timing")]
    [Tooltip("Seconds a unit spends 'in transit' (hidden) before emerging.")]
    public float travelDelay = 1.2f;

    [Tooltip("Minimum seconds between uses of THIS entrance. 0 = no cooldown.")]
    public float cooldown = 0.5f;

    [Header("Tunnel — Exit")]
    [Tooltip("Where units emerge at the LINKED tunnel. If empty, they appear just " +
             "in front of the linked tunnel transform.")]
    public Transform[] exitPoints;

    [Tooltip("Optional disappear/appear flash colour (procedural — no asset needed).")]
    [ColorUsage(false)] public Color travelFlashColor = new Color(0.5f, 0.8f, 1f);

    private float lastUseTime = -999f;
    private int   exitCursor;

    // ------------------------------------------------------------------ //
    // Eligibility
    // ------------------------------------------------------------------ //

    public bool CanUse(GameObject unit)
    {
        if (unit == null || linkedTunnel == null) return false;
        if (Time.time - lastUseTime < cooldown)   return false;

        UnitCategory uc = unit.GetComponent<UnitCategory>() ?? unit.GetComponentInParent<UnitCategory>();
        bool isAircraft = uc != null && uc.category == UnitCategory.Category.Aircraft;
        if (isAircraft) return false;

        bool infantry = IsInfantry(unit);
        if (infantry) return true;
        return allowVehicles && !infantryOnly;
    }

    // ------------------------------------------------------------------ //
    // Command path (owner) vs network apply
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Owner-side: start the travel locally AND broadcast it so other clients
    /// run the same sequence. Returns true if travel started.
    /// </summary>
    public bool TryCommandTravel(GameObject unit)
    {
        if (!CanUse(unit)) return false;

        Vector3 exitPos = linkedTunnel.NextExitPosition();
        Vector3 forward = (exitPos - linkedTunnel.transform.position).normalized;
        if (forward.sqrMagnitude < 0.0001f) forward = linkedTunnel.transform.forward;

        BeginTravelLocal(unit, exitPos, forward);

        GameEntity uge = unit.GetComponent<GameEntity>();
        if (uge != null)
            MapInteractableNetworkEvents.BroadcastTunnelTravel(
                EntityId, linkedTunnel.EntityId, uge.EntityId, exitPos, forward);

        return true;
    }

    /// <summary>Receive-side: run the travel locally with no re-broadcast.</summary>
    public void ApplyTravelFromNetwork(GameObject unit, Vector3 exitPos, Vector3 forward)
    {
        if (unit == null) return;
        BeginTravelLocal(unit, exitPos, forward);
    }

    private void BeginTravelLocal(GameObject unit, Vector3 exitPos, Vector3 forward)
    {
        lastUseTime = Time.time;
        if (linkedTunnel != null) linkedTunnel.lastUseTime = Time.time;

        // Drop from local selection so the player can't keep commanding it mid-transit.
        SelectableUnit su = unit.GetComponent<SelectableUnit>();
        if (su != null && UnitSelector.Instance != null) UnitSelector.Instance.RemoveFromSelection(su);

        SpawnFlash(unit.transform.position);
        StartCoroutine(TravelRoutine(unit, exitPos, forward));

        Debug.Log($"[Tunnel] '{name}' → '{(linkedTunnel != null ? linkedTunnel.name : "?")}' " +
                  $"transit of '{unit.name}' ({travelDelay}s).");
    }

    private IEnumerator TravelRoutine(GameObject unit, Vector3 exitPos, Vector3 forward)
    {
        if (unit == null) yield break;
        unit.SetActive(false);

        float t = 0f;
        while (t < travelDelay)
        {
            t += Time.deltaTime;
            yield return null;
        }

        if (unit == null) yield break;   // destroyed mid-transit (e.g. building wiped it)

        unit.transform.position = exitPos;
        if (forward.sqrMagnitude > 0.0001f)
            unit.transform.rotation = Quaternion.LookRotation(forward);
        unit.SetActive(true);

        NavMeshAgent agent = unit.GetComponent<NavMeshAgent>();
        if (agent != null && agent.enabled && agent.isOnNavMesh) agent.Warp(exitPos);

        GroundAutoAttackController aa = unit.GetComponent<GroundAutoAttackController>();
        if (aa != null) aa.OnUnloadedFromTransport(exitPos);

        SpawnFlash(exitPos);
        Debug.Log($"[Tunnel] '{unit.name}' emerged at {exitPos:F1}.");
    }

    // ------------------------------------------------------------------ //
    // Exit point selection
    // ------------------------------------------------------------------ //

    /// <summary>Returns the next emergence position for units arriving at THIS tunnel.</summary>
    public Vector3 NextExitPosition()
    {
        if (exitPoints != null && exitPoints.Length > 0)
        {
            Transform t = exitPoints[exitCursor % exitPoints.Length];
            exitCursor++;
            if (t != null) return t.position;
        }
        // Just in front of the tunnel mouth.
        return transform.position + transform.forward * 2.5f;
    }

    // ------------------------------------------------------------------ //
    // Cosmetic flash
    // ------------------------------------------------------------------ //

    private void SpawnFlash(Vector3 pos)
    {
        const int IgnoreRaycastLayer = 2;
        GameObject flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flash.name = "TunnelFlash";
        flash.transform.position = pos + Vector3.up * 0.5f;
        flash.layer = IgnoreRaycastLayer;
        Collider col = flash.GetComponent<Collider>();
        if (col != null) Destroy(col);

        Renderer r = flash.GetComponent<Renderer>();
        if (r != null)
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Standard");
            Material m = new Material(shader) { color = travelFlashColor };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", travelFlashColor);
            r.sharedMaterial = m;
        }

        ExplosionFlashFx fx = flash.AddComponent<ExplosionFlashFx>();
        fx.Init(2.5f, 0.4f);
    }
}
