using System;
using System.Numerics;
using BusinessLayer;
using System.Diagnostics;
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

        public EITMeasurement GetEITMeasurement()
        {
            try
            {
                return _daqPersistence.GetEITMeasurement();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public Complex[][] ComputeFourierTransform(EITMeasurement measurement)
        {
            try
            {
                return _daqPersistence.ComputeFourierTransform(measurement);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public Complex[][] ComputeDFT(EITMeasurement measurement)
        {
            try
            {
                return _daqPersistence.ComputeDFT(measurement);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public double[][] ComputeDCT(EITMeasurement measurement)
        {
            try
            {
                return _daqPersistence.ComputeDCT(measurement);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public Complex[][] ComputeFFT(EITMeasurement measurement)
        {
            try
            {
                return _daqPersistence.ComputeFFT(measurement);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }
    }
}

