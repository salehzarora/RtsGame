using UnityEngine;

/// <summary>
/// Movement dust — a small particle system parented at a unit's feet that
/// emits BY DISTANCE TRAVELLED (rate-over-distance), so it costs nothing
/// while idle and needs no per-frame script work at all. Works identically
/// for locally-driven and network-driven (remote ghost) units because the
/// particle system only watches the transform move. Purely visual, MP-safe.
///
/// Usage (once per unit, e.g. from UnitMovement.Start):
///     GroundDustFX.Attach(gameObject, 1f);    // infantry
///     GroundDustFX.Attach(gameObject, 2.2f);  // vehicles — bigger puffs
/// </summary>
public static class GroundDustFX
{
    private static Material _dustMat;

    private static Material DustMat
    {
        get
        {
            if (_dustMat == null)
            {
                Shader sh = Shader.Find("Sprites/Default");
                _dustMat = new Material(sh)
                {
                    mainTexture = SoftCircleTex.Get(),
                    color = new Color(1f, 1f, 1f, 0.65f)
                };
            }
            return _dustMat;
        }
    }

    /// <summary>Attach a dust emitter to <paramref name="unit"/>. Safe to call
    /// twice (no-ops if one exists). <paramref name="scale"/> ~1 = infantry,
    /// ~2 = light vehicle, ~3 = heavy vehicle.</summary>
    public static void Attach(GameObject unit, float scale)
    {
        if (unit == null || unit.transform.Find("MoveDustFX") != null) return;

        var go = new GameObject("MoveDustFX");
        go.transform.SetParent(unit.transform, false);
        go.transform.localPosition = new Vector3(0f, 0.15f, -0.2f * scale);

        var ps = go.AddComponent<ParticleSystem>();

        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.material = DustMat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;

        var main = ps.main;
        main.loop           = true;
        main.startLifetime  = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
        main.startSpeed     = new ParticleSystem.MinMaxCurve(0.15f, 0.45f);
        main.startSize      = new ParticleSystem.MinMaxCurve(0.22f * scale, 0.5f * scale);
        main.startColor     = new ParticleSystem.MinMaxGradient(
            new Color(0.55f, 0.50f, 0.42f, 0.35f),
            new Color(0.62f, 0.58f, 0.50f, 0.28f));
        main.maxParticles   = 24;
        main.simulationSpace = ParticleSystemSimulationSpace.World;  // puffs stay behind

        var em = ps.emission;
        em.rateOverTime     = 0f;
        em.rateOverDistance = 1.6f;          // puff every ~0.6 m of travel

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius    = 0.18f * scale;

        // Slight upward drift + growth, fading out — classic dust look.
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.x = new ParticleSystem.MinMaxCurve(0f, 0f);
        vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);
        vel.y = new ParticleSystem.MinMaxCurve(0.25f, 0.6f);

        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0.8f, 0f), new GradientAlphaKey(0.4f, 0.5f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(g);

        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.7f, 1f, 1.5f));

        ps.Play();
    }
}

/// <summary>Shared procedural soft-circle texture for runtime VFX materials
/// (Unity 6 can't load the builtin Default-Particle.psd).</summary>
public static class SoftCircleTex
{
    private static Texture2D _tex;

    public static Texture2D Get()
    {
        if (_tex == null)
        {
            const int S = 64;
            _tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, name = "SoftCircleShared" };
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(S / 2f, S / 2f)) / (S / 2f);
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a * (3f - 2f * a);
                    _tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            _tex.Apply();
        }
        return _tex;
    }
}
