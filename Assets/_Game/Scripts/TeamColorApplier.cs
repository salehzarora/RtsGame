using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One renderer + a list of material-slot indexes inside that renderer's
/// <c>Materials</c> array. Only the listed slots are recoloured by
/// <see cref="TeamColorApplier"/>; every other slot keeps its original material.
///
/// Use one entry per renderer. If a renderer has two slots and you want only
/// the body recoloured, list <c>0</c>; leave <c>1</c> out so the skin/secondary
/// material stays as-is.
/// </summary>
[System.Serializable]
public class RendererMaterialSlot
{
    [Tooltip("The renderer (MeshRenderer or SkinnedMeshRenderer) that carries the materials.")]
    public Renderer renderer;

    [Tooltip("Indexes into renderer.materials to recolor. Leave slots OUT of this list to keep " +
             "them at their original material (skin, weapon, boots, gear, etc.).")]
    public List<int> materialIndexes = new List<int>();
}

/// <summary>
/// One renderer + a list of material-slot indexes + a FIXED color. Slots
/// listed here are painted with <see cref="fixedColor"/> at OnEnable and
/// are NEVER touched by the dynamic team-color system, even if the menu
/// color changes. Use for boots, dark trim, gear — anything that should
/// be a permanent non-team color.
/// </summary>
[System.Serializable]
public class FixedColorSlot
{
    [Tooltip("The renderer that carries the materials. Usually the same renderer " +
             "you also use in teamColorSlots — just with different indexes.")]
    public Renderer renderer;

    [Tooltip("Indexes into renderer.materials to recolor with the FIXED color below. " +
             "These slots are never touched by the team-color flow.")]
    public List<int> materialIndexes = new List<int>();

    [Tooltip("The fixed (non-team) color applied to the listed slots at OnEnable. " +
             "Default is a dark gray suitable for boots / dark trim.")]
    [ColorUsage(showAlpha: false)]
    public Color fixedColor = new Color(0.10f, 0.10f, 0.10f);
}

