using Utility.Classes.Discretizer;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.Regulizers
{
    /// <summary>
    /// Provides no regularization.
    /// </summary>
    public sealed class NoRegularizer : IRegularizer
    {
        public double EvaluateTerm(IDiscretization discretization, ConductivityDistribution sigma) => 0.0;

        public ConductivityDistribution EvaluateGradient(IDiscretization discretization, ConductivityDistribution sigma)
        {
            var zeroGradient = sigma.Conductivities.ToDictionary(kvp => kvp.Key, kvp => 0.0);
            return new ConductivityDistribution(zeroGradient);
        }
    }
}
