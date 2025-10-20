using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericOptimizers
{
    /// <summary>
    /// Polyak Heavy Ball: adds momentum term β*(σ⁽ᵏ⁾−σ⁽ᵏ⁻¹⁾)
    /// Eq: v_{k+1}=βv_k −α∇J, σ_{k+1}=σ_k+v_{k+1}
    /// </summary>
    public sealed class PolyakHeavyBallOptimizer : INumericOptimizer
    {
        private const double _minConductivity = 1e-6;
        private const double _maxConductivity = 10.0;

        private readonly double _beta;
        private Dictionary<int, double>? _velocity;
        private Dictionary<int, double>? _pendingVelocity;
        private Dictionary<int, double>? _pendingSigma;

        public PolyakHeavyBallOptimizer(double beta = 0.8)
        {
            _beta = beta;
            _velocity = null;
        }

        public ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize)
        {
            CommitPendingStateIfAccepted(currentSigma);

            _velocity ??= currentSigma.Conductivities.ToDictionary(kv => kv.Key, _ => 0.0);

            var nextVel = new Dictionary<int, double>(currentSigma.Conductivities.Count);
            var nextSigma = new Dictionary<int, double>(currentSigma.Conductivities.Count);

            foreach (var kv in currentSigma.Conductivities)
            {
                int id = kv.Key;
                double conductivity = kv.Value;
                double gradient = totalGradient.GetConductivity(id);
                double v_prev = _velocity.TryGetValue(id, out double existingVelocity) ? existingVelocity : 0.0;

                double v_new = _beta * v_prev - stepSize * gradient;
                double candidate = conductivity + v_new;
                double clipped = NumericOptimizerGuards.ClipExcessiveGrowth(conductivity, candidate);
                clipped = Math.Max(_minConductivity, Math.Min(_maxConductivity, clipped));
                double appliedVelocity = clipped - conductivity;

                nextVel[id] = appliedVelocity;
                nextSigma[id] = clipped;
            }

            _pendingVelocity = NumericOptimizerGuards.Clone(nextVel);
            _pendingSigma = NumericOptimizerGuards.Clone(nextSigma);

            return new ConductivityDistribution(nextSigma);
        }

        private void CommitPendingStateIfAccepted(ConductivityDistribution currentSigma)
        {
            if (_pendingSigma == null)
            {
                return;
            }

            if (!NumericOptimizerGuards.ApproximatelyEqual(currentSigma.Conductivities, _pendingSigma))
            {
                return;
            }

            _velocity = _pendingVelocity != null
                ? NumericOptimizerGuards.Clone(_pendingVelocity)
                : currentSigma.Conductivities.ToDictionary(kv => kv.Key, _ => 0.0);

            _pendingSigma = null;
            _pendingVelocity = null;
        }
    }
}
