using UnityEngine;

/// <summary>
/// Click-feedback markers for player commands — a quick expanding ground ring
/// with a soft centre glow where the player right-clicked. Green for move
/// orders, red pulse for attack orders. Built procedurally (no assets), auto
/// destroyed, purely local — only the issuing client sees its own markers.
///
/// Usage:
///     CommandMarkerVFX.MoveMarker(groundPoint);
///     CommandMarkerVFX.AttackPing(targetPosition);
/// </summary>
public static class CommandMarkerVFX
{
    private static readonly Color MoveGreen = new Color(0.25f, 1f, 0.4f, 0.9f);
    private static readonly Color AttackRed = new Color(1f, 0.25f, 0.2f, 0.95f);

    public static void MoveMarker(Vector3 groundPos)  => Spawn(groundPos, MoveGreen, 1.5f, false);
    public static void AttackPing(Vector3 groundPos)  => Spawn(groundPos, AttackRed, 1.9f, true);

    // ------------------------------------------------------------------ //

    private static void Spawn(Vector3 pos, Color color, float maxRadius, bool doublePulse)
    {
        var go = new GameObject("CommandMarkerFX");
        go.transform.position = new Vector3(pos.x, pos.y + 0.06f, pos.z);
        var runner = go.AddComponent<MarkerRunner>();
        runner.color = color;
        runner.maxRadius = maxRadius;
        runner.pulses = doublePulse ? 2 : 1;
    }

    /// <summary>
    /// Draws an expanding flat ring (LineRenderer circle) + fading centre dot.
    /// One short-lived component per click; destroys itself.
    /// </summary>
    private class MarkerRunner : MonoBehaviour
    {
        public Color color;
        public float maxRadius = 1.5f;
        public int   pulses = 1;

        private const float PulseTime = 0.45f;
        private const int   Segments  = 36;

        private LineRenderer ring;
        private GameObject   dot;
        private Material     mat;
        private float        t;

        private void Start()
        {
            mat = new Material(Shader.Find("Sprites/Default")) { color = color };

            ring = gameObject.AddComponent<LineRenderer>();
            ring.useWorldSpace = false;
            ring.loop          = true;
            ring.positionCount = Segments;
            ring.startWidth    = 0.10f;
            ring.endWidth      = 0.10f;
            ring.material      = mat;
            ring.startColor    = color;
            ring.endColor      = color;
            ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ring.receiveShadows    = false;

            // Soft centre dot (small quad with the shared soft circle).
            dot = GameObject.CreatePrimitive(PrimitiveType.Quad);
            dot.name = "CenterGlow";
            Object.Destroy(dot.GetComponent<Collider>());
            dot.transform.SetParent(transform, false);
            dot.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            dot.transform.localScale    = Vector3.one * 0.6f;
            var dr = dot.GetComponent<MeshRenderer>();
            dr.material = new Material(Shader.Find("Sprites/Default"))
            { mainTexture = SoftCircleTex.Get(), color = color };
            dr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            dr.receiveShadows = false;
        }

        private void Update()
        {
            t += Time.deltaTime;
            float total = PulseTime * pulses;
            if (t >= total) { Destroy(gameObject); return; }

            float k = (t % PulseTime) / PulseTime;       // 0→1 per pulse
            float radius = Mathf.Lerp(0.15f, maxRadius, EaseOut(k));
            float alpha  = (1f - k);

            SetRingRadius(radius);
            var c = new Color(color.r, color.g, color.b, color.a * alpha);
            ring.startColor = c; ring.endColor = c;
            if (dot != null)
                dot.GetComponent<MeshRenderer>().material.color =
                    new Color(color.r, color.g, color.b, color.a * alpha * 0.7f);
        }

        private void SetRingRadius(float r)
        {
            for (int i = 0; i < Segments; i++)
            {
                float a = i / (float)Segments * Mathf.PI * 2f;
                ring.SetPosition(i, new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
            }
        }

        private static float EaseOut(float x) => 1f - (1f - x) * (1f - x);
    }
}
