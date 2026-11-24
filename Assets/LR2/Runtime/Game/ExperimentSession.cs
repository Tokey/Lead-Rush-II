using System;
using System.Collections.Generic;
using LR2.AI;
using LR2.AI.LLM;
using LR2.Combat;
using UnityEngine;

namespace LR2.Game
{
    [Serializable]
    public class ExperimentBotSlot
    {
        public BotArchetype archetype = BotArchetype.HumanLike;
        public int difficulty = 1;
        public TeamId team = TeamId.Bravo;

        public ExperimentBotSlot Clone() => new ExperimentBotSlot
        {
            archetype = archetype,
            difficulty = difficulty,
            team = team
        };
    }

    [Serializable]
    public class ExperimentMatchSpec
    {
        public string label = "MATCH 01";
        public bool aiVsAi;
        public GameModeType mode = GameModeType.TeamDeathmatch;
        public int mapIndex;
        public int scoreLimit = 15;
        public float timeLimitSeconds = 180f;
        public float respawnDelay = 2.5f;
        public bool spawnOrbs;
        public int playerWeapon;
        public LlmIntegrationMode llmMode = LlmIntegrationMode.Commander;
        public List<ExperimentBotSlot> bots = new List<ExperimentBotSlot>();

        public ExperimentMatchSpec Clone()
        {
            var c = new ExperimentMatchSpec
            {
                label = label,
                aiVsAi = aiVsAi,
                mode = mode,
                mapIndex = mapIndex,
                scoreLimit = scoreLimit,
                timeLimitSeconds = timeLimitSeconds,
                respawnDelay = respawnDelay,
                spawnOrbs = spawnOrbs,
                playerWeapon = playerWeapon,
                llmMode = llmMode,
                bots = new List<ExperimentBotSlot>(bots.Count)
            };
            foreach (var b in bots) if (b != null) c.bots.Add(b.Clone());
            return c;
        }
    }

    [Serializable]
    public class ExperimentSessionConfig
    {
        public string sessionName = "EXPERIMENT SESSION";
        public bool blindCondition;
        public bool randomizeOrder;
        public bool promptHumanRating = true;
        public int telemetryHz = 20;
        public List<ExperimentMatchSpec> matches = new List<ExperimentMatchSpec>();

        public ExperimentSessionConfig Clone()
        {
            var c = new ExperimentSessionConfig
            {
                sessionName = sessionName,
                blindCondition = blindCondition,
                randomizeOrder = randomizeOrder,
                promptHumanRating = promptHumanRating,
                telemetryHz = telemetryHz,
                matches = new List<ExperimentMatchSpec>(matches.Count)
            };
            foreach (var m in matches) if (m != null) c.matches.Add(m.Clone());
            return c;
        }
    }

    [Serializable]
    public class ExperimentRatingEntry
    {
        public int queueIndex;
        public int matchIndex;
        public string label;
        public string conditionTag;
        public float humanLikenessRating01;
        public string note;
    }

    /// <summary>
    /// B153: Multi-match experimental session builder, template presets, PlayerPrefs/JSON preset persistence,
    /// and runtime cross-scene queue runner (supports mixed Human/Dumb/LLM/Fitted matches + AI vs AI spectator blocks).
    /// </summary>
    public static class ExperimentSessionRunner
    {
        public const string PrefKey = "LR2.v1.ExperimentSession";

        public static readonly string[] TemplateNames =
        {
            "8-MATCH: 4 HUMAN VS 4 LLM",
            "6-MATCH: HUMAN VS DUMB VS LLM (1V1)",
            "4-MATCH: AI VS AI BENCH",
            "BLANK (1 MATCH)"
        };

        public static bool IsSessionActive { get; private set; }
        public static ExperimentSessionConfig ActiveConfig { get; private set; }
        public static int[] MatchOrder { get; private set; } = Array.Empty<int>();
        public static int CurrentQueueIndex { get; private set; }
        public static int SessionNumId { get; private set; }
        public static string RunId { get; private set; } = "";
        public static string SessionId { get; private set; } = "";
        public static string SessionDirPath { get; set; } = "";
        public static string SessionLogPath { get; private set; } = "";
        public static string JustCompletedSummary { get; set; } = "";
        public static readonly List<ExperimentRatingEntry> Ratings = new List<ExperimentRatingEntry>();

