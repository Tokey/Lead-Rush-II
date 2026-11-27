// Based on KINEMATION FPS Animation Framework demo FPSController (2025). LR2 changes: input comes from an
// ISoldierInputSource (human or AI) so bots and the player share one character; CoD-style sprint-out, ADS,
// flinch, view kick, death (ragdoll + weapon drop + death cam) and respawn; first/third person setup by role.
using System;
using System.Collections.Generic;
using System.Reflection;
using Demo.Scripts.Runtime.Character;
using KINEMATION.FPSAnimationFramework.Runtime.Camera;
using KINEMATION.FPSAnimationFramework.Runtime.Core;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.IkMotionLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Playables;
using KINEMATION.FPSAnimationFramework.Runtime.Recoil;
using KINEMATION.ProceduralRecoilAnimationSystem.Runtime;
using KINEMATION.Shared.KAnimationCore.Runtime.Input;
using KINEMATION.Shared.KAnimationCore.Runtime.Rig;
using LR2.Audio;
using LR2.Combat;
using LR2.Core;
using LR2.Weapons;
using UnityEngine;
using UnityEngine.Rendering;

namespace LR2.Characters
{
    [RequireComponent(typeof(CharacterController), typeof(SoldierMovement))]
    [DefaultExecutionOrder(-50)]
    public class SoldierController : MonoBehaviour
    {
        [SerializeField] private FPSControllerSettings settings;

        [Header("Look")]
        [SerializeField] private float minPitch = -80f;
        [SerializeField] private float maxPitch = 80f;
        [Tooltip("Max turn rate (deg/s) when a bot steers with absolute aim angles.")]
        public float maxBotTurnSpeed = 720f;

        [Header("Feel")]
        [Tooltip("Aim punch when hit (degrees per 25 damage).")]
        [SerializeField] private float flinchStrength = 1.4f;
        [SerializeField] private float flinchRecovery = 10f;
        [Tooltip("View kick recovery speed (camera returns after shots).")]
        [SerializeField] private float kickRecovery = 9f;
        [SerializeField] private float weaponSwapCooldown = 0.35f;

        [Header("View")]
        [Tooltip("Head renderers hidden (shadows only) in first person.")]
        [SerializeField] private Renderer[] firstPersonHidden = Array.Empty<Renderer>();
        [Tooltip("Eye socket used as aim origin for bots (falls back to the camera transform / head).")]
        [SerializeField] private Transform eyeSocket;

        [Header("Grenades (B136)")]
        [Tooltip("Frag grenades per life.")]
        [SerializeField, Range(0, 5)] private int grenadesPerLife = 2;
        [SerializeField] private GameObject grenadePrefab;
        [Tooltip("Fraction of the throw animation at which the grenade leaves the hand (0.335 matches C_Grenade_Toss forward whip).")]
        [SerializeField, Range(0.1f, 0.9f)] private float grenadeReleaseAt = 0.335f;
        [SerializeField] private float grenadeThrowSpeed = 18.5f;

        [Header("Death")]
        [SerializeField] private float deathCamDistance = 3.4f;
        [SerializeField] private float deathCamHeight = 1.6f;
        [SerializeField] private float droppedWeaponLifetime = 12f;

        public event Action<LR2Item> ItemEquipped;
        public event Action<SoldierController> Respawned;
        public event Action<SoldierController, DamageInfo> Died;

        // ------------------------------------------------------------------ components
        public Combatant Combatant { get; private set; }
        public Health Health { get; private set; }
        public SoldierMovement Movement { get; private set; }
        public FPSAnimator FPSAnimator { get; private set; }
        public Animator Animator { get; private set; }
        public UserInputController UserInput { get; private set; }
        public IPlayablesController Playables { get; private set; }
        public FPSCameraController CameraController { get; private set; }
        public RecoilAnimation RecoilAnimation { get; private set; }
        public RecoilPattern RecoilPattern { get; private set; }
        public Camera ViewCamera { get; private set; }
        public FPSControllerSettings Settings => settings;

        public ISoldierInputSource InputSource { get; set; }
        public bool ControlsEnabled { get; set; } = true;

        // ------------------------------------------------------------------ state
        public float Yaw => transform.eulerAngles.y;
        public float Pitch => _pitch;
        public Transform ViewTransform => ViewCamera != null && ViewCamera.isActiveAndEnabled ? ViewCamera.transform : EyeTransform;
        public Transform EyeTransform => eyeSocket != null ? eyeSocket : transform;
        public Vector3 AimOrigin { get; private set; }
        public Vector3 AimDirection { get; private set; }
        public bool IsAiming => _isAiming;
        public float AimBlend { get; private set; }
        public bool IsSprinting => Movement != null && Movement.State == SoldierMoveState.Sprinting;
        public bool IsCrouching => Movement != null && Movement.Stance == SoldierStance.Crouching;
        public bool IsProning => Movement != null && Movement.Stance == SoldierStance.Prone;
        public bool IsReloading => ActiveFirearm != null && ActiveFirearm.IsReloading;
        public bool IsDead => Health != null && !Health.IsAlive;
        public bool IsLocalPlayer => Combatant != null && Combatant.IsLocalPlayer;
        public bool IsSwitchingWeapon => _switching;
        /// <summary>B136: the gun is held up in Kinemation's inspect pose; 1/2/3 cycle barrel / grip / sight.</summary>
        public bool IsEditingAttachments => _editingAttachments;
        /// <summary>B136: a grenade throw animation is playing (hands busy).</summary>
        public bool IsThrowingGrenade => _throwing;
        /// <summary>B136: frag grenades left this life.</summary>
        public int GrenadesLeft => _grenadesLeft;
        public bool InteractTriggeredFrame { get; private set; }
        public LR2Item ActiveItem => _items.Count > 0 ? _items[_activeIndex] : null;
        public LR2Firearm ActiveFirearm => ActiveItem as LR2Firearm;
        public IReadOnlyList<LR2Item> Items => _items;
        public int ActiveSlot => _activeIndex;
        public float ScopeSensitivity => UserInput != null && _sensIndex >= 0 ? Mathf.Max(0.05f, UserInput.GetValue<float>(_sensIndex)) : 1f;
        // B155 raw look / recoil split for zero-stutter experiment telemetry
        public Vector2 LastRawLookDelta { get; private set; }
        public Vector2 LastRecoilKickDelta { get; private set; }
        public Vector2 LastMoveInput { get; private set; }
        public float LastLeanInput { get; private set; }
        public bool LastFireInput { get; private set; }

        /// <summary>False while sprinting or during the sprint-out / swap window.</summary>
        public bool CanShootNow => !IsDead && !IsSprinting && Time.time >= _sprintOutUntil && !_switching && !_throwing && !_editingAttachments;

        private readonly List<LR2Item> _items = new List<LR2Item>();
        private CharacterController _controller;
        private SoldierRagdoll _ragdoll;
        private RetargetProxy _proxy;
        private LR2PlayablesController _lr2Playables;
        private float _fullBodyGate, _proneGate;
        // D64: sprint start/end stepped Kinemation's look, stabilisation and playables weights in one frame (1 -> 0.8 / 0.78 /
        // 0.78 and back): about a fifth of the weapon pose snapped at every sprint start and end (C25: a sprint-interrupted
        // reload moved the gun 5.6 deg in the frame after the interrupt). The weights now ease toward their targets.
        private const float SprintWeightTau = 0.06f; // ~95 % in 0.18 s
        private float _lookWeight = 1f, _stabWeight = 1f, _playablesWeight = 1f;
        private float _lookTarget = 1f, _stabTarget = 1f;

