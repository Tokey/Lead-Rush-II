using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LR2.EditorTools
{
    /// <summary>One-click (or batch-mode) build of all Lead Rush II content. Menu: LR2 > Build Everything.</summary>
    public static class LR2BuildPipeline
    {
        private const string AutoBuildVersion = "v108"; // Claude: Kinemation driver + SWAT visual proxy, gear
        private const string StampPath = "Library/LR2_AutoBuildStamp.txt";
        private const string ReportPath = "Library/LR2_AutoBuildReport.txt";

        [InitializeOnLoadMethod]
        private static void ScheduleAutoRebuildOnScriptReload()
        {
            var args = Environment.GetCommandLineArgs();
            bool automatedRun = Application.isBatchMode
                || Array.IndexOf(args, "-executeMethod") >= 0
                || Array.IndexOf(args, "-lr2PoseCheck") >= 0
                || Array.IndexOf(args, "-lr2QaBot") >= 0
                || Array.IndexOf(args, "-lr2CaptureMedia") >= 0;
            if (!automatedRun) EnsureMainMenuPlayModeStart();
            if (automatedRun) return;
            if (File.Exists(StampPath) && File.ReadAllText(StampPath).Trim() == AutoBuildVersion) return;
            EditorApplication.delayCall += RunAutoRebuildIfReady;
        }

        /// <summary>Ensures Editor Play always boots into LR2_MainMenu unless an automated test harness temporarily clears it.</summary>
        public static void EnsureMainMenuPlayModeStart()
        {
            if (Application.isBatchMode || Application.isPlaying) return;
            if (SessionState.GetBool("LR2Smoke.Active", false) || SessionState.GetBool("LR2GeminiVerify.Active", false) || SessionState.GetBool("LR2ReadmeMedia.Active", false)) return;
            var menuAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(LR2MapBuilder.MenuScenePath);
            if (menuAsset != null && EditorSceneManager.playModeStartScene != menuAsset)
                EditorSceneManager.playModeStartScene = menuAsset;
        }

        public static void RestoreMainMenuLastSceneSetup()
        {
            string setupPath = Path.Combine(Directory.GetCurrentDirectory(), "Library", "LastSceneManagerSetup.txt");
            if (File.Exists(LR2MapBuilder.MenuScenePath))
            {
                File.WriteAllText(setupPath,
                    "sceneSetups:\n- path: " + LR2MapBuilder.MenuScenePath.Replace('\\', '/') + "\n  isLoaded: 1\n  isActive: 1\n  isSubScene: 0\n");
            }
        }

        private static void RunAutoRebuildIfReady()
        {
            if (Application.isBatchMode) return;
            if (File.Exists(StampPath) && File.ReadAllText(StampPath).Trim() == AutoBuildVersion) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += RunAutoRebuildIfReady;
                return;
            }

            File.WriteAllText(StampPath, AutoBuildVersion);
            bool ok = true;
            ok &= Step("FX", LR2ContentBuilder.BuildFx);
            ok &= Step("Audio", LR2ContentBuilder.BuildAudio);
            ok &= Step("Weapons", LR2ContentBuilder.BuildWeapons);
            ok &= Step("Soldiers", LR2ContentBuilder.BuildSoldiers);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string gripInfo = File.Exists("Library/LR2_GripCalibration.txt") ? File.ReadAllText("Library/LR2_GripCalibration.txt") : "n/a";
            File.WriteAllText(ReportPath,
                $"version={AutoBuildVersion}\nutc={DateTime.UtcNow:O}\nok={ok}\ngrip={gripInfo}\n");
            Debug.Log($"[LR2] Auto-rebuild {AutoBuildVersion}: {(ok ? "SUCCESS" : "FAILED")} ({gripInfo.Trim()})");
        }

        [MenuItem("LR2/Build Everything", priority = 0)]
        public static void BuildEverything() => Run(true);

        [MenuItem("LR2/Build All Maps Only", priority = 20)]
        public static void BuildMapOnly()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Step("Map: Saltworks", LR2MapBuilder.Build);
            Step("Map: Deadsector", LR2MapBuilder.BuildDeadsector);
            Step("Map: Foundry", LR2MapBuilder.BuildFoundry);
            Step("Map: Test Range", LR2MapBuilder.BuildTestRange);
        }

        [MenuItem("LR2/Build Menu Scene Only", priority = 21)]
        public static void BuildMenuOnly() => Step("Menu scene", LR2MapBuilder.BuildMenuScene);

        [MenuItem("LR2/Open Saltworks", priority = 40)]
        public static void OpenMap() => EditorSceneManager.OpenScene(LR2MapBuilder.ScenePath);

        [MenuItem("LR2/Open Deadsector", priority = 41)]
        public static void OpenDeadsector() => EditorSceneManager.OpenScene(LR2MapBuilder.DeadsectorScenePath);

        [MenuItem("LR2/Open Foundry", priority = 42)]
        public static void OpenFoundry() => EditorSceneManager.OpenScene(LR2MapBuilder.FoundryScenePath);

        [MenuItem("LR2/Open Test Range", priority = 43)]
        public static void OpenTestRange() => EditorSceneManager.OpenScene(LR2MapBuilder.TestRangeScenePath);

        [MenuItem("LR2/Open Main Menu", priority = 45)]
        public static void OpenMenu() => EditorSceneManager.OpenScene(LR2MapBuilder.MenuScenePath);

        /// <summary>Batch entry: Unity -batchmode -executeMethod LR2.EditorTools.LR2BuildPipeline.BuildAllBatch -quit</summary>
        public static void BuildAllBatch()
        {
            try
            {
                bool ok = Run(false);
                if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LR2] BUILD FAILED with exception: {ex}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        /// <summary>Builds the Windows 64-bit standalone executable into the configured build location.</summary>
        public static bool BuildWindowsPlayer()
        {
            ConfigureBuildIdentity();
            EditorUserBuildSettings.buildScriptsOnly = false;
            const string outExe = "H:/Unity Projects/Builds/Lead Rush II/Lead Rush II.exe";
            var scenes = Array.FindAll(EditorBuildSettings.scenes, s => s != null && s.enabled && File.Exists(s.path));
            var scenePaths = Array.ConvertAll(scenes, s => s.path);
            Directory.CreateDirectory(Path.GetDirectoryName(outExe));

            var opts = new BuildPlayerOptions
            {
                scenes = scenePaths,
                locationPathName = outExe,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(opts);
            bool ok = report != null && report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded;
            Debug.Log($"[LR2] Windows Build result={report?.summary.result} size={report?.summary.totalSize} errors={report?.summary.totalErrors} -> {outExe}");
            return ok;
        }

        /// <summary>Fast batch entry: rebuilds Ghost gear/uniforms, UI prefabs, and LR2_MainMenu hero scene without rebuilding map geometry.</summary>
        public static void RebuildGearUiAndMenuBatch()
        {
            try
            {
                ConfigureBuildIdentity();
                bool ok = true;
                ok &= Step("Military visual & gear", LR2MilitaryVisualBuilder.ApplyToSoldiers);
                ok &= Step("UI", LR2UIBuilder.Build);
                ok &= Step("Main menu scene", LR2MapBuilder.BuildMenuScene);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                RestoreMainMenuLastSceneSetup();
                Debug.Log(ok ? "[LR2] RebuildGearUiAndMenuBatch: SUCCESS" : "[LR2] RebuildGearUiAndMenuBatch: FAILED");
                if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LR2] RebuildGearUiAndMenuBatch FAILED: {ex}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        /// <summary>Batch entry: builds all content + maps if needed, then builds Windows 64-bit standalone executable.</summary>
        public static void BuildWindowsPlayerBatch()
        {
            bool ok = BuildWindowsPlayer();
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>Batch entry: rebuilds all content &amp; scenes, then builds the Windows standalone executable.</summary>
        public static void BuildAllAndPlayerBatch()
        {
            bool ok = Run(false) && BuildWindowsPlayer();
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        [MenuItem("LR2/Configure Build Icon & Version", priority = 35)]
        public static void ConfigureBuildIdentity()
        {
            PlayerSettings.bundleVersion = "11.24.26.2";
            EditorUserBuildSettings.buildScriptsOnly = false;
            const string iconPath = "Assets/LR2/UI/Icons/LR2_AppIcon.png";
            Directory.CreateDirectory(Path.GetDirectoryName(iconPath));
            if (!File.Exists(iconPath))
            {
                const int size = 256;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var px = new Color32[size * size];
                var bgOuter = new Color(0.055f, 0.068f, 0.090f, 1f);
                var bgInner = new Color(0.095f, 0.118f, 0.155f, 1f);
                var amber = new Color(0.95f, 0.65f, 0.18f, 1f);
                var bright = new Color(0.98f, 0.94f, 0.86f, 1f);
                Vector2 c = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = x - c.x, dy = y - c.y;
                        float r = Mathf.Sqrt(dx * dx + dy * dy) / (size * 0.5f);
                        Color col = Color.Lerp(bgInner, bgOuter, Mathf.Clamp01(r));

                        // Outer metallic bevel border
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) > size * 0.44f && Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) < size * 0.465f)
                            col = amber * 0.72f;

                        // Precision ring reticle
                        if (Mathf.Abs(r - 0.62f) < 0.032f && (Mathf.Abs(dx) > 14f || Mathf.Abs(dy) > 14f))
                            col = amber;

                        // Crosshair ticks
                        if (Mathf.Abs(dy) < 5f && Mathf.Abs(dx) > 38f && Mathf.Abs(dx) < 102f)
                            col = bright;
                        if (Mathf.Abs(dx) < 5f && Mathf.Abs(dy) > 38f && Mathf.Abs(dy) < 102f)
                            col = bright;

                        // Tactical chevron in lower quadrant
                        float chy = dy + 16f;
                        if (chy > -24f && chy < 4f && Mathf.Abs(Mathf.Abs(dx) - (4f - chy) * 1.15f) < 6f)
                            col = amber;

                        // Crisp center dot
                        if (r < 0.045f) col = amber;

                        px[y * size + x] = col;
                    }
                }
                tex.SetPixels32(px);
                tex.Apply();
                File.WriteAllBytes(iconPath, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(iconPath, ImportAssetOptions.ForceSynchronousImport);
            }

            if (AssetImporter.GetAtPath(iconPath) is TextureImporter ti)
            {
                bool dirty = ti.textureType != TextureImporterType.GUI || !ti.isReadable || ti.mipmapEnabled;
                if (dirty)
                {
                    ti.textureType = TextureImporterType.GUI;
                    ti.isReadable = true;
                    ti.mipmapEnabled = false;
                    ti.SaveAndReimport();
                }
            }

            var iconTex = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
            if (iconTex != null)
            {
                PlayerSettings.SetIcons(UnityEditor.Build.NamedBuildTarget.Unknown, new[] { iconTex }, IconKind.Any);
                int[] sizes = PlayerSettings.GetIconSizes(UnityEditor.Build.NamedBuildTarget.Standalone, IconKind.Any);
                if (sizes != null && sizes.Length > 0)
                {
                    var list = new Texture2D[sizes.Length];
                    for (int i = 0; i < sizes.Length; i++) list[i] = iconTex;
                    PlayerSettings.SetIcons(UnityEditor.Build.NamedBuildTarget.Standalone, list, IconKind.Any);
                }
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Batch entry (no -quit): rebuild FX, audio, weapons and soldiers (no maps), then start the play-mode smoke test in
        /// the same process. Honours -lr2Scene, -lr2Duration, -lr2OutDir and the -lr2Robot / -lr2PoseCheck harness flags.
        /// The smoke test exits Unity when it finishes.
        /// </summary>
        public static void BuildCharactersThenPlayBatch()
        {
            bool ok = true;
            // B136: a Kinemation re-import brings URP materials back; convert them before the guns and soldiers are built
            ok &= Step("Convert Kinemation demo materials", LR2ContentBuilder.ConvertDemoMaterials);
            ok &= Step("FX", LR2ContentBuilder.BuildFx);
            ok &= Step("Audio", LR2ContentBuilder.BuildAudio);
            ok &= Step("Weapons", LR2ContentBuilder.BuildWeapons);
            ok &= Step("Soldiers", LR2ContentBuilder.BuildSoldiers);
            ok &= Step("Military visual mirror", LR2MilitaryVisualBuilder.ApplyToSoldiers);
            AssetDatabase.SaveAssets();
            EditorUtility.ClearProgressBar();
            Debug.Log($"[LR2] Characters rebuilt: {(ok ? "SUCCESS" : "FAILED")}");
            if (!ok && Application.isBatchMode)
            {
                EditorApplication.Exit(1);
                return;
            }
            LR2SmokeTest.RunBatch();
        }

        private static bool Run(bool interactive)
        {
            if (interactive && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
            ConfigureBuildIdentity();
            bool ok = true;
            ok &= Step("Convert Kinemation demo materials", LR2ContentBuilder.ConvertDemoMaterials);
            ok &= Step("Data presets (difficulty, modes, fonts)", LR2ContentBuilder.BuildData);
            ok &= Step("FX", LR2ContentBuilder.BuildFx);
            ok &= Step("Audio", LR2ContentBuilder.BuildAudio);
            ok &= Step("Weapons", LR2ContentBuilder.BuildWeapons);
            ok &= Step("Soldiers", LR2ContentBuilder.BuildSoldiers);
            ok &= Step("Military visual mirror", LR2MilitaryVisualBuilder.ApplyToSoldiers);
            ok &= Step("Gameplay prefabs", LR2ContentBuilder.BuildGameplayPrefabs);
            ok &= Step("UI", LR2UIBuilder.Build);
            ok &= Step("Map: Saltworks", LR2MapBuilder.Build);
            ok &= Step("Map: Deadsector", LR2MapBuilder.BuildDeadsector);
            ok &= Step("Map: Foundry", LR2MapBuilder.BuildFoundry);
            ok &= Step("Map: Test Range", LR2MapBuilder.BuildTestRange);
            ok &= Step("Main menu scene", LR2MapBuilder.BuildMenuScene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(ok ? "[LR2] BUILD EVERYTHING: SUCCESS" : "[LR2] BUILD EVERYTHING: FINISHED WITH ERRORS (see log)");
            if (ok)
            {
                EditorSceneManager.OpenScene(LR2MapBuilder.MenuScenePath);
                RestoreMainMenuLastSceneSetup();
            }
            return ok;
        }

        private static bool Step(string name, Action action)
        {
            try
            {
                EditorUtility.DisplayProgressBar("Lead Rush II", name, 0.5f);
                action();
                Debug.Log($"[LR2] Step OK: {name}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[LR2] Step FAILED: {name}\n{e}");
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
