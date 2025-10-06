using System.Numerics;
using DataAccessLayer;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace BusinessLayer
{
    public class DAQPersistence : IDAQPersistence
    {
        private readonly IDAQRepository _daqRepository;
        private readonly IMeshRepository _meshRepository;

        public DAQPersistence(IDAQRepository daqRepository, IMeshRepository meshRepository)
        {
            _daqRepository = daqRepository;
            _meshRepository = meshRepository;
        }

        public EITMeasurement GetEITMeasurement() => _daqRepository.GetEITMeasurement();
        public Complex[][] ComputeFourierTransform(EITMeasurement measurement) => _daqRepository.ComputeFourierTransform(measurement);        
        public Complex[][] ComputeDFT(EITMeasurement measurement) => _daqRepository.ComputeDFT(measurement);
        public double[][] ComputeDCT(EITMeasurement measurement) => _daqRepository.ComputeDCT(measurement);        
        public Complex[][] ComputeFFT(EITMeasurement measurement) => _daqRepository.ComputeFFT(measurement);
        public void SaveEITMeasurement(EITMeasurement measurement, string name) => _daqRepository.SaveEITMeasurement(measurement, name);
        public EITMeasurement LoadEITMeasurement(string name, DateTime savedAt) => _daqRepository.LoadEITMeasurement(name, savedAt);        
        public void DeleteEITMeasurement(string name, DateTime savedAt) => _daqRepository.DeleteEITMeasurement(name, savedAt);        
        public void SaveFEMMesh(FEMMesh mesh, string name) => _meshRepository.SaveFEMMesh(mesh, name);        
        public void SaveLBMGrid(LBMGrid grid, string name) => _meshRepository.SaveLBMGrid(grid, name);
        public MatlabExportResult ExportFemMeshForMatlab(FEMMesh mesh, string name, DrivePattern drivePattern, string modelType)
            => _meshRepository.ExportFemMeshForMatlab(mesh, name, drivePattern, modelType);
        public FEMMesh LoadFEMMesh(string filePath) => _meshRepository.LoadFEMMesh(filePath);
        public LBMGrid LoadLBMGrid(string filePath) => _meshRepository.LoadLBMGrid(filePath);
        public IEnumerable<DiscretizationInfo> GetDiscretizationInfos() => _meshRepository.GetDiscretizationInfos();
        public void DeleteMesh(string filePath) => _meshRepository.DeleteMesh(filePath);
        public bool ConnectHardware() => _daqRepository.Connect();
        public bool DisconnectHardware() => _daqRepository.Disconnect();
        public bool ChangeHardwarePort(string portName) => _daqRepository.ChangePort(portName);
        public void SetFrequency(double frequency) => _daqRepository.SetExcitationFrequency(frequency);
    }
}