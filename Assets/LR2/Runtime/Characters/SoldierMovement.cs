// Based on KINEMATION FPS Animation Framework demo FPSMovement (2025), adapted for LR2:
// input is pushed by SoldierController (human or bot), plus mantling, CoD-style speed modifiers, footsteps and noise.
using System;
using Demo.Scripts.Runtime.Character;
using KINEMATION.FPSAnimationFramework.Runtime.Core;
using KINEMATION.Shared.KAnimationCore.Runtime.Core;
using KINEMATION.Shared.KAnimationCore.Runtime.Input;
using LR2.Combat;
using LR2.Core;
using LR2.FX;
using UnityEngine;

namespace LR2.Characters
{
    [RequireComponent(typeof(CharacterController))]
    public class SoldierMovement : MonoBehaviour
    {
        public delegate bool ActionConditionDelegate();
        public delegate void OnActionCallback();

        [SerializeField] private FPSMovementSettings movementSettings;

        [Header("LR2 Speed Modifiers")]
        [Tooltip("Speed multiplier while aiming down sights.")]
        [SerializeField, Range(0.2f, 1f)] private float adsSpeedMultiplier = 0.58f;
        [Tooltip("Speed multiplier when moving backwards.")]
        [SerializeField, Range(0.4f, 1f)] private float backwardsMultiplier = 0.82f;
        [Tooltip("Global multiplier applied to all gaits (tune overall pace).")]
        [SerializeField, Range(0.5f, 1.5f)] private float paceMultiplier = 1f;

        [Header("Mantle")]
        [SerializeField] private bool enableMantle = true;
        [SerializeField] private float mantleMinHeight = 0.45f;
        [SerializeField] private float mantleMaxHeight = 1.85f;
        [SerializeField] private float mantleReach = 0.85f;
        [SerializeField] private float mantleDuration = 0.42f;

        [Header("Footsteps")]
        [SerializeField] private float strideWalk = 1.12f;
        [SerializeField] private float strideSprint = 1.48f;
        [SerializeField] private float strideCrouch = 0.85f;
        [SerializeField] private float strideProne = 0.65f;
        [SerializeField] private float noiseWalk = 9f;
        [SerializeField] private float noiseSprint = 18f;
        [SerializeField] private float noiseCrouch = 3f;
        [SerializeField] private float noiseProne = 1.5f;

        [Header("Falling")]
        [Tooltip("Landing speed (m/s) above which fall damage starts.")]
        [SerializeField] private float fallDamageSpeed = 13f;
        [SerializeField] private float fallDamagePerMs = 9f;

        public OnActionCallback onStartMoving;
        public OnActionCallback onStopMoving;
        public OnActionCallback onSprintStarted;
        public OnActionCallback onSprintEnded;
        public OnActionCallback onCrouch;
        public OnActionCallback onUncrouch;
        public OnActionCallback onProneStarted;
        public OnActionCallback onProneEnded;
        public OnActionCallback onJump;
        public OnActionCallback onLanded;
        public OnActionCallback onSlideStarted;
        public OnActionCallback onSlideEnded;

        public ActionConditionDelegate _slideActionCondition;
        public ActionConditionDelegate _proneActionCondition;
        public ActionConditionDelegate _sprintActionCondition;

        public event Action Jumped;
        public event Action<float> Landed;
        public event Action SlideStarted;
        public event Action MantleStarted;
        /// <summary>Raised on every footfall with the planar speed (foley, AI hearing).</summary>
        public event Action<float> Footstep;

        public SoldierMoveState State { get; private set; }
        public SoldierStance Stance { get; private set; }
        public Vector2 AnimatorVelocity { get; private set; }
        public Vector3 MoveVector { get; private set; }
        public Vector3 Velocity => _controller != null && _controller.enabled ? _controller.velocity : _velocity;
        public bool IsGrounded => _controller != null && _controller.isGrounded;
        public float Speed => new Vector3(_velocity.x, 0f, _velocity.z).magnitude;
        public float SpeedMultiplier { get; set; } = 1f;
        public bool IsAimingSlowdown { get; set; }
        public bool IsMoving => _inputDirection.sqrMagnitude > 0.01f;
        public FPSMovementSettings Settings => movementSettings;

        private CharacterController _controller;
        private UserInputController _inputController;
        private Animator _animator;
        private Combatant _combatant;
        private Health _health;

        private Vector2 _inputDirection;
        private bool _sprintRequested;
        private SoldierMoveState _cachedState;
        private Vector3 _velocity;
        private float _originalHeight;
        private Vector3 _originalCenter;
        private GaitSettings _desiredGait;
        private GaitSettings _cachedGait;
        private float _slideProgress;
        private Vector3 _slideVector;
        // B90 (user 00:13): hold-to-crouch and double-tap slide
        private bool _crouchFromHold, _standWhenClear;
        private float _lastSprintTime = -10f;
        /// <summary>True while the crouch-hold key (X) is down.</summary>
        public bool CrouchHeld { get; private set; }
        private float _gaitProgress;
        private bool _wasMoving;
        private float _strideAccum;
        private float _airborneTime;
        private float _lastFallSpeed;
        private bool _initialized;

