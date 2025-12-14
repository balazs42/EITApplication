using Utility.Classes.Discretizer;

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
}