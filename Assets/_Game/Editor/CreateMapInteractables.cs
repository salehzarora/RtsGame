using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Editor helpers that drop placeholder Interactive Tactical Map objects into
/// the active scene, fully configured (mesh + Health + neutral GameEntity +
/// the right Map component + colliders). Placeholders use Unity primitives so
/// they work before any final art exists — swap the meshes later.
///
/// Menu: Tools → RTS → Map → …
///
/// Conventions matched from the existing setup tools:
///   • Objects are parented under a single "MapObjects" root (add it to the
///     scene's GameplayWorldRoot list for multiplayer so they hide until
///     MatchStart).
///   • Each object gets a deterministic, scene-serialized GameEntity id — the
///     SAME scene asset loads on both clients, so the baked id matches across
///     the network with no extra tooling. (The generic "Add GameEntity To
///     Scene Objects" tool does not stamp MapObjects, so we bake the id here.)
///   • Map objects live on the DEFAULT layer so friendly auto-attack (which
///     scans Unit/Building) never fires at them; explicit attacks still work
///     via MapInteractionRouter.
///
/// Also: Tools → RTS → Map → Setup Map Network Events adds the
/// MapInteractableNetworkEvents component to the NetworkManager so garrison /
/// tunnel state replicates in multiplayer.
/// </summary>
public static class CreateMapInteractables
{
    private static readonly Color TankColor    = new Color(0.80f, 0.55f, 0.15f);
    private static readonly Color ScorchColor  = new Color(0.12f, 0.10f, 0.08f);
    private static readonly Color BridgeColor  = new Color(0.55f, 0.45f, 0.35f);
    private static readonly Color RubbleColor  = new Color(0.25f, 0.22f, 0.20f);
    private static readonly Color BuildingColor= new Color(0.45f, 0.50f, 0.55f);
    private static readonly Color TowerColor   = new Color(0.50f, 0.48f, 0.42f);
    private static readonly Color TunnelColor  = new Color(0.18f, 0.18f, 0.22f);
    private static readonly Color FlagColor    = new Color(0.6f, 0.6f, 0.6f);

    // ================================================================== //
    // Fuel tank (explosive)
    // ================================================================== //

    [MenuItem("Tools/RTS/Map/Create Fuel Tank")]
    public static void CreateFuelTank()
    {
        GameObject root = NewMapObject("FuelTank");
        root.transform.position = SpawnPos();

        CapsuleCollider col = root.AddComponent<CapsuleCollider>();
        col.height = 3f; col.radius = 1.3f; col.center = new Vector3(0f, 1.5f, 0f);

        GameObject alive = AddVisual(root.transform, "AliveVisual", PrimitiveType.Cylinder,
            new Vector3(0f, 1.5f, 0f), new Vector3(2.4f, 1.5f, 2.4f), TankColor, true);
        GameObject dead  = AddVisual(root.transform, "DestroyedVisual", PrimitiveType.Cylinder,
            new Vector3(0f, 0.3f, 0f), new Vector3(2.6f, 0.3f, 2.6f), ScorchColor, false);

        Health h = AddHealth(root, 90f);
        ConfigureEntity(root, "FuelTank");

        ExplosiveMapObject ex = root.AddComponent<ExplosiveMapObject>();
        ex.maxHealth                 = 90f;
        ex.persistAfterDestroyed     = true;
        ex.isTargetable              = true;
        ex.aliveVisual               = alive;
        ex.destroyedVisual           = dead;
        ex.disableCollidersOnDestroy = new Collider[] { col };
        ex.explosionRadius           = 9f;
        ex.explosionDamage           = 140f;
        ex.affectsUnits              = true;
        ex.affectsBuildings          = true;
        ex.affectsMapObjects         = true;
        ex.chainReactionEnabled      = true;
        EditorUtility.SetDirty(ex);
        _ = h;

        Finish(root, "Fuel Tank",
            "Place near a chokepoint / cluster of units. Shoot it (right-click " +
            "with units) to detonate; nearby fuel tanks chain-react.");
    }

    // ================================================================== //
    // Destructible bridge
    // ================================================================== //

