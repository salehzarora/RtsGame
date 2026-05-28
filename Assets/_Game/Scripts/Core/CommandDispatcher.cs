using UnityEngine;

/// <summary>
/// Single entry point for executing <see cref="PlayerCommand"/>s.
///
/// Today every command originates locally from <see cref="UnitSelector"/> or
/// <see cref="RTSHUD"/>. Tomorrow, after a Photon / Netcode layer is added,
/// the same <see cref="Issue"/> call will be invoked by the network receive
/// path with commands deserialised from the wire. The execution body
/// downstream of <see cref="Issue"/> will be unchanged — that's the whole
/// reason this layer exists.
///
/// What the dispatcher DOES today:
///   • <see cref="CommandType.Move"/>   — resolves selected entity ids,
///     computes a formation, and issues <c>UnitMovement.MoveTo</c> + lets
///     the auto-attack controller know about the new guard position.
///   • <see cref="CommandType.Attack"/> — resolves the target by entity id
///     (or falls back to a direct <see cref="Health"/> reference for the
///     transition period) and routes to the appropriate combat component.
///   • <see cref="CommandType.Produce"/> — resolves the producer entity id
///     and calls the matching Produce* method on the building.
///
/// What it does NOT do yet:
///   • <see cref="CommandType.Build"/>           — scaffold only.
///     BuildingPlacementManager owns multi-step mouse input; not safe to
///     centralise yet.
///   • <see cref="CommandType.UnloadTransport"/> — scaffold; existing
///     <c>RTSHUD.OnClickUnloadAll</c> still owns this path.
///   • <see cref="CommandType.Stop"/> / <see cref="CommandType.Patrol"/> —
///     scaffolds. No UI affordance is wired today.
/// </summary>
public static class CommandDispatcher
{
    // ------------------------------------------------------------------ //
    // Tuning
    // ------------------------------------------------------------------ //

    /// <summary>
    /// World-unit spacing between units in a Move-command formation. Mirrors
    /// the existing <see cref="UnitSelector.formationSpacing"/> default so the
    /// command path produces the same grid the legacy code did.
    /// </summary>
    private const float FormationSpacing = 2f;

    /// <summary>
    /// When true, every issued command is logged at info level. Flip to false
    /// during stress tests to silence the channel.
    /// </summary>
    public static bool LogCommands = true;

    // ------------------------------------------------------------------ //
    // Network seam
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Fires AFTER a locally-issued command finishes its local-side execution
    /// pass. <see cref="NetworkCommandRelay"/> subscribes to this event and
    /// forwards the command to other clients in the room when
    /// <see cref="NetworkManagerRTS.IsMultiplayerEnabled"/> is true.
    ///
    /// Echo-loop protection: this event is intentionally NOT raised by
    /// <see cref="IssueRemote"/> — commands deserialised from the network
    /// only execute locally, they don't get re-broadcast.
    /// </summary>
    public static event System.Action<PlayerCommand> OnLocalCommandIssued;

    // Flag used to suppress the local-command event while we replay a
    // network-received command. Static field is fine — dispatch is single-
    // threaded on the Unity main thread.
    private static bool s_suppressLocalEvent;

