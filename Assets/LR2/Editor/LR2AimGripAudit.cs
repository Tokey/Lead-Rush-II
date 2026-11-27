using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using KINEMATION.Shared.KAnimationCore.Runtime.Rig;
using LR2.AI;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using LR2.Game;
using LR2.Weapons;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LR2.EditorTools
{
    /// <summary>
    /// Measures ADS and grip on the frame the player actually sees. For every rifle and sight (and the pistol) it records
    /// where the sight line lands on screen at 25 m, where the bullet ray lands, how far each hand slides on the gun between
    /// hip fire and ADS (Kinemation robot hands and the visible mirrored hands), how far the visible fingers sit from the
    /// robot's gripping fingers, and where the thumbs are relative to the sight picture. Nothing is pushed into place before
    /// measuring. A second ADS pass runs with WeaponAntiClip switched off, so Kinemation's own alignment can be compared.
    /// Screenshots (ADS with screen-centre / bullet / sight marks, hip, and side close-ups of the hands) go next to the report.
    /// Batch:  -executeMethod LR2.EditorTools.LR2AimGripAudit.RunBatch [-lr2AimGripTag before]
    /// Editor: LR2AgentBridge command "aim-audit [tag] [force] [motion|strafe|adstime]"; the editor stays open and returns to the scene it had.
    /// "adstime" times ADS in and out per gun (standing and out of a sprint) and writes ads_trace.csv.
    /// </summary>
    [InitializeOnLoad]
    public static class LR2AimGripAudit
    {
        private const string KeyActive = "LR2AimGrip.Active", KeyDone = "LR2AimGrip.Done", KeyTag = "LR2AimGrip.Tag";
        private const string KeyExit = "LR2AimGrip.Exit", KeyCode = "LR2AimGrip.Code", KeyPrevScene = "LR2AimGrip.PrevScene";
        private const string KeyMotion = "LR2AimGrip.Motion", KeyStrafe = "LR2AimGrip.Strafe", KeyAdsTiming = "LR2AimGrip.AdsTiming";
        private const string Scene = "Assets/LR2/Scenes/LR2_TestRange.unity";
        private static bool _hosted;

        static LR2AimGripAudit()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            if (SessionState.GetBool(KeyActive, false)) EditorApplication.update += WaitForPlay;
        }

        public static void RunBatch()
        {
            string tag = "batch";
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-lr2AimGripTag") tag = args[i + 1];
            Begin(tag, true);
        }

        /// <summary>
        /// Started by LR2AgentBridge in the developer's open editor. Returns an error text, or null when started.
        /// <paramref name="discardSceneChanges"/> lets an agent proceed when its own rebuild marked the open scene dirty.
        /// </summary>
        public static string RunFromBridge(string tag, bool discardSceneChanges = false, bool motion = false, bool strafe = false, bool adsTiming = false) =>
            Begin(string.IsNullOrEmpty(tag) ? "editor" : tag, false, discardSceneChanges, motion, strafe, adsTiming);

        private static string Begin(string tag, bool exitWhenDone, bool discardSceneChanges = false, bool motion = false, bool strafe = false, bool adsTiming = false)
        {
            SessionState.SetBool(KeyMotion, motion);
            SessionState.SetBool(KeyStrafe, strafe);
            SessionState.SetBool(KeyAdsTiming, adsTiming);
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "already in play mode";
            for (int i = 0; i < SceneManager.sceneCount && !discardSceneChanges; i++)
                if (SceneManager.GetSceneAt(i).isDirty)
                    return $"scene '{SceneManager.GetSceneAt(i).name}' has unsaved changes; not switching scenes (add 'force' to discard)";

            SessionState.SetString(KeyPrevScene, SceneManager.GetActiveScene().path);
            SessionState.SetString(KeyTag, tag);
            SessionState.SetBool(KeyExit, exitWhenDone);
            SessionState.SetBool(KeyDone, false);
            SessionState.SetInt(KeyCode, 0);
            SessionState.SetBool(KeyActive, true);
            EditorSceneManager.playModeStartScene = null;
            EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
            EditorApplication.update -= WaitForPlay;
            EditorApplication.update += WaitForPlay;
            EditorApplication.isPlaying = true;
            return null;
        }

        private static void WaitForPlay()
        {
            if (_hosted || !EditorApplication.isPlaying) return;
            _hosted = true;
            EditorApplication.update -= WaitForPlay;
            // The project pauses play mode while Unity is not the focused window (runInBackground off). Keep this
            // audit running behind the developer's other windows; the editor default is restored when play mode ends.
            Application.runInBackground = true;
            var host = new GameObject("LR2AimGripAudit");
            UnityEngine.Object.DontDestroyOnLoad(host);
            var runner = host.AddComponent<Runner>();
            runner.Tag = SessionState.GetString(KeyTag, "run");
            runner.MotionOnly = SessionState.GetBool(KeyMotion, false);
            runner.StrafeOnly = SessionState.GetBool(KeyStrafe, false);
            runner.AdsTimingOnly = SessionState.GetBool(KeyAdsTiming, false);
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool(KeyActive, false)) return;
            SessionState.SetBool(KeyActive, false);
            Application.runInBackground = PlayerSettings.runInBackground;
            bool done = SessionState.GetBool(KeyDone, false);
            int code = SessionState.GetInt(KeyCode, 0);
            if (SessionState.GetBool(KeyExit, false))
            {
                EditorApplication.delayCall += () => EditorApplication.Exit(done ? code : 3);
                return;
            }
            string prev = SessionState.GetString(KeyPrevScene, "");
            if (!string.IsNullOrEmpty(prev) && File.Exists(prev)) EditorSceneManager.OpenScene(prev, OpenSceneMode.Single);
            LR2BuildPipeline.EnsureMainMenuPlayModeStart();
            File.AppendAllText(LR2AgentBridge.ResultPath,
                $"[{DateTime.Now:HH:mm:ss}] aim-audit {(done ? "finished" : "stopped early")} (Logs/LR2_AimGrip/{SessionState.GetString(KeyTag, "")})\n");
        }

        /// <summary>One measured frame.</summary>
        private struct Snap
        {
            public bool valid;
            public Vector3 eyeMm;            // active aim point in camera space (mm): where Kinemation put the eye
            public Vector2 sightPx;          // sight line at 25 m, pixels from screen centre (1080p scale, +x right, +y up)
            public Vector2 bulletPx;         // bullet ray at 25 m
            public float sightBulletPx;      // distance between the two
            public float bulletAngleDeg;     // bullet direction vs camera forward
            public float fovV;
            public Vector3 rHand, lHand;     // robot hands in gun space (m)
            public Quaternion rHandRot, lHandRot;
            public Vector3 vrHand, vlHand;   // visible (mirrored) hands in gun space (m)
            public Quaternion vrHandRot, vlHandRot;
            public float rIkGap, lIkGap;     // robot hand to its Kinemation IK target (m)
            public float fingerGapMax;       // largest visible-vs-robot finger joint distance (m)
            public string fingerGapBone;
            public float thumbLPx, thumbRPx; // visible thumb tip distance from screen centre (px)
            public bool thumbLFront, thumbRFront; // thumb tip nearer to the camera than the aim point + 0.5 m
            public bool lockApplied;
            public float lockWeight;
            public string segmentAngles;     // visible vs robot direction (deg) per left thumb / index segment
        }

        // After CameraShake (10000). CameraShake adds its offset only while the view camera renders, so samples here are
        // the player's frame without shot shake (none of these tests apply trauma).
        [DefaultExecutionOrder(20000)]
        private sealed class Runner : MonoBehaviour
        {
            public string Tag;
            public bool MotionOnly, StrafeOnly, AdsTimingOnly;
            private const float RangeM = 25f;
            private static readonly string[] Prefabs =
            {
                "Assets/LR2/Prefabs/Weapons/LR2_Mk18.prefab",
                "Assets/LR2/Prefabs/Weapons/LR2_AK12.prefab",
                "Assets/LR2/Prefabs/Weapons/LR2_AK74N.prefab",
                "Assets/LR2/Prefabs/Weapons/LR2_FAL.prefab",
                "Assets/LR2/Prefabs/Weapons/LR2_Mk23.prefab",
            };
            private static readonly HumanBodyBones[] FingerJoints =
            {
                HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal, HumanBodyBones.LeftIndexDistal,
                HumanBodyBones.LeftMiddleDistal, HumanBodyBones.LeftRingDistal, HumanBodyBones.LeftLittleDistal,
                HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal, HumanBodyBones.RightIndexDistal,
                HumanBodyBones.RightMiddleDistal, HumanBodyBones.RightRingDistal, HumanBodyBones.RightLittleDistal,
            };

            private readonly StringBuilder _report = new StringBuilder();
            private readonly StringBuilder _csv = new StringBuilder();
            private string _outDir;
            private SoldierInput _cmd;
            private Action _atLateUpdate;
            private int _errors, _shots;
            private SoldierController _player;
            private Animator _anim;
            private GenericBoneMirror _mirror;
            private Transform _ikR, _ikL;

            private sealed class Driver : ISoldierInputSource
            {
                public Runner owner;
                public void GetInput(SoldierController s, ref SoldierInput input) => input = owner._cmd;
            }

            private sealed class Idle : ISoldierInputSource
            {
                public void GetInput(SoldierController s, ref SoldierInput input) => input.ClearAll();
            }

            private void OnLog(string msg, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    _errors++;
                    if (_errors <= 20) _report.AppendLine($"  [log {type}] {msg}");
                }
            }

            private void LateUpdate()
            {
                var action = _atLateUpdate;
                _atLateUpdate = null;
                action?.Invoke();
                if (_sampling) SampleStability();
                if (_strafeSampling) SampleStrafe();
                if (_adsSampling) SampleAds();
            }

            /// <summary>Runs <paramref name="action"/> at the end of this frame's LateUpdate chain and waits for it.</summary>
            private IEnumerator AtFrameEnd(Action action)
            {
                _atLateUpdate = action;
                while (_atLateUpdate != null) yield return null;
            }

            private IEnumerator Wait(float seconds)
            {
                float end = Time.time + seconds;
                while (Time.time < end) yield return null;
            }

            private void Line(string s)
            {
                _report.AppendLine(s);
                Debug.Log("[LR2AimGrip] " + s);
            }

            private IEnumerator Start()
            {
                _outDir = Path.Combine("Logs", "LR2_AimGrip", Tag);
                Directory.CreateDirectory(_outDir);
                Application.logMessageReceived += OnLog;
                Line($"=== LR2 aim + grip audit '{Tag}' {DateTime.Now:yyyy-MM-dd HH:mm} ===");
                _csv.AppendLine("gun,sight,pass,eye_x_mm,eye_y_mm,eye_z_mm,sight_px_x,sight_px_y,bullet_px_x,bullet_px_y,sight_bullet_px,bullet_deg," +
                                "slip_r_mm,slip_l_mm,slip_r_deg,slip_l_deg,vis_slip_r_mm,vis_slip_l_mm,ik_gap_r_mm,ik_gap_l_mm,finger_gap_mm,finger_gap_bone," +
                                "thumb_l_px,thumb_r_px,thumb_l_front,thumb_r_front,lock,lock_w,fov_v");

                float until = Time.realtimeSinceStartup + 25f;
                while (_player == null && Time.realtimeSinceStartup < until)
                {
                    var lp = Combatant.FindLocalPlayer();
                    if (lp != null) _player = lp.GetComponent<SoldierController>();
                    yield return null;
                }
                if (_player == null)
                {
                    Line("FAIL: no local player");
                    Finish(1);
                    yield break;
                }
                while (MatchManager.Instance != null && MatchManager.Instance.State == MatchState.Countdown && Time.realtimeSinceStartup < until + 20f)
                    yield return null;

                foreach (var bot in FindObjectsByType<BotController>())
                {
                    var s = bot.GetComponent<SoldierController>();
                    bot.enabled = false;
                    if (s != null) s.InputSource = new Idle();
                }

                _anim = _player.GetComponent<Animator>();
                _mirror = _player.GetComponent<GenericBoneMirror>();
                var rig = _player.GetComponentInChildren<KRigComponent>();
                if (rig != null)
                {
                    _ikR = rig.GetRigTransform("IK RightHand");
                    _ikL = rig.GetRigTransform("IK LeftHand");
                }
                bool visualOn = _mirror != null && _mirror.VisualRoot != null && _mirror.VisualRoot.activeInHierarchy;
                Line($"player={_player.name} mirror={(visualOn ? "visible" : "off")} ikTargets={(_ikR != null && _ikL != null)} cam={DescribeCamera()}");

                _player.InputSource = new Driver { owner = this };
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = _player.Yaw, aimPitch = 0f };
                yield return Wait(0.3f);

                var itemsField = typeof(SoldierController).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic);
                var equip = typeof(SoldierController).GetMethod("EquipImmediate", BindingFlags.Instance | BindingFlags.NonPublic);
                var items = itemsField != null ? itemsField.GetValue(_player) as List<LR2Item> : null;
                if (items == null || equip == null || _player.ActiveItem == null)
                {
                    Line("FAIL: cannot reach the soldier's item list");
                    Finish(1);
                    yield break;
                }
                Transform weaponBone = _player.ActiveItem.transform.parent;
                Line($"weapon parent bone = {weaponBone.name}");

                if (StrafeOnly)
                {
                    yield return StrafeFireCheck();
                    Line($"Runtime errors during audit: {_errors}");
                    Finish(0);
                    yield break;
                }

                if (AdsTimingOnly)
                {
                    yield return AdsTimingCheck(items, equip, weaponBone);
                    Line($"ADS reversal jumps: {_motionFailures}");
                    Line($"Runtime errors during audit: {_errors}");
                    Finish(_motionFailures > 0 ? 1 : 0);
                    yield break;
                }

                if (MotionOnly)
                {
                    yield return MotionCheck();
                    Line($"Runtime errors during audit: {_errors}");
                    Finish(_motionFailures > 0 ? 1 : 0);
                    yield break;
                }

                foreach (string path in Prefabs)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) { Line($"missing {path}"); continue; }
                    var go = Instantiate(prefab, weaponBone, false);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.SetActive(false);
                    var gun = go.GetComponent<LR2Firearm>();
                    if (gun == null) { Destroy(go); continue; }
                    items.Insert(0, gun);
                    equip.Invoke(_player, new object[] { 0 });
                    yield return Wait(0.9f);
                    yield return AuditGun(gun, Path.GetFileNameWithoutExtension(path).Replace("LR2_", ""));
                    items.RemoveAt(0);
                    Destroy(go);
                    equip.Invoke(_player, new object[] { 0 });
                    yield return Wait(0.3f);
                }

                yield return DriftCheck();

                Line($"Runtime errors during audit: {_errors}");
                Finish(0);
            }

            // ------------------------------------------------------------------ motion / stance stability
            private sealed class PhaseStats
            {
                public string name;
                public int frames, nan;
                public float muzzleAim, ikGapR, ikGapL, wristErr, gunRotStep, gunPosStep, camRollStep;
                public float sightDepthMin = float.MaxValue; // mm in front of the camera while aiming (< 0: the sight came behind the eye)
                public float stepAt, aimAt;                  // seconds into the phase of the biggest gun step / gun-vs-aim angle
                public string stepState, aimState;
                public int popDetails;
            }

            private float _phaseStart, _respawnedAt = -100f;
            private int _eyeDetails;
            private Transform _lastAimPoint;
            private float _aimPointChangedAt;

            private string SoldierState() =>
                $"aim={_player.AimBlend:0.00} sprint={_player.IsSprinting} prone={_player.IsProning} reload={(_player.ActiveFirearm != null && _player.ActiveFirearm.IsReloading)} " +
                $"switch={_player.IsSwitchingWeapon} fbw={_player.FullBodyWeightEffective:0.00}";

            private readonly List<PhaseStats> _phases = new List<PhaseStats>();
            private PhaseStats _current;
            private bool _sampling, _haveLast;
            private Vector3 _lastGunPos;
            private Quaternion _lastGunRot;
            private float _lastCamRoll, _yaw;
            private int _motionFailures;

            // Phases where the gun legitimately leaves the aim line or the hands leave it (sprint carry, reload, prone, slide).
            private static bool GunMayLeaveAim(string phase) =>
                phase.StartsWith("sprint") || phase.StartsWith("reload") || phase.StartsWith("prone") || phase.StartsWith("slide");

            private void SampleStability()
            {
                if (_current == null || _player == null || _player.IsDead) { _haveLast = false; return; }
                var item = _player.ActiveItem;
                var cam = _player.ViewCamera != null ? _player.ViewCamera.transform : null;
                if (item == null || cam == null) return;
                var s = _current;
                s.frames++;
                Vector3 gunPos = cam.InverseTransformPoint(item.transform.position);
                Quaternion gunRot = Quaternion.Inverse(cam.rotation) * item.transform.rotation;
                float camRoll = cam.eulerAngles.z;
                if (float.IsNaN(gunPos.x) || float.IsNaN(gunRot.x) || float.IsNaN(camRoll)) { s.nan++; return; }
                float muzzleAim = Vector3.Angle(item.transform.forward, _player.AimDirection);
                if (muzzleAim > s.muzzleAim) { s.muzzleAim = muzzleAim; s.aimAt = Time.time - _phaseStart; s.aimState = SoldierState(); }
                var ap = _player.ActiveFirearm != null ? _player.ActiveFirearm.ActiveAimPointTransform : null;
                if (ap != _lastAimPoint) { _lastAimPoint = ap; _aimPointChangedAt = Time.time; }
                if (ap != null && _player.AimBlend > 0.5f && Time.time - _aimPointChangedAt > 0.6f)
                {
                    float depth = cam.InverseTransformPoint(ap.position).z * 1000f;
                    s.sightDepthMin = Mathf.Min(s.sightDepthMin, depth);
                    if (depth < -20f && _eyeDetails < 6)
                    {
                        _eyeDetails++;
                        var gun = _player.ActiveFirearm;
                        Line($"      INTO-EYE {s.name} +{Time.time - _phaseStart:0.00}s depth={depth:0.0}mm aimPoint={ap.name} (parent {ap.parent.name}) " +
                             $"apLocalToGun={item.transform.InverseTransformPoint(ap.position).ToString("F3")} ammo={(gun != null ? gun.AmmoInMagazine : -1)} " +
                             $"camLocal={cam.localPosition.ToString("F3")} | {SoldierState()}");
                    }
                }
                var rh = LR2Bones.Get(_anim, HumanBodyBones.RightHand);
                var lh = LR2Bones.Get(_anim, HumanBodyBones.LeftHand);
                if (rh != null && _ikR != null) s.ikGapR = Mathf.Max(s.ikGapR, Vector3.Distance(rh.position, _ikR.position) * 1000f);
                if (lh != null && _ikL != null) s.ikGapL = Mathf.Max(s.ikGapL, Vector3.Distance(lh.position, _ikL.position) * 1000f);
                if (_mirror != null) s.wristErr = Mathf.Max(s.wristErr, Mathf.Max(_mirror.HandErrorLeft, _mirror.HandErrorRight) * 1000f);
                if (_haveLast)
                {
                    float rotStep = Quaternion.Angle(_lastGunRot, gunRot);
                    if (rotStep > s.gunRotStep) { s.stepAt = Time.time - _phaseStart; s.stepState = SoldierState(); }
                    if (rotStep > 12f && s.popDetails < 3)
                    {
                        s.popDetails++;
                        var clips = _anim != null ? _anim.GetCurrentAnimatorClipInfo(0) : null;
                        var st = _anim != null ? _anim.GetCurrentAnimatorStateInfo(0) : default;
                        var ikWeapon = item.transform.parent;
                        Line($"      POP {s.name} +{Time.time - _phaseStart:0.00}s ({Time.time - _respawnedAt:0.00}s after respawn): step {rotStep:0.0}deg " +
                             $"{(gunPos - _lastGunPos).magnitude * 100f:0.0}cm | clip={(clips != null && clips.Length > 0 ? clips[0].clip.name : "-")} t={st.normalizedTime:0.00} " +
                             $"inTransition={(_anim != null && _anim.IsInTransition(0))} | ikWeaponLocal={(ikWeapon != null ? ikWeapon.localPosition.ToString("F3") : "-")} " +
                             $"itemLocal={item.transform.localPosition.ToString("F3")} | {SoldierState()}");
                    }
                    s.gunRotStep = Mathf.Max(s.gunRotStep, rotStep);
                    s.gunPosStep = Mathf.Max(s.gunPosStep, (gunPos - _lastGunPos).magnitude * 100f);
                    s.camRollStep = Mathf.Max(s.camRollStep, Mathf.Abs(Mathf.DeltaAngle(_lastCamRoll, camRoll)));
                }
                _lastGunPos = gunPos;
                _lastGunRot = gunRot;
                _lastCamRoll = camRoll;
                _haveLast = true;
            }

            private SoldierInput Cmd(Vector2 move = default, bool aim = false, bool sprint = false, bool fire = false, float lean = 0f)
            {
                var c = SoldierInput.Empty;
                c.absoluteLook = true;
                c.aimYaw = _yaw;
                c.aimPitch = 0f;
                c.move = move;
                c.aim = aim;
                c.sprint = sprint;
                c.fire = fire;
                c.lean = lean;
                return c;
            }

            /// <summary>Holds <paramref name="cmd"/> for <paramref name="seconds"/> while every frame is measured under <paramref name="name"/>.</summary>
            private IEnumerator Phase(string name, float seconds, SoldierInput cmd, float yawSweep = 0f)
            {
                _current = new PhaseStats { name = name };
                _phases.Add(_current);
                _haveLast = false;
                _cmd = cmd;
                _phaseStart = Time.time;
                float start = Time.time, yaw0 = cmd.aimYaw;
                while (Time.time - start < seconds)
                {
                    if (yawSweep != 0f) _cmd.aimYaw = yaw0 + yawSweep * Mathf.Clamp01((Time.time - start) / seconds);
                    yield return null;
                }
                if (yawSweep != 0f) _yaw = yaw0 + yawSweep;
            }

            /// <summary>A one-frame key press (crouch, prone, jump, slide, reload are toggles or one-shots).</summary>
            private IEnumerator Press(Func<SoldierInput, SoldierInput> press)
            {
                var held = _cmd;
                _cmd = press(held);
                yield return null;
                _cmd = held;
            }

            private IEnumerator Snapshot(string shotName)
            {
                yield return AtFrameEnd(() =>
                {
                    Capture(_player.ViewCamera, "motion_" + shotName + "_fp", null);
                    CaptureBody("motion_" + shotName + "_side");
                });
            }

            /// <summary>Third-person view of the whole soldier from its left, for stance and death poses.</summary>
            private void CaptureBody(string shotName)
            {
                var go = new GameObject("LR2AimGrip_BodyCam");
                var cam = go.AddComponent<Camera>();
                cam.fieldOfView = 50f;
                cam.nearClipPlane = 0.05f;
                cam.enabled = false;
                Vector3 centre = _player.transform.position + Vector3.up * 0.8f;
                go.transform.position = centre - _player.transform.right * 3.2f + Vector3.up * 0.6f;
                go.transform.rotation = Quaternion.LookRotation(centre - go.transform.position, Vector3.up);
                var tex = Render(cam, 960, 540);
                if (tex != null)
                {
                    File.WriteAllBytes(Path.Combine(_outDir, shotName + ".png"), tex.EncodeToPNG());
                    Destroy(tex);
                    _shots++;
                }
                Destroy(go);
            }

            private void HipPose(out Vector3 pos, out Quaternion rot)
            {
                var cam = _player.ViewCamera.transform;
                var item = _player.ActiveItem.transform;
                pos = cam.InverseTransformPoint(item.position);
                rot = Quaternion.Inverse(cam.rotation) * item.rotation;
            }

            private IEnumerator DieAndRespawn(string name, Vector3 basePos, Quaternion baseRot)
            {
                _current = null;
                var fatal = new DamageInfo { amount = 5000f, type = DamageType.Suicide, point = _player.transform.position };
                _player.Health.ApplyDamage(ref fatal);
                yield return Wait(0.6f);
                yield return AtFrameEnd(() => CaptureBody($"motion_{name}_dead_side"));
                // Let the match respawn the player as it would in play (respawn delay + killcam). Respawning by hand here
                // made the match's own scheduled respawn fire again ~1.7 s later and reset the Animator a second time.
                float giveUp = Time.time + 12f;
                while (_player.IsDead && Time.time < giveUp) yield return null;
                if (_player.IsDead) _player.Respawn(_player.transform.position, _yaw);
                _respawnedAt = Time.time;
                _yaw = _player.Yaw;
                yield return Phase($"respawn_after_{name}", 1.4f, Cmd());
                Vector3 pos = default;
                Quaternion rot = default;
                yield return AtFrameEnd(() =>
                {
                    HipPose(out pos, out rot);
                    Capture(_player.ViewCamera, $"motion_respawn_after_{name}_fp", null);
                });
                float dPos = (pos - basePos).magnitude * 100f, dRot = Quaternion.Angle(rot, baseRot);
                bool leak = dPos > 2f || dRot > 3f;
                if (leak) _motionFailures++;
                Line($"  respawn after {name}: hip gun pose vs first life {dPos:0.00} cm / {dRot:0.00} deg {(leak ? "LEAK" : "ok")} " +
                     $"(aimBlend {_player.AimBlend:0.00}, sprinting {_player.IsSprinting}, prone {_player.IsProning})");
            }

            // ------------------------------------------------------------------ strafing and tracers (11-26)
            private bool _strafeSampling;
            private float _tracerLagMax, _originLagMax, _sightOffMaxPx, _bulletVsSightMaxPx;
            private int _tracerSamples, _lastAmmoSeen = -1;
            private float _lastShotSeen = -10f;

            /// <summary>
            /// Fires while strafing. Per frame: how far each live tracer's tail sits off the line from the gun's current muzzle
            /// to the tracer's head (a trailing streak), and how far the shot origin is from where the camera is drawn.
            /// </summary>
            private IEnumerator StrafeFireCheck()
            {
                Line("--- strafe fire (tracer tail off the current muzzle line; shot origin vs rendered camera) ---");
                _yaw = MostOpenYaw() + 90f; // strafing then runs along the open direction
                var passes = new[] { ("hip_left", false, -1f, true), ("hip_right", false, 1f, true), ("ads_left", true, -1f, true), ("ads_right", true, 1f, true),
                                     ("ads_left_nofire", true, -1f, false), ("ads_right_nofire", true, 1f, false) };
                foreach (var (label, aim, side, fire) in passes)
                {
                    if (_player.ActiveFirearm != null) _player.ActiveFirearm.RefillAmmo();
                    yield return Phase("settle_" + label, 0.5f, Cmd(aim: aim));
                    _tracerLagMax = _originLagMax = _sightOffMaxPx = _bulletVsSightMaxPx = 0f;
                    _tracerSamples = 0;
                    _strafeSampling = true;
                    yield return Phase("strafe_fire_" + label, 1.4f, Cmd(new Vector2(side, 0f), aim: aim, fire: fire));
                    _strafeSampling = false;
                    if (label == "ads_left") yield return Snapshot("strafe_fire_ads");
                    Line($"  {label,-10} speed={_player.Movement.Velocity.magnitude:0.0} m/s  tracer tail off muzzle line: max {_tracerLagMax:0.0} cm over {_tracerSamples} samples  " +
                         $"shot origin vs rendered camera: max {_originLagMax:0.0} cm  sight off screen centre (ADS): max {_sightOffMaxPx:0.0} px  bullet vs sight between shots (ADS): max {_bulletVsSightMaxPx:0.0} px");
                }
            }

            private void SampleStrafe()
            {
                var gun = _player.ActiveFirearm;
                var cam = _player.ViewCamera;
                if (gun == null || cam == null) return;
                Vector3 muzzle = gun.Muzzle.position;
                _originLagMax = Mathf.Max(_originLagMax, Vector3.Distance(_player.AimOrigin, cam.transform.position) * 100f);
                if (gun.AmmoInMagazine != _lastAmmoSeen) { _lastAmmoSeen = gun.AmmoInMagazine; _lastShotSeen = Time.time; }
                var ap = gun.ActiveAimPointTransform;
                if (ap != null && _player.AimBlend > 0.95f)
                {
                    Vector2 sightPx = ToPx(cam, ap.position + ap.forward * RangeM);
                    _sightOffMaxPx = Mathf.Max(_sightOffMaxPx, sightPx.magnitude); // reticle vs screen centre
                    // reticle vs where the bullet goes; the recoil kick is in the reticle but deliberately not in the bullet,
                    // so this only counts frames without a fresh shot
                    if (Time.time - _lastShotSeen > 0.12f)
                        _bulletVsSightMaxPx = Mathf.Max(_bulletVsSightMaxPx, (ToPx(cam, _player.AimOrigin + _player.AimDirection * RangeM) - sightPx).magnitude);
                }
                foreach (var t in FindObjectsByType<LR2.FX.Tracer>())
                {
                    var lr = t.GetComponent<LineRenderer>();
                    if (lr == null || !lr.enabled || lr.positionCount < 2) continue;
                    Vector3 head = lr.GetPosition(0), tail = lr.GetPosition(1);
                    Vector3 axis = head - muzzle;
                    if (axis.sqrMagnitude < 0.01f) continue;
                    Vector3 rel = tail - muzzle;
                    float off = (rel - Vector3.Project(rel, axis.normalized)).magnitude * 100f;
                    _tracerLagMax = Mathf.Max(_tracerLagMax, off);
                    _tracerSamples++;
                }
            }

            /// <summary>The yaw with the most free space ahead and behind (chest-height raycasts in 16 directions).</summary>
            private float MostOpenYaw()
            {
                Vector3 chest = _player.transform.position + Vector3.up * 1.2f;
                float best = _player.Yaw, bestRoom = -1f;
                for (int i = 0; i < 16; i++)
                {
                    float yaw = i * 22.5f;
                    Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                    float ahead = Physics.Raycast(chest, fwd, out var h1, 40f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) ? h1.distance : 40f;
                    float behind = Physics.Raycast(chest, -fwd, out var h2, 40f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) ? h2.distance : 40f;
                    float room = Mathf.Min(ahead, 25f) + Mathf.Min(behind, 12f) * 0.5f;
                    if (room > bestRoom) { bestRoom = room; best = yaw; }
                }
                return best;
            }

            // ------------------------------------------------------------------ ADS timing (11-26)
            private struct AdsSample
            {
                public float t;           // s since the press (or release) was issued
                public bool aiming, canFire;
                public Vector2 farPx;     // sight line at 25 m, px from screen centre (the angle of the sight)
                public Vector2 nearPx;    // sight line 0.25 m past the eye point (lateral offset of the sight picture)
                public float fov, blend, kinW;
                public Vector3 gunPos;    // item in camera space (m)
                public Quaternion gunRot;
                public Vector3 camRoot, gunRoot; // camera and item in the soldier's root space (m): which of the two is moving
            }

            private readonly List<AdsSample> _adsSamples = new List<AdsSample>();
            private readonly StringBuilder _adsTrace = new StringBuilder();
            private bool _adsSampling;
            private float _adsT0;
            private float _lastTrialYaw = float.NaN;

            /// <summary>
            /// How long ADS really takes, per gun: from the frame the aim button is pressed until the sight is on target
            /// (first within 10 px of where it comes to rest) and until it stays there, from standing and out of a sprint in
            /// both directions, plus the zoom, the way back to the hip pose, and a trial that reverses ADS half way in both
            /// directions to check the gun never jumps. 1080p pixels. The resting point drifts slowly with the idle head motion
            /// (a few px), so thresholds tighter than 10 px measure that drift, not the ADS.
            /// </summary>
            private IEnumerator AdsTimingCheck(List<LR2Item> items, MethodInfo equip, Transform weaponBone)
            {
                Line("--- ADS timing (ms from the press; onTarget = sight first within 10 px of its resting point, stays10 = within 10 px from then on; rebound = rise after onTarget; maxFrame = longest frame) ---");
                _yaw = MostOpenYaw();
                // phase "in": a/b = sight line at 25 m / at 0.25 m, px from its resting point; phase "out": a/b = gun offset from its hip pose, mm / deg
                _adsTrace.AppendLine("gun,sight,trial,phase,t_ms,a,b,fov,blend,kin_w,aiming,can_fire,cam_x_mm,cam_y_mm,cam_z_mm,gun_x_mm,gun_y_mm,gun_z_mm");
                int trial = 0;
                foreach (string path in Prefabs)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) { Line($"missing {path}"); continue; }
                    var go = Instantiate(prefab, weaponBone, false);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.SetActive(false);
                    var gun = go.GetComponent<LR2Firearm>();
                    if (gun == null) { Destroy(go); continue; }
                    items.Insert(0, gun);
                    equip.Invoke(_player, new object[] { 0 });
                    yield return Wait(0.9f);
                    string gunName = Path.GetFileNameWithoutExtension(path).Replace("LR2_", "");
                    Line($"  {gunName}: adsTime {gun.EffectiveAdsTime * 1000f:0} ms");
                    int[] sights = gun.HasUnifiedScopes ? new[] { 0, 2 } : new[] { -1 };
                    foreach (int sc in sights)
                    {
                        if (sc >= 0) gun.ApplyScopeChoice(sc);
                        string sight = sc >= 0 ? LR2Session.ScopeNames[sc].Split(' ')[0] : "IRON";
                        yield return AdsTrial(gunName, sight, false, trial++);
                    }
                    if (gun.HasUnifiedScopes) gun.ApplyScopeChoice(0);
                    // out of a sprint both ways (forward, then back towards the start), so direction is not tied to the gun
                    yield return AdsTrial(gunName, "IRON", true, trial++, false);
                    yield return AdsTrial(gunName, "IRON", true, trial++, true);
                    yield return AdsReversalTrial(gunName);
                    items.RemoveAt(0);
                    Destroy(go);
                    equip.Invoke(_player, new object[] { 0 });
                    yield return Wait(0.3f);
                }
                File.WriteAllText(Path.Combine(_outDir, "ads_trace.csv"), _adsTrace.ToString());
            }

            private IEnumerator AdsTrial(string gunName, string sight, bool fromSprint, int trial, bool reverse = false)
            {
                float yaw = _yaw + (reverse ? 180f : 0f);
                SoldierInput Still(bool aim) { var c = Cmd(aim: aim); c.aimYaw = yaw; return c; }
                _cmd = Still(false);
                // After a turn, Kinemation's turn layer swings the body round for about a second and the camera slides
                // ~6 cm with it; a trial started inside that measured the turn, not the ADS. Let it finish first.
                bool turned = float.IsNaN(_lastTrialYaw) || Mathf.Abs(Mathf.DeltaAngle(_lastTrialYaw, yaw)) > 1f;
                _lastTrialYaw = yaw;
                yield return Wait(turned ? 2.2f : 0.8f);
                Vector3 hipPos = default;
                Quaternion hipRot = default;
                float hipFov = 0f;
                yield return AtFrameEnd(() => { HipPose(out hipPos, out hipRot); hipFov = _player.ViewCamera.fieldOfView; });
                if (fromSprint)
                {
                    var run = Cmd(Vector2.up, sprint: true);
                    run.aimYaw = yaw;
                    _cmd = run;
                    float giveUp = Time.time + 2f;
                    while (!_player.IsSprinting && Time.time < giveUp) yield return null;
                    yield return Wait(0.7f);
                    if (!_player.IsSprinting) Line($"  {gunName} sprint trial: the soldier never sprinted");
                }

                string trialName = fromSprint ? (reverse ? "sprintR" : "sprint") : "stand";
                // ADS in: hold the button 1.0 s (the last 0.25 s is the resting point)
                _adsSamples.Clear();
                _cmd = Still(true);
                _adsT0 = Time.time;
                _adsSampling = true;
                yield return Wait(1.0f);
                _adsSampling = false;
                var inSamples = new List<AdsSample>(_adsSamples);

                // ADS out
                _adsSamples.Clear();
                _cmd = Still(false);
                _adsT0 = Time.time;
                _adsSampling = true;
                yield return Wait(0.8f);
                _adsSampling = false;
                var outSamples = new List<AdsSample>(_adsSamples);

                ReportAdsTrial(gunName, sight, trialName, inSamples, outSamples, hipPos, hipRot, hipFov);
            }

            private float _plainPeakMm, _plainPeakDeg;

            /// <summary>Fastest the gun moves relative to the camera between two samples (mm/s and deg/s).</summary>
            private static void AdsPeakSpeed(List<AdsSample> samples, out float mmPerS, out float degPerS)
            {
                mmPerS = degPerS = 0f;
                for (int i = 1; i < samples.Count; i++)
                {
                    float dt = samples[i].t - samples[i - 1].t;
                    if (dt <= 0.0001f) continue;
                    mmPerS = Mathf.Max(mmPerS, (samples[i].gunPos - samples[i - 1].gunPos).magnitude * 1000f / dt);
                    degPerS = Mathf.Max(degPerS, Quaternion.Angle(samples[i].gunRot, samples[i - 1].gunRot) / dt);
                }
            }

            /// <summary>
            /// Presses and releases ADS half way through each transition (in, then out) and compares the gun's top speed with
            /// a plain hip-to-ADS move. LR2Firearm.SetAdsCurve rebuilds Kinemation's ADS curve through the current point on
            /// every press and release; a jump there would show as a speed spike far above the plain move.
            /// </summary>
            private IEnumerator AdsReversalTrial(string gunName)
            {
                float yaw = _yaw;
                SoldierInput Still(bool aim) { var c = Cmd(aim: aim); c.aimYaw = yaw; return c; }
                _cmd = Still(false);
                bool turned = float.IsNaN(_lastTrialYaw) || Mathf.Abs(Mathf.DeltaAngle(_lastTrialYaw, yaw)) > 1f;
                _lastTrialYaw = yaw;
                yield return Wait(turned ? 2.2f : 0.8f);
                _adsSamples.Clear();
                _adsT0 = Time.time;
                _adsSampling = true;
                float limit;
                _cmd = Still(true);                                    // in ...
                limit = Time.time + 1f;
                while (_player.AimBlend < 0.5f && Time.time < limit) yield return null;
                _cmd = Still(false);                                   // ... let go half way
                limit = Time.time + 1f;
                while (_player.AimBlend > 0.25f && Time.time < limit) yield return null;
                _cmd = Still(true);                                    // ... and in again
                limit = Time.time + 1f;
                while (_player.AimBlend < 0.999f && Time.time < limit) yield return null;
                yield return Wait(0.3f);
                _cmd = Still(false);                                   // out ...
                limit = Time.time + 1f;
                while (_player.AimBlend > 0.5f && Time.time < limit) yield return null;
                _cmd = Still(true);                                    // ... back in half way
                limit = Time.time + 1f;
                while (_player.AimBlend < 0.999f && Time.time < limit) yield return null;
                yield return Wait(0.3f);
                _cmd = Still(false);
                yield return Wait(0.5f);
                _adsSampling = false;
                AdsPeakSpeed(_adsSamples, out float mm, out float deg);
                float rMm = _plainPeakMm > 1f ? mm / _plainPeakMm : 0f, rDeg = _plainPeakDeg > 1f ? deg / _plainPeakDeg : 0f;
                bool jump = rMm > 2.5f || rDeg > 2.5f;
                Line($"  {gunName,-6} reversal: gun top speed {mm,5:0} mm/s {deg,4:0} deg/s vs plain ADS {_plainPeakMm,5:0} / {_plainPeakDeg,4:0} " +
                     $"(x{rMm:0.00} / x{rDeg:0.00}){(jump ? " JUMP" : " ok")} over {_adsSamples.Count} frames");
                if (jump) _motionFailures++;
            }

            private void SampleAds()
            {
                var gun = _player.ActiveFirearm;
                var cam = _player.ViewCamera;
                float t = Time.time - _adsT0;
                if (gun == null || cam == null || t <= 0f) return; // the soldier reads the new input on the next frame
                var ap = gun.ActiveAimPointTransform;
                if (ap == null) return;
                var camT = cam.transform;
                _adsSamples.Add(new AdsSample
                {
                    t = t,
                    aiming = _player.IsAiming,
                    canFire = _player.CanShootNow,
                    farPx = ToPx(cam, ap.position + ap.forward * RangeM),
                    nearPx = ToPx(cam, ap.position + ap.forward * 0.25f),
                    fov = cam.fieldOfView,
                    blend = _player.AimBlend,
                    kinW = _player.UserInput != null ? _player.UserInput.GetValue<float>(KINEMATION.FPSAnimationFramework.Runtime.Core.FPSANames.AimingWeight) : -1f,
                    gunPos = camT.InverseTransformPoint(_player.ActiveItem.transform.position),
                    gunRot = Quaternion.Inverse(camT.rotation) * _player.ActiveItem.transform.rotation,
                    camRoot = _player.transform.InverseTransformPoint(camT.position),
                    gunRoot = _player.transform.InverseTransformPoint(_player.ActiveItem.transform.position),
                });
            }

            private static string RootCols(AdsSample s, CultureInfo c) => string.Join(",",
                (s.camRoot.x * 1000f).ToString("0.0", c), (s.camRoot.y * 1000f).ToString("0.0", c), (s.camRoot.z * 1000f).ToString("0.0", c),
                (s.gunRoot.x * 1000f).ToString("0.0", c), (s.gunRoot.y * 1000f).ToString("0.0", c), (s.gunRoot.z * 1000f).ToString("0.0", c));

            private void ReportAdsTrial(string gunName, string sight, string trialName, List<AdsSample> inS, List<AdsSample> outS,
                                        Vector3 hipPos, Quaternion hipRot, float hipFov)
            {
                if (inS.Count < 10) { Line($"  {gunName} {sight} {trialName}: too few samples ({inS.Count})"); return; }
                Vector2 restFar = Vector2.zero, restNear = Vector2.zero;
                float restFov = 0f;
                int n = 0;
                foreach (var s in inS)
                    if (s.t >= 0.75f) { restFar += s.farPx; restNear += s.nearPx; restFov += s.fov; n++; }
                if (n == 0) { Line($"  {gunName} {sight} {trialName}: no resting samples"); return; }
                restFar /= n; restNear /= n; restFov /= n;

                float Err(AdsSample s) => Mathf.Max((s.farPx - restFar).magnitude, (s.nearPx - restNear).magnitude);
                float aimFlag = -1f, canFire = -1f, onTarget = -1f, fov95 = -1f, blendDone = -1f, kinDone = -1f, lastOver10 = 0f;
                float minAfter = float.MaxValue, rebound = 0f, maxFrame = 0f, prevT = 0f;
                float fovSpan = Mathf.Abs(hipFov - restFov);
                var c = CultureInfo.InvariantCulture;
                foreach (var s in inS)
                {
                    float e = Err(s);
                    if (aimFlag < 0f && s.aiming) aimFlag = s.t;
                    if (canFire < 0f && s.canFire) canFire = s.t;
                    if (onTarget < 0f && e <= 10f) onTarget = s.t;
                    if (fov95 < 0f && Mathf.Abs(s.fov - restFov) <= 0.05f * Mathf.Max(0.5f, fovSpan)) fov95 = s.t;
                    if (blendDone < 0f && s.blend >= 0.999f) blendDone = s.t;
                    if (kinDone < 0f && s.kinW >= 0.999f) kinDone = s.t;
                    if (s.t < 0.75f && e > 10f) lastOver10 = s.t;
                    if (onTarget >= 0f && s.t < 0.75f)
                    {
                        minAfter = Mathf.Min(minAfter, e);
                        rebound = Mathf.Max(rebound, e - minAfter); // how far it swings back out after arriving
                    }
                    maxFrame = Mathf.Max(maxFrame, s.t - prevT);
                    prevT = s.t;
                    _adsTrace.AppendLine(string.Join(",", gunName, sight, trialName, "in", (s.t * 1000f).ToString("0", c),
                        (s.farPx - restFar).magnitude.ToString("0.0", c), (s.nearPx - restNear).magnitude.ToString("0.0", c),
                        s.fov.ToString("0.00", c), s.blend.ToString("0.000", c), s.kinW.ToString("0.000", c), s.aiming ? "1" : "0", s.canFire ? "1" : "0", RootCols(s, c)));
                }
                // stays10: the first frame after the last one that was more than 10 px off
                float stays10 = -1f;
                foreach (var s in inS) if (s.t > lastOver10) { stays10 = s.t; break; }
                if (trialName == "stand" && sight == "IRON") AdsPeakSpeed(inS, out _plainPeakMm, out _plainPeakDeg);

                var at = new StringBuilder();
                foreach (float probe in new[] { 0.05f, 0.1f, 0.15f, 0.2f, 0.25f, 0.3f, 0.4f, 0.5f })
                {
                    AdsSample best = inS[0];
                    foreach (var s in inS) if (Mathf.Abs(s.t - probe) < Mathf.Abs(best.t - probe)) best = s;
                    at.Append($" {probe * 1000f:0}:{Err(best):0}");
                }

                // out: back to the hip pose (within 6 mm and 1 deg of it) and the hip zoom
                float hipBack = -1f, fovBack = -1f;
                foreach (var s in outS)
                {
                    if (hipBack < 0f && (s.gunPos - hipPos).magnitude <= 0.006f && Quaternion.Angle(s.gunRot, hipRot) <= 1f) hipBack = s.t;
                    if (fovBack < 0f && Mathf.Abs(s.fov - hipFov) <= 0.05f * Mathf.Max(0.5f, fovSpan)) fovBack = s.t;
                    _adsTrace.AppendLine(string.Join(",", gunName, sight, trialName, "out", (s.t * 1000f).ToString("0", c),
                        ((s.gunPos - hipPos).magnitude * 1000f).ToString("0.0", c), Quaternion.Angle(s.gunRot, hipRot).ToString("0.00", c),
                        s.fov.ToString("0.00", c), s.blend.ToString("0.000", c), s.kinW.ToString("0.000", c), s.aiming ? "1" : "0", s.canFire ? "1" : "0", RootCols(s, c)));
                }
                string Ms(float v) => v < 0f ? "never" : $"{v * 1000f:0}";
                Line($"  {gunName,-6} {sight,-5} {trialName,-7} IN aim {Ms(aimFlag),4} canFire {Ms(canFire),4} onTarget {Ms(onTarget),4} stays10 {Ms(stays10),4} " +
                     $"fov95 {Ms(fov95),4} blend {Ms(blendDone),4} kin {Ms(kinDone),4} rebound {rebound,4:0.0}px maxFrame {maxFrame * 1000f,3:0}ms{(maxFrame > 0.03f ? " HITCH" : "")} " +
                     $"| OUT hip {Ms(hipBack),4} fov {Ms(fovBack),4} | err px at ms:{at}");
            }

            private IEnumerator MotionCheck()
            {
                Line("--- motion / stance stability (per-frame maxima; flags: FLIP gun > 50 deg off the aim line, POP > 12 deg or 4 cm in one frame, HANDS hand > 15 mm off its IK target) ---");
                _yaw = MostOpenYaw();
                Line($"  heading {_yaw:0} deg (most open direction; walls make Kinemation tuck the gun, which is not what this test is about)");
                var up = Vector2.up;
                _sampling = true;

                yield return Phase("idle", 1.2f, Cmd());
                HipPose(out Vector3 basePos, out Quaternion baseRot);
                yield return Phase("walk_forward", 1.0f, Cmd(up));
                yield return Phase("strafe_left", 0.8f, Cmd(Vector2.left));
                yield return Phase("strafe_right", 0.8f, Cmd(Vector2.right));
                yield return Phase("walk_back", 1.0f, Cmd(Vector2.down));
                yield return Phase("sprint", 1.0f, Cmd(up, sprint: true));
                yield return Snapshot("sprint");
                yield return Phase("sprint_turn", 0.8f, Cmd(up, sprint: true), 90f);
                yield return Phase("sprint_stop", 0.6f, Cmd());
                yield return Phase("turn_back", 0.6f, Cmd(), -90f);
                yield return Phase("walk_back_2", 1.6f, Cmd(Vector2.down));

                yield return Press(c => { c.crouch = true; return c; });
                yield return Phase("crouch_in", 0.7f, Cmd());
                yield return Snapshot("crouch");
                yield return Phase("crouch_walk", 1.0f, Cmd(up));
                yield return Phase("crouch_ads", 0.8f, Cmd(aim: true));
                yield return Press(c => { c.crouch = true; return c; });
                yield return Phase("crouch_out", 0.7f, Cmd());

                yield return Press(c => { c.prone = true; return c; });
                yield return Phase("prone_down", 1.3f, Cmd());
                yield return Snapshot("prone");
                yield return Phase("prone_crawl", 1.5f, Cmd(up));
                yield return Phase("prone_ads", 1.0f, Cmd(aim: true));
                yield return Phase("prone_ads_out", 0.4f, Cmd());
                yield return Press(c => { c.prone = true; return c; });
                yield return Phase("prone_up", 1.3f, Cmd());

                for (int j = 0; j < 3; j++)
                {
                    _cmd = Cmd(j == 1 ? Vector2.down : up);
                    yield return Press(c => { c.jump = true; return c; });
                    yield return Phase($"jump_{j + 1}", 0.45f, Cmd(j == 1 ? Vector2.down : up));
                    if (j == 0) yield return Snapshot("jump");
                    yield return Phase($"jump_{j + 1}_land", 0.5f, Cmd(j == 1 ? Vector2.down : up));
                }

                yield return Phase("sprint_into_slide", 0.7f, Cmd(up, sprint: true));
                yield return Press(c => { c.slide = true; return c; });
                yield return Phase("slide", 0.5f, Cmd(up, sprint: true));
                yield return Snapshot("slide");
                yield return Phase("slide_end", 0.8f, Cmd());
                yield return Phase("walk_back_3", 2.2f, Cmd(Vector2.down));

                yield return Phase("ads_in", 0.6f, Cmd(aim: true));
                yield return Phase("ads_walk", 1.0f, Cmd(up, aim: true));
                yield return Phase("ads_lean_q", 0.8f, Cmd(aim: true, lean: -1f));
                yield return Phase("ads_lean_e", 0.8f, Cmd(aim: true, lean: 1f));
                yield return Snapshot("ads_lean");
                yield return Phase("hip_fire", 0.8f, Cmd(fire: true));
                yield return Phase("ads_fire", 1.0f, Cmd(aim: true, fire: true));
                var rifle = _player.ActiveFirearm;
                for (int sight = 0; sight < 2 && rifle != null; sight++)
                {
                    if (rifle.HasUnifiedScopes) rifle.ApplyScopeChoice(sight == 0 ? 0 : 1);
                    rifle.RefillAmmo();
                    yield return Phase(sight == 0 ? "ads_spray_iron" : "ads_spray_holo", 3.0f, Cmd(aim: true, fire: true));
                    if (sight == 0) yield return Snapshot("ads_spray_iron");
                    yield return Phase("ads_settle_" + sight, 0.8f, Cmd(aim: true));
                }
                if (rifle != null && rifle.HasUnifiedScopes) rifle.ApplyScopeChoice(0);
                if (_player.ActiveFirearm != null) _player.ActiveFirearm.RefillAmmo();
                _cmd = Cmd(aim: true);
                yield return Press(c => { c.reload = true; return c; });
                yield return Phase("reload_ads", 2.6f, Cmd(aim: true));
                if (_player.ActiveFirearm != null) _player.ActiveFirearm.RefillAmmo();
                yield return Press(c => { c.reload = true; return c; });
                yield return Phase("reload_walk", 2.6f, Cmd(Vector2.down));

                yield return Press(c => { c.prone = true; return c; });
                yield return Phase("prone_for_death", 1.3f, Cmd());
                yield return DieAndRespawn("prone_death", basePos, baseRot);
                yield return Phase("sprint_for_death", 0.8f, Cmd(up, sprint: true));
                yield return DieAndRespawn("sprint_death", basePos, baseRot);
                yield return Press(c => { c.crouch = true; return c; });
                yield return Phase("crouch_ads_for_death", 1.0f, Cmd(aim: true));
                yield return DieAndRespawn("crouch_ads_death", basePos, baseRot);
                yield return Phase("final_idle", 1.0f, Cmd());
                _sampling = false;

                foreach (var p in _phases)
                {
                    bool flip = p.muzzleAim > 50f && !GunMayLeaveAim(p.name) && !p.name.StartsWith("respawn");
                    bool pop = (p.gunRotStep > 12f || p.gunPosStep > 4f) && !p.name.StartsWith("respawn");
                    bool hands = (p.ikGapR > 15f || p.ikGapL > 15f) && !p.name.StartsWith("reload");
                    if (flip || pop || hands || p.nan > 0) _motionFailures++;
                    bool intoEye = p.sightDepthMin < -15f;
                    if (intoEye) _motionFailures++;
                    string depth = p.sightDepthMin < float.MaxValue ? $" sightDepthMin={p.sightDepthMin,6:0.0}mm" : "";
                    Line($"  {p.name,-22} frames={p.frames,4} gunVsAim={p.muzzleAim,5:0.0}deg ikGap R/L={p.ikGapR,5:0.0}/{p.ikGapL,5:0.0}mm " +
                         $"wrist={p.wristErr,4:0.0}mm step gun={p.gunRotStep,5:0.0}deg/{p.gunPosStep,4:0.0}cm camRoll={p.camRollStep,4:0.0}deg{depth}" +
                         $"{(flip ? " FLIP" : "")}{(pop ? " POP" : "")}{(hands ? " HANDS" : "")}{(intoEye ? " INTO-EYE" : "")}{(p.nan > 0 ? " NAN" : "")}");
                    if (pop || flip || p.gunRotStep > 12f)
                        Line($"      biggest step at {p.stepAt:0.00}s [{p.stepState}]; biggest gun-vs-aim at {p.aimAt:0.00}s [{p.aimState}]");
                }
                Line($"motion failures: {_motionFailures}");
            }

            private IEnumerator AuditGun(LR2Firearm gun, string gunName)
            {
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = _player.Yaw, aimPitch = 0f };
                yield return Wait(0.6f);
                Snap hip = default;
                yield return AtFrameEnd(() =>
                {
                    hip = Measure(gun);
                    Capture(_player.ViewCamera, $"{gunName}_hip", null);
                    CaptureHands(gun, $"{gunName}_hip");
                });
                Row(gunName, "-", "hip", hip, hip);

                int sights = gun.HasUnifiedScopes ? LR2Session.ScopeNames.Length : 1;
                var antiClip = _player.GetComponent<WeaponAntiClip>();
                for (int sc = 0; sc < sights; sc++)
                {
                    if (gun.HasUnifiedScopes) gun.ApplyScopeChoice(sc);
                    string sight = gun.HasUnifiedScopes ? LR2Session.ScopeNames[sc].Split(' ')[0] : "IRON";
                    _cmd = new SoldierInput { absoluteLook = true, aimYaw = _player.Yaw, aimPitch = 0f, aim = true };
                    float limit = Time.time + 3f;
                    while (_player.AimBlend < 0.999f && Time.time < limit) yield return null;
                    yield return Wait(0.6f);

                    Snap ads = default;
                    yield return AtFrameEnd(() =>
                    {
                        ads = Measure(gun);
                        Capture(_player.ViewCamera, $"{gunName}_{sc}_{sight}_ads", ads);
                        if (sc == 0)
                        {
                            CaptureHands(gun, $"{gunName}_ads");
                            WithRobotShown(() =>
                            {
                                Capture(_player.ViewCamera, $"{gunName}_{sc}_{sight}_ads_robot", ads);
                                CaptureHands(gun, $"{gunName}_ads_robot");
                            });
                        }
                    });
                    Row(gunName, sight, "ads", ads, hip);

                    if (antiClip != null && antiClip.enabled && (sc == 0 || sc == 2))
                    {
                        antiClip.enabled = false;
                        yield return Wait(0.5f);
                        Snap raw = default;
                        yield return AtFrameEnd(() =>
                        {
                            raw = Measure(gun);
                            Capture(_player.ViewCamera, $"{gunName}_{sc}_{sight}_ads_noAntiClip", raw);
                        });
                        Row(gunName, sight, "ads_noAntiClip", raw, hip);
                        antiClip.enabled = true;
                    }

                    _cmd = new SoldierInput { absoluteLook = true, aimYaw = _player.Yaw, aimPitch = 0f };
                    yield return Wait(0.45f);
                }
            }

            /// <summary>Sight error after a long ADS spray and after respawns (the "drift" the developer saw).</summary>
            private IEnumerator DriftCheck()
            {
                var gun = _player.ActiveFirearm;
                if (gun == null) yield break;
                if (gun.HasUnifiedScopes) gun.ApplyScopeChoice(0);
                string name = "drift_" + gun.DisplayName.Replace(' ', '_');

                _cmd = new SoldierInput { absoluteLook = true, aimYaw = _player.Yaw, aimPitch = 0f, aim = true };
                yield return Wait(1.0f);
                Snap before = default;
                yield return AtFrameEnd(() => before = Measure(gun));
                Row(name, "IRON", "ads_fresh", before, before);

                gun.RefillAmmo();
                float yaw = _player.Yaw;
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = yaw, aimPitch = 0f, aim = true, fire = true };
                float end = Time.time + 3f;
                while (Time.time < end)
                {
                    if (gun.AmmoInMagazine < 5) gun.RefillAmmo();
                    yield return null;
                }
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = yaw, aimPitch = 0f, aim = true };
                yield return Wait(1.2f);
                Snap afterSpray = default;
                yield return AtFrameEnd(() =>
                {
                    afterSpray = Measure(gun);
                    Capture(_player.ViewCamera, name + "_after_spray", afterSpray);
                });
                Row(name, "IRON", "ads_after_spray", afterSpray, before);

                for (int d = 0; d < 3; d++)
                {
                    _cmd = new SoldierInput { absoluteLook = true, aimYaw = yaw, aimPitch = 0f };
                    var fatal = new DamageInfo { amount = 5000f, type = DamageType.Suicide, point = _player.transform.position };
                    _player.Health.ApplyDamage(ref fatal);
                    yield return Wait(0.4f);
                    _player.Respawn(_player.transform.position, yaw);
                    yield return Wait(0.8f);
                }
                gun = _player.ActiveFirearm;
                if (gun == null) yield break;
                if (gun.HasUnifiedScopes) gun.ApplyScopeChoice(0);
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = yaw, aimPitch = 0f, aim = true };
                yield return Wait(1.2f);
                Snap afterRespawn = default;
                yield return AtFrameEnd(() =>
                {
                    afterRespawn = Measure(gun);
                    Capture(_player.ViewCamera, name + "_after_3_respawns", afterRespawn);
                });
                Row(name, "IRON", "ads_after_3_respawns", afterRespawn, before);
            }

            private Snap Measure(LR2Firearm gun)
            {
                var s = new Snap();
                var cam = _player.ViewCamera;
                var ap = gun.ActiveAimPointTransform;
                if (cam == null || ap == null) return s;
                s.valid = true;
                var camT = cam.transform;
                s.fovV = cam.fieldOfView;
                s.eyeMm = camT.InverseTransformPoint(ap.position) * 1000f;
                Vector3 sightPoint = ap.position + ap.forward * RangeM;
                Vector3 bulletPoint = _player.AimOrigin + _player.AimDirection * RangeM;
                s.sightPx = ToPx(cam, sightPoint);
                s.bulletPx = ToPx(cam, bulletPoint);
                s.sightBulletPx = Vector2.Distance(s.sightPx, s.bulletPx);
                s.bulletAngleDeg = Vector3.Angle(_player.AimDirection, camT.forward);

                var gunT = gun.transform;
                var rh = LR2Bones.Get(_anim, HumanBodyBones.RightHand);
                var lh = LR2Bones.Get(_anim, HumanBodyBones.LeftHand);
                if (rh != null) { s.rHand = gunT.InverseTransformPoint(rh.position); s.rHandRot = Quaternion.Inverse(gunT.rotation) * rh.rotation; }
                if (lh != null) { s.lHand = gunT.InverseTransformPoint(lh.position); s.lHandRot = Quaternion.Inverse(gunT.rotation) * lh.rotation; }
                if (rh != null && _ikR != null) s.rIkGap = Vector3.Distance(rh.position, _ikR.position);
                if (lh != null && _ikL != null) s.lIkGap = Vector3.Distance(lh.position, _ikL.position);

                bool visualOn = _mirror != null && _mirror.VisualRoot != null && _mirror.VisualRoot.activeInHierarchy;
                if (visualOn)
                {
                    var vr = _mirror.VisualRightHand;
                    var vl = _mirror.VisualLeftHand;
                    if (vr != null) { s.vrHand = gunT.InverseTransformPoint(vr.position); s.vrHandRot = Quaternion.Inverse(gunT.rotation) * vr.rotation; }
                    if (vl != null) { s.vlHand = gunT.InverseTransformPoint(vl.position); s.vlHandRot = Quaternion.Inverse(gunT.rotation) * vl.rotation; }
                    foreach (var bone in FingerJoints)
                    {
                        var d = LR2Bones.Get(_anim, bone);
                        var v = _mirror.GetVisualBone(bone);
                        if (d == null || v == null) continue;
                        float gap = Vector3.Distance(d.position, v.position);
                        if (gap > s.fingerGapMax) { s.fingerGapMax = gap; s.fingerGapBone = bone.ToString(); }
                    }
                }

                if (visualOn) s.segmentAngles = SegmentAngles();
                ThumbTip(visualOn, true, cam, ap, out s.thumbLPx, out s.thumbLFront);
                ThumbTip(visualOn, false, cam, ap, out s.thumbRPx, out s.thumbRFront);

                var antiClip = _player.GetComponent<WeaponAntiClip>();
                if (antiClip != null && antiClip.enabled)
                {
                    var pa = typeof(WeaponAntiClip).GetProperty("LastLockApplied");
                    var pw = typeof(WeaponAntiClip).GetProperty("LastLockWeight");
                    if (pa != null) s.lockApplied = (bool)pa.GetValue(antiClip);
                    if (pw != null) s.lockWeight = (float)pw.GetValue(antiClip);
                }
                return s;
            }

            private static readonly HumanBodyBones[] SegmentBones =
            {
                HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
                HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
            };

            /// <summary>Angle between each visible finger segment and its robot twin (joint to next joint), plus the bone names.</summary>
            private string SegmentAngles()
            {
                var sb = new StringBuilder();
                foreach (var bone in SegmentBones)
                {
                    var d = LR2Bones.Get(_anim, bone);
                    var v = _mirror.GetVisualBone(bone);
                    if (d == null || v == null || d.childCount == 0 || v.childCount == 0) { sb.Append(bone).Append("=? "); continue; }
                    var dn = d.GetChild(0);
                    var vn = v.GetChild(0);
                    float ang = Vector3.Angle(dn.position - d.position, vn.position - v.position);
                    sb.Append($"{bone.ToString().Replace("Left", "")}={ang:0}deg[{d.name}>{dn.name}|{v.name}>{vn.name}] ");
                }
                return sb.ToString();
            }

            private void ThumbTip(bool visual, bool left, Camera cam, Transform ap, out float px, out bool front)
            {
                px = -1f;
                front = false;
                var mid = left ? HumanBodyBones.LeftThumbIntermediate : HumanBodyBones.RightThumbIntermediate;
                var dist = left ? HumanBodyBones.LeftThumbDistal : HumanBodyBones.RightThumbDistal;
                Transform a = visual ? _mirror.GetVisualBone(mid) : LR2Bones.Get(_anim, mid);
                Transform b = visual ? _mirror.GetVisualBone(dist) : LR2Bones.Get(_anim, dist);
                if (a == null || b == null) return;
                Vector3 tip = b.position + (b.position - a.position) * 0.8f;
                Vector3 vp = cam.WorldToViewportPoint(tip);
                if (vp.z <= 0f) return;
                px = Vector2.Distance(new Vector2(vp.x - 0.5f, vp.y - 0.5f) * new Vector2(1080f * cam.aspect, 1080f), Vector2.zero);
                float apDepth = cam.transform.InverseTransformPoint(ap.position).z;
                front = vp.z < apDepth + 0.5f;
            }

            private static Vector2 ToPx(Camera cam, Vector3 world)
            {
                Vector3 vp = cam.WorldToViewportPoint(world);
                if (vp.z <= 0f) return new Vector2(9999f, 9999f);
                return new Vector2((vp.x - 0.5f) * 1080f * cam.aspect, (vp.y - 0.5f) * 1080f);
            }

            private void Row(string gun, string sight, string pass, Snap s, Snap hip)
            {
                if (!s.valid) { Line($"{gun} {sight} {pass}: no camera or aim point"); return; }
                float slipR = (s.rHand - hip.rHand).magnitude * 1000f, slipL = (s.lHand - hip.lHand).magnitude * 1000f;
                float slipRd = Quaternion.Angle(s.rHandRot, hip.rHandRot), slipLd = Quaternion.Angle(s.lHandRot, hip.lHandRot);
                float vSlipR = (s.vrHand - hip.vrHand).magnitude * 1000f, vSlipL = (s.vlHand - hip.vlHand).magnitude * 1000f;
                Line($"{gun,-6} {sight,-6} {pass,-20} eye=({s.eyeMm.x:0.0},{s.eyeMm.y:0.0},{s.eyeMm.z:0.0})mm sight=({s.sightPx.x:0.0},{s.sightPx.y:0.0})px " +
                     $"bullet=({s.bulletPx.x:0.0},{s.bulletPx.y:0.0})px sight-bullet={s.sightBulletPx:0.0}px | slip R={slipR:0.0}mm/{slipRd:0.0}deg L={slipL:0.0}mm/{slipLd:0.0}deg " +
                     $"visSlip R={vSlipR:0.0} L={vSlipL:0.0}mm ikGap R={s.rIkGap * 1000f:0.0} L={s.lIkGap * 1000f:0.0}mm finger={s.fingerGapMax * 1000f:0.0}mm({s.fingerGapBone}) " +
                     $"thumbL={s.thumbLPx:0}px{(s.thumbLFront ? "*" : "")} thumbR={s.thumbRPx:0}px{(s.thumbRFront ? "*" : "")} lock={s.lockApplied}/{s.lockWeight:0.00} fov={s.fovV:0.0}" +
                     (pass == "ads" && sight == "IRON" && !string.IsNullOrEmpty(s.segmentAngles) ? "\n        segments: " + s.segmentAngles : ""));
                var c = CultureInfo.InvariantCulture;
                _csv.AppendLine(string.Join(",", gun, sight, pass,
                    s.eyeMm.x.ToString("0.0", c), s.eyeMm.y.ToString("0.0", c), s.eyeMm.z.ToString("0.0", c),
                    s.sightPx.x.ToString("0.0", c), s.sightPx.y.ToString("0.0", c), s.bulletPx.x.ToString("0.0", c), s.bulletPx.y.ToString("0.0", c),
                    s.sightBulletPx.ToString("0.0", c), s.bulletAngleDeg.ToString("0.000", c),
                    slipR.ToString("0.0", c), slipL.ToString("0.0", c), slipRd.ToString("0.0", c), slipLd.ToString("0.0", c),
                    vSlipR.ToString("0.0", c), vSlipL.ToString("0.0", c), (s.rIkGap * 1000f).ToString("0.0", c), (s.lIkGap * 1000f).ToString("0.0", c),
                    (s.fingerGapMax * 1000f).ToString("0.0", c), s.fingerGapBone ?? "-",
                    s.thumbLPx.ToString("0", c), s.thumbRPx.ToString("0", c), s.thumbLFront ? "1" : "0", s.thumbRFront ? "1" : "0",
                    s.lockApplied ? "1" : "0", s.lockWeight.ToString("0.00", c), s.fovV.ToString("0.0", c)));
            }

            private string DescribeCamera()
            {
                var cam = _player.ViewCamera;
                if (cam == null) return "none";
                var ctrl = _player.CameraController;
                Transform bone = null;
                if (ctrl != null)
                {
                    var f = ctrl.GetType().GetField("cameraBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null) bone = f.GetValue(ctrl) as Transform;
                }
                return $"{cam.name} parent={(cam.transform.parent != null ? cam.transform.parent.name : "-")} cameraBone={(bone != null ? bone.name : "-")}{(bone == cam.transform ? "(=camera)" : "")}";
            }

            private void Capture(Camera cam, string shotName, Snap? marks)
            {
                if (cam == null) return;
                const int w = 1280, h = 720;
                foreach (var pip in _player.GetComponentsInChildren<Camera>(false))
                    if (pip != cam && pip.enabled && pip.targetTexture != null) pip.Render();
                var tex = Render(cam, w, h);
                if (tex == null) return;
                Cross(tex, w / 2, h / 2, 10, 22, new Color(0f, 1f, 0.9f));          // screen centre (cyan ticks)
                if (marks.HasValue && marks.Value.valid)
                {
                    float k = h / 1080f;
                    var m = marks.Value;
                    Cross(tex, Mathf.RoundToInt(w / 2 + m.bulletPx.x * k), Mathf.RoundToInt(h / 2 + m.bulletPx.y * k), 0, 7, new Color(1f, 0f, 1f)); // bullet (magenta)
                    Cross(tex, Mathf.RoundToInt(w / 2 + m.sightPx.x * k), Mathf.RoundToInt(h / 2 + m.sightPx.y * k), 0, 5, new Color(1f, 0.9f, 0f));  // sight line (yellow)
                }
                tex.Apply();
                File.WriteAllBytes(Path.Combine(_outDir, shotName + ".png"), tex.EncodeToPNG());
                Destroy(tex);
                _shots++;
            }

            /// <summary>
            /// Runs <paramref name="capture"/> with Kinemation's robot drawn instead of the military mesh, to compare the visible
            /// grip with the real one. Same frame, same pose; renderers are restored afterwards.
            /// </summary>
            private void WithRobotShown(Action capture)
            {
                if (_mirror == null || _mirror.VisualRoot == null || !_mirror.VisualRoot.activeInHierarchy) return;
                var visualRoot = _mirror.VisualRoot.transform;
                var robot = new List<Renderer>();
                foreach (var r in _player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (!r.enabled && !r.transform.IsChildOf(visualRoot) && r.GetComponentInParent<LR2Item>(true) == null) robot.Add(r);
                var visual = new List<Renderer>();
                foreach (var r in visualRoot.GetComponentsInChildren<Renderer>())
                    if (r.enabled) visual.Add(r);
                foreach (var r in robot) r.enabled = true;
                foreach (var r in visual) r.enabled = false;
                try { capture(); }
                finally
                {
                    foreach (var r in robot) r.enabled = false;
                    foreach (var r in visual) r.enabled = true;
                }
            }

            /// <summary>Two side views of the hands on the gun (left side and right side), from a temporary camera.</summary>
            private void CaptureHands(LR2Firearm gun, string shotName)
            {
                var gunT = gun.transform;
                var go = new GameObject("LR2AimGrip_HandCam");
                var cam = go.AddComponent<Camera>();
                cam.fieldOfView = 34f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 50f;
                cam.cullingMask = ~0;
                cam.enabled = false;
                Vector3 centre = gunT.position + gunT.forward * 0.12f + gunT.up * 0.02f;
                for (int side = 0; side < 2; side++)
                {
                    float sx = side == 0 ? -1f : 1f;
                    go.transform.position = centre + gunT.right * (0.55f * sx) + gunT.up * 0.08f;
                    go.transform.rotation = Quaternion.LookRotation(centre - go.transform.position, gunT.up);
                    var tex = Render(cam, 960, 540);
                    if (tex == null) continue;
                    File.WriteAllBytes(Path.Combine(_outDir, $"{shotName}_hands_{(side == 0 ? "left" : "right")}.png"), tex.EncodeToPNG());
                    Destroy(tex);
                    _shots++;
                }
                Destroy(go);
            }

            private static Texture2D Render(Camera cam, int w, int h)
            {
                var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                try
                {
                    var req = new RenderPipeline.StandardRequest { destination = rt };
                    if (RenderPipeline.SupportsRenderRequest(cam, req))
                        for (int p = 0; p < 3; p++) RenderPipeline.SubmitRenderRequest(cam, req);
                    else
                    {
                        var prev = cam.targetTexture;
                        cam.targetTexture = rt;
                        cam.Render();
                        cam.targetTexture = prev;
                    }
                    var prevActive = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    RenderTexture.active = prevActive;
                    return tex;
                }
                finally { RenderTexture.ReleaseTemporary(rt); }
            }

            private static void Cross(Texture2D tex, int cx, int cy, int from, int to, Color c)
            {
                for (int d = from; d <= to; d++)
                {
                    Put(tex, cx + d, cy, c); Put(tex, cx - d, cy, c); Put(tex, cx, cy + d, c); Put(tex, cx, cy - d, c);
                    if (from == 0) { Put(tex, cx + d, cy + 1, c); Put(tex, cx - d, cy + 1, c); Put(tex, cx + 1, cy + d, c); Put(tex, cx + 1, cy - d, c); }
                }
            }

            private static void Put(Texture2D tex, int x, int y, Color c)
            {
                if (x >= 0 && y >= 0 && x < tex.width && y < tex.height) tex.SetPixel(x, y, c);
            }

            private void Finish(int code)
            {
                Line($"screenshots: {_shots} in {_outDir}");
                File.WriteAllText(Path.Combine(_outDir, "report.txt"), _report.ToString());
                File.WriteAllText(Path.Combine(_outDir, "metrics.csv"), _csv.ToString());
                Application.logMessageReceived -= OnLog;
                SessionState.SetInt(KeyCode, code);
                SessionState.SetBool(KeyDone, true);
                EditorApplication.delayCall += () => EditorApplication.isPlaying = false;
            }
        }
    }
}
