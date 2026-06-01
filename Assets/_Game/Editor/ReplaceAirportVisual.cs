using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click visual-only swap of the Airfield/Airport building. The user
/// imported a new FBX at
///   Assets/_Game/Art/Models/Airport_New.fbx
/// (or under …/Airport/) and wants it to REPLACE the cube-built Hangar /
/// Tower / Runway / Apron / Pad meshes on AirfieldPrefab WITHOUT touching
/// gameplay (slots, taxi points, lane corridors, takeoff/landing markers,
/// Building / SelectableBuilding / Airfield / Health / PowerConsumer /
/// SelectionRing / GameEntity / TeamColorMarker on the root).
///
/// What this tool does, step by step:
///   1. Loads the prefab via PrefabUtility.LoadPrefabContents so writes go
///      back to the prefab asset only (no scene contamination).
///   2. Wraps every old visual child (by name — see <see cref="VisualNamesToBackup"/>)
///      into a new inactive child "OldVisual_Backup". Old transforms are
///      preserved in world space, so you can drag them back at any time.
///      No mesh is destroyed.
///   3. Removes any prior "Visual_NewFbx" / "TeamColorAccent_New" children so
///      the tool is idempotent — run as many times as you like.
///   4. Instantiates the new FBX as a child named "Visual_NewFbx" at local
///      position (0,0,0), rotation identity, scale (1,1,1).
///   5. Auto-fits the FBX to the existing BoxCollider footprint by reading
///      the combined renderer bounds. If autoFit is OFF, scale stays (1,1,1).
///   6. Sets every new GameObject's layer to "Building" so selection /
///      placement / aircraft pathing keep working unchanged.
///   7. Drops in a small team-color cube on top of the new building, wired
///      to TeamColorApplier. The cube is recoloured at runtime by the
///      MultiplayerColors / PlayerFactionManager system — exactly the same
///      pipeline TeamColorMarker on the root already uses.
///   8. Saves the prefab and unloads the temporary contents.
///
/// What this tool deliberately does NOT do:
///   • Modify the FBX import settings.
///   • Touch any gameplay component (Airfield, Building, Health, etc.).
///   • Move, delete, or re-parent any gameplay marker (Slot_*, Taxi_*,
///     runway / landing waypoints, SelectionRing).
///   • Re-bake the BoxCollider — selection / placement footprint is
///     unchanged.
///   • Try to assign materials to renderers whose imported materials are
///     missing. If the FBX imported pink, log a warning telling the user
///     where to assign materials in the FBX's Materials tab.
///
/// Re-run any time: re-running deletes the previous "Visual_NewFbx" and
/// "TeamColorAccent_New" and inserts fresh ones, but leaves any backed-up
/// originals (under "OldVisual_Backup") in place.
///
/// Menu: Tools → RTS → Buildings → Replace Airport Visual With New FBX
/// </summary>
public static class ReplaceAirportVisual
{
    private const string MenuPath          = "Tools/RTS/Buildings/Replace Airport Visual With New FBX";
    private const string AirfieldPrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";

    // The user's task lists the FBX path with an extra "Airport/" folder.
    // The asset is currently at the flat path. Probe both so either layout
    // works — first hit wins; if neither exists, the tool errors out clearly.
    private static readonly string[] NewFbxCandidatePaths =
    {
        "Assets/_Game/Art/Models/Airport_New.fbx",
        "Assets/_Game/Art/Models/Airport/Airport_New.fbx",
    };

    private const string BackupParentName  = "OldVisual_Backup";
    private const string NewVisualChildName = "Visual_NewFbx";
    private const string TeamColorChildName = "TeamColorAccent_New";

    /// <summary>
    /// Direct-child names of AirfieldPrefab that are purely visual and safe
    /// to disable. Anything not in this list (Slot_*, Taxi_*, runway markers,
    /// landing markers, SelectionRing) is gameplay and stays untouched.
    /// </summary>
    private static readonly string[] VisualNamesToBackup =
    {
        "Hangar",
        "Tower",
        "Runway",
        "Apron",
        "South_Taxiway",
        "RoofBanner",
        "TeamColorAccent_DISABLED",
        "Pad_0", "Pad_1", "Pad_2", "Pad_3", "Pad_4", "Pad_5",
    };

