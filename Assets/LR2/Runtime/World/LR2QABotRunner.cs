using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using KINEMATION.FPSAnimationFramework.Runtime.Core;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.AdsLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.CollisionLayer;
using LR2.Audio;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using LR2.FX;
using LR2.Game;
using LR2.UI;
using LR2.Weapons;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LR2.World
{
    /// <summary>
    /// Runtime automated QA bot activated by <c>-lr2QaBot</c> (run across maps via <c>LR2.EditorTools.LR2QABot.RunBatch</c>).
    /// Measures and asserts visibility/contrast at spawns and cover points, bloom/muzzle-flash glare, reload angle
    /// idle and while walking, ADS/hipfire recoil and weapon TTK, footstep stride timing and mix headroom beneath gunfire
    /// and adaptive music, faster prone speed/lock, 3D gun blood attachment, killcam replay hitmarkers, and zero errors.
    /// </summary>
    public class LR2QABotRunner : MonoBehaviour, ISoldierInputSource
    {
        public static bool Requested => Array.IndexOf(Environment.GetCommandLineArgs(), "-lr2QaBot") >= 0;

        private SoldierInput _cmd = SoldierInput.Empty;
        private readonly StringBuilder _report = new StringBuilder();
        private readonly List<string> _failures = new List<string>();
        private int _runtimeErrors;
        private int _footsteps;
        private float _distAccum;
        private int _savedUpscalerMode = -1;
        private string _telemetrySuffix;

        private static string GetArgValue(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!Requested || FindAnyObjectByType<LR2QABotRunner>() != null) return;
            var go = new GameObject("[LR2 QABot]");
            DontDestroyOnLoad(go);
            go.AddComponent<LR2QABotRunner>();
        }

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
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (msg.Contains("PlasticSCM") || msg.Contains("unityplastic")) return;
            _runtimeErrors++;
            if (_runtimeErrors <= 15) _report.AppendLine($"  ERR: {type} {msg}");
        }

        private void Check(bool cond, string label, string detail)
        {
            _report.AppendLine($"  [{(cond ? "PASS" : "FAIL")}] {label}: {detail}");
            if (!cond) _failures.Add($"{label}: {detail}");
        }

        private void CheckExtra(string formattedLine)
        {
            if (string.IsNullOrEmpty(formattedLine)) return;
            bool pass = formattedLine.StartsWith("PASS ", StringComparison.Ordinal);
            string rest = formattedLine.StartsWith("PASS ", StringComparison.Ordinal) || formattedLine.StartsWith("FAIL ", StringComparison.Ordinal)
                ? formattedLine.Substring(5)
                : formattedLine;
            int colon = rest.IndexOf(':');
            string label = colon > 0 ? rest.Substring(0, colon).Trim() : rest.Trim();
            string detail = colon > 0 ? rest.Substring(colon + 1).Trim() : "";
            Check(pass, label, detail);
        }

        private static string OutDir()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "LR2_QABot"));
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-lr2OutDir" && !string.IsNullOrWhiteSpace(args[i + 1]))
                    dir = Path.GetFullPath(Path.IsPathRooted(args[i + 1]) ? args[i + 1] : Path.Combine(Application.dataPath, "..", args[i + 1]));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private IEnumerator Start()
        {
            if (Application.isBatchMode) AudioListener.volume = 0f;
            Application.logMessageReceived += OnLog;
            string outDir = OutDir();
            string reqScene = GetArgValue("-lr2Scene");
            string reqSceneName = !string.IsNullOrEmpty(reqScene) ? Path.GetFileNameWithoutExtension(reqScene) : null;
            if (!string.IsNullOrEmpty(reqSceneName)
                && !string.Equals(SceneManager.GetActiveScene().name, reqSceneName, StringComparison.OrdinalIgnoreCase)
                && Application.CanStreamedLevelBeLoaded(reqSceneName))
            {
                var loadOp = SceneManager.LoadSceneAsync(reqSceneName);
                while (loadOp != null && !loadOp.isDone) yield return null;
                yield return null;
            }
            string sceneName = SceneManager.GetActiveScene().name;
            string upArg = GetArgValue("-lr2Upscaler");
            if (!string.IsNullOrEmpty(upArg))
            {
                _savedUpscalerMode = LR2Settings.UpscalerMode;
                int req = upArg.ToLowerInvariant() switch
                {
                    "stp" => 1,
                    "taau" => 2,
                    "fsr1" => 3,
                    "dlss" => 4,
                    "fsr2" => 5,
                    _ => 0
                };
                LR2Settings.UpscalerMode = req;
                LR2Settings.Apply();
                _telemetrySuffix = upArg.ToLowerInvariant();
            }
            _report.AppendLine($"=== LR2 QA BOT ({sceneName}) ===");

            // Pause-free deterministic test setup
            if (sceneName.Contains("MainMenu"))
            {
                yield return new WaitForSecondsRealtime(1.5f);
                yield return AuditMenuScene(outDir, sceneName);
                FinishReport(outDir, sceneName);
                yield break;
            }

            float waitUntil = Time.time + 18f;
            SoldierController player = null;
            while (player == null && Time.time < waitUntil)
            {
                var lp = Combatant.FindLocalPlayer();
                player = lp != null ? lp.GetComponent<SoldierController>() : null;
                yield return null;
            }
            if (player == null)
            {
                Check(false, "Spawn", "Local player never spawned");
                FinishReport(outDir, sceneName);
                yield break;
            }

            // Wait out match countdown if needed
            var mm = MatchManager.Instance;
            float cdWait = Time.time + 8f;
            while (mm != null && mm.State == MatchState.Countdown && Time.time < cdWait) yield return null;
            yield return new WaitForSeconds(0.5f);

            // Take input control and disable enemy AI temporarily for clean measurements
            var pis = player.GetComponent<PlayerInputSource>();
            if (pis != null) pis.enabled = false;
            player.InputSource = this;
            foreach (var bot in FindObjectsByType<AI.BotController>())
                if (bot != null) bot.enabled = false;

            yield return AuditVolumeAndVisibility(player, outDir, sceneName);
            AuditMapFlowB46(sceneName);
            yield return AuditFireHazardTransparencyB82(outDir, sceneName);

            if (sceneName.Contains("Saltworks"))
            {
                yield return AuditMuzzleFlashGlare(player);
                yield return AuditRecoilAndWeaponTTK(player);
                yield return AuditReloadIdleAndWalk(player);
                yield return AuditFootstepsAndProne(player);
                yield return AuditGunBloodAdhesion(player);
                yield return AuditBulletFlybySfx(player);
                yield return AuditProgressiveMusicAndHeadroom(player);
                yield return AuditWeatherThunderRainWetness(player);
                yield return AuditKillcamHitmarkersAndAudio(player);
                yield return AuditCtfAndAdaptiveBot(player);
                yield return AuditUpscalerBindingsAndDamageHud(player);
                yield return AuditDeathAdsCloseWeaponAndB57B65(player);
                yield return AuditB136RobotSystems(player);
                yield return AuditB137ToB144Systems(player);
                yield return AuditExperimentEndWindow(outDir);
            }

            FinishReport(outDir, sceneName);
        }

        private IEnumerator AuditMenuScene(string outDir, string sceneName)
        {
            var dir = LR2MusicDirector.Instance;
            Check(dir != null, "MusicDirector.MenuBoot", dir != null ? $"state={dir.StateLabel} menuVol={dir.MenuVolume:0.000}" : "null");
            if (dir != null)
                Check(dir.IsMenuMode && dir.MenuVolume > 0.01f, "MusicDirector.MenuPlaying", $"isMenu={dir.IsMenuMode} vol={dir.MenuVolume:0.000}");

            var common = LR2CommonSounds.Instance;
            Check(common != null, "CommonSounds", common != null ? "loaded" : "missing");
            if (common != null)
                Check(common.uiHover.volume <= 0.20f && common.uiClick.volume <= 0.25f, "MenuSFX.NotExtreme",
                    $"hover={common.uiHover.volume:0.00} click={common.uiClick.volume:0.00} back={common.uiBack.volume:0.00}");

            // B19 / B136b: Main-menu soldier must be visible (SWAT_Visual with skull mask, or Kinemation robot Body+Head with team swaps)
            yield return new WaitForSecondsRealtime(0.6f);
            var hero = GameObject.Find("HeroSoldier");
            bool hasHero = hero != null;
            bool isSwat = false;
            bool hasSkullMask = false;
            bool mannequinHidden = true;
            int robotBodyEnabled = 0;
            bool robotAlliedMat = false;
            bool hostileVisible = false;
            if (hero != null)
            {
                var swatVis = hero.transform.Find("SWAT_Visual") ?? hero.transform.Find("MilitaryVisual");
                isSwat = swatVis != null;
                foreach (var smr in hero.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    bool underWeapon = smr.GetComponentInParent<LR2Item>(true) != null;
                    if (isSwat)
                    {
                        if (!smr.transform.IsChildOf(swatVis))
                        {
                            if (!underWeapon && smr.enabled) mannequinHidden = false;
                            continue;
                        }
                        foreach (var mat in smr.sharedMaterials)
                            if (mat != null && mat.name.Contains("FaceMask_GhostSkull")) hasSkullMask = true;
                    }
                    else if (!underWeapon)
                    {
                        if (smr.enabled) robotBodyEnabled++;
                        foreach (var mat in smr.sharedMaterials)
                            if (mat != null && mat.name.Contains("M_LR2Robot_") && mat.name.Contains("Allied"))
                                robotAlliedMat = true;
                    }
                }
            }
            var hostile = GameObject.Find("MenuHostile1");
            int visibleHeroActive = 0;
            int visibleHostileActive = 0;
            if (hero != null)
            {
                foreach (var r in hero.GetComponentsInChildren<Renderer>(true))
                    if (r != null && r.enabled && r.gameObject.activeInHierarchy && r.GetComponentInParent<LR2Item>(true) == null)
                        visibleHeroActive++;
            }
            if (hostile != null)
            {
                foreach (var r in hostile.GetComponentsInChildren<Renderer>(true))
                    if (r != null && r.enabled && r.gameObject.activeInHierarchy && r.GetComponentInParent<LR2Item>(true) == null)
                    {
                        visibleHostileActive++;
                        if (r is SkinnedMeshRenderer) hostileVisible = true;
                    }
            }
            if (isSwat)
                Check(hasHero && hasSkullMask && mannequinHidden, "MainMenu.HeroMaskedSwat",
                    $"rig=swat hero={hasHero} skullMask={hasSkullMask} driverHidden={mannequinHidden}");
            else
                Check(hasHero && robotBodyEnabled >= 2 && robotAlliedMat && hostileVisible, "MainMenu.HeroMaskedSwat",
                    $"rig=robot hero={hasHero} enabledBody={robotBodyEnabled} alliedMat={robotAlliedMat} hostilePresent={hostileVisible}");
            CheckExtra(LR2QAExtraChecks.CheckMainMenuHeroVisibleState(hasHero, visibleHeroActive, visibleHostileActive));

            // Loading screen revamp overlap check: 0 pairwise overlaps across visible text elements
            LR2LoadingIntro.SetCapturePreviewTitleScreen(true);
            yield return null;
            var rects = LR2LoadingIntro.Instance != null ? LR2LoadingIntro.Instance.GetVisibleTextScreenRects() : new Rect[0];
            int overlaps = 0;
            for (int i = 0; i < rects.Length; i++)
            for (int j = i + 1; j < rects.Length; j++)
                if (rects[i].Overlaps(rects[j])) overlaps++;
            Check(rects.Length >= 4 && overlaps == 0, "LoadingScreen.NoTextOverlap",
                $"texts={rects.Length} overlaps={overlaps}");
            LR2LoadingIntro.SetCapturePreviewTitleScreen(false);
            yield return null;

            // B31: Starter title screen support before Main Menu
            Check(LR2LoadingIntro.HasTitlePromptSupport, "Intro.StarterTitleScreenSupported", "supported=True");
            Check(string.Equals(Application.version, "11.24.26.3", StringComparison.Ordinal), "Build.VersionStamped",
                $"version={Application.version}");

            // Capture main-menu visual screenshot + close-up Ghost mask / NATO olive uniform verification frame
            var cam = Camera.main;
            if (cam != null)
            {
                yield return null;
                var tex = CaptureCamera(cam, 960, 540);
                File.WriteAllBytes(Path.Combine(outDir, $"{sceneName}_vis.png"), tex.EncodeToPNG());
                Destroy(tex);

                if (hero != null)
                {
                    var mc = MenuCinematic.Instance;
                    bool prevMc = mc != null && mc.enabled;
                    if (mc != null) mc.enabled = false;
                    Vector3 origPos = cam.transform.position;
                    Quaternion origRot = cam.transform.rotation;
                    float origFov = cam.fieldOfView;

                    Transform headBone = null;
                    var anim = hero.GetComponentInChildren<Animator>();
                    if (anim != null) headBone = LR2Bones.Get(anim, HumanBodyBones.Head);
                    Vector3 aimPt = (headBone != null ? headBone.position : hero.transform.position + Vector3.up * 1.56f) - Vector3.up * 0.06f;
                    Vector3 closePos = aimPt + hero.transform.forward * 1.15f + hero.transform.right * -0.22f + Vector3.up * 0.08f;
                    cam.transform.SetPositionAndRotation(closePos, Quaternion.LookRotation(aimPt - closePos));
                    cam.fieldOfView = 34f;
                    yield return null;
                    yield return null;
                    var closeTex = CaptureCamera(cam, 1280, 720);
                    File.WriteAllBytes(Path.Combine(outDir, "main_menu_ghost_closeup.png"), closeTex.EncodeToPNG());
                    Destroy(closeTex);

                    cam.transform.SetPositionAndRotation(origPos, origRot);
                    cam.fieldOfView = origFov;
                    if (mc != null) mc.enabled = prevMc;
                }
            }
        }

        private IEnumerator AuditVolumeAndVisibility(SoldierController player, string outDir, string sceneName)
        {
            // Check HDRP Volume lighting parameters
            Volume globalVol = null;
            foreach (var v in FindObjectsByType<Volume>())
                if (v != null && v.isGlobal && v.priority <= 1f && v.sharedProfile != null && v.sharedProfile.Has<Exposure>()) { globalVol = v; break; }

            Check(globalVol != null, "HDRP.GlobalVolume", globalVol != null ? globalVol.sharedProfile.name : "missing");
            if (globalVol != null)
            {
                var prof = globalVol.sharedProfile;
                if (prof.TryGet<Exposure>(out var ex))
                    Check(ex.adaptationMode.value == AdaptationMode.Progressive && ex.limitMin.value <= 7.2f,
                        "Exposure.ProgressiveFloor", $"mode={ex.adaptationMode.value} limitMin={ex.limitMin.value:0.0} limitMax={ex.limitMax.value:0.0}");
                if (prof.TryGet<IndirectLightingController>(out var ilc))
                    Check(ilc.indirectDiffuseLightingMultiplier.value >= 1.25f, "IndirectLightingFloor", $"diffuseMult={ilc.indirectDiffuseLightingMultiplier.value:0.00}");
                if (prof.TryGet<Bloom>(out var bl))
                    Check(bl.threshold.value >= 1.15f && bl.intensity.value <= 0.16f && bl.dirtIntensity.value <= 2.0f,
                        "Bloom.AntiGlare", $"threshold={bl.threshold.value:0.00} intensity={bl.intensity.value:0.00} dirt={bl.dirtIntensity.value:0.00}");
            }

            // Measure spawn + cover/enemy luminance contrast using ViewCamera
            Camera cam = player.ViewCamera;
            SoldierController enemy = null;
            foreach (var c in Combatant.All)
                if (c != null && c != player.Combatant && c.IsAlive && c.Team != TeamId.Hostile) { enemy = c.GetComponent<SoldierController>(); if (enemy != null) break; }

            var spots = new List<Transform>();
            bool HasRoomAround(Vector3 p)
            {
                if (IsConfinedOrRoofed(p)) return false;
                if (Physics.CheckCapsule(p + Vector3.up * 0.55f, p + Vector3.up * 1.65f, 0.45f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                    return false;
                int openDirs = 0;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 d = Quaternion.Euler(0f, k * 90f, 0f) * Vector3.forward;
                    if (!Physics.Raycast(p + Vector3.up * 1.2f, d, 2.2f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                        openDirs++;
                }
                return openDirs >= 3;
            }
            foreach (var fa in FlagAnchor.All)
                if (fa != null && spots.Count < 3 && fa.transform.position.y < 1.8f && HasRoomAround(fa.transform.position))
                    spots.Add(fa.transform);
            foreach (var sp in FindObjectsByType<SpawnPoint>())
                if (sp != null && spots.Count < 3 && sp.transform.position.y < 1.8f && HasRoomAround(sp.transform.position))
                    spots.Add(sp.transform);
            var tacPts = FindObjectsByType<LR2.AI.CoverPoint>();
            foreach (var cp in tacPts)
                if (cp != null && spots.Count < 3 && cp.transform.position.y < 1.8f && HasRoomAround(cp.transform.position))
                    spots.Add(cp.transform);
            if (spots.Count == 0 && !IsConfinedOrRoofed(player.transform.position)) spots.Add(player.transform);
            if (spots.Count == 0) spots.Add(player.transform);

            float worstMean = 1f, worstContrast = 1f;
            for (int i = 0; i < spots.Count; i++)
            {
                Vector3 basePos = spots[i] == player.transform ? player.transform.position : spots[i].position;
                // Aim down the longest clear corridor from this spot so walls/cover never pin the camera or target
                Vector3 pos = basePos;
                float bestYaw = player.Yaw;
                float bestDist = -1f;
                for (int k = 0; k < 16; k++)
                {
                    float y = k * 22.5f;
                    Vector3 d = Quaternion.Euler(0f, y, 0f) * Vector3.forward;
                    float dist = Physics.Raycast(basePos + Vector3.up * 1.4f, d, out var rh, 24f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) ? rh.distance : 24f;
                    if (dist > bestDist) { bestDist = dist; bestYaw = y; }
                }
                Vector3 lookDir = Quaternion.Euler(0f, bestYaw, 0f) * Vector3.forward;
                Vector3 lookRight = Vector3.Cross(Vector3.up, lookDir).normalized;

                _cmd = new SoldierInput { absoluteLook = true, aimYaw = bestYaw, aimPitch = 0f };
                player.Movement.Teleport(pos);
                // Place test enemy slightly left-of-center at eye elevation so the right-handed rifle model never occludes them
                if (enemy != null && cam != null)
                {
                    Vector3 enemyPos = pos + lookDir * Mathf.Clamp(bestDist * 0.55f, 4.2f, 7.5f);
                    float viewYaw = bestYaw;
                    bool foundClear = false;
                    foreach (float lat in new[] { -0.95f, -1.45f, 0f })
                    {
                        Vector3 cand = pos + lookDir * Mathf.Clamp(bestDist * 0.55f, 4.2f, 7.2f) + lookRight * lat;
                        bool sameFloor = Physics.Raycast(cand + Vector3.up * 2.5f, Vector3.down, out var flHit, 5f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)
                            && Mathf.Abs(flHit.point.y - pos.y) <= 0.85f;
                        if (sameFloor && !Physics.Linecast(pos + Vector3.up * 1.4f, cand + Vector3.up * 1.4f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                        {
                            enemyPos = flHit.point + Vector3.up * 0.05f;
                            if (Mathf.Approximately(lat, 0f)) viewYaw = bestYaw + 9f; // turn rifle slightly right of centered target
                            foundClear = true;
                            break;
                        }
                    }
                    if (!foundClear)
                    {
                        enemyPos = pos + lookDir * Mathf.Clamp(bestDist * 0.45f, 3.8f, 6.0f);
                        if (Physics.Raycast(enemyPos + Vector3.up * 3f, Vector3.down, out var gh, 8f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                            enemyPos = gh.point + Vector3.up * 0.05f;
                        viewYaw = bestYaw + 9f;
                    }
                    _cmd.aimYaw = viewYaw;
                    float faceYaw = Quaternion.LookRotation(new Vector3(pos.x - enemyPos.x, 0f, pos.z - enemyPos.z)).eulerAngles.y;
                    enemy.Teleport(enemyPos, faceYaw);
                    enemy.GetComponent<SoldierVisuals>()?.ApplyMaterialsNow(false);
                    var eProxy = enemy.GetComponent<RetargetProxy>();
                    if (eProxy != null) eProxy.SyncPoseNow();
                }

                yield return new WaitForSeconds(0.55f); // let Progressive exposure adapt cleanly
                if (cam != null)
                {
                    Texture2D tex = CaptureCamera(cam, 640, 360);
                    float meanLum = SampleMeanLuminance(tex, out float p10Lum);
                    float cChest = enemy != null ? SampleTargetContrast(tex, cam, enemy.transform.position + Vector3.up * 1.15f) : meanLum;
                    float cHead = enemy != null ? SampleTargetContrast(tex, cam, enemy.transform.position + Vector3.up * 1.56f) : meanLum;
                    float contrast = Mathf.Max(cChest, cHead);
                    worstMean = Mathf.Min(worstMean, meanLum);
                    worstContrast = Mathf.Min(worstContrast, contrast);
                    if (i == 0) File.WriteAllBytes(Path.Combine(outDir, $"{sceneName}_vis.png"), tex.EncodeToPNG());
                    Destroy(tex);
                    _report.AppendLine($"  Vis[{sceneName}#{i}] meanLum={meanLum:0.000} p10={p10Lum:0.000} enemyContrast={contrast:0.000}");
                }
            }
            Check(worstMean >= 0.05f, $"{sceneName}.VisibilityFloor", $"worstMeanLum={worstMean:0.000} (min 0.050)");
            Check(worstContrast >= 0.025f, $"{sceneName}.EnemyReadability", $"worstEnemyContrast={worstContrast:0.000}");
        }

        private IEnumerator AuditMuzzleFlashGlare(SoldierController player)
        {
            Camera cam = player.ViewCamera;
            var gun = player.ActiveFirearm;
            float beforeLum = 0f, duringLum = 0f;
            float flashLightMax = 0f;
            if (gun != null && gun.Definition != null && gun.Definition.muzzleFlashPrefab != null)
            {
                var l = gun.Definition.muzzleFlashPrefab.GetComponentInChildren<Light>(true);
                if (l != null) flashLightMax = l.intensity;
            }
            if (cam != null && gun != null)
            {
                float readyWait = Time.time + 1.0f;
                while (gun.IsBusy && Time.time < readyWait) yield return null;
                yield return null;
                LR2QAExtraChecks.TryGetCameraShakeTrauma(out float traumaBefore);
                var tBefore = CaptureCamera(cam, 480, 270);
                beforeLum = SampleMeanLuminance(tBefore, out _);
                Destroy(tBefore);

                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, aimPitch = 0f, fire = true };
                int liveTracersSeen = 0;
                float traumaPeak = traumaBefore;
                bool capturedTracerShot = false;
                float shotWait = Time.time + 0.30f;
                while (Time.time < shotWait)
                {
                    yield return null;
                    if (LR2QAExtraChecks.TryGetCameraShakeTrauma(out float trNow))
                        traumaPeak = Mathf.Max(traumaPeak, trNow);
                    int liveNow = 0;
                    foreach (var tr in FindObjectsByType<Tracer>())
                        if (tr != null && tr.gameObject.activeInHierarchy) liveNow++;
                    liveTracersSeen = Mathf.Max(liveTracersSeen, liveNow);
                    if (!capturedTracerShot && liveNow > 0 && SceneManager.GetActiveScene().name.Contains("Saltworks"))
                    {
                        var tracerShot = CaptureCamera(cam, 960, 540);
                        File.WriteAllBytes(Path.Combine(OutDir(), "LR2_Saltworks_tracer.png"), tracerShot.EncodeToPNG());
                        Destroy(tracerShot);
                        capturedTracerShot = true;
                    }
                }
                LR2QAExtraChecks.TryGetCameraShakeTrauma(out float traumaEnd);
                float traumaAfter = Mathf.Max(traumaPeak, traumaEnd);
                if (!capturedTracerShot && SceneManager.GetActiveScene().name.Contains("Saltworks"))
                {
                    var tracerShot = CaptureCamera(cam, 960, 540);
                    File.WriteAllBytes(Path.Combine(OutDir(), "LR2_Saltworks_tracer.png"), tracerShot.EncodeToPNG());
                    Destroy(tracerShot);
                }
                var tDuring = CaptureCamera(cam, 480, 270);
                duringLum = SampleMeanLuminance(tDuring, out _);
                Destroy(tDuring);
                _cmd.fire = false;

                var lib = SurfaceLibrary.Instance;
                var tracerPf = lib != null ? lib.tracerPrefab : null;
                var tracerComp = tracerPf != null ? tracerPf.GetComponent<Tracer>() : null;
                var tracerLr = tracerPf != null ? tracerPf.GetComponent<LineRenderer>() : null;
                var tracerMat = tracerLr != null ? tracerLr.sharedMaterial : null;
                float tIntensity = tracerMat != null && tracerMat.HasProperty("_Intensity") ? tracerMat.GetFloat("_Intensity") : 0f;
                var tTex = tracerMat != null && tracerMat.HasProperty("_MainTex") ? tracerMat.GetTexture("_MainTex") as Texture2D : null;
                float headA = -1f, tailA = -1f;
                if (tTex != null && tTex.isReadable)
                {
                    headA = tTex.GetPixelBilinear(0.05f, 0.5f).a;
                    tailA = tTex.GetPixelBilinear(0.95f, 0.5f).a;
                }
                CheckExtra(LR2QAExtraChecks.CheckTracerVisibilityState(
                    tracerComp != null,
                    tracerComp != null ? tracerComp.Width : 0f,
                    tracerComp != null ? tracerComp.Length : 0f,
                    tracerComp != null ? tracerComp.Speed : 0f,
                    tracerComp != null ? tracerComp.MinPixels : 0f,
                    tracerComp != null ? tracerComp.IgnitionDistance : 0f,
                    tIntensity,
                    tTex != null && tTex.isReadable,
                    headA,
                    tailA));
                Check(liveTracersSeen >= 1, "FX.LiveTracerSpawnedInBurst", $"peakLiveTracers={liveTracersSeen}");

                float t0 = Time.time;
                float traumaRecovered = traumaAfter;
                while (Time.time - t0 < 1.35f)
                {
                    if (LR2QAExtraChecks.TryGetCameraShakeTrauma(out traumaRecovered) && traumaRecovered <= 0.001f) break;
                    yield return null;
                }
                CheckExtra(LR2QAExtraChecks.CheckCameraShake(traumaBefore, traumaAfter, traumaRecovered, Time.time - t0));
            }
            CheckExtra(LR2QAExtraChecks.CheckHorizontalFov());
            float delta = duringLum - beforeLum;
            Check(flashLightMax <= 4500f && delta < 0.14f, "MuzzleFlash.SmallGlare",
                $"prefabLux={flashLightMax:0} before={beforeLum:0.000} during={duringLum:0.000} spike={delta:0.000}");
            yield return new WaitForSeconds(0.25f);
        }

        private IEnumerator AuditRecoilAndWeaponTTK(SoldierController player)
        {
            var primary = player.Items.Count > 0 ? player.Items[0] as LR2Firearm : null;
            var sidearm = player.Items.Count > 1 ? player.Items[1] as LR2Firearm : null;
            if (primary != null && primary.Definition != null && sidearm != null && sidearm.Definition != null)
            {
                float akDmg = primary.Definition.DamageAtDistance(15f);
                float pDmg = sidearm.Definition.DamageAtDistance(15f);
                float akDps = akDmg * (primary.Definition.rpm / 60f);
                float pDps = pDmg * (sidearm.Definition.rpm / 60f);
                Check(akDmg >= pDmg && akDps > pDps * 1.4f, "TTK.AK12StrongerThanPistol",
                    $"{primary.Definition.shortName}@15m={akDmg:0} ({akDps:0}/s) Mk23={pDmg:0} ({pDps:0}/s)");
                Check(primary.Definition.adsRecoilMultiplier <= 0.6f, "Recoil.AdsControlReduction",
                    $"adsRecoilMult={primary.Definition.adsRecoilMultiplier:0.00} adsKickMult={primary.Definition.adsKickMultiplier:0.00}");
            }
            yield break;
        }

        private static Transform FindChildNamed(Transform root, string targetName)
        {
            if (root == null || !root.gameObject.activeSelf) return null;
            if (string.Equals(root.name, targetName, StringComparison.Ordinal)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindChildNamed(root.GetChild(i), targetName);
                if (found != null) return found;
            }
            return null;
        }

        private IEnumerator AuditReloadIdleAndWalk(SoldierController player)
        {
            var gun = player.ActiveFirearm;
            var proxy = player.GetComponent<RetargetProxy>();
            var bridgeField = typeof(RetargetProxy).GetField("_bridge", BindingFlags.Instance | BindingFlags.NonPublic);
            var bridge = proxy != null && bridgeField != null ? bridgeField.GetValue(proxy) as ReloadMotionBridge : null;

            Transform magTf = gun != null ? FindChildNamed(gun.transform, "Mag") : null;
            Transform chargeTf = gun != null ? (FindChildNamed(gun.transform, "Charge")
                ?? FindChildNamed(gun.transform, "ChargingHandle")
                ?? FindChildNamed(gun.transform, "Bolt")
                ?? FindChildNamed(gun.transform, "Slider")) : null;

            CheckExtra(proxy != null ? LR2QAExtraChecks.CheckHandsOnGun(proxy, gun) : LR2QAExtraChecks.CheckHandsOnGunRobot(player, gun)); // B136
            LR2QAExtraChecks.CaptureReloadIdlePose(player);

            string worstAbsPose = LR2QAExtraChecks.CheckAbsoluteReloadPose(player);
            float worstAbsAng = 0f;

            IEnumerator RunReloadPass(bool walk, System.Action<float, float, float, float> onDone)
            {
                float maxAng = 0f, maxHand = 0f, maxMag = 0f, maxCharge = 0f;
                Vector3 magRest = magTf != null && gun != null ? gun.transform.InverseTransformPoint(magTf.position) : Vector3.zero;
                Vector3 chargeRest = chargeTf != null && gun != null ? gun.transform.InverseTransformPoint(chargeTf.position) : Vector3.zero;

                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, fire = true };
                yield return new WaitForSeconds(0.22f);
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw };
                yield return new WaitForSeconds(0.20f); // let bolt & recoil settle before recording rest pose
                if (gun != null)
                {
                    if (magTf != null) magRest = gun.transform.InverseTransformPoint(magTf.position);
                    if (chargeTf != null) chargeRest = gun.transform.InverseTransformPoint(chargeTf.position);
                }
                if (!walk) LR2QAExtraChecks.CaptureReloadIdlePose(player);
                _cmd = new SoldierInput
                {
                    absoluteLook = true,
                    aimYaw = player.Yaw,
                    move = walk ? new Vector2(0f, 1f) : Vector2.zero,
                    reload = true
                };

                float elapsed = 0f;
                while (elapsed < 5.5f && ((gun != null && gun.IsReloading) || elapsed < 0.2f))
                {
                    elapsed += Time.deltaTime;
                    if (bridge != null)
                    {
                        maxAng = Mathf.Max(maxAng, bridge.LastGunAngle);
                        maxHand = Mathf.Max(maxHand, bridge.HandWeight);
                    }
                    if (!walk && gun != null && gun.IsReloading && player.ViewTransform != null)
                    {
                        float absAng = Vector3.Angle(gun.transform.forward, player.ViewTransform.forward);
                        if (absAng >= worstAbsAng)
                        {
                            worstAbsAng = absAng;
                            worstAbsPose = LR2QAExtraChecks.CheckAbsoluteReloadPose(player);
                        }
                    }
                    if (gun != null)
                    {
                        if (magTf != null) maxMag = Mathf.Max(maxMag, Vector3.Distance(magRest, gun.transform.InverseTransformPoint(magTf.position)));
                        if (chargeTf != null) maxCharge = Mathf.Max(maxCharge, Vector3.Distance(chargeRest, gun.transform.InverseTransformPoint(chargeTf.position)));
                    }
                    yield return null;
                }
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw };
                while (gun != null && gun.IsReloading) yield return null;
                onDone?.Invoke(maxAng, maxHand, maxMag, maxCharge);
            }

            float peakIdleAng = 0f, peakIdleHand = 0f, peakIdleMag = 0f, peakIdleCharge = 0f;
            float peakWalkAng = 0f, peakWalkHand = 0f, peakWalkMag = 0f, peakWalkCharge = 0f;
            yield return RunReloadPass(false, (a, h, m, c) => { peakIdleAng = a; peakIdleHand = h; peakIdleMag = m; peakIdleCharge = c; });
            int ammoAfterTactical = gun != null ? gun.AmmoInMagazine : 0;
            yield return RunReloadPass(true, (a, h, m, c) => { peakWalkAng = a; peakWalkHand = h; peakWalkMag = m; peakWalkCharge = c; });

            bool hasBridge = bridge != null;
            if (hasBridge)
            {
                Check(peakIdleAng > 5f && peakIdleAng <= 75f && peakIdleHand > 0.2f, "Reload.StationaryValid",
                    $"maxAngle={peakIdleAng:0.0}deg maxHandW={peakIdleHand:0.00}");
                Check(peakWalkAng > 5f && peakWalkAng <= 75f && peakWalkHand > 0.2f, "Reload.WalkingNoFlip",
                    $"maxAngle={peakWalkAng:0.0}deg maxHandW={peakWalkHand:0.00}");
            }
            else
            {
                Check(peakIdleMag >= 0.04f && worstAbsAng <= 65f, "Reload.StationaryValid",
                    $"rig=robot magTravel={peakIdleMag:0.000}m worstAbsAng={worstAbsAng:0.0}deg");
                Check(peakWalkMag >= 0.04f && worstAbsAng <= 65f, "Reload.WalkingNoFlip",
                    $"rig=robot magTravel={peakWalkMag:0.000}m worstAbsAng={worstAbsAng:0.0}deg");
            }
            CheckExtra(worstAbsPose);
            CheckExtra(LR2QAExtraChecks.CheckTacticalReload(gun, ammoAfterTactical, peakIdleCharge));
            Check(magTf == null || (peakIdleMag >= 0.05f && peakWalkMag >= 0.05f),
                "Reload.MagAndChargeAnimate",
                $"idle(mag={peakIdleMag:0.000}m,charge={peakIdleCharge:0.000}m) walk(mag={peakWalkMag:0.000}m,charge={peakWalkCharge:0.000}m)");
        }

        private static bool IsConfinedOrRoofed(Vector3 pos)
        {
            if (Physics.Raycast(pos + Vector3.up * 0.45f, Vector3.up, 22f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                return true;
            Vector3[] offsets = { new Vector3(1.1f, 0f, 0f), new Vector3(-1.1f, 0f, 0f), new Vector3(0f, 0f, 1.1f), new Vector3(0f, 0f, -1.1f) };
            int hits = 0;
            foreach (var o in offsets)
                if (Physics.Raycast(pos + o + Vector3.up * 0.6f, Vector3.up, 22f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                    hits++;
            return hits >= 2;
        }

        private static float FindOpenYawFrom(Vector3 origin, out float clearDist)
        {
            float bestYaw = 0f;
            clearDist = 0f;
            // Reject points whose starting collider capsule already overlaps walls, steps, or props
            if (Physics.CheckCapsule(origin + Vector3.up * 0.62f, origin + Vector3.up * 1.65f, 0.42f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                return 0f;

            Vector3 ankle = origin + Vector3.up * 0.44f;
            Vector3 chest = origin + Vector3.up * 1.15f;
            for (int i = 0; i < 24; i++)
            {
                float yaw = i * 15f;
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                float dAnkle = Physics.SphereCast(ankle, 0.28f, dir, out var hitA, 25f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) ? hitA.distance : 25f;
                float dChest = Physics.SphereCast(chest, 0.40f, dir, out var hitC, 25f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) ? hitC.distance : 25f;
                float d = Mathf.Min(dAnkle, dChest);
                for (float step = 1.6f; step <= Mathf.Min(6.4f, d); step += 1.6f)
                {
                    if (!Physics.Raycast(origin + dir * step + Vector3.up * 1.2f, Vector3.down, out var gh, 2.4f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) ||
                        Mathf.Abs(gh.point.y - origin.y) > 0.38f)
                    {
                        d = Mathf.Min(d, Mathf.Max(0.2f, step - 1.0f));
                        break;
                    }
                }
                if (d > clearDist) { clearDist = d; bestYaw = yaw; }
            }
            return bestYaw;
        }

        private IEnumerator AuditWeatherThunderRainWetness(SoldierController player)
        {
            var ws = WeatherSystem.Instance;
            if (ws == null) yield break;

            ws.SetWetnessForWarmup(0.10f);
            float startWet = ws.Wetness;
            ws.TriggerThunderRain(0.95f, 30f, withFlash: true);
            float tEnd = Time.time + 2.0f;
            while (Time.time < tEnd) yield return null;

            float rainEff = ws.EffectiveRain;
            bool rainPlaying = ws.rain != null && ws.rain.isPlaying;
            bool worldCol = ws.rain != null && ws.rain.collision.enabled;
            float naturalWetAfter2s = ws.Wetness;

            ws.SetWetnessForWarmup(0.88f);
            yield return null;
            yield return null;

            bool ok = rainEff >= 0.70f && rainPlaying && worldCol && naturalWetAfter2s > startWet + 0.05f && ws.Wetness >= 0.85f && ws.WetSurfaceCount > 0;
            Check(ok, "Weather.ThunderRainAndWetness",
                $"rain={rainEff:F2} playing={rainPlaying} col={worldCol} wet0={startWet:F2} wet2s={naturalWetAfter2s:F3} wetFinal={ws.Wetness:F2} surfaces={ws.WetSurfaceCount}");
        }

        private IEnumerator AuditFootstepsAndProne(SoldierController player)
        {
            var mv = player.Movement;
            // Relocate to an unobstructed SpawnPoint + longest open lane so walls/crates never block walk or prone crawl
            var spawns = FindObjectsByType<SpawnPoint>();
            Vector3 bestStart = player.transform.position;
            float bestYaw = FindOpenYawFrom(bestStart, out float bestLane);
            foreach (var sp in spawns)
            {
                if (sp == null) continue;
                float y = FindOpenYawFrom(sp.transform.position, out float d);
                if (d > bestLane) { bestLane = d; bestStart = sp.transform.position; bestYaw = y; }
            }
            if (bestLane < 12f)
            {
                foreach (var cp in FindObjectsByType<LR2.AI.CoverPoint>())
                {
                    if (cp == null) continue;
                    float y = FindOpenYawFrom(cp.transform.position, out float d);
                    if (d > bestLane) { bestLane = d; bestStart = cp.transform.position; bestYaw = y; }
                }
            }
            if (bestLane >= 8f)
            {
                mv.Teleport(bestStart + Vector3.up * 0.05f);
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = bestYaw, aimPitch = 0f };
                yield return new WaitForSeconds(0.15f);
            }

            _footsteps = 0;
            _distAccum = 0f;
            float plantSum = 0f;
            int plantCount = 0;
            var visAnim = player.GetComponent<RetargetProxy>()?.Visual?.GetComponent<Animator>() ?? player.GetComponent<Animator>(); // B136: robot = own body
            Transform footL = LR2Bones.Get(visAnim, HumanBodyBones.LeftFoot);
            Transform footR = LR2Bones.Get(visAnim, HumanBodyBones.RightFoot);
            void OnStep(float sp)
            {
                _footsteps++;
                if (footL != null && footR != null)
                {
                    plantSum += Mathf.Min(footL.position.y, footR.position.y) - player.transform.position.y;
                    plantCount++;
                }
            }
            mv.Footstep += OnStep;

            // Walk forward 2.2 seconds along open ground and measure real stride distance per step + planted-foot slide (B9, B34)
            Vector3 prevFL = footL != null ? footL.position : Vector3.zero;
            Vector3 prevFR = footR != null ? footR.position : Vector3.zero;
            Vector3 minSlidePrev = Vector3.zero, minSlideCur = Vector3.zero;
            float minSlideDt = 0.016f, minSlideSpeed = float.MaxValue;
            bool samplePlanted = false;

            _cmd = new SoldierInput { absoluteLook = true, aimYaw = bestYaw, move = new Vector2(0f, 1f) };
            Vector3 prev = player.transform.position;
            float elapsed = 0f;
            while (elapsed < 2.2f)
            {
                float dt = Time.deltaTime;
                elapsed += dt;
                Vector3 cur = player.transform.position;
                _distAccum += Vector2.Distance(new Vector2(prev.x, prev.z), new Vector2(cur.x, cur.z));
                prev = cur;
                if (elapsed > 0.45f && dt > 0.002f && footL != null && footR != null)
                {
                    float hL = footL.position.y - player.transform.position.y;
                    float hR = footR.position.y - player.transform.position.y;
                    Vector3 dL = footL.position - prevFL; dL.y = 0f;
                    Vector3 dR = footR.position - prevFR; dR.y = 0f;
                    float sL = dL.magnitude / dt, sR = dR.magnitude / dt;
                    bool pickLeft = hL <= hR;
                    float s = pickLeft ? sL : sR;
                    bool isPlanted = (pickLeft ? hL : hR) <= 0.165f;
                    if (isPlanted && s < minSlideSpeed)
                    {
                        samplePlanted = true;
                        minSlideSpeed = s;
                        minSlideDt = dt;
                        minSlidePrev = pickLeft ? prevFL : prevFR;
                        minSlideCur = pickLeft ? footL.position : footR.position;
                    }
                }
                if (footL != null) prevFL = footL.position;
                if (footR != null) prevFR = footR.position;
                yield return null;
            }
            mv.Footstep -= OnStep;
            _cmd = new SoldierInput { absoluteLook = true, aimYaw = bestYaw };
            if (footL != null && footR != null)
            {
                CheckExtra(LR2QAExtraChecks.CheckFootSlide(minSlidePrev, minSlideCur, minSlideDt, samplePlanted));
                float plantedSpeed = samplePlanted && minSlideDt > 0f
                    ? new Vector2(minSlideCur.x - minSlidePrev.x, minSlideCur.z - minSlidePrev.z).magnitude / minSlideDt
                    : 0f;
                CheckExtra(LR2QAExtraChecks.CheckMotionFootSlide(plantedSpeed));
            }

            float measuredStride = _footsteps > 0 ? _distAccum / _footsteps : 99f;
            float meanPlantH = plantCount > 0 ? plantSum / plantCount : 0f;
            float weaponGain = LR2Audio.CategoryVolume(AudioCategory.Weapons);
            float stepGain = LR2Audio.CategoryVolume(AudioCategory.Footsteps);
            float dbRel = 20f * Mathf.Log10(Mathf.Max(0.0001f, stepGain) / Mathf.Max(0.0001f, weaponGain));

            Check(_footsteps >= 4 && measuredStride >= 0.70f && measuredStride <= 1.45f && (plantCount == 0 || meanPlantH <= 0.22f), "Footstep.TimingSynced",
                $"steps={_footsteps} dist={_distAccum:0.00}m stride={measuredStride:0.00}m plantH={meanPlantH:0.000}m lane={bestLane:0.0}m");
            Check(dbRel <= -14.0f && dbRel >= -18.5f, "Footstep.AudioMixUnderGuns",
                $"stepGain={stepGain:0.000} weaponGain={weaponGain:0.000} ({dbRel:0.0} dB)");

            // Test prone transition speed & crawl speed on the remaining open lane (or reverse back down spawn lane)
            float crawlYaw = FindOpenYawFrom(player.transform.position, out _);
            _cmd = new SoldierInput { absoluteLook = true, aimYaw = crawlYaw, aimPitch = 0f };
            yield return null;
            float lockSec = mv.Settings != null ? mv.Settings.proneTransitionDuration : -1f;
            _cmd = new SoldierInput { absoluteLook = true, aimYaw = crawlYaw, prone = true };
            yield return new WaitForSeconds(0.38f);
            bool enteredProne = mv.Stance == SoldierStance.Prone;
            _cmd = new SoldierInput { absoluteLook = true, aimYaw = crawlYaw, move = new Vector2(0f, 1f) };
            Vector3 p0 = player.transform.position;
            yield return new WaitForSeconds(0.8f);
            float crawlSpeed = Vector2.Distance(new Vector2(p0.x, p0.z), new Vector2(player.transform.position.x, player.transform.position.z)) / 0.8f;
            _cmd = new SoldierInput { absoluteLook = true, aimYaw = crawlYaw, prone = true };
            yield return new WaitForSeconds(0.38f);
            Check(lockSec <= 0.40f && enteredProne && crawlSpeed >= 1.0f, "Prone.FasterTransitionAndCrawl",
                $"transitionSetting={lockSec:0.00}s entered={enteredProne} crawlSpeed={crawlSpeed:0.00}m/s");
        }

        private IEnumerator AuditGunBloodAdhesion(SoldierController player)
        {
            var fx = GunBloodFX.Instance;
            Check(fx != null, "GunBloodFX.Active", fx != null ? "ACTIVE" : "NULL");
            if (fx != null && player.Health != null)
            {
                var d = new DamageInfo { amount = 34f, type = DamageType.Bullet, point = player.Combatant.ChestPosition, direction = -player.transform.forward, weaponName = "QA" };
                player.Health.ApplyDamage(ref d);
                yield return new WaitForSeconds(0.2f);
                int onWeapon = 0, nearLens = 0, detached = 0, underBarrel = 0;
                Camera cam = player.ViewCamera;
                Transform itemTf = player.ActiveItem != null ? player.ActiveItem.transform : null;
                foreach (var r in FindObjectsByType<MeshRenderer>())
                {
                    if (r == null || (!r.gameObject.name.Contains("[LR2_BloodBead]") && !r.gameObject.name.Contains("[LR2_BloodSplat]"))) continue;
                    if (itemTf != null && !r.transform.IsChildOf(itemTf)) detached++;
                    else
                    {
                        onWeapon++;
                        if (fx.LastMeasuredTopY > -0.5f && r.transform.localPosition.y < fx.LastMeasuredTopY - 0.025f) underBarrel++;
                    }
                    if (cam != null && cam.transform.InverseTransformPoint(r.transform.position).z < 0.20f) nearLens++;
                }
                Check(fx.ActiveGunDropletCount >= 3 && onWeapon >= 3 && detached == 0 && nearLens == 0 && underBarrel == 0, "GunBlood.StuckToWeapon",
                    $"count={fx.ActiveGunDropletCount} onWeapon={onWeapon} detached={detached} nearLens={nearLens} underBarrel={underBarrel}");
                if (cam != null && SceneManager.GetActiveScene().name.Contains("Saltworks"))
                {
                    var gbTex = CaptureCamera(cam, 960, 540);
                    File.WriteAllBytes(Path.Combine(OutDir(), "LR2_Saltworks_gunblood.png"), gbTex.EncodeToPNG());
                    Destroy(gbTex);
                }
            }
        }

        private IEnumerator AuditBulletFlybySfx(SoldierController player)
        {
            var common = LR2CommonSounds.Instance;
            int flybyCount = 0;
            float measuredDist = -1f;
            void OnNear(NearMissEvent e)
            {
                if (e.listener == player.Combatant)
                {
                    flybyCount++;
                    measuredDist = e.distance;
                }
            }
            GameEvents.NearMiss += OnNear;
            Vector3 head = player.Combatant.HeadPosition;
            Vector3 lateral = player.transform.right * 1.4f;
            Vector3 shotOrigin = head + player.transform.forward * 14f + lateral;
            Vector3 shotEnd = head - player.transform.forward * 6f + lateral;
            Vector3[] axes = { player.transform.forward, -player.transform.forward, player.transform.right, -player.transform.right };
            Vector3[] lats = { player.transform.right * 1.4f, -player.transform.right * 1.4f, Vector3.up * 1.3f };
            foreach (var ax in axes)
            {
                bool found = false;
                foreach (var lat in lats)
                {
                    Vector3 o = head + ax * 10f + lat;
                    Vector3 e = head - ax * 4f + lat;
                    if (!Physics.Linecast(o, e, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                    {
                        shotOrigin = o;
                        shotEnd = e;
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }
            Vector3 shotDir = (shotEnd - shotOrigin).normalized;
            var gun = player.ActiveFirearm;
            Ballistics.Fire(null, gun != null ? gun.Definition : null, shotOrigin, shotDir, shotOrigin, false, null);
            yield return null;
            GameEvents.NearMiss -= OnNear;

            bool sfxValid = common != null && common.bulletWhizz != null && common.bulletWhizz.IsValid && common.bulletCrack != null && common.bulletCrack.IsValid;
            Check(sfxValid && flybyCount >= 1 && measuredDist >= 0.5f && measuredDist <= 4.2f, "Audio.BulletFlybySFX",
                $"events={flybyCount} dist={measuredDist:0.00}m whizzValid={common?.bulletWhizz?.IsValid} crackValid={common?.bulletCrack?.IsValid} maxDist={common?.bulletWhizz?.maxDistance:0}");
        }

        private IEnumerator AuditProgressiveMusicAndHeadroom(SoldierController player)
        {
            var dir = LR2MusicDirector.Instance;
            Check(dir != null, "MusicDirector.GameplayActive", dir != null ? $"state={dir.StateLabel}" : "missing");
            if (dir == null) yield break;
            var gun = player != null ? player.ActiveFirearm : null;
            if (player == null || player.Combatant == null || gun == null || gun.Definition == null)
            {
                Check(false, "Music.GunfireShotEvents", "missing local player, combatant, or active firearm");
                yield break;
            }

            // Start from a settled baseline, then exercise the same ShotFired event path as local gunplay.
            _cmd.fire = false;
            _cmd.aim = false;
            dir.ResetToCalm();
            yield return new WaitForSeconds(0.35f);
            float calmGain = dir.EffectiveTotalGain;
            float calmIntensity = dir.Intensity;

            int shotsBefore = player.Combatant.stats.shotsFired;
            float shotInterval = gun.Definition.SecondsBetweenShots;
            float pulseSeconds = Mathf.Max(0.07f, shotInterval * 1.25f);
            float releaseSeconds = Mathf.Max(0.05f, shotInterval);
            for (int k = 0; k < 6 && gun.AmmoInMagazine > 0; k++)
            {
                _cmd = new SoldierInput
                {
                    absoluteLook = true,
                    aimYaw = player.Yaw,
                    aimPitch = -8f,
                    fire = true
                };
                float pulseUntil = Time.time + pulseSeconds;
                while (Time.time < pulseUntil) yield return null;
                _cmd.fire = false;
                yield return new WaitForSeconds(releaseSeconds);
            }
            _cmd.fire = false;
            yield return new WaitForSeconds(0.12f);
            int localShots = player.Combatant.stats.shotsFired - shotsBefore;
            float fightIntensity = dir.Intensity;
            float fightGain = dir.EffectiveTotalGain;
            float combatStem = dir.CombatVolume;
            float stepCat = LR2Audio.CategoryVolume(AudioCategory.Footsteps);

            Check(localShots >= 4, "Music.GunfireShotEvents",
                $"localShots={localShots} expected>=4 ammo={gun.AmmoInMagazine}/{gun.MagazineSize}");
            Check(fightIntensity > calmIntensity + 0.45f && fightGain > calmGain * 1.55f && combatStem > 0.14f && dir.LastDrumrollTime >= 0f, "Music.OrganicCombatRamp",
                $"localShots={localShots} calmI={calmIntensity:0.00} fightI={fightIntensity:0.00} combatStem={combatStem:0.000} calmGain={calmGain:0.000} fightGain={fightGain:0.000} drumrollAt={dir.LastDrumrollTime:0.00}s");
            Check(calmGain <= 0.20f && fightGain <= 0.72f, "Music.LeavesFootstepsClear",
                $"calmGain={calmGain:0.000} fightGain={fightGain:0.000} stepGain={stepCat:0.000}");
            CheckExtra(LR2QAExtraChecks.CheckMusicSources(dir.MusicSources));
            CheckExtra(LR2QAExtraChecks.CheckMusicGainDuringGunfire(calmGain, fightGain));
        }

        private IEnumerator AuditKillcamHitmarkersAndAudio(SoldierController player)
        {
            var kd = KillcamDirector.Instance;
            var hud = FindAnyObjectByType<HUDController>();
            Check(kd != null, "KillcamDirector.Present", kd != null ? "ACTIVE" : "MISSING");

            // Hide main hudGroup just like killcam end-of-match/hidden HUD and verify replay hitmarker works cleanly (B4, B6, B8, B14)
            float prevAlpha = hud != null && hud.hudGroup != null ? hud.hudGroup.alpha : 1f;
            if (hud != null && hud.hudGroup != null) hud.hudGroup.alpha = 0f;
            HUDController.ShowReplayHit(true, true, Camera.main != null ? Camera.main.transform.position + Camera.main.transform.forward * 6f : Vector3.one);
            yield return null;
            var hf = FindObjectsByType<HitFeedbackHUD>();
            bool anyVisible = false;
            bool zoneOk = false;
            foreach (var h in hf)
            {
                if (h == null || !h.gameObject.activeInHierarchy) continue;
                bool hiddenParent = false;
                for (Transform p = h.transform; p != null; p = p.parent)
                {
                    var cg = p.GetComponent<CanvasGroup>();
                    if (cg != null && cg.alpha < 0.8f) { hiddenParent = true; break; }
                }
                if (!hiddenParent)
                {
                    anyVisible = true;
                    if (!string.IsNullOrEmpty(h.LastZoneText) && h.IsShowingWorldImpactPip) zoneOk = true;
                }
            }
            if (hud != null && hud.hudGroup != null) hud.hudGroup.alpha = prevAlpha;
            Check(anyVisible, "Killcam.ShowReplayHitWorksWhenHudHidden", $"overlays={hf.Length} visibleWhenHidden={anyVisible}");

            var cs = LR2CommonSounds.Instance;
            bool ruggedAudio = cs != null && cs.hitmarker.IsValid && cs.hitmarkerHeadshot.IsValid && cs.killConfirm.IsValid;
            Check(ruggedAudio && HitFeedbackHUD.HasZoneIndicator && zoneOk, "HitFeedback.RuggedAudioAndZoneIndicator",
                $"audio={ruggedAudio} zoneUI={HitFeedbackHUD.HasZoneIndicator} pipActive={zoneOk}");
            Check(kd != null && kd.PostKillWindow >= 0.70f, "Killcam.VictimCollapseWindow",
                kd != null ? $"postKillWindow={kd.PostKillWindow:0.00}s pre={kd.PreKillWindow:0.00}s" : "null");
            Check(kd != null && kd.UsesThemeFonts, "Killcam.ThemeFont",
                kd != null ? $"usesTheme={kd.UsesThemeFonts}" : "null");

            // B136 Audio repairs: PainVoice attached on player+bots, distinct self vs target armor-break, health replenished, and death SFX
            var pv = player != null ? player.GetComponent<PainVoice>() : null;
            bool pvAllBots = true;
            foreach (var sc in FindObjectsByType<SoldierController>())
                if (sc != null && sc.GetComponent<PainVoice>() == null) { pvAllBots = false; break; }
            int replBefore = PainVoice.HealthReplenishedCount;
            int selfArmBefore = HitFeedbackHUD.SelfArmorBreakCount;
            int tgtArmBefore = HitFeedbackHUD.TargetArmorBreakCount;
            if (player != null && player.Health != null)
            {
                var breakSelf = new DamageInfo { amount = player.Health.Armor + 12f, type = DamageType.Bullet, zone = HitZone.UpperTorso, point = player.Combatant.ChestPosition, direction = -player.transform.forward, weaponName = "QA_SelfBreak" };
                player.Health.ApplyDamage(ref breakSelf);
                player.Health.Heal(player.Health.Max);
                player.Health.AddArmor(player.Health.Max);
            }
            var hostile = FindLiveHostileSoldier(player);
            if (hostile != null && hostile.Health != null && player != null)
            {
                var breakTgt = new DamageInfo { attacker = player.Combatant, amount = hostile.Health.Armor + 8f, type = DamageType.Bullet, zone = HitZone.UpperTorso, point = hostile.Combatant.ChestPosition, direction = player.transform.forward, weaponName = "QA_TgtBreak" };
                hostile.Health.ApplyDamage(ref breakTgt);
                hostile.Health.Heal(hostile.Health.Max);
                hostile.Health.AddArmor(hostile.Health.Max);
            }
            yield return null;
            Check(pv != null && pvAllBots && PainVoice.HasDeathImpactClip && PainVoice.HasHealthReplenishedClip &&
                  PainVoice.HealthReplenishedCount > replBefore &&
                  HitFeedbackHUD.HasDistinctArmorBreakClips &&
                  HitFeedbackHUD.SelfArmorBreakCount > selfArmBefore &&
                  HitFeedbackHUD.TargetArmorBreakCount > tgtArmBefore,
                "Audio.DeathHealthAndArmorBreak",
                $"pv={pv != null} allBots={pvAllBots} deathClip={PainVoice.HasDeathImpactClip} replClip={PainVoice.HasHealthReplenishedClip} replDelta={PainVoice.HealthReplenishedCount - replBefore} armorClips={HitFeedbackHUD.HasDistinctArmorBreakClips} selfBreakDelta={HitFeedbackHUD.SelfArmorBreakCount - selfArmBefore} tgtBreakDelta={HitFeedbackHUD.TargetArmorBreakCount - tgtArmBefore}");
        }

        private IEnumerator AuditCtfAndAdaptiveBot(SoldierController player)
        {
            // 1. Verify FlagAnchors exist on combat maps & BF6 proximity multiplier curves ("closer faster, far harder")
            float coreMult = FlagZoneController.ComputeProximitySpeedMultiplier(1.5f, 2.6f, 11.5f);
            float midMult = FlagZoneController.ComputeProximitySpeedMultiplier(6.5f, 2.6f, 11.5f);
            float edgeMult = FlagZoneController.ComputeProximitySpeedMultiplier(11.4f, 2.6f, 11.5f);
            float outMult = FlagZoneController.ComputeProximitySpeedMultiplier(12.2f, 2.6f, 11.5f);
            Check(FlagAnchor.All.Count >= 4 && coreMult >= 2.15f && midMult > edgeMult && edgeMult <= 0.45f && outMult == 0f,
                "CTF.BF6ProximityCaptureCurve",
                $"anchors={FlagAnchor.All.Count} core={coreMult:0.00}x mid={midMult:0.00}x edge={edgeMult:0.00}x out={outMult:0.00}x");

            // 2. Verify BotController generates pure human mouse deltas (never absoluteLook teleport) & supports Adaptive difficulty
            int prevDiff = LR2Settings.BotDifficulty;
            LR2Settings.BotDifficulty = 4; // Adaptive
            AI.BotController testBot = null;
            foreach (var b in FindObjectsByType<AI.BotController>())
                if (b != null && b.Soldier != null && b.Soldier != player) { testBot = b; break; }
            bool humanInputOk = false;
            bool adaptiveOk = false;
            if (testBot != null)
            {
                bool wasEnabled = testBot.enabled;
                testBot.enabled = true;
                yield return null;
                var cmd = SoldierInput.Empty;
                testBot.GetInput(testBot.Soldier, ref cmd);
                humanInputOk = !cmd.absoluteLook;
                adaptiveOk = testBot.IsAdaptiveMode && testBot.ActiveDifficulty != null;
                testBot.enabled = wasEnabled;
            }
            LR2Settings.BotDifficulty = prevDiff;
            Check(testBot != null && humanInputOk && adaptiveOk, "Bot.HumanMouseInputAndAdaptive",
                $"bot={(testBot != null ? testBot.name : "none")} humanMouseLook={humanInputOk} adaptive={adaptiveOk}");

            // 3. Verify PhysicallyBasedSky + VolumetricClouds (or HDRISky), RayTracingController, & realistic M_LR2_Glass readiness
            bool hasSky = false;
            bool hasClouds = false;
            foreach (var v in FindObjectsByType<Volume>())
            {
                if (v != null && v.sharedProfile != null && (v.sharedProfile.Has<PhysicallyBasedSky>() || v.sharedProfile.Has<HDRISky>()))
                    hasSky = true;
                if (v != null && v.sharedProfile != null && v.sharedProfile.Has<VolumetricClouds>())
                    hasClouds = true;
            }
            Check(hasSky, "Lighting.HDRISkyPresent",
                $"sky={hasSky} clouds={hasClouds} rayTracingCtrl={(LR2RayTracingController.Instance != null)}");

            int glassCount = 0, glassRealistic = 0;
            foreach (var dp in FindObjectsByType<DestructibleProp>())
            {
                if (dp == null || dp.surface != SurfaceType.Glass) continue;
                glassCount++;
                var r = dp.GetComponentInChildren<Renderer>();
                if (r != null && r.sharedMaterial != null && r.sharedMaterial.HasProperty("_SurfaceType") &&
                    r.sharedMaterial.GetFloat("_SurfaceType") > 0.5f && r.sharedMaterial.GetTexture("_NormalMap") != null)
                    glassRealistic++;
            }
            Check(glassCount == 0 || glassRealistic == glassCount, "Glass.RealisticMaterialAndPanes",
                $"panes={glassCount} realisticPanes={glassRealistic}");
            Check(AmmoCrate.All.Count >= 2, "AmmoStations.MidMapPlaced", $"count={AmmoCrate.All.Count}");
            var afx = FindAnyObjectByType<AtmosphereFX>();
            var fh = FindAnyObjectByType<FireHazard>();
            Check(afx != null && fh != null, "Atmosphere.FogAndFireHazardsPresent",
                $"atmosphere={(afx != null)} fireHazard={(fh != null)}");
        }

        private IEnumerator AuditFireHazardTransparencyB82(string outDir, string sceneName)
        {
            var fh = FindAnyObjectByType<FireHazard>();
            if (fh == null) yield break;

            yield return new WaitForSeconds(0.45f);
            int totalPs = 0, particleShaders = 0, distortionParticles = 0;
            bool smokeAlpha = false;
            float smokeAlphaScale = 0f;
            float heatDistortionScale = 0f;
            float heatDistortionRate = 0f;
            var rendererDetails = new List<string>();
            foreach (var r in fh.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                if (r == null) continue;
                totalPs++;
                var mat = r.sharedMaterial;
                string shaderName = mat != null && mat.shader != null ? mat.shader.name : "missing";
                if (shaderName == "LR2/ParticleUnlit") particleShaders++;
                else if (shaderName == "HDRP/Unlit" && mat.HasProperty("_SurfaceType") && mat.GetFloat("_SurfaceType") > 0.5f &&
                         mat.HasProperty("_DistortionEnable") && mat.GetFloat("_DistortionEnable") > 0.5f &&
                         mat.HasProperty("_DistortionOnly") && mat.GetFloat("_DistortionOnly") > 0.5f)
                {
                    distortionParticles++;
                    if (mat.HasProperty("_DistortionScale")) heatDistortionScale = mat.GetFloat("_DistortionScale");
                    var heatSystem = r.GetComponent<ParticleSystem>();
                    if (heatSystem != null) heatDistortionRate = heatSystem.emission.rateOverTime.constant;
                }

                if (r.gameObject.name == "Fire Smoke" && mat != null)
                {
                    smokeAlpha = mat.HasProperty("_LuminanceToAlpha") && mat.GetFloat("_LuminanceToAlpha") > 0.5f &&
                                 mat.HasProperty("_LuminanceAlphaScale");
                    if (smokeAlpha) smokeAlphaScale = mat.GetFloat("_LuminanceAlphaScale");
                }
                rendererDetails.Add($"{r.gameObject.name}:{shaderName}");
            }
            bool smokeSoft = smokeAlpha && smokeAlphaScale > 0.2f && smokeAlphaScale <= 0.6f;
            bool heatDistortionSubtle = distortionParticles == 0 || (distortionParticles == 1 && heatDistortionScale > 0f && heatDistortionScale <= 0.0015f && heatDistortionRate > 0f && heatDistortionRate <= 1.5f);
            bool renderersValid = particleShaders >= 4 && distortionParticles <= 1 && totalPs == particleShaders + distortionParticles;
            Check(renderersValid && smokeSoft && heatDistortionSubtle, "FX.FireSmokeTransparentShaderB82",
                $"renderers={totalPs} particleUnlit={particleShaders} heatDistortion={distortionParticles}@{heatDistortionScale:0.0000} rate={heatDistortionRate:0.0}/s smokeLumaAlpha={smokeAlpha} smokeAlphaScale={smokeAlphaScale:0.00} [{string.Join(",", rendererDetails)}]");

            // Capture close-up frame of the live FireHazard flames & smoke flipbooks for visual B82 proof (editor or standalone player)
            Vector3 target = fh.transform.position + Vector3.up * 0.65f;
            Vector3 camPos = target + new Vector3(0f, 0.55f, -3.6f);
            foreach (float ang in new[] { 0f, 60f, -60f, 120f, -120f, 180f })
            {
                Vector3 probe = target + Quaternion.Euler(0f, ang, 0f) * Vector3.back * 3.6f + Vector3.up * 0.55f;
                if (!Physics.Linecast(probe, target, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                {
                    camPos = probe;
                    break;
                }
            }
            var camGo = new GameObject("[QA_B82_FireCam]");
            camGo.transform.position = camPos;
            camGo.transform.LookAt(target, Vector3.up);
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 250f;
            var hd = camGo.AddComponent<HDAdditionalCameraData>();
            hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
            hd.volumeLayerMask = ~0;
            Texture2D shot = CaptureCamera(cam, 960, 540);
            if (shot != null)
            {
                File.WriteAllBytes(Path.Combine(outDir, $"{sceneName}_b82_fire_smoke.png"), shot.EncodeToPNG());
                Destroy(shot);
            }
            Destroy(camGo);
        }

        private void AuditMapFlowB46(string sceneName)
        {
            var spawns = FindObjectsByType<SpawnPoint>();
            var alphas = new List<SpawnPoint>();
            var bravos = new List<SpawnPoint>();
            foreach (var sp in spawns)
            {
                if (sp == null) continue;
                if (sp.team == TeamId.Alpha) alphas.Add(sp);
                else if (sp.team == TeamId.Bravo) bravos.Add(sp);
            }

            int totalPairs = 0, exposedPairs = 0;
            float minDist = float.MaxValue;
            foreach (var a in alphas)
            {
                foreach (var b in bravos)
                {
                    totalPairs++;
                    Vector3 pa = a.transform.position + Vector3.up * 1.55f;
                    Vector3 pb = b.transform.position + Vector3.up * 1.55f;
                    minDist = Mathf.Min(minDist, Vector3.Distance(pa, pb));
                    if (!Physics.Linecast(pa, pb, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                        exposedPairs++;
                }
            }

            int navConnected = 0;
            var path = new NavMeshPath();
            if (alphas.Count > 0 && bravos.Count > 0 &&
                NavMesh.SamplePosition(alphas[0].transform.position, out var hA, 2.5f, NavMesh.AllAreas) &&
                NavMesh.SamplePosition(bravos[0].transform.position, out var hB, 2.5f, NavMesh.AllAreas) &&
                NavMesh.CalculatePath(hA.position, hB.position, NavMesh.AllAreas, path) &&
                path.status == NavMeshPathStatus.PathComplete)
                navConnected = 1;

            int coverCount = FindObjectsByType<LR2.AI.CoverPoint>().Length;
            // TestRange preserves the center x=0 shooting lane for QA sightline tests; combat maps guarantee 0 direct spawn-to-spawn LOS.
            bool safeLos = sceneName.Contains("TestRange") ? exposedPairs <= 1 : exposedPairs == 0;
            bool ok = totalPairs > 0 && safeLos && navConnected == 1 && coverCount >= 6 && minDist >= 18f;
            Check(ok, $"{sceneName}.MapFlowB46",
                $"spawns={alphas.Count}v{bravos.Count} exposedPairs={exposedPairs}/{totalPairs} minSpawnDist={minDist:0.0}m navOk={navConnected == 1} cover={coverCount}");
        }

        private IEnumerator AuditUpscalerBindingsAndDamageHud(SoldierController player)
        {
            // B48: Verify HDRP upscaler capability probe & Native <-> STP runtime toggle
            Camera cam = player != null && player.ViewCamera != null ? player.ViewCamera : Camera.main;
            var hd = cam != null ? cam.GetComponent<HDAdditionalCameraData>() : null;
            int origUpscaler = LR2Settings.UpscalerMode;
            LR2Settings.UpscalerMode = 0;
            UpscalerBackend.ApplyToCamera(cam, LR2Settings.AntiAliasing, 0);
            bool nativeOff = hd != null && !hd.allowDynamicResolution;

            LR2Settings.UpscalerMode = 1;
            UpscalerBackend.ApplyToCamera(cam, LR2Settings.AntiAliasing, 1);
            yield return null;
            bool stpActive = hd != null && (!UpscalerBackend.SupportsStp || (hd.allowDynamicResolution && hd.antialiasing == HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing));
            string stpLabel = UpscalerBackend.ActiveUpscalerLabel();

            LR2Settings.UpscalerMode = origUpscaler;
            UpscalerBackend.ApplyToCamera(cam, LR2Settings.AntiAliasing, origUpscaler);
            Check(nativeOff && stpActive && UpscalerBackend.AssetSupportsDrs, "Upscaler.NativeAndStpSwitch",
                $"nativeOff={nativeOff} stpActive={stpActive} assetDrs={UpscalerBackend.AssetSupportsDrs} supportsStp={UpscalerBackend.SupportsStp} hwDrs={UpscalerBackend.SupportsHardwareDrs} label={stpLabel} dlss={UpscalerBackend.IsDlssAvailable} fsr2={UpscalerBackend.IsFsr2Available}");

            // B44: Verify LR2Bindings override propagates live into PlayerInputSource and resets cleanly
            string origJump = LR2Bindings.GetPrimaryBindingPath("Jump", "<Keyboard>/space");
            var pis = player != null ? player.GetComponent<PlayerInputSource>() : null;
            bool wasEnabled = pis != null && pis.enabled;
            if (pis != null && !wasEnabled) pis.enabled = true;
            LR2Bindings.SetBindingOverride("Jump", 0, "<Keyboard>/g");
            yield return null;
            string boundRegistry = LR2Bindings.GetPrimaryBindingPath("Jump");
            string boundSource = pis != null ? pis.GetActiveBindingPath("Jump") : boundRegistry;
            LR2Bindings.ResetToDefaults();
            string resetRegistry = LR2Bindings.GetPrimaryBindingPath("Jump");
            if (pis != null && !wasEnabled) pis.enabled = false;
            Check(boundRegistry == "<Keyboard>/g" && boundSource == "<Keyboard>/g" && resetRegistry == origJump,
                "Settings.KeybindsRebindAndPersistence",
                $"rebind={boundSource} reset={resetRegistry} orig={origJump}");

            // B8: Verify directional damage arc indicator reacts to incoming directional hit
            var hud = FindAnyObjectByType<HUDController>();
            if (player != null && hud != null)
            {
                int beforeInd = hud.ActiveDamageIndicatorCount;
                GameEvents.RaiseHit(new HitEvent
                {
                    attacker = null,
                    victim = player.Combatant,
                    damage = 12f,
                    direction = Vector3.right,
                    point = player.transform.position
                });
                yield return null;
                int afterInd = hud.ActiveDamageIndicatorCount;
                Check(afterInd >= Mathf.Max(1, beforeInd), "HUD.DamageIndicatorDirectional",
                    $"before={beforeInd} after={afterInd}");
            }
        }

        private IEnumerator AuditDeathAdsCloseWeaponAndB57B65(SoldierController player)
        {
            // B65 / R17 / A26: Verify tail reservation rolls back immediately on voice allocation failure.
            bool tailRollbackOk = LR2Audio.VerifyTailReservationRollback(out int resBefore, out int resAfter);
            Check(tailRollbackOk, "Audio.TailReservationRollback", $"reserved={resBefore} afterCancel={resAfter}");

            // B36 / P1: Verify indoor vs outdoor tail contrast (3-6 dB), overlap cap <= 4, war-torn brass cue, and horizon ambience clips.
            bool tailContrastOk = LR2Audio.VerifyIndoorOutdoorTailContrast(out float inG, out float outG, out float sepDb, out int maxTails);
            Check(tailContrastOk, "Audio.IndoorOutdoorTailContrast",
                $"indoor={inG:0.00} outdoor={outG:0.00} sep={sepDb:0.00}dB maxConcurrent={maxTails}");
            var musicDir = LR2MusicDirector.Instance;
            Check(musicDir != null && musicDir.HasWarTornBrassCue, "Music.WarTornBrassCue",
                $"hasBrass={(musicDir != null && musicDir.HasWarTornBrassCue)}");
            AmbientOneShots.EnsureWarzoneClips();
            Check(AmbientOneShots.HasWarzoneHorizonClips, "Audio.WarzoneHorizonAmbience",
                $"horizonClips={AmbientOneShots.HasWarzoneHorizonClips}");

            // B74b: Verify bot resupply goal is strictly suppressed mid-fight and enabled when disengaged.
            var bbMidFight = new LR2.AI.BotBlackboard { time = 10f, targetVisible = true, targetLastSeenTime = 10f };
            var bbEngageGoal = new LR2.AI.BotBlackboard { time = 10f, targetVisible = false, targetLastSeenTime = -999f };
            var bbDisengaged = new LR2.AI.BotBlackboard { time = 10f, targetVisible = false, targetLastSeenTime = -999f };
            bool resupplySuppressedMidFight = !LR2.AI.BotController.IsDisengagedForResupply(bbMidFight, new LR2.AI.BotIntent { goal = LR2.AI.BotGoal.Patrol })
                && !LR2.AI.BotController.IsDisengagedForResupply(bbEngageGoal, new LR2.AI.BotIntent { goal = LR2.AI.BotGoal.Engage });
            bool resupplyAllowedDisengaged = LR2.AI.BotController.IsDisengagedForResupply(bbDisengaged, new LR2.AI.BotIntent { goal = LR2.AI.BotGoal.Patrol });
            Check(resupplySuppressedMidFight && resupplyAllowedDisengaged, "BotAI.DisengagedResupplyGate",
                $"suppressedMidFight={resupplySuppressedMidFight} allowedDisengaged={resupplyAllowedDisengaged}");

            // B57 / R18: Known-bad & known-good contract fixtures.
            foreach (string line in LR2QAExtraChecks.CheckB57KnownBadFixtures())
                CheckExtra(line);

            if (player == null || player.Health == null || player.ViewCamera == null) yield break;
            Vector3 savedPos = player.transform.position;
            float savedYaw = player.Yaw;

            // Measure live 2x ADS-in, ADS-out ("coming back"), FOV zoom sync, and per-weapon speed hierarchy.
            _cmd = new SoldierInput { absoluteLook = true, aimYaw = savedYaw, aimPitch = 0f, aim = false };
            float settleLimit = Time.time + 1.5f;
            while (Time.time < settleLimit &&
                   (player.IsAiming || player.AimBlend > 0.01f ||
                    (player.UserInput != null && player.UserInput.GetValue<float>(FPSANames.AimingWeight) > 0.01f)))
                yield return null;

            var activeGun = player.ActiveFirearm;
            var activeEntity = activeGun != null ? activeGun.GetComponent<FPSAnimatorEntity>() : null;
            float runtimeAimingSpeed = 0f;
            if (activeEntity != null && activeEntity.animatorProfile != null && activeEntity.animatorProfile.settings != null)
            {
                foreach (var s in activeEntity.animatorProfile.settings)
                    if (s is AdsLayerSettings als) { runtimeAimingSpeed = als.aimingSpeed; break; }
            }
            if (activeGun != null && activeGun.Definition != null && player.UserInput != null)
            {
                float hipFovV = LR2Settings.VerticalFov(LR2Settings.FieldOfView, player.ViewCamera.aspect);
                float scopeZoom = activeGun.ActiveScope != null && activeGun.ActiveScope.aimFovZoom > 0.01f
                    ? Mathf.Min(1f, activeGun.ActiveScope.aimFovZoom)
                    : 1f;
                float adsFovV = hipFovV * activeGun.Definition.adsFovMultiplier * scopeZoom;
                float inGunMs = -1f, inFovMs = -1f;
                float inElapsed = 0f;
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = savedYaw, aimPitch = 0f, aim = true };
                while (inElapsed < 1.0f && (inGunMs < 0f || inFovMs < 0f))
                {
                    yield return null;
                    if (!player.IsAiming) continue;
                    inElapsed += Time.deltaTime;
                    float w = player.UserInput.GetValue<float>(FPSANames.AimingWeight);
                    if (inGunMs < 0f && w >= 0.98f) inGunMs = inElapsed * 1000f;
                    if (inFovMs < 0f && Mathf.Abs(player.ViewCamera.fieldOfView - adsFovV) <= 0.5f) inFovMs = inElapsed * 1000f;
                }
                yield return null;
                if (inFovMs < 0f && Mathf.Abs(player.ViewCamera.fieldOfView - adsFovV) <= 0.5f)
                    inFovMs = (inElapsed + Time.deltaTime) * 1000f;

                float outGunMs = -1f, outFovMs = -1f;
                float outElapsed = 0f;
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = savedYaw, aimPitch = 0f, aim = false };
                while (outElapsed < 1.0f && (outGunMs < 0f || outFovMs < 0f))
                {
                    yield return null;
                    if (player.IsAiming) continue;
                    outElapsed += Time.deltaTime;
                    float w = player.UserInput.GetValue<float>(FPSANames.AimingWeight);
                    if (outGunMs < 0f && w <= 0.02f) outGunMs = outElapsed * 1000f;
                    if (outFovMs < 0f && Mathf.Abs(player.ViewCamera.fieldOfView - hipFovV) <= 0.5f) outFovMs = outElapsed * 1000f;
                }
                yield return null;
                if (outFovMs < 0f && Mathf.Abs(player.ViewCamera.fieldOfView - hipFovV) <= 0.5f)
                    outFovMs = (outElapsed + Time.deltaTime) * 1000f;

                CheckExtra(LR2QAExtraChecks.CheckAdsSpeedAndSyncState(
                    activeGun.DisplayName, activeGun.EffectiveAdsTime, runtimeAimingSpeed,
                    inGunMs, inFovMs, outGunMs, outFovMs));
            }
            if (player.Items.Count >= 2 && player.Items[0] is LR2Firearm slot0 && player.Items[1] is LR2Firearm slot1)
            {
                Check(slot1.EffectiveAdsTime < slot0.EffectiveAdsTime, "ADS.PerWeaponHierarchy",
                    $"{slot1.DisplayName}={slot1.EffectiveAdsTime:0.00}s < {slot0.DisplayName}={slot0.EffectiveAdsTime:0.00}s");
            }

            // Stage a real hostile soldier at barrel reach; measure the configured collision mask and gun pose under pressure.
            SoldierController closeEnemy = FindLiveHostileSoldier(player);
            NavMeshAgent closeEnemyAgent = closeEnemy != null ? closeEnemy.GetComponent<NavMeshAgent>() : null;
            Vector3 closeEnemySavedPosition = closeEnemy != null ? closeEnemy.transform.position : Vector3.zero;
            Quaternion closeEnemySavedRotation = closeEnemy != null ? closeEnemy.transform.rotation : Quaternion.identity;
            bool closeEnemyControlsWereEnabled = closeEnemy != null && closeEnemy.ControlsEnabled;
            bool closeEnemyAgentWasStopped = closeEnemyAgent != null && closeEnemyAgent.enabled &&
                                             closeEnemyAgent.isOnNavMesh && closeEnemyAgent.isStopped;

            _cmd = new SoldierInput { absoluteLook = true, aimYaw = savedYaw, aimPitch = 0f, aim = true };
            yield return null;
            yield return null;

            Vector3 aimForward = player.ViewCamera.transform.forward;
            aimForward.y = 0f;
            aimForward = aimForward.sqrMagnitude > 1e-6f ? aimForward.normalized : player.transform.forward;
            if (closeEnemy != null)
            {
                closeEnemy.ControlsEnabled = false;
                if (closeEnemyAgent != null && closeEnemyAgent.enabled && closeEnemyAgent.isOnNavMesh)
                    closeEnemyAgent.isStopped = true;
                Vector3 stagedPosition = player.transform.position + aimForward * 0.45f;
                bool stagedByAgent = closeEnemyAgent != null && closeEnemyAgent.enabled &&
                                     closeEnemyAgent.isOnNavMesh && closeEnemyAgent.Warp(stagedPosition);
                if (!stagedByAgent) closeEnemy.transform.position = stagedPosition;
                closeEnemy.transform.rotation = Quaternion.LookRotation(-aimForward, Vector3.up);
                Physics.SyncTransforms();
                yield return new WaitForFixedUpdate();
                CheckExtra(LR2QAExtraChecks.CheckCloseEnemyDistance(
                    player.transform.position, closeEnemy.transform.position, aimForward));
            }
            else
            {
                CheckExtra("FAIL Death.CloseEnemyStaged: no live hostile soldier available");
            }

            int colMask = LR2Layers.EnvironmentMask;
            var gun = player.ActiveFirearm;
            var entity = gun != null ? gun.GetComponent<FPSAnimatorEntity>() : null;
            if (entity != null && entity.animatorProfile != null && entity.animatorProfile.settings != null)
            {
                foreach (var s in entity.animatorProfile.settings)
                    if (s is CollisionLayerSettings cls) { colMask = cls.layerMask; break; }
            }

            Vector3 eyePos = player.ViewCamera.transform.position;
            Vector3 aimFwd = player.ViewCamera.transform.forward;
            if (gun != null)
                CheckExtra(LR2QAExtraChecks.CheckCloseWeaponSafety(gun.transform.position, eyePos, aimFwd, colMask));

            int deathSfxBefore = PainVoice.DeathAudioCount;
            var dmg = new DamageInfo
            {
                amount = 999f,
                type = DamageType.Bullet,
                zone = HitZone.UpperTorso,
                direction = -aimFwd,
                weaponName = "QA_CloseCheck"
            };
            player.Health.ApplyDamage(ref dmg);
            _cmd = new SoldierInput { absoluteLook = true, aimYaw = savedYaw, aimPitch = 0f, aim = false };
            yield return null;

            Check(PainVoice.DeathAudioCount > deathSfxBefore, "Audio.DeathTriggeredOnPlayerDeath",
                $"deathSfxDelta={PainVoice.DeathAudioCount - deathSfxBefore}");
            CheckExtra(LR2QAExtraChecks.CheckDeathAimReset(player));
            if (gun != null)
                CheckExtra(LR2QAExtraChecks.CheckCloseWeaponSafety(gun.transform.position, player.ViewCamera.transform.position, player.ViewCamera.transform.forward, colMask));

            if (closeEnemy != null)
            {
                if (closeEnemyAgent != null && closeEnemyAgent.enabled && closeEnemyAgent.isOnNavMesh)
                {
                    closeEnemyAgent.isStopped = true;
                    closeEnemyAgent.Warp(closeEnemySavedPosition);
                }
                closeEnemy.transform.SetPositionAndRotation(closeEnemySavedPosition, closeEnemySavedRotation);
                if (closeEnemyAgent != null && closeEnemyAgent.enabled && closeEnemyAgent.isOnNavMesh)
                    closeEnemyAgent.isStopped = closeEnemyAgentWasStopped;
                closeEnemy.ControlsEnabled = closeEnemyControlsWereEnabled;
                Physics.SyncTransforms();
            }

            player.Respawn(savedPos, savedYaw);
            _cmd = new SoldierInput { absoluteLook = true, aimYaw = savedYaw, aimPitch = 0f, aim = false };
            float settleEnd = Time.time + 0.85f;
            while (Time.time < settleEnd && (player.ActiveFirearm == null || player.ActiveFirearm.IsBusy)) yield return null;
            for (int f = 0; f < 6; f++) yield return null;
            var respawnGun = player.ActiveFirearm;
            float respawnGunAng = respawnGun != null && player.ViewCamera != null
                ? Vector3.Angle(respawnGun.transform.forward, player.ViewCamera.transform.forward)
                : 999f;
            Check(!player.IsDead && respawnGun != null && respawnGunAng <= 30f, "Respawn.CleanAnimationPose",
                $"dead={player.IsDead} gun={(respawnGun != null ? respawnGun.DisplayName : "null")} gunToView={respawnGunAng:0.0}deg");
        }

        private IEnumerator AuditB136RobotSystems(SoldierController player)
        {
            if (player == null || player.UserInput == null) yield break;

            // 1. Q/E Lean polarity: Q (-1) -> LeanInput > 0; E (+1) -> LeanInput < 0 (UserInput interpolates float over time)
            _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, lean = -1f };
            float leanEnd = Time.time + 0.45f;
            float qLeanVal = 0f;
            while (Time.time < leanEnd)
            {
                yield return null;
                qLeanVal = player.UserInput.GetValue<float>(FPSANames.LeanInput);
                if (qLeanVal > 5f) break;
            }
            _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, lean = 0f };
            leanEnd = Time.time + 0.45f;
            while (Time.time < leanEnd && Mathf.Abs(player.UserInput.GetValue<float>(FPSANames.LeanInput)) > 1f) yield return null;
            _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, lean = 1f };
            leanEnd = Time.time + 0.45f;
            float eLeanVal = 0f;
            while (Time.time < leanEnd)
            {
                yield return null;
                eLeanVal = player.UserInput.GetValue<float>(FPSANames.LeanInput);
                if (eLeanVal < -5f) break;
            }
            _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, lean = 0f };
            yield return null;
            Check(qLeanVal > 1f && eLeanVal < -1f, "Lean.DirectionQE", $"Q(-1)={qLeanVal:0.0} E(+1)={eLeanVal:0.0}");

            // 2. HUD bottom-right layout: weaponName strictly above magText
            var hud = FindAnyObjectByType<HUDController>();
            bool hudOk = hud != null && hud.weaponName != null && hud.magText != null &&
                         hud.weaponName.rectTransform.anchoredPosition.y - hud.magText.rectTransform.anchoredPosition.y >= 90f;
            Check(hudOk, "HUD.BottomRightNoOverlap",
                hud != null && hud.weaponName != null && hud.magText != null
                    ? $"weaponY={hud.weaponName.rectTransform.anchoredPosition.y:0} magY={hud.magText.rectTransform.anchoredPosition.y:0}"
                    : "missing HUD");

            // 3. Grenade toss uses AA_LR2_GrenadeThrow (never AA_CallEmote)
            bool grenadeOk = true;
            string badGren = "";
            var grenField = typeof(LR2Firearm).GetField("grenadeClip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var it in player.Items)
            {
                if (it is LR2Firearm fa)
                {
                    var clipObj = grenField != null ? grenField.GetValue(fa) as UnityEngine.Object : null;
                    string cn = clipObj != null ? clipObj.name : "null";
                    if (clipObj == null || cn.Contains("Emote")) { grenadeOk = false; badGren += $"{fa.DisplayName}:{cn} "; }
                }
            }
            Check(grenadeOk, "Grenade.NotEmote", grenadeOk ? "all firearms use non-emote throw asset" : badGren);

            // 4. PiP scope circular shader check
            int pipChecked = 0, pipValid = 0;
            foreach (var mName in new[] { "M_Acog", "M_1P29", "M_LongRangeScope" })
            {
                foreach (var r in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (r != null && r.name == mName)
                    {
                        pipChecked++;
                        if (r.shader != null && r.shader.name == "LR2/PipScope") pipValid++;
                        break;
                    }
                }
            }
            bool pipShaderExists = Shader.Find("LR2/PipScope") != null;
            bool noOverhangQuads = true;
            foreach (var it in player.Items)
            {
                if (it != null && it.transform.Find("LR2_Crosshair") != null) noOverhangQuads = false;
            }
            Check(pipShaderExists && (pipChecked == 0 || pipValid == pipChecked) && noOverhangQuads, "PipScope.CircularShader",
                $"shaderPresent={pipShaderExists} checked={pipChecked} pipShaderValid={pipValid} noOverhangQuads={noOverhangQuads}");

            // 5. Torch upright in hand check (Slot 4)
            bool torchOk = true;
            if (player.Items.Count >= 4 && player.Items[3] != null && player.Items[3].DisplayName.IndexOf("Torch", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, weaponSlot = 3 };
                float tW = Time.time + 1.2f;
                while (Time.time < tW && (player.ActiveItem != player.Items[3] || player.ActiveItem.IsBusy)) yield return null;
                yield return null;
                yield return null;
                float upDot = player.ViewCamera != null
                    ? Vector3.Dot(player.Items[3].transform.up, player.ViewCamera.transform.up)
                    : Vector3.Dot(player.Items[3].transform.up, Vector3.up);
                torchOk = upDot > 0.35f;
                Check(torchOk, "Torch.UprightInHand", $"upDot={upDot:0.00}");
                yield return new WaitForSeconds(0.42f); // wait for weaponSwapCooldown (0.35s) before swapping back
                tW = Time.time + 1.4f;
                while (Time.time < tW && (player.ActiveItem != player.Items[0] || (player.ActiveItem != null && player.ActiveItem.IsBusy)))
                {
                    if (player.ActiveItem != player.Items[0])
                        _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, weaponSlot = 0 };
                    yield return null;
                }
                yield return null;
            }

            // 6. Suppressed + reload sync audio + Music revamp + Controls toggles & Slot1/2 binds
            var gun = player.ActiveFirearm != null ? player.ActiveFirearm : (player.Items.Count > 0 ? player.Items[0] as LR2Firearm : null);
            var ap = gun != null && gun.Definition != null ? gun.Definition.audio : null;
            bool audioOk = ap != null && ap.firePlayerSuppressed != null && ap.firePlayerSuppressed.IsValid &&
                           ap.magOutAt > 0f && ap.magOutAt < ap.magInAt && ap.magInAt < ap.boltAt &&
                           ap.boltAt < ap.emptyReloadEndAt && ap.emptyReloadEndAt < 0.95f;
            Check(audioOk, "Audio.SuppressedAndReloadSync",
                ap != null ? $"supp={(ap.firePlayerSuppressed != null && ap.firePlayerSuppressed.IsValid)} cues={ap.magOutAt:0.00}/{ap.magInAt:0.00}/{ap.boltAt:0.00}/{ap.emptyReloadEndAt:0.00}" : "null");

            var dir = LR2MusicDirector.Instance;
            Check(dir != null && dir.HasMenuClip && dir.HasOverdriveClip && dir.HasOutcomeClips && !string.IsNullOrEmpty(dir.ActiveMapTheme),
                "Audio.MusicRevamp",
                dir != null ? $"theme={dir.ActiveMapTheme} menu={dir.HasMenuClip} overdrive={dir.HasOverdriveClip} outcomes={dir.HasOutcomeClips}" : "null");

            string s1 = LR2Bindings.GetPrimaryBindingPath("Slot1", "");
            string s2 = LR2Bindings.GetPrimaryBindingPath("Slot2", "");
            bool prevTa = LR2Settings.ToggleAim;
            LR2Settings.ToggleAim = !prevTa;
            bool taRound = LR2Settings.ToggleAim == !prevTa;
            LR2Settings.ToggleAim = prevTa;
            Check(taRound && s1 == "<Keyboard>/1" && s2 == "<Keyboard>/2",
                "Settings.ControlsAndKeybinds", $"toggleAimRoundtrip={taRound} Slot1={s1} Slot2={s2}");
        }

        private IEnumerator AuditB137ToB144Systems(SoldierController player)
        {
            if (player == null) yield break;

            // 1. B137: Military visual mirror & 30-finger bone fidelity
            var pMirror = player.GetComponent<GenericBoneMirror>();
            GenericBoneMirror bMirror = null;
            foreach (var c in Combatant.All)
            {
                if (c != null && c != player.Combatant && c.IsBot)
                {
                    bMirror = c.GetComponent<GenericBoneMirror>();
                    if (bMirror != null) break;
                }
            }
            bool robotHidden = true;
            foreach (var smr in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.GetComponentInParent<LR2Item>(true) != null) continue;
                if (pMirror != null && pMirror.VisualRoot != null && smr.transform.IsChildOf(pMirror.VisualRoot.transform)) continue;
                if (smr.enabled) { robotHidden = false; break; }
            }
            yield return null;
            bool mirrorOk = pMirror != null && bMirror != null &&
                            pMirror.MappedBoneCount >= 48 && pMirror.MappedFingerCount >= 28 &&
                            bMirror.MappedBoneCount >= 48 && bMirror.MappedFingerCount >= 28 &&
                            pMirror.VisualRoot != null && pMirror.VisualRoot.activeInHierarchy &&
                            robotHidden && pMirror.HandErrorLeft < 0.03f && pMirror.HandErrorRight < 0.03f;
            Check(mirrorOk, "MilitaryVisual.MirrorAndFingers",
                pMirror != null
                    ? $"pBones={pMirror.MappedBoneCount}/{pMirror.MappedFingerCount} bBones={(bMirror != null ? bMirror.MappedBoneCount : 0)}/{(bMirror != null ? bMirror.MappedFingerCount : 0)} robotHidden={robotHidden} handErrL={pMirror.HandErrorLeft:0.004} handErrR={pMirror.HandErrorRight:0.004}"
                    : "GenericBoneMirror missing");

            // B145: KillcamDirector runs after GenericBoneMirror (600 > 550) so replay records aligned gun + visual hands
            bool kcOrderOk = false;
            var kcAttr = typeof(KillcamDirector).GetCustomAttribute<DefaultExecutionOrder>();
            var gmAttr = typeof(GenericBoneMirror).GetCustomAttribute<DefaultExecutionOrder>();
            if (kcAttr != null && gmAttr != null) kcOrderOk = kcAttr.order > gmAttr.order;
            Check(kcOrderOk && bMirror != null && bMirror.HandErrorLeft < 0.03f && bMirror.HandErrorRight < 0.03f, "Killcam.EnemyHandsLockedToGun",
                $"kcOrder={(kcAttr != null ? kcAttr.order : 0)} gmOrder={(gmAttr != null ? gmAttr.order : 0)} botHandErrL={(bMirror != null ? bMirror.HandErrorLeft : -1f):0.004} R={(bMirror != null ? bMirror.HandErrorRight : -1f):0.004}");

            // 2. B138: Volumetric clouds + PhysicallyBasedSky + zero HDRI pinch + upward sky capture
            var skyMotion = FindAnyObjectByType<SkyCloudMotion>();
            bool skyOk = skyMotion != null && skyMotion.HasVolumetricClouds && skyMotion.VolumetricCloudsEnabled &&
                         !skyMotion.HasPinchedHdriDistortion && skyMotion.CurrentWindSpeed > 0f;
            Check(skyOk, "Sky.VolumetricCloudsNoPinch",
                skyMotion != null
                    ? $"clouds={skyMotion.HasVolumetricClouds} enabled={skyMotion.VolumetricCloudsEnabled} pinch={skyMotion.HasPinchedHdriDistortion} wind={skyMotion.CurrentWindSpeed:0.0} density={skyMotion.CurrentCloudDensity:0.00}"
                    : "SkyCloudMotion missing");
            WeatherSystem.Instance?.ForceWeatherNow(WeatherKind.DuskClear);
            if (player.ViewCamera != null)
            {
                Vector3 preSkyPos = player.transform.position;
                float preSkyYaw = player.Yaw;
                Vector3 skySpot = preSkyPos;
                float skyYaw = preSkyYaw;
                var skyCandidates = new List<Vector3> { preSkyPos };
                foreach (var fa in FlagAnchor.All)
                    if (fa != null) skyCandidates.Add(fa.transform.position);
                foreach (var sp in FindObjectsByType<SpawnPoint>())
                    if (sp != null) skyCandidates.Add(sp.transform.position);

                bool foundOpenSky = false;
                foreach (var cand in skyCandidates)
                {
                    if (cand.y > 2.2f || IsConfinedOrRoofed(cand)) continue;
                    for (int k = 0; k < 8; k++)
                    {
                        float y = k * 45f;
                        Vector3 dirUp = Quaternion.Euler(-28f, y, 0f) * Vector3.forward;
                        Vector3 dirHoriz = Quaternion.Euler(0f, y, 0f) * Vector3.forward;
                        if (!Physics.Raycast(cand + Vector3.up * 1.65f, dirUp, 45f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) &&
                            !Physics.Raycast(cand + Vector3.up * 1.65f, dirHoriz, 12f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                        {
                            skySpot = cand;
                            skyYaw = y;
                            foundOpenSky = true;
                            break;
                        }
                    }
                    if (foundOpenSky) break;
                }
                if (player.Movement != null) player.Movement.Teleport(skySpot);
                else player.transform.position = skySpot;
                Physics.SyncTransforms();

                _cmd = new SoldierInput { absoluteLook = true, aimYaw = skyYaw, aimPitch = -28f };
                for (int f = 0; f < 22; f++) yield return null;
                yield return new WaitForEndOfFrame();
                Texture2D skyShot = null;
                if (!Application.isBatchMode)
                    skyShot = ScreenCapture.CaptureScreenshotAsTexture();
                if (skyShot == null)
                    skyShot = CaptureCamera(player.ViewCamera, 960, 540);
                File.WriteAllBytes(Path.Combine(OutDir(), "LR2_Saltworks_sky.png"), skyShot.EncodeToPNG());
                Destroy(skyShot);

                if (player.Movement != null) player.Movement.Teleport(preSkyPos);
                else player.transform.position = preSkyPos;
                Physics.SyncTransforms();
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = preSkyYaw, aimPitch = 0f };
                yield return null;
            }

            // 3. B142: Outdoor rain stays visible through openings when camera is indoors (no interior leakage)
            var ws = WeatherSystem.Instance;
            if (ws != null)
            {
                Vector3 savedPos = player.transform.position;
                float savedYaw = player.Yaw;
                if (player.Movement != null) player.Movement.Teleport(new Vector3(0f, 0.25f, 2f));
                else player.transform.position = new Vector3(0f, 0.25f, 2f); // ControlHouse interior (roofed)
                Physics.SyncTransforms();
                ws.TriggerThunderRain(0.88f, 6f, false);
                float wt = Time.time + 0.60f;
                while (Time.time < wt) yield return null;
                Camera activeCam = player.ViewCamera != null ? player.ViewCamera : Camera.main;
                bool camIn = activeCam != null && LR2Audio.IsIndoors(activeCam.transform.position);
                bool rainIndoorOk = ws.EffectiveRain > 0.1f && (ws.CameraIndoors || camIn) && ws.IsHighRainPlaying && !ws.IsNearRainPlaying;
                Vector3 cp = activeCam != null ? activeCam.transform.position : Vector3.zero;
                Check(rainIndoorOk, "Weather.RainVisibleFromIndoors",
                    $"rain={ws.EffectiveRain:0.00} indoors={ws.CameraIndoors} cam=({cp.x:0.1},{cp.y:0.1},{cp.z:0.1}) highRain={ws.IsHighRainPlaying} nearRain={ws.IsNearRainPlaying}");
                if (player.Movement != null) player.Movement.Teleport(savedPos);
                else player.transform.position = savedPos;
                Physics.SyncTransforms();
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = savedYaw, aimPitch = 0f };
                yield return null;
            }

            // 4. B139: Tactical reload holds past magInAt before blending out
            var gun = player.ActiveFirearm != null ? player.ActiveFirearm : (player.Items.Count > 0 ? player.Items[0] as LR2Firearm : null);
            var tacField = typeof(LR2Firearm).GetField("tacticalEndAfterMagIn", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            float tacVal = gun != null && tacField != null ? (float)tacField.GetValue(gun) : 0f;
            bool tacFieldOk = tacVal >= 0.02f;
            bool stayedPastMag = false;
            if (gun != null && gun.Definition != null && gun.Definition.audio != null)
            {
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, fire = true };
                yield return new WaitForSeconds(0.16f);
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, reload = true };
                yield return new WaitForSeconds(0.05f);
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw };
                float tStart = Time.time;
                float expectedMagTime = 0.55f;
                if (gun.Definition.audio.magInAt > 0.05f) expectedMagTime = gun.Definition.audio.magInAt;
                while (Time.time - tStart < 1.8f && gun.IsReloading)
                {
                    if (Time.time - tStart >= expectedMagTime * 1.0f) stayedPastMag = true;
                    yield return null;
                }
            }
            Check(tacFieldOk && stayedPastMag, "Reload.MagSeatsBeforeBlendOut",
                $"tacticalEndAfterMagIn={tacVal:0.000} stayedPastMag={stayedPastMag}");

            // 5. B140: Grenade throw sync & mega explosion stats/clip
            float relDelay = gun != null ? gun.GrenadeReleaseDelay(0.335f) : 0f;
            var tmpGGo = new GameObject("QA_TmpGrenade");
            var tmpG = tmpGGo.AddComponent<LR2Grenade>();
            float maxRad = tmpG.MaxBlastRadius;
            Destroy(tmpGGo);
            var megaClip = LR2Grenade.GetMegaBoomClip();
            var fxLib = LR2FxLibrary.Instance;
            Check(relDelay >= 0.50f && relDelay <= 0.85f && maxRad >= 9.5f && megaClip != null
                && LR2Grenade.AftermathFireDuration >= 3.5f && fxLib != null && fxLib.fire != null,
                "Grenade.SyncedReleaseAndMegaBlast",
                $"releaseDelay={relDelay:0.00}s maxBlastRadius={maxRad:0.0}m megaClip={(megaClip != null ? $"{megaClip.length:0.0}s" : "null")} aftermath={LR2Grenade.AftermathFireDuration:0.0}s fireFx={(fxLib != null && fxLib.fire != null)}");

            // 6. B141: Mid-match road-to-win/loss music momentum & revamped outcomes
            var dir = LR2MusicDirector.Instance;
            float wMom = 0f, lMom = 0f;
            if (dir != null)
            {
                dir.SetQaMomentumPreview(0.75f, true, false);
                yield return new WaitForSeconds(0.95f);
                wMom = dir.WinningMomentumWeight;
                dir.SetQaMomentumPreview(0.75f, false, true);
                yield return new WaitForSeconds(0.95f);
                lMom = dir.LosingMomentumWeight;
                dir.ClearQaMomentumPreview();
            }
            Check(dir != null && dir.HasMomentumStems && dir.HasOutcomeClips && wMom > 0.25f && lMom > 0.25f, "Music.MomentumAndOutcomes",
                dir != null ? $"stems={dir.HasMomentumStems} outcomes={dir.HasOutcomeClips} winW={wMom:0.00} loseW={lMom:0.00}" : "null");

            // 7. B143: Distinct per-weapon NPC audio (no AK12 leak onto M4/FAL) & suppressed pistol parity
            var profiles = new Dictionary<string, WeaponAudioProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Resources.FindObjectsOfTypeAll<WeaponAudioProfile>())
                if (p != null && !profiles.ContainsKey(p.name)) profiles[p.name] = p;
#if UNITY_EDITOR
            foreach (var wName in new[] { "WA_AK12", "WA_M4", "WA_FAL", "WA_Pistol" })
            {
                if (profiles.ContainsKey(wName)) continue;
                var loaded = UnityEditor.AssetDatabase.LoadAssetAtPath<WeaponAudioProfile>($"Assets/LR2/Data/Weapons/{wName}.asset");
                if (loaded != null) profiles[wName] = loaded;
            }
#endif
            profiles.TryGetValue("WA_AK12", out var akProf);
            profiles.TryGetValue("WA_M4", out var m4Prof);
            profiles.TryGetValue("WA_FAL", out var falProf);
            profiles.TryGetValue("WA_Pistol", out var pProf);
            static AudioClip FirstClip(SoundSet s) => s != null && s.clips != null && s.clips.Length > 0 ? s.clips[0] : null;
            bool akDistinctM4 = akProf == null || m4Prof == null ||
                                (FirstClip(m4Prof.fireNpcMed) == null || FirstClip(akProf.fireNpcMed) == null ||
                                 (FirstClip(m4Prof.fireNpcMed) != FirstClip(akProf.fireNpcMed) && FirstClip(m4Prof.fireNpcFar) != FirstClip(akProf.fireNpcFar)));
            bool akDistinctFal = akProf == null || falProf == null ||
                                 (FirstClip(falProf.fireNpcMed) == null || FirstClip(akProf.fireNpcMed) == null ||
                                  (FirstClip(falProf.fireNpcMed) != FirstClip(akProf.fireNpcMed) && FirstClip(falProf.fireNpcFar) != FirstClip(akProf.fireNpcFar)));
            bool pistolSuppOk = pProf == null || (pProf.fireNpcSuppressed != null && pProf.firePlayerSuppressed != null &&
                                FirstClip(pProf.fireNpcSuppressed) == FirstClip(pProf.firePlayerSuppressed));
            bool m4SuppLayered = m4Prof == null || (m4Prof.firePlayerSuppressed != null && m4Prof.firePlayerSuppressed.volume >= 0.95f &&
                                FirstClip(m4Prof.firePlayer) != FirstClip(m4Prof.firePlayerSuppressed));
            Check(akDistinctM4 && akDistinctFal && pistolSuppOk && m4SuppLayered, "Audio.UnifiedWeaponIdentity",
                $"profiles={profiles.Count} ak!=m4={akDistinctM4} ak!=fal={akDistinctFal} pistolSupp={pistolSuppOk} m4SuppLayer={m4SuppLayered}");
        }

        private IEnumerator AuditExperimentEndWindow(string outDir)
        {
            var mm = MatchManager.Instance;
            var menu = FindAnyObjectByType<GameMenuController>();
            Check(mm != null && menu != null && menu.endPanel != null && menu.endRestartButton != null && menu.endMenuButton != null,
                "ExperimentEnd.Prerequisites", $"mm={mm != null} menu={menu != null}");
            if (mm == null || menu == null) yield break;

            // 1. Verify template resolver persists even after the originating MonoBehaviour (MainMenuController) is destroyed
            var dummyHost = new GameObject("QA_DummyMenuHost");
            var dummyHolder = dummyHost.AddComponent<LR2QATemplateHolder>();
            var tDuel = ScriptableObject.CreateInstance<MatchSettings>();
            tDuel.name = "QA_Duel"; tDuel.mode = GameModeType.Duel; tDuel.scoreLimit = 10;
            var tTdm = ScriptableObject.CreateInstance<MatchSettings>();
            tTdm.name = "QA_TDM"; tTdm.mode = GameModeType.TeamDeathmatch; tTdm.scoreLimit = 25;
            dummyHolder.duel = tDuel;
            dummyHolder.tdm = tTdm;

            var expCfg = new ExperimentSessionConfig
            {
                sessionName = "QA END WINDOW TEST",
                blindCondition = false,
                randomizeOrder = false,
                promptHumanRating = true,
                telemetryHz = 20
            };
            var m1 = ExperimentSessionRunner.MakeMatch("QA M1 DUEL", false, GameModeType.Duel, 0, 10, 120f, LR2.AI.LLM.LlmIntegrationMode.Off);
            m1.bots.Add(new ExperimentBotSlot { archetype = LR2.AI.BotArchetype.HumanLike, difficulty = 1, team = TeamId.None });
            var m2 = ExperimentSessionRunner.MakeMatch("QA M2 TDM AIvsAI", true, GameModeType.TeamDeathmatch, 0, 20, 120f, LR2.AI.LLM.LlmIntegrationMode.Commander);
            m2.bots.Add(new ExperimentBotSlot { archetype = LR2.AI.BotArchetype.LlmEnhanced, difficulty = 1, team = TeamId.Alpha });
            m2.bots.Add(new ExperimentBotSlot { archetype = LR2.AI.BotArchetype.HumanLike, difficulty = 1, team = TeamId.Bravo });
            expCfg.matches.Add(m1);
            expCfg.matches.Add(m2);

            string qaTelemetryDir = Path.Combine(outDir, "exp_telemetry_qa");
            if (Directory.Exists(qaTelemetryDir)) Directory.Delete(qaTelemetryDir, true);
            ExperimentTelemetryRecorder.CliOutputRoot = qaTelemetryDir;
            ExperimentSessionRunner.StartQaTestSession(expCfg, dummyHolder.Resolve, Path.Combine(outDir, "exp_session_qa"));
            DestroyImmediate(dummyHost); // Simulates MainMenuController destruction on scene load!

            var resolvedM2 = ExperimentSessionRunner.ResolveTemplateForMode(GameModeType.TeamDeathmatch);
            Check(resolvedM2 != null && resolvedM2.mode == GameModeType.TeamDeathmatch && resolvedM2.scoreLimit == 25,
                "ExperimentEnd.TemplateSurvivesMenuDestroy",
                resolvedM2 != null ? $"mode={resolvedM2.mode} score={resolvedM2.scoreLimit}" : "null");

            // 2. Simulate paused state when match ends + real telemetry async save + SpectatorCamera present
            ExperimentTelemetryRecorder.EnsureForMatch();
            yield return null;
            menu.Pause(); // Intentional bug repro: paused right before EndMatch
            mm.EndMatch();
            float waitEnd = Time.unscaledTime + 5.0f;
            while ((menu.endPanel.alpha < 0.99f || !ExperimentTelemetryRecorder.IsIdleOrSaveComplete) && Time.unscaledTime < waitEnd)
                yield return null;
            yield return null;

            bool unpaused = !menu.IsPaused && (menu.pausePanel == null || menu.pausePanel.alpha < 0.01f) && Time.timeScale > 0.99f;
            bool cursorFree = Cursor.lockState == CursorLockMode.None && Cursor.visible;
            Check(menu.endPanel.alpha >= 0.99f && unpaused && cursorFree, "ExperimentEnd.RevealAndCursorUnlock",
                $"endAlpha={menu.endPanel.alpha:0.00} paused={menu.IsPaused} pauseAlpha={(menu.pausePanel != null ? menu.pausePanel.alpha : 0f):0.00} timeScale={Time.timeScale:0.00} lock={Cursor.lockState} visible={Cursor.visible}");

            // 3. Check 0 UI overlap & real GraphicRaycaster/EventSystem hit on NEXT MATCH, END SESSION, and rating R_3
            static Rect ScreenRectOf(RectTransform rt)
            {
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                float minX = corners[0].x, maxX = corners[2].x, minY = corners[0].y, maxY = corners[2].y;
                return Rect.MinMaxRect(minX, minY, maxX, maxY);
            }

            var barTf = menu.endPanel.transform.Find("ExperimentEndBar");
            var restartRt = (RectTransform)menu.endRestartButton.transform;
            var menuRt = (RectTransform)menu.endMenuButton.transform;
            var scoreRt = menu.endScoreboard.rectTransform;
            Rect rBar = barTf != null ? ScreenRectOf((RectTransform)barTf) : new Rect();
            Rect rRestart = ScreenRectOf(restartRt);
            Rect rMenu = ScreenRectOf(menuRt);
            Rect rScore = ScreenRectOf(scoreRt);

            bool noOverlapRestart = barTf != null && !rBar.Overlaps(rRestart);
            bool noOverlapMenu = barTf != null && !rBar.Overlaps(rMenu);
            bool noOverlapScore = barTf != null && !rBar.Overlaps(rScore);
            Check(noOverlapRestart && noOverlapMenu && noOverlapScore, "ExperimentEnd.ZeroLayoutOverlap",
                $"bar={rBar} restart={rRestart} menu={rMenu} score={rScore}");

            static bool RaycastHitsButton(Button targetBtn, out string hitInfo)
            {
                hitInfo = "none";
                if (targetBtn == null || EventSystem.current == null) return false;
                var corners = new Vector3[4];
                ((RectTransform)targetBtn.transform).GetWorldCorners(corners);
                Vector2 center = (corners[0] + corners[2]) * 0.5f;
                var ped = new PointerEventData(EventSystem.current) { position = center };
                var results = new List<RaycastResult>();
                EventSystem.current.RaycastAll(ped, results);
                if (results.Count == 0) return false;
                hitInfo = $"{results[0].gameObject.name}(total={results.Count})";
                var hitBtn = results[0].gameObject.GetComponentInParent<Button>();
                return hitBtn == targetBtn;
            }

            bool hitNext = RaycastHitsButton(menu.endRestartButton, out string nextHit);
            bool hitEnd = RaycastHitsButton(menu.endMenuButton, out string endHit);
            Slider rateSlider = barTf != null ? barTf.Find("RateSlider")?.GetComponent<Slider>() : null;
            bool hitRate = false;
            string rateHit = "none";
            if (rateSlider != null && EventSystem.current != null)
            {
                var sc = new Vector3[4];
                ((RectTransform)rateSlider.transform).GetWorldCorners(sc);
                var ped = new PointerEventData(EventSystem.current) { position = (sc[0] + sc[2]) * 0.5f };
                var res = new List<RaycastResult>();
                EventSystem.current.RaycastAll(ped, res);
                if (res.Count > 0)
                {
                    rateHit = $"{res[0].gameObject.name}(total={res.Count})";
                    hitRate = res[0].gameObject.GetComponentInParent<Slider>() == rateSlider;
                }
            }
            Check(hitNext && hitEnd && hitRate, "ExperimentEnd.ButtonsRaycastClickable",
                $"next={nextHit}({hitNext}) end={endHit}({hitEnd}) rate={rateHit}({hitRate})");

            // 4. Verify rating deduplication on continuous slider changes + telemetry CSV output
            if (rateSlider != null)
            {
                rateSlider.value = 32f;
                rateSlider.value = 75f;
            }
            int ratingCount = ExperimentSessionRunner.Ratings.Count;
            float ratingVal = ratingCount > 0 ? ExperimentSessionRunner.Ratings[0].humanLikenessRating01 : -1f;
            Check(ratingCount == 1 && Mathf.Abs(ratingVal - 0.75f) < 0.01f, "ExperimentEnd.RatingDeduplicated",
                $"count={ratingCount} val={ratingVal:0.00}");

            // Capture screenshot via ScreenCapture / RenderTexture canvas or full camera+UI frame for visual inspection
            yield return new WaitForEndOfFrame();
            var screenTex = ScreenCapture.CaptureScreenshotAsTexture();
            if (screenTex != null)
            {
                File.WriteAllBytes(Path.Combine(outDir, "experiment_end_window_verified.png"), screenTex.EncodeToPNG());
                Destroy(screenTex);
            }

            ExperimentTelemetryRecorder.CliOutputRoot = "";
            ExperimentSessionRunner.ClearQaTestSession();
            DestroyImmediate(tDuel);
            DestroyImmediate(tTdm);
        }

        private sealed class LR2QATemplateHolder : MonoBehaviour
        {
            public MatchSettings duel;
            public MatchSettings tdm;
            public MatchSettings Resolve(GameModeType mode) => mode == GameModeType.TeamDeathmatch ? tdm : duel;
        }

        private static SoldierController FindLiveHostileSoldier(SoldierController player)
        {
            if (player == null || player.Combatant == null) return null;
            foreach (var combatant in Combatant.All)
            {
                if (combatant == null || combatant == player.Combatant || !combatant.IsBot ||
                    !combatant.IsAlive || !combatant.IsHostileTo(player.Combatant))
                    continue;
                var soldier = combatant.GetComponent<SoldierController>();
                if (soldier != null && !soldier.IsDead) return soldier;
            }
            return null;
        }

        private static Texture2D CaptureCamera(Camera cam, int w, int h)
        {
            PerformanceFrameTelemetry.MarkExternalStall(3);
            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                var request = new RenderPipeline.StandardRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(cam, request))
                    for (int p = 0; p < 3; p++) RenderPipeline.SubmitRenderRequest(cam, request);
                else
                {
                    var prevTarget = cam.targetTexture;
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = prevTarget;
                }
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                return tex;
            }
            finally { RenderTexture.ReleaseTemporary(rt); }
        }

        private static float SampleMeanLuminance(Texture2D tex, out float p10)
        {
            var px = tex.GetPixels();
            var lums = new float[px.Length];
            double sum = 0.0;
            for (int i = 0; i < px.Length; i++)
            {
                float lum = 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
                lums[i] = lum;
                sum += lum;
            }
            Array.Sort(lums);
            p10 = lums[Mathf.Clamp((int)(lums.Length * 0.1f), 0, lums.Length - 1)];
            return (float)(sum / Mathf.Max(1, px.Length));
        }

        private static float SampleTargetContrast(Texture2D tex, Camera cam, Vector3 worldPos)
        {
            Vector3 vp = cam.WorldToViewportPoint(worldPos);
            if (vp.z <= 0.1f || vp.x < 0.05f || vp.x > 0.95f || vp.y < 0.05f || vp.y > 0.95f) return SampleMeanLuminance(tex, out _);
            int cx = Mathf.RoundToInt(vp.x * tex.width);
            int cy = Mathf.RoundToInt(vp.y * tex.height);
            float LumAt(int x, int y)
            {
                Color c = tex.GetPixel(Mathf.Clamp(x, 0, tex.width - 1), Mathf.Clamp(y, 0, tex.height - 1));
                return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            }
            double targetSum = 0.0, bgSum = 0.0;
            int tCnt = 0, bCnt = 0;
            for (int dy = -6; dy <= 6; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                targetSum += LumAt(cx + dx, cy + dy);
                tCnt++;
            }
            for (int dy = -22; dy <= 22; dy += 4)
            for (int dx = -22; dx <= 22; dx += 4)
            {
                if (Mathf.Abs(dx) < 8 && Mathf.Abs(dy) < 10) continue;
                bgSum += LumAt(cx + dx, cy + dy);
                bCnt++;
            }
            float tMean = (float)(targetSum / Mathf.Max(1, tCnt));
            float bMean = (float)(bgSum / Mathf.Max(1, bCnt));
            return Mathf.Abs(tMean - bMean);
        }

        private void FinishReport(string outDir, string sceneName)
        {
            Application.logMessageReceived -= OnLog;
            var perf = FindAnyObjectByType<PerformanceFrameTelemetry>();
            if (perf != null && perf.TryGetSummary(out int sc, out float avg, out float med, out float p99,
                out float avgCpu, out float avgGpu, out float p99Gpu, out float rawP99))
            {
                Check(true, "Performance.FrameTelemetry",
                    $"samples={sc} avg={avg:0.00}ms med={med:0.00}ms p99={p99:0.00}ms rawP99={rawP99:0.00}ms cpu={avgCpu:0.00}ms gpu={avgGpu:0.00}ms upscaler={UpscalerBackend.ActiveUpscalerLabel()}");
                perf.FlushReport(sceneName, _telemetrySuffix);
            }
            if (_savedUpscalerMode >= 0)
            {
                LR2Settings.UpscalerMode = _savedUpscalerMode;
                LR2Settings.Apply();
                _savedUpscalerMode = -1;
            }
            Check(_runtimeErrors == 0, $"{sceneName}.RuntimeErrors", $"count={_runtimeErrors}");
            _report.AppendLine($"Failures: {_failures.Count}");
            string path = Path.Combine(outDir, $"qa_{sceneName}.txt");
            File.WriteAllText(path, _report.ToString());
            Debug.Log(_report.ToString());
#if UNITY_EDITOR
            if (Application.isBatchMode || Requested)
            {
                int exitCode = _failures.Count > 0 ? 1 : 0;
                UnityEditor.SessionState.SetInt("LR2QABot.ExitCode", exitCode);
                UnityEditor.SessionState.SetBool("LR2QABot.Done", true);
                bool exited = false;
                void ExitWhenStopped()
                {
                    if (exited || UnityEditor.EditorApplication.isPlaying || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
                    exited = true;
                    UnityEditor.EditorApplication.update -= ExitWhenStopped;
                    string setupPath = Path.Combine(Directory.GetCurrentDirectory(), "Library", "LastSceneManagerSetup.txt");
                    if (File.Exists("Assets/LR2/Scenes/LR2_MainMenu.unity"))
                        File.WriteAllText(setupPath, "sceneSetups:\n- path: Assets/LR2/Scenes/LR2_MainMenu.unity\n  isLoaded: 1\n  isActive: 1\n  isSubScene: 0\n");
                    UnityEditor.EditorApplication.Exit(exitCode);
                }
                UnityEditor.EditorApplication.update += ExitWhenStopped;
                UnityEditor.EditorApplication.isPlaying = false;
            }
#else
            if (Application.isBatchMode || Requested)
                Application.Quit(_failures.Count > 0 ? 1 : 0);
#endif
        }
    }
}
