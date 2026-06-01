# Airfield — Unity Setup (one-click)

Original Meshy "HexaPad" airfield, **visual design unchanged**.
Footprint ≈ **70 × 70 m**, height ≈ **20.5 m**. Pivot = **bottom-center**, sits on **ground (Y=0)**.
Single fused mesh, one material, one UV set.

## QUICK START (do this on your RTS PC)
1. Copy the **entire `Airfield_Unity_Final` folder** into your project at `Assets/Buildings/Airfield/`
   (keep the `Editor/` subfolder inside it — Unity needs it there).
2. Let Unity import. Then run the menu: **Tools ▸ Airfield ▸ Setup Material & LOD Prefab**.
3. Done. It creates **`M_Airfield`** and **`Airfield.prefab`** (with LOD Group) in that folder and
   assigns the material to all 3 LODs. Drag `Airfield.prefab` into your scene.

The script auto-detects **URP** (→ URP/Lit) vs **Built-in** (→ Standard). It is idempotent — safe to re-run.

## Files
| File | Use | Tris |
|---|---|---|
| `SM_Airfield_LOD0.fbx` | **LOD0** (main) | 449,999 |
| `SM_Airfield_LOD1.fbx` | LOD1 (medium) | 200,000 |
| `SM_Airfield_LOD2.fbx` | LOD2 (far) | 79,999 |
| `Airfield_Albedo.png` | Base Map (sRGB) | 2048² |
| `Airfield_Normal.png` | Normal (linear) | 2048² |
| `Airfield_MetallicSmoothness.png` | **Metallic (RGB) + Smoothness (A)** (linear) | 2048² |
| `Airfield_Emission.png` | Emission (sRGB) | 2048² |
| `Airfield_Metallic.png`, `Airfield_Roughness.png` | source maps (not assigned directly) | 2048² |
| `Airfield_Unity_Final.blend` | editable source | — |
| `Editor/AirfieldSetup.cs` | the one-click setup script | — |

## Smoothness handling (why MetallicSmoothness exists)
Unity wants **Smoothness**, Meshy gives **Roughness**. I pre-baked
`Airfield_MetallicSmoothness.png` = **RGB: metallic, Alpha: (1 − roughness)**, which is exactly the
packed map URP/Lit and Standard expect. The script sets *Smoothness Source = Metallic Alpha*, so
roughness is handled correctly without plugging roughness directly. (Raw `Airfield_Roughness.png`
is kept only as a reference / for rebaking.)

## What the script sets (for manual verification)
- **Albedo** → Base Map, sRGB on.
- **Normal** → Texture Type = **Normal map**; assigned to Normal/Bump, `_NORMALMAP` on.
- **MetallicSmoothness** → Metallic map, sRGB **off**, alpha preserved; `_METALLICSPECGLOSSMAP` on, Metallic & Smoothness sliders = 1.
- **Emission** → Emission map, sRGB on, `_EMISSION` on, color white, RealtimeEmissive.

## LOD Group (created by the script)
Transition thresholds (screen-relative height — tune for your RTS camera):
LOD0 → LOD1 at 50% · LOD1 → LOD2 at 18% · LOD2 → cull at 4%.

## Collider & Static (now done by the script)
- **One BoxCollider** on the `Airfield` root, auto-sized to the LOD0 bounds (~70 × 20.5 × 70).
  **No MeshCollider** is used. If you want a flat ground footprint instead, just reduce the
  box **Size Y** (e.g. 3) and set **Center Y** to ~1.5 in the Inspector.
- All visual LOD meshes are marked **Static** (full static flags — batching, GI, occlusion, etc.).
- Optional later: add child BoxColliders for tower/hangars; keep the 6 pads clear for aircraft.

## FBX import (script does not change these; defaults are fine)
Scale Factor 1, Convert Units on (FBX baked Y-up at meter scale). Generate Colliders OFF.

## Notes
- 2048² textures; set per-platform Max Size 1024 if you need memory savings (keep LOD0 at 2048).
- One fused mesh — correct for a static building. Per-part (tower/pads) logic is a separate gameplay step (not done here).
- No mesh/design changes were made; this is materials + LOD + prefab only.

---

# TEST PROCEDURE (verify colors, LODs, collider)

### A. Colors / textures
1. Drag `Airfield.prefab` into a scene. Position at (0,0,0).
2. Add a **Directional Light** if the scene has none (GameObject ▸ Light ▸ Directional Light).
3. In Scene view set draw mode to **Shaded**. The runway markings, yellow lines, pad numbers
   (06/04/02…), tower and hangars should show full color — not grey/pink/black.
   - **Pink** = shader missing (wrong pipeline). Re-run Tools ▸ Airfield ▸ Setup.
   - **Flat/no bumps** = open `M_Airfield`, confirm Normal slot filled and that
     `Airfield_Normal` Texture Type = *Normal map* (no "fix normal map" warning).
   - **Washed out** = confirm `Airfield_Albedo` has sRGB **on** and MetallicSmoothness sRGB **off**.
4. Select `M_Airfield` → confirm 4 maps assigned: Base, Normal, Metallic (the *MetallicSmoothness* file),
   Emission. Smoothness Source = **Metallic Alpha**.

### B. LODs
1. Select the `Airfield` instance → in Inspector open the **LOD Group**. You'll see the LOD bar.
2. Drag the camera (or the LOD bar's vertical line) — Scene view shows **LOD0 → LOD1 → LOD2 → Culled**
   as it shrinks on screen. Confirm the mesh visibly swaps (watch the tris drop).
3. Open **Window ▸ Analysis ▸ Frame Debugger** (or Stats overlay) and zoom the Game camera out/in:
   triangle count should fall (~450k → ~200k → ~80k) as LODs switch.
4. Sanity check the LOD bounds: in the LOD Group component, **Recalculate Bounds** if the box looks off.

### C. Collider
1. Select `Airfield` → confirm **one BoxCollider**, **no MeshCollider**.
2. In Scene view the green collider wireframe should wrap the airfield footprint.
3. Quick raycast test: create a script-free check — enable **Gizmos**, enter Play mode, and in the
   Scene view use the **Physics Debugger** (Window ▸ Analysis ▸ Physics Debugger) to see the box.
   Or temporarily attach Unity's built-in: select the object, in Play mode click on it in Scene view —
   it should be pickable via the box.
4. (Optional, no gameplay code) Drop a primitive Cube with a Rigidbody above the airfield in Play mode;
   it should land on the BoxCollider, not fall through. Delete the cube after testing.
