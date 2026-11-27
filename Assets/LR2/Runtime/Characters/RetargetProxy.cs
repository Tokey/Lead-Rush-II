using System.Collections.Generic;
using LR2.Combat;
using UnityEngine;

namespace LR2.Characters
{
    /// <summary>
    /// Visual body proxy. Kinemation's humanoid FPS rig (the hidden "driver") does every animation (grip, ADS, reloads,
    /// equips for every demo weapon) and carries the first-person camera on its head. Every frame this copies the
    /// driver's pose onto the SWAT model through Unity's humanoid muscle space, then:
    /// <list type="number">
    /// <item>Third person (bots, remote players): slides the SWAT hips horizontally onto the driver's hips, so the body,
    /// its hitboxes and the gun all line up.</item>
    /// <item>First person (local player): pins the SWAT head onto the driver's head, so the camera sits at the soldier's
    /// own eyes, nudged back a little so the collar never reaches the lens. Leg IK then re-plants the feet.</item>
    /// <item>Two-bone IK locks the SWAT hands onto the driver's hands (fingers on grip / handguard / magazine).</item>
    /// </list>
    /// Stops on death so the SWAT ragdoll takes over.
    /// </summary>
    [DefaultExecutionOrder(250)] // legacy humanoid SWAT proxy, unused on the robot soldier; after BodycamRig (120), WeaponAntiClip (200) and FPSAnimator.LateUpdate
    public class RetargetProxy : MonoBehaviour
    {
        [SerializeField] private Animator driver;
        [SerializeField] private Animator visual;

        [Header("Hands")]
        [Tooltip("Lock the visual hands onto the driver hands (grip accuracy).")]
        [SerializeField] private bool handIK = true;
        [SerializeField, Range(0f, 1f)] private float handIKWeight = 1f;

        [Header("Body alignment")]
        [Tooltip("Third person: slide the visible body so its hips sit exactly over the driver's hips (body, hitboxes and gun line up).")]
        [SerializeField] private bool alignHips = true;
        [Tooltip("First person (local player): move the visible body so its EYES sit at the camera (the driver's eye socket).")]
        [SerializeField] private bool firstPersonHeadLock = true;
        [Tooltip("Visible model's eyes: height above its head joint, along the head's up axis (m).")]
        [SerializeField, Range(0f, 0.2f)] private float eyeHeight = 0.085f;
        [Tooltip("Visible model's eyes: distance in front of its head joint (m).")]
        [SerializeField, Range(0f, 0.2f)] private float eyeForward = 0.095f;
        [Tooltip("First person: shift the visible body this far back along the view (m), so the collar and hood stay behind the lens.")]
        [SerializeField, Range(0f, 0.2f)] private float firstPersonBodyBack = 0.02f;
        [Tooltip("First person: re-plant the feet with leg IK after the head lock moved the body.")]
        [SerializeField] private bool replantFeet = true;
        [Tooltip("B60b: HyperMotion contact-aware foot-plant lock pins the grounded stance foot XZ while walking upright.")]
        [SerializeField] private bool footPlantLock = true;
        [Tooltip("Log alignment numbers once, 1.5 s after spawn.")]
        [SerializeField] private bool logAlignment = true;

        private HumanPoseHandler _driverHandler, _visualHandler;
        private HumanPose _pose;
        private Health _health;
        private SoldierController _soldier;
        private Transform _dL, _dR, _dHead, _dHips;
        private Transform _vHips, _vHead, _vHeadTop;
        private Transform _vChest;
        private HitReactions _hitReactions;
        private Vector3 _eyeUpLocal = Vector3.up, _eyeFwdLocal = Vector3.forward;
        private bool _eyeAxesReady;
        private Transform _lUpper, _lLower, _lHand, _rUpper, _rLower, _rHand;
        private Transform _lThigh, _lShin, _lFoot, _rThigh, _rShin, _rFoot;
        private Quaternion _lOffset = Quaternion.identity, _rOffset = Quaternion.identity;
        private ReloadMotionBridge _bridge;
        private bool _offsetsReady;
        private int _frames;
        // B60a: HyperMotion-style C2 inertialization offset decay across weapon/reload/sprint transitions.
        private bool _hasPrevHands, _prevReloading, _prevSprinting, _prevAiming;
        private Transform _prevItemTf;
        private Vector3 _prevLPos, _prevRPos, _lInertialOffset, _rInertialOffset;
        private const float InertialHalfLifeSeconds = 0.045f;
        // B60b: HyperMotion contact-aware foot-plant XZ lock state.
        private bool _hasLeftPlant, _hasRightPlant;
        private Vector3 _leftPlantPos, _rightPlantPos, _prevRootPos;
        private bool _hasPrevRootPos;
        private static readonly int ProneWeightHash = Animator.StringToHash("ProneWeight");
        private static readonly List<int> _lProxIdx = new List<int>();
        private static readonly List<int> _lFingerIdx = new List<int>();
        private static readonly List<int> _lThumbProxIdx = new List<int>();
        private static readonly List<int> _lThumbDistIdx = new List<int>();
        private static readonly List<int> _rIndexIdx = new List<int>();
        private static readonly List<int> _rIndexProxIdx = new List<int>();
        private static readonly List<int> _rIndexDistIdx = new List<int>();
        private static readonly List<int> _rIndexSpreadIdx = new List<int>();
        private static readonly List<int> _rFingerIdx = new List<int>();
        private static readonly List<int> _rThumbProxIdx = new List<int>();
        private static readonly List<int> _rThumbDistIdx = new List<int>();
        private static readonly List<int> _lThumbSpreadIdx = new List<int>();
        private static readonly List<int> _rThumbSpreadIdx = new List<int>();
        private static readonly List<int> _spreadIdx = new List<int>();
        private static bool _fingerCacheReady;

        public Animator Driver => driver;
        public Animator Visual => visual;