        // Mantle
        private Vector3 _mantleStart, _mantleEnd;
        private float _mantleT;

        private static readonly int InAirHash = Animator.StringToHash("InAir");
        private static readonly int MoveXHash = Animator.StringToHash("MoveX");
        private static readonly int MoveYHash = Animator.StringToHash("MoveY");
        private static readonly int VelocityHash = Animator.StringToHash("Velocity");
        private static readonly int MovingHash = Animator.StringToHash("Moving");
        private static readonly int CrouchingHash = Animator.StringToHash("Crouching");
        private static readonly int ProningHash = Animator.StringToHash("Proning");
        private static readonly int SprintingHash = Animator.StringToHash("Sprinting");
        private static readonly int LocoSpeedHash = Animator.StringToHash("LocoSpeed");

        // Animation cadence: walkAnimReferenceSpeed (2.25) was replaced by stride matching below (11-18 04:25).
        [Header("Stride matching (B23/B50: feet must not skate)")]
        [Tooltip("Ground speed the SPRINT clips cover at playback 1 (~4.75 m/s).")]
        [SerializeField] private float sprintClipNaturalSpeed = 4.75f;
        [Tooltip("Largest share of the sprint stride blended into standing walk at full walk speed (a jog). Pose checks at 3.5 m/s: 0.20 -> 1.92 m/s foot slide (C7), 0.35 -> 0.69 m/s (C8); playback is capped at 1.5x, so zero slide needs ~0.55.")]
        [SerializeField, Range(0f, 0.6f)] private float walkSprintBlendMax = 0.56f;
        // Ground speed the walk+sprint blend covers at playback 1, measured by the pose-check GAIT probe at 3.5 m/s:
        // blend 0.20 -> 1.06 m/s (C7), 0.35 -> 1.89 (C8), 0.56 -> 2.58 (C9); blend 0 (slow walk) estimated.
        private static readonly float[] StrideBlend = { 0f, 0.20f, 0.35f, 0.56f };
        private static readonly float[] StrideSpeed = { 0.90f, 1.06f, 1.89f, 2.58f };

        private float WalkStrideAt(float blend)
        {
            blend = Mathf.Clamp01(blend);
            for (int i = 1; i < StrideBlend.Length; i++)
                if (blend <= StrideBlend[i])
                    return Mathf.Lerp(StrideSpeed[i - 1], StrideSpeed[i], Mathf.InverseLerp(StrideBlend[i - 1], StrideBlend[i], blend));
            // beyond the last measurement: continue the last slope toward the sprint clip's own stride
            int n = StrideBlend.Length - 1;
            float slope = (StrideSpeed[n] - StrideSpeed[n - 1]) / (StrideBlend[n] - StrideBlend[n - 1]);
            return Mathf.Min(sprintClipNaturalSpeed, StrideSpeed[n] + slope * (blend - StrideBlend[n]));
        }
        [SerializeField] private float sprintAnimReferenceSpeed = 4.75f;
        [SerializeField] private float crouchAnimReferenceSpeed = 2.1f;
        [SerializeField] private float proneAnimReferenceSpeed = 0.8f;
        private float _locoSpeed = 1f;
        private float _proneTransitionUntil;
        private Transform _leftFoot, _rightFoot;
        private bool _lFootLifted, _rFootLifted;
        private float _footGroundY = 0.14f;
        private float _lastStepAt = -10f;

        // ------------------------------------------------------------------ API used by SoldierController
        public void SetMoveInput(Vector2 move) => _inputDirection = Vector2.ClampMagnitude(move, 1f);

        public void SetSprintRequested(bool sprint)
        {
            _sprintRequested = sprint;
            if (State is SoldierMoveState.InAir or SoldierMoveState.Sliding or SoldierMoveState.Mantling) return;

            bool canSprint = sprint && CanSprint() && _inputDirection.y > 0.35f;
            if (canSprint && State != SoldierMoveState.Sprinting) State = SoldierMoveState.Sprinting;
            else if (!canSprint && State == SoldierMoveState.Sprinting) State = SoldierMoveState.Walking;
        }

        public void StopSprint()
        {
            _sprintRequested = false;
            if (State == SoldierMoveState.Sprinting) State = SoldierMoveState.Walking;
        }

        public bool RequestJump()
        {
            if (Time.time < _proneTransitionUntil) return false;
            if (State is SoldierMoveState.Mantling or SoldierMoveState.Sliding) return false;
            if (Stance == SoldierStance.Prone)
            {
                if (CanUnCrouch()) ExitProne(false);
                return false;
            }
            if (enableMantle && TryStartMantle()) return true;
            if (State == SoldierMoveState.InAir) return false;
            if (Stance == SoldierStance.Crouching)
            {
                if (CanUnCrouch()) UnCrouch();
                return false;
            }

            State = SoldierMoveState.InAir;
            _velocity.y = movementSettings != null ? movementSettings.jumpHeight : 5.5f;
            Jumped?.Invoke();
            return true;
        }

