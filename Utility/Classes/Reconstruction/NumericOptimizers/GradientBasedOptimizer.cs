using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.NumericOptimizers
{
    /// <summary>
    /// Simple gradient descent: σ←σ−α∇J, clamped to physical bounds.
    /// </summary>
    public sealed class GradientBasedOptimizer : INumericOptimizer
    {
        private readonly double _min = 1e-6;
        private readonly double _max = 10.0;

        public ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize)
        {
            // loop over elements
            var next = new Dictionary<int, double>(currentSigma.Conductivities.Count);
            foreach (var kv in currentSigma.Conductivities)
            {
                int id = kv.Key;

                double conductivity = kv.Value;
                double gradient = totalGradient.GetConductivity(id);
                double nextValue = conductivity - stepSize * gradient;          // standard GD step

                nextValue = NumericOptimizerGuards.ClipExcessiveGrowth(conductivity, nextValue);
                nextValue = Math.Max(_min, Math.Min(_max, nextValue));          // clamp

                next[id] = nextValue;
            }
            return new ConductivityDistribution(next);
        }
    }
}