        /// <summary>Distance between driver and visual hips right after the muscle-space copy (before alignment).</summary>
        public float RawHipsDelta { get; private set; }
        /// <summary>Driver head minus visual head right after the muscle-space copy (before alignment), world space.</summary>
        public Vector3 RawHeadDelta { get; private set; }
        /// <summary>World-space shift applied to the visual skeleton this frame.</summary>
        public Vector3 AppliedShift { get; private set; }
        /// <summary>Residual hand-IK errors (m) after solving.</summary>
        public float HandErrorLeft { get; private set; }
        public float HandErrorRight { get; private set; }
        /// <summary>True while the first-person eye lock is active.</summary>
        public bool FirstPersonActive { get; private set; }

        /// <summary>Estimated eye point of the visible model (world), from its head joint, eyeHeight and eyeForward.</summary>
        public Vector3 VisualEyePosition
        {
            get
            {
                if (_vHead == null) return transform.position;
                EnsureEyeAxes();
                return _vHead.position + _vHead.rotation * (_eyeUpLocal * eyeHeight + _eyeFwdLocal * eyeForward);
            }
        }

        /// <summary>
        /// Head-local "up" (head joint to head top) and "forward" (character facing, perpendicular to up), captured once
        /// from the retargeted pose so the eye point follows every head tilt afterwards.
        /// </summary>
        private void EnsureEyeAxes()
        {
            if (_eyeAxesReady || _vHead == null) return;
            Vector3 up = _vHeadTop != null ? (_vHeadTop.position - _vHead.position) : transform.up;
            if (up.sqrMagnitude < 1e-6f) up = transform.up;
            up.Normalize();
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, up);
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.ProjectOnPlane(transform.forward + transform.up * 0.1f, up);
            fwd.Normalize();
            Quaternion inv = Quaternion.Inverse(_vHead.rotation);
            _eyeUpLocal = inv * up;
            _eyeFwdLocal = inv * fwd;
            _eyeAxesReady = true;
        }

        public void Configure(Animator driverAnimator, Animator visualAnimator)
        {
            driver = driverAnimator;
            visual = visualAnimator;
        }

        private void Start()
        {
            _health = GetComponentInParent<Health>();
            _soldier = GetComponentInParent<SoldierController>();
            if (driver == null || visual == null || driver.avatar == null || visual.avatar == null || !driver.avatar.isHuman || !visual.avatar.isHuman)
            {
                Debug.LogWarning("[LR2] RetargetProxy needs two humanoid animators", this);
                enabled = false;
                return;
            }
            _driverHandler = new HumanPoseHandler(driver.avatar, driver.transform);
            _visualHandler = new HumanPoseHandler(visual.avatar, visual.transform);
            _dL = driver.GetBoneTransform(HumanBodyBones.LeftHand);
            _dR = driver.GetBoneTransform(HumanBodyBones.RightHand);
            _dHead = driver.GetBoneTransform(HumanBodyBones.Head);
            _dHips = driver.GetBoneTransform(HumanBodyBones.Hips);
            _vHips = visual.GetBoneTransform(HumanBodyBones.Hips);
            _vHead = visual.GetBoneTransform(HumanBodyBones.Head);
            _vChest = visual.GetBoneTransform(HumanBodyBones.Chest) ?? visual.GetBoneTransform(HumanBodyBones.Spine);
            _hitReactions = GetComponentInParent<HitReactions>();
            _vHeadTop = _vHead != null && _vHead.childCount > 0 ? FindHeadTop(_vHead) : null;
            _lUpper = visual.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _lLower = visual.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _lHand = visual.GetBoneTransform(HumanBodyBones.LeftHand);
            _rUpper = visual.GetBoneTransform(HumanBodyBones.RightUpperArm);
            _rLower = visual.GetBoneTransform(HumanBodyBones.RightLowerArm);
            _rHand = visual.GetBoneTransform(HumanBodyBones.RightHand);
            _lThigh = visual.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            _lShin = visual.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            _lFoot = visual.GetBoneTransform(HumanBodyBones.LeftFoot);
            _rThigh = visual.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            _rShin = visual.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            _rFoot = visual.GetBoneTransform(HumanBodyBones.RightFoot);
            _bridge = new ReloadMotionBridge(driver.transform, _soldier != null ? _soldier.Playables : null);
            EnsureFingerCache();
        }

        private static void EnsureFingerCache()
        {
            if (_fingerCacheReady) return;
            _fingerCacheReady = true;
            string[] names = HumanTrait.MuscleName;
            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i];
                bool isLeft = n.StartsWith("Left ");
                bool isRight = n.StartsWith("Right ");
                if (!isLeft && !isRight) continue;

                if (n.Contains("Spread"))
                {
                    if (isLeft && n.Contains("Thumb")) _lThumbSpreadIdx.Add(i);
                    else if (isRight && n.Contains("Thumb")) _rThumbSpreadIdx.Add(i);
                    else if (isRight && n.Contains("Index")) _rIndexSpreadIdx.Add(i);
                    else if (n.Contains("Index") || n.Contains("Middle") || n.Contains("Ring") || n.Contains("Little")) _spreadIdx.Add(i);
                    continue;
                }
                if (!n.Contains("Stretched")) continue;

