"""
Blender Python — author + export Generic RTS animations for Soldier_Rigged.

This script authors four Actions on the existing Armature in
Soldier_Unity_Final.blend and exports each as a Generic FBX with the
@<clip-name> Mecanim convention so Unity treats them as animation-only
clips that target the Soldier_Rigged Avatar.

OUTPUTS (created relative to this script's folder, walking up to Assets/):
    <project>/Assets/_Game/Animations/Soldier/Soldier_Rigged@Idle.fbx
    <project>/Assets/_Game/Animations/Soldier/Soldier_Rigged@Walk.fbx
    <project>/Assets/_Game/Animations/Soldier/Soldier_Rigged@Attack.fbx
    <project>/Assets/_Game/Animations/Soldier/Soldier_Rigged@Death.fbx

HOW TO RUN (pick one):
    GUI:
        1. Open Soldier_Unity_Final.blend in Blender.
        2. Switch to the "Scripting" workspace tab.
        3. Open this file (Text > Open) and click "Run Script".
        4. Watch the system console (Window > Toggle System Console) for
           per-step logs. Then preview each Action in the Dope Sheet
           > Action Editor (pick from the dropdown).
        5. If something looks wrong, see TUNING NOTES below and re-run.

    Headless (faster iteration, but no viewport preview):
        blender Soldier_Unity_Final.blend ^
                --background ^
                --python author_soldier_anims.py

WHAT THIS SCRIPT DOES NOT TOUCH
    • The mesh, materials, UVs, weights, textures — read-only.
    • The Armature bind pose (the FBX bind pose stays the combat stance).
    • The .blend on disk — the script does NOT call bpy.ops.wm.save_mainfile.
      Preview the actions, then save manually (Ctrl+S) if you want them
      persisted in the .blend. Closing without saving rolls back; only the
      exported FBX files persist.
    • SM_Weapon (the rifle). It is bone-parented to RightHand; we do NOT
      keyframe its TRS, so the rifle inherits the hand bone automatically
      and FirePoint stays at the barrel.

TUNING NOTES — the values that may need to flip for your rig
    Bone local axes vary between rigs. If after running the script:
      • Legs swing sideways instead of forward → swap WALK_LEG_AXIS to 2 (Z).
      • Soldier breathes backward instead of inflating → flip the sign of
        IDLE_SPINE_BREATH_DEG and IDLE_CHEST_BREATH_DEG.
      • Death falls backward instead of forward → flip the sign of
        DEATH_HIPS_FALL_DEG and DEATH_SPINE_FOLD_DEG.
      • Aim/recoil tilts the wrong way → flip ATTACK_CHEST_RECOIL_DEG.
    Re-run the script after edits.

BONE NAMES
    Pulled from the rig per README §3:
        Hips, Spine, Chest, Neck, Head
        LeftShoulder, LeftUpperArm, LeftLowerArm, LeftHand
        RightShoulder, RightUpperArm, RightLowerArm, RightHand
        LeftUpperLeg, LeftLowerLeg, LeftFoot
        RightUpperLeg, RightLowerLeg, RightFoot
    If your armature uses different names (Mixamo-style "LeftUpLeg" etc.)
    edit the constants at the top of each build_<clip>() function — the
    helpers log a clear MISSING line per absent bone so you'll see what
    needs renaming on first run.
"""

import os
import math
import bpy
from mathutils import Euler

# ---------------------------------------------------------------------------- #
# Configuration — frame rate, action frame ranges, output paths
# ---------------------------------------------------------------------------- #

FPS = 30
bpy.context.scene.render.fps = FPS

# Each clip's frame range. Frame 1 = first authored frame.
IDLE_START,   IDLE_END   = 1, 90    # 3.0 s loop
WALK_START,   WALK_END   = 1, 21    # 0.66 s loop (one cycle: L contact → R contact → L contact)
ATTACK_START, ATTACK_END = 1, 15    # 0.5 s one-shot
DEATH_START,  DEATH_END  = 1, 36    # 1.2 s one-shot

ARMATURE_NAME = "Armature"

# Where the FBX clips land. The script walks up from this .py's folder
# (Assets/_Game/Art/Soldier/Soldier_Unity_Final) to the Unity project root and
# drops the exports into Assets/_Game/Animations/Soldier/.
SCRIPT_DIR    = os.path.dirname(bpy.data.filepath) if bpy.data.filepath else os.getcwd()
PROJECT_ROOT  = os.path.normpath(os.path.join(SCRIPT_DIR, "..", "..", "..", "..", ".."))
OUTPUT_DIR    = os.path.join(PROJECT_ROOT, "Assets", "_Game", "Animations", "Soldier")
os.makedirs(OUTPUT_DIR, exist_ok=True)

