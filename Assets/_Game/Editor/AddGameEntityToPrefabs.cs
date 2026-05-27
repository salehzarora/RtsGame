using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click editor tool that stamps a <see cref="GameEntity"/> component onto
/// every standard unit/building prefab with sensible defaults
/// (<see cref="GameEntity.prefabTypeId"/>, <see cref="GameEntity.entityType"/>,
/// <see cref="GameEntity.ownerPlayerId"/>, <see cref="GameEntity.teamId"/>).
///
/// Menu: Tools → RTS → Multiplayer Prep → Add GameEntity To Prefabs
///
/// Idempotent — re-running just refreshes the prefabTypeId / owner / team to
/// the canonical values without duplicating components. Existing entityId on
/// the prefab asset is explicitly cleared so spawned instances generate fresh
/// runtime ids at Awake (we never want every Soldier to share one baked id).
///
/// The list of prefabs is hand-curated below — only the prefabs that exist
/// today. Any missing prefab is skipped with a single info log; the tool does
/// not fail-fast so a partial repo doesn't break the rest of the wiring.
/// </summary>
public static class AddGameEntityToPrefabs
{
    // ------------------------------------------------------------------ //
    // The canonical prefab → metadata table.
    // ------------------------------------------------------------------ //

    private struct PrefabSpec
    {
        public string     assetPath;
        public string     prefabTypeId;
        public EntityType entityType;
        public int        ownerId;
        public int        teamId;
        /// <summary>
        /// If true, the GameEntity infers owner/team from <see cref="Health"/>
        /// on Awake. Most prefabs want this so single-player keeps working
        /// off Health.team alone. Buildings that are spawned multiplayer-aware
        /// (CommandCenter, anything constructed via ConstructionSite) need
        /// this set to <c>false</c> so the dispatcher's
        /// <c>ApplyOwnership(cmd.playerId)</c> is authoritative.
        /// </summary>
        public bool       overrideTeamFromHealth;
    }

    private const int PlayerId = GameEntity.PlayerOwnerId; // 0
    private const int EnemyId  = GameEntity.EnemyOwnerId;  // 1

    private static readonly PrefabSpec[] Specs =
    {
        // ------- Player units -------
        Spec("Assets/_Game/Prefabs/WorkerPrefab.prefab",         "Worker",          EntityType.Unit, PlayerId),
        Spec("Assets/_Game/Prefabs/Worker.prefab",               "Worker",          EntityType.Unit, PlayerId),
        Spec("Assets/_Game/Prefabs/DozerPrefab.prefab",          "Dozer",           EntityType.Unit, PlayerId),
        Spec("Assets/_Game/Prefabs/SoldierPrefab.prefab",        "Soldier",         EntityType.Unit, PlayerId),
        Spec("Assets/_Game/Prefabs/RPGSoldierPrefab.prefab",     "RPGSoldier",      EntityType.Unit, PlayerId),
        Spec("Assets/_Game/Prefabs/HumveePrefab.prefab",         "Humvee",          EntityType.Unit, PlayerId),
        Spec("Assets/_Game/Prefabs/ArtilleryTankPrefab.prefab",  "ArtilleryTank",   EntityType.Unit, PlayerId),
        Spec("Assets/_Game/Prefabs/MissileLauncherPrefab.prefab","MissileLauncher", EntityType.Unit, PlayerId),
        Spec("Assets/_Game/Prefabs/APCPrefab.prefab",            "APC",             EntityType.Unit, PlayerId),
        Spec("Assets/_Game/Prefabs/StrikeJetPrefab.prefab",      "StrikeJet",       EntityType.Aircraft, PlayerId),

        // ------- Player buildings -------
        // Phase 10: CommandCenterPrefab uses overrideTeamFromHealth=false so the
        // dispatcher's ApplyOwnership(cmd.playerId) stays authoritative when
        // a Dozer finishes construction — otherwise the freshly-spawned CC
        // would always read back as Player team / owner 0.
        Spec("Assets/_Game/Prefabs/CommandCenterPrefab.prefab",     "CommandCenter",    EntityType.Building, PlayerId, overrideTeamFromHealth: false),
        Spec("Assets/_Game/Prefabs/Barracks.prefab",                "Barracks",         EntityType.Building, PlayerId),
        Spec("Assets/_Game/Prefabs/PowerPlantPrefab.prefab",        "PowerPlant",       EntityType.Building, PlayerId),
        Spec("Assets/_Game/Prefabs/VehicleFactoryPrefab.prefab",    "VehicleFactory",   EntityType.Building, PlayerId),
        Spec("Assets/_Game/Prefabs/AirfieldPrefab.prefab",          "Airfield",         EntityType.Building, PlayerId),
        Spec("Assets/_Game/Prefabs/MachineGunDefensePrefab.prefab", "MachineGunDefense",EntityType.Building, PlayerId),

        // ------- Enemy units / buildings -------
        Spec("Assets/_Game/Prefabs/EnemyDozerPrefab.prefab",            "EnemyDozer",            EntityType.Unit,     EnemyId),
        Spec("Assets/_Game/Prefabs/EnemyRPGSoldierPrefab.prefab",       "EnemyRPGSoldier",       EntityType.Unit,     EnemyId),
        Spec("Assets/_Game/Prefabs/EnemyBarracksPrefab.prefab",         "EnemyBarracks",         EntityType.Building, EnemyId),
        Spec("Assets/_Game/Prefabs/EnemyPowerPlantPrefab.prefab",       "EnemyPowerPlant",       EntityType.Building, EnemyId),
        Spec("Assets/_Game/Prefabs/EnemyMachineGunDefensePrefab.prefab","EnemyMachineGunDefense",EntityType.Building, EnemyId),
    };

