using Utility.Classes.Discretizer;
using Utility.Classes.ReconstructionParameters;

namespace Utility.Classes.Reconstruction.ErrorMetrics
{

    /// <summary>
    /// Implements the standard L2-norm squared misfit. J = 1/2 * ||d_sim - d_obs||^2.
    /// </summary>
    public sealed class L2ErrorMetric : IErrorMetric
    {
        public double Evaluate(IDiscretization discretization, double[] measured, double[] simulated)
        {
            if (measured.Length != simulated.Length)
                throw new ArgumentException("Measured and simulated vectors must have the same length.");

            double sumOfSquares = 0.0;
            for (int i = 0; i < measured.Length; i++)
            {
                // If either value is NaN, this point doesn't contribute to the error.
                if (double.IsNaN(measured[i]) || double.IsNaN(simulated[i]))
                    continue;

                double residual = measured[i] - simulated[i];
                sumOfSquares += residual * residual;
            }
            return 0.5 * sumOfSquares;
        }

        public double[] EvaluateAdjointSource(IDiscretization discretization, double[] measured, double[] simulated)
        {
            if (measured.Length != simulated.Length)
                throw new ArgumentException("Measured and simulated vectors must have the same length.");

            double[] residual = new double[measured.Length];
            for (int i = 0; i < measured.Length; i++)
            {
                // If a value is NaN, the residual (the source for the adjoint) should be zero.
                if (double.IsNaN(measured[i]) || double.IsNaN(simulated[i]))
                    residual[i] = 0.0;
                // adjoint PDE is ∇·(γ∇μ) = - S^T (Sϕ - d_obs),
                // so the boundary‐current we feed into our forward‐solver adjoint is
                //    Iℓ = - (ϕℓ - d_obs,ℓ) = d_obs,ℓ – ϕℓ
                else
                    residual[i] = simulated[i] - measured[i];
            }
            return residual;
        }
    }
}