        /// <summary>Crouch toggle; while sprinting this becomes a slide (CoD style). From prone, transitions cleanly to crouch.</summary>
        public void RequestCrouchToggle()
        {
            if (Time.time < _proneTransitionUntil) return;
            if (State == SoldierMoveState.Sprinting && CanSlide())
            {
                _slideProgress = 0f;
                State = SoldierMoveState.Sliding;
                return;
            }

            if (State is not (SoldierMoveState.Idle or SoldierMoveState.Walking)) return;

            if (Stance == SoldierStance.Prone)
            {
                ExitProne(true);
                return;
            }

            if (Stance == SoldierStance.Standing)
            {
                Crouch();
                _desiredGait = Gait(GaitKind.Crouch);
                return;
            }

            if (!CanUnCrouch()) return;
            UnCrouch();
            _desiredGait = Gait(GaitKind.Walk);
        }

        /// <summary>Toggles prone stance (from standing or crouching). Cannot prone mid-air.</summary>
        public void RequestProneToggle()
        {
            if (Time.time < _proneTransitionUntil) return;
            if (State is SoldierMoveState.InAir or SoldierMoveState.Mantling or SoldierMoveState.Sliding) return;
            if (_proneActionCondition != null && !_proneActionCondition.Invoke()) return;

            if (Stance == SoldierStance.Prone)
            {
                if (CanUnCrouch()) ExitProne(false);
                return;
            }
            EnterProne();
        }

        /// <summary>B90: crouch while the hold key is down, stand again on release (once there is headroom).</summary>
        public void SetCrouchHeld(bool held)
        {
            if (held == CrouchHeld) return;
            CrouchHeld = held;
            if (held)
            {
                _standWhenClear = false;
                if (State is SoldierMoveState.InAir or SoldierMoveState.Mantling or SoldierMoveState.Sliding) { _crouchFromHold = true; return; }
                if (Stance == SoldierStance.Prone)
                {
                    if (Time.time >= _proneTransitionUntil) ExitProne(true);
                    _crouchFromHold = Stance == SoldierStance.Crouching;
                    return;
                }
                if (Stance == SoldierStance.Standing)
                {
                    if (State == SoldierMoveState.Sprinting) StopSprint();
                    Crouch();
                    _desiredGait = Gait(GaitKind.Crouch);
                    _crouchFromHold = true;
                }
                return;
            }
            if (!_crouchFromHold) return;
            _crouchFromHold = false;
            if (State == SoldierMoveState.Sliding) return; // the slide stands up by itself when it ends
            if (Stance != SoldierStance.Crouching) return;
            if (CanUnCrouch())
            {
                UnCrouch();
                _desiredGait = IsMoving ? Gait(GaitKind.Walk) : Gait(GaitKind.Idle);
            }
            else _standWhenClear = true; // under a low ceiling: stand as soon as there is room
        }

        /// <summary>
        /// B90: slide on a double tap of the crouch-hold key. Allowed while sprinting or within 0.45 s of a sprint (the first
        /// tap already crouched and slowed the player), moving forward, standing or crouched, on the ground.
        /// </summary>
        public bool RequestSlide()
        {
            if (Time.time < _proneTransitionUntil) return false;
            if (State is SoldierMoveState.InAir or SoldierMoveState.Mantling or SoldierMoveState.Sliding) return false;
            if (Stance == SoldierStance.Prone || _inputDirection.y <= 0.35f) return false;
            bool recentSprint = State == SoldierMoveState.Sprinting || Time.time - _lastSprintTime <= 0.45f;
            if (!recentSprint) return false;
            if (_slideActionCondition != null && !_slideActionCondition.Invoke()) return false;
            _slideProgress = 0f;
            State = SoldierMoveState.Sliding;
            return true;
        }

        public void ForceStand()
        {
            if (Stance == SoldierStance.Prone && CanUnCrouch()) ExitProne(false);
            else if (Stance == SoldierStance.Crouching && CanUnCrouch()) UnCrouch();
        }

        public void Teleport(Vector3 position)
        {
            bool wasEnabled = _controller.enabled;
            _controller.enabled = false;
            transform.position = position;
            _controller.enabled = wasEnabled;
            ResetState();
        }

