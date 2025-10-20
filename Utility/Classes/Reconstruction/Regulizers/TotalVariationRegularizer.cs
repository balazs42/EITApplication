using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers;
using Utility.Classes.Solvers.FiniteElementSolver;
using Utility.Classes.Solvers.LatticeBoltzmannSolver;

namespace Utility.Classes.Reconstruction.Regulizers
{
    /// <summary>
    /// Implements Total Variation regularization. J = λ * ||∇σ||_L1.
    /// </summary>
    public sealed class TotalVariationRegularizer : IRegularizer
    {
        private readonly double _lambda;
        private const double Epsilon = 1e-8; // Small value to prevent division by zero

        public TotalVariationRegularizer(double lambda = 1e-3) => _lambda = lambda;

        public double EvaluateTerm(IDiscretization discretization, ConductivityDistribution sigma)
        {
            VectorField gradientField;
            if (discretization is FEMMesh femMesh)
            {
                gradientField = FiniteElementOperators.CalculateElementWiseGradient(femMesh, sigma.ToPotentialDistribution());
                double integral = 0;
                var elements = femMesh.GetElements().Cast<FEMElement>();

                foreach (var element in elements)
                {
                    var grad = gradientField.GetVector(element.Id);
                    integral += Math.Sqrt(grad.X * grad.X + grad.Y * grad.Y) * element.Area;
                }
                return _lambda * integral;
            }
            if (discretization is LBMGrid lbmGrid)
            {
                gradientField = LatticeBoltzmannOperators.CalculateGradient(lbmGrid, sigma.ToPotentialDistribution());
                double sum = gradientField.Data.Values.Sum(grad => Math.Sqrt(grad.X * grad.X + grad.Y * grad.Y));
                return _lambda * sum;
            }
            throw new NotSupportedException($"Mesh type {discretization.GetType().Name} not supported.");
        }

        public ConductivityDistribution EvaluateGradient(IDiscretization discretization, ConductivityDistribution sigma)
        {
            // Gradient is -λ * ∇·(∇σ / (||∇σ|| + ε)).

            // 1. Calculate the gradient field: ∇σ
            VectorField gradSigma;
            Dictionary<int, double> divergence;
            if (discretization is FEMMesh femMesh)
            {
                gradSigma = FiniteElementOperators.CalculateElementWiseGradient(femMesh, sigma.ToPotentialDistribution());
                var normalizedGradData = gradSigma.Data.ToDictionary(
                    kvp => kvp.Key,
                    kvp =>
                    {
                        double mag = Math.Sqrt(kvp.Value.X * kvp.Value.X + kvp.Value.Y * kvp.Value.Y);
                        double divisor = Math.Max(mag, Epsilon);
                        return (kvp.Value.X / divisor, kvp.Value.Y / divisor);
                    });
                var normalizedGradField = new VectorField(normalizedGradData);
                divergence = FiniteElementOperators.CalculateElementWiseDivergence(femMesh, normalizedGradField);
            }
            else if (discretization is LBMGrid grid)
            {
                gradSigma = LatticeBoltzmannOperators.CalculateGradient(grid, sigma.ToPotentialDistribution());
                var normalizedGradData = gradSigma.Data.ToDictionary(
                    kvp => kvp.Key,
                    kvp =>
                    {
                        double mag = Math.Sqrt(kvp.Value.X * kvp.Value.X + kvp.Value.Y * kvp.Value.Y);
                        double divisor = Math.Max(mag, Epsilon);
                        return (kvp.Value.X / divisor, kvp.Value.Y / divisor);
                    });
                var normalizedGradField = new VectorField(normalizedGradData);
                divergence = LatticeBoltzmannOperators.CalculateDivergence(grid, normalizedGradField).Get();
            }
            else
                throw new NotSupportedException($"Mesh type {discretization.GetType().Name} not supported.");

            // 4. Scale by -λ (Eq. (A.6))
            var gradientDict = divergence.ToDictionary(
                kvp => kvp.Key,
                kvp => -_lambda * kvp.Value
            );

            return new ConductivityDistribution(gradientDict);
        }
    }
}
