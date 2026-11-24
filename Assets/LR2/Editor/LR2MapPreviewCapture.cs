using System.IO;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using LR2.FX;
using LR2.Game;
using LR2.UI;
using LR2.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace LR2.EditorTools
{
    /// <summary>
    /// Captures 16:9 hero preview thumbnails for every map in LR2Session.MapScenes into
    /// Assets/LR2/Resources/LR2/MapPreviews/ so the Main Menu can show live map previews.
    /// </summary>
    public static class LR2MapPreviewCapture
    {
        private const int PreviewWidth = 960;
        private const int PreviewHeight = 540;
        private const string OutputFolder = "Assets/LR2/Resources/LR2/MapPreviews";
        private const string StampVersion = "v2";
        private const string StampPath = "Library/LR2_GeminiUiStamp.txt";
        private const string ReportPath = "Library/LR2_GeminiBuildReport.txt";
        private static double _readyAt;

        [InitializeOnLoadMethod]
        private static void ScheduleUiAndPreviewCapture()
        {
            if (Application.isBatchMode) return;
            if (File.Exists(StampPath) && File.ReadAllText(StampPath).Trim() == StampVersion) return;
            // Delay slightly so Claude's LR2BuildPipeline auto-rebuild finishes first on domain reload
            _readyAt = EditorApplication.timeSinceStartup + 2.5;
            EditorApplication.update -= WaitThenCapture;
            EditorApplication.update += WaitThenCapture;
        }

        private static void WaitThenCapture()
        {
            if (Application.isBatchMode || EditorApplication.timeSinceStartup < _readyAt) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (File.Exists(LR2AgentBridge.CommandPath) || File.Exists(LR2AgentBridge.RobotFlag)) return;
            if (SessionState.GetBool("LR2GeminiVerify.Active", false)) return;
            if (SceneManager.GetActiveScene().isDirty) return; // Never discard user's unsaved scene edits

            EditorApplication.update -= WaitThenCapture;
            if (File.Exists(StampPath) && File.ReadAllText(StampPath).Trim() == StampVersion) return;
            File.WriteAllText(StampPath, StampVersion);

            bool uiOk = false;
            int count = 0;
            try
            {
                LR2UIBuilder.Build();
                uiOk = true;
                count = CaptureAll(restorePreviousScene: true);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
            File.WriteAllText(ReportPath, $"version={StampVersion}\nutc={System.DateTime.UtcNow:O}\nui={uiOk}\npreviews={count}/{LR2Session.MapScenes.Length}\n");
            Debug.Log($"[LR2] Gemini {StampVersion} UI + Map Previews: ui={uiOk}, previews={count}/{LR2Session.MapScenes.Length}");
        }

        [MenuItem("LR2/UI/Capture Map Previews", priority = 220)]
        public static void CaptureAllInteractive()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("[LR2] Exit Play mode before capturing map previews.");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            CaptureAll(restorePreviousScene: true);
        }

        public static void CaptureAllBatch() => CaptureAll(restorePreviousScene: false);

        public static int CaptureAll(bool restorePreviousScene)
        {
            EnsureOutputDirectory();
            string prevScenePath = SceneManager.GetActiveScene().path;
            int captured = 0;

            try
            {
                for (int i = 0; i < LR2Session.MapScenes.Length; i++)
                {
                    string sceneName = LR2Session.MapScenes[i];
                    string scenePath = $"Assets/LR2/Scenes/{sceneName}.unity";
                    if (!File.Exists(scenePath))
                    {
                        Debug.LogWarning($"[LR2MapPreviewCapture] Missing scene at {scenePath}");
                        continue;
                    }

                    EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                    string outPath = $"{OutputFolder}/Preview_{sceneName}.png";
                    if (CaptureCurrentScene(sceneName, outPath))
                    {
                        captured++;
                        Debug.Log($"[LR2MapPreviewCapture] Captured {outPath}");
                    }
                }
            }
            finally
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                ConfigureSpriteImporters();
                if (restorePreviousScene && !string.IsNullOrEmpty(prevScenePath) && File.Exists(prevScenePath))
                    EditorSceneManager.OpenScene(prevScenePath, OpenSceneMode.Single);
            }

            Debug.Log($"[LR2MapPreviewCapture] Completed {captured}/{LR2Session.MapScenes.Length} map preview captures.");
            return captured;
        }

        private static void EnsureOutputDirectory()
        {
            Directory.CreateDirectory(OutputFolder);
        }

        private static bool CaptureCurrentScene(string sceneName, string assetPath)
        {
            Bounds arena = ComputeArenaBounds(out Vector3[] spawnPositions, out float floorY);
            Vector3 lookTarget = new Vector3(arena.center.x, floorY + 3.0f, arena.center.z);
            float baseRadius = Mathf.Clamp(Mathf.Max(arena.extents.x, arena.extents.z) * 0.68f, 24f, 44f);

            Light sun = FindSunLight();
            Vector3 sunDir = sun != null ? -sun.transform.forward : new Vector3(0.5f, 0.7f, 0.4f).normalized;
            Vector3 flatSun = new Vector3(sunDir.x, 0f, sunDir.z);
            float sunCamAz = flatSun.sqrMagnitude > 0.01f
                ? Mathf.Atan2(-flatSun.x, -flatSun.z) * Mathf.Rad2Deg
                : GetPreferredAzimuth(sceneName);

            Vector3 bestCamPos = lookTarget + Quaternion.Euler(0f, sunCamAz, 0f) * new Vector3(0f, 6.8f, -Mathf.Clamp(baseRadius * 1.15f, 28f, 44f));
            float bestScore = float.MinValue;

            // Gentle ~7-13 deg downward pitch from outer arena perimeter so sky, skyline, and cover all frame together
            float[] heights = { 5.8f, 8.2f };
            float[] radii = { Mathf.Clamp(baseRadius * 1.05f, 26f, 42f), Mathf.Clamp(baseRadius * 1.32f, 34f, 52f) };
            float[] azOffsets = { 0f, 22f, -22f, 45f, -45f, 75f, -75f, 110f, -110f, 145f, -145f, 180f };

            foreach (float radius in radii)
            {
                foreach (float height in heights)
                {
                    foreach (float off in azOffsets)
                    {
                        float az = sunCamAz + off;
                        Vector3 horizDir = Quaternion.Euler(0f, az, 0f) * Vector3.back;
                        Vector3 candidate = new Vector3(lookTarget.x, floorY, lookTarget.z) + horizDir * radius + Vector3.up * height;

                        if (Physics.CheckSphere(candidate, 1.8f, ~0, QueryTriggerInteraction.Ignore)) continue;
                        Vector3 viewDir = (lookTarget - candidate).normalized;
                        if (Physics.SphereCast(candidate, 0.85f, viewDir, out _, 10.5f, ~0, QueryTriggerInteraction.Ignore))
                            continue;

                        int visibleSpawns = 0;
                        foreach (var sp in spawnPositions)
                        {
                            Vector3 spEye = sp + Vector3.up * 1.6f;
                            if (!Physics.Linecast(candidate, spEye, ~0, QueryTriggerInteraction.Ignore))
                                visibleSpawns++;
                        }

                        float sunAlign = Vector3.Dot(-viewDir, sunDir);
                        float heightPref = 1f - Mathf.Abs(height - 7.0f) / 5f;
                        float score = visibleSpawns * 9f + sunAlign * 14f + heightPref * 4f;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestCamPos = candidate;
                        }
                    }
                }
            }

            if (bestScore <= float.MinValue * 0.5f)
            {
                for (int k = 0; k < 16; k++)
                {
                    float az = sunCamAz + k * 22.5f;
                    Vector3 horizDir = Quaternion.Euler(0f, az, 0f) * Vector3.back;
                    Vector3 candidate = new Vector3(lookTarget.x, floorY, lookTarget.z) + horizDir * Mathf.Clamp(baseRadius * 0.9f, 22f, 36f) + Vector3.up * 7.8f;
                    if (!Physics.CheckSphere(candidate, 1.6f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        bestCamPos = candidate;
                        break;
                    }
                }
            }

            var camGo = new GameObject("[LR2_PreviewCaptureCamera]");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            camGo.transform.position = bestCamPos;
            camGo.transform.LookAt(lookTarget, Vector3.up);

            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 52f;
            cam.nearClipPlane = 0.25f;
            cam.farClipPlane = 650f;
            cam.clearFlags = CameraClearFlags.Skybox;

            var hd = camGo.AddComponent<HDAdditionalCameraData>();
            hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
            hd.volumeLayerMask = ~0;
            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;

            var rt = new RenderTexture(PreviewWidth, PreviewHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.antiAliasing = 1;
            rt.Create();
            cam.targetTexture = rt;

            Volume fallbackVol = null;
            Texture2D tex = new Texture2D(PreviewWidth, PreviewHeight, TextureFormat.RGB24, false, false);

            try
            {
                // Render warmup frames so HDRP histogram exposure & volumetric clouds settle
                for (int w = 0; w < 4; w++) cam.Render();

                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, PreviewWidth, PreviewHeight), 0, 0, false);
                tex.Apply(false, false);

                float avgLum = SampleAverageLuminance(tex);
                if (avgLum < 0.045f || avgLum > 0.92f)
                {
                    // Fallback fixed exposure only when scene adaptation did not settle in EditMode
                    var volGo = new GameObject("[LR2_PreviewExposure]");
                    volGo.hideFlags = HideFlags.HideAndDontSave;
                    fallbackVol = volGo.AddComponent<Volume>();
                    fallbackVol.isGlobal = true;
                    fallbackVol.priority = 5000f;
                    var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                    profile.hideFlags = HideFlags.HideAndDontSave;
                    var exp = profile.Add<Exposure>(true);
                    exp.mode.Override(ExposureMode.Fixed);
                    exp.fixedExposure.Override(avgLum < 0.045f ? 10.2f : 12.6f);
                    fallbackVol.sharedProfile = profile;

                    cam.Render();
                    cam.Render();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, PreviewWidth, PreviewHeight), 0, 0, false);
                    tex.Apply(false, false);
                }

                RenderTexture.active = prev;
                File.WriteAllBytes(assetPath, tex.EncodeToPNG());
                return true;
            }
            finally
            {
                cam.targetTexture = null;
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
                Object.DestroyImmediate(camGo);
                if (fallbackVol != null)
                {
                    if (fallbackVol.sharedProfile != null) Object.DestroyImmediate(fallbackVol.sharedProfile);
                    Object.DestroyImmediate(fallbackVol.gameObject);
                }
            }
        }

        private static Bounds ComputeArenaBounds(out Vector3[] spawnPositions, out float floorY)
        {
            var spawns = Object.FindObjectsByType<SpawnPoint>();
            if (spawns != null && spawns.Length > 0)
            {
                var posList = new System.Collections.Generic.List<Vector3>(spawns.Length);
                Bounds b = new Bounds(spawns[0].transform.position, Vector3.zero);
                double sumY = 0.0;
                foreach (var sp in spawns)
                {
                    if (sp == null) continue;
                    b.Encapsulate(sp.transform.position);
                    posList.Add(sp.transform.position);
                    sumY += sp.transform.position.y;
                }
                spawnPositions = posList.ToArray();
                floorY = posList.Count > 0 ? (float)(sumY / posList.Count) : 0f;
                if (b.size.sqrMagnitude > 4f)
                {
                    b.Expand(new Vector3(12f, 6f, 12f));
                    return b;
                }
            }
            else
            {
                spawnPositions = System.Array.Empty<Vector3>();
                floorY = 0f;
            }

            var renderers = Object.FindObjectsByType<MeshRenderer>();
            Bounds rb = new Bounds(Vector3.zero, Vector3.one * 30f);
            bool has = false;
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled) continue;
                Vector3 ext = r.bounds.extents;
                if (ext.x > 140f || ext.z > 140f) continue; // ignore giant ground planes/skirts
                if (!has) { rb = r.bounds; has = true; }
                else rb.Encapsulate(r.bounds);
            }
            if (spawnPositions.Length == 0 && has) floorY = rb.min.y;
            return rb;
        }

        private static Light FindSunLight()
        {
            var lights = Object.FindObjectsByType<Light>();
            Light best = null;
            float maxIntensity = -1f;
            foreach (var l in lights)
            {
                if (l == null || !l.enabled || l.type != LightType.Directional) continue;
                if (l.intensity > maxIntensity)
                {
                    maxIntensity = l.intensity;
                    best = l;
                }
            }
            return best;
        }

        private static float GetPreferredAzimuth(string sceneName)
        {
            switch (sceneName)
            {
                case "LR2_Saltworks": return 35f;
                case "LR2_Deadsector": return -42f;
                case "LR2_Foundry": return 128f;
                case "LR2_TestRange": return 25f;
                default: return 35f;
            }
        }

        private static float SampleAverageLuminance(Texture2D tex)
        {
            Color32[] pixels = tex.GetPixels32();
            if (pixels == null || pixels.Length == 0) return 0.5f;
            int step = Mathf.Max(1, pixels.Length / 512);
            double sum = 0.0;
            int count = 0;
            for (int i = 0; i < pixels.Length; i += step)
            {
                Color c = pixels[i];
                sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
                count++;
            }
            return count > 0 ? (float)(sum / count) : 0.5f;
        }

        private static void ConfigureSpriteImporters()
        {
            foreach (string sceneName in LR2Session.MapScenes)
            {
                string path = $"{OutputFolder}/Preview_{sceneName}.png";
                if (!File.Exists(path)) continue;
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) continue;
                bool changed = ti.textureType != TextureImporterType.Sprite
                    || ti.spriteImportMode != SpriteImportMode.Single
                    || ti.mipmapEnabled
                    || ti.sRGBTexture == false
                    || ti.wrapMode != TextureWrapMode.Clamp;
                if (!changed) continue;

                ti.textureType = TextureImporterType.Sprite;
                ti.spriteImportMode = SpriteImportMode.Single;
                ti.sRGBTexture = true;
                ti.mipmapEnabled = false;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.filterMode = FilterMode.Bilinear;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.maxTextureSize = 1024;
                ti.SaveAndReimport();
            }
        }
    }

    /// <summary>
    /// End-to-end visible PlayMode verifier for Gemini deliverables: Main Menu + map thumbnails,
    /// Mode Rules persistence into MatchManager, two-column Settings, and GunBloodFX + HUD reload bar in Saltworks.
    /// </summary>
    [InitializeOnLoad]
    public static class LR2GeminiVerifyRunner
    {
        private const string KeyActive = "LR2GeminiVerify.Active";
        private const string KeyStage = "LR2GeminiVerify.Stage";
        private const string KeyAt = "LR2GeminiVerify.At";
        private const string KeyErrors = "LR2GeminiVerify.Errors";
        private const string KeyLog = "LR2GeminiVerify.Log";
        private const string KeyDeadline = "LR2GeminiVerify.Deadline";
        private const string VerifyDir = "Logs/LR2_GeminiVerify";
        private static bool _flickerDone;
        private static float _savedFov = -1f;
        private static float _savedBodycamMotion = -1f;

        static LR2GeminiVerifyRunner()
        {
            if (!SessionState.GetBool(KeyActive, false)) return;
            EditorApplication.update += Tick;
            Application.logMessageReceived += OnLog;
        }

        [MenuItem("LR2/UI/Run E2E Verification (Visible Play)", priority = 222)]
        public static void RunInteractive()
        {
            if (EditorApplication.isPlaying) return;
            StartVerify();
        }

        public static void RunVisibleAutoExit() => StartVerify();

        private static void StartVerify()
        {
            Directory.CreateDirectory(VerifyDir);
            foreach (var f in Directory.GetFiles(VerifyDir, "*.png")) File.Delete(f);

            _flickerDone = false;
            _savedFov = LR2Settings.FieldOfView;
            _savedBodycamMotion = LR2Settings.BodycamMotion;

            SessionState.SetBool(KeyActive, true);
            SessionState.SetInt(KeyStage, 0);
            SessionState.SetFloat(KeyAt, 0f);
            SessionState.SetInt(KeyErrors, 0);
            SessionState.SetString(KeyLog, "");
            SessionState.SetFloat(KeyDeadline, (float)EditorApplication.timeSinceStartup + 55f);

            LR2UIBuilder.Build();
            EditorSceneManager.OpenScene("Assets/LR2/Scenes/LR2_MainMenu.unity", OpenSceneMode.Single);
            EditorApplication.ExecuteMenuItem("Window/General/Game");

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            EditorApplication.isPlaying = true;
        }

        private static void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            SessionState.SetInt(KeyErrors, SessionState.GetInt(KeyErrors, 0) + 1);
            AppendNote($"{type}: {msg}");
        }

        private static void Tick()
        {
            if (!SessionState.GetBool(KeyActive, false) || !EditorApplication.isPlaying) return;

            int stage = SessionState.GetInt(KeyStage, 0);
            float nextAt = SessionState.GetFloat(KeyAt, 0f);
            float now = (float)EditorApplication.timeSinceStartup;
            float deadline = SessionState.GetFloat(KeyDeadline, 0f);
            if (deadline > 0f && now > deadline)
            {
                AppendNote($"Verify timed out at stage {stage}");
                FinishVerify();
                return;
            }
            if (nextAt <= 0f)
            {
                SessionState.SetFloat(KeyAt, now + 0.8f);
                return;
            }
            if (now < nextAt) return;

            switch (stage)
            {
                case 0:
                {
                    var mm = Object.FindAnyObjectByType<MainMenuController>();
                    if (mm == null)
                    {
                        SessionState.SetFloat(KeyAt, now + 0.5f);
                        return;
                    }
                    mm.SelectMap(0); // Saltworks
                    bool hasSprite = mm.mapPreviewImage != null && mm.mapPreviewImage.sprite != null;
                    AppendNote($"MainMenu previewImage.sprite={(hasSprite ? mm.mapPreviewImage.sprite.name : "NULL")}");
                    ScreenCapture.CaptureScreenshot(Path.Combine(VerifyDir, "01_menu_main.png"));
                    SessionState.SetInt(KeyStage, 1);
                    SessionState.SetFloat(KeyAt, now + 0.7f);
                    break;
                }
                case 1:
                {
                    var mm = Object.FindAnyObjectByType<MainMenuController>();
                    if (mm != null && mm.modeRulesButton != null)
                    {
                        mm.modeRulesButton.onClick.Invoke();
                        if (mm.modeRulesTdmTab != null) mm.modeRulesTdmTab.onClick.Invoke();
                        if (mm.rulesScoreLimitSlider != null) mm.rulesScoreLimitSlider.value = 50f;
                        if (mm.rulesCountdownSlider != null) mm.rulesCountdownSlider.value = 1f;
                        AppendNote($"ModeRules TDM custom={LR2Session.HasCustomModeRules(GameModeType.TeamDeathmatch)} score={(mm.rulesScoreLimitSlider != null ? mm.rulesScoreLimitSlider.value : -1)} cd={(mm.rulesCountdownSlider != null ? mm.rulesCountdownSlider.value : -1)}");
                    }
                    ScreenCapture.CaptureScreenshot(Path.Combine(VerifyDir, "02_menu_mode_rules.png"));
                    SessionState.SetInt(KeyStage, 2);
                    SessionState.SetFloat(KeyAt, now + 0.7f);
                    break;
                }
                case 2:
                {
                    var mm = Object.FindAnyObjectByType<MainMenuController>();
                    if (mm != null)
                    {
                        if (mm.modeRulesBackButton != null) mm.modeRulesBackButton.onClick.Invoke();
                        if (mm.settingsButton != null) mm.settingsButton.onClick.Invoke();
                        var sp = Object.FindAnyObjectByType<SettingsPanel>();
                        bool fovOk = sp != null && sp.fieldOfView != null && sp.fieldOfView.minValue <= 65.1f && sp.fieldOfView.maxValue >= 119.9f;
                        if (fovOk)
                        {
                            sp.fieldOfView.value = 104f;
                            LR2Settings.Save();
                            float persisted = PlayerPrefs.GetFloat("LR2.FOV", -1f);
                            AppendNote($"SettingsPanel uiVolume={(sp.uiVolume != null ? "OK" : "MISSING")}, fovRange=({sp.fieldOfView.minValue:0}..{sp.fieldOfView.maxValue:0}), persistedFOV={persisted:0}");
                            sp.fieldOfView.value = _savedFov > 0f ? _savedFov : 90f;
                            LR2Settings.Save();
                        }
                        else
                        {
                            AppendNote($"SettingsPanel uiVolume={(sp != null && sp.uiVolume != null ? "OK" : "MISSING")}, fovSlider={(sp != null && sp.fieldOfView != null ? $"{sp.fieldOfView.minValue:0}..{sp.fieldOfView.maxValue:0}" : "MISSING")}");
                        }
                    }
                    ScreenCapture.CaptureScreenshot(Path.Combine(VerifyDir, "03_menu_settings.png"));
                    SessionState.SetInt(KeyStage, 3);
                    SessionState.SetFloat(KeyAt, now + 0.7f);
                    break;
                }
                case 3:
                {
                    var mm = Object.FindAnyObjectByType<MainMenuController>();
                    if (mm != null)
                    {
                        if (mm.settingsBackButton != null) mm.settingsBackButton.onClick.Invoke();
                        mm.Play(mm.tdmSettings);
                        AppendNote("Clicked Play(tdmSettings) -> deploying to LR2_Saltworks");
                    }
                    SessionState.SetInt(KeyStage, 4);
                    SessionState.SetFloat(KeyAt, now + 2.2f);
                    break;
                }
                case 4:
                {
                    var match = MatchManager.Instance;
                    var player = Combatant.FindLocalPlayer();
                    if (match == null || player == null || match.State != MatchState.Playing)
                    {
                        SessionState.SetFloat(KeyAt, now + 0.35f);
                        return;
                    }
                    AppendNote($"Saltworks Match State={match.State} ScoreLimit={match.ScoreLimit} (expected 50), GunBloodFX={(GunBloodFX.Instance != null ? "ACTIVE" : "NULL")}");

                    var soldier = player.GetComponent<SoldierController>();
                    if (soldier != null && soldier.ActiveFirearm != null)
                    {
                        soldier.ActiveFirearm.OnFirePressed();
                        AppendNote($"Firing {soldier.ActiveFirearm.DisplayName} to consume round...");
                    }

                    SessionState.SetInt(KeyStage, 5);
                    SessionState.SetFloat(KeyAt, now + 0.25f);
                    break;
                }
                case 5:
                {
                    var player = Combatant.FindLocalPlayer();
                    var soldier = player != null ? player.GetComponent<SoldierController>() : null;
                    if (soldier != null && soldier.ActiveFirearm != null)
                    {
                        soldier.ActiveFirearm.OnFireReleased();
                        soldier.RequestReload();
                        AppendNote($"Reloading {soldier.ActiveFirearm.DisplayName}: IsReloading={soldier.ActiveFirearm.IsReloading}");
                    }

                    if (player != null && player.Health != null)
                    {
                        player.Health.CancelSpawnProtection();
                        var info = new DamageInfo
                        {
                            amount = 38f,
                            type = DamageType.Bullet,
                            attacker = null,
                            point = player.ChestPosition,
                            normal = Vector3.right,
                            direction = -Vector3.right,
                            force = 2f,
                        };
                        float dmg = player.Health.ApplyDamage(ref info);
                        AppendNote($"Applied damage={dmg:0} -> HP={player.Health.Current:0}, GunDrops={(GunBloodFX.Instance != null ? GunBloodFX.Instance.ActiveGunDropletCount : -1)}");
                    }

                    Combatant enemy = null;
                    foreach (var c in Combatant.All)
                        if (c != null && c != player) { enemy = c; break; }
                    if (player != null && enemy != null)
                    {
                        GameEvents.RaiseHit(new HitEvent
                        {
                            attacker = player,
                            victim = enemy,
                            damage = 100f,
                            zone = HitZone.Head,
                            type = DamageType.Bullet,
                            killed = true,
                            point = enemy.ChestPosition,
                            direction = Vector3.forward,
                        });
                        GameEvents.RaiseKilled(new KillEvent
                        {
                            killer = player,
                            victim = enemy,
                            weaponName = "AK-12",
                            type = DamageType.Bullet,
                            headshot = true,
                            distance = 24f,
                            victimPosition = enemy.Position,
                            direction = Vector3.forward,
                        });
                        AppendNote($"Triggered BF6 HitFeedback+KillFeed: {player.DisplayName} -> {enemy.DisplayName} (headshot kill)");
                    }

                    var hud = Object.FindAnyObjectByType<HUDController>();
                    if (hud != null)
                    {
                        string dmgSprite = hud.damageIndicatorTemplate != null && hud.damageIndicatorTemplate.sprite != null ? hud.damageIndicatorTemplate.sprite.name : "NULL";
                        bool hfActive = hud.GetComponentInParent<Canvas>()?.GetComponentInChildren<HitFeedbackHUD>(true) != null;
                        AppendNote($"HUD DamageArc.sprite={dmgSprite}, HitFeedbackHUD={(hfActive ? "OK" : "MISSING")}");
                    }

                    SessionState.SetInt(KeyStage, 6);
                    SessionState.SetFloat(KeyAt, now + 0.08f);
                    break;
                }
                case 6:
                {
                    ScreenCapture.CaptureScreenshot(Path.Combine(VerifyDir, "04_gameplay_blood_hud.png"));
                    CaptureCamFallback(Path.Combine(VerifyDir, "05_gameplay_cam.png"));
                    var probeGo = new GameObject("[LR2 VerifyFlickerProbe]");
                    Object.DontDestroyOnLoad(probeGo);
                    probeGo.AddComponent<VerifyFlickerProbe>();
                    SessionState.SetInt(KeyStage, 7);
                    SessionState.SetFloat(KeyAt, now + 0.4f);
                    break;
                }
                case 7:
                {
                    if (!_flickerDone)
                    {
                        SessionState.SetFloat(KeyAt, now + 0.25f);
                        return;
                    }
                    FinishVerify();
                    break;
                }
                default:
                {
                    FinishVerify();
                    break;
                }
            }
        }

        private static void AppendNote(string line)
        {
            string cur = SessionState.GetString(KeyLog, "");
            SessionState.SetString(KeyLog, cur + $"[{System.DateTime.Now:HH:mm:ss}] {line}\n");
            Debug.Log("[LR2 GeminiVerify] " + line);
        }

        private static void CaptureCamFallback(string path)
        {
            var player = Combatant.FindLocalPlayer();
            var soldier = player != null ? player.GetComponent<SoldierController>() : null;
            var cam = soldier != null && soldier.ViewCamera != null && soldier.ViewCamera.isActiveAndEnabled ? soldier.ViewCamera : Camera.main;
            if (cam == null) return;
            var rt = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var hd = cam.GetComponent<HDAdditionalCameraData>();
            var prevAa = hd != null ? hd.antialiasing : HDAdditionalCameraData.AntialiasingMode.None;
            try
            {
                if (hd != null) hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
                var req = new RenderPipeline.StandardRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(cam, req))
                {
                    RenderPipeline.SubmitRenderRequest(cam, req);
                    RenderPipeline.SubmitRenderRequest(cam, req);
                }
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.Destroy(tex);
            }
            finally
            {
                if (hd != null) hd.antialiasing = prevAa;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static void FinishVerify()
        {
            if (_savedBodycamMotion >= 0f)
            {
                LR2Settings.BodycamMotion = _savedBodycamMotion;
                LR2Settings.Save();
            }
            LR2Session.ResetModeRules(GameModeType.TeamDeathmatch);
            int errs = SessionState.GetInt(KeyErrors, 0);
            string fullLog = SessionState.GetString(KeyLog, "") + $"Errors={errs}\n";
            File.WriteAllText(Path.Combine(VerifyDir, "report.txt"), fullLog);

            SessionState.SetBool(KeyActive, false);
            SessionState.SetBool("LR2ReadmeMedia.LobbyOnly", false);
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= OnLog;

            bool autoExit = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-lr2AutoExit") >= 0;
            if (autoExit)
            {
                if (File.Exists(LR2MapBuilder.MenuScenePath))
                {
                    string setupPath = Path.Combine(Directory.GetCurrentDirectory(), "Library", "LastSceneManagerSetup.txt");
                    File.WriteAllText(setupPath,
                        "sceneSetups:\n- path: " + LR2MapBuilder.MenuScenePath.Replace('\\', '/') + "\n  isLoaded: 1\n  isActive: 1\n  isSubScene: 0\n");
                }
                EditorApplication.playModeStateChanged += state =>
                {
                    if (state == PlayModeStateChange.EnteredEditMode)
                        EditorApplication.Exit(errs == 0 ? 0 : 2);
                };
            }
            EditorApplication.isPlaying = false;
        }

        private class VerifyFlickerProbe : MonoBehaviour
        {
            private System.Collections.IEnumerator Start()
            {
                try
                {
                    LR2Settings.BodycamMotion = 0f;
                    string[] scenes = LR2Session.MapScenes;
                    for (int i = 0; i < scenes.Length; i++)
                    {
                        string sceneName = scenes[i];
                        if (i > 0)
                        {
                            var op = SceneManager.LoadSceneAsync(sceneName);
                            while (op != null && !op.isDone) yield return null;
                            yield return null;
                        }

                        float waitStart = Time.realtimeSinceStartup;
                        while (Time.realtimeSinceStartup - waitStart < 5f)
                        {
                            var mm = MatchManager.Instance;
                            if (mm != null && mm.State == MatchState.Playing && Combatant.FindLocalPlayer() != null)
                                break;
                            yield return null;
                        }

                        var player = Combatant.FindLocalPlayer();
                        var soldier = player != null ? player.GetComponent<SoldierController>() : null;
                        var cam = soldier != null && soldier.ViewCamera != null && soldier.ViewCamera.isActiveAndEnabled
                            ? soldier.ViewCamera
                            : Camera.main;

                        foreach (var bot in Object.FindObjectsByType<LR2.AI.BotController>())
                            if (bot != null) bot.enabled = false;
                        if (soldier != null)
                        {
                            var pis = soldier.GetComponent<LR2.Characters.PlayerInputSource>();
                            if (pis != null) pis.enabled = false;
                            if (soldier.Movement != null) soldier.Movement.StopSprint();
                            if (cam != null)
                            {
                                for (int turn = 0; turn < 3 && Physics.Raycast(cam.transform.position, cam.transform.forward, 2.5f); turn++)
                                    soldier.transform.Rotate(0f, 110f, 0f);
                            }
                        }

                        // Let HDRP progressive exposure and TAA settle at the stationary viewpoint.
                        yield return new WaitForSecondsRealtime(1.25f);

                        Animator[] anims = soldier != null ? soldier.GetComponentsInChildren<Animator>() : System.Array.Empty<Animator>();
                        foreach (var a in anims) if (a != null) a.speed = 0f;
                        if (soldier != null && soldier.CameraController != null) soldier.CameraController.enabled = false;

                        yield return new WaitForEndOfFrame();
                        Vector3 posA = cam != null ? cam.transform.position : Vector3.zero;
                        Quaternion rotA = cam != null ? cam.transform.rotation : Quaternion.identity;
                        Texture2D texA = ScreenCapture.CaptureScreenshotAsTexture();

                        for (int f = 0; f < 6; f++) yield return null;

                        yield return new WaitForEndOfFrame();
                        Vector3 posB = cam != null ? cam.transform.position : Vector3.zero;
                        Quaternion rotB = cam != null ? cam.transform.rotation : Quaternion.identity;
                        Texture2D texB = ScreenCapture.CaptureScreenshotAsTexture();

                        if (soldier != null && soldier.CameraController != null) soldier.CameraController.enabled = true;
                        foreach (var a in anims) if (a != null) a.speed = 1f;

                        SaveAndCompareFrames(sceneName, texA, texB, Vector3.Distance(posA, posB), Quaternion.Angle(rotA, rotB));
                    }
                }
                finally
                {
                    if (_savedBodycamMotion >= 0f) LR2Settings.BodycamMotion = _savedBodycamMotion;
                    _flickerDone = true;
                    Destroy(gameObject);
                }
            }

            private static void SaveAndCompareFrames(string sceneName, Texture2D texA, Texture2D texB, float posDelta, float rotDelta)
            {
                if (texA == null || texB == null)
                {
                    AppendNote($"Flicker[{sceneName}]: capture returned null texture");
                    if (texA != null) Object.Destroy(texA);
                    if (texB != null) Object.Destroy(texB);
                    return;
                }

                File.WriteAllBytes(Path.Combine(VerifyDir, $"06_{sceneName}_flickerA.png"), texA.EncodeToPNG());
                File.WriteAllBytes(Path.Combine(VerifyDir, $"06_{sceneName}_flickerB.png"), texB.EncodeToPNG());

                Color32[] a = texA.GetPixels32();
                Color32[] b = texB.GetPixels32();
                int count = Mathf.Min(a.Length, b.Length);
                double sumDiff = 0;
                int changedOver12 = 0;
                int maxDiff = 0;
                for (int i = 0; i < count; i++)
                {
                    int dr = Mathf.Abs(a[i].r - b[i].r);
                    int dg = Mathf.Abs(a[i].g - b[i].g);
                    int db = Mathf.Abs(a[i].b - b[i].b);
                    int d = Mathf.Max(dr, Mathf.Max(dg, db));
                    sumDiff += (dr + dg + db) / 3.0;
                    if (d > 12) changedOver12++;
                    if (d > maxDiff) maxDiff = d;
                }

                double meanRgb = count > 0 ? sumDiff / count : 0.0;
                double pct12 = count > 0 ? 100.0 * changedOver12 / count : 0.0;
                AppendNote($"Flicker[{sceneName}] camDelta=({posDelta:0.0000}m,{rotDelta:0.000}deg) meanDiff={meanRgb:0.00}/255 pct>12={pct12:0.00}% maxDiff={maxDiff}");

                Object.Destroy(texA);
                Object.Destroy(texB);
            }
        }
    }

    /// <summary>
    /// Captures fresh 16:9 README hero/gallery screenshots (Main Menu Hero, Half &amp; Half CTF Deploy screen,
    /// MK18 iron-sight ADS, barrel overheat red glow + smoke, player green vs enemy red lasers/tracers/uniforms,
    /// plus sequential combat frames for Docs/Media/gameplay_combat.gif).
    /// </summary>
    [InitializeOnLoad]
    public static class LR2ReadmeMediaRunner
    {
        private const string KeyActive = "LR2ReadmeMedia.Active";
        private static string MediaDir
        {
            get
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "-lr2MediaDir" && !string.IsNullOrWhiteSpace(args[i + 1])) return args[i + 1];
                return "Docs/Media";
            }
        }
        private static string GifDir
        {
            get
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "-lr2GifDir" && !string.IsNullOrWhiteSpace(args[i + 1])) return args[i + 1];
                return "Temp/GifFrames";
            }
        }
        private static int _savedBotTeam = -1;
        private static int _savedWeapon = -1;
        private static int _savedMap = -1;
        private static float _savedBodycam = -1f;

        static LR2ReadmeMediaRunner()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            if (EditorApplication.isPlaying && SessionState.GetBool(KeyActive, false))
                EditorApplication.delayCall += EnsureProbe;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(KeyActive, false))
                EnsureProbe();
        }

        [MenuItem("LR2/Media/Capture README Gallery", priority = 223)]
        public static void CaptureReadmeMediaInteractive() => Begin(autoExit: false, lobbyOnly: false);

        public static void CaptureReadmeMediaBatch() => Begin(autoExit: true, lobbyOnly: false);

        public static void CaptureLobbyTabsBatch() => Begin(autoExit: true, lobbyOnly: true);

        private static void Begin(bool autoExit, bool lobbyOnly = false)
        {
            if (EditorApplication.isPlaying) return;
            AssetDatabase.DisallowAutoRefresh();
            Directory.CreateDirectory(MediaDir);
            if (!lobbyOnly)
            {
                Directory.CreateDirectory(GifDir);
                foreach (string old in Directory.GetFiles(GifDir, "*.png")) File.Delete(old);
            }

            _savedBotTeam = LR2Settings.BotTeamMode;
            _savedWeapon = LR2Session.SelectedPrimaryWeapon;
            _savedMap = LR2Session.SelectedMapIndex;
            _savedBodycam = LR2Settings.BodycamMotion;

            SessionState.SetBool(KeyActive, true);
            SessionState.SetBool("LR2ReadmeMedia.AutoExit", autoExit);
            SessionState.SetBool("LR2ReadmeMedia.LobbyOnly", lobbyOnly);
            EditorSceneManager.playModeStartScene = null;
            EditorSceneManager.OpenScene("Assets/LR2/Scenes/LR2_MainMenu.unity", OpenSceneMode.Single);
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            EditorApplication.isPlaying = true;
            EditorApplication.delayCall += EnsureProbe;
        }

        private static void EnsureProbe()
        {
            if (!EditorApplication.isPlaying || Object.FindAnyObjectByType<ReadmeMediaProbe>() != null) return;
            var go = new GameObject("[LR2 ReadmeMediaProbe]");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<ReadmeMediaProbe>();
        }

        private static void Finish(int errors)
        {
            if (_savedBotTeam >= 0) LR2Settings.BotTeamMode = _savedBotTeam;
            if (_savedWeapon >= 0) LR2Session.SelectedPrimaryWeapon = _savedWeapon;
            if (_savedMap >= 0) LR2Session.SelectedMapIndex = _savedMap;
            if (_savedBodycam >= 0f) LR2Settings.BodycamMotion = _savedBodycam;
            LR2Session.ResetModeRules(GameModeType.CaptureTheFlag);
            LR2Settings.Save();

            SessionState.SetBool(KeyActive, false);
            SessionState.SetBool("LR2ReadmeMedia.LobbyOnly", false);
            bool autoExit = SessionState.GetBool("LR2ReadmeMedia.AutoExit", false)
                || System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-lr2AutoExit") >= 0;
            if (autoExit)
            {
                if (File.Exists(LR2MapBuilder.MenuScenePath))
                {
                    string setupPath = Path.Combine(Directory.GetCurrentDirectory(), "Library", "LastSceneManagerSetup.txt");
                    File.WriteAllText(setupPath,
                        "sceneSetups:\n- path: " + LR2MapBuilder.MenuScenePath.Replace('\\', '/') + "\n  isLoaded: 1\n  isActive: 1\n  isSubScene: 0\n");
                }
                EditorApplication.playModeStateChanged += state =>
                {
                    if (state == PlayModeStateChange.EnteredEditMode)
                        EditorApplication.Exit(errors > 0 ? 1 : 0);
                };
            }
            EditorApplication.isPlaying = false;
        }

        private class ReadmeMediaProbe : MonoBehaviour, ISoldierInputSource
        {
            private SoldierInput _cmd = SoldierInput.Empty;
            private int _errors;

            public void GetInput(SoldierController soldier, ref SoldierInput input)
            {
                input = _cmd;
                _cmd.reload = false;
                _cmd.prone = false;
                _cmd.crouch = false;
                _cmd.jump = false;
                _cmd.weaponSlot = -1;
            }

            private void OnLog(string msg, string stack, LogType type)
            {
                if ((type == LogType.Error || type == LogType.Exception) && !msg.Contains("PlasticSCM"))
                    _errors++;
            }

            private System.Collections.IEnumerator Start()
            {
                Application.logMessageReceived += OnLog;
                try
                {
                    // 1. Main Menu Hero screenshot
                    float t0 = Time.realtimeSinceStartup;
                    MainMenuController mm = null;
                    while (mm == null && Time.realtimeSinceStartup - t0 < 8f)
                    {
                        mm = Object.FindAnyObjectByType<MainMenuController>();
                        yield return null;
                    }
                    if (mm != null)
                    {
                        LR2Session.SelectedPrimaryWeapon = 0; // MK18
                        mm.SelectMap(0); // Saltworks
                        LR2Settings.LaserEnabled = true;
                        LR2Settings.BodycamMotion = 0f;
                        bool lobbyOnly = SessionState.GetBool("LR2ReadmeMedia.LobbyOnly", false)
                            || System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-lr2LobbyOnly") >= 0;

                        if (!lobbyOnly)
                        {
                            // B31: Capture the minimalistic loading / starter title screen cleanly first, then dismiss it before main_menu_hero.png
                            LR2LoadingIntro.SetCapturePreviewTitleScreen(true);
                            yield return new WaitForSecondsRealtime(0.25f);
                            yield return SaveScreenPng(Path.Combine(MediaDir, "title_screen.png"));
                            yield return SaveScreenPng(Path.Combine(MediaDir, "loading_screen.png"));
                            LR2LoadingIntro.SetCapturePreviewTitleScreen(false);
                        }

                        // Wait for the boot intro to finish fading: the 11-18 10:12 hero caught "LEADRUSH II / DEPLOYMENT READY"
                        // at half opacity over the menu (Claude 10:30). Then let auto-exposure settle.
                        float tIntro = Time.realtimeSinceStartup;
                        while ((LR2LoadingIntro.IsCoveringScreen || LR2LoadingIntro.IsShowingTitleScreen) && Time.realtimeSinceStartup - tIntro < 20f)
                            yield return null;
                        yield return new WaitForSecondsRealtime(1.6f);
                        if (!lobbyOnly)
                        {
                            yield return SaveScreenPng(Path.Combine(MediaDir, "main_menu_hero.png"));

                            // 1b. Mode Rules panel
                            if (mm.modeRulesButton != null)
                            {
                                mm.modeRulesButton.onClick.Invoke();
                                yield return new WaitForSecondsRealtime(0.4f);
                                yield return SaveScreenPng(Path.Combine(MediaDir, "mode_rules.png"));
                                if (mm.modeRulesBackButton != null) mm.modeRulesBackButton.onClick.Invoke();
                                yield return new WaitForSecondsRealtime(0.25f);
                            }

                            // 1c. Settings Graphics & Controls tabs
                            if (mm.settingsButton != null)
                            {
                                mm.settingsButton.onClick.Invoke();
                                yield return new WaitForSecondsRealtime(0.35f);
                                var spPanel = mm.settingsPanel != null ? mm.settingsPanel.GetComponentInChildren<SettingsPanel>(true) : null;
                                if (spPanel != null) spPanel.SelectTab(1);
                                yield return new WaitForSecondsRealtime(0.25f);
                                yield return SaveScreenPng(Path.Combine(MediaDir, "settings_graphics.png"));
                                yield return SaveScreenPng(Path.Combine(MediaDir, "settings_graphics_v2.png"));
                                if (spPanel != null)
                                {
                                    spPanel.SelectTab(2);
                                    yield return new WaitForSecondsRealtime(0.25f);
                                    yield return SaveScreenPng(Path.Combine(MediaDir, "settings_controls.png"));
                                    spPanel.SelectTab(0);
                                }
                                if (mm.settingsBackButton != null) mm.settingsBackButton.onClick.Invoke();
                                yield return new WaitForSecondsRealtime(0.25f);
                            }
                        }

                        // 2. Dual-Tab Combat Lobby (Skirmish + Experiments)
                        if (mm.duelButton != null)
                        {
                            mm.duelButton.onClick.Invoke();
                            yield return new WaitForSecondsRealtime(0.65f);
                            var lobby = Object.FindAnyObjectByType<LR2.UI.SkirmishLobby>();
                            if (lobby != null)
                            {
                                lobby.SwitchTab(false);
                                yield return new WaitForSecondsRealtime(0.45f);
                                yield return SaveScreenPng(Path.Combine(MediaDir, "lobby_skirmish_tab.png"));
                                yield return SaveScreenPng(Path.Combine(MediaDir, "menu_deploy.png"));
                                lobby.SwitchTab(true);
                                yield return new WaitForSecondsRealtime(0.55f);
                                yield return SaveScreenPng(Path.Combine(MediaDir, "lobby_experiments_tab.png"));
                                lobby.SwitchTab(false);
                                yield return new WaitForSecondsRealtime(0.25f);
                            }
                            if (lobbyOnly)
                            {
                                Finish(_errors);
                                yield break;
                            }
                            // 3. Deploy directly into Saltworks in CTF Half & Half
                            SkirmishSetup.ApplyPreset("2 TEAMS");
                            SkirmishSetup.Mode = GameModeType.CaptureTheFlag;
                            SkirmishSetup.PlayerSpectates = false;
                            SkirmishSetup.Active = true;
                            LR2Session.PrepareConfiguredMatch(mm.ctfSettings);
                            SceneManager.LoadScene("LR2_Saltworks");
                        }
                        else if (lobbyOnly)
                        {
                            Finish(_errors);
                            yield break;
                        }
                    }

                    // Wait for Saltworks active play
                    var soldier = default(SoldierController);
                    yield return WaitForPlayScene("LR2_Saltworks", s => soldier = s);
                    if (soldier == null)
                    {
                        Debug.LogError("[LR2 ReadmeMedia] Failed to enter Saltworks play state.");
                        yield break;
                    }

                    var local = Combatant.FindLocalPlayer();
                    GunBloodFX.EnsureInstance();

                    // Pause all bots briefly so staged weapon/HUD/armour captures stay clean
                    var botControllers = Object.FindObjectsByType<LR2.AI.BotController>();
                    foreach (var bc in botControllers)
                        if (bc != null) bc.enabled = false;

                    // Pick open lane on Saltworks and stage an Allied SWAT (green laser) + Hostile OpFor (red laser, desert/crimson uniform)
                    Combatant ally = null, enemy = null;
                    foreach (var c in Combatant.All)
                    {
                        if (c == null || c == local) continue;
                        if (!TeamUtil.AreHostile(local, c) && ally == null) ally = c;
                        else if (TeamUtil.AreHostile(local, c) && enemy == null) enemy = c;
                    }

                    Vector3 origin = GroundAt(new Vector3(-6.8f, 3f, 4.2f));
                    Vector3 aimTarget = GroundAt(new Vector3(1.5f, 3f, 8.8f)) + Vector3.up * 1.38f;
                    soldier.Movement.Teleport(origin);
                    AimAt(origin, aimTarget, -1.5f, out float yaw);
                    yield return null;

                    if (ally != null)
                    {
                        var ac = ally.GetComponent<SoldierController>();
                        Vector3 ap = GroundAt(new Vector3(-3.2f, 3f, 5.2f));
                        ac.Movement.Teleport(ap);
                        ac.transform.LookAt(new Vector3(aimTarget.x, ap.y, aimTarget.z));
                        ally.GetComponent<SoldierVisuals>()?.ApplyMaterialsNow(true);
                    }
                    Vector3 enemyLanePos = GroundAt(new Vector3(-1.0f, 3f, 8.8f));
                    if (enemy != null)
                    {
                        var ec = enemy.GetComponent<SoldierController>();
                        ec.Movement.Teleport(enemyLanePos);
                        ec.transform.LookAt(new Vector3(origin.x, enemyLanePos.y, origin.z));
                        enemy.GetComponent<SoldierVisuals>()?.ApplyMaterialsNow(false);
                    }

                    yield return new WaitForSecondsRealtime(0.75f);
                    yield return SaveScreenPng(Path.Combine(MediaDir, "lasers_green_vs_red.png"));
                    yield return SaveScreenPng(Path.Combine(MediaDir, "hud_armour.png"));

                    // ADS iron sight shot
                    _cmd.aim = true;
                    _cmd.aimYaw = yaw + 1.2f;
                    yield return new WaitForSecondsRealtime(0.45f);
                    yield return SaveScreenPng(Path.Combine(MediaDir, "mk18_ironsight_ads.png"));

                    // Close skull-mask OpFor ADS shot
                    if (enemy != null)
                    {
                        var ec = enemy.GetComponent<SoldierController>();
                        Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                        Vector3 closePos = GroundAt(origin + fwd * 3.2f);
                        ec.Movement.Teleport(closePos);
                        ec.transform.LookAt(new Vector3(origin.x, closePos.y, origin.z));
                        enemy.GetComponent<SoldierVisuals>()?.ApplyMaterialsNow(false);
                        AimAt(origin, closePos + Vector3.up * 1.48f, null, out _);
                        _cmd.aim = true;
                        yield return new WaitForSecondsRealtime(0.45f);
                        yield return SaveScreenPng(Path.Combine(MediaDir, "enemy_skullmask_ads.png"));
                        ec.Movement.Teleport(enemyLanePos);
                        ec.transform.LookAt(new Vector3(origin.x, enemyLanePos.y, origin.z));
                        AimAt(origin, aimTarget, -1.5f, out yaw);
                    }

                    // Sustained fire to build barrel red glow + rising barrel heat smoke + green tracers
                    _cmd.aim = false;
                    _cmd.fire = true;
                    for (int shotFrame = 0; shotFrame < 50; shotFrame++)
                    {
                        if (soldier.ActiveFirearm != null && soldier.ActiveFirearm.AmmoInMagazine < 8)
                            soldier.ActiveFirearm.RefillAmmo();
                        yield return new WaitForSecondsRealtime(0.045f);
                    }
                    yield return SaveScreenPng(Path.Combine(MediaDir, "mk18_overheat_smoke.png"));

                    // Combat HUD + Armour damaged + GunBloodFX droplets shot
                    if (soldier.ActiveFirearm != null) soldier.ActiveFirearm.RefillAmmo();
                    var localHealth = local != null ? local.GetComponent<Health>() : null;
                    if (localHealth != null)
                    {
                        Vector3 hitFrom = enemy != null ? enemy.transform.position : origin + Vector3.forward * 6f;
                        var hitDmg = new DamageInfo
                        {
                            amount = 28f,
                            type = DamageType.Bullet,
                            zone = HitZone.UpperTorso,
                            attacker = enemy,
                            victim = local,
                            point = origin + Vector3.up * 1.35f,
                            direction = (origin - hitFrom).normalized,
                            distance = 6f,
                            force = 12f
                        };
                        localHealth.ApplyDamage(ref hitDmg);
                    }
                    _cmd.aim = true;
                    _cmd.fire = true;
                    yield return new WaitForSecondsRealtime(0.12f);
                    yield return SaveScreenPng(Path.Combine(MediaDir, "combat_hud_blood.png"));

                    // Dynamic combat shot + sequential GIF frames (firing, overheat smoke, green vs red tracers)
                    foreach (var bc in botControllers)
                        if (bc != null) bc.enabled = true;
                    yield return new WaitForSecondsRealtime(0.15f);
                    yield return SaveScreenPng(Path.Combine(MediaDir, "gameplay_saltworks.png"));

                    for (int gf = 0; gf < 24; gf++)
                    {
                        _cmd.aim = gf >= 8 && gf < 18;
                        _cmd.fire = gf < 18;
                        _cmd.aimYaw = yaw + Mathf.Sin(gf * 0.28f) * 4.5f;
                        if (soldier.ActiveFirearm != null && soldier.ActiveFirearm.AmmoInMagazine < 6)
                            soldier.ActiveFirearm.RefillAmmo();
                        yield return new WaitForSecondsRealtime(0.055f);
                        yield return SaveScreenPng(Path.Combine(GifDir, $"frame_{gf:00}.png"));
                    }
                    _cmd.fire = false;
                    _cmd.aim = false;

                    // B41: Capture active CTF Flag Zone (feathered rings, capture arc, light column, sparks, operators contesting)
                    var flagZone = FlagZoneController.Instance;
                    if (flagZone != null)
                    {
                        Vector3 fPos = flagZone.ActiveFlagPosition;
                        if (ally != null)
                        {
                            var ac = ally.GetComponent<SoldierController>();
                            Vector3 ap = GroundAt(fPos + new Vector3(-1.8f, 0f, -1.2f));
                            ac.Movement.Teleport(ap);
                            ac.transform.LookAt(new Vector3(fPos.x + 2f, ap.y, fPos.z + 1.5f));
                            ally.GetComponent<SoldierVisuals>()?.ApplyMaterialsNow(true);
                        }
                        if (enemy != null)
                        {
                            var ec = enemy.GetComponent<SoldierController>();
                            Vector3 ep = GroundAt(fPos + new Vector3(2.4f, 0f, 1.6f));
                            ec.Movement.Teleport(ep);
                            ec.transform.LookAt(new Vector3(fPos.x - 1.5f, ep.y, fPos.z - 1f));
                            enemy.GetComponent<SoldierVisuals>()?.ApplyMaterialsNow(false);
                        }
                        PlaceOnClearVantage(
                            soldier,
                            fPos + Vector3.up * 1.15f,
                            new[] { 10.5f, 13.5f, 8.2f, 16.0f },
                            new[] { 30f, -30f, 145f, -145f, 85f, -85f, 0f, 180f },
                            fPos + new Vector3(-9f, 2f, -9f),
                            fPos + Vector3.up * 1.15f);
                        yield return new WaitForSecondsRealtime(0.75f);
                        yield return SaveScreenPng(Path.Combine(MediaDir, "ctf_flag_zone.png"));
                    }

                    // Additional Saltworks atmospheric shots (fog + fire barrel lighting)
                    yield return CaptureMapAtmosphericShots(
                        "LR2_Saltworks",
                        null,
                        "saltworks_fog_01.png",
                        "saltworks_fire_02.png",
                        new Vector3(-11f, 2f, -10f),
                        new Vector3(0f, 1.8f, 0f));

                    // AK-12 Circular PiP Scope ADS showcase (Slot 1 rifle on reload of Saltworks)
                    LR2Session.SelectedPrimaryWeapon = 1;
                    SceneManager.LoadScene("LR2_Saltworks");
                    yield return null;
                    SoldierController akSoldier = null;
                    yield return WaitForPlayScene("LR2_Saltworks", s => akSoldier = s);
                    if (akSoldier != null)
                    {
                        foreach (var bc in Object.FindObjectsByType<LR2.AI.BotController>())
                            if (bc != null) bc.enabled = false;
                        Vector3 pAk = GroundAt(new Vector3(-11.5f, 3f, 6.5f));
                        Vector3 lookAk = GroundAt(new Vector3(-1.5f, 3f, 9.5f)) + Vector3.up * 1.38f;
                        akSoldier.Movement.Teleport(pAk);
                        AimAt(pAk, lookAk, -1.5f, out _);
                        _cmd.aim = true;
                        yield return new WaitForSecondsRealtime(0.60f);
                        yield return SaveScreenPng(Path.Combine(MediaDir, "ak12_pip_scope_ads.png"));
                        _cmd.aim = false;
                    }
                    LR2Session.SelectedPrimaryWeapon = 0;

                    // Remaining 3 maps: 3 atmospheric shots per map (hero, fog/trench/lane, fire lighting)
                    yield return CaptureMapAtmosphericShots(
                        "LR2_Deadsector",
                        "gameplay_deadsector.png",
                        "deadsector_fog_01.png",
                        "deadsector_fire_02.png",
                        new Vector3(-10f, 2f, -11f),
                        new Vector3(0f, 1.7f, 0f));

                    yield return CaptureMapAtmosphericShots(
                        "LR2_Foundry",
                        "gameplay_foundry.png",
                        "foundry_trench_01.png",
                        "foundry_fire_02.png",
                        new Vector3(-12f, 2f, -9f),
                        new Vector3(0f, 1.8f, 0f));

                    yield return CaptureMapAtmosphericShots(
                        "LR2_TestRange",
                        "gameplay_testrange.png",
                        "testrange_lane_01.png",
                        "testrange_fire_02.png",
                        new Vector3(-8f, 2f, -14f),
                        new Vector3(0f, 1.5f, 4f));
                }
                finally
                {
                    Application.logMessageReceived -= OnLog;
                    Finish(_errors);
                    Destroy(gameObject);
                }
            }

            private System.Collections.IEnumerator WaitForPlayScene(string sceneName, System.Action<SoldierController> onReady)
            {
                float waitPlay = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - waitPlay < 20f)
                {
                    var match = MatchManager.Instance;
                    if (SceneManager.GetActiveScene().name == sceneName
                        && match != null && match.State == MatchState.Playing
                        && Combatant.FindLocalPlayer() != null)
                        break;
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(1.1f);

                var local = Combatant.FindLocalPlayer();
                var soldier = local != null ? local.GetComponent<SoldierController>() : null;
                if (soldier != null)
                {
                    var pis = soldier.GetComponent<PlayerInputSource>();
                    if (pis != null) pis.enabled = false;
                    soldier.InputSource = this;
                }
                onReady?.Invoke(soldier);
            }

            private System.Collections.IEnumerator CaptureMapAtmosphericShots(
                string sceneName,
                string heroFile,
                string fogFile,
                string fireFile,
                Vector3 fallbackPos,
                Vector3 fallbackLook)
            {
                SoldierController soldier = null;
                if (SceneManager.GetActiveScene().name != sceneName)
                {
                    SceneManager.LoadScene(sceneName);
                    yield return null;
                }
                yield return WaitForPlayScene(sceneName, s => soldier = s);
                if (soldier == null)
                {
                    Debug.LogError($"[LR2 ReadmeMedia] Failed to enter {sceneName} play state.");
                    yield break;
                }

                GunBloodFX.EnsureInstance();
                var fires = Object.FindObjectsByType<FireHazard>();
                System.Array.Sort(fires, (a, b) => string.CompareOrdinal(a.name, b.name));

                Vector3 arenaCenter = Vector3.zero;
                var spawns = Object.FindObjectsByType<SpawnPoint>();
                if (spawns != null && spawns.Length > 0)
                {
                    Vector3 sum = Vector3.zero;
                    foreach (var sp in spawns) sum += sp.transform.position;
                    arenaCenter = sum / spawns.Length;
                }

                Vector3 centerLook = new Vector3(arenaCenter.x, GroundAt(arenaCenter).y + 1.55f, arenaCenter.z);

                if (!string.IsNullOrEmpty(heroFile))
                {
                    PlaceOnClearVantage(soldier, centerLook, new[] { 15f, 11f, 8f }, new[] { 35f, -35f, 140f, -140f, 75f, -75f }, fallbackPos, fallbackLook);
                    yield return new WaitForSecondsRealtime(0.55f);
                    yield return SaveScreenPng(Path.Combine(MediaDir, heroFile));
                }

                if (!string.IsNullOrEmpty(fogFile))
                {
                    Vector3 fogLook = fires.Length > 0
                        ? fires[0].transform.position + Vector3.up * 0.9f
                        : centerLook;
                    PlaceOnClearVantage(soldier, fogLook, new[] { 12f, 16f, 9f }, new[] { 165f, -155f, 110f, -100f, 50f, -50f }, fallbackPos, fallbackLook);
                    yield return new WaitForSecondsRealtime(0.55f);
                    yield return SaveScreenPng(Path.Combine(MediaDir, fogFile));
                }

                if (!string.IsNullOrEmpty(fireFile))
                {
                    FireHazard haz = fires.Length > 1 ? fires[1] : (fires.Length == 1 ? fires[0] : null);
                    Vector3 fireLook = haz != null ? haz.transform.position + Vector3.up * 0.65f : centerLook;
                    PlaceOnClearVantage(soldier, fireLook, new[] { 5.8f, 7.2f, 4.6f, 9.0f }, new[] { 20f, -20f, 65f, -65f, 115f, -115f, 160f }, fallbackPos, fallbackLook);
                    yield return new WaitForSecondsRealtime(0.55f);
                    yield return SaveScreenPng(Path.Combine(MediaDir, fireFile));
                }
            }

            private void PlaceOnClearVantage(
                SoldierController soldier,
                Vector3 focusTarget,
                float[] distances,
                float[] anglesDeg,
                Vector3 fallbackPos,
                Vector3 fallbackLook)
            {
                Vector3 chosenPos = GroundAt(fallbackPos);
                bool found = false;

                foreach (float dist in distances)
                {
                    foreach (float ang in anglesDeg)
                    {
                        Vector3 probe = focusTarget + Quaternion.Euler(0f, ang, 0f) * Vector3.forward * dist;
                        Vector3 grounded = GroundAt(probe);
                        if (Mathf.Abs(grounded.y - probe.y) > 4.5f) continue;

                        if (Physics.CheckCapsule(grounded + Vector3.up * 0.45f, grounded + Vector3.up * 1.65f, 0.42f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                            continue;

                        Vector3 eye = grounded + Vector3.up * 1.55f;
                        Vector3 dir = focusTarget - eye;
                        float len = dir.magnitude;
                        if (len < 2.2f) continue;
                        if (Physics.SphereCast(eye, 0.25f, dir / len, out _, Mathf.Max(0.5f, len - 0.8f), LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                            continue;

                        chosenPos = grounded;
                        found = true;
                        break;
                    }
                    if (found) break;
                }

                soldier.Movement.Teleport(chosenPos);
                AimAt(chosenPos, found ? focusTarget : fallbackLook, null, out _);
                _cmd.aim = false;
                _cmd.fire = false;
            }

            private void AimAt(Vector3 soldierPos, Vector3 target, float? pitchOverride, out float yaw)
            {
                Vector3 d = target - (soldierPos + Vector3.up * 1.55f);
                float horiz = Mathf.Max(0.05f, Mathf.Sqrt(d.x * d.x + d.z * d.z));
                yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                float pitch = pitchOverride ?? Mathf.Clamp(-Mathf.Atan2(d.y, horiz) * Mathf.Rad2Deg, -35f, 35f);
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = yaw, aimPitch = pitch };
            }

            private static Vector3 GroundAt(Vector3 probe)
            {
                if (Physics.Raycast(probe + Vector3.up * 6f, Vector3.down, out var hit, 14f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                    return hit.point + Vector3.up * 0.05f;
                return probe;
            }

            private static System.Collections.IEnumerator SaveScreenPng(string path)
            {
                yield return new WaitForEndOfFrame();
                Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
                if (tex != null)
                {
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                    Object.Destroy(tex);
                }
            }
        }
    }
}