    [MenuItem("Tools/RTS/Map/Create Destructible Bridge")]
    public static void CreateBridge()
    {
        GameObject root = NewMapObject("DestructibleBridge");
        root.transform.position = SpawnPos();

        // Deck walk collider (disabled on destroy). Plain box; the NavMesh is
        // what actually carries units across — see note in the dialog.
        GameObject deck = AddVisual(root.transform, "AliveVisual", PrimitiveType.Cube,
            new Vector3(0f, 0.25f, 0f), new Vector3(6f, 0.5f, 16f), BridgeColor, true);
        BoxCollider deckCol = deck.AddComponent<BoxCollider>();   // child collider re-added intentionally

        GameObject collapsed = AddVisual(root.transform, "DestroyedVisual", PrimitiveType.Cube,
            new Vector3(0f, -0.6f, 0f), new Vector3(6f, 0.4f, 16f), RubbleColor, false);

        // Physical blocker (enabled on destroy) — for non-agent physics.
        GameObject blockerGO = new GameObject("Blocker");
        blockerGO.transform.SetParent(root.transform, false);
        BoxCollider blocker = blockerGO.AddComponent<BoxCollider>();
        blocker.size = new Vector3(6f, 3f, 16f);
        blocker.center = new Vector3(0f, 1.5f, 0f);
        blocker.enabled = false;

        // Carving NavMeshObstacle (the real agent blocker), disabled until collapse.
        GameObject obsGO = new GameObject("NavBlockObstacle");
        obsGO.transform.SetParent(root.transform, false);
        NavMeshObstacle obs = obsGO.AddComponent<NavMeshObstacle>();
        obs.shape   = NavMeshObstacleShape.Box;
        obs.size    = new Vector3(6f, 2f, 16f);
        obs.center  = new Vector3(0f, 1f, 0f);
        obs.carving = true;
        obs.enabled = false;

        AddHealth(root, 400f);
        ConfigureEntity(root, "DestructibleBridge");

        DestructibleBridge br = root.AddComponent<DestructibleBridge>();
        br.maxHealth                 = 400f;
        br.persistAfterDestroyed     = true;
        br.isTargetable              = true;
        br.aliveVisual               = deck;
        br.destroyedVisual           = collapsed;
        br.disableCollidersOnDestroy = new Collider[] { deckCol };
        br.enableCollidersOnDestroy  = new Collider[] { blocker };
        br.navMeshObstacle           = obs;
        br.blocksPathWhenDestroyed   = true;
        EditorUtility.SetDirty(br);

        Finish(root, "Destructible Bridge",
            "Position so the deck spans a gap on the BAKED NavMesh, then rebake " +
            "the NavMesh (Tools → RTS → Rebuild NavMesh). When destroyed the deck " +
            "carves out of the NavMesh and units can no longer cross.");
    }

    // ================================================================== //
    // Garrison building
    // ================================================================== //

    [MenuItem("Tools/RTS/Map/Create Garrison Building")]
    public static void CreateGarrison()
    {
        GameObject root = NewMapObject("GarrisonBuilding");
        root.transform.position = SpawnPos();

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size = new Vector3(5f, 5f, 5f); col.center = new Vector3(0f, 2.5f, 0f);

        GameObject alive = AddVisual(root.transform, "AliveVisual", PrimitiveType.Cube,
            new Vector3(0f, 2.5f, 0f), new Vector3(5f, 5f, 5f), BuildingColor, true);
        GameObject dead = AddVisual(root.transform, "DestroyedVisual", PrimitiveType.Cube,
            new Vector3(0f, 1f, 0f), new Vector3(5f, 2f, 5f), RubbleColor, false);

        Transform[] exits = MakeExitRing(root.transform, 4, 4f);

        AddHealth(root, 320f);
        ConfigureEntity(root, "GarrisonBuilding");

        DestructibleMapObject dmo = root.AddComponent<DestructibleMapObject>();
        dmo.maxHealth                 = 320f;
        dmo.persistAfterDestroyed     = true;
        dmo.isTargetable              = true;
        dmo.aliveVisual               = alive;
        dmo.destroyedVisual           = dead;
        dmo.disableCollidersOnDestroy = new Collider[] { col };
        EditorUtility.SetDirty(dmo);

        GarrisonBuilding g = root.AddComponent<GarrisonBuilding>();
        g.capacity            = 5;
        g.infantryOnly        = true;
        g.canFireFromBuilding = true;
        g.fireRange           = 20f;
        g.damagePerOccupant   = 5f;
        g.exitPoints          = exits;
        g.ejectOnDestroy      = true;
        EditorUtility.SetDirty(g);

        Finish(root, "Garrison Building",
            "Select infantry and right-click the building to garrison. Right-click " +
            "again (when you hold it) to eject. Occupied buildings fire at enemies.");
    }

