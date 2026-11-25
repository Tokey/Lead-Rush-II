using System;
using System.Collections;
using System.Collections.Generic;
using LR2.AI;
using LR2.AI.LLM;
using LR2.Combat;
using LR2.Core;
using LR2.Game;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LR2.UI
{
    /// <summary>
    /// B153: Super-customizable N-match Experimental Session Designer tab inside the lobby
    /// (hosts AI vs AI spectator tests, mixed Player vs Dumb/Human/LLM/Fitted blocks, blind Turing mode,
    /// randomized order, JSON preset save/load, and post-match believability rating).
    /// </summary>
    public class ExperimentsTabView : MonoBehaviour
    {
        public Action OnBack;
        public Action<ExperimentSessionConfig> OnStartExperiment;
        public Action<GameModeType> OnModeChanged;

        private UITheme _theme;
        private ExperimentSessionConfig _cfg;
        private int _selectedMatch;
        private int _templateIdx;
        private string _toastText = "";

        private RectTransform _queueContent;
        private ScrollRect _queueScroll;
        private RectTransform _rosterContent;
        private ScrollRect _rosterScroll;
        private TMP_Text _queueTitle, _rosterTitle, _footerSummary;
        private readonly List<Action> _refresh = new List<Action>();
        private bool _rebuilding;

        private Color Accent => _theme != null ? _theme.accent : new Color(1f, 0.71f, 0.28f);
        private Color Text => _theme != null ? _theme.text : new Color(1f, 1f, 1f, 0.92f);
        private Color Dim => _theme != null ? _theme.textDim : new Color(1f, 1f, 1f, 0.55f);
        private static readonly Color Panel = new Color(0.035f, 0.045f, 0.055f, 0.64f);
        private static readonly Color ButtonBg = new Color(0.14f, 0.16f, 0.18f, 0.68f);
        private static readonly Color RowColor = new Color(1f, 1f, 1f, 0.055f);
        private static readonly Color SelColor = new Color(0.95f, 0.65f, 0.22f, 0.20f);
        private static readonly Color Chip = new Color(0f, 0f, 0f, 0.45f);
        private static readonly string[] ArchShort = { "HUMAN", "DUMB", "LLM" };
        private static readonly Color[] ArchColors =
        {
            new Color(0.45f, 0.76f, 1.0f),
            new Color(0.96f, 0.42f, 0.36f),
            new Color(0.75f, 0.56f, 1.0f)
        };
        private static readonly string[] LlmDepthNames = { "OFF", "COMMANDER", "MICRO", "PILOT" };
        private static readonly int[] HzChoices = { 20, 30, 60 };

        public static ExperimentsTabView Create(Transform parent)
        {
            var go = new GameObject("ExperimentsTabView", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var view = go.AddComponent<ExperimentsTabView>();
            view.Build();
            return view;
        }

        private void Build()
        {
            _theme = Resources.Load<UITheme>("LR2/UITheme");
            _cfg = ExperimentSessionRunner.LoadDraft();
            if (_cfg.matches.Count == 0) _cfg = ExperimentSessionRunner.BuildTemplate(0);
            _selectedMatch = Mathf.Clamp(_selectedMatch, 0, _cfg.matches.Count - 1);

            var root = (RectTransform)transform;
            Anchor(root, 0f, 0f, 1f, 1f);

            BuildTopToolbar(root);
            BuildQueueColumn(root);
            BuildMatchConfigColumn(root);

            var back = IconBtn(root, "back", "BACK", 24, () => OnBack?.Invoke(), ButtonBg, Text);
            Anchor((RectTransform)back.transform, 0.135f, 0.035f, 0.220f, 0.1f);
            var openLogs = IconBtn(root, "rules", "OPEN LOGS", 18, OpenLogsFolder, ButtonBg, Accent);
            Anchor((RectTransform)openLogs.transform, 0.228f, 0.035f, 0.345f, 0.1f);
            var start = IconBtn(root, "flask", "START EXPERIMENT", 28, () =>
            {
                if (_cfg.matches.Count == 0) return;
                ExperimentSessionRunner.SaveDraft(_cfg);
                OnStartExperiment?.Invoke(_cfg);
            }, new Color(Accent.r, Accent.g, Accent.b, 0.92f), new Color(0.05f, 0.05f, 0.05f));
            Anchor((RectTransform)start.transform, 0.65f, 0.03f, 0.865f, 0.105f);
            start.GetComponentInChildren<TMP_Text>().color = new Color(0.05f, 0.05f, 0.05f);

            _footerSummary = Label(root, "", 14, Dim);
            _footerSummary.alignment = TextAlignmentOptions.MidlineRight;
            Anchor(_footerSummary.rectTransform, 0.352f, 0.032f, 0.64f, 0.102f);

            RebuildAll();
        }

        private void OpenLogsFolder()
        {
            try
            {
                string dir = ExperimentPaths.LogsRoot.Replace('/', '\\');
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                _toastText = $"LOGS: {dir}";
            }
            catch (Exception ex)
            {
                _toastText = "OPEN FAILED: " + ex.Message;
            }
            RefreshStatic();
            RebuildAll();
        }

        private void BuildTopToolbar(RectTransform root)
        {
            var bar = Img(root, "ExpToolbar", Panel);
            Anchor(bar.rectTransform, 0.135f, 0.772f, 0.865f, 0.848f);
            var br = bar.rectTransform;

            Stepper(br, 0.01f, 0.16f, 0.34f, 0.84f, () => ExperimentSessionRunner.TemplateNames[_templateIdx], d =>
            {
                _templateIdx = (_templateIdx + d + ExperimentSessionRunner.TemplateNames.Length) % ExperimentSessionRunner.TemplateNames.Length;
                RefreshStatic();
            }, Accent, () => "flask");

            var apply = IconBtn(br, "play", "LOAD TMPL", 16, () =>
            {
                _cfg = ExperimentSessionRunner.BuildTemplate(_templateIdx);
                _selectedMatch = 0;
                _toastText = "LOADED TEMPLATE";
                SaveAndRebuild();
            }, ButtonBg, Accent);
            Anchor((RectTransform)apply.transform, 0.348f, 0.16f, 0.455f, 0.84f);

            var saveBtn = IconBtn(br, "copy", "SAVE JSON", 15, () =>
            {
                string p = ExperimentSessionRunner.SavePresetFile(_cfg);
                _toastText = string.IsNullOrEmpty(p) ? "SAVE FAILED" : "SAVED PRESET JSON";
                RefreshStatic();
            }, ButtonBg, Text);
            Anchor((RectTransform)saveBtn.transform, 0.463f, 0.16f, 0.568f, 0.84f);

            var loadBtn = IconBtn(br, "rules", "LOAD JSON", 15, () =>
            {
                var loaded = ExperimentSessionRunner.LoadLatestPresetFile(out _);
                if (loaded != null && loaded.matches != null && loaded.matches.Count > 0)
                {
                    _cfg = loaded;
                    _selectedMatch = 0;
                    _toastText = "LOADED SAVED JSON";
                    SaveAndRebuild();
                }
                else
                {
                    _toastText = "NO SAVED JSON FOUND";
                    RefreshStatic();
                }
            }, ButtonBg, Dim);
            Anchor((RectTransform)loadBtn.transform, 0.574f, 0.16f, 0.678f, 0.84f);

            Stepper(br, 0.686f, 0.16f, 0.788f, 0.84f, () => _cfg.blindCondition ? "BLIND ON" : "BLIND OFF", d =>
            {
                _cfg.blindCondition = !_cfg.blindCondition; SaveAndRebuild();
            }, _cfg.blindCondition ? new Color(0.75f, 0.56f, 1f) : Dim);

            Stepper(br, 0.794f, 0.16f, 0.896f, 0.84f, () => _cfg.randomizeOrder ? "SHUFFLE" : "IN ORDER", d =>
            {
                _cfg.randomizeOrder = !_cfg.randomizeOrder; SaveAndRebuild();
            }, Text);

            Stepper(br, 0.902f, 0.16f, 0.992f, 0.84f, () => $"{_cfg.telemetryHz} HZ", d =>
            {
                int i = Mathf.Max(0, Array.IndexOf(HzChoices, _cfg.telemetryHz));
                _cfg.telemetryHz = HzChoices[(i + d + HzChoices.Length) % HzChoices.Length];
                SaveAndRebuild();
            }, new Color(0.36f, 0.88f, 0.56f));
        }

        private void BuildQueueColumn(RectTransform root)
        {
            var panel = Img(root, "QueuePanel", Panel);
            Anchor(panel.rectTransform, 0.135f, 0.122f, 0.455f, 0.762f);
            var pr = panel.rectTransform;

            Icon(pr, "rules", Accent, 0.03f, 0.918f, 0.085f, 0.982f);
            _queueTitle = Label(pr, "MATCH QUEUE", 23, Text, true);
            Anchor(_queueTitle.rectTransform, 0.1f, 0.915f, 0.7f, 0.985f);

            var rateTog = Btn(pr, "", 14, () => { _cfg.promptHumanRating = !_cfg.promptHumanRating; SaveAndRebuild(); }, Chip);
            Anchor((RectTransform)rateTog.transform, 0.69f, 0.92f, 0.975f, 0.98f);
            var rtLbl = rateTog.GetComponentInChildren<TMP_Text>();
            _refresh.Add(() => { if (rtLbl != null) { rtLbl.text = _cfg.promptHumanRating ? "RATE: ASK" : "RATE: OFF"; rtLbl.color = _cfg.promptHumanRating ? Accent : Dim; } });

            var view = Rect(pr, "QueueViewport");
            var vr = view.rectTransform();
            Anchor(vr, 0.02f, 0.115f, 0.975f, 0.905f);
            view.AddComponent<RectMask2D>();
            var content = Rect(vr, "Content");
            _queueContent = content.rectTransform();
            _queueContent.anchorMin = new Vector2(0f, 1f); _queueContent.anchorMax = new Vector2(1f, 1f); _queueContent.pivot = new Vector2(0.5f, 1f);
            _queueContent.offsetMin = _queueContent.offsetMax = Vector2.zero;
            var vl = content.AddComponent<VerticalLayoutGroup>();
            vl.spacing = 6f; vl.childControlHeight = true; vl.childControlWidth = true; vl.childForceExpandHeight = false;
            vl.padding = new RectOffset(4, 8, 4, 4);
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _queueScroll = panel.gameObject.AddComponent<ScrollRect>();
            _queueScroll.viewport = vr; _queueScroll.content = _queueContent; _queueScroll.horizontal = false;
            _queueScroll.movementType = ScrollRect.MovementType.Clamped; _queueScroll.scrollSensitivity = 36f;

            var add1 = IconBtn(pr, "plus", "ADD MATCH", 15, () =>
            {
                var baseSpec = CurrentMatch() != null ? CurrentMatch().Clone() : ExperimentSessionRunner.BuildTemplate(3).matches[0];
                baseSpec.label = $"MATCH {_cfg.matches.Count + 1:00}";
                _cfg.matches.Add(baseSpec);
                _selectedMatch = _cfg.matches.Count - 1;
                SaveAndRebuild();
            }, ButtonBg, Accent);
            Anchor((RectTransform)add1.transform, 0.02f, 0.02f, 0.30f, 0.098f);

            var add4H = IconBtn(pr, "player", "+4 HUMAN", 14, () => AppendBatchMatches(BotArchetype.HumanLike, LlmIntegrationMode.Off, 4), ButtonBg, ArchColors[0]);
            Anchor((RectTransform)add4H.transform, 0.31f, 0.02f, 0.56f, 0.098f);

            var add4L = IconBtn(pr, "brain", "+4 LLM", 14, () => AppendBatchMatches(BotArchetype.LlmEnhanced, LlmIntegrationMode.Commander, 4), ButtonBg, ArchColors[2]);
            Anchor((RectTransform)add4L.transform, 0.57f, 0.02f, 0.80f, 0.098f);

            var clr = IconBtn(pr, "trash", "", 15, () =>
            {
                _cfg.matches.Clear();
                var m = ExperimentSessionRunner.MakeMatch("MATCH 01", false, GameModeType.Duel, 0, 10, 180f, LlmIntegrationMode.Off);
                m.bots.Add(new ExperimentBotSlot { archetype = BotArchetype.HumanLike, difficulty = 1, team = TeamId.None });
                _cfg.matches.Add(m);
                _selectedMatch = 0;
                SaveAndRebuild();
            }, ButtonBg, new Color(1f, 0.45f, 0.4f));
            Anchor((RectTransform)clr.transform, 0.815f, 0.02f, 0.98f, 0.098f);
        }

        private void AppendBatchMatches(BotArchetype arch, LlmIntegrationMode llmMode, int count)
        {
            string tag = ArchShort[(int)arch];
            for (int i = 0; i < count; i++)
            {
                var m = ExperimentSessionRunner.MakeMatch($"{tag} #{i + 1}", false, GameModeType.TeamDeathmatch, i % LR2Session.MapScenes.Length, 15, 180f, llmMode);
                m.bots.Add(new ExperimentBotSlot { archetype = arch, difficulty = 1, team = TeamId.Alpha });
                m.bots.Add(new ExperimentBotSlot { archetype = arch, difficulty = 1, team = TeamId.Bravo });
                m.bots.Add(new ExperimentBotSlot { archetype = arch, difficulty = 1, team = TeamId.Bravo });
                _cfg.matches.Add(m);
            }
            _selectedMatch = _cfg.matches.Count - 1;
            SaveAndRebuild();
        }

        private void BuildMatchConfigColumn(RectTransform root)
        {
            var panel = Img(root, "MatchConfigPanel", Panel);
            Anchor(panel.rectTransform, 0.465f, 0.122f, 0.865f, 0.762f);
            var pr = panel.rectTransform;

            Icon(pr, "mode", Accent, 0.025f, 0.918f, 0.07f, 0.982f);
            _rosterTitle = Label(pr, "MATCH CONFIG", 22, Text, true);
            Anchor(_rosterTitle.rectTransform, 0.08f, 0.915f, 0.56f, 0.985f);

            // Role toggle (YOU PLAY vs AI VS AI SPECTATE moves cleanly here!)
            Stepper(pr, 0.57f, 0.92f, 0.975f, 0.98f,
                () => CurrentMatch() != null && CurrentMatch().aiVsAi ? "AI VS AI (SPECTATE)" : "YOU PLAY",
                d =>
                {
                    var m = CurrentMatch(); if (m == null) return;
                    m.aiVsAi = !m.aiVsAi;
                    if (m.aiVsAi && m.bots.Count < 2)
                    {
                        m.bots.Add(new ExperimentBotSlot { archetype = BotArchetype.HumanLike, difficulty = 1, team = TeamId.Alpha });
                        m.bots.Add(new ExperimentBotSlot { archetype = BotArchetype.DumbRobotic, difficulty = 1, team = TeamId.Bravo });
                    }
                    SaveAndRebuild();
                }, new Color(0.75f, 0.56f, 1f), () => CurrentMatch() != null && CurrentMatch().aiVsAi ? "spectate" : "player");

            // 2-column compact stepper rows (4 rows, y: 0.545..0.900)
            void Cell(float x0, float y0, float x1, float y1, string lbl, Func<string> val, Action<int> step, Func<string> icon = null)
            {
                var t = Label(pr, lbl, 12, Dim);
                Anchor(t.rectTransform, x0, y1 - 0.026f, x1, y1);
                Stepper(pr, x0, y0, x1, y1 - 0.026f, val, step, Text, icon);
            }

            Cell(0.025f, 0.815f, 0.485f, 0.900f, "MODE", () =>
            {
                var m = CurrentMatch(); if (m == null) return "-";
                int i = Mathf.Max(0, Array.IndexOf(SkirmishSetup.Modes, m.mode));
                return SkirmishSetup.ModeNames[i];
            }, d =>
            {
                var m = CurrentMatch(); if (m == null) return;
                int i = (Mathf.Max(0, Array.IndexOf(SkirmishSetup.Modes, m.mode)) + d + SkirmishSetup.Modes.Length) % SkirmishSetup.Modes.Length;
                m.mode = SkirmishSetup.Modes[i];
                OnModeChanged?.Invoke(m.mode);
                SaveAndRebuild();
            });

            Cell(0.51f, 0.815f, 0.975f, 0.900f, "MAP", () =>
            {
                var m = CurrentMatch(); if (m == null) return "-";
                return LR2Session.MapDisplayNames[Mathf.Clamp(m.mapIndex, 0, LR2Session.MapDisplayNames.Length - 1)].Split('-')[0].Trim();
            }, d =>
            {
                var m = CurrentMatch(); if (m == null) return;
                int n = LR2Session.MapScenes.Length;
                m.mapIndex = (m.mapIndex + d + n) % n;
                SaveAndRebuild();
            }, () => "map");

            Cell(0.025f, 0.725f, 0.485f, 0.810f, "LLM INTEGRATION DEPTH", () =>
            {
                var m = CurrentMatch(); if (m == null) return "OFF";
                return LlmDepthNames[Mathf.Clamp((int)m.llmMode, 0, 3)];
            }, d =>
            {
                var m = CurrentMatch(); if (m == null) return;
                m.llmMode = (LlmIntegrationMode)(((int)m.llmMode + d + 4) % 4);
                SaveAndRebuild();
            }, () => "brain");

            Cell(0.51f, 0.725f, 0.975f, 0.810f, "SCORE LIMIT", () =>
            {
                var m = CurrentMatch(); if (m == null) return "-";
                return $"{m.scoreLimit} KILLS";
            }, d =>
            {
                var m = CurrentMatch(); if (m == null) return;
                m.scoreLimit = Mathf.Clamp(m.scoreLimit + d * 5, 5, 50);
                SaveAndRebuild();
            }, () => "score");

            Cell(0.025f, 0.635f, 0.485f, 0.720f, "YOUR WEAPON", () =>
            {
                var m = CurrentMatch(); if (m == null) return "-";
                return LR2Session.PrimaryWeaponNames[Mathf.Clamp(m.playerWeapon, 0, LR2Session.PrimaryWeaponNames.Length - 1)];
            }, d =>
            {
                var m = CurrentMatch(); if (m == null) return;
                int n = LR2Session.PrimaryWeaponNames.Length;
                m.playerWeapon = (m.playerWeapon + d + n) % n;
                SaveAndRebuild();
            }, () => "gun");

            Cell(0.51f, 0.635f, 0.975f, 0.720f, "YOUR SCOPE", () =>
            {
                var m = CurrentMatch(); if (m == null) return "-";
                return LR2Session.ScopeNames[Mathf.Clamp(m.playerScope, 0, LR2Session.ScopeNames.Length - 1)];
            }, d =>
            {
                var m = CurrentMatch(); if (m == null) return;
                int n = LR2Session.ScopeNames.Length;
                m.playerScope = (m.playerScope + d + n) % n;
                SaveAndRebuild();
            }, () => "mode");

            Cell(0.025f, 0.545f, 0.485f, 0.630f, "RESPAWN DELAY", () =>
            {
                var m = CurrentMatch(); if (m == null) return "-";
                return $"{m.respawnDelay:0.0} S";
            }, d =>
            {
                var m = CurrentMatch(); if (m == null) return;
                m.respawnDelay = Mathf.Clamp(m.respawnDelay + d * 0.5f, 0.5f, 15f);
                SaveAndRebuild();
            }, () => "respawn");

            Cell(0.51f, 0.545f, 0.975f, 0.630f, "TIME LIMIT", () =>
            {
                var m = CurrentMatch(); if (m == null) return "-";
                return $"{Mathf.RoundToInt(m.timeLimitSeconds / 60f)} MIN";
            }, d =>
            {
                var m = CurrentMatch(); if (m == null) return;
                m.timeLimitSeconds = Mathf.Clamp(m.timeLimitSeconds + d * 60f, 60f, 900f);
                SaveAndRebuild();
            }, () => "clock");

            // Bot roster bar inside match
            var rHead = Label(pr, "BOTS IN THIS MATCH (TYPE / DIFFICULTY / TEAM)", 13, Dim);
            Anchor(rHead.rectTransform, 0.025f, 0.492f, 0.56f, 0.536f);

            Stepper(pr, 0.57f, 0.492f, 0.77f, 0.536f, () => "ALL TYPE", d =>
            {
                var m = CurrentMatch(); if (m == null || m.bots.Count == 0) return;
                int cur = Mathf.Clamp((int)m.bots[0].archetype, 0, ArchShort.Length - 1);
                int next = (cur + d + ArchShort.Length) % ArchShort.Length;
                foreach (var b in m.bots) b.archetype = (BotArchetype)next;
                if ((BotArchetype)next == BotArchetype.LlmEnhanced && m.llmMode == LlmIntegrationMode.Off)
                    m.llmMode = LlmIntegrationMode.Commander;
                SaveAndRebuild();
            }, Accent);

            Stepper(pr, 0.78f, 0.492f, 0.975f, 0.536f, () => "ALL DIFF", d =>
            {
                var m = CurrentMatch(); if (m == null || m.bots.Count == 0) return;
                int next = (m.bots[0].difficulty + d + 5) % 5;
                foreach (var b in m.bots) b.difficulty = next;
                SaveAndRebuild();
            }, Text);

            var rView = Rect(pr, "RosterViewport");
            var rvr = rView.rectTransform();
            Anchor(rvr, 0.02f, 0.115f, 0.975f, 0.486f);
            rView.AddComponent<RectMask2D>();
            var rCont = Rect(rvr, "Content");
            _rosterContent = rCont.rectTransform();
            _rosterContent.anchorMin = new Vector2(0f, 1f); _rosterContent.anchorMax = new Vector2(1f, 1f); _rosterContent.pivot = new Vector2(0.5f, 1f);
            _rosterContent.offsetMin = _rosterContent.offsetMax = Vector2.zero;
            var rvl = rCont.AddComponent<VerticalLayoutGroup>();
            rvl.spacing = 4f; rvl.childControlHeight = true; rvl.childControlWidth = true; rvl.childForceExpandHeight = false;
            rvl.padding = new RectOffset(4, 8, 4, 4);
            rCont.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _rosterScroll = panel.gameObject.AddComponent<ScrollRect>();
            _rosterScroll.viewport = rvr; _rosterScroll.content = _rosterContent; _rosterScroll.horizontal = false;
            _rosterScroll.movementType = ScrollRect.MovementType.Clamped; _rosterScroll.scrollSensitivity = 36f;

            var addBot = IconBtn(pr, "plus", "+ BOT", 16, () =>
            {
                var m = CurrentMatch(); if (m == null || m.bots.Count >= SkirmishSetup.MaxBots) return;
                bool tMode = m.mode != GameModeType.FreeForAll && m.mode != GameModeType.Duel;
                m.bots.Add(new ExperimentBotSlot { archetype = BotArchetype.HumanLike, difficulty = 1, team = tMode ? TeamId.Bravo : TeamId.None });
                SaveAndRebuild();
            }, ButtonBg, Accent);
            Anchor((RectTransform)addBot.transform, 0.025f, 0.02f, 0.28f, 0.098f);

            var bal = IconBtn(pr, "balance", "BALANCE", 15, () =>
            {
                var m = CurrentMatch(); if (m == null) return;
                for (int i = 0; i < m.bots.Count; i++) m.bots[i].team = (i % 2 == 0) ? TeamId.Bravo : TeamId.Alpha;
                SaveAndRebuild();
            }, ButtonBg, Text);
            Anchor((RectTransform)bal.transform, 0.295f, 0.02f, 0.58f, 0.098f);

            var copyAll = IconBtn(pr, "copy", "APPLY ROSTER TO ALL", 14, () =>
            {
                var cur = CurrentMatch(); if (cur == null) return;
                foreach (var m in _cfg.matches)
                {
                    if (m == cur) continue;
                    m.aiVsAi = cur.aiVsAi; m.mode = cur.mode; m.mapIndex = cur.mapIndex; m.scoreLimit = cur.scoreLimit;
                    m.timeLimitSeconds = cur.timeLimitSeconds; m.respawnDelay = cur.respawnDelay; m.llmMode = cur.llmMode;
                    m.playerWeapon = cur.playerWeapon; m.playerScope = cur.playerScope;
                }
                _toastText = "SETTINGS COPIED TO ALL MATCHES";
                SaveAndRebuild();
            }, ButtonBg, Accent);
            Anchor((RectTransform)copyAll.transform, 0.595f, 0.02f, 0.975f, 0.098f);
        }

        private ExperimentMatchSpec CurrentMatch() =>
            _cfg != null && _cfg.matches != null && _selectedMatch >= 0 && _selectedMatch < _cfg.matches.Count
                ? _cfg.matches[_selectedMatch] : null;

        private void SaveAndRebuild()
        {
            ExperimentSessionRunner.SaveDraft(_cfg);
            RebuildAll();
        }

        private void RefreshStatic()
        {
            foreach (var r in _refresh) r();
        }

        private void RebuildAll()
        {
            _rebuilding = true;
            _selectedMatch = Mathf.Clamp(_selectedMatch, 0, Mathf.Max(0, _cfg.matches.Count - 1));
            RebuildQueueRows();
            RebuildRosterRows();
            _rebuilding = false;
            RefreshStatic();

            var cur = CurrentMatch();
            _queueTitle.text = $"MATCH QUEUE  <color=#{Hex(Accent)}>{_cfg.matches.Count}</color>";
            _rosterTitle.text = cur != null ? $"MATCH {_selectedMatch + 1:00}  ·  {cur.label}" : "MATCH CONFIG";

            int nH = 0, nD = 0, nL = 0;
            foreach (var m in _cfg.matches)
                foreach (var b in m.bots)
                {
                    if (b.archetype == BotArchetype.DumbRobotic) nD++;
                    else if (b.archetype == BotArchetype.LlmEnhanced) nL++;
                    else nH++;
                }
            string status = !string.IsNullOrEmpty(_toastText) ? $"{_toastText}  ·  " : "";
            _footerSummary.text = $"{status}{_cfg.matches.Count} MATCHES ({nH} HUMAN, {nD} DUMB, {nL} LLM BOT SLOTS) · {(_cfg.blindCondition ? "BLIND" : "TAGGED")} · {_cfg.telemetryHz} HZ CSV";
        }

        private void RebuildQueueRows()
        {
            for (int i = _queueContent.childCount - 1; i >= 0; i--)
            {
                var g = _queueContent.GetChild(i).gameObject; g.SetActive(false); Destroy(g);
            }
            for (int i = 0; i < _cfg.matches.Count; i++)
            {
                int idx = i;
                var m = _cfg.matches[i];
                bool sel = i == _selectedMatch;
                var row = Img(_queueContent, "MRow", sel ? SelColor : RowColor);
                row.gameObject.AddComponent<LayoutElement>().preferredHeight = 62f;
                var b = row.gameObject.AddComponent<Button>();
                b.onClick.AddListener(() => { _selectedMatch = idx; _toastText = ""; RebuildAll(); });

                var rr = row.rectTransform;
                var num = Label(rr, $"{i + 1:00}", 21, sel ? Accent : Dim, true);
                Anchor(num.rectTransform, 0.03f, 0f, 0.13f, 1f);

                string who = m.aiVsAi ? "AIvAI" : "PLAY";
                string mapShort = LR2Session.MapScenes[Mathf.Clamp(m.mapIndex, 0, LR2Session.MapScenes.Length - 1)].Replace("LR2_", "");
                string archHint = m.bots.Count > 0 ? ArchShort[Mathf.Clamp((int)m.bots[0].archetype, 0, ArchShort.Length - 1)] : "-";
                var title = Label(rr, $"{m.label}\n<color=#{Hex(Dim)}><size=75%>{who} · {m.bots.Count} {archHint} · {mapShort}</size></color>", 16, Text);
                title.textWrappingMode = TextWrappingModes.Normal;
                Anchor(title.rectTransform, 0.14f, 0.05f, 0.67f, 0.95f);

                var dup = IconBtn(rr, "copy", "", 14, () =>
                {
                    var clone = m.Clone(); clone.label += " COPY";
                    _cfg.matches.Insert(idx + 1, clone); _selectedMatch = idx + 1; SaveAndRebuild();
                }, Chip, Accent);
                Anchor((RectTransform)dup.transform, 0.68f, 0.16f, 0.775f, 0.84f);

                var up = Btn(rr, "^", 16, () =>
                {
                    if (idx <= 0) return;
                    (_cfg.matches[idx - 1], _cfg.matches[idx]) = (_cfg.matches[idx], _cfg.matches[idx - 1]);
                    _selectedMatch = idx - 1; SaveAndRebuild();
                }, Chip);
                Anchor((RectTransform)up.transform, 0.785f, 0.16f, 0.875f, 0.84f);

                var del = IconBtn(rr, "trash", "", 14, () =>
                {
                    if (_cfg.matches.Count <= 1) return;
                    _cfg.matches.RemoveAt(idx);
                    _selectedMatch = Mathf.Clamp(_selectedMatch, 0, _cfg.matches.Count - 1);
                    SaveAndRebuild();
                }, new Color(1f, 0.3f, 0.27f, 0.22f), new Color(1f, 0.5f, 0.45f));
                Anchor((RectTransform)del.transform, 0.885f, 0.16f, 0.98f, 0.84f);
            }
        }

        private void RebuildRosterRows()
        {
            for (int i = _rosterContent.childCount - 1; i >= 0; i--)
            {
                var g = _rosterContent.GetChild(i).gameObject; g.SetActive(false); Destroy(g);
            }
            var m = CurrentMatch();
            if (m == null) return;
            bool teamsMatter = m.mode != GameModeType.FreeForAll && m.mode != GameModeType.Duel;

            for (int i = 0; i < m.bots.Count; i++)
            {
                var bot = m.bots[i];
                var row = Img(_rosterContent, "BRow", RowColor);
                row.gameObject.AddComponent<LayoutElement>().preferredHeight = 52f;
                var rr = row.rectTransform;

                var idxLbl = Label(rr, $"{i + 1:00}", 17, Dim, true);
                Anchor(idxLbl.rectTransform, 0.02f, 0f, 0.10f, 1f);

                int ai = Mathf.Clamp((int)bot.archetype, 0, ArchShort.Length - 1);
                Stepper(rr, 0.105f, 0.12f, 0.40f, 0.88f, () => ArchShort[Mathf.Clamp((int)bot.archetype, 0, ArchShort.Length - 1)], d =>
                {
                    int cur = Mathf.Clamp((int)bot.archetype, 0, ArchShort.Length - 1);
                    bot.archetype = (BotArchetype)((cur + d + ArchShort.Length) % ArchShort.Length);
                    if (bot.archetype == BotArchetype.LlmEnhanced && m.llmMode == LlmIntegrationMode.Off)
                        m.llmMode = LlmIntegrationMode.Commander;
                    SaveAndRebuild();
                }, ArchColors[ai]);

                Stepper(rr, 0.41f, 0.12f, 0.695f, 0.88f, () => SkirmishSetup.DifficultyNames[Mathf.Clamp(bot.difficulty, 0, 4)], d =>
                {
                    bot.difficulty = (bot.difficulty + d + 5) % 5; SaveAndRebuild();
                }, Text);

                if (teamsMatter)
                {
                    Color tc = bot.team == TeamId.None ? TeamUtil.EnemyColor : TeamUtil.TeamColor(bot.team);
                    Stepper(rr, 0.705f, 0.12f, 0.91f, 0.88f, () => TeamUtil.ShortName(bot.team), d =>
                    {
                        bot.team = SkirmishSetup.NextTeam(bot.team, d); SaveAndRebuild();
                    }, tc);
                }
                else
                {
                    var ffaLbl = Label(rr, "SOLO / FFA", 14, Dim);
                    Anchor(ffaLbl.rectTransform, 0.715f, 0f, 0.91f, 1f);
                }

                var del = IconBtn(rr, "trash", "", 13, () => { m.bots.Remove(bot); SaveAndRebuild(); }, new Color(1f, 0.3f, 0.27f, 0.18f), new Color(1f, 0.5f, 0.45f));
                Anchor((RectTransform)del.transform, 0.92f, 0.14f, 0.988f, 0.86f);
            }
        }

        // ------------------------------------------------------------------ widgets
        private void Stepper(RectTransform parent, float x0, float y0, float x1, float y1, Func<string> value, Action<int> step, Color? vc = null, Func<string> icon = null)
        {
            var box = Img(parent, "Stepper", Chip);
            Anchor(box.rectTransform, x0, y0, x1, y1);
            var prev = IconBtn(box.rectTransform, "chev-left", "", 22, () => step(-1), new Color(1f, 1f, 1f, 0.06f), Text);
            Anchor((RectTransform)prev.transform, 0f, 0f, 0.16f, 1f);
            var next = IconBtn(box.rectTransform, "chev-right", "", 22, () => step(1), new Color(1f, 1f, 1f, 0.06f), Text);
            Anchor((RectTransform)next.transform, 0.84f, 0f, 1f, 1f);
            Color col = vc ?? Text;
            Image ic = null;
            if (icon != null) { ic = Icon(box.rectTransform, icon(), col, 0.17f, 0.18f, 0.27f, 0.82f); }
            var v = Label(box.rectTransform, value(), 17, col, true);
            v.enableAutoSizing = true; v.fontSizeMin = 10f; v.fontSizeMax = 16f; v.alignment = TextAlignmentOptions.Center;
            Anchor(v.rectTransform, icon != null ? 0.26f : 0.16f, 0f, 0.84f, 1f);
            if (!_rebuilding)
                _refresh.Add(() =>
                {
                    if (v != null) v.text = value();
                    if (ic != null) { ic.sprite = LR2Icons.Get(icon()); ic.enabled = ic.sprite != null; }
                });
        }

        private Button IconBtn(Transform parent, string icon, string text, float size, Action onClick, Color bg, Color iconColor)
        {
            var b = Btn(parent, text, size, onClick, bg);
            if (string.IsNullOrEmpty(icon)) return b;
            var label = b.GetComponentInChildren<TMP_Text>();
            bool iconOnly = string.IsNullOrEmpty(text);
            var ic = Icon((RectTransform)b.transform, icon, iconColor, iconOnly ? 0.2f : 0.06f, 0.2f, iconOnly ? 0.8f : 0.06f, 0.8f);
            if (!iconOnly)
            {
                var rt = ic.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f); rt.pivot = new Vector2(0f, 0.5f);
                rt.sizeDelta = new Vector2(size * 1.15f, size * 1.15f); rt.anchoredPosition = new Vector2(size * 0.55f, 0f);
                label.rectTransform.offsetMin = new Vector2(size * 1.45f, 0f);
            }
            return b;
        }

        private Image Icon(RectTransform parent, string name, Color col, float x0, float y0, float x1, float y1)
        {
            var img = Img(parent, "Icon_" + name, col);
            img.sprite = LR2Icons.Get(name); img.preserveAspect = true; img.raycastTarget = false; img.enabled = img.sprite != null;
            Anchor(img.rectTransform, x0, y0, x1, y1);
            return img;
        }

        private Button Btn(Transform parent, string text, float size, Action onClick, Color bg)
        {
            var img = Img(parent, "Btn_" + text, bg);
            var b = img.gameObject.AddComponent<Button>();
            var cb = b.colors;
            cb.normalColor = Color.white; cb.highlightedColor = new Color(1.7f, 1.6f, 1.4f, 2.4f);
            cb.pressedColor = new Color(1.2f, 1.2f, 1.2f, 3f); cb.selectedColor = Color.white; cb.fadeDuration = 0.08f;
            b.colors = cb;
            b.onClick.AddListener(() => onClick());
            var l = Label(img.rectTransform, text, size, Text, true);
            l.alignment = TextAlignmentOptions.Center; l.enableAutoSizing = true; l.fontSizeMin = 10f; l.fontSizeMax = size;
            Anchor(l.rectTransform, 0f, 0f, 1f, 1f);
            return b;
        }

        private TMP_Text Label(Transform parent, string text, float size, Color color, bool heading = false)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (_theme != null && (heading ? _theme.headingFont : _theme.bodyFont) != null) t.font = heading ? _theme.headingFont : _theme.bodyFont;
            t.text = text; t.fontSize = size; t.color = color; t.richText = true; t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap; t.overflowMode = TextOverflowModes.Ellipsis;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            return t;
        }

        private static Image Img(Transform parent, string name, Color col)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var i = go.GetComponent<Image>(); i.color = col; return i;
        }

        private static GameObject Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false); return go;
        }

        private static void Anchor(RectTransform rt, float x0, float y0, float x1, float y1)
        {
            rt.anchorMin = new Vector2(x0, y0); rt.anchorMax = new Vector2(x1, y1); rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        private static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);
    }
}
