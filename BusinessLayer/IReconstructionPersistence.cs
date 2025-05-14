using Utility.Classes;

namespace BusinessLayer
{
    public interface IReconstructionPersistence
    {
        public Task<ReconstructionResult> GetReconstructionResult();
        public void StartReconstruction();
    }
}
