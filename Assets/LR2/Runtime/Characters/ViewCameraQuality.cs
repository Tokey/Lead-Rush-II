// Lead Rush II - anti-aliasing for the view camera (B43, user 2026-11-17: "enable TAA? edges are sharp").
// TAA was already on, but the builder set taaSharpenStrength 0.65, which brings crisp, stair-stepped edges back. This applies
// the LR2Settings.AntiAliasing mode with softer TAA values to the local player's camera and the menu camera.

using LR2.Core;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace LR2.Characters
{
    public sealed class ViewCameraQuality : MonoBehaviour
    {
        [Header("TAA (used when Anti-Aliasing = TAA)")]
        [Tooltip("Post-TAA sharpening. 0.65 (the old value) looked aliased; 0.25-0.35 keeps texture detail without stair-steps.")]
        [Range(0f, 1f)] public float taaSharpen = 0.3f;
        [Tooltip("Sharpening of the history buffer.")]
        [Range(0f, 1f)] public float taaHistorySharpening = 0.15f;
        [Tooltip("Higher = less shimmer on thin and specular edges.")]
        [Range(0f, 1f)] public float taaAntiFlicker = 0.7f;
        [Tooltip("Higher = smoother (more history), lower = less ghosting.")]
        [Range(0.6f, 0.95f)] public float taaBaseBlend = 0.9f;

        private Camera _applied, _appliedMain;
        private int _appliedMode = -1;
        private int _appliedUpscaler = -1;
        private float _nextCheck;

        private void OnEnable() => LR2Settings.Changed += Invalidate;
        private void OnDisable() => LR2Settings.Changed -= Invalidate;
        private void Invalidate() { _nextCheck = 0f; _appliedMode = -1; _appliedUpscaler = -1; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (FindAnyObjectByType<ViewCameraQuality>() != null) return;
            var go = new GameObject("LR2_ViewCameraQuality");
            DontDestroyOnLoad(go);
            go.AddComponent<ViewCameraQuality>();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;

            int mode = LR2Settings.AntiAliasing;
            int upscaler = LR2Settings.UpscalerMode;
            Camera view = null;
            foreach (var s in FindObjectsByType<SoldierController>())
                if (s != null && s.IsLocalPlayer && s.ViewCamera != null) { view = s.ViewCamera; break; }
            Camera main = Camera.main;
            if (view == _applied && main == _appliedMain && mode == _appliedMode && upscaler == _appliedUpscaler) return;

            if (view != null) Apply(view, mode, upscaler);
            if (main != null && main != view) Apply(main, mode, upscaler);
            _applied = view;
            _appliedMain = main;
            _appliedMode = mode;
            _appliedUpscaler = upscaler;
        }

        private void Apply(Camera cam, int mode, int upscaler)
        {
            UpscalerBackend.ApplyToCamera(cam, mode, upscaler, taaSharpen, taaHistorySharpening, taaAntiFlicker, taaBaseBlend);
        }
    }
}
