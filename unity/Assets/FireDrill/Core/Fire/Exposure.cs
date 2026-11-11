using System;

namespace FireDrill.Core
{
    /// <summary>
    /// Accumulated dose (명세서 4장 FED). FED += c × towel factor × dt / full_dose_s, never decreases, capped at 1.
    /// Stage = number of cfg.Fed.Stages thresholds reached: 0..3, 4 = failed.
    /// StageChanged fires once per stage reached, in order (even when one step crosses several).
    /// </summary>
    public sealed class Exposure
    {
        /// <summary>Thresholds come from float config; FED within this of a threshold counts as reached.</summary>
        private const double Eps = 1e-6;
        private readonly FireConfig.FedConfig _cfg;
        private double _fed;

        public Exposure(FireConfig cfg)
        {
            _cfg = (cfg ?? throw new ArgumentNullException(nameof(cfg))).Fed
                   ?? throw new ArgumentException("fire.fed is required", nameof(cfg));
            if (_cfg.Stages == null || _cfg.Stages.Count == 0) throw new ArgumentException("fire.fed.stages is required", nameof(cfg));
        }

        public float Fed => (float)_fed;
        public int Stage { get; private set; }
        public bool Failed => Stage >= _cfg.Stages.Count;
        public event Action<int> StageChanged;

        public float TowelFactor(TowelState towel)
        {
            switch (towel)
            {
                case TowelState.None: return _cfg.Towel.None;
                case TowelState.Dry: return _cfg.Towel.Dry;
                case TowelState.Wet: return _cfg.Towel.Wet;
                default: throw new ArgumentOutOfRangeException(nameof(towel), towel, null);
            }
        }

        public void Accumulate(float c, TowelState towel, float dt)
        {
            if (c <= 0 || dt <= 0 || Failed) return;
            _fed = Math.Min(1.0, _fed + (double)c * TowelFactor(towel) * dt / _cfg.FullDoseS);
            while (Stage < _cfg.Stages.Count && _fed + Eps >= _cfg.Stages[Stage])
            {
                Stage++;
                StageChanged?.Invoke(Stage);
            }
        }
    }
}
