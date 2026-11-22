using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LR2.AI;
using LR2.Combat;
using LR2.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace LR2.Characters
{
    /// <summary>
    /// Test harness for the first-person body, gun hold and weapon animations. Inert unless the game/editor is started
    /// with <c>-lr2PoseCheck</c> (e.g. through <c>LR2SmokeTest.RunBatch</c> without <c>-lr2Robot</c>). It freezes every other
    /// soldier, drives the local player through scripted poses (idle, look down/up, ADS, reload, pistol swap, walk,
    /// sprint) and saves first-person + third-person screenshots with alignment numbers to <c>Logs/LR2_PoseCheck</c>
    /// (or <c>-lr2OutDir</c>). A red marker shows the camera position in third-person shots.
    /// </summary>
    [DefaultExecutionOrder(1200)] // after RetargetProxy (60) and BodycamRig (1100): captures the final frame
    public class FirstPersonPoseCheck : MonoBehaviour, ISoldierInputSource
    {
        public static bool Requested => Array.IndexOf(Environment.GetCommandLineArgs(), "-lr2PoseCheck") >= 0;

        private const int Width = 1280, Height = 720;

        private enum View { FirstPerson, Front, Side, OverShoulder, HeadClose, Enemy, HandsLeft, HandsRight }

        private struct Shot
        {
            public string name;
            public View view;
            public bool showHead;
        }

        private SoldierInput _cmd = SoldierInput.Empty;
        private SoldierController _soldier;
        private RetargetProxy _proxy;
        private SoldierController _enemy;
        private string _outDir;
        private int _errors;
        private bool _done;
        private readonly StringBuilder _log = new StringBuilder();
        private readonly StringBuilder _motionCsv = new StringBuilder(
            "time,label,state,stance,moveSpeed,locoSpeed,leftFootY,rightFootY,leftFootSlide,rightFootSlide,handErrorLeft,handErrorRight,rawHipDelta,rawHeadDelta,cameraYaw,cameraPitch\n");
        private readonly Queue<Shot> _pending = new Queue<Shot>();
        private GameObject _marker, _eyeMarker;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!Requested || FindAnyObjectByType<FirstPersonPoseCheck>() != null) return;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-lr2Weapon")
                {
                    string w = args[i + 1].Trim().ToLowerInvariant();
                    if (w.Contains("12")) LR2.Game.LR2Session.SelectedPrimaryWeapon = 1;
                    else if (w.Contains("74")) LR2.Game.LR2Session.SelectedPrimaryWeapon = 2;
                    else if (w.Contains("fal")) LR2.Game.LR2Session.SelectedPrimaryWeapon = 3;
                    else LR2.Game.LR2Session.SelectedPrimaryWeapon = 0;
                }
            }
            var go = new GameObject("[LR2 PoseCheck]");
            DontDestroyOnLoad(go);
            go.AddComponent<FirstPersonPoseCheck>();
        }

        public void GetInput(SoldierController soldier, ref SoldierInput input)
        {
            input = _cmd;
            _cmd.reload = false; // one-shots: hand them out for exactly one frame
            _cmd.weaponSlot = -1;
            _cmd.jump = false;
            _cmd.crouch = false;
            _cmd.prone = false;
            _cmd.grenade = false; // B136
            _cmd.cycleScope = false;
            _cmd.fireMode = false;
            _cmd.inspect = false;
            _cmd.attachmentSlot = -1;
        }

        private void Note(string s) => _log.AppendLine($"[{Time.time,7:0.00}] {s}");
        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception) return;
            if (msg.Contains("PlasticSCM") || msg.Contains("unityplastic")) return; // editor package noise, not the game
            _errors++;
            if (_errors <= 20) Note($"{type}: {msg} | {(stack ?? "").Split('\n')[0]}");
        }

        private IEnumerator Start()
        {
            _outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "LR2_PoseCheck"));
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-lr2OutDir" && !string.IsNullOrWhiteSpace(args[i + 1]))
                    _outDir = Path.GetFullPath(Path.IsPathRooted(args[i + 1]) ? args[i + 1] : Path.Combine(Application.dataPath, "..", args[i + 1]));
            Directory.CreateDirectory(_outDir);
            foreach (var f in Directory.GetFiles(_outDir, "*.png")) File.Delete(f);
            Application.logMessageReceived += OnLog;
            Note("PoseCheck started");

            float waitUntil = Time.time + 20f;
            while (_soldier == null && Time.time < waitUntil)
            {
                var local = Combatant.FindLocalPlayer();
                _soldier = local != null ? local.GetComponent<SoldierController>() : null;
                yield return null;
            }
            if (_soldier == null) { Note("No local soldier found"); Finish(); yield break; }
            yield return Wait(3.5f); // spawn, countdown, first equip

            FreezeOthers();
            var pis = _soldier.GetComponent<PlayerInputSource>();
            if (pis != null) pis.enabled = false;
            _soldier.InputSource = this;
            _proxy = _soldier.GetComponent<RetargetProxy>();
            _cmd = SoldierInput.Empty;
            _cmd.absoluteLook = true;
            _cmd.aimYaw = OpenYaw();
            _cmd.aimPitch = 0f;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-lr2Weapon" && args[i + 1].Trim().ToLowerInvariant().Contains("mk23"))
                {
                    _cmd.weaponSlot = 1;
                    yield return Wait(0.8f);
                }
            }
            Note($"Local soldier '{_soldier.name}' weapon='{_soldier.ActiveFirearm?.DisplayName}' at {_soldier.transform.position}, facing yaw {_cmd.aimYaw:0}");
            yield return Wait(1.2f);

            // -lr2AimTest: only measure where the real bot AI's shots land (stationary vs strafing target), then quit.
            bool aimRun = Array.IndexOf(args, "-lr2AimTest") >= 0, gifRun = Array.IndexOf(args, "-lr2GifRun") >= 0;
            // -lr2DeathTest: ADS sight alignment + death/respawn pose integrity in six states (user 11-18 14:10), then quit.
            if (Array.IndexOf(args, "-lr2DeathTest") >= 0) { yield return DeathTest(); Finish(); yield break; }
            // -lr2AdsWallCheck: B125 "ADSing going wild ... when I hit other obstacles, and maybe death" (user 11-19), then quit.
            if (Array.IndexOf(args, "-lr2AdsWallCheck") >= 0) { yield return AdsWallCheck(); Finish(); yield break; }
            // -lr2GripCheck: where each hand sits on the gun (weapon space) + the gun's underside profile (B26/B49), then quit.
            if (Array.IndexOf(args, "-lr2GripCheck") >= 0) { yield return GripCheck(); Finish(); yield break; }
            // -lr2ExtrasCheck (B136): Kinemation's demo features in LR2 (sights, fire modes, inspect, grenade, knife, torch), then quit.
            if (Array.IndexOf(args, "-lr2ExtrasCheck") >= 0) { yield return ExtrasCheck(); Finish(); yield break; }
            // -lr2FxShots: fire close-ups and forced thunderstorm rain (B87/B93/B97/B101), then quit.
            if (Array.IndexOf(args, "-lr2FxShots") >= 0) { yield return FxShots(); Finish(); yield break; }
            // -lr2AdsWalkCheck: sight picture and view stability while moving in ADS (B91), then quit.
            if (Array.IndexOf(args, "-lr2AdsWalkCheck") >= 0) { yield return AdsWalkCheck(); Finish(); yield break; }
            if (aimRun) yield return BotAimTest();
            // -lr2GifRun: record real gameplay clips (HUD included) for the README GIFs, then quit.
            if (gifRun) yield return GifRun();
            if (aimRun || gifRun) { Finish(); yield break; }

            LogDriverWeaponBones("idle");
            yield return Capture("fp_01_idle", "idle");
            Note(LiveGunLine("hip"));
            // B26 left-thumb A/B: tucked thumb vs the C-clamp default
            RetargetProxy.LeftThumbBaseOverride = new Vector2(-1.25f, -0.85f);
            RetargetProxy.LeftThumbTipOverride = new Vector2(-0.6f, -0.15f);
            yield return Wait(0.2f);
            yield return Capture("fp_01b_idle_thumb_tucked", "idle, left thumb tucked");
            RetargetProxy.LeftThumbBaseOverride = null;
            RetargetProxy.LeftThumbTipOverride = null;
            yield return Wait(0.2f);
            yield return Capture("tp_01_front", null, View.Front, true);
            yield return Capture("tp_02_side", null, View.Side, true);
            yield return Capture("tp_03_overshoulder", null, View.OverShoulder, false);
            yield return Capture("tp_04_head_close", null, View.HeadClose, true);
            LogFingers("idle rifle (FingerMode 1: authored shapes + snug bias)");
            yield return Capture("tp_04b_hands_left", null, View.HandsLeft, true);
            yield return Capture("tp_04c_hands_right", null, View.HandsRight, true);
            // B26 A/B: the legacy hard clamps (FingerMode 0) for comparison, then back to the new mode
            RetargetProxy.FingerMode = 0;
            yield return Wait(0.2f);
            LogFingers("idle rifle (FingerMode 0: legacy clamps)");
            yield return Capture("tp_04d_hands_left_LEGACY", null, View.HandsLeft, true);
            yield return Capture("tp_04e_hands_right_LEGACY", null, View.HandsRight, true);
            RetargetProxy.FingerMode = 1;
            yield return Wait(0.2f);

            _cmd.aimPitch = 60f;
            yield return Wait(0.9f);
            yield return Capture("fp_02_lookdown", "look down 60");
            _cmd.aimPitch = -35f;
            yield return Wait(0.8f);
            yield return Capture("fp_03_lookup", "look up 35");
            _cmd.aimPitch = 0f;
            yield return Wait(0.6f);

            _cmd.aim = true;
            yield return Wait(1.0f);
            yield return Capture("fp_04_ads", "ADS rifle");
            Note(LiveGunLine("ads"));
            // B26 left-thumb A/B: tucked thumb vs the C-clamp default
            RetargetProxy.LeftThumbBaseOverride = new Vector2(-1.25f, -0.85f);
            RetargetProxy.LeftThumbTipOverride = new Vector2(-0.6f, -0.15f);
            yield return Wait(0.2f);
            yield return Capture("fp_04b_ads_thumb_tucked", "ADS, left thumb tucked");
            RetargetProxy.LeftThumbBaseOverride = null;
            RetargetProxy.LeftThumbTipOverride = null;
            yield return Wait(0.2f);
            yield return Capture("tp_05_ads_side", null, View.Side, true);
            LogFingers("ADS rifle");
            yield return Capture("tp_05b_ads_hands_right", null, View.HandsRight, true);
            _cmd.aim = false;
            yield return Wait(0.6f);

            // reload: spend a few rounds first so a tactical reload is allowed, then verify & photograph BOTH mag-out and charge-rack
            _cmd.fire = true;
            yield return Wait(0.3f);
            _cmd.fire = false;
            yield return Wait(0.4f);
            var akTf = _soldier.ActiveFirearm != null ? _soldier.ActiveFirearm.transform : null;
            var magTf = akTf != null ? FindDeep(akTf, "Mag") : null;
            var chargeTf = akTf != null ? (FindDeep(akTf, "Charge") ?? FindDeep(akTf, "Bolt")) : null;
            Vector3 magRestLocal = magTf != null && akTf != null ? akTf.InverseTransformPoint(magTf.position) : Vector3.zero;
            Vector3 chargeRestLocal = chargeTf != null && akTf != null ? akTf.InverseTransformPoint(chargeTf.position) : Vector3.zero;
            float maxMagDisp = 0f, maxChargeDisp = 0f;

            void SampleReloadParts()
            {
                if (akTf == null) return;
                if (magTf != null) maxMagDisp = Mathf.Max(maxMagDisp, Vector3.Distance(magRestLocal, akTf.InverseTransformPoint(magTf.position)));
                if (chargeTf != null) maxChargeDisp = Mathf.Max(maxChargeDisp, Vector3.Distance(chargeRestLocal, akTf.InverseTransformPoint(chargeTf.position)));
            }
            IEnumerator WaitAndSample(float sec)
            {
                float end = Time.time + sec;
                while (Time.time < end) { SampleReloadParts(); yield return null; }
            }

            // Captures follow the reload's own progress (B27 made reloads 1.5x faster; B28 tactical reloads end after mag-in).
            IEnumerator WaitReloadProgress(LR2.Weapons.LR2Firearm f, float p, float timeout)
            {
                float end = Time.time + timeout;
                while (Time.time < end && f != null && f.IsReloading && f.ReloadDisplayProgress < p) { SampleReloadParts(); yield return null; }
            }
            IEnumerator WaitReloadDone(LR2.Weapons.LR2Firearm f, float timeout)
            {
                float end = Time.time + timeout;
                while (Time.time < end && f != null && f.IsReloading) { SampleReloadParts(); yield return null; }
            }
            // B16/D40 "the gun jumps at the end of the reload": per-frame change of the gun's camera-relative pose.
            var jumpLog = new List<Vector3>(); // (time, deg this frame, cm this frame)
            Quaternion prevRel = Quaternion.identity;
            Vector3 prevRelPos = Vector3.zero;
            bool hasPrevRel = false;
            void SampleGunRel()
            {
                var item = _soldier.ActiveItem != null ? _soldier.ActiveItem.transform : null;
                var cam = _soldier.ViewCamera;
                if (item == null || cam == null) { hasPrevRel = false; return; }
                Quaternion rel = Quaternion.Inverse(cam.transform.rotation) * item.rotation;
                Vector3 relPos = cam.transform.InverseTransformPoint(item.position);
                if (hasPrevRel) jumpLog.Add(new Vector3(Time.time, Quaternion.Angle(prevRel, rel), (relPos - prevRelPos).magnitude * 100f));
                prevRel = rel;
                prevRelPos = relPos;
                hasPrevRel = true;
            }
            IEnumerator WaitReloadDoneMeasured(LR2.Weapons.LR2Firearm f, float timeout, string label)
            {
                jumpLog.Clear();
                hasPrevRel = false;
                float end = Time.time + timeout;
                while (Time.time < end && f != null && f.IsReloading) { SampleReloadParts(); SampleGunRel(); yield return null; }
                float tEnd = Time.time;
                float post = Time.time + 0.35f;
                while (Time.time < post) { SampleGunRel(); yield return null; }
                float maxDeg = 0f, maxCm = 0f, tDeg = 0f, tCm = 0f, maxJerk = 0f, prevDeg = -1f;
                var degs = new List<float>();
                foreach (var smp in jumpLog)
                {
                    if (smp.x < tEnd - 0.4f) continue;
                    degs.Add(smp.y);
                    if (prevDeg >= 0f) maxJerk = Mathf.Max(maxJerk, Mathf.Abs(smp.y - prevDeg));
                    prevDeg = smp.y;
                    if (smp.y > maxDeg) { maxDeg = smp.y; tDeg = smp.x - tEnd; }
                    if (smp.z > maxCm) { maxCm = smp.z; tCm = smp.x - tEnd; }
                }
                degs.Sort();
                float med = degs.Count > 0 ? degs[degs.Count / 2] : 0f;
                Note($"RELOAD-END-JUMP [{label}] maxRot={maxDeg:0.00} deg/frame at {tDeg:+0.00;-0.00}s maxMove={maxCm:0.00} cm/frame at {tCm:+0.00;-0.00}s " +
                     $"medianRot={med:0.00} deg/frame maxJerk={maxJerk:0.00} deg/frame^2 frames={degs.Count} (smooth: maxJerk < 1.0 and maxRot < 2.5)");
            }
            var fa = _soldier.ActiveFirearm;
            void LogReloadAngles(string label)
            {
                var br = _proxy != null ? _proxy.Bridge : null;
                var item = _soldier.ActiveItem != null ? _soldier.ActiveItem.transform : null;
                var cam = _soldier.ViewCamera;
                float gunVsCam = item != null && cam != null ? Vector3.Angle(item.forward, cam.transform.forward) : -1f;
                float upVsCam = item != null && cam != null ? Vector3.Angle(item.up, cam.transform.up) : -1f;
                Note($"RELOAD-ANGLES [{label}] overlap={ReloadMotionBridge.WeaponBoneOverlap:0} wbw={(br != null ? br.LastWeaponBoneWeight : -1f):0.00} " +
                     $"baseAngle={(br != null ? br.LastBaseAngle : -1f):0.0} bridgeAngle={(br != null ? br.LastGunAngle : -1f):0.0} gunFwdVsCam={gunVsCam:0.0} gunUpVsCam={upVsCam:0.0}");
            }
            LogReloadAngles("idle reference");

            // (0) B35 A/B: the alternative WeaponBoneOverlap first, then every measured reload runs on the game default
            // (0 since 11-18 04:19: Kinemation does not move the weapon bone on this rig, so the bridge adds the clip motion).
            float defaultOverlap = ReloadMotionBridge.WeaponBoneOverlap;
            ReloadMotionBridge.WeaponBoneOverlap = defaultOverlap > 0.5f ? 0f : 1f;
            _cmd.reload = true;
            yield return WaitAndSample(0.05f);
            yield return WaitReloadProgress(fa, 0.4f, 4f);
            LogReloadAngles("ALT tactical ~40%");
            yield return Capture("tp_06a_reload_front_ALT", null, View.Front, true);
            yield return WaitReloadDone(fa, 5f);
            ReloadMotionBridge.WeaponBoneOverlap = defaultOverlap;
            _cmd.fire = true;
            yield return Wait(0.2f);
            _cmd.fire = false;
            yield return Wait(0.4f);
            maxMagDisp = maxChargeDisp = 0f;

            // (1) TACTICAL reload (rounds left): mag must move, charging handle must NOT cycle, gun ends at magazine + 1.
            _cmd.reload = true;
            yield return WaitAndSample(0.05f);
            bool tacStarted = fa != null && fa.IsTacticalReload;
            float tacT0 = Time.time;
            DiagnoseReload("tactical start");
            yield return WaitReloadProgress(fa, 0.4f, 4f);
            LogReloadAngles("DEFAULT tactical ~40%");
            yield return Capture("fp_05_reload_a", "tactical reload, mag out (~40 %)");
            yield return Capture("tp_06_reload_front", null, View.Front, true);
            LogDriverWeaponBones("tactical reload ~40%");
            yield return WaitReloadDoneMeasured(fa, 5f, "tactical");
            float tacDur = Time.time - tacT0;
            Note($"RELOAD-TACTICAL started={tacStarted} duration={tacDur:0.00}s ammo={(fa != null ? fa.AmmoInMagazine : -1)}/{(fa != null ? fa.MagazineSize : -1)} " +
                 $"magMaxDisp={maxMagDisp:0.000}m chargeMaxDisp={maxChargeDisp:0.000}m (expect tactical, ammo = mag+1, mag>=0.05m, charge<0.02m)");
            if (magTf != null && maxMagDisp < 0.05f) Note("ERROR [PoseCheck] Mag did not detach during the tactical reload!");
            if (fa != null && !tacStarted) Note("ERROR [PoseCheck] Reload with rounds left was not tactical!");
            if (fa != null && fa.AmmoInMagazine != fa.MagazineSize + 1) Note("ERROR [PoseCheck] Tactical reload did not end at magazine + 1!");

            // (2) EMPTY reload: empty the magazine, then the full clip must play and the charging handle must cycle.
            maxMagDisp = maxChargeDisp = 0f;
            float emptyEnd = Time.time + 8f;
            _cmd.fire = true;
            while (Time.time < emptyEnd && fa != null && fa.AmmoInMagazine > 0) yield return null;
            _cmd.fire = false;
            yield return Wait(0.25f);
            if (fa != null && !fa.IsReloading) _cmd.reload = true;
            yield return WaitAndSample(0.05f);
            float emptyT0 = Time.time;
            yield return WaitReloadProgress(fa, 0.85f, 6f);
            LogReloadAngles("empty ~85%");
            yield return Capture("fp_06_reload_b", "empty reload, charge rack (~85 %)");
            DiagnoseReload("empty ~85%");
            yield return WaitReloadDoneMeasured(fa, 5f, "empty");
            Note($"RELOAD-EMPTY duration={Time.time - emptyT0:0.00}s ammo={(fa != null ? fa.AmmoInMagazine : -1)}/{(fa != null ? fa.MagazineSize : -1)} " +
                 $"magMaxDisp={maxMagDisp:0.000}m chargeMaxDisp={maxChargeDisp:0.000}m (expect ammo = mag, mag>=0.05m, charge>=0.02m)");
            if (magTf != null && maxMagDisp < 0.05f) Note("ERROR [PoseCheck] Mag did not detach during the empty reload!");
            if (chargeTf != null && maxChargeDisp < 0.02f) Note("ERROR [PoseCheck] Charging handle (Charge) did not cycle during the empty reload!");

            // weapon swap to the sidearm and back
            _cmd.weaponSlot = 1;
            yield return Wait(0.3f);
            yield return Capture("fp_07_swap_mid", "swap to slot 2 +0.3s");
            yield return Wait(1.1f);
            yield return Capture("fp_08_pistol", "pistol idle");
            yield return Capture("tp_07_pistol_front", null, View.Front, true);
            LogFingers("pistol");
            yield return Capture("tp_07b_pistol_hands_left", null, View.HandsLeft, true);
            _cmd.aim = true;
            yield return Wait(0.8f);
            yield return Capture("fp_09_pistol_ads", "pistol ADS");
            _cmd.aim = false;
            yield return Wait(0.5f);
            _cmd.weaponSlot = 0;
            yield return Wait(1.4f);
            yield return Capture("fp_10_rifle_back", "rifle back");

            // movement
            _cmd.move = new Vector2(0f, 1f);
            yield return Wait(1.0f);
            yield return MeasureGait("walk", 1.6f);
            yield return Capture("fp_11_walk", "walk");
            yield return Capture("tp_08_walk_side", null, View.Side, true);

            // (3) B16: tactical reload WHILE walking (the rifle used to swing ~90 degrees).
            {
                var wfa = _soldier.ActiveFirearm;
                _cmd.fire = true;
                yield return Wait(0.15f);
                _cmd.fire = false;
                yield return Wait(0.25f);
                _cmd.reload = true;
                yield return Wait(0.05f);
                yield return WaitReloadProgress(wfa, 0.45f, 4f);
                LogReloadAngles("walking tactical ~45%");
                yield return Capture("fp_11b_walk_reload", "tactical reload while walking (~45 %)");
                yield return Capture("tp_08b_walk_reload_side", null, View.Side, true);
                yield return WaitReloadDoneMeasured(wfa, 5f, "walking tactical");
                Note($"RELOAD-WALK ammo={(wfa != null ? wfa.AmmoInMagazine : -1)}/{(wfa != null ? wfa.MagazineSize : -1)}");
            }
            _cmd.sprint = true;
            yield return Wait(1.0f);
            yield return MeasureGait("sprint", 1.6f);
            yield return Capture("fp_12_sprint", "sprint");
            yield return Capture("tp_09_sprint_side", null, View.Side, true);

            // (4) D40: reload while sprinting (user 04:10: "badly broken while walking or sprinting")
            {
                var sfa = _soldier.ActiveFirearm;
                _cmd.sprint = false;
                _cmd.fire = true;
                yield return Wait(0.15f);
                _cmd.fire = false;
                _cmd.sprint = true;
                yield return Wait(0.6f);
                _cmd.reload = true;
                yield return WaitAndSample(0.05f);
                yield return WaitReloadProgress(sfa, 0.45f, 4f);
                LogReloadAngles("sprinting tactical ~45%");
                yield return Capture("fp_12b_sprint_reload", "tactical reload while sprinting (~45 %)");
                yield return Capture("tp_09b_sprint_reload_side", null, View.Side, true);
                yield return WaitReloadDoneMeasured(sfa, 5f, "sprinting tactical");
                Note($"RELOAD-SPRINT stillSprinting={_soldier.IsSprinting} ammo={(sfa != null ? sfa.AmmoInMagazine : -1)}/{(sfa != null ? sfa.MagazineSize : -1)}");
            }
            _cmd.sprint = false;
            _cmd.move = Vector2.zero;
            yield return Wait(0.8f);

            // Reset to open floor lane after walking/sprinting so stances and the enemy aren't against the perimeter wall.
            _soldier.Teleport(new Vector3(24f, 0.05f, 0f), OpenYaw());
            yield return Wait(0.25f);

            // crouch, prone, and jump stances
            _cmd.crouch = true;
            yield return null;
            _cmd.crouch = false;
            yield return Wait(0.6f);
            yield return Capture("fp_14_crouch", $"crouch ({_soldier.Movement?.Stance})");
            yield return Capture("tp_11_crouch_side", null, View.Side, true);

            // Exit crouch to standing cleanly, then test full prone transition
            _cmd.crouch = true;
            yield return null;
            _cmd.crouch = false;
            yield return Wait(0.45f);
            _cmd.prone = true;
            yield return null;
            _cmd.prone = false;
            yield return Wait(1.75f);
            yield return Capture("fp_15_prone", $"prone ({_soldier.Movement?.Stance})");
            yield return Capture("tp_12_prone_side", null, View.Side, true);

            _cmd.jump = true;
            yield return null;
            _cmd.jump = false;
            yield return Wait(1.25f);
            _cmd.jump = true;
            yield return null;
            _cmd.jump = false;
            yield return Wait(0.22f);
            yield return Capture("fp_16_jump", $"jump mid-air ({_soldier.Movement?.State})");
            yield return Wait(1.2f);

            // the enemy soldier (third person: retarget, hips alignment, gun hold)
            if (_enemy != null)
            {
                Vector3 fwd = Quaternion.Euler(0f, _cmd.aimYaw, 0f) * Vector3.forward;
                _enemy.Teleport(_soldier.transform.position + fwd * 4.5f, _cmd.aimYaw + 180f);
                yield return Wait(0.8f);
                yield return Capture("fp_13_enemy", "enemy 4.5 m ahead");
                yield return Capture("tp_10_enemy_close", null, View.Enemy, false);
                var ep = _enemy.GetComponent<RetargetProxy>();
                if (ep != null)
                    Note($"ENEMY proxy rawHipsDelta={ep.RawHipsDelta:0.000} rawHeadDelta={ep.RawHeadDelta.magnitude:0.000} shift={ep.AppliedShift} " +
                         $"handErr L={ep.HandErrorLeft:0.000} R={ep.HandErrorRight:0.000} firstPerson={ep.FirstPersonActive}");
            }

            // (K) B14.1 killcam evidence: a hostile bot ADSes and shoots the player dead; capture the replay it produces.
            yield return KillcamCheck();
            Finish();
        }

        // ------------------------------------------------------------------ real bot AI aim test (user 11-18 10:05)
        private SoldierController _aimShooter;
        private string _aimPhase;
        private sealed class AimShot
        {
            public string phase, zone = "none";
            public float time, lateral, height, dist, vLat, lead;
            public Vector3 cross, right, playerPos;
            public bool hit;
        }
        private readonly List<AimShot> _aimShots = new List<AimShot>();
        private readonly Dictionary<string, int[]> _aimStats = new Dictionary<string, int[]>(); // phase -> {shots, hits, head, trailingMisses, misses}

        /// <summary>
        /// One hostile bot runs its full AI (BotController: perception, brain, human aim model, recoil pull-down) against the
        /// player 15 m away: 12 s standing still, then 12 s strafing left and right. Every bot shot is intersected with the
        /// vertical plane through the player (perpendicular to the line of sight) and logged with the real hit result.
        /// </summary>
        private IEnumerator BotAimTest()
        {
            SoldierController shooter = null;
            foreach (var c in Combatant.All)
            {
                if (c == null || c.gameObject == _soldier.gameObject) continue;
                var sc = c.GetComponent<SoldierController>();
                if (sc != null && !sc.IsDead && _soldier.Combatant != null && TeamUtil.AreHostile(c, _soldier.Combatant)) { shooter = sc; break; }
            }
            var bot = shooter != null ? shooter.GetComponent<BotController>() : null;
            if (shooter == null || bot == null) { Note("AIM-TEST skipped: no hostile bot with a BotController"); yield break; }
            _aimShooter = shooter;

            _soldier.Teleport(new Vector3(24f, 0.05f, 0f), OpenYaw());
            float yaw = OpenYaw();
            Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            shooter.Teleport(_soldier.transform.position + fwd * 15f, yaw + 180f);
            _cmd = SoldierInput.Empty;
            _cmd.absoluteLook = true;
            _cmd.aimYaw = yaw;
            var h = _soldier.Health;
            float oldMax = h != null ? h.MaxHealth : 100f;
            if (h != null) { h.MaxHealth = 100000f; h.Heal(100000f); }
            GameEvents.ShotFired += OnAimShot;
            GameEvents.Hit += OnAimHit;
            bot.enabled = true;
            shooter.ControlsEnabled = true;
            // the bot's NavMeshAgent plans paths with updatePosition = false: move its internal position to the teleport too
            if (bot.Agent != null && bot.Agent.isOnNavMesh) bot.Agent.Warp(shooter.transform.position);
            Note($"AIM-TEST bot={shooter.name} weapon={shooter.ActiveFirearm?.DisplayName} start distance 15 m (real BotController AI)");
            // Start the fight the way a real one starts (C11-C13: a patrolling bot turned away before its meter filled):
            // the player fires a short burst at it, so it hears the shots and knows who attacked it.
            Vector3 eyeP = _soldier.ViewTransform != null ? _soldier.ViewTransform.position : _soldier.transform.position + Vector3.up * 1.6f;
            Vector3 toBot = shooter.transform.position + Vector3.up * 1.3f - eyeP;
            _cmd.aimYaw = Mathf.Atan2(toBot.x, toBot.z) * Mathf.Rad2Deg;
            _cmd.aimPitch = -Mathf.Atan2(toBot.y, new Vector2(toBot.x, toBot.z).magnitude) * Mathf.Rad2Deg;
            _cmd.fire = true;
            yield return Wait(0.25f);
            _cmd.fire = false;
            Note("AIM-TEST player fired a 0.25 s opening burst at the bot");

            _aimPhase = "stationary";
            float stEnd = Time.time + 12f, nextNote = Time.time + 3f;
            while (Time.time < stEnd)
            {
                if (Time.time >= nextNote) { nextNote += 3f; AimProgress(); }
                yield return null;
            }
            yield return Capture("aim_01_stationary", "bot firing at a stationary player");

            _aimPhase = "moving";
            float end = Time.time + 12f, flip = 0f;
            float dir = 1f;
            float mvNote = Time.time + 3f;
            while (Time.time < end)
            {
                if (Time.time >= mvNote) { mvNote += 3f; AimProgress(); }
                if (Time.time >= flip) { dir = -dir; flip = Time.time + UnityEngine.Random.Range(0.45f, 0.85f); }
                _cmd.move = new Vector2(dir, 0f);
                yield return null;
            }
            _cmd.move = Vector2.zero;
            yield return Capture("aim_02_moving", "bot firing at a strafing player");
            _aimPhase = null;

            GameEvents.ShotFired -= OnAimShot;
            GameEvents.Hit -= OnAimHit;
            bot.enabled = false;
            shooter.ControlsEnabled = false;
            if (h != null) { h.MaxHealth = oldMax; h.Heal(oldMax); }
            var csvOut = new StringBuilder("phase,time,lateral_m,height_m,distance_m,target_lateral_speed,lead_lateral_m,hit,zone\n");
            _aimStats.Clear();
            foreach (var sh in _aimShots)
            {
                csvOut.Append(FormattableString.Invariant($"{sh.phase},{sh.time:0.000},{sh.lateral:0.0000},{sh.height:0.0000},{sh.dist:0.00},{sh.vLat:0.000},{sh.lead:0.0000},{(sh.hit ? 1 : 0)},{sh.zone}\n"));
                if (!_aimStats.TryGetValue(sh.phase, out var a)) _aimStats[sh.phase] = a = new int[5];
                a[0]++;
                if (sh.hit) { a[1]++; if (sh.zone == "Head") a[2]++; }
                else { a[4]++; if (sh.phase == "moving" && Mathf.Abs(sh.vLat) > 0.5f && sh.lead < -0.25f) a[3]++; }
            }
            File.WriteAllText(Path.Combine(_outDir, "bot_aim_shots.csv"), csvOut.ToString());
            foreach (var kv in _aimStats)
            {
                var a = kv.Value;
                Note($"AIM-TEST [{kv.Key}] shots={a[0]} hits={a[1]} ({(a[0] > 0 ? 100f * a[1] / a[0] : 0f):0}%) head={a[2]} misses={a[4]} trailingMisses={a[3]} ({(a[4] > 0 ? 100f * a[3] / a[4] : 0f):0}% of misses behind the motion)");
            }
        }

        private void AimProgress()
        {
            if (_aimShooter == null) return;
            int shots = 0, hits = 0;
            foreach (var sh in _aimShots) if (sh.phase == _aimPhase) { shots++; if (sh.hit) hits++; }
            var bot = _aimShooter.GetComponent<BotController>();
            var bb = bot != null ? bot.Blackboard : null;
            var best = bot != null && bot.Perception != null ? bot.Perception.BestThreat : null;
            var self = _aimShooter.Combatant;
            var me = _soldier.Combatant;
            Vector3 eye = self != null ? self.EyePosition : _aimShooter.transform.position + Vector3.up * 1.6f;
            Vector3 head = me != null ? me.HeadPosition : _soldier.transform.position + Vector3.up * 1.7f;
            bool blocked = Physics.Linecast(eye, head, out var hit, LR2Layers.VisionMask, QueryTriggerInteraction.Ignore);
            float ang = self != null ? Vector3.Angle(self.ViewDirection, head - eye) : -1f;
            Note($"AIM-TEST [{_aimPhase}] bot {Vector3.Distance(_aimShooter.transform.position, _soldier.transform.position):0.0} m, goal={(bot != null ? bot.CurrentGoalName : "-")}, " +
                 $"botEnabled={(bot != null && bot.enabled)} controls={_aimShooter.ControlsEnabled}, bbTarget={(bb != null && bb.target != null ? bb.target.name : "none")} visible={(bb != null && bb.targetVisible)}, " +
                 $"best={(best != null && best.combatant != null ? best.combatant.name : "none")} det={(best != null ? best.detection : -1f):0.00} aware={(best != null && best.aware)}, " +
                 $"angleToPlayer={ang:0} deg, losBlocked={blocked}{(blocked ? " by " + hit.collider.name : "")}, hostile={(self != null && me != null && self.IsHostileTo(me))}, " +
                 $"shots {shots}, matched hits {hits}, player hp {(_soldier.Health != null ? _soldier.Health.Current : -1f):0}");
        }

        private void OnAimShot(ShotEvent e)
        {
            if (_aimPhase == null || e.shooter == null || _aimShooter == null || e.shooter.gameObject != _aimShooter.gameObject) return;
            Vector3 P = _soldier.transform.position;
            Vector3 n = P - e.origin; n.y = 0f;
            if (n.sqrMagnitude < 1e-4f) return;
            n.Normalize();
            Vector3 d = e.direction.normalized;
            float dn = Vector3.Dot(d, n);
            if (dn <= 1e-3f) return;
            float t = Vector3.Dot(P - e.origin, n) / dn;
            Vector3 X = e.origin + d * t;
            Vector3 right = Vector3.Cross(Vector3.up, n).normalized;
            float lateral = Vector3.Dot(X - P, right), height = X.y - P.y;
            Vector3 v = _soldier.Movement != null ? _soldier.Movement.Velocity : Vector3.zero;
            float vLat = Vector3.Dot(v, right);
            float lead = Mathf.Abs(vLat) > 0.5f ? lateral * Mathf.Sign(vLat) : lateral; // + = ahead of the motion
            float dist = Vector3.Distance(new Vector3(e.origin.x, 0f, e.origin.z), new Vector3(P.x, 0f, P.z));
            _aimShots.Add(new AimShot { phase = _aimPhase, time = Time.time, lateral = lateral, height = height, dist = dist, vLat = vLat, lead = lead,
                                        cross = X, right = right, playerPos = P });
        }

        private void OnAimHit(HitEvent e)
        {
            if (_aimPhase == null || _aimShooter == null || e.attacker == null || e.attacker.gameObject != _aimShooter.gameObject) return;
            if (e.victim == null || e.victim.gameObject != _soldier.gameObject) return;
            // Bullets are projectiles: a hit lands one or more frames after its shot (often after the next shot at 800 RPM).
            // Match it to the unmatched shot of the last 0.35 s whose crossing point is nearest the real impact point.
            AimShot best = null;
            float bestD = 1.2f;
            for (int i = _aimShots.Count - 1; i >= 0; i--)
            {
                var sh = _aimShots[i];
                if (Time.time - sh.time > 0.35f) break;
                if (sh.hit) continue;
                float d = Vector3.Distance(sh.cross, e.point);
                if (d < bestD) { bestD = d; best = sh; }
            }
            if (best == null) return;
            best.hit = true;
            best.zone = e.zone.ToString();
            // plot the real impact point, relative to where the player stood when the shot was fired
            best.lateral = Vector3.Dot(e.point - best.playerPos, best.right);
            best.height = e.point.y - best.playerPos.y;
            best.lead = Mathf.Abs(best.vLat) > 0.5f ? best.lateral * Mathf.Sign(best.vLat) : best.lateral;
        }

        // ------------------------------------------------------------------ gameplay GIF clips (user 11-18 10:33)
        private int _clipFrames;
        private bool _clipRecording;

        /// <summary>Saves the game view (HUD included) about <paramref name="fps"/> times a second into gif_&lt;name&gt;/.</summary>
        private IEnumerator RecordClip(string name, float seconds, float fps = 12f)
        {
            string dir = Path.Combine(_outDir, "gif_" + name);
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            _clipRecording = true;
            _clipFrames = 0;
            float end = Time.realtimeSinceStartup + seconds, next = 0f;
            while (Time.realtimeSinceStartup < end)
            {
                yield return new WaitForEndOfFrame();
                if (Time.realtimeSinceStartup < next) continue;
                next = Time.realtimeSinceStartup + 1f / fps;
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                if (tex == null) continue;
                File.WriteAllBytes(Path.Combine(dir, $"f_{_clipFrames++:000}.jpg"), tex.EncodeToJPG(92));
                Destroy(tex);
            }
            _clipRecording = false;
            Note($"GIF [{name}] {_clipFrames} frames in {seconds:0.0} s");
        }

        private void AimAt(SoldierController target, float errDeg)
        {
            Vector3 eyeP = _soldier.ViewTransform != null ? _soldier.ViewTransform.position : _soldier.transform.position + Vector3.up * 1.6f;
            Vector3 d = target.transform.position + Vector3.up * 1.3f - eyeP;
            _cmd.aimYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + Mathf.Sin(Time.time * 2.3f) * errDeg;
            _cmd.aimPitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg + Mathf.Sin(Time.time * 3.1f + 1f) * errDeg * 0.6f;
        }

        private IEnumerator GifRun()
        {
            SoldierController bot = null;
            foreach (var c in Combatant.All)
            {
                if (c == null || c.gameObject == _soldier.gameObject) continue;
                var sc = c.GetComponent<SoldierController>();
                if (sc != null && !sc.IsDead && _soldier.Combatant != null && TeamUtil.AreHostile(c, _soldier.Combatant)) { bot = sc; break; }
            }
            var brain = bot != null ? bot.GetComponent<BotController>() : null;
            float yaw = OpenYaw();

            // 1. tactical reload in first person
            _soldier.Teleport(new Vector3(24f, 0.05f, 0f), yaw);
            _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = yaw;
            yield return Wait(0.6f);
            _cmd.fire = true; yield return Wait(0.35f); _cmd.fire = false;
            yield return Wait(0.3f);
            StartCoroutine(RecordClip("reload", 3.0f));
            yield return Wait(0.25f);
            _cmd.reload = true; yield return null;
            while (_clipRecording) yield return null;

            // 2. sprint, slow down, crouch
            StartCoroutine(RecordClip("sprint", 3.4f));
            _cmd.move = new Vector2(0f, 1f); _cmd.sprint = true;
            yield return Wait(1.8f);
            _cmd.sprint = false;
            yield return Wait(0.6f);
            _cmd.move = Vector2.zero; _cmd.crouch = true; yield return null; _cmd.crouch = false;
            while (_clipRecording) yield return null;
            _cmd.crouch = true; yield return null; _cmd.crouch = false;
            yield return Wait(0.4f);

            if (bot == null || brain == null) { Note("GIF gunfight skipped: no hostile bot"); yield break; }

            // 3. gunfight against the real bot AI (bigger health pools so it lasts the clip)
            _soldier.Teleport(new Vector3(24f, 0.05f, 0f), yaw);
            Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            bot.Teleport(_soldier.transform.position + fwd * 16f, yaw + 180f);
            var ph = _soldier.Health; var bh = bot.Health;
            float pMax = ph != null ? ph.MaxHealth : 100f, bMax = bh != null ? bh.MaxHealth : 100f;
            if (ph != null) { ph.MaxHealth = 600f; ph.Heal(600f); }
            if (bh != null) { bh.MaxHealth = 450f; bh.Heal(450f); }
            brain.enabled = true; bot.ControlsEnabled = true;
            if (brain.Agent != null && brain.Agent.isOnNavMesh) brain.Agent.Warp(bot.transform.position);
            _cmd = SoldierInput.Empty; _cmd.absoluteLook = true;
            AimAt(bot, 0f);
            _cmd.fire = true; yield return Wait(0.2f); _cmd.fire = false;   // opening burst: the bot knows who shot
            _cmd.aim = true;
            StartCoroutine(RecordClip("gunfight", 6.5f));
            float burstEnd = 0f, nextBurst = Time.time + 0.5f, strafeFlip = 0f, dir = 1f;
            while (_clipRecording)
            {
                if (bot.IsDead || _soldier.IsDead) break;
                AimAt(bot, 1.6f);
                if (Time.time >= nextBurst) { burstEnd = Time.time + UnityEngine.Random.Range(0.25f, 0.5f); nextBurst = burstEnd + UnityEngine.Random.Range(0.4f, 0.8f); }
                _cmd.fire = Time.time < burstEnd;
                if (Time.time >= strafeFlip) { dir = -dir; strafeFlip = Time.time + UnityEngine.Random.Range(0.6f, 1.1f); }
                _cmd.move = new Vector2(dir * 0.6f, 0f);
                yield return null;
            }
            _cmd.fire = false; _cmd.move = Vector2.zero; _cmd.aim = false;
            while (_clipRecording) yield return null;

            // 4. the bot wins this time: death, then the killcam replay of it
            if (bh != null) { bh.MaxHealth = bMax; bh.Heal(bMax); }
            if (ph != null) { ph.MaxHealth = pMax; ph.Heal(pMax); }
            if (!bot.IsDead)
            {
                float until = Time.time + 10f;
                while (!_soldier.IsDead && Time.time < until) { AimAt(bot, 6f); yield return null; }
                if (_soldier.IsDead) yield return RecordClip("death_and_killcam", 5.4f);
                else Note("GIF killcam skipped: the bot did not kill the player within 10 s");
            }
            brain.enabled = false; bot.ControlsEnabled = false;
        }

        // ------------------------------------------------------------------ ADS alignment + death/respawn pose integrity
        /// <summary>Where the weapon's ADS aim point sits in camera space (on the axis = looking through the sights) and the gun's roll.</summary>
        private string AdsLine(string label)
        {
            var fa = _soldier.ActiveFirearm;
            var cam = _soldier.ViewCamera;
            var entity = fa != null ? fa.GetComponent<KINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorEntity>() : null;
            Transform ap = entity != null ? entity.defaultAimPoint : null;
            if (fa == null || cam == null || ap == null) return $"ADS-ALIGN [{label}] no aim point (entity={(entity != null)})";
            Vector3 local = cam.transform.InverseTransformPoint(ap.position);
            float roll = Vector3.SignedAngle(cam.transform.up, Vector3.ProjectOnPlane(fa.transform.up, cam.transform.forward), cam.transform.forward);
            float pointing = Vector3.Angle(fa.transform.forward, cam.transform.forward);
            float apAxis = Vector3.Angle(ap.forward, cam.transform.forward);
            return $"ADS-ALIGN [{label}] gun={fa.DisplayName} aimPoint='{ap.name}' in camera x={local.x * 100f:0.0} cm y={local.y * 100f:0.0} cm z={local.z * 100f:0.0} cm, " +
                   $"gunRoll={roll:0.0} deg, gunFwdVsCam={pointing:0.0} deg, aimPointFwdVsCam={apAxis:0.0} deg, aimBlend={_soldier.AimBlend:0.00}";
        }

        private float UserFloat(string name)
        {
            var ui = _soldier.UserInput;
            if (ui == null) return -1f;
            int idx = ui.GetPropertyIndex(name);
            return idx >= 0 ? ui.GetValue<float>(idx) : -1f;
        }

        private static readonly string[] BaseStateNames =
            { "Standing", "Crouching", "C_StandingToCrouch_Humanoid", "C_CrouchToProne_Humanoid", "C_ProneToCrouch_Humanoid", "C_CrouchToStanding_Humanoid", "ProneTree", "Sliding" };

        /// <summary>The hidden driver's Animator: base-layer state (and transition), the locomotion parameters, the graph.</summary>
        private string DriverAnim()
        {
            var a = _soldier.Animator;
            if (a == null) return "anim=none";
            if (!a.isActiveAndEnabled) return "anim=disabled";
            string Name(int h)
            {
                foreach (var n in BaseStateNames) if (Animator.StringToHash("Base Layer." + n) == h) return n.Replace("_Humanoid", "").Replace("C_", "");
                return h.ToString("x8");
            }
            var st = a.GetCurrentAnimatorStateInfo(0);
            string tr = a.IsInTransition(0) ? ">" + Name(a.GetNextAnimatorStateInfo(0).fullPathHash) : "";
            var g = a.playableGraph;
            string graph = g.IsValid() ? (g.IsPlaying() ? "play" : "stop") : "invalid";
            return $"anim={Name(st.fullPathHash)}{tr}@{st.normalizedTime:0.00} spr={a.GetFloat("Sprinting"):G3} fb={a.GetFloat("FullBodyWeight"):G3} " +
                   $"fbEff={_soldier.FullBodyWeightEffective:G3} prw={a.GetFloat("ProneWeight"):G3} prEff={_soldier.ProneWeightEffective:G3} " +
                   $"vel={a.GetFloat("Velocity"):0.00} mv={(a.GetBool("Moving") ? 1 : 0)} crc={(a.GetBool("Crouching") ? 1 : 0)} prn={(a.GetBool("Proning") ? 1 : 0)} graph={graph}";
        }

        /// <summary>Gun pose relative to the camera, hand IK residuals, stance and the Kinemation inputs that sprint/ADS change.</summary>
        private string PoseLine(string label)
        {
            var item = _soldier.ActiveItem != null ? _soldier.ActiveItem.transform : null;
            var cam = _soldier.ViewCamera;
            if (item == null || cam == null) return $"POSE [{label}] item={(item != null)} cam={(cam != null)} dead={_soldier.IsDead}";
            Vector3 lp = cam.transform.InverseTransformPoint(item.position);
            float roll = Vector3.SignedAngle(cam.transform.up, Vector3.ProjectOnPlane(item.up, cam.transform.forward), cam.transform.forward);
            float fwd = Vector3.Angle(item.forward, cam.transform.forward);
            string hands = _proxy != null ? $"handErr L={_proxy.HandErrorLeft * 100f:0.0} R={_proxy.HandErrorRight * 100f:0.0} cm" : "no proxy";
            string grip = "";
            var vis = _proxy != null ? _proxy.Visual : null;
            if (vis != null && vis.isHuman)
            {
                var rh = vis.GetBoneTransform(HumanBodyBones.RightHand);
                if (rh != null) grip = $" rHand-to-gun={Vector3.Distance(rh.position, item.position) * 100f:0.0} cm";
            }
            return $"POSE [{label}] gunFwdVsCam={fwd:0.0} deg gunRoll={roll:0.0} deg gunInCam=({lp.x * 100f:0.0},{lp.y * 100f:0.0},{lp.z * 100f:0.0}) cm {hands}{grip} " +
                   $"stance={_soldier.Movement?.Stance} state={_soldier.Movement?.State} aimBlend={_soldier.AimBlend:0.00} reloading={_soldier.IsReloading} " +
                   $"look={UserFloat("LookLayerWeight"):0.00} stab={UserFloat(KINEMATION.FPSAnimationFramework.Runtime.Core.FPSANames.StabilizationWeight):0.00} " +
                   $"playables={UserFloat(KINEMATION.FPSAnimationFramework.Runtime.Core.FPSANames.PlayablesWeight):0.00} dead={_soldier.IsDead} {DriverAnim()}";
        }

        // ------------------------------------------------------------------ grip check (B26 / B49)
        /// <summary>Palm centre (wrist to middle-finger knuckle midpoint) of a visual hand, in the active gun's local space (cm).</summary>
        private Vector3 PalmInItem(HumanBodyBones hand, HumanBodyBones knuckle, Transform item)
        {
            var vis = _proxy != null ? _proxy.Visual : null;
            if (vis == null || !vis.isHuman || item == null) return new Vector3(float.NaN, 0f, 0f);
            var h = vis.GetBoneTransform(hand);
            var k = vis.GetBoneTransform(knuckle);
            if (h == null) return new Vector3(float.NaN, 0f, 0f);
            Vector3 palm = k != null ? Vector3.Lerp(h.position, k.position, 0.5f) : h.position;
            return item.InverseTransformPoint(palm) * 100f;
        }

        /// <summary>Lowest mesh point per 1 cm slice along the barrel (item z), from every visible renderer of the gun.</summary>
        private string UndersideProfile(Transform item)
        {
            var minY = new SortedDictionary<int, float>();
            var maxY = new SortedDictionary<int, float>();
            int verts = 0;
            foreach (var r in item.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || r is LineRenderer || r is ParticleSystemRenderer || r is TrailRenderer) continue;
                if (r.name.Contains("Laser") || r.name.Contains("Haze") || r.name.Contains("Glow")) continue;
                Mesh m = null;
                bool baked = false;
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null) { m = new Mesh(); smr.BakeMesh(m); baked = true; }
                else { var mf = r.GetComponent<MeshFilter>(); if (mf != null) m = mf.sharedMesh; }
                if (m == null) continue;
