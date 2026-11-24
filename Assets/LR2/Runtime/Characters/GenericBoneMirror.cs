using UnityEngine;

namespace LR2.Characters
{
    /// <summary>
    /// B137: Mirrors Kinemation's live FPSGenericPlayer skeleton (arms, weapons, recoil, ADS, reload, grenade,
    /// all 30 finger phalanges, ragdoll) onto an external Humanoid military visual mesh via calibrated T-pose
    /// world-rotation offsets + exact 2-bone limb IK, preserving 100% of Kinemation's generic rig animations
    /// while rendering a realistic tactical soldier instead of the Unity default robot.
    /// </summary>
    [DefaultExecutionOrder(550)]
    public class GenericBoneMirror : MonoBehaviour
    {
        [SerializeField] private GameObject visualRoot;
        [SerializeField] private Transform driverHips;
        [SerializeField] private Transform visualHips;
        [SerializeField] private Transform[] driverBones = System.Array.Empty<Transform>();
        [SerializeField] private Transform[] visualBones = System.Array.Empty<Transform>();
        [SerializeField] private HumanBodyBones[] boneIds = System.Array.Empty<HumanBodyBones>();
        [SerializeField] private Quaternion[] bindOffsets = System.Array.Empty<Quaternion>();
        [SerializeField] private int[] fingerBoneIndices = System.Array.Empty<int>();

        [Header("Hand & Foot IK")]
        [SerializeField] private Transform dLHand;
        [SerializeField] private Transform dRHand;
        [SerializeField] private Transform vLUpper, vLLower, vLHand;
        [SerializeField] private Transform vRUpper, vRLower, vRHand;
        [SerializeField] private Quaternion lHandBindOffset = Quaternion.identity;
        [SerializeField] private Quaternion rHandBindOffset = Quaternion.identity;

        [SerializeField] private Transform dLFoot, dRFoot;
        [SerializeField] private Transform vLThigh, vLShin, vLFoot;
        [SerializeField] private Transform vRThigh, vRShin, vRFoot;
        [SerializeField] private Quaternion lFootBindOffset = Quaternion.identity;
        [SerializeField] private Quaternion rFootBindOffset = Quaternion.identity;

        [Header("First-Person Clearance")]
        [SerializeField, Range(0f, 0.20f)] private float firstPersonBodyBack = 0.045f;

        private SoldierController _soldier;
        private SoldierRagdoll _ragdoll;

        public GameObject VisualRoot => visualRoot;
        public float FirstPersonBodyBack => firstPersonBodyBack;
        public Transform VisualLeftUpperArm => vLUpper;
        public Transform VisualLeftLowerArm => vLLower;
        public Transform VisualLeftHand => vLHand;
        public Transform VisualRightUpperArm => vRUpper;
        public Transform VisualRightLowerArm => vRLower;
        public Transform VisualRightHand => vRHand;
        public Vector3 VisualEyePosition
        {
            get
            {
                var head = GetVisualBone(HumanBodyBones.Head);
                return head != null ? head.position + head.up * 0.08f : transform.position + Vector3.up * 1.55f;
            }
        }
        public int MappedBoneCount => driverBones != null ? driverBones.Length : 0;
        public int MappedFingerCount => fingerBoneIndices != null ? fingerBoneIndices.Length : 0;
        public float HandErrorLeft { get; private set; }
        public float HandErrorRight { get; private set; }

        public Transform GetVisualBone(HumanBodyBones bone)
        {
            if (boneIds == null || visualBones == null) return null;
            int n = Mathf.Min(boneIds.Length, visualBones.Length);
            for (int i = 0; i < n; i++)
                if (boneIds[i] == bone) return visualBones[i];
            return null;
        }

