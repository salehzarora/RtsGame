==================================================================
 RTS SOLDIER  -  Unity Import Notes
 Prepared from a Meshy-generated model for an isometric/top-down RTS
==================================================================

------------------------------------------------------------------
1. WHAT'S IN THIS FOLDER
------------------------------------------------------------------
  Soldier_LOD0.fbx      Full rig + LOD0 body (~30k tris) + weapon + markers
  Soldier_LOD1.fbx      Full rig + LOD1 body (~12k tris) + weapon + markers
  Soldier_LOD2.fbx      Full rig + LOD2 body (~5k tris)  + weapon + markers
  Soldier_Rigged.fbx    Full rig + ALL three LOD meshes in one file
                        (use this if you want to build a LODGroup from a
                         single import)
  Soldier_Unity_Final.blend   Editable master scene (textures packed)
  Textures/  (source: Meshy_AI_Sandstorm_Sentinel_0602185207_texture)
      Soldier_BaseColor.png   Albedo / diffuse  (sRGB)
      Soldier_Normal.png      Normal map        (set to "Normal map" in Unity)
      Soldier_Roughness.png   Roughness         (Non-Color / Linear)
      Soldier_Metallic.png    Metallic          (Non-Color / Linear)
      Soldier_Emission.png    Emission (this map is fully BLACK = no emission;
                              included for completeness, not connected).
  (Textures are ALSO embedded inside each FBX, so Unity can auto-extract
   materials on import.)

------------------------------------------------------------------
2. SCALE & ORIENTATION
------------------------------------------------------------------
  - Unit scale is 1:1 with Unity (model is ~1.90 m tall = correct).
  - Exported with Y-up / -Z-forward, so the soldier faces +Z (forward)
    in Unity. No rotation fix needed.
  - Feet are on the ground plane (origin at floor, between the feet).
    Set the transform position to (0,0,0) and the feet will sit on the floor.

------------------------------------------------------------------
3. HIERARCHY (as exported)
------------------------------------------------------------------
  SoldierRoot
    Armature                (Generic skeleton, 19 bones)
      [RightHand bone]
          SM_Weapon         (rifle, parented to the right hand)
              FirePoint         <-- spawn bullets / projectiles here
              MuzzleFlashPoint  <-- muzzle flash VFX
              AimPoint          <-- forward aim reference (1 m ahead of muzzle)
          WeaponHoldPoint   (grip reference on the right hand)
    SM_Soldier_LOD0         (SkinnedMeshRenderer)
    SM_Soldier_LOD1
    SM_Soldier_LOD2

  The weapon is attached to the RightHand BONE, so it stays locked to the
  hand during idle / walk / attack / death animations.

------------------------------------------------------------------
4. UNITY IMPORT STEPS
------------------------------------------------------------------
  A. Drag Soldier_LOD0.fbx (or Soldier_Rigged.fbx) into your project.
  B. Select the FBX > Inspector:
       - Model tab:    Scale Factor = 1, "Convert Units" leave default.
       - Rig tab:      Animation Type = "Generic".
                       (Avatar Definition: Create From This Model.)
                       NOTE: Bones are named with Unity-Humanoid names
                       (Hips, Spine, Chest, Neck, Head, Left/RightUpperArm,
                        ...UpperLeg, ...Foot, etc.). You MAY switch to
                        "Humanoid" and it will auto-map most bones, but the
                        bind pose is a combat stance (not a true T-pose), so
                        retargeted arm animations can look slightly off.
                        Generic is the recommended/stable choice.
       - Materials tab: "Extract Textures" then "Extract Materials"
                        (or use the Textures/ folder and assign manually).
       - On the Normal texture: set Texture Type = "Normal map".
  C. Apply.

------------------------------------------------------------------
5. LOD GROUP SETUP
------------------------------------------------------------------
  Option 1 (recommended): drop Soldier_Rigged.fbx in the scene, add a
  "LOD Group" component on the root, and assign:
       LOD0 -> SM_Soldier_LOD0   (e.g. 100%-50% screen height)
       LOD1 -> SM_Soldier_LOD1   (50%-20%)
       LOD2 -> SM_Soldier_LOD2   (20%-3%)
  The weapon renderer can be added to every LOD level.

  Option 2: import each Soldier_LODx.fbx separately and combine the meshes
  under one LOD Group manually.

------------------------------------------------------------------
6. TEAM COLOR  (M_TeamColor)
------------------------------------------------------------------
  - The soldier has a dedicated material slot "M_TeamColor" applied ONLY to
    the original blue accent areas of the Meshy design: the UPPER-ARM BANDS
    (armbands, wrapping front+back), the CHEST STRIP, and the KNEE PADS.
    (These were the blue regions in the source texture; no invented patches.)
  - It is a flat, untextured material (default red). To set a team color
    per player in Unity:
        var r = soldier.GetComponentsInChildren<Renderer>();
        // find the material named "M_TeamColor" and set its color:
        mat.color = teamColor;        // or material.SetColor("_BaseColor", teamColor)  (URP/HDRP)
  - It is NOT baked into the main texture, so changing it is instant and
    per-instance (use MaterialPropertyBlock for many units without extra
    draw calls / material instances).

------------------------------------------------------------------
7. MATERIAL SLOTS
------------------------------------------------------------------
  Body mesh slots:
     0  M_Soldier_Uniform   (main camo texture)   - arms, legs, general body
     1  M_Soldier_Armor     (same camo texture)   - torso/chest block
     2  M_Soldier_DarkGear  (same camo texture)   - boots / lower legs
     3  M_TeamColor          (flat tint)          - shoulder + helmet accents
  Weapon mesh:
        M_Soldier_Weapon     (same camo texture)
  NOTE: Uniform/Armor/DarkGear/Weapon currently SHARE the one baked Meshy
  camo texture (Meshy bakes everything into a single UV/texture, so they
  cannot be split by texture automatically). They are separate SLOTS so you
  can give each its own texture/tint/smoothness later in Unity if you wish.

------------------------------------------------------------------
8. ANIMATION
------------------------------------------------------------------
  - No animation clips are included; the rig is clean and ready to animate.
  - For Idle / Walk / Attack / Death: either hand-author clips on this
    Generic rig, or retarget Generic animations. (Mixamo's one-click
    auto-rigger expects an empty-handed A/T-pose, so it is NOT ideal for
    this weapon-holding model - see section 9.)
  - The rig was tested with a walk-cycle pose: legs/knees/spine deform
    cleanly and the weapon stays locked to the hand.

------------------------------------------------------------------
9. KNOWN LIMITATIONS / MANUAL CHECKS
------------------------------------------------------------------
  - Rig is GENERIC (not a true Humanoid T-pose). This was chosen because the
    model is permanently posed holding the rifle with both hands; forcing an
    A/T-pose would have damaged the fused hands/weapon. Bone names are
    Humanoid-compatible if you want to experiment with Humanoid retargeting.
  - The weapon was separated from a single fused Meshy mesh. The cut is at
    the grip and is approximate; a small open area on the body where the
    rifle sat is hidden behind the weapon (not visible from RTS camera).
  - Auto-weights are good for an RTS-distance camera. If you see minor
    pinching at extreme shoulder/hip rotations up close, do a quick weight-
    paint pass on those vertex groups.
  - Textures are the correct Sandstorm Sentinel soldier set (BaseColor,
    Normal, Roughness, Metallic). The earlier wrong aircraft textures
    (Image_0/2/3 = white/blue camo) were removed. Emission map is black.
  - Fingers are not individually rigged (single Hand bone per side) - fine
    for a fixed-grip RTS unit.
==================================================================
