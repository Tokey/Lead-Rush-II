using System.Collections.Generic;
using UnityEngine;

namespace LR2.Characters
{
    /// <summary>
    /// Shows the military soldier mesh in place of Kinemation's robot (FPSGenericPlayer). The robot stays the real,
    /// invisible character: Kinemation animates it and its IK puts the robot's hands on the gun. Every LateUpdate after
    /// that, each mapped bone of the visual skeleton takes its robot bone's world rotation through an offset measured in a
    /// shared T-pose (LR2MilitaryVisualBuilder), and the visual arms and legs are solved so wrists and ankles land on the
    /// robot's.
    ///
    /// Hands and fingers get one more step. The two skeletons do not rest their hands the same way (the Mixamo thumb sits at
    /// an angle in its bind pose), so a copied rotation can point a finger somewhere else: the left thumb stood straight up
    /// over the handguard and into the iron sight, 36-38 mm from the robot's thumb. After the copy, each visual hand and
    /// finger bone is swung to point the same way as its robot twin (joint to next joint). The robot's fingers are
    /// Kinemation's grip, so the visible fingers close on the gun the same way.
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
        [Tooltip("Size the visible hands to the robot's hands (wrist to middle knuckle). The military hands are bigger: with the " +
                 "same finger directions their thumb still rose beside the rear sight in ADS, where Kinemation's own hand stays below it.")]
        [SerializeField] private bool matchRobotHandSize = true;

        /// <summary>A visual finger bone: copied from its robot twin, then turned to point the same way (joint to next joint).</summary>
        private struct AimedBone
        {
            public Transform driver, driverNext, visual, visualNext;
            public Quaternion offset;
        }

        /// <summary>The bones that define a palm: wrist, and the index, middle and little finger knuckles.</summary>
        private struct Palm
        {
            public Transform dHand, dIndex, dMiddle, dLittle, vHand, vIndex, vMiddle, vLittle;
        }

        private static readonly string[] FingerWords = { "thumb", "index", "middle", "ring", "little", "pinky" };

