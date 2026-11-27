using LR2.Combat;
using LR2.Core;
using UnityEngine;

namespace LR2.Characters
{
    /// <summary>
    /// Bodycam-style camera motion layered on top of the Kinemation camera: a chest-mounted camera that bounces with
    /// every stride, rolls into strafes and turns, tilts with acceleration, breathes, shakes when hit and dips on
    /// landings. Runs after Kinemation's camera update (which resets the camera every frame), so it never accumulates.
    /// It only moves the rendered view, and it fades out while aiming down sights: Kinemation puts the camera on the
    /// sight in ADS, and any bodycam offset there would move the picture off the line the bullets fly along.
    /// </summary>
    [DefaultExecutionOrder(120)]
    public class BodycamRig : MonoBehaviour
    {
        [Header("Global")]
        [Tooltip("Overall strength (0 = disabled, uses crisp Kinemation FPSCameraController directly). Also scaled by the BODYCAM MOTION setting. Fades out completely while aiming down sights.")]
        [SerializeField, Range(0f, 2f)] private float intensity = 1f;

        // Past this aim blend the sight must sit exactly on the camera axis, so the bodycam adds nothing.
        private const float FullAdsBlend = 0.985f;
        private const float MinStrength = 0.0005f;

        [Header("Stride bob")]
        [SerializeField] private float strideLength = 1.55f;
        [SerializeField] private float bobVertical = 0.06f;
        [SerializeField] private float bobLateral = 0.035f;
        [SerializeField] private float bobRoll = 2.1f;
        [SerializeField] private float bobPitch = 1.35f;
        [SerializeField] private float bobYaw = 0.8f;
        [SerializeField] private float walkBobScale = 0.34f;
        [SerializeField] private float sprintMultiplier = 1.7f;
        [Tooltip("Small downward kick on every footfall (the camera is strapped to a body, not floating).")]
        [SerializeField] private float footfallKick = 0.9f;

        [Header("Body inertia (kept short so aiming stays responsive)")]
        [SerializeField] private float turnLag = 0.3f;
        [SerializeField] private float maxLag = 1.8f;
        [SerializeField] private float swayStiffness = 140f;
        [SerializeField] private float swayDamping = 17f;
        [SerializeField] private float turnRoll = 2.2f;
        [SerializeField] private float strafeRoll = 3.4f;
        [SerializeField] private float accelPitch = 2.2f;

        [Header("Handheld")]
        [SerializeField] private float handheldAmplitude = 0.4f;
        [SerializeField] private float handheldFrequency = 0.6f;
        [SerializeField] private float breathingAmplitude = 0.55f;
        [SerializeField] private float breathingRate = 0.26f;

        [Header("Impacts")]
        [SerializeField] private float landingKick = 0.09f;
        [SerializeField] private float damageShake = 3.2f;

        private SoldierController _soldier;
        private Health _health;
        private float _phase;
        private float _lastYaw, _lastPitch;
        private Vector2 _sway, _swayVel;
        private float _roll, _turnRoll, _accelTilt;
        private Vector3 _lastVel;
        private float _impact, _impactVel;
        private float _kick, _kickVel;
        private float _shake;
        private float _seed;
        private int _lastStep;
        private bool _ready;

        private void Start()
        {
            _soldier = GetComponentInParent<SoldierController>();
            _health = _soldier != null ? _soldier.Health : null;
            if (_soldier == null) { enabled = false; return; }
            _soldier.Movement.Landed += OnLanded;
            if (_health != null) _health.Damaged += OnDamaged;
            _seed = Random.value * 50f;
            _lastYaw = _soldier.Yaw;
            _lastPitch = _soldier.Pitch;
            _ready = true;
        }

        private void OnDestroy()
        {
            if (_soldier != null && _soldier.Movement != null) _soldier.Movement.Landed -= OnLanded;
            if (_health != null) _health.Damaged -= OnDamaged;
        }

        private void OnLanded(float speed) => _impactVel -= Mathf.Clamp(speed, 0f, 14f) * landingKick * 6f;

        private void OnDamaged(DamageInfo info) => _shake = Mathf.Min(1.6f, _shake + info.amount / 26f);

        public void ResetState()
        {
            _sway = _swayVel = Vector2.zero;
            _roll = _turnRoll = _accelTilt = 0f;
            _lastVel = Vector3.zero;
            _impact = _impactVel = _kick = _kickVel = _shake = 0f;
            if (_soldier != null)
            {
                _lastYaw = _soldier.Yaw;
                _lastPitch = _soldier.Pitch;
            }
        }

