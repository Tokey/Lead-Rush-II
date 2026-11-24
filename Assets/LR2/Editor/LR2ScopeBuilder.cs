using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KINEMATION.FPSAnimationFramework.Runtime.Core;
using KINEMATION.FPSAnimationFramework.Runtime.Playables;
using KINEMATION.Shared.KAnimationCore.Runtime.Core;
using LR2.Weapons;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace LR2.EditorTools
{
    /// <summary>
    /// B136i & B136r: Post-build upgrades for Kinemation Picture-in-Picture scopes and grenade throw animations
    /// without modifying Codex-owned LR2ContentBuilder.Robot.cs.
    /// - Upgrades M_Acog, M_1P29, M_LongRangeScope to keyword-free circular HDRP shader <c>LR2/PipScope</c>,
    ///   destroying overhanging square <c>LR2_Crosshair</c> quads and enabling SMAA on scope cameras.
    /// - Builds <c>AA_LR2_GrenadeThrow.asset</c> from <c>C_Grenade_Toss.anim</c> (replacing the emote clip
    ///   shipped on demo rifles) and assigns it to all 5 firearm prefabs.
    /// </summary>
    public static class LR2ScopeBuilder
    {
        private const string GrenadeAnimPath = "Assets/KinemationDemo/Animations/Locomotion/Curve/Actions/C_Grenade_Toss.anim";
        private const string CallEmoteTemplatePath = "Assets/KinemationDemo/Animations/Weapons/General/AA_CallEmote.asset";
        private const string OutGrenadeAssetPath = "Assets/LR2/Data/Weapons/AA_LR2_GrenadeThrow.asset";

        public static void UpgradePipScopesAndGrenades()
        {
            int scopesUpgraded = UpgradePipScopeMaterials();
            int quadsRemoved = CleanWeaponScopePrefabs();
            int grenadesFixed = AssignGrenadeThrowClipToWeapons();
            AssetDatabase.SaveAssets();
            Debug.Log($"[LR2] LR2ScopeBuilder: upgraded {scopesUpgraded} PiP scope materials, stripped {quadsRemoved} overhanging crosshair quads, updated {grenadesFixed} weapon grenade throws.");
        }

        private static int UpgradePipScopeMaterials()
        {
            Shader pipShader = Shader.Find("LR2/PipScope");
            if (pipShader == null)
            {
                Debug.LogWarning("[LR2ScopeBuilder] Shader LR2/PipScope not found yet (first import pending?).");
                return 0;
            }

            var specs = new[]
            {
                (
                    matPath: "Assets/KinemationDemo/Meshes/Attachments/Acog/M_Acog.mat",
                    rtPath: "Assets/KinemationDemo/Meshes/Attachments/Acog/RT_Acog.renderTexture",
                    retPath: "Assets/KinemationDemo/Meshes/Attachments/Acog/T_Reticle_Acog.png",
                    crossPath: "Assets/KinemationDemo/Meshes/Attachments/Acog/T_Crosshair_Acog.png",
                    retColor: new Color(2.8f, 0.16f, 0.10f, 1.0f),
                    retSize: 0.46f,
                    lensRadius: 0.482f,
                    softness: 0.068f,
                    distortion: 0.075f,
                    brightness: 1.15f
                ),
                (
                    matPath: "Assets/KinemationDemo/Meshes/Attachments/1P29/M_1P29.mat",
                    rtPath: "Assets/KinemationDemo/Meshes/Attachments/1P29/RT_1P29.renderTexture",
                    retPath: "Assets/KinemationDemo/Meshes/Attachments/1P29/T_Reticle_1P29.png",
                    crossPath: "",
                    retColor: new Color(0.18f, 0.18f, 0.18f, 0.92f),
                    retSize: 0.86f,
                    lensRadius: 0.484f,
                    softness: 0.065f,
                    distortion: 0.065f,
                    brightness: 1.12f
                ),
                (
                    matPath: "Assets/KinemationDemo/Meshes/Attachments/LongRangeScope/Mesh/M_LongRangeScope.mat",
                    rtPath: "Assets/KinemationDemo/Meshes/Attachments/LongRangeScope/Mesh/RT_LongRangeScope.renderTexture",
                    retPath: "Assets/KinemationDemo/Meshes/Attachments/LongRangeScope/Mesh/T_LongRangeScope_Reticle.png",
                    crossPath: "",
                    retColor: new Color(0.14f, 0.14f, 0.14f, 0.94f),
                    retSize: 0.94f,
                    lensRadius: 0.486f,
                    softness: 0.062f,
                    distortion: 0.085f,
                    brightness: 1.16f
                )
            };

            int upgraded = 0;
            foreach (var sp in specs)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(sp.matPath);
                if (mat == null) continue;

                // Preserve existing RenderTexture & Crosshair from saved material or sibling directory before switching shader
                string dir = Path.GetDirectoryName(sp.matPath).Replace('\\', '/');
                Texture rt = mat.HasProperty("_UnlitColorMap") ? mat.GetTexture("_UnlitColorMap") : null;
                if (rt == null && mat.HasProperty("_MainTex")) rt = mat.GetTexture("_MainTex");
                if (rt == null) rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(sp.rtPath);
                if (rt == null)
                {
                    foreach (var g in AssetDatabase.FindAssets("t:RenderTexture", new[] { dir }))
                    {
                        var found = AssetDatabase.LoadAssetAtPath<RenderTexture>(AssetDatabase.GUIDToAssetPath(g));
                        if (found != null) { rt = found; break; }
                    }
                }

                Texture cross = ReadSavedTex(mat, "_Crosshair")
                    ?? (string.IsNullOrEmpty(sp.crossPath) ? null : AssetDatabase.LoadAssetAtPath<Texture>(sp.crossPath))
                    ?? AssetDatabase.LoadAssetAtPath<Texture>(sp.retPath);
                if (cross == null)
                {
                    foreach (var g in AssetDatabase.FindAssets("t:Texture2D", new[] { dir }))
                    {
                        string tp = AssetDatabase.GUIDToAssetPath(g);
                        if (tp.IndexOf("Reticle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            tp.IndexOf("Crosshair", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            cross = AssetDatabase.LoadAssetAtPath<Texture2D>(tp);
                            if (cross != null) break;
                        }
                    }
                }

                mat.shader = pipShader;
                if (rt != null) mat.SetTexture("_MainTex", rt);
                if (cross != null) mat.SetTexture("_CrosshairTex", cross);
                mat.SetColor("_CrosshairColor", sp.retColor);
                mat.SetFloat("_CrosshairSize", sp.retSize);
                mat.SetFloat("_LensRadius", sp.lensRadius);
                mat.SetFloat("_EdgeSoftness", sp.softness);
                mat.SetFloat("_Distortion", sp.distortion);
                mat.SetFloat("_Brightness", sp.brightness);
                mat.SetColor("_Tint", new Color(0.95f, 0.98f, 1.0f, 1.0f));
                mat.renderQueue = 2450;
                EditorUtility.SetDirty(mat);
                upgraded++;
            }
            return upgraded;
        }

        private static Texture ReadSavedTex(Material mat, string propName)
        {
            var so = new SerializedObject(mat);
            var arr = so.FindProperty("m_SavedProperties.m_TexEnvs");
            if (arr == null || !arr.isArray) return null;
            for (int i = 0; i < arr.arraySize; i++)
            {
                var entry = arr.GetArrayElementAtIndex(i);
                var key = entry.FindPropertyRelative("first")?.stringValue;
                if (key == propName)
                    return entry.FindPropertyRelative("second.m_Texture")?.objectReferenceValue as Texture;
            }
            return null;
        }

        private static int CleanWeaponScopePrefabs()
        {
            int quadsRemoved = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/LR2/Prefabs/Weapons" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var contents = PrefabUtility.LoadPrefabContents(path);
                if (contents == null) continue;
                bool changed = false;

                var toDestroy = new List<GameObject>();
                foreach (var tf in contents.GetComponentsInChildren<Transform>(true))
                {
                    if (tf != null && tf.name == "LR2_Crosshair")
                        toDestroy.Add(tf.gameObject);
                }
                foreach (var q in toDestroy)
                {
                    if (q != null)
                    {
                        UnityEngine.Object.DestroyImmediate(q);
                        quadsRemoved++;
                        changed = true;
                    }
                }

                foreach (var cam in contents.GetComponentsInChildren<Camera>(true))
                {
                    if (cam.targetTexture == null) continue;
                    cam.allowHDR = true;
                    cam.nearClipPlane = Mathf.Min(cam.nearClipPlane, 0.08f);
                    cam.farClipPlane = Mathf.Max(cam.farClipPlane, 420f);
                    var hd = cam.GetComponent<HDAdditionalCameraData>();
                    if (hd != null)
                    {
                        hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                        hd.SMAAQuality = HDAdditionalCameraData.SMAAQualityLevel.Medium;
                        hd.stopNaNs = true;
                        EditorUtility.SetDirty(hd);
                    }
                    EditorUtility.SetDirty(cam);
                    changed = true;
                }

                if (changed) PrefabUtility.SaveAsPrefabAsset(contents, path);
                PrefabUtility.UnloadPrefabContents(contents);
            }
            return quadsRemoved;
        }

        private static int AssignGrenadeThrowClipToWeapons()
        {
            AnimationClip tossClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(GrenadeAnimPath);
            if (tossClip == null)
            {
                Debug.LogWarning("[LR2ScopeBuilder] Missing grenade toss clip: " + GrenadeAnimPath);
                return 0;
            }

            var emoteTemplate = AssetDatabase.LoadAssetAtPath<FPSAnimationAsset>(CallEmoteTemplatePath);
            var existing = AssetDatabase.LoadAssetAtPath<FPSAnimationAsset>(OutGrenadeAssetPath);
            var grenadeAsset = existing != null ? existing : ScriptableObject.CreateInstance<FPSAnimationAsset>();

            grenadeAsset.clip = tossClip;
            if (emoteTemplate != null)
            {
                grenadeAsset.rigAsset = emoteTemplate.rigAsset;
                grenadeAsset.mask = emoteTemplate.mask;
                grenadeAsset.overrideMask = emoteTemplate.overrideMask;
            }
            else
            {
                grenadeAsset.mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(
                    "Assets/KinemationDemo/Animations/Locomotion/Generic/UpperBody.mask");
            }

            grenadeAsset.isAdditive = true;
            grenadeAsset.blendTime = new BlendTime(0.14f, 0.22f) { rateScale = 1.25f };
            grenadeAsset.curves = new List<AnimCurve>
            {
                new AnimCurve
                {
                    name = FPSANames.Curve_MaskAttachHand,
                    curve = new AnimationCurve(
                        new Keyframe(0f, 1f),
                        new Keyframe(0.50f, 1f),
                        new Keyframe(0.68f, 0f),
                        new Keyframe(1f, 0f))
                }
            };

            if (existing == null)
            {
                LR2EditorUtil.EnsureFolder(Path.GetDirectoryName(OutGrenadeAssetPath));
                AssetDatabase.CreateAsset(grenadeAsset, OutGrenadeAssetPath);
            }
            else
            {
                EditorUtility.SetDirty(grenadeAsset);
            }

            int updated = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/LR2/Prefabs/Weapons" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var contents = PrefabUtility.LoadPrefabContents(path);
                if (contents == null) continue;
                var gun = contents.GetComponent<LR2Firearm>();
                if (gun != null)
                {
                    var so = new SerializedObject(gun);
                    var gp = so.FindProperty("grenadeClip");
                    if (gp != null) gp.objectReferenceValue = grenadeAsset;
                    var rate = so.FindProperty("grenadeThrowRate");
                    if (rate != null) rate.floatValue = 1.35f;
                    var tacEnd = so.FindProperty("tacticalEndAfterMagIn");
                    if (tacEnd != null) tacEnd.floatValue = 0.075f;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(gun);
                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                    updated++;
                }
                PrefabUtility.UnloadPrefabContents(contents);
            }
            return updated;
        }
    }
}
