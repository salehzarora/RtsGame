using UnityEngine;

/// <summary>
/// Lightweight positional camera shake for explosions and heavy weapons.
/// Purely local/visual — never touches gameplay state, safe in multiplayer.
///
/// Usage (one line, no wiring):
///     CameraShakeFX.ShakeAt(worldPos, 0.5f);
///
/// Strength is attenuated by the camera's distance to <c>worldPos</c>, so a
/// far-away barrel pop doesn't move the view but a nearby tank shot thumps.
/// The runner offsets the CAMERA CHILD's local position (the rig parent keeps
/// authoritative position), with critically-damped decay back to rest.
///
/// Setup: none. The runner attaches itself to Camera.main on first use.
/// </summary>
public static class CameraShakeFX
{
    private static ShakeRunner _runner;

    /// <summary>Max world distance at which a shake is still felt.</summary>
    private const float MaxRange = 55f;

    /// <summary>Shake originating at a world position (distance-attenuated).</summary>
    public static void ShakeAt(Vector3 worldPos, float strength)
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        float d = Vector3.Distance(cam.transform.position, worldPos);
        if (d > MaxRange) return;
        float atten = 1f - Mathf.Clamp01(d / MaxRange);

        if (_runner == null)
        {
            _runner = cam.GetComponent<ShakeRunner>();
            if (_runner == null) _runner = cam.gameObject.AddComponent<ShakeRunner>();
        }
        _runner.AddTrauma(strength * atten * atten);
    }

    /// <summary>
    /// Per-camera runner. Trauma decays linearly; offset amplitude is
    /// trauma², which reads as a sharp hit followed by a fast settle.
    /// </summary>
    private class ShakeRunner : MonoBehaviour
    {
        private float   trauma;
        private Vector3 applied;     // last offset we added — removed first each frame
        private float   seedX, seedY;

        private void Awake()
        {
            seedX = Random.value * 100f;
            seedY = Random.value * 200f;
        }

        public void AddTrauma(float t) => trauma = Mathf.Clamp01(trauma + t);

        private void LateUpdate()
        {
            // Remove last frame's offset so rig/zoom scripts keep authority.
            transform.localPosition -= applied;
            applied = Vector3.zero;

            if (trauma <= 0f) return;
            trauma = Mathf.Max(0f, trauma - Time.deltaTime * 1.6f);

            float amp = trauma * trauma * 0.45f;
            float t   = Time.unscaledTime * 22f;
            applied = new Vector3(
                (Mathf.PerlinNoise(seedX, t) - 0.5f) * 2f * amp,
                (Mathf.PerlinNoise(seedY, t) - 0.5f) * 2f * amp,
                0f);
            transform.localPosition += applied;
        }
    }
}
