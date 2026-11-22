using System.Collections.Generic;
using LR2.Audio;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using LR2.Game;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LR2.UI
{
    /// <summary>
    /// The in-game HUD. Auto-binds to the local player and MatchManager (works without one, too). All element
    /// references are assigned by the LR2 UI builder and remain editable in the Inspector.
    /// </summary>
    public class HUDController : MonoBehaviour
    {
        [Header("Root")]
        public CanvasGroup hudGroup;

        [Header("Crosshair")]
        public RectTransform[] crosshairLines = new RectTransform[4]; // up, down, left, right
        public Image crosshairDot;
        public float crosshairMinGap = 7f;
        public CanvasGroup crosshairGroup;

        [Header("Hitmarker")]
        public Image hitmarker;
        public float hitmarkerDuration = 0.28f;

        [Header("Ammo")]
        public TMP_Text weaponName;
        public TMP_Text magText;
        public TMP_Text reserveText;
        public TMP_Text fireModeText;
        public TMP_Text reloadPrompt;
        public RectTransform reloadBarRoot;
        public Image reloadBarFill;

        [Header("Health")]
        public Image healthFill;
        public TMP_Text healthText;
        public Image damageVignette;
        public Image lowHealthOverlay;

        [Header("Armour (B74; built at runtime above the health bar when left empty)")]
        public Image armorFill;
        public TMP_Text armorText;
        public Color armorColor = new Color(0.62f, 0.84f, 1f, 0.95f);

        [Header("Damage direction")]
        public RectTransform damageIndicatorRoot;
        public Image damageIndicatorTemplate;

        [Header("Kill feed")]
        public RectTransform killFeedRoot;
        public TMP_Text killFeedTemplate;
        public int killFeedMax = 5;
        public float killFeedLifetime = 5.5f;

        [Header("Score popups / medals")]
        public RectTransform popupRoot;
        public TMP_Text popupTemplate;
        public TMP_Text medalText;
        public CanvasGroup medalGroup;

        [Header("Match status")]
        public TMP_Text modeText;
        public TMP_Text timerText;
        public TMP_Text myScoreText;
        public TMP_Text enemyScoreText;
        public Image myScoreBar;
        public Image enemyScoreBar;
        public TMP_Text limitText;

        [Header("Center notifications")]
        public TMP_Text centerText;
        public TMP_Text subText;

        [Header("Minimap")]
        public RectTransform minimapRotator;
        public RawImage minimapImage;
        public RectTransform minimapIconRoot;
        public Image minimapIconTemplate;
        public float minimapRadius = 110f;
        public float minimapRange = 32f;

        [Header("Compass")]
        public RectTransform compassRoot;
        public TMP_Text compassTemplate;
        public float compassWidth = 640f;
        public float compassFov = 160f;

        [Header("Misc")]
        public TMP_Text fpsText;
        public TMP_Text bodycamOverlay;
        public string unitId = "";

        private UITheme _theme;
        private SoldierController _soldier;
        private Combatant _player;
        private float _hitmarkerTime = -10f;
        private Color _hitmarkerColor = Color.white;
        private float _hitmarkerScale = 1f;
        private float _damageFlash;
        private float _nextHeartbeat;
        private float _playingStartTime = -10f;
        private float _fpsTimer;
        private int _fpsFrames;

        private readonly List<(TMP_Text text, float time)> _feed = new List<(TMP_Text, float)>();
        private readonly List<(TMP_Text text, float time)> _popups = new List<(TMP_Text, float)>();
        private readonly Queue<string> _medalQueue = new Queue<string>();
        private float _medalShownAt = -10f;
        private readonly List<(Image img, Vector3 source, float time)> _damageIndicators = new List<(Image, Vector3, float)>();
        private readonly List<Image> _iconPool = new List<Image>();
        private readonly List<(TMP_Text label, float angle)> _compassLabels = new List<(TMP_Text, float)>();

        // BF6-style Flag Zone capture HUD + compass/minimap beacon
        private CanvasGroup _flagHudGroup;
        private TMP_Text _flagTitleText;
        private TMP_Text _flagSubText;
        private Image _flagFillFriendly;
        private Image _flagFillHostile;
        private TMP_Text _flagCompassMarker;
        private Image _flagMinimapIcon;

        // ------------------------------------------------------------------ B88 score labels + B96 minimap placement
        // User 11-19 00:13: the score panel showed two bare numbers that read as "1st and 2nd" in CTF, TDM and FFA, and the
        // minimap should sit at the bottom above the health bar like BF6 (top-left kept as an option).
        private TMP_Text _myScoreLabel, _enemyScoreLabel;
        private float _nextScoreLabelAt;
        private RectTransform _minimapRoot, _statusRoot, _chatRoot;
        private bool _layoutApplied, _layoutBottom;

        private void BuildScoreLabels()
        {
            if (modeText == null || myScoreText == null || enemyScoreText == null || _myScoreLabel != null) return;
            TMP_Text Label(string name, TMP_Text row, Color color)
            {
                var t = Instantiate(modeText, row.transform.parent);
                t.name = name;
                t.text = "";
                t.fontSize = 15f;
                t.enableAutoSizing = false;
                t.alignment = TextAlignmentOptions.BottomLeft;
                t.color = new Color(color.r, color.g, color.b, 0.95f);
                t.overflowMode = TextOverflowModes.Ellipsis;
                var rt = t.rectTransform;
                var rowRt = row.rectTransform;
                rt.anchorMin = rt.anchorMax = rowRt.anchorMin;
                rt.pivot = new Vector2(0f, 0f);
                rt.anchoredPosition = new Vector2(90f, rowRt.anchoredPosition.y + 3f);
                rt.sizeDelta = new Vector2(205f, 20f);
                return t;
            }
            _myScoreLabel = Label("MyScoreLabel", myScoreText, myScoreText.color);
            _enemyScoreLabel = Label("EnemyScoreLabel", enemyScoreText, enemyScoreText.color);
            if (limitText != null)
            {
                limitText.rectTransform.sizeDelta = new Vector2(170f, 24f);
                limitText.fontSize = 15f;
            }
        }

        private static string Ordinal(int n)
        {
            int t = n % 100;
            if (t is >= 11 and <= 13) return n + "TH";
            return n + (n % 10 == 1 ? "ST" : n % 10 == 2 ? "ND" : n % 10 == 3 ? "RD" : "TH");
        }

        private static string Short(string s) => string.IsNullOrEmpty(s) ? "-" : (s.Length > 14 ? s.Substring(0, 13) + "\u2026" : s);

        /// <summary>B116: score numbers and their labels wear the team colours they stand for.</summary>
        private void SetScoreColors(Color mine, Color theirs)
        {
            if (myScoreText != null) myScoreText.color = mine;
            if (enemyScoreText != null) enemyScoreText.color = theirs;
            if (_myScoreLabel != null) _myScoreLabel.color = new Color(mine.r, mine.g, mine.b, 0.85f);
            if (_enemyScoreLabel != null) _enemyScoreLabel.color = new Color(theirs.r, theirs.g, theirs.b, 0.85f);
        }

        private void UpdateScoreLabels(MatchManager mm)
        {
            if (_myScoreLabel == null || mm == null || mm.Settings == null || Time.unscaledTime < _nextScoreLabelAt) return;
            _nextScoreLabelAt = Time.unscaledTime + 0.25f;
            if (mm.IsTeamMode && _player != null && _player.Team != TeamId.None)
            {
                var other = mm.BestOpponentTeam(_player.Team);
                bool watching = LR2.Game.SkirmishSetup.Active && LR2.Game.SkirmishSetup.PlayerSpectates;
                _myScoreLabel.text = $"{(watching ? "WATCHING" : "YOUR TEAM")} \u00b7 {_player.Team.ToString().ToUpperInvariant()}";
                SetScoreColors(TeamUtil.TeamColor(_player.Team), TeamUtil.TeamColor(other));
                _enemyScoreLabel.text = $"ENEMY \u00b7 {other.ToString().ToUpperInvariant()}";
                return;
            }
            // Duel and free-for-all: you vs the best other player, with your place
            SetScoreColors(Friendly, Enemy);
            int mine = mm.GetDisplayScore(_player);
            int place = 1, players = 0;
            Combatant best = null;
            int bestScore = int.MinValue;
            foreach (var c in Combatant.All)
            {
                if (c == null || !c.isActiveAndEnabled) continue;
                players++;
                if (c == _player) continue;
                int sc = mm.GetDisplayScore(c);
                if (sc > mine) place++;
                if (sc > bestScore) { bestScore = sc; best = c; }
            }
            bool duel = mm.Settings.mode == GameModeType.Duel;
            _myScoreLabel.text = duel ? "YOU" : $"YOU \u00b7 {Ordinal(place)} OF {Mathf.Max(players, 1)}";
            string who = best != null ? Short(best.DisplayName) : "-";
            _enemyScoreLabel.text = duel ? $"RIVAL \u00b7 {who}" : (place == 1 ? $"2ND \u00b7 {who}" : $"LEADER \u00b7 {who}");
        }

        private void ApplyHudLayout()
        {
            if (_minimapRoot == null && minimapRotator != null && minimapRotator.parent != null) _minimapRoot = minimapRotator.parent.parent as RectTransform;
            if (_statusRoot == null && myScoreText != null) _statusRoot = myScoreText.transform.parent as RectTransform;
            if (_chatRoot == null)
            {
                var root = hudGroup != null ? hudGroup.transform : transform;
                foreach (var rt in root.GetComponentsInChildren<RectTransform>(true))
                    if (rt.name.IndexOf("Chat", System.StringComparison.OrdinalIgnoreCase) >= 0 && rt.parent == root) { _chatRoot = rt; break; }
            }
            bool bottom = LR2Settings.MinimapBottom;
            if (_layoutApplied && bottom == _layoutBottom) return;
            _layoutApplied = true;
            _layoutBottom = bottom;
            if (_minimapRoot != null)
            {
                _minimapRoot.pivot = new Vector2(0.5f, 0.5f);
                if (bottom)
                {
                    // BF6: bottom-left, sitting just above the health and armour bars
                    _minimapRoot.anchorMin = _minimapRoot.anchorMax = new Vector2(0f, 0f);
                    _minimapRoot.anchoredPosition = new Vector2(186f, 276f);
                }
                else
                {
                    _minimapRoot.anchorMin = _minimapRoot.anchorMax = new Vector2(0f, 1f);
                    _minimapRoot.anchoredPosition = new Vector2(150f, -150f);
                }
            }
            if (_statusRoot != null)
            {
                _statusRoot.anchorMin = _statusRoot.anchorMax = new Vector2(0f, 1f);
                _statusRoot.pivot = new Vector2(0f, 1f);
                _statusRoot.anchoredPosition = bottom ? new Vector2(40f, -36f) : new Vector2(40f, -290f);
            }
            if (_chatRoot != null)
            {
                // keep the chat clear of a bottom minimap (it sits at 30 % of the screen height by default)
                _chatRoot.anchorMin = _chatRoot.anchorMax = new Vector2(0f, bottom ? 0.40f : 0.30f);
            }
        }

        private void OnDestroy()
        {
            LR2Settings.Changed -= ApplyHudLayout;
        }

        private void Awake()
        {
            _theme = Resources.Load<UITheme>("LR2/UITheme");
            if (killFeedTemplate != null) killFeedTemplate.gameObject.SetActive(false);
            if (popupTemplate != null) popupTemplate.gameObject.SetActive(false);
            if (damageIndicatorTemplate != null) damageIndicatorTemplate.gameObject.SetActive(false);
            if (minimapIconTemplate != null) minimapIconTemplate.gameObject.SetActive(false);
            if (hitmarker != null) hitmarker.color = new Color(1f, 1f, 1f, 0f);
            if (medalGroup != null) medalGroup.alpha = 0f;
            // B136j: Keep weapon name + grenade badge well above the large magazine readout even on un-rebuilt scenes.
            if (weaponName != null && magText != null)
            {
                var wrt = weaponName.rectTransform;
                var ap = wrt.anchoredPosition;
                if (ap.y < 118f) wrt.anchoredPosition = new Vector2(ap.x, 118f);
                var sd = wrt.sizeDelta;
                if (sd.x < 480f) wrt.sizeDelta = new Vector2(480f, Mathf.Max(30f, sd.y));
            }
            BuildCompass();
            BuildFlagHud();
            TacticalChatSystem.Mount(hudGroup != null ? hudGroup.transform : transform);
            BuildScoreLabels();
            ApplyHudLayout();
            LR2Settings.Changed -= ApplyHudLayout;
            LR2Settings.Changed += ApplyHudLayout;
            // BF6-style hit feedback (thick X, kill skull, layered hit/kill sounds) replaces the old small hitmarker
            _hitFeedback = GetComponentInChildren<HitFeedbackHUD>(true);
            if (_hitFeedback == null)
            {
                Transform parent = hitmarker != null ? hitmarker.rectTransform.parent : transform;
                _hitFeedback = HitFeedbackHUD.Create(parent);
                if (hitmarker != null) _hitFeedback.transform.SetSiblingIndex(hitmarker.transform.GetSiblingIndex() + 1);
            }
            if (hitmarker != null) hitmarker.enabled = false;
        }

        private HitFeedbackHUD _hitFeedback;

        /// <summary>Draws killcam/replay hitmarker &amp; kill skull and plays the rugged hit confirm sound even if HUD is hidden.</summary>
        public static void ShowReplayHit(bool headshot, bool kill, Vector3 worldPoint = default, bool armorHit = false, bool armorBroke = false) =>
            HitFeedbackHUD.ShowReplayHit(headshot, kill, worldPoint, armorHit, armorBroke);

        private static HUDController _instance;
        private static Sprite _shieldSprite;
        private Image _armorBack, _armorIcon;
        private float _armorFlash, _lastArmor = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetArmorStatics() { _instance = null; _shieldSprite = null; }

        /// <summary>Shows a short popup line for the local player (ammo-station resupply etc.). No-op without a HUD.</summary>
        public static void Notify(string text)
        {
            if (_instance != null) _instance.AddPopup(text);
        }

        public int ActiveDamageIndicatorCount => _damageIndicators.Count;

        private void OnEnable()
        {
            _instance = this;
            GameEvents.Hit += OnHit;
            GameEvents.Killed += OnKilled;
            GameEvents.ScoreAwarded += OnScore;
            GameEvents.MedalAwarded += OnMedal;
            GameEvents.MatchStateChanged += OnMatchState;
        }

        private void OnDisable()
        {
            if (_instance == this) _instance = null;
            GameEvents.Hit -= OnHit;
            GameEvents.Killed -= OnKilled;
            GameEvents.ScoreAwarded -= OnScore;
            GameEvents.MedalAwarded -= OnMedal;
            GameEvents.MatchStateChanged -= OnMatchState;
        }

        private Color Friendly => _theme != null ? _theme.friendly : TeamUtil.FriendlyColor;
        private Color Enemy => _theme != null ? _theme.enemy : TeamUtil.EnemyColor;
        private Color Accent => _theme != null ? _theme.accent : new Color(1f, 0.71f, 0.28f);

        private void Bind()
        {
            if (LR2.Game.SkirmishSetup.Active && LR2.Game.SkirmishSetup.PlayerSpectates)
            {
                // AI vs AI: the HUD shows the soldier the spectator camera follows (health, ammo, hits), like a CS spectator
                var t = LR2.Game.SpectatorCamera.Target;
                if (t != _player) { _player = t; _soldier = t != null ? t.GetComponent<SoldierController>() : null; }
                return;
            }
            if (_player != null && _soldier != null) return;
            _player = Combatant.FindLocalPlayer();
            _soldier = _player != null ? _player.GetComponent<SoldierController>() : null;
        }

        private void Update()
        {
            Bind();
            float dt = Time.unscaledDeltaTime;
            UpdateCrosshair();
            if (LR2.Game.SpectatorCamera.HideCrosshair && crosshairGroup != null) crosshairGroup.alpha = 0f;
            UpdateHitmarker();
            UpdateAmmo();
            if (LR2.Game.SkirmishSetup.Active && LR2.Game.SkirmishSetup.PlayerSpectates && reloadPrompt != null) reloadPrompt.text = ""; // no "press R" for a bot you watch
            UpdateHealth(dt);
            UpdateDamageIndicators();
            UpdateFeed();
            UpdatePopups();
            UpdateMatchStatus(dt);
            UpdateCenter();
            UpdateFlagZoneHud(dt);
            UpdateMinimap();
            UpdateCompass();
            UpdateMisc(dt);
        }

        // ------------------------------------------------------------------ crosshair / hitmarker
        private void UpdateCrosshair()
        {
            if (crosshairGroup == null) return;
            var gun = _soldier != null ? _soldier.ActiveFirearm : null;
            if (_soldier == null || gun == null || _soldier.IsDead)
            {
                crosshairGroup.alpha = 0f;
                return;
            }
            float fov = _soldier.ViewCamera != null ? _soldier.ViewCamera.fieldOfView : LR2Settings.VerticalFov(LR2Settings.FieldOfView, 16f / 9f);
            float canvasHalfHeight = 540f;
            float spreadPx = Mathf.Tan(gun.CurrentSpread * Mathf.Deg2Rad) / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * canvasHalfHeight;
            float gap = crosshairMinGap + spreadPx;
            Vector2[] dirs = { Vector2.up, Vector2.down, Vector2.left, Vector2.right };
            for (int i = 0; i < crosshairLines.Length && i < 4; i++)
                if (crosshairLines[i] != null) crosshairLines[i].anchoredPosition = dirs[i] * (gap + 6f);

            float target = 1f - _soldier.AimBlend;
            if (_soldier.IsSprinting || _soldier.IsReloading) target *= 0.25f;
            crosshairGroup.alpha = Mathf.MoveTowards(crosshairGroup.alpha, target, Time.unscaledDeltaTime * 8f);
        }

        private void UpdateHitmarker()
        {
            if (hitmarker == null || _hitFeedback != null) return;
            float t = (Time.unscaledTime - _hitmarkerTime) / hitmarkerDuration;
            float a = t < 1f ? 1f - t * t : 0f;
            var c = _hitmarkerColor;
            c.a = a;
            hitmarker.color = c;
            hitmarker.rectTransform.localScale = Vector3.one * Mathf.Lerp(_hitmarkerScale * 1.15f, _hitmarkerScale, Mathf.Clamp01(t * 3f));
        }

        private void OnHit(HitEvent e)
        {
            if (_player == null) return;
            if (_hitFeedback == null && e.attacker == _player && e.victim != _player && e.damage > 0f)
            {
                _hitmarkerTime = Time.unscaledTime;
                bool head = e.zone == HitZone.Head;
                _hitmarkerColor = e.killed ? Enemy : head ? new Color(1f, 0.85f, 0.4f) : Color.white;
                _hitmarkerScale = e.killed ? 1.35f : head ? 1.15f : 1f;
                var s = LR2CommonSounds.Instance;
                if (LR2Settings.HitmarkerSound)
                {
                    if (s != null)
                    {
                        var mainSet = e.killed && s.killConfirm.IsValid ? s.killConfirm
                            : (head && s.hitmarkerHeadshot.IsValid ? s.hitmarkerHeadshot : s.hitmarker);
                        LR2Audio.Play2D(mainSet, e.killed ? 1.4f : 1.25f);
                        if (e.killed && s.hitmarker.IsValid) LR2Audio.Play2D(s.hitmarker, 1.15f);
                    }
                    PlayPunchHitLayer(head, e.killed);
                }
            }
            if (e.victim == _player && e.damage > 0f)
            {
                _damageFlash = Mathf.Min(1f, _damageFlash + e.damage / 45f);
                if (e.attacker != null && e.attacker != _player) SpawnDamageIndicator(e.attacker.Position);
                else if (e.direction.sqrMagnitude > 0.01f) SpawnDamageIndicator(_player.Position - e.direction * 10f);
            }
        }

        private static AudioClip _punchClip, _headPunchClip;

        private static void PlayPunchHitLayer(bool headshot, bool killed)
        {
            if (_punchClip == null) _punchClip = CreateHitPunchClip(false);
            if (_headPunchClip == null) _headPunchClip = CreateHitPunchClip(true);
            float vol = killed ? 1.0f : headshot ? 0.95f : 0.85f;
            float pitch = killed ? 0.95f : headshot ? 1.08f : Random.Range(0.96f, 1.05f);
            LR2Audio.PlayClip(headshot ? _headPunchClip : _punchClip, Vector3.zero, vol, pitch, 1f, 500f, 0f, AudioCategory.UI, 0);
        }

        private static AudioClip CreateHitPunchClip(bool headshot)
        {
            const int sr = 44100;
            int samples = Mathf.RoundToInt(sr * (headshot ? 0.095f : 0.065f));
            var data = new float[samples];
            float phaseLow = 0f, phaseHi = 0f;
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)sr;
                float envLow = Mathf.Exp(-t * 48f);
                float envHi = Mathf.Exp(-t * 140f);
                float freqLow = Mathf.Lerp(headshot ? 210f : 155f, 52f, Mathf.Clamp01(t * 28f));
                float freqHi = headshot ? 3600f : 2650f;
                phaseLow += 2f * Mathf.PI * freqLow / sr;
                phaseHi += 2f * Mathf.PI * freqHi / sr;
                float transient = i < 90 ? (1f - i / 90f) * 0.45f : 0f;
                float sample = Mathf.Sin(phaseLow) * envLow * 0.72f + Mathf.Sin(phaseHi) * envHi * (headshot ? 0.52f : 0.36f) + transient;
                data[i] = Mathf.Clamp(sample, -0.95f, 0.95f);
            }
            var clip = AudioClip.Create(headshot ? "LR2_HeadHitPunch" : "LR2_HitPunch", samples, 1, sr, false);
            clip.SetData(data, 0);
            return clip;
        }

        // ------------------------------------------------------------------ ammo / health
        private void UpdateAmmo()
        {
            var gun = _soldier != null ? _soldier.ActiveFirearm : null;
            bool show = gun != null && !_soldier.IsDead;
            // B136: the knife and the torch have no ammo, but their name (and the frag count) still show
            var item = _soldier != null && !_soldier.IsDead ? _soldier.ActiveItem : null;
            string frags = _soldier != null && _soldier.GrenadesLeft > 0 ? $"   FRAG x{_soldier.GrenadesLeft}" : "";
            if (magText != null) magText.enabled = show;
            if (reserveText != null) reserveText.enabled = show;
            if (weaponName != null) weaponName.enabled = show || item != null;
            if (fireModeText != null) fireModeText.enabled = show;
            if (!show)
            {
                if (weaponName != null && item != null) weaponName.text = item.DisplayName.ToUpperInvariant() + frags;
                if (reloadPrompt != null) reloadPrompt.text = "";
                if (reloadBarRoot != null) reloadBarRoot.gameObject.SetActive(false);
                return;
            }

            float ratio = (float)gun.AmmoInMagazine / Mathf.Max(1, gun.MagazineSize);
            if (weaponName != null) weaponName.text = gun.DisplayName.ToUpperInvariant() + (gun.IsSuppressed ? " (S)" : "") + frags;
            if (magText != null)
            {
                magText.text = gun.AmmoInMagazine.ToString();
                magText.color = gun.AmmoInMagazine == 0 ? Enemy : ratio <= 0.3f ? Accent : Color.white;
            }
            if (reserveText != null) reserveText.text = gun.ReserveAmmo.ToString();
            if (fireModeText != null) fireModeText.text = gun.FireMode.ToString().ToUpperInvariant();
            if (reloadBarRoot != null) reloadBarRoot.gameObject.SetActive(gun.IsReloading);
            if (reloadBarFill != null && gun.IsReloading) reloadBarFill.fillAmount = gun.ReloadDisplayProgress;
            if (reloadPrompt != null)
            {
                string prompt = "";
                if (gun.IsReloading) prompt = "RELOADING";
                else if (gun.AmmoInMagazine == 0 && gun.ReserveAmmo == 0) prompt = "NO AMMO";
                else if (ratio <= 0.3f && gun.ReserveAmmo > 0) prompt = "RELOAD [R]";
                reloadPrompt.text = prompt;
                reloadPrompt.color = gun.IsReloading ? Accent : gun.AmmoInMagazine == 0 ? Enemy : Accent;
            }
        }

        // ------------------------------------------------------------------ armour (B74)
        private void UpdateArmor(Health health, bool alive, float dt)
        {
            bool show = health != null && health.HasArmor;
            if (show) EnsureArmorWidgets();
            if (armorFill == null) return;

            float vis = show && alive ? 1f : 0f;
            float armor = show ? health.Armor : 0f;
            if (show && alive && _lastArmor > 0.001f && armor <= 0.001f) _armorFlash = 1f; // plate just broke
            _lastArmor = show && alive ? armor : -1f;
            _armorFlash = Mathf.MoveTowards(_armorFlash, 0f, dt * 2.2f);

            float n = show ? health.ArmorNormalized : 0f;
            armorFill.fillAmount = Mathf.MoveTowards(armorFill.fillAmount, n, dt * 3f);
            var c = Color.Lerp(armorColor, Color.white, _armorFlash * 0.6f);
            c.a = armorColor.a * vis;
            armorFill.color = c;
            if (_armorBack != null)
                _armorBack.color = Color.Lerp(new Color(0f, 0f, 0f, 0.45f * vis), new Color(0.85f, 0.12f, 0.08f, 0.75f * vis), _armorFlash);

            var tc = n > 0.001f ? new Color(armorColor.r, armorColor.g, armorColor.b, vis) : new Color(1f, 1f, 1f, 0.35f * vis);
            tc = Color.Lerp(tc, new Color(1f, 0.25f, 0.18f, vis), _armorFlash);
            if (armorText != null)
            {
                armorText.text = Mathf.CeilToInt(armor).ToString();
                armorText.color = tc;
                float shake = _armorFlash * _armorFlash * 4f;
                armorText.rectTransform.anchoredPosition = new Vector2(82f + Mathf.Sin(Time.unscaledTime * 90f) * shake, 0f);
            }
            if (_armorIcon != null) _armorIcon.color = tc;
        }

        private void EnsureArmorWidgets()
        {
            if (armorFill != null || healthFill == null) return;
            var hr = healthFill.rectTransform;
            var white = healthFill.sprite;

            // Bar sits 3.5 px above the health bar and stretches with it; a divider marks the two 25-point plates.
            _armorBack = MakeHudImage("ArmorBack", hr, white, new Color(0f, 0f, 0f, 0.45f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 0f), new Vector2(0f, 3.5f), new Vector2(0f, 5f));
            armorFill = MakeHudImage("ArmorFill", hr, white, armorColor, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 0f), new Vector2(0f, 3.5f), new Vector2(0f, 5f));
            armorFill.type = Image.Type.Filled;
            armorFill.fillMethod = Image.FillMethod.Horizontal;
            armorFill.fillAmount = 1f;
            MakeHudImage("ArmorPlateDivider", hr, white, new Color(0f, 0f, 0f, 0.85f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 0f), new Vector2(0f, 3.5f), new Vector2(3f, 5f));

            if (healthText != null)
            {
                var ht = healthText.rectTransform;
                ht.anchoredPosition += new Vector2(0f, 8f); // make room for the armour bar
                armorText = Instantiate(healthText, ht); // clone first so the copy carries the same font/shadow and no icon
                armorText.name = "ArmorText";
                _armorIcon = MakeHudImage("ArmorIcon", ht, ShieldSprite(), armorColor, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(64f, 3f), new Vector2(14f, 17f));
                var at = armorText.rectTransform;
                at.anchorMin = at.anchorMax = Vector2.zero;
                at.pivot = Vector2.zero;
                at.sizeDelta = new Vector2(80f, 28f);
                at.anchoredPosition = new Vector2(82f, 0f);
                armorText.fontSize = healthText.fontSize * 0.62f;
                armorText.alignment = TextAlignmentOptions.BottomLeft;
                armorText.text = "50";
            }
        }

        private static Image MakeHudImage(string name, Transform parent, Sprite sprite, Color color, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Procedural plate/shield glyph (flat crown, tapered point, lighter core) so no font glyph is needed.</summary>
        private static Sprite ShieldSprite()
        {
            if (_shieldSprite != null) return _shieldSprite;
            const int W = 32, H = 38;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "LR2_ArmorShield" };
            var px = new Color32[W * H];
            float aa = 2f / W;
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W * 2f - 1f;
                float v = (y + 0.5f) / H * 2f - 1f;
                float half = v > 0.05f ? 0.86f : 0.86f * Mathf.Sqrt(Mathf.Clamp01((v + 1f) / 1.05f));
                float top = 0.9f - 0.08f * u * u;
                float d = Mathf.Max(Mathf.Abs(u) - half, v - top);
                float a = Mathf.Clamp01(0.5f - d / aa);
                float core = Mathf.Clamp01(0.5f - (d + 0.18f) / aa);
                byte g = (byte)Mathf.RoundToInt(Mathf.Lerp(165f, 255f, core));
                px[y * W + x] = new Color32(g, g, g, (byte)Mathf.RoundToInt(a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            _shieldSprite = Sprite.Create(tex, new Rect(0f, 0f, W, H), new Vector2(0.5f, 0.5f), 100f);
            return _shieldSprite;
        }

        private void UpdateHealth(float dt)
        {
            var health = _player != null ? _player.Health : null;
            float n = health != null ? health.Normalized : 1f;
            bool alive = health != null && health.IsAlive;
            if (healthFill != null)
            {
                healthFill.fillAmount = Mathf.MoveTowards(healthFill.fillAmount, n, dt * 2.5f);
                healthFill.color = n < 0.35f ? Enemy : Color.Lerp(Accent, Color.white, n);
            }
            if (healthText != null)
            {
                healthText.text = Mathf.CeilToInt(n * 100f).ToString();
                healthText.color = alive && n < 0.35f
                    ? Color.Lerp(Enemy, Color.white, 0.25f + 0.25f * Mathf.Sin(Time.unscaledTime * 7f))
                    : Color.white;
            }

            UpdateArmor(health, alive, dt);

            _damageFlash = Mathf.MoveTowards(_damageFlash, 0f, dt * 1.6f);
            float low = alive ? Mathf.Clamp01((0.55f - n) / 0.55f) : 0f;
            if (damageVignette != null) damageVignette.color = new Color(0.55f, 0f, 0f, Mathf.Clamp01(_damageFlash * 0.75f + low * 0.55f));
            if (lowHealthOverlay != null)
            {
                float pulse = low > 0.4f ? (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6.5f)) * low : 0f;
                lowHealthOverlay.color = new Color(0.4f, 0f, 0f, pulse * 0.35f);
            }
            if (alive && low > 0.45f && Time.unscaledTime >= _nextHeartbeat && !AudioListener.pause)
            {
                _nextHeartbeat = Time.unscaledTime + Mathf.Lerp(1.15f, 0.68f, low);
                PlayLowHealthHeartbeat(Mathf.Lerp(0.35f, 0.72f, low));
            }
        }

        private static AudioClip _heartbeatClip;

        private static void PlayLowHealthHeartbeat(float volume)
        {
            if (_heartbeatClip == null) _heartbeatClip = CreateHeartbeatClip();
            if (_heartbeatClip != null)
                LR2Audio.PlayClip(_heartbeatClip, Vector3.zero, volume, 1f, 1f, 500f, 0f, AudioCategory.Sfx, 40, 0f, null, true);
        }

        private static AudioClip CreateHeartbeatClip()
        {
            const int sr = 44100;
            const float dur = 0.46f;
            int n = Mathf.RoundToInt(sr * dur);
            var data = new float[n];
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float lub = Mathf.Clamp01(t / 0.012f) * Mathf.Exp(-t * 22f) * Mathf.Sin(2f * Mathf.PI * 52f * t) * 0.78f;
                float t2 = t - 0.16f;
                float dub = t2 > 0f ? Mathf.Clamp01(t2 / 0.012f) * Mathf.Exp(-t2 * 26f) * Mathf.Sin(2f * Mathf.PI * 42f * t2) * 0.62f : 0f;
                lp += 0.14f * ((lub + dub) - lp);
                data[i] = Mathf.Clamp(lp * 0.90f, -0.88f, 0.88f);
            }
            var c = AudioClip.Create("LR2_LowHealthHeartbeat", n, 1, sr, false);
            c.SetData(data, 0);
            return c;
        }

        // ------------------------------------------------------------------ damage direction
        private void SpawnDamageIndicator(Vector3 source)
        {
            if (damageIndicatorTemplate == null || damageIndicatorRoot == null) return;
            Image img = null;
            foreach (var d in _damageIndicators)
                if (Time.unscaledTime - d.time > 2f) { img = d.img; break; }
            if (img == null)
            {
                img = Instantiate(damageIndicatorTemplate, damageIndicatorRoot);
                img.gameObject.SetActive(true);
            }
            _damageIndicators.RemoveAll(d => d.img == img);
            _damageIndicators.Add((img, source, Time.unscaledTime));
        }

        private void UpdateDamageIndicators()
        {
            if (_damageIndicators.Count == 0 || _soldier == null) return;
            Vector3 fwd = _soldier.AimDirection;
            fwd.y = 0f;
            foreach (var d in _damageIndicators)
            {
                float age = Time.unscaledTime - d.time;
                Vector3 to = d.source - _soldier.transform.position;
                to.y = 0f;
                float angle = Vector3.SignedAngle(fwd, to, Vector3.up);
                d.img.rectTransform.localEulerAngles = new Vector3(0f, 0f, -angle);
                d.img.color = new Color(0.9f, 0.08f, 0.05f, Mathf.Clamp01(1f - age / 2f) * 0.85f);
            }
        }

        // ------------------------------------------------------------------ kill feed / popups / medals
        private string ColoredName(Combatant c)
        {
            if (c == null) return "";
            Color col = c == _player ? Accent : TeamUtil.ColorFor(_player, c);
            return $"<color=#{ColorUtility.ToHtmlStringRGB(col)}>{c.DisplayName}</color>";
        }

        private void OnKilled(KillEvent e)
        {
            if (killFeedTemplate == null || killFeedRoot == null || e.victim == null) return;
            string weapon = string.IsNullOrEmpty(e.weaponName) ? "" : e.weaponName.ToUpperInvariant();
            string line = e.killer != null && e.killer != e.victim
                ? $"{ColoredName(e.killer)}  <color=#C8C8C8><size=80%>[{weapon}]</size></color>{(e.headshot ? " <color=#FFD27A><size=75%>HEADSHOT</size></color>" : "")}  {ColoredName(e.victim)}"
                : $"{ColoredName(e.victim)}  <color=#C8C8C8><size=80%>[{weapon}]</size></color>";
            var t = Instantiate(killFeedTemplate, killFeedRoot);
            t.gameObject.SetActive(true);
            t.text = line;
            t.transform.SetAsFirstSibling();
            _feed.Insert(0, (t, Time.unscaledTime));
            while (_feed.Count > killFeedMax)
            {
                Destroy(_feed[_feed.Count - 1].text.gameObject);
                _feed.RemoveAt(_feed.Count - 1);
            }
        }

        private void UpdateFeed()
        {
            for (int i = _feed.Count - 1; i >= 0; i--)
            {
                float age = Time.unscaledTime - _feed[i].time;
                if (age > killFeedLifetime)
                {
                    Destroy(_feed[i].text.gameObject);
                    _feed.RemoveAt(i);
                    continue;
                }
                _feed[i].text.alpha = Mathf.Clamp01((killFeedLifetime - age) / 0.6f);
            }
        }

        private void OnScore(ScoreEvent e)
        {
            if (e.combatant != _player || _player == null || popupTemplate == null) return;
            AddPopup($"<color=#{ColorUtility.ToHtmlStringRGB(Accent)}>+{e.points}</color>  {e.reason}");
        }

        private void OnMedal(MedalEvent e)
        {
            if (e.combatant != _player || _player == null) return;
            _medalQueue.Enqueue(e.title);
            if (e.points > 0) AddPopup($"<color=#{ColorUtility.ToHtmlStringRGB(Accent)}>+{e.points}</color>  {e.title}");
        }

        private void AddPopup(string text)
        {
            if (popupTemplate == null || popupRoot == null) return;
            var t = Instantiate(popupTemplate, popupRoot);
            t.gameObject.SetActive(true);
            t.text = text;
            _popups.Add((t, Time.unscaledTime));
        }

        private void UpdatePopups()
        {
            for (int i = _popups.Count - 1; i >= 0; i--)
            {
                float age = Time.unscaledTime - _popups[i].time;
                if (age > 2.2f)
                {
                    Destroy(_popups[i].text.gameObject);
                    _popups.RemoveAt(i);
                    continue;
                }
                var t = _popups[i].text;
                t.alpha = age < 0.12f ? age / 0.12f : Mathf.Clamp01((2.2f - age) / 0.5f);
                int stackIndex = _popups.Count - 1 - i;
                t.rectTransform.anchoredPosition = new Vector2(0f, stackIndex * -30f + age * 6f);
            }

            if (medalText == null || medalGroup == null) return;
            float since = Time.unscaledTime - _medalShownAt;
            if (since > 1.9f && _medalQueue.Count > 0)
            {
                medalText.text = _medalQueue.Dequeue();
                _medalShownAt = Time.unscaledTime;
                since = 0f;
            }
            medalGroup.alpha = since < 0.15f ? since / 0.15f : Mathf.Clamp01((1.8f - since) / 0.4f);
            medalGroup.transform.localScale = Vector3.one * (since < 0.2f ? Mathf.Lerp(1.35f, 1f, since / 0.2f) : 1f);
        }

        // ------------------------------------------------------------------ match status / center text
        private void OnMatchState(MatchStateEvent e)
        {
            if (e.current == MatchState.Playing) _playingStartTime = Time.unscaledTime;
        }

        private void UpdateMatchStatus(float dt)
        {
            var mm = MatchManager.Instance;
            bool has = mm != null && mm.Settings != null;
            if (modeText != null) modeText.text = has ? (mm.IsSuddenDeath ? "SUDDEN DEATH" : mm.Settings.displayName.ToUpperInvariant()) : "TRAINING";
            if (!has) return;
            float tr = Mathf.Max(0f, mm.State == MatchState.Countdown ? mm.Settings.timeLimitSeconds : mm.TimeRemaining);
            if (timerText != null)
            {
                timerText.text = $"{Mathf.FloorToInt(tr / 60f)}:{Mathf.FloorToInt(tr % 60f):00}";
                timerText.color = tr < 30f && mm.State == MatchState.Playing ? Enemy : Color.white;
            }
            int mine = mm.GetDisplayScore(_player);
            int theirs = mm.GetBestOpponentScore(_player);
            int limit = Mathf.Max(1, mm.ScoreLimit);
            if (myScoreText != null) myScoreText.text = mine.ToString();
            if (enemyScoreText != null) enemyScoreText.text = theirs.ToString();
            if (myScoreBar != null) myScoreBar.fillAmount = Mathf.MoveTowards(myScoreBar.fillAmount, (float)mine / limit, dt * 3.5f);
            if (enemyScoreBar != null) enemyScoreBar.fillAmount = Mathf.MoveTowards(enemyScoreBar.fillAmount, (float)theirs / limit, dt * 3.5f);
            // B88: set once per frame here only (a second, throttled writer made "15" and "FIRST TO 15" flicker, user 11-19)
            if (limitText != null) limitText.text = $"FIRST TO {limit}";
            UpdateScoreLabels(mm);
        }

        private void UpdateCenter()
        {
            if (centerText == null) return;
            var mm = MatchManager.Instance;
            string main = "", sub = "";
            if (mm != null && mm.State == MatchState.Countdown)
            {
                main = Mathf.CeilToInt(mm.CountdownRemaining).ToString();
                sub = "MATCH STARTS IN";
            }
            else if (mm != null && mm.State == MatchState.Playing && Time.unscaledTime - _playingStartTime < 1.4f)
            {
                main = "FIGHT";
            }
            else if (_player != null && !_player.IsAlive && (mm == null || mm.State == MatchState.Playing))
            {
                var killer = _player.Health != null ? _player.Health.LastDamage.attacker : null;
                main = killer != null && killer != _player ? $"KILLED BY {ColoredName(killer)}" : "YOU DIED";
                float r = mm != null ? mm.GetRespawnRemaining(_player) : 0f;
                sub = r > 0f ? $"RESPAWNING IN {Mathf.CeilToInt(r)}" : "";
            }
            centerText.text = main;
            if (subText != null) subText.text = sub;
        }

        // ------------------------------------------------------------------ minimap / compass
        private Image GetIcon(int index)
        {
            while (_iconPool.Count <= index)
            {
                var img = Instantiate(minimapIconTemplate, minimapIconRoot);
                _iconPool.Add(img);
            }
            return _iconPool[index];
        }

        private void UpdateMinimap()
        {
            var data = MinimapData.Current;
            if (minimapRotator == null || _soldier == null) return;
            float yaw = _soldier.Yaw;
            minimapRotator.localEulerAngles = new Vector3(0f, 0f, yaw);

            if (data != null && minimapImage != null && data.mapTexture != null)
            {
                minimapImage.texture = data.mapTexture;
                float ppm = minimapRadius / Mathf.Max(5f, minimapRange);
                Vector2 size = data.WorldSize * ppm;
                minimapImage.rectTransform.sizeDelta = size;
                Vector2 uv = data.WorldToUV(_soldier.transform.position);
                minimapImage.rectTransform.anchoredPosition = new Vector2((0.5f - uv.x) * size.x, (0.5f - uv.y) * size.y);
            }

            if (minimapIconTemplate == null || minimapIconRoot == null) return;
            int used = 0;
            float scale = minimapRadius / Mathf.Max(5f, minimapRange);
            Quaternion inv = Quaternion.Euler(0f, -yaw, 0f);
            foreach (var c in Combatant.All)
            {
                if (c == null || c == _player || !c.IsAlive) continue;
                bool friendly = _player != null && !TeamUtil.AreHostile(_player, c);
                bool drone = c.Team == TeamId.Hostile;
                Vector3 offset = c.Position - _soldier.transform.position;
                float dist = new Vector2(offset.x, offset.z).magnitude;
                bool visible = friendly || (Time.time - c.LastLoudShotTime < 2.5f) || (drone && dist < 15f);
                if (!visible) continue;
                Vector3 local = inv * offset;
                Vector2 p = new Vector2(local.x, local.z) * scale;
                if (p.magnitude > minimapRadius - 6f) p = p.normalized * (minimapRadius - 6f);
                var icon = GetIcon(used++);
                icon.gameObject.SetActive(true);
                icon.rectTransform.anchoredPosition = p;
                float pingAge = Time.time - c.LastLoudShotTime;
                float fade = friendly || drone ? 1f : Mathf.Clamp01(1f - Mathf.Max(0f, pingAge - 1.4f) / 1.1f);
                Color baseCol = drone ? TeamUtil.DroneColor : TeamUtil.ColorFor(_player, c);
                baseCol.a *= fade;
                icon.color = baseCol;
            }
            for (int ac = 0; ac < AmmoCrate.All.Count; ac++)
            {
                var crate = AmmoCrate.All[ac];
                if (crate == null || !crate.gameObject.activeInHierarchy) continue;
                Vector3 aOff = crate.transform.position - _soldier.transform.position;
                Vector3 aLoc = inv * aOff;
                Vector2 ap = new Vector2(aLoc.x, aLoc.z) * scale;
                if (ap.magnitude > minimapRadius - 7f) continue;
                var icon = GetIcon(used++);
                icon.gameObject.SetActive(true);
                icon.rectTransform.anchoredPosition = ap;
                icon.rectTransform.localScale = Vector3.one * 0.85f;
                icon.color = crate.IsReady ? Accent : new Color(0.55f, 0.55f, 0.55f, 0.45f);
            }
            for (int i = used; i < _iconPool.Count; i++) _iconPool[i].gameObject.SetActive(false);

            var flag = FlagZoneController.Instance;
            if (_flagMinimapIcon != null)
            {
                if (flag != null)
                {
                    Vector3 fOff = flag.ActiveFlagPosition - _soldier.transform.position;
                    Vector3 fLoc = inv * fOff;
                    Vector2 fp = new Vector2(fLoc.x, fLoc.z) * scale;
                    if (fp.magnitude > minimapRadius - 8f) fp = fp.normalized * (minimapRadius - 8f);
                    _flagMinimapIcon.gameObject.SetActive(true);
                    _flagMinimapIcon.rectTransform.anchoredPosition = fp;
                    float pulse = 1f + 0.18f * Mathf.Sin(Time.unscaledTime * 5f);
                    _flagMinimapIcon.rectTransform.localScale = Vector3.one * pulse;
                    _flagMinimapIcon.color = flag.State == FlagZoneController.ZoneState.Contested
                        ? Accent
                        : (flag.Progress >= 0f ? Friendly : Enemy);
                }
                else
                {
                    _flagMinimapIcon.gameObject.SetActive(false);
                }
            }
        }

        private void BuildCompass()
        {
            if (compassTemplate == null || compassRoot == null) return;
            compassTemplate.gameObject.SetActive(false);
            string[] cardinals = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            for (int i = 0; i < 24; i++)
            {
                float angle = i * 15f;
                var t = Instantiate(compassTemplate, compassRoot);
                t.gameObject.SetActive(true);
                bool cardinal = i % 3 == 0;
                t.text = cardinal ? cardinals[i / 3] : angle.ToString("0");
                t.fontSize = cardinal ? compassTemplate.fontSize * 1.25f : compassTemplate.fontSize * 0.8f;
                _compassLabels.Add((t, angle));
            }
        }

        private void UpdateCompass()
        {
            if (_compassLabels.Count == 0) return;
            float yaw = _soldier != null ? _soldier.Yaw : 0f;
            float half = compassFov * 0.5f;
            foreach (var (label, angle) in _compassLabels)
            {
                float rel = Mathf.DeltaAngle(yaw, angle);
                bool vis = Mathf.Abs(rel) < half;
                label.enabled = vis;
                if (!vis) continue;
                label.rectTransform.anchoredPosition = new Vector2(rel / half * compassWidth * 0.5f, 0f);
                label.alpha = 1f - Mathf.Pow(Mathf.Abs(rel) / half, 3f);
            }

            if (_flagCompassMarker != null)
            {
                var flag = FlagZoneController.Instance;
                if (flag != null && _soldier != null)
                {
                    Vector3 dir = flag.ActiveFlagPosition - _soldier.transform.position;
                    dir.y = 0f;
                    float flagYaw = LR2Math.DirectionToYawPitch(dir.sqrMagnitude > 0.01f ? dir : Vector3.forward).x;
                    float rel = Mathf.DeltaAngle(yaw, flagYaw);
                    bool vis = Mathf.Abs(rel) < half;
                    _flagCompassMarker.enabled = vis;
                    if (vis)
                    {
                        _flagCompassMarker.rectTransform.anchoredPosition = new Vector2(rel / half * compassWidth * 0.5f, -18f);
                        _flagCompassMarker.text = $"+ {dir.magnitude:0}m";
                        _flagCompassMarker.color = flag.State == FlagZoneController.ZoneState.Contested ? Accent : new Color(0.28f, 0.85f, 1f, 1f);
                    }
                }
                else
                {
                    _flagCompassMarker.enabled = false;
                }
            }
        }

        // ------------------------------------------------------------------ BF6 Flag Zone Capture HUD
        private void BuildFlagHud()
        {
            Transform parent = hudGroup != null ? hudGroup.transform : transform;
            var root = new GameObject("BF6FlagCaptureHUD");
            root.transform.SetParent(parent, false);
            var rt = root.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -124f);
            rt.sizeDelta = new Vector2(440f, 54f);
            _flagHudGroup = root.AddComponent<CanvasGroup>();
            _flagHudGroup.alpha = 0f;
            _flagHudGroup.interactable = false;
            _flagHudGroup.blocksRaycasts = false;

            var bg = root.AddComponent<Image>();
            bg.color = new Color(0.04f, 0.06f, 0.09f, 0.72f);

            var titleGo = new GameObject("Title").AddComponent<TextMeshProUGUI>();
            titleGo.transform.SetParent(root.transform, false);
            titleGo.fontSize = 15f;
            titleGo.fontStyle = FontStyles.Bold;
            titleGo.alignment = TextAlignmentOptions.Center;
            titleGo.color = Color.white;
            var tRt = titleGo.rectTransform;
            tRt.anchorMin = new Vector2(0f, 0.52f);
            tRt.anchorMax = new Vector2(1f, 1f);
            tRt.offsetMin = tRt.offsetMax = Vector2.zero;
            _flagTitleText = titleGo;

            var barBg = new GameObject("BarBg").AddComponent<Image>();
            barBg.transform.SetParent(root.transform, false);
            barBg.color = new Color(0.14f, 0.16f, 0.2f, 0.9f);
            var bRt = barBg.rectTransform;
            bRt.anchorMin = new Vector2(0.08f, 0.34f);
            bRt.anchorMax = new Vector2(0.92f, 0.48f);
            bRt.offsetMin = bRt.offsetMax = Vector2.zero;

            var fImg = new GameObject("FillFriendly").AddComponent<Image>();
            fImg.transform.SetParent(barBg.transform, false);
            fImg.color = new Color(0.18f, 0.78f, 1f, 0.96f);
            fImg.type = Image.Type.Filled;
            fImg.fillMethod = Image.FillMethod.Horizontal;
            fImg.fillOrigin = (int)Image.OriginHorizontal.Left;
            var fRt = fImg.rectTransform;
            fRt.anchorMin = new Vector2(0.5f, 0f);
            fRt.anchorMax = new Vector2(1f, 1f);
            fRt.offsetMin = fRt.offsetMax = Vector2.zero;
            _flagFillFriendly = fImg;

            var hImg = new GameObject("FillHostile").AddComponent<Image>();
            hImg.transform.SetParent(barBg.transform, false);
            hImg.color = new Color(0.96f, 0.34f, 0.2f, 0.96f);
            hImg.type = Image.Type.Filled;
            hImg.fillMethod = Image.FillMethod.Horizontal;
            hImg.fillOrigin = (int)Image.OriginHorizontal.Right;
            var hRt = hImg.rectTransform;
            hRt.anchorMin = new Vector2(0f, 0f);
            hRt.anchorMax = new Vector2(0.5f, 1f);
            hRt.offsetMin = hRt.offsetMax = Vector2.zero;
            _flagFillHostile = hImg;

            var subGo = new GameObject("Sub").AddComponent<TextMeshProUGUI>();
            subGo.transform.SetParent(root.transform, false);
            subGo.fontSize = 12.5f;
            subGo.alignment = TextAlignmentOptions.Center;
            subGo.color = new Color(0.8f, 0.85f, 0.9f, 0.92f);
            var sRt = subGo.rectTransform;
            sRt.anchorMin = new Vector2(0f, 0f);
            sRt.anchorMax = new Vector2(1f, 0.34f);
            sRt.offsetMin = sRt.offsetMax = Vector2.zero;
            _flagSubText = subGo;

            if (compassRoot != null)
            {
                var cGo = new GameObject("FlagCompassMarker").AddComponent<TextMeshProUGUI>();
                cGo.transform.SetParent(compassRoot, false);
                cGo.fontSize = 14f;
                cGo.fontStyle = FontStyles.Bold;
                cGo.alignment = TextAlignmentOptions.Center;
                cGo.enabled = false;
                cGo.rectTransform.sizeDelta = new Vector2(100f, 24f);
                _flagCompassMarker = cGo;
            }

            if (minimapIconRoot != null && minimapIconTemplate != null)
            {
                _flagMinimapIcon = Instantiate(minimapIconTemplate, minimapIconRoot);
                _flagMinimapIcon.name = "MinimapFlagIcon";
                _flagMinimapIcon.rectTransform.sizeDelta = new Vector2(13f, 13f);
                _flagMinimapIcon.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);
                _flagMinimapIcon.gameObject.SetActive(false);
            }
        }

        private void UpdateFlagZoneHud(float dt)
        {
            if (_flagHudGroup == null) return;
            var flag = FlagZoneController.Instance;
            var mm = MatchManager.Instance;
            bool show = flag != null && mm != null && mm.State != MatchState.Ended;
            _flagHudGroup.alpha = Mathf.MoveTowards(_flagHudGroup.alpha, show ? 1f : 0f, dt * 6f);
            if (!show) return;

            float prog = flag.Progress;
            if (_flagFillFriendly != null) _flagFillFriendly.fillAmount = Mathf.Clamp01(prog);
            if (_flagFillHostile != null) _flagFillHostile.fillAmount = Mathf.Clamp01(-prog);

            string multNote = flag.LocalPlayerInside
                ? (flag.LocalPlayerDistance <= flag.CoreRadius
                    ? $"CORE {flag.LocalPlayerSpeedMult:0.0}x FAST"
                    : $"PERIMETER {flag.LocalPlayerSpeedMult:0.0}x")
                : $"{flag.LocalPlayerDistance:0}m AWAY";

            string stateStr = flag.State switch
            {
                FlagZoneController.ZoneState.Contested => "CONTESTED — GET CLOSER!",
                FlagZoneController.ZoneState.Capturing => $"CAPTURING  ({multNote})",
                FlagZoneController.ZoneState.Losing => "ENEMY CAPTURING",
                _ => multNote
            };

            if (_flagTitleText != null)
            {
                _flagTitleText.text = $"FLAG // {flag.ActiveSectorName.ToUpperInvariant()}";
                _flagTitleText.color = flag.State == FlagZoneController.ZoneState.Contested ? Accent : Color.white;
            }
            if (_flagSubText != null)
                _flagSubText.text = $"{stateStr}   |   NEXT ROTATION {Mathf.CeilToInt(flag.RotationRemaining)}s";
        }

        // ------------------------------------------------------------------ misc
        private void UpdateMisc(float dt)
        {
            if (fpsText != null)
            {
                fpsText.enabled = LR2Settings.ShowFps;
                _fpsFrames++;
                _fpsTimer += dt;
                if (_fpsTimer >= 0.5f)
                {
                    int fps = Mathf.RoundToInt(_fpsFrames / _fpsTimer);
                    fpsText.text = $"{fps} FPS";
                    fpsText.color = fps >= 58 ? new Color(0.85f, 0.9f, 0.85f, 0.72f) : fps >= 30 ? Accent : Enemy;
                    _fpsFrames = 0;
                    _fpsTimer = 0f;
                }
            }
            if (bodycamOverlay != null)
            {
                bodycamOverlay.enabled = false; // REC overlay removed (user request)
            }
            if (hudGroup != null)
            {
                var mm = MatchManager.Instance;
                float target = mm != null && mm.State == MatchState.Ended ? 0f : 1f;
                hudGroup.alpha = Mathf.MoveTowards(hudGroup.alpha, target, dt * 3f);
            }
        }
    }
}
