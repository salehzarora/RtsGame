using UnityEngine;

/// <summary>
/// Base class for every Interactive Tactical Map feature — destructible
/// bridges, explosive fuel tanks, garrison buildings, watch towers, and tunnel
/// entrances. Provides the shared plumbing every map object needs:
///
///   • A cached sibling <see cref="GameEntity"/> (the stable, network-safe
///     identity — the project already replicates damage / death / ownership
///     through this, so map objects piggyback on the proven pipeline instead
///     of inventing a parallel one).
///   • An <see cref="Authoritative"/> gate — true in single-player and on the
///     Photon MasterClient. Use this to apply area damage / state changes
///     EXACTLY ONCE; non-authoritative clients only render visuals and receive
///     the authoritative outcome through the existing event bus.
///
/// Networking model (shared by all map objects):
///   • NO PhotonView, NO PhotonNetwork.Instantiate. Map objects are scene-baked
///     and carry a deterministic <see cref="GameEntity.EntityId"/> stamped by
///     Tools → RTS → Multiplayer Prep → Add GameEntity To Scene Objects.
///   • Destruction syncs through the existing master-authoritative
///     ApplyDamage / EntityDestroyed events (see <see cref="DestructibleMapObject"/>).
///   • Occupancy / travel state syncs through <see cref="MapInteractableNetworkEvents"/>
///     using the same RaiseEvent pattern as the APC transport.
///
/// Setup:
///   Map objects are normally created via the editor helper
///   (Tools → RTS → Map → Create …). If you build one by hand, give it a
///   <see cref="GameEntity"/> with entityType = MapObject and the desired
///   ownerPlayerId (−1 / Neutral for capturable or world objects).
/// </summary>
[RequireComponent(typeof(GameEntity))]
public abstract class MapInteractable : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Cached identity
    // ------------------------------------------------------------------ //

    private GameEntity cachedEntity;

    /// <summary>
    /// The sibling <see cref="GameEntity"/>. Resolved lazily so it is valid
    /// even if a subclass reads it before its own Awake runs.
    /// </summary>
    protected GameEntity Entity
    {
        get
        {
            if (cachedEntity == null) cachedEntity = GetComponent<GameEntity>();
            return cachedEntity;
        }
    }

    /// <summary>Stable network id, or empty string if the entity is missing.</summary>
    public string EntityId => Entity != null ? Entity.EntityId : string.Empty;

    // ------------------------------------------------------------------ //
    // Authority
    // ------------------------------------------------------------------ //

    /// <summary>
    /// True when THIS client is allowed to apply gameplay state changes that
    /// must happen exactly once (area damage, occupant kills, capture flips):
    ///   • single-player — always authoritative.
    ///   • multiplayer  — only the Photon MasterClient.
    /// Non-authoritative clients still render visuals and apply the outcome
    /// they receive over the network.
    /// </summary>
    public static bool Authoritative =>
        !NetworkManagerRTS.IsMultiplayerEnabled || NetworkManagerRTS.IsMaster;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    protected virtual void Awake()
    {
        cachedEntity = GetComponent<GameEntity>();
    }

    // ------------------------------------------------------------------ //
    // Shared helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// True when <paramref name="go"/> is infantry (foot soldier / worker /
    /// RPG soldier) — the unit class allowed to enter garrisons, watch towers,
    /// and tunnels by default. Resolved from <see cref="UnitCategory"/>:
    /// Category.Infantry counts as infantry; everything else does not.
    /// </summary>
    public static bool IsInfantry(GameObject go)
    {
        if (go == null) return false;
        UnitCategory uc = go.GetComponent<UnitCategory>() ?? go.GetComponentInParent<UnitCategory>();
        return uc != null && uc.category == UnitCategory.Category.Infantry;
    }

    /// <summary>
    /// Owner id of <paramref name="go"/> via its <see cref="GameEntity"/>, or
    /// <see cref="GameEntity.NeutralOwnerId"/> when none is found.
    /// </summary>
    public static int OwnerOf(GameObject go)
    {
        if (go == null) return GameEntity.NeutralOwnerId;
        GameEntity e = go.GetComponent<GameEntity>() ?? go.GetComponentInParent<GameEntity>();
        return e != null ? e.ownerPlayerId : GameEntity.NeutralOwnerId;
    }
}
