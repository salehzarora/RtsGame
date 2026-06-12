using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural combat VFX factory — muzzle flashes, impact sparks, explosions,
/// building damage smoke, and projectile trails, all built from code with the
/// built-in soft-circle particle texture. No prefab/scene wiring required, so
/// every weapon and every scene gets the effects with one-line hooks.
///
/// Design rules:
///   • Templates are built ONCE (lazily) and cached; per-event cost is a
///     single Instantiate of a small ParticleSystem + auto Destroy.
///   • Purely local visuals — safe in multiplayer (every client renders its
///     own copies; no network traffic, no gameplay state).
///   • Particle counts are RTS-budget tiny (12–48 per event).
///   • All systems use URP-compatible unlit particle materials.
///
/// Hook points (one line each):
///   • UnitCombat.ShowTracer / BuildingTurretCombat.ShowTracer → MuzzleFlash
///   • projectile Launch(...) → AddProjectileTrail
///   • projectile impact → Explosion(pos, size)
///   • Health.TakeDamage → HitFeedback (sparks + building smoke thresholds)
///   • Health.Heal → UpdateDamageSmoke (clears smoke on repair)
///   • Health.Die → DeathFeedback (sized by unit category)
/// </summary>
public static class CombatVFX
{
    // ------------------------------------------------------------------ //
    // Tuning (kept together so feel can be adjusted in one place)
    // ------------------------------------------------------------------ //

    private const float MuzzleLife    = 0.10f;
    private const float ImpactLife    = 0.45f;
    private const float ExpSmallLife  = 0.9f;
    private const float ExpMedLife    = 1.3f;
    private const float ExpLargeLife  = 2.0f;

    private static readonly Color FlashYellow = new Color(1f, 0.85f, 0.35f);
    private static readonly Color FireOrange  = new Color(1f, 0.45f, 0.10f);
    private static readonly Color SmokeGray   = new Color(0.34f, 0.33f, 0.32f);

    // ------------------------------------------------------------------ //
    // Shared resources
    // ------------------------------------------------------------------ //

    private static Texture2D _softTex;
    private static Material  _addMat;     // bright additive-look particles
    private static Material  _smokeMat;   // alpha-blended dark smoke

