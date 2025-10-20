using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericOptimizers
{
    /// <summary>
    /// Adam optimizer: adaptive moment estimates.
    /// Maintains per-element m and v, applies bias correction.
    /// </summary>
    public sealed class AdamGradientOptimizer : INumericOptimizer
    {
        private const double _minConductivity = 1e-6;
        private const double _maxConductivity = 10.0;
        private readonly double _beta1;
        private readonly double _beta2;
        private readonly double _eps;
        private readonly double _weightDecay;
        private readonly double? _maxGradNorm;
        private readonly double _velocityClip = 1000.0;  // Prevent excessive velocity

        private int _t = 0;
        private Dictionary<int, double>? _m;  // 1st moment
        private Dictionary<int, double>? _v;  // 2nd moment

        public AdamGradientOptimizer(
            double beta1 = 0.9,
            double beta2 = 0.999,
            double epsilon = 1e-8,
            double weightDecay = 0.0,
            double? maxGradientNorm = null)
        {
            _beta1 = beta1;
            _beta2 = beta2;
            _eps = epsilon;
            _weightDecay = weightDecay;
            _maxGradNorm = maxGradientNorm;

            _m = null;
            _v = null;
        }

        public ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize)
        {
            // lazy init
            if (_m == null || _v == null)
            {
                _m = currentSigma.Conductivities.ToDictionary(kv => kv.Key, kv => 0.0);
                _v = currentSigma.Conductivities.ToDictionary(kv => kv.Key, kv => 0.0);
            }
            _t++;

            // optional gradient clipping by global norm
            double scale = 1.0;
            if (_maxGradNorm.HasValue)
            {
                double norm = Math.Sqrt(totalGradient.Conductivities.Sum(kv => kv.Value * kv.Value));
                if (norm > _maxGradNorm.Value && norm > 0)
                {
                    scale = _maxGradNorm.Value / norm;
                }
            }

            var newSigma = new Dictionary<int, double>();
            foreach (var kv in currentSigma.Conductivities)
            {
                int id = kv.Key;
                double conductivity = kv.Value;
                double original = conductivity;
                double gradient = totalGradient.GetConductivity(id) * scale;

                if (!double.IsFinite(gradient))
                {
                    // skip update on invalid gradient
                    newSigma[id] = conductivity;
                    continue;
                }

                // decoupled weight decay (AdamW)
                if (_weightDecay != 0.0)
                {
                    conductivity = Math.Max(_minConductivity,
                        conductivity * (1 - stepSize * _weightDecay));
                }

                // update biased moments
                double m_prev = _m[id];
                double v_prev = _v[id];
                double m_new = _beta1 * m_prev + (1 - _beta1) * gradient;
                double v_new = _beta2 * v_prev + (1 - _beta2) * gradient * gradient;

                // Protect against NaN
                if (!double.IsFinite(m_new) || !double.IsFinite(v_new))
                {
                    newSigma[id] = conductivity;
                    continue;
                }

                _m[id] = m_new;
                _v[id] = v_new;

                // bias-corrected with protection against div/0 and overflow
                double m_hat = 0.0;
                double v_hat = 0.0;
                double beta1_t = Math.Pow(_beta1, _t);
                double beta2_t = Math.Pow(_beta2, _t);

                if (beta1_t < 1.0 && beta2_t < 1.0)
                {
                    m_hat = m_new / (1 - beta1_t);
                    v_hat = v_new / (1 - beta2_t);
                }

                double denom = Math.Sqrt(Math.Max(0.0, v_hat)) + _eps;
                if (!double.IsFinite(denom) || Math.Abs(denom) < double.Epsilon)
                {
                    newSigma[id] = conductivity;
                    continue;
                }

                // Compute and clip velocity
                double velocity = stepSize * m_hat / denom;
                if (Math.Abs(velocity) > _velocityClip)
                {
                    velocity = Math.Sign(velocity) * _velocityClip;
                }

                // update σ with bounds
                double σn = conductivity - velocity;
                σn = NumericOptimizerGuards.ClipExcessiveGrowth(original, σn);
                σn = Math.Max(_minConductivity, Math.Min(_maxConductivity, σn));
                newSigma[id] = σn;
            }
            return new ConductivityDistribution(newSigma);
        }
    }
}
