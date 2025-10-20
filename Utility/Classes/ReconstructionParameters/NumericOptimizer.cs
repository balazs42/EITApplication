namespace Utility.Classes.ReconstructionParameters
{
    public enum NumericOptimizer
    {
        GradientBased = 1,
        Polyak = 2,
        ADAM = 3,
        Nesterov = 4,
        GlobalTunneling = 5,
        HomotopyContinuation = 6,
        SimulatedAnnealing = 7,
        ParticleSwarm = 8,
        BFGS = 9
    };

    /// <summary>
    /// Strategy interface for updating conductivity via one optimization step.
    /// </summary>
    public interface INumericOptimizer
    {
        /// <param name="currentSigma">σ⁽ᵏ⁾, current conductivity distribution</param>
        /// <param name="totalGradient">G_σ = ∂J/∂σ, gradient of cost wrt σ</param>
        /// <param name="stepSize">α, base step length</param>
        /// <returns>σ⁽ᵏ⁺¹⁾, updated distribution</returns>
        ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize);
    }

    internal static class NumericOptimizerGuards
    {
        private const double GrowthLimit = 1_000.0;
        private const double BaselineEpsilon = 1e-12;

        public static double ClipExcessiveGrowth(double original, double candidate)
        {
            if (double.IsNaN(original) || double.IsInfinity(original))
            {
                return candidate;
            }

            double baseline = Math.Max(Math.Abs(original), BaselineEpsilon);
            double maxMagnitude = baseline * GrowthLimit;

            if (double.IsNaN(candidate))
            {
                return candidate;
            }

            double magnitude = Math.Abs(candidate);
            if (double.IsInfinity(magnitude) || magnitude > maxMagnitude)
            {
                double sign = candidate < 0 ? -1.0 : 1.0;
                return sign * maxMagnitude;
            }

            return candidate;
        }

        public static Dictionary<int, double> Clone(Dictionary<int, double> source)
            => source.ToDictionary(kv => kv.Key, kv => kv.Value);

        public static bool ApproximatelyEqual(
            Dictionary<int, double> first,
            Dictionary<int, double> second,
            double tolerance = 1e-12)
        {
            if (ReferenceEquals(first, second))
            {
                return true;
            }

            if (first.Count != second.Count)
            {
                return false;
            }

            foreach (var kvp in first)
            {
                if (!second.TryGetValue(kvp.Key, out double value))
                {
                    return false;
                }

                if (Math.Abs(kvp.Value - value) > tolerance)
                {
                    return false;
                }
            }

            return true;
        }
    }
}