    // Box collider footprint of the existing prefab: size (20, 2, 28).
    // We auto-fit the new FBX so its horizontal extent stays within this
    // box (minus margin) — that way selection / placement / aircraft
    // pathing all keep working without manual scale tweaking.
    private const float FootprintX = 20f;
    private const float FootprintZ = 28f;
    private const float FootprintMargin = 0.05f;   // shrink by 5% so the FBX sits inside the collider
    private const bool  AutoFit         = true;    // turn off to inspect the FBX at native scale

    /// <summary>
    /// Hard cap on the auto-fit scale. Anything above this is REJECTED and
    /// the FBX is left at scale 1 with a loud error — because a 5× / 10×
    /// fit multiplied by an FBX's internal child scales is the documented
    /// cause of the runtime "giant wall" + freeze. If your FBX needs a real
    /// scale bigger than this, fix it at import: open the FBX in the
    /// Project window → Model tab → set Scale Factor (e.g. 100 if the model
    /// is in centimetres) and re-run.
    /// </summary>
    private const float MaxAutoFitScale = 3.0f;

    private const string BuildingLayerName = "Building";

    // ================================================================== //
    // Menu entry
    // ================================================================== //

    [MenuItem(MenuPath)]
    public static void Run()
    {
        Debug.Log("[ReplaceAirportVisual] ─── Replacing Airfield visual with new FBX ───");

        // ---- 1. Locate prefab + FBX ------------------------------------ //
        GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(AirfieldPrefabPath);
        if (prefabAsset == null)
        {
            Debug.LogError($"[ReplaceAirportVisual] ✗ Prefab not found at '{AirfieldPrefabPath}'. " +
                           "Run Tools → RTS → Air System → Create Airfield Prefab first.");
            return;
        }

        string newFbxPath = FindNewFbxPath();
        if (string.IsNullOrEmpty(newFbxPath))
        {
            string tried = string.Join(", ", NewFbxCandidatePaths);
            Debug.LogError($"[ReplaceAirportVisual] ✗ No 'Airport_New.fbx' found. " +
                           $"Looked in: {tried}. Re-import the FBX or move it to one of those paths.");
            return;
        }
        GameObject newFbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(newFbxPath);
        if (newFbxAsset == null)
        {
            Debug.LogError($"[ReplaceAirportVisual] ✗ FBX path resolved to '{newFbxPath}' " +
                           "but Unity failed to load it as a GameObject — is the model importer healthy?");
            return;
        }
        Debug.Log($"[ReplaceAirportVisual]   Prefab: {AirfieldPrefabPath}");
        Debug.Log($"[ReplaceAirportVisual]   New FBX: {newFbxPath}");

        // ---- 2. Open the prefab for editing in isolation --------------- //
        GameObject root = PrefabUtility.LoadPrefabContents(AirfieldPrefabPath);
        if (root == null)
        {
            Debug.LogError($"[ReplaceAirportVisual] ✗ PrefabUtility.LoadPrefabContents returned null " +
                           $"for '{AirfieldPrefabPath}'. Prefab may be corrupted.");
            return;
        }

        int buildingLayer = LayerMask.NameToLayer(BuildingLayerName);
        if (buildingLayer < 0)
        {
            Debug.LogWarning($"[ReplaceAirportVisual] ⚠ Layer '{BuildingLayerName}' not found. " +
                             "New visual children will use the prefab root's layer instead.");
            buildingLayer = root.layer;
        }

        try
        {
            // ---- 3. Back up old visuals ------------------------------- //
            int backedUp = BackupOldVisuals(root);

            // ---- 4. Insert new FBX visual ----------------------------- //
            GameObject newVisual = InsertNewFbx(root, newFbxAsset, buildingLayer);

            // ---- 5. Auto-fit scale ------------------------------------ //
            if (AutoFit && newVisual != null)
                AutoFitFootprint(newVisual);

            // ---- 6. Team-color accent --------------------------------- //
            CreateTeamColorAccent(root, buildingLayer);

            // ---- 7. Save back ---------------------------------------- //
            PrefabUtility.SaveAsPrefabAsset(root, AirfieldPrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ReplaceAirportVisual] ✓ Done. Backed up {backedUp} old visual child(ren) " +
                      "into 'OldVisual_Backup' (inactive). New FBX is at child 'Visual_NewFbx'. " +
                      "Run Air System → Validate Airfield Slots if you want to double-check gameplay " +
                      "anchors are still wired.");
            Debug.Log("[ReplaceAirportVisual] ─────────────────────────────────────────");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ================================================================== //
    // Step helpers
    // ================================================================== //

    private static string FindNewFbxPath()
    {
        for (int i = 0; i < NewFbxCandidatePaths.Length; i++)
            if (File.Exists(NewFbxCandidatePaths[i])) return NewFbxCandidatePaths[i];
        return null;
    }

    /// <summary>
    /// For every direct child of <paramref name="root"/> whose name matches
    /// one of <see cref="VisualNamesToBackup"/>, reparent it under
    /// "OldVisual_Backup" (inactive). World position is preserved so the
    /// backed-up meshes still render at their original spot if you reactivate
    /// the backup parent. Gameplay markers (Slot_*, Taxi_*, runway/landing
    /// waypoints, SelectionRing) are intentionally NOT in that list and are
    /// left alone.
    /// </summary>
    private static int BackupOldVisuals(GameObject root)
    {
        Transform backup = root.transform.Find(BackupParentName);
        if (backup == null)
        {
            GameObject backupGO = new GameObject(BackupParentName);
            backupGO.transform.SetParent(root.transform, false);
            backupGO.transform.localPosition = Vector3.zero;
            backupGO.transform.localRotation = Quaternion.identity;
            backupGO.transform.localScale    = Vector3.one;
            backupGO.SetActive(false);
            backup = backupGO.transform;
            Debug.Log($"[ReplaceAirportVisual]   Created '{BackupParentName}' (inactive) on root.");
        }
        else
        {
            Debug.Log($"[ReplaceAirportVisual]   Found existing '{BackupParentName}' — reusing it.");
        }

        HashSet<string> namesToBackup = new HashSet<string>(VisualNamesToBackup);
        int count = 0;
        // Snapshot children since reparenting mutates the list during iteration.
        List<Transform> children = new List<Transform>(root.transform.childCount);
        for (int i = 0; i < root.transform.childCount; i++) children.Add(root.transform.GetChild(i));

        for (int i = 0; i < children.Count; i++)
        {
            Transform child = children[i];
            if (child == null) continue;
            if (child == backup) continue;
            if (!namesToBackup.Contains(child.gameObject.name)) continue;

            child.SetParent(backup, worldPositionStays: true);
            child.gameObject.SetActive(false);
            Debug.Log($"[ReplaceAirportVisual]     Backed up '{child.name}' → '{BackupParentName}/{child.name}' (inactive).");
            count++;
        }

        if (count == 0)
            Debug.Log($"[ReplaceAirportVisual]   Nothing to back up — old visuals were already removed.");
        return count;
    }

    private static GameObject InsertNewFbx(GameObject root, GameObject fbxAsset, int buildingLayer)
    {
        // Idempotency: drop any prior insertion before adding a fresh one.
        Transform existing = root.transform.Find(NewVisualChildName);
        if (existing != null)
        {
            Debug.Log($"[ReplaceAirportVisual]   Removing prior '{NewVisualChildName}' for fresh re-insert.");
            Object.DestroyImmediate(existing.gameObject);
        }

        GameObject newVisual = (GameObject)PrefabUtility.InstantiatePrefab(fbxAsset);
        if (newVisual == null)
        {
            Debug.LogError("[ReplaceAirportVisual] ✗ Failed to instantiate the new FBX as a prefab variant. " +
                           "Tool aborted.");
            return null;
        }
        newVisual.name = NewVisualChildName;
        newVisual.transform.SetParent(root.transform, worldPositionStays: false);
        newVisual.transform.localPosition = Vector3.zero;
        newVisual.transform.localRotation = Quaternion.identity;
        newVisual.transform.localScale    = Vector3.one;

        // Layer-stamp every node under the new visual so selection / placement
        // overlap checks (which use the Building layer) behave consistently.
        SetLayerRecursive(newVisual, buildingLayer);

        // Defensive strip — FBXes routinely ship with components that survive
        // BPM's MonoBehaviour ghost strip (Light/Camera/Animator are Behaviour,
        // not MonoBehaviour) and break gameplay if left in place. MeshColliders
        // are the freeze villain; we strip them unconditionally.
        StripDangerousComponents(newVisual);

        // Warn loudly if the imported FBX has any null shared materials — those
        // render as bright pink and the user asked us to surface that case.
        WarnIfPinkMaterials(newVisual);

        Debug.Log($"[ReplaceAirportVisual]   Inserted '{NewVisualChildName}' from FBX at " +
                  "localPosition=(0,0,0), localRotation=identity, localScale=(1,1,1).");
        return newVisual;
    }

    /// <summary>
    /// Uniformly scales the new visual so its combined renderer bounds fit
    /// inside the existing BoxCollider footprint (X=20, Z=28) minus a small
    /// margin. The Y axis is scaled by the same factor — no anisotropic
    /// squash. This is the part that handles FBXes imported with the wrong
    /// "Scale Factor" or unit system without forcing the user to dig into
    /// the model importer.
    /// </summary>
    private static void AutoFitFootprint(GameObject newVisual)
    {
        Renderer[] rends = newVisual.GetComponentsInChildren<Renderer>(includeInactive: false);
        if (rends == null || rends.Length == 0)
        {
            Debug.LogWarning("[ReplaceAirportVisual]   ⚠ New FBX has no enabled renderers — " +
                             "auto-fit skipped. (Empty model?)");
            return;
        }

        Bounds combined = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) combined.Encapsulate(rends[i].bounds);

        Vector3 size = combined.size;
        if (size.x <= 0.0001f || size.z <= 0.0001f)
        {
            Debug.LogWarning($"[ReplaceAirportVisual]   ⚠ FBX bounds size {size} is degenerate — " +
                             "auto-fit skipped.");
            return;
        }

        float targetX = FootprintX * (1f - FootprintMargin);
        float targetZ = FootprintZ * (1f - FootprintMargin);
        float fitX = targetX / size.x;
        float fitZ = targetZ / size.z;
        float fit  = Mathf.Min(fitX, fitZ);

        if (fit > MaxAutoFitScale)
        {
            Debug.LogError($"[ReplaceAirportVisual]   ✗ Auto-fit wanted scale ×{fit:F3}, which exceeds the " +
                           $"safety cap of ×{MaxAutoFitScale:F1}. That high a scale multiplies with internal " +
                           "FBX child scales and produces wall-sized geometry + placement freezes. " +
                           "Visual_NewFbx left at scale 1. Fix it at the import side: select " +
                           "Airport_New.fbx in the Project window → Model tab → set Scale Factor to (e.g.) " +
                           "100 if the model is in centimetres, then re-run this tool.");
            return;
        }

        Vector3 oldScale = newVisual.transform.localScale;
        Vector3 newScale = oldScale * fit;
        newVisual.transform.localScale = newScale;

        Debug.Log($"[ReplaceAirportVisual]   Auto-fit: native bounds=({size.x:F2},{size.y:F2},{size.z:F2}), " +
                  $"target footprint=({targetX:F1},_,{targetZ:F1}), scale ×{fit:F3} → " +
                  $"localScale=({newScale.x:F3},{newScale.y:F3},{newScale.z:F3}). " +
                  $"(cap = ×{MaxAutoFitScale:F1}.)");
    }