    private static Texture2D SoftTex
    {
        get
        {
            // Generated radial-gradient circle — the builtin
            // "Default-Particle.psd" fails to load in Unity 6, and a
            // procedural texture has zero dependencies anyway.
            if (_softTex == null)
            {
                const int S = 64;
                _softTex = new Texture2D(S, S, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp, name = "SoftCircle" };
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x, y), new Vector2(S / 2f, S / 2f)) / (S / 2f);
                        float a = Mathf.Clamp01(1f - d);
                        a = a * a * (3f - 2f * a);   // smoothstep falloff
                        _softTex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                _softTex.Apply();
            }
            return _softTex;
        }
    }

    private static Material BrightMat
    {
        get
        {
            if (_addMat == null) _addMat = MakeParticleMat(Color.white);
            return _addMat;
        }
    }

    private static Material SmokeMat
    {
        get
        {
            if (_smokeMat == null) _smokeMat = MakeParticleMat(new Color(1f, 1f, 1f, 0.8f));
            return _smokeMat;
        }
    }

    private static Material MakeParticleMat(Color tint)
    {
        // Sprites/Default: transparent, textured, vertex-colored, and works
        // under URP without per-keyword blend setup. Configuring URP's
        // Particles/Unlit surface via code was unreliable (particles rendered
        // as opaque untextured quads) — this path is what the editor's own
        // sprite/particle defaults use.
        Shader sh = Shader.Find("Sprites/Default");
        var m = new Material(sh) { mainTexture = SoftTex, color = tint };
        return m;
    }

    // ------------------------------------------------------------------ //
    // Public API — muzzle flash
    // ------------------------------------------------------------------ //

    public static void MuzzleFlash(Vector3 pos, Vector3 dir)
    {
        GameObject go = NewPS("MuzzleFlashFX", pos, out ParticleSystem ps);
        if (dir.sqrMagnitude > 0.001f) go.transform.rotation = Quaternion.LookRotation(dir);

        var main = ps.main;
        main.duration       = MuzzleLife;
        main.loop           = false;
        main.startLifetime  = new ParticleSystem.MinMaxCurve(0.04f, MuzzleLife);
        main.startSpeed     = new ParticleSystem.MinMaxCurve(2f, 5f);
        main.startSize      = new ParticleSystem.MinMaxCurve(0.18f, 0.40f);
        main.startColor     = new ParticleSystem.MinMaxGradient(FlashYellow, FireOrange);

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle     = 18f;
        shape.radius    = 0.03f;

        Burst(ps, 7);
        FadeOut(ps);
        AddLight(go, FlashYellow, 2.5f, 5f, MuzzleLife);
        PlayAndDispose(go, ps, MuzzleLife + 0.3f);
    }

    // ------------------------------------------------------------------ //
    // Public API — impact sparks (hitscan hits, light projectile hits)
    // ------------------------------------------------------------------ //

    public static void Impact(Vector3 pos)
    {
        GameObject go = NewPS("ImpactFX", pos, out ParticleSystem ps);
        var main = ps.main;
        main.duration      = ImpactLife;
        main.loop          = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, ImpactLife);
        main.startSpeed    = new ParticleSystem.MinMaxCurve(1.5f, 4.5f);
        main.startSize     = new ParticleSystem.MinMaxCurve(0.06f, 0.16f);
        main.startColor    = new ParticleSystem.MinMaxGradient(FlashYellow, new Color(0.6f, 0.55f, 0.5f));
        main.gravityModifier = 0.6f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius    = 0.05f;

        Burst(ps, 10);
        FadeOut(ps);
        PlayAndDispose(go, ps, ImpactLife + 0.3f);
    }

    // ------------------------------------------------------------------ //
    // Public API — explosions. size: 0 small, 1 medium, 2 large.
    // ------------------------------------------------------------------ //

    public static void Explosion(Vector3 pos, int size)
    {
        float scale = size <= 0 ? 1f : size == 1 ? 1.8f : 3.2f;
        float life  = size <= 0 ? ExpSmallLife : size == 1 ? ExpMedLife : ExpLargeLife;

        // --- fireball ---------------------------------------------------- //
        GameObject go = NewPS("ExplosionFX", pos, out ParticleSystem fire);
        var fm = fire.main;
        fm.duration      = 0.3f;
        fm.loop          = false;
        fm.startLifetime = new ParticleSystem.MinMaxCurve(0.28f, 0.60f);
        fm.startSpeed    = new ParticleSystem.MinMaxCurve(1.2f * scale, 3.5f * scale);
        fm.startSize     = new ParticleSystem.MinMaxCurve(0.45f * scale, 1.05f * scale);
        fm.startColor    = new ParticleSystem.MinMaxGradient(FlashYellow, FireOrange);
        var fs = fire.shape; fs.shapeType = ParticleSystemShapeType.Sphere; fs.radius = 0.12f * scale;
        Burst(fire, (short)(size <= 0 ? 10 : size == 1 ? 18 : 30));
        FadeOut(fire); ShrinkOver(fire);

        // --- white-hot core flash (very short, sells the detonation) ------ //
        var coreGO = new GameObject("Core");
        coreGO.transform.SetParent(go.transform, false);
        var core = coreGO.AddComponent<ParticleSystem>();
        core.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ConfigureRenderer(coreGO, BrightMat);
        var cm = core.main;
        cm.duration      = 0.1f;
        cm.loop          = false;
        cm.startLifetime = new ParticleSystem.MinMaxCurve(0.08f, 0.14f);
        cm.startSpeed    = 0f;
        cm.startSize     = new ParticleSystem.MinMaxCurve(0.9f * scale, 1.4f * scale);
        cm.startColor    = new Color(1f, 0.98f, 0.9f, 1f);
        Burst(core, 2);
        FadeOut(core);
        core.Play();

        // --- debris chunks (dark, gravity, bounce-free) ------------------- //
        if (size >= 1)
        {
            var debGO = new GameObject("Debris");
            debGO.transform.SetParent(go.transform, false);
            var deb = debGO.AddComponent<ParticleSystem>();
            deb.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ConfigureRenderer(debGO, SmokeMat);
            var dr = debGO.GetComponent<ParticleSystemRenderer>();
            dr.renderMode = ParticleSystemRenderMode.Mesh;
            dr.mesh = CubeMesh;
            var dm = deb.main;
            dm.duration       = 0.15f;
            dm.loop           = false;
            dm.startLifetime  = new ParticleSystem.MinMaxCurve(0.6f, 1.2f);
            dm.startSpeed     = new ParticleSystem.MinMaxCurve(3.0f * scale, 6.5f * scale);
            dm.startSize      = new ParticleSystem.MinMaxCurve(0.07f * scale, 0.16f * scale);
            dm.startColor     = new ParticleSystem.MinMaxGradient(
                new Color(0.15f, 0.13f, 0.11f), new Color(0.32f, 0.28f, 0.22f));
            dm.gravityModifier = 1.6f;
            dm.startRotation3D = true;
            dm.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            dm.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            dm.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            var ds = deb.shape;
            ds.shapeType = ParticleSystemShapeType.Cone;
            ds.angle = 38f; ds.radius = 0.1f * scale;
            debGO.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f); // cone up
            var drot = deb.rotationOverLifetime;
            drot.enabled = true;
            drot.x = new ParticleSystem.MinMaxCurve(-6f, 6f);
            Burst(deb, (short)(size == 1 ? 8 : 14));
            deb.Play();
        }

        // --- ground shockwave ring (fast expanding flat disc) ------------- //
        if (size >= 1)
        {
            var ringGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
            ringGO.name = "Shockwave";
            Object.Destroy(ringGO.GetComponent<Collider>());
            ringGO.transform.SetParent(go.transform, false);
            ringGO.transform.position = new Vector3(pos.x, 0.06f, pos.z);
            ringGO.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            var rr = ringGO.GetComponent<MeshRenderer>();
            rr.material = MakeParticleMat(new Color(1f, 0.8f, 0.5f, 0.55f));
            rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rr.receiveShadows = false;
            var grow = ringGO.AddComponent<ExpandAndFade>();
            grow.endScale = 3.4f * scale;
            grow.seconds  = 0.45f;
        }

        // Thump the camera — bigger blast, bigger kick (distance-attenuated).
        CameraShakeFX.ShakeAt(pos, size <= 0 ? 0.12f : size == 1 ? 0.3f : 0.55f);

        // --- smoke column (child system) ---------------------------------- //
        var smokeGO = new GameObject("Smoke");
        smokeGO.transform.SetParent(go.transform, false);
        var smoke = smokeGO.AddComponent<ParticleSystem>();
        smoke.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ConfigureRenderer(smokeGO, SmokeMat);
        var sm = smoke.main;
        sm.duration      = 0.5f;
        sm.loop          = false;
        sm.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.5f, life);
        sm.startSpeed    = new ParticleSystem.MinMaxCurve(0.6f, 1.6f);
        sm.startSize     = new ParticleSystem.MinMaxCurve(0.5f * scale, 1.1f * scale);
        sm.startColor    = new ParticleSystem.MinMaxGradient(SmokeGray, new Color(0.4f, 0.38f, 0.36f));
        var ss = smoke.shape; ss.shapeType = ParticleSystemShapeType.Sphere; ss.radius = 0.15f * scale;
        var vel = smoke.velocityOverLifetime; vel.enabled = true;
        vel.x = new ParticleSystem.MinMaxCurve(0f, 0f);   // all axes must share
        vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);   // the same curve mode
        vel.y = new ParticleSystem.MinMaxCurve(0.8f, 1.8f);
        Burst(smoke, (short)(size <= 0 ? 6 : size == 1 ? 10 : 18));
        FadeOut(smoke); GrowOver(smoke);

        AddLight(go, FireOrange, 5f * scale, 7f * scale, 0.35f);
        smoke.Play();
        PlayAndDispose(go, fire, life + 0.6f);
    }

    // ------------------------------------------------------------------ //
    // Public API — projectile trail
    // ------------------------------------------------------------------ //

    public static void AddProjectileTrail(GameObject projectile, Color color, float width)
    {
        if (projectile == null || projectile.GetComponent<TrailRenderer>() != null) return;
        var tr = projectile.AddComponent<TrailRenderer>();
        tr.time          = 0.28f;
        tr.startWidth    = width;
        tr.endWidth      = 0f;
        tr.material      = BrightMat;
        tr.startColor    = color;
        tr.endColor      = new Color(color.r, color.g, color.b, 0f);
        tr.numCapVertices = 2;
        tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    // ------------------------------------------------------------------ //
    // Public API — Health hooks (hit feedback, building smoke, death)
    // ------------------------------------------------------------------ //

    /// <summary>Per-hit feedback: small sparks + (buildings) smoke thresholds.</summary>
    public static void HitFeedback(Health h)
    {
        if (h == null) return;
        Impact(h.transform.position + Vector3.up * 1.0f);
        UpdateDamageSmoke(h);
    }

    /// <summary>
    /// Buildings emit smoke below 50% health, heavier below 25%. Cleared on
    /// repair above 50%. Smoke object is a child, so destruction cleans it up.
    /// </summary>
    public static void UpdateDamageSmoke(Health h)
    {
        if (h == null || h.GetComponent<Building>() == null) return;
        float ratio = h.maxHealth > 0f ? h.CurrentHealth / h.maxHealth : 1f;

        Transform existing = h.transform.Find("DamageSmokeFX");
        if (ratio > 0.5f)
        {
            if (existing != null) Object.Destroy(existing.gameObject);
            return;
        }

        ParticleSystem ps;
        if (existing == null)
        {
            var go = new GameObject("DamageSmokeFX");
            go.transform.SetParent(h.transform, false);
            go.transform.localPosition = Vector3.up * 1.5f;
            ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ConfigureRenderer(go, SmokeMat);
            var main = ps.main;
            main.loop          = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.4f, 2.4f);
            main.startSpeed    = new ParticleSystem.MinMaxCurve(0.5f, 1.1f);
            main.startSize     = new ParticleSystem.MinMaxCurve(0.5f, 1.0f);
            main.startColor    = new ParticleSystem.MinMaxGradient(SmokeGray, new Color(0.15f, 0.14f, 0.13f));
            main.maxParticles  = 40;
            var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.5f;
            var vel = ps.velocityOverLifetime; vel.enabled = true;
            vel.x = new ParticleSystem.MinMaxCurve(0f, 0f);
            vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);
            vel.y = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
            FadeOut(ps); GrowOver(ps);
            ps.Play();
        }
        else ps = existing.GetComponent<ParticleSystem>();

        if (ps != null)
        {
            var em = ps.emission;
            em.rateOverTime = ratio <= 0.25f ? 14f : 6f;   // heavier smoke when critical
        }
    }

    /// <summary>Death feedback sized by what died: building → big explosion +
    /// scorch + lingering smoke, vehicle/aircraft → medium + aftermath,
    /// infantry → small dust.</summary>
    public static void DeathFeedback(Health h)
    {
        if (h == null) return;
        Vector3 pos = h.transform.position + Vector3.up * 0.5f;
        Vector3 ground = h.transform.position;

        if (h.GetComponent<Building>() != null)
        {
            Explosion(pos, 2);
            ScorchMark(ground, 2.4f);
            LingeringSmoke(ground, 12f, 1.6f);
            return;
        }
        var cat = h.GetComponent<UnitCategory>();
        bool heavy = (cat != null && (cat.category == UnitCategory.Category.Vehicle ||
                                      cat.category == UnitCategory.Category.Aircraft)) ||
                     h.GetComponent<AirUnitController>() != null;
        if (heavy)
        {
            Explosion(pos, 1);
            ScorchMark(ground, 1.4f);
            LingeringSmoke(ground, 8f, 1.0f);
            return;
        }
        Explosion(pos, 0);                                  // infantry: small puff
    }

    // ------------------------------------------------------------------ //
    // Public API — battlefield aftermath (scorch marks + lingering smoke)
    // ------------------------------------------------------------------ //

    /// <summary>Dark soft scorch decal flat on the ground; fades out over
    /// ~25 s. Cheap: one quad + a tiny fade component.</summary>
    public static void ScorchMark(Vector3 groundPos, float radius)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "ScorchMarkFX";
        Object.Destroy(go.GetComponent<Collider>());
        go.transform.position   = new Vector3(groundPos.x, 0.03f, groundPos.z);
        go.transform.rotation   = Quaternion.Euler(90f, Random.Range(0f, 360f), 0f);
        go.transform.localScale = Vector3.one * (radius * 2f);

        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = MakeParticleMat(new Color(0.05f, 0.045f, 0.04f, 0.85f));
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;

        var fade = go.AddComponent<FadeAndDie>();
        fade.holdSeconds = 12f;
        fade.fadeSeconds = 14f;
    }

    /// <summary>Smoke column that keeps emitting for <paramref name="seconds"/>
    /// then dissipates — wreck/ruin aftermath.</summary>
    public static void LingeringSmoke(Vector3 groundPos, float seconds, float scale)
    {
        var go = new GameObject("LingeringSmokeFX");
        go.transform.position = groundPos + Vector3.up * 0.4f;
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ConfigureRenderer(go, SmokeMat);
        var main = ps.main;
        main.duration      = seconds;
        main.loop          = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.2f);
        main.startSpeed    = new ParticleSystem.MinMaxCurve(0.5f, 1.2f);
        main.startSize     = new ParticleSystem.MinMaxCurve(0.6f * scale, 1.3f * scale);
        main.startColor    = new ParticleSystem.MinMaxGradient(SmokeGray, new Color(0.2f, 0.19f, 0.18f));
        main.maxParticles  = 60;
        var em = ps.emission; em.rateOverTime = 7f;
        var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.35f * scale;
        var vel = ps.velocityOverLifetime; vel.enabled = true;
        vel.x = new ParticleSystem.MinMaxCurve(0f, 0f);
        vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);
        vel.y = new ParticleSystem.MinMaxCurve(0.9f, 1.7f);
        FadeOut(ps); GrowOver(ps);
        ps.Play();
        Object.Destroy(go, seconds + 4f);
    }

    // Shared unit cube for debris mesh particles.
    private static Mesh _cubeMesh;
    private static Mesh CubeMesh
    {
        get
        {
            if (_cubeMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                Object.Destroy(tmp);
            }
            return _cubeMesh;
        }
    }

    /// <summary>Scales a quad up while fading its material out, then destroys
    /// it — used for the explosion ground shockwave.</summary>
    private class ExpandAndFade : MonoBehaviour
    {
        public float endScale = 3f;
        public float seconds  = 0.45f;
        private float _t;
        private MeshRenderer _r;
        private Color _c;

        private void Awake()
        {
            _r = GetComponent<MeshRenderer>();
            if (_r != null) _c = _r.material.color;
        }

        private void Update()
        {
            _t += Time.deltaTime;
            float k = Mathf.Clamp01(_t / seconds);
            float ease = 1f - (1f - k) * (1f - k);
            transform.localScale = Vector3.one * Mathf.Lerp(0.3f, endScale, ease);
            if (_r != null)
                _r.material.color = new Color(_c.r, _c.g, _c.b, _c.a * (1f - k));
            if (k >= 1f) Destroy(gameObject);
        }
    }

    /// <summary>Minimal alpha-fade-then-destroy helper for aftermath decals.
    /// One instance per scorch mark; trivially cheap.</summary>
    private class FadeAndDie : MonoBehaviour
    {
        public float holdSeconds = 10f;
        public float fadeSeconds = 10f;
        private float _t;
        private MeshRenderer _r;
        private Color _c;

        private void Awake()
        {
            _r = GetComponent<MeshRenderer>();
            if (_r != null) _c = _r.sharedMaterial.color;
        }

        private void Update()
        {
            _t += Time.deltaTime;
            if (_t <= holdSeconds) return;
            float k = 1f - Mathf.Clamp01((_t - holdSeconds) / fadeSeconds);
            if (_r != null)
                _r.material.color = new Color(_c.r, _c.g, _c.b, _c.a * k);
            if (k <= 0f) Destroy(gameObject);
        }
    }

    // ------------------------------------------------------------------ //
    // Internals
    // ------------------------------------------------------------------ //

    private static GameObject NewPS(string name, Vector3 pos, out ParticleSystem ps)
    {
        var go = new GameObject(name);
        go.transform.position = pos;
        ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        // A fresh ParticleSystem starts playing the moment it's added;
        // configuring main.duration on a playing system raises an assert.
        // Stop+clear first, configure, then the caller Play()s explicitly.
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var em = ps.emission; em.rateOverTime = 0f;
        ConfigureRenderer(go, BrightMat);
        return go;
    }

    private static void ConfigureRenderer(GameObject go, Material mat)
    {
        var r = go.GetComponent<ParticleSystemRenderer>();
        if (r == null) r = go.AddComponent<ParticleSystemRenderer>();
        r.material = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    private static void Burst(ParticleSystem ps, short count)
    {
        var em = ps.emission;
        em.rateOverTime = 0f;
        em.SetBursts(new[] { new ParticleSystem.Burst(0f, count) });
    }

    private static void FadeOut(ParticleSystem ps)
    {
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.85f, 0.4f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(g);
    }

    private static void ShrinkOver(ParticleSystem ps)
    {
        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.25f));
    }

    private static void GrowOver(ParticleSystem ps)
    {
        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.6f));
    }

    private static void AddLight(GameObject go, Color c, float intensity, float range, float life)
    {
        var lgo = new GameObject("FlashLight");
        lgo.transform.SetParent(go.transform, false);
        var l = lgo.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = c; l.intensity = intensity; l.range = range;
        l.shadows = LightShadows.None;
        Object.Destroy(lgo, life);
    }

    private static void PlayAndDispose(GameObject go, ParticleSystem ps, float life)
    {
        ps.Play();
        Object.Destroy(go, life);
    }
}
