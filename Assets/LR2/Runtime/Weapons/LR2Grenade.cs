using System.Collections.Generic;
using LR2.Audio;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using LR2.FX;
using UnityEngine;

namespace LR2.Weapons
{
    /// <summary>
    /// B136 + B140 frag grenade, thrown by <see cref="SoldierController"/> at the synced release frame of Kinemation's
    /// throw animation. Bounces, cooks for <see cref="fuse"/> seconds, then detonates with a large multi-layer fireball,
    /// rising smoke plume, radial shockwave ring, sparks, HDRP flash, heavy shockwave rumble + crack boom, and camera shake.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class LR2Grenade : MonoBehaviour
    {
        [SerializeField] private float fuse = 2.8f;
        [Tooltip("Full damage inside this radius (m).")]
        [SerializeField] private float lethalRadius = 4.2f;
        [Tooltip("No damage beyond this radius (m).")]
        [SerializeField] private float maxRadius = 10.5f;
        [SerializeField] private float maxDamage = 190f;
        [SerializeField] private float force = 140f;
        [SerializeField] private float audibleRadius = 140f;

        public const float AftermathFireDuration = 4.2f;

        public float MaxBlastRadius => maxRadius;

        private Combatant _thrower;
        private float _explodeAt;
        private bool _exploded;
        private float _lastBounceSound = -1f;
        private static readonly Collider[] Overlap = new Collider[128];
        private static AudioClip _megaBoomClip;

        public static AudioClip GetMegaBoomClip()
        {
            if (_megaBoomClip != null) return _megaBoomClip;
            const int sr = 44100;
            const float dur = 2.8f;
            int n = Mathf.RoundToInt(sr * dur);
            float[] data = new float[n];
            var rng = new System.Random(1945);
            float lpLow = 0f, lpMid = 0f, prev = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                lpLow += 0.014f * (white - lpLow);
                lpMid += 0.095f * (white - lpMid);
                // Massive initial overpressure transient + pitch-dropping 92->28 Hz sub-bass thump + rolling thunderous rumble
                float crack = Mathf.Exp(-t * 28f) * (white * 0.72f + Mathf.Sin(2f * Mathf.PI * 195f * t) * 0.45f);
                float subF = Mathf.Lerp(92f, 28f, Mathf.Clamp01(t * 2.8f));
                float sub = Mathf.Sin(2f * Mathf.PI * subF * t) * Mathf.Exp(-t * 2.9f) * Mathf.Clamp01(t / 0.004f) * 1.15f;
                float pressureBody = lpMid * Mathf.Exp(-t * 3.8f) * 2.2f;
                float rumbleTail = (lpLow * 4.4f + lpMid * 0.7f) * Mathf.Exp(-Mathf.Max(0f, t - 0.12f) * 1.35f) * Mathf.Clamp01((dur - t) / 0.45f);
                float raw = (float)System.Math.Tanh((crack + sub + pressureBody + rumbleTail) * 1.55f);
                float dc = raw - prev * 0.996f;
                prev = raw * 0.04f;
                data[i] = Mathf.Clamp(raw - dc * 0.02f, -0.98f, 0.98f);
            }
            _megaBoomClip = AudioClip.Create("LR2_FragMegaBoom", n, 1, sr, false);
            _megaBoomClip.SetData(data, 0);
            return _megaBoomClip;
        }

        public static LR2Grenade Throw(GameObject prefab, Combatant thrower, Vector3 position, Vector3 velocity)
        {
            GameObject go = prefab != null ? Instantiate(prefab, position, Random.rotation) : BuildFallback(position);
            var g = go.GetComponent<LR2Grenade>() ?? go.AddComponent<LR2Grenade>();
            g._thrower = thrower;
            g._explodeAt = Time.time + g.fuse;
            var rb = go.GetComponent<Rigidbody>();
            rb.linearVelocity = velocity;
            rb.angularVelocity = Random.insideUnitSphere * 10f;
            if (thrower != null)
            {
                var mine = go.GetComponentsInChildren<Collider>();
                foreach (var c in thrower.GetComponentsInChildren<Collider>(true))
                    foreach (var m in mine)
                        if (c != null && m != null) Physics.IgnoreCollision(c, m, true);
            }
            return g;
        }