#if !UNITY_EDITOR
                if (!m.isReadable) { if (baked) Destroy(m); continue; } // the editor can read any mesh; a player only Read/Write ones
#endif
                var v = m.vertices;
                for (int i = 0; i < v.Length; i++)
                {
                    Vector3 lv = item.InverseTransformPoint(r.transform.TransformPoint(v[i]));
                    if (Mathf.Abs(lv.x) > 0.035f) continue; // centre line only (grip, magazine, trigger guard)
                    int z = Mathf.RoundToInt(lv.z * 100f);
                    if (!minY.TryGetValue(z, out float mn) || lv.y < mn) minY[z] = lv.y;
                    if (!maxY.TryGetValue(z, out float mx) || lv.y > mx) maxY[z] = lv.y;
                    verts++;
                }
                if (baked) Destroy(m);
            }
            var sb = new StringBuilder($"{verts} centre-line verts; z cm: bottom..top cm |");
            foreach (var kv in minY)
                if (kv.Key % 2 == 0) sb.Append($" {kv.Key}:{kv.Value * 100f:0}..{maxY[kv.Key] * 100f:0}");
            return sb.ToString();
        }

        private string GripLine(string label)
        {
            var item = _soldier.ActiveItem != null ? _soldier.ActiveItem.transform : null;
            if (item == null) return $"GRIP [{label}] no item";
            Vector3 r = PalmInItem(HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, item);
            Vector3 l = PalmInItem(HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, item);
            var vis = _proxy != null ? _proxy.Visual : null;
            string idx = "";
            if (vis != null && vis.isHuman)
            {
                var ri = vis.GetBoneTransform(HumanBodyBones.RightIndexDistal);
                if (ri != null) { Vector3 t = item.InverseTransformPoint(ri.position) * 100f; idx = $" rIndexTip=({t.x:0.0},{t.y:0.0},{t.z:0.0})"; }
            }
            var fa = _soldier.ActiveFirearm;
            var def = fa != null ? fa.Definition : null;
            string offs = def != null ? $" defOffsets R=({def.rightGripOffset.x * 100f:0.0},{def.rightGripOffset.y * 100f:0.0},{def.rightGripOffset.z * 100f:0.0}) L=({def.leftGripOffset.x * 100f:0.0},{def.leftGripOffset.y * 100f:0.0},{def.leftGripOffset.z * 100f:0.0})" : "";
            string hands = _proxy != null ? $" handErr L={_proxy.HandErrorLeft * 100f:0.0} R={_proxy.HandErrorRight * 100f:0.0} cm" : "";
            return $"GRIP [{label}] gun={fa?.DisplayName} item-space cm: rPalm=({r.x:0.0},{r.y:0.0},{r.z:0.0}) lPalm=({l.x:0.0},{l.y:0.0},{l.z:0.0}){idx}{offs}{hands}";
        }

        /// <summary>
        /// B91 (user 11-19: "ads is specially bad while moving, not while steady"). In ADS: stand, walk forward, strafe right and
        /// left, walk back; every frame (after all LateUpdates, so BodycamRig's camera motion is included) it records where the
        /// sight line points relative to the view, how far the view itself is off the bullet line (AimDirection), and the camera's
        /// vertical bob. The weapon stays on its authored animation pose. One first-person capture per phase.
        /// </summary>
        private IEnumerator AdsWalkCheck()
        {
            float yaw = OpenYaw();
            _soldier.Teleport(new Vector3(24f, 0.05f, 0f), yaw);
            _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = yaw; _cmd.aimPitch = 0f;
            if (_pistolRun) { _cmd.weaponSlot = 1; yield return Wait(0.9f); }
            yield return Wait(1.0f);
            _cmd.aim = true;
            yield return Wait(0.9f);
            yield return AdsSample("still", 1.2f, Vector2.zero);
            yield return AdsSample("walk-forward", 2.0f, new Vector2(0f, 1f));
            yield return AdsSample("strafe-right", 1.6f, new Vector2(1f, 0f));
            yield return AdsSample("strafe-left", 1.6f, new Vector2(-1f, 0f));
            yield return AdsSample("walk-back", 1.6f, new Vector2(0f, -1f));
            _cmd.move = Vector2.zero;
            _cmd.aim = false;
            yield return Wait(0.5f);
        }

        /// <summary>B87/B93: fire from 4.5 m and 2.2 m, then a forced thunderstorm, looking at open ground (rain, splashes, wet sheen).</summary>
        private IEnumerator FxShots()
        {
            var fires = FindObjectsByType<LR2.World.FireHazard>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            Note($"FX shots: {fires.Length} fire hazards in the scene");
            int shot = 0;
            foreach (var fire in fires)
            {
                if (shot >= 2) break;
                Vector3 f = fire.transform.position;
                foreach (float dist in new[] { 4.5f, 2.2f })
                {
                    Vector3 best = Vector3.zero; bool found = false;
                    for (int i = 0; i < 12 && !found; i++)
                    {
                        Vector3 dir = Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward;
                        Vector3 p = f + dir * dist;
                        if (!UnityEngine.AI.NavMesh.SamplePosition(p, out var hit, 0.8f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                        if (Physics.Linecast(hit.position + Vector3.up * 1.5f, f + Vector3.up * 0.8f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                        best = hit.position; found = true;
                    }
                    if (!found) continue;
                    Vector3 to = f - best;
                    float yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                    _soldier.Teleport(best, yaw);
                    _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = yaw; _cmd.aimPitch = dist < 3f ? 12f : 6f;
                    yield return Wait(2.2f);
                    yield return Capture($"fx_fire_{shot}_{dist:0.0}m", $"fire {shot} at {dist:0.0} m");
                }
                shot++;
            }
            var weather = LR2.World.WeatherSystem.Instance;
            if (weather != null && weather.ForceWeatherNow(LR2.World.WeatherKind.Thunderstorm))
            {
                float yaw = OpenYaw();
                _soldier.Teleport(new Vector3(24f, 0.05f, 0f), yaw);
                _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = yaw; _cmd.aimPitch = -4f;
                yield return Wait(7f);
                Note($"FX rain: effectiveRain={weather.EffectiveRain:0.00} wetness={weather.Wetness:0.00}");
                yield return Capture("fx_rain_storm", "thunderstorm rain");
                _cmd.move = new Vector2(0f, 1f);
                yield return Wait(1.2f);
                yield return Capture("fx_rain_walking", "walking in the rain");
                _cmd.move = Vector2.zero;
            }
            else Note("FX rain: no WeatherSystem / thunderstorm preset in this scene");
        }

        private IEnumerator AdsSample(string label, float seconds, Vector2 move)
        {
            _cmd.move = move;
            yield return Wait(0.4f); // into the gait
            var fa = _soldier.ActiveFirearm;
            var cam = _soldier.ViewCamera;
            var entity = fa != null ? fa.GetComponent<KINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorEntity>() : null;
            Transform ap = entity != null ? entity.defaultAimPoint : null;
            float maxAng = 0f, sumAng = 0f, maxOff = 0f, sumOff = 0f, maxView = 0f, sumView = 0f;
            float yMin = float.MaxValue, yMax = float.MinValue, speed = 0f;
            int n = 0;
            float end = Time.time + seconds;
            var eof = new WaitForEndOfFrame();
            while (Time.time < end && fa != null && cam != null && ap != null && !_soldier.IsDead)
            {
                yield return eof;
                Vector3 local = cam.transform.InverseTransformPoint(ap.position);
                float off = new Vector2(local.x, local.y).magnitude * 100f;
                float ang = Vector3.Angle(ap.forward, cam.transform.forward);
                float view = Vector3.Angle(cam.transform.forward, _soldier.AimDirection);
                maxAng = Mathf.Max(maxAng, ang); sumAng += ang;
                maxOff = Mathf.Max(maxOff, off); sumOff += off;
                maxView = Mathf.Max(maxView, view); sumView += view;
                float camY = cam.transform.position.y - _soldier.transform.position.y;
                yMin = Mathf.Min(yMin, camY); yMax = Mathf.Max(yMax, camY);
                speed += _soldier.Movement != null ? _soldier.Movement.Speed : 0f;
                n++;
            }
            if (n == 0) { Note($"ADS-MOVE [{label}] no samples (gun/camera/aim point missing)"); yield break; }
            Note($"ADS-MOVE [{label}] gun={fa.DisplayName} frames={n} speed={speed / n:0.00} m/s aimBlend={_soldier.AimBlend:0.00} | " +
                 $"sight vs view: angle max {maxAng:0.00} mean {sumAng / n:0.00} deg, offset max {maxOff:0.00} mean {sumOff / n:0.00} cm | " +
                 $"view vs bullet line: max {maxView:0.00} mean {sumView / n:0.00} deg | camera bob {(yMax - yMin) * 100f:0.0} cm | authored weapon pose");
            yield return Capture($"adsmove_{label}", $"ADS {label}");
        }

        private IEnumerator GripCheck()
        {
            float yaw = OpenYaw();
            _soldier.Teleport(new Vector3(24f, 0.05f, 0f), yaw);
            _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = yaw;
            yield return Wait(1.6f);
            var item = _soldier.ActiveItem != null ? _soldier.ActiveItem.transform : null;
            Note(GripLine("rifle hip"));
            if (item != null) Note($"PROFILE [{_soldier.ActiveFirearm?.DisplayName}] {UndersideProfile(item)}");
            yield return Capture("grip_rifle_left", null, View.HandsLeft, true);
            yield return Capture("grip_rifle_right", null, View.HandsRight, true);
            _cmd.aim = true; yield return Wait(1.3f);
            Note(GripLine("rifle ADS"));
            yield return Capture("grip_rifle_ads_right", null, View.HandsRight, true);
            _cmd.aim = false; yield return Wait(0.6f);

            _cmd.weaponSlot = 1; yield return Wait(1.6f);
            item = _soldier.ActiveItem != null ? _soldier.ActiveItem.transform : null;
            Note(GripLine("pistol hip"));
            if (item != null) Note($"PROFILE [{_soldier.ActiveFirearm?.DisplayName}] {UndersideProfile(item)}");
            yield return Capture("grip_pistol_left", null, View.HandsLeft, true);
            yield return Capture("grip_pistol_right", null, View.HandsRight, true);
            yield return Capture("grip_pistol_fp", "pistol first person", View.FirstPerson);
            _cmd.aim = true; yield return Wait(1.3f);
            Note(GripLine("pistol ADS"));
            yield return Capture("grip_pistol_ads_fp", "pistol ADS first person", View.FirstPerson);
            _cmd.aim = false; yield return Wait(0.5f);
        }

        /// <summary>
        /// B136: every Kinemation demo feature LR2 now wires up, captured in order: sight-group cycling in ADS, fire-mode switch,
        /// attachment editing (inspect pose + each group cycled), a grenade throw and its explosion, the knife (with a stab on
        /// the frozen enemy at 1.3 m) and the torch (lit, then off), then back to the rifle.
        /// </summary>
        private IEnumerator ExtrasCheck()
        {
            float yaw = OpenYaw();
            _soldier.Teleport(new Vector3(24f, 0.05f, 0f), yaw);
            _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = yaw;
            yield return Wait(1.6f);
            var gun = _soldier.ActiveFirearm;
            Note($"EXTRAS gun={gun?.DisplayName} scope={gun?.ActiveScope?.name} editable={gun?.HasEditableAttachments} " +
                 $"kinemationAttachments={gun?.UsesKinemationAttachments} grenades={_soldier.GrenadesLeft} items={_soldier.Items.Count}");
            _cmd.aim = true; yield return Wait(1.2f);
            yield return Capture("ex_01_ads_sight0", $"ADS sight {gun?.ActiveScope?.name}");
            _cmd.cycleScope = true; yield return Wait(1.0f);
            Note($"EXTRAS cycled sight -> {gun?.ActiveScope?.name}");
            yield return Capture("ex_02_ads_sight1", $"ADS sight {gun?.ActiveScope?.name}");
            _cmd.cycleScope = true; yield return Wait(0.8f);
            _cmd.aim = false; yield return Wait(0.6f);

            var mode = gun != null ? gun.FireMode.ToString() : "-";
            _cmd.fireMode = true; yield return Wait(0.3f);
            Note($"EXTRAS fire mode {mode} -> {(gun != null ? gun.FireMode.ToString() : "-")}");

            _cmd.inspect = true; yield return Wait(0.9f);
            Note($"EXTRAS inspect editing={_soldier.IsEditingAttachments}");
            yield return Capture("ex_03_inspect", "attachment editing");
            for (int g = 1; g <= 3; g++)
            {
                _cmd.attachmentSlot = g; yield return Wait(0.6f);
                Note($"EXTRAS cycled group {g}: scope={gun?.ActiveScope?.name} suppressed={gun?.IsSuppressed}");
                yield return Capture($"ex_04_inspect_group{g}", $"attachment group {g} cycled");
            }
            _cmd.inspect = true; yield return Wait(0.8f);
            Note($"EXTRAS inspect left editing={_soldier.IsEditingAttachments}");

            // throw down the clearest lane (the first run faced a wall 2 m away: the frag bounced back and killed the thrower)
            float bestYaw = _cmd.aimYaw, bestDist = 0f;
            for (int i = 0; i < 16; i++)
            {
                float y = i * 22.5f;
                Vector3 d = Quaternion.Euler(0f, y, 0f) * Vector3.forward;
                float dist = Physics.Raycast(_soldier.AimOrigin, d, out var hitInfo, 60f, LR2.Core.LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) ? hitInfo.distance : 60f;
                if (dist > bestDist) { bestDist = dist; bestYaw = y; }
            }
            _cmd.aimYaw = bestYaw; _cmd.aimPitch = -10f; yield return Wait(0.6f);
            Note($"EXTRAS grenade lane yaw={bestYaw:0} clear={bestDist:0.0} m");
            int before = _soldier.GrenadesLeft;
            _cmd.grenade = true; yield return Wait(0.3f);
            yield return Capture("ex_05_grenade_throw", "grenade throw");
            yield return Wait(1.0f);
            Note($"EXTRAS grenades {before} -> {_soldier.GrenadesLeft} throwing={_soldier.IsThrowingGrenade}");
            yield return Capture("ex_06_after_throw", "after the throw");
            yield return Wait(2.0f);
            yield return Capture("ex_07_explosion", "grenade explosion");
            yield return Wait(0.8f);
            if (_soldier.IsDead)
            {
                Note("EXTRAS the thrower died in the blast; respawning for the knife and torch steps");
                _soldier.Respawn(new Vector3(24f, 0.05f, 0f), yaw);
                yield return Wait(1.2f);
            }
            _cmd.aimYaw = yaw; _cmd.aimPitch = 0f; yield return Wait(0.4f);

            _cmd.weaponSlot = 2; yield return Wait(1.4f);
            Note($"EXTRAS slot 3 item={_soldier.ActiveItem?.DisplayName} ads={_soldier.ActiveItem?.SupportsAds}");
            yield return Capture("ex_08_knife", "knife");
            if (_enemy != null)
            {
                Vector3 fwd = Quaternion.Euler(0f, _cmd.aimYaw, 0f) * Vector3.forward;
                if (_enemy.IsDead) _enemy.Respawn(_soldier.transform.position + fwd * 1.3f, _cmd.aimYaw + 180f);
                else _enemy.Teleport(_soldier.transform.position + fwd * 1.3f, _cmd.aimYaw + 180f);
                yield return Wait(0.5f);
            }
            float hpBefore = _enemy != null && _enemy.Health != null ? _enemy.Health.Current + _enemy.Health.Armor : -1f;
            _cmd.fire = true; yield return Wait(0.16f);
            yield return Capture("ex_09_knife_stab", "knife stab");
            _cmd.fire = false; yield return Wait(0.6f);
            float hpAfter = _enemy != null && _enemy.Health != null ? _enemy.Health.Current + _enemy.Health.Armor : -1f;
            Note($"EXTRAS knife enemy hp+armour {hpBefore:0} -> {hpAfter:0} dead={(_enemy != null && _enemy.IsDead)}");
            yield return Capture("ex_10_knife_side", null, View.Side);

            _cmd.weaponSlot = 3; yield return Wait(1.4f);
            var torch = _soldier.ActiveItem as LR2.Weapons.LR2Torch;
            Note($"EXTRAS slot 4 item={_soldier.ActiveItem?.DisplayName} lit={(torch != null && torch.IsLit)}");
            yield return Capture("ex_11_torch", "torch");
            yield return Capture("ex_12_torch_front", null, View.Front);
            _cmd.fire = true; yield return Wait(0.1f); _cmd.fire = false; yield return Wait(0.5f);
            Note($"EXTRAS torch toggled lit={(torch != null && torch.IsLit)}");
            yield return Capture("ex_13_torch_off", "torch off");

            _cmd.weaponSlot = 0; yield return Wait(1.4f);
            Note($"EXTRAS back to {_soldier.ActiveItem?.DisplayName}");
            yield return Capture("ex_14_rifle_back", "rifle back");
        }

        private IEnumerator DeathTest()
        {
            float yaw = OpenYaw();
            // ADS sight alignment of this run's gun (MK18 "sideways in ADS", AK-74N "not looking through the irons")
            _soldier.Teleport(new Vector3(24f, 0.05f, 0f), yaw);
            _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = yaw;
            yield return Wait(0.8f);
            Note(AdsLine("hip"));
            _cmd.aim = true;
            yield return Wait(1.3f);
            Note(AdsLine("ads"));
            Note(PoseLine("ads reference"));
            yield return Capture("ads_00_reference", "ADS reference");
            _cmd.aim = false;
            yield return Wait(0.6f);
            Note(PoseLine("hip reference"));

            string[] cases = { "standing", "ads", "crouched", "sprinting", "reloading", "prone" };
            string only = null;
            var argv = Environment.GetCommandLineArgs();
            for (int i = 0; i < argv.Length - 1; i++) if (argv[i] == "-lr2DeathCases") only = "," + argv[i + 1].ToLowerInvariant() + ",";
            foreach (var c in cases)
            {
                if (only != null && !only.Contains("," + c + ",")) continue;
                if (_soldier.IsDead) { float w = Time.time + 12f; while (_soldier.IsDead && Time.time < w) yield return null; }
                _soldier.Teleport(new Vector3(24f, 0.05f, 0f), yaw);
                _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = yaw;
                yield return Wait(1.0f);
                switch (c)
                {
                    case "ads": _cmd.aim = true; break;
                    case "crouched": _cmd.crouch = true; yield return null; _cmd.crouch = false; break;
                    case "sprinting": _cmd.move = new Vector2(0f, 1f); _cmd.sprint = true; break;
                    case "reloading": _cmd.fire = true; yield return Wait(0.3f); _cmd.fire = false; yield return Wait(0.2f); _cmd.reload = true; break;
                    case "prone": _cmd.prone = true; yield return null; _cmd.prone = false; break;
                }
                yield return Wait(c == "prone" ? 1.9f : c == "reloading" ? 0.6f : 1.0f);
                Note(PoseLine($"{c} before death"));
                yield return Capture($"death_{c}_0_before", $"{c}, before death");
                _soldier.Health.Kill();
                _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = yaw;
                yield return Wait(0.6f);
                yield return Capture($"death_{c}_1_dead", $"{c}, 0.6 s after death");
                float until = Time.time + 14f;
                while (_soldier.IsDead && Time.time < until) yield return null;
                if (_soldier.IsDead) { Note($"DEATH [{c}] not respawned within 14 s"); continue; }
                _cmd.aimYaw = _soldier.Yaw;
                if (_pistolRun) { _cmd.weaponSlot = 1; yield return Wait(0.9f); }
                float[] marks = { 0.1f, 0.5f, 1.5f, 3.0f };
                float t0 = Time.time;
                foreach (float m in marks)
                {
                    while (Time.time - t0 < m) yield return null;
                    Note(PoseLine($"{c} after respawn +{m:0.0}s"));
                    if (m == 0.5f) yield return Capture($"death_{c}_2_respawned", $"{c}, 0.5 s after respawn");
                }
                // ADS after the respawn (user: "MK18 goes sideways in ADS", "AK-74N not looking through the irons")
                _cmd.aim = true; yield return Wait(1.2f);
                Note(AdsLine($"{c} ADS after respawn"));
                _cmd.aim = false; yield return Wait(0.5f);
                _cmd.fire = true; yield return Wait(0.12f); _cmd.fire = false; yield return Wait(0.7f);
                Note(PoseLine($"{c} after respawn + one shot"));
                yield return Capture($"death_{c}_3_after_shot", $"{c}, after one shot");
            }

            // Wall contact (user 14:20: "hitting walls when the gun gets back, that's when everything starts to break")
            yield return WallCase("walk", false);
            yield return WallCase("sprint", true);
        }

        private bool _pistolRun => Array.Exists(Environment.GetCommandLineArgs(), a => a.Trim().ToLowerInvariant().Contains("mk23"));

        /// <summary>Walk or sprint into the nearest wall, hold contact, back off: does the gun/hand pose return to the reference?</summary>
        private IEnumerator WallCase(string label, bool sprint)
        {
            if (_soldier.IsDead) { float w = Time.time + 12f; while (_soldier.IsDead && Time.time < w) yield return null; }
            Vector3 start = new Vector3(24f, 0.05f, 0f);
            // nearest wall from the start point (8 directions), at least 2.5 m away so the walk-up is a real approach
            float bestYaw = 0f, bestD = float.MaxValue;
            for (int i = 0; i < 16; i++)
            {
                float y = i * 22.5f;
                Vector3 dir = Quaternion.Euler(0f, y, 0f) * Vector3.forward;
                if (Physics.Raycast(start + Vector3.up * 1.3f, dir, out var hit, 30f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) && hit.distance > 2.5f && hit.distance < bestD)
                { bestD = hit.distance; bestYaw = y; }
            }
            if (bestD == float.MaxValue) { Note($"WALL [{label}] no wall within 30 m"); yield break; }
            _soldier.Teleport(start, bestYaw);
            _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = bestYaw;
            if (_pistolRun) { _cmd.weaponSlot = 1; yield return Wait(0.9f); }
            yield return Wait(0.8f);
            Note(PoseLine($"wall-{label} reference ({bestD:0.0} m from the wall)"));
            _cmd.move = new Vector2(0f, 1f);
            _cmd.sprint = sprint;
            float speed = sprint ? 6.1f : 3.5f;
            yield return Wait(bestD / speed * 0.55f);
            Note(PoseLine($"wall-{label} open ground, mid-approach"));
            yield return Wait(bestD / speed * 0.45f + 0.9f);   // arrive and keep pushing into the wall
            Note(PoseLine($"wall-{label} pressed into the wall"));
            yield return Capture($"wall_{label}_1_contact", $"{label} into the wall");
            yield return Wait(0.8f);
            _cmd.sprint = false;
            _cmd.move = new Vector2(0f, -1f);
            yield return Wait(0.9f);
            _cmd.move = Vector2.zero;
            float t0 = Time.time;
            foreach (float m in new[] { 0.2f, 1.0f, 2.5f })
            {
                while (Time.time - t0 < m) yield return null;
                Note(PoseLine($"wall-{label} backed off +{m:0.0}s"));
            }
            yield return Capture($"wall_{label}_2_after", $"{label}: 2.5 s after backing off the wall");
            _cmd.aim = true; yield return Wait(1.2f);
            Note(AdsLine($"wall-{label} ADS after the wall"));
            _cmd.aim = false; yield return Wait(0.5f);
        }

        // ------------------------------------------------------------------ B125: ADS against obstacles, and death
        private string _adsCase;
        private bool _adsRecording;
        private readonly System.Text.StringBuilder _adsCsv = new System.Text.StringBuilder();
        private readonly Dictionary<string, AdsStats> _adsStats = new Dictionary<string, AdsStats>();

        private sealed class AdsStats
        {
            public int frames, adsFrames;
            public float maxMuzzle, maxRoll, maxJump, maxPush, maxFeedback, maxFeedbackAngle, maxIkReach, pushGrowth;
            public float firstPush = -1f, lastPush;
        }

        /// <summary>
        /// B125: the player aims down sights into walls (head on, at 45 degrees, sliding along), into another soldier, and dies
        /// against a wall while aiming, then aims again after the respawn. Every frame: ADS weight, gun muzzle and roll against the
        /// camera, frame-to-frame jump, B59b eye-plane push, how far LR2's edits had moved the gun before Kinemation read it, the
        /// driver's weapon bone distance from its head (a runaway shows as metres). PASS lines at the end; the known-bad A/B is
        /// <c>-lr2NoItemReset -lr2NoFlipGuard</c>.
        /// </summary>
        private IEnumerator AdsWallCheck()
        {
            _adsCsv.AppendLine("case,t,ads,muzzleDeg,rollDeg,jumpDeg,eyePlane,push,feedbackCm,feedbackDeg,ikReach,guard,dead");
            StartCoroutine(AdsRecorder());
            Note($"ADSWALL itemReset={SoldierController.ItemFeedbackReset} flipGuard={RetargetProxy.FlipGuardEnabled}");
            Vector3 start = new Vector3(24f, 0.05f, 0f);
            float wallYaw = 0f, wallD = float.MaxValue;
            for (int i = 0; i < 16; i++)
            {
                float y = i * 22.5f;
                Vector3 dir = Quaternion.Euler(0f, y, 0f) * Vector3.forward;
                if (Physics.Raycast(start + Vector3.up * 1.3f, dir, out var hit, 30f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) && hit.distance > 2.5f && hit.distance < wallD)
                { wallD = hit.distance; wallYaw = y; }
            }
            if (wallD == float.MaxValue) { Note("ADSWALL no wall within 30 m"); yield break; }
            Note($"ADSWALL wall {wallD:0.0} m at yaw {wallYaw:0}");

            // 1) ADS straight into the wall, hold, slide left and right along it, back off
            yield return AdsWallRun("head_on", start, wallYaw, wallD, 0f, true);
            // 2) the same at 45 degrees to the wall
            yield return AdsWallRun("angle45", start, wallYaw + 45f, wallD * 1.41f, 0f, false);
            // 3) ADS into another soldier standing in the way
            if (_enemy != null)
            {
                if (_soldier.IsDead) { float w = Time.time + 12f; while (_soldier.IsDead && Time.time < w) yield return null; }
                float openYaw = OpenYaw();
                _soldier.Teleport(start, openYaw);
                _enemy.Teleport(start + Quaternion.Euler(0f, openYaw, 0f) * Vector3.forward * 2.2f, openYaw + 180f);
                _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = openYaw;
                yield return Wait(0.8f);
                _adsCase = "soldier"; _cmd.aim = true; yield return Wait(0.9f);
                _cmd.move = new Vector2(0f, 1f); yield return Wait(2.6f);
                yield return Capture("adswall_soldier_contact", "ADS pushed into a soldier");
                _cmd.move = new Vector2(1f, 0f); yield return Wait(1.0f);
                _cmd.move = Vector2.zero; _cmd.aim = false; yield return Wait(0.6f);
                _adsCase = null;
            }
            // 4) die against the wall while aiming, respawn, aim again
            yield return AdsWallRun("death", start, wallYaw, wallD, 0f, false, kill: true);

            _adsRecording = false;
            File.WriteAllText(Path.Combine(_outDir, "adswall.csv"), _adsCsv.ToString());
            foreach (var kv in _adsStats)
            {
                var s = kv.Value;
                bool runaway = s.pushGrowth > 0.10f || s.maxIkReach > 0.9f;
                bool pass = s.maxMuzzle < 20f && s.maxRoll < 20f && s.maxJump < 25f && !runaway;
                Note($"ADSWALL {(pass ? "PASS" : "FAIL")} [{kv.Key}] frames={s.frames} adsFrames={s.adsFrames} maxMuzzleVsCam={s.maxMuzzle:0.0} maxRoll={s.maxRoll:0.0} " +
                     $"maxJump={s.maxJump:0.0}deg/frame maxPush={s.maxPush * 100f:0.0}cm pushGrowth={s.pushGrowth * 100f:0.0}cm " +
                     $"maxFeedback={s.maxFeedback * 100f:0.0}cm/{s.maxFeedbackAngle:0.0}deg maxIkReach={s.maxIkReach:0.00}m");
            }
        }

        /// <summary>One wall run: aim, walk into the wall and keep pushing, slide along it, back off (or die against it).</summary>
        private IEnumerator AdsWallRun(string label, Vector3 start, float yaw, float dist, float pitch, bool slide, bool kill = false)
        {
            if (_soldier.IsDead) { float w = Time.time + 12f; while (_soldier.IsDead && Time.time < w) yield return null; }
            _soldier.Teleport(start, yaw);
            _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = yaw; _cmd.aimPitch = pitch;
            yield return Wait(0.8f);
            _adsCase = label;
            _cmd.aim = true; yield return Wait(0.9f);
            _cmd.move = new Vector2(0f, 1f);
            yield return Wait(dist / 2.2f + 1.6f);        // ADS walk is slow: arrive, then keep pressing into the wall
            yield return Capture($"adswall_{label}_1_contact", $"ADS {label} pressed into the wall");
            if (slide)
            {
                _cmd.move = new Vector2(-1f, 0.6f); yield return Wait(1.6f);
                yield return Capture($"adswall_{label}_2_slide", $"ADS {label} sliding along the wall");
                _cmd.move = new Vector2(1f, 0.6f); yield return Wait(1.6f);
            }
            if (kill)
            {
                yield return Wait(0.6f);
                _soldier.Health.Kill();
                _cmd = SoldierInput.Empty; _cmd.absoluteLook = true; _cmd.aimYaw = yaw;
                float until = Time.time + 14f;
                while (_soldier.IsDead && Time.time < until) yield return null;
                if (_soldier.IsDead) { Note("ADSWALL [death] no respawn within 14 s"); _adsCase = null; yield break; }
                _cmd.aimYaw = _soldier.Yaw;
                yield return Wait(0.4f);
                _adsCase = "death_respawn_ads";
                _cmd.aim = true; yield return Wait(1.6f);
                yield return Capture("adswall_death_respawn_ads", "ADS right after the respawn");
                _cmd.move = new Vector2(0f, 1f); yield return Wait(2.0f);   // and walk while aiming
                _cmd.move = Vector2.zero;
            }
            _cmd.move = new Vector2(0f, -1f); yield return Wait(1.0f);
            _cmd.move = Vector2.zero;
            yield return Capture($"adswall_{label}_3_after", $"ADS {label} after backing off");
            _cmd.aim = false; yield return Wait(0.6f);
            _adsCase = null;
        }

        /// <summary>Per-frame recorder for <see cref="AdsWallCheck"/> (after this frame's animation and LR2 edits).</summary>
        private IEnumerator AdsRecorder()
        {
            _adsRecording = true;
            Quaternion prevLocal = Quaternion.identity; bool havePrev = false;
            var eof = new WaitForEndOfFrame();
            while (_adsRecording)
            {
                yield return eof;
                if (_adsCase == null || _soldier == null) { havePrev = false; continue; }
                var cam = _soldier.ViewCamera;
                var item = _soldier.ActiveItem != null ? _soldier.ActiveItem.transform : null;
                var fa = _soldier.ActiveFirearm;
                bool dead = _soldier.IsDead;
                if (cam == null || item == null || dead)
                {
                    _adsCsv.AppendLine(FormattableString.Invariant($"{_adsCase},{Time.time:0.000},,,,,,,,,,,{(dead ? 1 : 0)}"));
                    havePrev = false; continue;
                }
                float ads = Mathf.Max(0f, UserFloat("AimingWeight"));
                Transform muzzle = fa != null && fa.Muzzle != null ? fa.Muzzle : item;
                float muzzleDeg = Vector3.Angle(muzzle.forward, cam.transform.forward);
                float rollDeg = Mathf.Abs(Vector3.SignedAngle(Vector3.ProjectOnPlane(cam.transform.up, muzzle.forward), Vector3.ProjectOnPlane(item.up, muzzle.forward), muzzle.forward));
                Quaternion local = Quaternion.Inverse(cam.transform.rotation) * item.rotation;
                float jump = havePrev ? Quaternion.Angle(prevLocal, local) : 0f;
                prevLocal = local; havePrev = true;
                var head = _soldier.Animator != null ? _soldier.Animator.GetBoneTransform(HumanBodyBones.Head) : null;
                float reach = head != null && item.parent != null ? Vector3.Distance(item.parent.position, head.position) : -1f;
                float push = _proxy != null ? _proxy.LastEyePlanePush : 0f, eye = _proxy != null ? _proxy.LastEyePlaneDist : 0f;
                float fb = _soldier.LastItemFeedbackOffset, fbDeg = _soldier.LastItemFeedbackAngle;
                float guard = _proxy != null ? _proxy.FlipGuardAngle : 0f;
                _adsCsv.AppendLine(FormattableString.Invariant(
                    $"{_adsCase},{Time.time:0.000},{ads:0.00},{muzzleDeg:0.0},{rollDeg:0.0},{jump:0.0},{eye:0.000},{push:0.000},{fb * 100f:0.0},{fbDeg:0.0},{reach:0.000},{guard:0.0},0"));
                if (!_adsStats.TryGetValue(_adsCase, out var st)) _adsStats[_adsCase] = st = new AdsStats();
                st.frames++;
                st.maxPush = Mathf.Max(st.maxPush, push);
                st.maxFeedback = Mathf.Max(st.maxFeedback, fb);
                st.maxFeedbackAngle = Mathf.Max(st.maxFeedbackAngle, fbDeg);
                st.maxIkReach = Mathf.Max(st.maxIkReach, reach);
                if (ads > 0.9f)
                {
                    st.adsFrames++;
                    st.maxMuzzle = Mathf.Max(st.maxMuzzle, muzzleDeg);
                    st.maxRoll = Mathf.Max(st.maxRoll, rollDeg);
                    st.maxJump = Mathf.Max(st.maxJump, jump);
                    if (st.firstPush < 0f) st.firstPush = push;
                    st.lastPush = push;
                    st.pushGrowth = Mathf.Max(st.pushGrowth, push - st.firstPush);
                }
            }
        }

        /// <summary>Scripted bot input: absolute aim at the target's chest, ADS held, trigger on demand.</summary>
        private sealed class ScriptedShooter : ISoldierInputSource
        {
            public SoldierController target;
            public bool fire;

            public void GetInput(SoldierController s, ref SoldierInput input)
            {
                input = SoldierInput.Empty;
                input.absoluteLook = true;
                if (target != null)
                {
                    Vector3 eye = s.ViewTransform != null ? s.ViewTransform.position : s.transform.position + Vector3.up * 1.6f;
                    Vector3 d = target.transform.position + Vector3.up * 1.3f - eye;
                    input.aimYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    input.aimPitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
                }
                input.aim = true;
                input.fire = fire;
            }
        }

        private IEnumerator KillcamCheck()
        {
            var kd = LR2.World.KillcamDirector.Instance;
            if (kd == null) { Note("KILLCAM skipped: no KillcamDirector in scene"); yield break; }
            SoldierController killer = null;
            foreach (var c in Combatant.All)
            {
                if (c == null || c.gameObject == _soldier.gameObject) continue;
                var sc = c.GetComponent<SoldierController>();
                if (sc != null && !sc.IsDead && _soldier.Combatant != null && TeamUtil.AreHostile(c, _soldier.Combatant)) { killer = sc; break; }
            }
            if (killer == null) { Note("KILLCAM skipped: no hostile soldier"); yield break; }

            // face each other 8 m apart on the open lane; the player stands still and watches the killer
            _soldier.Teleport(new Vector3(24f, 0.05f, 0f), OpenYaw());
            _cmd = SoldierInput.Empty;
            _cmd.absoluteLook = true;
            _cmd.aimYaw = OpenYaw();
            yield return Wait(0.3f);
            Vector3 fwd = Quaternion.Euler(0f, _cmd.aimYaw, 0f) * Vector3.forward;
            killer.Teleport(_soldier.transform.position + fwd * 8f, _cmd.aimYaw + 180f);
            var shooter = new ScriptedShooter { target = _soldier };
            killer.InputSource = shooter;
            killer.ControlsEnabled = true;
            yield return Wait(1.4f); // ADS settles

            // Which eye point frames the bot's gun like the player's own view? Render both with the head hidden.
            var kp = killer.GetComponent<RetargetProxy>();
            var kCam = killer.ViewCamera;
            if (kCam != null && kp != null && killer.ActiveFirearm != null)
            {
                Quaternion aim = Quaternion.Euler(killer.Pitch, killer.Yaw, 0f);
                float vfov = _soldier.ViewCamera != null ? _soldier.ViewCamera.fieldOfView : 60f;
                Vector3 drv = kCam.transform.position, vis = kp.VisualEyePosition;
                Vector3 dl = Quaternion.Inverse(aim) * (vis - drv);
                Note($"KILLCAM-EYE driverCam={drv} visualEye={vis} |d|={(vis - drv).magnitude:0.000} m (aim-local right={dl.x:0.000} up={dl.y:0.000} fwd={dl.z:0.000}) camRotVsAim={Quaternion.Angle(kCam.transform.rotation, aim):0.0} deg");
                killer.SetHeadHidden(true);
                yield return RenderFrom(drv, aim, vfov, "kc_00a_killer_from_driverCam", killer.ActiveFirearm.gameObject);
                yield return RenderFrom(vis, aim, vfov, "kc_00b_killer_from_visualEye", killer.ActiveFirearm.gameObject);
                killer.SetHeadHidden(false);
            }

            shooter.fire = true;
            float until = Time.time + 9f;
            while (!_soldier.IsDead && Time.time < until) yield return null;
            shooter.fire = false;
            killer.ControlsEnabled = false;
            Note($"KILLCAM-SETUP killer={killer.name} weapon={killer.ActiveFirearm?.DisplayName} playerDead={_soldier.IsDead} armour={(_soldier.Health != null ? _soldier.Health.Armor : -1f):0}");
            if (!_soldier.IsDead) { Note("KILLCAM skipped: player not killed within 9 s"); yield break; }

            until = Time.time + 3f;
            while (!LR2.World.KillcamDirector.IsPlaying && Time.time < until) yield return null;
            if (!LR2.World.KillcamDirector.IsPlaying) { Note("KILLCAM did not start within 3 s of the kill"); yield break; }
            float start = Time.time, dur = kd.Duration;
            float[] marks = { 0.06f, 0.30f, 0.50f, 0.60f, 0.68f, 0.76f, 0.86f, 0.97f };
            for (int i = 0; i < marks.Length; i++)
            {
                while (Time.time < start + marks[i] * dur && LR2.World.KillcamDirector.IsPlaying) yield return null;
                if (!LR2.World.KillcamDirector.IsPlaying) { Note($"KILLCAM ended before mark {marks[i]:0.00}"); break; }
                KillcamMetrics($"{marks[i]:0.00}");
                yield return Capture($"kc_{i + 1:00}_replay_{Mathf.RoundToInt(marks[i] * 100f):00}pct", null);
            }
        }

        private IEnumerator RenderFrom(Vector3 pos, Quaternion rot, float vfov, string name, GameObject gun)
        {
            yield return new WaitForEndOfFrame();
            var go = new GameObject("PoseCheckKillcamProbe");
            try
            {
                go.transform.SetPositionAndRotation(pos, rot);
                var cam = go.AddComponent<Camera>();
                cam.fieldOfView = vfov;
                cam.nearClipPlane = 0.02f;
                go.AddComponent<HDAdditionalCameraData>();
                var sb = new StringBuilder($"KILLCAM-PROBE [{name}]");
                AppendViewport(sb, "gun", cam, gun);
                Note(sb.ToString());
                RenderCamera(cam, name);
            }
            finally { Destroy(go); }
        }

        private string LiveGunLine(string label)
        {
            var sb = new StringBuilder($"LIVE-GUN [{label}]");
            var cam = _soldier != null ? _soldier.ViewCamera : null;
            if (cam != null && _soldier.ActiveFirearm != null) AppendViewport(sb, "gun", cam, _soldier.ActiveFirearm.gameObject);
            if (cam != null && _proxy != null)
            {
                Quaternion aim = cam.transform.rotation;
                Vector3 dl = Quaternion.Inverse(aim) * (_proxy.VisualEyePosition - cam.transform.position);
                sb.Append($" visualEye-lens aim-local=({dl.x:0.000},{dl.y:0.000},{dl.z:0.000})");
            }
            return sb.ToString();
        }

        private void KillcamMetrics(string label)
        {
            var kd = LR2.World.KillcamDirector.Instance;
            var cam = _soldier.ViewCamera;
            if (kd == null || cam == null) return;
            var sb = new StringBuilder($"KILLCAM [{label}] t-kill={kd.DebugSampleTime - kd.DebugKillTime:+0.00;-0.00} puppets={kd.DebugPuppets} " +
                                      $"shots={kd.DebugReplayShots} hits={kd.DebugReplayHits} vFov={cam.fieldOfView:0.0} cam={cam.transform.position}");
            AppendViewport(sb, "gun", cam, kd.DebugKillerGunPuppet);
            AppendViewport(sb, "victim", cam, kd.DebugVictimPuppet);
            var puppet = kd.DebugKillerPuppet;
            if (puppet != null)
            {
                // renderers of the killer's own body the lens sits in or within 4 cm of (they draw across the screen)
                var hidden = new List<string>();
                foreach (var r in puppet.GetComponentsInChildren<Renderer>())
                    if (r.enabled && r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) hidden.Add(r.name);
                sb.Append($" killerFirstPersonHidden=[{string.Join(",", hidden)}] (empty = killer head drawn across the lens)");
            }
            Note(sb.ToString());
        }

        /// <summary>Viewport centre and extent of a GameObject's visible (non shadow-only, non particle) renderers.</summary>
        private static void AppendViewport(StringBuilder sb, string tag, Camera cam, GameObject go)
        {
            if (go == null) { sb.Append($" {tag}=none"); return; }
            Bounds b = default;
            bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                if (r.name.IndexOf("LaserDot", StringComparison.OrdinalIgnoreCase) >= 0) continue; // metres away, not the gun
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            if (!any) { sb.Append($" {tag}=no-visible-renderers"); return; }
            Vector3 c = cam.WorldToViewportPoint(b.center);
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                Vector3 v = cam.WorldToViewportPoint(corner);
                if (v.z <= 0f) continue;
                x0 = Mathf.Min(x0, v.x); x1 = Mathf.Max(x1, v.x); y0 = Mathf.Min(y0, v.y); y1 = Mathf.Max(y1, v.y);
            }
            sb.Append($" {tag}VP=({c.x:0.00},{c.y:0.00}) depth={c.z:0.00} ext=[x {x0:0.00}..{x1:0.00}, y {y0:0.00}..{y1:0.00}]");
        }

        /// <summary>
        /// Foot-slide probe on the DRIVER (pure animation): while moving forward, the lower foot is the planted one, so its
        /// world speed along the move direction is the slide. Natural clip speed = (move speed - slide) / LocoSpeed;
        /// set SoldierMovement's *AnimReferenceSpeed to that value for zero foot sliding.
        /// </summary>
        /// <summary>Touchdown = first frame a visible foot is back within 2 cm of its lowest height after rising 3.5 cm above it.</summary>
        private static List<float> Touchdowns(List<float> t, List<float> h)
        {
            var result = new List<float>();
            float min = float.MaxValue;
            foreach (var v in h) min = Mathf.Min(min, v);
            bool lifted = false;
            for (int i = 0; i < h.Count; i++)
            {
                if (h[i] > min + 0.035f) lifted = true;
                else if (lifted && h[i] < min + 0.02f) { result.Add(t[i]); lifted = false; }
            }
            return result;
        }

        private static string FootSync(string label, List<float> t, List<float> hl, List<float> hr, List<float> steps)
        {
            var downs = Touchdowns(t, hl);
            downs.AddRange(Touchdowns(t, hr));
            downs.Sort();
            var dts = new List<float>();
            int unmatchedSteps = 0;
            foreach (float st in steps)
            {
                float best = float.MaxValue;
                foreach (float d in downs) best = Mathf.Min(best, Mathf.Abs(st - d));
                if (best <= 0.15f) dts.Add(best * 1000f); else unmatchedSteps++;
            }
            int missed = 0;
            foreach (float d in downs)
            {
                float best = float.MaxValue;
                foreach (float st in steps) best = Mathf.Min(best, Mathf.Abs(st - d));
                if (best > 0.15f) missed++;
            }
            dts.Sort();
            string stats = dts.Count > 0
                ? $"median|dt|={dts[dts.Count / 2]:0} ms p90={dts[Mathf.Min(dts.Count - 1, (int)(dts.Count * 0.9f))]:0} ms max={dts[dts.Count - 1]:0} ms"
                : "no matched steps";
            return $"FOOTSYNC [{label}] footstepEvents={steps.Count} visibleTouchdowns={downs.Count} {stats} " +
                   $"stepsWithoutTouchdown={unmatchedSteps} touchdownsWithoutStep={missed} (match window 150 ms)";
        }

        private IEnumerator MeasureGait(string label, float seconds)
        {
            var drv = _proxy != null ? _proxy.Driver : null;
            var lf = drv != null ? drv.GetBoneTransform(HumanBodyBones.LeftFoot) : null;
            var rf = drv != null ? drv.GetBoneTransform(HumanBodyBones.RightFoot) : null;
            if (lf == null || rf == null || _soldier.Movement == null) yield break;
            Vector3 pl = lf.position, pr = rf.position;
            var hl = new List<float>(); var hr = new List<float>();
            var sl = new List<float>(); var sr = new List<float>();
            float speed = 0f, loco = 0f;
            int n = 0;
            // B9: footstep events vs touchdowns of the VISIBLE (SWAT) feet, timed independently of SoldierMovement's driver-foot rule
            var vis = _proxy != null ? _proxy.Visual : null;
            var vlf = vis != null && vis.isHuman ? vis.GetBoneTransform(HumanBodyBones.LeftFoot) : null;
            var vrf = vis != null && vis.isHuman ? vis.GetBoneTransform(HumanBodyBones.RightFoot) : null;
            var ft = new List<float>(); var fvl = new List<float>(); var fvr = new List<float>();
            var steps = new List<float>();
            Action<float> onStep = _ => steps.Add(Time.time);
            _soldier.Movement.Footstep += onStep;
            float end = Time.time + seconds;
            yield return null;
            while (Time.time < end)
            {
                float dt = Time.deltaTime;
                if (dt > 1e-4f)
                {
                    Vector3 move = _soldier.Movement.Velocity;
                    move.y = 0f;
                    float vm = move.magnitude;
                    if (vm > 0.2f)
                    {
                        move /= vm;
                        float ground = _soldier.transform.position.y;
                        float leftSlide = Vector3.Dot((lf.position - pl) / dt, move);
                        float rightSlide = Vector3.Dot((rf.position - pr) / dt, move);
                        hl.Add(lf.position.y - ground); sl.Add(leftSlide);
                        hr.Add(rf.position.y - ground); sr.Add(rightSlide);
                        speed += vm;
                        loco += drv.GetFloat("LocoSpeed");
                        n++;
                        var cam = _soldier.ViewCamera;
                        _motionCsv.Append(
                            $"{Time.time:0.000},{Csv(label)},{_soldier.Movement.State},{_soldier.Movement.Stance}," +
                            $"{vm:0.000},{drv.GetFloat("LocoSpeed"):0.000},{lf.position.y - ground:0.000},{rf.position.y - ground:0.000}," +
                            $"{leftSlide:0.000},{rightSlide:0.000},{(_proxy != null ? _proxy.HandErrorLeft : -1f):0.000}," +
                            $"{(_proxy != null ? _proxy.HandErrorRight : -1f):0.000},{(_proxy != null ? _proxy.RawHipsDelta : -1f):0.000}," +
                            $"{(_proxy != null ? _proxy.RawHeadDelta.magnitude : -1f):0.000}," +
                            $"{(cam != null ? cam.transform.eulerAngles.y : 0f):0.000},{(cam != null ? cam.transform.eulerAngles.x : 0f):0.000}\n");
                    }
                }
                if (vlf != null && vrf != null)
                {
                    float g = _soldier.transform.position.y;
                    ft.Add(Time.time); fvl.Add(vlf.position.y - g); fvr.Add(vrf.position.y - g);
                }
                pl = lf.position;
                pr = rf.position;
                yield return null;
            }
            _soldier.Movement.Footstep -= onStep;
            if (ft.Count > 10) Note(FootSync(label, ft, fvl, fvr, steps));
            if (n == 0) { Note($"GAIT [{label}] not moving"); yield break; }
            speed /= n; loco /= n;
            // stance = foot within 1.5 cm of its lowest point in the window (planted, not swinging or airborne)
            float StanceSlide(List<float> h, List<float> s, out int count)
            {
                float min = float.MaxValue, sum = 0f;
                foreach (var v in h) min = Mathf.Min(min, v);
                count = 0;
                for (int i = 0; i < h.Count; i++)
                    if (h[i] < min + 0.015f) { sum += s[i]; count++; }
                return count > 0 ? sum / count : 0f;
            }
            float slideL = StanceSlide(hl, sl, out int cl), slideR = StanceSlide(hr, sr, out int cr);
            float slide = cl + cr > 0 ? (slideL * cl + slideR * cr) / (cl + cr) : 0f;
            float natural = loco > 0.01f ? (speed - slide) / loco : 0f;
            Note($"GAIT [{label}] move={speed:0.00} m/s LocoSpeed={loco:0.00} stanceFootSlide={slide:0.00} m/s (L {slideL:0.00} n{cl}, R {slideR:0.00} n{cr}) " +
                 $"-> clip natural speed ~{natural:0.00} m/s (state {_soldier.Movement.State}, {n} frames)");
        }

        /// <summary>Why does (or doesn't) the reload animation show? Clip, playables controller, layer weight inputs, curves.</summary>
        private void DiagnoseReload(string label)
        {
            var sb = new StringBuilder($"RELOAD-DIAG [{label}]");
            var fa = _soldier.ActiveFirearm;
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            if (fa != null)
            {
                var t = fa.GetType();
                object clip = t.GetField("reloadClip", F)?.GetValue(fa);
                object runtime = t.GetField("_reloadRuntime", F)?.GetValue(fa);
                object playables = t.GetField("_playables", F)?.GetValue(fa);
                var clipObj = clip as UnityEngine.Object;
                sb.Append($" gun={fa.name} reloading={fa.IsReloading} clip={(clipObj != null ? clipObj.name : "NULL")} runtimeCopy={(runtime as UnityEngine.Object != null)} playables={(playables != null ? playables.GetType().Name : "NULL")}");
                var animClip = clip?.GetType().GetField("clip", F)?.GetValue(clip) as AnimationClip;
                sb.Append($" animClip={(animClip != null ? animClip.name + " " + animClip.length.ToString("0.00") + "s" : "NULL")}");
            }
            var ui = _soldier.UserInput;
            if (ui != null)
            {
                int idx = ui.GetPropertyIndex(KINEMATION.FPSAnimationFramework.Runtime.Core.FPSANames.PlayablesWeight);
                sb.Append($" input.PlayablesWeight={(idx >= 0 ? ui.GetValue<float>(idx).ToString("0.00") : "missing")}");
            }
            if (_soldier.Animator != null) sb.Append($" animator.FullBodyWeight={_soldier.Animator.GetFloat("FullBodyWeight"):0.00}");
            var pc = _soldier.Playables;
            if (pc != null)
            {
                sb.Append($" controller={pc.GetType().Name} graphPlaying={pc.IsPlaying()}");
                foreach (var curve in new[] { "MaskAttachHand", "WeaponBoneWeight", "MaskLeftHand", "Overlay" })
                {
                    try { sb.Append($" curve.{curve}={pc.GetCurveValue(curve):0.00}"); } catch (Exception) { }
                }
                var mixField = pc.GetType().GetField("_masterMixer", F);
                if (mixField != null && mixField.GetValue(pc) is UnityEngine.Animations.AnimationMixerPlayable mix
                    && UnityEngine.Playables.PlayableExtensions.IsValid(mix) && UnityEngine.Playables.PlayableExtensions.GetInputCount(mix) > 1)
                    sb.Append($" masterMixer.input1Weight={UnityEngine.Playables.PlayableExtensions.GetInputWeight(mix, 1):0.00}");
                var cw = pc.GetType().GetField("_controllerWeight", F);
                if (cw != null) sb.Append($" controllerWeight={cw.GetValue(pc)}");
            }
            Note(sb.ToString());
            LogDriverWeaponBones(label);
        }

        /// <summary>Animated source bones (what the clip writes) vs procedural results (what the layers output), camera space.</summary>
        private void LogDriverWeaponBones(string label)
        {
            var drv = _proxy != null ? _proxy.Driver : null;
            var cam = _soldier.ViewCamera;
            if (drv == null || cam == null) return;
            var sb = new StringBuilder($"WEAPON-BONES [{label}] (camera space)");
            foreach (var n in new[] { "WeaponBone", "IK_Hand_Left", "IK_Hand_Right", "IK WeaponBone", "Left_Hand", "Right_Hand" })
            {
                var t = FindDeep(drv.transform, n);
                if (t == null) { sb.Append($" {n}=missing"); continue; }
                Vector3 lp = cam.transform.InverseTransformPoint(t.position);
                sb.Append($"\n      {n,-14} cam({lp.x,6:0.000},{lp.y,6:0.000},{lp.z,6:0.000}) local({t.localPosition.x:0.000},{t.localPosition.y:0.000},{t.localPosition.z:0.000}) localRot{t.localEulerAngles}");
            }
            Note(sb.ToString());
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform c in root)
            {
                if (c.name.StartsWith("SWAT_Visual") || !c.gameObject.activeSelf) continue; // hidden duplicates never move
                var r = FindDeep(c, name);
                if (r != null) return r;
            }
            return null;
        }

        private static IEnumerator Wait(float seconds)
        {
            float end = Time.time + seconds;
            while (Time.time < end) yield return null;
        }

        private IEnumerator Capture(string name, string metricsLabel, View view = View.FirstPerson, bool showHead = false)
        {
            if (metricsLabel != null) Metrics(metricsLabel);
            _pending.Enqueue(new Shot { name = name, view = view, showHead = showHead });
            while (_pending.Count > 0) yield return null; // LateUpdate renders it at the end of this frame
        }

        private void LateUpdate()
        {
            while (_pending.Count > 0) Render(_pending.Dequeue());
            // C16 froze for 3 min at game time 19.3 s with the editor still ticking: the in-game pause menu (Esc reaching
            // the visible test window) sets timeScale 0. The test owns the game: resume and say so.
            if (!_done && Time.timeScale == 0f)
            {
                var menu = FindAnyObjectByType<LR2.UI.GameMenuController>();
                if (menu != null && menu.IsPaused) menu.Resume();
                else Time.timeScale = 1f;
                Note("NOTE: the game was paused from outside the test (Esc in the visible window?); resumed");
            }
        }

        private void FreezeOthers()
        {
            foreach (var c in Combatant.All)
            {
                if (c == null) continue;
                var sc = c.GetComponent<SoldierController>();
                if (sc == null || sc == _soldier) continue;
                var bot = sc.GetComponent<BotController>();
                if (bot != null) bot.enabled = false;
                sc.ControlsEnabled = false;
                if (_enemy == null) _enemy = sc;
            }
            Note($"Froze other soldiers (enemy: {(_enemy != null ? _enemy.name : "none")})");
        }

        /// <summary>Yaw with the most open space at eye height, so third-person cameras don't land inside walls.</summary>
        private float OpenYaw()
        {
            float best = _soldier.transform.eulerAngles.y, bestDist = -1f;
            Vector3 eye = _soldier.transform.position + Vector3.up * 1.5f;
            for (int i = 0; i < 16; i++)
            {
                float yaw = i * 22.5f;
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                float d = Physics.Raycast(eye, dir, out var hit, 30f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) ? hit.distance : 30f;
                float back = Physics.Raycast(eye, -dir, out var hb, 6f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) ? hb.distance : 6f;
                float score = d + back * 0.5f;
                if (score > bestDist) { bestDist = score; best = yaw; }
            }
            return best;
        }

        private void Metrics(string label)
        {
            var cam = _soldier.ViewCamera;
            var sb = new StringBuilder($"METRICS [{label}]");
            if (cam != null)
                sb.Append($" camPos={cam.transform.position} camFwd={cam.transform.forward} vFov={cam.fieldOfView:0.0} aspect={cam.aspect:0.00} near={cam.nearClipPlane:0.000}");
            if (_proxy != null)
            {
                sb.Append($"\n    proxy: rawHipsDelta={_proxy.RawHipsDelta:0.000} rawHeadDelta={_proxy.RawHeadDelta} ({_proxy.RawHeadDelta.magnitude:0.000}) " +
                          $"shift={_proxy.AppliedShift} firstPerson={_proxy.FirstPersonActive} handErr L={_proxy.HandErrorLeft:0.000} R={_proxy.HandErrorRight:0.000}");
                var v = _proxy.Visual;
                if (cam != null && v != null)
                {
                    sb.Append("\n    visual bones in camera space (x right, y up, z fwd; in view = z>near and inside frustum):");
                    foreach (var b in new[] { HumanBodyBones.Head, HumanBodyBones.Neck, HumanBodyBones.UpperChest, HumanBodyBones.Chest,
                                 HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
                                 HumanBodyBones.LeftHand, HumanBodyBones.RightHand })
                    {
                        var t = v.GetBoneTransform(b);
                        if (t == null) continue;
                        Vector3 lp = cam.transform.InverseTransformPoint(t.position);
                        Vector3 vp = cam.WorldToViewportPoint(t.position);
                        bool inView = vp.z > cam.nearClipPlane && vp.x > 0f && vp.x < 1f && vp.y > 0f && vp.y < 1f;
                        sb.Append($"\n      {b,-14} ({lp.x,6:0.000},{lp.y,6:0.000},{lp.z,6:0.000}){(inView ? "  IN VIEW" : "")}");
                    }
                    Vector3 eyeLp = cam.transform.InverseTransformPoint(_proxy.VisualEyePosition);
                    sb.Append($"\n      visual EYE     ({eyeLp.x,6:0.000},{eyeLp.y,6:0.000},{eyeLp.z,6:0.000})");
                    var d = _proxy.Driver;
                    if (d != null)
                    {
                        sb.Append("\n    DRIVER bones in camera space:");
                        foreach (var b in new[] { HumanBodyBones.Head, HumanBodyBones.Neck, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.Hips })
                        {
                            var t = d.GetBoneTransform(b);
                            if (t == null) continue;
                            Vector3 lp = cam.transform.InverseTransformPoint(t.position);
                            sb.Append($"\n      {b,-14} ({lp.x,6:0.000},{lp.y,6:0.000},{lp.z,6:0.000})");
                        }
                        var vh = v.GetBoneTransform(HumanBodyBones.Hips);
                        var dh = d.GetBoneTransform(HumanBodyBones.Hips);
                        if (vh != null && dh != null)
                            sb.Append($"\n      hips height: driver {dh.position.y - _soldier.transform.position.y:0.000}  visual {vh.position.y - _soldier.transform.position.y:0.000}  (camera {cam.transform.position.y - _soldier.transform.position.y:0.000})");
                    }
                    foreach (var b in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
                    {
                        var t = v.GetBoneTransform(b);
                        if (t == null) continue;
                        float h = Physics.Raycast(t.position + Vector3.up * 0.5f, Vector3.down, out var hit, 3f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)
                            ? t.position.y - hit.point.y : -1f;
                        sb.Append($"\n      {b,-14} height above ground {h:0.000} m");
                    }
                }
            }
            var gun = _soldier.ActiveItem;
            if (gun != null && cam != null)
            {
                Vector3 gp = cam.transform.InverseTransformPoint(gun.transform.position);
                sb.Append($"\n    active item {gun.name} at camera-space ({gp.x:0.000},{gp.y:0.000},{gp.z:0.000}) aiming={_soldier.IsAiming} aimBlend={_soldier.AimBlend:0.00} reloading={_soldier.IsReloading} switching={_soldier.IsSwitchingWeapon}");
            }
            sb.Append($"\n    move state={_soldier.Movement?.State} speed={(_soldier.Movement != null ? _soldier.Movement.Speed : 0f):0.00} pitch={_soldier.Pitch:0.0}");
            Note(sb.ToString());
        }

        private HumanPoseHandler _fingerHandler;
        private HumanPose _fingerPose;

        /// <summary>B26: per finger, the humanoid muscle values and the measured bend (degrees) of the visible SWAT hands.</summary>
        private void LogFingers(string label)
        {
            var vis = _proxy != null ? _proxy.Visual : null;
            if (vis == null || !vis.isHuman || vis.avatar == null) { Note($"FINGERS [{label}] no visual humanoid"); return; }
            if (_fingerHandler == null) _fingerHandler = new HumanPoseHandler(vis.avatar, vis.transform);
            _fingerHandler.GetHumanPose(ref _fingerPose);
            string[] names = HumanTrait.MuscleName;
            float M(string n) { int i = System.Array.IndexOf(names, n); return i >= 0 && _fingerPose.muscles != null && i < _fingerPose.muscles.Length ? _fingerPose.muscles[i] : float.NaN; }
            float Bend(HumanBodyBones hand, HumanBodyBones a, HumanBodyBones b, HumanBodyBones c)
            {
                var h = vis.GetBoneTransform(hand); var p = vis.GetBoneTransform(a); var m = vis.GetBoneTransform(b); var d = vis.GetBoneTransform(c);
                if (h == null || p == null || m == null || d == null) return -1f;
                return Vector3.Angle(p.position - h.position, m.position - p.position) + Vector3.Angle(m.position - p.position, d.position - m.position);
            }
            var sb = new System.Text.StringBuilder($"FINGERS [{label}] (muscle 1/2/3, bend deg)");
            foreach (var side in new[] { "Left", "Right" })
            {
                bool L = side == "Left";
                var hand = L ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
                sb.Append("\n    " + side + ":");
                void F(string f, HumanBodyBones a, HumanBodyBones b, HumanBodyBones c)
                    => sb.Append($" {f} {M($"{side} {f} 1 Stretched"):0.00}/{M($"{side} {f} 2 Stretched"):0.00}/{M($"{side} {f} 3 Stretched"):0.00} bend={Bend(hand, a, b, c):0}");
                F("Thumb", L ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal, L ? HumanBodyBones.LeftThumbIntermediate : HumanBodyBones.RightThumbIntermediate, L ? HumanBodyBones.LeftThumbDistal : HumanBodyBones.RightThumbDistal);
                F("Index", L ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal, L ? HumanBodyBones.LeftIndexIntermediate : HumanBodyBones.RightIndexIntermediate, L ? HumanBodyBones.LeftIndexDistal : HumanBodyBones.RightIndexDistal);
                F("Middle", L ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal, L ? HumanBodyBones.LeftMiddleIntermediate : HumanBodyBones.RightMiddleIntermediate, L ? HumanBodyBones.LeftMiddleDistal : HumanBodyBones.RightMiddleDistal);
                F("Little", L ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal, L ? HumanBodyBones.LeftLittleIntermediate : HumanBodyBones.RightLittleIntermediate, L ? HumanBodyBones.LeftLittleDistal : HumanBodyBones.RightLittleDistal);
            }
            Note(sb.ToString());
        }

        private void Render(Shot shot)
        {
            var fp = _soldier != null ? _soldier.ViewCamera : null;
            if (fp == null) { Note($"skip {shot.name}: no view camera"); return; }
            if (shot.view == View.FirstPerson) { RenderCamera(fp, shot.name); return; }

            var t = _soldier.transform;
            Vector3 eye = fp.transform.position;
            Vector3 pos, look;
            switch (shot.view)
            {
                case View.Front: pos = t.position + t.forward * 2.3f + t.right * 0.55f + Vector3.up * 1.55f; look = t.position + Vector3.up * 1.3f; break;
                case View.Side: pos = t.position + t.right * 2.4f + t.forward * 0.4f + Vector3.up * 1.5f; look = t.position + t.forward * 0.25f + Vector3.up * 1.3f; break;
                case View.OverShoulder: pos = t.position - t.forward * 1.5f + t.right * 0.55f + Vector3.up * 2.0f; look = t.position + t.forward * 1.6f + Vector3.up * 1.35f; break;
                case View.HeadClose: pos = eye + t.right * 0.85f + t.forward * 0.25f + Vector3.up * 0.05f; look = eye - Vector3.up * 0.12f; break;
                case View.HandsLeft:
                case View.HandsRight:
                {
                    // close-up of the grip: midpoint of the visible hands, seen from the left or right side and slightly above
                    var vis = _proxy != null ? _proxy.Visual : null;
                    var hl = vis != null ? vis.GetBoneTransform(HumanBodyBones.LeftHand) : null;
                    var hr = vis != null ? vis.GetBoneTransform(HumanBodyBones.RightHand) : null;
                    Vector3 mid = hl != null && hr != null ? (hl.position + hr.position) * 0.5f : eye + t.forward * 0.35f - Vector3.up * 0.3f;
                    float side = shot.view == View.HandsLeft ? -1f : 1f;
                    pos = mid + t.right * (0.5f * side) + t.forward * 0.12f + Vector3.up * 0.14f;
                    look = mid;
                    break;
                }
                default:
                    if (_enemy == null) return;
                    var e = _enemy.transform;
                    pos = e.position + e.forward * 2.0f + e.right * 0.9f + Vector3.up * 1.6f; look = e.position + Vector3.up * 1.25f; break;
            }
            if (_marker == null)
            {
                _marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _marker.name = "PoseCheck_CameraMarker";
                Destroy(_marker.GetComponent<Collider>());
                _marker.transform.localScale = Vector3.one * 0.025f;
                var mat = new Material(Shader.Find("HDRP/Unlit")) { color = Color.red };
                _marker.GetComponent<Renderer>().sharedMaterial = mat;
            }
            _marker.SetActive(shot.view != View.Enemy);
            _marker.transform.position = eye;
            if (_eyeMarker == null)
            {
                _eyeMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _eyeMarker.name = "PoseCheck_EyeMarker";
                Destroy(_eyeMarker.GetComponent<Collider>());
                _eyeMarker.transform.localScale = Vector3.one * 0.018f;
                _eyeMarker.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("HDRP/Unlit")) { color = Color.green };
            }
            _eyeMarker.SetActive(shot.view != View.Enemy && _proxy != null);
            if (_proxy != null) _eyeMarker.transform.position = _proxy.VisualEyePosition;
            if (shot.showHead) _soldier.SetHeadHidden(false);

            var go = new GameObject("PoseCheckCam");
            try
            {
                go.transform.position = pos;
                go.transform.LookAt(look);
                var cam = go.AddComponent<Camera>();
                bool handsShot = shot.view == View.HandsLeft || shot.view == View.HandsRight;
                cam.fieldOfView = shot.view == View.HeadClose ? 40f : handsShot ? 38f : 52f;
                cam.nearClipPlane = 0.02f;
                go.AddComponent<HDAdditionalCameraData>();
                if (handsShot)
                {
                    // fill light for the grip close-ups only (the maps are dark); destroyed with the capture camera
                    var lightGo = new GameObject("PoseCheckFill");
                    lightGo.transform.SetParent(go.transform, false);
                    lightGo.transform.localPosition = new Vector3(0f, 0.25f, 0.05f);
                    var fill = lightGo.AddComponent<Light>();
                    fill.type = LightType.Point;
                    fill.range = 3f;
                    fill.color = new Color(1f, 0.95f, 0.88f);
                    fill.intensity = 1800f;
                    fill.shadows = LightShadows.None;
                    lightGo.AddComponent<HDAdditionalLightData>();
                }
                RenderCamera(cam, shot.name);
            }
            finally
            {
                Destroy(go);
                if (shot.showHead) _soldier.SetHeadHidden(true);
                _marker.SetActive(false);
                if (_eyeMarker != null) _eyeMarker.SetActive(false);
            }
        }

        private void RenderCamera(Camera cam, string name)
        {
            var rt = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                var request = new RenderPipeline.StandardRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(cam, request))
                    for (int p = 0; p < 3; p++) RenderPipeline.SubmitRenderRequest(cam, request);
                else
                {
                    var prev = cam.targetTexture;
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = prev;
                }
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(_outDir, name + ".png"), tex.EncodeToPNG());
                Destroy(tex);
                Note($"Captured {name}.png");
            }
            catch (Exception ex) { Note($"Capture {name} failed: {ex.Message}"); }
            finally { RenderTexture.ReleaseTemporary(rt); }
        }

        private void Finish()
        {
            if (_done) return;
            _done = true;
            var sb = new StringBuilder("=== LR2 POSE CHECK ===\n");
            sb.AppendLine($"Errors (game): {_errors}");
            sb.Append(_log);
            File.WriteAllText(Path.Combine(_outDir, "report.txt"), sb.ToString());
            File.WriteAllText(Path.Combine(_outDir, "motion_baseline.csv"), _motionCsv.ToString());
            Debug.Log($"[LR2] PoseCheck finished: {_outDir} (errors {_errors})");
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            Finish();
        }
    }
}