        private void LateUpdate()
        {
            if (!_ready || _soldier.IsDead || !_soldier.IsLocalPlayer) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            float adsT = Mathf.SmoothStep(0f, 1f, _soldier.AimBlend);
            float k = intensity * LR2Settings.BodycamMotion * (1f - adsT);
            if (_soldier.AimBlend >= FullAdsBlend) k = 0f;
            if (k <= MinStrength) return;

            var move = _soldier.Movement;
            float speed = move.IsGrounded ? move.Speed : 0f;
            bool sprinting = _soldier.IsSprinting;

            // ---- stride bob: B50 cuts walk bob to ~1/3 for BF6/CoD tactility while keeping sprint energetic & ADS steady
            _phase += speed / Mathf.Max(0.5f, strideLength) * Mathf.PI * dt;
            float speed01 = Mathf.Clamp01(speed / 4f) * (sprinting ? sprintMultiplier : walkBobScale);
            float s = Mathf.Sin(_phase);
            float c = Mathf.Cos(_phase * 2f);
            float down = (1f - c) * 0.5f;                         // 0 at mid-stride, 1 at each footfall
            Vector3 bobPos = new Vector3(s * bobLateral, -down * bobVertical, 0f) * speed01;
            float bobR = s * bobRoll * speed01;
            float bobP = down * bobPitch * speed01;
            float bobY = s * bobYaw * speed01;

            int step = Mathf.FloorToInt(_phase / Mathf.PI);       // footfall
            if (step != _lastStep && speed > 1f) _kickVel -= footfallKick * (sprinting ? 1.5f : 0.35f) * 10f;
            _lastStep = step;
            float kickAccel = -_kick * 260f - _kickVel * 22f;
            _kickVel += kickAccel * dt;
            _kick += _kickVel * dt;

            // ---- short rotational inertia (springs back fast)
            float yawVel = Mathf.DeltaAngle(_lastYaw, _soldier.Yaw) / dt;
            float pitchVel = (_soldier.Pitch - _lastPitch) / dt;
            _lastYaw = _soldier.Yaw;
            _lastPitch = _soldier.Pitch;
            Vector2 lagTarget = new Vector2(
                Mathf.Clamp(-yawVel / 100f * turnLag, -maxLag, maxLag),
                Mathf.Clamp(-pitchVel / 100f * turnLag, -maxLag, maxLag));
            Vector2 accel = (lagTarget - _sway) * swayStiffness - _swayVel * swayDamping;
            _swayVel += accel * dt;
            _sway += _swayVel * dt;
            _turnRoll = Mathf.Lerp(_turnRoll, Mathf.Clamp(-yawVel / 180f, -1f, 1f) * turnRoll, LR2Math.Damp(8f, dt));

            // ---- strafe roll + acceleration tilt (camera strapped to the chest leans with the body)
            Vector3 vel = move.Velocity;
            Vector3 localVel = _soldier.transform.InverseTransformDirection(vel);
            _roll = Mathf.Lerp(_roll, -localVel.x / 4f * strafeRoll, LR2Math.Damp(7f, dt));
            Vector3 localAcc = _soldier.transform.InverseTransformDirection((vel - _lastVel) / dt);
            _lastVel = vel;
            _accelTilt = Mathf.Lerp(_accelTilt, Mathf.Clamp(localAcc.z / 20f, -1f, 1f) * accelPitch, LR2Math.Damp(6f, dt));

            // ---- breathing + handheld micro shake (stronger when hurt / out of breath)
            float hurt = _health != null ? 1f + (1f - _health.Normalized) * 1.5f : 1f;
            float t = Time.time;
            float breath = Mathf.Sin(t * breathingRate * Mathf.PI * 2f) * breathingAmplitude * hurt * (sprinting ? 1.6f : 1f);
            float hx = (Mathf.PerlinNoise(t * handheldFrequency, _seed) - 0.5f) * 2f * handheldAmplitude;
            float hy = (Mathf.PerlinNoise(_seed, t * handheldFrequency * 1.21f) - 0.5f) * 2f * handheldAmplitude;

            // ---- impacts
            float impAccel = -_impact * 180f - _impactVel * 16f;
            _impactVel += impAccel * dt;
            _impact += _impactVel * dt;
            _shake = Mathf.MoveTowards(_shake, 0f, dt * 2.2f);
            float shx = (Mathf.PerlinNoise(t * 22f, _seed + 3f) - 0.5f) * damageShake * _shake;
            float shy = (Mathf.PerlinNoise(_seed + 7f, t * 24f) - 0.5f) * damageShake * _shake;

            var cam = transform;
            Vector3 posOffset = (bobPos + new Vector3(0f, _impact + _kick * 0.01f, 0f)) * k;
            posOffset.z = Mathf.Max(0f, posOffset.z);
            posOffset.y = Mathf.Clamp(posOffset.y, -0.06f, 0.06f);
            cam.localPosition += posOffset;
            Vector3 euler = new Vector3(
                bobP + _sway.y + breath + hy + shy + _kick + _accelTilt,
                bobY + _sway.x + hx + shx,
                bobR + _roll + _turnRoll + _sway.x * 0.35f) * k;
            cam.localRotation *= Quaternion.Euler(euler);
        }
    }
}
