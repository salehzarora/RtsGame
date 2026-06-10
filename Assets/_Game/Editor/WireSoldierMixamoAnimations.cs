using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Wires the four Mixamo animation FBXs to the existing SoldierPrefab via
/// Humanoid retargeting. Generic retargeting is impossible here — the Mixamo
/// skeleton is <c>mixamorig:*</c> while Soldier_Rigged uses plain Unity-Humanoid
/// bone names — so both sides are switched to Humanoid and the clips retarget
/// through muscle space.
///
/// <para>Menus</para>
/// <list type="bullet">
///   <item><b>Tools → RTS → Units → Wire Soldier Mixamo Animations</b> — the
///         full pipeline, in order:
///     <list type="number">
///       <item>Each Mixamo FBX → Humanoid rig, avatar CreateFromThisModel,
///             materials off, clip renamed (Idle/Walk/Attack/Death) with
///             loopTime set (Idle ✓, Walk ✓, Attack ✗, Death ✗).</item>
///       <item>Soldier_Rigged.fbx → Humanoid + CreateFromThisModel, then the
///             generated Avatar is VERIFIED (isValid &amp;&amp; isHuman). If the
///             mapping failed the importer is reverted to Generic and the run
///             aborts — the prefab is never touched with a broken avatar.</item>
///       <item>Creates <c>Assets/_Game/Animations/Soldier/Soldier_Mixamo.controller</c>
///             with parameters Speed (float) / IsAttacking (bool) / Attack
///             (trigger) / Die (trigger) — exactly what SoldierAnimator.cs
///             drives — and states Idle⇄Walk (Speed 0.05), AnyState→Attack
///             (trigger, exits back to Idle), AnyState→Death (trigger, no exit).</item>
///       <item>Opens SoldierPrefab.prefab: assigns the controller + Humanoid
///             avatar to the EXISTING Animator on the Soldier_Rigged instance
///             (no second Animator), keeps applyRootMotion = false, adds
///             SoldierAnimator to the root if missing and EXPLICITLY assigns
///             its animator reference — the auto-find would otherwise grab the
///             disabled legacy 'character' Animator that sits earlier in the
///             hierarchy.</item>
///     </list></item>
///   <item><b>Tools → RTS → Units → Validate Soldier Mixamo Setup</b> —
///         read-only audit of all of the above.</item>
///   <item><b>Tools → RTS → Units → Toggle Soldier Visual Rotation Fix</b> —
///         flips Soldier_Rigged.localRotation between (-90,0,0) and (0,0,0).
///         Humanoid playback may normalize the character upright on its own,
///         which would double-rotate against our stand-up fix; this cannot be
///         predicted offline. If the soldier lies down (or tips over) in Play
///         Mode after wiring, run this once and re-test.</item>
/// </list>
///
/// <para>WHAT IT DOES NOT TOUCH</para>
/// Mesh, materials, team color wiring, FirePoint reference, scale/globalScale,
/// prefab root transform, gameplay scripts, the OLD_Backup prefab, or the old
/// SoldierAnimatorController_REAL.controller.
/// </summary>
public static class WireSoldierMixamoAnimations
{
    // ------------------------------------------------------------------ //
    // Paths
    // ------------------------------------------------------------------ //

    private const string PrefabPath     = "Assets/_Game/Prefabs/SoldierPrefab.prefab";
    private const string RiggedFbxPath  = "Assets/_Game/Art/Soldier/Soldier_Unity_Final/Soldier_Rigged.fbx";
    private const string MixamoFolder   = "Assets/_Game/Animations/Soldier/Mixamo";
    private const string ControllerPath = "Assets/_Game/Animations/Soldier/Soldier_Mixamo.controller";

    private const string VisualRootName = "SoldierVisualRoot";
    private const string HolderName     = "ImportedModelGoesHere";

    // Animator parameter names — MUST stay in sync with SoldierAnimator.cs defaults.
    private const string ParamSpeed       = "Speed";
    private const string ParamIsAttacking = "IsAttacking";
    private const string ParamAttack      = "Attack";
    private const string ParamDie         = "Die";

    private const float WalkSpeedThreshold = 0.05f;

    // clipName → (fbx file name, loop)
    private static readonly (string clip, string file, bool loop)[] Clips =
    {
        ("Idle",   "Soldier_Idle",   true),
        ("Walk",   "Soldier_Walk",   true),
        ("Attack", "Soldier_Attack", false),
        ("Death",  "Soldier_Death",  false),
    };

    // ================================================================== //
    // 1. Wire — full pipeline
    // ================================================================== //