print(f"[author_soldier_anims] script dir : {SCRIPT_DIR}")
print(f"[author_soldier_anims] project    : {PROJECT_ROOT}")
print(f"[author_soldier_anims] output dir : {OUTPUT_DIR}")

# Tunable constants used by the build_<clip>() functions. Adjust here if
# motion direction is wrong on first preview — see TUNING NOTES.
IDLE_SPINE_BREATH_DEG = 1.4
IDLE_CHEST_BREATH_DEG = 1.1
IDLE_HEAD_LOOK_DEG    = 1.7

WALK_LEG_AXIS         = 0          # 0=X (front/back rotation in most rigs), 2=Z if your bones are oriented differently
WALK_LEG_SWING_DEG    = 22.0       # forward/back leg swing
WALK_KNEE_BEND_DEG    = 28.0       # max knee bend on lift
WALK_HIPS_BOB_M       = 0.020      # ±2 cm vertical hip oscillation (in metres BEFORE FBX globalScale)
WALK_HIPS_YAW_DEG     = 3.0        # counter-rotation
WALK_ARM_COUNTER_DEG  = 5.0        # tiny arm counter-sway (rifle still locked to hands)

ATTACK_CHEST_RECOIL_DEG = 7.0      # back-tilt for shot kick
ATTACK_R_ARM_KICK_DEG   = 6.0
ATTACK_L_ARM_KICK_DEG   = 5.0
ATTACK_HEAD_AIM_DEG     = 3.0

DEATH_HIPS_DROP_M       = 0.85     # how far the hips fall (BEFORE FBX globalScale)
DEATH_HIPS_FALL_DEG     = 82.0     # forward fold of the hips → lying face-down
DEATH_SPINE_FOLD_DEG    = 18.0     # chest flexes toward floor as the body collapses
DEATH_HEAD_DROP_DEG     = 12.0

# ---------------------------------------------------------------------------- #
# Find Armature, prep helpers
# ---------------------------------------------------------------------------- #

arm = bpy.data.objects.get(ARMATURE_NAME)
if arm is None or arm.type != 'ARMATURE':
    raise RuntimeError(
        f"Armature '{ARMATURE_NAME}' not found in this .blend. "
        f"Edit ARMATURE_NAME at the top of the script to match your rig.")

# Ensure the armature has animation_data so we can swap actions cleanly.
if arm.animation_data is None:
    arm.animation_data_create()

# Make sure the armature + every mesh that shares its skin is selectable for
# the FBX export step.
def select_armature_and_meshes():
    bpy.ops.object.select_all(action='DESELECT')
    arm.select_set(True)
    bpy.context.view_layer.objects.active = arm
    for o in bpy.data.objects:
        if o.type == 'MESH':
            # Heuristic: include any mesh that's parented to the Armature or
            # has the Armature as a modifier.
            if o.parent == arm or any(m.type == 'ARMATURE' and m.object == arm for m in o.modifiers):
                o.select_set(True)


def ensure_euler_xyz(bone):
    if bone.rotation_mode != 'XYZ':
        bone.rotation_mode = 'XYZ'


def kr_deg(bone_name, frame, axis, deg):
    """Insert a local-rotation keyframe on (axis ∈ 0/1/2 → X/Y/Z) at frame."""
    pb = arm.pose.bones.get(bone_name)
    if pb is None:
        print(f"[author] MISSING bone '{bone_name}' (rotation key at frame {frame} skipped).")
        return
    ensure_euler_xyz(pb)
    e = list(pb.rotation_euler)
    e[axis] = math.radians(deg)
    pb.rotation_euler = Euler(e, 'XYZ')
    pb.keyframe_insert(data_path='rotation_euler', frame=frame)


def kr_reset(bone_name, frame):
    """Insert a zero-rotation keyframe (rest pose) at frame."""
    pb = arm.pose.bones.get(bone_name)
    if pb is None: return
    ensure_euler_xyz(pb)
    pb.rotation_euler = Euler((0.0, 0.0, 0.0), 'XYZ')
    pb.keyframe_insert(data_path='rotation_euler', frame=frame)


def kl_axis(bone_name, frame, axis, val):
    """Insert a single-axis location keyframe (bone-local). axis ∈ 0/1/2."""
    pb = arm.pose.bones.get(bone_name)
    if pb is None:
        print(f"[author] MISSING bone '{bone_name}' (location key at frame {frame} skipped).")
        return
    loc = list(pb.location)
    loc[axis] = val
    pb.location = loc
    pb.keyframe_insert(data_path='location', frame=frame)


