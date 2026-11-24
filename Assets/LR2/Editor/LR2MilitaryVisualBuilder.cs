using System.Collections.Generic;
using System.IO;
using System.Linq;
using LR2.Characters;
using LR2.Core;
using LR2.Weapons;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LR2.EditorTools
{
    /// <summary>
    /// B137: Attaches a realistic Humanoid tactical soldier visual mesh (<see cref="MilitaryVisualFbxPath"/>) onto
    /// Kinemation's untouched <c>FPSGenericPlayer</c> driver via <see cref="GenericBoneMirror"/>, preserving 100% of
    /// Kinemation's generic weapon IK, finger poses, recoil, reloads, ADS, knife, torch, and grenade animations.
    /// Flip <see cref="UseMilitaryVisualMirror"/> to <c>false</c> and rebuild to revert to the robot mesh in 1 step.
    /// </summary>
    public static class LR2MilitaryVisualBuilder
    {
        public static bool UseMilitaryVisualMirror = true;
        public const string MilitaryVisualFbxPath = "Assets/KinemationDemo/Meshes/Character/MixamoSwat/Swat.fbx";
        private const string TPoseClipPath = "Assets/KinemationDemo/Animations/Locomotion/Generic/C_TPose_Generic.fbx";
        private const string CharMatDir = "Assets/LR2/Materials/Characters";
        private const string VisualRootName = "MilitaryVisual";

        private static readonly HumanBodyBones[] HierarchyOrder =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.Chest,
            HumanBodyBones.UpperChest,
            HumanBodyBones.Neck,
            HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.LeftHand,
            HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
            HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
            HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
            HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
            HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,
            HumanBodyBones.RightShoulder,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.RightHand,
            HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
            HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
            HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
            HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
            HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot,
            HumanBodyBones.RightToes
        };

        private static bool IsFingerBone(HumanBodyBones b) =>
            b >= HumanBodyBones.LeftThumbProximal && b <= HumanBodyBones.RightLittleDistal;

        [MenuItem("LR2/Characters/Rebuild Military Soldier Mirror", priority = 55)]
        public static void ApplyToSoldiersMenu() => ApplyToSoldiers();

        public static void ApplyToSoldiers()
        {
            string[] prefabPaths =
            {
                "Assets/LR2/Prefabs/Characters/LR2_Soldier_Player.prefab",
                "Assets/LR2/Prefabs/Characters/LR2_Soldier_Bot.prefab"
            };

            var tPoseClip = AssetDatabase.LoadAllAssetsAtPath(TPoseClipPath).OfType<AnimationClip>().FirstOrDefault(c => c != null && !c.name.StartsWith("__preview__"));
            var visualFbx = AssetDatabase.LoadAssetAtPath<GameObject>(MilitaryVisualFbxPath);
            if (UseMilitaryVisualMirror && visualFbx == null)
            {
                Debug.LogWarning($"[LR2] B137: Military visual FBX missing at {MilitaryVisualFbxPath}; keeping robot renderers.");
                return;
            }

            var alliedUniform = AssetDatabase.LoadAssetAtPath<Material>($"{CharMatDir}/M_Uniform_Allied_NavyBlue.mat");
            var enemyUniform = AssetDatabase.LoadAssetAtPath<Material>($"{CharMatDir}/M_Uniform_Enemy_DesertCrimson.mat");
            var skullMask = AssetDatabase.LoadAssetAtPath<Material>($"{CharMatDir}/M_FaceMask_GhostSkull.mat");
            var enemyMask = AssetDatabase.LoadAssetAtPath<Material>($"{CharMatDir}/M_FaceMask_EnemyMarauder.mat");

            foreach (var prefabPath in prefabPaths)
            {
                var root = PrefabUtility.LoadPrefabContents(prefabPath);
                if (root == null) continue;
                bool isPlayer = prefabPath.EndsWith("Player.prefab");

                try
                {
                    var oldVis = root.transform.Find(VisualRootName);
                    if (oldVis != null) Object.DestroyImmediate(oldVis.gameObject);
                    var oldMirror = root.GetComponent<GenericBoneMirror>();
                    if (oldMirror != null) Object.DestroyImmediate(oldMirror);

                    var ctrl = root.GetComponent<SoldierController>();
                    var drvAnim = root.GetComponent<Animator>();
                    var boneMap = root.GetComponent<LR2BoneMap>() ?? root.AddComponent<LR2BoneMap>();
                    int bakedCount = drvAnim != null ? boneMap.Bake(drvAnim) : 0;

                    if (!UseMilitaryVisualMirror)
                    {
                        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                            if (smr.GetComponentInParent<LR2Item>(true) == null) smr.enabled = true;
                        if (ctrl != null)
                        {
                            var headR = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                                .Where(r => r.GetComponentInParent<LR2Item>(true) == null && r.name.ToLowerInvariant().Contains("head"))
                                .Cast<Renderer>().ToArray();
                            var cso = new SerializedObject(ctrl);
                            var hp = cso.FindProperty("firstPersonHidden");
                            hp.arraySize = headR.Length;
                            for (int i = 0; i < headR.Length; i++) hp.GetArrayElementAtIndex(i).objectReferenceValue = headR[i];
                            cso.ApplyModifiedPropertiesWithoutUndo();
                        }
                        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                        continue;
                    }

                    // Ensure root is at origin and sample C_TPose_Generic so driver T-pose world rotations are canonical.
                    root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    if (tPoseClip != null) tPoseClip.SampleAnimation(root, 0f);

                    var visGo = (GameObject)PrefabUtility.InstantiatePrefab(visualFbx, root.transform);
                    PrefabUtility.UnpackPrefabInstance(visGo, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    visGo.name = VisualRootName;
                    visGo.transform.localPosition = Vector3.zero;
                    visGo.transform.localRotation = Quaternion.identity;
                    visGo.transform.localScale = Vector3.one;

                    var visAnim = visGo.GetComponent<Animator>();
                    Transform drvHips = LR2Bones.Resolve(drvAnim, root.transform, HumanBodyBones.Hips);
                    Transform visHips = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.Hips);
                    Transform drvHead = LR2Bones.Resolve(drvAnim, root.transform, HumanBodyBones.Head);
                    Transform visHead = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.Head);
                    Transform drvFoot = LR2Bones.Resolve(drvAnim, root.transform, HumanBodyBones.LeftFoot);
                    Transform visFoot = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.LeftFoot);

                    // Match visual character height 1:1 to driver robot height so cover, headshots, and first-person eye line align.
                    if (drvHead != null && visHead != null && drvFoot != null && visFoot != null)
                    {
                        float drvH = drvHead.position.y - drvFoot.position.y;
                        float visH = visHead.position.y - visFoot.position.y;
                        if (drvH > 0.4f && visH > 0.4f)
                        {
                            float scale = Mathf.Clamp(drvH / visH, 0.75f, 1.35f);
                            visGo.transform.localScale = Vector3.one * scale;
                        }
                    }

                    Transform dRUp = LR2Bones.Resolve(drvAnim, root.transform, HumanBodyBones.RightUpperArm);
                    Transform dRHand = LR2Bones.Resolve(drvAnim, root.transform, HumanBodyBones.RightHand);
                    Transform vRUp = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.RightUpperArm);
                    Transform vRHand = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.RightHand);
                    float dArmAngle = (dRUp != null && dRHand != null) ? Vector3.Angle(dRHand.position - dRUp.position, Vector3.right) : -1f;
                    float vArmAngle = (vRUp != null && vRHand != null) ? Vector3.Angle(vRHand.position - vRUp.position, Vector3.right) : -1f;

                    var drvBones = new List<Transform>(HierarchyOrder.Length);
                    var visBones = new List<Transform>(HierarchyOrder.Length);
                    var mappedIds = new List<HumanBodyBones>(HierarchyOrder.Length);
                    var offsets = new List<Quaternion>(HierarchyOrder.Length);
                    var fingerIndices = new List<int>(30);

                    foreach (var hb in HierarchyOrder)
                    {
                        var db = LR2Bones.Resolve(drvAnim, root.transform, hb);
                        var vb = LR2Bones.Resolve(visAnim, visGo.transform, hb);
                        if (db == null || vb == null) continue;
                        int idx = drvBones.Count;
                        drvBones.Add(db);
                        visBones.Add(vb);
                        mappedIds.Add(hb);
                        offsets.Add(Quaternion.Inverse(db.rotation) * vb.rotation);
                        if (IsFingerBone(hb)) fingerIndices.Add(idx);
                    }

                    Transform dLHand = LR2Bones.Resolve(drvAnim, root.transform, HumanBodyBones.LeftHand);
                    Transform vLUpper = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.LeftUpperArm);
                    Transform vLLower = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.LeftLowerArm);
                    Transform vLHand = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.LeftHand);
                    Transform vRUpper = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.RightUpperArm);
                    Transform vRLower = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.RightLowerArm);
                    Quaternion lHandOff = (dLHand != null && vLHand != null) ? Quaternion.Inverse(dLHand.rotation) * vLHand.rotation : Quaternion.identity;
                    Quaternion rHandOff = (dRHand != null && vRHand != null) ? Quaternion.Inverse(dRHand.rotation) * vRHand.rotation : Quaternion.identity;

                    Transform dLFoot = LR2Bones.Resolve(drvAnim, root.transform, HumanBodyBones.LeftFoot);
                    Transform dRFoot = LR2Bones.Resolve(drvAnim, root.transform, HumanBodyBones.RightFoot);
                    Transform vLThigh = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.LeftUpperLeg);
                    Transform vLShin = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.LeftLowerLeg);
                    Transform vLFoot = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.LeftFoot);
                    Transform vRThigh = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.RightUpperLeg);
                    Transform vRShin = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.RightLowerLeg);
                    Transform vRFoot = LR2Bones.Resolve(visAnim, visGo.transform, HumanBodyBones.RightFoot);
                    Quaternion lFootOff = (dLFoot != null && vLFoot != null) ? Quaternion.Inverse(dLFoot.rotation) * vLFoot.rotation : Quaternion.identity;
                    Quaternion rFootOff = (dRFoot != null && vRFoot != null) ? Quaternion.Inverse(dRFoot.rotation) * vRFoot.rotation : Quaternion.identity;

                    // Strip Animator, Colliders, Rigidbodies from the pure visual puppet.
                    foreach (var c in visGo.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
                    foreach (var rb in visGo.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(rb);
                    foreach (var an in visGo.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(an);

                    var mirror = root.AddComponent<GenericBoneMirror>();
                    mirror.Configure(
                        visGo, drvHips, visHips,
                        drvBones.ToArray(), visBones.ToArray(), mappedIds.ToArray(), offsets.ToArray(), fingerIndices.ToArray(),
                        dLHand, dRHand, vLUpper, vLLower, vLHand, vRUpper, vRLower, vRHand, lHandOff, rHandOff,
                        dLFoot, dRFoot, vLThigh, vLShin, vLFoot, vRThigh, vRShin, vRFoot, lFootOff, rFootOff);

                    // Hide ONLY the 4 robot body/arm/leg/head renderers; leave weapons/scopes/attachments untouched.
                    foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (r.transform.IsChildOf(visGo.transform)) continue;
                        if (r.GetComponentInParent<LR2Item>(true) != null) continue;
                        r.enabled = false;
                    }

                    var headRenderers = new List<Renderer>();
                    foreach (var r in visGo.GetComponentsInChildren<Renderer>(true))
                    {
                        r.gameObject.layer = LR2Layers.Character;
                        r.shadowCastingMode = ShadowCastingMode.On;
                        if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
                        if (r.name.ToLowerInvariant().Contains("head")) headRenderers.Add(r);
                    }
                    foreach (Transform t in visGo.GetComponentsInChildren<Transform>(true))
                        t.gameObject.layer = LR2Layers.Character;

                    if (ctrl != null && headRenderers.Count > 0)
                    {
                        var cso = new SerializedObject(ctrl);
                        var hp = cso.FindProperty("firstPersonHidden");
                        hp.arraySize = headRenderers.Count;
                        for (int i = 0; i < headRenderers.Count; i++) hp.GetArrayElementAtIndex(i).objectReferenceValue = headRenderers[i];
                        cso.ApplyModifiedPropertiesWithoutUndo();
                    }

                    var sv = root.GetComponent<SoldierVisuals>();
                    if (sv != null)
                    {
                        sv.SetTeamSwaps(System.Array.Empty<SoldierVisuals.TeamMaterialSwap>());
                        sv.SetGear(alliedUniform, enemyUniform, skullMask, enemyMask);
                        sv.ApplyMaterialsNow(isPlayer);
                        EditorUtility.SetDirty(sv);
                    }

                    // Restore Animator binding clean before saving.
                    if (drvAnim != null) drvAnim.Rebind();
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    Debug.Log($"[LR2] B137 MilitaryVisual on {Path.GetFileNameWithoutExtension(prefabPath)}: " +
                              $"bones={drvBones.Count} fingers={fingerIndices.Count} bakedMap={bakedCount} " +
                              $"tposeDeg(drv={dArmAngle:0.1},vis={vArmAngle:0.1}) headHidden={headRenderers.Count}");
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
            AssetDatabase.SaveAssets();
        }
    }
}
