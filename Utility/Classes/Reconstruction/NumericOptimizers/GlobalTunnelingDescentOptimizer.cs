using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericOptimizers
{
    /// <summary>
    /// Global Tunneling Descent: adds a repeller R around stalled minima.
    /// When gradient norm < tol, step on J+μR to tunnel out.
    /// </summary>
    public sealed class GlobalTunnelingDescentOptimizer : INumericOptimizer
    {
        private readonly double _tol;
        private readonly double _mu0;
        private readonly double _maxMu;  // Upper bound on mu growth
        private ConductivityDistribution _anchor;  // σ^k at stall
        private bool _anchored = false;
        private double _mu;
        private const double _delta = 0.5; // width of repeller in log domain
        private const double _minConductivity = 1e-6;
        private const double _maxConductivity = 10.0;

        private GradientBasedOptimizer GradientBasedOptimizer = new();

        public GlobalTunnelingDescentOptimizer(double tolerance = 1e-3, double mu0 = 1.0, double maxMu = 1000.0)
        {
            _anchor = new([]);
            _tol = tolerance;
            _mu0 = mu0;
            _maxMu = maxMu;
        }

        public ConductivityDistribution OptimizationStep(ConductivityDistribution sigmaK, ConductivityDistribution totalGradient, double alpha)
        {
            // compute grad norm
            double normG = Math.Sqrt(totalGradient.Conductivities.Sum(kv => kv.Value * kv.Value));

            if (normG > _tol || !_anchored)
            {
                // normal gradient descent
                _anchored = false;
                return GradientBasedOptimizer.OptimizationStep(sigmaK, totalGradient, alpha);
            }

            // stalled: set anchor and mu
            if (!_anchored)
            {
                _anchor = sigmaK;
                _mu = _mu0;
                _anchored = true;
            }

            // compute repeller gradient in log domain with protection against log(0)
            var repGrad = new Dictionary<int, double>();
            foreach (var kv in sigmaK.Conductivities)
            {
                int id = kv.Key;
                double conductivity = Math.Max(_minConductivity, kv.Value);
                double anchorConductivity = Math.Max(_minConductivity, _anchor.GetConductivity(id));

                double eta = Math.Log(conductivity);
                double eta0 = Math.Log(anchorConductivity);

                // Protect against extreme differences in log domain
                if (Math.Abs(eta - eta0) > 20.0)
                {
                    repGrad[id] = 0.0;
                    continue;
                }

                double r = Math.Exp(-Math.Pow(eta - eta0, 2) / (_delta * _delta));

                // ∇_σ R = dr/dη * dη/dσ = r * (-2(η-η0)/δ²) * (1/σ)
                // Protect against division by zero
                if (conductivity < double.Epsilon)
                {
                    repGrad[id] = 0.0;
                    continue;
                }

                double dRdσ = r * (-2 * (eta - eta0) / (_delta * _delta)) / conductivity;

                // Clip extreme gradients
                if (!double.IsFinite(dRdσ))
                {
                    dRdσ = 0.0;
                }
                repGrad[id] = dRdσ;
            }

            // tunnel step: G+μ repGrad with bounded μ
            var total = new Dictionary<int, double>();
            foreach (var kv in totalGradient.Conductivities)
            {
                int id = kv.Key;
                double combinedGrad = kv.Value + Math.Min(_maxMu, _mu) * repGrad[id];

                // Clip extreme combined gradients
                if (!double.IsFinite(combinedGrad))
                {
                    combinedGrad = kv.Value;
                }
                total[id] = combinedGrad;
            }

            // increase mu for next time with upper bound
            _mu = Math.Min(_maxMu, _mu * 1.5);

            var result = GradientBasedOptimizer.OptimizationStep(sigmaK, new ConductivityDistribution(total), alpha);

            // Ensure result stays within bounds
            var bounded = new Dictionary<int, double>();
            foreach (var kv in result.Conductivities)
            {
                bounded[kv.Key] = Math.Max(_minConductivity, Math.Min(_maxConductivity, kv.Value));
            }

            return new ConductivityDistribution(bounded);
        }
    }
}
