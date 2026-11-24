using System.Collections.Generic;
using System.IO;
using LR2.AI;
using LR2.Audio;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using LR2.Game;
using LR2.UI;
using LR2.World;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using static LR2.EditorTools.LR2EditorUtil;

namespace LR2.EditorTools
{
    /// <summary>
    /// Generates "Saltworks": a compact, vertical industrial harbour map (~54 x 46 m playable) for 1v1 - 2v2, with
    /// photoreal HDRP lighting, dense volumetrics, a bodycam lens volume, gameplay markers, NavMesh and minimap.
    /// Also builds the main menu scene and the build settings scene list.
    /// </summary>
    public static partial class LR2MapBuilder
    {
        public const string ScenePath = "Assets/LR2/Scenes/LR2_Saltworks.unity";
        public const string DeadsectorScenePath = "Assets/LR2/Scenes/LR2_Deadsector.unity";
        public const string FoundryScenePath = "Assets/LR2/Scenes/LR2_Foundry.unity";
        public const string TestRangeScenePath = "Assets/LR2/Scenes/LR2_TestRange.unity";
        public const string MenuScenePath = "Assets/LR2/Scenes/LR2_MainMenu.unity";
        private const string MatDir = "Assets/LR2/Materials/Environment";
        private const string LightDir = "Assets/LR2/Data/Lighting";
        private const string ConfigPath = "Assets/LR2/Data/Map/SaltworksConfig.asset";
        private static readonly Vector2 PlayMin = new Vector2(-27f, -23f), PlayMax = new Vector2(27f, 23f);

        private static Transform _structures, _props, _background, _lighting, _gameplay, _spawns, _cover, _tactical, _systems, _decals;
        private static Mats _m;
        private static MapBuildConfig _cfg;
        private static System.Random _rnd;
        private static readonly Dictionary<string, Mesh> _procMeshes = new Dictionary<string, Mesh>();
        private static readonly Dictionary<string, GameObject> _prefabCache = new Dictionary<string, GameObject>();
        private static Texture2D _detailMap;

        private class Mats
        {
            public Material concrete, asphalt, wallPlaster, metal, rust, corrugated, roof, wood, salt, curb, water, glass, darkMetal, steelGrate;
            public Material[] containers;
        }

        // ================================================================================================= entry
        public static void Build()
        {
            _cfg = LoadOrCreate<MapBuildConfig>(ConfigPath);
            ApplyReclaimedPreset(_cfg);
            _rnd = new System.Random(_cfg.seed);
            ConfigureNavAgent();
            EnsureFolder("Assets/LR2/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject("Saltworks");
            root.AddComponent<MapRoot>().config = _cfg;
            var env = Child(root.transform, "Environment");
            _structures = Child(env, "Structures");
            _props = Child(env, "Props");
            _background = Child(env, "Background");
            _decals = Child(env, "Decals");
            _lighting = Child(root.transform, "Lighting");
            _gameplay = Child(root.transform, "Gameplay");
            _spawns = Child(_gameplay, "Spawns");
            _cover = Child(_gameplay, "Cover");
            _tactical = Child(_gameplay, "Tactical");
            _systems = Child(root.transform, "Systems");

            _m = MakeMaterials();
            BuildGround();
            BuildControlHouse();
            BuildWorkshop();
            BuildWarehouse();
            BuildLoadingDock();
            BuildContainerYard();
            BuildQuay();
            BuildMidYard();
            BuildBoundary();
            if (_cfg.backgroundDressing) BuildBackground();
            BuildLighting();
            BuildGameplay();
            if (_cfg.decals) { PlaceDecals(); PlaceTargetedWearDecals(); }
            MarkStatic(env);
            BuildNature();   // after MarkStatic: swaying plants must stay dynamic

            EliminateSceneClipping("LR2_Saltworks", _structures, _props, _nature, _spawns, _gameplay);
            SetupDestructibleProps(root);

            Physics.SyncTransforms();
            BakeNavMesh(root, "Assets/LR2/Scenes/LR2_Saltworks_NavMesh.asset", true);
            GenerateCoverPoints(PlayMin, PlayMax, new[] { 0.6f, 3.7f, 5.3f });
            BuildMinimap("Assets/LR2/UI/Minimap/Saltworks_Minimap.png", PlayMin, PlayMax);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("[LR2] Map built: " + ScenePath);
        }

        // ================================================================================================= materials
        private static Mats MakeMaterials()
        {
            LR2GlassMaterials.Build();
            var dirtyGlass = AssetDatabase.LoadAssetAtPath<Material>("Assets/LR2/Materials/Environment/M_LR2_Glass_Dirty.mat")
                ?? MakeRealisticGlassMaterial();
            // Photoscanned Leartes texture sets (base colour + normal + ORM/ARD converted to HDRP mask maps), triplanar.
            var m = new Mats
            {
                concrete = PhotoMat("M_LR2_Concrete", "T_DamagedConcrete_BC", "T_DamagedConcrete_N", "T_DamagedConcrete_ORM", false, 0.33f, new Color(0.74f, 0.75f, 0.7f)),
                asphalt = PhotoMat("M_LR2_Ground", "T_BrokenAsphalt_BC", "T_BrokenAsphalt_N", "T_Brokenasphalt_ARD", true, 0.2f, new Color(0.66f, 0.68f, 0.64f)),
                wallPlaster = PhotoMat("M_LR2_Wall", "T_Concrete_BC", "T_Concrete_N", "T_Concrete_ORM", false, 0.36f, new Color(0.68f, 0.7f, 0.64f)),
                metal = PhotoMat("M_LR2_Metal", "T_Steel_BC", "T_Steel_N", "T_Steel_ORM", false, 0.8f, new Color(0.75f, 0.75f, 0.75f)),
                rust = PhotoMat("M_LR2_Rust", "T_Rustymetal_N", "T_Rustymetal_N", "T_Rustymetal_ORM", false, 0.6f, Color.white, "T_Rustymetal_BC"),
                corrugated = PhotoMat("M_LR2_Corrugated", "T_MetalplaneRusty_N", "T_MetalplaneRusty_N", "T_MetalplaneRusty_ORM", false, 0.45f, new Color(0.8f, 0.8f, 0.8f), "T_MetalplaneRusty_BC"),
                roof = PhotoMat("M_LR2_Roof", "T_TileRust_BC", "T_TileRust_N", "T_TileRust_ORM", false, 0.4f, new Color(0.78f, 0.78f, 0.78f)),
                wood = PhotoMat("M_LR2_Wood", "T_Planks01_B", "T_Planks01_N", "T_Planks01_ORM", false, 0.7f, new Color(0.75f, 0.72f, 0.68f)),
                salt = PhotoMat("M_LR2_Gravel", "T_Mud_BC", "T_Mud_N", "T_Mud_ARD", true, 0.45f, new Color(0.8f, 0.78f, 0.72f)),
                curb = PhotoMat("M_LR2_SafetyYellow", "T_Concrete_BC", "T_Concrete_N", "T_Concrete_ORM", false, 0.45f, new Color(0.76f, 0.63f, 0.20f)),
                darkMetal = PhotoMat("M_LR2_DarkMetal", "T_Steel_BC", "T_Steel_N", "T_Steel_ORM", false, 0.9f, new Color(0.22f, 0.23f, 0.24f)),
                steelGrate = PhotoMat("M_LR2_Grate", "T_Metalplane_BC", "T_Metalplane_N", "T_Metalplane_ORM", false, 0.8f, new Color(0.6f, 0.6f, 0.6f)),
                glass = dirtyGlass,
                water = FindLeartes("M_Ocean") ?? SaveMaterial(NewLit(new Color(0.03f, 0.07f, 0.08f), 0.96f), MatDir + "/M_LR2_Water.mat"),
            };
            Color[] containerColors =
            {
                new Color(0.62f, 0.25f, 0.18f), new Color(0.24f, 0.34f, 0.46f), new Color(0.28f, 0.4f, 0.28f),
                new Color(0.72f, 0.5f, 0.24f), new Color(0.5f, 0.5f, 0.5f), new Color(0.62f, 0.58f, 0.5f)
            };
            m.containers = new Material[containerColors.Length];
            for (int i = 0; i < containerColors.Length; i++)
                m.containers[i] = PhotoMat($"M_LR2_Container_{i}", "T_MetalplaneMid_BC", "T_MetalplaneMid_N", "T_MetalplaneMid_ORM", false, 0.42f, containerColors[i] * 1.3f);
            return m;
        }

        private static Texture2D FindTex(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var root in new[] { "Assets/LeartesStudios", "Assets/ScansFactory" })
            {
                if (!AssetDatabase.IsValidFolder(root)) continue;
                foreach (var g in AssetDatabase.FindAssets(name + " t:Texture2D", new[] { root }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (string.Equals(Path.GetFileNameWithoutExtension(p), name, System.StringComparison.OrdinalIgnoreCase))
                        return AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                }
            }
            return null;
        }

        /// <summary>Converts an Unreal-style ORM (AO, Roughness, Metal) or ARD (AO, Roughness, Displacement) map into an
        /// HDRP mask map (R metallic, G AO, B detail/height, A smoothness).</summary>
        private static Texture2D MakeMask(Texture2D src, bool ard, string outPath)
        {
            if (src == null) return null;
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            if (existing != null) return existing;
            int w = Mathf.Min(src.width, 1024), h = Mathf.Min(src.height, 1024);
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

        private static Shader _hdrpLit;

        /// <summary>Tileable HDRP detail map (R albedo grain, G normal Y, B smoothness mod, A normal X) for close-range surface breakup (B75/B42).</summary>
        private static Texture2D GetOrBuildDetailMap()
        {
            if (_detailMap != null) return _detailMap;
            string outPath = $"{MatDir}/Masks/T_LR2_Surface_Detail.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            if (existing != null) { _detailMap = existing; return _detailMap; }

            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true, true);
            var px = new Color32[S * S];
            float TileNoise(float u, float v, float freq, float seed)
            {
                // Seamless periodic blend across [0,1)
                float n00 = Mathf.PerlinNoise(u * freq + seed, v * freq + seed);
                float n10 = Mathf.PerlinNoise((u - 1f) * freq + seed, v * freq + seed);
                float n01 = Mathf.PerlinNoise(u * freq + seed, (v - 1f) * freq + seed);
                float n11 = Mathf.PerlinNoise((u - 1f) * freq + seed, (v - 1f) * freq + seed);
                float bx = Mathf.SmoothStep(0f, 1f, u);
                float by = Mathf.SmoothStep(0f, 1f, v);
                return Mathf.Lerp(Mathf.Lerp(n00, n10, bx), Mathf.Lerp(n01, n11, bx), by);
            }
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = x / (float)S;
                float v = y / (float)S;
                float nCoarse = TileNoise(u, v, 6f, 3.7f);
                float nFine = TileNoise(u, v, 18f, 11.3f);
                float grain = nCoarse * 0.6f + nFine * 0.4f;
                float d = 1f / S;
                float gx = (TileNoise((u + d) % 1f, v, 14f, 7.1f) - TileNoise((u - d + 1f) % 1f, v, 14f, 7.1f)) * 1.4f;
                float gy = (TileNoise(u, (v + d) % 1f, 14f, 7.1f) - TileNoise(u, (v - d + 1f) % 1f, 14f, 7.1f)) * 1.4f;
                byte r = (byte)Mathf.Clamp(Mathf.RoundToInt((0.5f + (grain - 0.5f) * 0.55f) * 255f), 0, 255);
                byte g = (byte)Mathf.Clamp(Mathf.RoundToInt((0.5f + gy * 0.45f) * 255f), 0, 255);
                byte b = (byte)Mathf.Clamp(Mathf.RoundToInt((0.5f + (0.5f - grain) * 0.5f) * 255f), 0, 255);
                byte a = (byte)Mathf.Clamp(Mathf.RoundToInt((0.5f + gx * 0.45f) * 255f), 0, 255);
                px[y * S + x] = new Color32(r, g, b, a);
            }
            tex.SetPixels32(px);
            tex.Apply(true, false);
            EnsureFolder(Path.GetDirectoryName(outPath).Replace('\\', '/'));
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceUpdate);
            var imp = (TextureImporter)AssetImporter.GetAtPath(outPath);
            if (imp != null)
            {
                imp.sRGBTexture = false;
                imp.mipmapEnabled = true;
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.SaveAndReimport();
            }
            _detailMap = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            return _detailMap;
        }

        /// <summary>HDRP/Lit triplanar material from a photoscan texture set. altBase: base colour if it lives under another name.</summary>
        private static Material PhotoMat(string outName, string baseName, string normalName, string maskName, bool ard, float worldScale, Color tint, string altBase = null)
        {
            if (_hdrpLit == null) _hdrpLit = Shader.Find("HDRP/Lit");
            var baseTex = FindTex(altBase ?? baseName);
            if (baseTex == null && altBase != null) baseTex = FindTex(baseName.Replace("_N", "_BC"));
            var normal = FindTex(normalName);
            var mask = MakeMask(FindTex(maskName), ard, $"{MatDir}/Masks/{maskName}_HDRPMask.png");
            var detail = GetOrBuildDetailMap();
            if (baseTex == null) Debug.LogWarning($"[LR2] Texture not found for {outName}: {altBase ?? baseName}");
            var m = new Material(_hdrpLit);
            m.SetColor("_BaseColor", tint);
            if (baseTex != null) m.SetTexture("_BaseColorMap", baseTex);
            if (normal != null)
            {
                m.SetTexture("_NormalMap", normal);
                m.SetFloat("_NormalScale", 1.45f);
            }
            if (mask != null)
            {
                m.SetTexture("_MaskMap", mask);
                m.SetFloat("_MetallicRemapMin", 0f);
                m.SetFloat("_MetallicRemapMax", 1f);
                m.SetFloat("_AORemapMin", 0.08f);
                m.SetFloat("_AORemapMax", 1f);
                m.SetFloat("_SmoothnessRemapMin", 0f);
                m.SetFloat("_SmoothnessRemapMax", 0.85f);
            }
            else m.SetFloat("_Smoothness", 0.22f);
            if (detail != null)
            {
                m.SetTexture("_DetailMap", detail);
                m.SetFloat("_UVDetail", 5f);
                m.SetFloat("_DetailAlbedoScale", 0.26f);
                m.SetFloat("_DetailNormalScale", 0.42f);
                m.SetFloat("_DetailSmoothnessScale", 0.24f);
            }
            m.SetFloat("_UVBase", 5f);             // triplanar world mapping: no stretching on scaled blocks
            m.SetFloat("_TexWorldScale", worldScale);
            HDMaterial.ValidateMaterial(m);
            return SaveMaterial(m, $"{MatDir}/{outName}.mat");
        }

        private static Material FindLeartes(string name)
        {
            foreach (var g in AssetDatabase.FindAssets(name + " t:Material", new[] { "Assets/LeartesStudios" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (Path.GetFileNameWithoutExtension(p) == name) return AssetDatabase.LoadAssetAtPath<Material>(p);
            }
            return null;
        }

        /// <summary>Clones a Leartes HDRP/Lit material and switches it to triplanar world mapping (no stretching on scaled blocks).</summary>
        private static Material EnvMat(string outName, string[] candidates, Color fallback, float worldScale, float smoothness)
        {
            Material src = null;
            foreach (var c in candidates)
            {
                src = FindLeartes(c);
                if (src != null && src.shader != null && src.shader.name == "HDRP/Lit") break;
                src = null;
            }
            Material m;
            if (src != null)
            {
                m = Object.Instantiate(src);
                m.SetFloat("_UVBase", 5f);              // Triplanar
                m.SetFloat("_TexWorldScale", worldScale);
                m.SetFloat("_UVDetail", 5f);
                HDMaterial.ValidateMaterial(m);
            }
            else m = NewLit(fallback, smoothness);
            return SaveMaterial(m, $"{MatDir}/{outName}.mat");
        }

        /// <summary>Generates a realistic HDRP transparent glass material with soda-lime teal-green edge tint, grime, stress cracks, normals, and refraction (B30).</summary>
        private static Material MakeRealisticGlassMaterial()
        {
            string texDir = $"{MatDir}/Masks";
            EnsureFolder(texDir);
            const int S = 256;
            string bcPath = $"{texDir}/T_LR2_Glass_BC.png";
            string nmPath = $"{texDir}/T_LR2_Glass_N.png";
            string mkPath = $"{texDir}/T_LR2_Glass_Mask.png";

            static Texture2D SaveTex(Texture2D tex, string path, bool srgb)
            {
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var imp = (TextureImporter)AssetImporter.GetAtPath(path);
                if (imp != null)
                {
                    imp.sRGBTexture = srgb;
                    imp.textureType = !srgb && path.EndsWith("_N.png") ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    imp.wrapMode = TextureWrapMode.Clamp;
                    imp.mipmapEnabled = true;
                    imp.SaveAndReimport();
                }
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }

            static float SegDist(Vector2 p, Vector2 a, Vector2 b)
            {
                Vector2 pa = p - a, ba = b - a;
                float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Mathf.Max(1e-6f, Vector2.Dot(ba, ba)));
                return (pa - ba * h).magnitude;
            }

            var bc = new Texture2D(S, S, TextureFormat.RGBA32, false);
            var nm = new Texture2D(S, S, TextureFormat.RGBA32, false, true);
            var mk = new Texture2D(S, S, TextureFormat.RGBA32, false, true);
            var bcPx = new Color[S * S];
            var nmPx = new Color[S * S];
            var mkPx = new Color[S * S];

            (Vector2 a, Vector2 b)[] cracks =
            {
                (new Vector2(0.02f, 0.03f), new Vector2(0.24f, 0.16f)),
                (new Vector2(0.24f, 0.16f), new Vector2(0.35f, 0.09f)),
                (new Vector2(0.03f, 0.21f), new Vector2(0.18f, 0.14f)),
                (new Vector2(0.98f, 0.94f), new Vector2(0.76f, 0.81f)),
                (new Vector2(0.76f, 0.81f), new Vector2(0.67f, 0.89f)),
            };

            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = x / (float)(S - 1);
                float v = y / (float)(S - 1);
                float edge = Mathf.Min(Mathf.Min(u, 1f - u), Mathf.Min(v, 1f - v));
                float rimDust = Mathf.Clamp01(1f - edge / 0.14f);
                float noise = Mathf.PerlinNoise(u * 9.5f + 2.1f, v * 9.5f + 4.7f);
                float streak = Mathf.PerlinNoise(u * 26f + 1.3f, v * 3.8f + 0.5f);
                float grime = Mathf.Clamp01(rimDust * 0.65f * (0.45f + 0.85f * noise) + Mathf.Max(0f, streak - 0.62f) * 0.6f);

                float minCrack = 1f;
                Vector2 p = new Vector2(u, v);
                for (int c = 0; c < cracks.Length; c++)
                    minCrack = Mathf.Min(minCrack, SegDist(p, cracks[c].a, cracks[c].b));
                float crackLine = Mathf.Clamp01(1f - minCrack / 0.009f);

                // Soda-lime green-cyan tint near edges, faint clear optical core, bright fractured crack lines
                Color tint = Color.Lerp(new Color(0.58f, 0.78f, 0.75f, 1f), new Color(0.28f, 0.45f, 0.42f, 1f), rimDust * 0.72f);
                tint = Color.Lerp(tint, new Color(0.32f, 0.34f, 0.31f, 1f), grime * 0.55f);
                tint = Color.Lerp(tint, new Color(0.88f, 0.94f, 0.93f, 1f), crackLine * 0.75f);
                float alpha = Mathf.Clamp01(0.22f + rimDust * 0.24f + grime * 0.22f + crackLine * 0.42f);
                tint.a = alpha;
                bcPx[y * S + x] = tint;

                float nx = (Mathf.PerlinNoise(u * 14f + 0.02f, v * 14f) - Mathf.PerlinNoise(u * 14f - 0.02f, v * 14f)) * 0.45f;
                float ny = (Mathf.PerlinNoise(u * 14f, v * 14f + 0.02f) - Mathf.PerlinNoise(u * 14f, v * 14f - 0.02f)) * 0.45f;
                if (crackLine > 0.05f) { nx += (noise - 0.5f) * 0.85f * crackLine; ny += (streak - 0.5f) * 0.85f * crackLine; }
                Vector3 nrm = new Vector3(nx, ny, 1f).normalized;
                nmPx[y * S + x] = new Color(nrm.x * 0.5f + 0.5f, nrm.y * 0.5f + 0.5f, nrm.z * 0.5f + 0.5f, 1f);

                float metallic = Mathf.Lerp(0.06f, 0.0f, grime);
                float ao = Mathf.Clamp01(1f - rimDust * 0.32f - grime * 0.22f);
                float smooth = Mathf.Clamp01(Mathf.Lerp(0.95f, 0.36f, Mathf.Max(grime * 0.85f, crackLine)));
                mkPx[y * S + x] = new Color(metallic, ao, 0.5f, smooth);
            }

            bc.SetPixels(bcPx); bc.Apply();
            nm.SetPixels(nmPx); nm.Apply();
            mk.SetPixels(mkPx); mk.Apply();
            var bcTex = SaveTex(bc, bcPath, true);
            var nmTex = SaveTex(nm, nmPath, false);
            var mkTex = SaveTex(mk, mkPath, false);

            if (_hdrpLit == null) _hdrpLit = Shader.Find("HDRP/Lit");
            var m = new Material(_hdrpLit) { name = "M_LR2_Glass" };
            m.SetColor("_BaseColor", new Color(0.86f, 0.96f, 0.94f, 0.32f));
            m.SetTexture("_BaseColorMap", bcTex);
            m.SetTexture("_NormalMap", nmTex);
            m.SetFloat("_NormalScale", 0.72f);
            m.SetTexture("_MaskMap", mkTex);
            m.SetFloat("_SurfaceType", 1f); // Transparent
            m.SetFloat("_BlendMode", 0f);   // Alpha
            m.SetFloat("_EnableBlendModePreserveSpecularLighting", 1f);
            m.SetFloat("_DoubleSidedEnable", 1f);
            m.SetFloat("_CullMode", 0f);
            m.SetFloat("_AlphaCutoffEnable", 0f);
            m.SetFloat("_UVBase", 0f);
            m.SetFloat("_Metallic", 0.05f);
            m.SetFloat("_Smoothness", 0.95f);
            m.SetFloat("_SmoothnessRemapMin", 0.32f);
            m.SetFloat("_SmoothnessRemapMax", 0.96f);
            m.SetFloat("_RefractionModel", 1f); // Box / planar glass refraction
            m.SetFloat("_Ior", 1.52f);
            m.SetFloat("_Thickness", 0.018f);
            m.SetColor("_TransmittanceColor", new Color(0.68f, 0.88f, 0.84f, 1f));
            m.SetFloat("_ATDistance", 0.14f);
            HDMaterial.ValidateMaterial(m);
            return SaveMaterial(m, $"{MatDir}/M_LR2_Glass.mat");
        }

