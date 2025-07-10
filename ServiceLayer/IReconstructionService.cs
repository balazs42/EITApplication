using Utility.Classes;
using Utility.Classes.Meshing;
using Utility.Classes.ReconstructionParameters;

namespace ServiceLayer
{
    public interface IReconstructionService
    {
        public Task<ReconstructionResult> GetReconstructionResult();
        public void InitializeReconstruction(IMesh mesh, EITReconstructionParameters parameters);
        public FEMMesh SolveFemForward(FEMMesh mesh);
        public FEMMesh SolveFemInverse(FEMMesh mesh, int maxIterCount, double stepSize);
    }
}
