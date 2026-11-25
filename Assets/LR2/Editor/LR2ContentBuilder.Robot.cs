using System.Collections.Generic;
using System.IO;
using System.Linq;
using Demo.Scripts.Runtime.AttachmentSystem;
using KINEMATION.FPSAnimationFramework.Runtime.Camera;
using KINEMATION.FPSAnimationFramework.Runtime.Core;

using KINEMATION.FPSAnimationFramework.Runtime.Playables;
using LR2.AI;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using LR2.FX;
using LR2.Weapons;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using static LR2.EditorTools.LR2EditorUtil;
using EditorAnimatorController = UnityEditor.Animations.AnimatorController;

namespace LR2.EditorTools
{
    /// <summary>
    /// B136 (user 11-22: "switch back to OG kinemation model ... default gun, and their default IK values for ADS and different
    /// kinds of ads ... enable all kinemation features"): the soldier is Kinemation's ORIGINAL demo character, the generic Unity
    /// robot (FPSGenericPlayer), with Kinemation's own guns, profiles, attachments, reloads and grip poses. LR2 keeps its shorter
    /// timings on its own COPIES of Kinemation assets (the originals stay untouched), and adds the knife, torch and grenade.
    /// </summary>
    public static partial class LR2ContentBuilder
    {
        public enum SoldierRig { KinemationRobot, SwatRetarget }

        /// <summary>B136: which soldier the builder makes. SwatRetarget = the old hidden driver + retargeted SWAT.</summary>
        public static readonly SoldierRig Rig = SoldierRig.KinemationRobot;
        private static bool Robot => Rig == SoldierRig.KinemationRobot;

        private const string RobotPrefab = DemoDir + "/Prefabs/FPSGenericPlayer.prefab";
        private const string GenericControllerPath = DemoDir + "/Animations/Locomotion/FPSAnimator_Generic.controller";
        private const string LR2GenericControllerPath = DataDir + "/Characters/LR2_FPSAnimator_Generic.controller";
        private const string RobotMatDir = DemoDir + "/Meshes/Character/UnityRobot/Materials";
        private const string GrenadeClipPath = DemoDir + "/Animations/Weapons/General/WeaponAnimations/Generic/C_GrenadeThrow.asset";
        private const string SightMatDir = "Assets/LR2/Materials/Weapons/Sights";

        // ------------------------------------------------------------------ controller copies
        /// <summary>Copies a Kinemation asset file to an LR2 path; an existing copy keeps its GUID (its .meta stays).</summary>
        private static T CopyKinemationAsset<T>(string src, string dst) where T : Object
        {
            EnsureFolder(Path.GetDirectoryName(dst).Replace('\\', '/'));
            File.Copy(src, dst, true);
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<T>(dst);
        }

        /// <summary>LR2's copy of Kinemation's generic controller with LR2's shorter timings (LocoSpeed, fast stances, turns).</summary>
        private static EditorAnimatorController BuildLR2GenericController()
        {
            var ctrl = CopyKinemationAsset<EditorAnimatorController>(GenericControllerPath, LR2GenericControllerPath);
            if (ctrl == null) { Debug.LogError("[LR2] B136: could not copy " + GenericControllerPath); return null; }
            ConfigureLocomotionSpeed(ctrl);
            return ctrl;
        }

        /// <summary>Kinemation's generic controller becomes LR2's copy; an override controller is copied and re-based on it.</summary>
        private static RuntimeAnimatorController LR2Override(RuntimeAnimatorController src, EditorAnimatorController lr2Ctrl)
        {
            if (src == null || lr2Ctrl == null) return src;
            string srcPath = AssetDatabase.GetAssetPath(src);
            if (src is EditorAnimatorController) return srcPath == GenericControllerPath ? lr2Ctrl : src;
            if (!(src is AnimatorOverrideController) || string.IsNullOrEmpty(srcPath)) return src;
            var copy = CopyKinemationAsset<AnimatorOverrideController>(srcPath, DataDir + "/Characters/LR2_" + Path.GetFileName(srcPath));
            if (copy == null) return src;
            var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(copy.overridesCount);
            copy.GetOverrides(pairs);
            copy.runtimeAnimatorController = lr2Ctrl;
            copy.ApplyOverrides(pairs);
            EditorUtility.SetDirty(copy);
            return copy;
        }

        // ------------------------------------------------------------------ sights (HDRP)
        private static readonly string[] SightMaterialNames = { "sight", "m_reddot", "m_1p29", "m_acog", "m_longrangescope" };

        private static bool IsSightMaterial(Material m) =>
            m != null && SightMaterialNames.Contains(m.name.ToLowerInvariant()) && AssetDatabase.GetAssetPath(m).StartsWith(DemoDir + "/Meshes/Attachments");

        private static bool IsPipLens(Material m) => m != null && (m.name == "M_1P29" || m.name == "M_Acog" || m.name == "M_LongRangeScope");

