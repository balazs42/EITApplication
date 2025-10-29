using System;
using Utility.Classes.Discretizer;

namespace Utility.Classes
{
    public sealed class ReconstructionFrame
    {
        public static Discretization? Mesh { get; private set; }
        public ConductivityDistribution ConductivityGradient;           // The gradient calculated of the distribution
        public PotentialDistribution CalculatedPotentialDistribution;   // The current calculated potential distribution of the model
        public PotentialDistribution CalculatedAdjointDistribution;     // The current adjoin (\mu) field calculated by the model which is somewhat a potential distribution
        public ConductivityDistribution CalculatedRegularization;
        public double[] MeasuredElectrodeValues { get; }                // Sanitized measurements used for this frame
        public double[] SimulatedElectrodeValues { get; }               // Sanitized simulated values for this frame

        public ReconstructionFrame(ConductivityDistribution conductivityGradient,
                                   PotentialDistribution calculatedPotentialDistribution,
                                   PotentialDistribution calculatedAdjointDistribution,
                                   ConductivityDistribution calculatedRegularization,
                                   double[]? measuredElectrodeValues = null,
                                   double[]? simulatedElectrodeValues = null)
        {
            ConductivityGradient = conductivityGradient;
            CalculatedPotentialDistribution = calculatedPotentialDistribution;
            CalculatedAdjointDistribution = calculatedAdjointDistribution;
            CalculatedRegularization = calculatedRegularization;
            MeasuredElectrodeValues = measuredElectrodeValues ?? Array.Empty<double>();
            SimulatedElectrodeValues = simulatedElectrodeValues ?? Array.Empty<double>();
        }
    }
}