    // ------------------------------------------------------------------ //
    // Public entry point
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Execute <paramref name="cmd"/>. Routes on <see cref="PlayerCommand.commandType"/>.
    /// Null and unknown command types are logged and ignored.
    ///
    /// Network behaviour: a command issued via this path is considered LOCAL.
    /// After executing it, <see cref="OnLocalCommandIssued"/> fires so the
    /// network relay can broadcast the same command to remote clients.
    /// Network-received commands go through <see cref="IssueRemote"/> instead.
    /// </summary>
    public static void Issue(PlayerCommand cmd)
    {
        if (cmd == null)
        {
            Debug.LogWarning("[CommandDispatcher] null command ignored.");
            return;
        }

        if (LogCommands)
            Debug.Log($"[CommandDispatcher] Issue {cmd}");

        // Phase 3 — multiplayer ownership validation. Only for LOCAL commands;
        // network-replayed commands (`s_suppressLocalEvent == true`) are trusted
        // because they were validated on the sender's side and ownership is
        // determined by the sender, not the receiver.
        if (!s_suppressLocalEvent && !ValidateLocalOwnership(cmd))
            return;

        switch (cmd.commandType)
        {
            case CommandType.Move:            ExecuteMove(cmd);             break;
            case CommandType.Attack:          ExecuteAttack(cmd);           break;
            case CommandType.Produce:         ExecuteProduce(cmd);          break;
            case CommandType.Build:           ExecuteBuild(cmd);            break;
            case CommandType.UnloadTransport: ExecuteUnloadTransport(cmd);  break;

            // Scaffolds — logged above, no execution side-effect today.
            // Existing call sites still own these paths; revisit when network
            // sync requires them to flow through the dispatcher.
            case CommandType.Stop:
            case CommandType.Patrol:
                Debug.Log($"[CommandDispatcher] {cmd.commandType} is a scaffold today — " +
                          "legacy call site still executes the action.");
                break;

            default:
                Debug.LogWarning($"[CommandDispatcher] unknown command type {cmd.commandType}");
                break;
        }

        // Local commands fan out to the network relay (if any). Remote-replayed
        // commands intentionally skip this branch — they're suppressed by the
        // flag <see cref="IssueRemote"/> sets while it calls back into Issue.
        if (!s_suppressLocalEvent)
            OnLocalCommandIssued?.Invoke(cmd);
    }

    /// <summary>
    /// Execute a command that arrived from a remote client. Behaviour is
    /// identical to <see cref="Issue"/> EXCEPT that the
    /// <see cref="OnLocalCommandIssued"/> event is suppressed for the
    /// duration so the network relay does not re-broadcast it (echo loop).
    /// </summary>
    public static void IssueRemote(PlayerCommand cmd)
    {
        if (cmd == null) return;

        Debug.Log($"[CommandDispatcher] Executing remote command #{cmd.commandId} " +
                  $"({cmd.commandType}) from player {cmd.playerId}.");

        bool prev = s_suppressLocalEvent;
        s_suppressLocalEvent = true;
        try
        {
            Issue(cmd);
        }
        finally
        {
            s_suppressLocalEvent = prev;
        }
    }

    // ------------------------------------------------------------------ //
    // Move
    // ------------------------------------------------------------------ //

    private static void ExecuteMove(PlayerCommand cmd)
    {
        if (!cmd.hasTargetPosition) return;
        if (cmd.selectedEntityIds == null || cmd.selectedEntityIds.Length == 0) return;

        // Resolve ids → GameEntity. Skip ids that no longer point to a live
        // entity (unit died mid-frame, etc.). We split ground vs. aircraft so
        // each group gets the right call.
        var ground   = new System.Collections.Generic.List<SelectableUnit>(cmd.selectedEntityIds.Length);
        var aircraft = new System.Collections.Generic.List<SelectableAircraft>(cmd.selectedEntityIds.Length);

        for (int i = 0; i < cmd.selectedEntityIds.Length; i++)
        {
            GameEntity e = EntityRegistry.Find(cmd.selectedEntityIds[i]);
            if (e == null) continue;

            SelectableUnit     gu = e.GetComponent<SelectableUnit>();
            SelectableAircraft ac = e.GetComponent<SelectableAircraft>();
            if (gu != null)      ground.Add(gu);
            else if (ac != null) aircraft.Add(ac);
        }

        // Formation positions — same centred-grid logic on every client, so
        // each unit's slot is deterministic regardless of who replays the
        // command.
        Vector3[] positions = GetFormationPositions(cmd.targetPosition, ground.Count);
        for (int i = 0; i < ground.Count; i++)
        {
            SelectableUnit u = ground[i];
            UnitMovement mv  = u.GetComponent<UnitMovement>();
            if (mv != null)
            {
                // Phase 10.14 — only the OWNER client runs the NavMeshAgent.
                // Non-owner clients receive UnitTransform broadcasts at 5 Hz
                // and lerp their transform; calling MoveTo there would
                // either no-op (the agent is disabled) or, worse, fight
                // the lerp.
                if (mv.LocallyControlled)
                {
                    mv.MoveTo(positions[i]);
                }
            }

            // Re-aim the auto-attack scan around the new guard position even
            // on remote clients — it's a local heuristic that doesn't move
            // the unit, only changes which targets the guard considers.
            u.GetComponent<GroundAutoAttackController>()?.NotifyManualMove(positions[i]);
        }

        // Aircraft: fly-to-point with the existing group-id batch behaviour.
        // We mint a fresh group id here so a future networked command will
        // still produce a coherent flight-of-jets.
        long aircraftGroupId = aircraft.Count > 1 ? System.DateTime.UtcNow.Ticks : 0L;
        for (int i = 0; i < aircraft.Count; i++)
            aircraft[i].GetComponent<AirUnitController>()?.FlyToPoint(cmd.targetPosition, aircraftGroupId);
    }

