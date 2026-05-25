using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Builds a real AnimatorController at
///   Assets/_Game/Animations/SoldierAnimatorController_REAL.controller
///
/// Menu: Tools → RTS → Setup → Setup Soldier Animator Controller
///
/// Why this exists:
///   Earlier in this project, "SoldierAnimatorController_REAL" was accidentally
///   created as a FOLDER, not a Controller asset. This tool creates the real
///   .controller next to that folder (the folder is left in place — the rule
///   is "do not delete anything"), wires up the three required parameters
///   (Speed float, Attack trigger, Die trigger), and assigns the first
///   AnimationClip found inside the Mixamo FBX as the default "Idle" state.
///
/// What it does NOT do:
///   • Modify the Mixamo FBX import settings (the user does that manually —
///     see the setup summary).
///   • Touch the existing empty folder asset.
///   • Assign the controller to any Animator in the scene. The user drags
///     the resulting .controller asset onto the character's Animator field.
/// </summary>
public static class SetupSoldierAnimator
{
    // ------------------------------------------------------------------ //
    // Asset paths
    // ------------------------------------------------------------------ //

    private const string AnimationsFolder  = "Assets/_Game/Animations";
    private const string MixamoIdleFbxName = "Ch15_nonPBR@Rifle Idle";
    private const string ClipNamePrimary   = "Rifle Idle";
    private const string ClipNameFallback  = "Rifle_Idle";

    // The folder named SoldierAnimatorController_REAL (without extension) is
    // the broken asset left over from the previous attempt. We do NOT delete
    // it — we just create the real Controller next to it with the same base
    // name + .controller extension. They live side by side as different assets.
    private const string ControllerPath =
        "Assets/_Game/Animations/SoldierAnimatorController_REAL.controller";

    // ------------------------------------------------------------------ //
    // Entry
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Setup/Setup Soldier Animator Controller")]
    public static void Run()
    {
        Debug.Log("[SetupSoldierAnimator] ── Building SoldierAnimatorController_REAL.controller ──");

        if (!AssetDatabase.IsValidFolder(AnimationsFolder))
        {
            Debug.LogError($"[SetupSoldierAnimator] ✗ Folder '{AnimationsFolder}' does not exist.");
            return;
        }

        // 1. Diagnose what's currently at the target path.
        string priorFolder = AnimationsFolder + "/SoldierAnimatorController_REAL";
        if (AssetDatabase.IsValidFolder(priorFolder))
        {
            Debug.LogWarning("[SetupSoldierAnimator]   ⚠ Existing FOLDER detected at " +
                             $"'{priorFolder}' — left untouched. Creating the real Controller " +
                             "asset as a sibling (different extension).");
        }

        // 2. Find the Mixamo idle clip BEFORE creating the controller — so we
        //    don't end up with an empty controller pointing at nothing.
        AnimationClip idleClip = FindIdleClip();
        if (idleClip == null)
        {
            Debug.LogError($"[SetupSoldierAnimator] ✗ No AnimationClip named '{ClipNamePrimary}' " +
                           $"or '{ClipNameFallback}' found inside " +
                           $"'{AnimationsFolder}/{MixamoIdleFbxName}.fbx'. " +
                           "Make sure the FBX is imported and its Animation tab shows the clip.");
            return;
        }

        // 3. Create / refresh the controller.
        AnimatorController controller =
            AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);

        if (controller == null)
        {
            controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            Debug.Log($"[SetupSoldierAnimator]   ✓ Created controller at {ControllerPath}");
        }
        else
        {
            Debug.Log($"[SetupSoldierAnimator]   = Controller already exists at {ControllerPath} — refreshing.");
        }

        // 4. Parameters — idempotent: only add if missing.
        EnsureParameter(controller, "Speed",  AnimatorControllerParameterType.Float);
        EnsureParameter(controller, "Attack", AnimatorControllerParameterType.Trigger);
        EnsureParameter(controller, "Die",    AnimatorControllerParameterType.Trigger);

        // 5. Default state in the base layer — "Idle" pointing at the Mixamo clip.
        AnimatorControllerLayer baseLayer = controller.layers[0];
        AnimatorStateMachine sm           = baseLayer.stateMachine;

        AnimatorState idleState = null;
        foreach (ChildAnimatorState s in sm.states)
        {
            if (s.state != null && s.state.name == "Idle") { idleState = s.state; break; }
        }
        if (idleState == null)
        {
            idleState = sm.AddState("Idle");
            Debug.Log("[SetupSoldierAnimator]   ✓ Added Idle state.");
        }
        idleState.motion = idleClip;
        sm.defaultState = idleState;

        // Persist any in-memory edits so the layer/parameter changes survive the next reload.
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("[SetupSoldierAnimator] ✓ Done.\n" +
                  $"  Controller : {ControllerPath}\n" +
                  $"  Default state : Idle → '{idleClip.name}'\n" +
                  "  Parameters : Speed (Float), Attack (Trigger), Die (Trigger)\n" +
                  "  Next: assign this controller to the Animator on " +
                  "SoldierPrefab/SoldierVisualRoot/character (see setup summary).");
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static void EnsureParameter(AnimatorController controller, string name,
                                        AnimatorControllerParameterType type)
    {
        foreach (AnimatorControllerParameter p in controller.parameters)
        {
            if (p.name == name)
            {
                if (p.type != type)
                {
                    Debug.LogWarning($"[SetupSoldierAnimator]   ⚠ Parameter '{name}' exists but " +
                                     $"is of type {p.type} (expected {type}). Leaving as-is — fix manually if wrong.");
                }
                return;
            }
        }

        controller.AddParameter(name, type);
        Debug.Log($"[SetupSoldierAnimator]   ✓ Added parameter '{name}' ({type}).");
    }

    /// <summary>
    /// Loads the Mixamo idle FBX and returns the first AnimationClip whose
    /// name matches "Rifle Idle" or "Rifle_Idle". Skips Unity's hidden
    /// __preview__ clips that sometimes appear in LoadAllAssetsAtPath results.
    /// </summary>
    private static AnimationClip FindIdleClip()
    {
        string fbxPath = $"{AnimationsFolder}/{MixamoIdleFbxName}.fbx";
        if (!File.Exists(fbxPath))
        {
            Debug.LogError($"[SetupSoldierAnimator] ✗ FBX not found at '{fbxPath}'.");
            return null;
        }

        Object[] subs = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
        AnimationClip first = null;

        foreach (Object o in subs)
        {
            AnimationClip clip = o as AnimationClip;
            if (clip == null) continue;
            if (clip.name.StartsWith("__preview__")) continue;

            if (clip.name == ClipNamePrimary || clip.name == ClipNameFallback)
                return clip;
            if (first == null) first = clip;
        }

        if (first != null)
        {
            Debug.LogWarning($"[SetupSoldierAnimator]   ⚠ Clip '{ClipNamePrimary}' not found by name — " +
                             $"falling back to first clip in FBX: '{first.name}'.");
        }
        return first;
    }
}
