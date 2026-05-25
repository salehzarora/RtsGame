using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

/// <summary>
/// Builds APCPrefab.prefab from primitives — an 8-wheeled armored personnel
/// carrier with a top-mounted heavy machine gun + short-range AA mount.
/// Mirrors the structure of Humvee / ArtilleryTank / MissileLauncher builders.
///
/// Menu: Tools → RTS → Vehicles → Create APC Prefab
///
/// Resulting hierarchy:
///   APCPrefab/                     (BoxCollider, NavMeshAgent, UnitMovement,
///     │                             SelectableUnit, Health (Player, 520 HP),
///     │                             UnitCategory.Vehicle, UnitCombat (primary MG),
///     │                             APCAntiAirAuto (secondary AA),
///     │                             GroundAutoAttackController, TeamColorMarker)
///     ├── Hull                     (long armored body — team color)
///     ├── Nose                     (sloped armored front — team color)
///     ├── SideSkirtL / R           (dark side armor over the wheels)
///     ├── Wheel FL / FR / MFL / MFR / MRL / MRR / RL / RR  (8 dark cylinders)
///     ├── Turret                   (small box on the roof — team color)
///     ├── MG_Mount                 (dark cylinder — main MG)
///     ├── AA_Mount                 (smaller dark cylinder — secondary AA pintle)
///     ├── FirePoint_MG             (Transform at MG muzzle — UnitCombat origin)
///     ├── FirePoint_AA             (Transform at AA muzzle — APCAntiAirAuto origin)
///     ├── SelectionCircle          (cyan flat cylinder under the hull)
///     └── HealthBar                (HealthBar component — runtime visuals)
///
/// Future-ready transport fields:
///   The APCCombat doesn't exist — transport boarding is intentionally out of
///   scope for this milestone. The prefab carries two unused public ints on
///   the root via inline notes; future EnemyTransportAI / PlayerTransportAI
///   can read transformPosition + a future ITransport component.
///
/// Safe to re-run — overwrites the prefab in place, preserving any references
/// VehicleFactoryProducer holds.
/// </summary>
public static class CreateAPCPrefab
{
    // ------------------------------------------------------------------ //
    // Constants
    // ------------------------------------------------------------------ //

    public  const string PrefabPath        = "Assets/_Game/Prefabs/APCPrefab.prefab";
    private const string MatFolder         = "Assets/_Game/Materials/APC";
    private const string UnitLayer         = "Unit";
    private const string SoldierPrefabPath = "Assets/_Game/Prefabs/SoldierPrefab.prefab";

    // Stats — spec values
    private const float MaxHealth      = 520f;
    private const float MoveSpeed      = 4.5f;     // between Humvee (6) and Tank (3.5)
    private const float StopDistance   = 1.2f;
    private const float AgentRadius    = 1.1f;
    private const float AgentHeight    = 1.8f;

    // Primary MG (anti-infantry)
    private const float PrimaryRange    = 12f;
    private const float PrimaryDamage   = 12f;
    private const float PrimaryCooldown = 0.25f;

    // Turret slewing (independent of body rotation — chassis keeps driving)
    private const float TurretTurnSpeed     = 220f;
    private const float TurretAimTolerance  = 18f;

    // Fire-while-moving tuning (UnitCombat applies these when the agent has
    // velocity above its movingSpeedThreshold). The APC stays effective on the
    // move but pays a small accuracy + cooldown penalty.
    private const float MovingCooldownMultiplier = 1.10f;
    private const float MovingAccuracy           = 0.85f;
    private const float StationaryAccuracy       = 1.00f;

    // Secondary AA (short-range anti-air)
    private const float AARange    = 10f;
    private const float AADamage   = 40f;
    private const float AACooldown = 1.3f;

    private const float BarHeight  = 2.2f;

    // Palette
    private static readonly Color HullOlive   = new Color(0.30f, 0.36f, 0.20f);   // body — TeamColorMarker repaints
    private static readonly Color NoseOlive   = new Color(0.34f, 0.40f, 0.22f);   // sloped nose — TeamColorMarker repaints
    private static readonly Color TurretOlive = new Color(0.28f, 0.34f, 0.18f);   // turret cube — TeamColorMarker repaints
    private static readonly Color WheelBlack  = new Color(0.08f, 0.08f, 0.08f);
    private static readonly Color SkirtDark   = new Color(0.16f, 0.18f, 0.14f);
    private static readonly Color GunDark     = new Color(0.18f, 0.18f, 0.18f);
    private static readonly Color RingCyan    = new Color(0.20f, 0.85f, 1.00f);

