using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural firing recoil — kicks a unit's VISUAL children backward (and a
/// touch upward) on each shot, then springs back. Works on infantry (upper
/// body jolt), vehicles (chassis rock) and buildings (turret thump) without
/// any prefab wiring: the runner attaches itself on first use and offsets the
/// direct visual children, never the gameplay root, so NavMeshAgent pathing,
/// colliders and network transforms are untouched. Purely local/visual —
/// multiplayer-safe.
///
/// Usage (one line at the weapon's fire moment):
///     RecoilKickFX.Kick(transform, shotDirection, 0.08f);
///
/// <c>shotDirection</c> is the direction the projectile travels; the body is
/// kicked the opposite way. Strength is metres of peak displacement.
/// </summary>
public static class RecoilKickFX
{
    public static void Kick(Transform unitRoot, Vector3 shotDir, float strength)
    {
        if (unitRoot == null || strength <= 0f) return;
        var runner = unitRoot.GetComponent<KickRunner>();
        if (runner == null) runner = unitRoot.gameObject.AddComponent<KickRunner>();

        Vector3 back = -shotDir;
        back.y = 0f;
        if (back.sqrMagnitude < 0.0001f) back = -unitRoot.forward;
        back.Normalize();

        runner.AddImpulse(back * strength + Vector3.up * (strength * 0.25f));
    }

    /// <summary>
    /// Spring runner: keeps one shared offset, applied additively to the
    /// direct children that carry renderers. Offsets are removed before the
    /// new frame's value is added, so animators / turret rotations / health
    /// bars (which only touch rotation or deeper transforms) are unaffected.
    /// </summary>
    private class KickRunner : MonoBehaviour
    {
        private readonly List<Transform> visuals = new List<Transform>();
        private Vector3 offset;          // current spring position
        private Vector3 velocity;        // spring velocity
        private Vector3 lastApplied;

        private const float Stiffness = 220f;   // spring k — snappy return
        private const float Damping   = 16f;    // near-critical damping

        private void Awake()
        {
            // Visual children = direct children that contain a renderer and
            // are not pure-gameplay helpers. Health bars are world-space UI
            // canvases — excluded so they stay glued above the unit.
            foreach (Transform child in transform)
            {
                if (child.GetComponent<Canvas>() != null) continue;
                if (child.GetComponentInChildren<Renderer>(true) == null) continue;
                visuals.Add(child);
            }
        }

        public void AddImpulse(Vector3 impulse)
        {
            // Impulse goes into the spring as instant displacement; the spring
            // pulls it back. Repeated shots accumulate but clamp keeps it sane.
            offset = Vector3.ClampMagnitude(offset + impulse, 0.35f);
        }

        private void LateUpdate()
        {
            // Undo last frame before computing this frame (keeps us additive
            // with respect to anything else moving these children).
            if (lastApplied != Vector3.zero)
                foreach (var v in visuals)
                    if (v != null) v.localPosition -= lastApplied;

            // Integrate the spring back toward zero.
            Vector3 accel = -Stiffness * offset - Damping * velocity;
            velocity += accel * Time.deltaTime;
            offset   += velocity * Time.deltaTime;

            if (offset.sqrMagnitude < 1e-8f && velocity.sqrMagnitude < 1e-6f)
            {
                offset = Vector3.zero;
                velocity = Vector3.zero;
                lastApplied = Vector3.zero;
                return;
            }

            // Apply in LOCAL space so the kick stays relative to the body.
            Vector3 local = transform.InverseTransformVector(offset);
            lastApplied = local;
            foreach (var v in visuals)
                if (v != null) v.localPosition += local;
        }
    }
}
