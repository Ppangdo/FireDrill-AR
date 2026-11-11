using FireDrill.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FireDrill.Runtime
{
    /// <summary>16.11 body effects without gauges: a global URP Volume whose vignette, saturation and blur follow the
    /// body stage (effects.body.stages[0..3]); stage 3 shakes the view (PC only). A hot handle flashes the screen
    /// edge red. Presentation only: the stage comes from PlayerExposure.</summary>
    public sealed class BodyEffects : MonoBehaviour
    {
        private RuntimeConfig.BodySettings _cfg;
        private IPlatform _platform;
        private Volume _volume;
        private Vignette _vignette;
        private ColorAdjustments _color;
        private DepthOfField _dof;
        private Color _hotColor;
        private float _hotLeft;
        private int _stage;
        private bool _enabled = true;

        public int Stage => _stage;
        public float VignetteIntensity => _vignette.intensity.value;

        public void Init(IPlatform platform, RuntimeConfig rt)
        {
            _cfg = rt.Effects.Body;
            _platform = platform;
            _hotColor = Materials.Parse(_cfg.HotColor);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _vignette = profile.Add<Vignette>(true);
            _color = profile.Add<ColorAdjustments>(true);
            _dof = profile.Add<DepthOfField>(true);
            _dof.mode.Override(DepthOfFieldMode.Gaussian);
            _dof.gaussianStart.Override(_cfg.BlurStartM);
            _dof.gaussianEnd.Override(_cfg.BlurEndM);
            _volume = gameObject.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 10;
            _volume.sharedProfile = profile;
            var cam = platform.Camera;
            if (cam != null) cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            SetStage(0);
        }

        public void SetStage(int stage)
        {
            _stage = Mathf.Clamp(stage, 0, _cfg.Stages.Count - 1);
            Apply();
        }

        public void HotHandle() => _hotLeft = _cfg.HotFlashS;

        /// <summary>Aborted / paused: every effect off (명세서 5장).</summary>
        public void SetVisible(bool visible)
        {
            _enabled = visible;
            _volume.weight = visible ? 1f : 0f;
            if (!visible) _platform.SetShake(0f, 0f);
            else Apply();
        }

        private void Apply()
        {
            var look = _cfg.Stages[_stage];
            _vignette.intensity.Override(look.Vignette);
            _vignette.color.Override(Color.black);
            _color.saturation.Override(look.Saturation);
            _dof.active = look.BlurRadius > 0f;
            _dof.gaussianMaxRadius.Override(look.BlurRadius);
            if (_enabled) _platform.SetShake(_stage >= 3 ? _cfg.ShakeDeg : 0f, _cfg.ShakeHz);
        }

        private void Update()
        {
            if (_hotLeft <= 0f) return;
            _hotLeft -= Time.deltaTime;
            float k = Mathf.Clamp01(_hotLeft / Mathf.Max(_cfg.HotFlashS, 1e-3f));
            var baseLook = _cfg.Stages[_stage];
            _vignette.color.Override(Color.Lerp(Color.black, _hotColor, k));
            _vignette.intensity.Override(Mathf.Max(baseLook.Vignette, 0.45f * k));
            if (_hotLeft <= 0f) Apply();
        }
    }
}
