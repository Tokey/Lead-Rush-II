using KINEMATION.ProceduralRecoilAnimationSystem.Runtime;
using KINEMATION.Shared.KAnimationCore.Runtime.Rig;
using LR2.Core;
using LR2.Weapons;
using UnityEngine;

namespace LR2.Characters
{
    /// <summary>
    /// Runs after Kinemation's FPSAnimator.LateUpdate() and BodycamRig.LateUpdate(), and BEFORE GenericBoneMirror:
    /// 1) Resets any per-frame local item offset so Kinemation's transform never accumulates drift.
    /// 2) Applies precision world-space ADS sight-line alignment on IK WeaponBone + IK hands when local player aims,
    ///    preserving live RecoilAnimation kick while eliminating aim-point parallax/drift across stances and respawns.
    /// </summary>
    [DefaultExecutionOrder(520)]
    public class WeaponAntiClip : MonoBehaviour
    {
        private SoldierController _soldier;
        private KRigComponent _rig;
        private Transform _weaponIkBone;
        private Transform _rightHandIk;
        private Transform _leftHandIk;
        private float _actionLockGate = 1f;

        public bool LastLockApplied { get; private set; }
        public float LastLockWeight { get; private set; }

        private void Awake()
        {
            _soldier = GetComponent<SoldierController>();
        }

        private void CacheRigBones()
        {
            if (_rig != null && _weaponIkBone != null) return;
            _rig = GetComponentInChildren<KRigComponent>();
            if (_rig == null) return;
            _weaponIkBone = _rig.GetRigTransform("IK WeaponBone");
            _rightHandIk = _rig.GetRigTransform("IK RightHand");
            _leftHandIk = _rig.GetRigTransform("IK LeftHand");
        }

        private void LateUpdate()
        {
            LastLockApplied = false;
            LastLockWeight = 0f;

            if (_soldier == null || _soldier.IsDead) return;
            var item = _soldier.ActiveItem;
            if (item == null || !item.gameObject.activeInHierarchy) return;

            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;

            if (!_soldier.IsLocalPlayer || _soldier.ViewCamera == null || !_soldier.ViewCamera.isActiveAndEnabled) return;
            if (!(item is LR2Firearm firearm)) return;

            float dt = Time.deltaTime;
            bool eligible = firearm.IsSightLockEligible
                && !_soldier.IsSprinting
                && !_soldier.IsSwitchingWeapon
                && !_soldier.IsThrowingGrenade
                && !_soldier.IsEditingAttachments
                && _soldier.FullBodyWeightEffective < 0.25f
                && !LR2.World.KillcamDirector.IsPlaying;
            _actionLockGate = Mathf.MoveTowards(_actionLockGate, eligible ? 1f : 0f, dt * 14f);

            float lockWeight = Mathf.SmoothStep(0f, 1f, _soldier.AimBlend) * _actionLockGate;
            if (lockWeight <= 0.001f) return;

            CacheRigBones();
            Transform weaponBone = (item.transform.parent != null && item.transform.parent != _soldier.transform)
                ? item.transform.parent
                : _weaponIkBone;
            Transform aimTf = firearm.ActiveAimPointTransform;
            if (weaponBone == null || aimTf == null || !aimTf.IsChildOf(weaponBone)) return;

            Transform camTf = _soldier.ViewCamera.transform;
            RecoilAnimation recoilAnim = firearm.LiveRecoilAnimation;
            Quaternion recRot = recoilAnim != null ? recoilAnim.OutRot : Quaternion.identity;
            Vector3 recLoc = recoilAnim != null ? recoilAnim.OutLoc : Vector3.zero;

            Quaternion targetAimRot = camTf.rotation * recRot;
            Quaternion fullDeltaRot = targetAimRot * Quaternion.Inverse(aimTf.rotation);
            Quaternion deltaRot = Quaternion.Slerp(Quaternion.identity, fullDeltaRot, lockWeight);

            bool moveRightHand = _rightHandIk != null && !_rightHandIk.IsChildOf(weaponBone);
            bool moveLeftHand = _leftHandIk != null && !_leftHandIk.IsChildOf(weaponBone);

            Vector3 pivot = weaponBone.position;
            weaponBone.rotation = deltaRot * weaponBone.rotation;
            if (moveRightHand)
            {
                _rightHandIk.position = pivot + deltaRot * (_rightHandIk.position - pivot);
                _rightHandIk.rotation = deltaRot * _rightHandIk.rotation;
            }
            if (moveLeftHand)
            {
                _leftHandIk.position = pivot + deltaRot * (_leftHandIk.position - pivot);
                _leftHandIk.rotation = deltaRot * _leftHandIk.rotation;
            }

            float curZ = Vector3.Dot(aimTf.position - camTf.position, camTf.forward);
            float targetZ = Mathf.Clamp(curZ, -0.06f, 0.25f);
            Vector3 targetAimPos = camTf.position + camTf.forward * targetZ + camTf.rotation * recLoc;
            Vector3 deltaPos = (targetAimPos - aimTf.position) * lockWeight;

            weaponBone.position += deltaPos;
            if (moveRightHand) _rightHandIk.position += deltaPos;
            if (moveLeftHand) _leftHandIk.position += deltaPos;

            LastLockApplied = true;
            LastLockWeight = lockWeight;
        }
    }
}
