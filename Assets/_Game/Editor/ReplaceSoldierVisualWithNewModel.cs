using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click visual swap for the existing <c>SoldierPrefab</c> — replaces the
/// primitive-cube body with the rigged Sandstorm Sentinel model in
/// <c>Assets/_Game/Art/Soldier/Soldier_Unity_Final</c> while leaving every
/// gameplay component on the prefab root untouched (UnitMovement, UnitCombat,
/// Health, SelectableUnit, UnitCategory, GroundAutoAttackController, GameEntity,
/// CapsuleCollider, NavMeshAgent, HealthBar, DozerBuilder).
///
/// <para>HOW IT WORKS, IN ORDER</para>
/// <list type="number">
///   <item>Verifies <c>Soldier_Rigged.fbx</c> + the five textures exist.</item>
///   <item>Configures the Soldier_Rigged FBX importer: Generic rig, Avatar
///         "Create From This Model", scale factor 1, no extra colliders.</item>
///   <item>Configures the Normal texture importer (TextureType = NormalMap).</item>
///   <item>Duplicates <c>SoldierPrefab.prefab</c> to
///         <c>SoldierPrefab_OLD_Backup.prefab</c> (overwritten each run).</item>
///   <item>Opens the live prefab via PrefabUtility and:
///   <list type="bullet">
///     <item>Disables (NOT deletes) the primitive-visual children:
///           LeftArm, RightArm, Head, Body, ChestStripe, Helmet, LeftLeg,
///           RightLeg, Backpack, TeamColorAccent_DISABLED, PrimitivePlaceholder,
///           RiflePlaceholder. SelectionCircle + HealthBar stay active.</item>
///     <item>Wipes <c>SoldierVisualRoot/ImportedModelGoesHere</c> of any stale
///           prior import, then instantiates Soldier_Rigged.fbx as its child.</item>
///     <item>Sets <c>UnitVisualModelSlot.model</c> on SoldierVisualRoot to the
///           new model's transform.</item>
///     <item>Builds a LODGroup on the new model root with three LOD levels
///           wired to SM_Soldier_LOD0 / LOD1 / LOD2 (50% / 20% / 3% cull
///           thresholds — README §5 recommendations).</item>
///     <item>Adds an Animator: Generic avatar from the FBX, applyRootMotion
///           false, runtimeAnimatorController LEFT NULL (the existing
///           SoldierAnimatorController_REAL targets the old Mixamo Humanoid
///           rig and won't retarget cleanly here — wiring a controller is a
///           separate, future task).</item>
///     <item>Re-points <c>UnitCombat.firePoint</c> at the FBX-internal
///           FirePoint Transform under the bone-mounted SM_Weapon so bullets
///           always spawn at the rifle barrel even through animations.
///           Falls back to the pre-existing FirePoint reference if the
///           bone-mounted one can't be located.</item>
///     <item>Wires the existing <c>TeamColorApplier</c> on SoldierVisualRoot:
///           clears the slot list, then adds three entries (one per LOD
///           SkinnedMeshRenderer) with <c>materialIndexes = [3]</c> — the
///           M_TeamColor slot per the README §7.</item>
///     <item>Disables the legacy root <c>TeamColorMarker</c> — TeamColorApplier
///           on the visual root fully supersedes it for the new model, and
///           the marker's renderer list points at now-inactive primitives.</item>
///   </list></item>
///   <item>Saves the prefab.</item>
/// </list>
///
/// <para>WHAT IT DOES NOT TOUCH</para>
/// <list type="bullet">
///   <item>Prefab root components (UnitMovement, UnitCombat, Health,
///         SelectableUnit, UnitCategory, GroundAutoAttackController, GameEntity,
///         CapsuleCollider, NavMeshAgent).</item>
///   <item>FirePoint Transform's parent identity — only the
///         <c>UnitCombat.firePoint</c> reference is repointed.</item>
///   <item>HealthBar, SelectionCircle — left active.</item>
///   <item>Root transform / scale / rotation / prefab name / prefab path.</item>
///   <item>Any gameplay script, the network sync, or production logic.</item>
///   <item>The old root TeamColorMarker is disabled, NOT removed, so a manual
///         "revert" rolls back cleanly.</item>
/// </list>
///
/// Re-running is safe: backup is re-copied, disabled children stay disabled,
/// ImportedModelGoesHere is wiped clean before re-instantiation.
///
/// Menus:
///   <c>Tools → RTS → Units → Replace Soldier Visual With New Model</c>
///   <c>Tools → RTS → Units → Validate Soldier Prefab</c>
/// </summary>
public static class ReplaceSoldierVisualWithNewModel
{
    // ================================================================== //
    // Asset paths
    // ================================================================== //

    private const string PrefabPath  = "Assets/_Game/Prefabs/SoldierPrefab.prefab";
    private const string BackupPath  = "Assets/_Game/Prefabs/SoldierPrefab_OLD_Backup.prefab";

    private const string AssetFolder   = "Assets/_Game/Art/Soldier/Soldier_Unity_Final";
    private const string RiggedFbxPath = AssetFolder + "/Soldier_Rigged.fbx";

    private const string TexBaseColor  = AssetFolder + "/Textures/Soldier_BaseColor.png";
    private const string TexNormal     = AssetFolder + "/Textures/Soldier_Normal.png";
    private const string TexRoughness  = AssetFolder + "/Textures/Soldier_Roughness.png";
    private const string TexMetallic   = AssetFolder + "/Textures/Soldier_Metallic.png";
    private const string TexEmission   = AssetFolder + "/Textures/Soldier_Emission.png";

    // ================================================================== //
    // Hierarchy contract
    // ================================================================== //

    private const string SoldierVisualRootName     = "SoldierVisualRoot";
    private const string ImportedModelHolderName   = "ImportedModelGoesHere";
    private const string FirePointName             = "FirePoint";
    private const string WeaponName                = "SM_Weapon";

    // Visual upscale for the new model. The Sandstorm Sentinel ships at a true
    // 1:1 ~1.90 m scale; under the RTS top-down camera this reads slightly
    // smaller than the previous primitive-cube body. 1.30 matches the old
    // footprint while keeping the existing CapsuleCollider (r=0.5, h=2.0)
    // happy — the visual sits inside the collider with no clipping. Tunable
    // in the Inspector after a swap.
    public const float NewVisualScale = 1.30f;

    // ================================================================== //
    // LOD configuration
    // ================================================================== //

    private const string LodMeshLod0Name = "SM_Soldier_LOD0";
    private const string LodMeshLod1Name = "SM_Soldier_LOD1";
    private const string LodMeshLod2Name = "SM_Soldier_LOD2";

    // screenRelativeTransitionHeight: the screen-height fraction BELOW which
    // we move to the next LOD. Below the lowest threshold the renderer is
    // culled. README §5 recommends 50% / 20% / 3% which we read as:
    //   LOD0 visible 100% → 50% screen height → transition at 0.5
    //   LOD1 visible  50% → 20% screen height → transition at 0.2
    //   LOD2 visible  20% →  3% screen height → transition at 0.03 (cull)
    private const float Lod0Transition = 0.5f;
    private const float Lod1Transition = 0.2f;
    private const float Lod2Transition = 0.03f;

    // LODGroup reference sphere — MUST be set explicitly. Calling
    // LODGroup.RecalculateBounds() during PrefabUtility.LoadPrefabContents
    // returns garbage because SkinnedMeshRenderer.bounds isn't initialised
    // until a real render pass runs, so the size ends up at ~0.022 (2 cm)
    // and the soldier is culled at every LOD level — invisible.
    // 2.0 m diameter matches the ~1.9 m model height; centre at hip height.
    private const float LodGroupSize = 2.0f;
    private static readonly Vector3 LodLocalReferencePoint = new Vector3(0f, 0.95f, 0f);

    // The M_TeamColor material slot index inside each LOD's renderer.
    // (README §7: slots 0..3 = Uniform / Armor / DarkGear / TeamColor.)
    private const int TeamColorMaterialSlot = 3;

    // ================================================================== //
    // 1. Entry — Replace
    // ================================================================== //

