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
    /// B136 frag grenade, thrown by <see cref="SoldierController"/> at the release frame of Kinemation's throw animation.
    /// Bounces, cooks for <see cref="fuse"/> seconds, then explodes: falloff damage (line of sight to the chest, cover keeps
    /// 25 %), ragdoll force, debris impulse, Particle Pack blast + HDRP flash, the drone blast sound and an AI noise event.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class LR2Grenade : MonoBehaviour
    {
        [SerializeField] private float fuse = 2.8f;
        [Tooltip("Full damage inside this radius (m).")]
        [SerializeField] private float lethalRadius = 3.2f;
        [Tooltip("No damage beyond this radius (m).")]
        [SerializeField] private float maxRadius = 8f;
        [SerializeField] private float maxDamage = 160f;
        [SerializeField] private float force = 90f;
        [SerializeField] private float audibleRadius = 90f;

        private Combatant _thrower;
        private float _explodeAt;
        private bool _exploded;
        private float _lastBounceSound = -1f;
        private static readonly Collider[] Overlap = new Collider[128];

        public static LR2Grenade Throw(GameObject prefab, Combatant thrower, Vector3 position, Vector3 velocity)
        {
            GameObject go = prefab != null ? Instantiate(prefab, position, Random.rotation) : BuildFallback(position);
            var g = go.GetComponent<LR2Grenade>() ?? go.AddComponent<LR2Grenade>();
            g._thrower = thrower;
            g._explodeAt = Time.time + g.fuse;
            var rb = go.GetComponent<Rigidbody>();
            rb.linearVelocity = velocity;
            rb.angularVelocity = Random.insideUnitSphere * 10f;
            // never collide with the thrower's own capsule and hitboxes
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

            // damage: one hit per soldier, from the blast centre to its chest
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
                if (Physics.Linecast(pos, target, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) k *= 0.25f; // behind cover
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

            // loose rigidbodies (props, debris, dropped guns)
            for (int i = 0; i < n; i++)
            {
                var rb = Overlap[i] != null ? Overlap[i].attachedRigidbody : null;
                if (rb != null && !rb.isKinematic && rb.gameObject != gameObject) rb.AddExplosionForce(force * 4f, pos, maxRadius, 0.6f, ForceMode.Impulse);
            }

            var fx = LR2FxLibrary.Instance;
            if (fx != null && fx.packExplosion != null) PackFx.Spawn(fx.packExplosion, pos, Quaternion.identity, 1.6f, 4f);
            ExplosionFlash.Spawn(pos, new Color(1f, 0.62f, 0.32f), 42000f, 14f, 0.3f);
            var s = LR2CommonSounds.Instance;
            if (s != null) LR2Audio.PlayAt(s.droneExplode, pos);
            GameEvents.RaiseNoise(_thrower, pos, audibleRadius, NoiseType.Explosion);
            Debug.Log($"[LR2] Frag exploded at {pos} (thrower {(_thrower != null ? _thrower.name : "none")}), {hit.Count} soldier(s) in range");

            var local = Combatant.FindLocalPlayer();
            if (local != null)
            {
                float d = Vector3.Distance(local.EyePosition, pos);
                if (d < 22f) CameraShake.AddTrauma(Mathf.Lerp(0.85f, 0.1f, d / 22f));
            }
            Destroy(gameObject);
        }
    }
}
