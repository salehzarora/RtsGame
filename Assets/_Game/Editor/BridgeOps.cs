using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Parameterless static entry points for the FileBridge <c>exec</c> op —
/// the automation counterpart to the interactive menus. Everything here is
/// dialog-free by design (modal dialogs would deadlock a headless bridge
/// call). Each method logs with a [BridgeOps] prefix so console readback
/// shows exactly what ran.
///
/// Invocation example (cmd.json):
///   { "id":"x", "op":"exec", "arg":"BridgeOps.OpenDevSandbox" }
/// </summary>
public static class BridgeOps
{
    // ---------------------------------------------------------------- //
    // Scene opening (silent — saves current scene first, no dialogs)
    // ---------------------------------------------------------------- //

    public static void OpenDevSandbox()  { OpenScene("Assets/Scenes/DevSandboxScene.unity"); }
    public static void OpenSampleScene() { OpenScene("Assets/Scenes/SampleScene.unity"); }
    public static void OpenMainMenu()    { OpenScene("Assets/Scenes/MainMenuScene.unity"); }

    private static void OpenScene(string path)
    {
        Scene cur = SceneManager.GetActiveScene();
        if (cur.isDirty) EditorSceneManager.SaveScene(cur);
        EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        Debug.Log("[BridgeOps] Opened scene: " + path);
    }

    // ---------------------------------------------------------------- //
    // Scene re-serialization (Force-Text) without the confirmation dialog
    // ---------------------------------------------------------------- //

    public static void ConvertScenesToTextSilent()
    {
        string restore = SceneManager.GetActiveScene().path;
        Scene cur = SceneManager.GetActiveScene();
        if (cur.isDirty) EditorSceneManager.SaveScene(cur);

        foreach (string p in Directory.GetFiles("Assets/Scenes", "*.unity")
                                      .Select(x => x.Replace('\\', '/')))
        {
            Scene s = EditorSceneManager.OpenScene(p, OpenSceneMode.Single);
            EditorSceneManager.MarkSceneDirty(s);
            bool ok = EditorSceneManager.SaveScene(s);
            Debug.Log("[BridgeOps] " + (ok ? "re-saved " : "SAVE FAILED ") + p);
        }
        if (!string.IsNullOrEmpty(restore) && File.Exists(restore))
            EditorSceneManager.OpenScene(restore, OpenSceneMode.Single);
        Debug.Log("[BridgeOps] ConvertScenesToTextSilent done.");
    }

    // ---------------------------------------------------------------- //
    // Building health bars — follow-up to the Health-component fix.
    // Adds a HealthBar child to the four production buildings so the new
    // Health is actually visible in-game. Idempotent.
    // ---------------------------------------------------------------- //

    public static void AddBuildingHealthBars()
    {
        // (path, heightOffset) — offset clears each building's roof.
        var jobs = new (string path, float h)[]
        {
            ("Assets/_Game/Prefabs/Barracks.prefab",            3.2f),
            ("Assets/_Game/Prefabs/PowerPlantPrefab.prefab",    3.6f),
            ("Assets/_Game/Prefabs/VehicleFactoryPrefab.prefab",3.0f),
            ("Assets/_Game/Prefabs/AirfieldPrefab.prefab",      4.0f),
        };
        foreach (var (path, h) in jobs)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (root.GetComponentInChildren<HealthBar>(true) != null)
                {
                    Debug.Log("[BridgeOps] " + Path.GetFileName(path) + ": HealthBar already present — skip.");
                    continue;
                }
                var barGO = new GameObject("HealthBar");
                barGO.transform.SetParent(root.transform, false);
                HealthBar hb = barGO.AddComponent<HealthBar>();
                hb.heightOffset = h;
                hb.size = new Vector2(2.4f, 0.22f);   // wider bar reads better on buildings
                hb.hideWhenFull = true;               // buildings: only show when damaged
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log("[BridgeOps] " + Path.GetFileName(path) + ": HealthBar added (h=" + h + ").");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[BridgeOps] AddBuildingHealthBars done.");
    }

    // ---------------------------------------------------------------- //
    // Play-mode smoke test — programmatic (no simulated mouse input).
    // Run SmokeSpawnCombat AFTER entering play mode in DevSandboxScene,
    // wait a few seconds, then SmokeReport.
    // ---------------------------------------------------------------- //

    private static GameObject _soldier, _enemy;

