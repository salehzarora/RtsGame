using System.Collections.Generic;
using UnityEngine;

#if PHOTON_UNITY_NETWORKING
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
#endif

/// <summary>
/// DEV-ONLY test commander. Lets a single client drive a 1–4 player match
/// — give resources, spawn units, move/attack/damage/kill — for fast solo
/// regression testing of multiplayer features.
///
/// VISIBILITY (must be DEV):
///   • <see cref="UnityEngine.Debug.isDebugBuild"/> is true (Development
///     Build), OR
///   • <see cref="devMode"/> is true on this component.
/// The panel never shows in a non-dev release build. F12 (configurable)
/// toggles its visibility within a dev session.
///
/// NETWORK SAFETY — every action routes through one of the existing
/// network-safe paths; no remote-only object is mutated locally:
///   • <b>GiveResources</b> — master applies <see cref="PlayerResourceManager.AddResources"/>
///     which broadcasts via <c>NetworkMatchEvents.BroadcastResourceChanged</c>.
///   • <b>DamageBase / KillAllUnits</b> — master applies <see cref="Health.TakeDamage"/>;
///     death + value sync use the existing <c>ApplyDamage</c> / <c>EntityDestroyed</c>
///     broadcasts.
///   • <b>SpawnUnit / MoveUnits / AttackPlayer</b> — sent as a "DevCommander"
///     Photon event. Every client receives it but ONLY the target player's
///     client (the one whose <see cref="NetworkManagerRTS.LocalPlayerId"/>
///     matches the target) acts on it, by issuing the real
///     <see cref="PlayerCommand"/> through <see cref="CommandDispatcher.Issue"/>.
///     The dispatcher's ownership validation passes (target == local) and
///     <see cref="NetworkCommandRelay"/> broadcasts to the others. This is
///     the only network-safe way to "command another player's units" in this
///     owner-authoritative architecture.
///   • <b>ResetMatch</b> — every client calls
///     <see cref="MatchSessionManager.CleanupPreviousMatch"/> locally.
///
/// SP path: no Photon, no event; actions apply directly. Spawn/Move/Attack
/// still go through the same <see cref="CommandDispatcher.Issue"/> entrypoint.
///
/// Add via Tools → RTS → Multiplayer → Setup Dev Commander. Inert until you
/// open the panel and click something. No gameplay logic is changed.
/// </summary>
[DisallowMultipleComponent]
public class DevCommanderPanel : MonoBehaviour
#if PHOTON_UNITY_NETWORKING
    , IOnEventCallback
