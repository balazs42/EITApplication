using Utility.Classes;
using Utility.Classes.ReconstructionParameters;

namespace ServiceLayer
{
    public interface IReconstructionService
    {
        public Task<ReconstructionResult> GetReconstructionResult();
        public void StartReconstruction(EITReconstructionParameters reconstructionParameters);
    }
}