    /// <summary>
    /// FBX models routinely ship with components that BPM's ghost strip can't
    /// touch (Light / Camera / Animator are not MonoBehaviour, so
    /// <c>Destroy(MonoBehaviour mb)</c> skips them), or that quietly cost a
    /// fortune at gameplay time (MeshColliders against the footprint check).
    /// Remove them here so the prefab is safe BEFORE it ever enters the
    /// placement / ghost pipeline.
    /// </summary>
    private static void StripDangerousComponents(GameObject newVisual)
    {
        int meshCols = 0, lights = 0, cams = 0, anims = 0, otherCols = 0;
        foreach (MeshCollider mc in newVisual.GetComponentsInChildren<MeshCollider>(true))
        {
            if (mc == null) continue;
            Object.DestroyImmediate(mc, true);
            meshCols++;
        }
        // Remove all OTHER colliders inside the FBX subtree too — the root
        // BoxCollider on the AirfieldPrefab owns the entire footprint.
        foreach (Collider c in newVisual.GetComponentsInChildren<Collider>(true))
        {
            if (c == null) continue;
            Object.DestroyImmediate(c, true);
            otherCols++;
        }
        foreach (Light l in newVisual.GetComponentsInChildren<Light>(true))
        {
            if (l == null) continue;
            Object.DestroyImmediate(l, true);
            lights++;
        }
        foreach (Camera cam in newVisual.GetComponentsInChildren<Camera>(true))
        {
            if (cam == null) continue;
            Object.DestroyImmediate(cam, true);
            cams++;
        }
        foreach (Animator a in newVisual.GetComponentsInChildren<Animator>(true))
        {
            if (a == null) continue;
            Object.DestroyImmediate(a, true);
            anims++;
        }
        if (meshCols + otherCols + lights + cams + anims > 0)
            Debug.LogWarning($"[ReplaceAirportVisual]   Stripped FBX components: " +
                             $"MeshColliders={meshCols}, other Colliders={otherCols}, " +
                             $"Lights={lights}, Cameras={cams}, Animators={anims}. " +
                             "Root BoxCollider owns the footprint; no visual collider needed.");
        else
            Debug.Log("[ReplaceAirportVisual]   FBX had no dangerous components to strip.");
    }

