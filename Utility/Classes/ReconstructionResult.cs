namespace Utility.Classes
{
    public class ReconstructionResult
    {
        public Mesh? Mesh { get; private set; }                                 // The mesh we will work during reconstruction
        public ConductivityDistribution OriginalConductivityDistribution;      // The original conductivity distribution which we try to reconstruct
        public ConductivityDistribution InitialConductivitiyDistribution;      // The initial conductivity distribution from which we started the iterations
        public ConductivityDistribution ReconstructedConductivityDistribution; // The reconstructed conductivity distribution of the model
        public List<ReconstructionFrame> Frames { get; private set; }
        #region Constructors
        public ReconstructionResult(Mesh mesh, ConductivityDistribution originalConductivityDistribution, ConductivityDistribution initialConductivitiyDistribution, ConductivityDistribution reconstructedConductivityDistribution, List<ReconstructionFrame> frames)
        {
            Mesh = mesh;
            OriginalConductivityDistribution = originalConductivityDistribution;
            InitialConductivitiyDistribution = initialConductivitiyDistribution;
            ReconstructedConductivityDistribution = reconstructedConductivityDistribution;
            Frames = frames;
        }

        public ReconstructionResult(ConductivityDistribution originalConductivityDistribution, ConductivityDistribution initialConductivitiyDistribution, ConductivityDistribution reconstructedConductivityDistribution, List<ReconstructionFrame> frames)
        {
            Mesh = null;
            OriginalConductivityDistribution = originalConductivityDistribution;
            InitialConductivitiyDistribution = initialConductivitiyDistribution;
            ReconstructedConductivityDistribution = reconstructedConductivityDistribution;
            Frames = frames;
        }
        #endregion
        #region Getters
        // --- Getter Functrions ---
        public Mesh? GetMesh() => Mesh;
        public ConductivityDistribution GetOriginalConductivityDistribution() => OriginalConductivityDistribution;
        public ConductivityDistribution GetInitialConductivityDistribution() => InitialConductivitiyDistribution;
        
        public ConductivityDistribution GetReconstructedConductivityDistribution() => ReconstructedConductivityDistribution;
        #endregion
        #region Setters
        // --- Setter Functions
        public void SetMesh(Mesh mesh) => Mesh = mesh;
        public void SetOriginalConductivityDistribution(ConductivityDistribution originalDistribution) => OriginalConductivityDistribution = originalDistribution;
        public void SetInitialConductivityDistribution(ConductivityDistribution initialDistribution) => InitialConductivitiyDistribution = initialDistribution;
        public void SetReconstructedConductivityDistribution(ConductivityDistribution reconstructedDistribution) => ReconstructedConductivityDistribution = reconstructedDistribution;
        #endregion
    }
}
