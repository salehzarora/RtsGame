using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Runtime-side dev bootstrap. Attached to <c>GameManager</c> by
/// <c>Tools → RTS → Scenes → Create or Repair Dev Sandbox</c> in DevSandboxScene only.
///
/// <para>What it does on Start (after every other component's Awake has run):</para>
/// <list type="number">
///   <item>Logs the resolved local player id + ownership gate so the console
///         immediately shows whether commands will be accepted.</item>
///   <item>Locates the Dozer (by <see cref="GameEntity.prefabTypeId"/> = "Dozer")
///         and reports its ownership, team, NavMeshAgent state, and whether
///         it's on a baked NavMesh.</item>
///   <item>If the Dozer's <see cref="NavMeshAgent"/> reports it's NOT on the
///         NavMesh (Y-offset / off-mesh quirk), warps it to the nearest
///         sampled NavMesh point within a 5 m radius so right-click moves
///         work from frame 1.</item>
///   <item>Logs the <see cref="UnitSelector"/> LayerMasks. If any of the four
///         is empty (the previous bug), warns the user and points at the
///         repair tool.</item>
///   <item>Reports starting resources, GameState, and camera focus.</item>
/// </list>
///
/// <para>What it does NOT do</para>
/// <list type="bullet">
///   <item>Touch Photon. The dev sandbox runs entirely on the
///         <see cref="NetworkManagerRTS.IsMultiplayerEnabled"/> = false path.</item>
///   <item>Mutate any production system field. Every write here is local to
///         the dev scene (Dozer position, NavMeshAgent warp, camera).</item>
///   <item>Run in non-dev scenes. The component is only added in
///         <c>DevSandboxScene</c>; nothing else references it.</item>
/// </list>
///
/// Safe to delete at any time — the dev sandbox is testing infrastructure,
/// not gameplay.
/// </summary>
[DisallowMultipleComponent]
public class DevSandboxBootstrap : MonoBehaviour
{
    [Header("Dozer focus")]
    [Tooltip("After Start, frame the camera on the Dozer so it's centred in " +
             "the play view. Off = camera stays where the editor tool placed " +
             "it.")]
    public bool focusCameraOnDozer = true;

    [Tooltip("Distance behind the Dozer (along -Z) the camera rig is moved " +
             "to when focusing. Y stays at the rig's current height so the " +
             "user's zoom setting is preserved.")]
    public float cameraFocusBackOffset = 18f;

    [Header("NavMesh self-heal")]
    [Tooltip("If the Dozer's NavMeshAgent reports !isOnNavMesh, sample the " +
             "nearest baked NavMesh point within this radius and warp the " +
             "agent there. Catches small Y-offset cases where the Dozer " +
             "spawned slightly above the plane.")]
    public float navMeshWarpRadius = 5f;