        /// <summary>FullBodyWeight as LR2 applies it (D57): the Animator curve, but only while sprinting, prone or sliding.</summary>
        public float FullBodyWeightEffective { get; private set; }
        /// <summary>ProneWeight as LR2 applies it (D57): the Animator curve, but only while prone or in a prone transition.</summary>
        public float ProneWeightEffective { get; private set; }
        private Transform _weaponBone;
        private SoldierInput _input = SoldierInput.Empty;
        private int _activeIndex;
        private bool _isAiming;
        private bool _firePressed;
        private bool _prevSprintInput;
        private bool _switching;
        private int _pendingIndex = -1;
        private float _switchAt;
        private float _lastSwitchTime = -10f;
        private float _sprintOutUntil;
        private float _yawAccum;
        private float _pitch;
        private float _lastLean;
        private Vector2 _flinch, _flinchVel;
        private Vector2 _kick, _kickApplied;
        private int _sensIndex = -1;
        private bool _initialized;
        private float _nextHurtVoiceAt;
        private IkMotionLayerSettings _runtimeAimingMotion;
        // B136: Kinemation demo features
        private bool _editingAttachments;
        private bool _throwing, _grenadeReleased;
        private float _throwStart, _throwEnd, _throwRelease;
        private int _grenadesLeft;
        private GameObject _heldGrenade;
        private int _inspectLayer = -2;

        // death cam / weapon drop
        private Transform _camParent;
        private Vector3 _camLocalPos;
        private Quaternion _camLocalRot;
        private bool _hasCamBindPose;
        private Combatant _killer;
        private Vector3 _deathPos;
        private GameObject _droppedWeapon;
        public GameObject DroppedWeapon => _droppedWeapon;

        private static readonly int FullBodyWeightHash = Animator.StringToHash("FullBodyWeight");
        // Default state of every Animator layer, for both Kinemation controllers (full paths, so each only matches its own
        // layer): humanoid driver = Base Layer, TurnInPlace, IK; B136 generic robot = Locomotion, TurnInPlace, InAir, IK, AdditiveIK.
        private static readonly int[] DefaultStateHashes =
        {
            Animator.StringToHash("Base Layer.Standing"), Animator.StringToHash("Locomotion.Standing"), Animator.StringToHash("TurnInPlace.Empty"),
            Animator.StringToHash("InAir.Empty"), Animator.StringToHash("IK.Standing"), Animator.StringToHash("AdditiveIK.Empty")
        };
        private static readonly int SlideHash = Animator.StringToHash("Sliding");
        private static readonly int ProneWeightHash = Animator.StringToHash("ProneWeight");
        private static readonly int InspectStartHash = Animator.StringToHash("AdditiveIK.InspectStart");
        private static readonly int InspectEndHash = Animator.StringToHash("AdditiveIK.InspectEnd");
        // prone tree / transitions of the humanoid driver and of the generic robot (B136)
        private static readonly int[] ProneMotionHashes =
        {
            Animator.StringToHash("Base Layer.ProneTree"), Animator.StringToHash("Base Layer.C_CrouchToProne_Humanoid"),
            Animator.StringToHash("Base Layer.C_ProneToCrouch_Humanoid"),
            Animator.StringToHash("Locomotion.Prone"), Animator.StringToHash("Locomotion.C_CrouchToProne"),
            Animator.StringToHash("Locomotion.C_ProneToCrouch")
        };
        /// <summary>Seconds for the FullBodyWeight / ProneWeight gates to open or close (D57).</summary>
        private const float CurveGateSeconds = 0.25f;

        // ------------------------------------------------------------------ lifecycle
        private void Awake()
        {
            Combatant = GetComponent<Combatant>();
            Health = GetComponent<Health>();
            Movement = GetComponent<SoldierMovement>();
            FPSAnimator = GetComponent<FPSAnimator>();
            Animator = GetComponent<Animator>();
            UserInput = GetComponent<UserInputController>();
            EnsureLR2Playables();
            Playables = GetComponent<IPlayablesController>();
            RecoilAnimation = GetComponent<RecoilAnimation>();
            RecoilPattern = GetComponent<RecoilPattern>();
            _controller = GetComponent<CharacterController>();
            _ragdoll = GetComponent<SoldierRagdoll>();
            _proxy = GetComponent<RetargetProxy>();
            ViewCamera = GetComponentInChildren<Camera>(true);
            if (ViewCamera != null && ViewCamera.transform.parent != null)
            {
                _camParent = ViewCamera.transform.parent;
                _camLocalPos = ViewCamera.transform.localPosition;
                _camLocalRot = ViewCamera.transform.localRotation;
                _hasCamBindPose = true;
            }
            if (InputSource == null) InputSource = GetComponent<ISoldierInputSource>();

            if (Health != null)
            {
                Health.Damaged += OnDamaged;
                Health.Died += OnDied;
            }
            if (GetComponent<PainVoice>() == null) gameObject.AddComponent<PainVoice>();
        }

        private void OnDestroy()
        {
            if (Health != null)
            {
                Health.Damaged -= OnDamaged;
                Health.Died -= OnDied;
            }
            LR2Settings.Changed -= OnSettingsChanged;
            ClearHeldGrenade();
            if (_droppedWeapon != null) Destroy(_droppedWeapon);
            if (_runtimeAimingMotion != null) Destroy(_runtimeAimingMotion);
            foreach (var m in _ownMotions.Values) if (m != null) Destroy(m);
            _ownMotions.Clear();
        }

        private void OnSettingsChanged()
        {
            if (!_initialized || !IsLocalPlayer) return;
            if (ActiveItem != null) ActiveItem.UpdateFieldOfView(_isAiming);
        }

