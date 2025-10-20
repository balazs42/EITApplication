using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericOptimizers
{
    /// <summary>
    /// Homotopy Continuation: blends convex prior and true cost via parameter t.
    /// H = (1-t) R₀ + t J, GradH = (1-t) ∇R₀ + t ∇J.  Warm-starts σ each stage.
    /// </summary>
    public sealed class HomotopyContinuationOptimizer : INumericOptimizer
    {
        private readonly ConductivityDistribution _sigmaPrior;
        private readonly int _steps;
        private int _stage;
        private GradientBasedOptimizer GradientBasedOptimizer = new();

        public HomotopyContinuationOptimizer(ConductivityDistribution sigmaPrior, int steps = 20)
        {
            _sigmaPrior = sigmaPrior;
            _steps = steps;
            _stage = 0;
        }

        public ConductivityDistribution OptimizationStep(ConductivityDistribution sigmaK, ConductivityDistribution gradJ, double stepSize)
        {
            // schedule t in [0,1]
            double t = (double)_stage / Math.Max(1, _steps);

            // ∇R₀ = σk - σ_prior
            var gradR0 = sigmaK.Conductivities.ToDictionary(
                kv => kv.Key,
                kv => kv.Value - _sigmaPrior.GetConductivity(kv.Key)
            );

            // GradH = (1-t)*gradR0 + t*gradJ
            var total = new Dictionary<int, double>();
            foreach (var kv in sigmaK.Conductivities)
            {
                int id = kv.Key;
                double g0 = gradR0[id];
                double gJ = gradJ.GetConductivity(id);
                total[id] = (1 - t) * g0 + t * gJ;
            }

            // one GD step on H
            var next = GradientBasedOptimizer.OptimizationStep(sigmaK, new ConductivityDistribution(total), stepSize);
            _stage = Math.Min(_stage + 1, _steps);
            return next;
        }
    }
}
