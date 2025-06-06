using Utility.Classes.Meshing;
using Utility.Classes.Solvers;

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
        /// <param name="mesh">The mesh on which the conductivity is defined.</param>
        /// <param name="sigma">The current conductivity distribution.</param>
        /// <returns>A scalar penalty value.</returns>
        double EvaluateTerm(IMesh mesh, ConductivityDistribution sigma);

        /// <summary>
        /// Evaluates the gradient of the regularization term with respect to conductivity.
        /// This is the second component of the total gradient used by the optimizer.
        /// </summary>
        /// <param name="mesh">The mesh on which the conductivity is defined.</param>
        /// <param name="sigma">The current conductivity distribution.</param>
        /// <returns>A new distribution representing the gradient of the regularization term.</returns>
        ConductivityDistribution EvaluateGradient(IMesh mesh, ConductivityDistribution sigma);
    }

    /// <summary>
    /// Provides no regularization.
    /// </summary>
    public sealed class NoRegularizer : IRegularizer
    {
        public double EvaluateTerm(IMesh mesh, ConductivityDistribution sigma) => 0.0;

        public ConductivityDistribution EvaluateGradient(IMesh mesh, ConductivityDistribution sigma)
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

        public double EvaluateTerm(IMesh mesh, ConductivityDistribution sigma)
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

        public ConductivityDistribution EvaluateGradient(IMesh mesh, ConductivityDistribution sigma)
        {
            // Gradient is λ * (σ - σ_prior).
            var gradientDict = new Dictionary<int, double>();
            foreach (var kvp in sigma.Conductivities)
            {
                gradientDict[kvp.Key] = _lambda * (kvp.Value - _sigmaPrior.GetConductivity(kvp.Key));
            }
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

        public double EvaluateTerm(IMesh mesh, ConductivityDistribution sigma)
        {
            VectorField gradientField;
            if (mesh is FEMMesh femMesh)
            {
                // For FEM, gradient is constant per element.
                gradientField = FiniteElementOperators.CalculateElementWiseGradient(femMesh, sigma.ToPotentialDistribution());
                double integral = 0;
                foreach (var element in femMesh.Elements)
                {
                    var grad = gradientField.GetVector(element.Id);
                    integral += (grad.X * grad.X + grad.Y * grad.Y) * element.Area;
                }
                return 0.5 * _lambda * integral;
            }
            if (mesh is LBMMesh lbmMesh)
            {
                // For LBM, gradient is per-node.
                gradientField = LatticeBoltzmannOperators.CalculateGradient(lbmMesh, sigma.ToPotentialDistribution());
                double sum = gradientField.Data.Values.Sum(grad => grad.X * grad.X + grad.Y * grad.Y);
                // Assuming grid spacing h=1, so area per node is 1.
                return 0.5 * _lambda * sum;
            }
            throw new NotSupportedException($"Mesh type {mesh.GetType().Name} not supported.");
        }

        public ConductivityDistribution EvaluateGradient(IMesh mesh, ConductivityDistribution sigma)
        {
            // Gradient is -λ * Δσ.
            PotentialDistribution laplacian;
            if (mesh is FEMMesh femMesh)
            {
                laplacian = FiniteElementOperators.CalculateLaplacian(femMesh, sigma.ToPotentialDistribution());
            }
            else if (mesh is LBMMesh lbmMesh)
            {
                laplacian = LatticeBoltzmannOperators.CalculateLaplacian(lbmMesh, sigma.ToPotentialDistribution());
            }
            else
            {
                throw new NotSupportedException($"Mesh type {mesh.GetType().Name} not supported for First-Order Tikhonov regularization.");
            }

            var gradientDict = laplacian.Potentials.ToDictionary(
                kvp => kvp.Key,
                kvp => -_lambda * kvp.Value
            );
            return new ConductivityDistribution(gradientDict);
        }
    }

    /// <summary>
    /// Implements Laplacian regularization. J = (λ/2) * ||Δσ||^2.
    /// </summary>
    public sealed class LaplaceRegularizer : IRegularizer
    {
        private readonly double _lambda;
        public LaplaceRegularizer(double lambda = 1e-6) => _lambda = lambda;

        public double EvaluateTerm(IMesh mesh, ConductivityDistribution sigma)
        {
            PotentialDistribution laplacian;
            if (mesh is FEMMesh femMesh)
                laplacian = FiniteElementOperators.CalculateLaplacian(femMesh, sigma.ToPotentialDistribution());
            else if (mesh is LBMMesh lbmMesh)
                laplacian = LatticeBoltzmannOperators.CalculateLaplacian(lbmMesh, sigma.ToPotentialDistribution());
            else
                throw new NotSupportedException($"Mesh type {mesh.GetType().Name} not supported.");

            // Calculate L2-norm squared of the Laplacian field
            double normSq = laplacian.Potentials.Values.Sum(v => v * v);
            return 0.5 * _lambda * normSq;
        }

        public ConductivityDistribution EvaluateGradient(IMesh mesh, ConductivityDistribution sigma)
        {
            // Gradient is λ * Δ^2 σ (bi-Laplacian).
            // This is achieved by applying the Laplacian operator twice.
            PotentialDistribution laplacian1, laplacian2;
            if (mesh is FEMMesh femMesh)
            {
                laplacian1 = FiniteElementOperators.CalculateLaplacian(femMesh, sigma.ToPotentialDistribution());
                laplacian2 = FiniteElementOperators.CalculateLaplacian(femMesh, laplacian1); // Apply again
            }
            else if (mesh is LBMMesh lbmMesh)
            {
                laplacian1 = LatticeBoltzmannOperators.CalculateLaplacian(lbmMesh, sigma.ToPotentialDistribution());
                laplacian2 = LatticeBoltzmannOperators.CalculateLaplacian(lbmMesh, laplacian1); // Apply again
            }
            else
            {
                throw new NotSupportedException($"Mesh type {mesh.GetType().Name} not supported.");
            }

            var gradientDict = laplacian2.Potentials.ToDictionary(
                 kvp => kvp.Key,
                 kvp => _lambda * kvp.Value
             );
            return new ConductivityDistribution(gradientDict);
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

        public double EvaluateTerm(IMesh mesh, ConductivityDistribution sigma)
        {
            VectorField gradientField;
            if (mesh is FEMMesh femMesh)
            {
                gradientField = FiniteElementOperators.CalculateElementWiseGradient(femMesh, sigma.ToPotentialDistribution());
                double integral = 0;
                foreach (var element in femMesh.Elements)
                {
                    var grad = gradientField.GetVector(element.Id);
                    integral += Math.Sqrt(grad.X * grad.X + grad.Y * grad.Y) * element.Area;
                }
                return _lambda * integral;
            }
            if (mesh is LBMMesh lbmMesh)
            {
                gradientField = LatticeBoltzmannOperators.CalculateGradient(lbmMesh, sigma.ToPotentialDistribution());
                double sum = gradientField.Data.Values.Sum(grad => Math.Sqrt(grad.X * grad.X + grad.Y * grad.Y));
                return _lambda * sum;
            }
            throw new NotSupportedException($"Mesh type {mesh.GetType().Name} not supported.");
        }

        public ConductivityDistribution EvaluateGradient(IMesh mesh, ConductivityDistribution sigma)
        {
            // Gradient is -λ * ∇·(∇σ / (||∇σ|| + ε)).

            // 1. Calculate the gradient field: ∇σ
            VectorField gradSigma;
            if (mesh is FEMMesh femMesh) // Note: Gradient is on elements, Divergence is on nodes. This needs careful handling.
                gradSigma = FiniteElementOperators.CalculateElementWiseGradient(femMesh, sigma.ToPotentialDistribution());
            else if (mesh is LBMMesh)
                gradSigma = LatticeBoltzmannOperators.CalculateGradient(mesh as LBMMesh, sigma.ToPotentialDistribution());
            else
                throw new NotSupportedException($"Mesh type {mesh.GetType().Name} not supported.");

            // 2. Normalize the gradient field: ∇σ / (||∇σ|| + ε)
            var normalizedGradData = gradSigma.Data.ToDictionary(
                kvp => kvp.Key,
                kvp =>
                {
                    double mag = Math.Sqrt(kvp.Value.X * kvp.Value.X + kvp.Value.Y * kvp.Value.Y);
                    double divisor = mag + Epsilon;
                    return (kvp.Value.X / divisor, kvp.Value.Y / divisor);
                }
            );
            var normalizedGradField = new VectorField(normalizedGradData);

            // 3. Calculate the divergence of the normalized field: ∇·(...)
            PotentialDistribution divergence;
            if (mesh is LBMMesh)
            {
                divergence = LatticeBoltzmannOperators.CalculateDivergence(mesh as LBMMesh, normalizedGradField);
            }
            else if (mesh is FEMMesh)
            {
                // NOTE: Implementing divergence on an FEM mesh is complex.
                // It requires integrating over element patches, similar to the Laplacian.
                // This is a placeholder for that advanced implementation.
                throw new NotImplementedException("Divergence for FEM meshes is not yet implemented in the operators helper.");
            }
            else
            {
                throw new NotSupportedException($"Mesh type {mesh.GetType().Name} not supported.");
            }

            // 4. Scale by -λ
            var gradientDict = divergence.Potentials.ToDictionary(
                kvp => kvp.Key,
                kvp => -_lambda * kvp.Value
            );

            return new ConductivityDistribution(gradientDict);
        }
    }
}