    [MenuItem("Tools/RTS/Units/Wire Soldier Mixamo Animations")]
    public static void Wire()
    {
        Debug.Log("[WireMixamo] ─── Wiring Mixamo animations to SoldierPrefab (Humanoid retarget) ───");

        // ---- Step 1: Mixamo FBXs → Humanoid + loop flags ---------------- //
        foreach (var (clip, file, loop) in Clips)
        {
            string path = $"{MixamoFolder}/{file}.fbx";
            if (!File.Exists(path))
            {
                Debug.LogError($"[WireMixamo] ✗ Missing '{path}' — aborting before any change.");
                return;
            }
        }

        foreach (var (clip, file, loop) in Clips)
        {
            string path = $"{MixamoFolder}/{file}.fbx";
            var mi = (ModelImporter)AssetImporter.GetAtPath(path);

            mi.animationType      = ModelImporterAnimationType.Human;
            mi.avatarSetup        = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.materialImportMode = ModelImporterMaterialImportMode.None; // skip junk Y-bot materials

            // Rename the take to a clean clip name + set looping.
            ModelImporterClipAnimation[] takes = mi.defaultClipAnimations;
            if (takes == null || takes.Length == 0)
            {
                Debug.LogError($"[WireMixamo] ✗ '{file}.fbx' contains no animation takes — aborting.");
                return;
            }
            for (int i = 0; i < takes.Length; i++)
            {
                takes[i].name     = i == 0 ? clip : $"{clip}_{i}";
                takes[i].loopTime = loop;
                // Keep the character planted: root motion is ignored at runtime
                // (applyRootMotion = false) so no extra bake flags needed.
            }
            mi.clipAnimations = takes;
            mi.SaveAndReimport();

            // Confirm the Humanoid avatar mapped (Mixamo rigs always should).
            Avatar av = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            bool ok = av != null && av.isValid && av.isHuman;
            Debug.Log($"[WireMixamo]   {file}.fbx → Humanoid, clip '{clip}' loop={loop}, " +
                      $"avatar={(ok ? "VALID" : "INVALID")}.");
            if (!ok)
            {
                Debug.LogError($"[WireMixamo] ✗ '{file}.fbx' Humanoid avatar failed to map — aborting. " +
                               "Open the FBX → Rig tab → Configure to inspect.");
                return;
            }
        }

        // ---- Step 2: Soldier_Rigged → Humanoid, verify, revert on failure  //
        var rmi = (ModelImporter)AssetImporter.GetAtPath(RiggedFbxPath);
        var prevType  = rmi.animationType;
        var prevSetup = rmi.avatarSetup;

        rmi.animationType = ModelImporterAnimationType.Human;
        rmi.avatarSetup   = ModelImporterAvatarSetup.CreateFromThisModel;
        rmi.SaveAndReimport();

        Avatar soldierAvatar = AssetDatabase.LoadAllAssetsAtPath(RiggedFbxPath)
                                            .OfType<Avatar>().FirstOrDefault();
        bool avatarOk = soldierAvatar != null && soldierAvatar.isValid && soldierAvatar.isHuman;

        if (!avatarOk)
        {
            // Hard revert — never leave the working soldier on a broken rig.
            rmi.animationType = prevType;
            rmi.avatarSetup   = prevSetup;
            rmi.SaveAndReimport();
            Debug.LogError("[WireMixamo] ✗ Soldier_Rigged Humanoid avatar is INVALID (auto-map failed " +
                           "or T-pose enforcement broke). REVERTED to Generic — prefab untouched. " +
                           "Open Soldier_Rigged.fbx → Rig → Configure to see which bone failed, then re-run.");
            return;
        }
        int mappedBones = soldierAvatar.humanDescription.human != null
            ? soldierAvatar.humanDescription.human.Length : 0;
        Debug.Log($"[WireMixamo]   Soldier_Rigged.fbx → Humanoid. Avatar '{soldierAvatar.name}' VALID, " +
                  $"{mappedBones} human bone mapping(s).");

        // ---- Step 3: Animator Controller -------------------------------- //
        AnimationClip Load(string clipName, string file)
        {
            return AssetDatabase.LoadAllAssetsAtPath($"{MixamoFolder}/{file}.fbx")
                .OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview") && c.name == clipName);
        }

        AnimationClip idleClip   = Load("Idle",   "Soldier_Idle");
        AnimationClip walkClip   = Load("Walk",   "Soldier_Walk");
        AnimationClip attackClip = Load("Attack", "Soldier_Attack");
        AnimationClip deathClip  = Load("Death",  "Soldier_Death");

        if (idleClip == null || walkClip == null || attackClip == null || deathClip == null)
        {
            Debug.LogError($"[WireMixamo] ✗ Clip load failed after reimport: " +
                           $"Idle={(idleClip != null)} Walk={(walkClip != null)} " +
                           $"Attack={(attackClip != null)} Death={(deathClip != null)}. Aborting.");
            return;
        }

        if (File.Exists(ControllerPath)) AssetDatabase.DeleteAsset(ControllerPath);
        AnimatorController ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        ctrl.AddParameter(ParamSpeed,       AnimatorControllerParameterType.Float);
        ctrl.AddParameter(ParamIsAttacking, AnimatorControllerParameterType.Bool);
        ctrl.AddParameter(ParamAttack,      AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter(ParamDie,         AnimatorControllerParameterType.Trigger);

        AnimatorStateMachine sm = ctrl.layers[0].stateMachine;
        AnimatorState idle   = sm.AddState("Idle",   new Vector3(260,   0, 0));
        AnimatorState walk   = sm.AddState("Walk",   new Vector3(260, 130, 0));
        AnimatorState attack = sm.AddState("Attack", new Vector3(520,  60, 0));
        AnimatorState death  = sm.AddState("Death",  new Vector3(520, 200, 0));
        idle.motion = idleClip;  walk.motion = walkClip;
        attack.motion = attackClip;  death.motion = deathClip;
        sm.defaultState = idle;

        var toWalk = idle.AddTransition(walk);
        toWalk.hasExitTime = false; toWalk.duration = 0.15f;
        toWalk.AddCondition(AnimatorConditionMode.Greater, WalkSpeedThreshold, ParamSpeed);

        var toIdle = walk.AddTransition(idle);
        toIdle.hasExitTime = false; toIdle.duration = 0.15f;
        toIdle.AddCondition(AnimatorConditionMode.Less, WalkSpeedThreshold, ParamSpeed);

        var anyAttack = sm.AddAnyStateTransition(attack);
        anyAttack.hasExitTime = false; anyAttack.duration = 0.05f;
        anyAttack.canTransitionToSelf = false;
        anyAttack.AddCondition(AnimatorConditionMode.If, 0f, ParamAttack);

        var attackDone = attack.AddTransition(idle);
        attackDone.hasExitTime = true; attackDone.exitTime = 0.9f; attackDone.duration = 0.1f;
        // No condition — Idle's Speed>0.05 transition immediately hands off to Walk if moving.

        var anyDie = sm.AddAnyStateTransition(death);
        anyDie.hasExitTime = false; anyDie.duration = 0.05f;
        anyDie.canTransitionToSelf = false;
        anyDie.AddCondition(AnimatorConditionMode.If, 0f, ParamDie);
        // Death has NO outgoing transitions — clip clamps on its last frame.

        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();
        Debug.Log($"[WireMixamo]   Controller created at '{ControllerPath}' " +
                  "(Idle⇄Walk on Speed, AnyState→Attack→Idle, AnyState→Death, no Death exit).");

        // ---- Step 4: Prefab wiring -------------------------------------- //
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            Transform visualRoot = root.transform.Find(VisualRootName);
            Transform holder     = visualRoot != null ? visualRoot.Find(HolderName) : null;
            Transform newModel   = holder != null && holder.childCount > 0 ? holder.GetChild(0) : null;
            if (newModel == null)
            {
                Debug.LogError($"[WireMixamo] ✗ '{VisualRootName}/{HolderName}/<model>' not found in prefab. Aborting.");
                return;
            }

            Animator anim = newModel.GetComponent<Animator>();
            if (anim == null) anim = newModel.gameObject.AddComponent<Animator>();
            anim.runtimeAnimatorController = ctrl;
            anim.avatar          = soldierAvatar;   // re-assert: Generic avatar was replaced by the Humanoid one
            anim.applyRootMotion = false;
            EditorUtility.SetDirty(anim);
            Debug.Log($"[WireMixamo]   Animator on '{newModel.name}': controller=Soldier_Mixamo, " +
                      $"avatar='{soldierAvatar.name}' (Humanoid), applyRootMotion=false.");

            SoldierAnimator sa = root.GetComponent<SoldierAnimator>();
            bool added = false;
            if (sa == null) { sa = root.AddComponent<SoldierAnimator>(); added = true; }
            // EXPLICIT assignment — Awake's GetComponentInChildren would find the
            // disabled legacy 'character' Animator first (it precedes
            // ImportedModelGoesHere in sibling order) and silently drive the
            // wrong, invisible rig.
            sa.animator = anim;
            EditorUtility.SetDirty(sa);
            Debug.Log($"[WireMixamo]   SoldierAnimator {(added ? "ADDED to" : "already on")} prefab root; " +
                      "animator reference wired EXPLICITLY to the new model's Animator.");

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[WireMixamo] ✓ Done. Press Play and produce a soldier. " +
                  "IF THE SOLDIER LIES DOWN OR TIPS OVER while animating, run " +
                  "Tools → RTS → Units → Toggle Soldier Visual Rotation Fix once and re-test " +
                  "(Humanoid playback may self-normalize orientation against the -90° stand-up fix).");
        Debug.Log("[WireMixamo] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // 2. Validate — read-only audit
    // ================================================================== //

    [MenuItem("Tools/RTS/Units/Validate Soldier Mixamo Setup")]
    public static void Validate()
    {
        Debug.Log("[ValidateMixamo] ─── Audit ───");
        int problems = 0;
        int V(string label, bool ok)
        {
            Debug.Log($"[ValidateMixamo]   {(ok ? "✓" : "✗")}  {label}");
            return ok ? 0 : 1;
        }

        // Mixamo FBXs
        foreach (var (clip, file, loop) in Clips)
        {
            string path = $"{MixamoFolder}/{file}.fbx";
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            problems += V($"{file}.fbx exists + Humanoid",
                mi != null && mi.animationType == ModelImporterAnimationType.Human);

            AnimationClip c = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .FirstOrDefault(x => !x.name.StartsWith("__preview") && x.name == clip);
            problems += V($"  clip '{clip}' present  (length={(c != null ? c.length.ToString("F2") : "?")}s, " +
                          $"loop={(c != null ? c.isLooping.ToString() : "?")}, expected loop={loop})",
                c != null && c.isLooping == loop);
        }

        // Soldier_Rigged avatar
        var rmi = AssetImporter.GetAtPath(RiggedFbxPath) as ModelImporter;
        problems += V("Soldier_Rigged.fbx rig = Humanoid",
            rmi != null && rmi.animationType == ModelImporterAnimationType.Human);
        Avatar av = AssetDatabase.LoadAllAssetsAtPath(RiggedFbxPath).OfType<Avatar>().FirstOrDefault();
        problems += V($"Soldier_Rigged avatar valid + human  ('{(av != null ? av.name : "<null>")}')",
            av != null && av.isValid && av.isHuman);

        // Controller
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        problems += V($"Controller exists at {ControllerPath}", ctrl != null);
        if (ctrl != null)
        {
            var names = ctrl.parameters.Select(p => p.name).ToArray();
            problems += V("  params: Speed / IsAttacking / Attack / Die",
                names.Contains(ParamSpeed) && names.Contains(ParamIsAttacking) &&
                names.Contains(ParamAttack) && names.Contains(ParamDie));
            var states = ctrl.layers[0].stateMachine.states.Select(s => s.state.name).ToArray();
            problems += V($"  states: Idle/Walk/Attack/Death  (got: {string.Join(", ", states)})",
                states.Contains("Idle") && states.Contains("Walk") &&
                states.Contains("Attack") && states.Contains("Death"));
            var death = ctrl.layers[0].stateMachine.states.FirstOrDefault(s => s.state.name == "Death").state;
            problems += V("  Death has no exit transitions",
                death != null && death.transitions.Length == 0);
        }

        // Prefab wiring
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab != null)
        {
            Transform holder = prefab.transform.Find($"{VisualRootName}/{HolderName}");
            Transform model  = holder != null && holder.childCount > 0 ? holder.GetChild(0) : null;
            Animator anim    = model != null ? model.GetComponent<Animator>() : null;
            problems += V("Animator on the new model", anim != null);
            if (anim != null)
            {
                problems += V("  controller = Soldier_Mixamo",
                    anim.runtimeAnimatorController != null &&
                    anim.runtimeAnimatorController.name == "Soldier_Mixamo");
                problems += V($"  avatar humanoid  ('{(anim.avatar != null ? anim.avatar.name : "<null>")}')",
                    anim.avatar != null && anim.avatar.isHuman);
                problems += V("  applyRootMotion = false", !anim.applyRootMotion);
            }

            SoldierAnimator sa = prefab.GetComponent<SoldierAnimator>();
            problems += V("SoldierAnimator on prefab root", sa != null);
            if (sa != null)
                problems += V("  SoldierAnimator.animator wired to the NEW model's Animator " +
                              "(not the legacy 'character' one)",
                    sa.animator != null && model != null && sa.animator.transform == model);

            UnitCombat combat = prefab.GetComponent<UnitCombat>();
            problems += V("UnitCombat.firePoint still wired",
                combat != null && combat.firePoint != null);
        }

        Debug.Log(problems == 0
            ? "[ValidateMixamo] ✓ All checks passed."
            : $"[ValidateMixamo] ⚠ {problems} problem(s) — re-run Wire Soldier Mixamo Animations.");
        Debug.Log("[ValidateMixamo] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // 2b. Idle-only safe test — Generic clip authored from the REAL bones
    //
    // The Mixamo Idle clip cannot drive this Generic rig (Humanoid clips are
    // ignored by Generic animators; Generic-imported Mixamo curves target
    // mixamorig:* paths that don't exist here). So this test authors a tiny
    // Generic breathing clip against the soldier's actual bone paths
    // (computed from the live prefab hierarchy — no guessing) and plays it
    // through a single-state controller. localRotation-only on Spine/Chest/
    // Head, composed with each bone's rest rotation: it cannot teleport,
    // collapse, or rescale the mesh, and the rig stays Generic — the
    // Humanoid failure mode is structurally impossible here.
    //
    // Outputs:
    //   Assets/_Game/Animations/Soldier/Soldier_Idle_Generic.anim
    //   Assets/_Game/Animations/Soldier/Soldier_Test_IdleOnly.controller
    //
    // Undo: Tools → RTS → Units → Rollback Mixamo Wiring (sets controller
    // back to null; the soldier returns to the static visible state).
    //
    // Menu: Tools → RTS → Units → Wire Idle-Only Test (Generic, Safe)
    // ================================================================== //

    private const string IdleClipPath       = "Assets/_Game/Animations/Soldier/Soldier_Idle_Generic.anim";
    private const string IdleControllerPath = "Assets/_Game/Animations/Soldier/Soldier_Test_IdleOnly.controller";
    private const float  IdleLoopSeconds    = 3f;

    [MenuItem("Tools/RTS/Units/Wire Idle-Only Test (Generic, Safe)")]
    public static void WireIdleOnlyTest()
    {
        Debug.Log("[IdleTest] ─── Idle-only safe test (Generic rig, authored clip) ───");

        // ---- Step 1 report: current state -------------------------------- //
        var rmi = AssetImporter.GetAtPath(RiggedFbxPath) as ModelImporter;
        Debug.Log($"[IdleTest]   FBX rig = {rmi?.animationType} (must be Generic), " +
                  $"globalScale = {rmi?.globalScale} (untouched).");
        if (rmi == null || rmi.animationType != ModelImporterAnimationType.Generic)
        {
            Debug.LogError("[IdleTest] ✗ Soldier_Rigged.fbx is not Generic — run Rollback Mixamo Wiring first.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            Transform visualRoot = root.transform.Find(VisualRootName);
            Transform holder     = visualRoot != null ? visualRoot.Find(HolderName) : null;
            Transform model      = holder != null && holder.childCount > 0 ? holder.GetChild(0) : null;
            if (model == null)
            {
                Debug.LogError($"[IdleTest] ✗ '{VisualRootName}/{HolderName}/<model>' not found. Aborting.");
                return;
            }
            Debug.Log($"[IdleTest]   Model: pos={model.localPosition} rot={model.localEulerAngles} " +
                      $"scale={model.localScale} (all preserved — read-only here).");

            // ---- Author the Generic idle clip from the real hierarchy ---- //
            Transform spine = FindBone(model, "Spine");
            Transform chest = FindBone(model, "Chest");
            Transform head  = FindBone(model, "Head");
            if (spine == null && chest == null && head == null)
            {
                Debug.LogError("[IdleTest] ✗ None of Spine/Chest/Head found under the model — " +
                               "bone names differ from README §3. Aborting (nothing changed).");
                return;
            }

            var clip = new AnimationClip { frameRate = 30f, legacy = false };

            if (spine != null) AddBreathCurves(clip, AnimationUtility.CalculateTransformPath(spine, model),
                                               spine.localRotation, new Vector3(1.6f, 0f, 0f));
            if (chest != null) AddBreathCurves(clip, AnimationUtility.CalculateTransformPath(chest, model),
                                               chest.localRotation, new Vector3(1.2f, 0f, 0f));
            if (head  != null) AddSwayCurves(clip,  AnimationUtility.CalculateTransformPath(head, model),
                                               head.localRotation, 2.0f);
            clip.EnsureQuaternionContinuity();

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            if (File.Exists(IdleClipPath)) AssetDatabase.DeleteAsset(IdleClipPath);
            AssetDatabase.CreateAsset(clip, IdleClipPath);
            Debug.Log($"[IdleTest]   Clip authored at '{IdleClipPath}': breathing on " +
                      $"{(spine != null ? "Spine " : "")}{(chest != null ? "Chest " : "")}" +
                      $"{(head != null ? "+ head sway" : "")} ({IdleLoopSeconds:F0}s loop, localRotation only).");

            // ---- Single-state controller --------------------------------- //
            if (File.Exists(IdleControllerPath)) AssetDatabase.DeleteAsset(IdleControllerPath);
            AnimatorController ctrl = AnimatorController.CreateAnimatorControllerAtPath(IdleControllerPath);
            AnimatorState idle = ctrl.layers[0].stateMachine.AddState("Idle", new Vector3(260, 0, 0));
            idle.motion = clip;
            ctrl.layers[0].stateMachine.defaultState = idle;
            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            Debug.Log($"[IdleTest]   Controller created at '{IdleControllerPath}' — one state, no parameters, no transitions.");

            // ---- Assign to the existing Animator (nothing else) ---------- //
            Animator anim = model.GetComponent<Animator>();
            if (anim == null) anim = model.gameObject.AddComponent<Animator>();
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion           = false;
            // AlwaysAnimate removes renderer-culling as a variable for this test.
            anim.cullingMode               = AnimatorCullingMode.AlwaysAnimate;
            EditorUtility.SetDirty(anim);
            Debug.Log($"[IdleTest]   Animator: controller=Soldier_Test_IdleOnly, avatar='{(anim.avatar != null ? anim.avatar.name : "<null>")}' " +
                      "(Generic, unchanged), applyRootMotion=false, cullingMode=AlwaysAnimate. " +
                      "SoldierAnimator NOT added (no parameters to drive).");

            // ---- Step 3 safety checks (read-only) ------------------------ //
            LODGroup lod = model.GetComponent<LODGroup>();
            Debug.Log($"[IdleTest]   LODGroup: size={(lod != null ? lod.size.ToString("F2") : "<none>")}, " +
                      $"ref={(lod != null ? lod.localReferencePoint.ToString() : "-")} (expected 2.00 / (0, 0.95, 0)).");
            int smrOn = 0, smrTotal = 0;
            foreach (SkinnedMeshRenderer smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            { smrTotal++; if (smr.enabled && smr.gameObject.activeSelf) smrOn++; }
            Debug.Log($"[IdleTest]   SkinnedMeshRenderers active: {smrOn}/{smrTotal}.");
            UnitCombat combat = root.GetComponent<UnitCombat>();
            Debug.Log($"[IdleTest]   UnitCombat.firePoint = '{(combat != null && combat.firePoint != null ? combat.firePoint.name : "<NULL>")}' (untouched).");
            TeamColorApplier tca = visualRoot != null ? visualRoot.GetComponent<TeamColorApplier>() : null;
            Debug.Log($"[IdleTest]   TeamColorApplier slots: {(tca != null ? tca.teamColorSlots.Count : 0)} (untouched).");

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[IdleTest] ✓ Done. Press Play → produce a soldier → he should be visible, upright, and " +
                  "breathing subtly (chest rise + small head sway on a 3 s loop). " +
                  "If ANYTHING is wrong, run Tools → RTS → Units → Rollback Mixamo Wiring to return to static. " +
                  "Walk/Attack/Death are NOT wired — stopping after Idle per plan.");
        Debug.Log("[IdleTest] ─────────────────────────────────────────────");
    }

    private static Transform FindBone(Transform parent, string name)
    {
        foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        Debug.LogWarning($"[IdleTest]   ⚠ Bone '{name}' not found — its curves are skipped.");
        return null;
    }

    /// <summary>Inhale/exhale: rest → rest*delta at mid-loop → rest. Quaternion
    /// curves composed with the bone's REST local rotation so the bind pose is
    /// the baseline (raw delta values would snap the bone to near-identity).</summary>
    private static void AddBreathCurves(AnimationClip clip, string path, Quaternion rest, Vector3 deltaEuler)
    {
        Quaternion mid = rest * Quaternion.Euler(deltaEuler);
        if (Quaternion.Dot(rest, mid) < 0f) mid = new Quaternion(-mid.x, -mid.y, -mid.z, -mid.w);
        float T = IdleLoopSeconds;
        SetQuatCurves(clip, path, new[] { (0f, rest), (T * 0.5f, mid), (T, rest) });
    }

    /// <summary>Head sway: rest → +deg at T/3 → −deg at 2T/3 → rest (Y axis).</summary>
    private static void AddSwayCurves(AnimationClip clip, string path, Quaternion rest, float deg)
    {
        Quaternion right = rest * Quaternion.Euler(0f, deg, 0f);
        Quaternion left  = rest * Quaternion.Euler(0f, -deg, 0f);
        if (Quaternion.Dot(rest, right) < 0f) right = new Quaternion(-right.x, -right.y, -right.z, -right.w);
        if (Quaternion.Dot(rest, left)  < 0f) left  = new Quaternion(-left.x,  -left.y,  -left.z,  -left.w);
        float T = IdleLoopSeconds;
        SetQuatCurves(clip, path, new[] { (0f, rest), (T / 3f, right), (2f * T / 3f, left), (T, rest) });
    }

    private static void SetQuatCurves(AnimationClip clip, string path, (float t, Quaternion q)[] keys)
    {
        for (int c = 0; c < 4; c++)
        {
            var curve = new AnimationCurve();
            foreach (var (t, q) in keys)
            {
                float v = c == 0 ? q.x : c == 1 ? q.y : c == 2 ? q.z : q.w;
                curve.AddKey(new Keyframe(t, v)); // zero tangents = gentle ease in/out
            }
            string prop = "localRotation." + "xyzw"[c];
            clip.SetCurve(path, typeof(Transform), prop, curve);
        }
    }

    // ================================================================== //
    // 2c. Full Generic animation set — Walk / Attack / Death + gameplay bridge
    //
    // Same proven-safe mechanism as the Idle test: clips authored at tool-run
    // time against the REAL bone hierarchy, Generic rig, localRotation (+ a
    // hips localPosition drop for Death) composed with each bone's rest pose.
    // Axis correctness is computed, not guessed: for every bone we convert the
    // desired WORLD rotation axis into that bone's local frame from the rest
    // pose, so leg swings pitch forward/back regardless of how the FBX authored
    // its bone axes.
    //
    // Arms are intentionally NOT animated in Walk — both hands grip the rifle,
    // and counter-sway would visibly tear the left hand off the foregrip.
    // Torso yaw + hip bob carry the walk read instead.
    //
    // Outputs:
    //   Assets/_Game/Animations/Soldier/Soldier_Idle_Generic.anim   (rebuilt)
    //   Assets/_Game/Animations/Soldier/Soldier_Walk_Generic.anim
    //   Assets/_Game/Animations/Soldier/Soldier_Attack_Generic.anim
    //   Assets/_Game/Animations/Soldier/Soldier_Death_Generic.anim
    //   Assets/_Game/Animations/Soldier/Soldier_Generic.controller
    //
    // Prefab wiring: controller on the existing Animator; SoldierAnimator
    // added to the root with its animator reference EXPLICITLY set (auto-find
    // would land on the disabled legacy 'character' Animator that precedes
    // the new model in sibling order).
    //
    // Undo: Rollback Mixamo Wiring (controller → null, SoldierAnimator removed).
    //
    // Menu: Tools → RTS → Units → Wire Full Soldier Animations (Generic, Safe)
    // ================================================================== //

    private const string WalkClipPath    = "Assets/_Game/Animations/Soldier/Soldier_Walk_Generic.anim";
    private const string AttackClipPath  = "Assets/_Game/Animations/Soldier/Soldier_Attack_Generic.anim";
    private const string DeathClipPath   = "Assets/_Game/Animations/Soldier/Soldier_Death_Generic.anim";
    private const string GenericCtrlPath = "Assets/_Game/Animations/Soldier/Soldier_Generic.controller";

    private const float WalkLoopSeconds   = 0.7f;
    private const float AttackLoopSeconds = 0.5f;
    private const float DeathSeconds      = 1.2f;

    private const float WalkLegSwingDeg = 22f;
    private const float WalkKneeBendDeg = 28f;
    private const float WalkHipsYawDeg  = 3f;
    private const float WalkHipsBobM    = 0.02f;

    private const float AttackRecoilDeg = 6f;

    private const float DeathHipsPitchDeg = 80f;
    private const float DeathHipsDropM    = 0.6f;
    private const float DeathKneeBendDeg  = 60f;
    private const float DeathSpineFoldDeg = 15f;

    [MenuItem("Tools/RTS/Units/Wire Full Soldier Animations (Generic, Safe)")]
    public static void WireFullGenericAnimations()
    {
        Debug.Log("[FullAnim] ─── Wiring full Generic animation set (Idle/Walk/Attack/Death) ───");

        var rmi = AssetImporter.GetAtPath(RiggedFbxPath) as ModelImporter;
        if (rmi == null || rmi.animationType != ModelImporterAnimationType.Generic)
        {
            Debug.LogError("[FullAnim] ✗ Soldier_Rigged.fbx is not Generic — run Rollback Mixamo Wiring first.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            Transform visualRoot = root.transform.Find(VisualRootName);
            Transform holder     = visualRoot != null ? visualRoot.Find(HolderName) : null;
            Transform model      = holder != null && holder.childCount > 0 ? holder.GetChild(0) : null;
            if (model == null)
            {
                Debug.LogError($"[FullAnim] ✗ '{VisualRootName}/{HolderName}/<model>' not found. Aborting.");
                return;
            }

            // ---- Locate bones (warns per missing name, skips its curves) -- //
            Transform spine = FindBone(model, "Spine");
            Transform chest = FindBone(model, "Chest");
            Transform head  = FindBone(model, "Head");
            Transform hips  = FindBone(model, "Hips");
            Transform lUpLeg = FindBone(model, "LeftUpperLeg");
            Transform rUpLeg = FindBone(model, "RightUpperLeg");
            Transform lLoLeg = FindBone(model, "LeftLowerLeg");
            Transform rLoLeg = FindBone(model, "RightLowerLeg");
            Transform rUpArm = FindBone(model, "RightUpperArm");
            if (hips == null || lUpLeg == null || rUpLeg == null)
            {
                Debug.LogError("[FullAnim] ✗ Hips/UpperLeg bones missing — cannot author Walk/Death. Aborting.");
                return;
            }

            // World axes in prefab-contents space (root is identity): X = right
            // (pitch axis), Y = up (yaw axis). Converted per-bone below.
            Vector3 right = Vector3.right, up = Vector3.up, down = Vector3.down;

            // ---- Idle (rebuilt, same as the proven test) ------------------ //
            var idleClip = new AnimationClip { frameRate = 30f, legacy = false };
            if (spine != null) AddBreathCurves(idleClip, Path(spine, model), spine.localRotation, new Vector3(1.6f, 0f, 0f));
            if (chest != null) AddBreathCurves(idleClip, Path(chest, model), chest.localRotation, new Vector3(1.2f, 0f, 0f));
            if (head  != null) AddSwayCurves(idleClip,  Path(head, model),  head.localRotation, 2.0f);
            FinishClip(idleClip, loop: true, IdleClipPath);

            // ---- Walk ----------------------------------------------------- //
            float T = WalkLoopSeconds;
            var walk = new AnimationClip { frameRate = 30f, legacy = false };

            // Legs: alternating pitch about WORLD right axis.
            SetQuatCurves(walk, Path(lUpLeg, model), new[] {
                (0f,      lUpLeg.localRotation * AxisDelta(lUpLeg, right,  WalkLegSwingDeg)),
                (T * .5f, lUpLeg.localRotation * AxisDelta(lUpLeg, right, -WalkLegSwingDeg)),
                (T,       lUpLeg.localRotation * AxisDelta(lUpLeg, right,  WalkLegSwingDeg)) });
            SetQuatCurves(walk, Path(rUpLeg, model), new[] {
                (0f,      rUpLeg.localRotation * AxisDelta(rUpLeg, right, -WalkLegSwingDeg)),
                (T * .5f, rUpLeg.localRotation * AxisDelta(rUpLeg, right,  WalkLegSwingDeg)),
                (T,       rUpLeg.localRotation * AxisDelta(rUpLeg, right, -WalkLegSwingDeg)) });

            // Knees: fold while the leg swings through (mid-phase per side).
            if (lLoLeg != null) SetQuatCurves(walk, Path(lLoLeg, model), new[] {
                (0f, lLoLeg.localRotation),
                (T * .25f, lLoLeg.localRotation * AxisDelta(lLoLeg, right, -WalkKneeBendDeg)),
                (T * .5f,  lLoLeg.localRotation),
                (T,        lLoLeg.localRotation) });
            if (rLoLeg != null) SetQuatCurves(walk, Path(rLoLeg, model), new[] {
                (0f, rLoLeg.localRotation),
                (T * .5f,  rLoLeg.localRotation),
                (T * .75f, rLoLeg.localRotation * AxisDelta(rLoLeg, right, -WalkKneeBendDeg)),
                (T,        rLoLeg.localRotation) });

            // Hips: vertical bob (highest at pass phases) + counter-yaw.
            Vector3 bob = ParentDelta(hips, Vector3.up * WalkHipsBobM);
            SetPosCurves(walk, Path(hips, model), new[] {
                (0f, hips.localPosition), (T * .25f, hips.localPosition + bob),
                (T * .5f, hips.localPosition), (T * .75f, hips.localPosition + bob),
                (T, hips.localPosition) });
            SetQuatCurves(walk, Path(hips, model), new[] {
                (0f,      hips.localRotation * AxisDelta(hips, up,  WalkHipsYawDeg)),
                (T * .5f, hips.localRotation * AxisDelta(hips, up, -WalkHipsYawDeg)),
                (T,       hips.localRotation * AxisDelta(hips, up,  WalkHipsYawDeg)) });
            // NO arm curves — hands stay welded to the rifle grip.
            FinishClip(walk, loop: true, WalkClipPath);

            // ---- Attack: recoil pulse, loops while engaged ---------------- //
            float A = AttackLoopSeconds;
            var atk = new AnimationClip { frameRate = 30f, legacy = false };
            if (chest != null) SetQuatCurves(atk, Path(chest, model), new[] {
                (0f, chest.localRotation),
                (A * .24f, chest.localRotation * AxisDelta(chest, right, -AttackRecoilDeg)),
                (A * .6f,  chest.localRotation * AxisDelta(chest, right, -AttackRecoilDeg * .3f)),
                (A,        chest.localRotation) });
            if (rUpArm != null) SetQuatCurves(atk, Path(rUpArm, model), new[] {
                (0f, rUpArm.localRotation),
                (A * .24f, rUpArm.localRotation * AxisDelta(rUpArm, right, -4f)),
                (A,        rUpArm.localRotation) });
            if (head != null) SetQuatCurves(atk, Path(head, model), new[] {
                (0f, head.localRotation),
                (A * .24f, head.localRotation * AxisDelta(head, right, -2f)),
                (A,        head.localRotation) });
            FinishClip(atk, loop: true, AttackClipPath); // loops while IsAttacking

            // ---- Death: buckle → pitch forward → drop → hold -------------- //
            float D = DeathSeconds;
            var death = new AnimationClip { frameRate = 30f, legacy = false };
            Vector3 drop = ParentDelta(hips, down * DeathHipsDropM);
            SetPosCurves(death, Path(hips, model), new[] {
                (0f, hips.localPosition),
                (D * .25f, hips.localPosition + drop * .25f),
                (D * .65f, hips.localPosition + drop * .9f),
                (D,        hips.localPosition + drop) });
            SetQuatCurves(death, Path(hips, model), new[] {
                (0f, hips.localRotation),
                (D * .25f, hips.localRotation * AxisDelta(hips, right, DeathHipsPitchDeg * .2f)),
                (D * .65f, hips.localRotation * AxisDelta(hips, right, DeathHipsPitchDeg * .85f)),
                (D,        hips.localRotation * AxisDelta(hips, right, DeathHipsPitchDeg)) });
            foreach (var (b, deg) in new[] { (lLoLeg, DeathKneeBendDeg), (rLoLeg, DeathKneeBendDeg) })
                if (b != null) SetQuatCurves(death, Path(b, model), new[] {
                    (0f, b.localRotation),
                    (D * .25f, b.localRotation * AxisDelta(b, right, deg)),
                    (D,        b.localRotation * AxisDelta(b, right, deg)) });
            foreach (var (b, deg) in new[] { (spine, DeathSpineFoldDeg), (chest, DeathSpineFoldDeg * .8f), (head, DeathSpineFoldDeg * .7f) })
                if (b != null) SetQuatCurves(death, Path(b, model), new[] {
                    (0f, b.localRotation),
                    (D * .65f, b.localRotation * AxisDelta(b, right, deg)),
                    (D,        b.localRotation * AxisDelta(b, right, deg)) });
            FinishClip(death, loop: false, DeathClipPath);

            Debug.Log("[FullAnim]   4 clips authored (Idle rebuilt, Walk 0.7s loop, Attack 0.5s loop-while-engaged, Death 1.2s clamp).");

            // ---- Controller ----------------------------------------------- //
            if (File.Exists(GenericCtrlPath)) AssetDatabase.DeleteAsset(GenericCtrlPath);
            AnimatorController ctrl = AnimatorController.CreateAnimatorControllerAtPath(GenericCtrlPath);
            ctrl.AddParameter(ParamSpeed,       AnimatorControllerParameterType.Float);
            ctrl.AddParameter(ParamIsAttacking, AnimatorControllerParameterType.Bool);
            ctrl.AddParameter(ParamAttack,      AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter(ParamDie,         AnimatorControllerParameterType.Trigger);

            AnimatorStateMachine sm = ctrl.layers[0].stateMachine;
            AnimatorState sIdle   = sm.AddState("Idle",   new Vector3(260,   0, 0));
            AnimatorState sWalk   = sm.AddState("Walk",   new Vector3(260, 130, 0));
            AnimatorState sAttack = sm.AddState("Attack", new Vector3(520,  60, 0));
            AnimatorState sDeath  = sm.AddState("Death",  new Vector3(520, 200, 0));
            sIdle.motion = idleClip; sWalk.motion = walk; sAttack.motion = atk; sDeath.motion = death;
            sm.defaultState = sIdle;

            var iw = sIdle.AddTransition(sWalk);
            iw.hasExitTime = false; iw.duration = 0.15f;
            iw.AddCondition(AnimatorConditionMode.Greater, WalkSpeedThreshold, ParamSpeed);

            var wi = sWalk.AddTransition(sIdle);
            wi.hasExitTime = false; wi.duration = 0.15f;
            wi.AddCondition(AnimatorConditionMode.Less, WalkSpeedThreshold, ParamSpeed);

            var aa = sm.AddAnyStateTransition(sAttack);
            aa.hasExitTime = false; aa.duration = 0.05f; aa.canTransitionToSelf = false;
            aa.AddCondition(AnimatorConditionMode.If, 0f, ParamAttack);

            // Attack loops while engaged; leaves when UnitCombat goes idle.
            var ai = sAttack.AddTransition(sIdle);
            ai.hasExitTime = false; ai.duration = 0.15f;
            ai.AddCondition(AnimatorConditionMode.IfNot, 0f, ParamIsAttacking);

            var ad = sm.AddAnyStateTransition(sDeath);
            ad.hasExitTime = false; ad.duration = 0.05f; ad.canTransitionToSelf = false;
            ad.AddCondition(AnimatorConditionMode.If, 0f, ParamDie);
            // Death: no outgoing transitions — clip clamps on its final pose.

            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            Debug.Log($"[FullAnim]   Controller created at '{GenericCtrlPath}'.");

            // ---- Prefab wiring -------------------------------------------- //
            Animator anim = model.GetComponent<Animator>();
            if (anim == null) anim = model.gameObject.AddComponent<Animator>();
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion           = false;
            anim.cullingMode               = AnimatorCullingMode.AlwaysAnimate;
            EditorUtility.SetDirty(anim);

            SoldierAnimator sa = root.GetComponent<SoldierAnimator>();
            bool added = false;
            if (sa == null) { sa = root.AddComponent<SoldierAnimator>(); added = true; }
            sa.animator = anim;   // EXPLICIT — auto-find would grab the legacy 'character' Animator
            EditorUtility.SetDirty(sa);
            Debug.Log($"[FullAnim]   Animator: controller=Soldier_Generic, rig stays Generic. " +
                      $"SoldierAnimator {(added ? "ADDED" : "already present")} with explicit animator reference.");

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[FullAnim] ✓ Done. Press Play: idle breathing → walk cycle on move → recoil while " +
                  "attacking → collapse on death. Tuning constants live at the top of this section " +
                  "(swing/bob/recoil/death angles). Rollback Mixamo Wiring restores the static state.");
        Debug.Log("[FullAnim] ─────────────────────────────────────────────");
    }

    // ---- Authoring helpers (axis-correct by construction) ------------- //

    private static string Path(Transform bone, Transform animRoot)
        => AnimationUtility.CalculateTransformPath(bone, animRoot);

    /// <summary>Rotation delta of <paramref name="deg"/> degrees about a WORLD
    /// axis, expressed in the bone's local frame (computed from the rest pose,
    /// valid in prefab-contents space where the root is identity).</summary>
    private static Quaternion AxisDelta(Transform bone, Vector3 worldAxis, float deg)
    {
        Vector3 localAxis = Quaternion.Inverse(bone.rotation) * worldAxis;
        return Quaternion.AngleAxis(deg, localAxis);
    }

    /// <summary>World-space positional delta converted into the bone's PARENT
    /// space (what localPosition curves are expressed in), scale-aware.</summary>
    private static Vector3 ParentDelta(Transform bone, Vector3 worldDelta)
        => bone.parent != null ? bone.parent.InverseTransformVector(worldDelta) : worldDelta;

    private static void SetPosCurves(AnimationClip clip, string path, (float t, Vector3 v)[] keys)
    {
        for (int c = 0; c < 3; c++)
        {
            var curve = new AnimationCurve();
            foreach (var (t, v) in keys)
                curve.AddKey(new Keyframe(t, c == 0 ? v.x : c == 1 ? v.y : v.z));
            clip.SetCurve(path, typeof(Transform), "localPosition." + "xyz"[c], curve);
        }
    }

    private static void FinishClip(AnimationClip clip, bool loop, string assetPath)
    {
        clip.EnsureQuaternionContinuity();
        var s = AnimationUtility.GetAnimationClipSettings(clip);
        s.loopTime = loop;
        AnimationUtility.SetAnimationClipSettings(clip, s);
        if (File.Exists(assetPath)) AssetDatabase.DeleteAsset(assetPath);
        AssetDatabase.CreateAsset(clip, assetPath);
    }

    // ================================================================== //
    // 2d. Replace Walk with Run — authored Generic run + in-place state swap
    //
    // The user's Soldier_Run.fbx is a Mixamo clip (mixamorig:* skeleton) and
    // cannot drive the Generic rig — same incompatibility as the other Mixamo
    // files. Per the user's approved fallback, the run is rebuilt in the same
    // authored-Generic style: faster cadence, larger leg swing, deeper knee
    // fold, forward torso lean. The controller is edited IN PLACE (Walk state
    // renamed to Run, motion swapped) so the prefab and every transition are
    // untouched; the Animator keeps the same controller reference.
    //
    // Soldier_Walk_Generic.anim stays on disk, simply unreferenced.
    // NOTE: re-running "Wire Full Soldier Animations" later would rebuild the
    // controller with Walk again — re-run this menu afterwards if you do.
    //
    // Menu: Tools → RTS → Units → Replace Walk With Run (Generic)
    // ================================================================== //

    private const string RunClipPath = "Assets/_Game/Animations/Soldier/Soldier_Run_Generic.anim";

    // Custom tactical-jog cycle, authored from scratch for the rifle soldier.
    // Gait phases (T = one full cycle, both steps):
    //   LEFT leg : contact-front at 0 → stance (sweeps back) over [0, T/2]
    //              → swing-through (forward) over [T/2, T].
    //   RIGHT leg: mirrored by T/2.
    // Anti-stiffness measures (vs. the earlier versions):
    //   • 5-key thigh profile — fast swing-through, decelerating into contact,
    //     instead of a symmetric 3-key sine.
    //   • Ankle dorsiflexion + small foot lift during swing — feet articulate
    //     instead of riding rigidly on the shins.
    //   • Staggered joint timing — knee fold peaks slightly BEFORE the ankle
    //     so the joints don't all hit their extremes on the same frame
    //     (the biggest "mechanical" tell).
    private const float RunCycleSeconds   = 0.45f;  // brisk tactical run
    private const float RunLegSwingDeg    = 45f;    // strong, unmistakable stride
    private const float RunKneeBaseDeg    = 12f;    // legs never fully lock
    private const float RunKneeFoldDeg    = 50f;    // deep recovery fold
    private const float RunAnkleLiftDeg   = 18f;    // toes-up during swing
    private const float RunFootLiftM      = 0.10f;  // clear world-up foot lift at swing mid
    private const float RunHipsBobM       = 0.06f;  // strong vertical bounce per step
    private const float RunHipsFwdShiftM  = 0.02f;  // slight fore/aft hip drive with the stride
    private const float RunHipsYawDeg     = 8f;     // visible counter-yaw
    private const float RunChestYawDeg    = 6f;     // chest counter-rotates vs hips — arms+rifle move as one unit
    private const float RunLeanDeg        = 10f;    // committed forward lean
    private const float RunLeanPulseDeg   = 3f;     // lean pulse per step — running impact

    // ---- Phase/direction correction toggles --------------------------- //
    // RunFlipForward (the real fix for "legs read reversed"): all sagittal
    // rotations (thigh swing, knee fold, ankle, lean, hip drive) were defined
    // assuming the model's visual forward is world +Z. If it's −Z, every
    // forward/back motion plays mirrored — swing reads as stance, knees fold
    // on the planted leg, lean goes backward. Flipping the pitch SIGN mirrors
    // the whole sagittal plane consistently. Default TRUE per the user's
    // report; set false if the flip overshoots.
    private const bool RunFlipForward   = true;
    // Torso lean sign, DECOUPLED from RunFlipForward. Empirical finding: with
    // RunFlipForward=true the legs read correctly but the spine/chest arched
    // BACKWARD — so the torso keeps the original (+1) sign while the legs use
    // the flipped one. Set to -1f if the torso ever leans backward again.
    private const float RunTorsoLeanSign = 1f;
    // Relabels which leg leads at t=0 (half-phase swap between L and R).
    // Does NOT change the perceived direction — only try if one specific leg
    // looks off against the other.
    private const bool RunSwapLeftRight = false;
    // Time-reverses the entire loop. Last resort — it inverts the perceived
    // travel direction but ALSO puts knee folds in the wrong phase, so prefer
    // RunFlipForward. Kept for experimentation.
    private const bool RunReverseCycle  = false;

    [MenuItem("Tools/RTS/Units/Replace Walk With Run (Generic)")]
    public static void ReplaceWalkWithRun()
    {
        Debug.Log("[RunSwap] ─── Replacing Walk with authored Generic Run ───");

        // ---- 1. Author the run clip from the prefab's rest pose --------- //
        //         Prefab is loaded READ-ONLY: we read bone transforms and
        //         unload WITHOUT saving — zero prefab modification.
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        AnimationClip run;
        try
        {
            Transform holder = root.transform.Find($"{VisualRootName}/{HolderName}");
            Transform model  = holder != null && holder.childCount > 0 ? holder.GetChild(0) : null;
            if (model == null)
            {
                Debug.LogError($"[RunSwap] ✗ '{VisualRootName}/{HolderName}/<model>' not found. Aborting.");
                return;
            }

            Transform hips   = FindBone(model, "Hips");
            Transform lUpLeg = FindBone(model, "LeftUpperLeg");
            Transform rUpLeg = FindBone(model, "RightUpperLeg");
            Transform lLoLeg = FindBone(model, "LeftLowerLeg");
            Transform rLoLeg = FindBone(model, "RightLowerLeg");
            Transform lFoot  = FindBone(model, "LeftFoot");
            Transform rFoot  = FindBone(model, "RightFoot");
            Transform spine  = FindBone(model, "Spine");
            Transform chest  = FindBone(model, "Chest");
            if (hips == null || lUpLeg == null || rUpLeg == null)
            {
                Debug.LogError("[RunSwap] ✗ Hips/UpperLeg bones missing — cannot author Run. Aborting.");
                return;
            }

            Vector3 right = Vector3.right, up = Vector3.up;
            float T = RunCycleSeconds;
            run = new AnimationClip { frameRate = 30f, legacy = false };

            // Sagittal sign — flips thigh swing / knee fold / ankle / lean /
            // hip drive together when the model's visual forward is world −Z.
            float fs = RunFlipForward ? -1f : 1f;
            // Optional whole-loop time reversal (keys re-sorted by AddKey).
            float Ph(float t) => RunReverseCycle ? T - t : t;

            float S = RunLegSwingDeg * fs;

            // Per-leg authoring. phaseA = contact-front at t0, stance over
            // [0, T/2], swing over [T/2, T] (knee folds .70T, ankle/lift .75T).
            // phaseB is the same gait shifted by half a cycle.
            void AuthorLeg(Transform thigh, Transform shin, Transform foot, bool phaseA)
            {
                Quaternion Th(float deg) => thigh.localRotation * AxisDelta(thigh, right, deg);
                if (phaseA)
                    SetQuatCurves(run, Path(thigh, model), new[] {
                        (Ph(0f),       Th( S)),
                        (Ph(T * .25f), Th( S * .15f)),   // mid-stance pass
                        (Ph(T * .50f), Th(-S)),          // toe-off, fully back
                        (Ph(T * .78f), Th( S * .10f)),   // fast swing-through
                        (Ph(T),        Th( S)) });       // decelerate into contact
                else
                    SetQuatCurves(run, Path(thigh, model), new[] {
                        (Ph(0f),       Th(-S)),
                        (Ph(T * .28f), Th( S * .10f)),
                        (Ph(T * .50f), Th( S)),
                        (Ph(T * .75f), Th( S * .15f)),
                        (Ph(T),        Th(-S)) });

                if (shin != null)
                {
                    // Knee fold direction flips with the facing sign too.
                    Quaternion kBase = shin.localRotation * AxisDelta(shin, right, -RunKneeBaseDeg * fs);
                    Quaternion kFold = shin.localRotation * AxisDelta(shin, right, -(RunKneeBaseDeg + RunKneeFoldDeg) * fs);
                    if (phaseA)
                        SetQuatCurves(run, Path(shin, model), new[] {
                            (Ph(0f), kBase), (Ph(T * .5f), kBase), (Ph(T * .70f), kFold),
                            (Ph(T * .92f), kBase), (Ph(T), kBase) });
                    else
                        SetQuatCurves(run, Path(shin, model), new[] {
                            (Ph(0f), kBase), (Ph(T * .20f), kFold), (Ph(T * .42f), kBase),
                            (Ph(T * .5f), kBase), (Ph(T), kBase) });
                }

                if (foot != null)
                {
                    float swingMid = phaseA ? T * .75f : T * .25f;
                    Quaternion flex = foot.localRotation * AxisDelta(foot, right, -RunAnkleLiftDeg * fs);
                    SetQuatCurves(run, Path(foot, model), new[] {
                        (Ph(0f), foot.localRotation),
                        (Ph(phaseA ? T * .5f : T * .45f), foot.localRotation),
                        (Ph(swingMid), flex),
                        (Ph(T), foot.localRotation) });
                    Vector3 lift = ParentDelta(foot, Vector3.up * RunFootLiftM);
                    SetPosCurves(run, Path(foot, model), new[] {
                        (Ph(0f), foot.localPosition),
                        (Ph(phaseA ? T * .5f : T * .45f), foot.localPosition),
                        (Ph(swingMid), foot.localPosition + lift),
                        (Ph(T), foot.localPosition) });
                }
            }

            bool leftIsA = !RunSwapLeftRight;
            AuthorLeg(lUpLeg, lLoLeg, lFoot,  leftIsA);
            AuthorLeg(rUpLeg, rLoLeg, rFoot, !leftIsA);

            // Hips — vertical bob (one bounce per step) + slight fore/aft drive
            // with the stride + counter-yaw. Drive direction follows fs.
            Vector3 bob = ParentDelta(hips, Vector3.up * RunHipsBobM);
            Vector3 fwd = ParentDelta(hips, Vector3.forward * RunHipsFwdShiftM * fs);
            SetPosCurves(run, Path(hips, model), new[] {
                (Ph(0f),       hips.localPosition + fwd),
                (Ph(T * .25f), hips.localPosition + bob - fwd),
                (Ph(T * .5f),  hips.localPosition + fwd),
                (Ph(T * .75f), hips.localPosition + bob - fwd),
                (Ph(T),        hips.localPosition + fwd) });
            SetQuatCurves(run, Path(hips, model), new[] {
                (Ph(0f),      hips.localRotation * AxisDelta(hips, up,  RunHipsYawDeg)),
                (Ph(T * .5f), hips.localRotation * AxisDelta(hips, up, -RunHipsYawDeg)),
                (Ph(T),       hips.localRotation * AxisDelta(hips, up,  RunHipsYawDeg)) });

            // Spine — forward lean with a per-step pulse. Uses the DECOUPLED
            // torso sign (NOT fs): with fs flipped for the legs, the torso
            // arched backward, so it keeps its own empirically-correct sign.
            float ts = RunTorsoLeanSign;
            if (spine != null)
            {
                Quaternion leanHi = spine.localRotation * AxisDelta(spine, right, (RunLeanDeg + RunLeanPulseDeg) * ts);
                Quaternion leanLo = spine.localRotation * AxisDelta(spine, right, (RunLeanDeg - RunLeanPulseDeg) * ts);
                SetQuatCurves(run, Path(spine, model), new[] {
                    (Ph(0f), leanHi), (Ph(T * .25f), leanLo), (Ph(T * .5f), leanHi),
                    (Ph(T * .75f), leanLo), (Ph(T), leanHi) });
            }

            // Chest — half-lean (torso sign) + counter-yaw OPPOSITE the hips.
            // Both arms and the rifle hang off the chest, so they rotate
            // together as one unit: zero grip separation.
            if (chest != null)
            {
                Quaternion cl = chest.localRotation * AxisDelta(chest, right, RunLeanDeg * .5f * ts);
                SetQuatCurves(run, Path(chest, model), new[] {
                    (Ph(0f),      cl * AxisDelta(chest, up, -RunChestYawDeg)),
                    (Ph(T * .5f), cl * AxisDelta(chest, up,  RunChestYawDeg)),
                    (Ph(T),       cl * AxisDelta(chest, up, -RunChestYawDeg)) });
            }
            // NO arm curves — rifle grip stays welded; FirePoint rides the rifle.

            FinishClip(run, loop: true, RunClipPath);
            Debug.Log($"[RunSwap]   Custom Run authored at '{RunClipPath}' ({RunCycleSeconds:F2}s loop): " +
                      $"stride ±{RunLegSwingDeg}° (5-key snap profile), knee {RunKneeBaseDeg}°+{RunKneeFoldDeg}° " +
                      $"fold on swing, ankle lift {RunAnkleLiftDeg}° + {RunFootLiftM * 100f:F0}cm, " +
                      $"bob {RunHipsBobM * 100f:F1}cm, hips yaw ±{RunHipsYawDeg}°, chest yaw ±{RunChestYawDeg}°, " +
                      $"lean {RunLeanDeg}°±{RunLeanPulseDeg}°.  " +
                      $"Phase toggles: FlipForward={RunFlipForward}, SwapLeftRight={RunSwapLeftRight}, " +
                      $"ReverseCycle={RunReverseCycle}.");
        }
        finally
        {
            // Read-only: deliberately NOT saved.
            PrefabUtility.UnloadPrefabContents(root);
        }

        // ---- 2. In-place controller edit: Walk → Run --------------------- //
        AnimatorController ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(GenericCtrlPath);
        if (ctrl == null)
        {
            Debug.LogError($"[RunSwap] ✗ Controller missing at '{GenericCtrlPath}' — run " +
                           "Wire Full Soldier Animations first.");
            return;
        }

        AnimatorState moveState = null;
        foreach (var cs in ctrl.layers[0].stateMachine.states)
        {
            if (cs.state.name == "Walk" || cs.state.name == "Run") { moveState = cs.state; break; }
        }
        if (moveState == null)
        {
            Debug.LogError("[RunSwap] ✗ Neither 'Walk' nor 'Run' state found in the controller. Aborting.");
            return;
        }

        string before = moveState.name;
        moveState.name   = "Run";
        moveState.motion = AssetDatabase.LoadAssetAtPath<AnimationClip>(RunClipPath);
        EditorUtility.SetDirty(moveState);
        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();

        Debug.Log($"[RunSwap]   Controller state '{before}' → 'Run', motion = Soldier_Run_Generic. " +
                  "All transitions (Idle⇄Run on Speed, AnyState→Attack, AnyState→Death) reference the " +
                  "same state object — untouched. Parameters Speed/IsAttacking/Attack/Die unchanged. " +
                  "Prefab NOT modified (same controller asset, same GUID). " +
                  "Soldier_Walk_Generic.anim kept on disk, unreferenced.");
        Debug.Log("[RunSwap] ✓ Done. Press Play: move a soldier — he should RUN (faster cycle, forward lean).");
        Debug.Log("[RunSwap] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // 2e. Preview Soldier Run Pose Frames — definitive binding diagnostic
    //
    // Answers "is the Run clip ACTUALLY driving the real bones?" with hard
    // numbers instead of guesses:
    //   A. Verifies the controller's Run state motion is Soldier_Run_Generic
    //      and that no state references Soldier_Walk_Generic.
    //   B. Verifies the prefab's Animator references Soldier_Generic.controller.
    //   C. Lists every transform path animated by the run clip.
    //   D. Instantiates the prefab into a temp object, samples the clip at
    //      0 / 25 / 50 / 75 / 100 % via AnimationClip.SampleAnimation, and
    //      logs each key bone's rotation delta from rest in degrees.
    //      Deltas ≈ 0 on the legs would prove a path/binding bug;
    //      big deltas prove the clip is fine and any remaining stiffness is
    //      a play-mode wiring issue (wrong controller/Animator at runtime).
    //
    // Read-only — instantiates a temp copy and destroys it; nothing saved.
    //
    // Menu: Tools → RTS → Units → Preview Soldier Run Pose Frames
    // ================================================================== //

    private static readonly string[] DiagBones =
    {
        "Hips", "Spine", "Chest",
        "LeftUpperLeg", "LeftLowerLeg", "LeftFoot",
        "RightUpperLeg", "RightLowerLeg", "RightFoot",
    };

    [MenuItem("Tools/RTS/Units/Preview Soldier Run Pose Frames")]
    public static void PreviewRunPoseFrames()
    {
        Debug.Log("[RunPreview] ─── Run clip binding diagnostic ───");

        // ---- A. Controller state → clip --------------------------------- //
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(GenericCtrlPath);
        if (ctrl == null) { Debug.LogError($"[RunPreview] ✗ Controller missing at '{GenericCtrlPath}'."); return; }

        bool walkReferenced = false;
        foreach (var cs in ctrl.layers[0].stateMachine.states)
        {
            string motionPath = cs.state.motion != null ? AssetDatabase.GetAssetPath(cs.state.motion) : "<null>";
            Debug.Log($"[RunPreview]   state '{cs.state.name}' → motion '{(cs.state.motion != null ? cs.state.motion.name : "<null>")}'  ({motionPath})");
            if (motionPath.EndsWith("Soldier_Walk_Generic.anim")) walkReferenced = true;
        }
        AnimatorState runState = null;
        foreach (var cs in ctrl.layers[0].stateMachine.states)
            if (cs.state.name == "Run") { runState = cs.state; break; }

        bool runUsesClip = runState != null && runState.motion != null &&
                           AssetDatabase.GetAssetPath(runState.motion) == RunClipPath;
        Debug.Log($"[RunPreview]   ► Run state uses Soldier_Run_Generic.anim: {(runUsesClip ? "YES" : "NO — FIX NEEDED (re-run Replace Walk With Run)")}");
        Debug.Log($"[RunPreview]   ► Soldier_Walk_Generic referenced anywhere: {(walkReferenced ? "YES — should be NO" : "no (correct)")}");

        // ---- B. Prefab Animator → controller ----------------------------- //
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        Transform pHolder = prefab != null ? prefab.transform.Find($"{VisualRootName}/{HolderName}") : null;
        Transform pModel  = pHolder != null && pHolder.childCount > 0 ? pHolder.GetChild(0) : null;
        Animator pAnim    = pModel != null ? pModel.GetComponent<Animator>() : null;
        Debug.Log($"[RunPreview]   ► Prefab Animator controller: " +
                  $"'{(pAnim != null && pAnim.runtimeAnimatorController != null ? pAnim.runtimeAnimatorController.name : "<null>")}'" +
                  $"  (expected 'Soldier_Generic'). Animator enabled={(pAnim != null ? pAnim.enabled.ToString() : "?")}");

        // ---- C. Clip curve bindings -------------------------------------- //
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(RunClipPath);
        if (clip == null) { Debug.LogError($"[RunPreview] ✗ Clip missing at '{RunClipPath}'."); return; }

        var pathSet = new HashSet<string>();
        foreach (var b in AnimationUtility.GetCurveBindings(clip)) pathSet.Add(b.path);
        Debug.Log($"[RunPreview]   Clip '{clip.name}': length={clip.length:F3}s, loop={clip.isLooping}, " +
                  $"{pathSet.Count} animated transform path(s):");
        foreach (string p in pathSet.OrderBy(x => x)) Debug.Log($"[RunPreview]     • '{p}'");

        // ---- D. Sample poses on a temp instance --------------------------- //
        GameObject temp = Object.Instantiate(prefab);
        temp.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            Transform tHolder = temp.transform.Find($"{VisualRootName}/{HolderName}");
            Transform tModel  = tHolder != null && tHolder.childCount > 0 ? tHolder.GetChild(0) : null;
            if (tModel == null) { Debug.LogError("[RunPreview] ✗ Model not found in temp instance."); return; }

            // Rest rotations before any sampling.
            var rest = new Dictionary<string, Quaternion>();
            var bones = new Dictionary<string, Transform>();
            foreach (string n in DiagBones)
            {
                Transform b = null;
                foreach (Transform t in tModel.GetComponentsInChildren<Transform>(true))
                    if (t.name == n) { b = t; break; }
                if (b == null) { Debug.LogWarning($"[RunPreview]   ⚠ bone '{n}' not found in instance."); continue; }
                bones[n] = b; rest[n] = b.localRotation;
            }

            var maxDelta = new Dictionary<string, float>();
            foreach (float pct in new[] { 0f, .25f, .5f, .75f, 1f })
            {
                clip.SampleAnimation(tModel.gameObject, clip.length * pct);
                var line = new System.Text.StringBuilder($"[RunPreview]   t={pct * 100f:F0}%  ");
                foreach (string n in DiagBones)
                {
                    if (!bones.ContainsKey(n)) continue;
                    float d = Quaternion.Angle(rest[n], bones[n].localRotation);
                    if (!maxDelta.ContainsKey(n) || d > maxDelta[n]) maxDelta[n] = d;
                    line.Append($"{n}={d:F0}°  ");
                }
                Debug.Log(line.ToString());
            }

            Debug.Log("[RunPreview]   ── Max rotation delta from rest per bone ──");
            int moving = 0, frozen = 0;
            foreach (string n in DiagBones)
            {
                if (!maxDelta.ContainsKey(n)) continue;
                bool ok = maxDelta[n] > 3f;
                if (ok) moving++; else frozen++;
                Debug.Log($"[RunPreview]     {(ok ? "✓" : "✗")} {n}: {maxDelta[n]:F1}°  {(ok ? "" : "← NOT MOVING (binding problem if unexpected)")}");
            }
            Debug.Log(frozen == 0
                ? $"[RunPreview] ✓ All {moving} key bones are driven by the clip. If the run still looks " +
                  "static in Play Mode, the problem is play-time wiring (check the Animator window while " +
                  "a soldier moves — current state should read 'Run')."
                : $"[RunPreview] ✗ {frozen} bone(s) not driven — path/binding bug. Re-run " +
                  "Replace Walk With Run (Generic) so curves are re-authored from the current hierarchy.");
        }
        finally
        {
            Object.DestroyImmediate(temp);
        }

        Debug.Log("[RunPreview] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // 3. Rollback — restore the last visible (pre-Mixamo) soldier state
    //
    // The Mixamo wiring changed exactly three things; this menu reverts
    // exactly those three and NOTHING else:
    //   1. Soldier_Rigged.fbx rig: Humanoid → Generic (avatar regenerated
    //      as the original Generic 'Soldier_RiggedAvatar').
    //   2. Animator on the model: runtimeAnimatorController → null,
    //      avatar → the regenerated Generic avatar, applyRootMotion stays false.
    //   3. SoldierAnimator component removed from the prefab root.
    //
    // Deliberately preserved (verified on disk to be the pre-wiring values):
    //   • globalScale = 100 on the FBX importer.
    //   • Soldier_Rigged localPosition (0, -0.1, 0) — ground fix.
    //   • Soldier_Rigged localRotation (-90, 0, 0) — stand-up fix.
    //   • Soldier_Rigged localScale AS FOUND (currently 1,1,1 — the user's
    //     tuned height after the globalScale fix; NOT reset to 1.3).
    //   • LODGroup size = 2.0, localReferencePoint (0, 0.95, 0).
    //   • Materials, M_TeamColor, FirePoint, gameplay scripts.
    //   • The four Mixamo FBXs (separate assets; their rig type has zero
    //     effect on the soldier's visibility) and the Soldier_Mixamo
    //     controller file (kept on disk, simply unreferenced).
    //
    // Menu: Tools → RTS → Units → Rollback Mixamo Wiring (Restore Soldier)
    // ================================================================== //

    [MenuItem("Tools/RTS/Units/Rollback Mixamo Wiring (Restore Soldier)")]
    public static void Rollback()
    {
        Debug.Log("[RollbackMixamo] ─── Restoring pre-Mixamo visible soldier ───");

        // ---- 1. FBX rig back to Generic ---------------------------------- //
        var rmi = (ModelImporter)AssetImporter.GetAtPath(RiggedFbxPath);
        if (rmi == null)
        {
            Debug.LogError($"[RollbackMixamo] ✗ Cannot get ModelImporter for '{RiggedFbxPath}'.");
            return;
        }

        Debug.Log($"[RollbackMixamo]   FBX before: animationType={rmi.animationType}, " +
                  $"globalScale={rmi.globalScale} (globalScale is NOT touched).");
        if (rmi.animationType != ModelImporterAnimationType.Generic)
        {
            rmi.animationType = ModelImporterAnimationType.Generic;
            rmi.avatarSetup   = ModelImporterAvatarSetup.CreateFromThisModel;
            rmi.SaveAndReimport();
            Debug.Log("[RollbackMixamo]   Soldier_Rigged.fbx → Generic (avatar regenerated).");
        }
        else
        {
            Debug.Log("[RollbackMixamo]   FBX already Generic — skipped reimport.");
        }

        Avatar genericAvatar = AssetDatabase.LoadAllAssetsAtPath(RiggedFbxPath)
                                            .OfType<Avatar>().FirstOrDefault();
        Debug.Log($"[RollbackMixamo]   Generic avatar: '{(genericAvatar != null ? genericAvatar.name : "<null>")}'  " +
                  $"valid={(genericAvatar != null && genericAvatar.isValid)}  " +
                  $"isHuman={(genericAvatar != null && genericAvatar.isHuman)} (should be False).");

        // ---- 2 + 3. Prefab: clear controller, remove SoldierAnimator ----- //
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            Transform visualRoot = root.transform.Find(VisualRootName);
            Transform holder     = visualRoot != null ? visualRoot.Find(HolderName) : null;
            Transform newModel   = holder != null && holder.childCount > 0 ? holder.GetChild(0) : null;
            if (newModel == null)
            {
                Debug.LogError($"[RollbackMixamo] ✗ '{VisualRootName}/{HolderName}/<model>' not found. Aborting.");
                return;
            }

            Animator anim = newModel.GetComponent<Animator>();
            if (anim != null)
            {
                anim.runtimeAnimatorController = null;
                anim.avatar                    = genericAvatar;
                anim.applyRootMotion           = false;
                EditorUtility.SetDirty(anim);
                Debug.Log("[RollbackMixamo]   Animator: controller=null, avatar=Generic, applyRootMotion=false.");
            }

            SoldierAnimator sa = root.GetComponent<SoldierAnimator>();
            if (sa != null)
            {
                Object.DestroyImmediate(sa, true);
                Debug.Log("[RollbackMixamo]   SoldierAnimator removed from prefab root.");
            }
            else
            {
                Debug.Log("[RollbackMixamo]   SoldierAnimator not present — nothing to remove.");
            }

            // ---- Defensive re-assert of the known-good visual state ------ //
            // Scale is intentionally left AS FOUND (the user's tuned height).
            Vector3 pos = newModel.localPosition;
            Vector3 rot = newModel.localEulerAngles;
            Debug.Log($"[RollbackMixamo]   Visual state kept: localPosition={pos}  " +
                      $"localEulerAngles={rot}  localScale={newModel.localScale}.");

            LODGroup lod = newModel.GetComponent<LODGroup>();
            if (lod != null && lod.size < 1f)
            {
                lod.size                = 2.0f;
                lod.localReferencePoint = new Vector3(0f, 0.95f, 0f);
                EditorUtility.SetDirty(lod);
                Debug.Log("[RollbackMixamo]   LODGroup size had collapsed — restored to 2.0 / (0, 0.95, 0).");
            }
            else if (lod != null)
            {
                Debug.Log($"[RollbackMixamo]   LODGroup OK: size={lod.size:F2}, ref={lod.localReferencePoint}.");
            }

            foreach (SkinnedMeshRenderer smr in newModel.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!smr.enabled) { smr.enabled = true; EditorUtility.SetDirty(smr); }
                if (!smr.gameObject.activeSelf) smr.gameObject.SetActive(true);
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[RollbackMixamo] ✓ Done. Soldier restored to the static (no-animation) visible state. " +
                  "Press Play and produce a soldier to confirm. The Mixamo FBXs / controller file remain " +
                  "on disk unreferenced — no further animation wiring will run unless you ask.");
        Debug.Log("[RollbackMixamo] ─────────────────────────────────────────────");
    }

    // ================================================================== //
    // 4. Orientation contingency toggle
    // ================================================================== //

    [MenuItem("Tools/RTS/Units/Toggle Soldier Visual Rotation Fix")]
    public static void ToggleRotationFix()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            Transform holder = root.transform.Find($"{VisualRootName}/{HolderName}");
            Transform model  = holder != null && holder.childCount > 0 ? holder.GetChild(0) : null;
            if (model == null)
            {
                Debug.LogError("[ToggleSoldierRot] ✗ Model not found in prefab.");
                return;
            }

            // Flip between the stand-up fix (-90,0,0) and identity (0,0,0).
            bool hasFix = Quaternion.Angle(model.localRotation, Quaternion.Euler(-90f, 0f, 0f)) < 1f;
            Vector3 target = hasFix ? Vector3.zero : new Vector3(-90f, 0f, 0f);
            Vector3 before = model.localEulerAngles;
            model.localRotation = Quaternion.Euler(target);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ToggleSoldierRot] ✓ '{model.name}'.localEulerAngles {before} → {target}. " +
                      "Re-test in Play Mode; run again to flip back.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
