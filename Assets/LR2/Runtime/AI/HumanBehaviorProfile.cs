using System;
using System.IO;
using UnityEngine;

namespace LR2.AI
{
    /// <summary>
    /// B157: Data-driven behavioral & biomechanical parameters fitted offline from real human experiment sessions
    /// (<c>Tools/analyze_experiment_session.py</c> -> <c>human_behavior_fit.json</c>). Used by <see cref="BotArchetype.HumanFitted"/>.
    /// </summary>
    [Serializable]
    public class HumanBehaviorProfile
    {
        public string sourceSession = "default_balabit_esta_baseline";
        public string fittedAtUtc = "";
        public int humanSamplesUsed;

        // Saccade / Fitts's Law & Balabit velocity curve
        public float fittsA = 0.065f;
        public float fittsB = 0.042f;
        public float betaA = 2.0f;
        public float betaB = 3.3f;

        // Log-normal visuomotor reaction latency (seconds = exp(mu + sigma * Z))
        public float reactMu = -0.95f;
        public float reactSigma = 0.28f;

        // Anisotropic flick endpoint spread & overshoot rate
        public float sigmaAlongFactor = 0.045f;
        public float sigmaPerpRatio = 0.44f;
        public float overshootRate = 0.16f;

        // Recoil compensation lag (s) & gain
        public float recoilLagSec = 0.095f;
        public float recoilCompGain = 0.78f;

        // Movement & tactical timing
        public float strafeSwitchHz = 1.25f;
        public float crouchDuty = 0.16f;
        public float adsRangeMeters = 9.0f;
        public float preferredRangeMeters = 18.0f;
        public float burstMean = 4.5f;
        public float burstPauseSec = 0.28f;
        public float reloadAmmoFrac = 0.35f;

        // Tier speed multipliers (Recruit, Regular, Hardened, Veteran)
        public float[] tierScale = { 0.72f, 0.90f, 1.00f, 1.15f };

        public float TierSkill(int diffIndex)
        {
            if (tierScale == null || tierScale.Length == 0) return 1f;
            return Mathf.Clamp(tierScale[Mathf.Clamp(diffIndex, 0, tierScale.Length - 1)], 0.45f, 1.6f);
        }
    }

    public static class HumanBehaviorProfileLoader
    {
        private static HumanBehaviorProfile _cached;
        private static bool _loaded;

        public static HumanBehaviorProfile Current
        {
            get
            {
                if (!_loaded) Reload();
                return _cached ??= new HumanBehaviorProfile();
            }
        }

        public static string DataFitPath => Path.Combine(LR2.Game.ExperimentPaths.ProfilesRoot, "human_behavior_fit.json");
        public static string EditorFitPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "LR2_Experiments", "human_behavior_fit.json"));
        public static string PlayerFitPath => Path.Combine(Application.persistentDataPath, "LR2_Experiments", "human_behavior_fit.json");

        public static void Reload()
        {
            _loaded = true;
            _cached = TryLoadFile(DataFitPath) ?? TryLoadFile(PlayerFitPath) ?? TryLoadFile(EditorFitPath) ?? new HumanBehaviorProfile();
        }

        private static HumanBehaviorProfile TryLoadFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                var p = JsonUtility.FromJson<HumanBehaviorProfile>(json);
                if (p != null)
                {
                    p.fittsA = Mathf.Clamp(p.fittsA, 0.02f, 0.35f);
                    p.fittsB = Mathf.Clamp(p.fittsB, 0.01f, 0.20f);
                    p.betaA = Mathf.Clamp(p.betaA, 1.1f, 6.0f);
                    p.betaB = Mathf.Clamp(p.betaB, 1.2f, 8.0f);
                    p.reactSigma = Mathf.Clamp(p.reactSigma, 0.05f, 0.85f);
                    p.recoilLagSec = Mathf.Clamp(p.recoilLagSec, 0.04f, 0.35f);
                    p.recoilCompGain = Mathf.Clamp(p.recoilCompGain, 0.25f, 1.15f);
                    p.strafeSwitchHz = Mathf.Clamp(p.strafeSwitchHz, 0.15f, 4.5f);
                    Debug.Log($"[LR2] Loaded HumanBehaviorProfile from {path} (source={p.sourceSession}, samples={p.humanSamplesUsed})");
                    return p;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LR2] Failed parsing {path}: {ex.Message}");
            }
            return null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _cached = null;
            _loaded = false;
        }
    }
}
