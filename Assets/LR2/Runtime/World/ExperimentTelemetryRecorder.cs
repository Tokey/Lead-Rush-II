using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using LR2.AI;
using LR2.AI.LLM;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using LR2.Game;
using LR2.UI;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LR2.World
{
    /// <summary>
    /// B155: Zero-stutter in-RAM gameplay telemetry recorder for human players and every bot archetype
    /// (<see cref="BotArchetype.HumanLike"/>, <see cref="BotArchetype.DumbRobotic"/>, <see cref="BotArchetype.LlmEnhanced"/>,
    /// <see cref="BotArchetype.HumanFitted"/>). Collects pre-allocated value structs at 20-60 Hz plus lossless frame-accumulated
    /// raw mouse vs recoil deltas, then serializes CSVs & JSON asynchronously off the main thread behind a full-screen Saving overlay
    /// at round end.
    /// </summary>
    public class ExperimentTelemetryRecorder : MonoBehaviour
    {
        public static ExperimentTelemetryRecorder Instance { get; private set; }
        public static bool IsIdleOrSaveComplete => Instance == null || !Instance._isSaving;
        public static string CliOutputRoot { get; set; } = "";

        private struct TickSample
        {
            public float matchTime;
            public int id;
            public string name;
            public bool isHuman;
            public BotArchetype archetype;
            public int difficulty;
            public TeamId team;
            public bool alive;
            public Vector3 pos;
            public Vector3 vel;
            public float speed;
            public float yaw;
            public float pitch;
            public float rawLookDx;
            public float rawLookDy;
            public float recoilDx;
            public float recoilDy;
            public float angVelYaw;
            public float angVelPitch;
            public Vector2 moveInput;
            public bool sprinting;
            public bool crouching;
            public bool proning;
            public bool aiming;
            public float aimBlend;
            public float lean;
            public bool firing;
            public bool reloading;
            public string weapon;
            public int ammoMag;
            public float hp;
            public float armor;
            public bool hasVisibleEnemy;
            public float nearestEnemyDist;
            public float errYawDeg;
            public float errPitchDeg;
            public float errTotalDeg;
            public float enemyVelLat;
            public float nearCoverDist;
            public BotGoal botGoal;
            public BotOrder botOrder;
            public bool isFlicking;
        }

        private struct TelemetryEvent
        {
            public float matchTime;
            public string type;
            public int actorId;
            public int targetId;
            public Vector3 pos;
            public float yaw;
            public float pitch;
            public float distM;
            public float errDeg;
            public float damage;
            public HitZone zone;
            public string weapon;
            public string extra;
        }

        private struct FlickRecord
        {
            public int actorId;
            public bool isHuman;
            public BotArchetype archetype;
            public int difficulty;
            public float startT;
            public float endT;
            public float durationS;
            public float startAngDeg;
            public float peakAngVel;
            public float timeToPeakFrac;
            public float pathLenDeg;
            public float straightnessRatio;
            public float overshootDeg;
            public float finalErrDeg;
            public float reactionLatencyS;
            public bool isCombat;
        }

        private struct RecoilShotRecord
        {
            public int actorId;
            public bool isHuman;
            public BotArchetype archetype;
            public int difficulty;
            public int burstId;
            public int shotIdx;
            public string weapon;
            public bool isAds;
            public float dtSinceBurstStart;
            public float recoilKickPitch;
            public float rawPulldownPitch;
            public float netPitchDrift;
            public float yawWeave;
            public float errYawDeg;
            public float errPitchDeg;
            public bool hitTarget;
        }

        private class ActorTracker
        {
            public int id;
            public Combatant combatant;
            public SoldierController soldier;
            public BotController bot;
            public string displayName;
            public bool isHuman;
            public BotArchetype archetype;
            public int difficulty;
            public TeamId team;

            // Per-frame lossless delta accumulators flushed each telemetry sample
            public Vector2 accRawLook;
            public Vector2 accRecoil;
            public bool accFire;

            public float prevYaw, prevPitch;
            public bool prevAim, prevCrouch, prevSprint, prevReload, prevAlive = true, prevTargetVis;
            public Combatant prevTarget;
            public float targetAcquiredAt = -999f;
            public bool loggedFirstShotForAcquire;

            // Flick detector
            public bool inFlick;
            public float flickStartT;
            public float flickStartYaw, flickStartPitch;
            public float flickGoalYaw, flickGoalPitch;
            public float flickPeakAngVel, flickTimeAtPeak, flickPathDeg, flickMaxBeyondGoal;
            public float flickStartErrDeg, flickReactLat;
            public bool flickIsCombat;

            // Recoil burst tracker
            public int burstId;
            public int burstShotIdx;
            public float lastShotTime = -999f;
            public float burstStartTime = -999f;
            public float shotAccRecoilPitch, shotAccRawPitch, shotAccYawWeave;
            public int lastRecoilRecordIndex = -1;
            public float shotFiredAt = -999f;

            // Match aggregates
            public int shots;
            public int hits;
            public int headshots;
            public float flickDurSum;
            public int flickCount;
            public float reactSum;
            public int reactCount;
            public float strafeSignPrev;
            public int strafeFlips;
            public int sampleCount;
            public int crouchSamples;
            public int adsSamples;
            public int sprintSamples;
        }

        private int _sampleHz = 20;
        private float _sampleInterval = 0.05f;
        private float _nextSampleAt;
        private float _matchStartTime = -1f;
        private float _mapMinX = -50f, _mapMaxX = 50f, _mapMinZ = -50f, _mapMaxZ = 50f;
        private Vector2 _lastGoalErr;

        private readonly List<TickSample> _ticks = new List<TickSample>(32768);
        private readonly List<TelemetryEvent> _events = new List<TelemetryEvent>(4096);
        private readonly List<FlickRecord> _flicks = new List<FlickRecord>(1024);
        private readonly List<RecoilShotRecord> _recoils = new List<RecoilShotRecord>(2048);
        private readonly List<float> _frameMs = new List<float>(16384);
        private readonly Dictionary<Combatant, ActorTracker> _trackers = new Dictionary<Combatant, ActorTracker>(24);
        private readonly List<ActorTracker> _trackerList = new List<ActorTracker>(24);

        private bool _isSaving;
        private bool _saveStarted;
        private volatile float _saveProgress;
        private volatile string _saveStage = "PREPARING BUFFERS...";
        private GameObject _overlayGo;
        private Image _overlayBarFill;
        private TMP_Text _overlayStatusText;
        private TMP_Text _overlaySubText;
        private float _overlayShownAt;

        public static void EnsureForMatch()
        {
            if (Instance != null) return;
            bool cliOn = Array.IndexOf(Environment.GetCommandLineArgs(), "-lr2RecordTelemetry") >= 0 || !string.IsNullOrEmpty(CliOutputRoot);
            if (!ExperimentSessionRunner.IsSessionActive && !LR2Settings.RecordTelemetry && !cliOn) return;
            var go = new GameObject("[LR2 Experiment Telemetry]");
            go.AddComponent<ExperimentTelemetryRecorder>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            _sampleHz = ExperimentSessionRunner.IsSessionActive && ExperimentSessionRunner.ActiveConfig != null
                ? Mathf.Clamp(ExperimentSessionRunner.ActiveConfig.telemetryHz, 10, 60)
                : 20;
            _sampleInterval = 1f / _sampleHz;

            var tri = UnityEngine.AI.NavMesh.CalculateTriangulation();
            if (tri.vertices != null && tri.vertices.Length > 0)
            {
                _mapMinX = _mapMinZ = float.MaxValue;
                _mapMaxX = _mapMaxZ = float.MinValue;
                foreach (var v in tri.vertices)
                {
                    if (v.x < _mapMinX) _mapMinX = v.x;
                    if (v.x > _mapMaxX) _mapMaxX = v.x;
                    if (v.z < _mapMinZ) _mapMinZ = v.z;
                    if (v.z > _mapMaxZ) _mapMaxZ = v.z;
                }
            }

            GameEvents.ShotFired += OnShotFired;
            GameEvents.Hit += OnHit;
            GameEvents.Killed += OnKilled;
            GameEvents.MatchStateChanged += OnMatchStateChanged;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            GameEvents.ShotFired -= OnShotFired;
            GameEvents.Hit -= OnHit;
            GameEvents.Killed -= OnKilled;
            GameEvents.MatchStateChanged -= OnMatchStateChanged;
        }

        private void OnMatchStateChanged(MatchStateEvent e)
        {
            if (e.current == MatchState.Playing && _matchStartTime < 0f)
            {
                _matchStartTime = Time.time;
                _nextSampleAt = Time.time;
                RefreshParticipants();
            }
            else if (e.current == MatchState.Ended && !_saveStarted)
            {
                _saveStarted = true;
                StartSavingAsync();
            }
        }

        private void RefreshParticipants()
        {
            var mm = MatchManager.Instance;
            IReadOnlyList<Combatant> all = mm != null ? mm.Participants : Combatant.All;
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null || c.Team == TeamId.Hostile || _trackers.ContainsKey(c)) continue;
                var sc = c.GetComponent<SoldierController>();
                var bot = c.GetComponent<BotController>();
                var tr = new ActorTracker
                {
                    id = _trackerList.Count + 1,
                    combatant = c,
                    soldier = sc,
                    bot = bot,
                    displayName = c.DisplayName,
                    isHuman = c.IsLocalPlayer && bot == null,
                    archetype = bot != null ? bot.Archetype : BotArchetype.HumanLike,
                    difficulty = bot != null ? bot.TierIndex : -1,
                    team = c.Team,
                    prevYaw = sc != null ? sc.Yaw : c.transform.eulerAngles.y,
                    prevPitch = sc != null ? sc.Pitch : 0f,
                    prevAlive = c.IsAlive
                };
                _trackers[c] = tr;
                _trackerList.Add(tr);
            }
        }

        private void LateUpdate()
        {
            if (_isSaving)
            {
                UpdateOverlayVisuals();
                return;
            }
            var mm = MatchManager.Instance;
            if (mm == null || mm.State != MatchState.Playing) return;
            if (_matchStartTime < 0f) { _matchStartTime = Time.time; _nextSampleAt = Time.time; }
            _frameMs.Add(Time.unscaledDeltaTime * 1000f);
            if (_trackerList.Count == 0) RefreshParticipants();

            // Accumulate every rendered frame cleanly into pre-allocated fields (zero allocations!)
            float dt = Mathf.Max(0.0001f, Time.deltaTime);
            for (int i = 0; i < _trackerList.Count; i++)
            {
                var tr = _trackerList[i];
                if (tr.soldier == null) continue;
                tr.accRawLook += tr.soldier.LastRawLookDelta;
                tr.accRecoil += tr.soldier.LastRecoilKickDelta;
                tr.accFire |= tr.soldier.LastFireInput;
                tr.shotAccRecoilPitch += tr.soldier.LastRecoilKickDelta.y;
                tr.shotAccRawPitch += tr.soldier.LastRawLookDelta.y;
                tr.shotAccYawWeave += Mathf.Abs(tr.soldier.LastRawLookDelta.x);
            }

            if (Time.time >= _nextSampleAt)
            {
                float interval = Mathf.Max(_sampleInterval, Time.time - (_nextSampleAt - _sampleInterval));
                _nextSampleAt = Time.time + _sampleInterval;
                SampleTick(Time.time - _matchStartTime, interval);
            }
        }

        private void SampleTick(float matchTime, float intervalDt)
        {
            for (int i = 0; i < _trackerList.Count; i++)
            {
                var tr = _trackerList[i];
                var c = tr.combatant;
                var sc = tr.soldier;
                if (c == null || sc == null) continue;

                bool alive = c.IsAlive;
                if (alive && !tr.prevAlive)
                    AddEvent(matchTime, "RESPAWN", tr.id, 0, c.Position, sc.Yaw, sc.Pitch, 0f, 0f, 0f, HitZone.UpperTorso, "", "");
                tr.prevAlive = alive;

                float curYaw = sc.Yaw;
                float curPitch = sc.Pitch;
                float angVelYaw = Mathf.DeltaAngle(tr.prevYaw, curYaw) / intervalDt;
                float angVelPitch = (curPitch - tr.prevPitch) / intervalDt;
                tr.prevYaw = curYaw;
                tr.prevPitch = curPitch;

                // Find nearest hostile + compute crosshair relative error (identical math for Human & Bot!)
                Combatant nearest = null;
                float minDist = 999f;
                bool anyVis = false;
                float errYaw = 0f, errPitch = 0f, errTotal = 0f, enemyLatVel = 0f;

                if (tr.bot != null && tr.bot.Blackboard.target != null && tr.bot.Blackboard.targetVisible)
                {
                    nearest = tr.bot.Blackboard.target;
                    minDist = Vector3.Distance(sc.AimOrigin, nearest.ChestPosition);
                    anyVis = true;
                }
                else
                {
                    for (int j = 0; j < _trackerList.Count; j++)
                    {
                        if (j == i) continue;
                        var other = _trackerList[j].combatant;
                        if (other == null || !other.IsAlive || !c.IsHostileTo(other)) continue;
                        float d = Vector3.Distance(sc.AimOrigin, other.ChestPosition);
                        if (d < minDist)
                        {
                            minDist = d;
                            nearest = other;
                        }
                    }
                    if (nearest != null && minDist < 85f)
                        anyVis = !Physics.Linecast(sc.AimOrigin, nearest.ChestPosition, LR2Layers.VisionMask, QueryTriggerInteraction.Ignore)
                              || !Physics.Linecast(sc.AimOrigin, nearest.HeadPosition, LR2Layers.VisionMask, QueryTriggerInteraction.Ignore);
                }

                if (nearest != null)
                {
                    Vector3 aimPt = nearest.ChestPosition;
                    Vector3 dir = aimPt - sc.AimOrigin;
                    Vector2 targetYawPitch = LR2Math.DirectionToYawPitch(dir);
                    errYaw = Mathf.DeltaAngle(targetYawPitch.x, curYaw);   // positive = right of target
                    errPitch = targetYawPitch.y - curPitch;               // positive = looking below target (wait Pitch down is +, so dir pitch - curPitch)
                    // Normalize consistently: desired.y - _aimPitch = how high/low crosshair sits relative to chest
                    Vector3 fwd = Quaternion.Euler(curPitch, curYaw, 0f) * Vector3.forward;
                    errTotal = Vector3.Angle(fwd, dir);
                    Vector3 nDir = dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.forward;
                    Vector3 lat = Vector3.ProjectOnPlane(nearest.Velocity, nDir);
                    enemyLatVel = lat.magnitude;
                }

                if (anyVis && nearest != null)
                {
                    if (!tr.prevTargetVis || tr.prevTarget != nearest)
                    {
                        tr.targetAcquiredAt = Time.time;
                        tr.loggedFirstShotForAcquire = false;
                        tr.prevTarget = nearest;
                        AddEvent(matchTime, "TARGET_ACQUIRED", tr.id, TargetIdOf(nearest), c.Position, curYaw, curPitch, minDist, errTotal, 0f, HitZone.UpperTorso, "", "");
                    }
                }
                tr.prevTargetVis = anyVis;

                // Transitions
                if (sc.IsAiming != tr.prevAim) { AddEvent(matchTime, sc.IsAiming ? "ADS_IN" : "ADS_OUT", tr.id, 0, c.Position, curYaw, curPitch, 0f, 0f, 0f, HitZone.UpperTorso, "", ""); tr.prevAim = sc.IsAiming; }
                if (sc.IsCrouching != tr.prevCrouch) { AddEvent(matchTime, sc.IsCrouching ? "CROUCH_IN" : "CROUCH_OUT", tr.id, 0, c.Position, curYaw, curPitch, 0f, 0f, 0f, HitZone.UpperTorso, "", ""); tr.prevCrouch = sc.IsCrouching; }
                if (sc.IsSprinting != tr.prevSprint) { AddEvent(matchTime, sc.IsSprinting ? "SPRINT_IN" : "SPRINT_OUT", tr.id, 0, c.Position, curYaw, curPitch, 0f, 0f, 0f, HitZone.UpperTorso, "", ""); tr.prevSprint = sc.IsSprinting; }
                if (sc.IsReloading && !tr.prevReload) { AddEvent(matchTime, "RELOAD_START", tr.id, 0, c.Position, curYaw, curPitch, 0f, 0f, 0f, HitZone.UpperTorso, sc.ActiveItem != null ? sc.ActiveItem.name : "", $"{sc.ActiveFirearm?.AmmoInMagazine ?? 0}"); }
                tr.prevReload = sc.IsReloading;

                // Online Flick Segmentation (identical for Human & all Bots!)
                UpdateFlickDetector(tr, matchTime, intervalDt, curYaw, curPitch, angVelYaw, angVelPitch, errTotal, anyVis, nearest);

                // Near cover probe
                float nearCover = 12f;
                for (int cIdx = 0; cIdx < CoverPoint.All.Count; cIdx++)
                {
                    var cp = CoverPoint.All[cIdx];
                    if (cp == null) continue;
                    float dc = Vector3.Distance(c.Position, cp.transform.position);
                    if (dc < nearCover) nearCover = dc;
                }

                Vector3 vel = sc.Movement != null ? sc.Movement.Velocity : c.Velocity;
                float spd = new Vector2(vel.x, vel.z).magnitude;

                if (alive)
                {
                    tr.sampleCount++;
                    if (sc.IsCrouching) tr.crouchSamples++;
                    if (sc.IsAiming) tr.adsSamples++;
                    if (sc.IsSprinting) tr.sprintSamples++;
                    float mx = sc.LastMoveInput.x;
                    if (Mathf.Abs(mx) > 0.35f)
                    {
                        float sgn = Mathf.Sign(mx);
                        if (tr.strafeSignPrev != 0f && sgn != tr.strafeSignPrev) tr.strafeFlips++;
                        tr.strafeSignPrev = sgn;
                    }
                }

                _ticks.Add(new TickSample
                {
                    matchTime = matchTime,
                    id = tr.id,
                    name = tr.displayName,
                    isHuman = tr.isHuman,
                    archetype = tr.archetype,
                    difficulty = tr.difficulty,
                    team = tr.team,
                    alive = alive,
                    pos = c.Position,
                    vel = vel,
                    speed = spd,
                    yaw = curYaw,
                    pitch = curPitch,
                    rawLookDx = tr.accRawLook.x,
                    rawLookDy = tr.accRawLook.y,
                    recoilDx = tr.accRecoil.x,
                    recoilDy = tr.accRecoil.y,
                    angVelYaw = angVelYaw,
                    angVelPitch = angVelPitch,
                    moveInput = sc.LastMoveInput,
                    sprinting = sc.IsSprinting,
                    crouching = sc.IsCrouching,
                    proning = sc.IsProning,
                    aiming = sc.IsAiming,
                    aimBlend = sc.AimBlend,
                    lean = sc.LastLeanInput,
                    firing = tr.accFire,
                    reloading = sc.IsReloading,
                    weapon = sc.ActiveItem != null ? sc.ActiveItem.name : "",
                    ammoMag = sc.ActiveFirearm != null ? sc.ActiveFirearm.AmmoInMagazine : 0,
                    hp = c.Health != null ? c.Health.Current : 0f,
                    armor = c.Health != null ? c.Health.Armor : 0f,
                    hasVisibleEnemy = anyVis,
                    nearestEnemyDist = nearest != null ? minDist : -1f,
                    errYawDeg = errYaw,
                    errPitchDeg = errPitch,
                    errTotalDeg = errTotal,
                    enemyVelLat = enemyLatVel,
                    nearCoverDist = nearCover,
                    botGoal = tr.bot != null ? tr.bot.Intent.goal : BotGoal.Idle,
                    botOrder = tr.bot != null && tr.bot.Blackboard.hasDirective ? tr.bot.Blackboard.directive.order : BotOrder.None,
                    isFlicking = tr.inFlick || (tr.bot != null && tr.bot.IsFlickingNow)
                });

                tr.accRawLook = Vector2.zero;
                tr.accRecoil = Vector2.zero;
                tr.accFire = false;
            }
        }

        private void UpdateFlickDetector(ActorTracker tr, float matchTime, float dt, float curYaw, float curPitch, float angVelYaw, float angVelPitch, float errTotal, bool vis, Combatant nearest)
        {
            float angVel = Mathf.Sqrt(angVelYaw * angVelYaw + angVelPitch * angVelPitch);
            float stepAng = angVel * dt;
            if (!tr.inFlick)
            {
                if (angVel >= 95f)
                {
                    tr.inFlick = true;
                    tr.flickStartT = matchTime;
                    tr.flickStartYaw = curYaw;
                    tr.flickStartPitch = curPitch;
                    tr.flickPeakAngVel = angVel;
                    tr.flickTimeAtPeak = matchTime;
                    tr.flickPathDeg = stepAng;
                    tr.flickStartErrDeg = errTotal;
                    tr.flickIsCombat = vis && nearest != null;
                    tr.flickReactLat = tr.flickIsCombat && tr.targetAcquiredAt > 0f ? Mathf.Clamp(Time.time - tr.targetAcquiredAt, 0f, 3f) : 0f;
                    tr.flickMaxBeyondGoal = 0f;
                    if (nearest != null)
                    {
                        Vector2 g = LR2Math.DirectionToYawPitch(nearest.ChestPosition - tr.soldier.AimOrigin);
                        tr.flickGoalYaw = g.x; tr.flickGoalPitch = g.y;
                    }
                    else
                    {
                        tr.flickGoalYaw = curYaw; tr.flickGoalPitch = curPitch;
                    }
                }
            }
            else
            {
                tr.flickPathDeg += stepAng;
                if (angVel > tr.flickPeakAngVel) { tr.flickPeakAngVel = angVel; tr.flickTimeAtPeak = matchTime; }
                float elapsed = matchTime - tr.flickStartT;
                if (tr.flickIsCombat && nearest != null)
                {
                    float netGoal = Mathf.Sqrt(Mathf.Pow(Mathf.DeltaAngle(tr.flickStartYaw, tr.flickGoalYaw), 2f) + Mathf.Pow(tr.flickGoalPitch - tr.flickStartPitch, 2f));
                    float netCur = Mathf.Sqrt(Mathf.Pow(Mathf.DeltaAngle(tr.flickStartYaw, curYaw), 2f) + Mathf.Pow(curPitch - tr.flickStartPitch, 2f));
                    if (netCur > netGoal) tr.flickMaxBeyondGoal = Mathf.Max(tr.flickMaxBeyondGoal, netCur - netGoal);
                }

                if (angVel < 35f || elapsed > 0.75f)
                {
                    tr.inFlick = false;
                    float netDisp = Mathf.Sqrt(Mathf.Pow(Mathf.DeltaAngle(tr.flickStartYaw, curYaw), 2f) + Mathf.Pow(curPitch - tr.flickStartPitch, 2f));
                    if (elapsed >= 0.04f && netDisp >= 5.5f)
                    {
                        float peakFrac = Mathf.Clamp01((tr.flickTimeAtPeak - tr.flickStartT) / Mathf.Max(0.01f, elapsed));
                        float straight = tr.flickPathDeg > 0.01f ? Mathf.Clamp01(netDisp / tr.flickPathDeg) : 1f;
                        tr.flickDurSum += elapsed;
                        tr.flickCount++;
                        _flicks.Add(new FlickRecord
                        {
                            actorId = tr.id,
                            isHuman = tr.isHuman,
                            archetype = tr.archetype,
                            difficulty = tr.difficulty,
                            startT = tr.flickStartT,
                            endT = matchTime,
                            durationS = elapsed,
                            startAngDeg = netDisp,
                            peakAngVel = tr.flickPeakAngVel,
                            timeToPeakFrac = peakFrac,
                            pathLenDeg = tr.flickPathDeg,
                            straightnessRatio = straight,
                            overshootDeg = tr.flickMaxBeyondGoal,
                            finalErrDeg = errTotal,
                            reactionLatencyS = tr.flickReactLat,
                            isCombat = tr.flickIsCombat
                        });
                    }
                }
            }
        }

        private void OnShotFired(ShotEvent e)
        {
            if (_isSaving || e.shooter == null || !_trackers.TryGetValue(e.shooter, out var tr)) return;
            float matchTime = _matchStartTime >= 0f ? Time.time - _matchStartTime : 0f;
            tr.shots++;
            if (tr.prevTargetVis && !tr.loggedFirstShotForAcquire && tr.targetAcquiredAt > 0f)
            {
                tr.loggedFirstShotForAcquire = true;
                float rLat = Time.time - tr.targetAcquiredAt;
                if (rLat >= 0.04f && rLat <= 2.5f) { tr.reactSum += rLat; tr.reactCount++; }
                AddEvent(matchTime, "FIRST_SHOT_ON_TARGET", tr.id, TargetIdOf(tr.prevTarget), e.origin, tr.soldier != null ? tr.soldier.Yaw : 0f, tr.soldier != null ? tr.soldier.Pitch : 0f, 0f, rLat, 0f, HitZone.UpperTorso, e.weaponName ?? "", "");
            }

            if (Time.time - tr.lastShotTime > 0.36f)
            {
                tr.burstId++;
                tr.burstShotIdx = 1;
                tr.burstStartTime = Time.time;
                tr.shotAccRecoilPitch = 0f;
                tr.shotAccRawPitch = 0f;
                tr.shotAccYawWeave = 0f;
            }
            else
            {
                tr.burstShotIdx++;
            }
            tr.lastShotTime = Time.time;

            float errY = 0f, errP = 0f;
            if (tr.prevTarget != null && tr.prevTarget.IsAlive && tr.soldier != null)
            {
                Vector2 g = LR2Math.DirectionToYawPitch(tr.prevTarget.ChestPosition - tr.soldier.AimOrigin);
                errY = Mathf.DeltaAngle(g.x, tr.soldier.Yaw);
                errP = g.y - tr.soldier.Pitch;
            }
            int idx = _recoils.Count;
            _recoils.Add(new RecoilShotRecord
            {
                actorId = tr.id,
                isHuman = tr.isHuman,
                archetype = tr.archetype,
                difficulty = tr.difficulty,
                burstId = tr.burstId,
                shotIdx = tr.burstShotIdx,
                weapon = e.weaponName ?? "",
                isAds = e.aiming,
                dtSinceBurstStart = Time.time - tr.burstStartTime,
                recoilKickPitch = tr.shotAccRecoilPitch,
                rawPulldownPitch = tr.shotAccRawPitch,
                netPitchDrift = tr.shotAccRecoilPitch + tr.shotAccRawPitch,
                yawWeave = tr.shotAccYawWeave,
                errYawDeg = errY,
                errPitchDeg = errP,
                hitTarget = false
            });
            tr.shotAccRecoilPitch = 0f;
            tr.shotAccRawPitch = 0f;
            tr.shotAccYawWeave = 0f;
            tr.lastRecoilRecordIndex = idx;
            tr.shotFiredAt = Time.time;

            AddEvent(matchTime, "SHOT", tr.id, TargetIdOf(tr.prevTarget), e.origin, tr.soldier != null ? tr.soldier.Yaw : 0f, tr.soldier != null ? tr.soldier.Pitch : 0f, 0f, Mathf.Sqrt(errY * errY + errP * errP), 0f, HitZone.UpperTorso, e.weaponName ?? "", e.suppressed ? "supp" : "loud");
        }

        private void OnHit(HitEvent e)
        {
            if (_isSaving || e.attacker == null || e.victim == e.attacker || !_trackers.TryGetValue(e.attacker, out var tr)) return;
            tr.hits++;
            if (e.zone == HitZone.Head) tr.headshots++;
            if (tr.lastRecoilRecordIndex >= 0 && tr.lastRecoilRecordIndex < _recoils.Count && Time.time - tr.shotFiredAt < 0.35f)
            {
                var rec = _recoils[tr.lastRecoilRecordIndex];
                rec.hitTarget = true;
                _recoils[tr.lastRecoilRecordIndex] = rec;
            }
            float matchTime = _matchStartTime >= 0f ? Time.time - _matchStartTime : 0f;
            float dist = Vector3.Distance(e.attacker.Position, e.victim != null ? e.victim.Position : e.point);
            AddEvent(matchTime, "HIT", tr.id, TargetIdOf(e.victim), e.point, tr.soldier != null ? tr.soldier.Yaw : 0f, tr.soldier != null ? tr.soldier.Pitch : 0f, dist, 0f, e.damage, e.zone, "", e.armorBroken ? "armor_break" : "");
        }

        private void OnKilled(KillEvent e)
        {
            if (_isSaving) return;
            float matchTime = _matchStartTime >= 0f ? Time.time - _matchStartTime : 0f;
            int aid = TargetIdOf(e.killer), vid = TargetIdOf(e.victim);
            AddEvent(matchTime, "KILL", aid, vid, e.victimPosition, 0f, 0f, e.distance, 0f, 0f, e.headshot ? HitZone.Head : HitZone.UpperTorso, e.weaponName ?? "", e.headshot ? "hs" : "");
        }

        private int TargetIdOf(Combatant c) => c != null && _trackers.TryGetValue(c, out var tr) ? tr.id : 0;

        private void AddEvent(float t, string type, int actor, int target, Vector3 pos, float yaw, float pitch, float dist, float err, float dmg, HitZone zone, string weapon, string extra)
        {
            _events.Add(new TelemetryEvent
            {
                matchTime = t,
                type = type,
                actorId = actor,
                targetId = target,
                pos = pos,
                yaw = yaw,
                pitch = pitch,
                distM = dist,
                errDeg = err,
                damage = dmg,
                zone = zone,
                weapon = weapon ?? "",
                extra = extra ?? ""
            });
        }

        // ------------------------------------------------------------------ end-of-round async save & saving screen
        private void StartSavingAsync()
        {
            _isSaving = true;
            _saveProgress = 0.05f;
            _saveStage = $"SNAPSHOT {_ticks.Count:N0} TICKS · PREPARING CSV BUFFERS...";
            _overlayShownAt = Time.unscaledTime;
            EnsureSavingOverlay();

            // Resolve session identity & InvasionFramerate-style Data/ output directory
            bool isSess = ExperimentSessionRunner.IsSessionActive;
            bool isCli = !string.IsNullOrEmpty(CliOutputRoot);
            int sessNumId = isSess ? ExperimentSessionRunner.SessionNumId : 0;
            string runId = isSess && !string.IsNullOrEmpty(ExperimentSessionRunner.RunId) ? ExperimentSessionRunner.RunId : ExperimentPaths.NewRunId();
            int matchIdx = isSess ? (ExperimentSessionRunner.CurrentQueueIndex + 1) : 1;
            var expSpec = ExperimentSessionRunner.CurrentMatchSpec;
            string condTag = expSpec != null ? ExperimentSessionRunner.ConditionTagOf(expSpec) : (SkirmishSetup.Active ? "skirmish" : "quickplay");
            string idTag = isCli ? $"cli_m{matchIdx:00}" : $"{sessNumId}_{runId}_m{matchIdx:00}";

            string outDir = ResolveMatchOutputDir(runId, out string sessionLogPath);
            var inv = CultureInfo.InvariantCulture;
            var ticksSnap = new List<TickSample>(_ticks);
            var eventsSnap = new List<TelemetryEvent>(_events);
            var flicksSnap = new List<FlickRecord>(_flicks);
            var recoilSnap = new List<RecoilShotRecord>(_recoils);
            var actorsSnap = new List<ActorTracker>(_trackerList);
            var frameSnap = new List<float>(_frameMs);

            var mm = MatchManager.Instance;
            string mapName = SceneManager.GetActiveScene().name;
            string modeName = mm != null && mm.Settings != null ? mm.Settings.mode.ToString() : "Unknown";
            string matchLabel = expSpec != null ? expSpec.label : (SkirmishSetup.Active ? "SKIRMISH" : "QUICKPLAY");
            string llmModeStr = LLMCommanderConfig.Mode().ToString();
            bool blind = SkirmishSetup.BlindMode;
            float durationS = _matchStartTime >= 0f ? Time.time - _matchStartTime : 0f;
            float minX = _mapMinX, maxX = _mapMaxX, minZ = _mapMinZ, maxZ = _mapMaxZ;

            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(outDir);
                    string pTicks = ExperimentPaths.UniqueFile(outDir, "Ticks", idTag, ".csv");
                    string pEvents = ExperimentPaths.UniqueFile(outDir, "Events", idTag, ".csv");
                    string pFlicks = ExperimentPaths.UniqueFile(outDir, "Flicks", idTag, ".csv");
                    string pRecoil = ExperimentPaths.UniqueFile(outDir, "Recoil", idTag, ".csv");
                    string pSum = ExperimentPaths.UniqueFile(outDir, "Summary", idTag, ".csv");
                    string pMeta = ExperimentPaths.UniqueFile(outDir, "Meta", idTag, ".json");

                    _saveStage = $"WRITING {Path.GetFileName(pTicks)} ({ticksSnap.Count:N0} ROWS)...";
                    _saveProgress = 0.18f;
                    WriteTicksCsv(pTicks, ticksSnap, sessNumId, runId, matchIdx, condTag, inv);

                    _saveStage = $"WRITING {Path.GetFileName(pEvents)} ({eventsSnap.Count:N0} EVENTS)...";
                    _saveProgress = 0.45f;
                    WriteEventsCsv(pEvents, eventsSnap, sessNumId, runId, matchIdx, condTag, inv);

                    _saveStage = $"WRITING {Path.GetFileName(pFlicks)} ({flicksSnap.Count:N0} FLICKS)...";
                    _saveProgress = 0.65f;
                    WriteFlicksCsv(pFlicks, flicksSnap, sessNumId, runId, matchIdx, condTag, inv);

                    _saveStage = $"WRITING {Path.GetFileName(pRecoil)} ({recoilSnap.Count:N0} SHOTS)...";
                    _saveProgress = 0.80f;
                    WriteRecoilsCsv(pRecoil, recoilSnap, sessNumId, runId, matchIdx, condTag, inv);

                    _saveStage = $"WRITING {Path.GetFileName(pSum)} & {Path.GetFileName(pMeta)}...";
                    _saveProgress = 0.94f;
                    ComputeFrameStats(frameSnap, out int fCount, out float fAvg, out float fP95, out float fP99, out int slow33);
                    WriteSummaryAndMeta(pSum, pMeta, actorsSnap, ticksSnap.Count, eventsSnap.Count, flicksSnap.Count, recoilSnap.Count,
                                        sessNumId, runId, matchIdx, condTag, mapName, modeName, matchLabel, llmModeStr, blind, durationS,
                                        minX, maxX, minZ, maxZ, fCount, fAvg, fP95, fP99, slow33, sessionLogPath, inv);

                    _saveProgress = 1f;
                    _saveStage = "COMPLETE";
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LR2] ExperimentTelemetryRecorder async save failed: {ex}");
                    _saveProgress = 1f;
                }
                finally
                {
                    // Ensure overlay remains visible at least 0.45s so user clearly sees confirmation without flicker
                    while (DateTime.UtcNow.Ticks > 0 && _saveProgress < 1f) { }
                    MarkSaveFinishedOnThread();
                }
            });
        }

        private void MarkSaveFinishedOnThread()
        {
            StartCoroutineOnMain();
        }

        private void StartCoroutineOnMain()
        {
            // Poll in Update instead since Task.Run executes off main thread
            _asyncDoneFlag = true;
        }
        private volatile bool _asyncDoneFlag;

        private void Update()
        {
            if (_isSaving && _asyncDoneFlag && Time.unscaledTime - _overlayShownAt >= 0.55f)
            {
                _isSaving = false;
                if (_overlayGo != null) _overlayGo.SetActive(false);
            }
        }

        private string ResolveMatchOutputDir(string runId, out string sessionLogPath)
        {
            sessionLogPath = "";
            if (!string.IsNullOrEmpty(CliOutputRoot))
            {
                string parent = Path.GetDirectoryName(CliOutputRoot.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(parent))
                    sessionLogPath = ExperimentPaths.EnsureSessionLogFile(parent, "cli");
                return CliOutputRoot;
            }
            if (ExperimentSessionRunner.IsSessionActive && !string.IsNullOrEmpty(ExperimentSessionRunner.SessionDirPath))
            {
                int num = ExperimentSessionRunner.CurrentQueueIndex + 1;
                string tag = ExperimentSessionRunner.ConditionTagOf(ExperimentSessionRunner.CurrentMatchSpec);
                sessionLogPath = ExperimentSessionRunner.SessionLogPath;
                return Path.Combine(ExperimentSessionRunner.SessionDirPath, $"match_{num:00}_{tag}");
            }
            string adHocDir = ExperimentPaths.AdHocLogDir(runId);
            ExperimentPaths.WriteSessionInfoCsv(adHocDir, 0, runId, "");
            sessionLogPath = ExperimentPaths.EnsureSessionLogFile(adHocDir, $"0_{runId}");
            return Path.Combine(adHocDir, "match_01_adhoc");
        }

        private static void ComputeFrameStats(List<float> ms, out int count, out float avgMs, out float p95Ms, out float p99Ms, out int slow33)
        {
            count = ms != null ? ms.Count : 0;
            if (count == 0) { avgMs = p95Ms = p99Ms = 0f; slow33 = 0; return; }
            double sum = 0.0;
            slow33 = 0;
            for (int i = 0; i < count; i++)
            {
                float v = ms[i];
                sum += v;
                if (v > 33.333f) slow33++;
            }
            avgMs = (float)(sum / count);
            ms.Sort();
            p95Ms = ms[Mathf.Clamp((int)(count * 0.95f), 0, count - 1)];
            p99Ms = ms[Mathf.Clamp((int)(count * 0.99f), 0, count - 1)];
        }

        private static void WriteTicksCsv(string path, List<TickSample> rows, int sessId, string runId, int matchIdx, string condTag, CultureInfo inv)
        {
            string sCond = SafeCsv(condTag);
            string sRun = SafeCsv(runId);
            using var w = new StreamWriter(path, false, new UTF8Encoding(false), 65536);
            w.WriteLine("session_id,run_id,match_index,condition_tag,match_time,participant_id,display_name,is_human,archetype,difficulty,team,alive,pos_x,pos_y,pos_z,vel_x,vel_y,vel_z,speed,yaw,pitch,raw_look_dx,raw_look_dy,recoil_dx,recoil_dy,ang_vel_yaw,ang_vel_pitch,move_x,move_y,is_sprinting,is_crouching,is_proning,is_aiming,aim_blend,lean,is_firing,is_reloading,weapon,ammo_mag,hp,armor,has_visible_enemy,nearest_enemy_dist,err_yaw_deg,err_pitch_deg,err_total_deg,enemy_vel_lat,near_cover_dist,bot_goal,bot_order,is_flicking");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                w.WriteLine(string.Format(inv,
                    "{0},{1},{2},{3},{4:0.000},{5},{6},{7},{8},{9},{10},{11},{12:0.000},{13:0.000},{14:0.000},{15:0.000},{16:0.000},{17:0.000},{18:0.000},{19:0.00},{20:0.00},{21:0.0000},{22:0.0000},{23:0.0000},{24:0.0000},{25:0.00},{26:0.00},{27:0.000},{28:0.000},{29},{30},{31},{32},{33:0.000},{34:0.00},{35},{36},{37},{38},{39:0.0},{40:0.0},{41},{42:0.00},{43:0.00},{44:0.00},{45:0.00},{46:0.00},{47:0.00},{48},{49},{50}",
                    sessId, sRun, matchIdx, sCond,
                    r.matchTime, r.id, SafeCsv(r.name), r.isHuman ? 1 : 0, r.archetype, r.difficulty, r.team, r.alive ? 1 : 0,
                    r.pos.x, r.pos.y, r.pos.z, r.vel.x, r.vel.y, r.vel.z, r.speed, r.yaw, r.pitch,
                    r.rawLookDx, r.rawLookDy, r.recoilDx, r.recoilDy, r.angVelYaw, r.angVelPitch,
                    r.moveInput.x, r.moveInput.y, r.sprinting ? 1 : 0, r.crouching ? 1 : 0, r.proning ? 1 : 0,
                    r.aiming ? 1 : 0, r.aimBlend, r.lean, r.firing ? 1 : 0, r.reloading ? 1 : 0, SafeCsv(r.weapon),
                    r.ammoMag, r.hp, r.armor, r.hasVisibleEnemy ? 1 : 0, r.nearestEnemyDist, r.errYawDeg, r.errPitchDeg,
                    r.errTotalDeg, r.enemyVelLat, r.nearCoverDist, r.botGoal, r.botOrder, r.isFlicking ? 1 : 0));
            }
        }

        private static void WriteEventsCsv(string path, List<TelemetryEvent> rows, int sessId, string runId, int matchIdx, string condTag, CultureInfo inv)
        {
            string sCond = SafeCsv(condTag);
            string sRun = SafeCsv(runId);
            using var w = new StreamWriter(path, false, new UTF8Encoding(false), 32768);
            w.WriteLine("session_id,run_id,match_index,condition_tag,match_time,event_type,actor_id,target_id,pos_x,pos_y,pos_z,yaw,pitch,dist_m,err_deg,damage,zone,weapon,extra");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                w.WriteLine(string.Format(inv,
                    "{0},{1},{2},{3},{4:0.000},{5},{6},{7},{8:0.000},{9:0.000},{10:0.000},{11:0.00},{12:0.00},{13:0.00},{14:0.00},{15:0.0},{16},{17},{18}",
                    sessId, sRun, matchIdx, sCond,
                    r.matchTime, r.type, r.actorId, r.targetId, r.pos.x, r.pos.y, r.pos.z, r.yaw, r.pitch, r.distM, r.errDeg, r.damage, r.zone, SafeCsv(r.weapon), SafeCsv(r.extra)));
            }
        }

        private static void WriteFlicksCsv(string path, List<FlickRecord> rows, int sessId, string runId, int matchIdx, string condTag, CultureInfo inv)
        {
            string sCond = SafeCsv(condTag);
            string sRun = SafeCsv(runId);
            using var w = new StreamWriter(path, false, new UTF8Encoding(false), 32768);
            w.WriteLine("session_id,run_id,match_index,condition_tag,actor_id,is_human,archetype,difficulty,start_t,end_t,duration_s,start_ang_deg,peak_ang_vel,time_to_peak_frac,path_len_deg,straightness_ratio,overshoot_deg,final_err_deg,reaction_latency_s,is_combat");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                w.WriteLine(string.Format(inv,
                    "{0},{1},{2},{3},{4},{5},{6},{7},{8:0.000},{9:0.000},{10:0.000},{11:0.00},{12:0.00},{13:0.000},{14:0.00},{15:0.000},{16:0.00},{17:0.00},{18:0.000},{19}",
                    sessId, sRun, matchIdx, sCond,
                    r.actorId, r.isHuman ? 1 : 0, r.archetype, r.difficulty, r.startT, r.endT, r.durationS, r.startAngDeg,
                    r.peakAngVel, r.timeToPeakFrac, r.pathLenDeg, r.straightnessRatio, r.overshootDeg, r.finalErrDeg, r.reactionLatencyS, r.isCombat ? 1 : 0));
            }
        }

        private static void WriteRecoilsCsv(string path, List<RecoilShotRecord> rows, int sessId, string runId, int matchIdx, string condTag, CultureInfo inv)
        {
            string sCond = SafeCsv(condTag);
            string sRun = SafeCsv(runId);
            using var w = new StreamWriter(path, false, new UTF8Encoding(false), 32768);
            w.WriteLine("session_id,run_id,match_index,condition_tag,actor_id,is_human,archetype,difficulty,burst_id,shot_idx,weapon,is_ads,dt_since_burst_start,recoil_kick_pitch,raw_pulldown_pitch,net_pitch_drift,yaw_weave,err_yaw_deg,err_pitch_deg,hit_target");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                w.WriteLine(string.Format(inv,
                    "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12:0.000},{13:0.0000},{14:0.0000},{15:0.0000},{16:0.0000},{17:0.00},{18:0.00},{19}",
                    sessId, sRun, matchIdx, sCond,
                    r.actorId, r.isHuman ? 1 : 0, r.archetype, r.difficulty, r.burstId, r.shotIdx, SafeCsv(r.weapon), r.isAds ? 1 : 0,
                    r.dtSinceBurstStart, r.recoilKickPitch, r.rawPulldownPitch, r.netPitchDrift, r.yawWeave, r.errYawDeg, r.errPitchDeg, r.hitTarget ? 1 : 0));
            }
        }

        private void WriteSummaryAndMeta(string summaryPath, string metaPath, List<ActorTracker> actors,
                                         int nTicks, int nEvents, int nFlicks, int nRecoils,
                                         int sessId, string runId, int matchIdx, string condTag,
                                         string mapName, string modeName, string matchLabel, string llmMode, bool blind, float durationS,
                                         float minX, float maxX, float minZ, float maxZ,
                                         int fCount, float fAvg, float fP95, float fP99, int slow33, string sessionLogPath,
                                         CultureInfo inv)
        {
            int alphaKills = 0, bravoKills = 0, hKills = 0, hDeaths = 0, hShots = 0, hHits = 0, hHs = 0;
            string sCond = SafeCsv(condTag);
            string sRun = SafeCsv(runId);
            using (var w = new StreamWriter(summaryPath, false, new UTF8Encoding(false)))
            {
                w.WriteLine("session_id,run_id,match_index,condition_tag,participant_id,display_name,is_human,archetype,difficulty,team,kills,deaths,assists,score,kd,shots,hits,acc_pct,headshots,hs_pct,avg_flick_s,avg_react_s,strafe_flips,crouch_duty,ads_duty,sprint_duty");
                foreach (var tr in actors)
                {
                    if (tr.combatant == null) continue;
                    var st = tr.combatant.stats;
                    if (tr.team == TeamId.Alpha) alphaKills += st.kills;
                    else if (tr.team == TeamId.Bravo) bravoKills += st.kills;
                    if (tr.isHuman) { hKills += st.kills; hDeaths += st.deaths; hShots += tr.shots; hHits += tr.hits; hHs += tr.headshots; }

                    float kd = (float)st.kills / Mathf.Max(1, st.deaths);
                    float acc = tr.shots > 0 ? 100f * tr.hits / tr.shots : 0f;
                    float hs = tr.hits > 0 ? 100f * tr.headshots / tr.hits : 0f;
                    float avgFlick = tr.flickCount > 0 ? tr.flickDurSum / tr.flickCount : 0f;
                    float avgReact = tr.reactCount > 0 ? tr.reactSum / tr.reactCount : 0f;
                    float n = Mathf.Max(1, tr.sampleCount);
                    w.WriteLine(string.Format(inv,
                        "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14:0.00},{15},{16},{17:0.00},{18},{19:0.00},{20:0.000},{21:0.000},{22},{23:0.000},{24:0.000},{25:0.000}",
                        sessId, sRun, matchIdx, sCond,
                        tr.id, SafeCsv(tr.displayName), tr.isHuman ? 1 : 0, tr.archetype, tr.difficulty, tr.team,
                        st.kills, st.deaths, st.assists, st.score, kd, tr.shots, tr.hits, acc, tr.headshots, hs,
                        avgFlick, avgReact, tr.strafeFlips, tr.crouchSamples / n, tr.adsSamples / n, tr.sprintSamples / n));
                }
            }

            string meta = "{\n" +
                $"  \"schemaVersion\": 2,\n" +
                $"  \"sessionId\": {sessId},\n" +
                $"  \"runId\": \"{SafeJson(runId)}\",\n" +
                $"  \"matchIndex\": {matchIdx},\n" +
                $"  \"conditionTag\": \"{SafeJson(condTag)}\",\n" +
                $"  \"scene\": \"{mapName}\",\n" +
                $"  \"mode\": \"{modeName}\",\n" +
                $"  \"label\": \"{SafeJson(matchLabel)}\",\n" +
                $"  \"llmMode\": \"{llmMode}\",\n" +
                $"  \"blind\": {(blind ? "true" : "false")},\n" +
                $"  \"durationSeconds\": {durationS.ToString("0.00", inv)},\n" +
                $"  \"sampleHz\": {_sampleHz},\n" +
                $"  \"bounds\": {{\"minX\": {minX.ToString("0.0", inv)}, \"maxX\": {maxX.ToString("0.0", inv)}, \"minZ\": {minZ.ToString("0.0", inv)}, \"maxZ\": {maxZ.ToString("0.0", inv)}}},\n" +
                $"  \"frameDelivery\": {{\"frameCount\": {fCount}, \"avgFrameMs\": {fAvg.ToString("0.000", inv)}, \"p95FrameMs\": {fP95.ToString("0.000", inv)}, \"p99FrameMs\": {fP99.ToString("0.000", inv)}, \"slowFrames33ms\": {slow33}}},\n" +
                $"  \"counts\": {{\"ticks\": {nTicks}, \"events\": {nEvents}, \"flicks\": {nFlicks}, \"recoilShots\": {nRecoils}}}\n" +
                "}";
            File.WriteAllText(metaPath, meta, new UTF8Encoding(false));

            float hAccPct = hShots > 0 ? 100f * hHits / hShots : 0f;
            float hHsPct = hHits > 0 ? 100f * hHs / hHits : 0f;
            ExperimentPaths.AppendSessionLogRow(sessionLogPath, sessId, runId, matchIdx, condTag, matchLabel, mapName, modeName,
                blind, durationS, alphaKills, bravoKills, hKills, hDeaths, hAccPct, hHsPct, fCount, fAvg, fP95, fP99, slow33);
        }

        private void EnsureSavingOverlay()
        {
            if (_overlayGo != null) { _overlayGo.SetActive(true); return; }
            var theme = Resources.Load<UITheme>("LR2/UITheme");
            _overlayGo = new GameObject("LR2 Saving Telemetry Overlay", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = _overlayGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 950;
            var scaler = _overlayGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var bgGo = new GameObject("BG", typeof(RectTransform), typeof(Image));
            bgGo.transform.SetParent(_overlayGo.transform, false);
            var bgRt = (RectTransform)bgGo.transform;
            bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one; bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;
            bgGo.GetComponent<Image>().color = new Color(0.025f, 0.03f, 0.04f, 0.96f);

            var titleGo = new GameObject("Title", typeof(RectTransform));
            titleGo.transform.SetParent(_overlayGo.transform, false);
            var title = titleGo.AddComponent<TextMeshProUGUI>();
            if (theme != null && theme.headingFont != null) title.font = theme.headingFont;
            title.text = "SAVING EXPERIMENT TELEMETRY";
            title.fontSize = 48f;
            title.color = Color.white;
            title.characterSpacing = 4f;
            title.alignment = TextAlignmentOptions.Center;
            var trt = title.rectTransform;
            trt.anchorMin = new Vector2(0.15f, 0.54f); trt.anchorMax = new Vector2(0.85f, 0.64f); trt.offsetMin = trt.offsetMax = Vector2.zero;

            var barBg = new GameObject("BarBg", typeof(RectTransform), typeof(Image));
            barBg.transform.SetParent(_overlayGo.transform, false);
            var bbrt = (RectTransform)barBg.transform;
            bbrt.anchorMin = new Vector2(0.32f, 0.485f); bbrt.anchorMax = new Vector2(0.68f, 0.505f); bbrt.offsetMin = bbrt.offsetMax = Vector2.zero;
            barBg.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);

            var barFill = new GameObject("BarFill", typeof(RectTransform), typeof(Image));
            barFill.transform.SetParent(barBg.transform, false);
            _overlayBarFill = barFill.GetComponent<Image>();
            _overlayBarFill.color = theme != null ? theme.accent : new Color(1f, 0.71f, 0.28f);
            _overlayBarFill.type = Image.Type.Filled;
            _overlayBarFill.fillMethod = Image.FillMethod.Horizontal;
            _overlayBarFill.fillAmount = 0.05f;
            var bfrt = _overlayBarFill.rectTransform;
            bfrt.anchorMin = Vector2.zero; bfrt.anchorMax = Vector2.one; bfrt.offsetMin = bfrt.offsetMax = Vector2.zero;

            var stGo = new GameObject("Stage", typeof(RectTransform));
            stGo.transform.SetParent(_overlayGo.transform, false);
            _overlayStatusText = stGo.AddComponent<TextMeshProUGUI>();
            if (theme != null && theme.bodyFont != null) _overlayStatusText.font = theme.bodyFont;
            _overlayStatusText.fontSize = 20f;
            _overlayStatusText.color = new Color(1f, 1f, 1f, 0.78f);
            _overlayStatusText.alignment = TextAlignmentOptions.Center;
            var srt = _overlayStatusText.rectTransform;
            srt.anchorMin = new Vector2(0.15f, 0.40f); srt.anchorMax = new Vector2(0.85f, 0.47f); srt.offsetMin = srt.offsetMax = Vector2.zero;

            var subGo = new GameObject("Sub", typeof(RectTransform));
            subGo.transform.SetParent(_overlayGo.transform, false);
            _overlaySubText = subGo.AddComponent<TextMeshProUGUI>();
            if (theme != null && theme.bodyFont != null) _overlaySubText.font = theme.bodyFont;
            _overlaySubText.text = "WRITING BACKGROUND CSV FILES TO PREVENT IN-MATCH STUTTER";
            _overlaySubText.fontSize = 15f;
            _overlaySubText.color = new Color(1f, 1f, 1f, 0.42f);
            _overlaySubText.alignment = TextAlignmentOptions.Center;
            var surt = _overlaySubText.rectTransform;
            surt.anchorMin = new Vector2(0.15f, 0.34f); surt.anchorMax = new Vector2(0.85f, 0.39f); surt.offsetMin = surt.offsetMax = Vector2.zero;
        }

        private void UpdateOverlayVisuals()
        {
            if (_overlayBarFill != null) _overlayBarFill.fillAmount = Mathf.Clamp01(_saveProgress);
            if (_overlayStatusText != null) _overlayStatusText.text = _saveStage;
        }

        private static string SafeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0) return "\"" + s.Replace('"', '\'') + "\"";
            return s;
        }

        private static string SafeJson(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "/").Replace("\"", "'");
    }
}
