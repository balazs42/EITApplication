using System.Numerics;
using BusinessLayer;
using System.Diagnostics;
using Utility.Classes;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;
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

        public void SaveLBMGrid(LBMGrid grid, string name)
        {
            try
            {
                _daqPersistence.SaveLBMGrid(grid, name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public MatlabExportResult ExportFemMeshForMatlab(FEMMesh mesh, string name, DrivePattern drivePattern)
        {
            try
            {
                return _daqPersistence.ExportFemMeshForMatlab(mesh, name, drivePattern);
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

        public LBMGrid LoadLBMGrid(string filePath)
        {
            try
            {
                return _daqPersistence.LoadLBMGrid(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public IEnumerable<DiscretizationInfo> GetDiscretizationInfos()
        {
            try
            {
                return _daqPersistence.GetDiscretizationInfos();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public void DeleteMesh(string filePath)
        {
            try
            {
                _daqPersistence.DeleteMesh(filePath);
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

        public bool ChangeHardwarePort(string portName)
        {
            try
            {
                return _daqPersistence.ChangeHardwarePort(portName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
                return false;
            }
        }

        public void SetFrequency(double frequency)
        {
            try
            {
                _daqPersistence.SetFrequency(frequency);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                Debug.WriteLine(ex.Message);
                Console.WriteLine(ex.Message);
            }
        }
    }
}