        private static Func<GameModeType, MatchSettings> _templateResolver;

        public static string PresetsDirectory => ExperimentPaths.PresetsRoot;

        public static ExperimentMatchSpec CurrentMatchSpec =>
            IsSessionActive && ActiveConfig != null && CurrentQueueIndex >= 0 && CurrentQueueIndex < MatchOrder.Length
                ? ActiveConfig.matches[MatchOrder[CurrentQueueIndex]]
                : null;

        public static int TotalMatches => IsSessionActive && MatchOrder != null ? MatchOrder.Length : 0;

        public static string ConditionTagOf(ExperimentMatchSpec m)
        {
            if (m == null) return "unknown";
            int d = 0, h = 0, l = 0, f = 0;
            foreach (var b in m.bots)
            {
                if (b == null) continue;
                switch (b.archetype)
                {
                    case BotArchetype.DumbRobotic: d++; break;
                    case BotArchetype.LlmEnhanced: l++; break;
                    case BotArchetype.HumanFitted: f++; break;
                    default: h++; break;
                }
            }
            string role = m.aiVsAi ? "aivsai" : "player";
            return $"{role}_h{h}_d{d}_l{l}_f{f}";
        }

        public static ExperimentSessionConfig LoadDraft()
        {
            string json = PlayerPrefs.GetString(PrefKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var cfg = JsonUtility.FromJson<ExperimentSessionConfig>(json);
                    if (cfg != null && cfg.matches != null && cfg.matches.Count > 0) return cfg;
                }
                catch (Exception) { }
            }
            return BuildTemplate(0);
        }

        public static void SaveDraft(ExperimentSessionConfig cfg)
        {
            if (cfg == null) return;
            try
            {
                PlayerPrefs.SetString(PrefKey, JsonUtility.ToJson(cfg));
                PlayerPrefs.Save();
            }
            catch (Exception) { }
        }

        public static string SavePresetFile(ExperimentSessionConfig cfg, string fileSlug = "experiment_preset")
        {
            if (cfg == null) return "";
            try
            {
                System.IO.Directory.CreateDirectory(PresetsDirectory);
                string clean = CleanSlug(string.IsNullOrEmpty(cfg.sessionName) ? fileSlug : cfg.sessionName);
                string path = System.IO.Path.Combine(PresetsDirectory, clean + ".json");
                System.IO.File.WriteAllText(path, JsonUtility.ToJson(cfg, true));
                return path;
            }
            catch (Exception) { return ""; }
        }

        public static ExperimentSessionConfig LoadLatestPresetFile(out string loadedPath)
        {
            loadedPath = "";
            try
            {
                if (!System.IO.Directory.Exists(PresetsDirectory)) return null;
                var files = System.IO.Directory.GetFiles(PresetsDirectory, "*.json");
                if (files.Length == 0) return null;
                Array.Sort(files, (a, b) => System.IO.File.GetLastWriteTimeUtc(b).CompareTo(System.IO.File.GetLastWriteTimeUtc(a)));
                loadedPath = files[0];
                return JsonUtility.FromJson<ExperimentSessionConfig>(System.IO.File.ReadAllText(files[0]));
            }
            catch (Exception) { return null; }
        }

