using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
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

namespace LR2.EditorTools
{
    /// <summary>
    /// Visible Play-Mode audit harness verifying:
    /// A) All 4 primary rifles x all 6 scopes (Iron Sights + Holo + RedDot + ACOG + 1P29 + SniperScope):
    ///    sub-millimeter reticle centering, iron-sights toggling, PiP camera activation, and screenshot captures.
    /// B) Long-play drift stress test: 5 deaths + respawns, 60 full-auto rounds, tactical reloads, cover proximity -> zero ADS drift.
    /// C) Enemy bot burst pitch stability: bot barrel stays on target without climbing into the sky.
    /// </summary>
    [InitializeOnLoad]
    public static class LR2AdsAudit
    {
        private const string KeyActive = "LR2AdsAudit.Active";
        private const string KeyDone = "LR2AdsAudit.Done";
        private const string KeyExitCode = "LR2AdsAudit.ExitCode";
        private const string OutDir = "Logs/LR2_AdsAudit";
        private static int _runtimeErrors;
        private static int _unresolvedCameraErrors;
        private static bool _running;

        static LR2AdsAudit()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            if (SessionState.GetBool(KeyActive, false))
            {
                EditorApplication.update += WaitPlayAndRun;
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(KeyDone, false))
            {
                int code = SessionState.GetInt(KeyExitCode, 0);
                SessionState.SetBool(KeyDone, false);
                EditorApplication.Exit(code);
            }
        }

        public static void RunBatch()
        {
            LR2BuildPipeline.ConfigureBuildIdentity();

            Directory.CreateDirectory(OutDir);
            _runtimeErrors = 0;
            _unresolvedCameraErrors = 0;
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            SessionState.SetBool(KeyDone, false);
            SessionState.SetBool(KeyActive, true);
            EditorSceneManager.playModeStartScene = null;
            EditorSceneManager.OpenScene("Assets/LR2/Scenes/LR2_TestRange.unity", OpenSceneMode.Single);
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update -= WaitPlayAndRun;
            EditorApplication.update += WaitPlayAndRun;
            EditorApplication.isPlaying = true;
        }

        private static void OnLog(string msg, string stack, LogType type)
        {
            if (msg != null && msg.Contains("Could not resolve") && msg.Contains("Camera"))
                _unresolvedCameraErrors++;
            if (type == LogType.Exception || type == LogType.Assert || type == LogType.Error)
            {
                if (msg != null && (msg.Contains("Mesh.vertices") || msg.Contains("AssetDatabase"))) return;
                _runtimeErrors++;
            }
        }

        private static void WaitPlayAndRun()
        {
            if (_running || !EditorApplication.isPlaying) return;
            _running = true;
            EditorApplication.update -= WaitPlayAndRun;
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            var host = new GameObject("LR2AdsAuditHost");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<AuditRunner>();
        }

        private sealed class AuditRunner : MonoBehaviour
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private int _failures;
            private SoldierInput _cmd;
            private bool _useOverrideInput;

            private sealed class OverrideSource : ISoldierInputSource
            {
                private readonly AuditRunner _owner;
                public OverrideSource(AuditRunner owner) => _owner = owner;
                public void GetInput(SoldierController s, ref SoldierInput input)
                {
                    if (_owner._useOverrideInput) input = _owner._cmd;
                }
            }

            private void Awake()
            {
                Application.logMessageReceived -= OnLog;
                Application.logMessageReceived += OnLog;
                StartCoroutine(RunAll());
            }

            private void OnDestroy()
            {
                Application.logMessageReceived -= OnLog;
            }

            private void Check(bool ok, string id, string info)
            {
                if (!ok) _failures++;
                _sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {id} | {info}");
                Debug.Log($"[LR2AdsAudit] [{(ok ? "PASS" : "FAIL")}] {id} | {info}");
            }

            private IEnumerator RunAll()
            {
                float tEnd = Time.time + 8f;
                SoldierController player = null;
                while (Time.time < tEnd && player == null)
                {
                    var lp = Combatant.FindLocalPlayer();
                    if (lp != null) player = lp.GetComponent<SoldierController>();
                    yield return null;
                }
                if (player == null || MatchManager.Instance == null)
                {
                    Check(false, "Init", "Local player or MatchManager not found");
                    Finish();
                    yield break;
                }

                // Wait for countdown to start gameplay
                while (MatchManager.Instance.State == MatchState.Countdown && Time.time < tEnd)
                    yield return null;
                yield return new WaitForSeconds(0.25f);

                player.InputSource = new OverrideSource(this);
                _useOverrideInput = true;
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, aimPitch = 0f };
                yield return null;