/// <summary>
/// Applies a team color to specific MATERIAL SLOTS on manually-assigned
/// renderers. Built for Mixamo SkinnedMeshRenderers where one mesh has several
/// material slots (body, body1, skin, gear) and only some of them should take
/// the army color.
///
/// Material handling:
///   Uses <c>Renderer.materials</c> (instance materials) so the original
///   project <c>.mat</c> assets are never modified. The renderer gets per-
///   instance copies of every slot, and only the indexes you listed are
///   tinted; untouched slots keep their original color.
///
/// Shader properties:
///   Tries URP Lit's <c>_BaseColor</c> first, falls back to the built-in
///   <c>_Color</c> for Standard / Sprite shaders. Both are set unconditionally
///   so the call works on either pipeline.
///
/// Color source:
///   When a <see cref="PlayerFactionManager"/> exists in the scene (set up by
///   the main menu), the applier reads <c>PlayerFactionManager.SelectedColor</c>
///   on OnEnable and subscribes to <c>OnColorChanged</c> so any future menu
///   change repaints the soldier live. The Inspector <see cref="teamColor"/>
///   field is only used as a fallback in preview scenes that have no manager.
///
/// Usage:
///   1. Attach this component to <c>SoldierVisualRoot</c>.
///   2. In the Inspector, expand <see cref="teamColorSlots"/>, click +.
///   3. Drag the renderer (e.g. Ch15's SkinnedMeshRenderer) into the
///      <c>renderer</c> field of the new slot entry.
///   4. Expand <c>materialIndexes</c>, click +, type the slot index(es) to
///      tint (e.g. <c>0</c> for Ch15_body, <c>1</c> for Ch15_body1, or both).
///   5. <see cref="teamColor"/> is now optional — it's only the fallback
///      color when no PlayerFactionManager is in the scene. In normal play
///      the menu's choice wins.
///
/// What this script does NOT do:
///   • Pick renderers or slots automatically.
///   • Modify or destroy the original <c>.mat</c> assets.
///   • Touch gameplay components (NavMeshAgent, UnitCombat, Health, etc.).
/// </summary>
[DisallowMultipleComponent]
public class TeamColorApplier : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Team Color")]
    [Tooltip("The color applied to every slot listed in teamColorSlots. Set this " +
             "in the Inspector for a static team color, or call ApplyTeamColor at " +
             "runtime to change it on the fly.")]
    [ColorUsage(showAlpha: false)]
    public Color teamColor = new Color(0.20f, 0.55f, 1.00f);  // default blue

    [Header("Targets")]
    [Tooltip("Per-renderer list of material slots to recolor. Drag a Renderer into " +
             "'renderer' and list the index(es) of the slot(s) that should take the " +
             "team color. Slots not listed keep their original material.")]
    public List<RendererMaterialSlot> teamColorSlots = new List<RendererMaterialSlot>();

    [Tooltip("Per-renderer list of material slots painted with a FIXED color at " +
             "OnEnable. These slots are independent of the team-color system — " +
             "useful for boots, dark trim, or any permanent non-team detail.")]
    public List<FixedColorSlot> fixedColorSlots = new List<FixedColorSlot>();

    [Header("Behaviour")]
    [Tooltip("FALLBACK ONLY. If a PlayerFactionManager exists in the scene, the " +
             "applier uses that manager's SelectedColor and ignores this toggle. " +
             "When no manager is present (preview scene, test rig), and this is " +
             "ticked, ApplyTeamColor is called once at OnEnable using the Inspector " +
             "teamColor value above.")]
    public bool applyOnStart = true;

    // ------------------------------------------------------------------ //
    // Shader property IDs — cached once.
    // ------------------------------------------------------------------ //

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId     = Shader.PropertyToID("_Color");

    // ------------------------------------------------------------------ //

    // ------------------------------------------------------------------ //
    // Owner-aware color binding (Phase 10.13).
    //
    // The color painted onto the slots is resolved each time from the
    // entity's canonical owner — never from the LOCAL client's
    // PlayerFactionManager.SelectedColor. Resolution order
    // (PlayerFactionManager.GetColorForOwner):
    //   1. MultiplayerColors[ownerPlayerId] (set by MatchStart).
    //   2. PlayerFactionManager.SelectedColor (only when ownerId == 0 and
    //      no MP slot is registered — i.e. legacy single-player path).
    //   3. Default slot color.
    //
    // We subscribe to MultiplayerColors.OnColorsChanged so a late MatchStart
    // still triggers a repaint (mirror of TeamColorMarker). The legacy
    // PlayerFactionManager.OnColorChanged subscription is kept for SP, but
    // the callback re-resolves from the owner — we never trust the color
    // it passes as-is, because that color is THIS client's local pick.
    // ------------------------------------------------------------------ //

    private bool subscribedPFM;
    private bool subscribedMP;

    private void OnEnable()
    {
        // Fixed-color slots are independent of any team-color flow.
        ApplyFixedColors();

        // Stay current with MP slot-color changes.
        MultiplayerColors.OnColorsChanged += HandleColorsChanged;
        subscribedMP = true;

        // SP backward-compat: if the legacy faction manager exists, listen
        // for its color changes too. The callback re-resolves from owner,
        // so the color it passes is ignored — it just triggers a refresh.
        PlayerFactionManager mgr = PlayerFactionManager.Instance;
        if (mgr != null)
        {
            mgr.OnColorChanged += HandleFactionColorChanged;
            subscribedPFM = true;
        }

        // Initial paint. At this point the spawning prefab's GameEntity
        // typically still has ownerPlayerId = 0 (the prefab default) —
        // CommandDispatcher.ApplyOwnership runs AFTER Instantiate and
        // re-paints us via OwnerColorApplier.ApplyToEntity → ForceRepaint.
        // If that hook hasn't fired yet (preview scene, designer-placed
        // unit), we still get the right color via the owner-aware
        // resolution below.
        if (applyOnStart || mgr != null)
            ResolveAndApply();
    }

    private void OnDisable()
    {
        if (subscribedMP)
        {
            MultiplayerColors.OnColorsChanged -= HandleColorsChanged;
            subscribedMP = false;
        }
        if (subscribedPFM)
        {
            PlayerFactionManager mgr = PlayerFactionManager.Instance;
            if (mgr != null) mgr.OnColorChanged -= HandleFactionColorChanged;
            subscribedPFM = false;
        }
    }

    private void HandleColorsChanged() => ResolveAndApply();
    private void HandleFactionColorChanged(Color _) => ResolveAndApply();

    /// <summary>
    /// Public façade for an owner-aware repaint. Called by
    /// <see cref="OwnerColorApplier.ApplyToEntity"/> after
    /// <see cref="GameEntity.ApplyOwnership"/> has stamped the canonical
    /// owner, so the body slots flip to the right color the instant the
    /// dispatcher's post-spawn step runs.
    /// </summary>
    public void ForceRepaint() => ResolveAndApply();

    private void ResolveAndApply()
    {
        // Resolve owner from a sibling or ancestor GameEntity. The applier
        // lives on a visual-root child (SoldierVisualRoot) so we walk up.
        GameEntity ge = GetComponent<GameEntity>();
        if (ge == null) ge = GetComponentInParent<GameEntity>();

        int ownerId = ge != null ? ge.ownerPlayerId : GameEntity.PlayerOwnerId;
        Color resolved = PlayerFactionManager.GetColorForOwner(ownerId);
        ApplyTeamColor(resolved);
    }

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Paint the listed material slots on each assigned renderer with
    /// <paramref name="color"/>. Indexes outside the renderer's Materials
    /// array are skipped with a warning. Stores <paramref name="color"/> in
    /// <see cref="teamColor"/> so the Inspector reflects the active value.
    /// </summary>
    public void ApplyTeamColor(Color color)
    {
        teamColor = color;

        if (teamColorSlots == null || teamColorSlots.Count == 0)
        {
            Debug.LogWarning($"[TeamColorApplier] '{name}': teamColorSlots is empty — " +
                             "nothing to recolor. Add a slot in the Inspector.", this);
            return;
        }

        for (int s = 0; s < teamColorSlots.Count; s++)
        {
            RendererMaterialSlot slot = teamColorSlots[s];

            if (slot == null || slot.renderer == null)
            {
                Debug.LogWarning($"[TeamColorApplier] '{name}': slot {s} has no renderer assigned — skipping.", this);
                continue;
            }

            if (slot.materialIndexes == null || slot.materialIndexes.Count == 0)
            {
                Debug.LogWarning($"[TeamColorApplier] '{name}': slot {s} ('{slot.renderer.name}') " +
                                 "has no material indexes listed — skipping. Add at least one index " +
                                 "(e.g. 0 for Element 0).", this);
                continue;
            }

            PaintSlots(slot.renderer, slot.materialIndexes, color, $"slot {s}");
        }
    }

    /// <summary>
    /// Paint every slot in <see cref="fixedColorSlots"/> with its own fixed
    /// color. Called once at OnEnable. Independent of PlayerFactionManager —
    /// these slots are never updated when the menu color changes.
    /// </summary>
    public void ApplyFixedColors()
    {
        if (fixedColorSlots == null || fixedColorSlots.Count == 0) return;

        for (int s = 0; s < fixedColorSlots.Count; s++)
        {
            FixedColorSlot slot = fixedColorSlots[s];
            if (slot == null || slot.renderer == null)
            {
                Debug.LogWarning($"[TeamColorApplier] '{name}': fixed slot {s} has no renderer assigned — skipping.", this);
                continue;
            }
            if (slot.materialIndexes == null || slot.materialIndexes.Count == 0)
            {
                Debug.LogWarning($"[TeamColorApplier] '{name}': fixed slot {s} ('{slot.renderer.name}') " +
                                 "has no material indexes listed — skipping.", this);
                continue;
            }
            PaintSlots(slot.renderer, slot.materialIndexes, slot.fixedColor, $"fixed slot {s}");
        }
    }

    /// <summary>
    /// Shared paint routine — writes <paramref name="color"/> into the listed
    /// indexes of <paramref name="renderer"/>.materials. If a slot's material
    /// is null (e.g. broken/pink prefab reference), a runtime URP Lit material
    /// is instantiated in its place so the color is still visible.
    /// </summary>
    private void PaintSlots(Renderer renderer, List<int> indexes, Color color, string label)
    {
        // Renderer.materials returns INSTANCE copies — the .mat assets on
        // disk are not touched. Read once, mutate selected entries, write
        // back to be defensive against future Unity changes.
        Material[] instances = renderer.materials;

        for (int i = 0; i < indexes.Count; i++)
        {
            int idx = indexes[i];
            if (idx < 0 || idx >= instances.Length)
            {
                Debug.LogWarning($"[TeamColorApplier] '{name}': {label} ('{renderer.name}') " +
                                 $"index {idx} is out of range — renderer has {instances.Length} " +
                                 "material(s). Skipping.", this);
                continue;
            }

            Material mat = instances[idx];
            if (mat == null)
            {
                // Null slot (pink in the editor). Instantiate a fresh URP Lit
                // material with the requested color so the slot becomes visible
                // and matches the requested team / fixed color.
                instances[idx] = CreateRuntimeMaterial(color);
                continue;
            }

            // URP Lit uses _BaseColor; Standard uses _Color. Set both —
            // Unity silently ignores the property name the shader doesn't expose.
            if (mat.HasProperty(BaseColorId)) mat.SetColor(BaseColorId, color);
            if (mat.HasProperty(ColorId))     mat.SetColor(ColorId,     color);
        }

        renderer.materials = instances;
    }

    private static Material CreateRuntimeMaterial(Color color)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        Material m = sh != null ? new Material(sh) { name = "TeamColorRuntimeMat" }
                                : new Material(Shader.Find("Hidden/InternalErrorShader"));
        if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, color);
        if (m.HasProperty(ColorId))     m.SetColor(ColorId,     color);
        return m;
    }

    // ------------------------------------------------------------------ //
    // Inspector helpers — right-click the component header in Edit or Play
    // mode to invoke these without entering Play mode first.
    // ------------------------------------------------------------------ //

    /// <summary>Re-apply <see cref="teamColor"/> using the current slot list.
    /// Convenient when tweaking colors in the Inspector during Play.</summary>
    [ContextMenu("Apply Team Color Now")]
    public void ApplyTeamColorNow() => ApplyTeamColor(teamColor);

    /// <summary>Re-apply every <see cref="fixedColorSlots"/> entry's fixed color.
    /// Useful when tweaking those colors in the Inspector during Play.</summary>
    [ContextMenu("Apply Fixed Colors Now")]
    public void ApplyFixedColorsNow() => ApplyFixedColors();

    /// <summary>Quick reset — paints every listed slot white so you can see
    /// the "before" state when comparing colors.</summary>
    [ContextMenu("Reset To White")]
    public void ResetToWhite() => ApplyTeamColor(Color.white);
}