def kl_reset(bone_name, frame):
    pb = arm.pose.bones.get(bone_name)
    if pb is None: return
    pb.location = (0.0, 0.0, 0.0)
    pb.keyframe_insert(data_path='location', frame=frame)


def new_action(name):
    """Create or replace a Blender Action; bind it to the armature; return it."""
    if name in bpy.data.actions:
        # Wipe and reuse — keyframes get re-authored from scratch.
        bpy.data.actions.remove(bpy.data.actions[name])
    act = bpy.data.actions.new(name=name)
    act.use_fake_user = True   # so the action survives even if not bound
    arm.animation_data.action = act
    return act


# ---------------------------------------------------------------------------- #
# IDLE — subtle breathing + micro head sway, looping
# ---------------------------------------------------------------------------- #

def build_idle():
    print("[author] Building Idle …")
    new_action("Idle")
    bpy.context.scene.frame_start = IDLE_START
    bpy.context.scene.frame_end   = IDLE_END

    mid = (IDLE_START + IDLE_END) // 2

    # Reset everything we touch at the start AND end so the loop closes cleanly.
    for f in (IDLE_START, IDLE_END):
        kr_reset("Spine", f)
        kr_reset("Chest", f)
        kr_reset("Head",  f)

    # Mid-cycle exhale: spine + chest curl slightly forward.
    kr_deg("Spine", mid, 0,  IDLE_SPINE_BREATH_DEG)
    kr_deg("Chest", mid, 0,  IDLE_CHEST_BREATH_DEG)

    # Head looks gently right then left across the cycle (eyes-scanning feel).
    q1, q3 = IDLE_START + (IDLE_END - IDLE_START)//3, IDLE_START + 2*(IDLE_END - IDLE_START)//3
    kr_deg("Head", q1, 1,  IDLE_HEAD_LOOK_DEG)
    kr_deg("Head", q3, 1, -IDLE_HEAD_LOOK_DEG)


# ---------------------------------------------------------------------------- #
# WALK — symmetric leg cycle with knee bend + hips bob + counter yaw
# ---------------------------------------------------------------------------- #

def build_walk():
    print("[author] Building Walk …")
    new_action("Walk")
    bpy.context.scene.frame_start = WALK_START
    bpy.context.scene.frame_end   = WALK_END

    # Step phases: contact-L → midstride → contact-R → midstride → contact-L
    span = WALK_END - WALK_START                        # e.g. 20
    f0 = WALK_START                                     # 1   — L contact
    f1 = WALK_START + span // 4                         # 6   — L pass / R lift
    f2 = WALK_START + span // 2                         # 11  — R contact
    f3 = WALK_START + (3 * span) // 4                   # 16  — R pass / L lift
    f4 = WALK_END                                       # 21  — L contact (loop close)

    A = WALK_LEG_AXIS

    # Legs — alternating forward/back swing.
    for f, sign in ((f0,  1), (f2, -1), (f4,  1)):
        kr_deg("LeftUpperLeg",  f, A,  WALK_LEG_SWING_DEG * sign)
        kr_deg("RightUpperLeg", f, A, -WALK_LEG_SWING_DEG * sign)

    # Knees fold at midstride on the lifting leg.
    kr_deg("LeftLowerLeg",  f1, A, -WALK_KNEE_BEND_DEG)   # left leg lifted
    kr_deg("RightLowerLeg", f3, A, -WALK_KNEE_BEND_DEG)   # right leg lifted
    # Knees nearly straight at contact phases.
    for f in (f0, f2, f4):
        kr_reset("LeftLowerLeg",  f)
        kr_reset("RightLowerLeg", f)

    # Hips vertical bob — up at midstride, down at contact.
    for f in (f0, f2, f4):
        kl_axis("Hips", f, 1, 0.0)                       # contact: lowest
    kl_axis("Hips", f1, 1, WALK_HIPS_BOB_M)
    kl_axis("Hips", f3, 1, WALK_HIPS_BOB_M)

    # Hips counter-rotation: opposite to lead leg.
    kr_deg("Hips", f0, 1,  WALK_HIPS_YAW_DEG)
    kr_deg("Hips", f2, 1, -WALK_HIPS_YAW_DEG)
    kr_deg("Hips", f4, 1,  WALK_HIPS_YAW_DEG)

    # Tiny arm counter-sway. Rifle stays locked to hands (SM_Weapon is bone-
    # parented and we don't key it).
    kr_deg("LeftUpperArm",  f0, A, -WALK_ARM_COUNTER_DEG)
    kr_deg("RightUpperArm", f0, A,  WALK_ARM_COUNTER_DEG)
    kr_deg("LeftUpperArm",  f2, A,  WALK_ARM_COUNTER_DEG)
    kr_deg("RightUpperArm", f2, A, -WALK_ARM_COUNTER_DEG)
    kr_deg("LeftUpperArm",  f4, A, -WALK_ARM_COUNTER_DEG)
    kr_deg("RightUpperArm", f4, A,  WALK_ARM_COUNTER_DEG)