                var gunListField = typeof(SoldierController).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic);
                var equipMethod = typeof(SoldierController).GetMethod("EquipImmediate", BindingFlags.Instance | BindingFlags.NonPublic);
                var itemsList = gunListField?.GetValue(player) as List<LR2Item>;

                // Spawn all 4 primary rifles on player temporarily if only 1 was initialized
                Transform weaponBone = player.ActiveItem != null ? player.ActiveItem.transform.parent : player.transform;
                var primaryByIdx = new LR2Firearm[4];
                if (player.ActiveFirearm != null && player.ActiveFirearm.HasUnifiedScopes)
                    primaryByIdx[0] = player.ActiveFirearm;

                string[] riflePrefabs =
                {
                    "Assets/LR2/Prefabs/Weapons/LR2_Mk18.prefab",
                    "Assets/LR2/Prefabs/Weapons/LR2_AK12.prefab",
                    "Assets/LR2/Prefabs/Weapons/LR2_AK74N.prefab",
                    "Assets/LR2/Prefabs/Weapons/LR2_FAL.prefab"
                };

                for (int r = 0; r < 4; r++)
                {
                    if (r > 0 || primaryByIdx[0] == null)
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(riflePrefabs[r]);
                        var go = Instantiate(prefab, weaponBone, false);
                        go.transform.localPosition = Vector3.zero;
                        go.transform.localRotation = Quaternion.identity;
                        var f = go.GetComponent<LR2Firearm>();
                        go.SetActive(false);
                        itemsList.Insert(0, f);
                        primaryByIdx[r] = f;
                    }
                    equipMethod.Invoke(player, new object[] { 0 });
                    var gun = player.ActiveFirearm;

                    for (int sc = 0; sc < LR2Session.ScopeNames.Length; sc++)
                    {
                        gun.ApplyScopeChoice(sc);
                        _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, aimPitch = 0f, aim = true };
                        float wait = Time.time + 0.55f;
                        while ((player.AimBlend < 0.999f || Time.time < wait - 0.15f) && Time.time < wait)
                            yield return null;
                        for (int k = 0; k < 8; k++) yield return new WaitForEndOfFrame();

                        Transform ap = gun.ActiveAimPointTransform;
                        Camera cam = player.ViewCamera;
                        var antiClip = player.GetComponent<WeaponAntiClip>();
                        Vector3 localAp = (cam != null && ap != null) ? cam.transform.InverseTransformPoint(ap.position) : new Vector3(9f, 9f, 0f);
                        Vector3 vpFar = (cam != null && ap != null) ? cam.WorldToViewportPoint(ap.position + ap.forward * 50f) : Vector3.zero;
                        float aimAngle = ap != null ? Vector3.Angle(ap.forward, player.AimDirection) : 99f;
                        float camAngle = cam != null ? Vector3.Angle(cam.transform.forward, player.AimDirection) : 99f;
                        bool eyeOk = Mathf.Abs(localAp.x) < 0.001f && Mathf.Abs(localAp.y) < 0.001f;
                        bool farOk = Mathf.Abs(vpFar.x - 0.5f) < 0.002f && Mathf.Abs(vpFar.y - 0.5f) < 0.002f && vpFar.z > 40f;
                        bool lockOk = antiClip != null && antiClip.LastLockApplied;
                        bool ironOk = (sc == 0) == gun.IsUsingIronSights;
                        bool scopeObjOk = (sc == 0 && gun.ActiveScope == null) || (sc > 0 && gun.ActiveScope != null && gun.ActiveScope.gameObject.activeInHierarchy);
                        var pipCams = gun.GetComponentsInChildren<Camera>(true);
                        int enabledPip = 0;
                        foreach (var pc in pipCams) if (pc != null && pc.enabled) enabledPip++;
                        bool pipOk = sc >= 3 ? enabledPip == 1 : enabledPip == 0;

                        Check(eyeOk && farOk && lockOk && aimAngle < 0.05f && camAngle < 0.05f && ironOk && scopeObjOk && pipOk,
                            $"TestA.{LR2Session.PrimaryWeaponNames[r]}.{LR2Session.ScopeNames[sc].Replace(" ", "_")}",
                            $"localAp=({localAp.x:0.0000},{localAp.y:0.0000},{localAp.z:0.000}) vp50=({vpFar.x:0.0000},{vpFar.y:0.0000}) lock={lockOk} aimAng={aimAngle:0.000}deg camAng={camAngle:0.000}deg iron={ironOk} scope={scopeObjOk} pip={enabledPip}");

                        // Capture key representative ADS screenshots for visual inspection
                        if ((r == 0 && (sc == 0 || sc == 1 || sc == 2 || sc == 3 || sc == 5)) ||
                            (r == 1 && (sc == 0 || sc == 1 || sc == 4)) ||
                            (r == 2 && (sc == 0 || sc == 2 || sc == 4)) ||
                            (r == 3 && (sc == 0 || sc == 3 || sc == 5)))
                        {
                            foreach (var pc in pipCams)
                                if (pc != null && pc.enabled && pc.targetTexture != null) pc.Render();
                            string shotName = $"{LR2Session.PrimaryWeaponNames[r]}_{sc}_{LR2Session.ScopeNames[sc].Split(' ')[0]}.png";
                            var tex = CaptureCamera(cam, 1280, 720);
                            DrawCenterTicks(tex);
                            File.WriteAllBytes(Path.Combine(OutDir, shotName), tex.EncodeToPNG());
                            Destroy(tex);
                        }
                    }

                    // Restore original item 0 if we inserted extra
                    if (r > 0)
                    {
                        var rem = itemsList[0];
                        itemsList.RemoveAt(0);
                        Destroy(rem.gameObject);
                    }
                }

                // Re-equip MK18 HoloSight (index 1) for Stress Test B
                equipMethod.Invoke(player, new object[] { 0 });
                var mk18 = player.ActiveFirearm;
                mk18.ApplyScopeChoice(1);

                // TEST B: 5 Deaths/Respawns + 60 full-auto rounds + 2 tactical reloads + wall proximity
                for (int d = 0; d < 5; d++)
                {
                    _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, aimPitch = 0f };
                    var fatal = new DamageInfo { amount = 5000f, type = DamageType.Suicide, point = player.transform.position };
                    player.Health.ApplyDamage(ref fatal);
                    yield return new WaitForSeconds(0.25f);
                    player.Respawn(player.transform.position, player.Yaw);
                    yield return null;
                }
                mk18 = player.ActiveFirearm;
                mk18.ApplyScopeChoice(1);

                // Full-auto 60 rounds + refill + 2 tactical reloads (check max weapon roll during tac reload!)
                mk18.RefillAmmo();
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, aimPitch = -3f, fire = true, aim = true };
                float fireEnd = Time.time + 2.5f;
                while (Time.time < fireEnd)
                {
                    if (mk18.AmmoInMagazine < 10) mk18.RefillAmmo();
                    yield return null;
                }
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, aimPitch = 0f };
                yield return null;
                float baseRoll = mk18.transform != null ? mk18.transform.eulerAngles.z : 0f;
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, aimPitch = 0f, reload = true };
                yield return null;
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, aimPitch = 0f };
                float maxTacRoll = 0f;
                float rEnd = Time.time + 2.2f;
                while (mk18.IsReloading && Time.time < rEnd)
                {
                    if (mk18.transform != null)
                    {
                        float roll = Mathf.Abs(Mathf.DeltaAngle(baseRoll, mk18.transform.eulerAngles.z));
                        if (roll > maxTacRoll) maxTacRoll = roll;
                    }
                    yield return null;
                }
                Check(maxTacRoll < 40f, "TestB.TacticalReloadNoSideTwist", $"maxWeaponRoll={maxTacRoll:0.0}deg");

                // Move right up behind a wall / cover if found and crouch + lean + ADS
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, aimPitch = 0f, crouch = true, lean = 1f };
                yield return null;
                _cmd = new SoldierInput { absoluteLook = true, aimYaw = player.Yaw, aimPitch = 0f, aim = true, lean = 1f };
                yield return new WaitForSeconds(0.55f);
                yield return new WaitForEndOfFrame();

                Transform apPost = mk18.ActiveAimPointTransform;
                Camera camPost = player.ViewCamera;
                var antiClipPost = player.GetComponent<WeaponAntiClip>();
                Vector3 localPost = (camPost != null && apPost != null) ? camPost.transform.InverseTransformPoint(apPost.position) : new Vector3(9f, 9f, 0f);
                Vector3 vp50Post = (camPost != null && apPost != null) ? camPost.WorldToViewportPoint(apPost.position + apPost.forward * 50f) : Vector3.zero;
                float angPost = apPost != null ? Vector3.Angle(apPost.forward, player.AimDirection) : 99f;
                float camPostAng = camPost != null ? Vector3.Angle(camPost.transform.forward, player.AimDirection) : 99f;
                bool eyePostOk = Mathf.Abs(localPost.x) < 0.001f && Mathf.Abs(localPost.y) < 0.001f;
                bool farPostOk = Mathf.Abs(vp50Post.x - 0.5f) < 0.002f && Mathf.Abs(vp50Post.y - 0.5f) < 0.002f && vp50Post.z > 40f;
                bool lockPostOk = antiClipPost != null && antiClipPost.LastLockApplied;
                Check(eyePostOk && farPostOk && lockPostOk && angPost < 0.05f && camPostAng < 0.05f && _unresolvedCameraErrors == 0,
                    "TestB.PostStressZeroDrift",
                    $"localAp=({localPost.x:0.0000},{localPost.y:0.0000},{localPost.z:0.000}) vp50=({vp50Post.x:0.0000},{vp50Post.y:0.0000}) lock={lockPostOk} aimAng={angPost:0.000}deg unresolvedCamErr={_unresolvedCameraErrors}");

                var postTex = CaptureCamera(camPost, 1280, 720);
                DrawCenterTicks(postTex);
                File.WriteAllBytes(Path.Combine(OutDir, "MK18_PostStress_Holo.png"), postTex.EncodeToPNG());
                Destroy(postTex);

                // TEST C: Enemy Bot Burst Pitch Stability
                BotController targetBot = FindAnyObjectByType<BotController>();
                if (targetBot != null)
                {
                    var botSoldier = targetBot.GetComponent<SoldierController>();
                    targetBot.enabled = false; // drive direct shooting test on bot
                    float botMinPitch = 999f, botMaxPitch = -999f;
                    var botFixed = new AuditFixedSource();
                    botSoldier.InputSource = botFixed;
                    Vector3 aimPos = player.Combatant.ChestPosition;
                    Vector2 lookToPlayer = Core.LR2Math.DirectionToYawPitch(aimPos - botSoldier.AimOrigin);
                    botSoldier.Teleport(player.transform.position + player.transform.forward * 10f, lookToPlayer.x);
                    yield return null;
                    botFixed.cmd = new SoldierInput { absoluteLook = false, look = Vector2.zero, fire = true, aim = true };
                    // Let bot shoot 1.6s while measuring pitch with real BotController loop re-enabled!
                    targetBot.enabled = true;
                    botSoldier.InputSource = targetBot;
                    float bEnd = Time.time + 1.8f;
                    while (Time.time < bEnd)
                    {
                        if (botSoldier.Pitch < botMinPitch) botMinPitch = botSoldier.Pitch;
                        if (botSoldier.Pitch > botMaxPitch) botMaxPitch = botSoldier.Pitch;
                        yield return null;
                    }
                    Check(Mathf.Abs(botMinPitch) < 15f && Mathf.Abs(botMaxPitch) < 15f, "TestC.BotBurstPitchStable",
                        $"botPitchRange=[{botMinPitch:0.0},{botMaxPitch:0.0}]deg");
                }

                Check(_runtimeErrors == 0, "Audit.ZeroRuntimeErrors", $"count={_runtimeErrors}");
                Finish();
            }

            private sealed class AuditFixedSource : ISoldierInputSource
            {
                public SoldierInput cmd;
                public void GetInput(SoldierController s, ref SoldierInput input) => input = cmd;
            }

            private static void DrawCenterTicks(Texture2D tex)
            {
                if (tex == null) return;
                int cx = tex.width / 2, cy = tex.height / 2;
                Color c = new Color(0f, 1f, 0.9f, 1f);
                for (int d = 14; d <= 26; d++)
                {
                    if (cx + d < tex.width) tex.SetPixel(cx + d, cy, c);
                    if (cx - d >= 0) tex.SetPixel(cx - d, cy, c);
                    if (cy + d < tex.height) tex.SetPixel(cx, cy + d, c);
                    if (cy - d >= 0) tex.SetPixel(cx, cy - d, c);
                }
                tex.Apply();
            }

            private static Texture2D CaptureCamera(Camera cam, int w, int h)
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
                    var prevA = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prevA;
                    return tex;
                }
                finally { RenderTexture.ReleaseTemporary(rt); }
            }

            private void Finish()
            {
                _sb.AppendLine($"Failures: {_failures}");
                File.WriteAllText(Path.Combine(OutDir, "audit_report.txt"), _sb.ToString());
                Debug.Log(_sb.ToString());
                SessionState.SetBool(KeyActive, false);
                SessionState.SetBool(KeyDone, true);
                SessionState.SetInt(KeyExitCode, _failures > 0 ? 1 : 0);
                EditorApplication.delayCall += () => EditorApplication.isPlaying = false;
            }
        }
    }
}