    private static void WarnIfPinkMaterials(GameObject root)
    {
        Renderer[] rends = root.GetComponentsInChildren<Renderer>(includeInactive: true);
        int pink = 0;
        for (int i = 0; i < rends.Length; i++)
        {
            Material[] mats = rends[i].sharedMaterials;
            if (mats == null) continue;
            for (int m = 0; m < mats.Length; m++)
                if (mats[m] == null) pink++;
        }
        if (pink > 0)
        {
            Debug.LogWarning($"[ReplaceAirportVisual]   ⚠ {pink} material slot(s) on the new FBX are null — " +
                             "these render PINK. Open the FBX in the Project window → Materials tab and " +
                             "either Extract Materials or remap them to URP Lit assets.");
        }
    }

    /// <summary>
    /// Adds a small team-coloured cube atop the building so the airport
    /// reads as the owner's color even when the new FBX is grey. The cube
    /// uses <see cref="TeamColorApplier"/> with slot 0 of its MeshRenderer,
    /// which the project's OwnerColorApplier / MultiplayerColors flow
    /// repaints automatically at match start.
    /// </summary>
    private static void CreateTeamColorAccent(GameObject root, int buildingLayer)
    {
        Transform old = root.transform.Find(TeamColorChildName);
        if (old != null) Object.DestroyImmediate(old.gameObject);

        GameObject accent = GameObject.CreatePrimitive(PrimitiveType.Cube);
        accent.name = TeamColorChildName;

        // Cubes from CreatePrimitive ship with a MeshCollider — strip it,
        // the airport already has its own BoxCollider for selection, and we
        // don't want this little flag to intercept clicks/raycasts.
        Collider col = accent.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col);

        accent.transform.SetParent(root.transform, false);
        // Sit it above the would-be tower (the old Tower child sat west of
        // the runway centreline). 6 m up so it's clearly visible from above.
        accent.transform.localPosition = new Vector3(-9f, 6f, 7f);
        accent.transform.localRotation = Quaternion.identity;
        accent.transform.localScale    = new Vector3(2f, 2f, 2f);
        SetLayerRecursive(accent, buildingLayer);

        TeamColorApplier applier = accent.AddComponent<TeamColorApplier>();
        applier.applyOnStart = true;
        MeshRenderer mr = accent.GetComponent<MeshRenderer>();
        applier.teamColorSlots = new List<RendererMaterialSlot>
        {
            new RendererMaterialSlot { renderer = mr, materialIndexes = new List<int> { 0 } },
        };

        Debug.Log($"[ReplaceAirportVisual]   Added '{TeamColorChildName}' (small cube + TeamColorApplier, " +
                  "slot 0). Color is resolved at runtime from the owner's army color.");
    }

    private static void SetLayerRecursive(GameObject go, int layer)
    {
        if (go == null) return;
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++)
            SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
    }
}