        private void Start()
        {
            ConfigureRole();
            if (ViewCamera != null && IsLocalPlayer) ViewCamera.fieldOfView = LR2Settings.VerticalFov(LR2Settings.FieldOfView, ViewCamera.aspect);
            if (FPSAnimator != null) FPSAnimator.Initialize();
            CameraController = ViewCamera != null && ViewCamera.gameObject.activeInHierarchy ? ViewCamera.GetComponent<FPSCameraController>() : null;
            if (CameraController != null)
            {
                var fovField = typeof(FPSCameraController).GetField("fovSpeed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fovField != null && fovField.FieldType == typeof(float))
                {
                    float cur = (float)fovField.GetValue(CameraController);
                    if (cur < 8f) fovField.SetValue(CameraController, 8f);
                }
            }
            if (GetComponent<WeaponAntiClip>() == null) gameObject.AddComponent<WeaponAntiClip>();
            if (IsLocalPlayer) LR2Settings.Changed += OnSettingsChanged;

            var rig = GetComponentInChildren<KRigComponent>();
            if (rig != null && settings != null) _weaponBone = rig.GetRigTransform(settings.weaponBone);
            if (_weaponBone == null) _weaponBone = transform;
            // B136: LR2Bones also resolves the generic Kinemation robot (GetBoneTransform only works on humanoid avatars)
            if (eyeSocket == null)
            {
                var head = LR2Bones.Get(Animator, HumanBodyBones.Head);
                eyeSocket = head != null ? head : (ViewCamera != null ? ViewCamera.transform : transform);
            }
            if (Combatant != null && Combatant.EyePoint == null)
            {
                Transform head = LR2Bones.Get(Animator, HumanBodyBones.Head);
                Transform chest = LR2Bones.Get(Animator, HumanBodyBones.Chest);
                Combatant.SetAimPoints(ViewCamera != null && IsLocalPlayer ? ViewCamera.transform : eyeSocket, head, chest);
            }

            InitializeMovement();
            InitializeWeapons();
            _grenadesLeft = grenadesPerLife; // B136
            _sensIndex = UserInput != null ? UserInput.GetPropertyIndex("SensitivityMultiplier") : -1;
            _pitch = 0f;
            _yawAccum = transform.eulerAngles.y;
            EquipImmediate(0);
            _initialized = true;
            GameEvents.RaiseSpawned(Combatant);
        }

        /// <summary>Hides the head/helmet meshes (shadows stay) - used by the killcam when looking through this soldier's eyes.</summary>
        public void SetHeadHidden(bool hidden)
        {
            foreach (var r in firstPersonHidden)
                if (r != null) r.shadowCastingMode = hidden ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
        }

        /// <summary>First person (local player) vs third person (bots): camera, listener, head visibility.</summary>
        private void ConfigureRole()
        {
            bool local = IsLocalPlayer;
            if (ViewCamera != null)
            {
                ViewCamera.gameObject.SetActive(local);
                var listener = ViewCamera.GetComponent<AudioListener>();
                if (listener != null) listener.enabled = local;
            }

            foreach (var r in firstPersonHidden)
                if (r != null) r.shadowCastingMode = local ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;

            if (local)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void InitializeMovement()
        {
            Movement.onJump = () => PlayTransitionMotion(settings != null ? settings.jumpingMotion : null);
            Movement.onLanded = () => PlayTransitionMotion(settings != null ? settings.jumpingMotion : null);
            Movement.onCrouch = () => PlayTransitionMotion(settings != null ? settings.crouchingMotion : null);
            Movement.onUncrouch = () => PlayTransitionMotion(settings != null ? settings.crouchingMotion : null);
            Movement.onProneStarted = () => PlayTransitionMotion(settings != null ? settings.crouchingMotion : null);
            Movement.onProneEnded = () => PlayTransitionMotion(settings != null ? settings.crouchingMotion : null);
            Movement._proneActionCondition = () => !_switching && !IsDead;
            Movement.onSprintStarted = OnSprintStarted;
            Movement.onSprintEnded = OnSprintEnded;
            Movement.onSlideStarted = () =>
            {
                if (Animator != null) Animator.CrossFade(SlideHash, 0.2f);
                var s = LR2CommonSounds.Instance;
                if (s != null) LR2Audio.PlayAt(s.slide, transform.position);
            };
            // The stop lurch (a 1.2 s roll of up to 9 deg) is for a moving soldier at the hip. In ADS it replaced the aiming motion
            // and kept the sight 50-65 px off target for half a second after a sprint (11-27), so a soldier who is aiming skips it.
            Movement.onStopMoving = () => { if (!_isAiming) PlayTransitionMotion(settings != null ? settings.stopMotion : null); };
            Movement._sprintActionCondition = () => !_switching && !IsDead;
            Movement._slideActionCondition = () => !_switching && !IsDead;
            Movement.Jumped += () =>
            {
                var s = LR2CommonSounds.Instance;
                if (s != null) LR2Audio.PlayAt(s.jump, transform.position);
            };
            Movement.MantleStarted += () =>
            {
                var s = LR2CommonSounds.Instance;
                if (s != null) LR2Audio.PlayAt(s.mantle, transform.position);
            };
            Movement.Landed += speed =>
            {
                var s = LR2CommonSounds.Instance;
                if (s != null && speed > 4f) LR2Audio.PlayAt(s.land, transform.position, Mathf.Clamp01(speed / 12f));
                if (IsLocalPlayer && speed > 6f) AddViewKick(new Vector2(0f, Mathf.Min(speed * 0.25f, 3f)));
            };
        }

        private void InitializeWeapons()
        {
            _items.Clear();
            if (settings == null || settings.weaponPrefabs == null) return;

            // Loadout slots: weaponPrefabs holds [0:Mk18, 1:Mk23, 2:AK12, 3:AK74N, 4:FAL, 5:Knife, 6:Torch].
            // Slot 0 = the chosen primary rifle (default MK18), 1 = MK23 sidearm, 2 = knife (key 3), 3 = torch (key 4) (B136).
            var spawnList = new List<GameObject>(4);
            if (settings.weaponPrefabs.Count >= 2)
            {
                int[] rifleSlotMap = { 0, 2, 3, 4 };
                int choice = IsLocalPlayer ? LR2.Game.LR2Session.SelectedPrimaryWeapon : 0;
                int primaryIdx = choice >= 0 && choice < rifleSlotMap.Length && rifleSlotMap[choice] < settings.weaponPrefabs.Count
                    ? rifleSlotMap[choice]
                    : 0;
                if (settings.weaponPrefabs[primaryIdx] != null) spawnList.Add(settings.weaponPrefabs[primaryIdx]);
                if (settings.weaponPrefabs[1] != null) spawnList.Add(settings.weaponPrefabs[1]);
                for (int extra = 5; extra < settings.weaponPrefabs.Count && extra <= 6; extra++)
                    if (settings.weaponPrefabs[extra] != null) spawnList.Add(settings.weaponPrefabs[extra]);
            }
            else
            {
                foreach (var p in settings.weaponPrefabs)
                    if (p != null) spawnList.Add(p);
            }

            bool allowPrimaryScopeChoice = settings.weaponPrefabs.Count >= 2;
            for (int i = 0; i < spawnList.Count; i++)
            {
                var prefab = spawnList[i];
                if (prefab == null) continue;
                var go = Instantiate(prefab, transform.position, Quaternion.identity);
                var t = go.transform;
                t.SetParent(_weaponBone, false);
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                var item = go.GetComponent<LR2Item>();
                if (item == null)
                {
                    Debug.LogWarning($"[LR2] Weapon prefab {prefab.name} has no LR2Item component.");
                    Destroy(go);
                    continue;
                }
                if (allowPrimaryScopeChoice && i == 0 && IsLocalPlayer && item is LR2Firearm primaryGun)
                    primaryGun.SetConfiguredScopeChoice(LR2.Game.LR2Session.SelectedPrimaryScope);
                _items.Add(item);
                go.SetActive(false);
            }
        }

        // ------------------------------------------------------------------ update
        /// <summary>
        /// B125 A/B switch: <c>-lr2NoItemReset</c> restores the old behaviour, where LR2's after-animation edits of the visible
        /// gun stay on its transform into the next frame and Kinemation reads them back (the bench then reproduces the bug).
        /// </summary>
        public static readonly bool ItemFeedbackReset = Array.IndexOf(Environment.GetCommandLineArgs(), "-lr2NoItemReset") < 0;
        /// <summary>B125 telemetry: how far LR2's after-animation edits had moved the visible gun off its Kinemation pose (m), read before the reset.</summary>
        public float LastItemFeedbackOffset { get; private set; }
        /// <summary>B125 telemetry: the same edit as an angle (deg).</summary>
        public float LastItemFeedbackAngle { get; private set; }

        /// <summary>
        /// B125 (user 11-19: "ADSing going wild ... during ADS, when I hit other obstacles, and maybe death"). The visible gun is a
        /// child of the driver's IK WeaponBone, and LR2 edits it after Kinemation every frame (RetargetProxy: eye-plane push, flip
        /// guard, reload bridge, prone tuck). Those edits are for the rendered frame only, but they stayed on the transform until
        /// WeaponAntiClip's LateUpdate, and Kinemation's ADS layer reads the gun's aim point from the transforms in Update
        /// (AdsLayerJob.UpdatePlayableJobData -> GetLocalAimPoint) before that. So every edit came back one frame later as an aim
        /// point offset that ADS "corrected" by moving the weapon bone the other way, and the next edit grew to undo that: near
        /// an obstacle in ADS (wall tuck pulls the gun back, eye-plane push shoves it forward) the loop has a gain of 1 and runs away.
        /// Runs before FPSAnimator.Update (order 0), so Kinemation always sees the gun at its own pose.
        /// </summary>
        private void ResetItemBeforeKinemation()
        {
            var item = ActiveItem;
            if (item == null) { LastItemFeedbackOffset = LastItemFeedbackAngle = 0f; return; }
            var t = item.transform;
            LastItemFeedbackOffset = t.localPosition.magnitude;
            LastItemFeedbackAngle = Quaternion.Angle(Quaternion.identity, t.localRotation);
            if (!ItemFeedbackReset) return;
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
        }

        private void Update()
        {
            if (!_initialized) return;
            ResetItemBeforeKinemation();

            if (IsDead)
            {
                if (IsLocalPlayer) UpdateDeathCam();
                return;
            }

            if (ControlsEnabled && InputSource != null) InputSource.GetInput(this, ref _input);
            else
            {
                bool keepLook = InputSource != null && IsLocalPlayer;
                Vector2 look = Vector2.zero;
                if (keepLook)
                {
                    InputSource.GetInput(this, ref _input);
                    look = _input.look;
                }
                _input.ClearAll();
                _input.look = look;
            }

            UpdateLook();
            UpdateAim(); // before the gun ticks: a shot fired this frame must use this frame's look, not last frame's
            UpdateActions();
            UpdateWeaponSwitch();
            if (ActiveItem != null) ActiveItem.Tick(Time.deltaTime);

            UpdateBodyCurveGates();
            float fullBody = FullBodyWeightEffective;
            float playablesTarget = IsSprinting ? Mathf.Max(0.78f, 1f - fullBody * 0.22f) : (1f - fullBody);
            float ease = 1f - Mathf.Exp(-Time.deltaTime / SprintWeightTau);
            _playablesWeight = Mathf.Lerp(_playablesWeight, playablesTarget, ease);
            _lookWeight = Mathf.Lerp(_lookWeight, _lookTarget, ease);
            _stabWeight = Mathf.Lerp(_stabWeight, _stabTarget, ease);
            if (UserInput != null)
            {
                UserInput.SetValue(FPSANames.PlayablesWeight, _playablesWeight);
                UserInput.SetValue("LookLayerWeight", _lookWeight);
                UserInput.SetValue(FPSANames.StabilizationWeight, _stabWeight);
            }

            float adsTime = ActiveFirearm != null ? ActiveFirearm.EffectiveAdsTime : 0.15f;
            AimBlend = Mathf.MoveTowards(AimBlend, _isAiming ? 1f : 0f, Time.deltaTime / adsTime);
            Movement.IsAimingSlowdown = _isAiming;

            LastMoveInput = _input.move;
            LastLeanInput = _input.lean;
            LastFireInput = _input.fire;
            InteractTriggeredFrame = _input.interact;
            _input.ResetOneShots();
        }

        /// <summary>
        /// Shot ray for this frame: from the eye (the camera, as it was last rendered; in ADS that is the sight) along the
        /// look direction the camera will render this frame. Bullets therefore land on the screen centre, which is where
        /// Kinemation's ADS layer puts the sight.
        /// </summary>
        private void UpdateAim()
        {
            Quaternion view = Quaternion.Euler(_pitch, transform.eulerAngles.y, 0f);
            AimDirection = view * Vector3.forward;
            AimOrigin = IsLocalPlayer && ViewCamera != null && ViewCamera.isActiveAndEnabled ? ViewCamera.transform.position : EyeTransform.position;
            // SoldierMovement moves the body after this (default order), so the eye is still where last frame left it:
            // at a 5 m/s strafe that put the shot about 8 cm behind the rendered crosshair. Shoot from where it will be.
            if (Movement != null) AimOrigin += Movement.Velocity * Time.deltaTime;
            if (IsLocalPlayer && AimBlend > 0.01f) AimDirection = FollowSight(view, AimDirection);
            if (Combatant != null) Combatant.ViewDirection = AimDirection;
        }

        private const float MaxSightFollowDegrees = 6f;

        /// <summary>
        /// In ADS the shot goes where the sight points. Strafing sways the gun 1.5-2 deg off the screen centre (32-47 px at
        /// 1080p, measured 11-26); with shots at the centre the developer saw them "lag behind" the red dot. The sight's
        /// offset from the camera (last frame's final pose) is applied to this frame's look direction. The recoil
        /// animation's kick is taken out first: the recoil pattern already moves the aim, and counting the kick as well
        /// would double the recoil. Capped so a gun in a wall tuck or mid-animation cannot throw the shot wide.
        /// </summary>
        private Vector3 FollowSight(Quaternion view, Vector3 centre)
        {
            var gun = ActiveFirearm;
            var sight = gun != null ? gun.ActiveAimPointTransform : null;
            var weaponBone = gun != null ? gun.transform.parent : null;
            if (sight == null || weaponBone == null || ViewCamera == null) return centre;
            Quaternion bone = weaponBone.rotation;
            Vector3 sightDir = bone * Quaternion.Inverse(gun.RecoilKickRotation) * Quaternion.Inverse(bone) * sight.forward;
            Vector3 offset = Quaternion.Inverse(ViewCamera.transform.rotation) * sightDir;
            float angle = Vector3.Angle(Vector3.forward, offset);
            if (angle > MaxSightFollowDegrees) offset = Vector3.Slerp(Vector3.forward, offset, MaxSightFollowDegrees / angle);
            return Vector3.Slerp(centre, view * offset, Mathf.Clamp01(AimBlend)).normalized;
        }

        private void UpdateLook()
        {
            float dt = Time.deltaTime;
            float deltaYaw, deltaPitch;
            if (_input.absoluteLook)
            {
                float maxStep = maxBotTurnSpeed * dt;
                deltaYaw = Mathf.Clamp(Mathf.DeltaAngle(transform.eulerAngles.y, _input.aimYaw), -maxStep, maxStep);
                deltaPitch = Mathf.Clamp(Mathf.Clamp(_input.aimPitch, minPitch, maxPitch) - _pitch, -maxStep, maxStep);
            }
            else
            {
                deltaYaw = _input.look.x;
                deltaPitch = _input.look.y;
            }
            float rawYaw = deltaYaw, rawPitch = deltaPitch;

            // Kinemation recoil pattern (camera climb).
            float recYaw = 0f, recPitch = 0f;
            if (RecoilPattern != null)
            {
                Vector2 recoil = RecoilPattern.GetRecoilDelta();
                recYaw += recoil.x;
                recPitch += recoil.y;
            }

            // Flinch (aim punch) spring and per-shot view kick that recovers.
            _flinch = Vector2.SmoothDamp(_flinch, Vector2.zero, ref _flinchVel, 1f / Mathf.Max(0.1f, flinchRecovery));
            Vector2 kickTarget = _kick;
            _kick = Vector2.Lerp(_kick, Vector2.zero, LR2Math.Damp(kickRecovery, dt));
            Vector2 kickDelta = kickTarget - _kickApplied;
            _kickApplied = kickTarget;
            recYaw += kickDelta.x + _flinch.x * dt * flinchRecovery;
            recPitch += kickDelta.y + _flinch.y * dt * flinchRecovery;
            deltaYaw += recYaw;
            deltaPitch += recPitch;
            LastRawLookDelta = new Vector2(rawYaw, rawPitch);
            LastRecoilKickDelta = new Vector2(recYaw, recPitch);

            _pitch = Mathf.Clamp(_pitch + deltaPitch, minPitch, maxPitch);
            _yawAccum += deltaYaw;
            transform.rotation *= Quaternion.Euler(0f, deltaYaw, 0f);

            if (UserInput != null)
            {
                // Feed raw look delta so RecoilPattern recognizes pull-down compensation instead of post-recoil sum.
                UserInput.SetValue(FPSANames.MouseDeltaInput, new Vector4(rawYaw, rawPitch));
                UserInput.SetValue(FPSANames.MouseInput, new Vector4(_yawAccum, _pitch));
            }
        }

        private void UpdateActions()
        {
            var item = ActiveItem;
            bool busySwitch = _switching;

            // Firing or aiming cancels sprint, then a short sprint-out delay before the gun is up (CoD feel).
            if ((_input.fire || _input.aim) && IsSprinting)
            {
                Movement.StopSprint();
                float sprintOut = ActiveFirearm != null && ActiveFirearm.Definition != null ? ActiveFirearm.Definition.sprintToFireTime : 0.2f;
                _sprintOutUntil = Time.time + sprintOut;
            }

            Movement.SetMoveInput(_input.move);
            // B16/D40 (COD): the last press wins. Pressing sprint during a reload interrupts it (the player; bots finish
            // their reloads, as a human would); a reload started while sprinting drops the sprint until it is done.
            bool sprintPressed = _input.sprint && !_prevSprintInput;
            _prevSprintInput = _input.sprint;
            if (sprintPressed && IsReloading && IsLocalPlayer) ActiveFirearm.InterruptReload();
            Movement.SetSprintRequested(_input.sprint && !_input.fire && !_input.aim && !busySwitch && !IsReloading);
            if (_input.jump) Movement.RequestJump();
            if (_input.crouch) Movement.RequestCrouchToggle();
            if (_input.prone) Movement.RequestProneToggle();
            // B90: hold X to crouch, tap X twice to slide (the second press crouches again, then the slide takes over)
            if (_input.crouchHold != Movement.CrouchHeld) Movement.SetCrouchHeld(_input.crouchHold);
            if (_input.slide) Movement.RequestSlide();

            // Lean (Kinemation lean layer: +Z rotation = lean LEFT, so Q (-1) -> +leanAngle, E (+1) -> -leanAngle)
            if (UserInput != null && settings != null && !Mathf.Approximately(_input.lean, _lastLean))
            {
                UserInput.SetValue(FPSANames.LeanInput, -_input.lean * settings.leanAngle);
                PlayTransitionMotion(settings.leanMotion);
                _lastLean = _input.lean;
            }

            UpdateGrenadeThrow();
            if (item == null) return;

            // B136: attachment editing (Kinemation's inspect pose: I to enter, then 1 barrel / 2 grip / 3 sight)
            if (_input.inspect) SetAttachmentEditing(!_editingAttachments);
            if (_editingAttachments)
            {
                bool leave = IsSprinting || _input.fire || _input.aim || _input.reload || _input.grenade || busySwitch ||
                             _input.weaponSlot >= 0 || _input.nextWeapon || !item.HasEditableAttachments;
                if (leave) SetAttachmentEditing(false);
                else
                {
                    if (_input.attachmentSlot > 0) item.OnAttachmentChanged(_input.attachmentSlot);
                    ReleaseTrigger();
                    return;
                }
            }

            // B136: grenade throw (G); the hands are busy until the throw animation ends
            if (_input.grenade) TryThrowGrenade();
            if (_throwing)
            {
                ReleaseTrigger();
                if (_isAiming) SetAim(false);
                return;
            }

            // B136: fire-mode switch (B) and sight-group cycling while aiming (V), as in Kinemation's demo
            if (_input.fireMode) item.OnChangeFireMode();
            if (_input.cycleScope && _isAiming)
            {
                item.OnCycleScope();
                PlayAimingMotion();
            }

            // Fire
            bool canFire = CanShootNow && !busySwitch;
            if (_input.fire && canFire)
            {
                if (!_firePressed)
                {
                    _firePressed = true;
                    item.OnFirePressed();
                }
            }
            else if (_firePressed)
            {
                _firePressed = false;
                item.OnFireReleased();
            }

            // Aim (ADS); the knife and the torch have no sight picture (B136)
            bool wantAim = _input.aim && !IsSprinting && !busySwitch && !IsReloading && item.SupportsAds;
            if (wantAim && !_isAiming) SetAim(true);
            else if (!wantAim && _isAiming) SetAim(false);

            if (_input.reload) RequestReload();

            if (_input.weaponSlot >= 0) BeginWeaponSwitch(_input.weaponSlot);
            else if (_input.nextWeapon && _items.Count > 1) BeginWeaponSwitch((_activeIndex + 1) % _items.Count);
        }

        private void SetAim(bool aim)
        {
            var item = ActiveItem;
            if (item == null) return;
            if (aim)
            {
                if (!item.OnAimPressed()) return;
                _isAiming = true;
            }
            else
            {
                item.OnAimReleased();
                _isAiming = false;
            }
            PlayAimingMotion();
            var s = LR2CommonSounds.Instance;
            if (s != null) LR2Audio.PlayAt(aim ? s.adsIn : s.adsOut, transform.position, IsLocalPlayer ? 1f : 0.6f);
        }

        // ADS settle (11-27). Kinemation's aiming motion tips and rolls the gun by up to 2.7 deg as it comes up. Played at the
        // old rate it ran 2.2x as long as the ADS itself, so the sight was still swinging 10-17 px on screen 0.2 s after
        // the gun had arrived. It now finishes within 1.5x the ADS time, at 60 % of its size.
        private const float AimingMotionAdsMultiple = 1.5f;
        private const float AimingMotionScale = 0.6f;
        private const float AimingMotionBlendSeconds = 0.04f;

        /// <summary>Kinemation's aiming IK motion, sped up to settle with the gun's ADS time (see AimingMotionAdsMultiple).</summary>
        private void PlayAimingMotion()
        {
            if (settings == null || settings.aimingMotion == null) return;
            var ikSrc = settings.aimingMotion;
            if (_runtimeAimingMotion == null)
            {
                _runtimeAimingMotion = Instantiate(ikSrc);
                _runtimeAimingMotion.hideFlags = HideFlags.HideAndDontSave;
                ScaleMotion(_runtimeAimingMotion, AimingMotionScale);
            }
            float adsTime = ActiveFirearm != null ? ActiveFirearm.EffectiveAdsTime : 0.15f;
            // the curves run over playback 0..1, so the motion lasts 1 / playRate seconds
            float targetRate = Mathf.Clamp(1f / (adsTime * AimingMotionAdsMultiple), 2f, 8f);
            _runtimeAimingMotion.playRate = targetRate;
            // blendTime is in playback units too: blend from the previous motion over the same time at every rate
            _runtimeAimingMotion.blendTime = Mathf.Clamp(AimingMotionBlendSeconds * targetRate, 0.05f, 0.3f);
            PlayTransitionMotion(_runtimeAimingMotion);
        }

        /// <summary>Scales a motion's rotation and translation curves (keys and tangents). For this soldier's own copy only.</summary>
        private static void ScaleMotion(IkMotionLayerSettings motion, float scale)
        {
            foreach (var curve in new[] { motion.rotationCurves.x, motion.rotationCurves.y, motion.rotationCurves.z,
                                          motion.translationCurves.x, motion.translationCurves.y, motion.translationCurves.z })
            {
                if (curve == null) continue;
                var keys = curve.keys;
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i].value *= scale;
                    keys[i].inTangent *= scale;
                    keys[i].outTangent *= scale;
                }
                curve.keys = keys;
            }
        }

