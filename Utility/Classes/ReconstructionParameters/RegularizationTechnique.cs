using System.Linq;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
using Utility.Classes.Solvers;
using Utility.Classes.Solvers.FiniteElementSolver;
using Utility.Classes.Solvers.LatticeBoltzmannSolver;

namespace Utility.Classes.ReconstructionParameters
{
    public enum RegularizationTechnique
    {
        None = 0,
        ZeroOrderTikhonov = 1,
        FirstOrderTikhonov = 2,
        TotalVariation = 3,
        Laplace = 4
    };

    /// <summary>
    /// Defines a regularization functional used to penalize non-physical solutions.
    /// </summary>
    public interface IRegularizer
    {
        /// <summary>
        /// Evaluates the regularization penalty term, J_regularization.
        /// </summary>
        /// <param name="discretization">The mesh on which the conductivity is defined.</param>
        /// <param name="sigma">The current conductivity distribution.</param>
        /// <returns>A scalar penalty value.</returns>
        double EvaluateTerm(IDiscretization discretization, ConductivityDistribution sigma);

        /// <summary>
        /// Evaluates the gradient of the regularization term with respect to conductivity.
        /// This is the second component of the total gradient used by the optimizer.
        /// </summary>
        /// <param name="discretization">The mesh on which the conductivity is defined.</param>
        /// <param name="sigma">The current conductivity distribution.</param>
        /// <returns>A new distribution representing the gradient of the regularization term.</returns>
        ConductivityDistribution EvaluateGradient(IDiscretization discretization, ConductivityDistribution sigma);
    }

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

    /// <summary>
    /// Implements Laplacian regularization. J = (λ/2) * ||Δσ||^2.
    /// </summary>
    public sealed class LaplaceRegularizer : IRegularizer
    {
        private readonly double _lambda;
        public LaplaceRegularizer(double lambda = 1e-6) => _lambda = lambda;

        public double EvaluateTerm(IDiscretization discretization, ConductivityDistribution sigma)
        {
            ScalarField laplacian;
            if (discretization is FEMMesh femMesh)
                laplacian = FiniteElementOperators.CalculateLaplacian(femMesh, sigma.ToPotentialDistribution());
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
            if (discretization is FEMMesh femMesh)
            {
                var laplacian1 = FiniteElementOperators.CalculateLaplacian(femMesh, sigma.ToPotentialDistribution());
                var laplacian2 = FiniteElementOperators.CalculateLaplacian(femMesh, laplacian1); // Δ(Δγ)
                var projected = FiniteElementOperators.ProjectVertexFieldToElements(femMesh, laplacian2);
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