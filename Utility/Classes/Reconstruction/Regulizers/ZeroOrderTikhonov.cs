using Utility.Classes.Discretizer;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.Regulizers
{

    /// <summary>
    /// Implements Zero-Order Tikhonov regularization. J = (λ/2) * ||σ - σ_prior||^2.
    /// </summary>
    public sealed class ZeroOrderTikhonov : IRegularizer
    {
        private readonly double _lambda;
        private readonly ConductivityDistribution _sigmaPrior;

        public ZeroOrderTikhonov(ConductivityDistribution sigmaPrior, double lambda = 1e-4)
        {
            _sigmaPrior = sigmaPrior;
            _lambda = lambda;
        }

        public double EvaluateTerm(IDiscretization discretization, ConductivityDistribution sigma)
        {
            double sumOfSquares = 0.0;
            foreach (var kvp in sigma.Conductivities)
            {
                double residual = kvp.Value - _sigmaPrior.GetConductivity(kvp.Key);
                sumOfSquares += residual * residual;
            }
            // Note: A true L2-norm would involve integrating over the domain,
            // which means multiplying by element area. This is a simplified sum.
            return 0.5 * _lambda * sumOfSquares;
        }

        public ConductivityDistribution EvaluateGradient(IDiscretization discretization, ConductivityDistribution sigma)
        {
            // Gradient is λ * (σ - σ_prior).
            var gradientDict = new Dictionary<int, double>();

            foreach (var kvp in sigma.Conductivities)
                gradientDict[kvp.Key] = _lambda * (kvp.Value - _sigmaPrior.GetConductivity(kvp.Key));

            return new ConductivityDistribution(gradientDict);
        }
    }
}