        private void ReleaseTrigger()
        {
            if (!_firePressed) return;
            _firePressed = false;
            if (ActiveItem != null) ActiveItem.OnFireReleased();
        }

        // ------------------------------------------------------------------ B136: attachment editing + grenades
        private void SetAttachmentEditing(bool on)
        {
            if (on == _editingAttachments || Animator == null) return;
            var item = ActiveItem;
            if (on && (item == null || !item.HasEditableAttachments || IsReloading || _switching || IsSprinting || _throwing || IsDead)) return;
            if (_inspectLayer == -2) _inspectLayer = Animator.GetLayerIndex("AdditiveIK");
            int hash = on ? InspectStartHash : InspectEndHash;
            bool hasState = _inspectLayer >= 0 && Animator.HasState(_inspectLayer, hash);
            if (on && !hasState) return; // the humanoid driver's controller has no inspect pose
            _editingAttachments = on;
            if (on)
            {
                if (_isAiming) SetAim(false);
                ReleaseTrigger();
            }
            if (hasState) Animator.CrossFade(hash, on ? 0.2f : 0.3f, _inspectLayer);
        }

        private void TryThrowGrenade()
        {
            var item = ActiveItem;
            if (_throwing || _switching || IsDead || _grenadesLeft <= 0 || item == null || item.IsBusy) return;
            if (IsSprinting) Movement.StopSprint();
            if (_isAiming) SetAim(false);
            ReleaseTrigger();
            if (!item.OnGrenadeThrow()) return;
            float dur = Mathf.Max(0.3f, item.GrenadeThrowDuration);
            _throwing = true;
            _grenadeReleased = false;
            _throwStart = Time.time;
            _throwEnd = _throwStart + dur;
            _throwRelease = _throwStart + Mathf.Clamp(item.GrenadeReleaseDelay(grenadeReleaseAt), 0.12f, dur * 0.85f);
            SpawnHeldGrenade();
            var cs = LR2CommonSounds.Instance;
            if (cs != null && IsLocalPlayer) LR2Audio.PlayAt(cs.weaponSwap, transform.position, 0.65f, 0f, true, 1.35f);
        }

