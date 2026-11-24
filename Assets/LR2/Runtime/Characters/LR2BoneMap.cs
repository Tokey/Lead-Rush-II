using System.Collections.Generic;
using UnityEngine;

namespace LR2.Characters
{
    /// <summary>
    /// B136: humanoid bone lookup that also works on GENERIC rigs. Kinemation's original demo soldier (the Unity robot,
    /// FPSGenericPlayer) has a generic avatar, so <see cref="Animator.GetBoneTransform"/> returns null for every bone; LR2's
    /// hitboxes, hit reactions, killcam, footsteps and aim points all asked for humanoid bones. The builder bakes the bones
    /// into this component; <see cref="LR2Bones.Get(Animator, HumanBodyBones)"/> reads it, then falls back to the humanoid
    /// avatar and finally to a name search (Unity robot "Left_UpperArm" naming, then Mixamo "mixamorig:LeftArm").
    /// </summary>
    [DisallowMultipleComponent]
    public class LR2BoneMap : MonoBehaviour
    {
        [SerializeField] private Transform[] bones = new Transform[(int)HumanBodyBones.LastBone];

        public Transform Get(HumanBodyBones bone)
        {
            int i = (int)bone;
            return bones != null && i >= 0 && i < bones.Length ? bones[i] : null;
        }

