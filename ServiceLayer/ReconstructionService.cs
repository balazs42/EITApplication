using BusinessLayer;
using Utility.Classes;
using Utility.Classes.ReconstructionParameters;
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
            try
            {
                return await _reconstructionPersistence.GetReconstructionResult();
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public void StartReconstruction(EITReconstructionParameters reconstructionParameters)
        {
            try
            {
                _reconstructionPersistence.StartReconstruction(reconstructionParameters);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }
    }
}
