using LR2.Combat;
using LR2.Core;
using LR2.Game;
using LR2.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LR2.Audio
{
    /// <summary>
    /// Adaptive, progressively escalating music system (B136o revamped):
    /// - Menu: distinct tactical dossier theme (<c>Resources/LR2/Music/Music_Menu</c>).
    /// - Gameplay: per-map 3-stem adaptive loops (<c>Music_{Map}_{Calm,Tension,Combat}</c> with fallback) plus
    ///   <c>Music_Overdrive</c> layer that starts super mild early in the match and escalates relentlessly into an
    ///   insane endgame/sudden-death climax, followed by distinct Winning vs Losing stingers when the match ends.
    /// </summary>
    public class LR2MusicDirector : MonoBehaviour
    {
        public static LR2MusicDirector Instance { get; private set; }

        [Header("Headroom caps (multiplied by LR2Settings.MusicVolume)")]
        [SerializeField, Range(0f, 1f)] private float menuCeiling = 0.24f;
        [SerializeField, Range(0f, 1f)] private float calmCeiling = 0.14f;
        [SerializeField, Range(0f, 1f)] private float tensionCeiling = 0.28f;
        [SerializeField, Range(0f, 1f)] private float combatCeiling = 0.44f;
        [SerializeField, Range(0f, 1f)] private float overdriveCeiling = 0.42f;
        [SerializeField, Range(0f, 1f)] private float momentumCeiling = 0.38f;
        [SerializeField, Range(0f, 1f)] private float outcomeCeiling = 0.56f;

        private AudioSource _menuSrc;
        private AudioSource _calmSrc;
        private AudioSource _tensionSrc;
        private AudioSource _combatSrc;
        private AudioSource _overdriveSrc;
        private AudioSource _winMomentumSrc;
        private AudioSource _loseMomentumSrc;
        private AudioSource _outcomeSrc;
        private AudioSource _drumrollSrc;
        private AudioSource _brassSurgeSrc;
        private AudioLowPassFilter _calmLpf, _tensionLpf, _combatLpf, _overdriveLpf, _winMomLpf, _loseMomLpf;

        private AudioClip _menuClip, _calmClip, _tensionClip, _combatClip, _overdriveClip, _winMomentumClip, _loseMomentumClip, _winClip, _loseClip;
        private AudioClip _drumrollClip, _brassSurgeClip;
        private float _qaMockProgress = -1f;
        private bool _qaMockWinning, _qaMockLosing;
        private bool _isMenuScene;
        private bool _outcomeTriggered;
        private float _intensity;
        private float _targetIntensity;
        private float _combatHoldUntil;
        private float _nextDrumrollAt;
        private float _nextBrassSurgeAt;
        private float _nextProximityScan;
        private float _proximityFloor;
        private Combatant _player;

        /// <summary>Current smoothed combat intensity 0..1.</summary>
        public float Intensity => _intensity;
        /// <summary>Target raw intensity 0..1 driven by live events and match progression.</summary>
        public float TargetIntensity => _targetIntensity;
        public bool IsMenuMode => _isMenuScene;
        public string ActiveMapTheme { get; private set; } = "Default";
        public float MatchProgress { get; private set; }
        public float OverdriveWeight { get; private set; }
        public string LastOutcomePlayed { get; private set; } = "None";
        public bool HasMenuClip => _menuClip != null;
        public bool HasOverdriveClip => _overdriveClip != null;
        public bool HasOutcomeClips => _winClip != null && _loseClip != null;
        public string StateLabel => _isMenuScene
            ? "Menu"
            : _outcomeTriggered
                ? $"End ({LastOutcomePlayed})"
                : WinningMomentumWeight >= 0.35f ? "MomentumWin"
                : LosingMomentumWeight >= 0.35f ? "MomentumLose"
                : OverdriveWeight >= 0.45f ? "Overdrive"
                : _intensity >= 0.65f ? "Combat"
                : _intensity >= 0.28f ? "Tension" : "Calm";
        public float CalmVolume => _calmSrc != null ? _calmSrc.volume : 0f;
        public float TensionVolume => _tensionSrc != null ? _tensionSrc.volume : 0f;
        public float CombatVolume => _combatSrc != null ? _combatSrc.volume : 0f;
        public float OverdriveVolume => _overdriveSrc != null ? _overdriveSrc.volume : 0f;
        public float WinningMomentumWeight { get; private set; }
        public float LosingMomentumWeight { get; private set; }
        public float WinningMomentumVolume => _winMomentumSrc != null ? _winMomentumSrc.volume : 0f;
        public float LosingMomentumVolume => _loseMomentumSrc != null ? _loseMomentumSrc.volume : 0f;
        public bool HasMomentumStems => _winMomentumClip != null && _loseMomentumClip != null;
        public float LastDrumrollTime { get; private set; } = -10f;
        public float LastBrassSurgeTime { get; private set; } = -10f;
        public bool HasWarTornBrassCue => _brassSurgeClip != null && _brassSurgeSrc != null;
        public float MenuVolume => _menuSrc != null ? _menuSrc.volume : 0f;
        public float EffectiveTotalGain => _isMenuScene ? MenuVolume : (CalmVolume + TensionVolume * 0.75f + CombatVolume * 0.85f + OverdriveVolume * 0.85f + WinningMomentumVolume * 0.8f + LosingMomentumVolume * 0.8f);
        public AudioSource[] MusicSources
        {
            get
            {
                var list = new System.Collections.Generic.List<AudioSource>(10);
                if (_menuSrc != null) list.Add(_menuSrc);
                if (_calmSrc != null) list.Add(_calmSrc);
                if (_tensionSrc != null) list.Add(_tensionSrc);
                if (_combatSrc != null) list.Add(_combatSrc);
                if (_overdriveSrc != null) list.Add(_overdriveSrc);
                if (_winMomentumSrc != null) list.Add(_winMomentumSrc);
                if (_loseMomentumSrc != null) list.Add(_loseMomentumSrc);
                if (_outcomeSrc != null) list.Add(_outcomeSrc);
                if (_drumrollSrc != null) list.Add(_drumrollSrc);
                if (_brassSurgeSrc != null) list.Add(_brassSurgeSrc);
                return list.ToArray();
            }
        }

        public void SetQaMomentumPreview(float progress, bool winning, bool losing)
        {
            _qaMockProgress = Mathf.Clamp01(progress);
            _qaMockWinning = winning;
            _qaMockLosing = losing;
        }

        public void ClearQaMomentumPreview()
        {
            _qaMockProgress = -1f;
            _qaMockWinning = _qaMockLosing = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            EnsureInstance();
        }

        public static LR2MusicDirector EnsureInstance()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("[LR2 MusicDirector]");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<LR2MusicDirector>();
            return Instance;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            menuCeiling = Mathf.Clamp(menuCeiling, 0.20f, 0.32f);
            calmCeiling = Mathf.Clamp(calmCeiling, 0.12f, 0.20f);
            tensionCeiling = Mathf.Clamp(tensionCeiling, 0.24f, 0.36f);
            combatCeiling = Mathf.Clamp(combatCeiling, 0.36f, 0.52f);
            overdriveCeiling = Mathf.Clamp(overdriveCeiling, 0.34f, 0.50f);

            LoadSharedClips();
            BuildSources();

            SceneManager.sceneLoaded += OnSceneLoaded;
            GameEvents.ShotFired += OnShotFired;
            GameEvents.Hit += OnHit;
            GameEvents.Killed += OnKilled;
            GameEvents.Noise += OnNoise;

            ConfigureForScene(SceneManager.GetActiveScene().name);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            GameEvents.ShotFired -= OnShotFired;
            GameEvents.Hit -= OnHit;
            GameEvents.Killed -= OnKilled;
            GameEvents.Noise -= OnNoise;
        }

        private void LoadSharedClips()
        {
            _menuClip = Resources.Load<AudioClip>("LR2/Music/Music_Menu");
            _overdriveClip = Resources.Load<AudioClip>("LR2/Music/Music_Overdrive");
            _winMomentumClip = Resources.Load<AudioClip>("LR2/Music/Music_Momentum_Winning");
            _loseMomentumClip = Resources.Load<AudioClip>("LR2/Music/Music_Momentum_Losing");
            _winClip = Resources.Load<AudioClip>("LR2/Music/Music_MatchWin");
            _loseClip = Resources.Load<AudioClip>("LR2/Music/Music_MatchLose");
            LoadMapStems("Default");
        }

        private static string ResolveMapTheme(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return "Default";
            if (sceneName.IndexOf("Deadsector", System.StringComparison.OrdinalIgnoreCase) >= 0) return "Deadsector";
            if (sceneName.IndexOf("Foundry", System.StringComparison.OrdinalIgnoreCase) >= 0) return "Foundry";
            if (sceneName.IndexOf("TestRange", System.StringComparison.OrdinalIgnoreCase) >= 0) return "TestRange";
            if (sceneName.IndexOf("Saltworks", System.StringComparison.OrdinalIgnoreCase) >= 0) return "Saltworks";
            return "Default";
        }

        private void LoadMapStems(string themeKey)
        {
            ActiveMapTheme = themeKey;
            AudioClip LoadStem(string role)
            {
                if (themeKey != "Default")
                {
                    var specific = Resources.Load<AudioClip>($"LR2/Music/Music_{themeKey}_{role}");
                    if (specific != null) return specific;
                }
                return Resources.Load<AudioClip>($"LR2/Music/Music_{role}");
            }

            _calmClip = LoadStem("Calm");
            _tensionClip = LoadStem("Tension");
            _combatClip = LoadStem("Combat");

            if (_calmSrc != null) _calmSrc.clip = _calmClip;
            if (_tensionSrc != null) _tensionSrc.clip = _tensionClip;
            if (_combatSrc != null) _combatSrc.clip = _combatClip;
        }

        private AudioSource CreateMusicSource(string srcName, out AudioLowPassFilter lpf)
        {
            var child = new GameObject(srcName);
            child.transform.SetParent(transform, false);
            var s = child.AddComponent<AudioSource>();
            s.loop = true;
            s.spatialBlend = 0f;
            s.playOnAwake = false;
            s.priority = 8;
            s.bypassEffects = true;
            s.bypassListenerEffects = true;
            s.bypassReverbZones = true;
            s.ignoreListenerPause = true;
            s.volume = 0f;
            lpf = child.AddComponent<AudioLowPassFilter>();
            lpf.cutoffFrequency = 18000f;
            lpf.lowpassResonanceQ = 0.7f;
            lpf.enabled = false;
            return s;
        }

        private void BuildSources()
        {
            _menuSrc = CreateMusicSource("Menu", out _);
            _menuSrc.clip = _menuClip;

            _calmSrc = CreateMusicSource("Stem_Calm", out _calmLpf);
            _tensionSrc = CreateMusicSource("Stem_Tension", out _tensionLpf);
            _combatSrc = CreateMusicSource("Stem_Combat", out _combatLpf);
            _overdriveSrc = CreateMusicSource("Stem_Overdrive", out _overdriveLpf);
            _winMomentumSrc = CreateMusicSource("Stem_MomentumWinning", out _winMomLpf);
            _loseMomentumSrc = CreateMusicSource("Stem_MomentumLosing", out _loseMomLpf);

            _calmSrc.clip = _calmClip;
            _tensionSrc.clip = _tensionClip;
            _combatSrc.clip = _combatClip;
            _overdriveSrc.clip = _overdriveClip;
            _winMomentumSrc.clip = _winMomentumClip;
            _loseMomentumSrc.clip = _loseMomentumClip;

            _outcomeSrc = CreateMusicSource("Music_MatchOutcome", out _);
            _outcomeSrc.loop = false;
            _outcomeSrc.priority = 6;

            _drumrollSrc = CreateMusicSource("Music_FirefightDrumroll", out var drumLpf);
            _drumrollSrc.loop = false;
            if (drumLpf != null) { drumLpf.enabled = true; drumLpf.cutoffFrequency = 4600f; }
            _drumrollClip = BuildFirefightDrumrollClip();
            _drumrollSrc.clip = _drumrollClip;

            _brassSurgeSrc = CreateMusicSource("Music_WarBrassSurge", out var brassLpf);
            _brassSurgeSrc.loop = false;
            _brassSurgeSrc.priority = 10;
            if (brassLpf != null) { brassLpf.enabled = true; brassLpf.cutoffFrequency = 950f; }
            _brassSurgeClip = BuildWarBrassSurgeClip();
            _brassSurgeSrc.clip = _brassSurgeClip;
        }

        private static AudioClip BuildWarBrassSurgeClip()
        {
            const int sr = 44100;
            const float dur = 1.25f;
            int n = Mathf.RoundToInt(sr * dur);
            float[] data = new float[n];
            float lp = 0f;
            float lpAlpha = Mathf.Clamp01(2f * Mathf.PI * 820f / sr);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float swell = Mathf.Clamp01(t / 0.14f) * Mathf.Exp(-Mathf.Max(0f, t - 0.14f) * 2.1f);
                float subF = Mathf.Lerp(76f, 36.71f, Mathf.Clamp01(t * 4.5f));
                float sub = Mathf.Sin(2f * Mathf.PI * subF * t) * Mathf.Exp(-t * 4.2f) * 0.55f;
                float sawD = 2f * ((73.42f * t) - Mathf.Floor(73.42f * t)) - 1f;
                float sawA = 2f * ((110.0f * t) - Mathf.Floor(110.0f * t)) - 1f;
                float sawEb = 2f * ((155.56f * t) - Mathf.Floor(155.56f * t)) - 1f;
                float brass = (float)System.Math.Tanh((sawD * 0.55f + sawA * 0.42f + sawEb * 0.24f * Mathf.Clamp01(t * 2f)) * 1.85f) * swell * 0.62f;
                float raw = sub + brass;
                lp += lpAlpha * (raw - lp);
                float tail = Mathf.Clamp01((dur - t) / 0.14f);
                data[i] = Mathf.Clamp(lp * 0.72f * tail, -0.78f, 0.78f);
            }
            var clip = AudioClip.Create("LR2_WarBrassSurge", n, 1, sr, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip BuildFirefightDrumrollClip()
        {
            const int sr = 44100;
            const float dur = 0.85f;
            int n = Mathf.RoundToInt(sr * dur);
            float[] data = new float[n];
            var rng = new System.Random(1987);
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float step = Mathf.Lerp(0.068f, 0.042f, t / dur);
                int stroke = Mathf.FloorToInt(t / step);
                float dt = t - stroke * step;
                if (dt > 0.07f) continue;
                float env = Mathf.Pow(Mathf.Clamp01(t / (dur * 0.88f)), 1.25f) * Mathf.Clamp01((dur - t) / 0.08f);
                float freq = (stroke >= 14) ? Mathf.Lerp(135f, 75f, dt * 18f) : Mathf.Lerp(215f, 145f, dt * 24f);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float raw = (Mathf.Sin(2f * Mathf.PI * freq * dt) * 0.62f + noise * Mathf.Exp(-dt * 34f) * 0.38f) * Mathf.Exp(-dt * 28f) * env;
                lp += 0.35f * (raw - lp);
                data[i] = Mathf.Clamp(lp * 0.55f, -0.75f, 0.75f);
            }
            var clip = AudioClip.Create("LR2_FirefightDrumroll", n, 1, sr, false);
            clip.SetData(data, 0);
            return clip;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ConfigureForScene(scene.name);
        }

        private void ConfigureForScene(string sceneName)
        {
            _player = null;
            _intensity = 0.04f;
            _targetIntensity = 0.05f;
            _combatHoldUntil = 0f;
            _proximityFloor = 0f;
            _outcomeTriggered = false;
            MatchProgress = 0f;
            OverdriveWeight = 0f;

            if (_outcomeSrc != null && _outcomeSrc.isPlaying) _outcomeSrc.Stop();

            _isMenuScene = string.IsNullOrEmpty(sceneName) || sceneName.Contains("MainMenu") || (Combatant.FindLocalPlayer() == null && FindAnyObjectByType<MatchManager>() == null);

            if (_isMenuScene)
            {
                ActiveMapTheme = "Menu";
                StopStem(_calmSrc);
                StopStem(_tensionSrc);
                StopStem(_combatSrc);
                StopStem(_overdriveSrc);
                StopStem(_winMomentumSrc);
                StopStem(_loseMomentumSrc);
                if (_menuSrc != null && _menuClip != null && !_menuSrc.isPlaying) _menuSrc.Play();
            }
            else
            {
                StopStem(_menuSrc);
                LoadMapStems(ResolveMapTheme(sceneName));
                StartSynchronizedGameplayStems();
            }
        }

        private void StartSynchronizedGameplayStems()
        {
            if (_calmSrc == null || _tensionSrc == null || _combatSrc == null) return;
            StopStem(_calmSrc);
            StopStem(_tensionSrc);
            StopStem(_combatSrc);
            StopStem(_overdriveSrc);
            StopStem(_winMomentumSrc);
            StopStem(_loseMomentumSrc);

            double dspStart = AudioSettings.dspTime + 0.06;
            if (_calmClip != null) _calmSrc.PlayScheduled(dspStart);
            if (_tensionClip != null) _tensionSrc.PlayScheduled(dspStart);
            if (_combatClip != null) _combatSrc.PlayScheduled(dspStart);
            if (_overdriveSrc != null && _overdriveClip != null) _overdriveSrc.PlayScheduled(dspStart);
            if (_winMomentumSrc != null && _winMomentumClip != null) _winMomentumSrc.PlayScheduled(dspStart);
            if (_loseMomentumSrc != null && _loseMomentumClip != null) _loseMomentumSrc.PlayScheduled(dspStart);
        }

        private static void StopStem(AudioSource s)
        {
            if (s == null) return;
            if (s.isPlaying) s.Stop();
            s.volume = 0f;
            s.pitch = 1f;
        }

        /// <summary>Resets dynamic intensity smoothly back to quiet opening stealth floor.</summary>
        public void ResetToCalm()
        {
            _intensity = 0.04f;
            _targetIntensity = 0.04f;
            _combatHoldUntil = 0f;
            _proximityFloor = 0f;
            OverdriveWeight = 0f;
            WinningMomentumWeight = 0f;
            LosingMomentumWeight = 0f;
            MatchProgress = 0f;
        }

        /// <summary>Allows runtime gameplay or QA tests to nudge combat intensity cleanly.</summary>
        public void NotifyCombatEvent(float impulse, float holdSeconds)
        {
            if (_isMenuScene || _outcomeTriggered) return;
            float prevTarget = _targetIntensity;
            float progScale = Mathf.Lerp(0.65f, 1.15f, MatchProgress);
            _targetIntensity = Mathf.Clamp01(Mathf.Max(_targetIntensity + impulse * 0.65f * progScale, Mathf.Min(1f, _intensity + impulse * progScale)));
            _combatHoldUntil = Mathf.Max(_combatHoldUntil, Time.unscaledTime + Mathf.Clamp(holdSeconds, 1.5f, 4.2f));
            if (_drumrollSrc != null && _drumrollClip != null && Time.unscaledTime >= _nextDrumrollAt &&
                ((_targetIntensity >= 0.48f && prevTarget < 0.48f) || impulse >= 0.30f))
            {
                _nextDrumrollAt = Time.unscaledTime + 4.5f;
                LastDrumrollTime = Time.unscaledTime;
                _drumrollSrc.volume = combatCeiling * LR2Settings.MusicVolume * 0.72f;
                _drumrollSrc.PlayOneShot(_drumrollClip);
            }
            if (_brassSurgeSrc != null && _brassSurgeClip != null && Time.unscaledTime >= _nextBrassSurgeAt &&
                ((_targetIntensity >= 0.72f && prevTarget < 0.65f) || impulse >= 0.48f))
            {
                _nextBrassSurgeAt = Time.unscaledTime + 10.0f;
                LastBrassSurgeTime = Time.unscaledTime;
                _brassSurgeSrc.volume = combatCeiling * LR2Settings.MusicVolume * 0.62f;
                _brassSurgeSrc.PlayOneShot(_brassSurgeClip);
            }
        }

        private void OnShotFired(ShotEvent e)
        {
            if (_isMenuScene || e.shooter == null) return;
            BindPlayer();
            if (_player == null) return;
            if (e.shooter == _player)
            {
                NotifyCombatEvent(0.56f, 3.6f);
                return;
            }
            float d = Vector3.Distance(_player.Position, e.origin);
            if (d < 30f) NotifyCombatEvent(Mathf.Lerp(0.45f, 0.18f, d / 30f), 3.0f);
        }

        private void OnHit(HitEvent e)
        {
            if (_isMenuScene) return;
            BindPlayer();
            if (_player == null) return;
            if (e.victim == _player)
                NotifyCombatEvent(e.damage >= 28f ? 0.58f : 0.40f, 3.8f);
            else if (e.attacker == _player && e.victim != _player)
                NotifyCombatEvent(e.killed ? 0.62f : 0.36f, 3.4f);
        }

        private void OnKilled(KillEvent e)
        {
            if (_isMenuScene) return;
            BindPlayer();
            if (_player == null) return;
            if (e.killer == _player) NotifyCombatEvent(0.50f, 3.2f);
            else if (e.victim == _player) _targetIntensity = Mathf.Min(_targetIntensity, 0.28f);
        }

        private void OnNoise(NoiseEvent e)
        {
            if (_isMenuScene || e.type != NoiseType.Explosion) return;
            BindPlayer();
            if (_player != null && Vector3.Distance(_player.Position, e.position) < 25f)
                NotifyCombatEvent(0.42f, 3.2f);
        }

        private void BindPlayer()
        {
            if (_player == null) _player = Combatant.FindLocalPlayer();
        }

        private float ComputeMatchProgress(MatchManager mm, out bool winning, out bool losing)
        {
            winning = false;
            losing = false;
            if (mm == null || mm.Settings == null) return 0f;

            float timeProg = mm.Settings.timeLimitSeconds > 1f
                ? Mathf.Clamp01(1f - mm.TimeRemaining / mm.Settings.timeLimitSeconds)
                : 0f;

            float scoreProg = 0f;
            if (_player != null)
            {
                int mine = mm.GetDisplayScore(_player);
                int theirs = mm.GetBestOpponentScore(_player);
                int lim = Mathf.Max(1, mm.ScoreLimit);
                scoreProg = Mathf.Clamp01(Mathf.Max(mine, theirs) / (float)lim);
                winning = mine > theirs;
                losing = theirs > mine;
                if (lim - Mathf.Max(mine, theirs) <= 2 && lim > 2)
                    scoreProg = Mathf.Max(scoreProg, 0.85f);
            }

            if (mm.State == MatchState.Playing && mm.TimeRemaining <= 45f && mm.Settings.timeLimitSeconds > 45f)
                timeProg = Mathf.Max(timeProg, Mathf.Lerp(0.75f, 1f, 1f - mm.TimeRemaining / 45f));

            return mm.IsSuddenDeath ? 1f : Mathf.Clamp01(Mathf.Max(timeProg, scoreProg));
        }

        private void TriggerOutcomeStinger(MatchManager mm, float userMusic)
        {
            _outcomeTriggered = true;
            bool won = mm != null && (mm.LocalPlayerWon || mm.IsDraw);
            LastOutcomePlayed = won ? "Win" : "Lose";
            AudioClip clip = won ? _winClip : _loseClip;
            if (_outcomeSrc != null && clip != null)
            {
                _outcomeSrc.Stop();
                _outcomeSrc.clip = clip;
                _outcomeSrc.volume = outcomeCeiling * userMusic;
                _outcomeSrc.Play();
            }
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            float userMusic = LR2Settings.MusicVolume;

            if (_isMenuScene)
            {
                if (_menuSrc != null)
                {
                    float targetMenu = menuCeiling * userMusic;
                    _menuSrc.volume = Mathf.MoveTowards(_menuSrc.volume, targetMenu, dt * 0.25f);
                }
                return;
            }

            BindPlayer();
            if (Time.unscaledTime >= _nextProximityScan)
            {
                _nextProximityScan = Time.unscaledTime + 0.45f;
                UpdateProximityFloor();
            }

            float now = Time.unscaledTime;
            var mm = MatchManager.Instance;
            bool ended = mm != null && mm.State == MatchState.Ended;
            if (mm != null && mm.State != MatchState.Ended && _outcomeTriggered)
            {
                _outcomeTriggered = false;
                if (_outcomeSrc != null && _outcomeSrc.isPlaying) _outcomeSrc.Stop();
            }
            if (ended && !_outcomeTriggered)
                TriggerOutcomeStinger(mm, userMusic);

            MatchProgress = ComputeMatchProgress(mm, out bool winning, out bool losing);
            if (_qaMockProgress >= 0f)
            {
                MatchProgress = _qaMockProgress;
                winning = _qaMockWinning;
                losing = _qaMockLosing;
            }

            // Progressive arc: super mild early (<0.25), smooth mid escalation, relentless endgame overdrive
            float progFloor = Mathf.Pow(MatchProgress, 1.35f) * 0.72f;
            bool playingMatch = (mm == null || mm.State == MatchState.Playing) || _qaMockProgress >= 0f;
            float baseFloor = playingMatch ? Mathf.Max(0.04f, Mathf.Max(_proximityFloor * Mathf.Lerp(0.45f, 1.0f, MatchProgress), progFloor)) : 0.03f;
            if (ended) baseFloor = 0f;

            if (now > _combatHoldUntil)
                _targetIntensity = Mathf.MoveTowards(_targetIntensity, baseFloor, dt * 0.22f);
            else
                _targetIntensity = Mathf.Max(_targetIntensity, baseFloor);

            // Mild cap very early in the match so opening traversal feels calm before battle peaks
            float earlyCap = Mathf.Lerp(0.56f, 1.0f, Mathf.SmoothStep(0.05f, 0.35f, MatchProgress));
            float clampedTarget = Mathf.Min(_targetIntensity, earlyCap);

            float rate = clampedTarget > _intensity ? 2.40f : 0.22f;
            _intensity = Mathf.MoveTowards(_intensity, clampedTarget, dt * rate);

            float targetOverdrive = ended ? 0f : Mathf.SmoothStep(0.60f, 0.96f, Mathf.Max(MatchProgress, _intensity * 0.85f));
            if (mm != null && mm.IsSuddenDeath && !ended) targetOverdrive = Mathf.Max(targetOverdrive, 0.90f);
            OverdriveWeight = Mathf.MoveTowards(OverdriveWeight, targetOverdrive, dt * 0.45f);

            // B141: Mid-to-late-match "road to win" vs "road to defeat" melodic/harmonic cues starting at >= 42% progress.
            float leadStrength = 1f;
            if (mm != null && _player != null && mm.ScoreLimit > 1 && _qaMockProgress < 0f)
            {
                int diff = mm.GetDisplayScore(_player) - mm.GetBestOpponentScore(_player);
                leadStrength = Mathf.Clamp(Mathf.Abs(diff) / Mathf.Max(2f, mm.ScoreLimit * 0.28f), 0.35f, 1.0f);
            }
            float momCurve = Mathf.SmoothStep(0.42f, 0.88f, MatchProgress) * leadStrength;
            float targetWinMom = (!ended && winning) ? momCurve : 0f;
            float targetLoseMom = (!ended && losing && !winning) ? momCurve : 0f;
            WinningMomentumWeight = Mathf.MoveTowards(WinningMomentumWeight, targetWinMom, dt * 0.38f);
            LosingMomentumWeight = Mathf.MoveTowards(LosingMomentumWeight, targetLoseMom, dt * 0.38f);

            float stateAtten = 1f;
            if (AudioListener.pause) stateAtten *= 0.35f;
            if (KillcamDirector.IsPlaying) stateAtten *= 0.42f;
            if (_player != null && !_player.IsAlive && !ended) stateAtten *= 0.48f;
            float master = userMusic * stateAtten;
            float momDuck = 1f - 0.30f * Mathf.Max(WinningMomentumWeight, LosingMomentumWeight);

            float gCalm, gTension, gCombat, gOverdrive, gWinMom, gLoseMom;
            if (ended)
            {
                gCalm = Mathf.MoveTowards(CalmVolume, 0f, dt * 0.5f);
                gTension = Mathf.MoveTowards(TensionVolume, 0f, dt * 0.6f);
                gCombat = Mathf.MoveTowards(CombatVolume, 0f, dt * 0.7f);
                gOverdrive = Mathf.MoveTowards(OverdriveVolume, 0f, dt * 0.8f);
                gWinMom = Mathf.MoveTowards(WinningMomentumVolume, 0f, dt * 0.9f);
                gLoseMom = Mathf.MoveTowards(LosingMomentumVolume, 0f, dt * 0.9f);
                if (_outcomeSrc != null) _outcomeSrc.volume = outcomeCeiling * userMusic * (AudioListener.pause ? 0.5f : 1f);
            }
            else
            {
                gCalm = calmCeiling * Mathf.Lerp(0.95f, 0.55f, MatchProgress) * master;
                gTension = tensionCeiling * Mathf.SmoothStep(0.08f, 0.92f, Mathf.Max(_intensity, MatchProgress * 0.75f)) * master * momDuck;
                gCombat = combatCeiling * Mathf.SmoothStep(0.24f, 0.98f, Mathf.Max(_intensity, MatchProgress * 0.82f)) * master;
                gOverdrive = overdriveCeiling * OverdriveWeight * master * momDuck;
                gWinMom = momentumCeiling * WinningMomentumWeight * master;
                gLoseMom = momentumCeiling * LosingMomentumWeight * master;
            }

            if (_calmSrc != null) _calmSrc.volume = gCalm;
            if (_tensionSrc != null) _tensionSrc.volume = gTension;
            if (_combatSrc != null) _combatSrc.volume = gCombat;
            if (_overdriveSrc != null) _overdriveSrc.volume = gOverdrive;
            if (_winMomentumSrc != null) _winMomentumSrc.volume = gWinMom;
            if (_loseMomentumSrc != null) _loseMomentumSrc.volume = gLoseMom;

            // Subtle progressive energy lift into climax while keeping stems sample-locked
            float pitchLift = 1f + OverdriveWeight * (winning ? 0.030f : losing ? 0.016f : 0.024f);
            if (ended) pitchLift = 1f;
            if (_calmSrc != null) _calmSrc.pitch = pitchLift;
            if (_tensionSrc != null) _tensionSrc.pitch = pitchLift;
            if (_combatSrc != null) _combatSrc.pitch = pitchLift;
            if (_overdriveSrc != null) _overdriveSrc.pitch = pitchLift;
            if (_winMomentumSrc != null) _winMomentumSrc.pitch = pitchLift;
            if (_loseMomentumSrc != null) _loseMomentumSrc.pitch = pitchLift;

            float cutoffCalm = Mathf.Lerp(10000f, 20000f, Mathf.Max(_intensity, MatchProgress));
            float cutoffTension = Mathf.Lerp(12000f, 22000f, Mathf.Max(_intensity, MatchProgress));
            float cutoffCombat = Mathf.Lerp(15000f, 22000f, Mathf.Max(_intensity, MatchProgress));
            if (_calmLpf != null) _calmLpf.cutoffFrequency = cutoffCalm;
            if (_tensionLpf != null) _tensionLpf.cutoffFrequency = cutoffTension;
            if (_combatLpf != null) _combatLpf.cutoffFrequency = cutoffCombat;
            if (_overdriveLpf != null) _overdriveLpf.cutoffFrequency = Mathf.Lerp(14000f, 22000f, OverdriveWeight);
            if (_winMomLpf != null) _winMomLpf.cutoffFrequency = Mathf.Lerp(12000f, 22000f, WinningMomentumWeight);
            if (_loseMomLpf != null) _loseMomLpf.cutoffFrequency = Mathf.Lerp(8000f, 16000f, LosingMomentumWeight);
        }

        private void UpdateProximityFloor()
        {
            if (_player == null || !_player.IsAlive)
            {
                _proximityFloor = 0f;
                return;
            }
            float minDist = 999f;
            var all = Combatant.All;
            Vector3 pPos = _player.Position;
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null || c == _player || !c.IsAlive || !TeamUtil.AreHostile(_player, c)) continue;
                float d = Vector3.Distance(pPos, c.Position);
                if (d < minDist) minDist = d;
            }

            float prox = minDist < 22f ? Mathf.Lerp(0.45f, 0.10f, Mathf.Clamp01((minDist - 5f) / 17f)) : 0f;
            float lowHp = (_player.Health != null && _player.Health.Normalized < 0.4f) ? 0.32f : 0f;
            _proximityFloor = Mathf.Max(prox, lowHp);
        }
    }
}
