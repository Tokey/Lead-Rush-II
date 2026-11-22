using LR2.Audio;
using LR2.Combat;
using LR2.Core;
using LR2.FX;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LR2.UI
{
    /// <summary>
    /// Tactile Battlefield/MW-inspired hit feedback for the local player and killcam replays:
    /// - thick outlined X hitmarker (white = body, orange = headshot, red = kill);
    /// - kill skull badge (gold rim on headshot kills);
    /// - rugged layered acoustics mixing real MW2019 hit/flesh/headshot/kill SFX over deep procedural punch;
    /// - <see cref="ShowReplayHit"/> keeps working cleanly during killcam replays even when the HUD is hidden.
    /// </summary>
    public class HitFeedbackHUD : MonoBehaviour
    {
        public static HitFeedbackHUD Instance { get; private set; }
        private static HitFeedbackHUD _replayOverlay;

        [Header("Hitmarker")]
        public float armLength = 15f;
        public float armThickness = 4f;
        public float armGap = 9f;
        public float holdTime = 0.12f;
        public float fadeTime = 0.2f;
        public Color bodyColor = new Color(1f, 1f, 1f, 1f);
        public Color headColor = new Color(1f, 0.6f, 0.12f, 1f);
        public Color killColor = new Color(1f, 0.16f, 0.12f, 1f);

        [Header("Kill marker")]
        public float skullSize = 42f;
        public float skullHold = 0.55f;
        public float skullFade = 0.3f;

        [Header("Audio")]
        [Range(0f, 1.5f)] public float volume = 1f;

        private RectTransform _root, _xRoot, _skullRT, _hitPipRT;
        private readonly Image[] _arms = new Image[4];
        private Image _skull, _skullRim, _hitPip;
        private TMP_Text _zoneLabel;
        [Tooltip("COD-style hit vibration: pixel amplitude of the marker shake on each hit (scaled up for headshots and kills).")]
        [SerializeField, Range(0f, 12f)] private float vibrationPixels = 4f;
        private float _hitTime = -10f, _killTime = -10f, _pipTime = -10f;
        private Color _color;
        private float _scale = 1f, _thick = 1f;
        private int _streak;
        private bool _headKill;
        private Combatant _player;
        private bool _isReplayOverlay;
        private Vector3 _lastHitWorld;
        private bool _hasWorldPoint;
        private string _lastZoneText = "";

        public static bool HasZoneIndicator => true;
        public string LastZoneText => _lastZoneText;
        public bool IsShowingWorldImpactPip => _hasWorldPoint && (Time.unscaledTime - _pipTime) < 0.36f;

        private static SurfaceLibrary _surfaceLib;
        private static AudioClip _bodyPunch, _headCrack, _killSlam, _killStamp, _targetArmorBreak, _selfArmorBreak;
        public static bool HasDistinctArmorBreakClips
        {
            get
            {
                EnsureClips();
                return _targetArmorBreak != null && _selfArmorBreak != null && _targetArmorBreak != _selfArmorBreak;
            }
        }
        public static int SelfArmorBreakCount { get; private set; }
        public static int TargetArmorBreakCount { get; private set; }
        /// <summary>Armour-hit tint for the hitmarker arms (cool steel blue, Warzone-style).</summary>
        public Color armorColor = new Color(0.62f, 0.84f, 1f, 1f);
        private static Sprite _skullSprite, _skullRimSprite, _pipSprite;

        public static HitFeedbackHUD Create(Transform parent)
        {
            var go = new GameObject("HitFeedback", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.AddComponent<HitFeedbackHUD>();
        }

        /// <summary>
        /// Triggers hitmarker visuals and rugged impact/kill sounds from killcam replays even when main HUD is hidden.
        /// </summary>
        public static void ShowReplayHit(bool headshot, bool kill, Vector3 worldPoint = default, bool armorHit = false, bool armorBroke = false)
        {
            HitFeedbackHUD target = Instance;
            if (target == null || !IsVisibleForReplay(target.transform))
                target = EnsureReplayOverlay();
            if (target == null) return;
            target.TriggerFeedback(headshot, kill, worldPoint, HitZone.Generic, armorHit, armorBroke);
        }

        private static bool IsVisibleForReplay(Transform t)
        {
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            for (Transform p = t; p != null; p = p.parent)
            {
                var cg = p.GetComponent<CanvasGroup>();
                if (cg != null && cg.alpha < 0.85f) return false;
                var cv = p.GetComponent<Canvas>();
                if (cv != null && !cv.enabled) return false;
            }
            return true;
        }

        private static HitFeedbackHUD EnsureReplayOverlay()
        {
            if (_replayOverlay != null && _replayOverlay.gameObject.activeInHierarchy) return _replayOverlay;
            var rootGo = new GameObject("[LR2 ReplayHitFeedback]", typeof(RectTransform));
            var canvas = rootGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 25;
            var scaler = rootGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var hf = Create(rootGo.transform);
            hf._isReplayOverlay = true;
            _replayOverlay = hf;
            return _replayOverlay;
        }

        private void Awake()
        {
            _root = (RectTransform)transform;
            _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0.5f);
            _root.pivot = new Vector2(0.5f, 0.5f);
            _root.anchoredPosition = Vector2.zero;
            _root.sizeDelta = new Vector2(200f, 200f);

            _xRoot = NewRect("X", _root, Vector2.zero);
            for (int i = 0; i < 4; i++)
            {
                var arm = NewRect("Arm" + i, _xRoot, Vector2.zero);
                var img = arm.gameObject.AddComponent<Image>();
                img.raycastTarget = false;
                var o = arm.gameObject.AddComponent<Outline>();
                o.effectColor = new Color(0f, 0f, 0f, 0.65f);
                o.effectDistance = new Vector2(1.4f, -1.4f);
                _arms[i] = img;
            }

            if (_skullSprite == null) BuildSkullSprites();
            _skullRT = NewRect("KillMarker", _root, Vector2.zero);
            _skullRT.sizeDelta = new Vector2(skullSize, skullSize);
            _skullRim = NewRect("Rim", _skullRT, Vector2.zero).gameObject.AddComponent<Image>();
            _skullRim.rectTransform.sizeDelta = new Vector2(skullSize, skullSize) * 1.18f;
            _skullRim.sprite = _skullRimSprite;
            _skullRim.raycastTarget = false;
            _skull = NewRect("Skull", _skullRT, Vector2.zero).gameObject.AddComponent<Image>();
            _skull.rectTransform.sizeDelta = new Vector2(skullSize, skullSize);
            _skull.sprite = _skullSprite;
            _skull.raycastTarget = false;
            var so = _skull.gameObject.AddComponent<Outline>();
            so.effectColor = new Color(0f, 0f, 0f, 0.7f);
            so.effectDistance = new Vector2(1.5f, -1.5f);

            SetAlpha(0f, 0f);
            _hitPipRT = NewRect("WorldHitPip", _root, Vector2.zero);
            _hitPipRT.sizeDelta = new Vector2(18f, 18f);
            _hitPip = _hitPipRT.gameObject.AddComponent<Image>();
            _hitPip.sprite = _pipSprite;
            _hitPip.raycastTarget = false;
            _hitPip.color = new Color(1f, 1f, 1f, 0f);

            var zoneRT = NewRect("HitZoneTag", _root, new Vector2(0f, -34f));
            zoneRT.sizeDelta = new Vector2(180f, 24f);
            _zoneLabel = zoneRT.gameObject.AddComponent<TextMeshProUGUI>();
            _zoneLabel.fontSize = 14f;
            _zoneLabel.fontStyle = FontStyles.Bold;
            _zoneLabel.characterSpacing = 3f;
            _zoneLabel.alignment = TextAlignmentOptions.Center;
            _zoneLabel.raycastTarget = false;
            _zoneLabel.color = new Color(1f, 1f, 1f, 0f);
            var zo = zoneRT.gameObject.AddComponent<Outline>();
            zo.effectColor = new Color(0f, 0f, 0f, 0.85f);
            zo.effectDistance = new Vector2(1.4f, -1.4f);
            if (!_isReplayOverlay && Instance == null) Instance = this;
        }

        private void OnEnable()
        {
            if (!_isReplayOverlay)
            {
                Instance = this;
                GameEvents.Hit += OnHit;
            }
        }

        private void OnDisable()
        {
            if (!_isReplayOverlay) GameEvents.Hit -= OnHit;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_replayOverlay == this) _replayOverlay = null;
        }

        private static RectTransform NewRect(string name, Transform parent, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            return rt;
        }

        private void OnHit(HitEvent e)
        {
            if (_player == null) _player = Combatant.FindLocalPlayer();
            if (_player == null) return;
            // B74 / 11-22: distinct armor-break SFX for self (heavy close vest rupture + warning pulse) vs target.
            if (e.victim == _player && e.armorBroken)
            {
                EnsureClips();
                PlayRaw(_selfArmorBreak, 1.05f * volume, Random.Range(0.97f, 1.03f), 0f);
                SelfArmorBreakCount++;
                if (!e.killed) return;
            }
            if (e.attacker != _player || e.victim == _player || e.damage <= 0f) return;
            TriggerFeedback(e.zone == HitZone.Head, e.killed, e.point, e.zone, e.armorDamage > 0f, e.armorBroken);
        }

        private void TriggerFeedback(bool head, bool killed, Vector3 worldPoint, HitZone zone = HitZone.Generic, bool armorHit = false, bool armorBroke = false)
        {
            float now = Time.unscaledTime;
            _streak = now - _hitTime < 0.25f ? Mathf.Min(_streak + 1, 6) : 0;
            _hitTime = now;
            bool cb = LR2Settings.ColorBlindMode > 0;
            Color bCol = cb ? HitmarkerPalette.Body : bodyColor;
            Color hCol = cb ? HitmarkerPalette.Head : headColor;
            Color kCol = cb ? HitmarkerPalette.Kill : killColor;
            _color = killed ? kCol : head ? hCol : armorHit && !cb ? armorColor : bCol;
            _scale = killed ? 1.45f : armorBroke ? 1.32f : head ? 1.18f : 1f + _streak * 0.03f;
            _thick = killed ? 1.5f : head ? 1.2f : 1f;
            _lastHitWorld = worldPoint;
            _hasWorldPoint = worldPoint != Vector3.zero;
            if (_hasWorldPoint) _pipTime = now;
            _lastZoneText = armorBroke && !killed ? "ARMOR BROKEN" : FormatZoneLabel(zone, head, killed);
            if (_zoneLabel != null) _zoneLabel.text = _lastZoneText;
            if (killed)
            {
                _killTime = now;
                _headKill = head;
                _skullRim.color = head ? new Color(1f, 0.72f, 0.2f, 1f) : new Color(0f, 0f, 0f, 0f);
            }

            if (LR2Settings.HitmarkerSound) PlaySounds(head, killed, armorHit, armorBroke);
        }

        private static string FormatZoneLabel(HitZone z, bool head, bool killed)
        {
            if (z == HitZone.Head || head) return killed ? "HEADSHOT KILL" : "HEAD";
            switch (z)
            {
                case HitZone.Neck: return "NECK";
                case HitZone.UpperTorso: return "UPPER CHEST";
                case HitZone.LowerTorso: return "TORSO";
                case HitZone.Arm: return "ARM HIT";
                case HitZone.Leg: return "LEG HIT";
                default: return killed ? "ELIMINATED" : "TORSO";
            }
        }

        private void Update()
        {
            if (_arms == null || _arms.Length < 4 || _arms[0] == null || _skullRT == null || _skull == null || _skullRim == null)
                return;

            float now = Time.unscaledTime;

            float t = now - _hitTime;
            float a = t < holdTime ? 1f : 1f - Mathf.Clamp01((t - holdTime) / fadeTime);
            float punch = 1f + 0.55f * Mathf.Exp(-t * 28f);
            // COD-style hit vibration (user 11-18 04:20): the whole marker shakes a few pixels and settles in ~0.12 s
            float vib = vibrationPixels * _scale * Mathf.Exp(-t * 26f);
            if (_xRoot != null) _xRoot.anchoredPosition = vib > 0.05f ? new Vector2(Mathf.Sin(t * 230f) * vib, Mathf.Cos(t * 190f) * vib * 0.8f) : Vector2.zero;
            float gap = armGap * _scale * (1f + 0.35f * Mathf.Exp(-t * 20f));
            float len = armLength * _scale * punch;
            float thick = armThickness * _thick;
            for (int i = 0; i < 4; i++)
            {
                float ang = 45f + 90f * i;
                Vector2 dir = new Vector2(Mathf.Cos(ang * Mathf.Deg2Rad), Mathf.Sin(ang * Mathf.Deg2Rad));
                var rt = _arms[i].rectTransform;
                rt.sizeDelta = new Vector2(len, thick);
                rt.anchoredPosition = dir * (gap + len * 0.5f);
                rt.localRotation = Quaternion.Euler(0f, 0f, ang);
                var c = _color;
                c.a = a;
                _arms[i].color = c;
            }

            float k = now - _killTime;
            float ka = k < skullHold ? 1f : 1f - Mathf.Clamp01((k - skullHold) / skullFade);
            float pop = k < 0.12f ? Mathf.Lerp(1.9f, 0.92f, k / 0.12f) : k < 0.2f ? Mathf.Lerp(0.92f, 1f, (k - 0.12f) / 0.08f) : 1f;
            if (k > skullHold) pop *= Mathf.Lerp(1f, 0.75f, Mathf.Clamp01((k - skullHold) / skullFade));
            _skullRT.localScale = Vector3.one * pop;
            _skullRT.anchoredPosition = new Vector2(0f, 52f + 8f * Mathf.Clamp01(k / (skullHold + skullFade)));
            var sc = LR2Settings.ColorBlindMode > 0 ? HitmarkerPalette.Kill : killColor;
            sc.a = ka;
            _skull.color = sc;
            var rc = _skullRim.color;
            rc.a = _headKill ? ka : 0f;
            _skullRim.color = rc;

            if (_zoneLabel != null)
            {
                float zt = now - _hitTime;
                float za = zt < 0.28f ? 1f : 1f - Mathf.Clamp01((zt - 0.28f) / 0.22f);
                var zc = _color;
                zc.a = za * 0.92f;
                _zoneLabel.color = zc;
            }
            if (_hitPip != null && _hitPipRT != null)
            {
                float pt = now - _pipTime;
                float pa = _hasWorldPoint ? Mathf.Clamp01(1f - pt / 0.36f) : 0f;
                Camera cam = Camera.main;
                if (pa > 0.01f && cam != null)
                {
                    Vector3 vp = cam.WorldToViewportPoint(_lastHitWorld);
                    if (vp.z > 0.2f && vp.x >= 0.02f && vp.x <= 0.98f && vp.y >= 0.02f && vp.y <= 0.98f)
                    {
                        var canvasRT = _root.parent as RectTransform;
                        Vector2 canvasSize = canvasRT != null ? canvasRT.rect.size : new Vector2(1920f, 1080f);
                        _hitPipRT.anchoredPosition = new Vector2((vp.x - 0.5f) * canvasSize.x, (vp.y - 0.5f) * canvasSize.y);
                        float ps = Mathf.Lerp(1.35f, 0.7f, Mathf.Clamp01(pt / 0.36f));
                        _hitPipRT.localScale = Vector3.one * ps;
                        var pc = _color;
                        pc.a = pa * 0.9f;
                        _hitPip.color = pc;
                    }
                    else _hitPip.color = new Color(_color.r, _color.g, _color.b, 0f);
                }
                else _hitPip.color = new Color(_color.r, _color.g, _color.b, 0f);
            }
        }

        private void SetAlpha(float x, float skull)
        {
            for (int i = 0; i < 4; i++) if (_arms[i] != null) _arms[i].color = new Color(1f, 1f, 1f, x);
            if (_skull != null) _skull.color = new Color(1f, 1f, 1f, skull);
            if (_skullRim != null) _skullRim.color = new Color(1f, 1f, 1f, 0f);
        }

        // ------------------------------------------------------------------ rugged layered combat SFX
        private void PlaySounds(bool head, bool killed, bool armorHit = false, bool armorBroke = false)
        {
            EnsureClips();
            // B74 armour layer on top of the normal confirm: a plate "tink" while armour soaks, a ceramic shatter when it breaks.
            if (armorBroke)
            {
                PlayRaw(_targetArmorBreak, 1.05f * volume, Random.Range(0.98f, 1.04f), 0f);
                TargetArmorBreakCount++;
            }
            else if (armorHit && !killed && !head)
            {
                var cs = LR2CommonSounds.Instance;
                if (cs != null && cs.hitmarkerArmor.IsValid) PlaySet(cs.hitmarkerArmor, 0.6f * volume, Random.Range(1.02f, 1.08f), 0f);
            }
            var common = LR2CommonSounds.Instance;
            if (_surfaceLib == null) _surfaceLib = Resources.Load<SurfaceLibrary>("LR2/SurfaceLibrary");

            float v = volume;
            float streakPitch = Random.Range(0.96f, 1.03f) + _streak * 0.012f;

            if (killed)
            {
                if (common != null)
                {
                    if (common.killConfirm.IsValid) PlaySet(common.killConfirm, 1.0f * v, Random.Range(0.98f, 1.02f), 0f);
                    var hitSet = head && common.hitmarkerHeadshot.IsValid ? common.hitmarkerHeadshot : common.hitmarker;
                    if (hitSet.IsValid) PlaySet(hitSet, 0.92f * v, head ? 1.02f : 0.96f, 0f);
                }
                if (_surfaceLib != null)
                {
                    var flesh = head && _surfaceLib.headshotImpact.IsValid ? _surfaceLib.headshotImpact : _surfaceLib.fleshImpact;
                    if (flesh.IsValid) PlaySet(flesh, 0.80f * v, Random.Range(0.95f, 1.04f), 0f);
                }
                PlayRaw(_killSlam, 0.98f * v, Random.Range(0.97f, 1.02f), 0f);
                PlayRaw(_killStamp, 0.60f * v, 1f, 0.025f);
                if (head) PlayRaw(_headCrack, 0.78f * v, Random.Range(0.98f, 1.03f), 0f);
            }
            else if (head)
            {
                if (common != null)
                {
                    if (common.hitmarkerHeadshot.IsValid) PlaySet(common.hitmarkerHeadshot, 0.96f * v, Random.Range(0.98f, 1.03f), 0f);
                    if (common.hitmarkerArmor.IsValid) PlaySet(common.hitmarkerArmor, 0.55f * v, 1.04f, 0f);
                }
                if (_surfaceLib != null && _surfaceLib.headshotImpact.IsValid)
                    PlaySet(_surfaceLib.headshotImpact, 0.72f * v, Random.Range(0.96f, 1.04f), 0f);
                PlayRaw(_headCrack, 0.80f * v, Random.Range(0.97f, 1.03f), 0f);
                PlayRaw(_bodyPunch, 0.45f * v, 1.08f, 0f);
            }
            else
            {
                if (common != null && common.hitmarker.IsValid)
                    PlaySet(common.hitmarker, 0.88f * v, streakPitch, 0f);
                if (_surfaceLib != null && _surfaceLib.fleshImpact.IsValid)
                    PlaySet(_surfaceLib.fleshImpact, 0.62f * v, Random.Range(0.95f, 1.05f), 0f);
                PlayRaw(_bodyPunch, 0.68f * v, streakPitch, 0f);
            }
        }

        private static void PlaySet(SoundSet set, float vol, float pitch, float delay)
        {
            if (set == null || !set.IsValid) return;
            // Use Weapons category with priority 0 and spatialBlend 0 so player gunfire ducking never silences hit confirms
            LR2Audio.PlayClip(set.Pick(), Vector3.zero, vol, pitch, 1f, 500f, 0f, AudioCategory.Weapons, 0, delay);
        }

        private static void PlayRaw(AudioClip clip, float vol, float pitch, float delay)
        {
            LR2Audio.PlayClip(clip, Vector3.zero, vol, pitch, 1f, 500f, 0f, AudioCategory.Weapons, 0, delay);
        }

        private static void EnsureClips()
        {
            if (_bodyPunch != null && _headCrack != null && _killSlam != null && _killStamp != null &&
                _targetArmorBreak != null && _selfArmorBreak != null) return;
            const int sr = 44100;
            var rng = new System.Random(1337);

            // Heavy chest/flesh impact punch: low-end sweep + saturated meaty snap (no high toy blip)
            _bodyPunch = Synth("LR2_HF_BodyPunch", 0.085f, sr, t =>
            {
                float n = (float)(rng.NextDouble() * 2.0 - 1.0);
                float f = Mathf.Lerp(165f, 44f, Mathf.Clamp01(t * 22f));
                float thump = (float)System.Math.Tanh(Mathf.Sin(2f * Mathf.PI * f * t) * 1.9f) * Mathf.Exp(-t * 42f);
                float snap = n * Mathf.Exp(-t * 210f) * 0.65f;
                float meat = Mathf.Sin(2f * Mathf.PI * 520f * t) * Mathf.Exp(-t * 95f) * 0.25f;
                return thump * 0.78f + snap + meat;
            });

            // Rugged helmet crack + metallic shear + skull snap
            _headCrack = Synth("LR2_HF_HeadCrack", 0.14f, sr, t =>
            {
                float n = (float)(rng.NextDouble() * 2.0 - 1.0);
                float crack = n * Mathf.Exp(-t * 170f) * 0.72f;
                float metal = (Mathf.Sin(2f * Mathf.PI * 1980f * t) * 0.42f
                             + Mathf.Sin(2f * Mathf.PI * 3140f * t) * 0.34f
                             + Mathf.Sin(2f * Mathf.PI * 4620f * t) * 0.20f) * Mathf.Exp(-t * 36f);
                float jaw = (float)System.Math.Tanh(Mathf.Sin(2f * Mathf.PI * 220f * t) * 1.8f) * Mathf.Exp(-t * 50f) * 0.55f;
                return crack + metal + jaw;
            });

            // Deep visceral kill confirm thud
            _killSlam = Synth("LR2_HF_KillSlam", 0.24f, sr, t =>
            {
                float n = (float)(rng.NextDouble() * 2.0 - 1.0);
                float f = Mathf.Lerp(175f, 36f, Mathf.Clamp01(t * 11f));
                float sub = (float)System.Math.Tanh(Mathf.Sin(2f * Mathf.PI * f * t) * 2.1f) * Mathf.Exp(-t * 14f);
                float crunch = n * Mathf.Exp(-t * 140f) * 0.55f;
                return sub * 0.92f + crunch;
            });

            // Authoritative low-mid steel lock-in stab (replaces bright bell chime)
            _killStamp = Synth("LR2_HF_KillStamp", 0.16f, sr, t =>
            {
                float n = (float)(rng.NextDouble() * 2.0 - 1.0);
                float low = Mathf.Sin(2f * Mathf.PI * 185f * t) * Mathf.Exp(-t * 24f) * 0.55f;
                float mid = (Mathf.Sin(2f * Mathf.PI * 620f * t) + 0.4f * Mathf.Sin(2f * Mathf.PI * 930f * t)) * Mathf.Exp(-t * 28f) * 0.42f;
                float bite = n * Mathf.Exp(-t * 260f) * 0.35f;
                return (float)System.Math.Tanh((low + mid + bite) * 1.35f);
            });

            _targetArmorBreak = SynthTargetArmorBreak(sr);
            _selfArmorBreak = SynthSelfArmorBreak(sr);
        }

        // Target armor break: bright, crisp ceramic/glass shatter + high confirm harmonic snap so breaking an enemy's plates pops cleanly.
        private static AudioClip SynthTargetArmorBreak(int sr)
        {
            var rng = new System.Random(4242);
            float prevNoise = 0f, crackle = 0f;
            return Synth("LR2_HF_TargetArmorBreak", 0.40f, sr, t =>
            {
                float n = (float)(rng.NextDouble() * 2.0 - 1.0);
                float hp = n - prevNoise;
                prevNoise = n;
                float crack = hp * Mathf.Exp(-t * 105f) * 0.90f;
                float split = t > 0.040f ? hp * Mathf.Exp(-(t - 0.040f) * 130f) * 0.65f : 0f;
                float chime = (Mathf.Sin(2f * Mathf.PI * 1480f * t) * 0.28f
                             + Mathf.Sin(2f * Mathf.PI * 2220f * t) * 0.24f
                             + Mathf.Sin(2f * Mathf.PI * 3520f * t) * 0.16f
                             + Mathf.Sin(2f * Mathf.PI * 5680f * t) * 0.10f) * Mathf.Exp(-t * 19f);
                if (rng.NextDouble() < 0.004 * Mathf.Exp(-t * 6.5f)) crackle = 0.50f + (float)rng.NextDouble() * 0.50f;
                crackle *= 0.92f;
                float shards = hp * crackle * 0.62f;
                float punch = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(210f, 80f, Mathf.Clamp01(t * 18f)) * t) * Mathf.Exp(-t * 36f) * 0.38f;
                return (float)System.Math.Tanh((crack + split + chime + shards + punch) * 1.32f);
            });
        }

        // Self armor break: heavy close-chest vest rupture + muffled hollow ceramic crunch + two-tone armor-depleted warning drop.
        private static AudioClip SynthSelfArmorBreak(int sr)
        {
            var rng = new System.Random(8484);
            float lp = 0f, ripLp = 0f;
            return Synth("LR2_HF_SelfArmorBreak", 0.52f, sr, t =>
            {
                float n = (float)(rng.NextDouble() * 2.0 - 1.0);
                // Heavy chest impact & vest rip
                float chestF = Mathf.Lerp(190f, 44f, Mathf.Clamp01(t * 11f));
                float chest = (float)System.Math.Tanh(Mathf.Sin(2f * Mathf.PI * chestF * t) * 2.1f) * Mathf.Exp(-t * 16f) * 0.85f;
                ripLp += 0.14f * (n - ripLp);
                float hollow = (Mathf.Sin(2f * Mathf.PI * 410f * t) * 0.30f + Mathf.Sin(2f * Mathf.PI * 665f * t) * 0.22f) * Mathf.Exp(-t * 24f);
                float rip = ripLp * Mathf.Exp(-t * 14f) * 0.72f;
                // Distinct descending two-tone self warning pulse (520 Hz -> 310 Hz at t=0.14s)
                float toneF = t < 0.14f ? 520f : 310f;
                float toneLocal = t < 0.14f ? t : (t - 0.14f);
                float toneEnv = Mathf.Clamp01(toneLocal / 0.008f) * Mathf.Exp(-toneLocal * 9.5f);
                float alert = (Mathf.Sin(2f * Mathf.PI * toneF * toneLocal) * 0.36f + Mathf.Sin(2f * Mathf.PI * toneF * 0.5f * toneLocal) * 0.20f) * toneEnv;
                float combined = chest + hollow + rip + alert;
                lp += 0.28f * (combined - lp);
                return (float)System.Math.Tanh(lp * 1.35f);
            });
        }

        private static AudioClip Synth(string name, float seconds, int sr, System.Func<float, float> fn)
        {
            int n = Mathf.RoundToInt(seconds * sr);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float fadeOut = Mathf.Clamp01((n - i) / (sr * 0.01f));
                data[i] = Mathf.Clamp(fn(t) * fadeOut, -0.98f, 0.98f);
            }
            var clip = AudioClip.Create(name, n, 1, sr, false);
            clip.SetData(data, 0);
            return clip;
        }

        // ------------------------------------------------------------------ skull sprite
        private static void BuildSkullSprites()
        {
            const int S = 128;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "LR2_KillSkull" };
            var rim = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "LR2_KillSkullRim" };
            var px = new Color32[S * S];
            var rpx = new Color32[S * S];
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f) / S * 2f - 1f;
                float v = (y + 0.5f) / S * 2f - 1f;
                float d = SkullSdf(u, v);
                float aa = 2f / S;
                byte a = (byte)(Mathf.Clamp01(0.5f - d / aa) * 255f);
                px[y * S + x] = new Color32(255, 255, 255, a);
                float ring = Mathf.Abs(Mathf.Sqrt(u * u + v * v) - 0.93f) - 0.045f;
                byte ra = (byte)(Mathf.Clamp01(0.5f - ring / aa) * 255f);
                rpx[y * S + x] = new Color32(255, 255, 255, ra);
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            rim.SetPixels32(rpx);
            rim.Apply(false, true);
            _skullSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
            _skullRimSprite = Sprite.Create(rim, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);

            const int P = 32;
            var ptex = new Texture2D(P, P, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "LR2_HitPip" };
            var ppx = new Color32[P * P];
            for (int y = 0; y < P; y++)
            for (int x = 0; x < P; x++)
            {
                float u = (x + 0.5f) / P * 2f - 1f;
                float v = (y + 0.5f) / P * 2f - 1f;
                float diamond = Mathf.Abs(Mathf.Abs(u) + Mathf.Abs(v) - 0.72f);
                float core = Mathf.Sqrt(u * u + v * v);
                float d = Mathf.Min(diamond - 0.08f, core - 0.16f);
                byte pa = (byte)(Mathf.Clamp01(0.5f - d / (2.5f / P)) * 255f);
                ppx[y * P + x] = new Color32(255, 255, 255, pa);
            }
            ptex.SetPixels32(ppx);
            ptex.Apply(false, true);
            _pipSprite = Sprite.Create(ptex, new Rect(0, 0, P, P), new Vector2(0.5f, 0.5f), 100f);
        }

        private static float SkullSdf(float u, float v)
        {
            float cranium = Ellipse(u, v - 0.18f, 0.62f, 0.56f);
            float jaw = Box(u, v + 0.42f, 0.34f, 0.2f, 0.12f);
            float cheeks = Box(u, v + 0.12f, 0.48f, 0.2f, 0.18f);
            float shape = Mathf.Min(cranium, Mathf.Min(jaw, cheeks));

            float eyeL = Ellipse(u + 0.24f, v - 0.08f, 0.17f, 0.15f);
            float eyeR = Ellipse(u - 0.24f, v - 0.08f, 0.17f, 0.15f);
            float nose = Triangle(u, v + 0.17f, 0.075f, 0.1f);
            float holes = Mathf.Min(Mathf.Min(eyeL, eyeR), nose);

            float teeth = 1f;
            for (int i = -2; i <= 2; i++)
                teeth = Mathf.Min(teeth, Box(u - i * 0.13f, v + 0.45f, 0.018f, 0.13f, 0f));
            float bite = Box(u, v + 0.4f, 0.3f, 0.016f, 0f);
            holes = Mathf.Min(holes, Mathf.Min(teeth, bite));

            return Mathf.Max(shape, -holes);
        }

        private static float Ellipse(float x, float y, float rx, float ry)
        {
            float k = Mathf.Sqrt((x * x) / (rx * rx) + (y * y) / (ry * ry));
            return (k - 1f) * Mathf.Min(rx, ry);
        }

        private static float Box(float x, float y, float hx, float hy, float r)
        {
            float qx = Mathf.Abs(x) - hx + r, qy = Mathf.Abs(y) - hy + r;
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        private static float Triangle(float x, float y, float w, float h)
        {
            float k = (h - y) / (2f * h);
            float edge = Mathf.Abs(x) - w * Mathf.Clamp01(k);
            return Mathf.Max(edge, Mathf.Max(-h - y, y - h));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            _replayOverlay = null;
            _surfaceLib = null;
            _bodyPunch = _headCrack = _killSlam = _killStamp = _targetArmorBreak = _selfArmorBreak = null;
            SelfArmorBreakCount = 0;
            TargetArmorBreakCount = 0;
            _skullSprite = _skullRimSprite = _pipSprite = null;
        }
    }
}
