using System;

namespace Utility.Classes
{
    public sealed class ReconstructionFrame
    {
        public ConductivityDistribution ConductivityGradient;           // The gradient calculated of the distribution
        public PotentialDistribution CalculatedPotentialDistribution;   // The current calculated potential distribution of the model
        public PotentialDistribution CalculatedAdjointDistribution;     // The current adjoin (\mu) field calculated by the model which is somewhat a potential distribution
        public ConductivityDistribution CalculatedRegularization;
        public double[] MeasuredElectrodeValues { get; }                // Sanitized measurements used for this frame
        public double[] SimulatedElectrodeValues { get; }               // Sanitized simulated values for this frame

        /// <summary>
        /// Optimizer-specific gradients that already account for all connected error metrics
        /// and regularizer contributions. Keyed by the optimizer block identifier.
        /// </summary>
        public IReadOnlyDictionary<string, ConductivityDistribution> OptimizerGradients { get; }

        /// <summary>
        /// Optimizer-specific regularization terms (pre-weighted by the solver-to-regularizer link).
        /// These are kept separate so the caller can apply additional global scaling if required.
        /// </summary>
        public IReadOnlyDictionary<string, ConductivityDistribution> OptimizerRegularizations { get; }

        public ReconstructionFrame(ConductivityDistribution conductivityGradient,
                                   PotentialDistribution calculatedPotentialDistribution,
                                   PotentialDistribution calculatedAdjointDistribution,
                                   ConductivityDistribution calculatedRegularization,
                                   double[]? measuredElectrodeValues = null,
                                   double[]? simulatedElectrodeValues = null,
                                   IReadOnlyDictionary<string, ConductivityDistribution>? optimizerGradients = null,
                                   IReadOnlyDictionary<string, ConductivityDistribution>? optimizerRegularizations = null)
        {
            ConductivityGradient = conductivityGradient;
            CalculatedPotentialDistribution = calculatedPotentialDistribution;
            CalculatedAdjointDistribution = calculatedAdjointDistribution;
            CalculatedRegularization = calculatedRegularization;
            MeasuredElectrodeValues = measuredElectrodeValues ?? Array.Empty<double>();
            SimulatedElectrodeValues = simulatedElectrodeValues ?? Array.Empty<double>();
            OptimizerGradients = optimizerGradients ?? new Dictionary<string, ConductivityDistribution>();
            OptimizerRegularizations = optimizerRegularizations ?? new Dictionary<string, ConductivityDistribution>();
        }
    }
}