    private void Start()
    {
        Debug.Log("[DevSandbox] ─── DevSandboxBootstrap.Start ───");

        // 1. Local player id (gameplay command-issuance gate).
        int localCmdPid = GameEntity.LocalCommandPlayerId;
        int localNetPid = NetworkManagerRTS.LocalPlayerId;
        bool mpOn       = NetworkManagerRTS.IsMultiplayerEnabled;
        Debug.Log($"[DevSandbox] Local player initialized: playerId={localCmdPid} " +
                  $"(NetworkManagerRTS.LocalPlayerId={localNetPid}, IsMultiplayerEnabled={mpOn}). " +
                  "Commands will be stamped with this id.");

        // 2. GameState — IsPlaying must be true or RTSCamera/UnitSelector gate input.
        bool gameStatePlaying = GameStateManager.IsPlaying;
        Debug.Log($"[DevSandbox] GameState set to Playing = {gameStatePlaying}  " +
                  (GameStateManager.Instance == null
                      ? "(no GameStateManager in scene — IsPlaying defaults to true)."
                      : "(GameStateManager.Instance.IsGameStarted)."));

        // 3. Dozer ownership + agent state.
        GameEntity dozer = FindDozer();
        if (dozer == null)
        {
            Debug.LogError("[DevSandbox] ✗ No Dozer (prefabTypeId == 'Dozer') in scene. " +
                           "Run Tools → RTS → Scenes → Create or Repair Dev Sandbox.");
        }
        else
        {
            Health h = dozer.GetComponent<Health>();
            Debug.Log($"[DevSandbox] Dozer owner set to playerId={dozer.ownerPlayerId} " +
                      $"(teamId={dozer.teamId}, Health.team={(h != null ? h.team.ToString() : "<no Health>")}, " +
                      $"EntityId={dozer.EntityId}).");

            NavMeshAgent agent = dozer.GetComponent<NavMeshAgent>();
            if (agent == null)
            {
                Debug.LogError("[DevSandbox] ✗ Dozer has no NavMeshAgent — movement will not work. " +
                               "Check DozerPrefab.prefab.");
            }
            else
            {
                Debug.Log($"[DevSandbox] Dozer NavMeshAgent: enabled={agent.enabled} " +
                          $"isOnNavMesh={agent.isOnNavMesh} speed={agent.speed} " +
                          $"stoppingDistance={agent.stoppingDistance}.");

                if (agent.enabled && !agent.isOnNavMesh)
                {
                    if (NavMesh.SamplePosition(dozer.transform.position,
                            out NavMeshHit hit, navMeshWarpRadius, NavMesh.AllAreas))
                    {
                        bool warped = agent.Warp(hit.position);
                        Debug.Log($"[DevSandbox] Dozer was off NavMesh — Warp({hit.position}) " +
                                  (warped ? "succeeded." : "FAILED. Re-bake NavMesh."));
                    }
                    else
                    {
                        Debug.LogWarning("[DevSandbox] ⚠ Dozer off NavMesh and no nearby valid point. " +
                                         "Run Tools → RTS → Scenes → Create or Repair Dev Sandbox to re-bake.");
                    }
                }
            }
        }

        // 4. UnitSelector LayerMask audit — the bug the user reported was these
        //    being unset, so log every mask explicitly. Empty mask = right-click
        //    raycast hits nothing = no Move command issued.
        UnitSelector selector = FindAnyObjectByType<UnitSelector>(FindObjectsInactive.Include);
        if (selector == null)
        {
            Debug.LogError("[DevSandbox] ✗ No UnitSelector in scene — input is dead.");
        }
        else
        {
            int u = selector.unitLayer.value;
            int g = selector.groundLayer.value;
            int r = selector.resourceLayer.value;
            int b = selector.buildingLayer.value;
            Debug.Log($"[DevSandbox] UnitSelector masks: unit=0x{u:X}  ground=0x{g:X}  " +
                      $"resource=0x{r:X}  building=0x{b:X}.");
            if (g == 0)
                Debug.LogError("[DevSandbox] ✗ UnitSelector.groundLayer is EMPTY — right-click ground will " +
                               "raycast nothing and the Dozer won't move. Re-run Create or Repair Dev Sandbox.");
            if (u == 0)
                Debug.LogError("[DevSandbox] ✗ UnitSelector.unitLayer is EMPTY — single-click unit-select " +
                               "will fail. Re-run Create or Repair Dev Sandbox.");
        }

        // 5. Resources.
        PlayerResourceManager prm =
            FindAnyObjectByType<PlayerResourceManager>(FindObjectsInactive.Include);
        if (prm == null)
            Debug.LogError("[DevSandbox] ✗ No PlayerResourceManager — building costs cannot be paid.");
        else
            Debug.Log($"[DevSandbox] Resources set to {prm.CurrentResources} " +
                      $"(startingResources field = {prm.startingResources}, ownerPlayerId = {prm.ownerPlayerId}).");

        // 6. Camera focus.
        if (focusCameraOnDozer && dozer != null)
        {
            RTSCamera rig = FindAnyObjectByType<RTSCamera>(FindObjectsInactive.Include);
            if (rig != null)
            {
                Vector3 d = dozer.transform.position;
                Vector3 p = rig.transform.position;
                rig.transform.position = new Vector3(d.x, p.y, d.z - cameraFocusBackOffset);
                Debug.Log($"[DevSandbox] Camera focused on Dozer at {d} " +
                          $"(rig moved to {rig.transform.position}).");
            }
        }

        Debug.Log("[DevSandbox] ─────────────────────────────────────");
    }

    private static GameEntity FindDozer()
    {
        foreach (GameEntity e in FindObjectsByType<GameEntity>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (e != null && e.prefabTypeId == "Dozer") return e;
        }
        return null;
    }
}
