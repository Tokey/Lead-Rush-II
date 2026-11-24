using System.Collections.Generic;
using LR2.AI;
using LR2.Audio;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LR2.Game
{
    /// <summary>
    /// Runs a match: spawns the player + bots (+ seeker drones), countdown, scoring, medals, respawns with smart spawn
    /// selection, time/score limits, sudden death and the end screen state.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class MatchManager : MonoBehaviour
    {
        public static MatchManager Instance { get; private set; }

        [Header("Setup")]
        [SerializeField] private MatchSettings defaultSettings;
        [SerializeField] private GameObject playerPrefab;
        [SerializeField] private GameObject botPrefab;
        [SerializeField] private GameObject orbPrefab;
        [Tooltip("Index = LR2Settings.BotDifficulty (Recruit, Regular, Hardened, Veteran).")]
        [SerializeField] private BotDifficulty[] difficulties;

        [Header("Scoring")]
        [SerializeField] private int killPoints = 100;
        [SerializeField] private int headshotBonus = 50;
        [SerializeField] private int assistPoints = 25;
        [SerializeField] private int dronePoints = 50;
        [SerializeField] private float assistWindow = 6f;
        [SerializeField] private float multiKillWindow = 4f;
        [SerializeField] private float longshotDistance = 30f;

        [Header("Flow")]
        [SerializeField] private float suddenDeathSeconds = 60f;
        [SerializeField] private string[] botCallsigns =
        {
            "xX_NoScopeGrandma_Xx", "TaxFormTerror", "MomPulledThePlug", "WiFi_On_A_Potato",
            "CrouchSpammer99", "KneeSlapperKing", "ForgotToReload", "200PingWarrior",
            "TouchGrass_Later", "AimAssist_Enjoyer", "Campfire_CEO", "ControllerDisconnected",
            "BudgetActionHero", "LagSpike_Larry", "TacticalSnackBreak", "SentToLobby",
            "HipfireHero", "CheckMyKillcam", "FreeKD_Delivery", "SweatyPalms_TTV",
            "HDMI_Unplugged", "FriendlyFire_Enjoyer", "ProneInA_Bush", "OneMoreGameMom",
            "SkillIssue_Official", "MousepadRanOut", "AltTabTactician", "CheesyDustFingers",
            "LaggingOnPurpose", "MyCatWalkedOnWASD", "ReloadAfter1Bullet", "LootGoblin_77"
        };

        public MatchSettings Settings { get; private set; }
        public MatchState State { get; private set; } = MatchState.None;
        public float TimeRemaining { get; private set; }
        public float CountdownRemaining { get; private set; }
        public Combatant LocalPlayer { get; private set; }
        public IReadOnlyList<Combatant> Participants => _participants;
        public int ScoreLimit => Settings != null ? Settings.scoreLimit : 15;
        public bool IsTeamMode => SkirmishSetup.Active ? SkirmishSetup.IsTeamGame : Settings != null && Settings.IsTeamMode;
        public bool IsSuddenDeath { get; private set; }
        public Combatant WinnerCombatant { get; private set; }
        public TeamId WinnerTeam { get; private set; }
        public bool LocalPlayerWon { get; private set; }
        public bool IsDraw { get; private set; }

        private readonly List<Combatant> _participants = new List<Combatant>();
        private readonly Dictionary<Combatant, float> _respawnAt = new Dictionary<Combatant, float>();
        private readonly Dictionary<Combatant, List<(Combatant attacker, float time)>> _damageLog = new Dictionary<Combatant, List<(Combatant, float)>>();
        private readonly Dictionary<Combatant, float> _lastKillTime = new Dictionary<Combatant, float>();
        private readonly Dictionary<Combatant, int> _multiKill = new Dictionary<Combatant, int>();
        private readonly Dictionary<Combatant, int> _deathStreak = new Dictionary<Combatant, int>();
        private readonly Dictionary<Combatant, Combatant> _lastKiller = new Dictionary<Combatant, Combatant>();
        private readonly Dictionary<TeamId, int> _teamScore = new Dictionary<TeamId, int>();
        private readonly HashSet<TeamId> _teamsInPlay = new HashSet<TeamId>();
        private readonly List<OrbDrone> _orbs = new List<OrbDrone>();
        private bool _firstBlood;
        private float _nextOrbSpawn;
        private float _lastTick = -1f;
        private int _nameIndex;
        private int _botsThisMatch = 1; // how many bots get names this match (friends only all-in when they all fit)
        private string[] _shuffledCallsigns;

        // ------------------------------------------------------------------ lifecycle
        private void Awake()
        {
            Instance = this;
            GameEvents.Killed += OnKilled;
            GameEvents.Hit += OnHit;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            GameEvents.Killed -= OnKilled;
            GameEvents.Hit -= OnHit;
        }

        private void Start()
        {
            Settings = LR2Session.PendingSettings != null ? LR2Session.PendingSettings
                : LR2Session.ActiveSettings != null ? LR2Session.ActiveSettings : defaultSettings;
            if (Settings == null) Settings = ScriptableObject.CreateInstance<MatchSettings>();
            LR2Session.ActiveSettings = Settings;
            LR2Session.PendingSettings = null;
            LR2Settings.Apply();
            Time.timeScale = 1f;
            if (LR2.World.KillcamDirector.Instance == null)
                new GameObject("[LR2 Killcam]").AddComponent<LR2.World.KillcamDirector>();
            LR2.World.MapAmbientFx.Ensure();
            LR2.World.SkyCloudMotion.Ensure();
            LR2.World.ExperimentTelemetryRecorder.EnsureForMatch();

            _teamScore[TeamId.Alpha] = 0;
            _teamScore[TeamId.Bravo] = 0;
            _teamScore[TeamId.Charlie] = 0;
            _teamScore[TeamId.Delta] = 0;
            SpawnParticipants();
            if (Settings.IsFlagMode && FlagZoneController.Instance == null)
                new GameObject("[LR2 FlagZone]").AddComponent<FlagZoneController>();
            TimeRemaining = Settings.timeLimitSeconds;
            CountdownRemaining = Settings.countdownSeconds;
            SetControls(false);
            SetState(Settings.countdownSeconds > 0f ? MatchState.Countdown : MatchState.Playing);
            if (State == MatchState.Playing) SetControls(true);
            _nextOrbSpawn = Time.time + Settings.countdownSeconds + Settings.orbSpawnInterval * 0.5f;
        }

        private void Update()
        {
            switch (State)
            {
                case MatchState.Countdown:
                    CountdownRemaining -= Time.deltaTime;
                    int tick = Mathf.CeilToInt(CountdownRemaining);
                    if (tick != Mathf.CeilToInt(_lastTick) && tick > 0 && tick <= 3)
                    {
                        var s = LR2CommonSounds.Instance;
                        if (s != null) LR2Audio.Play2D(s.countdownTick);
                    }
                    _lastTick = CountdownRemaining;
                    if (CountdownRemaining <= 0f)
                    {
                        CountdownRemaining = 0f;
                        SetControls(true);
                        SetState(MatchState.Playing);
                        var s = LR2CommonSounds.Instance;
                        if (s != null) LR2Audio.Play2D(s.matchStart);
                        LR2.World.WeatherSystem.Instance?.TriggerThunderRain(0.90f, 38f);
                    }
                    break;

                case MatchState.Playing:
                    TimeRemaining -= Time.deltaTime;
                    ProcessRespawns();
                    UpdateOrbs();
                    if (TimeRemaining <= 0f) OnTimeUp();
                    break;
            }
        }

        // ------------------------------------------------------------------ spawning
        private void SpawnParticipants()
        {
            _shuffledCallsigns = null;
            _nameIndex = 0;
            if (playerPrefab == null || botPrefab == null)
            {
                Debug.LogError("[LR2] MatchManager is missing playerPrefab/botPrefab. Run LR2 > Build Everything.");
                CreateFallbackCamera();
                return;
            }

            if (SkirmishSetup.Active) { _botsThisMatch = SkirmishSetup.Bots.Count; SpawnSkirmish(); return; }
            _botsThisMatch = Settings.mode == GameModeType.Duel ? 1 : Mathf.Max(1, Settings.enemyBots) + Mathf.Max(0, Settings.friendlyBots);

            BotTeamAlignment align = Settings.EffectiveBotTeamAlignment;
            bool team = align != BotTeamAlignment.FreeForAll && Settings.mode != GameModeType.Duel;
            TeamId playerTeam = team ? TeamId.Alpha : TeamId.None;
            LocalPlayer = Spawn(playerPrefab, playerTeam, LR2Settings.PlayerName, true);

            int enemies = Settings.mode == GameModeType.Duel ? 1 : Mathf.Max(1, Settings.enemyBots);
            if (align == BotTeamAlignment.FreeForAll)
            {
                for (int i = 0; i < enemies; i++) Spawn(botPrefab, TeamId.None, NextName(), false);
            }
            else if (align == BotTeamAlignment.AllBotsPlayerTeam)
            {
                int allies = Mathf.Max(1, Settings.friendlyBots + enemies);
                for (int i = 0; i < allies; i++)
                {
                    var bot = Spawn(botPrefab, TeamId.Alpha, NextName(), false);
                    var bc = bot != null ? bot.GetComponent<BotController>() : null;
                    if (bc != null && LocalPlayer != null) bc.SetSquadLeader(LocalPlayer);
                }
            }
            else if (align == BotTeamAlignment.BotsSameTeamVsPlayer)
            {
                for (int i = 0; i < enemies; i++) Spawn(botPrefab, TeamId.Bravo, NextName(), false);
            }
            else if (align == BotTeamAlignment.HalfAndHalf)
            {
                MatchSettings.SplitHalfAndHalf(enemies, out int halfAllies, out int halfEnemies);
                for (int i = 0; i < halfAllies; i++)
                {
                    var bot = Spawn(botPrefab, TeamId.Alpha, NextName(), false);
                    var bc = bot != null ? bot.GetComponent<BotController>() : null;
                    if (bc != null && LocalPlayer != null) bc.SetSquadLeader(LocalPlayer);
                }
                for (int i = 0; i < halfEnemies; i++) Spawn(botPrefab, TeamId.Bravo, NextName(), false);
            }
            else
            {
                int allies = Settings.IsFlagMode ? Mathf.Max(1, Settings.friendlyBots) : Settings.friendlyBots;
                for (int i = 0; i < allies; i++)
                {
                    var bot = Spawn(botPrefab, TeamId.Alpha, NextName(), false);
                    var bc = bot != null ? bot.GetComponent<BotController>() : null;
                    if (bc != null && LocalPlayer != null) bc.SetSquadLeader(LocalPlayer);
                }
                for (int i = 0; i < enemies; i++) Spawn(botPrefab, team ? TeamId.Bravo : TeamId.None, NextName(), false);
            }
        }

        private static readonly string[] DifficultyTags = { "REC", "REG", "HRD", "VET", "ADP" };
        private static readonly string[] ArchetypeTags = { "", "DUMB", "LLM", "FIT" };

        /// <summary>
        /// Skirmish &amp; Experiments lobby spawner: every slot has its own archetype, difficulty and team; AI vs AI spawns no
        /// player and hands the screen to <see cref="SpectatorCamera"/>. Blind Mode hides bot tags from the HUD/scoreboard.
        /// </summary>
        private void SpawnSkirmish()
        {
            bool teams = SkirmishSetup.IsTeamGame;
            bool mixed = false;
            for (int i = 1; i < SkirmishSetup.Bots.Count; i++)
                if (SkirmishSetup.Bots[i].difficulty != SkirmishSetup.Bots[0].difficulty ||
                    SkirmishSetup.Bots[i].archetype != SkirmishSetup.Bots[0].archetype) mixed = true;
            bool tag = !SkirmishSetup.BlindMode && (mixed || SkirmishSetup.PlayerSpectates);

            if (!SkirmishSetup.PlayerSpectates)
                LocalPlayer = Spawn(playerPrefab, teams ? TeamId.Alpha : TeamId.None, LR2Settings.PlayerName, true); // B116: you are always Alpha (blue)
            foreach (var slot in SkirmishSetup.Bots)
            {
                TeamId t = teams ? slot.team : TeamId.None;
                int d = Mathf.Clamp(slot.difficulty, 0, DifficultyTags.Length - 1);
                int a = Mathf.Clamp((int)slot.archetype, 0, ArchetypeTags.Length - 1);
                string aTag = !SkirmishSetup.BlindMode && !string.IsNullOrEmpty(ArchetypeTags[a]) ? ArchetypeTags[a] : "";
                string dTag = tag ? DifficultyTags[d] : "";
                string suffix = (!string.IsNullOrEmpty(aTag) && !string.IsNullOrEmpty(dTag)) ? $" [{aTag}·{dTag}]"
                              : !string.IsNullOrEmpty(aTag) ? $" [{aTag}]"
                              : !string.IsNullOrEmpty(dTag) ? $" [{dTag}]"
                              : "";
                string name = NextName() + suffix;
                var bot = Spawn(botPrefab, t, name, false, d, slot.archetype);
                var bc = bot != null ? bot.GetComponent<BotController>() : null;
                if (bc != null && LocalPlayer != null && t != TeamId.None && t == LocalPlayer.Team) bc.SetSquadLeader(LocalPlayer);
            }
            if (SkirmishSetup.PlayerSpectates) SpectatorCamera.Ensure();
        }

        /// <summary>Overview camera so the screen is never black if the soldier prefabs are missing.</summary>
        private void CreateFallbackCamera()
        {
            if (Camera.main != null) return;
            var go = new GameObject("FallbackCamera");
            go.tag = "MainCamera";
            go.transform.SetPositionAndRotation(new Vector3(-30f, 22f, -30f), Quaternion.Euler(30f, 45f, 0f));
            go.AddComponent<Camera>().fieldOfView = 60f;
            go.AddComponent<AudioListener>();
        }

        private string NextName()
        {
            if (_shuffledCallsigns == null || _shuffledCallsigns.Length == 0)
            {
                string[] fallback =
                {
                    "xX_NoScopeGrandma_Xx", "TaxFormTerror", "MomPulledThePlug", "WiFi_On_A_Potato",
                    "CrouchSpammer99", "KneeSlapperKing", "ForgotToReload", "200PingWarrior",
                    "TouchGrass_Later", "AimAssist_Enjoyer", "Campfire_CEO", "ControllerDisconnected",
                    "BudgetActionHero", "LagSpike_Larry", "TacticalSnackBreak", "SentToLobby",
                    "HipfireHero", "CheckMyKillcam", "FreeKD_Delivery", "SweatyPalms_TTV",
                    "HDMI_Unplugged", "FriendlyFire_Enjoyer", "ProneInA_Bush", "OneMoreGameMom",
                    "SkillIssue_Official", "MousepadRanOut", "AltTabTactician", "CheesyDustFingers",
                    "LaggingOnPurpose", "MyCatWalkedOnWASD", "ReloadAfter1Bullet", "LootGoblin_77"
                };
                // the code list is the one source of names (the scenes carry an older serialized copy with edgier tags)
                string[] src = fallback;
                // Weighted shuffle without repeats (Efraimidis-Spirakis keys u^(1/w)): featured tags come up far more often,
                // every old tag stays in the pool, and no two bots share a name in one match.
                var pool = new List<(string name, float key)>(src.Length + FeaturedCallsigns.Length + FriendCallsigns.Length);
                var seen = new HashSet<string>();
                if (!string.IsNullOrWhiteSpace(LR2Settings.PlayerName)) seen.Add(LR2Settings.PlayerName); // never a bot with your name
                // user 11-19: friends all play when every one of them fits (any team), dealt first; in a 1v1 or a small match
                // they only turn up now and then (weight 0.35 against 1 for an ordinary tag)
                bool allFriends = _botsThisMatch >= FriendCallsigns.Length;
                foreach (string n in FriendCallsigns)
                    if (seen.Add(n)) pool.Add((n, allFriends ? 1f + Random.value : Mathf.Pow(Mathf.Max(1e-6f, Random.value), 1f / 0.35f)));
                foreach (string n in FeaturedCallsigns)
                    if (seen.Add(n)) pool.Add((n, Mathf.Pow(Mathf.Max(1e-6f, Random.value), 1f / FeaturedWeight)));
                foreach (string n in src)
                    if (!string.IsNullOrWhiteSpace(n) && seen.Add(n)) pool.Add((n, Mathf.Max(1e-6f, Random.value)));
                pool.Sort((a, b) => b.key.CompareTo(a.key));
                _shuffledCallsigns = new string[pool.Count];
                for (int i = 0; i < pool.Count; i++) _shuffledCallsigns[i] = pool[i].name;
            }
            return _shuffledCallsigns[_nameIndex++ % _shuffledCallsigns.Length];
        }

        // User 11-19: the developer's friends play under friendly, funny tags built on their names, and are dealt first
        // (in random order), so with six or more bots every one of them is in the match.
        private static readonly string[] FriendCallsigns =
        {
            "EnchantedP0tato", "AshleshaNeverMisses", "Xiaokun_ClutchKing", "ClaypoolPartyTime", "JackAttack", "HolyTokeyMoley"
        };
        // User 11-18 14:10: favourite tags, weighted 4x in the shuffle after the friends.
        private static readonly string[] FeaturedCallsigns =
        {
            "xX_NoScopeGrandma_Xx", "MyCatWalkedOnWASD"
        };
        private const float FeaturedWeight = 4f;

        private Combatant Spawn(GameObject prefab, TeamId team, string displayName, bool local, int difficultyIndex = -1, BotArchetype archetype = BotArchetype.HumanLike)
        {
            if (team != TeamId.None) _teamsInPlay.Add(team);
            var sp = PickSpawn(team, null);
            Vector3 pos = sp != null ? SpawnPoint.ClearPosition(sp.transform.position, null, sp.name) : Vector3.zero;
            float yaw = sp != null ? sp.transform.eulerAngles.y : 0f;
            if (sp != null) sp.lastUsedTime = Time.time;

            var go = Instantiate(prefab, pos, Quaternion.Euler(0f, yaw, 0f));
            go.name = (local ? "Player_" : "Bot_") + displayName;
            var c = go.GetComponent<Combatant>();
            if (c == null) return null;
            c.DisplayName = displayName;
            c.Team = team;
            c.IsLocalPlayer = local;
            c.IsBot = !local;
            c.stats.Reset();

            var bot = go.GetComponent<BotController>();
            if (bot != null)
            {
                if (local) bot.enabled = false;
                else if (difficulties != null && difficulties.Length > 0)
                {
                    int di = difficultyIndex >= 0 ? difficultyIndex : LR2Settings.BotDifficulty;
                    bot.Difficulty = difficulties[Mathf.Clamp(di, 0, difficulties.Length - 1)];
                    bot.TierIndex = Mathf.Clamp(di, 0, 3);
                    bot.Archetype = archetype;
                    if (difficultyIndex >= 0) bot.AdaptiveOverride = difficultyIndex == 4; // per-slot difficulty beats the global one
                }
            }
            _participants.Add(c);
            _deathStreak[c] = 0;
            return c;
        }

        /// <summary>Spawn scoring: far from living hostiles (sweet spot 18-40 m), never in their line of sight, not occupied.</summary>
        public SpawnPoint PickSpawn(TeamId team, Combatant forCombatant)
        {
            var candidates = new List<(SpawnPoint sp, float score)>();
            foreach (var sp in SpawnPoint.All)
            {
                if (sp == null) continue;
                // Charlie/Delta (3-way, 4-way skirmish) have no bases: they use the neutral and free-for-all spawns; the scoring
                // below still keeps them near their own team and away from every other one
                bool extraTeam = team == TeamId.Charlie || team == TeamId.Delta;
                bool teamOk = team == TeamId.None ? sp.allowFreeForAll
                    : extraTeam ? (sp.allowFreeForAll || sp.team == TeamId.None)
                    : (sp.team == team || sp.team == TeamId.None);
                if (!teamOk) continue;
                candidates.Add((sp, ScoreSpawn(sp, forCombatant, team)));
            }
            if (candidates.Count == 0)
                foreach (var sp in SpawnPoint.All)
                    if (sp != null) candidates.Add((sp, ScoreSpawn(sp, forCombatant, team)));
            if (candidates.Count == 0) return null;

            candidates.Sort((a, b) => b.score.CompareTo(a.score));
            int pick = Random.Range(0, Mathf.Min(3, candidates.Count));
            return candidates[pick].sp;
        }

        private float ScoreSpawn(SpawnPoint sp, Combatant who, TeamId team)
        {
            Vector3 p = sp.transform.position;
            Vector3 head = p + Vector3.up * 1.6f;
            float nearest = 999f;
            float score = 0f;
            foreach (var c in Combatant.All)
            {
                if (c == null || c == who || !c.IsAlive) continue;
                bool hostile = who != null ? TeamUtil.AreHostile(who, c) : TeamUtil.AreHostile(team, c.Team);
                float d = Vector3.Distance(p, c.Position);
                if (!hostile)
                {
                    if (team != TeamId.None && d < 25f) score += 6f; // spawn near teammates in TDM
                    continue;
                }
                nearest = Mathf.Min(nearest, d);
                if (!Physics.Linecast(c.EyePosition, head, LR2Layers.VisionMask, QueryTriggerInteraction.Ignore)) score -= 120f;
            }
            if (nearest < 999f) score += nearest < 18f ? nearest * 2f - 40f : Mathf.Min(nearest, 40f);
            if (Physics.CheckCapsule(p + Vector3.up * 0.4f, p + Vector3.up * 1.6f, 0.4f, LR2Layers.CharacterMask, QueryTriggerInteraction.Ignore)) score -= 200f;
            if (Time.time - sp.lastUsedTime < 4f) score -= 30f;
            return score + Random.value * 6f;
        }

        private void ProcessRespawns()
        {
            if (_respawnAt.Count == 0) return;
            List<Combatant> ready = null;
            foreach (var kv in _respawnAt)
                if (Time.time >= kv.Value) (ready ??= new List<Combatant>()).Add(kv.Key);
            if (ready == null) return;
            foreach (var c in ready)
            {
                _respawnAt.Remove(c);
                if (c == null) continue;
                var soldier = c.GetComponent<SoldierController>();
                if (soldier == null) continue;
                var sp = PickSpawn(c.Team, c);
                if (sp != null) sp.lastUsedTime = Time.time;
                Vector3 at = sp != null ? SpawnPoint.ClearPosition(sp.transform.position, c, sp.name) : c.Position;
                soldier.Respawn(at, sp != null ? sp.transform.eulerAngles.y : 0f);
                soldier.ControlsEnabled = State == MatchState.Playing;
            }
        }

        public float GetRespawnRemaining(Combatant c)
        {
            return c != null && _respawnAt.TryGetValue(c, out float t) ? Mathf.Max(0f, t - Time.time) : 0f;
        }

        // ------------------------------------------------------------------ drones (legacy orb enemy)
        private void UpdateOrbs()
        {
            _orbs.RemoveAll(o => o == null);
            if (!Settings.spawnOrbs || orbPrefab == null || Time.time < _nextOrbSpawn || _orbs.Count >= Settings.maxOrbs) return;
            _nextOrbSpawn = Time.time + Settings.orbSpawnInterval;

            SpawnPoint best = null;
            float bestScore = float.MinValue;
            foreach (var sp in SpawnPoint.All)
            {
                if (sp == null || !sp.allowDrones) continue;
                float nearest = 999f;
                foreach (var c in _participants)
                    if (c != null && c.IsAlive) nearest = Mathf.Min(nearest, Vector3.Distance(c.Position, sp.transform.position));
                float s = (nearest > 15f ? 50f : 0f) + Mathf.Min(nearest, 35f) + Random.value * 15f;
                if (s > bestScore)
                {
                    bestScore = s;
                    best = sp;
                }
            }
            if (best == null) return;
            var go = Instantiate(orbPrefab, best.transform.position + Vector3.up * 1.6f, Quaternion.identity);
            var orb = go.GetComponent<OrbDrone>();
            if (orb != null) _orbs.Add(orb);
        }

        // ------------------------------------------------------------------ scoring
        private void OnHit(HitEvent e)
        {
            if (e.victim == null || e.attacker == null || e.attacker == e.victim) return;
            if (!_damageLog.TryGetValue(e.victim, out var list))
            {
                list = new List<(Combatant, float)>();
                _damageLog[e.victim] = list;
            }
            list.Add((e.attacker, Time.time));
        }

        private void OnKilled(KillEvent e)
        {
            var victim = e.victim;
            if (victim == null) return;
            bool victimIsDrone = victim.Team == TeamId.Hostile;
            var killer = e.killer;

            if (!victimIsDrone)
            {
                victim.stats.deaths++;
                victim.stats.currentStreak = 0;
                _deathStreak[victim] = (_deathStreak.TryGetValue(victim, out int ds) ? ds : 0) + 1;
                if (State == MatchState.Playing || State == MatchState.Countdown)
                    _respawnAt[victim] = Time.time + (Settings != null ? Settings.respawnDelay : 3f) + LR2.World.KillcamDirector.ExtraRespawnDelay(in e, Settings != null ? Settings.respawnDelay : 3f);
                if (killer != null && killer != victim) _lastKiller[victim] = killer;
            }

            if (killer == null || killer == victim || State != MatchState.Playing)
            {
                _damageLog.Remove(victim);
                return;
            }

            if (victimIsDrone)
            {
                Award(killer, dronePoints, "DRONE DESTROYED");
                _damageLog.Remove(victim);
                return;
            }
            if (killer.Team == TeamId.Hostile) // killed by a drone
            {
                _damageLog.Remove(victim);
                return;
            }

            // Kill credit
            killer.stats.kills++;
            killer.stats.currentStreak++;
            killer.stats.bestStreak = Mathf.Max(killer.stats.bestStreak, killer.stats.currentStreak);
            if (e.headshot) killer.stats.headshots++;
            if (IsTeamMode && killer.Team != TeamId.None) _teamScore[killer.Team] = TeamScore(killer.Team) + 1;
            Award(killer, killPoints, "KILL");

            // Medals
            if (!_firstBlood)
            {
                _firstBlood = true;
                Medal(killer, "first_blood", "FIRST BLOOD", 50);
            }
            if (e.headshot) Medal(killer, "headshot", "HEADSHOT", headshotBonus);
            if (e.wallbang) Medal(killer, "wallbang", "WALLBANG", 50);
            if (e.type == DamageType.Bullet)
            {
                if (e.distance >= longshotDistance) Medal(killer, "longshot", "LONGSHOT", 50);
                else if (e.distance > 0f && e.distance <= 3f) Medal(killer, "point_blank", "POINT BLANK", 25);
            }
            if (_lastKiller.TryGetValue(killer, out var nemesis) && nemesis == victim)
            {
                Medal(killer, "revenge", "REVENGE", 50);
                _lastKiller.Remove(killer);
            }
            if (_deathStreak.TryGetValue(killer, out int dstreak) && dstreak >= 3) Medal(killer, "comeback", "COMEBACK", 75);
            _deathStreak[killer] = 0;

            float last = _lastKillTime.TryGetValue(killer, out float lk) ? lk : -999f;
            int multi = Time.time - last <= multiKillWindow ? (_multiKill.TryGetValue(killer, out int m) ? m : 1) + 1 : 1;
            _multiKill[killer] = multi;
            _lastKillTime[killer] = Time.time;
            if (multi == 2) Medal(killer, "double_kill", "DOUBLE KILL", 50);
            else if (multi == 3) Medal(killer, "triple_kill", "TRIPLE KILL", 100);
            else if (multi >= 4) Medal(killer, "multi_kill", "MULTI KILL", 150);

            switch (killer.stats.currentStreak)
            {
                case 3: Medal(killer, "streak3", "RAMPAGE", 75); break;
                case 5: Medal(killer, "streak5", "UNSTOPPABLE", 125); break;
                case 7: Medal(killer, "streak7", "RELENTLESS", 175); break;
                case 10: Medal(killer, "streak10", "UNTOUCHABLE", 250); break;
            }

            // Assists
            if (_damageLog.TryGetValue(victim, out var log))
            {
                var credited = new HashSet<Combatant>();
                foreach (var (attacker, time) in log)
                {
                    if (attacker == null || attacker == killer || Time.time - time > assistWindow || credited.Contains(attacker)) continue;
                    if (attacker.Team == TeamId.Hostile) continue;
                    credited.Add(attacker);
                    attacker.stats.assists++;
                    Award(attacker, assistPoints, "ASSIST");
                }
                _damageLog.Remove(victim);
            }

            CheckScoreLimit(killer);
        }

        public void AwardFlagCapture(Combatant capturer, string sectorName)
        {
            if (capturer == null || State == MatchState.Ended) return;
            capturer.stats.kills += 3;
            if (IsTeamMode && capturer.Team != TeamId.None)
                _teamScore[capturer.Team] = TeamScore(capturer.Team) + 3;
            Award(capturer, 300, "FLAG CAPTURE");
            Medal(capturer, "flag_cap", $"FLAG SECURED · {sectorName} (+3)", 0);
            CheckScoreLimit(capturer);
        }

        private void Award(Combatant c, int points, string reason)
        {
            if (c == null || points == 0) return;
            c.stats.score += points;
            GameEvents.RaiseScore(new ScoreEvent { combatant = c, points = points, reason = reason });
        }

        private void Medal(Combatant c, string id, string title, int points)
        {
            c.stats.score += points;
            GameEvents.RaiseMedal(new MedalEvent { combatant = c, id = id, title = title, points = points });
            if (c.IsLocalPlayer)
            {
                var s = LR2CommonSounds.Instance;
                if (s != null) LR2Audio.Play2D(s.medal, 0.8f, 0.15f);
            }
        }

        public int GetTeamScore(TeamId team) => TeamScore(team);

        private int TeamScore(TeamId team) => _teamScore.TryGetValue(team, out int s) ? s : 0;

        /// <summary>Score shown on the HUD for a combatant (team score in TDM, kills otherwise).</summary>
        public int GetDisplayScore(Combatant c)
        {
            if (c == null) return 0;
            return IsTeamMode && c.Team != TeamId.None ? TeamScore(c.Team) : c.stats.kills;
        }

        /// <summary>Highest score among opponents of the given combatant.</summary>
        public int GetBestOpponentScore(Combatant c)
        {
            if (IsTeamMode && c != null && c.Team != TeamId.None) return TeamScore(BestOpponentTeam(c.Team));
            int best = 0;
            foreach (var p in _participants)
                if (p != null && p != c) best = Mathf.Max(best, p.stats.kills);
            return best;
        }

        /// <summary>The leading team other than <paramref name="team"/> (any number of teams).</summary>
        public TeamId BestOpponentTeam(TeamId team)
        {
            TeamId best = team == TeamId.Alpha ? TeamId.Bravo : TeamId.Alpha;
            int top = int.MinValue;
            foreach (var kv in _teamScore)
            {
                if (kv.Key == team || !_teamsInPlay.Contains(kv.Key)) continue;
                if (kv.Value > top) { top = kv.Value; best = kv.Key; }
            }
            return best;
        }

        /// <summary>Teams with at least one soldier this match.</summary>
        public IEnumerable<TeamId> TeamsInPlay => _teamsInPlay;

        private TeamId TopTeam()
        {
            TeamId best = TeamId.Alpha;
            int top = int.MinValue;
            foreach (var kv in _teamScore)
                if (_teamsInPlay.Contains(kv.Key) && kv.Value > top) { top = kv.Value; best = kv.Key; }
            return best;
        }

        private void CheckScoreLimit(Combatant killer)
        {
            int limit = ScoreLimit;
            if (IsTeamMode)
            {
                if (TeamScore(killer.Team) >= limit || IsSuddenDeath) EndMatch();
            }
            else if (killer.stats.kills >= limit || IsSuddenDeath) EndMatch();
        }

        private void OnTimeUp()
        {
            TimeRemaining = 0f;
            if (!IsSuddenDeath && Settings != null && Settings.mode != GameModeType.FreeForAll && IsTied())
            {
                IsSuddenDeath = true;
                TimeRemaining = suddenDeathSeconds;
                GameEvents.RaiseMedal(new MedalEvent { combatant = LocalPlayer, id = "sudden_death", title = "SUDDEN DEATH", points = 0 });
                return;
            }
            EndMatch();
        }

        private bool IsTied()
        {
            if (IsTeamMode)
            {
                int topScore = int.MinValue, leaders = 0;
                foreach (var kv in _teamScore)
                {
                    if (!_teamsInPlay.Contains(kv.Key)) continue;
                    if (kv.Value > topScore) { topScore = kv.Value; leaders = 1; }
                    else if (kv.Value == topScore) leaders++;
                }
                return leaders > 1;
            }
            int top = -1, count = 0;
            foreach (var p in _participants)
            {
                if (p == null) continue;
                if (p.stats.kills > top)
                {
                    top = p.stats.kills;
                    count = 1;
                }
                else if (p.stats.kills == top) count++;
            }
            return count > 1;
        }

        public void EndMatch()
        {
            if (State == MatchState.Ended) return;
            IsDraw = IsTied();
            if (IsTeamMode)
            {
                WinnerTeam = TopTeam();
                LocalPlayerWon = !IsDraw && LocalPlayer != null && LocalPlayer.Team == WinnerTeam;
            }
            else
            {
                Combatant best = null;
                foreach (var p in _participants)
                    if (p != null && (best == null || p.stats.kills > best.stats.kills || (p.stats.kills == best.stats.kills && p.stats.score > best.stats.score)))
                        best = p;
                WinnerCombatant = best;
                LocalPlayerWon = !IsDraw && best != null && best == LocalPlayer;
            }

            SetControls(false);
            SetState(MatchState.Ended);
            var s = LR2CommonSounds.Instance;
            if (s != null) LR2Audio.Play2D(LocalPlayerWon ? s.victory : s.defeat);
            Invoke(nameof(UnlockCursor), 2f);
        }

        private void UnlockCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void SetControls(bool enabled)
        {
            foreach (var c in _participants)
            {
                if (c == null) continue;
                var s = c.GetComponent<SoldierController>();
                if (s != null) s.ControlsEnabled = enabled;
            }
        }

        private void SetState(MatchState state)
        {
            var prev = State;
            State = state;
            GameEvents.RaiseMatchState(new MatchStateEvent { previous = prev, current = state });
        }

        public void RestartMatch()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
        }

        public void QuitToMenu()
        {
            if (ExperimentSessionRunner.IsSessionActive)
            {
                ExperimentSessionRunner.AbortSession();
                return;
            }
            Time.timeScale = 1f;
            LR2Session.ActiveSettings = null;
            if (Application.CanStreamedLevelBeLoaded(LR2Session.MenuScene)) SceneManager.LoadScene(LR2Session.MenuScene);
            else SceneManager.LoadScene(SceneManager.GetActiveScene().name);
        }
    }
}