        private static GameObject BuildFallback(Vector3 position)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "LR2_Grenade";
            go.transform.position = position;
            go.transform.localScale = new Vector3(0.065f, 0.05f, 0.065f);
            go.layer = LR2Layers.Debris;
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.4f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            return go;
        }

        private void Update()
        {
            if (!_exploded && Time.time >= _explodeAt) Explode();
        }

        private void OnCollisionEnter(Collision c)
        {
            if (_exploded || Time.time - _lastBounceSound < 0.12f || c.relativeVelocity.magnitude < 1.5f) return;
            _lastBounceSound = Time.time;
            var s = LR2CommonSounds.Instance;
            if (s != null) LR2Audio.PlayAt(s.ammoPickup, transform.position, Mathf.Clamp01(c.relativeVelocity.magnitude / 10f) * 0.6f, 0f, true, 1.6f);
            GameEvents.RaiseNoise(_thrower, transform.position, 12f, NoiseType.Impact);
        }

        private void Explode()
        {
            _exploded = true;
            Vector3 pos = transform.position + Vector3.up * 0.12f;

            var hit = new HashSet<Health>();
            int n = Physics.OverlapSphereNonAlloc(pos, maxRadius, Overlap, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var col = Overlap[i];
                if (col == null) continue;
                var health = col.GetComponentInParent<Health>();
                if (health == null || !health.IsAlive || !hit.Add(health)) continue;
                var victim = health.GetComponent<Combatant>();
                Vector3 target = victim != null ? victim.ChestPosition : col.bounds.center;
                float dist = Vector3.Distance(pos, target);
                float k = dist <= lethalRadius ? 1f : Mathf.Clamp01(1f - (dist - lethalRadius) / Mathf.Max(0.1f, maxRadius - lethalRadius));
                if (k <= 0f) continue;
                if (Physics.Linecast(pos, target, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) k *= 0.25f;
                var info = new DamageInfo
                {
                    amount = maxDamage * k * k,
                    type = DamageType.Explosion,
                    zone = HitZone.UpperTorso,
                    attacker = _thrower,
                    source = gameObject,
                    weaponName = "Frag",
                    point = target,
                    normal = (target - pos).normalized,
                    direction = (target - pos).normalized,
                    distance = dist,
                    force = force * k
                };
                health.ApplyDamage(ref info);
            }

            for (int i = 0; i < n; i++)
            {
                var rb = Overlap[i] != null ? Overlap[i].attachedRigidbody : null;
                if (rb != null && !rb.isKinematic && rb.gameObject != gameObject)
                    rb.AddExplosionForce(force * 5.2f, pos, maxRadius, 0.9f, ForceMode.Impulse);
            }

            var fx = LR2FxLibrary.Instance;
            if (fx != null)
            {
                if (fx.packExplosion != null)
                {
                    // B147: Triple-stacked multi-lobed explosion core for massive cinematic silhouette
                    PackFx.Spawn(fx.packExplosion, pos, Quaternion.identity, 5.2f, 6.5f);
                    PackFx.Spawn(fx.packExplosion, pos + Vector3.up * 1.4f, Quaternion.Euler(0f, 115f, 0f), 4.2f, 5.8f);
                    PackFx.Spawn(fx.packExplosion, pos + Vector3.up * 0.5f, Quaternion.Euler(0f, 235f, 0f), 3.6f, 5.2f);
                }
                SpawnMegaBlastFx(pos, fx);
            }
            ExplosionFlash.Spawn(pos + Vector3.up * 0.9f, new Color(1f, 0.52f, 0.14f), 320000f, 38f, 0.65f);

            // Huge 3-layer detonation audio: sub-bass cinema mega boom + close/medium C4 blast body + raining debris tail.
            LR2Audio.PlayClip(GetMegaBoomClip(), pos, 1.0f, Random.Range(0.93f, 1.04f), 14f, 220f, 0.85f, AudioCategory.Weapons, 16, 0f, null, true);
            var s = LR2CommonSounds.Instance;
            if (s != null)
            {
                LR2Audio.PlayAt(s.droneExplode, pos, 1f, 0f, false, Random.Range(0.82f, 0.90f));
                LR2Audio.PlayAt(s.orbExplode, pos, 0.95f, 0.15f);
            }

            GameEvents.RaiseNoise(_thrower, pos, audibleRadius, NoiseType.Explosion);
            Debug.Log($"[LR2] Frag exploded at {pos} (thrower {(_thrower != null ? _thrower.name : "none")}), {hit.Count} soldier(s) in range");

            var local = Combatant.FindLocalPlayer();
            if (local != null)
            {
                float d = Vector3.Distance(local.EyePosition, pos);
                if (d < 28f) LR2Audio.Duck(0.85f);
                if (d < 36f) CameraShake.AddTrauma(Mathf.Lerp(1.0f, 0.14f, d / 36f));
            }
            Destroy(gameObject);
        }

        private static void SpawnMegaBlastFx(Vector3 pos, LR2FxLibrary fx)
        {
            var root = new GameObject("GrenadeMegaBlastFX");
            root.transform.position = pos;
            Destroy(root, 9.5f);

            // 1. Towering central roaring fireball mushroom
            if (fx.fire != null)
            {
                var go = new GameObject("FireballCore");
                go.transform.SetParent(root.transform, false);
                var ps = go.AddComponent<ParticleSystem>();
                var main = ps.main;
                main.playOnAwake = false;
                main.loop = false;
                main.duration = 0.6f;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.85f, 1.85f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(4.5f, 12.5f);
                main.startSize = new ParticleSystem.MinMaxCurve(2.6f, 5.4f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.gravityModifier = -0.45f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var em = ps.emission;
                em.enabled = false;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = 1.4f;
                var sol = ps.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.75f, 1f, 2.15f));
                var noise = ps.noise;
                noise.enabled = true;
                noise.strength = 1.4f;
                noise.frequency = 0.7f;
                var col = ps.colorOverLifetime;
                col.enabled = true;
                var g = new Gradient();
                g.SetKeys(
                    new[] {
                        new GradientColorKey(new Color(1f, 0.96f, 0.68f), 0f),
                        new GradientColorKey(new Color(1f, 0.44f, 0.05f), 0.35f),
                        new GradientColorKey(new Color(0.72f, 0.08f, 0.01f), 0.75f),
                        new GradientColorKey(new Color(0.18f, 0.04f, 0.02f), 1f)
                    },
                    new[] {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(0.95f, 0.35f),
                        new GradientAlphaKey(0.6f, 0.72f),
                        new GradientAlphaKey(0f, 1f)
                    });
                col.color = g;
                LR2FxLibrary.ApplyFlipbook(ps, fx.fireTiles, 1f);
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = fx.fire;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ps.Emit(55);
            }

            // 2. Blinding hot molten core flash glow
            if (fx.fireGlow != null)
            {
                var go = new GameObject("BlastCoreGlow");
                go.transform.SetParent(root.transform, false);
                var ps = go.AddComponent<ParticleSystem>();
                var main = ps.main;
                main.playOnAwake = false;
                main.loop = false;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.55f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(1.0f, 4.0f);
                main.startSize = new ParticleSystem.MinMaxCurve(4.5f, 8.5f);
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var em = ps.emission;
                em.enabled = false;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.8f;
                var col = ps.colorOverLifetime;
                col.enabled = true;
                var g = new Gradient();
                g.SetKeys(
                    new[] { new GradientColorKey(new Color(1f, 0.95f, 0.75f), 0f), new GradientColorKey(new Color(1f, 0.42f, 0.06f), 0.55f), new GradientColorKey(new Color(0.8f, 0.15f, 0.02f), 1f) },
                    new[] { new GradientAlphaKey(0.95f, 0f), new GradientAlphaKey(0.65f, 0.4f), new GradientAlphaKey(0f, 1f) });
                col.color = g;
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = fx.fireGlow;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ps.Emit(14);
            }

            // 3. Radial ground fiery shockwave ring
            if (fx.fire != null)
            {
                var go = new GameObject("FireShockRing");
                go.transform.SetParent(root.transform, false);
                var ps = go.AddComponent<ParticleSystem>();
                var main = ps.main;
                main.playOnAwake = false;
                main.loop = false;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.60f, 1.25f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(10f, 20f);
                main.startSize = new ParticleSystem.MinMaxCurve(1.8f, 3.4f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var em = ps.emission;
                em.enabled = false;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 80f;
                shape.radius = 0.45f;
                go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
                var lim = ps.limitVelocityOverLifetime;
                lim.enabled = true;
                lim.dampen = 0.28f;
                lim.limit = 3.5f;
                var col = ps.colorOverLifetime;
                col.enabled = true;
                var g = new Gradient();
                g.SetKeys(
                    new[] { new GradientColorKey(new Color(1f, 0.90f, 0.50f), 0f), new GradientColorKey(new Color(1f, 0.36f, 0.05f), 0.45f), new GradientColorKey(new Color(0.28f, 0.10f, 0.04f), 1f) },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.80f, 0.42f), new GradientAlphaKey(0f, 1f) });
                col.color = g;
                LR2FxLibrary.ApplyFlipbook(ps, fx.fireTiles, 1f);
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = fx.fire;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ps.Emit(44);
            }

            // 4. Huge dark billowing mushroom smoke plume lit warm at the base
            if (fx.smoke != null)
            {
                var go = new GameObject("SmokePlume");
                go.transform.SetParent(root.transform, false);
                var ps = go.AddComponent<ParticleSystem>();
                var main = ps.main;
                main.playOnAwake = false;
                main.loop = false;
                main.duration = 0.6f;
                main.startLifetime = new ParticleSystem.MinMaxCurve(4.2f, 7.0f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(4.5f, 11.5f);
                main.startSize = new ParticleSystem.MinMaxCurve(3.2f, 6.4f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.gravityModifier = -0.36f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.42f, 0.24f, 0.14f, 0.90f), new Color(0.14f, 0.14f, 0.15f, 0.80f));
                var em = ps.emission;
                em.enabled = false;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = 1.2f;
                var sol = ps.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.65f, 1f, 2.6f));
                var col = ps.colorOverLifetime;
                col.enabled = true;
                var g = new Gradient();
                g.SetKeys(
                    new[] { new GradientColorKey(new Color(0.42f, 0.24f, 0.14f), 0f), new GradientColorKey(new Color(0.18f, 0.16f, 0.16f), 0.35f), new GradientColorKey(new Color(0.14f, 0.14f, 0.15f), 1f) },
                    new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.92f, 0.08f), new GradientAlphaKey(0.58f, 0.55f), new GradientAlphaKey(0f, 1f) });
                col.color = g;
                LR2FxLibrary.ApplyFlipbook(ps, fx.smokeTiles, 1f);
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = fx.smoke;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ps.Emit(42);
            }

            // 5. B147: Lingering crater fire + fiery ground glow + warm HDRP flame illumination
            Vector3 groundPos = pos;
            if (Physics.Raycast(pos + Vector3.up, Vector3.down, out var gHit, 4f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                groundPos = gHit.point + Vector3.up * 0.08f;

            if (fx.fire != null)
            {
                var go = new GameObject("AftermathFire");
                go.transform.SetParent(root.transform, false);
                go.transform.position = groundPos;
                go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
                var ps = go.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main;
                main.playOnAwake = false;
                main.loop = false;
                main.duration = AftermathFireDuration;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.55f, 1.15f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(1.6f, 3.8f);
                main.startSize = new ParticleSystem.MinMaxCurve(1.2f, 2.3f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.gravityModifier = -0.30f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var em = ps.emission;
                em.enabled = true;
                em.rateOverTime = 36f;
                var shape = ps.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 26f;
                shape.radius = 1.55f;
                var noise = ps.noise;
                noise.enabled = true;
                noise.strength = 0.8f;
                noise.frequency = 0.9f;
                var col = ps.colorOverLifetime;
                col.enabled = true;
                var g = new Gradient();
                g.SetKeys(
                    new[] {
                        new GradientColorKey(new Color(1f, 0.92f, 0.55f), 0f),
                        new GradientColorKey(new Color(1f, 0.42f, 0.06f), 0.45f),
                        new GradientColorKey(new Color(0.65f, 0.08f, 0.01f), 1f)
                    },
                    new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.92f, 0.14f), new GradientAlphaKey(0.70f, 0.65f), new GradientAlphaKey(0f, 1f) });
                col.color = g;
                LR2FxLibrary.ApplyFlipbook(ps, fx.fireTiles, 1f);
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = fx.fire;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ps.Play();
            }

            if (fx.fireGlow != null)
            {
                var go = new GameObject("AftermathFireGlow");
                go.transform.SetParent(root.transform, false);
                go.transform.position = groundPos + Vector3.up * 0.25f;
                var ps = go.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main;
                main.playOnAwake = false;
                main.loop = false;
                main.duration = AftermathFireDuration;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.8f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.8f);
                main.startSize = new ParticleSystem.MinMaxCurve(1.8f, 3.2f);
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var em = ps.emission;
                em.enabled = true;
                em.rateOverTime = 12f;
                var shape = ps.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 1.2f;
                var col = ps.colorOverLifetime;
                col.enabled = true;
                var g = new Gradient();
                g.SetKeys(
                    new[] { new GradientColorKey(new Color(1f, 0.55f, 0.12f), 0f), new GradientColorKey(new Color(0.9f, 0.22f, 0.03f), 1f) },
                    new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.75f, 0.2f), new GradientAlphaKey(0f, 1f) });
                col.color = g;
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = fx.fireGlow;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ps.Play();
            }

            ExplosionFlash.Spawn(groundPos + Vector3.up * 0.6f, new Color(1f, 0.42f, 0.08f), 42000f, 18f, AftermathFireDuration + 0.4f);

            // 6. Hot flying shrapnel & sparks
            if (fx.embers != null)
            {
                var go = new GameObject("ShrapnelSparks");
                go.transform.SetParent(root.transform, false);
                var ps = go.AddComponent<ParticleSystem>();
                var main = ps.main;
                main.playOnAwake = false;
                main.loop = false;
                main.startLifetime = new ParticleSystem.MinMaxCurve(1.0f, 2.4f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(12f, 30f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.18f);
                main.gravityModifier = 1.6f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.startColor = new Color(1f, 0.72f, 0.28f, 1f);
                var em = ps.emission;
                em.enabled = false;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = 0.25f;
                var col = ps.collision;
                col.enabled = true;
                col.type = ParticleSystemCollisionType.World;
                col.bounce = 0.35f;
                col.dampen = 0.3f;
                col.collidesWith = LR2Layers.EnvironmentMask;
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = fx.embers;
                r.renderMode = ParticleSystemRenderMode.Stretch;
                r.velocityScale = 0.05f;
                r.lengthScale = 2.4f;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ps.Emit(95);
            }
        }
    }
}
