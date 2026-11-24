using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LR2.Combat;
using LR2.Core;
using LR2.Game;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LR2.World
{
    /// <summary>
    /// Media capture for the features page (B103, user 11-19: "That needs to be detailed with SS gifs and all"). Runs inside
    /// a visible <c>LR2SmokeTest.RunBatch</c> play session when one of these flags is on the command line, writing PNG frames
    /// to <c>-lr2OutDir</c> (Tools turn the frame runs into GIFs):
    /// <c>-lr2MenuMedia</c> (main menu cinematic, lobby and presets, then AI vs AI spectating),
    /// <c>-lr2KillcamMedia</c> (a bot's real shot is made a lethal headshot on the player: the bullet cam plays),
    /// <c>-lr2FxMedia</c> (Particle Pack steam, sparks and an explosion through a capture camera, HUD hidden).
    /// </summary>
    public class MediaDirector : MonoBehaviour
    {
        private string _mode, _out;

        private static string ArgVal(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var args = Environment.GetCommandLineArgs();
            string mode = Array.IndexOf(args, "-lr2AimBench") >= 0 ? "bench"
                        : Array.IndexOf(args, "-lr2MenuMedia") >= 0 ? "menu"
                        : Array.IndexOf(args, "-lr2KillcamMedia") >= 0 ? "killcam"
                        : Array.IndexOf(args, "-lr2FxMedia") >= 0 ? "fx" : null;
            if (mode == null || FindAnyObjectByType<MediaDirector>() != null) return;
            string outDir = "Logs/LR2_Media";
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-lr2OutDir") outDir = args[i + 1];
            Directory.CreateDirectory(outDir);
            var go = new GameObject("[LR2 MediaDirector]");
            DontDestroyOnLoad(go);
            var md = go.AddComponent<MediaDirector>();
            md._mode = mode;
            md._out = outDir;
            if (mode == "bench")
            {
                IsBenchRun = true;
                ConfigureBenchSkirmish(args);
            }
        }

        public static bool IsBenchRun { get; private set; }

        private static void ConfigureBenchSkirmish(string[] args)
        {
            // before MatchManager.Start: AI vs AI; -lr2AlphaDiff / -lr2BravoDiff + -lr2BenchArchetypes Alpha=DumbRobotic,Bravo=HumanLike
            string tOut = ArgVal(args, "-lr2TelemetryOut");
            if (!string.IsNullOrEmpty(tOut)) ExperimentTelemetryRecorder.CliOutputRoot = tOut;
            SkirmishSetup.Active = true;
            SkirmishSetup.PlayerSpectates = true;
            SkirmishSetup.BlindMode = false;
            string benchMode = ArgVal(args, "-lr2BenchMode");
            bool isCtf = string.Equals(benchMode, "CTF", StringComparison.OrdinalIgnoreCase);
            SkirmishSetup.Mode = isCtf ? GameModeType.CaptureTheFlag : GameModeType.TeamDeathmatch;
            SkirmishSetup.Bots.Clear();

            var teamArch = new Dictionary<TeamId, LR2.AI.BotArchetype>();
            string archArg = ArgVal(args, "-lr2BenchArchetypes");
            if (!string.IsNullOrEmpty(archArg))
            {
                foreach (var tok in archArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = tok.Split('=');
                    if (kv.Length == 2 && Enum.TryParse(kv[0].Trim(), true, out TeamId tid) && Enum.TryParse(kv[1].Trim(), true, out LR2.AI.BotArchetype at))
                        teamArch[tid] = at;
                }
            }

            void AddTeamBots(TeamId team, string spec)
            {
                LR2.AI.BotArchetype arch = teamArch.TryGetValue(team, out var a) ? a : LR2.AI.BotArchetype.HumanLike;
                if (!string.IsNullOrWhiteSpace(spec) && int.TryParse(spec, out int single))
                {
                    int d = Mathf.Clamp(single, 0, 3);
                    for (int k = 0; k < 4; k++) SkirmishSetup.Bots.Add(new SkirmishBot { team = team, difficulty = d, archetype = arch });
                }
                else
                {
                    for (int d = 0; d < 4; d++) SkirmishSetup.Bots.Add(new SkirmishBot { team = team, difficulty = d, archetype = arch });
                }
            }
            AddTeamBots(TeamId.Alpha, ArgVal(args, "-lr2AlphaDiff"));
            AddTeamBots(TeamId.Bravo, ArgVal(args, "-lr2BravoDiff"));
            // a bench-only match (no asset touched): no score or time limit cuts the sample short
            var bench = ScriptableObject.CreateInstance<MatchSettings>();
            bench.mode = SkirmishSetup.Mode;
            bench.scoreLimit = 999;
            bench.timeLimitSeconds = 900f;
            bench.countdownSeconds = 2f;
            bench.spawnOrbs = false;
            LR2Session.PendingSettings = bench;
        }

        private IEnumerator Start()
        {
            Note($"media run '{_mode}' -> {_out}");
            if (_mode == "menu") yield return MenuRun();
            else if (_mode == "killcam") yield return KillcamRun();
            else if (_mode == "fx") yield return FxRun();
            else if (_mode == "bench") yield return BenchRun();
            Note("media run done");
        }

        private void Note(string s)
        {
            Debug.Log("[LR2] MEDIA " + s);
            File.AppendAllText(Path.Combine(_out, "media_log.txt"), $"{DateTime.Now:HH:mm:ss} {s}\n");
        }

        private IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(_out, name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
        }

        private IEnumerator Frames(string prefix, int count, float interval)
        {
            for (int i = 0; i < count; i++)
            {
                yield return Shot($"{prefix}_{i:000}");
                yield return new WaitForSecondsRealtime(interval);
            }
        }

        private static bool Click(Transform root, string buttonName)
        {
            foreach (var b in root.GetComponentsInChildren<Button>(true))
                if (b.name == "Btn_" + buttonName) { b.onClick.Invoke(); return true; }
            return false;
        }

        // ------------------------------------------------------------------ menu, lobby, AI vs AI
        private IEnumerator MenuRun()
        {
            yield return new WaitForSecondsRealtime(1.5f);
            // B130: the title is a ~21 s loop of hard-cut b-roll shots; sample the whole loop
            yield return Frames("menu_title", 48, 0.45f);
            var menu = FindAnyObjectByType<LR2.UI.MainMenuController>();
            if (menu == null || menu.duelButton == null) { Note("no main menu"); yield break; }
            menu.duelButton.onClick.Invoke(); // PLAY
            yield return new WaitForSecondsRealtime(2.4f);
            var lobby = FindAnyObjectByType<LR2.UI.SkirmishLobby>();
            if (lobby == null) { Note("no lobby"); yield break; }
            yield return Shot("lobby_2teams");
            yield return Frames("lobby_tdm_bg", 18, 0.15f);
            Click(lobby.transform, "4-WAY"); yield return new WaitForSecondsRealtime(1.6f); yield return Shot("lobby_4way");
            Click(lobby.transform, "FFA"); yield return new WaitForSecondsRealtime(2.2f); yield return Frames("lobby_ffa_bg", 22, 0.15f);
            Click(lobby.transform, "EXPERIMENTS"); yield return new WaitForSecondsRealtime(1.6f); yield return Shot("lobby_experiments");
            SkirmishSetup.ApplyPreset("AI VS AI"); SkirmishSetup.PlayerSpectates = true;
            Note($"starting AI vs AI: {SkirmishSetup.Bots.Count} bots, spectate={SkirmishSetup.PlayerSpectates}");
            Click(lobby.transform, "START");
            // the match scene loads; this object survives it
            float t0 = Time.realtimeSinceStartup;
            while (SpectatorCamera.Instance == null && Time.realtimeSinceStartup - t0 < 30f) yield return null;
            if (SpectatorCamera.Instance == null) { Note("no spectator camera after START"); yield break; }
            yield return new WaitForSecondsRealtime(9f); // countdown and the first contact
            yield return Frames("aivsai_chase", 30, 0.15f);
            SpectatorCamera.Instance.SetView(1);
            yield return new WaitForSecondsRealtime(1.2f);
            yield return Frames("aivsai_eye", 18, 0.15f);
            SpectatorCamera.Instance.SetView(0);
            yield return new WaitForSecondsRealtime(4f);
            yield return Shot("aivsai_bench");
        }

        /// <summary>Flip detector context: the hidden driver rig's clips per animator layer, Kinemation inputs, the gun's parent bone.</summary>
        private static string RigState(LR2.Characters.SoldierController sc, Transform item)
        {
            var sb = new System.Text.StringBuilder(" |");
            var ui = sc.UserInput;
            if (ui != null)
            {
                int ti = ui.GetPropertyIndex(KINEMATION.FPSAnimationFramework.Runtime.Core.FPSANames.TurnOffset);
                int di = ui.GetPropertyIndex(KINEMATION.FPSAnimationFramework.Runtime.Core.FPSANames.MouseDeltaInput);
                if (ti >= 0) sb.Append(FormattableString.Invariant($" turnOff={ui.GetValue<float>(ti):0}"));
                if (di >= 0) { var d = ui.GetValue<Vector4>(di); sb.Append(FormattableString.Invariant($" dLook=({d.x:0.0},{d.y:0.0})")); }
            }
            sb.Append(" chain=");
            Quaternion soldierRot = Quaternion.Euler(0f, sc.Yaw, 0f);
            for (Transform t = item; t != null && t != sc.transform; t = t.parent)
            {
                // each link: local rotation (deg from identity) and world rotation against the soldier's yaw
                var le = t.localRotation.eulerAngles;
                sb.Append(FormattableString.Invariant($"{t.name}(L{Quaternion.Angle(Quaternion.identity, t.localRotation):0}:{le.x:0},{le.y:0},{le.z:0} W{Quaternion.Angle(soldierRot, t.rotation):0})>"));
            }
            // every weapon bone of the driver rig, against the IK WeaponBone that carries the gun and against the soldier
            if (item.parent != null && sc.Animator != null)
                foreach (var wb in sc.Animator.GetComponentsInChildren<Transform>(true))
                    if (wb != item.parent && wb.name.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0 && wb.GetComponent<LR2.Weapons.LR2Item>() == null)
                        sb.Append(FormattableString.Invariant($" {wb.name}:vsIK{Quaternion.Angle(wb.rotation, item.parent.rotation):0}/vsSoldier{Quaternion.Angle(wb.rotation, soldierRot):0}"));
            var pc = sc.Animator != null ? sc.Animator.GetComponent<KINEMATION.FPSAnimationFramework.Runtime.Playables.FPSPlayablesController>() : null;
            if (pc != null)
                foreach (var cn in new[] { "WeaponBoneWeight", "FullBodyWeight", "MaskAttachHand" })
                    try { sb.Append(FormattableString.Invariant($" {cn}={pc.GetCurveValue(cn):0.00}/{pc.GetCurveValue(cn, true):0.00}")); } catch (Exception) { }
            var an = sc.Animator;
            if (an != null)
                for (int l = 0; l < an.layerCount; l++)
                {
                    float w = l == 0 ? 1f : an.GetLayerWeight(l);
                    if (w < 0.05f) continue;
                    var ci = an.GetCurrentAnimatorClipInfo(l);
                    string clip = ci.Length > 0 && ci[0].clip != null ? ci[0].clip.name : "-";
                    sb.Append(FormattableString.Invariant($" [{an.GetLayerName(l)} {w:0.0} {clip}{(an.IsInTransition(l) ? " ~" : "")}]"));
                }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ B125 black box: the frames around a raw flip
        private sealed class BBTrack
        {
            public readonly System.Collections.Generic.Queue<string> ring = new System.Collections.Generic.Queue<string>();
            public float prevRaw = -1f; public int after = -1; public bool prevAim;
            public float spikeFrom = -1f;
        }
        private readonly System.Collections.Generic.Dictionary<Combatant, BBTrack> _bb = new System.Collections.Generic.Dictionary<Combatant, BBTrack>();
        private readonly System.Text.StringBuilder _bbOut = new System.Text.StringBuilder();
        private int _bbEvents;
        /// <summary>B125: one-frame weapon-bone spikes (a jump of 60+ deg that comes back next frame) and jumps that stay.</summary>
        private int _bbSpikes, _bbSustained;

        private void BlackBox(Combatant c, LR2.Characters.SoldierController sc, Transform item, Quaternion aimRot, float devRaw, float calib, float t0)
        {
            if (devRaw < 0f) return;
            if (!_bb.TryGetValue(c, out var b)) _bb[c] = b = new BBTrack();
            var ik = item.parent;
            var head = LR2.Characters.LR2Bones.Get(sc.Animator, HumanBodyBones.Head); // B136: generic robot too
            var hand = LR2.Characters.LR2Bones.Get(sc.Animator, HumanBodyBones.RightHand);
            Vector3 le = ik != null && head != null ? (Quaternion.Inverse(head.rotation) * ik.rotation).eulerAngles : Vector3.zero;
            Vector3 lp = ik != null && head != null ? head.InverseTransformPoint(ik.position) * 100f : Vector3.zero;
            float handDev = hand != null ? Quaternion.Angle(aimRot, hand.rotation) : -1f;
            float ads = 0f, turn = 0f; Vector4 dl = Vector4.zero;
            var ui = sc.UserInput;
            if (ui != null)
            {
                int ai = ui.GetPropertyIndex("AimingWeight"); if (ai >= 0) ads = ui.GetValue<float>(ai);
                int ti = ui.GetPropertyIndex(KINEMATION.FPSAnimationFramework.Runtime.Core.FPSANames.TurnOffset); if (ti >= 0) turn = ui.GetValue<float>(ti);
                int di = ui.GetPropertyIndex(KINEMATION.FPSAnimationFramework.Runtime.Core.FPSANames.MouseDeltaInput); if (di >= 0) dl = ui.GetValue<Vector4>(di);
            }
            bool wallRay = ik != null && Physics.Raycast(ik.position - ik.forward * 0.12f, ik.forward, 0.5f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
            string clip = "-";
            var an = sc.Animator;
            if (an != null && an.isActiveAndEnabled) { var ci = an.GetCurrentAnimatorClipInfo(0); if (ci.Length > 0 && ci[0].clip != null) clip = ci[0].clip.name + "@" + an.GetCurrentAnimatorStateInfo(0).normalizedTime.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); }
            bool aimToggle = sc.IsAiming != b.prevAim; b.prevAim = sc.IsAiming;
            string line = FormattableString.Invariant(
                $"{Time.time - t0:0.000} raw={devRaw:0} ikLocalRot=({le.x:0},{le.y:0},{le.z:0}) ikLocalPos=({lp.x:0},{lp.y:0},{lp.z:0})cm handVsAim={handDev:0} calib={calib:0.0} ") + FormattableString.Invariant(
                $"ads={ads:0.00}{(aimToggle ? (sc.IsAiming ? " AIM-IN" : " AIM-OUT") : "")} turn={turn:0} dLook=({dl.x:0.0},{dl.y:0.0}) speed={(sc.Movement != null ? sc.Movement.Speed : 0f):0.0} ") + FormattableString.Invariant(
                $"wallRay={wallRay} sprint={sc.IsSprinting} reload={sc.IsReloading} switch={sc.IsSwitchingWeapon} clip={clip} dt={Time.deltaTime * 1000f:0}ms");
            if (b.spikeFrom >= 0f)
            {
                if (devRaw < b.spikeFrom + 25f) _bbSpikes++; else _bbSustained++;
                b.spikeFrom = -1f;
            }
            if (b.prevRaw >= 0f && devRaw - b.prevRaw > 60f) b.spikeFrom = b.prevRaw;
            b.ring.Enqueue(line);
            while (b.ring.Count > 40) b.ring.Dequeue();
            if (b.after >= 0)
            {
                _bbOut.AppendLine("   + " + line);
                if (--b.after < 0) _bbOut.AppendLine();
            }
            else if (_bbEvents < 8 && b.prevRaw >= 0f && devRaw - b.prevRaw > 60f)
            {
                _bbEvents++;
                _bbOut.AppendLine(FormattableString.Invariant($"=== EVENT {_bbEvents}: {c.DisplayName} raw {b.prevRaw:0} -> {devRaw:0} in one frame (40 frames before, 10 after)"));
                foreach (var l in b.ring) _bbOut.AppendLine("     " + l);
                b.after = 10;
            }
            b.prevRaw = devRaw;
        }

        private sealed class KinTrack
        {
            public object profile; public int layersHash; public int count; public bool init;
            public Quaternion calibRot; public Vector3 calibPos; public bool calibInit;
            public Transform hand, wbRight; public float spawnTime; public bool wasAlive;
        }
        private readonly System.Collections.Generic.Dictionary<LR2.Characters.SoldierController, KinTrack> _kin =
            new System.Collections.Generic.Dictionary<LR2.Characters.SoldierController, KinTrack>();
        private static readonly System.Reflection.FieldInfo ActiveProfileField = typeof(KINEMATION.FPSAnimationFramework.Runtime.Core.FPSBoneController)
            .GetField("_activeProfile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo LayersField = typeof(KINEMATION.FPSAnimationFramework.Runtime.Core.FPSBoneController)
            .GetField("_animationLayers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// B125: Kinemation re-link detection per soldier (the active profile or the layer playables change: every layer job is
        /// rebuilt and PoseSampler.Initialize re-bakes IK WeaponBoneRight's offset from the right hand from the rig's current
        /// pose) and how far that calibration has moved from the soldier's first one.
        /// </summary>
        private (bool relinked, int count, float alive, float calib, float calibCm, string profile) KinState(LR2.Characters.SoldierController sc)
        {
            if (!_kin.TryGetValue(sc, out var k)) _kin[sc] = k = new KinTrack();
            bool alive = !sc.IsDead;
            if (alive && !k.wasAlive) k.spawnTime = Time.time;
            k.wasAlive = alive;
            var bc = sc.GetComponent<KINEMATION.FPSAnimationFramework.Runtime.Core.FPSBoneController>();
            bool relinked = false; string pname = "-";
            if (bc != null && ActiveProfileField != null && LayersField != null)
            {
                var prof = ActiveProfileField.GetValue(bc);
                int h = 0;
                if (LayersField.GetValue(bc) is System.Collections.IList list && list.Count > 0)
                    h = ((KINEMATION.FPSAnimationFramework.Runtime.Core.AnimationLayer)list[0]).playable.GetHandle().GetHashCode();
                pname = prof is UnityEngine.Object o && o != null ? o.name : "null";
                if (k.init && (!ReferenceEquals(prof, k.profile) || h != k.layersHash)) { relinked = true; k.count++; }
                k.profile = prof; k.layersHash = h; k.init = true;
            }
            if (k.hand == null && sc.Animator != null)
            {
                k.hand = LR2.Characters.LR2Bones.Get(sc.Animator, HumanBodyBones.RightHand);
                if (k.hand != null) foreach (Transform ch in k.hand) if (ch.name == "IK WeaponBoneRight") k.wbRight = ch;
            }
            float calib = 0f, calibCm = 0f;
            if (k.wbRight != null)
            {
                if (!k.calibInit) { k.calibRot = k.wbRight.localRotation; k.calibPos = k.wbRight.localPosition; k.calibInit = true; }
                calib = Quaternion.Angle(k.calibRot, k.wbRight.localRotation);
                calibCm = (k.wbRight.localPosition - k.calibPos).magnitude * 100f;
            }
            return (relinked, k.count, Time.time - k.spawnTime, calib, calibCm, pname);
        }

        // ------------------------------------------------------------------ accuracy and K/D bench
        /// <summary>Plays the AI vs AI bench for -lr2BenchSeconds (default 120) and writes shots, hits, kills and deaths per difficulty.</summary>
        private IEnumerator BenchRun()
        {
            float secs = 120f;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-lr2BenchSeconds") float.TryParse(args[i + 1], out secs);
            DateTime runStartedUtc = DateTime.UtcNow.AddSeconds(-2);
            string benchScene = ArgVal(args, "-lr2BenchScene") ?? "LR2_Saltworks";
            if (MatchManager.Instance == null && !SceneManager.GetActiveScene().name.Equals(benchScene, StringComparison.OrdinalIgnoreCase))
            {
                ConfigureBenchSkirmish(args);
                SceneManager.LoadScene(benchScene);
                float loadWait = Time.realtimeSinceStartup + 30f;
                while (MatchManager.Instance == null && Time.realtimeSinceStartup < loadWait) yield return null;
            }
            float playWait = Time.realtimeSinceStartup + 20f;
            while (MatchManager.Instance != null && MatchManager.Instance.State != MatchState.Playing && Time.realtimeSinceStartup < playWait)
                yield return null;
            float t0 = Time.time;
            // B125 layer ablation: -lr2AblateLayers Sway,Ads,... sets those Kinemation layer types' alpha to 0 (read live) for the
            // bench, restored at the end, to find which layer turns the weapon bone
            var ablated = new System.Collections.Generic.List<(KINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorLayerSettings s, float a)>();
            string ablate = "";
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-lr2AblateLayers") ablate = args[i + 1];
            if (ablate.Length > 0)
                foreach (var ls in Resources.FindObjectsOfTypeAll<KINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorLayerSettings>())
                    foreach (var key in ablate.Split(','))
                        if (key.Length > 0 && ls.GetType().Name.StartsWith(key, StringComparison.Ordinal)) { ablated.Add((ls, ls.alpha)); ls.alpha = 0f; }
            Note($"ablated layers: {ablate} ({ablated.Count} settings)");
            // weapon-flip detector v2 (user 11-19: "the weapon keeps on going up then rotating fully ... mainly during collision"):
            // every frame, each living bot's gun rotation in its own aim frame against that weapon's usual pose (the first steady
            // sample). Catches roll around the barrel too, which a muzzle-vs-aim angle misses. Context per event: sprint, reload,
            // weapon switch, ADS, prone weight, wall in front, nearest other soldier. The first big events get a chase-cam burst.
            int samples = 0, flips = 0, flipsNearWall = 0, flipsSprint = 0, flipsReload = 0, flipsNearSoldier = 0, flipsSwitch = 0, bursts = 0;
            float maxDev = 0f;
            int normalLogged = 0;
            int holdShots = 0;
            var flipLog = new System.Text.StringBuilder();
            var baseline = new System.Collections.Generic.Dictionary<string, Quaternion>();
            var lastLog = new System.Collections.Generic.Dictionary<Combatant, float>();
            var prevRot = new System.Collections.Generic.Dictionary<Combatant, Quaternion>();
            // B125 v3: sampled at the end of the frame (the rendered gun and this frame's aim); the raw Kinemation weapon bone is
            // the gun's parent (its rest local pose is identity), so raw and shown separate Kinemation from LR2's edits
            var eof = new WaitForEndOfFrame();
            var prevShown = new System.Collections.Generic.Dictionary<Combatant, Quaternion>();
            int rawFlips = 0, adsFrames = 0, adsJitter = 0, pushFrames = 0; float maxRaw = 0f, maxAdsJump = 0f, maxFeedback = 0f, maxFeedbackDeg = 0f, maxPush = 0f;
            // B125 v4: Kinemation re-links (FPSBoneController rebuilds every layer job and PoseSampler.Initialize re-bakes the
            // hand-to-gun offset of IK WeaponBoneRight from whatever pose the rig is in) and that calibration's drift
            int relinks = 0; float maxCalib = 0f;
            var relinkLog = new System.Text.StringBuilder();
            while (Time.time - t0 < secs && (MatchManager.Instance == null || MatchManager.Instance.State != MatchState.Ended))
            {
                if (holdShots < 2 && Time.time - t0 > 15f + 25f * holdShots && SpectatorCamera.Instance != null)
                {
                    // normal holds for the visual check of any weapon-bone change (chase view, then eye view)
                    SpectatorCamera.Instance.SetView(holdShots);
                    yield return Frames($"hold{holdShots}", 8, 0.35f);
                    SpectatorCamera.Instance.SetView(0);
                    holdShots++;
                }
                Combatant burstOn = null; float burstDev = 0f;
                foreach (var c in Combatant.All)
                {
                    if (c == null || !c.IsAlive) { if (c != null) prevRot.Remove(c); continue; }
                    var sc = c.GetComponent<LR2.Characters.SoldierController>();
                    var item = sc != null ? sc.ActiveItem : null;
                    var gun = sc != null ? sc.ActiveFirearm : null;
                    if (item == null || gun == null || gun.Muzzle == null) continue;
                    samples++;
                    Quaternion aimRot = Quaternion.Euler(sc.Pitch, sc.Yaw, 0f);
                    Quaternion local = Quaternion.Inverse(aimRot) * item.transform.rotation;
                    string key = gun.Definition != null ? gun.Definition.name : item.name;
                    float muzzleAng = Vector3.Angle(gun.Muzzle.forward, aimRot * Vector3.forward);
                    bool sprint = sc.IsSprinting;
                    if (!baseline.ContainsKey(key))
                    {
                        if (muzzleAng < 8f && !sprint && !gun.IsReloading && !sc.IsSwitchingWeapon) baseline[key] = local;
                        continue;
                    }
                    float dev = Quaternion.Angle(local, baseline[key]);
                    Transform ikBone = item.transform.parent;
                    float devRaw = ikBone != null ? Quaternion.Angle(Quaternion.Inverse(aimRot) * ikBone.rotation, baseline[key]) : dev;
                    if (devRaw > 60f) rawFlips++;
                    maxRaw = Mathf.Max(maxRaw, devRaw);
                    maxFeedback = Mathf.Max(maxFeedback, sc.LastItemFeedbackOffset);
                    maxFeedbackDeg = Mathf.Max(maxFeedbackDeg, sc.LastItemFeedbackAngle);
                    var px = sc.GetComponent<LR2.Characters.RetargetProxy>();
                    float pushNow = px != null ? px.LastEyePlanePush : 0f;
                    maxPush = Mathf.Max(maxPush, pushNow);
                    var kin = KinState(sc);
                    BlackBox(c, sc, item.transform, aimRot, ikBone != null ? Quaternion.Angle(Quaternion.Inverse(aimRot) * ikBone.rotation, baseline[key]) : -1f, kin.calib, t0);
                    if (kin.relinked)
                    {
                        relinks++;
                        if (relinkLog.Length < 30000)
                            relinkLog.AppendLine(FormattableString.Invariant($"{Time.time - t0:0.00}s RELINK {c.DisplayName} {key} n={kin.count} alive={kin.alive:0.0}s ads={sc.IsAiming} speed={(sc.Movement != null ? sc.Movement.Speed : 0f):0.0} calib={kin.calib:0.0}deg/{kin.calibCm:0.0}cm profile={kin.profile}"));
                    }
                    maxCalib = Mathf.Max(maxCalib, kin.calib);
                    float adsW = 0f;
                    if (sc.UserInput != null) { int ai = sc.UserInput.GetPropertyIndex("AimingWeight"); if (ai >= 0) adsW = sc.UserInput.GetValue<float>(ai); }
                    if (adsW > 0.9f)
                    {
                        adsFrames++;
                        if (pushNow > 0.001f) pushFrames++;
                        if (prevShown.TryGetValue(c, out var ps))
                        {
                            float jump = Quaternion.Angle(ps, local);
                            maxAdsJump = Mathf.Max(maxAdsJump, jump);
                            if (jump > 10f) adsJitter++;
                        }
                        prevShown[c] = local;
                    }
                    else prevShown.Remove(c);
                    float rate = prevRot.TryGetValue(c, out var pr) && Time.deltaTime > 1e-4f ? Quaternion.Angle(pr, item.transform.rotation) / Time.deltaTime : 0f;
                    prevRot[c] = item.transform.rotation;
                    if (dev < 10f && normalLogged < 3 && !sprint && Time.time - t0 > 5f)
                    {
                        normalLogged++;
                        flipLog.AppendLine(FormattableString.Invariant($"NORMAL {c.DisplayName} dev={dev:0}") + RigState(sc, item.transform));
                    }
                    if (dev < 60f) continue;
                    flips++;
                    maxDev = Mathf.Max(maxDev, dev);
                    Vector3 eye = sc.EyeTransform != null ? sc.EyeTransform.position : c.transform.position + Vector3.up * 1.5f;
                    bool wall = Physics.SphereCast(eye, 0.15f, aimRot * Vector3.forward, out _, 1.0f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
                    float near = 99f; string nearName = "";
                    foreach (var o in Combatant.All)
                        if (o != null && o != c && o.IsAlive)
                        {
                            float d = Vector3.Distance(o.transform.position, c.transform.position);
                            if (d < near) { near = d; nearName = o.DisplayName; }
                        }
                    if (wall) flipsNearWall++;
                    if (sprint) flipsSprint++;
                    if (gun.IsReloading) flipsReload++;
                    if (sc.IsSwitchingWeapon) flipsSwitch++;
                    if (near < 1.2f) flipsNearSoldier++;
                    if (dev > burstDev && bursts < 4) { burstOn = c; burstDev = dev; }
                    if (lastLog.TryGetValue(c, out var lt) && Time.time - lt < 0.4f) continue;
                    lastLog[c] = Time.time;
                    if (flipLog.Length < 90000)
                    {
                        Vector3 e = local.eulerAngles, be = baseline[key].eulerAngles;
                        flipLog.AppendLine(FormattableString.Invariant(
                            $"{Time.time - t0:0.00}s {c.DisplayName} {key} dev={dev:0} rate={rate:0}dps muzzle={muzzleAng:0} local=({e.x:0},{e.y:0},{e.z:0}) base=({be.x:0},{be.y:0},{be.z:0}) ") + FormattableString.Invariant(
                            $"sprint={sprint} reload={gun.IsReloading} switch={sc.IsSwitchingWeapon} ads={sc.IsAiming} prone={sc.ProneWeightEffective:0.00} wall={wall} ") + FormattableString.Invariant(
                            $"near={near:0.0}m({nearName}) speed={(sc.Movement != null ? sc.Movement.Speed : 0f):0.0} pitch={sc.Pitch:0} goal={c.GetComponent<LR2.AI.BotController>()?.CurrentGoalName} calib={kin.calib:0.0}deg/{kin.calibCm:0.0}cm relinks={kin.count} alive={kin.alive:0.0}s") + RigState(sc, item.transform));
                    }
                }
                if (burstOn != null && burstDev > 80f && SpectatorCamera.Instance != null)
                {
                    bursts++;
                    flipLog.AppendLine(FormattableString.Invariant($"   -> burst flip{bursts} on {burstOn.DisplayName} dev={burstDev:0}"));
                    SpectatorCamera.Instance.SetTarget(burstOn);
                    SpectatorCamera.Instance.SetView(0);
                    yield return Frames($"flip{bursts}", 10, 0.06f);
                    continue;
                }
                yield return eof;
            }
            foreach (var (ls, a) in ablated) if (ls != null) ls.alpha = a;
            int guardFrames = 0;
            foreach (var px in FindObjectsByType<LR2.Characters.RetargetProxy>(FindObjectsSortMode.None)) guardFrames += px.FlipGuardFrames;
            File.WriteAllText(Path.Combine(_out, "blackbox.txt"), _bbOut.ToString());
            File.WriteAllText(Path.Combine(_out, "flips.txt"), FormattableString.Invariant(
                $"spikes1f={_bbSpikes} sustainedJumps={_bbSustained} sharedMotions={!LR2.Characters.SoldierController.PerSoldierIkMotions} relinks={relinks} maxCalibDrift={maxCalib:0.0}deg itemReset={LR2.Characters.SoldierController.ItemFeedbackReset} guard={LR2.Characters.RetargetProxy.FlipGuardEnabled} guardFrames={guardFrames} ablate={ablate} samples={samples} flips(dev>60)={flips} max={maxDev:0} rawFlips={rawFlips} maxRaw={maxRaw:0} adsFrames={adsFrames} adsJitter(>10deg/frame)={adsJitter} maxAdsJump={maxAdsJump:0} pushFramesInAds={pushFrames} maxPush={maxPush * 100f:0.0}cm maxFeedback={maxFeedback * 100f:0.0}cm/{maxFeedbackDeg:0}deg nearWall={flipsNearWall} nearSoldier(<1.2m)={flipsNearSoldier} sprint={flipsSprint} reload={flipsReload} switch={flipsSwitch} bursts={bursts}") + "\n" + relinkLog + flipLog);
            Note($"weapon flips: {flips} of {samples} (max {maxDev:0}, wall {flipsNearWall}, soldier {flipsNearSoldier}, sprint {flipsSprint}, reload {flipsReload}, switch {flipsSwitch})");
            var rows = new System.Collections.Generic.SortedDictionary<string, int[]>();
            foreach (var c in Combatant.All)
            {
                if (c == null || c.GetComponent<LR2.AI.BotController>() == null) continue;
                string n = c.DisplayName ?? "";
                int a = n.LastIndexOf('['), b = n.LastIndexOf(']');
                string key = a >= 0 && b > a ? n.Substring(a + 1, b - a - 1) : "BOT";
                if (!rows.TryGetValue(key, out var r)) rows[key] = r = new int[5];
                r[0] += c.stats.shotsFired; r[1] += c.stats.shotsHit; r[2] += c.stats.kills; r[3] += c.stats.deaths; r[4] += c.stats.headshots;
            }
            var sb = new System.Text.StringBuilder($"aim={(LR2.AI.BotController.LegacyAim ? "legacy" : "human (B118)")} seconds={Time.time - t0:0}\n");
            sb.AppendLine("difficulty,shots,hits,accuracy,kills,deaths,kd,headshots");
            foreach (var kv in rows)
            {
                var r = kv.Value;
                sb.AppendLine(FormattableString.Invariant($"{kv.Key},{r[0]},{r[1]},{(r[0] > 0 ? (float)r[1] / r[0] : 0f):0.000},{r[2]},{r[3]},{(r[3] > 0 ? (float)r[2] / r[3] : r[2]):0.00},{r[4]}"));
            }
            File.WriteAllText(Path.Combine(_out, "bench.csv"), sb.ToString());
            Note("bench written:\n" + sb);

            // B127: per-team result and each LLM commander's record (run with -lr2LlmTeams Alpha to compare LLM vs rule bots)
            var teams = new System.Text.StringBuilder("team,commander,kills,deaths,kd,score\n");
            var mmEnd = MatchManager.Instance;
            foreach (TeamId t in new[] { TeamId.Alpha, TeamId.Bravo, TeamId.Charlie, TeamId.Delta })
            {
                int k = 0, d = 0, n = 0;
                foreach (var c in Combatant.All) if (c != null && c.Team == t) { k += c.stats.kills; d += c.stats.deaths; n++; }
                if (n == 0) continue;
                LR2.AI.LLM.LLMCommander cmd = null;
                foreach (var lc in LR2.AI.LLM.LLMCommander.All) if (lc != null && lc.Team == t) cmd = lc;
                teams.AppendLine(FormattableString.Invariant($"{t},{(cmd != null ? "LLM " + cmd.Model : "rules")},{k},{d},{(d > 0 ? (float)k / d : k):0.00},{(mmEnd != null ? mmEnd.GetTeamScore(t) : 0)}"));
            }
            foreach (var lc in LR2.AI.LLM.LLMCommander.All)
                if (lc != null)
                    teams.AppendLine(FormattableString.Invariant(
                        $"# {lc.Team} LLM: mode={lc.Mode} decisions={lc.Decisions} failures={lc.Failures} orders={lc.OrdersIssued} rejected={lc.OrdersRejected} interrupts={lc.InterruptsTriggered} micro={lc.MicroChunksIssued} util={lc.UtilityOrdersIssued} avgLatency={(lc.Decisions - lc.Failures > 0 ? lc.TotalLatency / (lc.Decisions - lc.Failures) : 0f):0.00}s tokens={lc.PromptTokens}+{lc.CompletionTokens} lastPlan='{lc.LastPlan}' lastError='{lc.LastError}'"));
            teams.AppendLine(FormattableString.Invariant($"# Groq requests={LR2.AI.LLM.GroqClient.Requests} failures={LR2.AI.LLM.GroqClient.Failures} rateLimited={LR2.AI.LLM.GroqClient.RateLimited} key={LR2.AI.LLM.GroqClient.KeySource}"));
            File.WriteAllText(Path.Combine(_out, "teams.csv"), teams.ToString());
            Note("teams written:\n" + teams);

            // B149: Per-bot stats table & LLM decision logs for tier-vs-LLM behavioral study
            var botsSb = new System.Text.StringBuilder("team,commander,name,difficulty,shots,hits,accuracy,kills,deaths,kd,headshots\n");
            foreach (var c in Combatant.All)
            {
                if (c == null || c.GetComponent<LR2.AI.BotController>() == null) continue;
                string bn = c.DisplayName ?? "Bot";
                int bi = bn.LastIndexOf('['), bj = bn.LastIndexOf(']');
                string tier = bi >= 0 && bj > bi ? bn.Substring(bi + 1, bj - bi - 1) : "BOT";
                LR2.AI.LLM.LLMCommander cmd = null;
                foreach (var lc in LR2.AI.LLM.LLMCommander.All) if (lc != null && lc.Team == c.Team) cmd = lc;
                float acc = c.stats.shotsFired > 0 ? (float)c.stats.shotsHit / c.stats.shotsFired : 0f;
                float bkd = c.stats.deaths > 0 ? (float)c.stats.kills / c.stats.deaths : c.stats.kills;
                botsSb.AppendLine(FormattableString.Invariant($"{c.Team},{(cmd != null ? "LLM" : "rules")},\"{bn.Replace('"', '\'')}\",{tier},{c.stats.shotsFired},{c.stats.shotsHit},{acc:0.000},{c.stats.kills},{c.stats.deaths},{bkd:0.00},{c.stats.headshots}"));
            }
            File.WriteAllText(Path.Combine(_out, "bots.csv"), botsSb.ToString());
            string llmDir = Application.isEditor ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "LR2_LLM"))
                                                 : Path.Combine(Application.persistentDataPath, "LLM");
            if (Directory.Exists(llmDir))
            {
                foreach (var jf in Directory.GetFiles(llmDir, "llm_*.jsonl"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(jf) >= runStartedUtc)
                            File.Copy(jf, Path.Combine(_out, Path.GetFileName(jf)), true);
                    }
                    catch (Exception) { }
                }
            }
            if (ExperimentTelemetryRecorder.Instance != null || !string.IsNullOrEmpty(ExperimentTelemetryRecorder.CliOutputRoot))
            {
                mmEnd?.EndMatch();
                float waitUntil = Time.realtimeSinceStartup + 12f;
                while (!ExperimentTelemetryRecorder.IsIdleOrSaveComplete && Time.realtimeSinceStartup < waitUntil)
                    yield return null;
            }
            if (Array.IndexOf(args, "-lr2QuitOnDone") >= 0)
            {
                Note("bench complete; quitting (-lr2QuitOnDone)");
#if UNITY_EDITOR
                if (Application.isEditor) UnityEditor.EditorApplication.Exit(0); else
#endif
                Application.Quit(0);
            }
        }

        // ------------------------------------------------------------------ headshot bullet cam
        private IEnumerator KillcamRun()
        {
            yield return new WaitForSecondsRealtime(7f); // countdown
            var player = Combatant.FindLocalPlayer();
            var psc = player != null ? player.GetComponent<LR2.Characters.SoldierController>() : null;
            LR2.Characters.SoldierController bot = null;
            foreach (var c in Combatant.All)
            {
                if (c == null || c == player || !c.IsAlive || !TeamUtil.AreHostile(c, player)) continue;
                var sc = c.GetComponent<LR2.Characters.SoldierController>();
                if (sc != null && c.GetComponent<LR2.AI.BotController>() != null) { bot = sc; break; }
            }
            if (player == null || psc == null || bot == null) { Note("no player or hostile bot"); yield break; }
            // stage a duel 14 m apart on open ground, facing each other
            float yaw = psc.Yaw;
            Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 bp = player.Position + fwd * 14f;
            if (Physics.Raycast(bp + Vector3.up * 3f, Vector3.down, out var gh, 8f, LR2Layers.GroundMask, QueryTriggerInteraction.Ignore)) bp = gh.point + Vector3.up * 0.05f;
            bot.Teleport(bp, yaw + 180f);
            var agent = bot.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent != null && agent.isOnNavMesh) agent.Warp(bp);
            // no invulnerability: bots do not target a protected player (that is why the first staging never fired)
            player.Health.CancelSpawnProtection();
            bool armed = true, killed = false;
            void OnShot(ShotEvent e)
            {
                if (!armed || e.shooter == null || e.shooter == player || !TeamUtil.AreHostile(e.shooter, player)) return;
                armed = false;
                Vector3 head = player.HeadPosition;
                var info = new DamageInfo
                {
                    amount = 500f, type = DamageType.Bullet, zone = HitZone.Head, attacker = e.shooter,
                    weaponName = "rifle",
                    point = head, normal = -(head - e.origin).normalized, direction = (head - e.origin).normalized,
                    distance = Vector3.Distance(head, e.origin), force = 140f
                };
                player.Health.ApplyDamage(ref info);
                killed = true;
            }
            GameEvents.ShotFired += OnShot;
            float t0 = Time.realtimeSinceStartup;
            while (!killed && Time.realtimeSinceStartup - t0 < 25f) yield return null;
            GameEvents.ShotFired -= OnShot;
            if (!killed) { Note("the bot never fired"); yield break; }
            Note("headshot kill staged; recording the killcam");
            yield return new WaitForSecondsRealtime(1.9f);
            yield return Shot("killcam_replay");
            yield return new WaitForSecondsRealtime(0.6f);
            yield return Frames("killcam_bullet", 42, 0.09f);
        }

        // ------------------------------------------------------------------ Particle Pack on the map
        private IEnumerator FxRun()
        {
            yield return new WaitForSecondsRealtime(5f);
            foreach (var c in FindObjectsByType<Canvas>(FindObjectsSortMode.None)) c.enabled = false;
            var camGo = new GameObject("[Media Cam]");
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<HDAdditionalCameraData>();
            cam.depth = 50f;
            cam.fieldOfView = 48f;
            cam.nearClipPlane = 0.05f;
            var fx = MapAmbientFx.Instance;
            if (fx != null && fx.SteamSpots.Count > 0)
            {
                var (p, n) = fx.SteamSpots[0];
                Vector3 side = Vector3.Cross(Vector3.up, n).normalized;
                camGo.transform.position = p + n * 1.3f + side * 1.2f + Vector3.up * 0.25f;
                camGo.transform.LookAt(p + n * 0.6f + Vector3.up * 0.25f);
                yield return new WaitForSecondsRealtime(1f);
                yield return Frames("fx_steam", 20, 0.1f);
            }
            else Note("no steam leaks on this map");
            if (fx != null && fx.SparkSpots.Count > 0)
            {
                Vector3 s = fx.SparkSpots[0];
                camGo.transform.position = s + new Vector3(1.8f, -1.2f, 1.4f);
                camGo.transform.LookAt(s - Vector3.up * 0.3f);
                yield return new WaitForSecondsRealtime(0.5f);
                fx.SparkNow();
                yield return Frames("fx_sparks", 16, 0.07f);
            }
            else Note("no sparking fittings on this map");
            DestructibleProp boom = null;
            foreach (var d in FindObjectsByType<DestructibleProp>(FindObjectsSortMode.None)) if (d != null && d.explosive && d.isActiveAndEnabled) { boom = d; break; }
            if (boom != null)
            {
                Vector3 c = boom.transform.position + Vector3.up * 0.6f;
                Vector3 view = c + new Vector3(6f, 1.4f, 4f);
                foreach (float a in new[] { 0f, 90f, 180f, 270f })
                {
                    Vector3 v = c + Quaternion.Euler(0f, a, 0f) * new Vector3(6.5f, 1.4f, 3f);
                    if (!Physics.Linecast(v, c, LR2Layers.EnvironmentMask & ~(1 << boom.gameObject.layer), QueryTriggerInteraction.Ignore)) { view = v; break; }
                }
                camGo.transform.position = view;
                camGo.transform.LookAt(c + Vector3.up * 0.8f);
                yield return new WaitForSecondsRealtime(0.6f);
                var info = new DamageInfo { amount = 9999f, type = DamageType.Bullet, zone = HitZone.Generic, point = c, direction = (c - view).normalized, normal = -(c - view).normalized, force = 50f };
                boom.ApplyDamage(ref info);
                yield return Frames("fx_explosion", 26, 0.06f);
            }
            else Note("no explosive props on this map");
        }
    }
}