    // ------------------------------------------------------------------ //
    // Attack
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Resolve target via EntityRegistry; if the target was issued before the
    /// project's prefabs all carry GameEntity (i.e. during this transition
    /// phase), fall back to <paramref name="legacyTarget"/> so we don't drop
    /// the order. The legacy path is removed once every spawn carries an id.
    /// </summary>
    public static void IssueAttack(int playerId, string[] selectedIds,
                                   GameEntity targetEntity, Health legacyTarget)
    {
        string tid = targetEntity != null ? targetEntity.EntityId : "";
        PlayerCommand cmd = PlayerCommand.Attack(playerId, selectedIds, tid);
        if (LogCommands)
            Debug.Log($"[CommandDispatcher] Issue {cmd}");

        // Same ownership gate as Issue() so the fast-path attack honours MP rules.
        if (!s_suppressLocalEvent && !ValidateLocalOwnership(cmd))
            return;

        // Direct execute with the resolved Health to avoid an extra registry
        // hop in the common case. The general Issue() path still works if a
        // future caller doesn't have the Health handy.
        ExecuteAttackWith(cmd, legacyTarget);

        // Fire the local-command event so NetworkCommandRelay broadcasts this
        // attack to remote clients. (Issue() would do this automatically; the
        // fast-path needs an explicit invoke.)
        if (!s_suppressLocalEvent)
            OnLocalCommandIssued?.Invoke(cmd);
    }

    private static void ExecuteAttack(PlayerCommand cmd)
    {
        GameEntity target = EntityRegistry.Find(cmd.targetEntityId);
        Health     health = target != null ? target.GetComponent<Health>() : null;
        ExecuteAttackWith(cmd, health);
    }

    private static void ExecuteAttackWith(PlayerCommand cmd, Health targetHealth)
    {
        if (targetHealth == null) return;
        if (cmd.selectedEntityIds == null || cmd.selectedEntityIds.Length == 0) return;

        for (int i = 0; i < cmd.selectedEntityIds.Length; i++)
        {
            GameEntity e = EntityRegistry.Find(cmd.selectedEntityIds[i]);
            if (e == null) continue;

            // Ground combat — units carry exactly one of UnitCombat /
            // RocketCombat / MissileLauncherCombat. Null-safe access keeps
            // the call simple regardless of which one this unit has.
            e.GetComponent<UnitCombat>()?.SetTarget(targetHealth);
            e.GetComponent<RocketCombat>()?.SetTarget(targetHealth);
            e.GetComponent<MissileLauncherCombat>()?.SetTarget(targetHealth);
            e.GetComponent<GroundAutoAttackController>()?.NotifyManualAttack(targetHealth);

            // Aircraft path — use the existing solo-attack group id (0).
            e.GetComponent<AirUnitController>()?.AttackTarget(targetHealth, 0L);
        }
    }

    // ------------------------------------------------------------------ //
    // Produce
    // ------------------------------------------------------------------ //