# ---------------------------------------------------------------------------- #
# ATTACK — short rifle-recoil pulse, one-shot
# ---------------------------------------------------------------------------- #

def build_attack():
    print("[author] Building Attack …")
    new_action("Attack")
    bpy.context.scene.frame_start = ATTACK_START
    bpy.context.scene.frame_end   = ATTACK_END

    # Hold rest at start; peak recoil at ~1/3; return to rest at end.
    peak = ATTACK_START + (ATTACK_END - ATTACK_START) // 3      # ≈ frame 5
    ease = ATTACK_START + 2 * (ATTACK_END - ATTACK_START) // 3  # ≈ frame 10

    for f in (ATTACK_START, ATTACK_END):
        kr_reset("Chest", f)
        kr_reset("RightUpperArm", f)
        kr_reset("LeftUpperArm",  f)
        kr_reset("Head", f)

    # Recoil kick.
    kr_deg("Chest",         peak, 0, -ATTACK_CHEST_RECOIL_DEG)
    kr_deg("RightUpperArm", peak, 0, -ATTACK_R_ARM_KICK_DEG)
    kr_deg("LeftUpperArm",  peak, 0, -ATTACK_L_ARM_KICK_DEG)
    kr_deg("Head",          peak, 1,  ATTACK_HEAD_AIM_DEG)

    # Half-recovered at ease frame.
    kr_deg("Chest",         ease, 0, -ATTACK_CHEST_RECOIL_DEG * 0.4)
    kr_deg("RightUpperArm", ease, 0, -ATTACK_R_ARM_KICK_DEG   * 0.4)
    kr_deg("LeftUpperArm",  ease, 0, -ATTACK_L_ARM_KICK_DEG   * 0.4)


# ---------------------------------------------------------------------------- #
# DEATH — forward collapse, ends lying on the ground, one-shot
# ---------------------------------------------------------------------------- #

def build_death():
    print("[author] Building Death …")
    new_action("Death")
    bpy.context.scene.frame_start = DEATH_START
    bpy.context.scene.frame_end   = DEATH_END

    # Phases: stand → knees buckle → forward fold → landed → rest.
    f_stand   = DEATH_START                              # 1  — standing
    f_buckle  = DEATH_START + 8                          # 9
    f_fold    = DEATH_START + 18                         # 19 — folding forward
    f_landed  = DEATH_START + 28                         # 29 — on the ground
    f_rest    = DEATH_END                                # 36 — motionless

    # Standing rest pose.
    kr_reset("Hips",  f_stand)
    kr_reset("Spine", f_stand)
    kr_reset("Chest", f_stand)
    kr_reset("Head",  f_stand)
    kl_reset("Hips",  f_stand)

    # Knees buckle slightly, hips start dipping.
    kr_deg("LeftLowerLeg",  f_buckle, 0,  WALK_KNEE_BEND_DEG * 0.8)
    kr_deg("RightLowerLeg", f_buckle, 0,  WALK_KNEE_BEND_DEG * 0.8)
    kl_axis("Hips",         f_buckle, 1, -DEATH_HIPS_DROP_M * 0.2)
    kr_deg("Hips",          f_buckle, 0,  DEATH_HIPS_FALL_DEG * 0.2)

    # Fold forward — hips rotate, hips drop, spine curls.
    kl_axis("Hips",  f_fold, 1, -DEATH_HIPS_DROP_M * 0.7)
    kr_deg("Hips",   f_fold, 0,  DEATH_HIPS_FALL_DEG * 0.7)
    kr_deg("Spine",  f_fold, 0,  DEATH_SPINE_FOLD_DEG)
    kr_deg("Chest",  f_fold, 0,  DEATH_SPINE_FOLD_DEG * 0.6)
    kr_deg("Head",   f_fold, 0,  DEATH_HEAD_DROP_DEG)

    # Lying — final pose, end of fall.
    kl_axis("Hips",  f_landed, 1, -DEATH_HIPS_DROP_M)
    kr_deg("Hips",   f_landed, 0,  DEATH_HIPS_FALL_DEG)
    kr_deg("Spine",  f_landed, 0,  DEATH_SPINE_FOLD_DEG)
    kr_deg("Chest",  f_landed, 0,  DEATH_SPINE_FOLD_DEG * 0.6)
    kr_deg("Head",   f_landed, 0,  DEATH_HEAD_DROP_DEG)

    # Rest — hold the pose for the remaining frames (no motion).
    kl_axis("Hips",  f_rest, 1, -DEATH_HIPS_DROP_M)
    kr_deg("Hips",   f_rest, 0,  DEATH_HIPS_FALL_DEG)
    kr_deg("Spine",  f_rest, 0,  DEATH_SPINE_FOLD_DEG)
    kr_deg("Chest",  f_rest, 0,  DEATH_SPINE_FOLD_DEG * 0.6)
    kr_deg("Head",   f_rest, 0,  DEATH_HEAD_DROP_DEG)


