using System;
using UnityEngine;

namespace LR2.Core
{
    /// <summary>
    /// Player preferences (persisted in PlayerPrefs). UI menus write here; gameplay systems read and listen to
    /// <see cref="Changed"/>. Call <see cref="Apply"/> after editing to push engine-level settings.
    /// </summary>
    public static class LR2Settings
    {
        public static event Action Changed;

        private const string Prefix = "LR2.";

        public static float MouseSensitivity { get => GetF("v2.MouseSensitivity", 1.0f); set => SetF("v2.MouseSensitivity", Mathf.Clamp(value, 0.05f, 10f)); }
        public static float AdsSensitivityMultiplier { get => GetF("v2.AdsSens", 0.75f); set => SetF("v2.AdsSens", Mathf.Clamp(value, 0.1f, 2f)); }
        /// <summary>HORIZONTAL field of view in degrees (CoD/BF convention). Cameras need the vertical angle: use <see cref="VerticalFov"/>.</summary>
        public static float FieldOfView { get => GetF("FOV", 90f); set => SetF("FOV", Mathf.Clamp(value, 65f, 120f)); }

        /// <summary>Aspect ratio the horizontal FOV setting is measured on (16:9, like CoD and Battlefield).</summary>
        public const float FovReferenceAspect = 16f / 9f;

        /// <summary>
        /// Vertical camera angle for a HORIZONTAL FOV setting, using Hor+ scaling: the setting is measured on a 16:9 screen and the
        /// vertical angle is kept for every aspect ratio. Ultrawide screens therefore see more to the sides instead of getting a
        /// zoomed-in picture, and 16:9 screens are unchanged. <paramref name="aspect"/> is ignored on purpose (kept for callers).
        /// </summary>
        public static float VerticalFov(float horizontalDegrees, float aspect) =>
            Camera.HorizontalToVerticalFieldOfView(Mathf.Clamp(horizontalDegrees, 1f, 179f), FovReferenceAspect);
        public static bool InvertY { get => GetB("InvertY", false); set => SetB("InvertY", value); }
        /// <summary>Camera shake intensity 0..1 (shots, hits taken, near misses, reload jolts, landings). Default 1.</summary>
        public static float CameraShake { get => GetF("v3.CameraShake", 1f); set => SetF("v3.CameraShake", Mathf.Clamp01(value)); }
        /// <summary>View-camera anti-aliasing: 0 off, 1 FXAA, 2 SMAA, 3 TAA (default). Applied by ViewCameraQuality.</summary>
        public static int AntiAliasing { get => Mathf.Clamp(Mathf.RoundToInt(GetF("v3.AntiAliasing", 3f)), 0, 3); set => SetF("v3.AntiAliasing", Mathf.Clamp(value, 0, 3)); }
        /// <summary>HDRP upscaler mode: 0 Native (100%), 1 STP, 2 TAAU, 3 FSR 1.0, 4 DLSS (when plugin+RTX available), 5 FSR 2 (when plugin available). Handled by UpscalerBackend.</summary>
        public static int UpscalerMode { get => Mathf.Clamp(GetI("v3.Upscaler", 0), 0, 5); set => SetI("v3.Upscaler", Mathf.Clamp(value, 0, 5)); }
        /// <summary>Color-blind-safe hitmarker palette: 0 Standard, 1 Color-Blind Safe (HitmarkerPalette).</summary>
        public static int ColorBlindMode { get => Mathf.Clamp(GetI("v3.ColorBlind", 0), 0, 1); set => SetI("v3.ColorBlind", Mathf.Clamp(value, 0, 1)); }
        public static float MasterVolume { get => GetF("MasterVol", 0.9f); set => SetF("MasterVol", Mathf.Clamp01(value)); }
        public static float SfxVolume { get => GetF("SfxVol", 1f); set => SetF("SfxVol", Mathf.Clamp01(value)); }
        public static float MusicVolume { get => GetF("MusicVol", 0.55f); set => SetF("MusicVol", Mathf.Clamp01(value)); }
        public static float UiVolume { get => GetF("UiVol", 0.8f); set => SetF("UiVol", Mathf.Clamp01(value)); }
        public static int QualityLevel { get => GetI("Quality", -1); set => SetI("Quality", value); }
        public static bool VSync { get => GetB("VSync", false); set => SetB("VSync", value); }
        public static int FrameRateLimit { get => GetI("FpsLimit", 0); set => SetI("FpsLimit", Mathf.Max(0, value)); }
        public static bool ShowFps { get => GetB("ShowFps", false); set => SetB("ShowFps", value); }
        public static bool MotionBlur { get => GetB("v2.MotionBlur", false); set => SetB("v2.MotionBlur", value); }
        public static bool FilmGrain { get => GetB("FilmGrain", true); set => SetB("FilmGrain", value); }
        /// <summary>B96 (user 11-19 00:13): BF6-style minimap at the bottom-left above the health bar (default); off = top-left.</summary>
        public static bool MinimapBottom { get => GetB("v5.MinimapBottom", true); set => SetB("v5.MinimapBottom", value); }
        public static bool RayTracing { get => GetB("RayTracing", false); set => SetB("RayTracing", value); }
        public static bool HitmarkerSound { get => GetB("HitmarkerSound", true); set => SetB("HitmarkerSound", value); }
        /// <summary>Weapon laser sight. Lasers are enabled for every firearm by default; L or the settings toggle can disable them.</summary>
        public static bool LaserEnabled { get => GetB("v4.LaserEnabled", true); set => SetB("v4.LaserEnabled", value); }
        public static int BotDifficulty { get => GetI("BotDifficulty", 1); set => SetI("BotDifficulty", Mathf.Clamp(value, 0, 4)); }
        /// <summary>Bot team alignment in CTF/multi-bot modes: 0 Squads (Allies vs Enemies), 1 Free-For-All, 2 All Bots Same Team, 3 All Bots On Player Team, 4 Half & Half (50/50 Split).</summary>
        public static int BotTeamMode { get => Mathf.Clamp(GetI("v3.BotTeamMode", 0), 0, 4); set => SetI("v3.BotTeamMode", Mathf.Clamp(value, 0, 4)); }
        /// <summary>Bodycam lens look (distortion, grain, aberration, grade).</summary>
        public static bool BodycamLook { get => GetB("v2.BodycamLook", false); set => SetB("v2.BodycamLook", value); }
        /// <summary>B136q: Controls menu input behavior toggles.</summary>
        public static bool ToggleAim { get => GetB("v4.ToggleAim", false); set => SetB("v4.ToggleAim", value); }
        public static bool ToggleSprint { get => GetB("v4.ToggleSprint", false); set => SetB("v4.ToggleSprint", value); }
        public static bool AutoSprint { get => GetB("v4.AutoSprint", false); set => SetB("v4.AutoSprint", value); }
        /// <summary>B151: Groq LLM integration depth: 0 Off (Rules only), 1 Commander (Macro), 2 Tactical Micro, 3 Avatar Pilot (Action Chunking).</summary>
        public static int LlmMode { get => Mathf.Clamp(GetI("v6.LlmMode", 0), 0, 3); set => SetI("v6.LlmMode", Mathf.Clamp(value, 0, 3)); }
        /// <summary>B151: Teams commanded by LLM when LlmMode > 0: 0 None, 1 Alpha Team, 2 Bravo Team, 3 Both Teams.</summary>
        public static int LlmTeams { get => Mathf.Clamp(GetI("v6.LlmTeams", 1), 0, 3); set => SetI("v6.LlmTeams", Mathf.Clamp(value, 0, 3)); }
        /// <summary>B151: Groq decision cadence index: 0=5s, 1=8s, 2=12s, 3=18s.</summary>
        public static int LlmIntervalIndex { get => Mathf.Clamp(GetI("v6.LlmInterval", 1), 0, 3); set => SetI("v6.LlmInterval", Mathf.Clamp(value, 0, 3)); }
        public static float LlmIntervalSeconds => LlmIntervalIndex == 0 ? 5f : LlmIntervalIndex == 1 ? 8f : LlmIntervalIndex == 2 ? 12f : 18f;
        /// <summary>B155: Zero-stutter in-RAM telemetry CSV recording for human &amp; bots.</summary>
        public static bool RecordTelemetry { get => GetB("v7.RecordTelemetry", true); set => SetB("v7.RecordTelemetry", value); }
        /// <summary>Bodycam camera motion (head bob, inertia sway, handheld shake). On by default (user asked for visible bodycam motion);
        /// new v3 key so an old saved 0 from the v2 default doesn't keep it off.</summary>
        public static float BodycamMotion { get => GetF("v3.BodycamMotion", 1f); set => SetF("v3.BodycamMotion", Mathf.Clamp(value, 0f, 1.5f)); }
        public static string PlayerName { get => PlayerPrefs.GetString(Prefix + "PlayerName", "Operator"); set { PlayerPrefs.SetString(Prefix + "PlayerName", value); Notify(); } }

