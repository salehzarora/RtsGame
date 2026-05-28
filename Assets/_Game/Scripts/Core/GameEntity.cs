using UnityEngine;

/// <summary>
/// Classification of every networkable game object. Drives <see cref="EntityRegistry"/>
/// lookups and any future visibility / replication rules.
/// </summary>
public enum EntityType
{
    Unit,
    Building,
    Aircraft,
    Resource,
    Projectile,
    // Neutral, scene-baked interactable map features (destructible bridges,
    // fuel tanks, garrison buildings, watch towers, tunnel entrances). Added
    // at the END so existing serialized enum indices are preserved.
    MapObject,
}

/// <summary>
/// Stable runtime identity for every "networkable" object — units, buildings,
/// aircraft, resource nodes, optionally projectiles. Single source of truth
/// for ownership, team, prefab type, and entity ID used by
/// <see cref="EntityRegistry"/> + <see cref="CommandDispatcher"/>.
///
/// This component is the FOUNDATION for a future Photon / Netcode multiplayer
/// pass. Today nothing networks; the IDs are local only. The contract:
///   • <see cref="EntityId"/> is unique per scene-instance and stable for the
///     entire runtime of that instance.
///   • <see cref="OwnerPlayerId"/> + <see cref="TeamId"/> follow the project
///     mapping below — Player = 0, Enemy AI = 1.
///   • <see cref="PrefabTypeId"/> is a human-readable string ("Soldier", "APC",
///     "Barracks") used for future PhotonNetwork.Instantiate / Netcode prefab
///     lookups. We do NOT use prefab GUIDs because they're not stable across
///     refactors.
///
/// Setup notes:
///   • The component auto-registers with EntityRegistry on Awake and
///     unregisters on OnDestroy. No manual wiring required.
///   • If a sibling <see cref="Health"/> is present, owner/team default to
///     match its team (Player ↔ 0, Enemy ↔ 1). Inspector values override
///     this behaviour — flip <see cref="overrideTeamFromHealth"/> if you
///     need owner ≠ team or asymmetric setups later.
///   • The editor tool "Tools → RTS → Multiplayer Prep → Add GameEntity To
///     Prefabs" stamps this component onto every standard prefab and assigns
///     <see cref="prefabTypeId"/>.
/// </summary>
[DisallowMultipleComponent]
public class GameEntity : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Project-wide constants — kept here so every system can read them
    // without re-deriving the mapping.
    // ------------------------------------------------------------------ //

    /// <summary>Reserved id for the local player faction.</summary>
    public const int PlayerOwnerId = 0;

    /// <summary>Reserved id for the enemy AI faction.</summary>
    public const int EnemyOwnerId  = 1;

    /// <summary>Sentinel — unowned / world / neutral entities.</summary>
    public const int NeutralOwnerId = -1;

    /// <summary>
    /// Player id to stamp into <see cref="PlayerCommand.playerId"/> when the
    /// LOCAL client issues a command. In multiplayer this is
    /// <see cref="NetworkManagerRTS.LocalPlayerId"/>; in single-player it
    /// falls back to <see cref="PlayerOwnerId"/> (0). Use this everywhere
    /// you previously hard-coded <c>GameEntity.PlayerOwnerId</c> for command
    /// issuance.
    /// </summary>
    public static int LocalCommandPlayerId
    {
        get
        {
            int pid = NetworkManagerRTS.LocalPlayerId;
            return pid >= 0 ? pid : PlayerOwnerId;
        }
    }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Identity")]
    [Tooltip("Globally-unique id assigned the first time this entity wakes up. " +
             "Leave empty to auto-generate. Inspector edits during play are " +
             "intentionally NOT respected — id is meant to be stable for the " +
             "lifetime of the instance.")]
    [SerializeField] private string entityId = "";

    [Tooltip("Human-readable prefab class label used by future networked " +
             "instantiation. e.g. 'Soldier', 'Humvee', 'CommandCenter'. " +
             "Set automatically by the prefab repair tool.")]
    public string prefabTypeId = "";

    [Header("Ownership")]
    [Tooltip("Faction id of the player who owns this entity. Player = 0, " +
             "Enemy AI = 1, Neutral / Resource = -1.")]
    public int ownerPlayerId = NeutralOwnerId;

    [Tooltip("Team affiliation. Today this matches ownerPlayerId, but the " +
             "fields are split so future 2-vs-2 or shared-control modes have " +
             "a hook in place without a schema change.")]
    public int teamId = NeutralOwnerId;

    [Tooltip("If a sibling Health component exists, derive owner/team from " +
             "Health.Team at Awake. Disable for entities that should be team-" +
             "neutral regardless of Health setup (e.g. resource nodes).")]
    public bool overrideTeamFromHealth = true;

    [Header("Classification")]
    [Tooltip("What category of entity this is — drives EntityRegistry lookup " +
             "and any future replication / culling rules.")]
    public EntityType entityType = EntityType.Unit;

    // ------------------------------------------------------------------ //
    // Public read-only API
    // ------------------------------------------------------------------ //

    /// <summary>Stable id. Generated at Awake if the serialized value is empty.</summary>
    public string EntityId => entityId;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        // A serialized id means this is a SCENE-BAKED entity (the editor tools
        // stamped a deterministic id). A spawned unit's prefab ships with an
        // empty id and gets one from the spawn-id slot below. We capture this
        // BEFORE id resolution so the match-start self-heal at the end of Awake
        // can target scene-baked entities only.
        bool sceneBaked = !string.IsNullOrEmpty(entityId);

        // 1. Resolve the entity id. Priority order:
        //    a) entityId baked into the serialized field (scene-stamped object).
        //    b) NextSpawnId preset — the spawner pushed a deterministic id
        //       right before Instantiate, intended for THIS entity.
        //    c) Fresh GUID — single-player / non-networked spawn.
        if (string.IsNullOrEmpty(entityId))
        {
            string preset = ConsumeNextSpawnId();
            entityId = !string.IsNullOrEmpty(preset)
                ? preset
                : System.Guid.NewGuid().ToString("N");
        }

        // 2. Defaults: pull owner/team from sibling Health if present and the
        //    user hasn't opted out via the Inspector.
        if (overrideTeamFromHealth)
            SyncFromHealth();

        // 3. Register with the global lookup. Registry handles dup-id warnings
        //    so we don't fail silently on bad data.
        EntityRegistry.Register(this);

        // 4. Match-start self-heal for scene-baked entities (Phase 10.16).
        //    The starting Dozer lives inside a base that GameplayWorldRoot keeps
        //    INACTIVE until MatchStart, so it registers AFTER the one-shot
        //    MultiplayerMatchStarter → RemapAllForLocalPerspective pass and can
        //    miss it — leaving Health.team on the prefab default and read as
        //    hostile by friendly auto-attack. Produced units never hit this
        //    because CommandDispatcher.ExecuteProduce stamps ApplyOwnership at
        //    spawn. Route scene-baked entities through the SAME ApplyOwnership
        //    path now so owner/team/color and combat perspective all agree.
        //
        //    Guards keep this inert outside the one situation it fixes:
        //      • sceneBaked            — never touches spawned units' path.
        //      • ownerPlayerId >= 0    — skip neutral (resource nodes).
        //      • IsMultiplayerEnabled  — no-op in single-player.
        //      • IsMatchStarted        — only after slot mapping is authoritative.
        //      • LocalPlayerId >= 0    — our slot is known, so the Player/Enemy
        //                                perspective resolves correctly.
        //    Idempotent: a later RemapAllForLocalPerspective re-apply is a no-op.
        if (sceneBaked
            && ownerPlayerId >= 0
            && NetworkManagerRTS.IsMultiplayerEnabled
            && NetworkMatchCoordinator.Instance != null
            && NetworkMatchCoordinator.Instance.IsMatchStarted
            && NetworkManagerRTS.LocalPlayerId >= 0)
        {
            Debug.Log($"[GameEntity] Scene-baked '{name}' (owner {ownerPlayerId}) " +
                      "registered mid-match — adopting canonical ownership via ApplyOwnership.");
            ApplyOwnership(ownerPlayerId);
        }
    }

    private void OnDestroy()
    {
        EntityRegistry.Unregister(this);
    }

    // ------------------------------------------------------------------ //
    // Public helpers — also used by the editor prefab/scene tools
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Look up a sibling <see cref="Health"/> and copy its team enum into
    /// <see cref="ownerPlayerId"/> + <see cref="teamId"/>. Player → 0,
    /// Enemy → 1. No-op if no Health exists.
    /// </summary>
    public void SyncFromHealth()
    {
        Health h = GetComponent<Health>();
        if (h == null) h = GetComponentInChildren<Health>(true);
        if (h == null) return;

        switch (h.team)
        {
            case Health.Team.Player:
                ownerPlayerId = PlayerOwnerId;
                teamId        = PlayerOwnerId;
                break;
            case Health.Team.Enemy:
                ownerPlayerId = EnemyOwnerId;
                teamId        = EnemyOwnerId;
                break;
        }
    }

    /// <summary>
    /// Editor-only helper. Forcibly clears the entity id so the next Awake
    /// generates a fresh one. Used by the prefab tool to make sure freshly-
    /// stamped prefabs don't all share one baked id.
    /// </summary>
    public void EditorResetId()
    {
        entityId = "";
    }

    // ------------------------------------------------------------------ //
    // Phase 3: multiplayer ownership + team perspective
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Set the canonical ownership of this entity from a network/match source.
    /// Updates both <see cref="ownerPlayerId"/> and <see cref="teamId"/>, and
    /// in multiplayer mode also re-keys the sibling <see cref="Health"/>'s
    /// team enum into the LOCAL client's perspective
    /// (own = <c>Player</c>, opponent = <c>Enemy</c>).
    ///
    /// Used by:
    ///   • <see cref="CommandDispatcher.ExecuteProduce"/> / <c>ExecuteBuild</c>
    ///     after the spawn, so a Soldier produced by Player 1 carries
    ///     <c>ownerPlayerId = 1</c> on every client.
    ///   • <see cref="MultiplayerMatchStarter"/> at match start, once the
    ///     local player id is known, so scene-baked entities adopt the
    ///     correct perspective on each client.
    /// </summary>
    /// <summary>
    /// Fires AFTER <see cref="ApplyOwnership"/> has stamped the new owner
    /// and the color applier has repainted. Components that need to react
    /// to ownership transitions (e.g. <see cref="UnitMovement"/> toggling
    /// its NavMeshAgent based on whether THIS client is the owner) subscribe
    /// in their Awake and re-evaluate on every fire.
    /// </summary>
    public event System.Action<int> OnOwnershipApplied;

    public void ApplyOwnership(int newOwnerId)
    {
        ownerPlayerId = newOwnerId;
        teamId        = newOwnerId;
        ApplyLocalTeamPerspective();

        // Phase 10.7 — push the owner color through the canonical applier.
        OwnerColorApplier.ApplyToEntity(gameObject);

        // Phase 10.14 — let movement / per-owner gameplay components
        // re-gate themselves now that the canonical owner is set.
        OnOwnershipApplied?.Invoke(newOwnerId);
    }

    /// <summary>
    /// In multiplayer, push <see cref="ownerPlayerId"/> into the sibling
    /// <see cref="Health"/>'s team enum using the LOCAL-client perspective:
    ///   • owner == LocalPlayerId → <see cref="Health.Team.Player"/>
    ///   • otherwise               → <see cref="Health.Team.Enemy"/>
    /// This is the bridge between the canonical multi-player owner id and
    /// the existing 2-team combat code. No-op in single-player so existing
    /// Player/Enemy scene bake values are preserved.
    /// </summary>
    public void ApplyLocalTeamPerspective()
    {
        if (!NetworkManagerRTS.IsMultiplayerEnabled) return;

        Health h = GetComponent<Health>();
        if (h == null) h = GetComponentInChildren<Health>(true);
        if (h == null) return;

        int local = NetworkManagerRTS.LocalPlayerId;
        Health.Team next = (ownerPlayerId == local)
            ? Health.Team.Player
            : Health.Team.Enemy;
        if (h.team != next) h.team = next;
    }

    /// <summary>
    /// Walk every registered <see cref="GameEntity"/> and re-apply the local
    /// team perspective. Called once at multiplayer match start so scene-
    /// baked entities adopt the correct Player/Enemy mapping for THIS
    /// client. Cheap — scene-entity counts are in the dozens, not thousands.
    /// </summary>
    public static void RemapAllForLocalPerspective()
    {
        foreach (GameEntity e in EntityRegistry.All())
            if (e != null) e.ApplyLocalTeamPerspective();
    }

    // ------------------------------------------------------------------ //
    // Runtime defensive helper
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the <see cref="GameEntity"/> on <paramref name="go"/>, adding
    /// one (and registering it) if it's missing. Logs a warning so the
    /// developer notices and runs the prefab tool — the runtime path is a
    /// SAFETY NET, not the intended workflow.
    /// </summary>
    public static GameEntity EnsureOn(GameObject go)
    {
        if (go == null) return null;
        GameEntity e = go.GetComponent<GameEntity>();
        if (e != null) return e;

        e = go.AddComponent<GameEntity>();
        // AddComponent invokes Awake immediately in play mode, so the GUID
        // + EntityRegistry registration have already happened by the time
        // we return. We only need to nudge the user.
        Debug.LogWarning($"[GameEntity] '{go.name}' had no GameEntity — added at runtime. " +
                         "Run Tools → RTS → Multiplayer Prep → Add GameEntity To Prefabs " +
                         "(and …Scene Objects) so this is baked instead of a runtime fixup.");
        return e;
    }

    // ------------------------------------------------------------------ //
    // Late-bound id override — used by deterministic network spawning when
    // the spawner needs to re-key an already-instantiated entity.
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Replace the live entity id. Re-registers with <see cref="EntityRegistry"/>
    /// under the new id. Intended for runtime use ONLY when a freshly-spawned
    /// entity needs to be re-keyed to match a networked spawn id and the
    /// preferred <see cref="SetNextSpawnId"/> slot wasn't used.
    ///
    /// No-op if <paramref name="newId"/> matches the current id or is empty.
    /// </summary>
    public void OverrideEntityId(string newId)
    {
        if (string.IsNullOrEmpty(newId)) return;
        if (newId == entityId) return;

        // Unregister under the old id, swap, re-register.
        EntityRegistry.Unregister(this);
        entityId = newId;
        EntityRegistry.Register(this);
    }

    /// <summary>
    /// Editor-only setter that bakes <paramref name="newId"/> into the
    /// serialized <c>entityId</c> field. The scene-stamping tool uses this
    /// to assign deterministic ids that survive into runtime.
    /// </summary>
    public void EditorSetEntityId(string newId)
    {
        entityId = newId ?? "";
    }

    // ------------------------------------------------------------------ //
    // Static next-spawn-id slot — deterministic id assignment for spawned
    // entities. The pattern:
    //
    //   GameEntity.SetNextSpawnId("p0-7");
    //   GameObject go = Instantiate(prefab, pos, rot);
    //   GameEntity.SetNextSpawnId(null);   // defensive clear
    //
    // GameEntity.Awake on the spawned object consumes the preset before
    // generating a GUID. The Awake-time consume + the post-Instantiate
    // defensive clear together guarantee no leak if a future spawn path
    // forgets to push.
    //
    // Single-slot (not stack) — there is no nested-spawn scenario in this
    // project. If one ever appears, swap to a Stack<string>.
    // ------------------------------------------------------------------ //

    private static string s_nextSpawnId;

    /// <summary>
    /// Preset the id to be applied to the next-instantiated
    /// <see cref="GameEntity"/>. Pass null to clear.
    /// </summary>
    public static void SetNextSpawnId(string id)
    {
        s_nextSpawnId = id;
    }

    /// <summary>
    /// Consume the preset id (clears the slot). Returns null when no preset
    /// is queued. Called automatically by <see cref="Awake"/>.
    /// </summary>
    public static string ConsumeNextSpawnId()
    {
        string r = s_nextSpawnId;
        s_nextSpawnId = null;
        return r;
    }
}