        public void ResetState()
        {
            // D56: a death/teleport/respawn ends a sprint without a state change, so OnMovementStateChanged never fired the
            // sprint-end hooks: the next life kept Kinemation's sprint look/stabilisation weights (C17: look 0.80, stab 0.78
            // for the whole next life after dying mid-sprint) and the Animator kept the sprint blend.
            bool wasSprinting = _cachedState == SoldierMoveState.Sprinting || State == SoldierMoveState.Sprinting;
            _velocity = Vector3.zero;
            MoveVector = Vector3.zero;
            _inputDirection = Vector2.zero;
            _sprintRequested = false;
            _airborneTime = 0f;
            _proneTransitionUntil = 0f;
            CrouchHeld = _crouchFromHold = _standWhenClear = false; // B90
            _lastSprintTime = -10f;
            if (_initialized && Stance != SoldierStance.Standing)
            {
                if (_animator != null) _animator.SetBool(ProningHash, false);
                UnCrouch();
            }
            _cachedState = State = SoldierMoveState.Idle;
            _desiredGait = _cachedGait = Gait(GaitKind.Idle);
            ResetAnimatorParams();
            if (wasSprinting) onSprintEnded?.Invoke();
        }

        /// <summary>D56: zero the locomotion parameters the Animator would otherwise carry through a death or teleport
        /// (sprint blend, move vector, playback speed), so the next frame starts from the standing idle.</summary>
        public void ResetAnimatorParams()
        {
            AnimatorVelocity = Vector2.zero;
            _locoSpeed = 1f;
            if (_inputController != null) _inputController.SetValue(FPSANames.MoveInput, Vector4.zero);
            if (_animator == null || !_animator.isActiveAndEnabled || _animator.runtimeAnimatorController == null) return;
            _animator.SetFloat(MoveXHash, 0f);
            _animator.SetFloat(MoveYHash, 0f);
            _animator.SetFloat(VelocityHash, 0f);
            _animator.SetFloat(SprintingHash, 0f);
            _animator.SetFloat(LocoSpeedHash, 1f);
            _animator.SetBool(MovingHash, false);
            _animator.SetBool(InAirHash, false);
            if (Stance == SoldierStance.Standing)
            {
                _animator.SetBool(CrouchingHash, false);
                _animator.SetBool(ProningHash, false);
            }
        }

        // ------------------------------------------------------------------ internals
        private enum GaitKind { Idle, Walk, Sprint, Crouch, Prone }

        private GaitSettings Gait(GaitKind kind)
        {
            if (movementSettings == null) return new GaitSettings { velocity = kind == GaitKind.Sprint ? 7f : kind == GaitKind.Prone ? 0.6f : 4.5f, velocitySmoothing = 10f };
            switch (kind)
            {
                case GaitKind.Walk: return movementSettings.walking;
                case GaitKind.Sprint: return movementSettings.sprinting;
                case GaitKind.Crouch: return movementSettings.crouching;
                case GaitKind.Prone: return movementSettings.prone;
                default: return movementSettings.idle;
            }
        }

        private bool CanSlide()
        {
            return State == SoldierMoveState.Sprinting && Stance == SoldierStance.Standing
                && (_slideActionCondition == null || _slideActionCondition.Invoke());
        }

        private bool CanSprint()
        {
            bool condition = _sprintActionCondition == null || _sprintActionCondition.Invoke();
            return Stance == SoldierStance.Standing && condition && !IsAimingSlowdown;
        }

        private bool CanUnCrouch()
        {
            float height = _originalHeight - _controller.radius * 2f;
            Vector3 position = transform.TransformPoint(_originalCenter + Vector3.up * height / 2f);
            return !Physics.CheckSphere(position, _controller.radius * 0.95f, GroundMask, QueryTriggerInteraction.Ignore);
        }

        private int GroundMask => movementSettings != null && movementSettings.groundMask.value != 0
            ? movementSettings.groundMask.value | LR2Layers.EnvironmentMask
            : LR2Layers.EnvironmentMask;

        private void Crouch()
        {
            float crouchedHeight = _originalHeight * (movementSettings != null ? movementSettings.crouchRatio : 0.6f);
            SetCapsuleHeight(crouchedHeight);
            Stance = SoldierStance.Crouching;
            if (_animator != null)
            {
                _animator.SetBool(ProningHash, false);
                _animator.SetBool(CrouchingHash, true);
            }
            onCrouch?.Invoke();
        }

        private void UnCrouch()
        {
            SetCapsuleHeight(_originalHeight);
            Stance = SoldierStance.Standing;
            if (_animator != null) _animator.SetBool(CrouchingHash, false);
            onUncrouch?.Invoke();
        }

        private void EnterProne()
        {
            StopSprint();
            float proneHeight = Mathf.Max(_controller.radius * 2f + 0.1f, _originalHeight * 0.48f);
            SetCapsuleHeight(proneHeight);
            Stance = SoldierStance.Prone;
            if (_animator != null)
            {
                _animator.SetBool(CrouchingHash, false);
                _animator.SetBool(ProningHash, true);
            }
            _desiredGait = Gait(GaitKind.Prone);
            float lockSec = movementSettings != null ? movementSettings.proneTransitionDuration : 0.32f;
            _proneTransitionUntil = Time.time + Mathf.Min(lockSec, 0.26f);
            onProneStarted?.Invoke();
        }

