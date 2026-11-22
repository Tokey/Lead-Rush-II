using LR2.Audio;
using LR2.Combat;
using LR2.Core;
using LR2.FX;
using UnityEngine;

namespace LR2.Characters
{
    /// <summary>
    /// Visceral physical hit, guaranteed tactical death audio, and health-recovery cues for soldiers.
    /// Attached to every soldier in <see cref="SoldierController"/> (Awake) so both Robot and SWAT rigs share it.
    /// Avoids fake pitched vocal pain/death grunts in favour of weighty impact thuds, deep tactical death sub-drops,
    /// and clean health-recovery confirmation chimes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PainVoice : MonoBehaviour
    {
        private SoldierController _soldier;
        private Health _health;
        private LR2CommonSounds _common;
        private SurfaceLibrary _surfaces;
        private float _nextHitAudioAt;
        private float _nextRegenSfxAt;
        private float _nextReplenishSfxAt;

        private static AudioClip _hitThudClip;
        private static AudioClip _playerDeathClip;
        private static AudioClip _regenStartClip;
        private static AudioClip _healthReplenishedClip;

        public static bool HasDeathImpactClip
        {
            get
            {
                EnsureClips();
                return _playerDeathClip != null;
            }
        }

        public static bool HasHealthReplenishedClip
        {
            get
            {
                EnsureClips();
                return _healthReplenishedClip != null;
            }
        }

        public static int DeathAudioCount { get; private set; }
        public static int HealthReplenishedCount { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            DeathAudioCount = 0;
            HealthReplenishedCount = 0;
            _hitThudClip = null;
            _playerDeathClip = null;
            _regenStartClip = null;
            _healthReplenishedClip = null;
        }

        private void Awake()
        {
            _soldier = GetComponent<SoldierController>();
            _health = _soldier != null ? _soldier.Health : GetComponent<Health>();
            EnsureLibs();
            EnsureClips();
        }

        private void OnEnable()
        {
            if (_health == null) return;
            _health.Damaged += OnDamaged;
            _health.Died += OnDied;
            _health.RegenStarted += OnRegenStarted;
            _health.HealthReplenished += OnHealthReplenished;
        }

        private void OnDisable()
        {
            if (_health == null) return;
            _health.Damaged -= OnDamaged;
            _health.Died -= OnDied;
            _health.RegenStarted -= OnRegenStarted;
            _health.HealthReplenished -= OnHealthReplenished;
        }

        private void EnsureLibs()
        {
            if (_common == null) _common = LR2CommonSounds.Instance;
            if (_surfaces == null) _surfaces = SurfaceLibrary.Instance;
        }

        private void OnDamaged(DamageInfo info)
        {
            if (_soldier == null || _health == null || !_health.IsAlive) return;
            if (!_soldier.IsLocalPlayer) return; // Bots rely on crisp 3D bullet/armor impacts, no fake vocal grunts
            if (Time.unscaledTime < _nextHitAudioAt) return;

            EnsureLibs();
            EnsureClips();

            float severity = Mathf.Clamp01(info.amount / 45f + (1f - _health.Normalized) * 0.35f);
            float thudVol = Mathf.Lerp(0.42f, 0.80f, severity);
            if (_hitThudClip != null)
            {
                LR2Audio.PlayClip(_hitThudClip, Vector3.zero, thudVol, Random.Range(0.95f, 1.05f),
                    1f, 500f, 0f, AudioCategory.Weapons, 16, 0f, null, true);
            }

            if (_surfaces != null && _surfaces.fleshImpact != null && _surfaces.fleshImpact.IsValid)
            {
                AudioClip flesh = _surfaces.fleshImpact.Pick();
                if (flesh != null)
                {
                    LR2Audio.PlayClip(flesh, Vector3.zero, Mathf.Lerp(0.45f, 0.75f, severity),
                        Random.Range(0.94f, 1.05f), 1f, 500f, 0f, AudioCategory.Weapons, 24, 0f, null, true);
                }
            }

            _nextHitAudioAt = Time.unscaledTime + 0.14f;
        }

        private void OnRegenStarted()
        {
            if (_soldier == null || _health == null || !_soldier.IsLocalPlayer || !_health.IsAlive) return;
            if (_health.Normalized >= 0.85f || Time.unscaledTime < _nextRegenSfxAt) return;
            EnsureClips();
            if (_regenStartClip != null)
            {
                LR2Audio.PlayClip(_regenStartClip, Vector3.zero, 0.32f, 1.0f,
                    1f, 500f, 0f, AudioCategory.UI, 32, 0f, null, true);
            }
            _nextRegenSfxAt = Time.unscaledTime + 1.5f;
        }

        private void OnHealthReplenished()
        {
            if (_soldier == null || _health == null || !_soldier.IsLocalPlayer || !_health.IsAlive) return;
            if (Time.unscaledTime < _nextReplenishSfxAt) return;
            EnsureClips();
            if (_healthReplenishedClip != null)
            {
                LR2Audio.PlayClip(_healthReplenishedClip, Vector3.zero, 0.56f, 1.0f,
                    1f, 500f, 0f, AudioCategory.UI, 20, 0f, null, true);
                HealthReplenishedCount++;
            }
            _nextReplenishSfxAt = Time.unscaledTime + 1.5f;
        }

        private void OnDied(DamageInfo info)
        {
            if (_soldier == null) return;
            EnsureLibs();
            EnsureClips();

            if (_soldier.IsLocalPlayer)
            {
                // Guaranteed tactical local death: crisp fatal punch + deep sub-bass drop + unmuffled 2D bodyFall
                if (_playerDeathClip != null)
                {
                    LR2Audio.PlayClip(_playerDeathClip, Vector3.zero, 0.95f, 1.0f,
                        1f, 500f, 0f, AudioCategory.UI, 0, 0f, null, true);
                }
                if (_common != null && _common.bodyFall != null && _common.bodyFall.IsValid)
                {
                    AudioClip fallClip = _common.bodyFall.Pick();
                    if (fallClip != null)
                    {
                        LR2Audio.PlayClip(fallClip, Vector3.zero, 1.0f, Random.Range(0.95f, 1.03f),
                            1f, 500f, 0f, AudioCategory.Sfx, 16, 0.22f, null, true);
                    }
                }
                DeathAudioCount++;
                return;
            }

            // Bot death: crisp 3D flesh impact + 3D concrete body fall (zero fake cheesy voice groans)
            if (_surfaces != null && _surfaces.fleshImpact != null && _surfaces.fleshImpact.IsValid)
                LR2Audio.PlayAt(_surfaces.fleshImpact, transform.position + Vector3.up * 1.25f, 0.85f);
            if (_common != null && _common.bodyFall != null && _common.bodyFall.IsValid)
                LR2Audio.PlayAt(_common.bodyFall, transform.position, 1.0f, 0.28f);
            DeathAudioCount++;
        }

        private static void EnsureClips()
        {
            if (_hitThudClip == null) _hitThudClip = CreateHitThudClip();
            if (_playerDeathClip == null) _playerDeathClip = CreatePlayerDeathClip();
            if (_regenStartClip == null) _regenStartClip = CreateRegenStartClip();
            if (_healthReplenishedClip == null) _healthReplenishedClip = CreateHealthReplenishedClip();
        }

        private static AudioClip CreateHitThudClip()
        {
            const int sr = 44100;
            const float dur = 0.12f;
            int n = Mathf.RoundToInt(sr * dur);
            var data = new float[n];
            var rng = new System.Random(211);
            float lp = 0f;
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float env = Mathf.Exp(-t * 34f) * Mathf.Clamp01(t * 260f);
                float f = Mathf.Lerp(135f, 44f, Mathf.Clamp01(t * 14f));
                phase += 2f * Mathf.PI * f / sr;
                float thump = Mathf.Sin(phase) * env * 0.78f;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-t * 60f) * 0.28f;
                lp += 0.18f * ((thump + noise) - lp);
                data[i] = Mathf.Clamp((float)System.Math.Tanh(lp * 1.4f) * 0.88f, -0.95f, 0.95f);
            }
            var c = AudioClip.Create("LR2_LocalHitThud", n, 1, sr, false);
            c.SetData(data, 0);
            return c;
        }

        private static AudioClip CreatePlayerDeathClip()
        {
            const int sr = 44100;
            const float dur = 1.45f;
            int n = Mathf.RoundToInt(sr * dur);
            var data = new float[n];
            var rng = new System.Random(409);
            float lp = 0f;
            float lpAir = 0f;
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                // Fatal ballistic impact punch at t=0
                float impactEnv = Mathf.Exp(-t * 28f) * Mathf.Clamp01(t * 320f);
                float impact = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(160f, 48f, Mathf.Clamp01(t * 16f)) * t) * impactEnv * 0.65f;
                float crackNoise = (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-t * 45f) * 0.35f;

                // Deep cinematic sub-bass drop (108 Hz -> 32 Hz)
                float subF = Mathf.Lerp(108f, 32f, Mathf.Pow(Mathf.Clamp01(t / 1.05f), 0.55f));
                phase += 2f * Mathf.PI * subF / sr;
                float subEnv = Mathf.Clamp01(t / 0.03f) * Mathf.Exp(-t * 2.15f);
                float sub = (Mathf.Sin(phase) * 0.82f + Mathf.Sin(phase * 1.5f) * 0.14f) * subEnv * 0.72f;

                // Muffled low-pass air wash fading into silence
                float airRaw = (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-t * 2.6f) * 0.25f;
                lpAir += 0.045f * (airRaw - lpAir);

                float combined = impact + crackNoise + sub + lpAir;
                lp += 0.24f * (combined - lp);
                float tail = Mathf.Clamp01((dur - t) / 0.22f);
                data[i] = Mathf.Clamp((float)System.Math.Tanh(lp * 1.35f) * 0.90f * tail, -0.96f, 0.96f);
            }
            var c = AudioClip.Create("LR2_PlayerDeathImpact", n, 1, sr, false);
            c.SetData(data, 0);
            return c;
        }

        private static AudioClip CreateRegenStartClip()
        {
            const int sr = 44100;
            const float dur = 0.40f;
            int n = Mathf.RoundToInt(sr * dur);
            var data = new float[n];
            var rng = new System.Random(719);
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float env = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / dur));
                float air = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.22f;
                lp += 0.07f * (air - lp);
                float warmPad = (Mathf.Sin(2f * Mathf.PI * 196f * t) * 0.4f + Mathf.Sin(2f * Mathf.PI * 293.66f * t) * 0.25f) * 0.35f;
                data[i] = Mathf.Clamp((lp + warmPad) * env * 0.52f, -0.8f, 0.8f);
            }
            var c = AudioClip.Create("LR2_RegenStart", n, 1, sr, false);
            c.SetData(data, 0);
            return c;
        }

        private static AudioClip CreateHealthReplenishedClip()
        {
            const int sr = 44100;
            const float dur = 0.62f;
            int n = Mathf.RoundToInt(sr * dur);
            var data = new float[n];
            float lp = 0f;
            // Clean tactile two-note ascending resolve (D5 -> A5 + gentle fifth shimmer)
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float n1 = Mathf.Clamp01(t / 0.014f) * Mathf.Exp(-t * 8.5f);
                float t2 = Mathf.Max(0f, t - 0.11f);
                float n2 = t >= 0.11f ? Mathf.Clamp01(t2 / 0.014f) * Mathf.Exp(-t2 * 5.2f) : 0f;

                float sig1 = (Mathf.Sin(2f * Mathf.PI * 587.33f * t) * 0.72f + Mathf.Sin(2f * Mathf.PI * 1174.66f * t) * 0.18f) * n1;
                float sig2 = (Mathf.Sin(2f * Mathf.PI * 880.00f * t) * 0.70f + Mathf.Sin(2f * Mathf.PI * 1318.51f * t) * 0.20f) * n2;
                float raw = (sig1 * 0.45f + sig2 * 0.58f);
                lp += 0.28f * (raw - lp);
                float tail = Mathf.Clamp01((dur - t) / 0.10f);
                data[i] = Mathf.Clamp(lp * tail * 0.78f, -0.85f, 0.85f);
            }
            var c = AudioClip.Create("LR2_HealthReplenished", n, 1, sr, false);
            c.SetData(data, 0);
            return c;
        }
    }
}
