using UnityEngine;

/// <summary>
/// Discriminator for <see cref="PlayerCommand"/>. The dispatcher switches on
/// this to decide which executor to run.
/// </summary>
public enum CommandType
{
    Move,
    Attack,
    Build,
    Produce,
    UnloadTransport,
    Patrol,
    Stop,
}

/// <summary>
/// Plain-old-data record of a player intent. Built by client-side UI code
/// (<see cref="UnitSelector"/>, <see cref="RTSHUD"/>) and handed to
/// <see cref="CommandDispatcher"/>.
///
/// What this is — and isn't:
///   • IS: a self-describing description of "player X wants to do Y to/with
///     entities Z". Everything inside is value-typed or string — i.e. it
///     could be serialised over the wire as-is in a future Photon / Netcode
///     phase.
///   • IS NOT: a representation of HOW the command runs. The dispatcher
///     owns that. Two clients in a future networked game might receive the
///     same PlayerCommand but interpret formation/order details differently
///     — the command is the canonical INPUT, not the canonical OUTPUT.
///
/// Building commands:
///   Use the static factory methods below. They guarantee a unique
///   <see cref="commandId"/> and a sensible timestamp without callers
///   having to thread those concerns themselves.
///
/// Field reference:
///   • <see cref="commandId"/>      — monotonic, allocated by Allocate().
///   • <see cref="playerId"/>       — issuer (Player = 0, Enemy AI = 1).
///   • <see cref="commandType"/>    — what kind of intent.
///   • <see cref="selectedEntityIds"/> — entities the issuer is commanding
///     (the SOURCE side). Empty for commands with no source (none today).
///   • <see cref="targetEntityId"/> — single target entity, if any. Empty
///     string when not applicable. We pick string-or-empty over nullable
///     to keep future JSON serialisation simple.
///   • <see cref="targetPosition"/>  — world XZ point for movement / attack-
///     move / patrol orders. Validity is gated by <see cref="hasTargetPosition"/>.
///   • <see cref="productionType"/>  — "Soldier", "Worker", "Humvee", etc.
///     Only set for <see cref="CommandType.Produce"/> / <see cref="CommandType.Build"/>.
///   • <see cref="timestamp"/>      — Time.unscaledTime at construction.
///     Future networking will replace this with a tick number.
/// </summary>
[System.Serializable]
public class PlayerCommand
{
    // ------------------------------------------------------------------ //
    // Static command-id allocator
    // ------------------------------------------------------------------ //

    private static int s_nextCommandId = 1;

    /// <summary>
    /// Monotonic command-id allocator. Static state lives in the AppDomain;
    /// resets on domain reload (entering / exiting Play mode). Good enough
    /// for log-correlation today; future networking will share the counter
    /// across clients.
    /// </summary>
    private static int AllocateId() => s_nextCommandId++;

    // ------------------------------------------------------------------ //
    // Fields — public for easy serialisation later
    // ------------------------------------------------------------------ //

    public int          commandId;
    public int          playerId;
    public CommandType  commandType;
    public string[]     selectedEntityIds;
    public string       targetEntityId;
    public Vector3      targetPosition;
    public bool         hasTargetPosition;
    public string       productionType;
    public float        timestamp;

    /// <summary>
    /// Pre-allocated entity id the issuer mints for the PRIMARY entity this
    /// command will spawn. The receiver applies it via
    /// <see cref="GameEntity.SetNextSpawnId"/> so the spawned entity ends up
    /// with the same id on every client.
    /// </summary>
    /// <remarks>
    /// Populated for <see cref="CommandType.Produce"/> (spawned unit id) and
    /// <see cref="CommandType.Build"/> (construction-site id). Empty for
    /// commands that don't spawn anything (Move, Attack, UnloadTransport).
    /// </remarks>
    public string spawnEntityId;

    /// <summary>
    /// Pre-allocated entity id for a SECONDARY entity this command will
    /// eventually produce. Only Build uses this today — it carries the id
    /// the final building will adopt when the construction site completes.
    /// </summary>
    public string secondaryEntityId;

    // ------------------------------------------------------------------ //
    // Factory methods — preferred way to construct commands
    // ------------------------------------------------------------------ //

    public static PlayerCommand Move(int playerId, string[] selectedIds, Vector3 worldPoint)
    {
        return new PlayerCommand
        {
            commandId         = AllocateId(),
            playerId          = playerId,
            commandType       = CommandType.Move,
            selectedEntityIds = selectedIds ?? System.Array.Empty<string>(),
            targetEntityId    = "",
            targetPosition    = worldPoint,
            hasTargetPosition = true,
            productionType    = "",
            timestamp         = Time.unscaledTime,
            spawnEntityId     = "",
            secondaryEntityId = "",
        };
    }

