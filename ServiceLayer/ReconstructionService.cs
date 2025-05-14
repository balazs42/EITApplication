using BusinessLayer;
using Utility.Classes;
using Utility.Logger;

namespace ServiceLayer
{
    public class ReconstructionService : IReconstructionService
    {
        private readonly IReconstructionPersistence _reconstructionPersistence;
        private readonly ILogger _logger;

        public ReconstructionService(IReconstructionPersistence reconstructionPersistence, ILogger logger)
        {
            _reconstructionPersistence = reconstructionPersistence;
            _logger = logger;
        }

        public async Task<ReconstructionResult> GetReconstructionResult()
        {
            return await _reconstructionPersistence.GetReconstructionResult();
        }

        public void StartReconstruction(EITReconstructionParameters reconstructionParameters)
        {
            _reconstructionPersistence.StartReconstruction(reconstructionParameters);
        }
    }
}