        private void ExitProne(bool toCrouch)
        {
            if (_animator != null) _animator.SetBool(ProningHash, false);
            if (toCrouch)
            {
                Crouch();
                _desiredGait = Gait(GaitKind.Crouch);
            }
            else
            {
                UnCrouch();
                _desiredGait = IsMoving ? Gait(GaitKind.Walk) : Gait(GaitKind.Idle);
            }
            float lockSec = movementSettings != null ? movementSettings.proneTransitionDuration : 0.32f;
            _proneTransitionUntil = Time.time + Mathf.Min(lockSec, 0.26f);
            onProneEnded?.Invoke();
        }

        private void SetCapsuleHeight(float height)
        {
            float diff = _originalHeight - height;
            _controller.height = height;
            Vector3 center = _originalCenter;
            center.y -= diff / 2f;
            _controller.center = center;
        }

        private void UpdateMovementState()
        {
            if (State == SoldierMoveState.Mantling) return;
            if (State == SoldierMoveState.Sliding && !Mathf.Approximately(_slideProgress, 1f)) return;
            if (State == SoldierMoveState.InAir) return;

            if (State == SoldierMoveState.Sprinting && _inputDirection.y > 0.35f && Mathf.Abs(_inputDirection.x) < 0.75f
                && _sprintRequested && CanSprint())
                return;

            if (_sprintRequested && CanSprint() && _inputDirection.y > 0.35f)
            {
                State = SoldierMoveState.Sprinting;
                return;
            }

            State = IsMoving ? SoldierMoveState.Walking : SoldierMoveState.Idle;
        }

        private void OnMovementStateChanged()
        {
            if (_cachedState == SoldierMoveState.InAir) onLanded?.Invoke();
            if (_cachedState == SoldierMoveState.Sprinting) onSprintEnded?.Invoke();
            if (_cachedState == SoldierMoveState.Sliding)
            {
                onSlideEnded?.Invoke();
                if (CrouchHeld) _crouchFromHold = true;          // B90: still holding X: stay crouched
                else if (CanUnCrouch()) UnCrouch();
            }

            switch (State)
            {
                case SoldierMoveState.Idle:
                    _desiredGait = Gait(GaitKind.Idle);
                    return;
                case SoldierMoveState.InAir:
                    onJump?.Invoke();
                    return;
                case SoldierMoveState.Mantling:
                    return;
                case SoldierMoveState.Sprinting:
                    _gaitProgress = 0f;
                    _cachedGait = _desiredGait;
                    onSprintStarted?.Invoke();
                    _desiredGait = Gait(GaitKind.Sprint);
                    return;
                case SoldierMoveState.Sliding:
                    _desiredGait.velocitySmoothing = movementSettings != null ? movementSettings.slideDirectionSmoothing : 2f;
                    _slideVector = _velocity.sqrMagnitude > 0.1f ? _velocity : transform.forward * Gait(GaitKind.Sprint).velocity;
                    _slideVector.y = 0f;
                    if (_slideVector.sqrMagnitude < 0.01f) _slideVector = transform.forward;
                    _slideProgress = 0f;
                    onSlideStarted?.Invoke();
                    SlideStarted?.Invoke();
                    Crouch();
                    return;
            }

            if (Stance == SoldierStance.Prone)
            {
                _desiredGait = Gait(GaitKind.Prone);
                return;
            }

            if (Stance == SoldierStance.Crouching)
            {
                _desiredGait = Gait(GaitKind.Crouch);
                return;
            }

            if (_cachedState == SoldierMoveState.Idle)
            {
                _gaitProgress = 0f;
                _cachedGait = _desiredGait;
            }
            _desiredGait = Gait(GaitKind.Walk);
        }

        private float SpeedScale()
        {
            float scale = paceMultiplier * SpeedMultiplier;
            if (IsAimingSlowdown) scale *= adsSpeedMultiplier;
            if (_inputDirection.y < -0.1f) scale *= backwardsMultiplier;
            return scale;
        }

        private void UpdateSliding()
        {
            float curve = movementSettings != null ? movementSettings.slideCurve.Evaluate(_slideProgress) : 1f - _slideProgress;
            float speed = curve * (movementSettings != null ? movementSettings.slideSpeed : 9f);
            _velocity = _slideVector.normalized * speed;
            Vector3 desired = _velocity;
            desired.y = -2f;
            MoveVector = desired;
            _slideProgress = Mathf.Clamp01(_slideProgress + Time.deltaTime);
        }