    public static PlayerCommand Attack(int playerId, string[] selectedIds, string targetEntityId)
    {
        return new PlayerCommand
        {
            commandId         = AllocateId(),
            playerId          = playerId,
            commandType       = CommandType.Attack,
            selectedEntityIds = selectedIds ?? System.Array.Empty<string>(),
            targetEntityId    = targetEntityId ?? "",
            targetPosition    = Vector3.zero,
            hasTargetPosition = false,
            productionType    = "",
            timestamp         = Time.unscaledTime,
            spawnEntityId     = "",
            secondaryEntityId = "",
        };
    }

    public static PlayerCommand Produce(int playerId, string producerEntityId, string productionType,
                                        string spawnEntityId)
    {
        return new PlayerCommand
        {
            commandId         = AllocateId(),
            playerId          = playerId,
            commandType       = CommandType.Produce,
            // Producer is the SOURCE — encode it as the only selected entity
            // so the executor can look it up via EntityRegistry.
            selectedEntityIds = new[] { producerEntityId ?? "" },
            targetEntityId    = "",
            targetPosition    = Vector3.zero,
            hasTargetPosition = false,
            productionType    = productionType ?? "",
            timestamp         = Time.unscaledTime,
            // Pre-allocated id the spawned unit will adopt — same on every
            // client so subsequent Move/Attack commands targeting it resolve.
            spawnEntityId     = spawnEntityId ?? "",
            secondaryEntityId = "",
        };
    }

    public static PlayerCommand Build(int playerId, string dozerEntityId, string buildingType, Vector3 worldPoint,
                                      string siteEntityId, string finalBuildingEntityId)
    {
        // Emitted by BuildingPlacementManager.TryPlace at the moment the
        // player confirms a Dozer-driven placement (mouse click on ground).
        // The interactive ghost-preview / placement-mode UX is still owned
        // by BuildingPlacementManager — this command represents the final
        // "place site here" intent, which is the part future networking
        // will replicate.
        //
        // Two ids are minted upfront: the construction site adopts
        // <paramref name="siteEntityId"/>, the final completed building
        // adopts <paramref name="finalBuildingEntityId"/>. Both ids travel in
        // a single command so completion timing differences between clients
        // can't desync the final id.
        return new PlayerCommand
        {
            commandId         = AllocateId(),
            playerId          = playerId,
            commandType       = CommandType.Build,
            selectedEntityIds = new[] { dozerEntityId ?? "" },
            targetEntityId    = "",
            targetPosition    = worldPoint,
            hasTargetPosition = true,
            productionType    = buildingType ?? "",
            timestamp         = Time.unscaledTime,
            spawnEntityId     = siteEntityId          ?? "",
            secondaryEntityId = finalBuildingEntityId ?? "",
        };
    }

    public static PlayerCommand UnloadTransport(int playerId, string[] apcEntityIds)
    {
        // Multi-APC unload — `selectedEntityIds` carries EVERY transport the
        // player wants to unload, mirroring the existing UnloadAll fan-out
        // in RTSHUD. CommandDispatcher walks the array and runs each APC's
        // local unload sequence independently so their staggered drops
        // overlap in time.
        return new PlayerCommand
        {
            commandId         = AllocateId(),
            playerId          = playerId,
            commandType       = CommandType.UnloadTransport,
            selectedEntityIds = apcEntityIds ?? System.Array.Empty<string>(),
            targetEntityId    = "",
            targetPosition    = Vector3.zero,
            hasTargetPosition = false,
            productionType    = "",
            timestamp         = Time.unscaledTime,
            spawnEntityId     = "",
            secondaryEntityId = "",
        };
    }

    public static PlayerCommand Stop(int playerId, string[] selectedIds)
    {
        return new PlayerCommand
        {
            commandId         = AllocateId(),
            playerId          = playerId,
            commandType       = CommandType.Stop,
            selectedEntityIds = selectedIds ?? System.Array.Empty<string>(),
            targetEntityId    = "",
            targetPosition    = Vector3.zero,
            hasTargetPosition = false,
            productionType    = "",
            timestamp         = Time.unscaledTime,
            spawnEntityId     = "",
            secondaryEntityId = "",
        };
    }

