using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.ReconstructionParameters;
using Utility.Classes.Solvers;
using Utility.Classes.Solvers.FiniteElementSolver;
using Utility.Classes.Solvers.LatticeBoltzmannSolver;
using Workspace = Utility.Classes.Application.Workspace;

namespace Utility.Classes.Reconstruction.Regulizers
{
    /// <summary>
    /// Implements Laplacian regularization. J = (λ/2) * ||Δσ||^2.
    /// </summary>
    public sealed class LaplaceRegularizer : IRegularizer
    {
        private readonly double _lambda;
        public LaplaceRegularizer(double lambda = 1e-6) => _lambda = lambda;

        public double EvaluateTerm(IDiscretization discretization, ConductivityDistribution sigma)
        {
            bool useParallelFem = Workspace.GetReconstructionParameters()?.UseOmpParallelization == true;
            ScalarField laplacian;
            if (discretization is FEMMesh femMesh)
                laplacian = FiniteElementOperators.CalculateLaplacian(femMesh, sigma.ToPotentialDistribution(), useParallelFem);
            else if (discretization is LBMGrid lbmGrid)
                laplacian = LatticeBoltzmannOperators.CalculateLaplacian(lbmGrid, sigma.ToPotentialDistribution());
            else
                throw new NotSupportedException($"Mesh type {discretization.GetType().Name} not supported.");

            // Calculate L2-norm squared of the Laplacian field (Eq. (A.5))
            double normSq = laplacian.Get().Values.Sum(v => v * v);
            return 0.5 * _lambda * normSq;
        }

        public ConductivityDistribution EvaluateGradient(IDiscretization discretization, ConductivityDistribution sigma)
        {
            // Gradient is λ * Δ^2 σ (bi-Laplacian).
            // This is achieved by applying the Laplacian operator twice.
            bool useParallelFem = Workspace.GetReconstructionParameters()?.UseOmpParallelization == true;
            if (discretization is FEMMesh femMesh)
            {
                var laplacian1 = FiniteElementOperators.CalculateLaplacian(femMesh, sigma.ToPotentialDistribution(), useParallelFem);
                var laplacian2 = FiniteElementOperators.CalculateLaplacian(femMesh, laplacian1, useParallelFem); // Δ(Δγ)
                var projected = FiniteElementOperators.ProjectVertexFieldToElements(femMesh, laplacian2, useParallelFem);
                var gradientDict = projected.ToDictionary(
                    kvp => kvp.Key,
                    kvp => _lambda * kvp.Value);
                return new ConductivityDistribution(gradientDict);
            }
            if (discretization is LBMGrid lbmGrid)
            {
                var laplacian1 = LatticeBoltzmannOperators.CalculateLaplacian(lbmGrid, sigma.ToPotentialDistribution());
                var laplacian2 = LatticeBoltzmannOperators.CalculateLaplacian(lbmGrid, laplacian1);
                var gradientDict = laplacian2.Get().ToDictionary(
                    kvp => kvp.Key,
                    kvp => _lambda * kvp.Value);
                return new ConductivityDistribution(gradientDict);
            }
            throw new NotSupportedException($"Mesh type {discretization.GetType().Name} not supported.");
        }
    }
}