    public static void SmokeSpawnCombat()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] Not in play mode."); return; }

        var soldierPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/SoldierPrefab.prefab");
        var enemyPrefab   = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/EnemyRPGSoldierPrefab.prefab");
        if (soldierPrefab == null || enemyPrefab == null)
        { Debug.LogError("[BridgeOps] prefab load failed"); return; }

        _soldier = Object.Instantiate(soldierPrefab, new Vector3(6f, 0f, 6f),  Quaternion.identity);
        _enemy   = Object.Instantiate(enemyPrefab,   new Vector3(14f, 0f, 6f), Quaternion.identity);
        Debug.Log("[BridgeOps] Smoke: spawned Soldier at (6,0,6) and EnemyRPG at (14,0,6) — 8 m apart. " +
                  "Auto-attack should engage. Run BridgeOps.SmokeOrderMove next (optional), " +
                  "then SmokeReport after ~6 s.");
    }

    public static void SmokeOrderMove()
    {
        if (_soldier == null) { Debug.LogError("[BridgeOps] no smoke soldier"); return; }
        var mv = _soldier.GetComponent<UnitMovement>();
        if (mv != null) { mv.MoveTo(new Vector3(10f, 0f, 10f)); Debug.Log("[BridgeOps] Smoke: move order to (10,0,10) issued."); }
        else Debug.LogError("[BridgeOps] soldier has no UnitMovement");
    }

    public static void SmokeReport()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] Not in play mode."); return; }
        var sb = new System.Text.StringBuilder("[BridgeOps] ── Smoke report ──\n");
        foreach (Health h in Object.FindObjectsByType<Health>(FindObjectsSortMode.None))
        {
            sb.Append("  ").Append(h.gameObject.name)
              .Append("  team=").Append(h.team)
              .Append("  hp=").Append(h.CurrentHealth.ToString("F0")).Append('/').Append(h.maxHealth)
              .Append("  pos=").Append(h.transform.position.ToString("F1"))
              .AppendLine();
        }
        var agents = Object.FindObjectsByType<UnityEngine.AI.NavMeshAgent>(FindObjectsSortMode.None);
        sb.Append("  agents=").Append(agents.Length)
          .Append("  onNavMesh=").Append(agents.Count(a => a.isOnNavMesh)).AppendLine();
        Debug.Log(sb.ToString());
    }

    public static void SmokeCleanup()
    {
        if (_soldier != null) Object.Destroy(_soldier);
        if (_enemy   != null) Object.Destroy(_enemy);
        Debug.Log("[BridgeOps] Smoke objects destroyed.");
    }

    // ---------------------------------------------------------------- //
    // Repair stale soldier-visual overrides on SCENE INSTANCES.
    // Scene-placed soldiers saved before the orientation/ground fixes carry
    // instance overrides on the Soldier_Rigged child that shadow the
    // corrected prefab values → they lie flat while fresh spawns stand.
    // Re-asserts rot (-90,0,0), pos (0,-0.1,0), scale (1,1,1) on every
    // soldier instance in the OPEN scene, then saves the scene.
    // ---------------------------------------------------------------- //

    public static void FixSceneSoldierOrientations()
    {
        int fixedCount = 0, okCount = 0;
        foreach (GameEntity e in Object.FindObjectsByType<GameEntity>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (e.prefabTypeId != "Soldier") continue;
            Transform holder = e.transform.Find("SoldierVisualRoot/ImportedModelGoesHere");
            Transform model  = holder != null && holder.childCount > 0 ? holder.GetChild(0) : null;
            if (model == null) { Debug.LogWarning("[BridgeOps] '" + e.name + "': model child not found"); continue; }

            Quaternion wantRot = Quaternion.Euler(-90f, 0f, 0f);
            Vector3 wantPos = new Vector3(0f, -0.1f, 0f);
            bool wrong = Quaternion.Angle(model.localRotation, wantRot) > 0.5f ||
                         Vector3.Distance(model.localPosition, wantPos) > 0.01f ||
                         Vector3.Distance(model.localScale, Vector3.one) > 0.01f;
            if (!wrong) { okCount++; continue; }

            Undo.RecordObject(model, "Fix soldier visual");
            model.localRotation = wantRot;
            model.localPosition = wantPos;
            model.localScale    = Vector3.one;
            EditorUtility.SetDirty(model);
            fixedCount++;
            Debug.Log("[BridgeOps]   fixed '" + e.name + "' visual transform.");
        }
        if (fixedCount > 0)
        {
            Scene s = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(s);
            EditorSceneManager.SaveScene(s);
        }
        Debug.Log("[BridgeOps] FixSceneSoldierOrientations: " + fixedCount + " fixed, " + okCount + " already correct. Scene saved=" + (fixedCount > 0) + ".");
    }

    // ---------------------------------------------------------------- //
    // Externalize baked NavMeshData so scenes can serialize as TEXT.
    // NavMeshSurface.BuildNavMesh() leaves NavMeshData embedded in the
    // scene (binary-only object → whole scene falls back to binary).
    // Saving the data as a .asset removes the binary fallback.
    // ---------------------------------------------------------------- //

    public static void ExternalizeNavMeshData()
    {
        string restore = SceneManager.GetActiveScene().path;
        if (SceneManager.GetActiveScene().isDirty)
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());

        if (!AssetDatabase.IsValidFolder("Assets/Scenes/NavMeshData"))
            AssetDatabase.CreateFolder("Assets/Scenes", "NavMeshData");

        foreach (string p in Directory.GetFiles("Assets/Scenes", "*.unity")
                                      .Select(x => x.Replace('\\', '/')))
        {
            Scene s = EditorSceneManager.OpenScene(p, OpenSceneMode.Single);
            bool changed = false;
            foreach (var surf in Object.FindObjectsByType<Unity.AI.Navigation.NavMeshSurface>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var data = surf.navMeshData;
                if (data == null || AssetDatabase.Contains(data)) continue;
                string assetPath = "Assets/Scenes/NavMeshData/" + s.name + "_" + surf.gameObject.name + ".asset";
                AssetDatabase.CreateAsset(data, assetPath);
                EditorUtility.SetDirty(surf);
                changed = true;
                Debug.Log("[BridgeOps]   " + s.name + ": NavMeshData externalized → " + assetPath);
            }
            if (changed)
            {
                EditorSceneManager.MarkSceneDirty(s);
                EditorSceneManager.SaveScene(s);
            }
        }
        AssetDatabase.SaveAssets();
        if (!string.IsNullOrEmpty(restore) && File.Exists(restore))
            EditorSceneManager.OpenScene(restore, OpenSceneMode.Single);
        Debug.Log("[BridgeOps] ExternalizeNavMeshData done. Re-run ConvertScenesToTextSilent to re-serialize.");
    }

    // ---------------------------------------------------------------- //
    // Full game-view screenshot INCLUDING UI overlay canvases (play mode).
    // ScreenCapture grabs the rendered GameView; completes a frame later.
    // ---------------------------------------------------------------- //

    public static void ScreenshotFull()
    {
        ScreenCapture.CaptureScreenshot("Library/FileBridge/full.png");
        Debug.Log("[BridgeOps] Full screenshot queued → Library/FileBridge/full.png (ready next frame).");
    }

    // ---------------------------------------------------------------- //
    // Diagnose scene soldier instances — prints, per soldier, the visual
    // child states so "flat soldier" mysteries are answered with data:
    // which children are active, what the model's world rotation is, and
    // whether legacy visuals (PrimitivePlaceholder / character) are on.
    // ---------------------------------------------------------------- //

    /// <summary>Play-mode helper: park the camera rig close to the scene's
    /// soldier cluster so a follow-up screenshot shows them near-field.</summary>
    public static void FocusCameraOnSoldiers()
    {
        MoveRig(new Vector3(-12f, 8f, -13f), "soldier cluster");
    }

    /// <summary>Frame the smoke-test firefight area (spawns at x 6–14, z 6).</summary>
    public static void FocusCameraOnFight()
    {
        MoveRig(new Vector3(10f, 9f, -3f), "smoke-test firefight");
    }

    /// <summary>Frame the PowerPlant for building-damage VFX verification.</summary>
    public static void FocusCameraOnPowerPlant()
    {
        var b = FindBuilding("PowerPlant");
        if (b == null) { Debug.LogError("[BridgeOps] no PowerPlant instance"); return; }
        Vector3 p = b.transform.position;
        MoveRig(new Vector3(p.x, 8f, p.z - 9f), "PowerPlant");
    }

    /// <summary>Deterministic building-damage driver: drops the PowerPlant to
    /// ~35% so the damage smoke + health bar become visible. Repeat to push
    /// below 25% (heavier smoke).</summary>
    public static void SmokeDamagePowerPlant()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] Not in play mode."); return; }
        var b = FindBuilding("PowerPlant");
        var h = b != null ? b.GetComponent<Health>() : null;
        if (h == null) { Debug.LogError("[BridgeOps] PowerPlant Health not found"); return; }
        h.TakeDamage(h.CurrentHealth - h.maxHealth * 0.35f > 0 ? h.CurrentHealth - h.maxHealth * 0.35f : h.maxHealth * 0.12f);
        Debug.Log("[BridgeOps] PowerPlant damaged → " + h.CurrentHealth.ToString("F0") + "/" + h.maxHealth);
    }

    /// <summary>Kill the EnemyVehicleDummy → medium death explosion on camera.</summary>
    public static void SmokeKillDummyVehicle()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] Not in play mode."); return; }
        foreach (Health h in Object.FindObjectsByType<Health>(FindObjectsSortMode.None))
        {
            if (h.team == Health.Team.Enemy && h.gameObject.name.Contains("Vehicle"))
            {
                MoveRig(h.transform.position + new Vector3(0f, 7f, -8f), "dummy vehicle");
                h.TakeDamage(h.maxHealth + 10f);
                Debug.Log("[BridgeOps] Killed '" + h.gameObject.name + "' — medium explosion should play.");
                return;
            }
        }
        Debug.LogWarning("[BridgeOps] no enemy vehicle dummy found");
    }

    // ---------------------------------------------------------------- //
    // Visual polish ops (prefab edits — idempotent, edit mode)
    // ---------------------------------------------------------------- //

    /// <summary>Adds an engine exhaust TrailRenderer to the StrikeJet. Trails
    /// only emit while the jet moves, so parked aircraft stay clean.</summary>
    public static void AddJetEngineTrail()
    {
        const string prefabPath = "Assets/_Game/Prefabs/StrikeJetPrefab.prefab";
        const string matPath    = "Assets/_Game/Materials/Aircraft/Jet2/M_EngineTrail.mat";

        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Sprites/Default"))
            {
                mainTexture = Resources.GetBuiltinResource<Texture2D>("Default-Particle.psd"),
                color = new Color(0.95f, 0.97f, 1f, 0.65f)
            };
            AssetDatabase.CreateAsset(mat, matPath);
        }

        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            if (root.transform.Find("EngineTrail") != null)
            { Debug.Log("[BridgeOps] StrikeJet: EngineTrail already present."); return; }

            var go = new GameObject("EngineTrail");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(0f, 0.15f, -1.1f);  // tail, slightly raised

            var tr = go.AddComponent<TrailRenderer>();
            tr.time       = 0.55f;
            tr.startWidth = 0.22f;
            tr.endWidth   = 0f;
            tr.material   = mat;
            tr.startColor = new Color(0.95f, 0.97f, 1f, 0.65f);
            tr.endColor   = new Color(0.7f, 0.85f, 1f, 0f);
            tr.numCapVertices = 2;
            tr.minVertexDistance = 0.15f;
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Debug.Log("[BridgeOps] StrikeJet: EngineTrail added at tail (emits only while moving).");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        AssetDatabase.SaveAssets();
    }

    /// <summary>Replaces the flattened-cylinder SelectionCircle visuals on all
    /// player unit prefabs with a clean flat RING (annulus mesh) in a bright
    /// readable green — the most-seen UI element in an RTS. Footprint scale
    /// per prefab is preserved; any stray collider on the circle is removed.</summary>
    public static void ImproveSelectionCircles()
    {
        string[] prefabs =
        {
            "Assets/_Game/Prefabs/SoldierPrefab.prefab",
            "Assets/_Game/Prefabs/RPGSoldierPrefab.prefab",
            "Assets/_Game/Prefabs/WorkerPrefab.prefab",
            "Assets/_Game/Prefabs/Worker.prefab",
            "Assets/_Game/Prefabs/DozerPrefab.prefab",
            "Assets/_Game/Prefabs/HumveePrefab.prefab",
            "Assets/_Game/Prefabs/APCPrefab.prefab",
            "Assets/_Game/Prefabs/ArtilleryTankPrefab.prefab",
            "Assets/_Game/Prefabs/MissileLauncherPrefab.prefab",
        };

        Mesh ring = BuildOrLoadRingMesh("Assets/_Game/Art/VFX/SelectionRing.mesh");
        Material mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Game/Art/VFX/M_SelectionRing.mat");
        if (mat == null)
        {
            mat = new Material(Shader.Find("Sprites/Default"))
            { color = new Color(0.35f, 1f, 0.45f, 0.95f) };
            AssetDatabase.CreateAsset(mat, "Assets/_Game/Art/VFX/M_SelectionRing.mat");
        }

        foreach (string path in prefabs)
        {
            if (!File.Exists(path)) { Debug.LogWarning("[BridgeOps]   missing prefab " + path); continue; }
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform circle = FindDeep(root.transform, "SelectionCircle");
                if (circle == null)
                { Debug.LogWarning("[BridgeOps]   " + Path.GetFileName(path) + ": no SelectionCircle"); continue; }

                var mf = circle.GetComponent<MeshFilter>();
                if (mf == null) mf = circle.gameObject.AddComponent<MeshFilter>();
                mf.sharedMesh = ring;

                var mr = circle.GetComponent<MeshRenderer>();
                if (mr == null) mr = circle.gameObject.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;

                // Flatten Y so legacy cylinder squash values don't distort the ring,
                // and strip any leftover collider (the circle is pure UI).
                Vector3 s = circle.localScale;
                circle.localScale = new Vector3(s.x, 1f, s.z);
                var col = circle.GetComponent<Collider>();
                if (col != null) Object.DestroyImmediate(col, true);

                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log("[BridgeOps]   " + Path.GetFileName(path) + ": SelectionCircle → ring.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[BridgeOps] ImproveSelectionCircles done.");
    }

    private static Mesh BuildOrLoadRingMesh(string path)
    {
        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null) return existing;

        const int   seg = 48;
        const float rOut = 0.5f, rIn = 0.38f;
        var verts = new Vector3[seg * 2];
        var uvs   = new Vector2[seg * 2];
        var tris  = new int[seg * 6];
        for (int i = 0; i < seg; i++)
        {
            float a = i / (float)seg * Mathf.PI * 2f;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            verts[i * 2]     = new Vector3(c * rOut, 0f, s * rOut);
            verts[i * 2 + 1] = new Vector3(c * rIn,  0f, s * rIn);
            uvs[i * 2]     = new Vector2(1f, 0.5f);
            uvs[i * 2 + 1] = new Vector2(0f, 0.5f);
            int n = (i + 1) % seg;
            int t = i * 6;
            tris[t]     = i * 2;     tris[t + 1] = n * 2;     tris[t + 2] = i * 2 + 1;
            tris[t + 3] = i * 2 + 1; tris[t + 4] = n * 2;     tris[t + 5] = n * 2 + 1;
        }
        var m = new Mesh { name = "SelectionRing", vertices = verts, uv = uvs, triangles = tris };
        m.RecalculateNormals();
        m.RecalculateBounds();
        if (!AssetDatabase.IsValidFolder("Assets/_Game/Art/VFX"))
            AssetDatabase.CreateFolder("Assets/_Game/Art", "VFX");
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    /// <summary>Play-mode demo: select the first selectable unit so the new
    /// ring is visible, and park the camera on it.</summary>
    public static void ShowSelectionDemo()
    {
        var unit = Object.FindFirstObjectByType<SelectableUnit>();
        if (unit == null) { Debug.LogError("[BridgeOps] no SelectableUnit"); return; }
        unit.Select();
        MoveRig(unit.transform.position + new Vector3(0f, 7f, -7f), "selection demo");
        Debug.Log("[BridgeOps] Selected '" + unit.name + "' for ring demo.");
    }

    // ---------------------------------------------------------------- //
    // RPG soldier visual upgrade — proven Soldier_Rigged swap recipe
    // ---------------------------------------------------------------- //

    /// <summary>Upgrades RPGSoldierPrefab's primitive body to the rigged
    /// Sandstorm Sentinel model (same recipe as SoldierPrefab). Preserves
    /// RocketCombat, FirePoint/RPGLauncher, Health, selection, agent. Backup
    /// saved as RPGSoldierPrefab_PRIMITIVE_Backup.prefab.</summary>
    public static void UpgradeRPGSoldierVisual()
    {
        const string prefabPath = "Assets/_Game/Prefabs/RPGSoldierPrefab.prefab";
        const string backupPath = "Assets/_Game/Prefabs/RPGSoldierPrefab_PRIMITIVE_Backup.prefab";
        const string fbxPath    = "Assets/_Game/Art/Soldier/Soldier_Unity_Final/Soldier_Rigged.fbx";

        if (File.Exists(backupPath)) AssetDatabase.DeleteAsset(backupPath);
        AssetDatabase.CopyAsset(prefabPath, backupPath);

        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            Transform vr = root.transform.Find("SoldierVisualRoot");
            if (vr == null) { vr = new GameObject("SoldierVisualRoot").transform; vr.SetParent(root.transform, false); }

            // Keep the launcher + FirePoint working: if RPGLauncher lives under
            // a child we're about to disable, reparent it to the prefab root.
            Transform launcher = FindDeep(root.transform, "RPGLauncher");
            if (launcher != null && launcher.parent != root.transform)
                launcher.SetParent(root.transform, true);

            // Disable every legacy visual child of root + visual root, except
            // gameplay children (HealthBar, SelectionCircle, launcher, FirePoint).
            string[] keep = { "HealthBar", "SelectionCircle", "RPGLauncher", "FirePoint", "SoldierVisualRoot" };
            foreach (Transform c in root.transform)
            {
                if (System.Array.IndexOf(keep, c.name) >= 0) continue;
                if (c.gameObject.activeSelf) c.gameObject.SetActive(false);
            }
            foreach (Transform c in vr)
                if (c.name != "ImportedModelGoesHere" && c.gameObject.activeSelf)
                    c.gameObject.SetActive(false);

            // Holder + model instance (rot -90X stand-up, ground -0.1, scale 1).
            Transform holder = vr.Find("ImportedModelGoesHere");
            if (holder == null) { holder = new GameObject("ImportedModelGoesHere").transform; holder.SetParent(vr, false); }
            for (int i = holder.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(holder.GetChild(i).gameObject);

            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var model = (GameObject)PrefabUtility.InstantiatePrefab(fbx, holder);
            model.name = "RPG_Rigged";
            model.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            model.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            model.transform.localScale    = Vector3.one;

            // LODGroup with explicit bounds (RecalculateBounds is unreliable in
            // prefab contents — the SoldierPrefab lesson).
            var lod = model.GetComponent<LODGroup>();
            if (lod == null) lod = model.AddComponent<LODGroup>();
            var smrs = new SkinnedMeshRenderer[3];
            int si = 0;
            foreach (var r in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r.name.StartsWith("SM_Soldier_LOD") && si < 3) smrs[si++] = r;
            if (si == 3)
            {
                lod.SetLODs(new[]
                {
                    new LOD(0.5f,  new Renderer[] { smrs[0] }),
                    new LOD(0.2f,  new Renderer[] { smrs[1] }),
                    new LOD(0.03f, new Renderer[] { smrs[2] }),
                });
                lod.size = 2f;
                lod.localReferencePoint = new Vector3(0f, 0.95f, 0f);
            }

            // Team color: applier slots (index 3 = M_TeamColor) per LOD renderer;
            // legacy marker off (it points at the disabled primitives).
            var marker = root.GetComponent<TeamColorMarker>();
            if (marker != null) marker.enabled = false;
            var applier = vr.GetComponent<TeamColorApplier>();
            if (applier == null) applier = vr.gameObject.AddComponent<TeamColorApplier>();
            applier.teamColorSlots.Clear();
            for (int i = 0; i < si; i++)
                applier.teamColorSlots.Add(new RendererMaterialSlot
                { renderer = smrs[i], materialIndexes = new System.Collections.Generic.List<int> { 3 } });

            // Launcher placement: shoulder height on the right, muzzle forward.
            if (launcher != null)
                launcher.localPosition = new Vector3(0.3f, 1.45f, 0.05f);

            // RocketCombat.firePoint sanity — keep existing reference if alive.
            var combat = root.GetComponent<RocketCombat>();
            if (combat != null && combat.firePoint == null)
            {
                Transform fp = FindDeep(root.transform, "FirePoint");
                if (fp != null) combat.firePoint = fp;
            }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Debug.Log("[BridgeOps] RPGSoldier upgraded to rigged model (backup: " + backupPath + "). " +
                      "LODs=" + si + ", launcher=" + (launcher != null ? "repositioned" : "MISSING") + ".");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        AssetDatabase.SaveAssets();
    }

    // ---------------------------------------------------------------- //
    // Deterministic gameplay verifications (play mode, numeric results)
    // ---------------------------------------------------------------- //

    // ---------------------------------------------------------------- //
    // Skirmish loop — install + test controls
    // ---------------------------------------------------------------- //

    /// <summary>EDIT MODE: installs a configured SkirmishDirector in the open
    /// scene (idempotent — reuses the existing GO). DevSandbox defaults:
    /// lanes at the road ends, waypoint at the crossroads, objective at the
    /// staging pad.</summary>
    public static void AddSkirmishDirector()
    {
        if (Application.isPlaying) { Debug.LogError("[BridgeOps] edit mode only"); return; }
        var go = GameObject.Find("SkirmishDirector");
        if (go == null) go = new GameObject("SkirmishDirector");
        var d = go.GetComponent<SkirmishDirector>();
        if (d == null) d = go.AddComponent<SkirmishDirector>();
        d.enemyRpgPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/_Game/Prefabs/EnemyRPGSoldierPrefab.prefab");
        d.spawnPoints     = new[] { new Vector3(45f, 0f, 2f), new Vector3(4f, 0f, 45f) };
        d.centralWaypoint = new Vector3(10f, 0f, 2f);
        d.objective       = new Vector3(0f, 0f, 0f);
        d.autoStart       = true;
        EditorUtility.SetDirty(d);
        var s = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(s);
        EditorSceneManager.SaveScene(s);
        Debug.Log("[BridgeOps] SkirmishDirector installed in '" + s.name +
                  "' (autoStart, 2 lanes, crossroads waypoint, MP-gated). Prefab=" +
                  (d.enemyRpgPrefab != null ? "OK" : "MISSING"));
    }

    [MenuItem("Tools/RTS/Test/Install Skirmish Director In Open Scene")]
    public static void MenuInstallSkirmish() { AddSkirmishDirector(); }

    // ---------------------------------------------------------------- //
    // PvP flow smoke test — drives the REAL network path single-client
    // (the project supports 1-player matches). Run stages in play mode
    // from MainMenuScene: PvpConnect → PvpStatus → PvpCreateRoom →
    // PvpStartMatch → PvpStatus → PvpLeave.
    // ---------------------------------------------------------------- //

    public static void PvpConnect()
    {
        var nm = Object.FindFirstObjectByType<NetworkManagerRTS>(FindObjectsInactive.Include);
        if (nm == null) { Debug.LogError("[BridgeOps] no NetworkManagerRTS"); return; }
        if (!nm.multiplayerMode) { nm.multiplayerMode = true; Debug.Log("[BridgeOps] multiplayerMode was off — enabled for test."); }
        nm.Connect();
        Debug.Log("[BridgeOps] ★ PvP: Connect() issued.");
    }

    public static void PvpStatus()
    {
#if PHOTON_UNITY_NETWORKING
        Debug.Log("[BridgeOps] ★ PvP status: connected=" + Photon.Pun.PhotonNetwork.IsConnectedAndReady +
                  " server=" + Photon.Pun.PhotonNetwork.NetworkClientState +
                  " inRoom=" + Photon.Pun.PhotonNetwork.InRoom +
                  (Photon.Pun.PhotonNetwork.InRoom
                      ? " room='" + Photon.Pun.PhotonNetwork.CurrentRoom.Name + "' players=" + Photon.Pun.PhotonNetwork.CurrentRoom.PlayerCount
                      : "") +
                  " localPlayerId=" + NetworkManagerRTS.LocalPlayerId +
                  " matchStarted=" + (NetworkMatchCoordinator.Instance != null && NetworkMatchCoordinator.Instance.IsMatchStarted));
#else
        Debug.LogError("[BridgeOps] Photon not installed");
#endif
    }

    public static void PvpCreateRoom()
    {
        var nm = Object.FindFirstObjectByType<NetworkManagerRTS>(FindObjectsInactive.Include);
        if (nm == null) { Debug.LogError("[BridgeOps] no NetworkManagerRTS"); return; }
        nm.CreateRoom("BridgeSmokeTest", "default");
        Debug.Log("[BridgeOps] ★ PvP: CreateRoom('BridgeSmokeTest') issued.");
    }

    public static void PvpStartMatch()
    {
        var co = NetworkMatchCoordinator.Instance;
        if (co == null) { Debug.LogError("[BridgeOps] no NetworkMatchCoordinator"); return; }
        bool ok = co.RequestMatchStart(new Color(0.2f, 0.55f, 1f));
        Debug.Log("[BridgeOps] ★ PvP: RequestMatchStart → " + ok);
    }

    public static void PvpCornerReport()
    {
        int active = 0, total = 0;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!t.name.StartsWith("CornerBase_")) continue;
            total++;
            if (t.gameObject.activeInHierarchy) active++;
        }
        int myUnits = 0;
        foreach (var ge in Object.FindObjectsByType<GameEntity>(FindObjectsSortMode.None))
            if (ge.ownerPlayerId == NetworkManagerRTS.LocalPlayerId && ge.GetComponent<Health>() != null) myUnits++;
        Debug.Log("[BridgeOps] ★ PvP corners: " + active + "/" + total + " active. Owned entities: " + myUnits +
                  " (localPlayerId=" + NetworkManagerRTS.LocalPlayerId + ").");
    }

    /// <summary>Builds a Windows development player to Builds/TwoClientTest/
    /// for use as Client B in true two-client PvP tests.</summary>
    public static void BuildTwoClientPlayer()
    {
        if (Application.isPlaying) { Debug.LogError("[BridgeOps] edit mode only"); return; }
        var opts = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/MainMenuScene.unity", "Assets/Scenes/SampleScene.unity" },
            locationPathName = "Builds/TwoClientTest/RtsGame.exe",
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log("[BridgeOps] ★ Build result: " + report.summary.result +
                  " size=" + (report.summary.totalSize / (1024 * 1024)) + "MB errors=" + report.summary.totalErrors +
                  " → " + opts.locationPathName);
    }

    /// <summary>Play-mode: owned soldier attacks the nearest enemy-owned unit
    /// (real combat path on the owner — damage then syncs master-authoritative).</summary>
    public static void PvpAttackNearestEnemy()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] play mode only"); return; }
        Health attackerH = null; UnitCombat combat = null; RocketCombat rocket = null;
        foreach (var ge in Object.FindObjectsByType<GameEntity>(FindObjectsSortMode.None))
        {
            if (ge.ownerPlayerId != NetworkManagerRTS.LocalPlayerId) continue;
            // In-children lookup: newer unit prefabs keep combat on a child rig.
            var c = ge.GetComponentInChildren<UnitCombat>(); var r = ge.GetComponentInChildren<RocketCombat>();
            if (c == null && r == null) continue;
            attackerH = ge.GetComponentInChildren<Health>(); combat = c; rocket = r;
            break;
        }
        if (attackerH == null) { Debug.LogError("[BridgeOps] no owned combat unit"); return; }

        Health best = null; float bestD = float.MaxValue;
        foreach (var h in Object.FindObjectsByType<Health>(FindObjectsSortMode.None))
        {
            var ge = h.GetComponent<GameEntity>();
            if (ge == null || ge.ownerPlayerId == NetworkManagerRTS.LocalPlayerId || ge.ownerPlayerId < 0) continue;
            if (h.CurrentHealth <= 0f) continue;
            float d = Vector3.Distance(attackerH.transform.position, h.transform.position);
            if (d < bestD) { bestD = d; best = h; }
        }
        if (best == null) { Debug.LogError("[BridgeOps] no enemy-owned target found"); return; }
        if (combat != null) combat.SetTarget(best); else rocket.SetTarget(best);
        Debug.Log("[BridgeOps] ★ PvP attack: '" + attackerH.name + "' → '" + best.name +
                  "' (owner " + best.GetComponent<GameEntity>().ownerPlayerId + ", d=" + bestD.ToString("F1") +
                  ", hp=" + best.CurrentHealth + ")");
    }

    /// <summary>Play-mode: full entity census (mirrors the build's auto-tester
    /// census) — name, owner, hp, position for every player-owned Health.
    /// Run twice ~10 s apart to verify remote units move (snapshot sync).</summary>
    public static void PvpEntityPositions()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] play mode only"); return; }
        int own = 0, other = 0;
        var sb = new System.Text.StringBuilder(
            "[BridgeOps] ── A-side census pid=" + NetworkManagerRTS.LocalPlayerId + " ──\n");
        foreach (var h in Object.FindObjectsByType<Health>(FindObjectsSortMode.None))
        {
            var ge = h.GetComponent<GameEntity>();
            int owner = ge != null ? ge.ownerPlayerId : -9;
            if (owner < 0) continue;            // skip neutral props — units/buildings only
            bool mine = owner == NetworkManagerRTS.LocalPlayerId;
            if (mine) own++; else other++;
            sb.Append(mine ? "  [own]  " : "  [other]").Append(h.gameObject.name)
              .Append(" owner=").Append(owner)
              .Append(" hp=").Append(h.CurrentHealth.ToString("F0"))
              .Append(" pos=").Append(h.transform.position.ToString("F1")).AppendLine();
        }
        sb.Append("  totals: own=").Append(own).Append(" other=").Append(other);
        Debug.Log(sb.ToString());
    }

    /// <summary>Play-mode: produce a Worker from the local player's
    /// CommandCenter via the REAL networked command path (CommandDispatcher →
    /// remote replay) — verifies production ownership syncs to both clients.</summary>
    public static void PvpProduceWorker()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] play mode only"); return; }
        foreach (var p in Object.FindObjectsByType<CommandCenterProducer>(FindObjectsSortMode.None))
        {
            var ge = GameEntity.EnsureOn(p.gameObject);
            if (ge.ownerPlayerId != NetworkManagerRTS.LocalPlayerId) continue;
            string spawnId = NetworkEntityIdAllocator.Allocate();
            CommandDispatcher.Issue(PlayerCommand.Produce(
                ge.ownerPlayerId, ge.EntityId, "Worker", spawnId));
            Debug.Log("[BridgeOps] ★ Produce(Worker) issued from '" + p.name +
                      "' owner=" + ge.ownerPlayerId + " spawnId=" + spawnId);
            return;
        }
        Debug.LogError("[BridgeOps] no owned CommandCenterProducer found");
    }

    /// <summary>Play-mode: move one owned mobile unit +8/+8 via the REAL
    /// networked Move command (not a direct MoveTo) — the remote client
    /// should see it travel.</summary>
    public static void PvpMoveOwnUnit()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] play mode only"); return; }
        foreach (var ge in Object.FindObjectsByType<GameEntity>(FindObjectsSortMode.None))
        {
            if (ge.ownerPlayerId != NetworkManagerRTS.LocalPlayerId) continue;
            if (ge.GetComponent<UnitMovement>() == null) continue;
            Vector3 dest = ge.transform.position + new Vector3(8f, 0f, 8f);
            CommandDispatcher.Issue(PlayerCommand.Move(
                ge.ownerPlayerId, new[] { ge.EntityId }, dest));
            Debug.Log("[BridgeOps] ★ Move command: '" + ge.name + "' → " + dest.ToString("F1"));
            return;
        }
        Debug.LogError("[BridgeOps] no owned mobile unit found");
    }

    /// <summary>Play-mode: apply 30 damage to the nearest enemy-PLAYER-owned
    /// unit through the real Health pipeline (master-authoritative broadcast)
    /// — the owning client's log must show the same single hp drop.</summary>
    public static void PvpDamageEnemyUnit()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] play mode only"); return; }
        Health best = null; int bestOwner = -1;
        foreach (var h in Object.FindObjectsByType<Health>(FindObjectsSortMode.None))
        {
            var ge = h.GetComponent<GameEntity>();
            if (ge == null || ge.ownerPlayerId < 0 ||
                ge.ownerPlayerId == NetworkManagerRTS.LocalPlayerId) continue;
            if (h.CurrentHealth <= 0f) continue;
            best = h; bestOwner = ge.ownerPlayerId; break;
        }
        if (best == null) { Debug.LogError("[BridgeOps] no enemy-player-owned entity found"); return; }
        float before = best.CurrentHealth;
        best.TakeDamage(30f);
        Debug.Log("[BridgeOps] ★ Damage test: '" + best.name + "' owner=" + bestOwner +
                  " hp " + before.ToString("F0") + " → " + best.CurrentHealth.ToString("F0") +
                  " (−30 expected, check owning client log for the SAME single drop)");
    }

    /// <summary>Play-mode: order the owned dozer to construct a Barracks
    /// 12 m away via the REAL networked Build command — the site and final
    /// building must appear with the same ids and owner on both clients.</summary>
    public static void PvpBuildBarracks()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] play mode only"); return; }
        foreach (var ge in Object.FindObjectsByType<GameEntity>(FindObjectsSortMode.None))
        {
            if (ge.ownerPlayerId != NetworkManagerRTS.LocalPlayerId) continue;
            if (!ge.name.Contains("Dozer")) continue;
            Vector3 pos = ge.transform.position + new Vector3(12f, 0f, 0f);
            string siteId  = NetworkEntityIdAllocator.Allocate();
            string finalId = NetworkEntityIdAllocator.Allocate();
            CommandDispatcher.Issue(PlayerCommand.Build(
                ge.ownerPlayerId, ge.EntityId, "Barracks", pos, siteId, finalId));
            Debug.Log("[BridgeOps] ★ Build(Barracks) issued: dozer='" + ge.name +
                      "' at " + pos.ToString("F1") + " siteId=" + siteId + " finalId=" + finalId);
            return;
        }
        Debug.LogError("[BridgeOps] no owned dozer found");
    }

    /// <summary>Play-mode: order the owned dozer to construct a PowerPlant
    /// 12 m behind it via the REAL networked Build command. Required before
    /// Barracks production — UnitProducer blocks unpowered production.</summary>
    public static void PvpBuildPowerPlant()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] play mode only"); return; }
        foreach (var ge in Object.FindObjectsByType<GameEntity>(FindObjectsSortMode.None))
        {
            if (ge.ownerPlayerId != NetworkManagerRTS.LocalPlayerId) continue;
            if (!ge.name.Contains("Dozer")) continue;
            Vector3 pos = ge.transform.position + new Vector3(-12f, 0f, 0f);
            string siteId  = NetworkEntityIdAllocator.Allocate();
            string finalId = NetworkEntityIdAllocator.Allocate();
            CommandDispatcher.Issue(PlayerCommand.Build(
                ge.ownerPlayerId, ge.EntityId, "PowerPlant", pos, siteId, finalId));
            Debug.Log("[BridgeOps] ★ Build(PowerPlant) issued: dozer='" + ge.name +
                      "' at " + pos.ToString("F1") + " siteId=" + siteId + " finalId=" + finalId);
            return;
        }
        Debug.LogError("[BridgeOps] no owned dozer found");
    }

    /// <summary>Play-mode: produce a Soldier from the local player's Barracks
    /// via the REAL networked Produce command (run after PvpBuildBarracks
    /// completes construction).</summary>
    public static void PvpProduceSoldier()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] play mode only"); return; }
        foreach (var p in Object.FindObjectsByType<UnitProducer>(FindObjectsSortMode.None))
        {
            var ge = GameEntity.EnsureOn(p.gameObject);
            if (ge.ownerPlayerId != NetworkManagerRTS.LocalPlayerId) continue;
            string spawnId = NetworkEntityIdAllocator.Allocate();
            CommandDispatcher.Issue(PlayerCommand.Produce(
                ge.ownerPlayerId, ge.EntityId, "Soldier", spawnId));
            Debug.Log("[BridgeOps] ★ Produce(Soldier) issued from '" + p.name +
                      "' owner=" + ge.ownerPlayerId + " spawnId=" + spawnId);
            return;
        }
        Debug.LogError("[BridgeOps] no owned UnitProducer (Barracks) found — build one first");
    }

    public static void PvpLeave()
    {
        var nm = Object.FindFirstObjectByType<NetworkManagerRTS>(FindObjectsInactive.Include);
        if (nm == null) { Debug.LogError("[BridgeOps] no NetworkManagerRTS"); return; }
        nm.LeaveRoom();
        Debug.Log("[BridgeOps] ★ PvP: LeaveRoom() issued.");
    }

    /// <summary>Presentation: softer shadows, warm key light, trilight ambient
    /// (no more pitch-black shadow sides) and a subtle distance haze. Applies
    /// to the OPEN scene and saves it. Run on DevSandbox and SampleScene.</summary>
    public static void PolishSceneLighting()
    {
        if (Application.isPlaying) { Debug.LogError("[BridgeOps] edit mode only"); return; }

        // Key light: keep the authored angle; soften and warm it.
        Light sun = null;
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (l.type == LightType.Directional) { sun = l; break; }
        if (sun != null)
        {
            sun.color = new Color(1f, 0.956f, 0.875f);
            sun.intensity = 1.15f;
            sun.shadowStrength = 0.72f;
            sun.shadows = LightShadows.Soft;
            EditorUtility.SetDirty(sun);
        }

        // Ambient: trilight lifts shadowed sides so units stay readable
        // anywhere on the map (the audit showed near-black shadow zones).
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor     = new Color(0.58f, 0.65f, 0.75f);
        RenderSettings.ambientEquatorColor = new Color(0.46f, 0.47f, 0.47f);
        RenderSettings.ambientGroundColor  = new Color(0.30f, 0.28f, 0.26f);

        // Subtle linear haze for depth. Starts beyond minimap/ortho range so
        // top-down readability is unaffected.
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogStartDistance = 110f;
        RenderSettings.fogEndDistance   = 320f;
        RenderSettings.fogColor = new Color(0.63f, 0.68f, 0.74f);

        var s = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(s);
        EditorSceneManager.SaveScene(s);
        Debug.Log("[BridgeOps] ★ Lighting polished + saved for scene '" + s.name +
                  "' (soft warm sun 0.72 shadow, trilight ambient, linear haze 110–320).");
    }

    /// <summary>Presentation: resizes every prefab's HealthBar by unit class —
    /// infantry keep slim bars, vehicles get wider, buildings get big readable
    /// bars. Adjusts heightOffset so bars float clear of the model.</summary>
    public static void PolishHealthBars()
    {
        if (Application.isPlaying) { Debug.LogError("[BridgeOps] edit mode only"); return; }
        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Game/Prefabs" });
        int touched = 0;
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.Contains("Backup")) continue;
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var bar = root.GetComponentInChildren<HealthBar>(true);
                if (bar == null) continue;

                bool isBuilding = root.GetComponent<Building>() != null;
                var cat = root.GetComponent<UnitCategory>();
                bool isVehicle  = cat != null && cat.category == UnitCategory.Category.Vehicle;
                bool isAircraft = cat != null && cat.category == UnitCategory.Category.Aircraft;

                if (isBuilding)      { bar.size = new Vector2(2.4f, 0.22f); bar.depth = 0.05f; }
                else if (isVehicle)  { bar.size = new Vector2(1.5f, 0.17f); bar.depth = 0.05f; }
                else if (isAircraft) { bar.size = new Vector2(1.4f, 0.16f); bar.depth = 0.05f; }
                else                 { bar.size = new Vector2(1.0f, 0.14f); bar.depth = 0.04f; }

                PrefabUtility.SaveAsPrefabAsset(root, path);
                touched++;
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        Debug.Log("[BridgeOps] ★ PolishHealthBars: resized bars on " + touched + " prefab(s).");
    }

    /// <summary>Presentation cleanup: deactivates the oversized model-dev
    /// reference soldiers (Soldier_Rigged / Soldier_LOD*) that sit in the
    /// middle of DevSandbox. Disabled, NOT deleted — still available for
    /// model work by re-enabling in the Hierarchy. Saves the scene.</summary>
    public static void TidyDevSandboxModels()
    {
        if (Application.isPlaying) { Debug.LogError("[BridgeOps] edit mode only"); return; }
        string[] names = { "Soldier_Rigged", "Soldier_LOD0", "Soldier_LOD1", "Soldier_LOD2" };
        int off = 0;
        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            foreach (var n in names)
                if (root.name == n && root.activeSelf)
                {
                    root.SetActive(false);
                    EditorUtility.SetDirty(root);
                    off++;
                }
        }
        if (off > 0)
        {
            var s = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(s);
            EditorSceneManager.SaveScene(s);
        }
        Debug.Log("[BridgeOps] ★ TidyDevSandboxModels: deactivated " + off +
                  " dev reference model(s) (kept in scene, disabled).");
    }

    /// <summary>Direction compliance: waves are a manual dev tool, never an
    /// auto-running mode. Flips autoStart OFF on the scene instance.</summary>
    public static void DisableSkirmishAutostart()
    {
        if (Application.isPlaying) { Debug.LogError("[BridgeOps] edit mode only"); return; }
        var d = Object.FindFirstObjectByType<SkirmishDirector>(FindObjectsInactive.Include);
        if (d == null) { Debug.Log("[BridgeOps] no SkirmishDirector in open scene — nothing to do."); return; }
        d.autoStart = false;
        EditorUtility.SetDirty(d);
        var s = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(s);
        EditorSceneManager.SaveScene(s);
        Debug.Log("[BridgeOps] SkirmishDirector.autoStart = FALSE in '" + s.name +
                  "' — waves now run only via Tools → RTS → Test menus.");
    }

    [MenuItem("Tools/RTS/Test/Spawn Enemy Wave Now (Play)")]
    public static void SkirmishSpawnWaveNow()
    {
        var d = Object.FindFirstObjectByType<SkirmishDirector>();
        if (d == null || !Application.isPlaying) { Debug.LogError("[BridgeOps] need play mode + director"); return; }
        d.SpawnWave();
    }

    [MenuItem("Tools/RTS/Test/Clear Skirmish Enemies (Play)")]
    public static void SkirmishClear()
    {
        var d = Object.FindFirstObjectByType<SkirmishDirector>();
        if (d == null || !Application.isPlaying) { Debug.LogError("[BridgeOps] need play mode + director"); return; }
        d.ClearEnemies();
    }

    /// <summary>Play-mode status snapshot of the skirmish + battlefield.</summary>
    public static void SkirmishStatus()
    {
        var d = Object.FindFirstObjectByType<SkirmishDirector>();
        if (d == null) { Debug.LogError("[BridgeOps] no director"); return; }
        int playerUnits = 0, enemyUnits = 0;
        foreach (Health h in Object.FindObjectsByType<Health>(FindObjectsSortMode.None))
        {
            if (h.CurrentHealth <= 0f) continue;
            if (h.team == Health.Team.Player) playerUnits++; else enemyUnits++;
        }
        Debug.Log("[BridgeOps] ★ Skirmish status: wave=" + d.WaveNumber +
                  " activeSkirmishEnemies=" + d.AliveCount +
                  " | alive Health — player-team=" + playerUnits + " enemy-team=" + enemyUnits);
    }

    /// <summary>Play-mode demo: spawn the upgraded RPG soldier in front of the
    /// camera so its new rigged visual + shoulder launcher can be eyeballed.</summary>
    public static void SpawnRPGDemo()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] play mode only"); return; }
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/RPGSoldierPrefab.prefab");
        var go = Object.Instantiate(prefab, new Vector3(10f, 0f, 2f), Quaternion.identity);
        go.name = "RPG_Demo";
        MoveRig(new Vector3(10f, 6f, -4f), "RPG demo");
        Debug.Log("[BridgeOps] RPG demo spawned at (10,0,2) — check body model, launcher position, team color.");
    }

    /// <summary>Spawns a soldier 2.2 m from a fuel barrel, detonates the
    /// barrel, and logs the soldier's HP — proves area damage numerically.</summary>
    public static void TestBarrelAreaDamage()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] play mode only"); return; }
        Health barrel = null;
        foreach (Health h in Object.FindObjectsByType<Health>(FindObjectsSortMode.None))
            if (h.gameObject.name == "FuelBarrel") { barrel = h; break; }
        if (barrel == null) { Debug.LogError("[BridgeOps] no FuelBarrel in scene"); return; }

        var soldierPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/SoldierPrefab.prefab");
        Vector3 pos = barrel.transform.position;
        var s = Object.Instantiate(soldierPrefab, pos + new Vector3(2.2f, 0f, 0f), Quaternion.identity);
        var sh = s.GetComponent<Health>();
        float before = sh.CurrentHealth;
        barrel.TakeDamage(999f);   // detonate → ExplodeOnDeath area damage runs synchronously
        Debug.Log("[BridgeOps] ★ AreaDamage test: soldier HP " + before + " → " + sh.CurrentHealth +
                  "  (expected ~" + (before - 35f * 0.75f).ToString("F0") + "±10; unchanged = FAIL)");
    }

    /// <summary>Spawns one soldier next to cover and one in the open, applies
    /// identical 20 damage, and logs both HPs — proves cover numerically.</summary>
    public static void TestCoverReduction()
    {
        if (!Application.isPlaying) { Debug.LogError("[BridgeOps] play mode only"); return; }
        CoverObject cover = Object.FindFirstObjectByType<CoverObject>();
        if (cover == null) { Debug.LogError("[BridgeOps] no CoverObject in scene"); return; }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/SoldierPrefab.prefab");
        var inCover = Object.Instantiate(prefab, cover.transform.position + new Vector3(1f, 0f, 0f), Quaternion.identity);
        var open    = Object.Instantiate(prefab, cover.transform.position + new Vector3(30f, 0f, 30f), Quaternion.identity);
        var hc = inCover.GetComponent<Health>();
        var ho = open.GetComponent<Health>();
        hc.TakeDamage(20f);
        ho.TakeDamage(20f);
        Debug.Log("[BridgeOps] ★ Cover test: in-cover HP=" + hc.CurrentHealth +
                  " (expect ~87), open HP=" + ho.CurrentHealth + " (expect 80). " +
                  (hc.CurrentHealth > ho.CurrentHealth ? "COVER WORKS ✓" : "COVER NOT APPLYING ✗"));
        Object.Destroy(inCover, 5f); Object.Destroy(open, 5f);
    }

    /// <summary>Removes scene-root instances whose source prefab asset no
    /// longer exists ("Missing Prefab" — renders nothing, errors every scene
    /// load). EDIT MODE only; saves the scene when something was removed.</summary>
    public static void CleanMissingPrefabInstances()
    {
        if (Application.isPlaying) { Debug.LogError("[BridgeOps] edit mode only"); return; }
        Scene s = SceneManager.GetActiveScene();
        int removed = 0;
        foreach (GameObject root in s.GetRootGameObjects())
        {
            if (PrefabUtility.GetPrefabInstanceStatus(root) == PrefabInstanceStatus.MissingAsset)
            {
                Debug.Log("[BridgeOps]   removing missing-prefab instance '" + root.name + "'");
                Undo.DestroyObjectImmediate(root);
                removed++;
            }
        }
        if (removed > 0) { EditorSceneManager.MarkSceneDirty(s); EditorSceneManager.SaveScene(s); }
        Debug.Log("[BridgeOps] CleanMissingPrefabInstances: removed " + removed + ".");
    }

    private static GameObject FindBuilding(string nameContains)
    {
        foreach (Building b in Object.FindObjectsByType<Building>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (b.gameObject.name.Contains(nameContains)) return b.gameObject;
        return null;
    }

    private static void MoveRig(Vector3 pos, string label)
    {
        var rig = Object.FindFirstObjectByType<RTSCamera>();
        if (rig == null) { Debug.LogError("[BridgeOps] no RTSCamera"); return; }
        rig.transform.position = pos;
        Debug.Log("[BridgeOps] Camera rig moved to " + pos + " (" + label + ").");
    }

    public static void InspectSceneSoldiers()
    {
        var sb = new System.Text.StringBuilder("[BridgeOps] ── Scene soldier inspection ──\n");
        foreach (GameEntity e in Object.FindObjectsByType<GameEntity>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (e.prefabTypeId != "Soldier") continue;
            sb.Append("• '").Append(e.name).Append("'  pos=").Append(e.transform.position.ToString("F1"))
              .Append("  rootRot=").Append(e.transform.eulerAngles.ToString("F0")).AppendLine();

            Transform vr = e.transform.Find("SoldierVisualRoot");
            if (vr == null) { sb.AppendLine("    NO SoldierVisualRoot!"); continue; }
            for (int i = 0; i < vr.childCount; i++)
            {
                Transform c = vr.GetChild(i);
                sb.Append("    ").Append(c.gameObject.activeSelf ? "[on] " : "[off] ").Append(c.name);
                if (c.name == "ImportedModelGoesHere" && c.childCount > 0)
                {
                    Transform mdl = c.GetChild(0);
                    sb.Append("  → model '").Append(mdl.name)
                      .Append("' act=").Append(mdl.gameObject.activeSelf)
                      .Append(" localRot=").Append(mdl.localEulerAngles.ToString("F0"))
                      .Append(" worldRot=").Append(mdl.eulerAngles.ToString("F0"))
                      .Append(" scale=").Append(mdl.localScale.ToString("F2"));
                    int smrOn = 0, smrTotal = 0;
                    foreach (var r in mdl.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    { smrTotal++; if (r.enabled && r.gameObject.activeInHierarchy) smrOn++; }
                    sb.Append(" SMR=").Append(smrOn).Append('/').Append(smrTotal);
                }
                sb.AppendLine();
            }
        }
        Debug.Log(sb.ToString());
    }
}
