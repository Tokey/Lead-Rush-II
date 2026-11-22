using System;
using System.Collections;
using System.Collections.Generic;
using LR2.Audio;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using LR2.FX;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LR2.UI
{
    /// <summary>
    /// Minimalistic, classy obsidian loading curtain mounted before frame 0 so shaders, uniform textures, mipmaps,
    /// weapon prefabs, rigs, and HDRP exposure settle BEFORE anything is revealed on screen.
    /// Zero overlapping text layers: single unified overlay driven smoothly from boot through deployment.
    /// Automatically stays inert during headless / automated screenshot runners so QA captures are unaffected.
    /// </summary>
    [DefaultExecutionOrder(-900)]
    public class LR2LoadingIntro : MonoBehaviour
    {
        public static LR2LoadingIntro Instance { get; private set; }
        public static bool IsCoveringScreen => Instance != null && Instance._covering;
        public static bool HasTitlePromptSupport => true;
        public static bool IsShowingTitleScreen => Instance != null && Instance._titlePromptActive;

        private Canvas _canvas;
        private CanvasGroup _group;
        private Image _bg;
        private Image _barBg;
        private Image _progressFill;
        private TMP_Text _brandText;
        private TMP_Text _eyebrowText;
        private TMP_Text _titleText;
        private TMP_Text _statusText;
        private TMP_Text _tipText;
        private bool _covering;
        private bool _titlePromptActive;
        private bool _initialBoot = true;
        private bool _shadersWarmed;
        private string _pendingEyebrow = "TACTICAL COMBAT";
        private string _pendingTitle = "LEADRUSH II";
        private string _pendingSub = "INITIALIZING SYSTEMS";
        private Coroutine _activeRoutine;

        private static readonly string[] Tips =
        {
            "Suppressive fire and close-quarters engagements dynamically escalate the combat score.",
            "Supersonic crack and whizz cues locate incoming fire before you spot the muzzle.",
            "Aim down sights to tighten grouping and suppress vertical climb at range.",
            "Clean reloads keep mobility uninterrupted — listen for magazine seat and bolt lock.",
        };

        private static bool IsAutomatedRunner()
        {
            if (Application.isBatchMode) return true;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "-lr2QaBot" || a == "-lr2PoseCheck" || a == "-lr2Robot" || a == "-lr2CapturePreviews" || a == "-lr2Verify")
                    return true;
            }
            return false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BootBeforeFirstFrame()
        {
            if (IsAutomatedRunner() || Instance != null) return;
            var go = new GameObject("[LR2 LoadingIntro]");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<LR2LoadingIntro>();
        }

        /// <summary>Called by MainMenuController before activating a target map scene so a single curtain transitions cleanly.</summary>
        public static void BeginSceneTransition(string mapDisplayName)
        {
            if (Instance == null || IsAutomatedRunner()) return;
            Instance._pendingEyebrow = "SECTOR DEPLOYMENT";
            Instance._pendingTitle = CleanDisplayTitle(mapDisplayName, "COMBAT ZONE");
            Instance._pendingSub = "PREPARING ARENA  \u00b7  0%";
            Instance.ShowImmediate(0.05f);
        }

        /// <summary>Updates unified deployment progress on the loading curtain.</summary>
        public static void SetTransitionProgress(float progress01, string mapDisplayName)
        {
            if (Instance == null || IsAutomatedRunner()) return;
            float p = Mathf.Clamp01(progress01);
            if (!Instance._covering) Instance.ShowImmediate(p);
            if (Instance._eyebrowText != null) Instance._eyebrowText.text = "SECTOR DEPLOYMENT";
            if (Instance._titleText != null) Instance._titleText.text = CleanDisplayTitle(mapDisplayName, "COMBAT ZONE");
            if (Instance._statusText != null) Instance._statusText.text = $"PREPARING ARENA  \u00b7  {Mathf.RoundToInt(p * 100f)}%";
            Instance.SetProgress(p);
        }

        /// <summary>B31: Shows or hides the minimalistic title/loading screen on demand for deterministic QA & README capture.</summary>
        public static void SetCapturePreviewTitleScreen(bool visible)
        {
            if (visible)
            {
                if (Instance == null)
                {
                    var go = new GameObject("[LR2 LoadingIntro]");
                    DontDestroyOnLoad(go);
                    Instance = go.AddComponent<LR2LoadingIntro>();
                }
                if (Instance._activeRoutine != null)
                {
                    Instance.StopCoroutine(Instance._activeRoutine);
                    Instance._activeRoutine = null;
                }
                Instance._covering = true;
                Instance._titlePromptActive = true;
                if (Instance._group != null)
                {
                    Instance._group.alpha = 1f;
                    Instance._group.blocksRaycasts = true;
                }
                if (Instance._bg != null) Instance._bg.color = new Color(0.035f, 0.042f, 0.055f, 1.0f);
                if (Instance._brandText != null) Instance._brandText.gameObject.SetActive(true);
                if (Instance._eyebrowText != null)
                {
                    Instance._eyebrowText.text = "SECTOR DEPLOYMENT";
                    Instance._eyebrowText.gameObject.SetActive(true);
                }
                if (Instance._titleText != null) Instance._titleText.text = "SALTWORKS";
                if (Instance._statusText != null) Instance._statusText.text = "DEPLOYMENT READY  \u00b7  100%";
                if (Instance._tipText != null) Instance._tipText.text = Tips[0];
                if (Instance._barBg != null) Instance._barBg.gameObject.SetActive(true);
                if (Instance._progressFill != null) Instance._progressFill.fillAmount = 1f;
            }
            else if (Instance != null)
            {
                if (Instance._activeRoutine != null)
                {
                    Instance.StopCoroutine(Instance._activeRoutine);
                    Instance._activeRoutine = null;
                }
                if (Instance._group != null)
                {
                    Instance._group.alpha = 0f;
                    Instance._group.blocksRaycasts = false;
                }
                Instance._titlePromptActive = false;
                Instance._covering = false;
            }
        }

        /// <summary>Returns world/canvas screen rects of currently visible TMP_Text elements for overlap QA verification.</summary>
        public Rect[] GetVisibleTextScreenRects()
        {
            Canvas.ForceUpdateCanvases();
            var list = new List<Rect>(5);
            void Add(TMP_Text t)
            {
                if (t == null || !t.gameObject.activeInHierarchy || string.IsNullOrEmpty(t.text)) return;
                t.ForceMeshUpdate(true, true);
                var rt = t.rectTransform;
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                float minX = corners[0].x, maxX = corners[2].x;
                float minY = corners[0].y, maxY = corners[2].y;
                list.Add(Rect.MinMaxRect(minX, minY, maxX, maxY));
            }
            Add(_brandText);
            Add(_eyebrowText);
            Add(_titleText);
            Add(_statusText);
            Add(_tipText);
            return list.ToArray();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            BuildOverlay();
            SceneManager.sceneLoaded += OnSceneLoaded;

            string activeName = SceneManager.GetActiveScene().name;
            SetupPromptForScene(activeName);
            ShowImmediate(0f);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (IsAutomatedRunner())
            {
                if (_group != null) _group.alpha = 0f;
                _covering = false;
                return;
            }
            SetupPromptForScene(scene.name);
            if (_activeRoutine != null) StopCoroutine(_activeRoutine);
            _activeRoutine = StartCoroutine(PreloadAndRevealRoutine());
        }

        private void SetupPromptForScene(string sceneName)
        {
            bool menu = string.IsNullOrEmpty(sceneName) || sceneName.Contains("MainMenu");
            if (_initialBoot)
            {
                _pendingEyebrow = "TACTICAL COMBAT";
                _pendingTitle = "LEADRUSH II";
                _pendingSub = "INITIALIZING SYSTEMS";
            }
            else if (menu)
            {
                _pendingEyebrow = "COMMAND LINK";
                _pendingTitle = "MAIN MENU";
                _pendingSub = "PREPARING INTERFACE";
            }
            else
            {
                _pendingEyebrow = "SECTOR DEPLOYMENT";
                _pendingTitle = CleanDisplayTitle(sceneName.Replace("LR2_", ""), "COMBAT ZONE");
                _pendingSub = "SYNCHRONIZING SECTOR ASSETS";
            }
            if (_tipText != null) _tipText.text = Tips[UnityEngine.Random.Range(0, Tips.Length)];
        }

        private static string CleanDisplayTitle(string raw, string fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            string s = raw.Replace('_', ' ').Trim().ToUpperInvariant();
            if (s == "TESTRANGE") s = "TEST RANGE";
            return s;
        }

        private void ShowImmediate(float progress)
        {
            _covering = true;
            if (_group != null)
            {
                _group.alpha = 1f;
                _group.blocksRaycasts = true;
            }
            if (_brandText != null) _brandText.gameObject.SetActive(true);
            if (_eyebrowText != null)
            {
                _eyebrowText.text = _pendingEyebrow;
                _eyebrowText.gameObject.SetActive(true);
            }
            if (_titleText != null) _titleText.text = _pendingTitle;
            if (_statusText != null) _statusText.text = _pendingSub;
            if (_progressFill != null) _progressFill.fillAmount = Mathf.Clamp01(progress);
            if (_barBg != null) _barBg.gameObject.SetActive(true);
            if (_bg != null) _bg.color = new Color(0.035f, 0.042f, 0.055f, 1f);
            _titlePromptActive = false;
        }

        private IEnumerator PreloadAndRevealRoutine()
        {
            ShowImmediate(0.18f);
            float tStart = Time.unscaledTime;
            float minHold = _initialBoot ? 1.15f : 0.65f;
            string sceneName = SceneManager.GetActiveScene().name;
            bool isGameplay = !string.IsNullOrEmpty(sceneName) && !sceneName.Contains("MainMenu");

            if (_statusText != null) _statusText.text = "LOADING AUDIO STEMS";
            _ = LR2CommonSounds.Instance;
            _ = SurfaceLibrary.Instance;
            _ = FoleyLibrary.Instance;
            SetProgress(0.32f);
            yield return null;

            if (!_shadersWarmed)
            {
                if (_statusText != null) _statusText.text = "INITIALIZING PIPELINE";
                _shadersWarmed = true;
            }
            SetProgress(0.52f);
            yield return null;

            if (_statusText != null) _statusText.text = "PREPARING UNITS & GEAR";
            if (isGameplay)
            {
                float waitSpawn = Time.unscaledTime + 3.0f;
                while (Combatant.FindLocalPlayer() == null && Time.unscaledTime < waitSpawn)
                    yield return null;
            }

            SoldierVisuals.ApplyAllNow();
            PreloadActiveCharacterAndWeaponTextures();
            foreach (var proxy in FindObjectsByType<RetargetProxy>())
                if (proxy != null) proxy.SyncPoseNow();

            SetProgress(0.78f);

            if (_statusText != null) _statusText.text = "CALIBRATING OPTICS";
            for (int f = 0; f < 10; f++)
            {
                SoldierVisuals.ApplyAllNow();
                SetProgress(Mathf.Lerp(0.78f, 0.96f, f / 9f));
                yield return null;
            }

            while (Time.unscaledTime - tStart < minHold)
            {
                float p = Mathf.Clamp01(0.78f + 0.22f * ((Time.unscaledTime - tStart) / minHold));
                SetProgress(p);
                yield return null;
            }
            SetProgress(1f);
            if (_statusText != null) _statusText.text = "DEPLOYMENT READY";
            yield return new WaitForSecondsRealtime(0.12f);

            _initialBoot = false;

            float fade = 0f;
            while (fade < 0.34f)
            {
                fade += Time.unscaledDeltaTime;
                if (_group != null) _group.alpha = 1f - Mathf.Clamp01(fade / 0.34f);
                yield return null;
            }

            if (_group != null)
            {
                _group.alpha = 0f;
                _group.blocksRaycasts = false;
            }
            _titlePromptActive = false;
            _covering = false;
            _activeRoutine = null;
        }

        private void SetProgress(float v)
        {
            if (_progressFill != null) _progressFill.fillAmount = Mathf.Clamp01(v);
        }

        private static void PreloadActiveCharacterAndWeaponTextures()
        {
            foreach (var visuals in FindObjectsByType<SoldierVisuals>())
            {
                if (visuals == null) continue;
                foreach (var rend in visuals.GetComponentsInChildren<Renderer>(true))
                    PromoteRendererTexturesMip0(rend);
            }
            foreach (var firearm in FindObjectsByType<LR2.Weapons.LR2Firearm>())
            {
                if (firearm == null) continue;
                foreach (var rend in firearm.GetComponentsInChildren<Renderer>(true))
                    PromoteRendererTexturesMip0(rend);
            }
        }

        private static void PromoteRendererTexturesMip0(Renderer rend)
        {
            if (rend == null) return;
            var mats = rend.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var mat = mats[i];
                if (mat == null) continue;
                foreach (int propId in mat.GetTexturePropertyNameIDs())
                {
                    if (mat.GetTexture(propId) is Texture2D t2d)
                        t2d.requestedMipmapLevel = 0;
                }
            }
        }

        private void BuildOverlay()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 9999;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            gameObject.AddComponent<GraphicRaycaster>();
            _group = gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 1f;
            _group.blocksRaycasts = true;
            _group.interactable = false;

            // Matte obsidian backdrop
            var bg = new GameObject("Background").AddComponent<Image>();
            bg.transform.SetParent(transform, false);
            bg.color = new Color(0.035f, 0.042f, 0.055f, 1f);
            Stretch(bg.rectTransform);
            _bg = bg;

            var theme = Resources.Load<UITheme>("LR2/UITheme");
            TMP_FontAsset headingFont = theme != null ? (theme.headingFont ?? theme.bodyFont) : null;
            TMP_FontAsset bodyFont = theme != null ? (theme.bodyFont ?? theme.headingFont) : null;

            // Quiet minimal brand mark top-left (strictly outside centered column)
            _brandText = MakeText("BrandWordmark", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(48f, -42f),
                new Vector2(320f, 26f), 13f, FontStyles.Normal, new Color(1f, 1f, 1f, 0.34f), TextAlignmentOptions.Left, 6f, false, 0f, bodyFont);
            _brandText.text = "LEADRUSH II";

            // Center non-overlapping editorial stack (+84 / +30 / -24 bar / -56)
            Vector2 mid = new Vector2(0.5f, 0.5f);
            _eyebrowText = MakeText("Eyebrow", mid, new Vector2(0.5f, 0.5f), new Vector2(0f, 84f),
                new Vector2(760f, 26f), 12f, FontStyles.Normal, new Color(0.79f, 0.60f, 0.31f, 0.92f), TextAlignmentOptions.Center, 8f, true, 9f, bodyFont);

            _titleText = MakeText("Title", mid, new Vector2(0.5f, 0.5f), new Vector2(0f, 30f),
                new Vector2(860f, 66f), 44f, FontStyles.Normal, new Color(0.95f, 0.96f, 0.97f, 1f), TextAlignmentOptions.Center, 5f, true, 26f, headingFont);

            var barBg = new GameObject("BarBg").AddComponent<Image>();
            barBg.transform.SetParent(transform, false);
            barBg.color = new Color(1f, 1f, 1f, 0.11f);
            var bgRt = barBg.rectTransform;
            bgRt.anchorMin = bgRt.anchorMax = mid;
            bgRt.pivot = new Vector2(0.5f, 0.5f);
            bgRt.anchoredPosition = new Vector2(0f, -24f);
            bgRt.sizeDelta = new Vector2(420f, 2f);
            _barBg = barBg;

            var barFill = new GameObject("BarFill").AddComponent<Image>();
            barFill.transform.SetParent(barBg.transform, false);
            barFill.color = new Color(0.89f, 0.66f, 0.29f, 1f);
            barFill.type = Image.Type.Filled;
            barFill.fillMethod = Image.FillMethod.Horizontal;
            barFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            barFill.fillAmount = 0f;
            Stretch(barFill.rectTransform);
            _progressFill = barFill;

            _statusText = MakeText("Status", mid, new Vector2(0.5f, 0.5f), new Vector2(0f, -56f),
                new Vector2(820f, 26f), 13f, FontStyles.Normal, new Color(0.58f, 0.62f, 0.66f, 0.95f), TextAlignmentOptions.Center, 3f, true, 10f, bodyFont);

            _tipText = MakeText("Tip", new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, 58f),
                new Vector2(860f, 48f), 13.5f, FontStyles.Normal, new Color(0.43f, 0.47f, 0.52f, 0.88f), TextAlignmentOptions.Center, 0.8f, false, 0f, bodyFont);
            _tipText.textWrappingMode = TextWrappingModes.Normal;
        }

        private TMP_Text MakeText(string name, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size,
            float fontSize, FontStyles style, Color color, TextAlignmentOptions align, float spacing, bool noWrap, float autoSizeMin = 0f, TMP_FontAsset font = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var txt = go.AddComponent<TextMeshProUGUI>();
            if (font != null) txt.font = font;
            txt.alignment = align;
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.color = color;
            txt.characterSpacing = spacing;
            txt.raycastTarget = false;
            txt.textWrappingMode = noWrap ? TextWrappingModes.NoWrap : TextWrappingModes.Normal;
            if (autoSizeMin > 0f)
            {
                txt.enableAutoSizing = true;
                txt.fontSizeMin = autoSizeMin;
                txt.fontSizeMax = fontSize;
            }
            var rt = txt.rectTransform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return txt;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
