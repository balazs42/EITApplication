using DataAccessLayer;
using Utility.Classes.Measurement;

namespace BusinessLayer
{
    public class DAQPersistence : IDAQPersistence
    {
        private readonly IDAQRepository _daqRepository;

        public DAQPersistence(IDAQRepository daqRepository) 
        { 
            _daqRepository = daqRepository;
        }

        public EITMeasurements GetEITMeasurement()
        {
            return _daqRepository.GetEITMeasurement();
        }
    }
}
