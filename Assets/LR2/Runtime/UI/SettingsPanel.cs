using System.Collections.Generic;
using LR2.Core;
using LR2.Game;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LR2.UI
{
    /// <summary>Settings screen bound to LR2Settings (used in the main menu and the pause menu).</summary>
    public class SettingsPanel : MonoBehaviour
    {
        [Header("Controls")]
        public Slider sensitivity;
        public Slider adsSensitivity;
        public Toggle invertY;
        public Toggle toggleAim;
        public Toggle toggleSprint;
        public Toggle autoSprint;

        [Header("View")]
        public Slider fieldOfView;
        public Slider cameraShake;
        public Toggle bodycamLook;
        public Slider bodycamMotion;
        public Toggle motionBlur;
        public Toggle filmGrain;
        public Toggle minimapBottom;   // B96

        [Header("Audio")]
        public Slider masterVolume;
        public Slider sfxVolume;
        public Slider musicVolume;
        public Slider uiVolume;
        public Toggle hitmarkerSound;

        [Header("Graphics")]
        public Toggle vsync;
        public Toggle showFps;
        public Toggle rayTracing;
        public Button qualityPrev, qualityNext;
        public TMP_Text qualityValue;
        public Button antiAliasingPrev, antiAliasingNext;
        public TMP_Text antiAliasingValue;
        public Button upscalerPrev, upscalerNext;
        public TMP_Text upscalerValue;
        public TMP_Text upscalerStatusNote;
        public Button frameLimitPrev, frameLimitNext;
        public TMP_Text frameLimitValue;

        [Header("Gameplay & Accessibility")]
        public Button botDifficultyPrev, botDifficultyNext;
        public TMP_Text botDifficultyValue;
        public Button botTeamModePrev, botTeamModeNext;
        public TMP_Text botTeamModeValue;
        public Button colorBlindPrev, colorBlindNext;
        public TMP_Text colorBlindValue;
        public Toggle laserSight;

        [Header("Tabs & Keybinds (B44)")]
        public GameObject[] tabPanels;
        public Button[] tabButtons;
        public Button resetBindingsButton;
        public TMP_Text rebindStatusText;
        public List<KeybindSlot> keybindSlots = new List<KeybindSlot>();

        [System.Serializable]
        public struct KeybindSlot
        {
            public string actionName;
            public Button button;
            public TMP_Text label;
        }

        [Header("Actions (optional)")]
        public Button resetDefaultsButton;

        [Header("Value labels (optional)")]
        public TMP_Text sensitivityValue;
        public TMP_Text adsSensitivityValue;
        public TMP_Text fovValue;
        public TMP_Text cameraShakeValue;
        public TMP_Text bodycamMotionValue;
        public TMP_Text masterVolumeValue;
        public TMP_Text sfxVolumeValue;
        public TMP_Text musicVolumeValue;
        public TMP_Text uiVolumeValue;

        private static readonly int[] FrameLimits = { 0, 60, 120, 144, 165, 240 };
        private static readonly string[] AaLabels = { "OFF", "FXAA", "SMAA", "TAA" };
        private static readonly string[] UpscalerLabels = { "NATIVE", "STP", "TAAU", "FSR 1.0", "DLSS", "FSR 2" };
        private static readonly string[] ColorBlindLabels = { "STANDARD", "COLOR-BLIND SAFE" };
        private static readonly string[] DifficultyLabels = { "RECRUIT", "REGULAR", "HARDENED", "VETERAN", "ADAPTIVE" };
        private bool _loading;
        private int _activeTab;
        private UnityEngine.InputSystem.InputActionRebindingExtensions.RebindingOperation _activeRebind;

        private static int WrapStep(int cur, int count, int delta) => count > 0 ? (cur + delta % count + count) % count : 0;

        private void OnEnable()
        {
            LR2Settings.Changed -= OnSettingsChanged;
            LR2Settings.Changed += OnSettingsChanged;
            Load();
            SelectTab(0);
        }

        private void OnSettingsChanged()
        {
            if (!_loading) UpdateUpscalerNote();
        }

        public void Load()
        {
            _loading = true;
            Set(sensitivity, 0.1f, 5f, LR2Settings.MouseSensitivity);
            Set(adsSensitivity, 0.3f, 1.5f, LR2Settings.AdsSensitivityMultiplier);
            Set(fieldOfView, 65f, 120f, LR2Settings.FieldOfView, true);
            Set(cameraShake, 0f, 1f, LR2Settings.CameraShake);
            Set(bodycamMotion, 0f, 1.5f, LR2Settings.BodycamMotion);
            Set(masterVolume, 0f, 1f, LR2Settings.MasterVolume);
            Set(sfxVolume, 0f, 1f, LR2Settings.SfxVolume);
            Set(musicVolume, 0f, 1f, LR2Settings.MusicVolume);
            Set(uiVolume, 0f, 1f, LR2Settings.UiVolume);
            if (invertY != null) invertY.isOn = LR2Settings.InvertY;
            if (toggleAim != null) toggleAim.isOn = LR2Settings.ToggleAim;
            if (toggleSprint != null) toggleSprint.isOn = LR2Settings.ToggleSprint;
            if (autoSprint != null) autoSprint.isOn = LR2Settings.AutoSprint;
            if (bodycamLook != null) bodycamLook.isOn = LR2Settings.BodycamLook;
            if (motionBlur != null) motionBlur.isOn = LR2Settings.MotionBlur;
            if (filmGrain != null) filmGrain.isOn = LR2Settings.FilmGrain;
            if (minimapBottom != null) minimapBottom.isOn = LR2Settings.MinimapBottom;
            if (hitmarkerSound != null) hitmarkerSound.isOn = LR2Settings.HitmarkerSound;
            if (vsync != null) vsync.isOn = LR2Settings.VSync;
            if (showFps != null) showFps.isOn = LR2Settings.ShowFps;
            if (laserSight != null) laserSight.isOn = LR2Settings.LaserEnabled;
            if (rayTracing != null)
            {
                bool supported = GraphicsPresetContract.SupportsRayTracing;
                rayTracing.interactable = supported;
                rayTracing.isOn = supported && LR2Settings.RayTracing;
            }
            UpdateUpscalerNote();
            UpdateLabels();
            RefreshKeybindLabels();
            _loading = false;
        }

        private void UpdateUpscalerNote()
        {
            if (upscalerStatusNote != null)
                upscalerStatusNote.text = UpscalerBackend.GetCapabilitiesNote();
        }

        private void Awake()
        {
            Hook(sensitivity, v => LR2Settings.MouseSensitivity = v);
            Hook(adsSensitivity, v => LR2Settings.AdsSensitivityMultiplier = v);
            Hook(fieldOfView, v => LR2Settings.FieldOfView = v);
            Hook(cameraShake, v => LR2Settings.CameraShake = v);
            Hook(bodycamMotion, v => LR2Settings.BodycamMotion = v);
            Hook(masterVolume, v => { LR2Settings.MasterVolume = v; AudioListener.volume = v; });
            Hook(sfxVolume, v => LR2Settings.SfxVolume = v);
            Hook(musicVolume, v => LR2Settings.MusicVolume = v);
            Hook(uiVolume, v => LR2Settings.UiVolume = v);
            Hook(invertY, v => LR2Settings.InvertY = v);
            Hook(toggleAim, v => LR2Settings.ToggleAim = v);
            Hook(toggleSprint, v => LR2Settings.ToggleSprint = v);
            Hook(autoSprint, v => LR2Settings.AutoSprint = v);
            Hook(bodycamLook, v => LR2Settings.BodycamLook = v);
            Hook(motionBlur, v => LR2Settings.MotionBlur = v);
            Hook(filmGrain, v => LR2Settings.FilmGrain = v);
            Hook(minimapBottom, v => LR2Settings.MinimapBottom = v);
            Hook(hitmarkerSound, v => LR2Settings.HitmarkerSound = v);
            Hook(vsync, v => { LR2Settings.VSync = v; LR2Settings.Apply(); });
            Hook(showFps, v => LR2Settings.ShowFps = v);
            Hook(laserSight, v => LR2Settings.LaserEnabled = v);
            Hook(rayTracing, v => { LR2Settings.RayTracing = v && GraphicsPresetContract.SupportsRayTracing; LR2Settings.Apply(); });
            HookStep(qualityPrev, qualityNext, () => Mathf.Clamp(LR2Settings.QualityLevel >= 0 ? LR2Settings.QualityLevel : 2, 0, 3),
                () => 4, i =>
                {
                    int qIdx = GraphicsPresetContract.ResolveQualityIndex((LR2GraphicsPreset)i);
                    LR2Settings.QualityLevel = qIdx >= 0 ? qIdx : i;
                    LR2Settings.Apply();
                });
            HookStep(upscalerPrev, upscalerNext, () => Mathf.Clamp(LR2Settings.UpscalerMode, 0, UpscalerLabels.Length - 1),
                () => UpscalerLabels.Length, i => { LR2Settings.UpscalerMode = i; LR2Settings.Apply(); });
            HookStep(antiAliasingPrev, antiAliasingNext, () => Mathf.Clamp(LR2Settings.AntiAliasing, 0, AaLabels.Length - 1),
                () => AaLabels.Length, i => { LR2Settings.AntiAliasing = i; LR2Settings.Apply(); });
            HookStep(frameLimitPrev, frameLimitNext, () => Mathf.Max(0, System.Array.IndexOf(FrameLimits, LR2Settings.FrameRateLimit)),
                () => FrameLimits.Length, i => { LR2Settings.FrameRateLimit = FrameLimits[i]; LR2Settings.Apply(); });
            HookStep(colorBlindPrev, colorBlindNext, () => Mathf.Clamp(LR2Settings.ColorBlindMode, 0, ColorBlindLabels.Length - 1),
                () => ColorBlindLabels.Length, i => LR2Settings.ColorBlindMode = i);
            HookStep(botDifficultyPrev, botDifficultyNext, () => Mathf.Clamp(LR2Settings.BotDifficulty, 0, DifficultyLabels.Length - 1),
                () => DifficultyLabels.Length, i => LR2Settings.BotDifficulty = i);
            HookStep(botTeamModePrev, botTeamModeNext, () => Mathf.Clamp(LR2Settings.BotTeamMode, 0, LR2Session.BotTeamModeLabels.Length - 1),
                () => LR2Session.BotTeamModeLabels.Length, i => LR2Settings.BotTeamMode = i);
            if (resetDefaultsButton != null) resetDefaultsButton.onClick.AddListener(ResetDefaults);
            if (resetBindingsButton != null) resetBindingsButton.onClick.AddListener(ResetKeybinds);
            if (tabButtons != null)
            {
                for (int t = 0; t < tabButtons.Length; t++)
                {
                    int tabIndex = t;
                    if (tabButtons[t] != null) tabButtons[t].onClick.AddListener(() => SelectTab(tabIndex));
                }
            }
            for (int k = 0; k < keybindSlots.Count; k++)
            {
                var slot = keybindSlots[k];
                if (slot.button != null && !string.IsNullOrEmpty(slot.actionName))
                    slot.button.onClick.AddListener(() => StartRebind(slot));
            }
        }

        public void SelectTab(int index)
        {
            if (tabPanels == null || tabPanels.Length == 0) return;
            _activeTab = Mathf.Clamp(index, 0, tabPanels.Length - 1);
            for (int i = 0; i < tabPanels.Length; i++)
            {
                if (tabPanels[i] != null) tabPanels[i].SetActive(i == _activeTab);
                if (tabButtons != null && i < tabButtons.Length && tabButtons[i] != null)
                {
                    var img = tabButtons[i].targetGraphic as Graphic;
                    if (img != null) img.color = (i == _activeTab) ? new Color(1f, 0.71f, 0.28f, 0.92f) : new Color(0.11f, 0.13f, 0.16f, 0.88f);
                }
            }
        }

        private void StartRebind(KeybindSlot slot)
        {
            _activeRebind?.Dispose();
            _activeRebind = null;
            if (slot.label != null) slot.label.text = "...";
            if (rebindStatusText != null) rebindStatusText.text = $"PRESS ANY KEY FOR {slot.actionName.ToUpperInvariant()} (ESC CANCELS)";
            _activeRebind = LR2Bindings.BeginRebind(slot.actionName, 0,
                display =>
                {
                    _activeRebind = null;
                    if (slot.label != null) slot.label.text = display.ToUpperInvariant();
                    if (rebindStatusText != null) rebindStatusText.text = $"BOUND {slot.actionName.ToUpperInvariant()} TO [{display.ToUpperInvariant()}]";
                },
                () =>
                {
                    _activeRebind = null;
                    RefreshKeybindLabels();
                    if (rebindStatusText != null) rebindStatusText.text = "REBIND CANCELLED · CLICK ANY ACTION TO REBIND";
                });
        }

        public void ResetKeybinds()
        {
            _activeRebind?.Dispose();
            _activeRebind = null;
            LR2Bindings.ResetToDefaults();
            RefreshKeybindLabels();
            if (rebindStatusText != null) rebindStatusText.text = "KEYBINDS RESET TO DEFAULTS";
        }

        private void RefreshKeybindLabels()
        {
            for (int i = 0; i < keybindSlots.Count; i++)
            {
                var slot = keybindSlots[i];
                if (slot.label != null && !string.IsNullOrEmpty(slot.actionName))
                {
                    string key = LR2Bindings.GetDisplayString(slot.actionName, 0);
                    slot.label.text = string.IsNullOrEmpty(key) ? "-" : key.ToUpperInvariant();
                }
            }
        }

        private void HookStep(Button prev, Button next, System.Func<int> getIndex, System.Func<int> getCount, System.Action<int> applyIndex)
        {
            void Step(int delta)
            {
                if (_loading) return;
                int count = getCount();
                if (count <= 0) return;
                applyIndex(WrapStep(getIndex(), count, delta));
                UpdateLabels();
            }
            if (prev != null) prev.onClick.AddListener(() => Step(-1));
            if (next != null) next.onClick.AddListener(() => Step(1));
        }

        private void OnDisable()
        {
            LR2Settings.Changed -= OnSettingsChanged;
            _activeRebind?.Dispose();
            _activeRebind = null;
            LR2Settings.Save();
        }

        private static void Set(Slider s, float min, float max, float value, bool whole = false)
        {
            if (s == null) return;
            s.minValue = min;
            s.maxValue = max;
            s.wholeNumbers = whole;
            s.value = value;
        }

        private void Hook(Slider s, System.Action<float> apply)
        {
            if (s == null) return;
            s.onValueChanged.AddListener(v =>
            {
                if (_loading) return;
                apply(v);
                UpdateLabels();
            });
        }

        private void Hook(Toggle t, System.Action<bool> apply)
        {
            if (t == null) return;
            t.onValueChanged.AddListener(v => { if (!_loading) apply(v); });
        }

        private void UpdateLabels()
        {
            if (sensitivityValue != null) sensitivityValue.text = LR2Settings.MouseSensitivity.ToString("0.00");
            if (adsSensitivityValue != null) adsSensitivityValue.text = LR2Settings.AdsSensitivityMultiplier.ToString("0.00x");
            if (fovValue != null) fovValue.text = Mathf.RoundToInt(LR2Settings.FieldOfView).ToString();
            if (cameraShakeValue != null) cameraShakeValue.text = Mathf.RoundToInt(LR2Settings.CameraShake * 100f) + "%";
            if (bodycamMotionValue != null) bodycamMotionValue.text = Mathf.RoundToInt(LR2Settings.BodycamMotion * 100f) + "%";
            if (masterVolumeValue != null) masterVolumeValue.text = Mathf.RoundToInt(LR2Settings.MasterVolume * 100f) + "%";
            if (sfxVolumeValue != null) sfxVolumeValue.text = Mathf.RoundToInt(LR2Settings.SfxVolume * 100f) + "%";
            if (musicVolumeValue != null) musicVolumeValue.text = Mathf.RoundToInt(LR2Settings.MusicVolume * 100f) + "%";
            if (uiVolumeValue != null) uiVolumeValue.text = Mathf.RoundToInt(LR2Settings.UiVolume * 100f) + "%";
            if (qualityValue != null)
            {
                int q = LR2Settings.QualityLevel >= 0 ? LR2Settings.QualityLevel : QualitySettings.GetQualityLevel();
                var preset = (LR2GraphicsPreset)Mathf.Clamp(q, 0, 3);
                qualityValue.text = GraphicsPresetContract.Label(preset);
            }
            if (upscalerValue != null)
                upscalerValue.text = UpscalerLabels[Mathf.Clamp(LR2Settings.UpscalerMode, 0, UpscalerLabels.Length - 1)];
            if (antiAliasingValue != null)
                antiAliasingValue.text = AaLabels[Mathf.Clamp(LR2Settings.AntiAliasing, 0, AaLabels.Length - 1)];
            if (frameLimitValue != null)
            {
                int f = LR2Settings.FrameRateLimit;
                frameLimitValue.text = f <= 0 ? "UNLIMITED" : f + " FPS";
            }
            if (colorBlindValue != null)
                colorBlindValue.text = ColorBlindLabels[Mathf.Clamp(LR2Settings.ColorBlindMode, 0, ColorBlindLabels.Length - 1)];
            if (botDifficultyValue != null)
                botDifficultyValue.text = DifficultyLabels[Mathf.Clamp(LR2Settings.BotDifficulty, 0, DifficultyLabels.Length - 1)];
            if (botTeamModeValue != null)
                botTeamModeValue.text = LR2Session.BotTeamModeLabels[Mathf.Clamp(LR2Settings.BotTeamMode, 0, LR2Session.BotTeamModeLabels.Length - 1)];
        }

        public void ResetDefaults()
        {
            LR2Settings.ResetToDefaults();
            LR2Settings.Save();
            LR2Settings.Apply();
            Load();
        }
    }
}