        private void UpdateGrounded()
        {
            var normInput = Time.time < _proneTransitionUntil ? Vector2.zero : _inputDirection;
            var targetDirection = transform.right * normInput.x + transform.forward * normInput.y;

            float maxAccelTime = movementSettings != null ? movementSettings.accelerationCurve.keys[^1].time : 0.2f;
            _gaitProgress = Mathf.Min(_gaitProgress + Time.deltaTime, Mathf.Max(maxAccelTime, 0.0001f));
            float t = movementSettings != null ? movementSettings.accelerationCurve.Evaluate(_gaitProgress) : 1f;
            targetDirection *= Mathf.Lerp(_cachedGait.velocity, _desiredGait.velocity, t) * SpeedScale();

            targetDirection = Vector3.Lerp(_velocity, targetDirection, KMath.ExpDecayAlpha(_desiredGait.velocitySmoothing, Time.deltaTime));
            _velocity = targetDirection;
            targetDirection.y = -2f;
            MoveVector = targetDirection;
        }

        private void UpdateInAir()
        {
            float gravity = movementSettings != null ? movementSettings.gravity : 18f;
            float maxFall = movementSettings != null && movementSettings.maxFallVelocity > 0f ? movementSettings.maxFallVelocity : 40f;
            _velocity.y -= gravity * Time.deltaTime;
            _velocity.y = Mathf.Max(-maxFall, _velocity.y);
            _lastFallSpeed = Mathf.Max(_lastFallSpeed, -_velocity.y);

            var desired = transform.right * _inputDirection.x + transform.forward * _inputDirection.y;
            desired *= _desiredGait.velocity * SpeedScale();
            float airFriction = movementSettings != null ? movementSettings.airFriction : 1f;
            float airVelocity = movementSettings != null ? movementSettings.airVelocity : 2f;
            desired = Vector3.Lerp(_velocity, desired * airFriction, KMath.ExpDecayAlpha(airVelocity, Time.deltaTime));
            desired.y = _velocity.y;
            _velocity = desired;
            MoveVector = desired;
        }

        private bool TryStartMantle()
        {
            if (Stance != SoldierStance.Standing) return false;
            Vector3 fwd = transform.forward;
            Vector3 feet = transform.position;
            int mask = LR2Layers.EnvironmentMask;

            // 1) Something solid in front at knee/waist height?
            bool blocked = false;
            for (float h = mantleMinHeight; h <= mantleMaxHeight; h += 0.35f)
            {
                if (Physics.Raycast(feet + Vector3.up * h, fwd, mantleReach, mask, QueryTriggerInteraction.Ignore))
                {
                    blocked = true;
                    break;
                }
            }
            if (!blocked) return false;

            // 2) Find the ledge top.
            Vector3 probe = feet + fwd * (mantleReach * 0.9f) + Vector3.up * (mantleMaxHeight + 0.4f);
            if (!Physics.Raycast(probe, Vector3.down, out var top, mantleMaxHeight + 0.4f, mask, QueryTriggerInteraction.Ignore))
                return false;
            float ledge = top.point.y - feet.y;
            if (ledge < mantleMinHeight || ledge > mantleMaxHeight || top.normal.y < 0.7f) return false;

            // 3) Room to stand on top?
            Vector3 target = top.point + Vector3.up * 0.05f;
            float r = _controller.radius;
            Vector3 p1 = target + Vector3.up * (r + 0.05f);
            Vector3 p2 = target + Vector3.up * (_originalHeight - r);
            if (Physics.CheckCapsule(p1, p2, r * 0.9f, mask | (1 << LR2Layers.PlayerClip), QueryTriggerInteraction.Ignore))
                return false;

            _mantleStart = feet;
            _mantleEnd = target;
            _mantleT = 0f;
            _velocity = Vector3.zero;
            State = SoldierMoveState.Mantling;
            _controller.enabled = false;
            MantleStarted?.Invoke();
            onJump?.Invoke();
            return true;
        }

