namespace Utility.Classes
{
    public class ReconstructionResult
    {
        public Mesh? Mesh { get; private set; }                                 // The mesh we will work during reconstruction
        public PotentialDistribution CurrentPotentialDistribution;             // The current calculated potential distribution of the model
        public PotentialDistribution CurrentAdjointDistribution;               // The current adjoin (\mu) field calculated by the model which is somewhat a potential distribution
        public ConductivityDistribution OriginalConductivityDistribution;      // The original conductivity distribution which we try to reconstruct
        public ConductivityDistribution InitialConductivitiyDistribution;      // The initial conductivity distribution from which we started the iterations
        public ConductivityDistribution ReconstructedConductivityDistribution; // The reconstructed conductivity distribution of the model
        public List<ReconstructionFrame> Frames { get; private set; }
        #region Constructors
        public ReconstructionResult(Mesh mesh, PotentialDistribution currentPotentialDistribution, PotentialDistribution currentAdjointDistribution, ConductivityDistribution originalConductivityDistribution, ConductivityDistribution initialConductivitiyDistribution, ConductivityDistribution reconstructedConductivityDistribution)
        {
            Mesh = mesh;
            CurrentPotentialDistribution = currentPotentialDistribution;
            CurrentAdjointDistribution = currentAdjointDistribution;
            OriginalConductivityDistribution = originalConductivityDistribution;
            InitialConductivitiyDistribution = initialConductivitiyDistribution;
            ReconstructedConductivityDistribution = reconstructedConductivityDistribution;
        }

        public ReconstructionResult(PotentialDistribution currentPotentialDistribution, PotentialDistribution currentAdjointDistribution, ConductivityDistribution originalConductivityDistribution, ConductivityDistribution initialConductivitiyDistribution, ConductivityDistribution reconstructedConductivityDistribution)
        {
            Mesh = null;
            CurrentPotentialDistribution = currentPotentialDistribution;
            CurrentAdjointDistribution = currentAdjointDistribution;
            OriginalConductivityDistribution = originalConductivityDistribution;
            InitialConductivitiyDistribution = initialConductivitiyDistribution;
            ReconstructedConductivityDistribution = reconstructedConductivityDistribution;
        }
        #endregion
        #region Getters
        // --- Getter Functrions ---
        public Mesh? GetMesh() => Mesh;
        public PotentialDistribution GetCurrentPotentialDistribution() => CurrentAdjointDistribution;
        public PotentialDistribution GetCurrentAdjointDistribution() => CurrentAdjointDistribution;
        public ConductivityDistribution GetOriginalConductivityDistribution() => OriginalConductivityDistribution;
        public ConductivityDistribution GetInitialConductivityDistribution() => InitialConductivitiyDistribution;
        
        public ConductivityDistribution GetReconstructedConductivityDistribution() => ReconstructedConductivityDistribution;
        #endregion
        #region Setters
        // --- Setter Functions
        public void SetMesh(Mesh mesh) => Mesh = mesh;
        public void SetCurrentPotentialDistribution(PotentialDistribution potentialDistribution) => CurrentPotentialDistribution = potentialDistribution;
        public void SetCurrentAdjointDistribution(PotentialDistribution adjointDistribution) => CurrentAdjointDistribution = adjointDistribution;
        public void SetOriginalConductivityDistribution(ConductivityDistribution originalDistribution) => OriginalConductivityDistribution = originalDistribution;
        public void SetInitialConductivityDistribution(ConductivityDistribution initialDistribution) => InitialConductivitiyDistribution = initialDistribution;
        public void SetReconstructedConductivityDistribution(ConductivityDistribution reconstructedDistribution) => ReconstructedConductivityDistribution = reconstructedDistribution;
        #endregion
    }
}