        public static ExperimentSessionConfig BuildTemplate(int idx)
        {
            var cfg = new ExperimentSessionConfig();
            switch (idx)
            {
                case 0: // 8-MATCH: 4 NORMAL (HumanLike) vs 4 LLM
                    cfg.sessionName = "8-MATCH: 4 HUMAN vs 4 LLM";
                    cfg.blindCondition = false;
                    cfg.randomizeOrder = true;
                    cfg.promptHumanRating = true;
                    cfg.telemetryHz = 20;
                    for (int i = 0; i < 4; i++)
                    {
                        var norm = MakeMatch($"NORMAL #{i + 1}", false, GameModeType.TeamDeathmatch, i % LR2Session.MapScenes.Length, 15, 180f, LlmIntegrationMode.Off);
                        norm.bots.Add(new ExperimentBotSlot { archetype = BotArchetype.HumanLike, difficulty = i < 2 ? 1 : 3, team = TeamId.Alpha });
                        norm.bots.Add(new ExperimentBotSlot { archetype = BotArchetype.HumanLike, difficulty = i < 2 ? 1 : 3, team = TeamId.Bravo });
                        norm.bots.Add(new ExperimentBotSlot { archetype = BotArchetype.HumanLike, difficulty = i < 2 ? 1 : 3, team = TeamId.Bravo });
                        cfg.matches.Add(norm);

                        var llm = MakeMatch($"LLM #{i + 1}", false, GameModeType.TeamDeathmatch, i % LR2Session.MapScenes.Length, 15, 180f, LlmIntegrationMode.Commander);
                        llm.bots.Add(new ExperimentBotSlot { archetype = BotArchetype.LlmEnhanced, difficulty = i < 2 ? 1 : 3, team = TeamId.Alpha });
                        llm.bots.Add(new ExperimentBotSlot { archetype = BotArchetype.LlmEnhanced, difficulty = i < 2 ? 1 : 3, team = TeamId.Bravo });
                        llm.bots.Add(new ExperimentBotSlot { archetype = BotArchetype.LlmEnhanced, difficulty = i < 2 ? 1 : 3, team = TeamId.Bravo });
                        cfg.matches.Add(llm);
                    }
                    return cfg;

                case 1: // 6-MATCH: 1v1 BLIND TURING TEST (HumanLike vs DumbRobotic vs LLM)
                    cfg.sessionName = "6-MATCH: 1V1 TURING BLIND";
                    cfg.blindCondition = true;
                    cfg.randomizeOrder = true;
                    cfg.promptHumanRating = true;
                    cfg.telemetryHz = 20;
                    BotArchetype[] archs = { BotArchetype.HumanLike, BotArchetype.HumanLike, BotArchetype.DumbRobotic, BotArchetype.DumbRobotic, BotArchetype.LlmEnhanced, BotArchetype.LlmEnhanced };
                    string[] tags = { "HUMAN A", "HUMAN B", "DUMB A", "DUMB B", "LLM A", "LLM B" };
                    for (int i = 0; i < archs.Length; i++)
                    {
                        var m = MakeMatch($"DUEL {tags[i]}", false, GameModeType.Duel, i % 2, 10, 150f, archs[i] == BotArchetype.LlmEnhanced ? LlmIntegrationMode.Commander : LlmIntegrationMode.Off);
                        m.bots.Add(new ExperimentBotSlot { archetype = archs[i], difficulty = 1 + (i % 2) * 2, team = TeamId.None });
                        cfg.matches.Add(m);
                    }
                    return cfg;

                case 2: // 4-MATCH: AI VS AI SPECTATOR BENCH
                    cfg.sessionName = "4-MATCH: AI VS AI BENCH";
                    cfg.blindCondition = false;
                    cfg.randomizeOrder = false;
                    cfg.promptHumanRating = false;
                    cfg.telemetryHz = 20;
                    for (int i = 0; i < 4; i++)
                    {
                        BotArchetype alphaArch = i >= 2 ? BotArchetype.LlmEnhanced : BotArchetype.DumbRobotic;
                        var m = MakeMatch(i >= 2 ? $"AIvsAI LLM #{i - 1}" : $"AIvsAI DUMB #{i + 1}", true, GameModeType.TeamDeathmatch, i % LR2Session.MapScenes.Length, 25, 120f, i >= 2 ? LlmIntegrationMode.Commander : LlmIntegrationMode.Off);
                        for (int k = 0; k < 4; k++)
                        {
                            m.bots.Add(new ExperimentBotSlot { archetype = alphaArch, difficulty = 1, team = TeamId.Alpha });
                            m.bots.Add(new ExperimentBotSlot { archetype = BotArchetype.HumanLike, difficulty = 1, team = TeamId.Bravo });
                        }
                        cfg.matches.Add(m);
                    }
                    return cfg;

                default: // BLANK
                    cfg.sessionName = "CUSTOM EXPERIMENT";
                    var solo = MakeMatch("MATCH 01", false, GameModeType.Duel, 0, 10, 180f, LlmIntegrationMode.Off);
                    solo.bots.Add(new ExperimentBotSlot { archetype = BotArchetype.HumanLike, difficulty = 1, team = TeamId.None });
                    cfg.matches.Add(solo);
                    return cfg;
            }
        }

