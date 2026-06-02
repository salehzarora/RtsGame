# FighterJet — Unity-Ready RTS Aircraft Asset

Prepared from the Meshy "Azure Vortex" fighter jet. The original design and
silhouette are preserved — the mesh was cleaned, re-materialed, optimized into
LODs, and exported Unity-ready. No redesign was performed.

## Contents

```
FighterJet_Unity_Final/
├── FighterJet_LOD0.fbx      64,997 tris   (target 40k–80k)
├── FighterJet_LOD1.fbx      21,999 tris   (target 15k–30k)
├── FighterJet_LOD2.fbx       8,999 tris   (target  5k–12k)
├── FighterJet_Final.blend   master file (all 3 LODs + materials)
├── Textures/
│   ├── T_FighterJet_BaseColor.png   (2048², sRGB)
│   ├── T_FighterJet_Normal.png      (2048², linear / Non-Color)
│   ├── T_FighterJet_Roughness.png   (2048², linear / Non-Color)
│   └── T_FighterJet_Metallic.png    (2048², linear / Non-Color)
├── Previews/                material-slot + team-color reference renders
└── README.md
```

## Material slots (identical on every LOD)

| Slot | Material name      | Source                  | Unity setup |
|------|--------------------|-------------------------|-------------|
| 0    | `M_Jet_MainBody`   | base/normal/rough/metal | Standard textured |
| 1    | `M_Jet_DarkPanels` | base/normal/rough/metal | Standard textured |
| 2    | `M_Jet_Canopy`     | base/normal/rough/metal | Glass-ish — lower roughness, optionally transparent/reflective |
| 3    | `M_TeamColor`      | **flat albedo color**   | **Recolor per team** (see below) |

> `M_Jet_Emission` was intentionally **omitted**: the source emission map is
> effectively pure black, so an emission slot would render nothing. If you want
> glow later, add an emissive material and an emission mask — easy to do.

## ⭐ Team color (the key feature)

The blue edge accents (wing leading/trailing edges, twin-tail stripes,
fuselage stripes) were separated into their own material slot **`M_TeamColor`**.

`M_TeamColor` uses a **flat, tintable albedo color** (no base-color texture) so
you can recolor it per player/team in Unity without any baked-in blue fighting
your tint. Normal / roughness / metallic maps still apply to those faces, so
surface detail is preserved.

**To recolor in Unity:**
1. Select the `M_TeamColor` material on the imported model.
2. Do **not** assign a base/albedo texture to it (leave it empty).
3. Set the material **Albedo color** to the team color — or drive it from script:
   `renderer.materials[3].color = teamColor;`  (or use a `MaterialPropertyBlock`
   with `_BaseColor` / `_Color` for per-instance recoloring without extra draw calls).

See `Previews/team_blue.png` and `Previews/team_red.png` — the same accent faces,
recolored just by changing that one material.

## Unity import notes

- **Orientation/scale:** exported Y-up, -Z forward with transforms applied — the
  model imports upright with identity rotation. Native length ≈ 1.9 units; scale
  on import if your game uses metric (e.g. set Scale Factor so length ≈ 15 m).
- **Normal maps:** exported with tangents (`use_tspace`). Set
  `T_FighterJet_Normal` to **Texture Type = Normal map** in Unity, and the
  Roughness/Metallic PNGs to **sRGB = OFF**.
- **LOD Group:** add a `LOD Group` component and assign LOD0/1/2. Suggested
  screen-height thresholds: LOD0 100%–50%, LOD1 50%–20%, LOD2 20%–2%, then culled.
- **Roughness vs. Smoothness:** Unity's URP/HDRP Lit uses *smoothness*. Either
  invert the roughness map (smoothness = 1 − roughness) or plug it into the
  smoothness channel set to "Albedo Alpha / Metallic Alpha" workflow as you prefer.

## Mesh prep that was done

- Verified the real 195,316-tri jet (an earlier file was a placeholder cube).
- Applied transforms; welded doubles (mesh was already clean), dissolved
  degenerate faces, removed loose geometry, recalculated normals outward,
  smooth shading.
- Stripped stray datablocks and the embedded lossy JPG textures; reconnected the
  full-quality PNG map set.
- Classified faces against the base-color atlas to build the material slots;
  the canopy was geometry-isolated (largest upward-facing dark cluster at the
  cockpit) since it shares the dark texture region with panel detailing.
- Decimated (collapse) to the three LOD tri targets — silhouette preserved.
