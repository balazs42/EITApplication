using DataAccessLayer;
using Utility.Classes;

namespace BusinessLayer
{
    public class DAQPersistence : IDAQPersistence
    {
        private readonly IDAQRepository _daqRepository;

        public DAQPersistence(IDAQRepository daqRepository) 
        { 
            _daqRepository = daqRepository;
        }

        public EITMeasurement GetEITMeasurement()
        {
            return _daqRepository.GetEITMeasurement();
        }
    }
}