    public static PlayerCommand Patrol(int playerId, string[] selectedIds, Vector3 worldPoint)
    {
        return new PlayerCommand
        {
            commandId         = AllocateId(),
            playerId          = playerId,
            commandType       = CommandType.Patrol,
            selectedEntityIds = selectedIds ?? System.Array.Empty<string>(),
            targetEntityId    = "",
            targetPosition    = worldPoint,
            hasTargetPosition = true,
            productionType    = "",
            timestamp         = Time.unscaledTime,
            spawnEntityId     = "",
            secondaryEntityId = "",
        };
    }

    // ------------------------------------------------------------------ //
    // Diagnostics
    // ------------------------------------------------------------------ //

    public override string ToString()
    {
        int n = selectedEntityIds != null ? selectedEntityIds.Length : 0;
        string targetSeg = !string.IsNullOrEmpty(targetEntityId)
            ? $" → '{targetEntityId}'"
            : (hasTargetPosition ? $" → {targetPosition}" : "");
        string typeSeg   = !string.IsNullOrEmpty(productionType) ? $" [{productionType}]" : "";
        return $"Cmd#{commandId} p{playerId} {commandType}{typeSeg} ({n} src){targetSeg}";
    }

    // ------------------------------------------------------------------ //
    // Network (de)serialisation
    //
    // Photon's <c>RaiseEvent</c> accepts a single <c>object</c> payload; the
    // built-in serialiser knows the primitive types + Vector3 + Quaternion +
    // object[]. We pack into <c>object[]</c> (with strings boxed inside an
    // inner <c>object[]</c> for the selectedEntityIds slot) so no custom
    // serialiser registration is needed.
    //
    // Payload schema (index → field):
    //   0:  int          commandId
    //   1:  int          playerId
    //   2:  byte         commandType (cast from enum)
    //   3:  object[]?    selectedEntityIds (each element a string, may be null)
    //   4:  string       targetEntityId   ("" when none)
    //   5:  Vector3      targetPosition
    //   6:  bool         hasTargetPosition
    //   7:  string       productionType   ("" when none)
    //   8:  float        timestamp
    //   9:  string       spawnEntityId      (added v2)
    //   10: string       secondaryEntityId  (added v2)
    //
    // Schema changes need lockstep client deploys — bump
    // <see cref="NetworkPayloadVersion"/> if the layout changes.
    // ------------------------------------------------------------------ //

    /// <summary>Bumped when <see cref="ToNetworkPayload"/> layout changes.</summary>
    public const byte NetworkPayloadVersion = 2;

    /// <summary>Expected payload length for the current version.</summary>
    private const int PayloadLength = 11;

    public object[] ToNetworkPayload()
    {
        object[] selBoxed = null;
        if (selectedEntityIds != null)
        {
            selBoxed = new object[selectedEntityIds.Length];
            for (int i = 0; i < selectedEntityIds.Length; i++)
                selBoxed[i] = selectedEntityIds[i] ?? string.Empty;
        }

        return new object[]
        {
            commandId,
            playerId,
            (byte)commandType,
            selBoxed,
            targetEntityId    ?? string.Empty,
            targetPosition,
            hasTargetPosition,
            productionType    ?? string.Empty,
            timestamp,
            spawnEntityId     ?? string.Empty,
            secondaryEntityId ?? string.Empty,
        };
    }

    public static PlayerCommand FromNetworkPayload(object[] data)
    {
        if (data == null || data.Length < PayloadLength)
        {
            UnityEngine.Debug.LogError($"[PlayerCommand] FromNetworkPayload: payload null or too " +
                                       $"short (got {data?.Length ?? 0}, expected {PayloadLength}). " +
                                       "Schema mismatch between client builds?");
            return null;
        }

        // Unbox the inner string array (Photon serialises string[] as object[]
        // in our packing scheme).
        string[] sel = null;
        if (data[3] is object[] boxed)
        {
            sel = new string[boxed.Length];
            for (int i = 0; i < boxed.Length; i++)
                sel[i] = boxed[i] as string ?? string.Empty;
        }

        try
        {
            return new PlayerCommand
            {
                commandId         = (int)data[0],
                playerId          = (int)data[1],
                commandType       = (CommandType)(byte)data[2],
                selectedEntityIds = sel,
                targetEntityId    = data[4] as string ?? string.Empty,
                targetPosition    = (Vector3)data[5],
                hasTargetPosition = (bool)data[6],
                productionType    = data[7] as string ?? string.Empty,
                timestamp         = (float)data[8],
                spawnEntityId     = data[9]  as string ?? string.Empty,
                secondaryEntityId = data[10] as string ?? string.Empty,
            };
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[PlayerCommand] FromNetworkPayload: " +
                                       $"cast failed — {ex.Message}. Schema mismatch between clients?");
            return null;
        }
    }
}
