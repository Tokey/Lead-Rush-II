using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Demo.Scripts.Runtime.AttachmentSystem;
using KINEMATION.FPSAnimationFramework.Runtime.Camera;
using KINEMATION.FPSAnimationFramework.Runtime.Core;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.AttachHandLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.CollisionLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.PoseSamplerLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.SwayLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.TurnLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.ViewLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Playables;
using KINEMATION.Shared.KAnimationCore.Runtime.Core;
using KINEMATION.Shared.KAnimationCore.Runtime.Rig;
using LR2.AI;
using LR2.Audio;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using LR2.FX;
using LR2.Game;
using LR2.UI;
using LR2.Weapons;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using static LR2.EditorTools.LR2EditorUtil;

namespace LR2.EditorTools
{
    /// <summary>Builds all LR2 content assets: materials, FX, audio, weapons, soldiers, data presets, gameplay prefabs.</summary>
    public static partial class LR2ContentBuilder
    {
        private const string DataDir = "Assets/LR2/Data";
        private const string PrefabDir = "Assets/LR2/Prefabs";
        private const string FxDir = "Assets/LR2/FX";
        private const string ResDir = "Assets/LR2/Resources/LR2";
        private const string DemoDir = "Assets/KinemationDemo";
        private const string SwatPrefab = DemoDir + "/Tutorials/Swat.prefab";
        private const string SwatAk12 = DemoDir + "/Tutorials/Tutorial #1 - Basic Setup/Swat_FPSAnimator/AK12.prefab";
        // Kinemation's humanoid FPS rig drives every animation (grip, ADS, reload, equip); the SWAT body is a retargeted visual.
        private const string DriverPrefab = DemoDir + "/Prefabs/Humanoid/FPSHumanoidPlayer.prefab";
        private const string SwatModel = DemoDir + "/Meshes/Character/MixamoSwat/Swat.fbx";
        private static readonly bool UseKinemationDriver = true;
        private const string FullAk12 = DemoDir + "/Prefabs/AK12/AK12_Scriptable.prefab";
        private const string HumanoidPrefab = DemoDir + "/Prefabs/Humanoid/FPSHumanoidPlayer.prefab";
        private const string HumanoidAk12 = DemoDir + "/Prefabs/Humanoid/AK12_Scriptable_Humanoid.prefab";
        private const string HumanoidReloadClip = DemoDir + "/Animations/Weapons/AK12/C_AK12_ReloadEmpty.anim";
        private const string SwatRigPath = DemoDir + "/Tutorials/Tutorial #1 - Basic Setup/Swat_FPSAnimator/Rig_Swat.asset";
        private const string SwatProfilePath = DemoDir + "/Tutorials/Tutorial #1 - Basic Setup/Swat_FPSAnimator/AnimatorProfile_Rig_Swat.asset";
        private const string SwatMaskPath = DemoDir + "/Tutorials/Tutorial #1 - Basic Setup/Swat_FPSAnimator/Swat_UpperBody.mask";
        private const string WeaponAnimControllerPath = DemoDir + "/Meshes/Weapons/AK12/AK12_Animator.overrideController";
        private const string DemoReloadAssetPath = DemoDir + "/Animations/Weapons/AK12/AA_AK12_Reload_Empty.asset";

        // =====================================================================================================
        // 1. Kinemation demo materials: URP / Built-in -> HDRP Lit
        // =====================================================================================================
        public static void ConvertDemoMaterials()
        {
            var lit = HdrpLitShader;
            int converted = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { DemoDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;
                string sn = mat.shader.name;
                bool needs = sn.StartsWith("Universal Render Pipeline") || sn == "Hidden/InternalErrorShader" || sn == "Standard" ||
                             sn == "Standard (Specular setup)" || sn.StartsWith("Shader Graphs/") && !sn.Contains("HDRP");
                if (!needs || sn.StartsWith("HDRP/")) continue;
                if (IsSightMaterial(mat)) continue; // B136: reticles and scope lenses get HDRP Unlit (ConvertDemoSightMaterials)

                var tex = new Dictionary<string, Texture>();
                var floats = new Dictionary<string, float>();
                var colors = new Dictionary<string, Color>();
                var so = new SerializedObject(mat);
                ReadSaved(so.FindProperty("m_SavedProperties.m_TexEnvs"), (n, p) => tex[n] = p.FindPropertyRelative("m_Texture").objectReferenceValue as Texture);
                ReadSaved(so.FindProperty("m_SavedProperties.m_Floats"), (n, p) => floats[n] = p.floatValue);
                ReadSaved(so.FindProperty("m_SavedProperties.m_Colors"), (n, p) => colors[n] = p.colorValue);
                bool emissionKeyword = mat.shaderKeywords.Contains("_EMISSION");

                mat.shader = lit;
                Texture baseMap = Get(tex, "_BaseMap") ?? Get(tex, "_MainTex");
                Color baseColor = colors.TryGetValue("_BaseColor", out var bc) ? bc : colors.TryGetValue("_Color", out var cc) ? cc : Color.white;
                mat.SetTexture("_BaseColorMap", baseMap);
                mat.SetColor("_BaseColor", baseColor);
                var normal = Get(tex, "_BumpMap");
                if (normal != null)
                {
                    mat.SetTexture("_NormalMap", normal);
                    mat.SetFloat("_NormalScale", floats.TryGetValue("_BumpScale", out var bs) ? bs : 1f);
                }

                bool specularWorkflow = floats.TryGetValue("_WorkflowMode", out var wm) && wm < 0.5f || sn.Contains("Specular");
                float smooth = floats.TryGetValue("_Smoothness", out var sm) ? sm : floats.TryGetValue("_Glossiness", out var gl) ? gl : 0.45f;
                float metal = specularWorkflow ? 0f : floats.TryGetValue("_Metallic", out var me) ? me : 0f;
                var metalGloss = specularWorkflow ? null : Get(tex, "_MetallicGlossMap");
                if (metalGloss != null)
                {
                    mat.SetTexture("_MaskMap", metalGloss);
                    mat.SetFloat("_MetallicRemapMin", 0f);
                    mat.SetFloat("_MetallicRemapMax", 1f);
                    mat.SetFloat("_AORemapMin", 1f);
                    mat.SetFloat("_AORemapMax", 1f);
                    mat.SetFloat("_SmoothnessRemapMin", 0f);
                    mat.SetFloat("_SmoothnessRemapMax", Mathf.Clamp01(smooth));
                }
                else
                {
                    mat.SetFloat("_Metallic", metal);
                    mat.SetFloat("_Smoothness", specularWorkflow ? Mathf.Min(smooth, 0.38f) : smooth);
                }

                var emissionMap = Get(tex, "_EmissionMap");
                Color emission = colors.TryGetValue("_EmissionColor", out var ec) ? ec : Color.black;
                if (emissionKeyword && (emissionMap != null || emission.maxColorComponent > 0.01f))
                {
                    if (emissionMap != null) mat.SetTexture("_EmissiveColorMap", emissionMap);
                    HDMaterial.SetUseEmissiveIntensity(mat, true);
                    HDMaterial.SetEmissiveColor(mat, emission);
                    HDMaterial.SetEmissiveIntensity(mat, 400f, EmissiveIntensityUnit.Nits);
                }

                if (floats.TryGetValue("_Surface", out var surf) && surf > 0.5f) HDMaterial.SetSurfaceType(mat, true);
                if (floats.TryGetValue("_AlphaClip", out var ac) && ac > 0.5f)
                {
                    HDMaterial.SetAlphaClipping(mat, true);
                    HDMaterial.SetAlphaCutoff(mat, floats.TryGetValue("_Cutoff", out var cut) ? cut : 0.5f);
                }
                if (floats.TryGetValue("_Cull", out var cull) && cull < 0.5f) mat.SetFloat("_DoubleSidedEnable", 1f);

                HDMaterial.ValidateMaterial(mat);
                EditorUtility.SetDirty(mat);
                converted++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[LR2] Converted {converted} Kinemation demo materials to HDRP/Lit.");
            ConvertDemoSightMaterials();
        }

        private static Texture Get(Dictionary<string, Texture> d, string k) => d.TryGetValue(k, out var t) ? t : null;

        private static void ReadSaved(SerializedProperty array, System.Action<string, SerializedProperty> onEntry)
        {
            if (array == null || !array.isArray) return;
            for (int i = 0; i < array.arraySize; i++)
            {
                var e = array.GetArrayElementAtIndex(i);
                var first = e.FindPropertyRelative("first");
                var second = e.FindPropertyRelative("second");
                if (first != null && second != null) onEntry(first.stringValue, second);
            }
        }

        // =====================================================================================================
        // 2. FX: textures, materials, particle prefabs, surface library
        // =====================================================================================================
        public static void BuildFx()
        {
            string texDir = FxDir + "/Textures", matDir = FxDir + "/Materials", pfDir = PrefabDir + "/FX";
            var softDot = SaveTexture(MakeSoftDot(64), texDir + "/T_SoftDot.png");
            var puff = SaveTexture(MakePuff(128, 7), texDir + "/T_SmokePuff.png");
            var tracerTex = SaveTexture(MakeTracer(128, 16), texDir + "/T_Tracer.png");
            var hole = SaveTexture(MakeBulletHole(128, 3), texDir + "/T_BulletHole.png");
            var blood = SaveTexture(MakeBloodSplat(256, 11), texDir + "/T_BloodSplat.png");
            var chevron = SaveTexture(MakeChevron(64), texDir + "/T_FriendlyMarker.png");
            var flashTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/UnityTechnologies/ParticlePack/EffectExamples/Weapon Effects/Textures/MuzzleFlashFront.tif") ?? softDot;
            var sparkTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/UnityTechnologies/ParticlePack/EffectExamples/Misc Effects/Textures/SparkParticle.tif") ?? softDot;

            var mFlash = SaveMaterial(NewParticle(flashTex, new Color(1f, 0.78f, 0.5f), 2.4f, true), matDir + "/M_FX_MuzzleFlash.mat");
            var mSpark = SaveMaterial(NewParticle(sparkTex, new Color(1f, 0.72f, 0.38f), 3.2f, true), matDir + "/M_FX_Spark.mat");
            var mGlow = SaveMaterial(NewParticle(softDot, new Color(1f, 0.6f, 0.25f), 5f, true), matDir + "/M_FX_Glow.mat");
            var mDust = SaveMaterial(NewParticle(puff, new Color(0.62f, 0.6f, 0.57f, 0.55f), 1f, false), matDir + "/M_FX_Dust.mat");
            var mSmoke = SaveMaterial(NewParticle(puff, new Color(0.3f, 0.3f, 0.3f, 0.6f), 1f, false), matDir + "/M_FX_Smoke.mat");
            var mBlood = SaveMaterial(NewParticle(puff, new Color(0.75f, 0.03f, 0.03f, 0.94f), 1f, false), matDir + "/M_FX_Blood.mat");
            var mBloodDrop = SaveMaterial(NewParticle(softDot, new Color(0.45f, 0.01f, 0.01f, 1f), 1f, false), matDir + "/M_FX_BloodDrop.mat");
            var mTracer = SaveMaterial(NewParticle(tracerTex, Color.white, 180f, true), matDir + "/M_FX_Tracer.mat");
            mTracer.renderQueue = 3100;
            // Codex R23: Ensure T_Tracer.png stays CPU-readable so QA verifies real head-bright UV alpha pixels.
            if (AssetImporter.GetAtPath(texDir + "/T_Tracer.png") is TextureImporter tracerImp && !tracerImp.isReadable)
            {
                tracerImp.isReadable = true;
                tracerImp.SaveAndReimport();
            }
            var mMarker = SaveMaterial(NewParticle(chevron, TeamUtil.FriendlyColor, 2.5f, false), matDir + "/M_FriendlyMarker.mat");
            var mBrass = SaveMaterial(NewLit(new Color(0.78f, 0.58f, 0.25f), 0.82f, 1f), matDir + "/M_Brass.mat");
            var dHole = SaveMaterial(NewDecal(hole, Color.white), matDir + "/M_Decal_BulletHole.mat");
            var dHoleMetal = SaveMaterial(NewDecal(hole, new Color(0.55f, 0.55f, 0.6f)), matDir + "/M_Decal_BulletHole_Metal.mat");
            var dBlood = SaveMaterial(NewDecal(blood, new Color(0.6f, 0.05f, 0.04f)), matDir + "/M_Decal_Blood.mat");
            BuildRuntimeFxLibrary(sparkTex, softDot, puff);

            // Impacts per surface
            GameObject Impact(string name, Color dust, float dustSize, bool sparks, Color chunks, float chunkGravity)
            {
                var root = new GameObject(name);
                AddPS(root, "Dust", mDust, 0.5f, 1.2f, 0.4f, 1.8f, dustSize * 0.5f, dustSize, 6, dust, dust * 0.85f, -0.02f, 35f, false, 2.4f);
                AddPS(root, "Chunks", mDust, 0.3f, 0.7f, 2f, 5f, 0.02f, 0.05f, 9, chunks, chunks, chunkGravity, 30f, true, 0.6f);
                if (sparks) AddPS(root, "Sparks", mSpark, 0.08f, 0.28f, 4f, 11f, 0.015f, 0.04f, 14, Color.white, new Color(1f, 0.8f, 0.5f), 0.6f, 40f, true, 0.5f);
                return SavePrefab(root, pfDir + "/" + name + ".prefab");
            }

            var impConcrete = Impact("FX_Impact_Concrete", new Color(0.68f, 0.66f, 0.62f, 0.65f), 0.38f, true, new Color(0.35f, 0.34f, 0.32f, 1f), 1.4f);
            var impMetal = Impact("FX_Impact_Metal", new Color(0.42f, 0.42f, 0.45f, 0.38f), 0.24f, true, new Color(0.2f, 0.2f, 0.2f, 1f), 1.5f);
            var impWood = Impact("FX_Impact_Wood", new Color(0.55f, 0.45f, 0.33f, 0.56f), 0.32f, false, new Color(0.45f, 0.32f, 0.18f, 1f), 1.2f);
            var impDirt = Impact("FX_Impact_Dirt", new Color(0.5f, 0.43f, 0.33f, 0.74f), 0.54f, false, new Color(0.3f, 0.24f, 0.17f, 1f), 1.6f);
            var impGlass = Impact("FX_Impact_Glass", new Color(0.85f, 0.88f, 0.9f, 0.35f), 0.18f, true, new Color(0.8f, 0.9f, 0.95f, 1f), 1.4f);
            var impWater = Impact("FX_Impact_Water", new Color(0.85f, 0.9f, 0.95f, 0.68f), 0.45f, false, new Color(0.9f, 0.95f, 1f, 0.8f), 1.2f);

            // Blood (vivid arterial crimson mist cloud + high-velocity splatter droplets)
            GameObject Blood(string name, float scale)
            {
                var root = new GameObject(name);
                AddPS(root, "Mist", mBlood, 0.32f, 0.68f, 0.5f, 2.2f, 0.22f * scale, 0.52f * scale, Mathf.RoundToInt(18 * scale),
                    new Color(0.82f, 0.04f, 0.04f, 0.92f), new Color(0.42f, 0.01f, 0.01f, 0.78f), 0.12f, 45f, false, 2.8f);
                AddPS(root, "Spray", mBlood, 0.18f, 0.38f, 2.2f, 5.2f, 0.12f * scale, 0.28f * scale, Mathf.RoundToInt(10 * scale),
                    new Color(0.70f, 0.02f, 0.02f, 0.90f), new Color(0.35f, 0f, 0f, 0.75f), 0.35f, 28f, false, 1.9f);
                AddPS(root, "Drops", mBloodDrop, 0.35f, 0.72f, 2.4f, 6.0f, 0.016f, 0.042f, Mathf.RoundToInt(14 * scale),
                    new Color(0.55f, 0.01f, 0.01f, 1f), new Color(0.28f, 0f, 0f, 1f), 1.15f, 32f, true, 0.85f);
                return SavePrefab(root, pfDir + "/" + name + ".prefab");
            }
            var bloodFx = Blood("FX_Blood", 1.55f);
            var bloodHead = Blood("FX_Blood_Headshot", 2.5f);

            // Muzzle flash (subtle tactical flash & localized illumination, swirling heated barrel smoke wisps)
            var flash = new GameObject("FX_MuzzleFlash");
            var f1 = AddPS(flash, "FlashFront", mFlash, 0.025f, 0.04f, 0f, 0f, 0.10f, 0.16f, 1, Color.white, new Color(1f, 0.88f, 0.68f), 0f, 0f, false, 0.85f);
            var f1Main = f1.main;
            f1Main.simulationSpace = ParticleSystemSimulationSpace.Local;
            var side = AddPS(flash, "FlashSide", mFlash, 0.025f, 0.038f, 6f, 9f, 0.045f, 0.08f, 2, Color.white, new Color(1f, 0.85f, 0.6f), 0f, 12f, true, 0.8f);
            var sideMain = side.main;
            sideMain.simulationSpace = ParticleSystemSimulationSpace.Local;
            AddPS(flash, "Smoke", mSmoke, 0.65f, 1.25f, 0.35f, 1.15f, 0.06f, 0.14f, 4, new Color(0.65f, 0.65f, 0.65f, 0.22f), new Color(0.5f, 0.5f, 0.5f, 0.14f), -0.05f, 22f, false, 4.4f);
            AddPS(flash, "Sparks", mSpark, 0.05f, 0.13f, 5f, 12f, 0.010f, 0.022f, 7, Color.white, new Color(1f, 0.75f, 0.4f), 0.2f, 16f, true, 0.35f);
            var lightGo = new GameObject("FlashLight");
            lightGo.transform.SetParent(flash.transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 0f, 0.05f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.72f, 0.45f);
            light.range = 7.5f;
            light.lightUnit = LightUnit.Lumen;
            light.intensity = 3200f;    // localized warm muzzle spill without blinding room whiteout
            light.shadows = LightShadows.None; // point-light shadows on every shot caused frame spikes
            lightGo.AddComponent<HDAdditionalLightData>();
            flash.AddComponent<MuzzleFlash>();
            var flashPrefab = SavePrefab(flash, pfDir + "/FX_MuzzleFlash.prefab");

            // Tracer
            var tracer = new GameObject("FX_Tracer");
            var lr = tracer.AddComponent<LineRenderer>();
            lr.sharedMaterial = mTracer;
            lr.textureMode = LineTextureMode.Stretch;
            lr.alignment = LineAlignment.View;
            lr.numCapVertices = 2;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.positionCount = 2;
            tracer.AddComponent<Tracer>();
            var tracerPrefab = SavePrefab(tracer, pfDir + "/FX_Tracer.prefab");

            // Shell casing (5.45 / 5.56)
            var shell = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            shell.name = "FX_Shell_Rifle";
            shell.transform.localScale = new Vector3(0.0095f, 0.0115f, 0.0095f);
            shell.GetComponent<Renderer>().sharedMaterial = mBrass;
            shell.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
            shell.layer = LR2Layers.Debris;
            var rb = shell.AddComponent<Rigidbody>();
            rb.mass = 0.012f;
            rb.linearDamping = 0.1f;
            rb.angularDamping = 0.05f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            shell.AddComponent<ShellCasing>();
            SavePrefab(shell, pfDir + "/FX_Shell_Rifle.prefab");

            // Small explosion (seeker drone)
            var expl = new GameObject("FX_Explosion_Small");
            AddPS(expl, "Fireball", mGlow, 0.25f, 0.5f, 1f, 3.5f, 0.6f, 1.4f, 14, new Color(1f, 0.6f, 0.25f), new Color(1f, 0.85f, 0.5f), -0.2f, 180f, false, 2.2f);
            AddPS(expl, "Smoke", mSmoke, 1.6f, 3f, 0.6f, 2f, 0.8f, 1.6f, 10, new Color(0.18f, 0.17f, 0.16f, 0.8f), new Color(0.3f, 0.29f, 0.27f, 0.6f), -0.05f, 180f, false, 3.4f);
            AddPS(expl, "Sparks", mSpark, 0.4f, 1.1f, 6f, 14f, 0.02f, 0.05f, 36, Color.white, new Color(1f, 0.7f, 0.3f), 1.2f, 180f, true, 0.5f);
            SavePrefab(expl, pfDir + "/FX_Explosion_Small.prefab");

            // Surface library
            var lib = LoadOrCreate<SurfaceLibrary>(ResDir + "/SurfaceLibrary.asset");
            SurfaceFx Fx(SurfaceType t, GameObject impact, Material decal) => new SurfaceFx { surface = t, impactPrefab = impact, decalMaterial = decal };
            lib.surfaces = new[]
            {
                Fx(SurfaceType.Concrete, impConcrete, dHole), Fx(SurfaceType.Metal, impMetal, dHoleMetal), Fx(SurfaceType.Wood, impWood, dHole),
                Fx(SurfaceType.Dirt, impDirt, null), Fx(SurfaceType.Glass, impGlass, dHole), Fx(SurfaceType.Water, impWater, null),
                Fx(SurfaceType.Fabric, impDirt, null), Fx(SurfaceType.Plastic, impConcrete, dHole), Fx(SurfaceType.Default, impConcrete, dHole),
            };
            lib.fallback = Fx(SurfaceType.Concrete, impConcrete, dHole);
            lib.bloodPrefab = bloodFx;
            lib.bloodHeadshotPrefab = bloodHead;
            lib.bloodDecal = dBlood;
            lib.tracerPrefab = tracerPrefab;
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            Debug.Log("[LR2] FX built (impacts, blood, muzzle flash, tracer, casings, explosion, decals, surface library).");
        }

        /// <summary>
        /// B100: a capture-zone ring. Writes two textures: <paramref name="path"/>_A (white, alpha = ring + faint fill) for the
        /// decal's base colour, and _E (ring only, black elsewhere) for its emission, so only the line glows. Edges are
        /// anti-aliased over ~1.5 px; dashes (when <paramref name="dashes"/> &gt; 0) have crisp anti-aliased ends.
        /// </summary>
        private static (Texture2D albedo, Texture2D emission) ZoneRingTexture(string path, int size, float rIn, float rOut, int dashes, float duty, float fill)
        {
            var a = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            var e = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            var pa = new Color32[size * size];
            var pe = new Color32[size * size];
            float aa = 1.5f / (size * 0.5f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float ring = Mathf.Clamp01((r - rIn) / aa + 0.5f) * Mathf.Clamp01((rOut - r) / aa + 0.5f);
                    if (dashes > 0 && ring > 0f)
                    {
                        float phase = (Mathf.Atan2(dy, dx) / (2f * Mathf.PI) + 1f) * dashes;
                        float f = phase - Mathf.Floor(phase);
                        float w = aa * dashes / (2f * Mathf.PI * Mathf.Max(r, 0.05f));
                        ring *= Mathf.Clamp01((duty - f) / w + 0.5f) * Mathf.Clamp01(f / w + 0.5f);
                    }
                    // a thin solid hairline just inside a dashed ring keeps the boundary continuous
                    float hair = dashes > 0 ? Mathf.Clamp01((r - (rIn - 0.010f)) / aa + 0.5f) * Mathf.Clamp01(((rIn - 0.004f) - r) / aa + 0.5f) * 0.7f : 0f;
                    float line = Mathf.Max(ring, hair);
                    float inside = r < rIn ? fill * (0.3f + 0.7f * Mathf.Pow(r / rIn, 4f)) : 0f;
                    float alpha = Mathf.Clamp01(line + inside * (1f - line));
                    pa[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                    byte le = (byte)Mathf.RoundToInt(line * 255f);
                    pe[y * size + x] = new Color32(le, le, le, le);
                }
            a.SetPixels32(pa); a.Apply(false, false);
            e.SetPixels32(pe); e.Apply(false, false);
            var ta = SaveTexture(a, path + "_A.png", false, true, true);
            var te = SaveTexture(e, path + "_E.png", false, true, true);
            foreach (var t in new[] { ta, te })
            {
                var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) as TextureImporter;
                if (imp == null) continue;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.maxTextureSize = size;
                imp.textureCompression = TextureImporterCompression.CompressedHQ;
                imp.SaveAndReimport();
            }
            return (AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GetAssetPath(ta)),
                    AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GetAssetPath(te)));
        }

