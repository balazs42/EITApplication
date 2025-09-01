using System;
using System.Collections.Generic;
using System.Numerics;
using BusinessLayer;
using System.Diagnostics;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;
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

        public void SaveFEMMesh(FEMMesh mesh, string name)
        {
            try
            {
                _daqPersistence.SaveFEMMesh(mesh, name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public void SaveLBMMesh(LBMMesh mesh, string name)
        {
            try
            {
                _daqPersistence.SaveLBMMesh(mesh, name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public FEMMesh LoadFEMMesh(string filePath)
        {
            try
            {
                return _daqPersistence.LoadFEMMesh(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public LBMMesh LoadLBMMesh(string filePath)
        {
            try
            {
                return _daqPersistence.LoadLBMMesh(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public IEnumerable<MeshInfo> GetMeshes()
        {
            try
            {
                return _daqPersistence.GetMeshes();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public bool ConnectHardware()
        {
            try
            {
                return _daqPersistence.ConnectHardware();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                return false;
            }
        }

        public bool DisconnectHardware()
        {
            try
            {
                return _daqPersistence.DisconnectHardware();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                return false;
            }
        }
    }
}

