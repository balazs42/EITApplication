namespace Utility.Classes
{
    public class ReconstructionResult
    {
        public Mesh Mesh { get; private set; }                                              // The mesh we will work during reconstruction
        public PotentialDistribution CurrentPotentialDistribution { get; private set; }     // The current calculated potential distribution of the model
        public PotentialDistribution CurrentAdjointDistribution { get; private set; }       // The current adjoin (\mu) field calculated by the model which is somewhat a potential distribution
        public ConductivityDistribution OriginalConductivityDistribution { get; set; }      // The original conductivity distribution which we try to reconstruct
        public ConductivityDistribution InitialConductivitiyDistribution { get; set; }      // The initial conductivity distribution from which we started the iterations
        public ConductivityDistribution ReconstructedConductivityDistribution { get; set; } // The reconstructed conductivity distribution of the model

        public ReconstructionResult(Mesh mesh, PotentialDistribution currentPotentialDistribution, PotentialDistribution currentAdjointDistribution, ConductivityDistribution originalConductivityDistribution, ConductivityDistribution initialConductivitiyDistribution, ConductivityDistribution reconstructedConductivityDistribution)
        {
            Mesh = mesh;
            CurrentPotentialDistribution = currentPotentialDistribution;
            CurrentAdjointDistribution = currentAdjointDistribution;
            OriginalConductivityDistribution = originalConductivityDistribution;
            InitialConductivitiyDistribution = initialConductivitiyDistribution;
            ReconstructedConductivityDistribution = reconstructedConductivityDistribution;
        }
    }
}
