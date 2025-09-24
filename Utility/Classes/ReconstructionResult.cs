namespace Utility.Classes
{
    public class ReconstructionResult
    {
        public Discretization? Discretization { get; private set; }            // The discretization we will work during reconstruction
        public ConductivityDistribution OriginalConductivityDistribution;      // The original conductivity distribution which we try to reconstruct
        public ConductivityDistribution InitialConductivitiyDistribution;      // The initial conductivity distribution from which we started the iterations
        public ConductivityDistribution ReconstructedConductivityDistribution; // The reconstructed conductivity distribution of the model
        public List<ReconstructionFrame> Frames { get; private set; }
        #region Constructors
        public ReconstructionResult(Discretization discretization, ConductivityDistribution originalConductivityDistribution, ConductivityDistribution initialConductivitiyDistribution, ConductivityDistribution reconstructedConductivityDistribution, List<ReconstructionFrame> frames)
        {
            Discretization = discretization;
            OriginalConductivityDistribution = originalConductivityDistribution;
            InitialConductivitiyDistribution = initialConductivitiyDistribution;
            ReconstructedConductivityDistribution = reconstructedConductivityDistribution;
            Frames = frames;
        }

        public ReconstructionResult(ConductivityDistribution originalConductivityDistribution, ConductivityDistribution initialConductivitiyDistribution, ConductivityDistribution reconstructedConductivityDistribution, List<ReconstructionFrame> frames)
        {
            Discretization = null;
            OriginalConductivityDistribution = originalConductivityDistribution;
            InitialConductivitiyDistribution = initialConductivitiyDistribution;
            ReconstructedConductivityDistribution = reconstructedConductivityDistribution;
            Frames = frames;
        }
        #endregion
        #region Getters
        // --- Getter Functrions ---
        public Discretization? GetDiscretization() => Discretization;
        public ConductivityDistribution GetOriginalConductivityDistribution() => OriginalConductivityDistribution;
        public ConductivityDistribution GetInitialConductivityDistribution() => InitialConductivitiyDistribution;
        
        public ConductivityDistribution GetReconstructedConductivityDistribution() => ReconstructedConductivityDistribution;
        #endregion
        #region Setters
        // --- Setter Functions
        public void SetMesh(Discretization discretization) => Discretization = discretization;
        public void SetOriginalConductivityDistribution(ConductivityDistribution originalDistribution) => OriginalConductivityDistribution = originalDistribution;
        public void SetInitialConductivityDistribution(ConductivityDistribution initialDistribution) => InitialConductivitiyDistribution = initialDistribution;
        public void SetReconstructedConductivityDistribution(ConductivityDistribution reconstructedDistribution) => ReconstructedConductivityDistribution = reconstructedDistribution;
        #endregion
    }
}