        /// <summary>Pushes engine-level settings (quality, vsync, frame cap, master volume).</summary>
        public static void Apply()
        {
            int q = QualityLevel;
            if (q >= 0 && q < QualitySettings.names.Length && QualitySettings.GetQualityLevel() != q)
                QualitySettings.SetQualityLevel(q, true);
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            Application.targetFrameRate = FrameRateLimit > 0 ? FrameRateLimit : -1;
            AudioListener.volume = MasterVolume;
        }

        public static void ResetToDefaults()
        {
            foreach (var key in new[] { "MouseSensitivity", "v2.MouseSensitivity", "AdsSens", "v2.AdsSens", "FOV", "InvertY",
                         "v3.CameraShake", "v3.AntiAliasing", "v3.Upscaler", "v3.ColorBlind",
                         "MasterVol", "SfxVol", "MusicVol", "UiVol", "Quality", "VSync", "FpsLimit", "ShowFps",
                         "MotionBlur", "v2.MotionBlur", "FilmGrain", "RayTracing", "HitmarkerSound", "v3.LaserEnabled", "v4.LaserEnabled", "BotDifficulty", "v3.BotTeamMode",
                         "v4.ToggleAim", "v4.ToggleSprint", "v4.AutoSprint",
                         "v6.LlmMode", "v6.LlmTeams", "v6.LlmInterval", "v7.RecordTelemetry",
                         "BodycamLook", "v2.BodycamLook", "BodycamMotion", "v2.BodycamMotion", "v3.BodycamMotion" })
                PlayerPrefs.DeleteKey(Prefix + key);
            Notify();
        }

        public static void Save() => PlayerPrefs.Save();

        private static float GetF(string k, float d) => PlayerPrefs.GetFloat(Prefix + k, d);
        private static int GetI(string k, int d) => PlayerPrefs.GetInt(Prefix + k, d);
        private static bool GetB(string k, bool d) => PlayerPrefs.GetInt(Prefix + k, d ? 1 : 0) != 0;
        private static void SetF(string k, float v) { PlayerPrefs.SetFloat(Prefix + k, v); Notify(); }
        private static void SetI(string k, int v) { PlayerPrefs.SetInt(Prefix + k, v); Notify(); }
        private static void SetB(string k, bool v) { PlayerPrefs.SetInt(Prefix + k, v ? 1 : 0); Notify(); }
        private static void Notify() => Changed?.Invoke();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Changed = null;
    }
}
