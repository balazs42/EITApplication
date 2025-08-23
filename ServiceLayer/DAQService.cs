using System;
using System.Numerics;
using BusinessLayer;
using System.Diagnostics;
using Utility.Classes;
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

        public void SaveEITMeasurement(EITMeasurement measurement, string name)
        {
            try
            {
                _daqPersistence.SaveEITMeasurement(measurement, name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public EITMeasurement LoadEITMeasurement(string name, DateTime savedAt)
        {
            try
            {
                return _daqPersistence.LoadEITMeasurement(name, savedAt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public void DeleteEITMeasurement(string name, DateTime savedAt)
        {
            try
            {
                _daqPersistence.DeleteEITMeasurement(name, savedAt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public void SaveMesh(IMesh mesh, string name)
        {
            try
            {
                _daqPersistence.SaveMesh(mesh, name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public IMesh LoadMesh(string filePath)
        {
            try
            {
                return _daqPersistence.LoadMesh(filePath);
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

