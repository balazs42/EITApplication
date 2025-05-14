using Utility.Classes;

namespace BusinessLayer
{
    public class ReconstructionPersistence : IReconstructionPersistence
    {
        public async Task<ReconstructionResult> GetReconstructionResult()
        {
            await Task.Delay (1000);

            return new ReconstructionResult();
        }

        public void StartReconstruction(EITReconstructionParameters reconstructionParameters)
        {
            // TODO: reconstruction
        }
    }
}
