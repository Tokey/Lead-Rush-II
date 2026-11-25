using System.Collections.Generic;
using System.Text;
using LR2.Combat;
using LR2.Core;
using LR2.Game;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace LR2.UI
{
    /// <summary>Pause menu (Esc), settings, scoreboard (hold Tab) and end-of-match screen.</summary>
    public class GameMenuController : MonoBehaviour
    {
        [Header("Panels")]
        public CanvasGroup pausePanel;
        public CanvasGroup settingsPanel;
        public CanvasGroup scoreboardPanel;
        public CanvasGroup endPanel;

        [Header("Pause buttons")]
        public Button resumeButton;
        public Button settingsButton;
        public Button restartButton;
        public Button menuButton;
        public Button quitButton;
        public Button settingsBackButton;

        [Header("Scoreboard")]
        public TMP_Text scoreboardTitle;
        public TMP_Text scoreboardBody;

        [Header("End screen")]
        public TMP_Text endTitle;
        public TMP_Text endSubtitle;
        public TMP_Text endScoreboard;
        public Button endRestartButton;
        public Button endMenuButton;

        public bool IsPaused { get; private set; }
        private float _endShownAt = -1f;
        private float _aiAutoAdvanceAt = -1f;
        private bool _aiAutoAdvanceCancelled;
        private bool _nextActionTriggered;
        private GameObject _expBarGo;
        private TMP_Text _expStatusText;
        private Button _holdAutoBtn;
        private GameObject _ratePromptGo;
        private GameObject _rateMinLabelGo;
        private GameObject _rateMaxLabelGo;
        private Slider _rateSlider;
        private TMP_Text _rateValueText;
        private bool _ratingTouched;
        private bool _showRateSlider;
        private string _baseRestartLabel;
        private string _baseMenuLabel;
        private Vector2 _baseScoreboardPos, _baseScoreboardSize;
        private float _baseScoreboardFontSize;
        private Vector2 _baseRestartPos, _baseRestartSize;
        private Vector2 _baseMenuPos, _baseMenuSize;

        private void Awake()
        {
            Show(pausePanel, false);
            Show(settingsPanel, false);
            Show(scoreboardPanel, false);
            Show(endPanel, false);
            if (endScoreboard != null)
            {
                _baseScoreboardPos = endScoreboard.rectTransform.anchoredPosition;
                _baseScoreboardSize = endScoreboard.rectTransform.sizeDelta;
                _baseScoreboardFontSize = endScoreboard.fontSize;
            }
            if (endRestartButton != null)
            {
                var rt = (RectTransform)endRestartButton.transform;
                _baseRestartPos = rt.anchoredPosition;
                _baseRestartSize = rt.sizeDelta;
                var lbl = endRestartButton.GetComponentInChildren<TMP_Text>(true);
                if (lbl != null) _baseRestartLabel = lbl.text;
            }
            if (endMenuButton != null)
            {
                var rt = (RectTransform)endMenuButton.transform;
                _baseMenuPos = rt.anchoredPosition;
                _baseMenuSize = rt.sizeDelta;
                var lbl = endMenuButton.GetComponentInChildren<TMP_Text>(true);
                if (lbl != null) _baseMenuLabel = lbl.text;
            }
            Bind(resumeButton, Resume);
            Bind(settingsButton, () => { Show(pausePanel, false); Show(settingsPanel, true); });
            Bind(settingsBackButton, () => { Show(settingsPanel, false); Show(pausePanel, IsPaused); });
            Bind(restartButton, () => { Resume(); MatchManager.Instance?.RestartMatch(); });
            Bind(menuButton, () => { Resume(); MatchManager.Instance?.QuitToMenu(); });
            Bind(quitButton, Quit);
            Bind(endRestartButton, TriggerEndPrimaryAction);
            Bind(endMenuButton, TriggerEndSecondaryAction);
        }

        private void TriggerEndPrimaryAction()
        {
            Time.timeScale = 1f;
            AudioListener.pause = false;
            if (ExperimentSessionRunner.IsSessionActive)
            {
                if (_nextActionTriggered) return;
                _nextActionTriggered = true;
                if (_showRateSlider && !_ratingTouched && _rateSlider != null)
                    ExperimentSessionRunner.RecordBlindRating(_rateSlider.value / 100f, "default_unmoved");
                ExperimentSessionRunner.AdvanceToNextMatch();
            }
            else
            {
                MatchManager.Instance?.RestartMatch();
            }
        }

        private void TriggerEndSecondaryAction()
        {
            Time.timeScale = 1f;
            AudioListener.pause = false;
            if (ExperimentSessionRunner.IsSessionActive)
            {
                if (_nextActionTriggered) return;
                _nextActionTriggered = true;
                ExperimentSessionRunner.AbortSession();
            }
            else
            {
                MatchManager.Instance?.QuitToMenu();
            }
        }

        private static void Bind(Button b, UnityEngine.Events.UnityAction a)
        {
            if (b != null) b.onClick.AddListener(a);
        }

        private static void EnsureEventSystemReady()
        {
            if (UnityEngine.EventSystems.EventSystem.current != null) return;
            var existing = FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>();
            if (existing != null) return;
            var esGo = new GameObject("EventSystem",
                typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
            DontDestroyOnLoad(esGo);
        }

        private void Update()
        {
            var kb = Keyboard.current;
            var mm = MatchManager.Instance;
            bool ended = mm != null && mm.State == MatchState.Ended;

            if (kb != null && kb.escapeKey.wasPressedThisFrame && !ended)
            {
                if (settingsPanel != null && settingsPanel.alpha > 0.5f)
                {
                    Show(settingsPanel, false);
                    Show(pausePanel, true);
                }
                else if (IsPaused) Resume();
                else Pause();
            }

            bool tab = kb != null && kb.tabKey.isPressed && !IsPaused && !ended;
            Show(scoreboardPanel, tab);
            if (tab) FillScoreboard(scoreboardBody);

            if (ended)
            {
                if (IsPaused || (pausePanel != null && pausePanel.alpha > 0.01f) || (settingsPanel != null && settingsPanel.alpha > 0.01f))
                {
                    IsPaused = false;
                    Show(pausePanel, false);
                    Show(settingsPanel, false);
                    Show(scoreboardPanel, false);
                }
                if (Time.timeScale != 1f) Time.timeScale = 1f;
                if (AudioListener.pause) AudioListener.pause = false;
                if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                if (!Cursor.visible) Cursor.visible = true;

                if (!LR2.World.ExperimentTelemetryRecorder.IsIdleOrSaveComplete) return;
                if (_endShownAt < 0f) _endShownAt = Time.unscaledTime;
                float revealDelay = ExperimentSessionRunner.IsSessionActive ? 0.35f : 1.6f;
                if (Time.unscaledTime - _endShownAt > revealDelay && endPanel != null && endPanel.alpha < 1f)
                {
                    EnsureEventSystemReady();
                    Show(endPanel, true);
                    FillEnd(mm);
                }
                if (endPanel != null && endPanel.alpha > 0.9f && ExperimentSessionRunner.IsSessionActive && !_nextActionTriggered)
                {
                    if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame))
                    {
                        TriggerEndPrimaryAction();
                        return;
                    }
                    if (_aiAutoAdvanceAt > 0f && !_aiAutoAdvanceCancelled)
                    {
                        float left = _aiAutoAdvanceAt - Time.unscaledTime;
                        if (_expStatusText != null)
                            _expStatusText.text = $"AI VS AI  ·  ADVANCING TO NEXT MATCH IN {Mathf.Max(0f, left):0.0}S";
                        if (left <= 0f)
                        {
                            _aiAutoAdvanceAt = -1f;
                            TriggerEndPrimaryAction();
                        }
                    }
                }
            }
        }

        public void Pause()
        {
            IsPaused = true;
            Time.timeScale = 0f;
            AudioListener.pause = true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Show(pausePanel, true);
        }

        public void Resume()
        {
            IsPaused = false;
            Time.timeScale = 1f;
            AudioListener.pause = false;
            Show(pausePanel, false);
            Show(settingsPanel, false);
            var mm = MatchManager.Instance;
            if (mm == null || mm.State != MatchState.Ended)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
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

        private static void Show(CanvasGroup g, bool show)
        {
            if (g == null) return;
            g.alpha = show ? 1f : 0f;
            g.interactable = show;
            g.blocksRaycasts = show;
        }

        internal void FillEnd(MatchManager mm)
        {
            if (mm == null) return;
            _nextActionTriggered = false;
            if (endTitle != null)
            {
                if (mm.LocalPlayer != null)
                {
                    endTitle.text = mm.IsDraw ? "DRAW" : mm.LocalPlayerWon ? "VICTORY" : "DEFEAT";
                    endTitle.color = mm.IsDraw ? Color.white : mm.LocalPlayerWon ? new Color(1f, 0.75f, 0.3f) : new Color(1f, 0.32f, 0.28f);
                }
                else
                {
                    if (mm.IsDraw)
                    {
                        endTitle.text = "DRAW";
                        endTitle.color = Color.white;
                    }
                    else if (mm.IsTeamMode)
                    {
                        string winTeam = TeamUtil.DisplayName(mm.WinnerTeam).ToUpperInvariant();
                        endTitle.text = $"{winTeam} VICTORY";
                        endTitle.color = TeamUtil.TeamColor(mm.WinnerTeam);
                    }
                    else if (mm.WinnerCombatant != null)
                    {
                        endTitle.text = $"{mm.WinnerCombatant.DisplayName} WINS";
                        endTitle.color = new Color(1f, 0.75f, 0.3f);
                    }
                    else
                    {
                        endTitle.text = "MATCH COMPLETE";
                        endTitle.color = Color.white;
                    }
                }
            }
            if (endSubtitle != null)
            {
                string mvp = "";
                Combatant best = null;
                foreach (var p in mm.Participants)
                    if (p != null && (best == null || p.stats.score > best.stats.score)) best = p;
                if (best != null) mvp = $"TOP SCORE  {best.DisplayName}  -  {best.stats.kills} KILLS  /  {best.stats.score} POINTS";
                endSubtitle.text = mvp;
            }
            FillScoreboard(endScoreboard);

            if (ExperimentSessionRunner.IsSessionActive) BuildOrUpdateExperimentEndBar();
            else RestoreStandardEndLayout();
        }

        private void RestoreStandardEndLayout()
        {
            if (_expBarGo != null) _expBarGo.SetActive(false);
            if (endScoreboard != null)
            {
                endScoreboard.rectTransform.anchoredPosition = _baseScoreboardPos;
                endScoreboard.rectTransform.sizeDelta = _baseScoreboardSize;
                if (_baseScoreboardFontSize > 1f) endScoreboard.fontSize = _baseScoreboardFontSize;
            }
            if (endRestartButton != null)
            {
                var rt = (RectTransform)endRestartButton.transform;
                rt.anchoredPosition = _baseRestartPos;
                rt.sizeDelta = _baseRestartSize;
                endRestartButton.interactable = true;
                var lbl = endRestartButton.GetComponentInChildren<TMP_Text>(true);
                if (lbl != null && !string.IsNullOrEmpty(_baseRestartLabel)) lbl.text = _baseRestartLabel;
            }
            if (endMenuButton != null)
            {
                var rt = (RectTransform)endMenuButton.transform;
                rt.anchoredPosition = _baseMenuPos;
                rt.sizeDelta = _baseMenuSize;
                endMenuButton.interactable = true;
                var lbl = endMenuButton.GetComponentInChildren<TMP_Text>(true);
                if (lbl != null && !string.IsNullOrEmpty(_baseMenuLabel)) lbl.text = _baseMenuLabel;
            }
        }

        private static string DescribeHumanRating(int pct)
        {
            if (pct <= 15) return "ROBOTIC";
            if (pct <= 38) return "STIFF";
            if (pct <= 62) return "UNSURE";
            if (pct <= 84) return "NATURAL";
            return "HUMAN";
        }

        private void BuildOrUpdateExperimentEndBar()
        {
            int cur = ExperimentSessionRunner.CurrentQueueIndex + 1;
            int tot = Mathf.Max(1, ExperimentSessionRunner.TotalMatches);
            var spec = ExperimentSessionRunner.CurrentMatchSpec;
            bool aiBatch = spec != null && spec.aiVsAi;
            bool showRating = (ExperimentSessionRunner.ActiveConfig == null || ExperimentSessionRunner.ActiveConfig.promptHumanRating) && !aiBatch;
            _showRateSlider = showRating;
            _ratingTouched = false;

            if (endScoreboard != null)
            {
                endScoreboard.rectTransform.anchoredPosition = new Vector2(0f, 44f);
                endScoreboard.rectTransform.sizeDelta = new Vector2(960f, 300f);
                endScoreboard.fontSize = 26f;
                endScoreboard.overflowMode = TextOverflowModes.Truncate;
            }

            if (endRestartButton != null)
            {
                var rt = (RectTransform)endRestartButton.transform;
                rt.anchoredPosition = new Vector2(-210f, -425f);
                rt.sizeDelta = new Vector2(380f, 64f);
                endRestartButton.interactable = true;
                var lbl = endRestartButton.GetComponentInChildren<TMP_Text>(true);
                if (lbl != null) lbl.text = cur < tot ? $"NEXT MATCH ({cur + 1}/{tot})" : "FINISH EXPERIMENT";
            }
            if (endMenuButton != null)
            {
                var rt = (RectTransform)endMenuButton.transform;
                rt.anchoredPosition = new Vector2(210f, -425f);
                rt.sizeDelta = new Vector2(380f, 64f);
                endMenuButton.interactable = true;
                var lbl = endMenuButton.GetComponentInChildren<TMP_Text>(true);
                if (lbl != null) lbl.text = "END SESSION";
            }

            var theme = Resources.Load<UITheme>("LR2/UITheme");
            Color accent = theme != null ? theme.accent : new Color(1f, 0.71f, 0.28f);

            if (_expBarGo == null && endPanel != null)
            {
                _expBarGo = new GameObject("ExperimentEndBar", typeof(RectTransform), typeof(Image));
                _expBarGo.transform.SetParent(endPanel.transform, false);
                var bgImg = _expBarGo.GetComponent<Image>();
                bgImg.color = new Color(0.04f, 0.05f, 0.065f, 0.92f);
                bgImg.raycastTarget = false; // never block clicks on action buttons below or sibling controls

                var stGo = new GameObject("Status", typeof(RectTransform));
                stGo.transform.SetParent(_expBarGo.transform, false);
                _expStatusText = stGo.AddComponent<TextMeshProUGUI>();
                if (theme != null && theme.headingFont != null) _expStatusText.font = theme.headingFont;
                _expStatusText.fontSize = 20f;
                _expStatusText.color = accent;
                _expStatusText.alignment = TextAlignmentOptions.MidlineLeft;
                _expStatusText.raycastTarget = false;

                var hGo = new GameObject("HoldBtn", typeof(RectTransform), typeof(Image), typeof(Button));
                hGo.transform.SetParent(_expBarGo.transform, false);
                hGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.14f);
                var htGo = new GameObject("Text", typeof(RectTransform)); htGo.transform.SetParent(hGo.transform, false);
                var ht = htGo.AddComponent<TextMeshProUGUI>(); if (theme != null && theme.headingFont != null) ht.font = theme.headingFont;
                ht.text = "PAUSE AUTO"; ht.fontSize = 15f; ht.color = Color.white; ht.alignment = TextAlignmentOptions.Center;
                ht.raycastTarget = false;
                ht.rectTransform.anchorMin = Vector2.zero; ht.rectTransform.anchorMax = Vector2.one; ht.rectTransform.offsetMin = ht.rectTransform.offsetMax = Vector2.zero;
                _holdAutoBtn = hGo.GetComponent<Button>();
                _holdAutoBtn.onClick.AddListener(() =>
                {
                    _aiAutoAdvanceCancelled = true;
                    _aiAutoAdvanceAt = -1f;
                    if (_holdAutoBtn != null) _holdAutoBtn.gameObject.SetActive(false);
                    if (_expStatusText != null) _expStatusText.text = "AUTO-ADVANCE PAUSED  ·  PRESS NEXT MATCH OR [ENTER]";
                });

                // Continuous 0-100% Human-Likeness Turing Rating Slider
                _ratePromptGo = new GameObject("RatePrompt", typeof(RectTransform));
                _ratePromptGo.transform.SetParent(_expBarGo.transform, false);
                var rl = _ratePromptGo.AddComponent<TextMeshProUGUI>();
                if (theme != null && theme.bodyFont != null) rl.font = theme.bodyFont;
                rl.text = "DID BOTS FEEL HUMAN?";
                rl.fontSize = 15f;
                rl.color = new Color(1f, 1f, 1f, 0.85f);
                rl.alignment = TextAlignmentOptions.MidlineLeft;
                rl.raycastTarget = false;
                rl.rectTransform.anchorMin = new Vector2(0.025f, 0.08f);
                rl.rectTransform.anchorMax = new Vector2(0.255f, 0.48f);
                rl.rectTransform.offsetMin = rl.rectTransform.offsetMax = Vector2.zero;

                _rateMinLabelGo = new GameObject("RateMin", typeof(RectTransform));
                _rateMinLabelGo.transform.SetParent(_expBarGo.transform, false);
                var minT = _rateMinLabelGo.AddComponent<TextMeshProUGUI>();
                if (theme != null && theme.headingFont != null) minT.font = theme.headingFont;
                minT.text = "0% ROBOT"; minT.fontSize = 13f; minT.color = new Color(0.96f, 0.45f, 0.38f);
                minT.alignment = TextAlignmentOptions.MidlineRight; minT.raycastTarget = false;
                minT.rectTransform.anchorMin = new Vector2(0.26f, 0.08f); minT.rectTransform.anchorMax = new Vector2(0.355f, 0.48f);
                minT.rectTransform.offsetMin = minT.rectTransform.offsetMax = Vector2.zero;

                var sliderGo = new GameObject("RateSlider", typeof(RectTransform), typeof(Slider));
                sliderGo.transform.SetParent(_expBarGo.transform, false);
                var slRt = (RectTransform)sliderGo.transform;
                slRt.anchorMin = new Vector2(0.365f, 0.11f);
                slRt.anchorMax = new Vector2(0.735f, 0.45f);
                slRt.offsetMin = slRt.offsetMax = Vector2.zero;

                var bgGo = new GameObject("Background", typeof(RectTransform), typeof(Image));
                bgGo.transform.SetParent(sliderGo.transform, false);
                var bgImg2 = bgGo.GetComponent<Image>();
                bgImg2.color = new Color(1f, 1f, 1f, 0.16f);
                var bgRt = (RectTransform)bgGo.transform;
                bgRt.anchorMin = new Vector2(0f, 0.28f); bgRt.anchorMax = new Vector2(1f, 0.72f);
                bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

                var fillArea = new GameObject("Fill Area", typeof(RectTransform));
                fillArea.transform.SetParent(sliderGo.transform, false);
                var faRt = (RectTransform)fillArea.transform;
                faRt.anchorMin = new Vector2(0f, 0.28f); faRt.anchorMax = new Vector2(1f, 0.72f);
                faRt.offsetMin = new Vector2(6f, 0f); faRt.offsetMax = new Vector2(-6f, 0f);

                var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
                fillGo.transform.SetParent(fillArea.transform, false);
                var fillImg = fillGo.GetComponent<Image>();
                fillImg.color = new Color(accent.r, accent.g, accent.b, 0.88f);
                fillImg.raycastTarget = false;
                var fillRt = (RectTransform)fillGo.transform;
                fillRt.anchorMin = Vector2.zero; fillRt.anchorMax = Vector2.one;
                fillRt.offsetMin = fillRt.offsetMax = Vector2.zero;

                var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
                handleArea.transform.SetParent(sliderGo.transform, false);
                var haRt = (RectTransform)handleArea.transform;
                haRt.anchorMin = Vector2.zero; haRt.anchorMax = Vector2.one;
                haRt.offsetMin = new Vector2(10f, 0f); haRt.offsetMax = new Vector2(-10f, 0f);

                var handleGo = new GameObject("Handle", typeof(RectTransform), typeof(Image));
                handleGo.transform.SetParent(handleArea.transform, false);
                var handleImg = handleGo.GetComponent<Image>();
                handleImg.color = new Color(1f, 0.94f, 0.80f, 1f);
                var handleRt = (RectTransform)handleGo.transform;
                handleRt.anchorMin = new Vector2(0f, 0.08f); handleRt.anchorMax = new Vector2(0f, 0.92f);
                handleRt.sizeDelta = new Vector2(22f, 0f);

                _rateSlider = sliderGo.GetComponent<Slider>();
                _rateSlider.fillRect = fillRt;
                _rateSlider.handleRect = handleRt;
                _rateSlider.targetGraphic = handleImg;
                _rateSlider.direction = Slider.Direction.LeftToRight;
                _rateSlider.minValue = 0f;
                _rateSlider.maxValue = 100f;
                _rateSlider.wholeNumbers = true;
                _rateSlider.SetValueWithoutNotify(50f);

                _rateMaxLabelGo = new GameObject("RateMax", typeof(RectTransform));
                _rateMaxLabelGo.transform.SetParent(_expBarGo.transform, false);
                var maxT = _rateMaxLabelGo.AddComponent<TextMeshProUGUI>();
                if (theme != null && theme.headingFont != null) maxT.font = theme.headingFont;
                maxT.text = "100% HUMAN"; maxT.fontSize = 13f; maxT.color = new Color(0.42f, 0.82f, 1f);
                maxT.alignment = TextAlignmentOptions.MidlineLeft; maxT.raycastTarget = false;
                maxT.rectTransform.anchorMin = new Vector2(0.745f, 0.08f); maxT.rectTransform.anchorMax = new Vector2(0.845f, 0.48f);
                maxT.rectTransform.offsetMin = maxT.rectTransform.offsetMax = Vector2.zero;

                var valGo = new GameObject("RateValue", typeof(RectTransform));
                valGo.transform.SetParent(_expBarGo.transform, false);
                _rateValueText = valGo.AddComponent<TextMeshProUGUI>();
                if (theme != null && theme.headingFont != null) _rateValueText.font = theme.headingFont;
                _rateValueText.text = "50% · DRAG";
                _rateValueText.fontSize = 15f;
                _rateValueText.color = accent;
                _rateValueText.alignment = TextAlignmentOptions.MidlineRight;
                _rateValueText.raycastTarget = false;
                _rateValueText.rectTransform.anchorMin = new Vector2(0.85f, 0.08f);
                _rateValueText.rectTransform.anchorMax = new Vector2(0.98f, 0.48f);
                _rateValueText.rectTransform.offsetMin = _rateValueText.rectTransform.offsetMax = Vector2.zero;

                _rateSlider.onValueChanged.AddListener(v =>
                {
                    _ratingTouched = true;
                    int pct = Mathf.RoundToInt(v);
                    string desc = DescribeHumanRating(pct);
                    ExperimentSessionRunner.RecordBlindRating(pct / 100f);
                    if (_rateValueText != null) _rateValueText.text = $"{pct}% · {desc}";
                    if (_expStatusText != null) _expStatusText.text = $"RATED {pct}% ({desc})  ·  READY FOR NEXT MATCH [ENTER]";
                });
            }

            if (_expBarGo != null)
            {
                _expBarGo.SetActive(true);
                var rt = (RectTransform)_expBarGo.transform;
                if (showRating)
                {
                    rt.anchorMin = new Vector2(0.14f, 0.175f);
                    rt.anchorMax = new Vector2(0.86f, 0.320f);
                }
                else
                {
                    rt.anchorMin = new Vector2(0.14f, 0.195f);
                    rt.anchorMax = new Vector2(0.86f, 0.285f);
                }
                rt.offsetMin = rt.offsetMax = Vector2.zero;

                if (_expStatusText != null)
                {
                    var srt = _expStatusText.rectTransform;
                    srt.anchorMin = showRating ? new Vector2(0.025f, 0.54f) : new Vector2(0.025f, 0.12f);
                    srt.anchorMax = showRating ? new Vector2(0.76f, 0.95f) : new Vector2(0.76f, 0.88f);
                    srt.offsetMin = srt.offsetMax = Vector2.zero;
                }
                if (_holdAutoBtn != null)
                {
                    var hrt = (RectTransform)_holdAutoBtn.transform;
                    hrt.anchorMin = showRating ? new Vector2(0.78f, 0.56f) : new Vector2(0.78f, 0.16f);
                    hrt.anchorMax = showRating ? new Vector2(0.98f, 0.93f) : new Vector2(0.98f, 0.84f);
                    hrt.offsetMin = hrt.offsetMax = Vector2.zero;
                    _holdAutoBtn.gameObject.SetActive(aiBatch);
                }
                if (_ratePromptGo != null) _ratePromptGo.SetActive(showRating);
                if (_rateMinLabelGo != null) _rateMinLabelGo.SetActive(showRating);
                if (_rateMaxLabelGo != null) _rateMaxLabelGo.SetActive(showRating);
                if (_rateSlider != null)
                {
                    _rateSlider.gameObject.SetActive(showRating);
                    _rateSlider.SetValueWithoutNotify(50f);
                }
                if (_rateValueText != null)
                {
                    _rateValueText.gameObject.SetActive(showRating);
                    _rateValueText.text = "50% · DRAG";
                }
            }

            if (endRestartButton != null) endRestartButton.transform.SetAsLastSibling();
            if (endMenuButton != null) endMenuButton.transform.SetAsLastSibling();

            _aiAutoAdvanceCancelled = false;
            _aiAutoAdvanceAt = aiBatch ? Time.unscaledTime + 3.0f : -1f;
            if (_expStatusText != null)
                _expStatusText.text = $"EXPERIMENT MATCH {cur} / {tot}  ·  {(ExperimentSessionRunner.ActiveConfig != null && ExperimentSessionRunner.ActiveConfig.blindCondition ? "BLIND CONDITION" : spec?.label)}  ·  CSV SAVED  ·  [ENTER] NEXT";
        }

        private void FillScoreboard(TMP_Text target)
        {
            if (target == null) return;
            var mm = MatchManager.Instance;
            var list = new List<Combatant>();
            if (mm != null) list.AddRange(mm.Participants);
            else foreach (var c in Combatant.All) if (c != null && c.Team != TeamId.Hostile) list.Add(c);
            list.RemoveAll(c => c == null);
            list.Sort((a, b) => b.stats.score != a.stats.score ? b.stats.score.CompareTo(a.stats.score) : b.stats.kills.CompareTo(a.stats.kills));

            var local = Combatant.FindLocalPlayer();
            var sb = new StringBuilder();
            sb.Append("<color=#9AA0A6><size=80%>PLAYER<pos=55%>K<pos=65%>D<pos=75%>A<pos=87%>SCORE</size></color>\n");
            foreach (var c in list)
            {
                Color col = c == local ? new Color(1f, 0.75f, 0.3f) : TeamUtil.ColorFor(local, c);
                sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(col)}>{c.DisplayName}</color>")
                  .Append($"<pos=55%>{c.stats.kills}<pos=65%>{c.stats.deaths}<pos=75%>{c.stats.assists}<pos=87%>{c.stats.score}\n");
            }
            target.text = sb.ToString();
            if (scoreboardTitle != null && mm != null && mm.Settings != null) scoreboardTitle.text = mm.Settings.displayName.ToUpperInvariant();
        }
    }
}
