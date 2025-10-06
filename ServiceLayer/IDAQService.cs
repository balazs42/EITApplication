using System.Numerics;
using Utility.Classes;
using Utility.Classes.Measurement;
using Utility.Classes.Discretizer;
using Utility.Classes.Discretizer.FiniteElementMesh;
using Utility.Classes.Discretizer.LatticeBoltzmannGrid;

namespace ServiceLayer
{
    public interface IDAQService
    {
        EITMeasurement GetEITMeasurement();
        Complex[][] ComputeFourierTransform(EITMeasurement measurement);
        Complex[][] ComputeDFT(EITMeasurement measurement);
        double[][] ComputeDCT(EITMeasurement measurement);
        Complex[][] ComputeFFT(EITMeasurement measurement);
        void SaveEITMeasurement(EITMeasurement measurement, string name);
        EITMeasurement LoadEITMeasurement(string name, DateTime savedAt);
        void DeleteEITMeasurement(string name, DateTime savedAt);
        void SaveFEMMesh(FEMMesh mesh, string name);
        void SaveLBMGrid(LBMGrid grid, string name);
        FEMMesh LoadFEMMesh(string filePath);
        LBMGrid LoadLBMGrid(string filePath);
        IEnumerable<DiscretizationInfo> GetDiscretizationInfos();
        void DeleteMesh(string filePath);
        MatlabExportResult ExportFemMeshForMatlab(FEMMesh mesh, string name, DrivePattern drivePattern, string modelType);
        bool ConnectHardware();
        bool DisconnectHardware();
        bool ChangeHardwarePort(string portName);
        void SetFrequency(double frequency);
    }
}
