using BusinessLayer;
using Utility.Classes.Measurement;
using Utility.Logger;

namespace ServiceLayer
{
    public class DAQService : IDAQService
    {
        private readonly IDAQPersistence _daqPersistence;
        private readonly ILogger _logger;

        public DAQService(IDAQPersistence daqPersistence, ILogger logger)
        {
            _daqPersistence = daqPersistence;
            _logger = logger;
        }

        public EITMeasurements GetEITMeasurement()
        {
            try
            {
                return _daqPersistence.GetEITMeasurement();
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
