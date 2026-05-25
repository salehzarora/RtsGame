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

        if (PlayerFactionManager.Instance != null)
        {
            PlayerFactionManager.Instance.Register(this);
            ApplyColor(PlayerFactionManager.Instance.SelectedColor);
        }
        else
        {
            // No manager yet — paint the fallback so the prefab looks reasonable
            // when previewed without the game systems wired up.
            ApplyColor(fallbackColor);
        }
    }

    private void OnDisable()
    {
        if (team != Team.Player) return;
        if (PlayerFactionManager.Instance != null)
            PlayerFactionManager.Instance.Unregister(this);
    }

    // ------------------------------------------------------------------ //
    // Public API — called by PlayerFactionManager
    // ------------------------------------------------------------------ //

    /// <summary>Re-paint every body renderer with <paramref name="color"/>.
    /// Detail and ignore renderers are not touched.</summary>
    public void ApplyColor(Color color)
    {
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