# ---------------------------------------------------------------------------- #
# FBX export — one file per Action, Generic-compatible settings
# ---------------------------------------------------------------------------- #

def export_clip(action_name, file_basename, frame_start, frame_end):
    """Set the active action and export it as <file_basename>.fbx."""
    act = bpy.data.actions.get(action_name)
    if act is None:
        print(f"[author] ✗ Action '{action_name}' missing — cannot export.")
        return False

    arm.animation_data.action = act
    bpy.context.scene.frame_start = frame_start
    bpy.context.scene.frame_end   = frame_end

    select_armature_and_meshes()

    out_path = os.path.join(OUTPUT_DIR, file_basename + ".fbx")
    print(f"[author] Exporting → {out_path} (frames {frame_start}..{frame_end})")

    # Generic-rig friendly FBX export. These flags are picked to match what
    # Unity expects when importing a clip-only FBX with animationType=Generic
    # and avatarSetup=CopyFromOther (the runtime Avatar is on Soldier_Rigged).
    bpy.ops.export_scene.fbx(
        filepath              = out_path,
        check_existing        = False,
        use_selection         = True,
        use_active_collection = False,
        object_types          = {'ARMATURE', 'MESH'},
        bake_anim                       = True,
        bake_anim_use_all_bones         = True,
        bake_anim_use_nla_strips        = False,
        bake_anim_use_all_actions       = False,
        bake_anim_force_startend_keying = True,
        bake_anim_step                  = 1.0,
        bake_anim_simplify_factor       = 1.0,
        add_leaf_bones        = False,
        primary_bone_axis     = 'Y',
        secondary_bone_axis   = 'X',
        armature_nodetype     = 'NULL',
        mesh_smooth_type      = 'FACE',
        apply_unit_scale      = True,
        apply_scale_options   = 'FBX_SCALE_NONE',
        global_scale          = 1.0,
        bake_space_transform  = False,
        axis_forward          = '-Z',
        axis_up               = 'Y',
    )
    return True


# ---------------------------------------------------------------------------- #
# Main
# ---------------------------------------------------------------------------- #

print("[author] ─── Authoring Soldier animations ───")

build_idle()
build_walk()
build_attack()
build_death()

print("[author] ─── Exporting FBX clips ───")

ok_idle   = export_clip("Idle",   "Soldier_Rigged@Idle",   IDLE_START,   IDLE_END)
ok_walk   = export_clip("Walk",   "Soldier_Rigged@Walk",   WALK_START,   WALK_END)
ok_attack = export_clip("Attack", "Soldier_Rigged@Attack", ATTACK_START, ATTACK_END)
ok_death  = export_clip("Death",  "Soldier_Rigged@Death",  DEATH_START,  DEATH_END)

print("[author] ──── Summary ────")
print(f"  Idle   : {'OK' if ok_idle   else 'FAILED'}  frames {IDLE_START}..{IDLE_END}   (loop)")
print(f"  Walk   : {'OK' if ok_walk   else 'FAILED'}  frames {WALK_START}..{WALK_END}   (loop)")
print(f"  Attack : {'OK' if ok_attack else 'FAILED'}  frames {ATTACK_START}..{ATTACK_END} (one-shot)")
print(f"  Death  : {'OK' if ok_death  else 'FAILED'}  frames {DEATH_START}..{DEATH_END}  (one-shot)")
print(f"  Output : {OUTPUT_DIR}")
print("[author] Done. Verify in Blender's Action Editor; tweak constants and re-run if needed.")