    // ================================================================== //
    // Watch tower
    // ================================================================== //

    [MenuItem("Tools/RTS/Map/Create Watch Tower")]
    public static void CreateWatchTower()
    {
        GameObject root = NewMapObject("WatchTower");
        root.transform.position = SpawnPos();

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size = new Vector3(2.5f, 8f, 2.5f); col.center = new Vector3(0f, 4f, 0f);

        GameObject alive = AddVisual(root.transform, "AliveVisual", PrimitiveType.Cube,
            new Vector3(0f, 4f, 0f), new Vector3(2.5f, 8f, 2.5f), TowerColor, true);
        GameObject dead = AddVisual(root.transform, "DestroyedVisual", PrimitiveType.Cube,
            new Vector3(0f, 1f, 0f), new Vector3(2.7f, 2f, 2.7f), RubbleColor, false);

        // Capture flag/band near the top, tinted to the holder's color at runtime.
        GameObject flag = AddVisual(root.transform, "OwnerFlag", PrimitiveType.Cube,
            new Vector3(0f, 8.4f, 0f), new Vector3(2.8f, 0.8f, 2.8f), FlagColor, true);

        Transform[] exits = MakeExitRing(root.transform, 3, 3f);

        AddHealth(root, 220f);
        ConfigureEntity(root, "WatchTower");

        DestructibleMapObject dmo = root.AddComponent<DestructibleMapObject>();
        dmo.maxHealth                 = 220f;
        dmo.persistAfterDestroyed     = true;
        dmo.isTargetable              = true;
        dmo.aliveVisual               = alive;
        dmo.destroyedVisual           = dead;
        dmo.disableCollidersOnDestroy = new Collider[] { col };
        EditorUtility.SetDirty(dmo);

        WatchTower t = root.AddComponent<WatchTower>();
        t.capacity              = 2;
        t.infantryOnly          = true;
        t.canFireFromBuilding   = true;
        t.fireRange             = 26f;
        t.damagePerOccupant     = 6f;
        t.exitPoints            = exits;
        t.visionRadius          = 30f;
        t.ownerIndicatorRenderer = flag.GetComponent<Renderer>();
        EditorUtility.SetDirty(t);

        Finish(root, "Watch Tower",
            "Neutral until infantry garrison it; the flag tints to the holder's " +
            "color and occupants fire at long range. Vision radius is a placeholder " +
            "(no fog-of-war system yet).");
    }

    // ================================================================== //
    // Tunnel pair
    // ================================================================== //

