AIRFIELD - UNITY IMPORT PACKAGE
================================
Original Meshy "HexaPad" airfield. Visual design unchanged.
~70 x 70 m footprint, pivot bottom-center, sits on ground (Y=0).
LOD0 = 449,999 tris | LOD1 = 200,000 | LOD2 = 79,999.

WHAT'S INSIDE
-------------
  SM_Airfield_LOD0.fbx / LOD1.fbx / LOD2.fbx   (meshes)
  Airfield_Albedo.png        - base color (sRGB)
  Airfield_Normal.png        - normal map
  Airfield_MetallicSmoothness.png - RGB=metallic, A=smoothness(1-roughness)
  Airfield_Emission.png      - emission
  Airfield_Metallic.png / Airfield_Roughness.png - source maps (reference only)
  Editor/AirfieldSetup.cs    - one-click setup script
  UNITY_SETUP.md             - full details + test procedure
  (M_Airfield.mat and Airfield.prefab are CREATED for you when you run the script.)

------------------------------------------------------------
1) WHERE TO COPY
------------------------------------------------------------
Copy the entire "Airfield_Unity_Final" folder into your project at:
    Assets/Buildings/Airfield/
Keep the Editor/ subfolder inside it. Let Unity finish importing.
(Works with Unity 6 + URP, and also Built-in - the script auto-detects.)

------------------------------------------------------------
2) WHICH MENU BUTTON TO RUN
------------------------------------------------------------
Top menu:  Tools  >  Airfield  >  Setup Material & LOD Prefab
This creates M_Airfield, assigns all textures (with correct Smoothness),
sets the Normal map import type, builds Airfield.prefab with a LOD Group,
adds ONE BoxCollider (no MeshCollider), and marks the meshes Static.
Safe to run more than once. Then drag Airfield.prefab into your scene.

------------------------------------------------------------
3) HOW TO CONFIRM IT WORKS
------------------------------------------------------------
COLORS:
  - Add a Directional Light if your scene has none.
  - Markings, yellow lines, pad numbers, tower, hangars show full color.
  - Pink = re-run the menu (shader/pipeline). Washed out = check Albedo sRGB ON,
    MetallicSmoothness sRGB OFF. Flat = Normal slot filled + Texture Type = Normal map.

LODs:
  - Select the prefab instance > open the LOD Group component.
  - Move the camera (or drag the LOD bar): mesh swaps LOD0 -> LOD1 -> LOD2 -> Culled.
  - Stats/Frame Debugger: tris drop ~450k -> ~200k -> ~80k.

BOXCOLLIDER:
  - Select Airfield: exactly one BoxCollider, NO MeshCollider.
  - Green wireframe wraps the footprint.
  - Optional: in Play mode drop a Cube+Rigidbody above it; it lands on the box.
    (Want a flat ground footprint? Lower the BoxCollider Size Y, e.g. to 3, Center Y ~1.5.)

See UNITY_SETUP.md for the detailed version.