        private void SpawnHeldGrenade()
        {
            ClearHeldGrenade();
            if (grenadePrefab == null) return;
            Transform hand = null;
            var mirror = GetComponent<GenericBoneMirror>();
            if (mirror != null && mirror.VisualLeftHand != null) hand = mirror.VisualLeftHand;
            if (hand == null && Animator != null) hand = LR2Bones.Get(Animator, HumanBodyBones.LeftHand);
            if (hand == null) return;

            _heldGrenade = Instantiate(grenadePrefab, hand, false);
            _heldGrenade.name = "HeldGrenadeVisual";
            foreach (var g in _heldGrenade.GetComponentsInChildren<LR2Grenade>(true)) Destroy(g);
            foreach (var rb in _heldGrenade.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);
            foreach (var c in _heldGrenade.GetComponentsInChildren<Collider>(true)) Destroy(c);
            _heldGrenade.transform.localPosition = new Vector3(0f, 0.06f, 0.025f);
            _heldGrenade.transform.localRotation = Quaternion.Euler(15f, 0f, -20f);
        }

        private void ClearHeldGrenade()
        {
            if (_heldGrenade != null)
            {
                Destroy(_heldGrenade);
                _heldGrenade = null;
            }
        }

        private void UpdateGrenadeThrow()
        {
            if (!_throwing) return;
            if (!_grenadeReleased && Time.time >= _throwRelease)
            {
                _grenadeReleased = true;
                ReleaseGrenade();
            }
            if (Time.time >= _throwEnd)
            {
                _throwing = false;
                ClearHeldGrenade();
            }
        }

