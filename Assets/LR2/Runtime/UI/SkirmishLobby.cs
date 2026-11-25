using System;
using System.Collections;
using System.Collections.Generic;
using LR2.AI;
using LR2.Combat;
using LR2.Core;
using LR2.Game;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LR2.UI
{
    /// <summary>
    /// Command &amp; Conquer style skirmish lobby behind the main menu's PLAY (user 11-19: "just have play, then in that have all
    /// the settings ... 3 way, 4 way, so i can select how many bots and which team they can be in ... a AI vs AI mode ...
    /// make it scrllable ... maybe max 20"; later: "should be larger with more icon"). Left: the roster (you or a spectator
    /// seat, then up to 20 bots, each with its own difficulty and team). Right: mode, map, limits, weapon, OPTIONS and ALL
    /// RULES (the full per-mode rules sub-menu). Built in code with procedural icons (<see cref="LR2Icons"/>).
    /// </summary>
    public class SkirmishLobby : MonoBehaviour
    {
        public CanvasGroup Group { get; private set; }
        public Action OnBack, OnOptions, OnRules;
        public Action<GameModeType> OnStart;
        public Action<ExperimentSessionConfig> OnStartExperiment;
        /// <summary>Mode changed: the menu background cuts to a matching shot.</summary>
        public Action<GameModeType> OnModeChanged;

        private UITheme _theme;
        private GameObject _skirmishRoot;
        private ExperimentsTabView _experimentsView;
        private Button _skirmishTabBtn, _experimentsTabBtn;
        private bool _inExperiments;
        private RectTransform _content;
        private ScrollRect _scroll;
        private TMP_Text _countLabel, _summary;
        private Image _preview, _modeIcon;
        private Button _startButton;
        private readonly List<Action> _refresh = new List<Action>();
        private ModeRuleConfig _rules;
        private Func<GameModeType, MatchSettings> _templateFor;
        private Coroutine _intro;
        private bool _buildingRows; // row widgets are rebuilt, so they register no refresher
        private int _textCanvasWidth, _textCanvasHeight;
        private Vector2 _textCanvasRectSize;
        private float _textCanvasScale = -1f;

        private Color Accent => _theme != null ? _theme.accent : new Color(1f, 0.71f, 0.28f);
        private Color Text => _theme != null ? _theme.text : new Color(1f, 1f, 1f, 0.92f);
        private Color Dim => _theme != null ? _theme.textDim : new Color(1f, 1f, 1f, 0.55f);
        // user 11-19: "make the buttons lil seethrough but still perfectly visible, and let the slomo stuff play on bg"
        private static readonly Color Panel = new Color(0.035f, 0.045f, 0.055f, 0.58f);
        private static readonly Color ButtonBg = new Color(0.14f, 0.16f, 0.18f, 0.62f); // translucent slate: readable on any background, lifts on hover
        private static readonly Color RowColor = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color Chip = new Color(0f, 0f, 0f, 0.42f);
        private static readonly Color[] DiffColors =
        {
            new Color(0.62f, 0.85f, 0.55f), new Color(0.80f, 0.86f, 0.92f), new Color(1f, 0.70f, 0.32f),
            new Color(1f, 0.38f, 0.32f), new Color(0.72f, 0.55f, 1f)
        };
        private static readonly Color[] ArchColors =
        {
            new Color(0.45f, 0.76f, 1.0f), new Color(0.96f, 0.42f, 0.36f),
            new Color(0.75f, 0.56f, 1.0f)
        };
        private static readonly string[] DiffIcons = { "rank1", "rank2", "rank3", "rank4", "adaptive" };
        private static readonly string[] ArchIcons = { "player", "robot", "brain" };
        private static readonly string[] ModeIcons = { "tdm", "ffa", "ctf", "duel" };
        private static readonly string[] PresetIcons = { "duel", "teams2", "teams3", "teams4", "ffa" };

        public static SkirmishLobby Create(Transform parent, Func<GameModeType, MatchSettings> templateFor)
        {
            var go = new GameObject("SkirmishLobby", typeof(RectTransform), typeof(CanvasGroup));
            go.transform.SetParent(parent, false);
            var lobby = go.AddComponent<SkirmishLobby>();
            lobby._templateFor = templateFor;
            lobby.Build();
            return lobby;
        }

        // ------------------------------------------------------------------ build
        private void Build()
        {
            _theme = Resources.Load<UITheme>("LR2/UITheme");
            SkirmishSetup.Load();
            SkirmishSetup.PlayerSpectates = false;
            LoadRules();
            var root = (RectTransform)transform;
            Stretch(root);
            Group = GetComponent<CanvasGroup>();

            // centred (user 11-19 "why is the skirmish menu on the side?"); a lighter full-screen shade keeps the
            // slow-motion background readable around the panels
            var shade = Img(root, "Shade", new Color(0.015f, 0.02f, 0.025f, 0.22f));
            Anchor(shade.rectTransform, 0f, 0f, 1f, 1f);
            var band = Img(root, "ShadeBand", new Color(0.015f, 0.02f, 0.025f, 0.16f));
            Anchor(band.rectTransform, 0.12f, 0f, 0.88f, 1f);

            var title = Label(root, "COMBAT LOBBY", 56, Text, true);
            title.overflowMode = TextOverflowModes.Overflow;
            Anchor(title.rectTransform, 0.135f, 0.89f, 0.44f, 0.965f);
            var sub = Label(root, "SKIRMISH CUSTOM MATCHES  ·  CONTROLLED EXPERIMENTS, AI VS AI BENCH & TURING TELEMETRY", 16, Dim);
            Anchor(sub.rectTransform, 0.136f, 0.854f, 0.865f, 0.888f);

            // B153 Dual Tab Switcher ([ SKIRMISH ] | [ EXPERIMENTS ])
            _skirmishTabBtn = IconBtn(root, "tdm", "SKIRMISH", 22, () => SwitchTab(false), ButtonBg, Accent);
            Anchor((RectTransform)_skirmishTabBtn.transform, 0.48f, 0.892f, 0.668f, 0.962f);
            _experimentsTabBtn = IconBtn(root, "flask", "EXPERIMENTS", 22, () => SwitchTab(true), ButtonBg, Accent);
            Anchor((RectTransform)_experimentsTabBtn.transform, 0.677f, 0.892f, 0.865f, 0.962f);

            _skirmishRoot = Rect(root, "SkirmishTab");
            var sr = _skirmishRoot.rectTransform();
            Stretch(sr);

            // presets: icon tiles
            var presets = Rect(sr, "Presets");
            Anchor(presets.rectTransform(), 0.135f, 0.762f, 0.865f, 0.850f);
            var ph = presets.AddComponent<HorizontalLayoutGroup>();
            ph.spacing = 10f; ph.childForceExpandWidth = true; ph.childControlWidth = true; ph.childControlHeight = true;
            for (int i = 0; i < SkirmishSetup.Presets.Length; i++)
            {
                string preset = SkirmishSetup.Presets[i];
                IconBtn(presets.transform, PresetIcons[i], preset, 22, () => { SkirmishSetup.ApplyPreset(preset); SkirmishSetup.PlayerSpectates = false; LoadRules(); Rebuild(); OnModeChanged?.Invoke(SkirmishSetup.Mode); },
                        ButtonBg, Accent);
            }

            BuildRoster(sr);
            BuildMatchColumn(sr);

            var back = IconBtn(sr, "back", "BACK", 26, () => OnBack?.Invoke(), ButtonBg, Text);
            Anchor((RectTransform)back.transform, 0.135f, 0.035f, 0.245f, 0.1f);
            _startButton = IconBtn(sr, "play", "START", 36, Start_, new Color(Accent.r, Accent.g, Accent.b, 0.9f), new Color(0.05f, 0.05f, 0.05f));
            Anchor((RectTransform)_startButton.transform, 0.665f, 0.03f, 0.865f, 0.11f);
            _startButton.GetComponentInChildren<TMP_Text>().color = new Color(0.05f, 0.05f, 0.05f);
            _summary = Label(sr, "", 19, Dim);
            _summary.alignment = TextAlignmentOptions.MidlineRight;
            Anchor(_summary.rectTransform, 0.255f, 0.035f, 0.655f, 0.1f);

            _experimentsView = ExperimentsTabView.Create(root);
            _experimentsView.OnBack = () => OnBack?.Invoke();
            _experimentsView.OnStartExperiment = cfg => OnStartExperiment?.Invoke(cfg);
            _experimentsView.OnModeChanged = m => OnModeChanged?.Invoke(m);
            SwitchTab(false);

            Rebuild();
            RefreshCanvasText();
            StartCoroutine(RefreshTextAfterInitialCanvasPass());
        }

        public void SwitchTab(bool experiments)
        {
            _inExperiments = experiments;
            if (_skirmishRoot != null) _skirmishRoot.SetActive(!experiments);
            if (_experimentsView != null) _experimentsView.gameObject.SetActive(experiments);
            StyleTabBtn(_skirmishTabBtn, !experiments);
            StyleTabBtn(_experimentsTabBtn, experiments);
            RefreshCanvasText();
        }

        private void StyleTabBtn(Button b, bool active)
        {
            if (b == null) return;
            var img = b.GetComponent<Image>();
            if (img != null) img.color = active ? new Color(Accent.r, Accent.g, Accent.b, 0.90f) : ButtonBg;
            var txt = b.GetComponentInChildren<TMP_Text>(true);
            if (txt != null) txt.color = active ? new Color(0.05f, 0.05f, 0.05f) : Text;
        }

        private void LateUpdate()
        {
            var rectSize = ((RectTransform)transform).rect.size;
            var canvas = GetComponentInParent<Canvas>();
            float scale = canvas != null ? canvas.scaleFactor : 1f;
            if (Screen.width != _textCanvasWidth || Screen.height != _textCanvasHeight ||
                (rectSize - _textCanvasRectSize).sqrMagnitude > 1f || Mathf.Abs(scale - _textCanvasScale) > 0.001f)
                RefreshCanvasText();
        }

        private IEnumerator RefreshTextAfterInitialCanvasPass()
        {
            Vector2 lastSize = Vector2.zero;
            int stableFrames = 0;
            for (int i = 0; i < 8 && stableFrames < 2; i++)
            {
                yield return null;
                Canvas.ForceUpdateCanvases();
                Vector2 size = ((RectTransform)transform).rect.size;
                stableFrames = size.x > 0f && size.y > 0f && (size - lastSize).sqrMagnitude < 0.5f ? stableFrames + 1 : 0;
                lastSize = size;
                RefreshCanvasText();
            }
        }

        private void RefreshCanvasText()
        {
            Canvas.ForceUpdateCanvases();
            _textCanvasWidth = Screen.width;
            _textCanvasHeight = Screen.height;
            _textCanvasRectSize = ((RectTransform)transform).rect.size;
            var canvas = GetComponentInParent<Canvas>();
            _textCanvasScale = canvas != null ? canvas.scaleFactor : 1f;
            for (int i = 0; i < _refresh.Count; i++) _refresh[i]?.Invoke();
            Canvas.ForceUpdateCanvases();
            var labels = GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                if (labels[i] == null) continue;
                labels[i].SetAllDirty();
                labels[i].ForceMeshUpdate(true);
            }
        }

        private void BuildRoster(RectTransform root)
        {
            var panel = Img(root, "Roster", Panel);
            Anchor(panel.rectTransform, 0.135f, 0.125f, 0.567f, 0.76f);
            var pr = panel.rectTransform;

            Icon(pr, "tdm", Accent, 0.025f, 0.915f, 0.07f, 0.985f);
            _countLabel = Label(pr, "", 26, Text, true);
            Anchor(_countLabel.rectTransform, 0.08f, 0.912f, 0.37f, 0.988f);
            Stepper(pr, 0.38f, 0.918f, 0.65f, 0.982f, () => AllArchetypeLabel(), d =>
            {
                int cur = SkirmishSetup.Bots.Count > 0 ? Mathf.Clamp((int)SkirmishSetup.Bots[0].archetype, 0, SkirmishSetup.ArchetypeNames.Length - 1) : 0;
                int next = (cur + d + SkirmishSetup.ArchetypeNames.Length) % SkirmishSetup.ArchetypeNames.Length;
                foreach (var b in SkirmishSetup.Bots) b.archetype = (BotArchetype)next;
                Rebuild();
            }, null, () => SkirmishSetup.Bots.Count > 0 ? ArchIcons[Mathf.Clamp((int)SkirmishSetup.Bots[0].archetype, 0, ArchIcons.Length - 1)] : null);
            Stepper(pr, 0.66f, 0.918f, 0.975f, 0.982f, () => AllDifficultyLabel(), d =>
            {
                int cur = SkirmishSetup.Bots.Count > 0 ? SkirmishSetup.Bots[0].difficulty : 1;
                int next = (cur + d + SkirmishSetup.DifficultyNames.Length) % SkirmishSetup.DifficultyNames.Length;
                foreach (var b in SkirmishSetup.Bots) b.difficulty = next;
                LR2Settings.BotDifficulty = next; // B136: the lobby owns bot difficulty (the Settings panel no longer has its own copy)
                Rebuild();
            }, null, () => SkirmishSetup.Bots.Count > 0 ? DiffIcons[Mathf.Clamp(SkirmishSetup.Bots[0].difficulty, 0, 4)] : null);

            foreach (var (text, x) in new[] { ("SLOT", 0.04f), ("TYPE", 0.25f), ("DIFFICULTY", 0.48f), ("TEAM", 0.72f) })
            {
                var head = Label(pr, text, 14, Dim);
                Anchor(head.rectTransform, x, 0.868f, x + 0.25f, 0.908f);
            }

            var view = Rect(pr, "Viewport");
            var vr = view.rectTransform();
            Anchor(vr, 0.015f, 0.115f, 0.975f, 0.868f);
            view.AddComponent<RectMask2D>();
            var content = Rect(vr, "Content");
            _content = content.rectTransform();
            _content.anchorMin = new Vector2(0f, 1f); _content.anchorMax = new Vector2(1f, 1f); _content.pivot = new Vector2(0.5f, 1f);
            _content.offsetMin = _content.offsetMax = Vector2.zero;
            var vl = content.AddComponent<VerticalLayoutGroup>();
            vl.spacing = 5f; vl.childControlHeight = true; vl.childControlWidth = true; vl.childForceExpandHeight = false;
            vl.padding = new RectOffset(4, 12, 4, 4);
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll = panel.gameObject.AddComponent<ScrollRect>();
            _scroll.viewport = vr; _scroll.content = _content; _scroll.horizontal = false;
            _scroll.movementType = ScrollRect.MovementType.Clamped; _scroll.scrollSensitivity = 40f;

            var sbGo = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            sbGo.transform.SetParent(pr, false);
            sbGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);
            Anchor((RectTransform)sbGo.transform, 0.981f, 0.115f, 0.993f, 0.868f);
            var area = Rect(sbGo.transform, "Sliding Area");
            Stretch(area.rectTransform());
            var handle = Img(area.rectTransform(), "Handle", new Color(Accent.r, Accent.g, Accent.b, 0.75f));
            Stretch(handle.rectTransform);
            var sb = sbGo.GetComponent<Scrollbar>();
            sb.handleRect = handle.rectTransform; sb.targetGraphic = handle; sb.direction = Scrollbar.Direction.BottomToTop;
            _scroll.verticalScrollbar = sb;
            _scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            var add = IconBtn(pr, "plus", "ADD BOT", 24, AddBot, ButtonBg, Accent);
            Anchor((RectTransform)add.transform, 0.025f, 0.02f, 0.37f, 0.098f);
            var balance = IconBtn(pr, "balance", "BALANCE TEAMS", 21, BalanceTeams, ButtonBg, Text);
            Anchor((RectTransform)balance.transform, 0.385f, 0.02f, 0.72f, 0.098f);
            var clear = IconBtn(pr, "trash", "CLEAR", 21, () => { SkirmishSetup.Bots.Clear(); Rebuild(); }, ButtonBg, new Color(1f, 0.45f, 0.4f));
            Anchor((RectTransform)clear.transform, 0.735f, 0.02f, 0.975f, 0.098f);
        }

        private void BuildMatchColumn(RectTransform root)
        {
            var panel = Img(root, "Match", Panel);
            Anchor(panel.rectTransform, 0.577f, 0.125f, 0.865f, 0.76f);
            var pr = panel.rectTransform;
            Icon(pr, "mode", Accent, 0.04f, 0.915f, 0.12f, 0.985f);
            var t = Label(pr, "MATCH", 25, Text, true);
            Anchor(t.rectTransform, 0.14f, 0.912f, 0.9f, 0.988f);

            float y = 0.905f;
            void Row(string icon, string label, Func<string> value, Action<int> step, Func<string> valueIcon = null)
            {
                y -= 0.085f;
                Icon(pr, icon, Dim, 0.05f, y + 0.052f, 0.1f, y + 0.082f);
                var l = Label(pr, label, 16, Dim);
                Anchor(l.rectTransform, 0.115f, y + 0.050f, 0.95f, y + 0.084f);
                Stepper(pr, 0.045f, y, 0.955f, y + 0.050f, value, step, null, valueIcon);
            }
            Row("mode", "MODE", () => SkirmishSetup.ModeNames[ModeIndex()], d =>
            {
                int i = (ModeIndex() + d + SkirmishSetup.Modes.Length) % SkirmishSetup.Modes.Length;
                SkirmishSetup.Mode = SkirmishSetup.Modes[i];
                LoadRules(); Rebuild(); OnModeChanged?.Invoke(SkirmishSetup.Mode);
            }, () => ModeIcons[ModeIndex()]);
            Row("map", "MAP", () => LR2Session.MapDisplayNames[LR2Session.SelectedMapIndex].ToUpperInvariant(), d =>
            {
                int n = LR2Session.MapDisplayNames.Length;
                LR2Session.SelectedMapIndex = (LR2Session.SelectedMapIndex + d + n) % n;
                Rebuild();
            });
            y -= 0.125f;
            _preview = Img(pr, "MapPreview", Color.white);
            _preview.preserveAspect = false;
            Anchor(_preview.rectTransform, 0.05f, y + 0.008f, 0.95f, y + 0.120f);
            Row("score", "SCORE LIMIT", () => $"{_rules.scoreLimit}", d => { _rules.scoreLimit += d * (_rules.scoreLimit < 10 ? 1 : 5); SaveRules(); });
            Row("clock", "TIME LIMIT", () => _rules.timeLimitSeconds <= 0f ? "NONE" : $"{Mathf.RoundToInt(_rules.timeLimitSeconds / 60f)} MIN",
                d => { _rules.timeLimitSeconds = Mathf.Max(60f, _rules.timeLimitSeconds + d * 60f); SaveRules(); });
            Row("respawn", "RESPAWN", () => $"{_rules.respawnDelay:0.0} S", d => { _rules.respawnDelay = Mathf.Max(0.5f, _rules.respawnDelay + d * 0.5f); SaveRules(); });
            Row("drone", "SEEKER DRONES", () => _rules.spawnOrbs ? "ON" : "OFF", d => { _rules.spawnOrbs = !_rules.spawnOrbs; SaveRules(); });
            Row("gun", "YOUR WEAPON", () => LR2Session.PrimaryWeaponNames[LR2Session.SelectedPrimaryWeapon], d =>
            {
                int n = LR2Session.PrimaryWeaponNames.Length;
                LR2Session.SelectedPrimaryWeapon = (LR2Session.SelectedPrimaryWeapon + d + n) % n;
                Rebuild();
            });
            Row("mode", "YOUR SCOPE", () => LR2Session.ScopeNames[LR2Session.SelectedPrimaryScope], d =>
            {
                int n = LR2Session.ScopeNames.Length;
                LR2Session.SelectedPrimaryScope = (LR2Session.SelectedPrimaryScope + d + n) % n;
                Rebuild();
            });

            var opt = IconBtn(pr, "gear", "OPTIONS", 21, () => OnOptions?.Invoke(), ButtonBg, Text);
            Anchor((RectTransform)opt.transform, 0.045f, 0.012f, 0.49f, 0.088f);
            var rules = IconBtn(pr, "rules", "ALL RULES", 21, () => OnRules?.Invoke(), ButtonBg, Text);
            Anchor((RectTransform)rules.transform, 0.51f, 0.012f, 0.955f, 0.088f);
        }

        /// <summary>Redraws the roster, e.g. after the player's callsign changed on the title screen (B131).</summary>
        public void RefreshRoster()
        {
            if (_content != null) Rebuild();
        }

        // ------------------------------------------------------------------ roster
        private void Rebuild()
        {
            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                var old = _content.GetChild(i).gameObject;
                old.SetActive(false); // out of the layout this frame; destroyed at the end of it
                Destroy(old);
            }
            _buildingRows = true;
            SkirmishSetup.PlayerSpectates = false;
            SkirmishSetup.BlindMode = false;
            PlayerRow();
            for (int i = 0; i < SkirmishSetup.Bots.Count; i++) BotRow(i);
            _buildingRows = false;
            foreach (var r in _refresh) r();
            int n = SkirmishSetup.Bots.Count;
            _countLabel.text = $"PLAYERS  <color=#{Hex(Accent)}>{n + 1}</color><size=70%> / {SkirmishSetup.MaxBots + 1}</size>";
            var sprite = Resources.Load<Sprite>(LR2Session.PreviewResourcePath(LR2Session.SelectedMapIndex));
            _preview.sprite = sprite;
            _preview.color = sprite != null ? Color.white : new Color(1f, 1f, 1f, 0.05f);
            bool ok = n >= 1;
            _startButton.interactable = ok;
            _summary.text = ok ? Summary() : "ADD AT LEAST ONE BOT";
        }

        private string Summary()
        {
            int bots = SkirmishSetup.Bots.Count;
            if (!SkirmishSetup.IsTeamGame) return $"YOU  ·  FREE FOR ALL  ·  {bots} BOTS";
            int teams = SkirmishSetup.TeamCount;
            string kind = teams >= 4 ? "4-WAY" : teams == 3 ? "3-WAY" : teams == 2 ? "2 TEAMS" : "1 TEAM";
            return $"YOU  ·  {kind} {SkirmishSetup.ModeNames[ModeIndex()]}  ·  {bots} BOTS";
        }

        private RectTransform Row(Color strip)
        {
            var row = Img(_content, "Row", RowColor);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 72f;
            row.gameObject.AddComponent<CanvasGroup>();
            var s = Img(row.rectTransform, "Strip", strip);
            Anchor(s.rectTransform, 0f, 0f, 0f, 1f);
            s.rectTransform.sizeDelta = new Vector2(7f, 0f);
            s.rectTransform.anchoredPosition = new Vector2(3.5f, 0f);
            return row.rectTransform;
        }

        private void PlayerRow()
        {
            // B153: AI VS AI moved exclusively to the Experiments tab; Skirmish always spawns you in the match.
            var r = Row(TeamUtil.FriendlyColor);
            Icon(r, "player", TeamUtil.FriendlyColor, 0.025f, 0.2f, 0.068f, 0.8f);
            var name = Label(r, $"YOU  <color=#{Hex(Dim)}><size=72%>{LR2Settings.PlayerName}</size></color>", 26, Text, true);
            Anchor(name.rectTransform, 0.075f, 0f, 0.56f, 1f);
            bool teamMode = TeamsMatter;
            if (teamMode) SkirmishSetup.PlayerTeam = TeamId.Alpha;
            Color c = teamMode ? TeamUtil.FriendlyColor : Dim;
            Icon(r, teamMode ? "shield" : "ffa", c, 0.672f, 0.22f, 0.71f, 0.78f);
            var t = Label(r, teamMode ? "ALPHA · YOU" : "FREE FOR ALL", 17, c, true);
            Anchor(t.rectTransform, 0.72f, 0f, 0.97f, 1f);
        }

        private void BotRow(int index)
        {
            var bot = SkirmishSetup.Bots[index];
            bool teams = TeamsMatter;
            Color rowCol = teams && bot.team != TeamId.None ? TeamUtil.TeamColor(bot.team) : TeamUtil.EnemyColor; // FFA bots are red in the match too
            var r = Row(rowCol);
            Icon(r, "bot", rowCol, 0.025f, 0.22f, 0.068f, 0.78f);
            var name = Label(r, $"<color=#{Hex(Dim)}>{index + 1:00}</color>  BOT", 22, Text);
            Anchor(name.rectTransform, 0.075f, 0f, 0.215f, 1f);
            int a0 = Mathf.Clamp((int)bot.archetype, 0, ArchColors.Length - 1);
            Stepper(r, 0.22f, 0.14f, 0.435f, 0.86f, () => SkirmishSetup.ArchetypeNames[a0], d =>
            {
                bot.archetype = (BotArchetype)((a0 + d + SkirmishSetup.ArchetypeNames.Length) % SkirmishSetup.ArchetypeNames.Length);
                Rebuild();
            }, ArchColors[a0], () => ArchIcons[a0]);
            int d0 = Mathf.Clamp(bot.difficulty, 0, DiffColors.Length - 1);
            Stepper(r, 0.442f, 0.14f, 0.658f, 0.86f, () => SkirmishSetup.DifficultyNames[d0], d =>
            {
                bot.difficulty = (bot.difficulty + d + SkirmishSetup.DifficultyNames.Length) % SkirmishSetup.DifficultyNames.Length;
                Rebuild();
            }, DiffColors[d0], () => DiffIcons[d0]);
            TeamStepper(r, () => bot.team, t => bot.team = t);
            var x = IconBtn(r, "trash", "", 16, () => { SkirmishSetup.Bots.Remove(bot); Rebuild(); }, new Color(1f, 0.3f, 0.27f, 0.16f), new Color(1f, 0.5f, 0.45f));
            Anchor((RectTransform)x.transform, 0.935f, 0.16f, 0.995f, 0.84f);
        }

        private bool TeamsMatter => SkirmishSetup.Mode != GameModeType.FreeForAll && SkirmishSetup.Mode != GameModeType.Duel;

        private void TeamStepper(RectTransform r, Func<TeamId> get, Action<TeamId> set)
        {
            if (!TeamsMatter)
            {
                Icon(r, "ffa", TeamUtil.EnemyColor, 0.672f, 0.24f, 0.71f, 0.76f);
                var ffa = Label(r, "FREE FOR ALL", 16, Dim);
                Anchor(ffa.rectTransform, 0.72f, 0f, 0.93f, 1f);
                return;
            }
            var tc = get() == TeamId.None ? TeamUtil.EnemyColor : TeamUtil.TeamColor(get());
            Stepper(r, 0.665f, 0.14f, 0.93f, 0.86f, () => TeamUtil.ShortName(get()), d => { set(SkirmishSetup.NextTeam(get(), d)); Rebuild(); }, tc,
                    () => get() == TeamId.None ? "ffa" : "shield");
        }

        private void AddBot()
        {
            if (SkirmishSetup.Bots.Count >= SkirmishSetup.MaxBots) return;
            var b = new SkirmishBot { difficulty = SkirmishSetup.Bots.Count > 0 ? SkirmishSetup.Bots[SkirmishSetup.Bots.Count - 1].difficulty : 1 };
            b.team = TeamsMatter ? SmallestTeam() : TeamId.None;
            SkirmishSetup.Bots.Add(b);
            Rebuild();
            StartCoroutine(ScrollToBottom());
        }

        private IEnumerator ScrollToBottom()
        {
            yield return null;
            if (_scroll != null) _scroll.verticalNormalizedPosition = 0f;
        }

        /// <summary>The used team with the fewest soldiers (the player counts), or Bravo when only one team is used.</summary>
        private TeamId SmallestTeam()
        {
            var counts = new Dictionary<TeamId, int>();
            if (!SkirmishSetup.PlayerSpectates && SkirmishSetup.PlayerTeam != TeamId.None) counts[SkirmishSetup.PlayerTeam] = 1;
            foreach (var b in SkirmishSetup.Bots)
                if (b.team != TeamId.None) { counts.TryGetValue(b.team, out int n); counts[b.team] = n + 1; }
            if (counts.Count < 2) return counts.ContainsKey(TeamId.Alpha) ? TeamId.Bravo : TeamId.Alpha;
            TeamId best = TeamId.Alpha; int min = int.MaxValue;
            foreach (var kv in counts) if (kv.Value < min) { min = kv.Value; best = kv.Key; }
            return best;
        }

        /// <summary>Deal the bots round-robin over the teams already in use (player counted), so sizes differ by at most one.</summary>
        private void BalanceTeams()
        {
            if (!TeamsMatter) return;
            var used = new List<TeamId>();
            if (!SkirmishSetup.PlayerSpectates && SkirmishSetup.PlayerTeam != TeamId.None) used.Add(SkirmishSetup.PlayerTeam);
            foreach (var b in SkirmishSetup.Bots) if (b.team != TeamId.None && !used.Contains(b.team)) used.Add(b.team);
            if (used.Count < 2) { used.Clear(); used.Add(TeamId.Alpha); used.Add(TeamId.Bravo); }
            var size = new Dictionary<TeamId, int>();
            foreach (var t in used) size[t] = 0;
            if (!SkirmishSetup.PlayerSpectates && size.ContainsKey(SkirmishSetup.PlayerTeam)) size[SkirmishSetup.PlayerTeam] = 1;
            foreach (var b in SkirmishSetup.Bots)
            {
                TeamId pick = used[0];
                foreach (var t in used) if (size[t] < size[pick]) pick = t;
                b.team = pick; size[pick]++;
            }
            Rebuild();
        }

        private string AllArchetypeLabel()
        {
            if (SkirmishSetup.Bots.Count == 0) return "-";
            var a = SkirmishSetup.Bots[0].archetype;
            foreach (var b in SkirmishSetup.Bots) if (b.archetype != a) return "MIXED";
            return SkirmishSetup.ArchetypeNames[Mathf.Clamp((int)a, 0, SkirmishSetup.ArchetypeNames.Length - 1)];
        }

        private string AllDifficultyLabel()
        {
            if (SkirmishSetup.Bots.Count == 0) return "-";
            int d = SkirmishSetup.Bots[0].difficulty;
            foreach (var b in SkirmishSetup.Bots) if (b.difficulty != d) return "MIXED";
            return SkirmishSetup.DifficultyNames[Mathf.Clamp(d, 0, 4)];
        }

        private int ModeIndex() => Mathf.Max(0, Array.IndexOf(SkirmishSetup.Modes, SkirmishSetup.Mode));

        // ------------------------------------------------------------------ rules
        private void LoadRules()
        {
            var template = _templateFor != null ? _templateFor(SkirmishSetup.Mode) : null;
            _rules = template != null ? LR2Session.LoadModeRules(template) : default;
        }

        /// <summary>Saved per mode, so LR2Session.PrepareConfiguredMatch (which re-applies the saved rules) keeps them.</summary>
        private void SaveRules()
        {
            _rules.ClampFor(SkirmishSetup.Mode);
            LR2Session.SaveModeRules(SkirmishSetup.Mode, _rules);
            foreach (var r in _refresh) r();
        }

        /// <summary>The rules sub-menu may have changed them.</summary>
        public void Reload()
        {
            LoadRules();
            Rebuild();
        }

        private void Start_()
        {
            if (!_startButton.interactable) return;
            ExperimentSessionRunner.AbortSession();
            SkirmishSetup.PlayerSpectates = false;
            SkirmishSetup.BlindMode = false;
            SaveRules();
            SkirmishSetup.Save();
            SkirmishSetup.Active = true;
            OnStart?.Invoke(SkirmishSetup.Mode);
        }

        // ------------------------------------------------------------------ intro animation
        /// <summary>Rows slide in one after another (unscaled time: the menu background runs in slow motion).</summary>
        public void PlayIntro()
        {
            if (_intro != null) StopCoroutine(_intro);
            _intro = StartCoroutine(Intro());
        }

        private IEnumerator Intro()
        {
            var rows = new List<(RectTransform rt, CanvasGroup cg)>();
            for (int i = 0; i < _content.childCount; i++)
            {
                var rt = (RectTransform)_content.GetChild(i);
                var cg = rt.GetComponent<CanvasGroup>();
                if (cg != null && rt.gameObject.activeSelf) { cg.alpha = 0f; rows.Add((rt, cg)); }
            }
            float t0 = Time.unscaledTime;
            while (true)
            {
                bool done = true;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].cg == null) continue;
                    float k = Mathf.Clamp01((Time.unscaledTime - t0 - i * 0.03f) / 0.28f);
                    float e = 1f - (1f - k) * (1f - k) * (1f - k);
                    rows[i].cg.alpha = e;
                    if (k < 1f) done = false;
                }
                if (done) break;
                yield return null;
            }
            _intro = null;
        }

        // ------------------------------------------------------------------ widgets
        /// <summary>A "&lt; value &gt;" chip; optional icon in front of the value (re-evaluated on refresh).</summary>
        private void Stepper(RectTransform parent, float x0, float y0, float x1, float y1, Func<string> value, Action<int> step,
                             Color? valueColor = null, Func<string> icon = null)
        {
            var box = Img(parent, "Stepper", Chip);
            Anchor(box.rectTransform, x0, y0, x1, y1);
            // chevron icons, not "<" / ">" text: the heading font has no glyphs for them (user 11-20: "some icons missing
            // from left right buttons")
            var prev = IconBtn(box.rectTransform, "chev-left", "", 27, () => step(-1), new Color(1f, 1f, 1f, 0.06f), Text);
            Anchor((RectTransform)prev.transform, 0f, 0f, 0.15f, 1f);
            var next = IconBtn(box.rectTransform, "chev-right", "", 27, () => step(1), new Color(1f, 1f, 1f, 0.06f), Text);
            Anchor((RectTransform)next.transform, 0.85f, 0f, 1f, 1f);
            Color vc = valueColor ?? Text;
            Image ic = null;
            if (icon != null)
            {
                ic = Icon(box.rectTransform, icon(), vc, 0.17f, 0.18f, 0.27f, 0.82f);
                ic.preserveAspect = true;
            }
            var v = Label(box.rectTransform, value(), 24, vc, true);
            v.enableAutoSizing = true;
            v.fontSizeMin = 12f;
            v.fontSizeMax = 20f;
            v.alignment = TextAlignmentOptions.Center;
            Anchor(v.rectTransform, icon != null ? 0.26f : 0.15f, 0f, 0.85f, 1f);
            if (!_buildingRows)
                _refresh.Add(() =>
                {
                    if (v != null) v.text = value();
                    if (ic != null) { ic.sprite = LR2Icons.Get(icon()); ic.enabled = ic.sprite != null; }
                });
        }

        /// <summary>A button with an icon on the left of its label (icon only when the label is empty).</summary>
        private Button IconBtn(Transform parent, string icon, string text, float size, Action onClick, Color bg, Color iconColor)
        {
            var b = Btn(parent, text, size, onClick, bg);
            if (string.IsNullOrEmpty(icon)) return b;
            var label = b.GetComponentInChildren<TMP_Text>();
            bool iconOnly = string.IsNullOrEmpty(text);
            var ic = Icon((RectTransform)b.transform, icon, iconColor, iconOnly ? 0.2f : 0.06f, 0.2f, iconOnly ? 0.8f : 0.06f, 0.8f);
            ic.preserveAspect = true;
            if (!iconOnly)
            {
                // a square icon slot sized to the button's height
                var rt = ic.rectTransform;
                rt.anchorMin = new Vector2(0f, 0.5f); rt.anchorMax = new Vector2(0f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.sizeDelta = new Vector2(size * 1.25f, size * 1.25f);
                rt.anchoredPosition = new Vector2(size * 0.7f, 0f);
                label.rectTransform.offsetMin = new Vector2(size * 1.6f, 0f);
            }
            return b;
        }

        private Image Icon(RectTransform parent, string name, Color color, float x0, float y0, float x1, float y1)
        {
            var img = Img(parent, "Icon_" + name, color);
            img.sprite = LR2Icons.Get(name);
            img.preserveAspect = true;
            img.raycastTarget = false;
            img.enabled = img.sprite != null;
            Anchor(img.rectTransform, x0, y0, x1, y1);
            return img;
        }

        private Button Btn(Transform parent, string text, float size, Action onClick, Color bg)
        {
            var img = Img(parent, "Btn_" + text, bg);
            var b = img.gameObject.AddComponent<Button>();
            var cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.8f, 1.7f, 1.5f, 2.6f); // tint multiplies the faint background: hover lifts it
            cb.pressedColor = new Color(1.2f, 1.2f, 1.2f, 3.2f);
            cb.selectedColor = Color.white;
            cb.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.08f;
            b.colors = cb;
            b.onClick.AddListener(() => onClick());
            var l = Label(img.rectTransform, text, size, Text, true);
            l.alignment = TextAlignmentOptions.Center;
            Stretch(l.rectTransform);
            return b;
        }

        private TMP_Text Label(Transform parent, string text, float size, Color color, bool heading = false)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (_theme != null && (heading ? _theme.headingFont : _theme.bodyFont) != null) t.font = heading ? _theme.headingFont : _theme.bodyFont;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.richText = true;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.characterSpacing = heading ? 2f : 1f;
            return t;
        }

        private static Image Img(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            return img;
        }

        private static GameObject Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        private static void Anchor(RectTransform rt, float x0, float y0, float x1, float y1)
        {
            rt.anchorMin = new Vector2(x0, y0); rt.anchorMax = new Vector2(x1, y1); rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        private static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);
    }

    internal static class LobbyRectExt
    {
        public static RectTransform rectTransform(this GameObject go) => (RectTransform)go.transform;
    }
}
