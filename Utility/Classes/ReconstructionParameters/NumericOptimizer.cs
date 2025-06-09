namespace Utility.Classes.ReconstructionParameters
{
    public enum NumericOptimizer
    {
        GradientBased = 1,
        ParticleSwarm = 2
    };

    /// <summary>
    /// Defines a strategy for updating the solution parameters (conductivity)
    /// based on a calculated gradient.
    /// </summary>
    public interface INumericOptimizer
    {
        /// <summary>
        /// Takes one optimization step.
        /// </summary>
        /// <param name="currentSigma">The current conductivity distribution.</param>
        /// <param name="totalGradient">The total gradient of the cost function.</param>
        /// <returns>The updated conductivity distribution for the next iteration.</returns>
        ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize);
    }

    public sealed class GradientBasedOptimizer : INumericOptimizer
    {
        // TODO: Adaptive step size
        private readonly double _stepSize;
        private const double MinimumConductivity = 1e-6;

        public GradientBasedOptimizer(double stepSize = 1e-3)
        {
            _stepSize = stepSize;
        }

        public ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize)
        {
            var nextSigmaDict = new Dictionary<int, double>();
            foreach (var kvp in currentSigma.Conductivities)
            {
                int key = kvp.Key;
                double currentVal = kvp.Value;
                double gradVal = totalGradient.GetConductivity(key);
                double nextVal = currentVal - stepSize * gradVal;

                // Use Math.Max to ensure the conductivity never drops below the minimum threshold.
                nextSigmaDict[key] = Math.Max(MinimumConductivity, nextVal);
            }
            return new ConductivityDistribution(nextSigmaDict);
        }
    }

    public sealed class ParticleSwarmOptimizer : INumericOptimizer
    {
        public ConductivityDistribution OptimizationStep(ConductivityDistribution currentSigma, ConductivityDistribution totalGradient, double stepSize)
        {
            throw new NotImplementedException();

            // TODO: particvel swarm implementation
            // return guess;
        }
    }
}