    private static void ExecuteProduce(PlayerCommand cmd)
    {
        if (cmd.selectedEntityIds == null || cmd.selectedEntityIds.Length == 0) return;

        // Dup-spawn guard. A remote command may arrive that we already
        // executed locally (the originating client also gets the event back
        // via Photon's `ReceiverGroup.All`, though we currently use Others).
        // Belt-and-braces: if the spawn id already lives in the registry,
        // bail without re-spawning.
        if (!string.IsNullOrEmpty(cmd.spawnEntityId)
            && EntityRegistry.Find(cmd.spawnEntityId) != null)
        {
            Debug.Log($"[CommandDispatcher] Produce skipped — entity " +
                      $"'{cmd.spawnEntityId}' already exists locally.");
            return;
        }

        GameEntity producer = EntityRegistry.Find(cmd.selectedEntityIds[0]);
        if (producer == null)
        {
            Debug.LogWarning($"[CommandDispatcher] Produce target '{cmd.selectedEntityIds[0]}' " +
                             "not found in EntityRegistry — has the building been destroyed " +
                             "or never had GameEntity added? Run Tools → RTS → Multiplayer Prep " +
                             "→ Add GameEntity To Scene Objects.");
            return;
        }

        // Push the network-allocated id so GameEntity.Awake on the freshly-
        // spawned unit picks it up. The producer methods themselves don't
        // need to know about this — they just call Instantiate as before.
        // try/finally guarantees the slot is cleared even if the spawn fails
        // (resource check, missing prefab, etc.).
        GameEntity.SetNextSpawnId(cmd.spawnEntityId);
        try
        {
            switch (cmd.productionType)
            {
                case "Soldier":         producer.GetComponent<UnitProducer>()?.ProduceSoldier();           break;
                case "RPGSoldier":      producer.GetComponent<UnitProducer>()?.ProduceRPGSoldier();        break;
                case "Worker":          producer.GetComponent<CommandCenterProducer>()?.ProduceWorker();    break;
                case "Dozer":           producer.GetComponent<CommandCenterProducer>()?.ProduceDozer();     break;
                case "Humvee":          producer.GetComponent<VehicleFactoryProducer>()?.ProduceHumvee();   break;
                case "APC":             producer.GetComponent<VehicleFactoryProducer>()?.ProduceAPC();      break;
                case "ArtilleryTank":   producer.GetComponent<VehicleFactoryProducer>()?.ProduceArtilleryTank(); break;
                case "MissileLauncher": producer.GetComponent<VehicleFactoryProducer>()?.ProduceMissileLauncher(); break;
                case "StrikeJet":       producer.GetComponent<Airfield>()?.ProduceStrikeJet();              break;
                default:
                    Debug.LogWarning($"[CommandDispatcher] Unknown productionType '{cmd.productionType}'.");
                    break;
            }
        }
        finally
        {
            // Always clear — guarantees no stray preset id leaks to a later spawn.
            GameEntity.SetNextSpawnId(null);
        }

        if (!string.IsNullOrEmpty(cmd.spawnEntityId))
        {
            // Phase 3: stamp the canonical owner onto the spawned entity. In
            // MP this also re-keys Health.team into the local client's
            // perspective. In SP this is a no-op for owner=0 (matches the
            // prefab's default team).
            // Phase 9: ApplyOwnership now also force-repaints sibling
            // TeamColorMarkers so the spawned unit's colour matches the
            // owner immediately — fixes the "Player 1's worker spawns
            // orange" symptom.
            GameEntity spawned = EntityRegistry.Find(cmd.spawnEntityId);
            int producerOwner = -1;
            if (cmd.selectedEntityIds != null && cmd.selectedEntityIds.Length > 0)
            {
                GameEntity prod = EntityRegistry.Find(cmd.selectedEntityIds[0]);
                if (prod != null) producerOwner = prod.ownerPlayerId;
            }
            Debug.Log($"[CommandDispatcher] Execute Produce {cmd.productionType} " +
                      $"cmd.playerId={cmd.playerId} producerOwner={producerOwner}");

            if (spawned != null) spawned.ApplyOwnership(cmd.playerId);

            // Spam-production diagnostic — if the unit never registered, the
            // hotkey or button path bypassed SetNextSpawnId / Instantiate.
            bool registered = spawned != null;
            Debug.Log($"[Produce] Spawned {cmd.productionType} id={cmd.spawnEntityId} " +
                      $"owner={cmd.playerId} registered={registered}");
            Debug.Log($"[NetworkSpawn] Assigned entityId {cmd.spawnEntityId} " +
                      $"(owner {cmd.playerId}) to {cmd.productionType}.");
            Debug.Log($"[Ownership] {cmd.productionType} entityId={cmd.spawnEntityId} " +
                      $"owner={cmd.playerId}");
        }
    }

    // ------------------------------------------------------------------ //
    // Build — confirmed Dozer placement
    // ------------------------------------------------------------------ //

