using System.Collections;
using System.Collections.Generic;
using LR2.Core;
using LR2.Game;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LR2.UI
{
    /// <summary>Main menu: pick a map (with preview thumbnail), customize per-mode rules, adjust settings, and deploy.</summary>
    public class MainMenuController : MonoBehaviour
    {
        [Header("Modes")]
        public MatchSettings duelSettings;
        public MatchSettings ctfSettings;
        public MatchSettings ffaCtfSettings;
        public MatchSettings ffaSettings;
        public MatchSettings tdmSettings;

        [Header("Buttons")]
        public Button duelButton;
        public Button ctfButton;
        public Button ffaCtfButton;
        public Button ffaButton;
        public Button tdmButton;
        public Button modeRulesButton;
        public Button settingsButton;
        public Button quitButton;
        public Button settingsBackButton;

        [Header("Panels")]
        public CanvasGroup mainPanel;
        public CanvasGroup deployPanel;
        public CanvasGroup modeRulesPanel;
        public CanvasGroup settingsPanel;
        public CanvasGroup loadingPanel;

        [Header("Layer 2 Deployment Lobby")]
        public TMP_Text deployModeTitleText;
        public TMP_Text deployModeDescText;
        public Image deployMapPreviewImage;
        public Button deployWeaponPrevButton;
        public Button deployWeaponNextButton;
        public TMP_Text deployWeaponValueText;
        public TMP_Text deployWeaponInfoText;
        public Button deployMapPrevButton;
        public Button deployMapNextButton;
        public TMP_Text deployMapValueText;
        public Button deployDiffPrevButton;
        public Button deployDiffNextButton;
        public TMP_Text deployDiffValueText;
        public Button deployBotTeamPrevButton;
        public Button deployBotTeamNextButton;
        public TMP_Text deployBotTeamValueText;
        public Slider deployScoreSlider;
        public TMP_Text deployScoreValueText;
        public Slider deployBotsSlider;
        public TMP_Text deployBotsValueText;
        public Button deployStartButton;
        public Button deployRulesButton;
        public Button deployBackButton;

        [Header("Map Preview Card")]
        public Image mapPreviewImage;
        public TMP_Text mapPreviewTitle;
        public TMP_Text mapPreviewDescription;
        public TMP_Text mapPreviewIndexText;
        public TMP_Text mapPreviewStatusText;
        public Button mapPrevButton;
        public Button mapNextButton;

        [Header("Mode Rules Customizer")]
        public Button modeRulesDuelTab;
        public Button modeRulesCtfTab;
        public Button modeRulesFfaTab;
        public Button modeRulesTdmTab;
        public Button modeRulesBackButton;
        public Button modeRulesResetButton;
        public Button modeRulesPlayButton;
        public TMP_Text modeRulesTitleText;
        public Slider rulesScoreLimitSlider;
        public TMP_Text rulesScoreLimitValue;
        public Slider rulesTimeLimitSlider;
        public TMP_Text rulesTimeLimitValue;
        public Slider rulesRespawnSlider;
        public TMP_Text rulesRespawnValue;
        public Slider rulesCountdownSlider;
        public TMP_Text rulesCountdownValue;
        public Button rulesBotDiffPrevButton;
        public Button rulesBotDiffNextButton;
        public TMP_Text rulesBotDiffValueText;
        public Button rulesBotTeamPrevButton;
        public Button rulesBotTeamNextButton;
        public TMP_Text rulesBotTeamValueText;
        public Slider rulesEnemyBotsSlider;
        public TMP_Text rulesEnemyBotsValue;
        public Slider rulesFriendlyBotsSlider;
        public TMP_Text rulesFriendlyBotsValue;
        public Toggle rulesSeekerDronesToggle;
        public Slider rulesMaxOrbsSlider;
        public TMP_Text rulesMaxOrbsValue;
        public Slider rulesOrbIntervalSlider;
        public TMP_Text rulesOrbIntervalValue;

        [Header("Loading Anim")]
        public RectTransform loadingSpinner;
        public Image loadingBarFill;
        public TMP_Text loadingStatusText;

        [Header("Bot Difficulty Roster")]
        public Button difficultyPrevButton;
        public Button difficultyNextButton;
        public TMP_Text difficultyValueText;
        public TMP_Text versionText;
        public string mapScene = LR2Session.MapScene;

        private static readonly string[] DifficultyLabels = { "RECRUIT", "REGULAR", "HARDENED", "VETERAN", "ADAPTIVE" };
        private static readonly string[] WeaponBlurbs =
        {
            "5.56x45 MK18  ·  CRISP IRON SIGHTS · TIGHT CLOSE-QUARTERS HANDLING",
            "5.45x39 ASSAULT RIFLE  ·  CONTROLLABLE AUTO · BALANCED ERGONOMICS",
            "5.45x39 KALASHNIKOV  ·  HIGH RATE OF FIRE · CRISP IRON-SIGHT BURST",
            "7.62x51 BATTLE RIFLE  ·  HIGH-CALIBER STOPPING POWER · HEAVY KICK"
        };

        private bool _isLoading;
        private bool _loadingRules;
        private GameModeType _editingMode = GameModeType.Duel;
        private GameModeType _deployMode = GameModeType.Duel;
        private ModeRuleConfig _editingRules;
        private CanvasGroup _activeLayer;
        private readonly Dictionary<CanvasGroup, Coroutine> _layerAnims = new Dictionary<CanvasGroup, Coroutine>();

        private readonly List<Texture2D> _generatedTexs = new List<Texture2D>();
        private readonly List<Sprite> _generatedSprites = new List<Sprite>();
        private Sprite[] _cachedMapSprites;

        private SkirmishLobby _lobby;
        private CanvasGroup _settingsReturn;
        private bool _rulesFromLobby;

        private string _duelBaseLabel;
        private string _ctfBaseLabel;
        private string _ffaBaseLabel;
        private string _tdmBaseLabel;

        private void Start()
        {
            Time.timeScale = 1f;
            AudioListener.pause = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            var sp = settingsPanel != null ? settingsPanel.GetComponentInChildren<SettingsPanel>(true) : null;
            if (sp != null) sp.Load();
            LR2Settings.Apply();

            Show(mainPanel, true);
            Show(deployPanel, false);
            Show(modeRulesPanel, false);
            Show(settingsPanel, false);
            Show(loadingPanel, false);
            _activeLayer = mainPanel;
            if (versionText != null) versionText.text = $"LR2.{Application.version}";

            CacheBaseModeLabels();
            LR2.World.MenuCinematic.Ensure();
            LR2.World.SkyCloudMotion.Ensure();
            BuildSkirmishLobby();
            HideLegacyBotDifficultyControls();
            BuildTitleTiles();

            if (duelButton != null && _lobby == null) duelButton.onClick.AddListener(() => OpenDeployLobbyOrPlay(GameModeType.Duel, duelSettings));
            if (ctfButton != null) ctfButton.onClick.AddListener(() => OpenDeployLobbyOrPlay(GameModeType.CaptureTheFlag, ctfSettings));
            if (ffaButton != null) ffaButton.onClick.AddListener(() => OpenDeployLobbyOrPlay(GameModeType.FreeForAll, ffaSettings));
            if (tdmButton != null) tdmButton.onClick.AddListener(() => OpenDeployLobbyOrPlay(GameModeType.TeamDeathmatch, tdmSettings));
            if (modeRulesButton != null) modeRulesButton.onClick.AddListener(() => OpenModeRules(_editingMode));
            if (settingsButton != null) settingsButton.onClick.AddListener(() => { SwitchLayer(settingsPanel, 1f); OnMenuShot?.Invoke(LR2.World.MenuCinematic.OptionsShot); });
            if (settingsBackButton != null) settingsBackButton.onClick.AddListener(ReturnFromSettings);
            if (quitButton != null) quitButton.onClick.AddListener(Quit);

            if (mapPrevButton != null) mapPrevButton.onClick.AddListener(() => StepMap(-1));
            if (mapNextButton != null) mapNextButton.onClick.AddListener(() => StepMap(1));

            if (difficultyPrevButton != null) difficultyPrevButton.onClick.AddListener(() => StepDifficulty(-1));
            if (difficultyNextButton != null) difficultyNextButton.onClick.AddListener(() => StepDifficulty(1));

            BindDeployLobbyUI();
            BindModeRulesUI();
            SelectMap(LR2Session.SelectedMapIndex);
            RefreshDifficultyLabel();
            RefreshModeButtons();
        }

        /// <summary>
        /// User 11-19: "change the menu, just have play, then in that have all the settings". The first mode button becomes
        /// PLAY and opens the skirmish lobby; the other mode buttons and MODE RULES go (the rules live in the lobby's ALL
        /// RULES sub-menu); OPTIONS and QUIT move up under PLAY.
        /// </summary>
        private void BuildSkirmishLobby()
        {
            SkirmishSetup.Active = false;
            if (mainPanel == null || duelButton == null || mainPanel.transform.parent == null) return;
            _lobby = SkirmishLobby.Create(mainPanel.transform.parent, mode => SettingsFor(mode) ?? duelSettings);
            Show(_lobby.Group, false);
            _lobby.OnBack = () => { SwitchLayer(mainPanel, -1f); OnMenuShot?.Invoke(-1); };
            _lobby.OnOptions = () => { _settingsReturn = _lobby.Group; SwitchLayer(settingsPanel, 1f); OnMenuShot?.Invoke(LR2.World.MenuCinematic.OptionsShot); };
            _lobby.OnRules = () => { _rulesFromLobby = true; OpenModeRules(SkirmishSetup.Mode); };
            _lobby.OnStart = mode => Play(SettingsFor(mode) ?? duelSettings);
            _lobby.OnModeChanged = mode => OnMenuShot?.Invoke((int)mode);
            duelButton.onClick.AddListener(() => { SwitchLayer(_lobby.Group, 1f); _lobby.PlayIntro(); OnMenuShot?.Invoke((int)SkirmishSetup.Mode); });
            _duelBaseLabel = "PLAY";

            var hide = new[] { ctfButton, ffaCtfButton, ffaButton, tdmButton, modeRulesButton };
            var keep = new[] { duelButton, settingsButton, quitButton };
            var parent = duelButton.transform.parent;
            var slots = new List<float>();
            bool sameAnchors = true;
            var a0 = (RectTransform)duelButton.transform;
            foreach (var b in hide)
                if (b != null && b.transform.parent == parent) slots.Add(((RectTransform)b.transform).anchoredPosition.y);
            foreach (var b in keep)
            {
                if (b == null || b.transform.parent != parent) continue;
                var rt = (RectTransform)b.transform;
                slots.Add(rt.anchoredPosition.y);
                if (rt.anchorMin != a0.anchorMin || rt.anchorMax != a0.anchorMax) sameAnchors = false;
            }
            foreach (var b in hide) if (b != null) b.gameObject.SetActive(false);
            // difficulty is set per bot in the lobby now: the old main-menu stepper goes (user 11-19)
            foreach (var t in mainPanel.GetComponentsInChildren<Transform>(true))
                if (t.name == "BotDifficultyLabel" || t.name == "BotDifficultyRoster") t.gameObject.SetActive(false);
            // compact the column when the buttons are placed by hand (a layout group compacts it by itself)
            if (!sameAnchors || parent.GetComponent<LayoutGroup>() != null || slots.Count < 3) return;
            slots.Sort((x, y) => y.CompareTo(x));
            float step = (slots[0] - slots[slots.Count - 1]) / (slots.Count - 1);
            int k = 0;
            foreach (var b in keep)
            {
                if (b == null || b.transform.parent != parent) continue;
                var rt = (RectTransform)b.transform;
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, slots[0] - step * k++);
            }
        }

        /// <summary>
        /// B130 (user 11-20): "remove the map selection thing from main menu thats not needed anymore, maybe place the avatar
        /// there ... in the main menu the black bg tint may not be needed ... make the play settings etc buttons a bit cooler?
        /// maybe tiles?". The map card and the dark left shade go (maps are picked in the lobby). PLAY, SETTINGS and QUIT
        /// become tiles with an icon, a title and a line saying what is inside. <see cref="LR2.World.MenuCinematic"/> films the
        /// hero on the right, where the card was.
        /// </summary>
        private void BuildTitleTiles()
        {
            if (mainPanel == null || _lobby == null) return;
            var card = mainPanel.transform.Find("MapPreviewCard");
            if (card != null) card.gameObject.SetActive(false);
            var shade = mainPanel.transform.parent != null ? mainPanel.transform.parent.Find("LeftShade") : null;
            if (shade != null) shade.gameObject.SetActive(false);
            var theme = Resources.Load<UITheme>("LR2/UITheme");
            Color accent = theme != null ? theme.accent : new Color(1f, 0.71f, 0.28f);
            float top = 252f;
            top = Tile(duelButton, "play", "PLAY", "SKIRMISH  ·  1V1 TO 4-WAY  ·  AI VS AI", 128f, top, accent, theme, true);
            top = Tile(settingsButton, "gear", "SETTINGS", "GRAPHICS  ·  CONTROLS  ·  AUDIO", 86f, top, accent, theme, false);
            Tile(quitButton, "power", "QUIT", "BACK TO THE DESKTOP", 86f, top, accent, theme, false);
            BuildCallsignField(theme, accent);
        }

        /// <summary>
        /// B131 (user 11-20: "a place to put our names in the game, that will be used throuout instead of operator"): a
        /// CALLSIGN field in the top-right corner of the title screen. It writes <see cref="LR2Settings.PlayerName"/>, which
        /// names the player's soldier in every match (kill feed, scoreboard, chat, killcam) and shows next to YOU in the lobby.
        /// </summary>
        private void BuildCallsignField(UITheme theme, Color accent)
        {
            if (mainPanel == null || mainPanel.transform.Find("Callsign") != null) return;
            var root = new GameObject("Callsign", typeof(RectTransform));
            root.transform.SetParent(mainPanel.transform, false);
            var rt = (RectTransform)root.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(420f, 100f);
            rt.anchoredPosition = new Vector2(-48f, -38f);

            var capGo = new GameObject("Caption", typeof(RectTransform));
            capGo.transform.SetParent(root.transform, false);
            var cap = capGo.AddComponent<TextMeshProUGUI>();
            if (theme != null && theme.headingFont != null) cap.font = theme.headingFont;
            cap.text = "CALLSIGN"; // user 11-20: no hint line
            cap.richText = true;
            cap.fontSize = 17f;
            cap.color = accent;
            cap.characterSpacing = 3f;
            cap.alignment = TextAlignmentOptions.BottomLeft;
            cap.textWrappingMode = TextWrappingModes.NoWrap;
            cap.raycastTarget = false;
            var crt = cap.rectTransform;
            crt.anchorMin = new Vector2(0f, 0.66f); crt.anchorMax = new Vector2(1f, 1f);
            crt.offsetMin = new Vector2(2f, 0f); crt.offsetMax = new Vector2(0f, -4f);

            var go = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
            go.name = "CallsignField";
            go.transform.SetParent(root.transform, false);
            var frt = (RectTransform)go.transform;
            frt.anchorMin = new Vector2(0f, 0f); frt.anchorMax = new Vector2(1f, 0.62f);
            frt.pivot = new Vector2(0.5f, 0.5f);
            frt.offsetMin = frt.offsetMax = Vector2.zero;
            var bg = go.GetComponent<Image>();
            if (bg != null) { bg.sprite = null; bg.color = new Color(0.03f, 0.036f, 0.043f, 0.74f); }
            TileImage(go.transform, "Edge", null, accent, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(3f, 0f));
            var icon = TileImage(go.transform, "Icon", LR2Icons.Get("player"), new Color(1f, 1f, 1f, 0.78f), new Vector2(0f, 0.5f),
                                 new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(32f, 0f), new Vector2(30f, 30f));
            icon.preserveAspect = true;

            var input = go.GetComponent<TMP_InputField>();
            if (input.textViewport != null)
            {
                input.textViewport.offsetMin = new Vector2(60f, 4f);
                input.textViewport.offsetMax = new Vector2(-16f, -4f);
            }
            if (input.textComponent is TextMeshProUGUI txt)
            {
                if (theme != null && theme.headingFont != null) txt.font = theme.headingFont;
                txt.fontSize = 27f;
                txt.color = Color.white;
                txt.characterSpacing = 2f;
                txt.alignment = TextAlignmentOptions.MidlineLeft;
            }
            if (input.placeholder is TextMeshProUGUI ph)
            {
                if (theme != null && theme.bodyFont != null) ph.font = theme.bodyFont;
                ph.text = "TYPE YOUR NAME";
                ph.fontSize = 22f;
                ph.fontStyle = FontStyles.Normal;
                ph.color = new Color(1f, 1f, 1f, 0.32f);
                ph.alignment = TextAlignmentOptions.MidlineLeft;
            }
            input.characterLimit = 16;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.customCaretColor = true;
            input.caretColor = accent;
            input.caretWidth = 3;
            input.selectionColor = new Color(accent.r, accent.g, accent.b, 0.35f);
            input.onValidateInput = (text, index, ch) =>
                ch < 128 && (char.IsLetterOrDigit(ch) || ch == ' ' || ch == '_' || ch == '-' || ch == '.') ? ch : '\0';
            input.SetTextWithoutNotify(LR2Settings.PlayerName);
            // re-run OnEnable now that the text component is set: it builds the caret and the selection highlight
            input.enabled = false;
            input.enabled = true;
            input.onEndEdit.AddListener(v =>
            {
                string clean = CleanCallsign(v);
                if (clean != LR2Settings.PlayerName) LR2Settings.PlayerName = clean;
                input.SetTextWithoutNotify(clean);
                if (_lobby != null) _lobby.RefreshRoster();
            });
        }

        /// <summary>Trimmed, single-spaced, at most 16 characters; empty falls back to the old default.</summary>
        public static string CleanCallsign(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Operator";
            var parts = raw.Trim().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            string s = string.Join(" ", parts);
            return s.Length > 16 ? s.Substring(0, 16).TrimEnd() : s;
        }

        private static float Tile(Button b, string icon, string title, string subtitle, float h, float top, Color accent,
                                  UITheme theme, bool primary)
        {
            if (b == null || !b.gameObject.activeSelf) return top;
            float w = primary ? 580f : 520f;
            var rt = (RectTransform)b.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(66f, top);

            var bg = b.GetComponent<Image>();
            if (bg != null) bg.color = primary ? new Color(0.10f, 0.08f, 0.045f, 0.74f) : new Color(0.03f, 0.036f, 0.043f, 0.62f);
            var cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.7f, 1.6f, 1.4f, 1.25f);
            cb.pressedColor = new Color(2.1f, 1.9f, 1.6f, 1.3f);
            cb.selectedColor = cb.normalColor; // a click selects the button; only hover lights the tile
            cb.fadeDuration = 0.08f;
            b.colors = cb;

            // the builder's hover bar (UIButtonFX scales it in) becomes a full-height stripe
            if (b.transform.Find("Accent") is RectTransform bar)
            {
                bar.anchorMin = new Vector2(0f, 0f); bar.anchorMax = new Vector2(0f, 1f);
                bar.pivot = new Vector2(0f, 0.5f);
                bar.sizeDelta = new Vector2(primary ? 10f : 8f, 0f);
                bar.anchoredPosition = Vector2.zero;
            }
            var fx = b.GetComponent<UIButtonFX>();
            if (fx != null) fx.hoverScale = 1.015f;

            TileImage(b.transform, "TileEdge", null, new Color(accent.r, accent.g, accent.b, primary ? 1f : 0.35f),
                      new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(3f, 0f));

            float iconSize = Mathf.Round(h * (primary ? 0.42f : 0.40f));
            float iconX = 22f + iconSize * 0.5f + (primary ? 8f : 4f);
            Color iconIdle = primary ? accent : new Color(1f, 1f, 1f, 0.82f);
            var ic = TileImage(b.transform, "TileIcon", LR2Icons.Get(icon), iconIdle, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                               new Vector2(0.5f, 0.5f), new Vector2(iconX, 0f), new Vector2(iconSize, iconSize));
            ic.preserveAspect = true;

            float textX = iconX + iconSize * 0.5f + 22f;
            var labelTf = b.transform.Find("Label");
            var label = labelTf != null ? labelTf.GetComponent<TMP_Text>() : b.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                label.text = title;
                label.fontSize = primary ? 46f : 31f;
                label.alignment = TextAlignmentOptions.BottomLeft;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                label.overflowMode = TextOverflowModes.Overflow;
                var lt = label.rectTransform;
                lt.anchorMin = new Vector2(0f, 0.44f); lt.anchorMax = new Vector2(1f, 1f);
                lt.pivot = new Vector2(0f, 0.5f);
                lt.offsetMin = new Vector2(textX, 0f); lt.offsetMax = new Vector2(-60f, -6f);
            }
            var subTf = b.transform.Find("TileSubtitle");
            var sub = subTf != null ? subTf.GetComponent<TextMeshProUGUI>() : null;
            if (sub == null)
            {
                var subGo = new GameObject("TileSubtitle", typeof(RectTransform));
                subGo.transform.SetParent(b.transform, false);
                sub = subGo.AddComponent<TextMeshProUGUI>();
            }
            if (theme != null && theme.bodyFont != null) sub.font = theme.bodyFont;
            else if (label != null) sub.font = label.font;
            sub.text = subtitle;
            sub.fontSize = primary ? 18f : 15.5f;
            sub.color = new Color(1f, 1f, 1f, primary ? 0.72f : 0.58f);
            sub.characterSpacing = 2f;
            sub.alignment = TextAlignmentOptions.TopLeft;
            sub.textWrappingMode = TextWrappingModes.NoWrap;
            sub.overflowMode = TextOverflowModes.Ellipsis;
            sub.raycastTarget = false;
            var st = sub.rectTransform;
            st.anchorMin = new Vector2(0f, 0f); st.anchorMax = new Vector2(1f, 0.44f);
            st.offsetMin = new Vector2(textX, 6f); st.offsetMax = new Vector2(-60f, -2f);

            float chev = Mathf.Round(h * 0.26f);
            var chevImg = TileImage(b.transform, "TileChevron", LR2Icons.Get("chev-right"), new Color(1f, 1f, 1f, 0.38f),
                                    new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-34f, 0f),
                                    new Vector2(chev, chev));
            chevImg.preserveAspect = true;

            var tfx = b.GetComponent<TitleTileFX>();
            if (tfx == null) tfx = b.gameObject.AddComponent<TitleTileFX>();
            tfx.chevron = chevImg.rectTransform;
            tfx.chevronImage = chevImg;
            tfx.icon = ic;
            tfx.iconIdle = iconIdle;
            tfx.iconHover = primary ? Color.Lerp(accent, Color.white, 0.35f) : accent;
            return top - h - 14f;
        }

        private static Image TileImage(Transform parent, string name, Sprite sprite, Color color, Vector2 aMin, Vector2 aMax,
                                       Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var existing = parent.Find(name);
            var go = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = pivot;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            return img;
        }

        private void HideLegacyBotDifficultyControls()
        {
            if (mainPanel == null) return;
            var label = mainPanel.transform.Find("BotDifficultyLabel");
            if (label != null) label.gameObject.SetActive(false);
            var stepper = mainPanel.transform.Find("BotDifficultyRoster");
            if (stepper != null) stepper.gameObject.SetActive(false);
        }

        /// <summary>Menu background hook (B110): -1 = main menu, otherwise the GameModeType shown in the lobby.</summary>
        public static System.Action<int> OnMenuShot;

        private void OnDestroy()
        {
            foreach (var s in _generatedSprites)
                if (s != null) Destroy(s);
            foreach (var t in _generatedTexs)
                if (t != null) Destroy(t);
            _generatedSprites.Clear();
            _generatedTexs.Clear();
        }

        private void Update()
        {
            if (_isLoading && loadingSpinner != null)
            {
                loadingSpinner.Rotate(0f, 0f, -280f * Time.unscaledDeltaTime);
                return;
            }

            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                if (settingsPanel != null && settingsPanel.alpha > 0.5f) ReturnFromSettings();
                else if (modeRulesPanel != null && modeRulesPanel.alpha > 0.5f) ReturnFromModeRules();
                else if (_lobby != null && _lobby.Group.alpha > 0.5f) _lobby.OnBack?.Invoke();
                else if (deployPanel != null && deployPanel.alpha > 0.5f) ReturnFromDeploy();
            }
        }

        private void ReturnFromSettings()
        {
            LR2Settings.Save();
            SwitchLayer(_settingsReturn != null ? _settingsReturn : mainPanel, -1f);
            OnMenuShot?.Invoke(_settingsReturn != null ? (int)SkirmishSetup.Mode : -1);
            _settingsReturn = null;
            RefreshDifficultyLabel();
            RefreshDeployLabels();
            RefreshPreviewStatus();
        }

        private void ReturnFromDeploy()
        {
            SwitchLayer(mainPanel, -1f);
            RefreshDifficultyLabel();
            RefreshPreviewStatus();
        }

        private void StepDifficulty(int delta)
        {
            int count = DifficultyLabels.Length;
            int cur = Mathf.Clamp(LR2Settings.BotDifficulty, 0, count - 1);
            LR2Settings.BotDifficulty = (cur + delta % count + count) % count;
            RefreshDifficultyLabel();
            RefreshPreviewStatus();
        }

        private void RefreshDifficultyLabel()
        {
            if (difficultyValueText != null)
                difficultyValueText.text = DifficultyLabels[Mathf.Clamp(LR2Settings.BotDifficulty, 0, DifficultyLabels.Length - 1)];
        }

        // ------------------------------------------------------------------ map selection & preview
        private void StepMap(int delta)
        {
            int count = LR2Session.MapScenes.Length;
            int next = (LR2Session.SelectedMapIndex + delta % count + count) % count;
            SelectMap(next);
        }

        public void SelectMap(int index)
        {
            LR2Session.SelectedMapIndex = index;
            int idx = LR2Session.SelectedMapIndex;
            mapScene = LR2Session.CurrentMapScene;

            if (mapPreviewTitle != null && idx < LR2Session.MapDisplayNames.Length)
                mapPreviewTitle.text = LR2Session.MapDisplayNames[idx];
            if (mapPreviewDescription != null && idx < LR2Session.MapDescriptions.Length)
                mapPreviewDescription.text = LR2Session.MapDescriptions[idx];
            if (mapPreviewIndexText != null)
                mapPreviewIndexText.text = $"SECTOR {idx + 1:00} / {LR2Session.MapScenes.Length:00}";
            if (mapPreviewImage != null)
                mapPreviewImage.sprite = GetMapPreviewSprite(idx);
            if (deployMapPreviewImage != null)
                deployMapPreviewImage.sprite = GetMapPreviewSprite(idx);

            RefreshPreviewStatus();
            RefreshDeployLabels();
        }

        private Sprite GetMapPreviewSprite(int idx)
        {
            _cachedMapSprites ??= new Sprite[LR2Session.MapScenes.Length];
            if (_cachedMapSprites[idx] != null) return _cachedMapSprites[idx];

            Sprite loaded = Resources.Load<Sprite>(LR2Session.PreviewResourcePath(idx));
            if (loaded != null)
            {
                _cachedMapSprites[idx] = loaded;
                return loaded;
            }

            // Procedural tactical preview fallback if Play/Menu runs before screenshots are generated
            Sprite fallback = BuildProceduralMapSprite(idx);
            _cachedMapSprites[idx] = fallback;
            return fallback;
        }

        private Sprite BuildProceduralMapSprite(int idx)
        {
            const int w = 320, h = 180;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            Color[] accents =
            {
                new Color(0.28f, 0.56f, 0.68f, 1f), // Saltworks harbour
                new Color(0.75f, 0.36f, 0.24f, 1f), // Deadsector ruined city
                new Color(0.92f, 0.58f, 0.22f, 1f), // Foundry pipe refinery
                new Color(0.45f, 0.72f, 0.48f, 1f), // Test Range arena
            };
            Color col = accents[idx % accents.Length];
            Color bg = new Color(0.05f, 0.06f, 0.08f, 1f);

            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float ny = (float)y / h;
                for (int x = 0; x < w; x++)
                {
                    float nx = (float)x / w;
                    bool grid = (x % 32 == 0) || (y % 30 == 0);
                    float vignette = 1f - Mathf.Clamp01(Vector2.Distance(new Vector2(nx, ny), new Vector2(0.5f, 0.5f)) * 1.15f);
                    float horizon = ny < 0.42f ? 0.32f + ny * 0.6f : 0.14f;
                    Color c = Color.Lerp(bg, col * horizon, vignette * 0.85f);
                    if (grid) c = Color.Lerp(c, col, 0.22f);

                    // Distinct architectural blocks per map silhouette
                    if (ny > 0.28f && ny < 0.58f)
                    {
                        float blk = Mathf.Sin((nx * 14f + idx * 1.7f) * Mathf.PI);
                        if (blk > 0.62f && ny < 0.45f + (idx % 3) * 0.05f) c = Color.Lerp(c, col * 1.1f, 0.45f);
                    }
                    pixels[y * w + x] = c;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            var sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
            _generatedTexs.Add(tex);
            _generatedSprites.Add(sp);
            return sp;
        }

        // ------------------------------------------------------------------ per-mode rule customizer
        private MatchSettings SettingsFor(GameModeType mode) => mode switch
        {
            GameModeType.CaptureTheFlag => ctfSettings,
            GameModeType.FreeForAll => ffaSettings,
            GameModeType.TeamDeathmatch => tdmSettings,
            _ => duelSettings,
        };

        private void CacheBaseModeLabels()
        {
            _duelBaseLabel = ReadButtonLabel(duelButton, "1V1 DEATHMATCH");
            _ctfBaseLabel = ReadButtonLabel(ctfButton, "CAPTURE THE FLAG");
            _ffaBaseLabel = ReadButtonLabel(ffaButton, "FREE FOR ALL");
            _tdmBaseLabel = ReadButtonLabel(tdmButton, "TEAM DEATHMATCH");
        }

        private static string ReadButtonLabel(Button b, string fallback)
        {
            if (b == null) return fallback;
            var t = b.GetComponentInChildren<TMP_Text>(true);
            return t != null && !string.IsNullOrEmpty(t.text) ? t.text : fallback;
        }

        private static void SetButtonLabel(Button b, string text)
        {
            if (b == null) return;
            var t = b.GetComponentInChildren<TMP_Text>(true);
            if (t != null) t.text = text;
        }

        private void RefreshModeButtons()
        {
            SetButtonLabel(duelButton, _lobby != null ? "PLAY" : _duelBaseLabel + (LR2Session.HasCustomModeRules(GameModeType.Duel) ? "  [CUSTOM]" : ""));
            SetButtonLabel(ctfButton, _ctfBaseLabel);
            SetButtonLabel(ffaButton, _ffaBaseLabel + (LR2Session.HasCustomModeRules(GameModeType.FreeForAll) ? "  [CUSTOM]" : ""));
            SetButtonLabel(tdmButton, _tdmBaseLabel);
            RefreshPreviewStatus();
        }

        private void RefreshPreviewStatus()
        {
            if (mapPreviewStatusText == null) return;
            var template = SettingsFor(_editingMode) ?? duelSettings;
            var cfg = LR2Session.LoadModeRules(template);
            int mins = Mathf.RoundToInt(cfg.timeLimitSeconds / 60f);
            string modeLabel = _editingMode switch
            {
                GameModeType.CaptureTheFlag => LR2Settings.BotTeamMode == 1 ? "CTF · FFA"
                    : (LR2Settings.BotTeamMode == 2 ? "CTF · PACK"
                    : (LR2Settings.BotTeamMode == 3 ? "CTF · CO-OP"
                    : (LR2Settings.BotTeamMode == 4 ? "CTF · HALF & HALF" : "CTF · SQUADS"))),
                GameModeType.FreeForAll => "FFA",
                GameModeType.TeamDeathmatch => "TDM",
                _ => "1V1 DM",
            };
            string bots;
            if (_editingMode == GameModeType.Duel)
            {
                bots = "1V1";
            }
            else if (LR2Settings.BotTeamMode == (int)BotTeamAlignment.HalfAndHalf)
            {
                MatchSettings.SplitHalfAndHalf(cfg.enemyBots, out int ha, out int he);
                bots = $"{ha + 1}V{he}";
            }
            else if (_editingMode == GameModeType.TeamDeathmatch || (_editingMode == GameModeType.CaptureTheFlag && LR2Settings.BotTeamMode == 0))
            {
                bots = $"{Mathf.Max(1, cfg.friendlyBots) + 1}V{cfg.enemyBots}";
            }
            else
            {
                bots = $"1V{cfg.enemyBots}";
            }
            string drones = cfg.spawnOrbs ? $"DRONES ON ({cfg.maxOrbs})" : "DRONES OFF";
            mapPreviewStatusText.text = $"{modeLabel} RULES  ·  {bots}  ·  FIRST TO {cfg.scoreLimit}  ·  {mins} MIN  ·  {cfg.respawnDelay:0.0}S RESPAWN  ·  {drones}";
        }

        // ------------------------------------------------------------------ Layer 2 Deployment Lobby
        private void OpenDeployLobbyOrPlay(GameModeType mode, MatchSettings fallbackSettings)
        {
            _deployMode = mode;
            _editingMode = mode;
            if (deployPanel == null)
            {
                Play(SettingsFor(mode) ?? fallbackSettings);
                return;
            }
            LoadDeployLobby(mode);
            SwitchLayer(deployPanel, 1f);
        }

        private void BindDeployLobbyUI()
        {
            if (deployWeaponPrevButton != null) deployWeaponPrevButton.onClick.AddListener(() => StepWeapon(-1));
            if (deployWeaponNextButton != null) deployWeaponNextButton.onClick.AddListener(() => StepWeapon(1));
            if (deployMapPrevButton != null) deployMapPrevButton.onClick.AddListener(() => StepMap(-1));
            if (deployMapNextButton != null) deployMapNextButton.onClick.AddListener(() => StepMap(1));
            if (deployDiffPrevButton != null) deployDiffPrevButton.onClick.AddListener(() => { StepDifficulty(-1); RefreshDeployLabels(); });
            if (deployDiffNextButton != null) deployDiffNextButton.onClick.AddListener(() => { StepDifficulty(1); RefreshDeployLabels(); });
            if (deployBotTeamPrevButton != null) deployBotTeamPrevButton.onClick.AddListener(() => StepBotTeamMode(-1));
            if (deployBotTeamNextButton != null) deployBotTeamNextButton.onClick.AddListener(() => StepBotTeamMode(1));
            if (deployBackButton != null) deployBackButton.onClick.AddListener(ReturnFromDeploy);
            if (deployRulesButton != null) deployRulesButton.onClick.AddListener(() => OpenModeRules(_deployMode));
            if (deployStartButton != null) deployStartButton.onClick.AddListener(() => Play(SettingsFor(_deployMode) ?? duelSettings));

            HookDeploySlider(deployScoreSlider, v => _editingRules.scoreLimit = Mathf.RoundToInt(v));
            HookDeploySlider(deployBotsSlider, v => _editingRules.enemyBots = Mathf.RoundToInt(v));
        }

        private void HookDeploySlider(Slider s, System.Action<float> setter)
        {
            if (s == null) return;
            s.onValueChanged.AddListener(v =>
            {
                if (_loadingRules) return;
                setter(v);
                LR2Session.SaveModeRules(_deployMode, _editingRules);
                RefreshModeButtons();
                RefreshDeployLabels();
            });
        }

        private void StepWeapon(int delta)
        {
            int count = LR2Session.PrimaryWeaponNames.Length;
            int cur = LR2Session.SelectedPrimaryWeapon;
            LR2Session.SelectedPrimaryWeapon = (cur + delta % count + count) % count;
            RefreshDeployLabels();
        }

        private void StepBotTeamMode(int delta)
        {
            int count = LR2Session.BotTeamModeLabels.Length;
            int cur = Mathf.Clamp(LR2Settings.BotTeamMode, 0, count - 1);
            LR2Settings.BotTeamMode = (cur + delta % count + count) % count;
            LR2Settings.Save();
            RefreshDeployLabels();
            RefreshRulesLabels();
            RefreshPreviewStatus();
        }

        private void LoadDeployLobby(GameModeType mode)
        {
            _loadingRules = true;
            _editingMode = mode;
            _editingRules = LR2Session.LoadModeRules(SettingsFor(mode));
            if (deployModeTitleText != null)
            {
                deployModeTitleText.text = mode switch
                {
                    GameModeType.CaptureTheFlag => "CAPTURE THE FLAG  ·  STAGING",
                    GameModeType.FreeForAll => "FREE FOR ALL  ·  STAGING",
                    GameModeType.TeamDeathmatch => "TEAM DEATHMATCH  ·  SQUAD STAGING",
                    _ => "1V1 DEATHMATCH  ·  STAGING",
                };
            }
            if (deployModeDescText != null)
            {
                deployModeDescText.text = mode switch
                {
                    GameModeType.CaptureTheFlag => "Unified rotating strategic flags with proximity capture boost & team modes (Half & Half 50/50, Squads, FFA, Pack, Co-op).",
                    GameModeType.FreeForAll => "Every operator for themselves. Outshoot hostile fighters across dynamic weather and destructible cover.",
                    GameModeType.TeamDeathmatch => "Squad coordination against enemy fireteams. Support your teammate or push objective lanes.",
                    _ => "Head-to-head 1v1 tactical duel against a responsive human-input enemy bot.",
                };
            }
            SetRuleSlider(deployScoreSlider, 5f, 100f, _editingRules.scoreLimit, true);
            SetRuleSlider(deployBotsSlider, 1f, 8f, _editingRules.enemyBots, true);
            if (deployMapPreviewImage != null) deployMapPreviewImage.sprite = GetMapPreviewSprite(LR2Session.SelectedMapIndex);
            RefreshDeployLabels();
            _loadingRules = false;
        }

        private void RefreshDeployLabels()
        {
            int wIdx = LR2Session.SelectedPrimaryWeapon;
            if (deployWeaponValueText != null && wIdx < LR2Session.PrimaryWeaponNames.Length)
                deployWeaponValueText.text = LR2Session.PrimaryWeaponNames[wIdx];
            if (deployWeaponInfoText != null && wIdx < WeaponBlurbs.Length)
                deployWeaponInfoText.text = WeaponBlurbs[wIdx];

            int mIdx = LR2Session.SelectedMapIndex;
            if (deployMapValueText != null && mIdx < LR2Session.MapDisplayNames.Length)
                deployMapValueText.text = LR2Session.MapDisplayNames[mIdx];

            int dIdx = Mathf.Clamp(LR2Settings.BotDifficulty, 0, DifficultyLabels.Length - 1);
            if (deployDiffValueText != null)
                deployDiffValueText.text = DifficultyLabels[dIdx];

            int btIdx = Mathf.Clamp(LR2Settings.BotTeamMode, 0, LR2Session.BotTeamModeLabels.Length - 1);
            if (deployBotTeamValueText != null)
                deployBotTeamValueText.text = _deployMode == GameModeType.Duel ? "1V1 DUEL ONLY" : LR2Session.BotTeamModeLabels[btIdx];
            if (deployBotTeamPrevButton != null) deployBotTeamPrevButton.interactable = _deployMode != GameModeType.Duel;
            if (deployBotTeamNextButton != null) deployBotTeamNextButton.interactable = _deployMode != GameModeType.Duel;

            if (deployScoreValueText != null) deployScoreValueText.text = $"{_editingRules.scoreLimit} PTS";
            bool canEditBots = _deployMode != GameModeType.Duel;
            if (deployBotsSlider != null) deployBotsSlider.interactable = canEditBots;
            if (deployBotsValueText != null)
            {
                if (!canEditBots)
                {
                    deployBotsValueText.text = "1 (1V1)";
                }
                else if (LR2Settings.BotTeamMode == (int)BotTeamAlignment.HalfAndHalf)
                {
                    MatchSettings.SplitHalfAndHalf(_editingRules.enemyBots, out int halfAllies, out int halfEnemies);
                    deployBotsValueText.text = $"{halfAllies} ALLY / {halfEnemies} ENEMY";
                }
                else
                {
                    deployBotsValueText.text = $"{_editingRules.enemyBots} BOTS";
                }
            }
        }

        private void OpenModeRules(GameModeType mode)
        {
            _editingMode = mode;
            SwitchLayer(modeRulesPanel, 1f);
            LoadModeIntoEditor(mode);
        }

        private void ReturnFromModeRules()
        {
            if (_rulesFromLobby && _lobby != null)
            {
                _rulesFromLobby = false;
                SwitchLayer(_lobby.Group, -1f);
                _lobby.Reload();
                return;
            }
            SwitchLayer(deployPanel != null && _deployMode == _editingMode ? deployPanel : mainPanel, -1f);
            RefreshModeButtons();
            if (deployPanel != null) LoadDeployLobby(_deployMode);
        }

        private void BindModeRulesUI()
        {
            if (modeRulesDuelTab != null) modeRulesDuelTab.onClick.AddListener(() => { _editingMode = GameModeType.Duel; LoadModeIntoEditor(_editingMode); });
            if (modeRulesCtfTab != null) modeRulesCtfTab.onClick.AddListener(() => { _editingMode = GameModeType.CaptureTheFlag; LoadModeIntoEditor(_editingMode); });
            if (modeRulesFfaTab != null) modeRulesFfaTab.onClick.AddListener(() => { _editingMode = GameModeType.FreeForAll; LoadModeIntoEditor(_editingMode); });
            if (modeRulesTdmTab != null) modeRulesTdmTab.onClick.AddListener(() => { _editingMode = GameModeType.TeamDeathmatch; LoadModeIntoEditor(_editingMode); });
            if (modeRulesBackButton != null) modeRulesBackButton.onClick.AddListener(ReturnFromModeRules);
            if (modeRulesResetButton != null) modeRulesResetButton.onClick.AddListener(() =>
            {
                LR2Session.ResetModeRules(_editingMode);
                LoadModeIntoEditor(_editingMode);
                RefreshModeButtons();
            });
            if (modeRulesPlayButton != null) modeRulesPlayButton.onClick.AddListener(() =>
            {
                Show(modeRulesPanel, false);
                Play(SettingsFor(_editingMode) ?? duelSettings);
            });

            void StepRulesDifficulty(int delta)
            {
                if (_loadingRules) return;
                int total = DifficultyLabels.Length + 1; // -1 (Global) + 5 difficulties
                int cur = Mathf.Clamp(_editingRules.botDifficulty + 1, 0, total - 1);
                _editingRules.botDifficulty = (cur + delta % total + total) % total - 1;
                SaveCurrentRules();
                RefreshRulesLabels();
            }
            if (rulesBotDiffPrevButton != null) rulesBotDiffPrevButton.onClick.AddListener(() => StepRulesDifficulty(-1));
            if (rulesBotDiffNextButton != null) rulesBotDiffNextButton.onClick.AddListener(() => StepRulesDifficulty(1));
            if (rulesBotTeamPrevButton != null) rulesBotTeamPrevButton.onClick.AddListener(() => StepBotTeamMode(-1));
            if (rulesBotTeamNextButton != null) rulesBotTeamNextButton.onClick.AddListener(() => StepBotTeamMode(1));

            HookSlider(rulesScoreLimitSlider, v => _editingRules.scoreLimit = Mathf.RoundToInt(v));
            HookSlider(rulesTimeLimitSlider, v => _editingRules.timeLimitSeconds = Mathf.RoundToInt(v) * 60f);
            HookSlider(rulesRespawnSlider, v => _editingRules.respawnDelay = Mathf.RoundToInt(v) * 0.5f);
            HookSlider(rulesCountdownSlider, v => _editingRules.countdownSeconds = Mathf.RoundToInt(v));
            HookSlider(rulesEnemyBotsSlider, v => _editingRules.enemyBots = Mathf.RoundToInt(v));
            HookSlider(rulesFriendlyBotsSlider, v => _editingRules.friendlyBots = Mathf.RoundToInt(v));
            HookSlider(rulesMaxOrbsSlider, v => _editingRules.maxOrbs = Mathf.RoundToInt(v));
            HookSlider(rulesOrbIntervalSlider, v => _editingRules.orbSpawnInterval = Mathf.RoundToInt(v) * 5f);

            if (rulesSeekerDronesToggle != null)
            {
                rulesSeekerDronesToggle.onValueChanged.AddListener(on =>
                {
                    if (_loadingRules) return;
                    _editingRules.spawnOrbs = on;
                    SaveCurrentRules();
                    RefreshRulesLabels();
                });
            }
        }

        private void HookSlider(Slider s, System.Action<float> setter)
        {
            if (s == null) return;
            s.onValueChanged.AddListener(v =>
            {
                if (_loadingRules) return;
                setter(v);
                SaveCurrentRules();
                RefreshRulesLabels();
            });
        }

        private void LoadModeIntoEditor(GameModeType mode)
        {
            _loadingRules = true;
            _editingRules = LR2Session.LoadModeRules(SettingsFor(mode));

            HighlightTab(modeRulesDuelTab, mode == GameModeType.Duel);
            HighlightTab(modeRulesCtfTab, mode == GameModeType.CaptureTheFlag);
            HighlightTab(modeRulesFfaTab, mode == GameModeType.FreeForAll);
            HighlightTab(modeRulesTdmTab, mode == GameModeType.TeamDeathmatch);

            if (modeRulesTitleText != null)
            {
                string label = mode switch
                {
                    GameModeType.CaptureTheFlag => "CAPTURE THE FLAG  ·  UNIFIED STRATEGIC RULES",
                    GameModeType.FreeForAll => "FREE FOR ALL  ·  SOLO ARENA RULES",
                    GameModeType.TeamDeathmatch => "TEAM DEATHMATCH  ·  SQUAD RULES",
                    _ => "1V1 DEATHMATCH  ·  COMPETITIVE RULES",
                };
                modeRulesTitleText.text = label;
            }

            SetRuleSlider(rulesScoreLimitSlider, 1f, 100f, _editingRules.scoreLimit, true);
            SetRuleSlider(rulesTimeLimitSlider, 1f, 30f, Mathf.RoundToInt(_editingRules.timeLimitSeconds / 60f), true);
            SetRuleSlider(rulesRespawnSlider, 0f, 30f, Mathf.RoundToInt(_editingRules.respawnDelay * 2f), true);
            SetRuleSlider(rulesCountdownSlider, 0f, 10f, Mathf.RoundToInt(_editingRules.countdownSeconds), true);
            SetRuleSlider(rulesEnemyBotsSlider, 1f, 8f, _editingRules.enemyBots, true);
            SetRuleSlider(rulesFriendlyBotsSlider, 0f, 5f, _editingRules.friendlyBots, true);
            SetRuleSlider(rulesMaxOrbsSlider, 1f, 6f, _editingRules.maxOrbs, true);
            SetRuleSlider(rulesOrbIntervalSlider, 2f, 18f, Mathf.RoundToInt(_editingRules.orbSpawnInterval / 5f), true);

            if (rulesSeekerDronesToggle != null) rulesSeekerDronesToggle.SetIsOnWithoutNotify(_editingRules.spawnOrbs);

            RefreshRulesLabels();
            _loadingRules = false;
        }

        private void SaveCurrentRules()
        {
            LR2Session.SaveModeRules(_editingMode, _editingRules);
            RefreshModeButtons();
        }

        private void RefreshRulesLabels()
        {
            if (rulesScoreLimitValue != null) rulesScoreLimitValue.text = $"{_editingRules.scoreLimit} KILLS";
            if (rulesTimeLimitValue != null) rulesTimeLimitValue.text = $"{Mathf.RoundToInt(_editingRules.timeLimitSeconds / 60f)} MIN";
            if (rulesRespawnValue != null) rulesRespawnValue.text = $"{_editingRules.respawnDelay:0.0} S";
            if (rulesCountdownValue != null) rulesCountdownValue.text = $"{Mathf.RoundToInt(_editingRules.countdownSeconds)} S";

            bool canEditEnemies = _editingMode != GameModeType.Duel;
            if (rulesEnemyBotsSlider != null) rulesEnemyBotsSlider.interactable = canEditEnemies;
            if (rulesEnemyBotsValue != null) rulesEnemyBotsValue.text = canEditEnemies ? _editingRules.enemyBots.ToString() : "1 (1v1)";

            bool canEditFriendly = _editingMode == GameModeType.TeamDeathmatch || _editingMode == GameModeType.CaptureTheFlag;
            if (rulesFriendlyBotsSlider != null) rulesFriendlyBotsSlider.interactable = canEditFriendly;
            if (rulesFriendlyBotsValue != null) rulesFriendlyBotsValue.text = canEditFriendly ? _editingRules.friendlyBots.ToString() : "N/A";
            if (rulesBotTeamValueText != null)
            {
                int bt = Mathf.Clamp(LR2Settings.BotTeamMode, 0, LR2Session.BotTeamModeLabels.Length - 1);
                rulesBotTeamValueText.text = _editingMode == GameModeType.Duel ? "1V1 DUEL" : LR2Session.BotTeamModeLabels[bt];
            }
            if (rulesBotTeamPrevButton != null) rulesBotTeamPrevButton.interactable = _editingMode != GameModeType.Duel;
            if (rulesBotTeamNextButton != null) rulesBotTeamNextButton.interactable = _editingMode != GameModeType.Duel;

            bool dronesOn = _editingRules.spawnOrbs;
            if (rulesMaxOrbsSlider != null) rulesMaxOrbsSlider.interactable = dronesOn;
            if (rulesOrbIntervalSlider != null) rulesOrbIntervalSlider.interactable = dronesOn;
            if (rulesMaxOrbsValue != null) rulesMaxOrbsValue.text = dronesOn ? _editingRules.maxOrbs.ToString() : "OFF";
            if (rulesOrbIntervalValue != null) rulesOrbIntervalValue.text = dronesOn ? $"{Mathf.RoundToInt(_editingRules.orbSpawnInterval)} S" : "OFF";
            if (rulesBotDiffValueText != null)
            {
                int d = _editingRules.botDifficulty;
                rulesBotDiffValueText.text = d < 0 ? "GLOBAL SETTING" : DifficultyLabels[Mathf.Clamp(d, 0, DifficultyLabels.Length - 1)];
            }
        }

        private static void SetRuleSlider(Slider s, float min, float max, float val, bool whole)
        {
            if (s == null) return;
            s.minValue = min;
            s.maxValue = max;
            s.wholeNumbers = whole;
            s.SetValueWithoutNotify(val);
        }

        private static void HighlightTab(Button tab, bool active)
        {
            if (tab == null) return;
            var cb = tab.colors;
            cb.normalColor = active ? new Color(1f, 0.9f, 0.7f, 1f) : Color.white;
            cb.selectedColor = cb.normalColor;
            tab.colors = cb;
            var img = tab.GetComponent<Image>();
            if (img != null) img.color = active ? new Color(0.28f, 0.22f, 0.12f, 0.96f) : new Color(0.07f, 0.08f, 0.09f, 0.82f);
            var lbl = tab.GetComponentInChildren<TMP_Text>(true);
            if (lbl != null) lbl.color = active ? new Color(1f, 0.78f, 0.35f, 1f) : Color.white;
        }

        // ------------------------------------------------------------------ launch
        public void Play(MatchSettings settings)
        {
            if (_isLoading) return;
            LR2.World.MenuCinematic.Release();
            string targetScene = LR2Session.CurrentMapScene;
            if (!Application.CanStreamedLevelBeLoaded(targetScene)) targetScene = mapScene;
            if (!Application.CanStreamedLevelBeLoaded(targetScene)) targetScene = "LR2_Saltworks";
            if (!Application.CanStreamedLevelBeLoaded(targetScene))
            {
                Debug.LogError($"[LR2] Scene '{targetScene}' is not in Build Settings. Run LR2 > Build Everything.");
                return;
            }
            LR2Session.PrepareConfiguredMatch(settings);
            LR2Settings.Save();
            Show(mainPanel, false);
            Show(deployPanel, false);
            Show(settingsPanel, false);
            Show(modeRulesPanel, false);
            string niceName = LR2Session.MapDisplayNames[LR2Session.SelectedMapIndex];
            LR2LoadingIntro.BeginSceneTransition(niceName);
            Show(loadingPanel, !LR2LoadingIntro.IsCoveringScreen);
            StartCoroutine(LoadMapRoutine(targetScene));
        }

        private IEnumerator LoadMapRoutine(string sceneName)
        {
            _isLoading = true;
            if (loadingBarFill != null) loadingBarFill.fillAmount = 0f;
            string niceName = LR2Session.MapDisplayNames[LR2Session.SelectedMapIndex];
            LR2LoadingIntro.SetTransitionProgress(0.05f, niceName);
            var op = SceneManager.LoadSceneAsync(sceneName);
            if (op == null)
            {
                _isLoading = false;
                yield break;
            }
            op.allowSceneActivation = false;
            float start = Time.unscaledTime;
            while (!op.isDone)
            {
                float elapsed = Time.unscaledTime - start;
                float realPct = Mathf.Clamp01(op.progress / 0.9f);
                float smoothPct = Mathf.Min(realPct, Mathf.Clamp01(elapsed / 0.65f));
                LR2LoadingIntro.SetTransitionProgress(smoothPct, niceName);
                if (loadingBarFill != null) loadingBarFill.fillAmount = smoothPct;
                if (loadingStatusText != null) loadingStatusText.text = $"PREPARING ARENA  \u00b7  {Mathf.RoundToInt(smoothPct * 100f)}%";
                if (op.progress >= 0.9f && elapsed >= 0.65f)
                {
                    LR2LoadingIntro.SetTransitionProgress(1f, niceName);
                    if (loadingBarFill != null) loadingBarFill.fillAmount = 1f;
                    if (loadingStatusText != null) loadingStatusText.text = "PREPARING ARENA  \u00b7  100%";
                    op.allowSceneActivation = true;
                }
                yield return null;
            }
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void SwitchLayer(CanvasGroup next, float slideDir)
        {
            if (next == null) return;
            var prev = _activeLayer != next ? _activeLayer : null;
            if (prev == null)
            {
                if (mainPanel != null && mainPanel != next && mainPanel.alpha > 0.05f) prev = mainPanel;
                else if (deployPanel != null && deployPanel != next && deployPanel.alpha > 0.05f) prev = deployPanel;
                else if (modeRulesPanel != null && modeRulesPanel != next && modeRulesPanel.alpha > 0.05f) prev = modeRulesPanel;
                else if (settingsPanel != null && settingsPanel != next && settingsPanel.alpha > 0.05f) prev = settingsPanel;
            }
            _activeLayer = next;

            if (prev != null) AnimatePanel(prev, false, -slideDir * 72f);
            AnimatePanel(next, true, slideDir * 72f);
        }

        private void AnimatePanel(CanvasGroup g, bool show, float slideOffsetX)
        {
            if (g == null) return;
            if (_layerAnims.TryGetValue(g, out var c) && c != null) StopCoroutine(c);
            if (!gameObject.activeInHierarchy)
            {
                Show(g, show);
                return;
            }
            _layerAnims[g] = StartCoroutine(AnimatePanelRoutine(g, show, slideOffsetX));
        }

        private IEnumerator AnimatePanelRoutine(CanvasGroup g, bool show, float slideOffsetX)
        {
            var rt = g.transform as RectTransform;
            float startA = g.alpha;
            float endA = show ? 1f : 0f;
            Vector2 startPos = show ? new Vector2(slideOffsetX, 0f) : (rt != null ? rt.anchoredPosition : Vector2.zero);
            Vector2 endPos = show ? Vector2.zero : new Vector2(slideOffsetX, 0f);
            g.interactable = false;
            g.blocksRaycasts = false;
            const float duration = 0.22f;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration));
                g.alpha = Mathf.Lerp(startA, endA, u);
                if (rt != null) rt.anchoredPosition = Vector2.Lerp(startPos, endPos, u);
                yield return null;
            }
            g.alpha = endA;
            if (rt != null) rt.anchoredPosition = Vector2.zero;
            g.interactable = show;
            g.blocksRaycasts = show;
            _layerAnims[g] = null;
        }

        private static void Show(CanvasGroup g, bool show)
        {
            if (g == null) return;
            g.alpha = show ? 1f : 0f;
            g.interactable = show;
            g.blocksRaycasts = show;
            if (g.transform is RectTransform rt) rt.anchoredPosition = Vector2.zero;
        }
    }
}
