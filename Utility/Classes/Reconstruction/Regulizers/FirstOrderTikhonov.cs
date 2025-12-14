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
    /// Implements First-Order Tikhonov regularization. J = (λ/2) * ||∇σ||^2.
    /// </summary>
    public sealed class FirstOrderTikhonov : IRegularizer
    {
        private readonly double _lambda;
        public FirstOrderTikhonov(double lambda = 1e-4) => _lambda = lambda;

        public double EvaluateTerm(IDiscretization discretization, ConductivityDistribution sigma)
        {
            VectorField gradientField;
            if (discretization is FEMMesh femMesh)
            {
                // For FEM, gradient is constant per element.
                gradientField = FiniteElementOperators.CalculateElementWiseGradient(femMesh, sigma.ToPotentialDistribution());
                double integral = 0;
                var elements = femMesh.GetElements().Cast<FEMElement>();

                foreach (var element in elements)
                {
                    var grad = gradientField.GetVector(element.Id);
                    integral += (grad.X * grad.X + grad.Y * grad.Y) * element.Area;
                }
                return 0.5 * _lambda * integral;
            }
            if (discretization is LBMGrid lbmGrid)
            {
                // For LBM, gradient is per-node.
                gradientField = LatticeBoltzmannOperators.CalculateGradient(lbmGrid, sigma.ToPotentialDistribution());
                double sum = gradientField.Data.Values.Sum(grad => grad.X * grad.X + grad.Y * grad.Y);
                // Assuming grid spacing h=1, so area per node is 1.
                return 0.5 * _lambda * sum;
            }
            throw new NotSupportedException($"Mesh type {discretization.GetType().Name} not supported.");
        }

        public ConductivityDistribution EvaluateGradient(IDiscretization discretization, ConductivityDistribution sigma)
        {
            // Gradient is -λ * Δσ.
            if (discretization is FEMMesh femMesh)
            {
                var laplacian = FiniteElementOperators.CalculateLaplacian(femMesh, sigma.ToPotentialDistribution());
                var projected = FiniteElementOperators.ProjectVertexFieldToElements(femMesh, laplacian);
                var gradientDict = projected.ToDictionary(
                    kvp => kvp.Key,
                    kvp => -_lambda * kvp.Value);
                return new ConductivityDistribution(gradientDict);
            }
            if (discretization is LBMGrid lbmGrid)
            {
                var laplacian = LatticeBoltzmannOperators.CalculateLaplacian(lbmGrid, sigma.ToPotentialDistribution());
                var gradientDict = laplacian.Get().ToDictionary(
                    kvp => kvp.Key,
                    kvp => -_lambda * kvp.Value);
                return new ConductivityDistribution(gradientDict);
            }
            throw new NotSupportedException($"Mesh type {discretization.GetType().Name} not supported for First-Order Tikhonov regularization.");
        }
    }
}
