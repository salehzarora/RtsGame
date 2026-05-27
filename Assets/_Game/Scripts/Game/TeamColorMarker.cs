using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Paints the MAIN BODY renderers of a Player-team unit/building with the
/// player's chosen team color (read from <see cref="PlayerFactionManager"/>).
///
/// Renderer roles:
///   • <see cref="bodyColorRenderers"/> — painted with the team color. These
///     are the renderers that should clearly carry the army's identity (hull,
///     turret, fuselage, body cube, helmet, etc.).
///   • <see cref="detailRenderers"/>   — recorded for documentation only;
///     never painted. Use for tracks, wheels, cannons, windows, missiles —
///     parts that should keep their original neutral material.
///   • <see cref="ignoreRenderers"/>   — recorded for documentation only;
///     never painted. Use for the HealthBar, SelectionCircle, FirePoint,
///     AmmoIndicator, and other gameplay/system children whose color is
///     authoritatively controlled elsewhere.
///
/// Lifecycle:
///   • OnEnable: registers with the manager and pulls the current color.
///   • OnDisable: unregisters.
///   • When the player picks a new color in the menu, the manager calls
///     <see cref="ApplyColor"/> on every registered marker.
///
/// What it deliberately does NOT do:
///   • Instantiate materials. Color is applied via a MaterialPropertyBlock,
///     so shared materials are not duplicated and enemy prefabs that share
///     the same material asset are not affected.
///   • Recolor enemy units (Team = Enemy) — those have their own colors via
///     UnitColorMarker and per-prefab materials.
///   • Touch the unit's HealthBar, SelectionCircle, or shadows. Those should
///     be listed in <see cref="ignoreRenderers"/> if anywhere.
/// </summary>
[DisallowMultipleComponent]
public class TeamColorMarker : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    public enum Team { Player, Enemy }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Team")]
    [Tooltip("Only Player markers register with the PlayerFactionManager and " +
             "receive the player's selected color. Enemy is a no-op slot kept " +
             "for future enemy-faction theming.")]
    public Team team = Team.Player;

    [Header("Renderer Roles")]
    [Tooltip("MAIN BODY renderers — these get painted with the player's team color. " +
             "Use for hull, turret, fuselage, body cube, helmet — anything that " +
             "should clearly read as the army's color.")]
    [FormerlySerializedAs("accentRenderers")]
    public List<Renderer> bodyColorRenderers = new List<Renderer>();

    [Tooltip("Detail / neutral renderers — kept on the marker for documentation " +
             "but NEVER painted. Use for tracks, wheels, cannons, windows, " +
             "missiles, blades, exhaust stacks.")]
    public List<Renderer> detailRenderers = new List<Renderer>();

    [Tooltip("Ignored renderers — system children whose color is owned by " +
             "another component (HealthBar, SelectionCircle, FirePoint, etc.). " +
             "Listed here for documentation only; the marker never touches them.")]
    public List<Renderer> ignoreRenderers = new List<Renderer>();

    [Header("Fallback (no PlayerFactionManager in scene)")]
    [Tooltip("Color applied at Awake when no PlayerFactionManager exists yet " +
             "(e.g. previewing the prefab in isolation in the editor).")]
    [ColorUsage(false)] public Color fallbackColor = new Color(0.7f, 0.7f, 0.7f);

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private MaterialPropertyBlock mpb;

    // Shader property ids — cached once for both URP Lit (_BaseColor) and
    // the Standard pipeline (_Color). Setting both via the property block is
    // cheap; Unity silently ignores names the shader doesn't expose.
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId     = Shader.PropertyToID("_Color");

    // ------------------------------------------------------------------ //

    private void OnEnable()
    {
        // Enemy markers are a no-op for now — the field exists so enemy prefabs
        // can later opt in to a separate enemy-team palette without breaking.
        if (team != Team.Player) return;

        // Phase 4 MP path: the per-slot palette in MultiplayerColors wins
        // over the local PlayerFactionManager pick so a Player 0 unit looks
        // identical on Client A and Client B. We still register with the
        // faction manager (so SP color changes during a match repaint
        // everything), but the actual paint uses the per-owner colour.
        if (PlayerFactionManager.Instance != null)
            PlayerFactionManager.Instance.Register(this);

        // Subscribe to MultiplayerColors so a late-arriving MatchStart event
        // repaints us automatically.
        MultiplayerColors.OnColorsChanged += RepaintFromContext;

        RepaintFromContext();
    }

    private void OnDisable()
    {
        if (team != Team.Player) return;
        if (PlayerFactionManager.Instance != null)
            PlayerFactionManager.Instance.Unregister(this);

        MultiplayerColors.OnColorsChanged -= RepaintFromContext;
    }

    /// <summary>
    /// Public façade for <see cref="RepaintFromContext"/>. Phase 9 callers
    /// (most importantly <see cref="GameEntity.ApplyOwnership"/>) invoke this
    /// when the entity's owner changes mid-life, so the marker re-evaluates
    /// the slot palette and pulls the correct owner colour. Without this,
    /// a unit produced under owner 0 keeps the Player-0 tint even after its
    /// owner is reassigned to player 1.
    /// </summary>
    public void ForceRepaint() => RepaintFromContext();

    /// <summary>
    /// Resolve which colour this marker should currently display, then call
    /// <see cref="ApplyColor"/>.
    ///   • MP + owner registered → MultiplayerColors slot colour.
    ///   • PlayerFactionManager present → its SelectedColor (legacy SP path).
    ///   • Otherwise → <see cref="fallbackColor"/> for editor preview.
    /// </summary>
    private void RepaintFromContext()
    {
        // Resolve the canonical owner via sibling GameEntity. Missing
        // GameEntity → owner 0 (single-player default).
        GameEntity ge = GetComponent<GameEntity>();
        if (ge == null) ge = GetComponentInParent<GameEntity>();
        int ownerId = ge != null ? ge.ownerPlayerId : 0;

        // MP per-slot palette wins when available.
        if (MultiplayerColors.TryGetForOwner(ownerId, out Color slotColor))
        {
            ApplyColor(slotColor);
            return;
        }

        // SP fallback path — same as before this phase.
        if (PlayerFactionManager.Instance != null)
        {
            ApplyColor(PlayerFactionManager.Instance.SelectedColor);
            return;
        }

        ApplyColor(fallbackColor);
    }

    // ------------------------------------------------------------------ //
    // Public API — called by PlayerFactionManager
    // ------------------------------------------------------------------ //

    /// <summary>Re-paint every body renderer with <paramref name="color"/>.
    /// Detail and ignore renderers are not touched.
    ///
    /// Phase 4: when this marker's owner has a slot colour registered in
    /// <see cref="MultiplayerColors"/>, the slot colour OVERRIDES whatever
    /// the caller passed in. This is how
    /// <see cref="PlayerFactionManager"/>'s legacy "push local color to all
    /// markers" loop stops recolouring opponent units to the local pick.
    /// </summary>
    public void ApplyColor(Color color)
    {
        // MP override: any registered slot colour for THIS marker's owner
        // beats the argument. Keeps Player 0 blue on every client even when
        // Client B's PlayerFactionManager pushes red.
        GameEntity ge = GetComponent<GameEntity>();
        if (ge == null) ge = GetComponentInParent<GameEntity>();
        if (ge != null && MultiplayerColors.TryGetForOwner(ge.ownerPlayerId, out Color slotColor))
            color = slotColor;

        if (mpb == null) mpb = new MaterialPropertyBlock();

        for (int i = 0; i < bodyColorRenderers.Count; i++)
        {
            Renderer r = bodyColorRenderers[i];
            if (r == null) continue;

            r.GetPropertyBlock(mpb);
            mpb.SetColor(BaseColorId, color);   // URP Lit
            mpb.SetColor(ColorId,     color);   // Standard / Sprites
            r.SetPropertyBlock(mpb);
        }
    }
}