        private SoldierController _soldier;
        private SoldierRagdoll _ragdoll;
        private AimedBone[] _aimedBones;
        private Palm _leftPalm, _rightPalm;

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
            int i = IndexOf(bone);
            return i >= 0 ? visualBones[i] : null;
        }

        private int IndexOf(HumanBodyBones bone)
        {
            if (boneIds == null || visualBones == null || driverBones == null) return -1;
            int n = Mathf.Min(boneIds.Length, Mathf.Min(visualBones.Length, driverBones.Length));
            for (int i = 0; i < n; i++)
                if (boneIds[i] == bone) return i;
            return -1;
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
            _aimedBones = null;
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

            // A ragdolled body only mirrors the live ragdoll bones; there is no gun to hold.
            bool dead = (_ragdoll != null && _ragdoll.IsActive) || (_soldier != null && _soldier.IsDead);
            if (dead) return;

            bool firstPerson = _soldier != null && _soldier.IsLocalPlayer &&
                               _soldier.ViewCamera != null && _soldier.ViewCamera.isActiveAndEnabled &&
                               !LR2.World.KillcamDirector.IsPlaying;
            if (firstPerson && firstPersonBodyBack > 0f)
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

            // The arm solve set the wrists from the T-pose offset, which leaves the palm rolled (thumb on top of the
            // handguard). Turn each palm to match the robot's, then set the fingers again, parents first.
            if (_aimedBones == null)
            {
                _aimedBones = BuildAimedBones(count);
                _leftPalm = MakePalm(dLHand, vLHand, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftLittleProximal);
                _rightPalm = MakePalm(dRHand, vRHand, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightLittleProximal);
                if (matchRobotHandSize)
                {
                    MatchHandSize(_leftPalm);
                    MatchHandSize(_rightPalm);
                }
            }
            AlignPalm(_leftPalm);
            AlignPalm(_rightPalm);
            foreach (var b in _aimedBones)
            {
                if (b.visual == null || b.driver == null) continue;
                b.visual.rotation = b.driver.rotation * b.offset;
                if (b.driverNext == null || b.visualNext == null) continue;
                Vector3 want = b.driverNext.position - b.driver.position;
                Vector3 have = b.visualNext.position - b.visual.position;
                if (want.sqrMagnitude < 1e-10f || have.sqrMagnitude < 1e-10f) continue;
                b.visual.rotation = Quaternion.FromToRotation(have, want) * b.visual.rotation;
            }

            // Feet go onto the robot's feet in world space (no foot sliding from the different leg lengths).
            if (dLFoot != null && vLThigh != null && vLShin != null && vLFoot != null)
                RetargetProxy.Solve(vLThigh, vLShin, vLFoot, dLFoot.position, dLFoot.rotation * lFootBindOffset, 1f);
            if (dRFoot != null && vRThigh != null && vRShin != null && vRFoot != null)
                RetargetProxy.Solve(vRThigh, vRShin, vRFoot, dRFoot.position, dRFoot.rotation * rFootBindOffset, 1f);
        }

        /// <summary>Every finger bone, ordered so parents come first.</summary>
        private AimedBone[] BuildAimedBones(int count)
        {
            var fingers = new List<KeyValuePair<int, AimedBone>>(30);
            if (fingerBoneIndices != null)
            {
                foreach (int idx in fingerBoneIndices)
                {
                    if (idx < 0 || idx >= count) continue;
                    var db = driverBones[idx];
                    var vb = visualBones[idx];
                    if (db == null || vb == null) continue;
                    fingers.Add(new KeyValuePair<int, AimedBone>(Depth(vb), new AimedBone
                    {
                        driver = db, driverNext = NextJoint(db), visual = vb, visualNext = NextJoint(vb), offset = bindOffsets[idx],
                    }));
                }
            }
            fingers.Sort((a, b) => a.Key.CompareTo(b.Key));
            var list = new AimedBone[fingers.Count];
            for (int i = 0; i < list.Length; i++) list[i] = fingers[i].Value;
            return list;
        }

        private Palm MakePalm(Transform driverHand, Transform visualHand, HumanBodyBones index, HumanBodyBones middle, HumanBodyBones little)
        {
            int i = IndexOf(index), m = IndexOf(middle), l = IndexOf(little);
            if (i < 0 || m < 0 || l < 0) return default;
            return new Palm
            {
                dHand = driverHand, dIndex = driverBones[i], dMiddle = driverBones[m], dLittle = driverBones[l],
                vHand = visualHand, vIndex = visualBones[i], vMiddle = visualBones[m], vLittle = visualBones[l],
            };
        }

        /// <summary>Scales the visual hand (and so its fingers) once, so wrist to middle knuckle is as long as the robot's.</summary>
        private static void MatchHandSize(in Palm p)
        {
            if (p.dHand == null || p.dMiddle == null || p.vHand == null || p.vMiddle == null) return;
            float robot = Vector3.Distance(p.dHand.position, p.dMiddle.position);
            float visual = Vector3.Distance(p.vHand.position, p.vMiddle.position);
            if (robot < 1e-4f || visual < 1e-4f) return;
            p.vHand.localScale *= Mathf.Clamp(robot / visual, 0.6f, 1.4f);
        }

        /// <summary>Turns the visual hand about its wrist so its palm frame (wrist to middle knuckle, knuckle line) matches the robot's.</summary>
        private static void AlignPalm(in Palm p)
        {
            if (p.dHand == null || p.dIndex == null || p.dMiddle == null || p.dLittle == null ||
                p.vHand == null || p.vIndex == null || p.vMiddle == null || p.vLittle == null) return;
            if (!PalmFrame(p.dHand.position, p.dMiddle.position, p.dIndex.position, p.dLittle.position, out var robot)) return;
            if (!PalmFrame(p.vHand.position, p.vMiddle.position, p.vIndex.position, p.vLittle.position, out var visual)) return;
            p.vHand.rotation = robot * Quaternion.Inverse(visual) * p.vHand.rotation;
        }

        private static bool PalmFrame(Vector3 wrist, Vector3 middle, Vector3 index, Vector3 little, out Quaternion frame)
        {
            Vector3 forward = middle - wrist;
            Vector3 normal = Vector3.Cross(forward, little - index);
            frame = Quaternion.identity;
            if (forward.sqrMagnitude < 1e-10f || normal.sqrMagnitude < 1e-10f) return false;
            frame = Quaternion.LookRotation(forward, normal);
            return true;
        }

        /// <summary>The next joint of a finger: the child that continues the same finger (or its tip end bone).</summary>
        private static Transform NextJoint(Transform bone)
        {
            if (bone.childCount == 0) return null;
            string name = bone.name.ToLowerInvariant();
            foreach (var word in FingerWords)
            {
                if (!name.Contains(word)) continue;
                for (int i = 0; i < bone.childCount; i++)
                    if (bone.GetChild(i).name.ToLowerInvariant().Contains(word)) return bone.GetChild(i);
            }
            return bone.GetChild(0);
        }

        private static int Depth(Transform t)
        {
            int depth = 0;
            for (var p = t.parent; p != null; p = p.parent) depth++;
            return depth;
        }
    }
}