        /// <summary>Adds a persistent dark-steel perimeter frame + cross mullion bars around glass panes so intact/shattered windows read cleanly (B30).</summary>
        private static void EnhanceGlassWindowFrame(GameObject pane)
        {
            if (pane == null || _m == null || pane.name.Contains("_FramedDone")) return;
            var col = pane.GetComponent<BoxCollider>();
            Vector3 s = Vector3.Scale(col != null ? col.size : Vector3.one, new Vector3(
                Mathf.Abs(pane.transform.lossyScale.x),
                Mathf.Abs(pane.transform.lossyScale.y),
                Mathf.Abs(pane.transform.lossyScale.z)));
            if (s.y < 0.5f || Mathf.Max(s.x, s.z) < 0.6f) return;

            Transform host = pane.transform.parent != null ? pane.transform.parent : pane.transform;
            var frm = new GameObject("GlassFrame_" + pane.name).transform;
            frm.SetParent(host, false);
            frm.position = pane.transform.position;
            frm.rotation = pane.transform.rotation;

            bool wideX = s.x >= s.z;
            float width = wideX ? s.x : s.z;
            float height = s.y;
            float thick = Mathf.Clamp(Mathf.Min(s.x, s.z), 0.04f, 0.08f) + 0.04f;
            float rim = 0.045f;

            void Strip(string sn, Vector3 lp, Vector3 ls)
            {
                var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                bar.name = sn;
                bar.transform.SetParent(frm, false);
                bar.transform.localPosition = lp;
                bar.transform.localScale = ls;
                bar.GetComponent<Renderer>().sharedMaterial = _m.darkMetal;
                Object.DestroyImmediate(bar.GetComponent<Collider>());
                bar.layer = LR2Layers.Debris;
            }

            Vector3 barH = wideX ? new Vector3(width + rim, rim, thick) : new Vector3(thick, rim, width + rim);
            Vector3 barV = wideX ? new Vector3(rim, height, thick) : new Vector3(thick, height, rim);
            float halfW = width * 0.5f - rim * 0.2f;
            float halfH = height * 0.5f - rim * 0.2f;
            Strip("Top", new Vector3(0f, halfH, 0f), barH);
            Strip("Bottom", new Vector3(0f, -halfH, 0f), barH);
            Strip("Left", wideX ? new Vector3(-halfW, 0f, 0f) : new Vector3(0f, 0f, -halfW), barV);
            Strip("Right", wideX ? new Vector3(halfW, 0f, 0f) : new Vector3(0f, 0f, halfW), barV);
            if (width > 1.3f)
                Strip("MullionV", Vector3.zero, wideX ? new Vector3(0.032f, height - rim, thick * 0.9f) : new Vector3(thick * 0.9f, height - rim, 0.032f));
        }

        // ================================================================================================= helpers
        private static Transform Child(Transform parent, string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            return t;
        }

        private const string ProcMeshDir = "Assets/LR2/Meshes/Procedural";

        private static Mesh GetOrBuildProcMesh(string key, int nx, int ny, int nz, System.Func<Vector3, Vector3, Vector3> deform)
        {
            if (_procMeshes.TryGetValue(key, out var cached) && cached != null) return cached;
            EnsureFolder(ProcMeshDir);
            string path = $"{ProcMeshDir}/{key}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            var mesh = existing != null ? existing : new Mesh { name = key };
            mesh.Clear();

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            void AddFace(Vector3 normal, Vector3 uAxis, Vector3 vAxis, int nu, int nv)
            {
                if (Vector3.Dot(Vector3.Cross(uAxis, vAxis), normal) < 0f) (uAxis, vAxis) = (vAxis, uAxis);
                int start = verts.Count;
                for (int iv = 0; iv <= nv; iv++)
                {
                    float fv = iv / (float)nv;
                    for (int iu = 0; iu <= nu; iu++)
                    {
                        float fu = iu / (float)nu;
                        Vector3 raw = normal * 0.5f + (fu - 0.5f) * uAxis + (fv - 0.5f) * vAxis;
                        verts.Add(deform != null ? deform(raw, normal) : raw);
                        uvs.Add(new Vector2(fu, fv));
                    }
                }
                int stride = nu + 1;
                for (int iv = 0; iv < nv; iv++)
                {
                    for (int iu = 0; iu < nu; iu++)
                    {
                        int i00 = start + iv * stride + iu;
                        int i10 = i00 + 1;
                        int i01 = i00 + stride;
                        int i11 = i01 + 1;
                        tris.Add(i00); tris.Add(i10); tris.Add(i11);
                        tris.Add(i00); tris.Add(i11); tris.Add(i01);
                    }
                }
            }

            AddFace(Vector3.forward, Vector3.right, Vector3.up, nx, ny);
            AddFace(Vector3.back, Vector3.left, Vector3.up, nx, ny);
            AddFace(Vector3.right, Vector3.back, Vector3.up, nz, ny);
            AddFace(Vector3.left, Vector3.forward, Vector3.up, nz, ny);
            AddFace(Vector3.up, Vector3.right, Vector3.back, nx, nz);
            AddFace(Vector3.down, Vector3.right, Vector3.forward, nx, nz);

            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            if (existing == null) AssetDatabase.CreateAsset(mesh, path);
            else EditorUtility.SetDirty(mesh);
            _procMeshes[key] = mesh;
            return mesh;
        }

        private static Mesh GetWarpedContainerMesh(int variant)
        {
            int v = Mathf.Abs(variant) % 4;
            float s = v * 17.3f + 3.1f;
            return GetOrBuildProcMesh($"SM_LR2_Container_Warp{v}", 18, 7, 6, (p, _) =>
            {
                // Continuum thin-shell mechanics: Kirchhoff-Love clamped rectangular plate mode + volume-conserving
                // Poisson (nu=0.3) annular bulge collar + Euler column flange buckling; bottom sill kept flush.
                float ny = Mathf.Clamp01(p.y + 0.5f);
                float baseClamp = Mathf.Clamp01((ny - 0.04f) * 8f);
                float modeX = Mathf.Sin(Mathf.PI * Mathf.Clamp01(p.x + 0.5f));
                float modeY = Mathf.Sin(Mathf.PI * ny);
                float modeZ = Mathf.Sin(Mathf.PI * Mathf.Clamp01(p.z + 0.5f));
                float plateMode = (modeX * modeX) * (modeY * modeY) * baseClamp;
                float roofMode = (modeX * modeX) * (modeZ * modeZ);

                if (Mathf.Abs(p.z) > 0.35f)
                    p.z += Mathf.Sign(p.z) * Mathf.Sin(p.x * 28f * Mathf.PI) * 0.013f * (modeX * modeY);
                if (p.y > 0.45f)
                    p.y += Mathf.Sin(p.x * 22f * Mathf.PI) * 0.008f * roofMode;

                // 3 localized blast/shrapnel craters modeled via Ricker Poisson-collar profile w(r) = (1 - 1.4 r^2) exp(-r^2)
                float shellDeflect = 0f;
                for (int k = 0; k < 3; k++)
                {
                    float cx = Mathf.Sin(s + k * 2.4f) * 0.32f;
                    float cy = Mathf.Cos(s * 0.7f + k * 3.1f) * 0.20f;
                    float cz = (k % 2 == 0 ? 1f : -1f) * 0.5f;
                    float dx = (p.x - cx) * 4.2f, dy = (p.y - cy) * 3.5f, dz = (p.z - cz) * 2.5f;
                    float r2 = dx * dx + dy * dy + dz * dz;
                    float ricker = (1f - 1.4f * r2) * Mathf.Exp(-r2 * 2.6f);
                    shellDeflect += ricker * (0.048f + k * 0.012f);
                }
                if (Mathf.Abs(p.z) > 0.35f) p.z -= Mathf.Sign(p.z) * shellDeflect * plateMode;
                if (p.y > 0.45f) p.y -= shellDeflect * 0.42f * roofMode;

                // Euler elastic flange buckling along container span + torsion twist increasing with height
                float eulerBuckle = Mathf.Cos((2f + v) * Mathf.PI * p.x) * plateMode * 0.016f;
                float torsion = Mathf.Sin(p.x * 2.2f + s) * 0.018f * baseClamp;
                p.z += eulerBuckle + torsion;
                p.y -= Mathf.Max(0f, Mathf.Sin(p.x * Mathf.PI * (2f + (v & 1)))) * 0.016f * roofMode;
                return p;
            });
        }

        private static Mesh GetWarpedWallMesh(int variant, bool brokenTop, float breakDepth = 0.22f, float warpAmp = 0.032f)
        {
            int v = Mathf.Abs(variant) % 4;
            float s = v * 23.7f + 7.4f;
            string tag = brokenTop ? $"SM_LR2_Wall_Broken{v}_{Mathf.RoundToInt(breakDepth * 100f)}" : $"SM_LR2_Masonry_Warp{v}";
            return GetOrBuildProcMesh(tag, brokenTop ? 20 : 12, 7, brokenTop ? 4 : 2, (p, _) =>
            {
                float ny = Mathf.Clamp01(p.y + 0.5f); // 0 bottom footing .. 1 top parapet
                // Euler-Bernoulli cantilever beam blast deflection: delta(eta) = eta^2 * (1.5 - 0.5 * eta), clamped at floor
                float cantilever = ny * ny * (1.5f - 0.5f * ny);
                float bowZ = (Mathf.PerlinNoise(p.x * 3.1f + s, ny * 2.4f + s) - 0.5f) * 2.2f * warpAmp * cantilever;
                float bowX = (Mathf.PerlinNoise(p.z * 3.1f + s + 11.3f, ny * 2.4f + s) - 0.5f) * 1.15f * warpAmp * cantilever;
                p.z += bowZ * Mathf.Clamp01(Mathf.Abs(p.z) * 2f);
                p.x += bowX * Mathf.Clamp01(Mathf.Abs(p.x) * 2f);

                if (brokenTop && p.y > 0.08f)
                {
                    float topFade = Mathf.Clamp01((p.y - 0.08f) / 0.42f);
                    // Mohr-Coulomb 38-deg conjugated brittle masonry shear planes + Hertzian impact craters
                    const float cos38 = 0.7880f, sin38 = 0.6157f;
                    float shearA = Mathf.Abs(Mathf.Sin((p.x * cos38 + p.y * sin38 + s * 0.13f) * 7.5f));
                    float shearB = Mathf.Abs(Mathf.Sin((p.x * cos38 - p.y * sin38 + s * 0.27f) * 6.2f));
                    float fractureField = Mathf.Min(shearA, shearB);

                    float c1x = Mathf.Sin(s * 0.7f) * 0.24f;
                    float c2x = Mathf.Cos(s * 1.3f) * 0.30f;
                    float hertz1 = Mathf.Exp(-((p.x - c1x) * (p.x - c1x)) * 22f);
                    float hertz2 = Mathf.Exp(-((p.x - c2x) * (p.x - c2x)) * 18f);
                    float fineNoise = Mathf.PerlinNoise(p.x * 8.4f + s * 1.7f, p.z * 3.2f + 0.5f);

                    float bite = Mathf.Min(0.27f, (fractureField * 0.45f + (hertz1 + hertz2) * 0.55f + fineNoise * 0.25f) * breakDepth);
                    p.y -= bite * topFade;
                    if (topFade > 0.55f && Mathf.Abs(p.z) > 0.40f)
                        p.z *= 1f - (0.04f + fineNoise * 0.07f) * topFade;
                }
                else if (!brokenTop)
                {
                    // Brittle corner spall clamped away from base footing (ny < 0.08 keeps floor contact flush)
                    float baseMask = Mathf.Clamp01((ny - 0.08f) * 6f);
                    float chip = Mathf.PerlinNoise(p.x * 5.4f + s, p.y * 5.9f + p.z * 3.5f + s);
                    Vector3 inDir = new Vector3(
                        Mathf.Abs(p.x) > 0.38f ? Mathf.Sign(p.x) * Mathf.Clamp01((Mathf.Abs(p.x) - 0.38f) * 8f) : 0f,
                        Mathf.Abs(p.y) > 0.35f ? Mathf.Sign(p.y) * Mathf.Clamp01((Mathf.Abs(p.y) - 0.35f) * 6f) : 0f,
                        Mathf.Abs(p.z) > 0.38f ? Mathf.Sign(p.z) * Mathf.Clamp01((Mathf.Abs(p.z) - 0.38f) * 8f) : 0f);
                    p -= inDir * (chip * 0.028f * baseMask);
                }
                return p;
            });
        }

        private static Mesh GetRubbleMesh(int variant)
        {
            int v = Mathf.Abs(variant) % 3;
            float s = v * 31.1f + 4.9f;
            Vector3 plane1 = new Vector3(Mathf.Sin(s), 0.75f, Mathf.Cos(s)).normalized;
            Vector3 plane2 = new Vector3(-Mathf.Cos(s * 1.4f), 0.55f, Mathf.Sin(s * 0.8f)).normalized;
            return GetOrBuildProcMesh($"SM_LR2_Rubble{v}", 4, 3, 4, (p, _) =>
            {
                // Mohr-Coulomb cleavage planes producing sharp faceted concrete rubble chunks
                float d1 = Vector3.Dot(p, plane1);
                if (d1 > 0.22f) p -= plane1 * ((d1 - 0.22f) * 0.85f);
                float d2 = Vector3.Dot(p, plane2);
                if (d2 > 0.20f) p -= plane2 * ((d2 - 0.20f) * 0.85f);
                float rough = Mathf.PerlinNoise(p.x * 3.4f + s, p.z * 3.1f + s);
                p.x *= 0.72f + rough * 0.48f;
                p.z *= 0.72f + rough * 0.48f;
                if (p.y > -0.3f) p.y *= 0.52f + rough * 0.58f;
                return p;
            });
        }

        private static void ApplyMeshKeepBoxCollider(GameObject go, Mesh mesh)
        {
            if (go == null || mesh == null) return;
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = mesh;
        }

        private static void AddWarTornWallDetails(Transform parent, Vector3 wallCenter, float wallLen, float wallTopY, float yaw, Material mat, int seedBase, bool includeRebar = true, bool includeRubble = true)
        {
            Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            if (includeRebar)
            {
                int rods = Mathf.Clamp(Mathf.RoundToInt(wallLen * 0.35f), 2, 7);
                for (int i = 0; i < rods; i++)
                {
                    float h1 = Mathf.PerlinNoise(i * 3.7f + seedBase, seedBase * 0.3f);
                    float h2 = Mathf.PerlinNoise(seedBase * 0.5f, i * 5.3f + 2.1f);
                    float along = Mathf.Lerp(-wallLen * 0.42f, wallLen * 0.42f, (i + 0.35f + h1 * 0.3f) / rods);
                    float y = wallTopY - 0.14f - h2 * 0.28f;
                    var rod = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    rod.name = "Rebar";
                    rod.transform.SetParent(parent, false);
                    rod.transform.localPosition = wallCenter + right * along + Vector3.up * (y - wallCenter.y) + fwd * ((h1 - 0.5f) * 0.16f);
                    rod.transform.localRotation = Quaternion.Euler(12f + h2 * 28f, yaw + (h1 - 0.5f) * 45f, (h2 - 0.5f) * 35f);
                    rod.transform.localScale = new Vector3(0.022f, 0.22f + h1 * 0.18f, 0.022f);
                    rod.GetComponent<Renderer>().sharedMaterial = _m.rust;
                    rod.layer = LR2Layers.Debris;
                    Object.DestroyImmediate(rod.GetComponent<Collider>());
                }
            }
            if (includeRubble && wallCenter.y < 3.5f)
            {
                int chunks = Mathf.Clamp(Mathf.RoundToInt(wallLen * 0.22f), 2, 6);
                for (int i = 0; i < chunks; i++)
                {
                    float h1 = Mathf.PerlinNoise(i * 7.1f + seedBase, 1.3f);
                    float h2 = Mathf.PerlinNoise(2.9f, i * 4.7f + seedBase);
                    float along = Mathf.Lerp(-wallLen * 0.44f, wallLen * 0.44f, (i + h1 * 0.6f) / chunks);
                    float sideOff = (i % 2 == 0 ? 1f : -1f) * (0.36f + h2 * 0.45f);
                    var rb = Box("RubbleChunk", parent, new Vector3(wallCenter.x, 0.14f, wallCenter.z) + right * along + fwd * sideOff,
                        new Vector3(0.42f + h1 * 0.55f, 0.24f + h2 * 0.26f, 0.36f + h2 * 0.45f), mat, LR2Layers.Debris, SurfaceType.Concrete, h1 * 360f);
                    ApplyMeshKeepBoxCollider(rb, GetRubbleMesh(seedBase + i));
                    Object.DestroyImmediate(rb.GetComponent<Collider>());
                }
            }
        }

