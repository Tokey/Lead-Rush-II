using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LR2.Combat;
using LR2.Core;
using LR2.Game;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace LR2.AI.LLM
{
    public enum LlmIntegrationMode { Off = 0, Commander = 1, TacticalMicro = 2, AvatarPilot = 3 }

    /// <summary>
    /// B127 + B151: which teams get an LLM controller, integration depth (Commander / TacticalMicro / AvatarPilot),
    /// which Groq models, and how often. Command line wins over the saved setting:
    /// <c>-lr2LlmTeams Alpha,Bravo</c>, <c>-lr2LlmMode Commander|TacticalMicro|AvatarPilot</c>, <c>-lr2LlmModel name</c>, <c>-lr2LlmInterval seconds</c>.
    /// </summary>
    public static class LLMCommanderConfig
    {
        public const string PrefTeams = "LR2.LLM.Teams";
        public const string PrefModel = "LR2.LLM.Model";
        /// <summary>Tried in order; a model Groq rejects (unknown or retired) falls through to the next.</summary>
        public static readonly string[] DefaultModels = { "openai/gpt-oss-20b", "llama-3.1-8b-instant", "llama-3.3-70b-versatile" };
        /// <summary>Lowest-latency ordering for fast Avatar Pilot action-chunk synthesis.</summary>
        public static readonly string[] FastPilotModels = { "llama-3.1-8b-instant", "openai/gpt-oss-20b", "llama-3.3-70b-versatile" };
        /// <summary>B153/B154 runtime experiment override (cleared on subsystem init / menu load).</summary>
        public static LlmIntegrationMode? ExperimentModeOverride;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOverrides() => ExperimentModeOverride = null;

        private static string Arg(string name)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (string.Equals(a[i], name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
            return null;
        }

        public static LlmIntegrationMode Mode()
        {
            string raw = Arg("-lr2LlmMode");
            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (int.TryParse(raw.Trim(), out int n)) return (LlmIntegrationMode)Mathf.Clamp(n, 0, 3);
                if (Enum.TryParse(raw.Trim(), true, out LlmIntegrationMode parsed)) return parsed;
            }
            if (ExperimentModeOverride.HasValue) return ExperimentModeOverride.Value;
            if (!string.IsNullOrWhiteSpace(Arg("-lr2LlmTeams")))
                return LR2Settings.LlmMode > 0 ? (LlmIntegrationMode)LR2Settings.LlmMode : LlmIntegrationMode.Commander;
            return (LlmIntegrationMode)LR2Settings.LlmMode;
        }

        public static List<TeamId> Teams()
        {
            var list = new List<TeamId>();
            string raw = Arg("-lr2LlmTeams");
            if (string.IsNullOrWhiteSpace(raw))
            {
                if (Mode() != LlmIntegrationMode.Off)
                {
                    int cfgTeams = LR2Settings.LlmTeams;
                    if (cfgTeams == 1 || cfgTeams == 3) list.Add(TeamId.Alpha);
                    if (cfgTeams == 2 || cfgTeams == 3) list.Add(TeamId.Bravo);
                    if (list.Count > 0) return list;
                }
                raw = PlayerPrefs.GetString(PrefTeams, "");
            }
            foreach (var part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (Enum.TryParse(part.Trim(), true, out TeamId t) && t != TeamId.None && t != TeamId.Hostile && !list.Contains(t)) list.Add(t);
            return list;
        }

        public static string[] Models(LlmIntegrationMode mode = LlmIntegrationMode.Commander)
        {
            string[] baseList = mode == LlmIntegrationMode.AvatarPilot ? FastPilotModels : DefaultModels;
            string m = Arg("-lr2LlmModel") ?? PlayerPrefs.GetString(PrefModel, "");
            if (string.IsNullOrWhiteSpace(m)) return baseList;
            var list = new List<string> { m.Trim() };
            foreach (var d in baseList) if (d != m.Trim()) list.Add(d);
            return list.ToArray();
        }

        public static float Interval(LlmIntegrationMode mode = LlmIntegrationMode.Commander)
        {
            if (float.TryParse(Arg("-lr2LlmInterval"), NumberStyles.Float, CultureInfo.InvariantCulture, out float s))
                return Mathf.Clamp(s, 3f, 60f);
            float pref = LR2Settings.LlmIntervalSeconds;
            if (mode == LlmIntegrationMode.AvatarPilot) return Mathf.Min(pref, 5.5f);
            if (mode == LlmIntegrationMode.TacticalMicro) return Mathf.Min(pref, 8f);
            return pref;
        }
    }

    /// <summary>B127: creates the configured commanders once a match is playing. Lives only in match scenes.</summary>
    public class LLMCommanderHost : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            TryCreate();
        }

        private static void OnSceneLoaded(Scene s, LoadSceneMode m) => TryCreate();

        private static void TryCreate()
        {
            if (SceneManager.GetActiveScene().name == LR2Session.MenuScene) return;
            if (FindAnyObjectByType<LLMCommanderHost>() != null) return;
            new GameObject("LR2 LLM Commanders").AddComponent<LLMCommanderHost>();
        }

        private IEnumerator Start()
        {
            float until = Time.realtimeSinceStartup + 20f;
            while (MatchManager.Instance == null && Time.realtimeSinceStartup < until) yield return null;
            if (MatchManager.Instance == null) { Destroy(gameObject); yield break; }   // menu or a non-match scene
            while (MatchManager.Instance != null && MatchManager.Instance.State != MatchState.Playing) yield return null;
            if (MatchManager.Instance == null) { Destroy(gameObject); yield break; }
            yield return new WaitForSeconds(0.5f);

            var byTeam = new Dictionary<TeamId, List<BotController>>();
            var soloLlm = new List<BotController>();
            foreach (var c in Combatant.All)
            {
                if (c == null || !c.IsBot) continue;
                var b = c.GetComponent<BotController>();
                if (b == null || b.Archetype != BotArchetype.LlmEnhanced) continue;
                if (c.Team == TeamId.None) soloLlm.Add(b);
                else
                {
                    if (!byTeam.TryGetValue(c.Team, out var lst)) { lst = new List<BotController>(); byTeam[c.Team] = lst; }
                    lst.Add(b);
                }
            }

            LlmIntegrationMode activeMode = LLMCommanderConfig.Mode();
            if ((byTeam.Count > 0 || soloLlm.Count > 0) && activeMode == LlmIntegrationMode.Off)
                activeMode = LlmIntegrationMode.Commander;

            if (byTeam.Count > 0 || soloLlm.Count > 0)
            {
                foreach (var kv in byTeam)
                    gameObject.AddComponent<LLMCommander>().InitTeamSubset(kv.Key, activeMode, kv.Value);
                foreach (var solo in soloLlm)
                    gameObject.AddComponent<LLMCommander>().InitForSoloBot(solo, activeMode);
                yield break;
            }

            if (activeMode == LlmIntegrationMode.Off || LLMCommanderConfig.Teams().Count == 0)
            {
                Destroy(gameObject);
                yield break;
            }

            foreach (var team in LLMCommanderConfig.Teams())
            {
                bool any = false;
                foreach (var c in Combatant.All) if (c != null && c.Team == team && c.GetComponent<BotController>() != null) { any = true; break; }
                if (!any) { Debug.Log($"[LR2] LLM commander: no bots on {team}, skipped"); continue; }
                gameObject.AddComponent<LLMCommander>().Init(team);
            }
        }
    }

    /// <summary>
    /// B127: an LLM squad commander for one team (or one solo bot in 1v1 Duel / FFA). Every <see cref="Interval"/> s it sends Groq a compact JSON picture of what
    /// its bots know (no information they could not have: only enemies a bot has seen, with how long ago), and turns the reply into
    /// <see cref="BotDirective"/>s through the same <see cref="BotController.IssueDirective"/> path the chat orders use. The bots
    /// still aim, shoot, take cover and reload themselves; orders last 1.6 intervals, so a slow or failed reply only means the
    /// bots fall back to their own brains. Every exchange is logged (never the key) for the comparison with non-LLM bots.
    /// </summary>
    public class LLMCommander : MonoBehaviour
    {
        public static readonly List<LLMCommander> All = new List<LLMCommander>();

        public TeamId Team { get; private set; }
        public LlmIntegrationMode Mode { get; private set; } = LlmIntegrationMode.Commander;
        public float Interval { get; private set; } = 10f;
        public string Model => _models != null && _modelIndex < _models.Length ? _models[_modelIndex] : "-";
        public int Decisions { get; private set; }
        public int Failures { get; private set; }
        public int OrdersIssued { get; private set; }
        public int OrdersRejected { get; private set; }
        public int InterruptsTriggered { get; private set; }
        public int MicroChunksIssued { get; private set; }
        public int UtilityOrdersIssued { get; private set; }
        public int PromptTokens { get; private set; }
        public int CompletionTokens { get; private set; }
        public float TotalLatency { get; private set; }
        public string LastPlan { get; private set; } = "";
        public string LastError { get; private set; } = "";

        private string[] _models;
        private int _modelIndex;
        private BotController _soloBot;
        private HashSet<BotController> _subsetBots;
        private readonly Dictionary<Combatant, string> _ids = new Dictionary<Combatant, string>();
        private readonly Dictionary<string, Combatant> _byId = new Dictionary<string, Combatant>();
        private int _allyN, _enemyN;
        private Vector2 _boundsMin, _boundsMax;
        private string _logPath;
        private bool _toldNoKey;
        private int _lastAliveAllies = -1, _lastVisibleContacts = -1;
        /// <summary>What the team knew at the last decision: enemy -> last known position. Orders never use more than this.</summary>
        private readonly Dictionary<Combatant, Vector3> _known = new Dictionary<Combatant, Vector3>();

        public void Init(TeamId team)
        {
            LlmIntegrationMode mode = LLMCommanderConfig.Mode();
            if (mode == LlmIntegrationMode.Off) mode = LlmIntegrationMode.Commander;
            InitInternal(team, mode, null, null);
        }

        public void InitTeamSubset(TeamId team, LlmIntegrationMode mode, List<BotController> subset)
        {
            InitInternal(team, mode, null, subset != null ? new HashSet<BotController>(subset) : null);
        }

        public void InitForSoloBot(BotController bot, LlmIntegrationMode mode)
        {
            InitInternal(bot != null && bot.Combatant != null ? bot.Combatant.Team : TeamId.None, mode, bot, null);
        }

        private void InitInternal(TeamId team, LlmIntegrationMode mode, BotController solo, HashSet<BotController> subset)
        {
            Team = team;
            Mode = mode == LlmIntegrationMode.Off ? LlmIntegrationMode.Commander : mode;
            _soloBot = solo;
            _subsetBots = subset;
            Interval = LLMCommanderConfig.Interval(Mode);
            _models = LLMCommanderConfig.Models(Mode);
            ComputeBounds();
            string dir = Application.isEditor ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "LR2_LLM"))
                                              : Path.Combine(Application.persistentDataPath, "LLM");
            try { Directory.CreateDirectory(dir); } catch (Exception) { }
            string tag = _soloBot != null && _soloBot.Combatant != null ? _soloBot.Combatant.DisplayName.Replace(' ', '_') : team.ToString();
            _logPath = Path.Combine(dir, $"llm_{tag}_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            All.Add(this);
            Debug.Log($"[LR2] LLM {Mode} for {tag}: models {string.Join(", ", _models)}, every {Interval:0.0}s, key from {(GroqClient.HasKey ? GroqClient.KeySource : "nowhere")}");
            StartCoroutine(Loop());
        }

        private void OnDestroy() => All.Remove(this);

        private IEnumerator Loop()
        {
            yield return new WaitForSeconds(UnityEngine.Random.Range(0.2f, 1.5f)); // two commanders do not fire together
            while (MatchManager.Instance != null && MatchManager.Instance.State == MatchState.Playing)
            {
                if (!GroqClient.HasKey)
                {
                    LastError = "no Groq API key";
                    if (!_toldNoKey)
                    {
                        _toldNoKey = true;
                        Debug.LogWarning($"[LR2] LLM commander {Team}: no Groq API key. Set GROQ_API_KEY or put the key in {GroqClient.DefaultKeyPath}");
                        Say($"No LLM key: {TeamUtil.DisplayName(Team)} bots fight on their own.");
                    }
                    yield return new WaitForSeconds(5f);
                    GroqClient.ReloadKey();
                    continue;
                }

                string state = BuildState();
                GroqClient.Result result = null;
                float sent = Time.time;
                yield return GroqClient.Chat(Model, SystemPrompt(), state, 700, 0.4f, true, r => result = r);
                Decisions++;
                if (result != null && result.ok)
                {
                    TotalLatency += result.seconds;
                    PromptTokens += result.promptTokens;
                    CompletionTokens += result.completionTokens;
                    var (issued, rejected, plan, note) = Apply(result.content);
                    OrdersIssued += issued;
                    OrdersRejected += rejected;
                    if (!string.IsNullOrWhiteSpace(plan)) { LastPlan = plan; Say(plan); }
                    LastError = note ?? "";
                    Log(sent, state, result, issued, rejected, note);
                }
                else
                {
                    Failures++;
                    LastError = result != null ? result.error : "no result";
                    Log(sent, state, result, 0, 0, LastError);
                    if (result != null && result.modelRejected && _modelIndex < _models.Length - 1)
                    {
                        Debug.LogWarning($"[LR2] LLM commander {Team}: model {Model} rejected ({result.error}); trying {_models[_modelIndex + 1]}");
                        _modelIndex++;
                        continue;
                    }
                }
                SnapshotPulseCounts(out _lastAliveAllies, out _lastVisibleContacts);
                float nextAt = sent + Interval;
                while (Time.time < nextAt && MatchManager.Instance != null && MatchManager.Instance.State == MatchState.Playing)
                {
                    yield return new WaitForSeconds(0.25f);
                    if (Mode != LlmIntegrationMode.Commander && Time.time - sent >= 3.2f && GroqClient.CanSendNow && CheckReactiveInterrupt())
                    {
                        InterruptsTriggered++;
                        break;
                    }
                }
            }
        }

        private bool ControlsBot(Combatant c, BotController bot)
        {
            if (c == null || !c.IsAlive || bot == null) return false;
            if (_soloBot != null) return bot == _soloBot;
            if (c.Team != Team) return false;
            return _subsetBots == null || _subsetBots.Contains(bot);
        }

        private void SnapshotPulseCounts(out int aliveAllies, out int visibleContacts)
        {
            aliveAllies = 0;
            visibleContacts = 0;
            foreach (var c in Combatant.All)
            {
                if (c == null || !c.IsAlive) continue;
                var bot = c.GetComponent<BotController>();
                if (_soloBot != null ? (bot != _soloBot) : (c.Team != Team)) continue;
                aliveAllies++;
                if (bot != null && bot.Blackboard.targetVisible) visibleContacts++;
            }
        }

        private bool CheckReactiveInterrupt()
        {
            int alive = 0, vis = 0;
            bool urgentHurt = false;
            foreach (var c in Combatant.All)
            {
                if (c == null || !c.IsAlive) continue;
                var bot = c.GetComponent<BotController>();
                if (_soloBot != null ? (bot != _soloBot) : (c.Team != Team)) continue;
                alive++;
                if (bot != null)
                {
                    if (bot.Blackboard.targetVisible) vis++;
                    if (bot.Blackboard.health01 < 0.35f && bot.Blackboard.underFire) urgentHurt = true;
                }
            }
            bool trigger = (_lastAliveAllies > 0 && alive < _lastAliveAllies) || (vis > _lastVisibleContacts) || urgentHurt;
            _lastAliveAllies = alive;
            _lastVisibleContacts = vis;
            return trigger;
        }

        // ------------------------------------------------------------------ the prompt
        private string SystemPrompt()
        {
            string who = _soloBot != null && _soloBot.Combatant != null
                ? $"solo operator {_soloBot.Combatant.DisplayName}"
                : $"team {TeamUtil.DisplayName(Team)}";
            string basePrompt =
                $"You command {who} in Lead Rush II, a fast close-quarters shooter. Every {Interval:0.0}s you get the battlefield " +
                "as JSON and give your bots orders for the next stretch. Orders: \"hold\" (stay and watch), \"move\" (go to x,z), \"attack\" (hunt enemy id), \"flank\" " +
                "(reach x,z from the side, then engage), \"retreat\" (fall back, to x,z if given), \"regroup\" (join ally id), \"free\" (bot decides). " +
                "Coordinates are metres on the map's x,z plane inside bounds. Enemies listed are only what bots have seen; seenAgo is seconds since. " +
                "Keep bots in pairs, flank active fights, pull back badly hurt bots, and in CTF contest the flag zone. Never output orders for entries with player:true or llm:false; only order your LLM bot IDs. ";
            if (Mode == LlmIntegrationMode.Commander)
            {
                return basePrompt +
                    "Your bots aim, shoot, take cover and reload themselves. Reply with ONE JSON object only: " +
                    "{\"plan\":\"short sentence\",\"orders\":[{\"bot\":\"A1\",\"order\":\"flank\",\"x\":12,\"z\":-30,\"target\":\"E2\"}]}. " +
                    "At most one order per bot.";
            }
            if (Mode == LlmIntegrationMode.TacticalMicro)
            {
                return basePrompt +
                    "TACTICAL MICRO mode: also shape HOW each bot fights with optional per-bot fields: " +
                    "\"stance\":\"auto|stand|crouch|prone\", \"peek\":\"auto|hold|jiggle|wideswing\", " +
                    "\"burst\":\"auto|tap|burst|fullauto\", \"utility\":\"none|frag|reload|holdfire\", \"aggression\":-1.0..1.0. " +
                    "Use \"frag\" when enemies cluster or hold cover within ~8-24m, \"jiggle\"/\"wideswing\" in close duels, \"tap\"/\"burst\" at range, \"reload\" when safe (<55% ammo). " +
                    "Reply with ONE JSON object only: {\"plan\":\"short tactic\",\"orders\":[{\"bot\":\"A1\",\"order\":\"attack\",\"target\":\"E1\",\"stance\":\"crouch\",\"peek\":\"jiggle\",\"burst\":\"burst\",\"utility\":\"frag\",\"aggression\":0.5}]}.";
            }
            return basePrompt +
                "AVATAR PILOT mode (Fast Action Chunking): control both squad goals and timed 60Hz motor chunks in \"actions\" (2-3 sequential steps lasting 4-6s total so bots never stall between turns): " +
                "\"actions\":[{\"cmd\":\"sprint_to|strafe_fire|crouch_peek|hold_angle|throw_frag|fallback_reload|prone_ambush\",\"x\":12,\"z\":-30,\"dir\":-1|1,\"dur\":1.8,\"ads\":true}]. " +
                "Also set \"order\", \"stance\", \"peek\", \"burst\", \"utility\", \"aggression\". " +
                "Reply with ONE JSON object only: {\"plan\":\"short chunk plan\",\"orders\":[{\"bot\":\"A1\",\"order\":\"flank\",\"x\":10,\"z\":-14,\"stance\":\"crouch\",\"peek\":\"wideswing\",\"burst\":\"burst\",\"actions\":[{\"cmd\":\"sprint_to\",\"x\":10,\"z\":-14,\"dur\":1.8,\"ads\":false},{\"cmd\":\"crouch_peek\",\"dir\":1,\"dur\":1.6,\"ads\":true}]}]}.";
        }

        private string BuildState()
        {
            var mm = MatchManager.Instance;
            var sb = new StringBuilder(1024);
            var inv = CultureInfo.InvariantCulture;
            int us = 0, them = 0;
            if (_soloBot != null && _soloBot.Combatant != null)
            {
                us = _soloBot.Combatant.stats.kills;
                foreach (var p in Combatant.All)
                    if (p != null && p != _soloBot.Combatant && p.Team != TeamId.Hostile) them = Mathf.Max(them, p.stats.kills);
            }
            else if (mm != null)
            {
                us = mm.GetTeamScore(Team);
                foreach (TeamId t in Enum.GetValues(typeof(TeamId)))
                    if (t != Team && t != TeamId.None && t != TeamId.Hostile) them = Mathf.Max(them, mm.GetTeamScore(t));
            }
            sb.Append("{\"map\":\"").Append(SceneManager.GetActiveScene().name.Replace("LR2_", "")).Append('"');
            sb.Append(",\"mode\":\"").Append(mm != null && mm.Settings != null ? mm.Settings.mode.ToString() : "?").Append('"');
            sb.Append(",\"score\":{\"us\":").Append(us).Append(",\"bestEnemy\":").Append(them)
              .Append(",\"limit\":").Append(mm != null ? mm.ScoreLimit : 0).Append('}');
            sb.Append(",\"timeLeft\":").Append(Mathf.RoundToInt(mm != null ? mm.TimeRemaining : 0f));
            sb.Append(",\"bounds\":{\"minX\":").Append(Mathf.RoundToInt(_boundsMin.x)).Append(",\"maxX\":").Append(Mathf.RoundToInt(_boundsMax.x))
              .Append(",\"minZ\":").Append(Mathf.RoundToInt(_boundsMin.y)).Append(",\"maxZ\":").Append(Mathf.RoundToInt(_boundsMax.y)).Append('}');
            var flag = FlagZoneController.Instance;
            if (flag != null && mm != null && mm.Settings != null && mm.Settings.IsFlagMode)
            {
                Vector3 f = flag.ActiveFlagPosition;
                sb.Append(",\"flagZone\":{\"x\":").Append(Mathf.RoundToInt(f.x)).Append(",\"z\":").Append(Mathf.RoundToInt(f.z)).Append('}');
            }
            if (!string.IsNullOrEmpty(LastPlan)) sb.Append(",\"yourLastPlan\":\"").Append(Clean(LastPlan)).Append('"');

            // squad and what it knows
            var known = new Dictionary<Combatant, (Vector3 pos, float ago, string by)>();
            sb.Append(",\"squad\":[");
            bool first = true;
            int dead = 0;
            foreach (var c in Combatant.All)
            {
                if (c == null || c.Team == TeamId.Hostile) continue;
                var bot = c.GetComponent<BotController>();
                if (_soloBot != null ? (bot != _soloBot) : (c.Team != Team)) continue;
                if (!c.IsAlive) { dead++; continue; }
                string id = IdOf(c, true);
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":\"").Append(id).Append('"');
                if (bot == null) sb.Append(",\"player\":true");
                else if (_subsetBots != null && !_subsetBots.Contains(bot)) sb.Append(",\"llm\":false");
                sb.Append(",\"x\":").Append(Mathf.RoundToInt(c.Position.x)).Append(",\"z\":").Append(Mathf.RoundToInt(c.Position.z));
                if (c.Health != null)
                    sb.Append(",\"hp\":").Append(Mathf.RoundToInt(c.Health.Current)).Append(",\"armor\":").Append(Mathf.RoundToInt(c.Health.Armor));
                if (bot != null)
                {
                    var bb = bot.Blackboard;
                    sb.Append(",\"ammo\":").Append(Mathf.RoundToInt(bb.ammo01 * 100f)).Append(",\"doing\":\"").Append(bot.CurrentGoalName).Append('"');
                    if (bb.hasDirective && Time.time < bb.directiveUntil) sb.Append(",\"order\":\"").Append(bb.directive.order).Append('"');
                    var tgt = bb.target;
                    if (tgt != null && tgt.IsAlive && TeamUtil.AreHostile(c, tgt))
                    {
                        float ago = bb.targetVisible ? 0f : bb.TimeSinceSeen;
                        if (ago < 20f)
                        {
                            Vector3 p = bb.targetVisible ? tgt.Position : bb.targetLastKnown;
                            if (!known.TryGetValue(tgt, out var k) || ago < k.ago) known[tgt] = (p, ago, id);
                            if (bb.targetVisible) sb.Append(",\"sees\":\"").Append(IdOf(tgt, false)).Append("\",\"dist\":").Append(Mathf.RoundToInt(bb.targetDistance));
                        }
                    }
                }
                sb.Append('}');
            }
            _known.Clear();
            foreach (var kv in known) _known[kv.Key] = kv.Value.pos;
            sb.Append("],\"deadAllies\":").Append(dead);
            sb.Append(",\"enemies\":[");
            first = true;
            foreach (var kv in known)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":\"").Append(IdOf(kv.Key, false)).Append("\",\"x\":").Append(Mathf.RoundToInt(kv.Value.pos.x))
                  .Append(",\"z\":").Append(Mathf.RoundToInt(kv.Value.pos.z)).Append(",\"seenAgo\":")
                  .Append(kv.Value.ago.ToString("0.0", inv)).Append(",\"by\":\"").Append(kv.Value.by).Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ the reply
