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
        private GameObject _expBarGo;
        private TMP_Text _expStatusText;
        private Button _holdAutoBtn;
        private string _baseRestartLabel;

        private void Awake()
        {
            Show(pausePanel, false);
            Show(settingsPanel, false);
            Show(scoreboardPanel, false);
            Show(endPanel, false);
            if (endRestartButton != null)
            {
                var lbl = endRestartButton.GetComponentInChildren<TMP_Text>(true);
                if (lbl != null) _baseRestartLabel = lbl.text;
            }
            Bind(resumeButton, Resume);
            Bind(settingsButton, () => { Show(pausePanel, false); Show(settingsPanel, true); });
            Bind(settingsBackButton, () => { Show(settingsPanel, false); Show(pausePanel, IsPaused); });
            Bind(restartButton, () => { Resume(); MatchManager.Instance?.RestartMatch(); });
            Bind(menuButton, () => { Resume(); MatchManager.Instance?.QuitToMenu(); });
            Bind(quitButton, Quit);
            Bind(endRestartButton, () =>
            {
                if (ExperimentSessionRunner.IsSessionActive) ExperimentSessionRunner.AdvanceToNextMatch();
                else MatchManager.Instance?.RestartMatch();
            });
            Bind(endMenuButton, () =>
            {
                if (ExperimentSessionRunner.IsSessionActive) ExperimentSessionRunner.AbortSession();
                else MatchManager.Instance?.QuitToMenu();
            });
        }

        private static void Bind(Button b, UnityEngine.Events.UnityAction a)
        {
            if (b != null) b.onClick.AddListener(a);
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
                if (!LR2.World.ExperimentTelemetryRecorder.IsIdleOrSaveComplete) return;
                if (_endShownAt < 0f) _endShownAt = Time.unscaledTime;
                if (Time.unscaledTime - _endShownAt > 1.6f && endPanel != null && endPanel.alpha < 1f)
                {
                    Show(endPanel, true);
                    FillEnd(mm);
                }
                if (_aiAutoAdvanceAt > 0f && !_aiAutoAdvanceCancelled && ExperimentSessionRunner.IsSessionActive)
                {
                    float left = _aiAutoAdvanceAt - Time.unscaledTime;
                    if (_expStatusText != null)
                        _expStatusText.text = $"AI VS AI  ·  ADVANCING TO NEXT MATCH IN {Mathf.Max(0f, left):0.0}S";
                    if (left <= 0f)
                    {
                        _aiAutoAdvanceAt = -1f;
                        ExperimentSessionRunner.AdvanceToNextMatch();
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

        private void FillEnd(MatchManager mm)
        {
            if (mm == null) return;
            if (endTitle != null)
            {
                endTitle.text = mm.IsDraw ? "DRAW" : mm.LocalPlayerWon ? "VICTORY" : "DEFEAT";
                endTitle.color = mm.IsDraw ? Color.white : mm.LocalPlayerWon ? new Color(1f, 0.75f, 0.3f) : new Color(1f, 0.32f, 0.28f);
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
            else if (_expBarGo != null) _expBarGo.SetActive(false);
        }

        private void BuildOrUpdateExperimentEndBar()
        {
            int cur = ExperimentSessionRunner.CurrentQueueIndex + 1;
            int tot = Mathf.Max(1, ExperimentSessionRunner.TotalMatches);
            var spec = ExperimentSessionRunner.CurrentMatchSpec;

            if (endRestartButton != null)
            {
                var lbl = endRestartButton.GetComponentInChildren<TMP_Text>(true);
                if (lbl != null) lbl.text = cur < tot ? $"NEXT MATCH ({cur + 1}/{tot})" : "FINISH EXPERIMENT";
            }
            if (endMenuButton != null)
            {
                var lbl = endMenuButton.GetComponentInChildren<TMP_Text>(true);
                if (lbl != null) lbl.text = "END SESSION";
            }

            if (_expBarGo == null && endPanel != null)
            {
                var theme = Resources.Load<UITheme>("LR2/UITheme");
                Color accent = theme != null ? theme.accent : new Color(1f, 0.71f, 0.28f);
                _expBarGo = new GameObject("ExperimentEndBar", typeof(RectTransform), typeof(Image));
                _expBarGo.transform.SetParent(endPanel.transform, false);
                _expBarGo.GetComponent<Image>().color = new Color(0.04f, 0.05f, 0.065f, 0.90f);
                var rt = (RectTransform)_expBarGo.transform;
                rt.anchorMin = new Vector2(0.14f, 0.14f); rt.anchorMax = new Vector2(0.86f, 0.255f); rt.offsetMin = rt.offsetMax = Vector2.zero;

                var stGo = new GameObject("Status", typeof(RectTransform));
                stGo.transform.SetParent(_expBarGo.transform, false);
                _expStatusText = stGo.AddComponent<TextMeshProUGUI>();
                if (theme != null && theme.headingFont != null) _expStatusText.font = theme.headingFont;
                _expStatusText.fontSize = 20f; _expStatusText.color = accent; _expStatusText.alignment = TextAlignmentOptions.MidlineLeft;
                var srt = _expStatusText.rectTransform;
                srt.anchorMin = new Vector2(0.02f, 0.52f); srt.anchorMax = new Vector2(0.76f, 0.96f); srt.offsetMin = srt.offsetMax = Vector2.zero;

                // Optional AI vs AI Hold Auto-Next button
                var hGo = new GameObject("HoldBtn", typeof(RectTransform), typeof(Image), typeof(Button));
                hGo.transform.SetParent(_expBarGo.transform, false);
                hGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.14f);
                var hrt = (RectTransform)hGo.transform;
                hrt.anchorMin = new Vector2(0.78f, 0.54f); hrt.anchorMax = new Vector2(0.98f, 0.94f); hrt.offsetMin = hrt.offsetMax = Vector2.zero;
                var htGo = new GameObject("Text", typeof(RectTransform)); htGo.transform.SetParent(hGo.transform, false);
                var ht = htGo.AddComponent<TextMeshProUGUI>(); if (theme != null && theme.headingFont != null) ht.font = theme.headingFont;
                ht.text = "PAUSE AUTO"; ht.fontSize = 15f; ht.color = Color.white; ht.alignment = TextAlignmentOptions.Center;
                ht.rectTransform.anchorMin = Vector2.zero; ht.rectTransform.anchorMax = Vector2.one; ht.rectTransform.offsetMin = ht.rectTransform.offsetMax = Vector2.zero;
                _holdAutoBtn = hGo.GetComponent<Button>();
                _holdAutoBtn.onClick.AddListener(() => { _aiAutoAdvanceCancelled = true; if (_expStatusText != null) _expStatusText.text = "AUTO-ADVANCE PAUSED"; });

                // 5-button Blind Human-Likeness Turing Rating Bar
                var rateLblGo = new GameObject("RatePrompt", typeof(RectTransform)); rateLblGo.transform.SetParent(_expBarGo.transform, false);
                var rl = rateLblGo.AddComponent<TextMeshProUGUI>(); if (theme != null && theme.bodyFont != null) rl.font = theme.bodyFont;
                rl.text = "DID THESE BOTS FEEL HUMAN?"; rl.fontSize = 16f; rl.color = new Color(1f, 1f, 1f, 0.8f); rl.alignment = TextAlignmentOptions.MidlineLeft;
                rl.rectTransform.anchorMin = new Vector2(0.02f, 0.06f); rl.rectTransform.anchorMax = new Vector2(0.32f, 0.48f); rl.rectTransform.offsetMin = rl.rectTransform.offsetMax = Vector2.zero;

                string[] labels = { "0% ROBOT", "25% STIFF", "50% UNSURE", "75% NATURAL", "100% HUMAN" };
                float[] vals = { 0f, 0.25f, 0.5f, 0.75f, 1f };
                var rateBtns = new List<Image>();
                for (int i = 0; i < 5; i++)
                {
                    int k = i;
                    var bGo = new GameObject($"R_{k}", typeof(RectTransform), typeof(Image), typeof(Button));
                    bGo.transform.SetParent(_expBarGo.transform, false);
                    var img = bGo.GetComponent<Image>(); img.color = new Color(1f, 1f, 1f, 0.10f); rateBtns.Add(img);
                    var brt = (RectTransform)bGo.transform;
                    float x0 = 0.33f + k * 0.132f;
                    brt.anchorMin = new Vector2(x0, 0.08f); brt.anchorMax = new Vector2(x0 + 0.124f, 0.46f); brt.offsetMin = brt.offsetMax = Vector2.zero;
                    var tGo = new GameObject("Text", typeof(RectTransform)); tGo.transform.SetParent(bGo.transform, false);
                    var t = tGo.AddComponent<TextMeshProUGUI>(); if (theme != null && theme.headingFont != null) t.font = theme.headingFont;
                    t.text = labels[k]; t.fontSize = 14f; t.color = Color.white; t.alignment = TextAlignmentOptions.Center;
                    t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one; t.rectTransform.offsetMin = t.rectTransform.offsetMax = Vector2.zero;
                    bGo.GetComponent<Button>().onClick.AddListener(() =>
                    {
                        ExperimentSessionRunner.RecordBlindRating(vals[k]);
                        for (int j = 0; j < rateBtns.Count; j++) rateBtns[j].color = j == k ? new Color(accent.r, accent.g, accent.b, 0.75f) : new Color(1f, 1f, 1f, 0.10f);
                        if (_expStatusText != null) _expStatusText.text = $"RATED {labels[k]} · READY FOR NEXT MATCH";
                    });
                }
            }

            if (_expBarGo != null) _expBarGo.SetActive(true);
            bool aiBatch = spec != null && spec.aiVsAi;
            _aiAutoAdvanceCancelled = false;
            _aiAutoAdvanceAt = aiBatch ? Time.unscaledTime + 3.0f : -1f;
            if (_holdAutoBtn != null) _holdAutoBtn.gameObject.SetActive(aiBatch);
            if (_expStatusText != null)
                _expStatusText.text = $"EXPERIMENT MATCH {cur} / {tot}  ·  {(ExperimentSessionRunner.ActiveConfig.blindCondition ? "BLIND CONDITION" : spec?.label)}  ·  CSV SAVED";
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
