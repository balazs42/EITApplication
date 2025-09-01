using System;
using System.Collections.Generic;
using System.Numerics;
using Utility.Classes.Measurement;
using Utility.Classes.Meshing;
using Utility.Classes.Meshing.FiniteElementMesh;
using Utility.Classes.Meshing.LatticeBoltzmannMesh;

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
        void SaveLBMMesh(LBMMesh mesh, string name);
        FEMMesh LoadFEMMesh(string filePath);
        LBMMesh LoadLBMMesh(string filePath);
        IEnumerable<MeshInfo> GetMeshes();
        bool ConnectHardware();
        bool DisconnectHardware();
        bool ChangeHardwarePort(string portName);
        void SetFrequency(double frequency);
    }
}