                if (n.Contains("Thumb"))
                {
                    if (isLeft) { if (n.Contains("1 Stretched")) _lThumbProxIdx.Add(i); else _lThumbDistIdx.Add(i); }
                    else { if (n.Contains("1 Stretched")) _rThumbProxIdx.Add(i); else _rThumbDistIdx.Add(i); }
                }
                else if (isLeft && (n.Contains("Index") || n.Contains("Middle") || n.Contains("Ring") || n.Contains("Little")))
                {
                    if (n.Contains("1 Stretched")) _lProxIdx.Add(i);
                    else _lFingerIdx.Add(i);
                }
                else if (isRight)
                {
                    if (n.Contains("Index"))
                    {
                        _rIndexIdx.Add(i);
                        if (n.Contains("1 Stretched")) _rIndexProxIdx.Add(i); else _rIndexDistIdx.Add(i);
                    }
                    else if (n.Contains("Middle") || n.Contains("Ring") || n.Contains("Little")) _rFingerIdx.Add(i);
                }
            }
        }

        /// <summary>The head's end bone (Mixamo "HeadTop_End"): the child furthest from the head joint, skipping eyes/jaw.</summary>
        private static Transform FindHeadTop(Transform head)
        {
            Transform best = null;
            float bestD = 0f;
            foreach (Transform c in head)
            {
                string n = c.name.ToLowerInvariant();
                if (n.Contains("eye") || n.Contains("jaw")) continue;
                float d = (c.position - head.position).sqrMagnitude;
                if (n.Contains("top")) d += 100f;
                if (d > bestD) { bestD = d; best = c; }
            }
            return best;
        }

        private void OnDestroy()
        {
            _driverHandler?.Dispose();
            _visualHandler?.Dispose();
        }

        private bool IsFirstPerson =>
            _soldier != null && _soldier.IsLocalPlayer && _soldier.ViewCamera != null && _soldier.ViewCamera.isActiveAndEnabled;

        /// <summary>Forces an immediate pose synchronization (used during startup preload so frame 0 renders cleanly).</summary>
        /// <summary>Reload bridge of this soldier (QA reads its angles).</summary>
        public ReloadMotionBridge Bridge => _bridge;

        /// <summary>D56: forget per-life smoothing at a respawn: the inertial hand offsets and the previous-frame hand
        /// targets (at the death spot) and any held reload pose, so the new life does not blend in from the old one.</summary>
        public void ResetTransientState()
        {
            _hasPrevHands = false;
            _lInertialOffset = _rInertialOffset = Vector3.zero;
            _hasLeftPlant = _hasRightPlant = false;
            _bridge?.ResetHold();
        }

        public void SyncPoseNow()
        {
            if (_driverHandler == null || !enabled) return;
            LateUpdate();
        }

        private void LateUpdate()
        {
            if (_driverHandler == null) return;
            if (_health != null && !_health.IsAlive) return; // ragdoll owns the visual skeleton now

            // The visual root must stay at identity under the soldier: the pose below is expressed in that space.
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;

            _driverHandler.GetHumanPose(ref _pose);
            ToVisualSpace(ref _pose);
            RelaxGlovedFingerMuscles(ref _pose);
            _visualHandler.SetHumanPose(ref _pose);

            if (!_offsetsReady && _dL != null && _dR != null && _lHand != null && _rHand != null)
            {
                // muscle space aligns the hands anatomically: capture the constant bone-axis difference once
                _lOffset = Quaternion.Inverse(_dL.rotation) * _lHand.rotation;
                _rOffset = Quaternion.Inverse(_dR.rotation) * _rHand.rotation;
                _offsetsReady = true;
            }

            AlignBody();

            // B125: hit flinch on the VISIBLE chest and head, before the arms are solved onto the gun (it used to bend the hidden
            // driver after this copy, which swung the gun off the hands and never showed on the body)
            if (_hitReactions != null && !FirstPersonActive)
            {
                if (_vChest != null) _vChest.rotation = _hitReactions.SpineFlinch * _vChest.rotation;
                if (_vHead != null) _vHead.rotation = _hitReactions.HeadFlinch * _vHead.rotation;
            }

            if (handIK && handIKWeight > 0f && _dL != null && _dR != null)
            {
                Vector3 lPos = _dL.position, rPos = _dR.position;
                Quaternion lRot = _dL.rotation, rRot = _dR.rotation;
                Transform itemTf = _soldier != null && _soldier.ActiveItem != null ? _soldier.ActiveItem.transform : null;
                _bridge?.Apply(_soldier != null ? _soldier.ActiveFirearm : null, itemTf, _dL, _dR,
                               ref lPos, ref lRot, ref rPos, ref rRot);
                ApplyProneWeaponPose(itemTf, ref lPos, ref lRot, ref rPos, ref rRot);
                ApplyGloveClearance(itemTf, ref lPos, ref lRot, ref rPos);
                ApplyGripOffsets(itemTf, ref lPos, ref rPos);
                ApplyFlipGuard(itemTf, ref lPos, ref lRot, ref rPos, ref rRot);

                // B59b / B60d: Prevent collision pullback or IK deltas from ever pulling the weapon backwards through the eye/chest plane.
                if (itemTf != null && _soldier != null)
                {
                    Vector3 eyeRef = FirstPersonActive && _soldier.ViewCamera != null
                        ? _soldier.ViewCamera.transform.position
                        : (_vHead != null ? _vHead.position : transform.position + Vector3.up * 1.52f);
                    Vector3 aimFwd = FirstPersonActive && _soldier.ViewCamera != null
                        ? _soldier.ViewCamera.transform.forward
                        : (_soldier.AimDirection.sqrMagnitude > 1e-4f ? _soldier.AimDirection : transform.forward);
                    float fwdDist = Vector3.Dot(itemTf.position - eyeRef, aimFwd);
                    LastEyePlaneDist = fwdDist;
                    LastEyePlanePush = Mathf.Max(0f, 0.08f - fwdDist);
                    if (fwdDist < 0.08f)
                    {
                        Vector3 push = aimFwd * (0.08f - fwdDist);
                        itemTf.position += push;
                        lPos += push;
                        rPos += push;
                    }
                }

                // B60a: HyperMotion-style C2 inertialization offset decay on reload/sprint/ADS/weapon state transitions.
                bool isReloading = _soldier != null && _soldier.IsReloading;
                bool isSprinting = _soldier != null && _soldier.IsSprinting;
                bool isAiming = _soldier != null && _soldier.IsAiming;
                if (_hasPrevHands && (isReloading != _prevReloading || isSprinting != _prevSprinting || isAiming != _prevAiming || itemTf != _prevItemTf))
                {
                    _lInertialOffset = Vector3.ClampMagnitude(_lInertialOffset + (_prevLPos - lPos), 0.14f);
                    _rInertialOffset = Vector3.ClampMagnitude(_rInertialOffset + (_prevRPos - rPos), 0.10f);
                }
                if (_lInertialOffset.sqrMagnitude > 1e-8f || _rInertialOffset.sqrMagnitude > 1e-8f)
                {
                    float decay = Mathf.Exp(-Time.deltaTime / InertialHalfLifeSeconds);
                    _lInertialOffset *= decay;
                    _rInertialOffset *= decay;
                    lPos += _lInertialOffset;
                    rPos += _rInertialOffset;
                }
                _hasPrevHands = true;
                _prevReloading = isReloading;
                _prevSprinting = isSprinting;
                _prevAiming = isAiming;
                _prevItemTf = itemTf;
                _prevLPos = lPos;
                _prevRPos = rPos;

                Solve(_rUpper, _rLower, _rHand, rPos, rRot * _rOffset, handIKWeight);
                Solve(_lUpper, _lLower, _lHand, lPos, lRot * _lOffset, handIKWeight);
                HandErrorRight = _rHand != null ? Vector3.Distance(_rHand.position, rPos) : -1f;
                HandErrorLeft = _lHand != null ? Vector3.Distance(_lHand.position, lPos) : -1f;
            }

            if (logAlignment && ++_frames == 90)
                Debug.Log($"[LR2] RetargetProxy {name}: rawHipsDelta={RawHipsDelta:0.000} rawHeadDelta={RawHeadDelta} ({RawHeadDelta.magnitude:0.000}) " +
                          $"shift={AppliedShift} firstPerson={FirstPersonActive} handErr L={HandErrorLeft:0.000} R={HandErrorRight:0.000} " +
                          $"humanScale driver={driver.humanScale:0.0000} visual={visual.humanScale:0.0000}");
        }

        /// <summary>
        /// HumanPose.bodyPosition is NORMALISED (divided by Animator.humanScale) and relative to the handler root's parent.
        /// Re-express the driver's body in the soldier's space, still normalised by the driver's scale: SetHumanPose then
        /// multiplies by the visual's own scale, so a differently sized body keeps its feet on the ground.
        /// (Treating it as plain world units put the body off by ~1.2% of the soldier's distance from the map origin.)
        /// </summary>
        private void ToVisualSpace(ref HumanPose pose)
        {
            float ds = driver.humanScale > 1e-4f ? driver.humanScale : 1f;
            Vector3 bodyWorld = pose.bodyPosition * ds;
            Quaternion rotWorld = pose.bodyRotation;
            var dParent = driver.transform.parent;
            if (dParent != null)
            {
                bodyWorld = dParent.TransformPoint(bodyWorld);
                rotWorld = dParent.rotation * rotWorld;
            }
            var vParent = visual.transform.parent;
            if (vParent != null)
            {
                bodyWorld = vParent.InverseTransformPoint(bodyWorld);
                rotWorld = Quaternion.Inverse(vParent.rotation) * rotWorld;
            }
            pose.bodyPosition = bodyWorld / ds;
            pose.bodyRotation = rotWorld;
        }

        /// <summary>B125 telemetry: the gun pivot's distance in front of the eye plane before the B59b push (m), and the push applied (m).</summary>
        public float LastEyePlaneDist { get; private set; }
        public float LastEyePlanePush { get; private set; }
        /// <summary>B125 flip guard: larger of the muzzle-to-aim angle and the gun roll this frame, before the clamp (deg).</summary>
        public float FlipGuardAngle { get; private set; }
        /// <summary>B125: frames on which the guard turned the gun back since this soldier spawned.</summary>
        public int FlipGuardFrames { get; private set; }
        /// <summary>
        /// B125: off by default since 11-20. The flips had two root causes, both fixed (shared IK-motion curves evaluated on
        /// parallel animation threads: SoldierController.OwnMotion; HitReactions bending the hidden driver: now the visible body),
        /// and the A/B benches without this guard topped out at 64-67 deg (sprint carry) with zero one-frame spikes. A clamp
        /// would only hide a future regression, so it stays opt-in for diagnosis: -lr2FlipGuard.
        /// </summary>
        public static readonly bool FlipGuardEnabled =
            System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-lr2FlipGuard") >= 0;
        /// <summary>
        /// B125 (user 11-19: "the weapon keeps on going up then rotating fully ... happens to all the weapons"). Kinemation's
        /// "IK WeaponBone" (a child of the driver head that carries the gun) turned 60-180 deg for single frames in AI vs AI while
        /// the driver's animated WeaponBone stayed on the soldier's facing (flip bench 11-19: 0.6-7 % of bot samples, rates up to
        /// 13,000 deg/s; finger edits, wall tuck and the hand weapon bone ruled out by A/B, Kinemation's IK motion layer group
        /// implicated by ablation). The guard needs no learned pose: the muzzle must stay within a cone around the aim and the
        /// gun's up axis within a roll limit around the barrel; past either, gun and both hand targets turn back to the limit as
        /// one rigid piece. Normal motions (wall tuck 26 deg, ADS, equip, sprint carry, reload tilt) stay inside.
        /// </summary>
        private void ApplyFlipGuard(Transform item, ref Vector3 lPos, ref Quaternion lRot, ref Vector3 rPos, ref Quaternion rRot)
        {
            FlipGuardAngle = 0f;
            if (!FlipGuardEnabled || item == null || _soldier == null) return;
            Vector3 aimFwd = FirstPersonActive && _soldier.ViewCamera != null ? _soldier.ViewCamera.transform.forward : _soldier.AimDirection;
            if (aimFwd.sqrMagnitude < 1e-4f) return;
            aimFwd.Normalize();
            var fw = _soldier.ActiveFirearm;
            Transform muzzleTf = fw != null && fw.Muzzle != null && fw.Muzzle != fw.transform ? fw.Muzzle : item;
            bool special = _soldier.IsSprinting || _soldier.IsReloading || _soldier.IsSwitchingWeapon || _soldier.ProneWeightEffective > 0.05f;
            float coneLimit = special ? 70f : 45f, rollLimit = special ? 75f : 50f;

            Vector3 muzzle = muzzleTf.forward;
            float cone = Vector3.Angle(muzzle, aimFwd);
            Quaternion delta = Quaternion.identity;
            if (cone > coneLimit)
            {
                Vector3 clamped = Vector3.RotateTowards(aimFwd, muzzle, coneLimit * Mathf.Deg2Rad, 0f);
                delta = Quaternion.FromToRotation(muzzle, clamped);
                muzzle = clamped;
            }
            // roll: the gun's up (after the swing) against world up, both flattened onto the plane across the barrel
            Vector3 up = delta * item.up;
            Vector3 upFlat = Vector3.ProjectOnPlane(up, muzzle), worldFlat = Vector3.ProjectOnPlane(Vector3.up, muzzle);
            float roll = upFlat.sqrMagnitude > 1e-4f && worldFlat.sqrMagnitude > 1e-4f ? Vector3.SignedAngle(worldFlat, upFlat, muzzle) : 0f;
            if (Mathf.Abs(roll) > rollLimit)
                delta = Quaternion.AngleAxis(Mathf.Sign(roll) * rollLimit - roll, muzzle) * delta;
            FlipGuardAngle = Mathf.Max(cone, Mathf.Abs(roll));
            if (delta == Quaternion.identity) return;

            Vector3 pivot = item.position;
            item.rotation = delta * item.rotation;
            lPos = pivot + delta * (lPos - pivot);
            rPos = pivot + delta * (rPos - pivot);
            lRot = delta * lRot;
            rRot = delta * rRot;
            FlipGuardFrames++;
        }

        /// <summary>Kinemation's prone pose arches the rifle high and across the camera; rotate/tuck the gun+hands rigidly together.</summary>
        private void ApplyProneWeaponPose(Transform item, ref Vector3 lPos, ref Quaternion lRot, ref Vector3 rPos, ref Quaternion rRot)
        {
            // D57: the gated weight; the raw curve stays at its last value when a respawn leaves the prone clips (C18: the
            // full tuck, 30 deg and 11-14 cm, stayed on the standing gun for the whole next life)
            float w = _soldier != null ? _soldier.ProneWeightEffective : 0f;
            if (w <= 0.001f || _soldier == null || item == null) return;

            Vector3 aimFwd = _soldier.ViewCamera != null && _soldier.ViewCamera.isActiveAndEnabled
                ? _soldier.ViewCamera.transform.forward
                : Quaternion.AngleAxis(_soldier.Pitch, _soldier.transform.right) * _soldier.transform.forward;
            Vector3 aimRight = Vector3.Cross(Vector3.up, aimFwd);
            aimRight = aimRight.sqrMagnitude > 1e-4f ? aimRight.normalized : _soldier.transform.right;
            Vector3 aimUp = Vector3.Cross(aimFwd, aimRight).normalized;

            Quaternion rotDelta = Quaternion.AngleAxis(30f * w, aimRight);
            Vector3 pivot = item.position;
            Vector3 shift = (aimRight * -0.11f + aimUp * -0.14f + aimFwd * 0.11f) * w;
            Vector3 Xform(Vector3 p) => pivot + rotDelta * (p - pivot) + shift;

            item.SetPositionAndRotation(pivot + shift, rotDelta * item.rotation);
            lPos = Xform(lPos);
            rPos = Xform(rPos);
            lRot = rotDelta * lRot;
            rRot = rotDelta * rRot;
        }

        private bool IsHoldingPistol =>
            _soldier != null && _soldier.ActiveFirearm != null && _soldier.ActiveFirearm.Definition != null &&
            _soldier.ActiveFirearm.Definition.weaponClass == LR2.Weapons.WeaponClass.Pistol;

        /// <summary>Keep SWAT gloved fingers &amp; thumbs curled snugly into the grip/trigger (negative HumanTrait muscle = curled; positive = extended).</summary>
        /// <summary>
        /// B26 finger mode. 1 (default) = keep the AUTHORED Kinemation finger shapes and add a modest extra curl, with soft thumb limits.
        /// 0 = legacy hard clamps (Gemini, 11-17 20:44): every joint forced into one band and the thumbs pulled from their authored
        /// wrap (about -2.5) up to about -0.3, which flattened the grip. Static so the pose check can capture both.
        /// </summary>
        public static int FingerMode = 1;
        /// <summary>Optional left-thumb ranges (base joint, outer joints) for rifles; null = the C-clamp default. The pose check uses
        /// it to compare a tucked thumb against the default, which stood up beside the handguard in first person (run #5).</summary>
        public static Vector2? LeftThumbBaseOverride, LeftThumbTipOverride;
        [Header("Grip fingers (FingerMode 1)")]
        [Tooltip("Extra curl added to the right-hand grip fingers (middle, ring, little). Negative muscle = curled.")]
        [Range(-0.6f, 0f)] public float rightGripCurl = -0.18f;
        [Tooltip("Extra curl for the trigger finger (kept small so it rests along the frame).")]
        [Range(-0.6f, 0f)] public float triggerCurl = -0.05f;
        [Tooltip("Extra curl for the left hand on the handguard / support grip.")]
        [Range(-0.6f, 0f)] public float leftGripCurl = -0.12f;
        [Tooltip("Soft limits for the thumbs' first joint: allows a real wrap without the extreme authored rotation.")]
        public Vector2 thumbBaseRange = new Vector2(-1.6f, 0.3f);
        [Tooltip("Soft limits for the thumbs' outer joints.")]
        public Vector2 thumbTipRange = new Vector2(-1.0f, 0.6f);

        private void SnugAuthoredFingers(ref HumanPose pose)
        {
            if (pose.muscles == null || _soldier == null || _soldier.ActiveItem == null) return;
            float reloadHand = _bridge != null ? Mathf.Clamp01(_bridge.HandWeight) : 0f;
            float[] mus = pose.muscles; // array is a reference: edits land in the pose (ref params can't be captured)
            void Add(List<int> list, float bias)
            {
                for (int i = 0; i < list.Count; i++) { int idx = list[i]; if (idx < mus.Length) mus[idx] = Mathf.Clamp(mus[idx] + bias, -3f, 1f); }
            }
            void Soft(List<int> list, Vector2 range)
            {
                for (int i = 0; i < list.Count; i++) { int idx = list[i]; if (idx < mus.Length) mus[idx] = Mathf.Clamp(mus[idx], range.x, range.y); }
            }
            // Right (trigger) hand (B26/D63): wrap middle/ring/little snugly around the pistol grip, rest the index finger
            // naturally along the right frame/trigger guard (no upward hyperextension), and seat the right thumb around the backstrap.
            Add(_rFingerIdx, rightGripCurl);
            Soft(_rFingerIdx, new Vector2(-0.98f, -0.62f));
            Soft(_rIndexProxIdx, new Vector2(-0.22f, -0.02f));
            Soft(_rIndexDistIdx, new Vector2(-0.34f, -0.08f));
            Soft(_rIndexSpreadIdx, new Vector2(-0.32f, -0.08f));
            Soft(_rThumbProxIdx, new Vector2(-1.15f, -0.45f));
            Soft(_rThumbDistIdx, new Vector2(-0.68f, -0.22f));
            Soft(_rThumbSpreadIdx, new Vector2(-0.78f, -0.32f));
            // Left (support) hand: wrap fingers snugly under the rail and tuck the left thumb close against the handguard.
            bool pistol = IsHoldingPistol;
            float lProxMax = pistol ? -0.48f : Mathf.Lerp(-0.56f, -0.14f, reloadHand);
            float lDistMax = pistol ? -0.58f : Mathf.Lerp(-0.62f, -0.18f, reloadHand);
            Add(_lProxIdx, leftGripCurl * (1f - reloadHand));
            Add(_lFingerIdx, leftGripCurl * (1f - reloadHand));
            Soft(_lProxIdx, new Vector2(-0.96f, lProxMax));
            Soft(_lFingerIdx, new Vector2(-0.98f, lDistMax));
            Vector2 rifleThumbBase = new Vector2(-1.28f, Mathf.Lerp(-0.88f, 0.14f, reloadHand));
            Vector2 rifleThumbTip = new Vector2(-0.65f, Mathf.Lerp(-0.20f, 0.08f, reloadHand));
            Soft(_lThumbProxIdx, !pistol && LeftThumbBaseOverride.HasValue ? LeftThumbBaseOverride.Value : (pistol ? new Vector2(-0.65f, -0.18f) : rifleThumbBase));
            Soft(_lThumbDistIdx, !pistol && LeftThumbTipOverride.HasValue ? LeftThumbTipOverride.Value : (pistol ? new Vector2(-0.55f, -0.18f) : rifleThumbTip));
            Soft(_lThumbSpreadIdx, new Vector2(-0.85f, Mathf.Lerp(-0.45f, -0.10f, reloadHand)));
            Soft(_spreadIdx, new Vector2(-0.30f, IsHoldingPistol ? 0.04f : 0.08f));
        }

        private void RelaxGlovedFingerMuscles(ref HumanPose pose)
        {
            if (FingerMode == 1) { SnugAuthoredFingers(ref pose); return; }
            if (pose.muscles == null || _soldier == null || _soldier.ActiveItem == null) return;
            float reloadHand = _bridge != null ? Mathf.Clamp01(_bridge.HandWeight) : 0f;
            bool pistol = IsHoldingPistol;

            // Left fingers: snug C-clamp around rifle handguard or wrapped over right grip on pistol; softly open on reload.
            float lProxMax = pistol ? -0.48f : Mathf.Lerp(-0.34f, -0.14f, reloadHand);
            float lDistMax = pistol ? -0.58f : Mathf.Lerp(-0.42f, -0.18f, reloadHand);
            for (int i = 0; i < _lProxIdx.Count; i++)
            {
                int idx = _lProxIdx[i];
                if (idx < pose.muscles.Length) pose.muscles[idx] = Mathf.Clamp(pose.muscles[idx], -0.92f, lProxMax);
            }
            for (int i = 0; i < _lFingerIdx.Count; i++)
            {
                int idx = _lFingerIdx[i];
                if (idx < pose.muscles.Length) pose.muscles[idx] = Mathf.Clamp(pose.muscles[idx], -0.94f, lDistMax);
            }

            // Left & right thumbs: prevent upward "thumbs-up" horns while staying tucked flat against frame/handguard.
            float lThumbProxMax = pistol ? -0.05f : Mathf.Lerp(0.05f, 0.14f, reloadHand);
            float lThumbDistMax = pistol ? -0.14f : Mathf.Lerp(-0.04f, 0.08f, reloadHand);
            for (int i = 0; i < _lThumbProxIdx.Count; i++)
            {
                int idx = _lThumbProxIdx[i];
                if (idx < pose.muscles.Length) pose.muscles[idx] = Mathf.Clamp(pose.muscles[idx], -0.35f, lThumbProxMax);
            }
            for (int i = 0; i < _lThumbDistIdx.Count; i++)
            {
                int idx = _lThumbDistIdx[i];
                if (idx < pose.muscles.Length) pose.muscles[idx] = Mathf.Clamp(pose.muscles[idx], -0.45f, lThumbDistMax);
            }

            // Right hand: wrapped firmly on pistol grip; index lightly curved on trigger guard (never sticking upward).
            float rGripMax = pistol ? -0.56f : -0.52f;
            float rIndexMin = pistol ? -0.48f : -0.44f;
            float rIndexMax = pistol ? -0.22f : -0.18f;
            for (int i = 0; i < _rFingerIdx.Count; i++)
            {
                int idx = _rFingerIdx[i];
                if (idx < pose.muscles.Length) pose.muscles[idx] = Mathf.Clamp(pose.muscles[idx], -0.94f, rGripMax);
            }
            for (int i = 0; i < _rIndexIdx.Count; i++)
            {
                int idx = _rIndexIdx[i];
                if (idx < pose.muscles.Length) pose.muscles[idx] = Mathf.Clamp(pose.muscles[idx], rIndexMin, rIndexMax);
            }
            for (int i = 0; i < _rThumbProxIdx.Count; i++)
            {
                int idx = _rThumbProxIdx[i];
                if (idx < pose.muscles.Length) pose.muscles[idx] = Mathf.Clamp(pose.muscles[idx], -0.32f, pistol ? -0.04f : 0.02f);
            }
            for (int i = 0; i < _rThumbDistIdx.Count; i++)
            {
                int idx = _rThumbDistIdx[i];
                if (idx < pose.muscles.Length) pose.muscles[idx] = Mathf.Clamp(pose.muscles[idx], -0.42f, pistol ? -0.10f : -0.02f);
            }

            // Pull wide sideways finger splay cleanly inward for pistol two-handed grip & compact rifle grip
            float spreadCap = pistol ? 0.06f : 0.14f;
            for (int i = 0; i < _spreadIdx.Count; i++)
            {
                int idx = _spreadIdx[i];
                if (idx < pose.muscles.Length) pose.muscles[idx] = Mathf.Clamp(pose.muscles[idx], -0.25f, spreadCap);
            }
        }

        [Header("Grip placement on rifles (B26)")]
        [Tooltip("Moves the right (trigger) hand in the gun's frame, metres: x right, y up, z forward along the barrel. " +
                 "Slight +x and higher y keep the index finger cleanly along the right receiver wall above the trigger guard.")]
        public Vector3 rightGripOffset = new Vector3(0.006f, -0.004f, -0.075f);
        [Tooltip("Moves the left (support) hand on the handguard, same axes. Negative y lowers it so the fingers wrap under the rail instead of over it.")]
        public Vector3 leftGripOffset = new Vector3(0f, -0.036f, 0f);

        private void ApplyGripOffsets(Transform item, ref Vector3 lPos, ref Vector3 rPos)
        {
            if (item == null || IsHoldingPistol) return; // pistol hold was verified separately
            var def = _soldier != null && _soldier.ActiveFirearm != null ? _soldier.ActiveFirearm.Definition : null;
            Vector3 rOff = def != null ? def.rightGripOffset : rightGripOffset;
            Vector3 lOff = def != null ? def.leftGripOffset : leftGripOffset;
            float leftW = 1f - (_bridge != null ? Mathf.Clamp01(_bridge.HandWeight) : 0f); // left hand is free while it handles the magazine
            rPos += item.rotation * rOff;
            lPos += item.rotation * (lOff * leftW);
        }

        /// <summary>Keep wrist targets snug on the handguard/pistol grip, stepping outward cleanly during magazine removal/insertion.</summary>
        private void ApplyGloveClearance(Transform item, ref Vector3 lPos, ref Quaternion lRot, ref Vector3 rPos)
        {
            if (item == null) return;
            float reloadHand = _bridge != null ? Mathf.Clamp01(_bridge.HandWeight) : 0f;
            Vector3 wRight = item.right;
            Vector3 wUp = item.up;
            Vector3 wFwd = item.forward;

            if (IsHoldingPistol)
            {
                Vector3 pistolOff = -wRight * 0.004f - wUp * 0.003f + wFwd * 0.002f;
                Vector3 magClear = -wRight * 0.010f - wUp * 0.008f;
                lPos += Vector3.Lerp(pistolOff, magClear, reloadHand);
                lRot = Quaternion.AngleAxis(Mathf.Lerp(3.0f, 6.5f, reloadHand), wFwd) * lRot;
                rPos += wRight * 0.001f;
            }
            else
            {
                Vector3 rifleSnug = -wRight * 0.004f - wUp * 0.003f;
                Vector3 magClear = -wRight * 0.012f - wUp * 0.008f;
                lPos += Vector3.Lerp(rifleSnug, magClear, reloadHand);
                lRot = Quaternion.AngleAxis(Mathf.Lerp(2.5f, 8.0f, reloadHand), wFwd) * lRot;
                rPos += wRight * 0.001f;
            }
        }

        private void AlignBody()
        {
            if (_vHips == null) return;
            RawHipsDelta = _dHips != null ? Vector3.Distance(_dHips.position, _vHips.position) : -1f;
            RawHeadDelta = _dHead != null && _vHead != null ? _dHead.position - _vHead.position : Vector3.zero;

            FirstPersonActive = firstPersonHeadLock && IsFirstPerson && _dHead != null && _vHead != null;
            Vector3 shift;
            if (FirstPersonActive)
            {
                // eyes onto the lens (the driver's camera is its eye socket); the head joints of the two rigs sit at
                // different heights in the skull, so matching joints left the lens at the SWAT's chin
                var cam = _soldier.ViewCamera.transform;
                shift = cam.position - VisualEyePosition;
                if (firstPersonBodyBack > 0f)
                {
                    Vector3 fwd = cam.forward;
                    fwd.y = 0f;
                    if (fwd.sqrMagnitude > 1e-4f) shift -= fwd.normalized * firstPersonBodyBack;
                }
            }
            else if (alignHips && _dHips != null)
            {
                shift = _dHips.position - _vHips.position;
                shift.y = 0f; // keep the visual's own (scaled) hip height: feet stay on the ground
            }
            else shift = Vector3.zero;

            AppliedShift = shift;
            bool teleported = _hasPrevRootPos && Vector3.SqrMagnitude(transform.position - _prevRootPos) > 0.45f;
            _prevRootPos = transform.position;
            _hasPrevRootPos = true;
            if (teleported) _hasLeftPlant = _hasRightPlant = false;

            bool upright = _soldier == null || (!_soldier.IsDead && !_soldier.IsProning);
            bool hasFeet = _lFoot != null && _rFoot != null && _lThigh != null && _lShin != null && _rThigh != null && _rShin != null;
            bool plant = FirstPersonActive && replantFeet && upright && _lFoot != null && _rFoot != null;
            bool lockStance = footPlantLock && upright && hasFeet;
            if (!lockStance) _hasLeftPlant = _hasRightPlant = false;

            if (shift.sqrMagnitude < 1e-8f && !lockStance) return;

            Vector3 lf = Vector3.zero, rf = Vector3.zero;
            Quaternion lfr = Quaternion.identity, rfr = Quaternion.identity;
            if ((plant || lockStance) && _lFoot != null && _rFoot != null)
            {
                lf = _lFoot.position; lfr = _lFoot.rotation;
                rf = _rFoot.position; rfr = _rFoot.rotation;
            }
            if (shift.sqrMagnitude >= 1e-8f)
                _vHips.position += shift;

            if (lockStance)
            {
                float groundY = transform.position.y;
                float lh = lf.y - groundY;
                float rh = rf.y - groundY;
                bool lStance = lh <= 0.165f && lh <= rh + 0.015f;
                bool rStance = rh <= 0.165f && rh <= lh + 0.015f;

                Vector3 lTarget = lf + (plant ? new Vector3(shift.x, 0f, shift.z) : Vector3.zero);
                if (lStance)
                {
                    Vector2 lDelta = new Vector2(lf.x - _leftPlantPos.x, lf.z - _leftPlantPos.z);
                    if (!_hasLeftPlant || lDelta.sqrMagnitude > 0.38f * 0.38f)
                    {
                        _leftPlantPos = lf;
                        _hasLeftPlant = true;
                    }
                    lTarget = new Vector3(_leftPlantPos.x, lf.y, _leftPlantPos.z);
                    Solve(_lThigh, _lShin, _lFoot, lTarget, lfr, 1f);
                }
                else
                {
                    _hasLeftPlant = false;
                    if (plant) Solve(_lThigh, _lShin, _lFoot, lTarget, lfr, 1f);
                }

                Vector3 rTarget = rf + (plant ? new Vector3(shift.x, 0f, shift.z) : Vector3.zero);
                if (rStance)
                {
                    Vector2 rDelta = new Vector2(rf.x - _rightPlantPos.x, rf.z - _rightPlantPos.z);
                    if (!_hasRightPlant || rDelta.sqrMagnitude > 0.38f * 0.38f)
                    {
                        _rightPlantPos = rf;
                        _hasRightPlant = true;
                    }
                    rTarget = new Vector3(_rightPlantPos.x, rf.y, _rightPlantPos.z);
                    Solve(_rThigh, _rShin, _rFoot, rTarget, rfr, 1f);
                }
                else
                {
                    _hasRightPlant = false;
                    if (plant) Solve(_rThigh, _rShin, _rFoot, rTarget, rfr, 1f);
                }
            }
            else if (plant)
            {
                // feet follow the body sideways but keep the height the retargeted pose gave them (planted)
                Vector3 horiz = new Vector3(shift.x, 0f, shift.z);
                Solve(_lThigh, _lShin, _lFoot, lf + horiz, lfr, 1f);
                Solve(_rThigh, _rShin, _rFoot, rf + horiz, rfr, 1f);
            }
        }

        /// <summary>First-person body pull-back behind the lens (m); the killcam replays the same alignment.</summary>
        public float FirstPersonBodyBack => firstPersonBodyBack;

        /// <summary>Analytic two-bone IK (same maths as Animation Rigging's TwoBoneIK, keeps the current bend plane).</summary>
        internal static void Solve(Transform a, Transform b, Transform c, Vector3 target, Quaternion targetRot, float weight)
        {
            if (a == null || b == null || c == null) return;
            Vector3 aPos = a.position, bPos = b.position, cPos = c.position;
            Vector3 tPos = Vector3.Lerp(cPos, target, weight);
            Vector3 ab = bPos - aPos, bc = cPos - bPos, ac = cPos - aPos, at = tPos - aPos;
            float abLen = ab.magnitude, bcLen = bc.magnitude, acLen = ac.magnitude, atLen = at.magnitude;
            if (abLen < 1e-4f || bcLen < 1e-4f) return;

            float oldAngle = TriangleAngle(acLen, abLen, bcLen);
            float newAngle = TriangleAngle(Mathf.Min(atLen, abLen + bcLen - 1e-4f), abLen, bcLen);
            Vector3 axis = Vector3.Cross(ab, bc);
            if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(ab, Vector3.up);
            if (axis.sqrMagnitude < 1e-8f) axis = Vector3.right;
            axis.Normalize();
            float half = 0.5f * (oldAngle - newAngle);
            float s = Mathf.Sin(half);
            b.rotation = new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(half)) * b.rotation;

            cPos = c.position;
            ac = cPos - aPos;
            if (ac.sqrMagnitude > 1e-8f && at.sqrMagnitude > 1e-8f)
                a.rotation = Quaternion.FromToRotation(ac, at) * a.rotation;
            c.rotation = Quaternion.Slerp(c.rotation, targetRot, weight);
        }

        private static float TriangleAngle(float opposite, float side1, float side2)
        {
            float cos = Mathf.Clamp((side1 * side1 + side2 * side2 - opposite * opposite) / (2f * side1 * side2), -1f, 1f);
            return Mathf.Acos(cos);
        }
    }
}
