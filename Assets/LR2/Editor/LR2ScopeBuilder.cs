using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Demo.Scripts.Runtime.AttachmentSystem;
using KINEMATION.FPSAnimationFramework.Runtime.Core;
using KINEMATION.FPSAnimationFramework.Runtime.Playables;
using KINEMATION.Shared.KAnimationCore.Runtime.Core;
using LR2.Weapons;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace LR2.EditorTools
{
    /// <summary>
    /// B136i, B136r &amp; 11.24.26.3: Builds and upgrades all 5 Kinemation scopes across all 4 primary rifles
    /// (<c>LR2_Mk18</c>, <c>LR2_AK12</c>, <c>LR2_AK74N</c>, <c>LR2_FAL</c>), generates clean iron-sight vs scoped flat-rail
    /// skinned mesh variants (so iron sights toggle ON when unscoped and completely OFF when a scope is fitted),
    /// boresights every <c>AimPoint</c> and PiP <c>Camera</c> to sub-millimeter precision, and upgrades <c>LR2/PipScope</c>
    /// materials and grenade throw animation.
    /// </summary>
    public static class LR2ScopeBuilder
    {
        private const string GrenadeAnimPath = "Assets/KinemationDemo/Animations/Locomotion/Curve/Actions/C_Grenade_Toss.anim";
        private const string CallEmoteTemplatePath = "Assets/KinemationDemo/Animations/Weapons/General/AA_CallEmote.asset";
        private const string OutGrenadeAssetPath = "Assets/LR2/Data/Weapons/AA_LR2_GrenadeThrow.asset";
        private const string MeshOutDir = "Assets/LR2/Data/Weapons/Meshes";

        private struct WeaponOpticMountSpec
        {
            public string prefabPath;
            public string id;
            public float railTopY;
            public Vector2 scopeZRange;
            public float mountZ;
            public Vector3 ironAimPos;
            // Sight post removal boxes in weapon-root rest space: (zMin, zMax, yCut) within |x| < xHalfWidth
            public float removeHalfX;
            public float frontZMin, frontZMax, frontYCut;
            public float rearZMin, rearZMax, rearYCut;
            public bool addTopRailBridge;
            public float railBridgeZMin, railBridgeZMax, railBridgeY;
        }

        private static readonly WeaponOpticMountSpec[] WeaponSpecs =
        {
            new WeaponOpticMountSpec
            {
                id = "Mk18",
                prefabPath = "Assets/LR2/Prefabs/Weapons/LR2_Mk18.prefab",
                railTopY = 0.0400f,
                scopeZRange = new Vector2(-0.10f, 0.24f),
                mountZ = 0.015f,
                ironAimPos = new Vector3(0f, 0.0672f, -0.175f),
                removeHalfX = 0.022f,
                frontZMin = 0.260f, frontZMax = 0.345f, frontYCut = 0.0385f,
                rearZMin = 0f, rearZMax = -1f, rearYCut = 9f, // Mk18_Core already omits rear flip-up sight
                addTopRailBridge = false
            },
            new WeaponOpticMountSpec
            {
                id = "AK12",
                prefabPath = "Assets/LR2/Prefabs/Weapons/LR2_AK12.prefab",
                railTopY = 0.0445f,
                scopeZRange = new Vector2(-0.08f, 0.22f),
                mountZ = 0.015f,
                ironAimPos = new Vector3(0f, 0.0568f, -0.183f),
                removeHalfX = 0.022f,
                frontZMin = 0.275f, frontZMax = 0.355f, frontYCut = 0.0435f,
                rearZMin = -0.175f, rearZMax = -0.090f, rearYCut = 0.0438f,
                addTopRailBridge = false
            },
            new WeaponOpticMountSpec
            {
                id = "AK74N",
                prefabPath = "Assets/LR2/Prefabs/Weapons/LR2_AK74N.prefab",
                railTopY = 0.0455f,
                scopeZRange = new Vector2(-0.16f, 0.14f),
                mountZ = -0.020f,
                ironAimPos = new Vector3(0f, 0.0498f, -0.190f),
                removeHalfX = 0.022f,
                frontZMin = 0.430f, frontZMax = 0.515f, frontYCut = 0.0260f,
                rearZMin = 0.020f, rearZMax = 0.170f, rearYCut = 0.0440f,
                addTopRailBridge = true,
                railBridgeZMin = -0.155f, railBridgeZMax = 0.095f, railBridgeY = 0.0450f
            },
            new WeaponOpticMountSpec
            {
                id = "FAL",
                prefabPath = "Assets/LR2/Prefabs/Weapons/LR2_FAL.prefab",
                railTopY = 0.0325f,
                scopeZRange = new Vector2(-0.09f, 0.18f),
                mountZ = 0.020f,
                ironAimPos = new Vector3(0f, 0.0462f, -0.218f),
                removeHalfX = 0.022f,
                frontZMin = 0.380f, frontZMax = 0.455f, frontYCut = 0.0310f,
                rearZMin = -0.165f, rearZMax = -0.100f, rearYCut = 0.0315f,
                addTopRailBridge = true,
                railBridgeZMin = -0.105f, railBridgeZMax = 0.155f, railBridgeY = 0.0320f
            }
        };

        private struct OpticTemplateSpec
        {
            public string key;
            public string prefabPath;
            public float zOffset;
            public float ySink;
            public float xOffset;
            public float eyeReliefBehindRear;
            public float fovZoom;
            public float sensitivity;
            public bool isDovetail1P29;
            public bool isPip;
        }

        private static readonly OpticTemplateSpec[] OpticSpecs =
        {
            new OpticTemplateSpec
            {
                key = "HoloSight",
                prefabPath = "Assets/KinemationDemo/Meshes/Attachments/Holosight/HoloSight.prefab",
                zOffset = 0.000f, ySink = 0.0030f, xOffset = 0f,
                eyeReliefBehindRear = 0.145f, fovZoom = 0.72f, sensitivity = 0.80f,
                isDovetail1P29 = false, isPip = false
            },
            new OpticTemplateSpec
            {
                key = "RedDot",
                prefabPath = "Assets/KinemationDemo/Meshes/Attachments/Reddot/RedDot.prefab",
                zOffset = 0.015f, ySink = 0.0028f, xOffset = 0f,
                eyeReliefBehindRear = 0.130f, fovZoom = 0.82f, sensitivity = 0.88f,
                isDovetail1P29 = false, isPip = false
            },
            new OpticTemplateSpec
            {
                key = "Trijicon",
                prefabPath = "Assets/KinemationDemo/Meshes/Attachments/Acog/Trijicon.prefab",
                zOffset = 0.005f, ySink = 0.0038f, xOffset = 0f,
                eyeReliefBehindRear = 0.085f, fovZoom = 0.50f, sensitivity = 0.50f,
                isDovetail1P29 = false, isPip = true
            },
            new OpticTemplateSpec
            {
                key = "1P29",
                prefabPath = "Assets/KinemationDemo/Meshes/Attachments/1P29/1P29.prefab",
                zOffset = -0.045f, ySink = 0.024f, xOffset = 0.0099f,
                eyeReliefBehindRear = 0.115f, fovZoom = 0.50f, sensitivity = 0.50f,
                isDovetail1P29 = true, isPip = true
            },
            new OpticTemplateSpec
            {
                key = "SniperScope",
                prefabPath = "Assets/KinemationDemo/Meshes/Attachments/LongRangeScope/SniperScope.prefab",
                zOffset = 0.025f, ySink = 0.0002f, xOffset = 0f,
                eyeReliefBehindRear = 0.120f, fovZoom = 0.30f, sensitivity = 0.30f,
                isDovetail1P29 = false, isPip = true
            }
        };

        public static void UpgradePipScopesAndGrenades()
        {
            int scopesUpgraded = UpgradePipScopeMaterials();
            int sightMatsUpdated = UpgradeReflexReticleMaterials();
            int unifiedWeapons = BuildUnifiedWeaponScopes();
            int quadsRemoved = CleanWeaponScopePrefabs();
            int grenadesFixed = AssignGrenadeThrowClipToWeapons();
            AssetDatabase.SaveAssets();
            Debug.Log($"[LR2] LR2ScopeBuilder: upgraded {scopesUpgraded} PiP materials, {sightMatsUpdated} reflex reticles, configured {unifiedWeapons} rifles with 6 sight options (Iron+5 scopes), stripped {quadsRemoved} quad(s), updated {grenadesFixed} grenade throws.");
        }

        [MenuItem("LR2/Rebuild Unified Weapon Scopes")]
        public static void RebuildUnifiedWeaponScopesMenu()
        {
            UpgradePipScopesAndGrenades();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static int UpgradeReflexReticleMaterials()
        {
            Shader unlit = Shader.Find("HDRP/Unlit");
            if (unlit == null) return 0;
            int updated = 0;
            string[] mats =
            {
                "Assets/KinemationDemo/Meshes/Attachments/Holosight/Reticle/sight.mat",
                "Assets/KinemationDemo/Meshes/Attachments/Reddot/Mesh/M_Reddot.mat"
            };
            foreach (var p in mats)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (mat == null) continue;
                Texture tex = mat.HasProperty("_UnlitColorMap") ? mat.GetTexture("_UnlitColorMap") : null;
                if (tex == null) tex = ReadSavedTex(mat, "_BaseMap") ?? ReadSavedTex(mat, "_MainTex");
                if (tex == null)
                {
                    string dir = Path.GetDirectoryName(p).Replace('\\', '/');
                    foreach (var g in AssetDatabase.FindAssets("t:Texture2D", new[] { dir }))
                    {
                        tex = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(g));
                        if (tex != null) break;
                    }
                }
                mat.shader = unlit;
                mat.SetFloat("_SurfaceType", 1f);
                mat.SetFloat("_BlendMode", 1f);
                mat.SetFloat("_AlphaCutoffEnable", 0f);
                if (tex != null) mat.SetTexture("_UnlitColorMap", tex);
                mat.SetColor("_UnlitColor", p.Contains("Reddot") ? new Color(4.5f, 0.25f, 0.16f, 1f) : new Color(3.4f, 0.22f, 0.14f, 1f));
                mat.SetFloat("_DoubleSidedEnable", 1f);
                HDMaterial.ValidateMaterial(mat);
                EditorUtility.SetDirty(mat);
                updated++;
            }
            return updated;
        }

        private static int StripMissingScriptsRecursive(GameObject root)
        {
            if (root == null) return 0;
            int removed = 0;
            foreach (var tf in root.GetComponentsInChildren<Transform>(true))
                if (tf != null) removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(tf.gameObject);
            return removed;
        }

        private static int BuildUnifiedWeaponScopes()
        {
            LR2EditorUtil.EnsureFolder(MeshOutDir);
            var opticPrefabs = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in OpticSpecs)
            {
                var p = AssetDatabase.LoadAssetAtPath<GameObject>(o.prefabPath);
                if (p != null) opticPrefabs[o.key] = p;
                else Debug.LogWarning("[LR2ScopeBuilder] Missing scope template: " + o.prefabPath);
            }

            Material railMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/LR2/Materials/Weapons/M_AK12_Steel.mat")
                            ?? AssetDatabase.LoadAssetAtPath<Material>("Assets/LR2/Materials/Weapons/M_AK12_Polymer.mat");

            int configured = 0;
            foreach (var spec in WeaponSpecs)
            {
                var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(spec.prefabPath);
                if (prefabAsset == null) continue;
                var contents = PrefabUtility.LoadPrefabContents(spec.prefabPath);
                if (contents == null) continue;

                try
                {
                    StripMissingScriptsRecursive(contents);

                    // Normalize prefab root transform (e.g. LR2_FAL had non-zero authoring offset)
                    contents.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    contents.transform.localScale = Vector3.one;

                    var firearm = contents.GetComponent<LR2Firearm>();
                    var entity = contents.GetComponent<FPSAnimatorEntity>();
                    if (firearm == null || entity == null) continue;

                    // Pick active primary body SkinnedMeshRenderer
                    SkinnedMeshRenderer primarySmr = PickPrimaryBodyRenderer(contents, spec.id, out SkinnedMeshRenderer secondaryMk18Smr);
                    if (primarySmr == null || primarySmr.sharedMesh == null)
                    {
                        Debug.LogWarning($"[LR2ScopeBuilder] No primary SkinnedMeshRenderer on {spec.id}");
                        continue;
                    }

                    // Build clean IronSights and ScopedNoIrons meshes + capture matching material arrays
                    Mesh ironMesh;
                    Mesh scopedMesh;
                    Material[] ironMats = primarySmr.sharedMaterials;
                    Material[] scopedMats = primarySmr.sharedMaterials;
                    if (spec.id == "Mk18")
                    {
                        Mesh coreBase = primarySmr.sharedMesh;
                        string origCorePath = "Assets/KinemationDemo/Meshes/Weapons/Mk18/Mk18_Core.fbx";
                        var loadedCore = AssetDatabase.LoadAllAssetsAtPath(origCorePath).OfType<Mesh>().FirstOrDefault(m => m.vertices.Length > 10000);
                        if (loadedCore != null) coreBase = loadedCore;

                        Mesh reloadEmptyMesh = secondaryMk18Smr != null ? secondaryMk18Smr.sharedMesh : null;
                        if (reloadEmptyMesh == null)
                        {
                            string rePath = "Assets/KinemationDemo/Meshes/Weapons/Mk18/W_Mk18_ReloadEmpty.fbx";
                            reloadEmptyMesh = AssetDatabase.LoadAllAssetsAtPath(rePath).OfType<Mesh>().FirstOrDefault(m => m.vertices.Length > 10000);
                        }

                        if (secondaryMk18Smr != null && secondaryMk18Smr.sharedMaterials != null && secondaryMk18Smr.sharedMaterials.Length > 0)
                            ironMats = secondaryMk18Smr.sharedMaterials;

                        if (reloadEmptyMesh != null && secondaryMk18Smr != null)
                            ironMesh = SaveRemappedMk18IronMesh(reloadEmptyMesh, secondaryMk18Smr.bones, primarySmr.bones, $"{MeshOutDir}/Mesh_Mk18_IronSights.asset");
                        else if (reloadEmptyMesh != null)
                            ironMesh = SaveMeshCopy(reloadEmptyMesh, $"{MeshOutDir}/Mesh_Mk18_IronSights.asset");
                        else
                            ironMesh = coreBase;

                        scopedMesh = BuildScopedNoIronsMesh(contents.transform, primarySmr, coreBase, spec, $"{MeshOutDir}/Mesh_Mk18_ScopedNoIrons.asset");
                        if (secondaryMk18Smr != null) secondaryMk18Smr.gameObject.SetActive(false);
                    }
                    else
                    {
                        Mesh baseOrig = FindOriginalFbxMesh(spec.id, primarySmr.sharedMesh);
                        if (primarySmr.sharedMesh != baseOrig) primarySmr.sharedMesh = baseOrig;
                        ironMesh = baseOrig;
                        scopedMesh = BuildScopedNoIronsMesh(contents.transform, primarySmr, baseOrig, spec, $"{MeshOutDir}/Mesh_{spec.id}_ScopedNoIrons.asset");
                    }

                    // Keep aim points and unified scopes cleanly at weapon root (scale=1, rot=identity), matching Kinemation's prefabs
                    Transform attachParent = contents.transform;

                    // Create / update IronAimPoint in weapon root frame
                    var oldIronAp = contents.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t != null && t.name == "LR2_IronAimPoint");
                    if (oldIronAp != null) UnityEngine.Object.DestroyImmediate(oldIronAp.gameObject);
                    var ironAp = new GameObject("LR2_IronAimPoint").transform;
                    ironAp.SetParent(attachParent, false);
                    ironAp.position = spec.ironAimPos;
                    ironAp.rotation = Quaternion.identity;

                    // Create optional Picatinny rail bridge for AK74N / FAL
                    var oldRail = contents.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t != null && t.name == "LR2_TopRailBridge");
                    if (oldRail != null) UnityEngine.Object.DestroyImmediate(oldRail.gameObject);
                    Transform topRailTf = null;
                    if (spec.addTopRailBridge)
                    {
                        var railGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        UnityEngine.Object.DestroyImmediate(railGo.GetComponent<Collider>());
                        railGo.name = "LR2_TopRailBridge";
                        railGo.layer = contents.layer;
                        railGo.transform.SetParent(contents.transform, false);
                        float len = Mathf.Max(0.08f, spec.railBridgeZMax - spec.railBridgeZMin);
                        float midZ = 0.5f * (spec.railBridgeZMin + spec.railBridgeZMax);
                        railGo.transform.position = new Vector3(0f, spec.railBridgeY - 0.004f, midZ);
                        railGo.transform.rotation = Quaternion.identity;
                        railGo.transform.localScale = new Vector3(0.021f, 0.008f, len);
                        var rr = railGo.GetComponent<MeshRenderer>();
                        if (rr != null && railMat != null) rr.sharedMaterial = railMat;
                        railGo.SetActive(false);
                        topRailTf = railGo.transform;
                    }

                    // Remove any previous unified scopes container & hide legacy standalone scopes so nothing doubles up
                    var oldUnified = contents.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t != null && t.name == "LR2_UnifiedScopes");
                    if (oldUnified != null) UnityEngine.Object.DestroyImmediate(oldUnified.gameObject);
                    foreach (var oldSa in contents.GetComponentsInChildren<ScopeAttachment>(true))
                    {
                        if (oldSa == null) continue;
                        oldSa.gameObject.SetActive(false);
                    }
                    foreach (var t in contents.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == null) continue;
                        if (t.name == "RMRMount" || t.name == "SideScope" || t.name == "PointAim")
                            t.gameObject.SetActive(false);
                    }

                    var unifiedRoot = new GameObject("LR2_UnifiedScopes").transform;
                    unifiedRoot.SetParent(contents.transform, false);
                    unifiedRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    unifiedRoot.localScale = Vector3.one;

                    var installedScopes = new List<ScopeAttachment>(OpticSpecs.Length);
                    foreach (var os in OpticSpecs)
                    {
                        if (!opticPrefabs.TryGetValue(os.key, out var oPrefab) || oPrefab == null) continue;
                        var inst = (GameObject)PrefabUtility.InstantiatePrefab(oPrefab, unifiedRoot);
                        PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                        inst.name = $"LR2_Scope_{os.key}";
                        SetLayerRecursive(inst, contents.layer);
                        StripMissingScriptsRecursive(inst);

                        // Strip any colliders, duplicate ReticleCamera (keep single PiP camera such as 1P29's), and Trijicon piggyback RMR
                        foreach (var col in inst.GetComponentsInChildren<Collider>(true))
                            if (col != null) UnityEngine.Object.DestroyImmediate(col);
                        var scopeCams = inst.GetComponentsInChildren<Camera>(true);
                        if (scopeCams.Length > 1)
                        {
                            bool keptPrimary = false;
                            foreach (var cam in scopeCams)
                            {
                                if (cam == null) continue;
                                bool isReticle = cam.gameObject.name.IndexOf("ReticleCamera", StringComparison.OrdinalIgnoreCase) >= 0;
                                if ((isReticle || cam.targetTexture == null) && (keptPrimary || cam.targetTexture == null))
                                    UnityEngine.Object.DestroyImmediate(cam.gameObject);
                                else
                                    keptPrimary = true;
                            }
                        }
                        foreach (var sub in inst.GetComponentsInChildren<Transform>(true))
                        {
                            if (sub == null) continue;
                            if (sub.name == "RMRMount" || sub.name == "rearsight" || sub.name == "LR2_Crosshair" ||
                                (os.key == "Trijicon" && sub.name == "Reddot" && sub.gameObject != inst))
                                sub.gameObject.SetActive(false);
                        }
                        foreach (var subSa in inst.GetComponentsInChildren<ScopeAttachment>(true))
                            if (subSa != null && subSa.gameObject != inst) UnityEngine.Object.DestroyImmediate(subSa);

                        var sa = inst.GetComponent<ScopeAttachment>() ?? inst.AddComponent<ScopeAttachment>();
                        sa.aimFovZoom = os.fovZoom;
                        sa.sensitivityMultiplier = os.sensitivity;
                        sa.attachmentLayerSettings = new List<FPSAnimatorLayerSettings>(); // No weapon-specific skeletal offset override

                        // Place scope in weapon-root coordinates first
                        float targetY;
                        float targetX = os.xOffset;
                        float targetZ = Mathf.Clamp(spec.mountZ + os.zOffset, spec.scopeZRange.x, spec.scopeZRange.y);

                        if (os.isDovetail1P29)
                        {
                            if (spec.id == "AK74N")
                            {
                                // Native AK-74N side-dovetail coordinates calibrated to receiver lug
                                inst.transform.position = new Vector3(-0.0036f, -0.0019f, -0.0967f);
                            }
                            else
                            {
                                // Rail-mounted 1P29 on MK18 / AK12 / FAL: optically centered X = 0, clamp perched at railTopY
                                targetY = spec.railTopY - os.ySink;
                                inst.transform.position = new Vector3(targetX, targetY, targetZ);
                            }
                        }
                        else
                        {
                            targetY = spec.railTopY - os.ySink;
                            inst.transform.position = new Vector3(targetX, targetY, targetZ);
                        }
                        inst.transform.rotation = Quaternion.identity;

                        // If gun needs a top rail bridge (AK74N/FAL) and this is a Picatinny optic, attach a local rail bridge child
                        if (topRailTf != null && !os.isDovetail1P29)
                        {
                            var railCopy = UnityEngine.Object.Instantiate(topRailTf.gameObject);
                            railCopy.name = "LR2_ScopeRailBridge";
                            railCopy.SetActive(true);
                            railCopy.transform.SetPositionAndRotation(topRailTf.position, Quaternion.identity);
                            railCopy.transform.SetParent(inst.transform, true);
                        }

                        // Boresight reticle / PiP lens / Camera / AimPoint to sub-millimeter accuracy in weapon root space
                        Renderer opticWindow = FindOpticWindowRenderer(inst, os.isPip);
                        Bounds totalBnd = MeasureRendererBounds(inst);
                        Vector3 winCenter = opticWindow != null ? opticWindow.bounds.center : (sa.aimPoint != null ? sa.aimPoint.position : totalBnd.center);
                        float scopeRearZ = opticWindow != null ? opticWindow.bounds.min.z : totalBnd.min.z;

                        Transform apTf = sa.aimPoint;
                        if (apTf == null)
                        {
                            var existingAp = inst.transform.Find("AimPoint");
                            apTf = existingAp != null ? existingAp : new GameObject("AimPoint").transform;
                            apTf.SetParent(inst.transform, false);
                            sa.aimPoint = apTf;
                        }
                        float apZ = Mathf.Clamp(scopeRearZ - os.eyeReliefBehindRear, -0.32f, -0.10f);
                        apTf.position = new Vector3(winCenter.x, winCenter.y, apZ);
                        apTf.rotation = Quaternion.identity;

                        // Align PiP Camera cleanly onto the exact same optical axis (winCenter.x, winCenter.y) ahead of hand/thumb
                        foreach (var cam in inst.GetComponentsInChildren<Camera>(true))
                        {
                            if (cam == null) continue;
                            float camZ = Mathf.Max(cam.transform.position.z, 0.28f);
                            cam.transform.position = new Vector3(winCenter.x, winCenter.y, camZ);
                            cam.transform.rotation = Quaternion.identity;
                            cam.enabled = false;
                            cam.allowHDR = true;
                            cam.nearClipPlane = 0.06f;
                            cam.farClipPlane = 450f;
                            var hd = cam.GetComponent<HDAdditionalCameraData>() ?? cam.gameObject.AddComponent<HDAdditionalCameraData>();
                            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                            hd.SMAAQuality = HDAdditionalCameraData.SMAAQualityLevel.Medium;
                            hd.stopNaNs = true;
                            var al = cam.GetComponent<AudioListener>();
                            if (al != null) UnityEngine.Object.DestroyImmediate(al);
                            EditorUtility.SetDirty(cam);
                        }

                        // Default active state: HoloSight (index 0 in ScopeAttachment list = choice 1)
                        inst.SetActive(installedScopes.Count == 0);
                        installedScopes.Add(sa);
                        EditorUtility.SetDirty(sa);
                    }

                    if (topRailTf != null) UnityEngine.Object.DestroyImmediate(topRailTf.gameObject);

                    // Assign LR2Firearm serialized scope & iron-sight fields
                    var fso = new SerializedObject(firearm);
                    fso.FindProperty("primaryBodyRenderer").objectReferenceValue = primarySmr;
                    fso.FindProperty("ironSightsMesh").objectReferenceValue = ironMesh;
                    fso.FindProperty("scopedNoIronsMesh").objectReferenceValue = scopedMesh;
                    void SetMatArray(string propName, Material[] arr)
                    {
                        var mp = fso.FindProperty(propName);
                        if (mp == null || arr == null) return;
                        mp.arraySize = arr.Length;
                        for (int mIdx = 0; mIdx < arr.Length; mIdx++)
                            mp.GetArrayElementAtIndex(mIdx).objectReferenceValue = arr[mIdx];
                    }
                    SetMatArray("ironSightsMaterials", ironMats);
                    SetMatArray("scopedNoIronsMaterials", scopedMats);
                    fso.FindProperty("ironAimPoint").objectReferenceValue = ironAp;

                    var sgProp = fso.FindProperty("scopeGroups");
                    sgProp.ClearArray();
                    sgProp.InsertArrayElementAtIndex(0);
                    var firstGrp = sgProp.GetArrayElementAtIndex(0);
                    var attList = firstGrp.FindPropertyRelative("attachments");
                    attList.ClearArray();
                    for (int i = 0; i < installedScopes.Count; i++)
                    {
                        attList.InsertArrayElementAtIndex(i);
                        attList.GetArrayElementAtIndex(i).objectReferenceValue = installedScopes[i];
                    }
                    fso.FindProperty("kinemationAttachments").boolValue = true;
                    fso.FindProperty("gateWallTuckInAds").boolValue = true;
                    fso.ApplyModifiedPropertiesWithoutUndo();

                    // Set default active prefab look = HoloSight (scoped mesh + Holo AimPoint)
                    if (scopedMesh != null) primarySmr.sharedMesh = scopedMesh;
                    if (installedScopes.Count > 0 && installedScopes[0].aimPoint != null)
                        entity.defaultAimPoint = installedScopes[0].aimPoint;

                    StripMissingScriptsRecursive(contents);
                    EditorUtility.SetDirty(primarySmr);
                    EditorUtility.SetDirty(entity);
                    EditorUtility.SetDirty(firearm);
                    PrefabUtility.SaveAsPrefabAsset(contents, spec.prefabPath, out bool saved);
                    if (!saved)
                        throw new InvalidOperationException($"[LR2ScopeBuilder] Failed to save prefab: {spec.prefabPath}");
                    configured++;
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }

            AssetDatabase.SaveAssets();
            return configured;
        }

        private static SkinnedMeshRenderer PickPrimaryBodyRenderer(GameObject root, string weaponId, out SkinnedMeshRenderer secondaryMk18)
        {
            secondaryMk18 = null;
            var all = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (weaponId == "Mk18")
            {
                SkinnedMeshRenderer activeCore = null;
                SkinnedMeshRenderer reloadEmpty = null;
                foreach (var smr in all)
                {
                    if (smr == null || smr.sharedMesh == null) continue;
                    string ap = AssetDatabase.GetAssetPath(smr.sharedMesh);
                    if (smr.sharedMesh.name.Contains("IronSights") || ap.Contains("W_Mk18_ReloadEmpty") || (!smr.gameObject.activeInHierarchy && reloadEmpty == null))
                        reloadEmpty = smr;
                    else
                        activeCore = smr;
                }
                if (activeCore == null && all.Length > 0) activeCore = all[0];
                if (reloadEmpty == activeCore && all.Length > 1) reloadEmpty = all[1];
                secondaryMk18 = reloadEmpty;
                return activeCore;
            }
            return all.OrderByDescending(s => s.sharedMesh != null ? s.sharedMesh.vertexCount : 0).FirstOrDefault();
        }

        private static Mesh FindOriginalFbxMesh(string weaponId, Mesh fallback)
        {
            string fbxPath = weaponId switch
            {
                "AK12" => "Assets/KinemationDemo/Meshes/Weapons/AK12/W_AK12_ReloadEmpty.fbx",
                "AK74N" => "Assets/KinemationDemo/Meshes/Weapons/AK74N/AKS-74N.fbx",
                "FAL" => "Assets/KinemationDemo/Meshes/Weapons/FAL/W_FAL_ReloadEmpty.fbx",
                _ => null
            };
            if (!string.IsNullOrEmpty(fbxPath))
            {
                var found = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Mesh>()
                    .OrderByDescending(m => m.vertexCount).FirstOrDefault();
                if (found != null) return found;
            }
            return fallback;
        }

        private static Mesh SaveMeshCopy(Mesh src, string assetPath)
        {
            Mesh copy = UnityEngine.Object.Instantiate(src);
            copy.name = Path.GetFileNameWithoutExtension(assetPath);
            SaveOrReplaceMeshAsset(copy, assetPath);
            return AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        }

        private static Mesh SaveRemappedMk18IronMesh(Mesh srcMesh, Transform[] srcBones, Transform[] dstBones, string assetPath)
        {
            Mesh copy = UnityEngine.Object.Instantiate(srcMesh);
            copy.name = Path.GetFileNameWithoutExtension(assetPath);

            var dstIdxByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < dstBones.Length; i++)
                if (dstBones[i] != null) dstIdxByName[dstBones[i].name] = i;

            var remap = new int[srcBones.Length];
            for (int i = 0; i < srcBones.Length; i++)
            {
                string bName = srcBones[i] != null ? srcBones[i].name : "";
                remap[i] = dstIdxByName.TryGetValue(bName, out int mapped) ? mapped : 0;
            }

            var weights = copy.boneWeights;
            for (int i = 0; i < weights.Length; i++)
            {
                var bw = weights[i];
                if (bw.boneIndex0 >= 0 && bw.boneIndex0 < remap.Length) bw.boneIndex0 = remap[bw.boneIndex0];
                if (bw.boneIndex1 >= 0 && bw.boneIndex1 < remap.Length) bw.boneIndex1 = remap[bw.boneIndex1];
                if (bw.boneIndex2 >= 0 && bw.boneIndex2 < remap.Length) bw.boneIndex2 = remap[bw.boneIndex2];
                if (bw.boneIndex3 >= 0 && bw.boneIndex3 < remap.Length) bw.boneIndex3 = remap[bw.boneIndex3];
                weights[i] = bw;
            }
            copy.boneWeights = weights;

            var srcBps = srcMesh.bindposes;
            var newBps = new Matrix4x4[dstBones.Length];
            for (int i = 0; i < dstBones.Length; i++) newBps[i] = Matrix4x4.identity;
            for (int i = 0; i < srcBones.Length && i < srcBps.Length; i++)
            {
                int di = remap[i];
                if (di >= 0 && di < newBps.Length) newBps[di] = srcBps[i];
            }
            copy.bindposes = newBps;
            copy.RecalculateBounds();

            SaveOrReplaceMeshAsset(copy, assetPath);
            return AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        }

        private static Mesh BuildScopedNoIronsMesh(Transform weaponRoot, SkinnedMeshRenderer smr, Mesh srcMesh, WeaponOpticMountSpec spec, string assetPath)
        {
            Mesh copy = UnityEngine.Object.Instantiate(srcMesh);
            copy.name = Path.GetFileNameWithoutExtension(assetPath);

            var bones = smr.bones;
            var bps = srcMesh.bindposes;
            var weights = srcMesh.boneWeights;
            var verts = copy.vertices;

            var skinMats = new Matrix4x4[bones.Length];
            for (int b = 0; b < bones.Length; b++)
            {
                Transform bt = bones[b] != null ? bones[b] : smr.transform;
                Matrix4x4 bp = b < bps.Length ? bps[b] : Matrix4x4.identity;
                skinMats[b] = weaponRoot.worldToLocalMatrix * bt.localToWorldMatrix * bp;
            }

            int folded = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                Matrix4x4 m = Matrix4x4.identity;
                if (i < weights.Length && skinMats.Length > 0)
                {
                    var bw = weights[i];
                    m = ScaleMat(skinMats[bw.boneIndex0], bw.weight0);
                    if (bw.weight1 > 0f && bw.boneIndex1 < skinMats.Length) AddMat(ref m, skinMats[bw.boneIndex1], bw.weight1);
                    if (bw.weight2 > 0f && bw.boneIndex2 < skinMats.Length) AddMat(ref m, skinMats[bw.boneIndex2], bw.weight2);
                    if (bw.weight3 > 0f && bw.boneIndex3 < skinMats.Length) AddMat(ref m, skinMats[bw.boneIndex3], bw.weight3);
                }
                Vector3 wp = m.MultiplyPoint3x4(verts[i]);
                if (Mathf.Abs(wp.x) > spec.removeHalfX) continue;

                float newY = wp.y;
                if (wp.z >= spec.frontZMin && wp.z <= spec.frontZMax && wp.y > spec.frontYCut)
                    newY = spec.frontYCut;
                else if (spec.rearZMax > spec.rearZMin && wp.z >= spec.rearZMin && wp.z <= spec.rearZMax && wp.y > spec.rearYCut)
                    newY = spec.rearYCut;

                if (newY < wp.y - 1e-4f)
                {
                    wp.y = newY;
                    verts[i] = m.inverse.MultiplyPoint3x4(wp);
                    folded++;
                }
            }
            copy.vertices = verts;
            copy.RecalculateBounds();
            SaveOrReplaceMeshAsset(copy, assetPath);
            Debug.Log($"[LR2ScopeBuilder] {spec.id} ScopedNoIrons mesh: folded {folded} protruding iron-sight vertices.");
            return AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        }

        private static void SaveOrReplaceMeshAsset(Mesh mesh, string assetPath)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mesh, existing);
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
            else
            {
                AssetDatabase.CreateAsset(mesh, assetPath);
            }
        }

        private static Renderer FindOpticWindowRenderer(GameObject scopeRoot, bool isPip)
        {
            foreach (var r in scopeRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.gameObject.activeInHierarchy) continue;
                if (isPip)
                {
                    if (r.name == "Reticle" || r.sharedMaterials.Any(IsPipLens)) return r;
                }
                else
                {
                    if (r.name == "Plane") return r;
                }
            }
            return null;
        }

        private static Bounds MeasureRendererBounds(GameObject root)
        {
            Bounds b = default;
            bool init = false;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.gameObject.activeInHierarchy) continue;
                if (!init) { b = r.bounds; init = true; } else b.Encapsulate(r.bounds);
            }
            return b;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform)
                if (c != null) SetLayerRecursive(c.gameObject, layer);
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

        private static bool IsPipLens(Material m) =>
            m != null && (m.name == "M_1P29" || m.name == "M_Acog" || m.name == "M_LongRangeScope" || (m.shader != null && m.shader.name == "LR2/PipScope"));

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

                if (changed)
                {
                    StripMissingScriptsRecursive(contents);
                    PrefabUtility.SaveAsPrefabAsset(contents, path, out bool saved);
                    if (!saved) throw new InvalidOperationException($"[LR2ScopeBuilder] Failed to save prefab: {path}");
                }
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
                    StripMissingScriptsRecursive(contents);
                    var so = new SerializedObject(gun);
                    var gp = so.FindProperty("grenadeClip");
                    if (gp != null) gp.objectReferenceValue = grenadeAsset;
                    var rate = so.FindProperty("grenadeThrowRate");
                    if (rate != null) rate.floatValue = 1.35f;
                    var tacEnd = so.FindProperty("tacticalEndAfterMagIn");
                    if (tacEnd != null) tacEnd.floatValue = 0.025f;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(gun);
                    PrefabUtility.SaveAsPrefabAsset(contents, path, out bool saved);
                    if (!saved) throw new InvalidOperationException($"[LR2ScopeBuilder] Failed to save prefab: {path}");
                    updated++;
                }
                PrefabUtility.UnloadPrefabContents(contents);
            }
            return updated;
        }

        private static Matrix4x4 ScaleMat(in Matrix4x4 m, float s)
        {
            Matrix4x4 r = default;
            for (int i = 0; i < 16; i++) r[i] = m[i] * s;
            return r;
        }

        private static void AddMat(ref Matrix4x4 dst, in Matrix4x4 m, float s)
        {
            for (int i = 0; i < 16; i++) dst[i] += m[i] * s;
        }
    }
}