    private static void ExecuteBuild(PlayerCommand cmd)
    {
        if (!cmd.hasTargetPosition)
        {
            Debug.LogWarning("[CommandDispatcher] Build command has no target position — ignoring.");
            return;
        }

        // Sanity-check the building type. The interactive placement path on
        // BPM only fires for the five legitimate Dozer-buildable types; we
        // re-verify here so a future networked command can't ask us to spawn
        // an unknown class.
        if (!IsKnownBuildingType(cmd.productionType))
        {
            Debug.LogWarning($"[CommandDispatcher] Build command has unsupported " +
                             $"buildingType '{cmd.productionType}'. Expected one of " +
                             "Barracks, PowerPlant, VehicleFactory, Airfield, MachineGunDefense, " +
                             "CommandCenter (or 'Machine Gun Defense' for the legacy label). Ignoring.");
            return;
        }

        // Dup-spawn guard — if the site already exists locally, skip.
        if (!string.IsNullOrEmpty(cmd.spawnEntityId)
            && EntityRegistry.Find(cmd.spawnEntityId) != null)
        {
            Debug.Log($"[CommandDispatcher] Build skipped — site '{cmd.spawnEntityId}' " +
                      "already exists locally.");
            return;
        }

        string dozerId = (cmd.selectedEntityIds != null && cmd.selectedEntityIds.Length > 0)
            ? cmd.selectedEntityIds[0]
            : string.Empty;

        Debug.Log($"[CommandDispatcher] Execute Build: {cmd.productionType} at " +
                  $"{cmd.targetPosition:F1} (site id '{cmd.spawnEntityId}', " +
                  $"final id '{cmd.secondaryEntityId}', dozer id '{dozerId}', " +
                  $"cmd.playerId={cmd.playerId}).");

        // Phase 10.1 — cross-check that the requested owner actually matches
        // the dozer's owner. cmd.playerId was supplied by the issuing client;
        // dozer.ownerPlayerId is canonical and synchronised across clients.
        // If they disagree, reject the command — a buggy or hostile client
        // can't make another player's dozer build for it.
        //
        // We don't reject on missing-dozer (could be a legitimate race with a
        // dozer killed between Issue and replay). The site still spawns under
        // cmd.playerId; only the dozer-assignment step in BPM no-ops in that
        // case, mirroring pre-existing behaviour.
        if (string.IsNullOrEmpty(dozerId))
        {
            Debug.LogWarning("[CommandDispatcher] Build rejected — no dozer entityId in command.");
            return;
        }

        GameEntity dozerEntity = EntityRegistry.Find(dozerId);
        if (dozerEntity != null && dozerEntity.ownerPlayerId != cmd.playerId)
        {
            Debug.LogWarning($"[CommandDispatcher] Build rejected — cmd.playerId=" +
                             $"{cmd.playerId} but dozer '{dozerEntity.name}' " +
                             $"ownerPlayerId={dozerEntity.ownerPlayerId}. " +
                             "A client may not build with another player's dozer.");
            return;
        }

        BuildingPlacementManager bpm = Object.FindAnyObjectByType<BuildingPlacementManager>();
        if (bpm == null)
        {
            Debug.LogWarning("[CommandDispatcher] Build command but no " +
                             "BuildingPlacementManager in scene — ignoring.");
            return;
        }

        // Hand BPM a fully-described placement — all params come from the
        // command, none from BPM's local placement-mode state. This is what
        // lets a remote client (which never entered placement mode) execute
        // the same Build command. cmd.playerId is the validated owner; BPM
        // stamps it onto the construction site at spawn time, so the final
        // building inherits the correct owner on every client.
        bpm.ExecuteConfirmedDozerPlacement(
            cmd.targetPosition,
            cmd.productionType,
            dozerId,
            cmd.spawnEntityId,
            cmd.secondaryEntityId,
            cmd.playerId);
    }

