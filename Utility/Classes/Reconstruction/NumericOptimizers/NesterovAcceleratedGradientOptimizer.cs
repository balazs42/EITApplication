using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericOptimizers
{
    /// <summary>
    /// Nesterov accelerated gradient:
    /// y_k = σ_k + γ(σ_k - σ_{k-1}); then σ_{k+1} = y_k - α∇J(y_k)
    /// </summary>
    public sealed class NesterovAcceleratedGradientOptimizer : INumericOptimizer
    {
        private const double _minConductivity = 1e-6;
        private const double _maxConductivity = 10.0;

        private readonly double _gamma;
        private Dictionary<int, double>? _prevSigma;
        private Dictionary<int, double>? _pendingSigma;
        private Dictionary<int, double>? _pendingPrevSigma;

        public NesterovAcceleratedGradientOptimizer(double gamma = 0.9)
        {
            _gamma = gamma;
        }

        public ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize)
        {
            CommitPendingStateIfAccepted(currentSigma);

            _prevSigma ??= NumericOptimizerGuards.Clone(currentSigma.Conductivities);

            var y = new Dictionary<int, double>(currentSigma.Conductivities.Count);
            foreach (var kv in currentSigma.Conductivities)
            {
                int id = kv.Key;
                double conductivityK = kv.Value;
                double conductivityM = _prevSigma.TryGetValue(id, out double prevValue) ? prevValue : conductivityK;
                double extrapolated = conductivityK + _gamma * (conductivityK - conductivityM);
                y[id] = NumericOptimizerGuards.ClipExcessiveGrowth(conductivityK, extrapolated);
            }

            var nextSigma = new Dictionary<int, double>(y.Count);
            foreach (var kv in y)
            {
                int id = kv.Key;
                double yv = kv.Value;
                double gradient = totalGradient.GetConductivity(id);
                double nextValue = yv - stepSize * gradient;
                double original = currentSigma.GetConductivity(id);
                double clipped = NumericOptimizerGuards.ClipExcessiveGrowth(original, nextValue);
                clipped = Math.Max(_minConductivity, Math.Min(_maxConductivity, clipped));
                nextSigma[id] = clipped;
            }

            _pendingPrevSigma = NumericOptimizerGuards.Clone(currentSigma.Conductivities);
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

            _prevSigma = _pendingPrevSigma != null
                ? NumericOptimizerGuards.Clone(_pendingPrevSigma)
                : NumericOptimizerGuards.Clone(currentSigma.Conductivities);

            _pendingPrevSigma = null;
            _pendingSigma = null;
        }
    }
}
