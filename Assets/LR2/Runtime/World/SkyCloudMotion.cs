using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace LR2.World
{
    /// <summary>
    /// B138: Drives real HDRP PhysicallyBasedSky + VolumetricClouds that continuously drift with dynamic wind and react
    /// physically to WeatherSystem presets (DuskClear sculpted cumulus -> Overcast deck -> Drizzle -> dark towering Thunderstorm
    /// illuminated by lightning flashes). Replaces HDRI procedural pinch distortion (explicitly disables HDRISky distortion if found).
    /// </summary>
    public class SkyCloudMotion : MonoBehaviour
    {
        private VolumetricClouds _clouds;
        private CloudLayer _cloudLayer;
        private PhysicallyBasedSky _physSky;
        private HDRISky _hdriSky;
        private float _speed = -1f;
        private float _storm;
        private float _baseDensity = 0.42f;
        private float _baseShape = 0.855f;
        private float _baseErosion = 0.88f;
        private float _baseMultiScatter = 0.50f;
        private float _seed;

        public bool HasVolumetricClouds => _clouds != null;
        public bool HasCloudLayer => _cloudLayer != null && _cloudLayer.active;
        public bool VolumetricCloudsEnabled => _clouds != null && _clouds.enable.value;
        public bool HasPinchedHdriDistortion => _hdriSky != null && _hdriSky.active && _hdriSky.distortionMode.value != HDRISky.DistortionMode.None;
        public float CurrentWindSpeed => Mathf.Max(0f, _speed);
        public float CurrentCloudDensity => _clouds != null ? _clouds.densityMultiplier.value : 0f;

        public static void Ensure()
        {
            if (FindAnyObjectByType<SkyCloudMotion>() == null) new GameObject("[LR2 SkyMotion]").AddComponent<SkyCloudMotion>();
        }

        private void Start()
        {
            _seed = Random.value * 100f;
            foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                if (v == null || v.sharedProfile == null) continue;
                var prof = v.profile;
                if (prof == null) continue;

                if (prof.TryGet(out HDRISky h))
                {
                    _hdriSky = h;
                    _hdriSky.distortionMode.Override(HDRISky.DistortionMode.None);
                }
                if (prof.TryGet(out PhysicallyBasedSky p)) _physSky = p;
                if (prof.TryGet(out CloudLayer cl)) _cloudLayer = cl;
                if (prof.TryGet(out VolumetricClouds vc))
                {
                    _clouds = vc;
                    _baseDensity = vc.densityMultiplier.value > 0.05f ? vc.densityMultiplier.value : 0.42f;
                    // In HDRP VolumetricClouds, 0.82-0.88 keeps distinct sculpted cumulus islands; lower values flatten to grey overcast soup.
                    _baseShape = vc.shapeFactor.value >= 0.72f && vc.shapeFactor.value <= 0.94f ? vc.shapeFactor.value : 0.855f;
                    _baseErosion = vc.erosionFactor.value > 0.05f ? vc.erosionFactor.value : 0.88f;
                    _baseMultiScatter = vc.multiScattering.value > 0.05f ? vc.multiScattering.value : 0.50f;
                }
                if (_clouds != null && v.isGlobal) break;
            }

            if (_clouds == null && _cloudLayer == null)
            {
                enabled = false;
                return;
            }

            if (_clouds != null)
            {
                _clouds.enable.Override(true);
                _clouds.globalWindSpeed.overrideState = true;
                _clouds.orientation.overrideState = true;
                _clouds.verticalShapeWindSpeed.overrideState = true;
                _clouds.verticalErosionWindSpeed.overrideState = true;
                _clouds.densityMultiplier.overrideState = true;
                _clouds.shapeFactor.overrideState = true;
                _clouds.erosionFactor.overrideState = true;
                _clouds.multiScattering.overrideState = true;
                _clouds.sunLightDimmer.overrideState = true;
                _clouds.ambientLightProbeDimmer.overrideState = true;
                _clouds.scatteringTint.overrideState = true;
            }
            if (_cloudLayer != null)
            {
                _cloudLayer.opacity.overrideState = true;
                _cloudLayer.layerA.scrollOrientation.overrideState = true;
                _cloudLayer.layerA.scrollSpeed.overrideState = true;
                _cloudLayer.layerB.scrollOrientation.overrideState = true;
                _cloudLayer.layerB.scrollSpeed.overrideState = true;
            }
        }

        private static WindParameter.WindParamaterValue CustomWind(float v) =>
            new WindParameter.WindParamaterValue { mode = WindParameter.WindOverrideMode.Custom, customValue = v, additiveValue = 0f, multiplyValue = 1f };

        private void Update()
        {
            var w = WeatherSystem.Instance;
            float rain = w != null ? Mathf.Clamp01(w.EffectiveRain) : 0f;
            float windFactor = WeatherSystem.Wind;
            float flash = w != null ? w.LightningFlash : 0f;
            float stormTarget = w != null ? w.CloudStormWeight : Mathf.Clamp01(rain * 0.85f);
            _storm = Mathf.MoveTowards(_storm, stormTarget, Time.deltaTime * 0.14f);

            float t = Time.time + _seed;
            float gust = 1f + (Mathf.PerlinNoise(t * 0.08f, 3.1f) - 0.5f) * (0.42f + 0.55f * _storm)
                            + (Mathf.PerlinNoise(t * 0.25f, 5.7f) - 0.5f) * 0.25f * _storm;
            float baseWind = Mathf.Lerp(42f, 185f, Mathf.Clamp01(Mathf.Max(_storm, windFactor * 0.65f)));
            float targetSpeed = baseWind * Mathf.Max(0.4f, gust);
            _speed = _speed < 0f ? targetSpeed : Mathf.Lerp(_speed, targetSpeed, 1f - Mathf.Exp(-Time.deltaTime * 0.7f));
            float dir = 232f + (Mathf.PerlinNoise(t * 0.012f, 7.7f) - 0.5f) * (24f + 42f * _storm);

            if (_clouds != null)
            {
                _clouds.globalWindSpeed.value = CustomWind(_speed);
                _clouds.orientation.value = CustomWind(Mathf.Repeat(dir, 360f));
                _clouds.verticalShapeWindSpeed.value = Mathf.Lerp(3f, 18f, _storm);
                _clouds.verticalErosionWindSpeed.value = Mathf.Lerp(4.5f, 22f, _storm);

                // Morph sculpted cumulus into dramatic storm clouds while keeping 3D edge contrast and blue sky gaps.
                float pulse = (Mathf.PerlinNoise(t * 0.03f, 1.4f) - 0.5f) * 0.06f;
                _clouds.densityMultiplier.value = Mathf.Clamp(Mathf.Lerp(_baseDensity, 0.50f, _storm) + pulse, 0.30f, 0.58f);
                _clouds.shapeFactor.value = Mathf.Clamp(_baseShape - 0.085f * _storm, 0.74f, 0.92f);
                _clouds.erosionFactor.value = Mathf.Clamp(Mathf.Lerp(_baseErosion, 0.80f, _storm), 0.72f, 0.94f);
                _clouds.multiScattering.value = Mathf.Clamp01(Mathf.Lerp(_baseMultiScatter, 0.34f, _storm) + flash * 0.45f);
                _clouds.sunLightDimmer.value = Mathf.Clamp(Mathf.Lerp(1.0f, 0.58f, _storm) + flash * 0.42f, 0.45f, 1.0f);
                _clouds.ambientLightProbeDimmer.value = Mathf.Clamp(Mathf.Lerp(0.95f, 0.84f, _storm) + flash * 0.15f, 0.84f, 1.0f);
                _clouds.scatteringTint.value = Color.Lerp(Color.black, new Color(0.20f, 0.16f, 0.10f, 1f), _storm * (1f - flash * 0.85f));
            }

            if (_cloudLayer != null)
            {
                _cloudLayer.opacity.value = Mathf.Clamp01(Mathf.Lerp(0.42f, 0.65f, _storm));
                _cloudLayer.layerA.scrollOrientation.value = CustomWind(Mathf.Repeat(dir - 12f, 360f));
                _cloudLayer.layerA.scrollSpeed.value = CustomWind(_speed * 0.35f);
                _cloudLayer.layerB.scrollOrientation.value = CustomWind(Mathf.Repeat(dir + 18f, 360f));
                _cloudLayer.layerB.scrollSpeed.value = CustomWind(_speed * 0.20f);
            }
        }
    }
}