    private static PrefabSpec Spec(string path, string typeId, EntityType et, int ownerAndTeam,
                                   bool overrideTeamFromHealth = true)
    {
        return new PrefabSpec
        {
            assetPath              = path,
            prefabTypeId           = typeId,
            entityType             = et,
            ownerId                = ownerAndTeam,
            teamId                 = ownerAndTeam,
            overrideTeamFromHealth = overrideTeamFromHealth,
        };
    }

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Multiplayer Prep/Add GameEntity To Prefabs")]
    public static void Run()
    {
        Debug.Log("[AddGameEntityToPrefabs] ── Stamping GameEntity onto prefabs ──");

        int stamped = 0;
        int skipped = 0;
        int missing = 0;

        foreach (PrefabSpec spec in Specs)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(spec.assetPath);
            if (prefab == null)
            {
                Debug.Log($"[AddGameEntityToPrefabs]   skip — '{spec.assetPath}' not found in project.");
                missing++;
                continue;
            }

            // PrefabUtility.LoadPrefabContents gives us an editable copy we
            // can mutate then save back. This is the right API for editing
            // prefab assets at the root level.
            GameObject contents = PrefabUtility.LoadPrefabContents(spec.assetPath);
            try
            {
                GameEntity ge = contents.GetComponent<GameEntity>();
                bool added = false;
                if (ge == null)
                {
                    ge = contents.AddComponent<GameEntity>();
                    added = true;
                }

                ge.prefabTypeId           = spec.prefabTypeId;
                ge.entityType             = spec.entityType;
                ge.ownerPlayerId          = spec.ownerId;
                ge.teamId                 = spec.teamId;
                ge.overrideTeamFromHealth = spec.overrideTeamFromHealth;
                // Clear any baked entityId — we never want all spawns to share one.
                ge.EditorResetId();

                PrefabUtility.SaveAsPrefabAsset(contents, spec.assetPath);
                Debug.Log($"[AddGameEntityToPrefabs]   {(added ? "added" : "updated")} → {spec.assetPath} " +
                          $"({spec.prefabTypeId}, {spec.entityType}, owner {spec.ownerId})");
                if (added) stamped++; else skipped++;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[AddGameEntityToPrefabs] ── Done. stamped {stamped}, updated {skipped}, missing {missing} ──");
    }
}