        private static GameObject Box(string name, Transform parent, Vector3 center, Vector3 size, Material mat, int layer = LR2Layers.Default, SurfaceType? surface = null, float yaw = 0f)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.layer = layer;
            if (surface.HasValue) go.AddComponent<SurfaceIdentifier>().surface = surface.Value;
            return go;
        }

        /// <summary>Wall from a to b (XZ) with rectangular openings: (offset along wall, width, bottom, top).</summary>
        private static void Wall(string name, Transform parent, Vector3 a, Vector3 b, float height, float thick, Material mat, params Vector4[] openings)
            => WallWithFloor(name, parent, a, b, height, thick, mat, 0.102f, openings);

        private static void WallWithFloor(string name, Transform parent, Vector3 a, Vector3 b, float height, float thick, Material mat, float floorOffset, params Vector4[] openings)
        {
            var root = Child(parent, name);
            Vector3 dir = (b - a);
            dir.y = 0f;
            float len = dir.magnitude;
            dir /= len;
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg - 90f;
            bool xAxis = Mathf.Abs(dir.x) >= Mathf.Abs(dir.z); // X-walls span corners; Z-walls butt cleanly inside them (no Z-fighting)
            var ops = new List<Vector4>(openings);
            ops.Sort((p, q) => p.x.CompareTo(q.x));
            float cursor = 0f;
            int i = 0;
            void Seg(float from, float to, float y0, float y1)
            {
                if (to - from < 0.01f || y1 - y0 < 0.01f) return;
                float trimFrom = Mathf.Approximately(from, 0f) ? (xAxis ? -thick * 0.5f : thick * 0.502f) : from;
                float trimTo = Mathf.Approximately(to, len) ? len + (xAxis ? thick * 0.5f : -thick * 0.502f) : to;
                if (trimTo - trimFrom < 0.01f) return;
                if (y0 < 0.01f && a.y < 0.05f && y1 > 0.14f && floorOffset > 0.001f) y0 = floorOffset;
                Vector3 mid = a + dir * ((trimFrom + trimTo) * 0.5f);
                var seg = Box($"Seg{i++}", root, new Vector3(mid.x, a.y + (y0 + y1) * 0.5f, mid.z), new Vector3(trimTo - trimFrom, y1 - y0, thick), mat, LR2Layers.Default, SurfaceType.Concrete, yaw);
                if (mat == _m.wallPlaster || mat == _m.concrete || mat == _m.corrugated)
                    ApplyMeshKeepBoxCollider(seg, GetWarpedWallMesh(i + name.GetHashCode(), false, 0.14f, 0.018f));
            }
            foreach (var o in ops)
            {
                float s = o.x - o.y * 0.5f, e = o.x + o.y * 0.5f;
                Seg(cursor, s, 0f, height);
                Seg(s, e, 0f, o.z);          // below opening (window sill)
                Seg(s, e, o.w, height);      // above opening (lintel)
                cursor = e;
            }
            Seg(cursor, len, 0f, height);
        }

        /// <summary>Stairs: visual steps + a smooth invisible ramp collider (PlayerClip) that soldiers and the NavMesh use.</summary>
        private static void Stairs(string name, Transform parent, Vector3 bottom, Vector3 top, float width, Material stepMat, bool railings = true)
        {
            var root = Child(parent, name);
            Vector3 flat = top - bottom;
            float rise = flat.y;
            flat.y = 0f;
            float run = flat.magnitude;
            Vector3 dir = flat / run;
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            int steps = Mathf.Max(2, Mathf.RoundToInt(rise / 0.18f));
            float stepRun = run / steps, stepRise = rise / steps;
            for (int s = 0; s < steps; s++)
            {
                Vector3 p = bottom + dir * (stepRun * (s + 0.5f)) + Vector3.up * (stepRise * (s + 0.5f));
                var step = Box("Step" + s, root, p, new Vector3(width, stepRise, stepRun * 1.02f), stepMat, LR2Layers.Debris, SurfaceType.Metal, yaw);
                Object.DestroyImmediate(step.GetComponent<Collider>());
            }
            // Ramp collider
            float slopeLen = Mathf.Sqrt(run * run + rise * rise);
            float angle = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;
            var ramp = new GameObject("RampCollider");
            ramp.transform.SetParent(root, false);
            ramp.transform.localPosition = (bottom + top) * 0.5f + Vector3.up * 0.02f;
            ramp.transform.localRotation = Quaternion.Euler(-angle, yaw, 0f);
            var bc = ramp.AddComponent<BoxCollider>();
            bc.size = new Vector3(width, 0.1f, slopeLen + 0.2f);
            ramp.layer = LR2Layers.PlayerClip;
            ramp.AddComponent<SurfaceIdentifier>().surface = SurfaceType.Metal;
            if (!railings) return;
            Vector3 side = Vector3.Cross(Vector3.up, dir) * (width * 0.5f);
            foreach (float sgn in new[] { -1f, 1f })
            {
                var rail = Box("Rail", root, (bottom + top) * 0.5f + side * sgn + Vector3.up * 0.95f, new Vector3(0.05f, 0.05f, slopeLen), _m.darkMetal, LR2Layers.Debris);
                rail.transform.localRotation = Quaternion.Euler(-angle, yaw, 0f);
                Object.DestroyImmediate(rail.GetComponent<Collider>());
            }
        }

        private static void Railing(Transform parent, Vector3 a, Vector3 b, float height = 1.05f)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            Box("RailTop", parent, (a + b) * 0.5f + Vector3.up * height, new Vector3(0.05f, 0.05f, len), _m.darkMetal, LR2Layers.Default, SurfaceType.Metal, yaw);
            Box("RailMid", parent, (a + b) * 0.5f + Vector3.up * height * 0.5f, new Vector3(0.04f, 0.04f, len), _m.darkMetal, LR2Layers.Default, SurfaceType.Metal, yaw);
            int posts = Mathf.Max(2, Mathf.CeilToInt(len / 1.5f) + 1);
            for (int i = 0; i < posts; i++)
                Box("Post", parent, Vector3.Lerp(a, b, i / (float)(posts - 1)) + Vector3.up * height * 0.5f, new Vector3(0.05f, height, 0.05f), _m.darkMetal, LR2Layers.Default, SurfaceType.Metal);
        }

        private static GameObject Container(Transform parent, string name, Vector3 basePos, float yaw, int color)
        {
            var mat = _m.containers[Mathf.Abs(color) % _m.containers.Length];
            bool stacked = basePos.y > 0.5f;
            float settleY = stacked ? basePos.y : basePos.y - 0.02f;
            float jYaw = yaw + R(-1.3f, 1.3f);
            var c = Box(name, parent, new Vector3(basePos.x, settleY + 1.295f, basePos.z), new Vector3(6.06f, 2.59f, 2.44f), mat, LR2Layers.Default, SurfaceType.Metal, jYaw);
            c.transform.localRotation = Quaternion.Euler(stacked ? R(-0.25f, 0.25f) : R(-0.75f, 0.75f), jYaw, stacked ? R(-0.25f, 0.25f) : R(-0.95f, 0.95f));
            ApplyMeshKeepBoxCollider(c, GetWarpedContainerMesh(Mathf.Abs(color) + name.GetHashCode()));
            // door end detail (flush on container face, slightly scarred frame)
            var door = Box("Doors", c.transform, new Vector3(0.503f, 0f, 0f), new Vector3(0.01f, 0.93f, 0.92f), _m.darkMetal);
            Object.DestroyImmediate(door.GetComponent<Collider>());
            return c;
        }

        private static GameObject FindPackPrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            if (_prefabCache.TryGetValue(prefabName, out var cached) && cached != null) return cached;
            foreach (var root in new[] { "Assets/LeartesStudios", "Assets/ScansFactory", "Assets/Polyart", "Assets/LR2/Prefabs" })
            {
                if (!AssetDatabase.IsValidFolder(root)) continue;
                foreach (var g in AssetDatabase.FindAssets(prefabName + " t:Prefab", new[] { root }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (Path.GetFileNameWithoutExtension(p) == prefabName)
                    {
                        var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                        if (loaded != null) { _prefabCache[prefabName] = loaded; return loaded; }
                    }
                }
            }
            return null;
        }

        private static SurfaceType InferSurfaceType(string prefabName)
        {
            string l = (prefabName ?? "").ToLowerInvariant();
            if (l.Contains("wood") || l.Contains("crate") || l.Contains("pallet") || l.Contains("table") ||
                l.Contains("chair") || l.Contains("plank") || l.Contains("cart")) return SurfaceType.Wood;
            if (l.Contains("plastic") || l.Contains("cone") || l.Contains("tire") || l.Contains("trash"))
                return SurfaceType.Plastic;
            if (l.Contains("jersey") || l.Contains("rubble") || l.Contains("rock") || l.Contains("stone") || l.Contains("concrete"))
                return SurfaceType.Concrete;
            return SurfaceType.Metal;
        }

        /// <summary>Instantiates a pack prefab by name, grounds it, ensures a collider and SurfaceIdentifier. Returns null if not found.</summary>
        private static GameObject Prop(string prefabName, Transform parent, Vector3 pos, float yaw, float scale = 1f, bool addCollider = true, SurfaceType? surfaceOverride = null)
        {
            GameObject prefab = FindPackPrefab(prefabName);
            if (prefab == null) return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * scale;
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return go;
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            go.transform.position += Vector3.up * (pos.y - b.min.y); // sit on the target height
            if (addCollider && go.GetComponentInChildren<Collider>() == null)
            {
                b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                var bc = go.AddComponent<BoxCollider>();
                bc.center = go.transform.InverseTransformPoint(b.center);
                Vector3 ls = go.transform.lossyScale;
                bc.size = new Vector3(b.size.x / Mathf.Max(0.001f, ls.x), b.size.y / Mathf.Max(0.001f, ls.y), b.size.z / Mathf.Max(0.001f, ls.z));
            }
            if (addCollider && go.GetComponent<SurfaceIdentifier>() == null)
                go.AddComponent<SurfaceIdentifier>().surface = surfaceOverride ?? InferSurfaceType(prefabName);
            return go;
        }

        private static GameObject PropOrBox(string prefabName, string fallbackName, Transform parent, Vector3 pos, float yaw, float scale, Vector3 fallbackSize, Material fallbackMat, SurfaceType surface)
        {
            var p = Prop(prefabName, parent, pos, yaw, scale, true, surface);
            if (p != null) return p;
            return Box(fallbackName, parent, pos + Vector3.up * (fallbackSize.y * 0.5f), fallbackSize, fallbackMat, LR2Layers.Default, surface, yaw);
        }

        /// <summary>
        /// Replaces a primitive box/cylinder with an in-repo pack prefab scaled to preserve exact cover height,
        /// footprint, BoxCollider, and SurfaceIdentifier (B75). Falls back to Box() if the pack prefab is missing.
        /// </summary>
        private static GameObject FittedPackProp(string name, string prefabName, Transform parent, Vector3 center, Vector3 targetSize,
            float yaw, SurfaceType surface, int layer = LR2Layers.Default, Material overrideMat = null, bool addCollider = true)
        {
            var prefab = FindPackPrefab(prefabName);
            if (prefab == null)
                return Box(name, parent, center, targetSize, overrideMat ?? _m.concrete, layer, surface, yaw);

            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.localPosition = center;
            root.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            root.layer = layer;
            if (addCollider)
            {
                var bc = root.AddComponent<BoxCollider>();
                bc.size = targetSize;
            }
            root.AddComponent<SurfaceIdentifier>().surface = surface;

            var vis = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
            vis.name = "Mesh";
            foreach (var col in vis.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
            foreach (var tr in vis.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = layer;
            if (overrideMat != null)
            {
                foreach (var r in vis.GetComponentsInChildren<Renderer>(true))
                    if (!(r is ParticleSystemRenderer)) r.sharedMaterial = overrideMat;
            }

            var mfs = vis.GetComponentsInChildren<MeshFilter>(true);
            Bounds local = default;
            bool has = false;
            Matrix4x4 inv = vis.transform.worldToLocalMatrix;
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
                static Vector3 ClampSquash(Vector3 v)
                {
                    float x = Mathf.Max(0.05f, v.x), y = Mathf.Max(0.05f, v.y), z = Mathf.Max(0.05f, v.z);
                    float g = Mathf.Pow(x * y * z, 1f / 3f);
                    const float maxHalf = 1.42f; // <= 2.02x max/min (cleanly under LR2PropAudit 2.2x threshold)
                    return new Vector3(
                        Mathf.Clamp(x, g / maxHalf, g * maxHalf),
                        Mathf.Clamp(y, g / maxHalf, g * maxHalf),
                        Mathf.Clamp(z, g / maxHalf, g * maxHalf));
                }

                bool targetWideX = targetSize.x >= targetSize.z;
                bool meshWideX = local.size.x >= local.size.z;
                bool rotate90 = Mathf.Max(targetSize.x, targetSize.z) > 1.3f * Mathf.Min(targetSize.x, targetSize.z)
                                && Mathf.Max(local.size.x, local.size.z) > 1.3f * Mathf.Min(local.size.x, local.size.z)
                                && targetWideX != meshWideX;
                if (rotate90)
                {
                    vis.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                    float mx = Mathf.Max(0.05f, local.size.z);
                    float my = Mathf.Max(0.05f, local.size.y);
                    float mz = Mathf.Max(0.05f, local.size.x);
                    Vector3 s = ClampSquash(new Vector3(targetSize.x / mx, targetSize.y / my, targetSize.z / mz));
                    vis.transform.localScale = new Vector3(s.z, s.y, s.x);
                    Vector3 rotatedCenter = Quaternion.Euler(0f, 90f, 0f) * new Vector3(local.center.x * s.z, local.center.y * s.y, local.center.z * s.x);
                    vis.transform.localPosition = -rotatedCenter;
                    var boxCol = root.GetComponent<BoxCollider>();
                    if (boxCol != null) boxCol.size = new Vector3(targetSize.x, local.size.y * s.y, targetSize.z);
                }
                else
                {
                    Vector3 s = ClampSquash(new Vector3(
                        targetSize.x / Mathf.Max(0.05f, local.size.x),
                        targetSize.y / Mathf.Max(0.05f, local.size.y),
                        targetSize.z / Mathf.Max(0.05f, local.size.z)));
                    vis.transform.localScale = s;
                    vis.transform.localPosition = -Vector3.Scale(local.center, s);
                    var boxCol = root.GetComponent<BoxCollider>();
                    if (boxCol != null) boxCol.size = new Vector3(targetSize.x, local.size.y * s.y, targetSize.z);
                }
            }
            return root;
        }

        private static float R(float a, float b) => (float)(a + _rnd.NextDouble() * (b - a));

        // ================================================================================================= geometry
        // ------------------------------------------------------------------ terrain
        private static Terrain _terrain;
        private const float TerrainBaseY = -3f, TerrainHeight = 30f;

        /// <summary>Building footprints that must be perfectly flat (min.x, min.z, max.x, max.z).</summary>
        private static readonly Vector4[] FlatZones =
        {
            new Vector4(-26.5f, -8.5f, -15.5f, 4.5f),   // workshop
            new Vector4(-4.9f, -3.3f, 5.9f, 5.8f),      // control house + stairs
            new Vector4(-15.8f, -22.4f, 15.8f, -9.4f),  // warehouse + loading dock + ramps
            new Vector4(11.2f, -14.8f, 26.6f, 8.0f),    // container yard
            new Vector4(-19.6f, 11.2f, -10.6f, 18.6f),  // quay west stack + loose container + stairs
            new Vector4(-11.2f, 15.6f, 9.4f, 18.5f),    // quay stacks + bridge
            new Vector4(11.6f, 18.0f, 18.6f, 21.0f),    // quay loose container (east)
            new Vector4(-30f, 20.5f, 30f, 26f),         // quay strip (playable)
            new Vector4(-76f, 21.5f, 76f, 26.5f),       // full QuaySlab width so hillside never pierces slab outside play
        };

        /// <summary>Designed ground height (metres, relative to y = 0) at a world XZ position.</summary>
        private static float DesignHeight(float x, float z)
        {
            // Rolling ground: two octaves, ~±1.6 m in the open yard
            float h = (Mathf.PerlinNoise(x * 0.05f + 11.3f, z * 0.05f + 7.1f) - 0.5f) * 3.2f
                    + (Mathf.PerlinNoise(x * 0.16f + 50f, z * 0.16f + 50f) - 0.5f) * 0.6f;
            float Gauss(float cx, float cz, float rx, float rz, float amp)
            {
                float dx = (x - cx) / rx, dz = (z - cz) / rz;
                return amp * Mathf.Exp(-(dx * dx + dz * dz));
            }
            h += Gauss(-12.5f, 8.5f, 7f, 6f, 3.0f);      // NW berm: high ground over the north yard
            h += Gauss(4f, 9.5f, 6f, 3.5f, 2.2f);         // central hill between house and quay
            h += Gauss(11f, -3f, 5f, 4.5f, 1.8f);         // east hump next to mid
            h += Gauss(3f, -6.4f, 9f, 1.6f, -1.5f);       // drainage ditch across mid-south (trench)
            h += Gauss(-11f, -2f, 4f, 4f, -1.2f);         // west dip in front of the workshop
            // Outside the walls: hills for the skyline
            float ox = Mathf.Max(0f, Mathf.Abs(x) - 27.5f), oz = Mathf.Max(0f, -23.5f - z);
            float outside = Mathf.Sqrt(ox * ox + oz * oz);
            if (outside > 0f)
                h += outside * 0.25f + Mathf.SmoothStep(0f, 1f, outside / 22f) * (Mathf.PerlinNoise(x * 0.02f, z * 0.02f) * 16f);
            // Flatten building pads (just below 0 so concrete slabs sit on top without z-fighting)
            foreach (var r in FlatZones)
            {
                float dx = Mathf.Max(r.x - x, 0f, x - r.z), dz = Mathf.Max(r.y - z, 0f, z - r.w);
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d / 4.5f));
                h = Mathf.Lerp(-0.02f, h, t);
            }
            return h;
        }

        private static float GroundY(float x, float z)
        {
            if (_terrain == null) return 0f;
            return _terrain.SampleHeight(new Vector3(x, 0f, z)) + _terrain.transform.position.y;
        }

        /// <summary>Puts a ground-level design position (y = 0) onto the exact terrain elevation (incl. ditches/dips).</summary>
        private static Vector3 G(Vector3 p, float offset = 0f) => new Vector3(p.x, GroundY(p.x, p.z) + offset, p.z);

        /// <summary>Standing position for spawns/markers: on terrain, never below building floors.</summary>
        private static Vector3 Stand(Vector3 p) => new Vector3(p.x, Mathf.Max(GroundY(p.x, p.z) + 0.05f, 0.12f), p.z);

        private static void BuildTerrain(Transform parent)
        {
            const int res = 257;
            var data = new TerrainData { heightmapResolution = res };
            Vector3 origin = new Vector3(-80f, TerrainBaseY, -70f);
            data.size = new Vector3(160f, TerrainHeight, 96f);
            var heights = new float[res, res];
            for (int iz = 0; iz < res; iz++)
                for (int ix = 0; ix < res; ix++)
                {
                    float x = origin.x + data.size.x * ix / (res - 1), z = origin.z + data.size.z * iz / (res - 1);
                    heights[iz, ix] = Mathf.Clamp01((DesignHeight(x, z) - TerrainBaseY) / TerrainHeight);
                }
            data.SetHeights(0, 0, heights);

            // Layers from Leartes photoscan textures
            TerrainLayer Layer(string name, string baseName, string normalName, string maskName, bool ard, Color tint, float tile)
            {
                var l = new TerrainLayer
                {
                    diffuseTexture = FindTex(baseName),
                    normalMapTexture = FindTex(normalName),
                    maskMapTexture = MakeMask(FindTex(maskName), ard, $"{MatDir}/Masks/{maskName}_HDRPMask.png"),
                    tileSize = new Vector2(tile, tile),
                    diffuseRemapMax = tint,
                    normalScale = 1.2f,
                    smoothness = 0.12f
                };
                return SaveAsset(l, $"{MatDir}/Terrain/{name}.terrainlayer");
            }
            // forest floor (layer 0 = old yard, now moss/leaf litter), mud, gravel/old concrete
            var asphalt = Layer("TL_ForestFloor", "T_en_landscape_01_01", "T_en_landscape_01_01_n", "", false, new Color(0.8f, 0.8f, 0.78f, 1f), 7f);
            if (asphalt.diffuseTexture == null) asphalt = Layer("TL_ForestFloor", "T_BrokenAsphalt_BC", "T_BrokenAsphalt_N", "T_Brokenasphalt_ARD", true, new Color(0.78f, 0.78f, 0.78f, 1f), 6f);
            var dirt = Layer("TL_Mud", "T_Mud_BC", "T_Mud_N", "T_Mud_ARD", true, new Color(0.8f, 0.76f, 0.7f, 1f), 4f);
            var concrete = Layer("TL_OldConcrete", "T_BrokenAsphalt_BC", "T_BrokenAsphalt_N", "T_Brokenasphalt_ARD", true, new Color(0.7f, 0.72f, 0.68f, 1f), 5f);
            data.terrainLayers = new[] { asphalt, dirt, concrete };
            int ar = data.alphamapResolution = 256;
            var alpha = new float[ar, ar, 3];
            for (int iz = 0; iz < ar; iz++)
                for (int ix = 0; ix < ar; ix++)
                {
                    float x = origin.x + data.size.x * ix / (ar - 1), z = origin.z + data.size.z * iz / (ar - 1);
                    float h = DesignHeight(x, z);
                    float slope = data.GetSteepness((float)ix / (ar - 1), (float)iz / (ar - 1));
                    bool outside = Mathf.Abs(x) > 28f || z < -24f;
                    float dirtW = Mathf.Clamp01((slope - 8f) / 12f) + Mathf.Clamp01((h - 0.9f) * 0.8f) + (outside ? 1f : 0f)
                                  + Mathf.Clamp01((Mathf.PerlinNoise(x * 0.09f + 3f, z * 0.09f + 9f) - 0.62f) * 4f);
                    float concW = 0f;
                    foreach (var r in FlatZones)
                    {
                        float dx = Mathf.Max(r.x - x, 0f, x - r.z), dz = Mathf.Max(r.y - z, 0f, z - r.w);
                        concW = Mathf.Max(concW, 1f - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dz * dz) / 2.5f));
                    }
                    dirtW = Mathf.Clamp01(dirtW) * (1f - concW);
                    float asphaltW = Mathf.Clamp01(1f - dirtW - concW);
                    float sum = asphaltW + dirtW + concW + 1e-4f;
                    alpha[iz, ix, 0] = asphaltW / sum;
                    alpha[iz, ix, 1] = dirtW / sum;
                    alpha[iz, ix, 2] = concW / sum;
                }
            data.SetAlphamaps(0, 0, alpha);
            var savedData = SaveAsset(data, "Assets/LR2/Scenes/LR2_Saltworks_Terrain.asset");

            var go = Terrain.CreateTerrainGameObject(savedData);
            go.name = "Terrain";
            go.transform.SetParent(parent, false);
            go.transform.position = origin;
            go.layer = LR2Layers.Default;
            _terrain = go.GetComponent<Terrain>();
            _terrain.drawInstanced = true;
            _terrain.basemapDistance = 220f;
            _terrain.heightmapPixelError = 4f;
            go.AddComponent<SurfaceIdentifier>().surface = SurfaceType.Dirt;
        }

        private static void BuildGround()
        {
            var g = Child(_structures, "Ground");
            BuildTerrain(g);
            Box("QuaySlab", g, new Vector3(0f, -0.22f, 24f), new Vector3(150f, 0.5f, 4f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            // Painted safety lines (conformed to terrain slope so ends never float)
            for (int i = -5; i <= 5; i++)
            {
                float lx = i * 4.5f, lz = -8.4f;
                float yL = GroundY(lx - 1.2f, lz), yR = GroundY(lx + 1.2f, lz);
                float slopeDeg = Mathf.Atan2(yR - yL, 2.4f) * Mathf.Rad2Deg;
                var line = Box("Line", g, new Vector3(lx, Mathf.Max((yL + yR) * 0.5f, -0.02f) + 0.025f, lz), new Vector3(2.4f, 0.015f, 0.12f), _m.curb);
                line.transform.localRotation = Quaternion.Euler(0f, 0f, slopeDeg);
                Object.DestroyImmediate(line.GetComponent<Collider>());
            }
            // Water beyond the quay
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "Water";
            water.transform.SetParent(g, false);
            water.transform.localPosition = new Vector3(0f, -1.3f, 100f);
            water.transform.localScale = new Vector3(40f, 1f, 15f);
            water.GetComponent<Renderer>().sharedMaterial = _m.water;
            Object.DestroyImmediate(water.GetComponent<Collider>());
            Box("QuayWall", g, new Vector3(0f, -1.0f, 26.1f), new Vector3(150f, 2.2f, 0.4f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
        }

        private static void BuildControlHouse()
        {
            var h = Child(_structures, "ControlHouse");
            const float x0 = -4.5f, x1 = 4.5f, z0 = -1.5f, z1 = 5.5f, hgt = 3.4f, t = 0.25f;
            Vector4 Door(float off) => new Vector4(off, 1.4f, 0f, 2.3f);
            Vector4 Win(float off) => new Vector4(off, 1.6f, 1.0f, 2.1f);
            Wall("WallWest", h, new Vector3(x0, 0f, z0), new Vector3(x0, 0f, z1), hgt, t, _m.wallPlaster, Door(3.5f));
            Wall("WallEast", h, new Vector3(x1, 0f, z0), new Vector3(x1, 0f, z1), hgt, t, _m.wallPlaster, Door(3.5f));
            Wall("WallNorth", h, new Vector3(x0, 0f, z1), new Vector3(x1, 0f, z1), hgt, t, _m.wallPlaster, Win(2.5f), Win(6.5f));
            Wall("WallSouth", h, new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0), hgt, t, _m.wallPlaster, Win(3f), Win(7f));
            Box("Floor", h, new Vector3(0f, 0.05f, 2f), new Vector3(9f, 0.1f, 7f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            Box("Roof", h, new Vector3(0f, hgt + 0.1f, 2f), new Vector3(9.3f, 0.2f, 7.3f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            float py = hgt + 0.2f;
            // Parapet (gap at the south-west for the stairs landing; non-overlapping corners)
            Box("ParapetN", h, new Vector3(0f, py + 0.5f, z1 + 0.05f), new Vector3(9.3f, 1.0f, 0.2f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);
            Box("ParapetE", h, new Vector3(x1 + 0.05f, py + 0.5f, 2f), new Vector3(0.2f, 1.0f, 6.89f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);
            Box("ParapetW", h, new Vector3(x0 - 0.05f, py + 0.5f, 2.095f), new Vector3(0.2f, 1.0f, 7.09f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);
            Box("ParapetS", h, new Vector3(0.95f, py + 0.5f, z0 - 0.05f), new Vector3(7.09f, 1.0f, 0.2f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);
            // Exterior steel stairs (south) + landing onto the roof (1 cm gap before Roof edge to avoid top Z-fighting)
            Stairs("Stairs", h, new Vector3(5.6f, 0f, -2.45f), new Vector3(-1.4f, py, -2.45f), 1.3f, _m.steelGrate);
            Box("Landing", h, new Vector3(-2.75f, py - 0.05f, -2.38f), new Vector3(2.7f, 0.1f, 1.44f), _m.steelGrate, LR2Layers.Default, SurfaceType.Metal);
            Railing(h, new Vector3(-4.1f, py, -3.1f), new Vector3(-1.4f, py, -3.1f));
            // Roof clutter: AC unit + vent + pipework stacks (cover on the power position)
            FittedPackProp("ACUnit", "SM_ConcreteColum2", h, new Vector3(2.2f, py + 0.55f, 3.6f), new Vector3(1.8f, 1.1f, 1.2f), 0f, SurfaceType.Metal);
            PropOrBox("SM_Smallpipe2", "Vent", h, new Vector3(-2.4f, py, 4.2f), 90f, 1f, new Vector3(0.9f, 0.8f, 0.9f), _m.rust, SurfaceType.Metal);
            // Interior: desk + lockers + detailed crate & industrial chair
            var desk = FittedPackProp("Desk", "SM_Table_01", h, new Vector3(-2.6f, 0.5f, 4.4f), new Vector3(2.2f, 0.8f, 0.9f), 0f, SurfaceType.Wood);
            FittedPackProp("Lockers", "SM_Crate_02", h, new Vector3(3.9f, 1.05f, 4.6f), new Vector3(0.6f, 1.9f, 1.6f), 90f, SurfaceType.Wood);
            PropOrBox("SM_Crate_01", "Crate", h, new Vector3(1.2f, 0.1f, 0.2f), 14f, 0.95f, new Vector3(0.9f, 0.9f, 0.9f), _m.wood, SurfaceType.Wood);
            Prop("SM_Chair_01", h, new Vector3(-2.4f, 0.1f, 3.3f), 195f, 1f, true, SurfaceType.Wood);
            float deskTopY = 0.81f;
            if (desk != null)
            {
                var drs = desk.GetComponentsInChildren<Renderer>(true);
                if (drs.Length > 0)
                {
                    deskTopY = drs[0].bounds.max.y;
                    for (int ri = 1; ri < drs.Length; ri++) deskTopY = Mathf.Max(deskTopY, drs[ri].bounds.max.y);
                }
            }
            var chLantern = Prop("SM_Lantern_01", h, new Vector3(-2.6f, deskTopY, 4.4f), 0f, 0.9f, false);
            if (chLantern != null)
            {
                var lrs = chLantern.GetComponentsInChildren<Renderer>(true);
                if (lrs.Length > 0)
                {
                    float minY = lrs[0].bounds.min.y;
                    for (int ri = 1; ri < lrs.Length; ri++) minY = Mathf.Min(minY, lrs[ri].bounds.min.y);
                    chLantern.transform.position += Vector3.up * (deskTopY - 0.005f - minY);
                }
            }
            // Windows glass (thin, bullets penetrate)
            foreach (var (pos, rotY) in new[] { (new Vector3(-2f, 1.55f, z1), 0f), (new Vector3(2f, 1.55f, z1), 0f), (new Vector3(-1.5f, 1.55f, z0), 0f), (new Vector3(2.5f, 1.55f, z0), 0f) })
                Box("Glass", h, pos, new Vector3(1.6f, 1.1f, 0.03f), _m.glass, LR2Layers.Default, SurfaceType.Glass, rotY);
        }

        private static void BuildWorkshop()
        {
            var w = Child(_structures, "Workshop");
            const float x0 = -26f, x1 = -16f, z0 = -8f, z1 = 4f, hgt = 7f, t = 0.3f;
            Wall("WallEast", w, new Vector3(x1, 0f, z0), new Vector3(x1, 0f, z1), hgt, t, _m.corrugated,
                new Vector4(6f, 5f, 0f, 3.2f), new Vector4(6.5f, 3f, 4.4f, 5.8f));
            Wall("WallWest", w, new Vector3(x0, 0f, z0), new Vector3(x0, 0f, z1), hgt, t, _m.corrugated);
            Wall("WallNorth", w, new Vector3(x0, 0f, z1), new Vector3(x1, 0f, z1), hgt, t, _m.corrugated, new Vector4(7f, 1.4f, 0f, 2.3f), new Vector4(3f, 2.2f, 4.5f, 5.6f));
            Wall("WallSouth", w, new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0), hgt, t, _m.corrugated, new Vector4(3f, 1.4f, 0f, 2.3f));
            Box("Floor", w, new Vector3(-21f, 0.05f, -2f), new Vector3(10f, 0.1f, 12f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            // Upper floor with stairwell opening (west side)
            Box("UpperA", w, new Vector3(-20.2f, 3.5f, -2f), new Vector3(8.4f, 0.2f, 12f), _m.wood, LR2Layers.Default, SurfaceType.Wood);
            Box("UpperB", w, new Vector3(-25.2f, 3.5f, 2.5f), new Vector3(1.6f, 0.2f, 3f), _m.wood, LR2Layers.Default, SurfaceType.Wood);
            Box("UpperC", w, new Vector3(-25.2f, 3.5f, -7.25f), new Vector3(1.6f, 0.2f, 1.5f), _m.wood, LR2Layers.Default, SurfaceType.Wood);
            Stairs("InteriorStairs", w, new Vector3(-25.2f, 0f, -6.4f), new Vector3(-25.2f, 3.6f, 0.9f), 1.3f, _m.wood, false);
            Railing(w, new Vector3(-24.4f, 3.6f, -6.5f), new Vector3(-24.4f, 3.6f, 1.0f));
            Box("Roof", w, new Vector3(-21f, hgt + 0.1f, -2f), new Vector3(10.6f, 0.2f, 12.6f), _m.roof, LR2Layers.Default, SurfaceType.Metal);
            // Upstairs: pallet barricade at the east window + workbench + tool rack (B75 pack meshes)
            FittedPackProp("WindowCover", "SM_Woodpallet_1", w, new Vector3(-16.9f, 4.05f, -2f), new Vector3(0.6f, 0.9f, 2.6f), 90f, SurfaceType.Wood);
            FittedPackProp("Workbench", "SM_Table_02", w, new Vector3(-22f, 0.55f, 3.2f), new Vector3(3f, 0.9f, 0.9f), 0f, SurfaceType.Wood);
            Prop("SM_fireextinguisher", w, new Vector3(-20.2f, 0.1f, 3.1f), 0f);
            FittedPackProp("ToolRack", "SM_Grid1", w, new Vector3(-16.35f, 1.15f, 2.2f), new Vector3(0.4f, 2.1f, 2.5f), 90f, SurfaceType.Metal); // against east wall, clear of InteriorStairs
            Prop("SM_Gascylender", w, new Vector3(-17.4f, 0.1f, -6.6f), 25f, 1f, true, SurfaceType.Metal);
            Prop("SM_Barrel_02", w, new Vector3(-19.5f, 0.1f, -6.5f), 0f, 1f, true, SurfaceType.Metal);
            Prop("SM_Crate_02", w, new Vector3(-19.2f, 0.1f, 2.4f), 8f, 0.95f, true, SurfaceType.Wood);
        }

        private static void BuildWarehouse()
        {
            var wh = Child(_structures, "Warehouse");
            const float x0 = -14f, x1 = 14f, z0 = -22f, z1 = -12f, hgt = 7.5f, t = 0.3f;
            Wall("WallSouth", wh, new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0), hgt, t, _m.corrugated);
            Wall("WallNorth", wh, new Vector3(x0, 0f, z1), new Vector3(x1, 0f, z1), hgt, t, _m.corrugated,
                new Vector4(7f, 4f, 0f, 4.2f), new Vector4(13.75f, 1.4f, 1.2f, 3.5f), new Vector4(19f, 4f, 0f, 4.2f));
            Wall("WallWest", wh, new Vector3(x0, 0f, z0), new Vector3(x0, 0f, z1), hgt, t, _m.corrugated, new Vector4(5f, 3.2f, 0f, 3.6f));
            Wall("WallEast", wh, new Vector3(x1, 0f, z0), new Vector3(x1, 0f, z1), hgt, t, _m.corrugated, new Vector4(5f, 3.2f, 0f, 3.6f));
            Box("Floor", wh, new Vector3(0f, 0.05f, -17f), new Vector3(28.2f, 0.1f, 10.2f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            Box("Roof", wh, new Vector3(0f, hgt + 0.1f, -17f), new Vector3(28.6f, 0.2f, 10.6f), _m.roof, LR2Layers.Default, SurfaceType.Metal);
            // Steel columns
            for (int i = -2; i <= 2; i++) Box("Column", wh, new Vector3(i * 6f, hgt * 0.5f, -17f), new Vector3(0.35f, hgt, 0.35f), _m.darkMetal, LR2Layers.Default, SurfaceType.Metal);
            // Racking rows (cover + sightline breakers)
            void Rack(Vector3 c, float len)
            {
                var rack = Child(wh, "Rack");
                for (int lvl = 0; lvl < 3; lvl++)
                    Box("Shelf", rack, c + Vector3.up * (0.35f + lvl * 1.1f), new Vector3(len, 0.08f, 1.1f), _m.metal, LR2Layers.Default, SurfaceType.Metal);
                for (int s = 0; s <= 2; s++)
                    Box("Upright", rack, c + new Vector3((s - 1) * len * 0.5f, 1.3f, 0f), new Vector3(0.08f, 2.6f, 1.1f), _m.curb, LR2Layers.Default, SurfaceType.Metal);
                FittedPackProp("Goods", "SM_Crate_01", rack, c + new Vector3(-len * 0.2f, 0.78f, 0f), new Vector3(len * 0.36f, 0.72f, 0.9f), 0f, SurfaceType.Wood);
                FittedPackProp("GoodsPallet", "SM_Crate_02", rack, c + new Vector3(len * 0.2f, 1.88f, 0f), new Vector3(len * 0.34f, 0.72f, 0.9f), 10f, SurfaceType.Wood);
            }
            Rack(new Vector3(-8f, 0f, -19.5f), 6f);
            Rack(new Vector3(1f, 0f, -19.5f), 6f);
            Rack(new Vector3(9.5f, 0f, -19.5f), 5f);
            Rack(new Vector3(-4.5f, 0f, -15.6f), 5f);
            Rack(new Vector3(5.5f, 0f, -15.6f), 4f);
            // Smooth interior steps/ramps connecting Warehouse floor (y=0.1) up to LoadingDock platform (y=1.2)
            Stairs("DockApproachW", wh, new Vector3(-7f, 0.1f, -14.6f), new Vector3(-7f, 1.2f, -12.1f), 3.2f, _m.concrete, false);
            Stairs("DockApproachE", wh, new Vector3(5f, 0.1f, -14.6f), new Vector3(5f, 1.2f, -12.1f), 3.2f, _m.concrete, false);
        }

        private static void BuildLoadingDock()
        {
            var d = Child(_structures, "LoadingDock");
            Box("Platform", d, new Vector3(0f, 0.6f, -10.8f), new Vector3(24f, 1.2f, 2.4f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            Box("EdgeStripe", d, new Vector3(0f, 1.21f, -9.65f), new Vector3(24f, 0.02f, 0.12f), _m.curb);
            // Ramps (high end meets Platform at x=±12, y=1.2; low end meets ground at x=±15.6)
            foreach (float s in new[] { -1f, 1f })
            {
                var ramp = Box("Ramp", d, new Vector3(s * 13.8f, 0.55f, -10.8f), new Vector3(3.8f, 0.2f, 2.4f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
                ramp.transform.localRotation = Quaternion.Euler(0f, 0f, -s * 18f);
            }
            for (int i = -3; i <= 3; i += 2) Box("Bumper", d, new Vector3(i * 3.2f, 0.65f, -9.5f), new Vector3(0.5f, 0.6f, 0.2f), _m.darkMetal, LR2Layers.Default, SurfaceType.Plastic);
        }

        private static void BuildContainerYard()
        {
            var y = Child(_structures, "ContainerYard");
            // Stack A (2-high) and stack B (2-high) joined by a catwalk on top
            Container(y, "A1", new Vector3(20f, 0f, -6f), 0f, 0);
            Container(y, "A2", new Vector3(20f, 0f, -3.5f), 0f, 1);
            Container(y, "A3", new Vector3(20f, 2.59f, -6f), 0f, 3);
            Container(y, "A4", new Vector3(20f, 2.59f, -3.5f), 0f, 4);
            Container(y, "B1", new Vector3(20f, 0f, 4f), 0f, 2);
            Container(y, "B2", new Vector3(20f, 0f, 6.5f), 0f, 5);
            Container(y, "B3", new Vector3(20f, 2.59f, 4f), 0f, 1);
            Container(y, "B4", new Vector3(20f, 2.59f, 6.5f), 0f, 0);
            Box("Catwalk", y, new Vector3(20f, 5.13f, 0.25f), new Vector3(1.5f, 0.1f, 5f), _m.steelGrate, LR2Layers.Default, SurfaceType.Metal);
            Railing(y, new Vector3(19.25f, 5.18f, -2.2f), new Vector3(19.25f, 5.18f, 2.7f));
            Railing(y, new Vector3(20.75f, 5.18f, -2.2f), new Vector3(20.75f, 5.18f, 2.7f));
            Stairs("StairsA", y, new Vector3(24.6f, 0f, -14.2f), new Vector3(24.6f, 5.18f, -7.2f), 1.3f, _m.steelGrate);
            Box("StairsTopLanding", y, new Vector3(24.06f, 5.13f, -6.5f), new Vector3(2.02f, 0.1f, 1.6f), _m.steelGrate, LR2Layers.Default, SurfaceType.Metal);
            // Loose containers (clear of LoadingDock East Ramp)
            Container(y, "C1", new Vector3(18.4f, 0f, -12.4f), 90f, 3);
            Container(y, "C2", new Vector3(25.2f, 0f, 1.2f), 90f, 4);
            Container(y, "C3", new Vector3(14.5f, 0f, 1.5f), 0f, 2);
        }

        private static void BuildQuay()
        {
            var q = Child(_structures, "Quay");
            Container(q, "W1", new Vector3(-8f, 0f, 17f), 0f, 5);
            Container(q, "W2", new Vector3(-8f, 2.59f, 17f), 0f, 0);
            Container(q, "E1", new Vector3(6f, 0f, 17f), 0f, 1);
            Container(q, "E2", new Vector3(6f, 2.59f, 17f), 0f, 3);
            Container(q, "L1", new Vector3(-18.5f, 0f, 11.6f), 90f, 2); // clear of StairsWest bottom at (-17.2, 0, 17)
            Container(q, "L2", new Vector3(15f, 0f, 19.5f), 0f, 4);
            Container(q, "L3", new Vector3(-1f, 0f, 21.2f), 0f, 0);
            // Long catwalk bridge between the stacks (flush between W2 and E2, no overlap Z-fighting)
            Box("Bridge", q, new Vector3(-1f, 5.13f, 17f), new Vector3(7.92f, 0.1f, 1.5f), _m.steelGrate, LR2Layers.Default, SurfaceType.Metal);
            Railing(q, new Vector3(-5f, 5.18f, 16.25f), new Vector3(3f, 5.18f, 16.25f));
            Railing(q, new Vector3(-5f, 5.18f, 17.75f), new Vector3(3f, 5.18f, 17.75f));
            Stairs("StairsWest", q, new Vector3(-17.2f, 0f, 17f), new Vector3(-11.1f, 5.18f, 17f), 1.3f, _m.steelGrate);
            // Quay edge: curb + bollards
            Box("Curb", q, new Vector3(0f, 0.15f, 22.9f), new Vector3(60f, 0.3f, 0.4f), _m.curb, LR2Layers.Default, SurfaceType.Concrete);
            for (float x = -24f; x <= 24f; x += 6f)
            {
                PropOrBox("SM_Tire", "Bollard", q, new Vector3(x, 0.05f, 22.4f), R(0f, 90f), 1f, new Vector3(0.55f, 0.45f, 0.55f), _m.darkMetal, SurfaceType.Plastic);
                if (Mathf.RoundToInt(x) % 12 == 0)
                    Prop("SM_Docks_Rope_01", q, new Vector3(x + 1.2f, 0.05f, 22.3f), R(0f, 360f), 0.9f, false);
            }
            Prop("SM_Lantern_Pole", q, new Vector3(-14f, 0.05f, 22.1f), 180f, 1f, true, SurfaceType.Metal);
            Prop("SM_Lantern_Pole", q, new Vector3(14f, 0.05f, 22.1f), 180f, 1f, true, SurfaceType.Metal);
        }

        private static void BuildMidYard()
        {
            var y = Child(_structures, "MidYard");
            void RubbleCluster(string name, Vector3 c, float yaw, float scale)
            {
                var rubble = LoadRuins(name);
                if (rubble != null)
                {
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(rubble, _props);
                    go.transform.position = G(c);
                    go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                    go.transform.localScale = Vector3.one * scale;
                    if (go.GetComponentInChildren<Collider>() == null)
                    {
                        var rs = go.GetComponentsInChildren<Renderer>();
                        if (rs.Length > 0)
                        {
                            var b = rs[0].bounds;
                            foreach (var r in rs) b.Encapsulate(r.bounds);
                            var bc = go.AddComponent<BoxCollider>();
                            bc.center = go.transform.InverseTransformPoint(b.center);
                            bc.size = b.size / Mathf.Max(0.01f, scale);
                        }
                    }
                    go.AddComponent<SurfaceIdentifier>().surface = SurfaceType.Concrete;
                }
            }
            RubbleCluster("SM_Rubble_01_01", new Vector3(-9f, 0f, -4.5f), 25f, 1.15f);
            RubbleCluster("SM_Rubble_02_01", new Vector3(9.5f, 0f, 6.5f), -40f, 1.15f);
            Prop("SM_Jersey", _props, G(new Vector3(-9.5f, 0f, -2.5f)), 25f, 1f, true, SurfaceType.Concrete);
            Prop("SM_Jersey", _props, G(new Vector3(9f, 0f, 4.5f)), -35f, 1f, true, SurfaceType.Concrete);
            int yardIdx = 0;
            void Block(Vector3 p, float yaw)
            {
                yardIdx++;
                Vector3 gp = G(p);
                string pref = yardIdx % 3 == 0 ? "SM_Cablespool" : (yardIdx % 2 == 0 ? "SM_Crate_01" : "SM_Jersey");
                SurfaceType surf = pref == "SM_Jersey" ? SurfaceType.Concrete : (pref == "SM_Crate_01" ? SurfaceType.Wood : SurfaceType.Metal);
                var go = Prop(pref, _props, gp, yaw + R(-4f, 4f), pref == "SM_Crate_01" ? 1.05f : 1f, true, surf);
                if (go == null)
                {
                    var blk = Box("ConcreteBlock", y, G(p, 0.44f), new Vector3(1.6f, 0.9f, 0.8f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete, yaw + R(-3f, 3f));
                    ApplyMeshKeepBoxCollider(blk, GetWarpedWallMesh(Mathf.RoundToInt(p.x * 13 + p.z), true, 0.16f, 0.038f));
                }
            }
            Block(new Vector3(-4f, 0f, -8f), 0f);
            Block(new Vector3(6f, 0f, -6.5f), 15f);
            Block(new Vector3(-12.5f, 0f, 8f), 80f);
            Block(new Vector3(10f, 0f, 12.5f), 0f);
            Block(new Vector3(-6f, 0f, 11f), -20f);
            Block(new Vector3(12.5f, 0f, -4f), 90f);
            // B46: waist-high plaza cargo breaks every ~10m across central engagement lane
            Block(new Vector3(-1.5f, 0f, -3.2f), 8f);
            Block(new Vector3(2.2f, 0f, 3.8f), -12f);
        }

        private static void BuildBoundary()
        {
            var b = Child(_structures, "Boundary");
            void BrokenBoundary(string n, Vector3 center, float len, float height, float thick, float yaw, int seed)
            {
                var w = Box(n, b, center, new Vector3(len, height, thick), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete, yaw);
                ApplyMeshKeepBoxCollider(w, GetWarpedWallMesh(seed, true, 0.20f, 0.05f));
                AddWarTornWallDetails(b, center, len, center.y + height * 0.44f, yaw, _m.wallPlaster, seed, true, false);
            }
            BrokenBoundary("WallWest", new Vector3(-28.3f, 1.15f, 0f), 52f, 6.4f, 0.55f, 90f, 11);
            BrokenBoundary("WallSouth", new Vector3(0f, 1.15f, -24.3f), 57f, 6.4f, 0.55f, 0f, 23);
            BrokenBoundary("WallEast", new Vector3(28.3f, 1.15f, -6f), 37f, 6.4f, 0.55f, 90f, 37);
            // East perimeter container wall (3-high) for a "full" skyline
            for (int i = 0; i < 4; i++)
                for (int lvl = 0; lvl < 3; lvl++)
                    Container(b, $"EastWall{i}_{lvl}", new Vector3(30.5f, lvl * 2.59f, -18f + i * 6.4f), 90f, i + lvl);
            // Invisible clip walls (block soldiers, not bullets)
            var clip = Child(b, "PlayerClip");
            void Clip(Vector3 c, Vector3 s)
            {
                var go = new GameObject("Clip");
                go.transform.SetParent(clip, false);
                go.transform.localPosition = c;
                go.layer = LR2Layers.PlayerClip;
                go.AddComponent<BoxCollider>().size = s;
            }
            Clip(new Vector3(-27.9f, 6f, 0f), new Vector3(0.4f, 12f, 52f));
            Clip(new Vector3(27.9f, 6f, 0f), new Vector3(0.4f, 12f, 52f));
            Clip(new Vector3(0f, 6f, -23.9f), new Vector3(57f, 12f, 0.4f));
            Clip(new Vector3(0f, 6f, 23.4f), new Vector3(57f, 12f, 0.4f));
            Clip(new Vector3(0f, 12.2f, 0f), new Vector3(57f, 0.4f, 52f)); // ceiling: no climbing out
            var ceiling = clip.GetChild(clip.childCount - 1).gameObject;
            var mod = ceiling.AddComponent<NavMeshModifier>();
            mod.ignoreFromBuild = true; // never bake walkable area on top of the ceiling
        }

        private static void BuildBackground()
        {
            var bg = _background;
            int found = 0;
            void P(string n, Vector3 p, float yaw, float s = 1f) { if (Prop(n, bg, p.y == 0f ? G(p) : p, yaw, s, false) != null) found++; }
            P("SM_Silo", new Vector3(-12f, 0f, -36f), 0f, 1.2f);
            P("SM_Slio1", new Vector3(-2f, 0f, -38f), 30f, 1.2f);
            P("SM_Slio2", new Vector3(9f, 0f, -35f), 60f, 1.2f);
            P("SM_CylenderBuilding", new Vector3(22f, 0f, -36f), 0f, 1f);
            P("SM_Crane_01", new Vector3(-20f, 0f, 30f), 90f, 1f);
            P("SM_Crane_02", new Vector3(16f, 0f, 31f), -90f, 1f);
            P("SM_MainBuilding1", new Vector3(-46f, 0f, -12f), 90f, 1f);
            P("SM_MainBuilding3", new Vector3(-48f, 0f, 14f), 90f, 1f);
            P("SM_MainBuilding5", new Vector3(46f, 0f, -22f), -90f, 1f);
            P("SM_MainBuilding7", new Vector3(50f, 0f, 12f), -90f, 1f);
            P("SM_Boat", new Vector3(2f, -1.3f, 44f), 75f, 1f);
            P("SM_Ship_Part_01", new Vector3(-30f, -1.3f, 58f), 10f, 1f);
            // Pack industrial building silhouettes replacing primitive background blocks (B75)
            P("SM_MainBuilding2", new Vector3(-6f, 0f, -46f), 0f, 1.25f);
            P("SM_MainBuilding4", new Vector3(16f, 0f, -45f), 180f, 1.15f);
            P("SM_MainBuilding6", new Vector3(-40f, 0f, -2f), 90f, 1.2f);
            P("SM_Building", new Vector3(42f, 0f, -6f), -90f, 1.15f);
            Debug.Log($"[LR2] Background: {found} Leartes set pieces placed.");
        }

        private static void PlaceDecals()
        {
            string[] names =
            {
                "M_CrackDecal", "M_CrackDecal_2", "M_CrackDecal_3", "M_CrackDecal_4",
                "M_LeakDecal", "M_LeakDecal_2", "M_LeakDecal_3", "M_LeakDecal_4",
                "M_RustDecal", "M_RustDecal_2", "M_RustDecal_3", "M_RustDecal_4",
                "M_GraffittiDecal", "M_GraffittiDecal_2", "M_Puddle", "M_Decal_Puddle_A_MIC", "M_Decal_Salt"
            };
            int placed = PlaceProjectedDecals(PlayMin, PlayMax, 240, names);
            Debug.Log($"[LR2] Decals placed: {placed}");
        }

        private static void PlaceTargetedWearDecals()
        {
            if (_decals == null) return;
            var rust = FindLeartes("M_RustDecal_3") ?? FindLeartes("M_RustDecal");
            var leak = FindLeartes("M_LeakDecal_2") ?? FindLeartes("M_LeakDecal");
            var crack = FindLeartes("M_CrackDecal_3") ?? FindLeartes("M_CrackDecal");
            Material[] seamPool = { rust, leak, crack };
            int placed = 0;

            if (_props != null)
            {
                for (int i = 0; i < _props.childCount && placed < 48; i++)
                {
                    var ch = _props.GetChild(i);
                    if (ch == null) continue;
                    var mat = seamPool[i % seamPool.Length];
                    if (mat == null) continue;
                    Vector3 origin = ch.position + Vector3.up * 0.6f;
                    if (Physics.Raycast(origin, Vector3.down, out var hit, 2.5f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)
                        && hit.normal.y > 0.75f)
                    {
                        CreateDecal(mat, hit.point, hit.normal, R(1.1f, 2.0f), true);
                        placed++;
                    }
                }
            }
            Debug.Log($"[LR2] Targeted seam/wear decals placed: {placed}");
        }

        private static int PlaceProjectedDecals(Vector2 playMin, Vector2 playMax, int tries, string[] matNames)
        {
            var mats = new List<Material>();
            foreach (var n in matNames)
            {
                var m = FindLeartes(n);
                if (m != null) mats.Add(m);
            }
            if (mats.Count == 0) return 0;
            var placedPoints = new List<Vector3>();
            int placed = 0;
            for (int i = 0; i < tries; i++)
            {
                Vector3 o = new Vector3(R(playMin.x, playMax.x), 11f, R(playMin.y, playMax.y));
                Vector3 dir = i % 3 == 0 ? Vector3.down : new Vector3(R(-1f, 1f), -0.15f, R(-1f, 1f)).normalized;
                if (i % 3 != 0) o.y = R(0.6f, 3.6f);
                if (!Physics.Raycast(o, dir, out var hit, 28f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                var mat = mats[i % mats.Count];
                bool floorDecal = hit.normal.y > 0.7f;
                if ((mat.name.Contains("Puddle") || mat.name.Contains("Salt")) && !floorDecal) continue;
                if (hit.collider != null && hit.collider.GetComponentInParent<DestructibleProp>() != null) continue;
                // Keep decals spaced apart so overlapping projectors never shimmer or Z-fight
                bool tooClose = false;
                for (int j = 0; j < placedPoints.Count; j++)
                    if ((placedPoints[j] - hit.point).sqrMagnitude < 1.6f * 1.6f) { tooClose = true; break; }
                if (tooClose) continue;
                var dp = CreateDecal(mat, hit.point, hit.normal, floorDecal ? R(1.5f, 3.2f) : R(0.8f, 2.0f), floorDecal);
                if (dp != null) { placedPoints.Add(hit.point); placed++; }
            }
            return placed;
        }

        // ------------------------------------------------------------------ B101 cinematic camera post-processing
        // User 11-19 00:34: "ADD CAMERA post processing effects to make the scenes more realistic". A real camera look on top of the
        // existing ACES / SSAO / SSR / contact shadows: bloom that actually blooms with lens dirt around bright lights, screen-space
        // lens flares with soft anamorphic streaks, a touch of chromatic aberration at the edges, and a subtle cool-shadow / warm-
        // highlight split tone. Kept subtle so it never hides a target.
        private static Texture2D _lensDirt;

        private static Texture2D LensDirtTexture()
        {
            if (_lensDirt != null) return _lensDirt;
            const string path = "Assets/LR2/Data/Lighting/T_LensDirt.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return _lensDirt = existing;
            const int w = 1024, h = 576;
            var px = new Color[w * h];
            var rng = new System.Random(1911);
            void Blob(float cx, float cy, float r, float v)
            {
                int x0 = Mathf.Max(0, (int)(cx - r)), x1 = Mathf.Min(w - 1, (int)(cx + r));
                int y0 = Mathf.Max(0, (int)(cy - r)), y1 = Mathf.Min(h - 1, (int)(cy + r));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / r;
                        if (d >= 1f) continue;
                        float a = (1f - d * d) * v;
                        px[y * w + x].r += a; px[y * w + x].g += a; px[y * w + x].b += a;
                    }
            }
            for (int i = 0; i < 70; i++) Blob((float)rng.NextDouble() * w, (float)rng.NextDouble() * h, 18f + (float)rng.NextDouble() * 95f, 0.05f + (float)rng.NextDouble() * 0.16f);
            for (int i = 0; i < 380; i++) Blob((float)rng.NextDouble() * w, (float)rng.NextDouble() * h, 1.2f + (float)rng.NextDouble() * 3.2f, 0.25f + (float)rng.NextDouble() * 0.45f);
            for (int i = 0; i < px.Length; i++) { float v = Mathf.Clamp01(px[i].r); px[i] = new Color(v, v, v, 1f); }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels(px);
            tex.Apply(false, false);
            return _lensDirt = SaveTexture(tex, path, false, false, true);
        }

        private static void ApplyCinematicPost(VolumeProfile profile, float strength)
        {
            if (profile == null) return;
            if (!profile.TryGet<Bloom>(out var bloom)) bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(0.12f * strength);
            bloom.threshold.Override(1.25f);
            bloom.scatter.Override(0.6f);
            bloom.dirtTexture.Override(LensDirtTexture());
            bloom.dirtIntensity.Override(1.6f * strength);
            if (!profile.TryGet<ScreenSpaceLensFlare>(out var flare)) flare = profile.Add<ScreenSpaceLensFlare>(true);
            flare.intensity.Override(0.32f * strength);
            flare.firstFlareIntensity.Override(0.6f);
            flare.secondaryFlareIntensity.Override(0.35f);
            flare.warpedFlareIntensity.Override(0.25f);
            flare.streaksIntensity.Override(0.45f);
            flare.streaksLength.Override(0.45f);
            flare.streaksThreshold.Override(0.4f);
            flare.vignetteEffect.Override(1f);
            flare.chromaticAbberationIntensity.Override(0.35f);
            if (!profile.TryGet<ChromaticAberration>(out var ca)) ca = profile.Add<ChromaticAberration>(true);
            ca.intensity.Override(0.07f);
            if (!profile.TryGet<SplitToning>(out var split)) split = profile.Add<SplitToning>(true);
            split.shadows.Override(new Color(0.42f, 0.47f, 0.52f));
            split.highlights.Override(new Color(0.55f, 0.5f, 0.44f));
            split.balance.Override(-12f);
            EditorUtility.SetDirty(profile);
        }

        private static DecalProjector CreateDecal(Material mat, Vector3 point, Vector3 normal, float sizeXY, bool floorDecal)
        {
            var go = new GameObject("Decal_" + mat.name);
            go.transform.SetParent(_decals, false);
            go.transform.position = point + normal * 0.02f;
            go.transform.rotation = Quaternion.LookRotation(-normal, floorDecal ? Vector3.forward : Vector3.up) * Quaternion.Euler(0f, 0f, R(0f, 360f));
            var dp = go.AddComponent<DecalProjector>();
            dp.material = mat;
            dp.size = new Vector3(sizeXY, sizeXY, 0.20f); // slim depth prevents bleed-through on thin walls
            dp.startAngleFade = 65f;
            dp.endAngleFade = 95f;
            dp.drawDistance = 75f;
            return dp;
        }

        private static void DressProps()
        {
            // called from BuildGameplay after structures exist (needs colliders for spacing checks)
        }

        private static void MarkStatic(Transform t)
        {
            var flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic |
                        StaticEditorFlags.ReflectionProbeStatic | StaticEditorFlags.ContributeGI;
            foreach (var r in t.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, flags);
        }

        // ================================================================================================= lighting
        private static void BuildLighting()
        {
            // Sun
            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(_lighting, false);
            sunGo.transform.rotation = Quaternion.Euler(_cfg.sunElevation, _cfg.sunAzimuth, 0f);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.lightUnit = LightUnit.Lux;
            sun.intensity = _cfg.sunLux;
            sun.useColorTemperature = true;
            sun.colorTemperature = _cfg.sunTemperature;
            sun.color = Color.white;
            sun.shadows = LightShadows.Soft;
            var hdSun = sunGo.AddComponent<HDAdditionalLightData>();
            hdSun.angularDiameter = _cfg.sunAngularDiameter;
            hdSun.SetShadowResolutionOverride(false);
            RenderSettings.sun = sun;

            // Global volume (photoreal grade + HDRI skybox + atmosphere + reflection probe)
            var profile = NewProfile(LightDir + "/LR2_Saltworks_Profile.asset");
            var ve = profile.Add<VisualEnvironment>(true);
            ve.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            ApplyHdriSky(ve, profile, "Assets/LeartesStudios/Art/Textures/T_HDRI_01.HDR", exposureEv: 11.6f, rotationDeg: 38f);
            // Global indirect/sky visibility floor so dark shadows & sheltered walls keep clear enemy silhouettes
            var ilcGlobal = profile.Add<IndirectLightingController>(true);
            ilcGlobal.indirectDiffuseLightingMultiplier.Override(2.25f);
            ilcGlobal.reflectionLightingMultiplier.Override(1.55f);
            var fog = profile.Add<Fog>(true);
            fog.enabled.Override(true);
            fog.meanFreePath.Override(Mathf.Max(_cfg.fogMeanFreePath, 68f));
            fog.baseHeight.Override(_cfg.fogBaseHeight);
            fog.maximumHeight.Override(9.5f);
            fog.enableVolumetricFog.Override(true);
            fog.albedo.Override(_cfg.fogAlbedo);
            fog.anisotropy.Override(_cfg.fogAnisotropy);
            fog.depthExtent.Override(_cfg.fogDepthExtent);
            fog.maxFogDistance.Override(3000f);
            var exposure = profile.Add<Exposure>(true);
            exposure.mode.Override(ExposureMode.AutomaticHistogram);
            exposure.compensation.Override(Mathf.Clamp(_cfg.exposureCompensation, 0.30f, 0.48f));
            exposure.limitMin.Override(Mathf.Min(_cfg.exposureLimitMin, 2.0f));
            exposure.limitMax.Override(Mathf.Max(_cfg.exposureLimitMax, 14.5f));
            exposure.adaptationMode.Override(AdaptationMode.Progressive);
            exposure.adaptationSpeedDarkToLight.Override(2.8f);
            exposure.adaptationSpeedLightToDark.Override(1.8f);
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.ACES);
            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(Mathf.Min(_cfg.bloom, 0.06f));
            bloom.threshold.Override(2.8f);
            bloom.scatter.Override(0.35f);
            bloom.dirtIntensity.Override(0f);
            bloom.dirtTexture.Override(null);
            var color = profile.Add<ColorAdjustments>(true);
            color.contrast.Override(_cfg.contrast);
            color.saturation.Override(_cfg.saturation);
            var wb = profile.Add<WhiteBalance>(true);
            wb.temperature.Override(_cfg.temperature);
            var smh = profile.Add<ShadowsMidtonesHighlights>(true);
            smh.shadows.Override(new Vector4(0.96f, 0.99f, 1.03f, 0.015f));
            smh.highlights.Override(new Vector4(1.04f, 1.00f, 0.94f, 0.02f));
            var vig = profile.Add<Vignette>(true);
            vig.intensity.Override(_cfg.vignette);
            vig.smoothness.Override(0.45f);
            var ao = profile.Add<ScreenSpaceAmbientOcclusion>(true);
            ao.intensity.Override(0.78f);
            ao.radius.Override(1.45f);
            ao.directLightingStrength.Override(0.24f);
            var ssr = profile.Add<ScreenSpaceReflection>(true);
            ssr.enabled.Override(true);
            var gi = profile.Add<GlobalIllumination>(true);
            gi.enable.Override(false); // controllable via LR2RayTracingController DXR toggle at runtime
            var contact = profile.Add<ContactShadows>(true);
            contact.enable.Override(true);
            contact.length.Override(0.34f);
            contact.opacity.Override(0.90f);
            var micro = profile.Add<MicroShadowing>(true);
            micro.enable.Override(true);
            var shadows = profile.Add<HDShadowSettings>(true);
            shadows.maxShadowDistance.Override(95f);
            shadows.cascadeShadowSplitCount.Override(4);
            DisableMotionBlur(profile);
            ApplyCinematicPost(profile, 1f);
            SaveProfile(profile);

            var vol = new GameObject("GlobalVolume");
            vol.transform.SetParent(_lighting, false);
            var v = vol.AddComponent<Volume>();
            v.isGlobal = true;
            v.priority = 0f;
            v.sharedProfile = profile;
            AddArenaReflectionProbe(_lighting, new Vector3(0f, 3.5f, 0f), new Vector3(85f, 30f, 85f));

            // Bodycam lens volume (player-toggleable)
            var bodycam = NewProfile(LightDir + "/LR2_Bodycam_Profile.asset");
            var ld = bodycam.Add<LensDistortion>(true);
            ld.intensity.Override(_cfg.lensDistortion);
            ld.scale.Override(1.06f);
            var ca = bodycam.Add<ChromaticAberration>(true);
            ca.intensity.Override(_cfg.chromaticAberration);
            var grain = bodycam.Add<FilmGrain>(true);
            grain.type.Override(FilmGrainLookup.Medium3);
            grain.intensity.Override(_cfg.filmGrain);
            grain.response.Override(0.75f);
            DisableMotionBlur(bodycam);
            var bvig = bodycam.Add<Vignette>(true);
            bvig.intensity.Override(Mathf.Clamp01(_cfg.vignette + 0.12f));
            bvig.smoothness.Override(0.42f);
            SaveProfile(bodycam);
            var bvGo = new GameObject("BodycamLensVolume");
            bvGo.transform.SetParent(_lighting, false);
            var bv = bvGo.AddComponent<Volume>();
            bv.isGlobal = true;
            bv.priority = 10f;
            bv.sharedProfile = bodycam;
            bvGo.AddComponent<BodycamPostFX>();

            // Warm indoor lights + doorway bounce fills (keeps sheltered interiors & dark pockets readable without blowing out mood)
            AddLight(_lighting, "EmergencyHouse", new Vector3(0f, 2.9f, 2f), Quaternion.identity, LightType.Point, 3600f, 3400f, 14f, 0f, false);
            AddLight(_lighting, "EmergencyWorkshopDown", new Vector3(-21f, 2.9f, -2f), Quaternion.identity, LightType.Point, 3600f, 3600f, 15f, 0f, false);
            AddLight(_lighting, "EmergencyWorkshopUp", new Vector3(-21f, 6.2f, -2f), Quaternion.identity, LightType.Point, 3600f, 2800f, 13f, 0f, false);
            AddLight(_lighting, "EmergencyWarehouseW", new Vector3(-7f, 5.6f, -17f), Quaternion.identity, LightType.Point, 3800f, 5200f, 20f, 0f, false);
            AddLight(_lighting, "EmergencyWarehouseCenter", new Vector3(0f, 5.6f, -17f), Quaternion.identity, LightType.Point, 4200f, 6400f, 22f, 0f, false);
            AddLight(_lighting, "EmergencyWarehouseE", new Vector3(7f, 5.6f, -17f), Quaternion.identity, LightType.Point, 3800f, 5200f, 20f, 0f, false);
            AddLight(_lighting, "HouseBounceW", new Vector3(-4.9f, 2.8f, 2f), Quaternion.identity, LightType.Point, 5000f, 2400f, 13f, 0f, false);
            AddLight(_lighting, "HouseBounceE", new Vector3(4.9f, 2.8f, 2f), Quaternion.identity, LightType.Point, 5000f, 2400f, 13f, 0f, false);
            AddLight(_lighting, "WorkshopDoorBounce", new Vector3(-16.5f, 3.0f, -2f), Quaternion.identity, LightType.Point, 5200f, 2600f, 14f, 0f, false);
            AddLight(_lighting, "WarehouseBayW", new Vector3(-7f, 3.8f, -11.2f), Quaternion.identity, LightType.Point, 5200f, 2800f, 15f, 0f, false);
            AddLight(_lighting, "WarehouseBayE", new Vector3(7f, 3.8f, -11.2f), Quaternion.identity, LightType.Point, 5200f, 2800f, 15f, 0f, false);
            AddLight(_lighting, "LoadingDockBounce", new Vector3(0f, 4.0f, -5.5f), Quaternion.identity, LightType.Point, 5400f, 2600f, 15f, 0f, false);
            AddLight(_lighting, "ContainerYardFillNE", new Vector3(16.5f, 4.4f, 6.5f), Quaternion.identity, LightType.Point, 5400f, 2800f, 16f, 0f, false);
            AddLight(_lighting, "ContainerYardFillNW", new Vector3(-15f, 4.4f, 12f), Quaternion.identity, LightType.Point, 5400f, 2600f, 16f, 0f, false);
            AddLight(_lighting, "SaltworksPlazaFillN", new Vector3(0f, 4.5f, 12f), Quaternion.identity, LightType.Point, 5000f, 3800f, 20f, 0f, false);
            AddLight(_lighting, "SaltworksPlazaFillW", new Vector3(-12.5f, 4.5f, 6f), Quaternion.identity, LightType.Point, 4800f, 3600f, 20f, 0f, false);
            AddLight(_lighting, "SaltworksPlazaFillE", new Vector3(12.5f, 4.5f, 0f), Quaternion.identity, LightType.Point, 4800f, 3600f, 20f, 0f, false);
            // B92: shadowless practical accent lights at key dark corners & doorways
            AddLight(_lighting, "Accent_QuayCraneW", new Vector3(-20.5f, 3.8f, 16.5f), Quaternion.identity, LightType.Point, 5600f, 3400f, 16f, 0f, false);
            AddLight(_lighting, "Accent_ControlStair", new Vector3(4.5f, 4.2f, -3.5f), Quaternion.identity, LightType.Point, 4800f, 3200f, 14f, 0f, false);
            AddLight(_lighting, "Accent_WarehouseSouthDoor", new Vector3(0f, 3.6f, -21.8f), Quaternion.identity, LightType.Point, 4200f, 3400f, 16f, 0f, false);
            AddLight(_lighting, "Accent_ContainerAlleyN", new Vector3(19.5f, 3.8f, 11.5f), Quaternion.identity, LightType.Point, 5400f, 3200f, 15f, 0f, false);
            AddLight(_lighting, "Accent_WestWorkshopYard", new Vector3(-13.5f, 3.6f, 2.5f), Quaternion.identity, LightType.Point, 3800f, 3200f, 16f, 0f, false);
            AddLight(_lighting, "Accent_EastContainerAlley", new Vector3(12.5f, 3.6f, 0.5f), Quaternion.identity, LightType.Point, 4000f, 3200f, 16f, 0f, false);

            foreach (var bp in new[] { new Vector3(-11.5f, 0f, 7.5f), new Vector3(11.5f, 0f, -4.5f), new Vector3(-4.5f, 0f, 17f), new Vector3(7.5f, 0f, 10.5f) })
                SpawnBurningBarrel(_lighting, _props, G(bp));
            PlaceEnvironmentRealism(new Color(0.76f, 0.82f, 0.86f, 1f), new Color(0.54f, 0.50f, 0.44f, 1f), 0.14f, 22f);

            if (!_cfg.practicalLights) return; // abandoned for a century: no power anywhere
            // Practical lights: sodium lamp poles + warehouse/house interiors
            Vector3[] poles = { new Vector3(-12f, 0f, 2f), new Vector3(10f, 0f, -6f), new Vector3(-3f, 0f, 14f), new Vector3(16f, 0f, 12f), new Vector3(-20f, 0f, -14f), new Vector3(24f, 0f, -16f) };
            for (int i = 0; i < poles.Length; i++)
            {
                var pole = Child(_lighting, "LampPole" + i);
                pole.position = G(poles[i]);
                Box("Pole", pole, new Vector3(0f, 4f, 0f), new Vector3(0.18f, 8f, 0.18f), _m.darkMetal, LR2Layers.Default, SurfaceType.Metal);
                Box("Arm", pole, new Vector3(0.6f, 7.9f, 0f), new Vector3(1.3f, 0.12f, 0.25f), _m.darkMetal, LR2Layers.Default, SurfaceType.Metal);
                Prop("SM_Lantern_01", pole, new Vector3(1.2f, 7.62f, 0f), 0f, 1.25f, false);
                var sod = AddLight(pole, "Sodium", new Vector3(1.2f, 7.75f, 0f), Quaternion.Euler(90f, 0f, 0f), LightType.Spot, 2100f, 9000f, 22f, 115f, i < 2);
                if (i < 2 && sod.TryGetComponent<HDAdditionalLightData>(out var hdSod))
                {
                    hdSod.SetShadowResolutionOverride(true);
                    hdSod.SetShadowResolution(512);
                }
            }
            Vector3[] whLights = { new Vector3(-9f, 7.0f, -17f), new Vector3(-3f, 7.0f, -17f), new Vector3(3f, 7.0f, -17f), new Vector3(9f, 7.0f, -17f) };
            for (int i = 0; i < whLights.Length; i++)
            {
                var l = AddLight(_lighting, "WarehouseLamp" + i, whLights[i], Quaternion.Euler(90f, 0f, 0f), LightType.Spot, 4200f, 7000f, 14f, 120f, false);
                if (i == 2) l.gameObject.AddComponent<LightFlicker>();
            }
            AddLight(_lighting, "HouseLamp", new Vector3(0f, 3.2f, 2f), Quaternion.Euler(90f, 0f, 0f), LightType.Spot, 3600f, 2600f, 8f, 120f, false);
            AddLight(_lighting, "WorkshopLamp", new Vector3(-21f, 3.2f, -2f), Quaternion.Euler(90f, 0f, 0f), LightType.Spot, 3400f, 3000f, 9f, 120f, false);
            AddLight(_lighting, "WorkshopUpLamp", new Vector3(-21f, 6.7f, -2f), Quaternion.Euler(90f, 0f, 0f), LightType.Spot, 3400f, 2200f, 8f, 120f, false);
        }

        private static Light AddLight(Transform parent, string name, Vector3 pos, Quaternion rot, LightType type, float kelvin, float lumen, float range, float angle, bool shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            var l = go.AddComponent<Light>();
            l.type = type;
            l.useColorTemperature = true;
            l.colorTemperature = kelvin;
            l.lightUnit = LightUnit.Lumen;
            l.intensity = lumen;
            l.range = range;
            if (type == LightType.Spot)
            {
                l.spotAngle = angle;
                l.innerSpotAngle = angle * 0.55f;
            }
            l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            var hd = go.AddComponent<HDAdditionalLightData>();
            // Cull distant point/spot shading & shadow evaluation outside local combat pockets across all 4 maps (P3).
            hd.fadeDistance = Mathf.Max(range * 1.85f, range + 8f);
            if (shadows) hd.shadowFadeDistance = Mathf.Max(range * 1.35f, range + 4f);
            return l;
        }

        private static VolumeProfile NewProfile(string path)
        {
            EnsureFolder(Path.GetDirectoryName(path));
            var existing = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);
            return profile;
        }

        private static void DisableMotionBlur(VolumeProfile profile)
        {
            var mb = profile.Add<MotionBlur>(true);
            mb.intensity.Override(0f);
            mb.maximumVelocity.Override(0f);
            mb.cameraMotionBlur.Override(false);
            if (!profile.name.Contains("Menu"))
            {
                var dof = profile.TryGet<DepthOfField>(out var existingDof) ? existingDof : profile.Add<DepthOfField>(true);
                dof.focusMode.Override(DepthOfFieldMode.Off);
            }
        }

        private static void SaveProfile(VolumeProfile profile)
        {
            if (profile == null) return;
            foreach (var comp in profile.components)
            {
                if (comp == null) continue;
                comp.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
                if (!AssetDatabase.Contains(comp)) AssetDatabase.AddObjectToAsset(comp, profile);
                EditorUtility.SetDirty(comp);
            }
            EditorUtility.SetDirty(profile);
        }

        private static void AddSpawn(Vector3 pos, float yaw, TeamId team, bool drones = true, bool snapToGround = true)
        {
            var go = new GameObject($"Spawn_{team}");
            go.transform.SetParent(_spawns, false);
            go.transform.localPosition = snapToGround ? Stand(pos) : pos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            var sp = go.AddComponent<SpawnPoint>();
            sp.team = team;
            sp.allowFreeForAll = true;
            sp.allowDrones = drones;
        }

        private static void AddTac(Vector3 pos, TacticalPointType type, float weight, bool snapLowToGround = true)
        {
            var go = new GameObject($"Tac_{type}");
            go.transform.SetParent(_tactical, false);
            go.transform.localPosition = snapLowToGround && pos.y < 0.1f ? Stand(pos) : pos;
            var tp = go.AddComponent<TacticalPoint>();
            tp.type = type;
            tp.weight = weight;
        }

        private static void AddAmmoCrate(Vector3 pos)
        {
            var crate = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/Props/LR2_AmmoCrate.prefab");
            if (crate == null) return;
            var c = (GameObject)PrefabUtility.InstantiatePrefab(crate, _gameplay);
            c.transform.position = pos;
        }

        private static void AddPlayerClipBox(Vector2 playMin, Vector2 playMax, float height = 12f)
        {
            var clip = Child(_structures, "PlayerClip");
            void Clip(Vector3 c, Vector3 s, bool noNav = false)
            {
                var go = new GameObject("Clip");
                go.transform.SetParent(clip, false);
                go.transform.localPosition = c;
                go.layer = LR2Layers.PlayerClip;
                go.AddComponent<BoxCollider>().size = s;
                if (noNav) go.AddComponent<NavMeshModifier>().ignoreFromBuild = true;
            }
            float w = playMax.x - playMin.x;
            float d = playMax.y - playMin.y;
            float cx = (playMin.x + playMax.x) * 0.5f;
            float cz = (playMin.y + playMax.y) * 0.5f;
            Clip(new Vector3(playMin.x - 0.3f, height * 0.5f, cz), new Vector3(0.4f, height, d + 2f));
            Clip(new Vector3(playMax.x + 0.3f, height * 0.5f, cz), new Vector3(0.4f, height, d + 2f));
            Clip(new Vector3(cx, height * 0.5f, playMin.y - 0.3f), new Vector3(w + 2f, height, 0.4f));
            Clip(new Vector3(cx, height * 0.5f, playMax.y + 0.3f), new Vector3(w + 2f, height, 0.4f));
            Clip(new Vector3(cx, height + 0.2f, cz), new Vector3(w + 2f, 0.4f, d + 2f), true);
        }

        private static void AddMatchSystems(float reverbSize = 35f)
        {
            var mmGo = new GameObject("MatchManager");
            mmGo.transform.SetParent(_systems, false);
            var mm = mmGo.AddComponent<MatchManager>();
            SetField(mm, "defaultSettings", AssetDatabase.LoadAssetAtPath<MatchSettings>("Assets/LR2/Data/Game/MatchSettings_Duel.asset"));
            SetField(mm, "playerPrefab", AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/Characters/LR2_Soldier_Player.prefab"));
            SetField(mm, "botPrefab", AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/Characters/LR2_Soldier_Bot.prefab"));
            SetField(mm, "orbPrefab", AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/Enemies/LR2_OrbDrone.prefab"));
            var so = new SerializedObject(mm);
            var diffs = so.FindProperty("difficulties");
            string[] names = { "Recruit", "Regular", "Hardened", "Veteran" };
            diffs.arraySize = names.Length;
            for (int i = 0; i < names.Length; i++)
                diffs.GetArrayElementAtIndex(i).objectReferenceValue = AssetDatabase.LoadAssetAtPath<BotDifficulty>($"Assets/LR2/Data/AI/BotDifficulty_{names[i]}.asset");
            so.ApplyModifiedPropertiesWithoutUndo();

            var ui = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/UI/LR2_GameUI.prefab");
            if (ui != null) PrefabUtility.InstantiatePrefab(ui, _systems);
            LR2UIBuilder.EnsureEventSystem();

            var audio = new GameObject("AudioEnvironment");
            audio.transform.SetParent(_systems, false);
            var env = audio.AddComponent<LR2AudioEnvironment>();
            var common = AssetDatabase.LoadAssetAtPath<LR2CommonSounds>("Assets/LR2/Resources/LR2/LR2CommonSounds.asset");
            if (common != null) env.ambienceLoops = common.ambienceLoops;
            Reverb(_systems, "Reverb_Arena", new Vector3(0f, 2f, 0f), reverbSize * 0.7f, reverbSize * 1.2f, AudioReverbPreset.City);

            var kz = new GameObject("KillZone");
            kz.transform.SetParent(_systems, false);
            kz.AddComponent<KillZone>();
        }

        // ================================================================================================= gameplay
        private static void BuildGameplay()
        {
            // Spawns
            void Spawn(Vector3 p, float yaw, TeamId team, bool drones = true) => AddSpawn(p, yaw, team, drones, true);
            Spawn(new Vector3(-22f, 0.05f, -5f), 90f, TeamId.Alpha);
            Spawn(new Vector3(-19f, 0.05f, 1.5f), 90f, TeamId.Alpha);
            Spawn(new Vector3(-24f, 0.05f, 2.5f), 120f, TeamId.Alpha);
            Spawn(new Vector3(-18.5f, 0.05f, -6.5f), 70f, TeamId.Alpha);
            Spawn(new Vector3(-24f, 0.05f, -12f), 45f, TeamId.Alpha);
            Spawn(new Vector3(-23.5f, 0.05f, 9f), 120f, TeamId.Alpha);
            Spawn(new Vector3(24f, 0.05f, -11f), -60f, TeamId.Bravo);
            Spawn(new Vector3(23f, 0.05f, 9.5f), -120f, TeamId.Bravo);
            Spawn(new Vector3(25f, 0.05f, -3f), -90f, TeamId.Bravo);
            Spawn(new Vector3(16.0f, 0.05f, -17.5f), -45f, TeamId.Bravo);
            Spawn(new Vector3(23.5f, 0.05f, 4f), -90f, TeamId.Bravo);
            Spawn(new Vector3(17f, 0.05f, 15f), -135f, TeamId.Bravo);
            Spawn(new Vector3(0f, 0.05f, 19.5f), 180f, TeamId.None);
            Spawn(new Vector3(-8.5f, 0.05f, -16.5f), 0f, TeamId.None);
            Spawn(new Vector3(8.5f, 0.05f, -16.5f), 0f, TeamId.None);
            Spawn(new Vector3(0f, 0.05f, -6f), 0f, TeamId.None, false);

            // Tactical points
            void Tac(Vector3 p, TacticalPointType type, float weight) => AddTac(p, type, weight, true);
            Tac(new Vector3(0.5f, 3.65f, 2.5f), TacticalPointType.Overlook, 0.9f);       // control house roof
            Tac(new Vector3(-17.6f, 3.65f, -2f), TacticalPointType.Overlook, 0.8f);      // workshop window
            Tac(new Vector3(20f, 5.2f, -4.8f), TacticalPointType.Overlook, 0.75f);       // container stack A
            Tac(new Vector3(20f, 5.2f, 5.2f), TacticalPointType.Overlook, 0.6f);         // container stack B
            Tac(new Vector3(-1f, 5.2f, 17f), TacticalPointType.Sniper, 0.7f);            // quay bridge
            Tac(new Vector3(0f, 0.05f, -17f), TacticalPointType.Patrol, 0.6f);           // warehouse interior
            Tac(new Vector3(-9f, 0.05f, -17.5f), TacticalPointType.Patrol, 0.5f);
            Tac(new Vector3(9f, 0.05f, -17.5f), TacticalPointType.Patrol, 0.5f);
            Tac(new Vector3(0f, 1.25f, -10.8f), TacticalPointType.Patrol, 0.55f);         // loading dock
            Tac(new Vector3(0f, 0.05f, 10f), TacticalPointType.Patrol, 0.5f);             // north yard
            Tac(new Vector3(-2f, 0.05f, 19.5f), TacticalPointType.Patrol, 0.5f);          // quay lane
            Tac(new Vector3(12f, 0.05f, 0f), TacticalPointType.Patrol, 0.5f);
            Tac(new Vector3(-12f, 0.05f, 4f), TacticalPointType.Patrol, 0.5f);
            Tac(new Vector3(-4.9f, 0.05f, 2f), TacticalPointType.Choke, 0.6f);            // house doors
            Tac(new Vector3(4.9f, 0.05f, 2f), TacticalPointType.Choke, 0.6f);
            Tac(new Vector3(-7f, 0.05f, -11.6f), TacticalPointType.Choke, 0.5f);          // warehouse openings
            Tac(new Vector3(5f, 0.05f, -11.6f), TacticalPointType.Choke, 0.5f);

            // Props from the Leartes kit (cover + set dressing)
            DressYard();

            // Two mirrored strategic mid-map ammo stations on contested routes (B39)
            var crate = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/Props/LR2_AmmoCrate.prefab");
            if (crate != null)
            {
                foreach (var p in new[] { new Vector3(-9.5f, 0f, -1.5f), new Vector3(9.5f, 0f, 3.5f) })
                {
                    var c = (GameObject)PrefabUtility.InstantiatePrefab(crate, _gameplay);
                    c.transform.position = G(p);
                }
            }

            // Capture The Flag DM strategic zones (NavMesh plaza anchors) + harbour windblown mist & steam particles
            AddFlagAnchor(_gameplay, "Harbour Centre Plaza", G(new Vector3(0f, 0f, 8f)));
            AddFlagAnchor(_gameplay, "Warehouse Bay Floor", new Vector3(0f, 0.05f, -17f));
            AddFlagAnchor(_gameplay, "West Workshop Yard", G(new Vector3(-12.5f, 0f, 2f)));
            AddFlagAnchor(_gameplay, "East Container Alley", G(new Vector3(12.5f, 0f, 0f)));
            AddAmbientParticles(_lighting, "SaltworksSeaMist", true, new Vector3(0f, 3.2f, 4f), new Vector3(44f, 5.5f, 38f),
                new Color(0.76f, 0.82f, 0.88f, 0.16f), new Color(0.62f, 0.68f, 0.74f, 0.08f), 85, 14f, 3.5f, 6.0f, 0.65f, 1.35f, new Vector3(1.1f, 0.08f, 0.35f), 0f);
            AddAmbientParticles(_lighting, "WorkshopSparksMist", false, new Vector3(-17f, 1.6f, -2f), new Vector3(8f, 3f, 8f),
                new Color(1f, 0.7f, 0.35f, 0.72f), new Color(1f, 0.45f, 0.18f, 0.35f), 32, 6f, 1.8f, 3.2f, 0.025f, 0.055f, new Vector3(0.4f, 0.55f, -0.2f), -0.05f);

            // Systems
            var mmGo = new GameObject("MatchManager");
            mmGo.transform.SetParent(_systems, false);
            var mm = mmGo.AddComponent<MatchManager>();
            SetField(mm, "defaultSettings", AssetDatabase.LoadAssetAtPath<MatchSettings>("Assets/LR2/Data/Game/MatchSettings_Duel.asset"));
            SetField(mm, "playerPrefab", AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/Characters/LR2_Soldier_Player.prefab"));
            SetField(mm, "botPrefab", AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/Characters/LR2_Soldier_Bot.prefab"));
            SetField(mm, "orbPrefab", AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/Enemies/LR2_OrbDrone.prefab"));
            var so = new SerializedObject(mm);
            var diffs = so.FindProperty("difficulties");
            string[] names = { "Recruit", "Regular", "Hardened", "Veteran" };
            diffs.arraySize = names.Length;
            for (int i = 0; i < names.Length; i++)
                diffs.GetArrayElementAtIndex(i).objectReferenceValue = AssetDatabase.LoadAssetAtPath<BotDifficulty>($"Assets/LR2/Data/AI/BotDifficulty_{names[i]}.asset");
            so.ApplyModifiedPropertiesWithoutUndo();

            var ui = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/UI/LR2_GameUI.prefab");
            if (ui != null) PrefabUtility.InstantiatePrefab(ui, _systems);
            LR2UIBuilder.EnsureEventSystem();

            var audio = new GameObject("AudioEnvironment");
            audio.transform.SetParent(_systems, false);
            var env = audio.AddComponent<LR2AudioEnvironment>();
            var common = AssetDatabase.LoadAssetAtPath<LR2CommonSounds>("Assets/LR2/Resources/LR2/LR2CommonSounds.asset");
            if (common != null) env.ambienceLoops = common.ambienceLoops;
            Reverb(_systems, "Reverb_Warehouse", new Vector3(0f, 3f, -17f), 9f, 15f, AudioReverbPreset.Hangar);
            Reverb(_systems, "Reverb_House", new Vector3(0f, 1.7f, 2f), 3.5f, 5.5f, AudioReverbPreset.Room);
            Reverb(_systems, "Reverb_Workshop", new Vector3(-21f, 3f, -2f), 5f, 8f, AudioReverbPreset.Livingroom);
            Reverb(_systems, "Reverb_Yard", new Vector3(0f, 2f, 0f), 30f, 45f, AudioReverbPreset.City);

            var kz = new GameObject("KillZone");
            kz.transform.SetParent(_systems, false);
            kz.AddComponent<KillZone>();
        }

        private static void Reverb(Transform parent, string name, Vector3 pos, float min, float max, AudioReverbPreset preset)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var rz = go.AddComponent<AudioReverbZone>();
            rz.minDistance = min;
            rz.maxDistance = max;
            rz.reverbPreset = preset;
        }

        private static void DressYard()
        {
            int placed = 0;
            void P(string n, Vector3 p, float yaw = 0f, float s = 1f)
            {
                if (_rnd.NextDouble() > _cfg.propDensity + 0.1f) return;
                if (Prop(n, _props, p.y == 0f ? G(p) : p, yaw + R(-12f, 12f), s) != null) placed++;
            }
            // Barrel clusters
            foreach (var c in new[] { new Vector3(-13f, 0f, -9f), new Vector3(12f, 0f, 9.5f), new Vector3(-6f, 0f, 21.5f), new Vector3(7f, 0f, -22.8f) + new Vector3(0f, 0f, 1.5f), new Vector3(23f, 0f, 13f) })
            {
                P("SM_Barrel_01", c, R(0, 360));
                P("SM_Barrel_02", c + new Vector3(0.8f, 0f, 0.3f), R(0, 360));
                P("SM_Barrel_04", c + new Vector3(0.2f, 0f, 0.95f), R(0, 360));
            }
            // Pallet stacks + crates
            foreach (var c in new[] { new Vector3(-10f, 0f, 10f), new Vector3(8f, 0f, -8.6f), new Vector3(-17f, 0f, -16f), new Vector3(11f, 0f, 16f), new Vector3(-2f, 1.2f, -10.8f) })
            {
                P("SM_Woodpallet_1", c, R(0, 90));
                P("SM_Crate_01", c + new Vector3(1.4f, 0f, 0.2f), R(0, 90));
                P("SM_Crate_02", c + new Vector3(-1.2f, 0f, -0.4f), R(0, 90));
            }
            // Jersey barriers (mantle-height cover)
            foreach (var (p, yaw) in new[] { (new Vector3(-6f, 0f, -2f), 90f), (new Vector3(6.5f, 0f, -1.5f), 90f), (new Vector3(-14f, 0f, 14f), 0f), (new Vector3(2f, 0f, 12f), 10f), (new Vector3(-1f, 0f, -24f + 1.5f), 0f), (new Vector3(14f, 0f, -9f), 80f) })
                P("SM_Jersey", p, yaw);
            // Industrial clutter
            P("SM_Cablespool", new Vector3(-4f, 0f, 8.5f), 0f);
            P("SM_Cablespool", new Vector3(17.5f, 0f, -8.5f), 40f);
            P("SM_Gascylender", new Vector3(-15.2f, 0f, -5.8f), 0f);
            P("SM_Tire", new Vector3(13f, 0f, 3f), 0f);
            P("SM_Tire", new Vector3(13.4f, 0f, 3.9f), 30f);
            P("SM_Trashcan", new Vector3(-5.2f, 0f, -1.2f), 0f);
            P("SM_Firehydrant", new Vector3(5.2f, 0f, 6.2f), 0f);
            P("SM_Conebarrier", new Vector3(3f, 0f, -9f), 0f);
            P("SM_Conebarrier", new Vector3(-8f, 0f, -9.3f), 0f);
            P("SM_fireextinguisher", new Vector3(-15.6f, 0f, 3.5f), 0f);
            P("SM_BigPipe", new Vector3(-24f, 0f, 15f), 0f);
            P("SM_PipeMadule1", new Vector3(26f, 0f, 16f), 90f);
            P("SM_Anchor", new Vector3(10f, 0f, 22f), 0f);
            P("SM_Plasticbarrel", new Vector3(-12.4f, 0f, -9.6f), 0f);
            Debug.Log($"[LR2] Props placed from Leartes kit: {placed}");
        }

        // ================================================================================================= mathematical anti-clipping
        private static void EliminateSceneClipping(string mapName, Transform structures, Transform props, Transform nature, Transform spawns, Transform gameplay)
        {
            Physics.SyncTransforms();

            // Collect immovable static obstacles (walls, containers, racks, crates, stairs, silos) — ignore terrain & flat ground/floor slabs.
            var staticObs = new List<(Collider col, Bounds bounds)>();
            if (structures != null)
            {
                foreach (var c in structures.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null || c.isTrigger || c is TerrainCollider) continue;
                    var b = c.bounds;
                    if (b.size.y < 0.35f && (b.size.x > 6f || b.size.z > 6f)) continue; // floor / road / quay slab
                    staticObs.Add((c, b));
                }
            }

            // Collect keep-clear anchors (spawns, ammo crates, and tactical doorway chokes) so no prop/bush blocks movement.
            var keepClear = new List<(Vector3 pos, float pushRadius, float killRadius)>();
            if (spawns != null)
                foreach (var sp in spawns.GetComponentsInChildren<SpawnPoint>(true)) keepClear.Add((sp.transform.position, 1.7f, 1.35f));
            if (gameplay != null)
                foreach (var ac in gameplay.GetComponentsInChildren<AmmoCrate>(true)) keepClear.Add((ac.transform.position, 1.7f, 1.35f));
            var tactical = structures != null && structures.parent != null ? structures.parent.Find("Tactical") : null;
            if (tactical != null)
            {
                foreach (Transform t in tactical)
                    if (t.name.StartsWith("Choke")) keepClear.Add((t.position, 2.5f, 2.1f));
            }
            if (structures != null)
            {
                // Keep both ends of every staircase/ramp collider clear of props & foliage
                foreach (var c in structures.GetComponentsInChildren<BoxCollider>(true))
                {
                    if (c.name != "RampCollider") continue;
                    Vector3 fwd = c.transform.forward * (c.size.z * 0.45f);
                    keepClear.Add((c.transform.position - fwd, 1.8f, 1.4f));
                    keepClear.Add((c.transform.position + fwd, 1.8f, 1.4f));
                }
            }

            // Gather movable props & nature items (immediate children under each container group).
            var candidates = new List<Transform>();
            void GatherDirectChildren(Transform parent)
            {
                if (parent == null) return;
                for (int i = 0; i < parent.childCount; i++)
                {
                    var ch = parent.GetChild(i);
                    if (ch.GetComponent<ParticleSystem>() != null || ch.GetComponent<Light>() != null || ch.GetComponent<DecalProjector>() != null) continue;
                    candidates.Add(ch);
                }
            }
            GatherDirectChildren(props);
            if (nature != null)
            {
                GatherDirectChildren(nature.Find("Trees"));
                GatherDirectChildren(nature.Find("Undergrowth"));
                GatherDirectChildren(nature.Find("Overgrowth"));
            }

            // Static structure lookup so rays hit structures/terrain, not movable props.
            var propColliderSet = new HashSet<Collider>();
            foreach (var tr in candidates)
                foreach (var c in tr.GetComponentsInChildren<Collider>(true)) propColliderSet.Add(c);

            bool TryBounds(Transform tr, out Bounds bounds)
            {
                bounds = default;
                var rs = tr.GetComponentsInChildren<Renderer>(true);
                bool has = false;
                foreach (var r in rs)
                {
                    if (r is ParticleSystemRenderer) continue;
                    if (!has) { bounds = r.bounds; has = true; }
                    else bounds.Encapsulate(r.bounds);
                }
                return has;
            }

            bool TryFivePointFloor(Bounds b, HashSet<Collider> skipCols, float maxDist, out float targetFloorY)
            {
                targetFloorY = 0f;
                float probeY = b.min.y + Mathf.Min(1.2f, b.size.y * 0.6f + 0.35f);
                if (!RaycastStaticFloor(new Vector3(b.center.x, probeY, b.center.z), maxDist, out var cHit, skipCols))
                    return false;
                float minY = cHit.point.y;
                float insetX = Mathf.Min(0.12f, b.extents.x * 0.35f);
                float insetZ = Mathf.Min(0.12f, b.extents.z * 0.35f);
                float[] xs = { b.min.x + insetX, b.max.x - insetX };
                float[] zs = { b.min.z + insetZ, b.max.z - insetZ };
                foreach (float cx in xs)
                    foreach (float cz in zs)
                    {
                        if (RaycastStaticFloor(new Vector3(cx, probeY, cz), maxDist, out var kh, skipCols) && kh.point.y >= cHit.point.y - 0.45f)
                            minY = Mathf.Min(minY, kh.point.y);
                    }
                targetFloorY = minY;
                return true;
            }

            bool RaycastStaticFloor(Vector3 origin, float maxDist, out RaycastHit bestHit, HashSet<Collider> extraSkip = null)
            {
                bestHit = default;
                var hits = Physics.RaycastAll(origin, Vector3.down, maxDist, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
                float bestDist = float.MaxValue;
                bool found = false;
                for (int i = 0; i < hits.Length; i++)
                {
                    var h = hits[i];
                    if (propColliderSet.Contains(h.collider)) continue;
                    if (extraSkip != null && extraSkip.Contains(h.collider)) continue;
                    if (h.normal.y < 0.45f) continue;
                    if (h.distance < bestDist)
                    {
                        bestDist = h.distance;
                        bestHit = h;
                        found = true;
                    }
                }
                return found;
            }

            void SnapToSurface(Transform tr)
            {
                if (!TryBounds(tr, out var b)) return;
                if (TryFivePointFloor(b, null, 3.5f, out float floorY))
                    tr.position += Vector3.up * (floorY - b.min.y - 0.015f);
            }

            int adjusted = 0, removed = 0;

            // Pass 1: Snap props to static ground/floor and reject large props overhanging steep ledges.
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                var tr = candidates[i];
                if (tr == null) { candidates.RemoveAt(i); continue; }
                // Wall-mounted ivy / roof moss / webs should keep their surface alignment; trees are already planted with -0.28m terrain embed in BuildNature.
                bool isOvergrowth = nature != null && tr.parent != null && tr.parent.name == "Overgrowth";
                bool isTree = nature != null && tr.parent != null && tr.parent.name == "Trees";
                bool isFoliage = nature != null && tr.IsChildOf(nature);
                if (isOvergrowth || isTree) continue;

                SnapToSurface(tr);
                if (!TryBounds(tr, out var b)) continue;
                if (!isFoliage && Mathf.Max(b.size.x, b.size.z) > 1.1f)
                {
                    float minY = float.MaxValue, maxY = float.MinValue;
                    bool unsupported = false;
                    float[] cx = { b.min.x + 0.1f, b.max.x - 0.1f };
                    float[] cz = { b.min.z + 0.1f, b.max.z - 0.1f };
                    foreach (float x in cx)
                        foreach (float z in cz)
                        {
                            if (!RaycastStaticFloor(new Vector3(x, b.min.y + 0.6f, z), 1.6f, out var ch)) { unsupported = true; break; }
                            minY = Mathf.Min(minY, ch.point.y);
                            maxY = Mathf.Max(maxY, ch.point.y);
                        }
                    if (unsupported || maxY - minY > 0.55f)
                    {
                        Object.DestroyImmediate(tr.gameObject);
                        candidates.RemoveAt(i);
                        removed++;
                        continue;
                    }
                }
            }

            // Helper: compute horizontal Minimum Translation Vector to push box A out of box B.
            bool TryBoxMtv(Bounds a, Bounds b, float margin, out Vector3 mtv)
            {
                mtv = Vector3.zero;
                float ox = Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x) - margin;
                float oy = Mathf.Min(a.max.y, b.max.y) - Mathf.Max(a.min.y, b.min.y) - margin;
                float oz = Mathf.Min(a.max.z, b.max.z) - Mathf.Max(a.min.z, b.min.z) - margin;
                if (ox <= 0f || oy <= 0f || oz <= 0f) return false;
                if (ox <= oz) mtv = new Vector3(a.center.x >= b.center.x ? ox : -ox, 0f, 0f);
                else mtv = new Vector3(0f, 0f, a.center.z >= b.center.z ? oz : -oz);
                return true;
            }

            // Pass 2: 4-iteration mathematical MTV separation against spawns, static walls/containers, and peer props.
            for (int iter = 0; iter < 4; iter++)
            {
                Physics.SyncTransforms();
                for (int i = 0; i < candidates.Count; i++)
                {
                    var tr = candidates[i];
                    if (tr == null) continue;
                    bool isOvergrowth = nature != null && tr.parent != null && tr.parent.name == "Overgrowth";
                    bool isFoliage = nature != null && tr.IsChildOf(nature);
                    if (isOvergrowth || !TryBounds(tr, out var bi)) continue;

                    Vector3 totalPush = Vector3.zero;

                    // Keep spawns, ammo crates & doorway chokes unobstructed
                    for (int k = 0; k < keepClear.Count; k++)
                    {
                        var (kcPos, pushR, _) = keepClear[k];
                        if (Mathf.Abs(bi.min.y - kcPos.y) > 2.0f) continue;
                        Vector3 d = new Vector3(bi.center.x - kcPos.x, 0f, bi.center.z - kcPos.z);
                        float r = pushR + Mathf.Min(bi.extents.x, bi.extents.z);
                        float dist = d.magnitude;
                        if (dist < r)
                        {
                            Vector3 n = dist > 0.01f ? d / dist : Vector3.forward;
                            totalPush += n * (r - dist);
                        }
                    }

                    // Static structures (exact ComputePenetration when prop has a collider, else AABB MTV; keep in-play undergrowth clear of walls/props)
                    bool inPlayUndergrowth = isFoliage && tr.parent != null && tr.parent.name == "Undergrowth" && InPlay(bi.center, 3f);
                    if (!isFoliage || inPlayUndergrowth)
                    {
                        var itemCol = tr.GetComponentInChildren<Collider>(true);
                        for (int s = 0; s < staticObs.Count; s++)
                        {
                            var (sc, sb) = staticObs[s];
                            if (sc == null || !TryBoxMtv(bi, sb, 0.05f, out var aabbMtv)) continue;
                            if (!isFoliage && itemCol != null && !itemCol.isTrigger)
                            {
                                if (Physics.ComputePenetration(itemCol, itemCol.transform.position, itemCol.transform.rotation,
                                        sc, sc.transform.position, sc.transform.rotation, out var dir, out float depth) && depth > 0.03f)
                                {
                                    dir.y = 0f;
                                    if (dir.sqrMagnitude > 0.001f) totalPush += dir.normalized * (depth + 0.04f);
                                    else totalPush += aabbMtv;
                                }
                            }
                            else
                            {
                                totalPush += aabbMtv;
                            }
                        }

                        if (!isFoliage)
                        {
                            // Peer props (skip non-colliding grass; separate colliding props by MTV)
                            for (int j = 0; j < candidates.Count; j++)
                            {
                                if (i == j || candidates[j] == null) continue;
                                if (nature != null && candidates[j].IsChildOf(nature) && candidates[j].GetComponentInChildren<Collider>(true) == null) continue;
                                if (!TryBounds(candidates[j], out var bj)) continue;
                                if (TryBoxMtv(bi, bj, 0.06f, out var pmv))
                                {
                                    if (pmv.sqrMagnitude > 0.002f) totalPush += pmv * 0.55f;
                                }
                            }
                        }
                    }

                    if (totalPush.sqrMagnitude > 0.0004f)
                    {
                        totalPush.y = 0f;
                        if (totalPush.magnitude > 2.5f) totalPush = totalPush.normalized * 2.5f;
                        tr.position += totalPush;
                        SnapToSurface(tr);
                        adjusted++;
                    }
                }
            }

            // Pass 3: Remove any non-tree prop that still penetrates a wall/container or blocks a spawn/crate.
            Physics.SyncTransforms();
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                var tr = candidates[i];
                if (tr == null) continue;
                bool isOvergrowth = nature != null && tr.parent != null && tr.parent.name == "Overgrowth";
                if (isOvergrowth || !TryBounds(tr, out var bi)) continue;
                bool keepTree = nature != null && tr.parent != null && tr.parent.name == "Trees";

                bool bad = false;
                for (int k = 0; k < keepClear.Count && !bad; k++)
                {
                    var (kcPos, _, killR) = keepClear[k];
                    if (Mathf.Abs(bi.min.y - kcPos.y) > 2.0f) continue;
                    float dist = Vector2.Distance(new Vector2(bi.center.x, bi.center.z), new Vector2(kcPos.x, kcPos.z));
                    if (dist < killR) bad = true;
                }
                if (!bad && !keepTree)
                {
                    var itemCol = tr.GetComponentInChildren<Collider>(true);
                    if (itemCol != null && !itemCol.isTrigger)
                    {
                        for (int s = 0; s < staticObs.Count && !bad; s++)
                        {
                            var (sc, sb) = staticObs[s];
                            if (sc == null || !bi.Intersects(sb)) continue;
                            if (Physics.ComputePenetration(itemCol, itemCol.transform.position, itemCol.transform.rotation,
                                    sc, sc.transform.position, sc.transform.rotation, out _, out float depth) && depth > 0.09f)
                                bad = true;
                        }
                    }
                    else if (nature != null && tr.parent != null && tr.parent.name == "Undergrowth" && InPlay(bi.center, 3f))
                    {
                        for (int s = 0; s < staticObs.Count && !bad; s++)
                        {
                            var (sc, sb) = staticObs[s];
                            if (sc != null && TryBoxMtv(bi, sb, -0.05f, out _)) bad = true;
                        }
                    }
                }
                if (bad)
                {
                    Object.DestroyImmediate(tr.gameObject);
                    candidates.RemoveAt(i);
                    removed++;
                }
            }

            // B99 + B144: Final 5-point grounding pass after MTV nudges so no surviving prop/crate/pole/log floats above sloped terrain
            int grounded = 0;
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                var tr = candidates[i];
                if (tr == null) continue;
                bool isOvergrowth = nature != null && tr.parent != null && tr.parent.name == "Overgrowth";
                bool isTree = nature != null && tr.parent != null && tr.parent.name == "Trees";
                if (isOvergrowth || isTree || !TryBounds(tr, out var b)) continue;
                bool isPropGroup = props != null && tr.IsChildOf(props);
                if (TryFivePointFloor(b, null, 4.5f, out float gFloorY))
                {
                    float gap = b.min.y - gFloorY;
                    if (gap > 0.01f || gap < -0.22f)
                    {
                        tr.position += Vector3.up * (gFloorY - b.min.y - 0.015f);
                        grounded++;
                    }
                }
                else if (isPropGroup)
                {
                    Object.DestroyImmediate(tr.gameObject);
                    removed++;
                }
            }

            void GroundFreestanding(Transform tr, float embed)
            {
                if (tr == null || !TryBounds(tr, out var b)) return;
                var selfCols = new HashSet<Collider>(tr.GetComponentsInChildren<Collider>(true));
                if (TryFivePointFloor(b, selfCols, 5.0f, out float fy))
                {
                    float delta = fy - b.min.y - embed;
                    if (Mathf.Abs(delta) > 0.005f) { tr.position += Vector3.up * delta; grounded++; }
                }
            }

            if (gameplay != null)
                foreach (var ac in gameplay.GetComponentsInChildren<AmmoCrate>(true))
                    if (ac != null) GroundFreestanding(ac.transform, 0.015f);
            var lightingRoot = structures != null && structures.parent != null ? structures.parent.Find("Lighting") : null;
            if (lightingRoot != null)
            {
                for (int i = 0; i < lightingRoot.childCount; i++)
                    if (lightingRoot.GetChild(i).name.StartsWith("LampPole"))
                        GroundFreestanding(lightingRoot.GetChild(i), 0.05f);
            }
            if (structures != null)
            {
                string[] groundKeys = { "FallenTree", "BurntLog", "Desk", "Workbench", "Lockers", "SM_Chair_", "SM_Barrel_", "SM_Gascylender", "SM_fireextinguisher", "Crate", "Bollard", "SM_Docks_Rope" };
                foreach (Transform t in structures.GetComponentsInChildren<Transform>(true))
                {
                    if (t == structures || t.GetComponentInParent<Light>() != null) continue;
                    if (t.name.Contains("_LOD") || t.name == "Mesh" || (t.parent != null && (t.parent.name == "Mesh" || t.parent.GetComponent<LODGroup>() != null))) continue;
                    bool isGroundProp = false;
                    for (int k = 0; k < groundKeys.Length; k++)
                        if (t.name.IndexOf(groundKeys[k], System.StringComparison.OrdinalIgnoreCase) >= 0) { isGroundProp = true; break; }
                    if (isGroundProp)
                        GroundFreestanding(t, t.name.Contains("FallenTree") || t.name.Contains("BurntLog") ? 0.06f : 0.01f);
                }
                // Re-seat SM_Lantern_01 inside ControlHouse flush on top of Desk after Desk grounding.
                var ch = structures.Find("ControlHouse");
                if (ch != null)
                {
                    var chDesk = ch.Find("Desk");
                    Transform chLantern = null;
                    for (int i = 0; i < ch.childCount; i++)
                        if (ch.GetChild(i).name.Contains("SM_Lantern_01")) { chLantern = ch.GetChild(i); break; }
                    if (chDesk != null && chLantern != null && TryBounds(chDesk, out var db) && TryBounds(chLantern, out var lb))
                        chLantern.position += Vector3.up * (db.max.y - 0.005f - lb.min.y);
                }
            }

            // B85: Guarantee every SpawnPoint clears the soldier capsule after final prop placement
            if (spawns != null)
            {
                Physics.SyncTransforms();
                foreach (var sp in spawns.GetComponentsInChildren<SpawnPoint>(true))
                {
                    if (sp == null || SpawnPoint.IsClear(sp.transform.position)) continue;
                    Vector3 orig = sp.transform.position;
                    bool fixedSp = false;
                    for (float r = 0.5f; r <= 2.75f && !fixedSp; r += 0.5f)
                    {
                        for (int k = 0; k < 12; k++)
                        {
                            float ang = k * (360f / 12f) * Mathf.Deg2Rad;
                            Vector3 probe = orig + new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
                            if (RaycastStaticFloor(probe + Vector3.up * 1.5f, 3.0f, out var floorHit))
                                probe.y = floorHit.point.y + 0.05f;
                            if (SpawnPoint.IsClear(probe))
                            {
                                sp.transform.position = probe;
                                fixedSp = true;
                                break;
                            }
                        }
                    }
                }
            }

            Physics.SyncTransforms();
            Debug.Log($"[LR2] Anti-clip ({mapName}): adjusted={adjusted}, grounded={grounded}, removed={removed} overlapping/floating props.");
        }

        // ================================================================================================= destructible objects
        private static void SetupDestructibleProps(GameObject root)
        {
            var explosionFx = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/FX/FX_Explosion_Small.prefab");
            var candidates = new List<GameObject>();

            if (_props != null)
            {
                for (int i = 0; i < _props.childCount; i++)
                {
                    var ch = _props.GetChild(i).gameObject;
                    if (ch.GetComponent<Collider>() != null || ch.GetComponentInChildren<Collider>() != null)
                        candidates.Add(ch);
                }
            }

            if (_structures != null)
            {
                string[] breakableNames = { "Glass", "Desk", "Lockers", "Crate", "Goods", "Workbench", "ToolRack", "WindowCover", "ACUnit", "Vent", "ConcreteBlock", "Bollard", "Barricade", "Sandbag" };
                foreach (var t in _structures.GetComponentsInChildren<Transform>(true))
                {
                    if (t == _structures) continue;
                    bool match = false;
                    foreach (var bn in breakableNames)
                        if (t.name.IndexOf(bn, System.StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
                    if (match && (t.GetComponent<Collider>() != null || t.GetComponentInChildren<Collider>() != null))
                        candidates.Add(t.gameObject);
                }
            }

            int count = 0, explosives = 0;
            var seen = new HashSet<GameObject>();
            foreach (var go in candidates)
            {
                if (go == null || !seen.Add(go)) continue;
                if (go.GetComponent<SpawnPoint>() != null || go.GetComponent<AmmoCrate>() != null || go.GetComponentInParent<DestructibleProp>() != null) continue;

                // Ensure transform isn't frozen into a static batch mesh so hit jolt & shatter work cleanly.
                foreach (var tr in go.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.SetStaticEditorFlags(tr.gameObject, 0);

                var dp = go.GetComponent<DestructibleProp>();
                if (dp == null) dp = go.AddComponent<DestructibleProp>();
                string lower = go.name.ToLowerInvariant();
                if (lower.Contains("glass") || (lower.Contains("window") && !lower.Contains("cover")))
                {
                    dp.maxHealth = 25f;
                    dp.surface = SurfaceType.Glass;
                    var glassCracked = AssetDatabase.LoadAssetAtPath<Material>("Assets/LR2/Materials/Environment/M_LR2_Glass_Cracked.mat");
                    var glassDirty = AssetDatabase.LoadAssetAtPath<Material>("Assets/LR2/Materials/Environment/M_LR2_Glass_Dirty.mat");
                    var targetGlass = (count % 2 == 0 && glassCracked != null) ? glassCracked : (glassDirty != null ? glassDirty : _m?.glass);
                    if (targetGlass != null)
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                            r.sharedMaterial = targetGlass;
                    EnhanceGlassWindowFrame(go);
                }
                else if (lower.Contains("barrel_01") || lower.Contains("gascyl") || lower.Contains("fuel"))
                {
                    dp.maxHealth = 65f;
                    dp.surface = SurfaceType.Metal;
                    dp.explosive = true;
                    dp.blastRadius = 5.5f;
                    dp.blastDamage = 90f;
                    dp.explosionPrefab = explosionFx;
                    explosives++;
                }
                else if (lower.Contains("wood") || lower.Contains("crate") || lower.Contains("pallet") || lower.Contains("desk") ||
                         lower.Contains("workbench") || lower.Contains("table") || lower.Contains("chair") || lower.Contains("goods") || lower.Contains("log"))
                {
                    dp.maxHealth = 85f;
                    dp.surface = SurfaceType.Wood;
                }
                else if (lower.Contains("plastic") || lower.Contains("cone") || lower.Contains("tire") || lower.Contains("trash") || lower.Contains("extinguisher"))
                {
                    dp.maxHealth = 75f;
                    dp.surface = SurfaceType.Plastic;
                }
                else if (lower.Contains("barrel") || lower.Contains("locker") || lower.Contains("spool") || lower.Contains("hydrant") ||
                         lower.Contains("pipe") || lower.Contains("rack") || lower.Contains("acunit") || lower.Contains("vent") || lower.Contains("bollard") || lower.Contains("anchor"))
                {
                    dp.maxHealth = 120f;
                    dp.surface = SurfaceType.Metal;
                }
                else
                {
                    dp.maxHealth = 200f;
                    dp.surface = SurfaceType.Concrete;
                }

                if (go.GetComponent<SurfaceIdentifier>() == null)
                    go.AddComponent<SurfaceIdentifier>().surface = dp.surface;

                // Dynamic NavMesh carving on solid floor-level cover + intact glass panels (destruction opens paths in real time).
                var rs = go.GetComponentsInChildren<Renderer>(true);
                if (rs.Length > 0)
                {
                    Bounds localBounds = ComputeLocalBounds(go.transform, rs);
                    Vector3 worldSize = Vector3.Scale(localBounds.size, new Vector3(
                        Mathf.Abs(go.transform.lossyScale.x),
                        Mathf.Abs(go.transform.lossyScale.y),
                        Mathf.Abs(go.transform.lossyScale.z)));
                    bool glassPanel = dp.surface == SurfaceType.Glass && worldSize.y > 1.2f && Mathf.Max(worldSize.x, worldSize.z) > 1.2f;
                    bool solidCover = dp.surface != SurfaceType.Glass && worldSize.y > 0.35f && worldSize.magnitude < 8.0f;
                    if (glassPanel || solidCover)
                    {
                        var mod = go.GetComponent<NavMeshModifier>();
                        if (mod == null) mod = go.AddComponent<NavMeshModifier>();
                        mod.ignoreFromBuild = true;
                        var obs = go.GetComponent<NavMeshObstacle>();
                        if (obs == null) obs = go.AddComponent<NavMeshObstacle>();
                        obs.shape = NavMeshObstacleShape.Box;
                        obs.carving = true;
                        obs.carveOnlyStationary = true;
                        obs.center = localBounds.center;
                        obs.size = new Vector3(
                            Mathf.Max(0.2f, localBounds.size.x),
                            Mathf.Max(0.2f, localBounds.size.y),
                            Mathf.Max(0.2f, localBounds.size.z));
                    }
                }
                count++;
            }
            Debug.Log($"[LR2] Destructible objects configured: {count} ({explosives} explosive).");
        }

        private static Bounds ComputeLocalBounds(Transform root, Renderer[] rs)
        {
            Bounds local = default;
            bool has = false;
            Matrix4x4 inv = root.worldToLocalMatrix;
            foreach (var r in rs)
            {
                if (r is ParticleSystemRenderer) continue;
                var mf = r.GetComponent<MeshFilter>();
                Bounds src = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds : r.bounds;
                Matrix4x4 m = mf != null && mf.sharedMesh != null ? inv * r.transform.localToWorldMatrix : inv;
                Vector3 c = src.center, e = src.extents;
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 pt = m.MultiplyPoint3x4(c + new Vector3(e.x * sx, e.y * sy, e.z * sz));
                    if (!has) { local = new Bounds(pt, Vector3.zero); has = true; }
                    else local.Encapsulate(pt);
                }
            }
            return has ? local : new Bounds(Vector3.zero, Vector3.one);
        }

        // ================================================================================================= navigation / cover / minimap
        private static void ConfigureNavAgent()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset");
            if (assets == null || assets.Length == 0) return;
            var so = new SerializedObject(assets[0]);
            void F(string p, float v) { var prop = so.FindProperty(p); if (prop != null) prop.floatValue = v; }
            F("m_Settings.Array.data[0].agentRadius", 0.35f);
            F("m_Settings.Array.data[0].agentHeight", 1.8f);
            F("m_Settings.Array.data[0].agentClimb", 0.45f);
            F("m_Settings.Array.data[0].agentSlope", 46f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void BakeNavMesh(GameObject root, string navAssetPath = "Assets/LR2/Scenes/LR2_Saltworks_NavMesh.asset", bool saltworksDropLinks = true)
        {
            var surface = root.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = LR2Layers.ToMask(LR2Layers.Default, LR2Layers.Obstacle, LR2Layers.PlayerClip);
            surface.BuildNavMesh();
            if (surface.navMeshData != null)
            {
                if (AssetDatabase.LoadAssetAtPath<NavMeshData>(navAssetPath) != null) AssetDatabase.DeleteAsset(navAssetPath);
                AssetDatabase.CreateAsset(surface.navMeshData, navAssetPath);
                EditorUtility.SetDirty(surface);
            }

            if (saltworksDropLinks)
            {
                void Link(Vector3 a, Vector3 b)
                {
                    var go = new GameObject("DropLink");
                    go.transform.SetParent(_gameplay, false);
                    var l = go.AddComponent<NavMeshLink>();
                    l.startPoint = a;
                    l.endPoint = b;
                    l.bidirectional = false;
                    l.width = 1f;
                }
                Link(new Vector3(0f, 3.62f, 5.2f), new Vector3(0f, 0.05f, 6.8f));
                Link(new Vector3(4.2f, 3.62f, 2f), new Vector3(5.6f, 0.05f, 3.6f));
                Link(new Vector3(20.9f, 5.2f, 0.3f), new Vector3(22.6f, 0.05f, 0.3f));
                Link(new Vector3(0f, 1.25f, -9.8f), new Vector3(0f, 0.05f, -8.6f));
            }
            Debug.Log($"[LR2] NavMesh baked: {navAssetPath}");
        }

        private static void GenerateCoverPoints(Vector2 playMin, Vector2 playMax, float[] sampleLevels)
        {
            int made = 0;
            var positions = new List<Vector3>();
            Vector3[] dirs = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right, new Vector3(1, 0, 1).normalized, new Vector3(-1, 0, 1).normalized, new Vector3(1, 0, -1).normalized, new Vector3(-1, 0, -1).normalized };
            for (float x = playMin.x + 1f; x < playMax.x; x += 2.2f)
                for (float z = playMin.y + 1f; z < playMax.y; z += 2.2f)
                {
                foreach (float level in sampleLevels)
                {
                    if (!NavMesh.SamplePosition(new Vector3(x, level, z), out var hit, 1.3f, NavMesh.AllAreas)) continue;
                    Vector3 p = hit.position;
                    if (Mathf.Abs(p.y - level) > 1.4f) continue;
                    if (positions.Exists(q => (q - p).sqrMagnitude < 6f)) continue;
                    foreach (var d in dirs)
                    {
                        if (!Physics.Raycast(p + Vector3.up * 0.9f, d, out var wall, 1.3f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                        bool high = Physics.Raycast(p + Vector3.up * 1.6f, d, 1.4f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
                        var go = new GameObject("Cover");
                        go.transform.SetParent(_cover, false);
                        go.transform.position = p;
                        go.transform.rotation = Quaternion.LookRotation(-wall.normal);
                        go.AddComponent<CoverPoint>().crouchCover = !high;
                        positions.Add(p);
                        made++;
                        break;
                    }
                }
                }
            Debug.Log($"[LR2] Cover points generated: {made}");
        }

        /// <summary>Tactical minimap rendered by top-down raycasts (independent of GPU/batch-mode rendering).</summary>
        private static void BuildMinimap(string textureOutPath, Vector2 playMin, Vector2 playMax)
        {
            Vector2 min = playMin - new Vector2(3f, 3f), max = playMax + new Vector2(3f, 3f);
            int w = 600, h = Mathf.RoundToInt(w * (max.y - min.y) / (max.x - min.x));
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var heights = new float[w, h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float wx = Mathf.Lerp(min.x, max.x, (x + 0.5f) / w), wz = Mathf.Lerp(min.y, max.y, (y + 0.5f) / h);
                    heights[x, y] = Physics.Raycast(new Vector3(wx, 18f, wz), Vector3.down, out var hit, 28f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) ? hit.point.y : -2f;
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float ht = heights[x, y];
                    float edge = 0f;
                    if (x > 0 && y > 0 && x < w - 1 && y < h - 1)
                        edge = Mathf.Abs(heights[x + 1, y] - heights[x - 1, y]) + Mathf.Abs(heights[x, y + 1] - heights[x, y - 1]);
                    Color c;
                    if (ht < -1f) c = new Color(0.05f, 0.1f, 0.13f, 1f); // water
                    else
                    {
                        float lum = 0.16f + Mathf.Clamp01(ht / 8f) * 0.42f;
                        c = new Color(lum * 0.92f, lum, lum * 1.02f, 1f);
                        if (edge > 0.4f) c = Color.Lerp(c, new Color(0.85f, 0.88f, 0.9f), 0.65f);
                    }
                    tex.SetPixel(x, y, c);
                }
            tex.Apply();
            var saved = SaveTexture(tex, textureOutPath, false, false, false);
            var data = new GameObject("MinimapData").AddComponent<MinimapData>();
            data.transform.SetParent(_systems, false);
            data.mapTexture = saved;
            data.worldMin = min;
            data.worldMax = max;
        }

        // ================================================================================================= main menu scene
        public static void BuildMenuScene()
        {
            _rnd ??= new System.Random(1024);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cfg = LoadOrCreate<MapBuildConfig>(ConfigPath);
            if (_m == null) _m = MakeMaterials();
            var set = new GameObject("MenuSet").transform;
            Box("Floor", set, new Vector3(0f, -0.25f, 0f), new Vector3(40f, 0.5f, 40f), _m.asphalt);
            Container(set, "BackA", new Vector3(-1.5f, 0f, 4.2f), 0f, 0);
            Container(set, "BackB", new Vector3(-1.5f, 2.59f, 4.2f), 0f, 3);
            Container(set, "Side", new Vector3(5.2f, 0f, 1.5f), 70f, 1);
            var crate = PropOrBox("SM_Crate_01", "Crate", set, new Vector3(1.6f, 0f, 1.4f), 12f, 1f, new Vector3(0.9f, 0.9f, 0.9f), _m.wood, SurfaceType.Wood);
            PropOrBox("SM_Cablespool", "Crate2", set, new Vector3(2.55f, 0f, 1.9f), -22f, 0.95f, new Vector3(0.7f, 0.7f, 0.7f), _m.wood, SurfaceType.Metal);
            Prop("SM_Barrel_01", set, new Vector3(-1.35f, 0f, 2.4f), 35f, 1f, false);
            Prop("SM_Gascylender", set, new Vector3(-1.9f, 0f, 2.75f), 15f, 1f, false);
            Prop("SM_Woodpallet_1", set, new Vector3(0.6f, 0f, 3.1f), 40f, 1f, false);
            float crateTopY = 0.62f;
            if (crate != null)
            {
                var crateRenderers = crate.GetComponentsInChildren<Renderer>(true);
                if (crateRenderers.Length > 0)
                {
                    crateTopY = crateRenderers[0].bounds.max.y;
                    for (int ri = 1; ri < crateRenderers.Length; ri++)
                        crateTopY = Mathf.Max(crateTopY, crateRenderers[ri].bounds.max.y);
                }
            }
            var lantern = Prop("SM_Lantern_01", set, new Vector3(1.6f, crateTopY, 1.4f), 0f, 1f, false);
            if (lantern != null)
            {
                var lRenderers = lantern.GetComponentsInChildren<Renderer>(true);
                if (lRenderers.Length > 0)
                {
                    float minY = lRenderers[0].bounds.min.y;
                    for (int ri = 1; ri < lRenderers.Length; ri++)
                        minY = Mathf.Min(minY, lRenderers[ri].bounds.min.y);
                    lantern.transform.position += Vector3.up * (crateTopY - 0.005f - minY);
                }
            }

            var bot = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/Characters/LR2_Soldier_Bot.prefab");
            if (bot != null)
            {
                var hero = (GameObject)PrefabUtility.InstantiatePrefab(bot);
                hero.name = "HeroSoldier";
                hero.transform.SetPositionAndRotation(new Vector3(0.4f, 0f, 1.6f), Quaternion.Euler(0f, 205f, 0f));
                var bc = hero.GetComponent<BotController>();
                if (bc != null) Object.DestroyImmediate(bc);
                var agent = hero.GetComponent<NavMeshAgent>();
                if (agent != null) Object.DestroyImmediate(agent);
                var c = hero.GetComponent<Combatant>();
                if (c != null)
                {
                    c.DisplayName = "OPERATOR";
                    c.IsLocalPlayer = false;
                    c.IsBot = false;
                    c.Team = TeamId.Alpha;
                }
                var alliedUniform = AssetDatabase.LoadAssetAtPath<Material>("Assets/LR2/Materials/Characters/M_Uniform_Allied_NavyBlue.mat");
                var enemyUniform = AssetDatabase.LoadAssetAtPath<Material>("Assets/LR2/Materials/Characters/M_Uniform_Enemy_DesertCrimson.mat");
                var skullMask = AssetDatabase.LoadAssetAtPath<Material>("Assets/LR2/Materials/Characters/M_FaceMask_GhostSkull.mat");
                var enemyMask = AssetDatabase.LoadAssetAtPath<Material>("Assets/LR2/Materials/Characters/M_FaceMask_EnemyMarauder.mat");
                var sv = hero.GetComponent<SoldierVisuals>();
                var swatVis = hero.transform.Find("SWAT_Visual") ?? hero.transform.Find("MilitaryVisual");
                if (swatVis != null)
                {
                    if (sv != null)
                    {
                        sv.SetGear(alliedUniform, enemyUniform, skullMask, enemyMask);
                        sv.ApplyMaterialsNow(true);
                    }
                    foreach (var r in hero.GetComponentsInChildren<Renderer>(true))
                    {
                        bool underSwat = r.transform.IsChildOf(swatVis);
                        bool underWeapon = r.transform.GetComponentInParent<Weapons.LR2Item>(true) != null;
                        if (!underSwat && !underWeapon) r.enabled = false;
                        else r.enabled = true;
                    }
                    foreach (var smr in hero.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (!smr.transform.IsChildOf(swatVis)) continue;
                        var mats = smr.sharedMaterials;
                        bool changed = false;
                        for (int mi = 0; mi < mats.Length; mi++)
                        {
                            if (mats[mi] == null) continue;
                            string mn = mats[mi].name.ToLowerInvariant();
                            if ((mn.Contains("head") || mn.Contains("facemask") || mn.Contains("skull") || mn.Contains("balaclava")) && skullMask != null)
                            {
                                mats[mi] = skullMask;
                                changed = true;
                            }
                            else if ((mn.Contains("body") || mn.Contains("uniform")) && alliedUniform != null)
                            {
                                mats[mi] = alliedUniform;
                                changed = true;
                            }
                        }
                        if (changed) smr.sharedMaterials = mats;
                    }
                }
                else
                {
                    // B136 (Kinemation robot rig): keep teamSwaps intact for HeroSoldier (allied blue) and MenuCinematic clones (hostiles crimson),
                    // and make all body + head renderers visible in the third-person menu scene.
                    if (sv != null) sv.ApplyMaterialsNow(true);
                    foreach (var r in hero.GetComponentsInChildren<Renderer>(true))
                    {
                        bool underWeapon = r.transform.GetComponentInParent<Weapons.LR2Item>(true) != null;
                        if (underWeapon) continue;
                        r.enabled = true;
                        r.shadowCastingMode = ShadowCastingMode.On;
                    }
                }
            }

            var camGo = new GameObject("MenuCamera");
            camGo.tag = "MainCamera";
            camGo.transform.SetPositionAndRotation(new Vector3(-1.05f, 1.48f, -1.85f), Quaternion.Euler(3f, 28.5f, 0f));
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 38f;
            var hd = camGo.AddComponent<HDAdditionalCameraData>();
            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
            hd.TAAQuality = HDAdditionalCameraData.TAAQualityLevel.High;
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<MenuCameraDrift>();

            var lights = new GameObject("Lighting").transform;
            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(lights, false);
            sunGo.transform.rotation = Quaternion.Euler(9f, 150f, 0f);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.lightUnit = LightUnit.Lux;
            sun.intensity = 9000f;
            sun.useColorTemperature = true;
            sun.colorTemperature = 3600f;
            sun.shadows = LightShadows.Soft;
            sunGo.AddComponent<HDAdditionalLightData>();
            AddLight(lights, "RimLight", new Vector3(2.2f, 2.6f, 3.4f), Quaternion.LookRotation(new Vector3(0.4f, 1.2f, 1.6f) - new Vector3(2.2f, 2.6f, 3.4f)), LightType.Spot, 5800f, 28000f, 12f, 54f, true);
            AddLight(lights, "KeyLight", new Vector3(-2.2f, 2.25f, -0.8f), Quaternion.LookRotation(new Vector3(0.4f, 1.28f, 1.6f) - new Vector3(-2.2f, 2.25f, -0.8f)), LightType.Spot, 4500f, 24000f, 12f, 64f, false);
            AddLight(lights, "FillLight", new Vector3(0.1f, 1.55f, -1.2f), Quaternion.LookRotation(new Vector3(0.4f, 1.25f, 1.6f) - new Vector3(0.1f, 1.55f, -1.2f)), LightType.Spot, 5200f, 11500f, 9f, 55f, false);
            AddLight(lights, "LanternGlow", new Vector3(1.6f, crateTopY + 0.14f, 1.4f), Quaternion.identity, LightType.Point, 2400f, 220f, 4.5f, 0f, false);

            var profile = NewProfile(LightDir + "/LR2_Menu_Profile.asset");
            var ve = profile.Add<VisualEnvironment>(true);
            ve.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            ApplyHdriSky(ve, profile, "Assets/LeartesStudios/Art/Textures/T_HDRI_01.HDR", exposureEv: 10.6f, rotationDeg: 28f);
            var ilcMenu = profile.Add<IndirectLightingController>(true);
            ilcMenu.indirectDiffuseLightingMultiplier.Override(1.15f);
            ilcMenu.reflectionLightingMultiplier.Override(1.10f);
            var fog = profile.Add<Fog>(true);
            fog.enabled.Override(true);
            fog.enableVolumetricFog.Override(true);
            fog.meanFreePath.Override(38f);
            fog.maximumHeight.Override(8.5f);
            fog.albedo.Override(new Color(0.6f, 0.66f, 0.72f));
            fog.anisotropy.Override(0.8f);
            var ex = profile.Add<Exposure>(true);
            ex.mode.Override(ExposureMode.AutomaticHistogram);
            ex.compensation.Override(0.0f);
            ex.limitMin.Override(5.5f);
            ex.limitMax.Override(11.5f);
            ex.adaptationMode.Override(AdaptationMode.Progressive);
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.ACES);
            var color = profile.Add<ColorAdjustments>(true);
            color.contrast.Override(30f);
            color.saturation.Override(-12f);
            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(0.08f);
            bloom.threshold.Override(2.5f);
            bloom.scatter.Override(0.35f);
            bloom.dirtIntensity.Override(0f);
            var vig = profile.Add<Vignette>(true);
            vig.intensity.Override(0.38f);
            var grain = profile.Add<FilmGrain>(true);
            grain.intensity.Override(0.25f);
            var dof = profile.Add<DepthOfField>(true);
            dof.focusMode.Override(DepthOfFieldMode.Manual);
            dof.nearFocusStart.Override(0f);
            dof.nearFocusEnd.Override(0.4f);
            dof.farFocusStart.Override(4.5f);
            dof.farFocusEnd.Override(12f);
            var menuAo = profile.Add<ScreenSpaceAmbientOcclusion>(true);
            menuAo.intensity.Override(0.78f);
            menuAo.radius.Override(1.45f);
            menuAo.directLightingStrength.Override(0.24f);
            var menuCs = profile.Add<ContactShadows>(true);
            menuCs.enable.Override(true);
            menuCs.length.Override(0.34f);
            menuCs.opacity.Override(0.90f);
            DisableMotionBlur(profile);
            SaveProfile(profile);
            var vol = new GameObject("Volume").AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = profile;

            var ui = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/LR2/Prefabs/UI/LR2_MainMenuUI.prefab");
            if (ui != null) PrefabUtility.InstantiatePrefab(ui);
            LR2UIBuilder.EnsureEventSystem();
            var amb = new GameObject("Ambience").AddComponent<LR2AudioEnvironment>();
            var common = AssetDatabase.LoadAssetAtPath<LR2CommonSounds>("Assets/LR2/Resources/LR2/LR2CommonSounds.asset");
            if (common != null) amb.ambienceLoops = common.ambienceLoops;

            EditorSceneManager.SaveScene(scene, MenuScenePath);
            SetBuildScenes();
            Debug.Log("[LR2] Main menu scene built: " + MenuScenePath);
        }

        public static void SetBuildScenes()
        {
            var required = new[] { MenuScenePath, ScenePath, DeadsectorScenePath, FoundryScenePath, TestRangeScenePath };
            var list = new List<EditorBuildSettingsScene>();
            var added = new HashSet<string>();
            foreach (var p in required)
            {
                if (File.Exists(p) && added.Add(p)) list.Add(new EditorBuildSettingsScene(p, true));
            }
            foreach (var s in EditorBuildSettings.scenes)
                if (added.Add(s.path) && File.Exists(s.path)) list.Add(new EditorBuildSettingsScene(s.path, false));
            EditorBuildSettings.scenes = list.ToArray();
        }
    }
}