        /// <summary>
        /// B136: Kinemation's sights are URP shader graphs (magenta in HDRP). Red dot / holo reticles become additive HDRP Unlit
        /// with their reticle texture; picture-in-picture scope lenses become HDRP Unlit showing their scope camera's render
        /// texture (the crosshair is a separate overlay quad, see <see cref="PrepareScopes"/>). Converted in place, like the rest
        /// of the demo materials, so every prefab that uses them follows.
        /// </summary>
        public static void ConvertDemoSightMaterials()
        {
            var unlit = Shader.Find("HDRP/Unlit");
            if (unlit == null) return;
            int n = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { DemoDir + "/Meshes/Attachments" }))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (!IsSightMaterial(mat) || mat.shader == unlit) continue;
                var so = new SerializedObject(mat);
                var tex = new Dictionary<string, Texture>();
                ReadSaved(so.FindProperty("m_SavedProperties.m_TexEnvs"), (k, p) => tex[k] = p.FindPropertyRelative("m_Texture").objectReferenceValue as Texture);
                Texture reticle = Get(tex, "_BaseMap") ?? Get(tex, "_MainTex");
                Texture lensRt = Get(tex, "_LenseTexture") as RenderTexture ?? reticle as RenderTexture;
                bool pip = IsPipLens(mat);
                if (pip && lensRt == null)
                {
                    // the 1P29 / long-range materials only name their crosshair: the render texture sits next to them
                    string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(mat)).Replace('\\', '/');
                    lensRt = AssetDatabase.FindAssets("t:RenderTexture", new[] { dir })
                        .Select(g => AssetDatabase.LoadAssetAtPath<RenderTexture>(AssetDatabase.GUIDToAssetPath(g))).FirstOrDefault(r => r != null);
                }
                mat.shader = unlit;
                if (pip)
                {
                    mat.SetFloat("_SurfaceType", 0f);
                    mat.SetTexture("_UnlitColorMap", lensRt);
                    mat.SetColor("_UnlitColor", Color.white);
                }
                else
                {
                    mat.SetFloat("_SurfaceType", 1f);
                    mat.SetFloat("_BlendMode", 1f); // additive
                    mat.SetFloat("_AlphaCutoffEnable", 0f);
                    mat.SetTexture("_UnlitColorMap", reticle);
                    mat.SetColor("_UnlitColor", new Color(3.2f, 0.22f, 0.14f, 1f)); // HDR red: stays readable against bright skies
                    mat.SetFloat("_DoubleSidedEnable", 1f);
                }
                HDMaterial.ValidateMaterial(mat);
                EditorUtility.SetDirty(mat);
                n++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[LR2] B136: {n} Kinemation sight / scope lens materials converted to HDRP Unlit.");
        }

        private static Material CrosshairMaterial(Material lens)
        {
            if (lens == null) return null;
            // read the crosshair from the ORIGINAL URP material data (still serialized under its old property names)
            var so = new SerializedObject(lens);
            Texture cross = null;
            Color color = new Color(0.1f, 0.1f, 0.1f, 1f);
            ReadSaved(so.FindProperty("m_SavedProperties.m_TexEnvs"), (k, p) => { if (k == "_Crosshair") cross = p.FindPropertyRelative("m_Texture").objectReferenceValue as Texture; });
            ReadSaved(so.FindProperty("m_SavedProperties.m_Colors"), (k, p) => { if (k == "_CrosshairColor") color = p.colorValue; });
            if (cross == null) return null;
            var m = new Material(Shader.Find("HDRP/Unlit"));
            m.SetFloat("_SurfaceType", 1f);
            m.SetFloat("_BlendMode", 0f); // alpha
            m.SetTexture("_UnlitColorMap", cross);
            color.a = 1f;
            m.SetColor("_UnlitColor", color.maxColorComponent > 0.5f ? color * 2.2f : color); // lit red reticles glow a little
            HDMaterial.ValidateMaterial(m);
            return SaveMaterial(m, $"{SightMatDir}/M_Crosshair_{lens.name}.mat");
        }

        /// <summary>
        /// B136: scope cameras get HDRP camera data (they render only while the local player aims through them, see
        /// LR2Firearm.UpdateScopeCameras), and every picture-in-picture lens gets a crosshair quad just in front of it.
        /// </summary>
        private static void PrepareScopes(GameObject weapon)
        {
            foreach (var cam in weapon.GetComponentsInChildren<Camera>(true))
            {
                if (cam.targetTexture == null) continue;
                cam.enabled = false;
                var hd = cam.GetComponent<HDAdditionalCameraData>() ?? cam.gameObject.AddComponent<HDAdditionalCameraData>();
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;
                hd.stopNaNs = true;
                var listener = cam.GetComponent<AudioListener>();
                if (listener != null) Object.DestroyImmediate(listener);
            }
            foreach (var r in weapon.GetComponentsInChildren<Renderer>(true))
            {
                if (r.transform.Find("LR2_Crosshair") != null) continue;
                var mats = r.sharedMaterials;
                int lensIndex = System.Array.FindIndex(mats, IsPipLens);
                if (lensIndex < 0) continue;
                var cross = CrosshairMaterial(mats[lensIndex]);
                if (cross == null) continue;
                // the lens face: the renderer's bounds in the weapon's frame (x right, y up, z = barrel)
                var wb = BoundsIn(weapon.transform, r.bounds);
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.DestroyImmediate(quad.GetComponent<Collider>());
                quad.name = "LR2_Crosshair";
                quad.transform.SetParent(r.transform, true);
                quad.transform.position = weapon.transform.TransformPoint(new Vector3(wb.center.x, wb.center.y, wb.min.z - 0.0008f));
                quad.transform.rotation = weapon.transform.rotation; // a Unity quad faces -Z: toward the eye
                float size = Mathf.Max(0.004f, Mathf.Min(wb.size.x, wb.size.y));
                var ls = quad.transform.lossyScale;
                quad.transform.localScale = Vector3.Scale(quad.transform.localScale, new Vector3(size / Mathf.Max(1e-4f, ls.x), size / Mathf.Max(1e-4f, ls.y), 1f));
                var qr = quad.GetComponent<MeshRenderer>();
                qr.sharedMaterial = cross;
                qr.shadowCastingMode = ShadowCastingMode.Off;
                qr.receiveShadows = false;
                quad.layer = r.gameObject.layer;
            }
        }

        /// <summary>A world-space AABB re-expressed as an AABB in <paramref name="frame"/>'s local space.</summary>
        private static Bounds BoundsIn(Transform frame, Bounds world)
        {
            var b = new Bounds(frame.InverseTransformPoint(world.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var c = world.center + Vector3.Scale(world.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                b.Encapsulate(frame.InverseTransformPoint(c));
            }
            return b;
        }

        // ------------------------------------------------------------------ guns
        private static EditorAnimatorController _lr2GenericController;

        private static void ConfigureRobotFirearm(SerializedObject so, GameObject go)
        {
            if (_lr2GenericController == null) _lr2GenericController = BuildLR2GenericController();
            void B(string n, bool v) { var p = so.FindProperty(n); if (p != null) p.boolValue = v; }
            B("kinemationAttachments", true);
            B("gateWallTuckInAds", true); // Gate wall collision out during ADS so cover parapets never deflect sights off target
            var ov = so.FindProperty("overrideController");
            if (ov != null) ov.objectReferenceValue = LR2Override(ov.objectReferenceValue as RuntimeAnimatorController, _lr2GenericController);
            // every gun can throw: the pistol borrows the rifles' grenade toss
            var gp = so.FindProperty("grenadeClip");
            if (gp != null && gp.objectReferenceValue == null) gp.objectReferenceValue = AssetDatabase.LoadAssetAtPath<FPSAnimationAsset>(GrenadeClipPath);
            PrepareScopes(go);
        }

        /// <summary>
        /// B136 (user 11-22: "add a generic laser attachment, from where the laser is coming out of, from all the guns who are
        /// missing them"). None of Kinemation's guns carries a laser module, so each gets LR2's: a PEQ-style box on the right
        /// rail of a rifle's handguard (3 o'clock, 15 cm behind the muzzle) or a compact unit under a pistol's frame. The bare
        /// gun is probed with temporary mesh colliders so the device sits on the surface, and the beam leaves its lens
        /// ("LaserEmitter", parallel to the bore) instead of the muzzle.
        /// </summary>
        private static void AddLaserDevice(GameObject gunGo, LR2Firearm gun, Transform muzzle, bool pistol)
        {
            if (gunGo == null || gun == null || muzzle == null) return;
            var old = gunGo.transform.Find("LR2_LaserDevice");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            GameObject dev = null;
            try
            {
                Vector3 fwd = muzzle.forward, up = gunGo.transform.up;
                up = Vector3.ProjectOnPlane(up, fwd).normalized;
                Vector3 right = Vector3.Cross(up, fwd).normalized;
                Vector3 side = pistol ? -up : right;                       // pistol: under the frame; rifle: 3 o'clock
                Vector3 across = Vector3.Cross(fwd, side).normalized;
                Vector3 basePos = muzzle.position - fwd * (pistol ? 0.06f : 0.15f);
                float crossTolerance = pistol ? 0.075f : 0.045f;
                float minReach = pistol ? 0.018f : 0.014f;

                // Kinemation's gun meshes are not Read/Write (no MeshCollider cooks), so read vertices directly: skinned meshes are
                // baked first (a baked copy is readable and in the renderer's frame), static ones go through the editor's read-only
                // mesh data. The device sits on the outermost vertex of a slice across the handguard; several depths are tried.
                var points = new List<Vector3>(4096);
                foreach (var r in gunGo.GetComponentsInChildren<Renderer>())
                {
                    if (r is ParticleSystemRenderer || r is LineRenderer || r.GetComponentInParent<BaseAttachment>(true) != null) continue;
                    try
                    {
                        if (r is SkinnedMeshRenderer smr)
                        {
                            var bake = new Mesh();
                            smr.BakeMesh(bake, true);
                            var m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                            foreach (var v in bake.vertices) points.Add(m.MultiplyPoint3x4(v));
                            Object.DestroyImmediate(bake);
                        }
                        else
                        {
                            var mesh = r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null;
                            if (mesh == null) continue;
                            var m = r.transform.localToWorldMatrix;
                            using (var data = Mesh.AcquireReadOnlyMeshData(mesh))
                            {
                                var verts = new Unity.Collections.NativeArray<Vector3>(data[0].vertexCount, Unity.Collections.Allocator.Temp);
                                data[0].GetVertices(verts);
                                foreach (var v in verts) points.Add(m.MultiplyPoint3x4(v));
                                verts.Dispose();
                            }
                        }
                    }
                    catch (System.Exception e) { Debug.LogWarning($"[LR2] B136 laser mount: cannot read {r.name}: {e.Message}"); }
                }
                float reach = float.NegativeInfinity;
                int inSlice = 0;
                float usedBack = pistol ? 0.06f : 0.15f;
                foreach (float back in pistol ? new[] { 0.06f, 0.08f, 0.04f } : new[] { 0.15f, 0.2f, 0.25f, 0.1f, 0.3f })
                {
                    Vector3 probe = muzzle.position - fwd * back;
                    reach = float.NegativeInfinity;
                    inSlice = 0;
                    foreach (var w in points)
                    {
                        Vector3 d = w - probe;
                        if (Mathf.Abs(Vector3.Dot(d, fwd)) > 0.03f || Mathf.Abs(Vector3.Dot(d, across)) > crossTolerance) continue;
                        reach = Mathf.Max(reach, Vector3.Dot(d, side));
                        inSlice++;
                    }
                    if (inSlice > 0 && reach < 0.12f) { usedBack = back; break; }
                }
                basePos = muzzle.position - fwd * usedBack;
                float rawReach = reach;
                bool onSurface = inSlice > 0 && reach < 0.12f;
                reach = onSurface ? Mathf.Clamp(reach, minReach, 0.06f) : 0.02f;
                Vector3 surface = basePos + side * reach;

                // body sizes in the device frame: x across the gun (away from it), y the other cross axis, z along the bore
                float thick = pistol ? 0.022f : 0.028f, wide = pistol ? 0.030f : 0.034f, len = pistol ? 0.046f : 0.072f;
                dev = new GameObject("LR2_LaserDevice");
                dev.transform.SetPositionAndRotation(surface + side * (thick * 0.5f + 0.004f), Quaternion.LookRotation(fwd, pistol ? right : up));
                var polymer = AssetDatabase.LoadAssetAtPath<Material>("Assets/LR2/Materials/Weapons/M_AK12_Polymer.mat")
                              ?? SaveMaterial(NewLit(new Color(0.12f, 0.12f, 0.12f), 0.3f, 0.05f), "Assets/LR2/Materials/Weapons/M_LaserDevice.mat");
                var lensMat = SaveMaterial(NewEmissive(new Color(0.04f, 0.05f, 0.05f), new Color(0.6f, 0.1f, 0.05f), 30f), "Assets/LR2/Materials/Weapons/M_LaserLens.mat");
                GameObject Part(PrimitiveType type, string name, Vector3 localPos, Vector3 size, Quaternion localRot, Material mat)
                {
                    var g = GameObject.CreatePrimitive(type);
                    Object.DestroyImmediate(g.GetComponent<Collider>());
                    g.name = name;
                    g.transform.SetParent(dev.transform, false);
                    g.transform.localPosition = localPos;
                    g.transform.localRotation = localRot;
                    g.transform.localScale = size;
                    g.GetComponent<Renderer>().sharedMaterial = mat;
                    return g;
                }
                // device frame: +x points away from the gun in both cases (rifle: LookRotation(fwd, up) -> x = right;
                // pistol: LookRotation(fwd, right) -> x = down), y runs across the gun, z along the bore
                Part(PrimitiveType.Cube, "Body", Vector3.zero, new Vector3(thick, wide, len), Quaternion.identity, polymer);
                Part(PrimitiveType.Cube, "Clamp", new Vector3(-(thick * 0.5f + 0.002f), 0f, -len * 0.1f), new Vector3(0.006f, wide * 0.55f, len * 0.5f),
                     Quaternion.identity, polymer);
                Part(PrimitiveType.Cylinder, "Lens", new Vector3(0f, 0f, len * 0.5f + 0.003f), new Vector3(0.012f, 0.003f, 0.012f), Quaternion.Euler(90f, 0f, 0f), lensMat);
                Part(PrimitiveType.Cube, "Switch", new Vector3(0f, wide * 0.5f + 0.003f, -len * 0.3f), new Vector3(0.01f, 0.006f, 0.014f), Quaternion.identity, polymer);
                var emitter = new GameObject("LaserEmitter").transform;
                emitter.SetParent(dev.transform, false);
                emitter.localPosition = new Vector3(0f, 0f, len * 0.5f + 0.006f);
                emitter.rotation = Quaternion.LookRotation(fwd, up);           // parallel to the bore
                dev.transform.SetParent(gunGo.transform, true);
                SetLayerRecursive(dev, gunGo.layer);
                var gso = new SerializedObject(gun);
                gso.FindProperty("laserEmitter").objectReferenceValue = emitter;
                gso.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log($"[LR2] B136 laser device on {gunGo.name}: {(onSurface ? $"on the surface ({inSlice} vertices, raw {rawReach * 100f:0.0} cm -> {reach * 100f:0.0} cm out)" : "fallback offset")}, " +
                          $"{Vector3.Distance(emitter.position, muzzle.position) * 100f:0} cm from the muzzle");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[LR2] B136 laser device failed on " + gunGo.name + ": " + e.Message);
                if (dev != null) Object.DestroyImmediate(dev);
            }
        }

        private static GameObject BuildKnife()
        {
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(DemoDir + "/Prefabs/Knife/Knife.prefab");
            if (src == null) { Debug.LogError("[LR2] B136: Kinemation knife prefab missing"); return null; }
            if (_lr2GenericController == null) _lr2GenericController = BuildLR2GenericController();
            var go = (GameObject)PrefabUtility.InstantiatePrefab(src);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = "LR2_Knife";
            var demoType = FindType("Demo.Scripts.Runtime.Item.MeleeWeapon");
            var demo = demoType != null ? go.GetComponent(demoType) : null;
            var melee = go.AddComponent<LR2Melee>();
            if (demo != null)
            {
                CopyMatchingFields(demo, melee);
                Object.DestroyImmediate(demo);
            }
            // B136: the demo knife has no aim point, and Kinemation's ADS job reads it when the profile links (see LR2Item)
            var kEntity = go.GetComponent<FPSAnimatorEntity>();
            if (kEntity != null && kEntity.defaultAimPoint == null)
            {
                var ap = new GameObject("AimPoint").transform;
                ap.SetParent(go.transform, false);
                ap.localPosition = new Vector3(0f, 0.0005f, -0.001f); // the knife root is scaled x100 (centimetre mesh)
                kEntity.defaultAimPoint = ap;
                EditorUtility.SetDirty(kEntity);
            }
            var so = new SerializedObject(melee);
            var ov = so.FindProperty("overrideController");
            if (ov != null) ov.objectReferenceValue = LR2Override(ov.objectReferenceValue as RuntimeAnimatorController, _lr2GenericController);
            so.ApplyModifiedPropertiesWithoutUndo();
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.updateWhenOffscreen = true;
            return SavePrefab(go, PrefabDir + "/Weapons/LR2_Knife.prefab");
        }

        private static GameObject BuildTorch()
        {
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(DemoDir + "/Prefabs/Torch/Torch_Scriptable.prefab");
            var knife = AssetDatabase.LoadAssetAtPath<GameObject>(DemoDir + "/Prefabs/Knife/Knife.prefab");
            if (src == null || knife == null) { Debug.LogError("[LR2] B136: Kinemation torch or knife prefab missing"); return null; }
            if (_lr2GenericController == null) _lr2GenericController = BuildLR2GenericController();
            var go = (GameObject)PrefabUtility.InstantiatePrefab(src);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = "LR2_Torch";
            var demoType = FindType("Demo.Scripts.Runtime.Item.Weapon");
            var demo = demoType != null ? go.GetComponent(demoType) : null;
            if (demo != null) Object.DestroyImmediate(demo);
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);

            var torch = go.AddComponent<LR2Torch>();
            var entity = go.GetComponent<FPSAnimatorEntity>() ?? go.AddComponent<FPSAnimatorEntity>();
            // the knife's own profile: the demo's torch profile is missing and its torch clip never moves the weapon bones
            entity.animatorProfile = AssetDatabase.LoadAssetAtPath<FPSAnimatorProfile>(DemoDir + "/AnimatorProfiles/AnimatorProfile_Knife.asset");
            if (entity.defaultAimPoint == null) entity.defaultAimPoint = go.transform.Find("AimPoint");
            EditorUtility.SetDirty(entity);

            // the knife's one-handed setup: its (unarmed) override controller and the equip / unequip motions
            var kMelee = knife.GetComponent(FindType("Demo.Scripts.Runtime.Item.MeleeWeapon"));
            var kso = kMelee != null ? new SerializedObject(kMelee) : null;
            var so = new SerializedObject(torch);
            void Ref(string n, Object v) { var p = so.FindProperty(n); if (p != null) p.objectReferenceValue = v; }
            if (kso != null)
            {
                Ref("overrideController", LR2Override(kso.FindProperty("overrideController").objectReferenceValue as RuntimeAnimatorController, _lr2GenericController));
                Ref("equipMotion", kso.FindProperty("equipMotion").objectReferenceValue);
                Ref("unEquipMotion", kso.FindProperty("unEquipMotion").objectReferenceValue);
            }

            // flame head: warm flickering light, ember material, a small fire
            var head = go.transform.Find("Cube (1)");
            Vector3 tip = head != null ? head.localPosition + Vector3.up * 0.09f : new Vector3(0f, 0.26f, 0f);
            var lightGo = new GameObject("TorchLight");
            lightGo.transform.SetParent(go.transform, false);
            lightGo.transform.localPosition = tip + Vector3.up * 0.06f;
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.6f, 0.3f);
            light.range = 11f;
            light.lightUnit = LightUnit.Lumen;
            light.intensity = 1600f;
            light.shadows = LightShadows.Soft;
            var hdl = lightGo.AddComponent<HDAdditionalLightData>();
            hdl.SetShadowResolutionOverride(true);
            hdl.SetShadowResolution(256);
            Ref("torchLight", light);
            if (head != null)
            {
                var ember = SaveMaterial(NewEmissive(new Color(0.25f, 0.08f, 0.02f), new Color(1f, 0.42f, 0.12f), 9000f), "Assets/LR2/Materials/Weapons/M_TorchEmber.mat");
                var hr = head.GetComponent<Renderer>();
                if (hr != null) { hr.sharedMaterial = ember; Ref("emissiveHead", hr); }
            }
            var lib = AssetDatabase.LoadAssetAtPath<LR2FxLibrary>("Assets/LR2/Resources/LR2/FXLibrary.asset");
            if (lib != null && lib.fire != null)
            {
                var fireGo = new GameObject("Flame");
                fireGo.transform.SetParent(go.transform, false);
                fireGo.transform.localPosition = tip;
                var ps = fireGo.AddComponent<ParticleSystem>();
                var main = ps.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.28f, 0.5f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.15f, 0.45f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.07f, 0.15f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 48;
                main.gravityModifier = -0.15f;
                var em = ps.emission;
                em.rateOverTime = 34f;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.025f;
                var col = ps.colorOverLifetime;
                col.enabled = true;
                var grad = new Gradient();
                grad.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.85f, 0.55f), 0f), new GradientColorKey(new Color(1f, 0.4f, 0.12f), 1f) },
                             new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.9f, 0.15f), new GradientAlphaKey(0f, 1f) });
                col.color = grad;
                var sol = ps.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.35f));
                LR2FxLibrary.ApplyFlipbook(ps, lib.fireTiles, 1f);
                var pr = fireGo.GetComponent<ParticleSystemRenderer>();
                pr.sharedMaterial = lib.fire;
                pr.renderMode = ParticleSystemRenderMode.Billboard;
                pr.shadowCastingMode = ShadowCastingMode.Off;
                Ref("flame", ps);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            return SavePrefab(go, PrefabDir + "/Weapons/LR2_Torch.prefab");
        }

        private static GameObject BuildGrenadePrefab()
        {
            var go = new GameObject("LR2_Grenade");
            go.layer = LR2Layers.Debris;
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.name = "Body";
            body.transform.SetParent(go.transform, false);
            body.transform.localScale = new Vector3(0.062f, 0.046f, 0.062f);
            var spoon = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(spoon.GetComponent<Collider>());
            spoon.name = "Spoon";
            spoon.transform.SetParent(go.transform, false);
            spoon.transform.localPosition = new Vector3(0.026f, 0.02f, 0f);
            spoon.transform.localScale = new Vector3(0.008f, 0.07f, 0.016f);
            var olive = SaveMaterial(NewLit(new Color(0.22f, 0.25f, 0.16f), 0.35f, 0.1f), "Assets/LR2/Materials/Weapons/M_Grenade.mat");
            var steel = SaveMaterial(NewLit(new Color(0.55f, 0.56f, 0.57f), 0.6f, 0.9f), "Assets/LR2/Materials/Weapons/M_GrenadeSpoon.mat");
            body.GetComponent<Renderer>().sharedMaterial = olive;
            spoon.GetComponent<Renderer>().sharedMaterial = steel;
            SetLayerRecursive(go, LR2Layers.Debris);
            var col = go.AddComponent<SphereCollider>();
            col.radius = 0.038f;
            var pm = new PhysicsMaterial("LR2_Grenade") { bounciness = 0.32f, dynamicFriction = 0.55f, staticFriction = 0.6f, bounceCombine = PhysicsMaterialCombine.Average };
            col.sharedMaterial = SaveAsset(pm, "Assets/LR2/Data/Weapons/PM_Grenade.asset");
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.4f;
            rb.linearDamping = 0.05f;
            rb.angularDamping = 0.6f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            go.AddComponent<LR2Grenade>();
            return SavePrefab(go, PrefabDir + "/Weapons/LR2_Grenade.prefab");
        }

        // ------------------------------------------------------------------ team colours
        /// <summary>B136: the robot tinted per team (navy allies, crimson enemies) until a better-looking soldier replaces it.</summary>
        private static SoldierVisuals.TeamMaterialSwap[] MakeRobotTeamMaterials()
        {
            const string dir = "Assets/LR2/Materials/Characters";
            var list = new List<SoldierVisuals.TeamMaterialSwap>();
            foreach (var part in new[] { "Body", "Arms", "Legs" })
            {
                var src = AssetDatabase.LoadAssetAtPath<Material>($"{RobotMatDir}/M_Armature_{part}.mat");
                if (src == null) continue;
                if (src.shader == null || !src.shader.name.StartsWith("HDRP/")) ConvertDemoMaterials();
                Material Team(string team, Color tint)
                {
                    var m = new Material(src);
                    m.SetColor("_BaseColor", tint);
                    HDMaterial.ValidateMaterial(m);
                    return SaveMaterial(m, $"{dir}/M_LR2Robot_{part}_{team}.mat");
                }
                list.Add(new SoldierVisuals.TeamMaterialSwap
                {
                    key = part.ToLowerInvariant(),
                    friendly = Team("Allied", new Color(0.42f, 0.55f, 0.82f)),
                    enemy = Team("Enemy", new Color(0.84f, 0.34f, 0.26f))
                });
            }
            return list.ToArray();
        }

        // ------------------------------------------------------------------ soldiers
        private static void BuildRobotSoldiers()
        {
            ConfigureUpscaling();
            _lr2GenericController = BuildLR2GenericController();
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(RobotPrefab);
            if (src == null)
            {
                Debug.LogError("[LR2] B136: Kinemation's FPSGenericPlayer is missing: " + RobotPrefab);
                return;
            }
            var demoCtrlType = FindType("Demo.Scripts.Runtime.Character.FPSController");
            var demoMoveType = FindType("Demo.Scripts.Runtime.Character.FPSMovement");
            var probe = demoCtrlType != null ? src.GetComponent(demoCtrlType) : null;
            var probeMove = demoMoveType != null ? src.GetComponent(demoMoveType) : null;
            var srcSettings = probe != null ? new SerializedObject(probe).FindProperty("settings").objectReferenceValue as ScriptableObject : null;
            var srcMove = probeMove != null ? new SerializedObject(probeMove).FindProperty("movementSettings").objectReferenceValue as ScriptableObject : null;

            ScriptableObject soldierSettings = null, moveSettings = null;
            if (srcSettings != null)
            {
                soldierSettings = SaveAsset(Object.Instantiate(srcSettings), DataDir + "/Characters/LR2_SoldierSettings.asset");
                var sso = new SerializedObject(soldierSettings);
                var list = sso.FindProperty("weaponPrefabs");
                list.ClearArray();
                string[] order = { "Mk18", "Mk23", "AK12", "AK74N", "FAL", "Knife", "Torch" }; // [5] knife, [6] torch (SoldierController)
                int inserted = 0;
                foreach (var id in order)
                {
                    var w = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabDir}/Weapons/LR2_{id}.prefab");
                    if (w == null) { Debug.LogWarning("[LR2] B136: missing weapon prefab LR2_" + id); continue; }
                    list.InsertArrayElementAtIndex(inserted);
                    list.GetArrayElementAtIndex(inserted).objectReferenceValue = w;
                    inserted++;
                }
                sso.FindProperty("equipDelay").floatValue = 0.3f; // LR2's quick swap
                sso.ApplyModifiedPropertiesWithoutUndo();
            }
            if (srcMove != null)
            {
                moveSettings = SaveAsset(Object.Instantiate(srcMove), DataDir + "/Characters/LR2_MovementSettings.asset");
                var mso = new SerializedObject(moveSettings);
                void Gait(string n, float v, float smooth) { var p = mso.FindProperty(n); if (p == null) return; p.FindPropertyRelative("velocity").floatValue = v; if (smooth > 0f) p.FindPropertyRelative("velocitySmoothing").floatValue = smooth; }
                Gait("walking", 3.5f, 16f);
                Gait("sprinting", 6.1f, 12f);
                Gait("crouching", 2.0f, 16f);
                Gait("prone", 1.35f, 12f);
                void F(string n, float v) { var p = mso.FindProperty(n); if (p != null) p.floatValue = v; }
                F("jumpHeight", 5.2f);
                F("gravity", 17f);
                F("slideSpeed", 8.5f);
                F("proneTransitionDuration", 0.32f);
                var gm = mso.FindProperty("groundMask");
                if (gm != null) gm.intValue = LR2Layers.EnvironmentMask;
                mso.ApplyModifiedPropertiesWithoutUndo();
            }

            var markerMat = AssetDatabase.LoadAssetAtPath<Material>(FxDir + "/Materials/M_FriendlyMarker.mat");
            var regular = AssetDatabase.LoadAssetAtPath<BotDifficulty>(DataDir + "/AI/BotDifficulty_Regular.asset");
            var teamSwaps = MakeRobotTeamMaterials();
            var grenade = BuildGrenadePrefab();

            foreach (bool player in new[] { true, false })
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(src);
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                go.name = player ? "LR2_Soldier_Player" : "LR2_Soldier_Bot";
                go.layer = LR2Layers.Character;

                var pi = FindType("UnityEngine.InputSystem.PlayerInput");
                if (pi != null) foreach (var c in go.GetComponentsInChildren(pi, true)) Object.DestroyImmediate(c);
                var demoCtrl = demoCtrlType != null ? go.GetComponent(demoCtrlType) : null;
                var demoMove = demoMoveType != null ? go.GetComponent(demoMoveType) : null;
                if (demoCtrl != null) Object.DestroyImmediate(demoCtrl);
                var move = go.AddComponent<SoldierMovement>();
                if (demoMove != null)
                {
                    CopyMatchingFields(demoMove, move);
                    Object.DestroyImmediate(demoMove);
                }
                if (moveSettings != null) SetField(move, "movementSettings", moveSettings);

                var cc = go.GetComponent<CharacterController>();
                cc.height = 1.8f;
                cc.radius = 0.38f;
                cc.center = new Vector3(0f, 0.92f, 0f);
                cc.stepOffset = 0.42f;
                cc.slopeLimit = 50f;
                cc.skinWidth = 0.04f;

                var anim = go.GetComponent<Animator>();
                anim.runtimeAnimatorController = _lr2GenericController != null ? _lr2GenericController : anim.runtimeAnimatorController;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                var fpsAnim = go.GetComponent<FPSAnimator>();
                if (fpsAnim != null)
                {
                    // linked by the equipped item (LR2Firearm.OnEquip), as in Kinemation's demo
                    var faso = new SerializedObject(fpsAnim);
                    var ap = faso.FindProperty("animatorProfile");
                    if (ap != null) ap.objectReferenceValue = null;
                    faso.ApplyModifiedPropertiesWithoutUndo();
                }
                if (go.GetComponent<WeaponAntiClip>() == null) go.AddComponent<WeaponAntiClip>();

                var combatant = go.AddComponent<Combatant>();
                var health = go.AddComponent<Health>();
                health.MaxHealth = 100f;
                health.MaxArmor = 50f;
                var ctrl = go.AddComponent<SoldierController>();
                if (soldierSettings != null) SetField(ctrl, "settings", soldierSettings);
                if (grenade != null) SetField(ctrl, "grenadePrefab", grenade);

                // generic rig: bake the humanoid bone lookup LR2's systems use (hitboxes, flinch, aim points, killcam)
                var boneMap = go.AddComponent<LR2BoneMap>();
                int bones = boneMap.Bake(anim);
                var ragdoll = go.AddComponent<SoldierRagdoll>();
                var rbones = BuildHitboxRagdoll(anim, health);
                ragdoll.SetBones(rbones);
                var chest = LR2Bones.Get(anim, HumanBodyBones.Chest) ?? LR2Bones.Get(anim, HumanBodyBones.Spine);
                var head = LR2Bones.Get(anim, HumanBodyBones.Head);
                go.AddComponent<HitReactions>().Configure(chest, null, false);
                go.AddComponent<SoldierFoley>();
                var visuals = go.AddComponent<SoldierVisuals>();
                visuals.FriendlyMarkerMaterial = markerMat;
                visuals.SetTeamSwaps(teamSwaps);
                visuals.ApplyMaterialsNow(player);

                var cam = go.GetComponentInChildren<Camera>(true);
                combatant.SetAimPoints(player && cam != null ? cam.transform : head, head, chest);

                // first person: the TFP head mesh is shadow-only for the local player
                var hidden = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.name.ToLowerInvariant().Contains("head")).Cast<Renderer>().ToArray();
                var cso = new SerializedObject(ctrl);
                var hp = cso.FindProperty("firstPersonHidden");
                hp.arraySize = hidden.Length;
                for (int i = 0; i < hidden.Length; i++) hp.GetArrayElementAtIndex(i).objectReferenceValue = hidden[i];
                cso.FindProperty("eyeSocket").objectReferenceValue = head;
                cso.ApplyModifiedPropertiesWithoutUndo();
                foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.updateWhenOffscreen = true;

                if (cam != null)
                {
                    var camCtrl = cam.GetComponent<FPSCameraController>();
                    if (camCtrl != null)
                    {
                        var ccso = new SerializedObject(camCtrl);
                        var fovSpd = ccso.FindProperty("fovSpeed");
                        if (fovSpd != null) fovSpd.floatValue = 8f;
                        var easeFunc = ccso.FindProperty("fovEaseMode.easeFunc");
                        if (easeFunc != null) easeFunc.enumValueIndex = 1;
                        ccso.ApplyModifiedPropertiesWithoutUndo();
                    }
                    if (player && cam.GetComponent<BodycamRig>() == null) cam.gameObject.AddComponent<BodycamRig>();
                    var hd = cam.GetComponent<HDAdditionalCameraData>() ?? cam.gameObject.AddComponent<HDAdditionalCameraData>();
                    hd.allowDynamicResolution = true;
                    hd.allowDeepLearningSuperSampling = true;
                    hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                    hd.TAAQuality = HDAdditionalCameraData.TAAQualityLevel.High;
                    hd.taaSharpenStrength = 0.65f;
                    hd.stopNaNs = true;
                    hd.dithering = true;
                    cam.fieldOfView = 90f;
                    cam.nearClipPlane = 0.03f;
                    if (player)
                    {
                        cam.gameObject.tag = "MainCamera";
                        if (cam.GetComponent<AudioListener>() == null) cam.gameObject.AddComponent<AudioListener>();
                        cam.gameObject.SetActive(true);
                    }
                    else
                    {
                        var al = cam.GetComponent<AudioListener>();
                        if (al != null) Object.DestroyImmediate(al);
                        cam.gameObject.SetActive(false);
                    }
                }

                if (player)
                {
                    go.AddComponent<PlayerInputSource>();
                    combatant.IsLocalPlayer = true;
                }
                else
                {
                    var agent = go.AddComponent<NavMeshAgent>();
                    agent.radius = 0.35f;
                    agent.height = 1.8f;
                    agent.speed = 5f;
                    agent.acceleration = 40f;
                    agent.angularSpeed = 720f;
                    agent.stoppingDistance = 0.5f;
                    agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;
                    var bot = go.AddComponent<BotController>();
                    bot.Difficulty = regular;
                    combatant.IsBot = true;
                }
                Debug.Log($"[LR2] B136 {go.name}: Kinemation robot, {bones} humanoid bones resolved, {rbones.Count} hitboxes, " +
                          $"{hidden.Length} first-person hidden renderer(s), controller {(_lr2GenericController != null ? _lr2GenericController.name : "OG")}");
                SavePrefab(go, $"{PrefabDir}/Characters/{(player ? "LR2_Soldier_Player" : "LR2_Soldier_Bot")}.prefab");
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[LR2] Soldier prefabs built (B136: Kinemation's original robot with its own guns, knife, torch and grenades).");
        }
    }
}