    /// <summary>
    /// Whitelist of buildingType strings that <see cref="ExecuteBuild"/> will
    /// honour. Must mirror the labels that BPM's StartDozerBuild* helpers
    /// pass into <see cref="BuildingPlacementManager.StartDozerBuildingPlacement"/>.
    /// </summary>
    private static bool IsKnownBuildingType(string t)
    {
        if (string.IsNullOrEmpty(t)) return false;
        switch (t)
        {
            case "Barracks":
            case "PowerPlant":
            case "VehicleFactory":
            case "Airfield":
            case "MachineGunDefense":
            case "Machine Gun Defense": // current BPM label
            case "CommandCenter":       // Phase 10 — Dozer-buildable CC
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ //
    // UnloadTransport — fan out across one or more selected APCs
    // ------------------------------------------------------------------ //

    private static void ExecuteUnloadTransport(PlayerCommand cmd)
    {
        if (cmd.selectedEntityIds == null || cmd.selectedEntityIds.Length == 0)
        {
            Debug.LogWarning("[CommandDispatcher] UnloadTransport command has no APC ids — ignoring.");
            return;
        }

        int triggered = 0;
        for (int i = 0; i < cmd.selectedEntityIds.Length; i++)
        {
            GameEntity e = EntityRegistry.Find(cmd.selectedEntityIds[i]);
            if (e == null)
            {
                Debug.LogWarning($"[CommandDispatcher] UnloadTransport: entity " +
                                 $"'{cmd.selectedEntityIds[i]}' not found in registry — skipping.");
                continue;
            }

            APCTransport apc = e.GetComponent<APCTransport>();
            if (apc == null)
            {
                Debug.LogWarning($"[CommandDispatcher] UnloadTransport: '{e.name}' is " +
                                 "registered but has no APCTransport component — skipping.");
                continue;
            }

            if (apc.PassengerCount == 0) continue;     // empty APC — silently skip
            apc.UnloadAll();
            triggered++;
        }

        Debug.Log($"[CommandDispatcher] Execute UnloadTransport: {triggered} transport(s).");
    }

    // ------------------------------------------------------------------ //
    // Local-issue ownership validation (multiplayer only)
    //
    // Rules:
    //   • cmd.playerId must match this client's LocalPlayerId — you can't
    //     issue commands as someone else.
    //   • Every entity in selectedEntityIds must be owned by cmd.playerId —
    //     you can't command someone else's units.
    //   • targetEntityId for Attack is NOT validated; attacking an opponent's
    //     unit is the whole point.
    // Single-player passes through unconditionally.
    // ------------------------------------------------------------------ //

    private static bool ValidateLocalOwnership(PlayerCommand cmd)
    {
        if (!NetworkManagerRTS.IsMultiplayerEnabled) return true;

        int local = NetworkManagerRTS.LocalPlayerId;
        if (cmd.playerId != local)
        {
            Debug.LogWarning($"[CommandDispatcher] Rejecting local command — " +
                             $"cmd.playerId={cmd.playerId} but LocalPlayerId={local}. " +
                             "You can only issue commands as the local player.");
            return false;
        }

        if (cmd.selectedEntityIds != null)
        {
            for (int i = 0; i < cmd.selectedEntityIds.Length; i++)
            {
                string id = cmd.selectedEntityIds[i];
                if (string.IsNullOrEmpty(id)) continue;

                GameEntity e = EntityRegistry.Find(id);
                if (e == null) continue;     // freshly-killed entity slipped through — let the executor no-op

                if (e.ownerPlayerId != local)
                {
                    Debug.LogWarning($"[CommandDispatcher] Rejecting local command — " +
                                     $"selected entity '{id}' is owned by player " +
                                     $"{e.ownerPlayerId}, not {local}.");
                    return false;
                }
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ //
    // Formation helper — mirrored from UnitSelector so the dispatcher is
    // self-contained (no static back-reference into a MonoBehaviour).
    // ------------------------------------------------------------------ //

    private static Vector3[] GetFormationPositions(Vector3 center, int count)
    {
        if (count <= 0) return System.Array.Empty<Vector3>();

        Vector3[] positions = new Vector3[count];
        int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(count)));

        for (int i = 0; i < count; i++)
        {
            int row = i / cols;
            int col = i % cols;
            float offsetX = (col - (cols - 1) * 0.5f) * FormationSpacing;
            float offsetZ = -row * FormationSpacing;
            positions[i] = center + new Vector3(offsetX, 0f, offsetZ);
        }
        return positions;
    }
}
