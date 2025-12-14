using Utility.Classes.Discretizer;

namespace Utility.Classes.ReconstructionParameters
{
    public enum ErrorMetric
    {
        L2 = 1,
        Wasserstein2 = 2,
        ConductivityAwareW2 = 3,
        EnergyBasedWasserstein2 = 4,
        UnbalancedWasserstein2 = 5
    }

    /// <summary>
    /// Defines a misfit functional that measures the discrepancy between
    /// measured and simulated data.
    /// </summary>
    public interface IErrorMetric
    {
        /// <summary>
        /// Evaluates the misfit functional, J_misfit.
        /// This corresponds to the first term in your total cost function.
        /// </summary>
        /// <param name="measured">Observed boundary potentials.</param>
        /// <param name="simulated">Simulated boundary potentials from the forward model.</param>
        /// <returns>A scalar value representing the misfit.</returns>
        double Evaluate(IDiscretization discretization, double[] measured, double[] simulated);

        /// <summary>
        /// Evaluates the source term for the adjoint PDE problem.
        /// For L2, this is the residual (simulated - measured).
        /// For W2, this is the Kantorovich potential, φ.
        /// </summary>
        /// <param name="measured">Observed boundary potentials.</param>
        /// <param name="simulated">Simulated boundary potentials from the forward model.</param>
        /// <returns>A vector to be used as the source on the right-hand-side of the adjoint PDE.</returns>
        double[] EvaluateAdjointSource(IDiscretization discretization, double[] measured, double[] simulated);
    }
}