    [MenuItem("Tools/RTS/Units/Replace Soldier Visual With New Model")]
    public static void Replace()
    {
        Debug.Log("[ReplaceSoldier] ─── Replacing SoldierPrefab visual with Soldier_Unity_Final model ───");

        // -- A. Source-asset sanity ----------------------------------------
        if (!AssetExists(RiggedFbxPath, "Rigged FBX")) return;
        AssetExists(TexBaseColor, "BaseColor texture");
        AssetExists(TexNormal,    "Normal texture");
        AssetExists(TexRoughness, "Roughness texture");
        AssetExists(TexMetallic,  "Metallic texture");
        AssetExists(TexEmission,  "Emission texture");

        // -- B. FBX importer config (Generic rig + Avatar) ----------------
        ConfigureRiggedFbxImporter();

        // -- C. Normal-map texture importer config ------------------------
        ConfigureNormalTextureImporter();

        // -- D. Backup the prefab -----------------------------------------
        if (!File.Exists(PrefabPath))
        {
            Debug.LogError($"[ReplaceSoldier] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }
        if (File.Exists(BackupPath)) AssetDatabase.DeleteAsset(BackupPath);
        bool copied = AssetDatabase.CopyAsset(PrefabPath, BackupPath);
        Debug.Log(copied
            ? $"[ReplaceSoldier]   Backed up SoldierPrefab → '{BackupPath}'."
            : $"[ReplaceSoldier] ⚠ Backup failed at '{BackupPath}' — continuing anyway.");

        // -- E. Open prefab + run the visual swap -------------------------
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[ReplaceSoldier] ✗ LoadPrefabContents returned null.");
            return;
        }

        try
        {
            // E.1 — find SoldierVisualRoot first (was E.2 before; moved up
            //        because the legacy primitive children are nested INSIDE
            //        SoldierVisualRoot/PrimitivePlaceholder/..., not at the
            //        prefab root. Earlier versions of this tool walked the
            //        root and found nothing — log line was "Disabled 0".)
            Transform visualRoot = root.transform.Find(SoldierVisualRootName);
            if (visualRoot == null)
            {
                Debug.LogWarning($"[ReplaceSoldier]   '{SoldierVisualRootName}' missing — creating one.");
                visualRoot = new GameObject(SoldierVisualRootName).transform;
                visualRoot.SetParent(root.transform, worldPositionStays: false);
            }

            // E.2 — disable EVERY child of SoldierVisualRoot except
            //        ImportedModelGoesHere. That sweeps in one pass:
            //          • PrimitivePlaceholder (the cube body parts)
            //          • RiflePlaceholder (older rifle visual)
            //          • TeamColorAccent_DISABLED
            //          • The stale character.fbx PrefabInstance from a
            //            previous swap attempt (still nested here)
            //          • Anything else a future user pastes in.
            //        Inactive — NOT deleted, so PrefabUtility.Revert keeps
            //        the rollback path open.
            int disabled = 0, alreadyOff = 0;
            for (int i = 0; i < visualRoot.childCount; i++)
            {
                Transform child = visualRoot.GetChild(i);
                if (child == null) continue;
                if (child.name == ImportedModelHolderName) continue;

                if (child.gameObject.activeSelf)
                {
                    child.gameObject.SetActive(false);
                    disabled++;
                    Debug.Log($"[ReplaceSoldier]     · Disabled '{SoldierVisualRootName}/{child.name}'.");
                }
                else
                {
                    alreadyOff++;
                }
            }
            Debug.Log($"[ReplaceSoldier]   Disabled {disabled} legacy visual child(ren) under " +
                      $"'{SoldierVisualRootName}' (and {alreadyOff} already off). All non-{ImportedModelHolderName} " +
                      "siblings are now inactive — the new model is the only visible visual.");

            // E.3 — find or create ImportedModelGoesHere; ensure it's active.
            Transform holder = visualRoot.Find(ImportedModelHolderName);
            if (holder == null)
            {
                Debug.LogWarning($"[ReplaceSoldier]   '{ImportedModelHolderName}' missing — creating one.");
                holder = new GameObject(ImportedModelHolderName).transform;
                holder.SetParent(visualRoot, worldPositionStays: false);
            }
            if (!holder.gameObject.activeSelf)
            {
                holder.gameObject.SetActive(true);
                Debug.Log($"[ReplaceSoldier]     · Re-enabled '{ImportedModelHolderName}' (was inactive).");
            }

            // E.3b — clear any prior import under the holder (idempotent re-runs).
            for (int i = holder.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(holder.GetChild(i).gameObject);

            // E.4 — instantiate the new model under the holder.
            GameObject fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(RiggedFbxPath);
            if (fbxAsset == null)
            {
                Debug.LogError($"[ReplaceSoldier] ✗ Cannot load '{RiggedFbxPath}' as GameObject.");
                return;
            }
            GameObject newModel = (GameObject)PrefabUtility.InstantiatePrefab(fbxAsset, holder);
            newModel.name = "Soldier_Rigged";
            newModel.transform.localPosition    = Vector3.zero;
            newModel.transform.localRotation    = Quaternion.identity;
            // Scale bumped from 1.0 → NewVisualScale (1.30 default) so the
            // ~1.90 m model reads at the same footprint the primitive body
            // had on the RTS top-down camera. CapsuleCollider (r=0.5, h=2.0)
            // on the ROOT is untouched — the scale lives on the Visual child
            // only, so gameplay (selection raycast, NavMeshAgent radius,
            // attack range) is unaffected.
            newModel.transform.localScale       = Vector3.one * NewVisualScale;
            Debug.Log($"[ReplaceSoldier]   Spawned '{newModel.name}' under " +
                      $"'{SoldierVisualRootName}/{ImportedModelHolderName}' at local (0,0,0,euler 0,0,0,scale {NewVisualScale:F2}). " +
                      "Tweak NewVisualScale in code or the Visual child's localScale in the Inspector for finer tuning.");

            // E.5 — UnitVisualModelSlot.model ← newModel transform.
            UnitVisualModelSlot slot = visualRoot.GetComponent<UnitVisualModelSlot>();
            if (slot != null)
            {
                slot.model = newModel.transform;
                EditorUtility.SetDirty(slot);
                Debug.Log("[ReplaceSoldier]   UnitVisualModelSlot.model wired.");
            }

            // E.6 — LODGroup on the new model root.
            SetupLodGroup(newModel);

            // E.7 — Animator (Generic, avatar from FBX, controller LEFT NULL).
            SetupAnimator(newModel);

            // E.8 — Repoint UnitCombat.firePoint to the bone-mounted FirePoint.
            RewireFirePoint(root, newModel);

            // E.9 — TeamColorApplier wiring (slots 3 on each LOD renderer).
            WireTeamColorApplier(visualRoot.gameObject, newModel);

            // E.10 — disable the legacy root TeamColorMarker (its renderer list
            //        points at the now-inactive primitives).
            TeamColorMarker marker = root.GetComponent<TeamColorMarker>();
            if (marker != null && marker.enabled)
            {
                marker.enabled = false;
                EditorUtility.SetDirty(marker);
                Debug.Log("[ReplaceSoldier]   Root TeamColorMarker disabled — TeamColorApplier on " +
                          "SoldierVisualRoot now owns team-color painting.");
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[ReplaceSoldier] ✓ Done. SoldierPrefab now uses Soldier_Unity_Final. " +
                      "Gameplay components on the root are UNCHANGED. Validate with " +
                      "Tools → RTS → Units → Validate Soldier Prefab.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[ReplaceSoldier] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // 2. Validate
    // ================================================================== //

    // ================================================================== //
    // Fix Soldier Ground Alignment
    //
    // After the -90° X rotation the soldier stands upright but the FBX's
    // pivot isn't perfectly at the boot soles, so the visual hovers a few
    // cm above Y=0. Drop Soldier_Rigged.localPosition.y to the value below
    // until the boots sit on the ground.
    //
    // Only Soldier_Rigged.localPosition.y is written. localPosition.x/z,
    // localRotation, localScale, gameplay scripts, collider, NavMeshAgent,
    // FirePoint, materials, and team color are all untouched.
    //
    // Idempotent — sets the same Y each run. Tunable: tweak
    // SoldierVisualGroundY in code (or via Inspector after the menu runs,
    // which persists across re-runs of this menu only if the value here
    // hasn't changed).
    //
    // Menu: Tools → RTS → Units → Fix Soldier Ground Alignment
    // ================================================================== //

    // Negative = drops the visual into the ground. The number is the local
    // Y offset on Soldier_Rigged after the -90° X stand-up rotation. -0.10
    // is a typical "slight float" correction; tweak in 0.02 increments if
    // the soldier ends up sunk or still floating.
    public const float SoldierVisualGroundY = -0.10f;

    [MenuItem("Tools/RTS/Units/Fix Soldier Ground Alignment")]
    public static void FixSoldierGroundAlignment()
    {
        Debug.Log("[FixSoldierGround] ─── Aligning boots to ground ───");

        if (!File.Exists(PrefabPath))
        {
            Debug.LogError($"[FixSoldierGround] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[FixSoldierGround] ✗ LoadPrefabContents returned null.");
            return;
        }

        try
        {
            Transform visualRoot = root.transform.Find(SoldierVisualRootName);
            Transform holder     = visualRoot != null ? visualRoot.Find(ImportedModelHolderName) : null;
            Transform newModel   = holder != null && holder.childCount > 0 ? holder.GetChild(0) : null;

            if (newModel == null)
            {
                Debug.LogError("[FixSoldierGround] ✗ Could not find " +
                               $"'{SoldierVisualRootName}/{ImportedModelHolderName}/<model>'. Run Replace first.");
                return;
            }

            Vector3 before = newModel.localPosition;
            // Write Y only — X and Z stay exactly where they were.
            newModel.localPosition = new Vector3(before.x, SoldierVisualGroundY, before.z);

            Debug.Log($"[FixSoldierGround]   '{newModel.name}'.localPosition  " +
                      $"{before} → {newModel.localPosition}  (Y only; X={before.x:F4} and Z={before.z:F4} unchanged).");
            Debug.Log($"[FixSoldierGround]   Root, SoldierVisualRoot, ImportedModelGoesHere transforms NOT modified. " +
                      "Soldier_Rigged.localRotation, localScale, FirePoint, LODGroup, materials, TeamColorApplier NOT modified.");

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[FixSoldierGround] ✓ Saved. Press Play to verify the boots touch Y=0. " +
                      $"If still floating, lower SoldierVisualGroundY (try {SoldierVisualGroundY - 0.05f:F2}); " +
                      $"if sunk, raise it (try {SoldierVisualGroundY + 0.05f:F2}).");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[FixSoldierGround] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // Fix Soldier Visual Orientation
    //
    // The Sandstorm Sentinel FBX comes in lying on its back because the
    // Blender source uses Z-up authoring but the export didn't convert to
    // Unity's Y-up convention. Standard cure: rotate the visual -90° around
    // local X. That maps the model's authoring +Z (intended up) to Unity's
    // +Y (world up) so the soldier stands upright.
    //
    // Applied to Soldier_Rigged.localRotation ONLY. The prefab root and
    // SoldierVisualRoot keep identity rotation (per the user's spec:
    // "Keep SoldierPrefab root rotation = (0,0,0)"). Gameplay scripts,
    // collider, NavMeshAgent, UnitCombat, etc. are untouched.
    //
    // The pivot of the FBX is at the feet (README §2: "feet are on the
    // ground plane, origin at floor"), so after the -90 X rotation feet stay
    // on Y=0 and no localPosition.y bump is needed. If after testing the
    // soldier walks backward instead of forward, run this menu a second time
    // with a 180° Y override — see SoldierVisualForwardYOffset below.
    //
    // Idempotent — sets the rotation each run; no-ops if already correct.
    //
    // Menu: Tools → RTS → Units → Fix Soldier Visual Orientation
    // ================================================================== //

    // Default rotation for the visual swap. -90 X stands the model upright;
    // Y stays 0 so the model's authored forward axis still points +Z in
    // Unity (matching the gameplay root's forward). If the soldier walks
    // backward after standing, change SoldierVisualForwardYOffset to 180.
    private static readonly Vector3 SoldierVisualStandUpEuler = new Vector3(-90f, 0f, 0f);
    public const float SoldierVisualForwardYOffset = 0f;

    [MenuItem("Tools/RTS/Units/Fix Soldier Visual Orientation")]
    public static void FixSoldierVisualOrientation()
    {
        Debug.Log("[FixSoldierOrient] ─── Standing the soldier upright ───");

        if (!File.Exists(PrefabPath))
        {
            Debug.LogError($"[FixSoldierOrient] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[FixSoldierOrient] ✗ LoadPrefabContents returned null.");
            return;
        }

        try
        {
            // Locate Soldier_Rigged exactly where the swap tool places it.
            Transform visualRoot = root.transform.Find(SoldierVisualRootName);
            Transform holder     = visualRoot != null ? visualRoot.Find(ImportedModelHolderName) : null;
            Transform newModel   = holder != null && holder.childCount > 0 ? holder.GetChild(0) : null;

            if (newModel == null)
            {
                Debug.LogError("[FixSoldierOrient] ✗ Could not find " +
                               $"'{SoldierVisualRootName}/{ImportedModelHolderName}/<model>'. " +
                               "Run Replace Soldier Visual With New Model first.");
                return;
            }

            // Defensive check: never write to the prefab root rotation.
            if (root.transform.localRotation != Quaternion.identity)
            {
                Debug.LogWarning("[FixSoldierOrient] ⚠ Prefab root has a non-identity local rotation " +
                                 $"{root.transform.localEulerAngles}. This menu does NOT touch the root — leaving as-is.");
            }
            if (visualRoot.localRotation != Quaternion.identity)
            {
                Debug.LogWarning("[FixSoldierOrient] ⚠ SoldierVisualRoot has a non-identity local rotation " +
                                 $"{visualRoot.localEulerAngles}. Per the user's spec this menu does NOT touch it — " +
                                 "the rotation is applied to Soldier_Rigged only.");
            }

            // ---- Apply rotation -----------------------------------------
            Vector3 before          = newModel.localEulerAngles;
            Vector3 target          = SoldierVisualStandUpEuler;
            target.y               += SoldierVisualForwardYOffset;
            newModel.localRotation  = Quaternion.Euler(target);

            // Position is intentionally NOT touched. README §2: FBX pivot is
            // at feet. After -90 X the feet stay at Y=0. If a future test
            // shows the soldier sunk into the ground, only then bump
            // localPosition.y on this transform.
            Vector3 pos = newModel.localPosition;

            Debug.Log($"[FixSoldierOrient]   '{newModel.name}'.localEulerAngles  " +
                      $"{before} → {target}.  Root and SoldierVisualRoot rotations untouched.");
            Debug.Log($"[FixSoldierOrient]   '{newModel.name}'.localPosition stays at {pos}  " +
                      "(FBX pivot at feet per README §2 — no Y bump needed).");
            Debug.Log($"[FixSoldierOrient]   '{newModel.name}'.localScale stays at {newModel.localScale}  " +
                      "(FBX globalScale=100 already applied; rotation does not change size).");

            // ---- Verify LODGroup is still healthy -----------------------
            LODGroup lod = newModel.GetComponent<LODGroup>();
            if (lod != null)
            {
                if (lod.size < 1f)
                {
                    lod.size                = 2.0f;
                    lod.localReferencePoint = new Vector3(0f, 0.95f, 0f);
                    EditorUtility.SetDirty(lod);
                    Debug.Log("[FixSoldierOrient]   LODGroup size was < 1 m — restored to 2.0 with " +
                              "localReferencePoint (0, 0.95, 0).");
                }
                else
                {
                    Debug.Log($"[FixSoldierOrient]   LODGroup OK: size = {lod.size:F2} m, " +
                              $"localReferencePoint = {lod.localReferencePoint}, " +
                              $"3 LODs at {Lod0Transition:F2} / {Lod1Transition:F2} / {Lod2Transition:F2}.");
                }
            }

            // ---- Verify FirePoint is still reachable in the new tree ----
            UnitCombat combat = root.GetComponent<UnitCombat>();
            if (combat != null && combat.firePoint != null)
            {
                bool insideVisual = combat.firePoint.IsChildOf(newModel);
                Debug.Log($"[FixSoldierOrient]   UnitCombat.firePoint = '{combat.firePoint.name}'  " +
                          $"(child of Soldier_Rigged: {insideVisual}). The FirePoint is bone-mounted under " +
                          "SM_Weapon, so it rotates with the rifle bone — bullets continue to spawn at the " +
                          "barrel and aim forward through any animation.");
            }
            else
            {
                Debug.LogWarning("[FixSoldierOrient] ⚠ UnitCombat or its firePoint is null. " +
                                 "Run Replace Soldier Visual With New Model to rewire.");
            }

            // ---- Verify TeamColorApplier still maps slot 3 --------------
            TeamColorApplier applier = visualRoot != null
                ? visualRoot.GetComponent<TeamColorApplier>()
                : null;
            if (applier != null)
            {
                int slot3Count = 0;
                foreach (var slot in applier.teamColorSlots)
                    if (slot != null && slot.renderer != null && slot.materialIndexes.Contains(TeamColorMaterialSlot))
                        slot3Count++;
                Debug.Log($"[FixSoldierOrient]   TeamColorApplier still paints material slot {TeamColorMaterialSlot} " +
                          $"on {slot3Count} LOD renderer(s) — M_TeamColor (upper-arm bands + chest strip + knee pads " +
                          "only). Rotation does not affect material slot mapping.");
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[FixSoldierOrient] ✓ Saved. Press Play to verify upright stance.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[FixSoldierOrient] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // Fix Soldier FBX Import Scale
    //
    // The Sandstorm Sentinel FBX ships with its mesh vertices authored at
    // ~1 cm scale (mesh.bounds.size.y ≈ 0.01). With ModelImporter.globalScale
    // at the default 1, the rendered soldier ends up ~1.3 cm tall after the
    // prefab's Soldier_Rigged.localScale = 1.3 is applied — visually
    // invisible from any RTS camera distance. This menu bumps the importer's
    // globalScale to 100 so mesh.bounds.size.y becomes ~1.0 m and the
    // prefab renders at the intended ~1.3 m height with the existing 1.3 scale.
    //
    // Touches ONLY the FBX importer settings. Does NOT modify SoldierPrefab,
    // SoldierPrefab_OLD_Backup, materials, gameplay scripts, or the model
    // itself. After reimport, every consumer of this FBX (both prefabs)
    // sees the rescaled mesh automatically — no prefab edit required.
    //
    // Re-running is safe — sets globalScale to 100 each time, no-ops if it's
    // already 100.
    //
    // Menu: Tools → RTS → Units → Fix Soldier FBX Import Scale
    // ================================================================== //

    private const float SoldierFbxGlobalScale = 100f;

    [MenuItem("Tools/RTS/Units/Fix Soldier FBX Import Scale")]
    public static void FixSoldierFbxImportScale()
    {
        Debug.Log("[FixSoldierFbxScale] ─── Setting globalScale = 100 on Soldier_Rigged.fbx ───");

        if (!File.Exists(RiggedFbxPath))
        {
            Debug.LogError($"[FixSoldierFbxScale] ✗ FBX not found at '{RiggedFbxPath}'. Aborting.");
            return;
        }

        ModelImporter mi = AssetImporter.GetAtPath(RiggedFbxPath) as ModelImporter;
        if (mi == null)
        {
            Debug.LogError($"[FixSoldierFbxScale] ✗ Could not get ModelImporter for '{RiggedFbxPath}'.");
            return;
        }

        float before = mi.globalScale;
        if (!Mathf.Approximately(before, SoldierFbxGlobalScale))
        {
            mi.globalScale = SoldierFbxGlobalScale;
            mi.SaveAndReimport();
            AssetDatabase.Refresh();
            Debug.Log($"[FixSoldierFbxScale]   globalScale {before} → {SoldierFbxGlobalScale}. Reimport complete.");
        }
        else
        {
            Debug.Log($"[FixSoldierFbxScale]   globalScale already {SoldierFbxGlobalScale} — skipped reimport.");
        }

        // Verify the new mesh bounds by loading the FBX prefab post-reimport
        // and inspecting every Mesh sub-asset. SkinnedMeshRenderer.bounds in
        // an offline asset isn't authoritative, but mesh.bounds is — it's the
        // bake-time AABB of the vertex positions in mesh-local space.
        Debug.Log("[FixSoldierFbxScale] Post-reimport mesh bounds:");
        Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(RiggedFbxPath);
        int meshCount = 0;
        foreach (Object o in subAssets)
        {
            if (o is Mesh m && !m.name.StartsWith("Mixamo") && !m.name.Contains("Avatar"))
            {
                meshCount++;
                Bounds b = m.bounds;
                Debug.Log($"[FixSoldierFbxScale]   • '{m.name}'  bounds.size = {b.size}  center = {b.center}  " +
                          $"verticesY ≈ {b.size.y:F3} m");
            }
        }
        if (meshCount == 0)
            Debug.LogWarning("[FixSoldierFbxScale] ⚠ No Mesh sub-assets found in FBX — reimport may not have completed.");

        Debug.Log("[FixSoldierFbxScale] ✓ Done. SoldierPrefab and SoldierPrefab_OLD_Backup were NOT modified — they " +
                  "automatically pick up the rescaled mesh because they nest the FBX, not a copy. " +
                  "Press Play to verify visibility; if the height is wrong, tune Soldier_Rigged.localScale " +
                  "in the Inspector (currently 1.3 → ~1.3 m world height with the new mesh).");
        Debug.Log("[FixSoldierFbxScale] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // 3. Create And Bind Soldier Materials
    //    FBX ships with materialLocation=External, externalObjects={}.
    //    Renderer slots aren't bound to any project .mat — they fall back
    //    to Unity's "default-Material" which in URP renders as alpha=0 /
    //    unlit black. Creates 5 URP Lit materials in
    //    Assets/_Game/Materials/Soldier_New/ and adds them to the FBX's
    //    material remap so the renderers pick them up on reimport.
    // ================================================================== //

    private const string SoldierMaterialFolder = "Assets/_Game/Materials/Soldier_New";

    private const string MatNameUniform   = "M_Soldier_Uniform";
    private const string MatNameArmor     = "M_Soldier_Armor";
    private const string MatNameDarkGear  = "M_Soldier_DarkGear";
    private const string MatNameTeamColor = "M_TeamColor";
    private const string MatNameWeapon    = "M_Soldier_Weapon";

    [MenuItem("Tools/RTS/Units/Create And Bind Soldier Materials")]
    public static void CreateAndBindMaterials()
    {
        Debug.Log("[SoldierMaterials] ─── Creating + binding soldier materials ───");

        EnsureFolder(SoldierMaterialFolder);

        Texture2D baseColorTex = AssetDatabase.LoadAssetAtPath<Texture2D>(TexBaseColor);
        Texture2D normalTex    = AssetDatabase.LoadAssetAtPath<Texture2D>(TexNormal);
        Texture2D roughnessTex = AssetDatabase.LoadAssetAtPath<Texture2D>(TexRoughness);
        Texture2D metallicTex  = AssetDatabase.LoadAssetAtPath<Texture2D>(TexMetallic);

        if (baseColorTex == null) Debug.LogWarning($"[SoldierMaterials] ⚠ BaseColor texture missing at '{TexBaseColor}'.");
        if (normalTex    == null) Debug.LogWarning($"[SoldierMaterials] ⚠ Normal texture missing at '{TexNormal}'.");

        // Camo body materials all share the same baked Meshy texture per
        // README §7 — slots split so you can tint/tune them separately later.
        Material uniformMat   = CreateOrUpdateCamoMaterial(MatNameUniform,   baseColorTex, normalTex, metallicTex, roughnessTex);
        Material armorMat     = CreateOrUpdateCamoMaterial(MatNameArmor,     baseColorTex, normalTex, metallicTex, roughnessTex);
        Material darkGearMat  = CreateOrUpdateCamoMaterial(MatNameDarkGear,  baseColorTex, normalTex, metallicTex, roughnessTex);
        Material weaponMat    = CreateOrUpdateCamoMaterial(MatNameWeapon,    baseColorTex, normalTex, metallicTex, roughnessTex);
        Material teamColorMat = CreateOrUpdateTeamColorMaterial(MatNameTeamColor);

        AssetDatabase.SaveAssets();

        // Bind the materials to the FBX via the importer's external-objects
        // remap. AddRemap matches the literal material slot name baked into
        // the FBX (see README §7) to the .mat asset on disk.
        ModelImporter mi = AssetImporter.GetAtPath(RiggedFbxPath) as ModelImporter;
        if (mi == null)
        {
            Debug.LogError($"[SoldierMaterials] ✗ Cannot get ModelImporter for '{RiggedFbxPath}'.");
            return;
        }

        AddOrUpdateRemap(mi, MatNameUniform,   uniformMat);
        AddOrUpdateRemap(mi, MatNameArmor,     armorMat);
        AddOrUpdateRemap(mi, MatNameDarkGear,  darkGearMat);
        AddOrUpdateRemap(mi, MatNameTeamColor, teamColorMat);
        AddOrUpdateRemap(mi, MatNameWeapon,    weaponMat);

        // Make sure the importer is in the right mode for AddRemap to take
        // effect on reimport (External + remap mode).
        mi.materialLocation     = ModelImporterMaterialLocation.External;
        mi.materialName         = ModelImporterMaterialName.BasedOnMaterialName;
        mi.materialSearch       = ModelImporterMaterialSearch.RecursiveUp;
        mi.materialImportMode   = ModelImporterMaterialImportMode.ImportStandard;

        mi.SaveAndReimport();
        AssetDatabase.Refresh();

        Debug.Log($"[SoldierMaterials] ✓ Created 5 materials in '{SoldierMaterialFolder}' and bound them to '{RiggedFbxPath}'. " +
                  "Run Debug And Fix Soldier Visibility to confirm the renderers picked up the new materials.");
        Debug.Log("[SoldierMaterials] ─────────────────────────────────────────────");
    }

    private static Material CreateOrUpdateCamoMaterial(
        string name, Texture2D baseColor, Texture2D normal, Texture2D metallic, Texture2D roughness)
    {
        string path = SoldierMaterialFolder + "/" + name + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        bool created = mat == null;
        if (created)
        {
            mat = new Material(ResolveLitShader()) { name = name };
            AssetDatabase.CreateAsset(mat, path);
        }
        else
        {
            mat.shader = ResolveLitShader();
        }

        // BaseColor white so the texture isn't tinted.
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     Color.white);

        if (baseColor != null)
        {
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", baseColor);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", baseColor);
        }
        if (normal != null && mat.HasProperty("_BumpMap"))
        {
            mat.SetTexture("_BumpMap", normal);
            mat.SetFloat("_BumpScale", 1f);
            mat.EnableKeyword("_NORMALMAP");
        }
        if (metallic != null && mat.HasProperty("_MetallicGlossMap"))
        {
            mat.SetTexture("_MetallicGlossMap", metallic);
            mat.EnableKeyword("_METALLICSPECGLOSSMAP");
        }

        // Mid-low metallic, mid smoothness for a camo / matte uniform look.
        if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic",   0.1f);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.45f);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.45f);

        EditorUtility.SetDirty(mat);
        Debug.Log($"[SoldierMaterials]   {(created ? "Created" : "Updated")} '{path}'  " +
                  $"baseColor={(baseColor != null ? baseColor.name : "<null>")}  " +
                  $"normal={(normal != null ? normal.name : "<null>")}.");
        return mat;
    }

    private static Material CreateOrUpdateTeamColorMaterial(string name)
    {
        string path = SoldierMaterialFolder + "/" + name + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        bool created = mat == null;
        if (created)
        {
            mat = new Material(ResolveLitShader()) { name = name };
            AssetDatabase.CreateAsset(mat, path);
        }
        else
        {
            mat.shader = ResolveLitShader();
        }

        // White base — TeamColorApplier writes per-instance _BaseColor at runtime.
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     Color.white);

        // No textures; flat tint. Strip the keywords/textures if a previous
        // version added them by accident.
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", null);
        if (mat.HasProperty("_BumpMap")) mat.SetTexture("_BumpMap", null);
        if (mat.HasProperty("_MetallicGlossMap")) mat.SetTexture("_MetallicGlossMap", null);
        mat.DisableKeyword("_NORMALMAP");
        mat.DisableKeyword("_METALLICSPECGLOSSMAP");
        if (mat.HasProperty("_Metallic"))   mat.SetFloat("_Metallic",   0.0f);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.45f);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.45f);
        EditorUtility.SetDirty(mat);
        Debug.Log($"[SoldierMaterials]   {(created ? "Created" : "Updated")} '{path}'  (flat tint, TeamColorApplier owns runtime color).");
        return mat;
    }

    private static void AddOrUpdateRemap(ModelImporter mi, string slotName, Material mat)
    {
        if (mat == null) return;
        var ident = new AssetImporter.SourceAssetIdentifier(typeof(Material), slotName);
        mi.AddRemap(ident, mat);
        Debug.Log($"[SoldierMaterials]   FBX externalObjects remap: '{slotName}' → '{AssetDatabase.GetAssetPath(mat)}'.");
    }

    private static Shader ResolveLitShader()
    {
        Shader urp = Shader.Find("Universal Render Pipeline/Lit");
        Shader std = Shader.Find("Standard");
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

    // ================================================================== //
    // 4. Debug And Fix Soldier Visibility
    //    Runs in place on the existing prefab — no re-instantiation. Dumps
    //    every visibility-relevant state, then fixes the LODGroup bounds and
    //    any disabled renderers in one pass.
    // ================================================================== //

    [MenuItem("Tools/RTS/Units/Debug And Fix Soldier Visibility")]
    public static void DebugAndFixVisibility()
    {
        Debug.Log("[FixSoldierVisibility] ─── Diagnose + repair ───");

        if (!File.Exists(PrefabPath))
        {
            Debug.LogError($"[FixSoldierVisibility] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[FixSoldierVisibility] ✗ LoadPrefabContents returned null.");
            return;
        }

        try
        {
            int fixes = 0;

            // ---- A. Active state of the parent chain ---------------------
            Debug.Log($"[FixSoldierVisibility] Root '{root.name}' active = {root.activeSelf}");
            Transform visualRoot = root.transform.Find(SoldierVisualRootName);
            if (visualRoot == null)
            {
                Debug.LogError($"[FixSoldierVisibility] ✗ '{SoldierVisualRootName}' missing — re-run Replace.");
                return;
            }
            Debug.Log($"[FixSoldierVisibility] '{SoldierVisualRootName}' active = {visualRoot.gameObject.activeSelf}, " +
                      $"pos = {visualRoot.localPosition}, scale = {visualRoot.localScale}");
            if (!visualRoot.gameObject.activeSelf)
            {
                visualRoot.gameObject.SetActive(true);
                fixes++;
                Debug.Log("[FixSoldierVisibility]   · Re-enabled SoldierVisualRoot.");
            }

            // ---- B. Children of SoldierVisualRoot ------------------------
            for (int i = 0; i < visualRoot.childCount; i++)
            {
                Transform c = visualRoot.GetChild(i);
                if (c == null) continue;
                Debug.Log($"[FixSoldierVisibility]   • '{SoldierVisualRootName}/{c.name}' active = {c.gameObject.activeSelf}");
                if (c.name == ImportedModelHolderName)
                {
                    if (!c.gameObject.activeSelf)
                    {
                        c.gameObject.SetActive(true);
                        fixes++;
                        Debug.Log($"[FixSoldierVisibility]     · Re-enabled '{c.name}' (must be active).");
                    }
                }
                else
                {
                    if (c.gameObject.activeSelf)
                    {
                        c.gameObject.SetActive(false);
                        fixes++;
                        Debug.Log($"[FixSoldierVisibility]     · Disabled '{c.name}' (stale legacy visual).");
                    }
                }
            }

            // ---- C. The new model itself ---------------------------------
            Transform holder = visualRoot.Find(ImportedModelHolderName);
            if (holder == null || holder.childCount == 0)
            {
                Debug.LogError("[FixSoldierVisibility] ✗ No model under ImportedModelGoesHere — run Replace.");
                return;
            }
            Transform newModel = holder.GetChild(0);
            Debug.Log($"[FixSoldierVisibility] New model '{newModel.name}': active = {newModel.gameObject.activeSelf}, " +
                      $"localPos = {newModel.localPosition}, localScale = {newModel.localScale}, " +
                      $"localRot = {newModel.localEulerAngles}");
            if (!newModel.gameObject.activeSelf)
            {
                newModel.gameObject.SetActive(true);
                fixes++;
                Debug.Log("[FixSoldierVisibility]   · Re-enabled new model root.");
            }

            // ---- D. SkinnedMeshRenderers + materials --------------------
            SkinnedMeshRenderer[] smrs = newModel.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
            Debug.Log($"[FixSoldierVisibility] Found {smrs.Length} SkinnedMeshRenderer(s) under new model.");
            foreach (SkinnedMeshRenderer smr in smrs)
            {
                int slots = smr.sharedMaterials != null ? smr.sharedMaterials.Length : 0;
                string meshBounds = smr.sharedMesh != null
                    ? $"meshBounds.size={smr.sharedMesh.bounds.size}, center={smr.sharedMesh.bounds.center}"
                    : "sharedMesh=<null>";
                Debug.Log($"[FixSoldierVisibility]   • '{smr.gameObject.name}'  goActive={smr.gameObject.activeSelf}  " +
                          $"rendererEnabled={smr.enabled}  materialSlots={slots}  " +
                          $"sharedMesh={(smr.sharedMesh != null ? smr.sharedMesh.name : "<null>")}  " +
                          $"updateWhenOffscreen={smr.updateWhenOffscreen}  {meshBounds}  " +
                          $"localBounds.size={smr.localBounds.size}  rootBone={(smr.rootBone != null ? smr.rootBone.name : "<null>")}");

                if (!smr.gameObject.activeSelf)
                {
                    smr.gameObject.SetActive(true);
                    fixes++;
                    Debug.Log($"[FixSoldierVisibility]     · Re-enabled GameObject '{smr.gameObject.name}'.");
                }
                if (!smr.enabled)
                {
                    smr.enabled = true;
                    fixes++;
                    Debug.Log($"[FixSoldierVisibility]     · Re-enabled Renderer on '{smr.gameObject.name}'.");
                }

                // Per-slot material introspection. Prints assigned material's
                // name + shader + base-color alpha so the next compile of
                // Editor.log shows whether the slots are NULL, defaulted to
                // Lit-Default-Material, or pointing at proper extracted .mat
                // assets. ALSO logs a missingMaterial counter so a fix tool
                // can act on it.
                for (int j = 0; j < slots; j++)
                {
                    Material m = smr.sharedMaterials[j];
                    if (m == null)
                    {
                        Debug.LogWarning($"[FixSoldierVisibility]     ⚠ slot[{j}] = NULL  (renders as missing-pink at runtime)");
                        continue;
                    }
                    string shaderName = m.shader != null ? m.shader.name : "<no-shader>";
                    Color   baseCol   = m.HasProperty("_BaseColor")
                        ? m.GetColor("_BaseColor")
                        : (m.HasProperty("_Color") ? m.GetColor("_Color") : Color.magenta);
                    Texture baseTex   = m.HasProperty("_BaseMap")
                        ? m.GetTexture("_BaseMap")
                        : (m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null);
                    string assetPath  = AssetDatabase.GetAssetPath(m);
                    bool isInternal   = string.IsNullOrEmpty(assetPath) || assetPath.EndsWith(".fbx");
                    Debug.Log($"[FixSoldierVisibility]     · slot[{j}] mat='{m.name}'  shader='{shaderName}'  " +
                              $"baseColor=({baseCol.r:F2},{baseCol.g:F2},{baseCol.b:F2},a={baseCol.a:F2})  " +
                              $"baseMap={(baseTex != null ? baseTex.name : "<null>")}  " +
                              $"sourceAsset='{(string.IsNullOrEmpty(assetPath) ? "<runtime instance>" : assetPath)}'" +
                              (isInternal ? "  (INTERNAL — embedded in FBX; not a wired .mat)" : ""));
                    if (baseCol.a < 0.05f)
                        Debug.LogWarning($"[FixSoldierVisibility]     ⚠ slot[{j}] base-color alpha is {baseCol.a:F3} — soldier will be fully transparent.");
                }
            }

            // ---- E. LODGroup — the actual root cause of invisibility -----
            LODGroup lodGroup = newModel.GetComponentInChildren<LODGroup>(includeInactive: true);
            if (lodGroup == null)
            {
                Debug.LogWarning("[FixSoldierVisibility]   ⚠ No LODGroup found on new model — re-run Replace.");
            }
            else
            {
                Debug.Log($"[FixSoldierVisibility] LODGroup: size = {lodGroup.size:F4} m, " +
                          $"localReferencePoint = {lodGroup.localReferencePoint}, " +
                          $"enabled = {lodGroup.enabled}");

                LOD[] lvls = lodGroup.GetLODs();
                Debug.Log($"[FixSoldierVisibility]   • LOD level count = {lvls.Length}");
                for (int i = 0; i < lvls.Length; i++)
                {
                    int rcount = lvls[i].renderers != null ? lvls[i].renderers.Length : 0;
                    Debug.Log($"[FixSoldierVisibility]     LOD{i}: screenRelativeTransitionHeight = " +
                              $"{lvls[i].screenRelativeTransitionHeight:F3}, renderers = {rcount}");
                }

                // THE FIX: tiny size = invisible. If size is anywhere below
                // a reasonable threshold (1 m diameter), force it to the
                // correct 2 m sphere centred on the hips.
                if (lodGroup.size < 1f ||
                    Vector3.Distance(lodGroup.localReferencePoint, LodLocalReferencePoint) > 0.5f)
                {
                    float prevSize = lodGroup.size;
                    Vector3 prevRef = lodGroup.localReferencePoint;
                    lodGroup.size                = LodGroupSize;
                    lodGroup.localReferencePoint = LodLocalReferencePoint;
                    EditorUtility.SetDirty(lodGroup);
                    fixes++;
                    Debug.Log($"[FixSoldierVisibility]   · LODGroup bounds fixed: " +
                              $"size {prevSize:F4} → {LodGroupSize:F2},  " +
                              $"localReferencePoint {prevRef} → {LodLocalReferencePoint}. " +
                              "Tiny size = every LOD was culled = soldier invisible.");
                }
                if (!lodGroup.enabled)
                {
                    lodGroup.enabled = true;
                    EditorUtility.SetDirty(lodGroup);
                    fixes++;
                    Debug.Log("[FixSoldierVisibility]   · LODGroup re-enabled.");
                }
            }

            // ---- F. Save -------------------------------------------------
            if (fixes > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log($"[FixSoldierVisibility] ✓ Applied {fixes} fix(es) and saved '{PrefabPath}'.");
            }
            else
            {
                Debug.Log("[FixSoldierVisibility] ✓ Diagnosis complete — no fixes were needed.");
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[FixSoldierVisibility] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // 4. Validate
    // ================================================================== //

    [MenuItem("Tools/RTS/Units/Validate Soldier Prefab")]
    public static void Validate()
    {
        Debug.Log("[ValidateSoldier] ─── Audit ───");

        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (asset == null)
        {
            Debug.LogError($"[ValidateSoldier] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        int problems = 0;

        // Backup
        problems += V("Backup exists at " + BackupPath, File.Exists(BackupPath));

        // New model + holder
        Transform visualRoot = asset.transform.Find(SoldierVisualRootName);
        problems += V($"'{SoldierVisualRootName}' present", visualRoot != null);

        // Every child of SoldierVisualRoot EXCEPT ImportedModelGoesHere must
        // be inactive — that includes PrimitivePlaceholder (the cube body),
        // any stale character.fbx PrefabInstance, and anything else nested
        // here from previous swap attempts. Validation walks the children
        // directly rather than relying on a name list, so it catches future
        // additions automatically.
        int activeStale = 0;
        if (visualRoot != null)
        {
            for (int i = 0; i < visualRoot.childCount; i++)
            {
                Transform c = visualRoot.GetChild(i);
                if (c == null) continue;
                if (c.name == ImportedModelHolderName) continue;
                if (c.gameObject.activeSelf)
                {
                    Debug.LogWarning($"[ValidateSoldier]     ⚠ '{SoldierVisualRootName}/{c.name}' is ACTIVE — " +
                                     "old visual will draw on top of the new model. Re-run Replace.");
                    activeStale++;
                }
            }
        }
        problems += V($"All legacy children of '{SoldierVisualRootName}' inactive (got {activeStale} still active)",
            activeStale == 0);

        Transform holder = visualRoot != null ? visualRoot.Find(ImportedModelHolderName) : null;
        problems += V($"'{ImportedModelHolderName}' present under '{SoldierVisualRootName}'", holder != null);

        Transform newModel = holder != null && holder.childCount > 0 ? holder.GetChild(0) : null;
        problems += V($"New model instantiated under '{ImportedModelHolderName}'", newModel != null);
        if (newModel != null)
        {
            Debug.Log($"[ValidateSoldier]     • New model local scale = ({newModel.localScale.x:F3}, " +
                      $"{newModel.localScale.y:F3}, {newModel.localScale.z:F3})  " +
                      $"(NewVisualScale default = {NewVisualScale:F2}).");
        }

        UnitVisualModelSlot slot = visualRoot != null ? visualRoot.GetComponent<UnitVisualModelSlot>() : null;
        if (slot != null)
            problems += V("UnitVisualModelSlot.model == new model",
                slot.model == newModel);

        // LODGroup
        LODGroup lod = newModel != null ? newModel.GetComponentInChildren<LODGroup>(includeInactive: true) : null;
        problems += V("LODGroup present on new model", lod != null);
        if (lod != null)
        {
            LOD[] lods = lod.GetLODs();
            problems += V($"LODGroup has 3 levels (got {lods.Length})", lods.Length == 3);
        }

        // Animator
        Animator anim = newModel != null ? newModel.GetComponentInChildren<Animator>(includeInactive: true) : null;
        problems += V("Animator present on new model", anim != null);
        if (anim != null)
        {
            problems += V($"Animator.avatar non-null (got {(anim.avatar != null ? anim.avatar.name : "<null>")})",
                anim.avatar != null);
            // Controller LEFT NULL by design — don't fail validation on it.
            Debug.Log($"[ValidateSoldier]     • Animator.runtimeAnimatorController = " +
                      $"{(anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "<null — wire later if you author clips>")}");
        }

        // FirePoint reference
        UnitCombat combat = asset.GetComponent<UnitCombat>();
        problems += V("UnitCombat present on root", combat != null);
        if (combat != null)
        {
            problems += V("UnitCombat.firePoint non-null", combat.firePoint != null);
            if (combat.firePoint != null)
            {
                bool insideVisual = visualRoot != null && combat.firePoint.IsChildOf(visualRoot);
                Debug.Log($"[ValidateSoldier]     • UnitCombat.firePoint = '{combat.firePoint.name}'  " +
                          $"(inside SoldierVisualRoot subtree: {insideVisual})");
            }
        }

        // TeamColorApplier
        TeamColorApplier applier = visualRoot != null ? visualRoot.GetComponent<TeamColorApplier>() : null;
        problems += V("TeamColorApplier present on SoldierVisualRoot", applier != null);
        if (applier != null)
        {
            int validSlots = 0;
            for (int i = 0; i < applier.teamColorSlots.Count; i++)
            {
                var s = applier.teamColorSlots[i];
                if (s == null) continue;
                if (s.renderer == null) continue;
                if (!s.materialIndexes.Contains(TeamColorMaterialSlot)) continue;
                validSlots++;
            }
            problems += V($"TeamColorApplier.teamColorSlots has ≥1 entry painting slot {TeamColorMaterialSlot} " +
                          $"(got {validSlots})",
                validSlots >= 1);
        }

        // Legacy marker disabled
        TeamColorMarker marker = asset.GetComponent<TeamColorMarker>();
        problems += V("Root TeamColorMarker disabled (or absent)",
            marker == null || !marker.enabled);

        // CapsuleCollider untouched
        CapsuleCollider cap = asset.GetComponent<CapsuleCollider>();
        problems += V("Root CapsuleCollider present (selection raycast surface)", cap != null);
        if (cap != null)
            Debug.Log($"[ValidateSoldier]     • CapsuleCollider radius={cap.radius} height={cap.height} center={cap.center}");

        // GameEntity ownership
        GameEntity ge = asset.GetComponent<GameEntity>();
        problems += V("GameEntity.prefabTypeId == 'Soldier'",
            ge != null && ge.prefabTypeId == "Soldier");

        if (problems == 0)
            Debug.Log("[ValidateSoldier] ✓ All checks passed.");
        else
            Debug.LogWarning($"[ValidateSoldier] ⚠ {problems} problem(s). " +
                             "Re-run Replace Soldier Visual With New Model.");
        Debug.Log("[ValidateSoldier] ─────────────────────────────────────────────");
    }

    private static int V(string label, bool ok)
    {
        Debug.Log($"[ValidateSoldier]   {(ok ? "✓" : "✗")}  {label}");
        return ok ? 0 : 1;
    }

    // ================================================================== //
    // FBX importer config — Generic rig, Avatar from this model, scale 1
    // ================================================================== //

    private static void ConfigureRiggedFbxImporter()
    {
        ModelImporter mi = AssetImporter.GetAtPath(RiggedFbxPath) as ModelImporter;
        if (mi == null)
        {
            Debug.LogWarning($"[ReplaceSoldier] ⚠ Could not get ModelImporter for '{RiggedFbxPath}'.");
            return;
        }

        bool changed = false;
        if (mi.animationType != ModelImporterAnimationType.Generic)
        {
            mi.animationType  = ModelImporterAnimationType.Generic;
            changed = true;
        }
        if (mi.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
        {
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            changed = true;
        }
        if (!Mathf.Approximately(mi.globalScale, 1f))
        {
            mi.globalScale = 1f;
            changed = true;
        }
        if (mi.addCollider)
        {
            mi.addCollider = false;
            changed = true;
        }
        if (mi.importNormals != ModelImporterNormals.Import)
        {
            mi.importNormals = ModelImporterNormals.Import;
            changed = true;
        }

        if (changed)
        {
            mi.SaveAndReimport();
            Debug.Log($"[ReplaceSoldier]   Reimported '{RiggedFbxPath}' as Generic rig (Avatar from this model, scale 1, no add-collider).");
        }
        else
        {
            Debug.Log("[ReplaceSoldier]   FBX importer already configured — skipped reimport.");
        }
    }

    private static void ConfigureNormalTextureImporter()
    {
        TextureImporter ti = AssetImporter.GetAtPath(TexNormal) as TextureImporter;
        if (ti == null) return;
        if (ti.textureType != TextureImporterType.NormalMap)
        {
            ti.textureType = TextureImporterType.NormalMap;
            ti.SaveAndReimport();
            Debug.Log("[ReplaceSoldier]   Soldier_Normal.png → TextureType = NormalMap.");
        }
    }

    // ================================================================== //
    // LODGroup setup
    // ================================================================== //

    private static void SetupLodGroup(GameObject newModel)
    {
        SkinnedMeshRenderer lod0 = FindChildSkinned(newModel.transform, LodMeshLod0Name);
        SkinnedMeshRenderer lod1 = FindChildSkinned(newModel.transform, LodMeshLod1Name);
        SkinnedMeshRenderer lod2 = FindChildSkinned(newModel.transform, LodMeshLod2Name);

        if (lod0 == null || lod1 == null || lod2 == null)
        {
            Debug.LogWarning("[ReplaceSoldier] ⚠ Could not locate all three SM_Soldier_LODn renderers — " +
                             $"LOD0={(lod0 != null ? "OK" : "MISSING")}, " +
                             $"LOD1={(lod1 != null ? "OK" : "MISSING")}, " +
                             $"LOD2={(lod2 != null ? "OK" : "MISSING")}. Skipping LODGroup.");
            return;
        }

        // The weapon renderer is added to every LOD level so it stays visible
        // at all distances. README §5: "weapon renderer can be added to every
        // LOD level".
        SkinnedMeshRenderer weapon = null;
        Transform weaponT = FindChildByName(newModel.transform, WeaponName);
        if (weaponT != null) weapon = weaponT.GetComponent<SkinnedMeshRenderer>();
        Renderer[] weaponArr = weapon != null ? new Renderer[] { weapon } : new Renderer[0];

        LODGroup group = newModel.GetComponent<LODGroup>();
        if (group == null) group = newModel.AddComponent<LODGroup>();

        var lods = new LOD[3];
        lods[0] = new LOD(Lod0Transition, Combine(lod0, weaponArr));
        lods[1] = new LOD(Lod1Transition, Combine(lod1, weaponArr));
        lods[2] = new LOD(Lod2Transition, Combine(lod2, weaponArr));
        group.SetLODs(lods);

        // Set bounding sphere EXPLICITLY. Do NOT call RecalculateBounds()
        // here — see LodGroupSize comment for why it produces a 2 cm sphere
        // that culls every LOD.
        group.size                = LodGroupSize;
        group.localReferencePoint = LodLocalReferencePoint;
        EditorUtility.SetDirty(group);

        Debug.Log($"[ReplaceSoldier]   LODGroup populated: " +
                  $"LOD0 ({Lod0Transition*100:F0}%), LOD1 ({Lod1Transition*100:F0}%), LOD2 ({Lod2Transition*100:F0}%). " +
                  $"size = {LodGroupSize:F2} m,  localReferencePoint = {LodLocalReferencePoint}. " +
                  $"Weapon renderer added to every level: {(weapon != null ? "yes" : "no (SM_Weapon not found)")}.");
    }

    private static Renderer[] Combine(Renderer body, Renderer[] extras)
    {
        var list = new List<Renderer> { body };
        for (int i = 0; i < extras.Length; i++)
            if (extras[i] != null) list.Add(extras[i]);
        return list.ToArray();
    }

    // ================================================================== //
    // Animator setup
    // ================================================================== //

    private static void SetupAnimator(GameObject newModel)
    {
        Animator anim = newModel.GetComponent<Animator>();
        if (anim == null) anim = newModel.AddComponent<Animator>();

        // Find the Avatar sub-asset inside the FBX (created when we set
        // avatarSetup = CreateFromThisModel + Generic).
        Avatar avatar = null;
        foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(RiggedFbxPath))
        {
            if (o is Avatar a) { avatar = a; break; }
        }
        if (avatar != null)
        {
            anim.avatar = avatar;
            Debug.Log($"[ReplaceSoldier]   Animator.avatar ← '{avatar.name}' (Generic).");
        }
        else
        {
            Debug.LogWarning("[ReplaceSoldier] ⚠ No Avatar sub-asset found in the rigged FBX. " +
                             "Animator added without an avatar — re-run after Unity finishes the FBX import.");
        }

        anim.applyRootMotion = false;
        // runtimeAnimatorController INTENTIONALLY left null — wire one later
        // when you author/import animation clips for the new Generic rig.
        EditorUtility.SetDirty(anim);
    }

    // ================================================================== //
    // FirePoint rewire
    // ================================================================== //

    private static void RewireFirePoint(GameObject root, GameObject newModel)
    {
        UnitCombat combat = root.GetComponent<UnitCombat>();
        if (combat == null)
        {
            Debug.LogWarning("[ReplaceSoldier] ⚠ Root UnitCombat missing — cannot rewire FirePoint.");
            return;
        }

        // The FBX's FirePoint sits under SM_Weapon, which is parented to the
        // RightHand bone. Find it by name anywhere under the new model.
        Transform fbxFire = FindChildByName(newModel.transform, FirePointName);
        if (fbxFire == null)
        {
            // Fallback: leave the old reference alone if it's still valid.
            if (combat.firePoint != null && combat.firePoint.IsChildOf(root.transform))
            {
                Debug.LogWarning("[ReplaceSoldier] ⚠ FBX-internal FirePoint not found — keeping the existing " +
                                 $"UnitCombat.firePoint reference '{combat.firePoint.name}'.");
                return;
            }
            Debug.LogError("[ReplaceSoldier] ✗ FBX-internal FirePoint not found AND existing reference is " +
                           "broken — bullets will fall back to chest height.");
            return;
        }

        Transform prev = combat.firePoint;
        combat.firePoint = fbxFire;
        EditorUtility.SetDirty(combat);
        Debug.Log($"[ReplaceSoldier]   UnitCombat.firePoint: " +
                  $"{(prev != null ? "'" + prev.name + "'" : "<null>")} → '{fbxFire.name}' " +
                  $"(bone-mounted; bullets follow the rifle through animations).");
    }

    // ================================================================== //
    // TeamColorApplier wiring
    // ================================================================== //

    private static void WireTeamColorApplier(GameObject visualRoot, GameObject newModel)
    {
        TeamColorApplier applier = visualRoot.GetComponent<TeamColorApplier>();
        if (applier == null) applier = visualRoot.AddComponent<TeamColorApplier>();

        applier.teamColorSlots.Clear();

        foreach (string name in new[] { LodMeshLod0Name, LodMeshLod1Name, LodMeshLod2Name })
        {
            SkinnedMeshRenderer smr = FindChildSkinned(newModel.transform, name);
            if (smr == null)
            {
                Debug.LogWarning($"[ReplaceSoldier] ⚠ '{name}' not found — skipping TeamColorApplier slot for it.");
                continue;
            }

            int slotCount = smr.sharedMaterials != null ? smr.sharedMaterials.Length : 0;
            if (slotCount <= TeamColorMaterialSlot)
            {
                Debug.LogWarning($"[ReplaceSoldier] ⚠ '{name}' has only {slotCount} material slot(s); " +
                                 $"expected at least {TeamColorMaterialSlot + 1}. Slot still added; " +
                                 "verify the FBX material remap.");
            }

            applier.teamColorSlots.Add(new RendererMaterialSlot
            {
                renderer = smr,
                materialIndexes = new List<int> { TeamColorMaterialSlot },
            });
            Debug.Log($"[ReplaceSoldier]     TeamColorApplier slot: renderer='{name}', materialIndexes=[{TeamColorMaterialSlot}].");
        }

        EditorUtility.SetDirty(applier);
        Debug.Log($"[ReplaceSoldier]   TeamColorApplier populated with {applier.teamColorSlots.Count} renderer slot(s) " +
                  $"(material index {TeamColorMaterialSlot} = M_TeamColor).");
    }

    // ================================================================== //
    // Lookup helpers
    // ================================================================== //

    private static bool AssetExists(string path, string label)
    {
        if (File.Exists(path)) return true;
        Debug.LogWarning($"[ReplaceSoldier] ⚠ Missing {label}: '{path}'.");
        return false;
    }

    private static SkinnedMeshRenderer FindChildSkinned(Transform parent, string name)
    {
        foreach (SkinnedMeshRenderer smr in parent.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true))
            if (smr != null && smr.gameObject.name == name) return smr;
        return null;
    }

    private static Transform FindChildByName(Transform parent, string name)
    {
        foreach (Transform t in parent.GetComponentsInChildren<Transform>(includeInactive: true))
            if (t != null && t.name == name) return t;
        return null;
    }
}
