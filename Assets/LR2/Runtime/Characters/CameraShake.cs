// Lead Rush II - camera shake for the local player's view (B29, user request 2026-11-17).
//
// Two layers, both tunable in the Inspector of the auto-created "LR2_CameraShake" object:
//  1. Trauma: a 0..1 value that sources add to and that decays over time. The shake amount is trauma^2, driven by smooth Perlin noise
//     on pitch / yaw / roll plus a little position (the "trauma" model from Squirrel Eiserloh's GDC 2016 talk on camera juice).
//  2. Jolts: short directional kicks on a damped spring (reload mag slap, bolt release, flinch away from a hit, landing).
//
// The offset is applied only while HDRP renders the view camera and is removed right after, so aim, bullets, AI line of sight and the
// audio listener never shake. Sources: your own shots, hits you take, near misses, reload cues, landings.

using LR2.Combat;
using LR2.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace LR2.Characters
{
    [DefaultExecutionOrder(10000)]
    public sealed class CameraShake : MonoBehaviour
    {
        public static CameraShake Instance { get; private set; }
        /// <summary>Current trauma 0..1 (QA reads this; no reflection needed).</summary>
        public float Trauma => _trauma;

        [Header("Trauma shake")]
        [Tooltip("Trauma lost per second.")] public float decayPerSecond = 1.8f;
        [Tooltip("Max pitch/yaw shake in degrees at full trauma.")] public float maxAngle = 1.8f;
        [Tooltip("Max roll shake in degrees at full trauma.")] public float maxRoll = 2.6f;
        [Tooltip("Max position shake in metres at full trauma.")] public float maxOffset = 0.01f;
        [Tooltip("Noise speed (higher = more jittery).")] public float frequency = 20f;

        [Header("Sources (trauma added)")]
        public float rifleShot = 0.075f;
        public float pistolShot = 0.05f;
        [Tooltip("Multiplier on shot shake while aiming down sights.")] public float adsShotMultiplier = 0.55f;
        public float hitTakenBase = 0.22f;
        [Tooltip("Extra trauma per point of damage taken.")] public float hitTakenPerDamage = 0.006f;
        public float headshotTakenExtra = 0.15f;
        public float nearMiss = 0.1f;

        [Header("Jolts (spring kicks, degrees per second)")]
        public float magOutJolt = 60f;
        public float magInJolt = 140f;
        public float boltJolt = 120f;
        public float hitFlinchJolt = 170f;
        public float landingJoltPerMs = 22f;
        [Tooltip("Spring stiffness of the jolt layer.")] public float joltStiffness = 260f;
        [Tooltip("Spring damping of the jolt layer.")] public float joltDamping = 20f;

        private float _trauma;
        private Vector3 _jolt, _joltVel; // degrees (pitch, yaw, roll)
        private SoldierController _local;
        private float _nextFind;
        private float _prevY, _prevVy;
        private bool _havePrevY;

        private Camera _applied;
        private Quaternion _savedRot;
        private Vector3 _savedPos;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Instance != null) return;
            var go = new GameObject("LR2_CameraShake");
            DontDestroyOnLoad(go);
            go.AddComponent<CameraShake>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnEnable()
        {
            GameEvents.ShotFired += OnShot;
            GameEvents.Hit += OnHit;
            GameEvents.NearMiss += OnNearMiss;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
        }

        private void OnDisable()
        {
            GameEvents.ShotFired -= OnShot;
            GameEvents.Hit -= OnHit;
            GameEvents.NearMiss -= OnNearMiss;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
            Restore();
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ public API
        /// <summary>Adds 0..1 trauma to the local view (scaled by the Camera Shake setting).</summary>
        public static void AddTrauma(float amount)
        {
            if (Instance == null || amount <= 0f) return;
            Instance._trauma = Mathf.Clamp01(Instance._trauma + amount * LR2Settings.CameraShake);
        }

        /// <summary>Kicks the view: x = pitch (positive looks down), y = yaw, z = roll, in degrees per second.</summary>
        public static void AddJolt(Vector3 degreesPerSecond)
        {
            if (Instance == null) return;
            Instance._joltVel += degreesPerSecond * LR2Settings.CameraShake;
        }

        /// <summary>Reload cue from the local player's gun: 0 = mag out, 1 = mag in, 2 = bolt / charging handle.</summary>
        public static void ReloadCue(int cue)
        {
            var s = Instance;
            if (s == null) return;
            float side = (Time.frameCount & 1) == 0 ? 1f : -1f;
            switch (cue)
            {
                case 0: AddJolt(new Vector3(-0.3f, 0.2f * side, 0.6f) * s.magOutJolt); AddTrauma(0.03f); break;
                case 1: AddJolt(new Vector3(1f, 0.15f * side, -0.45f) * s.magInJolt); AddTrauma(0.06f); break;
                default: AddJolt(new Vector3(0.55f, -0.25f, 0.8f) * s.boltJolt); AddTrauma(0.05f); break;
            }
        }

        // ------------------------------------------------------------------ sources
        private bool IsLocal(Combatant c) => c != null && c.IsLocalPlayer;

        private void OnShot(ShotEvent e)
        {
            if (!IsLocal(e.shooter)) return;
            var soldier = FindLocal();
            var gun = soldier != null ? soldier.ActiveFirearm : null;
            AddTrauma(ShotTrauma(gun != null ? gun.Definition : null, soldier != null && soldier.IsAiming, e.suppressed));
        }

        /// <summary>Per-shot trauma the live player gets; the killcam calls it for replayed shots so both feel the same.</summary>
        private float ShotTrauma(LR2.Weapons.WeaponDefinition def, bool aiming, bool suppressed)
        {
            bool pistol = def != null && def.weaponClass == LR2.Weapons.WeaponClass.Pistol;
            float t = pistol ? pistolShot : rifleShot;
            if (def != null) t *= Mathf.Clamp(def.cameraKick.magnitude / 0.14f, 0.6f, 1.8f); // heavier kick = more shake
            if (aiming) t *= adsShotMultiplier;
            if (suppressed) t *= 0.85f;
            return t;
        }

        /// <summary>Killcam: the same shot shake as the live player for a replayed shot of <paramref name="def"/>.</summary>
        public static void ReplayShot(LR2.Weapons.WeaponDefinition def, bool aiming)
        {
            if (Instance == null) return;
            AddTrauma(Instance.ShotTrauma(def, aiming, def != null && def.suppressed));
        }

        private void OnHit(HitEvent e)
        {
            if (!IsLocal(e.victim) || e.damage <= 0f) return;
            float t = hitTakenBase + e.damage * hitTakenPerDamage + (e.zone == HitZone.Head ? headshotTakenExtra : 0f);
            AddTrauma(t);

            // Flinch away from the shooter: the head snaps up and turns away from the incoming side.
            var cam = FindLocal() != null ? _local.ViewCamera : null;
            float side = 0f;
            if (cam != null && e.attacker != null)
            {
                Vector3 local = cam.transform.InverseTransformDirection(e.attacker.transform.position - cam.transform.position);
                side = local.x >= 0f ? -1f : 1f;
            }
            float k = Mathf.Clamp01(0.45f + e.damage / 60f);
            AddJolt(new Vector3(-0.8f, 0.6f * side, 0.9f * side) * hitFlinchJolt * k);
        }

        private void OnNearMiss(NearMissEvent e)
        {
            if (!IsLocal(e.listener)) return;
            AddTrauma(nearMiss * Mathf.Clamp01(1.5f - e.distance));
        }

        private SoldierController FindLocal()
        {
            if (_local != null && _local.isActiveAndEnabled && _local.IsLocalPlayer) return _local;
            _local = null;
            if (Time.unscaledTime < _nextFind) return null;
            _nextFind = Time.unscaledTime + 0.5f;
            foreach (var s in FindObjectsByType<SoldierController>())
                if (s != null && s.IsLocalPlayer) { _local = s; _havePrevY = false; break; }
            return _local;
        }

        // ------------------------------------------------------------------ simulation
        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            _trauma = Mathf.Max(0f, _trauma - decayPerSecond * dt);

            // damped spring back to rest (sub-stepped for stability at low frame rates)
            int steps = Mathf.Clamp(Mathf.CeilToInt(dt / 0.004f), 1, 8);
            float h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                _joltVel += (-_jolt * joltStiffness - _joltVel * joltDamping) * h;
                _jolt += _joltVel * h;
            }

            // landing: the view camera's fall speed drops to ~0 within a frame (ignores teleports and respawns)
            var soldier = FindLocal();
            var cam = soldier != null ? soldier.ViewCamera : null;
            if (cam != null)
            {
                float y = cam.transform.position.y;
                if (_havePrevY)
                {
                    float dy = y - _prevY;
                    float vy = Mathf.Abs(dy) < 2f ? dy / dt : 0f;
                    if (_prevVy < -4.5f && vy > -1f) AddJolt(new Vector3(Mathf.Min(-_prevVy, 14f) * landingJoltPerMs, 0f, 0f));
                    _prevVy = vy;
                }
                _prevY = y;
                _havePrevY = true;
            }
        }

        private void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_applied != null || _local == null || cam != _local.ViewCamera) return;
            float shake = _trauma * _trauma;
            if (shake < 1e-4f && _jolt.sqrMagnitude < 1e-4f) return;
            float t = Time.time * frequency;
            Vector3 e = new Vector3(
                maxAngle * shake * Noise(11.3f, t),
                maxAngle * shake * Noise(47.9f, t),
                maxRoll * shake * Noise(83.1f, t)) + _jolt;
            Vector3 p = maxOffset * shake * new Vector3(Noise(5.1f, t), Noise(19.7f, t), 0f);

            var tr = cam.transform;
            _savedRot = tr.localRotation;
            _savedPos = tr.localPosition;
            tr.localRotation = _savedRot * Quaternion.Euler(e);
            tr.localPosition = _savedPos + p;
            _applied = cam;
        }

        private void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == _applied) Restore();
        }

        private void Restore()
        {
            if (_applied == null) return;
            var tr = _applied.transform;
            tr.localRotation = _savedRot;
            tr.localPosition = _savedPos;
            _applied = null;
        }

        private static float Noise(float seed, float t) => Mathf.PerlinNoise(seed, t) * 2f - 1f;
    }
}