        /// <summary>Resolves every humanoid bone under this object (builder time, or lazily at runtime).</summary>
        public int Bake(Animator animator)
        {
            if (bones == null || bones.Length != (int)HumanBodyBones.LastBone) bones = new Transform[(int)HumanBodyBones.LastBone];
            int found = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i] = LR2Bones.Resolve(animator, transform, (HumanBodyBones)i);
                if (bones[i] != null) found++;
            }
            return found;
        }
    }

    public static class LR2Bones
    {
        private static readonly Dictionary<Animator, LR2BoneMap> MapCache = new Dictionary<Animator, LR2BoneMap>();

        /// <summary>The bone, or null. Works for humanoid and generic (Kinemation robot) rigs.</summary>
        public static Transform Get(Animator animator, HumanBodyBones bone)
        {
            if (animator == null || bone < 0 || bone >= HumanBodyBones.LastBone) return null;
            var map = FindMap(animator);
            var t = map != null ? map.Get(bone) : null;
            return t != null ? t : Resolve(animator, animator.transform, bone);
        }

        /// <summary>The bone of the soldier that owns <paramref name="anyPart"/> (its Animator is searched upward then downward).</summary>
        public static Transform Get(Component anyPart, HumanBodyBones bone)
        {
            if (anyPart == null) return null;
            var anim = anyPart.GetComponentInParent<Animator>();
            if (anim == null) anim = anyPart.GetComponentInChildren<Animator>();
            return Get(anim, bone);
        }

        private static LR2BoneMap FindMap(Animator animator)
        {
            if (MapCache.TryGetValue(animator, out var map) && map != null) return map;
            map = animator.GetComponent<LR2BoneMap>();
            if (map != null)
            {
                if (MapCache.Count > 256) MapCache.Clear(); // destroyed soldiers leave fake-null keys behind
                MapCache[animator] = map;
            }
            return map;
        }

        /// <summary>Humanoid avatar first, then a name search that skips renderer objects (the robot's TFP "Head" mesh).</summary>
        public static Transform Resolve(Animator animator, Transform root, HumanBodyBones bone)
        {
            if (animator != null && animator.isHuman && animator.avatar != null)
            {
                var h = animator.GetBoneTransform(bone);
                if (h != null) return h;
            }
            if (root == null) return null;
            var searchRoot = FindHips(root) ?? root;
            if (bone == HumanBodyBones.Hips && searchRoot.name.EndsWith("Hips")) return searchRoot;
            foreach (var name in CandidateNames(bone))
            {
                var t = FindDeep(searchRoot, name);
                if (t != null) return t;
            }
            return null;
        }

        private static Transform FindHips(Transform root)
        {
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if ((t.name == "Hips" || t.name.EndsWith(":Hips")) && t.GetComponent<Renderer>() == null) return t;
                for (int i = 0; i < t.childCount; i++) queue.Enqueue(t.GetChild(i));
            }
            return null;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name && root.GetComponent<Renderer>() == null) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>"LeftUpperArm" -> "Left_UpperArm" (Unity robot / Starter Assets armature), plus Mixamo names (incl. all 30 finger bones).</summary>
        public static IEnumerable<string> CandidateNames(HumanBodyBones bone)
        {
            string n = bone.ToString();
            if (n.StartsWith("Left")) yield return "Left_" + n.Substring(4);
            else if (n.StartsWith("Right")) yield return "Right_" + n.Substring(5);
            if (n.Contains("Little"))
            {
                string pinky = n.Replace("Little", "Pinky");
                if (pinky.StartsWith("Left")) yield return "Left_" + pinky.Substring(4);
                else if (pinky.StartsWith("Right")) yield return "Right_" + pinky.Substring(5);
                yield return pinky;
            }
            yield return n;
            string mixamo = Mixamo(bone);
            if (mixamo != null) yield return "mixamorig:" + mixamo;
        }

        private static string Mixamo(HumanBodyBones b)
        {
            switch (b)
            {
                case HumanBodyBones.Hips: return "Hips";
                case HumanBodyBones.Spine: return "Spine";
                case HumanBodyBones.Chest: return "Spine1";
                case HumanBodyBones.UpperChest: return "Spine2";
                case HumanBodyBones.Neck: return "Neck";
                case HumanBodyBones.Head: return "Head";
                case HumanBodyBones.LeftShoulder: return "LeftShoulder";
                case HumanBodyBones.RightShoulder: return "RightShoulder";
                case HumanBodyBones.LeftUpperArm: return "LeftArm";
                case HumanBodyBones.RightUpperArm: return "RightArm";
                case HumanBodyBones.LeftLowerArm: return "LeftForeArm";
                case HumanBodyBones.RightLowerArm: return "RightForeArm";
                case HumanBodyBones.LeftHand: return "LeftHand";
                case HumanBodyBones.RightHand: return "RightHand";
                case HumanBodyBones.LeftUpperLeg: return "LeftUpLeg";
                case HumanBodyBones.RightUpperLeg: return "RightUpLeg";
                case HumanBodyBones.LeftLowerLeg: return "LeftLeg";
                case HumanBodyBones.RightLowerLeg: return "RightLeg";
                case HumanBodyBones.LeftFoot: return "LeftFoot";
                case HumanBodyBones.RightFoot: return "RightFoot";
                case HumanBodyBones.LeftToes: return "LeftToeBase";
                case HumanBodyBones.RightToes: return "RightToeBase";
                case HumanBodyBones.LeftThumbProximal: return "LeftHandThumb1";
                case HumanBodyBones.LeftThumbIntermediate: return "LeftHandThumb2";
                case HumanBodyBones.LeftThumbDistal: return "LeftHandThumb3";
                case HumanBodyBones.LeftIndexProximal: return "LeftHandIndex1";
                case HumanBodyBones.LeftIndexIntermediate: return "LeftHandIndex2";
                case HumanBodyBones.LeftIndexDistal: return "LeftHandIndex3";
                case HumanBodyBones.LeftMiddleProximal: return "LeftHandMiddle1";
                case HumanBodyBones.LeftMiddleIntermediate: return "LeftHandMiddle2";
                case HumanBodyBones.LeftMiddleDistal: return "LeftHandMiddle3";
                case HumanBodyBones.LeftRingProximal: return "LeftHandRing1";
                case HumanBodyBones.LeftRingIntermediate: return "LeftHandRing2";
                case HumanBodyBones.LeftRingDistal: return "LeftHandRing3";
                case HumanBodyBones.LeftLittleProximal: return "LeftHandPinky1";
                case HumanBodyBones.LeftLittleIntermediate: return "LeftHandPinky2";
                case HumanBodyBones.LeftLittleDistal: return "LeftHandPinky3";
                case HumanBodyBones.RightThumbProximal: return "RightHandThumb1";
                case HumanBodyBones.RightThumbIntermediate: return "RightHandThumb2";
                case HumanBodyBones.RightThumbDistal: return "RightHandThumb3";
                case HumanBodyBones.RightIndexProximal: return "RightHandIndex1";
                case HumanBodyBones.RightIndexIntermediate: return "RightHandIndex2";
                case HumanBodyBones.RightIndexDistal: return "RightHandIndex3";
                case HumanBodyBones.RightMiddleProximal: return "RightHandMiddle1";
                case HumanBodyBones.RightMiddleIntermediate: return "RightHandMiddle2";
                case HumanBodyBones.RightMiddleDistal: return "RightHandMiddle3";
                case HumanBodyBones.RightRingProximal: return "RightHandRing1";
                case HumanBodyBones.RightRingIntermediate: return "RightHandRing2";
                case HumanBodyBones.RightRingDistal: return "RightHandRing3";
                case HumanBodyBones.RightLittleProximal: return "RightHandPinky1";
                case HumanBodyBones.RightLittleIntermediate: return "RightHandPinky2";
                case HumanBodyBones.RightLittleDistal: return "RightHandPinky3";
                default: return null;
            }
        }
    }
}