    // ------------------------------------------------------------------ //
    // Entry point
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Vehicles/Create APC Prefab")]
    public static void Create()
    {
        int unitLayer = LayerMask.NameToLayer(UnitLayer);
        if (unitLayer < 0)
        {
            Debug.LogError($"[CreateAPCPrefab] ✗ Layer '{UnitLayer}' does not exist.\n" +
                           "  Fix: Edit → Project Settings → Tags and Layers → add 'Unit' user layer.");
            return;
        }

        Debug.Log("[CreateAPCPrefab] ── Building APCPrefab ──");

        Material hullMat   = LoadOrCreateMat("APCHull",   HullOlive);
        Material noseMat   = LoadOrCreateMat("APCNose",   NoseOlive);
        Material turretMat = LoadOrCreateMat("APCTurret", TurretOlive);
        Material wheelMat  = LoadOrCreateMat("APCWheel",  WheelBlack);
        Material skirtMat  = LoadOrCreateMat("APCSkirt",  SkirtDark);
        Material gunMat    = LoadOrCreateMat("APCGun",    GunDark);
        Material ringMat   = LoadOrCreateMat("APCRing",   RingCyan);
        AssetDatabase.SaveAssets();

        GameObject root = new GameObject("APCPrefab");
        try
        {
            root.layer = unitLayer;
            BuildAPC(root, unitLayer,
                     hullMat, noseMat, turretMat, wheelMat, skirtMat, gunMat, ringMat);

            EnsureFolder("Assets/_Game/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[CreateAPCPrefab] ✓ Saved {PrefabPath}.\n" +
                      $"  Stats: HP={MaxHealth}, Speed={MoveSpeed}, " +
                      $"MG (range {PrimaryRange}, dmg {PrimaryDamage}/0.25s), " +
                      $"AA (range {AARange}, dmg {AADamage}/1.3s).\n" +
                      "  Drop into VehicleFactoryProducer → APC Prefab " +
                      "(or run Tools → RTS → Vehicles → Repair APC Production).");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // ------------------------------------------------------------------ //
    // Construction
    // ------------------------------------------------------------------ //

    private static void BuildAPC(
        GameObject root, int unitLayer,
        Material hull, Material nose, Material turret,
        Material wheel, Material skirt, Material gun, Material ring)
    {
        // ── Gameplay components on the root ──────────────────────────── //

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size   = new Vector3(2.0f, 1.4f, 4.4f);
        col.center = new Vector3(0f,  0.7f, 0f);

        NavMeshAgent agent     = root.AddComponent<NavMeshAgent>();
        agent.speed            = MoveSpeed;
        agent.angularSpeed     = 200f;
        agent.acceleration     = 14f;
        agent.stoppingDistance = StopDistance;
        agent.radius           = AgentRadius;
        agent.height           = AgentHeight;

        root.AddComponent<UnitMovement>();

        Health hp        = root.AddComponent<Health>();
        hp.team          = Health.Team.Player;
        hp.maxHealth     = MaxHealth;

        SelectableUnit sel = root.AddComponent<SelectableUnit>();

        UnitCategory cat = root.AddComponent<UnitCategory>();
        cat.category     = UnitCategory.Category.Vehicle;

        // Primary MG via UnitCombat (Bullet damage type — strong vs infantry,
        // weak vs vehicles/buildings, auto-skips Aircraft in
        // GroundAutoAttackController.CanEngageCategory).
        // Fire-while-moving fields are set so the APC keeps shooting while
        // driving — the chassis stays on its move order, the turret aims.
        UnitCombat combat       = root.AddComponent<UnitCombat>();
        combat.attackRange      = PrimaryRange;
        combat.attackDamage     = PrimaryDamage;
        combat.attackCooldown   = PrimaryCooldown;
        combat.isRanged         = true;
        combat.rotationSpeed    = 240f;
        combat.damageType       = DamageType.Bullet;
        combat.tracerColor      = new Color(1f, 0.85f, 0.3f);
        combat.tracerDuration   = 0.06f;
        combat.tracerWidth      = 0.05f;
        combat.canFireWhileMoving      = true;
        combat.movingCooldownMultiplier = MovingCooldownMultiplier;
        combat.movingAccuracy          = MovingAccuracy;
        combat.stationaryAccuracy      = StationaryAccuracy;

        // Independent turret rotation. UnitCombat reads VehicleTurretController
        // (auto-found via GetComponent in Awake) and gates firing on IsAimed
        // — the chassis keeps moving while the turret slews to the target.
        VehicleTurretController turretCtl = root.AddComponent<VehicleTurretController>();
        turretCtl.turretTurnSpeed         = TurretTurnSpeed;
        turretCtl.aimToleranceDegrees     = TurretAimTolerance;
        turretCtl.returnToForwardWhenIdle = true;
        turretCtl.idleReturnSpeed         = 80f;

        // Secondary AA via APCAntiAirAuto (independent scanner — fires at
        // hostile Aircraft within aaRange while UnitCombat handles ground).
        APCAntiAirAuto aa     = root.AddComponent<APCAntiAirAuto>();
        aa.aaRange            = AARange;
        aa.aaDamage           = AADamage;
        aa.aaCooldown         = AACooldown;
        aa.aaDamageType       = DamageType.MachineGun;     // 0.55× vs Aircraft
        aa.tracerColor        = new Color(0.55f, 0.90f, 1.0f);
        aa.tracerDuration     = 0.06f;
        aa.tracerWidth        = 0.06f;

        // Guard radius for ground auto-acquire. autoFireWhileMoving lets the
        // APC scan + engage from its CURRENT position while a long move order
        // is in flight — body keeps going, turret picks up opportunistic
        // targets along the way. Target is dropped automatically when it
        // wanders past attackRange × transitTargetLoseMultiplier.
        GroundAutoAttackController guard = root.AddComponent<GroundAutoAttackController>();
        guard.detectionRadius     = 14f;
        guard.leashRadius         = PrimaryRange + 8f;
        guard.scanInterval        = 0.35f;
        guard.autoFireWhileMoving = true;

        // Infantry transport — loads up to 6 infantry, heals them slowly while
        // inside, emergency-unloads survivors with 50% damage on APC death.
        // Default-passenger spawn is on so newly produced APCs arrive carrying
        // a full squad; the SoldierPrefab reference is resolved via AssetDatabase
        // so the assignment survives in the prefab.
        APCTransport transport = root.AddComponent<APCTransport>();
        transport.capacity                  = 6;
        transport.enterRange                = 2.5f;
        transport.unloadSpacing             = 1.5f;
        transport.healPassengers            = true;
        transport.passengerHealRate         = 5f;
        transport.allowPassengersToFire     = false;
        transport.spawnWithDefaultPassengers = true;
        transport.defaultPassengerCount     = 6;
        transport.fillToCapacityOnSpawn     = true;
        transport.defaultPassengerPrefab    = AssetDatabase.LoadAssetAtPath<GameObject>(SoldierPrefabPath);
        if (transport.defaultPassengerPrefab == null)
            Debug.LogWarning($"[CreateAPCPrefab] ⚠ SoldierPrefab not found at " +
                             $"{SoldierPrefabPath} — APCs will spawn empty until " +
                             "Tools → RTS → Vehicles → Repair APC Transport is run.");

        // Team color — repaint hull / nose / turret with the player's selected
        // color. Wheels / skirts / guns stay dark.
        TeamColorMarker tcm = root.AddComponent<TeamColorMarker>();
        tcm.team = TeamColorMarker.Team.Player;
        tcm.bodyColorRenderers = new List<Renderer>();
        tcm.detailRenderers    = new List<Renderer>();
        tcm.ignoreRenderers    = new List<Renderer>();

        // ── Visual children ──────────────────────────────────────────── //

        // Main hull — wide, low, long armored box.
        GameObject hullGO = Spawn(root, "Hull",
              PrimitiveType.Cube, hull,
              new Vector3(0f, 0.80f, 0f),
              new Vector3(1.8f, 0.80f, 3.6f));
        tcm.bodyColorRenderers.Add(hullGO.GetComponent<Renderer>());

        // Sloped nose — smaller wedge in front, tilted forward via Y rotation
        // is harder than just an angled cube. Use a smaller cube placed +Z
        // with a slight pitch for the slope read.
        GameObject noseGO = Spawn(root, "Nose",
              PrimitiveType.Cube, nose,
              new Vector3(0f, 0.65f, 2.00f),
              new Vector3(1.7f, 0.65f, 0.90f));
        noseGO.transform.localRotation = Quaternion.Euler(-12f, 0f, 0f);
        tcm.bodyColorRenderers.Add(noseGO.GetComponent<Renderer>());

        // Side skirts — dark armor strips along the wheel line.
        GameObject skirtL = Spawn(root, "SideSkirtL",
              PrimitiveType.Cube, skirt,
              new Vector3(-1.00f, 0.45f, 0f),
              new Vector3(0.10f, 0.55f, 3.8f));
        GameObject skirtR = Spawn(root, "SideSkirtR",
              PrimitiveType.Cube, skirt,
              new Vector3( 1.00f, 0.45f, 0f),
              new Vector3(0.10f, 0.55f, 3.8f));
        tcm.detailRenderers.Add(skirtL.GetComponent<Renderer>());
        tcm.detailRenderers.Add(skirtR.GetComponent<Renderer>());

        // 8 wheels — 4 per side, evenly spaced along the chassis.
        SpawnWheel(root, "WheelFL",  wheel, new Vector3(-1.05f, 0.40f,  1.50f));
        SpawnWheel(root, "WheelFR",  wheel, new Vector3( 1.05f, 0.40f,  1.50f));
        SpawnWheel(root, "WheelMFL", wheel, new Vector3(-1.05f, 0.40f,  0.50f));
        SpawnWheel(root, "WheelMFR", wheel, new Vector3( 1.05f, 0.40f,  0.50f));
        SpawnWheel(root, "WheelMRL", wheel, new Vector3(-1.05f, 0.40f, -0.50f));
        SpawnWheel(root, "WheelMRR", wheel, new Vector3( 1.05f, 0.40f, -0.50f));
        SpawnWheel(root, "WheelRL",  wheel, new Vector3(-1.05f, 0.40f, -1.50f));
        SpawnWheel(root, "WheelRR",  wheel, new Vector3( 1.05f, 0.40f, -1.50f));

        // Turret pivot — empty Transform that VehicleTurretController rotates.
        // All weapons + fire points live under it so they rotate together,
        // while the rest of the chassis keeps driving. Pivot at the roof,
        // slightly behind the cab — same anchor point the old single-cube
        // turret used.
        GameObject turretPivot = new GameObject("Turret");
        turretPivot.transform.SetParent(root.transform, false);
        turretPivot.transform.localPosition = new Vector3(0f, 1.45f, -0.20f);
        turretPivot.transform.localRotation = Quaternion.identity;
        turretCtl.turret = turretPivot.transform;

        // Turret visual — the team-coloured box on top, now a child of the
        // pivot so it rotates with the gun.
        GameObject turretBox = Spawn(turretPivot, "TurretBox",
              PrimitiveType.Cube, turret,
              Vector3.zero,
              new Vector3(1.10f, 0.45f, 1.10f));
        tcm.bodyColorRenderers.Add(turretBox.GetComponent<Renderer>());

        // Primary MG — long dark cylinder pointing forward (+Z in turret-local
        // space). All positions below are RELATIVE to the turret pivot.
        GameObject mg = Spawn(turretPivot, "MG_Mount",
              PrimitiveType.Cylinder, gun,
              new Vector3(0f, 0.10f, 0.75f),
              new Vector3(0.10f, 0.65f, 0.10f));
        mg.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        tcm.detailRenderers.Add(mg.GetComponent<Renderer>());

        // Secondary AA pintle — smaller, side-mounted on the turret, tilted up.
        GameObject aaPintle = Spawn(turretPivot, "AA_Mount",
              PrimitiveType.Cylinder, gun,
              new Vector3(0.45f, 0.40f, 0.10f),
              new Vector3(0.07f, 0.45f, 0.07f));
        aaPintle.transform.localRotation = Quaternion.Euler(60f, 0f, 0f);
        tcm.detailRenderers.Add(aaPintle.GetComponent<Renderer>());

        // FirePoint for the primary MG — at the muzzle of the MG cylinder.
        // VehicleTurretController.firePoint takes precedence over UnitCombat's
        // own firePoint when tracers are drawn, so we wire BOTH for safety.
        GameObject firePointMG = new GameObject("FirePoint_MG");
        firePointMG.transform.SetParent(turretPivot.transform, false);
        firePointMG.transform.localPosition = new Vector3(0f, 0.10f, 1.40f);
        combat.firePoint     = firePointMG.transform;
        turretCtl.firePoint  = firePointMG.transform;

        // FirePoint for the AA mount — also under the turret pivot so the
        // cyan tracer originates from the AA muzzle as the turret slews.
        GameObject firePointAA = new GameObject("FirePoint_AA");
        firePointAA.transform.SetParent(turretPivot.transform, false);
        firePointAA.transform.localPosition = new Vector3(0.45f, 0.75f, 0.30f);
        aa.firePoint = firePointAA.transform;

        // Selection circle just above the ground plane.
        GameObject circle = Spawn(root, "SelectionCircle",
              PrimitiveType.Cylinder, ring,
              new Vector3(0f, 0.02f, 0f),
              new Vector3(2.8f, 0.02f, 2.8f));
        circle.SetActive(false);
        sel.selectionCircle = circle;
        tcm.ignoreRenderers.Add(circle.GetComponent<Renderer>());

        // Health bar — clears the turret silhouette.
        GameObject bar = new GameObject("HealthBar");
        bar.transform.SetParent(root.transform, false);
        HealthBar hb     = bar.AddComponent<HealthBar>();
        hb.heightOffset  = BarHeight;

        // Overhead 6-slot passenger indicator — sits just below the HealthBar
        // and is shown only when the APC is selected and has at least one
        // passenger. Builds its own slot quads at runtime via Awake.
        GameObject indicator = new GameObject("PassengerIndicator");
        indicator.transform.SetParent(root.transform, false);
        APCPassengerIndicator pax = indicator.AddComponent<APCPassengerIndicator>();
        pax.transport            = transport;
        pax.heightOffset         = BarHeight - 0.30f;     // slightly under the HealthBar
        pax.slotSpacing          = 0.18f;
        pax.slotSize             = new Vector2(0.14f, 0.10f);
        pax.depth                = 0.04f;
        pax.occupiedColor        = new Color(0.22f, 0.82f, 0.22f);
        pax.emptyColor           = new Color(0.50f, 0.50f, 0.50f);
        pax.onlyShowWhenSelected = true;
        pax.hideWhenEmpty        = true;

        // Final pass — everything on the Unit layer.
        SetLayerRecursive(root.transform, unitLayer);
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static void SpawnWheel(GameObject parent, string name, Material mat, Vector3 localPos)
    {
        GameObject w = Spawn(parent, name,
                             PrimitiveType.Cylinder, mat,
                             localPos,
                             new Vector3(0.50f, 0.22f, 0.50f));
        // Default cylinder axis = +Y. Rotate 90° on Z so the disc faces left/right.
        w.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
    }

    private static GameObject Spawn(GameObject parent, string name, PrimitiveType type,
                                    Material mat, Vector3 localPos, Vector3 localScale)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = localPos;
        go.transform.localScale    = localScale;

        Collider c = go.GetComponent<Collider>();
        if (c != null) Object.DestroyImmediate(c);

        Renderer r = go.GetComponent<Renderer>();
        if (r != null)
        {
            r.sharedMaterial    = mat;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows    = true;
        }
        return go;
    }

    private static void SetLayerRecursive(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++)
            SetLayerRecursive(t.GetChild(i), layer);
    }

    // ------------------------------------------------------------------ //
    // Material factory
    // ------------------------------------------------------------------ //

    private static Material LoadOrCreateMat(string name, Color color)
    {
        EnsureFolder(MatFolder);
        string path = $"{MatFolder}/{name}.mat";

        Material m  = AssetDatabase.LoadAssetAtPath<Material>(path);
        Shader target = ResolveShader();

        if (m == null)
        {
            m = new Material(target) { name = name };
            AssetDatabase.CreateAsset(m, path);
        }
        else if (m.shader == null || m.shader.name != target.name)
        {
            m.shader = target;
        }

        m.color = color;
        if (m.HasProperty("_BaseColor"))
            m.SetColor("_BaseColor", color);
        EditorUtility.SetDirty(m);
        return m;
    }

    private static Shader ResolveShader()
    {
        RenderPipelineAsset rp = GraphicsSettings.defaultRenderPipeline;
        bool isURP = rp != null && rp.GetType().Name.Contains("Universal");

        Shader urp = Shader.Find("Universal Render Pipeline/Lit");
        Shader std = Shader.Find("Standard");

        if (isURP && urp != null) return urp;
        if (!isURP && std != null) return std;
        return urp ?? std ?? Shader.Find("Hidden/InternalErrorShader");
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        string leaf   = Path.GetFileName(folder);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