        private void UpdateMantle()
        {
            _mantleT += Time.deltaTime / Mathf.Max(0.05f, mantleDuration);
            float t = Mathf.Clamp01(_mantleT);
            // Rise first, then move over the ledge.
            float up = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t * 1.6f));
            float fwd = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.35f) / 0.65f));
            Vector3 pos = new Vector3(
                Mathf.Lerp(_mantleStart.x, _mantleEnd.x, fwd),
                Mathf.Lerp(_mantleStart.y, _mantleEnd.y + 0.02f, up),
                Mathf.Lerp(_mantleStart.z, _mantleEnd.z, fwd));
            transform.position = pos;
            MoveVector = Vector3.zero;
            if (t >= 1f)
            {
                _controller.enabled = true;
                State = SoldierMoveState.Idle;
                onLanded?.Invoke();
            }
        }

        private void UpdateAnimatorParams()
        {
            if (_animator == null) return;
            var animatorVelocity = Time.time < _proneTransitionUntil ? Vector2.zero : _inputDirection;
            animatorVelocity *= State is SoldierMoveState.InAir or SoldierMoveState.Mantling ? 0f : 1f;
            AnimatorVelocity = Vector2.Lerp(AnimatorVelocity, animatorVelocity, KMath.ExpDecayAlpha(_desiredGait.velocitySmoothing, Time.deltaTime));

            _animator.SetFloat(MoveXHash, AnimatorVelocity.x);
            _animator.SetFloat(MoveYHash, AnimatorVelocity.y);
            _animator.SetFloat(VelocityHash, AnimatorVelocity.magnitude);
            _animator.SetBool(InAirHash, State == SoldierMoveState.InAir);
            _animator.SetBool(MovingHash, IsMoving);

            float planar = new Vector3(_velocity.x, 0f, _velocity.z).magnitude;
            float sprintWeight = _animator.GetFloat(SprintingHash);
            float t = KMath.ExpDecayAlpha(_desiredGait.velocitySmoothing, Time.deltaTime);
            // Gently blend forward walking partway toward the upright sprint clip (natural ~4.75 m/s) for a full stride
            // without pulling the rifle down off-screen into sprint carry.
            float targetSprint = State == SoldierMoveState.Sprinting ? 1f
                : (Stance == SoldierStance.Standing && _inputDirection.y > 0.35f && planar > 1.8f
                    ? Mathf.Clamp01((planar - 1.8f) / 1.7f) * walkSprintBlendMax
                    : 0f);
            // B90: the sprint pose blends in about twice as fast as before (95 % in ~0.15 s) so sprint starts feel snappy
            float sprintT = targetSprint > sprintWeight ? KMath.ExpDecayAlpha(20f, Time.deltaTime) : t;
            sprintWeight = Mathf.Lerp(sprintWeight, targetSprint, sprintT);
            if (targetSprint <= 0f && sprintWeight < 0.004f) sprintWeight = 0f;
            _animator.SetFloat(SprintingHash, sprintWeight);

            float reference = Stance == SoldierStance.Prone ? proneAnimReferenceSpeed
                : Stance == SoldierStance.Crouching ? crouchAnimReferenceSpeed
                : State == SoldierMoveState.Sprinting ? sprintAnimReferenceSpeed
                : WalkStrideAt(sprintWeight); // measured stride of the walk+sprint blend (no skating)
            float targetLoco = planar > 0.3f ? Mathf.Clamp(planar / Mathf.Max(0.1f, reference), 0.72f, 1.50f) : 1f;
            _locoSpeed = Mathf.Lerp(_locoSpeed, targetLoco, KMath.ExpDecayAlpha(8f, Time.deltaTime));
            _animator.SetFloat(LocoSpeedHash, _locoSpeed);

            if (_inputController != null)
                _inputController.SetValue(FPSANames.MoveInput, new Vector4(AnimatorVelocity.x, AnimatorVelocity.y));
        }

        private void UpdateFootsteps()
        {
            if (State is SoldierMoveState.InAir or SoldierMoveState.Mantling) return;
            float planar = new Vector3(_velocity.x, 0f, _velocity.z).magnitude;
            float minSpeed = Stance == SoldierStance.Prone ? 0.25f : 0.55f;
            if (planar < minSpeed || State == SoldierMoveState.Sliding) return;
            _strideAccum += planar * Time.deltaTime;

            float stride = State == SoldierMoveState.Sprinting ? strideSprint
                : Stance == SoldierStance.Prone ? strideProne
                : Stance == SoldierStance.Crouching ? strideCrouch : strideWalk;

            // Synchronize footsteps to actual Humanoid driver foot touchdowns within [0.48*stride .. 1.25*stride]
            bool plantedByAnim = false;
            if (Stance != SoldierStance.Prone && _leftFoot != null && _rightFoot != null && Time.time - _lastStepAt >= 0.20f)
            {
                float ly = _leftFoot.position.y - transform.position.y;
                float ry = _rightFoot.position.y - transform.position.y;
                float minNow = Mathf.Min(ly, ry);
                _footGroundY = Mathf.Lerp(_footGroundY, Mathf.Clamp(minNow, 0.04f, 0.22f), Time.deltaTime * 4f);
                float liftH = _footGroundY + (State == SoldierMoveState.Sprinting ? 0.055f : 0.032f);
                float plantH = _footGroundY + 0.018f;
                if (ly > liftH) _lFootLifted = true;
                else if (_lFootLifted && ly <= plantH) { _lFootLifted = false; if (_strideAccum >= stride * 0.48f) plantedByAnim = true; }
                if (ry > liftH) _rFootLifted = true;
                else if (_rFootLifted && ry <= plantH) { _rFootLifted = false; if (_strideAccum >= stride * 0.48f) plantedByAnim = true; }
            }

            float fallbackCap = (_leftFoot != null && _rightFoot != null && Stance != SoldierStance.Prone) ? stride * 1.25f : stride;
            if (!plantedByAnim && _strideAccum < fallbackCap) return;
            _strideAccum = 0f;
            _lastStepAt = Time.time;
            Footstep?.Invoke(planar);

            float noise = State == SoldierMoveState.Sprinting ? noiseSprint
                : Stance == SoldierStance.Prone ? noiseProne
                : Stance == SoldierStance.Crouching ? noiseCrouch : noiseWalk;
            if (IsAimingSlowdown) noise *= 0.6f;
            float intensity = State == SoldierMoveState.Sprinting ? 0.92f
                : Stance == SoldierStance.Prone ? 0.2f
                : Stance == SoldierStance.Crouching ? 0.35f : 0.68f;
            PlayFootstep(intensity);
            GameEvents.RaiseNoise(_combatant, transform.position, noise, NoiseType.Footstep);
        }

        private void PlayFootstep(float intensity)
        {
            Vector3 origin = transform.position + Vector3.up * 0.3f;
            SurfaceType surface = SurfaceType.Concrete;
            if (Physics.Raycast(origin, Vector3.down, out var hit, 1.2f, LR2Layers.GroundMask, QueryTriggerInteraction.Ignore))
                surface = SurfaceDetector.Detect(hit.collider);
            bool local = _combatant != null && _combatant.IsLocalPlayer;
            ImpactSystem.PlayFootstep(transform.position, surface, intensity, local);
        }

        private void HandleLanding()
        {
            float impact = _lastFallSpeed;
            _lastFallSpeed = 0f;
            Landed?.Invoke(impact);
            if (_airborneTime > 0.25f)
            {
                PlayFootstep(Mathf.Clamp01(impact / 10f) + 0.4f);
                GameEvents.RaiseNoise(_combatant, transform.position, Mathf.Lerp(8f, 22f, impact / 15f), NoiseType.Footstep);
            }

            if (impact > fallDamageSpeed && _health != null)
            {
                var info = new DamageInfo
                {
                    amount = (impact - fallDamageSpeed) * fallDamagePerMs,
                    type = DamageType.Fall,
                    point = transform.position,
                    direction = Vector3.down,
                    weaponName = "Fall"
                };
                _health.ApplyDamage(ref info);
            }
        }

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _inputController = GetComponent<UserInputController>();
            _animator = GetComponent<Animator>();
            _combatant = GetComponent<Combatant>();
            _health = GetComponent<Health>();
        }

        private void Start()
        {
            _originalHeight = _controller.height;
            _originalCenter = _controller.center;
            if (_animator != null)
            {
                // B136: LR2Bones also resolves the generic Kinemation robot's feet
                _leftFoot = LR2Bones.Get(_animator, HumanBodyBones.LeftFoot);
                _rightFoot = LR2Bones.Get(_animator, HumanBodyBones.RightFoot);
            }
            _cachedState = State = SoldierMoveState.Idle;
            Stance = SoldierStance.Standing;
            _desiredGait = _cachedGait = Gait(GaitKind.Idle);
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized) return;
            if (State == SoldierMoveState.Mantling)
            {
                UpdateMantle();
                UpdateAnimatorParams();
                _cachedState = State;
                return;
            }

            if (State == SoldierMoveState.Sprinting) _lastSprintTime = Time.time;
            if (_standWhenClear && !CrouchHeld && Stance == SoldierStance.Crouching && State != SoldierMoveState.Sliding && CanUnCrouch())
            {
                _standWhenClear = false;
                UnCrouch();
                _desiredGait = IsMoving ? Gait(GaitKind.Walk) : Gait(GaitKind.Idle);
            }

            UpdateMovementState();
            if (_cachedState != State) OnMovementStateChanged();

            bool moving = IsMoving;
            if (_wasMoving != moving)
            {
                if (moving) onStartMoving?.Invoke();
                else onStopMoving?.Invoke();
            }
            _wasMoving = moving;

            if (State == SoldierMoveState.InAir) UpdateInAir();
            else if (State == SoldierMoveState.Sliding) UpdateSliding();
            else UpdateGrounded();

            if (_controller.enabled) _controller.Move(MoveVector * Time.deltaTime);
            UpdateAnimatorParams();
            UpdateFootsteps();

            if (_combatant != null) _combatant.Velocity = Velocity;
            _cachedState = State;
        }

        private void LateUpdate()
        {
            if (!_initialized || State == SoldierMoveState.Mantling || !_controller.enabled) return;
            bool grounded = _controller.isGrounded;
            if (!grounded) _airborneTime += Time.deltaTime;

            if (State != SoldierMoveState.InAir && !grounded && _airborneTime > 0.12f)
            {
                State = SoldierMoveState.InAir;
                if (_velocity.y > 0f) _velocity.y = 0f;
            }
            else if (State == SoldierMoveState.InAir && grounded && _velocity.y <= 0.01f)
            {
                State = SoldierMoveState.Idle;
                HandleLanding();
                _airborneTime = 0f;
            }

            if (grounded && State != SoldierMoveState.InAir) _airborneTime = 0f;
        }
    }
}