        private static Material NewZoneDecal((Texture2D albedo, Texture2D emission) tex)
        {
            var m = NewDecal(tex.albedo, Color.white);
            void F(string p, float v) { if (m.HasProperty(p)) m.SetFloat(p, v); }
            F("_AffectAlbedo", 1f);
            F("_AffectNormal", 0f);
            F("_AffectMetal", 0f);
            F("_AffectSmoothness", 0f);
            F("_AffectAO", 0f);
            F("_AffectEmission", 1f);
            F("_DecalBlend", 1f);
            if (m.HasProperty("_EmissiveColorMap")) m.SetTexture("_EmissiveColorMap", tex.emission);
            if (m.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", Color.white * 1500f);
            HDMaterial.ValidateMaterial(m);
            return m;
        }

        private static Material NewDecal(Texture tex, Color color)
        {
            var shader = Shader.Find("HDRP/Decal");
            var m = new Material(shader);
            m.SetTexture("_BaseColorMap", tex);
            m.SetColor("_BaseColor", color);
            if (m.HasProperty("_AffectAlbedo")) m.SetFloat("_AffectAlbedo", 1f);
            if (m.HasProperty("_AffectNormal")) m.SetFloat("_AffectNormal", 0f);
            HDMaterial.ValidateMaterial(m);
            return m;
        }

        private static ParticleSystem AddPS(GameObject parent, string name, Material mat, float lifeMin, float lifeMax, float speedMin, float speedMax,
            float sizeMin, float sizeMax, int burst, Color a, Color b, float gravity, float coneAngle, bool stretch, float sizeGrow)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 0.6f;
            main.loop = false;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifeMin, lifeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speedMin, speedMax);
            main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
            main.startColor = new ParticleSystem.MinMaxGradient(a, b);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = Mathf.Max(8, burst * 3);
            main.stopAction = ParticleSystemStopAction.None;
            var em = ps.emission;
            em.rateOverTime = 0f;
            em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)burst) });
            var sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.angle = Mathf.Clamp(coneAngle, 0f, 90f);
            if (coneAngle >= 90f) sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = 0.02f;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.4f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, sizeGrow));
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = stretch ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            if (stretch)
            {
                r.velocityScale = 0.035f;
                r.lengthScale = 1.2f;
            }
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        // ---- procedural textures
        private static Texture2D NewTex(int w, int h) => new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };

        private static Texture2D MakeSoftDot(int s)
        {
            var t = NewTex(s, s);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(s * 0.5f, s * 0.5f)) / (s * 0.5f);
                    float a = Mathf.Clamp01(1f - d);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a * a * (3f - 2f * a)));
                }
            t.Apply();
            return t;
        }

        private static Texture2D MakePuff(int s, int seed)
        {
            var t = NewTex(s, s);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = x / (float)s, v = y / (float)s;
                    float d = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f)) * 2f;
                    float n = Mathf.PerlinNoise(u * 4f + seed, v * 4f + seed) * 0.6f + Mathf.PerlinNoise(u * 9f + seed * 2, v * 9f) * 0.4f;
                    float a = Mathf.Clamp01((1f - d) * 1.4f) * Mathf.Clamp01(n * 1.6f - 0.25f);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            t.Apply();
            return t;
        }

        private static Texture2D MakeTracer(int w, int h)
        {
            var t = NewTex(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    // UV.x=0 is LineRenderer pos 0 (HEAD), UV.x=1 is pos 1 (TAIL): head must stay bright so vertex alpha never cancels it.
                    float along = 1f - x / (float)(w - 1);
                    float across = 1f - Mathf.Abs((y + 0.5f) / h * 2f - 1f);
                    float core = Mathf.Pow(across, 2.2f);
                    float halo = Mathf.Pow(across, 1.15f) * 0.55f;
                    float alongGlow = Mathf.Lerp(0.18f, 1f, Mathf.Pow(along, 0.85f));
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, alongGlow * Mathf.Clamp01(core + halo)));
                }
            t.Apply();
            return t;
        }

        private static Texture2D MakeBulletHole(int s, int seed)
        {
            var t = NewTex(s, s);
            var rnd = new System.Random(seed);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = (x + 0.5f) / s - 0.5f, v = (y + 0.5f) / s - 0.5f;
                    float d = Mathf.Sqrt(u * u + v * v) * 2f;
                    float ang = Mathf.Atan2(v, u);
                    float jag = Mathf.PerlinNoise(Mathf.Cos(ang) * 3f + 10f, Mathf.Sin(ang) * 3f + 10f) * 0.18f;
                    Color c;
                    if (d < 0.16f + jag * 0.4f) c = new Color(0.02f, 0.02f, 0.02f, 1f);
                    else if (d < 0.5f + jag) c = new Color(0.12f, 0.11f, 0.1f, Mathf.Lerp(0.9f, 0f, (d - 0.16f) / 0.5f));
                    else c = new Color(0.2f, 0.19f, 0.18f, 0f);
                    t.SetPixel(x, y, c);
                }
            for (int i = 0; i < 40; i++)
            {
                int px = rnd.Next(s / 4, s * 3 / 4), py = rnd.Next(s / 4, s * 3 / 4);
                t.SetPixel(px, py, new Color(0.05f, 0.05f, 0.05f, 0.8f));
            }
            t.Apply();
            return t;
        }

        private static Texture2D MakeBloodSplat(int s, int seed)
        {
            var t = NewTex(s, s);
            var clear = new Color(0.4f, 0f, 0f, 0f);
            for (int y = 0; y < s; y++) for (int x = 0; x < s; x++) t.SetPixel(x, y, clear);
            var rnd = new System.Random(seed);
            for (int i = 0; i < 22; i++)
            {
                float cx = (float)(0.3 + rnd.NextDouble() * 0.4) * s, cy = (float)(0.3 + rnd.NextDouble() * 0.4) * s;
                float r = (float)(0.03 + rnd.NextDouble() * (i < 4 ? 0.16 : 0.06)) * s;
                if (i >= 4)
                {
                    float a = (float)(rnd.NextDouble() * Mathf.PI * 2), dist = (float)(0.1 + rnd.NextDouble() * 0.3) * s;
                    cx = s * 0.5f + Mathf.Cos(a) * dist;
                    cy = s * 0.5f + Mathf.Sin(a) * dist;
                }
                for (int y = Mathf.Max(0, (int)(cy - r)); y < Mathf.Min(s, (int)(cy + r)); y++)
                    for (int x = Mathf.Max(0, (int)(cx - r)); x < Mathf.Min(s, (int)(cx + r)); x++)
                    {
                        float d = Vector2.Distance(new Vector2(x, y), new Vector2(cx, cy)) / r;
                        if (d > 1f) continue;
                        var c = t.GetPixel(x, y);
                        c.a = Mathf.Max(c.a, Mathf.Clamp01((1f - d) * 3f) * 0.95f);
                        c.r = 0.45f;
                        t.SetPixel(x, y, c);
                    }
            }
            t.Apply();
            return t;
        }

        private static Texture2D MakeChevron(int s)
        {
            var t = NewTex(s, s);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = (x + 0.5f) / s * 2f - 1f, v = (y + 0.5f) / s * 2f - 1f;
                    float shape = Mathf.Abs(u) + (v + 0.2f) * 1.1f; // downward triangle
                    float inside = Mathf.Clamp01((0.75f - shape) * 12f) * Mathf.Clamp01((v + 0.75f) * 12f);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, inside));
                }
            t.Apply();
            return t;
        }

        // =====================================================================================================
        // 3. Audio: common sounds, weapon audio profile, surface sounds, importer settings
        // =====================================================================================================
        public static void BuildAudio()
        {
            const string audio = "Assets/LR2/Audio";
            ConfigureAudioImporters(audio);

            var common = LoadOrCreate<LR2CommonSounds>(ResDir + "/LR2CommonSounds.asset");
            FillSoundSets(common, audio + "/Common");
            common.ambienceLoops = LoadClips(audio + "/Ambience");
            common.ambienceVolume = 0.32f;
            if (common.hitmarker != null) { common.hitmarker.volume = 1f; common.hitmarker.pitch = new Vector2(0.97f, 1.04f); }
            if (common.hitmarkerHeadshot != null) { common.hitmarkerHeadshot.volume = 1f; common.hitmarkerHeadshot.pitch = new Vector2(0.98f, 1.03f); }
            if (common.killConfirm != null) { common.killConfirm.volume = 1f; }
            if (common.bodyFall != null) { common.bodyFall.volume = 1f; common.bodyFall.pitch = new Vector2(0.95f, 1.03f); }
            if (common.painGrunt != null) { common.painGrunt.volume = 0.18f; common.painGrunt.pitch = new Vector2(0.97f, 1.03f); }
            if (common.playerHurt != null) { common.playerHurt.volume = 0.18f; common.playerHurt.pitch = new Vector2(0.97f, 1.03f); }
            if (common.bulletWhizz != null) { common.bulletWhizz.minDistance = 2.5f; common.bulletWhizz.maxDistance = 16f; common.bulletWhizz.pitch = new Vector2(0.94f, 1.08f); }
            if (common.bulletCrack != null) { common.bulletCrack.minDistance = 2.5f; common.bulletCrack.maxDistance = 18f; common.bulletCrack.pitch = new Vector2(0.95f, 1.06f); }
            // B126: drone blast (Common/droneExplode, private MW SFX C4 bodies; the folder is git-ignored like all LR2 audio)
            if (common.droneExplode != null) { common.droneExplode.volume = 1f; common.droneExplode.minDistance = 4f; common.droneExplode.maxDistance = 90f; common.droneExplode.pitch = new Vector2(0.94f, 1.06f); common.droneExplode.priority = 64; }
            EditorUtility.SetDirty(common);

            var foley = LoadOrCreate<FoleyLibrary>(ResDir + "/FoleyLibrary.asset");
            FillSoundSets(foley, audio + "/Foley");
            if (foley.pain != null) { foley.pain.volume = 0.32f; foley.pain.pitch = new Vector2(0.84f, 0.93f); }
            EditorUtility.SetDirty(foley);

            var wa = LoadOrCreate<WeaponAudioProfile>(DataDir + "/Weapons/WA_AK12.asset");
            FillSoundSets(wa, audio + "/Weapons/AK12");
            wa.magOutAt = 0.13f; wa.magInAt = 0.43f; wa.boltAt = 0.63f; wa.emptyReloadEndAt = 0.82f;
            if (wa.firePlayerSuppressed != null) wa.firePlayerSuppressed.volume = 1.0f;
            if (wa.fireNpcSuppressed != null) wa.fireNpcSuppressed.volume = 0.86f;
            if (wa.fireTailSuppressed != null) wa.fireTailSuppressed.volume = 0.56f;
            if (wa.fireTailInteriorSuppressed != null) wa.fireTailInteriorSuppressed.volume = 0.64f;
            EditorUtility.SetDirty(wa);
            foreach (var variant in new[] { "M4", "FAL", "Pistol" })
            {
                if (!AssetDatabase.IsValidFolder(audio + "/Weapons/" + variant)) continue;
                var wv = LoadOrCreate<WeaponAudioProfile>(DataDir + $"/Weapons/WA_{variant}.asset");
                EditorUtility.CopySerialized(wa, wv);
                // B143: Clear AK12 shot/tail/handling arrays before filling variant clips so M4/FAL/Pistol never inherit AK-47 NPC bands.
                foreach (var set in new[]
                {
                    wv.firePlayer, wv.fireFirstShot, wv.fireMech, wv.fireMechAds, wv.fireTail, wv.fireTailInterior,
                    wv.firePlayerSuppressed, wv.fireNpcSuppressed, wv.fireTailSuppressed, wv.fireTailInteriorSuppressed,
                    wv.fireNpcClose, wv.fireNpcMed, wv.fireNpcFar, wv.fireNpcDistant,
                    wv.dryFire, wv.magOut, wv.magIn, wv.boltCharge, wv.fireModeSwitch
                })
                {
                    if (set != null) set.clips = System.Array.Empty<AudioClip>();
                }
                FillSoundSets(wv, audio + "/Weapons/" + variant);

                // Backfill missing variant bands from THIS weapon's own recordings (never from AK12!).
                AudioClip[] ownCore = wv.firePlayer.IsValid ? wv.firePlayer.clips : (wv.fireNpcClose.IsValid ? wv.fireNpcClose.clips : System.Array.Empty<AudioClip>());
                AudioClip[] ownNpc = wv.fireNpcClose.IsValid ? wv.fireNpcClose.clips : ownCore;
                if (!wv.firePlayer.IsValid && ownNpc.Length > 0) wv.firePlayer.clips = ownNpc;
                if (!wv.fireFirstShot.IsValid && ownCore.Length > 0) wv.fireFirstShot.clips = ownCore;
                if (!wv.fireNpcClose.IsValid && ownCore.Length > 0) wv.fireNpcClose.clips = ownCore;
                if (!wv.fireNpcMed.IsValid && ownNpc.Length > 0) wv.fireNpcMed.clips = ownNpc;
                if (!wv.fireNpcFar.IsValid && ownNpc.Length > 0) wv.fireNpcFar.clips = ownNpc;
                if (!wv.fireNpcDistant.IsValid && ownNpc.Length > 0) wv.fireNpcDistant.clips = ownNpc;
                if (!wv.fireMech.IsValid && wa.fireMech.IsValid) wv.fireMech.clips = wa.fireMech.clips;
                if (!wv.fireMechAds.IsValid && wa.fireMechAds.IsValid) wv.fireMechAds.clips = wa.fireMechAds.clips;
                if (!wv.fireTail.IsValid && wa.fireTail.IsValid) wv.fireTail.clips = wa.fireTail.clips;
                if (!wv.fireTailInterior.IsValid && wa.fireTailInterior.IsValid) wv.fireTailInterior.clips = wa.fireTailInterior.clips;
                if (!wv.dryFire.IsValid && wa.dryFire.IsValid) wv.dryFire.clips = wa.dryFire.clips;
                if (!wv.fireModeSwitch.IsValid && wa.fireModeSwitch.IsValid) wv.fireModeSwitch.clips = wa.fireModeSwitch.clips;

                if (variant == "M4")
                {
                    wv.magOutAt = 0.14f; wv.magInAt = 0.47f; wv.boltAt = 0.66f; wv.emptyReloadEndAt = 0.86f;
                    if (wv.firePlayerSuppressed != null) wv.firePlayerSuppressed.pitch = new Vector2(0.92f, 0.99f);
                }
                else if (variant == "FAL")
                {
                    wv.magOutAt = 0.17f; wv.magInAt = 0.55f; wv.boltAt = 0.74f; wv.emptyReloadEndAt = 0.90f;
                }
                else if (variant == "Pistol")
                {
                    wv.magOutAt = 0.16f; wv.magInAt = 0.46f; wv.boltAt = 0.64f; wv.emptyReloadEndAt = 0.81f;
                    if (wv.firePlayerSuppressed.IsValid) wv.fireNpcSuppressed.clips = wv.firePlayerSuppressed.clips;
                    // suppressed sidearm: no big rifle tails, much shorter reach
                    wv.fireTail.volume = 0.15f;
                    wv.fireTailInterior.volume = 0.2f;
                    wv.fireNpcDistant.volume = 0.2f;
                    if (wv.fireTailSuppressed != null) wv.fireTailSuppressed.volume = 0.18f;
                    if (wv.fireTailInteriorSuppressed != null) wv.fireTailInteriorSuppressed.volume = 0.24f;
                }
                EditorUtility.SetDirty(wv);
            }

            var lib = LoadOrCreate<SurfaceLibrary>(ResDir + "/SurfaceLibrary.asset");
            foreach (var s in lib.surfaces.Concat(new[] { lib.fallback }))
            {
                if (s == null) continue;
                string name = s.surface == SurfaceType.Default ? "Concrete" : s.surface.ToString();
                var imp = LoadClips($"{audio}/Surfaces/{name}/impactSound");
                var steps = LoadClips($"{audio}/Surfaces/{name}/footstepSound");
                if (imp.Length > 0) s.impactSound = new SoundSet(AudioCategory.Sfx, 0.85f, 40f, 1f) { clips = imp };
                if (steps.Length > 0) s.footstepSound = new SoundSet(AudioCategory.Footsteps, 0.65f, 28f, 1f) { clips = steps, pitch = new Vector2(0.92f, 1.08f) };
            }
            var flesh = LoadClips(audio + "/Flesh/fleshImpact");
            var head = LoadClips(audio + "/Flesh/headshotImpact");
            if (flesh.Length > 0) lib.fleshImpact = new SoundSet(AudioCategory.Sfx, 0.95f, 30f, 1f) { clips = flesh };
            if (head.Length > 0) lib.headshotImpact = new SoundSet(AudioCategory.Sfx, 1f, 35f, 1f) { clips = head };
            EditorUtility.SetDirty(lib);
            LR2MusicGenerator.EnsureGenerated(true);
            AssetDatabase.SaveAssets();
            Debug.Log("[LR2] Audio wired (common sounds, AK-12 layered audio, surface impacts/footsteps, adaptive music stems).");
        }

        private static void FillSoundSets(object target, string folderRoot)
        {
            foreach (var f in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType != typeof(SoundSet)) continue;
                var clips = LoadClips(folderRoot + "/" + f.Name);
                if (clips.Length == 0) continue;
                var set = (SoundSet)f.GetValue(target) ?? new SoundSet();
                set.clips = clips;
                f.SetValue(target, set);
            }
        }

        private static void ConfigureAudioImporters(string root)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { root }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var imp = AssetImporter.GetAtPath(path) as AudioImporter;
                if (imp == null) continue;
                bool ambience = path.Contains("/Ambience/");
                bool stereoOk = ambience || path.Contains("/firePlayer/") || path.Contains("/firePlayerSuppressed/") || path.Contains("/fireFirstShot/") || path.Contains("/fireTail") || path.Contains("/Common/hitmarker") || path.Contains("/Common/killConfirm");
                imp.forceToMono = !stereoOk;
                imp.loadInBackground = ambience;
                var s = imp.defaultSampleSettings;
                s.loadType = ambience ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = 0.75f;
                s.preloadAudioData = !ambience;
                imp.defaultSampleSettings = s;
                imp.SaveAndReimport();
            }
        }

        // =====================================================================================================
        // 4. Weapons: definition + LR2 AK-12 prefab (script-swapped from the Kinemation SWAT AK12)
        // =====================================================================================================
        private class WeaponSpec
        {
            public string id, display, source, audio = "AK12";
            /// <summary>B136: the Kinemation GENERIC demo prefab for the robot rig (null = source).</summary>
            public string robotSource;
            public WeaponClass cls = WeaponClass.AssaultRifle;
            public float rpm = 700f, near = 33f, mid = 27f, far = 23f, head = 1.5f, range = 32f;
            public float hip = 2.6f, ads = 0.06f, adsTime = 0.21f, sprintToFire = 0.24f, adsFov = 0.72f;
            public int mag = 30, reserve = 120, maxReserve = 210;
            public bool auto = true, akMaterials;
            public Vector2 kick = new Vector2(0.12f, 0.06f);
            // Kinemation demo recoil patterns climb 3-4 deg/shot; scale to ~1-1.3 deg (hip). ADS multiplies pattern + kick.
            public float pattern = 0.33f, patternH = 0.3f, adsRecoil = 0.55f, adsKick = 0.5f;
            // B49 / B26 (D63): Per-gun hand offsets in weapon space (x right, y up, z forward).
            public Vector3 rightGripOffset = new Vector3(0.002f, -0.012f, -0.038f);
            public Vector3 leftGripOffset = new Vector3(0f, -0.036f, 0f);
        }

        /// <summary>Every gun comes from a Kinemation demo weapon (its own grip pose, ADS, reload and equip animations) made
        /// for the humanoid driver rig. Loadout = weaponPrefabs order (1 = primary, 2 = sidearm).</summary>
        private static readonly WeaponSpec[] Guns =
        {
            // B55/B49/B26(D63): LeadRushHDRP default MK18 Mod 1 (clean, attachment-free iron-sight primary) is Slot 0 default; AK-12 remains selectable alongside Mk23 sidearm.
            new WeaponSpec { id = "Mk18", display = "MK18", source = DemoDir + "/Prefabs/Mk18/Mk18_Scriptable.prefab", audio = "M4", akMaterials = true,
                             rpm = 700f, near = 37.5f, mid = 33.4f, far = 21.5f, range = 28f, hip = 2.0f, adsTime = 0.18f, sprintToFire = 0.2f, kick = new Vector2(0.1f, 0.06f),
                             pattern = 0.3f, adsRecoil = 0.50f, rightGripOffset = new Vector3(-0.004f, -0.022f, 0.012f), leftGripOffset = new Vector3(0f, -0.036f, 0f) }, // B47: 5.56 recoils less than 5.45 in ADS
            new WeaponSpec { id = "Mk23", display = "MK23", source = DemoDir + "/Prefabs/Humanoid/Mk23Mod0_Scriptable_Humanoid.prefab", audio = "Pistol",
                             robotSource = DemoDir + "/Prefabs/Pistol/Mk23Mod0_Scriptable.prefab",
                             cls = WeaponClass.Pistol, rpm = 300f, auto = false, mag = 12, reserve = 48, maxReserve = 72, near = 37.5f, mid = 33.4f, far = 21.5f,
                             head = 1.8f, range = 18f, hip = 1.6f, ads = 0.08f, adsTime = 0.14f, sprintToFire = 0.12f, adsFov = 0.82f, kick = new Vector2(0.4f, 0.1f),
                             pattern = 1f, patternH = 1f, rightGripOffset = Vector3.zero, leftGripOffset = Vector3.zero },
            // B107: slower, distinct per-weapon cadence; damage tiers compensate for the slower cycle while staying near current
            // body-shot TTK against both 100 HP and 100 HP + 50 armor (discrete shot counts limit exact matching).
            new WeaponSpec { id = "AK12", display = "AK-12", source = HumanoidAk12, robotSource = FullAk12, akMaterials = true, rpm = 600f, near = 37.5f, mid = 30f, far = 25f, hip = 2.2f },
            new WeaponSpec { id = "AK74N", display = "AK-74N", source = DemoDir + "/Prefabs/AK12/AK74N_Scriptable.prefab", akMaterials = true, rpm = 550f, near = 37.5f, mid = 30f, far = 25f,
                             adsTime = 0.22f, pattern = 0.36f, rightGripOffset = new Vector3(0.002f, -0.014f, -0.030f), leftGripOffset = new Vector3(0f, -0.036f, 0f) },
            new WeaponSpec { id = "FAL", display = "FAL", source = DemoDir + "/Prefabs/FAL/FAL_Scriptable.prefab", audio = "FAL", akMaterials = true, rpm = 500f, mag = 20, reserve = 80, maxReserve = 140,
                             near = 48f, mid = 42f, far = 37.5f, range = 45f, hip = 3.0f, adsTime = 0.27f, sprintToFire = 0.3f, adsFov = 0.66f, kick = new Vector2(0.24f, 0.12f),
                             pattern = 0.5f, patternH = 0.4f, adsRecoil = 0.60f, rightGripOffset = new Vector3(0.000f, -0.016f, -0.010f), leftGripOffset = new Vector3(0f, -0.036f, 0f) }, // B47: 7.62x51 stays harder in ADS
        };

        public static void BuildWeapons()
        {
            int built = 0;
            foreach (var g in Guns)
                if (BuildWeapon(g) != null) built++;
            if (Robot)
            {
                // B136 (user 11-22): the knife and the torch as slots 3 and 4
                if (BuildKnife() != null) built++;
                if (BuildTorch() != null) built++;
                LR2ScopeBuilder.UpgradePipScopesAndGrenades();
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[LR2] Weapons built: {built}/{Guns.Length} (Kinemation demo animation sets).");
        }

        private static GameObject BuildWeapon(WeaponSpec spec)
        {
            var def = LoadOrCreate<WeaponDefinition>(DataDir + $"/Weapons/WD_{spec.id}.asset");
            def.displayName = spec.display;
            def.shortName = spec.display;
            def.weaponClass = spec.cls;
            def.rpm = spec.rpm;
            def.allowAuto = spec.auto;
            def.magazineSize = spec.mag;
            def.startingReserve = spec.reserve;
            def.maxReserve = spec.maxReserve;
            def.damageSteps = new[]
            {
                new DamageStep { maxDistance = spec.range * 0.62f, damage = spec.near },
                new DamageStep { maxDistance = spec.range * 1.25f, damage = spec.mid },
                new DamageStep { maxDistance = 999f, damage = spec.far },
            };
            def.headMultiplier = spec.head;
            def.upperTorsoMultiplier = 1.1f;
            def.limbMultiplier = 0.9f;
            def.effectiveRange = spec.range;
            def.hipSpread = spec.hip;
            def.adsSpread = spec.ads;
            def.adsTime = spec.adsTime;
            def.sprintToFireTime = spec.sprintToFire;
            def.adsFovMultiplier = spec.adsFov;
            def.cameraKick = spec.kick;
            def.adsKickMultiplier = spec.adsKick;
            def.recoilPatternScale = spec.pattern;
            def.recoilPatternHorizontalScale = spec.patternH;
            def.adsRecoilMultiplier = spec.adsRecoil;
            def.audio = AssetDatabase.LoadAssetAtPath<WeaponAudioProfile>(DataDir + $"/Weapons/WA_{spec.audio}.asset")
                        ?? AssetDatabase.LoadAssetAtPath<WeaponAudioProfile>(DataDir + "/Weapons/WA_AK12.asset");
            def.shellPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDir + "/FX/FX_Shell_Rifle.prefab");
            def.muzzleFlashPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDir + "/FX/FX_MuzzleFlash.prefab");
            def.tracerEvery = 1;
            def.rightGripOffset = spec.rightGripOffset;
            def.leftGripOffset = spec.leftGripOffset;
            EditorUtility.SetDirty(def);

            bool robot = Robot; // B136: the generic demo guns exactly as shipped (profiles, attachments, reloads)
            string weaponSrc = robot ? (spec.robotSource ?? spec.source) : UseKinemationDriver ? spec.source : SwatAk12;
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(weaponSrc);
            if (src == null)
            {
                Debug.LogError("[LR2] Weapon source prefab not found: " + weaponSrc);
                return null;
            }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(src);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = "LR2_" + spec.id;

            // Photoscanned object-space triplanar materials so steel receiver & polymer grips look lived-in without texture swim.
            if (spec.akMaterials) ApplyWeaponMaterials(go);
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.updateWhenOffscreen = true;

            // B49/B55: Clear Generic-rig AttachHandLayerSettings from Kinemation demo attachments (e.g. Mk18 GripEmpty/GripMount)
            // so Humanoid left-hand IK stays locked to the handguard instead of snapping down to the waist.
            if (!robot)
                foreach (var att in go.GetComponentsInChildren<BaseAttachment>(true))
                {
                    if (att != null && att.attachmentLayerSettings != null && att.attachmentLayerSettings.Count > 0)
                    {
                        att.attachmentLayerSettings.Clear();
                        EditorUtility.SetDirty(att);
                    }
                }

            // B49: AK-74N's demo 1P29 scope shader renders magenta in HDRP ADS; switch AK-74N cleanly to its built-in DefaultAimPoint iron sights.
            if (!robot && spec.id == "AK74N")
            {
                foreach (var tf in go.GetComponentsInChildren<Transform>(true))
                {
                    if (tf.name == "1P29" || tf.name == "SideScope") tf.gameObject.SetActive(false);
                }
            }
            // Default MK18: use LeadRushHDRP's W_Mk18_ReloadEmpty mesh (crisp built-in iron sights) and disable all optional attachments.
            if (!robot && spec.id == "Mk18") // B136: the robot keeps the stock MK18 with its attachments
            {
                foreach (var att in go.GetComponentsInChildren<BaseAttachment>(true))
                {
                    if (att != null) att.gameObject.SetActive(false);
                }
                SkinnedMeshRenderer hdrpMk18Smr = null;
                SkinnedMeshRenderer coreMk18Smr = null;
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr == null || smr.sharedMesh == null) continue;
                    string meshPath = AssetDatabase.GetAssetPath(smr.sharedMesh);
                    if (meshPath.Contains("W_Mk18_ReloadEmpty")) hdrpMk18Smr = smr;
                    else if (meshPath.Contains("Mk18_Core")) coreMk18Smr = smr;
                }
                if (hdrpMk18Smr != null)
                {
                    hdrpMk18Smr.gameObject.SetActive(true);
                    hdrpMk18Smr.updateWhenOffscreen = true;
                    Transform rigNode = hdrpMk18Smr.rootBone != null ? hdrpMk18Smr.rootBone.parent : null;
                    if (rigNode != null)
                    {
                        rigNode.gameObject.SetActive(true);
                        rigNode.name = "Mk18";
                    }
                    // Apply LeadRushHDRP Demo/Items/MK18/Materials PBR slots if present
                    var pMats = hdrpMk18Smr.sharedMaterials;
                    var mkFinish = AssetDatabase.LoadAssetAtPath<Material>("Assets/Demo/Items/MK18/Materials/finish.001.mat");
                    var mkBright = AssetDatabase.LoadAssetAtPath<Material>("Assets/Demo/Items/MK18/Materials/metal bright-ish.mat");
                    var mkDark = AssetDatabase.LoadAssetAtPath<Material>("Assets/Demo/Items/MK18/Materials/metal dark.mat");
                    var mkPlastic = AssetDatabase.LoadAssetAtPath<Material>("Assets/Demo/Items/MK18/Materials/plastic.mat");
                    var mkPoly = AssetDatabase.LoadAssetAtPath<Material>("Assets/Demo/Items/MK18/Materials/polymer.mat");
                    if (pMats != null && pMats.Length >= 6 && mkDark != null && mkPoly != null && mkFinish != null)
                    {
                        pMats[0] = mkFinish;
                        pMats[1] = mkPoly;
                        pMats[2] = mkDark;
                        pMats[3] = mkBright ?? mkDark;
                        pMats[4] = mkPlastic ?? mkDark;
                        pMats[5] = mkDark;
                        hdrpMk18Smr.sharedMaterials = pMats;
                    }
                }
                if (coreMk18Smr != null)
                {
                    coreMk18Smr.gameObject.SetActive(false);
                    Transform unusedRig = coreMk18Smr.rootBone != null ? coreMk18Smr.rootBone.parent : null;
                    if (unusedRig != null && unusedRig != go.transform)
                    {
                        unusedRig.name = "Mk18_Core_Unused";
                        unusedRig.gameObject.SetActive(false);
                    }
                }
            }

            // Ensure a single weapon Animator on the child node that owns the rig (never shadow AK12's W_AK12_ReloadEmpty child Animator with a root Animator).
            var weaponCtrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(WeaponAnimControllerPath);
            Animator chosenAnim = null;
            foreach (var cand in go.GetComponentsInChildren<Animator>(true))
            {
                if (cand.transform.Find("AK12Rig") != null || cand.transform.Find("FAL") != null ||
                    cand.transform.Find("Mk18") != null || cand.transform.Find("Mk23Mod0Rig") != null)
                {
                    chosenAnim = cand;
                    break;
                }
            }
            if (chosenAnim == null) chosenAnim = go.GetComponentInChildren<Animator>(true);
            foreach (var extra in go.GetComponentsInChildren<Animator>(true))
            {
                if (chosenAnim != null && extra != chosenAnim && extra.gameObject == go)
                    Object.DestroyImmediate(extra);
            }
            if (chosenAnim == null && weaponCtrl != null && spec.id == "AK12")
            {
                var rigParent = System.Array.Find(go.GetComponentsInChildren<Transform>(true), t => t.name == "AK12Rig")?.parent;
                var targetGo = rigParent != null ? rigParent.gameObject : go;
                chosenAnim = targetGo.GetComponent<Animator>() ?? targetGo.AddComponent<Animator>();
            }
            if (chosenAnim != null)
            {
                if (chosenAnim.runtimeAnimatorController == null && weaponCtrl != null)
                    chosenAnim.runtimeAnimatorController = weaponCtrl;
                chosenAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            var demoType = FindType("Demo.Scripts.Runtime.Item.Weapon");
            var demo = demoType != null ? go.GetComponent(demoType) : null;
            var gun = go.AddComponent<LR2Firearm>();
            if (demo != null)
            {
                CopyMatchingFields(demo, gun);
                Object.DestroyImmediate(demo);
            }

            // Copy recoil/camera-shake/camera-reload assets from the full AK12 prefab if missing on the tutorial prefab.
            var fullAk = AssetDatabase.LoadAssetAtPath<GameObject>(FullAk12);
            var fullDemo = fullAk != null && demoType != null ? fullAk.GetComponent(demoType) : null;
            var so = new SerializedObject(gun);
            if (fullDemo != null && spec.id == "AK12")
            {
                var fso = new SerializedObject(fullDemo);
                foreach (var propName in new[] { "recoilData", "recoilPatternSettings", "cameraShake", "cameraReloadAnimation" })
                {
                    var dst = so.FindProperty(propName);
                    var srcProp = fso.FindProperty(propName);
                    if (dst != null && dst.objectReferenceValue == null && srcProp != null && srcProp.objectReferenceValue != null)
                        dst.objectReferenceValue = srcProp.objectReferenceValue;
                }
            }

            // Bake a SWAT-rig-compatible reload animation asset + configure Swat profile (MaskAttachHand & wall collision layerMask).
            var swatReload = UseKinemationDriver ? null : ConfigureSwatAnimationRigAndReload(); // driver: Kinemation's own reload clip
            var reloadProp = so.FindProperty("reloadClip");
            if (reloadProp != null && swatReload != null) reloadProp.objectReferenceValue = swatReload;
            if (!robot && spec.id == "Mk18" && reloadProp != null)
            {
                // Generic C_Mk18_ReloadEmpty animates IK hands in Blender Z-up (-1.97 m below rig); use clean Humanoid rifle reload clip like AK-74N
                // while Mk18_Animator.overrideController drives Mk18/Root/Mag and Mk18/Root/Bolt on the LeadRushHDRP MK18 rig.
                var humReload = AssetDatabase.LoadAssetAtPath<FPSAnimationAsset>(DemoDir + "/Animations/Weapons/AK12/AA_AK12_Reload_Empty.asset");
                if (humReload != null) reloadProp.objectReferenceValue = humReload;
            }
            var reloadAsset = reloadProp != null ? reloadProp.objectReferenceValue as FPSAnimationAsset : null;
            BakeReloadRestPose(so, reloadAsset != null ? reloadAsset.clip : null, spec.id);
            var botTracerProp = so.FindProperty("botTracerEvery");
            if (botTracerProp != null) botTracerProp.intValue = 1;
            var tacEndProp = so.FindProperty("tacticalEndAfterMagIn");
            if (tacEndProp != null) tacEndProp.floatValue = 0.03f;
            // B49: Ensure every rifle/pistol weapon uses the Humanoid FPSAnimator controller (AK74N/Mk18/FAL came from Generic demo prefabs).
            if (UseKinemationDriver && !robot)
            {
                var humCtrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(DemoDir + "/Animations/Locomotion/FPSAnimator_Humanoid.controller");
                var ovProp = so.FindProperty("overrideController");
                if (ovProp != null && humCtrl != null) ovProp.objectReferenceValue = humCtrl;
            }
            if (robot) ConfigureRobotFirearm(so, go);
            if (!robot && (spec.id == "AK74N" || spec.id == "Mk18"))
            {
                var sg = so.FindProperty("scopeGroups");
                if (sg != null) sg.ClearArray();
            }
            if (!robot && spec.id == "Mk18")
            {
                var ba = so.FindProperty("barrelAttachments.attachments");
                if (ba != null) ba.ClearArray();
                var ga = so.FindProperty("gripAttachments.attachments");
                if (ga != null) ga.ClearArray();
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            // Muzzle + ejection port from mesh bounds (longest axis = barrel, always pointing away from rear AimPoint).
            // B136: the robot's guns carry attachments; measure the bare gun (a fitted suppressor moves the muzzle at runtime)
            var bounds = robot ? LocalBounds(go, true) : LocalBounds(go);
            int axis = bounds.size.x > bounds.size.y ? (bounds.size.x > bounds.size.z ? 0 : 2) : (bounds.size.y > bounds.size.z ? 1 : 2);
            float sign = Mathf.Abs(bounds.max[axis]) >= Mathf.Abs(bounds.min[axis]) ? 1f : -1f;
            var entity = go.GetComponent<FPSAnimatorEntity>();
            if (entity != null && robot)
            {
                // B136: the gun's own profile and aim points stay as shipped
                if (entity.defaultAimPoint == null)
                    entity.defaultAimPoint = go.GetComponentsInChildren<Transform>(true)
                        .FirstOrDefault(tf => tf.name == "AimPoint" || tf.name == "DefaultAimPoint" || tf.name == "PointAim");
                EditorUtility.SetDirty(entity);
            }
            else if (entity != null)
            {
                string profPath = spec.cls == WeaponClass.Pistol
                    ? DemoDir + "/AnimatorProfiles/AnimatorProfile_Pistol_Humanoid.asset"
                    : DemoDir + "/AnimatorProfiles/AnimatorProfile_Rifle_Humanoid.asset";
                var humProfile = AssetDatabase.LoadAssetAtPath<FPSAnimatorProfile>(profPath);
                if (humProfile != null && (entity.animatorProfile == null || !entity.animatorProfile.name.Contains("Humanoid")))
                    entity.animatorProfile = humProfile;
                TuneHumanoidCollision(humProfile, spec.cls == WeaponClass.Pistol);
                TuneHumanoidSway(humProfile);
                TuneHumanoidTurn(humProfile);
                if (spec.id == "Mk18")
                {
                    var ironAp = go.transform.Find("AimPoint_Iron");
                    if (ironAp == null)
                    {
                        ironAp = new GameObject("AimPoint_Iron").transform;
                        ironAp.SetParent(go.transform, false);
                    }
                    ironAp.localPosition = new Vector3(0f, 0.068f, -0.175f);
                    ironAp.localRotation = Quaternion.identity;
                    entity.defaultAimPoint = ironAp;
                }
                else if (spec.id == "AK74N")
                {
                    // D58: B49 hid the 1P29 scope but left the entity aiming through the hidden scope's eye point, 3.1 cm above
                    // and 1.4 cm left of the irons (user 11-18: "AK74N ADS is totally off, not looking through the iron sights").
                    // Kinemation's DefaultAimPoint marks the iron line (y 0.052); keep it, with MK18-like eye relief.
                    var ironAp = go.transform.Find("AimPoint_Iron");
                    if (ironAp == null)
                    {
                        ironAp = new GameObject("AimPoint_Iron").transform;
                        ironAp.SetParent(go.transform, false);
                    }
                    var kinemationIron = go.transform.Find("DefaultAimPoint");
                    Vector3 iron = kinemationIron != null ? kinemationIron.localPosition : new Vector3(0f, 0.052f, -0.048f);
                    ironAp.localPosition = new Vector3(iron.x, iron.y, -0.20f);
                    ironAp.localRotation = Quaternion.identity;
                    entity.defaultAimPoint = ironAp;
                }
                else if (entity.defaultAimPoint == null)
                {
                    entity.defaultAimPoint = go.GetComponentsInChildren<Transform>(true)
                        .FirstOrDefault(t => t.name == "AimPoint" || t.name == "DefaultAimPoint" || t.name == "PointAim");
                    if (spec.id == "Mk18")
                {
                    var ironAp = go.transform.Find("AimPoint_Iron");
                    if (ironAp == null)
                    {
                        ironAp = new GameObject("AimPoint_Iron").transform;
                        ironAp.SetParent(go.transform, false);
                    }
                    ironAp.localPosition = new Vector3(0f, 0.068f, -0.175f);
                    ironAp.localRotation = Quaternion.identity;
                    entity.defaultAimPoint = ironAp;
                }
                else if (entity.defaultAimPoint == null)
                    {
                        var ap = new GameObject("AimPoint").transform;
                        ap.SetParent(go.transform, false);
                        ap.localPosition = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
                        entity.defaultAimPoint = ap;
                    }
                }
                EditorUtility.SetDirty(entity);
            }
            if (entity != null && entity.defaultAimPoint != null)
            {
                Vector3 aimLoc = go.transform.InverseTransformPoint(entity.defaultAimPoint.position);
                sign = aimLoc[axis] <= bounds.center[axis] ? 1f : -1f;
            }
            Vector3 muzzlePos = bounds.center;
            muzzlePos[axis] = sign > 0 ? bounds.max[axis] : bounds.min[axis];
            muzzlePos.y = axis == 1 ? muzzlePos.y : bounds.center.y + bounds.extents.y * 0.25f;
            Vector3 barrelDir = Vector3.zero;
            barrelDir[axis] = sign;

            var muzzle = new GameObject("Muzzle").transform;
            muzzle.SetParent(go.transform, false);
            muzzle.localPosition = muzzlePos;
            muzzle.localRotation = Quaternion.LookRotation(barrelDir);
            var flashPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDir + "/FX/FX_MuzzleFlash.prefab");
            var flash = flashPrefab != null ? (GameObject)PrefabUtility.InstantiatePrefab(flashPrefab, muzzle) : null;

            var port = new GameObject("EjectionPort").transform;
            port.SetParent(go.transform, false);
            Vector3 portPos = bounds.center;
            portPos[axis] = Mathf.Lerp(bounds.center[axis], muzzlePos[axis], -0.1f);
            port.localPosition = portPos + Vector3.up * bounds.extents.y * 0.3f;
            port.localRotation = Quaternion.LookRotation(barrelDir);
            var ejector = port.gameObject.AddComponent<ShellEjector>();
            ejector.ShellPrefab = def.shellPrefab;

            gun.Configure(def, muzzle, flash != null ? flash.GetComponent<MuzzleFlash>() : null, ejector);
            if (robot) AddLaserDevice(go, gun, muzzle, spec.cls == WeaponClass.Pistol); // B136
            EditorUtility.SetDirty(gun);
            return SavePrefab(go, PrefabDir + $"/Weapons/LR2_{spec.id}.prefab");
        }

        private static void BakeReloadRestPose(SerializedObject so, AnimationClip clip, string id)
        {
            var rootProp = so.FindProperty("reloadRestPose");
            if (rootProp == null) return;
            var rest = default(ReloadMotionBridge.Rest);
            rest.gunRot = Quaternion.identity;
            rest.handLRot = Quaternion.identity;
            rest.handRRot = Quaternion.identity;

            if (clip != null)
            {
                var bindings = AnimationUtility.GetCurveBindings(clip);
                bool TrySampleBone(string path, string fallbackPath, out Vector3 pos, out Quaternion rot)
                {
                    pos = Vector3.zero;
                    rot = Quaternion.identity;
                    string matched = null;
                    foreach (var p in new[] { path, fallbackPath })
                    {
                        if (string.IsNullOrEmpty(p)) continue;
                        string leaf = p.Substring(p.LastIndexOf('/') + 1);
                        foreach (var b in bindings)
                        {
                            if (b.type == typeof(Transform) && b.path == p) { matched = b.path; break; }
                        }
                        if (matched == null)
                        {
                            foreach (var b in bindings)
                            {
                                if (b.type == typeof(Transform) && (b.path == leaf || b.path.EndsWith("/" + leaf, System.StringComparison.Ordinal)))
                                { matched = b.path; break; }
                            }
                        }
                        if (matched != null) break;
                    }
                    if (matched == null) return false;

                    bool hasPos = false, hasRot = false, hasEuler = false;
                    Vector3 euler = Vector3.zero;
                    foreach (var b in bindings)
                    {
                        if (b.type != typeof(Transform) || b.path != matched) continue;
                        var curve = AnimationUtility.GetEditorCurve(clip, b);
                        if (curve == null || curve.length == 0) continue;
                        float v = curve.Evaluate(0f);
                        switch (b.propertyName)
                        {
                            case "m_LocalPosition.x": pos.x = v; hasPos = true; break;
                            case "m_LocalPosition.y": pos.y = v; hasPos = true; break;
                            case "m_LocalPosition.z": pos.z = v; hasPos = true; break;
                            case "m_LocalRotation.x": rot.x = v; hasRot = true; break;
                            case "m_LocalRotation.y": rot.y = v; hasRot = true; break;
                            case "m_LocalRotation.z": rot.z = v; hasRot = true; break;
                            case "m_LocalRotation.w": rot.w = v; hasRot = true; break;
                            case "localEulerAnglesRaw.x": euler.x = v; hasEuler = true; break;
                            case "localEulerAnglesRaw.y": euler.y = v; hasEuler = true; break;
                            case "localEulerAnglesRaw.z": euler.z = v; hasEuler = true; break;
                        }
                    }
                    if (!hasRot && hasEuler) { rot = Quaternion.Euler(euler); hasRot = true; }
                    float magSq = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
                    rot = magSq > 1e-6f ? rot.normalized : Quaternion.identity;
                    return hasPos || hasRot;
                }

                bool gOk = TrySampleBone("Skeleton/ik_hand_gun", "Skeleton/WeaponBone", out rest.gunPos, out rest.gunRot);
                bool lOk = TrySampleBone("Skeleton/IK_Hand_Left", null, out rest.handLPos, out rest.handLRot);
                bool rOk = TrySampleBone("Skeleton/IK_Hand_Right", null, out rest.handRPos, out rest.handRRot);
                rest.valid = gOk && lOk && rOk;
                if (rest.valid)
                    Debug.Log($"[LR2] Baked reload rest pose for {id} ({clip.name}): gunPos={rest.gunPos:F3} gunRot={rest.gunRot.eulerAngles:F1}");
                else
                    Debug.LogWarning($"[LR2] Incomplete reload rest pose for {id} ({clip.name}): gun={gOk} L={lOk} R={rOk}");
            }

            rootProp.FindPropertyRelative("valid").boolValue = rest.valid;
            rootProp.FindPropertyRelative("gunPos").vector3Value = rest.gunPos;
            rootProp.FindPropertyRelative("gunRot").quaternionValue = rest.gunRot;
            rootProp.FindPropertyRelative("handLPos").vector3Value = rest.handLPos;
            rootProp.FindPropertyRelative("handLRot").quaternionValue = rest.handLRot;
            rootProp.FindPropertyRelative("handRPos").vector3Value = rest.handRPos;
            rootProp.FindPropertyRelative("handRRot").quaternionValue = rest.handRRot;
        }

        // ------------------------------------------------------------------ textured AK-12 PBR materials (object-space triplanar)
        private static void ApplyWeaponMaterials(GameObject weaponGo)
        {
            const string leartesTex = "Assets/LeartesStudios/AbandonedFactory/HDRP/Art/Textures";
            const string matOut = "Assets/LR2/Materials/Weapons";
            EnsureFolder(matOut + "/Masks");

            var steelBc = AssetDatabase.LoadAssetAtPath<Texture2D>($"{leartesTex}/Steel/T_Steel_BC.TGA");
            var steelN = AssetDatabase.LoadAssetAtPath<Texture2D>($"{leartesTex}/Steel/T_Steel_N.TGA");
            var steelOrm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{leartesTex}/Steel/T_Steel_ORM.TGA");

            var milledBc = AssetDatabase.LoadAssetAtPath<Texture2D>($"{leartesTex}/Tilemetal/Mid/T_MetalplaneMid_BC.TGA");
            var milledN = AssetDatabase.LoadAssetAtPath<Texture2D>($"{leartesTex}/Tilemetal/Mid/T_MetalplaneMid_N.TGA");
            var milledOrm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{leartesTex}/Tilemetal/Mid/T_MetalplaneMid_ORM.TGA");

            var polyBc = AssetDatabase.LoadAssetAtPath<Texture2D>($"{leartesTex}/Asphalt2/T_Asphalt2_BC.TGA");
            var polyN = AssetDatabase.LoadAssetAtPath<Texture2D>($"{leartesTex}/Asphalt2/T_Asphalt2_N.TGA");
            var polyArd = AssetDatabase.LoadAssetAtPath<Texture2D>($"{leartesTex}/Asphalt2/T_Asphalt2_ARD.TGA");

            var matReceiver = WeaponTexMat($"{matOut}/M_AK12_Receiver.mat", steelBc, steelN,
                WeaponMaskMap(steelOrm, false, $"{matOut}/Masks/T_Steel_Mask.png"),
                new Color(0.58f, 0.62f, 0.66f), 0.42f, 0.68f, 0.38f, 0.64f, 0.85f, 3.5f);
            var matMilled = WeaponTexMat($"{matOut}/M_AK12_Milled.mat", milledBc ?? steelBc, milledN ?? steelN,
                WeaponMaskMap(milledOrm ?? steelOrm, false, $"{matOut}/Masks/T_Milled_Mask.png"),
                new Color(0.72f, 0.75f, 0.78f), 0.50f, 0.76f, 0.42f, 0.70f, 0.80f, 3.5f);
            var polyMask = WeaponMaskMap(steelOrm ?? polyArd, steelOrm == null, $"{matOut}/Masks/T_Polymer_Mask.png");
            var matPolymer = WeaponTexMat($"{matOut}/M_AK12_Polymer.mat", steelBc ?? polyBc, steelN ?? polyN, polyMask,
                new Color(0.36f, 0.38f, 0.40f), 0.04f, 0.12f, 0.26f, 0.44f, 0.30f, 2.5f);
            var matPolyMag = WeaponTexMat($"{matOut}/M_AK12_PolymerMag.mat", steelBc ?? polyBc, steelN ?? polyN, polyMask,
                new Color(0.42f, 0.40f, 0.38f), 0.04f, 0.14f, 0.28f, 0.46f, 0.30f, 2.5f);
            var matWindow = SaveMaterial(NewLit(new Color(0.32f, 0.25f, 0.16f), 0.78f, 0.20f), $"{matOut}/M_AK12_MagWindow.mat");
            var matFollower = SaveMaterial(NewLit(new Color(0.85f, 0.22f, 0.08f), 0.55f, 0.25f), $"{matOut}/M_AK12_Follower.mat");

            foreach (var r in weaponGo.GetComponentsInChildren<Renderer>(true))
            {
                bool isOptic = false;
                for (Transform t = r.transform; t != null && t != weaponGo.transform; t = t.parent)
                {
                    string tn = t.name.ToLowerInvariant();
                    if (tn.Contains("holosight") || tn.Contains("scope") || tn.Contains("optic") || tn.Contains("reticle") || tn.Contains("glass") || tn.Contains("lens"))
                    {
                        isOptic = true;
                        break;
                    }
                }
                if (isOptic) continue;

                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    string n = mats[i].name.ToLowerInvariant();
                    if (n.Contains("sight") || n.Contains("holo") || n.Contains("reticle") || n.Contains("lens") || n.Contains("brass") || n.Contains("bullet") || n.Contains("primer"))
                        continue;
                    Material rep = (n.Contains("milled") || n.Contains("metal bright")) ? matMilled
                        : (n.Contains("finish") || n.Contains("metal dark") || n.Contains("metal")) ? matReceiver
                        : n.Contains("follower") ? matFollower
                        : n.Contains("translucent") ? matWindow
                        : n.Contains("mag") ? matPolyMag
                        : (n.Contains("polymer") || n.Contains("plastic")) ? matPolymer
                        : null;
                    if (rep != null)
                    {
                        mats[i] = rep;
                        changed = true;
                    }
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        private static Material WeaponTexMat(string path, Texture2D bc, Texture2D norm, Texture2D mask, Color tint,
            float metalMin, float metalMax, float smoothMin, float smoothMax, float normScale, float triScale)
        {
            var m = new Material(HdrpLitShader);
            m.SetColor("_BaseColor", tint);
            if (bc != null) m.SetTexture("_BaseColorMap", bc);
            if (norm != null)
            {
                m.SetTexture("_NormalMap", norm);
                m.SetFloat("_NormalScale", normScale);
            }
            if (mask != null)
            {
                m.SetTexture("_MaskMap", mask);
                m.SetFloat("_MetallicRemapMin", metalMin);
                m.SetFloat("_MetallicRemapMax", metalMax);
                m.SetFloat("_AORemapMin", 0.2f);
                m.SetFloat("_AORemapMax", 1f);
                m.SetFloat("_SmoothnessRemapMin", smoothMin);
                m.SetFloat("_SmoothnessRemapMax", smoothMax);
            }
            else
            {
                m.SetFloat("_Metallic", (metalMin + metalMax) * 0.5f);
                m.SetFloat("_Smoothness", (smoothMin + smoothMax) * 0.5f);
            }
            m.SetFloat("_UVBase", 5f); // Triplanar
            m.SetFloat("_ObjectSpaceUVMapping", 1f); // Locked to weapon local space so grain never swims in first person
            m.SetFloat("_TexWorldScale", triScale);
            HDMaterial.ValidateMaterial(m);
            return SaveMaterial(m, path);
        }

        private static Texture2D WeaponMaskMap(Texture2D src, bool ard, string outPath)
        {
            if (src == null) return null;
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            if (existing != null) return existing;
            int w = Mathf.Min(src.width, 512), h = Mathf.Min(src.height, 512);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var read = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            read.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var px = read.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                px[i] = new Color32(ard ? (byte)0 : c.b, c.r, ard ? c.b : (byte)0, (byte)(255 - c.g));
            }
            read.SetPixels32(px);
            read.Apply();
            EnsureFolder(Path.GetDirectoryName(outPath).Replace('\\', '/'));
            File.WriteAllBytes(outPath, read.EncodeToPNG());
            Object.DestroyImmediate(read);
            AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceUpdate);
            var imp = (TextureImporter)AssetImporter.GetAtPath(outPath);
            if (imp != null)
            {
                imp.sRGBTexture = false;
                imp.mipmapEnabled = true;
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        // ------------------------------------------------------------------ SWAT rig reload baking & AnimatorProfile setup
        private static FPSAnimationAsset ConfigureSwatAnimationRigAndReload()
        {
            // Compute exact pistol-grip delta between AA_Swat_Rifle_Idle's RightHand and the AK-12 pistol grip mesh.
            Vector3 handGripShift = new Vector3(-0.012f, -0.042f, -0.095f);
            Quaternion handGripRot = Quaternion.Euler(-8f, 12f, -14f);
            CalibrateRightHandPistolGrip(ref handGripShift, ref handGripRot);

            // 1. Update Swat AnimatorProfile sub-assets for AttachHand reload masking, right-hand pistol grip & collision layers
            var profileSubs = AssetDatabase.LoadAllAssetsAtPath(SwatProfilePath);
            if (profileSubs != null)
            {
                foreach (var sub in profileSubs)
                {
                    if (sub is CollisionLayerSettings colLayer)
                    {
                        colLayer.layerMask = LR2Layers.EnvironmentMask;
                        colLayer.barrelLength = 0.45f;
                        colLayer.rayStartOffset = 0.12f;
                        colLayer.smoothingSpeed = 10f;
                        colLayer.useSecondaryPose = false;
                        colLayer.primaryPose = new KTransform(new Vector3(0.04f, 0.08f, -0.04f), Quaternion.Euler(-22f, -18f, 10f));
                        EditorUtility.SetDirty(colLayer);
                    }
                    else if (sub is AttachHandLayerSettings attachLayer)
                    {
                        attachLayer.overridePoseWeight = 1f;
                        attachLayer.curveBlending = new List<CurveBlend>
                        {
                            new CurveBlend
                            {
                                name = "MaskAttachHand",
                                mode = ECurveBlendMode.Mask,
                                clampMin = 0f,
                                source = ECurveSource.Playables
                            }
                        };
                        EditorUtility.SetDirty(attachLayer);
                    }
                    else if (sub is PoseSamplerLayerSettings poseLayer)
                    {
                        poseLayer.defaultWeaponPose = new KTransform(new Vector3(0.165f, 1.431f, 0.494f), Quaternion.identity);
                        poseLayer.overwriteRoot = true;
                        poseLayer.overwriteWeaponBone = true;
                        EditorUtility.SetDirty(poseLayer);
                    }
                    else if (sub is ViewLayerSettings viewLayer)
                    {
                        viewLayer.ikHandGun.pose = new KTransform(new Vector3(0.028f, 0.046f, -0.045f), Quaternion.identity);
                        viewLayer.ikHandRight = new KPose
                        {
                            element = new KRigElement(47, "IK RightHand"),
                            pose = new KTransform(handGripShift, handGripRot),
                            space = ESpaceType.ParentBoneSpace,
                            modifyMode = EModifyMode.Add
                        };
                        EditorUtility.SetDirty(viewLayer);
                    }
                }
            }

            // 2. Build SWAT upper-body AvatarMask including WeaponBone
            var swatMask = BuildSwatUpperBodyMask();

            // 3. Bake SWAT-compatible reload clip & FPSAnimationAsset
            var reloadClip = BuildSwatReloadClip();
            var demoAsset = AssetDatabase.LoadAssetAtPath<FPSAnimationAsset>(DemoReloadAssetPath);
            var swatRig = AssetDatabase.LoadAssetAtPath<KRig>(SwatRigPath);
            string assetPath = DataDir + "/Weapons/AA_LR2_AK12_Reload_Swat.asset";
            FPSAnimationAsset animAsset;
            if (demoAsset != null) animAsset = SaveAsset(Object.Instantiate(demoAsset), assetPath);
            else animAsset = LoadOrCreate<FPSAnimationAsset>(assetPath);

            animAsset.rigAsset = swatRig;
            if (reloadClip != null) animAsset.clip = reloadClip;
            if (swatMask != null) animAsset.mask = swatMask;
            animAsset.isAdditive = false;
            // Drive left-hand detach smoothly over the full ~2.45s procedural magazine swap & charging handle sequence.
            if (animAsset.curves == null) animAsset.curves = new List<AnimCurve>();
            animAsset.curves.RemoveAll(c => c.name == "WeaponBoneWeight" || c.name == "MaskAttachHand");
            animAsset.curves.Add(new AnimCurve
            {
                name = "MaskAttachHand",
                curve = new AnimationCurve(
                    new Keyframe(0.00f, 0f),
                    new Keyframe(0.05f, 1f),
                    new Keyframe(0.82f, 1f),
                    new Keyframe(0.95f, 0f),
                    new Keyframe(1.00f, 0f))
            });
            EditorUtility.SetDirty(animAsset);
            return animAsset;
        }

        /// <summary>
        /// D59: wall/obstacle tuck for the humanoid Kinemation profiles LR2 links (the SWAT-profile tuning in
        /// ConfigureSwatAnimationRigAndReload is unused with the Kinemation driver). The demo rifle pose raises the muzzle
        /// 85 degrees, which swung the rifle across the view at every wall once D57 stopped walking from masking the layer
        /// (C21: 73 degrees "pressed into the wall"); the demo pistol raycast layer 6, which LR2 never uses, so it ignored walls.
        /// A COD-like tuck instead: muzzle up and slightly in, pulled back a few centimetres.
        /// </summary>
        /// <summary>
        /// D60: materials for effects that are built at runtime (fire hazards, atmosphere mist, barrel smoke, muzzle heat and
        /// heat haze), saved under Resources so player builds keep them. See LR2FxLibrary for why runtime-made HDRP/Unlit
        /// transparent materials turned into opaque boxes in builds. Uses the Unity Particle Pack flipbooks when installed.
        /// </summary>
        private static void BuildRuntimeFxLibrary(Texture2D sparkTex, Texture2D softDot, Texture2D puff)
        {
            const string res = "Assets/LR2/Resources/LR2/FX";
            const string pack = "Assets/UnityTechnologies/ParticlePack/EffectExamples";
            var flameSheet = AssetDatabase.LoadAssetAtPath<Texture2D>(pack + "/Fire & Explosion Effects/Textures/Explosion.tif");
            var steamSheet = AssetDatabase.LoadAssetAtPath<Texture2D>(pack + "/Smoke & Steam Effects/Textures/smokeysteam.tif");
            // B87: the Particle Pack smoke sheet is RGB on black but Unity imports it without alpha. Use the shader's
            // luminance-to-alpha mode on blended smoke materials so the black card background stays transparent.
            Texture2D smokeTex = steamSheet != null ? steamSheet : puff;
            Vector2Int smokeTiles = steamSheet != null ? new Vector2Int(8, 8) : Vector2Int.one;

            Material SoftSmoke(Texture2D texture)
            {
                var material = NewParticle(texture, Color.white, 1f, false);
                if (material.HasProperty("_LuminanceToAlpha")) material.SetFloat("_LuminanceToAlpha", 1f);
                // Smoke flipbook cells overlap; cap each sheet's alpha so the stack reads as a soft plume, not a solid card.
                if (material.HasProperty("_LuminanceAlphaScale")) material.SetFloat("_LuminanceAlphaScale", 0.25f);
                return material;
            }

            var lib = LoadOrCreate<LR2FxLibrary>("Assets/LR2/Resources/LR2/FXLibrary.asset");
            lib.fire = SaveMaterial(NewParticle(flameSheet != null ? flameSheet : sparkTex, new Color(1f, 0.7f, 0.42f),
                flameSheet != null ? 2.4f : 2.6f, true), res + "/M_FX_Fire.mat"); // 2.4: luminance alpha dims the mid-tones
            lib.fireTiles = flameSheet != null ? new Vector2Int(6, 6) : Vector2Int.one;
            lib.smoke = SaveMaterial(SoftSmoke(smokeTex), res + "/M_FX_Smoke.mat");
            lib.smokeTiles = smokeTiles;
            lib.embers = SaveMaterial(NewParticle(sparkTex, new Color(1f, 0.72f, 0.38f), 3.2f, true), res + "/M_FX_Spark.mat");
            lib.mist = SaveMaterial(SoftSmoke(smokeTex), res + "/M_FX_Mist.mat");
            lib.mistTiles = smokeTiles;
            lib.barrelSmoke = SaveMaterial(SoftSmoke(smokeTex), res + "/M_FX_BarrelSmoke.mat");
            lib.barrelSmokeTiles = smokeTiles;
            lib.muzzleHeat = SaveMaterial(NewParticle(softDot, new Color(1f, 0.12f, 0.015f, 0.2f), 1f, true), res + "/M_FX_MuzzleHeat.mat");
            lib.fireGlow = SaveMaterial(NewParticle(softDot, Color.white, 1.35f, true), res + "/M_FX_FireGlow.mat");
            // B87 soft particles: fade near geometry so flames and smoke do not cut hard lines into barrels and walls
            foreach (var (pm, soft) in new[] { (lib.fire, 0.18f), (lib.smoke, 0.45f), (lib.embers, 0.05f), (lib.fireGlow, 0.35f), (lib.mist, 0.8f), (lib.barrelSmoke, 0.06f) })
                if (pm != null && pm.HasProperty("_SoftFade")) { pm.SetFloat("_SoftFade", soft); EditorUtility.SetDirty(pm); }

            // B100 (user 11-19 00:13: the CTF zone effect "is HORRIBLE. use something sharp, that adheres to the ground, not just
            // floats above holes"): crisp ring textures drawn as HDRP decals, so the marking lies on the ground it is projected on
            lib.zoneRingOuter = SaveMaterial(NewZoneDecal(ZoneRingTexture(res + "/T_ZoneRingOuter", 2048, 0.950f, 0.985f, 60, 0.6f, 0.11f)),
                res + "/M_FX_ZoneRingOuter.mat");
            lib.zoneRingCore = SaveMaterial(NewZoneDecal(ZoneRingTexture(res + "/T_ZoneRingCore", 1024, 0.885f, 0.975f, 0, 1f, 0.18f)),
                res + "/M_FX_ZoneRingCore.mat");

            // HDRP distortion-only template: the heat haze needs HDRP's distortion pass, so it stays on HDRP/Unlit, but as a
            // validated asset its shader variant is part of the build
            var hdUnlit = Shader.Find("HDRP/Unlit");
            if (hdUnlit != null)
            {
                var haze = new Material(hdUnlit);
                void F(string p, float v) { if (haze.HasProperty(p)) haze.SetFloat(p, v); }
                F("_SurfaceType", 1f);
                F("_BlendMode", 0f);
                F("_DistortionEnable", 1f);
                F("_DistortionOnly", 1f);
                F("_DistortionDepthTest", 1f);
                F("_DistortionVectorScale", 2f);
                F("_DistortionVectorBias", -1f);
                F("_DoubleSidedEnable", 1f);
                HDMaterial.ValidateMaterial(haze);
                lib.heatHaze = SaveMaterial(haze, res + "/M_FX_HeatHaze.mat");
            }
            EditorUtility.SetDirty(lib);
            Debug.Log($"[LR2] Runtime FX library: fire={(flameSheet != null ? "ParticlePack flipbook 6x6" : "spark texture")} " +
                      $"smoke={(steamSheet != null ? "ParticlePack steam flipbook 8x8" : "LR2 puff")}");
        }

        private static void TuneHumanoidCollision(FPSAnimatorProfile profile, bool pistol)
        {
            if (profile == null) return;
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(profile)))
            {
                if (!(sub is CollisionLayerSettings col)) continue;
                col.layerMask = LR2Layers.EnvironmentMask;
                col.useSecondaryPose = false;
                col.targetSpace = ESpaceType.BoneSpace;
                col.smoothingSpeed = 10f;
                if (pistol)
                {
                    col.rayStartOffset = 0.05f;
                    col.barrelLength = 0.32f;
                    col.primaryPose = new KTransform(new Vector3(0f, 0.03f, -0.07f), Quaternion.Euler(-20f, -6f, 0f));
                }
                else
                {
                    col.rayStartOffset = 0.12f;
                    col.barrelLength = 0.5f;
                    col.primaryPose = new KTransform(new Vector3(0.02f, 0.035f, -0.06f), Quaternion.Euler(-25f, -12f, 8f));
                }
                // Kinemation's CollisionLayerJob ignores useSecondaryPose and takes the secondary pose whenever the mouse pitch
                // input is above zero; the demo rifle's secondary is a 90-degree yaw (C23: the rifle lay sideways across the
                // view at 74 degrees against a wall) and the pistol's drops it 20 cm. One tuck for both.
                col.secondaryPose = col.primaryPose;
                // B125 (user 11-19: "it's happening during ADS, when I hit other obstacles"; reproduced by -lr2AdsWallCheck:
                // pressed into a wall in ADS the full tuck swung the rifle 23 deg off the sight line, top rail in view). The tuck
                // pivots about the weapon bone, which sits at the eye in ADS, so the whole gun crosses the view. Keep the existing
                // FullBodyWeight mask and add the ADS weight, like the sway layer: at full ADS 12 % of the tuck is left (about
                // 3 deg and 7 mm), so the sights stay on target and the barrel just meets the wall.
                var blends = new List<CurveBlend>();
                if (col.curveBlending != null)
                    foreach (var b in col.curveBlending)
                        if (b.name != FPSANames.AimingWeight) blends.Add(b);
                blends.Add(new CurveBlend { name = FPSANames.AimingWeight, mode = ECurveBlendMode.Mask, clampMin = 0.12f, source = ECurveSource.Input });
                col.curveBlending = blends;
                EditorUtility.SetDirty(col);
            }
        }

        /// <summary>
        /// B117 (user 11-19 "can the turn in place animation be faster and more seemless?"). Kinemation's turn layer let the
        /// upper body twist 90 deg before the feet reacted, then unwound the counter-rotation over 1 s (turnSpeed 1) on an
        /// ease-in-out curve: a long lag, then a slow shuffle. Now the feet follow at 50 deg and catch up in about 0.45 s on an
        /// ease-out curve (quick start, soft landing). The turn clips speed up to match (<see cref="ConfigureFastTurns"/>).
        /// Idempotent: safe on every build.
        /// </summary>
        private static void TuneHumanoidTurn(FPSAnimatorProfile profile)
        {
            if (profile == null) return;
            int n = 0;
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(profile)))
            {
                if (!(sub is TurnLayerSettings turn)) continue;
                // reverted to Kinemation's values (user 11-19: severe animation bugs in AI vs AI; remove anything forced)
                turn.angleThreshold = 90f;
                turn.turnSpeed = 1f;
                turn.turnCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
                EditorUtility.SetDirty(turn);
                n++;
            }
            Debug.Log($"[LR2] B117 turn in place: {n} turn layer(s) in {profile.name} at Kinemation defaults (90 deg, speed 1, ease-in-out)");
        }

        /// <summary>B117: the TurnLeft/TurnRight clips of the driver controller play at 1.7x, blend in over 0.08 s and out over at most 0.15 s.</summary>
        private static void ConfigureFastTurns(UnityEditor.Animations.AnimatorController ctrl)
        {
            int states = 0, entries = 0;
            static bool IsTurn(UnityEditor.Animations.AnimatorStateTransition t) =>
                t.conditions.Any(c => c.parameter == "TurnLeft" || c.parameter == "TurnRight");
            void Walk(UnityEditor.Animations.AnimatorStateMachine sm, bool inTurn)
            {
                foreach (var cs in sm.states)
                {
                    if (inTurn)
                    {
                        cs.state.speed = 1f;
                        states++;

                    }
                    foreach (var t in cs.state.transitions)
                        if (IsTurn(t)) { t.duration = 0.12f; entries++; }
                }
                foreach (var t in sm.anyStateTransitions)
                    if (IsTurn(t)) { t.duration = 0.12f; entries++; }
                foreach (var sub in sm.stateMachines) Walk(sub.stateMachine, inTurn || sub.stateMachine.name.StartsWith("Turn"));
            }
            foreach (var layer in ctrl.layers)
                if (layer.name == "TurnInPlace") Walk(layer.stateMachine, false);
            Debug.Log($"[LR2] B117 turn in place: {states} turn clip state(s) at 1x, {entries} turn entries at 0.12 s (reverted from B117)");
        }

        private const string SwayTunedLabel = "LR2SwayTuned";

        private static void TuneHumanoidSway(FPSAnimatorProfile profile)
        {
            if (profile == null) return;
            // B135 (user 11-20: "this is how we can modify the sway of ads"): the defaults below go in once (a fresh
            // Kinemation import has no label); after that the Sway layer is the developer's to edit in Kinemation's
            // profile inspector (Edit Layer), and rebuilds leave it alone instead of resetting it.
            var labels = new List<string>(AssetDatabase.GetLabels(profile));
            if (labels.Contains(SwayTunedLabel)) return;
            labels.Add(SwayTunedLabel);
            AssetDatabase.SetLabels(profile, labels.ToArray());
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(profile)))
            {
                if (!(sub is SwayLayerSettings sway)) continue;

                // B91: Kinemation's sway runs on the weapon bone. Mask it down smoothly with the actual ADS input,
                // and cut its vertical position spring response so the rifle keeps its authored sight alignment.
                sway.curveBlending = new List<CurveBlend>
                {
                    new CurveBlend
                    {
                        name = FPSANames.AimingWeight,
                        mode = ECurveBlendMode.Mask,
                        clampMin = 0.22f,
                        source = ECurveSource.Input
                    }
                };

                var movePosition = sway.moveSwayPositionSpring;
                movePosition.scale.y = -0.15f;
                sway.moveSwayPositionSpring = movePosition;

                var aimPosition = sway.aimSwayPositionSpring;
                aimPosition.scale.y = -0.12f;
                sway.aimSwayPositionSpring = aimPosition;

                EditorUtility.SetDirty(sway);
            }
        }

        private static void CalibrateRightHandPistolGrip(ref Vector3 shiftLocal, ref Quaternion rotOffset)
        {
            var swatPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SwatPrefab);
            var idleAsset = AssetDatabase.LoadAssetAtPath<FPSAnimationAsset>(
                DemoDir + "/Tutorials/Tutorial #1 - Basic Setup/Swat_FPSAnimator/AA_Swat_Rifle_Idle.asset");
            var akPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDir + "/Weapons/LR2_AK12.prefab")
                           ?? AssetDatabase.LoadAssetAtPath<GameObject>(SwatAk12);
            if (swatPrefab == null || idleAsset == null || idleAsset.clip == null || akPrefab == null) return;

            GameObject swatGo = null, akGo = null;
            try
            {
                swatGo = (GameObject)PrefabUtility.InstantiatePrefab(swatPrefab);
                akGo = (GameObject)PrefabUtility.InstantiatePrefab(akPrefab);
                swatGo.hideFlags = HideFlags.HideAndDontSave;
                akGo.hideFlags = HideFlags.HideAndDontSave;
                swatGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                var anim = swatGo.GetComponent<Animator>();
                if (anim == null || !anim.isHuman) return;
                idleAsset.clip.SampleAnimation(swatGo, 0f);

                Transform rHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
                Transform rMid = anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
                if (rHand == null) return;
                Vector3 palmOffsetFromWrist = rMid != null ? (rMid.position - rHand.position) * 0.5f : rHand.up * 0.04f;

                // Place AK-12 at PoseSampler's defaultWeaponPose + ViewLayer ikHandGun.
                Vector3 wepCompPos = new Vector3(0.165f, 1.431f, 0.494f);
                akGo.transform.SetPositionAndRotation(wepCompPos, Quaternion.identity);

                // Scan AK-12 vertices in weapon local space to find the pistol grip core below the trigger guard & behind magwell.
                var smr = akGo.GetComponentInChildren<SkinnedMeshRenderer>(true);
                Vector3 gripLocal = new Vector3(0.01f, -0.095f, -0.135f);
                if (smr != null && smr.sharedMesh != null)
                {
                    var baked = new Mesh();
                    smr.BakeMesh(baked);
                    var verts = baked.vertices;
                    Vector3 sum = Vector3.zero;
                    int cnt = 0;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 worldV = smr.transform.TransformPoint(verts[i]);
                        Vector3 lv = akGo.transform.InverseTransformPoint(worldV);
                        if (Mathf.Abs(lv.x) < 0.04f && lv.y > -0.145f && lv.y < -0.055f && lv.z > -0.205f && lv.z < -0.075f)
                        {
                            sum += lv;
                            cnt++;
                        }
                    }
                    Object.DestroyImmediate(baked);
                    if (cnt > 12) gripLocal = sum / cnt;
                }

                // Target wrist position places the palm center slightly right/down of the pistol grip centerline.
                Vector3 targetWristWorld = akGo.transform.TransformPoint(gripLocal) + new Vector3(0.016f, -0.008f, -0.018f) - palmOffsetFromWrist;
                Vector3 rawDelta = akGo.transform.InverseTransformVector(targetWristWorld - rHand.position);
                // Clamp delta within safe anatomical reach around PoseSampler's idle hand pose.
                shiftLocal = new Vector3(
                    Mathf.Clamp(rawDelta.x, -0.045f, 0.035f),
                    Mathf.Clamp(rawDelta.y, -0.075f, 0.030f),
                    Mathf.Clamp(rawDelta.z, -0.140f, 0.040f));
                rotOffset = Quaternion.Euler(-6f, 10f, -10f);

                File.WriteAllText("Library/LR2_GripCalibration.txt",
                    $"rHandInWep={akGo.transform.InverseTransformPoint(rHand.position):F4} gripLocal={gripLocal:F4} rawDelta={rawDelta:F4} appliedShift={shiftLocal:F4}\n");
            }
            finally
            {
                if (swatGo != null) Object.DestroyImmediate(swatGo);
                if (akGo != null) Object.DestroyImmediate(akGo);
            }
        }

        private static AvatarMask BuildSwatUpperBodyMask()
        {
            var srcMask = AssetDatabase.LoadAssetAtPath<AvatarMask>(SwatMaskPath);
            if (srcMask == null) return null;

            static void EnableWeaponBoneOnMask(AvatarMask m)
            {
                for (int i = 0; i < m.transformCount; i++)
                {
                    if (m.GetTransformPath(i) == "mixamorig:Hips/WeaponBone")
                    {
                        m.SetTransformActive(i, true);
                        return;
                    }
                }
                int idx = m.transformCount;
                m.transformCount = idx + 1;
                m.SetTransformPath(idx, "mixamorig:Hips/WeaponBone");
                m.SetTransformActive(idx, true);
            }

            EnableWeaponBoneOnMask(srcMask);
            EditorUtility.SetDirty(srcMask);

            var mask = Object.Instantiate(srcMask);
            EnableWeaponBoneOnMask(mask);
            return SaveAsset(mask, DataDir + "/Weapons/LR2_Swat_UpperBody.mask");
        }

        private static AnimationClip BuildSwatReloadClip()
        {
            var srcClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(HumanoidReloadClip);
            var idleAsset = AssetDatabase.LoadAssetAtPath<FPSAnimationAsset>(
                DemoDir + "/Tutorials/Tutorial #1 - Basic Setup/Swat_FPSAnimator/AA_Swat_Rifle_Idle.asset");
            var humPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HumanoidPrefab);
            var swatPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SwatPrefab);
            if (srcClip == null || humPrefab == null || swatPrefab == null)
            {
                Debug.LogWarning("[LR2] Could not bake Swat reload clip (missing source asset).");
                return null;
            }

            GameObject humGo = null, swatGo = null;
            try
            {
                humGo = (GameObject)PrefabUtility.InstantiatePrefab(humPrefab);
                swatGo = (GameObject)PrefabUtility.InstantiatePrefab(swatPrefab);
                humGo.hideFlags = HideFlags.HideAndDontSave;
                swatGo.hideFlags = HideFlags.HideAndDontSave;
                humGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                swatGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                var humAnim = humGo.GetComponent<Animator>();
                var swatAnim = swatGo.GetComponent<Animator>();
                Avatar humAvatar = humAnim != null ? humAnim.avatar : null;
                Avatar swatAvatar = swatAnim != null ? swatAnim.avatar : null;
                if (humAvatar == null || swatAvatar == null)
                {
                    Debug.LogWarning("[LR2] Could not bake Swat reload clip (missing humanoid Animator/Avatar).");
                    return null;
                }

                Transform FindByName(Transform root, string n)
                {
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        if (t.name == n) return t;
                    return null;
                }

                var humWep = FindByName(humGo.transform, "WeaponBone");
                var swatWep = FindByName(swatGo.transform, "WeaponBone");
                var swatHips = swatAnim.GetBoneTransform(HumanBodyBones.Hips);
                Vector3 restHipsPos = swatHips != null ? swatHips.localPosition : Vector3.zero;
                Quaternion restHipsRot = swatHips != null ? swatHips.localRotation : Quaternion.identity;

                // Collect all upper-body/arm/hand/finger mixamorig bones BEFORE destroying Animators.
                var swatBones = new List<(Transform t, string path)>();
                var spine = FindByName(swatGo.transform, "mixamorig:Spine");
                if (spine != null)
                {
                    foreach (var t in spine.GetComponentsInChildren<Transform>(true))
                    {
                        if (!t.name.StartsWith("mixamorig:")) continue;
                        swatBones.Add((t, AnimationUtility.CalculateTransformPath(t, swatGo.transform)));
                    }
                }

                // 1) Sample SWAT idle pose while swatAnim is still attached (FBX humanoid clip requires Animator), then strip Animators so SampleAnimation drives Generic Skeleton/Hips paths directly on humGo.
                Vector3 swatBaseCompPos = new Vector3(0.165f, 1.431f, 0.494f);
                Quaternion swatBaseCompRot = Quaternion.identity;
                if (idleAsset != null && idleAsset.clip != null)
                {
                    idleAsset.clip.SampleAnimation(swatGo, 0f);
                }
                if (swatHips != null)
                {
                    swatHips.localPosition = restHipsPos;
                    swatHips.localRotation = restHipsRot;
                }
                var idleBoneRot = new Quaternion[swatBones.Count];
                for (int b = 0; b < swatBones.Count; b++) idleBoneRot[b] = swatBones[b].t.localRotation;

                Object.DestroyImmediate(humAnim);
                Object.DestroyImmediate(swatAnim);

                Vector3 idleWepWorldPos = swatGo.transform.TransformPoint(swatBaseCompPos);
                Quaternion idleWepWorldRot = swatGo.transform.rotation * swatBaseCompRot;
                Vector3 wepSpineLocalPos = spine != null ? spine.InverseTransformPoint(idleWepWorldPos) : Vector3.zero;
                Quaternion wepSpineLocalRot = spine != null ? Quaternion.Inverse(spine.rotation) * idleWepWorldRot : Quaternion.identity;

                int steps = Mathf.Max(2, Mathf.CeilToInt(srcClip.length * 30f));
                var rotCurves = new AnimationCurve[swatBones.Count][];
                for (int b = 0; b < swatBones.Count; b++)
                    rotCurves[b] = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
                var prevBoneQ = new Quaternion[swatBones.Count];
                var retargetBoneRot0 = new Quaternion[swatBones.Count];

                var wepPosCurves = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
                var wepRotCurves = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
                Quaternion prevWepQ = Quaternion.identity;
                Vector3 humWep0CompPos = Vector3.zero;
                Quaternion humWep0CompRot = Quaternion.identity;

                // Track motion amplitude to log proof that HumanPose retargeting moved the arms.
                float maxLeftArmAngle = 0f, maxRightArmAngle = 0f;
                int leftForeIdx = swatBones.FindIndex(x => x.t.name == "mixamorig:LeftForeArm");
                int rightForeIdx = swatBones.FindIndex(x => x.t.name == "mixamorig:RightForeArm");

                using (var humPoseH = new HumanPoseHandler(humAvatar, humGo.transform))
                using (var swatPoseH = new HumanPoseHandler(swatAvatar, swatGo.transform))
                {
                    var pose = new HumanPose();
                    for (int s = 0; s <= steps; s++)
                    {
                        float time = srcClip.length * (s / (float)steps);
                        srcClip.SampleAnimation(humGo, time);
                        humPoseH.GetHumanPose(ref pose);
                        swatPoseH.SetHumanPose(ref pose);

                        if (swatHips != null)
                        {
                            swatHips.localPosition = restHipsPos;
                            swatHips.localRotation = restHipsRot;
                        }

                        if (s == 0)
                        {
                            for (int b = 0; b < swatBones.Count; b++) retargetBoneRot0[b] = swatBones[b].t.localRotation;
                            if (humWep != null)
                            {
                                humWep0CompPos = humGo.transform.InverseTransformPoint(humWep.position);
                                humWep0CompRot = Quaternion.Inverse(humGo.transform.rotation) * humWep.rotation;
                            }
                        }

                        for (int b = 0; b < swatBones.Count; b++)
                        {
                            // Keep upper-body Mixamo bones & fingers in their calibrated SWAT rifle idle grip;
                            // AttachHandLayerJob detaches IK LeftHand smoothly for mag-out, pouch grab, mag-seat & bolt rack.
                            Quaternion q = idleBoneRot[b];
                            swatBones[b].t.localRotation = q;

                            if (b == leftForeIdx) maxLeftArmAngle = Mathf.Max(maxLeftArmAngle, Quaternion.Angle(idleBoneRot[b], q));
                            if (b == rightForeIdx) maxRightArmAngle = Mathf.Max(maxRightArmAngle, Quaternion.Angle(idleBoneRot[b], q));

                            if (s > 0 && Quaternion.Dot(prevBoneQ[b], q) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                            prevBoneQ[b] = q;
                            rotCurves[b][0].AddKey(time, q.x);
                            rotCurves[b][1].AddKey(time, q.y);
                            rotCurves[b][2].AddKey(time, q.z);
                            rotCurves[b][3].AddKey(time, q.w);
                        }

                        if (humWep != null && swatWep != null && swatWep.parent != null)
                        {
                            Vector3 humCompPos = humGo.transform.InverseTransformPoint(humWep.position);
                            Quaternion humCompRot = Quaternion.Inverse(humGo.transform.rotation) * humWep.rotation;
                            Vector3 targetCompPos = swatBaseCompPos + (humCompPos - humWep0CompPos);
                            Quaternion targetCompRot = (humCompRot * Quaternion.Inverse(humWep0CompRot)) * swatBaseCompRot;

                            // Compensate PoseSamplerJob's spinePose additive offset so intra-clip spine lean isn't doubled.
                            Vector3 spineWorldPos = spine != null ? spine.TransformPoint(wepSpineLocalPos) - idleWepWorldPos : Vector3.zero;
                            Quaternion spineWorldRot = spine != null ? Quaternion.Inverse(idleWepWorldRot) * (spine.rotation * wepSpineLocalRot) : Quaternion.identity;

                            Vector3 desiredWorldPos = swatGo.transform.TransformPoint(targetCompPos) - spineWorldPos;
                            Quaternion desiredWorldRot = swatGo.transform.rotation * targetCompRot;
                            Quaternion preSpineWorldRot = desiredWorldRot * Quaternion.Inverse(spineWorldRot);

                            Vector3 lp = swatWep.parent.InverseTransformPoint(desiredWorldPos);
                            Quaternion lr = Quaternion.Inverse(swatWep.parent.rotation) * preSpineWorldRot;

                            if (s > 0 && Quaternion.Dot(prevWepQ, lr) < 0f) lr = new Quaternion(-lr.x, -lr.y, -lr.z, -lr.w);
                            prevWepQ = lr;
                            wepPosCurves[0].AddKey(time, lp.x);
                            wepPosCurves[1].AddKey(time, lp.y);
                            wepPosCurves[2].AddKey(time, lp.z);
                            wepRotCurves[0].AddKey(time, lr.x);
                            wepRotCurves[1].AddKey(time, lr.y);
                            wepRotCurves[2].AddKey(time, lr.z);
                            wepRotCurves[3].AddKey(time, lr.w);
                        }
                    }
                }

                var outClip = new AnimationClip { name = "LR2_AK12_Reload_Swat", frameRate = 30f };
                string[] rotProps = { "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" };
                string[] posProps = { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z" };
                for (int b = 0; b < swatBones.Count; b++)
                    for (int k = 0; k < 4; k++)
                        outClip.SetCurve(swatBones[b].path, typeof(Transform), rotProps[k], rotCurves[b][k]);

                if (swatWep != null)
                {
                    string wepPath = AnimationUtility.CalculateTransformPath(swatWep, swatGo.transform);
                    for (int k = 0; k < 3; k++) outClip.SetCurve(wepPath, typeof(Transform), posProps[k], wepPosCurves[k]);
                    for (int k = 0; k < 4; k++) outClip.SetCurve(wepPath, typeof(Transform), rotProps[k], wepRotCurves[k]);
                }

                var saved = SaveAsset(outClip, DataDir + "/Weapons/LR2_AK12_Reload_Swat.anim");
                int curveCount = AnimationUtility.GetCurveBindings(saved).Length;
                Debug.Log($"[LR2] Baked SWAT AK-12 reload clip: {curveCount} curves, {saved.length:0.00}s (LForeArm max delta={maxLeftArmAngle:0.0} deg, RForeArm max delta={maxRightArmAngle:0.0} deg)");
                return saved;
            }
            finally
            {
                if (humGo != null) Object.DestroyImmediate(humGo);
                if (swatGo != null) Object.DestroyImmediate(swatGo);
            }
        }

        private static Bounds LocalBounds(GameObject go) => LocalBounds(go, false);

        private static Bounds LocalBounds(GameObject go, bool excludeAttachments)
        {
            var b = new Bounds(Vector3.zero, Vector3.zero);
            bool first = true;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer) continue;
                if (excludeAttachments && r.GetComponentInParent<BaseAttachment>(true) != null) continue;
                var rb = r.bounds;
                var corners = new[]
                {
                    rb.min, rb.max, new Vector3(rb.min.x, rb.min.y, rb.max.z), new Vector3(rb.min.x, rb.max.y, rb.min.z),
                    new Vector3(rb.max.x, rb.min.y, rb.min.z), new Vector3(rb.min.x, rb.max.y, rb.max.z),
                    new Vector3(rb.max.x, rb.min.y, rb.max.z), new Vector3(rb.max.x, rb.max.y, rb.min.z)
                };
                foreach (var c in corners)
                {
                    var l = go.transform.InverseTransformPoint(c);
                    if (first) { b = new Bounds(l, Vector3.zero); first = false; }
                    else b.Encapsulate(l);
                }
            }
            return b;
        }

        // =====================================================================================================
        // 5. Soldiers: player + bot prefabs from the Kinemation SWAT setup
        // =====================================================================================================
        /// <summary>Adds a "LocoSpeed" speed multiplier to every locomotion state of the humanoid FPS animator, so the
        /// walk cycle cadence follows the real ground speed (no more frantic legs at walking pace).</summary>
        private static void ConfigureLocomotionSpeed()
        {
            const string path = "Assets/KinemationDemo/Animations/Locomotion/FPSAnimator_Humanoid.controller";
            ConfigureLocomotionSpeed(AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(path));
        }

        /// <summary>LR2's shortened timings on a Kinemation controller: LocoSpeed cadence, fast prone/crouch, quick turn entries.</summary>
        private static void ConfigureLocomotionSpeed(UnityEditor.Animations.AnimatorController ctrl)
        {
            if (ctrl == null) { Debug.LogWarning("[LR2] Locomotion controller not found"); return; }
            bool hasParam = false;
            foreach (var p in ctrl.parameters) if (p.name == "LocoSpeed") hasParam = true;
            if (!hasParam) ctrl.AddParameter(new AnimatorControllerParameter { name = "LocoSpeed", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
            bool UsesMove(Motion m)
            {
                if (m is UnityEditor.Animations.BlendTree bt)
                {
                    if (bt.blendParameter == "MoveX" || bt.blendParameterY == "MoveY" || bt.blendParameter == "Velocity") return true;
                    foreach (var c in bt.children) if (UsesMove(c.motion)) return true;
                }
                return false;
            }
            int count = 0;
            void Walk(UnityEditor.Animations.AnimatorStateMachine sm)
            {
                foreach (var cs in sm.states)
                {
                    var st = cs.state;
                    bool isProneState = st.name.IndexOf("Prone", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    foreach (var tr in st.transitions)
                    {
                        if (isProneState || (tr.destinationState != null && tr.destinationState.name.IndexOf("Prone", System.StringComparison.OrdinalIgnoreCase) >= 0))
                            tr.duration = Mathf.Min(tr.duration, 0.16f);
                    }
                    if (!UsesMove(st.motion)) continue;
                    st.speedParameterActive = true;
                    st.speedParameter = "LocoSpeed";
                    count++;
                }
                foreach (var sub in sm.stateMachines) Walk(sub.stateMachine);
            }
            foreach (var layer in ctrl.layers) Walk(layer.stateMachine);
            ConfigureFastStances(ctrl);
            ConfigureFastTurns(ctrl);
            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            Debug.Log($"[LR2] LocoSpeed driving {count} locomotion states");
        }

        /// <summary>
        /// B90 (user 11-19 00:13: "proning should be 5x faster and smoother", crouching "more faster and seamless"). The demo
        /// chained full-length stance clips, each played to 95 %: stand -> C_StandingToCrouch -> C_CrouchToProne -> prone
        /// (about 6 s at 30 fps) and prone -> C_ProneToCrouch -> C_CrouchToStanding -> stand (about 5 s). Now going down skips
        /// the stand-to-crouch clip and plays crouch-to-prone at 3.2x, leaving it at 82 %; crouch goes to prone directly;
        /// getting up plays prone-to-crouch at 3.2x and crossfades straight to crouch or stand (about 0.9 s each way).
        /// Stand/crouch crossfades 0.2 -> 0.12 s. Idempotent: safe on every build.
        /// </summary>
        private static void ConfigureFastStances(UnityEditor.Animations.AnimatorController ctrl)
        {
            static void Collect(UnityEditor.Animations.AnimatorStateMachine sm, List<UnityEditor.Animations.AnimatorState> into)
            {
                foreach (var cs in sm.states) into.Add(cs.state);
                foreach (var sub in sm.stateMachines) Collect(sub.stateMachine, into);
            }
            foreach (var layer in ctrl.layers)
            {
                var all = new List<UnityEditor.Animations.AnimatorState>();
                Collect(layer.stateMachine, all);
                // B136: the humanoid driver names its clips *_Humanoid and its prone state ProneTree; the generic robot does not
                UnityEditor.Animations.AnimatorState S(string n) => all.Find(st => st.name == n) ?? all.Find(st => st.name == n + "_Humanoid");
                var toProne = S("C_CrouchToProne");
                if (toProne == null) continue;
                var standing = all.Find(st => st.name == "Standing" && System.Array.Exists(st.transitions, t => t.destinationState != null
                    && (t.destinationState.name == "Crouching" || t.destinationState.name.StartsWith("C_StandingToCrouch") || t.destinationState == toProne)));
                var crouching = S("Crouching");
                var prone = S("ProneTree") ?? S("Prone");
                var proneToCrouch = S("C_ProneToCrouch");
                var standToCrouch = S("C_StandingToCrouch");
                var crouchToStand = S("C_CrouchToStanding");
                if (standing == null || crouching == null || prone == null || proneToCrouch == null)
                {
                    Debug.LogWarning("[LR2] B90: stance states not found, prone/crouch speed unchanged");
                    continue;
                }
                toProne.speed = 3.2f;
                proneToCrouch.speed = 3.2f;
                if (standToCrouch != null) standToCrouch.speed = 3f;
                if (crouchToStand != null) crouchToStand.speed = 3f;

                foreach (var t in standing.transitions)
                {
                    if (t.destinationState == standToCrouch || t.destinationState == toProne)
                    {
                        t.destinationState = toProne;                // stand -> prone without the stand-to-crouch clip
                        t.hasExitTime = false; t.duration = 0.1f; t.hasFixedDuration = true;
                    }
                    else if (t.destinationState == crouching) { t.duration = 0.12f; t.hasFixedDuration = true; }
                }

                UnityEditor.Animations.AnimatorStateTransition crouchToProne = null;
                foreach (var t in crouching.transitions)
                {
                    if (t.destinationState == toProne) crouchToProne = t;
                    else if (t.destinationState == standing) { t.duration = 0.12f; t.hasFixedDuration = true; }
                }
                if (crouchToProne == null)
                {
                    crouchToProne = crouching.AddTransition(toProne);
                    crouchToProne.AddCondition(UnityEditor.Animations.AnimatorConditionMode.If, 0f, "Proning");
                }
                crouchToProne.hasExitTime = false; crouchToProne.duration = 0.1f; crouchToProne.hasFixedDuration = true;
                // first in the list: EnterProne clears Crouching in the same frame, which would also fire crouch -> stand
                var ordered = new List<UnityEditor.Animations.AnimatorStateTransition> { crouchToProne };
                foreach (var t in crouching.transitions) if (t != crouchToProne) ordered.Add(t);
                crouching.transitions = ordered.ToArray();

                foreach (var t in toProne.transitions)
                    if (t.destinationState == prone) { t.hasExitTime = true; t.exitTime = 0.82f; t.duration = 0.12f; t.hasFixedDuration = true; }
                foreach (var t in prone.transitions)
                    if (t.destinationState == proneToCrouch) { t.duration = 0.1f; t.hasFixedDuration = true; }

                // prone -> up: crossfade straight to crouch (Crouching set by ExitProne(true)) or to stand
                foreach (var t in new List<UnityEditor.Animations.AnimatorStateTransition>(proneToCrouch.transitions))
                    if (crouchToStand != null && t.destinationState == crouchToStand) proneToCrouch.RemoveTransition(t);
                UnityEditor.Animations.AnimatorStateTransition upCrouch = null, upStand = null;
                foreach (var t in proneToCrouch.transitions)
                {
                    if (t.destinationState == crouching) upCrouch = t;
                    else if (t.destinationState == standing) upStand = t;
                }
                if (upCrouch == null)
                {
                    upCrouch = proneToCrouch.AddTransition(crouching);
                    upCrouch.AddCondition(UnityEditor.Animations.AnimatorConditionMode.If, 0f, "Crouching");
                }
                if (upStand == null)
                {
                    upStand = proneToCrouch.AddTransition(standing);
                    upStand.AddCondition(UnityEditor.Animations.AnimatorConditionMode.IfNot, 0f, "Crouching");
                }
                upCrouch.hasExitTime = true; upCrouch.exitTime = 0.78f; upCrouch.duration = 0.15f; upCrouch.hasFixedDuration = true;
                upStand.hasExitTime = true; upStand.exitTime = 0.78f; upStand.duration = 0.2f; upStand.hasFixedDuration = true;
                Debug.Log("[LR2] B90 fast stances: prone ~0.9 s each way (was ~5-6 s), crouch crossfades 0.12 s");
            }
        }

        /// <summary>Enables HDRP 17.5 dynamic resolution + STP (with DLSS/FSR2 priority when vendor plugins exist) on every HDRP asset; native cameras keep allowDynamicResolution = false via UpscalerBackend.</summary>
        private static void ConfigureUpscaling()
        {
            foreach (var g in AssetDatabase.FindAssets("t:HDRenderPipelineAsset", new[] { "Assets/Settings" }))
            {
                var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(AssetDatabase.GUIDToAssetPath(g));
                if (asset == null) continue;
                var so = new SerializedObject(asset);
                var d = so.FindProperty("m_RenderPipelineSettings.dynamicResolutionSettings");
                if (d == null) continue;
                void B(string n, bool v) { var p = d.FindPropertyRelative(n); if (p != null) p.boolValue = v; }
                void F(string n, float v) { var p = d.FindPropertyRelative(n); if (p != null) p.floatValue = v; }
                void I(string n, int v) { var p = d.FindPropertyRelative(n); if (p != null) { if (p.propertyType == SerializedPropertyType.Enum) p.enumValueIndex = v; else p.intValue = v; } }
                B("enabled", true);
                B("useMipBias", true);
                B("forceResolution", false);
                F("minPercentage", 67f);
                F("maxPercentage", 100f);
                F("forcedPercentage", 77f);
                I("dynResType", (int)UnityEngine.Rendering.DynamicResolutionType.Hardware);
                I("upsampleFilter", (int)UnityEngine.Rendering.DynamicResUpscaleFilter.EdgeAdaptiveScalingUpres);
                B("DLSSUseOptimalSettings", true);
                I("DLSSPerfQualitySetting", 2);           // Quality
                var names = d.FindPropertyRelative("advancedUpscalerNames");
                if (names != null && names.isArray)
                {
                    names.arraySize = 3;
                    names.GetArrayElementAtIndex(0).stringValue = nameof(AdvancedUpscalers.DLSS);
                    names.GetArrayElementAtIndex(1).stringValue = nameof(AdvancedUpscalers.FSR2);
                    names.GetArrayElementAtIndex(2).stringValue = nameof(AdvancedUpscalers.STP);
                }
                var list = d.FindPropertyRelative("advancedUpscalersByPriority");
                if (list != null && list.isArray)
                {
                    list.arraySize = 2;
                    list.GetArrayElementAtIndex(0).intValue = (int)AdvancedUpscalers.DLSS;
                    list.GetArrayElementAtIndex(1).intValue = (int)AdvancedUpscalers.STP;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(asset);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[LR2] Upscaling: HDRP DRS enabled (DLSS -> FSR2 -> STP priority; Native cameras stay 100% via UpscalerBackend)");
        }

        private static Material[] _gear;
        private const string SwatMatDir = DemoDir + "/Meshes/Character/MixamoSwat/Materials";

        /// <summary>
        /// Team uniforms: only the blue cloth of the SWAT texture (shirt, trousers, hood) is re-dyed (woodland olive vs.
        /// desert tan) - vest, pouches, gloves, boots, helmet and goggles keep their own charcoal/black look.
        /// Face: black knit balaclava with a bone-white Ghost-style skull print (no visible human face).
        /// </summary>
        private static Material[] MakeGear()
        {
            if (_gear != null) return _gear;
            const string dir = "Assets/LR2/Materials/Characters";
            EnsureFolder(dir);
            var body = AssetDatabase.LoadAssetAtPath<Material>(SwatMatDir + "/Soldier_body1.mat");
            var head = AssetDatabase.LoadAssetAtPath<Material>(SwatMatDir + "/Soldier_head6.mat");
            _gear = new[]
            {
                DyeUniform(body, dir, "Uniform_Allied_NavyBlue", new Color(0.11f, 0.30f, 0.72f), Color.clear, Color.clear, false),
                DyeUniform(body, dir, "Uniform_Enemy_DesertCrimson", new Color(0.86f, 0.70f, 0.44f), new Color(0.64f, 0.48f, 0.27f), new Color(0.56f, 0.11f, 0.09f), true),
                SkullMask(head, dir, "FaceMask_GhostSkull", new Color(0.82f, 0.80f, 0.73f)),
                SkullMask(head, dir, "FaceMask_EnemyMarauder", new Color(0.82f, 0.16f, 0.12f)),
            };
            return _gear;
        }

        private static Texture2D ReadableCopy(Texture src, int maxSize)
        {
            string assetPath = AssetDatabase.GetAssetPath(src);
            if (!string.IsNullOrEmpty(assetPath))
            {
                string full = Path.GetFullPath(assetPath);
                if (File.Exists(full))
                {
                    var raw = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (raw.LoadImage(File.ReadAllBytes(full)))
                    {
                        if (raw.width <= maxSize && raw.height <= maxSize) return raw;
                        int rw = Mathf.Min(raw.width, maxSize), rh = Mathf.Min(raw.height, maxSize);
                        var scaled = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
                        var dst = new Color[rw * rh];
                        for (int y = 0; y < rh; y++)
                            for (int x = 0; x < rw; x++)
                                dst[y * rw + x] = raw.GetPixelBilinear((x + 0.5f) / rw, (y + 0.5f) / rh);
                        scaled.SetPixels(dst);
                        scaled.Apply();
                        Object.DestroyImmediate(raw);
                        return scaled;
                    }
                    Object.DestroyImmediate(raw);
                }
            }
            int w = Mathf.Min(src.width, maxSize), h = Mathf.Min(src.height, maxSize);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        private static Texture2D SaveColorTexture(Texture2D tex, string path)
        {
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null)
            {
                imp.sRGBTexture = true;
                imp.maxTextureSize = 2048;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Material DyeUniform(Material body, string dir, string name, Color clothA, Color clothB, Color vestTint, bool hostileFaction)
        {
            if (body == null) return null;
            var m = new Material(body) { name = name };
            var src = body.GetTexture("_BaseColorMap");
            if (src != null)
            {
                var tex = ReadableCopy(src, 2048);
                int w = tex.width, h = tex.height;
                var px = tex.GetPixels();
                for (int y = 0; y < h; y++)
                {
                    float v = y / (float)(h - 1);
                    for (int x = 0; x < w; x++)
                    {
                        float u = x / (float)(w - 1);
                        int i = y * w + x;
                        var c = px[i];
                        float l = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
                        float blue = Mathf.Clamp01((c.b - Mathf.Max(c.r, c.g) - 0.010f) * 9f);
                        float shade = Mathf.Clamp(l * 2.35f, 0.14f, 1.32f);
                        if (blue > 0.01f)
                        {
                            Color cloth = clothA;
                            if (clothB.a > 0.01f)
                            {
                                float camo = Mathf.PerlinNoise(u * 22f + 1.3f, v * 22f + 3.7f);
                                cloth = camo > 0.52f ? clothB : clothA;
                            }
                            var dyed = new Color(cloth.r * shade, cloth.g * shade, cloth.b * shade, c.a);
                            px[i] = Color.Lerp(c, dyed, blue);
                        }
                        else if (hostileFaction)
                        {
                            // Transform dark plate carrier / pouches / helmet trim into crimson tactical OpFor gear & replace white "SWAT" back stencil
                            bool isSwatStencil = u >= 0.70f && u <= 0.78f && v >= 0.57f && v <= 0.80f && c.r > 0.55f && c.g > 0.55f && c.b > 0.55f;
                            if (isSwatStencil)
                            {
                                px[i] = new Color(0.46f, 0.08f, 0.07f, c.a);
                            }
                            else if (vestTint.a > 0.01f && l > 0.09f && l < 0.76f)
                            {
                                var rig = new Color(vestTint.r * shade, vestTint.g * shade, vestTint.b * shade, c.a);
                                px[i] = Color.Lerp(c, rig, 0.68f);
                            }
                        }
                    }
                }
                tex.SetPixels(px);
                tex.Apply();
                m.SetTexture("_BaseColorMap", SaveColorTexture(tex, $"{dir}/T_{name}.png"));
            }
            m.SetColor("_BaseColor", Color.white);
            return SaveMaterial(m, $"{dir}/M_{name}.mat");
        }

        /// <summary>Balaclava + faction skull print painted in the SWAT head UV layout (bone-white for allies, crimson Marauder for enemies).</summary>
        private static Material SkullMask(Material head, string dir, string matName, Color printColor)
        {
            if (head == null) return null;
            var m = new Material(head) { name = matName };
            var src = head.GetTexture("_BaseColorMap");
            if (src != null)
            {
                var tex = ReadableCopy(src, 1024);
                int w = tex.width, h = tex.height;
                var px = tex.GetPixels();
                float E(float u, float v, float cu, float cv, float ru, float rv) { float a = (u - cu) / ru, b = (v - cv) / rv; return a * a + b * b; }
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        float u = x / (float)(w - 1), v = 1f - y / (float)(h - 1); // v: 0 = top of the image
                        var c = px[i];
                        float l = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
                        float knit = 0.5f + 0.5f * Mathf.Sin(x * 1.3f) * Mathf.Sin(y * 1.1f);
                        Color fabric = new Color(0.045f, 0.046f, 0.05f) * (0.7f + l * 0.7f + knit * 0.18f);
                        fabric.a = 1f;

                        bool cranium = E(u, v, 0.5f, 0.355f, 0.15f, 0.165f) < 1f;
                        bool jaw = E(u, v, 0.5f, 0.495f, 0.1f, 0.065f) < 1f;
                        bool eye = E(u, v, 0.437f, 0.334f, 0.045f, 0.036f) < 1f || E(u, v, 0.563f, 0.334f, 0.045f, 0.036f) < 1f;
                        bool nose = v > 0.37f && v < 0.423f && Mathf.Abs(u - 0.5f) < 0.004f + (v - 0.37f) * 0.32f;
                        bool teethZone = v > 0.456f && v < 0.497f && Mathf.Abs(u - 0.5f) < 0.07f;
                        bool mouthLine = teethZone && v > 0.472f && v < 0.479f;
                        bool toothGap = teethZone && Mathf.Repeat(u * 55f, 1f) < 0.16f;
                        float hollow = Mathf.Min(E(u, v, 0.4f, 0.43f, 0.045f, 0.035f), E(u, v, 0.6f, 0.43f, 0.045f, 0.035f));

                        Color outc = fabric;
                        if ((cranium || jaw) && v > 0.18f && v < 0.6f)
                        {
                            Color bone = printColor * (0.82f + l * 0.3f);
                            if (hollow < 1f) bone *= Mathf.Lerp(0.55f, 1f, hollow);
                            float wear = Mathf.PerlinNoise(u * 38f, v * 38f);
                            bone = Color.Lerp(bone, fabric, Mathf.Clamp01((0.3f - wear) * 4f) * 0.75f); // printed, worn
                            outc = bone;
                            if (eye || nose || mouthLine || toothGap) outc = new Color(0.012f, 0.012f, 0.014f);
                        }
                        if (v < 0.17f || v > 0.72f) outc = new Color(0.02f, 0.02f, 0.022f); // eyeballs/teeth/tongue: void
                        outc.a = 1f;
                        px[i] = outc;
                    }
                tex.SetPixels(px);
                tex.Apply();
                m.SetTexture("_BaseColorMap", SaveColorTexture(tex, $"{dir}/T_{matName}.png"));
            }
            m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.18f);
            return SaveMaterial(m, $"{dir}/M_{matName}.mat");
        }

        public static void BuildSoldiers()
        {
            if (Robot) { BuildRobotSoldiers(); return; } // B136: the original Kinemation robot
            ConfigureLocomotionSpeed();
            ConfigureUpscaling();
            string soldierSrc = UseKinemationDriver ? DriverPrefab : SwatPrefab;
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(soldierSrc);
            if (src == null)
            {
                Debug.LogError("[LR2] Soldier source prefab not found: " + soldierSrc);
                return;
            }
            var weapon = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDir + "/Weapons/LR2_Mk18.prefab");
            var demoCtrlType = FindType("Demo.Scripts.Runtime.Character.FPSController");
            var demoMoveType = FindType("Demo.Scripts.Runtime.Character.FPSMovement");
            var settingsType = FindType("Demo.Scripts.Runtime.Character.FPSControllerSettings");
            var moveSettingsType = FindType("Demo.Scripts.Runtime.Character.FPSMovementSettings");

            // Settings copies (keep the demo assets untouched)
            var probe = src.GetComponent(demoCtrlType);
            var probeMove = src.GetComponent(demoMoveType);
            var srcSettings = probe != null ? new SerializedObject(probe).FindProperty("settings").objectReferenceValue as ScriptableObject : null;
            var srcMove = probeMove != null ? new SerializedObject(probeMove).FindProperty("movementSettings").objectReferenceValue as ScriptableObject : null;
            ScriptableObject soldierSettings = null, moveSettings = null;
            if (srcSettings != null)
            {
                soldierSettings = SaveAsset(Object.Instantiate(srcSettings), DataDir + "/Characters/LR2_SoldierSettings.asset");
                var sso = new SerializedObject(soldierSettings);
                var list = sso.FindProperty("weaponPrefabs");
                list.ClearArray();
                string[] order =
                {
                    PrefabDir + "/Weapons/LR2_Mk18.prefab",
                    PrefabDir + "/Weapons/LR2_Mk23.prefab",
                    PrefabDir + "/Weapons/LR2_AK12.prefab",
                    PrefabDir + "/Weapons/LR2_AK74N.prefab",
                    PrefabDir + "/Weapons/LR2_FAL.prefab",
                };
                int inserted = 0;
                foreach (var wPath in order)
                {
                    var wObj = AssetDatabase.LoadAssetAtPath<GameObject>(wPath);
                    if (wObj == null) continue;
                    list.InsertArrayElementAtIndex(inserted);
                    list.GetArrayElementAtIndex(inserted).objectReferenceValue = wObj;
                    inserted++;
                }
                sso.FindProperty("equipDelay").floatValue = 0.3f;
                var wbIdx = sso.FindProperty("weaponBone.index");
                if (wbIdx != null && !UseKinemationDriver) wbIdx.intValue = 46; // SWAT rig index only
                sso.ApplyModifiedPropertiesWithoutUndo();
            }
            if (srcMove != null)
            {
                moveSettings = SaveAsset(Object.Instantiate(srcMove), DataDir + "/Characters/LR2_MovementSettings.asset");
                var mso = new SerializedObject(moveSettings);
                void Gait(string n, float v, float smooth) { var p = mso.FindProperty(n); if (p == null) return; p.FindPropertyRelative("velocity").floatValue = v; if (smooth > 0f) p.FindPropertyRelative("velocitySmoothing").floatValue = smooth; }
                // B90 (user 11-19 00:13 "movement snappy, now it kinda feels sluggish"): quicker speed response (13/9/13 before)
                Gait("walking", 3.5f, 16f);   // realistic pace, snappy response
                Gait("sprinting", 6.1f, 12f);
                Gait("crouching", 2.0f, 16f);
                Gait("prone", 1.35f, 12f);     // brisk crawl + quick stance response
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

            foreach (bool player in new[] { true, false })
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(src);
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                go.name = player ? "LR2_Soldier_Player" : "LR2_Soldier_Bot";
                go.layer = LR2Layers.Character;

                var pi = FindType("UnityEngine.InputSystem.PlayerInput");
                if (pi != null) foreach (var c in go.GetComponentsInChildren(pi, true)) Object.DestroyImmediate(c);

                var demoCtrl = go.GetComponent(demoCtrlType);
                var demoMove = go.GetComponent(demoMoveType);
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

                var fpsAnim = go.GetComponent<FPSAnimator>();
                if (fpsAnim != null)
                {
                    // Leave profile unlinked until LR2Firearm.OnEquip() links it with the weapon FPSAnimatorEntity
                    // (matches Kinemation's Swat.prefab so AdsLayerJob always receives a non-null _entity).
                    var faso = new SerializedObject(fpsAnim);
                    var ap = faso.FindProperty("animatorProfile");
                    if (ap != null) ap.objectReferenceValue = null;
                    faso.ApplyModifiedPropertiesWithoutUndo();
                }

                var playablesCtrl = go.GetComponent<FPSPlayablesController>();
                var swatUpperMask = AssetDatabase.LoadAssetAtPath<AvatarMask>(DataDir + "/Weapons/LR2_Swat_UpperBody.mask");
                if (!UseKinemationDriver && playablesCtrl != null && swatUpperMask != null) playablesCtrl.upperBodyMask = swatUpperMask;

                if (go.GetComponent<WeaponAntiClip>() == null) go.AddComponent<WeaponAntiClip>();

                var combatant = go.AddComponent<Combatant>();
                var health = go.AddComponent<Health>();
                health.MaxHealth = 100f;
                health.MaxArmor = 50f; // B74 (user 11-18 04:30): 100 HP + 50 armour = 1.5x effective health, readout stays 100
                var ctrl = go.AddComponent<SoldierController>();
                if (soldierSettings != null) SetField(ctrl, "settings", soldierSettings);

                var anim = go.GetComponent<Animator>();
                if (anim != null) anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                // Visual body: the SWAT model, posed from the (hidden) Kinemation driver every frame.
                Animator bodyAnim = anim;
                Transform bodyRoot = go.transform;
                if (UseKinemationDriver)
                {
                    var swatModel = AssetDatabase.LoadAssetAtPath<GameObject>(SwatModel);
                    if (swatModel != null && anim != null)
                    {
                        var vis = (GameObject)PrefabUtility.InstantiatePrefab(swatModel, go.transform);
                        PrefabUtility.UnpackPrefabInstance(vis, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                        vis.name = "SWAT_Visual";
                        vis.transform.localPosition = Vector3.zero;
                        vis.transform.localRotation = Quaternion.identity;
                        vis.transform.localScale = Vector3.one;
                        var visAnim = vis.GetComponent<Animator>() ?? vis.AddComponent<Animator>();
                        visAnim.runtimeAnimatorController = null;
                        visAnim.applyRootMotion = false;
                        visAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                            if (!r.transform.IsChildOf(vis.transform)) r.enabled = false; // driver mannequin stays invisible
                        go.AddComponent<RetargetProxy>().Configure(anim, visAnim);
                        bodyAnim = visAnim;
                        bodyRoot = vis.transform;
                    }
                    else Debug.LogError("[LR2] SWAT visual model missing: " + SwatModel);
                }
                var ragdoll = go.AddComponent<SoldierRagdoll>();
                ragdoll.SetBones(BuildHitboxRagdoll(bodyAnim, health));
                go.AddComponent<HitReactions>();
                go.AddComponent<SoldierFoley>(); // gear/weapon rattle, breathing, effort, pain, trigger reset, mag drops
                var visuals = go.AddComponent<SoldierVisuals>();
                visuals.FriendlyMarkerMaterial = markerMat;
                var gear = MakeGear();
                visuals.SetGear(gear[0], gear[1], gear[2], gear[3]);
                visuals.ApplyMaterialsNow(player); // Pre-bake uniform & balaclava texture onto prefab so frame 0 never flashes old model textures

                var head = bodyAnim.GetBoneTransform(HumanBodyBones.Head);
                var chest = bodyAnim.GetBoneTransform(HumanBodyBones.Chest) ?? bodyAnim.GetBoneTransform(HumanBodyBones.Spine);
                var cam = go.GetComponentInChildren<Camera>(true);
                combatant.SetAimPoints(player && cam != null ? cam.transform : head, head, chest);

                // First-person: hide the head mesh (shadows only).
                var hidden = bodyRoot.GetComponentsInChildren<Renderer>(true).Where(r => r.name.ToLowerInvariant().Contains("head")).ToArray();
                var cso = new SerializedObject(ctrl);
                var hp = cso.FindProperty("firstPersonHidden");
                hp.arraySize = hidden.Length;
                for (int i = 0; i < hidden.Length; i++) hp.GetArrayElementAtIndex(i).objectReferenceValue = hidden[i];
                cso.FindProperty("eyeSocket").objectReferenceValue = head;
                cso.ApplyModifiedPropertiesWithoutUndo();

                foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.updateWhenOffscreen = true;

                if (cam != null)
                {
                    if (!UseKinemationDriver) cam.transform.localPosition = new Vector3(0.001f, 0.037f, 0.065f); // driver keeps Kinemation's ADS-aligned camera
                    var camCtrl = cam.GetComponent<FPSCameraController>();
                    if (camCtrl != null)
                    {
                        var ccso = new SerializedObject(camCtrl);
                        var fovSpd = ccso.FindProperty("fovSpeed");
                        if (fovSpd != null) fovSpd.floatValue = 8f;
                        var easeFunc = ccso.FindProperty("fovEaseMode.easeFunc");
                        if (easeFunc != null) easeFunc.enumValueIndex = 1; // EEaseFunc.Sine (matches AdsLayerSettings)
                        ccso.ApplyModifiedPropertiesWithoutUndo();
                    }
                    if (player && cam.GetComponent<BodycamRig>() == null) cam.gameObject.AddComponent<BodycamRig>(); // bodycam motion back on

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

                SavePrefab(go, $"{PrefabDir}/Characters/{(player ? "LR2_Soldier_Player" : "LR2_Soldier_Bot")}.prefab");
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[LR2] Soldier prefabs built (player + bot share the SWAT FPS rig, hitbox ragdoll, AK-12).");
        }

        private static List<RagdollBone> BuildHitboxRagdoll(Animator anim, Health health)
        {
            var result = new List<RagdollBone>();
            if (anim == null) return result;
            var bodies = new Dictionary<HumanBodyBones, Rigidbody>();

            Transform B(HumanBodyBones b) => LR2Bones.Get(anim, b); // B136: humanoid or the generic robot
            void Add(HumanBodyBones bone, HumanBodyBones childBone, HitZone zone, float mass, float radiusMeters, bool box)
            {
                var t = B(bone);
                if (t == null) return;
                var child = B(childBone);
                float scale = Mathf.Max(0.0001f, t.lossyScale.x);
                Vector3 toChild = child != null ? t.InverseTransformPoint(child.position) : new Vector3(0f, 0.15f / scale, 0f);
                Collider col;
                if (bone == HumanBodyBones.Head)
                {
                    var s = t.gameObject.AddComponent<SphereCollider>();
                    s.radius = 0.115f / scale;
                    s.center = new Vector3(0f, 0.07f / scale, 0.015f / scale);
                    col = s;
                }
                else if (box)
                {
                    var bx = t.gameObject.AddComponent<BoxCollider>();
                    float len = toChild.magnitude;
                    bx.center = toChild * 0.5f;
                    bx.size = new Vector3(0.34f / scale, Mathf.Max(len, 0.12f / scale), 0.22f / scale);
                    col = bx;
                }
                else
                {
                    var cap = t.gameObject.AddComponent<CapsuleCollider>();
                    Vector3 a = new Vector3(Mathf.Abs(toChild.x), Mathf.Abs(toChild.y), Mathf.Abs(toChild.z));
                    cap.direction = a.x > a.y ? (a.x > a.z ? 0 : 2) : (a.y > a.z ? 1 : 2);
                    cap.center = toChild * 0.5f;
                    cap.height = toChild.magnitude + radiusMeters * 2f / scale;
                    cap.radius = radiusMeters / scale;
                    col = cap;
                }
                t.gameObject.layer = LR2Layers.Hitbox;
                var rb = t.gameObject.AddComponent<Rigidbody>();
                rb.mass = mass;
                rb.isKinematic = true;
                rb.interpolation = RigidbodyInterpolation.None;
                var hb = t.gameObject.AddComponent<Hitbox>();
                hb.Configure(health, zone);
                bodies[bone] = rb;
                result.Add(new RagdollBone { body = rb, collider = col });
            }

            Add(HumanBodyBones.Hips, HumanBodyBones.Spine, HitZone.LowerTorso, 12f, 0.12f, true);
            Add(HumanBodyBones.Chest, HumanBodyBones.Neck, HitZone.UpperTorso, 14f, 0.14f, true);
            Add(HumanBodyBones.Head, HumanBodyBones.Head, HitZone.Head, 5f, 0.11f, false);
            Add(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HitZone.Arm, 2.5f, 0.055f, false);
            Add(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HitZone.Arm, 2.5f, 0.055f, false);
            Add(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, HitZone.Arm, 1.8f, 0.045f, false);
            Add(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, HitZone.Arm, 1.8f, 0.045f, false);
            Add(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HitZone.Leg, 7f, 0.085f, false);
            Add(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HitZone.Leg, 7f, 0.085f, false);
            Add(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HitZone.Leg, 4f, 0.065f, false);
            Add(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HitZone.Leg, 4f, 0.065f, false);

            // Joints: connect each body to its nearest parent body.
            foreach (var kv in bodies)
            {
                if (kv.Key == HumanBodyBones.Hips) continue;
                Transform p = kv.Value.transform.parent;
                Rigidbody parentBody = null;
                while (p != null && parentBody == null)
                {
                    parentBody = p.GetComponent<Rigidbody>();
                    p = p.parent;
                }
                if (parentBody == null) continue;
                var j = kv.Value.gameObject.AddComponent<CharacterJoint>();
                j.connectedBody = parentBody;
                j.enableProjection = true;
                bool limb = kv.Key != HumanBodyBones.Chest && kv.Key != HumanBodyBones.Head;
                j.lowTwistLimit = new SoftJointLimit { limit = limb ? -70f : -20f };
                j.highTwistLimit = new SoftJointLimit { limit = limb ? 40f : 20f };
                j.swing1Limit = new SoftJointLimit { limit = limb ? 60f : 25f };
                j.swing2Limit = new SoftJointLimit { limit = limb ? 30f : 25f };
            }
            return result;
        }

        // =====================================================================================================
        // 6. Data presets: bot difficulties, match settings, UI theme + fonts
        // =====================================================================================================
        public static void BuildData()
        {
            void Diff(string name, Vector2 reaction, float aimSpeed, float err, float settle, float decay, float head, float tol,
                float aggr, float strafe, float slide, float flank, float crouch, Vector2Int burst, float detect = 1f, float composure = 0.25f,
                float overshoot = 0.16f, float preFire = 0.05f)
            {
                var d = LoadOrCreate<BotDifficulty>($"{DataDir}/AI/BotDifficulty_{name}.asset");
                d.displayName = name;
                d.reactionTime = reaction;
                d.aimSpeed = aimSpeed;
                d.initialAimError = err;
                d.settledAimError = settle;
                d.errorDecay = decay;
                d.headshotChance = head;
                d.fireTolerance = tol;
                d.aggression = aggr;
                d.strafeChance = strafe;
                d.slideChance = slide;
                d.flankChance = flank;
                d.crouchPeekChance = crouch;
                d.burstSize = burst;
                d.visionRange = 75f;
                d.detectionSpeed = detect;
                d.composure = composure;
                d.overshootChance = overshoot;
                d.preFireChance = preFire;
                EditorUtility.SetDirty(d);
            }
            // D61 (user 11-18 "even the veteran bot seems too easy ... it rarely wins"): the detection meter needed ~0.75 s at
            // 20 m before a Veteran even reacted, and stress/strafe/movement stacked ~5 deg of error in a real fight. Per-preset
            // detection speed and composure; Veteran also reacts a little faster and settles tighter.
            Diff("Recruit", new Vector2(0.5f, 0.72f), 4.6f, 6f, 1.35f, 1.5f, 0.07f, 4.5f, 0.35f, 0.12f, 0f, 0.08f, 0.05f, new Vector2Int(2, 4), 0.9f, 0.08f, 0.1f, 0f);
            Diff("Regular", new Vector2(0.4f, 0.58f), 6.1f, 4.6f, 0.88f, 2.1f, 0.13f, 3.8f, 0.51f, 0.35f, 0.09f, 0.22f, 0.12f, new Vector2Int(3, 5), 1.13f, 0.25f, 0.15f, 0.13f);
            Diff("Hardened", new Vector2(0.29f, 0.43f), 7.8f, 3.4f, 0.54f, 2.8f, 0.2f, 3f, 0.69f, 0.61f, 0.19f, 0.38f, 0.2f, new Vector2Int(3, 7), 1.45f, 0.45f, 0.22f, 0.27f);
            Diff("Veteran", new Vector2(0.17f, 0.26f), 9.6f, 2.5f, 0.32f, 3.5f, 0.28f, 2.2f, 0.88f, 0.88f, 0.3f, 0.55f, 0.28f, new Vector2Int(4, 8), 1.9f, 0.66f, 0.28f, 0.42f);
            // B123 (user 11-19 "vet is vastly more superior ... let the skill gap between each setting be somewhat similar"):
            // one skill dial from Recruit (a little less helpless) to Veteran (trimmed a little); Regular and Hardened sit where
            // a duel model of these formulas gives every adjacent pair the same edge (~91 %), not 96/94/92 %.
            // B89 (user 11-19 00:13): overshoot chance and pre-fire chance (last two values): pros flick faster, so they
            // overshoot more often, and they pre-fire corners and time shots to the arrival of a flick.

            void Match(string file, string display, string desc, GameModeType mode, int limit, float time, int friendly, int enemy)
            {
                var m = LoadOrCreate<MatchSettings>($"{DataDir}/Game/{file}.asset");
                m.displayName = display;
                m.description = desc;
                m.mode = mode;
                m.scoreLimit = limit;
                m.timeLimitSeconds = time;
                m.countdownSeconds = 5f;
                m.respawnDelay = 3f;
                m.friendlyBots = friendly;
                m.enemyBots = enemy;
                m.spawnOrbs = true;
                m.maxOrbs = 2;
                m.orbSpawnInterval = 30f;
                EditorUtility.SetDirty(m);
            }
            Match("MatchSettings_Duel", "1v1 Deathmatch", "1v1 Deathmatch against an adaptive enemy operator. First to 15 kills.", GameModeType.Duel, 15, 480f, 0, 1);
            Match("MatchSettings_CTF", "Capture The Flag", "Rotating strategic flag mode with proximity capture boost and team options (Half & Half 50/50, Squads, FFA, Pack, Co-op).", GameModeType.CaptureTheFlag, 25, 480f, 1, 3);
            Match("MatchSettings_FFA", "Free For All", "You against three bots. First to 20 kills.", GameModeType.FreeForAll, 20, 600f, 0, 3);
            Match("MatchSettings_TDM", "Team Deathmatch", "You and a bot squadmate against two bots. First team to 40.", GameModeType.TeamDeathmatch, 40, 600f, 1, 2);

            // Fonts + theme
            var theme = LoadOrCreate<UITheme>(ResDir + "/UITheme.asset");
            theme.headingFont = MakeFont("Assets/Level/Bebas-Regular.otf", "Assets/LR2/UI/Fonts/LR2_Bebas SDF.asset");
            theme.bodyFont = MakeFont("Assets/LR2/UI/Fonts/BarlowCondensed-SemiBold.ttf", "Assets/LR2/UI/Fonts/LR2_Barlow SDF.asset");
            theme.numberFont = MakeFont("Assets/LR2/UI/Fonts/Rajdhani-Bold.ttf", "Assets/LR2/UI/Fonts/LR2_Rajdhani SDF.asset");
            EditorUtility.SetDirty(theme);
            AssetDatabase.SaveAssets();
            Debug.Log("[LR2] Data presets built (4 bot difficulties, 3 match modes, UI theme + fonts).");
        }

        private static TMP_FontAsset MakeFont(string fontPath, string assetPath)
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
            if (existing != null) return existing;
            var font = AssetDatabase.LoadAssetAtPath<Font>(fontPath);
            if (font == null)
            {
                Debug.LogWarning("[LR2] Font not found: " + fontPath);
                return null;
            }
            var fa = TMP_FontAsset.CreateFontAsset(font);
            if (fa == null) return null;
            EnsureFolder(System.IO.Path.GetDirectoryName(assetPath));
            AssetDatabase.CreateAsset(fa, assetPath);
            if (fa.atlasTextures != null)
                foreach (var tex in fa.atlasTextures)
                    if (tex != null)
                    {
                        tex.name = fa.name + " Atlas";
                        AssetDatabase.AddObjectToAsset(tex, fa);
                    }
            if (fa.material != null)
            {
                fa.material.name = fa.name + " Material";
                AssetDatabase.AddObjectToAsset(fa.material, fa);
            }
            EditorUtility.SetDirty(fa);
            return fa;
        }

        // =====================================================================================================
        // 7. Gameplay prefabs: seeker drone (legacy orb) + ammo crate
        // =====================================================================================================
        public static void BuildGameplayPrefabs()
        {
            string matDir = FxDir + "/Materials";
            var body = SaveMaterial(NewLit(new Color(0.09f, 0.1f, 0.11f), 0.62f, 1f), matDir + "/M_Drone_Body.mat");
            var eye = SaveMaterial(NewEmissive(new Color(1f, 0.4f, 0.1f), new Color(1f, 0.35f, 0.08f), 9000f), matDir + "/M_Drone_Eye.mat");
            var crateMat = SaveMaterial(NewLit(new Color(0.22f, 0.27f, 0.18f), 0.35f, 0.15f), matDir + "/M_AmmoCrate.mat");
            var stripMat = SaveMaterial(NewEmissive(new Color(0.2f, 1f, 0.4f), new Color(0.2f, 1f, 0.35f), 3000f), matDir + "/M_AmmoCrate_Strip.mat");

            // Seeker drone
            var drone = new GameObject("LR2_OrbDrone");
            drone.layer = LR2Layers.Hitbox; // raycast-hittable, never blocks movement
            var sphere = drone.AddComponent<SphereCollider>();
            sphere.radius = 0.32f;
            var shell = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(shell.GetComponent<Collider>());
            shell.name = "Shell";
            shell.transform.SetParent(drone.transform, false);
            shell.transform.localScale = Vector3.one * 0.55f;
            shell.GetComponent<Renderer>().sharedMaterial = body;
            var spinner = new GameObject("Spinner").transform;
            spinner.SetParent(drone.transform, false);
            for (int i = 0; i < 4; i++)
            {
                var e = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(e.GetComponent<Collider>());
                e.name = "Eye" + i;
                e.transform.SetParent(spinner, false);
                float a = i * 90f * Mathf.Deg2Rad;
                e.transform.localPosition = new Vector3(Mathf.Cos(a) * 0.27f, 0f, Mathf.Sin(a) * 0.27f);
                e.transform.localRotation = Quaternion.Euler(0f, -i * 90f, 0f);
                e.transform.localScale = new Vector3(0.05f, 0.05f, 0.16f);
                e.GetComponent<Renderer>().sharedMaterial = eye;
            }
            var glowGo = new GameObject("Glow");
            glowGo.transform.SetParent(drone.transform, false);
            var glow = glowGo.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = new Color(1f, 0.45f, 0.15f);
            glow.range = 6f;
            glow.lightUnit = LightUnit.Lumen;
            glow.intensity = 2500f;
            glow.shadows = LightShadows.None;
            glowGo.AddComponent<HDAdditionalLightData>();
            var c = drone.AddComponent<Combatant>();
            c.DisplayName = "SEEKER DRONE";
            c.Team = TeamId.Hostile;
            var h = drone.AddComponent<Health>();
            h.MaxHealth = 60f;
            h.Regenerate = false;
            var orb = drone.AddComponent<OrbDrone>();
            orb.ExplosionPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDir + "/FX/FX_Explosion_Small.prefab");
            SetField(orb, "spinner", spinner);
            SetField(orb, "glow", glow);
            SavePrefab(drone, PrefabDir + "/Enemies/LR2_OrbDrone.prefab");

            // Ammo crate
            var crate = new GameObject("LR2_AmmoCrate");
            crate.layer = LR2Layers.IgnoreRaycast;
            var trigger = crate.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(1.8f, 1.6f, 1.8f);
            trigger.center = new Vector3(0f, 0.8f, 0f);
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "Box";
            box.transform.SetParent(crate.transform, false);
            box.transform.localPosition = new Vector3(0f, 0.27f, 0f);
            box.transform.localScale = new Vector3(0.95f, 0.54f, 0.52f);
            box.GetComponent<Renderer>().sharedMaterial = crateMat;
            box.layer = LR2Layers.Default;
            // B75: Replace primitive box visual with the Leartes crate mesh while keeping exact BoxCollider & strip alignment.
            var cratePackPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LeartesStudios/Art/Prefabs/SM_Crate_01.prefab");
            if (cratePackPrefab != null)
            {
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(cratePackPrefab, crate.transform);
                visual.name = "CrateMesh";
                foreach (var col in visual.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
                var mfs = visual.GetComponentsInChildren<MeshFilter>(true);
                if (mfs.Length > 0)
                {
                    Bounds local = default;
                    bool has = false;
                    Matrix4x4 inv = visual.transform.worldToLocalMatrix;
                    foreach (var mf in mfs)
                    {
                        if (mf.sharedMesh == null) continue;
                        Bounds sb = mf.sharedMesh.bounds;
                        Matrix4x4 m = inv * mf.transform.localToWorldMatrix;
                        Vector3 sc = sb.center, se = sb.extents;
                        for (int sx = -1; sx <= 1; sx += 2)
                        for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 pt = m.MultiplyPoint3x4(sc + new Vector3(se.x * sx, se.y * sy, se.z * sz));
                            if (!has) { local = new Bounds(pt, Vector3.zero); has = true; }
                            else local.Encapsulate(pt);
                        }
                    }
                    if (has)
                    {
                        Vector3 target = new Vector3(0.95f, 0.54f, 0.52f);
                        Vector3 s = new Vector3(
                            target.x / Mathf.Max(0.05f, local.size.x),
                            target.y / Mathf.Max(0.05f, local.size.y),
                            target.z / Mathf.Max(0.05f, local.size.z));
                        visual.transform.localScale = s;
                        visual.transform.localPosition = new Vector3(
                            -local.center.x * s.x,
                            -local.min.y * s.y,
                            -local.center.z * s.z);
                        var boxRenderer = box.GetComponent<Renderer>();
                        if (boxRenderer != null) boxRenderer.enabled = false;
                    }
                }
            }
            var strip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(strip.GetComponent<Collider>());
            strip.name = "Strip";
            strip.transform.SetParent(crate.transform, false);
            strip.transform.localPosition = new Vector3(0f, 0.4f, -0.265f);
            strip.transform.localScale = new Vector3(0.7f, 0.05f, 0.01f);
            strip.GetComponent<Renderer>().sharedMaterial = stripMat;
            var lgo = new GameObject("Indicator");
            lgo.transform.SetParent(crate.transform, false);
            lgo.transform.localPosition = new Vector3(0f, 0.6f, -0.4f);
            var il = lgo.AddComponent<Light>();
            il.type = LightType.Point;
            il.color = new Color(0.3f, 1f, 0.45f);
            il.range = 3f;
            il.lightUnit = LightUnit.Lumen;
            il.intensity = 400f;
            il.shadows = LightShadows.None;
            lgo.AddComponent<HDAdditionalLightData>();
            var ac = crate.AddComponent<AmmoCrate>();
            SetField(ac, "indicator", il);
            SetField(ac, "indicatorRenderer", strip.GetComponent<Renderer>());
            box.AddComponent<SurfaceIdentifier>().surface = SurfaceType.Metal;
            SavePrefab(crate, PrefabDir + "/Props/LR2_AmmoCrate.prefab");
            AssetDatabase.SaveAssets();
            Debug.Log("[LR2] Gameplay prefabs built (seeker drone, ammo crate).");
        }
    }
}