    [MenuItem("Tools/RTS/Map/Create Tunnel Pair")]
    public static void CreateTunnelPair()
    {
        Vector3 basePos = SpawnPos();
        TunnelEntrance a = CreateTunnel("TunnelEntrance_A", basePos + new Vector3(-12f, 0f, 0f));
        TunnelEntrance b = CreateTunnel("TunnelEntrance_B", basePos + new Vector3( 12f, 0f, 0f));

        a.linkedTunnel = b;
        b.linkedTunnel = a;
        EditorUtility.SetDirty(a);
        EditorUtility.SetDirty(b);

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[Map] Created linked Tunnel Pair (A ↔ B). Select infantry and " +
                  "right-click a tunnel to travel to the other side.");
        Selection.activeGameObject = a.gameObject;
    }

    private static TunnelEntrance CreateTunnel(string name, Vector3 pos)
    {
        GameObject root = NewMapObject(name);
        root.transform.position = pos;

        BoxCollider col = root.AddComponent<BoxCollider>();
        col.size = new Vector3(3f, 3f, 3f); col.center = new Vector3(0f, 1.5f, 0f);

        AddVisual(root.transform, "Mouth", PrimitiveType.Cube,
            new Vector3(0f, 1.5f, 0f), new Vector3(3f, 3f, 3f), TunnelColor, true);

        // One exit point in front of the mouth.
        GameObject exit = new GameObject("ExitPoint");
        exit.transform.SetParent(root.transform, false);
        exit.transform.localPosition = new Vector3(0f, 0f, 2.5f);

        ConfigureEntity(root, "TunnelEntrance");

        TunnelEntrance t = root.AddComponent<TunnelEntrance>();
        t.infantryOnly = true;
        t.allowVehicles = false;
        t.travelDelay = 1.2f;
        t.cooldown    = 0.5f;
        t.exitPoints  = new[] { exit.transform };
        EditorUtility.SetDirty(t);
        return t;
    }

    // ================================================================== //
    // Network events setup
    // ================================================================== //

    [MenuItem("Tools/RTS/Map/Setup Map Network Events")]
    public static void SetupMapNetworkEvents()
    {
        GameObject nm = GameObject.Find("NetworkManager");
        if (nm == null)
        {
            EditorUtility.DisplayDialog("Setup Map Network Events",
                "No 'NetworkManager' GameObject found. Run Tools → RTS → Multiplayer → " +
                "Setup Network Manager first, then re-run this.", "OK");
            return;
        }

        if (nm.GetComponent<MapInteractableNetworkEvents>() == null)
        {
            nm.AddComponent<MapInteractableNetworkEvents>();
            EditorUtility.SetDirty(nm);
            Debug.Log("[Map] Added MapInteractableNetworkEvents to NetworkManager.");
        }
        else
        {
            Debug.Log("[Map] MapInteractableNetworkEvents already present on NetworkManager.");
        }
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
    }

    // ================================================================== //
    // Shared helpers
    // ================================================================== //

    private static GameObject NewMapObject(string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(EnsureMapRoot(), false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
        return go;
    }

    private static Transform EnsureMapRoot()
    {
        GameObject root = GameObject.Find("MapObjects");
        if (root == null)
        {
            root = new GameObject("MapObjects");
            Undo.RegisterCreatedObjectUndo(root, "Create MapObjects root");
        }
        return root.transform;
    }

    private static GameObject AddVisual(Transform parent, string name, PrimitiveType type,
                                        Vector3 localPos, Vector3 localScale, Color color, bool active)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale    = localScale;

        Collider c = go.GetComponent<Collider>();
        if (c != null) Object.DestroyImmediate(c);   // root holds the gameplay collider

        Renderer r = go.GetComponent<Renderer>();
        if (r != null) r.sharedMaterial = MakeMat(color);

        go.SetActive(active);
        return go;
    }

    private static Transform[] MakeExitRing(Transform parent, int count, float radius)
    {
        var exits = new Transform[count];
        for (int i = 0; i < count; i++)
        {
            float ang = i * Mathf.PI * 2f / count;
            GameObject e = new GameObject($"ExitPoint_{i}");
            e.transform.SetParent(parent, false);
            e.transform.localPosition = new Vector3(Mathf.Cos(ang) * radius, 0f, Mathf.Sin(ang) * radius);
            exits[i] = e.transform;
        }
        return exits;
    }

    private static Health AddHealth(GameObject go, float max)
    {
        Health h = go.GetComponent<Health>();
        if (h == null) h = go.AddComponent<Health>();
        h.maxHealth = max;
        EditorUtility.SetDirty(h);
        return h;
    }

    private static GameEntity ConfigureEntity(GameObject go, string typeId)
    {
        GameEntity ge = go.GetComponent<GameEntity>();
        if (ge == null) ge = go.AddComponent<GameEntity>();
        ge.ownerPlayerId          = GameEntity.NeutralOwnerId;
        ge.teamId                 = GameEntity.NeutralOwnerId;
        ge.entityType             = EntityType.MapObject;
        ge.prefabTypeId           = typeId;
        ge.overrideTeamFromHealth = false;
        // Bake a stable, scene-serialized id. Identical on both clients because
        // they load the same scene asset.
        ge.EditorSetEntityId("map-" + System.Guid.NewGuid().ToString("N"));
        EditorUtility.SetDirty(ge);
        return ge;
    }

    private static Material MakeMat(Color c)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        Material m = new Material(sh) { color = c };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        return m;
    }

    private static Vector3 SpawnPos()
    {
        SceneView sv = SceneView.lastActiveSceneView;
        if (sv != null) return sv.pivot;
        return Vector3.zero;
    }

    private static void Finish(GameObject root, string label, string howToUse)
    {
        EditorUtility.SetDirty(root);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Selection.activeGameObject = root;
        Debug.Log($"[Map] Created {label} '{root.name}'. {howToUse}");
    }
}