        public static ExperimentMatchSpec MakeMatch(string label, bool aiVsAi, GameModeType mode, int mapIndex, int scoreLimit, float timeSec, LlmIntegrationMode llmMode)
        {
            return new ExperimentMatchSpec
            {
                label = label,
                aiVsAi = aiVsAi,
                mode = mode,
                mapIndex = Mathf.Clamp(mapIndex, 0, LR2Session.MapScenes.Length - 1),
                scoreLimit = scoreLimit,
                timeLimitSeconds = timeSec,
                respawnDelay = 2.0f,
                spawnOrbs = false,
                playerWeapon = LR2Session.SelectedPrimaryWeapon,
                llmMode = llmMode,
                bots = new List<ExperimentBotSlot>()
            };
        }

        public static void StartSession(ExperimentSessionConfig cfg, Func<GameModeType, MatchSettings> templateResolver, Action<MatchSettings> launchScene)
        {
            if (cfg == null || cfg.matches == null || cfg.matches.Count == 0) return;
            SaveDraft(cfg);
            _templateResolver = templateResolver;
            ActiveConfig = cfg.Clone();
            Ratings.Clear();
            JustCompletedSummary = "";

            int n = ActiveConfig.matches.Count;
            MatchOrder = new int[n];
            for (int i = 0; i < n; i++) MatchOrder[i] = i;
            if (ActiveConfig.randomizeOrder && n > 1)
            {
                for (int i = n - 1; i > 0; i--)
                {
                    int j = UnityEngine.Random.Range(0, i + 1);
                    (MatchOrder[i], MatchOrder[j]) = (MatchOrder[j], MatchOrder[i]);
                }
            }

            SessionNumId = ExperimentPaths.PeekNextSessionId();
            RunId = ExperimentPaths.NewRunId();
            SessionId = $"{SessionNumId}_{RunId}";
            SessionDirPath = ExperimentPaths.SessionLogDir(SessionNumId, RunId);
            ExperimentPaths.WriteSessionInfoCsv(SessionDirPath, SessionNumId, RunId, JsonUtility.ToJson(ActiveConfig, false));
            SessionLogPath = ExperimentPaths.EnsureSessionLogFile(SessionDirPath, SessionId);
            CurrentQueueIndex = 0;
            IsSessionActive = true;

            PrepareCurrentAndLaunch(launchScene);
        }