        private void ReleaseGrenade()
        {
            if (_grenadesLeft <= 0) { ClearHeldGrenade(); return; }
            _grenadesLeft--;
            Vector3 eye = AimOrigin, dir = AimDirection;
            Vector3 spawn = eye + dir * 0.45f - transform.right * 0.16f - Vector3.up * 0.08f;
            if (_heldGrenade != null && (_heldGrenade.transform.position - eye).sqrMagnitude < 1.1f * 1.1f)
                spawn = _heldGrenade.transform.position;
            else
            {
                var mirror = GetComponent<GenericBoneMirror>();
                Transform hand = (mirror != null && mirror.VisualLeftHand != null)
                    ? mirror.VisualLeftHand
                    : LR2Bones.Get(Animator, HumanBodyBones.LeftHand);
                if (hand != null && (hand.position - eye).sqrMagnitude < 1.0f * 1.0f) spawn = hand.position;
            }
            ClearHeldGrenade();
            if (Physics.Linecast(eye, spawn, out var block, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                spawn = block.point - (spawn - eye).normalized * 0.12f;
            Vector3 velocity = (dir + Vector3.up * 0.12f).normalized * grenadeThrowSpeed + (Movement != null ? Movement.Velocity * 0.6f : Vector3.zero);
            LR2Grenade.Throw(grenadePrefab, Combatant, spawn, velocity);
            var cs = LR2CommonSounds.Instance;
            if (cs != null) LR2Audio.PlayAt(cs.mantle, spawn, 0.72f, 0f, false, 1.3f);
        }

        public bool RequestReload()
        {
            var item = ActiveItem;
            if (item == null || _switching || IsDead || _throwing || _editingAttachments) return false;
            if (_isAiming) SetAim(false);
            return item.OnReload();
        }

        // ------------------------------------------------------------------ weapons
        private void BeginWeaponSwitch(int index)
        {
            if (index == _activeIndex || index < 0 || index >= _items.Count || _switching || _throwing) return;
            if (Time.time - _lastSwitchTime < weaponSwapCooldown) return;
            SetAttachmentEditing(false);
            _lastSwitchTime = Time.time;
            if (_isAiming) SetAim(false);
            if (_firePressed)
            {
                _firePressed = false;
                ActiveItem.OnFireReleased();
            }
            ActiveItem.OnUnEquip();
            _switching = true;
            _pendingIndex = index;
            _switchAt = Time.time + (settings != null ? settings.equipDelay : 0.3f);
            var s = LR2CommonSounds.Instance;
            if (s != null) LR2Audio.PlayAt(s.weaponSwap, transform.position, IsLocalPlayer ? 1f : 0.6f);
        }

        private void UpdateWeaponSwitch()
        {
            if (!_switching || Time.time < _switchAt) return;
            _switching = false;
            EquipImmediate(_pendingIndex);
        }

        private void EquipImmediate(int index)
        {
            if (_items.Count == 0) return;
            index = Mathf.Clamp(index, 0, _items.Count - 1);
            for (int i = 0; i < _items.Count; i++)
                if (i != index && _items[i] != null) _items[i].gameObject.SetActive(false);
            _activeIndex = index;
            var item = _items[index];
            item.gameObject.SetActive(true);
            item.OnEquip(this);
            ItemEquipped?.Invoke(item);
        }

        public void RefillAmmo()
        {
            foreach (var item in _items) item.RefillAmmo();
            _grenadesLeft = grenadesPerLife; // B136
        }

        public int AddReserveAmmo(int rounds)
        {
            int accepted = 0;
            if (ActiveItem != null) accepted += ActiveItem.AddReserveAmmo(rounds);
            foreach (var item in _items)
                if (item != ActiveItem) accepted += item.AddReserveAmmo(rounds / 2);
            return accepted;
        }

        /// <summary>Adds a per-shot camera kick (x = yaw, y = pitch; negative pitch = up).</summary>
        public void AddViewKick(Vector2 kick) => _kick += kick;

        private void PlayTransitionMotion(FPSAnimatorLayerSettings layerSettings)
        {
            if (layerSettings == null || FPSAnimator == null) return;
            FPSAnimator.LinkAnimatorLayer(layerSettings is IkMotionLayerSettings ik ? OwnMotion(ik) : layerSettings);
        }

        /// <summary>
        /// B125 A/B switch: <c>-lr2SharedIkMotions</c> links the shared motion assets as before (the known-bad case).
        /// </summary>
        public static readonly bool PerSoldierIkMotions = Array.IndexOf(Environment.GetCommandLineArgs(), "-lr2SharedIkMotions") < 0;
        private readonly Dictionary<IkMotionLayerSettings, IkMotionLayerSettings> _ownMotions = new Dictionary<IkMotionLayerSettings, IkMotionLayerSettings>();

        /// <summary>
        /// B125 root cause. Kinemation's IkMotionLayerJob evaluates the motion's AnimationCurves inside the animation job
        /// (IkMotionLayerJob.ProcessAnimation -> VectorCurve.GetValue -> AnimationCurve.Evaluate), and Unity evaluates different
        /// soldiers' animation jobs in parallel on worker threads. The stop, crouch, lean, jump, equip and unequip motions were
        /// shared assets, so two soldiers playing the same motion in the same frame evaluated the same curve on two threads at
        /// once. AnimationCurve caches the last segment it used and is not safe for that (Unity issue tracker: "you may evaluate a
        /// curve in a thread, but not the same curve in multiple threads"): now and then one of them read another segment's
        /// cubic and got a garbage angle, and the gun's IK WeaponBone turned 60-180 deg for that single frame. The black-box
        /// bench showed exactly that: the gun's rotation relative to the head jumping for one frame and back, mostly in combat.
        /// Each soldier now plays its own copy of every motion (own curve objects), so a curve is only ever read by one job.
        /// </summary>
        public IkMotionLayerSettings OwnMotion(IkMotionLayerSettings shared)
        {
            if (shared == null || !PerSoldierIkMotions) return shared;
            if (_ownMotions.TryGetValue(shared, out var own) && own != null) return own;
            if (shared.hideFlags == HideFlags.HideAndDontSave) return shared; // already a runtime copy (the ADS-rate aiming motion)
            own = Instantiate(shared);
            own.name = shared.name + "_" + name;
            own.hideFlags = HideFlags.HideAndDontSave;
            _ownMotions[shared] = own;
            return own;
        }

        private void OnSprintStarted()
        {
            // B16/D40: sprint interrupts the reload (COD); before this the reload kept running hidden in the sprint carry.
            if (ActiveFirearm != null && ActiveFirearm.IsReloading) ActiveFirearm.InterruptReload();
            if (_firePressed && ActiveItem != null)
            {
                _firePressed = false;
                ActiveItem.OnFireReleased();
            }
            if (_isAiming) SetAim(false);
            // targets only: Update eases the Kinemation weights toward them (D64)
            _stabTarget = 0.78f;
            _lookTarget = 0.8f;
        }

        private void OnSprintEnded()
        {
            _stabTarget = 1f;
            _lookTarget = 1f;
        }

        // ------------------------------------------------------------------ damage / death / respawn
        private void OnDamaged(DamageInfo info)
        {
            if (IsDead) return;
            float scale = Mathf.Clamp(info.amount / 25f, 0.3f, 2f) * flinchStrength * (_isAiming ? 0.7f : 1f);
            _flinch += new Vector2(UnityEngine.Random.Range(-1f, 1f), -UnityEngine.Random.Range(0.4f, 1f)) * scale;

        }

        private void ForceResetAimState()
        {
            _isAiming = false;
            AimBlend = 0f;
            if (ActiveItem != null) ActiveItem.OnAimReleased();
            if (UserInput != null)
            {
                UserInput.SetValue(FPSANames.IsAiming, false);
                UserInput.SetValue("AimingWeight", 0f);
            }
            if (RecoilAnimation != null)
            {
                RecoilAnimation.isAiming = false;
                RecoilAnimation.Stop();
            }
            if (ViewCamera != null && Movement != null)
            {
                float hipFovH = IsLocalPlayer ? LR2Settings.FieldOfView : 90f;
                ViewCamera.fieldOfView = LR2Settings.VerticalFov(hipFovH, ViewCamera.aspect);
            }
        }

        /// <summary>D57: put <see cref="LR2PlayablesController"/> (gated FullBodyWeight/ProneWeight) in place of Kinemation's
        /// stock controller before Kinemation initialises in Start. The stock one has no Awake and holds only a mask and
        /// editor-preview fields, which are copied over.</summary>
        private void EnsureLR2Playables()
        {
            _lr2Playables = GetComponent<LR2PlayablesController>();
            if (_lr2Playables != null) return;
            var stock = GetComponent<FPSPlayablesController>();
            if (stock == null) return;
            string fields = JsonUtility.ToJson(stock);
            DestroyImmediate(stock);
            _lr2Playables = gameObject.AddComponent<LR2PlayablesController>();
            JsonUtility.FromJsonOverwrite(fields, _lr2Playables);
        }

        private bool DriverInProneMotion()
        {
            if (Animator == null || !Animator.isActiveAndEnabled) return false;
            int cur = Animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
            if (Array.IndexOf(ProneMotionHashes, cur) >= 0) return true;
            if (!Animator.IsInTransition(0)) return false;
            int next = Animator.GetNextAnimatorStateInfo(0).fullPathHash;
            return Array.IndexOf(ProneMotionHashes, next) >= 0;
        }

        /// <summary>D57: decide when the arms follow the body animation and hand Kinemation the gated curve values.</summary>
        private void UpdateBodyCurveGates()
        {
            float rawFullBody = Animator != null ? Animator.GetFloat(FullBodyWeightHash) : 0f;
            float rawProne = Animator != null ? Animator.GetFloat(ProneWeightHash) : 0f;
            bool proneMotion = IsProning || DriverInProneMotion();
            bool armsFollowBody = IsSprinting || proneMotion || (Movement != null && Movement.State == SoldierMoveState.Sliding);
            float step = Time.deltaTime / CurveGateSeconds;
            _fullBodyGate = Mathf.MoveTowards(_fullBodyGate, armsFollowBody ? 1f : 0f, step);
            _proneGate = Mathf.MoveTowards(_proneGate, proneMotion ? 1f : 0f, step);
            FullBodyWeightEffective = Mathf.Clamp01(rawFullBody) * _fullBodyGate;
            ProneWeightEffective = Mathf.Clamp01(rawProne) * _proneGate;
            if (_lr2Playables != null)
            {
                _lr2Playables.FullBodyOverride = FullBodyWeightEffective;
                _lr2Playables.ProneOverride = ProneWeightEffective;
            }
        }

        /// <summary>
        /// D56: start a life from a clean animation state. Death disables the driver Animator in whatever state it was in
        /// (sprint blend, prone, crouch transition) and the Kinemation inputs keep their last values; re-enabling it at the
        /// spawn resumed all of that: dying prone replayed the prone-to-crouch-to-stand get-up for 3 s with the gun rolled
        /// 22 degrees, dying mid-sprint kept the sprint look/stabilisation weights for the whole next life (C17).
        /// </summary>
        private void ResetAnimationState()
        {
            _lookWeight = _lookTarget = _stabWeight = _stabTarget = _playablesWeight = 1f; // a new life starts settled
            _lastLean = 0f;
            if (UserInput != null)
            {
                UserInput.SetValue(FPSANames.StabilizationWeight, 1f);
                UserInput.SetValue("LookLayerWeight", 1f);
                UserInput.SetValue(FPSANames.PlayablesWeight, 1f);
                UserInput.SetValue(FPSANames.LeanInput, 0f);
                UserInput.SetValue(FPSANames.MouseDeltaInput, Vector4.zero);
                UserInput.SetValue(FPSANames.MouseInput, new Vector4(_yawAccum, 0f, 0f, 0f));
                UserInput.SetValue(FPSANames.TurnOffset, 0f);
            }
            if (Movement != null) Movement.ResetAnimatorParams();
            if (Animator != null && Animator.isActiveAndEnabled && Animator.runtimeAnimatorController != null)
            {
                for (int layer = 0; layer < Animator.layerCount; layer++)
                    foreach (int h in DefaultStateHashes)
                        if (Animator.HasState(layer, h)) { Animator.Play(h, layer, 0f); break; }
                Animator.Update(0f);
            }
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i] == null) continue;
                _items[i].transform.localPosition = Vector3.zero;
                _items[i].transform.localRotation = Quaternion.identity;
            }
            _editingAttachments = false;
            _throwing = false;
            ClearHeldGrenade();
            if (_proxy != null) _proxy.ResetTransientState();
            // the body-curve gates start closed: a stale prone/sprint curve must not reach the new life's first frame
            _fullBodyGate = _proneGate = 0f;
            FullBodyWeightEffective = ProneWeightEffective = 0f;
            if (_lr2Playables != null) _lr2Playables.FullBodyOverride = _lr2Playables.ProneOverride = 0f;
        }