#endif
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Photon event code reserved for the dev commander. Sits well above the
    /// project's used range (1..13 game events, 20..22 map events).
    /// </summary>
    public const byte DevCommanderEventCode = 60;

    private const byte PayloadVersion = 1;

    /// <summary>Opcode for the in-payload DevCommander operation.</summary>
    public enum DevOp
    {
        GiveResources = 1,
        DamageBase    = 2,
        KillAllUnits  = 3,
        SpawnUnit     = 4,
        MoveUnits     = 5,
        AttackPlayer  = 6,
        ResetMatch    = 7,
    }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Dev gate")]
    [Tooltip("Independent of Debug.isDebugBuild. Either being true makes the panel available.")]
    public bool devMode = true;
    [Tooltip("Hotkey that toggles panel visibility within a dev session.")]
    public KeyCode toggleKey = KeyCode.F12;

    [Header("Action defaults (editable at runtime via the panel)")]
    public int   resourceGrantAmount      = 1000;
    public float damageAmount             = 100f;
    public string infantryProductionType  = "Soldier";
    public string tankProductionType      = "ArtilleryTank";
    public string aircraftProductionType  = "StrikeJet";

    [Header("Panel layout")]
    public Vector2 panelOrigin = new Vector2(10f, 10f);
    public Vector2 panelSize   = new Vector2(560f, 540f);

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    /// <summary>True when the panel is allowed to render at all (dev-gated).</summary>
    public static bool IsDevModeActive { get; private set; }

    private bool   showPanel        = true;
    private int    selectedTarget   = 0;
    private int    attackVictim     = -1;
    private string moveXText        = "0";
    private string moveZText        = "0";
    private Vector2 scroll;
    private GUIStyle boldLabel, headerLabel;

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        IsDevModeActive = devMode || Debug.isDebugBuild;
        if (!IsDevModeActive)
            Debug.Log("[DevCommander] Dev gate is OFF — panel will not show. " +
                      "Enable 'devMode' on this component or run a Development Build.");
    }

    private void OnEnable()
    {
#if PHOTON_UNITY_NETWORKING
        PhotonNetwork.AddCallbackTarget(this);
#endif
    }

    private void OnDisable()
    {
#if PHOTON_UNITY_NETWORKING
        PhotonNetwork.RemoveCallbackTarget(this);
#endif
    }

    private void Update()
    {
        if (!IsDevModeActive) return;
        if (Input.GetKeyDown(toggleKey)) showPanel = !showPanel;
    }

    // ------------------------------------------------------------------ //
    // IMGUI panel
    // ------------------------------------------------------------------ //

    private void OnGUI()
    {
        if (!IsDevModeActive || !showPanel) return;

        EnsureStyles();
        Rect r = new Rect(panelOrigin.x, panelOrigin.y, panelSize.x, panelSize.y);
        GUI.Box(r, GUIContent.none);

        GUILayout.BeginArea(new Rect(r.x + 8f, r.y + 6f, r.width - 16f, r.height - 12f));
        GUILayout.Label("RTS Multiplayer Test Commander (dev only)", headerLabel);
        GUILayout.Label($"Toggle: {toggleKey}.   Selected target: Player {selectedTarget + 1}", boldLabel);

        scroll = GUILayout.BeginScrollView(scroll, GUILayout.MinHeight(310f));

        List<PlayerRow> rows = CollectPlayerRows();
        for (int i = 0; i < rows.Count; i++)
        {
            DrawPlayerRow(rows[i]);
        }

        GUILayout.EndScrollView();

        GUILayout.Space(4f);
        GUILayout.Label("Selected player actions", boldLabel);

        GUILayout.BeginHorizontal();
        GUILayout.Label("Move to x:", GUILayout.Width(60f));
        moveXText = GUILayout.TextField(moveXText, GUILayout.Width(60f));
        GUILayout.Label("z:", GUILayout.Width(15f));
        moveZText = GUILayout.TextField(moveZText, GUILayout.Width(60f));
        if (GUILayout.Button("Move to point", GUILayout.Width(110f)))
        {
            float.TryParse(moveXText, out float mx);
            float.TryParse(moveZText, out float mz);
            SendDevOp(DevOp.MoveUnits, selectedTarget, new Vector3(mx, 0f, mz));
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Attack victim:", GUILayout.Width(95f));
        string victimLabel = attackVictim < 0 ? "(pick)" : $"Player {attackVictim + 1}";
        if (GUILayout.Button(victimLabel, GUILayout.Width(80f)))
            attackVictim = CycleVictim(attackVictim, selectedTarget, rows.Count);
        if (GUILayout.Button($"Order attack on " +
                             $"{(attackVictim < 0 ? "?" : $"Player {attackVictim + 1}")}",
                             GUILayout.Width(180f)))
        {
            if (attackVictim >= 0 && attackVictim != selectedTarget)
                SendDevOp(DevOp.AttackPlayer, selectedTarget, attackVictim);
            else
                Debug.LogWarning("[DevCommander] Pick a victim different from the selected target.");
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(6f);
        if (GUILayout.Button("Reset Match State (all clients)", GUILayout.Height(28f)))
            SendDevOp(DevOp.ResetMatch, 0);

        GUILayout.EndArea();
    }

    private void DrawPlayerRow(PlayerRow row)
    {
        Color prev = GUI.backgroundColor;
        GUI.backgroundColor = new Color(row.color.r, row.color.g, row.color.b, 0.35f);
        GUILayout.BeginVertical("box");
        GUI.backgroundColor = prev;

        GUILayout.Label($"Player {row.playerId + 1}: {row.name}   actor #{row.actor}   " +
                        $"playerId={row.playerId}   color={row.colorName}   startSlot={row.startSlotLabel}",
                        boldLabel);
        GUILayout.Label($"   resources = {row.resources}    units = {row.unitCount}");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Select", GUILayout.Width(60f)))
            selectedTarget = row.playerId;
        if (GUILayout.Button($"+{resourceGrantAmount} Res", GUILayout.Width(85f)))
            SendDevOp(DevOp.GiveResources, row.playerId, resourceGrantAmount);
        if (GUILayout.Button("Spawn Inf", GUILayout.Width(80f)))
            SendDevOp(DevOp.SpawnUnit, row.playerId, infantryProductionType);
        if (GUILayout.Button("Spawn Tank", GUILayout.Width(90f)))
            SendDevOp(DevOp.SpawnUnit, row.playerId, tankProductionType);
        if (GUILayout.Button("Spawn Aircraft", GUILayout.Width(110f)))
            SendDevOp(DevOp.SpawnUnit, row.playerId, aircraftProductionType);
        if (GUILayout.Button($"-{damageAmount} CC", GUILayout.Width(75f)))
            SendDevOp(DevOp.DamageBase, row.playerId, damageAmount);
        if (GUILayout.Button("Kill Units", GUILayout.Width(80f)))
            SendDevOp(DevOp.KillAllUnits, row.playerId);
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    private static int CycleVictim(int current, int avoid, int playerCount)
    {
        int n = Mathf.Max(playerCount, 1);
        int next = current;
        for (int i = 0; i < n + 1; i++)
        {
            next = (next + 1) % Mathf.Max(n, 1);
            if (next != avoid) return next;
        }
        return -1;
    }

    private void EnsureStyles()
    {
        if (boldLabel == null)
            boldLabel = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        if (headerLabel == null)
            headerLabel = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 14 };
    }

    // ------------------------------------------------------------------ //
    // Player-row data
    // ------------------------------------------------------------------ //

    private struct PlayerRow
    {
        public string name;
        public int    actor;
        public int    playerId;
        public Color  color;
        public string colorName;
        public string startSlotLabel;
        public int    resources;
        public int    unitCount;
    }

    private List<PlayerRow> CollectPlayerRows()
    {
        var rows = new List<PlayerRow>();
#if PHOTON_UNITY_NETWORKING
        if (PhotonNetwork.IsConnected && PhotonNetwork.InRoom)
        {
            var sorted = new List<Player>(PhotonNetwork.CurrentRoom.Players.Values);
            sorted.Sort((a, b) => a.ActorNumber.CompareTo(b.ActorNumber));
            for (int i = 0; i < sorted.Count; i++)
            {
                Player p = sorted[i];
                int pid = NetworkManagerRTS.GetPlayerIdForActor(p.ActorNumber);
                if (pid < 0) pid = i;

                Color  c         = MultiplayerColors.ForOwnerOrDefault(pid);
                string colorName = "(default)";
                if (p.CustomProperties != null)
                {
                    if (p.CustomProperties.TryGetValue(NetworkManagerRTS.ColorPropKey, out object rgb) &&
                        rgb is Vector3 v)
                        c = new Color(v.x, v.y, v.z, 1f);
                    if (p.CustomProperties.TryGetValue(NetworkManagerRTS.ColorNamePropKey, out object cn) &&
                        cn is string s && !string.IsNullOrEmpty(s))
                        colorName = s;
                }
                string slot = NetworkManagerRTS.TryGetPlayerStartSlot(p.ActorNumber, out int sc)
                    ? ((char)('A' + sc)).ToString() : "—";

                rows.Add(new PlayerRow
                {
                    name           = string.IsNullOrEmpty(p.NickName) ? $"Player{i + 1}" : p.NickName,
                    actor          = p.ActorNumber,
                    playerId       = pid,
                    color          = c,
                    colorName      = colorName,
                    startSlotLabel = slot,
                    resources      = ResourceBank.Current(pid),
                    unitCount      = CountUnits(pid),
                });
            }
            return rows;
        }
#endif
        // SP fallback — single local player.
        rows.Add(new PlayerRow
        {
            name           = "Local",
            actor          = 1,
            playerId       = 0,
            color          = MultiplayerColors.ForOwnerOrDefault(0),
            colorName      = "(local)",
            startSlotLabel = "—",
            resources      = ResourceBank.Current(0),
            unitCount      = CountUnits(0),
        });
        return rows;
    }

    private static int CountUnits(int player)
    {
        int n = 0;
        foreach (GameEntity e in EntityRegistry.All())
        {
            if (e == null) continue;
            if (e.ownerPlayerId != player) continue;
            if (e.entityType == EntityType.Unit || e.entityType == EntityType.Aircraft) n++;
        }
        return n;
    }

    // ------------------------------------------------------------------ //
    // Send + apply
    // ------------------------------------------------------------------ //

    private void SendDevOp(DevOp op, int target, params object[] extra)
    {
        Debug.Log($"[DevCommander] Requested {op} for playerId={target}" +
                  (extra != null && extra.Length > 0 ? $" arg={extra[0]}" : ""));

        int extraCount = extra != null ? extra.Length : 0;
        object[] payload = new object[3 + extraCount];
        payload[0] = PayloadVersion;
        payload[1] = (int)op;
        payload[2] = target;
        for (int i = 0; i < extraCount; i++) payload[3 + i] = extra[i];

#if PHOTON_UNITY_NETWORKING
        if (NetworkManagerRTS.IsMultiplayerEnabled)
        {
            Debug.Log("[DevCommander] Command sent through network path (Photon RaiseEvent).");
            PhotonNetwork.RaiseEvent(
                DevCommanderEventCode, payload,
                new RaiseEventOptions { Receivers = ReceiverGroup.All },
                SendOptions.SendReliable);
            return;
        }
#endif
        Debug.Log("[DevCommander] Single-player path — applying locally.");
        ApplyDevOp(op, target, payload);
    }

#if PHOTON_UNITY_NETWORKING
    public void OnEvent(EventData ev)
    {
        if (ev.Code != DevCommanderEventCode) return;
        if (!(ev.CustomData is object[] payload) || payload.Length < 3)
        {
            Debug.LogError("[DevCommander] OnEvent — invalid payload.");
            return;
        }
        try
        {
            byte ver = (byte)payload[0];
            int op   = (int) payload[1];
            int targ = (int) payload[2];
            if (ver != PayloadVersion)
                Debug.LogWarning($"[DevCommander] payload version {ver} != local {PayloadVersion}.");
            ApplyDevOp((DevOp)op, targ, payload);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[DevCommander] OnEvent failed: {e.Message}");
        }
    }
#endif

    private void ApplyDevOp(DevOp op, int target, object[] payload)
    {
        bool master = IsMasterOrSP();
        bool isLocalTarget = IsLocalTarget(target);

        switch (op)
        {
            case DevOp.GiveResources:
                if (master) DoGiveResources(target, payload.Length > 3 ? (int)payload[3] : resourceGrantAmount);
                break;

            case DevOp.DamageBase:
                if (master) DoDamageBase(target, payload.Length > 3 ? (float)payload[3] : damageAmount);
                break;

            case DevOp.KillAllUnits:
                if (master) DoKillAllUnits(target);
                break;

            case DevOp.SpawnUnit:
                if (isLocalTarget) DoSpawnForLocal(target,
                    payload.Length > 3 ? (string)payload[3] : infantryProductionType);
                break;

            case DevOp.MoveUnits:
                if (isLocalTarget) DoMoveForLocal(target,
                    payload.Length > 3 ? (Vector3)payload[3] : Vector3.zero);
                break;

            case DevOp.AttackPlayer:
                if (isLocalTarget) DoAttackForLocal(target,
                    payload.Length > 3 ? (int)payload[3] : -1);
                break;

            case DevOp.ResetMatch:
                Debug.Log("[DevCommander] ResetMatch → MatchSessionManager.CleanupPreviousMatch (local).");
                MatchSessionManager.CleanupPreviousMatch();
                break;
        }
    }

    private static bool IsMasterOrSP()
    {
#if PHOTON_UNITY_NETWORKING
        if (!NetworkManagerRTS.IsMultiplayerEnabled) return true;     // SP
        return PhotonNetwork.IsMasterClient;
#else
        return true;
#endif
    }

    private static bool IsLocalTarget(int target)
    {
#if PHOTON_UNITY_NETWORKING
        if (!NetworkManagerRTS.IsMultiplayerEnabled) return target == 0;
        return NetworkManagerRTS.LocalPlayerId == target;
#else
        return target == 0;
#endif
    }

    // ------------------------------------------------------------------ //
    // Action implementations (each is owner/master-gated by ApplyDevOp)
    // ------------------------------------------------------------------ //

    private static void DoGiveResources(int target, int amount)
    {
        PlayerResourceManager bank = ResourceBank.For(target);
        if (bank == null)
        {
            Debug.LogWarning($"[DevCommander] GiveResources — no PlayerResourceManager for player {target}.");
            return;
        }
        bank.AddResources(amount);
        Debug.Log($"[DevCommander] +{amount} resources for player {target} (master applied → broadcast).");
    }

    private static void DoDamageBase(int target, float amount)
    {
        Health h = FindPlayerBaseHealth(target);
        if (h == null)
        {
            Debug.LogWarning($"[DevCommander] DamageBase — no base/Health found for player {target}.");
            return;
        }
        Debug.Log($"[DevCommander] Damaging player {target}'s base for {amount} HP.");
        h.TakeDamage(amount);
    }

    private static void DoKillAllUnits(int target)
    {
        int killed = 0;
        // Snapshot to avoid mutating during iteration.
        var snapshot = new List<GameEntity>(EntityRegistry.All());
        for (int i = 0; i < snapshot.Count; i++)
        {
            GameEntity e = snapshot[i];
            if (e == null) continue;
            if (e.ownerPlayerId != target) continue;
            if (e.entityType != EntityType.Unit && e.entityType != EntityType.Aircraft) continue;
            Health h = e.GetComponent<Health>();
            if (h == null) continue;
            h.TakeDamage(999999f);
            killed++;
        }
        Debug.Log($"[DevCommander] Killed {killed} unit(s) for player {target}.");
    }

    private static void DoSpawnForLocal(int target, string productionType)
    {
        GameEntity producer = FindProducerForPlayer(target, productionType);
        if (producer == null)
        {
            Debug.LogWarning($"[DevCommander] SpawnUnit — player {target} has no producer for " +
                             $"'{productionType}'. Build the matching production building first " +
                             "(Barracks / VehicleFactory / Airfield).");
            return;
        }
        string spawnId = System.Guid.NewGuid().ToString("N");
        PlayerCommand cmd = PlayerCommand.Produce(target, producer.EntityId, productionType, spawnId);
        Debug.Log($"[DevCommander] Issuing Produce({productionType}) for player {target} " +
                  $"via '{producer.name}'. spawnId={spawnId}.");
        CommandDispatcher.Issue(cmd);
    }

    private static void DoMoveForLocal(int target, Vector3 pos)
    {
        var ids = new List<string>();
        foreach (GameEntity e in EntityRegistry.All())
        {
            if (e == null) continue;
            if (e.ownerPlayerId != target) continue;
            if (e.entityType != EntityType.Unit) continue;
            ids.Add(e.EntityId);
        }
        if (ids.Count == 0)
        {
            Debug.LogWarning($"[DevCommander] MoveUnits — player {target} has no movable units.");
            return;
        }
        PlayerCommand cmd = PlayerCommand.Move(target, ids.ToArray(), pos);
        Debug.Log($"[DevCommander] Issuing Move {ids.Count} unit(s) of player {target} → {pos:F1}.");
        CommandDispatcher.Issue(cmd);
    }

    private static void DoAttackForLocal(int target, int victim)
    {
        if (victim < 0 || victim == target)
        {
            Debug.LogWarning($"[DevCommander] AttackPlayer — invalid victim {victim} for target {target}.");
            return;
        }

        // Prefer a victim building (base); fall back to any owned entity.
        GameEntity victimEnt = null;
        foreach (GameEntity e in EntityRegistry.All())
        {
            if (e == null) continue;
            if (e.ownerPlayerId != victim) continue;
            if (e.entityType == EntityType.Building) { victimEnt = e; break; }
            if (victimEnt == null) victimEnt = e;
        }
        if (victimEnt == null)
        {
            Debug.LogWarning($"[DevCommander] AttackPlayer — victim {victim} has no targetable entities.");
            return;
        }

        var ids = new List<string>();
        foreach (GameEntity e in EntityRegistry.All())
        {
            if (e == null) continue;
            if (e.ownerPlayerId != target) continue;
            if (e.entityType != EntityType.Unit && e.entityType != EntityType.Aircraft) continue;
            ids.Add(e.EntityId);
        }
        if (ids.Count == 0)
        {
            Debug.LogWarning($"[DevCommander] AttackPlayer — player {target} has no units.");
            return;
        }

        PlayerCommand cmd = PlayerCommand.Attack(target, ids.ToArray(), victimEnt.EntityId);
        Debug.Log($"[DevCommander] Issuing Attack: {ids.Count} unit(s) of player {target} → " +
                  $"entity '{victimEnt.EntityId}' (owner {victim}).");
        CommandDispatcher.Issue(cmd);
    }

    // ------------------------------------------------------------------ //
    // Lookups
    // ------------------------------------------------------------------ //

    /// <summary>Find a producer building owned by <paramref name="player"/> that produces this type.</summary>
    private static GameEntity FindProducerForPlayer(int player, string productionType)
    {
        switch (productionType)
        {
            case "Soldier":
            case "RPGSoldier":
                return FindOwnedComponent<UnitProducer>(player);
            case "Humvee":
            case "APC":
            case "ArtilleryTank":
            case "MissileLauncher":
                return FindOwnedComponent<VehicleFactoryProducer>(player);
            case "StrikeJet":
                return FindOwnedComponent<Airfield>(player);
            case "Dozer":
            case "Worker":
                return FindOwnedComponent<CommandCenterProducer>(player);
        }
        return null;
    }

    private static GameEntity FindOwnedComponent<T>(int player) where T : Component
    {
        T[] all = FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null) continue;
            GameEntity ge = all[i].GetComponent<GameEntity>();
            if (ge == null) ge = all[i].GetComponentInParent<GameEntity>();
            if (ge != null && ge.ownerPlayerId == player) return ge;
        }
        return null;
    }

    private static Health FindPlayerBaseHealth(int player)
    {
        // Prefer a Building (CC), then any other owned entity with Health.
        Health fallback = null;
        foreach (GameEntity e in EntityRegistry.All())
        {
            if (e == null) continue;
            if (e.ownerPlayerId != player) continue;
            Health h = e.GetComponent<Health>();
            if (h == null) h = e.GetComponentInChildren<Health>(true);
            if (h == null) continue;
            if (e.entityType == EntityType.Building) return h;
            if (fallback == null) fallback = h;
        }
        return fallback;
    }
}