        public static void RecordBlindRating(float rating01, string note = "")
        {
            var m = CurrentMatchSpec;
            if (m == null) return;
            Ratings.Add(new ExperimentRatingEntry
            {
                queueIndex = CurrentQueueIndex,
                matchIndex = MatchOrder[CurrentQueueIndex],
                label = m.label,
                conditionTag = ConditionTagOf(m),
                humanLikenessRating01 = Mathf.Clamp01(rating01),
                note = note ?? ""
            });
            try
            {
                if (!string.IsNullOrEmpty(SessionDirPath))
                {
                    System.IO.Directory.CreateDirectory(SessionDirPath);
                    string p = System.IO.Path.Combine(SessionDirPath, $"Ratings_{SessionId}.csv");
                    if (!System.IO.File.Exists(p))
                        System.IO.File.WriteAllText(p, "session_id,run_id,queue_idx,match_idx,label,condition,rating_0_100,note\n");
                    string line = $"{SessionNumId},{RunId},{CurrentQueueIndex + 1},{MatchOrder[CurrentQueueIndex] + 1},\"{(m.label ?? "").Replace('"', '\'')}\",{ConditionTagOf(m)},{Mathf.RoundToInt(rating01 * 100f)},\"{(note ?? "").Replace('"', '\'')}\"\n";
                    System.IO.File.AppendAllText(p, line);
                }
            }
            catch (Exception) { }
        }

        public static void AdvanceToNextMatch()
        {
            if (!IsSessionActive || ActiveConfig == null)
            {
                MatchManager.Instance?.QuitToMenu();
                return;
            }
            CurrentQueueIndex++;
            if (CurrentQueueIndex >= MatchOrder.Length)
            {
                ExperimentPaths.AdvanceSessionId(SessionNumId);
                JustCompletedSummary = $"EXPERIMENT COMPLETE (SESSION #{SessionNumId}_{RunId})  ·  {MatchOrder.Length} MATCHES LOGGED  ·  {SessionDirPath}";
                FinishSessionAndReturnToMenu();
                return;
            }

            ApplyMatchSpecToRuntime(CurrentMatchSpec, out MatchSettings template);
            LR2Session.PrepareConfiguredMatch(template);
            Time.timeScale = 1f;
            UnityEngine.SceneManagement.SceneManager.LoadScene(LR2Session.CurrentMapScene);
        }

        public static void AbortSession()
        {
            if (IsSessionActive)
                JustCompletedSummary = $"EXPERIMENT ENDED EARLY ({CurrentQueueIndex}/{MatchOrder.Length})  ·  DATA SAVED TO {SessionDirPath}";
            FinishSessionAndReturnToMenu();
        }

        private static void FinishSessionAndReturnToMenu()
        {
            IsSessionActive = false;
            SkirmishSetup.BlindMode = false;
            SkirmishSetup.Active = false;
            LLMCommanderConfig.ExperimentModeOverride = null;
            Time.timeScale = 1f;
            LR2Session.ActiveSettings = null;
            if (Application.CanStreamedLevelBeLoaded(LR2Session.MenuScene))
                UnityEngine.SceneManagement.SceneManager.LoadScene(LR2Session.MenuScene);
        }

        private static void PrepareCurrentAndLaunch(Action<MatchSettings> launchScene)
        {
            ApplyMatchSpecToRuntime(CurrentMatchSpec, out MatchSettings template);
            launchScene?.Invoke(template);
        }

        private static void ApplyMatchSpecToRuntime(ExperimentMatchSpec m, out MatchSettings template)
        {
            template = _templateResolver != null ? _templateResolver(m.mode) : null;
            LR2Session.SelectedMapIndex = m.mapIndex;
            LR2Session.SelectedPrimaryWeapon = m.playerWeapon;

            ModeRuleConfig rc = LR2Session.LoadModeRules(template);
            rc.scoreLimit = Mathf.Max(1, m.scoreLimit);
            rc.timeLimitSeconds = Mathf.Max(30f, m.timeLimitSeconds);
            rc.respawnDelay = Mathf.Clamp(m.respawnDelay, 0.5f, 15f);
            rc.spawnOrbs = m.spawnOrbs;
            rc.countdownSeconds = 3f;
            LR2Session.SaveModeRules(m.mode, rc);

            SkirmishSetup.Mode = m.mode;
            SkirmishSetup.PlayerSpectates = m.aiVsAi;
            SkirmishSetup.BlindMode = ActiveConfig != null && ActiveConfig.blindCondition;
            SkirmishSetup.PlayerTeam = (m.mode == GameModeType.FreeForAll || m.mode == GameModeType.Duel || m.aiVsAi) ? TeamId.None : TeamId.Alpha;
            SkirmishSetup.Bots.Clear();
            foreach (var b in m.bots)
            {
                if (b == null) continue;
                SkirmishSetup.Bots.Add(new SkirmishBot
                {
                    archetype = b.archetype,
                    difficulty = Mathf.Clamp(b.difficulty, 0, 4),
                    team = b.team
                });
            }
            if (SkirmishSetup.Bots.Count == 0)
                SkirmishSetup.Bots.Add(new SkirmishBot { archetype = BotArchetype.HumanLike, difficulty = 1, team = SkirmishSetup.PlayerTeam == TeamId.None ? TeamId.None : TeamId.Bravo });
            SkirmishSetup.Active = true;

            LLMCommanderConfig.ExperimentModeOverride = m.llmMode;
        }

        private static string CleanSlug(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "exp";
            var chars = new char[Mathf.Min(raw.Length, 28)];
            int len = 0;
            foreach (char ch in raw)
            {
                if (len >= chars.Length) break;
                chars[len++] = char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_';
            }
            return new string(chars, 0, len).Trim('_');
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            IsSessionActive = false;
            ActiveConfig = null;
            MatchOrder = Array.Empty<int>();
            CurrentQueueIndex = 0;
            SessionNumId = 0;
            RunId = "";
            SessionId = "";
            SessionDirPath = "";
            SessionLogPath = "";
            JustCompletedSummary = "";
            Ratings.Clear();
        }
    }
}
