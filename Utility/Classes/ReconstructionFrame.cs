namespace Utility.Classes
{
    public sealed class ReconstructionFrame
    {
        public static Discretization? Mesh { get; private set; }
        public ConductivityDistribution ConductivityGradient;           // The gradient calculated of the distribution
        public PotentialDistribution CalculatedPotentialDistribution;   // The current calculated potential distribution of the model
        public PotentialDistribution CalculatedAdjointDistribution;     // The current adjoin (\mu) field calculated by the model which is somewhat a potential distribution
        public ConductivityDistribution CalculatedRegularization;

        public ReconstructionFrame(ConductivityDistribution conductivityGradient, 
                                   PotentialDistribution calculatedPotentialDistribution,
                                   PotentialDistribution calculatedAdjointDistribution,
                                   ConductivityDistribution calculatedRegularization)
        {
            ConductivityGradient = conductivityGradient;
            CalculatedPotentialDistribution = calculatedPotentialDistribution;
            CalculatedAdjointDistribution = calculatedAdjointDistribution;
            CalculatedRegularization = calculatedRegularization;
        }
    }
}