        private void OnDied(DamageInfo info)
        {
            _killer = info.attacker;
            _deathPos = transform.position;
            _editingAttachments = false; // B136
            _throwing = false;
            ClearHeldGrenade();
            if (_firePressed && ActiveItem != null) ActiveItem.OnFireReleased();
            _firePressed = false;
            if (ActiveItem != null) ActiveItem.CancelActions();
            ForceResetAimState();
            _switching = false;
            _input.ClearAll();
            Movement.ResetState();
            Movement.enabled = false;
            _controller.enabled = false;

            if (GetComponent<PainVoice>() == null)
            {
                var s = LR2CommonSounds.Instance;
                if (s != null) LR2Audio.PlayAt(s.bodyFall, transform.position, 1f, 0.45f);
            }

            if (CameraController != null) CameraController.enabled = false;
            if (IsLocalPlayer)
            {
                SetHeadHidden(false);
                BeginDeathCam();
            }
            if (FPSAnimator != null) FPSAnimator.enabled = false;
            if (_ragdoll != null) _ragdoll.Activate(info);
            else if (Animator != null) Animator.enabled = false;

            DropWeapon(info);
            Died?.Invoke(this, info);
        }

        private void DropWeapon(DamageInfo info)
        {
            var item = ActiveItem;
            if (item == null) return;
            // B59b: Spawn slightly forward/right of chest & drop forward/downward so close-range attacker fire
            // never drives the dropped weapon backwards through the victim's torso.
            // D56: start from where the gun really is. A fixed standing chest height (1.12 m) made the gun jump up 0.4 m
            // when a crouched soldier died and 0.8 m when a prone one did ("dead while crouching puts the gun up").
            Vector3 fwd = transform.forward;
            Vector3 dropPos = item.transform.position;
            float ahead = Vector3.Dot(dropPos - transform.position, fwd);
            if (ahead < 0.28f) dropPos += fwd * (0.28f - ahead);
            float height = dropPos.y - transform.position.y;
            dropPos.y = transform.position.y + Mathf.Clamp(height, 0.25f, 1.45f);
            _droppedWeapon = Instantiate(item.gameObject, dropPos, item.transform.rotation);
            foreach (var b in _droppedWeapon.GetComponentsInChildren<MonoBehaviour>(true)) Destroy(b);
            foreach (var a in _droppedWeapon.GetComponentsInChildren<Animator>(true)) Destroy(a);
            foreach (var l in _droppedWeapon.GetComponentsInChildren<Light>(true)) Destroy(l);
            foreach (var p in _droppedWeapon.GetComponentsInChildren<ParticleSystem>(true)) Destroy(p.gameObject);
            // the MK18 carries four scope cameras (Camera is not a MonoBehaviour, so the strip above kept them): a dropped
            // copy must not render anything
            foreach (var cam in _droppedWeapon.GetComponentsInChildren<Camera>(true)) { cam.enabled = false; cam.targetTexture = null; }
            // the laser beam and dot are plain renderers (the WeaponLaser script itself is stripped above), so a dead soldier's
            // dropped gun kept a frozen beam (user 11-18: "turn off enemies laser once they die")
            foreach (var lr in _droppedWeapon.GetComponentsInChildren<LineRenderer>(true)) Destroy(lr);
            foreach (var t in _droppedWeapon.GetComponentsInChildren<Transform>(true))
                if (t != null && (t.name == "LaserDot" || t.name == "LaserBeam")) Destroy(t.gameObject);
            SetLayerRecursive(_droppedWeapon.transform, LR2Layers.Debris);
            var bounds = new Bounds(_droppedWeapon.transform.position, Vector3.zero);
            foreach (var r in _droppedWeapon.GetComponentsInChildren<Renderer>())
            {
                // the laser beam (up to 100 m long) and its dot at the hit point would drag the collider centre far away
                if (r is LineRenderer || r is TrailRenderer || r is ParticleSystemRenderer || r.name == "LaserDot") continue;
                bounds.Encapsulate(r.bounds);
            }
            var box = _droppedWeapon.AddComponent<BoxCollider>();
            box.center = _droppedWeapon.transform.InverseTransformPoint(bounds.center);
            box.size = new Vector3(0.08f, 0.2f, 0.8f);
            var rb = _droppedWeapon.AddComponent<Rigidbody>();
            rb.mass = 3.5f;
            rb.linearVelocity = transform.forward * 0.9f + transform.right * 0.45f + Vector3.up * 0.25f;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 3.5f;
            Destroy(_droppedWeapon, droppedWeaponLifetime);
            item.gameObject.SetActive(false);
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        private void BeginDeathCam()
        {
            if (ViewCamera == null) return;
            var cam = ViewCamera.transform;
            if (!_hasCamBindPose && cam.parent != null)
            {
                _camParent = cam.parent;
                _camLocalPos = cam.localPosition;
                _camLocalRot = cam.localRotation;
                _hasCamBindPose = true;
            }
            cam.SetParent(null, true);
        }

        private void UpdateDeathCam()
        {
            if (ViewCamera == null || ViewCamera.transform.parent != null) return;
            var cam = ViewCamera.transform;
            Vector3 focus = _deathPos + Vector3.up * 0.6f;
            Vector3 lookTarget = _killer != null && _killer.IsAlive ? _killer.ChestPosition : focus;
            Vector3 away = (focus - lookTarget);
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = -transform.forward;
            Vector3 desired = focus + away.normalized * deathCamDistance + Vector3.up * deathCamHeight;
            if (Physics.Linecast(focus, desired, out var block, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                desired = block.point + block.normal * 0.25f;
            cam.position = Vector3.Lerp(cam.position, desired, LR2Math.Damp(3f, Time.deltaTime));
            Quaternion look = Quaternion.LookRotation((lookTarget - cam.position).normalized);
            cam.rotation = Quaternion.Slerp(cam.rotation, look, LR2Math.Damp(4f, Time.deltaTime));
        }

        private void EndDeathCam()
        {
            if (ViewCamera == null || !_hasCamBindPose || _camParent == null) return;
            var cam = ViewCamera.transform;
            cam.SetParent(_camParent, false);
            cam.localPosition = _camLocalPos;
            cam.localRotation = _camLocalRot;
            ClearCameraPlayback(CameraController);
            var bodycam = ViewCamera.GetComponent<BodycamRig>();
            if (bodycam != null) bodycam.ResetState();
        }

        private static FieldInfo[] _cameraPlaybackFields;

        /// <summary>
        /// FPSAnimator is disabled while dead, so a camera shake or reload camera animation that was playing at the moment
        /// of death freezes and would finish playing after respawn. Kinemation's FPSCameraController has no public reset,
        /// so its protected playback state is cleared here (the vendor code treats a null shake or animation as idle).
        /// </summary>
        private static void ClearCameraPlayback(FPSCameraController controller)
        {
            if (controller == null) return;
            if (_cameraPlaybackFields == null)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(FPSCameraController);
                _cameraPlaybackFields = new[]
                {
                    type.GetField("_activeShake", flags), type.GetField("_cameraShake", flags),
                    type.GetField("_cameraShakeTarget", flags), type.GetField("_cameraShakePlayback", flags),
                    type.GetField("_cameraAnimationAsset", flags), type.GetField("_activeCameraAnimation", flags),
                    type.GetField("_cachedCameraAnimation", flags), type.GetField("_animationPlayback", flags),
                    type.GetField("_animationLength", flags),
                };
            }
            foreach (var field in _cameraPlaybackFields)
            {
                if (field == null) continue;
                object idle = field.FieldType == typeof(KINEMATION.Shared.KAnimationCore.Runtime.Core.KTransform)
                    ? KINEMATION.Shared.KAnimationCore.Runtime.Core.KTransform.Identity
                    : field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null;
                field.SetValue(controller, idle);
            }
        }

        public void Teleport(Vector3 position, float yaw)
        {
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            _yawAccum = yaw;
            _pitch = 0f;
            Movement.Teleport(position);
        }

        /// <summary>Revives the soldier at a spawn: health, ammo, pose, controls, camera.</summary>
        public void Respawn(Vector3 position, float yaw)
        {
            if (_droppedWeapon != null) Destroy(_droppedWeapon);
            bool wasDead = IsDead;
            // B136h: Restore bone rest transforms + rebind Animator BEFORE rebuilding Kinemation playables so
            // PoseSampler/BlendingLayer/AttachHandLayer never capture crumpled corpse angles into the next life.
            if (_ragdoll != null) _ragdoll.Deactivate();
            // Re-parent ViewCamera under Head BEFORE Animator.Rebind() and RebuildPlayables() so Kinemation binds Head/Camera stream handles cleanly.
            EndDeathCam();
            if (Animator != null)
            {
                Animator.enabled = true;
                if (wasDead) Animator.Rebind();
            }
            if (CameraController != null) CameraController.enabled = true;

            _controller.enabled = true;
            Movement.enabled = true;
            Teleport(position, yaw);
            _flinch = _flinchVel = _kick = _kickApplied = Vector2.zero;
            ForceResetAimState();
            _firePressed = false;
            _switching = false;
            _sprintOutUntil = 0f;
            _input.ClearAll();
            ResetAnimationState();

            if (FPSAnimator != null)
            {
                FPSAnimator.enabled = true;
                // Kinemation only rebuilds when it *sees* the Animator re-enable; force it after bone rest + Animator reset.
                if (wasDead) FPSAnimator.RebuildPlayables();
            }

            if (Health != null) Health.Revive();
            RefillAmmo();
            EquipImmediate(0);
            ForceResetAimState();
            ResetAnimationState();
            if (Combatant != null) Combatant.SpawnTime = Time.time;
            ConfigureRole();
            Respawned?.Invoke(this);
            GameEvents.RaiseSpawned(Combatant);
        }
    }
}
