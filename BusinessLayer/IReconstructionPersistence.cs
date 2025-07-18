using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.ReconstructionParameters;

namespace BusinessLayer
{
    public interface IReconstructionPersistence
    {
        public Task<ReconstructionResult> GetReconstructionResult();
        public void InitializeReconstruction(IMesh mesh, EITReconstructionParameters parameters);
        public PotentialDistribution LBMSolveForward();
        public ReconstructionResult LBMSolveInverse();
        public FEMMesh SolveFemForward(FEMMesh mesh);
        public FEMMesh SolveFemInverse(FEMMesh mesh, int maxIterCount, double stepSize, double regularization);
        public ReconstructionResult InverseSolveStepFem(FEMMesh mesh, double[] measurement, BoundaryCondition boundaryCondition, double stepSize);
        public List<double[]> SimulateFemMeasurements(FEMMesh mesh, double excitationAmplitude);
    }
}