#pragma warning disable 0649 // filled by JsonUtility
        [Serializable] private class Reply { public string plan; public OrderDto[] orders; }
        [Serializable] private class ActionDto { public string cmd; public float x; public float z; public float dir; public float dur; public bool ads; }
        [Serializable] private class OrderDto { public string bot; public string order; public string target; public float x; public float z; public string stance; public string peek; public string burst; public string utility; public float aggression; public ActionDto[] actions; }
#pragma warning restore 0649

        private (int issued, int rejected, string plan, string note) Apply(string content)
        {
            int a = content.IndexOf('{'), b = content.LastIndexOf('}');
            if (a < 0 || b <= a) return (0, 0, null, "reply had no JSON object");
            Reply reply;
            try { reply = JsonUtility.FromJson<Reply>(content.Substring(a, b - a + 1)); }
            catch (Exception e) { return (0, 0, null, "unreadable JSON: " + e.Message); }
            if (reply == null) return (0, 0, null, "empty JSON");
            int issued = 0, rejected = 0;
            var reasons = new List<string>();
            if (reply.orders != null)
                foreach (var o in reply.orders)
                {
                    string why = Issue(o);
                    if (why == null) issued++;
                    else { rejected++; if (reasons.Count < 4) reasons.Add($"{o?.bot}:{why}"); }
                }
            return (issued, rejected, reply.plan, reasons.Count > 0 ? string.Join("; ", reasons) : null);
        }

        /// <summary>Turns one order into a directive. Returns null when issued, or why it was rejected.</summary>
        private string Issue(OrderDto o)
        {
            if (o == null || string.IsNullOrEmpty(o.bot)) return "incomplete";
            string rawOrder = string.IsNullOrEmpty(o.order) ? (o.actions != null && o.actions.Length > 0 ? "hold" : null) : o.order;
            if (string.IsNullOrEmpty(rawOrder)) return "incomplete";
            if (!_byId.TryGetValue(o.bot.Trim(), out var c) || c == null || !c.IsAlive) return "unknown bot";
            var bot = c.GetComponent<BotController>();
            if (!ControlsBot(c, bot)) return "not managed by this LLM";
            bool hasXZ = !(Mathf.Approximately(o.x, 0f) && Mathf.Approximately(o.z, 0f));
            Combatant target = null;
            if (!string.IsNullOrEmpty(o.target)) _byId.TryGetValue(o.target.Trim(), out target);
            float dur = Interval * 1.6f;
            string reason = $"LLM[{Mode}]: {rawOrder}";

            BotDirective d = new BotDirective
            {
                duration = dur,
                rationale = reason,
                stance = ParseStance(o.stance),
                peek = ParsePeek(o.peek),
                burst = ParseBurst(o.burst),
                utility = ParseUtility(o.utility),
                aggression = Mathf.Clamp(o.aggression, -1f, 1f),
                microQueue = ParseActions(o.actions, c.Position)
            };
            if (d.utility != BotUtilityAction.None) UtilityOrdersIssued++;
            if (d.microQueue != null && d.microQueue.Length > 0) MicroChunksIssued += d.microQueue.Length;

            switch (rawOrder.Trim().ToLowerInvariant())
            {
                case "hold":
                {
                    Vector3 p = c.Position;
                    if (hasXZ && Sample(new Vector3(o.x, c.Position.y, o.z), out var h) && Vector3.Distance(h, c.Position) < 15f) p = h;
                    d.order = BotOrder.HoldPosition; d.position = p;
                    bot.IssueDirective(d);
                    return null;
                }
                case "move": case "push": case "advance":
                {
                    if (!hasXZ) return "move without x,z";
                    if (!Sample(new Vector3(o.x, c.Position.y, o.z), out var p)) return "x,z off the map";
                    d.order = BotOrder.MoveTo; d.position = p;
                    bot.IssueDirective(d);
                    return null;
                }
                case "attack": case "hunt": case "engage":
                {
                    // fair play: head for where the team last saw that enemy, not its live position (BotOrder.Attack tracks the
                    // target's real position, which no player would know); the bot's own eyes take over once it is in view
                    if (target != null && target.IsAlive && TeamUtil.AreHostile(c, target) && _known.TryGetValue(target, out var seenAt)
                        && Sample(seenAt, out var at))
                    {
                        d.order = BotOrder.MoveTo; d.position = at; d.target = target; d.rationale = reason + " " + o.target;
                        bot.IssueDirective(d);
                        return null;
                    }
                    if (hasXZ && Sample(new Vector3(o.x, c.Position.y, o.z), out var p))
                    {
                        d.order = BotOrder.MoveTo; d.position = p;
                        bot.IssueDirective(d);
                        return null;
                    }
                    return "attack without a known enemy";
                }
                case "flank":
                {
                    Vector3 goal;
                    if (hasXZ && Sample(new Vector3(o.x, c.Position.y, o.z), out var p)) goal = p;
                    else if (target != null && target.IsAlive && _known.TryGetValue(target, out var seen))
                    {
                        Vector3 to = seen - c.Position; to.y = 0f;
                        Vector3 side = Vector3.Cross(Vector3.up, to.sqrMagnitude > 0.01f ? to.normalized : c.transform.forward);
                        if (!Sample(seen + side * 11f - to.normalized * 4f, out goal) && !Sample(seen - side * 11f - to.normalized * 4f, out goal))
                            return "no flank route";
                    }
                    else return "flank without x,z or target";
                    d.order = BotOrder.Flank; d.position = goal;
                    bot.IssueDirective(d);
                    return null;
                }
                case "retreat": case "fall back": case "fallback":
                {
                    Vector3 p = Vector3.zero;
                    if (hasXZ && Sample(new Vector3(o.x, c.Position.y, o.z), out var h)) p = h;
                    d.order = BotOrder.Retreat; d.position = p;
                    bot.IssueDirective(d);
                    return null;
                }
                case "regroup": case "follow":
                {
                    if (target == null || !target.IsAlive || target.Team != Team) return "regroup without an ally";
                    Vector3 off = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) * Vector3.forward * 2.2f;
                    if (!Sample(target.Position + off, out var p)) p = target.Position;
                    d.order = BotOrder.MoveTo; d.position = p; d.duration = Mathf.Min(dur, 8f);
                    bot.IssueDirective(d);
                    return null;
                }
                case "free": case "none":
                    d.order = BotOrder.FreeRoam; d.duration = 0.1f;
                    bot.IssueDirective(d);
                    return null;
                default:
                    return "unknown order " + rawOrder;
            }
        }

        private static BotStanceOrder ParseStance(string s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "stand" => BotStanceOrder.Stand,
            "crouch" => BotStanceOrder.Crouch,
            "prone" => BotStanceOrder.Prone,
            _ => BotStanceOrder.Auto
        };

        private static BotPeekStyle ParsePeek(string s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "hold" or "hold_angle" => BotPeekStyle.HoldAngle,
            "jiggle" => BotPeekStyle.Jiggle,
            "wideswing" or "wide" or "swing" => BotPeekStyle.WideSwing,
            _ => BotPeekStyle.Auto
        };

        private static BotBurstStyle ParseBurst(string s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "tap" => BotBurstStyle.Tap,
            "burst" => BotBurstStyle.Burst,
            "fullauto" or "full" or "spray" => BotBurstStyle.FullAuto,
            _ => BotBurstStyle.Auto
        };

        private static BotUtilityAction ParseUtility(string s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "frag" or "grenade" or "throwfrag" => BotUtilityAction.ThrowFrag,
            "reload" or "safereload" => BotUtilityAction.SafeReload,
            "holdfire" or "sneak" => BotUtilityAction.HoldFire,
            _ => BotUtilityAction.None
        };

        private BotMicroAction[] ParseActions(ActionDto[] arr, Vector3 botPos)
        {
            if (Mode != LlmIntegrationMode.AvatarPilot || arr == null || arr.Length == 0) return null;
            var list = new List<BotMicroAction>(Mathf.Min(arr.Length, 4));
            for (int i = 0; i < arr.Length && list.Count < 4; i++)
            {
                var a = arr[i];
                if (a == null || string.IsNullOrEmpty(a.cmd)) continue;
                BotMicroActionKind kind = a.cmd.Trim().ToLowerInvariant() switch
                {
                    "sprint_to" or "sprint" => BotMicroActionKind.SprintTo,
                    "strafe_fire" or "strafe" => BotMicroActionKind.StrafeFire,
                    "crouch_peek" or "peek" => BotMicroActionKind.CrouchPeek,
                    "hold_angle" or "hold" => BotMicroActionKind.HoldAngle,
                    "throw_frag" or "frag" => BotMicroActionKind.ThrowFrag,
                    "fallback_reload" or "reload" => BotMicroActionKind.FallBackReload,
                    "prone_ambush" or "prone" => BotMicroActionKind.ProneAmbush,
                    _ => BotMicroActionKind.None
                };
                if (kind == BotMicroActionKind.None) continue;
                Vector3 pos = botPos;
                if (!(Mathf.Approximately(a.x, 0f) && Mathf.Approximately(a.z, 0f)) && Sample(new Vector3(a.x, botPos.y, a.z), out var sampled))
                    pos = sampled;
                list.Add(new BotMicroAction
                {
                    kind = kind,
                    targetPos = pos,
                    strafeDir = Mathf.Abs(a.dir) > 0.1f ? Mathf.Sign(a.dir) : (i % 2 == 0 ? 1f : -1f),
                    duration = Mathf.Clamp(a.dur > 0.1f ? a.dur : 1.5f, 0.5f, 4.5f),
                    ads = a.ads
                });
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        // ------------------------------------------------------------------ helpers
        private string IdOf(Combatant c, bool ally)
        {
            if (_ids.TryGetValue(c, out var id)) return id;
            id = ally ? (c.GetComponent<BotController>() == null ? "P" + (++_allyN) : "A" + (++_allyN)) : "E" + (++_enemyN);
            _ids[c] = id;
            _byId[id] = c;
            return id;
        }

        private static bool Sample(Vector3 p, out Vector3 onMesh)
        {
            if (NavMesh.SamplePosition(p, out var hit, 8f, NavMesh.AllAreas)) { onMesh = hit.position; return true; }
            onMesh = p;
            return false;
        }

        private void ComputeBounds()
        {
            var tri = NavMesh.CalculateTriangulation();
            if (tri.vertices == null || tri.vertices.Length == 0) { _boundsMin = new Vector2(-50f, -50f); _boundsMax = new Vector2(50f, 50f); return; }
            Vector2 mn = new Vector2(float.MaxValue, float.MaxValue), mx = new Vector2(float.MinValue, float.MinValue);
            foreach (var v in tri.vertices) { mn = Vector2.Min(mn, new Vector2(v.x, v.z)); mx = Vector2.Max(mx, new Vector2(v.x, v.z)); }
            _boundsMin = mn; _boundsMax = mx;
        }

        private void Say(string text)
        {
            var chat = LR2.UI.TacticalChatSystem.Instance;
            if (chat == null) return;
            string sender = _soloBot != null && _soloBot.Combatant != null
                ? _soloBot.Combatant.DisplayName
                : $"{TeamUtil.DisplayName(Team).ToUpperInvariant()} CMD";
            Color col = Team == TeamId.None ? TeamUtil.EnemyColor : TeamUtil.TeamColor(Team);
            chat.PostCommander(sender, col, Clean(text));
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('"', '\'').Replace('\\', '/').Replace('\n', ' ').Replace('\r', ' ');
            return s.Length > 140 ? s.Substring(0, 140) : s;
        }

        private void Log(float sentAt, string state, GroqClient.Result r, int issued, int rejected, string note)
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            try
            {
                var inv = CultureInfo.InvariantCulture;
                string reply = r != null && r.content != null ? Clean(r.content.Length > 1500 ? r.content.Substring(0, 1500) : r.content) : "";
                string line = "{\"t\":" + sentAt.ToString("0.0", inv) + ",\"team\":\"" + Team + "\",\"model\":\"" + Model +
                              "\",\"status\":" + (r != null ? r.status : 0) + ",\"ok\":" + (r != null && r.ok ? "true" : "false") +
                              ",\"seconds\":" + (r != null ? r.seconds.ToString("0.00", inv) : "0") +
                              ",\"promptTokens\":" + (r != null ? r.promptTokens : 0) + ",\"completionTokens\":" + (r != null ? r.completionTokens : 0) +
                              ",\"remainingRequests\":\"" + (r?.remainingRequests ?? "") + "\",\"remainingTokens\":\"" + (r?.remainingTokens ?? "") +
                              "\",\"issued\":" + issued + ",\"rejected\":" + rejected + ",\"note\":\"" + Clean(note) +
                              "\",\"reply\":\"" + reply + "\",\"state\":" + state + "}";
                File.AppendAllText(_logPath, line + "\n");
            }
            catch (Exception) { }
        }
    }
}
