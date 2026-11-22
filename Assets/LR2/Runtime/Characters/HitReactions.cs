using LR2.Combat;
using UnityEngine;

namespace LR2.Characters
{
    /// <summary>
    /// Procedural flinch for third-person soldiers: a damped spring rotates spine/head away from the hit.
    /// B125: the spring runs in Update and <see cref="RetargetProxy"/> applies it to the VISIBLE chest and head before it solves
    /// the arms onto the gun. Before, this bent the hidden driver's chest and head in LateUpdate (order 500) after the visible
    /// body had been copied: the body never flinched, but the gun (parented under the driver's head) swung up to ~70 deg off
    /// the hands every time a bot was hit. Without a RetargetProxy it still bends the bones itself.
    /// </summary>
    [DefaultExecutionOrder(500)]
    public class HitReactions : MonoBehaviour
    {
        [SerializeField] private Transform spine;
        [SerializeField] private Transform head;
        [SerializeField, Range(0f, 45f)] private float maxAngle = 28f;
        [SerializeField] private float degreesPer25Damage = 16f;
        [SerializeField] private float stiffness = 175f;
        [SerializeField] private float damping = 11f;
        [SerializeField] private bool disableForLocalPlayer = true;
        [Tooltip("B136: off on Kinemation's robot. Its gun hangs under the head and the hands under the chest, so bending the head " +
                 "on its own would pull the gun off the hands; a chest-only flinch moves the head, gun and arms together.")]
        [SerializeField] private bool bendHead = true;

        private Health _health;
        private Combatant _combatant;
        private Vector3 _angle;      // axis * degrees
        private Vector3 _velocity;
        private float _headBias;
        private RetargetProxy _proxy;

        /// <summary>Current flinch of the chest (world rotation to pre-multiply); identity when calm.</summary>
        public Quaternion SpineFlinch { get; private set; } = Quaternion.identity;
        /// <summary>Current flinch of the head (world rotation to pre-multiply); identity when calm.</summary>
        public Quaternion HeadFlinch { get; private set; } = Quaternion.identity;

        public void Configure(Transform spineBone, Transform headBone, bool bendHeadBone = true)
        {
            spine = spineBone;
            head = headBone;
            bendHead = bendHeadBone;
        }

        private void Awake()
        {
            _health = GetComponent<Health>();
            _combatant = GetComponent<Combatant>();
            _proxy = GetComponent<RetargetProxy>();
            var animator = GetComponent<Animator>();
            if (animator != null)
            {
                // B136: LR2Bones also finds the generic robot's bones
                if (spine == null) spine = LR2Bones.Get(animator, HumanBodyBones.Chest);
                if (spine == null) spine = LR2Bones.Get(animator, HumanBodyBones.Spine);
                if (head == null && bendHead) head = LR2Bones.Get(animator, HumanBodyBones.Head);
            }
        }

        private void OnEnable()
        {
            if (_health != null) _health.Damaged += OnDamaged;
        }

        private void OnDisable()
        {
            if (_health != null) _health.Damaged -= OnDamaged;
        }

        private void OnDamaged(DamageInfo info)
        {
            if (disableForLocalPlayer && _combatant != null && _combatant.IsLocalPlayer) return;
            Vector3 dir = info.direction.sqrMagnitude > 0.001f ? info.direction.normalized : -transform.forward;
            Vector3 axis = Vector3.Cross(Vector3.up, dir);
            if (axis.sqrMagnitude < 0.001f) axis = transform.right;
            float deg = Mathf.Min(maxAngle, degreesPer25Damage * info.amount / 25f);
            _velocity += axis.normalized * deg * stiffness * 0.12f;
            _headBias = info.zone == HitZone.Head ? 1.6f : 0.6f;
        }

        private void Update()
        {
            SpineFlinch = HeadFlinch = Quaternion.identity;
            if (_health != null && !_health.IsAlive) { _angle = _velocity = Vector3.zero; return; }
            float dt = Time.deltaTime;
            Vector3 accel = -_angle * stiffness - _velocity * damping;
            _velocity += accel * dt;
            _angle += _velocity * dt;
            if (_angle.magnitude > maxAngle) _angle = _angle.normalized * maxAngle;
            float mag = _angle.magnitude;
            if (mag < 0.01f) return;
            SpineFlinch = Quaternion.AngleAxis(mag, _angle / mag);
            HeadFlinch = Quaternion.AngleAxis(mag * _headBias, _angle / mag);
        }

        private void LateUpdate()
        {
            if (_proxy != null && _proxy.isActiveAndEnabled) return; // the proxy bends the visible body (B125)
            if (_health != null && !_health.IsAlive) return;
            if (spine != null) spine.rotation = SpineFlinch * spine.rotation;
            if (head != null && bendHead) head.rotation = HeadFlinch * head.rotation;
        }
    }
}
