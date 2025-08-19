using System.Numerics;
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

        public EITMeasurement GetEITMeasurement()
        {
            return _daqRepository.GetEITMeasurement();
        }

        public Complex[][] ComputeFourierTransform(EITMeasurement measurement)
        {
            return _daqRepository.ComputeFourierTransform(measurement);
        }

        public Complex[][] ComputeDFT(EITMeasurement measurement)
        {
            return _daqRepository.ComputeDFT(measurement);
        }

        public double[][] ComputeDCT(EITMeasurement measurement)
        {
            return _daqRepository.ComputeDCT(measurement);
        }

        public Complex[][] ComputeFFT(EITMeasurement measurement)
        {
            return _daqRepository.ComputeFFT(measurement);
        }
    }
}