        public void Configure(
            GameObject visualRootObj,
            Transform drvHips, Transform visHips,
            Transform[] drvBones, Transform[] visBones, HumanBodyBones[] ids, Quaternion[] offsets, int[] fingerIdx,
            Transform dlh, Transform drh, Transform vlu, Transform vll, Transform vlh, Transform vru, Transform vrl, Transform vrh,
            Quaternion lHandOff, Quaternion rHandOff,
            Transform dlf, Transform drf, Transform vlt, Transform vls, Transform vlf, Transform vrt, Transform vrs, Transform vrf,
            Quaternion lFootOff, Quaternion rFootOff)
        {
            visualRoot = visualRootObj;
            driverHips = drvHips;
            visualHips = visHips;
            driverBones = drvBones ?? System.Array.Empty<Transform>();
            visualBones = visBones ?? System.Array.Empty<Transform>();
            boneIds = ids ?? System.Array.Empty<HumanBodyBones>();
            bindOffsets = offsets ?? System.Array.Empty<Quaternion>();
            fingerBoneIndices = fingerIdx ?? System.Array.Empty<int>();
            dLHand = dlh; dRHand = drh;
            vLUpper = vlu; vLLower = vll; vLHand = vlh;
            vRUpper = vru; vRLower = vrl; vRHand = vrh;
            lHandBindOffset = lHandOff; rHandBindOffset = rHandOff;
            dLFoot = dlf; dRFoot = drf;
            vLThigh = vlt; vLShin = vls; vLFoot = vlf;
            vRThigh = vrt; vRShin = vrs; vRFoot = vrf;
            lFootBindOffset = lFootOff; rFootBindOffset = rFootOff;
        }

        private void Awake()
        {
            _soldier = GetComponent<SoldierController>();
            _ragdoll = GetComponent<SoldierRagdoll>();
        }

        private void LateUpdate()
        {
            if (visualRoot == null || driverHips == null || visualHips == null) return;
            visualRoot.transform.localPosition = Vector3.zero;
            visualRoot.transform.localRotation = Quaternion.identity;

            visualHips.position = driverHips.position;
            int count = Mathf.Min(driverBones.Length, Mathf.Min(visualBones.Length, bindOffsets.Length));
            for (int i = 0; i < count; i++)
            {
                var db = driverBones[i];
                var vb = visualBones[i];
                if (db == null || vb == null) continue;
                vb.rotation = db.rotation * bindOffsets[i];
            }

            // While ragdolled after death, mirror the live ragdoll bones directly without forcing weapon IK.
            bool dead = (_ragdoll != null && _ragdoll.IsActive) || (_soldier != null && _soldier.IsDead);
            if (dead) return;

            bool fp = _soldier != null && _soldier.IsLocalPlayer &&
                      _soldier.ViewCamera != null && _soldier.ViewCamera.isActiveAndEnabled &&
                      !LR2.World.KillcamDirector.IsPlaying;
            if (fp && firstPersonBodyBack > 0f)
                visualHips.position -= transform.forward * firstPersonBodyBack;

            if (dRHand != null && vRUpper != null && vRLower != null && vRHand != null)
            {
                RetargetProxy.Solve(vRUpper, vRLower, vRHand, dRHand.position, dRHand.rotation * rHandBindOffset, 1f);
                HandErrorRight = Vector3.Distance(vRHand.position, dRHand.position);
            }
            if (dLHand != null && vLUpper != null && vLLower != null && vLHand != null)
            {
                RetargetProxy.Solve(vLUpper, vLLower, vLHand, dLHand.position, dLHand.rotation * lHandBindOffset, 1f);
                HandErrorLeft = Vector3.Distance(vLHand.position, dLHand.position);
            }

            // Re-apply all finger phalanges after wrist rotation override so triggers, grips, mag & grenade curls stay exact.
            if (fingerBoneIndices != null)
            {
                for (int k = 0; k < fingerBoneIndices.Length; k++)
                {
                    int idx = fingerBoneIndices[k];
                    if (idx < 0 || idx >= count) continue;
                    var db = driverBones[idx];
                    var vb = visualBones[idx];
                    if (db != null && vb != null) vb.rotation = db.rotation * bindOffsets[idx];
                }
            }

            // Match feet onto driver feet positions in world space (eliminates height-ratio foot skating).
            if (dLFoot != null && vLThigh != null && vLShin != null && vLFoot != null)
                RetargetProxy.Solve(vLThigh, vLShin, vLFoot, dLFoot.position, dLFoot.rotation * lFootBindOffset, 1f);
            if (dRFoot != null && vRThigh != null && vRShin != null && vRFoot != null)
                RetargetProxy.Solve(vRThigh, vRShin, vRFoot, dRFoot.position, dRFoot.rotation * rFootBindOffset, 1f);
        }
    }
}
